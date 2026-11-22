using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Cli;
using Xrmd.Metadata;
using Xrmd.TestSupport;

namespace Xrmd.Integration.Tests;

/// <summary>The <c>plugin</c> group of <c>xrmd</c> against the in-memory host (registration through the Web API).</summary>
[Collection("xrmd")]
public sealed class CliPluginTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Pipe = TestEntities.Pipe;
    private static readonly string DllPath = Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.dll");

    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "xrmd-cli-int", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private bool _tty;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{Pipe}; DELETE FROM data.plugintracelog; UPDATE data.organization SET plugintracelogsetting = 2");
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{Pipe}; DELETE FROM data.plugintracelog; UPDATE data.organization SET plugintracelogsetting = 1");
        try { Directory.Delete(_configDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private Task<int> RunAsync(params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, new StringReader(""), _tty, fx.App.Server.CreateHandler(), _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
        };
        return CliApp.RunAsync(args, env);
    }

    private JsonElement Out() => JsonDocument.Parse(_out.ToString()).RootElement.Clone();

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<string> AddStepAsync(string message)
    {
        (await RunAsync("plugin", "step", "add", "--type", SetAttribute, "--message", message, "--entity", Pipe, "--stage", "20",
            "--config", "pb_data=x", "--output", "json")).Should().Be(0, _err.ToString());
        return Out().GetProperty("stepId").GetString()!;
    }

    private async Task<string?> CreatePipeAndReadDataAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/" + TestEntities.PipeSet)
        {
            Content = new StringContent("""{"pb_name":"p"}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        req.Headers.Add("Prefer", "return=representation");
        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.IsSuccessStatusCode.Should().BeTrue(body);
        var json = JsonDocument.Parse(body).RootElement;
        return json.TryGetProperty("pb_data", out var d) ? d.GetString() : null;
    }

    private const string SetAttribute = "Xrmd.TestPlugins.SetAttributePlugin";

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Register_step_image_then_create_fires_plugin()
    {
        (await RunAsync("plugin", "register", DllPath, "--output", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("assembly").GetString().Should().Be("Xrmd.TestPlugins");

        (await RunAsync("plugin", "step", "add", "--type", SetAttribute, "--message", "Create", "--entity", Pipe, "--stage", "20",
            "--config", "pb_data=from-plugin", "--output", "json")).Should().Be(0, _err.ToString());
        var stepId = Out().GetProperty("stepId").GetString()!;

        (await RunAsync("plugin", "image", "add", "--step", stepId, "--type", "post", "--alias", "after", "--attributes", "pb_name",
            "--output", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("imageId").GetString().Should().NotBeNullOrEmpty();
        (await ScalarAsync("SELECT messagepropertyname FROM data.sdkmessageprocessingstepimage")).Should().Be("Id");
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstepimage")).Should().Be(1);

        (await CreatePipeAndReadDataAsync()).Should().Be("from-plugin");

        (await RunAsync("plugin", "list", "--output", "json")).Should().Be(0, _err.ToString());
        var rows = Out().EnumerateArray().ToList();
        rows.Should().Contain(r => r.GetProperty("stepId").GetString() == stepId);

        _tty = false;
        (await RunAsync("plugin", "step", "remove", stepId)).Should().Be(2);
        _err.ToString().Should().Contain("Refusing to run a destructive command without --yes");
        (await RunAsync("plugin", "step", "remove", stepId, "--yes")).Should().Be(0, _err.ToString());
        (await CreatePipeAndReadDataAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Register_twice_is_idempotent()
    {
        (await RunAsync("plugin", "register", DllPath, "--output", "json")).Should().Be(0, _err.ToString());
        var firstTypes = await CountAsync("SELECT COUNT(*) FROM data.plugintype");
        firstTypes.Should().BeGreaterThan(1);
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly")).Should().Be(1);

        // The update path: a stale row gets the file's content and version back.
        await ExecAsync("UPDATE data.pluginassembly SET version = N'0.0.0.1', content = N'" + Convert.ToBase64String(PluginTestHelper.EarlyBoundPluginBytes) + "'");
        (await RunAsync("plugin", "register", DllPath, "--output", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("created").GetBoolean().Should().BeFalse();
        (await ScalarAsync("SELECT version FROM data.pluginassembly")).Should().Be("1.0.0.0");
        (await ScalarAsync("SELECT content FROM data.pluginassembly")).Should().Be(Convert.ToBase64String(File.ReadAllBytes(DllPath)));

        // A type the second run must add: delete one, register again.
        await ExecAsync($"DELETE FROM data.plugintype WHERE plugintypeid IN ({TestSql.Dialect.Limit("SELECT plugintypeid FROM data.plugintype", "1")})");
        (await RunAsync("plugin", "register", DllPath, "--output", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("typesAdded").GetInt32().Should().Be(1);

        (await RunAsync("plugin", "register", DllPath, "--output", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("typesAdded").GetInt32().Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.plugintype")).Should().Be(firstTypes);
    }

    [Fact]
    public async Task Register_refuses_an_assembly_with_a_different_identity_or_duplicates()
    {
        (await RunAsync("plugin", "register", DllPath)).Should().Be(0, _err.ToString());
        var token = (string)(await ScalarAsync("SELECT publickeytoken FROM data.pluginassembly"))!;
        await ExecAsync("UPDATE data.pluginassembly SET publickeytoken = N'0123456789abcdef'");
        (await RunAsync("plugin", "register", DllPath)).Should().Be(5);
        _err.ToString().Should().Contain("public key token");

        await ExecAsync($"UPDATE data.pluginassembly SET publickeytoken = N'{token}'");
        await ExecAsync("INSERT INTO data.pluginassembly (pluginassemblyid, name, version, culture, publickeytoken, content) SELECT NEWID(), name, version, culture, publickeytoken, content FROM data.pluginassembly");
        (await RunAsync("plugin", "register", DllPath)).Should().Be(5);
        _err.ToString().Should().Contain("named 'Xrmd.TestPlugins'");
    }

    [Fact]
    public async Task Image_message_property_follows_the_message()
    {
        (await RunAsync("plugin", "register", DllPath)).Should().Be(0, _err.ToString());
        var update = await AddStepAsync("Update");
        (await RunAsync("plugin", "image", "add", "--step", update, "--type", "pre", "--alias", "before")).Should().Be(0, _err.ToString());
        (await ScalarAsync("SELECT messagepropertyname FROM data.sdkmessageprocessingstepimage")).Should().Be("Target");

        var assign = await AddStepAsync("Assign");
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep")).Should().Be(2);
        (await RunAsync("plugin", "image", "add", "--step", assign, "--type", "post", "--alias", "after")).Should().Be(0, _err.ToString());
    }

    [Fact]
    public async Task Image_on_a_message_with_unknown_property_is_exit_2()
    {
        (await RunAsync("plugin", "register", DllPath)).Should().Be(0, _err.ToString());
        var step = await AddStepAsync("Retrieve");
        (await RunAsync("plugin", "image", "add", "--step", step, "--type", "pre", "--alias", "x")).Should().Be(2);
        _err.ToString().Should().Contain("'Retrieve'");
    }

    [Fact]
    public async Task Step_on_unknown_message_is_exit_5()
    {
        (await RunAsync("plugin", "register", DllPath)).Should().Be(0, _err.ToString());
        (await RunAsync("plugin", "step", "add", "--type", SetAttribute, "--message", "NoSuchMessage", "--entity", Pipe, "--stage", "20"))
            .Should().Be(5);
        _err.ToString().Should().Contain("NoSuchMessage");
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep")).Should().Be(0);
    }

    [Fact]
    public async Task Step_on_unknown_type_is_exit_4()
    {
        (await RunAsync("plugin", "register", DllPath)).Should().Be(0, _err.ToString());
        (await RunAsync("plugin", "step", "add", "--type", "Nope.Missing", "--message", "Create", "--entity", Pipe, "--stage", "20"))
            .Should().Be(4);
    }

    [Fact]
    public async Task Trace_lists_plugintracelog()
    {
        (await RunAsync("plugin", "register", DllPath)).Should().Be(0, _err.ToString());
        (await RunAsync("plugin", "step", "add", "--type", "Xrmd.TestPlugins.ThrowPlugin", "--message", "Create", "--entity", Pipe, "--stage", "40",
            "--config", "sleep:0")).Should().Be(0, _err.ToString());
        await CreatePipeAndReadDataAsync();

        (await RunAsync("plugin", "trace", "--type", "Xrmd.TestPlugins.ThrowPlugin", "--take", "10", "--output", "json")).Should().Be(0, _err.ToString());
        var rows = Out().EnumerateArray().ToList();
        rows.Should().HaveCount(1);
        rows[0].GetProperty("typename").GetString().Should().Be("Xrmd.TestPlugins.ThrowPlugin");
        rows[0].GetProperty("messageblock").GetString().Should().Contain("ThrowPlugin start");

        (await RunAsync("plugin", "trace", "--type", "Other.Type", "--output", "json")).Should().Be(0);
        Out().GetArrayLength().Should().Be(0);
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}
