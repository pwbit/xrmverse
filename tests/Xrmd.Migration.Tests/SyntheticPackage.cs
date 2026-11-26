using System.Text;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Tests;

/// <summary>
/// A small package built from invented data, for tests: two tables, one N:N relationship and one file entry.
/// Build it as a folder or as a packed <c>.xrmpkg</c>.
/// </summary>
internal static class SyntheticPackage
{
    public const string Passphrase = "correct horse battery";
    public const string AccountChunk = "data/account/00001.jsonl.gz";
    public const string ContactChunk = "data/contact/00001.jsonl.gz";
    public const string RelationshipChunk = "nn/xv_account_contact/00001.jsonl.gz";
    public const string FileEntry = "files/annotation/documentbody/6f1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a1";

    public const string SalesLikeYaml = "version: 1\ntables:\n  - source: account\n    target: xv_company\n    columns:\n      name: xv_name\n";

    public static string BuildFolder(string folder, bool withMaps = false)
    {
        using var writer = PackageWriter.Create(folder, Passphrase, withMaps ? PackageHeader.MappedVersion : 1);
        Write(writer, AccountChunk, "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"v\":{\"name\":\"Contoso Example\"}}\n");
        Write(writer, ContactChunk, "{\"id\":\"22222222-2222-2222-2222-222222222222\",\"v\":{\"emailaddress1\":\"anna@example.com\"}}\n");
        Write(writer, RelationshipChunk, "{\"a\":\"11111111-1111-1111-1111-111111111111\",\"b\":\"22222222-2222-2222-2222-222222222222\"}\n");
        Write(writer, FileEntry, "invented attachment");
        var mapPaths = withMaps ? PackageMaps.Write(writer, [("Sales Like", SalesLikeYaml)], Catalog) : [];
        writer.Complete(new PackageManifest
        {
            Maps = [.. mapPaths],
            CreatedOn = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero),
            ToolVersion = "test",
            Source = new PackageSource("https://contoso.example.com", "00000000-0000-0000-0000-00000000e001", "00000000-0000-0000-0000-00000000a001"),
            Solutions = [new PackageSolution(1, "xv_core", "1.0.0.0", false, "solutions/1-xv_core.zip")],
            Tables = [new PackageTable("account", "accountid", 1, [AccountChunk]), new PackageTable("contact", "contactid", 1, [ContactChunk])],
            Relationships = [new PackageRelationship("xv_account_contact", 1, [RelationshipChunk])],
        });
        return folder;
    }

    public static readonly SourceCatalog Catalog = new(
        [new SourceCatalogTable("account", "accountid", 1, "user", false, [new SourceCatalogColumn("name", "String", [], false), new SourceCatalogColumn("parentaccountid", "Lookup", ["account"], false)])],
        [new SourceCatalogOption("account", "statuscode", 1, 0, "Active")]);

    public static string BuildFile(string root, bool withMaps = false)
    {
        var folder = BuildFolder(Path.Combine(root, "pkg-folder"), withMaps);
        var file = Path.Combine(root, "test.xrmpkg");
        PackageWriter.Pack(folder, file);
        return file;
    }

    /// <summary>The on-disk blob of an entry in a package folder.</summary>
    public static string BlobOf(string folder, string logicalPath) =>
        Path.Combine(folder, PackageHeader.Parse(File.ReadAllBytes(Path.Combine(folder, PackageHeader.FileName))).Open(Passphrase).EntryName(logicalPath));

    private static void Write(PackageWriter writer, string path, string content)
    {
        using var s = writer.CreateEntry(path);
        s.Write(Encoding.UTF8.GetBytes(content));
        s.Commit();
    }
}
