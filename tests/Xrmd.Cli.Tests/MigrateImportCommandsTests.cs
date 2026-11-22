using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xrmd.Cli.Commands;
using Xrmd.Migration.Tests;

namespace Xrmd.Cli.Tests;

/// <summary>
/// <c>xrmd migrate import | status | report | cancel | finalize</c> (plan M4b task 4) against a scripted Admin API: the upload in
/// chunks, the start with the users mapping and options, the wait that prints each phase once, the report formats.
/// </summary>
[Collection("migrate-import")]
public sealed class MigrateImportCommandsTests : IDisposable
{
    private const string Url = "http://xrmd.test:5000";
    private static readonly Guid PackageId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RunId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly string[] Phases = ["verify", "schema", "security", "records", "nn", "deferred-lookups", "sharing", "files", "post"];

    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-import-cli-").FullName;

    public void Dispose()
    {
        MigrateImport.ChunkBytes = MigrateImport.DefaultChunkBytes;
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Every timer fires at once and moves the clock forward by its due time.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) Interlocked.Add(ref _ticks, dueTime.Ticks);
            return System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private static HttpResponseMessage Run(string state, int phase, string? message = null) => FakeHandler.Json(HttpStatusCode.OK, new JsonObject
    {
        ["runId"] = RunId.ToString(), ["state"] = state, ["phase"] = phase, ["phases"] = new JsonArray([.. Phases.Select(p => (JsonNode)p)]), ["message"] = message,
    }.ToJsonString());

    private static readonly string Report = new JsonObject
    {
        ["runId"] = RunId.ToString(), ["state"] = "completed_with_errors",
        ["tables"] = new JsonArray(new JsonObject { ["table"] = "contact", ["exported"] = 4, ["imported"] = 3, ["failed"] = 1 }),
        ["issues"] = new JsonArray(new JsonObject
        {
            ["phase"] = 4, ["severity"] = "error", ["code"] = "0x80040216", ["table"] = "contact", ["recordId"] = "55555555-5555-5555-5555-555555555555",
            ["column"] = null, ["message"] = "Too long, \"name\"",
        }),
        ["issuesTruncated"] = false,
        ["lookupsNulled"] = new JsonArray(new JsonObject { ["table"] = "contact", ["column"] = "parentcustomerid", ["count"] = 3 }),
        ["users"] = new JsonObject { ["entra"] = 0, ["entra-remap"] = 0, ["local"] = 1, ["stub"] = 1, ["existing"] = 0 },
        ["phases"] = new JsonArray(new JsonObject { ["phase"] = 1, ["name"] = "verify", ["startedOn"] = "2026-11-17T12:00:00Z", ["endedOn"] = "2026-11-17T12:00:01Z", ["seconds"] = 1.0 }),
    }.ToJsonString();

    /// <summary>A server that takes the upload and the start, then answers the polls with <paramref name="polls"/> in turn.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Server(Queue<Func<HttpResponseMessage>> polls, Func<int, HttpResponseMessage?>? chunk = null,
        Func<HttpResponseMessage>? package = null) => req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        var method = req.Method.Method;
        if (path == "/admin/migrate/packages" && method == "POST") return FakeHandler.Json(HttpStatusCode.Created, $$"""{"packageId":"{{PackageId}}"}""");
        if (path.StartsWith($"/admin/migrate/packages/{PackageId}/chunks/", StringComparison.Ordinal))
            return chunk?.Invoke(int.Parse(path[(path.LastIndexOf('/') + 1)..])) ?? new HttpResponseMessage(HttpStatusCode.NoContent);
        if (path == $"/admin/migrate/packages/{PackageId}/complete") return FakeHandler.Json(HttpStatusCode.OK, $$"""{"packageId":"{{PackageId}}","state":"complete"}""");
        if (path == $"/admin/migrate/packages/{PackageId}" && method == "GET")
            return package?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        if (path == "/admin/migrate/runs" && method == "POST") return FakeHandler.Json(HttpStatusCode.Accepted, $$"""{"runId":"{{RunId}}","asyncOperationId":"{{Guid.NewGuid()}}"}""");
        if (path == $"/admin/migrate/runs/{RunId}" && method == "GET") return polls.Count > 1 ? polls.Dequeue()() : polls.Peek()();
        if (path == $"/admin/migrate/runs/{RunId}/report") return FakeHandler.Json(HttpStatusCode.OK, Report);
        if (path == $"/admin/migrate/runs/{RunId}/cancel") return Run("cancelled", 3);
        if (path == $"/admin/migrate/runs/{RunId}/finalize") return Run("finalized", 3);
        if (path == $"/admin/migrate/runs/{RunId}/activate") return FakeHandler.Json(HttpStatusCode.OK, $$"""
            {"runId":"{{RunId}}","items":[
              {"table":"sdkmessageprocessingstep","id":"66666666-6666-6666-6666-666666666666","name":"Send to ERP","action":"activated","message":null},
              {"table":"workflow","id":"77777777-7777-7777-7777-777777777777","name":"Welcome mail","action":"skipped","message":"It is already on."}]}
            """);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    };

    private static async Task<TestCli> CliAsync(Func<HttpRequestMessage, HttpResponseMessage> server)
    {
        var cli = new TestCli(server) { Clock = new SteppingClock() };
        cli.Vars["XRMD_MIGRATE_PASSPHRASE"] = SyntheticPackage.Passphrase;
        (await cli.LoginDevAsync(Url)).Should().Be(0, cli.Err.ToString());
        return cli;
    }

    private static List<(string Method, string Path, string? Body)> Calls(TestCli cli) =>
        [.. cli.Handler.Requests.Select((r, i) => (r.Method.Method, r.RequestUri!.AbsolutePath, cli.Handler.Bodies[i]))];

    [Fact]
    public async Task Import_uploads_starts_and_waits()
    {
        var file = SyntheticPackage.BuildFile(_root);
        var users = Path.Combine(_root, "users.yaml");
        await File.WriteAllTextAsync(users, """
            # xrmd migrate users
            defaults: { licensed: entra, disabled: stub, application: stub }
            users:
              - id: 6f1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a1
                name: "Ada Example"
                action: local   # no Entra id
                username: "ada"
            """);
        var polls = new Queue<Func<HttpResponseMessage>>([() => Run("queued", 0), () => Run("running", 1), () => Run("running", 1), () => Run("running", 4),
            () => Run("completed", 9)]);
        using var cli = await CliAsync(Server(polls));

        (await cli.RunAsync("migrate", "import", file, "--users", users, "--strict", "--keep-package")).Should().Be(0, cli.Err.ToString());

        var calls = Calls(cli);
        calls.Count(c => c.Path.Contains("/chunks/")).Should().Be(1);
        var start = JsonNode.Parse(calls.Single(c => c.Method == "POST" && c.Path == "/admin/migrate/runs").Body!)!;
        start["packageId"]!.GetValue<string>().Should().Be(PackageId.ToString());
        start["passphrase"]!.GetValue<string>().Should().Be(SyntheticPackage.Passphrase);
        start["users"]!["users"]![0]!["action"]!.GetValue<string>().Should().Be("local");
        start["users"]!["users"]![0]!["username"]!.GetValue<string>().Should().Be("ada");
        start["options"]!["strict"]!.GetValue<string>().Should().Be("true");
        start["options"]!["keepPackage"]!.GetValue<string>().Should().Be("true");
        var err = cli.Err.ToString();
        foreach (var phase in new[] { "verify", "records", "post" })
            System.Text.RegularExpressions.Regex.Matches(err, $@"\b{phase}\b").Count.Should().Be(1, $"phase {phase} is printed once: {err}");
        err.Should().Contain("completed");
    }

    [Fact]
    public async Task Import_with_errors_exits_5_and_prints_report_hint()
    {
        var file = SyntheticPackage.BuildFile(_root);
        using var cli = await CliAsync(Server(new Queue<Func<HttpResponseMessage>>([() => Run("running", 3), () => Run("completed_with_errors", 9, "Some rows could not be imported: see the report.")])));
        (await cli.RunAsync("migrate", "import", file)).Should().Be(5, cli.Err.ToString());
        cli.Err.ToString().Should().Contain($"xrmd migrate report {RunId}");
        cli.Err.ToString().Should().Contain($"xrmd migrate import --resume {RunId}").And.Contain($"xrmd migrate finalize {RunId}");
    }

    [Fact]
    public async Task Bad_users_yaml_fails_before_upload()
    {
        var file = SyntheticPackage.BuildFile(_root);
        var users = Path.Combine(_root, "users.yaml");
        await File.WriteAllTextAsync(users, """
            users:
              - id: 6f1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a1
                action: teleport
            """);
        using var cli = await CliAsync(Server(new Queue<Func<HttpResponseMessage>>([() => Run("completed", 9)])));
        (await cli.RunAsync("migrate", "import", file, "--users", users)).Should().Be(2, cli.Err.ToString());
        cli.Err.ToString().Should().Contain("teleport");
        Calls(cli).Should().NotContain(c => c.Path.StartsWith("/admin/migrate", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("defaults:\n  licensed: teleport\n", "teleport")]
    [InlineData("users:\n  - id: 6f1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a1\n    action: local\n", "username")]
    [InlineData("users:\n  - id: 6f1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a1\n    action: stub\n  - id: 6f1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a1\n    action: stub\n", "more than once")]
    public async Task Users_yaml_checked_with_the_server_rules_before_upload(string yaml, string expected)
    {
        var file = SyntheticPackage.BuildFile(_root);
        var users = Path.Combine(_root, "users-" + Guid.NewGuid().ToString("N")[..6] + ".yaml");
        await File.WriteAllTextAsync(users, yaml);
        using var cli = await CliAsync(Server(new Queue<Func<HttpResponseMessage>>([() => Run("completed", 9)])));
        (await cli.RunAsync("migrate", "import", file, "--users", users)).Should().Be(2, cli.Err.ToString());
        cli.Err.ToString().Should().Contain(expected);
        Calls(cli).Should().NotContain(c => c.Path.StartsWith("/admin/migrate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wrong_passphrase_exits_8_before_upload()
    {
        var file = SyntheticPackage.BuildFile(_root);
        using var cli = await CliAsync(Server(new Queue<Func<HttpResponseMessage>>([() => Run("completed", 9)])));
        cli.Vars["XRMD_MIGRATE_PASSPHRASE"] = "not the passphrase";
        (await cli.RunAsync("migrate", "import", file)).Should().Be(8, cli.Err.ToString());
        Calls(cli).Should().NotContain(c => c.Path.StartsWith("/admin/migrate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_after_interrupted_upload_starts_one_run()
    {
        var file = SyntheticPackage.BuildFile(_root);
        MigrateImport.ChunkBytes = (int)Math.Ceiling(new FileInfo(file).Length / 3.0); // three chunks
        var polls = new Queue<Func<HttpResponseMessage>>([() => Run("completed", 9)]);
        // First attempt: chunk 1 never gets through (the connection drops every time): the CLI gives up after its retries.
        using var cli = await CliAsync(Server(polls, chunk: n => n == 1 ? throw new HttpRequestException("connection reset") : null));
        (await cli.RunAsync("migrate", "import", file)).Should().Be(6, cli.Err.ToString());
        Calls(cli).Count(c => c.Path.EndsWith("/chunks/1", StringComparison.Ordinal)).Should().Be(3);
        Calls(cli).Should().NotContain(c => c.Path == "/admin/migrate/runs");

        // Second attempt, same package: the server has chunk 0; the CLI sends 1 and 2 only, completes, and starts one run.
        cli.Handler = new FakeHandler(Server(polls, package: () => FakeHandler.Json(HttpStatusCode.OK,
            $$"""{"packageId":"{{PackageId}}","state":"uploading","chunks":[0]}""")));
        (await cli.RunAsync("migrate", "import", file)).Should().Be(0, cli.Err.ToString());
        var calls = Calls(cli);
        calls.Should().NotContain(c => c.Method == "POST" && c.Path == "/admin/migrate/packages");
        calls.Where(c => c.Path.Contains("/chunks/")).Select(c => c.Path[(c.Path.LastIndexOf('/') + 1)..]).Should().Equal("1", "2");
        calls.Count(c => c.Method == "POST" && c.Path == "/admin/migrate/runs").Should().Be(1);

        // A third import of a package the server holds complete starts its run without uploading.
        cli.Handler = new FakeHandler(Server(polls, package: () => FakeHandler.Json(HttpStatusCode.OK, $$"""{"packageId":"{{PackageId}}","state":"complete","chunks":[]}""")));
        (await cli.RunAsync("migrate", "import", file)).Should().Be(0, cli.Err.ToString());
        Calls(cli).Should().NotContain(c => c.Path.Contains("/chunks/") || c.Path.EndsWith("/complete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Report_formats_md_json_csv()
    {
        using var cli = await CliAsync(Server(new Queue<Func<HttpResponseMessage>>([() => Run("completed_with_errors", 9)])));
        (await cli.RunAsync("migrate", "report", RunId.ToString(), "--format", "md")).Should().Be(0, cli.Err.ToString());
        var md = cli.Out.ToString();
        md.Should().Contain("# Migration report").And.Contain("| contact | 4 | 3 | 1 |").And.Contain("parentcustomerid").And.Contain("0x80040216");

        cli.Out.GetStringBuilder().Clear();
        (await cli.RunAsync("migrate", "report", RunId.ToString(), "--format", "json")).Should().Be(0, cli.Err.ToString());
        JsonNode.Parse(cli.Out.ToString())!["tables"]![0]!["failed"]!.GetValue<int>().Should().Be(1);

        cli.Out.GetStringBuilder().Clear();
        (await cli.RunAsync("migrate", "report", RunId.ToString(), "--format", "csv")).Should().Be(0, cli.Err.ToString());
        var lines = cli.Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Trim().Should().Be("phase,severity,code,table,recordId,column,message");
        lines[1].Trim().Should().Be("4,error,0x80040216,contact,55555555-5555-5555-5555-555555555555,,\"Too long, \"\"name\"\"\"");
    }

    [Fact]
    public async Task Cancel_and_finalize()
    {
        using var cli = await CliAsync(Server(new Queue<Func<HttpResponseMessage>>([() => Run("running", 3)])));
        (await cli.RunAsync("migrate", "cancel", RunId.ToString())).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("migrate", "finalize", RunId.ToString(), "--keep-package")).Should().Be(0, cli.Err.ToString());
        var calls = Calls(cli);
        calls.Should().Contain(c => c.Method == "POST" && c.Path == $"/admin/migrate/runs/{RunId}/cancel");
        JsonNode.Parse(calls.Single(c => c.Path.EndsWith("/finalize", StringComparison.Ordinal)).Body!)!["keepPackage"]!.GetValue<bool>().Should().BeTrue();
        (await cli.RunAsync("migrate", "status", RunId.ToString())).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain("running");
    }

    [Fact]
    public async Task Activate_prints_each_item_and_exits_0()
    {
        using var cli = await CliAsync(Server(new Queue<Func<HttpResponseMessage>>([() => Run("finalized", 10)])));
        (await cli.RunAsync("migrate", "activate", RunId.ToString(), "--all")).Should().Be(0, cli.Err.ToString());
        var output = cli.Out.ToString();
        output.Should().Contain("activated").And.Contain("Send to ERP").And.Contain("skipped").And.Contain("It is already on.");
        var body = JsonNode.Parse(Calls(cli).Single(c => c.Path.EndsWith("/activate", StringComparison.Ordinal)).Body!)!;
        body["all"]!.GetValue<bool>().Should().BeTrue();
        body["dryRun"]!.GetValue<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData]
    [InlineData("--all", "--id", "66666666-6666-6666-6666-666666666666")]
    [InlineData("--id", "not-a-guid")]
    public async Task Activate_needs_all_or_id(params string[] args)
    {
        using var cli = await CliAsync(Server(new Queue<Func<HttpResponseMessage>>([() => Run("finalized", 10)])));
        (await cli.RunAsync(["migrate", "activate", RunId.ToString(), .. args])).Should().Be(2, cli.Err.ToString());
        Calls(cli).Should().NotContain(c => c.Path.EndsWith("/activate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Activate_dry_run_sends_ids_and_dryRun()
    {
        using var cli = await CliAsync(Server(new Queue<Func<HttpResponseMessage>>([() => Run("finalized", 10)])));
        (await cli.RunAsync("migrate", "activate", RunId.ToString(), "--id", "66666666-6666-6666-6666-666666666666", "--dry-run")).Should().Be(0, cli.Err.ToString());
        var body = JsonNode.Parse(Calls(cli).Single(c => c.Path.EndsWith("/activate", StringComparison.Ordinal)).Body!)!;
        body["dryRun"]!.GetValue<bool>().Should().BeTrue();
        body["ids"]![0]!.GetValue<string>().Should().Be("66666666-6666-6666-6666-666666666666");
    }

    [Fact]
    public void Report_markdown_lists_left_off_logic()
    {
        var report = System.Text.Json.JsonDocument.Parse("""
            {"runId":"r","state":"finalized","tables":[],"issues":[],"phases":[],
             "deactivated":[{"table":"workflow","id":"77777777-7777-7777-7777-777777777777","name":"Welcome mail","sourceState":1,"sourceStatus":2,"activatedOn":null}]}
            """).RootElement;
        MigrateReportFormat.Markdown(report).Should().Contain("## Business logic left off").And.Contain("| workflow | Welcome mail | 77777777-7777-7777-7777-777777777777 | 1 |");
    }
}
