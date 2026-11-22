using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Integration.Tests;

namespace Xrmd.Solution.Tests;

/// <summary>
/// The managed test solution builder of plan 2.6 (task 1; the file is linked from <c>Xrmd.Integration.Tests</c>): what it builds is
/// a file the reader accepts, with the tables, the managed-property elements of each component and the step.
/// </summary>
public class ManagedTestSolutionShapeTests
{
    private static SolutionPackage Read(ManagedSpec spec)
    {
        var (solution, customizations, files) = SolutionZip.Read(ManagedTestSolution.Build(spec));
        return CustomizationsReader.Read(solution, customizations, files);
    }

    private static XElement? Kept(SolutionPackage package, (int, string) key) =>
        package.Unmodelled!.TryGetValue(key, out var xml) ? xml : null;

    [Fact]
    public void Built_managed_file_reads_back()
    {
        var package = Read(ManagedTestSolution.V1);

        package.Solution.UniqueName.Should().Be("xm_base");
        package.Solution.Managed.Should().BeTrue();
        package.Solution.Version.Should().Be("1.0.0.0");
        package.Publisher.UniqueName.Should().Be(ManagedTestSolution.PublisherName);
        package.Publisher.CustomizationPrefix.Should().Be(ManagedTestSolution.Prefix);

        var table = package.Entities.Should().ContainSingle().Subject;
        table.LogicalName.Should().Be("xm_item");
        table.PrimaryNameAttribute.Should().Be("xm_name");
        table.Attributes.Select(a => a.LogicalName).Should().BeEquivalentTo("xm_itemid", "xm_name", "xm_code", "xm_legacy");
        table.Attributes.Single(a => a.LogicalName == "xm_code").DisplayName.UserLocalizedLabel.Label.Should().Be("Code");
        ((Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata)table.Attributes.Single(a => a.LogicalName == "xm_code")).MaxLength.Should().Be(100);

        package.OptionSets.Should().ContainSingle(o => o.Name == "xm_status");
        package.Roles.Should().ContainSingle(r => r.Name == "xm_reader").Which.Privileges.Should().ContainSingle();
        package.WebResources.Should().ContainSingle(w => w.Name == "xm_/scripts/item.js").Which.Content.Should().NotBeEmpty();
        package.Assemblies.Should().ContainSingle().Which.Types.Should().ContainSingle(t => t.TypeName == ManagedTestSolution.StepPlugin);
        var step = package.Steps.Should().ContainSingle().Subject;
        step.PrimaryEntity.Should().Be("xm_item");
        step.Stage.Should().Be(40);
        var view = package.Opaque.Should().ContainSingle().Subject;
        view.EntityLogicalName.Should().Be("xm_item");
    }

    [Fact]
    public void Built_file_carries_the_managed_properties_of_each_component()
    {
        var package = Read(ManagedTestSolution.V1 with
        {
            Tables = [new("xm_item", "Item", [
                new("xm_name", "Name", PrimaryName: true),
                new("xm_code", "Code", Props: new(IsCustomizable: false, IsRenameable: false, CanModifyRequirementLevelSettings: false)),
            ], new(IsCustomizable: false, CanCreateAttributes: false))],
            OptionSets = [new("xm_status", "Status", [(100000000, "Open")], TestManagedProps.Locked)],
            Views = [new("Active Items", "xm_item", ["xm_name"], TestManagedProps.Locked)],
            WebResources = [new("xm_/scripts/item.js", "//", TestManagedProps.Locked)],
            Steps = [new("xm_item", "Update", ManagedTestSolution.StepPlugin, 40, Props: new(IsCustomizable: false))],
        });

        // The element's own flag: a direct child, or (for the entity) a child of its entity element, never a nested attribute's.
        static string Flag(XElement? xml, string name) =>
            xml?.Elements(name).Select(e => e.Value).FirstOrDefault()
            ?? xml?.Elements("entity").Elements(name).Select(e => e.Value).FirstOrDefault() ?? "missing";

        var table = package.Entities.Single();
        var tableXml = package.Unmodelled![(Xrmd.Solution.ComponentType.Entity, "xm_item")];
        Flag(tableXml, "IsCustomizable").Should().Be("0");
        Flag(tableXml, "CanCreateAttributes").Should().Be("0");
        var codeXml = Kept(package, (Xrmd.Solution.ComponentType.Attribute, "xm_item.xm_code"));
        Flag(codeXml, "IsCustomizable").Should().Be("0");
        Flag(codeXml, "IsRenameable").Should().Be("0");
        Flag(codeXml, "CanModifyRequirementLevelSettings").Should().Be("0");
        Flag(Kept(package, (Xrmd.Solution.ComponentType.Attribute, "xm_item.xm_name")), "IsCustomizable").Should().Be("1");

        Flag(Kept(package, (Xrmd.Solution.ComponentType.OptionSet, "xm_status")), "IsCustomizable").Should().Be("0");
        var view = package.Opaque.Single().Xml;
        Flag(view, "IsCustomizable").Should().Be("0");
        Flag(view, "CanBeDeleted").Should().Be("0");
        table.LogicalName.Should().Be("xm_item");
        Flag(Kept(package, (Xrmd.Solution.ComponentType.WebResource, package.WebResources.Single().Id.ToString("D"))), "IsCustomizable").Should().Be("0");
        Flag(Kept(package, (Xrmd.Solution.ComponentType.SdkMessageProcessingStep, package.Steps.Single().Id.ToString("D"))), "IsCustomizable").Should().Be("0");
    }

    [Fact]
    public void V2_and_Other_differ_from_V1_as_specified()
    {
        var v2 = Read(ManagedTestSolution.V2);
        v2.Solution.Version.Should().Be("2.0.0.0");
        var table = v2.Entities.Single();
        table.Attributes.Select(a => a.LogicalName).Should().BeEquivalentTo("xm_itemid", "xm_name", "xm_code", "xm_extra");
        table.Attributes.Single(a => a.LogicalName == "xm_name").DisplayName.UserLocalizedLabel.Label.Should().Be("Title");
        v2.Roles.Should().BeEmpty();
        v2.Steps.Should().ContainSingle();

        var other = Read(ManagedTestSolution.Other);
        other.Solution.UniqueName.Should().Be("xm_other");
        other.Entities.Single().Attributes.Single(a => a.LogicalName == "xm_code").DisplayName.UserLocalizedLabel.Label.Should().Be("Code (other)");

        // The same component has the same ids in every build.
        Read(ManagedTestSolution.V1).Steps.Single().Id.Should().Be(v2.Steps.Single().Id);
        ManagedTestSolution.Build(ManagedTestSolution.V1).Should().Equal(ManagedTestSolution.Build(ManagedTestSolution.V1));
    }

    [Fact]
    public void Unmanaged_flag_is_written()
    {
        Read(ManagedTestSolution.V1 with { Managed = false }).Solution.Managed.Should().BeFalse();
    }
}
