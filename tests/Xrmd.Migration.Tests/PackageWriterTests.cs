using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Tests;

public sealed class PackageWriterTests : IDisposable
{
    internal const string Passphrase = "correct horse battery";
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder => Path.Combine(_root, "pkg");

    private static void WriteEntry(PackageWriter writer, string path, string content)
    {
        using var s = writer.CreateEntry(path);
        s.Write(Encoding.UTF8.GetBytes(content));
        s.Commit();
    }

    internal static PackageManifest EmptyManifest() => new()
    {
        CreatedOn = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero),
        ToolVersion = "test",
        Source = new PackageSource("https://contoso.example.com", "env", "org"),
    };

    [Fact]
    public void Header_and_manifest_are_the_only_named_files_and_nothing_shows_in_clear()
    {
        using (var writer = PackageWriter.Create(Folder, Passphrase))
        {
            WriteEntry(writer, "data/account/00001.jsonl.gz", "{\"name\":\"account\"}");
            writer.WriteProgress(new { table = "account" });
            writer.Complete(EmptyManifest() with { Tables = [new PackageTable("account", "accountid", 1, ["data/account/00001.jsonl.gz"])] });
        }
        var files = Directory.GetFiles(Folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Folder, f).Replace('\\', '/')).ToList();
        files.Should().Contain(["header.json", "manifest"]);
        files.Except(["header.json", "manifest"]).Should().OnlyContain(f => System.Text.RegularExpressions.Regex.IsMatch(f, "^e/[0-9a-f]{32}$"));
        files.Should().HaveCount(3, "progress and per-entry records are removed when the export completes");
        foreach (var f in files)
            Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Folder, f))).Should().NotContain("account");
    }

    [Fact]
    public void Resume_discards_partial_entry_and_keeps_completed()
    {
        var writer = PackageWriter.Create(Folder, Passphrase);
        WriteEntry(writer, "A", "done");
        var abandoned = writer.CreateEntry("B");
        abandoned.Write("half"u8);
        writer.Dispose(); // the export stops: B is never completed

        using var resumed = PackageWriter.OpenForResume(Folder, Passphrase);
        resumed.HasEntry("A").Should().BeTrue();
        resumed.HasEntry("B").Should().BeFalse();
        resumed.Entries.Single().Path.Should().Be("A");
        Directory.GetFiles(Folder, "*.partial", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void Resume_keeps_the_version_the_folder_was_created_with()
    {
        PackageWriter.Create(Folder, Passphrase, PackageHeader.MappedVersion).Dispose();
        using (var resumed = PackageWriter.OpenForResume(Folder, Passphrase)) resumed.Complete(new PackageManifest());
        using var reader = PackageReader.Open(Folder, Passphrase);
        reader.Header.FormatVersion.Should().Be(2);
        reader.Manifest.FormatVersion.Should().Be(2);
    }

    [Fact]
    public void Create_refuses_an_unsupported_version()
    {
        var act = () => PackageWriter.Create(Folder, Passphrase, 3);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Resume_keeps_progress()
    {
        using (var writer = PackageWriter.Create(Folder, Passphrase))
            writer.WriteProgress(new Progress("account", 5000));
        using var resumed = PackageWriter.OpenForResume(Folder, Passphrase);
        resumed.ReadProgress<Progress>().Should().Be(new Progress("account", 5000));
    }

    private sealed record Progress(string Table, int Rows);

    [Fact]
    public void Resume_with_wrong_passphrase_throws_PackagePassphraseException()
    {
        PackageWriter.Create(Folder, Passphrase).Dispose();
        var act = () => PackageWriter.OpenForResume(Folder, "another passphrase");
        act.Should().Throw<PackagePassphraseException>();
    }

    [Fact]
    public void Create_refuses_a_folder_that_is_not_empty()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "x"), "x");
        var act = () => PackageWriter.Create(Folder, Passphrase);
        act.Should().Throw<IOException>();
    }

    [Fact]
    public void Pack_refuses_incomplete_export()
    {
        using (var writer = PackageWriter.Create(Folder, Passphrase)) WriteEntry(writer, "A", "x");
        var act = () => PackageWriter.Pack(Folder, Path.Combine(_root, "p.xrmpkg"));
        act.Should().Throw<PackageFormatException>().WithMessage("*not complete*");
    }

    [Fact]
    public void Pack_writes_store_only_zip()
    {
        using (var writer = PackageWriter.Create(Folder, Passphrase))
        {
            WriteEntry(writer, "A", new string('x', 10_000));
            writer.Complete(EmptyManifest());
        }
        var file = Path.Combine(_root, "p.xrmpkg");
        PackageWriter.Pack(Folder, file);
        using var zip = ZipFile.OpenRead(file);
        zip.Entries.Should().OnlyContain(e => e.CompressedLength == e.Length);
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
            Directory.GetFiles(Folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Folder, f).Replace('\\', '/')));
    }

    [Fact]
    public async Task Pack_is_the_same_bytes_every_time()
    {
        using (var writer = PackageWriter.Create(Folder, Passphrase))
        {
            WriteEntry(writer, "A", new string('x', 1_000));
            writer.Complete(EmptyManifest());
        }
        var first = Path.Combine(_root, "a.xrmpkg");
        var second = Path.Combine(_root, "b.xrmpkg");
        PackageWriter.Pack(Folder, first);
        await Task.Delay(2100); // a zip time has a resolution of two seconds
        PackageWriter.Pack(Folder, second);
        File.ReadAllBytes(second).Should().Equal(File.ReadAllBytes(first), "a resumed upload finds the package by its checksum");
    }
}

public sealed class PackageWriterFailureTests : IDisposable
{
    private readonly string _folder = Path.Combine(Directory.CreateTempSubdirectory("xrmpkg-").FullName, "pkg");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);

    [Fact]
    public void An_entry_disposed_without_commit_does_not_count()
    {
        using (var writer = PackageWriter.Create(_folder, PackageWriterTests.Passphrase))
        {
            try
            {
                using var s = writer.CreateEntry("data/account/00001.jsonl.gz");
                s.Write("half a chunk"u8);
                throw new IOException("the source went away");
            }
            catch (IOException) { }
            writer.HasEntry("data/account/00001.jsonl.gz").Should().BeFalse();
        }
        using var resumed = PackageWriter.OpenForResume(_folder, PackageWriterTests.Passphrase);
        resumed.HasEntry("data/account/00001.jsonl.gz").Should().BeFalse();
        Directory.GetFiles(_folder, "*", SearchOption.AllDirectories).Should().ContainSingle(f => f.EndsWith("header.json"));
    }

    [Fact]
    public void A_blob_without_its_record_does_not_count_on_resume()
    {
        using (var writer = PackageWriter.Create(_folder, PackageWriterTests.Passphrase))
        {
            using var s = writer.CreateEntry("A");
            s.Write("x"u8);
            s.Commit();
        }
        // A crash between the blob's rename and its record: the record is missing.
        foreach (var meta in Directory.GetFiles(Path.Combine(_folder, "e"), "*.meta")) File.Delete(meta);
        using var resumed = PackageWriter.OpenForResume(_folder, PackageWriterTests.Passphrase);
        resumed.HasEntry("A").Should().BeFalse();
        Directory.GetFiles(Path.Combine(_folder, "e")).Should().BeEmpty();
    }

    [Fact]
    public void A_chunk_writer_disposed_without_complete_drops_the_open_chunk()
    {
        using var writer = PackageWriter.Create(_folder, PackageWriterTests.Passphrase);
        using (var chunk = new Xrmd.Migration.Records.ChunkWriter(writer, "data/account", recordsPerChunk: 2))
        {
            for (var i = 0; i < 3; i++)
                chunk.Add(new Xrmd.Migration.Records.MigrationRecord(Guid.NewGuid(), new Dictionary<string, Xrmd.Migration.Records.MigrationValue>()));
        }
        writer.Entries.Select(e => e.Path).Should().Equal("data/account/00001.jsonl.gz");
    }
}
