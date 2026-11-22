using Xrmd.Abstractions;
using Xrmd.Audit;

namespace Xrmd.Host;

/// <summary>
/// Purges the expired audit rows (<see cref="AuditRetention.PurgeAsync"/>, plan 13 ruling A8) <see cref="FirstRunDelay"/> after
/// startup (a host restarted more often than hourly still purges), then every <see cref="Interval"/>. A failed run is logged and
/// retried at the next tick. Idle when the host has no organization database.
/// </summary>
public sealed class AuditRetentionService(Func<IDbConnectionFactory> factory, bool enabled, ILogger<AuditRetentionService> logger,
    TimeProvider time, IMigrationLock? migrationLock = null) : BackgroundService
{
    /// <summary>How often the purge runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>How long after startup the first purge runs.</summary>
    public static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled) return;
        try
        {
            await Task.Delay(FirstRunDelay, time, stoppingToken);
            await RunAsync(stoppingToken);
            using var timer = new PeriodicTimer(Interval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken)) await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One purge; a failure is logged and retried at the next run (cancellation propagates).</summary>
    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // Migration plan M3 (R4): no background write while the organization is locked for a migration.
        if (migrationLock?.ActiveRunId is not null) return;
        try
        {
            var purged = await PurgeOnceAsync(stoppingToken);
            if (purged > 0) logger.LogInformation("Audit retention deleted {Count} expired audit rows.", purged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The audit retention purge failed; it runs again in {Interval}.", Interval);
        }
    }

    /// <summary>One purge on a connection of its own (each batch commits on its own: a stopped purge keeps what it deleted).</summary>
    public async Task<int> PurgeOnceAsync(CancellationToken ct)
    {
        await using var conn = await factory().OpenAsync(ct);
        return await AuditRetention.PurgeAsync(new DataSession(conn, null), time.GetUtcNow().UtcDateTime, ct);
    }
}
