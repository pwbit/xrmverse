using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Query;
using Xrmd.Query.Cosmos;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>Plan #78, task 2: the SQL of <see cref="JsonPathConditionNode"/> on the test engine (plan 2.7b, Task 5), on rows of an elastic table.</summary>
[Collection("xrmd")]
public sealed class JsonPathConditionSqlTests(XrmdFixture fx)
{
    private static readonly SemaphoreSlim TableLock = new(1, 1);
    private static string? _table;

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private async Task<string> TableAsync()
    {
        await TableLock.WaitAsync();
        try
        {
            if (_table is not null) return _table;
            var name = "new_jsonpath" + Guid.NewGuid().ToString("N")[..6];
            var entity = Entity(name, name, "Json path");
            entity.TableType = "Elastic";
            await Admin.ExecuteAsync(new CreateEntityRequest
            {
                Entity = entity,
                PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
            });
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new StringAttributeMetadata
                {
                    LogicalName = "new_payload", SchemaName = "new_Payload", DisplayName = Label("Payload"), MaxLength = 4000,
                    FormatName = StringFormatName.Json,
                },
            });
            return _table = name;
        }
        finally
        {
            TableLock.Release();
        }
    }

    /// <summary>Creates the rows (label to JSON, null for no value) under a fresh tag; returns the tag.</summary>
    private async Task<string> SeedAsync(string table, params (string Label, string? Json)[] rows)
    {
        var tag = "t" + Guid.NewGuid().ToString("N")[..8] + ":";
        foreach (var (label, json) in rows)
        {
            var e = new Entity(table) { ["new_name"] = tag + label };
            if (json is not null) e["new_payload"] = json;
            await Admin.CreateAsync(e);
        }
        return tag;
    }

    private async Task RawUpdateAsync(string table, string name, string payload)
    {
        await using var conn = await fx.App.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var cmd = TestSql.Command(conn, $"UPDATE [data].[{table}] SET [new_payload] = @v WHERE [new_name] = @n", ("@v", payload), ("@n", name));
        (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
    }

    /// <summary>The labels of the tagged rows that satisfy all the nodes, ordered.</summary>
    private async Task<string[]> MatchAsync(string table, string tag, params JsonPathConditionNode[] nodes)
    {
        var root = new EntityNode { EntityName = table };
        root.Columns.Add(new ColumnNode("new_name"));
        root.Filter.Conditions.Add(new ConditionNode(null, "new_name", ConditionOperator.BeginsWith, [tag]));
        root.Filter.JsonPaths.AddRange(nodes);
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        var compiled = new SqlQueryCompiler(store, factory.Dialect).Compile(new QueryModel { Root = root },
            new QueryContext(Guid.Empty, Guid.Empty, DateTime.UtcNow));
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.Command(compiled.Sql);
        cmd.AddParameters(compiled.Parameters.ToArray());
        await using var rd = await cmd.ExecuteReaderAsync();
        var names = new List<string>();
        var ordinal = rd.GetOrdinal(compiled.Columns.Single(c => c.ResultName == "new_name").SqlAlias);
        while (await rd.ReadAsync()) names.Add(rd.GetString(ordinal)[tag.Length..]);
        names.Sort(StringComparer.Ordinal);
        return [.. names];
    }

    private static JsonPathConditionNode N(JsonPathTest test, object? value, string parent = "$", string key = "n") =>
        new("new_payload", parent, key, test, value);

    [Fact]
    public async Task Comparisons_are_type_strict()
    {
        var table = await TableAsync();
        var tag = await SeedAsync(table,
            ("num12", """{"n":12}"""), ("str12", """{"n":"12"}"""), ("num5", """{"n":5}"""), ("str5", """{"n":"5"}"""),
            ("true", """{"n":true}"""), ("null", """{"n":null}"""), ("missing", """{"m":1}"""), ("strA", """{"n":"a"}"""),
            ("strAs", """{"n":"a "}"""), ("strUpper", """{"n":"A"}"""), ("arr", """{"n":[5]}"""));

        (await MatchAsync(table, tag, N(JsonPathTest.Greater, 5.0))).Should().Equal("num12");
        (await MatchAsync(table, tag, N(JsonPathTest.GreaterEqual, 5.0))).Should().Equal("num12", "num5");
        (await MatchAsync(table, tag, N(JsonPathTest.Less, 12.0))).Should().Equal("num5");
        (await MatchAsync(table, tag, N(JsonPathTest.Equal, 5.0))).Should().Equal("num5");
        (await MatchAsync(table, tag, N(JsonPathTest.Equal, "12"))).Should().Equal("str12");
        (await MatchAsync(table, tag, N(JsonPathTest.Greater, "12"))).Should().Equal("str5", "strA", "strAs", "strUpper");
        (await MatchAsync(table, tag, N(JsonPathTest.Equal, "a"))).Should().Equal("strA");
        (await MatchAsync(table, tag, N(JsonPathTest.Equal, true))).Should().Equal("true");
        (await MatchAsync(table, tag, N(JsonPathTest.Equal, null))).Should().Equal("null");
        // != matches every defined value that is not equal, other types included; a missing key never matches.
        (await MatchAsync(table, tag, N(JsonPathTest.NotEqual, 5.0))).Should().Equal(
            "arr", "null", "num12", "str12", "str5", "strA", "strAs", "strUpper", "true");
        (await MatchAsync(table, tag, N(JsonPathTest.Defined, null))).Should().HaveCount(10).And.NotContain("missing");
        (await MatchAsync(table, tag, N(JsonPathTest.NotDefined, null))).Should().Equal("missing");
    }

    [Fact]
    public async Task Path_through_scalar_or_invalid_json_does_not_match()
    {
        var table = await TableAsync();
        var tag = await SeedAsync(table, ("scalar", """{"a":5}"""), ("array", """{"a":[1]}"""), ("empty", "{}"), ("null", null), ("bad", "{}"));
        await RawUpdateAsync(table, tag + "bad", "not json");

        var defined = N(JsonPathTest.Defined, null, "$.a", "b");
        (await MatchAsync(table, tag, defined)).Should().BeEmpty();
        (await MatchAsync(table, tag, N(JsonPathTest.Equal, 1.0, "$.a", "b"))).Should().BeEmpty();
        (await MatchAsync(table, tag, N(JsonPathTest.NotEqual, 1.0, "$.a", "b"))).Should().BeEmpty();
        (await MatchAsync(table, tag, N(JsonPathTest.NotDefined, null, "$.a", "b"))).Should().Equal("array", "bad", "empty", "null", "scalar");
        // a path into the stored value that is not JSON at all
        (await MatchAsync(table, tag, N(JsonPathTest.Defined, null, "$", "a"))).Should().Equal("array", "scalar");
    }

    [Fact]
    public async Task Index_key_requires_an_array_and_control_characters_in_keys_run()
    {
        var table = await TableAsync();
        var tag = await SeedAsync(table, ("obj", """{"a":{"2":1}}"""), ("arr", """{"a":[0,0,1]}"""), ("nl", "{\"a\\nb\":{\"x\":1}}"));

        (await MatchAsync(table, tag, new JsonPathConditionNode("new_payload", "$.a", "2", JsonPathTest.Defined, null, KeyIsIndex: true)))
            .Should().Equal("arr");
        (await MatchAsync(table, tag, new JsonPathConditionNode("new_payload", "$.a", "2", JsonPathTest.Defined, null)))
            .Should().Equal("obj");
        (await MatchAsync(table, tag, new JsonPathConditionNode("new_payload", "$.a", "2", JsonPathTest.NotDefined, null, KeyIsIndex: true)))
            .Should().Equal("nl", "obj");

        var parent = JsonPaths.Format([new PropertyStep("a\nb")]);
        (await MatchAsync(table, tag, N(JsonPathTest.Defined, null, parent, "x"))).Should().Equal("nl");
    }

    [Fact]
    public async Task Key_match_is_exact_and_parent_type_is_checked()
    {
        var table = await TableAsync();
        var tag = await SeedAsync(table, ("spaced", """{"b ":1}"""), ("plain", """{"b":1}"""), ("arr", """{"a":[0,0,1]}"""),
            ("root", "\r\n\t [5,6,7]"));

        (await MatchAsync(table, tag, N(JsonPathTest.Defined, null, "$", "b"))).Should().Equal("plain");
        (await MatchAsync(table, tag, N(JsonPathTest.Equal, 1.0, "$", "b"))).Should().Equal("plain");
        (await MatchAsync(table, tag, N(JsonPathTest.Defined, null, "$", "b "))).Should().Equal("spaced");
        // a property key never reaches an array element; an index key never reaches an object member
        (await MatchAsync(table, tag, N(JsonPathTest.Defined, null, "$.a", "2"))).Should().BeEmpty();
        (await MatchAsync(table, tag, new JsonPathConditionNode("new_payload", "$.a", "2", JsonPathTest.Defined, null, true))).Should().Equal("arr");
        // a root array with leading whitespace and newlines
        (await MatchAsync(table, tag, new JsonPathConditionNode("new_payload", "$", "1", JsonPathTest.Equal, 6.0, true))).Should().Equal("root");
    }

    [Fact]
    public async Task Keys_named_like_json_path_keywords_are_plain_keys()
    {
        // Plan 2.7b, Task 5 fix round 1: PostgreSQL reads the parent path as a jsonpath, whose keywords (size, last, type,
        // exists) must still be property names there.
        var table = await TableAsync();
        var tag = await SeedAsync(table, ("hit", """{"size":{"last":1},"type":"t","exists":true}"""), ("miss", """{"size":{"first":1}}"""));

        (await MatchAsync(table, tag, N(JsonPathTest.Equal, 1.0, "$.size", "last"))).Should().Equal("hit");
        (await MatchAsync(table, tag, N(JsonPathTest.Defined, null, "$.size", "last"))).Should().Equal("hit");
        (await MatchAsync(table, tag, N(JsonPathTest.Equal, "t", "$", "type"))).Should().Equal("hit");
        (await MatchAsync(table, tag, N(JsonPathTest.Equal, true, "$", "exists"))).Should().Equal("hit");
        (await MatchAsync(table, tag, new JsonPathConditionNode("new_payload", JsonPaths.Format([new PropertyStep("size")]), "last",
            JsonPathTest.NotDefined, null))).Should().Equal("miss");
    }

    [Fact]
    public async Task Array_index_and_quoted_key()
    {
        var table = await TableAsync();
        var tag = await SeedAsync(table,
            ("hit", """{"a b":[10,20,30],"a.b":{"x\"y":7}}"""), ("short", """{"a b":[10]}"""), ("plain", """{"a":{"b":1}}"""));

        var parent = JsonPaths.Format([new PropertyStep("a b")]);
        parent.Should().Be("$.\"a b\"");
        (await MatchAsync(table, tag, new JsonPathConditionNode("new_payload", parent, "2", JsonPathTest.Defined, null, true))).Should().Equal("hit");
        (await MatchAsync(table, tag, new JsonPathConditionNode("new_payload", parent, "2", JsonPathTest.Greater, 25.0, true))).Should().Equal("hit");
        (await MatchAsync(table, tag, new JsonPathConditionNode("new_payload", parent, "1", JsonPathTest.Equal, 20.0, true))).Should().Equal("hit");
        (await MatchAsync(table, tag, N(JsonPathTest.Defined, null, "$.a", "b"))).Should().Equal("plain");

        // keys with a dot, a quote and a backslash survive the path formatting
        var dotted = JsonPaths.Format([new PropertyStep("a.b")]);
        (await MatchAsync(table, tag, N(JsonPathTest.Equal, 7.0, dotted, "x\"y"))).Should().Equal("hit");
    }
}
