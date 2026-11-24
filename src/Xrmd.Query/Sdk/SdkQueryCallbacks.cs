using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;
using Xrmd.Model.Query;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Sdk;

/// <summary>
/// The calls of the compilers (on the model, S.2c) to the SDK-typed callback implemented in another module,
/// <see cref="IComputedColumns"/>. A table of the store is passed as its cached SDK view
/// (<see cref="SdkMetadataView.SdkView(TableDefinition)"/>), the instance <c>store.GetEntity(name)</c> returns, so that an
/// implementation's caches by instance keep working (Review Focus 4). Transitional: removed when the
/// callback takes the model (Core S.2e, Workflows S.2h).
/// </summary>
internal static class SdkQueryCallbacks
{
    /// <summary>
    /// Translates the calculated columns of the tables of <paramref name="store"/> (metadata snapshots, never changed once handed
    /// out): each table and column as its cached view, the other tables as <c>store.GetEntity</c> finds them.
    /// </summary>
    public static ComputedTranslation Published(IComputedColumns computed, IMetadataStore store) =>
        (table, column) => computed.Translate(table.SdkView(), table.SdkView(column), store.GetEntity);

    /// <summary>
    /// Translates the calculated columns of tables that may still change (a table being defined): each table is converted afresh,
    /// once for the life of the returned function, and <paramref name="tables"/> finds the others.
    /// </summary>
    public static ComputedTranslation Fresh(IComputedColumns computed, Func<string, TableDefinition?> tables)
    {
        var pairs = new SdkPairs(SdkMetadataBridge.ToSdk);
        Func<string, EntityMetadata?> entities = name => tables(name) is { } table ? pairs.Sdk(table) : null;
        return (table, column) => computed.Translate(pairs.Sdk(table), pairs.Sdk(table, column), entities);
    }

    /// <summary>
    /// The model of the SDK arguments of a twin that translates calculated columns (<see cref="Sql.FormulaSqlCheck"/>): the model of
    /// <paramref name="entity"/>, <paramref name="attribute"/> and of the tables <paramref name="entities"/> finds (a view is looked
    /// up, any other object converted afresh), and a translation that calls <paramref name="computed"/> with the caller's own SDK
    /// instances, as before the compilers were on the model.
    /// </summary>
    public static (TableDefinition Table, ColumnDefinition Column, Func<string, TableDefinition?> Tables, ComputedTranslation Translate)
        Models(IComputedColumns computed, EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities)
    {
        var pairs = new SdkPairs(table => throw new InvalidOperationException($"The table '{table.LogicalName}' has no SDK instance."));
        var table = pairs.Model(entity);
        var column = pairs.Model(entity, table, attribute);
        Func<string, TableDefinition?> tables = name => entities(name) is { } e ? pairs.Model(e) : null;
        return (table, column, tables, (t, c) => computed.Translate(pairs.Sdk(t), pairs.Sdk(t, c), entities));
    }

    /// <summary>
    /// The model and SDK instances of the tables and columns of one call, paired both ways by instance. A column is paired with the
    /// column at the same position of its table (a view and its model, and a conversion, keep the order of the columns).
    /// </summary>
    private sealed class SdkPairs(Func<TableDefinition, EntityMetadata> sdkOf)
    {
        private readonly Dictionary<TableDefinition, EntityMetadata> _sdk = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<EntityMetadata, TableDefinition> _models = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ColumnDefinition, AttributeMetadata> _columns = new(ReferenceEqualityComparer.Instance);

        public TableDefinition Model(EntityMetadata entity)
        {
            if (_models.TryGetValue(entity, out var table)) return table;
            table = entity.ToModel();
            _models[entity] = table;
            _sdk.TryAdd(table, entity);
            return table;
        }

        public ColumnDefinition Model(EntityMetadata entity, TableDefinition table, AttributeMetadata attribute)
        {
            var index = entity.Attributes is { } attributes ? Array.IndexOf(attributes, attribute) : -1;
            if (index >= 0 && table.Attributes is { } columns && index < columns.Count) return columns[index];
            var column = attribute.ToModel();
            _columns[column] = attribute;
            return column;
        }

        public EntityMetadata Sdk(TableDefinition table)
        {
            if (_sdk.TryGetValue(table, out var entity)) return entity;
            entity = sdkOf(table);
            _sdk[table] = entity;
            return entity;
        }

        public AttributeMetadata Sdk(TableDefinition table, ColumnDefinition column)
        {
            if (_columns.TryGetValue(column, out var attribute)) return attribute;
            var entity = Sdk(table);
            var index = table.Attributes?.IndexOf(column) ?? -1;
            attribute = index >= 0 && entity.Attributes is { } attributes && index < attributes.Length
                ? attributes[index]
                : SdkMetadataBridge.ToSdk(column);
            _columns[column] = attribute;
            return attribute;
        }
    }
}
