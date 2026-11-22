using FluentAssertions;

namespace Xrmd.Files.Tests;

/// <summary><see cref="FileStoreReadStream"/>: a seekable read-only view of a blob, read in 1 MiB ranges (migration M3, task 3).</summary>
public sealed class FileStoreReadStreamTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-frs-").FullName;
    private readonly FileSystemFileStore _store;

    public FileStoreReadStreamTests() => _store = new FileSystemFileStore(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<byte[]> PutAsync(string key, int size)
    {
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        await _store.WriteAsync(key, new MemoryStream(bytes), default);
        return bytes;
    }

    [Fact]
    public async Task Reads_across_block_boundaries()
    {
        var bytes = await PutAsync("a/blob", 3 * 1024 * 1024 + 512 * 1024);
        await using var stream = await FileStoreReadStream.OpenAsync(_store, "a/blob", default);

        stream.Seek(1024 * 1024 - 10, SeekOrigin.Begin);
        var buffer = new byte[20];
        stream.ReadExactly(buffer);
        buffer.Should().Equal(bytes.AsSpan(1024 * 1024 - 10, 20).ToArray());

        stream.Position = 0;
        var all = new MemoryStream();
        await stream.CopyToAsync(all);
        all.ToArray().Should().Equal(bytes);
    }

    [Fact]
    public async Task Seek_end_and_length()
    {
        var bytes = await PutAsync("b/blob", 5000);
        await using var stream = await FileStoreReadStream.OpenAsync(_store, "b/blob", default);

        stream.CanSeek.Should().BeTrue();
        stream.CanWrite.Should().BeFalse();
        stream.Length.Should().Be(5000);
        stream.Seek(-4, SeekOrigin.End).Should().Be(4996);
        var tail = new byte[10];
        stream.Read(tail, 0, 10).Should().Be(4);
        tail[..4].Should().Equal(bytes[^4..]);
        stream.Read(tail, 0, 10).Should().Be(0);
    }

    [Fact]
    public async Task Missing_key_throws()
    {
        var act = () => FileStoreReadStream.OpenAsync(_store, "missing/blob", default);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }
}
