using System.Text.Json;
using FluentAssertions;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Cli;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

/// <summary>The <c>queue</c> group of <c>xrmd</c> (plan 4.1a, task 7) against the in-memory host, over the Web API.</summary>
[Collection("xrmd")]
public sealed class CliQueueTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "xrmd-cli-int", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public async Task InitializeAsync() =>
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());

    public Task DisposeAsync()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }

    private Task<int> RunAsync(params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, new StringReader(""), false, fx.App.Server.CreateHandler(), _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
        };
        return CliApp.RunAsync(args, env);
    }

    private JsonElement Out() => JsonDocument.Parse(_out.ToString()).RootElement.Clone();

    private async Task<JsonElement> ListAsync(string queue, params string[] extra)
    {
        (await RunAsync(["queue", "list", "-o", "json", .. extra])).Should().Be(0, _err.ToString());
        return Out().EnumerateArray().Single(r => r.GetProperty("name").GetString() == queue);
    }

    [Fact]
    public async Task Create_then_list_shows_a_public_queue_and_the_public_filter_leaves_a_private_one_out()
    {
        var pub = "cli-pub-" + Guid.NewGuid().ToString("N")[..8];
        var priv = "cli-priv-" + Guid.NewGuid().ToString("N")[..8];
        (await RunAsync("queue", "create", pub)).Should().Be(0, _err.ToString());
        (await RunAsync("queue", "create", priv, "--private")).Should().Be(0, _err.ToString());

        (await ListAsync(pub)).GetProperty("visibility").GetString().Should().Be("public");
        (await ListAsync(priv)).GetProperty("visibility").GetString().Should().Be("private");

        (await RunAsync("queue", "list", "--public", "-o", "json")).Should().Be(0, _err.ToString());
        var names = Out().EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        names.Should().Contain(pub).And.NotContain(priv);
    }

    [Fact]
    public async Task Member_add_adds_a_user_to_the_queue()
    {
        var name = "cli-mem-" + Guid.NewGuid().ToString("N")[..8];
        (await RunAsync("queue", "create", name, "--private")).Should().Be(0, _err.ToString());
        (await RunAsync("queue", "member", "add", name, fx.AdminObjectId.ToString())).Should().Be(0, _err.ToString());
        var queue = (await ListAsync(name)).GetProperty("queueid").GetString();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM data.queuemembership WHERE queueid = @queue");
        cmd.Parameters.AddWithValue("@queue", Guid.Parse(queue!));
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Items_lists_the_items_of_a_queue_and_is_empty_for_a_new_one()
    {
        var name = "cli-items-" + Guid.NewGuid().ToString("N")[..8];
        (await RunAsync("queue", "create", name)).Should().Be(0, _err.ToString());
        (await RunAsync("queue", "items", name, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_queue_exits_not_found()
    {
        (await RunAsync("queue", "items", "no-such-queue-" + Guid.NewGuid().ToString("N"))).Should().Be(ExitCodes.NotFound);
        (await RunAsync("queue", "member", "add", "no-such-queue-" + Guid.NewGuid().ToString("N"), fx.AdminObjectId.ToString())).Should().Be(ExitCodes.NotFound);
        _err.ToString().Should().Contain("not found");
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}
