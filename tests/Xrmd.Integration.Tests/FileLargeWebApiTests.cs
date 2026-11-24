using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>A 50 MB file through the Web API: chunked upload in 4 MB chunks, then ranged download in 4 MB ranges.</summary>
[Collection("xrmd")]
public sealed class FileLargeWebApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Doc = "new_doc";
    private const int ChunkSize = 4194304;
    private const int FileSize = 50 * 1024 * 1024;

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    /// <summary>Deterministic bytes: the same seed always gives the same content.</summary>
    internal static byte[] SeededBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        configure?.Invoke(request);
        return fx.Client.SendAsync(request);
    }

    /// <summary>A new user-owned table with the file column <see cref="Doc"/> (100 MB) and a record of it.</summary>
    private async Task<(string Table, Guid Id)> RecordAsync()
    {
        var table = "new_fl" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = table, Attribute = new FileAttributeMetadata { LogicalName = Doc, SchemaName = Doc, MaxSizeInKB = 100 * 1024 },
        });
        return (table, await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" }));
    }

    private static string BlockId(int i) => Convert.ToBase64String(BitConverter.GetBytes(i));

    private Task<long> SessionsOfAsync(Guid recordId) =>
        TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.filesession WHERE record_id = @id", ("@id", recordId));

    [Fact]
    public async Task A_three_block_upload_commits_closes_its_session_and_has_the_size_of_its_blocks()
    {
        // Plan 2.7b, Task 6, on both engines: the session row is read under its update lock, written by each block and deleted
        // by the commit, in the commit's transaction.
        var (table, id) = await RecordAsync();
        var token = ((InitializeFileBlocksUploadResponse)await Admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = Doc, FileName = "three.bin",
        })).FileContinuationToken;
        byte[][] blocks = [SeededBytes(1000, 1), SeededBytes(2000, 2), SeededBytes(3, 3)];
        for (var i = 0; i < blocks.Length; i++)
            await Admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = BlockId(i), BlockData = blocks[i] });
        (await SessionsOfAsync(id)).Should().Be(1);

        var commit = (CommitFileBlocksUploadResponse)await Admin.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [BlockId(0), BlockId(1), BlockId(2)], FileName = "three.bin", MimeType = "application/octet-stream",
        });

        commit.FileSizeInBytes.Should().Be(3003);
        (await SessionsOfAsync(id)).Should().Be(0, "a committed session is closed");
        Convert.ToInt64(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT filesizeinbytes FROM data.fileattachment WHERE fileattachmentid = @f",
            ("@f", commit.FileId))).Should().Be(3003);
        var set = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(table)!.EntitySetName;
        using var whole = await SendAsync(HttpMethod.Get, $"/api/data/v9.2/{set}({id})/{Doc}/$value");
        whole.StatusCode.Should().Be(HttpStatusCode.OK);
        (await whole.Content.ReadAsByteArrayAsync()).Should().Equal([.. blocks[0], .. blocks[1], .. blocks[2]]);

        // The expiry sweep's delete returns the sessions it removes (OUTPUT deleted on SQL Server, RETURNING on PostgreSQL).
        await Admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = Doc, FileName = "expired.bin",
        });
        var expired = (Guid)(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT id FROM meta.filesession WHERE record_id = @id", ("@id", id)))!;
        await TestSql.ExecAsync(fx.ConnectionString,
            "UPDATE meta.filesession SET last_used_on = DATEADD(hour, -25, SYSUTCDATETIME()) WHERE id = @s", ("@s", expired));
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            (await FileSessionStore.DeleteExpiredAsync(new DataSession(conn, null), DateTime.UtcNow - FileSessionStore.Lifetime, default))
                .Should().Contain(expired);
        (await SessionsOfAsync(id)).Should().Be(0, "the expired session is deleted");
    }

    [Fact]
    public async Task Two_blocks_of_one_upload_sent_at_once_wait_for_each_other_and_both_are_kept()
    {
        // Plan 2.7b, Task 6: UploadBlock reads its session row with an update lock held to its commit (SQL Server: UPDLOCK,
        // ROWLOCK, HOLDLOCK; PostgreSQL: FOR UPDATE), so a second block of the same session waits and then adds to the first one's
        // list instead of overwriting it.
        var (table, id) = await RecordAsync();
        var files = fx.App.Services.GetRequiredService<FileColumnService>();
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        string token;
        await using (var init = await factory.OpenAsync())
            token = await files.InitializeUploadAsync(Caller.Internal, new EntityReference(table, id), Doc, "race.bin", new DataSession(init, null), default);

        await using var conn = await factory.OpenAsync();
        int spid;
        await using (var cmd = TestSql.Command(conn, TestSql.SessionIdQuery)) spid = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Task second;
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await files.UploadBlockAsync(Caller.Internal, token, BlockId(0), new MemoryStream(SeededBytes(10, 1)), new DataSession(conn, tx), default);
            second = Task.Run(async () =>
            {
                await using var other = await factory.OpenAsync();
                await using var otherTx = await other.BeginTransactionAsync();
                await files.UploadBlockAsync(Caller.Internal, token, BlockId(1), new MemoryStream(SeededBytes(20, 2)), new DataSession(other, otherTx), default);
                await otherTx.CommitAsync();
            });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Convert.ToInt32(await TestSql.ScalarAsync(fx.ConnectionString, TestSql.BlockedByQuery, ("@spid", spid))) == 0)
            {
                second.IsCompleted.Should().BeFalse("the second block must wait for the first one's transaction");
                DateTime.UtcNow.Should().BeBefore(deadline);
                await Task.Delay(20);
            }
            await tx.CommitAsync();
        }
        await second;

        await using var read = await factory.OpenAsync();
        (await files.DescribeUploadAsync(Caller.Internal, token, new DataSession(read, null), default)).BlockIds
            .Should().Equal(BlockId(0), BlockId(1));
    }

    [Fact]
    public async Task WebApi_chunked_50MB_round_trip_with_range_download()
    {
        var (table, id) = await RecordAsync();
        var set = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(table)!.EntitySetName;
        var path = $"/api/data/v9.2/{set}({id})/{Doc}";
        var content = SeededBytes(FileSize, 5081);
        var expected = SHA256.HashData(content);

        string location;
        using (var start = await SendAsync(HttpMethod.Patch, path + "?x-ms-file-name=big.bin", configure: r => r.Headers.Add("x-ms-transfer-mode", "chunked")))
        {
            start.StatusCode.Should().Be(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
            location = start.Headers.Location!.PathAndQuery;
        }
        for (long from = 0; from < FileSize; from += ChunkSize)
        {
            var to = Math.Min(from + ChunkSize, FileSize) - 1;
            var body = new ByteArrayContent(content, (int)from, (int)(to - from + 1));
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            body.Headers.ContentRange = new ContentRangeHeaderValue(from, to, FileSize);
            using var chunk = await SendAsync(HttpMethod.Patch, location, body, r => r.Headers.Add("x-ms-file-name", "big.bin"));
            chunk.StatusCode.Should().Be(to + 1 < FileSize ? HttpStatusCode.PartialContent : HttpStatusCode.NoContent,
                await chunk.Content.ReadAsStringAsync());
        }

        // Ranged download in 4 MB ranges, reassembled and hashed.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        while (received < FileSize)
        {
            var to = Math.Min(received + ChunkSize, FileSize) - 1;
            using var part = await SendAsync(HttpMethod.Get, path + "/$value", configure: r => r.Headers.Range = new RangeHeaderValue(received, to));
            part.StatusCode.Should().Be(HttpStatusCode.PartialContent);
            part.Content.Headers.ContentRange!.ToString().Should().Be($"bytes {received}-{to}/{FileSize}");
            var bytes = await part.Content.ReadAsByteArrayAsync();
            bytes.Length.Should().Be((int)(to - received + 1));
            hash.AppendData(bytes);
            received += bytes.Length;
        }
        hash.GetHashAndReset().Should().Equal(expected, "the ranged download is the uploaded file");

        // The whole file in one GET.
        using var whole = await SendAsync(HttpMethod.Get, path + "/$value");
        whole.StatusCode.Should().Be(HttpStatusCode.OK);
        SHA256.HashData(await whole.Content.ReadAsByteArrayAsync()).Should().Equal(expected);
    }
}
