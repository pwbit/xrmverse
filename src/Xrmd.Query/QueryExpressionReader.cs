using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Query.Model;

namespace Xrmd.Query;

/// <summary>
/// Reads <see cref="QueryExpression"/> / <see cref="QueryByAttribute"/> into a <see cref="QueryModel"/>.
/// </summary>
/// <remarks>Throws <see cref="XrmdValidationException"/> for features not supported (column comparison conditions,
/// attribute expressions of a column set) and for filters/link-entities nested beyond <see cref="QueryLimits"/>.</remarks>
public static class QueryExpressionReader
{
    public static QueryModel Read(QueryExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var root = new EntityNode { EntityName = query.EntityName };
        ReadColumns(root, query.ColumnSet);
        root.Filter = ReadFilter(query.Criteria, depth: 1);
        ReadOrders(root, query.Orders);
        ReadLinks(root, query.LinkEntities, depth: 1);

        var model = new QueryModel { Root = root, Top = query.TopCount, Distinct = query.Distinct };
        ApplyPaging(model, query.PageInfo);
        return model;
    }

    public static QueryModel Read(QueryByAttribute query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var root = new EntityNode { EntityName = query.EntityName };
        ReadColumns(root, query.ColumnSet);
        for (var i = 0; i < query.Attributes.Count; i++)
        {
            var value = i < query.Values.Count ? query.Values[i] : null;
            root.Filter.Conditions.Add(new ConditionNode(null, query.Attributes[i], ConditionOperator.Equal, new[] { value }));
        }
        foreach (var o in query.Orders)
            root.Orders.Add(new OrderNode(o.AttributeName, o.OrderType == OrderType.Descending));

        var model = new QueryModel { Root = root, Top = query.TopCount };
        ApplyPaging(model, query.PageInfo);
        return model;
    }

    private static void ApplyPaging(QueryModel model, PagingInfo? page)
    {
        if (page == null) return;
        if (page.Count > 0) model.PageSize = page.Count;
        if (page.PageNumber > 0) model.PageNumber = page.PageNumber;
        model.PagingCookie = string.IsNullOrEmpty(page.PagingCookie) ? null : page.PagingCookie;
        model.ReturnTotalRecordCount = page.ReturnTotalRecordCount;
    }

    private static void ReadColumns(EntityNode node, ColumnSet? columns)
    {
        if (columns == null) return;
        if (columns.AttributeExpressions is { Count: > 0 })
            throw new XrmdValidationException("ColumnSet.AttributeExpressions is not supported.");
        node.AllColumns = columns.AllColumns;
        foreach (var c in columns.Columns) node.Columns.Add(new ColumnNode(c));
    }

    private static void ReadOrders(EntityNode node, IEnumerable<OrderExpression>? orders)
    {
        if (orders == null) return;
        foreach (var o in orders)
            node.Orders.Add(new OrderNode(o.AttributeName, o.OrderType == OrderType.Descending));
    }

    private static void ReadLinks(EntityNode parent, IEnumerable<LinkEntity>? links, int depth)
    {
        if (links == null) return;
        foreach (var l in links)
        {
            if (depth > QueryLimits.MaxLinkDepth)
                throw new XrmdValidationException($"Link-entities are nested more than {QueryLimits.MaxLinkDepth} levels deep.");
            var node = new LinkNode
            {
                EntityName = l.LinkToEntityName,
                From = l.LinkToAttributeName,
                To = l.LinkFromAttributeName,
                JoinType = l.JoinOperator,
                Alias = string.IsNullOrEmpty(l.EntityAlias) ? null : l.EntityAlias,
            };
            ReadColumns(node, l.Columns);
            node.Filter = ReadFilter(l.LinkCriteria, depth: 1);
            ReadOrders(node, l.Orders);
            ReadLinks(node, l.LinkEntities, depth + 1);
            parent.Links.Add(node);
        }
    }

    private static FilterNode ReadFilter(FilterExpression? f, int depth)
    {
        if (depth > QueryLimits.MaxFilterDepth)
            throw new XrmdValidationException($"Filters are nested more than {QueryLimits.MaxFilterDepth} levels deep.");
        var node = new FilterNode();
        if (f == null) return node;
        node.Operator = f.FilterOperator;
        foreach (var c in f.Conditions)
        {
            if (c.CompareColumns)
                throw new XrmdValidationException($"Condition on '{c.AttributeName}': column comparison (CompareColumns) is not supported.");
            node.Conditions.Add(new ConditionNode(
                string.IsNullOrEmpty(c.EntityName) ? null : c.EntityName,
                c.AttributeName, c.Operator, c.Values.ToArray<object?>()));
        }
        foreach (var sub in f.Filters) node.Filters.Add(ReadFilter(sub, depth + 1));
        return node;
    }
}
