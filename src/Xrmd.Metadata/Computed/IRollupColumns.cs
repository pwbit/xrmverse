using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata.Computed;

/// <summary>
/// Turns the <c>FormulaDefinition</c> (XAML) of a rollup column into a <see cref="RollupDefinition"/> and checks its shape and
/// relationships (plan 2.12b, rulings R3, R4). Implemented outside the metadata layer (it needs the XAML reader);
/// <see cref="MetadataStore"/> works without one and then refuses rollup columns.
/// </summary>
public interface IRollupColumns
{
    /// <summary>
    /// Translates and validates the definition of <paramref name="attribute"/> of <paramref name="entity"/>; <paramref name="entities"/>
    /// finds a table by logical name (the table being changed included), <paramref name="relationships"/> a one-to-many relationship
    /// by SchemaName. Implementations cache by (entity, attribute, definition text).
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 with the rollup error code of the refused shape.</exception>
    /// <exception cref="Xrmd.Abstractions.XrmdValidationException">The definition is not valid.</exception>
    RollupDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities,
        Func<string, OneToManyRelationshipMetadata?> relationships);
}

/// <summary>What happened to the definition of a rollup column.</summary>
public enum RollupChange
{
    Added,
    Updated,
    Deleted,
    Imported,
}

/// <summary>
/// The recalculation jobs of rollup columns (plan 2.12b, ruling R8): told, after the metadata change is committed, that a rollup
/// column was added, updated, deleted or imported. <see cref="MetadataStore"/> logs a failure and goes on (the jobs are reconciled
/// at host start, ruling R9).
/// </summary>
public interface IRollupJobs
{
    Task DefinitionChangedAsync(string entity, string attribute, RollupChange change, CancellationToken ct);
}
