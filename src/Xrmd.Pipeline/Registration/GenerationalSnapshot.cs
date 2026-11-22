namespace Xrmd.Pipeline.Registration;

/// <summary>
/// A value read from the database once per generation, shared by the registries of the pipeline (<see cref="StepRegistry"/>,
/// <see cref="CustomApis.CustomApiRegistry"/>): <see cref="Invalidate"/> starts a new generation, and the next read loads the
/// value again. A loaded value is stale as soon as its generation differs from the current one, whenever the invalidation ran
/// (also while it was loading): the check is made at every read, so no invalidation can be lost. One load at a time.
/// </summary>
/// <typeparam name="T">The snapshot type (immutable once loaded).</typeparam>
internal sealed class GenerationalSnapshot<T> where T : class
{
    private sealed record Entry(int Generation, T Value);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Entry? _entry;
    private int _generation;

    /// <summary>The current generation: it changes at every <see cref="Invalidate"/>.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// The last value loaded with the generation it was loaded for, stale or not; null before the first load. It never loads and
    /// never waits, so a synchronous reader can use it.
    /// </summary>
    public (int Generation, T Value)? Loaded => _entry is { } entry ? (entry.Generation, entry.Value) : null;

    /// <summary>Drops the loaded value: the next read loads it again.</summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>The value of the current generation, loaded with <paramref name="load"/> when it is missing or stale.</summary>
    public async Task<T> GetAsync(Func<CancellationToken, Task<T>> load, CancellationToken ct)
    {
        var entry = _entry;
        if (entry is not null && entry.Generation == Generation) return entry.Value;
        await _gate.WaitAsync(ct);
        try
        {
            entry = _entry;
            if (entry is not null && entry.Generation == Generation) return entry.Value;
            var generation = Generation;
            entry = new Entry(generation, await load(ct));
            _entry = entry;
            return entry.Value;
        }
        finally { _gate.Release(); }
    }
}
