using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

public class SolutionZipTests
{
    private const string Manifest = "<ImportExportXml><SolutionManifest><UniqueName>S</UniqueName></SolutionManifest></ImportExportXml>";
    private const string Customizations = "<ImportExportXml><Entities /></ImportExportXml>";

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static (string, byte[]) Text(string name, string content) => (name, Encoding.UTF8.GetBytes(content));

    private static void ShouldBeInvalidArgument(Action act, string message) =>
        act.Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400)
            .WithMessage(message);

    [Fact]
    public void Reads_the_two_documents_and_the_other_files()
    {
        var (solution, customizations, files) = SolutionZip.Read(SolutionFixtureZip.Build());

        solution.Root!.Element("SolutionManifest")!.Element("UniqueName")!.Value.Should().Be("XrmverseFixture");
        customizations.Root!.Element("Entities").Should().NotBeNull();
        files.Keys.Should().BeEquivalentTo([
            "[Content_Types].xml",
            "PluginAssemblies/XrmdTestPlugins-6B1C3F4E-1A2B-4C3D-8E9F-0A1B2C3D4E10/XrmdTestPlugins.dll",
            "WebResources/xv_/scripts/project.js6B1C3F4E-1A2B-4C3D-8E9F-0A1B2C3D4E41",
        ]);
        files["pluginassemblies/xrmdtestplugins-6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e10/xrmdtestplugins.dll"]
            .Should().Equal(SolutionFixtureZip.TestPluginBytes, "zips written on Windows do not keep the case of the paths they name");
    }

    [Fact]
    public void Missing_customizations_xml_is_a_clear_error() =>
        ShouldBeInvalidArgument(() => SolutionZip.Read(SolutionFixtureZip.Build(omit: "customizations.xml")),
            "The solution file is missing customizations.xml.");

    [Fact]
    public void Missing_solution_xml_is_a_clear_error() =>
        ShouldBeInvalidArgument(() => SolutionZip.Read(SolutionFixtureZip.Build(omit: "solution.xml")),
            "The solution file is missing solution.xml.");

    [Fact]
    public void Bytes_that_are_not_a_zip_are_refused() =>
        ShouldBeInvalidArgument(() => SolutionZip.Read(Encoding.UTF8.GetBytes("not a zip")), "The solution file is not a valid zip archive.");

    [Theory]
    [InlineData("../x")]
    [InlineData("WebResources/../../x")]
    [InlineData("..\\x")]
    [InlineData("/etc/x")]
    [InlineData("\\x")]
    [InlineData("C:/x")]
    public void Zip_with_path_traversal_entry_is_refused(string name) =>
        ShouldBeInvalidArgument(
            () => SolutionZip.Read(Zip(Text("solution.xml", Manifest), Text("customizations.xml", Customizations), Text(name, "x"))),
            $"The solution file contains an entry with an invalid path: '{name}'.");

    [Fact]
    public void Duplicate_entries_are_refused() =>
        ShouldBeInvalidArgument(
            () => SolutionZip.Read(Zip(Text("solution.xml", Manifest), Text("customizations.xml", Customizations), Text("a/x.js", "1"),
                Text("A/X.js", "2"))),
            "The solution file contains the entry 'A/X.js' more than once.");

    [Fact]
    public void Content_larger_than_the_cap_is_refused()
    {
        var big = new byte[SolutionZip.MaxUncompressedBytes - 10];
        var zip = Zip(Text("solution.xml", Manifest), Text("customizations.xml", Customizations), ("big.bin", big));

        ShouldBeInvalidArgument(() => SolutionZip.Read(zip),
            $"The solution file is larger than {SolutionZip.MaxUncompressedBytes / (1024 * 1024)} MB uncompressed.");
    }

    [Fact]
    public void Xml_with_a_dtd_is_refused() =>
        ShouldBeInvalidArgument(
            () => SolutionZip.Read(Zip(Text("solution.xml", "<!DOCTYPE x [<!ENTITY a \"b\">]><ImportExportXml>&a;</ImportExportXml>"),
                Text("customizations.xml", Customizations))),
            "The solution file contains a solution.xml that is not valid XML*");
}
