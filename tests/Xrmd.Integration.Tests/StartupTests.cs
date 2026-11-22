using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xrmd.Host;

namespace Xrmd.Integration.Tests;

public class StartupTests
{
    [Fact]
    public async Task Startup_with_unreachable_sql_fails_after_retries_with_clear_message()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // Port 1 is closed: no SQL container needed.
            b.UseSetting("ConnectionStrings:Org", "Server=127.0.0.1,1;Database=xrmverse;User Id=sa;Password=x;Connect Timeout=1;TrustServerCertificate=True");
            b.UseSetting("Auth:Mode", "Development");
            b.UseSetting("Auth:Audience", "http://localhost/");
            b.UseSetting("Auth:DevSigningKey", DevTokenFactory.SigningKey);
            b.UseSetting("Seed:AdminObjectId", Guid.NewGuid().ToString());
            b.UseSetting("Startup:SqlRetries", "2");
            b.UseSetting("Startup:SqlRetryDelaySeconds", "0");
        });

        var act = () => Task.Run(() => app.CreateClient());
        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.ToString().Should().Contain("SQL Server not reachable after 2 attempts");
    }

    [Fact]
    public async Task Concurrent_bootstrap_of_a_new_database_serializes_and_both_succeed()
    {
        await using var db = new TestDatabase();
        await db.StartAsync();
        var dbName = "xrmverse_race_" + Guid.NewGuid().ToString("N")[..8];
        var cs = TestProvider.WithDatabase(db.ConnectionString, dbName);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [StorageSettings.ProviderKey] = TestProvider.Setting(TestProvider.Current), ["ConnectionStrings:Org"] = cs }).Build();

        var running = 0;
        var maxRunning = 0;
        async Task Work()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            await Task.Delay(500);
            Interlocked.Decrement(ref running);
        }

        await Task.WhenAll(
            Task.Run(() => DatabaseBootstrapper.RunExclusiveAsync(config, NullLogger.Instance, Work)),
            Task.Run(() => DatabaseBootstrapper.RunExclusiveAsync(config, NullLogger.Instance, Work)));

        maxRunning.Should().Be(1, "the startup work must be serialized by the applock");
        (await TestProvider.DatabaseExistsAsync(db.ConnectionString, dbName)).Should().BeTrue();
    }

    [Fact]
    public async Task Bootstraps_of_two_databases_on_one_server_do_not_wait_for_each_other()
    {
        // Issue #70: the organizations of one installation start together on one SQL Server; a server-wide startup lock made
        // each wait for the others' first migration and seed, past the lock timeout when they are slow.
        await using var db = new TestDatabase();
        await db.StartAsync();
        IConfiguration ConfigFor(string name) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [StorageSettings.ProviderKey] = TestProvider.Setting(TestProvider.Current),
            ["ConnectionStrings:Org"] = TestProvider.WithDatabase(db.ConnectionString, name),
        }).Build();
        var held = new TaskCompletionSource();
        var entered = new TaskCompletionSource();

        var first = Task.Run(() => DatabaseBootstrapper.RunExclusiveAsync(ConfigFor("xrmverse_one"), NullLogger.Instance, async () =>
        {
            entered.SetResult();
            await held.Task;
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await DatabaseBootstrapper.RunExclusiveAsync(ConfigFor("xrmverse_two"), NullLogger.Instance, () => Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            held.TrySetResult();
            await first;
        }
    }

    [Fact]
    public async Task Wrong_password_fails_fast_with_login_message()
    {
        await using var db = new TestDatabase();
        await db.StartAsync();
        var cs = new DbConnectionStringBuilder { ConnectionString = db.ConnectionString, ["Password"] = "Wrong_pw_1!" }.ConnectionString;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [StorageSettings.ProviderKey] = TestProvider.Setting(TestProvider.Current),
            ["ConnectionStrings:Org"] = cs, ["Startup:SqlRetries"] = "5", ["Startup:SqlRetryDelaySeconds"] = "30"
        }).Build();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var act = () => DatabaseBootstrapper.RunExclusiveAsync(config, NullLogger.Instance, () => Task.CompletedTask);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("login failed");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "a login failure is not retried");
    }

    [Fact]
    public async Task Connection_string_without_database_is_rejected()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Org"] = "Server=127.0.0.1,1;User Id=sa;Password=x" }).Build();
        var act = () => DatabaseBootstrapper.RunExclusiveAsync(config, NullLogger.Instance, () => Task.CompletedTask);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Database");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int cur;
        while (value > (cur = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, cur) != cur) { }
    }
}
