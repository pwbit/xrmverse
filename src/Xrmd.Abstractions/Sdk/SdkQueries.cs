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
        var result = new RecordQuery();
        Fill(query, result, SdkRecords.ToModelMap);
        return result;
    }

    public static QueryExpression ToSdk(this RecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = new QueryExpression();
        Fill(query, result, SdkRecords.ToSdkMap);
        return result;
    }

    public static AttributeQuery ToModel(this QueryByAttribute query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = new AttributeQuery();
        Fill(query, result, SdkRecords.ToModelMap);
        return result;
    }

    public static QueryByAttribute ToSdk(this AttributeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = new QueryByAttribute();
        Fill(query, result, SdkRecords.ToSdkMap);
        return result;
    }

    public static FetchQuery ToModel(this FetchExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = new FetchQuery();
        Fill(query, result, SdkRecords.ToModelMap);
        return result;
    }

    public static FetchExpression ToSdk(this FetchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = new FetchExpression(query.Text);
        Fill(query, result, SdkRecords.ToSdkMap);
        return result;
    }

    public static QueryFilter ToModel(this FilterExpression filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var result = new QueryFilter();
        Fill(filter, result, SdkRecords.ToModelMap);
        return result;
    }

    public static FilterExpression ToSdk(this QueryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var result = new FilterExpression();
        Fill(filter, result, SdkRecords.ToSdkMap);
        return result;
    }

    public static QueryCondition ToModel(this ConditionExpression condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var result = new QueryCondition();
        Fill(condition, result, SdkRecords.ToModelMap);
        return result;
    }

    public static ConditionExpression ToSdk(this QueryCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var result = new ConditionExpression();
        Fill(condition, result, SdkRecords.ToSdkMap);
        return result;
    }

    public static QueryLink ToModel(this LinkEntity link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var result = new QueryLink();
        Fill(link, result, SdkRecords.ToModelMap);
        return result;
    }

    public static LinkEntity ToSdk(this QueryLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var result = new LinkEntity();
        Fill(link, result, SdkRecords.ToSdkMap);
        return result;
    }

    public static QueryOrder ToModel(this OrderExpression order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var result = new QueryOrder();
        Fill(order, result);
        return result;
    }

    public static OrderExpression ToSdk(this QueryOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var result = new OrderExpression();
        Fill(order, result);
        return result;
    }

    public static QueryPaging ToModel(this PagingInfo paging)
    {
        ArgumentNullException.ThrowIfNull(paging);
        var result = new QueryPaging();
        Fill(paging, result);
        return result;
    }

    public static PagingInfo ToSdk(this QueryPaging paging)
    {
        ArgumentNullException.ThrowIfNull(paging);
        var result = new PagingInfo();
        Fill(paging, result);
        return result;
    }

    public static ColumnExpression ToModel(this XrmAttributeExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var result = new ColumnExpression();
        Fill(expression, result);
        return result;
    }

    public static XrmAttributeExpression ToSdk(this ColumnExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var result = new XrmAttributeExpression();
        Fill(expression, result);
        return result;
    }

    // Fill: see SdkRecords. Children of a known type go through map; containers are cleared and refilled in source order.

    internal static void Fill(QueryExpression from, RecordQuery into, IFillMap map)
    {
        into.EntityName = from.EntityName;
        into.Columns = (ColumnSelection?)map.Child(from.ColumnSet);
        into.Criteria = (QueryFilter?)map.Child(from.Criteria);
        into.Distinct = from.Distinct;
        into.NoLock = from.NoLock;
        into.TopCount = from.TopCount;
        into.Paging = (QueryPaging?)map.Child(from.PageInfo);
        into.QueryHints = from.QueryHints;
        into.ForceSeek = from.ForceSeek;
        into.DataSource = from.DataSource;
        into.SubQuery = from.SubQueryExpression is { } sub ? (RecordQuery?)map.Child(sub) : null;
        into.Orders.Clear();
        foreach (var o in from.Orders) into.Orders.Add((QueryOrder)map.Child(o)!);
        into.Links.Clear();
        foreach (var l in from.LinkEntities) into.Links.Add((QueryLink)map.Child(l)!);
    }

    internal static void Fill(RecordQuery from, QueryExpression into, IFillMap map)
    {
        into.EntityName = from.EntityName;
        into.ColumnSet = (ColumnSet)map.Child(from.Columns)!;
        into.Criteria = (FilterExpression)map.Child(from.Criteria)!;
        into.Distinct = from.Distinct;
        into.NoLock = from.NoLock;
        into.TopCount = from.TopCount;
        into.PageInfo = (PagingInfo)map.Child(from.Paging)!;
        into.QueryHints = from.QueryHints;
        into.ForceSeek = from.ForceSeek;
        into.DataSource = from.DataSource;
        into.SubQueryExpression = from.SubQuery is { } sub ? (QueryExpression?)map.Child(sub) : null;
        SdkRecords.Refill(into.Orders, [.. (from.Orders ?? []).Select(o => (OrderExpression)map.Child(o)!)]);
        SdkRecords.Refill(into.LinkEntities, [.. (from.Links ?? []).Select(l => (LinkEntity)map.Child(l)!)]);
    }

    internal static void Fill(QueryByAttribute from, AttributeQuery into, IFillMap map)
    {
        into.EntityName = from.EntityName;
        into.Columns = (ColumnSelection?)map.Child(from.ColumnSet);
        into.Paging = (QueryPaging?)map.Child(from.PageInfo);
        into.TopCount = from.TopCount;
        into.Attributes.Clear();
        foreach (var a in from.Attributes) into.Attributes.Add(a);
        into.Values.Clear();
        foreach (var v in from.Values) into.Values.Add(map.Value(v, null));
        into.Orders.Clear();
        foreach (var o in from.Orders) into.Orders.Add((QueryOrder)map.Child(o)!);
    }

    internal static void Fill(AttributeQuery from, QueryByAttribute into, IFillMap map)
    {
        into.EntityName = from.EntityName;
        into.ColumnSet = (ColumnSet)map.Child(from.Columns)!;
        into.PageInfo = (PagingInfo)map.Child(from.Paging)!;
        into.TopCount = from.TopCount;
        SdkRecords.Refill(into.Attributes, [.. from.Attributes ?? []]);
        var old = into.Values.ToArray();
        SdkRecords.Refill(into.Values, [.. (from.Values ?? []).Select((v, i) => map.Value(v, i < old.Length ? old[i] : null)!)]);
        SdkRecords.Refill(into.Orders, [.. (from.Orders ?? []).Select(o => (OrderExpression)map.Child(o)!)]);
    }

    internal static void Fill(FetchExpression from, FetchQuery into, IFillMap map) => into.Text = from.Query;

    internal static void Fill(FetchQuery from, FetchExpression into, IFillMap map) => into.Query = from.Text;

    internal static void Fill(FilterExpression from, QueryFilter into, IFillMap map)
    {
        into.Logic = (FilterLogic)(int)from.FilterOperator;
        into.IsQuickFindFilter = from.IsQuickFindFilter;
        into.FilterHint = from.FilterHint;
        into.AnyAllLink = (QueryLink?)map.Child(from.AnyAllFilterLinkEntity);
        into.Conditions.Clear();
        foreach (var c in from.Conditions) into.Conditions.Add((QueryCondition)map.Child(c)!);
        into.Filters.Clear();
        foreach (var f in from.Filters) into.Filters.Add((QueryFilter)map.Child(f)!);
    }

    internal static void Fill(QueryFilter from, FilterExpression into, IFillMap map)
    {
        into.FilterOperator = (LogicalOperator)(int)from.Logic;
        into.IsQuickFindFilter = from.IsQuickFindFilter;
        into.FilterHint = from.FilterHint;
        into.AnyAllFilterLinkEntity = (LinkEntity?)map.Child(from.AnyAllLink);
        SdkRecords.Refill(into.Conditions, [.. (from.Conditions ?? []).Select(c => (ConditionExpression)map.Child(c)!)]);
        SdkRecords.Refill(into.Filters, [.. (from.Filters ?? []).Select(f => (FilterExpression)map.Child(f)!)]);
    }

    internal static void Fill(ConditionExpression from, QueryCondition into, IFillMap map)
    {
        into.EntityName = from.EntityName;
        into.AttributeName = from.AttributeName;
        into.Operator = (FilterOperator)(int)from.Operator;
        into.CompareColumns = from.CompareColumns;
        into.Values.Clear();
        foreach (var v in from.Values) into.Values.Add(map.Value(v, null));
    }

    internal static void Fill(QueryCondition from, ConditionExpression into, IFillMap map)
    {
        into.EntityName = from.EntityName;
        into.AttributeName = from.AttributeName;
        into.Operator = (ConditionOperator)(int)from.Operator;
        into.CompareColumns = from.CompareColumns;
        var old = into.Values.ToArray();
        SdkRecords.Refill(into.Values, [.. (from.Values ?? []).Select((v, i) => map.Value(v, i < old.Length ? old[i] : null)!)]);
    }

    internal static void Fill(LinkEntity from, QueryLink into, IFillMap map)
    {
        into.FromEntityName = from.LinkFromEntityName;
        into.FromAttributeName = from.LinkFromAttributeName;
        into.ToEntityName = from.LinkToEntityName;
        into.ToAttributeName = from.LinkToAttributeName;
        into.Join = (JoinKind)(int)from.JoinOperator;
        into.Columns = (ColumnSelection?)map.Child(from.Columns);
        into.Criteria = (QueryFilter?)map.Child(from.LinkCriteria);
        into.Alias = from.EntityAlias;
        into.ForceSeek = from.ForceSeek;
        into.Links.Clear();
        foreach (var l in from.LinkEntities) into.Links.Add((QueryLink)map.Child(l)!);
        into.Orders.Clear();
        foreach (var o in from.Orders) into.Orders.Add((QueryOrder)map.Child(o)!);
    }

    internal static void Fill(QueryLink from, LinkEntity into, IFillMap map)
    {
        into.LinkFromEntityName = from.FromEntityName;
        into.LinkFromAttributeName = from.FromAttributeName;
        into.LinkToEntityName = from.ToEntityName;
        into.LinkToAttributeName = from.ToAttributeName;
        into.JoinOperator = (JoinOperator)(int)from.Join;
        into.Columns = (ColumnSet)map.Child(from.Columns)!;
        into.LinkCriteria = (FilterExpression)map.Child(from.Criteria)!;
        into.EntityAlias = from.Alias;
        into.ForceSeek = from.ForceSeek;
        SdkRecords.Refill(into.LinkEntities, [.. (from.Links ?? []).Select(l => (LinkEntity)map.Child(l)!)]);
        SdkRecords.Refill(into.Orders, [.. (from.Orders ?? []).Select(o => (OrderExpression)map.Child(o)!)]);
    }

    internal static void Fill(OrderExpression from, QueryOrder into)
    {
        into.AttributeName = from.AttributeName;
        into.Direction = (SortDirection)(int)from.OrderType;
        into.Alias = from.Alias;
        into.EntityName = from.EntityName;
    }

    internal static void Fill(QueryOrder from, OrderExpression into)
    {
        into.AttributeName = from.AttributeName;
        into.OrderType = (OrderType)(int)from.Direction;
        into.Alias = from.Alias;
        into.EntityName = from.EntityName;
    }

    internal static void Fill(PagingInfo from, QueryPaging into)
    {
        into.PageNumber = from.PageNumber;
        into.Count = from.Count;
        into.ReturnTotalRecordCount = from.ReturnTotalRecordCount;
        into.ReturnRecordCountLessThanMarv = from.ReturnRecordCountLessThanMarv;
        into.PagingCookie = from.PagingCookie;
    }

    internal static void Fill(QueryPaging from, PagingInfo into)
    {
        into.PageNumber = from.PageNumber;
        into.Count = from.Count;
        into.ReturnTotalRecordCount = from.ReturnTotalRecordCount;
        into.ReturnRecordCountLessThanMarv = from.ReturnRecordCountLessThanMarv;
        into.PagingCookie = from.PagingCookie;
    }

    internal static void Fill(XrmAttributeExpression from, ColumnExpression into)
    {
        into.AttributeName = from.AttributeName;
        into.Aggregate = (ColumnAggregate)(int)from.AggregateType;
        into.Alias = from.Alias;
        into.GroupBy = from.HasGroupBy;
        into.DateGrouping = (ColumnDateGrouping)(int)from.DateTimeGrouping;
    }

    internal static void Fill(ColumnExpression from, XrmAttributeExpression into)
    {
        into.AttributeName = from.AttributeName;
        into.AggregateType = (XrmAggregateType)(int)from.Aggregate;
        into.Alias = from.Alias;
        into.HasGroupBy = from.GroupBy;
        into.DateTimeGrouping = (XrmDateTimeGrouping)(int)from.DateGrouping;
    }
}
