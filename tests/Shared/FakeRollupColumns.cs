using System.Collections.Concurrent;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Computed;

namespace Xrmd.Tests.Shared;

/// <summary>An <see cref="IRollupColumns"/> whose result a test sets per <c>table.column</c> (no XAML involved).</summary>
internal sealed class FakeRollupColumns : IRollupColumns
{
    private readonly ConcurrentDictionary<string, RollupDefinition> _definitions = new(StringComparer.Ordinal);

    /// <summary>Makes <paramref name="column"/> of <paramref name="table"/> translate to <paramref name="definition"/>.</summary>
    public FakeRollupColumns With(string table, string column, RollupDefinition definition)
    {
        _definitions[$"{table}.{column}"] = definition;
        return this;
    }

    /// <summary>
    /// A rollup of <paramref name="table"/> that aggregates <paramref name="aggregated"/> of the <paramref name="related"/> rows whose
    /// <paramref name="lookup"/> points to it through <paramref name="relationship"/>, filtered by <paramref name="filter"/>.
    /// </summary>
    public FakeRollupColumns Related(string table, string column, RollupAggregate aggregate, string related, string lookup, string relationship,
        string aggregated, ComputedCondition? filter = null, ComputedCondition? sourceFilter = null)
    {
        List<RollupSource> sources = [new(related, aggregated), new(related, lookup)];
        sources.AddRange(RollupColumnRules.ColumnsOf(filter).Select(c => new RollupSource(related, c)));
        sources.AddRange(RollupColumnRules.ColumnsOf(sourceFilter).Select(c => new RollupSource(table, c)));
        return With(table, column, new RollupDefinition(aggregate, aggregated, new RollupRelated(relationship, related, lookup, filter), null,
            sourceFilter, sources.Distinct().ToList()));
    }

    public RollupDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities,
        Func<string, OneToManyRelationshipMetadata?> relationships)
    {
        var key = $"{entity.LogicalName}.{attribute.LogicalName}";
        return _definitions.TryGetValue(key, out var definition)
            ? definition
            : throw new XrmdValidationException($"FakeRollupColumns has no definition for '{key}'.");
    }
}

/// <summary>An <see cref="IRollupJobs"/> that records each notification (optionally failing after recording it).</summary>
internal sealed class FakeRollupJobs : IRollupJobs
{
    private readonly ConcurrentQueue<(string Entity, string Attribute, RollupChange Change)> _calls = new();

    public IReadOnlyList<(string Entity, string Attribute, RollupChange Change)> Calls => _calls.ToList();

    /// <summary>A call for this change throws after being recorded (the store must log it and go on).</summary>
    public RollupChange? FailOn { get; set; }

    public Task DefinitionChangedAsync(string entity, string attribute, RollupChange change, CancellationToken ct)
    {
        _calls.Enqueue((entity, attribute, change));
        return change == FailOn ? Task.FromException(new InvalidOperationException("rollup jobs are down")) : Task.CompletedTask;
    }

    /// <summary>Waits (up to five seconds) until <paramref name="count"/> calls are recorded; the store notifies after the commit.</summary>
    public async Task<IReadOnlyList<(string Entity, string Attribute, RollupChange Change)>> WaitForAsync(int count)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (_calls.Count < count && DateTime.UtcNow < until) await Task.Delay(20);
        return Calls;
    }
}
