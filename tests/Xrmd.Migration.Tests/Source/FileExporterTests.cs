using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;
using Xrmd.Migration.Source;

namespace Xrmd.Migration.Tests.Source;

public sealed class FileExporterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly SourceTable Account = new("account", "accounts", "accountid", false, false, false,
    [
        new("accountid", "Uniqueidentifier", null, [], false, true),
        new("xv_contract", "File", null, [], true, true),
        new("entityimage", "Image", null, [], false, true),
    ]);

    private static readonly SourceTable Note = new("annotation", "annotations", "annotationid", false, false, false,
        [new("annotationid", "Uniqueidentifier", null, [], false, true), new("documentbody", "Memo", null, [], false, true)]);

    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static DataverseClient Client(FakeDataverse fake) => new(fake, FakeDataverse.Source, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask);

    private async Task<(MigrationValue? Value, byte[] Content)> Export(FakeDataverse fake, SourceTable table, string column, JsonElement row, string? entryPath)
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        MigrationValue? value;
        using (var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase))
        using (var client = Client(fake))
        {
            value = await new FileExporter(client, writer).ExportAsync(table, table.Columns.Single(c => c.LogicalName == column), Id, row, CancellationToken.None);
            writer.Complete(new PackageManifest());
        }
        if (entryPath is null) return (value, []);
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        using var s = reader.OpenEntry(entryPath);
        using var m = new MemoryStream();
        await s.CopyToAsync(m);
        return (value, m.ToArray());
    }

    [Fact]
    public async Task Large_file_is_downloaded_in_ranges()
    {
        var content = RandomNumberGenerator.GetBytes(20 * 1024 * 1024);
        var fake = new FakeDataverse().Get(@"^/api/data/v9\.2/accounts\(11111111-1111-1111-1111-111111111111\)/xv_contract/\$value$", (request, _) =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var from = (int)range.From!.Value;
            var to = (int)Math.Min(range.To!.Value, content.Length - 1);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(content, from, to - from + 1) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            response.Headers.Add("x-ms-file-name", "contract.pdf");
            response.Headers.Add("x-ms-file-size", content.Length.ToString());
            return response;
        });
        var path = $"files/account/xv_contract/{Id}";
        var (value, stored) = await Export(fake, Account, "xv_contract", Row("""{"xv_contract":"22222222-2222-2222-2222-222222222222"}"""), path);
        value.Should().Be(new FileValue(path, "contract.pdf", "application/pdf", content.Length));
        SHA256.HashData(stored).Should().Equal(SHA256.HashData(content));
        fake.Requests.Should().HaveCount(5);
        fake.Requests.Select(r => r.Request.Headers.Range!.Ranges.Single().From).Should().Equal(0, 4194304, 8388608, 12582912, 16777216);
    }

    [Fact]
    public async Task Server_ignoring_the_range_still_gives_the_whole_file()
    {
        var content = Encoding.UTF8.GetBytes("small file");
        var fake = new FakeDataverse().Get(@"xv_contract/\$value$", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            response.Headers.Add("x-ms-file-name", "a.txt");
            return response;
        });
        var path = $"files/account/xv_contract/{Id}";
        var (value, stored) = await Export(fake, Account, "xv_contract", Row("""{"xv_contract":"22222222-2222-2222-2222-222222222222"}"""), path);
        stored.Should().Equal(content);
        ((FileValue)value!).Bytes.Should().Be(content.Length);
        fake.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Image_uses_full_size()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var fake = new FakeDataverse().Get(@"^/api/data/v9\.2/accounts\(11111111-1111-1111-1111-111111111111\)/entityimage/\$value\?size=full$",
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
        var path = $"files/account/entityimage/{Id}";
        var (value, stored) = await Export(fake, Account, "entityimage", Row("""{"entityimage":"AAAA"}"""), path);
        value.Should().Be(new ImageValue(path));
        stored.Should().Equal(png);
    }

    [Fact]
    public async Task Note_body_streams_to_package()
    {
        var body = Encoding.UTF8.GetBytes(new string('x', 300_000));
        var fake = new FakeDataverse().Get(@"^/api/data/v9\.2/annotations\(11111111-1111-1111-1111-111111111111\)/documentbody/\$value$",
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Convert.ToBase64String(body)) });
        var path = $"files/annotation/documentbody/{Id}";
        var (value, stored) = await Export(fake, Note, "documentbody",
            Row("""{"isdocument":true,"filename":"notes.txt","mimetype":"text/plain","filesize":300000}"""), path);
        value.Should().Be(new FileValue(path, "notes.txt", "text/plain", body.Length));
        stored.Should().Equal(body);
    }

    [Fact]
    public async Task Note_body_falls_back_to_json_when_value_is_refused()
    {
        var body = Encoding.UTF8.GetBytes("hello");
        var fake = new FakeDataverse()
            .Get(@"documentbody/\$value$", (_, _) => FakeDataverse.Json("""{"error":{"message":"not supported"}}""", HttpStatusCode.BadRequest))
            .GetJson(@"^/api/data/v9\.2/annotations\(11111111-1111-1111-1111-111111111111\)\?\$select=documentbody$",
                $$"""{"documentbody":"{{Convert.ToBase64String(body)}}"}""");
        var path = $"files/annotation/documentbody/{Id}";
        var (_, stored) = await Export(fake, Note, "documentbody", Row("""{"isdocument":true,"filename":"a.txt","mimetype":"text/plain"}"""), path);
        stored.Should().Equal(body);
    }

    [Fact]
    public async Task Note_body_sent_as_binary_is_kept_as_is()
    {
        var body = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF, 0x0A, 0x80, 0x7F };
        var fake = new FakeDataverse().Get(@"documentbody/\$value$",
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        var path = $"files/annotation/documentbody/{Id}";
        var (value, stored) = await Export(fake, Note, "documentbody",
            Row($$"""{"isdocument":true,"filename":"a.pdf","mimetype":"application/pdf","filesize":{{body.Length}}}"""), path);
        stored.Should().Equal(body);
        value.Should().Be(new FileValue(path, "a.pdf", "application/pdf", body.Length));
    }

    [Fact]
    public async Task Note_body_sent_as_binary_without_a_size_is_kept_as_is()
    {
        var body = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF };
        var fake = new FakeDataverse().Get(@"documentbody/\$value$",
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        var path = $"files/annotation/documentbody/{Id}";
        var (_, stored) = await Export(fake, Note, "documentbody", Row("""{"isdocument":true,"filename":"a.pdf"}"""), path);
        stored.Should().Equal(body);
    }

    [Fact]
    public async Task Note_body_with_the_size_of_its_text_is_not_decoded()
    {
        var body = Encoding.UTF8.GetBytes("QUJD");
        var fake = new FakeDataverse().Get(@"documentbody/\$value$",
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        var path = $"files/annotation/documentbody/{Id}";
        var (_, stored) = await Export(fake, Note, "documentbody", Row("""{"isdocument":true,"filename":"a.txt","filesize":4}"""), path);
        stored.Should().Equal(body, "a 4-byte file whose text happens to be base64 is the file itself");
    }

    [Fact]
    public async Task Note_body_json_with_line_breaks_is_decoded()
    {
        var body = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        var wrapped = Convert.ToBase64String(body, Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\\r\\n");
        var fake = new FakeDataverse()
            .Get(@"documentbody/\$value$", (_, _) => FakeDataverse.Json("""{"error":{"message":"not supported"}}""", HttpStatusCode.BadRequest))
            .GetJson(@"\?\$select=documentbody$", $$"""{"documentbody":"{{wrapped}}"}""");
        var path = $"files/annotation/documentbody/{Id}";
        var (_, stored) = await Export(fake, Note, "documentbody", Row("""{"isdocument":true,"filename":"a.bin","filesize":200}"""), path);
        stored.Should().Equal(body);
    }

    [Fact]
    public async Task Null_file_column_makes_no_request()
    {
        var fake = new FakeDataverse();
        var (value, _) = await Export(fake, Account, "xv_contract", Row("""{"xv_contract":null}"""), null);
        value.Should().Be(NullValue.Instance);
        fake.Requests.Should().BeEmpty();
    }

    private static FakeDataverse Ranged(byte[] content, Func<int, HttpStatusCode> status)
    {
        var call = 0;
        return new FakeDataverse().Get(@"xv_contract/\$value$", (request, _) =>
        {
            call++;
            var range = request.Headers.Range!.Ranges.Single();
            var from = (int)range.From!.Value;
            var code = status(call);
            HttpResponseMessage response;
            if (code == HttpStatusCode.OK)
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            else
            {
                var to = (int)Math.Min(range.To!.Value, content.Length - 1);
                response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(content, from, to - from + 1) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
            }
            response.Headers.Add("x-ms-file-size", content.Length.ToString());
            return response;
        });
    }

    [Fact]
    public async Task Whole_body_after_a_range_is_an_error_not_a_corrupt_file()
    {
        var content = RandomNumberGenerator.GetBytes(6 * 1024 * 1024);
        var fake = Ranged(content, call => call == 1 ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
        await FluentActions.Awaiting(() => Export(fake, Account, "xv_contract", Row("""{"xv_contract":"x"}"""), null))
            .Should().ThrowAsync<DataverseException>().WithMessage("*ranges*");
    }

    [Fact]
    public async Task Short_file_is_an_error()
    {
        var content = Encoding.UTF8.GetBytes("abc");
        var fake = new FakeDataverse().Get(@"xv_contract/\$value$", (_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            r.Headers.Add("x-ms-file-size", "10");
            return r;
        });
        await FluentActions.Awaiting(() => Export(fake, Account, "xv_contract", Row("""{"xv_contract":"x"}"""), null))
            .Should().ThrowAsync<DataverseException>().WithMessage("*3 bytes received, 10 announced*");
    }

    [Fact]
    public async Task Deleted_file_is_reported_as_missing()
    {
        var fake = new FakeDataverse().Get(@"xv_contract/\$value$", (_, _) => FakeDataverse.Json("""{"error":{"message":"gone"}}""", HttpStatusCode.NotFound));
        await FluentActions.Awaiting(() => Export(fake, Account, "xv_contract", Row("""{"xv_contract":"x"}"""), null))
            .Should().ThrowAsync<SourceFileMissingException>();
    }
}
