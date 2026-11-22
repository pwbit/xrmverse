using System.Data;
using System.Formats.Tar;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Metadata;

namespace Xrmd.Admin;

/// <summary>
/// The lifecycle of the organization databases on the SQL Server of <c>ConnectionStrings:Org</c>: create, list, delete, back
/// up and restore. The served database (the one of <c>ConnectionStrings:Org</c>) can be listed and backed up, never deleted
/// or restored over. Database names reach T-SQL only after <see cref="OrganizationDatabase.NamePattern"/> and through QUOTENAME;
/// every value is a parameter. A database is an xrmverse one when it has the table <c>meta.schema_version</c> (the table of
/// <see cref="SchemaMigrator"/>).
/// </summary>
/// <remarks>Plan 2.7a, Task 5: the SQL Server <see cref="IOrgProvisioner"/>. Its files archives are <see cref="OrgBackupFiles"/>'s (plan 2.7b, Task 9).</remarks>
public sealed class SqlServerOrgProvisioner(IConfiguration configuration, ILogger<SqlServerOrgProvisioner> logger, IFileStore fileStore, FileColumnService fileColumns)
    : IOrgProvisioner, IOrgBackupSeams
{
    public StorageProvider Provider => StorageProvider.SqlServer;

    Func<CancellationToken, Task>? IOrgBackupSeams.AfterInstanceCheck { get => AfterInstanceCheck; set => AfterInstanceCheck = value; }
    Func<CancellationToken, Task>? IOrgBackupSeams.BeforeArchive { get => BeforeArchive; set => BeforeArchive = value; }
    Func<CancellationToken, Task>? IOrgBackupSeams.BeforeFilesRestore { get => BeforeFilesRestore; set => BeforeFilesRestore = value; }

    /// <summary>Test seam: runs between the existence check and the CREATE DATABASE of <see cref="CreateAsync"/>.</summary>
    internal Func<CancellationToken, Task>? BeforeCreateDatabase { get; set; }

    /// <summary>Test seam: runs right after <see cref="CreateAsync"/> created the database.</summary>
    internal Func<CancellationToken, Task>? AfterCreateDatabase { get; set; }

    /// <summary>Test seam: runs before the RESTORE statement, with the master connection it runs on.</summary>
    internal Func<SqlConnection, CancellationToken, Task>? BeforeRestore { get; set; }

    /// <summary>Test seam: runs in <see cref="DropAsync(string?, CancellationToken)"/> and <see cref="RestoreAsync(string?, string?, bool, bool, CancellationToken)"/>
    /// right after the instance lock check (ruling M6), while its lock is held.</summary>
    internal Func<CancellationToken, Task>? AfterInstanceCheck { get; set; }

    /// <summary>Test seam: runs in <see cref="BackupAsync"/> after the .bak is written and before the files archive is.</summary>
    internal Func<CancellationToken, Task>? BeforeArchive { get; set; }

    /// <summary>Test seam: runs in <see cref="RestoreAsync"/> after the archive was validated, before the blobs are restored from it.</summary>
    internal Func<CancellationToken, Task>? BeforeFilesRestore { get; set; }

    /// <summary>The most missing files a <see cref="RestoreResult"/> lists one by one.</summary>
    public const int MaxReportedMissing = OrgBackupFiles.MaxReportedMissing;

    public const string DefaultBackupDirectory = "/var/opt/mssql/backup";

    private const int CannotOpenBackupDevice = 3201;
    private static readonly int[] UnusableBackup = [3013, 3241, 3242, 3243, 3169];

    public string BackupDirectory => configuration["Admin:BackupDirectory"] is { Length: > 0 } d ? d : DefaultBackupDirectory;

    /// <summary>
    /// The directory of the <c>.files.tar</c> archives as this process sees it (<c>Admin:FilesBackupDirectory</c>). It is the backup
    /// directory itself by default: the compose files mount the same volume on SQL Server and on xrmd at that path.
    /// </summary>
    public string FilesBackupDirectory => configuration["Admin:FilesBackupDirectory"] is { Length: > 0 } d ? d : BackupDirectory;

    /// <summary>The files side of backup and restore (ruling F9), shared with PostgreSQL (plan 2.7b, Task 9).</summary>
    private OrgBackupFiles Files => new(fileStore, fileColumns, logger, FilesBackupDirectory);

    private string ArchivePathOf(string bakFile) => Files.ArchivePathOf(bakFile, ".bak");

    private string OrgConnectionString =>
        configuration.GetConnectionString("Org") ?? throw new InvalidOperationException("ConnectionStrings:Org is not configured.");

    private string ServedDatabase => new SqlConnectionStringBuilder(OrgConnectionString).InitialCatalog;

    private string ConnectionStringOf(string database) => new SqlConnectionStringBuilder(OrgConnectionString) { InitialCatalog = database }.ConnectionString;

    private async Task<SqlConnection> OpenMasterAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(ConnectionStringOf("master"));
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

    /// <summary>The name, validated before any SQL.</summary>
    public static string RequireValidName(string? name) => OrgProvisioning.RequireValidName(name);

    private static XrmdFaultException Conflict(string message) => OrgProvisioning.Conflict(message);

    private static XrmdFaultException NotFound(string message) => OrgProvisioning.NotFound(message);

    private bool IsServed(string database) => string.Equals(database, ServedDatabase, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 409 when another xrmd process serves <paramref name="database"/> (ruling M6). The check takes the instance lock itself rather
    /// than only testing it (<c>APPLOCK_TEST</c>), and the caller holds it until <c>SINGLE_USER WITH ROLLBACK IMMEDIATE</c> (which
    /// ends this session too): a process starting on the database in between is refused at startup instead of having its sessions
    /// ended under it. From SINGLE_USER on, this provisioner's master session is the database's one user, so no process can connect.
    /// </summary>
    /// <param name="hold">Receives the session holding the lock; dispose it once the database is in SINGLE_USER (or on failure).</param>
    private async Task HoldNotServedByAnotherAsync(string database, InstanceLockHold hold, CancellationToken ct)
    {
        hold.Connection = await OrganizationDatabase.TryTakeInstanceLockAsync(ConnectionStringOf(database), StorageProvider.SqlServer, ct)
                          ?? throw Conflict($"The database '{database}' is served by another xrmd process; stop it first.");
        if (AfterInstanceCheck is { } hook) await hook(ct);
    }

    /// <summary>The provisioner's instance lock session, if taken; disposing it ignores a session already ended by ROLLBACK IMMEDIATE.</summary>
    private sealed class InstanceLockHold : IAsyncDisposable
    {
        public System.Data.Common.DbConnection? Connection { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (Connection is not { } connection) return;
            Connection = null;
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception)
            {
                // Ended by SINGLE_USER WITH ROLLBACK IMMEDIATE: nothing to release.
            }
        }
    }

    private static async Task<bool> ExistsAsync(SqlConnection master, string database, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END", master);
        cmd.Parameters.AddWithValue("@db", database);
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }

    private static async Task<bool> IsXrmverseAsync(SqlConnection master, string database, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT CASE WHEN OBJECT_ID(QUOTENAME(@db) + N'.meta.schema_version', N'U') IS NULL THEN 0 ELSE 1 END", master);
        cmd.Parameters.AddWithValue("@db", database);
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }

    /// <summary>An existing xrmverse database: 404 when there is none, 400 when it is not an xrmverse one.</summary>
    private static async Task RequireOrganizationAsync(SqlConnection master, string database, CancellationToken ct)
    {
        if (!await ExistsAsync(master, database, ct)) throw NotFound($"The database '{database}' does not exist.");
        if (!await IsXrmverseAsync(master, database, ct))
            throw XrmdFaultException.InvalidArgument($"The database '{database}' is not an xrmverse organization database (it has no meta.schema_version table).");
    }

    /// <summary>Creates the database, migrates and seeds it and makes <paramref name="adminObjectId"/> its System Administrator.</summary>
    public async Task<CreatedOrganization> CreateAsync(string? name, Guid adminObjectId, string? adminName, CancellationToken ct)
    {
        var database = RequireValidName(name);
        if (adminObjectId == Guid.Empty) throw XrmdFaultException.InvalidArgument("'adminObjectId' must be the administrator's Entra object id (a non-empty GUID).");
        var fullName = string.IsNullOrWhiteSpace(adminName) ? "Administrator" : adminName.Trim();

        await using var master = await OpenMasterAsync(ct);
        if (await ExistsAsync(master, database, ct)) throw Conflict($"The database '{database}' already exists.");
        if (BeforeCreateDatabase is { } before) await before(ct);
        try
        {
            // Unconditional: a database that appeared since the check (a concurrent create, or anyone else) is a 409,
            // never a database this request would migrate into and then drop on failure.
            await OrganizationDatabase.CreateDatabaseAsync(master, database, ct);
        }
        catch (SqlException ex) when (ex.Number == 1801)
        {
            throw Conflict($"The database '{database}' already exists.");
        }

        // From here on the database is this request's own: on failure it is dropped.
        try
        {
            if (AfterCreateDatabase is { } after) await after(ct);
            await OrganizationDatabase.EnableReadCommittedSnapshotAsync(master, database, ct);
            // Services of their own, bound to the new database: nothing of the served organization (caches included) is touched.
            await using var services = OrganizationDatabase.BuildServices(ConnectionStringOf(database));
            var seed = new SeedOptions(database, SeedOptions.DeriveRootBusinessUnitId(database), adminObjectId, fullName);
            var populated = await OrganizationDatabase.PopulateAsync(services, seed, ct);
            return new CreatedOrganization(database, database, populated.RootBusinessUnitId, populated.AdminUserId);
        }
        catch (Exception ex)
        {
            // Do not leave a half-set-up database behind (it would answer 409 to the retry).
            try
            {
                await DropAsync(master, database, CancellationToken.None);
            }
            catch (Exception dropFailure) when (dropFailure is SqlException or InvalidOperationException)
            {
                // The original failure is the one to report; the leftover is logged so it can be removed by hand.
                logger.LogError(dropFailure, "Creating organization database {Database} failed ({Reason}) and the half-created database could not be dropped; drop it by hand",
                    database, ex.Message);
            }

            throw;
        }
    }

    /// <summary>The xrmverse databases of the server (ONLINE, with <c>meta.schema_version</c>), by database name.</summary>
    public async Task<IReadOnlyList<OrganizationInfo>> ListAsync(CancellationToken ct)
    {
        await using var master = await OpenMasterAsync(ct);
        var names = new List<string>();
        await using (var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name", master))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) names.Add(rd.GetString(0));

        var result = new List<OrganizationInfo>();
        foreach (var database in names)
        {
            int version;
            try
            {
                if (!await IsXrmverseAsync(master, database, ct)) continue;
                version = await ScalarInAsync<int?>(master, database, "SELECT MAX(version) FROM {0}.meta.schema_version", ct) ?? 0;
            }
            catch (SqlException)
            {
                continue; // not accessible to this login
            }

            string? org = null;
            try
            {
                org = await ScalarInAsync<string?>(master, database, "SELECT TOP 1 name FROM {0}.data.organization", ct);
            }
            catch (SqlException)
            {
                // Set up only in part: no organization row yet.
            }

            result.Add(new OrganizationInfo(string.IsNullOrEmpty(org) ? database : org, database, version, IsServed(database)));
        }

        return result;
    }

    /// <param name="template">A SELECT with <c>{0}</c> where the quoted database name goes.</param>
    private static async Task<T?> ScalarInAsync<T>(SqlConnection master, string database, string template, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("DECLARE @sql nvarchar(max) = REPLACE(@template, N'{0}', QUOTENAME(@db)); EXEC(@sql);", master);
        cmd.Parameters.AddWithValue("@template", template);
        cmd.Parameters.AddWithValue("@db", database);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)value;
    }

    public async Task<bool> ExistsAsync(string? name, CancellationToken ct)
    {
        var database = RequireValidName(name);
        await using var master = await OpenMasterAsync(ct);
        return await ExistsAsync(master, database, ct);
    }

    public async Task DropAsync(string? name, CancellationToken ct)
    {
        var database = RequireValidName(name);
        await using var master = await OpenMasterAsync(ct);
        if (IsServed(database)) throw Conflict($"The database '{database}' is the one this server serves; it cannot be deleted.");
        await RequireOrganizationAsync(master, database, ct);
        await using var hold = new InstanceLockHold();
        await HoldNotServedByAnotherAsync(database, hold, ct);
        await DropAsync(master, database, ct);
    }

    private async Task DropAsync(SqlConnection master, string database, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            IF DB_ID(@db) IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(400) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@db);
                EXEC(@sql);
            END
            """, master) { CommandTimeout = 0 };
        cmd.Parameters.AddWithValue("@db", database);
        await cmd.ExecuteNonQueryAsync(ct);
        SqlConnection.ClearPool(new SqlConnection(ConnectionStringOf(database)));
    }

    /// <summary>Writes <c>{BackupDirectory}/{db}-{yyyyMMddHHmmss}.bak</c> on the SQL Server host (COPY_ONLY: the backup chain is untouched).</summary>
    public async Task<BackupResult> BackupAsync(string? name, CancellationToken ct)
    {
        var database = RequireValidName(name);
        await using var master = await OpenMasterAsync(ct);
        await RequireOrganizationAsync(master, database, ct);

        var file = Join(BackupDirectory, $"{database}-{DateTime.UtcNow:yyyyMMddHHmmss}.bak");
        await using (var cmd = new SqlCommand(
            "DECLARE @sql nvarchar(max) = N'BACKUP DATABASE ' + QUOTENAME(@db) + N' TO DISK = @file WITH COPY_ONLY, INIT'; " +
            "EXEC sp_executesql @sql, N'@file nvarchar(512)', @file = @file;", master) { CommandTimeout = 0 })
        {
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@file", file);
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex) when (ex.Number == CannotOpenBackupDevice)
            {
                throw XrmdFaultException.InvalidArgument($"The backup directory '{BackupDirectory}' cannot be written by SQL Server: {ex.Message}");
            }
        }

        await using var size = new SqlCommand("""
            SELECT TOP 1 CAST(bs.backup_size AS bigint)
            FROM msdb.dbo.backupset bs JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id = bs.media_set_id
            WHERE mf.physical_device_name = @file AND bs.database_name = @db
            ORDER BY bs.backup_finish_date DESC
            """, master);
        size.Parameters.AddWithValue("@file", file);
        size.Parameters.AddWithValue("@db", database);
        var bytes = await size.ExecuteScalarAsync(ct);

        // The archive is written after the .bak. A file uploaded in between is in the archive without being in the database (an
        // orphan the cleanup sweep removes). A file deleted or replaced in between is in the .bak but no longer in the store, so
        // the archive lacks it: the backup cannot see that, a restore reports it (RestoreResult.MissingFiles).
        if (BeforeArchive is { } hook) await hook(ct);
        var archive = ArchivePathOf(file);
        var (archiveBytes, count) = await Files.WriteArchiveAsync(database, archive, file, ct);
        return new BackupResult(file, bytes is long l ? l : 0, archive, archiveBytes, count);
    }

    /// <summary>
    /// Restores <paramref name="file"/> (in the backup directory) as <paramref name="name"/>, with its files moved to new names
    /// in the server's default data and log folders. Over an existing database only with <paramref name="overwrite"/>.
    /// </summary>
    public Task<RestoreResult> RestoreAsync(string? name, string? file, bool overwrite, CancellationToken ct) => RestoreAsync(name, file, overwrite, files: true, ct);

    /// <summary>
    /// Like the overload without <paramref name="files"/>. With <paramref name="files"/> the matching <c>.files.tar</c> (ruling F9) is
    /// required and replaces the blobs under the target's prefix (<c>{database}/</c>), remapped from the archive's source database.
    /// Order: the archive is found and validated first (a missing or unusable one stops before anything changes), then SQL Server is
    /// restored, then the blobs are replaced. If the blobs fail, the database is already restored: the error says so, and repeating
    /// the restore with overwrite redoes both. Without <paramref name="files"/> the target's blobs are not touched.
    /// Afterwards (with or without files) every <c>fileattachment</c> row of the restored database is checked against the store; the
    /// ones without a blob are returned, never silently accepted.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(string? name, string? file, bool overwrite, bool files, CancellationToken ct)
    {
        var database = RequireValidName(name);
        if (!IsInBackupDirectory(file))
            throw XrmdFaultException.InvalidArgument($"The backup file must be a file directly in the backup directory '{BackupDirectory}'; got '{file}'.");

        // The archive is opened once and held (shared read) from validation to the last blob, so it cannot change in between.
        FileStream? archiveStream = null;
        string? archive = null;
        string? sourceDatabase = null;
        try
        {
            if (files)
            {
                archive = ArchivePathOf(file!);
                (archiveStream, sourceDatabase) = await OrgBackupFiles.OpenArchiveAsync(archive, file!, ct);
            }

            await using var master = await OpenMasterAsync(ct);
            if (IsServed(database)) throw Conflict($"The database '{database}' is the one this server serves; it cannot be restored over.");
            await using var hold = new InstanceLockHold();
            var exists = await ExistsAsync(master, database, ct);
            if (exists)
            {
                if (!overwrite) throw Conflict($"The database '{database}' already exists; set overwrite to true to restore over it.");
                if (!await IsXrmverseAsync(master, database, ct))
                    throw XrmdFaultException.InvalidArgument($"The database '{database}' is not an xrmverse organization database; it is not restored over.");
                // SINGLE_USER WITH ROLLBACK IMMEDIATE would end the other process's lock session, and it would keep serving a replaced database.
                await HoldNotServedByAnotherAsync(database, hold, ct);
            }

            var moves = await MovesAsync(master, database, file!, ct);
            logger.LogInformation("Restoring organization database {Database} from {File} (over an existing one: {Exists})", database, file, exists);
            if (exists)
            {
                await using var single = new SqlCommand(
                    "DECLARE @sql nvarchar(400) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE'; EXEC(@sql);", master);
                single.Parameters.AddWithValue("@db", database);
                await single.ExecuteNonQueryAsync(ct);
                await hold.DisposeAsync(); // ended by ROLLBACK IMMEDIATE; from here this session is the database's one user
            }

            try
            {
                if (BeforeRestore is { } hook) await hook(master, ct);
                // RESTORE takes literals for MOVE: they are built in T-SQL from parameters, with their quotes doubled.
                await using var restore = new SqlCommand("""
                    DECLARE @sql nvarchar(max) = N'RESTORE DATABASE ' + QUOTENAME(@db) + N' FROM DISK = N''' + REPLACE(@file, N'''', N'''''') + N''' WITH '
                        + (SELECT STRING_AGG(CAST(N'MOVE N''' + REPLACE(m.logical, N'''', N'''''') + N''' TO N''' + REPLACE(m.physical, N'''', N'''''') + N'''' AS nvarchar(max)), N', ')
                           FROM OPENJSON(@moves) WITH (logical nvarchar(260) '$.logical', physical nvarchar(520) '$.physical') AS m)
                        + CASE WHEN @replace = 1 THEN N', REPLACE' ELSE N'' END;
                    IF @sql IS NULL THROW 50000, 'The restore statement could not be built.', 1;
                    EXEC(@sql);
                    """, master) { CommandTimeout = 0 };
                restore.Parameters.AddWithValue("@db", database);
                restore.Parameters.AddWithValue("@file", file!);
                restore.Parameters.AddWithValue("@moves", JsonSerializer.Serialize(moves, MoveJson));
                restore.Parameters.AddWithValue("@replace", exists ? 1 : 0);
                await restore.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex) when (exists)
            {
                var revertFailure = await TryMultiUserAsync(master, database);
                if (revertFailure is null) throw;
                throw StillSingleUser(database, revertFailure, ex);
            }

            if (exists && await TryMultiUserAsync(master, database) is { } failure) throw StillSingleUser(database, failure, restoreFailure: null);

            // Change tracking tokens issued before the restore (by this database before it was replaced, or by the backup's source
            // after its backup) may name versions the restored database reuses: the restore time expires them all.
            await StampRestoreAsync(database);

            // The restored database has the meta.filesecret of the backup: tokens signed with the key cached for the old one are void.
            // (With several organizations on one server, item 2.17, another xrmd process serving this database keeps its cached key; its
            // stale tokens still fail the session lookup, because meta.filesession was replaced by the restore.)
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
    /// Sets <c>meta.change_tracking_state.restored_at</c> of the restored <paramref name="database"/> to now (UTC), so that every
    /// change tracking token issued before is expired (plan 2.13). A backup from before change tracking has no such table (or
    /// column) yet: there is nothing to stamp, and no token of it can be valid (no table has a tracking start version).
    /// </summary>
    private async Task StampRestoreAsync(string database)
    {
        await using var conn = new SqlConnection(ConnectionStringOf(database));
        await conn.OpenAsync(CancellationToken.None);
        // Dynamic SQL: the column is resolved only when it exists, so the batch compiles on an older schema.
        await using var cmd = new SqlCommand("""
            IF COL_LENGTH(N'meta.change_tracking_state', N'restored_at') IS NOT NULL
                EXEC(N'UPDATE meta.change_tracking_state SET restored_at = SYSUTCDATETIME()');
            """, conn);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        logger.LogInformation("Restore of {Database}: change tracking tokens issued before the restore are expired", database);
    }

    /// <summary>The check of the restored database's <c>fileattachment</c> rows against the store (<see cref="OrgBackupFiles.CheckFilesAsync"/>).</summary>
    private async Task<RestoreResult> CheckFilesAsync(string database)
    {
        await using var conn = new SqlConnection(ConnectionStringOf(database));
        await conn.OpenAsync(CancellationToken.None);
        await using (var exists = new SqlCommand("SELECT OBJECT_ID(N'data.fileattachment')", conn))
            if (await exists.ExecuteScalarAsync(CancellationToken.None) is null or DBNull) return RestoreResult.Complete;
        return await Files.CheckFilesAsync(database, conn);
    }

    /// <summary>Sets the database back to MULTI_USER; the failure, or null when it worked.</summary>
    private static async Task<Exception?> TryMultiUserAsync(SqlConnection master, string database)
    {
        try
        {
            await using var multi = new SqlCommand(
                "IF DB_ID(@db) IS NOT NULL BEGIN DECLARE @sql nvarchar(400) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET MULTI_USER WITH ROLLBACK IMMEDIATE'; EXEC(@sql); END", master);
            multi.Parameters.AddWithValue("@db", database);
            await multi.ExecuteNonQueryAsync(CancellationToken.None);
            return null;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // InvalidOperationException: the connection was closed by a failed RESTORE.
            return ex;
        }
    }

    /// <summary>
    /// The failure to report when the MULTI_USER revert failed: the restore's own error (or success) plus the state the
    /// database may be left in and how to fix it; logged at error level too.
    /// </summary>
    private XrmdFaultException StillSingleUser(string database, Exception revertFailure, Exception? restoreFailure)
    {
        var note = $"Setting it back to MULTI_USER failed ({revertFailure.Message}): the database '{database}' may still be in SINGLE_USER mode; " +
                   $"run ALTER DATABASE [{database}] SET MULTI_USER.";
        logger.LogError(restoreFailure ?? revertFailure, "Restore of {Database}: {Note}", database, note);
        if (restoreFailure is XrmdFaultException fault) return new XrmdFaultException(fault.ErrorCode, $"{fault.Message} {note}", fault.HttpStatus);
        return restoreFailure is null
            ? new XrmdFaultException(ErrorCodes.Unexpected, $"The database '{database}' was restored. {note}", StatusCodes.Status500InternalServerError)
            : new XrmdFaultException(ErrorCodes.Unexpected, $"The restore of '{database}' failed: {restoreFailure.Message} {note}", StatusCodes.Status500InternalServerError);
    }

    private static readonly JsonSerializerOptions MoveJson = new(JsonSerializerDefaults.Web);

    private sealed record FileMove(string Logical, string Physical);

    /// <summary>The files of the backup (<c>RESTORE FILELISTONLY</c>), each mapped to a new name in the default data or log folder.</summary>
    private static async Task<List<FileMove>> MovesAsync(SqlConnection master, string database, string file, CancellationToken ct)
    {
        string? dataPath, logPath;
        await using (var paths = new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260)), CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(260))", master))
        await using (var rd = await paths.ExecuteReaderAsync(ct))
        {
            await rd.ReadAsync(ct);
            dataPath = rd.IsDBNull(0) ? null : rd.GetString(0);
            logPath = rd.IsDBNull(1) ? null : rd.GetString(1);
        }

        if (dataPath is null || logPath is null) throw new InvalidOperationException("SQL Server reports no default data or log folder.");

        var moves = new List<FileMove>();
        try
        {
            await using var cmd = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @file", master) { CommandTimeout = 0 };
            cmd.Parameters.AddWithValue("@file", file);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var logical = rd.GetOrdinal("LogicalName");
            var physical = rd.GetOrdinal("PhysicalName");
            var type = rd.GetOrdinal("Type");
            var n = 0;
            while (await rd.ReadAsync(ct))
            {
                var kind = rd.GetString(type);
                if (kind is not ("D" or "L"))
                    throw XrmdFaultException.InvalidArgument($"The backup contains a file of type '{kind}' (filestream or full-text), which is not supported.");
                var target = kind == "L" ? logPath : dataPath;
                moves.Add(new FileMove(rd.GetString(logical), Join(target, $"{database}_{n++}{Extension(rd.GetString(physical), kind)}")));
            }
        }
        catch (SqlException ex) when (ex.Number == CannotOpenBackupDevice)
        {
            throw NotFound($"The backup file '{file}' does not exist or SQL Server cannot read it.");
        }
        catch (SqlException ex) when (UnusableBackup.Contains(ex.Number))
        {
            throw XrmdFaultException.InvalidArgument($"The file '{file}' is not a usable SQL Server backup: {ex.Message}");
        }

        return moves.Count > 0 ? moves : throw XrmdFaultException.InvalidArgument($"The backup '{file}' lists no database files.");
    }

    private static string Extension(string physicalName, string kind)
    {
        var dot = physicalName.LastIndexOf('.');
        var ext = dot >= 0 ? physicalName[dot..] : "";
        return ext.Length is > 1 and <= 6 && ext.Skip(1).All(char.IsAsciiLetterOrDigit) ? ext : kind == "L" ? ".ldf" : ".mdf";
    }

    /// <summary>
    /// True when <paramref name="file"/> is a file directly in <see cref="BackupDirectory"/>: both are compared as text (the file
    /// is on the SQL Server host, which this process may not see), with no <c>.</c>/<c>..</c> or empty segments.
    /// </summary>
    public bool IsInBackupDirectory(string? file) => OrgBackupFiles.IsInDirectory(BackupDirectory, file);

    private static string Join(string directory, string name)
    {
        var separator = directory.Contains('\\') && !directory.Contains('/') ? '\\' : '/';
        return directory.TrimEnd('/', '\\') + separator + name;
    }
}
