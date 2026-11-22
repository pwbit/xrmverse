using System.Collections.Concurrent;
using System.Data.Common;
using Npgsql;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Storage.PostgreSql;

/// <summary>
/// The PostgreSQL <see cref="IDbConnectionFactory"/> (plan 2.7a, Task 3): opens connections from one <see cref="NpgsqlDataSource"/>
/// per connection string, shared by every factory of the process on that string (one connection pool, as SqlClient keeps one per
/// string). Sessions run in UTC (<see cref="Normalize"/>).
/// </summary>
public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private static readonly ConcurrentDictionary<string, NpgsqlDataSource> DataSources = new(StringComparer.Ordinal);

    private readonly NpgsqlDataSource _dataSource;

    /// <param name="connectionString">An Npgsql connection string (<c>Host=…;Database=…;Username=…;Password=…</c>).</param>
    /// <exception cref="ArgumentException">The connection string is empty or not an Npgsql one.</exception>
    public NpgsqlConnectionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        PostgresStorage.Register();
        _dataSource = DataSources.GetOrAdd(Normalize(connectionString), cs => NpgsqlDataSource.Create(cs));
    }

    public ISqlDialect Dialect => PostgresDialect.Instance;

    public Type ConnectionType => typeof(NpgsqlConnection);

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default) => await _dataSource.OpenConnectionAsync(ct);

    /// <summary>
    /// Disposes and forgets the data sources of this process on the database of <paramref name="connectionString"/> (same host,
    /// port and database, whatever the other keywords), closing their idle connections (plan 2.7a, Task 5; Task 3 carry-over m9).
    /// Called before the database is dropped: a database created again under the same name then gets a new data source, whose
    /// types are loaded from it (with its <c>citext</c>), instead of a pool of connections to the dropped one.
    /// </summary>
    public static async Task ForgetDatabaseAsync(string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        foreach (var key in DataSources.Keys)
        {
            var candidate = new NpgsqlConnectionStringBuilder(key);
            if (!string.Equals(candidate.Database, target.Database, StringComparison.Ordinal)
                || !string.Equals(candidate.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                || candidate.Port != target.Port) continue;
            if (DataSources.TryRemove(key, out var dataSource)) await dataSource.DisposeAsync();
        }
    }

    /// <summary>
    /// Closes the idle connections of this process's data sources on the database of <paramref name="connectionString"/> (same host,
    /// port and database), keeping the data sources (plan 2.7b, Task 9): before a restore renames that database away, which an open
    /// session would block.
    /// </summary>
    public static void ClearDatabasePools(string connectionString)
    {
        foreach (var dataSource in DataSourcesOf(connectionString)) dataSource.Clear();
    }

    /// <summary>
    /// After a restore put another database under the name of <paramref name="connectionString"/> (plan 2.7b, Task 9): the idle
    /// connections of this process's data sources on it are closed and their types reloaded from the new database (its
    /// <c>citext</c> has another type OID). Unlike <see cref="ForgetDatabaseAsync"/> the data sources stay: a factory of this
    /// process that holds one keeps working on the restored database.
    /// </summary>
    public static async Task RefreshDatabaseAsync(string connectionString, CancellationToken ct)
    {
        foreach (var dataSource in DataSourcesOf(connectionString))
        {
            dataSource.Clear();
            try
            {
                await dataSource.ReloadTypesAsync(ct);
            }
            catch (NpgsqlException)
            {
                // Not reachable now: the types load when the data source next opens a physical connection.
            }
        }
    }

    private static List<NpgsqlDataSource> DataSourcesOf(string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var result = new List<NpgsqlDataSource>();
        foreach (var (key, dataSource) in DataSources)
        {
            var candidate = new NpgsqlConnectionStringBuilder(key);
            if (string.Equals(candidate.Database, target.Database, StringComparison.Ordinal)
                && string.Equals(candidate.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                && candidate.Port == target.Port) result.Add(dataSource);
        }

        return result;
    }

    /// <summary>The startup option that pins <c>standard_conforming_strings</c> on (final review m2).</summary>
    internal const string StandardConformingStrings = "-c standard_conforming_strings=on";

    /// <summary>
    /// <paramref name="connectionString"/> with the session time zone set to UTC, whatever it said: xrmverse stores UTC
    /// <c>timestamptz</c> values and its date arithmetic and date parts (<c>now()</c>, intervals, <c>date_trunc</c>) must work
    /// in UTC, as SQL Server's <c>datetime2</c> does. Its <c>Options</c> end with <see cref="StandardConformingStrings"/>: the
    /// dialect's text literals (<see cref="PostgresDialect.TextLiteral"/>) double quotes only, which keeps a backslash a
    /// character only while that setting is on; a startup option outranks a database or role default and earlier options.
    /// </summary>
    public static string Normalize(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Timezone = "UTC" };
        builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? StandardConformingStrings : $"{builder.Options} {StandardConformingStrings}";
        return builder.ConnectionString;
    }
}
