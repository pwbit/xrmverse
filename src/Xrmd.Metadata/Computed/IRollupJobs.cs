namespace Xrmd.Metadata.Computed;

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
