using System.Data.Common;
using Microsoft.Data.SqlClient;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Host;

/// <summary>
/// Startup coordination with the database server: waits until the server accepts connections (retrying), creates the
/// organization database when it does not exist (on SQL Server turning its READ_COMMITTED_SNAPSHOT on), and holds an applock of
/// that database (taken in <c>master</c>; on PostgreSQL a session advisory lock in the maintenance database <c>postgres</c>, plan
/// 2.7a, Task 5) so that several instances starting together on one database run migrations and seed one at a time.
/// </summary>
/// <remarks>Issue #70: the lock used to be server-wide, so the organizations of one installation, which start together on one
/// SQL Server, each waited for the others' migrations and seed, beyond the lock timeout when those are slow (a first start).
/// Databases do not share anything the startup writes; one process per database is the instance lock's job.</remarks>
public static class DatabaseBootstrapper
{
    /// <summary>The startup lock's resource for <paramref name="database"/> (names are compared ignoring case, as SQL Server's are).</summary>
    private static string LockResource(string database) => "xrmd-startup:" + database.ToLowerInvariant();

    /// <summary>
    /// Runs <paramref name="work"/> (migrate + seed) once SQL Server is reachable, the database exists and the
    /// startup lock is held. Config: <c>Startup:SqlRetries</c> (default 30), <c>Startup:SqlRetryDelaySeconds</c> (default 2).
    /// </summary>
    public static async Task RunExclusiveAsync(IConfiguration config, ILogger logger, Func<Task> work, CancellationToken ct = default)
    {
        var connectionString = config.GetConnectionString("Org")!;
        var retries = Math.Max(1, config.GetValue("Startup:SqlRetries", 30));
        var delay = TimeSpan.FromSeconds(Math.Max(0, config.GetValue("Startup:SqlRetryDelaySeconds", 2.0)));
        var provider = StorageSettings.ParseProvider(config[StorageSettings.ProviderKey]);

        var database = StorageConnectionStrings.DatabaseName(connectionString);
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("ConnectionStrings:Org must specify a Database (Initial Catalog).");

        // The server-level connection: master on SQL Server, the maintenance database postgres on PostgreSQL (plan 2.7a, Task 5),
        // whose advisory locks are those of that database, shared by every process starting on the server.
        await using var conn = provider == StorageProvider.PostgreSql
            ? await OpenWithRetryAsync(ct => PostgresDatabases.OpenMaintenanceAsync(connectionString, ct), "PostgreSQL", retries, delay, logger, ct)
            : await OpenWithRetryAsync(ct => OpenSqlServerAsync(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString, ct),
                "SQL Server", retries, delay, logger, ct);
        var resource = LockResource(database);

        // Session-scoped applock on the server-level connection: released when the connection closes, even on a crash.
        var session = new DataSession(conn, null);
        try
        {
            await session.Dialect.AcquireAppLockAsync(session, resource, session: true, TimeSpan.FromMilliseconds(120_000), ct);
        }
        catch (AppLockException ex)
        {
            throw new InvalidOperationException($"Could not acquire the startup lock '{resource}' ({ex.Detail}).", ex);
        }

        try
        {
            // Existence check and CREATE (and, on SQL Server, READ_COMMITTED_SNAPSHOT) happen under the lock so concurrent instances
            // cannot both try to create it. PostgreSQL: the database is created as ruling P5 says; its first migration (the host's
            // populate path) runs before any other connection to it sends a string parameter (plan 2.7a, Task 4).
            if (provider == StorageProvider.PostgreSql)
            {
                if (!await PostgresDatabases.ExistsAsync(conn, database, ct)) await PostgresDatabases.CreateDatabaseAsync(conn, database, ct);
            }
            else
            {
                await OrganizationDatabase.EnsureDatabaseAsync((SqlConnection)conn, database, ct);
            }

            await work();
        }
        finally
        {
            // Pooled connections keep their session: release explicitly rather than relying on close.
            // Best effort: a broken connection must not mask the exception from the startup work.
            try
            {
                await session.Dialect.ReleaseAppLockAsync(session, resource, session: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not release the startup lock; it is dropped when the connection closes.");
            }
        }
    }

    private static async Task<DbConnection> OpenSqlServerAsync(string connectionString, CancellationToken ct)
    {
        var conn = new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Opens the server-level connection, retrying while the server is unreachable; a login failure (classified by the dialects,
    /// ruling P9) stops at once.
    /// </summary>
    private static async Task<DbConnection> OpenWithRetryAsync(Func<CancellationToken, Task<DbConnection>> open, string engine, int retries, TimeSpan delay,
        ILogger logger, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                return await open(ct);
            }
            catch (DbException ex) when (SqlDialects.Classify(ex) == DbErrorKind.LoginFailed)
            {
                throw new InvalidOperationException($"{engine} login failed (check the user and password in ConnectionStrings:Org): {ex.Message}", ex);
            }
            catch (DbException ex)
            {
                last = ex;
                logger.LogWarning("{Engine} not reachable yet (attempt {Attempt}/{Retries}): {Message}", engine, attempt, retries, ex.Message);
                if (attempt < retries) await Task.Delay(delay, ct);
            }
        }
        throw new InvalidOperationException($"{engine} not reachable after {retries} attempts: {last?.Message}", last);
    }
}
