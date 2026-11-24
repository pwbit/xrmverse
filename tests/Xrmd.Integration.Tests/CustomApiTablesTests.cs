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
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The seeded custom API tables (plan 15, task 1): <c>customapi</c>, <c>customapirequestparameter</c> and
/// <c>customapiresponseproperty</c>, their choices and relationships, their privileges, writes on them with the usual
/// privileges (plan 3.1), and the refusal of direct writes (C10) on an API's own <c>sdkmessage</c> and <c>sdkmessagefilter</c>
/// rows. Rows are written the way the system path writes them (SQL here, the internal caller for the importer).
/// </summary>
[Collection("xrmd")]
public sealed class CustomApiTablesTests(XrmdFixture fx)
{
    private const string Refusal = "maintained by the server";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private static IEnumerable<int> OptionValues(AttributeMetadata a) =>
        ((EnumAttributeMetadata)a).OptionSet!.Options.Select(o => o.Value!.Value);

    private AttributeMetadata Column(string entity, string name) => Store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == name);

    [Fact]
    public void Custom_api_tables_are_seeded_with_learn_columns()
    {
        (CustomApiTables.Api, CustomApiTables.RequestParameter, CustomApiTables.ResponseProperty)
            .Should().Be(("customapi", "customapirequestparameter", "customapiresponseproperty"));
        (CustomApiTables.ApiTypeCode, CustomApiTables.RequestParameterTypeCode, CustomApiTables.ResponsePropertyTypeCode)
            .Should().Be((9990, 9991, 9992));

        foreach (var (name, set, otc) in new[]
                 {
                     ("customapi", "customapis", 9990), ("customapirequestparameter", "customapirequestparameters", 9991),
                     ("customapiresponseproperty", "customapiresponseproperties", 9992),
                 })
        {
            var e = Store.GetEntity(name);
            e.Should().NotBeNull(name);
            e!.EntitySetName.Should().Be(set);
            e.ObjectTypeCode.Should().Be(otc);
            e.OwnershipType.Should().Be(OwnershipTypes.UserOwned);
            e.PrimaryIdAttribute.Should().Be(name + "id");
            e.PrimaryNameAttribute.Should().Be("name");
            MetadataStore.IsSystemEntity(e).Should().BeTrue();
            Column(name, "name").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(100);
            Column(name, "uniquename").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(128);
            Column(name, "description").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(300);
            Column(name, "displayname").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(100);
            Column(name, "xrmverse_componentxml").Should().BeOfType<MemoAttributeMetadata>().Which.IsValidForCreate.Should().BeFalse();
            Column(name, "ownerid").AttributeType.Should().Be(AttributeTypeCode.Owner);
            Column(name, "owninguser").Should().BeOfType<LookupAttributeMetadata>();
            Column(name, "owningbusinessunit").Should().BeOfType<LookupAttributeMetadata>();
            OptionValues(Column(name, "statecode")).Should().Equal(0, 1);
            OptionValues(Column(name, "statuscode")).Should().Equal(1, 2);
            e.Attributes.Should().NotContain(a => a.LogicalName == "iscustomizable");
        }

        const string api = "customapi";
        var binding = (PicklistAttributeMetadata)Column(api, "bindingtype");
        binding.OptionSet!.Name.Should().Be("customapi_bindingtype");
        OptionValues(binding).Should().Equal(0, 1, 2);
        Column(api, "boundentitylogicalname").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(100);
        var allowed = (PicklistAttributeMetadata)Column(api, "allowedcustomprocessingsteptype");
        allowed.OptionSet!.Name.Should().Be("customapi_allowedcustomprocessingsteptype");
        OptionValues(allowed).Should().Equal(0, 1, 2);
        foreach (var flag in new[] { "isfunction", "isprivate", "workflowsdkstepenabled" })
            Column(api, flag).Should().BeOfType<BooleanAttributeMetadata>();
        Column(api, "executeprivilegename").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(100);
        Column(api, "plugintypeid").Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().Equal("plugintype");
        Column(api, "sdkmessageid").Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().Equal("sdkmessage");
        Store.GetOptionSet("customapi_bindingtype").Should().NotBeNull();
        Store.GetOptionSet("customapi_allowedcustomprocessingsteptype").Should().NotBeNull();

        foreach (var field in new[] { "customapirequestparameter", "customapiresponseproperty" })
        {
            Column(field, "customapiid").Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().Equal("customapi");
            var type = (PicklistAttributeMetadata)Column(field, "type");
            type.OptionSet!.Name.Should().Be("customapifieldtype");
            OptionValues(type).Should().Equal(Enumerable.Range(0, 13));
            Column(field, "logicalentityname").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(100);
        }
        Column("customapirequestparameter", "isoptional").Should().BeOfType<BooleanAttributeMetadata>();
        Store.GetEntity("customapiresponseproperty")!.Attributes.Should().NotContain(a => a.LogicalName == "isoptional");
        ((OptionSetMetadata)Store.GetOptionSet("customapifieldtype")!).Options.Select(o => o.Label.UserLocalizedLabel.Label)
            .Should().Equal(Enum.GetNames<CustomApiType>());

        var relationships = Store.GetOneToManyRelationships();
        foreach (var (schema, referenced, referencing, attribute, delete, navigation) in new[]
                 {
                     ("customapi_customapirequestparameter", "customapi", "customapirequestparameter", "customapiid", CascadeType.Cascade,
                         "CustomAPIRequestParameters"),
                     ("customapi_customapiresponseproperty", "customapi", "customapiresponseproperty", "customapiid", CascadeType.Cascade,
                         "CustomAPIResponseProperties"),
                     ("plugintype_customapi", "plugintype", "customapi", "plugintypeid", CascadeType.Restrict, null),
                     ("sdkmessage_customapi", "sdkmessage", "customapi", "sdkmessageid", CascadeType.Restrict, null),
                 })
        {
            var r = relationships.Single(x => x.SchemaName == schema);
            r.ReferencedEntity.Should().Be(referenced);
            r.ReferencingEntity.Should().Be(referencing);
            r.ReferencingAttribute.Should().Be(attribute);
            r.CascadeConfiguration.Delete.Should().Be(delete, schema);
            if (navigation is not null) r.ReferencedEntityNavigationPropertyName.Should().Be(navigation);
        }
    }

    [Fact]
    public async Task Custom_api_privileges_are_granted_to_standard_roles()
    {
        var customizer = await RolePrivilegesAsync(StandardRoles.SystemCustomizer);
        var basic = await RolePrivilegesAsync(StandardRoles.BasicUser);
        var adminUser = (Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", ("@oid", fx.AdminObjectId)))!;
        var administrator = await Cache(fx).GetProfileAsync(adminUser, default);
        foreach (var table in new[] { "CustomAPI", "CustomAPIRequestParameter", "CustomAPIResponseProperty" })
        {
            foreach (var verb in new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo", "Assign", "Share" })
            {
                var name = $"prv{verb}{table}";
                (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name = @n", ("@n", name))).Should().Be(1, name);
                customizer.Should().ContainKey(name).WhoseValue.Should().Be((int)DepthMask.Global, name);
                administrator.DepthOf(name).Should().Be(DepthMask.Global, name);
            }
            basic.Should().ContainKey($"prvRead{table}").WhoseValue.Should().Be((int)DepthMask.Global);
            basic.Keys.Where(k => k.EndsWith(table, StringComparison.Ordinal)).Should().Equal($"prvRead{table}");
        }
    }

    [Fact]
    public async Task Custom_api_tables_are_written_with_the_usual_privileges()
    {
        // Plan 3.1 lifted the C10 refusal on the three tables: their mutable columns, owner and state change as for any
        // user-owned row (the authoring rules are pinned by CustomApiAuthoringTests).
        var rows = await InsertApiAsync("new_DirectWrites");
        try
        {
            foreach (var ((table, set), id) in Tables.Zip([rows.Api, rows.Parameter, rows.Property]))
            {
                await Admin.UpdateAsync(new Entity(table, id) { ["name"] = "changed" });
                await Admin.ExecuteAsync(new AssignRequest
                {
                    Target = new EntityReference(table, id), Assignee = new EntityReference("systemuser", rows.Owner),
                });
                var (status, body) = await SendAsync(HttpMethod.Patch, $"{set}({id})", """{ "displayname": "Changed" }""", fx.AdminObjectId);
                status.Should().Be(HttpStatusCode.NoContent, body);
                (await ScalarAsync($"SELECT COUNT(*) FROM data.{table} WHERE {table}id = @id AND name = N'changed' AND displayname = N'Changed'",
                    ("@id", id))).Should().Be(1);
            }

            // Reads work, for a Basic User too; writes need the privileges a Basic User lacks.
            var reader = Guid.NewGuid();
            var readerId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), reader);
            await AssignRoleAsync(fx, readerId, StandardRoles.BasicUser);
            var service = ServiceFor(fx, reader);
            foreach (var ((table, _), id) in Tables.Zip([rows.Api, rows.Parameter, rows.Property]))
            {
                var query = new QueryExpression(table) { ColumnSet = new ColumnSet("name") };
                query.Criteria.AddCondition(table + "id", ConditionOperator.Equal, id);
                (await service.RetrieveMultipleAsync(query)).Entities.Should().ContainSingle(table);
                (await FaultAsync(() => service.UpdateAsync(new Entity(table, id) { ["name"] = "denied" }))).HttpStatus.Should().Be(403);
            }
            var web = await SendAsync(HttpMethod.Get, $"customapis({rows.Api})?$select=uniquename", null, reader);
            web.Status.Should().Be(HttpStatusCode.OK, web.Body);
            web.Body.Should().Contain("new_DirectWrites");

            // The Delete of the API takes its parameters, properties, filter and message with it.
            var deleted = await SendAsync(HttpMethod.Delete, $"customapis({rows.Api})", null, fx.AdminObjectId);
            deleted.Status.Should().Be(HttpStatusCode.NoContent, deleted.Body);
            (await ScalarAsync("SELECT COUNT(*) FROM data.customapirequestparameter WHERE customapirequestparameterid = @id", ("@id", rows.Parameter)))
                .Should().Be(0);
            (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE sdkmessageid = @id", ("@id", rows.Message))).Should().Be(0);
        }
        finally
        {
            await DeleteApiAsync(rows);
        }
    }

    [Fact]
    public async Task Direct_writes_on_api_message_and_filter_are_refused()
    {
        var rows = await InsertApiAsync("new_MessageWrites");
        try
        {
            await RefusedAsync(() => Admin.UpdateAsync(new Entity("sdkmessage", rows.Message) { ["isprivate"] = true }));
            await RefusedAsync(() => Admin.DeleteAsync("sdkmessage", rows.Message));
            await RefusedAsync(() => Admin.UpdateAsync(new Entity("sdkmessagefilter", rows.Filter) { ["isvisible"] = false }));
            await RefusedAsync(() => Admin.DeleteAsync("sdkmessagefilter", rows.Filter));
            await WebRefusedAsync(HttpMethod.Patch, $"sdkmessages({rows.Message})", """{ "isprivate": true }""");
            await WebRefusedAsync(HttpMethod.Delete, $"sdkmessages({rows.Message})", null);
            await WebRefusedAsync(HttpMethod.Patch, $"sdkmessagefilters({rows.Filter})", """{ "isvisible": false }""");
            await WebRefusedAsync(HttpMethod.Delete, $"sdkmessagefilters({rows.Filter})", null);

            // A new filter on the API's message, and a message that claims to be an API's.
            await RefusedAsync(() => Admin.CreateAsync(new Entity("sdkmessagefilter")
            {
                ["name"] = "extra", ["sdkmessageid"] = new EntityReference("sdkmessage", rows.Message), ["primaryobjecttypecode"] = "account",
            }));
            await RefusedAsync(() => Admin.CreateAsync(new Entity("sdkmessage") { ["name"] = "new_Forged", ["categoryname"] = "CustomAPI" }));

            (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE sdkmessageid = @id AND isprivate = 0", ("@id", rows.Message))).Should().Be(1);
            (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessageid = @id", ("@id", rows.Message))).Should().Be(1);
            (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE name = N'new_Forged'")).Should().Be(0);

            // Reads work.
            (await Admin.RetrieveAsync("sdkmessage", rows.Message, new ColumnSet("name"))).GetAttributeValue<string>("name")
                .Should().Be("new_MessageWrites");
        }
        finally
        {
            await DeleteApiAsync(rows);
        }
    }

    [Fact]
    public async Task Custom_action_message_can_still_be_created()
    {
        var id = SdkMessageCatalog.MessageId("new_StillAnAction");
        await Admin.CreateAsync(new Entity("sdkmessage", id) { ["name"] = "new_StillAnAction", ["categoryname"] = "CustomAction" });
        var filter = await Admin.CreateAsync(new Entity("sdkmessagefilter")
        {
            ["name"] = "new_StillAnAction filter", ["sdkmessageid"] = new EntityReference("sdkmessage", id), ["primaryobjecttypecode"] = "none",
        });
        await Admin.UpdateAsync(new Entity("sdkmessagefilter", filter) { ["isvisible"] = false });
        await Admin.UpdateAsync(new Entity("sdkmessage", id) { ["isprivate"] = true });
        await Admin.DeleteAsync("sdkmessagefilter", filter);
        await Admin.DeleteAsync("sdkmessage", id);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE sdkmessageid = @id", ("@id", id))).Should().Be(0);
    }

    [Fact]
    public async Task Custom_action_message_cannot_be_renamed_to_an_api_name()
    {
        var rows = await InsertApiAsync("new_RenameTarget");
        var id = SdkMessageCatalog.MessageId("new_RenamedAction");
        await Admin.CreateAsync(new Entity("sdkmessage", id) { ["name"] = "new_RenamedAction", ["categoryname"] = "CustomAction" });
        try
        {
            // Steps match messages by name: the renamed action would take over the API's message.
            await RefusedAsync(() => Admin.UpdateAsync(new Entity("sdkmessage", id) { ["name"] = "new_RenameTarget" }));
            await RefusedAsync(() => Admin.UpdateAsync(new Entity("sdkmessage", id) { ["name"] = "NEW_renametarget" }));
            await WebRefusedAsync(HttpMethod.Patch, $"sdkmessages({id})", """{ "name": "new_RenameTarget" }""");
            (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE sdkmessageid = @id AND name = N'new_RenamedAction'", ("@id", id)))
                .Should().Be(1);

            // A free name is still fine.
            await Admin.UpdateAsync(new Entity("sdkmessage", id) { ["name"] = "new_RenamedAgain" });
            (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE sdkmessageid = @id AND name = N'new_RenamedAgain'", ("@id", id)))
                .Should().Be(1);
        }
        finally
        {
            await Admin.DeleteAsync("sdkmessage", id);
            await DeleteApiAsync(rows);
        }
    }

    [Fact]
    public async Task The_internal_caller_is_not_refused()
    {
        var cache = Cache(fx);
        var system = await cache.SystemUserIdAsync(default);
        var rows = await InsertApiAsync("new_InternalCaller");
        try
        {
            var create = new CreateRequest { Target = new Entity("sdkmessage") { ["name"] = "new_Forged", ["categoryname"] = "CustomAPI" } };
            var delete = new DeleteRequest { Target = new EntityReference("sdkmessage", rows.Message) };
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            var session = new DataSession(conn, null);

            foreach (var request in new OrganizationRequest[] { create, delete })
            {
                await SystemTableRules.EnforceAsync(request, Caller.Internal, system, cache, Store, session, default);
                var systemCaller = new Caller(system, await RootBusinessUnitAsync(fx));
                await RefusedAsync(() => SystemTableRules.EnforceAsync(request, systemCaller, system, cache, Store, session, default));
            }
        }
        finally
        {
            await DeleteApiAsync(rows);
        }
    }

    private static readonly (string Table, string Set)[] Tables =
    [
        ("customapi", "customapis"), ("customapirequestparameter", "customapirequestparameters"),
        ("customapiresponseproperty", "customapiresponseproperties"),
    ];

    private static async Task RefusedAsync(Func<Task> act)
    {
        var fault = await FaultAsync(act);
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain(Refusal);
    }

    private async Task WebRefusedAsync(HttpMethod method, string path, string? body)
    {
        var (status, text) = await SendAsync(method, path, body, fx.AdminObjectId);
        status.Should().Be(HttpStatusCode.BadRequest, $"{method} {path}: {text}");
        System.Text.Json.Nodes.JsonNode.Parse(text)!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203", $"{method} {path}: {text}");
        text.Should().Contain(Refusal);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, string? body, Guid objectId)
    {
        var request = new HttpRequestMessage(method, $"/api/data/v9.2/{path}");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed record ApiRows(Guid Api, Guid Parameter, Guid Property, Guid Message, Guid Filter, Guid Owner);

    /// <summary>An API with one parameter and one property, its message and its filter, written with SQL as the system path does.</summary>
    private async Task<ApiRows> InsertApiAsync(string uniqueName)
    {
        var owner = (Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", ("@oid", fx.AdminObjectId)))!;
        var bu = await RootBusinessUnitAsync(fx);
        var rows = new ApiRows(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SdkMessageCatalog.MessageId(uniqueName),
            SdkMessageCatalog.FilterId(uniqueName, "none"), owner);
        await ExecAsync("""
            INSERT INTO data.sdkmessage (sdkmessageid, name, categoryname, isprivate, createdon, modifiedon, statecode, statuscode)
            VALUES (@message, @name, N'CustomAPI', 0, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1);
            INSERT INTO data.sdkmessagefilter (sdkmessagefilterid, name, sdkmessageid, primaryobjecttypecode, iscustomprocessingstepallowed,
                createdon, modifiedon, statecode, statuscode)
            VALUES (@filter, @name, @message, N'none', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1);
            INSERT INTO data.customapi (customapiid, name, uniquename, bindingtype, allowedcustomprocessingsteptype, isfunction, isprivate,
                workflowsdkstepenabled, sdkmessageid, ownerid, owneridtype, owninguser, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            VALUES (@api, @name, @name, 0, 2, 0, 0, 0, @message, @owner, 8, @owner, @bu, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1);
            INSERT INTO data.customapirequestparameter (customapirequestparameterid, name, uniquename, customapiid, type, isoptional,
                ownerid, owneridtype, owninguser, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            VALUES (@parameter, N'Text', N'Text', @api, 10, 0, @owner, 8, @owner, @bu, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1);
            INSERT INTO data.customapiresponseproperty (customapiresponsepropertyid, name, uniquename, customapiid, type,
                ownerid, owneridtype, owninguser, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            VALUES (@property, N'Result', N'Result', @api, 10, @owner, 8, @owner, @bu, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1);
            """, ("@message", rows.Message), ("@filter", rows.Filter), ("@api", rows.Api), ("@parameter", rows.Parameter),
            ("@property", rows.Property), ("@name", uniqueName), ("@owner", owner), ("@bu", bu));
        return rows;
    }

    private Task DeleteApiAsync(ApiRows rows) => ExecAsync("""
        DELETE FROM data.customapirequestparameter WHERE customapiid = @api;
        DELETE FROM data.customapiresponseproperty WHERE customapiid = @api;
        DELETE FROM data.customapi WHERE customapiid = @api;
        DELETE FROM data.sdkmessagefilter WHERE sdkmessageid = @message;
        DELETE FROM data.sdkmessage WHERE sdkmessageid = @message;
        """, ("@api", rows.Api), ("@message", rows.Message));

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
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

    private async Task<Dictionary<string, int>> RolePrivilegesAsync(Guid roleId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, """
            SELECT p.name, rp.privilegedepthmask FROM data.roleprivileges AS rp JOIN data.privilege AS p ON p.privilegeid = rp.privilegeid
            WHERE rp.roleid = @role
            """);
        cmd.Parameters.AddWithValue("@role", roleId);
        var rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) rows.Add(rd.GetString(0), rd.GetInt32(1));
        return rows;
    }
}
