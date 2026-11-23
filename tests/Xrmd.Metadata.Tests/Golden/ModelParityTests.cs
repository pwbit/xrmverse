using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Model;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata.Tests.Golden;

/// <summary>
/// S.2b1 final review: <see cref="MetadataJson"/> is a drop-in replacement for <see cref="MetadataSerializer"/> on any input
/// JSON, not only on the complete golden files. Each test reads the same text through both serializers and writes it again;
/// the two outputs must be identical. The expected behaviour comes only from what the SDK instances do (black box).
/// </summary>
public sealed class ModelParityTests
{
    private const string SeedPrefix = "Xrmd.Metadata.Seed.";
    private static readonly Assembly SeedAssembly = typeof(MetadataSerializer).Assembly;
    private static readonly Assembly SdkAssembly = typeof(EntityMetadata).Assembly;
    private static readonly Assembly ModelAssembly = typeof(WireTypeAttribute).Assembly;

    public static TheoryData<string> SeedResources()
    {
        var data = new TheoryData<string>();
        foreach (var name in SeedAssembly.GetManifestResourceNames()
                     .Where(r => r.StartsWith(SeedPrefix, StringComparison.Ordinal) && r.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(r => r, StringComparer.Ordinal))
            data.Add(name);
        return data;
    }

    /// <summary>(a) Every raw seed table (as committed, often partial) reads and writes the same text through both serializers.</summary>
    [Theory]
    [MemberData(nameof(SeedResources))]
    public void Raw_seed_table_writes_the_same_json_through_both_serializers(string resource)
    {
        using var stream = SeedAssembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var sdk = RoundTripSdk(MetadataCorpus.Table, json);
        var model = RoundTripModel(MetadataCorpus.Table, json);

        // Only the first difference is reported: the tables are long.
        if (model != sdk) Assert.Fail($"{resource}: {FirstDifference(sdk, model)}");
    }

    public static TheoryData<string> FullFiles()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _, _) in MetadataCorpus.Entries.Where(e => e.Name.StartsWith("full/", StringComparison.Ordinal) && !e.Name.Contains('.')))
            data.Add(name);
        return data;
    }

    /// <summary>
    /// (b) Leave-one-out over each complete golden file: every key of every object (top level and nested) is dropped in turn,
    /// every object is emptied in turn (<c>{}</c>, keeping its <c>@odata.type</c>), and every object or array value is
    /// replaced by <c>null</c> in turn. Each variant must read and write the
    /// same text through both serializers (or fail in both). This pins every default a missing key falls back to.
    /// </summary>
    [Theory]
    [MemberData(nameof(FullFiles))]
    public void Every_partial_variant_of_a_full_file_writes_the_same_json(string name)
    {
        var (_, root, golden) = MetadataCorpus.Entries.First(e => e.Name == name);
        golden = ReadableBySdk(golden);
        var mismatches = new List<string>();
        var variants = 0;
        foreach (var (description, json) in Variants(golden))
        {
            variants++;
            var sdk = Outcome(() => RoundTripSdk(root, json));
            var model = Outcome(() => RoundTripModel(root, json));
            if (sdk != model) mismatches.Add($"{description}: {FirstDifference(sdk, model)}");
        }

        variants.Should().BeGreaterThan(10);
        if (mismatches.Count > 0) Assert.Fail($"{name}: {mismatches.Count} of {variants} variants differ:\n" + string.Join("\n", mismatches));
    }

    public static TheoryData<string> FreshTypes()
    {
        var data = new TheoryData<string>();
        foreach (var type in ModelAssembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic && t.GetCustomAttribute<WireTypeAttribute>() is not null)
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
            data.Add(type.GetCustomAttribute<WireTypeAttribute>()!.Name);
        return data;
    }

    /// <summary>
    /// (c) A freshly constructed model type writes what the freshly constructed SDK type writes: the SDK one from its
    /// parameterless constructor, or else from its simplest public constructor with default arguments (what the JSON
    /// reader does when no key is present).
    /// </summary>
    [Theory]
    [MemberData(nameof(FreshTypes))]
    public void Fresh_model_instance_writes_what_a_fresh_sdk_instance_writes(string wire)
    {
        var modelType = ModelAssembly.GetTypes().Single(t => t.IsClass && t.GetCustomAttribute<WireTypeAttribute>()?.Name == wire);
        var sdkType = SdkAssembly.GetTypes().Single(t => t.IsPublic && t.Name == wire
            && t.Namespace is "Microsoft.Xrm.Sdk" or "Microsoft.Xrm.Sdk.Metadata");

        var sdk = JsonSerializer.Serialize(Construct(sdkType), sdkType, MetadataSerializer.Options);
        var model = JsonSerializer.Serialize(Activator.CreateInstance(modelType), modelType, MetadataJson.Options);

        if (model != sdk) Assert.Fail($"{wire}: {FirstDifference(sdk, model)}");
    }

    /// <summary>
    /// Setting <c>Value</c> or <c>CanBeChanged</c> on the managed property types, after construction, behaves alike on both
    /// sides (observed on the SDK instances: whether the setters touch the "modified" flags).
    /// </summary>
    [Fact]
    public void Managed_property_setters_behave_alike()
    {
        var sdkRequired = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
        {
            IsValueModified = false,
            IsCanBeChangedPropertyModified = false,
        };
        sdkRequired.Value = AttributeRequiredLevel.ApplicationRequired;
        sdkRequired.CanBeChanged = false;
        var modelRequired = new RequiredLevelSetting { IsValueModified = false, IsCanBeChangedPropertyModified = false };
        modelRequired.Value = RequirementLevel.ApplicationRequired;
        modelRequired.CanBeChanged = false;

        // Observed on the SDK instance: the setters leave the "modified" flags alone.
        sdkRequired.IsValueModified.Should().BeFalse();
        sdkRequired.IsCanBeChangedPropertyModified.Should().BeFalse();
        JsonSerializer.Serialize(modelRequired, MetadataJson.Options)
            .Should().Be(JsonSerializer.Serialize(sdkRequired, MetadataSerializer.Options));

        var sdkFlag = new BooleanManagedProperty { Value = true, CanBeChanged = false };
        var modelFlag = new ManagedFlag { Value = true, CanBeChanged = false };
        JsonSerializer.Serialize(modelFlag, MetadataJson.Options)
            .Should().Be(JsonSerializer.Serialize(sdkFlag, MetadataSerializer.Options));
    }

    /// <summary>The cases of the review, spelled out: each nested type read from <c>{}</c> writes the SDK text.</summary>
    [Theory]
    [InlineData("""{"LogicalName":"t","DisplayName":{}}""")]
    [InlineData("""{"LogicalName":"t","IsAuditEnabled":{}}""")]
    [InlineData("""{"LogicalName":"t","Attributes":[{"@odata.type":"Microsoft.Dynamics.CRM.StringAttributeMetadata","RequiredLevel":{}}]}""")]
    [InlineData("""{"LogicalName":"t","Keys":[{"AsyncJob":{}}]}""")]
    public void Empty_nested_objects_write_the_same_json(string json)
    {
        RoundTripModel(MetadataCorpus.Table, json).Should().Be(RoundTripSdk(MetadataCorpus.Table, json));
    }

    /// <summary>
    /// The SDK serializer cannot read <c>Privileges</c> or <c>Keys[].AsyncJob.KeyAttributes</c> at all (see
    /// SdkMetadataBridgeTests); they are taken out so that the rest of the file can be compared.
    /// </summary>
    private static string ReadableBySdk(string json)
    {
        var doc = JsonNode.Parse(json)!.AsObject();
        if (!doc.ContainsKey("Privileges")) return json;
        doc.Remove("Privileges");
        foreach (var key in doc["Keys"]?.AsArray() ?? []) (key?["AsyncJob"] as JsonObject)?.Remove("KeyAttributes");
        return doc.ToJsonString();
    }

    private static object Construct(Type type)
    {
        if (type.GetConstructor(Type.EmptyTypes) is { } parameterless) return parameterless.Invoke(null);
        // SecurityPrivilegeMetadata has no public constructor: its non-public parameterless one.
        if (type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes) is { } hidden) return hidden.Invoke(null);
        var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        return ctor.Invoke(ctor.GetParameters()
            .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray());
    }

    /// <summary>Every single-key removal and every single-object emptying of <paramref name="json"/>.</summary>
    private static IEnumerable<(string Description, string Json)> Variants(string json)
    {
        var paths = new List<(string Path, List<object> Steps, bool IsObject)>();
        void Walk(JsonNode? node, string path, List<object> steps)
        {
            switch (node)
            {
                case JsonObject obj:
                    paths.Add((path, steps, true));
                    foreach (var (key, child) in obj)
                    {
                        var next = new List<object>(steps) { key };
                        paths.Add((path + "/" + key, next, false));
                        Walk(child, path + "/" + key, next);
                    }
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++) Walk(array[i], path + "/" + i, new List<object>(steps) { i });
                    break;
            }
        }
        Walk(JsonNode.Parse(json), "", []);

        foreach (var (path, steps, isObject) in paths)
        {
            var doc = JsonNode.Parse(json)!;
            if (isObject)
            {
                var obj = (JsonObject)Navigate(doc, steps)!;
                if (obj.Count == 0 || obj.Count == 1 && obj.ContainsKey(MetadataSerializer.ODataTypeProperty)) continue;
                foreach (var key in obj.Select(p => p.Key).Where(k => k != MetadataSerializer.ODataTypeProperty).ToList()) obj.Remove(key);
                yield return ("empty " + (path.Length == 0 ? "/" : path), doc.ToJsonString());
            }
            else
            {
                var parent = (JsonObject)Navigate(doc, steps.Take(steps.Count - 1))!;
                parent.Remove((string)steps[^1]);
                yield return ("drop " + path, doc.ToJsonString());

                // An explicit null in place of an object or an array.
                doc = JsonNode.Parse(json)!;
                parent = (JsonObject)Navigate(doc, steps.Take(steps.Count - 1))!;
                if (parent[(string)steps[^1]] is JsonObject or JsonArray)
                {
                    parent[(string)steps[^1]] = null;
                    yield return ("null " + path, doc.ToJsonString());
                }
            }
        }
    }

    private static JsonNode? Navigate(JsonNode node, IEnumerable<object> steps)
    {
        JsonNode? current = node;
        foreach (var step in steps) current = step is int i ? current![i] : current![(string)step];
        return current;
    }

    private static string Outcome(Func<string> roundTrip)
    {
        try { return roundTrip(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return "throws " + ex.GetType().Name;
        }
    }

    private static string RoundTripSdk(string root, string json) => root switch
    {
        MetadataCorpus.Table => MetadataSerializer.Serialize(MetadataSerializer.Deserialize(json)),
        MetadataCorpus.Column => MetadataSerializer.SerializeAttribute(MetadataSerializer.DeserializeAttribute(json)),
        MetadataCorpus.Choiceset => MetadataSerializer.SerializeOptionSet(MetadataSerializer.DeserializeOptionSet(json)),
        MetadataCorpus.Relationship => MetadataSerializer.SerializeRelationship(MetadataSerializer.DeserializeRelationship(json)),
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, null),
    };

    private static string RoundTripModel(string root, string json) => root switch
    {
        MetadataCorpus.Table => MetadataJson.Serialize(MetadataJson.DeserializeTable(json)),
        MetadataCorpus.Column => MetadataJson.SerializeColumn(MetadataJson.DeserializeColumn(json)),
        MetadataCorpus.Choiceset => MetadataJson.SerializeChoiceSet(MetadataJson.DeserializeChoiceSet(json)),
        MetadataCorpus.Relationship => MetadataJson.SerializeRelationship(MetadataJson.DeserializeRelationship(json)),
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, null),
    };

    private static string FirstDifference(string expected, string actual)
    {
        var length = Math.Min(expected.Length, actual.Length);
        var index = 0;
        while (index < length && expected[index] == actual[index]) index++;
        string Around(string s) => s[Math.Max(0, index - 60)..index] + "<<HERE>>" + s[index..Math.Min(s.Length, index + 60)];
        return $"at {index}: sdk {Around(expected)} | model {Around(actual)}";
    }
}
