using Microsoft.Xrm.Sdk.Query;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xunit;

namespace Xrmd.Query.Tests.Sdk;

public class QueryModelParityTests
{
    private static void AssertParity<TSdk, TModel>() where TSdk : struct, Enum where TModel : struct, Enum
    {
        Assert.Equal(Enum.GetNames<TSdk>().OrderBy(n => n, StringComparer.Ordinal), Enum.GetNames<TModel>().OrderBy(n => n, StringComparer.Ordinal));
        foreach (var name in Enum.GetNames<TSdk>())
            Assert.Equal(Convert.ToInt32(Enum.Parse<TSdk>(name)), Convert.ToInt32(Enum.Parse<TModel>(name)));
    }

    [Fact] public void SortDirection_matches_OrderType() => AssertParity<OrderType, SortDirection>();
    [Fact] public void ColumnAggregate_matches_XrmAggregateType() => AssertParity<XrmAggregateType, ColumnAggregate>();
    [Fact] public void ColumnDateGrouping_matches_XrmDateTimeGrouping() => AssertParity<XrmDateTimeGrouping, ColumnDateGrouping>();

    private static void AssertSame(object? sdk, object? model, string what) => Assert.True(Equals(sdk, model), $"{what}: sdk={sdk ?? "null"} model={model ?? "null"}");

    private static void AssertEnum(Enum sdk, Enum model, string what) => AssertSame(Convert.ToInt32(sdk), Convert.ToInt32(model), what);

    private static void AssertColumns(ColumnSet sdk, ColumnSelection? model, string what)
    {
        Assert.Equal(sdk is null, model is null);
        if (sdk is null) return;
        AssertSame(sdk.AllColumns, model!.AllColumns, what + ".AllColumns");
        Assert.Equal(sdk.Columns.Count == 0, model.Columns.Count == 0);
        Assert.Equal(sdk.AttributeExpressions.Count == 0, model.Expressions.Count == 0);
    }

    private static void AssertFilter(FilterExpression sdk, QueryFilter? model, string what)
    {
        Assert.Equal(sdk is null, model is null);
        if (sdk is null) return;
        AssertEnum(sdk.FilterOperator, model!.Logic, what + ".Logic");
        Assert.Equal(sdk.Conditions.Count == 0, model.Conditions.Count == 0);
        Assert.Equal(sdk.Filters.Count == 0, model.Filters.Count == 0);
        AssertSame(sdk.IsQuickFindFilter, model.IsQuickFindFilter, what + ".IsQuickFindFilter");
        AssertSame(sdk.FilterHint, model.FilterHint, what + ".FilterHint");
        Assert.Equal(sdk.AnyAllFilterLinkEntity is null, model.AnyAllLink is null);
    }

    private static void AssertPaging(PagingInfo sdk, QueryPaging? model, string what)
    {
        Assert.Equal(sdk is null, model is null);
        if (sdk is null) return;
        AssertSame(sdk.PageNumber, model!.PageNumber, what + ".PageNumber");
        AssertSame(sdk.Count, model.Count, what + ".Count");
        AssertSame(sdk.ReturnTotalRecordCount, model.ReturnTotalRecordCount, what + ".ReturnTotalRecordCount");
        AssertSame(sdk.ReturnRecordCountLessThanMarv, model.ReturnRecordCountLessThanMarv, what + ".ReturnRecordCountLessThanMarv");
        AssertSame(sdk.PagingCookie, model.PagingCookie, what + ".PagingCookie");
    }

    [Fact]
    public void Defaults_match_the_sdk()
    {
        var q = new QueryExpression(); var m = new RecordQuery();
        AssertSame(q.EntityName, m.EntityName, "RecordQuery.EntityName");
        AssertColumns(q.ColumnSet, m.Columns, "RecordQuery.Columns");
        AssertFilter(q.Criteria, m.Criteria, "RecordQuery.Criteria");
        Assert.Equal(q.Orders.Count == 0, m.Orders.Count == 0);
        Assert.Equal(q.LinkEntities.Count == 0, m.Links.Count == 0);
        AssertSame(q.Distinct, m.Distinct, "RecordQuery.Distinct");
        AssertSame(q.NoLock, m.NoLock, "RecordQuery.NoLock");
        AssertSame(q.TopCount, m.TopCount, "RecordQuery.TopCount");
        AssertPaging(q.PageInfo, m.Paging, "RecordQuery.Paging");
        AssertSame(q.QueryHints, m.QueryHints, "RecordQuery.QueryHints");
        AssertSame(q.ForceSeek, m.ForceSeek, "RecordQuery.ForceSeek");
        AssertSame(q.DataSource, m.DataSource, "RecordQuery.DataSource");
        Assert.Equal(q.SubQueryExpression is null, m.SubQuery is null);

        var qa = new QueryByAttribute(); var ma = new AttributeQuery();
        AssertSame(qa.EntityName, ma.EntityName, "AttributeQuery.EntityName");
        AssertColumns(qa.ColumnSet, ma.Columns, "AttributeQuery.Columns");
        Assert.Equal(qa.Attributes.Count == 0, ma.Attributes.Count == 0);
        Assert.Equal(qa.Values.Count == 0, ma.Values.Count == 0);
        Assert.Equal(qa.Orders.Count == 0, ma.Orders.Count == 0);
        AssertPaging(qa.PageInfo, ma.Paging, "AttributeQuery.Paging");
        AssertSame(qa.TopCount, ma.TopCount, "AttributeQuery.TopCount");

        var f = new FilterExpression(); var mf = new QueryFilter();
        AssertFilter(f, mf, "QueryFilter");

        var c = new ConditionExpression(); var mc = new QueryCondition();
        AssertSame(c.EntityName, mc.EntityName, "QueryCondition.EntityName");
        AssertSame(c.AttributeName, mc.AttributeName, "QueryCondition.AttributeName");
        AssertEnum(c.Operator, mc.Operator, "QueryCondition.Operator");
        Assert.Equal(c.Values.Count == 0, mc.Values.Count == 0);
        AssertSame(c.CompareColumns, mc.CompareColumns, "QueryCondition.CompareColumns");

        var l = new LinkEntity(); var ml = new QueryLink();
        AssertSame(l.LinkFromEntityName, ml.FromEntityName, "QueryLink.FromEntityName");
        AssertSame(l.LinkFromAttributeName, ml.FromAttributeName, "QueryLink.FromAttributeName");
        AssertSame(l.LinkToEntityName, ml.ToEntityName, "QueryLink.ToEntityName");
        AssertSame(l.LinkToAttributeName, ml.ToAttributeName, "QueryLink.ToAttributeName");
        AssertEnum(l.JoinOperator, ml.Join, "QueryLink.Join");
        AssertColumns(l.Columns, ml.Columns, "QueryLink.Columns");
        AssertFilter(l.LinkCriteria, ml.Criteria, "QueryLink.Criteria");
        Assert.Equal(l.LinkEntities.Count == 0, ml.Links.Count == 0);
        AssertSame(l.EntityAlias, ml.Alias, "QueryLink.Alias");
        AssertSame(l.ForceSeek, ml.ForceSeek, "QueryLink.ForceSeek");
        Assert.Equal(l.Orders.Count == 0, ml.Orders.Count == 0);

        var o = new OrderExpression(); var mo = new QueryOrder();
        AssertSame(o.AttributeName, mo.AttributeName, "QueryOrder.AttributeName");
        AssertEnum(o.OrderType, mo.Direction, "QueryOrder.Direction");
        AssertSame(o.Alias, mo.Alias, "QueryOrder.Alias");
        AssertSame(o.EntityName, mo.EntityName, "QueryOrder.EntityName");

        AssertPaging(new PagingInfo(), new QueryPaging(), "QueryPaging");

        var x = new XrmAttributeExpression(); var mx = new ColumnExpression();
        AssertSame(x.AttributeName, mx.AttributeName, "ColumnExpression.AttributeName");
        AssertEnum(x.AggregateType, mx.Aggregate, "ColumnExpression.Aggregate");
        AssertSame(x.Alias, mx.Alias, "ColumnExpression.Alias");
        AssertSame(x.HasGroupBy, mx.GroupBy, "ColumnExpression.GroupBy");
        AssertEnum(x.DateTimeGrouping, mx.DateGrouping, "ColumnExpression.DateGrouping");
    }
}
