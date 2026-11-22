using System.IO.Compression;
using System.Text;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Records;

/// <summary>
/// Writes the records of one table (or the pairs of one relationship) as gzip-compressed JSON lines, in package entries
/// of at most <c>recordsPerChunk</c> records: <c>&lt;prefix&gt;/00001.jsonl.gz</c>, <c>00002</c>, … A table without
/// records has no chunk. A full chunk is committed when the next one starts; call <see cref="Complete"/> after the last
/// record: disposing without it (an exception while exporting) discards the chunk being written. A resumed export passes
/// <c>firstChunkNumber</c> to continue the numbering after the chunks it keeps.
/// </summary>
public sealed class ChunkWriter(PackageWriter writer, string prefix, int recordsPerChunk = 5000, int firstChunkNumber = 1) : IDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private readonly List<string> _chunks = [];
    private PackageEntryStream? _entry;
    private GZipStream? _gzip;
    private int _inChunk;

    public IReadOnlyList<string> Chunks => _chunks;

    public long Count { get; private set; }

    public void Add(MigrationRecord record)
    {
        if (_gzip is null || _inChunk == recordsPerChunk) Next();
        _gzip!.Write(Encoding.UTF8.GetBytes(RecordLineCodec.Write(record)));
        _gzip.Write(NewLine);
        _inChunk++;
        Count++;
    }

    /// <summary>Commits the chunk being written.</summary>
    public void Complete() => Close(commit: true);

    private void Next()
    {
        Close(commit: true);
        var path = $"{prefix}/{firstChunkNumber + _chunks.Count:D5}.jsonl.gz";
        _entry = writer.CreateEntry(path);
        _gzip = new GZipStream(_entry, CompressionLevel.Fastest, leaveOpen: true);
        _chunks.Add(path);
        _inChunk = 0;
    }

    private void Close(bool commit)
    {
        _gzip?.Dispose();
        if (commit) _entry?.Commit();
        _entry?.Dispose();
        _gzip = null;
        _entry = null;
    }

    public void Dispose() => Close(commit: false);
}
