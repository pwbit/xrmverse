using System.Text.Json;
using FluentAssertions;
using Xrmd.Model.Metadata;

namespace Xrmd.Model.Tests;

/// <summary>
/// S.2b1, Task 5: <see cref="MetadataJson"/> over the own model only (no SDK). The byte-for-byte comparison with the
/// golden files lives in Xrmd.Metadata.Tests (ModelGoldenTests), next to the corpus.
/// </summary>
public sealed class MetadataJsonTests
{
    [Fact]
    public void Odata_type_not_first_still_reads()
    {
        // PostgreSQL jsonb orders keys its own way: the discriminator may come last.
        const string json = """{"LogicalName":"name","MaxLength":100,"@odata.type":"Microsoft.Dynamics.CRM.StringAttributeMetadata"}""";

        var column = MetadataJson.DeserializeColumn(json);

        column.Should().BeOfType<TextColumn>();
        column.LogicalName.Should().Be("name");
        ((TextColumn)column).MaxLength.Should().Be(100);
    }

    [Fact]
    public void Unknown_discriminator_throws_as_today()
    {
        // Same as MetadataSerializer (pinned together in ModelGoldenTests): an @odata.type nobody knows is an error, not a guess.
        const string json = """{"@odata.type":"Microsoft.Dynamics.CRM.FutureAttributeMetadata","LogicalName":"future"}""";

        FluentActions.Invoking(() => MetadataJson.DeserializeColumn(json)).Should().Throw<JsonException>();
    }

    [Fact]
    public void Numeric_enum_reads()
    {
        const string json = """{"LogicalName":"name","AttributeType":14}""";

        MetadataJson.DeserializeColumn(json).AttributeType.Should().Be(ColumnTypeCode.String);
    }

    [Fact]
    public void Omitted_and_empty_collections_stay_distinct()
    {
        var omitted = new TableDefinition { LogicalName = "t" };
        var empty = new TableDefinition { LogicalName = "t", Attributes = [], Keys = [] };

        var omittedJson = MetadataJson.Serialize(omitted);
        var emptyJson = MetadataJson.Serialize(empty);

        omittedJson.Should().NotContain("\"Attributes\"").And.NotContain("\"Keys\"");
        emptyJson.Should().Contain("\"Attributes\":[]").And.Contain("\"Keys\":[]");
        MetadataJson.DeserializeTable(omittedJson).Attributes.Should().BeNull();
        MetadataJson.DeserializeTable(emptyJson).Attributes.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Null_json_throws()
    {
        FluentActions.Invoking(() => MetadataJson.DeserializeTable("null"))
            .Should().Throw<JsonException>().WithMessage("JSON 'null' is not a TableDefinition.");
        FluentActions.Invoking(() => MetadataJson.DeserializeColumn("null"))
            .Should().Throw<JsonException>().WithMessage("JSON 'null' is not a ColumnDefinition.");
        FluentActions.Invoking(() => MetadataJson.DeserializeChoiceSet("null"))
            .Should().Throw<JsonException>().WithMessage("JSON 'null' is not a ChoiceSetBase.");
        FluentActions.Invoking(() => MetadataJson.DeserializeRelationship("null"))
            .Should().Throw<JsonException>().WithMessage("JSON 'null' is not a RelationshipDefinition.");
    }

    [Fact]
    public void Clone_keeps_runtime_type()
    {
        var table = new TableDefinition
        {
            LogicalName = "t",
            Attributes = [new TextColumn { LogicalName = "name", MaxLength = 50 }, new LookupColumn { LogicalName = "parentid", Targets = ["t"] }],
        };
        ColumnDefinition column = new ChoiceColumn { LogicalName = "kind", OptionSet = new ChoiceSet { Options = [new StateOption { Value = 1 }] } };

        var tableCopy = MetadataJson.Clone(table);
        var columnCopy = MetadataJson.Clone(column);

        tableCopy.Should().NotBeSameAs(table);
        tableCopy.Attributes!.Select(a => a.GetType()).Should().Equal(typeof(TextColumn), typeof(LookupColumn));
        ((TextColumn)tableCopy.Attributes![0]).MaxLength.Should().Be(50);
        columnCopy.Should().BeOfType<ChoiceColumn>().Which.OptionSet!.Options!.Single().Should().BeOfType<StateOption>();
    }

    [Fact]
    public void Discriminator_is_the_wire_type_name()
    {
        MetadataJson.SerializeColumn(new TextColumn())
            .Should().StartWith("{\"@odata.type\":\"Microsoft.Dynamics.CRM.StringAttributeMetadata\"");
        MetadataJson.SerializeChoiceSet(new BooleanChoiceSet())
            .Should().StartWith("{\"@odata.type\":\"Microsoft.Dynamics.CRM.BooleanOptionSetMetadata\"");
        MetadataJson.SerializeRelationship(new ManyToManyRelationship())
            .Should().StartWith("{\"@odata.type\":\"Microsoft.Dynamics.CRM.ManyToManyRelationshipMetadata\"");
    }

    [Fact]
    public void Relationship_kind_zero_is_written_with_its_web_api_name()
    {
        // RelationshipType has two names for 0 (OneToManyRelationship and Default); the stored JSON has the first.
        MetadataJson.SerializeRelationship(new OneToManyRelationship())
            .Should().Contain("\"RelationshipType\":\"OneToManyRelationship\"");
    }

    [Fact]
    public void Key_attributes_are_an_array_of_key_value_pairs()
    {
        const string json = """{"Id":"00000000-0000-0000-0000-000000000001","LogicalName":"account","KeyAttributes":[{"Key":"accountnumber","Value":"A-1"},{"Key":"region","Value":7}]}""";

        var pointer = JsonSerializer.Deserialize<RecordPointer>(json, MetadataJson.Options)!;

        pointer.KeyAttributes!.Keys.Should().Equal("accountnumber", "region");
        pointer.KeyAttributes["region"].GetInt32().Should().Be(7);
        JsonSerializer.Serialize(pointer, MetadataJson.Options).Should().Be(json);
    }

    /// <summary>
    /// Missing keys fall back to the defaults of the SDK types (established against SDK instances in
    /// Xrmd.Metadata.Tests, ModelParityTests): empty nested objects are written as the SDK writes them.
    /// </summary>
    [Fact]
    public void Empty_nested_objects_take_the_sdk_defaults()
    {
        const string json = """
            {"LogicalName":"t","DisplayName":{},"IsAuditEnabled":{},
             "Attributes":[{"@odata.type":"Microsoft.Dynamics.CRM.StringAttributeMetadata","RequiredLevel":{}}],
             "Keys":[{"AsyncJob":{}}]}
            """;

        var written = MetadataJson.Serialize(MetadataJson.DeserializeTable(json));

        written.Should().Contain("\"DisplayName\":{\"LocalizedLabels\":[]}")
            .And.Contain("\"IsAuditEnabled\":{\"Value\":false,\"CanBeChanged\":true}")
            .And.Contain("\"RequiredLevel\":{\"IsValueModified\":true,\"IsCanBeChangedPropertyModified\":true,\"Value\":\"None\",\"CanBeChanged\":true}")
            .And.Contain("\"AsyncJob\":{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"KeyAttributes\":[]}");
    }

    [Fact]
    public void Null_labels_and_options_read_as_empty_lists_like_the_sdk()
    {
        const string json = """{"@odata.type":"Microsoft.Dynamics.CRM.OptionSetMetadata","DisplayName":{"LocalizedLabels":null},"Name":"os","Options":null}""";

        MetadataJson.SerializeChoiceSet(MetadataJson.DeserializeChoiceSet(json))
            .Should().Be("""{"@odata.type":"Microsoft.Dynamics.CRM.OptionSetMetadata","Options":[],"DisplayName":{"LocalizedLabels":[]},"Name":"os"}""");
    }
}
