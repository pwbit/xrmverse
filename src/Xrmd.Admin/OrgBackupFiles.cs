using System.Data.Common;
using System.Formats.Tar;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xrmd.Core.Errors;
using Xrmd.Files;

namespace Xrmd.Admin;

/// <summary>A backup file written by <see cref="IOrgProvisioner.BackupAsync"/>.</summary>
/// <param name="File">The database backup: a <c>.bak</c>, a path on the SQL Server host; or a <c>.dump</c> of <c>pg_dump</c>, a path
/// in xrmd's own backup directory (plan 2.7b, ruling Q7).</param>
/// <param name="SizeBytes">Its size.</param>
/// <param name="FilesFile">The <c>.files.tar</c> written with it (ruling F9): its path under <c>Admin:FilesBackupDirectory</c>,
/// as xrmd sees that directory (with SQL Server, the same files as the SQL Server's backup directory, mounted in both containers).</param>
/// <param name="FilesSizeBytes">The size of the archive.</param>
/// <param name="FilesCount">The blobs in the archive.</param>
public sealed record BackupResult(string File, long SizeBytes, string? FilesFile = null, long FilesSizeBytes = 0, int FilesCount = 0);

/// <summary>A <c>fileattachment</c> row of a restored database whose blob is not in the file store.</summary>
public sealed record MissingFile(Guid FileAttachmentId, string? Entity, string? Attribute, Guid? RecordId);

/// <summary>
/// What a restore leaves to report. <see cref="MissingFiles"/> lists (at most <see cref="OrgBackupFiles.MaxReportedMissing"/> of)
/// the files the restored database references and the store does not hold; <see cref="MissingCount"/> counts all of them.
/// </summary>
public sealed record RestoreResult(IReadOnlyList<MissingFile> MissingFiles, int MissingCount)
{
    public static RestoreResult Complete { get; } = new([], 0);
}

/// <summary>
/// The files side of an organization backup (ruling F9), the same on both engines (plan 2.7b, Task 9; moved here unchanged from
/// <see cref="SqlServerOrgProvisioner"/>): the <c>.files.tar</c> beside the database backup, its checks, the restore of its blobs
/// under the target's prefix, and the check of the restored database's <c>fileattachment</c> rows against the store.
/// </summary>
public sealed class OrgBackupFiles(IFileStore fileStore, FileColumnService fileColumns, ILogger logger, string filesBackupDirectory)
{
    /// <summary>The most missing files a <see cref="RestoreResult"/> lists one by one.</summary>
    public const int MaxReportedMissing = 1000;

    public const string ArchiveExtension = ".files.tar";
    private const string UploadFolder = "_upload";

    /// <summary>
    /// The archive that goes with a backup file: same name, <c>.files.tar</c> instead of <paramref name="extension"/> (<c>.bak</c>
    /// or <c>.dump</c>), in the files backup directory.
    /// </summary>
    public string ArchivePathOf(string backupFile, string extension)
    {
        var name = backupFile.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = name[..^extension.Length];
        return Path.Combine(filesBackupDirectory, name + ArchiveExtension);
    }

    /// <summary>
    /// Streams every blob under <c>{database}/</c> (but <c>_upload/</c>) of the file store into a tar written to a temporary file next
    /// to <paramref name="archive"/>, then renames it: the archive is whole or absent.
    /// </summary>
    public async Task<(long Bytes, int Count)> WriteArchiveAsync(string database, string archive, string backupFile, CancellationToken ct)
    {
        var temp = archive + ".tmp";
        var count = 0;
        var moved = false;
        try
        {
            Directory.CreateDirectory(filesBackupDirectory);
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await using (var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: false))
            {
                var prefix = database + "/";
                var uploads = prefix + UploadFolder + "/";
                await foreach (var key in fileStore.ListAsync(prefix, ct))
                {
                    if (key.StartsWith(uploads, StringComparison.Ordinal)) continue;
                    var length = await fileStore.GetLengthAsync(key, ct);
                    if (length is null) continue; // deleted since the listing
                    Stream content;
                    try
                    {
                        content = await fileStore.OpenReadAsync(key, 0, null, ct);
                    }
                    catch (FileNotFoundException)
                    {
                        continue; // deleted between the length and the open: skipped like the case above (a restore reports it)
                    }

                    await using var _ = content;
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, key) { DataStream = new KnownLengthStream(content, length.Value) }, ct);
                    count++;
                }
            }

            File.Move(temp, archive, overwrite: true);
            moved = true;
            return (new FileInfo(archive).Length, count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(archive);
            throw new XrmdFaultException(ErrorCodes.Unexpected,
                $"The SQL backup '{backupFile}' was written but its files archive '{archive}' could not be: {ex.Message} " +
                $"Restoring that {Path.GetExtension(backupFile)} needs its archive (or files=false); take the backup again.", StatusCodes.Status500InternalServerError);
        }
        finally
        {
            if (!moved) TryDelete(temp);
        }
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* best effort: the explicit error is the one reported */ }
        catch (UnauthorizedAccessException) { /* idem */ }
    }

    /// <summary>A forward-only stream that reports a known <see cref="Length"/> (a tar header needs the size before the data).</summary>
    private sealed class KnownLengthStream(Stream inner, long length) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Account(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Account(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Account(await inner.ReadAsync(buffer, ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Account(int n)
        {
            _read += n;
            if (n == 0 && _read != length)
                throw new IOException($"The blob changed while it was archived: expected {length} bytes, read {_read}.");
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Opens the archive of a restore (held, shared read, from validation to the last blob, so it cannot change in between) and
    /// validates it: 404 when it is missing, 400 when it is unusable. Returns the open stream (at position 0) and the source
    /// database of its entries (null for an archive without blobs).
    /// </summary>
    public static async Task<(FileStream Stream, string? Source)> OpenArchiveAsync(string archive, string backupFile, CancellationToken ct)
    {
        if (!File.Exists(archive))
            throw OrgProvisioning.NotFound($"The files archive '{archive}' that goes with '{backupFile}' does not exist. " +
                                           "Restore with files=false (--no-files) to restore the database without its files.");
        // Unbuffered: the second pass re-reads the file instead of replaying a buffer of the first.
        var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var source = await ValidateArchiveAsync(stream, archive, ct);
            stream.Position = 0;
            return (stream, source);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The files phase of a restore whose database is restored: waits for the queued blob deletions, runs
    /// <paramref name="beforeFilesRestore"/> (a test seam), then replaces the blobs (<see cref="RestoreFilesAsync"/>). Not cancelled
    /// halfway, which would leave the files incomplete.
    /// </summary>
    public async Task RestoreBlobsAsync(string database, Stream archiveStream, string archive, string? source, string backupFile,
        Func<CancellationToken, Task>? beforeFilesRestore)
    {
        // A blob deletion still queued (a file released before the restore) must not delete a blob the archive restores.
        if (!await fileColumns.WhenDeletionsIdleAsync(TimeSpan.FromSeconds(30), CancellationToken.None))
            logger.LogWarning("Restore of {Database}: queued file deletions did not finish within 30 seconds; a restored blob " +
                              "they name may be deleted after the restore.", database);
        if (beforeFilesRestore is { } hook) await hook(CancellationToken.None);
        await RestoreFilesAsync(database, archiveStream, archive, source, backupFile);
    }

    /// <summary>
    /// Compares the <c>fileattachment</c> rows of <paramref name="database"/> (read on <paramref name="connection"/>, open on it; the
    /// caller checked that the table exists) with the blobs <c>{database}/{id}</c> of the store, listed once (one listing of the
    /// prefix, not one read per row). Missing ones are logged as a warning and returned (the first <see cref="MaxReportedMissing"/>,
    /// with their count).
    /// </summary>
    public async Task<RestoreResult> CheckFilesAsync(string database, DbConnection connection)
    {
        var missing = new List<MissingFile>();
        var count = 0;
        var prefix = database + "/";
        var uploads = prefix + UploadFolder + "/";
        var blobs = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var key in fileStore.ListAsync(prefix, CancellationToken.None))
            if (!key.StartsWith(uploads, StringComparison.Ordinal)) blobs.Add(key);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT fileattachmentid, objecttypecode, regardingfieldname, objectid FROM data.fileattachment";
        cmd.CommandTimeout = 0;
        await using var rd = await cmd.ExecuteReaderAsync(CancellationToken.None);
        while (await rd.ReadAsync(CancellationToken.None))
        {
            var id = rd.GetGuid(0);
            if (blobs.Contains(prefix + id.ToString("D"))) continue;
            count++;
            if (missing.Count < MaxReportedMissing)
                missing.Add(new MissingFile(id, rd.IsDBNull(1) ? null : Convert.ToString(rd.GetValue(1), System.Globalization.CultureInfo.InvariantCulture),
                    rd.IsDBNull(2) ? null : rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetGuid(3)));
        }

        if (count > 0)
            logger.LogWarning("Restore of {Database}: {Count} file(s) referenced by the restored database are not in the file store (e.g. {First}). " +
                              "They were deleted or replaced after the backup's database snapshot, or the restore ran with files=false.",
                database, count, missing[0].FileAttachmentId);
        return count == 0 ? RestoreResult.Complete : new RestoreResult(missing, count);
    }

    /// <summary>
    /// The one check of an archive entry, run in the validation pass and again in the restore pass: a regular file whose name is a
    /// valid key <c>{source}/{blob}</c>, not under <c>_upload/</c>, of the same database as the entries before it. <paramref name="source"/>
    /// is set by the first entry when null.
    /// </summary>
    private static void CheckEntry(TarEntry entry, string archive, ref string? source)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            throw XrmdFaultException.InvalidArgument($"The files archive '{archive}' has an entry '{entry.Name}' that is not a regular file.");
        try
        {
            FileKeys.ValidateKey(entry.Name);
        }
        catch (ArgumentException ex)
        {
            throw XrmdFaultException.InvalidArgument($"The files archive '{archive}' is not usable: {ex.Message}");
        }

        var segments = entry.Name.Split('/');
        if (segments.Length < 2 || segments[1] == UploadFolder)
            throw XrmdFaultException.InvalidArgument($"The files archive '{archive}' has an entry '{entry.Name}' that is not a blob of an organization.");
        source ??= segments[0];
        if (!entry.Name.StartsWith(source + "/", StringComparison.Ordinal))
            throw XrmdFaultException.InvalidArgument($"The files archive '{archive}' mixes databases ('{source}' and '{segments[0]}').");
    }

    /// <summary>Reads the archive's headers through <see cref="CheckEntry"/>. Returns the source database, or null for an archive without blobs.</summary>
    private static async Task<string?> ValidateArchiveAsync(Stream input, string archive, CancellationToken ct)
    {
        string? source = null;
        try
        {
            await using var reader = new TarReader(input, leaveOpen: true);
            while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry) CheckEntry(entry, archive, ref source);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
        {
            throw XrmdFaultException.InvalidArgument($"The files archive '{archive}' is not a usable tar: {ex.Message}");
        }

        return source;
    }

    /// <summary>
    /// Replaces the blobs under <c>{database}/</c> with the archive's, its entries moved from <paramref name="source"/> to the target
    /// prefix. Every entry goes through <see cref="CheckEntry"/> again.
    /// </summary>
    private async Task RestoreFilesAsync(string database, Stream input, string archive, string? source, string backupFile)
    {
        var ct = CancellationToken.None;
        try
        {
            await fileStore.DeletePrefixAsync(database + "/", ct);
            if (source is null) return;
            await using var reader = new TarReader(input, leaveOpen: true);
            var expected = source;
            while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                CheckEntry(entry, archive, ref expected);
                // An empty blob has no data stream in the tar reader.
                await using var data = entry.DataStream ?? new MemoryStream([], writable: false);
                await fileStore.WriteAsync(database + entry.Name[source.Length..], data, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore of {Database}: the database was restored from {File} but its files could not be (database restored, files incomplete)", database, backupFile);
            throw new XrmdFaultException(ErrorCodes.Unexpected,
                $"The database '{database}' was restored from '{backupFile}' but restoring its files from '{archive}' failed: {ex.Message} " +
                "The files are incomplete; repeat the restore with overwrite to redo both.", StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// True when <paramref name="file"/> is a file directly in <paramref name="directory"/>: both are compared as text (with SQL
    /// Server the file is on the SQL Server host, which this process may not see), with no <c>.</c>/<c>..</c> or empty segments.
    /// </summary>
    public static bool IsInDirectory(string directory, string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Contains('\0')) return false;
        var path = file.Replace('\\', '/');
        var segments = path.Split('/');
        if (segments.Skip(1).Any(s => s.Length == 0) || segments.Any(s => s is "." or "..")) return false;
        var slash = path.LastIndexOf('/');
        if (slash < 0 || slash == path.Length - 1) return false;
        var normalized = directory.Replace('\\', '/').TrimEnd('/');
        var windows = directory.Contains('\\') || directory.Contains(':');
        return string.Equals(path[..slash], normalized, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
