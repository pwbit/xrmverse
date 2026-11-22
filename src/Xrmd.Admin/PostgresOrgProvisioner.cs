using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Admin;

/// <summary>
/// The lifecycle of the organization databases on the PostgreSQL server of <c>ConnectionStrings:Org</c> (plan 2.7a, Task 5):
/// create (ruling P5: <see cref="PostgresDatabases.CreateDatabaseSql"/>, then the populate path of SQL Server, which migrates
/// first), list, test and drop. The server-level SQL is <see cref="PostgresDatabases"/>'s (the maintenance database <c>postgres</c>
/// plays SQL Server's <c>master</c>). Backup and restore (plan 2.7b, ruling Q7) run <c>pg_dump</c> and <c>pg_restore</c>
/// (<see cref="PgTools"/>, in the xrmd container: ruling Q9) into and from xrmd's own backup directory (ruling Q8), with the
/// <c>.files.tar</c> of <see cref="OrgBackupFiles"/> beside each <c>.dump</c>, as on SQL Server.
/// </summary>
/// <remarks>
/// Plan 2.7a placed this class in <c>Xrmd.Storage.PostgreSql</c> (ruling P3); it lives here because it runs the populate path of
/// <see cref="OrganizationDatabase"/> and the Admin API's faults, and <c>Xrmd.Admin</c> already references the PostgreSQL
/// project (the reverse reference would be a cycle). Its Npgsql code is in that project.
/// </remarks>
public sealed class PostgresOrgProvisioner(IConfiguration configuration, ILogger<PostgresOrgProvisioner> logger, IFileStore fileStore,
    FileColumnService fileColumns) : IOrgProvisioner, IOrgBackupSeams
{
    /// <summary>Test seam: runs between <see cref="CreateAsync"/>'s existence check and its <c>CREATE DATABASE</c>.</summary>
    internal Func<CancellationToken, Task>? BeforeCreateDatabase { get; set; }

    /// <summary>Test seam: runs right after <see cref="CreateAsync"/> created the database.</summary>
    internal Func<CancellationToken, Task>? AfterCreateDatabase { get; set; }

    /// <summary>Test seam: runs in <see cref="DropAsync"/> and <see cref="RestoreAsync"/> right after the instance lock check (ruling M6), while its lock is held.</summary>
    internal Func<CancellationToken, Task>? AfterInstanceCheck { get; set; }

    /// <summary>Test seam: runs in <see cref="BackupAsync"/> after the .dump is written and before the files archive is.</summary>
    internal Func<CancellationToken, Task>? BeforeArchive { get; set; }

    /// <summary>Test seam: runs in <see cref="RestoreAsync"/> after the archive was validated, before the blobs are restored from it.</summary>
    internal Func<CancellationToken, Task>? BeforeFilesRestore { get; set; }

    /// <summary>Test seam: runs in <see cref="RestoreAsync"/> after <c>pg_restore</c> loaded the staging database, before it takes the target's name.</summary>
    internal Func<CancellationToken, Task>? BeforeSwap { get; set; }

    /// <summary>Test seam: runs in <see cref="BackupAsync"/> after <c>pg_dump</c> wrote its temporary file and before it is renamed.</summary>
    internal Func<CancellationToken, Task>? AfterDump { get; set; }

    /// <summary>Test seam: the clock of the backup file names.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    Func<CancellationToken, Task>? IOrgBackupSeams.AfterInstanceCheck { get => AfterInstanceCheck; set => AfterInstanceCheck = value; }
    Func<CancellationToken, Task>? IOrgBackupSeams.BeforeArchive { get => BeforeArchive; set => BeforeArchive = value; }
    Func<CancellationToken, Task>? IOrgBackupSeams.BeforeFilesRestore { get => BeforeFilesRestore; set => BeforeFilesRestore = value; }

    public StorageProvider Provider => StorageProvider.PostgreSql;

    /// <summary>
    /// The backup directory of PostgreSQL organizations (ruling Q8): in the xrmd container, where <c>pg_dump</c> runs; the compose
    /// files mount the <c>backups</c> volume there.
    /// </summary>
    public const string DefaultBackupDirectory = "/var/lib/xrmd/backups";

    /// <summary>The extension of the backup files of <c>pg_dump --format=custom</c>.</summary>
    public const string DumpExtension = ".dump";

    /// <summary>Where the <c>.dump</c> files are written and read (<c>Admin:BackupDirectory</c>, default <see cref="DefaultBackupDirectory"/>).</summary>
    public string BackupDirectory => configuration["Admin:BackupDirectory"] is { Length: > 0 } d ? d : DefaultBackupDirectory;

    /// <summary>The directory of the <c>.files.tar</c> archives (<c>Admin:FilesBackupDirectory</c>); the backup directory by default.</summary>
    public string FilesBackupDirectory => configuration["Admin:FilesBackupDirectory"] is { Length: > 0 } d ? d : BackupDirectory;

    /// <summary>The client tools (<c>Storage:PgToolsPath</c>, default <see cref="PgTools.DefaultPath"/>).</summary>
    private PgTools Tools => new(configuration[PgTools.ConfigurationKey] ?? PgTools.DefaultPath);

    private OrgBackupFiles Files => new(fileStore, fileColumns, logger, FilesBackupDirectory);

    private string OrgConnectionString =>
        configuration.GetConnectionString("Org") ?? throw new InvalidOperationException("ConnectionStrings:Org is not configured.");

    private string ServedDatabase => PostgresDatabases.DatabaseOf(OrgConnectionString);

    private string ConnectionStringOf(string database) => PostgresDatabases.ConnectionStringOf(OrgConnectionString, database);

    /// <summary>The served database, compared as PostgreSQL compares names: with their case.</summary>
    private bool IsServed(string database) => string.Equals(database, ServedDatabase, StringComparison.Ordinal);

    /// <summary>The name, valid and storable by PostgreSQL (63 bytes), before any SQL.</summary>
    private static string RequireValidName(string? name)
    {
        var database = OrgProvisioning.RequireValidName(name);
        return (PostgresDatabases.NameRefusal(database) ?? PostgresDatabases.ReservedNameRefusal(database)) is { } refusal
            ? throw XrmdFaultException.InvalidArgument(refusal)
            : database;
    }

    public async Task<CreatedOrganization> CreateAsync(string? name, Guid adminObjectId, string? adminName, CancellationToken ct)
    {
        var database = RequireValidName(name);
        if (adminObjectId == Guid.Empty) throw XrmdFaultException.InvalidArgument("'adminObjectId' must be the administrator's Entra object id (a non-empty GUID).");
        var fullName = string.IsNullOrWhiteSpace(adminName) ? "Administrator" : adminName.Trim();

        await using var maintenance = await PostgresDatabases.OpenMaintenanceAsync(OrgConnectionString, ct);
        if (await PostgresDatabases.ExistsAsync(maintenance, database, ct)) throw OrgProvisioning.Conflict($"The database '{database}' already exists.");
        if (BeforeCreateDatabase is { } before) await before(ct);
        // Unconditional: a database that appeared since the check is a 409, never a database this request would migrate into and
        // then drop on failure.
        if (!await PostgresDatabases.CreateDatabaseAsync(maintenance, database, ct))
            throw OrgProvisioning.Conflict($"The database '{database}' already exists.");

        // From here on the database is this request's own: on failure it is dropped.
        try
        {
            if (AfterCreateDatabase is { } after) await after(ct);
            // Services of their own, bound to the new database. The populate path migrates before anything else opens a
            // connection there: PrepareDatabaseAsync creates citext and reloads the data source's types before any string
            // parameter is sent (plan 2.7a, Task 4).
            await using var services = OrganizationDatabase.BuildServices(ConnectionStringOf(database), StorageProvider.PostgreSql);
            var seed = new SeedOptions(database, SeedOptions.DeriveRootBusinessUnitId(database), adminObjectId, fullName);
            var populated = await OrganizationDatabase.PopulateAsync(services, seed, ct);
            return new CreatedOrganization(database, database, populated.RootBusinessUnitId, populated.AdminUserId);
        }
        catch (Exception ex)
        {
            // Do not leave a half-set-up database behind (it would answer 409 to the retry).
            try
            {
                await PostgresDatabases.DropDatabaseAsync(maintenance, OrgConnectionString, database, CancellationToken.None);
            }
            catch (Exception dropFailure) when (dropFailure is DbException or InvalidOperationException)
            {
                logger.LogError(dropFailure, "Creating organization database {Database} failed ({Reason}) and the half-created database could not be dropped; drop it by hand",
                    database, ex.Message);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<OrganizationInfo>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<string> names;
        await using (var maintenance = await PostgresDatabases.OpenMaintenanceAsync(OrgConnectionString, ct))
            names = await PostgresDatabases.ListDatabasesAsync(maintenance, ct);

        var result = new List<OrganizationInfo>();
        foreach (var database in names)
        {
            if (await PostgresDatabases.ReadOrganizationAsync(OrgConnectionString, database, ct) is not { } state) continue;
            result.Add(new OrganizationInfo(string.IsNullOrEmpty(state.OrganizationName) ? database : state.OrganizationName, database,
                state.SchemaVersion, IsServed(database)));
        }

        return result;
    }

    public async Task<bool> ExistsAsync(string? name, CancellationToken ct)
    {
        var database = RequireValidName(name);
        await using var maintenance = await PostgresDatabases.OpenMaintenanceAsync(OrgConnectionString, ct);
        return await PostgresDatabases.ExistsAsync(maintenance, database, ct);
    }

    /// <summary>
    /// Drops the organization database. As on SQL Server (ruling M6), the instance lock is taken rather than tested and held until
    /// the drop, whose <c>WITH (FORCE)</c> ends that session too: a process starting on the database in between is refused at
    /// startup instead of having its sessions ended under it.
    /// </summary>
    public async Task DropAsync(string? name, CancellationToken ct)
    {
        var requested = RequireValidName(name);
        await using var maintenance = await PostgresDatabases.OpenMaintenanceAsync(OrgConnectionString, ct);
        if (IsServed(requested)) throw OrgProvisioning.Conflict($"The database '{requested}' is the one this server serves; it cannot be deleted.");
        var database = await PostgresDatabases.FindAsync(maintenance, requested, ct)
                       ?? throw OrgProvisioning.NotFound($"The database '{requested}' does not exist.");
        if (await PostgresDatabases.ReadOrganizationAsync(OrgConnectionString, database, ct) is null)
            throw XrmdFaultException.InvalidArgument($"The database '{database}' is not an xrmverse organization database (it has no meta.schema_version table).");

        var hold = await OrganizationDatabase.TryTakeInstanceLockAsync(ConnectionStringOf(database), StorageProvider.PostgreSql, ct)
                   ?? throw OrgProvisioning.Conflict($"The database '{database}' is served by another xrmd process; stop it first.");
        try
        {
            if (AfterInstanceCheck is { } hook) await hook(ct);
            await PostgresDatabases.DropDatabaseAsync(maintenance, OrgConnectionString, database, ct);
        }
        finally
        {
            try { await hold.DisposeAsync(); } catch (Exception) { /* ended by DROP DATABASE WITH (FORCE) */ }
        }
    }

    /// <summary>
    /// Writes <c>{BackupDirectory}/{db}-{yyyyMMddHHmmss}.dump</c> with <c>pg_dump --format=custom --no-owner --no-privileges</c>
    /// (ruling Q7): one consistent snapshot of the database, taken while it keeps serving (<c>pg_dump</c> reads in a repeatable read
    /// transaction and holds only <c>ACCESS SHARE</c> locks, which writes do not wait for; a schema change waits for the dump).
    /// The dump is written to a temporary file of its own (a GUID in its name, so concurrent backups never share one) and renamed
    /// without overwriting: it is whole or absent, and a backup of the same database in the same second gets the name
    /// <c>{db}-{yyyyMMddHHmmss}-{n}.dump</c> (<see cref="MoveToFreeName"/>). Then the files archive, as on SQL Server.
    /// </summary>
    public async Task<BackupResult> BackupAsync(string? name, CancellationToken ct)
    {
        var requested = RequireValidName(name);
        string database;
        await using (var maintenance = await PostgresDatabases.OpenMaintenanceAsync(OrgConnectionString, ct))
            database = await PostgresDatabases.FindAsync(maintenance, requested, ct)
                       ?? throw OrgProvisioning.NotFound($"The database '{requested}' does not exist.");
        if (await PostgresDatabases.ReadOrganizationAsync(OrgConnectionString, database, ct) is null)
            throw XrmdFaultException.InvalidArgument($"The database '{database}' is not an xrmverse organization database (it has no meta.schema_version table).");

        var stem = $"{database}-{UtcNow():yyyyMMddHHmmss}";
        var temp = Path.Combine(BackupDirectory, $"{stem}.{Guid.NewGuid():N}{DumpExtension}.tmp");
        string file;
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            await Tools.DumpAsync(ConnectionStringOf(database), temp, ct);
            if (AfterDump is { } afterDump) await afterDump(ct);
            file = MoveToFreeName(temp, BackupDirectory, stem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OrgBackupFiles.TryDelete(temp);
            throw XrmdFaultException.InvalidArgument($"The backup directory '{BackupDirectory}' cannot be written: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            OrgBackupFiles.TryDelete(temp);
            throw new XrmdFaultException(ErrorCodes.Unexpected, $"The backup of '{database}' failed: {ex.Message}", StatusCodes.Status500InternalServerError);
        }
        catch
        {
            OrgBackupFiles.TryDelete(temp); // cancelled: no partial .dump is left
            throw;
        }

        var bytes = new FileInfo(file).Length;
        // The archive is written after the .dump, as on SQL Server: a file deleted or replaced in between is in the dump but not in
        // the archive, and a restore reports it (RestoreResult.MissingFiles).
        if (BeforeArchive is { } hook) await hook(ct);
        var archive = Files.ArchivePathOf(file, DumpExtension);
        var (archiveBytes, count) = await Files.WriteArchiveAsync(database, archive, file, ct);
        return new BackupResult(file, bytes, archive, archiveBytes, count);
    }

    /// <summary>
    /// Renames <paramref name="temp"/> to <c>{stem}.dump</c>, or <c>{stem}-{n}.dump</c> when that name (or its files archive) is
    /// taken: <see cref="File.Move(string, string, bool)"/> without overwrite fails on a taken name, so two backups never write
    /// to or replace each other's file.
    /// </summary>
    private string MoveToFreeName(string temp, string directory, string stem)
    {
        const int Attempts = 100;
        for (var n = 0; n < Attempts; n++)
        {
            var candidate = Path.Combine(directory, (n == 0 ? stem : $"{stem}-{n}") + DumpExtension);
            if (File.Exists(Files.ArchivePathOf(candidate, DumpExtension))) continue;
            try
            {
                File.Move(temp, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // taken by a backup of the same second: try the next suffix
            }
        }

        throw new IOException($"no free backup file name for '{stem}{DumpExtension}' after {Attempts} attempts");
    }

    /// <summary>
    /// Restores <paramref name="file"/> (a <c>.dump</c> directly in the backup directory) as <paramref name="name"/> (ruling Q7).
    /// The order and the checks are SQL Server's: the archive first (with <paramref name="files"/>), then the database, then the
    /// blobs, then the check of the files the restored database references. The database is created as <see cref="CreateAsync"/>
    /// creates one (template0, ICU <c>und</c>) under a staging name, loaded with <c>pg_restore --no-owner --no-privileges
    /// --exit-on-error</c> (the objects belong to this server's login, whatever roles the source server had; the source's grants
    /// would name roles this server may not have), migrated to this version's schema (the system seed upgrades run when the
    /// organization is served, as after a SQL Server restore), stamped with its restore time, and only then given the target's
    /// name (<see cref="PostgresDatabases.SwapInAsync"/>): a failure leaves the target as it was and drops the staging database.
    /// Over an existing database (with <paramref name="overwrite"/>) the instance lock is held from the check to the swap (ruling
    /// M6), so a process starting on it meanwhile is refused.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(string? name, string? file, bool overwrite, bool files, CancellationToken ct)
    {
        var database = RequireValidName(name);
        if (!OrgBackupFiles.IsInDirectory(BackupDirectory, file))
            throw XrmdFaultException.InvalidArgument($"The backup file must be a file directly in the backup directory '{BackupDirectory}'; got '{file}'.");

        FileStream? archiveStream = null;
        string? archive = null;
        string? sourceDatabase = null;
        try
        {
            if (files)
            {
                archive = Files.ArchivePathOf(file!, DumpExtension);
                (archiveStream, sourceDatabase) = await OrgBackupFiles.OpenArchiveAsync(archive, file!, ct);
            }

            if (!File.Exists(file)) throw OrgProvisioning.NotFound($"The backup file '{file}' does not exist.");

            await using (var maintenance = await PostgresDatabases.OpenMaintenanceAsync(OrgConnectionString, ct))
            {
                if (IsServed(database)) throw OrgProvisioning.Conflict($"The database '{database}' is the one this server serves; it cannot be restored over.");
                // Task 9 fix round 1: the staging and replaced databases a restore stopped half way left behind (a process that
                // died). Safe beside restores running in any process of the server, without the instance lock (which guards
                // one target only): the sweep drops only a database no session is on and whose claim it can take, and every
                // running restore holds the claim of its own from before it creates it until after it is renamed or dropped.
                foreach (var leftover in await PostgresDatabases.SweepLeftoversAsync(maintenance, OrgConnectionString, ct))
                    logger.LogWarning("Restore of {Database}: dropped {Leftover}, left behind by an earlier restore", database, leftover);
                DbConnection? hold = null;
                try
                {
                    var exists = await PostgresDatabases.ExistsAsync(maintenance, database, ct);
                    if (exists)
                    {
                        if (!overwrite) throw OrgProvisioning.Conflict($"The database '{database}' already exists; set overwrite to true to restore over it.");
                        if (await PostgresDatabases.ReadOrganizationAsync(OrgConnectionString, database, ct) is null)
                            throw XrmdFaultException.InvalidArgument($"The database '{database}' is not an xrmverse organization database; it is not restored over.");
                        // Ending its sessions would take the database from under the other process, which would keep serving a replaced one.
                        hold = await OrganizationDatabase.TryTakeInstanceLockAsync(ConnectionStringOf(database), StorageProvider.PostgreSql, ct)
                               ?? throw OrgProvisioning.Conflict($"The database '{database}' is served by another xrmd process; stop it first.");
                        if (AfterInstanceCheck is { } check) await check(ct);
                    }

                    logger.LogInformation("Restoring organization database {Database} from {File} (over an existing one: {Exists})", database, file, exists);
                    await RestoreDatabaseAsync(maintenance, database, file!, exists, async _ =>
                    {
                        if (hold is null) return;
                        await hold.DisposeAsync(); // the lock goes with the session; the database takes no new connection already
                        hold = null;
                    }, ct);
                }
                finally
                {
                    if (hold is not null)
                    {
                        try { await hold.DisposeAsync(); } catch (Exception) { /* a session already ended */ }
                    }
                }
            }

            // The restored database has the meta.filesecret of the backup: tokens signed with the key cached for the old one are void.
            fileColumns.ForgetDatabase(database);

            // From here the database is restored: the blob phase is not cancelled halfway, which would leave the files incomplete.
            if (archiveStream is not null)
                await Files.RestoreBlobsAsync(database, archiveStream, archive!, sourceDatabase, file!, BeforeFilesRestore);

            return await CheckFilesAsync(database);
        }
        finally
        {
            if (archiveStream is not null) await archiveStream.DisposeAsync();
        }
    }

    /// <summary>
    /// The database side of <see cref="RestoreAsync"/>: a staging database loaded, migrated and stamped, then swapped in for
    /// <paramref name="database"/> (<paramref name="replace"/>: over the existing one; <paramref name="release"/> ends the caller's
    /// session on it once it takes no new connection). The staging database is dropped on any failure.
    /// </summary>
    private async Task RestoreDatabaseAsync(DbConnection maintenance, string database, string file, bool replace,
        Func<CancellationToken, Task> release, CancellationToken ct)
    {
        var staging = await PostgresDatabases.CreateStagingAsync(maintenance, ct);
        try
        {
            try
            {
                await Tools.RestoreAsync(ConnectionStringOf(staging), file, ct);
            }
            catch (InvalidOperationException ex) when (IsUnusableArchive(ex.Message))
            {
                throw XrmdFaultException.InvalidArgument($"The file '{file}' is not a usable PostgreSQL backup: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                throw new XrmdFaultException(ErrorCodes.Unexpected, $"The restore of '{database}' from '{file}' failed: {ex.Message}",
                    StatusCodes.Status500InternalServerError);
            }

            // A backup of an older version is brought to this version's schema before anyone can open it.
            await using (var services = OrganizationDatabase.BuildServices(ConnectionStringOf(staging), StorageProvider.PostgreSql))
                await services.GetRequiredService<SchemaMigrator>().MigrateAsync(ct);
            // Change tracking tokens issued before the restore (by the database it replaces, or by the backup's source after its
            // backup) may name versions the restored database reuses: the restore time expires them all.
            await StampRestoreAsync(staging);
            if (BeforeSwap is { } hook) await hook(ct);

            if (!await PostgresDatabases.SwapInAsync(maintenance, OrgConnectionString, staging, database, replace, release, ct))
                throw OrgProvisioning.Conflict(replace
                    ? $"The database '{database}' changed while it was being restored over; retry the restore."
                    : $"The database '{database}' already exists; set overwrite to true to restore over it.");
        }
        catch
        {
            try
            {
                await PostgresDatabases.DropDatabaseAsync(maintenance, OrgConnectionString, staging, CancellationToken.None);
            }
            catch (Exception dropFailure) when (dropFailure is DbException or InvalidOperationException)
            {
                logger.LogError(dropFailure, "Restore of {Database}: the staging database {Staging} could not be dropped; the next restore sweeps it", database, staging);
            }

            throw;
        }
        finally
        {
            await PostgresDatabases.ReleaseClaimAsync(maintenance, staging);
        }

        logger.LogInformation("Restore of {Database}: change tracking tokens issued before the restore are expired", database);
    }

    /// <summary>True when <c>pg_restore</c>'s error says the file is not an archive it can read.</summary>
    private static bool IsUnusableArchive(string message) =>
        message.Contains("does not appear to be a valid archive", StringComparison.Ordinal)
        || message.Contains("unsupported version", StringComparison.Ordinal)
        || message.Contains("input file is too short", StringComparison.Ordinal)
        || message.Contains("could not read input file", StringComparison.Ordinal);

    /// <summary>
    /// Sets <c>meta.change_tracking_state.restored_at</c> of <paramref name="database"/> to now (UTC), as on SQL Server (plan 2.13);
    /// a backup without that table or column has nothing to stamp.
    /// </summary>
    private async Task StampRestoreAsync(string database)
    {
        await using var conn = await PostgresDatabases.OpenUnpooledAsync(ConnectionStringOf(database), CancellationToken.None);
        await using var exists = conn.CreateCommand();
        exists.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'meta' " +
                             "AND table_name = 'change_tracking_state' AND column_name = 'restored_at')";
        if (await exists.ExecuteScalarAsync(CancellationToken.None) is not true) return;
        await using var stamp = conn.CreateCommand();
        stamp.CommandText = "UPDATE meta.change_tracking_state SET restored_at = now()";
        await stamp.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>The check of the restored database's <c>fileattachment</c> rows against the store (<see cref="OrgBackupFiles.CheckFilesAsync"/>).</summary>
    private async Task<RestoreResult> CheckFilesAsync(string database)
    {
        await using var conn = await PostgresDatabases.OpenUnpooledAsync(ConnectionStringOf(database), CancellationToken.None);
        await using (var exists = conn.CreateCommand())
        {
            exists.CommandText = "SELECT to_regclass('data.fileattachment') IS NOT NULL";
            if (await exists.ExecuteScalarAsync(CancellationToken.None) is not true) return RestoreResult.Complete;
        }

        return await Files.CheckFilesAsync(database, conn);
    }
}
