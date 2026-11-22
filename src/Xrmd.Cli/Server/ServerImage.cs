using System.Reflection;
using System.Text.RegularExpressions;

namespace Xrmd.Cli.Server;

/// <summary>
/// The server image an installation runs (<c>XRMD_IMAGE</c> in <c>.env</c>): pinned to the version of the CLI that wrote
/// it, never <c>latest</c>. A CLI without a release version (a development build) uses the image built locally.
/// </summary>
public static partial class ServerImage
{
    /// <summary>The published repository of the server image.</summary>
    public const string Repository = "ghcr.io/pwbit/xrmd";

    /// <summary>
    /// The repository of the server image before the GitHub organization was renamed (Power-Builders-Italia → pwbit).
    /// Installations written by an older CLI still name it; <see cref="WithTag"/> moves them to <see cref="Repository"/>.
    /// </summary>
    public const string LegacyRepository = "ghcr.io/power-builders-italia/xrmd";

    /// <summary>The image a development CLI writes: <c>docker build -f docker/Dockerfile -t xrmd:local .</c>.</summary>
    public const string LocalImage = "xrmd:local";

    /// <summary>The build command printed next to <see cref="LocalImage"/>.</summary>
    public const string LocalBuildCommand = "docker build -f docker/Dockerfile -t xrmd:local .";

    /// <summary>The version of the running CLI (entry assembly informational version, without build metadata); null when unknown.</summary>
    public static string? CliVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version)) return null;
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    /// <summary>
    /// The image of a new installation: <see cref="Repository"/>:<paramref name="cliVersion"/>; <see cref="LocalImage"/> for a
    /// development version (null, <c>0.0.0</c> or <c>0.0.0-*</c>). A version that is not SemVer is refused (exit 2).
    /// </summary>
    public static string Default(string? cliVersion)
    {
        if (string.IsNullOrWhiteSpace(cliVersion) || cliVersion == "0.0.0" || cliVersion.StartsWith("0.0.0-", StringComparison.Ordinal))
            return LocalImage;
        RequireSemVer(cliVersion, "The CLI version");
        return $"{Repository}:{cliVersion}";
    }

    /// <summary>
    /// <paramref name="image"/> with its tag replaced by <paramref name="version"/>. Refused: a version that is not SemVer
    /// (exit 2), and an image that is not <see cref="Repository"/>, with or without a tag (exit 5): a local or custom image has no tag to move.
    /// An image of <see cref="LegacyRepository"/> is accepted and moved to <see cref="Repository"/>.
    /// </summary>
    public static string WithTag(string image, string version)
    {
        RequireSemVer(version, "The version");
        RequireMovable(image);
        return $"{Repository}:{version}";
    }

    /// <summary>
    /// Refuses (exit 5) an image whose tag cannot be moved: anything but <see cref="Repository"/> or
    /// <see cref="LegacyRepository"/>, with or without a tag (a local or custom image).
    /// </summary>
    public static void RequireMovable(string image)
    {
        if (!IsPublished(image, Repository) && !IsPublished(image, LegacyRepository))
            throw new CliException(ExitCodes.Rejected,
                $"XRMD_IMAGE is '{image}', not {Repository}: only that image can be moved to another version. " +
                "Edit XRMD_IMAGE in .env yourself to run another image.");
    }

    /// <summary>The tag of <paramref name="image"/> when it is <see cref="Repository"/>:&lt;tag&gt; or the legacy one; else null.</summary>
    public static string? TagOf(string image)
    {
        foreach (var repository in new[] { Repository, LegacyRepository })
            if (image.StartsWith(repository + ":", StringComparison.Ordinal) && !image.Contains('@'))
                return image[(repository.Length + 1)..];
        return null;
    }

    /// <summary>True when <paramref name="version"/> is a SemVer version (no build metadata), such as 1.4.2 or 1.5.0-rc.1.</summary>
    public static bool IsSemVer(string version) => SemVerPattern().IsMatch(version);

    /// <summary>
    /// SemVer 2.0 precedence of two versions accepted by <see cref="IsSemVer"/>: a negative number when <paramref name="a"/>
    /// comes first. A pre-release comes before its release; pre-release identifiers compare numerically when both are numbers,
    /// else ordinally, and numbers come before text.
    /// </summary>
    public static int CompareSemVer(string a, string b)
    {
        static (long[] Core, string[] Pre) Split(string v)
        {
            var dash = v.IndexOf('-');
            var core = (dash < 0 ? v : v[..dash]).Split('.').Select(long.Parse).ToArray();
            return (core, dash < 0 ? [] : v[(dash + 1)..].Split('.'));
        }
        var (ac, ap) = Split(a);
        var (bc, bp) = Split(b);
        for (var i = 0; i < 3; i++)
            if (ac[i] != bc[i]) return ac[i].CompareTo(bc[i]);
        if (ap.Length == 0 || bp.Length == 0) return bp.Length.CompareTo(ap.Length); // no pre-release ranks higher
        for (var i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            var an = long.TryParse(ap[i], out var x) && ap[i].All(char.IsAsciiDigit);
            var bn = long.TryParse(bp[i], out var y) && bp[i].All(char.IsAsciiDigit);
            var c = (an, bn) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(ap[i], bp[i]),
            };
            if (c != 0) return Math.Sign(c);
        }
        return ap.Length.CompareTo(bp.Length);
    }

    private static bool IsPublished(string image, string repository) =>
        image == repository || (image.StartsWith(repository + ":", StringComparison.Ordinal) && !image.Contains('@'));

    private static void RequireSemVer(string version, string what)
    {
        if (!SemVerPattern().IsMatch(version))
            throw new CliException(ExitCodes.Usage, $"{what} must be a SemVer version such as 1.4.2 or 1.5.0-rc.1: '{version}'.");
    }

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z")]
    private static partial Regex SemVerPattern();
}
