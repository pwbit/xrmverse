using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Tests;

public sealed class PackageReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Opens_zip_and_folder_with_same_manifest()
    {
        var file = SyntheticPackage.BuildFile(_root);
        using var zip = PackageReader.Open(file, SyntheticPackage.Passphrase);
        using var folder = PackageReader.Open(Path.Combine(_root, "pkg-folder"), SyntheticPackage.Passphrase);
        JsonSerializer.Serialize(zip.Manifest).Should().Be(JsonSerializer.Serialize(folder.Manifest));
        zip.Manifest.Tables.Select(t => t.LogicalName).Should().Equal("account", "contact");
        using var reader = new StreamReader(zip.OpenEntry(SyntheticPackage.FileEntry));
        reader.ReadToEnd().Should().Be("invented attachment");
    }

    [Fact]
    public void Wrong_passphrase_fails_before_reading_entries()
    {
        var file = SyntheticPackage.BuildFile(_root);
        var act = () => PackageReader.Open(file, "another passphrase");
        act.Should().Throw<PackagePassphraseException>().WithMessage("The passphrase is wrong for this package.");
    }

    [Fact]
    public void Version_3_is_refused()
    {
        var folder = SyntheticPackage.BuildFolder(Path.Combine(_root, "pkg"));
        var headerFile = Path.Combine(folder, PackageHeader.FileName);
        var header = JsonNode.Parse(File.ReadAllText(headerFile))!;
        header["formatVersion"] = 3;
        File.WriteAllText(headerFile, header.ToJsonString());
        var act = () => PackageReader.Open(folder, SyntheticPackage.Passphrase);
        act.Should().Throw<PackageFormatException>().WithMessage("Package format version 3 is not supported (this xrmd reads versions 1, 2).");
    }

    [Fact]
    public void Version_1_package_still_opens()
    {
        var folder = SyntheticPackage.BuildFolder(Path.Combine(_root, "pkg"));
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        reader.Header.FormatVersion.Should().Be(1);
        reader.Manifest.FormatVersion.Should().Be(1);
        reader.Manifest.Maps.Should().BeEmpty();
    }

    [Fact]
    public async Task Version_2_package_round_trips_its_maps_and_catalog()
    {
        var file = SyntheticPackage.BuildFile(_root, withMaps: true);
        using var reader = PackageReader.Open(file, SyntheticPackage.Passphrase);
        reader.Manifest.FormatVersion.Should().Be(2);
        reader.Manifest.Maps.Should().Equal("map/1-sales-like.yaml");
        var (files, catalog) = await PackageMaps.ReadAsync(reader, CancellationToken.None);
        files.Should().ContainSingle().Which.Name.Should().Be("sales-like");
        files[0].Tables.Should().ContainSingle().Which.Target.Should().Be("xv_company");
        catalog.Should().BeEquivalentTo(SyntheticPackage.Catalog);
        (await reader.VerifyAsync(null, CancellationToken.None)).Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task Version_1_package_has_no_maps_and_an_empty_catalog()
    {
        var folder = SyntheticPackage.BuildFolder(Path.Combine(_root, "pkg"));
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var (files, catalog) = await PackageMaps.ReadAsync(reader, CancellationToken.None);
        files.Should().BeEmpty();
        catalog.Tables.Should().BeEmpty();
        catalog.Options.Should().BeEmpty();
    }

    [Fact]
    public void EntryPath_sanitizes_names()
    {
        PackageMaps.EntryPath(1, "Sales Migration").Should().Be("map/1-sales-migration.yaml");
        PackageMaps.EntryPath(12, "  A/b_c.yaml! ").Should().Be("map/12-a-b-c-yaml.yaml");
        PackageMaps.EntryPath(2, "***").Should().Be("map/2-map.yaml");
    }

    [Fact]
    public void A_file_that_is_not_a_package_is_refused()
    {
        var file = Path.Combine(_root, "x.xrmpkg");
        File.WriteAllText(file, "not a zip");
        var act = () => PackageReader.Open(file, SyntheticPackage.Passphrase);
        act.Should().Throw<PackageFormatException>();
    }

    [Fact]
    public async Task Verify_reports_missing_and_tampered_entries()
    {
        var folder = SyntheticPackage.BuildFolder(Path.Combine(_root, "pkg"));
        using (var clean = PackageReader.Open(folder, SyntheticPackage.Passphrase))
            (await clean.VerifyAsync(null, CancellationToken.None)).Problems.Should().BeEmpty();

        File.Delete(SyntheticPackage.BlobOf(folder, SyntheticPackage.ContactChunk));
        var blob = SyntheticPackage.BlobOf(folder, SyntheticPackage.AccountChunk);
        var bytes = File.ReadAllBytes(blob);
        bytes[20] ^= 1;
        File.WriteAllBytes(blob, bytes);

        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var result = await reader.VerifyAsync(null, CancellationToken.None);
        result.Problems.Should().BeEquivalentTo(new[]
        {
            new PackageEntryProblem(SyntheticPackage.AccountChunk, "tampered"),
            new PackageEntryProblem(SyntheticPackage.ContactChunk, "missing"),
        });
        result.EntriesChecked.Should().Be(4);
    }

    [Fact]
    public async Task Swapped_blobs_are_reported_as_tampered()
    {
        var folder = SyntheticPackage.BuildFolder(Path.Combine(_root, "pkg"));
        var a = SyntheticPackage.BlobOf(folder, SyntheticPackage.AccountChunk);
        var c = SyntheticPackage.BlobOf(folder, SyntheticPackage.ContactChunk);
        File.Move(a, a + ".x");
        File.Move(c, a);
        File.Move(a + ".x", c);

        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        (await reader.VerifyAsync(null, CancellationToken.None)).Problems.Select(p => (p.Path, p.Problem)).Should().BeEquivalentTo(new[]
        {
            (SyntheticPackage.AccountChunk, "tampered"),
            (SyntheticPackage.ContactChunk, "tampered"),
        });
    }

    [Fact]
    public void Opening_a_listed_entry_that_is_missing_throws_integrity()
    {
        var folder = SyntheticPackage.BuildFolder(Path.Combine(_root, "pkg"));
        File.Delete(SyntheticPackage.BlobOf(folder, SyntheticPackage.FileEntry));
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var act = () => reader.OpenEntry(SyntheticPackage.FileEntry);
        act.Should().Throw<PackageIntegrityException>().Which.LogicalPath.Should().Be(SyntheticPackage.FileEntry);
    }
}
