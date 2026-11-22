using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

public class AggregateCompilerTests
{
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc));

    private static CompiledQuery Compile(QueryModel q) => new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(q, Context);

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static object? Param(CompiledQuery c, string name) => c.Parameters.Single(p => p.ParameterName == name).Value;

    private static QueryModel Aggregate(params ColumnNode[] columns)
    {
        var root = new EntityNode { EntityName = "pb_project" };
        root.Columns.AddRange(columns);
        return new QueryModel { Root = root, Aggregate = true };
    }

    [Fact]
    public void Groupby_lookup_has_a_name_column_and_groups_by_id()
    {
        var c = Compile(Aggregate(new ColumnNode("pb_customerid", "cust", GroupBy: true), new ColumnNode("pb_projectid", "n", XrmAggregate.Count)));
        var name = c.Columns.Should().ContainSingle(x => x.Kind == ResultKind.LookupName).Subject;
        name.ResultName.Should().Be("cust");
        var sql = Normalize(c.Sql);
        sql.Should().Contain("(SELECT n.[fullname] FROM [data].[systemuser] AS n WHERE n.[systemuserid] = t0.[pb_customerid]) AS c2");
        sql.Should().Contain("GROUP BY t0.[pb_customerid] ORDER BY");
    }

    [Fact]
    public void Groupby_with_dategrouping_has_no_name_column()
    {
        var c = Compile(Aggregate(new ColumnNode("pb_startdate", "m", GroupBy: true, DateGrouping: DateGrouping.Month),
            new ColumnNode("pb_projectid", "n", XrmAggregate.Count)));
        c.Columns.Should().NotContain(x => x.Kind == ResultKind.LookupName);
    }

    [Fact]
    public void Sum_and_groupby()
    {
        var q = Aggregate(new ColumnNode("pb_count", "total", XrmAggregate.Sum), new ColumnNode("pb_priority", "prio", GroupBy: true));
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_count", ConditionOperator.GreaterThan, [1]));
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        sql.Should().StartWith("SELECT TOP (@top) SUM(CAST(t0.[pb_count] AS bigint)) AS c0, t0.[pb_priority] AS c1 FROM [data].[pb_project] AS t0 WHERE (t0.[pb_count] > @p0) GROUP BY t0.[pb_priority]");
        sql.Should().NotContain("pb_projectid");
        Param(c, "@top").Should().Be(5001);
        c.UsesKeyset.Should().BeFalse();
        c.Columns.Should().OnlyContain(x => x.Kind == ResultKind.Aggregate && x.Attribute != null);
        c.Columns.Select(x => x.ResultName).Should().Equal("total", "prio");
        c.Columns.Select(x => x.Attribute!.LogicalName).Should().Equal("pb_count", "pb_priority");
    }

    [Fact]
    public void Count_and_countcolumn_distinct()
    {
        var q = Aggregate(new ColumnNode("pb_projectid", "n", XrmAggregate.Count),
            new ColumnNode("pb_name", "names", XrmAggregate.CountColumn, Distinct: true),
            new ColumnNode("pb_name", "nn", XrmAggregate.CountColumn));
        var sql = Normalize(Compile(q).Sql);
        sql.Should().Contain("COUNT(*) AS c0, COUNT(DISTINCT t0.[pb_name]) AS c1, COUNT(t0.[pb_name]) AS c2 FROM");
        sql.Should().NotContain("GROUP BY");
    }

    [Theory]
    [InlineData(XrmAggregate.Avg, "pb_budget", "AVG(t0.[pb_budget])")]
    [InlineData(XrmAggregate.Min, "pb_startdate", "MIN(t0.[pb_startdate])")]
    [InlineData(XrmAggregate.Max, "pb_ratio", "MAX(t0.[pb_ratio])")]
    [InlineData(XrmAggregate.Sum, "pb_budget", "SUM(t0.[pb_budget])")]
    public void Numeric_aggregates(XrmAggregate aggregate, string attribute, string expected) =>
        Normalize(Compile(Aggregate(new ColumnNode(attribute, "a", aggregate))).Sql).Should().Contain(expected + " AS c0");

    [Fact]
    public void Avg_on_integer_avoids_integer_division()
    {
        Normalize(Compile(Aggregate(new ColumnNode("pb_count", "a", XrmAggregate.Avg))).Sql)
            .Should().Contain("AVG(CAST(t0.[pb_count] AS decimal(38, 10))) AS c0");
    }

    [Theory]
    [InlineData(DateGrouping.Day, "CAST(t0.[pb_startdate] AS date)")]
    [InlineData(DateGrouping.Week, "DATEPART(week, t0.[pb_startdate])")]
    [InlineData(DateGrouping.Month, "DATEPART(month, t0.[pb_startdate])")]
    [InlineData(DateGrouping.Quarter, "DATEPART(quarter, t0.[pb_startdate])")]
    [InlineData(DateGrouping.Year, "DATEPART(year, t0.[pb_startdate])")]
    public void Dategrouping_in_select_and_group_by(DateGrouping grouping, string expression)
    {
        var q = Aggregate(new ColumnNode("pb_projectid", "n", XrmAggregate.Count),
            new ColumnNode("pb_startdate", "d", GroupBy: true, DateGrouping: grouping));
        var sql = Normalize(Compile(q).Sql);
        sql.Should().Contain($"{expression} AS c1 FROM");
        sql.Should().Contain($"GROUP BY {expression}");
    }

    [Fact]
    public void Count_sql_probes_the_record_limit()
    {
        var q = Aggregate(new ColumnNode("pb_count", "total", XrmAggregate.Sum), new ColumnNode("pb_priority", "prio", GroupBy: true));
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_count", ConditionOperator.GreaterThan, [1]));
        var c = Compile(q);
        Normalize(c.CountSql!).Should().Be(
            "SELECT COUNT(*) FROM (SELECT TOP 50001 1 AS x FROM [data].[pb_project] AS t0 WHERE (t0.[pb_count] > @p0)) AS q");
    }

    [Fact]
    public void Order_by_aggregate_alias_without_pk_tiebreak()
    {
        var q = Aggregate(new ColumnNode("pb_count", "total", XrmAggregate.Sum), new ColumnNode("pb_priority", "prio", GroupBy: true));
        q.Root.Orders.Add(new OrderNode("", true, "total"));
        var sql = Normalize(Compile(q).Sql);
        sql.Should().EndWith("ORDER BY c0 DESC, c1 ASC");
        sql.Should().NotContain("pb_projectid");
    }

    [Fact]
    public void Order_by_unknown_alias_is_rejected()
    {
        var q = Aggregate(new ColumnNode("pb_count", "total", XrmAggregate.Sum));
        q.Root.Orders.Add(new OrderNode("", true, "nope"));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*nope*");
    }

    [Fact]
    public void Order_by_attribute_must_be_grouped()
    {
        var q = Aggregate(new ColumnNode("pb_count", "total", XrmAggregate.Sum), new ColumnNode("pb_priority", "prio", GroupBy: true));
        q.Root.Orders.Add(new OrderNode("pb_priority", true));
        Normalize(Compile(q).Sql).Should().EndWith("ORDER BY t0.[pb_priority] DESC");

        var bad = Aggregate(new ColumnNode("pb_count", "total", XrmAggregate.Sum));
        bad.Root.Orders.Add(new OrderNode("pb_name", false));
        FluentActions.Invoking(() => Compile(bad)).Should().Throw<XrmdValidationException>().WithMessage("*pb_name*");
    }

    [Fact]
    public void Groupby_on_link_entity()
    {
        var q = Aggregate(new ColumnNode("pb_projectid", "n", XrmAggregate.Count));
        var link = new LinkNode { EntityName = "systemuser", From = "systemuserid", To = "pb_customerid", Alias = "u" };
        link.Columns.Add(new ColumnNode("fullname", "owner", GroupBy: true));
        q.Root.Links.Add(link);
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain("COUNT(*) AS c0, t1.[fullname] AS c1 FROM").And.Contain("GROUP BY t1.[fullname]");
        c.Columns[1].EntityAlias.Should().Be("u");
    }

    [Fact]
    public void Top_and_offset_paging_apply_to_groups()
    {
        var q = Aggregate(new ColumnNode("pb_projectid", "n", XrmAggregate.Count), new ColumnNode("pb_priority", "prio", GroupBy: true));
        q.Top = 4;
        Param(Compile(q), "@top").Should().Be(4);

        q.Top = null;
        q.PageSize = 10;
        q.PageNumber = 2;
        var c = Compile(q);
        Normalize(c.Sql).Should().EndWith("GROUP BY t0.[pb_priority] ORDER BY c1 ASC OFFSET (@offset) ROWS FETCH NEXT (@fetch) ROWS ONLY");
        Param(c, "@offset").Should().Be(10);
        Param(c, "@fetch").Should().Be(11);
        c.UsesKeyset.Should().BeFalse();
    }

    [Fact]
    public void Same_groupby_expression_twice_does_not_throw()
    {
        var q = Aggregate(new ColumnNode("pb_priority", "a", GroupBy: true), new ColumnNode("pb_priority", "b", GroupBy: true));
        var sql = Normalize(Compile(q).Sql);
        sql.Should().EndWith("GROUP BY t0.[pb_priority], t0.[pb_priority] ORDER BY c0 ASC");
    }

    [Fact]
    public void Order_by_alias_of_duplicate_expression()
    {
        var q = Aggregate(new ColumnNode("pb_projectid", "n1", XrmAggregate.Count), new ColumnNode("pb_projectid", "n2", XrmAggregate.Count));
        q.Root.Orders.Add(new OrderNode("", true, "n2"));
        Normalize(Compile(q).Sql).Should().EndWith("ORDER BY c1 DESC");
    }

    [Fact]
    public void Rejects_sum_on_string() =>
        FluentActions.Invoking(() => Compile(Aggregate(new ColumnNode("pb_name", "s", XrmAggregate.Sum))))
            .Should().Throw<XrmdValidationException>().WithMessage("*pb_name*");

    [Fact]
    public void Rejects_avg_on_datetime() =>
        FluentActions.Invoking(() => Compile(Aggregate(new ColumnNode("pb_startdate", "s", XrmAggregate.Avg))))
            .Should().Throw<XrmdValidationException>();

    [Fact]
    public void Rejects_column_without_alias() =>
        FluentActions.Invoking(() => Compile(Aggregate(new ColumnNode("pb_count", null, XrmAggregate.Sum))))
            .Should().Throw<XrmdValidationException>().WithMessage("*alias*");

    [Fact]
    public void Rejects_column_without_aggregate_or_groupby() =>
        FluentActions.Invoking(() => Compile(Aggregate(new ColumnNode("pb_count", "c"))))
            .Should().Throw<XrmdValidationException>();

    [Fact]
    public void Rejects_dategrouping_on_non_datetime() =>
        FluentActions.Invoking(() => Compile(Aggregate(new ColumnNode("pb_count", "c", GroupBy: true, DateGrouping: DateGrouping.Year))))
            .Should().Throw<XrmdValidationException>();

    [Fact]
    public void Rejects_all_columns()
    {
        var q = Aggregate(new ColumnNode("pb_count", "c", XrmAggregate.Sum));
        q.Root.AllColumns = true;
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void Rejects_total_record_count()
    {
        var q = Aggregate(new ColumnNode("pb_count", "c", XrmAggregate.Sum));
        q.ReturnTotalRecordCount = true;
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void Rejects_duplicate_alias() =>
        FluentActions.Invoking(() => Compile(Aggregate(new ColumnNode("pb_count", "c", XrmAggregate.Sum), new ColumnNode("pb_ratio", "c", XrmAggregate.Sum))))
            .Should().Throw<XrmdValidationException>();
}
