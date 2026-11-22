using FluentAssertions;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Migration.Tests;

public sealed class ChunkTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static MigrationRecord Row(int i) =>
        new(new Guid(i, 0, 0, new byte[8]), new Dictionary<string, MigrationValue> { ["name"] = new StringValue($"Row {i}") });

    [Fact]
    public async Task Splits_at_records_per_chunk()
    {
        var folder = Path.Combine(_root, "pkg");
        IReadOnlyList<string> chunks;
        using (var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase))
        {
            using (var chunk = new ChunkWriter(writer, "data/account"))
            {
                for (var i = 0; i < 12_001; i++) chunk.Add(Row(i));
                chunk.Complete();
                chunk.Count.Should().Be(12_001);
                chunks = chunk.Chunks;
            }
            writer.Complete(new PackageManifest());
        }
        chunks.Should().Equal("data/account/00001.jsonl.gz", "data/account/00002.jsonl.gz", "data/account/00003.jsonl.gz");

        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var names = new List<string>();
        await foreach (var record in ChunkReader.ReadAsync(reader, chunks, CancellationToken.None))
            names.Add(((StringValue)record.Values["name"]).Value);
        names.Should().HaveCount(12_001);
        names[0].Should().Be("Row 0");
        names[^1].Should().Be("Row 12000");
    }

    [Fact]
    public void Empty_table_has_no_chunks()
    {
        using var writer = PackageWriter.Create(Path.Combine(_root, "pkg"), SyntheticPackage.Passphrase);
        using var chunk = new ChunkWriter(writer, "data/account");
        chunk.Complete();
        chunk.Chunks.Should().BeEmpty();
        chunk.Count.Should().Be(0);
        writer.Entries.Should().BeEmpty();
    }
}
