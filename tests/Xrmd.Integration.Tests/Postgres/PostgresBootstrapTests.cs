using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin;
using Xrmd.Host;
using Xrmd.Metadata;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>A PostgreSQL 16 container with the organization database of <see cref="TestDatabase"/>, without a host.</summary>
public sealed class PostgresServer : IAsyncLifetime
{
    private readonly TestDatabase _db = new(StorageProvider.PostgreSql);

    public string ConnectionString => _db.ConnectionString;

    public string ConnectionStringOf(string database) => TestProvider.WithDatabase(StorageProvider.PostgreSql, ConnectionString, database);

    public Task InitializeAsync() => _db.StartAsync();

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();
}

/// <summary>
/// Plan 2.7a, Task 5: the startup of a PostgreSQL organization. The startup lock is a session advisory lock in the maintenance
/// database, so two processes bootstrapping one new database create it (as ruling P5 says) and migrate it once; the instance lock
/// is a session advisory lock in the organization database, so a second process is refused.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class PostgresBootstrapTests(PostgresServer fx) : IClassFixture<PostgresServer>
{
    private async Task<T?> ScalarAsync<T>(string database, string sql)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionStringOf(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    [Fact]
    public async Task Two_hosts_bootstrapping_one_new_database_migrate_it_once()
    {
        var name = "race_" + Guid.NewGuid().ToString("N")[..8];
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Org"] = fx.ConnectionStringOf(name),
            [StorageSettings.ProviderKey] = "PostgreSql",
        }).Build();

        var running = 0;
        var maxRunning = 0;
        var migrated = 0;
        async Task Work()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            try
            {
                // What the host's populate path does first: migrate (on services of its own, bound to the new database).
                await using var services = OrganizationDatabase.BuildServices(fx.ConnectionStringOf(name), StorageProvider.PostgreSql);
                var migrator = services.GetRequiredService<SchemaMigrator>();
                if ((await migrator.GetStatusAsync()).PendingMigrations > 0) Interlocked.Increment(ref migrated);
                await migrator.MigrateAsync();
                await Task.Delay(300);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        await Task.WhenAll(
            Task.Run(() => DatabaseBootstrapper.RunExclusiveAsync(config, NullLogger.Instance, Work)),
            Task.Run(() => DatabaseBootstrapper.RunExclusiveAsync(config, NullLogger.Instance, Work)));

        maxRunning.Should().Be(1, "the startup work is serialized by the advisory lock");
        migrated.Should().Be(1, "the second process finds the database migrated");
        (await ScalarAsync<long>(name, "SELECT COUNT(*) FROM meta.schema_version")).Should().Be(21);
        (await ScalarAsync<long>(name, "SELECT COUNT(DISTINCT version) FROM meta.schema_version")).Should().Be(21);
        (await ScalarAsync<string>(PostgresDatabases.MaintenanceDatabase, $"SELECT datlocprovider::text || '/' || daticulocale FROM pg_database WHERE datname = '{name}'"))
            .Should().Be("i/und", "the bootstrapper creates a missing database as ruling P5 says");
    }

    [Fact]
    public async Task A_second_process_is_refused_the_instance_lock_of_a_postgresql_database()
    {
        var cs = fx.ConnectionString;
        await using var first = await InstanceLock.AcquireAsync(cs, NullLogger.Instance, CancellationToken.None, StorageProvider.PostgreSql);
        var second = () => InstanceLock.AcquireAsync(cs, NullLogger.Instance, CancellationToken.None, StorageProvider.PostgreSql);
        (await second.Should().ThrowAsync<InstanceLockException>()).Which.Message.Should().Contain("Another xrmd process already serves");
        (await InstanceLock.IsHeldByAnotherAsync(cs, StorageProvider.PostgreSql, CancellationToken.None)).Should().BeTrue();
        (await first.EnsureHeldAsync(CancellationToken.None)).Should().BeTrue();

        await first.DisposeAsync();
        (await InstanceLock.IsHeldByAnotherAsync(cs, StorageProvider.PostgreSql, CancellationToken.None)).Should().BeFalse();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int cur;
        while (value > (cur = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, cur) != cur) { }
    }
}
