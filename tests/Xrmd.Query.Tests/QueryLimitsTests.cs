using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Model.Query;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

/// <summary>
/// Structural limits (<see cref="QueryLimits"/>): nesting depth, parameter count, and the input checks of the final
/// review (link conditions on later link-entities, distinct without the primary key, unsupported QueryExpression
/// features). The deep inputs run on the default test thread: a recursion without the limit would crash the test host.
/// </summary>
public class QueryLimitsTests
{
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc));

    private static CompiledQuery Compile(QueryModel q) => new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(q, Context);

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static QueryModel Project(params string[] columns)
    {
        var root = new EntityNode { EntityName = "pb_project" };
        root.Columns.AddRange(columns.Select(c => new ColumnNode(c)));
        return new QueryModel { Root = root };
    }

    private static string NestedFiltersFetch(int depth)
    {
        var sb = new StringBuilder("<fetch><entity name=\"pb_project\">");
        for (var i = 0; i < depth; i++) sb.Append("<filter>");
        sb.Append("<condition attribute=\"pb_name\" operator=\"eq\" value=\"x\"/>");
        for (var i = 0; i < depth; i++) sb.Append("</filter>");
        return sb.Append("</entity></fetch>").ToString();
    }

    private static string NestedLinksFetch(int depth)
    {
        var sb = new StringBuilder("<fetch><entity name=\"pb_project\">");
        sb.Append("<link-entity name=\"systemuser\" from=\"systemuserid\" to=\"pb_customerid\">");
        for (var i = 1; i < depth; i++) sb.Append("<link-entity name=\"systemuser\" from=\"systemuserid\" to=\"createdby\">");
        for (var i = 0; i < depth; i++) sb.Append("</link-entity>");
        return sb.Append("</entity></fetch>").ToString();
    }

    /// <summary>A filter tree <paramref name="depth"/> levels deep (the root filter is level 1).</summary>
    private static FilterNode NestedFilters(int depth)
    {
        var root = new FilterNode();
        var current = root;
        for (var i = 1; i < depth; i++)
        {
            var child = new FilterNode();
            current.Filters.Add(child);
            current = child;
        }
        current.Conditions.Add(new ConditionNode(null, "pb_name", FilterOperator.Equal, ["x"]));
        return root;
    }

    /// <summary>Link-entities <paramref name="depth"/> levels deep: pb_project → systemuser → systemuser (createdby)...</summary>
    private static QueryModel NestedLinks(int depth)
    {
        var q = Project("pb_name");
        EntityNode parent = q.Root;
        for (var i = 0; i < depth; i++)
        {
            var link = new LinkNode
            {
                EntityName = "systemuser",
                From = "systemuserid",
                To = i == 0 ? "pb_customerid" : "createdby",
                JoinType = JoinKind.LeftOuter,
            };
            parent.Links.Add(link);
            parent = link;
        }
        return q;
    }

    // ---- filter depth -----------------------------------------------------------------------------------------------

    [Fact]
    public void FetchXml_with_10000_nested_filters_is_a_validation_error()
    {
        FluentActions.Invoking(() => FetchXmlParser.Parse(NestedFiltersFetch(10_000)))
            .Should().Throw<XrmdValidationException>().WithMessage("*nested more than 32*");
    }

    [Fact]
    public void FetchXml_filter_depth_limit_is_32()
    {
        FetchXmlParser.Parse(NestedFiltersFetch(QueryLimits.MaxFilterDepth)).Root.Filter.Should().NotBeNull();
        FluentActions.Invoking(() => FetchXmlParser.Parse(NestedFiltersFetch(QueryLimits.MaxFilterDepth + 1)))
            .Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void FetchXml_filter_at_the_limit_compiles()
    {
        var c = Compile(FetchXmlParser.Parse(NestedFiltersFetch(QueryLimits.MaxFilterDepth)));
        c.Sql.Should().Contain("t0.[pb_name] = @p0");
    }

    [Fact]
    public void FetchXml_sibling_filters_count_the_and_wrapper()
    {
        // Two top-level filters are wrapped in one AND: 32 levels each become 33 in the AST.
        var inner = NestedFiltersFetch(QueryLimits.MaxFilterDepth);
        var body = inner["<fetch><entity name=\"pb_project\">".Length..^"</entity></fetch>".Length];
        FluentActions.Invoking(() => FetchXmlParser.Parse($"<fetch><entity name=\"pb_project\">{body}{body}</entity></fetch>"))
            .Should().Throw<XrmdValidationException>().WithMessage("*nested more than 32*");
    }

    [Fact]
    public void Compiler_rejects_a_10000_deep_filter_ast()
    {
        var q = Project("pb_name");
        q.Root.Filter = NestedFilters(10_000);
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*nested more than 32*");
    }

    [Fact]
    public void Compiler_filter_depth_limit_is_32()
    {
        var q = Project("pb_name");
        q.Root.Filter = NestedFilters(QueryLimits.MaxFilterDepth);
        Compile(q).Sql.Should().Contain("@p0");
        q.Root.Filter = NestedFilters(QueryLimits.MaxFilterDepth + 1);
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void Compiler_checks_the_depth_of_link_filters()
    {
        var q = NestedLinks(1);
        q.Root.Links[0].Filter = NestedFilters(QueryLimits.MaxFilterDepth + 1);
        q.Root.Links[0].Filter.Filters[0].Conditions.Add(new ConditionNode(null, "fullname", FilterOperator.Equal, ["x"]));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*nested more than 32*");
    }

    [Fact]
    public void QueryExpression_with_10000_nested_filters_is_a_validation_error()
    {
        var q = new QueryExpression("pb_project") { ColumnSet = new ColumnSet("pb_name") };
        var current = q.Criteria;
        for (var i = 1; i < 10_000; i++)
        {
            var child = new FilterExpression();
            current.AddFilter(child);
            current = child;
        }
        FluentActions.Invoking(() => QueryExpressionReader.Read(q))
            .Should().Throw<XrmdValidationException>().WithMessage("*nested more than 32*");
    }

    // ---- link depth -------------------------------------------------------------------------------------------------

    [Fact]
    public void FetchXml_link_depth_limit_is_10()
    {
        var q = FetchXmlParser.Parse(NestedLinksFetch(QueryLimits.MaxLinkDepth));
        Compile(q).Sql.Should().Contain("AS t10 ON");
        FluentActions.Invoking(() => FetchXmlParser.Parse(NestedLinksFetch(QueryLimits.MaxLinkDepth + 1)))
            .Should().Throw<XrmdValidationException>().WithMessage("*nested more than 10*");
        FluentActions.Invoking(() => FetchXmlParser.Parse(NestedLinksFetch(10_000)))
            .Should().Throw<XrmdValidationException>().WithMessage("*nested more than 10*");
    }

    [Fact]
    public void Compiler_link_depth_limit_is_10()
    {
        Compile(NestedLinks(QueryLimits.MaxLinkDepth)).Sql.Should().Contain("AS t10 ON");
        FluentActions.Invoking(() => Compile(NestedLinks(QueryLimits.MaxLinkDepth + 1)))
            .Should().Throw<XrmdValidationException>().WithMessage("*nested more than 10*");
        FluentActions.Invoking(() => Compile(NestedLinks(10_000)))
            .Should().Throw<XrmdValidationException>().WithMessage("*nested more than 10*");
    }

    [Fact]
    public void QueryExpression_link_depth_limit_is_10()
    {
        var q = new QueryExpression("pb_project") { ColumnSet = new ColumnSet("pb_name") };
        var link = q.AddLink("systemuser", "pb_customerid", "systemuserid");
        for (var i = 1; i <= QueryLimits.MaxLinkDepth; i++) link = link.AddLink("systemuser", "createdby", "systemuserid");
        FluentActions.Invoking(() => QueryExpressionReader.Read(q))
            .Should().Throw<XrmdValidationException>().WithMessage("*nested more than 10*");
    }

    // ---- parameters -------------------------------------------------------------------------------------------------

    [Fact]
    public void Compiler_rejects_more_than_2000_parameters()
    {
        var q = Project("pb_name");
        q.Root.Filter.Operator = FilterLogic.Or;
        for (var i = 0; i < 2100; i++)
            q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_count", FilterOperator.Equal, [i]));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*2000*");
    }

    [Fact]
    public void Compiler_counts_paging_parameters_in_the_limit()
    {
        var q = Project("pb_name");
        q.Root.Filter.Operator = FilterLogic.Or;
        for (var i = 0; i < QueryLimits.MaxParameters; i++)
            q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_count", FilterOperator.Equal, [i]));
        // 2000 condition parameters + @top.
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>();
        q.Root.Filter.Conditions.RemoveAt(0);
        Compile(q).Parameters.Should().HaveCount(QueryLimits.MaxParameters);
    }

    // ---- link conditions on later link-entities ---------------------------------------------------------------------

    private static QueryModel TwoLinks()
    {
        var q = Project("pb_name");
        q.Root.Links.Add(new LinkNode { EntityName = "systemuser", From = "systemuserid", To = "pb_customerid", Alias = "a" });
        q.Root.Links.Add(new LinkNode { EntityName = "systemuser", From = "systemuserid", To = "createdby", Alias = "b" });
        return q;
    }

    [Fact]
    public void Link_condition_on_a_later_link_is_rejected()
    {
        var q = TwoLinks();
        q.Root.Links[0].Filter.Conditions.Add(new ConditionNode("b", "fullname", FilterOperator.Equal, ["x"]));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*'a'*'b'*joined after*");
    }

    [Fact]
    public void Link_condition_on_an_earlier_link_or_itself_is_accepted()
    {
        var q = TwoLinks();
        q.Root.Links[1].Filter.Conditions.Add(new ConditionNode("a", "fullname", FilterOperator.Equal, ["x"]));
        q.Root.Links[1].Filter.Conditions.Add(new ConditionNode("b", "fullname", FilterOperator.Equal, ["y"]));
        var sql = Normalize(Compile(q).Sql);
        sql.Should().Contain("AS t2 ON t2.[systemuserid] = t0.[createdby] AND (t1.[fullname] = @p0 AND t2.[fullname] = @p1)");
    }

    [Fact]
    public void Root_condition_on_any_link_is_accepted()
    {
        var q = TwoLinks();
        q.Root.Filter.Conditions.Add(new ConditionNode("b", "fullname", FilterOperator.Equal, ["x"]));
        Normalize(Compile(q).Sql).Should().Contain("WHERE (t2.[fullname] = @p0)");
    }

    // ---- distinct and the primary key -------------------------------------------------------------------------------

    [Fact]
    public void Distinct_does_not_select_the_primary_key_unless_requested()
    {
        var q = Project("pb_name");
        q.Distinct = true;
        q.Top = 10;
        var c = Compile(q);
        Normalize(c.Sql).Should().StartWith("SELECT DISTINCT TOP (@top) t0.[pb_name] AS c0 FROM");
        c.Columns.Select(x => x.ResultName).Should().Equal("pb_name");
    }

    [Fact]
    public void Distinct_selects_the_primary_key_when_requested()
    {
        var q = Project("pb_name", "pb_projectid");
        q.Distinct = true;
        q.Top = 10;
        Compile(q).Columns.Select(x => x.ResultName).Should().Equal("pb_name", "pb_projectid");

        var all = Project();
        all.Root.AllColumns = true;
        all.Distinct = true;
        all.Top = 10;
        Compile(all).Columns[0].ResultName.Should().Be("pb_projectid");
    }

    [Fact]
    public void Distinct_without_any_column_selects_the_primary_key()
    {
        var q = Project();
        q.Distinct = true;
        q.Top = 10;
        Normalize(Compile(q).Sql).Should().StartWith("SELECT DISTINCT TOP (@top) t0.[pb_projectid] AS c0 FROM");
    }

    [Fact]
    public void Distinct_tiebreak_follows_the_select_list()
    {
        var q = Project("pb_count", "pb_name", "pb_ratio");
        q.Distinct = true;
        q.Root.Orders.Add(new OrderNode("pb_name", true));
        Normalize(Compile(q).Sql).Should().EndWith("ORDER BY t0.[pb_name] DESC, c0 ASC, c2 ASC");
    }

    // ---- unsupported QueryExpression features -----------------------------------------------------------------------

    [Fact]
    public void QueryExpression_compare_columns_is_not_supported()
    {
        var q = new QueryExpression("pb_project") { ColumnSet = new ColumnSet("pb_name") };
        q.Criteria.AddCondition(new ConditionExpression("pb_name", ConditionOperator.Equal, true, "pb_name"));
        FluentActions.Invoking(() => QueryExpressionReader.Read(q))
            .Should().Throw<XrmdValidationException>().WithMessage("*CompareColumns*not supported*");
    }

    [Fact]
    public void QueryExpression_attribute_expressions_are_not_supported()
    {
        var columns = new ColumnSet("pb_name");
        columns.AttributeExpressions.Add(new XrmAttributeExpression("pb_count", XrmAggregateType.Sum, "total"));
        var q = new QueryExpression("pb_project") { ColumnSet = columns };
        FluentActions.Invoking(() => QueryExpressionReader.Read(q))
            .Should().Throw<XrmdValidationException>().WithMessage("*AttributeExpressions*not supported*");
    }
}
