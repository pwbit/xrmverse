using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Custom APIs authored outside a solution import (plan 3.1, task 2): a Create of <c>customapi</c> checks the definition as
/// the import does and writes the API's <c>sdkmessage</c> and <c>sdkmessagefilter</c>; an Update changes only the mutable
/// columns; a Delete removes the parameters, properties, filter and message, unless a step is registered on the message.
/// Task 3: request parameters and response properties are created under the import's field rules, keep their immutable
/// columns (Associate and Disassociate included), and a managed API, or its fields, is not changed.
/// </summary>
[Collection("solution-import")]
public sealed class CustomApiAuthoringTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Echo = "Xrmd.TestPlugins.CustomApiEchoPlugin";

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
    }

    public Task DisposeAsync() => SolutionImportTests.ClearImportAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static Entity Api(string uniqueName, int binding = 0, string? bound = null, int stepType = 0, bool isFunction = false)
    {
        var api = new Entity(CustomApiTables.Api)
        {
            ["uniquename"] = uniqueName,
            ["name"] = uniqueName,
            ["bindingtype"] = new OptionSetValue(binding),
            ["isfunction"] = isFunction,
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(stepType),
        };
        if (bound is not null) api["boundentitylogicalname"] = bound;
        return api;
    }

    private static async Task<XrmdFaultException> InvalidAsync(Func<Task> act, params string[] contains)
    {
        var fault = await FaultAsync(act);
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument, fault.ToString());
        foreach (var text in contains) fault.Message.Should().Contain(text);
        return fault;
    }

    private async Task<Guid> PluginTypeAsync(string typeName = Echo)
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        return await PluginTestHelper.RegisterTypeAsync(Admin, assembly, typeName);
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt32(await ScalarAsync(sql, parameters));

    // ---- Create -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_global_action_writes_message_and_filter()
    {
        var id = await Admin.CreateAsync(Api("new_Ping"));

        var messageId = SdkMessageCatalog.MessageId("new_Ping");
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE name = N'new_Ping'")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE sdkmessageid = @id AND name = N'new_Ping' AND categoryname = N'CustomAPI' AND isprivate = 0",
            ("@id", messageId))).Should().Be(1);
        (await CountAsync("""
            SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessagefilterid = @filter AND sdkmessageid = @id
                AND primaryobjecttypecode = N'none' AND secondaryobjecttypecode = N'none' AND isvisible = 1 AND iscustomprocessingstepallowed = 0
            """, ("@filter", SdkMessageCatalog.FilterId("new_Ping", "none")), ("@id", messageId))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessageid = @id", ("@id", messageId))).Should().Be(1);
        var row = await Admin.RetrieveAsync(CustomApiTables.Api, id, new ColumnSet("sdkmessageid"));
        row.GetAttributeValue<EntityReference>("sdkmessageid").Id.Should().Be(messageId);
    }

    [Fact]
    public async Task Create_defaults_flags_and_options_left_out()
    {
        var id = await Admin.CreateAsync(new Entity(CustomApiTables.Api) { ["uniquename"] = "new_Bare", ["name"] = "new_Bare" });
        var parameter = await Admin.CreateAsync(new Entity(CustomApiTables.RequestParameter)
        {
            ["uniquename"] = "Text", ["name"] = "Text", ["type"] = new OptionSetValue((int)CustomApiType.String),
            ["customapiid"] = new EntityReference(CustomApiTables.Api, id),
        });

        // Stored as an import stores them, not NULL.
        (await CountAsync("""
            SELECT COUNT(*) FROM data.customapi WHERE customapiid = @id AND isprivate = 0 AND workflowsdkstepenabled = 0 AND isfunction = 0
                AND bindingtype = 0 AND allowedcustomprocessingsteptype = 0
            """, ("@id", id))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.customapirequestparameter WHERE customapirequestparameterid = @id AND isoptional = 0",
            ("@id", parameter))).Should().Be(1);
        var row = await Admin.RetrieveAsync(CustomApiTables.Api, id, new ColumnSet("isfunction", "bindingtype", "allowedcustomprocessingsteptype"));
        row.GetAttributeValue<bool>("isfunction").Should().BeFalse();
        row.GetAttributeValue<OptionSetValue>("bindingtype").Value.Should().Be(0);
        row.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype").Value.Should().Be(0);
        (await Admin.RetrieveAsync(CustomApiTables.RequestParameter, parameter, new ColumnSet("isoptional")))
            .GetAttributeValue<bool>("isoptional").Should().BeFalse();
    }

    [Fact]
    public async Task Create_entity_bound_on_account()
    {
        await Admin.CreateAsync(Api("new_PingAccount", binding: 1, bound: "account", stepType: 2));

        (await CountAsync("""
            SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessagefilterid = @filter AND primaryobjecttypecode = N'account'
                AND iscustomprocessingstepallowed = 1
            """, ("@filter", SdkMessageCatalog.FilterId("new_PingAccount", "account")))).Should().Be(1);
    }

    [Fact]
    public async Task Create_refuses_sdkmessageid_given()
    {
        var api = Api("new_Ping");
        api["sdkmessageid"] = new EntityReference("sdkmessage", Guid.NewGuid());
        await InvalidAsync(() => Admin.CreateAsync(api), "Custom API 'new_Ping' cannot be created", "sdkmessageid");
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE uniquename = N'new_Ping'")).Should().Be(0);
    }

    [Fact]
    public async Task Create_refuses_existing_message_name()
    {
        await Admin.CreateAsync(new Entity("sdkmessage", SdkMessageCatalog.MessageId("new_Old")) { ["name"] = "new_Old", ["categoryname"] = "CustomAction" });

        await InvalidAsync(() => Admin.CreateAsync(Api("new_Old")), "Custom API 'new_Old' cannot be created",
            "the organization already has the message 'new_Old'");
        await InvalidAsync(() => Admin.CreateAsync(Api("NEW_OLD")), "Custom API 'NEW_OLD' cannot be created", "'new_Old'");
        // Final review I4: trailing spaces are ignored as SQL Server's collation ignores them, on both engines.
        await InvalidAsync(() => Admin.CreateAsync(Api("New_Old  ")), "'new_Old'");
    }

    [Fact]
    public async Task Create_refuses_reserved_name()
    {
        await InvalidAsync(() => Admin.CreateAsync(Api("Create")), "Custom API 'Create' cannot be created",
            "'Create' is the name of a message xrmverse handles itself");
        await Admin.CreateAsync(Api("new_Create"));
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE name = N'new_Create'")).Should().Be(1);
    }

    [Fact]
    public async Task Create_refuses_unknown_prefix()
    {
        await InvalidAsync(() => Admin.CreateAsync(Api("zz_Ping")), "Custom API 'zz_Ping' cannot be created",
            "does not start with the prefix of an existing publisher");
    }

    [Fact]
    public async Task Create_refuses_bad_characters()
    {
        await InvalidAsync(() => Admin.CreateAsync(Api("new_Pi-ng")), "Custom API 'new_Pi-ng' cannot be created",
            "letters, digits and '_' only");
    }

    [Fact]
    public async Task Create_refuses_unknown_plugin_type()
    {
        var unknown = Guid.NewGuid();
        var api = Api("new_Ping");
        api["plugintypeid"] = new EntityReference("plugintype", unknown);
        await InvalidAsync(() => Admin.CreateAsync(api), "Custom API 'new_Ping' cannot be created", $"'{unknown}'", "plugintypeid");
    }

    [Fact]
    public async Task Create_refuses_workflow_activity_type()
    {
        var type = await PluginTypeAsync();
        await ScalarAsync("UPDATE data.plugintype SET isworkflowactivity = 1 WHERE plugintypeid = @id", ("@id", type));
        var api = Api("new_Ping");
        api["plugintypeid"] = new EntityReference("plugintype", type);
        await InvalidAsync(() => Admin.CreateAsync(api), "Custom API 'new_Ping' cannot be created", "is a workflow activity");

        // The plug-in type can change after Create, under the same rules.
        var id = await Admin.CreateAsync(Api("new_Ping"));
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["plugintypeid"] = new EntityReference("plugintype", type) }),
            "Custom API 'new_Ping' cannot be updated", "is a workflow activity");
        var unknown = Guid.NewGuid();
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["plugintypeid"] = new EntityReference("plugintype", unknown) }),
            "Custom API 'new_Ping' cannot be updated", $"'{unknown}'", "does not exist");
    }

    [Fact]
    public async Task Create_with_a_plugin_type_is_accepted()
    {
        var type = await PluginTypeAsync();
        var api = Api("new_Ping");
        api["plugintypeid"] = new EntityReference("plugintype", type);
        var id = await Admin.CreateAsync(api);
        (await Admin.RetrieveAsync(CustomApiTables.Api, id, new ColumnSet("plugintypeid"))).GetAttributeValue<EntityReference>("plugintypeid").Id
            .Should().Be(type);
    }

    [Fact]
    public async Task Create_refuses_function_enabled_for_workflow()
    {
        var api = Api("new_Ping", isFunction: true);
        api["workflowsdkstepenabled"] = true;
        await InvalidAsync(() => Admin.CreateAsync(api), "Custom API 'new_Ping' cannot be created", "enabled for workflows");
    }

    [Fact]
    public async Task Create_refuses_bad_binding()
    {
        await InvalidAsync(() => Admin.CreateAsync(Api("new_Ping", binding: 1)), "Custom API 'new_Ping' cannot be created",
            "needs a boundentitylogicalname");
        await InvalidAsync(() => Admin.CreateAsync(Api("new_Ping", binding: 1, bound: "new_nothing")), "'new_nothing'");
        await InvalidAsync(() => Admin.CreateAsync(Api("new_Ping", bound: "account")), "is global");
    }

    [Fact]
    public async Task Create_refuses_name_equal_to_entity_set()
    {
        // An entity set name carries no publisher prefix: the entity set rule runs before the name rules, as at import.
        await InvalidAsync(() => Admin.CreateAsync(Api("accounts")), "Custom API 'accounts' cannot be created",
            "'accounts' is the entity set of table 'account'");
    }

    [Fact]
    public async Task Create_refuses_duplicate_name_ignoring_case()
    {
        await Admin.CreateAsync(Api("new_Ping"));
        await InvalidAsync(() => Admin.CreateAsync(Api("NEW_PING")), "Custom API 'NEW_PING' already exists.");
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE uniquename = N'new_Ping'")).Should().Be(1);
    }

    [Fact]
    public async Task Create_refuses_related_entities()
    {
        foreach (var table in CustomApiTables.All)
        {
            var target = table == CustomApiTables.Api ? Api("new_Ping") : new Entity(table) { ["uniquename"] = "P", ["name"] = "P" };
            target.RelatedEntities[new Relationship(CustomApiTables.RequestParametersRelationship)] =
                new EntityCollection([new Entity(CustomApiTables.RequestParameter) { ["uniquename"] = "P", ["name"] = "P" }]);
            await InvalidAsync(() => Admin.CreateAsync(target),
                $"Creating related records in the same request is not supported for {table}: create the request parameters and response properties with their own Create.");
        }
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE uniquename = N'new_Ping'")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE name = N'new_Ping'")).Should().Be(0);
    }

    [Fact]
    public async Task Web_api_deep_insert_is_refused()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/customapis")
        {
            Content = new StringContent("""
                { "uniquename": "new_Ping", "name": "new_Ping", "bindingtype": 0, "isfunction": false, "allowedcustomprocessingsteptype": 0,
                  "CustomAPIRequestParameters": [ { "uniquename": "P", "name": "P", "type": 10 } ] }
                """, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE uniquename = N'new_Ping'")).Should().Be(0);
    }

    [Fact]
    public async Task Failed_create_leaves_no_message()
    {
        var api = Api("new_Ping");
        api["plugintypeid"] = new EntityReference("plugintype", Guid.NewGuid());
        await InvalidAsync(() => Admin.CreateAsync(api));
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE name = N'new_Ping'")).Should().Be(0);

        // A failure after the message and filter are written (a PostOperation step of the Create) rolls them back too.
        var thrower = await PluginTypeAsync("Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, thrower, "Create", CustomApiTables.Api, 40, config: "invalid:after the core");
        (await FaultAsync(() => Admin.CreateAsync(Api("new_Ping")))).Message.Should().Contain("after the core");
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE name = N'new_Ping'")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessageid = @id", ("@id", SdkMessageCatalog.MessageId("new_Ping"))))
            .Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE uniquename = N'new_Ping'")).Should().Be(0);
    }

    [Fact]
    public async Task Basic_user_cannot_create()
    {
        var objectId = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);

        var fault = await FaultAsync(() => ServiceFor(fx, objectId).CreateAsync(Api("new_Ping")));
        fault.HttpStatus.Should().Be(403, fault.ToString());
        fault.ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE name = N'new_Ping'")).Should().Be(0);
    }

    // ---- Update -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_mutable_columns()
    {
        var id = await Admin.CreateAsync(Api("new_Ping"));

        await Admin.UpdateAsync(new Entity(CustomApiTables.Api, id)
        {
            ["displayname"] = "Ping!", ["isprivate"] = true, ["executeprivilegename"] = "prvReadaccount", ["name"] = "Ping", ["description"] = "d",
        });

        var row = await Admin.RetrieveAsync(CustomApiTables.Api, id, new ColumnSet("displayname", "isprivate", "executeprivilegename"));
        row.GetAttributeValue<string>("displayname").Should().Be("Ping!");
        row.GetAttributeValue<bool>("isprivate").Should().BeTrue();
        row.GetAttributeValue<string>("executeprivilegename").Should().Be("prvReadaccount");
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE sdkmessageid = @id AND isprivate = 1", ("@id", SdkMessageCatalog.MessageId("new_Ping"))))
            .Should().Be(1);
    }

    [Fact]
    public async Task Update_uniquename_case_change_is_refused()
    {
        var id = await Admin.CreateAsync(Api("new_Ping"));
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["uniquename"] = "new_PING" }),
            "uniquename of custom API 'new_Ping' cannot be changed once created.");
        (await CountAsync($"SELECT COUNT(*) FROM data.customapi WHERE {TestSql.Dialect.BinaryCollate("uniquename")} = N'new_Ping'")).Should().Be(1);
    }

    [Fact]
    public async Task Update_bindingtype_is_refused()
    {
        var id = await Admin.CreateAsync(Api("new_Ping"));
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["bindingtype"] = new OptionSetValue(1) }),
            "bindingtype of custom API 'new_Ping' cannot be changed once created.");
        foreach (var (column, value) in new (string, object?)[]
                 {
                     ("boundentitylogicalname", "account"), ("isfunction", true), ("allowedcustomprocessingsteptype", new OptionSetValue(2)),
                     ("workflowsdkstepenabled", true), ("sdkmessageid", new EntityReference("sdkmessage", SdkMessageCatalog.MessageId("Create"))),
                 })
            await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { [column] = value }),
                $"{column} of custom API 'new_Ping' cannot be changed once created.");
    }

    [Fact]
    public async Task Update_same_immutable_value_is_accepted()
    {
        var id = await Admin.CreateAsync(Api("new_Ping"));
        await Admin.UpdateAsync(new Entity(CustomApiTables.Api, id)
        {
            ["uniquename"] = "new_Ping", ["bindingtype"] = new OptionSetValue(0), ["boundentitylogicalname"] = null, ["isfunction"] = false,
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(0), ["workflowsdkstepenabled"] = false,
            ["sdkmessageid"] = new EntityReference("sdkmessage", SdkMessageCatalog.MessageId("new_Ping")), ["displayname"] = "Same",
        });
        (await Admin.RetrieveAsync(CustomApiTables.Api, id, new ColumnSet("displayname"))).GetAttributeValue<string>("displayname").Should().Be("Same");
    }

    [Fact]
    public async Task Update_same_bound_table_different_case_is_accepted()
    {
        var id = await Admin.CreateAsync(Api("new_PingAccount", binding: 1, bound: "Account"));
        (await CountAsync($"SELECT COUNT(*) FROM data.customapi WHERE customapiid = @id AND {TestSql.Dialect.BinaryCollate("boundentitylogicalname")} = N'account'",
            ("@id", id))).Should().Be(1);

        await Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["boundentitylogicalname"] = "Account", ["displayname"] = "Same table" });

        (await Admin.RetrieveAsync(CustomApiTables.Api, id, new ColumnSet("displayname"))).GetAttributeValue<string>("displayname")
            .Should().Be("Same table");
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["boundentitylogicalname"] = "Contact" }),
            "boundentitylogicalname of custom API 'new_PingAccount' cannot be changed once created.");
    }

    [Fact]
    public async Task Update_empty_bound_table_matches_null_is_accepted()
    {
        var id = await Admin.CreateAsync(Api("new_Ping"));

        await Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["boundentitylogicalname"] = "", ["displayname"] = "Empty" });

        (await Admin.RetrieveAsync(CustomApiTables.Api, id, new ColumnSet("displayname"))).GetAttributeValue<string>("displayname")
            .Should().Be("Empty");
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["boundentitylogicalname"] = "account" }),
            "boundentitylogicalname of custom API 'new_Ping' cannot be changed once created.");
    }

    [Fact]
    public async Task Update_same_field_table_different_case_or_empty_is_accepted()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        var withTable = await Admin.CreateAsync(Parameter(api, "Record", CustomApiType.EntityReference, table: "Account"));
        var withoutTable = await Admin.CreateAsync(Parameter(api, "Text", CustomApiType.String));

        await Admin.UpdateAsync(new Entity(CustomApiTables.RequestParameter, withTable) { ["logicalentityname"] = "ACCOUNT", ["displayname"] = "Record!" });
        await Admin.UpdateAsync(new Entity(CustomApiTables.RequestParameter, withoutTable) { ["logicalentityname"] = "", ["displayname"] = "Text!" });

        (await Admin.RetrieveAsync(CustomApiTables.RequestParameter, withTable, new ColumnSet("displayname"))).GetAttributeValue<string>("displayname")
            .Should().Be("Record!");
        (await Admin.RetrieveAsync(CustomApiTables.RequestParameter, withoutTable, new ColumnSet("displayname"))).GetAttributeValue<string>("displayname")
            .Should().Be("Text!");
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.RequestParameter, withTable) { ["logicalentityname"] = "contact" }),
            "logicalentityname of custom API 'new_Ping' request parameter 'Record' cannot be changed once created.");
    }

    // ---- Unknown privilege (warning) ----------------------------------------------------------------------------------

    [Fact]
    public async Task Create_and_update_warn_about_an_executeprivilegename_that_names_no_privilege()
    {
        var logger = new ListLogger<Xrmd.Pipeline.CustomApis.CustomApiAuthoring>();
        var service = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId, authoringLogger: logger);

        var known = Api("new_Known");
        known["executeprivilegename"] = "prvReadaccount";
        var id = await service.CreateAsync(known);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
        // Final review I4: the privilege is found ignoring case, on both engines.
        var cased = Api("new_KnownCased");
        cased["executeprivilegename"] = "PRVREADACCOUNT";
        await service.CreateAsync(cased);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);

        var unknown = Api("new_Unknown");
        unknown["executeprivilegename"] = "prvNoSuchPrivilege";
        await service.CreateAsync(unknown);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which.Message
            .Should().Contain("prvNoSuchPrivilege").And.Contain("new_Unknown").And.Contain("names no privilege");

        await service.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["executeprivilegename"] = "prvNoSuchOther" });
        logger.Entries.Count(e => e.Level == LogLevel.Warning).Should().Be(2);
        logger.Entries.Last().Message.Should().Contain("prvNoSuchOther").And.Contain("new_Known");
        (await service.RetrieveAsync(CustomApiTables.Api, id, new ColumnSet("executeprivilegename"))).GetAttributeValue<string>("executeprivilegename")
            .Should().Be("prvNoSuchOther");
    }

    // ---- Delete -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_removes_message_filter_and_children()
    {
        var id = await Admin.CreateAsync(Api("new_Ping"));
        var messageId = SdkMessageCatalog.MessageId("new_Ping");
        // Task 3 authors parameters and properties; here they are written as the internal path does.
        var owner = (Guid)(await ScalarAsync("SELECT ownerid FROM data.customapi WHERE customapiid = @id", ("@id", id)))!;
        var bu = await RootBusinessUnitAsync(fx);
        await ScalarAsync("""
            INSERT INTO data.customapirequestparameter (customapirequestparameterid, name, uniquename, customapiid, type, isoptional,
                ownerid, owneridtype, owninguser, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            VALUES (NEWID(), N'Text', N'Text', @api, 10, 0, @owner, 8, @owner, @bu, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1);
            INSERT INTO data.customapiresponseproperty (customapiresponsepropertyid, name, uniquename, customapiid, type,
                ownerid, owneridtype, owninguser, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            VALUES (NEWID(), N'Result', N'Result', @api, 10, @owner, 8, @owner, @bu, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1);
            """, ("@api", id), ("@owner", owner), ("@bu", bu));

        await Admin.DeleteAsync(CustomApiTables.Api, id);

        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE uniquename = N'new_Ping'")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.customapirequestparameter WHERE customapiid = @id", ("@id", id))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.customapiresponseproperty WHERE customapiid = @id", ("@id", id))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessageid = @id", ("@id", messageId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE sdkmessageid = @id OR name = N'new_Ping'", ("@id", messageId))).Should().Be(0);

        // The name is free again.
        await Admin.CreateAsync(Api("new_Ping"));
    }

    [Fact]
    public async Task Delete_refused_while_step_registered()
    {
        var id = await Admin.CreateAsync(Api("new_Ping", stepType: 2));
        var step = await PluginTestHelper.RegisterStepAsync(Admin, await PluginTypeAsync(), "new_Ping", null, 40);

        await InvalidAsync(() => Admin.DeleteAsync(CustomApiTables.Api, id),
            $"Custom API 'new_Ping' cannot be deleted: the step 'new_Ping::40:1' ({step}) is registered on its message. Delete the step first.");
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE uniquename = N'new_Ping'")).Should().Be(1);

        await Admin.DeleteAsync("sdkmessageprocessingstep", step);
        await Admin.DeleteAsync(CustomApiTables.Api, id);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE name = N'new_Ping'")).Should().Be(0);
    }

    [Fact]
    public async Task Direct_write_to_api_message_still_refused()
    {
        await Admin.CreateAsync(Api("new_Ping"));
        var messageId = SdkMessageCatalog.MessageId("new_Ping");
        await InvalidAsync(() => Admin.UpdateAsync(new Entity("sdkmessage", messageId) { ["isprivate"] = true }), "sdkmessage");
        await InvalidAsync(() => Admin.DeleteAsync("sdkmessagefilter", SdkMessageCatalog.FilterId("new_Ping", "none")), "sdkmessagefilter");
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE sdkmessageid = @id AND isprivate = 0", ("@id", messageId))).Should().Be(1);
    }
    // ---- Update of an API (plan 3.1 task 3 rulings) -------------------------------------------------------------------

    [Fact]
    public async Task Update_with_unknown_sdkmessageid_is_refused()
    {
        var id = await Admin.CreateAsync(Api("new_Ping"));
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["sdkmessageid"] = new EntityReference("sdkmessage", Guid.NewGuid()) }),
            "sdkmessageid of custom API 'new_Ping' cannot be changed once created.");
    }

    [Fact]
    public async Task Update_managed_api_is_refused()
    {
        var id = await Admin.CreateAsync(Api("new_Ping"));
        await MakeManagedAsync(id);
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["displayname"] = "Changed" }),
            "Custom API 'new_Ping' is managed: change it in its solution and import an update.");
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE customapiid = @id AND displayname IS NULL", ("@id", id))).Should().Be(1);
    }

    [Fact]
    public async Task Managed_import_makes_api_and_fields_read_only()
    {
        // A real managed import (plan 2.6 routing): the file says Managed 1, and the API is delivered with ismanaged 1.
        var (solution, customizations, files) = Xrmd.Solution.SolutionZip.Read(CustomApiTestSolution.Build("xt_managedapis",
            new TestApi("xt_Managed", PluginTypeName: Echo, Requests: [("Text", 10, false, null)], Responses: [("TextOut", 10)])));
        solution.Root!.Element("SolutionManifest")!.Element("Managed")!.Value = "1";
        var kept = files.Where(f => !f.Key.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        await Admin.ExecuteAsync(new Microsoft.Crm.Sdk.Messages.ImportSolutionRequest
        {
            CustomizationFile = Xrmd.Solution.SolutionZip.Write(solution, customizations, kept), ImportJobId = Guid.NewGuid(),
        });

        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = N'xt_managedapis' AND ismanaged = 1")).Should().Be(1);
        var id = (Guid)(await ScalarAsync("SELECT customapiid FROM data.customapi WHERE uniquename = N'xt_Managed' AND ismanaged = 1"))!;
        var parameter = (Guid)(await ScalarAsync("SELECT customapirequestparameterid FROM data.customapirequestparameter WHERE customapiid = @id", ("@id", id)))!;

        const string managed = "Custom API 'xt_Managed' is managed: change it in its solution and import an update.";
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.Api, id) { ["displayname"] = "Changed" }), managed);
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.RequestParameter, parameter) { ["displayname"] = "Changed" }), managed);
        await InvalidAsync(() => Admin.CreateAsync(Parameter(id, "More", CustomApiType.String)), managed);
        // The row Delete is the generic managed rule of plan 2.6.
        var fault = await FaultAsync(() => Admin.DeleteAsync(CustomApiTables.Api, id));
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.Message.Should().Contain("managed");
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE customapiid = @id", ("@id", id))).Should().Be(1);
    }

    // ---- Request parameters and response properties (task 3) ---------------------------------------------------------

    private static Entity Parameter(Guid? api, string uniqueName, CustomApiType type, bool? isOptional = false, string? table = null)
    {
        var parameter = new Entity(CustomApiTables.RequestParameter)
        {
            ["uniquename"] = uniqueName, ["name"] = uniqueName, ["type"] = new OptionSetValue((int)type),
        };
        if (api is { } id) parameter["customapiid"] = new EntityReference(CustomApiTables.Api, id);
        if (isOptional is { } optional) parameter["isoptional"] = optional;
        if (table is not null) parameter["logicalentityname"] = table;
        return parameter;
    }

    private static Entity Property(Guid api, string uniqueName, CustomApiType type, string? table = null)
    {
        var property = new Entity(CustomApiTables.ResponseProperty)
        {
            ["uniquename"] = uniqueName, ["name"] = uniqueName, ["type"] = new OptionSetValue((int)type),
            ["customapiid"] = new EntityReference(CustomApiTables.Api, api),
        };
        if (table is not null) property["logicalentityname"] = table;
        return property;
    }

    /// <summary>Marks the API as delivered by a managed solution, as a managed import leaves it.</summary>
    private Task MakeManagedAsync(Guid api) => ScalarAsync("UPDATE data.customapi SET ismanaged = 1 WHERE customapiid = @id", ("@id", api));

    [Fact]
    public async Task Create_parameter_and_property()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        var parameter = await Admin.CreateAsync(Parameter(api, "Text", CustomApiType.String));
        var property = await Admin.CreateAsync(Property(api, "Count", CustomApiType.Integer));

        (await CountAsync("SELECT COUNT(*) FROM data.customapirequestparameter WHERE customapirequestparameterid = @id AND customapiid = @api AND type = 10 AND isoptional = 0",
            ("@id", parameter), ("@api", api))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.customapiresponseproperty WHERE customapiresponsepropertyid = @id AND customapiid = @api AND type = 7",
            ("@id", property), ("@api", api))).Should().Be(1);
    }

    [Fact]
    public async Task Create_parameter_requires_customapiid()
    {
        await InvalidAsync(() => Admin.CreateAsync(Parameter(null, "Text", CustomApiType.String)), "customapiid is required");
        await InvalidAsync(() => Admin.CreateAsync(Parameter(Guid.NewGuid(), "Text", CustomApiType.String)), "customapiid", "does not exist");
        (await CountAsync("SELECT COUNT(*) FROM data.customapirequestparameter WHERE uniquename = N'Text'")).Should().Be(0);
    }

    [Fact]
    public async Task Create_parameter_refuses_duplicate_name_ignoring_case()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        await Admin.CreateAsync(Parameter(api, "Text", CustomApiType.String));
        await InvalidAsync(() => Admin.CreateAsync(Parameter(api, "TEXT", CustomApiType.String)), "Custom API 'new_Ping'", "'TEXT'",
            "more than once");
        // A property may share a parameter's name, and another API may use it.
        await Admin.CreateAsync(Property(api, "TEXT", CustomApiType.String));
        await Admin.CreateAsync(Parameter(await Admin.CreateAsync(Api("new_Pong")), "TEXT", CustomApiType.String));
        (await CountAsync("SELECT COUNT(*) FROM data.customapirequestparameter WHERE customapiid = @api", ("@api", api))).Should().Be(1);
    }

    [Fact]
    public async Task Create_parameter_refuses_target_on_entity_bound()
    {
        var api = await Admin.CreateAsync(Api("new_PingAccount", binding: 1, bound: "account"));
        await InvalidAsync(() => Admin.CreateAsync(Parameter(api, "Target", CustomApiType.EntityReference, table: "account")),
            "Custom API 'new_PingAccount'", "'Target'", "the server adds Target");
    }

    [Fact]
    public async Task Create_property_refuses_isoptional()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        var property = Property(api, "Count", CustomApiType.Integer);
        property["isoptional"] = false;
        // A response property has no isoptional column: the usual 400 for an unknown attribute, given before any rule runs.
        await InvalidAsync(() => Admin.CreateAsync(property), "Attribute 'isoptional' not found on entity 'customapiresponseproperty'.");
        (await CountAsync("SELECT COUNT(*) FROM data.customapiresponseproperty WHERE customapiid = @api", ("@api", api))).Should().Be(0);
    }

    [Fact]
    public async Task Create_property_refuses_logicalentityname_on_entitycollection()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        await InvalidAsync(() => Admin.CreateAsync(Property(api, "Rows", CustomApiType.EntityCollection, table: "account")),
            "Custom API 'new_Ping'", "response property 'Rows' has the logicalentityname 'account'");
        // A request parameter of that type may name a table.
        await Admin.CreateAsync(Parameter(api, "Rows", CustomApiType.EntityCollection, table: "account"));
    }

    [Fact]
    public async Task Create_parameter_refuses_unknown_table()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        await InvalidAsync(() => Admin.CreateAsync(Parameter(api, "Row", CustomApiType.EntityReference, table: "new_nothing")),
            "Custom API 'new_Ping'", "request parameter 'Row'", "'new_nothing'");
    }

    [Fact]
    public async Task Update_parameter_type_is_refused()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        var parameter = await Admin.CreateAsync(Parameter(api, "Text", CustomApiType.String));
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.RequestParameter, parameter) { ["type"] = new OptionSetValue(7) }),
            "type of custom API 'new_Ping' request parameter 'Text' cannot be changed once created.");
        var property = await Admin.CreateAsync(Property(api, "Count", CustomApiType.Integer));
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.ResponseProperty, property) { ["uniquename"] = "COUNT" }),
            "uniquename of custom API 'new_Ping' response property 'Count' cannot be changed once created.");
    }

    [Fact]
    public async Task Update_parameter_isoptional_is_refused()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        var parameter = await Admin.CreateAsync(Parameter(api, "Text", CustomApiType.String));
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.RequestParameter, parameter) { ["isoptional"] = true }),
            "isoptional of custom API 'new_Ping' request parameter 'Text' cannot be changed once created.");
    }

    [Fact]
    public async Task Update_parameter_customapiid_is_refused()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        var other = await Admin.CreateAsync(Api("new_Pong"));
        var parameter = await Admin.CreateAsync(Parameter(api, "Text", CustomApiType.String));
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.RequestParameter, parameter) { ["customapiid"] = new EntityReference(CustomApiTables.Api, other) }),
            "customapiid of custom API 'new_Ping' request parameter 'Text' cannot be changed once created.");
        // A missing API is the same refusal, not a 404.
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.RequestParameter, parameter) { ["customapiid"] = new EntityReference(CustomApiTables.Api, Guid.NewGuid()) }),
            "customapiid of custom API 'new_Ping' request parameter 'Text' cannot be changed once created.");
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.RequestParameter, parameter) { ["customapiid"] = null }),
            "customapiid of custom API 'new_Ping' request parameter 'Text' cannot be changed once created.");
        (await CountAsync("SELECT COUNT(*) FROM data.customapirequestparameter WHERE customapirequestparameterid = @id AND customapiid = @api",
            ("@id", parameter), ("@api", api))).Should().Be(1);
    }

    [Fact]
    public async Task Update_parameter_displayname_is_accepted()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        var parameter = await Admin.CreateAsync(Parameter(api, "Text", CustomApiType.String));
        await Admin.UpdateAsync(new Entity(CustomApiTables.RequestParameter, parameter)
        {
            ["displayname"] = "The text", ["name"] = "Text!", ["description"] = "d",
            ["uniquename"] = "Text", ["type"] = new OptionSetValue(10), ["isoptional"] = false, ["logicalentityname"] = null,
            ["customapiid"] = new EntityReference(CustomApiTables.Api, api),
        });
        (await Admin.RetrieveAsync(CustomApiTables.RequestParameter, parameter, new ColumnSet("displayname")))
            .GetAttributeValue<string>("displayname").Should().Be("The text");
    }

    [Fact]
    public async Task Delete_parameter_is_accepted()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        var parameter = await Admin.CreateAsync(Parameter(api, "Text", CustomApiType.String));
        var property = await Admin.CreateAsync(Property(api, "Count", CustomApiType.Integer));
        await Admin.DeleteAsync(CustomApiTables.RequestParameter, parameter);
        await Admin.DeleteAsync(CustomApiTables.ResponseProperty, property);
        (await CountAsync("SELECT COUNT(*) FROM data.customapirequestparameter WHERE customapiid = @api", ("@api", api))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.customapiresponseproperty WHERE customapiid = @api", ("@api", api))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE customapiid = @api", ("@api", api))).Should().Be(1);
    }

    [Fact]
    public async Task Create_parameter_on_managed_api_is_refused()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        var parameter = await Admin.CreateAsync(Parameter(api, "Text", CustomApiType.String));
        var property = await Admin.CreateAsync(Property(api, "Count", CustomApiType.Integer));
        await MakeManagedAsync(api);
        const string Managed = "Custom API 'new_Ping' is managed: change it in its solution and import an update.";

        await InvalidAsync(() => Admin.CreateAsync(Parameter(api, "Other", CustomApiType.String)), Managed);
        await InvalidAsync(() => Admin.CreateAsync(Property(api, "Other", CustomApiType.String)), Managed);
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.RequestParameter, parameter) { ["displayname"] = "x" }), Managed);
        await InvalidAsync(() => Admin.UpdateAsync(new Entity(CustomApiTables.ResponseProperty, property) { ["displayname"] = "x" }), Managed);
        await InvalidAsync(() => Admin.DeleteAsync(CustomApiTables.RequestParameter, parameter), Managed);
        await InvalidAsync(() => Admin.DeleteAsync(CustomApiTables.ResponseProperty, property), Managed);
        (await CountAsync("SELECT COUNT(*) FROM data.customapirequestparameter WHERE customapiid = @api", ("@api", api))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.customapiresponseproperty WHERE customapiid = @api", ("@api", api))).Should().Be(1);
    }

    [Fact]
    public async Task Associate_parameter_to_other_api_is_refused()
    {
        var api = await Admin.CreateAsync(Api("new_Ping"));
        var other = await Admin.CreateAsync(Api("new_Pong"));
        var parameter = await Admin.CreateAsync(Parameter(api, "Text", CustomApiType.String));
        var property = await Admin.CreateAsync(Property(api, "Count", CustomApiType.Integer));

        await InvalidAsync(() => Task.Run(() => Admin.Associate(CustomApiTables.Api, other, new Relationship(CustomApiTables.RequestParametersRelationship),
                new EntityReferenceCollection { new(CustomApiTables.RequestParameter, parameter) })),
            "customapiid of custom API 'new_Ping' request parameter 'Text' cannot be changed once created.");
        await InvalidAsync(() => Task.Run(() => Admin.Disassociate(CustomApiTables.Api, api, new Relationship(CustomApiTables.RequestParametersRelationship),
                new EntityReferenceCollection { new(CustomApiTables.RequestParameter, parameter) })),
            "customapiid of custom API 'new_Ping' request parameter 'Text' cannot be changed once created.");
        await InvalidAsync(() => Task.Run(() => Admin.Associate(CustomApiTables.Api, other, new Relationship(CustomApiTables.ResponsePropertiesRelationship),
                new EntityReferenceCollection { new(CustomApiTables.ResponseProperty, property) })),
            "customapiid of custom API 'new_Ping' response property 'Count' cannot be changed once created.");
        (await CountAsync("SELECT COUNT(*) FROM data.customapirequestparameter WHERE customapiid = @api", ("@api", api))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.customapiresponseproperty WHERE customapiid = @api", ("@api", api))).Should().Be(1);
    }

    [Fact]
    public async Task Added_required_parameter_is_enforced_at_once()
    {
        var api = Api("new_Echo");
        api["plugintypeid"] = new EntityReference("plugintype", await PluginTypeAsync());
        var id = await Admin.CreateAsync(api);
        await Admin.ExecuteAsync(new OrganizationRequest("new_Echo"));

        await Admin.CreateAsync(Parameter(id, "Text", CustomApiType.String, isOptional: false));

        await InvalidAsync(() => Admin.ExecuteAsync(new OrganizationRequest("new_Echo")), "'Text'");
        (await Admin.ExecuteAsync(new OrganizationRequest("new_Echo") { ["Text"] = "x" })).Should().NotBeNull();
    }
}
