using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;

namespace Xrmd.Schema.Tests;

/// <summary>
/// The web admin keeps a copy of the column type list (<c>ui/src/admin/tables/columnTypes.ts</c>) and builds the table and
/// column request bodies itself (<c>ui/src/admin/tables/schemaBodies.ts</c>). Both are pinned against committed fixtures
/// that the Vitest suite asserts too. No test writes them: they change only by hand, together with the C# they mirror.
/// </summary>
public class UiFixtureTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static JsonObject Fixture(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "ui", file);
        File.Exists(path).Should().BeTrue($"the committed fixture fixtures/ui/{file} must exist");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    [Fact]
    public void Column_types_fixture_equals_SchemaColumnTypes_Names()
    {
        var committed = Fixture("column-types.json")["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        committed.Should().Equal(SchemaColumnTypes.Names,
            "fixtures/ui/column-types.json and ui/src/admin/tables/columnTypes.ts must list SchemaColumnTypes.Names in order; " +
            "update both by hand when the list changes (the Vitest suite compares the TypeScript list with the fixture)");
    }

    [Fact]
    public void Column_types_fixture_holds_the_file_and_image_MaxSizeInKB_limits()
    {
        var limits = Fixture("column-types.json")["maxSizeInKB"]!;
        int Value(string type, string name) => limits[type]![name]!.GetValue<int>();
        (Value("file", "default"), Value("file", "min"), Value("file", "max")).Should().Be(
            (Xrmd.Metadata.FileColumns.DefaultFileMaxSizeInKB, Xrmd.Metadata.FileColumns.MinSizeInKB, Xrmd.Metadata.FileColumns.MaxFileSizeInKB),
            "fixtures/ui/column-types.json and maxSizeInKB in ui/src/admin/tables/columnTypes.ts must hold FileColumns' file limits");
        (Value("image", "default"), Value("image", "min"), Value("image", "max")).Should().Be(
            (Xrmd.Metadata.FileColumns.DefaultImageMaxSizeInKB, Xrmd.Metadata.FileColumns.MinSizeInKB, Xrmd.Metadata.FileColumns.MaxImageSizeInKB),
            "fixtures/ui/column-types.json and maxSizeInKB in ui/src/admin/tables/columnTypes.ts must hold FileColumns' image limits");
    }

    /// <summary>Builds the body of one fixture case with the code the CLI uses.</summary>
    public static JsonObject Build(string kind, JsonNode input) => kind switch
    {
        "entity" => SchemaWebApiBodies.Entity(input.Deserialize<SchemaTable>(Options)!),
        "attribute" => SchemaWebApiBodies.Attribute(input.Deserialize<SchemaColumn>(Options)!),
        "lookup" => SchemaWebApiBodies.Lookup(
            input["column"].Deserialize<SchemaColumn>(Options)!,
            input["table"]!.GetValue<string>(),
            input["relationshipName"]!.GetValue<string>(),
            input["referencedAttribute"]!.GetValue<string>()),
        "customer" => SchemaWebApiBodies.Customer(input["column"].Deserialize<SchemaColumn>(Options)!, input["table"]!.GetValue<string>()),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown case kind."),
    };

    [Fact]
    public void Schema_bodies_fixture_equals_SchemaWebApiBodies_output()
    {
        var cases = Fixture("schema-bodies.json")["cases"]!.AsArray();
        cases.Should().NotBeEmpty();
        foreach (var node in cases)
        {
            var name = node!["name"]!.GetValue<string>();
            var actual = Build(node["kind"]!.GetValue<string>(), node["input"]!);
            JsonNode.DeepEquals(actual, node["expected"]).Should().BeTrue(
                $"case '{name}' must equal SchemaWebApiBodies output; actual: {actual.ToJsonString()}. " +
                "If the C# bodies changed on purpose, update fixtures/ui/schema-bodies.json and ui/src/admin/tables/schemaBodies.ts by hand");
        }
    }
}
