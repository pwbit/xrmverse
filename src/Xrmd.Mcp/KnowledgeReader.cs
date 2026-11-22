using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Mcp.Tools;

namespace Xrmd.Mcp;

/// <summary>What the knowledge tables say about one table: synonyms by column logical name, and the glossary terms of its sources.</summary>
internal sealed record TableKnowledge(IReadOnlyDictionary<string, IReadOnlyList<string>> Synonyms, IReadOnlyList<GlossaryTerm> Glossary)
{
    public static readonly TableKnowledge Empty = new(new Dictionary<string, IReadOnlyList<string>>(), []);
}

/// <summary>A glossary term with the names of the knowledge sources that define it.</summary>
internal sealed record GlossaryTerm(string Term, string? Description, IReadOnlyList<string> Sources);

/// <summary>
/// Reads the knowledge tables (D3) through the organization service as the caller, so that depth, sharing and field security
/// apply. Only active rows (<c>statecode</c> 0) count. A caller who misses the read privilege on a knowledge table, or who may
/// not read a secured knowledge column a query filters on or selects, gets nothing from that table rather than an error (only
/// <see cref="ErrorCodes.PrivilegeDenied"/> and <see cref="ErrorCodes.FieldReadDenied"/> are caught; any other fault propagates).
/// </summary>
internal static class KnowledgeReader
{
    private const string State = "statecode";

    /// <summary>
    /// The synonyms of the columns of <paramref name="table"/> (from the synonyms rows of its active source tables whose source is
    /// active and readable; a column the table does not have is simply never looked up) and the terms of those sources.
    /// </summary>
    public static async Task<TableKnowledge> ForTableAsync(OrganizationService service, string table, CancellationToken ct)
    {
        var sourceEntities = await OrEmptyAsync(() => SourceEntitiesAsync(service, table, ct));
        if (sourceEntities.Count == 0) return TableKnowledge.Empty;
        var sources = await SourcesAsync(service, sourceEntities.Select(ParentOf).OfType<Guid>().ToHashSet(), null, ct);
        var entityIds = sourceEntities.Where(e => ParentOf(e) is { } parent && sources.ContainsKey(parent)).Select(e => (object)e.Id).ToArray();
        if (entityIds.Length == 0) return TableKnowledge.Empty;

        var synonyms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var query = Active(KnowledgeSchema.Synonyms, KnowledgeSchema.ColumnLogicalName, KnowledgeSchema.SynonymsText);
        query.Criteria.AddCondition(KnowledgeSchema.SkillEntity, ConditionOperator.In, entityIds);
        query.AddOrder("createdon", OrderType.Ascending);
        query.AddOrder(KnowledgeSchema.Synonyms + "id", OrderType.Ascending);
        foreach (var row in await OrEmptyAsync(() => MetadataTools.AllAsync(service, query, ct)))
        {
            var column = row.GetAttributeValue<string>(KnowledgeSchema.ColumnLogicalName)?.Trim();
            var parts = SplitSynonyms(row.GetAttributeValue<string>(KnowledgeSchema.SynonymsText));
            if (string.IsNullOrEmpty(column) || parts.Count == 0) continue;
            // Rows in creation order; a synonym repeated in any case keeps its first spelling.
            synonyms[column] = (synonyms.TryGetValue(column, out var known) ? known.Concat(parts) : parts)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return new TableKnowledge(synonyms, await TermsOfAsync(service, sources, ct));
    }

    /// <summary>
    /// The active terms of the active, readable sources, optionally only the sources that contain <paramref name="table"/>
    /// and/or are named <paramref name="source"/>.
    /// </summary>
    public static async Task<IReadOnlyList<GlossaryTerm>> TermsAsync(OrganizationService service, string? table, string? source, CancellationToken ct)
    {
        HashSet<Guid>? ids = null;
        if (!string.IsNullOrWhiteSpace(table))
        {
            ids = (await OrEmptyAsync(() => SourceEntitiesAsync(service, table.Trim(), ct))).Select(ParentOf).OfType<Guid>().ToHashSet();
            if (ids.Count == 0) return [];
        }
        var sources = await SourcesAsync(service, ids, string.IsNullOrWhiteSpace(source) ? null : source.Trim(), ct);
        return await TermsOfAsync(service, sources, ct);
    }

    /// <summary>The parts of a synonyms string: split on commas and semicolons, trimmed, empty parts dropped.</summary>
    public static IReadOnlyList<string> SplitSynonyms(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : text.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The active terms of <paramref name="sources"/> (id → name), grouped by term and description, ordered by term.</summary>
    private static async Task<IReadOnlyList<GlossaryTerm>> TermsOfAsync(OrganizationService service, IReadOnlyDictionary<Guid, string> sources,
        CancellationToken ct)
    {
        if (sources.Count == 0) return [];
        var query = Active(KnowledgeSchema.GlossaryTerm, KnowledgeSchema.Term, KnowledgeSchema.Description, KnowledgeSchema.Skill);
        query.Criteria.AddCondition(KnowledgeSchema.Skill, ConditionOperator.In, sources.Keys.Select(s => (object)s).ToArray());
        var rows = await OrEmptyAsync(() => MetadataTools.AllAsync(service, query, ct));
        return rows
            .Select(r => (Term: r.GetAttributeValue<string>(KnowledgeSchema.Term)?.Trim(), Description: NullIfEmpty(r.GetAttributeValue<string>(KnowledgeSchema.Description)),
                Source: sources[r.GetAttributeValue<EntityReference>(KnowledgeSchema.Skill).Id]))
            .Where(r => !string.IsNullOrEmpty(r.Term))
            .GroupBy(r => (r.Term!, r.Description))
            .Select(g => new GlossaryTerm(g.Key.Item1, g.Key.Description,
                g.Select(r => r.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(t => t.Term, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Description, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The active sources the caller can read (id → name), optionally only among <paramref name="ids"/> and/or named
    /// <paramref name="name"/>: the one set every result is drawn from.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> SourcesAsync(OrganizationService service, HashSet<Guid>? ids, string? name,
        CancellationToken ct)
    {
        if (ids is { Count: 0 }) return new Dictionary<Guid, string>();
        var query = Active(KnowledgeSchema.Source, KnowledgeSchema.Name);
        if (ids is not null) query.Criteria.AddCondition(KnowledgeSchema.Source + "id", ConditionOperator.In, ids.Select(i => (object)i).ToArray());
        if (name is not null) query.Criteria.AddCondition(KnowledgeSchema.Name, ConditionOperator.Equal, name);
        return (await OrEmptyAsync(() => MetadataTools.AllAsync(service, query, ct)))
            .ToDictionary(r => r.Id, r => r.GetAttributeValue<string>(KnowledgeSchema.Name) ?? "");
    }

    private static Guid? ParentOf(Entity sourceEntity) => sourceEntity.GetAttributeValue<EntityReference>(KnowledgeSchema.SourceLookup)?.Id;

    /// <summary>The active source tables naming <paramref name="table"/>, with their source.</summary>
    private static Task<IReadOnlyList<Entity>> SourceEntitiesAsync(OrganizationService service, string table, CancellationToken ct)
    {
        var query = Active(KnowledgeSchema.SourceEntity, KnowledgeSchema.SourceLookup);
        query.Criteria.AddCondition(KnowledgeSchema.EntityLogicalName, ConditionOperator.Equal, table);
        return MetadataTools.AllAsync(service, query, ct);
    }

    private static QueryExpression Active(string table, params string[] columns)
    {
        var query = new QueryExpression(table) { ColumnSet = new ColumnSet(columns) };
        query.Criteria.AddCondition(State, ConditionOperator.Equal, 0);
        return query;
    }

    /// <summary>The rows, or none when the caller misses the read privilege on the table or may not read a secured column of the query.</summary>
    private static async Task<IReadOnlyList<Entity>> OrEmptyAsync(Func<Task<IReadOnlyList<Entity>>> read)
    {
        try
        {
            return await read();
        }
        catch (XrmdFaultException fault) when (fault.ErrorCode is ErrorCodes.PrivilegeDenied or ErrorCodes.FieldReadDenied)
        {
            return [];
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
