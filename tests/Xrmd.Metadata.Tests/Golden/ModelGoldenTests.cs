using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Xrmd.Model;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata.Tests.Golden;

/// <summary>
/// S.2b1, Task 5: the own model reproduces every golden metadata file byte for byte — read with <see cref="MetadataJson"/>
/// by the entry's root, written again, compared as strings (no normalisation).
/// </summary>
public sealed class ModelGoldenTests
{
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _, _) in MetadataCorpus.Entries) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Model_round_trip_is_byte_identical(string name)
    {
        var (_, root, golden) = MetadataCorpus.Entries.First(e => e.Name == name);

        var written = RoundTrip(root, golden);

        written.Should().Be(golden, "{0} must round-trip unchanged; {1}", name, written == golden ? "" : FirstDifference(golden, written));
    }

    private const string OptionRoot = "option";

    /// <summary>An unknown <c>@odata.type</c> fails on both sides alike, for each polymorphic root.</summary>
    [Theory]
    [InlineData(MetadataCorpus.Column, "FutureAttributeMetadata")]
    [InlineData(MetadataCorpus.Choiceset, "FutureOptionSetMetadata")]
    [InlineData(MetadataCorpus.Relationship, "FutureRelationshipMetadata")]
    [InlineData(OptionRoot, "FutureOptionMetadata")]
    public void Unknown_discriminator_throws_in_both_serializers(string root, string wire)
    {
        // The option root is an unknown option type inside the Options of an OptionSetMetadata.
        var json = root == OptionRoot
            ? $$"""{"@odata.type":"Microsoft.Dynamics.CRM.OptionSetMetadata","Name":"os","Options":[{"@odata.type":"Microsoft.Dynamics.CRM.{{wire}}","Value":1}]}"""
            : $$"""{"@odata.type":"Microsoft.Dynamics.CRM.{{wire}}","SchemaName":"future"}""";
        Action sdk = root switch
        {
            MetadataCorpus.Column => () => MetadataSerializer.DeserializeAttribute(json),
            MetadataCorpus.Choiceset or OptionRoot => () => MetadataSerializer.DeserializeOptionSet(json),
            _ => () => MetadataSerializer.DeserializeRelationship(json),
        };
        Action model = root switch
        {
            MetadataCorpus.Column => () => MetadataJson.DeserializeColumn(json),
            MetadataCorpus.Choiceset or OptionRoot => () => MetadataJson.DeserializeChoiceSet(json),
            _ => () => MetadataJson.DeserializeRelationship(json),
        };

        sdk.Should().Throw<JsonException>();
        model.Should().Throw<JsonException>();
    }

    /// <summary>
    /// Nested objects with <c>@odata.type</c> as their last property (PostgreSQL jsonb orders keys its own way) still read
    /// on both sides, keep their runtime types and write the same text.
    /// </summary>
    [Fact]
    public void Nested_odata_type_last_still_reads()
    {
        const string json = """
            {"LogicalName":"t","SchemaName":"T",
             "Attributes":[{"LogicalName":"name","AttributeType":"String","OptionSet":null,"@odata.type":"Microsoft.Dynamics.CRM.StringAttributeMetadata"},
                           {"LogicalName":"pick","AttributeType":"Picklist","OptionSet":{"Name":"os","Options":[{"Value":1},{"Value":2,"@odata.type":"Microsoft.Dynamics.CRM.StateOptionMetadata"}],"@odata.type":"Microsoft.Dynamics.CRM.OptionSetMetadata"},"@odata.type":"Microsoft.Dynamics.CRM.PicklistAttributeMetadata"}]}
            """;

        var sdk = MetadataSerializer.Deserialize(json);
        var model = MetadataJson.DeserializeTable(json);

        sdk.Attributes.Select(a => a.GetType().Name).Should().Equal("StringAttributeMetadata", "PicklistAttributeMetadata");
        model.Attributes!.Select(a => a.GetType()).Should().Equal(typeof(TextColumn), typeof(ChoiceColumn));
        ((Microsoft.Xrm.Sdk.Metadata.PicklistAttributeMetadata)sdk.Attributes[1]).OptionSet.Should().BeOfType<Microsoft.Xrm.Sdk.Metadata.OptionSetMetadata>()
            .Which.Options.Should().HaveCount(2);
        MetadataJson.Serialize(model).Should().Be(MetadataSerializer.Serialize(sdk));
    }

    /// <summary>Both sides discriminate the same wire names for each root pair.</summary>
    [Theory]
    [InlineData(typeof(Microsoft.Xrm.Sdk.Metadata.AttributeMetadata), typeof(ColumnDefinition))]
    [InlineData(typeof(Microsoft.Xrm.Sdk.Metadata.OptionMetadata), typeof(ChoiceOption))]
    [InlineData(typeof(Microsoft.Xrm.Sdk.Metadata.OptionSetMetadataBase), typeof(ChoiceSetBase))]
    [InlineData(typeof(Microsoft.Xrm.Sdk.Metadata.RelationshipMetadataBase), typeof(RelationshipDefinition))]
    public void Discriminator_sets_match(Type sdkRoot, Type modelRoot)
    {
        static HashSet<string> Discriminators(System.Text.Json.JsonSerializerOptions options, Type root) =>
            options.GetTypeInfo(root).PolymorphismOptions!.DerivedTypes.Select(d => (string)d.TypeDiscriminator!).ToHashSet();

        var sdk = Discriminators(MetadataSerializer.Options, sdkRoot);
        var model = Discriminators(MetadataJson.Options, modelRoot);

        sdk.Should().NotBeEmpty();
        model.Should().BeEquivalentTo(sdk);
    }

    public static TheoryData<string> OmittedNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _, _) in MetadataCorpus.Entries.Where(e => e.Name.EndsWith(".omitted", StringComparison.Ordinal))) data.Add(name);
        return data;
    }

    private static readonly string[] IdentifyingMembers = ["LogicalName", "SchemaName", "Name", "MetadataId"];

    /// <summary>
    /// A model instance fresh from its constructor, with only the identifying members set (as the corpus does on the SDK side),
    /// writes the <c>.omitted</c> file: the constructor defaults of the model match the SDK ones (e.g. <c>"Options":[]</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(OmittedNames))]
    public void Fresh_model_instance_writes_the_omitted_file(string name)
    {
        var (_, root, golden) = MetadataCorpus.Entries.First(e => e.Name == name);
        var wire = name["full/".Length..^".omitted".Length];
        var type = typeof(WireTypeAttribute).Assembly.GetTypes()
            .Single(t => t.GetCustomAttribute<WireTypeAttribute>()?.Name == wire);

        string written;
        if (typeof(ChoiceOption).IsAssignableFrom(type))
        {
            // Options are written wrapped in a choice set named after the option type.
            var read = (ChoiceSet)MetadataJson.DeserializeChoiceSet(golden);
            var wrapper = new ChoiceSet { Name = read.Name };
            wrapper.Options!.Add((ChoiceOption)Fresh(type, read.Options!.Single()));
            written = MetadataJson.SerializeChoiceSet(wrapper);
        }
        else
        {
            written = root switch
            {
                MetadataCorpus.Table => MetadataJson.Serialize((TableDefinition)Fresh(type, MetadataJson.DeserializeTable(golden))),
                MetadataCorpus.Column => MetadataJson.SerializeColumn((ColumnDefinition)Fresh(type, MetadataJson.DeserializeColumn(golden))),
                MetadataCorpus.Choiceset => MetadataJson.SerializeChoiceSet((ChoiceSetBase)Fresh(type, MetadataJson.DeserializeChoiceSet(golden))),
                MetadataCorpus.Relationship => MetadataJson.SerializeRelationship((RelationshipDefinition)Fresh(type, MetadataJson.DeserializeRelationship(golden))),
                _ => throw new InvalidOperationException($"Unknown root {root}."),
            };
        }

        written.Should().Be(golden, "{0} from a fresh instance; {1}", name, written == golden ? "" : FirstDifference(golden, written));
    }

    /// <summary>A new instance of <paramref name="type"/> with the identifying members copied from <paramref name="source"/>.</summary>
    private static object Fresh(Type type, object source)
    {
        var instance = Activator.CreateInstance(type)!;
        foreach (var member in IdentifyingMembers)
            if (type.GetProperty(member) is { } property) property.SetValue(instance, property.GetValue(source));
        return instance;
    }

    private static string RoundTrip(string root, string json) => root switch
    {
        MetadataCorpus.Table => MetadataJson.Serialize(MetadataJson.DeserializeTable(json)),
        MetadataCorpus.Column => MetadataJson.SerializeColumn(MetadataJson.DeserializeColumn(json)),
        MetadataCorpus.Choiceset => MetadataJson.SerializeChoiceSet(MetadataJson.DeserializeChoiceSet(json)),
        MetadataCorpus.Relationship => MetadataJson.SerializeRelationship(MetadataJson.DeserializeRelationship(json)),
        _ => throw new InvalidOperationException($"Unknown root {root}."),
    };

    /// <summary>The first differing index with up to 80 characters of context on each side, for both texts.</summary>
    private static string FirstDifference(string expected, string actual)
    {
        var length = Math.Min(expected.Length, actual.Length);
        var index = 0;
        while (index < length && expected[index] == actual[index]) index++;
        string Around(string s)
        {
            var start = Math.Max(0, index - 80);
            var end = Math.Min(s.Length, index + 80);
            return s[start..index] + "<<HERE>>" + s[index..end];
        }
        return $"first difference at index {index} (golden length {expected.Length}, written {actual.Length})\n" +
               $"  golden:  {Around(expected)}\n" +
               $"  written: {Around(actual)}";
    }
}
