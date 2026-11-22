using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Computed;

namespace Xrmd.Tests.Shared;

/// <summary>An <see cref="IComputedColumns"/> whose result a test sets per <c>table.column</c> (no XAML involved).</summary>
internal sealed class FakeComputedColumns : IComputedColumns
{
    private readonly Dictionary<string, ComputedDefinition> _definitions = new(StringComparer.Ordinal);

    /// <summary>Makes <paramref name="column"/> of <paramref name="table"/> read <paramref name="sources"/>.</summary>
    public FakeComputedColumns With(string table, string column, params ComputedExpr.Column[] sources)
    {
        _definitions[$"{table}.{column}"] = new ComputedDefinition(sources.Length > 0 ? sources[0] : new ComputedExpr.Literal(0), sources, 0);
        return this;
    }

    /// <summary>
    /// Makes translating <paramref name="column"/> of <paramref name="table"/> fail with <paramref name="message"/>; with
    /// <paramref name="formula"/>, only while the column's <c>FormulaDefinition</c> is that text (another formula translates).
    /// </summary>
    public FakeComputedColumns Throwing(string table, string column, string message, string? formula = null)
    {
        _failures[$"{table}.{column}"] = (message, formula);
        return this;
    }

    private readonly Dictionary<string, (string Message, string? Formula)> _failures = new(StringComparer.Ordinal);

    /// <summary>The configured definition, its mask computed from its sources as a translator does; throws for an unconfigured column.</summary>
    public ComputedDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities)
    {
        var key = $"{entity.LogicalName}.{attribute.LogicalName}";
        if (_failures.TryGetValue(key, out var failure) && (failure.Formula is null || failure.Formula == ComputedColumnRules.FormulaOf(attribute)))
            throw new XrmdValidationException(failure.Message);
        if (!_definitions.TryGetValue(key, out var definition))
            throw new InvalidOperationException($"FakeComputedColumns has no definition for '{key}'.");
        return definition with { SourceTypeMask = ComputedColumnRules.SourceTypeMask(entity, definition, entities) };
    }
}
