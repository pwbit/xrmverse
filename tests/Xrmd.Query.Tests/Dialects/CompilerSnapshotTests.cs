using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Query;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;
using Xrmd.Storage.PostgreSql;
using static Xrmd.Metadata.Computed.ComputedExpr;

namespace Xrmd.Query.Tests.Dialects;

/// <summary>
/// Plan 2.7a, Task 7: twelve representative <see cref="QueryModel"/>s (and rollup recalculations) compiled for PostgreSQL equal the
/// checked-in snapshots under <c>Dialects/Snapshots/pg/</c>: the SQL text, then each parameter (name, type, value). A change of the
/// PostgreSQL SQL shows as a diff of a snapshot; <c>XRMD_UPDATE_SNAPSHOTS=1</c> rewrites them (review the diff before committing).
/// The SQL Server text of the same models is asserted by the other compiler tests, unchanged.
/// </summary>
public sealed class CompilerSnapshotTests
{
    private static readonly ISqlDialect Pg = PostgresDialect.Instance;

    private static readonly QueryContext Context = new(
        new Guid("11111111-1111-1111-1111-111111111111"), new Guid("22222222-2222-2222-2222-222222222222"),
        new DateTime(2026, 11, 19, 10, 30, 0, DateTimeKind.Utc));

    private static readonly DateTime Now = new(2026, 11, 19, 10, 30, 0, DateTimeKind.Utc);

    private readonly Definitions _computed = new();

    private sealed class Definitions : IComputedColumns
    {
        private readonly Dictionary<string, ComputedExpr> _bodies = new(StringComparer.Ordinal);

        public Definitions Set(string table, string column, ComputedExpr body)
        {
            _bodies[$"{table}.{column}"] = body;
            return this;
        }

        public ComputedDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities) =>
            _bodies.TryGetValue($"{entity.LogicalName}.{attribute.LogicalName}", out var body)
                ? new ComputedDefinition(body, [], entity.LogicalName == "xrmv_rollacct" ? 1 : 0)
                : throw new XrmdValidationException($"No definition for '{entity.LogicalName}.{attribute.LogicalName}'.");
    }

    private CompiledQuery Compile(QueryModel q) => new SqlQueryCompiler(TestMetadata.Instance, Pg, _computed).Compile(q, Context);

    private static QueryModel Query(string entity, params string[] columns)
    {
        var root = new EntityNode { EntityName = entity };
        root.Columns.AddRange(columns.Select(c => new ColumnNode(c)));
        return new QueryModel { Root = root };
    }

    private static Column Own(string attribute) => new(null, attribute);

    private static Function Fx(string name, params ComputedExpr[] args) => new("fx." + name, args);

    [Fact]
    public void Filter_order_and_paging()
    {
        var q = Query("pb_project", "pb_name", "pb_count");
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_count", FilterOperator.GreaterThan, [1]));
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_name", FilterOperator.Like, ["a[_]%"]));
        q.Root.Orders.Add(new OrderNode("pb_name", Descending: true));
        q.PageSize = 50;
        q.PageNumber = 3;
        Matches(Compile(q));
    }

    [Fact]
    public void Keyset_page()
    {
        var q = Query("pb_project", "pb_name");
        q.PageSize = 10;
        q.PageNumber = 2;
        q.PagingCookie = new PagingCookie(1, "pb_projectid", new Guid("33333333-3333-3333-3333-333333333333"),
            new Guid("44444444-4444-4444-4444-444444444444")).Format();
        Matches(Compile(q));
    }

    [Fact]
    public void Outer_link_entity()
    {
        var q = Query("pb_project", "pb_name");
        var link = new LinkNode
        {
            EntityName = "systemuser", From = "systemuserid", To = "pb_customerid", JoinType = JoinKind.LeftOuter, Alias = "u",
        };
        link.Columns.Add(new ColumnNode("fullname"));
        link.Filter.Conditions.Add(new ConditionNode(null, "fullname", FilterOperator.BeginsWith, ["Ad"]));
        q.Root.Links.Add(link);
        Matches(Compile(q));
    }

    [Fact]
    public void Aggregate_grouped_by_month_and_week()
    {
        var q = Query("pb_project");
        q.Aggregate = true;
        q.Root.Columns.Add(new ColumnNode("pb_startdate", "m", GroupBy: true, DateGrouping: DateGrouping.Month));
        q.Root.Columns.Add(new ColumnNode("pb_startdate", "w", GroupBy: true, DateGrouping: DateGrouping.Week));
        q.Root.Columns.Add(new ColumnNode("pb_projectid", "n", XrmAggregate.Count));
        q.Root.Columns.Add(new ColumnNode("pb_count", "total", XrmAggregate.Sum));
        Matches(Compile(q));
    }

    /// <summary>Plan 2.7b, ruling Q1 (#299): text equality, <c>in</c> and a join on text compare text keys.</summary>
    private static QueryModel TextKeyFilterModel()
    {
        var q = Query("pb_project", "pb_name");
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_name", FilterOperator.Equal, ["Acme "]));
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_name", FilterOperator.NotEqual, ["Beta"]));
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_name", FilterOperator.In, ["a", "b"]));
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_name", FilterOperator.In, Enumerable.Range(0, 101).Select(i => (object?)("v" + i)).ToList()));
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_count", FilterOperator.Equal, [3]));
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_name", FilterOperator.BeginsWith, ["Ac"]));
        var link = new LinkNode { EntityName = "xrmv_calcorder", From = "xrmv_name", To = "pb_name", JoinType = JoinKind.Inner, Alias = "o" };
        q.Root.Links.Add(link);
        return q;
    }

    /// <summary>Plan 2.7b, ruling Q1: distinct and group-by on text compare text keys.</summary>
    private static (QueryModel Distinct, QueryModel Grouped) TextKeyGroupModels()
    {
        var distinct = Query("pb_project", "pb_name", "pb_count");
        distinct.Distinct = true;
        distinct.Root.Orders.Add(new OrderNode("pb_name", false));
        var grouped = Query("pb_project");
        grouped.Aggregate = true;
        grouped.Root.Columns.Add(new ColumnNode("pb_name", "n", GroupBy: true));
        grouped.Root.Columns.Add(new ColumnNode("pb_name", "d", XrmAggregate.CountColumn, Distinct: true));
        grouped.Root.Columns.Add(new ColumnNode("pb_projectid", "c", XrmAggregate.Count));
        grouped.Root.Orders.Add(new OrderNode("pb_name", false));
        return (distinct, grouped);
    }

    [Fact]
    public void Text_keys_in_conditions_and_joins() => Matches(Compile(TextKeyFilterModel()));

    [Fact]
    public void Text_keys_in_distinct() => Matches(Compile(TextKeyGroupModels().Distinct));

    [Fact]
    public void Text_keys_in_group_by() => Matches(Compile(TextKeyGroupModels().Grouped));

    [Fact]
    public void Text_keys_leave_the_sql_server_text_as_it_was()
    {
        // TextKey is the expression itself on SQL Server: the columns compare as before.
        var sqlServer = new SqlQueryCompiler(TestMetadata.Instance, Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance, _computed);
        var filter = sqlServer.Compile(TextKeyFilterModel(), Context).Sql;
        filter.Should().Contain("t0.[pb_name] = @p0").And.Contain("t0.[pb_name] <> @p1").And.Contain("t0.[pb_name] IN (@p2, @p3)")
            .And.Contain("t0.[pb_name] IN (SELECT value FROM OPENJSON(@p4)").And.Contain("INNER JOIN [data].[xrmv_calcorder] AS t1 ON t1.[xrmv_name] = t0.[pb_name]")
            .And.NotContain("rtrim");
        var (distinct, grouped) = TextKeyGroupModels();
        sqlServer.Compile(distinct, Context).Sql.Should().Contain("SELECT DISTINCT TOP (@top) t0.[pb_name] AS c0").And.Contain("ORDER BY t0.[pb_name] ASC");
        sqlServer.Compile(grouped, Context).Sql.Should().Contain("GROUP BY t0.[pb_name]").And.Contain("COUNT(DISTINCT t0.[pb_name])");
    }

    [Fact]
    public void Total_record_count()
    {
        var q = Query("pb_project", "pb_name");
        q.ReturnTotalRecordCount = true;
        q.PageSize = 25;
        Matches(Compile(q));
    }

    [Fact]
    public void Relative_date_operators()
    {
        var q = Query("pb_project", "pb_name");
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_startdate", FilterOperator.LastXDays, [7]));
        q.Root.Filter.Conditions.Add(new ConditionNode(null, "pb_startdate", FilterOperator.ThisMonth, []));
        Matches(Compile(q));
    }

    [Fact]
    public void Calculated_column()
    {
        _computed.Set("xrmv_calcorder", "xrmv_total", new Binary("Multiply", Own("xrmv_amount"), Own("xrmv_quantity")));
        Matches(Compile(Query("xrmv_calcorder", "xrmv_total")));
    }

    [Fact]
    public void Formula_text_of_a_number()
    {
        _computed.Set("xrmv_fxorder", "xrmv_fxtext", Fx("Text", Own("xrmv_qty"), new Literal("0")));
        Matches(Compile(Query("xrmv_fxorder", "xrmv_fxtext")));
    }

    [Fact]
    public void Formula_string_functions_and_rounding()
    {
        _computed.Set("xrmv_fxorder", "xrmv_fxtext",
            Fx("Concat", Fx("Upper", Fx("Left", Own("xrmv_code"), new Literal(2m))), Fx("Substitute", Own("xrmv_code"), new Literal("a"), new Literal("b"), new Literal(2m))));
        _computed.Set("xrmv_fxorder", "xrmv_fxdec", Fx("Round", Own("xrmv_amount"), new Literal(1m)));
        _computed.Set("xrmv_fxorder", "xrmv_fxint", Fx("Len", Own("xrmv_code")));
        Matches(Compile(Query("xrmv_fxorder", "xrmv_fxtext", "xrmv_fxdec", "xrmv_fxint")));
    }

    [Fact]
    public void Rollup_sum_of_related_rows()
    {
        var definition = new RollupDefinition(RollupAggregate.Sum, "xrmv_amount",
            new RollupRelated("xrmv_rollorder_acct", "xrmv_rollorder", "xrmv_acctid",
                new ComputedCondition.Compare("Equal", Own("xrmv_status"), new Literal(2))),
            null, null, [new RollupSource("xrmv_rollorder", "xrmv_amount"), new RollupSource("xrmv_rollorder", "xrmv_acctid")]);
        Matches(Rollup("xrmv_total", definition));
    }

    [Fact]
    public void Rollup_average_over_the_hierarchy()
    {
        var definition = new RollupDefinition(RollupAggregate.Avg, "xrmv_budget", null, "xrmv_rollacct_parent", null,
            [new RollupSource("xrmv_rollacct", "xrmv_budget")]);
        Matches(Rollup("xrmv_tree", definition));
    }

    [Fact]
    public void Change_tracking_window()
    {
        var q = Query("pb_project", "pb_name");
        q.Root.Filter.Versions.Add(new VersionRangeNode(100, 250));
        Matches(Compile(q));
    }

    private RollupCommand Rollup(string column, RollupDefinition definition)
    {
        var table = TestMetadata.Instance.GetEntity("xrmv_rollacct")!;
        return new RollupSql(TestMetadata.Instance, Pg, _computed)
            .Recalculate(table, TestMetadata.Attribute("xrmv_rollacct", column), definition, new RollupScope.All(), Now);
    }

    private static void Matches(CompiledQuery c, [CallerMemberName] string name = "", [CallerFilePath] string file = "") =>
        Matches(c.Sql, c.Parameters, name, file);

    private static void Matches(RollupCommand c, [CallerMemberName] string name = "", [CallerFilePath] string file = "") =>
        Matches(c.Sql, c.Parameters, name, file);

    private static void Matches(string sql, IEnumerable<DbParameter> parameters, string name, string file)
    {
        var text = new StringBuilder(sql.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd()).Append("\n\n-- parameters\n");
        foreach (var p in parameters)
            text.Append(CultureInfo.InvariantCulture, $"-- {p.ParameterName} {p.DbType} = {Format(p.Value)}\n");
        var path = Path.Combine(Path.GetDirectoryName(file)!, "Snapshots", "pg", name + ".sql");
        if (Environment.GetEnvironmentVariable("XRMD_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.ToString());
        }
        File.Exists(path).Should().BeTrue($"the snapshot {path} is checked in (XRMD_UPDATE_SNAPSHOTS=1 writes it)");
        text.ToString().Should().Be(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), $"the snapshot {name}.sql");
    }

    private static string Format(object? value) => value switch
    {
        null or DBNull => "NULL",
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
