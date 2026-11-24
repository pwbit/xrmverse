using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata.Sdk;
using Xrmd.Query;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Integration.Tests;

/// <summary>Executes SQL produced by <see cref="SqlQueryCompiler"/> on SQL Server (syntax and type checks).</summary>
[Collection("xrmd")]
public sealed class SqlQueryCompilerSqlTests(XrmdFixture fx) : IAsyncLifetime
{
    private static readonly DateTime Start = new(2026, 11, 7, 10, 30, 15, DateTimeKind.Utc);

    private IMetadataStore _store = null!;
    private IDbConnectionFactory _factory = null!;
    private Guid _projectId;
    private readonly string _name = "Q50%_[" + Guid.NewGuid().ToString("N");

    private Guid _adminId;

    public async Task InitializeAsync()
    {
        _store = fx.App.Services.GetRequiredService<IMetadataStore>();
        _factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        await TestEntities.EnsureProjectAsync(_store);
        var svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        Guid adminId;
        await using (var conn = await _factory.OpenAsync())
        await using (var cmd = conn.Command("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid"))
        {
            cmd.AddParameter("@oid", DbType.Guid, fx.AdminObjectId);
            adminId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }
        _adminId = adminId;
        _projectId = await svc.CreateAsync(new Entity(TestEntities.Project)
        {
            ["pb_name"] = _name,
            ["pb_budget"] = new Money(1234.5m),
            ["pb_customerid"] = new EntityReference("systemuser", adminId),
            ["pb_startdate"] = Start,
            ["pb_priority"] = new OptionSetValue(2),
            ["pb_count"] = 7,
            ["pb_ratio"] = 0.5,
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<List<Dictionary<string, object?>>> RunAsync(QueryModel q) =>
        RunAsync(q, new QueryContext(Guid.Empty, Guid.Empty, DateTime.UtcNow));

    private async Task<List<Dictionary<string, object?>>> RunAsync(QueryModel q, QueryContext context)
    {
        var compiled = new SqlQueryCompiler(_store, TestSql.Dialect).Compile(q, context);
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.Command(compiled.Sql);
        cmd.AddParameters(compiled.Parameters.ToArray());
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await rd.ReadAsync())
            rows.Add(compiled.Columns.Where(c => c.Kind == ResultKind.Value)
                .ToDictionary(c => c.ResultName, c => rd.IsDBNull(rd.GetOrdinal(c.SqlAlias)) ? null : rd.GetValue(rd.GetOrdinal(c.SqlAlias))));
        return rows;
    }

    private static QueryModel ProjectQuery(params ConditionNode[] conditions)
    {
        var root = new EntityNode { EntityName = TestEntities.Project, AllColumns = true };
        root.Filter.Conditions.AddRange(conditions);
        var link = new LinkNode { EntityName = "systemuser", From = "systemuserid", To = "pb_customerid", JoinType = JoinOperator.LeftOuter, Alias = "u" };
        link.Columns.Add(new ColumnNode("fullname"));
        root.Links.Add(link);
        return new QueryModel { Root = root, Top = 10 };
    }

    private static ConditionNode Cond(string attribute, ConditionOperator op, params object?[] values) => new(null, attribute, op, values);

    private static object?[] Many(object first, Func<int, object> other) =>
        [first, .. Enumerable.Range(0, 150).Select(other)];

    [Fact]
    public async Task Compiled_queries_execute_and_find_the_row()
    {
        var conditions = new[]
        {
            Cond("pb_name", ConditionOperator.BeginsWith, "Q50%_["),
            Cond("pb_name", ConditionOperator.EndsWith, _name[^6..]),
            Cond("pb_count", ConditionOperator.Between, 5, 9),
            Cond("pb_budget", ConditionOperator.GreaterThan, "1000"),
            Cond("pb_ratio", ConditionOperator.LessEqual, 0.5),
            Cond("pb_startdate", ConditionOperator.Equal, "2026-11-07T10:30:15Z"),
            Cond("versionnumber", ConditionOperator.GreaterThan, 0L),
            Cond("pb_count", ConditionOperator.In, Many(7, i => 1000 + i)),
            Cond("pb_priority", ConditionOperator.In, Many(2, i => 1000 + i)),
            Cond("pb_name", ConditionOperator.In, Many(_name, i => "x" + i)),
            Cond("pb_projectid", ConditionOperator.In, Many(_projectId, _ => Guid.NewGuid())),
            Cond("pb_startdate", ConditionOperator.In, Many(Start, i => Start.AddDays(i + 1))),
            Cond("pb_budget", ConditionOperator.In, Many(1234.5m, i => (decimal)i)),
            Cond("pb_ratio", ConditionOperator.In, Many(0.5, i => i + 0.25)),
            Cond("pb_count", ConditionOperator.NotIn, Many(1, i => 1000 + i)),
        };
        foreach (var condition in conditions)
        {
            // Each condition AND the unique name, so that rows of other tests never match.
            var rows = await RunAsync(ProjectQuery(condition, Cond("pb_name", ConditionOperator.Equal, _name)));
            rows.Should().ContainSingle($"condition {condition.Operator} on {condition.Attribute}");
            rows[0]["pb_projectid"].Should().Be(_projectId);
            rows[0]["u.fullname"].Should().Be(XrmdFixture.AdminName);
            rows[0]["versionnumber"].Should().BeOfType<long>();
        }
    }

    private async Task<(CompiledQuery Compiled, List<object?[]> Rows, object? Count)> RunRawAsync(QueryModel q)
    {
        var compiled = new SqlQueryCompiler(_store, TestSql.Dialect).Compile(q, new QueryContext(Guid.Empty, Guid.Empty, DateTime.UtcNow));
        await using var conn = await _factory.OpenAsync();
        var rows = new List<object?[]>();
        await using (var cmd = conn.Command(compiled.Sql))
        {
            cmd.AddParameters(compiled.Parameters);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                rows.Add(compiled.Columns.Select(c => rd.IsDBNull(rd.GetOrdinal(c.SqlAlias)) ? null : rd.GetValue(rd.GetOrdinal(c.SqlAlias))).ToArray());
        }
        object? count = null;
        if (compiled.CountSql is not null)
        {
            await using var cmd = conn.Command(compiled.CountSql);
            cmd.AddParameters(compiled.Parameters);
            count = await cmd.ExecuteScalarAsync();
        }
        return (compiled, rows, count);
    }

    [Fact]
    public async Task Paged_and_aggregate_queries_execute()
    {
        QueryModel Named(Action<QueryModel> configure)
        {
            var root = new EntityNode { EntityName = TestEntities.Project };
            root.Columns.Add(new ColumnNode("pb_name"));
            root.Filter.Conditions.Add(Cond("pb_name", ConditionOperator.Equal, _name));
            var q = new QueryModel { Root = root, ReturnTotalRecordCount = true };
            configure(q);
            return q;
        }

        // Page 1 with window total count.
        var (c1, rows1, _) = await RunRawAsync(Named(_ => { }));
        rows1.Should().ContainSingle();
        rows1[0][c1.Columns.ToList().FindIndex(c => c.Kind == ResultKind.TotalCount)].Should().Be(1);

        // OFFSET page (ordered by name), distinct OFFSET page, keyset page with CountSql.
        var (_, rows2, _) = await RunRawAsync(Named(q => { q.Root.Orders.Add(new OrderNode("pb_name", true)); q.PageNumber = 2; q.PageSize = 1; }));
        rows2.Should().BeEmpty();
        var (_, rows3, count3) = await RunRawAsync(Named(q => { q.Distinct = true; q.Root.Orders.Add(new OrderNode("pb_name", true)); q.PageNumber = 2; }));
        rows3.Should().BeEmpty();
        count3.Should().Be(1);
        var cookie = new Xrmd.Query.FetchXml.PagingCookie(1, "pb_projectid", Guid.Empty, Guid.Empty).Format();
        var (c4, rows4, count4) = await RunRawAsync(Named(q => { q.PageNumber = 2; q.PagingCookie = cookie; }));
        c4.UsesKeyset.Should().BeTrue();
        rows4.Should().ContainSingle();
        count4.Should().Be(1);

        // Aggregate with groupby, dategrouping, order by alias, OFFSET page, and the record-limit probe.
        var root = new EntityNode { EntityName = TestEntities.Project };
        root.Columns.Add(new ColumnNode("pb_count", "total", XrmAggregate.Sum));
        root.Columns.Add(new ColumnNode("pb_count", "avg", XrmAggregate.Avg));
        root.Columns.Add(new ColumnNode("pb_name", "names", XrmAggregate.CountColumn, Distinct: true));
        root.Columns.Add(new ColumnNode("pb_priority", "prio", GroupBy: true));
        root.Columns.Add(new ColumnNode("pb_startdate", "month", GroupBy: true, DateGrouping: DateGrouping.Month));
        root.Columns.Add(new ColumnNode("ownerid", "owner", GroupBy: true));
        root.Filter.Conditions.Add(Cond("pb_name", ConditionOperator.Equal, _name));
        root.Orders.Add(new OrderNode("", true, "total"));
        var agg = new QueryModel { Root = root, Aggregate = true };
        var (c5, rows5, count5) = await RunRawAsync(agg);
        rows5.Should().ContainSingle();
        rows5[0][0].Should().BeOfType<long>().And.Be(7L);
        rows5[0][1].Should().Be(7m);
        rows5[0][2].Should().Be(1);
        rows5[0][4].Should().Be(11);
        count5.Should().Be(1);
        c5.Columns.Should().Contain(c => c.Kind == ResultKind.LookupType && c.ResultName == "owner");
        agg.PageNumber = 2;
        var (_, rows6, _) = await RunRawAsync(agg);
        rows6.Should().BeEmpty();
    }

    // ---- team and hierarchy operators, shared records ---------------------------------------------------------------

    private sealed class FixedSecurity(FilterNode filter) : IQuerySecurity
    {
        public FilterNode? ReadFilter(Microsoft.Xrm.Sdk.Metadata.EntityMetadata entity) => filter;

        public IReadOnlySet<string> UnreadableColumns(Microsoft.Xrm.Sdk.Metadata.EntityMetadata entity) => new HashSet<string>();
    }

    private sealed class FixedHierarchy(params Guid[] ids) : IHierarchyResolver
    {
        public IReadOnlyCollection<Guid> Expand(string entityName, Guid id, ConditionOperator op) => ids;

        public IReadOnlyCollection<Guid> ReportingHierarchy(bool includeTeams) => throw new NotSupportedException();
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.Command(sql);
        foreach (var (name, value) in parameters) cmd.AddParameter(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A project named <paramref name="name"/> owned by <paramref name="ownerId"/> (written directly: no owner checks).</summary>
    private async Task<Guid> ProjectOwnedByAsync(string name, Guid ownerId, int ownerType)
    {
        var svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        var id = await svc.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = name });
        await ExecAsync("UPDATE data.pb_project SET ownerid = @owner, owneridtype = @type WHERE pb_projectid = @id",
            ("@owner", ownerId), ("@type", ownerType), ("@id", id));
        return id;
    }

    private static QueryModel NamedProjects(string name, params ConditionNode[] conditions)
    {
        var root = new EntityNode { EntityName = TestEntities.Project };
        root.Filter.Conditions.Add(Cond("pb_name", ConditionOperator.Equal, name));
        root.Filter.Conditions.AddRange(conditions);
        return new QueryModel { Root = root };
    }

    [Fact]
    public async Task Team_operators_run_on_sql_server()
    {
        var bu = await SecurityTestHelper.RootBusinessUnitAsync(fx);
        var user = await SecurityTestHelper.CreateUserAsync(fx, bu);
        var team = await SecurityTestHelper.CreateTeamAsync(fx, bu, 0, user);
        var otherTeam = await SecurityTestHelper.CreateTeamAsync(fx, bu, 0);
        var name = "team-ops-" + Guid.NewGuid().ToString("N");
        var own = await ProjectOwnedByAsync(name, user, 8);
        var teams = await ProjectOwnedByAsync(name, team, 9);
        await ProjectOwnedByAsync(name, otherTeam, 9);
        await ProjectOwnedByAsync(name, _adminId, 8);
        var context = new QueryContext(user, bu, DateTime.UtcNow) { Hierarchy = new FixedHierarchy(bu) };

        var both = await RunAsync(NamedProjects(name, Cond("ownerid", ConditionOperator.EqualUserOrUserTeams)), context);
        both.Select(r => r["pb_projectid"]).Should().BeEquivalentTo(new object[] { own, teams });
        var teamOnly = await RunAsync(NamedProjects(name, Cond("ownerid", ConditionOperator.EqualUserTeams)), context);
        teamOnly.Select(r => r["pb_projectid"]).Should().Equal(teams);

        // Hierarchy operators: ids from the resolver as OPENJSON.
        (await RunAsync(NamedProjects(name, Cond("owningbusinessunit", ConditionOperator.Under, bu)), context)).Should().HaveCount(4);
        (await RunAsync(NamedProjects(name, Cond("owningbusinessunit", ConditionOperator.NotUnder, bu)), context)).Should().BeEmpty();
    }

    [Fact]
    public async Task Shared_node_runs_on_sql_server()
    {
        var bu = await SecurityTestHelper.RootBusinessUnitAsync(fx);
        var user = await SecurityTestHelper.CreateUserAsync(fx, bu);
        var team = await SecurityTestHelper.CreateTeamAsync(fx, bu, 1, user);
        var name = "shared-" + Guid.NewGuid().ToString("N");
        var toUser = await ProjectOwnedByAsync(name, _adminId, 8);
        var toTeam = await ProjectOwnedByAsync(name, _adminId, 8);
        var inherited = await ProjectOwnedByAsync(name, _adminId, 8);
        var writeOnly = await ProjectOwnedByAsync(name, _adminId, 8);
        await ProjectOwnedByAsync(name, _adminId, 8); // not shared
        var otc = _store.GetEntity(TestEntities.Project)!.ObjectTypeCode!.Value;
        foreach (var (principal, type, project, mask, inheritedMask) in new[]
                 {
                     (user, 8, toUser, 1, 0), (team, 9, toTeam, 1 | 2, 0), (user, 8, inherited, 0, 1), (user, 8, writeOnly, 2, 0),
                 })
            await ExecAsync("""
                INSERT INTO data.principalobjectaccess (principalobjectaccessid, principalid, principaltypecode, objectid, objecttypecode,
                    accessrightsmask, inheritedaccessrightsmask)
                VALUES (@id, @principal, @type, @object, @otc, @mask, @inherited)
                """, ("@id", Guid.NewGuid()), ("@principal", principal), ("@type", type), ("@object", project), ("@otc", otc), ("@mask", mask), ("@inherited", inheritedMask));

        var filter = new FilterNode();
        filter.Shared.Add(new SharedRecordsNode(user, otc, 1));
        var context = new QueryContext(user, bu, DateTime.UtcNow) { Security = new FixedSecurity(filter) };

        var rows = await RunAsync(NamedProjects(name), context);
        rows.Select(r => r["pb_projectid"]).Should().BeEquivalentTo(new object[] { toUser, toTeam, inherited });
    }
}
