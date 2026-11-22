using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

public class CustomApiCommandsTests
{
    private const string EchoId = "11111111-1111-1111-1111-111111111111", RenameId = "33333333-3333-3333-3333-333333333333",
        SweepId = "44444444-4444-4444-4444-444444444444";

    private const string Echo = """
        {"customapiid":"11111111-1111-1111-1111-111111111111","uniquename":"xt_Echo","name":"Echo","displayname":"Echo API","description":"Echoes text",
         "bindingtype":0,"boundentitylogicalname":null,"isfunction":false,"isprivate":false,
         "plugintypeid":{"plugintypeid":"22222222-2222-2222-2222-222222222222","typename":"Acme.Plugins.EchoPlugin"}}
        """;

    private const string Rename = """
        {"customapiid":"33333333-3333-3333-3333-333333333333","uniquename":"xt_Rename","name":"Rename","displayname":"Rename","description":null,
         "bindingtype":1,"boundentitylogicalname":"account","isfunction":true,"isprivate":true,"plugintypeid":null}
        """;

    private const string Collection = """
        {"customapiid":"44444444-4444-4444-4444-444444444444","uniquename":"xt_Sweep","name":"Sweep","displayname":"Sweep","description":null,
         "bindingtype":2,"boundentitylogicalname":"contact","isfunction":false,"isprivate":false,"plugintypeid":null}
        """;

    private static readonly string[] Parameters =
    [
        """{"uniquename":"Times","name":"Times","type":7,"isoptional":true,"logicalentityname":null,"_customapiid_value":"11111111-1111-1111-1111-111111111111"}""",
        """{"uniquename":"Text","name":"Text","type":10,"isoptional":false,"logicalentityname":null,"_customapiid_value":"11111111-1111-1111-1111-111111111111"}""",
        """{"uniquename":"Target","name":"Target","type":5,"isoptional":false,"logicalentityname":"account","_customapiid_value":"33333333-3333-3333-3333-333333333333"}""",
    ];

    private static readonly string[] Properties =
    [
        """{"uniquename":"Echoed","name":"Echoed","type":10,"logicalentityname":null,"_customapiid_value":"11111111-1111-1111-1111-111111111111"}""",
    ];

    /// <summary>
    /// A server that answers the three entity sets like the real one: only single-valued <c>$expand</c> (a collection expand is a 400,
    /// as the server answers it), and the <c>$filter</c> on the unique name and on <c>_customapiid_value</c>.
    /// </summary>
    private static TestCli Server(params string[] apis) => new(request => Answer(request, apis));

    private static HttpResponseMessage Answer(HttpRequestMessage request, string[] apis)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
        if (url.Contains("CustomAPIRequestParameters") || url.Contains("CustomAPIResponseProperties"))
            return FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":{"code":"0x80040203","message":"Could not find a property named 'CustomAPIRequestParameters'."}}""");
        string Id(string row) => JsonDocument.Parse(row).RootElement.GetProperty("_customapiid_value").GetString()!;
        IEnumerable<string> Rows(IEnumerable<string> all, string key, Func<string, string> value)
        {
            var marker = key + " eq ";
            var at = url.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return all;
            var wanted = url[(at + marker.Length)..].Split('&')[0].Trim('\'');
            return all.Where(r => value(r) == wanted);
        }
        IEnumerable<string>? rows = null;
        if (url.Contains("/customapis?"))
            rows = Rows(apis, "uniquename", r => JsonDocument.Parse(r).RootElement.GetProperty("uniquename").GetString()!);
        else if (url.Contains("/customapirequestparameters?")) rows = Rows(Parameters, "_customapiid_value", Id);
        else if (url.Contains("/customapiresponseproperties?")) rows = Rows(Properties, "_customapiid_value", Id);
        return rows is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : FakeHandler.Json(HttpStatusCode.OK, "{\"value\":[" + string.Join(",", rows) + "]}");
    }

    private static string Request(TestCli cli, string entitySet) =>
        Uri.UnescapeDataString(cli.Handler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/" + entitySet, StringComparison.Ordinal)).RequestUri!.PathAndQuery);

    [Fact]
    public async Task List_prints_one_row_per_api_with_binding_kind_plugin_type_and_counts()
    {
        using var cli = Server(Echo, Rename, Collection);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "list")).Should().Be(0, cli.Err.ToString());

        var lines = cli.Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split("  ", StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToArray()).ToList();
        lines[0].Should().Equal("uniqueName", "binding", "kind", "private", "pluginType", "parameters", "properties");
        lines.Single(l => l[0] == "xt_Echo").Should().Equal("xt_Echo", "global", "action", "false", "Acme.Plugins.EchoPlugin", "2", "1");
        lines.Single(l => l[0] == "xt_Rename").Should().Equal("xt_Rename", "entity:account", "function", "true", "1", "0");
        lines.Single(l => l[0] == "xt_Sweep")[1].Should().Be("collection:contact");

        var query = Request(cli, "customapis");
        query.Should().Contain("$select=customapiid,uniquename,name,displayname,description,bindingtype,boundentitylogicalname,isfunction,isprivate")
            .And.Contain("$expand=plugintypeid($select=typename)").And.Contain("$orderby=uniquename")
            .And.NotContain("iscustomizable").And.NotContain("$filter").And.NotContain("CustomAPIRequestParameters");
        Request(cli, "customapirequestparameters").Should().Contain("$select=uniquename,name,type,isoptional,logicalentityname,_customapiid_value")
            .And.NotContain("iscustomizable").And.NotContain("$filter");
        Request(cli, "customapiresponseproperties").Should().Contain("$select=uniquename,name,type,logicalentityname,_customapiid_value")
            .And.NotContain("iscustomizable").And.NotContain("$filter");
    }

    [Fact]
    public async Task List_json_has_typed_fields()
    {
        using var cli = Server(Echo, Rename);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "list", "-o", "json")).Should().Be(0, cli.Err.ToString());

        var rows = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        rows.GetArrayLength().Should().Be(2);
        var echo = rows[0];
        echo.GetProperty("uniqueName").GetString().Should().Be("xt_Echo");
        echo.GetProperty("binding").GetString().Should().Be("global");
        echo.GetProperty("kind").GetString().Should().Be("action");
        echo.GetProperty("private").GetBoolean().Should().BeFalse();
        echo.GetProperty("pluginType").GetString().Should().Be("Acme.Plugins.EchoPlugin");
        echo.GetProperty("parameters").GetInt32().Should().Be(2);
        echo.GetProperty("properties").GetInt32().Should().Be(1);
        rows[1].GetProperty("pluginType").ValueKind.Should().Be(JsonValueKind.Null);
        rows[1].GetProperty("binding").GetString().Should().Be("entity:account");
    }

    [Fact]
    public async Task Show_prints_the_definition_with_parameters_and_properties()
    {
        using var cli = Server(Echo, Rename);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "show", "xt_Echo")).Should().Be(0, cli.Err.ToString());

        var text = cli.Out.ToString();
        text.Should().Contain("uniqueName").And.Contain("xt_Echo").And.Contain("Acme.Plugins.EchoPlugin").And.Contain("Echoes text");
        text.Should().Contain("Parameters").And.Contain("Properties");
        var parameters = text.Split('\n').Where(l => l.TrimStart().StartsWith("Text ", StringComparison.Ordinal) || l.TrimStart().StartsWith("Times ", StringComparison.Ordinal)).ToList();
        parameters.Select(l => l.Split("  ", StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToArray())
            .Should().BeEquivalentTo(new[] { new[] { "Text", "String", "false", "" }, new[] { "Times", "Integer", "true", "" } }.Select(r => r.Where(c => c.Length > 0).ToArray()));
        text.Should().Contain("Echoed");

        Request(cli, "customapis").Should().Contain("$filter=uniquename eq 'xt_Echo'").And.NotContain("iscustomizable");
        Request(cli, "customapirequestparameters").Should().Contain($"$filter=_customapiid_value eq {EchoId}");
        Request(cli, "customapiresponseproperties").Should().Contain($"$filter=_customapiid_value eq {EchoId}");
    }

    [Fact]
    public async Task Show_json_has_the_same_shape_the_mcp_tool_returns()
    {
        using var cli = Server(Echo, Rename);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "show", "xt_Rename", "-o", "json")).Should().Be(0, cli.Err.ToString());

        var api = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        api.GetProperty("uniqueName").GetString().Should().Be("xt_Rename");
        api.GetProperty("binding").GetString().Should().Be("entity:account");
        api.GetProperty("kind").GetString().Should().Be("function");
        api.GetProperty("private").GetBoolean().Should().BeTrue();
        api.GetProperty("pluginType").ValueKind.Should().Be(JsonValueKind.Null);
        var parameter = api.GetProperty("parameters").EnumerateArray().Single();
        parameter.GetProperty("uniqueName").GetString().Should().Be("Target");
        parameter.GetProperty("type").GetString().Should().Be("EntityReference");
        parameter.GetProperty("optional").GetBoolean().Should().BeFalse();
        parameter.GetProperty("table").GetString().Should().Be("account");
        api.GetProperty("properties").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Show_of_an_unknown_name_exits_4_with_a_message()
    {
        using var cli = Server(Echo);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "show", "xt_Missing")).Should().Be(4);
        cli.Err.ToString().Should().Contain("xt_Missing").And.Contain("not found");
        cli.Out.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Parameters_on_a_second_page_are_counted()
    {
        var cli = new TestCli(request =>
        {
            var url = request.RequestUri!.ToString();
            if (!url.Contains("/customapirequestparameters?")) return Answer(request, [Echo, Rename]);
            // The first page holds Times (Echo), the second Text (Echo) and Target (Rename), linked by @odata.nextLink.
            return url.Contains("$skiptoken")
                ? FakeHandler.Json(HttpStatusCode.OK, "{\"value\":[" + Parameters[1] + "," + Parameters[2] + "]}")
                : FakeHandler.Json(HttpStatusCode.OK, "{\"value\":[" + Parameters[0] + "],\"@odata.nextLink\":\"" + new Uri(request.RequestUri!, "customapirequestparameters?$skiptoken=2") + "\"}");
        });
        using var _ = cli;
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "list", "-o", "json")).Should().Be(0, cli.Err.ToString());
        var rows = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        rows[0].GetProperty("parameters").GetInt32().Should().Be(2);
        rows[1].GetProperty("parameters").GetInt32().Should().Be(1);
    }

    private const string NewApiId = "55555555-5555-5555-5555-555555555555", TypeId = "66666666-6666-6666-6666-666666666666";

    private const string PingFile = """
        {
          "uniquename": "new_Ping", "name": "Ping", "displayname": "Ping", "bindingtype": 0, "isfunction": false,
          "allowedcustomprocessingsteptype": 0, "plugintype": "Acme.Plugins.PingPlugin",
          "requestparameters": [ { "uniquename": "Text", "name": "Text", "displayname": "Text", "type": 10, "isoptional": false } ],
          "responseproperties": [ { "uniquename": "TextOut", "name": "TextOut", "displayname": "TextOut", "type": 10 } ]
        }
        """;

    /// <summary>A server for create and delete: the API POST answers 204 with its id, a field POST fails when <paramref name="failFieldsWith"/> is set.</summary>
    private static TestCli Writer(HttpStatusCode? failFieldsWith = null, bool knownType = true, string? existing = null, HttpStatusCode? failDeleteWith = null, bool cancelFields = false) => new(request =>
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
        var method = request.Method.Method;
        if (method == "GET" && url.Contains("/plugintypes?"))
            return FakeHandler.Json(HttpStatusCode.OK, knownType ? $$"""{"value":[{"plugintypeid":"{{TypeId}}"}]}""" : """{"value":[]}""");
        if (method == "GET" && url.Contains("/customapis?"))
            return FakeHandler.Json(HttpStatusCode.OK, existing is null ? """{"value":[]}""" : "{\"value\":[" + existing + "]}");
        if (method == "POST" && url.EndsWith("/customapis", StringComparison.Ordinal))
        {
            var created = new HttpResponseMessage(HttpStatusCode.NoContent);
            created.Headers.Add("OData-EntityId", $"http://xrmd.test:5000/api/data/v9.2/customapis({NewApiId})");
            return created;
        }
        if (method == "POST" && cancelFields && url.EndsWith("/customapiresponseproperties", StringComparison.Ordinal))
            throw new OperationCanceledException("The caller cancelled the request.");
        if (method == "POST" && failFieldsWith is { } status && url.EndsWith("/customapiresponseproperties", StringComparison.Ordinal))
            return FakeHandler.Json(status, """{"error":{"code":"0x80040203","message":"Custom API 'new_Ping' cannot take the response property 'TextOut': bad."}}""");
        if (method == "POST") return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (method == "DELETE" && failDeleteWith is { } deleteStatus)
            return FakeHandler.Json(deleteStatus, """{"error":{"code":"0x80040203","message":"Custom API 'new_Ping' cannot be deleted: step 'S' is registered."}}""");
        if (method == "DELETE") return new HttpResponseMessage(HttpStatusCode.NoContent);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private static string WriteFile(TestCli cli, string json = PingFile)
    {
        Directory.CreateDirectory(cli.ConfigDir);
        var path = Path.Combine(cli.ConfigDir, "ping.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static List<(string Method, string Url, JsonElement? Body)> Calls(TestCli cli) =>
        cli.Handler.Requests.Select((r, i) => (r.Method.Method, Uri.UnescapeDataString(r.RequestUri!.PathAndQuery),
            cli.Handler.Bodies[i] is { } b ? (JsonElement?)JsonDocument.Parse(b).RootElement : null)).ToList();

    [Fact]
    public async Task Create_posts_api_then_fields()
    {
        using var cli = Writer();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "create", "--file", WriteFile(cli))).Should().Be(0, cli.Err.ToString());

        var posts = Calls(cli).Where(c => c.Method == "POST").ToList();
        posts.Select(p => p.Url.Split('?')[0]).Should().Equal("/api/data/v9.2/customapis", "/api/data/v9.2/customapirequestparameters", "/api/data/v9.2/customapiresponseproperties");
        var api = posts[0].Body!.Value;
        api.GetProperty("uniquename").GetString().Should().Be("new_Ping");
        api.TryGetProperty("requestparameters", out _).Should().BeFalse();
        api.TryGetProperty("responseproperties", out _).Should().BeFalse();
        api.TryGetProperty("plugintype", out _).Should().BeFalse();
        posts[1].Body!.Value.GetProperty("CustomAPIId@odata.bind").GetString().Should().Be($"/customapis({NewApiId})");
        posts[1].Body!.Value.GetProperty("uniquename").GetString().Should().Be("Text");
        posts[2].Body!.Value.GetProperty("CustomAPIId@odata.bind").GetString().Should().Be($"/customapis({NewApiId})");
        cli.Out.ToString().Should().Contain(NewApiId);
        Calls(cli).Should().NotContain(c => c.Method == "DELETE");
    }

    [Fact]
    public async Task Create_json_prints_the_created_ids()
    {
        using var cli = Writer();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "create", "--file", WriteFile(cli), "-o", "json")).Should().Be(0, cli.Err.ToString());

        var result = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        result.GetProperty("customapiid").GetString().Should().Be(NewApiId);
        result.GetProperty("uniqueName").GetString().Should().Be("new_Ping");
        result.GetProperty("requestparameters").GetInt32().Should().Be(1);
        result.GetProperty("responseproperties").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Create_rolls_back_on_field_failure()
    {
        using var cli = Writer(failFieldsWith: HttpStatusCode.BadRequest);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "create", "--file", WriteFile(cli))).Should().Be(ExitCodes.Rejected);

        cli.Err.ToString().Should().Contain("cannot take the response property 'TextOut'");
        var calls = Calls(cli);
        calls.Last().Method.Should().Be("DELETE");
        calls.Last().Url.Should().EndWith($"/customapis({NewApiId})");
        calls.Count(c => c.Method == "POST").Should().Be(3);
    }

    [Fact]
    public async Task Create_rolls_back_when_cancelled()
    {
        using var cli = Writer(cancelFields: true);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());

        // The cancellation ends the command as the CLI ends any cancelled command, after the half-made API is deleted.
        (await cli.RunAsync("customapi", "create", "--file", WriteFile(cli))).Should().Be(ExitCodes.Unexpected);

        cli.Err.ToString().Should().Contain("Cancelled.");
        var calls = Calls(cli);
        calls.Last().Method.Should().Be("DELETE");
        calls.Last().Url.Should().EndWith($"/customapis({NewApiId})");
        calls.Count(c => c.Method == "POST").Should().Be(3);
    }

    [Fact]
    public async Task Create_resolves_plugintype_by_name()
    {
        using var cli = Writer();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "create", "--file", WriteFile(cli))).Should().Be(0, cli.Err.ToString());

        var calls = Calls(cli);
        calls.Should().Contain(c => c.Method == "GET" && c.Url.Contains("plugintypes?") && c.Url.Contains("typename eq 'Acme.Plugins.PingPlugin'"));
        calls.Single(c => c.Method == "POST" && c.Url.EndsWith("/customapis")).Body!.Value.GetProperty("plugintypeid@odata.bind").GetString()
            .Should().Be($"/plugintypes({TypeId})");
    }

    [Fact]
    public async Task Create_with_unknown_plugintype_posts_nothing()
    {
        using var cli = Writer(knownType: false);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "create", "--file", WriteFile(cli))).Should().Be(ExitCodes.NotFound);

        cli.Err.ToString().Should().Contain("Acme.Plugins.PingPlugin");
        Calls(cli).Should().NotContain(c => c.Method != "GET");
    }

    [Fact]
    public async Task Create_sends_solution_header()
    {
        using var cli = Writer();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "create", "--file", WriteFile(cli), "--solution", "PingSolution")).Should().Be(0, cli.Err.ToString());

        var posts = cli.Handler.Requests.Where(r => r.Method.Method == "POST").ToList();
        posts.Should().HaveCount(3);
        posts.Should().OnlyContain(r => r.Headers.GetValues("MSCRM.SolutionUniqueName").Single() == "PingSolution");
    }

    [Fact]
    public async Task Create_without_a_uniquename_posts_nothing()
    {
        using var cli = Writer();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "create", "--file", WriteFile(cli, """{"name":"Ping"}"""))).Should().Be(ExitCodes.Usage);
        Calls(cli).Should().NotContain(c => c.Method == "POST");
    }

    [Fact]
    public async Task Delete_by_unique_name()
    {
        using var cli = Writer(existing: $$"""{"customapiid":"{{NewApiId}}","uniquename":"new_Ping"}""");
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "delete", "new_Ping", "--yes")).Should().Be(0, cli.Err.ToString());

        var calls = Calls(cli);
        calls.Should().Contain(c => c.Method == "GET" && c.Url.Contains("$filter=uniquename eq 'new_Ping'"));
        calls.Single(c => c.Method == "DELETE").Url.Should().EndWith($"/customapis({NewApiId})");
        cli.Out.ToString().Should().Contain("new_Ping");
    }

    [Fact]
    public async Task Delete_without_yes_and_without_a_terminal_is_refused()
    {
        using var cli = Writer(existing: $$"""{"customapiid":"{{NewApiId}}","uniquename":"new_Ping"}""");
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "delete", "new_Ping")).Should().Be(ExitCodes.Usage);
        Calls(cli).Should().NotContain(c => c.Method == "DELETE");
    }

    [Fact]
    public async Task Delete_refused_by_the_server_exits_rejected()
    {
        using var cli = Writer(existing: $$"""{"customapiid":"{{NewApiId}}","uniquename":"new_Ping"}""", failDeleteWith: HttpStatusCode.BadRequest);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "delete", "new_Ping", "--yes")).Should().Be(ExitCodes.Rejected);
        cli.Err.ToString().Should().Contain("step 'S' is registered");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("[\"A\"]")]
    public async Task Create_with_a_non_string_plugintype_is_a_usage_error(string value)
    {
        using var cli = Writer();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        var json = $$"""{"uniquename":"new_Ping","name":"Ping","plugintype":{{value}}}""";
        (await cli.RunAsync("customapi", "create", "--file", WriteFile(cli, json))).Should().Be(ExitCodes.Usage);
        Calls(cli).Should().NotContain(c => c.Method == "POST");
    }

    [Fact]
    public async Task Delete_unknown_name_exits_not_found()
    {
        using var cli = Writer();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("customapi", "delete", "new_Nope", "--yes")).Should().Be(ExitCodes.NotFound);

        cli.Err.ToString().Should().Contain("Custom API 'new_Nope' not found.");
        Calls(cli).Should().NotContain(c => c.Method == "DELETE");
    }
}
