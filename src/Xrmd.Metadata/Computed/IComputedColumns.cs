using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata.Computed;

/// <summary>
/// Turns the <c>FormulaDefinition</c> of a calculated column into a <see cref="ComputedDefinition"/> and checks it (plan 2.12a).
/// Implemented outside the metadata layer (it needs the XAML reader); <see cref="MetadataStore"/> works without one and then
/// refuses calculated columns.
/// </summary>
public interface IComputedColumns
{
    /// <summary>
    /// Translates and validates the definition of <paramref name="attribute"/> of <paramref name="entity"/>; <paramref name="entities"/>
    /// finds a table by logical name (the table being changed included). Implementations cache by (entity, attribute, definition text).
    /// </summary>
    /// <exception cref="Xrmd.Abstractions.XrmdValidationException">The definition is not valid.</exception>
    ComputedDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities);

    /// <summary>
    /// <see cref="Translate"/> when a column is defined (created, updated or imported), with the checks too costly for every read: for
    /// a formula column, the SQL a query reading it would hold is compiled and refused when too large (plan 2.12c, final review C1).
    /// The default is <see cref="Translate"/>.
    /// </summary>
    /// <exception cref="Xrmd.Abstractions.XrmdValidationException">The definition is not valid.</exception>
    ComputedDefinition Check(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities) =>
        Translate(entity, attribute, entities);
}
