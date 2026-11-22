using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

/// <summary>
/// A Customer column in the solution file: <c>type="customer"</c> with one relationship to <c>account</c> and one to
/// <c>contact</c> (the fixture's <c>xv_task.xv_projectid</c>, turned into one), read and written back.
/// </summary>
public class CustomerSolutionTests
{
    private const string Column = "xv_projectid";

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static (XDocument Solution, XDocument Customizations, IReadOnlyDictionary<string, byte[]> Files) Zip(bool bothRelationships = true) =>
        SolutionZip.Read(SolutionFixtureZip.Build(document =>
        {
            var attribute = document.Descendants("Entity").Single(e => Is(e.Element("Name")?.Value, "xv_task"))
                .Descendants("attribute").Single(a => Is(a.Element("Name")?.Value, Column));
            attribute.Element("Type")!.Value = "customer";
            var relationship = document.Descendants("EntityRelationship").Single(r => (string?)r.Attribute("Name") == "xv_project_xv_task_ProjectId");
            relationship.Element("ReferencedEntityName")!.Value = "account";
            if (!bothRelationships) return;
            var second = new XElement(relationship);
            second.SetAttributeValue("Name", "xv_task_ProjectId_contact");
            second.Element("ReferencedEntityName")!.Value = "contact";
            second.Descendants("NavigationPropertyName").First().Value = "xv_ProjectId_contact";
            relationship.AddAfterSelf(second);
        }));

    private static SolutionPackage Read(bool bothRelationships = true)
    {
        var (solution, customizations, files) = Zip(bothRelationships);
        return CustomizationsReader.Read(solution, customizations, files);
    }

    [Fact]
    public void Reads_a_customer_column_with_its_two_relationships()
    {
        var package = Read();

        var lookup = package.Lookups.Single(l => l.EntityLogicalName == "xv_task" && l.LogicalName == Column);
        lookup.AttributeType.Should().Be(AttributeTypeCode.Customer);
        lookup.IsCustomAttribute.Should().BeTrue();
        lookup.Targets.Should().BeEquivalentTo("account", "contact");
        package.Relationships.OfType<OneToManyRelationshipMetadata>().Where(r => r.ReferencingAttribute == Column && r.ReferencingEntity == "xv_task")
            .Select(r => (r.SchemaName, r.ReferencedEntity)).Should().BeEquivalentTo(
                [("xv_project_xv_task_ProjectId", "account"), ("xv_task_ProjectId_contact", "contact")]);
    }

    [Fact]
    public void Writes_a_customer_column_as_type_customer_with_both_relationships()
    {
        var package = Read();

        var written = CustomizationsWriter.Write(package).Customizations;

        written.Descendants("attribute").Single(a => Is(a.Element("Name")?.Value, Column) && Is(a.Ancestors("Entity").First().Element("Name")?.Value, "xv_task"))
            .Element("Type")!.Value.Should().Be("customer");
        written.Descendants("EntityRelationship")
            .Where(r => r.Element("ReferencingEntityName")?.Value == "xv_task" && r.Element("ReferencingAttributeName")?.Value == "xv_ProjectId")
            .Select(r => r.Element("ReferencedEntityName")!.Value).Should().BeEquivalentTo("account", "contact");

        // Written then read again: the same two relationships and the same column.
        var (solution, customizations) = CustomizationsWriter.Write(package);
        var again = CustomizationsReader.Read(solution, customizations, CustomizationsWriter.Files(package));
        again.Lookups.Single(l => l.EntityLogicalName == "xv_task" && l.LogicalName == Column).AttributeType.Should().Be(AttributeTypeCode.Customer);
        again.Lookups.Single(l => l.EntityLogicalName == "xv_task" && l.LogicalName == Column).Targets.Should().BeEquivalentTo("account", "contact");
        XmlNormalizer.Normalize(CustomizationsWriter.Write(again).Customizations).Should().Be(XmlNormalizer.Normalize(customizations));
    }
}
