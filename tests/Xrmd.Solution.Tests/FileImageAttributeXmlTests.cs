using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

/// <summary>
/// File and image attributes in <c>customizations.xml</c> (ruling F11): <c>Type</c> <c>file</c> / <c>image</c> with
/// <c>MaxSizeInKB</c>, <c>CanStoreFullImage</c> and <c>IsPrimaryImage</c>. The companion attributes a file may carry
/// (<c>&lt;file&gt;_name</c>, <c>&lt;image&gt;id</c>, <c>&lt;image&gt;_timestamp</c>, <c>&lt;image&gt;_url</c>) are read as
/// attributes the server generates with their main column, never as columns to create, and xrmverse does not write them.
/// </summary>
public class FileImageAttributeXmlTests
{
    private static XElement Attribute(string physicalName, string type, params XElement[] typeSpecific) =>
        new("attribute", new XAttribute("PhysicalName", physicalName),
            new XElement("Type", type),
            new XElement("Name", physicalName.ToLowerInvariant()),
            new XElement("LogicalName", physicalName.ToLowerInvariant()),
            new XElement("RequiredLevel", "none"),
            new XElement("DisplayMask", "ValidForAdvancedFind|ValidForForm|ValidForGrid"),
            new XElement("ImeMode", "auto"),
            new XElement("ValidForUpdateApi", "1"),
            new XElement("ValidForReadApi", "1"),
            new XElement("ValidForCreateApi", "1"),
            new XElement("IsCustomField", "1"),
            new XElement("IsAuditEnabled", "0"),
            new XElement("IsSecured", "0"),
            new XElement("IntroducedVersion", "1.0.0.0"),
            new XElement("IsCustomizable", "1"),
            new XElement("IsRenameable", "1"),
            new XElement("CanModifySearchSettings", "1"),
            new XElement("CanModifyRequirementLevelSettings", "1"),
            new XElement("CanModifyAdditionalSettings", "1"),
            new XElement("SourceType", "0"),
            new XElement("IsGlobalFilterEnabled", "0"),
            new XElement("IsSortableEnabled", "0"),
            new XElement("CanModifyGlobalFilterSettings", "1"),
            new XElement("CanModifyIsSortableSettings", "1"),
            new XElement("IsDataSourceSecret", "0"),
            new XElement("AutoNumberFormat"),
            new XElement("IsSearchable", "0"),
            new XElement("IsFilterable", "0"),
            new XElement("IsRetrievable", "0"),
            new XElement("IsLocalizable", "0"),
            typeSpecific,
            new XElement("displaynames", new XElement("displayname", new XAttribute("description", physicalName), new XAttribute("languagecode", "1033"))),
            new XElement("Descriptions", new XElement("Description", new XAttribute("description", ""), new XAttribute("languagecode", "1033"))));

    /// <summary>The fixture with a file column and two image columns on <c>xv_project</c> (the second one primary).</summary>
    private static (XDocument Solution, XDocument Customizations, IReadOnlyDictionary<string, byte[]> Files) Fixture(bool companions)
    {
        var (solution, customizations, files) = SolutionZip.Read(SolutionFixtureZip.Build());
        var attributes = customizations.Root!.Element("Entities")!.Elements("Entity")
            .Single(e => e.Element("Name")!.Value == "xv_Project").Element("EntityInfo")!.Element("entity")!.Element("attributes")!;
        attributes.Add(
            Attribute("xv_Contract", "file", new XElement("MaxSizeInKB", "65536")),
            Attribute("xv_Photo", "image", new XElement("MaxSizeInKB", "10240"), new XElement("CanStoreFullImage", "1"), new XElement("IsPrimaryImage", "0")),
            Attribute("xv_Logo", "image", new XElement("MaxSizeInKB", "2048"), new XElement("CanStoreFullImage", "0"), new XElement("IsPrimaryImage", "1")));
        if (companions)
            attributes.Add(
                Attribute("xv_Contract_Name", "nvarchar", new XElement("Format", "text"), new XElement("MaxLength", "255"), new XElement("Length", "510")),
                Attribute("xv_PhotoId", "uniqueidentifier"),
                Attribute("xv_Photo_Timestamp", "bigint", new XElement("MinValue", "-9223372036854775808"), new XElement("MaxValue", "9223372036854775807")),
                Attribute("xv_Photo_URL", "nvarchar", new XElement("Format", "url"), new XElement("MaxLength", "200"), new XElement("Length", "400")));
        return (solution, customizations, files);
    }

    private static SolutionPackage Read(bool companions)
    {
        var (solution, customizations, files) = Fixture(companions);
        return CustomizationsReader.Read(solution, customizations, files);
    }

    private static T Attribute<T>(SolutionPackage package, string name) where T : AttributeMetadata =>
        package.Entities.Single(e => e.LogicalName == "xv_project").Attributes.OfType<T>().Single(a => a.LogicalName == name);

    private static XElement WrittenAttribute(XDocument customizations, string name) =>
        customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == name);

    [Fact]
    public void File_and_image_attributes_are_read_with_their_properties()
    {
        var package = Read(companions: false);

        var contract = Attribute<FileAttributeMetadata>(package, "xv_contract");
        contract.SchemaName.Should().Be("xv_Contract");
        contract.MaxSizeInKB.Should().Be(65536);
        var photo = Attribute<ImageAttributeMetadata>(package, "xv_photo");
        (photo.MaxSizeInKB, photo.CanStoreFullImage, photo.IsPrimaryImage).Should().Be((10240, true, false));
        var logo = Attribute<ImageAttributeMetadata>(package, "xv_logo");
        (logo.MaxSizeInKB, logo.CanStoreFullImage, logo.IsPrimaryImage).Should().Be((2048, false, true));
        package.Entities.Single(e => e.LogicalName == "xv_project").Attributes.OfType<ImageAttributeMetadata>().Select(a => a.LogicalName)
            .Should().Equal(["xv_photo", "xv_logo"], "the image columns keep the file's order, which is their creation order");
    }

    [Fact]
    public void Companions_in_the_file_are_read_as_generated_by_their_main_column()
    {
        var package = Read(companions: true);

        foreach (var (name, main) in new[] { ("xv_contract_name", "xv_contract"), ("xv_photoid", "xv_photo"), ("xv_photo_timestamp", "xv_photo"), ("xv_photo_url", "xv_photo") })
        {
            var companion = package.Entities.Single(e => e.LogicalName == "xv_project").Attributes.Single(a => a.LogicalName == name);
            companion.AttributeOf.Should().Be(main, $"{name} is generated with {main}");
            TypeMapping.IsStoredByOtherAttribute(companion).Should().BeTrue($"the import must not create {name} as a column of its own");
        }
    }

    [Fact]
    public void Solution_export_import_round_trips_file_and_image_attributes()
    {
        var package = Read(companions: true);

        var written = CustomizationsWriter.Write(package).Customizations;

        var contract = WrittenAttribute(written, "xv_contract");
        contract.Attribute("PhysicalName")!.Value.Should().Be("xv_Contract");
        contract.Element("Type")!.Value.Should().Be("file");
        contract.Element("MaxSizeInKB")!.Value.Should().Be("65536");
        contract.Element("CanStoreFullImage").Should().BeNull("only an image column has it");
        var photo = WrittenAttribute(written, "xv_photo");
        photo.Element("Type")!.Value.Should().Be("image");
        (photo.Element("MaxSizeInKB")!.Value, photo.Element("CanStoreFullImage")!.Value, photo.Element("IsPrimaryImage")!.Value)
            .Should().Be(("10240", "1", "0"));
        var logo = WrittenAttribute(written, "xv_logo");
        (logo.Element("MaxSizeInKB")!.Value, logo.Element("CanStoreFullImage")!.Value, logo.Element("IsPrimaryImage")!.Value)
            .Should().Be(("2048", "0", "1"));
        // What the file had around the modelled elements is written again (kept), in its place.
        photo.Element("IsRetrievable")!.Value.Should().Be("0");
        photo.Elements().Select(e => e.Name.LocalName).Should().ContainInOrder(
            "IsLocalizable", "MaxSizeInKB", "CanStoreFullImage", "IsPrimaryImage", "MaxHeight", "MaxWidth", "displaynames");
        (photo.Element("MaxHeight")!.Value, photo.Element("MaxWidth")!.Value).Should().Be(("144", "144"), "thumbnails are always 144x144");
        contract.Element("MaxHeight").Should().BeNull();

        // The companions are the server's (created with their main column on import): xrmverse does not write them.
        written.Descendants("attribute").Select(a => a.Element("LogicalName")?.Value)
            .Should().NotContain(["xv_contract_name", "xv_photoid", "xv_photo_timestamp", "xv_photo_url"]);

        var (s, c, f) = SolutionZip.Read(SolutionZip.Write(CustomizationsWriter.Write(package).Solution, written, CustomizationsWriter.Files(package)));
        var again = CustomizationsReader.Read(s, c, f);
        XmlNormalizer.Normalize(CustomizationsWriter.Write(again).Customizations).Should().Be(XmlNormalizer.Normalize(written));
        Attribute<ImageAttributeMetadata>(again, "xv_logo").IsPrimaryImage.Should().BeTrue();
        Attribute<FileAttributeMetadata>(again, "xv_contract").MaxSizeInKB.Should().Be(65536);
    }

    [Fact]
    public void A_file_or_image_attribute_without_a_size_is_read_without_one()
    {
        var (solution, customizations, files) = Fixture(companions: false);
        foreach (var size in customizations.Descendants("attribute").Where(a => a.Element("LogicalName")?.Value is "xv_contract" or "xv_photo")
                     .Select(a => a.Element("MaxSizeInKB")!).ToList())
            size.Remove();

        var package = CustomizationsReader.Read(solution, customizations, files);

        Attribute<FileAttributeMetadata>(package, "xv_contract").MaxSizeInKB.Should().BeNull("the server applies its default (32768)");
        Attribute<ImageAttributeMetadata>(package, "xv_photo").MaxSizeInKB.Should().BeNull("the server applies its default (10240)");
    }

    [Fact]
    public void Two_primary_images_in_one_table_are_an_explicit_error()
    {
        var (solution, customizations, files) = Fixture(companions: false);
        customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == "xv_photo").Element("IsPrimaryImage")!.Value = "1";

        var act = () => CustomizationsReader.Read(solution, customizations, files);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message == "Entity 'xv_project' has more than one primary image (xv_photo, xv_logo): a table has one primary image.");
    }

    [Fact]
    public void A_thumbnail_size_other_than_144_is_an_explicit_error()
    {
        var (solution, customizations, files) = Fixture(companions: false);
        customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == "xv_photo").Add(new XElement("MaxWidth", "200"));

        var act = () => CustomizationsReader.Read(solution, customizations, files);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.Message.Contains("MaxWidth 200"));
    }

    [Fact]
    public void A_companion_without_its_main_column_is_an_ordinary_attribute()
    {
        var (solution, customizations, files) = Fixture(companions: true);
        customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == "xv_contract").Remove();

        var package = CustomizationsReader.Read(solution, customizations, files);

        Attribute<StringAttributeMetadata>(package, "xv_contract_name").AttributeOf.Should().BeNull();
    }

    [Fact]
    public void Reading_an_image_attribute_with_an_invalid_bit_fails_naming_it()
    {
        var (solution, customizations, files) = Fixture(companions: false);
        customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == "xv_photo").Element("CanStoreFullImage")!.Value = "maybe";

        var act = () => CustomizationsReader.Read(solution, customizations, files);

        act.Should().Throw<XrmdFaultException>().WithMessage("*CanStoreFullImage*");
    }
}
