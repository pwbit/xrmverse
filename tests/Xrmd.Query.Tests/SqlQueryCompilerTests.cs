using System.Data.Common;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xrmd.Abstractions;
using Xrmd.Model.Query;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

public class SqlQueryCompilerTests
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

    private static ConditionNode Cond(string attribute, FilterOperator op, params object?[] values) => new(null, attribute, op, values);

    private static LinkNode UserLink(string? alias, JoinKind join = JoinKind.LeftOuter, params string[] columns)
    {
        var link = new LinkNode { EntityName = "systemuser", From = "systemuserid", To = "pb_customerid", JoinType = join, Alias = alias };
        link.Columns.AddRange(columns.Select(c => new ColumnNode(c)));
        return link;
    }

    private static object? Param(CompiledQuery c, string name) => c.Parameters.Single(p => p.ParameterName == name).Value;

    /// <summary>The parameters of the conditions (without the paging ones).</summary>
    private static List<DbParameter> FilterParameters(CompiledQuery c) =>
        c.Parameters.Where(p => p.ParameterName.StartsWith("@p", StringComparison.Ordinal)).ToList();

    [Fact]
    public void Compile_select_columns_and_pk()
    {
        var q = Project("pb_name");
        q.Top = 5;
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        sql.Should().Contain("SELECT TOP (@top) t0.[pb_projectid] AS c0, t0.[pb_name] AS c1 FROM [data].[pb_project] AS t0");
        sql.Should().Contain("ORDER BY t0.[pb_projectid] ASC");
        Param(c, "@top").Should().Be(5);
        c.Columns.Select(x => x.ResultName).Should().Equal("pb_projectid", "pb_name");
        c.Columns.Select(x => x.SqlAlias).Should().Equal("c0", "c1");
        c.Columns.Should().OnlyContain(x => x.Kind == ResultKind.Value && x.EntityAlias == null && x.EntityName == "pb_project");
        c.Root.LogicalName.Should().Be("pb_project");
        c.PageSize.Should().Be(5000);
        c.PageNumber.Should().Be(1);
        c.UsesKeyset.Should().BeFalse();
        c.CountSql.Should().BeNull();
    }

    [Fact]
    public void Compile_without_top_reads_a_default_page_and_selects_pk_only_by_default()
    {
        var c = Compile(Project());
        var sql = Normalize(c.Sql);
        sql.Should().StartWith("SELECT TOP (@top) t0.[pb_projectid] AS c0 FROM [data].[pb_project] AS t0");
        Param(c, "@top").Should().Be(SqlQueryCompiler.MaxPageSize + 1);
        c.Columns.Should().ContainSingle();
    }

    [Fact]
    public void Compile_records_page_size_and_number()
    {
        var q = Project();
        q.PageSize = 50;
        q.PageNumber = 3;
        var c = Compile(q);
        c.PageSize.Should().Be(50);
        c.PageNumber.Should().Be(3);
    }

    [Fact]
    public void Compile_all_columns_selects_readable_attributes_with_lookup_type_and_versionnumber_cast()
    {
        var q = Project();
        q.Root.AllColumns = true;
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        sql.Should().Contain("CAST(t0.[versionnumber] AS bigint) AS c");
        sql.Should().Contain("t0.[pb_budget_base] AS c");
        c.Columns[0].ResultName.Should().Be("pb_projectid");
        var owner = c.Columns.Single(x => x.ResultName == "ownerid" && x.Kind == ResultKind.Value);
        var ownerType = c.Columns.Single(x => x.ResultName == "ownerid" && x.Kind == ResultKind.LookupType);
        sql.Should().Contain($"t0.[ownerid] AS {owner.SqlAlias}");
        sql.Should().Contain($"t0.[owneridtype] AS {ownerType.SqlAlias}");
        c.Columns.Should().NotContain(x => x.ResultName == "pb_customerid" && x.Kind == ResultKind.LookupType);
    }

    [Fact]
    public void Compile_selected_lookup_adds_primary_name_subquery()
    {
        var q = Project("pb_customerid");
        q.Root.Filter.Conditions.Add(Cond("pb_name", FilterOperator.Equal, "x"));
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        var name = c.Columns.Single(x => x.Kind == ResultKind.LookupName);
        name.ResultName.Should().Be("pb_customerid");
        name.EntityAlias.Should().BeNull();
        name.Attribute!.LogicalName.Should().Be("pb_customerid");
        sql.Should().Contain(
            $"(SELECT n.[fullname] FROM [data].[systemuser] AS n WHERE n.[systemuserid] = t0.[pb_customerid]) AS {name.SqlAlias}");
        sql.Should().Contain("WHERE (t0.[pb_name] = @p0)"); // filter parameters keep their numbering
    }

    [Fact]
    public void Compile_owner_lookup_name_is_a_case_over_the_type_column()
    {
        var q = Project("ownerid");
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        var name = c.Columns.Single(x => x.Kind == ResultKind.LookupName);
        name.ResultName.Should().Be("ownerid");
        // team is not in the test metadata: only the systemuser branch.
        var otc = c.Parameters.Single(p => Equals(p.Value, 8));
        sql.Should().Contain(
            $"CASE t0.[owneridtype] WHEN {otc.ParameterName} THEN (SELECT n.[fullname] FROM [data].[systemuser] AS n WHERE n.[systemuserid] = t0.[ownerid]) END AS {name.SqlAlias}");
    }

    [Fact]
    public void Compile_link_lookup_name_uses_the_link_result_name()
    {
        var q = Project("pb_name");
        q.Root.Links.Add(UserLink("u", JoinKind.LeftOuter, "businessunitid", "fullname"));
        var c = Compile(q);
        // businessunit is not in the test metadata: no name column for it; systemuser has no other lookup selected.
        c.Columns.Should().NotContain(x => x.Kind == ResultKind.LookupName);

        var q2 = Project("pb_name");
        var link = new LinkNode { EntityName = "pb_project", From = "pb_customerid", To = "pb_customerid", Alias = "p" };
        link.Columns.Add(new ColumnNode("pb_customerid"));
        q2.Root.Links.Add(link);
        var c2 = Compile(q2);
        var name = c2.Columns.Single(x => x.Kind == ResultKind.LookupName);
        name.ResultName.Should().Be("p.pb_customerid");
        name.EntityAlias.Should().Be("p");
        Normalize(c2.Sql).Should().Contain($"(SELECT n.[fullname] FROM [data].[systemuser] AS n WHERE n.[systemuserid] = t1.[pb_customerid]) AS {name.SqlAlias}");
    }

    [Fact]
    public void Compile_condition_eq_is_parameterized()
    {
        var q = Project("pb_name");
        q.Root.Filter.Conditions.Add(Cond("pb_name", FilterOperator.Equal, "x'y"));
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain("WHERE (t0.[pb_name] = @p0)");
        c.Sql.Should().NotContain("x'y");
        Param(c, "@p0").Should().Be("x'y");
    }

    [Theory]
    [InlineData(FilterOperator.NotEqual, "<>")]
    [InlineData(FilterOperator.GreaterThan, ">")]
    [InlineData(FilterOperator.GreaterEqual, ">=")]
    [InlineData(FilterOperator.LessThan, "<")]
    [InlineData(FilterOperator.LessEqual, "<=")]
    public void Compile_comparison_operators(FilterOperator op, string sqlOp)
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond("pb_count", op, "3"));
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain($"WHERE (t0.[pb_count] {sqlOp} @p0)");
        Param(c, "@p0").Should().Be(3);
    }

    [Fact]
    public void Compile_nested_filters()
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond("pb_count", FilterOperator.GreaterThan, 1));
        var or = new FilterNode { Operator = FilterLogic.Or };
        or.Conditions.Add(Cond("pb_name", FilterOperator.Equal, "a"));
        or.Conditions.Add(Cond("pb_name", FilterOperator.Equal, "b"));
        q.Root.Filter.Filters.Add(or);
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain("WHERE (t0.[pb_count] > @p0 AND (t0.[pb_name] = @p1 OR t0.[pb_name] = @p2))");
        Param(c, "@p0").Should().Be(1);
        Param(c, "@p1").Should().Be("a");
        Param(c, "@p2").Should().Be("b");
    }

    [Fact]
    public void Compile_left_outer_link_with_alias()
    {
        var q = Project("pb_name");
        q.Root.Links.Add(UserLink("u", JoinKind.LeftOuter, "fullname"));
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        sql.Should().Contain("LEFT OUTER JOIN [data].[systemuser] AS t1 ON t1.[systemuserid] = t0.[pb_customerid]");
        sql.Should().Contain("t1.[fullname] AS c2");
        var col = c.Columns.Single(x => x.ResultName == "u.fullname");
        col.EntityAlias.Should().Be("u");
        col.EntityName.Should().Be("systemuser");
        col.SqlAlias.Should().Be("c2");
        sql.Should().NotContain(" u.").And.NotContain("[u]");
    }

    [Fact]
    public void Compile_inner_link_with_filter_and_order()
    {
        var q = Project();
        var link = UserLink("u", JoinKind.Inner);
        link.Filter.Conditions.Add(Cond("fullname", FilterOperator.Equal, "Ann"));
        link.Orders.Add(new OrderNode("fullname", true));
        q.Root.Links.Add(link);
        q.Root.Orders.Add(new OrderNode("pb_name", false));
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        sql.Should().Contain("INNER JOIN [data].[systemuser] AS t1 ON t1.[systemuserid] = t0.[pb_customerid] AND (t1.[fullname] = @p0)");
        sql.Should().EndWith("ORDER BY t0.[pb_name] ASC, t1.[fullname] DESC, t0.[pb_projectid] ASC");
    }

    [Fact]
    public void Compile_link_without_alias_gets_generated_alias()
    {
        var q = Project();
        q.Root.Links.Add(UserLink(null, JoinKind.LeftOuter, "fullname"));
        var c = Compile(q);
        c.Columns.Should().Contain(x => x.ResultName == "systemuser1.fullname" && x.EntityAlias == "systemuser1");
    }

    [Fact]
    public void Compile_condition_on_link_alias()
    {
        var q = Project();
        q.Root.Links.Add(UserLink("u"));
        q.Root.Filter.Conditions.Add(new ConditionNode("u", "fullname", FilterOperator.Equal, ["Ann"]));
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain("WHERE (t1.[fullname] = @p0)");
    }

    [Fact]
    public void Compile_null_and_not_null()
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond("pb_count", FilterOperator.Null));
        q.Root.Filter.Conditions.Add(Cond("pb_count", FilterOperator.NotNull));
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain("WHERE (t0.[pb_count] IS NULL AND t0.[pb_count] IS NOT NULL)");
        FilterParameters(c).Should().BeEmpty();
    }

    [Fact]
    public void Compile_begins_with_escapes_wildcards()
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond("pb_name", FilterOperator.BeginsWith, "50%_["));
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain("WHERE (t0.[pb_name] LIKE @p0)");
        Param(c, "@p0").Should().Be("50[%][_][[]%");
    }

    [Theory]
    [InlineData(FilterOperator.DoesNotBeginWith, "NOT LIKE", "a[%]%")]
    [InlineData(FilterOperator.EndsWith, "LIKE", "%a[%]")]
    [InlineData(FilterOperator.DoesNotEndWith, "NOT LIKE", "%a[%]")]
    [InlineData(FilterOperator.Like, "LIKE", "a%")]
    [InlineData(FilterOperator.NotLike, "NOT LIKE", "a%")]
    public void Compile_like_family(FilterOperator op, string sqlOp, string expected)
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond("pb_name", op, "a%"));
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain($"WHERE (t0.[pb_name] {sqlOp} @p0)");
        Param(c, "@p0").Should().Be(expected);
    }

    [Fact]
    public void Compile_in_and_not_in()
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond("pb_count", FilterOperator.In, 1, "2", 3));
        q.Root.Filter.Conditions.Add(Cond("pb_priority", FilterOperator.NotIn, 1));
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain("WHERE (t0.[pb_count] IN (@p0, @p1, @p2) AND t0.[pb_priority] NOT IN (@p3))");
        FilterParameters(c).Select(p => p.Value).Should().Equal(1, 2, 3, 1);
    }

    [Fact]
    public void Compile_in_with_3000_values_uses_openjson()
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond("pb_count", FilterOperator.In, Enumerable.Range(0, 3000).Cast<object?>().ToArray()));
        var c = Compile(q);
        FilterParameters(c).Should().ContainSingle();
        Normalize(c.Sql).Should().Contain("WHERE (t0.[pb_count] IN (SELECT value FROM OPENJSON(@p0) WITH (value int '$')))");
        ((string)Param(c, "@p0")!).Should().StartWith("[0,1,2,");
    }

    [Fact]
    public void Compile_openjson_for_guids()
    {
        var q = Project();
        var ids = Enumerable.Range(0, 101).Select(_ => (object?)Guid.NewGuid()).ToArray();
        q.Root.Filter.Conditions.Add(Cond("pb_customerid", FilterOperator.NotIn, ids));
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain("t0.[pb_customerid] NOT IN (SELECT value FROM OPENJSON(@p0) WITH (value uniqueidentifier '$'))");
        ((string)Param(c, "@p0")!).Should().Contain(ids[0]!.ToString());
    }

    [Fact]
    public void Compile_between()
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond("pb_count", FilterOperator.Between, 1, 5));
        q.Root.Filter.Conditions.Add(Cond("pb_count", FilterOperator.NotBetween, 7, 9));
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain("WHERE (t0.[pb_count] BETWEEN @p0 AND @p1 AND t0.[pb_count] NOT BETWEEN @p2 AND @p3)");
        FilterParameters(c).Select(p => p.Value).Should().Equal(1, 5, 7, 9);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Compile_between_requires_two_values(int count)
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond("pb_count", FilterOperator.Between, Enumerable.Range(0, count).Cast<object?>().ToArray()));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*pb_count*");
    }

    [Theory]
    [InlineData("pb_nope")]
    [InlineData("name]; DROP TABLE x--")]
    public void Compile_rejects_unknown_attribute(string name)
    {
        void Check(QueryModel q) =>
            FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().Which.Message.Should().Contain(name);

        Check(Project(name));
        var cond = Project();
        cond.Root.Filter.Conditions.Add(Cond(name, FilterOperator.Equal, "x"));
        Check(cond);
        var order = Project();
        order.Root.Orders.Add(new OrderNode(name, false));
        Check(order);
        var link = Project();
        link.Root.Links.Add(new LinkNode { EntityName = "systemuser", From = name, To = "pb_customerid" });
        Check(link);
        var linkCol = Project();
        linkCol.Root.Links.Add(UserLink("u", JoinKind.Inner, name));
        Check(linkCol);
    }

    [Theory]
    [InlineData("pb_nope")]
    [InlineData("name]; DROP TABLE x--")]
    public void Compile_rejects_unknown_entity_and_alias(string name)
    {
        FluentActions.Invoking(() => Compile(new QueryModel { Root = new EntityNode { EntityName = name } }))
            .Should().Throw<XrmdValidationException>().Which.Message.Should().Contain(name);

        var link = Project();
        link.Root.Links.Add(new LinkNode { EntityName = name, From = "x", To = "pb_customerid" });
        FluentActions.Invoking(() => Compile(link)).Should().Throw<XrmdValidationException>().Which.Message.Should().Contain(name);

        var alias = Project();
        alias.Root.Filter.Conditions.Add(new ConditionNode(name, "fullname", FilterOperator.Null, []));
        FluentActions.Invoking(() => Compile(alias)).Should().Throw<XrmdValidationException>().Which.Message.Should().Contain(name);
    }

    [Fact]
    public void Compile_rejects_duplicate_link_alias()
    {
        var q = Project();
        q.Root.Links.Add(UserLink("u"));
        q.Root.Links.Add(UserLink("u"));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*u*");
    }

    [Theory]
    [InlineData("pb_count", "abc")]
    [InlineData("pb_customerid", "not-a-guid")]
    public void Compile_rejects_wrong_value_type(string attribute, string value)
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond(attribute, FilterOperator.Equal, value));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().Which.Message.Should().Contain(attribute);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public void Compile_rejects_top_over_5000(int top)
    {
        var q = Project();
        q.Top = top;
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void Compile_rejects_operators_of_later_tasks()
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond("pb_startdate", FilterOperator.Today));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*Today*not supported*");
    }

    [Fact]
    public void Compile_distinct_has_no_pk_tiebreak()
    {
        var q = Project("pb_name");
        q.Distinct = true;
        q.Top = 3;
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        sql.Should().StartWith("SELECT DISTINCT TOP (@top) ");
        sql.Should().NotContain("ORDER BY");
    }

    [Fact]
    public void Compile_order_by_pk_is_not_repeated()
    {
        var q = Project();
        q.Root.Orders.Add(new OrderNode("pb_projectid", true));
        Normalize(Compile(q).Sql).Should().EndWith("ORDER BY t0.[pb_projectid] DESC");
    }
}
