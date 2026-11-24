using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata.Computed;
using Xrmd.Query.Sdk;

namespace Xrmd.Query.Sql;

/// <summary>
/// The SDK twin of <see cref="FormulaSqlCheck.Check(Xrmd.Model.Metadata.TableDefinition, Xrmd.Model.Metadata.ColumnDefinition, Func{string, Xrmd.Model.Metadata.TableDefinition?}, IComputedColumns, ISqlDialect)"/>
/// (S.2c, R1, R2): the SDK arguments become their models (a view is looked up, any other object converted afresh), and
/// <paramref name="computed"/> is called with the caller's own SDK instances, as before. Transitional: removed with the SDK view (S.2i).
/// </summary>
public static partial class FormulaSqlCheck
{
    /// <param name="entity">The table of the column (with the column, as it is being defined).</param>
    /// <param name="attribute">The formula column.</param>
    /// <param name="entities">Finds a table by logical name (the table being changed included).</param>
    /// <param name="computed">Translates the column and the computed columns it reads.</param>
    /// <param name="dialect">The SQL the column is compiled in (plan 2.7a).</param>
    /// <exception cref="XrmdValidationException">The SQL of the column is too large, holds too many applies or nests too deeply.</exception>
    public static void Check(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities, IComputedColumns computed, ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(computed);
        var (table, column, tables, translate) = SdkQueryCallbacks.Models(computed, entity, attribute, entities);
        Run(table, column, tables, translate, dialect);
    }
}
