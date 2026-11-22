using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

/// <summary>
/// Field security profiles in <c>customizations.xml</c> (plan 14, ruling F8): component type 70 under
/// <c>FieldSecurityProfiles</c>, each <c>FieldSecurityProfile</c> with <c>name</c>, <c>fieldsecurityprofileid</c> and
/// <c>description</c> attributes, holding <c>FieldPermissions/FieldPermission</c> elements whose values are child elements
/// (<c>EntityName</c>, <c>AttributeName</c>, <c>CanRead</c>, <c>CanUpdate</c>, <c>CanCreate</c>, <c>CanReadUnmasked</c>), the
/// shape of a real Dataverse export.
/// </summary>
public class FieldSecurityProfileXmlTests
{
    private static readonly Guid ProfileId = new("2d3c1b4a-5e6f-4a1b-9c2d-3e4f5a6b7c80");

    /// <summary>A permission in the shape of a real Dataverse export: child elements, in the export's order.</summary>
    private static XElement PermissionXml(string attribute, int read, int update, int create) =>
        new("FieldPermission",
            new XElement("EntityName", "xv_project"), new XElement("AttributeName", attribute),
            new XElement("CanRead", read), new XElement("CanUpdate", update), new XElement("CanCreate", create),
            new XElement("CanReadUnmasked", 0));

    private static XElement ProfileXml() =>
        new("FieldSecurityProfile",
            new XAttribute("name", "Finance"),
            new XAttribute("description", "Budget readers"),
            new XAttribute("fieldsecurityprofileid", ProfileId.ToString("B")),
            new XElement("FieldPermissions",
                PermissionXml("xv_budget", read: 4, update: 4, create: 0),
                PermissionXml("xv_code", read: 4, update: 0, create: 4)));

    private static SolutionPackage Read(Action<XDocument> change)
    {
        var (solution, customizations, files) = SolutionZip.Read(SolutionFixtureZip.Build());
        change(customizations);
        return CustomizationsReader.Read(solution, customizations, files);
    }

    private static SolutionPackage ReadWithProfile() =>
        Read(d => d.Root!.Element("FieldSecurityProfiles")!.Add(ProfileXml()));

    [Fact]
    public void Profile_round_trips_through_solution_xml()
    {
        var package = ReadWithProfile();

        var profile = package.FieldSecurityProfiles.Should().ContainSingle().Subject;
        profile.Id.Should().Be(ProfileId);
        profile.Name.Should().Be("Finance");
        profile.Description.Should().Be("Budget readers");
        profile.Permissions.Should().BeEquivalentTo(new[]
        {
            new PackageFieldPermission("xv_project", "xv_budget", 4, 0, 4, 0),
            new PackageFieldPermission("xv_project", "xv_code", 4, 4, 0, 0),
        });
        package.Unsupported.Should().NotContain(n => n.Name.LocalName == "FieldSecurityProfiles", "the section is a known component now");

        var (writtenSolution, writtenCustomizations) = CustomizationsWriter.Write(package);
        var written = writtenCustomizations.Root!.Element("FieldSecurityProfiles")!.Element("FieldSecurityProfile")!;
        written.Attribute("name")!.Value.Should().Be("Finance");
        written.Attribute("fieldsecurityprofileid")!.Value.Should().Be(ProfileId.ToString("B"));
        written.Attribute("description")!.Value.Should().Be("Budget readers");
        var permissions = written.Element("FieldPermissions")!.Elements("FieldPermission").ToList();
        permissions.Select(p => p.Element("AttributeName")!.Value).Should().Equal("xv_budget", "xv_code");
        permissions[0].Attributes().Should().BeEmpty();
        permissions[0].Elements().Select(e => e.Name.LocalName)
            .Should().Equal("EntityName", "AttributeName", "CanRead", "CanUpdate", "CanCreate", "CanReadUnmasked");
        permissions[0].Elements().Select(e => e.Value).Should().Equal("xv_project", "xv_budget", "4", "4", "0", "0");
        written.Descendants().Should().NotContain(e => e.Name.LocalName.Contains("systemuser", StringComparison.OrdinalIgnoreCase)
                                                      || e.Name.LocalName.Contains("team", StringComparison.OrdinalIgnoreCase));

        var zip = SolutionZip.Write(writtenSolution, writtenCustomizations, CustomizationsWriter.Files(package));
        var (s, c, f) = SolutionZip.Read(zip);
        var again = CustomizationsReader.Read(s, c, f);
        again.FieldSecurityProfiles.Should().BeEquivalentTo(package.FieldSecurityProfiles);
        XmlNormalizer.Normalize(CustomizationsWriter.Write(again).Customizations).Should().Be(XmlNormalizer.Normalize(writtenCustomizations));
    }

    [OpenProject365Fact]
    public void The_profile_of_a_real_export_is_read()
    {
        var zip = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "OpenProject365_1_0_1_1.zip"));
        var (solution, customizations, files) = SolutionZip.Read(zip);
        // Autonumber columns are refused (issue #38) before the profiles are reached: drop their formats.
        foreach (var format in customizations.Descendants("AutoNumberFormat").ToList()) format.Remove();

        var profile = CustomizationsReader.Read(solution, customizations, files).FieldSecurityProfiles.Should().ContainSingle().Subject;

        profile.Id.Should().Be(new Guid("85a50f28-d6cf-f111-a05b-7ced8d2d53e4"));
        profile.Name.Should().Be("OP365 Cost Visibility");
        profile.Permissions.Should().HaveCount(9);
        profile.Permissions.Should().ContainEquivalentOf(new PackageFieldPermission("op365_resource", "op365_hourlycost", 4, 4, 4, 0));
        profile.Permissions.Should().ContainEquivalentOf(new PackageFieldPermission("op365_agentproposal", "op365_evidence", 4, 4, 0, 0));
    }

    [Fact]
    public void A_package_without_profiles_writes_an_empty_section()
    {
        var package = Read(_ => { });

        package.FieldSecurityProfiles.Should().BeEmpty();
        CustomizationsWriter.Write(package).Customizations.Root!.Element("FieldSecurityProfiles")!.HasElements.Should().BeFalse();
    }

    [Fact]
    public void A_permission_value_other_than_0_or_4_is_refused()
    {
        var act = () => Read(d =>
        {
            var profile = ProfileXml();
            profile.Descendants("CanRead").First().Value = "2";
            d.Root!.Element("FieldSecurityProfiles")!.Add(profile);
        });

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
                                                            && e.Message.Contains("Finance") && e.Message.Contains("CanRead"));
    }

    [Theory]
    [InlineData("EntityName")]
    [InlineData("AttributeName")]
    public void A_permission_without_its_table_or_column_is_refused(string missing)
    {
        var act = () => Read(d =>
        {
            var profile = ProfileXml();
            profile.Descendants(missing).First().Remove();
            d.Root!.Element("FieldSecurityProfiles")!.Add(profile);
        });

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400
                                                            && e.Message.Contains("Finance") && e.Message.Contains(missing));
    }

    [Fact]
    public void A_permission_outside_the_FieldPermissions_element_is_counted_not_read()
    {
        var package = Read(d =>
        {
            var profile = ProfileXml();
            profile.Add(PermissionXml("xv_extra", read: 4, update: 0, create: 0));
            d.Root!.Element("FieldSecurityProfiles")!.Add(profile);
        });

        var profile = package.FieldSecurityProfiles.Should().ContainSingle().Subject;
        profile.Permissions.Select(p => p.Attribute).Should().Equal("xv_budget", "xv_code");
        profile.UnwrappedPermissions.Should().Be(1, "the import log warns about it");
    }
}
