using System.Text.Json;
using Xrmd.Migration.Mapping;

namespace Xrmd.Migration.Source;

/// <summary>Builds <c>map/source.json</c> from the metadata an export already read (spec §7).</summary>
internal static class SourceCatalogBuilder
{
    /// <param name="tables">The exported data tables, with all their columns.</param>
    /// <param name="options">The choice and status values read for them.</param>
    /// <param name="languageCode">The <c>languagecode</c> of the organization: the label language the catalog keeps.</param>
    public static SourceCatalog Build(IEnumerable<SourceTable> tables, IEnumerable<SourceOption> optionValues, int languageCode)
    {
        var options = optionValues.ToList();
        var catalogTables = tables.OrderBy(t => t.LogicalName, StringComparer.Ordinal).ToList();
        var names = catalogTables.Select(t => t.LogicalName).ToHashSet(StringComparer.Ordinal);
        var sets = options.GroupBy(o => (o.Table, o.Column)).ToDictionary(g => g.Key, g => g.First());
        return new SourceCatalog(
            [.. catalogTables.Select(t => new SourceCatalogTable(t.LogicalName, t.PrimaryKey, t.ObjectTypeCode, t.Ownership, t.IsActivity,
                [.. t.Columns.Select(c => Column(t, c, sets))], t.DisplayName))],
            [.. options.Where(o => names.Contains(o.Table)).OrderBy(o => o.Table, StringComparer.Ordinal).ThenBy(o => o.Column, StringComparer.Ordinal).ThenBy(o => o.Value)
                .Select(o => new SourceCatalogOption(o.Table, o.Column, o.Value, o.State, LabelOf(o.Metadata, languageCode)))]);
    }

    private static SourceCatalogColumn Column(SourceTable table, SourceColumn c, Dictionary<(string Table, string Column), SourceOption> sets)
    {
        var d = c.Details;
        var set = sets.GetValueOrDefault((table.LogicalName, c.LogicalName));
        return new SourceCatalogColumn(c.LogicalName, c.AttributeType, c.Targets, c.IsCustom, c.DisplayName, d?.MaxLength, d?.Precision, d?.MinValue, d?.MaxValue,
            d?.Format, c.DateBehavior, set?.OptionSet ?? set?.SetName, set?.OptionSet is not null);
    }

    /// <summary>The label of an option in <paramref name="languageCode"/>; the label the user sees, or the first one, when the organization language has none.</summary>
    internal static string? LabelOf(JsonElement option, int languageCode) =>
        option.ValueKind == JsonValueKind.Object && option.TryGetProperty("Label", out var label) ? LabelOfLabel(label, languageCode) : null;

    /// <summary>The text of a Dataverse <c>Label</c> object in <paramref name="languageCode"/>; see <see cref="LabelOf"/> for the fallbacks.</summary>
    internal static string? LabelOfLabel(JsonElement label, int languageCode)
    {
        if (label.ValueKind != JsonValueKind.Object) return null;        string? first = null;
        if (label.TryGetProperty("LocalizedLabels", out var all) && all.ValueKind == JsonValueKind.Array)
            foreach (var l in all.EnumerateArray())
            {
                var text = l.TryGetProperty("Label", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                first ??= text;
                if (l.TryGetProperty("LanguageCode", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() == languageCode) return text;
            }
        if (first is not null) return first;
        return label.TryGetProperty("UserLocalizedLabel", out var user) && user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("Label", out var ut) && ut.ValueKind == JsonValueKind.String ? ut.GetString() : null;
    }
}
