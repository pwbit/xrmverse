using System.Data;
using System.Data.Common;
using Xrmd.Abstractions.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Xrmd.Storage.PostgreSql;

/// <summary>What <see cref="PostgresDatabases.ReadOrganizationAsync"/> finds in a database.</summary>
/// <param name="SchemaVersion">The highest <c>meta.schema_version</c>.</param>
/// <param name="OrganizationName">The <c>data.organization</c> name, or null when the database is set up only in part.</param>
public sealed record PostgresOrganizationState(int SchemaVersion, string? OrganizationName);

/// <summary>
/// The server-level SQL of PostgreSQL organization databases (plan 2.7a, Task 5): the maintenance connection (the database
/// <c>postgres</c> on the server of an organization's connection string, PostgreSQL's counterpart of SQL Server's
/// <c>master</c>), <c>CREATE DATABASE</c> as ruling P5 says, the list of databases, a database's xrmverse state, and
/// <c>DROP DATABASE</c>. PostgreSQL has no cross-database query: what is read inside a database is read on a connection to it.
/// Every parameter is pinned to <c>text</c>: the maintenance database has no <c>citext</c> (string parameters go as
/// <c>citext</c> otherwise, <see cref="PostgresDialect.ConfigureParameter"/>). Database names are quoted
/// (<see cref="PostgresDialect.Quote"/>) and checked against PostgreSQL's 63-byte limit.
/// </summary>
public static class PostgresDatabases
{
    /// <summary>The maintenance database every PostgreSQL server has (the provisioning's <c>master</c>).</summary>
    public const string MaintenanceDatabase = "postgres";

    /// <summary>SQLSTATE <c>42P04</c> (<c>duplicate_database</c>).</summary>
    private const string DuplicateDatabase = "42P04";

    /// <summary>
    /// <c>CREATE DATABASE "&lt;db&gt;" TEMPLATE template0 ENCODING 'UTF8' LOCALE_PROVIDER icu ICU_LOCALE 'und'</c> (plan 2.7a,
    /// ruling P5): UTF-8 with the ICU root locale, so ordering of letters and digits is SQL Server's; case is ignored by the
    /// <c>citext</c> columns, not by the collation.
    /// </summary>
    public static string CreateDatabaseSql(string database)
    {
        EnsureName(database);
        return $"CREATE DATABASE {PostgresDialect.Instance.Quote(database)} TEMPLATE template0 ENCODING 'UTF8' LOCALE_PROVIDER icu ICU_LOCALE 'und'";
    }

    /// <summary>The database <paramref name="connectionString"/> names (empty when none).</summary>
    public static string DatabaseOf(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).Database ?? "";

    /// <summary><paramref name="connectionString"/> with its database replaced by <paramref name="database"/>.</summary>
    public static string ConnectionStringOf(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    /// <summary>The connection string of the maintenance database on the server of <paramref name="connectionString"/>.</summary>
    public static string MaintenanceConnectionString(string connectionString) => ConnectionStringOf(connectionString, MaintenanceDatabase);

    /// <summary>
    /// Refuses a database name PostgreSQL would cut (over 63 bytes in UTF-8; the organization name rule allows 100 characters).
    /// </summary>
    /// <exception cref="ArgumentException">The name is too long.</exception>
    public static void EnsureName(string database)
    {
        ArgumentException.ThrowIfNullOrEmpty(database);
        if (NameRefusal(database) is { } refusal) throw new ArgumentException(refusal, nameof(database));
    }

    /// <summary>Why PostgreSQL cannot store <paramref name="database"/> as a database name, or null when it can.</summary>
    public static string? NameRefusal(string database) =>
        System.Text.Encoding.UTF8.GetByteCount(database) > PostgresDialect.MaxIdentifierBytes
            ? $"The database name '{database}' is longer than the {PostgresDialect.MaxIdentifierBytes} bytes PostgreSQL allows."
            : null;

    /// <summary>An open connection to the maintenance database of the server of <paramref name="connectionString"/> (pooled, UTC).</summary>
    public static Task<DbConnection> OpenMaintenanceAsync(string connectionString, CancellationToken ct) =>
        OpenAsync(MaintenanceConnectionString(connectionString), pooling: true, ct);

    /// <summary>
    /// An open connection on <paramref name="connectionString"/> outside every pool (<c>Pooling=false</c>): closing it ends its
    /// session, which releases the session's advisory locks (the instance lock), and it caches no type of a database that is
    /// not migrated yet.
    /// </summary>
    public static Task<DbConnection> OpenUnpooledAsync(string connectionString, CancellationToken ct) =>
        OpenAsync(connectionString, pooling: false, ct);

    private static async Task<DbConnection> OpenAsync(string connectionString, bool pooling, CancellationToken ct)
    {
        PostgresStorage.Register();
        var builder = new NpgsqlConnectionStringBuilder(NpgsqlConnectionFactory.Normalize(connectionString)) { Pooling = pooling };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// True when the server has a database named exactly <paramref name="database"/>: PostgreSQL stores the quoted name as written
    /// and compares names with their case (plan 2.7a, Task 5 fix round 1), so <c>zenith</c> and <c>ZENITH</c> are two databases.
    /// </summary>
    /// <param name="maintenance">An open connection to the server (<see cref="OpenMaintenanceAsync"/>).</param>
    public static async Task<bool> ExistsAsync(DbConnection maintenance, string database, CancellationToken ct) =>
        await FindAsync(maintenance, database, ct) is not null;

    /// <summary>
    /// The database named exactly <paramref name="database"/> (its case counting, as PostgreSQL compares names); null when there
    /// is none.
    /// </summary>
    /// <param name="maintenance">An open connection to the server (<see cref="OpenMaintenanceAsync"/>).</param>
    public static async Task<string?> FindAsync(DbConnection maintenance, string database, CancellationToken ct)
    {
        await using var cmd = Command(maintenance, "SELECT datname FROM pg_database WHERE datname = @db", ("@db", database));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// <see cref="CreateDatabaseSql"/> with no existence guard: false when the database exists already (SQLSTATE 42P04), so a
    /// caller that gets true created the database itself (and may drop it on failure).
    /// </summary>
    public static async Task<bool> CreateDatabaseAsync(DbConnection maintenance, string database, CancellationToken ct)
    {
        await using var cmd = Command(maintenance, CreateDatabaseSql(database));
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == DuplicateDatabase)
        {
            return false;
        }
    }

    /// <summary><see cref="CreateDatabaseAsync(DbConnection, string, CancellationToken)"/> on the server of <paramref name="connectionString"/>.</summary>
    public static async Task<bool> CreateDatabaseAsync(string connectionString, string database, CancellationToken ct)
    {
        await using var maintenance = await OpenMaintenanceAsync(connectionString, ct);
        return await CreateDatabaseAsync(maintenance, database, ct);
    }

    /// <summary>
    /// The databases of the server that accept connections, but the maintenance database and the templates, by name.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection maintenance, CancellationToken ct)
    {
        await using var cmd = Command(maintenance,
            "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate AND datname <> @maintenance " +
            $"AND datname NOT LIKE '{LikePrefix(StagingPrefix)}%' AND datname NOT LIKE '{LikePrefix(ReplacedPrefix)}%' ORDER BY datname",
            ("@maintenance", MaintenanceDatabase));
        var names = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) names.Add(rd.GetString(0));
        return names;
    }

    /// <summary>
    /// The xrmverse state of <paramref name="database"/> on the server of <paramref name="connectionString"/>, read on an unpooled
    /// connection to it; null when it is not an xrmverse database (no <c>meta.schema_version</c>) or this login cannot open it.
    /// </summary>
    public static async Task<PostgresOrganizationState?> ReadOrganizationAsync(string connectionString, string database, CancellationToken ct)
    {
        DbConnection conn;
        try
        {
            conn = await OpenUnpooledAsync(ConnectionStringOf(connectionString, database), ct);
        }
        catch (NpgsqlException)
        {
            return null; // not accessible to this login
        }

        await using (conn)
        try
        {
            bool hasVersion, hasOrganization;
            await using (var exists = Command(conn, "SELECT to_regclass('meta.schema_version') IS NOT NULL, to_regclass('data.organization') IS NOT NULL"))
            await using (var rd = await exists.ExecuteReaderAsync(ct))
            {
                await rd.ReadAsync(ct);
                hasVersion = rd.GetBoolean(0);
                hasOrganization = rd.GetBoolean(1);
            }

            // Plan 2.7a, Task 6: the reader is closed before the next command (one command at a time on a connection).
            if (!hasVersion) return null;
            if (!hasOrganization) return new PostgresOrganizationState(await VersionAsync(conn, ct), null);

            var version = await VersionAsync(conn, ct);
            await using var name = Command(conn, "SELECT name::text FROM data.organization LIMIT 1");
            return new PostgresOrganizationState(version, await name.ExecuteScalarAsync(ct) as string);
        }
        catch (NpgsqlException ex) when (IsGone(ex))
        {
            // Plan 2.7a, Task 6: dropped (WITH (FORCE)) while it was read, as a list running beside another drop sees it.
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> says the database went away under the connection: <c>3D000</c> (<c>invalid_catalog_name</c>),
    /// <c>57P01</c> (<c>admin_shutdown</c>, the session ended by a forced drop), or the connection broke.
    /// </summary>
    private static bool IsGone(NpgsqlException ex) => ex is PostgresException pg ? pg.SqlState is "3D000" or "57P01" : ex.IsTransient || ex.InnerException is IOException;

    private static async Task<int> VersionAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = Command(conn, "SELECT COALESCE(MAX(version), 0) FROM meta.schema_version");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Drops <paramref name="database"/> if it exists, ending its sessions (<c>WITH (FORCE)</c>, as SQL Server's <c>SINGLE_USER WITH
    /// ROLLBACK IMMEDIATE</c> does). The connection pools of this process on that database go first (Task 3 carry-over m9): their
    /// data sources are disposed and forgotten, so a database created again under the same name gets fresh ones (with its types).
    /// </summary>
    public static async Task DropDatabaseAsync(DbConnection maintenance, string connectionString, string database, CancellationToken ct)
    {
        EnsureName(database);
        await NpgsqlConnectionFactory.ForgetDatabaseAsync(ConnectionStringOf(connectionString, database));
        await using var cmd = Command(maintenance, $"DROP DATABASE IF EXISTS {PostgresDialect.Instance.Quote(database)} WITH (FORCE)");
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary><see cref="DropDatabaseAsync(DbConnection, string, string, CancellationToken)"/> on the server of <paramref name="connectionString"/>.</summary>
    public static async Task DropDatabaseAsync(string connectionString, string database, CancellationToken ct)
    {
        await using var maintenance = await OpenMaintenanceAsync(connectionString, ct);
        await DropDatabaseAsync(maintenance, connectionString, database, ct);
    }

    /// <summary>The prefix of the databases a restore loads before they take an organization's name (<see cref="StagingName"/>).</summary>
    public const string StagingPrefix = "xrmd_restore_";

    /// <summary>The prefix of the databases a restore has replaced and drops (<see cref="SwapInAsync"/>).</summary>
    public const string ReplacedPrefix = "xrmd_replaced_";

    /// <summary>
    /// Why <paramref name="database"/> cannot be an organization's name because a restore uses names like it (Task 9 fix round 1:
    /// such databases are left out of the organization list and swept), or null.
    /// </summary>
    public static string? ReservedNameRefusal(string database) =>
        database.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase) || database.StartsWith(ReplacedPrefix, StringComparison.OrdinalIgnoreCase)
            ? $"The database name '{database}' is reserved: names starting with '{StagingPrefix}' or '{ReplacedPrefix}' are the restore's own."
            : null;

    /// <summary>A LIKE pattern matching <paramref name="prefix"/> literally (its underscores escaped).</summary>
    private static string LikePrefix(string prefix) => prefix.Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>
    /// A fresh name for the database a restore is loaded into before it takes the organization's name (plan 2.7b, ruling Q7):
    /// <c>xrmd_restore_&lt;32 hex digits&gt;</c>, 45 bytes.
    /// </summary>
    public static string StagingName() => StagingPrefix + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Creates a staging database (<see cref="StagingName"/>) as an organization's is created, claimed by the session of
    /// <paramref name="maintenance"/> (<see cref="ClaimAsync"/>) before it exists, so <see cref="SweepLeftoversAsync"/> never drops it
    /// while this restore runs.
    /// </summary>
    /// <exception cref="InvalidOperationException">A database of that name exists already.</exception>
    public static async Task<string> CreateStagingAsync(DbConnection maintenance, CancellationToken ct)
    {
        var staging = StagingName();
        await ClaimAsync(maintenance, staging, ct);
        try
        {
            if (!await CreateDatabaseAsync(maintenance, staging, ct))
                throw new InvalidOperationException($"The staging database '{staging}' exists already.");
            return staging;
        }
        catch
        {
            await ReleaseClaimAsync(maintenance, staging);
            throw;
        }
    }

    /// <summary>
    /// Takes the session advisory lock of the restore's own database <paramref name="database"/> (a staging or replaced one) on the
    /// session of <paramref name="maintenance"/>: held until <see cref="ReleaseClaimAsync"/> or the session ends, so a restore that
    /// stopped half way (a crashed process) leaves a database the sweep may drop, and a running one does not.
    /// </summary>
    public static async Task ClaimAsync(DbConnection maintenance, string database, CancellationToken ct)
    {
        await using var cmd = Command(maintenance, $"SELECT pg_advisory_lock({ClaimKey})", ("@db", database));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Releases <see cref="ClaimAsync"/> of <paramref name="database"/> (best effort: the session's end releases it too).</summary>
    public static async Task ReleaseClaimAsync(DbConnection maintenance, string database)
    {
        try
        {
            await using var cmd = Command(maintenance, $"SELECT pg_advisory_unlock({ClaimKey})", ("@db", database));
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // the session is gone (or the connection is closed or broken), and its locks with it
        }
    }

    /// <summary>The advisory lock key of a restore's database: its own hash seed, apart from the instance and application locks.</summary>
    private const string ClaimKey = "hashtextextended('xrmd-restore:' || @db, 2701)";

    /// <summary>
    /// Drops the staging and replaced databases (<see cref="StagingPrefix"/>, <see cref="ReplacedPrefix"/>) that a restore left behind
    /// (Task 9 fix round 1): those with no session on them and whose claim (<see cref="ClaimAsync"/>) this session can take, so a
    /// restore running in any process of the server keeps its own. Returns the names dropped; a database that cannot be dropped
    /// is skipped (the next sweep tries again).
    /// </summary>
    public static async Task<IReadOnlyList<string>> SweepLeftoversAsync(DbConnection maintenance, string connectionString, CancellationToken ct)
    {
        var candidates = new List<string>();
        await using (var cmd = Command(maintenance,
                         "SELECT d.datname FROM pg_database d " +
                         $"WHERE (d.datname LIKE '{LikePrefix(StagingPrefix)}%' OR d.datname LIKE '{LikePrefix(ReplacedPrefix)}%') " +
                         "AND NOT EXISTS (SELECT 1 FROM pg_stat_activity a WHERE a.datname = d.datname) ORDER BY d.datname"))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct)) candidates.Add(rd.GetString(0));
        }

        var dropped = new List<string>();
        foreach (var database in candidates)
        {
            await using (var claim = Command(maintenance, $"SELECT pg_try_advisory_lock({ClaimKey})", ("@db", database)))
            {
                if (await claim.ExecuteScalarAsync(ct) is not true) continue; // a restore is running with it
            }

            try
            {
                await DropDatabaseAsync(maintenance, connectionString, database, ct);
                dropped.Add(database);
            }
            catch (DbException)
            {
                // left for the next sweep
            }
            finally
            {
                await ReleaseClaimAsync(maintenance, database);
            }
        }

        return dropped;
    }

    /// <summary>
    /// Gives the restored database <paramref name="staging"/> the name <paramref name="target"/> (plan 2.7b, ruling Q7): the
    /// organization appears whole or not at all, never half restored, for a process that starts on it meanwhile. With
    /// <paramref name="replace"/> an existing <paramref name="target"/> is replaced in the same transaction: it is closed to new
    /// connections first (<c>ALLOW_CONNECTIONS false</c>, so a process starting on it is refused), then
    /// <paramref name="afterLockOut"/> runs (the caller ends its own sessions there, the instance lock's included), its other
    /// sessions are ended, and both renames commit together; the replaced database is then dropped. False, with nothing changed,
    /// when <paramref name="target"/> exists and <paramref name="replace"/> is false (a database that appeared since the caller's
    /// check). The connection pools of this process on the staging name are forgotten; those on <paramref name="target"/> are cleared
    /// and reload their types from the restored database (<see cref="NpgsqlConnectionFactory.RefreshDatabaseAsync"/>).
    /// </summary>
    /// <param name="maintenance">An open connection to the server (<see cref="OpenMaintenanceAsync"/>).</param>
    /// <param name="connectionString">A connection string of the server (any database).</param>
    public static async Task<bool> SwapInAsync(DbConnection maintenance, string connectionString, string staging, string target, bool replace,
        Func<CancellationToken, Task>? afterLockOut, CancellationToken ct)
    {
        EnsureName(staging);
        EnsureName(target);
        var dialect = PostgresDialect.Instance;
        // Nobody but this process knows the staging name; its pools go, and a list that opened it meanwhile is shut out.
        await NpgsqlConnectionFactory.ForgetDatabaseAsync(ConnectionStringOf(connectionString, staging));
        await ExecAsync(maintenance, $"ALTER DATABASE {dialect.Quote(staging)} ALLOW_CONNECTIONS false", ct);
        await TerminateSessionsAsync(maintenance, staging, ct);

        string? replaced = null;
        if (replace && await ExistsAsync(maintenance, target, ct))
        {
            replaced = ReplacedPrefix + Guid.NewGuid().ToString("N");
            await ClaimAsync(maintenance, replaced, ct); // released after its drop, below
            await ExecAsync(maintenance, $"ALTER DATABASE {dialect.Quote(target)} ALLOW_CONNECTIONS false", ct);
        }

        try
        {
            if (replaced is not null)
            {
                if (afterLockOut is { } hook) await hook(ct);
                NpgsqlConnectionFactory.ClearDatabasePools(ConnectionStringOf(connectionString, target));
                await TerminateSessionsAsync(maintenance, target, ct);
            }

            await using var tx = await maintenance.BeginTransactionAsync(ct);
            await using (var rename = Command(maintenance,
                             (replaced is null ? "" : $"ALTER DATABASE {dialect.Quote(target)} RENAME TO {dialect.Quote(replaced)}; ") +
                             $"ALTER DATABASE {dialect.Quote(staging)} RENAME TO {dialect.Quote(target)}; " +
                             $"ALTER DATABASE {dialect.Quote(target)} ALLOW_CONNECTIONS true"))
            {
                rename.Transaction = tx;
                try
                {
                    await rename.ExecuteNonQueryAsync(ct);
                }
                catch (PostgresException ex) when (ex.SqlState == DuplicateDatabase)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    return false;
                }
            }

            await tx.CommitAsync(ct);
        }
        catch when (replaced is not null)
        {
            // The target was not replaced: it accepts connections again.
            try { await ExecAsync(maintenance, $"ALTER DATABASE {dialect.Quote(target)} ALLOW_CONNECTIONS true", CancellationToken.None); }
            catch (DbException) { /* the original failure is the one to report */ }
            await ReleaseClaimAsync(maintenance, replaced);
            throw;
        }

        await NpgsqlConnectionFactory.RefreshDatabaseAsync(ConnectionStringOf(connectionString, target), CancellationToken.None);
        if (replaced is not null)
        {
            try
            {
                await DropDatabaseAsync(maintenance, connectionString, replaced, CancellationToken.None);
            }
            finally
            {
                await ReleaseClaimAsync(maintenance, replaced); // a replaced database that could not be dropped is swept later
            }
        }

        return true;
    }

    /// <summary>Ends every session on <paramref name="database"/> but the caller's (<c>pg_terminate_backend</c>).</summary>
    public static async Task TerminateSessionsAsync(DbConnection maintenance, string database, CancellationToken ct)
    {
        await using var cmd = Command(maintenance,
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid()", ("@db", database));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = Command(connection, sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>True when <paramref name="ex"/> says the database exists already (SQLSTATE 42P04).</summary>
    public static bool IsDuplicateDatabase(DbException ex) => ex is PostgresException { SqlState: DuplicateDatabase };

    private static DbCommand Command(DbConnection connection, string sql, params (string Name, string Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            ((NpgsqlParameter)cmd.AddParameter(name, DbType.String, value)).NpgsqlDbType = NpgsqlDbType.Text;
        return cmd;
    }
}
