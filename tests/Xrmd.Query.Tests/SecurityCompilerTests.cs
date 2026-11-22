using System.Data;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

/// <summary>Team and business unit hierarchy operators, and the read-security hook of the compiler.</summary>
public class SecurityCompilerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly QueryContext Context = new(UserId, Guid.NewGuid(), new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc));

    private const string TeamSubquery = "(SELECT tm.[teamid] FROM [data].[teammembership] AS tm WHERE tm.[systemuserid] = @p0)";

    private static CompiledQuery Compile(QueryModel q, QueryContext? context = null) =>
        new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(q, context ?? Context);

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static ConditionNode Cond(string attribute, ConditionOperator op, params object?[] values) => new(null, attribute, op, values);

    private static QueryModel Project(params ConditionNode[] conditions)
    {
        var root = new EntityNode { EntityName = "pb_project" };
        root.Filter.Conditions.AddRange(conditions);
        return new QueryModel { Root = root };
    }

    private static object? Param(CompiledQuery c, string name) => c.Parameters.Single(p => p.ParameterName == name).Value;

    /// <summary>Returns a fixed filter per entity (null: no restriction) and records the entities it was asked for.</summary>
    private sealed class FakeSecurity(Func<EntityMetadata, FilterNode?> filter) : IQuerySecurity
    {
        public List<string> Requested { get; } = [];

        public FilterNode? ReadFilter(EntityMetadata entity)
        {
            Requested.Add(entity.LogicalName);
            return filter(entity);
        }

        public IReadOnlySet<string> UnreadableColumns(EntityMetadata entity) => new HashSet<string>();
    }

    /// <summary>Follows the <see cref="IHierarchyResolver"/> contract: business unit hierarchy only.</summary>
    private sealed class FakeHierarchy(params Guid[] ids) : IHierarchyResolver
    {
        public List<(string Entity, Guid Id, ConditionOperator Op)> Calls { get; } = [];

        /// <summary>What <see cref="ReportingHierarchy"/> returns: the users, then the users and teams.</summary>
        public (Guid[] Users, Guid[] UsersAndTeams) Reporting { get; init; } = ([], []);

        public List<bool> ReportingCalls { get; } = [];

        public IReadOnlyCollection<Guid> ReportingHierarchy(bool includeTeams)
        {
            ReportingCalls.Add(includeTeams);
            return includeTeams ? Reporting.UsersAndTeams : Reporting.Users;
        }

        public IReadOnlyCollection<Guid> Expand(string entityName, Guid id, ConditionOperator op)
        {
            Calls.Add((entityName, id, op));
            if (entityName != "businessunit")
                throw new XrmdValidationException($"Operator {op}: this resolver expands only the business unit tree, not '{entityName}'.");
            return ids;
        }
    }

    private static FilterNode OwnedBy(Guid owner)
    {
        var f = new FilterNode();
        f.Conditions.Add(Cond("owninguser", ConditionOperator.Equal, owner));
        return f;
    }

    // ---- team operators ---------------------------------------------------------------------------------------------

    [Fact]
    public void EqualUserTeams_compiles_to_teammembership_subquery()
    {
        var c = Compile(Project(Cond("ownerid", ConditionOperator.EqualUserTeams)));
        Normalize(c.Sql).Should().Contain($"WHERE (t0.[ownerid] IN {TeamSubquery})");
        Param(c, "@p0").Should().Be(UserId);
    }

    [Fact]
    public void EqualUserOrUserTeams_compiles_to_user_or_teams()
    {
        var c = Compile(Project(Cond("ownerid", ConditionOperator.EqualUserOrUserTeams, "ignored")));
        Normalize(c.Sql).Should().Contain($"WHERE ((t0.[ownerid] = @p0 OR t0.[ownerid] IN {TeamSubquery}))");
        c.Parameters.Where(p => p.ParameterName.StartsWith("@p", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Value.Should().Be(UserId);
    }

    [Fact]
    public void Team_operators_require_a_lookup_or_uniqueidentifier()
    {
        var act = () => Compile(Project(Cond("pb_name", ConditionOperator.EqualUserTeams)));
        act.Should().Throw<XrmdValidationException>().WithMessage("*lookup or uniqueidentifier*");
    }

    // ---- hierarchy operators ----------------------------------------------------------------------------------------

    [Fact]
    public void Under_uses_hierarchy_ids_as_json()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), bu = Guid.NewGuid();
        var hierarchy = new FakeHierarchy(a, b);
        var c = Compile(Project(Cond("owningbusinessunit", ConditionOperator.Under, bu.ToString())), Context with { Hierarchy = hierarchy });

        Normalize(c.Sql).Should().Contain("WHERE (t0.[owningbusinessunit] IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@p0)))");
        var p = c.Parameters.Single(x => x.ParameterName == "@p0");
        p.DbType.Should().Be(DbType.String);
        ((string)p.Value!).Should().Contain(a.ToString()).And.Contain(b.ToString());
        hierarchy.Calls.Should().Equal(("businessunit", bu, ConditionOperator.Under));
    }

    [Theory]
    [InlineData(ConditionOperator.UnderOrEqual)]
    [InlineData(ConditionOperator.Above)]
    [InlineData(ConditionOperator.AboveOrEqual)]
    public void Other_hierarchy_operators_use_the_same_shape(ConditionOperator op)
    {
        var bu = Guid.NewGuid();
        var hierarchy = new FakeHierarchy(Guid.NewGuid());
        var c = Compile(Project(Cond("owningbusinessunit", op, bu)), Context with { Hierarchy = hierarchy });
        Normalize(c.Sql).Should().Contain("WHERE (t0.[owningbusinessunit] IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@p0)))");
        hierarchy.Calls.Should().Equal(("businessunit", bu, op));
    }

    [Fact]
    public void Under_on_a_table_without_hierarchical_relationship_is_rejected()
    {
        var hierarchy = new FakeHierarchy(Guid.NewGuid());
        var context = Context with { Hierarchy = hierarchy };

        // A lookup to systemuser and the primary key of pb_project: neither has a hierarchical relationship in the test metadata,
        // and neither is the business unit tree, so the in-memory resolver is never asked.
        var lookup = () => Compile(Project(Cond("pb_customerid", ConditionOperator.Under, Guid.NewGuid())), context);
        lookup.Should().Throw<XrmdValidationException>().WithMessage("*hierarchical relationship*");
        var pk = () => Compile(Project(Cond("pb_projectid", ConditionOperator.Under, Guid.NewGuid())), context);
        pk.Should().Throw<XrmdValidationException>().WithMessage("*hierarchical relationship*");
        hierarchy.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Under_without_hierarchy_resolver_is_rejected()
    {
        var act = () => Compile(Project(Cond("owningbusinessunit", ConditionOperator.Under, Guid.NewGuid())));
        act.Should().Throw<XrmdValidationException>().WithMessage("*Under*");
    }

    [Fact]
    public void Under_requires_one_value()
    {
        var act = () => Compile(Project(Cond("owningbusinessunit", ConditionOperator.Under)), Context with { Hierarchy = new FakeHierarchy() });
        act.Should().Throw<XrmdValidationException>().WithMessage("*exactly one value*");
    }

    [Fact]
    public void NotUnder_includes_nulls()
    {
        var c = Compile(Project(Cond("owningbusinessunit", ConditionOperator.NotUnder, Guid.NewGuid())),
            Context with { Hierarchy = new FakeHierarchy(Guid.NewGuid()) });
        Normalize(c.Sql).Should().Contain(
            "WHERE ((t0.[owningbusinessunit] NOT IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@p0)) OR t0.[owningbusinessunit] IS NULL))");
    }

    [Fact]
    public void Empty_hierarchy_matches_nothing_and_NotUnder_everything()
    {
        var context = Context with { Hierarchy = new FakeHierarchy() };
        Normalize(Compile(Project(Cond("owningbusinessunit", ConditionOperator.Under, Guid.NewGuid())), context).Sql)
            .Should().Contain("WHERE (1 = 0)");
        Normalize(Compile(Project(Cond("owningbusinessunit", ConditionOperator.NotUnder, Guid.NewGuid())), context).Sql)
            .Should().Contain("WHERE (1 = 1)");
    }

    // ---- security filter --------------------------------------------------------------------------------------------

    [Fact]
    public void Security_filter_is_anded_to_where()
    {
        var owner = Guid.NewGuid();
        var q = Project();
        q.Root.Filter.Operator = LogicalOperator.Or;
        q.Root.Filter.Conditions.Add(Cond("pb_name", ConditionOperator.Equal, "a"));
        q.Root.Filter.Conditions.Add(Cond("pb_name", ConditionOperator.Equal, "b"));
        var security = new FakeSecurity(_ => OwnedBy(owner));

        var c = Compile(q, Context with { Security = security });

        Normalize(c.Sql).Should().Contain("WHERE ((t0.[pb_name] = @p0 OR t0.[pb_name] = @p1) AND (t0.[owninguser] = @p2))");
        Param(c, "@p2").Should().Be(owner);
        security.Requested.Should().Equal("pb_project");
    }

    [Fact]
    public void Security_filter_alone_becomes_the_where()
    {
        var c = Compile(Project(), Context with { Security = new FakeSecurity(_ => OwnedBy(Guid.NewGuid())) });
        Normalize(c.Sql).Should().Contain("WHERE (t0.[owninguser] = @p0)");
    }

    [Fact]
    public void Null_security_filter_adds_nothing()
    {
        var c = Compile(Project(), Context with { Security = new FakeSecurity(_ => null) });
        Normalize(c.Sql).Should().NotContain("WHERE");
    }

    [Fact]
    public void Empty_security_filter_denies_everything()
    {
        var empty = new FakeSecurity(_ => new FilterNode { Operator = LogicalOperator.Or });
        Normalize(Compile(Project(), Context with { Security = empty }).Sql).Should().Contain("WHERE (1 = 0)");

        var q = Project();
        q.Root.Links.Add(new LinkNode { EntityName = "systemuser", From = "systemuserid", To = "pb_customerid", JoinType = JoinOperator.LeftOuter, Alias = "u" });
        var linkOnly = new FakeSecurity(e => e.LogicalName == "systemuser" ? new FilterNode() : null);
        var sql = Normalize(Compile(q, Context with { Security = linkOnly }).Sql);
        sql.Should().Contain("LEFT OUTER JOIN [data].[systemuser] AS t1 ON t1.[systemuserid] = t0.[pb_customerid] AND (1 = 0)");
        sql.Should().NotContain("WHERE");
    }

    [Fact]
    public void Security_filter_goes_into_link_on_clause()
    {
        var linkOwner = Guid.NewGuid();
        var q = Project();
        var link = new LinkNode { EntityName = "systemuser", From = "systemuserid", To = "pb_customerid", JoinType = JoinOperator.LeftOuter, Alias = "u" };
        link.Filter.Conditions.Add(Cond("fullname", ConditionOperator.Equal, "x"));
        q.Root.Links.Add(link);
        var security = new FakeSecurity(e => e.LogicalName == "systemuser" ? OwnedBy(linkOwner) : null);

        var c = Compile(q, Context with { Security = security });

        var sql = Normalize(c.Sql);
        sql.Should().Contain("LEFT OUTER JOIN [data].[systemuser] AS t1 ON t1.[systemuserid] = t0.[pb_customerid] AND (t1.[fullname] = @p0) AND (t1.[owninguser] = @p1)");
        sql.Should().NotContain("WHERE");
        Param(c, "@p1").Should().Be(linkOwner);
        security.Requested.Should().BeEquivalentTo("pb_project", "systemuser");
    }

    [Fact]
    public void Security_filter_applies_to_count_and_aggregate_probe()
    {
        var owner = Guid.NewGuid();
        var context = Context with { Security = new FakeSecurity(_ => OwnedBy(owner)) };

        // Keyset page with total record count: the count runs as its own statement.
        var keyset = Project(Cond("pb_name", ConditionOperator.Equal, "a"));
        keyset.ReturnTotalRecordCount = true;
        keyset.PageNumber = 2;
        keyset.PagingCookie = new PagingCookie(1, "pb_projectid", Guid.Empty, Guid.NewGuid()).Format();
        var c1 = Compile(keyset, context);
        c1.UsesKeyset.Should().BeTrue();
        Normalize(c1.Sql).Should().Contain("WHERE ((t0.[pb_name] = @p0) AND (t0.[owninguser] = @p1)) AND t0.[pb_projectid] > @last");
        Normalize(c1.CountSql!).Should().Contain("WHERE ((t0.[pb_name] = @p0) AND (t0.[owninguser] = @p1))");

        // Aggregate: the record-limit probe.
        var agg = Project();
        agg.Aggregate = true;
        agg.Root.Columns.Add(new ColumnNode("pb_projectid", "n", XrmAggregate.Count));
        var c2 = Compile(agg, context);
        Normalize(c2.Sql).Should().Contain("WHERE (t0.[owninguser] = @p0)");
        Normalize(c2.CountSql!).Should().Contain("WHERE (t0.[owninguser] = @p0)");

        // Distinct with total record count.
        var distinct = Project();
        distinct.Distinct = true;
        distinct.ReturnTotalRecordCount = true;
        distinct.Root.Columns.Add(new ColumnNode("pb_name"));
        Normalize(Compile(distinct, context).CountSql!).Should().Contain("WHERE (t0.[owninguser] = @p0)");
    }

    [Fact]
    public void Shared_node_compiles_to_poa_subquery()
    {
        var user = Guid.NewGuid();
        var filter = new FilterNode { Operator = LogicalOperator.Or };
        filter.Conditions.Add(Cond("ownerid", ConditionOperator.EqualUserOrUserTeams));
        filter.Shared.Add(new SharedRecordsNode(user, 10000, 1));

        var c = Compile(Project(), Context with { Security = new FakeSecurity(_ => filter) });

        Normalize(c.Sql).Should().Contain(
            "WHERE ((t0.[ownerid] = @p0 OR t0.[ownerid] IN (SELECT tm.[teamid] FROM [data].[teammembership] AS tm WHERE tm.[systemuserid] = @p0)) " +
            "OR t0.[pb_projectid] IN (SELECT poa.[objectid] FROM [data].[principalobjectaccess] AS poa WHERE poa.[objecttypecode] = @p1 " +
            "AND ((ISNULL(poa.[accessrightsmask], 0) | ISNULL(poa.[inheritedaccessrightsmask], 0)) & @p2) <> 0 " +
            "AND (poa.[principalid] = @p3 OR poa.[principalid] IN (SELECT tm.[teamid] FROM [data].[teammembership] AS tm WHERE tm.[systemuserid] = @p3))))");
        Param(c, "@p1").Should().Be(10000);
        Param(c, "@p2").Should().Be(1);
        Param(c, "@p3").Should().Be(user);
    }

    [Fact]
    public void Shared_node_with_a_type_attribute_matches_the_code_of_the_row()
    {
        // Plan 2.11 A13 (RF1), review C1: activitypointer rows are shared through their activity, recorded with the activity's
        // table code, which the row holds in activitytypecode. The share must match that code, never the id alone.
        var filter = new FilterNode { Operator = LogicalOperator.Or };
        filter.Shared.Add(new SharedRecordsNode(UserId, 10000, 1, TypeAttribute: "pb_count"));

        var c = Compile(Project(), Context with { Security = new FakeSecurity(_ => filter) });

        Normalize(c.Sql).Should().Contain(
            "WHERE (t0.[pb_projectid] IN (SELECT poa.[objectid] FROM [data].[principalobjectaccess] AS poa WHERE " +
            "poa.[objecttypecode] = t0.[pb_count] AND ((ISNULL(poa.[accessrightsmask], 0) | ISNULL(poa.[inheritedaccessrightsmask], 0)) & @p0) <> 0 " +
            "AND (poa.[principalid] = @p1 OR");
    }

    [Fact]
    public void Shared_node_with_principals_matches_exactly_those_principals()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var filter = new FilterNode { Operator = LogicalOperator.Or };
        filter.Shared.Add(new SharedRecordsNode(UserId, 10000, 1, [a, b]));

        var c = Compile(Project(), Context with { Security = new FakeSecurity(_ => filter) });

        Normalize(c.Sql).Should().Contain(
            "WHERE (t0.[pb_projectid] IN (SELECT poa.[objectid] FROM [data].[principalobjectaccess] AS poa WHERE poa.[objecttypecode] = @p0 " +
            "AND ((ISNULL(poa.[accessrightsmask], 0) | ISNULL(poa.[inheritedaccessrightsmask], 0)) & @p1) <> 0 " +
            "AND poa.[principalid] IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@p2))))");
        ((string)Param(c, "@p2")!).Should().Contain(a.ToString()).And.Contain(b.ToString());
        c.Parameters.Should().NotContain(p => Equals(p.Value, UserId), "the principal set replaces the user and its teams");

        // An empty set shares nothing.
        var none = new FilterNode { Operator = LogicalOperator.Or };
        none.Shared.Add(new SharedRecordsNode(UserId, 10000, 1, []));
        Normalize(Compile(Project(), Context with { Security = new FakeSecurity(_ => none) }).Sql).Should().Contain("WHERE (1 = 0)");
    }

    [Fact]
    public void Owned_node_is_one_json_parameter_whatever_the_number_of_principals()
    {
        // A manager with many reports (plan 2.5, H4): the owner branch must not inline one parameter per principal.
        var many = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToList();
        var filter = new FilterNode { Operator = LogicalOperator.Or };
        filter.Owned.Add(new OwnedRecordsNode(many));

        var c = Compile(Project(), Context with { Security = new FakeSecurity(_ => filter) });

        Normalize(c.Sql).Should().Contain("WHERE (t0.[ownerid] IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@p0)))");
        c.Parameters.Select(p => p.ParameterName).Should().Equal("@p0", "@top");
        ((string)Param(c, "@p0")!).Should().Contain(many[0].ToString()).And.Contain(many[^1].ToString());

        // An empty set owns nothing.
        var none = new FilterNode { Operator = LogicalOperator.Or };
        none.Owned.Add(new OwnedRecordsNode([]));
        Normalize(Compile(Project(), Context with { Security = new FakeSecurity(_ => none) }).Sql).Should().Contain("WHERE (1 = 0)");
    }

    [Fact]
    public void Owned_node_in_every_link_scope_keeps_the_parameter_count_flat()
    {
        // 30 link-entities each restricted by a 100-principal reach: 3000 inline parameters before, 31 JSON parameters now.
        var reach = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList();
        var q = Project();
        for (var i = 0; i < 30; i++)
            q.Root.Links.Add(new LinkNode { EntityName = "pb_project", From = "pb_projectid", To = "pb_projectid", Alias = "l" + i });
        var c = Compile(q, Context with
        {
            Security = new FakeSecurity(_ =>
            {
                var f = new FilterNode { Operator = LogicalOperator.Or };
                f.Owned.Add(new OwnedRecordsNode(reach));
                return f;
            }),
        });

        c.Parameters.Where(p => p.ParameterName != "@top").Should().HaveCount(31);
    }

    // ---- user hierarchy operators (plan 2.5, H7) --------------------------------------------------------------------

    [Theory]
    [InlineData(ConditionOperator.EqualUserOrUserHierarchy, false)]
    [InlineData(ConditionOperator.EqualUserOrUserHierarchyAndTeams, true)]
    public void User_hierarchy_operators_compile_to_the_resolved_ids(ConditionOperator op, bool teams)
    {
        var report = Guid.NewGuid();
        var team = Guid.NewGuid();
        var resolver = new FakeHierarchy { Reporting = ([UserId, report], [UserId, report, team]) };

        var c = Compile(Project(Cond("ownerid", op)), Context with { Hierarchy = resolver });

        Normalize(c.Sql).Should().Contain("WHERE (t0.[ownerid] IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@p0)))");
        var json = (string)Param(c, "@p0")!;
        json.Should().Contain(UserId.ToString()).And.Contain(report.ToString());
        json.Contains(team.ToString()).Should().Be(teams);
        resolver.ReportingCalls.Should().Equal(teams);
    }

    [Fact]
    public void User_hierarchy_operators_need_an_id_column_and_a_resolver()
    {
        var resolver = new FakeHierarchy { Reporting = ([UserId], [UserId]) };
        var onName = () => Compile(Project(Cond("pb_name", ConditionOperator.EqualUserOrUserHierarchy)), Context with { Hierarchy = resolver });
        onName.Should().Throw<XrmdValidationException>().WithMessage("*requires a lookup or uniqueidentifier*");

        var noResolver = () => Compile(Project(Cond("ownerid", ConditionOperator.EqualUserOrUserHierarchyAndTeams)));
        noResolver.Should().Throw<XrmdValidationException>().WithMessage("*EqualUserOrUserHierarchyAndTeams*not supported in this context*");

        // A resolver that refuses (the model is disabled) passes its error through.
        var disabled = new RefusingHierarchy();
        var refused = () => Compile(Project(Cond("ownerid", ConditionOperator.EqualUserOrUserHierarchy)), Context with { Hierarchy = disabled });
        refused.Should().Throw<XrmdValidationException>().WithMessage("disabled");

        // No id at all matches nothing.
        Normalize(Compile(Project(Cond("ownerid", ConditionOperator.EqualUserOrUserHierarchy)), Context with { Hierarchy = new FakeHierarchy() }).Sql)
            .Should().Contain("WHERE (1 = 0)");
    }

    private sealed class RefusingHierarchy : IHierarchyResolver
    {
        public IReadOnlyCollection<Guid> Expand(string entityName, Guid id, ConditionOperator op) => throw new XrmdValidationException("disabled");

        public IReadOnlyCollection<Guid> ReportingHierarchy(bool includeTeams) => throw new XrmdValidationException("disabled");
    }

    // ---- FetchXML ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("eq-useroruserteams", ConditionOperator.EqualUserOrUserTeams)]
    [InlineData("under", ConditionOperator.Under)]
    [InlineData("eq-or-under", ConditionOperator.UnderOrEqual)]
    [InlineData("above", ConditionOperator.Above)]
    [InlineData("eq-or-above", ConditionOperator.AboveOrEqual)]
    [InlineData("not-under", ConditionOperator.NotUnder)]
    [InlineData("eq-useroruserhierarchy", ConditionOperator.EqualUserOrUserHierarchy)]
    [InlineData("eq-useroruserhierarchyandteams", ConditionOperator.EqualUserOrUserHierarchyAndTeams)]
    public void FetchXml_parses_team_and_hierarchy_operators(string op, ConditionOperator expected)
    {
        var q = FetchXmlParser.Parse($"""<fetch><entity name="a"><filter><condition attribute="x" operator="{op}"/></filter></entity></fetch>""");
        q.Root.Filter.Conditions.Single().Operator.Should().Be(expected);
    }
}
