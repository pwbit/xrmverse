using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Xrmd.Files;

/// <summary>
/// The background file work of the host: it drains the <see cref="FileDeletionQueue"/> (the blob deletions that follow a
/// commit or a rollback, <see cref="FileColumnService.ProcessDeletionsAsync"/>) and runs <see cref="FileColumnService.SweepAsync"/>
/// every <see cref="Interval"/> (the first run one interval after startup): expired file sessions and their staged blocks,
/// orphan blobs (ruling F8: a blob deletion that failed or was never run is retried here) and stale temporary files. Idle when
/// the host has no organization database.
/// </summary>
public sealed class FileCleanupService(Func<FileColumnService> files, bool enabled, ILogger<FileCleanupService> logger, TimeProvider time)
    : BackgroundService
{
    /// <summary>How often the sweep runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled) return Task.CompletedTask;
        return Task.WhenAll(Task.Run(() => files().ProcessDeletionsAsync(stoppingToken), CancellationToken.None), SweepLoopAsync(stoppingToken));
    }

    private async Task SweepLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await files().SweepAsync(time.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "The file cleanup sweep failed; it runs again in {Interval}.", Interval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
