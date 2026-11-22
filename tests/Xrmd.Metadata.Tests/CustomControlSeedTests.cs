using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>Plan 3.5 UI-1d, task 1: the embedded seed definition of <c>customcontrol</c>.</summary>
public class CustomControlSeedTests
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
    public void Seed_defines_the_table()
    {
        CustomControlSchema.Table.Should().Be("customcontrol");
        CustomControlSchema.TypeCode.Should().Be(66);
        var table = Seed(CustomControlSchema.Table);
        (table.SchemaName, table.EntitySetName, table.ObjectTypeCode, table.OwnershipType, table.PrimaryNameAttribute)
            .Should().Be(("CustomControl", "customcontrols", CustomControlSchema.TypeCode, OwnershipTypes.OrganizationOwned, CustomControlSchema.Name));

        var name = Column<StringAttributeMetadata>(table, CustomControlSchema.Name);
        name.MaxLength.Should().Be(200);
        name.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired);
        Column<StringAttributeMetadata>(table, CustomControlSchema.CompatibleDataTypes).MaxLength.Should().Be(1000);
        Column<StringAttributeMetadata>(table, CustomControlSchema.Version).MaxLength.Should().Be(40);
        Column<StringAttributeMetadata>(table, CustomControlSchema.IntroducedVersion).MaxLength.Should().Be(48);
        foreach (var memo in new[] { CustomControlSchema.Manifest, CustomControlSchema.ClientJson, CustomControlSchema.AuthoringManifest })
            Column<MemoAttributeMetadata>(table, memo).MaxLength.Should().Be(MemoMax, memo);

        // The read-only ismanaged and the import-only component XML of the other row components.
        var managed = Column<BooleanAttributeMetadata>(table, "ismanaged");
        (managed.IsValidForCreate, managed.IsValidForUpdate, managed.DefaultValue).Should().Be((false, false, false));
        var xml = Column<MemoAttributeMetadata>(table, "xrmverse_componentxml");
        (xml.IsValidForCreate, xml.IsValidForUpdate).Should().Be((false, false));

        CustomControlSchema.ImportOnly.Should().Be("Custom controls are created by solution import in this version.");
    }
}
