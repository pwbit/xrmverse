using Meziantou.Framework.Tds.Handler;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Sdk;
using Xrmd.Query.Model;
using Xrmd.Tds.Translation;

namespace Xrmd.Tds.Execution;

/// <summary>
/// Shapes the rows of a <see cref="TranslatedQuery"/>, as the query executor returns them, into a TDS result set with the column
/// types of ruling T7: text and memo <c>nvarchar</c>, whole number <c>int</c>, big integer <c>bigint</c>, decimal <c>decimal</c>,
/// float <c>float</c>, currency <c>money</c>, two options <c>bit</c>, date and time <c>datetime2</c> in UTC, unique identifier and
/// lookup <c>uniqueidentifier</c>, choice, state and status <c>int</c>, names and labels <c>nvarchar</c>; an aggregate has the type
/// of its source (a count is an <c>int</c>, the average of whole numbers a <c>decimal</c>). Every column is nullable; a missing
/// value is <c>null</c>, never <see cref="DBNull"/>: the TDS server library writes a <c>null</c> as SQL NULL for every type, but
/// fails on <see cref="DBNull"/> for any type other than <c>nvarchar</c> (it converts it to the column's type).
/// </summary>
public static class ResultSetBuilder
{
    /// <summary>Builds the result set of <paramref name="columns"/> (columns of <paramref name="model"/>) over <paramref name="rows"/>.</summary>
    public static TdsResultSet Build(QueryModel model, IReadOnlyList<OutputColumn> columns, IEnumerable<Entity> rows, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(store);

        var readers = columns.Select(c => Reader(model, c, store)).ToList();
        var set = new TdsResultSet();
        for (var i = 0; i < columns.Count; i++) set.Columns.Add(Column(columns[i].Name, i, readers[i].Type));
        foreach (var row in rows)
        {
            var values = new object?[readers.Count];
            for (var i = 0; i < readers.Count; i++) values[i] = readers[i].Read(row);
            set.Rows.Add(values);
        }
        return set;
    }

    /// <summary>
    /// <paramref name="columns"/> less the columns of a <c>*</c> that are in <paramref name="unreadableOf"/> their table (ruling T7:
    /// <c>*</c> lists the readable columns only). A column named in the select list stays: the executor returns it null.
    /// </summary>
    /// <param name="unreadableOf">The secured columns the caller cannot read, by table logical name (asked only for the tables of
    /// <c>*</c> columns).</param>
    public static IReadOnlyList<OutputColumn> WithoutUnreadableStarColumns(QueryModel model, IReadOnlyList<OutputColumn> columns,
        Func<string, IReadOnlySet<string>> unreadableOf)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(unreadableOf);
        return columns.Where(c => !c.FromStar || !unreadableOf(EntityName(model, c.EntityAlias)).Contains(c.Attribute)).ToList();
    }

    /// <summary>The logical name of the table whose exposed name in the query is <paramref name="exposed"/>.</summary>
    public static string EntityName(QueryModel model, string exposed)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Node(model, exposed).Node.EntityName;
    }

    /// <summary>
    /// A nullable column. SQL Server sends a column without a name (an aggregate or a system function without alias) with an empty
    /// name, which the TDS server library refuses: it is named <c>Column&lt;position&gt;</c>, as ADO.NET and Power BI name it.
    /// </summary>
    internal static TdsColumn Column(string name, int index, TdsColumnType type) =>
        new(string.IsNullOrEmpty(name) ? "Column" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : name, type, true);

    /// <summary>The SQL type of an attribute's raw value (ruling T7).</summary>
    internal static TdsColumnType TypeOf(AttributeMetadata attribute) => attribute switch
    {
        StringAttributeMetadata or MemoAttributeMetadata => TdsColumnType.NVarChar,
        IntegerAttributeMetadata => TdsColumnType.Int32,
        BigIntAttributeMetadata => TdsColumnType.Int64,
        DecimalAttributeMetadata => TdsColumnType.Decimal,
        DoubleAttributeMetadata => TdsColumnType.Double,
        MoneyAttributeMetadata => TdsColumnType.Money,
        BooleanAttributeMetadata => TdsColumnType.Boolean,
        DateTimeAttributeMetadata => TdsColumnType.DateTime2,
        UniqueIdentifierAttributeMetadata or LookupAttributeMetadata => TdsColumnType.Guid,
        PicklistAttributeMetadata or StateAttributeMetadata or StatusAttributeMetadata => TdsColumnType.Int32,
        _ => TdsColumnType.NVarChar,
    };

    /// <summary>The SQL value of an executor value: the id of a lookup, the number of a choice, the amount of money, UTC dates.</summary>
    internal static object? SqlValue(object? value) => value switch
    {
        null or DBNull => null,
        AliasedValue aliased => SqlValue(aliased.Value),
        EntityReference reference => reference.Id,
        OptionSetValue option => option.Value,
        Money money => money.Value,
        // As EntityRowMapper: a local time is converted, an unspecified one is the stored UTC value.
        DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        DateTime date => DateTime.SpecifyKind(date, DateTimeKind.Utc),
        _ => value,
    };

    private sealed record ColumnReader(TdsColumnType Type, Func<Entity, object?> Read);

    private static ColumnReader Reader(QueryModel model, OutputColumn column, IMetadataStore store)
    {
        if (column.Value == OutputValue.Aggregate)
        {
            var aggregate = FindAggregate(model.Root, column.Attribute)
                            ?? throw new InvalidOperationException($"The query has no aggregate column '{column.Attribute}'.");
            var source = Attribute(Entity(store, aggregate.Owner), aggregate.Node.Attribute);
            var type = aggregate.Node switch
            {
                { GroupBy: true } => TypeOf(source),
                { Aggregate: XrmAggregate.Count or XrmAggregate.CountColumn } => TdsColumnType.Int32,
                { Aggregate: XrmAggregate.Avg } when source is IntegerAttributeMetadata or BigIntAttributeMetadata => TdsColumnType.Decimal,
                _ => TypeOf(source),
            };
            var key = column.Attribute;
            return new ColumnReader(type, row => SqlValue(Get(row, key)));
        }

        var (node, isRoot) = Node(model, column.EntityAlias);
        var attribute = Attribute(Entity(store, node), column.Attribute);
        // Root attributes are keyed by their logical name, link-entity ones by "<alias>.<attribute>" (the executor's result names).
        var resultName = isRoot ? attribute.LogicalName : $"{((LinkNode)node).Alias}.{attribute.LogicalName}";
        return column.Value switch
        {
            OutputValue.Name => new ColumnReader(TdsColumnType.NVarChar,
                row => (Unwrap(Get(row, resultName)) as EntityReference)?.Name),
            OutputValue.Label => new ColumnReader(TdsColumnType.NVarChar,
                row => Unwrap(Get(row, resultName)) is null
                    ? null
                    : row.FormattedValues.TryGetValue(resultName, out var label) ? label : null),
            _ => new ColumnReader(TypeOf(attribute), row => SqlValue(Get(row, resultName))),
        };
    }

    private static EntityMetadata Entity(IMetadataStore store, EntityNode node) =>
        store.GetEntity(node.EntityName) ?? throw new InvalidOperationException($"The table '{node.EntityName}' of the query has no metadata.");

    private static object? Get(Entity row, string key) => row.Attributes.TryGetValue(key, out var value) ? value : null;

    private static object? Unwrap(object? value) => value is AliasedValue aliased ? aliased.Value : value;

    private static AttributeMetadata Attribute(EntityMetadata entity, string name) =>
        entity.Attributes?.FirstOrDefault(a => string.Equals(a.LogicalName, name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"The table '{entity.LogicalName}' has no column '{name}'.");

    /// <summary>
    /// The node of the table whose exposed name is <paramref name="exposed"/>: the link with that alias, else the root (whose
    /// exposed name no link has).
    /// </summary>
    private static (EntityNode Node, bool IsRoot) Node(QueryModel model, string exposed)
    {
        static LinkNode? Find(EntityNode node, string alias)
        {
            foreach (var link in node.Links)
            {
                if (string.Equals(link.Alias, alias, StringComparison.Ordinal)) return link;
                if (Find(link, alias) is { } found) return found;
            }
            return null;
        }

        return Find(model.Root, exposed) is { } link ? (link, false) : (model.Root, true);
    }

    private static (EntityNode Owner, ColumnNode Node)? FindAggregate(EntityNode node, string alias)
    {
        if (node.Columns.FirstOrDefault(c => string.Equals(c.Alias, alias, StringComparison.Ordinal)) is { } column) return (node, column);
        foreach (var link in node.Links)
            if (FindAggregate(link, alias) is { } found) return found;
        return null;
    }
}
