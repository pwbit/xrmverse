using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The file upload messages (InitializeFileBlocksUpload, UploadBlock, CommitFileBlocksUpload, DeleteFile) through the
/// organization service, the continuation sessions (ruling F6), the commit semantics (F7), file values on Update (F4), the
/// cascades (F8), the companion <c>_name</c> on read and the read access of <c>fileattachment</c> rows, which follows the
/// owning record.
/// </summary>
[Collection("xrmd")]
public sealed class FileUploadTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Doc = "new_doc";
    private const int FileTooBig = unchecked((int)0x80044a02);

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private IFileStore Blobs => fx.App.Services.GetRequiredService<IFileStore>();
    private FileColumnService Files => fx.App.Services.GetRequiredService<FileColumnService>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private static string Db => "xrmverse_test";

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A user-owned table with the file column <see cref="Doc"/> (and an image column when asked).</summary>
    private async Task<string> NewTableAsync(int? maxSizeInKB = null, bool image = false)
    {
        var name = "new_fu" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name, Attribute = new FileAttributeMetadata { LogicalName = Doc, SchemaName = Doc, MaxSizeInKB = maxSizeInKB },
        });
        if (image)
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name, Attribute = new ImageAttributeMetadata { LogicalName = "new_pic", SchemaName = "new_pic" },
            });
        return name;
    }

    private static string BlockId(int i) => Convert.ToBase64String(Encoding.ASCII.GetBytes($"block-{i:D6}"));

    private static byte[] Bytes(int length, byte seed) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed))];

    private static async Task<string> InitializeAsync(OrganizationService s, string table, Guid id, string fileName = "report.pdf",
        string attribute = Doc) =>
        ((InitializeFileBlocksUploadResponse)await s.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = attribute, FileName = fileName,
        })).FileContinuationToken;

    private static Task UploadBlockAsync(OrganizationService s, string token, string blockId, byte[] data) =>
        s.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = blockId, BlockData = data });

    private static async Task<CommitFileBlocksUploadResponse> CommitAsync(OrganizationService s, string token, string[] blockList,
        string fileName = "report.pdf", string mimeType = "application/pdf") =>
        (CommitFileBlocksUploadResponse)await s.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = blockList, FileName = fileName, MimeType = mimeType,
        });

    /// <summary>Uploads <paramref name="blocks"/> in order and commits them in order; returns the file id.</summary>
    private async Task<Guid> UploadAsync(string table, Guid id, string fileName, params byte[][] blocks)
    {
        var token = await InitializeAsync(Admin, table, id, fileName);
        for (var i = 0; i < blocks.Length; i++) await UploadBlockAsync(Admin, token, BlockId(i), blocks[i]);
        return (await CommitAsync(Admin, token, [.. Enumerable.Range(0, blocks.Length).Select(BlockId)], fileName)).FileId;
    }

    private async Task<byte[]?> BlobAsync(Guid fileId)
    {
        await fx.DrainFileDeletionsAsync();
        if (await Blobs.GetLengthAsync($"{Db}/{fileId}", default) is null) return null;
        await using var stream = await Blobs.OpenReadAsync($"{Db}/{fileId}", 0, null, default);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private async Task<List<string>> StagedKeysAsync(Guid sessionId)
    {
        await fx.DrainFileDeletionsAsync();
        var keys = new List<string>();
        await foreach (var key in Blobs.ListAsync($"{Db}/_upload/{sessionId}/", default)) keys.Add(key);
        return keys;
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.AddParameter(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<Guid> SessionOfAsync(Guid recordId) =>
        (Guid)(await ScalarAsync("SELECT id FROM meta.filesession WHERE record_id = @id", ("@id", recordId)))!;

    private async Task<int> AttachmentRowsAsync(Guid recordId) =>
        Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM data.fileattachment WHERE objectid = @id", ("@id", recordId)));

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act, int code)
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(code, fault.Message);
        return fault;
    }

    /// <summary>A user (and its service) with the given privileges on <paramref name="table"/>, at Global depth unless given.</summary>
    private async Task<(Guid UserId, OrganizationService Service)> UserAsync(string table, DepthMask depth, params string[] rights)
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        var schema = Store.GetEntity(table)!.SchemaName;
        await GrantAsync(fx, user, team: false, [.. rights.Select(r => ($"prv{r}{schema}", depth))]);
        return (user, ServiceFor(fx, objectId));
    }

    private HttpRequestMessage WebApi(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return req;
    }

    // ---- upload, commit and Retrieve --------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_commit_sets_column_to_file_id_and_name()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        byte[] first = Bytes(1000, 1), second = Bytes(500, 2);

        var token = await InitializeAsync(Admin, table, id, "report.pdf");
        await UploadBlockAsync(Admin, token, BlockId(0), first);
        await UploadBlockAsync(Admin, token, BlockId(1), second);
        var response = await CommitAsync(Admin, token, [BlockId(0), BlockId(1)]);

        response.FileId.Should().NotBe(Guid.Empty);
        response.FileSizeInBytes.Should().Be(1500);
        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Doc, Doc + "_name"));
        record[Doc].Should().Be(response.FileId);
        record[Doc + "_name"].Should().Be("report.pdf");
        (await BlobAsync(response.FileId)).Should().Equal([.. first, .. second]);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.filesession WHERE record_id = @id", ("@id", id))).Should().Be(0, "a committed session is closed");
    }

    [Fact]
    public async Task Retrieve_returns_file_id_and_name()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var fileId = await UploadAsync(table, id, "notes.txt", Bytes(10, 3));

        var all = await Admin.RetrieveAsync(table, id, new ColumnSet(true));
        all[Doc].Should().Be(fileId);
        all[Doc + "_name"].Should().Be("notes.txt");
        var listed = (await Admin.RetrieveMultipleAsync(new QueryExpression(table) { ColumnSet = new ColumnSet(Doc + "_name") })).Entities
            .Single(e => e.Id == id);
        listed[Doc + "_name"].Should().Be("notes.txt");

        // Web API: the companion is a property of $metadata and of $select (read-only: a write is ignored, as for other read-only columns).
        var set = Store.GetEntity(table)!.EntitySetName;
        var res = await fx.Client.SendAsync(WebApi(HttpMethod.Get, $"/api/data/v9.2/{set}({id})?$select={Doc},{Doc}_name"));
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!;
        json[Doc]!.GetValue<Guid>().Should().Be(fileId);
        json[Doc + "_name"]!.GetValue<string>().Should().Be("notes.txt");

        var metadata = await (await fx.Client.SendAsync(WebApi(HttpMethod.Get, "/api/data/v9.2/$metadata"))).Content.ReadAsStringAsync();
        metadata.Should().Contain($"<Property Name=\"{Doc}_name\" Type=\"Edm.String\"");

        var patch = WebApi(HttpMethod.Patch, $"/api/data/v9.2/{set}({id})");
        patch.Content = new StringContent($$"""{ "{{Doc}}_name": "renamed.txt" }""", Encoding.UTF8, "application/json");
        (await fx.Client.SendAsync(patch)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc + "_name")))[Doc + "_name"].Should().Be("notes.txt");
    }

    [Fact]
    public async Task fileattachment_row_has_size_mime_regardingfieldname()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id, "data.csv");
        await UploadBlockAsync(Admin, token, BlockId(0), Bytes(321, 4));
        var fileId = (await CommitAsync(Admin, token, [BlockId(0)], "data.csv", "text/csv")).FileId;

        var row = await Admin.RetrieveAsync("fileattachment", fileId, new ColumnSet(true));
        row["filename"].Should().Be("data.csv");
        row["mimetype"].Should().Be("text/csv");
        row["filesizeinbytes"].Should().Be(321L);
        row["regardingfieldname"].Should().Be(Doc);
        row["objecttypecode"].Should().Be(table);
        row.GetAttributeValue<EntityReference>("objectid").Should().BeEquivalentTo(new EntityReference(table, id), o => o.Including(r => r.LogicalName).Including(r => r.Id));
        (await ScalarAsync("SELECT objectidtype FROM data.fileattachment WHERE fileattachmentid = @id", ("@id", fileId)))
            .Should().Be(Store.GetEntity(table)!.ObjectTypeCode);
    }

    // ---- block and commit rules -------------------------------------------------------------------------------------

    [Fact]
    public async Task Out_of_order_blocks_commit_in_BlockList_order()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        byte[] a = Bytes(300, 5), b = Bytes(200, 6), c = Bytes(100, 7);

        var token = await InitializeAsync(Admin, table, id);
        await UploadBlockAsync(Admin, token, BlockId(2), c);
        await UploadBlockAsync(Admin, token, BlockId(0), a);
        await UploadBlockAsync(Admin, token, BlockId(1), b);
        var fileId = (await CommitAsync(Admin, token, [BlockId(0), BlockId(1), BlockId(2)])).FileId;

        (await BlobAsync(fileId)).Should().Equal([.. a, .. b, .. c]);
    }

    [Fact]
    public async Task Unknown_or_missing_block_in_BlockList_is_refused()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);
        await UploadBlockAsync(Admin, token, BlockId(0), Bytes(10, 1));
        await UploadBlockAsync(Admin, token, BlockId(1), Bytes(10, 2));

        (await FaultAsync(() => CommitAsync(Admin, token, [BlockId(0), BlockId(9)]), ErrorCodes.InvalidArgument)).Message.Should().Contain("not uploaded");
        (await FaultAsync(() => CommitAsync(Admin, token, [BlockId(0)]), ErrorCodes.InvalidArgument)).Message.Should().Contain("omits");
        (await FaultAsync(() => CommitAsync(Admin, token, [BlockId(0), BlockId(1), BlockId(0)]), ErrorCodes.InvalidArgument))
            .Message.Should().Contain("more than once");

        // The session is still usable: the right list commits.
        (await CommitAsync(Admin, token, [BlockId(1), BlockId(0)])).FileSizeInBytes.Should().Be(20);
        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc))).Contains(Doc).Should().BeTrue();
    }

    [Fact]
    public async Task BlockId_rules_enforced()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);

        (await FaultAsync(() => UploadBlockAsync(Admin, token, "not base64!", [1]), ErrorCodes.InvalidArgument)).Message.Should().Contain("Base64");
        (await FaultAsync(() => UploadBlockAsync(Admin, token, Convert.ToBase64String(new byte[65]), [1]), ErrorCodes.InvalidArgument))
            .Message.Should().Contain("64 bytes");
        (await FaultAsync(() => UploadBlockAsync(Admin, token, "", [1]), ErrorCodes.InvalidArgument)).Message.Should().Contain("BlockId");

        await UploadBlockAsync(Admin, token, Convert.ToBase64String(new byte[64]), [1]);
        (await FaultAsync(() => UploadBlockAsync(Admin, token, Convert.ToBase64String(new byte[8]), [2]), ErrorCodes.InvalidArgument))
            .Message.Should().Contain("same length");
    }

    [Fact]
    public async Task Block_over_4MB_is_refused()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);

        (await FaultAsync(() => UploadBlockAsync(Admin, token, BlockId(0), new byte[4194305]), ErrorCodes.InvalidArgument))
            .Message.Should().Contain("4194304");
        await UploadBlockAsync(Admin, token, BlockId(0), new byte[4194304]);
    }

    [Fact]
    public async Task A_session_stages_at_most_50000_blocks()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);
        await UploadBlockAsync(Admin, token, BlockId(0), [1]);
        // The session row as 50,000 staged blocks would leave it (empty ones: the size limit is not what is checked here).
        var blocks = Enumerable.Range(0, FileColumnService.MaxUncommittedBlocks).Select(i => new StagedBlock(BlockId(i), i, 0)).ToList();
        await ScalarAsync("UPDATE meta.filesession SET block_ids = @blocks WHERE record_id = @id",
            ("@blocks", System.Text.Json.JsonSerializer.Serialize(blocks)), ("@id", id));

        var fault = await FaultAsync(() => UploadBlockAsync(Admin, token, BlockId(FileColumnService.MaxUncommittedBlocks), [2]), ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("50000 uncommitted blocks");
        // Replacing a staged block does not add one.
        await UploadBlockAsync(Admin, token, BlockId(7), [3]);
    }

    [Fact]
    public async Task A_replaced_block_whose_UploadBlock_rolls_back_keeps_the_staged_content()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);
        await UploadBlockAsync(Admin, token, BlockId(0), Bytes(10, 1));
        await UploadBlockAsync(Admin, token, BlockId(1), Bytes(5, 2));
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "UploadBlock", null, stage: 40, config: "invalid:no block");

        // The same block id again, with other content and another size: a post-operation step rolls it back.
        (await FaultAsync(() => UploadBlockAsync(Admin, token, BlockId(0), Bytes(30, 9)), ErrorCodes.ISVAborted)).Message.Should().Be("no block");
        await PluginTestHelper.ClearAsync(fx);

        var commit = await CommitAsync(Admin, token, [BlockId(0), BlockId(1)]);
        commit.FileSizeInBytes.Should().Be(15);
        (await BlobAsync(commit.FileId)).Should().Equal([.. Bytes(10, 1), .. Bytes(5, 2)]);
    }

    [Fact]
    public async Task Size_limit_enforced_at_the_crossing_block_and_staging_is_cleared()
    {
        var table = await NewTableAsync(maxSizeInKB: 1);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);
        await UploadBlockAsync(Admin, token, BlockId(0), Bytes(600, 1));
        await UploadBlockAsync(Admin, token, BlockId(0), Bytes(600, 2)); // the same block again replaces it: still 600 bytes
        var session = await SessionOfAsync(id);
        (await StagedKeysAsync(session)).Should().HaveCount(1);

        var fault = await FaultAsync(() => UploadBlockAsync(Admin, token, BlockId(1), Bytes(600, 3)), FileTooBig);

        fault.Message.Should().Be("The file is larger than the maximum size of the column.");
        (await StagedKeysAsync(session)).Should().BeEmpty();
        (await ScalarAsync("SELECT COUNT(*) FROM meta.filesession WHERE id = @id", ("@id", session))).Should().Be(0);
        await FaultAsync(() => CommitAsync(Admin, token, [BlockId(0)]), ErrorCodes.InvalidArgument);
    }

    // ---- tokens and security ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Foreign_caller_token_is_refused_without_revealing_session()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);
        var (_, other) = await UserAsync(table, DepthMask.Global, "Read", "Write");
        var unknown = FileSessionTokens.Derive(new byte[32], Guid.NewGuid()).Issue(Guid.NewGuid(), Guid.NewGuid());

        var foreign = await FaultAsync(() => UploadBlockAsync(other, token, BlockId(0), [1]), ErrorCodes.InvalidArgument);
        var missing = await FaultAsync(() => UploadBlockAsync(other, unknown, BlockId(0), [1]), ErrorCodes.InvalidArgument);
        var foreignCommit = await FaultAsync(() => CommitAsync(other, token, []), ErrorCodes.InvalidArgument);

        foreign.Message.Should().Be(missing.Message).And.Be(foreignCommit.Message);
        foreign.HttpStatus.Should().Be(missing.HttpStatus);
        // The owner's session is untouched.
        await UploadBlockAsync(Admin, token, BlockId(0), [1, 2, 3]);
    }

    [Fact]
    public async Task Tampered_token_is_refused()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);
        var tampered = (token[0] == 'A' ? "B" : "A") + token[1..];
        var unknown = FileSessionTokens.Derive(new byte[32], Guid.NewGuid()).Issue(Guid.NewGuid(), Guid.NewGuid());

        var fault = await FaultAsync(() => UploadBlockAsync(Admin, tampered, BlockId(0), [1]), ErrorCodes.InvalidArgument);
        fault.Message.Should().Be((await FaultAsync(() => UploadBlockAsync(Admin, unknown, BlockId(0), [1]), ErrorCodes.InvalidArgument)).Message);
        await FaultAsync(() => UploadBlockAsync(Admin, "garbage", BlockId(0), [1]), ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Expired_session_is_refused_and_swept()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);
        await UploadBlockAsync(Admin, token, BlockId(0), Bytes(100, 1));
        var session = await SessionOfAsync(id);
        await ScalarAsync("UPDATE meta.filesession SET last_used_on = DATEADD(hour, -25, SYSUTCDATETIME()) WHERE id = @id", ("@id", session));

        await FaultAsync(() => UploadBlockAsync(Admin, token, BlockId(1), Bytes(100, 2)), ErrorCodes.InvalidArgument);

        (await ScalarAsync("SELECT COUNT(*) FROM meta.filesession WHERE id = @id", ("@id", session))).Should().Be(0);
        (await StagedKeysAsync(session)).Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_removes_expired_sessions_and_old_orphan_blobs()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);
        await UploadBlockAsync(Admin, token, BlockId(0), Bytes(100, 1));
        var session = await SessionOfAsync(id);
        var kept = await UploadAsync(table, id, "kept.bin", Bytes(10, 9));
        var orphan = Guid.NewGuid();
        await Blobs.WriteAsync($"{Db}/{orphan}", new MemoryStream([1, 2, 3]), default);
        var now = DateTime.UtcNow;

        await Files.SweepAsync(now, default);
        (await BlobAsync(orphan)).Should().NotBeNull("an orphan is deleted 24 hours after the sweep first saw it");
        (await StagedKeysAsync(session)).Should().HaveCount(1, "the session was used within 24 hours");

        await Files.SweepAsync(now.AddHours(25), default);
        (await BlobAsync(orphan)).Should().BeNull();
        (await BlobAsync(kept)).Should().NotBeNull("a blob with a fileattachment row is never an orphan");
        (await StagedKeysAsync(session)).Should().BeEmpty();
        (await ScalarAsync("SELECT COUNT(*) FROM meta.filesession WHERE id = @id", ("@id", session))).Should().Be(0);
        await FaultAsync(() => UploadBlockAsync(Admin, token, BlockId(1), [1]), ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Upload_requires_write_on_record()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var (_, reader) = await UserAsync(table, DepthMask.Global, "Read");

        await FaultAsync(() => InitializeAsync(reader, table, id), ErrorCodes.PrivilegeDenied);

        var fileId = await UploadAsync(table, id, "a.txt", [1]);
        await FaultAsync(() => reader.ExecuteAsync(new DeleteFileRequest { FileId = fileId }), ErrorCodes.PrivilegeDenied);
        (await BlobAsync(fileId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Upload_messages_refuse_columns_that_are_neither_file_nor_image()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });

        (await FaultAsync(() => InitializeAsync(Admin, table, id, "a.txt", "new_name"), ErrorCodes.InvalidArgument))
            .Message.Should().Contain("is not a file or image column");
    }

    [Fact]
    public async Task fileattachment_read_follows_the_owning_record()
    {
        var table = await NewTableAsync();
        var (userId, user) = await UserAsync(table, DepthMask.Basic, "Read", "Write", "Create");
        var own = await user.CreateAsync(new Entity(table) { ["new_name"] = "mine" });
        var others = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "not mine" });
        var token = await InitializeAsync(user, table, own, "mine.txt");
        await UploadBlockAsync(user, token, BlockId(0), [1, 2]);
        var mine = (await CommitAsync(user, token, [BlockId(0)], "mine.txt")).FileId;
        var notMine = await UploadAsync(table, others, "secret.txt", [3]);

        (await user.RetrieveAsync("fileattachment", mine, new ColumnSet("filename")))["filename"].Should().Be("mine.txt");
        await FaultAsync(() => user.RetrieveAsync("fileattachment", notMine, new ColumnSet("filename")), ErrorCodes.AccessDenied);
        var listed = await user.RetrieveMultipleAsync(new QueryExpression("fileattachment") { ColumnSet = new ColumnSet("filename") });
        listed.Entities.Select(e => e.Id).Should().Contain(mine).And.NotContain(notMine);

        // Shared for Read: the file follows.
        await Admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = new EntityReference(table, others),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", userId), AccessMask = AccessRights.ReadAccess },
        });
        (await user.RetrieveAsync("fileattachment", notMine, new ColumnSet("filename")))["filename"].Should().Be("secret.txt");

        // No read privilege on the table: no file of it either.
        var (_, stranger) = await UserAsync(table, DepthMask.Global, "Write");
        (await stranger.RetrieveMultipleAsync(new QueryExpression("fileattachment") { ColumnSet = new ColumnSet("filename") }))
            .Entities.Select(e => e.Id).Should().NotContain([mine, notMine]);
    }

    // ---- replace, delete and update ---------------------------------------------------------------------------------

    [Fact]
    public async Task Recommit_replaces_previous_file_and_deletes_old_blob()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var first = await UploadAsync(table, id, "v1.txt", Bytes(10, 1));
        var second = await UploadAsync(table, id, "v2.txt", Bytes(20, 2));

        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Doc, Doc + "_name"));
        record[Doc].Should().Be(second);
        record[Doc + "_name"].Should().Be("v2.txt");
        (await BlobAsync(first)).Should().BeNull();
        (await BlobAsync(second)).Should().Equal(Bytes(20, 2));
        (await AttachmentRowsAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task DeleteFile_clears_column_and_blob()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var fileId = await UploadAsync(table, id, "gone.txt", Bytes(10, 1));

        await Admin.ExecuteAsync(new DeleteFileRequest { FileId = fileId });

        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Doc, Doc + "_name"));
        record.Contains(Doc).Should().BeFalse();
        record.Contains(Doc + "_name").Should().BeFalse();
        (await BlobAsync(fileId)).Should().BeNull();
        (await AttachmentRowsAsync(id)).Should().Be(0);
        await FaultAsync(() => Admin.ExecuteAsync(new DeleteFileRequest { FileId = fileId }), ErrorCodes.ObjectDoesNotExist);
    }

    [Fact]
    public async Task Update_setting_file_column_to_value_is_refused_and_to_null_deletes()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var fileId = await UploadAsync(table, id, "x.txt", Bytes(10, 1));

        (await FaultAsync(() => Admin.UpdateAsync(new Entity(table, id) { [Doc] = Guid.NewGuid() }), ErrorCodes.InvalidArgument))
            .Message.Should().Be($"File column {Doc} can only be set with the file upload messages.");
        await FaultAsync(() => Admin.CreateAsync(new Entity(table) { [Doc] = fileId }), ErrorCodes.InvalidArgument);
        (await BlobAsync(fileId)).Should().NotBeNull();

        await Admin.UpdateAsync(new Entity(table, id) { [Doc] = null });

        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Doc, Doc + "_name"));
        record.Contains(Doc).Should().BeFalse();
        record.Contains(Doc + "_name").Should().BeFalse();
        (await BlobAsync(fileId)).Should().BeNull();
        (await AttachmentRowsAsync(id)).Should().Be(0);
    }

    // ---- cascades ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Record_delete_removes_files_after_commit()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var fileId = await UploadAsync(table, id, "x.txt", Bytes(10, 1));

        await Admin.DeleteAsync(table, id);

        (await AttachmentRowsAsync(id)).Should().Be(0);
        (await BlobAsync(fileId)).Should().BeNull();
    }

    [Fact]
    public async Task Column_drop_removes_its_files()
    {
        var table = await NewTableAsync();
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = table, Attribute = new FileAttributeMetadata { LogicalName = "new_other", SchemaName = "new_other" },
        });
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var dropped = await UploadAsync(table, id, "x.txt", Bytes(10, 1));
        var token = await InitializeAsync(Admin, table, id, "y.txt", "new_other");
        await UploadBlockAsync(Admin, token, BlockId(0), [7]);
        var kept = (await CommitAsync(Admin, token, [BlockId(0)], "y.txt")).FileId;

        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = Doc });

        (await BlobAsync(dropped)).Should().BeNull();
        (await ScalarAsync("SELECT COUNT(*) FROM data.fileattachment WHERE fileattachmentid = @id", ("@id", dropped))).Should().Be(0);
        (await BlobAsync(kept)).Should().NotBeNull();
        (await AttachmentRowsAsync(id)).Should().Be(1);
    }

    /// <summary>A store whose deletions wait until <see cref="Release"/> (a slow or unreachable store).</summary>
    private sealed class GatedFileStore(IFileStore inner) : IFileStore
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deletes;

        public int Deletes => Volatile.Read(ref _deletes);
        public void Release() => _open.TrySetResult();
        public Task WriteAsync(string key, Stream content, CancellationToken ct) => inner.WriteAsync(key, content, ct);
        public Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct) => inner.OpenReadAsync(key, offset, length, ct);
        public Task<long?> GetLengthAsync(string key, CancellationToken ct) => inner.GetLengthAsync(key, ct);

        public async Task DeleteAsync(string key, CancellationToken ct)
        {
            await _open.Task.WaitAsync(ct);
            await inner.DeleteAsync(key, ct);
            Interlocked.Increment(ref _deletes);
        }

        public async Task DeletePrefixAsync(string prefix, CancellationToken ct)
        {
            await _open.Task.WaitAsync(ct);
            await inner.DeletePrefixAsync(prefix, ct);
        }

        public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);
        public Task ConcatAsync(IReadOnlyList<string> sourceKeys, string targetKey, CancellationToken ct) => inner.ConcatAsync(sourceKeys, targetKey, ct);
    }

    [Fact]
    public async Task Column_drop_hands_its_blob_deletions_to_the_background_queue()
    {
        var table = await NewTableAsync();
        var files = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "r" + i });
            files.Add(await UploadAsync(table, id, $"f{i}.txt", Bytes(10, (byte)i)));
        }
        var gate = new GatedFileStore(Blobs);
        var queue = new FileDeletionQueue();
        var service = new FileColumnService(gate, Store, fx.App.Services.GetRequiredService<IDbConnectionFactory>(), deletions: queue);
        var entity = Store.GetEntity(table)!;

        // The cascade of the dropped column (what DeleteAttribute runs in its transaction), then the commit's callbacks.
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var session = new DataSession(conn, await conn.BeginTransactionAsync());
        await service.OnAttributeDeletingAsync(entity, entity.Attributes.Single(a => a.LogicalName == Doc), session, default);
        await session.Transaction!.CommitAsync();
        var committed = Task.Run(session.Committed);
        (await Task.WhenAny(committed, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(committed,
            "the commit hands the deletions to the queue instead of waiting for the store");
        await committed;
        queue.Pending.Should().Be(files.Count);
        gate.Deletes.Should().Be(0);
        foreach (var file in files) (await Blobs.GetLengthAsync($"{Db}/{file}", default)).Should().NotBeNull();

        // The background drains the queue once the store answers.
        using var stop = new CancellationTokenSource();
        var drain = service.ProcessDeletionsAsync(stop.Token);
        gate.Release();
        await queue.WhenIdleAsync(TimeSpan.FromSeconds(30));
        gate.Deletes.Should().Be(files.Count);
        foreach (var file in files) (await Blobs.GetLengthAsync($"{Db}/{file}", default)).Should().BeNull();
        await stop.CancelAsync();
        await drain;
    }

    [Fact]
    public async Task Table_delete_removes_its_files()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var fileId = await UploadAsync(table, id, "x.txt", Bytes(10, 1));

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });

        (await BlobAsync(fileId)).Should().BeNull();
        (await AttachmentRowsAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task Solution_cascade_delete_removes_the_files_of_its_tables()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var solution = "new_files" + run;
        await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = solution, ["friendlyname"] = solution, ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", SystemSeed.DefaultPublisherId),
        });
        var table = "new_fc" + run;
        static Label Label(string text) => new(text, 1033);
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = table, SchemaName = table, DisplayName = Label(table), DisplayCollectionName = Label(table + "s"),
                OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
            SolutionUniqueName = solution,
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = table, Attribute = new FileAttributeMetadata { LogicalName = Doc, SchemaName = Doc }, SolutionUniqueName = solution,
        });
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var fileId = await UploadAsync(table, id, "x.txt", Bytes(10, 1));

        var res = await fx.Client.SendAsync(WebApi(HttpMethod.Delete, $"/admin/solutions/{solution}?cascade=true"));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Store.GetEntity(table).Should().BeNull();
        (await BlobAsync(fileId)).Should().BeNull();
        (await AttachmentRowsAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task Plugin_failure_during_commit_rolls_back_and_keeps_old_file()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var old = await UploadAsync(table, id, "old.txt", Bytes(10, 1));
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", table, stage: 40, config: "invalid:no new file");
        await fx.DrainFileDeletionsAsync();
        var blobsBefore = new List<string>();
        await foreach (var key in Blobs.ListAsync($"{Db}/", default)) if (!key.Contains("/_upload/")) blobsBefore.Add(key);

        var token = await InitializeAsync(Admin, table, id, "new.txt");
        await UploadBlockAsync(Admin, token, BlockId(0), Bytes(20, 2));
        (await FaultAsync(() => CommitAsync(Admin, token, [BlockId(0)], "new.txt"), ErrorCodes.ISVAborted)).Message.Should().Be("no new file");

        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Doc, Doc + "_name"));
        record[Doc].Should().Be(old);
        record[Doc + "_name"].Should().Be("old.txt");
        (await BlobAsync(old)).Should().Equal(Bytes(10, 1));
        (await AttachmentRowsAsync(id)).Should().Be(1);
        await fx.DrainFileDeletionsAsync();
        var blobsAfter = new List<string>();
        await foreach (var key in Blobs.ListAsync($"{Db}/", default)) if (!key.Contains("/_upload/")) blobsAfter.Add(key);
        blobsAfter.Should().BeEquivalentTo(blobsBefore, "the blob of the rolled-back commit is deleted");
    }

    // ---- review fixes: in-flight staging, required parameters, key cache, lost Write, single upload, context ----------

    private async Task<Caller> AdminCallerAsync() =>
        new((Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid",
            ("@oid", fx.AdminObjectId)))!, await RootBusinessUnitAsync(fx));

    /// <summary>The record Update of a commit through the pipeline, in <paramref name="session"/>'s transaction.</summary>
    private RecordUpdate PipelineUpdate(Caller caller, DataSession session) => async (target, ct) =>
    {
        using var scope = fx.App.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PipelineExecutor>().ExecuteAsync(new UpdateRequest { Target = target },
            new InvocationContext(caller, caller.UserId, 1, null, session, Guid.NewGuid()), ct);
    };

    [Fact]
    public async Task Sweep_keeps_the_staging_of_an_upload_in_progress_in_another_transaction()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var caller = await AdminCallerAsync();
        var abandoned = Guid.NewGuid();
        await Blobs.WriteAsync($"{Db}/_upload/{abandoned}/0", new MemoryStream([7]), default);
        var now = DateTime.UtcNow;

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var session = new DataSession(conn, await conn.BeginTransactionAsync());
        var token = await Files.InitializeUploadAsync(caller, new EntityReference(table, id), Doc, "inflight.bin", session, default);
        await Files.UploadBlockAsync(caller, token, BlockId(0), new MemoryStream(Bytes(50, 3)), session, default);

        // The session row is not committed: the sweep cannot see it, and must not take its blocks for abandoned ones.
        await Files.SweepAsync(now, default);
        (await StagedKeysAsync(abandoned)).Should().HaveCount(1, "staging without a visible session is kept for 24 hours");
        Guid inFlight;
        await using (var cmd = session.Command("SELECT id FROM meta.filesession WHERE record_id = @id"))
        {
            cmd.AddParameter("@id", id);
            inFlight = (Guid)(await cmd.ExecuteScalarAsync())!;
        }
        (await StagedKeysAsync(inFlight)).Should().HaveCount(1, "the blocks of the upload in progress are kept");

        var (fileId, size) = await Files.CommitAsync(caller, token, [BlockId(0)], "inflight.bin", "application/octet-stream", session,
            PipelineUpdate(caller, session), default);
        await session.Transaction!.CommitAsync();
        session.Committed();

        size.Should().Be(50);
        (await BlobAsync(fileId)).Should().Equal(Bytes(50, 3));
        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc)))[Doc].Should().Be(fileId);

        await Files.SweepAsync(now.AddHours(25), default);
        (await StagedKeysAsync(abandoned)).Should().BeEmpty("staging still without a session after 24 hours is deleted");
    }

    [Fact]
    public async Task Commit_requires_FileName_and_MimeType()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);
        await UploadBlockAsync(Admin, token, BlockId(0), [1, 2]);

        (await FaultAsync(() => CommitAsync(Admin, token, [BlockId(0)], mimeType: ""), ErrorCodes.InvalidArgument))
            .Message.Should().Contain("'MimeType'");
        (await FaultAsync(() => Admin.ExecuteAsync(new CommitFileBlocksUploadRequest
            {
                FileContinuationToken = token, BlockList = [BlockId(0)], FileName = "report.pdf",
            }), ErrorCodes.InvalidArgument)).Message.Should().Contain("'MimeType'");
        (await FaultAsync(() => CommitAsync(Admin, token, [BlockId(0)], fileName: ""), ErrorCodes.InvalidArgument))
            .Message.Should().Contain("'FileName'");
        (await FaultAsync(() => Admin.ExecuteAsync(new CommitFileBlocksUploadRequest
            {
                FileContinuationToken = token, BlockList = [BlockId(0)], MimeType = "application/pdf",
            }), ErrorCodes.InvalidArgument)).Message.Should().Contain("'FileName'");

        var committed = await CommitAsync(Admin, token, [BlockId(0)]);
        (await ScalarAsync("SELECT mimetype FROM data.fileattachment WHERE fileattachmentid = @id", ("@id", committed.FileId)))
            .Should().Be("application/pdf");
    }

    [Fact]
    public async Task Token_key_cache_is_forgotten_for_a_replaced_database()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = await InitializeAsync(Admin, table, id);
        var original = (byte[])(await ScalarAsync("SELECT TOP 1 secret FROM meta.filesecret"))!;
        try
        {
            await ScalarAsync("UPDATE meta.filesecret SET secret = @secret", ("@secret", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            await UploadBlockAsync(Admin, token, BlockId(0), [1]); // the cached key still opens the session

            Files.ForgetDatabase(Db).Should().BeTrue();
            await FaultAsync(() => UploadBlockAsync(Admin, token, BlockId(1), [2]), ErrorCodes.InvalidArgument);
        }
        finally
        {
            await ScalarAsync("UPDATE meta.filesecret SET secret = @secret", ("@secret", original));
            Files.ForgetDatabase(Db);
        }
        Files.ForgetDatabase("not_cached").Should().BeFalse();
        await UploadBlockAsync(Admin, token, BlockId(1), [2]);
    }

    [Fact]
    public async Task Commit_is_refused_when_the_caller_lost_write_after_initialize()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var (user, service) = await UserAsync(table, DepthMask.Global, "Read", "Write");
        var token = await InitializeAsync(service, table, id);
        await UploadBlockAsync(service, token, BlockId(0), [1, 2, 3]);

        await ScalarAsync("DELETE FROM data.systemuserroles WHERE systemuserid = @user", ("@user", user));
        await GrantAsync(fx, user, team: false, ($"prvRead{Store.GetEntity(table)!.SchemaName}", DepthMask.Global));

        await FaultAsync(() => CommitAsync(service, token, [BlockId(0)]), ErrorCodes.PrivilegeDenied);
        (await Admin.RetrieveAsync(table, id, new ColumnSet(Doc))).GetAttributeValue<Guid?>(Doc).Should().BeNull();
        (await AttachmentRowsAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task Commit_update_runs_nested_under_the_commit_context()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", table, stage: 40, config: TestEntities.PipeLog);

        await UploadAsync(table, id, "a.txt", [1]);

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{TestEntities.PipeLog}");
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<(string Name, string Data)>();
        while (await rd.ReadAsync()) rows.Add((rd.GetString(0), rd.GetString(1)));
        rows.Should().ContainSingle();
        rows[0].Name.Should().Be("Update|40|0|2", "the Update runs one level below the commit message");
        rows[0].Data.Split('\n').Should().Contain("parent=CommitFileBlocksUpload");
    }
}
