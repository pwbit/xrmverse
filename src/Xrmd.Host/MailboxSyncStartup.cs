using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Mail;
using Xrmd.Pipeline.Async;

namespace Xrmd.Host;

/// <summary>
/// At host start, after the migrations, and then every <see cref="SweepInterval"/>: queues the sync job of every mailbox that can receive
/// and has none (<see cref="MailboxSyncJob.ScheduleReceivableAsync"/>, plan 4.1c, task 9), so a job lost to a crash, a restored database or
/// a race with a test is repaired here. A failed or skipped sweep (the database may be down, or locked for a migration) is tried again
/// after <see cref="RetryInterval"/>. Idle when the host has no organization database.
/// </summary>
public sealed class MailboxSyncStartup(Func<IDbConnectionFactory> factory, AsyncOperationQueue queue, bool enabled, ILogger<MailboxSyncStartup> logger,
    TimeProvider time, IMigrationLock? migrationLock = null) : BackgroundService
{
    /// <summary>How long after a failed or skipped attempt the next one runs.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long after a successful sweep the next one runs.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled) return;
        await Task.Yield(); // never hold up the host's startup
        try
        {
            while (true) await Task.Delay(await TryOnceAsync(stoppingToken) ? SweepInterval : RetryInterval, time, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One sweep; false when skipped (a migration holds the organization) or failed (logged).</summary>
    private async Task<bool> TryOnceAsync(CancellationToken stoppingToken)
    {
        // Migration plan M3 (R4): no background write while the organization is locked for a migration.
        if (migrationLock?.ActiveRunId is not null) return false;
        try
        {
            var queued = await MailboxSyncJob.ScheduleReceivableAsync(factory(), queue, logger, stoppingToken);
            if (queued > 0) logger.LogInformation("{Count} mailbox sync jobs were queued.", queued);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The mailbox sync jobs could not be scheduled; trying again in {Interval}.", RetryInterval);
            return false;
        }
    }
}
