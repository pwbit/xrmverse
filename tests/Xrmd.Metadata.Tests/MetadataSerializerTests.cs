using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

public class MetadataSerializerTests
{
    private static StringAttributeMetadata Name() => new("pb_Name")
    {
        MetadataId = Guid.NewGuid(),
        LogicalName = "pb_name",
        MaxLength = 200,
        DisplayName = SystemAttributes.CreateLabel("Name", 1033),
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
        FormatName = StringFormatName.Text,
    };

    private static LookupAttributeMetadata Parent() => new()
    {
        MetadataId = Guid.NewGuid(),
        LogicalName = "pb_parentid",
        SchemaName = "pb_ParentId",
        Targets = ["pb_project", "account"],
    };

    [Fact]
    public void String_attribute_round_trips_with_odata_type()
    {
        var original = Name();

        var json = MetadataSerializer.SerializeAttribute(original);
        var back = MetadataSerializer.DeserializeAttribute(json);

        JsonNode.Parse(json)!["@odata.type"]!.GetValue<string>()
            .Should().Be("Microsoft.Dynamics.CRM.StringAttributeMetadata");
        back.Should().BeOfType<StringAttributeMetadata>();
        back.Should().BeEquivalentTo(original, o => o.RespectingRuntimeTypes());
        back.AttributeType.Should().Be(AttributeTypeCode.String);
        back.DisplayName.LocalizedLabels.Should().ContainSingle(l => l.Label == "Name" && l.LanguageCode == 1033);
    }

    [Fact]
    public void Lookup_attribute_round_trips_targets()
    {
        var original = Parent();

        var json = MetadataSerializer.SerializeAttribute(original);
        var back = MetadataSerializer.DeserializeAttribute(json);

        json.Should().Contain("\"@odata.type\":\"Microsoft.Dynamics.CRM.LookupAttributeMetadata\"");
        back.Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().Equal("pb_project", "account");
        back.Should().BeEquivalentTo(original, o => o.RespectingRuntimeTypes());
    }

    [Fact]
    public void Json_is_web_api_shaped()
    {
        var node = JsonNode.Parse(MetadataSerializer.SerializeAttribute(Name()))!;

        node["LogicalName"]!.GetValue<string>().Should().Be("pb_name");
        node["AttributeType"]!.GetValue<string>().Should().Be("String");
        node["AttributeTypeName"]!["Value"]!.GetValue<string>().Should().Be("StringType");
        node["RequiredLevel"]!["Value"]!.GetValue<string>().Should().Be("ApplicationRequired");
        node["DisplayName"]!["LocalizedLabels"]![0]!["Label"]!.GetValue<string>().Should().Be("Name");
        node["DisplayName"]!["UserLocalizedLabel"]!["LanguageCode"]!.GetValue<int>().Should().Be(1033);
        node.AsObject().Should().NotContainKey("ExtensionData");
        node.AsObject().Should().NotContainKey("Description", "null properties are omitted");
    }

    [Fact]
    public void Entity_round_trips_with_polymorphic_attributes_and_internal_setters()
    {
        var entity = new EntityMetadata
        {
            MetadataId = Guid.NewGuid(),
            LogicalName = "pb_project",
            SchemaName = "pb_Project",
            EntitySetName = "pb_projects",
            OwnershipType = OwnershipTypes.UserOwned,
            DisplayName = new Label("Project", 1033),
        };
        var json = $$"""
            {"LogicalName":"pb_project","ObjectTypeCode":10000,"PrimaryIdAttribute":"pb_projectid",
             "PrimaryNameAttribute":"pb_name","OwnershipType":"UserOwned",
             "Attributes":[{{MetadataSerializer.SerializeAttribute(Name())}},{{MetadataSerializer.SerializeAttribute(Parent())}}]}
            """;

        var parsed = MetadataSerializer.Deserialize(json);
        parsed.ObjectTypeCode.Should().Be(10000);
        parsed.PrimaryIdAttribute.Should().Be("pb_projectid");
        parsed.Attributes.Select(a => a.GetType()).Should().Equal(typeof(StringAttributeMetadata), typeof(LookupAttributeMetadata));

        var back = MetadataSerializer.Deserialize(MetadataSerializer.Serialize(entity));
        back.Should().BeEquivalentTo(entity);
    }

    [Fact]
    public void Every_concrete_attribute_subtype_round_trips_to_its_own_type()
    {
        var types = typeof(AttributeMetadata).Assembly.GetTypes()
            .Where(t => typeof(AttributeMetadata).IsAssignableFrom(t) && t is { IsAbstract: false, IsPublic: true });

        foreach (var type in types)
        {
            var attr = (AttributeMetadata)Activator.CreateInstance(type)!;
            attr.LogicalName = "x";

            var back = MetadataSerializer.DeserializeAttribute(MetadataSerializer.SerializeAttribute(attr));

            back.GetType().Should().Be(type);
            back.AttributeType.Should().Be(attr.AttributeType);
        }
    }

    [Fact]
    public void Status_options_keep_their_state()
    {
        var statuscode = SystemAttributes.Complete(new EntityMetadata { LogicalName = "pb_x" }, [])
            .Single(a => a.LogicalName == "statuscode");

        var back = (StatusAttributeMetadata)MetadataSerializer.DeserializeAttribute(MetadataSerializer.SerializeAttribute(statuscode));

        back.OptionSet.Options.Should().AllBeOfType<StatusOptionMetadata>()
            .And.Contain(o => ((StatusOptionMetadata)o).State == 1 && o.Value == 2);
    }
}
