using Microsoft.AspNetCore.Http;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;

namespace Xrmd.Admin;

/// <summary>An organization database as <c>GET /admin/orgs</c> lists it.</summary>
public sealed record OrganizationInfo(string Name, string Database, int SchemaVersion, bool Served);

/// <summary>The answer of <c>POST /admin/orgs</c>.</summary>
public sealed record CreatedOrganization(string Name, string Database, Guid RootBusinessUnitId, Guid AdminUserId);

/// <summary>
/// The lifecycle of the organization databases on the server of <c>ConnectionStrings:Org</c> (plan 2.7a, Task 5): create, list,
/// test, drop, back up and restore, on the engine of <c>Storage:Provider</c> (one per server, ruling P2): SQL Server's
/// <c>BACKUP</c>/<c>RESTORE</c> (<see cref="SqlServerOrgProvisioner"/>), or <c>pg_dump</c>/<c>pg_restore</c> since plan 2.7b
/// (<see cref="PostgresOrgProvisioner"/>, ruling Q7). The served database (the one of <c>ConnectionStrings:Org</c>) is never
/// dropped or restored over.
/// </summary>
public interface IOrgProvisioner
{
    /// <summary>The engine of the databases this provisioner manages.</summary>
    StorageProvider Provider { get; }

    /// <summary>Creates the database, migrates and seeds it and makes <paramref name="adminObjectId"/> its System Administrator.</summary>
    Task<CreatedOrganization> CreateAsync(string? name, Guid adminObjectId, string? adminName, CancellationToken ct);

    /// <summary>Drops the organization database <paramref name="name"/> (409 when it is served, by this or another process).</summary>
    Task DropAsync(string? name, CancellationToken ct);

    /// <summary>The xrmverse databases of the server (with <c>meta.schema_version</c>), by database name.</summary>
    Task<IReadOnlyList<OrganizationInfo>> ListAsync(CancellationToken ct);

    /// <summary>True when the server has a database named <paramref name="name"/> (an xrmverse one or not).</summary>
    Task<bool> ExistsAsync(string? name, CancellationToken ct);

    /// <summary>
    /// Backs up the organization database <paramref name="name"/> into the backup directory, with the <c>.files.tar</c> of its
    /// file and image columns beside it (ruling F9).
    /// </summary>
    Task<BackupResult> BackupAsync(string? name, CancellationToken ct);

    /// <summary>
    /// Restores <paramref name="file"/> (a file directly in the backup directory) as <paramref name="name"/>; over an existing
    /// database only with <paramref name="overwrite"/>, never over the served one or one another xrmd process serves (409). With
    /// <paramref name="files"/> the matching <c>.files.tar</c> is required and replaces the blobs of the target. The restored
    /// database's files that the store lacks are returned, never silently accepted.
    /// </summary>
    Task<RestoreResult> RestoreAsync(string? name, string? file, bool overwrite, bool files, CancellationToken ct);
}

/// <summary>
/// The test seams of backup and restore that both provisioners have (plan 2.7b, Task 9): the interleavings the integration
/// tests run on both engines.
/// </summary>
internal interface IOrgBackupSeams
{
    /// <summary>Runs in drop and restore right after the instance lock check (ruling M6), while its lock is held.</summary>
    Func<CancellationToken, Task>? AfterInstanceCheck { get; set; }

    /// <summary>Runs in backup after the database backup is written and before the files archive is.</summary>
    Func<CancellationToken, Task>? BeforeArchive { get; set; }

    /// <summary>Runs in restore after the archive was validated, before the blobs are restored from it.</summary>
    Func<CancellationToken, Task>? BeforeFilesRestore { get; set; }
}

/// <summary>The checks and faults the provisioners share (plan 2.7a, Task 5).</summary>
public static class OrgProvisioning
{
    /// <summary>The name, validated before any SQL.</summary>
    public static string RequireValidName(string? name) =>
        OrganizationDatabase.IsValidName(name) ? name! : throw XrmdFaultException.InvalidArgument($"Invalid organization database name '{name}'.");

    /// <summary>The 409 of a database that exists or is served.</summary>
    public static XrmdFaultException Conflict(string message) => new(ErrorCodes.InvalidArgument, message, StatusCodes.Status409Conflict);

    /// <summary>The 404 of a database that does not exist.</summary>
    public static XrmdFaultException NotFound(string message) => new(ErrorCodes.ObjectDoesNotExist, message, StatusCodes.Status404NotFound);
}
