using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;
using static Xrmd.Metadata.Computed.ComputedExpr;

namespace Xrmd.Query.Tests;

/// <summary>
/// The SQL of formula columns (plan 2.12c, Task 3): the <c>fx.*</c> functions of the Power Fx translator written in T-SQL with Power Fx
/// semantics (blank number 0, blank text "", Mod with the divisor's sign, Round half away from zero, Weekday independent of
/// <c>@@DATEFIRST</c>, no <c>FORMAT()</c>), and the range of decimal intermediates (±100,000,000,000). The definitions are given per
/// column by a test <see cref="IComputedColumns"/>, in the shapes <c>FormulaTranslator</c> produces. A value used more than once is
/// computed once by an <c>OUTER APPLY (SELECT MAX(x.v) AS [v] FROM (VALUES (…)) AS x(v))</c> of the row (<c>cN.[v]</c>).
/// </summary>
public sealed class FormulaSqlTests
{
    private const string Order = "xrmv_fxorder";
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc));

    private readonly Definitions _computed = new();

    private sealed class Definitions : IComputedColumns
    {
        private readonly Dictionary<string, ComputedExpr> _bodies = new(StringComparer.Ordinal);

        public Definitions Set(string column, ComputedExpr body)
        {
            _bodies[column] = body;
            return this;
        }

        public ComputedDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities) =>
            _bodies.TryGetValue(attribute.LogicalName, out var body)
                ? new ComputedDefinition(body, [], 0)
                : throw new XrmdValidationException($"No definition for '{entity.LogicalName}.{attribute.LogicalName}'.");
    }

    private CompiledQuery Select(params string[] columns)
    {
        var root = new EntityNode { EntityName = Order };
        root.Columns.AddRange(columns.Select(c => new ColumnNode(c)));
        return new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect, _computed).Compile(new QueryModel { Root = root }, Context);
    }

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    /// <summary>The selected expression of <paramref name="column"/>, without its alias, whitespace normalized.</summary>
    private static string Selected(CompiledQuery c, string column)
    {
        var alias = c.Columns.Single(x => x.Kind == ResultKind.Value && x.ResultName == column).SqlAlias;
        var sql = c.Sql;
        var list = Regex.Replace(sql[..sql.IndexOf("\nFROM ", StringComparison.Ordinal)], @"^SELECT (DISTINCT )?(TOP \(@top\) )?", "");
        // The items are separated by the commas outside parentheses and quotes.
        var items = new List<string>();
        var (depth, begin, quoted) = (0, 0, false);
        for (var i = 0; i < list.Length; i++)
        {
            if (list[i] == '\'') quoted = !quoted;
            if (quoted) continue;
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

    /// <summary>The <c>FROM</c> clause, with the applies of the row.</summary>
    private static string From(CompiledQuery c)
    {
        var sql = Normalize(c.Sql);
        var from = sql.IndexOf(" FROM ", StringComparison.Ordinal);
        var where = sql.IndexOf(" WHERE ", from, StringComparison.Ordinal);
        return where < 0 ? sql[(from + 1)..] : sql[(from + 1)..where];
    }

    /// <summary>The values of the query's parameters, without the row count (<c>@top</c>).</summary>
    private static List<object?> Values(CompiledQuery c) => c.Parameters.Where(p => p.ParameterName != "@top").Select(p => (object?)p.Value).ToList();

    private static Column Own(string attribute) => new(null, attribute);

    private static Function Fx(string name, params ComputedExpr[] args) => new("fx." + name, args);

    private static Literal Dec(decimal value) => new(value);

    private static Literal Text(string value) => new(value);

    private static Function Zero(string column) => Fx("Zero", Own(column));

    private static Function Empty(string column) => Fx("Empty", Own(column));

    /// <summary>The value of the text formula column, inside its <c>LEFT(…, 4000)</c>.</summary>
    private string TextValue(ComputedExpr body, out CompiledQuery c)
    {
        _computed.Set("xrmv_fxtext", body);
        c = Select("xrmv_fxtext");
        var sql = Selected(c, "xrmv_fxtext");
        sql.Should().StartWith("LEFT(").And.EndWith(", 4000)");
        return sql["LEFT(".Length..^", 4000)".Length];
    }

    /// <summary>The value of the whole-number formula column, inside its <c>TRY_CAST(… AS int)</c>.</summary>
    private string IntValue(ComputedExpr body, out CompiledQuery c)
    {
        _computed.Set("xrmv_fxint", body);
        c = Select("xrmv_fxint");
        var sql = Selected(c, "xrmv_fxint");
        sql.Should().StartWith("TRY_CAST(").And.EndWith(" AS int)");
        return sql["TRY_CAST(".Length..^" AS int)".Length];
    }

    // ---- blank values (F5) ---------------------------------------------------------------------------------------------

    [Fact]
    public void Zero_is_coalesce()
    {
        IntValue(Zero("xrmv_qty"), out _).Should().Be("COALESCE(t0.[xrmv_qty], 0)");
        TextValue(Fx("Concat", Empty("xrmv_code"), Text("x")), out var c).Should().Be("CONCAT(COALESCE(t0.[xrmv_code], N''), @p0)");
        Values(c).Single().Should().Be("x");
    }

    [Fact]
    public void IsBlank_of_text_counts_a_space_as_text()
    {
        // IsBlank(xrmv_code) is Or(Null, Equal x ""): N' ' = N'' is true in SQL Server (trailing spaces), false in Power Fx.
        _computed.Set("xrmv_fxbool", new Conditional(
            [new ComputedBranch(new ComputedCondition.Or(
                new ComputedCondition.Compare("Null", Own("xrmv_code"), null),
                new ComputedCondition.Compare("Equal", Own("xrmv_code"), Text(""))), new Literal(true))],
            new Literal(false)));

        var sql = Selected(Select("xrmv_fxbool"), "xrmv_fxbool");

        sql.Should().Be("CAST(CASE WHEN (t0.[xrmv_code] IS NULL OR DATALENGTH(t0.[xrmv_code]) = 0) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS bit)");
    }

    // ---- numbers ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Decimal_intermediates_out_of_range_are_null()
    {
        _computed.Set("xrmv_fxdec", new Binary("Multiply", Zero("xrmv_amount"), Dec(1000m)));

        var c = Select("xrmv_fxdec");

        From(c).Should().Contain("OUTER APPLY (SELECT MAX(x.v) AS [v] FROM (VALUES ((COALESCE(t0.[xrmv_amount], 0) * CAST(1000 AS decimal(4, 0))))) AS x(v)) AS c0");
        Selected(c, "xrmv_fxdec").Should().Be(
            "TRY_CAST(ROUND(CASE WHEN ABS(c0.[v]) <= 100000000000 THEN CAST(c0.[v] AS decimal(14, 2)) END, 2) AS decimal(38, 2))");
    }

    [Fact]
    public void Round_half_away_from_zero()
    {
        // SQL Server's ROUND of a decimal rounds half away from zero, as Power Fx; one more integer digit keeps 9.99 → 10.0 from
        // overflowing (error 8115).
        _computed.Set("xrmv_fxdec", Fx("Round", Zero("xrmv_amount"), Dec(1m)));

        var c = Select("xrmv_fxdec");

        From(c).Should().Contain("(SELECT MAX(x.v) AS [v] FROM (VALUES (ROUND(CAST(COALESCE(t0.[xrmv_amount], 0) AS decimal(24, 2)), 1))) AS x(v))");
    }

    [Fact]
    public void RoundDown_RoundUp_Trunc_Int()
    {
        IntValue(Fx("RoundDown", Zero("xrmv_amount"), Dec(0m)), out _).Should().Be("ROUND(COALESCE(t0.[xrmv_amount], 0), 0, 1)");
        IntValue(Fx("Trunc", Zero("xrmv_amount")), out _).Should().Be("ROUND(COALESCE(t0.[xrmv_amount], 0), 0, 1)");
        // Int floors: Int(-2.5) is -3.
        IntValue(Fx("Int", Zero("xrmv_amount")), out _).Should().Be("FLOOR(COALESCE(t0.[xrmv_amount], 0))");
        // A whole number is its own Int, Trunc and Round to 0 digits.
        IntValue(Fx("Int", Zero("xrmv_qty")), out _).Should().Be("COALESCE(t0.[xrmv_qty], 0)");

        var up = IntValue(Fx("RoundUp", Zero("xrmv_amount"), Dec(0m)), out var c);
        From(c).Should().Contain("OUTER APPLY (SELECT MAX(x.v) AS [v] FROM (VALUES (CAST(COALESCE(t0.[xrmv_amount], 0) AS decimal(24, 2)))) AS x(v)) AS c0");
        up.Should().Be("TRY_CAST(CASE WHEN ROUND(c0.[v], 0, 1) = c0.[v] THEN c0.[v] WHEN c0.[v] > 0 " +
                       "THEN ROUND(c0.[v], 0, 1) + TRY_CAST(POWER(CAST(10 AS float), -(0)) AS decimal(37, 2)) " +
                       "ELSE ROUND(c0.[v], 0, 1) - TRY_CAST(POWER(CAST(10 AS float), -(0)) AS decimal(37, 2)) END AS decimal(24, 2))");
    }

    [Fact]
    public void Mod_sign_follows_divisor()
    {
        // Power Fx: Mod(-7, 3) = 2, Mod(7, -3) = -2; T-SQL % follows the dividend: ((n % d) + d) % d. A zero divisor gives null.
        IntValue(Fx("Mod", Zero("xrmv_qty"), Dec(3m)), out _).Should().Be(
            "(((CAST(COALESCE(t0.[xrmv_qty], 0) AS decimal(10, 0)) % NULLIF(CAST(3 AS decimal(10, 0)), 0)) + CAST(3 AS decimal(10, 0))) " +
            "% NULLIF(CAST(3 AS decimal(10, 0)), 0))");
        _computed.Set("xrmv_fxfloat", Fx("Mod", Fx("Zero", Own("xrmv_ratio")), Zero("xrmv_qty")));
        Selected(Select("xrmv_fxfloat"), "xrmv_fxfloat").Should().Be(
            "CAST((c0.[v] - c1.[v] * FLOOR(c0.[v] / NULLIF(c1.[v], 0))) AS float)");
    }

    [Fact]
    public void Zero_is_a_coalesce_of_its_operand()
    {
        // F5: fx.Zero is COALESCE(x, 0) whatever x is (the translator writes it around a blank operand only, never around an error).
        _computed.Set("xrmv_fxfloat", new Binary("Add", Fx("Zero", Fx("Sqrt", Dec(-1m))), Dec(1m)));
        Selected(Select("xrmv_fxfloat"), "xrmv_fxfloat").Should().Contain("COALESCE(SQRT(");
        IntValue(new Binary("Add", Fx("Zero", Fx("Mod", Zero("xrmv_qty"), Dec(0m))), Dec(1m)), out var c);
        From(c).Should().Contain("(VALUES ((COALESCE((((");
    }

    [Fact]
    public void Ln_Sqrt_Exp_Power_give_null_outside_their_domain()
    {
        _computed.Set("xrmv_fxfloat", Fx("Ln", Zero("xrmv_qty")));
        Selected(Select("xrmv_fxfloat"), "xrmv_fxfloat").Should().Contain("LOG(CASE WHEN c0.[v] > 0 THEN CAST(c0.[v] AS float) END)");
        _computed.Set("xrmv_fxfloat", Fx("Sqrt", Own("xrmv_qty")));
        Selected(Select("xrmv_fxfloat"), "xrmv_fxfloat").Should().Be("CAST(SQRT(CASE WHEN t0.[xrmv_qty] >= 0 THEN CAST(t0.[xrmv_qty] AS float) END) AS float)");
        _computed.Set("xrmv_fxfloat", Fx("Exp", Own("xrmv_ratio")));
        Selected(Select("xrmv_fxfloat"), "xrmv_fxfloat").Should().Be("CAST(CASE WHEN t0.[xrmv_ratio] > 709 THEN NULL ELSE EXP(t0.[xrmv_ratio]) END AS float)");
        _computed.Set("xrmv_fxfloat", Fx("Power", Own("xrmv_ratio"), Dec(2m)));
        Selected(Select("xrmv_fxfloat"), "xrmv_fxfloat").Should().Be(
            "CAST(CASE WHEN t0.[xrmv_ratio] = 0 AND CAST(2 AS float) < 0 THEN NULL " +
            "WHEN t0.[xrmv_ratio] < 0 AND CAST(2 AS float) <> FLOOR(CAST(2 AS float)) THEN NULL " +
            "WHEN CAST(2 AS float) * LOG10(ABS(NULLIF(t0.[xrmv_ratio], 0))) > 308 THEN NULL " +
            "ELSE POWER(t0.[xrmv_ratio], CAST(2 AS float)) END AS float)");
    }

    [Fact]
    public void Sum_Average_Max_Min()
    {
        IntValue(Fx("Max", Zero("xrmv_qty"), Dec(5m)), out _).Should().Be("GREATEST(CAST(COALESCE(t0.[xrmv_qty], 0) AS decimal(10, 0)), CAST(5 AS decimal(10, 0)))");
        IntValue(Fx("Min", Zero("xrmv_qty"), Dec(5m)), out _).Should().Be("LEAST(CAST(COALESCE(t0.[xrmv_qty], 0) AS decimal(10, 0)), CAST(5 AS decimal(10, 0)))");
        IntValue(Fx("Sum", Zero("xrmv_qty"), Dec(5m)), out var c).Should().Be(
            "CASE WHEN ABS(c0.[v]) <= 100000000000 THEN CAST(c0.[v] AS decimal(12, 0)) END");
        From(c).Should().Contain("(SELECT MAX(x.v) AS [v] FROM (VALUES ((CAST(COALESCE(t0.[xrmv_qty], 0) AS decimal(10, 0)) + CAST(5 AS decimal(1, 0))))) AS x(v))");
        IntValue(Fx("Average", Zero("xrmv_qty"), Dec(5m)), out c);
        From(c).Should().Contain(" / NULLIF(CAST(2 AS decimal(28, 10)), 0))");
    }

    [Fact]
    public void Value_of_non_number_is_null()
    {
        // TRY_CAST: 'abc' and '' give null (Power Fx: blank); the range of a decimal applies.
        IntValue(Fx("Value", Empty("xrmv_code")), out var c).Should().Be("CASE WHEN ABS(c0.[v]) <= 100000000000 THEN c0.[v] END");
        From(c).Should().Contain("OUTER APPLY (SELECT MAX(x.v) AS [v] FROM (VALUES (TRY_CAST(COALESCE(t0.[xrmv_code], N'') AS decimal(22, 10)))) AS x(v)) AS c0");
        _computed.Set("xrmv_fxfloat", Fx("Float", Own("xrmv_code")));
        Selected(Select("xrmv_fxfloat"), "xrmv_fxfloat").Should().Be("CAST(TRY_CAST(NULLIF(TRIM(t0.[xrmv_code]), N'') AS float) AS float)");
        // Decimal of a number is the number.
        IntValue(Fx("Decimal", Own("xrmv_qty")), out _).Should().Be("t0.[xrmv_qty]");
    }

    // ---- Text(number, format) ---------------------------------------------------------------------------------------------

    [Fact]
    public void Text_whole_number_format_0()
    {
        // No FORMAT(): a whole number as text; a decimal rounded half away from zero first.
        TextValue(Fx("Text", Zero("xrmv_qty"), Text("0")), out var c).Should().Be("CAST(CAST(COALESCE(t0.[xrmv_qty], 0) AS bigint) AS nvarchar(20))");
        Values(c).Should().BeEmpty("the format is not written");
        TextValue(Fx("Text", Zero("xrmv_amount"), Text("0")), out _).Should().Be(
            "CAST(TRY_CAST(ROUND(CAST(COALESCE(t0.[xrmv_amount], 0) AS decimal(24, 2)), 0) AS bigint) AS nvarchar(20))");

        // "000": digit padding.
        TextValue(Fx("Text", Zero("xrmv_qty"), Text("000")), out c).Should().Be(
            "CONCAT(CASE WHEN c0.[v] < 0 THEN N'-' ELSE N'' END, CASE WHEN LEN(CAST(ABS(c0.[v]) AS nvarchar(20))) >= 3 " +
            "THEN CAST(ABS(c0.[v]) AS nvarchar(20)) ELSE RIGHT(CONCAT(N'000', CAST(ABS(c0.[v]) AS nvarchar(20))), 3) END)");
        From(c).Should().Contain("OUTER APPLY (SELECT MAX(x.v) AS [v] FROM (VALUES (CAST(COALESCE(t0.[xrmv_qty], 0) AS bigint))) AS x(v)) AS c0");

        // "#": a zero value gives no digit; literal characters are parameters.
        TextValue(Fx("Text", Zero("xrmv_qty"), Text("ORD-#")), out c).Should().Be(
            "CONCAT(CASE WHEN c0.[v] < 0 THEN N'-' ELSE N'' END, @p0, CASE WHEN c0.[v] = 0 THEN N'' ELSE CAST(ABS(c0.[v]) AS nvarchar(20)) END)");
        Values(c).Single().Should().Be("ORD-");
    }

    [Fact]
    public void Text_with_literals_between_digits()
    {
        // "0-0" on 123 is "12-3": the first placeholder takes the extra digits.
        var sql = TextValue(Fx("Text", Zero("xrmv_qty"), Text("0-0")), out var c);
        sql.Should().Be("CONCAT(CASE WHEN c0.[v] < 0 THEN N'-' ELSE N'' END, NULLIF(LEFT(c2.[v], c1.[v] - 1), N' '), @p0, " +
                        "NULLIF(SUBSTRING(c2.[v], c1.[v] - 0, 1), N' '))");
        From(c).Should().Contain("(SELECT MAX(x.v) AS [v] FROM (VALUES (CASE WHEN LEN(CAST(ABS(c0.[v]) AS nvarchar(20))) > 2 THEN LEN(CAST(ABS(c0.[v]) AS nvarchar(20))) ELSE 2 END)) AS x(v)) AS c1")
            .And.Contain("(SELECT MAX(x.v) AS [v] FROM (VALUES (RIGHT(CONCAT(N'00', CAST(ABS(c0.[v]) AS nvarchar(20))), c1.[v]))) AS x(v)) AS c2");
    }

    // ---- text ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Len_counts_trailing_spaces()
    {
        IntValue(Fx("Len", Empty("xrmv_code")), out _).Should().Be("(LEN(CONCAT(COALESCE(t0.[xrmv_code], N''), N'.')) - 1)");
    }

    [Fact]
    public void Trim_collapses_inner_spaces()
    {
        TextValue(Fx("Trim", Empty("xrmv_code")), out _).Should().Be(
            "REPLACE(REPLACE(REPLACE(TRIM(COALESCE(t0.[xrmv_code], N'')) COLLATE Latin1_General_BIN2, N' ', N' ' + NCHAR(1)), " +
            "NCHAR(1) + N' ', N''), NCHAR(1), N'') COLLATE DATABASE_DEFAULT");
        TextValue(Fx("TrimEnds", Own("xrmv_code")), out _).Should().Be("TRIM(t0.[xrmv_code])");
        TextValue(Fx("Upper", Own("xrmv_code")), out _).Should().Be("UPPER(t0.[xrmv_code])");
        TextValue(Fx("Lower", Own("xrmv_code")), out _).Should().Be("LOWER(t0.[xrmv_code])");
    }

    [Fact]
    public void Mid_Left_Right_Replace_Substitute()
    {
        TextValue(Fx("Left", Own("xrmv_code"), Dec(2m)), out _).Should().Be("LEFT(t0.[xrmv_code], 2)");
        TextValue(Fx("Right", Own("xrmv_code"), Zero("xrmv_qty")), out _).Should().Be(
            "RIGHT(t0.[xrmv_code], CASE WHEN c0.[v] >= 0 THEN CAST(c0.[v] AS bigint) END)");
        TextValue(Fx("Left", Own("xrmv_code"), Dec(-1m)), out _).Should().Be("LEFT(t0.[xrmv_code], NULL)");
        TextValue(Fx("Mid", Own("xrmv_code"), Dec(2m), Dec(3m)), out _).Should().Be("SUBSTRING(t0.[xrmv_code], 2, 3)");
        TextValue(Fx("Mid", Own("xrmv_code"), Dec(2m)), out _).Should().Be("SUBSTRING(t0.[xrmv_code], 2, 2147483647)");
        TextValue(Fx("Mid", Own("xrmv_code"), Dec(0m)), out _).Should().Be("SUBSTRING(t0.[xrmv_code], NULL, 2147483647)");
        TextValue(Fx("Replace", Own("xrmv_code"), Dec(2m), Dec(3m), Text("XY")), out _).Should().Be(
            "CONCAT(LEFT(t0.[xrmv_code], 2 - 1), @p0, SUBSTRING(t0.[xrmv_code], 2 + 3, 2147483647))");
        TextValue(Fx("Replace", Own("xrmv_code"), Zero("xrmv_qty"), Dec(3m), Text("XY")), out _).Should().StartWith(
            "CASE WHEN c1.[v] IS NULL OR 3 IS NULL THEN NULL ELSE CONCAT(LEFT(t0.[xrmv_code], c1.[v] - 1), @p0, ");
        // Substitute is case-sensitive (binary), as Power Fx.
        TextValue(Fx("Substitute", Own("xrmv_code"), Text("a"), Text("x")), out _).Should().Be(
            "REPLACE(t0.[xrmv_code] COLLATE Latin1_General_BIN2, @p0, @p1) COLLATE DATABASE_DEFAULT");
        // The instance: each search starts after the one before (a missing one is 2^30, past any text), all in one value.
        TextValue(Fx("Substitute", Own("xrmv_code"), Text("a"), Text("x"), Dec(2m)), out var c).Should().Be(
            "CASE WHEN DATALENGTH(@p0) = 0 OR COALESCE(c1.[v], 1073741824) = 1073741824 THEN t0.[xrmv_code] " +
            "ELSE STUFF(t0.[xrmv_code], c1.[v], c0.[v], @p1) END");
        From(c).Should().Contain("(SELECT MAX(x.v) AS [v] FROM (VALUES ((LEN(CONCAT(@p0, N'.')) - 1))) AS x(v)) AS c0")
            .And.Contain("(SELECT MAX(x.v) AS [v] FROM (VALUES ((CHARINDEX(@p0 COLLATE Latin1_General_BIN2, t0.[xrmv_code], " +
                         "(CHARINDEX(@p0 COLLATE Latin1_General_BIN2, t0.[xrmv_code]) + 1073741823) % 1073741824 + 1 + c0.[v]) " +
                         "+ 1073741823) % 1073741824 + 1)) AS x(v)) AS c1");
        Regex.Matches(c.Sql, "OUTER APPLY").Count.Should().Be(2);
        // Final review N1: one apply per instance made Substitute(x, "a", "b", 100) 101 applies; it is now 2, without NULLIF.
        TextValue(Fx("Substitute", Own("xrmv_code"), Text("a"), Text("x"), Dec(100m)), out c);
        Regex.Matches(c.Sql, "OUTER APPLY").Count.Should().Be(2);
        Regex.Matches(c.Sql, "CHARINDEX").Count.Should().Be(100);
        c.Sql.Should().NotContain("NULLIF");
        _computed.Set("xrmv_fxtext", Fx("Substitute", Own("xrmv_code"), Text("a"), Text("x"), Zero("xrmv_qty")));
        FluentActions.Invoking(() => Select("xrmv_fxtext")).Should().Throw<XrmdValidationException>()
            .WithMessage("The formula column 'xrmv_fxorder.xrmv_fxtext' uses Substitute with an instance that is not a constant number*");
    }

    [Fact]
    public void In_StartsWith_EndsWith_are_yes_no_values()
    {
        _computed.Set("xrmv_fxbool", new Conditional(
            [new ComputedBranch(new ComputedCondition.Compare("Equal", Fx("StartsWith", Empty("xrmv_code"), Text("AB")), new Literal(true)), new Literal(true))],
            new Literal(false)));
        Selected(Select("xrmv_fxbool"), "xrmv_fxbool").Should().Contain(
            "CAST(CASE WHEN DATALENGTH(c0.[v]) >= DATALENGTH(@p0) AND LEFT(c0.[v], DATALENGTH(@p0) / 2) = @p0 THEN 1 ELSE 0 END AS bit) = CAST(1 AS bit)");
        _computed.Set("xrmv_fxbool", new Conditional(
            [new ComputedBranch(new ComputedCondition.Compare("Equal", Fx("ExactIn", Text("b"), Empty("xrmv_code")), new Literal(true)), new Literal(true))],
            new Literal(false)));
        Selected(Select("xrmv_fxbool"), "xrmv_fxbool").Should().Contain(
            "CAST(CASE WHEN DATALENGTH(@p0) = 0 OR CHARINDEX(@p0 COLLATE Latin1_General_BIN2, COALESCE(t0.[xrmv_code], N'')) > 0 THEN 1 ELSE 0 END AS bit)");
    }

    // ---- dates -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Milliseconds", "millisecond")]
    [InlineData("Seconds", "second")]
    [InlineData("Minutes", "minute")]
    [InlineData("Hours", "hour")]
    [InlineData("Days", "day")]
    [InlineData("Months", "month")]
    [InlineData("Quarters", "quarter")]
    [InlineData("Years", "year")]
    public void DateAdd_units(string unit, string part)
    {
        _computed.Set("xrmv_fxdate", Fx("DateAdd", Own("xrmv_startedon"), Dec(3m), Text(unit)));

        var c = Select("xrmv_fxdate");

        // A result past 9999-12-31 or before 0001-01-01 is null, not SQL error 517.
        Selected(c, "xrmv_fxdate").Should().Be(
            $"CAST(CASE WHEN 3 BETWEEN DATEDIFF_BIG({part}, t0.[xrmv_startedon], CAST('0001-01-01' AS datetime2)) " +
            $"AND DATEDIFF_BIG({part}, t0.[xrmv_startedon], CAST('9999-12-31T23:59:59.9999999' AS datetime2)) " +
            $"THEN DATEADD({part}, 3, t0.[xrmv_startedon]) END AS datetime2)");
        Values(c).Should().BeEmpty("the unit is a keyword, not a value");
    }

    [Fact]
    public void DateAdd_count_is_a_whole_number()
    {
        _computed.Set("xrmv_fxdate", Fx("DateAdd", Own("xrmv_startedon"), Zero("xrmv_amount"), Text("Days")));

        var c = Select("xrmv_fxdate");

        From(c).Should().Contain("OUTER APPLY (SELECT MAX(x.v) AS [v] FROM (VALUES (TRY_CAST(COALESCE(t0.[xrmv_amount], 0) AS int))) AS x(v)) AS c0");
        Selected(c, "xrmv_fxdate").Should().EndWith("THEN DATEADD(day, c0.[v], t0.[xrmv_startedon]) END AS datetime2)");
    }

    [Fact]
    public void DateDiff_counts_boundaries()
    {
        IntValue(Fx("DateDiff", Own("xrmv_startedon"), Own("xrmv_duedate"), Text("Days")), out _).Should().Be(
            "DATEDIFF(day, t0.[xrmv_startedon], t0.[xrmv_duedate])");
        IntValue(Fx("DateDiff", Own("xrmv_startedon"), Own("xrmv_duedate"), Text("Quarters")), out _).Should().Be(
            "DATEDIFF(quarter, t0.[xrmv_startedon], t0.[xrmv_duedate])");
        // Below a minute the count may pass an int: DATEDIFF_BIG.
        IntValue(Fx("DateDiff", Own("xrmv_startedon"), Own("xrmv_duedate"), Text("Seconds")), out _).Should().Be(
            "DATEDIFF_BIG(second, t0.[xrmv_startedon], t0.[xrmv_duedate])");
    }

    [Fact]
    public void Weekday_Sunday_is_1()
    {
        // Independent of SET DATEFIRST: (DATEPART(weekday) + @@DATEFIRST - 1) % 7 + 1 is 1 on a Sunday under any setting.
        IntValue(Fx("Weekday", Own("xrmv_duedate")), out _).Should().Be("((DATEPART(weekday, t0.[xrmv_duedate]) + @@DATEFIRST - 1) % 7 + 1)");
        // DATEDIFF(week) counts Sunday boundaries whatever DATEFIRST is.
        IntValue(Fx("WeekNum", Own("xrmv_duedate")), out _).Should().Be(
            "(DATEDIFF(week, DATEFROMPARTS(YEAR(t0.[xrmv_duedate]), 1, 1), t0.[xrmv_duedate]) + 1)");
    }

    [Fact]
    public void ISOWeekNum()
    {
        IntValue(Fx("ISOWeekNum", Own("xrmv_duedate")), out _).Should().Be("DATEPART(iso_week, t0.[xrmv_duedate])");
        IntValue(Fx("Day", Own("xrmv_duedate")), out _).Should().Be("DATEPART(day, t0.[xrmv_duedate])");
        IntValue(Fx("Second", Own("xrmv_duedate")), out _).Should().Be("DATEPART(second, t0.[xrmv_duedate])");
    }

    [Fact]
    public void UTCToday_and_IsUTCToday()
    {
        _computed.Set("xrmv_fxdate", Fx("UTCToday"));
        Selected(Select("xrmv_fxdate"), "xrmv_fxdate").Should().Be("CAST(CAST(CAST(SYSUTCDATETIME() AS date) AS datetime2) AS datetime2)");
        // UTCToday is a date and time at midnight: DATEPART(hour, <date>) would be SQL error 9810.
        IntValue(Fx("Hour", Fx("UTCToday")), out _).Should().Be("DATEPART(hour, CAST(CAST(SYSUTCDATETIME() AS date) AS datetime2))");
        _computed.Set("xrmv_fxbool", new Conditional(
            [new ComputedBranch(new ComputedCondition.Compare("Equal", Fx("IsUTCToday", Own("xrmv_duedate")), new Literal(true)), new Literal(true))],
            new Literal(false)));
        Selected(Select("xrmv_fxbool"), "xrmv_fxbool").Should().Contain(
            "CAST(CASE WHEN CAST(t0.[xrmv_duedate] AS date) = CAST(SYSUTCDATETIME() AS date) THEN 1 ELSE 0 END AS bit)");
    }

    [Fact]
    public void Constant_outside_int_is_null()
    {
        // A whole constant written into SQL that does not fit int overflows on every row (error 8115): it is null instead.
        TextValue(Fx("Char", Dec(3000000000m)), out _).Should().Be("NCHAR(NULL)");
        IntValue(Fx("Round", Zero("xrmv_amount"), Dec(3000000000m)), out _).Should().Contain(", NULL)");
        _computed.Set("xrmv_fxdate", Fx("DateAdd", Own("xrmv_startedon"), Dec(3000000000m), Text("Milliseconds")));
        Selected(Select("xrmv_fxdate"), "xrmv_fxdate").Should().Be("CAST(NULL AS datetime2)");
        TextValue(Fx("Left", Own("xrmv_code"), Dec(3000000000m)), out _).Should().Be("LEFT(t0.[xrmv_code], 3000000000)", "a count is a bigint");
    }

    // ---- every name, chains --------------------------------------------------------------------------------------------

    public static TheoryData<string, ComputedExpr> EveryFunction() => new()
    {
        { "xrmv_fxint", Zero("xrmv_qty") },
        { "xrmv_fxtext", Empty("xrmv_code") },
        { "xrmv_fxtext", Fx("Concat", Empty("xrmv_code"), Text("a")) },
        { "xrmv_fxtext", Fx("Concatenate", Empty("xrmv_code")) },
        { "xrmv_fxtext", Fx("Concatenate", Empty("xrmv_code"), Text("a"), Text("b")) },
        { "xrmv_fxfloat", Fx("Power", Zero("xrmv_qty"), Dec(2m)) },
        { "xrmv_fxint", Fx("Mod", Zero("xrmv_qty"), Dec(2m)) },
        { "xrmv_fxint", Fx("Abs", Zero("xrmv_qty")) },
        { "xrmv_fxint", Fx("Int", Zero("xrmv_amount")) },
        { "xrmv_fxfloat", Fx("Exp", Zero("xrmv_qty")) },
        { "xrmv_fxfloat", Fx("Ln", Zero("xrmv_qty")) },
        { "xrmv_fxfloat", Fx("Sqrt", Zero("xrmv_qty")) },
        { "xrmv_fxtext", Fx("Char", Dec(65m)) },
        { "xrmv_fxdec", Fx("Trunc", Zero("xrmv_amount"), Dec(1m)) },
        { "xrmv_fxdec", Fx("Round", Zero("xrmv_amount"), Dec(1m)) },
        { "xrmv_fxdec", Fx("RoundDown", Zero("xrmv_amount"), Dec(1m)) },
        { "xrmv_fxdec", Fx("RoundUp", Zero("xrmv_amount"), Zero("xrmv_qty")) },
        { "xrmv_fxdec", Fx("Average", Zero("xrmv_amount"), Zero("xrmv_qty")) },
        { "xrmv_fxdec", Fx("Max", Zero("xrmv_amount"), Zero("xrmv_qty")) },
        { "xrmv_fxfloat", Fx("Min", Fx("Zero", Own("xrmv_ratio")), Zero("xrmv_qty")) },
        { "xrmv_fxdec", Fx("Sum", Zero("xrmv_amount"), Zero("xrmv_qty"), Dec(1.5m)) },
        { "xrmv_fxtext", Fx("Upper", Empty("xrmv_code")) },
        { "xrmv_fxtext", Fx("Lower", Empty("xrmv_code")) },
        { "xrmv_fxtext", Fx("Trim", Empty("xrmv_code")) },
        { "xrmv_fxtext", Fx("TrimEnds", Empty("xrmv_code")) },
        { "xrmv_fxint", Fx("Len", Empty("xrmv_code")) },
        { "xrmv_fxtext", Fx("Left", Empty("xrmv_code"), Zero("xrmv_qty")) },
        { "xrmv_fxtext", Fx("Right", Empty("xrmv_code"), Dec(1m)) },
        { "xrmv_fxtext", Fx("Mid", Empty("xrmv_code"), Zero("xrmv_qty"), Zero("xrmv_qty")) },
        { "xrmv_fxtext", Fx("Replace", Empty("xrmv_code"), Zero("xrmv_qty"), Dec(1m), Text("x")) },
        { "xrmv_fxtext", Fx("Substitute", Empty("xrmv_code"), Text("a"), Text("b"), Dec(3m)) },
        { "xrmv_fxtext", Fx("Text", Zero("xrmv_amount"), Text("#0")) },
        { "xrmv_fxdec", Fx("Value", Empty("xrmv_code")) },
        { "xrmv_fxdec", Fx("Decimal", Fx("Zero", Own("xrmv_ratio"))) },
        { "xrmv_fxfloat", Fx("Float", Zero("xrmv_amount")) },
        { "xrmv_fxdate", Fx("DateAdd", Own("xrmv_duedate"), Zero("xrmv_qty"), Text("Months")) },
        { "xrmv_fxint", Fx("DateDiff", Own("xrmv_duedate"), Own("xrmv_startedon"), Text("Milliseconds")) },
        { "xrmv_fxint", Fx("Day", Own("xrmv_duedate")) },
        { "xrmv_fxint", Fx("Month", Own("xrmv_duedate")) },
        { "xrmv_fxint", Fx("Year", Own("xrmv_duedate")) },
        { "xrmv_fxint", Fx("Hour", Own("xrmv_duedate")) },
        { "xrmv_fxint", Fx("Minute", Own("xrmv_duedate")) },
        { "xrmv_fxint", Fx("Second", Own("xrmv_duedate")) },
        { "xrmv_fxint", Fx("Weekday", Own("xrmv_duedate")) },
        { "xrmv_fxint", Fx("WeekNum", Own("xrmv_duedate")) },
        { "xrmv_fxint", Fx("ISOWeekNum", Own("xrmv_duedate")) },
        { "xrmv_fxdate", Fx("UTCToday") },
        { "xrmv_fxbool", Fx("IsUTCToday", Own("xrmv_duedate")) },
        { "xrmv_fxbool", Fx("In", Text("a"), Empty("xrmv_code")) },
        { "xrmv_fxbool", Fx("ExactIn", Text("a"), Empty("xrmv_code")) },
        { "xrmv_fxbool", Fx("StartsWith", Empty("xrmv_code"), Text("a")) },
        { "xrmv_fxbool", Fx("EndsWith", Empty("xrmv_code"), Text("a")) },
    };

    [Theory]
    [MemberData(nameof(EveryFunction))]
    public void Every_fx_function_is_computed(string column, ComputedExpr body)
    {
        _computed.Set(column, body);

        var c = Select(column);

        c.Sql.Should().NotContain("FORMAT(");
        Selected(c, column).Should().NotBeEmpty();
    }

    [Fact]
    public void Unknown_fx_function_is_refused_as_a_formula_column()
    {
        _computed.Set("xrmv_fxint", Fx("Nope", Zero("xrmv_qty")));

        FluentActions.Invoking(() => Select("xrmv_fxint")).Should().Throw<XrmdValidationException>()
            .WithMessage("The formula column 'xrmv_fxorder.xrmv_fxint' uses the function 'fx.Nope', which cannot be computed.");
    }

    [Fact]
    public void Formula_chain_of_ten_is_computed()
    {
        // F10: a chain of formula columns may hold 10 (the calculated-only guard of 5 does not apply).
        _computed.Set("xrmv_fxchain1", Zero("xrmv_qty"));
        for (var i = 2; i <= 11; i++) _computed.Set($"xrmv_fxchain{i}", new Binary("Add", Own($"xrmv_fxchain{i - 1}"), Dec(1m)));

        Select("xrmv_fxchain10").Sql.Should().Contain("t0.[xrmv_qty]");
        FluentActions.Invoking(() => Select("xrmv_fxchain11")).Should().Throw<XrmdValidationException>()
            .WithMessage("The formula column 'xrmv_fxorder.xrmv_fxchain1' chains more than 10 formula or calculated columns.");
    }

    [Fact]
    public void Double_result_is_a_float()
    {
        _computed.Set("xrmv_fxfloat", Fx("Float", Zero("xrmv_qty")));

        Selected(Select("xrmv_fxfloat"), "xrmv_fxfloat").Should().Be("CAST(CAST(COALESCE(t0.[xrmv_qty], 0) AS float) AS float)");
    }

    // ---- size (final review C1, I1) ----------------------------------------------------------------------------------------

    [Fact]
    public void A_value_read_more_than_once_is_computed_once()
    {
        // FormulaTranslator writes one node for both sides of a blank-safe '=' (and for the subject of Switch): the node is
        // computed once, in an apply, and read as cN.[v] wherever it occurs.
        var upper = Fx("Upper", Empty("xrmv_code"));
        _computed.Set("xrmv_fxbool", new Conditional(
            [new ComputedBranch(new ComputedCondition.And(new ComputedCondition.Compare("NotNull", upper, null),
                new ComputedCondition.Compare("Equal", upper, upper)), new Literal(true))],
            new Literal(false)));

        var c = Select("xrmv_fxbool");

        From(c).Should().Contain("OUTER APPLY (SELECT MAX(x.v) AS [v] FROM (VALUES (UPPER(COALESCE(t0.[xrmv_code], N'')))) AS x(v)) AS c0");
        Regex.Matches(c.Sql, "UPPER").Count.Should().Be(1);
        Selected(c, "xrmv_fxbool").Should().Be(
            "CAST(CASE WHEN (c0.[v] IS NOT NULL AND c0.[v] = c0.[v]) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS bit)");
    }

    [Fact]
    public void A_yes_no_value_read_more_than_once_is_computed_through_tinyint()
    {
        // MAX, which keeps SQL Server from writing the value into each reader, takes no bit.
        var flag = new Conditional([new ComputedBranch(new ComputedCondition.Compare("Equal", Zero("xrmv_qty"), Dec(1m)), new Literal(true))],
            new Literal(false));
        _computed.Set("xrmv_fxbool", new Conditional(
            [new ComputedBranch(new ComputedCondition.And(new ComputedCondition.Compare("NotNull", flag, null),
                new ComputedCondition.Compare("Equal", flag, new Literal(true))), new Literal(true))],
            new Literal(false)));

        var c = Select("xrmv_fxbool");

        From(c).Should().Contain("OUTER APPLY (SELECT CAST(MAX(CAST(x.v AS tinyint)) AS bit) AS [v] FROM (VALUES (CASE WHEN COALESCE(t0.[xrmv_qty], 0) = ");
        Selected(c, "xrmv_fxbool").Should().StartWith("CAST(CASE WHEN (c0.[v] IS NOT NULL AND c0.[v] = CAST(1 AS bit)) THEN");
    }

    [Fact]
    public void A_chained_formula_column_read_more_than_once_is_computed_once()
    {
        _computed.Set("xrmv_fxchain1", Fx("Upper", Empty("xrmv_code")));
        _computed.Set("xrmv_fxtext", Fx("Concat", Own("xrmv_fxchain1"), Own("xrmv_fxchain1")));

        var c = Select("xrmv_fxtext");

        Regex.Matches(c.Sql, "UPPER").Count.Should().Be(1);
        Selected(c, "xrmv_fxtext").Should().Be("LEFT(CONCAT(c0.[v], c0.[v]), 4000)");
    }

    /// <summary>A balanced tree of <paramref name="depth"/> levels joining its leaves with <paramref name="join"/>, no node shared.</summary>
    private static ComputedExpr Tree(int depth, Func<ComputedExpr> leaf, Func<ComputedExpr, ComputedExpr, ComputedExpr> join) =>
        depth == 0 ? leaf() : join(Tree(depth - 1, leaf, join), Tree(depth - 1, leaf, join));

    [Fact]
    public void An_expression_whose_sql_is_too_long_is_refused()
    {
        // 2^17 texts joined by '&': several MB of SQL in one expression, refused in our words rather than built.
        _computed.Set("xrmv_fxtext", Tree(17, () => Empty("xrmv_code"), (l, r) => Fx("Concat", l, r)));

        FluentActions.Invoking(() => Select("xrmv_fxtext")).Should().Throw<XrmdValidationException>()
            .WithMessage("The formula column 'xrmv_fxorder.xrmv_fxtext' is too complex to compute: its SQL would exceed 300,000 characters*");
    }

    [Fact]
    public void An_expression_with_too_many_applies_is_refused()
    {
        // 2^15 different sums, each computed once in an apply (the range of decimals): far more applies than a column may hold.
        var leaf = 0m;
        _computed.Set("xrmv_fxdec", Tree(15, () => new Binary("Add", Zero("xrmv_amount"), Dec(++leaf)), (l, r) => new Binary("Add", l, r)));

        FluentActions.Invoking(() => Select("xrmv_fxdec")).Should().Throw<XrmdValidationException>()
            .WithMessage("The formula column 'xrmv_fxorder.xrmv_fxdec' is too complex to compute: its SQL would need more than 250 intermediate values*");
    }

    /// <summary>
    /// <c>amount + leaf(1) + … + leaf(n - 1)</c>: each partial sum is computed once, in an apply of its own (the range of decimal
    /// intermediates), so the sum holds about <paramref name="n"/> applies.
    /// </summary>
    private static ComputedExpr Sum(int n, Func<int, ComputedExpr>? leaf = null)
    {
        leaf ??= i => Dec(i);
        ComputedExpr sum = Zero("xrmv_amount");
        for (var i = 1; i < n; i++) sum = new Binary("Add", sum, leaf(i));
        return sum;
    }

    private static int Applies(string sql) => Regex.Matches(sql, "OUTER APPLY").Count;

    [Fact]
    public void A_formula_column_holds_at_most_250_applies()
    {
        // Final review N1: SQL Server's compile time grows faster than the number of applies (SQL Server 2022: 264 applies 0.9 s,
        // 873 25 s, about 2,900 out of stack space, Msg 8621), so a column may hold 250, refused in our words beyond.
        _computed.Set("xrmv_fxdec", Sum(240));
        Applies(Select("xrmv_fxdec").Sql).Should().BeInRange(230, 250);

        _computed.Set("xrmv_fxdec", Sum(260));
        FluentActions.Invoking(() => Select("xrmv_fxdec")).Should().Throw<XrmdValidationException>()
            .WithMessage("The formula column 'xrmv_fxorder.xrmv_fxdec' is too complex to compute: its SQL would need more than 250 " +
                         "intermediate values; simplify the formula.");
    }

    [Fact]
    public void Check_refuses_a_formula_column_with_more_than_250_applies()
    {
        var entity = TestMetadata.Instance.GetEntity(Order)!;
        _computed.Set("xrmv_fxdec", Sum(240));
        FormulaSqlCheck.Check(entity, TestMetadata.Attribute(Order, "xrmv_fxdec"), TestMetadata.Instance.GetEntity, _computed, TestMetadata.Dialect);

        _computed.Set("xrmv_fxdec", Sum(260));
        FluentActions.Invoking(() => FormulaSqlCheck.Check(entity, TestMetadata.Attribute(Order, "xrmv_fxdec"), TestMetadata.Instance.GetEntity,
                _computed, TestMetadata.Dialect))
            .Should().Throw<XrmdValidationException>()
            .WithMessage("The formula column 'xrmv_fxorder.xrmv_fxdec' is too complex to compute: its SQL would need more than 250 intermediate values*");
    }

    [Fact]
    public void Formula_columns_read_together_are_each_held_to_the_bounds_of_one_column()
    {
        // Final review M1: the bound was on all the formula columns of a query, so that columns each accepted could not be read
        // together. Each column is held to its own bounds; the query to twice them (within SQL Server's 30 s command timeout).
        // Texts of about 1,100 characters of SQL each, 270,000 a column (their parameters differ, so no two columns share an apply).
        static ComputedExpr Codes(int depth) => Tree(depth, () => Empty("xrmv_code"), (l, r) => Fx("Concat", l, r));
        for (var column = 1; column <= 2; column++)
            _computed.Set($"xrmv_fxchain{column}",
                Sum(240, _ => Fx("Len", Fx("Concat", Text("x"), Fx("Concat", Codes(4), Codes(3))))));

        var c = Select("xrmv_fxchain1", "xrmv_fxchain2");

        Applies(c.Sql).Should().BeGreaterThan(450);
        c.Sql.Length.Should().BeGreaterThan(500_000);
    }

    [Fact]
    public void A_query_reading_formula_columns_holds_at_most_500_applies()
    {
        for (var column = 1; column <= 3; column++)
        {
            var first = column * 1000;
            _computed.Set($"xrmv_fxchain{column}", Sum(240, i => Dec(first + i)));
        }

        Applies(Select("xrmv_fxchain1", "xrmv_fxchain2").Sql).Should().BeGreaterThan(450);
        FluentActions.Invoking(() => Select("xrmv_fxchain1", "xrmv_fxchain2", "xrmv_fxchain3"))
            .Should().Throw<XrmdValidationException>()
            .WithMessage("The query is too complex to compute: the calculated and formula columns it reads would together need more than 500 " +
                         "intermediate values; read fewer formula columns in one query.");
    }

    [Fact]
    public void Check_compiles_a_formula_column_when_it_is_defined()
    {
        // The definition-time check: the SQL a query reading the column would hold, refused when it is too long.
        var entity = TestMetadata.Instance.GetEntity(Order)!;
        _computed.Set("xrmv_fxtext", Fx("Upper", Empty("xrmv_code")));
        FormulaSqlCheck.Check(entity, TestMetadata.Attribute(Order, "xrmv_fxtext"), TestMetadata.Instance.GetEntity, _computed, TestMetadata.Dialect);

        _computed.Set("xrmv_fxtext", Tree(17, () => Empty("xrmv_code"), (l, r) => Fx("Concat", l, r)));
        FluentActions.Invoking(() => FormulaSqlCheck.Check(entity, TestMetadata.Attribute(Order, "xrmv_fxtext"), TestMetadata.Instance.GetEntity,
                _computed, TestMetadata.Dialect))
            .Should().Throw<XrmdValidationException>()
            .WithMessage("The formula column 'xrmv_fxorder.xrmv_fxtext' is too complex to compute*");
    }
}
