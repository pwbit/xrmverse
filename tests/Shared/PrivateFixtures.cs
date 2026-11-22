using Xunit;

namespace Xrmd.TestSupport;

/// <summary>
/// Fixtures that stay in the private repository: the OpenProject365 export is not public (eng/public-export/manifest.txt).
/// In the public copy (eng/public-export/export.sh writes <c>.public-mirror</c> at its root) the tests that read it are
/// skipped; here a missing export fails them, as before.
/// </summary>
public static class PrivateFixtures
{
    /// <summary>The unmodified OpenProject365 1.0.1.1 export, copied next to the test assembly.</summary>
    public static string OpenProject365Path { get; } =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "OpenProject365_1_0_1_1.zip");

    /// <summary>True in the public copy of the repository.</summary>
    public static bool PublicMirror { get; } = FindUp(".public-mirror");

    /// <summary>Why a test on the OpenProject365 export is skipped, or null when it runs.</summary>
    public static string? OpenProject365SkipReason =>
        PublicMirror && !File.Exists(OpenProject365Path) ? "The OpenProject365 export is not in the public repository." : null;

    private static bool FindUp(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "xrmverse.sln")))
                return File.Exists(Path.Combine(dir.FullName, name));
        return false;
    }
}

/// <summary>A fact that reads the OpenProject365 export: skipped in the public copy of the repository.</summary>
public sealed class OpenProject365FactAttribute : FactAttribute
{
    public OpenProject365FactAttribute() => Skip = PrivateFixtures.OpenProject365SkipReason;
}

/// <summary>A theory that reads the OpenProject365 export: skipped in the public copy of the repository.</summary>
public sealed class OpenProject365TheoryAttribute : TheoryAttribute
{
    public OpenProject365TheoryAttribute() => Skip = PrivateFixtures.OpenProject365SkipReason;
}
