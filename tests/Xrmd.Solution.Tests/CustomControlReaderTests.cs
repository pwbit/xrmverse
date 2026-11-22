using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

/// <summary>
/// The <c>CustomControls</c> node of <c>customizations.xml</c> and the <c>Controls/</c> folder of a solution file (plan 3.5 UI-1d,
/// rulings R3 and R4): each <c>CustomControl</c> is read with its manifest and the files of the manifest's folder, and written back
/// byte for byte. The manifests and bundles are written here by hand.
/// </summary>
public class CustomControlReaderTests
{
    private const string Name = "xv_Xv.Gauge";
    private const string Folder = "Controls/" + Name + "/";

    /// <summary>A field control bound to a Decimal column, with code, a style sheet and resource strings.</summary>
    private const string GaugeManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <manifest>
          <control namespace="Xv" constructor="Gauge" version="1.2.3" display-name-key="Gauge_Name" control-type="standard">
            <property name="value" display-name-key="Value" of-type="Decimal" usage="bound" required="true" />
            <property name="maximum" display-name-key="Maximum" of-type="Whole.None" usage="input" />
            <resources>
              <code path="bundle.js" order="1" />
              <css path="css/gauge.css" order="1" />
              <resx path="strings/Gauge.1033.resx" version="1.0.0" />
            </resources>
          </control>
        </manifest>
        """;

    /// <summary>A data-set control with code only.</summary>
    private const string BoardManifest = """
        <manifest>
          <control namespace="Xv" constructor="Board" version="2.0.0" display-name-key="Board_Name" control-type="virtual">
            <data-set name="items" display-name-key="Items" />
            <resources>
              <code path="bundle.js" order="1" />
              <platform-library name="React" version="16.14.0" />
            </resources>
          </control>
        </manifest>
        """;

    private static Dictionary<string, byte[]> GaugeFiles() => new(StringComparer.Ordinal)
    {
        [Folder + "ControlManifest.xml"] = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(GaugeManifest)).ToArray(),
        [Folder + "bundle.js"] = Encoding.UTF8.GetBytes("var gauge = 1; // written for the test\n"),
        [Folder + "css/gauge.css"] = Encoding.UTF8.GetBytes(".gauge { color: red; }"),
        [Folder + "strings/Gauge.1033.resx"] = Encoding.UTF8.GetBytes("<root><data name=\"Gauge_Name\"><value>Gauge</value></data></root>"),
    };

    private static XElement Entry(string name, string? fileName = null) => new("CustomControl",
        new XElement("Name", name), new XElement("FileName", fileName ?? $"/Controls/{name}/ControlManifest.xml"));

    /// <summary>The fixture solution with <paramref name="controls"/> under <c>CustomControls</c> and <paramref name="extra"/> entries.</summary>
    private static SolutionPackage ReadWith(IEnumerable<XElement> controls, IReadOnlyDictionary<string, byte[]> extra)
    {
        var (solution, customizations, files) = SolutionZip.Read(SolutionFixtureZip.Build());
        var node = customizations.Root!.Element("CustomControls")!;
        node.Add(controls);
        var all = new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in extra) all[path] = content;
        return CustomizationsReader.Read(solution, customizations, all);
    }

    private static SolutionPackage ReadRealExport()
    {
        var zip = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "OpenProject365_1_0_1_1.zip"));
        var (solution, customizations, files) = SolutionZip.Read(zip);
        foreach (var format in customizations.Descendants("AutoNumberFormat").ToList()) format.Remove();
        return CustomizationsReader.Read(solution, customizations, files);
    }

    [Fact]
    public void Reads_control_with_manifest_and_files()
    {
        var files = GaugeFiles();
        var package = ReadWith([Entry(Name)], files);

        var control = package.CustomControls.Should().ContainSingle().Subject;
        control.Name.Should().Be(Name);
        control.ManifestPath.Should().Be(Folder + "ControlManifest.xml");
        control.Version.Should().Be("1.2.3");
        control.CompatibleDataTypes.Should().Be("Decimal", "only bound properties count");
        control.Manifest.Should().StartWith("<?xml").And.Contain("constructor=\"Gauge\"");
        control.Files.Keys.Should().BeEquivalentTo("ControlManifest.xml", "bundle.js", "css/gauge.css", "strings/Gauge.1033.resx");
        foreach (var (path, content) in files) control.Files[path[Folder.Length..]].Should().Equal(content);
        ((string?)control.Source.Element("Name")).Should().Be(Name);
        package.KeptFiles!.Keys.Should().NotContain(k => k.StartsWith("Controls/", StringComparison.OrdinalIgnoreCase));
        package.Unsupported.Should().NotContain(u => u.Name.LocalName == "CustomControls");
        package.Sources!.Should().ContainKey((ComponentType.CustomControl, Name.ToLowerInvariant()));
    }

    [Fact]
    public void Data_set_control_is_compatible_with_dataset()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["Controls/xv_Xv.Board/ControlManifest.xml"] = Encoding.UTF8.GetBytes(BoardManifest),
            ["Controls/xv_Xv.Board/bundle.js"] = Encoding.UTF8.GetBytes("var board;"),
        };

        var control = ReadWith([Entry("xv_Xv.Board")], files).CustomControls.Should().ContainSingle().Subject;

        control.CompatibleDataTypes.Should().Be("dataset");
        control.Version.Should().Be("2.0.0");
    }

    [Fact]
    public void Missing_resource_file_is_400_naming_control_and_file()
    {
        var files = GaugeFiles();
        files.Remove(Folder + "css/gauge.css");

        var act = () => ReadWith([Entry(Name)], files);

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400
            && e.Message.Contains(Name, StringComparison.Ordinal) && e.Message.Contains("css/gauge.css", StringComparison.Ordinal));
    }

    [Fact]
    public void File_path_longer_than_the_column_is_400_naming_control_and_file()
    {
        var files = GaugeFiles();
        var longName = "img/" + new string('a', 397) + ".png";
        files[Folder + longName] = [1, 2, 3];

        var act = () => ReadWith([Entry(Name)], files);

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400
            && e.Message.Contains(Name, StringComparison.Ordinal) && e.Message.Contains(longName, StringComparison.Ordinal));
    }

    [Fact]
    public void File_path_of_exactly_400_characters_is_read()
    {
        var files = GaugeFiles();
        var longest = "img/" + new string('a', 392) + ".png";
        files[Folder + longest] = [1, 2, 3];

        var control = ReadWith([Entry(Name)], files).CustomControls.Should().ContainSingle().Subject;

        longest.Length.Should().Be(400);
        control.Files.Should().ContainKey(longest);
    }

    [Fact]
    public void Control_nested_in_another_controls_folder_keeps_its_own_files()
    {
        const string outer = "Controls/xv_Xv.Board/";
        const string inner = outer + "gauge/";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [outer + "ControlManifest.xml"] = Encoding.UTF8.GetBytes(BoardManifest),
            [outer + "bundle.js"] = Encoding.UTF8.GetBytes("var board;"),
        };
        foreach (var (path, content) in GaugeFiles()) files[inner + path[Folder.Length..]] = content;

        var controls = ReadWith([Entry("xv_Xv.Board"), Entry(Name, "/" + inner + "ControlManifest.xml")], files).CustomControls;

        controls.Single(c => c.Name == "xv_Xv.Board").Files.Keys.Should().BeEquivalentTo("ControlManifest.xml", "bundle.js");
        controls.Single(c => c.Name == Name).Files.Keys.Should()
            .BeEquivalentTo("ControlManifest.xml", "bundle.js", "css/gauge.css", "strings/Gauge.1033.resx");
    }

    [Fact]
    public void Missing_manifest_is_400()
    {
        var act = () => ReadWith([Entry(Name)], new Dictionary<string, byte[]>());

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400
            && e.Message.Contains(Name, StringComparison.Ordinal) && e.Message.Contains(Folder + "ControlManifest.xml", StringComparison.Ordinal));
    }

    [Fact]
    public void Writer_writes_node_and_files_back_byte_for_byte()
    {
        var files = GaugeFiles();
        var package = ReadWith([Entry(Name)], files);

        var (_, customizations) = CustomizationsWriter.Write(package);
        var written = CustomizationsWriter.Files(package);

        var entry = customizations.Root!.Element("CustomControls")!.Elements("CustomControl").Should().ContainSingle().Subject;
        ((string?)entry.Element("Name")).Should().Be(Name);
        ((string?)entry.Element("FileName")).Should().Be("/" + Folder + "ControlManifest.xml");
        foreach (var (path, content) in files) written.Should().ContainKey(path).WhoseValue.Should().Equal(content);
    }

    [OpenProject365Fact]
    public void Real_export_reads_its_eight_controls()
    {
        var package = ReadRealExport();

        package.CustomControls.Should().HaveCount(8);
        package.CustomControls.Single(c => c.Name == "op365_OP365.ProgressField").CompatibleDataTypes.Should().Be("Decimal");
        package.CustomControls.Single(c => c.Name == "op365_OP365.CostSummary").CompatibleDataTypes.Should().Be("Currency");
        package.CustomControls.Single(c => c.Name == "op365_OP365.QuoteComparison").CompatibleDataTypes.Should().Be("Whole.None");
        package.CustomControls.Single(c => c.Name == "op365_OP365.GanttChart").CompatibleDataTypes.Should().Be("dataset");
        package.CustomControls.Single(c => c.Name == "op365_OP365.WbsTreeGrid").Version.Should().Be("1.0.4");
        package.CustomControls.Should().OnlyContain(c => c.Files.ContainsKey("bundle.js") && c.Files.ContainsKey("ControlManifest.xml"));
        package.KeptFiles!.Keys.Should().NotContain(k => k.StartsWith("Controls/", StringComparison.Ordinal));
    }
}
