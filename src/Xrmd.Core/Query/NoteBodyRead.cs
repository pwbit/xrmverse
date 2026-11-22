using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Core.Query;

/// <summary>
/// Note bodies on the read path of <see cref="QueryExecutor"/> (plan 2.11, ruling A16, decision D3). <c>annotation.documentbody</c>
/// has no SQL column: selected on an <c>annotation</c> (by name or through all columns) it is taken out of the query and read
/// afterwards from the file store for the notes of the page (<see cref="AnnotationBodies"/>), as base64 text, in one batch for the
/// whole page: the root's notes and those of every link-entity on <c>annotation</c> (review I2: FetchXML all-attributes, the Web
/// API's <c>$expand</c>), where it comes back as an <see cref="AliasedValue"/> named <c>{alias}.documentbody</c>. The batch counts
/// toward <c>Files:MaxNoteBodyBytesPerRead</c> (ruling Y5). Only the rows the query returned get their body, so a body is read
/// exactly where its note is (the note's read security); a note without a body has no value, as a null column. The body cannot be
/// used in a condition, an order, a join or an aggregate, nor take an alias (400). A distinct query reads it only with the note's
/// primary key.
/// </summary>
internal sealed class NoteBodyRead
{
    private const string Body = ActivitySchema.DocumentBody;

    /// <summary>A scope whose notes get their body: the root (<see cref="Alias"/> null) or a link-entity by its result alias.</summary>
    private sealed record Target(string? Alias, string PrimaryKey, bool AddedKey);

    private NoteBodyRead(QueryModel query, IReadOnlyList<Target> targets)
    {
        Query = query;
        _targets = targets;
    }

    private readonly IReadOnlyList<Target> _targets;

    /// <summary>The query to compile: without <c>documentbody</c>.</summary>
    public QueryModel Query { get; }

    /// <summary>Checks the uses of <c>documentbody</c> in <paramref name="query"/> and prepares the query to compile.</summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for the body used other than as a selected column.</exception>
    public static NoteBodyRead Prepare(QueryModel query, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(query);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectAliases(query.Root, aliases);
        DemandNoBodyUse(query.Root, parent: null, aliases);
        if (query.Aggregate || store.GetEntity(ActivitySchema.Annotation) is not { } annotation
            || (annotation.Attributes ?? []).All(a => a.LogicalName != Body))
            return new NoteBodyRead(query, []);
        var pk = SystemAttributes.PrimaryIdName(annotation);

        var targets = new List<Target>();
        var changed = false;
        // The result aliases of the link-entities, by the compiler's own rule (LinkAliases, final review M4), visited in its order.
        var linkAliases = new LinkAliases(aliases.Keys.Where(k => HasExplicitAlias(query.Root, k)));
        EntityNode Visit(EntityNode node, string? alias)
        {
            var copy = node;
            if (node.EntityName == ActivitySchema.Annotation)
            {
                var named = node.Columns.Where(c => c.Attribute == Body).ToList();
                if (named.FirstOrDefault(c => c.Alias is not null) is { } aliased)
                    throw XrmdFaultException.InvalidArgument($"The note body '{aliased.Attribute}' of '{ActivitySchema.Annotation}' cannot take an alias.");
                if (node.AllColumns || named.Count > 0)
                {
                    // The root always reads its key outside a distinct query; a link-entity reads it with all columns only.
                    var hasKey = node.AllColumns || node.Columns.Any(c => c.Attribute == pk && c.Alias is null)
                                 || (alias is null && !query.Distinct);
                    if (!hasKey && query.Distinct)
                        throw XrmdFaultException.InvalidArgument(
                            $"A distinct query reads the note body '{Body}' only with its primary key '{pk}': select it too.");
                    targets.Add(new Target(alias, pk, AddedKey: !hasKey));
                    if (named.Count > 0 || !hasKey)
                    {
                        copy = Shallow(node);
                        copy.Columns.AddRange(node.Columns.Where(c => c.Attribute != Body));
                        if (!hasKey) copy.Columns.Add(new ColumnNode(pk));
                        changed = true;
                    }
                }
            }
            var links = new List<LinkNode>();
            foreach (var link in node.Links)
            {
                var linkAlias = linkAliases.For(link.EntityName, link.Alias);
                var visited = (LinkNode)Visit(link, linkAlias);
                if (!ReferenceEquals(visited, link)) changed = true;
                links.Add(visited);
            }
            if (!links.SequenceEqual(node.Links))
            {
                if (ReferenceEquals(copy, node))
                {
                    copy = Shallow(node);
                    copy.Columns.AddRange(node.Columns);
                }
                copy.Links.AddRange(links);
            }
            else if (!ReferenceEquals(copy, node)) copy.Links.AddRange(node.Links);
            return copy;
        }
        var root = Visit(query.Root, null);
        return new NoteBodyRead(changed ? query.WithRoot(root) : query, targets);
    }

    /// <summary>Adds the bodies to the notes of <paramref name="result"/>, the root's and the link-entities', in one batch.</summary>
    /// <exception cref="InvalidOperationException">The executor has no file service to read them from (fail closed: never an empty body).</exception>
    public async Task CompleteAsync(EntityCollection result, FileColumnService? files, DataSession session, CancellationToken ct)
    {
        if (_targets.Count == 0) return;
        Guid? NoteOf(Entity entity, Target target) =>
            target.Alias is null
                ? entity.Id != Guid.Empty ? entity.Id : null
                : entity.Attributes.TryGetValue($"{target.Alias}.{target.PrimaryKey}", out var value) && value is AliasedValue { Value: Guid id } ? id : null;
        var ids = result.Entities.SelectMany(e => _targets.Select(t => NoteOf(e, t))).OfType<Guid>().Distinct().ToList();
        if (ids.Count > 0)
        {
            if (files is null)
                throw new InvalidOperationException("Note bodies are read from the file store: this query executor has no file service.");
            var bodies = await files.Annotations.ReadAsync(session, ids, ct);
            foreach (var entity in result.Entities)
                foreach (var target in _targets)
                {
                    if (NoteOf(entity, target) is not { } id || !bodies.TryGetValue(id, out var body)) continue;
                    if (target.Alias is null) entity[Body] = body;
                    else entity[$"{target.Alias}.{Body}"] = new AliasedValue(ActivitySchema.Annotation, Body, body);
                }
        }
        foreach (var target in _targets.Where(t => t.AddedKey && t.Alias is not null))
            foreach (var entity in result.Entities) entity.Attributes.Remove($"{target.Alias}.{target.PrimaryKey}");
    }

    /// <summary>
    /// A copy of <paramref name="node"/> with its settings and filter and no columns, links or orders but its orders (the caller adds
    /// the columns and links). Every property of <see cref="EntityNode"/> and <see cref="LinkNode"/> is copied here.
    /// </summary>
    private static EntityNode Shallow(EntityNode node)
    {
        EntityNode copy = node is LinkNode link
            ? new LinkNode
            {
                EntityName = link.EntityName, From = link.From, To = link.To, JoinType = link.JoinType, Alias = link.Alias,
                ToTypeCode = link.ToTypeCode, ToTypeAttribute = link.ToTypeAttribute,
            }
            : new EntityNode { EntityName = node.EntityName };
        copy.AllColumns = node.AllColumns;
        copy.Filter = node.Filter;
        copy.Orders.AddRange(node.Orders);
        return copy;
    }

    private static bool HasExplicitAlias(EntityNode node, string alias) =>
        node.Links.Any(l => l.Alias == alias || HasExplicitAlias(l, alias));

    private static void CollectAliases(EntityNode node, Dictionary<string, string> aliases)
    {
        foreach (var link in node.Links)
        {
            if (link.Alias is { } alias) aliases.TryAdd(alias, link.EntityName);
            aliases.TryAdd(link.EntityName, link.EntityName);
            CollectAliases(link, aliases);
        }
    }

    /// <summary>Refuses the body in a condition, an order, a join or an aggregate (400).</summary>
    private static void DemandNoBodyUse(EntityNode node, EntityNode? parent, Dictionary<string, string> aliases)
    {
        DemandFilter(node.Filter, node.EntityName, aliases);
        foreach (var order in node.Orders.Where(o => o.Alias is null)) Demand(node.EntityName, order.Attribute, "ordered by");
        foreach (var column in node.Columns)
        {
            if (column.Aggregate is not null || column.GroupBy) Demand(node.EntityName, column.Attribute, "aggregated");
        }
        if (node is LinkNode link)
        {
            Demand(link.EntityName, link.From, "joined on");
            if (parent is not null) Demand(parent.EntityName, link.To, "joined on");
        }
        foreach (var child in node.Links) DemandNoBodyUse(child, node, aliases);
    }

    private static void DemandFilter(FilterNode filter, string entity, Dictionary<string, string> aliases)
    {
        foreach (var condition in filter.Conditions)
            Demand(condition.EntityAlias is { } alias ? aliases.GetValueOrDefault(alias, alias) : entity, condition.Attribute, "filtered on");
        foreach (var child in filter.Filters) DemandFilter(child, entity, aliases);
    }

    private static void Demand(string table, string? attribute, string use)
    {
        if (table != ActivitySchema.Annotation || attribute != ActivitySchema.DocumentBody) return;
        throw XrmdFaultException.InvalidArgument(
            $"The note body '{attribute}' of '{table}' cannot be {use}: it is kept in the file store, not in a column. " +
            "It can only be selected.");
    }
}
