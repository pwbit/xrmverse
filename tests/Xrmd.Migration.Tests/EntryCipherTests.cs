using System.Security.Cryptography;
using FluentAssertions;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Tests;

public class EntryCipherTests
{
    internal static readonly PackageKeys Keys = PackageKeys.Derive("correct horse battery", new byte[16]);

    internal static byte[] Encrypt(byte[] plain, string path, PackageKeys? keys = null)
    {
        using var blob = new MemoryStream();
        using (var w = EntryCipher.OpenWrite(blob, keys ?? Keys, path))
        {
            // Odd-sized writes cross segment boundaries.
            for (var i = 0; i < plain.Length; i += 70_001) w.Write(plain, i, Math.Min(70_001, plain.Length - i));
        }
        return blob.ToArray();
    }

    internal static byte[] Decrypt(byte[] blob, string path, PackageKeys? keys = null)
    {
        using var r = EntryCipher.OpenRead(new MemoryStream(blob), keys ?? Keys, path);
        using var plain = new MemoryStream();
        r.CopyTo(plain);
        return plain.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1_048_575)]
    [InlineData(1_048_576)]
    [InlineData(1_048_577)]
    [InlineData(3 * 1_048_576 + 7)]
    public void Round_trips_sizes(int size)
    {
        var plain = RandomNumberGenerator.GetBytes(size);
        Decrypt(Encrypt(plain, "data/a/1"), "data/a/1").Should().Equal(plain);
    }

    [Fact]
    public void Flipped_byte_is_detected()
    {
        var blob = Encrypt(RandomNumberGenerator.GetBytes(5000), "data/a/1");
        blob[20] ^= 0x01;
        var act = () => Decrypt(blob, "data/a/1");
        act.Should().Throw<PackageIntegrityException>().Which.LogicalPath.Should().Be("data/a/1");
    }

    [Fact]
    public void Truncated_final_segment_is_detected()
    {
        var blob = Encrypt(RandomNumberGenerator.GetBytes(1_048_576 + 100), "data/a/1");
        var cut = blob[..(12 + 1_048_576 + 16)];
        var act = () => Decrypt(cut, "data/a/1");
        act.Should().Throw<PackageIntegrityException>();
    }

    [Fact]
    public void Data_after_final_segment_is_detected()
    {
        var blob = Encrypt(RandomNumberGenerator.GetBytes(100), "data/a/1");
        var act = () => Decrypt([.. blob, 1, 2, 3], "data/a/1");
        act.Should().Throw<PackageIntegrityException>();
    }

    [Fact]
    public void Blob_read_under_another_path_is_detected()
    {
        var blob = Encrypt(RandomNumberGenerator.GetBytes(100), "data/a/1");
        var act = () => Decrypt(blob, "data/b/1");
        act.Should().Throw<PackageIntegrityException>();
    }

    [Fact]
    public void Wrong_key_is_detected()
    {
        var blob = Encrypt(RandomNumberGenerator.GetBytes(100), "data/a/1");
        var other = PackageKeys.Derive("another passphrase", new byte[16]);
        var act = () => Decrypt(blob, "data/a/1", other);
        act.Should().Throw<PackageIntegrityException>();
    }
}

public class EntryCipherOwnershipTests
{
    private sealed class Tracking(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public void Disposing_the_read_stream_closes_the_source()
    {
        var source = new Tracking(EntryCipherTests.Encrypt([1, 2, 3], "data/a/1"));
        EntryCipher.OpenRead(source, EntryCipherTests.Keys, "data/a/1").Dispose();
        source.Disposed.Should().BeTrue();
    }
}
