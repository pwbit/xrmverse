using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Model.Query;
using Xrmd.Query;
using Xrmd.Abstractions;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

public class QueryExpressionReaderTests
{
    [Fact]
    public void Read_maps_columns_filter_links_orders()
    {
        var q = new QueryExpression("pb_project")
        {
            ColumnSet = new ColumnSet("pb_name"),
            TopCount = 10,
        };
        q.Criteria.AddCondition("pb_count", ConditionOperator.GreaterThan, 5);
        var sub = new FilterExpression(LogicalOperator.Or);
        sub.AddCondition("pb_name", ConditionOperator.Equal, "a");
        sub.AddCondition("pb_name", ConditionOperator.Equal, "b");
        q.Criteria.AddFilter(sub);
        q.LinkEntities.Add(new LinkEntity("pb_project", "systemuser", "pb_customerid", "systemuserid", JoinOperator.LeftOuter)
        {
            EntityAlias = "u",
            Columns = new ColumnSet("fullname"),
        });
        q.Orders.Add(new OrderExpression("pb_name", OrderType.Descending));

        var m = QueryExpressionReader.Read(q);

        m.Root.EntityName.Should().Be("pb_project");
        m.Root.Columns.Should().Equal(new ColumnNode("pb_name"));
        m.Root.Filter.Conditions[0].Should().BeEquivalentTo(
            new ConditionNode(null, "pb_count", FilterOperator.GreaterThan, new object?[] { 5 }));
        m.Root.Filter.Filters.Should().HaveCount(1);
        m.Root.Filter.Filters[0].Operator.Should().Be(FilterLogic.Or);
        var l = m.Root.Links[0];
        l.EntityName.Should().Be("systemuser");
        l.From.Should().Be("systemuserid");
        l.To.Should().Be("pb_customerid");
        l.JoinType.Should().Be(JoinKind.LeftOuter);
        l.Alias.Should().Be("u");
        l.Columns.Should().Equal(new ColumnNode("fullname"));
        m.Root.Orders[0].Descending.Should().BeTrue();
        m.Top.Should().Be(10);
    }

    [Fact]
    public void Read_maps_nested_links_and_link_criteria()
    {
        var q = new QueryExpression("a") { ColumnSet = new ColumnSet(true) };
        var l1 = q.AddLink("b", "bid", "id");
        l1.LinkCriteria.AddCondition("x", ConditionOperator.Equal, 1);
        l1.AddLink("c", "cid", "id");
        l1.Orders.Add(new OrderExpression("x", OrderType.Ascending));

        var m = QueryExpressionReader.Read(q);

        m.Root.AllColumns.Should().BeTrue();
        var b = m.Root.Links[0];
        b.Filter.Conditions.Should().HaveCount(1);
        b.Links[0].EntityName.Should().Be("c");
        b.Orders[0].Descending.Should().BeFalse();
    }

    [Fact]
    public void Read_maps_paging()
    {
        var q = new QueryExpression("pb_project")
        {
            PageInfo = new PagingInfo { Count = 50, PageNumber = 2, PagingCookie = "c", ReturnTotalRecordCount = true },
        };
        var m = QueryExpressionReader.Read(q);
        m.PageSize.Should().Be(50);
        m.PageNumber.Should().Be(2);
        m.PagingCookie.Should().Be("c");
        m.ReturnTotalRecordCount.Should().BeTrue();
    }

    [Fact]
    public void Read_QueryByAttribute_maps_paging()
    {
        var q = new QueryByAttribute("pb_project")
        {
            PageInfo = new PagingInfo { Count = 25, PageNumber = 3, PagingCookie = "k", ReturnTotalRecordCount = true },
        };
        var m = QueryExpressionReader.Read(q);
        m.PageSize.Should().Be(25);
        m.PageNumber.Should().Be(3);
        m.PagingCookie.Should().Be("k");
        m.ReturnTotalRecordCount.Should().BeTrue();
    }

    [Fact]
    public void Read_QueryByAttribute_builds_and_conditions()
    {
        var q = new QueryByAttribute("pb_project") { ColumnSet = new ColumnSet("pb_name") };
        q.AddAttributeValue("pb_name", "x");
        q.AddAttributeValue("pb_count", 3);

        var m = QueryExpressionReader.Read(q);

        m.Root.Filter.Operator.Should().Be(FilterLogic.And);
        m.Root.Filter.Conditions.Should().HaveCount(2);
        m.Root.Filter.Conditions.Should().OnlyContain(c => c.Operator == FilterOperator.Equal);
        m.Root.Filter.Conditions[1].Values.Should().Equal(3);
    }

    [Fact]
    public void Read_condition_with_EntityName_keeps_alias()
    {
        var q = new QueryExpression("pb_project");
        q.Criteria.AddCondition(new ConditionExpression("u", "fullname", ConditionOperator.Equal, "x"));
        var m = QueryExpressionReader.Read(q);
        m.Root.Filter.Conditions[0].EntityAlias.Should().Be("u");
    }

    private static CompiledQuery Compile(QueryExpression q) =>
        new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect)
            .Compile(QueryExpressionReader.Read(q), new QueryContext(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow));

    [Fact]
    public void An_undefined_condition_operator_is_refused_by_the_compiler_as_before()
    {
        var q = new QueryExpression("pb_project") { ColumnSet = new ColumnSet("pb_name") };
        q.Criteria.AddCondition("pb_name", (ConditionOperator)999, "a");

        var act = () => Compile(q);

        act.Should().Throw<XrmdValidationException>().WithMessage("Operator 999 is not supported (attribute 'pb_name').");
    }

    [Fact]
    public void An_undefined_logical_operator_falls_back_to_and_as_before()
    {
        QueryExpression Query(LogicalOperator op)
        {
            var q = new QueryExpression("pb_project") { ColumnSet = new ColumnSet("pb_name") };
            q.Criteria.FilterOperator = op;
            q.Criteria.AddCondition("pb_name", ConditionOperator.Equal, "a");
            q.Criteria.AddCondition("pb_count", ConditionOperator.Equal, 1);
            return q;
        }

        var undefined = Compile(Query((LogicalOperator)7)).Sql;

        undefined.Should().Be(Compile(Query(LogicalOperator.And)).Sql).And.Contain(" AND ");
    }
}
