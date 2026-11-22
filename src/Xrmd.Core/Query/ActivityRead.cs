using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Query.Model;

namespace Xrmd.Core.Query;

/// <summary>
/// The activity model on the read path of <see cref="QueryExecutor"/> (plan 2.11):
/// <list type="bullet">
/// <item>Party list columns (A8, decision D5) have no SQL column: selected on the root table (by name or through all columns), they
/// are taken out of the query and read afterwards for the whole page in one batched read of <c>activityparty</c>
/// (<see cref="ActivityParties.ReadAsync"/>), each as an <see cref="EntityCollection"/> of <c>activityparty</c> entities. The rows of
/// the page passed the caller's read restriction, and a party row belongs to its activity (pre-flight C13), so no further check
/// applies. A party list with no row is omitted, as a null value is (from memory: checklist 3j).</item>
/// <item>A party list in a condition, an order, a join, an aggregate or a link-entity's columns is refused (400):
/// the client queries or links <c>activityparty</c> instead. A distinct query reads party lists when it selects the primary key
/// (all columns do); without it, a party list is refused (400).</item>
/// <item>The formatted <c>statuscode</c> of an <c>activitypointer</c> row uses the option set of the activity's table, read from the
/// row's <c>activitytypecode</c> (A11, checklist 3f), which is read for it when the query does not select it.</item>
/// </list>
/// </summary>
internal sealed class ActivityRead
{
    private const string StatusCode = "statuscode";

    private ActivityRead(QueryModel query, EntityMetadata? root, IReadOnlyList<string> partyLists, bool statusLabels, bool addedTypeCode)
    {
        Query = query;
        _root = root;
        _partyLists = partyLists;
        _statusLabels = statusLabels;
        _addedTypeCode = addedTypeCode;
    }

    private readonly EntityMetadata? _root;
    private readonly IReadOnlyList<string> _partyLists;
    private readonly bool _statusLabels;
    private readonly bool _addedTypeCode;

    /// <summary>The query to compile: without the party list columns, with <c>activitytypecode</c> when it is needed for a label.</summary>
    public QueryModel Query { get; }

    /// <summary>Checks the party list columns of <paramref name="query"/> and prepares the query to compile.</summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for a party list used other than as a root column of a plain query.</exception>
    public static ActivityRead Prepare(QueryModel query, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(query);
        var root = store.GetEntity(query.Root.EntityName);
        if (root is null) return new ActivityRead(query, null, [], false, false); // the compiler reports the unknown table

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectAliases(query.Root, aliases);
        DemandNoPartyListUse(query.Root, parent: null, store, aliases);

        var partyLists = (root.Attributes ?? []).Where(ActivitySchema.IsPartyList).Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);
        List<string> selected = query.Aggregate ? []
            : query.Root.AllColumns ? [.. partyLists]
            : [.. query.Root.Columns.Where(c => partyLists.Contains(c.Attribute)).Select(c => c.Attribute).Distinct()];
        if (selected.Count > 0)
        {
            if (query.Root.Columns.FirstOrDefault(c => partyLists.Contains(c.Attribute) && c.Alias is not null) is { } aliased)
                throw XrmdFaultException.InvalidArgument(
                    $"The party list column '{aliased.Attribute}' of '{root.LogicalName}' cannot take an alias.");
            // A distinct query selects the primary key only with all columns or when it is asked for: without it the rows have no id
            // to attach the parties to, so the party list is refused rather than dropped.
            var pk = SystemAttributes.PrimaryIdName(root);
            if (query.Distinct && !query.Root.AllColumns && query.Root.Columns.All(c => c.Attribute != pk || c.Alias is not null))
                throw XrmdFaultException.InvalidArgument(
                    $"A distinct query reads the party list column '{selected[0]}' of '{root.LogicalName}' only with its primary key " +
                    $"'{pk}': select it too, or query '{ActivitySchema.Party}'.");
        }

        var statusLabels = !query.Aggregate && root.LogicalName == ActivitySchema.Pointer
                           && (query.Root.AllColumns || query.Root.Columns.Any(c => c.Attribute == StatusCode && c.Alias is null));
        var addTypeCode = statusLabels && !query.Distinct && !query.Root.AllColumns
                          && query.Root.Columns.All(c => c.Attribute != ActivitySchema.ActivityTypeCode || c.Alias is not null);

        if (selected.Count == 0 && !addTypeCode) return new ActivityRead(query, root, selected, statusLabels, false);
        var node = new EntityNode { EntityName = query.Root.EntityName, AllColumns = query.Root.AllColumns, Filter = query.Root.Filter };
        node.Columns.AddRange(query.Root.Columns.Where(c => !partyLists.Contains(c.Attribute)));
        if (addTypeCode) node.Columns.Add(new ColumnNode(ActivitySchema.ActivityTypeCode));
        node.Links.AddRange(query.Root.Links);
        node.Orders.AddRange(query.Root.Orders);
        var copy = query.WithRoot(node);
        return new ActivityRead(copy, root, selected, statusLabels, addTypeCode);
    }

    /// <summary>Adds the party lists to the rows of <paramref name="result"/> and sets the pointer's status labels.</summary>
    public async Task CompleteAsync(EntityCollection result, IMetadataStore store, DataSession session, int languageCode, CancellationToken ct)
    {
        if (_root is null) return;
        if (_partyLists.Count > 0)
        {
            var ids = result.Entities.Select(e => e.Id).Where(id => id != Guid.Empty).Distinct().ToList();
            if (ids.Count > 0)
            {
                var lists = await new ActivityParties(store).ReadAsync(session, _root, ids, _partyLists, ct);
                foreach (var entity in result.Entities)
                {
                    if (!lists.TryGetValue(entity.Id, out var byColumn)) continue;
                    foreach (var (column, parties) in byColumn)
                        if (parties.Entities.Count > 0) entity[column] = parties;
                }
            }
        }
        if (!_statusLabels) return;
        foreach (var entity in result.Entities)
        {
            if (entity.GetAttributeValue<OptionSetValue>(StatusCode) is { } status)
            {
                if (FormattedValues.ActivityStatus(status.Value, entity.GetAttributeValue<string>(ActivitySchema.ActivityTypeCode), store.GetEntity, languageCode) is { } label)
                    entity.FormattedValues[StatusCode] = label;
                else entity.FormattedValues.Remove(StatusCode); // no label rather than the pointer's own, which may name another status
            }
            if (_addedTypeCode)
            {
                entity.Attributes.Remove(ActivitySchema.ActivityTypeCode);
                entity.FormattedValues.Remove(ActivitySchema.ActivityTypeCode);
            }
        }
    }

    private static void CollectAliases(EntityNode node, Dictionary<string, string> aliases)
    {
        foreach (var link in node.Links)
        {
            if (link.Alias is { } alias) aliases.TryAdd(alias, link.EntityName);
            aliases.TryAdd(link.EntityName, link.EntityName);
            CollectAliases(link, aliases);
        }
    }

    /// <summary>Refuses a party list in a condition, an order, a join, a link-entity column or an aggregate (400).</summary>
    private static void DemandNoPartyListUse(EntityNode node, EntityNode? parent, IMetadataStore store, Dictionary<string, string> aliases)
    {
        DemandFilter(node.Filter, node.EntityName, store, aliases);
        foreach (var order in node.Orders.Where(o => o.Alias is null)) Demand(store, node.EntityName, order.Attribute, "ordered by");
        foreach (var column in node.Columns)
        {
            if (column.Aggregate is not null || column.GroupBy) Demand(store, node.EntityName, column.Attribute, "aggregated");
            else if (parent is not null) Demand(store, node.EntityName, column.Attribute, "read in a link-entity");
        }
        if (node is LinkNode link)
        {
            Demand(store, link.EntityName, link.From, "joined on");
            if (parent is not null) Demand(store, parent.EntityName, link.To, "joined on");
        }
        foreach (var child in node.Links) DemandNoPartyListUse(child, node, store, aliases);
    }

    private static void DemandFilter(FilterNode filter, string entity, IMetadataStore store, Dictionary<string, string> aliases)
    {
        foreach (var condition in filter.Conditions)
            Demand(store, condition.EntityAlias is { } alias ? aliases.GetValueOrDefault(alias, alias) : entity, condition.Attribute, "filtered on");
        foreach (var child in filter.Filters) DemandFilter(child, entity, store, aliases);
    }

    private static void Demand(IMetadataStore store, string table, string? attribute, string use)
    {
        if (attribute is null || store.GetEntity(table) is not { } m) return;
        if ((m.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attribute) is not { } a || !ActivitySchema.IsPartyList(a)) return;
        throw XrmdFaultException.InvalidArgument(
            $"The party list column '{attribute}' of '{m.LogicalName}' cannot be {use}: its parties are rows of '{ActivitySchema.Party}'. " +
            $"Query '{ActivitySchema.Party}' or link to it on '{ActivitySchema.ActivityId}' instead.");
    }
}
