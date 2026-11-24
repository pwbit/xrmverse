using System.Text.RegularExpressions;
using FluentAssertions;
using Xrmd.Abstractions;
using Xrmd.Model.Query;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

public class PagingCompilerTests
{
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc));

    private static CompiledQuery Compile(QueryModel q) => new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(q, Context);

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static object? Param(CompiledQuery c, string name) => c.Parameters.Single(p => p.ParameterName == name).Value;

    private static QueryModel Project(params string[] columns)
    {
        var root = new EntityNode { EntityName = "pb_project" };
        root.Columns.AddRange(columns.Select(c => new ColumnNode(c)));
        return new QueryModel { Root = root };
    }

    private static string Cookie(int page, Guid last, string pk = "pb_projectid") =>
        new PagingCookie(page, pk, Guid.NewGuid(), last).Format();

    [Fact]
    public void First_page_reads_pagesize_plus_one()
    {
        var q = Project("pb_name");
        q.PageSize = 50;
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        sql.Should().StartWith("SELECT TOP (@top) ");
        Param(c, "@top").Should().Be(51);
        sql.Should().NotContain("OFFSET");
        c.PageSize.Should().Be(50);
    }

    [Fact]
    public void Default_page_size_is_5000()
    {
        var c = Compile(Project());
        Param(c, "@top").Should().Be(5001);
        c.PageSize.Should().Be(5000);
    }

    [Fact]
    public void Keyset_with_valid_cookie()
    {
        var g = Guid.NewGuid();
        var q = Project("pb_name");
        q.PageSize = 10;
        q.PageNumber = 2;
        q.PagingCookie = Cookie(1, g);
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_count", FilterOperator.GreaterThan, [1]));
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        c.UsesKeyset.Should().BeTrue();
        var m = Regex.Match(sql, @"WHERE \(t0\.\[pb_count\] > @p0\) AND t0\.\[pb_projectid\] > (@\w+)");
        m.Success.Should().BeTrue(sql);
        Param(c, m.Groups[1].Value).Should().Be(g);
        Param(c, "@top").Should().Be(11);
        sql.Should().NotContain("OFFSET");
        sql.Should().EndWith("ORDER BY t0.[pb_projectid] ASC");
    }

    [Fact]
    public void Keyset_when_ordered_by_pk_ascending()
    {
        var q = Project();
        q.Root.Orders.Add(new OrderNode("pb_projectid", false));
        q.PageNumber = 3;
        q.PagingCookie = Cookie(2, Guid.NewGuid());
        Compile(q).UsesKeyset.Should().BeTrue();
    }

    [Fact]
    public void Keyset_with_n_to_1_link()
    {
        var q = Project();
        q.Root.Links.Add(new LinkNode { EntityName = "systemuser", From = "systemuserid", To = "pb_customerid", Alias = "u" });
        q.PageNumber = 2;
        q.PagingCookie = Cookie(1, Guid.NewGuid());
        Compile(q).UsesKeyset.Should().BeTrue();
    }

    [Fact]
    public void Offset_with_1_to_n_link_even_with_valid_cookie()
    {
        var root = new EntityNode { EntityName = "systemuser" };
        root.Links.Add(new LinkNode { EntityName = "pb_project", From = "pb_customerid", To = "systemuserid", Alias = "p" });
        var q = new QueryModel { Root = root, PageNumber = 2, PagingCookie = Cookie(1, Guid.NewGuid(), "systemuserid") };
        var c = Compile(q);
        c.UsesKeyset.Should().BeFalse();
        Normalize(c.Sql).Should().Contain("OFFSET (@offset) ROWS");
    }

    [Fact]
    public void Offset_when_ordered_by_other_column()
    {
        var q = Project("pb_name");
        q.Root.Orders.Add(new OrderNode("pb_name", false));
        q.PageNumber = 3;
        q.PageSize = 10;
        q.PagingCookie = Cookie(2, Guid.NewGuid());
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        c.UsesKeyset.Should().BeFalse();
        sql.Should().EndWith("ORDER BY t0.[pb_name] ASC, t0.[pb_projectid] ASC OFFSET (@offset) ROWS FETCH NEXT (@fetch) ROWS ONLY");
        sql.Should().NotContain("TOP");
        Param(c, "@offset").Should().Be(20);
        Param(c, "@fetch").Should().Be(11);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<cookie page=\"x\"/>")]
    [InlineData("<cookie page=\"1\"><accountid last=\"zz\" first=\"zz\"/></cookie>")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<cookie page=\"5\"><pb_projectid last=\"{8A5F6F2E-6F3B-4C55-9B7E-2D3B1F0B6A11}\" first=\"{8A5F6F2E-6F3B-4C55-9B7E-2D3B1F0B6A11}\" /></cookie>")]
    [InlineData("<cookie page=\"1\"><accountid last=\"{8A5F6F2E-6F3B-4C55-9B7E-2D3B1F0B6A11}\" first=\"{8A5F6F2E-6F3B-4C55-9B7E-2D3B1F0B6A11}\" /></cookie>")]
    public void Malformed_cookie_falls_back_to_offset(string? cookie)
    {
        var q = Project();
        q.PageNumber = 2;
        q.PageSize = 7;
        q.PagingCookie = cookie;
        var c = Compile(q);
        c.UsesKeyset.Should().BeFalse();
        Normalize(c.Sql).Should().Contain("OFFSET (@offset) ROWS FETCH NEXT (@fetch) ROWS ONLY");
        Param(c, "@offset").Should().Be(7);
        Param(c, "@fetch").Should().Be(8);
    }

    [Fact]
    public void Distinct_never_uses_keyset_and_orders_by_select_list()
    {
        var q = Project("pb_name", "pb_count");
        q.Distinct = true;
        q.PageNumber = 2;
        q.PagingCookie = Cookie(1, Guid.NewGuid());
        var c = Compile(q);
        c.UsesKeyset.Should().BeFalse();
        Normalize(c.Sql).Should().Contain("ORDER BY c0 ASC, c1 ASC OFFSET (@offset) ROWS");
    }

    [Fact]
    public void Distinct_rejects_order_by_column_not_selected()
    {
        var q = Project("pb_name");
        q.Distinct = true;
        q.Root.Orders.Add(new OrderNode("pb_count", false));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*pb_count*");
    }

    [Fact]
    public void Distinct_allows_order_by_selected_column()
    {
        var q = Project("pb_name");
        q.Distinct = true;
        q.Root.Orders.Add(new OrderNode("pb_name", true));
        Normalize(Compile(q).Sql).Should().Contain("ORDER BY t0.[pb_name] DESC");
    }

    [Fact]
    public void Top_wins_over_paging()
    {
        var q = Project();
        q.Top = 3;
        q.PageSize = 10;
        q.PageNumber = 4;
        var c = Compile(q);
        Param(c, "@top").Should().Be(3);
        c.Sql.Should().NotContain("OFFSET");
        c.UsesKeyset.Should().BeFalse();
    }

    [Fact]
    public void Total_count_adds_window_column()
    {
        var q = Project("pb_name");
        q.ReturnTotalRecordCount = true;
        var c = Compile(q);
        var total = c.Columns.Single(x => x.Kind == ResultKind.TotalCount);
        total.Attribute.Should().BeNull();
        Normalize(c.Sql).Should().Contain($"COUNT(*) OVER() AS {total.SqlAlias}");
        c.CountSql.Should().BeNull();
    }

    [Fact]
    public void Total_count_with_keyset_uses_count_sql_without_keyset()
    {
        var q = Project("pb_name");
        q.ReturnTotalRecordCount = true;
        q.PageNumber = 2;
        q.PagingCookie = Cookie(1, Guid.NewGuid());
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_count", FilterOperator.GreaterThan, [1]));
        var c = Compile(q);
        c.UsesKeyset.Should().BeTrue();
        c.Columns.Should().NotContain(x => x.Kind == ResultKind.TotalCount);
        c.Sql.Should().NotContain("OVER()");
        Normalize(c.CountSql!).Should().Be("SELECT COUNT(*) FROM [data].[pb_project] AS t0 WHERE (t0.[pb_count] > @p0)");
    }

    [Fact]
    public void Total_count_with_distinct_counts_distinct_rows()
    {
        var q = Project("pb_name");
        q.Distinct = true;
        q.ReturnTotalRecordCount = true;
        var c = Compile(q);
        c.Columns.Should().NotContain(x => x.Kind == ResultKind.TotalCount);
        // The primary key is not selected unless requested: it would make every row distinct.
        Normalize(c.CountSql!).Should().Be("SELECT COUNT(*) FROM (SELECT DISTINCT t0.[pb_name] AS c0 FROM [data].[pb_project] AS t0) AS q");
    }

    [Fact]
    public void Rejects_pagesize_over_5000()
    {
        var q = Project();
        q.PageSize = 5001;
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void Rejects_offset_overflow()
    {
        var q = Project();
        q.PageNumber = int.MaxValue;
        q.PageSize = 5000;
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*too large*");
    }

    [Fact]
    public void Rejects_page_zero()
    {
        var q = Project();
        q.PageNumber = 0;
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>();
    }
}
