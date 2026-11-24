using Xrmd.Abstractions;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Query.Model;

namespace Xrmd.Query;

/// <summary>
/// Reads <see cref="RecordQuery"/> / <see cref="AttributeQuery"/> into a <see cref="QueryModel"/>; the SDK query types are read
/// by the twins in <c>Sdk/</c>, which convert first.
/// </summary>
/// <remarks>Throws <see cref="XrmdValidationException"/> for features not supported (column comparison conditions,
/// attribute expressions of a column set) and for filters/link-entities nested beyond <see cref="QueryLimits"/>.</remarks>
public static partial class QueryExpressionReader
{
    public static QueryModel Read(RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var root = new EntityNode { EntityName = query.EntityName! };
        ReadColumns(root, query.Columns);
        root.Filter = ReadFilter(query.Criteria, depth: 1);
        ReadOrders(root, query.Orders);
        ReadLinks(root, query.Links, depth: 1);

        var model = new QueryModel { Root = root, Top = query.TopCount, Distinct = query.Distinct };
        ApplyPaging(model, query.Paging);
        return model;
    }

    public static QueryModel Read(AttributeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var root = new EntityNode { EntityName = query.EntityName! };
        ReadColumns(root, query.Columns);
        for (var i = 0; i < query.Attributes.Count; i++)
        {
            var value = i < query.Values.Count ? query.Values[i] : null;
            root.Filter.Conditions.Add(new ConditionNode(null, query.Attributes[i], FilterOperator.Equal, new[] { value }));
        }
        foreach (var o in query.Orders)
            root.Orders.Add(new OrderNode(o.AttributeName!, o.Direction == SortDirection.Descending));

        var model = new QueryModel { Root = root, Top = query.TopCount };
        ApplyPaging(model, query.Paging);
        return model;
    }

    private static void ApplyPaging(QueryModel model, QueryPaging? page)
    {
        if (page == null) return;
        if (page.Count > 0) model.PageSize = page.Count;
        if (page.PageNumber > 0) model.PageNumber = page.PageNumber;
        model.PagingCookie = string.IsNullOrEmpty(page.PagingCookie) ? null : page.PagingCookie;
        model.ReturnTotalRecordCount = page.ReturnTotalRecordCount;
    }

    private static void ReadColumns(EntityNode node, ColumnSelection? columns)
    {
        if (columns == null) return;
        if (columns.Expressions is { Count: > 0 })
            throw ExpressionsRefusal();
        node.AllColumns = columns.AllColumns;
        foreach (var c in columns.Columns) node.Columns.Add(new ColumnNode(c));
    }

    private static void ReadOrders(EntityNode node, IEnumerable<QueryOrder>? orders)
    {
        if (orders == null) return;
        foreach (var o in orders)
            node.Orders.Add(new OrderNode(o.AttributeName!, o.Direction == SortDirection.Descending));
    }

    private static void ReadLinks(EntityNode parent, IEnumerable<QueryLink>? links, int depth)
    {
        if (links == null) return;
        foreach (var l in links)
        {
            if (depth > QueryLimits.MaxLinkDepth)
                throw LinkDepthRefusal();
            var node = new LinkNode
            {
                EntityName = l.ToEntityName!,
                From = l.ToAttributeName!,
                To = l.FromAttributeName!,
                JoinType = l.Join,
                Alias = string.IsNullOrEmpty(l.Alias) ? null : l.Alias,
            };
            ReadColumns(node, l.Columns);
            node.Filter = ReadFilter(l.Criteria, depth: 1);
            ReadOrders(node, l.Orders);
            ReadLinks(node, l.Links, depth + 1);
            parent.Links.Add(node);
        }
    }

    private static FilterNode ReadFilter(QueryFilter? f, int depth)
    {
        if (depth > QueryLimits.MaxFilterDepth)
            throw FilterDepthRefusal();
        var node = new FilterNode();
        if (f == null) return node;
        // The model keeps undefined enum values; the compiler refuses an undefined operator (XrmdValidationException) and
        // treats an undefined logical operator as AND.
        node.Operator = f.Logic;
        foreach (var c in f.Conditions)
        {
            if (c.CompareColumns)
                throw CompareColumnsRefusal(c.AttributeName);
            node.Conditions.Add(new ConditionNode(
                string.IsNullOrEmpty(c.EntityName) ? null : c.EntityName,
                c.AttributeName!, c.Operator, c.Values.ToArray()));
        }
        foreach (var sub in f.Filters) node.Filters.Add(ReadFilter(sub, depth + 1));
        return node;
    }

    internal static XrmdValidationException ExpressionsRefusal() => new("ColumnSet.AttributeExpressions is not supported.");

    internal static XrmdValidationException LinkDepthRefusal() =>
        new($"Link-entities are nested more than {QueryLimits.MaxLinkDepth} levels deep.");

    internal static XrmdValidationException FilterDepthRefusal() =>
        new($"Filters are nested more than {QueryLimits.MaxFilterDepth} levels deep.");

    internal static XrmdValidationException CompareColumnsRefusal(string? attribute) =>
        new($"Condition on '{attribute}': column comparison (CompareColumns) is not supported.");
}
