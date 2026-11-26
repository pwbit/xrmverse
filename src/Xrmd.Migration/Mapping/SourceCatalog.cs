namespace Xrmd.Migration.Mapping;

/// <summary>
/// <c>map/source.json</c>: what the source organization looked like at export time, so that a map can be checked and
/// applied offline (tables, their columns, and the option labels of choice columns).
/// </summary>
public sealed record SourceCatalog(IReadOnlyList<SourceCatalogTable> Tables, IReadOnlyList<SourceCatalogOption> Options)
{
    public static SourceCatalog Empty { get; } = new([], []);
}

/// <param name="Ownership"><c>user</c> or <c>organization</c>.</param>
/// <param name="DisplayName">The table's display name as the source organization shows it; null when the source gave none (and in packages written before archive tables).</param>
public sealed record SourceCatalogTable(string LogicalName, string PrimaryKey, int? ObjectTypeCode, string Ownership, bool IsActivity,
    IReadOnlyList<SourceCatalogColumn> Columns, string? DisplayName = null);

/// <summary>
/// A column of a source table. The details after <paramref name="IsCustom"/> are what an archive table (spec §8.7) needs to recreate the
/// column; they are optional, absent in packages written before and for tables whose details the export did not read.
/// </summary>
/// <param name="DateBehavior"><c>UserLocal</c>, <c>DateOnly</c> or <c>TimeZoneIndependent</c> for a date column.</param>
/// <param name="OptionSetName">The name of the choice column's option set (its global name when <paramref name="IsGlobalOptionSet"/>).</param>
public sealed record SourceCatalogColumn(string LogicalName, string AttributeType, IReadOnlyList<string> Targets, bool IsCustom,
    string? DisplayName = null, int? MaxLength = null, int? Precision = null, double? MinValue = null, double? MaxValue = null,
    string? Format = null, string? DateBehavior = null, string? OptionSetName = null, bool IsGlobalOptionSet = false);

/// <param name="Label">The label in the organization's base language.</param>
public sealed record SourceCatalogOption(string Table, string Column, int Value, int? State, string? Label);
