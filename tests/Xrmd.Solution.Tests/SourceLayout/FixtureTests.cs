using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;

namespace Xrmd.Solution.Tests.SourceLayout;

public class FixtureTests
{
    public static TheoryData<string, char> Copies => new()
    {
        { "OpenProject365", 'a' }, { "OpenProject365", 'b' }, { "XrmSharedTest", 'a' }, { "XrmSharedTest", 'b' }, { "xrmCalcTest", 'a' }, { "xrmCalcTest", 'b' },
    };

    private static (string Name, string Version) Manifest(XDocument doc)
    {
        var m = doc.Root!.Element("SolutionManifest")!;
        return (m.Element("UniqueName")!.Value, m.Element("Version")!.Value);
    }

    [SourceLayoutTheory]
    [MemberData(nameof(Copies))]
    public void Every_fixture_has_a_solution_manifest_matching_its_zip(string solution, char copy)
    {
        var folderManifest = Manifest(XDocument.Load(Path.Combine(SourceLayoutFixtures.Folder(solution, copy), "Other", "Solution.xml")));

        using var zip = new ZipArchive(new MemoryStream(SourceLayoutFixtures.Zip(solution)));
        using var entry = zip.GetEntry("solution.xml")!.Open();
        var zipManifest = Manifest(XDocument.Load(entry));

        folderManifest.Name.Should().Be(solution);
        folderManifest.Should().Be(zipManifest);
    }

    [SourceLayoutFact]
    public void The_cdsproj_sample_has_its_project_file_and_sources()
    {
        File.Exists(Path.Combine(SourceLayoutFixtures.CdsprojSample, "XrmSharedTest.cdsproj")).Should().BeTrue();
        File.Exists(Path.Combine(SourceLayoutFixtures.CdsprojSample, "src", "Other", "Solution.xml")).Should().BeTrue();
    }
}
