using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Query.Sql;

/// <summary>
/// The SDK twins of <see cref="RollupSql"/> (S.2c, R1, R2): the table and the rollup column become their models (a view is looked up,
/// any other object converted afresh). Transitional: removed with the SDK view (S.2i).
/// </summary>
/// <remarks>
/// A table that is not a view of the store is converted to a fresh model, so a calculated column the rollup reads reaches
/// <see cref="IComputedColumns"/> as the SDK view of that fresh model, not as the caller's own object. Production passes store
/// views (<c>store.GetEntity</c>), whose model is the store's own table.
/// </remarks>
public sealed partial class RollupSql
{
    /// <summary>The SDK twin of <see cref="Recalculate(Xrmd.Model.Metadata.TableDefinition, Xrmd.Model.Metadata.ColumnDefinition, RollupDefinition, RollupScope, DateTime)"/>.</summary>
    /// <exception cref="XrmdValidationException">A definition this translation cannot write (refused at definition time otherwise).</exception>
    public RollupCommand Recalculate(EntityMetadata table, AttributeMetadata rollup, RollupDefinition definition, RollupScope scope, DateTime nowUtc) =>
        Recalculate(table?.ToModel()!, rollup?.ToModel()!, definition, scope, nowUtc);

    /// <summary>The SDK twin of <see cref="Measure(Xrmd.Model.Metadata.TableDefinition, Xrmd.Model.Metadata.ColumnDefinition, RollupDefinition, Guid, int)"/>.</summary>
    public RollupCommand Measure(EntityMetadata table, AttributeMetadata rollup, RollupDefinition definition, Guid id, int maxDepth) =>
        Measure(table?.ToModel()!, rollup?.ToModel()!, definition, id, maxDepth);

    /// <summary>
    /// Refuses every calculated column: used when the server has no calculated column translator. It implements the SDK-typed
    /// <see cref="IComputedColumns"/> (Xrmd.Metadata's <c>Sdk/</c> until S.2h).
    /// </summary>
    private sealed class NoComputedColumns : IComputedColumns
    {
        public ComputedDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities) =>
            throw new XrmdValidationException(
                $"The column '{entity.LogicalName}.{attribute.LogicalName}' is calculated, and calculated columns are not available on this server.");
    }
}
