using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Query;
using Xrmd.Model.Records;

namespace Xrmd.Pipeline.Tests.Sdk;

public class QueryConversionTests
{
    private static ColumnSet Columns()
    {
        var cs = new ColumnSet("name", "revenue");
        cs.AttributeExpressions.Add(new XrmAttributeExpression("revenue", XrmAggregateType.Sum, "total"));
        cs.AttributeExpressions.Add(new XrmAttributeExpression("createdon", XrmAggregateType.None, "m")
        {
            HasGroupBy = true,
            DateTimeGrouping = XrmDateTimeGrouping.Month,
        });
        return cs;
    }

    private static QueryExpression KitchenSink()
    {
        var anyAll = new LinkEntity("account", "task", "accountid", "regardingobjectid", JoinOperator.Any)
        {
            EntityAlias = "t",
        };
        anyAll.LinkCriteria.AddCondition("statecode", ConditionOperator.Equal, 0);
        var nested = new FilterExpression(LogicalOperator.And);
        nested.AddCondition("a", ConditionOperator.NotNull);
        var criteria = new FilterExpression(LogicalOperator.Or)
        {
            IsQuickFindFilter = true,
            FilterHint = "hint",
            AnyAllFilterLinkEntity = anyAll,
        };
        criteria.Filters.Add(nested);
        criteria.AddCondition("revenue", ConditionOperator.GreaterThan, new Money(5m));
        criteria.AddCondition("status", ConditionOperator.Equal, new OptionSetValue(2));
        criteria.AddCondition("owner", ConditionOperator.Equal, new EntityReference("systemuser", Guid.NewGuid()) { Name = "Me" });
        criteria.AddCondition("id", ConditionOperator.Equal, Guid.NewGuid());
        criteria.AddCondition("name", ConditionOperator.Like, "Con%");
        criteria.AddCondition("count", ConditionOperator.Equal, 3);
        criteria.AddCondition("empty", ConditionOperator.Equal, new object?[] { null });
        criteria.AddCondition("code", ConditionOperator.In, new[] { 1, 2, 3 });
        criteria.Conditions.Add(new ConditionExpression("contact", "fullname", ConditionOperator.Equal, "x"));
        criteria.Conditions.Add(new ConditionExpression { AttributeName = "a", Operator = ConditionOperator.Equal, CompareColumns = true });
        criteria.Conditions[^1].Values.Add("b");

        var link = new LinkEntity("account", "contact", "primarycontactid", "contactid", JoinOperator.LeftOuter)
        {
            EntityAlias = "pc",
            ForceSeek = "ix",
            Columns = new ColumnSet("fullname"),
        };
        link.LinkCriteria.AddCondition("statecode", ConditionOperator.Equal, 0);
        link.Orders.Add(new OrderExpression("fullname", OrderType.Descending));
        link.LinkEntities.Add(new LinkEntity("contact", "systemuser", "ownerid", "systemuserid", JoinOperator.Inner));

        var q = new QueryExpression("account")
        {
            ColumnSet = Columns(),
            Criteria = criteria,
            Distinct = true,
            NoLock = true,
            TopCount = 7,
            QueryHints = "hint",
            ForceSeek = "seek",
            DataSource = "ds",
            SubQueryExpression = new QueryExpression("contact") { ColumnSet = new ColumnSet(true) },
            PageInfo = new PagingInfo { PageNumber = 2, Count = 50, ReturnTotalRecordCount = true, ReturnRecordCountLessThanMarv = true, PagingCookie = "<cookie/>" },
        };
        q.Orders.Add(new OrderExpression("name", OrderType.Ascending));
        q.Orders.Add(new OrderExpression { AttributeName = "x", OrderType = OrderType.Descending, Alias = "al", EntityName = "contact" });
        q.LinkEntities.Add(link);
        return q;
    }

    [Fact]
    public void Kitchen_sink_QueryExpression_round_trips()
    {
        var q = KitchenSink();
        var model = Assert.IsType<RecordQuery>(((QueryBase)q).ToModel());
        var back = Assert.IsType<QueryExpression>(model.ToSdk());
        DeepEqual.Assert(q, back);
        Assert.NotSame(q, back);

        Assert.Equal(["revenue", "status", "owner", "id", "name", "count", "empty", "code", "fullname", "a"],
            model.Criteria!.Conditions.Select(c => c.AttributeName!).ToArray());
        Assert.IsType<MoneyValue>(model.Criteria.Conditions[0].Values[0]);
        Assert.IsType<ChoiceValue>(model.Criteria.Conditions[1].Values[0]);
        Assert.IsType<RecordReference>(model.Criteria.Conditions[2].Values[0]);
        Assert.Equal(new[] { 1, 2, 3 }, Assert.IsType<int[]>(Assert.Single(model.Criteria.Conditions[7].Values)));
        Assert.Equal(2, model.Columns!.Expressions.Count);
        Assert.True(model.Columns.Expressions[1].GroupBy);
        Assert.Equal(ColumnDateGrouping.Month, model.Columns.Expressions[1].DateGrouping);
        Assert.NotNull(model.SubQuery);
    }

    [Fact]
    public void QueryByAttribute_round_trips()
    {
        var q = new QueryByAttribute("account")
        {
            ColumnSet = Columns(),
            TopCount = 4,
            PageInfo = new PagingInfo { PageNumber = 1, Count = 10, PagingCookie = "c" },
        };
        q.AddAttributeValue("name", "Contoso");
        q.AddAttributeValue("owner", new EntityReference("systemuser", Guid.NewGuid()));
        q.AddOrder("name", OrderType.Descending);
        var model = Assert.IsType<AttributeQuery>(((QueryBase)q).ToModel());
        Assert.IsType<RecordReference>(model.Values[1]);
        DeepEqual.Assert(q, model.ToSdk());
    }

    [Fact]
    public void FetchExpression_round_trips()
    {
        var q = new FetchExpression("<fetch top='1'><entity name='account'/></fetch>");
        var model = Assert.IsType<FetchQuery>(((QueryBase)q).ToModel());
        Assert.Equal(q.Query, model.Text);
        DeepEqual.Assert(q, model.ToSdk());
    }

    [Fact]
    public void Null_members_stay_null()
    {
        // SDK LinkEntity.Columns coerces null to a new ColumnSet, so a null link Columns is not asserted.
        var link = new LinkEntity("a", "b", "x", "y", JoinOperator.Inner) { Columns = null!, LinkCriteria = null! };
        var q = new QueryExpression("account") { ColumnSet = null!, Criteria = null!, PageInfo = null! };
        q.LinkEntities.Add(link);
        var model = q.ToModel();
        Assert.Null(model.Columns);
        Assert.Null(model.Criteria);
        Assert.Null(model.Paging);
        Assert.Null(model.SubQuery);
        Assert.Null(model.Links[0].Criteria);
        var back = model.ToSdk();
        Assert.Null(back.ColumnSet);
        Assert.Null(back.Criteria);
        Assert.Null(back.PageInfo);
        Assert.Null(back.SubQueryExpression);
        Assert.Null(back.LinkEntities[0].LinkCriteria);
    }

    [Fact]
    public void Undefined_enum_values_survive()
    {
        var q = new QueryExpression("account");
        q.Criteria = new FilterExpression((LogicalOperator)5);
        q.Criteria.Conditions.Add(new ConditionExpression("a", (ConditionOperator)999, 1));
        q.LinkEntities.Add(new LinkEntity("a", "b", "x", "y", (JoinOperator)77));
        var model = q.ToModel();
        Assert.Equal((FilterLogic)5, model.Criteria!.Logic);
        Assert.Equal((FilterOperator)999, model.Criteria.Conditions[0].Operator);
        Assert.Equal((JoinKind)77, model.Links[0].Join);
        DeepEqual.Assert(q, model.ToSdk());
    }

    [Fact]
    public void Model_lists_are_not_the_SDK_collections()
    {
        var q = new QueryExpression("account");
        q.Criteria.AddCondition("a", ConditionOperator.Equal, 1);
        var model = q.ToModel();
        model.Criteria!.Conditions[0].Values.Add(2);
        Assert.Single(q.Criteria.Conditions[0].Values);
    }
}
