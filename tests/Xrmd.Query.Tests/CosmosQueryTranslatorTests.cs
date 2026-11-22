using FluentAssertions;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Query.Cosmos;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

/// <summary>Plan #78, task 3: the Cosmos SQL subset is bound to the table and becomes an ordinary query model.</summary>
public sealed class CosmosQueryTranslatorTests
{
    private static readonly Microsoft.Xrm.Sdk.Metadata.EntityMetadata Table = TestMetadata.Instance.GetEntity("pb_reading")!;
    private static readonly IReadOnlyDictionary<string, object?> NoParameters = new Dictionary<string, object?>();

    private static CosmosQueryPlan Plan(string sql, IReadOnlyDictionary<string, object?>? parameters = null, int? pageSize = null,
        string? cookie = null) =>
        CosmosQueryTranslator.Translate(CosmosSqlParser.Parse(sql), Table, parameters ?? NoParameters, pageSize, cookie);

    private static FilterNode Filter(string where, IReadOnlyDictionary<string, object?>? parameters = null) =>
        Plan("SELECT c.props.pb_name FROM c WHERE " + where, parameters).Model.Root.Filter;

    private static string Fault(Action act)
    {
        var ex = act.Should().Throw<XrmdFaultException>().Which;
        ex.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        ex.HttpStatus.Should().Be(400);
        return ex.Message;
    }

    private static string Unsupported(string construct) => $"ExecuteCosmosSqlQuery: {construct} is not supported by xrmverse.";

    private static int Depth(FilterNode f) => 1 + (f.Filters.Count == 0 ? 0 : f.Filters.Max(Depth));

    [Fact]
    public void Projection_columns_and_names()
    {
        var plan = Plan("SELECT c.props.pb_name AS deviceId, c.props.pb_value power, c.props.pb_payload.list[0], c.props.pb_payload.a.b " +
                        "FROM c WHERE c.props.pb_value > 1");

        plan.Model.Root.EntityName.Should().Be("pb_reading");
        plan.Model.Root.Columns.Select(c => c.Attribute).Should().Equal("pb_readingid", "pb_name", "pb_value", "pb_payload");
        plan.Projection.Select(p => p.Name).Should().Equal("deviceId", "power", "$1", "b");
        plan.Projection[0].Should().BeEquivalentTo(new { Attribute = "pb_name", JsonSteps = Array.Empty<CosmosPathStep>() });
        plan.Projection[2].Attribute.Should().Be("pb_payload");
        plan.Projection[2].JsonSteps.Should().Equal(new PropertyStep("list"), new IndexStep(0));
    }

    [Fact]
    public void Where_only_column_is_not_selected()
    {
        var plan = Plan("SELECT c.props.pb_name FROM c WHERE c.props.pb_value > 1");
        plan.Model.Root.Columns.Select(c => c.Attribute).Should().Equal("pb_readingid", "pb_name");
    }

    [Fact]
    public void Duplicate_result_name_is_400()
    {
        Fault(() => Plan("SELECT c.props.pb_value AS power, c.props.pb_payload.power FROM c"))
            .Should().Be("ExecuteCosmosSqlQuery: duplicate result name 'power'.");
    }

    [Fact]
    public void Top_level_comparisons_become_condition_nodes()
    {
        var p = new Dictionary<string, object?> { ["@v"] = 5 };
        Filter("c.props.pb_value >= @v", p).Conditions.Should().BeEquivalentTo(new[] { new ConditionNode(null, "pb_value", ConditionOperator.GreaterEqual, [5]) }, o => o.WithStrictOrdering());
        Filter("5 < c.props.pb_value").Conditions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Attribute = "pb_value", Operator = ConditionOperator.GreaterThan });
        Filter("c.props.pb_name = 'x'").Conditions.Should().BeEquivalentTo(new[] { new ConditionNode(null, "pb_name", ConditionOperator.Equal, ["x"], true) }, o => o.WithStrictOrdering());
        Filter("c.props.pb_name != null").Conditions.Should().BeEquivalentTo(new[] { new ConditionNode(null, "pb_name", ConditionOperator.NotNull, [], true) }, o => o.WithStrictOrdering());
        Filter("IS_DEFINED(c.props.pb_value)").Conditions.Should().BeEquivalentTo(new[] { new ConditionNode(null, "pb_value", ConditionOperator.NotNull, []) }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Top_level_errors()
    {
        Fault(() => Filter("c.props.pb_value = null")).Should()
            .Be(Unsupported("a comparison of a column with null (use NOT IS_DEFINED)"));
        Fault(() => Filter("c.props.pb_payload = 'x'")).Should()
            .Be(Unsupported("a comparison of the whole JSON column 'pb_payload'"));
    }

    [Fact]
    public void Memo_column_compiles_with_the_binary_collation()
    {
        var context = new QueryContext(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc));
        string Sql(string where) => System.Text.RegularExpressions.Regex.Replace(
            new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(Plan("SELECT c.props.pb_name FROM c WHERE " + where).Model, context).Sql, @"\s+", " ");

        Sql("c.props.pb_notes = 'Abc '").Should().Contain(
            "(t0.[pb_notes] COLLATE Latin1_General_BIN2 = @p0 AND DATALENGTH(t0.[pb_notes]) = DATALENGTH(@p0))");
        Sql("c.props.pb_notes != 'Abc'").Should().Contain("t0.[pb_notes] IS NOT NULL AND NOT ((t0.[pb_notes] COLLATE Latin1_General_BIN2 = @p0");
        Sql("c.props.pb_notes > 'Abc'").Should().Contain("t0.[pb_notes] COLLATE Latin1_General_BIN2 > @p0");
    }

    [Fact]
    public void Literals_are_type_strict_on_text_and_number_columns()
    {
        var p = new Dictionary<string, object?> { ["@n"] = 5, ["@s"] = "5", ["@b"] = true };
        const string str = "ExecuteCosmosSqlQuery: the value for '{0}' must be a string.";
        const string num = "ExecuteCosmosSqlQuery: the value for '{0}' must be a number.";

        Fault(() => Filter("c.props.pb_name = 5")).Should().Be(string.Format(str, "pb_name"));
        Fault(() => Filter("c.props.pb_name = true")).Should().Be(string.Format(str, "pb_name"));
        Fault(() => Filter("c.props.pb_notes = @n", p)).Should().Be(string.Format(str, "pb_notes"));
        Fault(() => Filter("@b != c.props.pb_name", p)).Should().Be(string.Format(str, "pb_name"));
        Fault(() => Filter("c.props.pb_value = 'x'")).Should().Be(string.Format(num, "pb_value"));
        Fault(() => Filter("c.props.pb_value = @s", p)).Should().Be(string.Format(num, "pb_value"));
        Fault(() => Filter("c.props.pb_value = @b", p)).Should().Be(string.Format(num, "pb_value"));
        Fault(() => Filter("c.props.pb_value = false")).Should().Be(string.Format(num, "pb_value"));

        Filter("c.props.pb_name = 'x' AND c.props.pb_value = @n AND c.props.pb_notes = @s", p).Conditions.Should().HaveCount(3);
    }

    [Fact]
    public void Result_name_starting_with_at_sign_is_reserved()
    {
        Fault(() => Plan("SELECT c.props.pb_payload[\"@odata.type\"] FROM c")).Should()
            .Be("ExecuteCosmosSqlQuery: the result name '@odata.type' is reserved.");
        Fault(() => Plan("SELECT c.props.pb_payload[\"@x\"] FROM c")).Should()
            .Be("ExecuteCosmosSqlQuery: the result name '@x' is reserved.");
    }

    [Fact]
    public void Json_paths_become_json_nodes()
    {
        var p = new Dictionary<string, object?> { ["@s"] = "x", ["@n"] = 7L };
        var f = Filter("c.props.pb_payload.a.b > 5 AND c.props.pb_payload.a.c = @s AND c.props.pb_payload.d[2] <= 2.5 AND c.props.pb_payload.e = @n " +
                       "AND c.props.pb_payload.f = true AND c.props.pb_payload.g != null AND IS_DEFINED(c.props.pb_payload.h)", p);

        f.Conditions.Should().BeEmpty();
        f.JsonPaths.Should().Equal(
            new JsonPathConditionNode("pb_payload", "$.a", "b", JsonPathTest.Greater, 5d),
            new JsonPathConditionNode("pb_payload", "$.a", "c", JsonPathTest.Equal, "x"),
            new JsonPathConditionNode("pb_payload", "$.d", "2", JsonPathTest.LessEqual, 2.5d, true),
            new JsonPathConditionNode("pb_payload", "$", "e", JsonPathTest.Equal, 7d),
            new JsonPathConditionNode("pb_payload", "$", "f", JsonPathTest.Equal, true),
            new JsonPathConditionNode("pb_payload", "$", "g", JsonPathTest.NotEqual, null),
            new JsonPathConditionNode("pb_payload", "$", "h", JsonPathTest.Defined, null));
    }

    [Fact]
    public void Json_range_with_bool_or_null_is_400()
    {
        Fault(() => Filter("c.props.pb_payload.a > true")).Should().Be(Unsupported("a range comparison with bool"));
        Fault(() => Filter("c.props.pb_payload.a <= null")).Should().Be(Unsupported("a range comparison with null"));
    }

    [Fact]
    public void Not_over_undefined_follows_cosmos()
    {
        var f = Filter("NOT (c.props.pb_payload.n > 5)");
        f.JsonPaths.Should().Equal(new JsonPathConditionNode("pb_payload", "$", "n", JsonPathTest.LessEqual, 5d));

        var g = Filter("NOT (c.props.pb_payload.a = 1 AND c.props.pb_payload.b = 2)");
        g.Operator.Should().Be(LogicalOperator.Or);
        g.JsonPaths.Select(j => j.Test).Should().Equal(JsonPathTest.NotEqual, JsonPathTest.NotEqual);

        Filter("NOT IS_DEFINED(c.props.pb_payload.n)").JsonPaths.Should().ContainSingle().Which.Test.Should().Be(JsonPathTest.NotDefined);
        Filter("NOT IS_DEFINED(c.props.pb_value)").Conditions.Should().BeEquivalentTo(new[] { new ConditionNode(null, "pb_value", ConditionOperator.Null, []) }, o => o.WithStrictOrdering());
        Filter("NOT NOT (c.props.pb_value = 1)").Conditions.Single().Operator.Should().Be(ConditionOperator.Equal);
    }

    [Fact]
    public void Same_operator_chains_are_flattened()
    {
        var and = string.Join(" AND ", Enumerable.Range(0, 1000).Select(i => $"c.props.pb_value != {i}"));
        var f = Filter(and);
        f.Conditions.Should().HaveCount(1000);
        Depth(f).Should().Be(1);

        var mixed = string.Join(" OR ", Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? $"c.props.pb_value = {i}" : $"c.props.pb_payload.a = {i}"));
        var m = Filter(mixed);
        m.Operator.Should().Be(LogicalOperator.Or);
        (m.Conditions.Count + m.JsonPaths.Count).Should().Be(1000);
        Depth(m).Should().Be(1);

        var alternating = Filter("(c.props.pb_value = 1 OR c.props.pb_value = 2) AND (c.props.pb_value = 3 OR c.props.pb_value = 4) AND c.props.pb_value = 5");
        alternating.Operator.Should().Be(LogicalOperator.And);
        alternating.Filters.Should().HaveCount(2);
        alternating.Conditions.Should().HaveCount(1);
    }

    [Fact]
    public void Order_and_paging()
    {
        Plan("SELECT c.props.pb_name FROM c").Model.Root.Orders.Should().Equal(new OrderNode("pb_readingid", false));
        Plan("SELECT c.props.pb_name FROM c ORDER BY c.props.pb_value DESC").Model.Root.Orders
            .Should().Equal(new OrderNode("pb_value", true), new OrderNode("pb_readingid", false));
        Fault(() => Plan("SELECT c.props.pb_name FROM c ORDER BY c.props.pb_payload.x")).Should().Be(Unsupported("ORDER BY on a JSON path"));

        Fault(() => Plan("SELECT c.props.pb_name FROM c", pageSize: 0)).Should().Contain("PageSize");
        Fault(() => Plan("SELECT c.props.pb_name FROM c", pageSize: 5001)).Should().Contain("PageSize");
        Plan("SELECT c.props.pb_name FROM c").Model.PageSize.Should().BeNull();
        Plan("SELECT c.props.pb_name FROM c", pageSize: 5000).Model.PageSize.Should().Be(5000);

        var fetch = new PagingCookie(2, "pb_readingid", Guid.NewGuid(), Guid.NewGuid()).Format();
        var encoded = CosmosPagingCookie.Encode(fetch);
        encoded.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        CosmosPagingCookie.Decode(encoded).Format().Should().Be(fetch);
        var paged = Plan("SELECT c.props.pb_name FROM c", pageSize: 10, cookie: encoded).Model;
        paged.PageNumber.Should().Be(3);
        paged.PagingCookie.Should().Be(fetch);

        Fault(() => CosmosPagingCookie.Decode("%%%")).Should().Be("ExecuteCosmosSqlQuery: the PagingCookie is not valid.");
        Fault(() => Plan("SELECT c.props.pb_name FROM c", cookie: "%%%")).Should().Be("ExecuteCosmosSqlQuery: the PagingCookie is not valid.");
    }

    [Fact]
    public void Parameter_errors()
    {
        Fault(() => Filter("c.props.pb_value = @missing")).Should().Be("ExecuteCosmosSqlQuery: the parameter '@missing' has no value.");
        Fault(() => Filter("c.props.pb_value.x = 1")).Should().Be(Unsupported("a property path into the non-JSON column 'pb_value'"));
        var ex = ((Action)(() => Filter("c.props.nope = 1"))).Should().Throw<XrmdValidationException>().Which;
        ex.Message.Should().Contain("'pb_reading' entity doesn't contain attribute with Name = 'nope'");
    }

    private static Xrmd.Query.Sql.CompiledQuery Compiled(string where)
    {
        var plan = Plan("SELECT c.props.pb_name FROM c WHERE " + where);
        return new Xrmd.Query.Sql.SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(plan.Model,
            new QueryContext(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Numeric_literals_on_an_integer_column_compile()
    {
        Compiled("c.props.pb_value > 1").Sql.Should().Contain("pb_value");
        Compiled("c.props.pb_value = 2.0").Sql.Should().Contain("pb_value");
        var act = () => Compiled("c.props.pb_value = 1.5");
        act.Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void Negated_not_null_comparison_has_a_neutral_message()
    {
        Fault(() => Filter("NOT (c.props.pb_value != null)")).Should().Contain("use IS_DEFINED or NOT IS_DEFINED");
    }

    [Fact]
    public void Deeply_alternating_groups_are_refused_with_the_depth_text()
    {
        var open = string.Concat(Enumerable.Range(0, 17).Select(_ => "c.props.pb_value = 1 OR c.props.pb_value = 2 AND ("));
        var where = open + "c.props.pb_value = 3" + new string(')', 17);
        var act = () => Filter(where);
        act.Should().Throw<XrmdValidationException>().Which.Message.Should().Contain("nested more than 32 levels");
    }

    [Theory]
    [InlineData("<x/>")]
    [InlineData("not xml")]
    [InlineData("<cookie page=\"0\"><pb_readingid last=\"{00000000-0000-0000-0000-000000000001}\" first=\"{00000000-0000-0000-0000-000000000001}\" /></cookie>")]
    [InlineData("<cookie page=\"2147483647\"><pb_readingid last=\"{00000000-0000-0000-0000-000000000001}\" first=\"{00000000-0000-0000-0000-000000000001}\" /></cookie>")]
    public void Valid_base64_of_a_bad_cookie_is_refused(string fetch)
    {
        Fault(() => CosmosPagingCookie.Decode(CosmosPagingCookie.Encode(fetch))).Should().Be("ExecuteCosmosSqlQuery: the PagingCookie is not valid.");
    }

    [Theory]
    [InlineData("PGNvb2tpZT4=")]
    [InlineData("PG+vb2tpZT4")]
    public void Non_url_safe_forms_are_refused(string cookie)
    {
        Fault(() => CosmosPagingCookie.Decode(cookie)).Should().Be("ExecuteCosmosSqlQuery: the PagingCookie is not valid.");
    }
}
