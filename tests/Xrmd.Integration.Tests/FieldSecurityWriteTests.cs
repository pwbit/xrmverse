using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Field-level security on every write path (plan 14, task 5, rulings F1 and F4; Review Focus 4): a secured column in a Create
/// needs <c>cancreate</c> (0x8004F502) unless its value is null; in an Update it needs <c>canupdate</c> or an update field share
/// on the record (0x8004F507), whatever the value; System Administrators bypass, System Customizers do not; a plug-in step that
/// runs as a user is checked as that user; uploading, committing and deleting a secured file needs update on the column.
/// </summary>
[Collection("xrmd")]
public sealed class FieldSecurityWriteTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Table = "new_fswrite";
    private const string S1 = "new_fsws1", Plain = "new_fswplain", Doc = "new_fswdoc", Img = "new_fswimg";
    private const string Api = "/api/data/v9.2/";
    private const string Plugins = "Xrmd.TestPlugins.";
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private Guid _root;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private string Set => Store.GetEntity(Table)!.EntitySetName;

    public async Task InitializeAsync()
    {
        _root = await RootBusinessUnitAsync(fx);
        await Lock.WaitAsync();
        try
        {
            await EnsureTableAsync();
            if (Store.GetEntity(Table)!.Attributes.All(a => a.LogicalName != Img))
                await Admin.ExecuteAsync(new CreateAttributeRequest
                {
                    EntityName = Table, Attribute = new ImageAttributeMetadata { LogicalName = Img, SchemaName = "new_FsWImg", IsSecured = true },
                });
        }
        finally
        {
            Lock.Release();
        }
    }

    private async Task EnsureTableAsync()
    {
        if (Store.GetEntity(Table) is not null) return;
        // new_fsws1 is secured, new_fswplain is not; new_fswdoc is a secured file column.
        var entity = new EntityMetadata { LogicalName = Table, SchemaName = "new_FsWrite", OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes(
        [
            new StringAttributeMetadata { LogicalName = S1, SchemaName = "new_FsWS1", MaxLength = 100, IsSecured = true },
            new StringAttributeMetadata { LogicalName = Plain, SchemaName = "new_FsWPlain", MaxLength = 100 },
        ]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = Table, Attribute = new FileAttributeMetadata { LogicalName = Doc, SchemaName = "new_FsWDoc", IsSecured = true },
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A user with every record privilege on the table (Global) and, when <paramref name="permissions"/> is not empty, a new
    /// profile with those column permissions (Allowed = 4, Not Allowed = 0).
    /// </summary>
    private async Task<(Guid Id, Guid ObjectId)> UserAsync(params (string Attribute, bool Read, bool Create, bool Update)[] permissions)
    {
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, _root, oid);
        await GrantAsync(fx, user, team: false, ("prvCreatenew_FsWrite", DepthMask.Global), ("prvReadnew_FsWrite", DepthMask.Global),
            ("prvWritenew_FsWrite", DepthMask.Global), ("prvAppendnew_FsWrite", DepthMask.Global), ("prvDeletenew_FsWrite", DepthMask.Global));
        if (permissions.Length > 0) await GiveProfileAsync(user, permissions);
        return (user, oid);
    }

    private async Task<Guid> GiveProfileAsync(Guid user, params (string Attribute, bool Read, bool Create, bool Update)[] permissions)
    {
        var profile = await Admin.CreateAsync(new Entity(FieldSecurityTables.Profile) { ["name"] = "fsw-" + Guid.NewGuid().ToString("N")[..8] });
        foreach (var (attribute, read, create, update) in permissions)
            await Admin.CreateAsync(new Entity(FieldSecurityTables.Permission)
            {
                [FieldSecurityTables.ProfileId] = new EntityReference(FieldSecurityTables.Profile, profile),
                [FieldSecurityTables.EntityName] = Table, [FieldSecurityTables.AttributeLogicalName] = attribute,
                [FieldSecurityTables.CanRead] = new OptionSetValue(read ? 4 : 0),
                [FieldSecurityTables.CanCreate] = new OptionSetValue(create ? 4 : 0),
                [FieldSecurityTables.CanUpdate] = new OptionSetValue(update ? 4 : 0),
            });
        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("systemuser", user), Relationship = new Relationship("systemuserprofiles_association"),
            RelatedEntities = [new EntityReference(FieldSecurityTables.Profile, profile)],
        });
        return profile;
    }

    private async Task RemoveProfileAsync(Guid user, Guid profile) =>
        await Admin.ExecuteAsync(new DisassociateRequest
        {
            Target = new EntityReference("systemuser", user), Relationship = new Relationship("systemuserprofiles_association"),
            RelatedEntities = [new EntityReference(FieldSecurityTables.Profile, profile)],
        });

    /// <summary>A field share of <paramref name="attribute"/> on <paramref name="record"/> to <paramref name="user"/> (direct SQL).</summary>
    private async Task ShareAsync(Guid record, string attribute, Guid user, bool read, bool update)
    {
        var m = Store.GetEntity(Table)!;
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, $"""
                INSERT INTO data.{FieldSecurityTables.Share} (principalobjectattributeaccessid, objectid, objectidtype, objecttypecode,
                    attributeid, principalid, principalidtype, readaccess, updateaccess)
                VALUES (NEWID(), @record, @otc, @table, @attribute, @principal, 8, @read, @update)
                """);
            cmd.Parameters.AddWithValue("@record", record);
            cmd.Parameters.AddWithValue("@otc", m.ObjectTypeCode!.Value);
            cmd.Parameters.AddWithValue("@table", Table);
            cmd.Parameters.AddWithValue("@attribute", m.Attributes.Single(x => x.LogicalName == attribute).MetadataId!.Value);
            cmd.Parameters.AddWithValue("@principal", user);
            cmd.Parameters.AddWithValue("@read", read);
            cmd.Parameters.AddWithValue("@update", update);
            await cmd.ExecuteNonQueryAsync();
        }
        Cache(fx).Invalidate();
    }

    private Task<Guid> RecordAsync() =>
        Admin.CreateAsync(new Entity(Table) { ["new_name"] = "fsw-" + Guid.NewGuid().ToString("N")[..10], [S1] = "secret", [Plain] = "plain" });

    private async Task<string?> StoredAsync(Guid id, string attribute) =>
        (await Admin.RetrieveAsync(Table, id, new ColumnSet(attribute))).GetAttributeValue<string>(attribute);

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (XrmdFaultException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("The call was expected to fail.");
    }

    private static async Task DeniedAsync(Func<Task> act, int code, string because)
    {
        var fault = await FaultAsync(act);
        fault.ErrorCode.Should().Be(code, because);
        fault.HttpStatus.Should().Be(403, because);
    }

    private static string Hex(int code) => "0x" + ((uint)code).ToString("x8");

    private async Task<HttpResponseMessage> SendAsync(Guid oid, HttpMethod method, string path, HttpContent? content = null)
    {
        using var req = new HttpRequestMessage(method, Api + path) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<string> InitializeUploadAsync(OrganizationService service, Guid id) =>
        ((InitializeFileBlocksUploadResponse)await service.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(Table, id), FileAttributeName = Doc, FileName = "secret.txt",
        })).FileContinuationToken;

    private static async Task<Guid> UploadRestAsync(OrganizationService service, string token, byte[] content)
    {
        var block = Convert.ToBase64String(BitConverter.GetBytes(0L));
        await service.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = block, BlockData = content });
        return ((CommitFileBlocksUploadResponse)await service.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [block], FileName = "secret.txt", MimeType = "text/plain",
        })).FileId;
    }

    private async Task<Guid?> FileIdAsync(Guid id) =>
        (await Admin.RetrieveAsync(Table, id, new ColumnSet(Doc))).GetAttributeValue<Guid?>(Doc);

    // ---- Review Focus 4 -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Write_rules_for_secured_columns()
    {
        var (_, oid) = await UserAsync();
        var service = ServiceFor(fx, oid);

        // Create: a non-null secured value needs cancreate; a null one writes nothing and passes.
        await DeniedAsync(() => service.CreateAsync(new Entity(Table) { ["new_name"] = "c1", [S1] = "x" }), ErrorCodes.FieldCreateDenied,
            "the user has no cancreate on new_fsws1");
        var created = await service.CreateAsync(new Entity(Table) { ["new_name"] = "c2", [S1] = null, [Plain] = "p" });
        (await StoredAsync(created, S1)).Should().BeNull();

        // Update: the column needs canupdate whatever the value, null and unchanged included; other columns pass.
        var id = await RecordAsync();
        foreach (var value in new object?[] { "secret", null, "other" })
            await DeniedAsync(() => service.UpdateAsync(new Entity(Table, id) { [S1] = value }), ErrorCodes.FieldUpdateDenied,
                $"the user has no canupdate on new_fsws1 (value {value ?? "null"})");
        await service.UpdateAsync(new Entity(Table, id) { [Plain] = "changed" });
        (await StoredAsync(id, S1)).Should().Be("secret");

        // A System Customizer is checked like anyone else (F1).
        var (customizer, customizerOid) = await UserAsync();
        await AssignRoleAsync(fx, customizer, StandardRoles.SystemCustomizer);
        await DeniedAsync(() => ServiceFor(fx, customizerOid).CreateAsync(new Entity(Table) { ["new_name"] = "c3", [S1] = "x" }),
            ErrorCodes.FieldCreateDenied, "a System Customizer does not bypass field security");
        await DeniedAsync(() => ServiceFor(fx, customizerOid).UpdateAsync(new Entity(Table, id) { [S1] = "x" }),
            ErrorCodes.FieldUpdateDenied, "a System Customizer does not bypass field security");

        // A System Administrator bypasses.
        var administratorOid = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, _root, administratorOid), StandardRoles.SystemAdministrator);
        var administrator = ServiceFor(fx, administratorOid);
        var byAdministrator = await administrator.CreateAsync(new Entity(Table) { ["new_name"] = "c4", [S1] = "by admin" });
        await administrator.UpdateAsync(new Entity(Table, byAdministrator) { [S1] = "updated by admin" });
        (await StoredAsync(byAdministrator, S1)).Should().Be("updated by admin");

        // The profile's rights are separate: create without update, then update without create.
        var (creator, creatorOid) = await UserAsync((S1, false, true, false));
        var own = await ServiceFor(fx, creatorOid).CreateAsync(new Entity(Table) { ["new_name"] = "c5", [S1] = "mine" });
        (await StoredAsync(own, S1)).Should().Be("mine");
        await DeniedAsync(() => ServiceFor(fx, creatorOid).UpdateAsync(new Entity(Table, own) { [S1] = "x" }), ErrorCodes.FieldUpdateDenied,
            "cancreate does not give canupdate");
        var (_, updaterOid) = await UserAsync((S1, false, false, true));
        await DeniedAsync(() => ServiceFor(fx, updaterOid).CreateAsync(new Entity(Table) { ["new_name"] = "c6", [S1] = "x" }),
            ErrorCodes.FieldCreateDenied, "canupdate does not give cancreate");
        await ServiceFor(fx, updaterOid).UpdateAsync(new Entity(Table, id) { [S1] = null });
        (await StoredAsync(id, S1)).Should().BeNull();

        // An update field share allows the update on its record only; a read share does not.
        var other = await RecordAsync();
        await ShareAsync(id, S1, creator, read: false, update: true);
        await ShareAsync(other, S1, creator, read: true, update: false);
        await ServiceFor(fx, creatorOid).UpdateAsync(new Entity(Table, id) { [S1] = "shared update" });
        (await StoredAsync(id, S1)).Should().Be("shared update");
        await DeniedAsync(() => ServiceFor(fx, creatorOid).UpdateAsync(new Entity(Table, other) { [S1] = "x" }), ErrorCodes.FieldUpdateDenied,
            "a read share does not allow an update");

        // The Web API answers 403 with the code in the error body.
        var patch = await SendAsync(oid, HttpMethod.Patch, $"{Set}({id})", Json($$"""{ "{{S1}}": "web" }"""));
        var body = await patch.Content.ReadAsStringAsync();
        patch.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain(Hex(ErrorCodes.FieldUpdateDenied));
        var post = await SendAsync(oid, HttpMethod.Post, Set, Json($$"""{ "new_name": "web", "{{S1}}": "web" }"""));
        body = await post.Content.ReadAsStringAsync();
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain(Hex(ErrorCodes.FieldCreateDenied));
        (await StoredAsync(id, S1)).Should().Be("shared update");
    }

    // ---- plug-ins -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Plugin_user_is_checked_as_itself()
    {
        var (withoutProfile, _) = await UserAsync();
        var (withProfile, _) = await UserAsync((S1, true, true, true));
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Plugins + "UpdateAttributePlugin");

            // The caller (a System Administrator) bypasses field security; the step's user does not, and is what counts.
            var step = await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", Table, 40, config: $"{S1}=by plug-in",
                impersonatingUserId: withoutProfile);
            var name = "fsw-plugin-" + Guid.NewGuid().ToString("N")[..8];
            // The plug-in does not catch the fault: it surfaces with its code, as a plug-in failure (status 400).
            (await FaultAsync(() => Admin.CreateAsync(new Entity(Table) { ["new_name"] = name }))).ErrorCode
                .Should().Be(ErrorCodes.FieldUpdateDenied, "the step runs as a user without canupdate on new_fsws1");
            var query = new QueryExpression(Table) { ColumnSet = new ColumnSet(false) };
            query.Criteria.AddCondition("new_name", ConditionOperator.Equal, name);
            (await Admin.RetrieveMultipleAsync(query)).Entities.Should().BeEmpty("the Create rolled back");

            // The same step running as a user holding canupdate succeeds.
            await Admin.UpdateAsync(new Entity("sdkmessageprocessingstep", step)
            {
                ["impersonatinguserid"] = new EntityReference("systemuser", withProfile),
            });
            fx.App.Services.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>().Invalidate();
            var id = await Admin.CreateAsync(new Entity(Table) { ["new_name"] = name });
            (await StoredAsync(id, S1)).Should().Be("by plug-in");
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }

    // ---- files --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Secured_file_column_upload_needs_update()
    {
        var id = await RecordAsync();
        var content = "secret file"u8.ToArray();
        var (user, oid) = await UserAsync((Doc, true, false, false));
        var service = ServiceFor(fx, oid);

        // InitializeFileBlocksUpload, the Web API upload and DeleteFile need update on the column.
        await DeniedAsync(() => InitializeUploadAsync(service, id), ErrorCodes.FieldUpdateDenied, "the user has no canupdate on new_fswdoc");
        var upload = new ByteArrayContent(content);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        upload.Headers.Add("x-ms-file-name", "secret.txt");
        var res = await SendAsync(oid, HttpMethod.Patch, $"{Set}({id})/{Doc}", upload);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain(Hex(ErrorCodes.FieldUpdateDenied));

        var fileId = await UploadRestAsync(Admin, await InitializeUploadAsync(Admin, id), content);
        await DeniedAsync(() => service.ExecuteAsync(new DeleteFileRequest { FileId = fileId }), ErrorCodes.FieldUpdateDenied,
            "deleting the file of a secured column needs canupdate");
        res = await SendAsync(oid, HttpMethod.Delete, $"{Set}({id})/{Doc}");
        body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain(Hex(ErrorCodes.FieldUpdateDenied));
        (await FileIdAsync(id)).Should().Be(fileId);

        // The commit is checked too: canupdate taken away after the upload was opened refuses it.
        var profile = await GiveProfileAsync(user, (Doc, true, false, true));
        var token = await InitializeUploadAsync(service, id);
        await RemoveProfileAsync(user, profile);
        await DeniedAsync(() => UploadRestAsync(service, token, content), ErrorCodes.FieldUpdateDenied, "the commit writes the column");
        (await FileIdAsync(id)).Should().Be(fileId);

        // With canupdate the user uploads and deletes.
        await GiveProfileAsync(user, (Doc, true, false, true));
        var replaced = await UploadRestAsync(service, await InitializeUploadAsync(service, id), content);
        (await FileIdAsync(id)).Should().Be(replaced);
        await service.ExecuteAsync(new DeleteFileRequest { FileId = replaced });
        (await FileIdAsync(id)).Should().BeNull();

        // An update field share on the record is enough.
        var (sharedUser, sharedOid) = await UserAsync();
        await ShareAsync(id, Doc, sharedUser, read: true, update: true);
        var sharedFile = await UploadRestAsync(ServiceFor(fx, sharedOid), await InitializeUploadAsync(ServiceFor(fx, sharedOid), id), content);
        (await FileIdAsync(id)).Should().Be(sharedFile);
        res = await SendAsync(sharedOid, HttpMethod.Delete, $"{Set}({id})/{Doc}");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await FileIdAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task Secured_image_column_written_with_bytes_needs_create_or_update()
    {
        var (user, oid) = await UserAsync();
        var service = ServiceFor(fx, oid);
        var image = Png();
        async Task<Guid?> ImageIdAsync(Guid id) => (await Admin.RetrieveAsync(Table, id, new ColumnSet(Img + "id"))).GetAttributeValue<Guid?>(Img + "id");

        // The image is the table's primary image (its only one), so a Create may carry it.
        await DeniedAsync(() => service.CreateAsync(new Entity(Table) { ["new_name"] = "img-c", [Img] = image }), ErrorCodes.FieldCreateDenied,
            "setting a secured image on create needs cancreate");
        var id = await RecordAsync();
        await DeniedAsync(() => service.UpdateAsync(new Entity(Table, id) { [Img] = image }), ErrorCodes.FieldUpdateDenied,
            "setting a secured image on update needs canupdate");
        (await ImageIdAsync(id)).Should().BeNull();

        await GiveProfileAsync(user, (Img, true, true, true));
        var created = await service.CreateAsync(new Entity(Table) { ["new_name"] = "img-c2", [Img] = image });
        (await ImageIdAsync(created)).Should().NotBeNull();
        await service.UpdateAsync(new Entity(Table, id) { [Img] = image });
        (await ImageIdAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Naming_a_companion_of_a_secured_column_needs_its_rights()
    {
        var (_, oid) = await UserAsync();
        var service = ServiceFor(fx, oid);
        var id = await RecordAsync();

        // The file name is read-only and the core would ignore it, but naming it is a write to the secured column.
        await DeniedAsync(() => service.CreateAsync(new Entity(Table) { ["new_name"] = "companion-c", [Doc + "_name"] = "x.txt" }),
            ErrorCodes.FieldCreateDenied, "the file name follows the secured file column");
        await DeniedAsync(() => service.UpdateAsync(new Entity(Table, id) { [Doc + "_name"] = "x.txt" }), ErrorCodes.FieldUpdateDenied,
            "the file name follows the secured file column");
        await DeniedAsync(() => service.UpdateAsync(new Entity(Table, id) { [Img + "id"] = Guid.NewGuid() }), ErrorCodes.FieldUpdateDenied,
            "the image id follows the secured image column");
    }

    private static byte[] Png()
    {
        using var bitmap = new SkiaSharp.SKBitmap(16, 16);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap)) canvas.Clear(SkiaSharp.SKColors.Blue);
        using var encoded = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = encoded.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task DeleteFile_of_a_file_without_its_column_name_fails_closed()
    {
        var id = await RecordAsync();
        var fileId = await UploadRestAsync(Admin, await InitializeUploadAsync(Admin, id), "orphan"u8.ToArray());
        // A row written before the column name was always filled: the field check cannot know the column.
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, "UPDATE data.fileattachment SET regardingfieldname = NULL WHERE fileattachmentid = @id");
            cmd.Parameters.AddWithValue("@id", fileId);
            await cmd.ExecuteNonQueryAsync();
        }
        var (_, oid) = await UserAsync();

        // Refused with the core operation's 404 before any write, never let through unchecked.
        foreach (var service in new[] { ServiceFor(fx, oid), Admin })
        {
            var fault = await service.Invoking(s => s.ExecuteAsync(new DeleteFileRequest { FileId = fileId })).Should().ThrowAsync<XrmdFaultException>();
            (fault.Which.ErrorCode, fault.Which.HttpStatus).Should().Be((ErrorCodes.ObjectDoesNotExist, 404));
        }
        (await FileIdAsync(id)).Should().Be(fileId);
    }
}
