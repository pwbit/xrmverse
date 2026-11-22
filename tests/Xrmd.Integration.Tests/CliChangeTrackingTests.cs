using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Cli;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The <c>change-tracking</c> group of <c>xrmd</c> (plan 2.13, task 5) against the in-memory host, over the Web API.</summary>
[Collection("xrmd")]
public sealed class CliChangeTrackingTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "xrmd-cli-int", Guid.NewGuid().ToString("N"));
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());
    }

    /// <summary>Leaves the retention at the Dataverse default (7 days) for the other tests.</summary>
    public async Task DisposeAsync()
    {
        await RunAsync("change-tracking", "retention", "7");
        try { Directory.Delete(_configDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private Task<int> RunAsync(params string[] args) => RunAsync(null, args);

    private Task<int> RunAsync(TextReader? input, params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, input ?? new StringReader(""), false, fx.App.Server.CreateHandler(), _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
        };
        return CliApp.RunAsync(args, env);
    }

    private JsonElement Out() => JsonDocument.Parse(_out.ToString()).RootElement.Clone();

    private async Task<string> NewTableAsync(string suffix)
    {
        var table = $"new_ct{suffix}{_run}";
        (await RunAsync("table", "create", table, "--label", "Tracked", "--plural-label", "Trackeds")).Should().Be(0, _err.ToString());
        return table;
    }

    [Fact]
    public async Task Status_lists_tracked_tables_and_retention()
    {
        var table = await NewTableAsync("a");
        (await RunAsync("change-tracking", "retention", "12")).Should().Be(0, _err.ToString());
        (await RunAsync("change-tracking", "enable", table, "--yes")).Should().Be(0, _err.ToString());

        (await RunAsync("change-tracking", "status")).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("12 days").And.Contain(table);

        (await RunAsync("change-tracking", "status", "--table", table)).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("is on").And.Contain("can be turned on");
    }

    [Fact]
    public async Task Enable_needs_yes_without_tty()
    {
        var table = await NewTableAsync("b");
        (await RunAsync("change-tracking", "enable", table)).Should().Be(2);
        _err.ToString().Should().Contain("--yes");
        (await RunAsync("change-tracking", "status", "--table", table, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("changeTrackingEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Enable_then_status_shows_table()
    {
        var table = await NewTableAsync("c");
        (await RunAsync("change-tracking", "enable", table, "--yes")).Should().Be(0, _err.ToString());
        (await RunAsync("change-tracking", "status", "--table", table, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("changeTrackingEnabled").GetBoolean().Should().BeTrue();

        (await RunAsync("change-tracking", "status", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("tables").EnumerateArray().Select(t => t.GetString()).Should().Contain(table);

        // Already on: said so, exit 0, no confirmation needed.
        (await RunAsync("change-tracking", "enable", table)).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("already on");
    }

    [Fact]
    public async Task Retention_out_of_range_exits_5()
    {
        (await RunAsync("change-tracking", "retention", "366")).Should().Be(5);
        (await RunAsync("change-tracking", "retention", "0")).Should().Be(0, _err.ToString());
        (await RunAsync("change-tracking", "status", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("retentionDays").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Unknown_table_exits_4()
    {
        (await RunAsync("change-tracking", "enable", "new_nosuchtable", "--yes")).Should().Be(4);
        (await RunAsync("change-tracking", "status", "--table", "new_nosuchtable")).Should().Be(4);
    }

    [Fact]
    public async Task Ineligible_table_exits_5()
    {
        (await RunAsync("change-tracking", "enable", "audit", "--yes")).Should().Be(5);
    }

    [Fact]
    public async Task Json_output()
    {
        (await RunAsync("change-tracking", "retention", "30", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("retentionDays").GetInt32().Should().Be(30);
        (await RunAsync("change-tracking", "status", "-o", "json")).Should().Be(0, _err.ToString());
        var status = Out();
        status.GetProperty("retentionDays").GetInt32().Should().Be(30);
        status.GetProperty("tables").ValueKind.Should().Be(JsonValueKind.Array);
        (await RunAsync("change-tracking", "status", "--table", "audit", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("canChangeTrackingBeEnabled").GetBoolean().Should().BeFalse();
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}
