using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

/// <summary>
/// The <c>environmentvariabledefinitions/</c> folder of a solution file (#167, task 2): one
/// <c>environmentvariabledefinition.xml</c> per definition and an optional <c>environmentvariablevalues.json</c> beside it, read
/// into <see cref="PackageEnvironmentVariable"/> and written back with what xrmverse does not model (the label sets,
/// <c>iscustomizable</c>, any unknown child) in place.
/// </summary>
public class EnvironmentVariableReaderTests
{
    private const string Folder = "environmentvariabledefinitions/";

    private static string DefinitionPath(string schema) => $"{Folder}{schema}/environmentvariabledefinition.xml";

    private static string ValuePath(string schema) => $"{Folder}{schema}/environmentvariablevalues.json";

    /// <summary>A Boolean definition in the shape of the OpenProject365 export: two languages, no default, no value file.</summary>
    private const string BooleanDefinition = """
        <environmentvariabledefinition schemaname="xv_FeatureEnabled">
          <description default="Turns the feature on.">
            <label description="Schaltet die Funktion ein." languagecode="1031" />
            <label description="Turns the feature on." languagecode="1033" />
          </description>
          <displayname default="Feature Enabled">
            <label description="Funktion aktiviert" languagecode="1031" />
            <label description="Feature Enabled" languagecode="1033" />
          </displayname>
          <introducedversion>1.0.1.0</introducedversion>
          <iscustomizable>1</iscustomizable>
          <isrequired>0</isrequired>
          <secretstore>0</secretstore>
          <type>100000002</type>
        </environmentvariabledefinition>
        """;

    private static Dictionary<string, byte[]> Files(params (string Path, string Content)[] entries) =>
        entries.ToDictionary(e => e.Path, e => Encoding.UTF8.GetBytes(e.Content), StringComparer.Ordinal);

    /// <summary>The fixture solution with <paramref name="extra"/> archive entries added (text, UTF-8).</summary>
    private static SolutionPackage ReadWith(params (string Path, string Content)[] extra)
    {
        var (solution, customizations, files) = SolutionZip.Read(SolutionFixtureZip.Build());
        var all = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        foreach (var (path, content) in extra) all[path] = Encoding.UTF8.GetBytes(content);
        return CustomizationsReader.Read(solution, customizations, all);
    }

    private static SolutionPackage ReadRealShapes()
    {
        var (solution, customizations, files) = SolutionZip.Read(SolutionFixtureZip.BuildWithRealShapes());
        return CustomizationsReader.Read(solution, customizations, files);
    }

    private static SolutionPackage ReadRealExport()
    {
        var zip = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "OpenProject365_1_0_1_1.zip"));
        var (solution, customizations, files) = SolutionZip.Read(zip);
        // Autonumber columns are refused (issue #38) before anything else is read: drop their formats.
        foreach (var format in customizations.Descendants("AutoNumberFormat").ToList()) format.Remove();
        return CustomizationsReader.Read(solution, customizations, files);
    }

    [Fact]
    public void Reads_definition_and_value()
    {
        var variable = ReadRealShapes().EnvironmentVariables.Should().ContainSingle().Subject;

        variable.SchemaName.Should().Be("xv_ServiceUrl");
        variable.Type.Should().Be((int)VariableType.String);
        variable.DefaultValue.Should().Be("https://service.example.com");
        variable.Value.Should().Be("https://service.example.org");
        variable.HasValueFile.Should().BeTrue();
        variable.IsRequired.Should().BeFalse();
        variable.SecretStore.Should().Be(0);
        variable.ValueSchema.Should().BeNull();
        variable.IntroducedVersion.Should().BeNull();
        variable.Hint.Should().BeNull();
        UnmodelledXml.IsMarker(variable.Source.Element("type")!).Should().BeTrue();
    }

    [Fact]
    public void Reads_definition_without_value_file()
    {
        var (variables, read) = EnvironmentVariableReader.Read(Files((DefinitionPath("xv_FeatureEnabled"), BooleanDefinition)), 1033);

        read.Should().Equal(DefinitionPath("xv_FeatureEnabled"));
        var variable = variables.Should().ContainSingle().Subject;
        variable.SchemaName.Should().Be("xv_FeatureEnabled");
        variable.Type.Should().Be((int)VariableType.Boolean);
        variable.DefaultValue.Should().BeNull();
        variable.IsRequired.Should().BeFalse();
        variable.SecretStore.Should().Be(0);
        variable.IntroducedVersion.Should().Be("1.0.1.0");
        variable.Value.Should().BeNull();
        variable.HasValueFile.Should().BeFalse();

        // The label sets and iscustomizable travel in Source; the base language's labels give the display name and description.
        variable.Source.Element("displayname")!.Elements("label").Should().HaveCount(2);
        variable.Source.Element("iscustomizable")!.Value.Should().Be("1");
        UnmodelledXml.IsMarker(variable.Source.Element("isrequired")!).Should().BeTrue();
        variable.Source.Attribute("schemaname").Should().BeNull("the schema name is modelled");
        EnvironmentVariableReader.BaseLabel(variable, EnvironmentVariableSchema.DisplayName, 1033).Should().Be("Feature Enabled");
        EnvironmentVariableReader.BaseLabel(variable, EnvironmentVariableSchema.Description, 1031).Should().Be("Schaltet die Funktion ein.");
        EnvironmentVariableReader.BaseLabel(variable, EnvironmentVariableSchema.Hint, 1033).Should().BeNull();
    }

    [OpenProject365Fact]
    public void Real_export_definitions_are_read()
    {
        var package = ReadRealExport();

        package.EnvironmentVariables.Should().HaveCount(19).And.OnlyContain(v => v.SchemaName.StartsWith("op365_") && !v.HasValueFile);
        package.KeptFiles!.Keys.Should().NotContain(k => k.StartsWith(Folder, StringComparison.OrdinalIgnoreCase));
        var extraction = package.EnvironmentVariables.Single(v => v.SchemaName == "op365_InvoiceAiExtraction");
        extraction.Type.Should().Be((int)VariableType.Boolean);
        extraction.DefaultValue.Should().Be("yes");
        package.EnvironmentVariables.Single(v => v.SchemaName == "op365_PurchaseOrderLibraryId").Source.Element("description")!
            .Elements("label").Should().HaveCount(4);
    }

    [Fact]
    public void Folder_name_must_match_schemaname()
    {
        var act = () => EnvironmentVariableReader.Read(Files((DefinitionPath("xv_Other"), BooleanDefinition)), 1033);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message.Contains(DefinitionPath("xv_Other")) && e.Message.Contains("xv_FeatureEnabled"));
    }

    [Fact]
    public void Folder_name_matches_ignoring_case()
    {
        var (variables, _) = EnvironmentVariableReader.Read(Files((DefinitionPath("XV_FEATUREENABLED"), BooleanDefinition)), 1033);

        variables.Single().SchemaName.Should().Be("xv_FeatureEnabled");
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"environmentvariablevalues":{}}""")]
    [InlineData("""{"environmentvariablevalues":{"environmentvariablevalue":{"value":12}}}""")]
    [InlineData("""{"environmentvariablevalues":{"environmentvariablevalue":{"value":"a","origin":"x"}}}""")]
    [InlineData("""{"environmentvariablevalues":{"environmentvariablevalue":[{"value":"a"},{"value":"b"}]}}""")]
    [InlineData("""{"environmentvariablevalues":{"environmentvariablevalue":{"iscustomizable":"2","value":"a"}}}""")]
    [InlineData("""{"environmentvariablevalues":{"environmentvariablevalue":{"iscustomizable":1,"value":"a"}}}""")]
    public void Invalid_json_value_file_is_refused(string json)
    {
        var act = () => EnvironmentVariableReader.Read(Files(
            (DefinitionPath("xv_FeatureEnabled"), BooleanDefinition), (ValuePath("xv_FeatureEnabled"), json)), 1033);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message.Contains(ValuePath("xv_FeatureEnabled")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData(null)]
    public void Value_iscustomizable_is_read_and_written_back(string? flag)
    {
        var json = flag is null
            ? """{"environmentvariablevalues":{"environmentvariablevalue":{"value":"a"}}}"""
            : """{"environmentvariablevalues":{"environmentvariablevalue":{"iscustomizable":""" + $"\"{flag}\"" + ""","value":"a"}}}""";
        var package = ReadWith((DefinitionPath("xv_FeatureEnabled"), BooleanDefinition), (ValuePath("xv_FeatureEnabled"), json));

        var variable = package.EnvironmentVariables.Single();
        variable.ValueIsCustomizable.Should().Be(flag, "a managed export writes 0, an unmanaged one 1");
        var written = CustomizationsWriter.EnvironmentVariableFiles(package);
        Encoding.UTF8.GetString(written[ValuePath("xv_FeatureEnabled")]).Should().Be(json);
    }

    [Fact]
    public void Value_file_without_definition_is_refused()
    {
        var act = () => EnvironmentVariableReader.Read(Files((ValuePath("xv_Lost"),
            """{"environmentvariablevalues":{"environmentvariablevalue":{"iscustomizable":"1","value":"a"}}}""")), 1033);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message.Contains(ValuePath("xv_Lost")));
    }

    [Theory]
    [InlineData("<type>7</type>", "type")]
    [InlineData("<secretstore>2</secretstore>", "secretstore")]
    [InlineData("<isrequired>yes</isrequired>", "isrequired")]
    public void Unknown_choice_or_flag_is_refused(string replacement, string element)
    {
        var xml = BooleanDefinition.Replace($"<{element}>", "<!--").Replace($"</{element}>", "-->")
            .Replace("</environmentvariabledefinition>", replacement + "</environmentvariabledefinition>");
        var act = () => EnvironmentVariableReader.Read(Files((DefinitionPath("xv_FeatureEnabled"), xml)), 1033);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message.Contains(DefinitionPath("xv_FeatureEnabled")) && e.Message.Contains(element));
    }

    [Fact]
    public void Writer_round_trips_definition_and_value()
    {
        var withValue = BooleanDefinition.Replace("<introducedversion>",
            "<defaultvalue>no</defaultvalue><hint>Yes or no</hint><xv_future>kept</xv_future><introducedversion>");
        var package = ReadWith(
            (DefinitionPath("xv_FeatureEnabled"), withValue),
            (ValuePath("xv_FeatureEnabled"), """{"environmentvariablevalues":{"environmentvariablevalue":{"iscustomizable":"1","value":"yes"}}}"""),
            (DefinitionPath("xv_ServiceUrl"), """<environmentvariabledefinition schemaname="xv_ServiceUrl"><type>100000000</type><defaultvalue>https://a.example</defaultvalue><valueschema>{"type":"string"}</valueschema></environmentvariabledefinition>"""));

        var written = CustomizationsWriter.EnvironmentVariableFiles(package);
        written.Keys.Should().BeEquivalentTo(DefinitionPath("xv_FeatureEnabled"), ValuePath("xv_FeatureEnabled"), DefinitionPath("xv_ServiceUrl"));
        var (again, _) = EnvironmentVariableReader.Read(written, 1033);

        again.Should().BeEquivalentTo(package.EnvironmentVariables, o => o.Excluding(v => v.Source));
        foreach (var (before, after) in package.EnvironmentVariables.Zip(again))
            Unmodelled(after.Source).Should().Equal(Unmodelled(before.Source), "what xrmverse does not model is written back as it was");
        var feature = again.Single(v => v.SchemaName == "xv_FeatureEnabled");
        feature.Value.Should().Be("yes");
        feature.Hint.Should().Be("Yes or no");
        feature.Source.Element("xv_future")!.Value.Should().Be("kept");
        again.Single(v => v.SchemaName == "xv_ServiceUrl").ValueSchema.Should().Be("""{"type":"string"}""");

        static List<string> Unmodelled(XElement source) =>
            source.Elements().Where(e => !UnmodelledXml.IsMarker(e)).Select(e => e.ToString(SaveOptions.DisableFormatting)).ToList();
    }

    [Fact]
    public void Writer_writes_the_value_file_in_the_export_shape()
    {
        var package = ReadRealShapes();

        var written = CustomizationsWriter.EnvironmentVariableFiles(package);

        // The fixture file ends with a line feed (an editor's); the export writes the JSON alone.
        Encoding.UTF8.GetString(written[ValuePath("xv_ServiceUrl")]).Should()
            .Be(Encoding.UTF8.GetString(SolutionFixtureZip.RealShapesEntries()[ValuePath("xv_ServiceUrl")]).TrimEnd('\n'));
        var definition = XElement.Parse(Encoding.UTF8.GetString(written[DefinitionPath("xv_ServiceUrl")]));
        ((string?)definition.Attribute("schemaname")).Should().Be("xv_ServiceUrl");
        definition.Elements().Select(e => e.Name.LocalName).Should().Equal("type", "defaultvalue", "isrequired", "secretstore");
    }

    [Fact]
    public void Files_are_consumed_not_kept()
    {
        var package = ReadWith(
            (DefinitionPath("xv_FeatureEnabled"), BooleanDefinition),
            (ValuePath("xv_FeatureEnabled"), """{"environmentvariablevalues":{"environmentvariablevalue":{"iscustomizable":"1","value":"yes"}}}"""),
            ($"{Folder}xv_FeatureEnabled/notes.txt", "not read"));

        package.EnvironmentVariables.Should().ContainSingle().Which.Value.Should().Be("yes");
        package.KeptFiles!.Keys.Where(k => k.StartsWith(Folder, StringComparison.OrdinalIgnoreCase))
            .Should().Equal([$"{Folder}xv_FeatureEnabled/notes.txt"], "a file on another path stays a kept file");
    }

    [Fact]
    public void Two_definitions_differing_only_by_case_are_refused()
    {
        var act = () => EnvironmentVariableReader.Read(Files(
            (DefinitionPath("xv_FeatureEnabled"), BooleanDefinition),
            (DefinitionPath("XV_featureenabled"), BooleanDefinition.Replace("xv_FeatureEnabled", "XV_featureenabled"))), 1033);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.Message.Contains("XV_featureenabled"));
    }

    [Fact]
    public void A_package_without_the_folder_has_no_environment_variables()
    {
        ReadWith().EnvironmentVariables.Should().BeEmpty();
    }
}
