using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Computed;
using Xrmd.Query.Sql;
using static Xrmd.Metadata.Computed.ComputedExpr;

namespace Xrmd.Query.Tests;

/// <summary>
/// The SQL of a rollup recalculation (plan 2.12b, Task 3; rulings R6, R7, R8): one statement per rollup and scope, which aggregates
/// the related rows (or the rows of the hierarchy) of each target row, gives each target its state, and updates the rows whose value
/// or state changes. The tables are <c>xrmv_rollacct</c> (hierarchical, the rollups) and <c>xrmv_rollorder</c> (the related rows) of
/// <see cref="TestMetadata"/>; the definitions are written here in the shape the XAML translator gives.
/// </summary>
public sealed class RollupSqlTests
{
    private const string Account = "xrmv_rollacct", Order = "xrmv_rollorder";
    private static readonly DateTime Now = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);

    private readonly Definitions _computed = new();

    private sealed class Definitions : IComputedColumns
    {
        private readonly Dictionary<string, ComputedExpr> _bodies = new(StringComparer.Ordinal);

        public void Set(string table, string column, ComputedExpr body) => _bodies[$"{table}.{column}"] = body;

        public ComputedDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities) =>
            _bodies.TryGetValue($"{entity.LogicalName}.{attribute.LogicalName}", out var body)
                ? new ComputedDefinition(body, [], 1)
                : throw new XrmdValidationException($"No definition for '{entity.LogicalName}.{attribute.LogicalName}'.");
    }

    private static Column Own(string attribute) => new(null, attribute);

    private static RollupDefinition Related(RollupAggregate aggregate, string column, ComputedCondition? filter = null) =>
        new(aggregate, column, new RollupRelated("xrmv_rollorder_acct", Order, "xrmv_acctid", filter), null, null,
            [new RollupSource(Order, column), new RollupSource(Order, "xrmv_acctid")]);

    private static RollupDefinition Tree(RollupAggregate aggregate, string column, ComputedCondition? sourceFilter = null) =>
        new(aggregate, column, null, "xrmv_rollacct_parent", sourceFilter, [new RollupSource(Account, column)]);

    private RollupCommand Build(string rollup, RollupDefinition definition, RollupScope? scope = null)
    {
        var table = TestMetadata.Instance.GetEntity(Account)!;
        return new RollupSql(TestMetadata.Instance, TestMetadata.Dialect, _computed)
            .Recalculate(table, TestMetadata.Attribute(Account, rollup), definition, scope ?? new RollupScope.All(), Now);
    }

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static object? Parameter(RollupCommand c, string name) => c.Parameters.Single(p => p.ParameterName == name).Value;

    [Fact]
    public void Related_sum_groups_by_lookup_with_filter_parameters()
    {
        var filter = new ComputedCondition.And(
            new ComputedCondition.Compare("Equal", Own("xrmv_status"), new Literal(2)),
            new ComputedCondition.Compare("Equal", Own("xrmv_code"), new Literal("north")));

        var c = Build("xrmv_total", Related(RollupAggregate.Sum, "xrmv_amount", filter));

        var sql = Normalize(c.Sql);
        sql.Should().StartWith("WITH tg AS (SELECT t.[xrmv_rollacctid] AS id FROM [data].[xrmv_rollacct] AS t)");
        sql.Should().Contain("nodes AS (SELECT id AS root, id AS node FROM tg)");
        sql.Should().Contain("FROM nodes AS n JOIN [data].[xrmv_rollorder] AS r ON r.[xrmv_acctid] = n.node");
        sql.Should().Contain("WHERE ((r.[xrmv_status] = 2 AND r.[xrmv_code] = @p0)) GROUP BY n.root");
        Parameter(c, "@p0").Should().Be("north");
        sql.Should().Contain("UPDATE t SET t.[xrmv_total] = res.value, t.[xrmv_total_date] = @now, t.[xrmv_total_state] = res.state");
        sql.Should().Contain("OUTPUT inserted.[xrmv_rollacctid], inserted.[xrmv_total], CAST(NULL AS decimal(19, 4)), inserted.[xrmv_total_date], inserted.[xrmv_total_state]");
        sql.Should().Contain("FROM [data].[xrmv_rollacct] AS t JOIN res ON res.id = t.[xrmv_rollacctid]");
        Parameter(c, "@now").Should().Be(Now);
        // The empty set sums to 0 (R7); a value that does not fit the column is an overflow (state 2, no value).
        sql.Should().Contain("CAST(COALESCE(agg.value, 0) AS decimal(38, 2)) AS v");
        sql.Should().Contain("WHEN x.v IS NOT NULL AND TRY_CAST(x.v AS decimal(23,2)) IS NULL THEN 2");
        sql.Should().Contain("CASE WHEN st.state = 1 THEN TRY_CAST(x.v AS decimal(23,2)) END AS value");
    }

    [Fact]
    public void Values_truncated_to_column_precision_before_sum()
    {
        // xrmv_amount has 4 decimals, the rollup 2: each value is cut to 2 decimals, then summed (R7, Learn's example).
        var c = Build("xrmv_total", Related(RollupAggregate.Sum, "xrmv_amount"));

        Normalize(c.Sql).Should().Contain("SUM(CAST(ROUND(r.[xrmv_amount], 2, 1) AS decimal(38, 2))) AS value");
    }

    [Fact]
    public void Count_counts_non_null_column()
    {
        var c = Build("xrmv_count", Related(RollupAggregate.Count, "xrmv_qty"));

        var sql = Normalize(c.Sql);
        sql.Should().Contain("COUNT_BIG(r.[xrmv_qty]) AS value");
        sql.Should().Contain("CAST(COALESCE(agg.value, 0) AS decimal(38, 0)) AS v");
        sql.Should().Contain("TRY_CAST(x.v AS int)");
    }

    [Fact]
    public void Average_and_min_of_whole_numbers_are_not_truncated_and_average_rounds()
    {
        var avg = Normalize(Build("xrmv_count", Related(RollupAggregate.Avg, "xrmv_qty")).Sql);
        avg.Should().Contain("AVG(CAST(COALESCE(r.[xrmv_qty], 0) AS decimal(38, 10))) AS value");
        avg.Should().Contain("CAST(ROUND(COALESCE(agg.value, 0), 0) AS decimal(38, 0)) AS v");

        var min = Normalize(Build("xrmv_count", Related(RollupAggregate.Min, "xrmv_qty")).Sql);
        min.Should().Contain("MIN(CAST(r.[xrmv_qty] AS decimal(38, 0))) AS value");
        min.Should().Contain("CAST(agg.value AS decimal(38, 0)) AS v");
    }

    [Fact]
    public void Hierarchy_uses_recursive_cte_with_100_level_guard()
    {
        var filter = new ComputedCondition.Compare("NotNull", Own("xrmv_region"), null);

        var c = Build("xrmv_tree", Tree(RollupAggregate.Sum, "xrmv_budget", filter));

        var sql = Normalize(c.Sql);
        sql.Should().Contain(
            "tree AS (SELECT tg.id AS root, tg.id AS node, 0 AS depth, 0 AS revisit FROM tg UNION ALL " +
            "SELECT tree.root, ch.[xrmv_rollacctid], tree.depth + 1, CASE WHEN ch.[xrmv_rollacctid] = tree.root THEN 1 ELSE 0 END " +
            "FROM tree JOIN [data].[xrmv_rollacct] AS ch ON ch.[xrmv_parentid] = tree.node WHERE tree.revisit = 0 AND tree.depth <= 100)");
        sql.Should().Contain("nodes AS (SELECT root, node FROM tree WHERE revisit = 0 AND depth <= 100)");
        sql.Should().Contain("walk AS (SELECT root, MAX(revisit) AS revisit, MAX(depth) AS depth FROM tree GROUP BY root)");
        // The source filter keeps the rows of the hierarchy that count; the walk goes on through the others.
        sql.Should().Contain("JOIN [data].[xrmv_rollacct] AS s ON s.[xrmv_rollacctid] = n.node");
        sql.Should().Contain("WHERE (s.[xrmv_region] IS NOT NULL) GROUP BY n.root");
        sql.Should().Contain("CASE WHEN walk.revisit = 1 THEN 6 WHEN walk.depth > 100 THEN 5 ");
        sql.Should().EndWith("OPTION (MAXRECURSION 102);");
    }

    [Fact]
    public void Currency_aggregates_base_and_multiplies_target_rate()
    {
        var c = Build("xrmv_pricetotal", Related(RollupAggregate.Sum, "xrmv_price"));

        var sql = Normalize(c.Sql);
        sql.Should().Contain("LEFT JOIN [data].[transactioncurrency] AS cur ON cur.[transactioncurrencyid] = t.[transactioncurrencyid]");
        sql.Should().Contain("COALESCE(cur.[currencyprecision], 2) AS prec");
        sql.Should().Contain("CASE WHEN t.[transactioncurrencyid] IS NULL OR t.[exchangerate] IS NULL THEN 0 ELSE 1 END AS hascurrency");
        sql.Should().Contain("SUM(CAST((CAST(r.[xrmv_price] AS decimal(38, 10)) / NULLIF(CAST(r.[exchangerate] AS decimal(38, 10)), 0)) AS decimal(38, 10))) AS value");
        sql.Should().Contain("CROSS APPLY (SELECT CAST(COALESCE(agg.value, 0) AS decimal(38, 4)) AS b) AS xb");
        // A base too large for the conversion gives null there, and state 2 from the base range check (not an error).
        sql.Should().Contain("CROSS APPLY (SELECT TRY_CAST(xb.b AS decimal(28, 4)) * CAST(tg.rate AS decimal(28, 10)) AS v) AS x");
        sql.Should().Contain("WHEN xb.b < @basemin OR xb.b > @basemax THEN 2");
        sql.Should().Contain("WHEN tg.hascurrency = 0 THEN 7");
        sql.Should().Contain("t.[xrmv_pricetotal_base] = res.base");
        sql.Should().Contain("inserted.[xrmv_pricetotal_base]");
    }

    [Fact]
    public void Update_only_where_value_or_state_differs()
    {
        var all = Normalize(Build("xrmv_total", Related(RollupAggregate.Sum, "xrmv_amount")).Sql);
        all.Should().Contain("WHERE EXISTS (SELECT t.[xrmv_total], t.[xrmv_total_state] EXCEPT SELECT res.value, res.state)");

        var money = Normalize(Build("xrmv_pricetotal", Related(RollupAggregate.Sum, "xrmv_price")).Sql);
        money.Should().Contain(
            "WHERE EXISTS (SELECT t.[xrmv_pricetotal], t.[xrmv_pricetotal_base], t.[xrmv_pricetotal_state] EXCEPT SELECT res.value, res.base, res.state)");

        // One row (CalculateRollupField) is always written: its _date says when it was calculated.
        var id = Guid.NewGuid();
        var one = Build("xrmv_total", Related(RollupAggregate.Sum, "xrmv_amount"), new RollupScope.One(id));
        var sql = Normalize(one.Sql);
        sql.Should().StartWith("WITH tg AS (SELECT t.[xrmv_rollacctid] AS id FROM [data].[xrmv_rollacct] AS t WHERE t.[xrmv_rollacctid] = @id)");
        sql.Should().NotContain("EXISTS");
        Parameter(one, "@id").Should().Be(id);
    }

    [Fact]
    public void ChangedSince_limits_targets_to_touched_and_differing_rows()
    {
        var since = Now.AddHours(-1);

        var related = Build("xrmv_total", Related(RollupAggregate.Sum, "xrmv_amount"), new RollupScope.ChangedSince(since));
        var sql = Normalize(related.Sql);
        sql.Should().Contain(
            "touched AS (SELECT t.[xrmv_rollacctid] AS id FROM [data].[xrmv_rollacct] AS t WHERE t.[modifiedon] >= @since " +
            "UNION SELECT r.[xrmv_acctid] FROM [data].[xrmv_rollorder] AS r WHERE r.[modifiedon] >= @since AND r.[xrmv_acctid] IS NOT NULL)");
        sql.Should().Contain(
            "WHERE EXISTS (SELECT t.[xrmv_total], t.[xrmv_total_state] EXCEPT SELECT res.value, res.state) OR t.[xrmv_rollacctid] IN (SELECT id FROM touched)");
        Parameter(related, "@since").Should().Be(since);

        // Over the hierarchy, the ancestors of a touched row are touched too.
        var tree = Normalize(Build("xrmv_tree", Tree(RollupAggregate.Sum, "xrmv_budget"), new RollupScope.ChangedSince(since)).Sql);
        tree.Should().Contain(
            "up AS (SELECT id, 0 AS depth FROM changed UNION ALL SELECT p.[xrmv_parentid], up.depth + 1 FROM up " +
            "JOIN [data].[xrmv_rollacct] AS p ON p.[xrmv_rollacctid] = up.id WHERE p.[xrmv_parentid] IS NOT NULL AND up.depth < 100)");
        tree.Should().Contain("touched AS (SELECT DISTINCT id FROM up)");
    }

    [Fact]
    public void Basic_calculated_aggregated_column_inlines_its_expression()
    {
        _computed.Set(Order, "xrmv_basic", new Binary("Multiply", Own("xrmv_amount"), Own("xrmv_qty")));

        var c = Build("xrmv_total", Related(RollupAggregate.Sum, "xrmv_basic"));

        var sql = Normalize(c.Sql);
        sql.Should().NotContain("r.[xrmv_basic]", "a calculated column has no column of its own");
        // The column's own K8 cast (decimal(38, 2)), then the rollup's truncation.
        var sum = Regex.Match(sql, @"SUM\(CAST\(ROUND\(TRY_CAST\(ROUND\(\((?<body>.*?)\), 2\) AS decimal\(38, 2\)\), 2, 1\) AS decimal\(38, 2\)\)\) AS value");
        sum.Success.Should().BeTrue(sql);
        sum.Groups["body"].Value.Should().Contain("r.[xrmv_amount]").And.Contain("r.[xrmv_qty]").And.Contain(" * ");
    }

    [Fact]
    public void Measure_counts_rows_and_depth_of_one_row()
    {
        var id = Guid.NewGuid();
        var table = TestMetadata.Instance.GetEntity(Account)!;

        var c = new RollupSql(TestMetadata.Instance, TestMetadata.Dialect, _computed).Measure(table, TestMetadata.Attribute(Account, "xrmv_tree"), Tree(RollupAggregate.Sum, "xrmv_budget"), id, 10);

        var sql = Normalize(c.Sql);
        sql.Should().Contain("WHERE tree.revisit = 0 AND tree.depth <= 10)");
        sql.Should().Contain("SELECT (SELECT COUNT(*) FROM tg) AS found, COALESCE((SELECT MAX(depth) FROM tree), 0) AS depth, (SELECT COUNT_BIG(*) FROM nodes AS n");
        Parameter(c, "@id").Should().Be(id);
    }

    [Fact]
    public void Related_lookup_that_does_not_exist_is_refused_naming_the_rollup()
    {
        var definition = new RollupDefinition(RollupAggregate.Sum, "xrmv_amount", new RollupRelated("xrmv_rollorder_acct", Order, "xrmv_nope", null),
            null, null, []);

        var act = () => Build("xrmv_total", definition);

        act.Should().Throw<XrmdValidationException>().Which.Message.Should()
            .Be("The rollup column 'xrmv_rollacct.xrmv_total' follows the lookup 'xrmv_rollorder.xrmv_nope', which does not exist.");
    }

    [Theory]
    [InlineData(RollupAggregate.Sum, "xrmv_last")]
    [InlineData(RollupAggregate.Avg, "xrmv_last")]
    [InlineData(RollupAggregate.Count, "xrmv_last")]
    [InlineData(RollupAggregate.Count, "xrmv_total")]
    public void Aggregate_the_rollup_type_cannot_hold_is_refused(RollupAggregate aggregate, string rollup)
    {
        var act = () => Build(rollup, Related(aggregate, aggregate == RollupAggregate.Count ? "xrmv_qty" : "xrmv_on"));

        var fault = act.Should().Throw<Xrmd.Core.Errors.XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(unchecked((int)0x80060545));
        fault.Message.Should().Contain($"'xrmv_rollacct.{rollup}'").And.Contain(aggregate.ToString().ToUpperInvariant());
    }

    [Fact]
    public void Max_of_dates_is_written()
    {
        Normalize(Build("xrmv_last", Related(RollupAggregate.Max, "xrmv_on")).Sql).Should().Contain("MAX(r.[xrmv_on]) AS value");
    }

    [Fact]
    public void Guard_of_the_reused_calculated_sql_names_the_rollup()
    {
        var filter = new ComputedCondition.Compare("NotNull", Own("xrmv_nope"), null);

        var act = () => Build("xrmv_total", Related(RollupAggregate.Sum, "xrmv_amount", filter));

        act.Should().Throw<XrmdValidationException>().Which.Message.Should()
            .Be("The rollup column 'xrmv_rollacct.xrmv_total' reads the column 'xrmv_rollorder.xrmv_nope', which does not exist.");
    }
}
