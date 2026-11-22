using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Starting a migration run (migration M3, task 5; spec §7.1–§7.2, plan rulings R1, R6–R8): the run is a system job that verifies
/// the package and finalizes itself; the start refuses a wrong passphrase, an organization with business data, a second open run and
/// a server path outside the import root. Each test serves a database of its own with the async worker on.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationRunTests(XrmdFixture fx) : IDisposable
{
    private const string Api = "api/data/v9.2/";
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-m3-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Host(WebApplicationFactory<Program> app, XrmdFixture fx) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> App { get; } = app;
        public HttpClient Client { get; } = app.CreateClient();
        public string Token { get; } = DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl);
        public string ConnectionString => App.Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!;

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
        {
            var req = new HttpRequestMessage(method, path) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return Client.SendAsync(req);
        }

        public async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var conn = await TestProvider.OpenAsync(ConnectionString);
            await using var cmd = TestSql.Command(conn, sql, parameters);
            return await cmd.ExecuteScalarAsync();
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private Host Start(string? importRoot = null, Action<IServiceCollection>? services = null, bool keepDatabase = false,
        string? connectionString = null) => new(fx.WithOwnDatabase(b =>
    {
        b.UseSetting("Pipeline:AsyncWorkerEnabled", "true");
        if (importRoot is not null) b.UseSetting(MigrationPackages.ImportRootSetting, importRoot);
        if (connectionString is not null) b.UseSetting("ConnectionStrings:Org", connectionString);
        if (services is not null) b.ConfigureServices(services);
    }, keepDatabase), fx);

    /// <summary>Phase 1 counts its runs (it still verifies); phase 2 waits for <paramref name="gate"/> unless it is null.</summary>
    private static Action<IServiceCollection> Phases(Counter verifyRuns, TaskCompletionSource? gate, TaskCompletionSource? entered = null) => s =>
    {
        foreach (var d in s.Where(d => d.ImplementationType == typeof(VerifyPackagePhase)).ToList()) s.Remove(d);
        s.AddSingleton<IMigrationPhase>(new CountingPhase(new VerifyPackagePhase(), verifyRuns));
        s.AddSingleton<IMigrationPhase>(new GatePhase(gate, entered));
    };

    private sealed class Counter
    {
        private int _value;
        public int Value => Volatile.Read(ref _value);
        public void Increment() => Interlocked.Increment(ref _value);
    }

    private sealed class CountingPhase(IMigrationPhase inner, Counter runs) : IMigrationPhase
    {
        public int Order => inner.Order;
        public string Name => inner.Name;

        public Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
        {
            runs.Increment();
            return inner.RunAsync(context, ct);
        }
    }

    private sealed class GatePhase(TaskCompletionSource? gate, TaskCompletionSource? entered) : IMigrationPhase
    {
        public int Order => 2;
        public string Name => "gate";

        public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
        {
            entered?.TrySetResult();
            if (gate is not null) await gate.Task.WaitAsync(ct);
        }
    }

    /// <summary>Adds <paramref name="phases"/> to the host's phases.</summary>
    private static Action<IServiceCollection> With(params IMigrationPhase[] phases) => s =>
    {
        foreach (var phase in phases) s.AddSingleton(phase);
    };

    private sealed class ProbePhase(int order, Func<MigrationPhaseContext, Task> run) : IMigrationPhase
    {
        public int Order => order;
        public string Name => "probe" + order;
        public Task RunAsync(MigrationPhaseContext context, CancellationToken ct) => run(context);
    }

    /// <summary>The order of the last phase the host runs: the phase a completed run records.</summary>
    private static int LastPhase(Host host) => host.App.Services.GetServices<IMigrationPhase>().Max(p => p.Order);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<HttpResponseMessage> PostAsync(Host host, string path, JsonObject? body = null) =>
        host.SendAsync(HttpMethod.Post, path, body is null ? null : Json(body));

    private static StringContent AccountJson() => new("""{"name":"after"}""", Encoding.UTF8, "application/json");

    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonObject StartBody(Guid? packageId = null, string? serverPath = null, string passphrase = MigrationTestPackage.Passphrase)
    {
        var body = new JsonObject { ["passphrase"] = passphrase };
        if (packageId is { } id) body["packageId"] = id.ToString();
        if (serverPath is not null) body["serverPath"] = serverPath;
        return body;
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    private async Task<Guid> UploadAsync(Host host, bool damage = false) =>
        await MigrationTestPackage.UploadAsync(host.Client, host.Token, MigrationTestPackage.Build(_root, damage));

    private static async Task<Guid> StartRunAsync(Host host, Guid packageId)
    {
        var started = await ReadAsync(await host.SendAsync(HttpMethod.Post, "admin/migrate/runs", Json(StartBody(packageId))), HttpStatusCode.Accepted);
        return Guid.Parse(started["runId"]!.GetValue<string>());
    }

    /// <summary>Polls the run until its state is one of <paramref name="states"/>, at most 30 s.</summary>
    private static async Task<JsonObject> WaitAsync(Host host, Guid runId, params string[] states)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        JsonObject status;
        do
        {
            status = await ReadAsync(await host.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{runId}"), HttpStatusCode.OK);
            if (states.Contains(status["state"]!.GetValue<string>())) return status;
            await Task.Delay(250);
        }
        while (DateTime.UtcNow < until);
        throw new TimeoutException($"The run {runId} is still {status["state"]} ({status["message"]}).");
    }

    [Fact]
    public async Task Start_runs_verify_and_finalizes()
    {
        await using var host = Start();
        var package = await UploadAsync(host);

        var run = await StartRunAsync(host, package);
        var status = await WaitAsync(host, run, MigrationRunStates.Finalized, MigrationRunStates.Failed);

        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Finalized, status["message"]?.ToString());
        status["phase"]!.GetValue<int>().Should().Be(LastPhase(host));
        status["phases"]!.AsArray().Select(p => p!.GetValue<string>()).Should().Equal(host.App.Services.GetServices<IMigrationPhase>().OrderBy(p => p.Order).Select(p => p.Name));
        host.App.Services.GetRequiredService<MigrationLock>().ActiveRunId.Should().BeNull();
        (await host.SendAsync(HttpMethod.Get, $"admin/migrate/packages/{package}")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the uploaded package goes once the run is finalized");
        var list = await ReadAsync(await host.SendAsync(HttpMethod.Get, "admin/migrate/runs"), HttpStatusCode.OK);
        list["value"]!.AsArray().Select(r => r!["runId"]!.GetValue<string>()).Should().Contain(run.ToString());
    }

    [Fact]
    public async Task Wrong_passphrase_is_400_and_queues_nothing()
    {
        await using var host = Start();
        var package = await UploadAsync(host);

        var res = await host.SendAsync(HttpMethod.Post, "admin/migrate/runs", Json(StartBody(package, passphrase: "a wrong passphrase!")));
        (await ReadAsync(res, HttpStatusCode.BadRequest)).ToJsonString().Should().Contain("The passphrase is wrong for this package.");

        (await host.ScalarAsync("SELECT COUNT(*) FROM meta.migration_run")).Should().Be(0);
        (await host.ScalarAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype = 99010")).Should().Be(0);
        host.App.Services.GetRequiredService<MigrationLock>().ActiveRunId.Should().BeNull();
    }

    [Fact]
    public async Task Short_passphrase_is_400()
    {
        await using var host = Start();
        var res = await host.SendAsync(HttpMethod.Post, "admin/migrate/runs", Json(StartBody(Guid.NewGuid(), passphrase: "short")));
        (await ReadAsync(res, HttpStatusCode.BadRequest))["error"]!["message"]!.GetValue<string>().Should().StartWith("'passphrase' is required");
    }

    [Fact]
    public async Task Business_data_present_is_409_with_tables()
    {
        await using var host = Start();
        (await host.SendAsync(HttpMethod.Post, Api + "accounts", new StringContent("""{"name":"existing"}""", Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var package = await UploadAsync(host);

        var body = await ReadAsync(await host.SendAsync(HttpMethod.Post, "admin/migrate/runs", Json(StartBody(package))), HttpStatusCode.Conflict);

        body["tables"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("account");
        body["error"]!["message"]!.GetValue<string>().Should().Contain("already holds business data");
        (await host.ScalarAsync("SELECT COUNT(*) FROM meta.migration_run")).Should().Be(0);
        host.App.Services.GetRequiredService<MigrationLock>().ActiveRunId.Should().BeNull();
    }

    [Fact]
    public async Task Start_while_another_job_runs_is_409_and_leaves_no_lock()
    {
        await using var host = Start();
        var package = await UploadAsync(host);
        var admin = await TestServices.CallerAsync(host.App.Services, fx.AdminObjectId);
        Guid busy;
        await using (var conn = await TestProvider.OpenAsync(host.ConnectionString))
        {
            busy = await host.App.Services.GetRequiredService<Xrmd.Pipeline.Async.AsyncOperationQueue>().EnqueueSystemJobAsync(new DataSession(conn, null),
                admin, Xrmd.Pipeline.Async.AsyncOperationQueue.DuplicateRulePublish, "busy", "PublishDuplicateRule", null, null, null, default);
        }
        // As the worker leaves a job it took: in progress.
        await host.ScalarAsync("UPDATE data.asyncoperation SET statecode = 2, statuscode = 20 WHERE asyncoperationid = @id", ("@id", busy));

        var res = await host.SendAsync(HttpMethod.Post, "admin/migrate/runs", Json(StartBody(package)));

        (await ReadAsync(res, HttpStatusCode.Conflict))["error"]!["message"]!.GetValue<string>().Should().Contain("System jobs are in progress");
        (await host.ScalarAsync("SELECT COUNT(*) FROM meta.migration_run")).Should().Be(0);
        host.App.Services.GetRequiredService<MigrationLock>().ActiveRunId.Should().BeNull();
    }

    [Fact]
    public async Task Second_start_while_open_is_409()
    {
        await using var host = Start();
        var damaged = await UploadAsync(host, damage: true);
        var run = await StartRunAsync(host, damaged);
        await WaitAsync(host, run, MigrationRunStates.Failed);
        var other = await UploadAsync(host);

        var res = await host.SendAsync(HttpMethod.Post, "admin/migrate/runs", Json(StartBody(other)));

        (await ReadAsync(res, HttpStatusCode.Conflict)).ToJsonString().Should().Contain($"Another migration run is open: {run}");
    }

    [Fact]
    public async Task Damaged_package_fails_and_keeps_lock()
    {
        await using var host = Start();
        var package = await UploadAsync(host, damage: true);

        var run = await StartRunAsync(host, package);
        var status = await WaitAsync(host, run, MigrationRunStates.Failed, MigrationRunStates.Finalized);

        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Failed);
        status["message"]!.GetValue<string>().Should().StartWith("The package is damaged");
        status["phase"]!.GetValue<int>().Should().Be(0);
        var write = await host.SendAsync(HttpMethod.Post, Api + "accounts", new StringContent("""{"name":"x"}""", Encoding.UTF8, "application/json"));
        write.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await host.SendAsync(HttpMethod.Get, $"admin/migrate/packages/{package}")).StatusCode.Should().Be(HttpStatusCode.OK, "a failed run keeps its package");
    }

    [Fact]
    public async Task Server_path_runs_from_import_root()
    {
        var file = MigrationTestPackage.Build(_root);
        await using var host = Start(importRoot: _root);

        var started = await ReadAsync(await host.SendAsync(HttpMethod.Post, "admin/migrate/runs", Json(StartBody(serverPath: file))), HttpStatusCode.Accepted);
        var status = await WaitAsync(host, Guid.Parse(started["runId"]!.GetValue<string>()), MigrationRunStates.Finalized, MigrationRunStates.Failed);

        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Finalized, status["message"]?.ToString());
        File.Exists(file).Should().BeTrue("a package under the import root is the administrator's: the run never deletes it");
    }

    [Fact]
    public async Task Server_path_outside_root_is_400()
    {
        var file = MigrationTestPackage.Build(_root);
        await using var host = Start(importRoot: Path.Combine(_root, "imports"));

        var res = await host.SendAsync(HttpMethod.Post, "admin/migrate/runs", Json(StartBody(serverPath: file)));

        (await ReadAsync(res, HttpStatusCode.BadRequest)).ToJsonString().Should().Contain("must be under Migration:ImportRoot");
    }

    [Fact]
    public async Task Server_path_disabled_is_400()
    {
        var file = MigrationTestPackage.Build(_root);
        await using var host = Start();

        var res = await host.SendAsync(HttpMethod.Post, "admin/migrate/runs", Json(StartBody(serverPath: file)));

        (await ReadAsync(res, HttpStatusCode.BadRequest)).ToJsonString().Should().Contain("Server paths are disabled");
    }

    [Fact]
    public async Task Passphrase_never_in_ledger_or_job_data()
    {
        await using var host = Start();
        var package = await UploadAsync(host);
        var run = await StartRunAsync(host, package);
        await WaitAsync(host, run, MigrationRunStates.Finalized, MigrationRunStates.Failed);

        var pattern = ("@p", (object)("%" + MigrationTestPackage.Passphrase + "%"));
        (await host.ScalarAsync("SELECT COUNT(*) FROM meta.migration_run WHERE options LIKE @p OR message LIKE @p", pattern)).Should().Be(0);
        (await host.ScalarAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE data LIKE @p OR message LIKE @p OR friendlymessage LIKE @p", pattern))
            .Should().Be(0);
        (await host.ScalarAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype = 99010")).Should().Be(1);
    }

    [Fact]
    public async Task Fresh_organization_has_no_business_data()
    {
        await using var host = Start();
        var store = host.App.Services.GetRequiredService<IMetadataStore>();
        await using var conn = await TestProvider.OpenAsync(host.ConnectionString);

        // Every table an export can carry: the ones its exclusion catalog does not leave out (M2).
        var exportable = store.GetAllEntities().Select(e => e.LogicalName).Where(t => Xrmd.Migration.Source.ExclusionCatalog.Find(t) is null);

        var busy = await MigrationPreconditions.TablesWithDataAsync(new DataSession(conn, null), store, exportable, default);

        string.Join(", ", busy).Should().BeEmpty("a fresh organization holds only the rows it is created with");
    }

    [Fact]
    public async Task Cancel_running_then_resume_completes()
    {
        var verifyRuns = new Counter();
        var gate = NewSignal();
        var entered = NewSignal();
        await using var host = Start(services: Phases(verifyRuns, gate, entered));
        var package = await UploadAsync(host);
        var run = await StartRunAsync(host, package);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        (await PostAsync(host, $"admin/migrate/runs/{run}/cancel")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var cancelled = await WaitAsync(host, run, MigrationRunStates.Cancelled, MigrationRunStates.Failed);
        cancelled["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Cancelled, cancelled["message"]?.ToString());
        cancelled["phase"]!.GetValue<int>().Should().Be(2, "verify and schema (no solutions in the package) are done; the gate shares order 2 and runs after them");
        (await host.SendAsync(HttpMethod.Post, Api + "accounts", AccountJson())).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        gate.SetResult();
        var resumed = await ReadAsync(await PostAsync(host, $"admin/migrate/runs/{run}/resume", new JsonObject { ["passphrase"] = MigrationTestPackage.Passphrase }),
            HttpStatusCode.Accepted);
        resumed["asyncOperationId"]!.GetValue<string>().Should().NotBe(cancelled["asyncOperationId"]!.GetValue<string>());
        var done = await WaitAsync(host, run, MigrationRunStates.Finalized, MigrationRunStates.Failed);

        done["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Finalized, done["message"]?.ToString());
        done["phase"]!.GetValue<int>().Should().Be(LastPhase(host));
        verifyRuns.Value.Should().Be(1, "a resume starts at the first phase not completed");
        (await host.SendAsync(HttpMethod.Post, Api + "accounts", AccountJson())).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Cancel_queued_is_200_and_job_never_runs()
    {
        var verifyRuns = new Counter();
        await using var host = Start(services: Phases(verifyRuns, null));
        var package = await UploadAsync(host);
        var run = Guid.NewGuid();
        var store = new MigrationRunStore(host.App.Services.GetRequiredService<IDbConnectionFactory>());
        // A queued run whose job is queued but not woken: the worker polls, so cancel at once.
        await store.CreateRunAsync(new MigrationRunRow(run, package, null, MigrationRunStates.Queued, 0, "{}", null, null, Guid.Empty, DateTime.UtcNow,
            null, null, null), default);
        host.App.Services.GetRequiredService<MigrationLock>().Set(run);

        var res = await ReadAsync(await PostAsync(host, $"admin/migrate/runs/{run}/cancel"), HttpStatusCode.OK);

        res["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Cancelled);
        (await PostAsync(host, $"admin/migrate/runs/{run}/cancel")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        verifyRuns.Value.Should().Be(0);
    }

    [Fact]
    public async Task Restart_mid_run_needs_passphrase_on_resume()
    {
        string database;
        Guid run;
        var entered = NewSignal();
        await using (var first = Start(services: Phases(new Counter(), NewSignal(), entered), keepDatabase: true))
        {
            database = TestProvider.DatabaseOf(first.ConnectionString);
            run = await StartRunAsync(first, await UploadAsync(first));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        try
        {
            var connection = TestProvider.WithDatabase(fx.ConnectionString, database);
            var verifyRuns = new Counter();
            await using var second = Start(services: Phases(verifyRuns, null), connectionString: connection);

            var status = await ReadAsync(await second.SendAsync(HttpMethod.Get, $"admin/migrate/runs/{run}"), HttpStatusCode.OK);
            status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Failed);
            status["message"]!.GetValue<string>().Should().Contain("resume it with the package passphrase");
            (await second.SendAsync(HttpMethod.Post, Api + "accounts", AccountJson())).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            (await PostAsync(second, $"admin/migrate/runs/{run}/resume", new JsonObject { ["passphrase"] = "a wrong passphrase!" }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await PostAsync(second, $"admin/migrate/runs/{run}/resume", new JsonObject { ["passphrase"] = MigrationTestPackage.Passphrase }))
                .StatusCode.Should().Be(HttpStatusCode.Accepted);
            var done = await WaitAsync(second, run, MigrationRunStates.Finalized, MigrationRunStates.Failed);
            done["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Finalized, done["message"]?.ToString());
            verifyRuns.Value.Should().Be(0, "verify completed before the restart");
        }
        finally
        {
            await fx.DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Finalize_running_is_409()
    {
        var gate = NewSignal();
        var entered = NewSignal();
        await using var host = Start(services: Phases(new Counter(), gate, entered));
        var run = await StartRunAsync(host, await UploadAsync(host));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var res = await PostAsync(host, $"admin/migrate/runs/{run}/finalize");

        (await ReadAsync(res, HttpStatusCode.Conflict)).ToJsonString().Should().Contain("cancel it before finalizing it");
        gate.SetResult();
        await WaitAsync(host, run, MigrationRunStates.Finalized);
    }

    [Fact]
    public async Task Finalize_failed_lifts_lock_and_deletes_package()
    {
        await using var host = Start();
        var package = await UploadAsync(host, damage: true);
        var run = await StartRunAsync(host, package);
        await WaitAsync(host, run, MigrationRunStates.Failed);

        var res = await ReadAsync(await PostAsync(host, $"admin/migrate/runs/{run}/finalize"), HttpStatusCode.OK);

        res["state"]!.GetValue<string>().Should().Be(MigrationRunStates.Finalized);
        res["finalizedOn"].Should().NotBeNull();
        (await host.SendAsync(HttpMethod.Post, Api + "accounts", AccountJson())).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await host.SendAsync(HttpMethod.Get, $"admin/migrate/packages/{package}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Finalize_keepPackage_keeps_it()
    {
        await using var host = Start();
        var package = await UploadAsync(host, damage: true);
        var run = await StartRunAsync(host, package);
        await WaitAsync(host, run, MigrationRunStates.Failed);

        (await PostAsync(host, $"admin/migrate/runs/{run}/finalize", new JsonObject { ["keepPackage"] = true })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await host.SendAsync(HttpMethod.Get, $"admin/migrate/packages/{package}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.SendAsync(HttpMethod.Delete, $"admin/migrate/packages/{package}")).StatusCode.Should().Be(HttpStatusCode.NoContent,
            "a finalized run no longer holds its package");
    }

    [Fact]
    public async Task Resume_finalized_is_409()
    {
        await using var host = Start();
        var run = await StartRunAsync(host, await UploadAsync(host));
        await WaitAsync(host, run, MigrationRunStates.Finalized);

        var res = await PostAsync(host, $"admin/migrate/runs/{run}/resume", new JsonObject { ["passphrase"] = MigrationTestPackage.Passphrase });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PostAsync(host, $"admin/migrate/runs/{run}/finalize")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PostAsync(host, $"admin/migrate/runs/{Guid.NewGuid()}/resume", new JsonObject { ["passphrase"] = MigrationTestPackage.Passphrase }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Job_ends_completed_with_errors_when_an_error_issue_exists()
    {
        var phase = new ProbePhase(50, c => c.Ledger.IssueAsync(
            new Xrmd.Admin.Migration.Import.MigrationIssue(50, Xrmd.Admin.Migration.Import.MigrationIssue.Error, "0x80040203", "account", Guid.NewGuid(), null, "Refused."), default));
        await using var host = Start(services: With(phase));
        var package = await UploadAsync(host);
        var run = await StartRunAsync(host, package);

        var status = await WaitAsync(host, run, MigrationRunStates.CompletedWithErrors, MigrationRunStates.Finalized, MigrationRunStates.Failed);

        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.CompletedWithErrors, status["message"]?.ToString());
        host.App.Services.GetRequiredService<MigrationLock>().ActiveRunId.Should().Be(run);
        (await host.SendAsync(HttpMethod.Get, $"admin/migrate/packages/{package}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Phase_runs_inside_migration_scope()
    {
        bool? inScope = null;
        var phase = new ProbePhase(50, _ => { inScope = MigrationScope.IsActive; return Task.CompletedTask; });
        await using var host = Start(services: With(phase));
        var run = await StartRunAsync(host, await UploadAsync(host));

        await WaitAsync(host, run, MigrationRunStates.Finalized, MigrationRunStates.Failed);

        inScope.Should().BeTrue();
        var timings = await host.ScalarAsync("SELECT COUNT(*) FROM meta.migration_phase WHERE runid = @id AND endedon IS NOT NULL", ("@id", run));
        timings.Should().Be(host.App.Services.GetServices<IMigrationPhase>().Count(), "every phase, the probe included");
    }
}
