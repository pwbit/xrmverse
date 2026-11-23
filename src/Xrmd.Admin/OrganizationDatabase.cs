using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Audit;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Admin;

/// <summary>Defers resolving the real handler until it is first needed (breaks the store/handler construction cycle).</summary>
public sealed class LazyEntityCreatedHandler(Func<IEntityCreatedHandler> resolve) : IEntityCreatedHandler
{
    public Task OnEntityCreatedAsync(EntityMetadata entity, DataSession session, CancellationToken ct) =>
        resolve().OnEntityCreatedAsync(entity, session, ct);
}

/// <summary>Defers resolving the real handler until it is first needed (breaks the store/handler construction cycle).</summary>
public sealed class LazyEntityDeletedHandler(Func<IEntityDeletedHandler> resolve) : IEntityDeletedHandler
{
    public Task OnEntityDeletingAsync(EntityMetadata entity, DataSession session, CancellationToken ct) =>
        resolve().OnEntityDeletingAsync(entity, session, ct);
}

/// <summary>An <see cref="IAttributeDeletedHandler"/> resolved on first use (see <see cref="LazyEntityDeletedHandler"/>).</summary>
public sealed class LazyAttributeDeletedHandler(Func<IAttributeDeletedHandler> resolve) : IAttributeDeletedHandler
{
    public Task OnAttributeDeletingAsync(EntityMetadata entity, AttributeMetadata attribute, DataSession session, CancellationToken ct) =>
        resolve().OnAttributeDeletingAsync(entity, attribute, session, ct);
}

/// <summary>An <see cref="IAttributeSecurityChangedHandler"/> resolved on first use (see <see cref="LazyEntityDeletedHandler"/>).</summary>
public sealed class LazyAttributeSecurityChangedHandler(Func<IAttributeSecurityChangedHandler> resolve) : IAttributeSecurityChangedHandler
{
    public void OnAttributeSecurityChanged(string entity, string attribute) => resolve().OnAttributeSecurityChanged(entity, attribute);
}

/// <summary>What <see cref="OrganizationDatabase.PopulateAsync"/> created or found.</summary>
public sealed record PopulatedOrganization(Guid RootBusinessUnitId, Guid AdminUserId);

/// <summary>
/// The reusable part of setting up an organization database: create it (with READ_COMMITTED_SNAPSHOT), then migrate,
/// seed and give it its first System Administrator. Used by the host's startup (on the services of the served database)
/// and by <c>POST /admin/orgs</c> (on services of their own, bound to the new database).
/// </summary>
public static class OrganizationDatabase
{
    /// <summary>The names an organization database may have; also what keeps the name safe to quote into T-SQL.</summary>
    public static readonly Regex NamePattern = OrganizationName.Pattern;

    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    /// <summary>Creates <paramref name="database"/> when missing and turns READ_COMMITTED_SNAPSHOT on (only when off: WITH ROLLBACK IMMEDIATE ends the other sessions).</summary>
    /// <param name="master">An open connection to the server (any database): the statements name the database.</param>
    /// <remarks>For the startup path, which runs under its applock. A caller that must know it created the database uses <see cref="CreateDatabaseAsync"/>.</remarks>
    public static async Task EnsureDatabaseAsync(SqlConnection master, string database, CancellationToken ct)
    {
        await using (var create = new SqlCommand(
            "IF DB_ID(@db) IS NULL BEGIN DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@db); EXEC(@sql); END", master))
        {
            create.Parameters.AddWithValue("@db", database);
            await create.ExecuteNonQueryAsync(ct);
        }

        await EnableReadCommittedSnapshotAsync(master, database, ct);
    }

    /// <summary>
    /// <c>CREATE DATABASE</c> with no existence guard: it fails with SQL error 1801 when the database exists, so a caller
    /// that gets past it created the database itself (and may drop it on failure).
    /// </summary>
    public static async Task CreateDatabaseAsync(SqlConnection master, string database, CancellationToken ct)
    {
        await using var create = new SqlCommand("DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@db); EXEC(@sql);", master);
        create.Parameters.AddWithValue("@db", database);
        await create.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Turns READ_COMMITTED_SNAPSHOT on (only when off: WITH ROLLBACK IMMEDIATE ends the other sessions).</summary>
    public static async Task EnableReadCommittedSnapshotAsync(SqlConnection master, string database, CancellationToken ct)
    {
        // Read committed snapshot: readers on their own connections (the step registry, trace and image reads) never
        // wait for the row locks of a writing pipeline transaction (that wait could close a cycle with the
        // registry's lock).
        await using var rcsi = new SqlCommand(
            "IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @db AND is_read_committed_snapshot_on = 0) " +
            "BEGIN DECLARE @sql nvarchar(400) = N'ALTER DATABASE ' + QUOTENAME(@db) + " +
            "N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE'; EXEC(@sql); END",
            master);
        rcsi.Parameters.AddWithValue("@db", database);
        await rcsi.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The applock that an xrmd process holds in the organization database it serves, for its whole lifetime (the host's
    /// <c>InstanceLock</c>): one process per organization database.
    /// </summary>
    public const string InstanceLockResource = "xrmd-instance";

    /// <summary>
    /// True when another session holds <see cref="InstanceLockResource"/> in the database of <paramref name="connectionString"/>, i.e.
    /// another xrmd process serves it: the lock is tried on a session of its own (<see cref="TryTakeInstanceLockAsync"/>) and released
    /// at once when it was free (plan 2.7a, Task 5: one engine-neutral test instead of SQL Server's <c>APPLOCK_TEST</c>). The
    /// database must exist.
    /// </summary>
    public static async Task<bool> IsInstanceLockHeldByAnotherAsync(string connectionString, StorageProvider provider, CancellationToken ct)
    {
        var connection = await TryTakeInstanceLockAsync(connectionString, provider, ct);
        if (connection is null) return true;
        await connection.DisposeAsync();
        return false;
    }

    /// <summary>
    /// Takes <see cref="InstanceLockResource"/> (Exclusive, Session owner, no wait) in the database of <paramref name="connectionString"/>
    /// on a connection of its own, opened outside the pool (<c>Pooling=false</c>) so that closing it really ends the session and
    /// releases the lock: <c>sp_getapplock</c> on SQL Server, a session advisory lock on PostgreSQL (plan 2.7a, Task 5), through
    /// <see cref="ISqlDialect.TryAcquireAppLockAsync"/>.
    /// </summary>
    /// <param name="provider">The engine of <paramref name="connectionString"/> (<c>Storage:Provider</c>).</param>
    /// <returns>The open connection holding the lock, or null when another session holds it.</returns>
    public static async Task<DbConnection?> TryTakeInstanceLockAsync(string connectionString, StorageProvider provider, CancellationToken ct)
    {
        var connection = await OpenUnpooledAsync(connectionString, provider, ct);
        try
        {
            var session = new DataSession(connection, null);
            bool taken;
            try
            {
                taken = await session.Dialect.TryAcquireAppLockAsync(session, InstanceLockResource, session: true, ct);
            }
            catch (AppLockException ex)
            {
                throw new InvalidOperationException($"Could not acquire the instance lock '{InstanceLockResource}' ({ex.Detail}).", ex);
            }
            if (taken) return connection;
            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            try { await connection.DisposeAsync(); } catch (Exception) { /* a broken session is already gone */ }
            throw;
        }
    }

    /// <summary>A connection on <paramref name="connectionString"/> outside every pool, of the engine of <paramref name="provider"/>.</summary>
    private static async Task<DbConnection> OpenUnpooledAsync(string connectionString, StorageProvider provider, CancellationToken ct)
    {
        if (provider == StorageProvider.PostgreSql) return await PostgresDatabases.OpenUnpooledAsync(connectionString, ct);
        var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
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
    /// Migrates the database the services are bound to, loads its metadata, seeds the system entities, the message and
    /// privilege catalogs and the standard roles, and makes the administrator a System Administrator. Idempotent.
    /// </summary>
    public static async Task<PopulatedOrganization> PopulateAsync(IServiceProvider services, SeedOptions seed, CancellationToken ct = default)
    {
        await services.GetRequiredService<SchemaMigrator>().MigrateAsync(ct);
        await services.GetRequiredService<IMetadataStore>().LoadAsync();
        await services.GetRequiredService<SystemSeed>().EnsureAsync(seed, ct);
        await AuditSettingsUpgrade.EnsureAsync(services.GetRequiredService<IMetadataStore>(), services.GetRequiredService<IDbConnectionFactory>(), ct,
            services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger(nameof(AuditSettingsUpgrade)));
        await AccessTeamSettingsUpgrade.EnsureAsync(services.GetRequiredService<IMetadataStore>(), ct,
            services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger(nameof(AccessTeamSettingsUpgrade)));
        await ChangeTrackingSettingsUpgrade.EnsureAsync(services.GetRequiredService<IMetadataStore>(), ct,
            services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger(nameof(ChangeTrackingSettingsUpgrade)));
        await DuplicateDetectionSettingsUpgrade.EnsureAsync(services.GetRequiredService<IMetadataStore>(), ct,
            services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger(nameof(DuplicateDetectionSettingsUpgrade)));
        await CurrencyColumnsUpgrade.EnsureAsync(services.GetRequiredService<IMetadataStore>(), services.GetRequiredService<IDbConnectionFactory>(), ct,
            services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger(nameof(CurrencyColumnsUpgrade)));
        await ActivitySettingsUpgrade.EnsureAsync(services.GetRequiredService<IMetadataStore>(), services.GetRequiredService<IDbConnectionFactory>(), ct,
            services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger(nameof(ActivitySettingsUpgrade)));
        await QueueSettingsUpgrade.EnsureAsync(services.GetRequiredService<IMetadataStore>(), services.GetRequiredService<IDbConnectionFactory>(), ct,
            services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger(nameof(QueueSettingsUpgrade)));
        await services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(ct);
        await services.GetRequiredService<PrivilegeCatalog>().EnsureAsync(ct);
        var security = services.GetRequiredService<SecurityBootstrap>();
        await security.EnsureAsync(ct);
        var adminUserId = await security.EnsureSystemAdministratorAsync(seed.AdminObjectId, seed.AdminName, ct);
        // After the administrator is inserted, so that an administrator added at a later start has its mailbox in the same start.
        await MailboxSettingsUpgrade.EnsureAsync(services.GetRequiredService<IMetadataStore>(), services.GetRequiredService<IDbConnectionFactory>(), ct,
            services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger(nameof(MailboxSettingsUpgrade)));

        var factory = services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.Command(factory.Dialect.Limit(
            "SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL ORDER BY createdon, businessunitid", "1"));
        var root = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        return new PopulatedOrganization(root, adminUserId);
    }

    /// <summary>
    /// A container with the services <see cref="PopulateAsync"/> needs, bound to <paramref name="connectionString"/> and sharing
    /// nothing with the served organization (own metadata store, own security cache). Dispose it when done.
    /// </summary>
    /// <param name="provider">The engine of the database (<c>Storage:Provider</c>; plan 2.7a, ruling P3): its SQL dialect is registered.</param>
    public static ServiceProvider BuildServices(string connectionString, StorageProvider provider = StorageProvider.SqlServer)
    {
        var services = new ServiceCollection();
        // Plan 2.7a, Task 3: the connection factory of the provider (an Npgsql connection string for PostgreSql).
        IDbConnectionFactory factory = provider == StorageProvider.PostgreSql
            ? new NpgsqlConnectionFactory(connectionString)
            : new SqlConnectionFactory(connectionString);
        services.AddSingleton(factory);
        // One source for the dialect (fix round 1, m1): the one of the factory's connections, refused when it is not the configured one.
        services.AddSingleton(SqlDialects.Of(factory, provider));
        // Calculated columns (plan 2.12a): the store takes the translator through its optional IComputedColumns parameter.
        services.AddSingleton<Xrmd.Metadata.Computed.IComputedColumns, Xrmd.Workflows.Calculated.CalculatedColumns>();
        // Rollup columns (plan 2.12b): the store takes the translator through its optional IRollupColumns parameter.
        services.AddSingleton<Xrmd.Metadata.Computed.IRollupColumns, Xrmd.Workflows.Rollup.RollupColumnsService>();
        services.AddSingleton<IMetadataStore, MetadataStore>();
        services.AddSingleton<SdkMessageCatalog>();
        services.AddSingleton<IEntityCreatedHandler>(sp => new LazyEntityCreatedHandler(() => sp.GetRequiredService<SdkMessageCatalog>()));
        services.AddSingleton<PrivilegeCatalog>();
        services.AddSingleton<IEntityCreatedHandler>(sp => new LazyEntityCreatedHandler(() => sp.GetRequiredService<PrivilegeCatalog>()));
        services.AddSingleton<IEntityDeletedHandler>(sp => new LazyEntityDeletedHandler(() => sp.GetRequiredService<SdkMessageCatalog>()));
        services.AddSingleton<IEntityDeletedHandler>(sp => new LazyEntityDeletedHandler(() => sp.GetRequiredService<PrivilegeCatalog>()));
        services.AddSingleton<IEntityDeletedHandler>(sp => sp.GetRequiredService<SecurityCache>());
        services.AddSingleton<SecurityCache>();
        services.AddSingleton<SecurityBootstrap>();
        services.AddSingleton<SystemSeed>();
        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<AuditSettings>();
        services.AddSingleton<AuditWriter>();
        return services.BuildServiceProvider();
    }
}
