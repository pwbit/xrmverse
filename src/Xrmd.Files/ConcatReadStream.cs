namespace Xrmd.Files;

/// <summary>Reads the sources one after another through the store; only the current source's response is open.</summary>
internal sealed class ConcatReadStream(IFileStore store, IReadOnlyList<string> keys, Func<int, CancellationToken, Task>? beforeSource = null) : Stream
{
    private int _next;
    private Stream? _current;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Async reads only.");
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (true)
        {
            if (_current == null)
            {
                if (_next >= keys.Count) return 0;
                if (beforeSource != null) await beforeSource(_next, ct);
                _current = await store.OpenReadAsync(keys[_next++], 0, null, ct);
            }
            var read = await _current.ReadAsync(buffer, ct);
            if (read > 0) return read;
            await _current.DisposeAsync();
            _current = null;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_current != null) await _current.DisposeAsync();
        _current = null;
        await base.DisposeAsync();
    }
}
