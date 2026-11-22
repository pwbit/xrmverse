using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

/// <summary>
/// Plan 3.5 UI-1a final review (I1, #107; I2): a like-family condition on a lookup matches the related record's primary name, and
/// an order on a lookup sorts by that name, an order on a choice by its label (unless <c>useraworderby</c>), as Dataverse does.
/// </summary>
public class LookupNameCompilerTests
{
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc));

    private const string UserName = "(SELECT n.[fullname] FROM [data].[systemuser] AS n WHERE n.[systemuserid] = t0.[pb_customerid])";

    private static CompiledQuery Compile(QueryModel q) => new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(q, Context);

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static QueryModel Project(params string[] columns)
    {
        var root = new EntityNode { EntityName = "pb_project" };
        root.Columns.AddRange(columns.Select(c => new ColumnNode(c)));
        return new QueryModel { Root = root };
    }

    private static ConditionNode Cond(string attribute, ConditionOperator op, params object?[] values) => new(null, attribute, op, values);

    private static object? Param(CompiledQuery c, string name) => c.Parameters.Single(p => p.ParameterName == name).Value;

    private static QueryModel Filtered(string attribute, ConditionOperator op, string value)
    {
        var q = Project("pb_name");
        q.Root.Filter.Conditions.Add(Cond(attribute, op, value));
        return q;
    }

    [Fact]
    public void Like_on_lookup_matches_the_target_primary_name()
    {
        var c = Compile(Filtered("pb_customerid", ConditionOperator.Like, "Ann%"));
        Normalize(c.Sql).Should().Contain($"WHERE ({UserName} LIKE @p0)");
        Param(c, "@p0").Should().Be("Ann%");
    }

    [Fact]
    public void Like_on_customer_lookup_switches_on_the_type_column()
    {
        var c = Compile(Filtered("pb_partnerid", ConditionOperator.Like, "Acme%"));
        Normalize(c.Sql).Should().Contain(
            "WHERE (CASE t0.[pb_partneridtype] " +
            "WHEN @p0 THEN (SELECT n.[name] FROM [data].[account] AS n WHERE n.[accountid] = t0.[pb_partnerid]) " +
            "WHEN @p1 THEN (SELECT n.[fullname] FROM [data].[systemuser] AS n WHERE n.[systemuserid] = t0.[pb_partnerid]) END LIKE @p2)");
        Param(c, "@p0").Should().Be(1);
        Param(c, "@p1").Should().Be(8);
        Param(c, "@p2").Should().Be("Acme%");
    }

    [Theory]
    [InlineData(ConditionOperator.BeginsWith, "LIKE", "50[%]%")]
    [InlineData(ConditionOperator.DoesNotBeginWith, "NOT LIKE", "50[%]%")]
    [InlineData(ConditionOperator.EndsWith, "LIKE", "%50[%]")]
    [InlineData(ConditionOperator.DoesNotEndWith, "NOT LIKE", "%50[%]")]
    [InlineData(ConditionOperator.NotLike, "NOT LIKE", "50%")]
    public void Like_family_on_lookup_uses_the_name_and_escapes_as_on_text(ConditionOperator op, string sqlOp, string pattern)
    {
        var c = Compile(Filtered("pb_customerid", op, "50%"));
        // NOT LIKE over the scalar name keeps NULL semantics: an empty lookup matches neither LIKE nor NOT LIKE (no NOT EXISTS).
        Normalize(c.Sql).Should().Contain($"WHERE ({UserName} {sqlOp} @p0)").And.NotContain("EXISTS");
        Param(c, "@p0").Should().Be(pattern);
    }

    [Fact]
    public void Like_on_lookup_in_link_entity_uses_its_alias()
    {
        var q = Project("pb_name");
        var link = new LinkNode { EntityName = "pb_project", From = "pb_customerid", To = "pb_customerid", Alias = "p" };
        link.Filter.Conditions.Add(Cond("pb_customerid", ConditionOperator.Like, "Ann%"));
        q.Root.Links.Add(link);
        Normalize(Compile(q).Sql).Should().Contain(
            "(SELECT n.[fullname] FROM [data].[systemuser] AS n WHERE n.[systemuserid] = t1.[pb_customerid]) LIKE @p0");
    }

    [Fact]
    public void Like_on_lookup_without_named_target_is_refused()
    {
        var q = new QueryModel { Root = new EntityNode { EntityName = "annotation" } };
        q.Root.Filter.Conditions.Add(Cond("objectid", ConditionOperator.Like, "x%"));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*objectid*primary name*");
    }

    [Fact]
    public void Like_on_partylist_is_refused()
    {
        // A party list has no column (plan 2.11 A8): it cannot be named in a condition at all.
        FluentActions.Invoking(() => Compile(Filtered("pb_parties", ConditionOperator.Like, "x%")))
            .Should().Throw<XrmdValidationException>().WithMessage("*pb_parties*");
    }

    [Fact]
    public void Like_on_lookup_requires_one_text_value()
    {
        var q = Project();
        q.Root.Filter.Conditions.Add(Cond("pb_customerid", ConditionOperator.Like, "a", "b"));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*pb_customerid*one*");
    }

    [Fact]
    public void Queries_without_lookup_like_keep_parameter_numbering()
    {
        var q = Project("ownerid");
        q.Root.Filter.Conditions.Add(Cond("pb_customerid", ConditionOperator.Equal, Guid.Empty));
        var c = Compile(q);
        // The condition is @p0 and only the selected owner name adds its type code parameter after it.
        Normalize(c.Sql).Should().Contain("WHERE (t0.[pb_customerid] = @p0)");
        Param(c, "@p1").Should().Be(8);
        c.Parameters.Count(p => p.ParameterName.StartsWith("@p", StringComparison.Ordinal)).Should().Be(2);
    }

    // ---- I2: order ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, "ASC")]
    [InlineData(true, "DESC")]
    public void Order_by_lookup_uses_the_name(bool descending, string direction)
    {
        var q = Project("pb_name");
        q.Root.Orders.Add(new OrderNode("pb_customerid", descending));
        Normalize(Compile(q).Sql).Should().EndWith($"ORDER BY {UserName} {direction}, t0.[pb_projectid] ASC");
    }

    [Fact]
    public void Order_by_customer_lookup_uses_case()
    {
        var q = Project("pb_name");
        q.Root.Orders.Add(new OrderNode("pb_partnerid", false));
        Normalize(Compile(q).Sql).Should().Contain(
            "ORDER BY CASE t0.[pb_partneridtype] " +
            "WHEN @p0 THEN (SELECT n.[name] FROM [data].[account] AS n WHERE n.[accountid] = t0.[pb_partnerid]) " +
            "WHEN @p1 THEN (SELECT n.[fullname] FROM [data].[systemuser] AS n WHERE n.[systemuserid] = t0.[pb_partnerid]) END ASC");
    }

    [Fact]
    public void Order_by_owner_uses_case()
    {
        var q = Project("pb_name");
        q.Root.Orders.Add(new OrderNode("ownerid", true));
        Normalize(Compile(q).Sql).Should().Contain(
            "ORDER BY CASE t0.[owneridtype] WHEN @p0 THEN (SELECT n.[fullname] FROM [data].[systemuser] AS n WHERE n.[systemuserid] = t0.[ownerid]) END DESC");
    }

    [Fact]
    public void Order_by_lookup_in_a_link_uses_the_link_alias()
    {
        var q = Project("pb_name");
        var link = new LinkNode { EntityName = "pb_project", From = "pb_customerid", To = "pb_customerid", Alias = "p" };
        link.Orders.Add(new OrderNode("pb_customerid", false));
        q.Root.Links.Add(link);
        Normalize(Compile(q).Sql).Should().Contain(
            "ORDER BY (SELECT n.[fullname] FROM [data].[systemuser] AS n WHERE n.[systemuserid] = t1.[pb_customerid]) ASC");
    }

    [Fact]
    public void Order_by_lookup_without_named_target_is_refused()
    {
        var q = new QueryModel { Root = new EntityNode { EntityName = "annotation" } };
        q.Root.Orders.Add(new OrderNode("objectid", false));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*objectid*primary name*");
    }

    [Fact]
    public void Distinct_order_by_lookup_uses_its_selected_name_column()
    {
        var q = Project("pb_customerid");
        q.Distinct = true;
        q.Top = 10;
        q.Root.Orders.Add(new OrderNode("pb_customerid", false));
        var c = Compile(q);
        var name = c.Columns.Single(x => x.Kind == ResultKind.LookupName);
        Normalize(c.Sql).Should().EndWith($"ORDER BY {name.SqlAlias} ASC");
    }

    [Fact]
    public void Distinct_order_by_unselected_lookup_is_refused()
    {
        var q = Project("pb_name");
        q.Distinct = true;
        q.Root.Orders.Add(new OrderNode("pb_customerid", false));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*pb_customerid*selected*");
    }

    [Theory]
    [InlineData(false, "ASC")]
    [InlineData(true, "DESC")]
    public void Order_by_choice_uses_its_label(bool descending, string direction)
    {
        var q = Project("pb_name");
        q.Root.Orders.Add(new OrderNode("pb_stage", descending));
        var c = Compile(q);
        Normalize(c.Sql).Should().EndWith($"ORDER BY CASE t0.[pb_stage] WHEN 1 THEN @p0 WHEN 2 THEN @p1 WHEN 3 THEN @p2 END {direction}, t0.[pb_projectid] ASC");
        new[] { Param(c, "@p0"), Param(c, "@p1"), Param(c, "@p2") }.Should().Equal("Zulu", "Alpha", "Mike");
    }

    [Fact]
    public void Order_by_boolean_uses_its_label()
    {
        var q = Project("pb_name");
        q.Root.Orders.Add(new OrderNode("pb_flag", false));
        var c = Compile(q);
        Normalize(c.Sql).Should().Contain("ORDER BY CASE t0.[pb_flag] WHEN 1 THEN @p0 WHEN 0 THEN @p1 END ASC");
        new[] { Param(c, "@p0"), Param(c, "@p1") }.Should().Equal("Active", "Paused");
    }

    [Fact]
    public void Useraworderby_keeps_the_value_order_of_choices()
    {
        var q = Project("pb_name");
        q.UseRawOrderBy = true;
        q.Root.Orders.Add(new OrderNode("pb_stage", false));
        q.Root.Orders.Add(new OrderNode("pb_customerid", false));
        // useraworderby concerns choices only: the lookup is still ordered by its name.
        Normalize(Compile(q).Sql).Should().EndWith($"ORDER BY t0.[pb_stage] ASC, {UserName} ASC, t0.[pb_projectid] ASC");
    }

    [Fact]
    public void Distinct_order_by_choice_label_is_refused_unless_raw()
    {
        var q = Project("pb_stage");
        q.Distinct = true;
        q.Root.Orders.Add(new OrderNode("pb_stage", false));
        FluentActions.Invoking(() => Compile(q)).Should().Throw<XrmdValidationException>().WithMessage("*pb_stage*useraworderby*");

        q.UseRawOrderBy = true;
        Normalize(Compile(q).Sql).Should().Contain("ORDER BY t0.[pb_stage] ASC");
    }

    [Fact]
    public void Order_by_choice_without_options_keeps_the_value_order()
    {
        // No option has a label: nothing to sort by but the value (the formatted value is empty for every row as well).
        var q = Project("pb_name");
        q.Root.Orders.Add(new OrderNode("pb_priority", false));
        Normalize(Compile(q).Sql).Should().EndWith("ORDER BY t0.[pb_priority] ASC, t0.[pb_projectid] ASC");
    }

    [Fact]
    public void Stored_value_order_keeps_lookups_and_choices_by_value()
    {
        // The TDS endpoint: SQL ORDER BY semantics (the name and the label are columns of their own there).
        var q = Project("pb_name");
        q.OrderByStoredValues = true;
        q.Root.Orders.Add(new OrderNode("pb_customerid", false));
        q.Root.Orders.Add(new OrderNode("pb_stage", true));
        Normalize(Compile(q).Sql).Should().EndWith("ORDER BY t0.[pb_customerid] ASC, t0.[pb_stage] DESC, t0.[pb_projectid] ASC");
    }

    [Fact]
    public void Fetchxml_reads_useraworderby()
    {
        FetchXmlParser.Parse("<fetch useraworderby='true'><entity name='pb_project'/></fetch>").UseRawOrderBy.Should().BeTrue();
        FetchXmlParser.Parse("<fetch><entity name='pb_project'/></fetch>").UseRawOrderBy.Should().BeFalse();
    }
}
