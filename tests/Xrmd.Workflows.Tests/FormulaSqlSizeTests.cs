using System.Diagnostics;
using FluentAssertions;
using Xrmd.Metadata.Computed;
using Xrmd.Query;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;
using Xrmd.Query.Tests;
using Xrmd.Workflows.Calculated;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// The size of the SQL of formula columns (plan 2.12c, final review C1 and I1): Power Fx formulas translated by
/// <see cref="CalculatedColumns"/> and compiled by <see cref="SqlQueryCompiler"/>, on the formula columns of <c>xrmv_fxorder</c>
/// (<see cref="TestMetadata"/>). A value read more than once (the sides of a blank-safe <c>=</c>, the subject of <c>Switch</c>, a
/// chained formula column) is computed once, so that the SQL grows with the formula, not exponentially; and no expression nests
/// <c>CASE</c> (<c>COALESCE</c>, <c>IIF</c> and <c>NULLIF</c> count as one) more than 10 deep (SQL Server error 125).
/// </summary>
public sealed class FormulaSqlSizeTests
{
    private const string Order = "xrmv_fxorder";

    /// <summary>A compilation that takes longer is taken as a blow-up (the shapes below compile in milliseconds).</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc));

    private readonly CalculatedColumns _computed = new(Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance);

    private static void Define(string column, string formula) =>
        ComputedColumnRules.SetFormula(TestMetadata.Attribute(Order, column), formula);

    /// <summary>The SQL of a query reading <paramref name="column"/> with the formula <paramref name="formula"/>, within <see cref="Limit"/>.</summary>
    private string Sql(string column, string? formula = null)
    {
        if (formula is not null) Define(column, formula);
        var compile = Task.Run(() =>
        {
            var root = new EntityNode { EntityName = Order };
            root.Columns.Add(new ColumnNode(column));
            return new SqlQueryCompiler(TestMetadata.Instance, Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance, _computed).Compile(new QueryModel { Root = root }, Context).Sql;
        });
        compile.Wait(Limit).Should().BeTrue($"the SQL of {column} compiles within {Limit.TotalSeconds} s");
        return compile.Result;
    }

    /// <summary>
    /// The deepest nesting of <c>CASE</c> in one expression of <paramref name="sql"/>, counting <c>COALESCE</c>, <c>IIF</c> and
    /// <c>NULLIF</c> as SQL Server does (it writes them as <c>CASE</c>). The applies are never inside an expression, so the
    /// deepest nesting of the text is the deepest of its expressions.
    /// </summary>
    private static int CaseNesting(string sql)
    {
        var (depth, max) = (0, 0);
        var parens = new Stack<bool>();
        var counted = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\'')
            {
                i = sql.IndexOf('\'', i + 1);
                continue;
            }
            if (c == '[')
            {
                i = sql.IndexOf(']', i + 1);
                continue;
            }
            if (c == '(')
            {
                parens.Push(counted);
                if (counted) max = Math.Max(max, ++depth);
                counted = false;
                continue;
            }
            if (c == ')')
            {
                if (parens.Pop()) depth--;
                continue;
            }
            if (!char.IsLetter(c) || (i > 0 && (char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] is '_' or '@'))) continue;
            var end = i;
            while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
            var word = sql[i..end];
            if (word == "CASE") max = Math.Max(max, ++depth);
            else if (word == "END") depth--;
            else counted = word is "COALESCE" or "IIF" or "NULLIF" && end < sql.Length && sql[end] == '(';
            i = end - 1;
        }
        return max;
    }

    [Fact]
    public void Case_nesting_counts_case_coalesce_iif_and_nullif()
    {
        CaseNesting("SELECT CASE WHEN a = 1 THEN COALESCE(NULLIF(x, 0), IIF(b > 1, 1, 0)) END AS [CASE]").Should().Be(3);
        CaseNesting("CASE WHEN 1 = 1 THEN 1 END + CASE WHEN 2 = 2 THEN CASE WHEN 3 = 3 THEN 3 END END").Should().Be(2);
    }

    // ---- C1: SQL grows with the formula ------------------------------------------------------------------------------

    /// <summary><c>If(If(…(x = x, x) = x, x) …)</c>: the blank-safe <c>=</c> of two nullable values, nested <paramref name="depth"/> deep.</summary>
    private static string NestedIfEqual(int depth)
    {
        var formula = "If(xrmv_amount=xrmv_amount,xrmv_amount)";
        for (var i = 1; i < depth; i++) formula = $"If({formula}=xrmv_amount,xrmv_amount)";
        return formula;
    }

    [Fact]
    public void Nested_blank_safe_equality_grows_linearly()
    {
        // Final review C1: 10 deep (291 characters) gave 5.5 MB of SQL, 20 deep (571) an OutOfMemoryException.
        var ten = Sql("xrmv_fxdec", NestedIfEqual(10));
        var twenty = Sql("xrmv_fxdec", NestedIfEqual(20));

        NestedIfEqual(20).Length.Should().Be(571);
        twenty.Length.Should().BeLessThan(60_000);
        twenty.Length.Should().BeLessThan(ten.Length * 3, "twice the formula gives about twice the SQL");
        CaseNesting(twenty).Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public void Equality_of_two_nested_conditionals_grows_linearly()
    {
        // If(A = A, x) where A is itself If(B = B, x): each side was written three times, 3^n.
        var formula = "If(xrmv_qty>1,xrmv_amount)";
        for (var i = 0; i < 4; i++) formula = $"If({formula}={formula},xrmv_amount)";

        formula.Length.Should().BeLessThan(1000);
        var sql = Sql("xrmv_fxdec", formula);

        // The formula text doubles at each level, so does its SQL: no worse than linear in the formula.
        sql.Length.Should().BeLessThan(formula.Length * 100);
        CaseNesting(sql).Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public void Nested_switch_grows_linearly()
    {
        // Final review C1: Switch nested 10 deep (248 characters) gave 104 MB of SQL in 5.5 s (its subject once per branch).
        static string Nested(int depth)
        {
            var formula = "xrmv_qty";
            for (var i = 0; i < depth; i++) formula = $"Switch({formula},1,1,2,2,3,3,4,4)";
            return formula;
        }

        var five = Sql("xrmv_fxdec", Nested(5));
        var ten = Sql("xrmv_fxdec", Nested(10));

        Nested(10).Length.Should().Be(248);
        ten.Length.Should().BeLessThan(30_000);
        ten.Length.Should().BeLessThan(five.Length * 3);
        CaseNesting(ten).Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public void Chain_of_formula_columns_reading_the_previous_four_times_grows_linearly()
    {
        // Final review C1: each link read the previous one 4 times and rebuilt its SQL each time, ×4 per link (chain 7: 24 s).
        Define("xrmv_fxchain1", "xrmv_amount*2");
        for (var i = 2; i <= 10; i++)
            Define($"xrmv_fxchain{i}", $"If(xrmv_fxchain{i - 1}=xrmv_fxchain{i - 1}, xrmv_fxchain{i - 1}*xrmv_fxchain{i - 1})");

        var watch = Stopwatch.StartNew();
        var five = Sql("xrmv_fxchain5");
        var ten = Sql("xrmv_fxchain10");
        watch.Stop();

        ten.Length.Should().BeLessThan(60_000);
        ten.Length.Should().BeLessThan(five.Length * 3, "twice the links give about twice the SQL");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        CaseNesting(ten).Should().BeLessThanOrEqualTo(10);
    }

    // ---- I1: CASE nested at most 10 deep ---------------------------------------------------------------------------------

    /// <summary><c>If(qty > n-1, If(qty > n-2, … If(qty > 0, 0, 1) …, 1), 1)</c> with <paramref name="depth"/> nested <c>If</c>.</summary>
    private static string NestedIf(int depth, string inner = "0", string otherwise = "1")
    {
        var formula = $"If(xrmv_qty>0,{inner},{otherwise})";
        for (var i = 1; i < depth; i++) formula = $"If(xrmv_qty>{i},{formula},{otherwise})";
        return formula;
    }

    [Theory]
    [InlineData(11)]
    [InlineData(15)]
    [InlineData(30)]
    public void Nested_if_keeps_case_nesting_within_ten(int depth)
    {
        // Final review I1: If nested 11 deep wrote CASE nested 11 deep, SQL Server error 125 when a row is read.
        CaseNesting(Sql("xrmv_fxdec", NestedIf(depth))).Should().BeLessThanOrEqualTo(10);
        CaseNesting(Sql("xrmv_fxtext", NestedIf(depth, "\"a\"", "\"b\""))).Should().BeLessThanOrEqualTo(10);
        CaseNesting(Sql("xrmv_fxbool", NestedIf(depth, "true", "xrmv_qty=3"))).Should().BeLessThanOrEqualTo(10);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(18)]
    public void Nested_if_on_errors_keeps_case_nesting_within_ten(int depth)
    {
        // Task 6b: a condition that can be an error (a division) adds its own CASE level (NULL first); an inline value then reaches
        // 6 levels in its apply, still within SQL Server's 10.
        var formula = "If(xrmv_qty/2>0,1/xrmv_qty,Blank())";
        for (var i = 1; i < depth; i++) formula = $"If(xrmv_qty/{i + 2}>{i},{formula}+1,Max(xrmv_qty/{i},Blank()))";
        CaseNesting(Sql("xrmv_fxdec", formula)).Should().BeLessThanOrEqualTo(10);
        CaseNesting(Sql("xrmv_fxbool", NestedIf(depth, "IsBlank(xrmv_qty/0)", "xrmv_qty/3>1"))).Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public void Nested_yes_no_values_keep_case_nesting_within_ten()
    {
        // A yes/no value used as a condition is a CASE (its value) inside a CASE (the If): Not(If(…)) nested 12 deep.
        var formula = "xrmv_qty>0";
        for (var i = 0; i < 12; i++) formula = $"Not(If({formula},xrmv_qty>{i},false))";

        CaseNesting(Sql("xrmv_fxbool", formula)).Should().BeLessThanOrEqualTo(10);
    }

    // ---- N1: applies -------------------------------------------------------------------------------------------------------

    private static int Applies(string sql) => sql.Split("OUTER APPLY").Length - 1;

    [Fact]
    public void Substitute_with_an_instance_needs_no_apply_per_instance()
    {
        // Final review N1: each instance was an apply of its own, so Substitute(x, "a", "b", 100) held 101, and 29 of them (985
        // characters) ran SQL Server out of stack space after 3 minutes (Msg 8621).
        Applies(Sql("xrmv_fxtext", "Substitute(xrmv_code,\"a\",\"b\",100)")).Should().BeLessThanOrEqualTo(3);

        var formula = string.Join("&", Enumerable.Repeat("Substitute(xrmv_code,\"a\",\"b\",100)", 29));
        formula.Length.Should().Be(985);
        var sql = Sql("xrmv_fxtext", formula);
        Applies(sql).Should().BeLessThanOrEqualTo(60);
        sql.Length.Should().BeLessThan(300_000);
        CaseNesting(sql).Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public void A_chain_of_formula_columns_past_250_applies_is_refused_when_defined()
    {
        // Each '+' of a decimal sum is computed once (the range of its intermediates): 90 applies for 90 terms, and a chain adds
        // the applies of its links. The column that passes 250 is refused when it is defined, in our words.
        var entity = TestMetadata.Instance.GetEntity(Order)!;
        var terms = string.Join("+", Enumerable.Repeat("xrmv_qty", 85));
        Define("xrmv_fxchain1", "xrmv_qty+" + terms);
        Define("xrmv_fxchain2", "xrmv_fxchain1+" + terms);
        Define("xrmv_fxchain3", "xrmv_fxchain2+" + terms);

        _computed.Check(entity, TestMetadata.Attribute(Order, "xrmv_fxchain2"), TestMetadata.Instance.GetEntity);
        Applies(Sql("xrmv_fxchain2")).Should().BeInRange(150, 250);
        var refusal = "The formula column 'xrmv_fxorder.xrmv_fxchain3' is too complex to compute: its SQL would need more than 250 " +
                      "intermediate values; simplify the formula.";
        FluentActions.Invoking(() => _computed.Check(entity, TestMetadata.Attribute(Order, "xrmv_fxchain3"), TestMetadata.Instance.GetEntity))
            .Should().Throw<Xrmd.Abstractions.XrmdValidationException>().WithMessage(refusal);
        FluentActions.Invoking(() => Sql("xrmv_fxchain3")).Should().Throw<AggregateException>()
            .WithInnerException<Xrmd.Abstractions.XrmdValidationException>().WithMessage(refusal);
    }
}
