using System.Text.Json;
using FluentAssertions;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Tests;

/// <summary><see cref="PackageReader.Open(Stream, string, bool)"/>: a packed package read from any seekable stream (migration M3, task 3).</summary>
public sealed class PackageReaderStreamTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Open_stream_reads_same_manifest_as_path()
    {
        var file = SyntheticPackage.BuildFile(_root);
        using var byPath = PackageReader.Open(file, SyntheticPackage.Passphrase);
        using var byStream = PackageReader.Open(new MemoryStream(File.ReadAllBytes(file)), SyntheticPackage.Passphrase);

        JsonSerializer.Serialize(byStream.Manifest).Should().Be(JsonSerializer.Serialize(byPath.Manifest));
        using var reader = new StreamReader(byStream.OpenEntry(SyntheticPackage.FileEntry));
        reader.ReadToEnd().Should().Be("invented attachment");
    }

    [Fact]
    public void Wrong_passphrase_on_stream_throws_PackagePassphraseException()
    {
        var file = SyntheticPackage.BuildFile(_root);
        var act = () => PackageReader.Open(new MemoryStream(File.ReadAllBytes(file)), "another passphrase");
        act.Should().Throw<PackagePassphraseException>();
    }

    [Fact]
    public void Not_a_zip_stream_is_a_format_error()
    {
        var act = () => PackageReader.Open(new MemoryStream([1, 2, 3, 4]), SyntheticPackage.Passphrase);
        act.Should().Throw<PackageFormatException>();
    }

    [Fact]
    public async Task Verify_works_on_stream()
    {
        var file = SyntheticPackage.BuildFile(_root);
        using var reader = PackageReader.Open(new MemoryStream(File.ReadAllBytes(file)), SyntheticPackage.Passphrase);
        (await reader.VerifyAsync(null, default)).Problems.Should().BeEmpty();
    }
}
