using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>#167, task 1: the embedded seed definitions of <c>environmentvariabledefinition</c> and <c>environmentvariablevalue</c>.</summary>
public class EnvironmentVariableSeedTests
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
    public void Seed_defines_both_tables()
    {
        var definition = Seed(EnvironmentVariableSchema.Definition);
        (definition.SchemaName, definition.EntitySetName, definition.ObjectTypeCode, definition.OwnershipType, definition.PrimaryNameAttribute)
            .Should().Be(("EnvironmentVariableDefinition", "environmentvariabledefinitions", EnvironmentVariableSchema.DefinitionTypeCode,
                OwnershipTypes.UserOwned, EnvironmentVariableSchema.SchemaName));
        EnvironmentVariableSchema.DefinitionTypeCode.Should().Be(380);
        var schemaName = Column<StringAttributeMetadata>(definition, EnvironmentVariableSchema.SchemaName);
        schemaName.MaxLength.Should().Be(100);
        schemaName.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired);
        Column<StringAttributeMetadata>(definition, EnvironmentVariableSchema.DisplayName).MaxLength.Should().Be(100);
        Column<StringAttributeMetadata>(definition, EnvironmentVariableSchema.IntroducedVersion).MaxLength.Should().Be(48);
        foreach (var memo in new[]
                 {
                     EnvironmentVariableSchema.Description, EnvironmentVariableSchema.DefaultValue, EnvironmentVariableSchema.ValueSchema,
                     EnvironmentVariableSchema.Hint,
                 })
            Column<MemoAttributeMetadata>(definition, memo).MaxLength.Should().Be(2000, memo);
        Column<BooleanAttributeMetadata>(definition, EnvironmentVariableSchema.IsRequired).DefaultValue.Should().BeFalse();

        var type = Column<PicklistAttributeMetadata>(definition, EnvironmentVariableSchema.Type);
        type.DefaultFormValue.Should().Be((int)VariableType.String);
        type.OptionSet.Options.Select(o => o.Value).Should().Equal(100000000, 100000001, 100000002, 100000003, 100000004, 100000005);
        Enum.GetValues<VariableType>().Select(t => (int)t).Should().Equal(100000000, 100000001, 100000002, 100000003, 100000004, 100000005);
        var store = Column<PicklistAttributeMetadata>(definition, EnvironmentVariableSchema.SecretStore);
        store.DefaultFormValue.Should().Be(0);
        store.OptionSet.Options.Select(o => o.Value).Should().Equal(0, 1);

        var value = Seed(EnvironmentVariableSchema.Value);
        (value.SchemaName, value.EntitySetName, value.ObjectTypeCode, value.OwnershipType, value.PrimaryNameAttribute)
            .Should().Be(("EnvironmentVariableValue", "environmentvariablevalues", EnvironmentVariableSchema.ValueTypeCode,
                OwnershipTypes.UserOwned, EnvironmentVariableSchema.SchemaName));
        EnvironmentVariableSchema.ValueTypeCode.Should().Be(381);
        Column<StringAttributeMetadata>(value, EnvironmentVariableSchema.SchemaName).MaxLength.Should().Be(100);
        Column<MemoAttributeMetadata>(value, EnvironmentVariableSchema.ValueColumn).MaxLength.Should().Be(2000);
        var lookup = Column<LookupAttributeMetadata>(value, EnvironmentVariableSchema.DefinitionLookup);
        lookup.Targets.Should().Equal(EnvironmentVariableSchema.Definition);
        lookup.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired);

        var link = value.ManyToOneRelationships.Should().ContainSingle().Subject;
        (link.SchemaName, link.ReferencedEntity, link.ReferencingAttribute)
            .Should().Be((EnvironmentVariableSchema.Relationship, EnvironmentVariableSchema.Definition, EnvironmentVariableSchema.DefinitionLookup));
        var cascade = link.CascadeConfiguration;
        (cascade.Delete, cascade.Assign, cascade.Reparent).Should().Be((CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade));
        (cascade.Merge, cascade.Share, cascade.Unshare, cascade.Archive, cascade.RollupView)
            .Should().Be((CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade));
    }
}
