using System.IO.Compression;
using System.Text;

namespace Xrmd.Migration.Tests.Source;

/// <summary>Builds small solution zips (solution.xml and customizations.xml) for tests.</summary>
internal static class SolutionZip
{
    public static byte[] Build(string uniqueName, string version, bool managed, string customizations = "<ImportExportXml/>")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "solution.xml", $"""
                <ImportExportXml version="9.2" SolutionPackageVersion="9.2" languagecode="1033">
                  <SolutionManifest><UniqueName>{uniqueName}</UniqueName><Version>{version}</Version><Managed>{(managed ? 1 : 0)}</Managed></SolutionManifest>
                </ImportExportXml>
                """);
            Add(zip, "customizations.xml", customizations);
        }
        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(Encoding.UTF8.GetBytes(content));
    }
}
