using System.Data;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Computed;
using Xrmd.Model.Query;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;
using static Xrmd.Metadata.Computed.ComputedExpr;

namespace Xrmd.Query.Tests;

/// <summary>
/// The SQL of calculated columns (plan 2.12a, Task 3; rulings K7, K8, K10): each one is computed from its definition where the
/// query reads it, with its parent record read by an OUTER APPLY that carries no read restriction, and cast to the column's type.
/// The definitions are given per column by a test <see cref="IComputedColumns"/>, in the shapes the XAML translator produces.
/// </summary>
public sealed class ComputedSqlTests
{
    private const string Order = "xrmv_calcorder";
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc));

    private readonly Definitions _computed = new();

    /// <summary>The definition of each <c>table.column</c>, as a test sets it.</summary>
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
                ? new ComputedDefinition(body, [], 0)
                : throw new XrmdValidationException($"No definition for '{entity.LogicalName}.{attribute.LogicalName}'.");
    }

    private CompiledQuery Compile(QueryModel q, QueryContext? context = null) =>
        new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect, _computed).Compile(q, context ?? Context);

    private CompiledQuery Select(params string[] columns) => Compile(new QueryModel { Root = Root(columns) });

    private static EntityNode Root(params string[] columns)
    {
        var root = new EntityNode { EntityName = Order };
        root.Columns.AddRange(columns.Select(c => new ColumnNode(c)));
        return root;
    }

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    /// <summary>The selected expression of <paramref name="attribute"/> (its select-list item without the alias), whitespace normalized.</summary>
    private static string Selected(CompiledQuery c, string attribute)
    {
        var alias = c.Columns.Single(x => x.Kind is ResultKind.Value or ResultKind.Aggregate && x.ResultName == attribute).SqlAlias;
        var sql = c.Sql;
        var from = sql.IndexOf("\nFROM ", StringComparison.Ordinal);
        var list = Regex.Replace(sql[..from], @"^SELECT (DISTINCT )?(TOP \(@top\) )?", "");
        // The items are separated by the commas outside parentheses.
        var items = new List<string>();
        var (depth, begin) = (0, 0);
        for (var i = 0; i < list.Length; i++)
        {
            if (list[i] == '(') depth++;
            else if (list[i] == ')') depth--;
            else if (list[i] == ',' && depth == 0)
            {
                items.Add(list[begin..i]);
                begin = i + 1;
            }
        }
        items.Add(list[begin..]);
        var item = Normalize(items.Single(x => Normalize(x).EndsWith(" AS " + alias, StringComparison.Ordinal)));
        return item[..^(" AS " + alias).Length];
    }

    private static Column Own(string attribute) => new(null, attribute);

    private static Literal Int(int value) => new(value);

    // A money column (decimal(19, 4)) enters a product as is, an int column as decimal(10, 0): (30, 4) keeps every digit.
    private const string Amount = "t0.[xrmv_amount]";
    private const string Quantity = "CAST(t0.[xrmv_quantity] AS decimal(10, 0))";
    private const string TotalSql = $"TRY_CAST(ROUND(({Amount} * {Quantity}), 2) AS decimal(19, 4))";

    [Fact]
    public void Own_columns_multiply()
    {
        _computed.Set(Order, "xrmv_total", new Binary("Multiply", Own("xrmv_amount"), Own("xrmv_quantity")));

        var c = Select("xrmv_total");

        Selected(c, "xrmv_total").Should().Be(TotalSql);
        c.Sql.Should().NotContain("t0.[xrmv_total]", "a calculated column has no column");
    }

    [Fact]
    public void Parent_column_is_correlated_subquery_without_security_predicate()
    {
        _computed.Set(Order, "xrmv_label", new Function("CONCAT", [Own("xrmv_name"), new Literal(" / "), new Column("xrmv_accountid", "name")]));
        var owner = Guid.NewGuid();
        var security = new OwnerOnly(owner);

        var c = Compile(new QueryModel { Root = Root("xrmv_label") }, Context with { Security = security });

        var sql = Normalize(c.Sql);
        sql.Should().Contain("FROM [data].[xrmv_calcorder] AS t0 OUTER APPLY (SELECT * FROM [data].[account] WHERE [accountid] = t0.[xrmv_accountid]) AS c0");
        Selected(c, "xrmv_label").Should().Be("LEFT(CONCAT(t0.[xrmv_name], @p0, c0.[name]), 200)");
        // The read restriction applies to the order rows only: the parent value is read with system rights (K10).
        security.Requested.Should().Equal(Order);
        sql.Should().Contain("WHERE (t0.[ownerid] = @p1)").And.NotContain("c0.[ownerid]");
    }

    [Fact]
    public void Parent_column_of_a_link_entity_is_applied_inside_its_join()
    {
        _computed.Set(Order, "xrmv_label", new Function("CONCAT", [Own("xrmv_name"), new Column("xrmv_accountid", "name")]));
        var root = new EntityNode { EntityName = "account" };
        root.Columns.Add(new ColumnNode("name"));
        var link = new LinkNode { EntityName = Order, From = "xrmv_accountid", To = "accountid", Alias = "o", JoinType = JoinKind.LeftOuter };
        link.Columns.Add(new ColumnNode("xrmv_label"));
        link.Filter.Conditions.Add(new ConditionNode(null, "xrmv_label", FilterOperator.NotNull, []));
        root.Links.Add(link);

        var sql = Normalize(Compile(new QueryModel { Root = root }).Sql);

        sql.Should().Contain("LEFT OUTER JOIN ([data].[xrmv_calcorder] AS t1 OUTER APPLY (SELECT * FROM [data].[account] WHERE [accountid] = t1.[xrmv_accountid]) AS c0) " +
                             "ON t1.[xrmv_accountid] = t0.[accountid] AND (LEFT(CONCAT(t1.[xrmv_name], c0.[name]), 200) IS NOT NULL)");
    }

    [Fact]
    public void String_literal_is_a_parameter()
    {
        _computed.Set(Order, "xrmv_label", new Function("CONCAT", [Own("xrmv_name"), new Literal("it's")]));

        var c = Select("xrmv_label");

        c.Sql.Should().NotContain("it's");
        var p = c.Parameters.Single(x => x.ParameterName == "@p0");
        p.DbType.Should().Be(DbType.String);
        p.Value.Should().Be("it's");
    }

    [Fact]
    public void Same_column_in_select_filter_and_order_adds_its_parameters_once()
    {
        _computed.Set(Order, "xrmv_label", new Function("CONCAT", [Own("xrmv_name"), new Literal(" / "), new Column("xrmv_accountid", "name")]));
        var root = Root("xrmv_label");
        root.Filter.Conditions.Add(new ConditionNode(null, "xrmv_label", FilterOperator.Like, ["a%"]));
        root.Orders.Add(new OrderNode("xrmv_label", Descending: true));

        var c = Compile(new QueryModel { Root = root });

        var label = "LEFT(CONCAT(t0.[xrmv_name], @p0, c0.[name]), 200)";
        var sql = Normalize(c.Sql);
        sql.Should().Contain($"{label} AS c1").And.Contain($"WHERE ({label} LIKE @p1)").And.Contain($"ORDER BY {label} DESC");
        c.Parameters.Count(p => " / ".Equals(p.Value)).Should().Be(1);
        Regex.Matches(sql, "OUTER APPLY").Count.Should().Be(1, "one parent record per lookup");
    }

    [Fact]
    public void Calculated_groupby_and_aggregate_read_no_subquery_expression()
    {
        _computed.Set(Order, "xrmv_label", new Function("CONCAT", [Own("xrmv_name"), new Literal(" / "), new Column("xrmv_accountid", "name")]));
        _computed.Set(Order, "xrmv_total", new Binary("Multiply", Own("xrmv_amount"), Own("xrmv_quantity")));
        var root = new EntityNode { EntityName = Order };
        root.Columns.Add(new ColumnNode("xrmv_label", "label", GroupBy: true));
        root.Columns.Add(new ColumnNode("xrmv_total", "sum", XrmAggregate.Sum));
        root.Orders.Add(new OrderNode("xrmv_label", false));

        var c = Compile(new QueryModel { Root = root, Aggregate = true });

        var label = "LEFT(CONCAT(t0.[xrmv_name], @p0, c0.[name]), 200)";
        var sql = Normalize(c.Sql);
        sql.Should().Contain($"GROUP BY {label}").And.Contain($"SUM({TotalSql}) AS c1").And.Contain($"ORDER BY {label} ASC");
        c.Parameters.Count(p => " / ".Equals(p.Value)).Should().Be(1);
    }

    [Fact]
    public void Conditional_is_case_when()
    {
        var date = Own("xrmv_orderdate");
        _computed.Set(Order, "xrmv_followup", new Conditional(
            [
                new ComputedBranch(new ComputedCondition.Compare("GreaterThan", Own("xrmv_amount"), new Literal(1000m)), new Function("ADDDAYS", [Int(2), date])),
                new ComputedBranch(new ComputedCondition.Compare("GreaterThan", Own("xrmv_amount"), new Literal(100m)), new Function("ADDWEEKS", [Int(1), date])),
            ],
            new Function("ADDMONTHS", [Int(1), date])));
        _computed.Set(Order, "xrmv_isbig", new Conditional(
            [
                new ComputedBranch(new ComputedCondition.Or(
                    new ComputedCondition.And(
                        new ComputedCondition.Compare("GreaterThan", Own("xrmv_amount"), new Literal(1000m)),
                        new ComputedCondition.Compare("GreaterEqual", Own("xrmv_quantity"), Int(10))),
                    new ComputedCondition.Not(new ComputedCondition.Compare("NotEqual", Own("xrmv_rush"), new Literal(true)))), new Literal(true)),
            ],
            new Literal(false)));
        _computed.Set(Order, "xrmv_tier", new Conditional(
            [new ComputedBranch(new ComputedCondition.Compare("Equal", Own("xrmv_status"), Int(100000001)), Int(100000002))], null));

        var c = Select("xrmv_followup", "xrmv_isbig", "xrmv_tier");

        Selected(c, "xrmv_followup").Should().Be(
            "CAST(CASE WHEN t0.[xrmv_amount] > CAST(1000 AS decimal(4, 0)) THEN DATEADD(day, 2, t0.[xrmv_orderdate]) " +
            "WHEN t0.[xrmv_amount] > CAST(100 AS decimal(3, 0)) THEN DATEADD(week, 1, t0.[xrmv_orderdate]) " +
            "ELSE DATEADD(month, 1, t0.[xrmv_orderdate]) END AS datetime2)");
        Selected(c, "xrmv_isbig").Should().Be(
            "CAST(CASE WHEN ((t0.[xrmv_amount] > CAST(1000 AS decimal(4, 0)) AND t0.[xrmv_quantity] >= 10) " +
            "OR (NOT (t0.[xrmv_rush] <> CAST(1 AS bit)))) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS bit)");
        Selected(c, "xrmv_tier").Should().Be("TRY_CAST(CASE WHEN t0.[xrmv_status] = 100000001 THEN 100000002 END AS int)");
    }

    [Fact]
    public void Text_conditions_use_like_patterns()
    {
        _computed.Set(Order, "xrmv_tier", new Conditional(
            [
                new ComputedBranch(new ComputedCondition.Compare("Contains", Own("xrmv_name"), new Literal("50%")), Int(1)),
                new ComputedBranch(new ComputedCondition.Compare("BeginsWith", Own("xrmv_name"), new Column("xrmv_accountid", "name")), Int(2)),
                new ComputedBranch(new ComputedCondition.Compare("DoesNotContain", Own("xrmv_name"), new Literal("x")), Int(3)),
                new ComputedBranch(new ComputedCondition.Compare("Null", Own("xrmv_name"), null), Int(4)),
            ],
            Int(5)));

        var c = Select("xrmv_tier");

        Selected(c, "xrmv_tier").Should().Be(
            "TRY_CAST(CASE WHEN t0.[xrmv_name] LIKE @p0 " +
            "THEN 1 WHEN t0.[xrmv_name] LIKE CONCAT(REPLACE(REPLACE(REPLACE(c0.[name], N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]'), N'%') " +
            "THEN 2 WHEN t0.[xrmv_name] NOT LIKE @p1 THEN 3 WHEN t0.[xrmv_name] IS NULL THEN 4 ELSE 5 END AS int)");
        c.Parameters[0].Value.Should().Be("%50[%]%");
        c.Parameters[1].Value.Should().Be("%x%");
    }

    [Fact]
    public void Add_and_diff_functions_map_to_dateadd_datediff()
    {
        _computed.Set(Order, "xrmv_age", new Function("DIFFINDAYS", [Own("xrmv_orderdate"), new Now()]));
        _computed.Set(Order, "xrmv_followup", new Function("SUBTRACTHOURS", [Own("xrmv_quantity"), new Function("ADDYEARS", [Int(1), new Now()])]));
        _computed.Set(Order, "xrmv_tier", new Function("DIFFINMINUTES", [Own("xrmv_orderdate"), new Function("ADDHOURS", [Int(3), Own("xrmv_orderdate")])]));

        var c = Select("xrmv_age", "xrmv_followup", "xrmv_tier");

        Selected(c, "xrmv_age").Should().Be("TRY_CAST(DATEDIFF(day, t0.[xrmv_orderdate], SYSUTCDATETIME()) AS int)");
        Selected(c, "xrmv_followup").Should().Be("CAST(DATEADD(hour, -(t0.[xrmv_quantity]), DATEADD(year, 1, SYSUTCDATETIME())) AS datetime2)");
        Selected(c, "xrmv_tier").Should().Be("TRY_CAST(DATEDIFF(minute, t0.[xrmv_orderdate], DATEADD(hour, 3, t0.[xrmv_orderdate])) AS int)");
    }

    [Fact]
    public void Trim_functions_map_to_substring_and_left()
    {
        _computed.Set(Order, "xrmv_code", new Function("TRIMLEFT", [new Function("TRIMRIGHT", [Own("xrmv_name"), Int(2)]), Int(3)]));

        var c = Select("xrmv_code");

        const string name = "t0.[xrmv_name]";
        const string right = $"CASE WHEN {name} IS NULL THEN NULL WHEN LEN(CONCAT({name}, N'.')) - 1 <= 2 THEN N'' " +
                             $"ELSE LEFT({name}, LEN(CONCAT({name}, N'.')) - 1 - 2) END";
        Selected(c, "xrmv_code").Should().Be($"LEFT(SUBSTRING({right}, 3 + 1, 4000), 100)");
    }

    [Fact]
    public void Divide_uses_nullif()
    {
        _computed.Set(Order, "xrmv_total", new Binary("Multiply", Own("xrmv_amount"), Own("xrmv_quantity")));
        _computed.Set(Order, "xrmv_double", new Binary("Divide", Own("xrmv_total"), new Literal(2m)));
        _computed.Set(Order, "xrmv_age", new Binary("Divide", Own("xrmv_quantity"), Int(4)));

        var c = Select("xrmv_double", "xrmv_age");

        // A chained calculated column enters as its own (cast) value.
        Selected(c, "xrmv_double").Should().Be(
            $"TRY_CAST(ROUND((CAST({TotalSql} AS decimal(28, 10)) / NULLIF(CAST(2 AS decimal(28, 10)), 0)), 2) AS decimal(38, 2))");
        // Whole numbers are divided as decimals, never with integer division.
        Selected(c, "xrmv_age").Should().Be("TRY_CAST((CAST(t0.[xrmv_quantity] AS decimal(28, 10)) / NULLIF(CAST(4 AS decimal(28, 10)), 0)) AS int)");
    }

    [Fact]
    public void Division_keeps_10_decimals_for_a_precision_10_decimal()
    {
        // 1 / 3 into a Decimal of Precision 10: decimal(28, 10) / decimal(28, 10) is decimal(38, 10) (SQL Server keeps 10 decimals),
        // where decimal(38, 10) operands would give decimal(38, 6) and 0.3333330000.
        _computed.Set(Order, "xrmv_ratio", new Binary("Divide", Own("xrmv_quantity"), Int(3)));

        var c = Select("xrmv_ratio");

        Selected(c, "xrmv_ratio").Should().Be(
            "TRY_CAST(ROUND((CAST(t0.[xrmv_quantity] AS decimal(28, 10)) / NULLIF(CAST(3 AS decimal(28, 10)), 0)), 10) AS decimal(38, 10))");
    }

    [Fact]
    public void Operand_that_may_not_fit_is_try_cast()
    {
        // xrmv_double is a decimal(38, 2): the product of two would need 77 digits, each operand is narrowed with TRY_CAST, so that
        // a value too large gives null (K8) instead of failing the query (error 8115).
        _computed.Set(Order, "xrmv_double", Own("xrmv_amount"));
        _computed.Set(Order, "xrmv_ratio", new Binary("Multiply", Own("xrmv_double"), Own("xrmv_double")));
        _computed.Set(Order, "xrmv_age", new Binary("Add", Own("xrmv_double"), new Literal(0.5m)));

        var c = Select("xrmv_ratio", "xrmv_age");

        const string d = "TRY_CAST(ROUND(t0.[xrmv_amount], 2) AS decimal(38, 2))";
        Selected(c, "xrmv_ratio").Should().Be(
            $"TRY_CAST(ROUND((TRY_CAST({d} AS decimal(19, 2)) * TRY_CAST({d} AS decimal(18, 2))), 10) AS decimal(38, 10))");
        // A sum of 2 decimals keeps 35 integer digits: the decimal(38, 2) is narrowed to (37, 2), the constant keeps its own type.
        Selected(c, "xrmv_age").Should().Be($"TRY_CAST((TRY_CAST({d} AS decimal(37, 2)) + CAST(0.5 AS decimal(1, 1))) AS int)");
    }

    [Fact]
    public void Money_base_columns_are_read_from_their_column()
    {
        // The base value of the row's money and of the parent's: the stored _base columns, as is (both in the base currency).
        _computed.Set(Order, "xrmv_total", new Binary("Multiply", Own("xrmv_amount_base"), Own("xrmv_quantity")));
        _computed.Set(Order, "xrmv_fromparent", new Column("xrmv_parentid", "xrmv_credit_base"));

        var c = Select("xrmv_total", "xrmv_fromparent");

        Selected(c, "xrmv_total").Should().Be($"TRY_CAST(ROUND((t0.[xrmv_amount_base] * {Quantity}), 2) AS decimal(19, 4))");
        Selected(c, "xrmv_fromparent").Should().Be("TRY_CAST(ROUND(c0.[xrmv_credit_base], 2) AS decimal(19, 4))");
    }

    [Fact]
    public void Result_cast_per_type()
    {
        _computed.Set(Order, "xrmv_label", Own("xrmv_name"));
        _computed.Set(Order, "xrmv_age", Own("xrmv_amount"));
        _computed.Set(Order, "xrmv_double", Own("xrmv_amount"));
        _computed.Set(Order, "xrmv_total", Own("xrmv_amount"));
        _computed.Set(Order, "xrmv_totalcur", Own("xrmv_amount"));
        _computed.Set(Order, "xrmv_totalpricing", Own("xrmv_amount"));
        _computed.Set(Order, "xrmv_isbig", Own("xrmv_rush"));
        _computed.Set(Order, "xrmv_tier", Int(100000000));
        _computed.Set(Order, "xrmv_followup", Own("xrmv_orderdate"));
        _computed.Set(Order, "xrmv_shipday", Own("xrmv_orderdate"));

        var c = Select("xrmv_label", "xrmv_age", "xrmv_double", "xrmv_total", "xrmv_totalcur", "xrmv_totalpricing", "xrmv_isbig", "xrmv_tier",
            "xrmv_followup", "xrmv_shipday");

        Selected(c, "xrmv_label").Should().Be("LEFT(t0.[xrmv_name], 200)");
        Selected(c, "xrmv_age").Should().Be("TRY_CAST(t0.[xrmv_amount] AS int)");
        Selected(c, "xrmv_double").Should().Be("TRY_CAST(ROUND(t0.[xrmv_amount], 2) AS decimal(38, 2))");
        Selected(c, "xrmv_total").Should().Be("TRY_CAST(ROUND(t0.[xrmv_amount], 2) AS decimal(19, 4))");
        // PrecisionSource 2: the precision of the record's currency; 1: the organization's pricing precision.
        Selected(c, "xrmv_totalcur").Should().Be("TRY_CAST(ROUND(t0.[xrmv_amount], COALESCE(c0.[currencyprecision], 2)) AS decimal(19, 4))");
        Selected(c, "xrmv_totalpricing").Should().Be("TRY_CAST(ROUND(t0.[xrmv_amount], COALESCE(c1.[pricingdecimalprecision], 2)) AS decimal(19, 4))");
        Selected(c, "xrmv_isbig").Should().Be("CAST(t0.[xrmv_rush] AS bit)");
        Selected(c, "xrmv_tier").Should().Be("TRY_CAST(100000000 AS int)");
        Selected(c, "xrmv_followup").Should().Be("CAST(t0.[xrmv_orderdate] AS datetime2)");
        Selected(c, "xrmv_shipday").Should().Be("CAST(t0.[xrmv_orderdate] AS date)");
        var sql = Normalize(c.Sql);
        sql.Should().Contain("OUTER APPLY (SELECT [currencyprecision] FROM [data].[transactioncurrency] WHERE [transactioncurrencyid] = t0.[transactioncurrencyid]) AS c0")
            .And.Contain("OUTER APPLY (SELECT TOP (1) [pricingdecimalprecision] FROM [data].[organization]) AS c1");
    }

    [Fact]
    public void Money_base_divides_by_exchangerate()
    {
        _computed.Set(Order, "xrmv_total", new Binary("Multiply", Own("xrmv_amount"), Own("xrmv_quantity")));

        var c = Select("xrmv_total_base");

        Selected(c, "xrmv_total_base").Should().Be($"TRY_CAST(ROUND({TotalSql} / NULLIF(t0.[exchangerate], 0), 4) AS decimal(19, 4))");
    }

    [Fact]
    public void Parent_money_enters_as_base_times_rate()
    {
        _computed.Set(Order, "xrmv_fromparent", new Binary("Add", new Column("xrmv_parentid", "xrmv_credit"), new Column("xrmv_parentid", "xrmv_pcalc")));
        _computed.Set("xrmv_calcparent", "xrmv_pcalc", new Binary("Multiply", Own("xrmv_credit"), Int(2)));

        var c = Select("xrmv_fromparent");

        const string parentCalc = "TRY_CAST(ROUND((c0.[xrmv_credit] * CAST(2 AS decimal(1, 0))), 2) AS decimal(19, 4))";
        Selected(c, "xrmv_fromparent").Should().Be(
            "TRY_CAST(ROUND((TRY_CAST((c0.[xrmv_credit_base] * t0.[exchangerate]) AS decimal(28, 10)) + " +
            $"TRY_CAST((TRY_CAST(ROUND({parentCalc} / NULLIF(c0.[exchangerate], 0), 4) AS decimal(19, 4)) * t0.[exchangerate]) AS decimal(28, 10))), 2) AS decimal(19, 4))");
        Normalize(c.Sql).Should().Contain("OUTER APPLY (SELECT * FROM [data].[xrmv_calcparent] WHERE [xrmv_calcparentid] = t0.[xrmv_parentid]) AS c0");
    }

    [Fact]
    public void All_columns_include_calculated_columns()
    {
        _computed.Set(Order, "xrmv_total", Own("xrmv_amount"));
        foreach (var name in new[] { "xrmv_totalcur", "xrmv_totalpricing", "xrmv_fromparent" }) _computed.Set(Order, name, Own("xrmv_amount"));
        foreach (var name in new[] { "xrmv_label", "xrmv_code" }) _computed.Set(Order, name, Own("xrmv_name"));
        foreach (var name in new[] { "xrmv_followup", "xrmv_shipday" }) _computed.Set(Order, name, Own("xrmv_orderdate"));
        _computed.Set(Order, "xrmv_age", Own("xrmv_quantity"));
        _computed.Set(Order, "xrmv_isbig", Own("xrmv_rush"));
        _computed.Set(Order, "xrmv_tier", Own("xrmv_status"));
        _computed.Set(Order, "xrmv_double", Own("xrmv_amount"));
        _computed.Set(Order, "xrmv_ratio", Own("xrmv_amount"));

        var c = Compile(new QueryModel { Root = new EntityNode { EntityName = Order, AllColumns = true } });

        c.Columns.Where(x => x.Kind == ResultKind.Value).Select(x => x.ResultName).Should().Contain(["xrmv_total", "xrmv_total_base", "xrmv_label", "xrmv_isbig"]);
        Selected(c, "xrmv_total").Should().Be("TRY_CAST(ROUND(t0.[xrmv_amount], 2) AS decimal(19, 4))");
    }

    [Fact]
    public void Without_a_translator_calculated_columns_are_not_readable()
    {
        var act = () => new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(new QueryModel { Root = Root("xrmv_total") }, Context);
        act.Should().Throw<XrmdValidationException>().WithMessage("*doesn't contain attribute*xrmv_total*");

        var all = new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(new QueryModel { Root = new EntityNode { EntityName = Order, AllColumns = true } }, Context);
        all.Columns.Select(x => x.ResultName).Should().NotContain(["xrmv_total", "xrmv_total_base"]);
    }

    [Fact]
    public void More_than_50_calculated_columns_is_refused()
    {
        for (var i = 1; i <= 51; i++) _computed.Set("xrmv_many", $"xrmv_m{i}", Int(i));
        var fifty = new EntityNode { EntityName = "xrmv_many" };
        fifty.Columns.AddRange(Enumerable.Range(1, 50).Select(i => new ColumnNode($"xrmv_m{i}")));
        // The same column again (a filter, an order) is not another one.
        fifty.Orders.Add(new OrderNode("xrmv_m1", false));
        Compile(new QueryModel { Root = fifty }).Columns.Should().HaveCount(51);

        var act = () => Compile(new QueryModel { Root = new EntityNode { EntityName = "xrmv_many", AllColumns = true } });

        act.Should().Throw<XrmdValidationException>().WithMessage("*more than 50 calculated columns*'xrmv_many.xrmv_m51'*");
    }

    /// <summary>A read restriction to the records the user owns, on every table; records the tables it was asked for.</summary>
    private sealed class OwnerOnly(Guid owner) : IQuerySecurity
    {
        public List<string> Requested { get; } = [];

        public FilterNode? ReadFilter(EntityMetadata entity)
        {
            Requested.Add(entity.LogicalName);
            var filter = new FilterNode();
            filter.Conditions.Add(new ConditionNode(null, "ownerid", FilterOperator.Equal, [owner]));
            return filter;
        }

        public IReadOnlySet<string> UnreadableColumns(EntityMetadata entity) => new HashSet<string>();
    }
}
