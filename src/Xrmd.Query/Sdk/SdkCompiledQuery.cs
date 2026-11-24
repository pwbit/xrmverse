using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Query.Sql;

// The SDK twins of the model members of the compiled query (S.2c, R1): its callers outside Xrmd.Query still read the SDK until they
// move to the model (Core S.2e). Transitional: removed with the SDK view (S.2i).

public sealed partial record ResultColumn
{
    /// <summary>
    /// The SDK view of <see cref="Column"/>: the column of the cached view of its table (the instance <c>store.GetEntity(name)</c>
    /// returns), as the compiler read it before it was on the model; null with <see cref="Column"/>.
    /// </summary>
    public AttributeMetadata? Attribute { get; private init; }

    /// <summary>
    /// This column with <see cref="Attribute"/> set from <paramref name="table"/>, the table of <see cref="Column"/>, through the
    /// pairs of one compilation (<paramref name="views"/>).
    /// </summary>
    internal ResultColumn WithView(ResultColumnViews views, TableDefinition table) =>
        Column is null ? this : this with { Attribute = views.Of(table, Column) };
}

/// <summary>
/// The SDK views of the columns of one compilation: each table's model columns paired by position with the columns of its cached
/// view once, so that a wide <c>AllColumns</c> query does not search the table for every column. The pairs are those of
/// <see cref="SdkMetadataView.SdkView(TableDefinition, ColumnDefinition)"/>: a column not found by instance falls back to it.
/// </summary>
internal sealed class ResultColumnViews
{
    private readonly Dictionary<TableDefinition, Dictionary<ColumnDefinition, AttributeMetadata>> _tables =
        new(ReferenceEqualityComparer.Instance);

    public AttributeMetadata Of(TableDefinition table, ColumnDefinition column)
    {
        if (!_tables.TryGetValue(table, out var pairs))
        {
            pairs = new Dictionary<ColumnDefinition, AttributeMetadata>(ReferenceEqualityComparer.Instance);
            if (table.Attributes is { } columns && table.SdkView().Attributes is { } views)
                for (var i = 0; i < columns.Count && i < views.Length; i++)
                    pairs.TryAdd(columns[i], views[i]); // the first position wins, as List.IndexOf
            _tables[table] = pairs;
        }
        return pairs.TryGetValue(column, out var view) ? view : table.SdkView(column);
    }
}

public sealed partial record CompiledQuery
{
    /// <summary>The SDK view of <see cref="Table"/>: the instance <c>store.GetEntity(name)</c> returns for it.</summary>
    public EntityMetadata Root => Table.SdkView();
}
