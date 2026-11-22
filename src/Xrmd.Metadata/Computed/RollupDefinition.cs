namespace Xrmd.Metadata.Computed;

/// <summary>The aggregate function of a rollup column (plan 2.12b, Global Constraints).</summary>
public enum RollupAggregate
{
    Sum,
    Count,
    Min,
    Max,
    Avg,
}

/// <summary>The values of a rollup column's <c>&lt;column&gt;_state</c> companion (plan 2.12b, Global Constraints).</summary>
public enum RollupState
{
    NotCalculated = 0,
    Calculated = 1,
    OverflowError = 2,
    OtherError = 3,
    RetryLimitExceeded = 4,
    HierarchicalRecursionLimitReached = 5,
    LoopDetected = 6,
    CurrencyMissing = 7,
}

/// <summary>
/// The related rows a rollup aggregates: the rows of <paramref name="Entity"/> whose lookup <paramref name="Lookup"/> points to the
/// rollup's row through the one-to-many relationship <paramref name="Relationship"/> (its SchemaName), kept when
/// <paramref name="Filter"/> holds (null: every row). The filter reads columns of the related row (<c>ComputedExpr.Column(null, ...)</c>).
/// </summary>
public sealed record RollupRelated(string Relationship, string Entity, string Lookup, ComputedCondition? Filter);

/// <summary>A column a rollup definition reads: the aggregated column, a filter column or a lookup it follows.</summary>
public sealed record RollupSource(string Entity, string Attribute);

/// <summary>
/// A translated rollup definition (plan 2.12b, ruling R3): <paramref name="Aggregate"/> of <paramref name="AggregatedAttribute"/>, a
/// column of the related rows (<paramref name="Related"/>) or, with no related section, of the rollup's own table; over the table's
/// hierarchy when <paramref name="HierarchicalRelationship"/> names it. <paramref name="SourceFilter"/> filters the rollup's own rows
/// (columns of that row). <paramref name="Sources"/> lists every column read. Storage-free: the SQL is built elsewhere.
/// </summary>
public sealed record RollupDefinition(
    RollupAggregate Aggregate,
    string AggregatedAttribute,
    RollupRelated? Related,
    string? HierarchicalRelationship,
    ComputedCondition? SourceFilter,
    IReadOnlyList<RollupSource> Sources)
{
    /// <summary>The table whose column is aggregated: the related table, or the rollup's own table (<paramref name="rollupEntity"/>).</summary>
    public string AggregatedEntity(string rollupEntity) => Related?.Entity ?? rollupEntity;
}
