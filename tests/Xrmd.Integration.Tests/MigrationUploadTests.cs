using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Admin.Migration;
using Xrmd.Files;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The chunked upload of a migration package to <c>/admin/migrate/packages</c> (migration M3, task 3; spec §7.1).</summary>
[Collection("xrmd")]
public sealed class MigrationUploadTests(XrmdFixture fx)
{
    private IFileStore Files => fx.App.Services.GetRequiredService<IFileStore>();

    private string Database => TestProvider.DatabaseOf(fx.ConnectionString);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, Guid? oid = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid ?? fx.AdminObjectId, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    private static ByteArrayContent Bytes(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private async Task<Guid> NewPackageAsync()
    {
        var res = await SendAsync(HttpMethod.Post, "admin/migrate/packages");
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return Guid.Parse(JsonNode.Parse(body)!["packageId"]!.GetValue<string>());
    }

    private async Task PutChunkAsync(Guid id, int n, byte[] bytes, HttpStatusCode expected = HttpStatusCode.NoContent)
    {
        var res = await SendAsync(HttpMethod.Put, $"admin/migrate/packages/{id}/chunks/{n}", Bytes(bytes));
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
    }

    private Task<HttpResponseMessage> CompleteAsync(Guid id, int chunks, string sha256) =>
        SendAsync(HttpMethod.Post, $"admin/migrate/packages/{id}/complete", JsonContent.Create(new { chunks, sha256 }));

    private static byte[] Random(int size)
    {
        var bytes = new byte[size];
        System.Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task Upload_three_chunks_complete_and_read_back()
    {
        var parts = new[] { Random(1000), Random(2000), Random(500) };
        var whole = parts.SelectMany(p => p).ToArray();
        var id = await NewPackageAsync();

        // Out of order, and chunk 1 sent twice (a retry replaces it).
        await PutChunkAsync(id, 2, parts[2]);
        await PutChunkAsync(id, 1, Random(10));
        await PutChunkAsync(id, 0, parts[0]);
        await PutChunkAsync(id, 1, parts[1]);

        var status = JsonNode.Parse(await (await SendAsync(HttpMethod.Get, $"admin/migrate/packages/{id}")).Content.ReadAsStringAsync())!;
        status["state"]!.GetValue<string>().Should().Be("uploading");
        status["chunks"]!.AsArray().Select(n => n!.GetValue<int>()).Should().BeEquivalentTo([0, 1, 2]);

        var res = await CompleteAsync(id, 3, Sha(whole));
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode.Parse(body)!["bytes"]!.GetValue<long>().Should().Be(whole.Length);

        await using var stored = await FileStoreReadStream.OpenAsync(Files, MigrationPackageKeys.Package(Database, id), default);
        var copy = new MemoryStream();
        await stored.CopyToAsync(copy);
        copy.ToArray().Should().Equal(whole);
        (await Files.GetLengthAsync(MigrationPackageKeys.Chunk(Database, id, 0), default)).Should().BeNull("the chunks go once the package is complete");
        var done = JsonNode.Parse(await (await SendAsync(HttpMethod.Get, $"admin/migrate/packages/{id}")).Content.ReadAsStringAsync())!;
        done["state"]!.GetValue<string>().Should().Be("complete");

        (await SendAsync(HttpMethod.Delete, $"admin/migrate/packages/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Get, $"admin/migrate/packages/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Files.GetLengthAsync(MigrationPackageKeys.Package(Database, id), default)).Should().BeNull();
    }

    [Fact]
    public async Task Missing_chunk_is_reported()
    {
        var id = await NewPackageAsync();
        await PutChunkAsync(id, 0, Random(10));
        await PutChunkAsync(id, 2, Random(10));

        var res = await CompleteAsync(id, 4, new string('0', 64));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("Missing chunks: 1, 3.");
    }

    [Fact]
    public async Task Checksum_mismatch_keeps_chunks()
    {
        var id = await NewPackageAsync();
        await PutChunkAsync(id, 0, Random(100));

        var res = await CompleteAsync(id, 1, new string('0', 64));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("The package checksum does not match.");

        (await Files.GetLengthAsync(MigrationPackageKeys.Chunk(Database, id, 0), default)).Should().Be(100);
        (await Files.GetLengthAsync(MigrationPackageKeys.Package(Database, id), default)).Should().BeNull();
        var status = JsonNode.Parse(await (await SendAsync(HttpMethod.Get, $"admin/migrate/packages/{id}")).Content.ReadAsStringAsync())!;
        status["state"]!.GetValue<string>().Should().Be("uploading");
    }

    [Fact]
    public async Task Chunk_over_8MiB_is_413_and_bad_number_is_400()
    {
        var id = await NewPackageAsync();
        await PutChunkAsync(id, 0, new byte[8 * 1024 * 1024 + 1], HttpStatusCode.RequestEntityTooLarge);
        await PutChunkAsync(id, 0, new byte[8 * 1024 * 1024]);
        await PutChunkAsync(id, 100000, [1], HttpStatusCode.BadRequest);
        await PutChunkAsync(Guid.NewGuid(), 0, [1], HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Chunk_after_complete_is_409()
    {
        var bytes = Random(64);
        var id = await NewPackageAsync();
        await PutChunkAsync(id, 0, bytes);
        (await CompleteAsync(id, 1, Sha(bytes))).StatusCode.Should().Be(HttpStatusCode.OK);

        await PutChunkAsync(id, 1, bytes, HttpStatusCode.Conflict);
        await SendAsync(HttpMethod.Delete, $"admin/migrate/packages/{id}");
    }

    [Fact]
    public async Task Non_admin_gets_403()
    {
        var oid = Guid.NewGuid();
        await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);

        (await SendAsync(HttpMethod.Post, "admin/migrate/packages", oid: oid)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
