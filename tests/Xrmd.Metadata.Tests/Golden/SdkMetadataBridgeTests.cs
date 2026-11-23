using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata.Tests.Golden;

/// <summary>
/// S.2b1, Task 6: the bridge converts SDK metadata to the own model and back; both serializers write the same JSON, so
/// each direction, through the bridge, must reproduce the golden text.
/// </summary>
public sealed class SdkMetadataBridgeTests
{
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _, _) in MetadataCorpus.Entries)
            if (name != UnreadableBySdk) data.Add(name);
        return data;
    }

    // The reflection-filled entity carries members the SDK serializer cannot read back at all (Privileges: no usable
    // constructor; Keys[].AsyncJob: an abstract KeyAttributeCollection). It is a corpus artefact, not stored metadata;
    // the bridge tests cover every other entry, and the seed tables stand for the entity root.
    private const string UnreadableBySdk = "full/EntityMetadata";

    [Theory]
    [MemberData(nameof(Names))]
    public void Sdk_to_model_to_sdk_writes_the_golden_text(string name)
    {
        var (_, root, golden) = MetadataCorpus.Entries.First(e => e.Name == name);

        var written = root switch
        {
            MetadataCorpus.Table => MetadataSerializer.Serialize(SdkMetadataBridge.ToSdk(SdkMetadataBridge.ToModel(MetadataSerializer.Deserialize(golden)))),
            MetadataCorpus.Column => MetadataSerializer.SerializeAttribute(SdkMetadataBridge.ToSdk(SdkMetadataBridge.ToModel(MetadataSerializer.DeserializeAttribute(golden)))),
            MetadataCorpus.Choiceset => MetadataSerializer.SerializeOptionSet(SdkMetadataBridge.ToSdk(SdkMetadataBridge.ToModel(MetadataSerializer.DeserializeOptionSet(golden)))),
            MetadataCorpus.Relationship => MetadataSerializer.SerializeRelationship(SdkMetadataBridge.ToSdk(SdkMetadataBridge.ToModel(MetadataSerializer.DeserializeRelationship(golden)))),
            _ => throw new InvalidOperationException($"Unknown root {root}."),
        };

        written.Should().Be(golden);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Model_to_sdk_to_model_writes_the_golden_text(string name)
    {
        var (_, root, golden) = MetadataCorpus.Entries.First(e => e.Name == name);

        var written = root switch
        {
            MetadataCorpus.Table => MetadataJson.Serialize(SdkMetadataBridge.ToModel(SdkMetadataBridge.ToSdk(MetadataJson.DeserializeTable(golden)))),
            MetadataCorpus.Column => MetadataJson.SerializeColumn(SdkMetadataBridge.ToModel(SdkMetadataBridge.ToSdk(MetadataJson.DeserializeColumn(golden)))),
            MetadataCorpus.Choiceset => MetadataJson.SerializeChoiceSet(SdkMetadataBridge.ToModel(SdkMetadataBridge.ToSdk(MetadataJson.DeserializeChoiceSet(golden)))),
            MetadataCorpus.Relationship => MetadataJson.SerializeRelationship(SdkMetadataBridge.ToModel(SdkMetadataBridge.ToSdk(MetadataJson.DeserializeRelationship(golden)))),
            _ => throw new InvalidOperationException($"Unknown root {root}."),
        };

        written.Should().Be(golden);
    }

    [Fact]
    public void Sdk_cannot_read_privileges_or_key_async_job()
    {
        var (_, _, golden) = MetadataCorpus.Entries.First(e => e.Name == UnreadableBySdk);

        var act = () => MetadataSerializer.Deserialize(golden);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Runtime_types_are_kept()
    {
        var sdk = new StringAttributeMetadata("name") { SchemaName = "Name" };
        var column = SdkMetadataBridge.ToModel(sdk);
        column.Should().BeOfType<TextColumn>();
        SdkMetadataBridge.ToSdk(column).Should().BeOfType<StringAttributeMetadata>();

        var relationship = new OneToManyRelationshipMetadata { SchemaName = "a_b" };
        var model = SdkMetadataBridge.ToModel(relationship);
        model.Should().BeOfType<OneToManyRelationship>();
        SdkMetadataBridge.ToSdk(model).Should().BeOfType<OneToManyRelationshipMetadata>();

        var choiceSet = new OptionSetMetadata { Name = "os" };
        SdkMetadataBridge.ToModel(choiceSet).Should().BeOfType<ChoiceSet>();
        SdkMetadataBridge.ToSdk(SdkMetadataBridge.ToModel(choiceSet)).Should().BeOfType<OptionSetMetadata>();
    }
}
