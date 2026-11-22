using System.Text;
using FluentAssertions;
using Xrmd.Files;

namespace Xrmd.Files.Tests;

/// <summary>
/// Behaviour every <see cref="IFileStore"/> must have. A store test class derives from this, creates its store in
/// <see cref="CreateStore"/> and removes whatever it created in <see cref="CleanupAsync"/>. No file-system assumptions here.
/// </summary>
public abstract class FileStoreContract : IAsyncLifetime
{
    protected IFileStore Store { get; private set; } = null!;

    /// <summary>A store that is empty and isolated from other tests.</summary>
    protected abstract Task<IFileStore> CreateStore();

    /// <summary>Releases the store and everything the test left in it.</summary>
    protected abstract Task CleanupAsync();

    public async Task InitializeAsync() => Store = await CreateStore();

    public Task DisposeAsync() => CleanupAsync();

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private async Task<string> ReadAllAsync(string key, long offset = 0, long? length = null)
    {
        await using var stream = await Store.OpenReadAsync(key, offset, length, default);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<List<string>> ListAsync(string prefix)
    {
        var keys = new List<string>();
        await foreach (var key in Store.ListAsync(prefix, default)) keys.Add(key);
        return keys;
    }

    [Fact]
    public async Task Write_then_read_round_trips_bytes()
    {
        var data = new byte[300_000];
        new Random(7).NextBytes(data);
        await Store.WriteAsync("org/file1", new MemoryStream(data), default);

        await using var stream = await Store.OpenReadAsync("org/file1", 0, null, default);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.ToArray().Should().Equal(data);
        (await Store.GetLengthAsync("org/file1", default)).Should().Be(data.Length);
    }

    [Fact]
    public async Task Write_replaces_an_existing_blob()
    {
        await Store.WriteAsync("org/f", Bytes("old content"), default);
        await Store.WriteAsync("org/f", Bytes("new"), default);
        (await ReadAllAsync("org/f")).Should().Be("new");
        (await Store.GetLengthAsync("org/f", default)).Should().Be(3);
    }

    [Fact]
    public async Task Read_range_returns_exact_slice()
    {
        await Store.WriteAsync("org/f", Bytes("0123456789"), default);
        (await ReadAllAsync("org/f", 3, 4)).Should().Be("3456");
        (await ReadAllAsync("org/f", 7, null)).Should().Be("789");
        (await ReadAllAsync("org/f", 8, 100)).Should().Be("89");
        (await ReadAllAsync("org/f", 0, 0)).Should().BeEmpty();
    }

    [Fact]
    public async Task Read_with_offset_beyond_the_end_returns_an_empty_stream()
    {
        await Store.WriteAsync("org/f", Bytes("abc"), default);
        (await ReadAllAsync("org/f", 10, null)).Should().BeEmpty();
        (await ReadAllAsync("org/f", 3, 5)).Should().BeEmpty();
    }

    [Fact]
    public async Task Concat_of_an_empty_list_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ConcatAsync([], "org/final", default));
    }

    [Fact]
    public async Task DeletePrefix_with_an_empty_prefix_is_rejected()
    {
        await Store.WriteAsync("org/f", Bytes("x"), default);
        await Assert.ThrowsAsync<ArgumentException>(() => Store.DeletePrefixAsync("", default));
        (await Store.GetLengthAsync("org/f", default)).Should().Be(1);
    }

    [Fact]
    public async Task Missing_key_read_throws_FileNotFound_and_length_is_null()
    {
        var act = async () => await Store.OpenReadAsync("org/missing", 0, null, default);
        await act.Should().ThrowAsync<FileNotFoundException>();
        (await Store.GetLengthAsync("org/missing", default)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_is_idempotent()
    {
        await Store.WriteAsync("org/f", Bytes("x"), default);
        await Store.DeleteAsync("org/f", default);
        (await Store.GetLengthAsync("org/f", default)).Should().BeNull();
        await Store.DeleteAsync("org/f", default);
        await Store.DeleteAsync("never/existed", default);
    }

    [Fact]
    public async Task DeletePrefix_removes_only_that_prefix()
    {
        await Store.WriteAsync("org/_upload/s1/0", Bytes("a"), default);
        await Store.WriteAsync("org/_upload/s1/1", Bytes("b"), default);
        await Store.WriteAsync("org/_upload/s2/0", Bytes("c"), default);
        await Store.WriteAsync("org/final", Bytes("d"), default);
        await Store.WriteAsync("other/_upload/s1/0", Bytes("e"), default);

        await Store.DeletePrefixAsync("org/_upload/s1/", default);

        (await ListAsync("")).Should().BeEquivalentTo("org/_upload/s2/0", "org/final", "other/_upload/s1/0");
        await Store.DeletePrefixAsync("no/such/prefix/", default);
    }

    [Fact]
    public async Task List_returns_keys_under_prefix()
    {
        await Store.WriteAsync("org/a", Bytes("1"), default);
        await Store.WriteAsync("org/b/c", Bytes("2"), default);
        await Store.WriteAsync("other/d", Bytes("3"), default);

        (await ListAsync("org/")).Should().BeEquivalentTo("org/a", "org/b/c");
        (await ListAsync("org/b/")).Should().BeEquivalentTo("org/b/c");
        (await ListAsync("nothing/")).Should().BeEmpty();
    }

    [Fact]
    public async Task Concat_preserves_order()
    {
        await Store.WriteAsync("org/_upload/s/0", Bytes("AAA"), default);
        await Store.WriteAsync("org/_upload/s/1", Bytes("BB"), default);
        await Store.WriteAsync("org/_upload/s/2", Bytes("C"), default);

        await Store.ConcatAsync(["org/_upload/s/2", "org/_upload/s/0", "org/_upload/s/1"], "org/final", default);

        (await ReadAllAsync("org/final")).Should().Be("CAAABB");
        (await Store.GetLengthAsync("org/final", default)).Should().Be(6);
    }

    [Fact]
    public async Task Concat_with_a_missing_source_throws_and_leaves_no_target()
    {
        await Store.WriteAsync("org/s/0", Bytes("A"), default);
        var act = async () => await Store.ConcatAsync(["org/s/0", "org/s/missing"], "org/final", default);
        await act.Should().ThrowAsync<FileNotFoundException>();
        (await Store.GetLengthAsync("org/final", default)).Should().BeNull();
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../b")]
    [InlineData("a/..")]
    [InlineData("a\\b")]
    [InlineData("/abs")]
    [InlineData("a//b")]
    [InlineData("a/b/")]
    [InlineData("")]
    [InlineData("a/b c")]
    public async Task Traversal_keys_are_rejected(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.WriteAsync(key, Bytes("x"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.OpenReadAsync(key, 0, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetLengthAsync(key, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.DeleteAsync(key, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ConcatAsync([key], "org/t", default));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ConcatAsync(["org/s"], key, default));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../b")]
    [InlineData("a\\b")]
    [InlineData("/abs")]
    public async Task Traversal_prefixes_are_rejected(string prefix)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.DeletePrefixAsync(prefix, default));
        await Assert.ThrowsAsync<ArgumentException>(async () => { await foreach (var _ in Store.ListAsync(prefix, default)) { } });
    }

    [Fact]
    public async Task Write_is_atomic()
    {
        const int total = 8 * 1024 * 1024;
        var data = new byte[total];
        new Random(3).NextBytes(data);
        await Store.WriteAsync("org/big", Bytes("old"), default); // replacement must also stay atomic

        using var done = new CancellationTokenSource();
        var observed = new List<long?>();
        var reader = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                observed.Add(await Store.GetLengthAsync("org/big", default));
                await Task.Yield();
            }
        });

        await Store.WriteAsync("org/big", new TrickleStream(data), default);
        await done.CancelAsync();
        await reader;

        observed.Should().NotBeEmpty();
        observed.Should().OnlyContain(l => l == null || l == 3 || l == total);
        (await Store.GetLengthAsync("org/big", default)).Should().Be(total);
    }

    /// <summary>Hands out the data in small pieces with pauses, so a non-atomic writer would be seen half way.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, 256 * 1024), data.Length - _position);
            if (n > 0) Thread.Sleep(5);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
