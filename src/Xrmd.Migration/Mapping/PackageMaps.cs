using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Mapping;

/// <summary>The map files and the source catalog of a format version 2 package (entries <c>map/&lt;n&gt;-&lt;name&gt;.yaml</c> and <c>map/source.json</c>).</summary>
public static partial class PackageMaps
{
    public const string SourcePath = "map/source.json";

    /// <summary>The entry path of map number <paramref name="n"/> (from 1): the name lowercased, with every run of other characters than <c>a-z0-9</c> as one <c>-</c>.</summary>
    public static string EntryPath(int n, string name)
    {
        var slug = Unsafe().Replace(name.ToLowerInvariant(), "-").Trim('-');
        return $"map/{n}-{(slug.Length == 0 ? "map" : slug)}.yaml";
    }

    /// <summary>Writes the maps (numbered in order) and the catalog as entries; returns the map entry paths, for <see cref="PackageManifest.Maps"/>.</summary>
    public static IReadOnlyList<string> Write(PackageWriter w, IReadOnlyList<(string Name, string Yaml)> maps, SourceCatalog catalog)
    {
        var paths = new List<string>();
        for (var i = 0; i < maps.Count; i++)
        {
            paths.Add(EntryPath(i + 1, maps[i].Name));
            WriteEntry(w, paths[i], Encoding.UTF8.GetBytes(maps[i].Yaml));
        }
        WriteEntry(w, SourcePath, JsonSerializer.SerializeToUtf8Bytes(catalog, PackageJson.Options));
        return paths;
    }

    /// <summary>Reads the maps listed in the manifest and the catalog; empty for a package without maps.</summary>
    public static async Task<(IReadOnlyList<MapFile> Files, SourceCatalog Catalog)> ReadAsync(PackageReader r, CancellationToken ct)
    {
        if (r.Manifest.Maps.Count == 0 && !r.Manifest.Entries.Any(e => e.Path == SourcePath)) return ([], SourceCatalog.Empty);
        var files = new List<MapFile>();
        foreach (var path in r.Manifest.Maps)
        {
            await using var stream = r.OpenEntry(path);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            files.Add(MapReader.Parse(await reader.ReadToEndAsync(ct), NameOf(path)));
        }
        return (files, await ReadCatalogAsync(r, ct));
    }

    /// <summary>The source catalog of the package; empty for a package without one.</summary>
    public static async Task<SourceCatalog> ReadCatalogAsync(PackageReader r, CancellationToken ct)
    {
        if (!r.Manifest.Entries.Any(e => e.Path == SourcePath)) return SourceCatalog.Empty;
        await using var stream = r.OpenEntry(SourcePath);
        try { return await JsonSerializer.DeserializeAsync<SourceCatalog>(stream, PackageJson.Options, ct) ?? SourceCatalog.Empty; }
        catch (JsonException e) { throw new PackageFormatException($"{SourcePath} is not valid: {e.Message}"); }
    }

    private static string NameOf(string path)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        var dash = file.IndexOf('-');
        return dash >= 0 ? file[(dash + 1)..] : file;
    }

    private static void WriteEntry(PackageWriter w, string path, byte[] content)
    {
        using var s = w.CreateEntry(path);
        s.Write(content);
        s.Commit();
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Unsafe();
}
