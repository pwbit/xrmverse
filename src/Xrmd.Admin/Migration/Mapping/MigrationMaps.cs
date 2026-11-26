using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Packaging;

namespace Xrmd.Admin.Migration.Mapping;

/// <summary>
/// Which maps an import run uses (migration table mapping spec §8.1): the maps of its start request, kept in the run option
/// <see cref="OptionName"/>, replace the package's for the whole run (resume included); without them, the package's own. The one
/// place that shows a package through its maps (<see cref="AttachAsync"/>).
/// </summary>
public static class MigrationMaps
{
    /// <summary>The run option that holds the maps of the start request: a JSON array of YAML texts.</summary>
    public const string OptionName = "maps";

    /// <summary>Parses and combines map texts, each named <c>map &lt;n&gt;</c> (from 1).</summary>
    /// <exception cref="MapFormatException">A text is not a valid map; the message starts with its name.</exception>
    /// <exception cref="MapValidationException">The maps break the offline rules together.</exception>
    public static MapSet Parse(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var files = new List<MapFile>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var name = $"map {i + 1}";
            try { files.Add(MapReader.Parse(texts[i], name)); }
            catch (MapFormatException e) { throw new MapFormatException($"{name}: {e.Message}", e); }
        }
        return MapSet.Combine(files);
    }

    /// <summary>
    /// The run option that keeps the package's map texts when the start request had no maps (table mapping spec §8.7): the report shows
    /// the maps a run used, so that a dry run's choices can be saved with them. Not read by the phases (they read the package).
    /// </summary>
    public const string PackageOptionName = "packageMaps";

    /// <summary>The map texts a run used: those of the request, else the package's as kept at the start; empty without maps.</summary>
    public static IReadOnlyList<string> UsedTexts(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (OptionTexts(options) is { } texts) return texts;
        if (!options.TryGetValue(PackageOptionName, out var json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>The map texts stored in <paramref name="package"/>, in manifest order.</summary>
    public static async Task<List<string>> PackageTextsAsync(PackageReader package, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        var texts = new List<string>();
        foreach (var path in package.Manifest.Maps)
        {
            await using var stream = package.OpenEntry(path);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            texts.Add(await reader.ReadToEndAsync(ct));
        }
        return texts;
    }

    /// <summary>The value of the run option for <paramref name="texts"/>.</summary>
    public static string ToOption(IEnumerable<string> texts) => new JsonArray([.. texts.Select(t => (JsonNode)t)]).ToJsonString();

    /// <summary>The map texts of the run's options; null when the run has none (it uses the package's maps).</summary>
    /// <exception cref="MapFormatException">The option is not a JSON array of strings.</exception>
    public static IReadOnlyList<string>? OptionTexts(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.TryGetValue(OptionName, out var json)) return null;
        string?[]? texts;
        try { texts = JsonSerializer.Deserialize<string?[]>(json); }
        catch (JsonException e) { throw new MapFormatException($"The run option '{OptionName}' is not a JSON array of map texts: {e.Message}", e); }
        if (texts is null || texts.Any(t => t is null)) throw new MapFormatException($"The run option '{OptionName}' is not a JSON array of map texts.");
        return texts!;
    }

    /// <summary>The maps of the run: those of its options when it has some, else the package's (empty for a package without maps).</summary>
    public static async Task<MapSet> LoadAsync(PackageReader package, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (OptionTexts(options) is { } texts) return Parse(texts);
        var (files, _) = await PackageMaps.ReadAsync(package, ct);
        return MapSet.Combine(files);
    }

    /// <summary>
    /// Shows <paramref name="package"/> under the target names of the run's maps: sets its <see cref="PackageReader.Translator"/>
    /// (record translator on the organization's metadata, derived values read from the package). Nothing happens without maps.
    /// </summary>
    public static async Task AttachAsync(PackageReader package, IReadOnlyDictionary<string, string> options, IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var maps = await LoadAsync(package, options, ct);
        if (maps.IsEmpty) return;
        var catalog = await PackageMaps.ReadCatalogAsync(package, ct);
        var derived = await DerivedIndex.BuildAsync(package, maps, catalog, ct);
        var metadata = services.GetRequiredService<IMetadataStore>();
        var target = new MetadataMapTarget(metadata);
        // Spec §8.7: a lookup of an archive table to a table the package's solutions or delta create is kept, in the census and the
        // dry run as by the schema phase (before the schema phase that table does not exist yet).
        var planned = maps.Tables.Any(t => t.Archive) ? await DryRunPreview.PlannedTablesAsync(package, maps, metadata, null, ct) : [];
        package.Translator = new MapPackageTranslator(new RecordTranslator(maps, catalog, target, derived, planned.Contains));
    }
}
