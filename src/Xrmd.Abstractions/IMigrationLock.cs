using Xrmd.Core.Errors;

namespace Xrmd.Abstractions;

/// <summary>
/// The organization's migration lock (migration spec §7.2, plan M3 R4): while a migration run is open (not finalized), writes from
/// every front end are refused with 503 and reads go through. One process serves one organization database, so the lock is a
/// process-wide state, loaded from the run ledger at start.
/// </summary>
public interface IMigrationLock
{
    /// <summary>The open run holding the lock, or null when the organization is not locked.</summary>
    Guid? ActiveRunId { get; }

    /// <summary>
    /// Throws the 503 of a locked organization unless it is not locked or the current flow is the migration itself
    /// (<see cref="MigrationScope"/>).
    /// </summary>
    /// <exception cref="XrmdFaultException">503 <c>0x80040216</c>: the organization is being migrated.</exception>
    void Demand()
    {
        if (ActiveRunId is { } run && !MigrationScope.IsActive) throw MigrationLocks.Locked(run);
    }
}

/// <summary>The default <see cref="IMigrationLock"/>: never locked (a host without the migration server side, tests).</summary>
public sealed class NoMigrationLock : IMigrationLock
{
    public static readonly NoMigrationLock Instance = new();

    public Guid? ActiveRunId => null;
}

/// <summary>The fault of a locked organization.</summary>
public static class MigrationLocks
{
    /// <summary>
    /// 503 with the generic code <c>0x80040216</c>: Dataverse has no state like this one, so no code of its own exists (xrmverse-only
    /// behavior, documented in docs/migration.md).
    /// </summary>
    public static XrmdFaultException Locked(Guid runId) => new(ErrorCodes.Unexpected,
        $"The organization is being migrated (run {runId}): writes are refused until the run is finalized. Reads are allowed.", 503);
}
