using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xrmd.Abstractions.Storage;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Integration.Tests;

/// <summary>
/// A database container with an empty organization database, shared by the test fixtures: SQL Server, or PostgreSQL 16
/// (<c>postgres:16-alpine</c>) when the run is on PostgreSQL (<see cref="TestProvider"/>; plan 2.7a, Task 5).
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private const string DatabaseName = "xrmverse_test";
    /// <summary>
    /// SQL Server's own memory cap (<c>memory.memorylimitmb</c>; 2048 MB is the least it accepts). Uncapped, it sizes itself from
    /// the machine's memory, and every collection running in parallel has a container of its own: on a CI runner (7 GB for the
    /// whole job) they would take the memory of the test process.
    /// </summary>
    private const string MemoryLimitMb = "2048";

    private readonly MsSqlContainer? _sql;
    private readonly PostgreSqlContainer? _postgres;

    /// <param name="provider">The engine; <see cref="TestProvider.Current"/> when null.</param>
    public TestDatabase(StorageProvider? provider = null)
    {
        Provider = provider ?? TestProvider.Current;
        if (Provider == StorageProvider.PostgreSql)
            // Task 5 fix round 1: the default 100 connections ran out on a full run (53300) with every host's pool on one server.
            _postgres = new PostgreSqlBuilder("postgres:16-alpine").WithCommand("-c", "max_connections=500").Build();
        else
            _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", MemoryLimitMb)
                .Build();
    }

    /// <summary>The engine of the container.</summary>
    public StorageProvider Provider { get; }

    public string ConnectionString { get; private set; } = "";

    private IContainer Container => (IContainer?)_sql ?? _postgres!;

    public async Task StartAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.StartAsync();
            var server = _postgres.GetConnectionString();
            // As the provisioning creates an organization database (ruling P5).
            await PostgresDatabases.CreateDatabaseAsync(server, DatabaseName, CancellationToken.None);
            ConnectionString = TestProvider.WithDatabase(StorageProvider.PostgreSql, server, DatabaseName);
            return;
        }

        await _sql!.StartAsync();

        await using (var master = new SqlConnection(_sql.GetConnectionString()))
        {
            await master.OpenAsync();
            await using var cmd = master.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{DatabaseName}]";
            await cmd.ExecuteNonQueryAsync();
        }

        ConnectionString = new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = DatabaseName }.ConnectionString;
    }

    /// <summary>Freezes the database container: connections and commands hang (an unreachable server that does not refuse).</summary>
    public Task PauseAsync() => Container.PauseAsync();

    public Task UnpauseAsync() => Container.UnpauseAsync();

    public ValueTask DisposeAsync() => Container.DisposeAsync();
}
