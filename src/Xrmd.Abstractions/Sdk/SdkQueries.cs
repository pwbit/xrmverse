using Microsoft.Xrm.Sdk.Query;
using Xrmd.Model.Query;
using Xrmd.Model.Records;

namespace Xrmd.Abstractions.Sdk;

/// <summary>
/// Converters between the Microsoft SDK's query types and our own query definitions (<c>Xrmd.Model.Query</c>). Every
/// conversion copies into new instances; the model and the SDK object never share a mutable collection, and a null
/// reference-typed member stays null. Enum members convert by unchecked cast, so an undefined value survives the round
/// trip. Condition values and QueryByAttribute values convert with <see cref="SdkRecords.ValueToModel"/> and
/// <see cref="SdkRecords.ValueToSdk"/>.
/// <para>
/// Not carried: <c>ExtensionData</c> of every type and the lazy-file members of <c>ColumnSet</c>
/// (<c>HasLazyFileAttribute</c>, <c>LazyFileAttributeEntityName</c>, <c>LazyFileAttributeKey</c>,
/// <c>LazyFileAttributeValue</c>, <c>LazyFileAttributeSizeLimit</c>); they are not part of the data contract.
/// </para>
/// </summary>
public static class SdkQueries
{
    public static QueryDefinition ToModel(this QueryBase query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query switch
        {
            QueryExpression q => q.ToModel(),
            QueryByAttribute q => q.ToModel(),
            FetchExpression q => q.ToModel(),
            _ => throw new NotSupportedException($"Query type '{query.GetType().FullName}' is not supported."),
        };
    }

    public static QueryBase ToSdk(this QueryDefinition query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query switch
        {
            RecordQuery q => q.ToSdk(),
            AttributeQuery q => q.ToSdk(),
            FetchQuery q => q.ToSdk(),
            _ => throw new NotSupportedException($"Query definition '{query.GetType().FullName}' is not supported."),
        };
    }

    public static RecordQuery ToModel(this QueryExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = new RecordQuery
        {
            EntityName = query.EntityName,
            Columns = query.ColumnSet?.ToModel(),
            Criteria = query.Criteria?.ToModel(),
            Distinct = query.Distinct,
            NoLock = query.NoLock,
            TopCount = query.TopCount,
            Paging = query.PageInfo?.ToModel(),
            QueryHints = query.QueryHints,
            ForceSeek = query.ForceSeek,
            DataSource = query.DataSource,
            SubQuery = query.SubQueryExpression?.ToModel(),
        };
        foreach (var o in query.Orders) result.Orders.Add(o?.ToModel()!);
        foreach (var l in query.LinkEntities) result.Links.Add(l?.ToModel()!);
        return result;
    }

    public static QueryExpression ToSdk(this RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = new QueryExpression(query.EntityName)
        {
            ColumnSet = query.Columns?.ToSdk()!,
            Criteria = query.Criteria?.ToSdk()!,
            Distinct = query.Distinct,
            NoLock = query.NoLock,
            TopCount = query.TopCount,
            PageInfo = query.Paging?.ToSdk()!,
            QueryHints = query.QueryHints,
            ForceSeek = query.ForceSeek,
            DataSource = query.DataSource,
            SubQueryExpression = query.SubQuery?.ToSdk(),
        };
        foreach (var o in query.Orders ?? []) result.Orders.Add(o?.ToSdk()!);
        foreach (var l in query.Links ?? []) result.LinkEntities.Add(l?.ToSdk()!);
        return result;
    }

    public static AttributeQuery ToModel(this QueryByAttribute query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = new AttributeQuery
        {
            EntityName = query.EntityName,
            Columns = query.ColumnSet?.ToModel(),
            Paging = query.PageInfo?.ToModel(),
            TopCount = query.TopCount,
        };
        foreach (var a in query.Attributes) result.Attributes.Add(a);
        foreach (var v in query.Values) result.Values.Add(SdkRecords.ValueToModel(v));
        foreach (var o in query.Orders) result.Orders.Add(o?.ToModel()!);
        return result;
    }

    public static QueryByAttribute ToSdk(this AttributeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = new QueryByAttribute(query.EntityName)
        {
            ColumnSet = query.Columns?.ToSdk()!,
            PageInfo = query.Paging?.ToSdk()!,
            TopCount = query.TopCount,
        };
        foreach (var a in query.Attributes ?? []) result.Attributes.Add(a);
        foreach (var v in query.Values ?? []) result.Values.Add(SdkRecords.ValueToSdk(v));
        foreach (var o in query.Orders ?? []) result.Orders.Add(o?.ToSdk()!);
        return result;
    }

    public static FetchQuery ToModel(this FetchExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new FetchQuery { Text = query.Query };
    }

    public static FetchExpression ToSdk(this FetchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new FetchExpression(query.Text);
    }

    public static QueryFilter ToModel(this FilterExpression filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var result = new QueryFilter
        {
            Logic = (FilterLogic)(int)filter.FilterOperator,
            IsQuickFindFilter = filter.IsQuickFindFilter,
            FilterHint = filter.FilterHint,
            AnyAllLink = filter.AnyAllFilterLinkEntity?.ToModel(),
        };
        foreach (var c in filter.Conditions) result.Conditions.Add(c?.ToModel()!);
        foreach (var f in filter.Filters) result.Filters.Add(f?.ToModel()!);
        return result;
    }

    public static FilterExpression ToSdk(this QueryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var result = new FilterExpression((LogicalOperator)(int)filter.Logic)
        {
            IsQuickFindFilter = filter.IsQuickFindFilter,
            FilterHint = filter.FilterHint,
            AnyAllFilterLinkEntity = filter.AnyAllLink?.ToSdk(),
        };
        foreach (var c in filter.Conditions ?? []) result.Conditions.Add(c?.ToSdk()!);
        foreach (var f in filter.Filters ?? []) result.Filters.Add(f?.ToSdk()!);
        return result;
    }

    public static QueryCondition ToModel(this ConditionExpression condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var result = new QueryCondition
        {
            EntityName = condition.EntityName,
            AttributeName = condition.AttributeName,
            Operator = (FilterOperator)(int)condition.Operator,
            CompareColumns = condition.CompareColumns,
        };
        foreach (var v in condition.Values) result.Values.Add(SdkRecords.ValueToModel(v));
        return result;
    }

    public static ConditionExpression ToSdk(this QueryCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var result = new ConditionExpression
        {
            EntityName = condition.EntityName,
            AttributeName = condition.AttributeName,
            Operator = (ConditionOperator)(int)condition.Operator,
            CompareColumns = condition.CompareColumns,
        };
        foreach (var v in condition.Values ?? []) result.Values.Add(SdkRecords.ValueToSdk(v));
        return result;
    }

    public static QueryLink ToModel(this LinkEntity link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var result = new QueryLink
        {
            FromEntityName = link.LinkFromEntityName,
            FromAttributeName = link.LinkFromAttributeName,
            ToEntityName = link.LinkToEntityName,
            ToAttributeName = link.LinkToAttributeName,
            Join = (JoinKind)(int)link.JoinOperator,
            Columns = link.Columns?.ToModel(),
            Criteria = link.LinkCriteria?.ToModel(),
            Alias = link.EntityAlias,
            ForceSeek = link.ForceSeek,
        };
        foreach (var l in link.LinkEntities) result.Links.Add(l?.ToModel()!);
        foreach (var o in link.Orders) result.Orders.Add(o?.ToModel()!);
        return result;
    }

    public static LinkEntity ToSdk(this QueryLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var result = new LinkEntity
        {
            LinkFromEntityName = link.FromEntityName,
            LinkFromAttributeName = link.FromAttributeName,
            LinkToEntityName = link.ToEntityName,
            LinkToAttributeName = link.ToAttributeName,
            JoinOperator = (JoinOperator)(int)link.Join,
            Columns = link.Columns?.ToSdk()!,
            LinkCriteria = link.Criteria?.ToSdk()!,
            EntityAlias = link.Alias,
            ForceSeek = link.ForceSeek,
        };
        foreach (var l in link.Links ?? []) result.LinkEntities.Add(l?.ToSdk()!);
        foreach (var o in link.Orders ?? []) result.Orders.Add(o?.ToSdk()!);
        return result;
    }

    public static QueryOrder ToModel(this OrderExpression order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new QueryOrder
        {
            AttributeName = order.AttributeName,
            Direction = (SortDirection)(int)order.OrderType,
            Alias = order.Alias,
            EntityName = order.EntityName,
        };
    }

    public static OrderExpression ToSdk(this QueryOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new OrderExpression
        {
            AttributeName = order.AttributeName,
            OrderType = (OrderType)(int)order.Direction,
            Alias = order.Alias,
            EntityName = order.EntityName,
        };
    }

    public static QueryPaging ToModel(this PagingInfo paging)
    {
        ArgumentNullException.ThrowIfNull(paging);
        return new QueryPaging
        {
            PageNumber = paging.PageNumber,
            Count = paging.Count,
            ReturnTotalRecordCount = paging.ReturnTotalRecordCount,
            ReturnRecordCountLessThanMarv = paging.ReturnRecordCountLessThanMarv,
            PagingCookie = paging.PagingCookie,
        };
    }

    public static PagingInfo ToSdk(this QueryPaging paging)
    {
        ArgumentNullException.ThrowIfNull(paging);
        return new PagingInfo
        {
            PageNumber = paging.PageNumber,
            Count = paging.Count,
            ReturnTotalRecordCount = paging.ReturnTotalRecordCount,
            ReturnRecordCountLessThanMarv = paging.ReturnRecordCountLessThanMarv,
            PagingCookie = paging.PagingCookie,
        };
    }

    public static ColumnExpression ToModel(this XrmAttributeExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new ColumnExpression
        {
            AttributeName = expression.AttributeName,
            Aggregate = (ColumnAggregate)(int)expression.AggregateType,
            Alias = expression.Alias,
            GroupBy = expression.HasGroupBy,
            DateGrouping = (ColumnDateGrouping)(int)expression.DateTimeGrouping,
        };
    }

    public static XrmAttributeExpression ToSdk(this ColumnExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new XrmAttributeExpression
        {
            AttributeName = expression.AttributeName,
            AggregateType = (XrmAggregateType)(int)expression.Aggregate,
            Alias = expression.Alias,
            HasGroupBy = expression.GroupBy,
            DateTimeGrouping = (XrmDateTimeGrouping)(int)expression.DateGrouping,
        };
    }
}
