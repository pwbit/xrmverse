using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// Plan 3.5 UI-1b, task 1: the <c>isdefault</c> and <c>formactivationstate</c> columns of the embedded <c>systemform</c> seed,
/// with the types, options and defaults of Learn's SystemForm table reference (no database).
/// </summary>
public class SystemFormSeedTests
{
    private static EntityMetadata Seed()
    {
        using var stream = typeof(SystemSeed).Assembly.GetManifestResourceStream("Xrmd.Metadata.Seed.systemform.json")
                           ?? throw new InvalidOperationException("No seed resource for systemform.");
        using var reader = new StreamReader(stream);
        return MetadataSerializer.Deserialize(reader.ReadToEnd());
    }

    [Fact]
    public void Systemform_has_isdefault_and_activation_state()
    {
        var form = Seed();

        var isDefault = form.Attributes.Should().ContainSingle(a => a.LogicalName == "isdefault").Subject
            .Should().BeOfType<BooleanAttributeMetadata>().Subject;
        isDefault.DefaultValue.Should().BeFalse();

        var state = form.Attributes.Should().ContainSingle(a => a.LogicalName == "formactivationstate").Subject
            .Should().BeOfType<PicklistAttributeMetadata>().Subject;
        state.DefaultFormValue.Should().Be(1);
        state.OptionSet.Name.Should().Be("systemform_formactivationstate");
        state.OptionSet.Options.Select(o => (o.Value!.Value, o.Label.UserLocalizedLabel.Label)).Should().Equal((0, "Inactive"), (1, "Active"));
    }
}
