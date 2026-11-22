using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>Plan 3.5 UI-1f, task 1: the embedded seed definition of <c>savedqueryvisualization</c> (Global Constraints).</summary>
public class ChartSeedTests
{
    private const int MemoMax = 1073741823;

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
    public void Seed_defines_the_visualization_table()
    {
        (ChartSchema.Table, ChartSchema.TypeCode, ChartSchema.ComponentType).Should().Be(("savedqueryvisualization", 1111, 59));
        var table = Seed(ChartSchema.Table);
        (table.SchemaName, table.EntitySetName, table.ObjectTypeCode, table.OwnershipType, table.PrimaryNameAttribute)
            .Should().Be(("SavedQueryVisualization", ChartSchema.EntitySet, ChartSchema.TypeCode, OwnershipTypes.OrganizationOwned, ChartSchema.Name));

        Column<StringAttributeMetadata>(table, ChartSchema.Name).MaxLength.Should().Be(100);
        Column<MemoAttributeMetadata>(table, ChartSchema.Description).MaxLength.Should().Be(2000);
        Column<StringAttributeMetadata>(table, ChartSchema.PrimaryEntityTypeCode).MaxLength.Should().Be(64);
        Column<MemoAttributeMetadata>(table, ChartSchema.DataDescription).MaxLength.Should().Be(MemoMax);
        Column<MemoAttributeMetadata>(table, ChartSchema.PresentationDescription).MaxLength.Should().Be(MemoMax);
        Column<BooleanAttributeMetadata>(table, ChartSchema.IsDefault).DefaultValue.Should().BeFalse();
        Column<PicklistAttributeMetadata>(table, ChartSchema.ChartType).OptionSet.Options.Select(o => o.Value).Should().Equal(0, 1);
        Column<PicklistAttributeMetadata>(table, ChartSchema.Type).OptionSet.Options.Select(o => o.Value).Should().Equal(0, 1);
        Column<LookupAttributeMetadata>(table, ChartSchema.WebResourceId).Targets.Should().Equal("webresource");
        Column<StringAttributeMetadata>(table, ChartSchema.IntroducedVersion).MaxLength.Should().Be(48);
        var managed = Column<BooleanAttributeMetadata>(table, "ismanaged");
        (managed.IsValidForCreate, managed.IsValidForUpdate, managed.DefaultValue).Should().Be((false, false, false));
        var xml = Column<MemoAttributeMetadata>(table, "xrmverse_componentxml");
        (xml.IsValidForCreate, xml.IsValidForUpdate).Should().Be((false, false));
    }
}
