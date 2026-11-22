using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Xrmd.Migration.Packaging;

/// <summary>
/// Encrypts and decrypts one package entry as a stream, without holding the entry in memory.
/// <para>
/// Blob layout: <c>"XPK1"</c>, an 8-byte random nonce prefix, then segments. A segment is the AES-256-GCM ciphertext of
/// <see cref="SegmentSize"/> plaintext bytes (the last one 0 to <see cref="SegmentSize"/>) followed by its 16-byte tag.
/// The 12-byte nonce is the prefix and a big-endian segment counter whose top bit marks the last segment, and the
/// entry's logical path is the associated data: a cut, extended, reordered or swapped blob fails to decrypt.
/// </para>
/// </summary>
public static class EntryCipher
{
    public const int SegmentSize = 1_048_576;
    private const int TagSize = 16;
    private const int PrefixSize = 8;
    private const uint FinalFlag = 0x8000_0000;
    private static readonly byte[] Magic = "XPK1"u8.ToArray();

    /// <summary>
    /// A write-only stream that encrypts into <paramref name="destination"/>; disposing it writes the last segment and
    /// disposes <paramref name="destination"/>.
    /// </summary>
    public static Stream OpenWrite(Stream destination, PackageKeys keys, string logicalPath) =>
        new EncryptingStream(destination, keys, logicalPath);

    /// <summary>A read-only stream over the plaintext of the blob in <paramref name="source"/>; disposing it disposes <paramref name="source"/>.</summary>
    /// <exception cref="PackageIntegrityException">On read, when the blob was changed, cut, extended or belongs to another path or key.</exception>
    public static Stream OpenRead(Stream source, PackageKeys keys, string logicalPath) =>
        new DecryptingStream(source, keys, logicalPath);

    private static void Nonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, uint counter, bool final)
    {
        if (counter >= FinalFlag) throw new InvalidOperationException("The entry has too many segments.");
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[PrefixSize..], final ? counter | FinalFlag : counter);
    }

    private abstract class CipherStream(Stream inner, PackageKeys keys, string logicalPath) : Stream
    {
        protected readonly Stream Inner = inner;
        protected readonly AesGcm Aes = new(keys.EncryptionKey, TagSize);
        protected readonly byte[] Associated = Encoding.UTF8.GetBytes(logicalPath);
        protected readonly string LogicalPath = logicalPath;
        protected readonly byte[] Prefix = new byte[PrefixSize];
        protected readonly byte[] Plain = new byte[SegmentSize];
        protected readonly byte[] Sealed = new byte[SegmentSize + TagSize];
        protected uint Counter;

        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Aes.Dispose();
                Inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class EncryptingStream : CipherStream
    {
        private int _filled;
        private bool _disposed;

        public EncryptingStream(Stream inner, PackageKeys keys, string logicalPath) : base(inner, keys, logicalPath)
        {
            RandomNumberGenerator.Fill(Prefix);
            Inner.Write(Magic);
            Inner.Write(Prefix);
        }

        public override bool CanRead => false;
        public override bool CanWrite => !_disposed;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (buffer.Length > 0)
            {
                // A full segment is sealed only once more data arrives: until then it might be the last one.
                if (_filled == SegmentSize) Seal(final: false);
                var n = Math.Min(SegmentSize - _filled, buffer.Length);
                buffer[..n].CopyTo(Plain.AsSpan(_filled));
                _filled += n;
                buffer = buffer[n..];
            }
        }

        private void Seal(bool final)
        {
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, Prefix, Counter++, final);
            Aes.Encrypt(nonce, Plain.AsSpan(0, _filled), Sealed.AsSpan(0, _filled), Sealed.AsSpan(_filled, TagSize), Associated);
            Inner.Write(Sealed, 0, _filled + TagSize);
            _filled = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                Seal(final: true);
                Inner.Flush();
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }

    private sealed class DecryptingStream(Stream inner, PackageKeys keys, string logicalPath) : CipherStream(inner, keys, logicalPath)
    {
        private bool _started;
        private bool _finished;
        private int _offset;
        private int _available;
        private int _pending = -1; // a byte read ahead to tell whether a full segment is the last one

        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (!_started) Start();
            while (_available == 0)
            {
                if (_finished) return 0;
                Open();
            }
            var n = Math.Min(buffer.Length, _available);
            Plain.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            _available -= n;
            return n;
        }

        private void Start()
        {
            _started = true;
            Span<byte> head = stackalloc byte[Magic.Length + PrefixSize];
            if (Inner.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) != head.Length || !head[..Magic.Length].SequenceEqual(Magic))
                throw Broken("it does not start with an entry header");
            head[Magic.Length..].CopyTo(Prefix);
        }

        private void Open()
        {
            var read = 0;
            if (_pending >= 0)
            {
                Sealed[0] = (byte)_pending;
                read = 1;
                _pending = -1;
            }
            read += Inner.ReadAtLeast(Sealed.AsSpan(read), Sealed.Length - read, throwOnEndOfStream: false);
            if (read < TagSize) throw Broken("its last segment is missing");
            var final = read < Sealed.Length;
            if (!final)
            {
                _pending = Inner.ReadByte();
                final = _pending < 0;
            }
            var length = read - TagSize;
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, Prefix, Counter++, final);
            try
            {
                Aes.Decrypt(nonce, Sealed.AsSpan(0, length), Sealed.AsSpan(length, TagSize), Plain.AsSpan(0, length), Associated);
            }
            catch (AuthenticationTagMismatchException e)
            {
                throw Broken("it was changed, cut, or belongs to another entry or passphrase", e);
            }
            _offset = 0;
            _available = length;
            _finished = final;
        }

        private PackageIntegrityException Broken(string why, Exception? inner = null) =>
            new(LogicalPath, $"The package entry '{LogicalPath}' cannot be trusted: {why}.", inner);
    }
}
