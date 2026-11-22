using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xrmd.Abstractions;
using Xrmd.Cli;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The <c>solution</c> group of <c>xrmd</c> against the in-memory host (solution actions of the Web API and the Admin API).</summary>
[Collection("solution-import")]
public sealed class CliSolutionTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Fixture = SolutionFixtureZip.FixtureName;

    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "xrmd-cli-int", Guid.NewGuid().ToString("N"));
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private bool _tty;
    private string _stdin = "";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
        await ExecAsync("DELETE FROM data.asyncoperation");
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());
    }

    public async Task DisposeAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await ExecAsync("DELETE FROM data.asyncoperation");
        try { Directory.Delete(_configDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private Task<int> RunAsync(params string[] args) => RunAsync(TimeProvider.System, args);

    private Task<int> RunAsync(TimeProvider clock, params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, new StringReader(_stdin), _tty, fx.App.Server.CreateHandler(), _configDir, new NoProcess(), clock)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
        };
        return CliApp.RunAsync(args, env);
    }

    private JsonElement Out() => JsonDocument.Parse(_out.ToString()).RootElement.Clone();

    private string WriteZip(byte[] zip)
    {
        Directory.CreateDirectory(_configDir);
        var path = Path.Combine(_configDir, Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(path, zip);
        return path;
    }

    private static byte[] FailingZip() => SolutionFixtureZip.Build(c =>
    {
        // A step that names a plug-in type the solution does not contain: the import fails and rolls back.
        var step = c.Root!.Element("SdkMessageProcessingSteps")!.Elements("SdkMessageProcessingStep").First();
        step.Element("PluginTypeId")!.Value = Guid.NewGuid().ToString("D");
    });

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<string> TextAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<int> FixtureSolutionsAsync() => CountAsync($"SELECT COUNT(*) FROM data.solution WHERE uniquename = N'{Fixture}'");

    /// <summary>A clock whose timers fire after 10 ms instead of the command's 2 s: the polling loop does not sleep.</summary>
    private sealed class FastClock : TimeProvider
    {
        public int Timers;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref Timers);
            return System.CreateTimer(callback, state, TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);
        }
    }

    private AsyncOperationWorker NewWorker() =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(), Options.Create(new PipelineOptions()));

    /// <summary>
    /// Runs <c>xrmd</c> on a <see cref="FastClock"/> while a worker of the test (the host's own worker is disabled in the
    /// fixture) picks the queued job up once the CLI has polled it at least once. The worker is observed: if it throws, the test fails with its
    /// exception instead of waiting on a job nobody runs; the CLI gets <c>--timeout 2</c> and the test a hard bound.
    /// </summary>
    private async Task<(int Exit, FastClock Clock)> RunWithWorkerAsync(params string[] args)
    {
        var clock = new FastClock();
        using var stop = new CancellationTokenSource();
        var worker = Task.Run(async () =>
        {
            // Not before the CLI has polled once (and so seen the job waiting): it then sleeps on the clock.
            while (Volatile.Read(ref clock.Timers) == 0 && !stop.IsCancellationRequested) await Task.Delay(20);
            var ran = false;
            while (!ran && !stop.IsCancellationRequested)
            {
                ran = await NewWorker().RunOnceAsync(stop.Token);
                if (!ran) await Task.Delay(50);
            }
        });

        var cli = RunAsync(clock, [.. args, "--timeout", "2"]);
        if (await Task.WhenAny(cli, worker) == worker) await worker; // rethrows a worker failure
        var exit = await cli.WaitAsync(TimeSpan.FromMinutes(3));
        stop.Cancel();
        await worker.WaitAsync(TimeSpan.FromMinutes(1));
        return (exit, clock);
    }

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_export_roundtrip_of_the_fixture()
    {
        var zip = WriteZip(SolutionFixtureZip.Build());
        (await RunAsync("solution", "import", zip, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("status").GetString().Should().Be("completed");
        Out().GetProperty("importJobId").GetGuid().Should().NotBeEmpty();
        Store.GetEntity("xv_project").Should().NotBeNull();

        (await RunAsync("solution", "list", "-o", "json")).Should().Be(0, _err.ToString());
        var fixture = Out().EnumerateArray().Single(s => s.GetProperty("uniquename").GetString() == Fixture);
        fixture.GetProperty("managed").GetBoolean().Should().BeFalse();

        var exported = Path.Combine(_configDir, "exported.zip");
        (await RunAsync("solution", "export", Fixture, "--out", exported, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("bytes").GetInt64().Should().Be(new FileInfo(exported).Length);
        using (var archive = ZipFile.OpenRead(exported))
            archive.Entries.Select(e => e.FullName).Should().Contain(["solution.xml", "customizations.xml"]);

        // Remove the solution's tables, then import the exported file: the tables are back.
        await SolutionImportTests.ClearImportAsync(fx);
        Store.GetEntity("xv_project").Should().BeNull();
        (await RunAsync("solution", "import", exported, "--overwrite")).Should().Be(0, _err.ToString());
        Store.GetEntity("xv_project").Should().NotBeNull();
        Store.GetEntity("xv_task").Should().NotBeNull();
    }

    [Fact]
    public async Task Create_add_component_and_publish()
    {
        var name = "xv_cli" + _run;
        (await RunAsync("solution", "create", name, "--publisher", "NoSuchPublisher")).Should().Be(4);
        var publisher = await TextAsync($"SELECT uniquename FROM data.publisher WHERE publisherid = '{SystemSeed.DefaultPublisherId}'");
        (await RunAsync("solution", "create", name, "--publisher", publisher, "--display-name", "Cli", "--version", "1.2.0.0", "-o", "json"))
            .Should().Be(0, _err.ToString());
        Out().GetProperty("uniquename").GetString().Should().Be(name);

        (await RunAsync("solution", "list", "-o", "json")).Should().Be(0, _err.ToString());
        var row = Out().EnumerateArray().Single(s => s.GetProperty("uniquename").GetString() == name);
        row.GetProperty("friendlyname").GetString().Should().Be("Cli");
        row.GetProperty("version").GetString().Should().Be("1.2.0.0");
        row.GetProperty("publisher").GetString().Should().Be(publisher);

        var tableName = "new_cli" + _run;
        (await RunAsync("table", "create", tableName, "--label", "T", "--plural-label", "Ts")).Should().Be(0, _err.ToString());
        var tableId = Store.GetEntity(tableName)!.MetadataId!.Value;
        (await RunAsync("solution", "add-component", name, "--type", "entity", "--id", tableId.ToString(), "-o", "json")).Should().Be(0, _err.ToString());
        (await RunAsync("solution", "add-component", name, "--type", "1", "--id", tableId.ToString())).Should().Be(0, _err.ToString());
        (await RunAsync("solution", "add-component", name, "--type", "nonsense", "--id", tableId.ToString())).Should().Be(2);
        (await RunAsync("solution", "add-component", name, "--type", "entity", "--id", "not-a-guid")).Should().Be(2);
        (await CountAsync($"SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid WHERE s.uniquename = N'{name}'"))
            .Should().Be(1);

        (await RunAsync("solution", "publish")).Should().Be(0, _err.ToString());

        _tty = false;
        (await RunAsync("solution", "delete", name, "--yes")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "delete", tableName, "--yes")).Should().Be(0, _err.ToString());
    }

    [Fact]
    public async Task Async_import_waits_for_completion()
    {
        var zip = WriteZip(SolutionFixtureZip.Build());
        var (exit, clock) = await RunWithWorkerAsync("solution", "import", zip, "--async", "-o", "json");

        exit.Should().Be(0, _err.ToString());
        Out().GetProperty("status").GetString().Should().Be("completed");
        Out().GetProperty("asyncOperationId").GetGuid().Should().NotBeEmpty();
        clock.Timers.Should().BeGreaterThan(0, "the command polls on the injected clock");
        _err.ToString().Should().Contain("Waiting").And.Contain("Succeeded");
        Store.GetEntity("xv_project").Should().NotBeNull();
        // In json mode stdout is the JSON only.
        _out.ToString().Should().NotContain("Waiting");
    }

    [Fact]
    public async Task Import_failure_prints_importjob_log_and_exits_5()
    {
        var zip = WriteZip(FailingZip());
        (await RunAsync("solution", "import", zip)).Should().Be(5, _err.ToString());
        _err.ToString().Should().Contain("import failed").And.Contain("failure");
        Store.GetEntity("xv_project").Should().BeNull();
        (await FixtureSolutionsAsync()).Should().Be(0);

        // The asynchronous import fails the same way: the system job ends Failed and the log is printed.
        var (exit, _) = await RunWithWorkerAsync("solution", "import", zip, "--async");
        exit.Should().Be(5, _err.ToString());
        _err.ToString().Should().Contain("failure");
        _out.ToString().Should().BeEmpty(_err.ToString());
    }

    [Fact]
    public void Component_type_names_match_the_solution_component_types()
    {
        var consts = typeof(Xrmd.Solution.ComponentType).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .ToDictionary(f => f.Name.ToLowerInvariant(), f => (int)f.GetRawConstantValue()!);
        foreach (var (name, value) in Xrmd.Cli.Commands.SolutionCommands.ComponentTypes)
            consts.Should().ContainKey(name).WhoseValue.Should().Be(value, name);
        Xrmd.Cli.Commands.SolutionCommands.ComponentTypes.Select(t => t.Name).Should().BeEquivalentTo(consts.Keys,
            "every ComponentType const has a CLI name");
    }

    [Fact]
    public async Task Import_failing_before_an_importjob_exists_keeps_the_server_error()
    {
        var garbage = WriteZip([1, 2, 3, 4, 5]);
        var exit = await RunAsync("solution", "import", garbage);
        exit.Should().NotBe(0);
        _err.ToString().Should().NotBeNullOrWhiteSpace();
        _out.ToString().Should().BeEmpty();
        (await FixtureSolutionsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Import_of_a_missing_file_or_managed_option_is_a_usage_error()
    {
        (await RunAsync("solution", "import", Path.Combine(_configDir, "nope.zip"))).Should().Be(2);
        var zip = WriteZip(SolutionFixtureZip.Build());
        (await RunAsync("solution", "import", zip, "--managed")).Should().Be(2);
        (await RunAsync("solution", "export", Fixture, "--out", Path.Combine(_configDir, "x.zip"), "--managed")).Should().Be(2);
        Store.GetEntity("xv_project").Should().BeNull();
    }

    [Fact]
    public async Task Delete_without_cascade_keeps_tables()
    {
        (await RunAsync("solution", "import", WriteZip(SolutionFixtureZip.Build()))).Should().Be(0, _err.ToString());

        // No terminal and no --yes: refused before anything is deleted.
        (await RunAsync("solution", "delete", Fixture)).Should().Be(2);
        _err.ToString().Should().Contain("Refusing to run a destructive command without --yes");
        (await FixtureSolutionsAsync()).Should().Be(1);

        (await RunAsync("solution", "delete", Fixture, "--yes", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("deleted").GetString().Should().Be(Fixture);
        (await FixtureSolutionsAsync()).Should().Be(0);
        Store.GetEntity("xv_project").Should().NotBeNull("deleting only the solution container keeps its components");
        Store.GetEntity("xv_task").Should().NotBeNull();

        (await RunAsync("solution", "delete", Fixture, "--yes")).Should().Be(4);
    }

    [Fact]
    public async Task Delete_with_cascade_drops_tables()
    {
        (await RunAsync("solution", "import", WriteZip(SolutionFixtureZip.Build()))).Should().Be(0, _err.ToString());

        // The confirmation is asked on a terminal; an answer other than yes aborts.
        _tty = true;
        _stdin = "n\n";
        (await RunAsync("solution", "delete", Fixture, "--cascade")).Should().Be(1);
        (await FixtureSolutionsAsync()).Should().Be(1);
        Store.GetEntity("xv_project").Should().NotBeNull();

        _stdin = "y\n";
        (await RunAsync("solution", "delete", Fixture, "--cascade", "-o", "json")).Should().Be(0, _err.ToString());
        var result = Out();
        result.GetProperty("deleted").EnumerateArray()
            .Should().Contain(d => d.GetProperty("type").GetInt32() == 1 && d.GetProperty("name").GetString() == "xv_project");
        result.TryGetProperty("kept", out _).Should().BeTrue();
        (await FixtureSolutionsAsync()).Should().Be(0);
        Store.GetEntity("xv_project").Should().BeNull();
        Store.GetEntity("xv_task").Should().BeNull();
        Store.GetOptionSet("xv_priority").Should().BeNull();

        _tty = false;
        (await RunAsync("solution", "delete", Fixture, "--cascade", "--yes")).Should().Be(4);
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}
