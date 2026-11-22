using System.IO.Compression;
using System.Runtime.CompilerServices;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Records;

/// <summary>Reads the records of the chunks written by <see cref="ChunkWriter"/>, in order, one at a time.</summary>
public static class ChunkReader
{
    public static async IAsyncEnumerable<MigrationRecord> ReadAsync(PackageReader reader, IEnumerable<string> chunks, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var chunk in chunks)
        {
            await using var entry = reader.OpenEntry(chunk);
            await using var gzip = new GZipStream(entry, CompressionMode.Decompress);
            using var text = new StreamReader(gzip);
            while (await text.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;
                yield return RecordLineCodec.Read(line);
            }
        }
    }
}
