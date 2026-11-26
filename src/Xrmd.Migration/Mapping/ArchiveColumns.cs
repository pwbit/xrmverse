namespace Xrmd.Migration.Mapping;

/// <summary>What an archive table (spec §8.7) does with one column of its source table.</summary>
public enum ArchiveColumnKind
{
    /// <summary>The primary key (the id becomes the archive's key), <c>activityid</c>, <c>activitytypecode</c>, a money column's <c>_base</c>: the server's.</summary>
    Key,

    /// <summary>Left out by the map (<c>target: null</c>, or a custom column under <c>customColumns: drop</c>).</summary>
    Dropped,

    /// <summary>A column every table (or every activity) of xrmverse has: written under its own name, never created.</summary>
    ByName,

    /// <summary>Created as <see cref="ArchiveColumn.Target"/> with the source's type, values copied as they are.</summary>
    Copied,

    /// <summary>A lookup whose tables are all in the target (present, mapped or archived): created as a lookup <see cref="ArchiveColumn.Target"/>.</summary>
    Lookup,

    /// <summary>A lookup the archive cannot keep: created as the text column <see cref="ArchiveColumn.TextTarget"/>.</summary>
    LookupAsText,

    /// <summary>A type an archive table does not hold (an image, a file, a unique identifier …): left out, counted.</summary>
    Unsupported,
}

/// <param name="Target">The archive column (for <see cref="ArchiveColumnKind.Lookup"/> the lookup); null when nothing is written.</param>
/// <param name="TextTarget">For a lookup (kept or not): the text column it becomes when its tables are not all in the target.</param>
/// <param name="IsMultiSelect">A <c>Virtual</c> source column with options: a multi-select choice.</param>
public sealed record ArchiveColumn(SourceCatalogColumn Source, ArchiveColumnKind Kind, string? Target, string? TextTarget = null, bool IsMultiSelect = false);

/// <summary>
/// The columns of an archive table, derived from the source catalog and the map entry. One plan for the schema phase, which creates
/// them, and the record translator, which writes into them; both name them through <see cref="ArchiveNames"/>.
/// </summary>
public static class ArchiveColumns
{
    /// <summary>The source types an archive column is created with (the source's <c>AttributeType</c>).</summary>
    public static IReadOnlySet<string> CopiedTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "String", "Memo", "Integer", "BigInt", "Decimal", "Double", "Money", "Boolean", "DateTime", "Picklist", "State", "Status",
    };

    private static readonly HashSet<string> LookupTypes = new(StringComparer.Ordinal) { "Lookup", "Customer", "Owner" };

    /// <summary>The tables a customer column refers to; an archive keeps one only when it refers to exactly these.</summary>
    public static IReadOnlyList<string> CustomerTargets { get; } = ["account", "contact"];

    /// <summary>
    /// The plan of every column of <paramref name="table"/>, in the catalog's order, for the archive entry <paramref name="entry"/>.
    /// </summary>
    /// <param name="present">For a source table name: true when its rows have a table in the target (present there, mapped, or archived).</param>
    public static IReadOnlyList<ArchiveColumn> Plan(MapTable entry, SourceCatalogTable table, SourceCatalog catalog, Func<string, bool> present)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(present);
        var withOptions = catalog.Options.Where(o => o.Table == table.LogicalName).Select(o => o.Column).ToHashSet(StringComparer.Ordinal);
        var money = table.Columns.Where(c => c.AttributeType == "Money").Select(c => c.LogicalName + "_base").ToHashSet(StringComparer.Ordinal);
        return [.. table.Columns.Select(c => Of(entry, table, c, withOptions.Contains(c.LogicalName), money.Contains(c.LogicalName), present))];
    }

    private static ArchiveColumn Of(MapTable entry, SourceCatalogTable table, SourceCatalogColumn column, bool hasOptions, bool isMoneyBase,
        Func<string, bool> present)
    {
        var name = column.LogicalName;
        if (name == table.PrimaryKey || name is "activityid" or "activitytypecode" || isMoneyBase) return new(column, ArchiveColumnKind.Key, null);
        entry.Columns.TryGetValue(name, out var listed);
        if (listed is { Drop: true }) return new(column, ArchiveColumnKind.Dropped, null);
        // customColumns: drop leaves out the custom columns the entry does not list, as on any other entry.
        if (listed is null && !entry.CarryCustomColumns && RecordTranslator.IsCustomColumn(column)) return new(column, ArchiveColumnKind.Dropped, null);
        if (listed is null && IsByName(table, column)) return new(column, ArchiveColumnKind.ByName, name);

        var target = listed?.Target ?? ArchiveNames.Column(name);
        if (LookupTypes.Contains(column.AttributeType))
        {
            var text = listed is null ? ArchiveNames.LookupText(name) : ArchiveNames.Fit(listed.Target + "name", 5);
            return new(column, KeepsLookup(column, present) ? ArchiveColumnKind.Lookup : ArchiveColumnKind.LookupAsText, target, text);
        }
        if (CopiedTypes.Contains(column.AttributeType)) return new(column, ArchiveColumnKind.Copied, target);
        if (column.AttributeType == "Virtual" && hasOptions) return new(column, ArchiveColumnKind.Copied, target, IsMultiSelect: true);
        return new(column, ArchiveColumnKind.Unsupported, null);
    }

    /// <summary>
    /// The system columns every table has (spec §6.1) and, of an activity, the columns every activity has and its party lists: an archive
    /// table of the same kind has them too.
    /// </summary>
    private static bool IsByName(SourceCatalogTable table, SourceCatalogColumn column) =>
        RecordTranslator.SystemColumns.Contains(column.LogicalName)
        || (table.IsActivity && (RecordTranslator.ActivityColumns.Contains(column.LogicalName) || column.AttributeType == "PartyList"));

    /// <summary>
    /// A lookup is kept when the archive can create it: one table (a lookup), or exactly account and contact (a customer column), each
    /// with a table in the target.
    /// </summary>
    public static bool KeepsLookup(SourceCatalogColumn column, Func<string, bool> present)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(present);
        var targets = column.Targets.Distinct(StringComparer.Ordinal).ToList();
        return column.AttributeType switch
        {
            "Lookup" => targets.Count == 1 && present(targets[0]),
            "Customer" => targets.Order(StringComparer.Ordinal).SequenceEqual(CustomerTargets) && targets.All(present),
            _ => false,
        };
    }
}
