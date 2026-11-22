using FluentAssertions;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Query.Cosmos;

namespace Xrmd.Query.Tests;

/// <summary>The tokenizer and parser of the ExecuteCosmosSqlQuery subset (plan #78, rulings J1 and J2).</summary>
public sealed class CosmosSqlParserTests
{
    private static CosmosPath P(params object[] steps) => new(steps.Select(s => s is int i ? (CosmosPathStep)new IndexStep(i) : new PropertyStep((string)s)).ToList());

    private static PathOperand Op(params object[] steps) => new(P(steps));

    private static string Fault(string text)
    {
        var act = () => CosmosSqlParser.Parse(text);
        var ex = act.Should().Throw<XrmdFaultException>().Which;
        ex.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        ex.HttpStatus.Should().Be(400);
        return ex.Message;
    }

    [Fact]
    public void Parses_projection_filter_and_order()
    {
        var q = CosmosSqlParser.Parse("select c.props.new_deviceid as deviceId, c.props.new_energy.power from c where c.props.new_type='Humidity' and c.props.new_energy.power > 5 order by c.props.createdon desc");

        q.SourceAlias.Should().Be("c");
        q.Select.Should().HaveCount(2);
        q.Select[0].Alias.Should().Be("deviceId");
        q.Select[0].Path.Should().Be(P("props", "new_deviceid"));
        q.Select[1].Alias.Should().BeNull();
        q.Select[1].Path.Should().Be(P("props", "new_energy", "power"));
        q.Where.Should().Be(new And(
            new Comparison(Op("props", "new_type"), CosmosComparisonOp.Equal, new LiteralOperand("Humidity")),
            new Comparison(Op("props", "new_energy", "power"), CosmosComparisonOp.Greater, new LiteralOperand(5m))));
        q.OrderBy.Should().Be(new CosmosOrder(P("props", "createdon"), true));
    }

    [Fact]
    public void Accepts_implicit_alias_other_source_alias_and_ascending_order()
    {
        var q = CosmosSqlParser.Parse("SELECT x.props.a a1, x.props.b FROM x ORDER BY x.props.a ASC");

        q.SourceAlias.Should().Be("x");
        q.Select[0].Alias.Should().Be("a1");
        q.Where.Should().BeNull();
        q.OrderBy!.Descending.Should().BeFalse();
    }

    [Fact]
    public void Parses_paths()
    {
        var q = CosmosSqlParser.Parse("select c.props.j[\"a b\"][2].x, c.props.j['q'] from c");

        q.Select[0].Path.Should().Be(P("props", "j", "a b", 2, "x"));
        q.Select[1].Path.Should().Be(P("props", "j", "q"));
    }

    [Theory]
    [InlineData("c.props.x = \"it\\'s\"", "it's")]
    [InlineData("c.props.x = 'é'", "é")]
    [InlineData("c.props.x = 'a\\\\b\\/c\\n\\t'", "a\\b/c\n\t")]
    [InlineData("c.props.x = '\\u0041\\\"'", "A\"")]
    [InlineData("c.props.x = ''", "")]
    public void Decodes_string_literals(string where, string expected)
    {
        var q = CosmosSqlParser.Parse($"select c.props.x from c where {where}");

        q.Where.Should().Be(new Comparison(Op("props", "x"), CosmosComparisonOp.Equal, new LiteralOperand(expected)));
    }

    [Theory]
    [InlineData("-1.5e2", -150)]
    [InlineData("42", 42)]
    [InlineData("0.25", 0.25)]
    [InlineData("+7", 7)]
    [InlineData("1E3", 1000)]
    public void Parses_numbers(string text, double expected)
    {
        var q = CosmosSqlParser.Parse($"select c.props.x from c where c.props.x = {text}");

        ((Comparison)q.Where!).Right.Should().Be(new LiteralOperand((decimal)expected));
    }

    [Theory]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    public void Parses_keyword_literals(string text, object? expected)
    {
        var q = CosmosSqlParser.Parse($"select c.props.x from c where c.props.x != {text}");

        q.Where.Should().Be(new Comparison(Op("props", "x"), CosmosComparisonOp.NotEqual, new LiteralOperand(expected)));
    }

    [Theory]
    [InlineData("=", CosmosComparisonOp.Equal)]
    [InlineData("!=", CosmosComparisonOp.NotEqual)]
    [InlineData("<>", CosmosComparisonOp.NotEqual)]
    [InlineData("<", CosmosComparisonOp.Less)]
    [InlineData("<=", CosmosComparisonOp.LessEqual)]
    [InlineData(">", CosmosComparisonOp.Greater)]
    [InlineData(">=", CosmosComparisonOp.GreaterEqual)]
    public void Parses_comparison_operators(string op, CosmosComparisonOp expected)
    {
        var q = CosmosSqlParser.Parse($"select c.props.x from c where c.props.x {op} 1");

        ((Comparison)q.Where!).Op.Should().Be(expected);
    }

    [Fact]
    public void Parses_parameters_and_keeps_a_literal_on_the_left()
    {
        var q = CosmosSqlParser.Parse("select c.props.a from c where c.props.a = @p1 and 5 < c.props.x");

        q.Where.Should().Be(new And(
            new Comparison(Op("props", "a"), CosmosComparisonOp.Equal, new ParameterOperand("@p1")),
            new Comparison(new LiteralOperand(5m), CosmosComparisonOp.Less, Op("props", "x"))));
    }

    [Fact]
    public void Parses_is_defined_and_parentheses()
    {
        var q = CosmosSqlParser.Parse("select c.props.a from c where (is_defined(c.props.j.n) or c.props.a = 1) and NOT IS_DEFINED(c.props.z)");

        q.Where.Should().Be(new And(
            new Or(new IsDefined(P("props", "j", "n")), new Comparison(Op("props", "a"), CosmosComparisonOp.Equal, new LiteralOperand(1m))),
            new Not(new IsDefined(P("props", "z")))));
    }

    [Fact]
    public void Precedence_is_not_and_or()
    {
        var q = CosmosSqlParser.Parse("select c.props.a from c where c.props.a = 1 OR NOT c.props.b = 2 AND c.props.c = 3");

        static Comparison C(string n, decimal v) => new(Op("props", n), CosmosComparisonOp.Equal, new LiteralOperand(v));
        q.Where.Should().Be(new Or(C("a", 1), new And(new Not(C("b", 2)), C("c", 3))));
    }

    [Theory]
    [InlineData("SELECT * FROM c", "SELECT *")]
    [InlineData("SELECT VALUE c.props.a FROM c", "SELECT VALUE")]
    [InlineData("select distinct c.props.a from c", "DISTINCT")]
    [InlineData("select top 5 c.props.a from c", "TOP")]
    [InlineData("select c.props.a from c offset 0 limit 5", "OFFSET LIMIT")]
    [InlineData("select c.props.a from c group by c.props.a", "GROUP BY")]
    [InlineData("select c.props.a from c join t in c.props.tags", "JOIN")]
    [InlineData("select c.props.a from c where exists(select 1)", "EXISTS")]
    [InlineData("select c.props.a from c where not exists(select 1)", "EXISTS")]
    [InlineData("select c.props.a from c where (select 1) = 1", "a subquery")]
    [InlineData("select c.props.a from (select 1) c", "a subquery")]
    [InlineData("select c.props.a from c where c.props.a in (1,2)", "IN")]
    [InlineData("select c.props.a from c where c.props.a not in (1,2)", "IN")]
    [InlineData("select c.props.a from c where c.props.a between 1 and 2", "BETWEEN")]
    [InlineData("select c.props.a from c where c.props.a like 'x%'", "LIKE")]
    [InlineData("select c.props.a from c where c.props.a not like 'x%'", "LIKE")]
    [InlineData("select count(1) from c", "function COUNT")]
    [InlineData("select c.props.a from c where contains(c.props.a, 'x')", "function CONTAINS")]
    [InlineData("select c.props.a from c where udf.myfn(c.props.a) = 1", "function udf.myfn")]
    [InlineData("select c.props.a from c where c.props.a + 1 > 2", "an arithmetic operator")]
    [InlineData("select c.props.a from c where c.props.a = 1 * 2", "an arithmetic operator")]
    [InlineData("select c.props.a from c where c.props.a = 4 % 2", "an arithmetic operator")]
    [InlineData("select c.props.a from c where c.props.a = 4 - 2", "an arithmetic operator")]
    [InlineData("select c.props.a from c where c.props.a = 'a' || 'b'", "string concatenation (||)")]
    [InlineData("select c.props.a from c where c.props.a ?? 1 = 1", "the ?? operator")]
    [InlineData("select c.props.a from c where c.props.a = 1 ? 1 : 2", "the ?: operator")]
    [InlineData("select c.props.a from c where c.props.a = {\"a\": 1}", "an object or array literal")]
    [InlineData("select c.props.a from c where c.props.a = [1, 2]", "an object or array literal")]
    [InlineData("select c.props.a from c where c.props.a = c.props.b", "a comparison between two properties")]
    [InlineData("select c.props.a from c where c.props.a = undefined", "undefined")]
    [InlineData("select c.props.a from c where 1 = 1", "a comparison without a property")]
    [InlineData("select c.props.a from c where @a = 'x'", "a comparison without a property")]
    [InlineData("select c.props.a from c order by c.props.a, c.props.b", "ORDER BY with more than one item")]
    [InlineData("select c.id from c", "a property outside c.props")]
    [InlineData("select c._ts from c", "a property outside c.props")]
    [InlineData("select c.props from c", "a property outside c.props")]
    [InlineData("select c from c", "a property outside c.props")]
    [InlineData("select c.props.a from c where c.id = 'x'", "a property outside c.props")]
    [InlineData("select c.props.a from c order by c.id", "a property outside c.props")]
    public void Refuses_construct(string text, string construct)
    {
        Fault(text).Should().Be($"ExecuteCosmosSqlQuery: {construct} is not supported by xrmverse.");
    }

    [Theory]
    [InlineData("select c.props.a fro c", 17)]
    [InlineData("select c.props.a from", 21)]
    [InlineData("select from c", 7)]
    [InlineData("select c.props.a from c where", 29)]
    [InlineData("select c.props.a from c where c.props.a", 39)]
    [InlineData("select c.props.a from c where c.props.a = 'abc", 42)]
    [InlineData("select c.props.a from c where c.props.a = 1 2", 44)]
    [InlineData("select c.props.a from c where (c.props.a = 1", 44)]
    [InlineData("select c.props.a from c where c.props.j[-1] = 1", 40)]
    [InlineData("select c.props.a from c where c.props.j[1.5] = 1", 40)]
    [InlineData("select c.props.a from c where c.props.j[@p] = 1", 40)]
    [InlineData("select c.props.a from c where c.props.j[1 = 1", 42)]
    [InlineData("select c.props.a from c where c.props.j[\"a\" = 1", 44)]
    [InlineData("select c.props.a from c where c.props.a = 1e", 42)]
    [InlineData("select c.props.a from c where c.props.a = 'a\\x'", 42)]
    [InlineData("select c.props.a from c where c.props.a = 'a\\u12'", 42)]
    [InlineData("select c.props.a from c where c.props.a = '\\ud800'", 42)]
    [InlineData("select c.props.a from c where c.props.a = '\\udc00x'", 42)]
    [InlineData("select c.props.a from c where c.props.a = 1e30", 42)]
    [InlineData("select c.props.a from c where c.props.a = 1e400", 42)]
    [InlineData("select c.props.a from c where c.props.a = 1e-400", 42)]
    [InlineData("select c.props.a from c where c.props.a = 1 #", 44)]
    [InlineData("select c.props.a from c where d.props.a = 1", 30)]
    [InlineData("select c.props.a from c order by", 32)]
    [InlineData("", 0)]
    public void Syntax_error_reports_position(string text, int position)
    {
        Fault(text).Should().StartWith($"ExecuteCosmosSqlQuery: syntax error at position {position}: ").And.EndWith(".");
    }

    [Fact]
    public void Nesting_beyond_the_filter_depth_limit_is_a_validation_error()
    {
        var open = new string('(', QueryLimits.MaxFilterDepth + 1);
        var close = new string(')', QueryLimits.MaxFilterDepth + 1);
        var act = () => CosmosSqlParser.Parse($"select c.props.a from c where {open}c.props.a = 1{close}");

        act.Should().Throw<XrmdValidationException>().WithMessage("*nested more than*");

        var ok = new string('(', QueryLimits.MaxFilterDepth);
        var okClose = new string(')', QueryLimits.MaxFilterDepth);
        CosmosSqlParser.Parse($"select c.props.a from c where {ok}c.props.a = 1{okClose}").Where.Should().BeOfType<Comparison>();
    }

    [Fact]
    public void Long_not_chains_are_limited_like_nesting()
    {
        var act = () => CosmosSqlParser.Parse($"select c.props.a from c where {string.Concat(Enumerable.Repeat("not ", 40))}c.props.a = 1");

        act.Should().Throw<XrmdValidationException>().WithMessage("*nested more than*");
    }

    [Fact]
    public void Long_and_chains_do_not_overflow_the_stack()
    {
        var text = "select c.props.a from c where " + string.Join(" and ", Enumerable.Repeat("c.props.a = 1", 200));

        CosmosSqlParser.Parse(text).Where.Should().BeOfType<And>();
    }

    [Fact]
    public void Lone_surrogate_is_a_syntax_error_but_a_pair_is_fine()
    {
        Fault("select c.props.a from c where c.props.a = '\ud83d'").Should().Contain("unpaired surrogate");
        Fault("select c.props.a from c where c.props.a = '" + '\ud83d' + "x'").Should().Contain("unpaired surrogate");
        Fault("select c.props.a from c where c.props.a = '" + '\ude00' + "'").Should().Contain("unpaired surrogate");

        var q = CosmosSqlParser.Parse("select c.props.a from c where c.props.a = '\ud83d\ude00' or c.props.a = '\ud83d\ude00'");
        ((Comparison)((Or)q.Where!).Left).Right.Should().Be(new LiteralOperand("\ud83d\ude00"));
    }

    [Theory]
    [InlineData("1e30")]
    [InlineData("1e400")]
    [InlineData("1e-400")]
    public void Out_of_range_numbers_are_refused(string number)
    {
        Fault($"select c.props.a from c where c.props.a = {number}").Should().Contain("the number is out of range");
    }

    [Theory]
    [InlineData("-0")]
    [InlineData("0e-400")]
    [InlineData("0.000")]
    public void Zero_parses_as_zero(string number)
    {
        var q = CosmosSqlParser.Parse($"select c.props.a from c where c.props.a = {number}");

        ((LiteralOperand)((Comparison)q.Where!).Right).Value.Should().Be(0m);
    }

    [Theory]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void Connective_cap_is_1000(int connectives, bool ok)
    {
        var text = "select c.props.a from c where " + string.Join(" and ", Enumerable.Repeat("c.props.a = 1", connectives + 1));
        var act = () => CosmosSqlParser.Parse(text);

        if (ok) act.Should().NotThrow();
        else act.Should().Throw<XrmdFaultException>().WithMessage("*more than 1000 AND/OR operators*");
    }
}
