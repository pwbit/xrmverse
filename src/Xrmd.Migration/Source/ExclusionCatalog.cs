using System.Text.Json;

namespace Xrmd.Migration.Source;

/// <summary>Why a table is not migrated: its category (spec §6.3) and the reason shown in the report.</summary>
public sealed record Exclusion(string Category, string Reason, bool Optional);

/// <summary>
/// The tables that make no sense in xrmverse or come with the schema (spec §6.3), from the embedded
/// <c>fixtures/migration/excluded-tables.json</c>: exact logical names, and prefixes for Microsoft first-party apps.
/// Tables of the category <c>not-supported-yet</c> are counted in the report, never exported.
/// </summary>
public static class ExclusionCatalog
{
    public const string NotSupportedYet = "not-supported-yet";

    private static readonly Lazy<(Dictionary<string, Exclusion> Exact, List<(string Prefix, Exclusion Exclusion)> Prefixes)> Data = new(Load);

    /// <summary>True for names of Microsoft first-party apps (<c>msdyn_</c>, <c>adx_</c>, …): tables, but also columns and option sets.</summary>
    public static bool IsFirstParty(string logicalName)
    {
        foreach (var (prefix, exclusion) in Data.Value.Prefixes)
            if (exclusion.Category == "first-party" && logicalName.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The first-party prefixes (<c>msdyn_</c>, <c>adx_</c>, …), for checks that need the names themselves.</summary>
    public static IReadOnlyList<string> FirstPartyPrefixes => FirstParty.Value;

    private static readonly Lazy<IReadOnlyList<string>> FirstParty = new(() =>
        Data.Value.Prefixes.Where(p => p.Exclusion.Category == "first-party").Select(p => p.Prefix).ToList());

    public static Exclusion? Find(string logicalName)
    {
        var (exact, prefixes) = Data.Value;
        if (exact.TryGetValue(logicalName, out var e)) return e;
        foreach (var (prefix, exclusion) in prefixes)
            if (logicalName.StartsWith(prefix, StringComparison.Ordinal)) return exclusion;
        return null;
    }

    private static (Dictionary<string, Exclusion>, List<(string, Exclusion)>) Load()
    {
        using var stream = typeof(ExclusionCatalog).Assembly.GetManifestResourceStream("Xrmd.Migration.ExcludedTables.json")
            ?? throw new InvalidOperationException("The exclusion catalog is not embedded.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var reasons = root.GetProperty("categories").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var optional = root.GetProperty("optional").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        var exact = new Dictionary<string, Exclusion>(StringComparer.Ordinal);
        foreach (var category in root.GetProperty("tables").EnumerateObject())
            foreach (var name in category.Value.EnumerateArray())
                exact[name.GetString()!] = new Exclusion(category.Name, reasons[category.Name], optional.Contains(name.GetString()!));
        var prefixes = new List<(string, Exclusion)>();
        foreach (var category in root.GetProperty("prefixes").EnumerateObject())
            foreach (var prefix in category.Value.EnumerateArray())
                prefixes.Add((prefix.GetString()!, new Exclusion(category.Name, reasons[category.Name], false)));
        return (exact, prefixes);
    }
}
