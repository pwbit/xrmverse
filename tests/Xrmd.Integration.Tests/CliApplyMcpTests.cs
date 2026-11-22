using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xrmd.Abstractions;
using Xrmd.Cli;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests;

/// <summary><c>xrmd apply</c> and <c>xrmd mcp</c> against the in-memory host.</summary>
[Collection("xrmd")]
public sealed class CliApplyMcpTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Schema = """
        publisher: ya
        tables:
          - name: ya_thing
            label: Thing
            pluralLabel: Things
            ownership: user
            primaryName: { name: ya_name, label: Name, maxLength: 100 }
            columns:
              - { name: ya_count, type: int, label: Count }
        """;

    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "xrmd-cli-int", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public async Task InitializeAsync()
    {
        await CleanAsync();
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());
    }

    public async Task DisposeAsync()
    {
        await CleanAsync();
        try { Directory.Delete(_configDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private async Task CleanAsync()
    {
        foreach (var e in Store.GetAllEntities().Where(e => e.LogicalName.StartsWith("ya_", StringComparison.Ordinal)).ToList())
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteEntityAsync(s, e.LogicalName, default));
    }

    private CliEnvironment Env(Stream? mcpIn = null, Stream? mcpOut = null) =>
        new(_out, _err, new StringReader(""), false, fx.App.Server.CreateHandler(), _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
            McpInput = mcpIn,
            McpOutput = mcpOut,
        };

    private Task<int> RunAsync(params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        return CliApp.RunAsync(args, Env());
    }

    private string WriteSchema(string yaml)
    {
        Directory.CreateDirectory(_configDir);
        var path = Path.Combine(_configDir, Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public async Task Apply_diff_then_apply_then_diff_is_empty()
    {
        var file = WriteSchema(Schema);

        (await RunAsync("apply", file, "--diff")).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("+ ").And.Contain("ya_thing");
        Store.GetEntity("ya_thing").Should().BeNull("--diff must not change anything");

        (await RunAsync("apply", file)).Should().Be(0, _err.ToString());
        Store.GetEntity("ya_thing").Should().NotBeNull();

        (await RunAsync("apply", file, "--diff", "-o", "json")).Should().Be(0, _err.ToString());
        var diff = JsonDocument.Parse(_out.ToString()).RootElement;
        diff.GetProperty("changes").GetArrayLength().Should().Be(0);
        diff.GetProperty("rejections").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Apply_with_unknown_publisher_prefix_fails_before_any_change()
    {
        var file = WriteSchema(Schema.Replace("ya", "yb")); // no publisher owns 'yb'

        (await RunAsync("apply", file)).Should().NotBe(0);
        _err.ToString().Should().Contain("'yb'").And.Contain("xrmd publisher create");
        Store.GetAllEntities().Should().NotContain(e => e.LogicalName.StartsWith("yb_", StringComparison.Ordinal));
        (await RunAsync("apply", file, "--diff")).Should().NotBe(0, "a diff checks the prefix too");

        // With the publisher created through the CLI, the same file applies.
        (await RunAsync("publisher", "create", "--unique-name", "yb" + Guid.NewGuid().ToString("N")[..6], "--prefix", "yb",
            "--option-value-prefix", "12399")).Should().Be(0, _err.ToString());
        (await RunAsync("apply", file)).Should().Be(0, _err.ToString());
        Store.GetEntity("yb_thing").Should().NotBeNull();
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteEntityAsync(s, "yb_thing", default));
    }

    [Fact]
    public async Task Apply_invalid_yaml_is_exit_2_with_line()
    {
        var file = WriteSchema("publisher: ya\ntables: [oops\n");

        (await RunAsync("apply", file)).Should().Be(2);
        _err.ToString().Should().Contain("line ").And.Contain("column ");
        _out.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_missing_file_is_exit_2()
    {
        (await RunAsync("apply", Path.Combine(_configDir, "nope.yaml"))).Should().Be(2);
    }

    [Fact]
    public async Task Apply_with_rejections_is_exit_5_and_never_success()
    {
        var file = WriteSchema(Schema.Replace("{ name: ya_count, type: int, label: Count }",
            "{ name: ya_ref, type: lookup, target: ya_nowhere, label: Ref }"));

        (await RunAsync("apply", file)).Should().Be(5);
        _err.ToString().Should().NotBeEmpty();
        _out.ToString().Should().Contain("! ");
        Store.GetEntity("ya_thing").Should().BeNull();
    }

    [Fact]
    public async Task Apply_diff_with_rejections_prints_the_plan_and_exits_5()
    {
        var file = WriteSchema(Schema.Replace("{ name: ya_count, type: int, label: Count }",
            "{ name: ya_ref, type: lookup, target: ya_nowhere, label: Ref }"));

        (await RunAsync("apply", file, "--diff")).Should().Be(5);
        _out.ToString().Should().Contain("! ");
    }

    [Fact]
    public async Task Apply_diff_predicts_a_value_the_server_refuses()
    {
        var file = WriteSchema(Schema.Replace("{ name: ya_count, type: int, label: Count }",
            "{ name: ya_big, type: string, label: Big, maxLength: 99999999 }"));

        (await RunAsync("apply", file, "--diff")).Should().Be(5, _err.ToString());
        _out.ToString().Should().Contain("! ").And.Contain("ya_big");
        (await RunAsync("apply", file)).Should().Be(5, _err.ToString());
        _out.ToString().Should().Contain("! ").And.Contain("ya_big");
        Store.GetEntity("ya_thing").Should().BeNull();
    }

    private async Task<int> RunMcpAsync(HttpMessageHandler? handler, bool withKey, TimeSpan? timeout = null)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, new StringReader(""), false, handler, _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => withKey && name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
            McpInput = new MemoryStream(),
            McpOutput = new MemoryStream(),
            McpStartupTimeout = timeout,
        };
        return await CliApp.RunAsync(["mcp"], env);
    }

    [Fact]
    public async Task Mcp_bridge_without_credentials_is_exit_3()
    {
        (await RunMcpAsync(fx.App.Server.CreateHandler(), withKey: false)).Should().Be(3, _err.ToString());
        _err.ToString().Should().NotBeEmpty();
        _out.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_bridge_remote_401_is_exit_3()
    {
        (await RunMcpAsync(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized))), withKey: true))
            .Should().Be(3, _err.ToString());
        _out.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_bridge_startup_timeout_is_exit_6()
    {
        var never = new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        (await RunMcpAsync(never, withKey: true, TimeSpan.FromMilliseconds(500))).Should().Be(6, _err.ToString());
        _err.ToString().Should().Contain("did not respond");
        _out.ToString().Should().BeEmpty();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Fact]
    public async Task Mcp_bridge_lists_remote_tools_over_stdio()
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var toServer = new Pipe();
        var fromServer = new Pipe();
        var bridge = CliApp.RunAsync(["mcp"], Env(toServer.Reader.AsStream(), fromServer.Writer.AsStream()));

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), fromServer.Reader.AsStream())))
        {
            var tools = await client.ListToolsAsync();
            tools.Select(t => t.Name).Should().Contain(["list_tables", "query", "create_record"]);

            var result = await client.CallToolAsync("list_tables", new Dictionary<string, object?>());
            result.IsError.Should().NotBe(true);

            (await client.ListResourceTemplatesAsync()).Select(t => t.UriTemplate).Should().Contain("xrmverse://tables/{name}");
        }

        await toServer.Writer.CompleteAsync();
        (await bridge.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(0, _err.ToString());
        _out.ToString().Should().BeEmpty("stdout is the MCP channel");
    }

    [Fact]
    public async Task Mcp_bridge_exits_6_when_the_server_is_unreachable()
    {
        (await RunAsync("auth", "login", "--url", "http://127.0.0.1:1/", "--profile", "dead", "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, new StringReader(""), false, null, _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
            McpInput = new MemoryStream(),
            McpOutput = new MemoryStream(),
        };

        (await CliApp.RunAsync(["mcp", "--profile", "dead"], env)).Should().Be(6);
        _err.ToString().Should().NotBeEmpty();
        _out.ToString().Should().BeEmpty();
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}
