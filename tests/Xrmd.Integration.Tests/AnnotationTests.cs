using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.11, task 5: note bodies (<c>annotation.documentbody</c>, rulings A15 and A16, decision D3). A body is base64 on the wire
/// and never a SQL column: it is a <c>fileattachment</c> row (<c>regardingfieldname</c> <c>documentbody</c>) and a blob of the file
/// store, written and released with the transaction rules of a file column commit, so the 2.2 sweep, deletion queue and backups
/// cover it (RF2). A body is read only through its note (the note's read security; its <c>fileattachment</c> row follows the
/// note).
/// </summary>
[Collection("xrmd")]
public sealed class AnnotationTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const int FileTooBig = unchecked((int)0x80044a02);
    private const string Body = "documentbody";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private IFileStore Blobs => fx.App.Services.GetRequiredService<IFileStore>();
    private FileColumnService Files => fx.App.Services.GetRequiredService<FileColumnService>();
    private static string Db => "xrmverse_test";

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync("UPDATE data.organization SET maxuploadfilesize = 5242880");
    }

    // ---- helpers ----

    private string Tag(string label) => $"{label} {_run}";

    private static byte[] Bytes(int length, byte seed) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed))];

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);

    private async Task<EntityReference> AccountAsync() =>
        new("account", await Admin.CreateAsync(new Entity("account") { ["name"] = Tag("Contoso Example") }));

    private Entity Note(EntityReference regarding, byte[]? body = null, string? fileName = "plan.txt", Guid? owner = null)
    {
        var note = new Entity("annotation") { ["subject"] = Tag("Note"), ["objectid"] = regarding };
        if (fileName is not null) note["filename"] = fileName;
        if (body is not null) note[Body] = Base64(body);
        if (owner is { } o) note["ownerid"] = new EntityReference("systemuser", o);
        return note;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    /// <summary>The <c>fileattachment</c> rows of the note's body: id, regardingfieldname, filename, mimetype, size.</summary>
    private async Task<List<(Guid Id, string Field, string? FileName, string? MimeType, long Size)>> BodyRowsAsync(Guid note)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn,
            "SELECT fileattachmentid, regardingfieldname, filename, mimetype, filesizeinbytes FROM data.fileattachment WHERE objectid = @id");
        cmd.Parameters.AddWithValue("@id", note);
        var rows = new List<(Guid, string, string?, string?, long)>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            rows.Add((rd.GetGuid(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetInt64(4)));
        return rows;
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

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act, int code)
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(code, fault.Message);
        return fault;
    }

    private async Task<(Guid Id, OrganizationService Service)> UserAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var oid = Guid.NewGuid();
        var id = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        if (privileges.Length > 0) await GrantAsync(fx, id, team: false, privileges);
        return (id, ServiceFor(fx, oid));
    }

    /// <summary>An organization service whose file service uses <paramref name="store"/> and the read cap <paramref name="cap"/>.</summary>
    private OrganizationService ServiceWith(IFileStore store, long? cap = null)
    {
        var options = new FileStoreOptions();
        if (cap is { } c) options.MaxNoteBodyBytesPerRead = c;
        var files = new FileColumnService(store, fx.App.Services.GetRequiredService<IMetadataStore>(),
            fx.App.Services.GetRequiredService<IDbConnectionFactory>(), deletions: fx.App.Services.GetRequiredService<FileDeletionQueue>(),
            options: options);
        return TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId, files: files);
    }

    private async Task<HttpResponseMessage> GetAsync(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    // ---- fix round 1 ----

    [Fact]
    public async Task Reading_more_note_bodies_than_the_cap_is_400()
    {
        var account = await AccountAsync();
        var first = await Admin.CreateAsync(Note(account, Bytes(6, 1)));
        await Admin.CreateAsync(Note(account, Bytes(6, 2)));
        var spy = new SpyFileStore(Blobs);
        var service = ServiceWith(spy, cap: 10);

        // One body under the cap is read.
        (await service.RetrieveAsync("annotation", first, new ColumnSet(Body))).GetAttributeValue<string>(Body).Should().Be(Base64(Bytes(6, 1)));
        var reads = spy.Reads;
        var query = new QueryExpression("annotation") { ColumnSet = new ColumnSet("subject", Body) };
        query.Criteria.AddCondition("objectid", ConditionOperator.Equal, account.Id);

        var fault = await FaultAsync(() => service.RetrieveMultipleAsync(query), ErrorCodes.InvalidArgument);

        fault.Message.Should().Contain("12").And.Contain("10").And.Contain("ColumnSet").And.Contain("$select");
        spy.Reads.Should().Be(reads, "the cap is checked before any blob is read");
        // Without the body, the same page is read.
        query.ColumnSet = new ColumnSet("subject");
        (await service.RetrieveMultipleAsync(query)).Entities.Should().HaveCount(2);
    }

    [Fact]
    public async Task Link_entity_all_attributes_read_the_note_body()
    {
        var account = await AccountAsync();
        var note = await Admin.CreateAsync(Note(account, Bytes(5, 3)));
        var fetch = $"""
            <fetch>
              <entity name="account">
                <attribute name="name" />
                <filter><condition attribute="accountid" operator="eq" value="{account.Id}" /></filter>
                <link-entity name="annotation" from="objectid" to="accountid" link-type="outer" alias="n">
                  <all-attributes />
                </link-entity>
                <link-entity name="annotation" from="objectid" to="accountid" link-type="outer">
                  <all-attributes />
                </link-entity>
              </entity>
            </fetch>
            """;

        var row = (await Admin.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities.Should().ContainSingle().Subject;

        row.GetAttributeValue<AliasedValue>("n.annotationid").Value.Should().Be(note);
        row.GetAttributeValue<AliasedValue>("n." + Body).Value.Should().Be(Base64(Bytes(5, 3)));
        row.GetAttributeValue<AliasedValue>("annotation1." + Body).Value.Should().Be(Base64(Bytes(5, 3)), "an unaliased link has the compiler's alias");
    }

    [Fact]
    public async Task Web_api_expand_of_a_note_reads_its_body()
    {
        var account = await AccountAsync();
        var note = await Admin.CreateAsync(Note(account, Bytes(5, 4)));
        var attachment = (await BodyRowsAsync(note)).Single().Id;

        var res = await GetAsync($"fileattachments({attachment})?$select=filename&$expand=objectid_annotation");
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode.Parse(body)!["objectid_annotation"]![Body]!.GetValue<string>().Should().Be(Base64(Bytes(5, 4)));
    }

    [Fact]
    public async Task Vanished_blob_whose_row_is_gone_reads_no_body()
    {
        var account = await AccountAsync();
        var id = await Admin.CreateAsync(Note(account, Bytes(5, 1)));
        var spy = new SpyFileStore(Blobs) { VanishOnOpen = true };
        // Another transaction clears the body between this read's row and its blob (committed meanwhile).
        spy.BeforeOpen = async _ =>
        {
            spy.BeforeOpen = null;
            await Admin.UpdateAsync(new Entity("annotation", id) { [Body] = null });
        };

        var note = await ServiceWith(spy).RetrieveAsync("annotation", id, new ColumnSet(Body, "subject"));

        note.Contains(Body).Should().BeFalse("the row read again is gone: the note has no body any more");
        spy.Reads.Should().Be(1, "no second open without a row");
    }

    [Fact]
    public async Task Vanished_blob_whose_row_stays_is_404_after_one_retry()
    {
        var account = await AccountAsync();
        var id = await Admin.CreateAsync(Note(account, Bytes(5, 1)));
        var spy = new SpyFileStore(Blobs) { VanishOnOpen = true };

        await FaultAsync(() => ServiceWith(spy).RetrieveAsync("annotation", id, new ColumnSet(Body)), ErrorCodes.ObjectDoesNotExist);

        spy.Reads.Should().Be(2, "the body row is read again and its blob opened once more");
    }

    [Fact]
    public async Task Oversized_body_is_refused_before_it_is_decoded()
    {
        await ExecAsync("UPDATE data.organization SET maxuploadfilesize = 10");
        var account = await AccountAsync();

        // 24 base64 characters decode to 18 bytes: the size is refused from the length, even though the text is not base64.
        await FaultAsync(() => Admin.CreateAsync(new Entity("annotation") { ["objectid"] = account, [Body] = new string('!', 24) }), FileTooBig);
    }

    [Fact]
    public async Task Client_isdocument_is_ignored()
    {
        var account = await AccountAsync();
        var id = await Admin.CreateAsync(new Entity("annotation") { ["objectid"] = account, ["subject"] = Tag("Flag"), ["isdocument"] = true });
        (await Admin.RetrieveAsync("annotation", id, new ColumnSet("isdocument"))).GetAttributeValue<bool>("isdocument").Should().BeFalse();

        await Admin.UpdateAsync(new Entity("annotation", id) { ["isdocument"] = true });
        (await Admin.RetrieveAsync("annotation", id, new ColumnSet("isdocument"))).GetAttributeValue<bool>("isdocument").Should().BeFalse();

        await Admin.UpdateAsync(new Entity("annotation", id) { [Body] = Base64(Bytes(3, 1)), ["isdocument"] = false });
        (await Admin.RetrieveAsync("annotation", id, new ColumnSet("isdocument"))).GetAttributeValue<bool>("isdocument").Should().BeTrue();
    }

    [Fact]
    public async Task Rolled_back_update_keeps_old_body_and_deletes_new_blob()
    {
        var account = await AccountAsync();
        var id = await Admin.CreateAsync(Note(account, Bytes(8, 1)));
        var old = (await BodyRowsAsync(id)).Single().Id;
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", "annotation", stage: 40, config: "invalid:no new body");
        var spy = new SpyFileStore(Blobs);

        (await FaultAsync(() => ServiceWith(spy).UpdateAsync(new Entity("annotation", id) { [Body] = Base64(Bytes(9, 2)) }), ErrorCodes.ISVAborted))
            .Message.Should().Be("no new body");

        await PluginTestHelper.ClearAsync(fx);
        spy.Written.Should().ContainSingle();
        await fx.DrainFileDeletionsAsync();
        (await Blobs.GetLengthAsync(spy.Written.Single(), default)).Should().BeNull("the rolled-back body's blob is deleted");
        (await BodyRowsAsync(id)).Should().ContainSingle().Which.Id.Should().Be(old);
        (await BlobAsync(old)).Should().Equal(Bytes(8, 1));
        (await Admin.RetrieveAsync("annotation", id, new ColumnSet(Body))).GetAttributeValue<string>(Body).Should().Be(Base64(Bytes(8, 1)));
    }

    [Fact]
    public async Task Renaming_note_renames_its_body_row()
    {
        var account = await AccountAsync();
        var id = await Admin.CreateAsync(Note(account, Bytes(4, 1)));

        await Admin.UpdateAsync(new Entity("annotation", id) { ["filename"] = "renamed.csv", ["mimetype"] = "text/csv" });

        var row = (await BodyRowsAsync(id)).Should().ContainSingle().Subject;
        (row.FileName, row.MimeType, row.Size).Should().Be(("renamed.csv", "text/csv", 4L));
    }

    // ---- create, read, limits ----

    [Fact]
    public async Task Note_with_body_on_account()
    {
        var account = await AccountAsync();
        var content = Bytes(12, 3);

        var id = await Admin.CreateAsync(Note(account, content));

        var note = await Admin.RetrieveAsync("annotation", id, new ColumnSet("isdocument", "filesize", "mimetype", "filename", Body));
        note.GetAttributeValue<bool>("isdocument").Should().BeTrue();
        note.GetAttributeValue<int>("filesize").Should().Be(12);
        note.GetAttributeValue<string>("mimetype").Should().Be("text/plain");
        note.GetAttributeValue<string>(Body).Should().Be(Base64(content));
        (await ScalarAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'data' AND table_name = 'annotation' AND column_name = 'documentbody'"))
            .Should().Be(0, "a note body is never a SQL column (D3)");
        var row = (await BodyRowsAsync(id)).Should().ContainSingle().Subject;
        row.Field.Should().Be(Body);
        (row.FileName, row.MimeType, row.Size).Should().Be(("plan.txt", "text/plain", 12L));
        (await BlobAsync(row.Id)).Should().Equal(content);

        // AllColumns and RetrieveMultiple return the body too (A16).
        (await Admin.RetrieveAsync("annotation", id, new ColumnSet(true))).GetAttributeValue<string>(Body).Should().Be(Base64(content));
        var query = new QueryExpression("annotation") { ColumnSet = new ColumnSet("subject", Body) };
        query.Criteria.AddCondition("objectid", ConditionOperator.Equal, account.Id);
        (await Admin.RetrieveMultipleAsync(query)).Entities.Should().ContainSingle().Which.GetAttributeValue<string>(Body).Should().Be(Base64(content));
    }

    [Fact]
    public async Task Note_without_body_has_no_attachment()
    {
        var account = await AccountAsync();

        var id = await Admin.CreateAsync(Note(account, fileName: null));

        var note = await Admin.RetrieveAsync("annotation", id, new ColumnSet(true));
        note.GetAttributeValue<bool>("isdocument").Should().BeFalse();
        note.Contains(Body).Should().BeFalse();
        (await BodyRowsAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Body_over_maxuploadfilesize_gives_0x80044a02()
    {
        await ExecAsync("UPDATE data.organization SET maxuploadfilesize = 10");
        var account = await AccountAsync();

        await FaultAsync(() => Admin.CreateAsync(Note(account, Bytes(11, 1))), FileTooBig);
        var id = await Admin.CreateAsync(Note(account, Bytes(10, 1)));
        await FaultAsync(() => Admin.UpdateAsync(new Entity("annotation", id) { [Body] = Base64(Bytes(11, 2)) }), FileTooBig);

        (await BodyRowsAsync(id)).Should().ContainSingle().Which.Size.Should().Be(10);
    }

    [Fact]
    public async Task Invalid_base64_is_400()
    {
        var account = await AccountAsync();

        var fault = await FaultAsync(() => Admin.CreateAsync(new Entity("annotation") { ["objectid"] = account, [Body] = "not base64!" }),
            ErrorCodes.InvalidArgument);

        fault.Message.Should().Contain(Body);
    }

    [Fact]
    public async Task Body_cannot_be_filtered_or_sorted()
    {
        var filtered = new QueryExpression("annotation") { ColumnSet = new ColumnSet("subject") };
        filtered.Criteria.AddCondition(Body, ConditionOperator.NotNull);
        var sorted = new QueryExpression("annotation") { ColumnSet = new ColumnSet("subject") };
        sorted.AddOrder(Body, OrderType.Ascending);

        (await FaultAsync(() => Admin.RetrieveMultipleAsync(filtered), ErrorCodes.InvalidArgument)).Message.Should().Contain(Body);
        (await FaultAsync(() => Admin.RetrieveMultipleAsync(sorted), ErrorCodes.InvalidArgument)).Message.Should().Contain(Body);
    }

    // ---- update, clear, delete ----

    [Fact]
    public async Task Update_replaces_body_and_deletes_old_blob_after_commit()
    {
        var account = await AccountAsync();
        var id = await Admin.CreateAsync(Note(account, Bytes(12, 1)));
        var old = (await BodyRowsAsync(id)).Single().Id;

        await Admin.UpdateAsync(new Entity("annotation", id) { [Body] = Base64(Bytes(20, 2)), ["filename"] = "plan.pdf" });

        var row = (await BodyRowsAsync(id)).Should().ContainSingle().Subject;
        row.Id.Should().NotBe(old);
        (row.FileName, row.MimeType, row.Size).Should().Be(("plan.pdf", "application/pdf", 20L));
        (await BlobAsync(row.Id)).Should().Equal(Bytes(20, 2));
        (await BlobAsync(old)).Should().BeNull("the replaced body's blob is deleted after the commit");
        var note = await Admin.RetrieveAsync("annotation", id, new ColumnSet("filesize", "mimetype", Body));
        note.GetAttributeValue<int>("filesize").Should().Be(20);
        note.GetAttributeValue<string>("mimetype").Should().Be("application/pdf");
        note.GetAttributeValue<string>(Body).Should().Be(Base64(Bytes(20, 2)));
    }

    [Fact]
    public async Task Clearing_body_resets_filesize_and_isdocument()
    {
        var account = await AccountAsync();
        var cleared = await Admin.CreateAsync(Note(account, Bytes(12, 1)));
        var emptied = await Admin.CreateAsync(Note(account, Bytes(12, 1)));
        var blobs = new[] { (await BodyRowsAsync(cleared)).Single().Id, (await BodyRowsAsync(emptied)).Single().Id };

        await Admin.UpdateAsync(new Entity("annotation", cleared) { [Body] = null });
        await Admin.UpdateAsync(new Entity("annotation", emptied) { [Body] = "" });

        foreach (var id in new[] { cleared, emptied })
        {
            var note = await Admin.RetrieveAsync("annotation", id, new ColumnSet("filesize", "isdocument", Body));
            note.GetAttributeValue<bool>("isdocument").Should().BeFalse();
            note.GetAttributeValue<int?>("filesize").Should().Be(0);
            note.Contains(Body).Should().BeFalse();
            (await BodyRowsAsync(id)).Should().BeEmpty();
        }
        foreach (var blob in blobs) (await BlobAsync(blob)).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_note_deletes_blob()
    {
        var account = await AccountAsync();
        var id = await Admin.CreateAsync(Note(account, Bytes(12, 1)));
        var blob = (await BodyRowsAsync(id)).Single().Id;

        await Admin.DeleteAsync("annotation", id);

        (await BodyRowsAsync(id)).Should().BeEmpty();
        (await BlobAsync(blob)).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_account_deletes_its_notes_and_blobs()
    {
        var account = await AccountAsync();
        var first = await Admin.CreateAsync(Note(account, Bytes(12, 1)));
        var second = await Admin.CreateAsync(Note(account, Bytes(5, 2), "b.bin"));
        var blobs = new[] { (await BodyRowsAsync(first)).Single().Id, (await BodyRowsAsync(second)).Single().Id };

        await Admin.DeleteAsync("account", account.Id);

        (await ScalarAsync("SELECT COUNT(*) FROM data.annotation WHERE objectid = @id", ("@id", account.Id))).Should().Be(0);
        foreach (var blob in blobs)
        {
            (await ScalarAsync("SELECT COUNT(*) FROM data.fileattachment WHERE fileattachmentid = @id", ("@id", blob))).Should().Be(0);
            (await BlobAsync(blob)).Should().BeNull();
        }
    }

    [Fact]
    public async Task Note_body_is_not_a_file_column()
    {
        var account = await AccountAsync();
        var id = await Admin.CreateAsync(Note(account, Bytes(12, 1)));
        var blob = (await BodyRowsAsync(id)).Single().Id;

        // The file messages do not reach a note body: it is written and cleared through the note (A16, A18).
        await FaultAsync(() => Admin.ExecuteAsync(new InitializeFileBlocksDownloadRequest
        {
            Target = new EntityReference("annotation", id), FileAttributeName = Body,
        }), ErrorCodes.InvalidArgument);
        (await FaultAsync(() => Admin.ExecuteAsync(new DeleteFileRequest { FileId = blob }), ErrorCodes.InvalidArgument))
            .Message.Should().Contain(Body);
        (await BlobAsync(blob)).Should().NotBeNull();
    }

    // ---- RF2: the sweep and a rolled-back create ----

    [Fact]
    public async Task Sweep_keeps_note_bodies()
    {
        var account = await AccountAsync();
        var id = await Admin.CreateAsync(Note(account, Bytes(12, 1)));
        var blob = (await BodyRowsAsync(id)).Single().Id;
        var now = DateTime.UtcNow;

        await Files.SweepAsync(now, default);
        await Files.SweepAsync(now.AddHours(25), default);

        (await BlobAsync(blob)).Should().Equal(Bytes(12, 1), "a note body has a fileattachment row: it is never an orphan");
        (await Admin.RetrieveAsync("annotation", id, new ColumnSet(Body))).GetAttributeValue<string>(Body).Should().Be(Base64(Bytes(12, 1)));
    }

    [Fact]
    public async Task Rolled_back_note_create_leaves_no_blob()
    {
        var account = await AccountAsync();
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", "annotation", stage: 40, config: "invalid:no note");
        var spy = new SpyFileStore(Blobs);
        var files = new FileColumnService(spy, fx.App.Services.GetRequiredService<IMetadataStore>(),
            fx.App.Services.GetRequiredService<IDbConnectionFactory>(), deletions: fx.App.Services.GetRequiredService<FileDeletionQueue>());
        var service = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId, files: files);

        (await FaultAsync(() => service.CreateAsync(Note(account, Bytes(12, 1))), ErrorCodes.ISVAborted)).Message.Should().Be("no note");

        spy.Written.Should().ContainSingle("the body was written before the transaction ended");
        await fx.DrainFileDeletionsAsync();
        (await Blobs.GetLengthAsync(spy.Written.Single(), default)).Should().BeNull("the blob of a rolled-back create is deleted");
        (await ScalarAsync("SELECT COUNT(*) FROM data.annotation WHERE objectid = @id", ("@id", account.Id))).Should().Be(0);
    }

    // ---- targets ----

    [Fact]
    public void Annotation_is_a_permanent_target_of_fileattachment_objectid()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var lookup = store.GetEntity("fileattachment")!.Attributes.OfType<Microsoft.Xrm.Sdk.Metadata.LookupAttributeMetadata>()
            .Single(a => a.LogicalName == "objectid");

        lookup.Targets.Should().Contain("annotation");
        store.GetRelationship("annotation_FileAttachments").Should().NotBeNull("each target has its relationship, as a file column's table");
    }

    [Fact]
    public async Task Body_named_in_a_link_entity_is_read()
    {
        var account = await AccountAsync();
        await Admin.CreateAsync(Note(account, Bytes(3, 7)));
        var query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
        query.Criteria.AddCondition("accountid", ConditionOperator.Equal, account.Id);
        var link = query.AddLink("annotation", "accountid", "objectid");
        link.EntityAlias = "n";
        link.Columns = new ColumnSet("subject", Body);

        var row = (await Admin.RetrieveMultipleAsync(query)).Entities.Should().ContainSingle().Subject;

        row.GetAttributeValue<AliasedValue>("n." + Body).Value.Should().Be(Base64(Bytes(3, 7)));
        row.Contains("n.annotationid").Should().BeFalse("the key read for the body is not a column the query asked for");
        var aliased = """
            <fetch><entity name="account"><attribute name="name" />
              <link-entity name="annotation" from="objectid" to="accountid"><attribute name="documentbody" alias="b" /></link-entity>
            </entity></fetch>
            """;
        (await FaultAsync(() => Admin.RetrieveMultipleAsync(new FetchExpression(aliased)), ErrorCodes.InvalidArgument)).Message.Should().Contain(Body);
    }

    [Fact]
    public async Task RetrieveMultiple_reads_the_body_of_each_note()
    {
        var account = await AccountAsync();
        for (var i = 0; i < 3; i++) await Admin.CreateAsync(Note(account, Bytes(4 + i, (byte)i)));
        await Admin.CreateAsync(Note(account, fileName: null));

        var query = new QueryExpression("annotation") { ColumnSet = new ColumnSet(true) };
        query.Criteria.AddCondition("objectid", ConditionOperator.Equal, account.Id);
        query.AddOrder("filesize", OrderType.Ascending);
        var notes = (await Admin.RetrieveMultipleAsync(query)).Entities;

        notes.Select(n => n.GetAttributeValue<string>(Body)).Should().Equal(null, Base64(Bytes(4, 0)), Base64(Bytes(5, 1)), Base64(Bytes(6, 2)));
    }

    [Fact]
    public async Task Note_on_email_and_custom_activity()
    {
        var email = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Mail") });
        var name = $"new_nact{_run}";
        var entity = TestHelpers.Entity(name, name, "Note activity " + _run);
        entity.IsActivity = true;
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
            {
                LogicalName = "subject", SchemaName = "Subject", MaxLength = 200, DisplayName = TestHelpers.Label("Subject"),
            },
        });
        var custom = await Admin.CreateAsync(new Entity(name) { ["subject"] = Tag("Custom") });

        var onEmail = await Admin.CreateAsync(Note(new EntityReference("email", email), Bytes(3, 1)));
        var onCustom = await Admin.CreateAsync(Note(new EntityReference(name, custom), Bytes(4, 2)));

        (await Admin.RetrieveAsync("annotation", onEmail, new ColumnSet(Body, "objectid"))).GetAttributeValue<string>(Body).Should().Be(Base64(Bytes(3, 1)));
        var read = await Admin.RetrieveAsync("annotation", onCustom, new ColumnSet(Body, "objectid"));
        read.GetAttributeValue<EntityReference>("objectid").LogicalName.Should().Be(name);
        read.GetAttributeValue<string>(Body).Should().Be(Base64(Bytes(4, 2)));

        // Clean up: the custom activity (with notes it cannot be deleted, Task 2) and its table.
        await Admin.DeleteAsync("annotation", onCustom);
        await Admin.DeleteAsync(name, custom);
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });
    }

    [Fact]
    public async Task Note_on_table_without_HasNotes_is_400()
    {
        var users = await Admin.RetrieveMultipleAsync(new QueryExpression("systemuser") { ColumnSet = new ColumnSet("systemuserid"), TopCount = 1 });

        await FaultAsync(() => Admin.CreateAsync(Note(users.Entities[0].ToEntityReference(), Bytes(3, 1))), ErrorCodes.InvalidArgument);
    }

    // ---- security ----

    [Fact]
    public async Task Basic_user_reads_own_notes_only()
    {
        var (userId, user) = await UserAsync(("prvReadAccount", DepthMask.Global), ("prvReadNote", DepthMask.Basic));
        var account = await AccountAsync();
        var mine = await Admin.CreateAsync(Note(account, Bytes(6, 1), owner: userId));
        var other = await Admin.CreateAsync(Note(account, Bytes(7, 2)));
        var otherBlob = (await BodyRowsAsync(other)).Single().Id;

        var query = new QueryExpression("annotation") { ColumnSet = new ColumnSet(true) };
        query.Criteria.AddCondition("objectid", ConditionOperator.Equal, account.Id);
        var notes = (await user.RetrieveMultipleAsync(query)).Entities;

        notes.Should().ContainSingle().Which.Id.Should().Be(mine);
        notes[0].GetAttributeValue<string>(Body).Should().Be(Base64(Bytes(6, 1)));
        (await FaultAsync(() => user.RetrieveAsync("annotation", other, new ColumnSet(Body)), ErrorCodes.AccessDenied))
            .Message.Should().NotContain(Base64(Bytes(7, 2)));
        // The fileattachment row of a body follows its note: the other note's row is not readable either.
        var attachments = new QueryExpression("fileattachment") { ColumnSet = new ColumnSet("fileattachmentid", "regardingfieldname") };
        attachments.Criteria.AddCondition("objectid", ConditionOperator.In, mine, other);
        var rows = (await user.RetrieveMultipleAsync(attachments)).Entities;
        rows.Should().ContainSingle().Which.GetAttributeValue<string>("regardingfieldname").Should().Be(Body);
        rows[0].Id.Should().NotBe(otherBlob);
    }

    // ---- Web API ----

    [Fact]
    public async Task Post_annotation_with_objectid_bind_and_documentbody()
    {
        var account = await AccountAsync();
        var content = Bytes(9, 4);
        var json = new JsonObject
        {
            ["subject"] = Tag("Web note"),
            ["filename"] = "notes.txt",
            [Body] = Base64(content),
            ["objectid_account@odata.bind"] = $"/accounts({account.Id})",
        }.ToJsonString();

        var req = new HttpRequestMessage(HttpMethod.Post, Api + "annotations") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var url = res.Headers.GetValues("OData-EntityId").Single();
        var id = Guid.Parse(url[(url.LastIndexOf('(') + 1)..^1]);

        var get = new HttpRequestMessage(HttpMethod.Get, Api + $"annotations({id})?$select=documentbody,filesize,isdocument,mimetype");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var read = await fx.Client.SendAsync(get);
        var body = await read.Content.ReadAsStringAsync();
        read.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var note = JsonNode.Parse(body)!.AsObject();
        note[Body]!.GetValue<string>().Should().Be(Base64(content));
        note["filesize"]!.GetValue<int>().Should().Be(9);
        note["isdocument"]!.GetValue<bool>().Should().BeTrue();
        note["mimetype"]!.GetValue<string>().Should().Be("text/plain");

        var filter = new HttpRequestMessage(HttpMethod.Get, Api + "annotations?$select=subject&$filter=documentbody ne null");
        filter.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        (await fx.Client.SendAsync(filter)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
