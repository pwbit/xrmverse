namespace Xrmd.Query.Cosmos;

/// <summary>A parsed <c>ExecuteCosmosSqlQuery</c> statement of the supported subset (plan #78, ruling J2).</summary>
public sealed record CosmosQuery(string SourceAlias, IReadOnlyList<CosmosSelectItem> Select, CosmosExpr? Where, CosmosOrder? OrderBy);

/// <summary>One projected path with its optional alias.</summary>
public sealed record CosmosSelectItem(CosmosPath Path, string? Alias);

/// <summary>The single ORDER BY item.</summary>
public sealed record CosmosOrder(CosmosPath Path, bool Descending);

/// <summary>A property path after the source alias; the first step is always <c>props</c>.</summary>
public sealed record CosmosPath(IReadOnlyList<CosmosPathStep> Steps)
{
    public bool Equals(CosmosPath? other) => other is not null && Steps.SequenceEqual(other.Steps);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var s in Steps) h.Add(s);
        return h.ToHashCode();
    }
}

public abstract record CosmosPathStep;

/// <summary>A named step: <c>.name</c> or <c>["name"]</c>.</summary>
public sealed record PropertyStep(string Name) : CosmosPathStep;

/// <summary>An array step: <c>[n]</c> with a non-negative integer literal.</summary>
public sealed record IndexStep(int Index) : CosmosPathStep;

public abstract record CosmosExpr;

public sealed record Comparison(CosmosOperand Left, CosmosComparisonOp Op, CosmosOperand Right) : CosmosExpr;

public sealed record IsDefined(CosmosPath Path) : CosmosExpr;

public sealed record Not(CosmosExpr Inner) : CosmosExpr;

public sealed record And(CosmosExpr Left, CosmosExpr Right) : CosmosExpr;

public sealed record Or(CosmosExpr Left, CosmosExpr Right) : CosmosExpr;

public abstract record CosmosOperand;

public sealed record PathOperand(CosmosPath Path) : CosmosOperand;

/// <summary>A literal: <see cref="string"/>, <see cref="decimal"/>, <see cref="bool"/> or <c>null</c>.</summary>
public sealed record LiteralOperand(object? Value) : CosmosOperand;

/// <summary>A query parameter; <see cref="Name"/> includes the leading <c>@</c>.</summary>
public sealed record ParameterOperand(string Name) : CosmosOperand;

public enum CosmosComparisonOp
{
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
}
