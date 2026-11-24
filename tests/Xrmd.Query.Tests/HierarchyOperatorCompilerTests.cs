using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Model.Query;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

/// <summary>Plan 2.5, task 2: the tree operators compile to a recursive CTE on tables with a hierarchical relationship (H6, H8).</summary>
public class HierarchyOperatorCompilerTests
{
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc));

    private static CompiledQuery Compile(QueryModel q, QueryContext? context = null) =>
        new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(q, context ?? Context);

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static QueryModel Query(string entity, params ConditionNode[] conditions)
    {
        var root = new EntityNode { EntityName = entity };
        root.Filter.Conditions.AddRange(conditions);
        return new QueryModel { Root = root };
    }

    private static ConditionNode Cond(string attribute, FilterOperator op, params object?[] values) => new(null, attribute, op, values);

    private const string Down =
        "WITH h0 (id, lvl) AS (SELECT t.[accountid], 0 FROM [data].[account] AS t WHERE t.[accountid] = @p0 " +
        "UNION ALL SELECT c.[accountid], h.lvl + 1 FROM [data].[account] AS c JOIN h0 AS h ON c.[parentaccountid] = h.id " +
        "WHERE h.lvl < 100 AND c.[accountid] <> @p0)";

    private const string Up =
        "WITH h0 (id, parent, lvl) AS (SELECT t.[accountid], t.[parentaccountid], 0 FROM [data].[account] AS t WHERE t.[accountid] = @p0 " +
        "UNION ALL SELECT c.[accountid], c.[parentaccountid], h.lvl + 1 FROM [data].[account] AS c JOIN h0 AS h ON c.[accountid] = h.parent " +
        "WHERE h.lvl < 100 AND c.[accountid] <> @p0)";

    [Theory]
    [InlineData(FilterOperator.Under, Down, "t0.[accountid] IN (SELECT id FROM h0 WHERE lvl > 0)")]
    [InlineData(FilterOperator.UnderOrEqual, Down, "t0.[accountid] IN (SELECT id FROM h0)")]
    [InlineData(FilterOperator.NotUnder, Down, "(t0.[accountid] NOT IN (SELECT id FROM h0) OR t0.[accountid] IS NULL)")]
    [InlineData(FilterOperator.Above, Up, "t0.[accountid] IN (SELECT id FROM h0 WHERE lvl > 0)")]
    [InlineData(FilterOperator.AboveOrEqual, Up, "t0.[accountid] IN (SELECT id FROM h0)")]
    public void Operators_on_the_primary_key_compile_to_a_cte(FilterOperator op, string cte, string predicate)
    {
        var id = Guid.NewGuid();
        var c = Compile(Query("account", Cond("accountid", op, id)));
        var sql = Normalize(c.Sql);
        sql.Should().StartWith(cte + " SELECT TOP (@top)");
        sql.Should().Contain($"WHERE ({predicate})");
        c.Parameters.Single(p => p.ParameterName == "@p0").Value.Should().Be(id);
    }

    [Theory]
    [InlineData(FilterOperator.Under, Down, "t0.[parentaccountid] IN (SELECT id FROM h0 WHERE lvl > 0)")]
    [InlineData(FilterOperator.UnderOrEqual, Down, "t0.[parentaccountid] IN (SELECT id FROM h0)")]
    [InlineData(FilterOperator.NotUnder, Down, "(t0.[parentaccountid] NOT IN (SELECT id FROM h0) OR t0.[parentaccountid] IS NULL)")]
    [InlineData(FilterOperator.Above, Up, "t0.[parentaccountid] IN (SELECT id FROM h0 WHERE lvl > 0)")]
    [InlineData(FilterOperator.AboveOrEqual, Up, "t0.[parentaccountid] IN (SELECT id FROM h0)")]
    public void Operators_on_a_lookup_to_the_hierarchical_table_run_the_cte_on_the_target(FilterOperator op, string cte, string predicate)
    {
        // account.parentaccountid is a lookup to account: the CTE runs on account, the predicate on the lookup column.
        var c = Compile(Query("account", Cond("parentaccountid", op, Guid.NewGuid())));
        var sql = Normalize(c.Sql);
        sql.Should().StartWith(cte + " SELECT TOP (@top)");
        sql.Should().Contain($"WHERE ({predicate})");
    }

    [Fact]
    public void Two_hierarchical_conditions_get_h0_and_h1()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Compile(Query("account", Cond("accountid", FilterOperator.Under, a), Cond("parentaccountid", FilterOperator.Above, b)));
        var sql = Normalize(c.Sql);
        sql.Should().StartWith("WITH h0 (id, lvl) AS (");
        sql.Should().Contain("), h1 (id, parent, lvl) AS (");
        sql.Should().Contain("t0.[accountid] IN (SELECT id FROM h0 WHERE lvl > 0)");
        sql.Should().Contain("t0.[parentaccountid] IN (SELECT id FROM h1 WHERE lvl > 0)");
        // The second CTE uses its own parameter.
        sql.Should().Contain("WHERE t.[accountid] = @p1");
        c.Parameters.Single(p => p.ParameterName == "@p1").Value.Should().Be(b);
        Regex.Matches(sql, "WITH ").Should().HaveCount(1);
    }

    [Fact]
    public void The_prelude_is_the_first_token_of_every_statement()
    {
        var id = Guid.NewGuid();
        var q = Query("account", Cond("accountid", FilterOperator.UnderOrEqual, id));
        q.ReturnTotalRecordCount = true;
        q.Distinct = true;
        var c = Compile(q);
        c.Sql.Should().StartWith("WITH h0");
        c.CountSql.Should().NotBeNull();
        Normalize(c.CountSql!).Should().StartWith(Down).And.Contain("SELECT COUNT(*) FROM (SELECT DISTINCT");
    }

    [Fact]
    public void The_prelude_precedes_the_count_probe_of_an_aggregate_query()
    {
        var q = Query("account", Cond("accountid", FilterOperator.Under, Guid.NewGuid()));
        q.Aggregate = true;
        q.Root.Columns.Add(new ColumnNode("accountid", "n", XrmAggregate.Count, false, false, null));
        var c = Compile(q);
        c.Sql.Should().StartWith("WITH h0");
        Normalize(c.CountSql!).Should().StartWith("WITH h0").And.Contain("SELECT COUNT(*) FROM (SELECT TOP 50001 1 AS x");
    }

    [Fact]
    public void The_prelude_precedes_offset_and_keyset_paging()
    {
        var q = Query("account", Cond("accountid", FilterOperator.Under, Guid.NewGuid()));
        q.PageNumber = 2;
        q.PageSize = 10;
        var offset = Compile(q);
        offset.Sql.Should().StartWith("WITH h0").And.Contain("OFFSET");

        q.PagingCookie = new PagingCookie(1, "accountid", Guid.NewGuid(), Guid.NewGuid()).Format();
        var keyset = Compile(q);
        keyset.UsesKeyset.Should().BeTrue();
        keyset.Sql.Should().StartWith("WITH h0");
    }

    [Fact]
    public void Query_without_hierarchical_conditions_has_no_prelude()
    {
        Compile(Query("account", Cond("name", FilterOperator.Equal, "x"))).Sql.Should().StartWith("SELECT");
    }

    [Fact]
    public void Business_unit_conditions_still_use_the_resolver_without_a_cte()
    {
        var resolver = new Resolver(Guid.NewGuid());
        var c = Compile(Query("systemuser", Cond("businessunitid", FilterOperator.Under, Guid.NewGuid())), Context with { Hierarchy = resolver });
        c.Sql.Should().StartWith("SELECT").And.NotContain("WITH h");
        Normalize(c.Sql).Should().Contain("OPENJSON(@p0)");
        resolver.Calls.Should().Equal("businessunit");
    }

    [Theory]
    [InlineData("pb_project", "pb_projectid")]
    [InlineData("pb_project", "pb_customerid")] // a lookup to systemuser, which has no hierarchical relationship in this metadata
    public void A_table_without_a_hierarchical_relationship_is_refused(string entity, string attribute)
    {
        var act = () => Compile(Query(entity, Cond(attribute, FilterOperator.Under, Guid.NewGuid())), Context with { Hierarchy = new Resolver() });
        act.Should().Throw<XrmdValidationException>().WithMessage("*hierarchical relationship*");
    }

    [Theory]
    [InlineData(FilterOperator.Under)]
    [InlineData(FilterOperator.UnderOrEqual)]
    [InlineData(FilterOperator.Above)]
    [InlineData(FilterOperator.AboveOrEqual)]
    [InlineData(FilterOperator.NotUnder)]
    public void A_multi_target_lookup_is_refused_instead_of_walking_its_first_target(FilterOperator op)
    {
        // pb_regardingid targets account (hierarchical) and systemuser: walking account would silently miss a user value.
        var act = () => Compile(Query("account", Cond("pb_regardingid", op, Guid.NewGuid())));
        act.Should().Throw<XrmdValidationException>()
            .WithMessage($"Operator {op} is not supported on the multi-target lookup 'pb_regardingid' (targets: account, systemuser)*");
    }

    [Fact]
    public void Ownerid_walks_the_user_tree()
    {
        // ownerid targets systemuser and team: the user hierarchy applies (this metadata gives systemuser no hierarchical
        // relationship, so the refusal names the table the compiler chose).
        var act = () => Compile(Query("account", Cond("ownerid", FilterOperator.Under, Guid.NewGuid())));
        act.Should().Throw<XrmdValidationException>().WithMessage("*'systemuser' has none*");
    }

    [Fact]
    public void A_tree_operator_in_a_link_entity_filter_gets_the_prelude()
    {
        // The CTE of a condition in a link-entity's ON clause is registered while the FROM clause is built, so the prelude
        // must be read after it (SqlQueryCompiler): a reorder would give "Invalid object name 'h0'" at run time.
        var id = Guid.NewGuid();
        var q = FetchXmlParser.Parse($"""
            <fetch><entity name="account"><attribute name="name"/>
              <link-entity name="account" from="accountid" to="parentaccountid" alias="p">
                <filter><condition attribute="accountid" operator="under" value="{id}"/></filter>
              </link-entity>
            </entity></fetch>
            """);
        var c = Compile(q);
        var sql = Normalize(c.Sql);
        sql.Should().StartWith(Down + " SELECT TOP (@top)");
        sql.Should().Contain("ON t1.[accountid] = t0.[parentaccountid] AND (t1.[accountid] IN (SELECT id FROM h0 WHERE lvl > 0))");
        c.Parameters.Single(p => p.ParameterName == "@p0").Value.Should().Be(id);
    }

    [Fact]
    public void A_non_key_non_lookup_attribute_is_refused()
    {
        var act = () => Compile(Query("account", Cond("name", FilterOperator.Under, Guid.NewGuid())));
        act.Should().Throw<XrmdValidationException>().WithMessage("*lookup or uniqueidentifier*");
    }

    [Fact]
    public void A_tree_operator_takes_one_value()
    {
        var act = () => Compile(Query("account", Cond("accountid", FilterOperator.Under)));
        act.Should().Throw<XrmdValidationException>().WithMessage("*exactly one value*");
    }

    [Fact]
    public void RowAggregate_is_refused()
    {
        var act = () => FetchXmlParser.Parse("""
            <fetch><entity name="account"><attribute name="name"/><attribute name="accountid" rowaggregate="CountChildren" alias="kids"/></entity></fetch>
            """);
        act.Should().Throw<XrmdValidationException>().WithMessage("*rowaggregate*not supported*");
    }

    /// <summary>The business unit resolver: records the entity it was asked for.</summary>
    private sealed class Resolver(params Guid[] ids) : IHierarchyResolver
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyCollection<Guid> Expand(string entityName, Guid id, FilterOperator op)
        {
            Calls.Add(entityName);
            return ids;
        }

        public IReadOnlyCollection<Guid> ReportingHierarchy(bool includeTeams) => throw new NotSupportedException();
    }
}
