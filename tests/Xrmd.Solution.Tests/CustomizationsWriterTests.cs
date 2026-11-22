using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

public class CustomizationsWriterTests
{
    private static (XDocument Solution, XDocument Customizations, IReadOnlyDictionary<string, byte[]> Files) Fixture() =>
        SolutionZip.Read(SolutionFixtureZip.Build());

    /// <summary>The first line where two canonical texts differ, with some context, so that a failure says what differs.</summary>
    private static void ShouldBeSameDocument(string actual, string expected)
    {
        if (actual == expected) return;
        var a = actual.Split('\n');
        var e = expected.Split('\n');
        var line = Enumerable.Range(0, Math.Min(a.Length, e.Length)).FirstOrDefault(i => a[i] != e[i], Math.Min(a.Length, e.Length));
        string Around(string[] lines) => string.Join('\n', lines.Skip(Math.Max(0, line - 8)).Take(16));
        actual.Should().Be(expected, "the documents differ at line {0}:\n--- written ---\n{1}\n--- fixture ---\n{2}", line + 1, Around(a), Around(e));
    }

    [Fact]
    public void Reader_writer_round_trip_is_stable()
    {
        var (solution, customizations, files) = Fixture();
        var package = CustomizationsReader.Read(solution, customizations, files);

        var written = CustomizationsWriter.Write(package);

        ShouldBeSameDocument(XmlNormalizer.Normalize(written.Customizations), XmlNormalizer.Normalize(customizations));
        ShouldBeSameDocument(XmlNormalizer.Normalize(written.Solution), XmlNormalizer.Normalize(solution));
    }

    [Fact]
    public void Written_package_reads_back_to_the_same_package()
    {
        var (solution, customizations, files) = Fixture();
        var package = CustomizationsReader.Read(solution, customizations, files);

        var (writtenSolution, writtenCustomizations) = CustomizationsWriter.Write(package);
        var zip = SolutionZip.Write(writtenSolution, writtenCustomizations, CustomizationsWriter.Files(package));
        var (s, c, f) = SolutionZip.Read(zip);
        var again = CustomizationsReader.Read(s, c, f);

        XmlNormalizer.Normalize(CustomizationsWriter.Write(again).Customizations).Should().Be(XmlNormalizer.Normalize(writtenCustomizations));
        again.Assemblies.Single().Content.Should().Equal(SolutionFixtureZip.TestPluginBytes);
        again.WebResources.Single().Content.Should().Equal(package.WebResources.Single().Content);
    }

    [Fact]
    public void Files_are_named_as_the_export_names_them()
    {
        var (solution, customizations, files) = Fixture();
        var package = CustomizationsReader.Read(solution, customizations, files);

        CustomizationsWriter.Files(package).Keys.Should().BeEquivalentTo(
            "PluginAssemblies/XrmdTestPlugins-6B1C3F4E-1A2B-4C3D-8E9F-0A1B2C3D4E10/XrmdTestPlugins.dll",
            "WebResources/xv_/scripts/project.js6B1C3F4E-1A2B-4C3D-8E9F-0A1B2C3D4E41");
    }

    [Fact]
    public void Zip_has_the_two_documents_the_files_and_the_content_types()
    {
        var zip = SolutionZip.Write(new XDocument(new XElement("ImportExportXml")), new XDocument(new XElement("ImportExportXml")),
            new Dictionary<string, byte[]> { ["PluginAssemblies/A-1/A.dll"] = [1, 2], ["WebResources/xv_/a.js1"] = [3] });

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        archive.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
            "[Content_Types].xml", "solution.xml", "customizations.xml", "PluginAssemblies/A-1/A.dll", "WebResources/xv_/a.js1");
        using var stream = archive.GetEntry("[Content_Types].xml")!.Open();
        var types = XDocument.Load(stream).Root!;
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        types.Elements(ns + "Default").Select(d => (string)d.Attribute("Extension")!).Should().BeEquivalentTo("xml", "dll");
        types.Elements(ns + "Override").Select(o => (string)o.Attribute("PartName")!).Should().Equal("/WebResources/xv_/a.js1");
    }

    [Fact]
    public void Missing_dependencies_are_written_and_read_back()
    {
        var (solution, customizations, files) = Fixture();
        var dependency = new PackageMissingDependency(
            new PackageDependencyNode(9, "xv_shared", "Shared", "Other"),
            new PackageDependencyNode(2, "xv_level", "Level", null, "xv_project", "Project"));
        var package = CustomizationsReader.Read(solution, customizations, files) with { MissingDependencies = [dependency] };

        var written = CustomizationsWriter.Write(package);

        var element = written.Solution.Descendants("MissingDependency").Should().ContainSingle().Subject;
        element.Element("Required")!.Attribute("type")!.Value.Should().Be("9");
        element.Element("Dependent")!.Attribute("parentSchemaName")!.Value.Should().Be("xv_project");
        CustomizationsReader.Read(written.Solution, written.Customizations, CustomizationsWriter.Files(package))
            .MissingDependencies.Should().Equal(dependency);
    }

    [Fact]
    public void Components_without_kept_xml_are_written_with_the_sdk_defaults()
    {
        var (solution, customizations, files) = Fixture();
        var bare = CustomizationsReader.Read(solution, customizations, files) with { Unmodelled = null };

        var written = CustomizationsWriter.Write(bare).Customizations;

        var project = written.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project");
        project.Element("HasRelatedNotes")!.Value.Should().Be("False");
        project.Element("IsQuickCreateEnabled")!.Value.Should().Be("0");
        project.Element("ChangeTrackingEnabled")!.Value.Should().Be("1", "modelled (plan 2.13): written from the metadata, not a default");
        XElement Attribute(string name) => project.Element("attributes")!.Elements("attribute").Single(a => a.Element("Name")!.Value == name);
        Attribute("xv_name").Element("DisplayMask")!.Value.Should().Be("PrimaryName|ValidForAdvancedFind|ValidForForm|ValidForGrid|RequiredForForm");
        Attribute("xv_count").Element("DisplayMask")!.Value.Should().Be("ValidForAdvancedFind|ValidForForm|ValidForGrid");
        Attribute("xv_count").Element("IsDataSourceSecret")!.Value.Should().Be("0");
        Attribute("xv_projectid").Element("DisplayMask")!.Value.Should().Be("ValidForAdvancedFind|RequiredForGrid");
        Attribute("xv_count").Elements().Select(e => e.Name.LocalName).Should().ContainInOrder("RequiredLevel", "DisplayMask", "ImeMode", "ValidForUpdateApi");
        written.Descendants("option").Should().OnlyContain(o => o.Attribute("IsHidden") != null);
        written.Descendants("WebResource").Single().Element("IsHidden")!.Value.Should().Be("0");
        written.Descendants("SdkMessageProcessingStep").Should().OnlyContain(s => s.Element("IsCustomizable")!.Value == "1");
        written.Descendants("EntityRelationship").Should().OnlyContain(r => r.Element("IsValidForAdvancedFind")!.Value == "1");
    }

    [Fact]
    public void Kept_xml_is_written_in_its_original_position_around_what_xrmverse_writes()
    {
        var shape = UnmodelledXml.AttributeShape;
        var file = XElement.Parse("""
            <attribute PhysicalName="x_A" Future="9"><Type>int</Type><Unknown>1</Unknown><Name>x_a</Name><DisplayMask /><Format>none</Format><Later>2</Later></attribute>
            """);
        var kept = UnmodelledXml.Capture(file, shape);
        var written = new XElement("attribute", new XAttribute("PhysicalName", "x_A"), new XElement("Type", "int"), new XElement("Name", "x_a"),
            new XElement("RequiredLevel", "none"), new XElement("Format", "none"));

        var merged = UnmodelledXml.Merge(written, kept, shape);

        merged.Elements().Select(e => e.Name.LocalName).Should().Equal("Type", "Unknown", "Name", "RequiredLevel", "DisplayMask", "Format", "Later");
        merged.Attribute("Future")!.Value.Should().Be("9");
        merged.Descendants().Attributes().Should().NotContain(a => a.Name.NamespaceName == UnmodelledXml.Namespace.NamespaceName);
    }

    [Fact]
    public void Kept_xml_carrying_a_modelled_element_or_attribute_fails_naming_it()
    {
        var shape = UnmodelledXml.AttributeShape;
        var written = new XElement("attribute", new XAttribute("PhysicalName", "x_A"), new XElement("Type", "int"), new XElement("Name", "x_a"));
        var injectedElement = XElement.Parse("""<attribute><Type xmlns:u="urn:xrmverse:unmodelled" u:m="1" /><Name>x_forged</Name></attribute>""");
        var injectedAttribute = XElement.Parse("""<attribute PhysicalName="x_Forged"><Type xmlns:u="urn:xrmverse:unmodelled" u:m="1" /></attribute>""");
        var injectedOption = XElement.Parse("""
            <optionset xmlns:u="urn:xrmverse:unmodelled"><options u:m="1"><option u:m="1" u:key="1" value="9"><labels u:m="1" /></option></options></optionset>
            """);

        FluentActions.Invoking(() => UnmodelledXml.Merge(written, injectedElement, shape, "attribute 'x.x_a'"))
            .Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 500 && e.Message.Contains("attribute 'x.x_a'") && e.Message.Contains("'Name'"));
        FluentActions.Invoking(() => UnmodelledXml.Merge(written, injectedAttribute, shape, "attribute 'x.x_a'"))
            .Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 500 && e.Message.Contains("'PhysicalName'"));
        var set = XElement.Parse("""<optionset Name="s"><options><option value="1"><labels /></option></options></optionset>""");
        FluentActions.Invoking(() => UnmodelledXml.Merge(set, injectedOption, UnmodelledXml.OptionSetShape, "option set 's'"))
            .Should().Throw<XrmdFaultException>().Where(e => e.Message.Contains("option set 's'") && e.Message.Contains("'value'"));
    }

    [Theory]
    [InlineData("Entities")]
    [InlineData("SdkMessageProcessingSteps")]
    [InlineData("AppModules")]
    [InlineData("Languages")]
    public void A_kept_root_node_xrmverse_writes_itself_fails_naming_it(string name)
    {
        var (solution, customizations, files) = Fixture();
        var package = CustomizationsReader.Read(solution, customizations, files);
        var forged = package with { Unsupported = [.. package.Unsupported, new XElement(name, new XElement("forged"))] };

        FluentActions.Invoking(() => CustomizationsWriter.Write(forged))
            .Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 500 && e.Message.Contains($"'{name}'"));
    }

    [Fact]
    public void A_kept_empty_element_stays_empty()
    {
        var shape = UnmodelledXml.OptionSetShape;
        var file = XElement.Parse("""<optionset Name="s"><OptionSetType>picklist</OptionSetType><options><option value="1" /></options></optionset>""");
        var written = XElement.Parse("""<optionset Name="s"><OptionSetType>picklist</OptionSetType><options><option value="1"><labels /></option></options></optionset>""");

        var merged = UnmodelledXml.Merge(written, UnmodelledXml.Capture(file, shape), shape);

        merged.Element("options")!.Element("option")!.Attribute("IsHidden").Should().BeNull("the file's option had none: it is kept empty, not given the defaults");
    }

    [Fact]
    public void Kept_options_are_matched_by_value()
    {
        var shape = UnmodelledXml.OptionSetShape;
        var file = XElement.Parse("""
            <optionset Name="s"><OptionSetType>picklist</OptionSetType><IsCustomizable>0</IsCustomizable><options><option value="2" IsHidden="1"><labels /></option><option value="1" IsHidden="0"><labels /></option></options></optionset>
            """);
        var written = XElement.Parse("""
            <optionset Name="s"><OptionSetType>picklist</OptionSetType><options><option value="1"><labels /></option><option value="2"><labels /></option><option value="3"><labels /></option></options></optionset>
            """);

        var merged = UnmodelledXml.Merge(written, UnmodelledXml.Capture(file, shape), shape);

        merged.Element("IsCustomizable")!.Value.Should().Be("0");
        merged.Element("options")!.Elements("option").Select(o => ((string)o.Attribute("value")!, (string?)o.Attribute("IsHidden")))
            .Should().Equal(("2", "1"), ("1", "0"), ("3", "0"));
    }

    [Fact]
    public void Exporter_writes_stored_flag()
    {
        var (solution, customizations, files) = Fixture();
        var package = CustomizationsReader.Read(solution, customizations, files);
        package.Entities.Single(e => e.LogicalName == "xv_project").AutoCreateAccessTeams = true;
        XElement ProjectInfo(XDocument d) => d.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project");

        var written = CustomizationsWriter.Write(package).Customizations;
        ProjectInfo(written).Element("AutoCreateAccessTeams")!.Value.Should().Be("1");
        written.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Task").Element("AutoCreateAccessTeams")!.Value.Should().Be("0");
        // In the place the file had it.
        ProjectInfo(written).Elements().Select(e => e.Name.LocalName).Should()
            .Equal(ProjectInfo(customizations).Elements().Select(e => e.Name.LocalName));

        // Without kept XML (a table created in xrmverse): in the place of the defaults, with the stored value.
        var bare = CustomizationsWriter.Write(package with { Unmodelled = null }).Customizations;
        var names = ProjectInfo(bare).Elements().Select(e => e.Name.LocalName).ToList();
        ProjectInfo(bare).Element("AutoCreateAccessTeams")!.Value.Should().Be("1");
        names.IndexOf("AutoCreateAccessTeams").Should().Be(names.IndexOf("IsDocumentManagementEnabled") + 1);

        // And it reads back.
        CustomizationsReader.Read(solution, written, files).Entities.Single(e => e.LogicalName == "xv_project").AutoCreateAccessTeams.Should().BeTrue();
    }

    [Fact]
    public void Exporter_writes_ChangeTrackingEnabled_and_the_computed_CanChangeTrackingBeEnabled()
    {
        var (solution, customizations, files) = Fixture();
        var package = CustomizationsReader.Read(solution, customizations, files);
        package.Entities.Single(e => e.LogicalName == "xv_task").ChangeTrackingEnabled = true;
        package.Entities.Single(e => e.LogicalName == "xv_tag").ChangeTrackingEnabled = null;
        XElement Info(XDocument d, string name) => d.Descendants("entity").Single(e => (string?)e.Attribute("Name") == name);

        var written = CustomizationsWriter.Write(package).Customizations;
        Info(written, "xv_Project").Element("ChangeTrackingEnabled")!.Value.Should().Be("1", "the fixture's value round-trips");
        Info(written, "xv_Task").Element("ChangeTrackingEnabled")!.Value.Should().Be("1");
        Info(written, "xv_Tag").Element("ChangeTrackingEnabled")!.Value.Should().Be("0", "a table without the setting has none");
        Info(written, "xv_Project").Element("CanChangeTrackingBeEnabled")!.Value.Should().Be("1");
        // In the place the file had them.
        Info(written, "xv_Project").Elements().Select(e => e.Name.LocalName).Should()
            .Equal(Info(customizations, "xv_Project").Elements().Select(e => e.Name.LocalName));

        // Without kept XML (a table created in xrmverse): in the place of the defaults.
        var bare = CustomizationsWriter.Write(package with { Unmodelled = null }).Customizations;
        var names = Info(bare, "xv_Task").Elements().Select(e => e.Name.LocalName).ToList();
        Info(bare, "xv_Task").Element("ChangeTrackingEnabled")!.Value.Should().Be("1");
        names.IndexOf("ChangeTrackingEnabled").Should().Be(names.IndexOf("EntityHelpUrlEnabled") + 1);
        names.IndexOf("CanChangeTrackingBeEnabled").Should().Be(names.IndexOf("ChangeTrackingEnabled") + 1);

        // The value the exporter computes and puts on the copy is the one written.
        var computed = CustomizationsReader.Read(solution, customizations, files);
        computed.Entities.Single(e => e.LogicalName == "xv_tag").CanChangeTrackingBeEnabled = new BooleanManagedProperty(false);
        Info(CustomizationsWriter.Write(computed).Customizations, "xv_Tag").Element("CanChangeTrackingBeEnabled")!.Value.Should().Be("0");

        // And it reads back.
        var back = CustomizationsReader.Read(solution, written, files);
        back.Entities.Single(e => e.LogicalName == "xv_task").ChangeTrackingEnabled.Should().BeTrue();
        back.Entities.Single(e => e.LogicalName == "xv_project").ChangeTrackingEnabled.Should().BeTrue();
    }

    [Fact]
    public void Exporter_writes_IsDuplicateCheckSupported()
    {
        var (solution, customizations, files) = Fixture();
        var package = CustomizationsReader.Read(solution, customizations, files);
        package.Entities.Single(e => e.LogicalName == "xv_task").IsDuplicateDetectionEnabled = new BooleanManagedProperty(true);
        package.Entities.Single(e => e.LogicalName == "xv_tag").IsDuplicateDetectionEnabled = null;
        XElement Info(XDocument d, string name) => d.Descendants("entity").Single(e => (string?)e.Attribute("Name") == name);

        var written = CustomizationsWriter.Write(package).Customizations;
        Info(written, "xv_Project").Element("IsDuplicateCheckSupported")!.Value.Should().Be("0", "the fixture's value round-trips");
        Info(written, "xv_Task").Element("IsDuplicateCheckSupported")!.Value.Should().Be("1", "modelled (plan 2.15): written from the metadata");
        Info(written, "xv_Tag").Element("IsDuplicateCheckSupported")!.Value.Should().Be("0", "a table without the setting has none");
        // In the place the file had it.
        Info(written, "xv_Project").Elements().Select(e => e.Name.LocalName).Should()
            .Equal(Info(customizations, "xv_Project").Elements().Select(e => e.Name.LocalName));

        // Without kept XML (a table created in xrmverse): in the place of the default, after EntitySetName.
        var bare = CustomizationsWriter.Write(package with { Unmodelled = null }).Customizations;
        var names = Info(bare, "xv_Task").Elements().Select(e => e.Name.LocalName).ToList();
        Info(bare, "xv_Task").Element("IsDuplicateCheckSupported")!.Value.Should().Be("1");
        names.IndexOf("IsDuplicateCheckSupported").Should().Be(names.IndexOf("EntitySetName") + 1);

        // And it reads back.
        CustomizationsReader.Read(solution, written, files).Entities.Single(e => e.LogicalName == "xv_task")
            .IsDuplicateDetectionEnabled!.Value.Should().BeTrue();
    }

    [Fact]
    public void Normalizer_compares_display_masks_and_entity_settings()
    {
        XDocument Doc(string mask, string tracking) => XDocument.Parse(
            $"""<ImportExportXml><Entities><Entity><EntityInfo><entity Name="a"><attributes><attribute><DisplayMask>{mask}</DisplayMask></attribute></attributes><ChangeTrackingEnabled>{tracking}</ChangeTrackingEnabled></entity></EntityInfo></Entity></Entities></ImportExportXml>""");

        XmlNormalizer.Normalize(Doc("ValidForGrid", "0")).Should().NotBe(XmlNormalizer.Normalize(Doc("ValidForForm", "0")));
        XmlNormalizer.Normalize(Doc("ValidForGrid", "0")).Should().NotBe(XmlNormalizer.Normalize(Doc("ValidForGrid", "1")));
        XmlNormalizer.Normalize(XDocument.Parse("<a><attributes><attribute><IsAuditEnabled>1</IsAuditEnabled></attribute></attributes></a>"))
            .Should().NotBe(XmlNormalizer.Normalize(XDocument.Parse("<a><attributes><attribute><IsAuditEnabled>0</IsAuditEnabled></attribute></attributes></a>")));
        XmlNormalizer.Normalize(XDocument.Parse("<a><options><option value=\"1\" IsHidden=\"1\" /></options></a>"))
            .Should().NotBe(XmlNormalizer.Normalize(XDocument.Parse("<a><options><option value=\"1\" IsHidden=\"0\" /></options></a>")));
        XmlNormalizer.Normalize(XDocument.Parse("<a><attributes><attribute><IntroducedVersion>1.0</IntroducedVersion></attribute></attributes></a>"))
            .Should().Be(XmlNormalizer.Normalize(XDocument.Parse("<a><attributes><attribute><IntroducedVersion>2.0</IntroducedVersion></attribute></attributes></a>")),
                "IntroducedVersion is the build that wrote the file");
    }

    [Fact]
    public void Normalizer_ignores_formatting_attribute_order_and_empty_labels_only()
    {
        var a = XDocument.Parse("""<ImportExportXml version="1"><x b="2" a="1"> t </x><Descriptions><Description description="" languagecode="1033" /></Descriptions></ImportExportXml>""");
        var b = XDocument.Parse("""<ImportExportXml version="2"><x a="1" b="2">t</x><Descriptions /></ImportExportXml>""");
        var c = XDocument.Parse("""<ImportExportXml><x a="1" b="3">t</x><Descriptions /></ImportExportXml>""");

        XmlNormalizer.Normalize(a).Should().Be(XmlNormalizer.Normalize(b));
        XmlNormalizer.Normalize(a).Should().NotBe(XmlNormalizer.Normalize(c));
    }
}
