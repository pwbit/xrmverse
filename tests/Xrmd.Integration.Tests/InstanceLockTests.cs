using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xrmd.Host;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>One SQL Server container shared by the <see cref="InstanceLockTests"/>; each test uses databases of its own.</summary>
public sealed class InstanceLockSqlFixture : IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    public string ConnectionString => _db.ConnectionString;

    public Task InitializeAsync() => _db.StartAsync();

    public Task PauseAsync() => _db.PauseAsync();

    public Task UnpauseAsync() => _db.UnpauseAsync();

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();
}

/// <summary>One xrmd process per organization database: the <c>xrmd-instance</c> applock (plan 9, Task 1).</summary>
public sealed class InstanceLockTests(InstanceLockSqlFixture sql, ITestOutputHelper output) : IClassFixture<InstanceLockSqlFixture>, IDisposable
{
    private readonly Guid _adminOid = Guid.NewGuid();
    private readonly string _filesRoot = Path.Combine(Path.GetTempPath(), "xrmd-lock-files-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // The in-process watchdog sets the process-wide exit code when it stops a host; do not leak it into the test host.
        Environment.ExitCode = 0;
        if (Directory.Exists(_filesRoot)) Directory.Delete(_filesRoot, recursive: true);
    }

    private static string NewDatabase() => "lock_" + Guid.NewGuid().ToString("N")[..10];

    private string ConnectionStringOf(string database) => TestProvider.WithDatabase(sql.ConnectionString, database);

    private WebApplicationFactory<Program> Host(string database, bool asyncWorker = false, Guid? adminOid = null, int sqlRetries = 3, int sqlRetryDelaySeconds = 1) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting(StorageSettings.ProviderKey, TestProvider.Setting(TestProvider.Current));
            b.UseSetting("ConnectionStrings:Org", ConnectionStringOf(database));
            b.UseSetting("Auth:Mode", "Development");
            b.UseSetting("Auth:Audience", "http://localhost/");
            b.UseSetting("Auth:DevSigningKey", DevTokenFactory.SigningKey);
            b.UseSetting("Seed:OrgName", database);
            b.UseSetting("Seed:AdminObjectId", (adminOid ?? _adminOid).ToString());
            b.UseSetting("Pipeline:AsyncWorkerEnabled", asyncWorker ? "true" : "false");
            b.UseSetting("Files:FileSystem:Root", _filesRoot);
            b.UseSetting("Startup:SqlRetries", sqlRetries.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Startup:SqlRetryDelaySeconds", sqlRetryDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // The lock's and the watchdog's log lines in the test output.
            b.ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(new OutputLoggerProvider(output))));
        });

    private sealed class OutputLoggerProvider(ITestOutputHelper output) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName.Contains("InstanceLock") || categoryName.Contains("Xrmd.Host") ? new TestOutputLogger(output) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }
    }

    private static string HeldMessage(string database) =>
        $"Another xrmd process already serves the database '{database}' (applock 'xrmd-instance' is held). Run one xrmd process per organization database.";

    private static async Task<HttpStatusCode> HealthAsync(WebApplicationFactory<Program> host) =>
        (await host.CreateClient().GetAsync("/healthz")).StatusCode;

    /// <summary>
    /// The session that holds <c>xrmd-instance</c> in <paramref name="database"/> (its id and login time: the engine may reuse the id
    /// of a killed session), or null. The engine's lock views (plan 2.7a, Task 6): <c>sys.dm_tran_locks</c> on SQL Server, the
    /// advisory lock of <c>hashtextextended('xrmd-instance', 0)</c> in <c>pg_locks</c> on PostgreSQL.
    /// </summary>
    private async Task<(int Id, DateTime LoginTime)?> LockSessionAsync(string database)
    {
        await using var conn = await TestProvider.OpenAsync(TestProvider.Maintenance(sql.ConnectionString));
        await using var cmd = TestSql.Command(conn, TestProvider.IsPostgres
            ? $$"""
              SELECT l.pid, a.backend_start FROM pg_locks AS l JOIN pg_stat_activity AS a ON a.pid = l.pid
              WHERE l.locktype = 'advisory' AND l.granted AND l.mode = 'ExclusiveLock' AND l.objsubid = 1
                AND l.database = (SELECT oid FROM pg_database WHERE datname = {{TestSql.Text(database)}}) -- a literal: no citext in the maintenance database
                AND ((l.classid::bigint << 32) | l.objid::bigint) = hashtextextended('xrmd-instance', 0)
              LIMIT 1
              """
            : """
              SELECT TOP (1) l.request_session_id, s.login_time FROM sys.dm_tran_locks AS l
              JOIN sys.dm_exec_sessions AS s ON s.session_id = l.request_session_id
              WHERE l.resource_type = N'APPLICATION' AND l.resource_database_id = DB_ID(@db)
                AND l.resource_description LIKE N'%[[]xrmd-instance]%' AND l.request_status = N'GRANT'
              """, TestProvider.IsPostgres ? [] : [("@db", database)]);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt32(0), reader.GetDateTime(1)) : null;
    }

    private async Task KillAsync(int spid)
    {
        await using var conn = await TestProvider.OpenAsync(TestProvider.Maintenance(sql.ConnectionString));
        await using var cmd = TestSql.Command(conn, TestProvider.IsPostgres ? $"SELECT pg_terminate_backend({spid})" : $"KILL {spid}");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(250);
        }
        return await condition();
    }

    private static async Task<InstanceLockException> StartupFailureAsync(WebApplicationFactory<Program> host)
    {
        var act = () => Task.Run(() => host.CreateClient());
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        for (Exception? e = thrown; e is not null; e = e.InnerException)
            if (e is InstanceLockException lockException) return lockException;
        if (thrown is AggregateException aggregate && aggregate.Flatten().InnerExceptions.OfType<InstanceLockException>().FirstOrDefault() is { } inner)
            return inner;
        throw new Xunit.Sdk.XunitException($"Expected an InstanceLockException, got: {thrown}");
    }

    [Fact]
    public async Task Second_host_on_the_same_database_fails_startup_with_the_explicit_error()
    {
        var db = NewDatabase();
        await using var first = Host(db);
        (await HealthAsync(first)).Should().Be(HttpStatusCode.OK);

        await using var second = Host(db);
        (await StartupFailureAsync(second)).Message.Should().Be(HeldMessage(db));

        (await HealthAsync(first)).Should().Be(HttpStatusCode.OK, "the refused process does not disturb the one serving");
        (await LockSessionAsync(db)).Should().NotBeNull();
    }

    [Fact]
    public async Task Refused_second_host_neither_migrates_nor_seeds_the_served_database()
    {
        var db = NewDatabase();
        await using var first = Host(db);
        (await HealthAsync(first)).Should().Be(HttpStatusCode.OK);
        var versions = await ScalarAsync(db, "SELECT COUNT(*) FROM meta.schema_version");
        var users = await ScalarAsync(db, "SELECT COUNT(*) FROM data.systemuser");

        // A seeding process would make this other administrator a System Administrator of the served database.
        var otherAdmin = Guid.NewGuid();
        await using var second = Host(db, adminOid: otherAdmin);
        (await StartupFailureAsync(second)).Message.Should().Be(HeldMessage(db));

        (await ScalarAsync(db, "SELECT COUNT(*) FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", ("@oid", otherAdmin)))
            .Should().Be(0, "the refused process stops before the seed");
        (await ScalarAsync(db, "SELECT COUNT(*) FROM data.systemuser")).Should().Be(users);
        (await ScalarAsync(db, "SELECT COUNT(*) FROM meta.schema_version")).Should().Be(versions);
    }

    [Fact]
    public async Task Host_stops_within_the_bound_when_sql_server_hangs()
    {
        var db = NewDatabase();
        // Retries 2 x 1 s: the watchdog's deadline is its 5 s floor.
        await using var host = Host(db, sqlRetries: 2, sqlRetryDelaySeconds: 1);
        (await HealthAsync(host)).Should().Be(HttpStatusCode.OK);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await sql.PauseAsync();
        try
        {
            // Next check within 10 s, then at most the 5 s deadline (a connect timeout alone is 15 s per attempt).
            (await EventuallyAsync(() => Task.FromResult(lifetime.ApplicationStopping.IsCancellationRequested), TimeSpan.FromSeconds(20)))
                .Should().BeTrue("a process that cannot confirm its lock stops within the check interval plus the deadline");
        }
        finally
        {
            await sql.UnpauseAsync();
        }
    }

    private async Task<long> ScalarAsync(string database, string sqlText, params (string Name, object? Value)[] parameters) =>
        await TestSql.CountAsync(ConnectionStringOf(database), sqlText, parameters);

    [Fact]
    public async Task Hosts_on_different_databases_both_start()
    {
        await using var first = Host(NewDatabase());
        await using var second = Host(NewDatabase());
        (await HealthAsync(first)).Should().Be(HttpStatusCode.OK);
        (await HealthAsync(second)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lock_is_released_when_the_first_host_stops_and_a_new_host_then_starts()
    {
        var db = NewDatabase();
        var first = Host(db);
        (await HealthAsync(first)).Should().Be(HttpStatusCode.OK);
        (await LockSessionAsync(db)).Should().NotBeNull();
        await first.DisposeAsync();
        // SQL Server ends the session of a closed connection asynchronously: the lock goes within moments, not at once.
        (await EventuallyAsync(async () => await LockSessionAsync(db) is null, TimeSpan.FromSeconds(10)))
            .Should().BeTrue("stopping the process ends the lock session");

        await using var next = Host(db);
        (await HealthAsync(next)).Should().Be(HttpStatusCode.OK);
        (await LockSessionAsync(db)).Should().NotBeNull();
    }

    [Fact]
    public async Task Lock_is_reacquired_after_the_lock_session_is_killed()
    {
        var db = NewDatabase();
        await using var host = Host(db);
        (await HealthAsync(host)).Should().Be(HttpStatusCode.OK);
        var killed = (await LockSessionAsync(db))!.Value;

        await KillAsync(killed.Id);

        (await EventuallyAsync(async () => await LockSessionAsync(db) is { } session && session != killed, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the watchdog takes the lock again");
        (await HealthAsync(host)).Should().Be(HttpStatusCode.OK);
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task Host_stops_when_another_process_took_the_lock_after_a_loss()
    {
        var db = NewDatabase();
        await using var host = Host(db);
        (await HealthAsync(host)).Should().Be(HttpStatusCode.OK);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await KillAsync((await LockSessionAsync(db))!.Value.Id);
        // Another process takes the lock before the watchdog notices the loss.
        await using var other = await OrganizationDatabase.TryTakeInstanceLockAsync(ConnectionStringOf(db), TestProvider.Current, default);
        other.Should().NotBeNull("the lock is free once its session is killed");

        (await EventuallyAsync(() => Task.FromResult(lifetime.ApplicationStopping.IsCancellationRequested), TimeSpan.FromSeconds(30)))
            .Should().BeTrue("a process that lost its lock to another must not keep serving");
    }

    [Fact]
    public async Task Async_jobs_of_the_first_host_are_not_reset_by_a_refused_second_host()
    {
        var db = NewDatabase();
        await using var first = Host(db);
        (await HealthAsync(first)).Should().Be(HttpStatusCode.OK);
        var job = Guid.NewGuid();
        (await TestSql.ExecAsync(ConnectionStringOf(db), """
            INSERT INTO data.asyncoperation (asyncoperationid, name, operationtype, depth, retrycount, statecode, statuscode, startedon,
                createdon, createdby, modifiedon, modifiedby, ownerid, owneridtype, owninguser, owningbusinessunit)
            SELECT @id, N'running in the first process', 1, 1, 0, 2, 20, SYSUTCDATETIME(),
                SYSUTCDATETIME(), systemuserid, SYSUTCDATETIME(), systemuserid, systemuserid, 8, systemuserid, businessunitid
            FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid
            """, ("@oid", _adminOid), ("@id", job))).Should().Be(1);

        await using var second = Host(db, asyncWorker: true);
        await StartupFailureAsync(second);
        await Task.Delay(TimeSpan.FromSeconds(2)); // a worker that had started would have recovered the job by now

        (await TestSql.CountAsync(ConnectionStringOf(db), "SELECT statecode * 100 + statuscode FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", job)))
            .Should().Be(220, "the job stays Locked / In Progress");
    }
}
