namespace Xrmd.Files;

/// <summary>
/// A seekable, read-only view of one blob of an <see cref="IFileStore"/>: reads go to the store as ranged reads of
/// <see cref="BlockSize"/> bytes, and the last block read is kept, so a reader that seeks (a zip archive) does not download the blob
/// first. Used to read an uploaded migration package in place (migration plan M3, R5). Not thread-safe.
/// </summary>
public sealed class FileStoreReadStream : Stream
{
    /// <summary>The size of one ranged read: 1 MiB.</summary>
    public const int BlockSize = 1024 * 1024;

    private readonly IFileStore _store;
    private readonly string _key;
    private readonly long _length;
    private long _position;
    private long _blockStart = -1;
    private byte[] _block = [];
    private int _blockLength;

    private FileStoreReadStream(IFileStore store, string key, long length)
    {
        _store = store;
        _key = key;
        _length = length;
    }

    /// <summary>Opens <paramref name="key"/> of <paramref name="store"/>.</summary>
    /// <exception cref="FileNotFoundException">The key is absent.</exception>
    public static async Task<Stream> OpenAsync(IFileStore store, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(key);
        var length = await store.GetLengthAsync(key, ct) ?? throw new FileNotFoundException($"The blob '{key}' does not exist.", key);
        return new FileStoreReadStream(store, key, length);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        return _position = target;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (!EnsureBlockAsync(CancellationToken.None, sync: true).GetAwaiter().GetResult()) return 0;
        return CopyFromBlock(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!await EnsureBlockAsync(cancellationToken, sync: false)) return 0;
        return CopyFromBlock(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int CopyFromBlock(Span<byte> buffer)
    {
        var inBlock = (int)(_position - _blockStart);
        var n = Math.Min(buffer.Length, _blockLength - inBlock);
        _block.AsSpan(inBlock, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    /// <summary>Loads the block holding <see cref="_position"/>; false at the end of the blob.</summary>
    private async Task<bool> EnsureBlockAsync(CancellationToken ct, bool sync)
    {
        if (_position >= _length) return false;
        if (_blockStart >= 0 && _position >= _blockStart && _position < _blockStart + _blockLength) return true;
        var start = _position / BlockSize * BlockSize;
        var length = (int)Math.Min(BlockSize, _length - start);
        if (_block.Length < length) _block = new byte[BlockSize];
        await using var source = await _store.OpenReadAsync(_key, start, length, ct);
        var read = 0;
        while (read < length)
        {
            var n = sync ? source.Read(_block, read, length - read) : await source.ReadAsync(_block.AsMemory(read, length - read), ct);
            if (n == 0) throw new IOException($"The blob '{_key}' ended before its length ({_length} bytes).");
            read += n;
        }
        _blockStart = start;
        _blockLength = length;
        return true;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
