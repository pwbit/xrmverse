using System.Collections.Concurrent;
using System.IO.Compression;
using Xunit;

namespace Xrmd.TestSupport;

/// <summary>
/// The solution source folders under tests/fixtures/solutions/SourceLayout: two clones (a, b) per solution made with the
/// Power Platform CLI, plus the matching unmanaged zip. The fixture folder is copied next to the test assembly.
/// The folder stays in the private repository (eng/public-export/manifest.txt): in the public copy the tests that read it
/// are skipped, as for <see cref="PrivateFixtures"/>; here a missing folder fails them.
/// No .dll is committed (repository rule): the plug-in assembly files of the clones are taken from the solution's zip,
/// which holds the same bytes, when a clone folder is first asked for.
/// </summary>
public static class SourceLayoutFixtures
{
    /// <summary>The solutions that have fixtures.</summary>
    public static readonly string[] Solutions = ["OpenProject365", "XrmSharedTest", "xrmCalcTest"];

    private static string Root { get; } =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "SourceLayout");

    /// <summary>Why a test on the source folder fixtures is skipped, or null when it runs.</summary>
    public static string? SkipReason =>
        PrivateFixtures.PublicMirror && !Directory.Exists(Root) ? "The solution source folder fixtures are not in the public repository." : null;

    /// <summary>The source folder of clone <paramref name="copy"/> ('a' or 'b') of a solution (the folder holding Other/Solution.xml).</summary>
    public static string Folder(string solution, char copy) =>
        Restored.GetOrAdd(solution + "/" + copy, _ => RestoreAssemblies(solution, Path.Combine(Root, solution, copy.ToString())));

    private static readonly ConcurrentDictionary<string, string> Restored = new();

    // The clones hold PluginAssemblies/<name>-<id>/<name>.dll; the files are left out of git and written back from the zip.
    private static string RestoreAssemblies(string solution, string folder)
    {
        var zip = Path.Combine(Root, solution, solution + ".zip");
        if (!Directory.Exists(Path.Combine(folder, "PluginAssemblies")) || !File.Exists(zip))
            return folder;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.TrimStart('/');
            if (!name.StartsWith("PluginAssemblies/", StringComparison.Ordinal) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;
            var target = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target) && Directory.Exists(Path.GetDirectoryName(target)))
                entry.ExtractToFile(target);
        }
        return folder;
    }

    /// <summary>The unmanaged zip exported at the same version as the clones.</summary>
    public static byte[] Zip(string solution) => File.ReadAllBytes(Path.Combine(Root, solution, solution + ".zip"));

    /// <summary>
    /// The <c>Other/EntityMaps.xml</c> of a later clone of xrmCalcTest that had one column mapping (account → xrmv_test);
    /// only this file is kept from that clone.
    /// </summary>
    public static byte[] EntityMapsReference => File.ReadAllBytes(Path.Combine(Root, "xrmCalcTest", "entitymap", "EntityMaps.xml"));

    /// <summary>A full copy of the XrmSharedTest clone folder, with its .cdsproj file.</summary>
    public static string CdsprojSample { get; } = Path.Combine(Root, "XrmSharedTest", "cdsproj-sample");
}

/// <summary>A fact that reads the solution source folder fixtures: skipped in the public copy of the repository.</summary>
public sealed class SourceLayoutFactAttribute : FactAttribute
{
    public SourceLayoutFactAttribute() => Skip = SourceLayoutFixtures.SkipReason;
}

/// <summary>A theory that reads the solution source folder fixtures: skipped in the public copy of the repository.</summary>
public sealed class SourceLayoutTheoryAttribute : TheoryAttribute
{
    public SourceLayoutTheoryAttribute() => Skip = SourceLayoutFixtures.SkipReason;
}
