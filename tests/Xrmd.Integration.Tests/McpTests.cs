using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xrmd.Abstractions;
using Xrmd.Admin;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.Mcp;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The MCP server on <c>/mcp</c> (streamable HTTP, stateless), driven by the SDK's <see cref="McpClient"/> over the test
/// host: every tool runs through the Core as the token's user.
/// </summary>
[Collection("xrmd")]
public sealed class McpTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string AdminRequired = "The Admin API requires the System Administrator role.";

    private readonly string _prefix = "mcp-" + Guid.NewGuid().ToString("N")[..8] + "-";
    private Guid _readerOid;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(Store);
        // A user whose only role reads projects and solutions: no create, no System Administrator.
        _readerOid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), _readerOid);
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "mcp-" + Guid.NewGuid().ToString("N")[..8],
            ("prvReadpb_Project", DepthMask.Global), ("prvReadSolution", DepthMask.Global)));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal static async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> app, string baseUrl, Guid oid)
    {
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, baseUrl));
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(baseUrl + "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private Task<McpClient> ConnectAsync(Guid oid) => ConnectAsync(fx.App, fx.BaseUrl, oid);

    private Task<McpClient> AdminAsync() => ConnectAsync(fx.AdminObjectId);

    internal static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static async Task<JsonObject> CallJsonAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        result.IsError.Should().NotBe(true, Text(result));
        return JsonNode.Parse(Text(result))!.AsObject();
    }

    private async Task CreateAccountsAsync(McpClient client, params string[] suffixes)
    {
        foreach (var suffix in suffixes)
            await CallJsonAsync(client, "create_record", new() { ["table"] = TestEntities.Project, ["data"] = new Dictionary<string, object?> { ["pb_name"] = _prefix + suffix } });
    }

    [Fact]
    public async Task Lists_the_twenty_six_tools()
    {
        await using var client = await AdminAsync();
        var tools = await client.ListToolsAsync();
        tools.Select(t => t.Name).Should().BeEquivalentTo(
            "list_tables", "describe_table", "query", "create_record", "update_record", "delete_record",
            "list_solutions", "list_component_layers", "export_solution", "apply_schema", "get_plugin_traces", "get_async_jobs", "get_audit_history", "list_custom_apis", "list_glossary_terms",
            "apply_workflow", "export_workflow", "list_workflows", "set_workflow_state",
            "migrate_inspect_package", "migrate_start_import", "migrate_get_status", "migrate_get_report", "migrate_cancel", "migrate_finalize", "migrate_activate");
    }

    [Fact]
    public async Task Query_fetchxml_pages_with_cookie()
    {
        await using var client = await AdminAsync();
        await CreateAccountsAsync(client, "a", "b", "c");
        var fetch = $"""
            <fetch><entity name="pb_project"><attribute name="pb_name" /><order attribute="pb_name" />
            <filter><condition attribute="pb_name" operator="like" value="{_prefix}%" /></filter></entity></fetch>
            """;

        var first = await CallJsonAsync(client, "query", new() { ["fetchXml"] = fetch, ["pageSize"] = 2 });
        first["records"]!.AsArray().Select(r => r!["pb_name"]!.GetValue<string>()).Should().Equal(_prefix + "a", _prefix + "b");
        first["moreRecords"]!.GetValue<bool>().Should().BeTrue();
        var cookie = first["pagingCookie"]!.GetValue<string>();
        cookie.Should().NotBeNullOrEmpty();

        var second = await CallJsonAsync(client, "query", new() { ["fetchXml"] = fetch, ["pageSize"] = 2, ["pagingCookie"] = cookie });
        second["records"]!.AsArray().Select(r => r!["pb_name"]!.GetValue<string>()).Should().Equal(_prefix + "c");
        second["moreRecords"]!.GetValue<bool>().Should().BeFalse();
        second["pagingCookie"].Should().BeNull();

        var tooLarge = await client.CallToolAsync("query", new Dictionary<string, object?> { ["fetchXml"] = fetch, ["pageSize"] = 501 });
        tooLarge.IsError.Should().BeTrue();
        Text(tooLarge).Should().Contain("500");
    }

    [Fact]
    public async Task Query_odata_uses_web_api_syntax()
    {
        await using var client = await AdminAsync();
        await CreateAccountsAsync(client, "x", "y", "z");

        var odata = $"pb_projects?$select=pb_name&$filter=startswith(pb_name,'{_prefix}')&$orderby=pb_name desc";
        var first = await CallJsonAsync(client, "query", new() { ["odata"] = odata, ["pageSize"] = 2 });
        var records = first["records"]!.AsArray();
        records.Select(r => r!["pb_name"]!.GetValue<string>()).Should().Equal(_prefix + "z", _prefix + "y");
        records[0]!.AsObject().ContainsKey("pb_projectid").Should().BeTrue();
        first["moreRecords"]!.GetValue<bool>().Should().BeTrue();

        var second = await CallJsonAsync(client, "query",
            new() { ["odata"] = odata, ["pageSize"] = 2, ["pagingCookie"] = first["pagingCookie"]!.GetValue<string>() });
        second["records"]!.AsArray().Select(r => r!["pb_name"]!.GetValue<string>()).Should().Equal(_prefix + "x");
        second["moreRecords"]!.GetValue<bool>().Should().BeFalse();

        // Same validation as the Web API: an unsupported option is an error naming it, not a transport failure.
        var invalid = await client.CallToolAsync("query", new Dictionary<string, object?> { ["odata"] = "pb_projects?$skip=1" });
        invalid.IsError.Should().BeTrue();
        Text(invalid).Should().Contain("0x80040203").And.Contain("$skip");

        var both = await client.CallToolAsync("query", new Dictionary<string, object?> { ["odata"] = "pb_projects", ["fetchXml"] = "<fetch />" });
        both.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Create_update_delete_record_as_caller()
    {
        await using var client = await AdminAsync();
        var created = await CallJsonAsync(client, "create_record",
            new() { ["table"] = TestEntities.Project, ["data"] = new Dictionary<string, object?> { ["pb_name"] = _prefix + "crud" } });
        var id = created["id"]!.GetValue<string>();

        await CallJsonAsync(client, "update_record",
            new() { ["table"] = TestEntities.Project, ["id"] = id, ["data"] = new Dictionary<string, object?> { ["pb_name"] = _prefix + "crud2" } });
        var read = await CallJsonAsync(client, "query", new() { ["odata"] = $"pb_projects?$select=pb_name&$filter=pb_projectid eq {id}" });
        read["records"]![0]!["pb_name"]!.GetValue<string>().Should().Be(_prefix + "crud2");

        await CallJsonAsync(client, "delete_record", new() { ["table"] = TestEntities.Project, ["id"] = id });
        var gone = await client.CallToolAsync("delete_record", new Dictionary<string, object?> { ["table"] = TestEntities.Project, ["id"] = id });
        gone.IsError.Should().BeTrue();
        Text(gone).Should().Contain("0x80040217");
    }

    /// <summary>Plan 2.10 (ruling X16): the generic record tools go through the Core, which sets the currency, rate and base values.</summary>
    [Fact]
    public async Task Mcp_create_record_with_money_sets_base()
    {
        await using var client = await AdminAsync();
        var created = await CallJsonAsync(client, "create_record",
            new() { ["table"] = TestEntities.Project, ["data"] = new Dictionary<string, object?> { ["pb_name"] = _prefix + "money", ["pb_budget"] = 250.5m } });
        var id = created["id"]!.GetValue<string>();

        var read = await CallJsonAsync(client, "query",
            new() { ["odata"] = $"pb_projects?$select=pb_budget,pb_budget_base,exchangerate,_transactioncurrencyid_value&$filter=pb_projectid eq {id}" });

        var record = read["records"]![0]!;
        record["pb_budget"]!.GetValue<decimal>().Should().Be(250.5m);
        record["pb_budget_base"]!.GetValue<decimal>().Should().Be(250.5m); // the base currency: rate 1
        record["exchangerate"]!.GetValue<decimal>().Should().Be(1m);
        record["_transactioncurrencyid_value"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Create_record_respects_caller_security()
    {
        await using var client = await ConnectAsync(_readerOid);
        var result = await client.CallToolAsync("create_record",
            new Dictionary<string, object?> { ["table"] = TestEntities.Project, ["data"] = new Dictionary<string, object?> { ["pb_name"] = _prefix + "denied" } });

        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("0x80040220").And.Contain("prvCreatepb_Project");

        // Reading is allowed by the role: the caller's own security, not a privileged path.
        var read = await CallJsonAsync(client, "query", new() { ["odata"] = $"pb_projects?$select=pb_name&$filter=pb_name eq '{_prefix}denied'" });
        read["records"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_schema_requires_system_administrator()
    {
        const string yaml = """
            publisher: zm
            tables:
              - name: zm_widget
                label: Widget
                pluralLabel: Widgets
                ownership: user
                primaryName: { name: zm_name, label: Name }
            """;
        await using var reader = await ConnectAsync(_readerOid);

        var denied = await reader.CallToolAsync("apply_schema", new Dictionary<string, object?> { ["yaml"] = yaml, ["dryRun"] = false });
        denied.IsError.Should().BeTrue();
        Text(denied).Should().Contain("0x80040220").And.Contain(AdminRequired);
        Store.GetEntity("zm_widget").Should().BeNull();

        // The role check precedes input validation.
        var malformed = await reader.CallToolAsync("apply_schema", new Dictionary<string, object?> { ["yaml"] = "tables: [", ["dryRun"] = false });
        malformed.IsError.Should().BeTrue();
        Text(malformed).Should().Contain(AdminRequired);

        // The schema resource is admin-only too.
        var resource = async () => await reader.ReadResourceAsync("xrmverse://schema");
        (await resource.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain(AdminRequired);

        // An administrator without dryRun gets a dry run: an MCP agent must opt into writes.
        await using var admin = await AdminAsync();
        var plan = await CallJsonAsync(admin, "apply_schema", new() { ["yaml"] = yaml });
        plan["applied"]!.GetValue<bool>().Should().BeFalse();
        plan["changes"]!.AsArray().Select(c => c!.GetValue<string>()).Should().Contain(c => c.Contains("zm_widget"));
        Store.GetEntity("zm_widget").Should().BeNull();
    }

    [Fact]
    public async Task Apply_schema_with_rejections_is_an_error_when_writing_and_a_plan_when_dry()
    {
        var yaml = """
            publisher: zm
            tables:
              - name: zm_gadget
                label: Gadget
                primaryName: { name: zm_name, label: Name }
                columns:
                  - { name: zm_big, type: string, label: Big, maxLength: 99999999 }
            """;
        await using var admin = await AdminAsync();

        var plan = await CallJsonAsync(admin, "apply_schema", new() { ["yaml"] = yaml });
        plan["applied"]!.GetValue<bool>().Should().BeFalse();
        plan["rejections"]!.AsArray().Select(r => r!.GetValue<string>()).Should().ContainSingle().Which.Should().Contain("zm_gadget.zm_big");

        var write = await admin.CallToolAsync("apply_schema", new Dictionary<string, object?> { ["yaml"] = yaml, ["dryRun"] = false });
        write.IsError.Should().BeTrue();
        Text(write).Should().Contain("rejected").And.Contain("zm_gadget.zm_big");
        Store.GetEntity("zm_gadget").Should().BeNull();
    }

    [Fact]
    public async Task Export_solution_over_10MB_is_an_error()
    {
        McpLimits.ExportSolutionMaxBytes.Should().Be(10L * 1024 * 1024);

        var name = "mcpexport" + Guid.NewGuid().ToString("N")[..8];
        var service = ServiceFor(fx, fx.AdminObjectId);
        var solutionId = await service.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = name, ["friendlyname"] = name, ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", SystemSeed.DefaultPublisherId),
        });
        try
        {
            await using (var client = await AdminAsync())
            {
                var exported = await CallJsonAsync(client, "export_solution", new() { ["solutionName"] = name });
                exported["fileName"]!.GetValue<string>().Should().Be(name + ".zip");
                var bytes = Convert.FromBase64String(exported["base64"]!.GetValue<string>());
                exported["sizeBytes"]!.GetValue<long>().Should().Be(bytes.Length);
                bytes[..2].Should().Equal((byte)'P', (byte)'K');

                var solutions = await CallJsonAsync(client, "list_solutions", new());
                solutions["solutions"]!.AsArray().Select(s => s!["uniquename"]!.GetValue<string>()).Should().Contain(name);
            }

            // The limit lowered below the file's size: the same answer as a file over 10 MB. The host with the lower limit serves a
            // database of its own (one process per database), with the same solution.
            await using var small = fx.WithOwnDatabase(b => b.UseSetting("Mcp:ExportSolutionMaxBytes", "100"));
            await TestServices.NewOrganizationService(small.Services, fx.AdminObjectId).CreateAsync(new Entity("solution")
            {
                ["uniquename"] = name, ["friendlyname"] = name, ["version"] = "1.0.0.0",
                ["publisherid"] = new EntityReference("publisher", SystemSeed.DefaultPublisherId),
            });
            await using var limited = await ConnectAsync(small, fx.BaseUrl, fx.AdminObjectId);
            var tooLarge = await limited.CallToolAsync("export_solution", new Dictionary<string, object?> { ["solutionName"] = name });
            tooLarge.IsError.Should().BeTrue();
            Text(tooLarge).Should().Contain("xrmd solution export");
        }
        finally
        {
            await service.DeleteAsync("solution", solutionId);
        }
    }

    [Fact]
    public async Task Unauthenticated_is_401()
    {
        using var http = fx.App.CreateClient();
        using var content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        (await http.PostAsync("/mcp", content)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The endpoint exists: the same request with a token is not refused.
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var authenticated = await http.PostAsync("/mcp", content);
        authenticated.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized).And.NotBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resource_schema_exports_yaml_for_administrator()
    {
        await using var client = await AdminAsync();
        // Without a publisher: the default publisher's prefix ('new'), the same answer as the Admin API's export for it (other
        // tests of the collection may leave 'new_' components the YAML cannot describe: then the same error).
        string expected;
        using (var scope = fx.App.Services.CreateScope())
        {
            try { expected = scope.ServiceProvider.GetRequiredService<SchemaApplier>().Export("new"); }
            catch (XrmdFaultException ex) { expected = ex.Message; }
        }
        string byDefault;
        try { byDefault = (await client.ReadResourceAsync("xrmverse://schema")).Contents.OfType<TextResourceContents>().Single().Text; }
        catch (McpProtocolException ex) { byDefault = ex.Message; }
        byDefault.Should().Contain(expected);
        expected.Should().NotContain("publisher prefix is required");

        var named = await client.ReadResourceAsync("xrmverse://schema?publisher=zz");
        named.Contents.OfType<TextResourceContents>().Single().Text.Should().Contain("publisher: zz");
    }

    [Fact]
    public async Task Describe_table_marks_secured_columns()
    {
        var table = "new_mcpsec" + Guid.NewGuid().ToString("N")[..8];
        var admin = ServiceFor(fx, fx.AdminObjectId);
        await admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateEntityRequest
        {
            Entity = TestHelpers.Entity(table, table, "Secured"),
            PrimaryAttribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = TestHelpers.Label("Name"),
            },
        });
        await admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateAttributeRequest
        {
            EntityName = table,
            Attribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
            {
                LogicalName = "new_pin", SchemaName = "new_Pin", MaxLength = 10, DisplayName = TestHelpers.Label("Pin"), IsSecured = true,
            },
        });

        await using var client = await AdminAsync();
        var columns = (await CallJsonAsync(client, "describe_table", new() { ["table"] = table }))["columns"]!.AsArray();
        columns.Single(c => c!["name"]!.GetValue<string>() == "new_pin")!["secured"]!.GetValue<bool>().Should().BeTrue();
        columns.Single(c => c!["name"]!.GetValue<string>() == "new_name")!.AsObject().ContainsKey("secured").Should().BeFalse();
    }

    [Fact]
    public async Task Query_omits_secured_columns_the_caller_cannot_read()
    {
        const string table = "new_mcpfs";
        var admin = ServiceFor(fx, fx.AdminObjectId);
        if (Store.GetEntity(table) is null)
        {
            await admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateEntityRequest
            {
                Entity = TestHelpers.Entity(table, "new_McpFs", "MCP field security"),
                PrimaryAttribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
                {
                    LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = TestHelpers.Label("Name"),
                },
            });
            await admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateAttributeRequest
            {
                EntityName = table,
                Attribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
                {
                    LogicalName = "new_pin", SchemaName = "new_Pin", MaxLength = 10, DisplayName = TestHelpers.Label("Pin"), IsSecured = true,
                },
            });
        }
        var id = await admin.CreateAsync(new Microsoft.Xrm.Sdk.Entity(table) { ["new_name"] = "visible", ["new_pin"] = "1234" });
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ("prvReadnew_McpFs", DepthMask.Global));
        var fetch = $"<fetch><entity name='{table}'><attribute name='new_name' /><attribute name='new_pin' /><filter><condition attribute='{table}id' operator='eq' value='{id}' /></filter></entity></fetch>";

        await using var client = await ConnectAsync(oid);
        var record = (await CallJsonAsync(client, "query", new() { ["fetchXml"] = fetch }))["records"]!.AsArray().Single()!.AsObject();
        record.ContainsKey("new_pin").Should().BeFalse("a secured column the caller cannot read is left out, as in the Web API");
        record["new_name"]!.GetValue<string>().Should().Be("visible");

        await using var adminClient = await AdminAsync();
        (await CallJsonAsync(adminClient, "query", new() { ["fetchXml"] = fetch }))["records"]!.AsArray().Single()!["new_pin"]!
            .GetValue<string>().Should().Be("1234");
    }

    [Fact]
    public async Task Mcp_query_activitypointer_by_regarding()
    {
        // Plan 2.11 A22: the generic query tool reads activities through activitypointer, as the Web API and FetchXML do.
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var account = await admin.CreateAsync(new Entity("account") { ["name"] = _prefix + "account" });
        var regarding = new EntityReference("account", account);
        var email = await admin.CreateAsync(new Entity("email")
        {
            ["subject"] = _prefix + "mail", ["regardingobjectid"] = regarding,
            ["to"] = new EntityCollection([new Entity("activityparty") { ["addressused"] = "someone@example.com" }]),
        });
        var task = await admin.CreateAsync(new Entity("task") { ["subject"] = _prefix + "task", ["regardingobjectid"] = regarding });
        await using var client = await AdminAsync();

        var odata = await CallJsonAsync(client, "query", new()
        {
            ["odata"] = $"activitypointers?$select=subject,activitytypecode&$filter=_regardingobjectid_value eq {account}&$orderby=subject",
        });
        var records = odata["records"]!.AsArray();
        records.Select(r => Guid.Parse(r!["activityid"]!.GetValue<string>())).Should().Equal(email, task);
        records.Select(r => r!["activitytypecode"]!.GetValue<string>()).Should().Equal("email", "task");

        var fetch = $"""
            <fetch><entity name="activitypointer"><attribute name="subject"/><attribute name="activitytypecode"/><order attribute="subject"/>
              <filter><condition attribute="regardingobjectid" operator="eq" value="{account}"/></filter></entity></fetch>
            """;
        (await CallJsonAsync(client, "query", new() { ["fetchXml"] = fetch }))["records"]!.AsArray()
            .Select(r => r!["subject"]!.GetValue<string>()).Should().Equal(_prefix + "mail", _prefix + "task");

        // An activity table read with all its columns leaves the party lists out of the record, as the Web API does.
        var all = (await CallJsonAsync(client, "query", new() { ["odata"] = $"emails?$filter=activityid eq {email}" }))["records"]!.AsArray().Single()!.AsObject();
        all["subject"]!.GetValue<string>().Should().Be(_prefix + "mail");
        all.ContainsKey("to").Should().BeFalse();
    }

    [Fact]
    public async Task Resource_table_returns_compact_metadata()
    {
        await using var client = await AdminAsync();
        var result = await client.ReadResourceAsync("xrmverse://tables/pb_project");
        var text = result.Contents.OfType<TextResourceContents>().Single().Text;
        var json = JsonNode.Parse(text)!.AsObject();

        json["logicalName"]!.GetValue<string>().Should().Be(TestEntities.Project);
        json["entitySetName"]!.GetValue<string>().Should().Be("pb_projects");
        json["primaryIdAttribute"]!.GetValue<string>().Should().Be("pb_projectid");
        json["primaryNameAttribute"]!.GetValue<string>().Should().Be("pb_name");
        var columns = json["columns"]!.AsArray();
        columns.Should().Contain(c => c!["name"]!.GetValue<string>() == "pb_name" && c["type"]!.GetValue<string>() == "String");
        columns.Should().Contain(c => c!["name"]!.GetValue<string>() == "pb_customerid" && c["targets"]![0]!.GetValue<string>() == "systemuser");

        // describe_table answers the same document; list_tables lists the table.
        var described = await CallJsonAsync(client, "describe_table", new() { ["table"] = TestEntities.Project });
        JsonNode.DeepEquals(described, json).Should().BeTrue();
        var tables = await CallJsonAsync(client, "list_tables", new());
        tables["tables"]!.AsArray().Should().Contain(t => t!["logicalName"]!.GetValue<string>() == TestEntities.Project);
        json["isCustom"]!.GetValue<bool>().Should().BeTrue();
        tables["tables"]!.AsArray().Single(t => t!["logicalName"]!.GetValue<string>() == TestEntities.Project)!["isCustom"]!.GetValue<bool>().Should().BeTrue();
        tables["tables"]!.AsArray().Single(t => t!["logicalName"]!.GetValue<string>() == "systemuser")!["isCustom"]!.GetValue<bool>().Should().BeFalse();

        var unknown = await client.CallToolAsync("describe_table", new Dictionary<string, object?> { ["table"] = "nosuchtable" });
        unknown.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Diagnostics_tools_read_as_caller()
    {
        await using var client = await AdminAsync();
        var traces = await CallJsonAsync(client, "get_plugin_traces", new() { ["take"] = 5 });
        traces["records"]!.AsArray().Count.Should().BeLessThanOrEqualTo(5);
        var jobs = await CallJsonAsync(client, "get_async_jobs", new() { ["take"] = 5, ["status"] = "Succeeded" });
        jobs["records"]!.AsArray().All(j => j!["statuscode"]!.GetValue<int>() == 30).Should().BeTrue();
        var badStatus = await client.CallToolAsync("get_async_jobs", new Dictionary<string, object?> { ["status"] = "Done" });
        badStatus.IsError.Should().BeTrue();
        Text(badStatus).Should().Contain("0x80040203").And.Contain("Done");

        // A user without read access to the trace table is refused by the Core.
        await using var reader = await ConnectAsync(_readerOid);
        var denied = await reader.CallToolAsync("get_plugin_traces", new Dictionary<string, object?> { ["take"] = 5 });
        denied.IsError.Should().BeTrue();
        Text(denied).Should().Contain("0x80040220");
    }

    [Fact]
    public async Task Get_audit_history_returns_attribute_details()
    {
        await using var client = await AdminAsync();
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var org = await admin.RetrieveMultipleAsync(new Microsoft.Xrm.Sdk.Query.QueryExpression("organization") { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("isauditenabled"), TopCount = 1 });
        var orgId = org.Entities[0].Id;
        var wasEnabled = org.Entities[0].GetAttributeValue<bool>("isauditenabled");
        await admin.UpdateAsync(new Entity("organization", orgId) { ["isauditenabled"] = true });
        try
        {
            var name = _prefix + "audit";
            var id = await admin.CreateAsync(new Entity("account") { ["name"] = name });
            await admin.UpdateAsync(new Entity("account", id) { ["name"] = name + "-2" });

            var history = await CallJsonAsync(client, "get_audit_history", new() { ["table"] = "account", ["id"] = id.ToString() });
            var details = history["AuditDetailCollection"]!["AuditDetails"]!.AsArray();
            details.Should().HaveCount(2);
            details[0]!["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.AttributeAuditDetail");
            details[0]!["OldValue"]!["name"]!.GetValue<string>().Should().Be(name);
            details[0]!["NewValue"]!["name"]!.GetValue<string>().Should().Be(name + "-2");
            details[0]!["AuditRecord"]!["action"]!.GetValue<string>().Should().Be("Update");
            details[0]!["AuditRecord"]!["createdon"]!.GetValue<string>().Should().NotBeNullOrEmpty();

            var one = await CallJsonAsync(client, "get_audit_history", new() { ["table"] = "account", ["id"] = id.ToString(), ["take"] = 1 });
            one["AuditDetailCollection"]!["AuditDetails"]!.AsArray().Should().HaveCount(1);
            one["AuditDetailCollection"]!["MoreRecords"]!.GetValue<bool>().Should().BeTrue();

            var column = await CallJsonAsync(client, "get_audit_history", new() { ["table"] = "account", ["id"] = id.ToString(), ["column"] = "name" });
            column["AuditDetailCollection"]!["AuditDetails"]!.AsArray().Should().HaveCount(2);

            var bad = await client.CallToolAsync("get_audit_history", new Dictionary<string, object?> { ["table"] = "account", ["id"] = "nope" });
            bad.IsError.Should().BeTrue();
            var badTake = await client.CallToolAsync("get_audit_history", new Dictionary<string, object?> { ["table"] = "account", ["id"] = id.ToString(), ["take"] = 0 });
            badTake.IsError.Should().BeTrue();
        }
        finally
        {
            await admin.UpdateAsync(new Entity("organization", orgId) { ["isauditenabled"] = wasEnabled });
        }
    }

    [Fact]
    public async Task Get_audit_history_needs_read_audit_summary()
    {
        await using var reader = await ConnectAsync(_readerOid);
        var denied = await reader.CallToolAsync("get_audit_history", new Dictionary<string, object?> { ["table"] = "account", ["id"] = Guid.NewGuid().ToString() });
        denied.IsError.Should().BeTrue();
        Text(denied).Should().Contain("0x80040220").And.Contain("prvReadRecordAuditHistory");

        // The history privilege alone is not enough: the audit summary is needed too.
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        var role = await CreateRoleAsync(fx, "mcpaud-" + Guid.NewGuid().ToString("N")[..8], ("prvReadRecordAuditHistory", DepthMask.Global));
        await AssignRoleAsync(fx, user, role);
        try
        {
            await using var partial = await ConnectAsync(oid);
            var missing = await partial.CallToolAsync("get_audit_history", new Dictionary<string, object?> { ["table"] = "account", ["id"] = Guid.NewGuid().ToString() });
            missing.IsError.Should().BeTrue();
            Text(missing).Should().Contain("0x80040220").And.Contain("prvReadAuditSummary");
        }
        finally
        {
            await RevokeAuditPrivilegesAsync(fx);
        }
    }
}
