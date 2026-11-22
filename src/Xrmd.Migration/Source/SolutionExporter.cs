using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Source;

/// <summary>The export cannot go on; the message says what the user has to change.</summary>
public sealed class MigrationExportException(string message) : Exception(message);

public sealed record SourceSolution(Guid SolutionId, string UniqueName, string Version, bool Managed, string PublisherPrefix, string PublisherUniqueName);

/// <summary>
/// Lists the solutions of the source and puts the chosen ones in the package: unmanaged ones through <c>ExportSolution</c>,
/// managed ones from the files the user gives (Dataverse does not export a managed solution it did not build).
/// </summary>
public static class SolutionExporter
{
    private static readonly HashSet<string> SystemSolutions = new(StringComparer.OrdinalIgnoreCase) { "Default", "Active", "Basic" };
    private static readonly string[] MicrosoftPrefixes = ["msdyn", "msdynce", "msdynmkt", "mspp", "adx"];

    /// <summary>Visible solutions in creation order (a solution comes after the ones it can depend on), without the system ones.</summary>
    public static async Task<IReadOnlyList<SourceSolution>> ListAsync(DataverseClient client, CancellationToken ct)
    {
        var rows = await client.GetAllAsync(
            "solutions?$select=solutionid,uniquename,version,ismanaged&$expand=publisherid($select=uniquename,customizationprefix)" +
            "&$filter=isvisible eq true&$orderby=createdon asc", ct);
        return [.. rows
            .Select(r => new SourceSolution(r.GetProperty("solutionid").GetGuid(), r.GetProperty("uniquename").GetString()!,
                r.GetProperty("version").GetString()!, r.GetProperty("ismanaged").GetBoolean(),
                r.GetProperty("publisherid").GetProperty("customizationprefix").GetString() ?? "",
                r.GetProperty("publisherid").GetProperty("uniquename").GetString() ?? ""))
            .Where(s => !SystemSolutions.Contains(s.UniqueName))];
    }

    /// <summary>Same rule as the solution import of xrmverse: Microsoft's own first-party solutions are not carried.</summary>
    public static bool IsMicrosoft(SourceSolution s) =>
        MicrosoftPrefixes.Contains(s.PublisherPrefix, StringComparer.OrdinalIgnoreCase)
        || s.UniqueName.StartsWith("microsoft", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s.UniqueName, "dynamics365", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes <c>solutions/&lt;n&gt;-&lt;name&gt;.zip</c> for each chosen solution, in the given order. Missing or mismatched
    /// managed solution files are reported, all at once, before anything is downloaded. <paramref name="coverage"/>, when given,
    /// collects what the solutions carry for the schema delta.
    /// </summary>
    public static async Task<IReadOnlyList<PackageSolution>> ExportAsync(DataverseClient client, PackageWriter writer, IReadOnlyList<SourceSolution> chosen,
        IReadOnlyDictionary<string, string> managedFiles, Action<PackageWarning> warn, CancellationToken ct, SolutionCoverage? coverage = null)
    {
        var carried = new List<SourceSolution>();
        foreach (var s in chosen)
        {
            if (IsMicrosoft(s))
                warn(new PackageWarning("microsoft-solution", $"Solution {s.UniqueName} is a Microsoft first-party solution and is not exported."));
            else
                carried.Add(s);
        }

        var missing = carried.Where(s => s.Managed && !managedFiles.ContainsKey(s.UniqueName)).Select(s => s.UniqueName).ToList();
        if (missing.Count > 0)
            throw new MigrationExportException(
                $"Dataverse cannot export a managed solution: give the original managed solution file with --solution-file <path> for {string.Join(", ", missing)}.");
        foreach (var s in carried.Where(s => s.Managed))
        {
            var fileVersion = ManifestVersion(managedFiles[s.UniqueName]);
            if (fileVersion != s.Version)
                throw new MigrationExportException(
                    $"The file for {s.UniqueName} holds version {fileVersion}, but the source environment has version {s.Version}: give the file of the installed version.");
        }

        var result = new List<PackageSolution>();
        foreach (var s in carried)
        {
            var order = result.Count + 1;
            var path = $"{SolutionsPrefix}{order}-{s.UniqueName}.zip";
            using (var entry = writer.CreateEntry(path))
            {
                if (s.Managed)
                {
                    await using var file = File.OpenRead(managedFiles[s.UniqueName]);
                    await file.CopyToAsync(entry, ct);
                    file.Position = 0;
                    coverage?.Add(file);
                }
                else
                {
                    using var response = await client.PostActionAsync("ExportSolution", new { SolutionName = s.UniqueName, Managed = false }, ct);
                    var zip = response.RootElement.GetProperty("ExportSolutionFile").GetBytesFromBase64();
                    entry.Write(zip);
                    coverage?.Add(new MemoryStream(zip, writable: false));
                }
                entry.Commit();
            }
            result.Add(new PackageSolution(order, s.UniqueName, s.Version, s.Managed, path));
        }
        return result;
    }

    internal const string SolutionsPrefix = "solutions/";

    private static string ManifestVersion(string zipFile)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipFile);
            var entry = zip.GetEntry("solution.xml") ?? throw new InvalidDataException("solution.xml is missing.");
            using var s = entry.Open();
            return XDocument.Load(s).Root?.Element("SolutionManifest")?.Element("Version")?.Value
                ?? throw new InvalidDataException("solution.xml has no version.");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException)
        {
            throw new MigrationExportException($"'{zipFile}' is not a solution file: {e.Message}");
        }
    }
}
