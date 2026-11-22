using Xrmd.Files;

namespace Xrmd.Integration.Tests;

/// <summary>
/// An <see cref="IFileStore"/> over another one that counts the reads (<see cref="OpenReadAsync"/>, <see cref="GetLengthAsync"/>), records the keys written
/// and, when <see cref="VanishOnOpen"/> is set, answers a read as if the blob had been deleted between its length and its open.
/// </summary>
public sealed class SpyFileStore(IFileStore inner) : IFileStore
{
    private int _reads;

    /// <summary>The number of reads (opens and lengths) since the store was made.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>When true, <see cref="OpenReadAsync"/> throws <see cref="FileNotFoundException"/> whatever the store holds.</summary>
    public bool VanishOnOpen { get; set; }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _written = new();

    /// <summary>The keys written (<see cref="WriteAsync"/>, <see cref="ConcatAsync"/>) since the store was made, in order.</summary>
    public IReadOnlyList<string> Written => [.. _written];

    public Task WriteAsync(string key, Stream content, CancellationToken ct)
    {
        _written.Enqueue(key);
        return inner.WriteAsync(key, content, ct);
    }

    /// <summary>Runs before each <see cref="OpenReadAsync"/> (e.g. to make another transaction change the rows meanwhile).</summary>
    public Func<string, Task>? BeforeOpen { get; set; }

    public async Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct)
    {
        Interlocked.Increment(ref _reads);
        if (BeforeOpen is { } before) await before(key);
        return VanishOnOpen ? throw new FileNotFoundException($"File '{key}' does not exist.", key) : await inner.OpenReadAsync(key, offset, length, ct);
    }

    public Task<long?> GetLengthAsync(string key, CancellationToken ct)
    {
        Interlocked.Increment(ref _reads);
        return inner.GetLengthAsync(key, ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);

    public Task DeletePrefixAsync(string prefix, CancellationToken ct) => inner.DeletePrefixAsync(prefix, ct);

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);

    public Task ConcatAsync(IReadOnlyList<string> sourceKeys, string targetKey, CancellationToken ct)
    {
        _written.Enqueue(targetKey);
        return inner.ConcatAsync(sourceKeys, targetKey, ct);
    }
}
