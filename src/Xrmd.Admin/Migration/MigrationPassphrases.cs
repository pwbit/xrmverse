using System.Collections.Concurrent;

namespace Xrmd.Admin.Migration;

/// <summary>
/// The passphrases of the runs about to be run (plan M3 R6): the start and resume requests put one, the job takes it. Only in this
/// process's memory: never logged, never in the ledger or the job's data. After a restart the run must be resumed with it.
/// </summary>
public sealed class MigrationPassphrases
{
    private readonly ConcurrentDictionary<Guid, string> _byRun = new();

    public void Put(Guid runId, string passphrase) => _byRun[runId] = passphrase;

    /// <summary>Takes (and removes) the passphrase of <paramref name="runId"/>.</summary>
    public bool TryTake(Guid runId, out string passphrase) => _byRun.TryRemove(runId, out passphrase!);

    public void Remove(Guid runId) => _byRun.TryRemove(runId, out _);
}
