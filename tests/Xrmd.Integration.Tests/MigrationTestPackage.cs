using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Integration.Tests;

/// <summary>
/// A small synthetic migration package for the server-side tests (migration M3): invented accounts and contacts, an attachment blob
/// of 100 KiB (the largest entry, so a test can damage it), packed into one <c>.xrmpkg</c> file.
/// </summary>
internal static class MigrationTestPackage
{
    public const string Passphrase = "an invented passphrase";

    /// <summary>Builds the package under <paramref name="root"/>; <paramref name="damage"/> flips one byte of the attachment's blob.</summary>
    public static string Build(string root, bool damage = false)
    {
        var file = Build(root, p => p
            .Security("businessunit", "businessunitid")
            .Table("account", "accountid", Enumerable.Range(1, 3).Select(i => Record(Guid.NewGuid(), ("name", new StringValue($"Example {i}")))))
            .Table("contact", "contactid", Enumerable.Range(1, 2).Select(i => Record(Guid.NewGuid(), ("emailaddress1", new StringValue($"person{i}@example.com")))))
            .File("files/annotation/documentbody/" + Guid.NewGuid(), Encoding.UTF8.GetBytes(new string('x', 100 * 1024))));
        if (damage)
        {
            var folder = file[..^".xrmpkg".Length];
            var blob = new DirectoryInfo(Path.Combine(folder, "e")).GetFiles().OrderByDescending(f => f.Length).First();
            var bytes = File.ReadAllBytes(blob.FullName);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(blob.FullName, bytes);
            File.Delete(file);
            PackageWriter.Pack(folder, file);
        }
        return file;
    }

    /// <summary>
    /// Builds a package with the tables, relationships and files <paramref name="build"/> adds, written as the exporter writes them
    /// (gzip chunks through <see cref="ChunkWriter"/>); returns the <c>.xrmpkg</c> file.
    /// </summary>
    public static string Build(string root, Action<PackageBuilder> build, int recordsPerChunk = 5000) => Build(root, build, null, recordsPerChunk);

    /// <summary>
    /// As <see cref="Build(string, Action{PackageBuilder}, int)"/>, with <paramref name="maps"/> (a format version 2 package: the maps and
    /// the source catalog stored as the exporter stores them) when not null.
    /// </summary>
    public static string Build(string root, Action<PackageBuilder> build, PackageMapsPart? maps, int recordsPerChunk = 5000)
    {
        var folder = Path.Combine(root, "pkg-" + Guid.NewGuid().ToString("N")[..8]);
        using (var writer = PackageWriter.Create(folder, Passphrase, maps is null ? PackageHeader.CurrentVersion : PackageHeader.MappedVersion))
        {
            var builder = new PackageBuilder(writer, recordsPerChunk);
            build(builder);
            var mapEntries = maps is null ? [] : PackageMaps.Write(writer, maps.Maps, maps.Catalog);
            writer.Complete(new PackageManifest
            {
                CreatedOn = DateTimeOffset.UtcNow,
                ToolVersion = "test",
                Source = new PackageSource("https://contoso.example.com", "00000000-0000-0000-0000-00000000e001", "00000000-0000-0000-0000-00000000a001"),
                Tables = builder.Tables,
                Relationships = builder.Relationships,
                Solutions = builder.Solutions,
                Maps = mapEntries,
                ArchiveCandidates = builder.ArchiveCandidates,
            });
        }
        var file = folder + ".xrmpkg";
        PackageWriter.Pack(folder, file);
        return file;
    }

    /// <summary>The maps of a test package (each with its name and YAML text) and its source catalog.</summary>
    public sealed record PackageMapsPart(IReadOnlyList<(string Name, string Yaml)> Maps, SourceCatalog Catalog);

    /// <summary>A source catalog table with <paramref name="columns"/> (none of them custom); no object type code.</summary>
    public static SourceCatalogTable CatalogTable(string table, string primaryKey, bool isActivity, string ownership,
        params (string Column, string Type, string[] Targets)[] columns) =>
        new(table, primaryKey, null, ownership, isActivity, [.. columns.Select(c => new SourceCatalogColumn(c.Column, c.Type, c.Targets, false))]);

    /// <summary>A record with its values.</summary>
    public static MigrationRecord Record(Guid id, params (string Column, MigrationValue Value)[] values) =>
        new(id, values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal));

    /// <summary>Adds the parts of a test package.</summary>
    internal sealed class PackageBuilder(PackageWriter writer, int recordsPerChunk)
    {
        public List<PackageTable> Tables { get; } = [];
        public List<PackageRelationship> Relationships { get; } = [];
        public List<PackageSolution> Solutions { get; } = [];
        public List<PackageTable> ArchiveCandidates { get; } = [];

        /// <summary>A solution file (<c>solutions/&lt;name&gt;.zip</c>), imported at <paramref name="order"/> (default: after the others).</summary>
        public PackageBuilder Solution(string uniqueName, byte[] zip, int? order = null, bool managed = false)
        {
            var path = $"solutions/{uniqueName}.zip";
            File(path, zip);
            Solutions.Add(new PackageSolution(order ?? Solutions.Count + 1, uniqueName, "1.0.0.0", managed, path));
            return this;
        }

        /// <summary>A data table (<c>data/</c>), or a security table (<c>security/</c>) when <paramref name="security"/>.</summary>
        public PackageBuilder Table(string name, string primaryKey, IEnumerable<MigrationRecord> records, bool security = false)
        {
            using var chunks = new ChunkWriter(writer, $"{(security ? "security" : "data")}/{name}", recordsPerChunk);
            foreach (var r in records) chunks.Add(r);
            chunks.Complete();
            Tables.Add(new PackageTable(name, primaryKey, chunks.Count, [.. chunks.Chunks]));
            return this;
        }

        /// <summary>A data table the export kept as an archive candidate (table mapping spec §8.7): a standard table the target lacks.</summary>
        public PackageBuilder Candidate(string name, string primaryKey, IEnumerable<MigrationRecord> records)
        {
            Table(name, primaryKey, records);
            ArchiveCandidates.Add(Tables[^1]);
            return this;
        }

        public PackageBuilder Security(string name, string primaryKey, params MigrationRecord[] records) => Table(name, primaryKey, records, security: true);

        /// <summary>An N:N relationship: its intersect rows under <c>nn/</c>.</summary>
        public PackageBuilder Relationship(string schemaName, IEnumerable<MigrationRecord> pairs)
        {
            using var chunks = new ChunkWriter(writer, $"nn/{schemaName}", recordsPerChunk);
            foreach (var r in pairs) chunks.Add(r);
            chunks.Complete();
            Relationships.Add(new PackageRelationship(schemaName, chunks.Count, [.. chunks.Chunks]));
            return this;
        }

        /// <summary>A file entry (<c>files/…</c>).</summary>
        public PackageBuilder File(string path, byte[] content)
        {
            using var s = writer.CreateEntry(path);
            s.Write(content);
            s.Commit();
            return this;
        }
    }

    /// <summary>Uploads <paramref name="file"/> with the Admin API in chunks of 64 KiB and completes it; returns the package id.</summary>
    public static async Task<Guid> UploadAsync(HttpClient client, string token, string file)
    {
        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
        {
            var req = new HttpRequestMessage(method, path) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(req);
        }

        var created = await SendAsync(HttpMethod.Post, "admin/migrate/packages");
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = Guid.Parse(JsonNode.Parse(await created.Content.ReadAsStringAsync())!["packageId"]!.GetValue<string>());
        var bytes = await File.ReadAllBytesAsync(file);
        var chunks = bytes.Chunk(64 * 1024).ToList();
        for (var n = 0; n < chunks.Count; n++)
        {
            var content = new ByteArrayContent(chunks[n]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            (await SendAsync(HttpMethod.Put, $"admin/migrate/packages/{id}/chunks/{n}", content)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        var complete = await SendAsync(HttpMethod.Post, $"admin/migrate/packages/{id}/complete",
            JsonContent.Create(new { chunks = chunks.Count, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }));
        complete.StatusCode.Should().Be(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
        return id;
    }
}
