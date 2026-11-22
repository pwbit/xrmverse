using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Xrmd.Migration.Packaging;

/// <summary>
/// Writes a package as a folder, so that an interrupted export can resume (<see cref="OpenForResume"/>), then
/// <see cref="Pack"/> zips the finished folder.
/// <para>
/// Folder layout: <c>header.json</c> (clear), <c>manifest</c> (encrypted manifest, written by <see cref="Complete"/>),
/// and every other entry under its opaque name <c>e/&lt;hex&gt;</c>. An entry is written to <c>&lt;name&gt;.partial</c>;
/// <see cref="PackageEntryStream.Commit"/> renames the blob into place and then writes an encrypted record of its path,
/// hash and size to <c>&lt;name&gt;.meta</c>: only a blob with its record counts on resume. A stream disposed without
/// <c>Commit</c> (an exception while writing) is discarded. Writers of different entries may run in parallel.
/// </para>
/// </summary>
public sealed class PackageWriter : IDisposable
{
    public const string ManifestFile = "manifest";
    internal const string ManifestPath = "manifest.json";
    private const string ProgressPath = "progress.json";
    internal const string Partial = ".partial";
    private const string Meta = ".meta";

    private readonly string _folder;
    private readonly PackageKeys _keys;
    private readonly Dictionary<string, PackageEntry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<PackageEntryStream> _open = [];
    private readonly Lock _lock = new();
    private bool _disposed;

    private PackageWriter(string folder, PackageKeys keys)
    {
        _folder = folder;
        _keys = keys;
    }

    /// <summary>Starts a new package in an empty or missing <paramref name="folder"/>.</summary>
    /// <exception cref="IOException">The folder exists and is not empty.</exception>
    public static PackageWriter Create(string folder, string passphrase)
    {
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException($"The folder '{folder}' is not empty: resume an export there, or choose another folder.");
        var salt = RandomNumberGenerator.GetBytes(PackageKeys.SaltLength);
        var keys = PackageKeys.Derive(passphrase, salt);
        Directory.CreateDirectory(Path.Combine(folder, "e"));
        File.WriteAllBytes(Path.Combine(folder, PackageHeader.FileName), JsonSerializer.SerializeToUtf8Bytes(PackageHeader.For(salt, keys), PackageJson.Options));
        return new PackageWriter(folder, keys);
    }

    /// <summary>Reopens an export that stopped: completed entries are kept, half-written ones discarded.</summary>
    /// <exception cref="PackagePassphraseException">The passphrase is not the one the export started with.</exception>
    /// <exception cref="PackageFormatException">The folder holds no package header, or the export is already complete.</exception>
    public static PackageWriter OpenForResume(string folder, string passphrase)
    {
        var headerFile = Path.Combine(folder, PackageHeader.FileName);
        if (!File.Exists(headerFile)) throw new PackageFormatException($"The folder '{folder}' holds no package to resume.");
        if (File.Exists(Path.Combine(folder, ManifestFile))) throw new PackageFormatException("This export is already complete.");
        var keys = PackageHeader.Parse(File.ReadAllBytes(headerFile)).Open(passphrase);
        var writer = new PackageWriter(folder, keys);
        var dir = Path.Combine(folder, "e");
        Directory.CreateDirectory(dir);
        foreach (var partial in Directory.GetFiles(dir, "*" + Partial)) File.Delete(partial);
        foreach (var meta in Directory.GetFiles(dir, "*" + Meta))
        {
            var blob = meta[..^Meta.Length];
            if (!File.Exists(blob)) { File.Delete(meta); continue; }
            var entry = JsonSerializer.Deserialize<PackageEntry>(writer.ReadInternal(meta, "meta:" + Path.GetFileName(blob)), PackageJson.Options)!;
            writer._entries[entry.Path] = entry;
        }
        // A blob without its record was being committed when the export stopped: it does not count.
        var progress = writer.BlobPath(ProgressPath);
        foreach (var blob in Directory.GetFiles(dir))
            if (!blob.EndsWith(Meta, StringComparison.Ordinal) && blob != progress && !File.Exists(blob + Meta)) File.Delete(blob);
        return writer;
    }

    /// <summary>The entries completed so far.</summary>
    public IReadOnlyCollection<PackageEntry> Entries
    {
        get { lock (_lock) return [.. _entries.Values]; }
    }

    public bool HasEntry(string logicalPath)
    {
        lock (_lock) return _entries.ContainsKey(logicalPath);
    }

    /// <summary>
    /// A write-only stream for the entry at <paramref name="logicalPath"/> (replacing it if it exists). The entry counts
    /// once <see cref="PackageEntryStream.Commit"/> is called; disposing the stream without it discards the entry.
    /// </summary>
    public PackageEntryStream CreateEntry(string logicalPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (logicalPath is ManifestPath or ProgressPath || logicalPath.StartsWith("meta:", StringComparison.Ordinal))
            throw new ArgumentException($"'{logicalPath}' is reserved.", nameof(logicalPath));
        var stream = new PackageEntryStream(this, logicalPath, BlobPath(logicalPath));
        lock (_lock) _open.Add(stream);
        return stream;
    }

    public void WriteProgress<T>(T progress) => WriteInternal(BlobPath(ProgressPath), ProgressPath, JsonSerializer.SerializeToUtf8Bytes(progress, PackageJson.Options));

    public T? ReadProgress<T>()
    {
        var file = BlobPath(ProgressPath);
        return File.Exists(file) ? JsonSerializer.Deserialize<T>(ReadInternal(file, ProgressPath), PackageJson.Options) : default;
    }

    /// <summary>Writes the manifest with the completed entries (sorted by path) and removes the export's bookkeeping.</summary>
    public void Complete(PackageManifest manifest)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            if (_open.Count > 0) throw new InvalidOperationException("An entry is still being written.");
            var entries = _entries.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
            WriteInternal(Path.Combine(_folder, ManifestFile), ManifestPath,
                JsonSerializer.SerializeToUtf8Bytes(manifest with { FormatVersion = PackageHeader.CurrentVersion, Entries = entries }, PackageJson.Options));
        }
        File.Delete(BlobPath(ProgressPath));
        foreach (var meta in Directory.GetFiles(Path.Combine(_folder, "e"), "*" + Meta)) File.Delete(meta);
    }

    /// <summary>The time of every packed entry (within the DOS range in any time zone).</summary>
    private static readonly DateTimeOffset PackTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Zips a completed package folder into <paramref name="packageFile"/>, without compression.</summary>
    /// <exception cref="PackageFormatException">The export in the folder is not complete.</exception>
    public static void Pack(string folder, string packageFile)
    {
        if (!File.Exists(Path.Combine(folder, ManifestFile)) || !File.Exists(Path.Combine(folder, PackageHeader.FileName)))
            throw new PackageFormatException($"The export in '{folder}' is not complete: resume it before packing.");
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        if (files.Any(f => f.EndsWith(Partial, StringComparison.Ordinal) || f.EndsWith(Meta, StringComparison.Ordinal)))
            throw new PackageFormatException($"The export in '{folder}' is not complete: it still holds unfinished entries.");
        using var output = new FileStream(packageFile, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var entry = zip.CreateEntry(Path.GetRelativePath(folder, file).Replace('\\', '/'), CompressionLevel.NoCompression);
            // A fixed time: the same export always packs to the same bytes, so a resumed upload finds it by its checksum.
            entry.LastWriteTime = PackTime;
            using var source = File.OpenRead(file);
            using var target = entry.Open();
            source.CopyTo(target);
        }
    }

    /// <summary>Stops the writer. Entries whose streams are still open are discarded (resume rewrites them).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<PackageEntryStream> open;
        lock (_lock) open = [.. _open];
        foreach (var stream in open) stream.Abandon();
    }

    internal PackageKeys Keys => _keys;

    private string BlobPath(string logicalPath) => Path.Combine(_folder, _keys.EntryName(logicalPath));

    private void WriteInternal(string file, string logicalPath, byte[] content)
    {
        var temp = file + Partial;
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
        using (var cipher = EntryCipher.OpenWrite(fs, _keys, logicalPath))
            cipher.Write(content);
        File.Move(temp, file, overwrite: true);
    }

    private byte[] ReadInternal(string file, string logicalPath)
    {
        using var fs = File.OpenRead(file);
        using var cipher = EntryCipher.OpenRead(fs, _keys, logicalPath);
        using var buffer = new MemoryStream();
        cipher.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal void Completed(PackageEntryStream stream, PackageEntry entry, string blob, string partial)
    {
        // Record last: a crash at any point leaves either the old entry uncounted or the new one without its record,
        // never a record next to content it does not describe.
        File.Delete(blob + Meta);
        File.Move(partial, blob, overwrite: true);
        WriteInternal(blob + Meta, "meta:" + Path.GetFileName(blob), JsonSerializer.SerializeToUtf8Bytes(entry, PackageJson.Options));
        lock (_lock)
        {
            _entries[entry.Path] = entry;
            _open.Remove(stream);
        }
    }

    internal void Forget(PackageEntryStream stream)
    {
        lock (_lock) _open.Remove(stream);
    }
}

/// <summary>The stream of one entry being written: call <see cref="Commit"/> when the entry is complete.</summary>
public sealed class PackageEntryStream : Stream
{
    private readonly PackageWriter _writer;
    private readonly string _path;
    private readonly string _blob;
    private readonly string _partial;
    private readonly FileStream _file;
    private readonly Stream _cipher;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytes;
    private bool _done;

    internal PackageEntryStream(PackageWriter writer, string path, string blob)
    {
        _writer = writer;
        _path = path;
        _blob = blob;
        _partial = blob + PackageWriter.Partial;
        _file = new FileStream(_partial, FileMode.Create, FileAccess.Write);
        _cipher = EntryCipher.OpenWrite(_file, writer.Keys, path);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_done;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _bytes; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_done, this);
        _hash.AppendData(buffer);
        _cipher.Write(buffer);
        _bytes += buffer.Length;
    }

    /// <summary>Seals the entry and makes it count. The stream cannot be written after this.</summary>
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_done, this);
        _done = true;
        _cipher.Dispose(); // writes the last segment and closes the file
        var entry = new PackageEntry(_path, Convert.ToHexStringLower(_hash.GetHashAndReset()), _bytes);
        _hash.Dispose();
        _writer.Completed(this, entry, _blob, _partial);
    }

    /// <summary>Closes the file without committing: the entry does not count and its partial file is removed.</summary>
    internal void Abandon()
    {
        if (_done) return;
        _done = true;
        _file.Dispose();
        _hash.Dispose();
        try { File.Delete(_partial); } catch (IOException) { /* resume deletes it */ }
        _writer.Forget(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Abandon();
        base.Dispose(disposing);
    }
}
