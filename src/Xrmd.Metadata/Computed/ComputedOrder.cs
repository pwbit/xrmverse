using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;

namespace Xrmd.Metadata.Computed;

/// <summary>
/// The order of a set of computed columns, calculated (<c>SourceType</c> 1, plan 2.12a) and rollup (<c>SourceType</c> 2, plan 2.12b),
/// by what they read of one another (rulings R14 and Review Focus 5): a solution import creates a column after the ones it reads (a
/// rollup after the basic calculated columns it aggregates or filters on, a calculated column after the rollups it reads), an uninstall
/// deletes it before them. A column reads another one when its definition names it, or one of the companions the server generates
/// for it (a rollup's <c>_date</c>, <c>_state</c>, a Currency column's <c>_base</c>).
/// </summary>
/// <remarks>
/// A definition that cannot be translated (a column, a table or a relationship it names is missing, a refused shape) adds no
/// constraint: whoever writes the column next gets the store's own answer for it, naming the column. A cycle, which the definition
/// rules refuse, falls back to the order given.
/// </remarks>
public static class ComputedOrder
{
    /// <summary>True for a calculated, formula or rollup column (not a rollup's <c>_base</c>).</summary>
    public static bool IsComputed(AttributeMetadata a) =>
        a is not null && (ComputedColumnRules.IsComputed(a) || RollupColumns.IsRollup(a));

    /// <summary>
    /// <paramref name="columns"/> (table, column) ordered so that a column comes after every other one of the list it reads
    /// (<paramref name="readersLast"/>, an import) or before every one it reads (an uninstall); otherwise in the order given.
    /// <paramref name="entities"/> finds a table with its columns (the ones being ordered included), <paramref name="relationships"/> a
    /// one-to-many relationship by SchemaName.
    /// </summary>
    public static IReadOnlyList<(string Entity, string Attribute)> Order(IReadOnlyList<(string Entity, string Attribute)> columns,
        Func<string, EntityMetadata?> entities, Func<string, OneToManyRelationshipMetadata?> relationships, IComputedColumns? computed,
        IRollupColumns? rollups, bool readersLast)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(relationships);
        if (columns.Count < 2) return columns.ToList();
        var keys = columns.Select(c => Key(c.Entity, c.Attribute)).ToList();
        var reads = columns.Select(c => Reads(c.Entity, c.Attribute, entities, relationships, computed, rollups)).ToList();

        // reads[i] holds keys[j]: column i reads column j.
        var remaining = Enumerable.Range(0, columns.Count).ToList();
        var order = new List<(string, string)>();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(i => !remaining.Any(j => j != i && (readersLast
                    ? reads[i].Contains(keys[j]) // i waits for every column it reads
                    : reads[j].Contains(keys[i]))), // i waits for every column that reads it
                -1);
            if (next < 0) next = remaining[0];
            remaining.Remove(next);
            order.Add(columns[next]);
        }
        return order;
    }

    /// <summary>
    /// The columns (<c>table.column</c>) the definition of <paramref name="entity"/>.<paramref name="attribute"/> reads, a companion also
    /// under the name of the column it belongs to (see <see cref="Add"/>); empty for a column that is not computed or whose definition
    /// cannot be translated.
    /// </summary>
    private static HashSet<string> Reads(string entity, string attribute, Func<string, EntityMetadata?> entities,
        Func<string, OneToManyRelationshipMetadata?> relationships, IComputedColumns? computed, IRollupColumns? rollups)
    {
        var reads = new HashSet<string>(StringComparer.Ordinal);
        var table = entities(entity);
        var column = table?.Attributes?.FirstOrDefault(a => a.LogicalName == attribute);
        if (table is null || column is null) return reads;
        try
        {
            if (ComputedColumnRules.IsComputed(column) && computed is not null)
            {
                foreach (var source in computed.Translate(table, column, entities).Sources)
                {
                    var owner = source.Lookup is null ? entity
                        : table.Attributes?.OfType<LookupAttributeMetadata>().FirstOrDefault(l => l.LogicalName == source.Lookup)?.Targets is [var target]
                            ? target : null;
                    if (owner is not null) Add(reads, owner, source.Attribute, entities);
                }
            }
            else if (RollupColumns.IsRollup(column) && rollups is not null)
            {
                var definition = rollups.Translate(table, column, entities, relationships);
                foreach (var source in RollupColumnRules.Reads(entity, definition, relationships)) Add(reads, source.Entity, source.Attribute, entities);
            }
        }
        catch (Exception ex) when (ex is XrmdValidationException or XrmdFaultException)
        {
            // No constraint: the store answers for this definition when the column is written.
        }
        return reads;
    }

    /// <summary>
    /// Adds <paramref name="entity"/>.<paramref name="attribute"/>, and its main column when it is a companion the server generates: a
    /// rollup's <c>_date</c> or <c>_state</c>, the <c>_base</c> of a computed Currency column. A column of its own that only has such a
    /// name (a simple <c>xv_start_date</c>) is not one. A companion not generated yet (its rollup or Currency column is being created)
    /// is known by its main column.
    /// </summary>
    private static void Add(HashSet<string> reads, string entity, string attribute, Func<string, EntityMetadata?> entities)
    {
        reads.Add(Key(entity, attribute));
        var columns = entities(entity)?.Attributes ?? [];
        AttributeMetadata? Find(string name) => columns.FirstOrDefault(a => a.LogicalName == name);
        string? Main(string suffix) => attribute.EndsWith(suffix, StringComparison.Ordinal) && attribute.Length > suffix.Length
            ? attribute[..^suffix.Length] : null;

        var main = Find(attribute) switch
        {
            { } companion when RollupColumns.IsCompanion(companion) => companion.AttributeOf,
            MoneyAttributeMetadata { IsBaseCurrency: true } => Main(TypeMapping.BaseColumnSuffix) is { } money && Find(money) is { } m && IsComputed(m)
                ? money : null,
            null => (Main(RollupColumns.DateSuffix) ?? Main(RollupColumns.StateSuffix)) is { } rollup && Find(rollup) is { } r && RollupColumns.IsRollup(r)
                ? rollup
                : Main(TypeMapping.BaseColumnSuffix) is { } money && Find(money) is MoneyAttributeMetadata m && IsComputed(m) ? money : null,
            _ => null,
        };
        if (main is not null) reads.Add(Key(entity, main));
    }

    private static string Key(string entity, string attribute) => $"{entity}.{attribute}";
}
