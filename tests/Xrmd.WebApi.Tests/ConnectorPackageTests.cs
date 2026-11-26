using System.Text.Json.Nodes;
using FluentAssertions;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// X.12a: the custom connector package of <c>connectors/xrmverse/</c> (the OpenAPI definition and the API properties a customer
/// imports) stays importable and in step with the design-time routes of <c>/api/connector/v1</c>.
/// </summary>
public class ConnectorPackageTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PackageDir = Path.Combine(RepoRoot, "connectors", "xrmverse");
    private static readonly string DefinitionPath = Path.Combine(PackageDir, "apiDefinition.swagger.json");
    private static readonly string PropertiesPath = Path.Combine(PackageDir, "apiProperties.json");

    private static readonly string[] OperationIds =
        ["ListRows", "GetRow", "AddRow", "UpdateRow", "DeleteRow", "RunCustomApi", "GetTables", "GetTableSchema", "GetCustomApis", "GetCustomApiSchema", "OnRowChanged", "DeleteSubscription"];

    private static readonly string[] DesignTime = ["GetTables", "GetTableSchema", "GetCustomApis", "GetCustomApiSchema"];

    /// <summary>X.12b: the trigger's own operations; the unsubscribe call is hidden, the trigger is not.</summary>
    private static readonly string[] Hidden = [.. DesignTime, "DeleteSubscription"];

    private static readonly string[] ChangeCombinations =
        ["create", "update", "delete", "create,update", "create,delete", "update,delete", "create,update,delete"];

    private static readonly string[] ProductNames = ["Dataverse", "Power Automate", "Microsoft", "Dynamics"];

    private static readonly string[] Verbs = ["get", "put", "post", "delete", "options", "head", "patch"];

    private static JsonObject Definition() => JsonNode.Parse(File.ReadAllText(DefinitionPath))!.AsObject();

    /// <summary>Every operation of the definition: its path, HTTP method and node.</summary>
    private static IEnumerable<(string Path, string Method, JsonObject Operation)> Operations()
    {
        foreach (var (path, item) in Definition()["paths"]!.AsObject())
            foreach (var (method, operation) in item!.AsObject())
                if (Verbs.Contains(method))
                    yield return (path, method, operation!.AsObject());
    }

    private static JsonObject Operation(string id) => Operations().Single(o => (string?)o.Operation["operationId"] == id).Operation;

    /// <summary>Every object of <paramref name="node"/>'s tree, <paramref name="node"/> included.</summary>
    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                yield return obj;
                foreach (var (_, child) in obj)
                    foreach (var o in Objects(child)) yield return o;
                break;
            case JsonArray array:
                foreach (var child in array)
                    foreach (var o in Objects(child)) yield return o;
                break;
        }
    }

    [Fact]
    public void Definition_is_swagger_2_and_under_1_MB()
    {
        new FileInfo(DefinitionPath).Length.Should().BeLessThan(1024 * 1024);
        var definition = Definition();
        ((string?)definition["swagger"]).Should().Be("2.0");
        ((string?)definition["info"]!["title"]).Should().Be("xrmverse");
        ((string?)definition["host"]).Should().Be("{{HOST}}");
        ((string?)definition["basePath"]).Should().Be("/");
        definition["schemes"]!.AsArray().Select(s => (string?)s).Should().Equal("https");

        var metadata = definition["x-ms-connector-metadata"]!.AsArray()
            .ToDictionary(m => (string)m!["propertyName"]!, m => (string?)m!["propertyValue"]);
        metadata["Website"].Should().Be("https://xrmverse.com");
        metadata["Privacy policy"].Should().StartWith("https://xrmverse.com/");

        var oauth = definition["securityDefinitions"]!["oauth2_auth"]!;
        ((string?)oauth["type"]).Should().Be("oauth2");
        ((string?)oauth["flow"]).Should().Be("accessCode");
        ((string?)oauth["authorizationUrl"]).Should().Be("https://login.microsoftonline.com/common/oauth2/authorize");
        ((string?)oauth["tokenUrl"]).Should().Be("https://login.windows.net/common/oauth2/authorize");
        oauth["scopes"]!.AsObject().Should().BeEmpty();
    }

    [Fact]
    public void Every_operation_id_is_one_of_the_twelve_names()
    {
        Operations().Select(o => (string?)o.Operation["operationId"]).Should().BeEquivalentTo(OperationIds);
    }

    [Fact]
    public void Every_dynamic_reference_names_an_existing_operation()
    {
        var references = Objects(Definition())
            .SelectMany(o => new[] { o["x-ms-dynamic-values"], o["x-ms-dynamic-schema"] })
            .Where(r => r is not null)
            .Select(r => (string?)r!["operationId"])
            .ToList();

        references.Should().NotBeEmpty();
        references.Should().OnlyContain(id => OperationIds.Contains(id));
        references.Should().Contain(["GetTables", "GetTableSchema", "GetCustomApis", "GetCustomApiSchema"]);
    }

    [Fact]
    public void Table_dropdown_feeds_the_schema_of_the_same_action()
    {
        foreach (var id in new[] { "ListRows", "GetRow", "AddRow", "UpdateRow", "DeleteRow" })
        {
            var operation = Operation(id);
            var table = operation["parameters"]!.AsArray().Single(p => (string?)p!["name"] == "table")!;
            var values = table["x-ms-dynamic-values"]!;
            ((string?)values["operationId"]).Should().Be("GetTables");
            ((string?)values["value-collection"]).Should().Be("value");
            ((string?)values["value-path"]).Should().Be("entitySetName");
            ((string?)values["value-title"]).Should().Be("displayName");

            foreach (var schema in Objects(operation).Select(o => o["x-ms-dynamic-schema"]).Where(s => s is not null))
            {
                ((string?)schema!["operationId"]).Should().Be("GetTableSchema");
                ((string?)schema["parameters"]!["name"]!["parameter"]).Should().Be("table", $"{id} reads the schema of its table");
                ((string?)schema["parameters"]!["use"]).Should().BeOneOf("read", "create", "update");
            }
        }

        var uses = new[] { ("ListRows", "read"), ("GetRow", "read"), ("AddRow", "create"), ("AddRow", "read"), ("UpdateRow", "update"), ("UpdateRow", "read") };
        foreach (var (id, use) in uses)
            Objects(Operation(id)).Select(o => o["x-ms-dynamic-schema"]).Where(s => s is not null)
                .Select(s => (string?)s!["parameters"]!["use"]).Should().Contain(use, $"{id} uses the '{use}' schema");

        var run = Operation("RunCustomApi");
        var api = run["parameters"]!.AsArray().Single(p => (string?)p!["name"] == "customApi")!["x-ms-dynamic-values"]!;
        ((string?)api["operationId"]).Should().Be("GetCustomApis");
        ((string?)api["value-path"]).Should().Be("uniqueName");
        var parts = Objects(run).Select(o => o["x-ms-dynamic-schema"]).Where(s => s is not null).ToList();
        parts.Should().OnlyContain(s => (string?)s!["operationId"] == "GetCustomApiSchema"
            && (string?)s["parameters"]!["uniqueName"]!["parameter"] == "customApi");
        parts.Select(s => (string?)s!["parameters"]!["part"]).Should().BeEquivalentTo(["request", "response"]);
    }

    [Fact]
    public void List_rows_pages_and_hides_query_options_as_advanced()
    {
        var list = Operation("ListRows");
        ((string?)list["x-ms-pageable"]!["nextLinkName"]).Should().Be("@odata.nextLink");
        var parameters = list["parameters"]!.AsArray().ToDictionary(p => (string)p!["name"]!, p => p!);
        foreach (var name in new[] { "$select", "$filter", "$orderby", "$expand", "$top", "fetchXml" })
            ((string?)parameters[name]["x-ms-visibility"]).Should().Be("advanced", name);
    }

    [Fact]
    public void Fixed_headers_are_internal_with_a_default()
    {
        var headers = Operations().SelectMany(o => o.Operation["parameters"]?.AsArray() ?? [])
            .Where(p => (string?)p!["in"] == "header").ToList();
        headers.Select(h => (string?)h!["name"]).Should().Contain(["Prefer", "If-Match"]);
        headers.Should().OnlyContain(h => (string?)h!["x-ms-visibility"] == "internal" && h["default"] != null && (bool?)h["required"] == true);
    }

    [Fact]
    public void Fixed_headers_are_pinned_per_operation()
    {
        string? Default(string operation, string header) => Operation(operation)["parameters"]!.AsArray()
            .Where(p => (string?)p!["in"] == "header" && (string?)p["name"] == header).Select(p => (string?)p!["default"]).SingleOrDefault();

        Default("UpdateRow", "If-Match").Should().Be("*");
        Default("UpdateRow", "Prefer").Should().Be("return=representation");
        Default("AddRow", "Prefer").Should().Be("return=representation");
        Default("AddRow", "If-Match").Should().BeNull();
    }

    [Fact]
    public void Design_time_operations_are_internal()
    {
        foreach (var id in DesignTime)
            ((string?)Operation(id)["x-ms-visibility"]).Should().Be("internal", id);
        foreach (var id in OperationIds.Except(Hidden))
            ((string?)Operation(id)["x-ms-visibility"]).Should().NotBe("internal", id);
    }

    [Fact]
    public void No_microsoft_product_name_in_titles_ids_or_summaries()
    {
        var definition = Definition();
        var texts = new List<string?> { (string?)definition["info"]!["title"], (string?)definition["info"]!["description"] };
        foreach (var (_, _, operation) in Operations())
        {
            texts.Add((string?)operation["operationId"]);
            texts.Add((string?)operation["summary"]);
            texts.Add((string?)operation["description"]);
        }

        foreach (var o in Objects(definition["paths"]))
        {
            texts.Add((string?)o["x-ms-summary"]);
            if (o["in"] is not null) texts.Add((string?)o["name"]);
            if (o["in"] is not null) texts.Add((string?)o["description"]);
        }

        texts.Should().NotBeEmpty();
        foreach (var text in texts.Where(t => t is not null))
            foreach (var name in ProductNames)
                text.Should().NotContainEquivalentOf(name);
    }

    /// <summary>X.12b final review (I2): the labels are the connector's own, not those of another product's connector.</summary>
    [Fact]
    public void No_label_copies_another_connector()
    {
        var summaries = Objects(Definition()).Select(o => (string?)o["x-ms-summary"]).OfType<string>().ToList();
        summaries.Should().NotBeEmpty();
        summaries.Should().NotContain(["Table name", "Change type", "Select columns"]);
    }

    [Fact]
    public void Change_combinations_have_display_names()
    {
        var changes = Operation("OnRowChanged")["parameters"]!.AsArray().Single(p => (string?)p!["in"] == "body")!["schema"]!["properties"]!["changes"]!;
        var values = changes["x-ms-enum-values"]!.AsArray();
        values.Select(v => (string?)v!["value"]).Should().Equal(changes["enum"]!.AsArray().Select(c => (string?)c));
        values.Select(v => (string?)v!["displayName"]).Should().OnlyContain(d => !string.IsNullOrWhiteSpace(d)).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Properties_have_placeholders_and_no_secret()
    {
        var text = File.ReadAllText(PropertiesPath);
        text.Should().Contain("{{CLIENT_ID}}").And.Contain("{{RESOURCE_URI}}");

        var properties = JsonNode.Parse(text)!["properties"]!;
        var token = properties["connectionParameters"]!["token"]!;
        ((string?)token["type"]).Should().BeEquivalentTo("oauthSetting");
        var settings = token["oAuthSettings"]!;
        ((string?)settings["identityProvider"]).Should().Be("aad");
        ((string?)settings["clientId"]).Should().Be("{{CLIENT_ID}}");
        ((string?)settings["redirectMode"]).Should().Be("GlobalPerConnector");
        ((string?)settings["properties"]!["AzureActiveDirectoryResourceId"]).Should().Be("{{RESOURCE_URI}}");
        ((string?)settings["customParameters"]!["resourceUri"]!["value"]).Should().Be("{{RESOURCE_URI}}");
        ((string?)settings["customParameters"]!["tenantId"]!["value"]).Should().Be("common");
        ((string?)properties["iconBrandColor"]).Should().MatchRegex("^#[0-9a-f]{6}$").And.NotBe("#ffffff").And.NotBe("#007ee5");
        ((string?)properties["publisher"]).Should().Be("Power Builders");

        // The client secret is given at import, never written in the file: no key names a secret.
        Objects(JsonNode.Parse(text)).SelectMany(o => o.Select(p => p.Key))
            .Should().NotContain(k => k.Contains("secret", StringComparison.OrdinalIgnoreCase));
        File.Exists(Path.Combine(PackageDir, "icon.png")).Should().BeTrue();
    }

    [Fact]
    public void Paths_match_server_routes()
    {
        var byId = Operations().ToDictionary(o => (string)o.Operation["operationId"]!, o => (o.Path, o.Method));
        byId["GetTables"].Should().Be(("/api/connector/v1/tables", "get"));
        byId["GetTableSchema"].Should().Be(("/api/connector/v1/tables/{name}/schema", "get"));
        byId["GetCustomApis"].Should().Be(("/api/connector/v1/customapis", "get"));
        byId["GetCustomApiSchema"].Should().Be(("/api/connector/v1/customapis/{uniqueName}/schema", "get"));
        foreach (var id in DesignTime)
            byId[id].Path.Should().StartWith("/api/connector/v1/");

        byId["ListRows"].Should().Be(("/api/data/v9.2/{table}", "get"));
        byId["AddRow"].Should().Be(("/api/data/v9.2/{table}", "post"));
        byId["GetRow"].Should().Be(("/api/data/v9.2/{table}({id})", "get"));
        byId["UpdateRow"].Should().Be(("/api/data/v9.2/{table}({id})", "patch"));
        byId["DeleteRow"].Should().Be(("/api/data/v9.2/{table}({id})", "delete"));
        // The qualified form of the global custom API route (CustomApiEndpoints): a static segment the connector platform
        // can tell apart from POST {table}, which a bare {customApi} template would duplicate.
        byId["RunCustomApi"].Should().Be(("/api/data/v9.2/Microsoft.Dynamics.CRM.{customApi}", "post"));

        Operation("GetTableSchema")["parameters"]!.AsArray().Select(p => (string?)p!["name"]).Should().BeEquivalentTo("name", "use");
        Operation("GetCustomApiSchema")["parameters"]!.AsArray().Select(p => (string?)p!["name"]).Should().BeEquivalentTo("uniqueName", "part");

        // Every path parameter of a template is declared, and every declared path parameter is in the template.
        foreach (var (path, _, operation) in Operations())
        {
            var inTemplate = System.Text.RegularExpressions.Regex.Matches(path, @"\{(\w+)\}").Select(m => m.Groups[1].Value);
            var declared = (operation["parameters"]?.AsArray() ?? []).Where(p => (string?)p!["in"] == "path").Select(p => (string)p!["name"]!);
            declared.Should().BeEquivalentTo(inTemplate, path);
        }
    }

    [Fact]
    public void Trigger_is_a_single_webhook_on_subscriptions()
    {
        var (path, method, trigger) = Operations().Single(o => (string?)o.Operation["operationId"] == "OnRowChanged");
        path.Should().Be("/api/connector/v1/subscriptions");
        method.Should().Be("post");
        ((string?)trigger["x-ms-trigger"]).Should().Be("single");
        ((string?)trigger["summary"]).Should().Be("When a row is created, updated or deleted");
        ((string?)trigger["x-ms-visibility"]).Should().NotBe("internal");

        var body = trigger["parameters"]!.AsArray().Single(p => (string?)p!["in"] == "body")!["schema"]!["properties"]!.AsObject();
        body["callbackUrl"]!["x-ms-notification-url"]!.GetValue<bool>().Should().BeTrue();
        ((string?)body["callbackUrl"]!["x-ms-visibility"]).Should().Be("internal");

        var values = body["table"]!["x-ms-dynamic-values"]!;
        ((string?)values["operationId"]).Should().Be("GetTables");
        ((string?)values["value-path"]).Should().Be("logicalName");

        body["changes"]!["enum"]!.AsArray().Select(c => (string?)c).Should().BeEquivalentTo(ChangeCombinations);
        body.ContainsKey("filteringColumns").Should().BeTrue();

        var created = trigger["responses"]!["201"]!;
        created["headers"]!.AsObject().ContainsKey("Location").Should().BeTrue();
    }

    [Fact]
    public void Notification_content_has_change_table_id_row()
    {
        // The maker portal reads the notification content on the path item, not on the operation (live import, 2026-11-26).
        var path = Definition()["paths"]!["/api/connector/v1/subscriptions"]!.AsObject();
        Operation("OnRowChanged").AsObject().ContainsKey("x-ms-notification-content").Should().BeFalse();
        var properties = path["x-ms-notification-content"]!["schema"]!["properties"]!.AsObject();
        properties.Select(p => p.Key).Should().BeEquivalentTo("change", "table", "id", "row");
        var schema = properties["row"]!["x-ms-dynamic-schema"]!;
        ((string?)schema["operationId"]).Should().Be("GetTableSchema");
        ((string?)schema["parameters"]!["use"]).Should().Be("read");
        ((string?)schema["parameters"]!["name"]!["parameter"]).Should().Be("table");
        // `table` is a property of the body parameter: the maker portal requires the path-string form as well (live import, 2026-11-26).
        var dynamic = properties["row"]!["x-ms-dynamic-properties"]!;
        ((string?)dynamic["operationId"]).Should().Be("GetTableSchema");
        ((string?)dynamic["parameters"]!["name"]!["parameterReference"]).Should().Be("subscription/table");
        ((string?)dynamic["parameters"]!["use"]!["value"]).Should().Be("read");
    }

    [Fact]
    public void Unsubscribe_is_internal()
    {
        var (path, method, operation) = Operations().Single(o => (string?)o.Operation["operationId"] == "DeleteSubscription");
        path.Should().Be("/api/connector/v1/subscriptions/{id}");
        method.Should().Be("delete");
        ((string?)operation["x-ms-visibility"]).Should().Be("internal");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "xrmverse.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("xrmverse.sln not found above " + AppContext.BaseDirectory);
    }
}
