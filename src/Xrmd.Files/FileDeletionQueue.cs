using System.Threading.Channels;

namespace Xrmd.Files;

/// <summary>
/// The blob deletions that follow a transaction's outcome (ruling F8: a released file after its commit, a written one after its
/// rollback, the staged blocks of an ended upload session), handed from the request thread to the background: a bounded,
/// in-process queue of store keys that <see cref="FileColumnService.ProcessDeletionsAsync"/> drains (run by
/// <see cref="FileCleanupService"/>). An item ending with <c>/</c> is a prefix (staged blocks), any other a blob key.
/// </summary>
/// <remarks>
/// Nothing here is durable. A deletion that is never run (the queue was full, the process stopped or crashed before draining
/// it, or the store failed) leaves a blob without a <c>fileattachment</c> row, or staged blocks without a session: the hourly
/// orphan sweep (<see cref="FileColumnService.SweepAsync"/>) deletes those once the grace has passed, so a lost item only
/// delays the deletion.
/// </remarks>
public sealed class FileDeletionQueue
{
    /// <summary>The items the queue holds before refusing more (each refused one is left to the sweep).</summary>
    public const int DefaultCapacity = 100_000;

    private readonly Channel<string> _channel;
    private long _pending;

    public FileDeletionQueue(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false,
        });
    }

    /// <summary>The items enqueued and not yet processed (or being processed).</summary>
    public long Pending => Interlocked.Read(ref _pending);

    /// <summary>Queues the deletion of a blob key or of a prefix (ending with <c>/</c>); false, never blocking, when the queue is full.</summary>
    public bool TryEnqueue(string keyOrPrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyOrPrefix);
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(keyOrPrefix)) return true;
        Interlocked.Decrement(ref _pending);
        return false;
    }

    /// <summary>The next item, waiting for one.</summary>
    internal ValueTask<string> ReadAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);

    /// <summary>Marks an item read with <see cref="ReadAsync"/> as processed (deleted, or left to the sweep).</summary>
    internal void Processed() => Interlocked.Decrement(ref _pending);

    /// <summary>Waits until every queued item has been processed (tests, orderly shutdown).</summary>
    /// <exception cref="TimeoutException">Items are still pending after <paramref name="timeout"/>.</exception>
    public async Task WhenIdleAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Pending > 0)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{Pending} file deletion(s) are still pending after {timeout}.");
            await Task.Delay(10, ct);
        }
    }
}
