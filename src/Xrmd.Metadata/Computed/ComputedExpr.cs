namespace Xrmd.Metadata.Computed;

/// <summary>
/// The expression of a calculated column, after its definition text (XAML) is translated: a closed tree that a SQL
/// translator evaluates (plan 2.12a). The metadata layer only reads which columns it names; it never evaluates it.
/// </summary>
public abstract record ComputedExpr
{
    /// <summary>A constant: a string, a number, a boolean, an option value or a date.</summary>
    public sealed record Literal(object? Value) : ComputedExpr;

    /// <summary>A column of the same record (<paramref name="Lookup"/> null) or of the record a single-target lookup points to.</summary>
    public sealed record Column(string? Lookup, string Attribute) : ComputedExpr;

    /// <summary>The current time (UTC).</summary>
    public sealed record Now : ComputedExpr;

    /// <summary><paramref name="Operator"/> is <c>Add</c>, <c>Subtract</c>, <c>Multiply</c> or <c>Divide</c>.</summary>
    public sealed record Binary(string Operator, ComputedExpr Left, ComputedExpr Right) : ComputedExpr;

    /// <summary>A function by its Dataverse name (<c>ADDDAYS</c>, <c>DIFFINDAYS</c>, <c>CONCAT</c>, <c>TRIMLEFT</c>, ...).</summary>
    public sealed record Function(string Name, IReadOnlyList<ComputedExpr> Args) : ComputedExpr;

    /// <summary>The first branch whose condition holds gives the value; none holding gives <paramref name="Otherwise"/> (null when absent).</summary>
    public sealed record Conditional(IReadOnlyList<ComputedBranch> Branches, ComputedExpr? Otherwise) : ComputedExpr;
}

/// <summary>A condition and the value it selects.</summary>
public sealed record ComputedBranch(ComputedCondition When, ComputedExpr Then);

/// <summary>A condition of a <see cref="ComputedExpr.Conditional"/>.</summary>
public abstract record ComputedCondition
{
    /// <summary>
    /// A comparison; <paramref name="Operator"/> uses the operator names of the workflow conditions (<c>Equal</c>, <c>NotEqual</c>,
    /// <c>Greater</c>, ..., <c>Null</c>, <c>NotNull</c>); <paramref name="Right"/> is null for the unary ones.
    /// </summary>
    public sealed record Compare(string Operator, ComputedExpr Left, ComputedExpr? Right) : ComputedCondition;

    public sealed record And(ComputedCondition Left, ComputedCondition Right) : ComputedCondition;

    public sealed record Or(ComputedCondition Left, ComputedCondition Right) : ComputedCondition;

    public sealed record Not(ComputedCondition Operand) : ComputedCondition;
}

/// <summary>
/// A translated definition: the expression, the distinct columns it reads (<paramref name="Sources"/>) and the
/// <c>SourceTypeMask</c> the translator derived (the metadata rules recompute it from the sources, <see cref="ComputedColumnRules.SourceTypeMask"/>).
/// </summary>
public sealed record ComputedDefinition(ComputedExpr Body, IReadOnlyList<ComputedExpr.Column> Sources, int SourceTypeMask)
{
    /// <summary>
    /// The <c>DateTimeBehavior</c> of the date a formula column gives (<c>UserLocal</c>, <c>DateOnly</c> or <c>TimeZoneIndependent</c>;
    /// plan 2.12c), which the metadata rules compare with the column's; null for any other result and for a calculated column.
    /// </summary>
    public string? DateBehavior { get; init; }
}
