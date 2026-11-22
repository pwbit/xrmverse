using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>#143, task 1: the embedded seed definitions of <c>entitymap</c> and <c>attributemap</c> (no database).</summary>
public class AttributeMapSeedTests
{
    private static EntityMetadata Seed(string logicalName)
    {
        using var stream = typeof(SystemSeed).Assembly.GetManifestResourceStream($"Xrmd.Metadata.Seed.{logicalName}.json")
                           ?? throw new InvalidOperationException($"No seed resource for {logicalName}.");
        using var reader = new StreamReader(stream);
        return MetadataSerializer.Deserialize(reader.ReadToEnd());
    }

    private static T Column<T>(EntityMetadata e, string name) where T : AttributeMetadata =>
        e.Attributes.Should().ContainSingle(a => a.LogicalName == name, name).Subject.Should().BeAssignableTo<T>(name).Subject;

    [Fact]
    public void Seed_defines_entitymap_and_attributemap()
    {
        var map = Seed(AttributeMapSchema.EntityMap);
        (map.SchemaName, map.EntitySetName, map.ObjectTypeCode, map.OwnershipType)
            .Should().Be(("EntityMap", "entitymaps", 4600, OwnershipTypes.OrganizationOwned));
        map.PrimaryNameAttribute.Should().BeNull();
        foreach (var name in new[] { AttributeMapSchema.SourceEntity, AttributeMapSchema.TargetEntity })
            Column<StringAttributeMetadata>(map, name).MaxLength.Should().Be(100);

        var attribute = Seed(AttributeMapSchema.AttributeMap);
        (attribute.SchemaName, attribute.EntitySetName, attribute.ObjectTypeCode, attribute.OwnershipType)
            .Should().Be(("AttributeMap", "attributemaps", 4601, OwnershipTypes.OrganizationOwned));
        attribute.PrimaryNameAttribute.Should().BeNull();
        Column<LookupAttributeMetadata>(attribute, AttributeMapSchema.EntityMapLookup).Targets.Should().Equal(AttributeMapSchema.EntityMap);
        foreach (var name in new[] { AttributeMapSchema.SourceAttribute, AttributeMapSchema.TargetAttribute })
            Column<StringAttributeMetadata>(attribute, name).MaxLength.Should().Be(100);
        Column<BooleanAttributeMetadata>(attribute, AttributeMapSchema.IsSystem).DefaultValue.Should().BeFalse();
        var link = attribute.ManyToOneRelationships.Should().ContainSingle().Subject;
        (link.SchemaName, link.ReferencedEntity, link.ReferencingAttribute, link.CascadeConfiguration.Delete, link.CascadeConfiguration.Assign)
            .Should().Be((AttributeMapSchema.Relationship, AttributeMapSchema.EntityMap, AttributeMapSchema.EntityMapLookup, CascadeType.Cascade,
                CascadeType.NoCascade));
    }
}
