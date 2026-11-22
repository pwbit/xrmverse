using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary><c>xrmd webhook</c> and <c>xrmd plugin step add --webhook</c> / <c>plugin list</c> over a fake server.</summary>
public class WebhookCommandsTests
{
    private static readonly Guid HookId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid StepId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid MessageId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid FilterId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private const string Hook = """
        {"serviceendpointid":"33333333-3333-3333-3333-333333333333","name":"new_hook","url":"https://hooks.example.test/in?code=s3cr3t&x=1",
        "contract":8,"authtype":5,"isauthvalueset":true}
        """;

    private static HttpResponseMessage Created(string set, Guid id)
    {
        var created = new HttpResponseMessage(HttpStatusCode.NoContent);
        created.Headers.Add("OData-EntityId", $"http://xrmd.test:5000/api/data/v9.2/{set}({id})");
        return created;
    }

    private static HttpResponseMessage Server(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        if (request.Method == HttpMethod.Post && path.EndsWith("/serviceendpoints")) return Created("serviceendpoints", HookId);
        if (request.Method == HttpMethod.Post && path.EndsWith("/sdkmessageprocessingsteps")) return Created("sdkmessageprocessingsteps", StepId);
        if (request.Method == HttpMethod.Get && path.EndsWith("/serviceendpoints"))
            return query.Contains("'new_hook'") || !query.Contains("name eq")
                ? FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{{Hook}}]}""")
                : FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
        if (request.Method == HttpMethod.Get && path.EndsWith("/sdkmessages"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"sdkmessageid":"{{MessageId}}","name":"Create"}]}""");
        if (request.Method == HttpMethod.Get && path.EndsWith("/sdkmessagefilters"))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{"sdkmessagefilterid":"{{FilterId}}","primaryobjecttypecode":"account"}]}""");
        if (request.Method == HttpMethod.Get && path.EndsWith("/sdkmessageprocessingsteps"))
            return FakeHandler.Json(HttpStatusCode.OK, $$$"""
                {"value":[{"sdkmessageprocessingstepid":"{{{StepId}}}","name":"new_hook: Create of account","stage":40,"mode":1,"rank":1,
                "_sdkmessageid_value":"{{{MessageId}}}","_sdkmessagefilterid_value":"{{{FilterId}}}","_plugintypeid_value":null,
                "eventhandler_serviceendpoint":{"serviceendpointid":"{{{HookId}}}","name":"new_hook"}}]}
                """);
        if (request.Method == HttpMethod.Get && path.EndsWith("/pluginassemblies")) return FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
        if (request.Method == HttpMethod.Get && path.EndsWith("/plugintypes")) return FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
        if (request.Method.Method == "PATCH" || request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static async Task<TestCli> LoggedInAsync()
    {
        var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler = new FakeHandler(Server);
        return cli;
    }

    private static JsonElement PostBody(TestCli cli, string endsWith)
    {
        var post = cli.Handler.Requests.Single(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith(endsWith));
        return JsonDocument.Parse(cli.Handler.Bodies[cli.Handler.Requests.IndexOf(post)]!).RootElement;
    }

    [Fact]
    public async Task Create_header_webhook_builds_settings_xml()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("webhook", "create", "--name", "new_hook", "--url", "https://hooks.example.test/in",
            "--auth", "header", "--value", "X-Api-Key=s3cr3t", "--value", "X-Tenant=a&b", "-o", "json");

        code.Should().Be(0, cli.Err.ToString());
        var body = PostBody(cli, "/serviceendpoints");
        body.GetProperty("name").GetString().Should().Be("new_hook");
        body.GetProperty("url").GetString().Should().Be("https://hooks.example.test/in");
        body.GetProperty("contract").GetInt32().Should().Be(8);
        body.GetProperty("authtype").GetInt32().Should().Be(5);
        body.GetProperty("authvalue").GetString().Should().Be(
            "<settings><setting name=\"X-Api-Key\" value=\"s3cr3t\" /><setting name=\"X-Tenant\" value=\"a&amp;b\" /></settings>");
        JsonDocument.Parse(cli.Out.ToString()).RootElement.GetProperty("serviceendpointid").GetString().Should().Be(HookId.ToString());
        cli.Out.ToString().Should().NotContain("s3cr3t");
    }

    [Fact]
    public async Task Create_key_webhook()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("webhook", "create", "--name", "new_hook", "--url", "https://hooks.example.test/in", "--auth", "key", "--value", "abc==");

        code.Should().Be(0, cli.Err.ToString());
        var body = PostBody(cli, "/serviceendpoints");
        body.GetProperty("authtype").GetInt32().Should().Be(4);
        body.GetProperty("authvalue").GetString().Should().Be("abc==");
        cli.Out.ToString().Should().Contain(HookId.ToString()).And.NotContain("abc==");
    }

    [Theory]
    [InlineData("header", "novalue")]   // not K=V
    [InlineData("query", "=v")]         // blank name
    public async Task Create_rejects_a_malformed_value_with_exit_2(string auth, string value)
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("webhook", "create", "--name", "n", "--url", "https://hooks.example.test/in", "--auth", auth, "--value", value);

        code.Should().Be(2);
        cli.Handler.Requests.Should().BeEmpty();
        cli.Err.ToString().Should().NotContain(value + "\n");
    }

    [Fact]
    public async Task Create_key_with_two_values_exits_2()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("webhook", "create", "--name", "n", "--url", "https://hooks.example.test/in", "--auth", "key", "--value", "a", "--value", "b");

        code.Should().Be(2);
        cli.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task List_hides_query_string()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("webhook", "list", "-o", "json");

        code.Should().Be(0, cli.Err.ToString());
        var row = JsonDocument.Parse(cli.Out.ToString()).RootElement.EnumerateArray().Single();
        row.GetProperty("name").GetString().Should().Be("new_hook");
        row.GetProperty("url").GetString().Should().Be("https://hooks.example.test/in");
        row.GetProperty("authtype").GetString().Should().Be("header");
        row.GetProperty("steps").GetInt32().Should().Be(1);
        cli.Out.ToString().Should().NotContain("s3cr3t");
    }

    [Fact]
    public async Task Show_prints_the_endpoint_and_its_steps_without_the_query()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("webhook", "show", "new_hook", "-o", "json");

        code.Should().Be(0, cli.Err.ToString());
        var root = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        root.GetProperty("serviceendpointid").GetString().Should().Be(HookId.ToString());
        root.GetProperty("isauthvalueset").GetBoolean().Should().BeTrue();
        root.GetProperty("steps").EnumerateArray().Single().GetProperty("stepId").GetString().Should().Be(StepId.ToString());
        cli.Out.ToString().Should().NotContain("s3cr3t");
    }

    [Fact]
    public async Task Update_patches_url_and_auth()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("webhook", "update", "new_hook", "--url", "https://hooks.example.test/v2", "--auth", "query", "--value", "token=t0k");

        code.Should().Be(0, cli.Err.ToString());
        var patch = cli.Handler.Requests.Single(r => r.Method.Method == "PATCH");
        patch.RequestUri!.AbsolutePath.Should().EndWith($"/serviceendpoints({HookId})");
        var body = JsonDocument.Parse(cli.Handler.Bodies[cli.Handler.Requests.IndexOf(patch)]!).RootElement;
        body.GetProperty("url").GetString().Should().Be("https://hooks.example.test/v2");
        body.GetProperty("authtype").GetInt32().Should().Be(6);
        body.GetProperty("authvalue").GetString().Should().Be("<settings><setting name=\"token\" value=\"t0k\" /></settings>");
    }

    [Fact]
    public async Task Update_without_a_change_exits_2()
    {
        using var cli = await LoggedInAsync();

        (await cli.RunAsync("webhook", "update", "new_hook")).Should().Be(2);
        (await cli.RunAsync("webhook", "update", "new_hook", "--auth", "key")).Should().Be(2);
        cli.Handler.Requests.Should().NotContain(r => r.Method.Method == "PATCH");
    }

    [Fact]
    public async Task Delete_removes_the_endpoint()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("webhook", "delete", "new_hook", "--yes");

        code.Should().Be(0, cli.Err.ToString());
        cli.Handler.Requests.Single(r => r.Method == HttpMethod.Delete).RequestUri!.AbsolutePath.Should().EndWith($"/serviceendpoints({HookId})");
    }

    [Fact]
    public async Task Delete_unknown_exits_4()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("webhook", "delete", "nope", "--yes");

        code.Should().Be(4);
        cli.Err.ToString().Should().Contain("nope");
        cli.Handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Server_refusal_exits_5()
    {
        using var cli = await LoggedInAsync();
        cli.Handler = new FakeHandler(r => r.Method == HttpMethod.Post
            ? FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":{"code":"0x80040203","message":"Webhook 'n': url port 8080 is not allowed."}}""")
            : Server(r));

        var code = await cli.RunAsync("webhook", "create", "--name", "n", "--url", "https://hooks.example.test:8080/in", "--auth", "key", "--value", "k");

        code.Should().Be(5);
        cli.Err.ToString().Should().Contain("port 8080");
    }

    [Fact]
    public async Task Step_add_with_webhook_binds_serviceendpoint()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("plugin", "step", "add", "--webhook", "new_hook", "--message", "Create", "--entity", "account", "--stage", "40", "--mode", "async");

        code.Should().Be(0, cli.Err.ToString());
        var body = PostBody(cli, "/sdkmessageprocessingsteps");
        body.GetProperty("eventhandler_serviceendpoint@odata.bind").GetString().Should().Be($"/serviceendpoints({HookId})");
        body.TryGetProperty("plugintypeid@odata.bind", out _).Should().BeFalse();
        body.GetProperty("sdkmessageid@odata.bind").GetString().Should().Be($"/sdkmessages({MessageId})");
        body.GetProperty("sdkmessagefilterid@odata.bind").GetString().Should().Be($"/sdkmessagefilters({FilterId})");
        body.GetProperty("mode").GetInt32().Should().Be(1);
        body.GetProperty("name").GetString().Should().Be("new_hook: Create of account");
        cli.Out.ToString().Should().Contain(StepId.ToString());
    }

    [Fact]
    public async Task Step_add_with_unknown_webhook_exits_4()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("plugin", "step", "add", "--webhook", "nope", "--message", "Create", "--stage", "40");

        code.Should().Be(4);
        cli.Handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("--type", "Acme.Plugins.Ping", "--webhook", "new_hook")]
    [InlineData("--webhook", "new_hook", "--config", "x")]
    [InlineData("--message", "Create")] // neither --type nor --webhook
    public async Task Step_add_with_an_invalid_combination_exits_2(params string[] extra)
    {
        using var cli = await LoggedInAsync();
        string[] args = ["plugin", "step", "add", "--message", "Create", "--stage", "40", .. extra];

        var code = await cli.RunAsync(args);

        code.Should().Be(2);
        cli.Handler.Requests.Should().BeEmpty("nothing is sent for an invalid combination");
    }

    [Fact]
    public async Task Step_add_with_both_type_and_webhook_exits_2()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("plugin", "step", "add", "--type", "Acme.Plugins.Ping", "--webhook", "new_hook", "--message", "Create", "--stage", "40");

        code.Should().Be(2);
        cli.Err.ToString().Should().Contain("--type").And.Contain("--webhook");
        cli.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Plugin_list_shows_webhook_steps()
    {
        using var cli = await LoggedInAsync();

        var code = await cli.RunAsync("plugin", "list");

        code.Should().Be(0, cli.Err.ToString());
        var output = cli.Out.ToString();
        output.Should().Contain("Webhooks").And.Contain("new_hook").And.Contain(StepId.ToString()).And.Contain("Create").And.Contain("account").And.Contain("async");

        (await cli.RunAsync("plugin", "list", "-o", "json")).Should().Be(0, cli.Err.ToString());
        var row = JsonDocument.Parse(cli.Out.ToString()).RootElement.EnumerateArray().Single();
        row.GetProperty("webhook").GetString().Should().Be("new_hook");
        row.GetProperty("message").GetString().Should().Be("Create");
        row.GetProperty("mode").GetString().Should().Be("async");
    }

    [Fact]
    public async Task Plugin_list_without_webhooks_on_the_server()
    {
        using var cli = await LoggedInAsync();
        // A server before webhooks: the navigation property is unknown, so the expand is a 400.
        cli.Handler = new FakeHandler(request =>
            !request.RequestUri!.AbsolutePath.EndsWith("/sdkmessageprocessingsteps") ? Server(request)
            : Uri.UnescapeDataString(request.RequestUri.Query).Contains("eventhandler_serviceendpoint")
                ? FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":{"code":"0x80060888","message":"Could not find a property named 'eventhandler_serviceendpoint'."}}""")
                : FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}"""));

        var code = await cli.RunAsync("plugin", "list");

        code.Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().NotContain("Webhooks");
        cli.Handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/sdkmessageprocessingsteps")).Should().Be(2);
    }
}
