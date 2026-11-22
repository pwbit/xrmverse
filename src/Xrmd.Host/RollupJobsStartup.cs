using Xrmd.Abstractions;
using Xrmd.Metadata.Computed;
using Xrmd.Pipeline.Async;

namespace Xrmd.Host;

/// <summary>
/// At host start, brings the rollup calculation jobs in step with the rollup columns (<see cref="RollupJobScheduler.ReconcileAsync"/>,
/// plan 2.12b, ruling R9): a notification lost to a crash, a restored database or a job that ended is repaired here. Tried again every
/// <see cref="RetryInterval"/> until it succeeds once (the database may be down, or locked for a migration). Idle when the host has no
/// organization database.
/// </summary>
public sealed class RollupJobsStartup(Func<RollupJobScheduler> scheduler, bool enabled, ILogger<RollupJobsStartup> logger,
    TimeProvider time, IMigrationLock? migrationLock = null) : BackgroundService
{
    /// <summary>How long after a failed or skipped attempt the next one runs.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled) return;
        await Task.Yield(); // never hold up the host's startup
        try
        {
            while (!await TryOnceAsync(stoppingToken)) await Task.Delay(RetryInterval, time, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One reconcile; false when skipped (a migration holds the organization) or failed (logged).</summary>
    private async Task<bool> TryOnceAsync(CancellationToken stoppingToken)
    {
        // Migration plan M3 (R4): no background write while the organization is locked for a migration.
        if (migrationLock?.ActiveRunId is not null) return false;
        try
        {
            await scheduler().ReconcileAsync(stoppingToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The rollup calculation jobs could not be reconciled; trying again in {Interval}.", RetryInterval);
            return false;
        }
    }
}

/// <summary>
/// The <see cref="IRollupJobs"/> the metadata store is given: the scheduler, resolved on first use (it depends on the store).
/// </summary>
internal sealed class LazyRollupJobs(Func<RollupJobScheduler> scheduler) : IRollupJobs
{
    public Task DefinitionChangedAsync(string entity, string attribute, RollupChange change, CancellationToken ct) =>
        scheduler().DefinitionChangedAsync(entity, attribute, change, ct);
}
