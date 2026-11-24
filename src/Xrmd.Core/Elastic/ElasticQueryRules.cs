using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Query.Model;

namespace Xrmd.Core.Elastic;

/// <summary>
/// What a query may do on an elastic table (plan 3.4, ruling E9): no link entities (from an elastic root, or from any root to an
/// elastic table, 0x80048d0b), no sort on a lookup column, and the narrow aggregate shapes the table type supports; plus the
/// partition filter of the <c>partitionId</c> request parameter and the default page size of 500. A query on standard tables
/// passes untouched. Column comparison conditions need no check here: the query readers refuse them for every table.
/// </summary>
public static class ElasticQueryRules
{
    /// <summary>The page size of a query on an elastic table that names none (standard tables keep 5000).</summary>
    public const int DefaultPageSize = 500;

    /// <summary>Refuses the constructs an elastic table does not support.</summary>
    /// <exception cref="XrmdFaultException">0x80048d0b for a link entity; 400 InvalidArgument naming the refused construct and
    /// the elastic table otherwise.</exception>
    public static void Demand(QueryModel q, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(store);
        var root = store.GetEntity(q.Root.EntityName);
        DemandLinks(q.Root, root, store);
        if (root is null || !ElasticTables.IsElastic(root)) return;

        var table = root.LogicalName;
        foreach (var order in q.Root.Orders)
            if (order.Alias is null && root.Attributes?.FirstOrDefault(a => IsAttribute(a, order.Attribute)) is LookupAttributeMetadata)
                throw Refused(table, $"sorting by the lookup column '{order.Attribute}'");

        if (q.Distinct)
        {
            if (root.OwnershipType == OwnershipTypes.UserOwned)
                throw Refused(table, "a distinct query on a user-owned table");
            if (q.Root.Orders.Count > 1)
                throw Refused(table, "a distinct query with more than one sort column");
            if (q.Root.Orders.Count == 1 && !q.Root.AllColumns
                && !q.Root.Columns.Any(c => string.Equals(c.Attribute, q.Root.Orders[0].Attribute, StringComparison.OrdinalIgnoreCase)))
                throw Refused(table, "a distinct query sorted by a column it does not select");
        }
        if (q.Aggregate && q.Root.Orders.Count > 0 && q.Root.Columns.Any(c => c.GroupBy || c.DateGrouping is not null))
            throw Refused(table, "sorting combined with group by in an aggregate query");
    }

    /// <summary>
    /// A copy of <paramref name="q"/> whose root also requires <c>partitionid</c> to equal <paramref name="partitionId"/> exactly
    /// (binary collation, as the record identity of ruling E1). The original filter is kept as one nested filter, so an
    /// <c>or</c> at the top level cannot escape the partition.
    /// </summary>
    public static QueryModel WithPartition(QueryModel q, string partitionId)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(partitionId);
        ElasticTables.DemandValidPartitionId(partitionId);
        var filter = new FilterNode { Operator = LogicalOperator.And };
        filter.Filters.Add(q.Root.Filter);
        filter.Conditions.Add(new ConditionNode(null, ElasticTables.PartitionId, ConditionOperator.Equal, [partitionId], BinaryCollation: true));
        var root = new EntityNode { EntityName = q.Root.EntityName, AllColumns = q.Root.AllColumns, Filter = filter };
        root.Columns.AddRange(q.Root.Columns);
        root.Links.AddRange(q.Root.Links);
        root.Orders.AddRange(q.Root.Orders);
        return Copy(q, root, q.PageSize);
    }

    /// <summary>
    /// <paramref name="q"/> with the elastic default page size when its root is an elastic table and it names none (and has no
    /// <c>top</c>); otherwise <paramref name="q"/> itself.
    /// </summary>
    public static QueryModel WithDefaultPageSize(QueryModel q, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(store);
        if (q.PageSize is not null || q.Top is not null) return q;
        return store.GetEntity(q.Root.EntityName) is { } root && ElasticTables.IsElastic(root) ? Copy(q, q.Root, DefaultPageSize) : q;
    }

    /// <summary>
    /// Applies the <c>partitionId</c> parameter of a RetrieveMultiple request (null or absent: none). Only an elastic root takes a
    /// partition.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when the parameter is not a string or the root is not an elastic table.</exception>
    public static QueryModel ApplyRequest(QueryModel q, OrganizationRequest request, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var (name, value) in request.Parameters)
        {
            if (!string.Equals(name, ElasticRecordKey.PartitionIdParameter, StringComparison.OrdinalIgnoreCase) || value is null) continue;
            if (value is not string partition)
                throw XrmdFaultException.InvalidArgument("The partitionId parameter must be a string.");
            if (store.GetEntity(q.Root.EntityName) is not { } root || !ElasticTables.IsElastic(root))
                throw XrmdFaultException.InvalidArgument(
                    $"The partitionId parameter applies to elastic tables only; '{q.Root.EntityName}' is not an elastic table.");
            return WithPartition(q, partition);
        }
        return q;
    }

    private static void DemandLinks(EntityNode node, EntityMetadata? entity, IMetadataStore store)
    {
        var elastic = entity is not null && ElasticTables.IsElastic(entity);
        foreach (var link in node.Links)
        {
            if (elastic) throw LinkRefused(entity!.LogicalName);
            var target = store.GetEntity(link.EntityName);
            if (target is not null && ElasticTables.IsElastic(target)) throw LinkRefused(target.LogicalName);
            DemandLinks(link, target, store);
        }
    }

    private static QueryModel Copy(QueryModel q, EntityNode root, int? pageSize) => new()
    {
        Root = root,
        Top = q.Top,
        PageSize = pageSize,
        PageNumber = q.PageNumber,
        PagingCookie = q.PagingCookie,
        Distinct = q.Distinct,
        Aggregate = q.Aggregate,
        ReturnTotalRecordCount = q.ReturnTotalRecordCount,
        UseRawOrderBy = q.UseRawOrderBy,
        OrderByStoredValues = q.OrderByStoredValues,
    };

    private static bool IsAttribute(AttributeMetadata a, string name) => string.Equals(a.LogicalName, name, StringComparison.OrdinalIgnoreCase);

    private static XrmdFaultException LinkRefused(string table) =>
        new(ErrorCodes.ElasticLinkEntityNotSupported, $"Link entities are not supported for elastic table '{table}'.", 400);

    private static XrmdFaultException Refused(string table, string construct) =>
        XrmdFaultException.InvalidArgument($"Queries on elastic table '{table}' do not support {construct}.");
}
