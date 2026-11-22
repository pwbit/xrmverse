using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Query.Cosmos;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

/// <summary>Plan #78, task 2: JSON path conditions compile to OPENJSON over the parent path, with a typed key test.</summary>
public class JsonPathConditionCompilerTests
{
    private const string Table = "new_sensordata";

    private sealed class Unreadable : IQuerySecurity
    {
        public FilterNode? ReadFilter(EntityMetadata entity) => null;

        public IReadOnlySet<string> UnreadableColumns(EntityMetadata entity) => entity.LogicalName == Table
            ? new HashSet<string>(["new_payload"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();
    }

    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc));

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static CompiledQuery Compile(QueryModel q, QueryContext? context = null) =>
        new SqlQueryCompiler(TestMetadata.Instance, TestMetadata.Dialect).Compile(q, context ?? Context);

    private static CompiledQuery CompileNode(JsonPathConditionNode node, QueryContext? context = null)
    {
        var root = new EntityNode { EntityName = Table };
        root.Columns.Add(new ColumnNode("new_name"));
        root.Filter.JsonPaths.Add(node);
        return Compile(new QueryModel { Root = root }, context);
    }

    private static string Where(CompiledQuery c)
    {
        var sql = Normalize(c.Sql);
        var at = sql.IndexOf("WHERE ", StringComparison.Ordinal);
        return sql[(at + 6)..];
    }

    private static DbParameter P(CompiledQuery c, string name) => c.Parameters.Single(p => p.ParameterName == name);

    private const string Open = "EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(t0.[new_payload]) = 1 THEN JSON_QUERY(t0.[new_payload], @p0) END) AS j WHERE j.[key] = @p1 AND DATALENGTH(j.[key]) = DATALENGTH(@p1) AND ISJSON(CASE WHEN ISJSON(t0.[new_payload]) = 1 THEN JSON_QUERY(t0.[new_payload], @p0) END, OBJECT) = 1";

    private static JsonPathConditionNode Node(JsonPathTest test, object? value, string parent = "$.a", string key = "b") =>
        new("new_payload", parent, key, test, value);

    [Fact]
    public void String_equality_is_type_checked_binary_and_length_guarded()
    {
        var c = CompileNode(Node(JsonPathTest.Equal, "x"));

        Where(c).Should().Contain(
            "(ISJSON(t0.[new_payload]) = 1 AND EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(t0.[new_payload]) = 1 THEN JSON_QUERY(t0.[new_payload], @p0) END) AS j " +
            "WHERE j.[key] = @p1 AND DATALENGTH(j.[key]) = DATALENGTH(@p1) AND ISJSON(CASE WHEN ISJSON(t0.[new_payload]) = 1 THEN JSON_QUERY(t0.[new_payload], @p0) END, OBJECT) = 1 AND j.[type] = 1 AND (j.[value] COLLATE Latin1_General_BIN2 = @p2 AND DATALENGTH(j.[value]) = DATALENGTH(@p2))))");
        P(c, "@p0").Value.Should().Be("$.a");
        P(c, "@p1").Value.Should().Be("b");
        P(c, "@p2").Value.Should().Be("x");
        P(c, "@p2").DbType.Should().Be(DbType.String);
    }

    [Fact]
    public void Index_key_requires_an_array_parent()
    {
        var node = new JsonPathConditionNode("new_payload", "$.a", "2", JsonPathTest.Defined, null, KeyIsIndex: true);

        var where = Where(CompileNode(node));
        where.Should().Contain($"ISJSON({"""CASE WHEN ISJSON(t0.[new_payload]) = 1 THEN JSON_QUERY(t0.[new_payload], @p0) END"""}, ARRAY) = 1").And.NotContain("OBJECT");
    }

    [Fact]
    public void Number_comparison_casts_to_float()
    {
        var c = CompileNode(Node(JsonPathTest.Greater, 5.5));

        Where(c).Should().Contain($"(ISJSON(t0.[new_payload]) = 1 AND {Open} AND j.[type] = 2 AND TRY_CAST(j.[value] AS float) > @p2))");
        P(c, "@p2").DbType.Should().Be(DbType.Double);
        P(c, "@p2").Value.Should().Be(5.5);
    }

    [Theory]
    [InlineData(JsonPathTest.Equal, "=")]
    [InlineData(JsonPathTest.Less, "<")]
    [InlineData(JsonPathTest.LessEqual, "<=")]
    [InlineData(JsonPathTest.Greater, ">")]
    [InlineData(JsonPathTest.GreaterEqual, ">=")]
    public void Number_operators_map(JsonPathTest test, string op) =>
        Where(CompileNode(Node(test, 1.0))).Should().Contain($"TRY_CAST(j.[value] AS float) {op} @p2");

    [Fact]
    public void String_range_uses_binary_collation()
    {
        var c = CompileNode(Node(JsonPathTest.GreaterEqual, "m"));

        Where(c).Should().Contain("j.[type] = 1 AND j.[value] COLLATE Latin1_General_BIN2 >= @p2))");
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Bool_equality_compares_the_literal(bool value, string literal)
    {
        var c = CompileNode(Node(JsonPathTest.Equal, value));

        Where(c).Should().Contain($"{Open} AND j.[type] = 3 AND j.[value] = '{literal}'))");
        c.Parameters.Select(p => p.ParameterName).Where(n => n.StartsWith("@p")).Should().BeEquivalentTo(["@p0", "@p1"]);
    }

    [Fact]
    public void Null_equality_tests_the_json_null_type()
    {
        var c = CompileNode(Node(JsonPathTest.Equal, null));

        Where(c).Should().Contain($"{Open} AND j.[type] = 0))");
        c.Parameters.Select(p => p.ParameterName).Where(n => n.StartsWith("@p")).Should().BeEquivalentTo(["@p0", "@p1"]);
    }

    [Fact]
    public void NotEqual_negates_the_typed_test_inside_the_exists()
    {
        var c = CompileNode(Node(JsonPathTest.NotEqual, 5.0));

        Where(c).Should().Contain($"(ISJSON(t0.[new_payload]) = 1 AND {Open} AND NOT (j.[type] = 2 AND TRY_CAST(j.[value] AS float) = @p2)))");
    }

    [Fact]
    public void NotEqual_null_negates_the_null_type()
    {
        Where(CompileNode(Node(JsonPathTest.NotEqual, null))).Should().Contain($"{Open} AND NOT (j.[type] = 0)))");
    }

    [Fact]
    public void Defined_is_the_key_test_only()
    {
        var c = CompileNode(Node(JsonPathTest.Defined, null));

        Where(c).Should().Contain($"(ISJSON(t0.[new_payload]) = 1 AND {Open}))");
        c.Parameters.Select(p => p.ParameterName).Where(n => n.StartsWith("@p")).Should().BeEquivalentTo(["@p0", "@p1"]);
    }

    [Fact]
    public void NotDefined_matches_a_null_or_invalid_column()
    {
        var c = CompileNode(Node(JsonPathTest.NotDefined, "ignored"));

        Where(c).Should().Contain($"(t0.[new_payload] IS NULL OR ISJSON(t0.[new_payload]) = 0 OR NOT {Open}))");
        c.Parameters.Select(p => p.ParameterName).Where(n => n.StartsWith("@p")).Should().BeEquivalentTo(["@p0", "@p1"]);
    }

    [Fact]
    public void Path_and_key_travel_only_as_parameters()
    {
        var c = CompileNode(Node(JsonPathTest.Equal, "v'; DROP TABLE x;--", "$.\"x\"\"y\"", "a'b"));

        var sql = c.Sql;
        sql.Should().NotContain("a'b").And.NotContain("x\"\"y").And.NotContain("DROP");
        P(c, "@p0").Value.Should().Be("$.\"x\"\"y\"");
        P(c, "@p1").Value.Should().Be("a'b");
    }

    [Fact]
    public void Range_comparison_of_bool_or_null_is_refused()
    {
        var act = () => CompileNode(Node(JsonPathTest.Less, true));
        act.Should().Throw<XrmdValidationException>();
        var act2 = () => CompileNode(Node(JsonPathTest.Greater, null));
        act2.Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void Path_condition_on_a_non_json_column_is_refused()
    {
        var act = () => CompileNode(new JsonPathConditionNode("new_name", "$", "a", JsonPathTest.Defined, null));
        act.Should().Throw<XrmdValidationException>().WithMessage("Attribute 'new_name' does not hold JSON.");
    }

    [Fact]
    public void Path_condition_on_an_unreadable_column_is_a_field_read_denial()
    {
        var context = new QueryContext(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow) { Security = new Unreadable() };

        var act = () => CompileNode(Node(JsonPathTest.Defined, null), context);

        act.Should().Throw<XrmdFaultException>().Which.ErrorCode.Should().Be(unchecked((int)0x8004F504));
    }

    [Fact]
    public void Json_paths_combine_with_the_conditions_by_the_filter_operator()
    {
        var root = new EntityNode { EntityName = Table };
        root.Filter.Operator = LogicalOperator.Or;
        root.Filter.Conditions.Add(new ConditionNode(null, "new_name", ConditionOperator.Equal, ["n"]));
        root.Filter.JsonPaths.Add(Node(JsonPathTest.Defined, null));

        var where = Where(Compile(new QueryModel { Root = root }));

        where.Should().Contain("(t0.[new_name] = @p0 OR (ISJSON(t0.[new_payload]) = 1 AND ");
    }

    // ---- PostgreSQL (plan 2.7b, Task 5) ------------------------------------------------------------------------------

    private static CompiledQuery CompilePg(JsonPathConditionNode node)
    {
        var root = new EntityNode { EntityName = Table };
        root.Columns.Add(new ColumnNode("new_name"));
        root.Filter.JsonPaths.Add(node);
        return new SqlQueryCompiler(TestMetadata.Instance, Xrmd.Storage.PostgreSql.PostgresDialect.Instance).Compile(new QueryModel { Root = root }, Context);
    }

    private const string PgValid = "pg_input_is_valid(CAST(t0.\"new_payload\" AS text), 'jsonb')";

    private const string PgParent =
        "(SELECT CASE WHEN " + PgValid + " THEN jsonb_path_query_first(CAST(t0.\"new_payload\" AS jsonb), CAST('strict ' || @p0 AS jsonpath), '{}', true) END AS p) AS jp";

    private const string PgType =
        "CASE jsonb_typeof(e.v) WHEN 'null' THEN 0 WHEN 'string' THEN 1 WHEN 'number' THEN 2 WHEN 'boolean' THEN 3 WHEN 'array' THEN 4 ELSE 5 END AS \"type\"";

    private const string PgObjectMember =
        "EXISTS (SELECT 1 FROM " + PgParent + " CROSS JOIN LATERAL (SELECT e.k AS \"key\", " + PgType + ", e.v #>> '{}' AS \"value\" " +
        "FROM jsonb_each(CASE WHEN jsonb_typeof(jp.p) = 'object' THEN jp.p END) AS e(k, v)) AS j " +
        "WHERE (CAST(j.\"key\" AS text) COLLATE \"C\" = CAST(@p1 AS text) COLLATE \"C\")";

    [Fact]
    public void Postgresql_reads_the_parent_with_a_strict_silent_json_path_and_lists_the_object_members()
    {
        var c = CompilePg(Node(JsonPathTest.Equal, "x"));

        Where(c).Should().Contain(
            $"({PgValid} AND {PgObjectMember} AND j.\"type\" = 1 AND (CAST(j.\"value\" AS text) COLLATE \"C\" = CAST(@p2 AS text) COLLATE \"C\")))");
        c.Sql.Should().NotContain("[").And.NotContain("OPENJSON");
        P(c, "@p0").Value.Should().Be("$.a");
        P(c, "@p1").Value.Should().Be("b");
    }

    [Fact]
    public void Postgresql_index_key_lists_the_array_elements_from_zero()
    {
        var where = Where(CompilePg(new JsonPathConditionNode("new_payload", "$.a", "2", JsonPathTest.Defined, null, KeyIsIndex: true)));

        where.Should().Contain("FROM jsonb_array_elements(CASE WHEN jsonb_typeof(jp.p) = 'array' THEN jp.p END) WITH ORDINALITY AS e(v, n)")
            .And.Contain("SELECT CAST(e.n - 1 AS text) AS \"key\"").And.NotContain("jsonb_each");
    }

    [Fact]
    public void Postgresql_number_comparison_goes_through_try_float()
    {
        var where = Where(CompilePg(Node(JsonPathTest.Greater, 5.5)));

        where.Should().Contain("j.\"type\" = 2 AND (SELECT CASE WHEN tt.t ~").And.Contain("\"data\".\"try_float\"(trim(tt.t))")
            .And.Contain("FROM (VALUES (CAST(j.\"value\" AS text))) AS tt(t)) > @p2))");
    }

    [Fact]
    public void Postgresql_not_defined_matches_a_null_or_invalid_column()
    {
        Where(CompilePg(Node(JsonPathTest.NotDefined, null))).Should().Contain(
            $"(t0.\"new_payload\" IS NULL OR NOT {PgValid} OR NOT {PgObjectMember}))");
    }

    [Fact]
    public void Postgresql_bool_and_string_range_comparisons()
    {
        Where(CompilePg(Node(JsonPathTest.Equal, true))).Should().Contain("j.\"type\" = 3 AND j.\"value\" = 'true'))");
        Where(CompilePg(Node(JsonPathTest.GreaterEqual, "m"))).Should().Contain("j.\"type\" = 1 AND CAST(j.\"value\" AS text) COLLATE \"C\" >= @p2))");
    }

    // ---- BinaryCollation on every string comparison ----------------------------------------------------------------

    private static string CompileCondition(ConditionOperator op, params object?[] values)
    {
        var root = new EntityNode { EntityName = Table };
        root.Filter.Conditions.Add(new ConditionNode(null, "new_name", op, values, BinaryCollation: true));
        return Where(Compile(new QueryModel { Root = root }));
    }

    [Theory]
    [InlineData(ConditionOperator.GreaterThan, ">")]
    [InlineData(ConditionOperator.GreaterEqual, ">=")]
    [InlineData(ConditionOperator.LessThan, "<")]
    [InlineData(ConditionOperator.LessEqual, "<=")]
    public void Binary_collation_applies_to_range_comparisons(ConditionOperator op, string sql) =>
        CompileCondition(op, "m").Should().Contain($"t0.[new_name] COLLATE Latin1_General_BIN2 {sql} @p0");

    [Fact]
    public void Binary_collation_equality_keeps_the_length_guard() =>
        CompileCondition(ConditionOperator.Equal, "m").Should().Contain(
            "(t0.[new_name] COLLATE Latin1_General_BIN2 = @p0 AND DATALENGTH(t0.[new_name]) = DATALENGTH(@p0))");

    [Fact]
    public void Binary_collation_not_equal_never_matches_null() =>
        CompileCondition(ConditionOperator.NotEqual, "m").Should().Contain(
            "(t0.[new_name] IS NOT NULL AND NOT ((t0.[new_name] COLLATE Latin1_General_BIN2 = @p0 AND DATALENGTH(t0.[new_name]) = DATALENGTH(@p0))))");

    // ---- JsonPaths.Format --------------------------------------------------------------------------------------------

    private static string Format(params CosmosPathStep[] steps) => JsonPaths.Format(steps);

    [Fact]
    public void Format_uses_dot_notation_for_identifiers_and_brackets_for_indexes()
    {
        Format().Should().Be("$");
        Format(new PropertyStep("a"), new PropertyStep("_b2"), new IndexStep(3), new PropertyStep("C")).Should().Be("$.a._b2[3].C");
    }

    [Theory]
    [InlineData("a\"b", "$.\"a\\\"b\"")]
    [InlineData("a\\b", "$.\"a\\\\b\"")]
    [InlineData("a.b", "$.\"a.b\"")]
    [InlineData("a b", "$.\"a b\"")]
    [InlineData("$a", "$.\"$a\"")]
    [InlineData("$", "$.\"$\"")]
    [InlineData("", "$.\"\"")]
    [InlineData("1a", "$.\"1a\"")]
    [InlineData("caffè", "$.\"caffè\"")]
    [InlineData("a\"\\b", "$.\"a\\\"\\\\b\"")]
    public void Format_quotes_and_escapes_keys_that_are_not_identifiers(string key, string expected) =>
        Format(new PropertyStep(key)).Should().Be(expected);

    [Fact]
    public void Format_escapes_newline_and_tab_with_short_escapes() =>
        Format(new PropertyStep("a\nb\r\t")).Should().Be("$.\"a\\nb\\r\\t\"");

    [Fact]
    public void Format_refuses_other_control_characters() =>
        ((Action)(() => Format(new PropertyStep("a\u0001b")))).Should().Throw<Xrmd.Abstractions.XrmdValidationException>();

    [Fact]
    public void Format_quotes_a_key_in_the_middle_of_a_path() =>
        Format(new PropertyStep("a b"), new IndexStep(2), new PropertyStep("c\"d")).Should().Be("$.\"a b\"[2].\"c\\\"d\"");
}
