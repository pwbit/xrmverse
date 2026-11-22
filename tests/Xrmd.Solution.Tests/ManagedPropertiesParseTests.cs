using System.Text.Json;
using FluentAssertions;
using Xrmd.Integration.Tests;
using Xrmd.Solution.Layers;

namespace Xrmd.Solution.Tests;

/// <summary>
/// Plan 2.6, Task 5 (ruling M13): the managed properties a layer payload gives a component are read from the component's own
/// element only, and a payload that cannot be read is reported, never taken as "everything allowed".
/// </summary>
public class ManagedPropertiesParseTests
{
    private static string Payload(string xml) => JsonSerializer.Serialize(new { xml, files = new Dictionary<string, string>() });

    private static SolutionPackage Read(ManagedSpec spec)
    {
        var (solution, customizations, files) = SolutionZip.Read(ManagedTestSolution.Build(spec));
        return CustomizationsReader.Read(solution, customizations, files);
    }

    [Fact]
    public void Flags_of_a_built_column_and_table_come_from_their_own_elements()
    {
        var spec = ManagedTestSolution.V1 with
        {
            Tables = [new(ManagedTestSolution.Table, "Item", [
                new("xm_name", "Name", PrimaryName: true),
                new("xm_code", "Code", Props: new(IsRenameable: false, CanModifyRequirementLevelSettings: false)),
            ], new(IsCustomizable: false))],
        };
        var package = Read(spec);

        ManagedProperties.Parse(LayerPayload.From(package, ComponentType.Attribute, "xm_item.xm_code"), ComponentType.Attribute)
            .Should().Be(new ManagedFlags(IsCustomizable: true, IsRenameable: false, CanBeDeleted: true, CanModifyRequirementLevel: false));
        ManagedProperties.Parse(LayerPayload.From(package, ComponentType.Entity, "xm_item"), ComponentType.Entity)
            .IsCustomizable.Should().BeFalse();
    }

    /// <summary>Final review I4: the table's CanCreateAttributes, CanCreateForms, CanCreateViews and CanCreateCharts are read too.</summary>
    [Fact]
    public void Table_create_flags_come_from_the_entity_element()
    {
        var spec = ManagedTestSolution.V1 with
        {
            Tables = [new(ManagedTestSolution.Table, "Item", [new("xm_name", "Name", PrimaryName: true)],
                new(CanCreateAttributes: false, CanCreateForms: false, CanCreateViews: false))],
        };

        var flags = ManagedProperties.Parse(LayerPayload.From(Read(spec), ComponentType.Entity, "xm_item"), ComponentType.Entity);

        flags.Should().Be(new ManagedFlags(true, true, true, true, CanCreateAttributes: false, CanCreateForms: false, CanCreateViews: false,
            CanCreateCharts: true));
        ManagedProperties.Parse(Payload("<ImportExportXml><Entity><EntityInfo><entity><CanCreateCharts>0</CanCreateCharts></entity></EntityInfo></Entity></ImportExportXml>"),
            ComponentType.Entity).CanCreateCharts.Should().BeFalse();
    }

    [Fact]
    public void Row_component_reads_only_the_direct_children_of_its_element()
    {
        // A nested element carrying IsCustomizable (inside the role's privileges) is not the role's own property.
        var payload = Payload("<ImportExportXml><Roles><Role id=\"{1}\"><RolePrivileges><RolePrivilege><IsCustomizable>0</IsCustomizable></RolePrivilege></RolePrivileges></Role></Roles></ImportExportXml>");

        ManagedProperties.Parse(payload, ComponentType.Role).Should().Be(ManagedFlags.Open);
        ManagedProperties.Parse(Payload("<ImportExportXml><Roles><Role><IsCustomizable>0</IsCustomizable></Role></Roles></ImportExportXml>"), ComponentType.Role)
            .IsCustomizable.Should().BeFalse();
    }

    [Fact]
    public void Missing_payload_allows_everything()
    {
        ManagedProperties.Parse(null, ComponentType.Role).Should().Be(ManagedFlags.Open);
        ManagedProperties.Parse(Payload("<ImportExportXml />"), ComponentType.SavedQuery).Should().Be(ManagedFlags.Open);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"files\":{}}")]
    [InlineData("{\"xml\":\"<unclosed>\"}")]
    public void Unreadable_payload_throws(string payload)
    {
        var parse = () => ManagedProperties.Parse(payload, ComponentType.Attribute);

        parse.Should().Throw<InvalidOperationException>().WithMessage("*cannot be read*");
    }
}
