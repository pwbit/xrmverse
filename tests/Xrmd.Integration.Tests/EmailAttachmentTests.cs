using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1b, task 4: <c>activitymimeattachment</c> with its body (<c>body</c>, base64 on the wire) in the file store, as a note's body
/// is (<see cref="AnnotationTests"/>). The body is a <c>fileattachment</c> row and a blob, never a column; the attachment follows its
/// email: read needs read on the email, create, update and delete need write on it; <c>email.attachmentcount</c> follows the rows.
/// </summary>
[Collection("xrmd")]
public sealed class EmailAttachmentTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const int FileTooBig = unchecked((int)0x80044a02);
    private const string Table = "activitymimeattachment";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private IFileStore Blobs => fx.App.Services.GetRequiredService<IFileStore>();
    private static string Db => "xrmverse_test";

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync("UPDATE data.organization SET maxuploadfilesize = 5242880, blockedattachments = '" + MailSchema.DefaultBlockedAttachments + "'");
    }

    // ---- helpers ----

    private static byte[] Bytes(int length, byte seed) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed))];

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);

    private async Task<EntityReference> EmailAsync(OrganizationService? service = null) =>
        new("email", await (service ?? Admin).CreateAsync(new Entity("email") { ["subject"] = "Attachments " + _run }));

    private static Entity Attachment(EntityReference email, byte[]? body, string? fileName = "report.txt", string? mimeType = null)
    {
        var attachment = new Entity(Table) { ["objectid"] = email, ["subject"] = "Attachment" };
        if (fileName is not null) attachment["filename"] = fileName;
        if (mimeType is not null) attachment["mimetype"] = mimeType;
        if (body is not null) attachment["body"] = Base64(body);
        return attachment;
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

    /// <summary>The <c>fileattachment</c> rows of the attachment's body: id, regardingfieldname, filename, mimetype, size.</summary>
    private async Task<List<(Guid Id, string Field, string? FileName, string? MimeType, long Size)>> BodyRowsAsync(Guid attachment)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn,
            "SELECT fileattachmentid, regardingfieldname, filename, mimetype, filesizeinbytes FROM data.fileattachment WHERE objectid = @id");
        cmd.Parameters.AddWithValue("@id", attachment);
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

    private async Task<int> AttachmentCountAsync(Guid email) =>
        (await Admin.RetrieveAsync("email", email, new ColumnSet("attachmentcount"))).GetAttributeValue<int>("attachmentcount");

    private async Task<long> AttachmentRowsAsync(Guid email) =>
        Convert.ToInt64(await ScalarAsync("SELECT COUNT(*) FROM data.activitymimeattachment WHERE objectid = @id", ("@id", email)));

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

    private async Task<HttpResponseMessage> GetAsync(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    // ---- bodies ----

    [Fact]
    public async Task Create_WithBase64Body_StoresFile_SetsFilesize_AndAttachmentNumber()
    {
        var email = await EmailAsync();
        var first = Bytes(10, 3);

        var id = await Admin.CreateAsync(Attachment(email, first));
        var second = await Admin.CreateAsync(Attachment(email, Bytes(4, 9), "two.bin"));

        var read = await Admin.RetrieveAsync(Table, id, new ColumnSet("body", "filesize", "attachmentnumber", "mimetype", "filename", "objectid"));
        read.GetAttributeValue<string>("body").Should().Be(Base64(first));
        read.GetAttributeValue<int>("filesize").Should().Be(10);
        read.GetAttributeValue<int>("attachmentnumber").Should().Be(1);
        read.GetAttributeValue<string>("mimetype").Should().Be("text/plain");
        read.GetAttributeValue<EntityReference>("objectid").Id.Should().Be(email.Id);
        (await Admin.RetrieveAsync(Table, second, new ColumnSet("attachmentnumber", "mimetype", "filesize")))
            .Should().Match<Entity>(e => e.GetAttributeValue<int>("attachmentnumber") == 2
                                         && e.GetAttributeValue<string>("mimetype") == "application/octet-stream" && e.GetAttributeValue<int>("filesize") == 4);

        var row = (await BodyRowsAsync(id)).Should().ContainSingle().Which;
        (row.Field, row.FileName, row.Size).Should().Be(("body", "report.txt", 10L));
        (await BlobAsync(row.Id)).Should().Equal(first);
        // The body has no column.
        (await ScalarAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'data' AND table_name = 'activitymimeattachment' AND column_name = 'body'"))
            .Should().Be(0);
    }

    [Fact]
    public async Task Retrieve_WithAllColumns_AndWebApi_ReturnTheBody()
    {
        var email = await EmailAsync();
        var content = Bytes(12, 5);
        var id = await Admin.CreateAsync(Attachment(email, content));

        (await Admin.RetrieveAsync(Table, id, new ColumnSet(true))).GetAttributeValue<string>("body").Should().Be(Base64(content));
        var res = await GetAsync($"activitymimeattachments({id})?$select=body,filesize");
        var json = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, json);
        JsonNode.Parse(json)!["body"]!.GetValue<string>().Should().Be(Base64(content));
        (await GetAsync("activitymimeattachments?$filter=body ne null")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ReplacesBody_AndDelete_ReleasesFile()
    {
        var email = await EmailAsync();
        var id = await Admin.CreateAsync(Attachment(email, Bytes(8, 1)));
        var old = (await BodyRowsAsync(id)).Single().Id;

        await Admin.UpdateAsync(new Entity(Table, id) { ["body"] = Base64(Bytes(5, 2)) });

        var row = (await BodyRowsAsync(id)).Should().ContainSingle().Which;
        row.Id.Should().NotBe(old);
        (await BlobAsync(old)).Should().BeNull("the replaced body's blob is deleted");
        (await BlobAsync(row.Id)).Should().Equal(Bytes(5, 2));
        (await Admin.RetrieveAsync(Table, id, new ColumnSet("filesize"))).GetAttributeValue<int>("filesize").Should().Be(5);

        await Admin.DeleteAsync(Table, id);

        (await BodyRowsAsync(id)).Should().BeEmpty();
        (await BlobAsync(row.Id)).Should().BeNull();
    }

    [Fact]
    public async Task ReadAllAsync_ReturnsTheAttachmentsOfAnEmailInOrder()
    {
        var email = await EmailAsync();
        var other = await EmailAsync();
        await Admin.CreateAsync(Attachment(email, Bytes(3, 1), "a.txt"));
        await Admin.CreateAsync(Attachment(email, Bytes(4, 2), "b.pdf"));
        await Admin.CreateAsync(Attachment(other, Bytes(5, 3), "c.txt"));

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var all = await fx.App.Services.GetRequiredService<FileColumnService>().Attachments.ReadAllAsync(new DataSession(conn, null), email.Id, default);

        all.Select(a => (a.FileName, a.MimeType, a.ContentId)).Should().Equal(("a.txt", "text/plain", null), ("b.pdf", "application/pdf", null));
        var expected = new[] { Bytes(3, 1), Bytes(4, 2) };
        for (var i = 0; i < all.Count; i++)
        {
            await using var body = all[i].Body;
            using var copy = new MemoryStream();
            await body.CopyToAsync(copy);
            copy.ToArray().Should().Equal(expected[i]);
        }
    }

    // ---- count ----

    [Fact]
    public async Task AttachmentCount_FollowsCreateAndDelete()
    {
        var email = await EmailAsync();
        (await AttachmentCountAsync(email.Id)).Should().Be(0);

        var first = await Admin.CreateAsync(Attachment(email, Bytes(2, 1)));
        await Admin.CreateAsync(Attachment(email, Bytes(2, 2), "two.txt"));
        (await AttachmentCountAsync(email.Id)).Should().Be(2);

        await Admin.DeleteAsync(Table, first);
        (await AttachmentCountAsync(email.Id)).Should().Be(1);
        // The count is the server's: a client value changes nothing.
        await Admin.UpdateAsync(new Entity("email", email.Id) { ["attachmentcount"] = 7 });
        (await AttachmentCountAsync(email.Id)).Should().Be(1);
    }

    // ---- refusals ----

    [Fact]
    public async Task Create_OverMaxUploadSize_Is400()
    {
        await ExecAsync("UPDATE data.organization SET maxuploadfilesize = 10");
        var email = await EmailAsync();
        var spy = new SpyFileStore(Blobs);

        await FaultAsync(() => Admin.CreateAsync(Attachment(email, Bytes(11, 1))), FileTooBig);

        await AssertNothingLeftAsync(email.Id, spy);
        (await Admin.CreateAsync(Attachment(email, Bytes(10, 1)))).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Create_BlockedExtension_Is400()
    {
        await ExecAsync("UPDATE data.organization SET blockedattachments = 'EXE; bat'");
        var email = await EmailAsync();

        var fault = await FaultAsync(() => Admin.CreateAsync(Attachment(email, Bytes(3, 1), "Run.exe")), ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("exe");
        await FaultAsync(() => Admin.CreateAsync(Attachment(email, Bytes(3, 1), "run.BAT")), ErrorCodes.InvalidArgument);
        foreach (var sneaky in new[] { "run.exe.", "run.exe ", "run.EXE..", "run.exe. ." })
            await FaultAsync(() => Admin.CreateAsync(Attachment(email, Bytes(3, 1), sneaky)), ErrorCodes.InvalidArgument);
        // Also when the name is changed to a blocked one.
        var ok = await Admin.CreateAsync(Attachment(email, Bytes(3, 1), "run.txt"));
        await FaultAsync(() => Admin.UpdateAsync(new Entity(Table, ok) { ["filename"] = "run.exe" }), ErrorCodes.InvalidArgument);

        (await AttachmentRowsAsync(email.Id)).Should().Be(1);
        (await BodyRowsAsync(ok)).Should().ContainSingle();
    }

    [Fact]
    public async Task DefaultBlockedAttachments_RefuseAnExecutable_OutOfTheBox()
    {
        // Final review I2: the organization is seeded with Dataverse's default list.
        await ExecAsync("UPDATE data.organization SET blockedattachments = '" + MailSchema.DefaultBlockedAttachments + "'");
        var email = await EmailAsync();
        foreach (var name in new[] { "setup.exe", "run.js", "image.svg" })
            await FaultAsync(() => Admin.CreateAsync(Attachment(email, Bytes(3, 1), name)), ErrorCodes.InvalidArgument);
        await Admin.CreateAsync(Attachment(email, Bytes(3, 1), "report.pdf"));
    }

    [Fact]
    public async Task Create_OnClosedEmail_Is400()
    {
        var email = await EmailAsync();
        // A sent email: state Completed, status Sent.
        await ExecAsync($"UPDATE data.email SET statecode = 1, statuscode = 3 WHERE activityid = '{email.Id}'");

        await FaultAsync(() => Admin.CreateAsync(Attachment(email, Bytes(3, 1))), ErrorCodes.InvalidArgument);

        await AssertNothingLeftAsync(email.Id, new SpyFileStore(Blobs));
    }

    [Fact]
    public async Task Create_OnOtherTable_Is400()
    {
        var task = new EntityReference("task", await Admin.CreateAsync(new Entity("task") { ["subject"] = "Task " + _run }));
        var template = new EntityReference("template", Guid.NewGuid());

        foreach (var target in new[] { task, template })
        {
            var fault = (await FluentActions.Awaiting(() => Admin.CreateAsync(Attachment(target, Bytes(3, 1)))).Should().ThrowAsync<XrmdFaultException>()).Which;
            fault.HttpStatus.Should().Be(400, fault.Message);
        }

        await AssertNothingLeftAsync(task.Id, new SpyFileStore(Blobs));
    }

    [Fact]
    public async Task Create_WithInvalidBase64_Is400_AndWritesNothing()
    {
        var email = await EmailAsync();
        var attachment = Attachment(email, null);
        attachment["body"] = "not base64!";

        await FaultAsync(() => Admin.CreateAsync(attachment), ErrorCodes.InvalidArgument);

        await AssertNothingLeftAsync(email.Id, new SpyFileStore(Blobs));
    }

    /// <summary>No attachment row of <paramref name="record"/> and no blob written through <paramref name="spy"/> survives.</summary>
    private async Task AssertNothingLeftAsync(Guid record, SpyFileStore spy)
    {
        (await AttachmentRowsAsync(record)).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.fileattachment WHERE objectid = @id", ("@id", record))).Should().Be(0);
        await fx.DrainFileDeletionsAsync();
        foreach (var key in spy.Written) (await Blobs.GetLengthAsync(key, default)).Should().BeNull();
    }

    // ---- security ----

    [Fact]
    public async Task Security_FollowsEmail()
    {
        var (userId, user) = await UserAsync(("prvReadActivity", DepthMask.Basic), ("prvWriteActivity", DepthMask.Basic), ("prvCreateActivity", DepthMask.Basic));
        var (_, readOnly) = await UserAsync(("prvReadActivity", DepthMask.Global));
        var mine = new EntityReference("email", await Admin.CreateAsync(new Entity("email")
            { ["subject"] = "Mine " + _run, ["ownerid"] = new EntityReference("systemuser", userId) }));
        var theirs = await EmailAsync();
        var mineAttachment = await Admin.CreateAsync(Attachment(mine, Bytes(6, 1)));
        var theirAttachment = await Admin.CreateAsync(Attachment(theirs, Bytes(7, 2)));
        var theirBlob = (await BodyRowsAsync(theirAttachment)).Single().Id;

        // Read follows the email: the user reads its own email's attachments and nothing else (the body included).
        var query = new QueryExpression(Table) { ColumnSet = new ColumnSet(true) };
        query.Criteria.AddCondition("objectid", ConditionOperator.In, mine.Id, theirs.Id);
        var rows = (await user.RetrieveMultipleAsync(query)).Entities;
        rows.Should().ContainSingle().Which.Id.Should().Be(mineAttachment);
        rows[0].GetAttributeValue<string>("body").Should().Be(Base64(Bytes(6, 1)));
        (await FaultAsync(() => user.RetrieveAsync(Table, theirAttachment, new ColumnSet("body")), ErrorCodes.AccessDenied))
            .Message.Should().NotContain(Base64(Bytes(7, 2)));
        var files = new QueryExpression("fileattachment") { ColumnSet = new ColumnSet("fileattachmentid") };
        files.Criteria.AddCondition("objectid", ConditionOperator.In, mineAttachment, theirAttachment);
        (await user.RetrieveMultipleAsync(files)).Entities.Should().ContainSingle().Which.Id.Should().NotBe(theirBlob);

        // Write follows the email: create, update and delete on one's own email, not on another's.
        var created = await user.CreateAsync(Attachment(mine, Bytes(2, 4), "mine.txt"));
        await user.UpdateAsync(new Entity(Table, created) { ["body"] = Base64(Bytes(3, 5)) });
        await FaultAsync(() => user.CreateAsync(Attachment(theirs, Bytes(2, 4))), ErrorCodes.AccessDenied);
        await FaultAsync(() => user.UpdateAsync(new Entity(Table, theirAttachment) { ["subject"] = "x" }), ErrorCodes.AccessDenied);
        await FaultAsync(() => user.DeleteAsync(Table, theirAttachment), ErrorCodes.AccessDenied);
        await user.DeleteAsync(Table, created);

        // Read on the email alone reads the attachment but writes nothing.
        (await readOnly.RetrieveAsync(Table, theirAttachment, new ColumnSet("filename"))).Id.Should().Be(theirAttachment);
        await FaultAsync(() => readOnly.CreateAsync(Attachment(theirs, Bytes(2, 4))), ErrorCodes.PrivilegeDenied);
        await FaultAsync(() => readOnly.UpdateAsync(new Entity(Table, theirAttachment) { ["subject"] = "x" }), ErrorCodes.PrivilegeDenied);
        await FaultAsync(() => readOnly.DeleteAsync(Table, theirAttachment), ErrorCodes.PrivilegeDenied);

        // A user without read on activities reads no attachment.
        var (_, nobody) = await UserAsync(("prvReadAccount", DepthMask.Global));
        (await nobody.RetrieveMultipleAsync(new QueryExpression(Table) { ColumnSet = new ColumnSet("filename") })).Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Detaching_AnAttachment_IsRefused()
    {
        var email = await EmailAsync();
        var other = await EmailAsync();
        var id = await Admin.CreateAsync(Attachment(email, Bytes(3, 1)));

        await FaultAsync(() => Admin.UpdateAsync(new Entity(Table, id) { ["objectid"] = null }), ErrorCodes.InvalidArgument);
        await FaultAsync(() => Admin.UpdateAsync(new Entity(Table, id) { ["objectid"] = other }), ErrorCodes.InvalidArgument);
        var fault = await FluentActions.Awaiting(() => Admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.DisassociateRequest
        {
            Target = email,
            Relationship = new Relationship("email_activity_mime_attachments"),
            RelatedEntities = new EntityReferenceCollection { new EntityReference(Table, id) },
        })).Should().ThrowAsync<XrmdFaultException>();
        fault.Which.HttpStatus.Should().Be(400);

        (await AttachmentRowsAsync(email.Id)).Should().Be(1);
        (await AttachmentCountAsync(email.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Orphan_Attachment_IsNotWritableByAnUnprivilegedUser()
    {
        var email = await EmailAsync();
        var id = await Admin.CreateAsync(Attachment(email, Bytes(3, 1)));
        await ExecAsync($"UPDATE data.activitymimeattachment SET objectid = NULL WHERE activitymimeattachmentid = '{id}'");
        var (_, nobody) = await UserAsync(("prvReadAccount", DepthMask.Global));

        await FaultAsync(() => nobody.DeleteAsync(Table, id), ErrorCodes.AccessDenied);
        await FaultAsync(() => nobody.UpdateAsync(new Entity(Table, id) { ["subject"] = "x" }), ErrorCodes.AccessDenied);

        (await BodyRowsAsync(id)).Should().ContainSingle();
    }

    [Fact]
    public async Task EmailDelete_FiresDeleteSteps_OnAttachments_WithoutWriteOnTheEmail()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Delete", Table, stage: 40, config: "invalid:attachment delete step");
        var (userId, user) = await UserAsync(("prvReadActivity", DepthMask.Basic), ("prvDeleteActivity", DepthMask.Basic));
        var email = new EntityReference("email", await Admin.CreateAsync(new Entity("email")
            { ["subject"] = "Mine " + _run, ["ownerid"] = new EntityReference("systemuser", userId) }));
        var id = await Admin.CreateAsync(Attachment(email, Bytes(3, 1)));

        // The step on the attachment's Delete fires with the cascade (and aborts it).
        (await FaultAsync(() => user.DeleteAsync("email", email.Id), ErrorCodes.ISVAborted)).Message.Should().Be("attachment delete step");
        (await AttachmentRowsAsync(email.Id)).Should().Be(1);

        await PluginTestHelper.ClearAsync(fx);
        // Without the step, Delete on the email alone is enough (no Write on it).
        await user.DeleteAsync("email", email.Id);
        (await BodyRowsAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task EmailDelete_DeletesAttachments_AndFiles()
    {
        var email = await EmailAsync();
        var a = await Admin.CreateAsync(Attachment(email, Bytes(4, 1)));
        var b = await Admin.CreateAsync(Attachment(email, Bytes(5, 2), "b.txt"));
        var files = (await BodyRowsAsync(a)).Concat(await BodyRowsAsync(b)).Select(r => r.Id).ToList();
        files.Should().HaveCount(2);

        await Admin.DeleteAsync("email", email.Id);

        (await AttachmentRowsAsync(email.Id)).Should().Be(0);
        foreach (var attachment in new[] { a, b }) (await BodyRowsAsync(attachment)).Should().BeEmpty();
        foreach (var file in files) (await BlobAsync(file)).Should().BeNull();
    }

    [Fact]
    public async Task ChunkedAttachmentMessages_StayRefused()
    {
        foreach (var name in new[] { "InitializeAttachmentBlocksUpload", "InitializeAttachmentBlocksDownload" })
            (await FluentActions.Awaiting(() => Admin.ExecuteAsync(new OrganizationRequest(name)))
                .Should().ThrowAsync<NotSupportedException>(name)).Which.Message.Should().Contain("activitymimeattachment.body");
    }

    // ---- objecttypecode on the Web API (plan 4.1b, task 9, ruling P13) ----

    private async Task<HttpResponseMessage> PostAttachmentAsync(Guid email, string? typeCode)
    {
        var nav = fx.App.Services.GetRequiredService<IMetadataStore>().GetOneToManyRelationships()
            .Single(r => r.ReferencingEntity == Table && r.ReferencingAttribute == "objectid" && r.ReferencedEntity == "email")
            .ReferencingEntityNavigationPropertyName;
        var body = new JsonObject
        {
            ["filename"] = "notes.txt", ["mimetype"] = "text/plain", ["body"] = Base64(Bytes(5, 1)),
            [$"{nav}@odata.bind"] = $"/emails({email})",
        };
        if (typeCode is not null) body["objecttypecode"] = typeCode;
        var req = new HttpRequestMessage(HttpMethod.Post, Api + "activitymimeattachments")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    [Fact]
    public async Task WebApi_Create_AcceptsObjectTypeCode_OfTheBoundEmail()
    {
        var email = await EmailAsync();
        var res = await PostAttachmentAsync(email.Id, "email");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await AttachmentRowsAsync(email.Id)).Should().Be(1);
    }

    [Fact]
    public async Task WebApi_Create_RefusesObjectTypeCode_OfAnotherTable()
    {
        var email = await EmailAsync();
        var res = await PostAttachmentAsync(email.Id, "task");
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("objecttypecode");
        (await AttachmentRowsAsync(email.Id)).Should().Be(0);
    }
}
