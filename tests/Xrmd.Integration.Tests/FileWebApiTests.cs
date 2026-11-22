using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using SkiaSharp;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Files;
using Xrmd.Host;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The Web API of file and image columns (Learn, "Use file column data" and "Use image column data"): the property routes
/// (single-request and chunked PATCH, <c>GET …/$value</c> with <c>Range</c> and <c>size=full</c>, <c>DELETE</c>, the JSON
/// property) and the six file messages as unbound actions.
/// </summary>
[Collection("xrmd")]
public sealed class FileWebApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Doc = "new_doc";
    private const string Pic = "new_pic";
    private const string Plain = "new_plainpic";
    private const string Api = "/api/data/v9.2/";
    private const int ChunkSize = 4194304;
    private const string Db = "xrmverse_test";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private IFileStore Blobs => fx.App.Services.GetRequiredService<IFileStore>();

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A user-owned table with the file column <c>new_doc</c> and, when asked, two image columns: <c>new_pic</c> (full
    /// images kept) and <c>new_plainpic</c> (thumbnail only). Returns the logical name and the entity set name.</summary>
    private async Task<(string Table, string Set)> NewTableAsync(bool images = false, int? maxSizeInKB = null)
    {
        var name = "new_fw" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name, Attribute = new FileAttributeMetadata { LogicalName = Doc, SchemaName = Doc, MaxSizeInKB = maxSizeInKB },
        });
        if (images)
        {
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name, Attribute = new ImageAttributeMetadata { LogicalName = Pic, SchemaName = Pic, CanStoreFullImage = true },
            });
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name, Attribute = new ImageAttributeMetadata { LogicalName = Plain, SchemaName = Plain, CanStoreFullImage = false },
            });
        }
        return (name, Store.GetEntity(name)!.EntitySetName);
    }

    private async Task<(string Table, string Set, Guid Id)> NewRecordAsync(bool images = false, int? maxSizeInKB = null)
    {
        var (table, set) = await NewTableAsync(images, maxSizeInKB);
        return (table, set, await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" }));
    }

    private static byte[] Content(int length) => RandomNumberGenerator.GetBytes(length);

    private static byte[] Image(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Green);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null,
        Action<HttpRequestMessage>? configure = null, Guid? objectId = null)
    {
        var request = new HttpRequestMessage(method, path.StartsWith('/') ? path : Api + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        configure?.Invoke(request);
        return fx.Client.SendAsync(request);
    }

    private static ByteArrayContent Octets(byte[] bytes, int offset = 0, int? count = null)
    {
        var content = new ByteArrayContent(bytes, offset, count ?? bytes.Length - offset);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    /// <summary>A single-request upload (PATCH with the file as the body and its name in <c>x-ms-file-name</c>).</summary>
    private Task<HttpResponseMessage> PatchFileAsync(string set, Guid id, string column, byte[] bytes, string? fileName = "report.txt",
        Action<HttpRequestMessage>? configure = null, Guid? objectId = null) =>
        SendAsync(HttpMethod.Patch, $"{set}({id})/{column}", Octets(bytes), r =>
        {
            if (fileName is not null) r.Headers.Add("x-ms-file-name", fileName);
            configure?.Invoke(r);
        }, objectId);

    private static async Task ExpectStatusAsync(HttpResponseMessage response, HttpStatusCode status) =>
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static async Task ExpectErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? contains = null)
    {
        var json = await JsonAsync(response, status);
        json["error"]!["code"]!.GetValue<string>().Should().Be(code);
        if (contains is not null) json["error"]!["message"]!.GetValue<string>().Should().Contain(contains);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : throw new InvalidOperationException($"The response has no '{name}' header.");

    private Task<HttpResponseMessage> PostJsonAsync(string action, JsonObject body) =>
        SendAsync(HttpMethod.Post, action, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

    private static JsonObject Target(string table, Guid id) =>
        new() { ["@odata.type"] = $"Microsoft.Dynamics.CRM.{table}", [table + "id"] = id.ToString() };

    // ---- single request -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Single_request_upload_and_download_round_trip()
    {
        var (table, set, id) = await NewRecordAsync();
        var content = Content(ChunkSize + 12345);

        using (var upload = await PatchFileAsync(set, id, Doc, content, "report.txt"))
        {
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
            Header(upload, "OData-Version").Should().Be("4.0");
        }

        using (var download = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value"))
        {
            await ExpectStatusAsync(download, HttpStatusCode.OK);
            (await download.Content.ReadAsByteArrayAsync()).Should().Equal(content);
            Header(download, "x-ms-file-size").Should().Be(content.Length.ToString());
            Header(download, "x-ms-file-name").Should().Be("report.txt");
            Header(download, "mimetype").Should().Be("text/plain", "without a mimetype header the type follows the file name, as Learn's example shows");
            Header(download, "Access-Control-Expose-Headers").Should().Be("x-ms-file-size; x-ms-file-name; mimetype");
            Header(download, "X-Content-Type-Options").Should().Be("nosniff");
        }

        // The record holds the file id, which the property without $value returns.
        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Doc, Doc + "_name"));
        record[Doc + "_name"].Should().Be("report.txt");
        var property = await JsonAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}"));
        property["value"]!.GetValue<string>().Should().Be(((Guid)record[Doc]).ToString());
        property["@odata.context"]!.GetValue<string>().Should().EndWith($"$metadata#{set}({id})/{Doc}");

        // A mimetype header is kept as sent; the replaced file is gone.
        using (var again = await PatchFileAsync(set, id, Doc, [1, 2, 3], "data.bin", r => r.Headers.Add("mimetype", "application/x-custom")))
            await ExpectStatusAsync(again, HttpStatusCode.NoContent);
        using (var download = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value"))
        {
            (await download.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
            Header(download, "mimetype").Should().Be("application/x-custom");
        }

        // An unknown extension falls back to application/octet-stream; a missing name is refused.
        using (var unknown = await PatchFileAsync(set, id, Doc, [4], "blob.zzzunknown"))
            await ExpectStatusAsync(unknown, HttpStatusCode.NoContent);
        using (var download = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value"))
            Header(download, "mimetype").Should().Be("application/octet-stream");
        await ExpectErrorAsync(await PatchFileAsync(set, id, Doc, [1], fileName: null), HttpStatusCode.BadRequest, "0x80040203", "x-ms-file-name");
    }

    /// <summary>A body of <paramref name="length"/> zero bytes produced as it is sent (no buffer of its size).</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Single_request_over_128MB_is_413()
    {
        // The column takes 200 MB, so that the request limit is what refuses the file, not MaxSizeInKB.
        var (table, set, id) = await NewRecordAsync(maxSizeInKB: 200 * 1024);
        var stagedBefore = await StagedKeysAsync();
        const long over = 128L * 1024 * 1024 + 1;

        // Declared by Content-Length: refused before the body is read.
        var declared = new StreamContent(new ZeroStream(over));
        declared.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        declared.Headers.ContentLength = over;
        await ExpectErrorAsync(await SendAsync(HttpMethod.Patch, $"{set}({id})/{Doc}", declared, r => r.Headers.Add("x-ms-file-name", "big.bin")),
            HttpStatusCode.RequestEntityTooLarge, "0x80040203", "128 MB");

        // Sent without a length (chunked transfer encoding): refused once the body passes 128 MB.
        var streamed = new StreamContent(new ZeroStream(over));
        streamed.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Patch, $"{set}({id})/{Doc}", streamed, r =>
        {
            r.Headers.Add("x-ms-file-name", "big.bin");
            r.Headers.TransferEncodingChunked = true;
        }), HttpStatusCode.RequestEntityTooLarge, "0x80040203", "128 MB");

        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc))).Contains(Doc).Should().BeFalse("nothing was stored");
        await AssertNoStagingAsync(id, stagedBefore);
    }

    [Fact]
    public async Task Single_request_crossing_MaxSizeInKB_fails_and_leaves_no_staging()
    {
        var (table, set, id) = await NewRecordAsync(maxSizeInKB: 1024);
        var stagedBefore = await StagedKeysAsync();

        // 1 MB + 1 byte in one request: the UploadBlock that crosses the limit refuses it.
        await ExpectErrorAsync(await PatchFileAsync(set, id, Doc, Content(1024 * 1024 + 1)), HttpStatusCode.BadRequest, "0x80044a02");

        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc))).Contains(Doc).Should().BeFalse("nothing was stored");
        await AssertNoStagingAsync(id, stagedBefore);
    }

    private async Task<HashSet<string>> StagedKeysAsync()
    {
        await fx.DrainFileDeletionsAsync();
        var staged = new HashSet<string>();
        await foreach (var key in Blobs.ListAsync($"{Db}/_upload/", default)) staged.Add(key);
        return staged;
    }

    /// <summary>No upload session of the record and no staged block that was not there before the request.</summary>
    private async Task AssertNoStagingAsync(Guid recordId, HashSet<string> stagedBefore)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM meta.filesession WHERE record_id = @id";
        cmd.Parameters.AddWithValue("@id", recordId);
        (Convert.ToInt32(await cmd.ExecuteScalarAsync())).Should().Be(0, "the failed upload's session was ended");
        (await StagedKeysAsync()).Except(stagedBefore).Should().BeEmpty("the failed upload's staged blocks were deleted");
    }

    [Fact]
    public async Task Non_ascii_name_via_query()
    {
        var (table, set, id) = await NewRecordAsync();
        const string name = "résumé 東京.txt";

        using (var upload = await SendAsync(HttpMethod.Patch, $"{set}({id})/{Doc}?x-ms-file-name={Uri.EscapeDataString(name)}", Octets([1, 2])))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc + "_name")))[Doc + "_name"].Should().Be(name);

        // The header wins over the query (Learn), when both are sent.
        using (var both = await SendAsync(HttpMethod.Patch, $"{set}({id})/{Doc}?x-ms-file-name=query.txt", Octets([3]),
                   r => r.Headers.Add("x-ms-file-name", "header.txt")))
            await ExpectStatusAsync(both, HttpStatusCode.NoContent);
        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc + "_name")))[Doc + "_name"].Should().Be("header.txt");

        // The download names a non-ASCII file percent-encoded (a header value is ASCII).
        using (var upload = await SendAsync(HttpMethod.Patch, $"{set}({id})/{Doc}?x-ms-file-name={Uri.EscapeDataString(name)}", Octets([1, 2])))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        using var download = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value");
        Uri.UnescapeDataString(Header(download, "x-ms-file-name")).Should().Be(name);
    }

    [Theory]
    [InlineData("100%25.txt")]
    [InlineData("a%20b.txt")]
    [InlineData("plain.txt")]
    public async Task A_name_with_percent_signs_round_trips_verbatim(string name)
    {
        var (table, set, id) = await NewRecordAsync();
        using (var upload = await PatchFileAsync(set, id, Doc, [1], name))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc + "_name")))[Doc + "_name"].Should().Be(name, "the header is taken as sent");

        // A name with '%' is percent-encoded in the response header like a non-ASCII one, so that decoding it is lossless.
        using var download = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value");
        var header = Header(download, "x-ms-file-name");
        Uri.UnescapeDataString(header).Should().Be(name);
        if (name.Contains('%')) header.Should().Be(Uri.EscapeDataString(name));
        else header.Should().Be(name);
    }

    // ---- chunked ------------------------------------------------------------------------------------------------------

    private async Task<string> StartChunkedAsync(string set, Guid id, string column, string fileName)
    {
        using var start = await SendAsync(HttpMethod.Patch, $"{set}({id})/{column}?x-ms-file-name={Uri.EscapeDataString(fileName)}",
            configure: r => r.Headers.Add("x-ms-transfer-mode", "chunked"));
        await ExpectStatusAsync(start, HttpStatusCode.OK);
        return start.Headers.Location!.ToString();
    }

    private Task<HttpResponseMessage> ChunkAsync(string location, byte[] content, long from, long to, string fileName = "big.pdf") =>
        SendAsync(HttpMethod.Patch, new Uri(location).PathAndQuery, Octets(content, (int)from, (int)(to - from + 1)), r =>
        {
            r.Headers.Add("x-ms-file-name", fileName);
            r.Content!.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
        });

    [Fact]
    public async Task Chunked_upload_location_sessiontoken_and_206_then_204()
    {
        var (table, set, id) = await NewRecordAsync();
        var content = Content(2 * ChunkSize + 777);

        string location;
        using (var start = await SendAsync(HttpMethod.Patch, $"{set}({id})/{Doc}?x-ms-file-name=big.pdf",
                   configure: r => r.Headers.Add("x-ms-transfer-mode", "chunked")))
        {
            await ExpectStatusAsync(start, HttpStatusCode.OK);
            location = start.Headers.Location!.ToString();
            location.Should().StartWith($"{fx.BaseUrl}api/data/v9.2/{set}({id})/{Doc}?sessiontoken=");
            Header(start, "x-ms-chunk-size").Should().Be("4194304");
            Header(start, "Accept-Ranges").Should().Be("bytes");
            Header(start, "Access-Control-Expose-Headers").Should().Be("x-ms-chunk-size");
        }

        using (var first = await ChunkAsync(location, content, 0, ChunkSize - 1))
            await ExpectStatusAsync(first, HttpStatusCode.PartialContent);
        using (var second = await ChunkAsync(location, content, ChunkSize, 2 * ChunkSize - 1))
            await ExpectStatusAsync(second, HttpStatusCode.PartialContent);
        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc))).Contains(Doc).Should().BeFalse("the file is committed with its last byte");
        using (var last = await ChunkAsync(location, content, 2 * ChunkSize, content.Length - 1))
            await ExpectStatusAsync(last, HttpStatusCode.NoContent);

        using var download = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value");
        SHA256.HashData(await download.Content.ReadAsByteArrayAsync()).Should().Equal(SHA256.HashData(content));
        Header(download, "x-ms-file-name").Should().Be("big.pdf");
        Header(download, "mimetype").Should().Be("application/pdf");

        // The session ended with its commit.
        await ExpectErrorAsync(await ChunkAsync(location, content, 0, 9), HttpStatusCode.BadRequest, "0x80040203");
    }

    [Fact]
    public async Task Chunked_gap_or_overlap_is_refused()
    {
        var (table, set, id) = await NewRecordAsync();
        var content = Content(3000);
        var location = await StartChunkedAsync(set, id, Doc, "a.bin");

        using (var first = await ChunkAsync(location, content, 0, 999))
            await ExpectStatusAsync(first, HttpStatusCode.PartialContent);

        await ExpectErrorAsync(await ChunkAsync(location, content, 1001, 1999), HttpStatusCode.BadRequest, "0x80040203", "1000");
        await ExpectErrorAsync(await ChunkAsync(location, content, 500, 1499), HttpStatusCode.BadRequest, "0x80040203", "1000");
        await ExpectErrorAsync(await ChunkAsync(location, content, 0, 999), HttpStatusCode.BadRequest, "0x80040203", "1000");

        // A Content-Range whose total changed, or a body of another length, is refused too.
        using (var otherTotal = await SendAsync(HttpMethod.Patch, new Uri(location).PathAndQuery, Octets(content, 1000, 1000), r =>
               r.Content!.Headers.ContentRange = new ContentRangeHeaderValue(1000, 1999, 5000)))
            await ExpectErrorAsync(otherTotal, HttpStatusCode.BadRequest, "0x80040203");
        using (var shortBody = await SendAsync(HttpMethod.Patch, new Uri(location).PathAndQuery, Octets(content, 1000, 10), r =>
               r.Content!.Headers.ContentRange = new ContentRangeHeaderValue(1000, 1999, 3000)))
            await ExpectErrorAsync(shortBody, HttpStatusCode.BadRequest, "0x80040203");
        using (var missing = await SendAsync(HttpMethod.Patch, new Uri(location).PathAndQuery, Octets(content, 1000, 1000)))
            await ExpectErrorAsync(missing, HttpStatusCode.BadRequest, "0x80040203", "Content-Range");

        // The session survives a refused chunk: the right one continues it.
        using (var second = await ChunkAsync(location, content, 1000, 1999))
            await ExpectStatusAsync(second, HttpStatusCode.PartialContent);
        using (var last = await ChunkAsync(location, content, 2000, 2999))
            await ExpectStatusAsync(last, HttpStatusCode.NoContent);
        using var download = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value");
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(content);

        // A token for another record or column is refused, saying nothing of the session.
        var (_, otherSet, otherId) = await NewRecordAsync();
        var foreign = await StartChunkedAsync(set, id, Doc, "b.bin");
        var query = new Uri(foreign).Query;
        using (var wrongRecord = await SendAsync(HttpMethod.Patch, $"{otherSet}({otherId})/{Doc}{query}", Octets(content, 0, 10), r =>
               r.Content!.Headers.ContentRange = new ContentRangeHeaderValue(0, 9, 10)))
            await ExpectErrorAsync(wrongRecord, HttpStatusCode.BadRequest, "0x80040203", "not valid");
    }

    // ---- download -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Range_download_206_headers()
    {
        var (_, set, id) = await NewRecordAsync();
        var content = Content(5000);
        using (var upload = await PatchFileAsync(set, id, Doc, content, "r.pdf"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);

        using (var partial = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value", configure: r => r.Headers.Range = new RangeHeaderValue(0, 99)))
        {
            await ExpectStatusAsync(partial, HttpStatusCode.PartialContent);
            (await partial.Content.ReadAsByteArrayAsync()).Should().Equal(content[..100]);
            Header(partial, "x-ms-file-size").Should().Be("5000");
            Header(partial, "x-ms-file-name").Should().Be("r.pdf");
            Header(partial, "x-ms-chunk-size").Should().Be("4194304");
            Header(partial, "mimetype").Should().Be("application/pdf");
            Header(partial, "Access-Control-Expose-Headers").Should().Be("x-ms-file-size; x-ms-file-name; x-ms-chunk-size; mimetype");
            partial.Content.Headers.ContentRange!.ToString().Should().Be("bytes 0-99/5000");
            Header(partial, "X-Content-Type-Options").Should().Be("nosniff");
        }

        using (var rest = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value", configure: r => r.Headers.Range = new RangeHeaderValue(4000, null)))
        {
            await ExpectStatusAsync(rest, HttpStatusCode.PartialContent);
            (await rest.Content.ReadAsByteArrayAsync()).Should().Equal(content[4000..]);
        }

        // A range past the end, a suffix range or several ranges are refused explicitly.
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value", configure: r => r.Headers.Range = new RangeHeaderValue(6000, 7000)),
            HttpStatusCode.BadRequest, "0x80040203");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value", configure: r => r.Headers.Range = new RangeHeaderValue(null, 100)),
            HttpStatusCode.BadRequest, "0x80040203");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value", configure: r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-1,5-6")),
            HttpStatusCode.BadRequest, "0x80040203");
    }

    [Fact]
    public async Task Size_full_on_image()
    {
        var (table, set, id) = await NewRecordAsync(images: true);
        var full = Image(300, 200);

        // A single-request upload sets an image column (Learn: images upload with the file APIs).
        using (var upload = await PatchFileAsync(set, id, Pic, full, "photo.png"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        using (var upload = await PatchFileAsync(set, id, Plain, full, "photo.png"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Pic, Plain));
        var thumbnail = record.GetAttributeValue<byte[]>(Pic);

        // $value is the thumbnail by default, ?size=full the full image.
        using (var small = await SendAsync(HttpMethod.Get, $"{set}({id})/{Pic}/$value"))
        {
            await ExpectStatusAsync(small, HttpStatusCode.OK);
            (await small.Content.ReadAsByteArrayAsync()).Should().Equal(thumbnail);
            Header(small, "mimetype").Should().Be("image/png");
            Header(small, "X-Content-Type-Options").Should().Be("nosniff");
        }
        using (var large = await SendAsync(HttpMethod.Get, $"{set}({id})/{Pic}/$value?size=full"))
        {
            await ExpectStatusAsync(large, HttpStatusCode.OK);
            (await large.Content.ReadAsByteArrayAsync()).Should().Equal(full);
            Header(large, "x-ms-file-name").Should().Be("photo.png");
            Header(large, "mimetype").Should().Be("image/png");
        }

        // A column that keeps no full image answers 204 (Learn); its thumbnail is still there.
        using (var none = await SendAsync(HttpMethod.Get, $"{set}({id})/{Plain}/$value?size=full"))
            await ExpectStatusAsync(none, HttpStatusCode.NoContent);
        using (var small = await SendAsync(HttpMethod.Get, $"{set}({id})/{Plain}/$value"))
            (await small.Content.ReadAsByteArrayAsync()).Should().Equal(record.GetAttributeValue<byte[]>(Plain));

        // The property without $value is the thumbnail, base64 encoded.
        var property = await JsonAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{Pic}"));
        Convert.FromBase64String(property["value"]!.GetValue<string>()).Should().Equal(thumbnail);

        // size has one value; on a file column it is ignored (a file has one size).
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{Pic}/$value?size=thumbnail"), HttpStatusCode.BadRequest, "0x80040203", "size");
        using (var upload = await PatchFileAsync(set, id, Doc, [9, 8, 7], "f.bin"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        using (var file = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value?size=full"))
            (await file.Content.ReadAsByteArrayAsync()).Should().Equal(9, 8, 7);

        // A missing record stays a 404, not the 204 of a missing full image.
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({Guid.NewGuid()})/{Plain}/$value?size=full"), HttpStatusCode.NotFound, "0x80040217");
    }

    // ---- delete and errors --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_property_204()
    {
        var (table, set, id) = await NewRecordAsync(images: true);
        using (var upload = await PatchFileAsync(set, id, Doc, [1, 2, 3]))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        using (var upload = await PatchFileAsync(set, id, Pic, Image(50, 50), "p.png"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);

        using (var delete = await SendAsync(HttpMethod.Delete, $"{set}({id})/{Doc}"))
            await ExpectStatusAsync(delete, HttpStatusCode.NoContent);
        using (var delete = await SendAsync(HttpMethod.Delete, $"{set}({id})/{Pic}"))
            await ExpectStatusAsync(delete, HttpStatusCode.NoContent);

        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Doc, Doc + "_name", Pic));
        record.Contains(Doc).Should().BeFalse();
        record.Contains(Doc + "_name").Should().BeFalse();
        record.Contains(Pic).Should().BeFalse();
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value"), HttpStatusCode.NotFound, "0x80040217");
        using (var empty = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}"))
            await ExpectStatusAsync(empty, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Unknown_column_or_non_file_column_is_400_with_code()
    {
        var (_, set, id) = await NewRecordAsync();

        foreach (var column in new[] { "new_bogus", "new_name" })
        {
            await ExpectErrorAsync(await PatchFileAsync(set, id, column, [1]), HttpStatusCode.BadRequest, "0x80040203", column);
            await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{column}/$value"), HttpStatusCode.BadRequest, "0x80040203", column);
            await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{column}"), HttpStatusCode.BadRequest, "0x80040203", column);
            await ExpectErrorAsync(await SendAsync(HttpMethod.Delete, $"{set}({id})/{column}"), HttpStatusCode.BadRequest, "0x80040203", column);
        }
        // Without a key, or on an unknown entity set.
        await ExpectErrorAsync(await SendAsync(HttpMethod.Patch, $"{set}/{Doc}", Octets([1]), r => r.Headers.Add("x-ms-file-name", "a.bin")),
            HttpStatusCode.BadRequest, "0x80040203");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"nosuchsets({id})/{Doc}/$value"), HttpStatusCode.NotFound, "0x80060888");
    }

    [Fact]
    public async Task Upload_needs_write_and_download_needs_read()
    {
        var (table, set, id) = await NewRecordAsync();
        using (var upload = await PatchFileAsync(set, id, Doc, [1, 2, 3]))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, team: false, ($"prvRead{Store.GetEntity(table)!.SchemaName}", DepthMask.Global));

        using (var read = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value", objectId: objectId))
            (await read.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
        await ExpectErrorAsync(await PatchFileAsync(set, id, Doc, [4], objectId: objectId), HttpStatusCode.Forbidden, "0x80040220");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Patch, $"{set}({id})/{Doc}?x-ms-file-name=a.bin", configure: r => r.Headers.Add("x-ms-transfer-mode", "chunked"),
            objectId: objectId), HttpStatusCode.Forbidden, "0x80040220");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Delete, $"{set}({id})/{Doc}", objectId: objectId), HttpStatusCode.Forbidden, "0x80040220");

        var stranger = Guid.NewGuid();
        await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), stranger);
        using (var denied = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value", objectId: stranger))
            await ExpectStatusAsync(denied, HttpStatusCode.Forbidden);
    }

    // ---- the file messages as actions ---------------------------------------------------------------------------------

    [Fact]
    public async Task Actions_InitializeFileBlocksUpload_UploadBlock_Commit_round_trip()
    {
        var (table, set, id) = await NewRecordAsync();
        var content = Content(ChunkSize + 10);
        var blockIds = new[] { "YmxvY2stMDAw", "YmxvY2stMDAx" };

        var init = await JsonAsync(await PostJsonAsync("InitializeFileBlocksUpload", new JsonObject
        {
            ["Target"] = Target(table, id), ["FileName"] = "upload.pdf", ["FileAttributeName"] = Doc,
        }));
        init["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.InitializeFileBlocksUploadResponse");
        var token = init["FileContinuationToken"]!.GetValue<string>();

        // Blocks out of order; the BlockList order defines the file.
        using (var second = await PostJsonAsync("UploadBlock", new JsonObject
               {
                   ["BlockId"] = blockIds[1], ["BlockData"] = Convert.ToBase64String(content[ChunkSize..]), ["FileContinuationToken"] = token,
               }))
            await ExpectStatusAsync(second, HttpStatusCode.NoContent);
        using (var first = await PostJsonAsync("UploadBlock", new JsonObject
               {
                   ["BlockId"] = blockIds[0], ["BlockData"] = Convert.ToBase64String(content[..ChunkSize]), ["FileContinuationToken"] = token,
               }))
            await ExpectStatusAsync(first, HttpStatusCode.NoContent);

        var commit = await JsonAsync(await PostJsonAsync("CommitFileBlocksUpload", new JsonObject
        {
            ["FileName"] = "upload.pdf", ["MimeType"] = "application/pdf",
            ["BlockList"] = new JsonArray(blockIds.Select(b => (JsonNode)b!).ToArray()), ["FileContinuationToken"] = token,
        }));
        commit["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.CommitFileBlocksUploadResponse");
        commit["FileSizeInBytes"]!.GetValue<long>().Should().Be(content.Length);
        var fileId = Guid.Parse(commit["FileId"]!.GetValue<string>());
        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc)))[Doc].Should().Be(fileId);

        using (var download = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value"))
            SHA256.HashData(await download.Content.ReadAsByteArrayAsync()).Should().Equal(SHA256.HashData(content));

        // Target is an entity reference: a missing @odata.type or key is refused, naming the parameter.
        await ExpectErrorAsync(await PostJsonAsync("InitializeFileBlocksUpload", new JsonObject
        {
            ["Target"] = new JsonObject { [table + "id"] = id.ToString() }, ["FileName"] = "x", ["FileAttributeName"] = Doc,
        }), HttpStatusCode.BadRequest, "0x80040203", "Target");

        // DeleteFile with the FileId.
        using (var delete = await PostJsonAsync("DeleteFile", new JsonObject { ["FileId"] = fileId.ToString() }))
            await ExpectStatusAsync(delete, HttpStatusCode.NoContent);
        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc))).Contains(Doc).Should().BeFalse();
    }

    [Fact]
    public async Task DownloadBlock_action_returns_base64_Data()
    {
        var (table, set, id) = await NewRecordAsync();
        var content = Content(3000);
        using (var upload = await PatchFileAsync(set, id, Doc, content, "d.pdf"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);

        var init = await JsonAsync(await PostJsonAsync("InitializeFileBlocksDownload", new JsonObject
        {
            ["Target"] = Target(table, id), ["FileAttributeName"] = Doc,
        }));
        init["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.InitializeFileBlocksDownloadResponse");
        init["FileSizeInBytes"]!.GetValue<long>().Should().Be(3000);
        init["FileName"]!.GetValue<string>().Should().Be("d.pdf");
        init["IsChunkingSupported"]!.GetValue<bool>().Should().BeTrue();
        var token = init["FileContinuationToken"]!.GetValue<string>();

        using var assembled = new MemoryStream();
        for (var offset = 0; offset < 3000; offset += 1024)
        {
            var block = await JsonAsync(await PostJsonAsync("DownloadBlock", new JsonObject
            {
                ["Offset"] = offset, ["BlockLength"] = 1024, ["FileContinuationToken"] = token,
            }));
            block["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.DownloadBlockResponse");
            assembled.Write(Convert.FromBase64String(block["Data"]!.GetValue<string>()));
        }
        assembled.ToArray().Should().Equal(content);

        // Offset is an Edm.Int64: a string is refused, naming it.
        await ExpectErrorAsync(await PostJsonAsync("DownloadBlock", new JsonObject
        {
            ["Offset"] = "0", ["BlockLength"] = 1024, ["FileContinuationToken"] = token,
        }), HttpStatusCode.BadRequest, "0x80040203", "Offset");
    }

    // ---- review round 1: one write path (the SDK messages and Update), plug-ins, tokens, ranges ---------------------------

    private async Task<List<string>> FileBlobsAsync()
    {
        await fx.DrainFileDeletionsAsync();
        var keys = new List<string>();
        await foreach (var key in Blobs.ListAsync($"{Db}/", default)) if (!key.Contains("/_upload/")) keys.Add(key);
        return keys;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<Guid> StepAsync(string type, string message, string? entity, int stage, string config)
    {
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var plugin = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins." + type);
        return await PluginTestHelper.RegisterStepAsync(Admin, plugin, message, entity, stage: stage, config: config);
    }

    /// <summary>The <c>pb_name</c> and <c>pb_data</c> rows the ContextRecorderPlugin wrote.</summary>
    private async Task<List<(string Name, string Data)>> RecordedAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{TestEntities.PipeLog}");
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string)>();
        while (await rd.ReadAsync()) rows.Add((rd.GetString(0), rd.GetString(1)));
        return rows;
    }

    [Fact]
    public async Task Property_patch_with_a_failing_update_plugin_keeps_the_old_file()
    {
        var (table, set, id) = await NewRecordAsync();
        using (var upload = await PatchFileAsync(set, id, Doc, [1, 2, 3], "old.txt"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        var old = (Guid)(await Admin.RetrieveAsync(table, id, new ColumnSet(Doc)))[Doc];
        var blobsBefore = await FileBlobsAsync();
        await StepAsync("ThrowPlugin", "Update", table, 40, "invalid:no new file");

        await ExpectErrorAsync(await PatchFileAsync(set, id, Doc, Content(5000), "new.txt"), HttpStatusCode.BadRequest, "0x80040265", "no new file");
        var location = await StartChunkedAsync(set, id, Doc, "new.pdf");
        await ExpectErrorAsync(await ChunkAsync(location, Content(100), 0, 99), HttpStatusCode.BadRequest, "0x80040265", "no new file");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Delete, $"{set}({id})/{Doc}"), HttpStatusCode.BadRequest, "0x80040265", "no new file");

        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Doc, Doc + "_name"));
        record[Doc].Should().Be(old);
        record[Doc + "_name"].Should().Be("old.txt");
        using (var download = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value"))
            (await download.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
        (await FileBlobsAsync()).Should().BeEquivalentTo(blobsBefore, "the blobs of the rolled-back commits are deleted");
    }

    [Fact]
    public async Task Property_patch_whose_plugin_swallows_a_failed_nested_call_fails_as_the_sdk_path()
    {
        await TestEntities.EnsurePipeAsync(Store);
        var (table, set, id) = await NewRecordAsync();
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = table, Attribute = new StringAttributeMetadata { LogicalName = "pb_data", SchemaName = "pb_data", MaxLength = 4000 },
        });
        var existing = await Admin.CreateAsync(new Entity(TestEntities.PipeLog) { ["pb_name"] = "existing" });
        // The plug-in tries to create a record that exists, catches the fault and returns normally.
        await StepAsync("TryCreatePlugin", "Update", table, 20, $"{TestEntities.PipeLog}|{existing}");

        var response = await PatchFileAsync(set, id, Doc, [1, 2, 3], "a.txt");

        await ExpectErrorAsync(response, HttpStatusCode.InternalServerError, "0x80040224", "no active transaction");
        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc))).Contains(Doc).Should().BeFalse();
    }

    [Fact]
    public async Task Property_routes_run_the_update_as_the_sdk_path()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        var (table, set, id) = await NewRecordAsync();
        await StepAsync("ContextRecorderPlugin", "Update", table, 40, TestEntities.PipeLog);

        // The upload's Update runs one level below CommitFileBlocksUpload, as on the SDK path.
        using (var upload = await PatchFileAsync(set, id, Doc, [1, 2, 3], "a.txt"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        var rows = await RecordedAsync();
        rows.Should().ContainSingle();
        rows[0].Name.Should().Be("Update|40|0|2");
        rows[0].Data.Split('\n').Should().Contain("parent=CommitFileBlocksUpload");

        // The chunked upload's too.
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        var location = await StartChunkedAsync(set, id, Doc, "b.bin");
        using (var last = await ChunkAsync(location, [4, 5], 0, 1))
            await ExpectStatusAsync(last, HttpStatusCode.NoContent);
        rows = await RecordedAsync();
        rows.Should().ContainSingle();
        rows[0].Name.Should().Be("Update|40|0|2");
        rows[0].Data.Split('\n').Should().Contain("parent=CommitFileBlocksUpload");

        // DELETE is an Update of the column to null at the first level.
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        using (var delete = await SendAsync(HttpMethod.Delete, $"{set}({id})/{Doc}"))
            await ExpectStatusAsync(delete, HttpStatusCode.NoContent);
        rows = await RecordedAsync();
        rows.Should().ContainSingle();
        rows[0].Name.Should().Be("Update|40|0|1");
        rows[0].Data.Split('\n').Should().Contain(l => l.StartsWith("target=") && l.Contains(Doc));
        rows[0].Data.Split('\n').Should().Contain("parent=");
    }

    [Fact]
    public async Task Value_download_runs_the_download_messages_through_the_pipeline()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        var (_, set, id) = await NewRecordAsync();
        var content = Content(ChunkSize + 10);
        using (var upload = await PatchFileAsync(set, id, Doc, content, "two-blocks.bin"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        await StepAsync("ContextRecorderPlugin", "InitializeFileBlocksDownload", null, 40, TestEntities.PipeLog);
        await StepAsync("ContextRecorderPlugin", "DownloadBlock", null, 40, TestEntities.PipeLog);

        // The whole file: InitializeFileBlocksDownload, then one DownloadBlock per 4 MB.
        using (var download = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value"))
        {
            await ExpectStatusAsync(download, HttpStatusCode.OK);
            (await download.Content.ReadAsByteArrayAsync()).Should().Equal(content);
        }
        (await RecordedAsync()).Select(r => r.Name).Should().BeEquivalentTo(
            "InitializeFileBlocksDownload|40|0|1", "DownloadBlock|40|0|1", "DownloadBlock|40|0|1");
        // The download session the request opened is ended once the file is served.
        (await ScalarAsync($"SELECT COUNT(*) FROM meta.filesession WHERE record_id = '{id}'")).Should().Be(0);

        // A range is the offset and length of the blocks: one block here.
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        using (var partial = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value", configure: r => r.Headers.Range = new RangeHeaderValue(10, 19)))
        {
            await ExpectStatusAsync(partial, HttpStatusCode.PartialContent);
            (await partial.Content.ReadAsByteArrayAsync()).Should().Equal(content[10..20]);
        }
        (await RecordedAsync()).Select(r => r.Name).Should().BeEquivalentTo("InitializeFileBlocksDownload|40|0|1", "DownloadBlock|40|0|1");

        // A step can deny the download, on either message.
        await PluginTestHelper.ClearAsync(fx);
        await StepAsync("ThrowPlugin", "InitializeFileBlocksDownload", null, 20, "invalid:no download");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value"), HttpStatusCode.BadRequest, "0x80040265", "no download");
        await PluginTestHelper.ClearAsync(fx);
        await StepAsync("ThrowPlugin", "DownloadBlock", null, 20, "invalid:no block");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value"), HttpStatusCode.BadRequest, "0x80040265", "no block");
    }

    [Fact]
    public async Task Image_downloads_run_retrieve_or_the_download_messages_through_the_pipeline()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        var (table, set, id) = await NewRecordAsync(images: true);
        var full = Image(300, 200);
        using (var upload = await PatchFileAsync(set, id, Pic, full, "photo.png"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        var url = (await Admin.RetrieveAsync(table, id, new ColumnSet(Pic + "_url"))).GetAttributeValue<string>(Pic + "_url");
        await StepAsync("ContextRecorderPlugin", "Retrieve", table, 40, TestEntities.PipeLog);
        await StepAsync("ContextRecorderPlugin", "InitializeFileBlocksDownload", null, 40, TestEntities.PipeLog);
        await StepAsync("ContextRecorderPlugin", "DownloadBlock", null, 40, TestEntities.PipeLog);

        // The thumbnail is a Retrieve of the column, on $value and on download.aspx.
        using (var small = await SendAsync(HttpMethod.Get, $"{set}({id})/{Pic}/$value"))
            await ExpectStatusAsync(small, HttpStatusCode.OK);
        using (var small = await SendAsync(HttpMethod.Get, url))
            await ExpectStatusAsync(small, HttpStatusCode.OK);
        (await RecordedAsync()).Select(r => r.Name).Should().BeEquivalentTo("Retrieve|40|0|1", "Retrieve|40|0|1");

        // The full image is InitializeFileBlocksDownload and DownloadBlock, on $value?size=full and on download.aspx?Full=true.
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        using (var large = await SendAsync(HttpMethod.Get, $"{set}({id})/{Pic}/$value?size=full"))
            (await large.Content.ReadAsByteArrayAsync()).Should().Equal(full);
        using (var large = await SendAsync(HttpMethod.Get, url + "&Full=true"))
            (await large.Content.ReadAsByteArrayAsync()).Should().Equal(full);
        (await RecordedAsync()).Select(r => r.Name).Should().BeEquivalentTo(
            "InitializeFileBlocksDownload|40|0|1", "DownloadBlock|40|0|1", "InitializeFileBlocksDownload|40|0|1", "DownloadBlock|40|0|1");

        // A step denies each of them.
        await PluginTestHelper.ClearAsync(fx);
        await StepAsync("ThrowPlugin", "Retrieve", table, 20, "invalid:no thumbnail");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{Pic}/$value"), HttpStatusCode.BadRequest, "0x80040265", "no thumbnail");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, url), HttpStatusCode.BadRequest, "0x80040265", "no thumbnail");
        await PluginTestHelper.ClearAsync(fx);
        await StepAsync("ThrowPlugin", "DownloadBlock", null, 20, "invalid:no full image");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, url + "&Full=true"), HttpStatusCode.BadRequest, "0x80040265", "no full image");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/{Pic}/$value?size=full"), HttpStatusCode.BadRequest, "0x80040265",
            "no full image");
    }

    [Fact]
    public async Task Another_users_sessiontoken_is_refused_as_an_unknown_one()
    {
        var (table, set, id) = await NewRecordAsync();
        var location = new Uri(await StartChunkedAsync(set, id, Doc, "a.bin")).PathAndQuery;
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        var schema = Store.GetEntity(table)!.SchemaName;
        await GrantAsync(fx, user, team: false, ($"prvRead{schema}", DepthMask.Global), ($"prvWrite{schema}", DepthMask.Global));

        async Task<string> RefusalAsync(string path)
        {
            using var response = await SendAsync(HttpMethod.Patch, path, Octets([1, 2]), r =>
                r.Content!.Headers.ContentRange = new ContentRangeHeaderValue(0, 1, 2), objectId);
            var json = await JsonAsync(response, HttpStatusCode.BadRequest);
            json["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203");
            return json["error"]!["message"]!.GetValue<string>();
        }

        var foreign = await RefusalAsync(location);
        var unknown = await RefusalAsync($"{set}({id})/{Doc}?sessiontoken=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        foreign.Should().Be(FileColumnService.InvalidTokenMessage).And.Be(unknown);

        // The owner's upload is untouched.
        using var own = await ChunkAsync("http://localhost" + location, [7, 8], 0, 1);
        await ExpectStatusAsync(own, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Range_on_an_empty_file_is_the_whole_empty_file()
    {
        var (_, set, id) = await NewRecordAsync();
        using (var upload = await PatchFileAsync(set, id, Doc, [], "empty.txt"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);

        using var response = await SendAsync(HttpMethod.Get, $"{set}({id})/{Doc}/$value", configure: r => r.Headers.Range = new RangeHeaderValue(0, 99));
        await ExpectStatusAsync(response, HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        response.Content.Headers.ContentRange.Should().BeNull();
        Header(response, "x-ms-file-size").Should().Be("0");
    }

    [Fact]
    public async Task Navigation_properties_and_dollar_segments_are_refused_naming_them()
    {
        var (_, set, id) = await NewRecordAsync();

        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/ownerid"), HttpStatusCode.BadRequest, "0x80040203",
            "navigation property 'ownerid'");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Delete, $"{set}({id})/ownerid"), HttpStatusCode.BadRequest, "0x80040203",
            "navigation property 'ownerid'");
        await ExpectErrorAsync(await SendAsync(HttpMethod.Get, $"{set}/$count"), HttpStatusCode.BadRequest, "0x80040203", "'$count'");
    }

    [Fact]
    public async Task Int64_parameters_may_be_strings_with_IEEE754Compatible()
    {
        var (table, set, id) = await NewRecordAsync();
        using (var upload = await PatchFileAsync(set, id, Doc, [1, 2, 3, 4], "d.bin"))
            await ExpectStatusAsync(upload, HttpStatusCode.NoContent);
        var token = (await JsonAsync(await PostJsonAsync("InitializeFileBlocksDownload", new JsonObject
        {
            ["Target"] = Target(table, id), ["FileAttributeName"] = Doc,
        })))["FileContinuationToken"]!.GetValue<string>();

        var body = new JsonObject { ["Offset"] = "1", ["BlockLength"] = "2", ["FileContinuationToken"] = token };
        var content = new StringContent(body.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; IEEE754Compatible=true");
        var block = await JsonAsync(await SendAsync(HttpMethod.Post, "DownloadBlock", content));
        Convert.FromBase64String(block["Data"]!.GetValue<string>()).Should().Equal(2, 3);
    }

    private sealed class ListLogger : ILogger<RequestLoggingMiddleware>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    [Fact]
    public async Task Request_log_redacts_the_sessiontoken()
    {
        RequestLoggingMiddleware.RedactQuery(new QueryString("?sessiontoken=abc&x-ms-file-name=a.txt"))
            .Should().Be("?sessiontoken=***&x-ms-file-name=a.txt");
        RequestLoggingMiddleware.RedactQuery(new QueryString("?a=1&SessionToken=abc")).Should().Be("?a=1&SessionToken=***");
        RequestLoggingMiddleware.RedactQuery(new QueryString("?%73essiontoken=abc")).Should().Be("?%73essiontoken=***");
        RequestLoggingMiddleware.RedactQuery(QueryString.Empty).Should().Be("");

        var logger = new ListLogger();
        var middleware = new RequestLoggingMiddleware(_ => Task.CompletedTask, logger);
        var context = new DefaultHttpContext();
        context.Request.Method = "PATCH";
        context.Request.Path = "/api/data/v9.2/accounts(1)/new_doc";
        context.Request.QueryString = new QueryString("?SESSIONTOKEN=secret-token-value");
        await middleware.InvokeAsync(context);

        logger.Lines.Should().ContainSingle().Which.Should().Contain("SESSIONTOKEN=***").And.NotContain("secret-token-value");
    }
}
