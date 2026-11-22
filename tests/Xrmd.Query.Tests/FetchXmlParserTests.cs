using FluentAssertions;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;

namespace Xrmd.Query.Tests;

public class FetchXmlParserTests
{
    [Fact]
    public void Parse_full_query()
    {
        var q = FetchXmlParser.Parse("""
            <fetch top="5" distinct="true" returntotalrecordcount="1" page="2" paging-cookie="abc">
              <entity name="pb_project">
                <attribute name="pb_name"/>
                <attribute name="pb_count" alias="c"/>
                <filter type="and">
                  <condition attribute="pb_name" operator="eq" value="x"/>
                  <filter type="or">
                    <condition attribute="pb_count" operator="gt" value="3"/>
                    <condition entityname="o" attribute="name" operator="null"/>
                  </filter>
                </filter>
                <link-entity name="account" from="accountid" to="pb_accountid" link-type="outer" alias="o">
                  <attribute name="name"/>
                  <order attribute="name"/>
                </link-entity>
                <order attribute="pb_name" descending="true"/>
              </entity>
            </fetch>
            """);
        q.Top.Should().Be(5);
        q.Distinct.Should().BeTrue();
        q.ReturnTotalRecordCount.Should().BeTrue();
        q.PageNumber.Should().Be(2);
        q.PagingCookie.Should().Be("abc");
        q.Root.EntityName.Should().Be("pb_project");
        q.Root.AllColumns.Should().BeFalse();
        q.Root.Columns.Should().Equal(new ColumnNode("pb_name"), new ColumnNode("pb_count", "c"));
        q.Root.Filter.Operator.Should().Be(LogicalOperator.And);
        var cond = q.Root.Filter.Conditions.Should().ContainSingle().Subject;
        cond.Attribute.Should().Be("pb_name");
        cond.Operator.Should().Be(ConditionOperator.Equal);
        cond.Values.Should().Equal("x");
        var inner = q.Root.Filter.Filters.Should().ContainSingle().Subject;
        inner.Operator.Should().Be(LogicalOperator.Or);
        inner.Conditions.Should().HaveCount(2);
        inner.Conditions[1].EntityAlias.Should().Be("o");
        inner.Conditions[1].Operator.Should().Be(ConditionOperator.Null);
        inner.Conditions[1].Values.Should().BeEmpty();
        var link = q.Root.Links.Should().ContainSingle().Subject;
        link.EntityName.Should().Be("account");
        link.From.Should().Be("accountid");
        link.To.Should().Be("pb_accountid");
        link.JoinType.Should().Be(JoinOperator.LeftOuter);
        link.Alias.Should().Be("o");
        link.Columns.Should().Equal(new ColumnNode("name"));
        link.Orders.Should().Equal(new OrderNode("name", false));
        q.Root.Orders.Should().Equal(new OrderNode("pb_name", true));
    }

    [Fact]
    public void Parse_combines_sibling_filters_with_and()
    {
        var q = FetchXmlParser.Parse("""
            <fetch><entity name="a">
            <filter type="or"><condition attribute="x" operator="null"/></filter>
            <filter><condition attribute="y" operator="null"/></filter>
            </entity></fetch>
            """);
        q.Root.Filter.Operator.Should().Be(LogicalOperator.And);
        q.Root.Filter.Conditions.Should().BeEmpty();
        q.Root.Filter.Filters.Select(f => f.Operator).Should().Equal(LogicalOperator.Or, LogicalOperator.And);
        q.Root.Filter.Filters.Select(f => f.Conditions.Single().Attribute).Should().Equal("x", "y");

        var q3 = FetchXmlParser.Parse("""
            <fetch><entity name="a">
            <filter><condition attribute="x" operator="null"/></filter>
            <filter type="or"><condition attribute="y" operator="null"/></filter>
            <filter><condition attribute="z" operator="null"/></filter>
            </entity></fetch>
            """);
        q3.Root.Filter.Operator.Should().Be(LogicalOperator.And);
        q3.Root.Filter.Filters.Select(f => f.Conditions.Single().Attribute).Should().Equal("x", "y", "z");
    }

    [Fact]
    public void Parse_defaults_and_all_attributes()
    {
        var q = FetchXmlParser.Parse("""<fetch count="10"><entity name="a"><all-attributes/><link-entity name="b" from="x" to="y"/></entity></fetch>""");
        q.PageSize.Should().Be(10);
        q.Top.Should().BeNull();
        q.PageNumber.Should().Be(1);
        q.Root.AllColumns.Should().BeTrue();
        q.Root.Links[0].JoinType.Should().Be(JoinOperator.Inner);
    }

    [Theory]
    [InlineData("eq", ConditionOperator.Equal)]
    [InlineData("ne", ConditionOperator.NotEqual)]
    [InlineData("gt", ConditionOperator.GreaterThan)]
    [InlineData("ge", ConditionOperator.GreaterEqual)]
    [InlineData("lt", ConditionOperator.LessThan)]
    [InlineData("le", ConditionOperator.LessEqual)]
    [InlineData("like", ConditionOperator.Like)]
    [InlineData("not-like", ConditionOperator.NotLike)]
    [InlineData("in", ConditionOperator.In)]
    [InlineData("not-in", ConditionOperator.NotIn)]
    [InlineData("null", ConditionOperator.Null)]
    [InlineData("not-null", ConditionOperator.NotNull)]
    [InlineData("begins-with", ConditionOperator.BeginsWith)]
    [InlineData("not-begin-with", ConditionOperator.DoesNotBeginWith)]
    [InlineData("ends-with", ConditionOperator.EndsWith)]
    [InlineData("not-end-with", ConditionOperator.DoesNotEndWith)]
    [InlineData("between", ConditionOperator.Between)]
    [InlineData("not-between", ConditionOperator.NotBetween)]
    [InlineData("on", ConditionOperator.On)]
    [InlineData("on-or-after", ConditionOperator.OnOrAfter)]
    [InlineData("on-or-before", ConditionOperator.OnOrBefore)]
    [InlineData("last-x-days", ConditionOperator.LastXDays)]
    [InlineData("next-x-days", ConditionOperator.NextXDays)]
    [InlineData("last-x-months", ConditionOperator.LastXMonths)]
    [InlineData("this-month", ConditionOperator.ThisMonth)]
    [InlineData("this-year", ConditionOperator.ThisYear)]
    [InlineData("eq-userid", ConditionOperator.EqualUserId)]
    [InlineData("ne-userid", ConditionOperator.NotEqualUserId)]
    [InlineData("eq-businessid", ConditionOperator.EqualBusinessId)]
    [InlineData("eq-userteams", ConditionOperator.EqualUserTeams)]
    public void Parse_operators(string op, ConditionOperator expected)
    {
        var q = FetchXmlParser.Parse($"""<fetch><entity name="a"><filter><condition attribute="x" operator="{op}"/></filter></entity></fetch>""");
        q.Root.Filter.Conditions.Single().Operator.Should().Be(expected);
    }

    [Fact]
    public void Parse_in_reads_value_children()
    {
        var q = FetchXmlParser.Parse("""<fetch><entity name="a"><filter><condition attribute="pb_count" operator="in"><value>1</value><value>2</value></condition></filter></entity></fetch>""");
        q.Root.Filter.Conditions.Single().Values.Should().Equal("1", "2");
    }

    [Fact]
    public void Parse_aggregate()
    {
        var q = FetchXmlParser.Parse("""
            <fetch aggregate="true"><entity name="a">
              <attribute name="pb_count" alias="total" aggregate="sum"/>
              <attribute name="pb_priority" alias="p" groupby="true"/>
              <attribute name="createdon" alias="m" groupby="true" dategrouping="month"/>
              <attribute name="pb_name" alias="n" aggregate="countcolumn" distinct="true"/>
              <order alias="total" descending="1"/>
            </entity></fetch>
            """);
        q.Aggregate.Should().BeTrue();
        q.Root.Columns.Should().Equal(
            new ColumnNode("pb_count", "total", XrmAggregate.Sum),
            new ColumnNode("pb_priority", "p", null, true),
            new ColumnNode("createdon", "m", null, true, false, DateGrouping.Month),
            new ColumnNode("pb_name", "n", XrmAggregate.CountColumn, false, true));
        q.Root.Orders.Should().Equal(new OrderNode("total", true, "total"));
    }

    [Fact] public void Parse_rejects_malformed_xml() => Rejects("<fetch><entity");
    [Fact] public void Parse_rejects_dtd() => Rejects("""<!DOCTYPE fetch [<!ENTITY x "y">]><fetch><entity name="a"/></fetch>""");
    [Fact] public void Parse_rejects_unknown_operator() => Rejects("""<fetch><entity name="a"><filter><condition attribute="x" operator="foo"/></filter></entity></fetch>""");
    [Fact] public void Parse_rejects_count_over_5000() => Rejects("""<fetch count="5001"><entity name="a"/></fetch>""");
    [Fact] public void Parse_rejects_top_with_count() => Rejects("""<fetch top="5" count="5"><entity name="a"/></fetch>""");
    [Fact] public void Parse_rejects_unknown_element() => Rejects("""<fetch><entity name="a"><bogus/></entity></fetch>""");
    [Fact] public void Parse_rejects_bad_link_type() => Rejects("""<fetch><entity name="a"><link-entity name="b" from="x" to="y" link-type="foo"/></entity></fetch>""");
    [Fact] public void Parse_rejects_missing_entity() => Rejects("<fetch/>");

    private static void Rejects(string xml)
    {
        var act = () => FetchXmlParser.Parse(xml);
        act.Should().Throw<XrmdValidationException>();
    }
}
