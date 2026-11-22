using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The storage provider the integration tests run on (plan 2.7a, Task 5): <c>XRMD_TEST_PROVIDER</c> = <c>sqlserver</c> (the default
/// when absent) or <c>postgres</c>. <see cref="TestDatabase"/> starts that engine and <see cref="XrmdFixture"/> sets
/// <c>Storage:Provider</c> to it. The helpers here keep the fixtures' own SQL (connection strings, databases) provider-neutral.
/// </summary>
public static class TestProvider
{
    public const string EnvironmentVariable = "XRMD_TEST_PROVIDER";

    /// <summary>The provider of this run, from <see cref="EnvironmentVariable"/>.</summary>
    public static StorageProvider Current { get; } = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static bool IsPostgres => Current == StorageProvider.PostgreSql;

    /// <summary>The provider <paramref name="value"/> names: <c>sqlserver</c> (or nothing) or <c>postgres</c>, ignoring case.</summary>
    public static StorageProvider Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "sqlserver" or "mssql" => StorageProvider.SqlServer,
        "postgres" or "postgresql" or "pg" => StorageProvider.PostgreSql,
        _ => throw new InvalidOperationException($"{EnvironmentVariable} must be sqlserver or postgres; got '{value}'."),
    };

    /// <summary>The <c>Storage:Provider</c> setting of <paramref name="provider"/>.</summary>
    public static string Setting(StorageProvider provider) => provider.ToString();

    /// <summary><paramref name="connectionString"/> (of <paramref name="provider"/>) with its database replaced by <paramref name="database"/>.</summary>
    public static string WithDatabase(StorageProvider provider, string connectionString, string database) => provider == StorageProvider.PostgreSql
        ? new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString
        : new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;

    /// <summary>The database <paramref name="connectionString"/> names.</summary>
    public static string DatabaseOf(string connectionString) => StorageConnectionStrings.DatabaseName(connectionString);

    /// <summary>The provider of <paramref name="connectionString"/>: an Npgsql string names a <c>Host</c>, a SQL Server one a <c>Server</c>/<c>Data Source</c>.</summary>
    public static StorageProvider ProviderOf(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return builder.ContainsKey("Host") ? StorageProvider.PostgreSql : StorageProvider.SqlServer;
    }

    /// <summary>An open connection of the right provider on <paramref name="connectionString"/> (the tests' own SQL).</summary>
    public static async Task<DbConnection> OpenAsync(string connectionString)
    {
        // PostgreSQL: through the data source the server uses for this string (UTC sessions, the citext types it reloaded).
        if (ProviderOf(connectionString) == StorageProvider.PostgreSql) return await new NpgsqlConnectionFactory(connectionString).OpenAsync();
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>The connection factory of the right provider on <paramref name="connectionString"/> (plan 2.7a, Task 6).</summary>
    public static IDbConnectionFactory Factory(string connectionString) => ProviderOf(connectionString) == StorageProvider.PostgreSql
        ? new NpgsqlConnectionFactory(connectionString)
        : new Xrmd.Core.SqlConnectionFactory(connectionString);

    /// <summary><paramref name="connectionString"/> with its database replaced by <paramref name="database"/>, of the string's provider.</summary>
    public static string WithDatabase(string connectionString, string database) =>
        WithDatabase(ProviderOf(connectionString), connectionString, database);

    /// <summary>The server's maintenance database of <paramref name="connectionString"/>'s provider: <c>master</c> on SQL Server, <c>postgres</c> on PostgreSQL.</summary>
    public static string MaintenanceDatabase(string connectionString) =>
        ProviderOf(connectionString) == StorageProvider.PostgreSql ? "postgres" : "master";

    /// <summary><paramref name="connectionString"/> on the server's maintenance database (<see cref="MaintenanceDatabase"/>).</summary>
    public static string Maintenance(string connectionString) => WithDatabase(connectionString, MaintenanceDatabase(connectionString));

    /// <summary>
    /// Creates the empty database <paramref name="database"/> on the server of <paramref name="connectionString"/> (plan 2.7a,
    /// Task 6): <c>CREATE DATABASE</c> on SQL Server, the organization statement of ruling P5 on PostgreSQL.
    /// </summary>
    public static async Task CreateDatabaseAsync(string connectionString, string database)
    {
        if (ProviderOf(connectionString) == StorageProvider.PostgreSql)
        {
            await PostgresDatabases.CreateDatabaseAsync(connectionString, database, CancellationToken.None);
            return;
        }

        await using var conn = await OpenAsync(connectionString);
        await using var cmd = conn.Command($"CREATE DATABASE [{database}]");
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>True when <paramref name="database"/> exists on the server of <paramref name="connectionString"/>.</summary>
    public static async Task<bool> DatabaseExistsAsync(string connectionString, string database) =>
        ProviderOf(connectionString) == StorageProvider.PostgreSql
            // A literal: the maintenance database has no citext, the type the data source gives a string parameter.
            ? await TestSql.ScalarAsync(Maintenance(connectionString), "SELECT 1 FROM pg_database WHERE datname = " + TestSql.Text(database)) is not null
            : await TestSql.ScalarAsync(Maintenance(connectionString), "SELECT DB_ID(@n)", ("@n", database)) is not null;

    /// <summary>
    /// Drops <paramref name="database"/> on the server of <paramref name="connectionString"/> (any database of that server), if it
    /// exists, ending its sessions first.
    /// </summary>
    public static async Task DropDatabaseAsync(string connectionString, string database)
    {
        if (ProviderOf(connectionString) == StorageProvider.PostgreSql)
        {
            await PostgresDatabases.DropDatabaseAsync(connectionString, database, CancellationToken.None);
            return;
        }

        await using var master = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await using var drop = new SqlCommand("""
            IF DB_ID(@db) IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(400) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@db);
                EXEC(@sql);
            END
            """, master);
        drop.Parameters.AddWithValue("@db", database);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The directory of the PostgreSQL client tools of this machine for backup and restore (plan 2.7b, Task 9;
    /// <c>Storage:PgToolsPath</c>): <c>XRMD_TEST_PG_TOOLS</c> when set, else the highest <c>/usr/lib/postgresql/&lt;major&gt;/bin</c>
    /// that has <c>pg_dump</c> (Debian and Ubuntu, GitHub's runners included), else the directory of <c>pg_dump</c> on <c>PATH</c>;
    /// null when there is none.
    /// </summary>
    public static string? PgToolsPath { get; } = FindPgTools();

    private static string? FindPgTools()
    {
        if (Environment.GetEnvironmentVariable("XRMD_TEST_PG_TOOLS") is { Length: > 0 } configured) return configured;
        const string debian = "/usr/lib/postgresql";
        if (Directory.Exists(debian))
        {
            var best = Directory.GetDirectories(debian)
                .Select(d => (Dir: Path.Combine(d, "bin"), Major: int.TryParse(Path.GetFileName(d), out var m) ? m : 0))
                .Where(c => c.Major >= Xrmd.Storage.PostgreSql.PgTools.MinimumMajorVersion && File.Exists(Path.Combine(c.Dir, "pg_dump")))
                .OrderByDescending(c => c.Major)
                .Select(c => c.Dir)
                .FirstOrDefault();
            if (best is not null) return best;
        }

        return Xrmd.Storage.PostgreSql.PgTools.FindOnPath("pg_dump", Environment.GetEnvironmentVariable("PATH"));
    }
}

/// <summary>
/// A fact about SQL Server's own engine artifacts (plan 2.7a, Task 6): a T-SQL migration script, a SQL Server database option, a
/// <c>sys.dm_*</c> view. Skipped, with <paramref name="reason"/>, when the run is on PostgreSQL, which has no such artifact (its
/// schema is one baseline script, ruling P4); not a way to skip a feature on PostgreSQL (since plan 2.7b both engines run every feature).
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute(string reason) => Skip = TestProvider.IsPostgres ? "SQL Server only: " + reason : null;
}
