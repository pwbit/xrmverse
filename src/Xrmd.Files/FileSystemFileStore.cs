using System.Runtime.CompilerServices;

namespace Xrmd.Files;

/// <summary>
/// <see cref="IFileStore"/> on a directory. Writes go to a temporary file beside the target and are renamed into place,
/// so a reader never observes a partial blob. The root is assumed to be operator-controlled (symlinks inside it are not handled).
/// </summary>
public sealed class FileSystemFileStore : IFileStore
{
    // Suffix of in-flight writes; never listed. A final key ending in it is refused.
    private const string TempSuffix = ".xrmd-tmp";

    private readonly string _root;

    // Directory creation + temp-file open (shared) versus empty-directory pruning (exclusive): within this process a
    // writer's freshly created directory can never be pruned before its temp file makes it non-empty. The retry in
    // OpenTemp still covers another store instance or process pruning the same root.
    private readonly ReaderWriterLockSlim _dirLock = new(LockRecursionPolicy.NoRecursion);

    public FileSystemFileStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public Task<FileStoreCheck> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var label = $"Local directory '{_root}'";
        if (File.Exists(_root))
            return Task.FromResult(FileStoreCheck.Failure($"{label} is a file, not a directory."));
        // The store creates a missing root on the first write, so a missing root is fine as long as that can work:
        // probe the nearest existing ancestor instead.
        var missing = !Directory.Exists(_root);
        var probeDir = _root;
        while (!Directory.Exists(probeDir))
        {
            var parent = Path.GetDirectoryName(probeDir);
            if (parent is null)
                return Task.FromResult(FileStoreCheck.Failure($"{label} cannot be created: no parent directory exists."));
            if (File.Exists(parent))
                return Task.FromResult(FileStoreCheck.Failure($"{label} cannot be created: '{parent}' is a file."));
            probeDir = parent;
        }
        try
        {
            // There is no portable way to ask "may this process write here" without trying, so create a temp file that the
            // OS deletes on close; it carries the in-flight suffix, so a listing never shows it.
            var probe = Path.Combine(probeDir, Guid.NewGuid().ToString("N") + TempSuffix);
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return Task.FromResult(FileStoreCheck.Failure(missing
                ? $"{label} does not exist and cannot be created: '{probeDir}' is not writable ({e.GetType().Name})."
                : $"{label} is not writable: {e.GetType().Name}."));
        }
        return Task.FromResult(missing
            ? FileStoreCheck.Warn($"{label} does not exist yet; it will be created on the first write.")
            : FileStoreCheck.Success($"{label} exists and is writable."));
    }

    public async Task WriteAsync(string key, Stream content, CancellationToken ct)
    {
        var path = ResolveKey(key);
        ArgumentNullException.ThrowIfNull(content);
        await WriteAtomicAsync(path, async (target, token) => await content.CopyToAsync(target, 81920, token).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    public Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct)
    {
        var path = ResolveKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length is < 0) throw new ArgumentOutOfRangeException(nameof(length));
        ct.ThrowIfCancellationRequested();
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (DirectoryNotFoundException)
        {
            throw new FileNotFoundException($"File '{key}' does not exist.", key);
        }
        catch (FileNotFoundException)
        {
            throw new FileNotFoundException($"File '{key}' does not exist.", key);
        }
        catch (UnauthorizedAccessException) when (Directory.Exists(path))
        {
            throw new FileNotFoundException($"File '{key}' does not exist.", key);
        }
        stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
        var remaining = Math.Max(0, stream.Length - stream.Position);
        return Task.FromResult<Stream>(new SliceStream(stream, length is { } l ? Math.Min(l, remaining) : remaining));
    }

    public Task<long?> GetLengthAsync(string key, CancellationToken ct)
    {
        var info = new FileInfo(ResolveKey(key));
        return Task.FromResult<long?>(info.Exists ? info.Length : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = ResolveKey(key);
        if (File.Exists(path)) File.Delete(path); // absent key (or directory): nothing to do
        return Task.CompletedTask;
    }

    public Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        FileKeys.ValidatePrefix(prefix);
        if (prefix.Length == 0) throw new ArgumentException("DeletePrefix needs a non-empty prefix.", nameof(prefix));
        foreach (var key in Enumerate(prefix))
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(ResolveKey(key));
        }
        PruneEmptyDirectories(DirectoryOf(prefix));
        // The prefix itself may name a directory (e.g. 'org/_upload/s1/') that is now empty.
        var self = prefix.EndsWith('/') ? DirectoryOf(prefix) : null;
        if (self != null && self != _root) TryDeleteEmpty(self);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        FileKeys.ValidatePrefix(prefix);
        foreach (var key in Enumerate(prefix))
        {
            ct.ThrowIfCancellationRequested();
            yield return key;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task ConcatAsync(IReadOnlyList<string> sourceKeys, string targetKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceKeys);
        if (sourceKeys.Count == 0) throw new ArgumentException("ConcatAsync needs at least one source key.", nameof(sourceKeys));
        var sources = sourceKeys.Select(ResolveKey).ToList();
        var target = ResolveKey(targetKey);
        await WriteAtomicAsync(target, async (output, token) =>
        {
            foreach (var (source, index) in sources.Select((s, i) => (s, i)))
            {
                FileStream input;
                try
                {
                    input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    throw new FileNotFoundException($"File '{sourceKeys[index]}' does not exist.", sourceKeys[index]);
                }
                await using (input.ConfigureAwait(false))
                    await input.CopyToAsync(output, 81920, token).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task WriteAtomicAsync(string path, Func<Stream, CancellationToken, Task> fill, CancellationToken ct)
    {
        var temp = $"{path}.{Guid.NewGuid():N}{TempSuffix}";
        try
        {
            var stream = OpenTemp(path, temp);
            await using (stream.ConfigureAwait(false))
            {
                await fill(stream, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort; never replace the original exception */ }
            throw;
        }
    }

    // DeletePrefix prunes empty directories and may remove the one just created: create and open under the shared
    // directory lock, and retry a few times for pruning done outside this instance.
    private FileStream OpenTemp(string path, string temp)
    {
        for (var attempt = 0; ; attempt++)
        {
            _dirLock.EnterReadLock();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            }
            catch (DirectoryNotFoundException) when (attempt < 5) { }
            finally
            {
                _dirLock.ExitReadLock();
            }
        }
    }

    /// <summary>Deletes in-flight write leftovers (crashed writers) not modified for <paramref name="olderThan"/>; returns how many. Callers must pick a threshold well above any plausible write duration (hours, not seconds), or a slow in-flight write could be deleted.</summary>
    public Task<int> PurgeStaleTempFilesAsync(TimeSpan olderThan, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var purged = 0;
        if (!Directory.Exists(_root)) return Task.FromResult(0);
        foreach (var file in Directory.EnumerateFiles(_root, "*" + TempSuffix, SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                File.Delete(file);
                purged++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* in use or gone: next sweep */ }
        }
        return Task.FromResult(purged);
    }

    /// <summary>True when the full path lies strictly inside the root (a sibling such as <c>root-evil</c> is outside).</summary>
    internal static bool IsUnderRoot(string root, string fullPath) =>
        fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private void TryDeleteEmpty(string directory)
    {
        _dirLock.EnterWriteLock();
        try { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* concurrent writer; leave it */ }
        finally { _dirLock.ExitWriteLock(); }
    }

    private IEnumerable<string> Enumerate(string prefix)
    {
        var directory = DirectoryOf(prefix);
        if (!Directory.Exists(directory)) yield break;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(TempSuffix, StringComparison.Ordinal)) continue;
            var key = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (key.StartsWith(prefix, StringComparison.Ordinal)) yield return key;
        }
    }

    // The deepest existing-by-construction directory that can hold keys with the prefix.
    private string DirectoryOf(string prefix)
    {
        var slash = prefix.LastIndexOf('/');
        return slash < 0 ? _root : Path.GetFullPath(Path.Combine(_root, prefix[..slash]));
    }

    private void PruneEmptyDirectories(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var sub in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            TryDeleteEmpty(sub);
        }
    }

    private string ResolveKey(string key)
    {
        FileKeys.ValidateKey(key);
        if (key.EndsWith(TempSuffix, StringComparison.Ordinal))
            throw new ArgumentException($"A file key must not end with '{TempSuffix}'.", nameof(key));
        var path = Path.GetFullPath(Path.Combine(_root, key));
        if (!IsUnderRoot(_root, path))
            throw new ArgumentException($"File key '{key}' resolves outside the store root.", nameof(key));
        return path;
    }

    /// <summary>Exposes at most <c>count</c> bytes of the inner stream from its current position.</summary>
    private sealed class SliceStream(Stream inner, long count) : Stream
    {
        private long _remaining = count;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0) return 0;
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
