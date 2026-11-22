namespace Xrmd.Files;

/// <summary>
/// Blob storage for file and image columns. Keys are <c>/</c>-separated segments of <c>[A-Za-z0-9._-]</c>;
/// a <c>..</c> segment, an absolute path or a backslash is rejected with <see cref="ArgumentException"/>.
/// Prefixes are plain string prefixes of keys (as in S3), so <c>db/</c> addresses everything under <c>db</c>.
/// </summary>
public interface IFileStore
{
    /// <summary>
    /// Confirms the store is reachable and usable without writing any blob (used by <c>xrmd doctor</c>, never by <c>/healthz</c>).
    /// The message names the store kind and its directory, bucket or container, and never a credential. The default says ok
    /// without probing, which suits wrappers and in-memory stores; the built-in stores override it.
    /// </summary>
    Task<FileStoreCheck> CheckAsync(CancellationToken ct) => Task.FromResult(FileStoreCheck.Success("The file store is in use (no probe available)."));

    /// <summary>Stores the stream under the key, replacing any existing blob. Atomic: readers see the old or the new content, never a partial one. Two concurrent writes to the same key are not guaranteed to both succeed (the content never mixes).</summary>
    Task WriteAsync(string key, Stream content, CancellationToken ct);

    /// <summary>Opens a read stream over <paramref name="length"/> bytes (or to the end) starting at <paramref name="offset"/>. Throws <see cref="FileNotFoundException"/> when the key is absent.</summary>
    Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct);

    /// <summary>The blob size in bytes, or null when the key is absent.</summary>
    Task<long?> GetLengthAsync(string key, CancellationToken ct);

    /// <summary>Deletes the blob. Idempotent: an absent key is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Deletes every blob whose key starts with the prefix.</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken ct);

    /// <summary>The keys that start with the prefix (an empty prefix lists everything), in no particular order.</summary>
    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct);

    /// <summary>Writes the concatenation of the source blobs, in list order, as the target. Throws <see cref="FileNotFoundException"/> when a source is absent.</summary>
    Task ConcatAsync(IReadOnlyList<string> sourceKeys, string targetKey, CancellationToken ct);
}

/// <summary>How usable a store is: <see cref="Ok"/>, <see cref="Warning"/> (works, but something needs attention) or <see cref="Error"/>.</summary>
public enum FileStoreStatus
{
    Ok,
    Warning,
    Error,
}

/// <summary>The outcome of <see cref="IFileStore.CheckAsync"/>: a <see cref="Status"/> and a message that is safe to show (no secrets).</summary>
public sealed record FileStoreCheck(FileStoreStatus Status, string Message)
{
    public bool IsOk => Status == FileStoreStatus.Ok;

    public static FileStoreCheck Success(string message) => new(FileStoreStatus.Ok, message);

    public static FileStoreCheck Warn(string message) => new(FileStoreStatus.Warning, message);

    public static FileStoreCheck Failure(string message) => new(FileStoreStatus.Error, message);
}
