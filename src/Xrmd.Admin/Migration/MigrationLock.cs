using Xrmd.Abstractions;

namespace Xrmd.Admin.Migration;

/// <summary>
/// The process-wide migration lock (plan M3 R4): set when a run is started, cleared when it is finalized, and loaded from the run
/// ledger at start, so a restart keeps it (one process per organization database).
/// </summary>
public sealed class MigrationLock : IMigrationLock
{
    // A boxed run id: reads and writes of a reference are atomic, a Guid? is not.
    private object? _runId;

    public Guid? ActiveRunId => Volatile.Read(ref _runId) as Guid?;

    /// <summary>Loads the lock from the ledger: on when a run is not finalized. A dry run never takes it (table mapping spec §8.6).</summary>
    public async Task LoadAsync(MigrationRunStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var open = await store.GetOpenRunAsync(ct);
        Volatile.Write(ref _runId, open is null || open.IsDryRun ? null : open.RunId);
    }

    public void Set(Guid runId) => Volatile.Write(ref _runId, runId);

    public void Clear() => Volatile.Write(ref _runId, null);
}
