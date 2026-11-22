using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Elastic;

namespace Xrmd.Host;

/// <summary>
/// Deletes the elastic rows whose time to live has passed (<see cref="ElasticTtl.PurgeAsync"/>, plan 3.4 ruling E7) every
/// <c>Elastic:TtlSweepSeconds</c> seconds, in batches of <see cref="Batch"/> rows per table. A sweep that finds a full batch
/// runs again at once. A failed sweep is logged and retried at the next tick. A setting of 0 (or less) disables the sweep: the
/// service does nothing.
/// </summary>
public sealed class ElasticTtlService(Func<IDbConnectionFactory> factory, int sweepSeconds, IServiceScopeFactory scopes,
    ILogger<ElasticTtlService> logger, TimeProvider time, IMigrationLock? migrationLock = null) : BackgroundService
{
    /// <summary>Rows deleted per table in one pass.</summary>
    public const int Batch = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (sweepSeconds <= 0) return;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(sweepSeconds), time);
            while (await timer.WaitForNextTickAsync(stoppingToken)) await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One sweep; a failure is logged and retried at the next tick (cancellation propagates).</summary>
    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // Migration plan M3 (R4): no background write while the organization is locked for a migration.
        if (migrationLock?.ActiveRunId is not null) return;
        try
        {
            // A pass deletes at most Batch rows per table, so a count of Batch or more may mean a table still has expired rows:
            // sweep again at once. A smaller count means every table was drained (a failing row is passed over, never counted).
            int deleted;
            do
            {
                deleted = await SweepOnceAsync(stoppingToken);
                if (deleted > 0) logger.LogInformation("The time to live sweep deleted {Count} expired elastic rows.", deleted);
            }
            while (deleted >= Batch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The time to live sweep failed; it runs again in {Seconds} seconds.", sweepSeconds);
        }
    }

    /// <summary>One pass over all elastic tables on a connection of its own (each row commits on its own).</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var conn = await factory().OpenAsync(ct);
        return await ElasticTtl.PurgeAsync(scope.ServiceProvider.GetRequiredService<CoreOperations>(), new DataSession(conn, null),
            time.GetUtcNow().UtcDateTime, Batch, ct, logger);
    }
}
