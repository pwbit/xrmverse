using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary><c>xrmd envvar list|get|set|unset</c> over the Web API (a fake server holding definitions and values).</summary>
public class EnvVarCommandsTests
{
    private static readonly Guid UrlId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FlagId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid KeyId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid UrlValueId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>Three definitions: a string with a default and a value, a boolean with neither, a secret.</summary>
    private sealed class Server
    {
        public List<(Guid Id, string Name, int Type, string? Default)> Definitions { get; } =
        [
            (UrlId, "xv_ServiceUrl", 100000000, "https://default.example.com"),
            (FlagId, "xv_Enabled", 100000002, null),
            (KeyId, "xv_ApiKey", 100000005, null),
        ];

        public Dictionary<Guid, (Guid ValueId, string Value)> Values { get; } = new() { [UrlId] = (UrlValueId, "https://prod.example.com") };

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (request.Method == HttpMethod.Get && path.EndsWith("/environmentvariabledefinitions"))
            {
                var rows = Definitions.Where(d => !query.Contains("schemaname eq ") || query.Contains($"schemaname eq '{d.Name}'", StringComparison.OrdinalIgnoreCase))
                    .Select(d => new Dictionary<string, object?>
                    {
                        ["environmentvariabledefinitionid"] = d.Id, ["schemaname"] = d.Name, ["type"] = d.Type,
                        ["defaultvalue"] = d.Default, ["displayname"] = d.Name,
                    }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));
                return FakeHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { value = rows }));
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/environmentvariablevalues"))
            {
                var rows = Values.Where(v => !query.Contains("_environmentvariabledefinitionid_value eq ") || query.Contains(v.Key.ToString()))
                    .Select(v => new Dictionary<string, object?>
                    {
                        ["environmentvariablevalueid"] = v.Value.ValueId, ["value"] = v.Value.Value, ["_environmentvariabledefinitionid_value"] = v.Key,
                    });
                return FakeHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { value = rows }));
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private static async Task<(TestCli Cli, Server Server)> StartAsync()
    {
        var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        var server = new Server();
        cli.Handler = new FakeHandler(server.Respond);
        return (cli, server);
    }

    private static string? Body(TestCli cli, HttpRequestMessage request) => cli.Handler.Bodies[cli.Handler.Requests.IndexOf(request)];

    [Fact]
    public async Task List_shows_default_and_value()
    {
        var (cli, _) = await StartAsync();
        using var _cli = cli;

        (await cli.RunAsync("envvar", "list", "-o", "json")).Should().Be(0, cli.Err.ToString());
        var rows = JsonDocument.Parse(cli.Out.ToString()).RootElement.EnumerateArray().ToList();
        rows.Should().HaveCount(3);
        var url = rows.Single(r => r.GetProperty("schemaname").GetString() == "xv_ServiceUrl");
        url.GetProperty("type").GetString().Should().Be("String");
        url.GetProperty("default").GetString().Should().Be("https://default.example.com");
        url.GetProperty("value").GetString().Should().Be("https://prod.example.com");
        var flag = rows.Single(r => r.GetProperty("schemaname").GetString() == "xv_Enabled");
        flag.GetProperty("type").GetString().Should().Be("Boolean");
        flag.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);

        (await cli.RunAsync("envvar", "list")).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain("xv_Enabled").And.Contain("(none)").And.Contain("https://prod.example.com");
    }

    [Fact]
    public async Task Get_shows_the_effective_value()
    {
        var (cli, _) = await StartAsync();
        using var _cli = cli;

        (await cli.RunAsync("envvar", "get", "xv_ServiceUrl", "-o", "json")).Should().Be(0, cli.Err.ToString());
        var row = JsonDocument.Parse(cli.Out.ToString()).RootElement;
        row.GetProperty("value").GetString().Should().Be("https://prod.example.com");
        row.GetProperty("effective").GetString().Should().Be("https://prod.example.com");
        cli.Handler.Requests.Should().Contain(r => Uri.UnescapeDataString(r.RequestUri!.Query).Contains("schemaname eq 'xv_ServiceUrl'"));
    }

    [Fact]
    public async Task Set_creates_value_when_missing()
    {
        var (cli, _) = await StartAsync();
        using var _cli = cli;

        (await cli.RunAsync("envvar", "set", "xv_Enabled", "true")).Should().Be(0, cli.Err.ToString());

        var post = cli.Handler.Requests.Single(r => r.Method == HttpMethod.Post);
        post.RequestUri!.AbsolutePath.Should().EndWith("/environmentvariablevalues");
        var body = JsonDocument.Parse(Body(cli, post)!).RootElement;
        body.GetProperty("value").GetString().Should().Be("true");
        body.GetProperty("EnvironmentVariableDefinitionId@odata.bind").GetString().Should().Be($"/environmentvariabledefinitions({FlagId})");
        cli.Out.ToString().Should().Contain("xv_Enabled");
    }

    [Fact]
    public async Task Set_updates_existing_value()
    {
        var (cli, _) = await StartAsync();
        using var _cli = cli;

        (await cli.RunAsync("envvar", "set", "xv_ServiceUrl", "https://test.example.com")).Should().Be(0, cli.Err.ToString());

        cli.Handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
        var patch = cli.Handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        patch.RequestUri!.AbsolutePath.Should().EndWith($"/environmentvariablevalues({UrlValueId})");
        JsonDocument.Parse(Body(cli, patch)!).RootElement.GetProperty("value").GetString().Should().Be("https://test.example.com");
    }

    [Fact]
    public async Task Unset_deletes_value()
    {
        var (cli, _) = await StartAsync();
        using var _cli = cli;

        (await cli.RunAsync("envvar", "unset", "xv_ServiceUrl")).Should().Be(0, cli.Err.ToString());

        var delete = cli.Handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        delete.RequestUri!.AbsolutePath.Should().EndWith($"/environmentvariablevalues({UrlValueId})");
    }

    [Fact]
    public async Task Unset_without_value_changes_nothing()
    {
        var (cli, _) = await StartAsync();
        using var _cli = cli;

        (await cli.RunAsync("envvar", "unset", "xv_Enabled")).Should().Be(0, cli.Err.ToString());

        cli.Handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("unset")]
    public async Task Unknown_schema_is_exit_4(string command)
    {
        var (cli, _) = await StartAsync();
        using var _cli = cli;

        string[] args = command == "set" ? ["envvar", command, "xv_Missing", "x"] : ["envvar", command, "xv_Missing"];
        (await cli.RunAsync(args)).Should().Be(4);
        cli.Err.ToString().Should().Contain("xv_Missing");
        cli.Handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Secret_set_is_exit_2()
    {
        var (cli, _) = await StartAsync();
        using var _cli = cli;

        (await cli.RunAsync("envvar", "set", "xv_ApiKey", "s3cret")).Should().Be(2);
        cli.Err.ToString().Should().Contain("Azure Key Vault");
        cli.Handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Server_refusal_is_exit_5()
    {
        var (cli, server) = await StartAsync();
        using var _cli = cli;
        cli.Handler = new FakeHandler(r => r.Method == HttpMethod.Patch
            ? FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":{"code":"0x80040203","message":"Refused."}}""")
            : server.Respond(r));

        (await cli.RunAsync("envvar", "set", "xv_ServiceUrl", "x")).Should().Be(5);
    }
}
