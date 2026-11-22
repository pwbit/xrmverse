using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

public class OptionSetSerializationTests
{
    private static Label Label(string en, string it) => new(new LocalizedLabel(en, 1033), new[] { new LocalizedLabel(en, 1033), new LocalizedLabel(it, 1040) });

    private static OptionSetMetadata Priority() => new(new OptionMetadataCollection
    {
        new OptionMetadata(Label("Low", "Bassa"), 1),
        new OptionMetadata(Label("High", "Alta"), 2),
    })
    {
        MetadataId = Guid.NewGuid(),
        Name = "new_priority",
        IsGlobal = true,
        OptionSetType = OptionSetType.Picklist,
        DisplayName = Label("Priority", "Priorità"),
    };

    [Fact]
    public void Global_option_set_round_trips_in_the_Web_API_shape()
    {
        var original = Priority();

        var json = MetadataSerializer.SerializeOptionSet(original);
        var node = JsonNode.Parse(json)!.AsObject();

        node.First().Key.Should().Be("@odata.type");
        node["@odata.type"]!.GetValue<string>().Should().Be("Microsoft.Dynamics.CRM.OptionSetMetadata");
        node["Name"]!.GetValue<string>().Should().Be("new_priority");
        node["IsGlobal"]!.GetValue<bool>().Should().BeTrue();
        node["OptionSetType"]!.GetValue<string>().Should().Be("Picklist");
        node["Options"]!.AsArray().Select(o => o!["Value"]!.GetValue<int>()).Should().Equal(1, 2);
        node["Options"]![1]!["Label"]!["LocalizedLabels"]![1]!["Label"]!.GetValue<string>().Should().Be("Alta");

        var back = MetadataSerializer.DeserializeOptionSet(json);
        back.Should().BeOfType<OptionSetMetadata>();
        // The SDK's parameterless OptionMetadata constructor sets ExternalValue to "" (the JSON has none either way).
        back.Should().BeEquivalentTo(original, o => o.RespectingRuntimeTypes()
            .Excluding(m => m.Path.EndsWith(nameof(OptionMetadata.ExternalValue), StringComparison.Ordinal)));
        var again = MetadataSerializer.SerializeOptionSet(back);
        MetadataSerializer.SerializeOptionSet(MetadataSerializer.DeserializeOptionSet(again)).Should().Be(again, "the stored form is stable");
    }

    [Fact]
    public void Web_API_body_deserializes_to_a_global_OptionSetMetadata()
    {
        const string body = """
            {
              "@odata.type": "Microsoft.Dynamics.CRM.OptionSetMetadata",
              "Name": "new_priority",
              "IsGlobal": true,
              "OptionSetType": "Picklist",
              "Options": [ { "Value": 1, "Label": { "LocalizedLabels": [ { "Label": "Low", "LanguageCode": 1033 } ] } } ]
            }
            """;

        var back = MetadataSerializer.DeserializeOptionSet(body).Should().BeOfType<OptionSetMetadata>().Subject;

        back.Name.Should().Be("new_priority");
        back.IsGlobal.Should().BeTrue();
        back.OptionSetType.Should().Be(OptionSetType.Picklist);
        back.Options.Should().ContainSingle(o => o.Value == 1 && o.Label.LocalizedLabels[0].Label == "Low");
    }
}
