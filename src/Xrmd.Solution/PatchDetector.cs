using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Xrmd.Solution;

/// <summary>
/// Tells a solution patch file apart (plan 2.6, ruling M12): xrmverse does not support solution patches yet, so a patch file is
/// refused before anything is written, never imported as a plain solution. The one place the later patch item replaces.
/// </summary>
public static partial class PatchDetector
{
    /// <summary>The text of every refusal of a patch: a patch file, <c>CloneAsPatch</c>, <c>CloneAsSolution</c>.</summary>
    public const string NotSupported = "solution patches are not supported yet by xrmverse";

    /// <summary>
    /// True when the solution is a patch: its <c>SolutionManifest</c> (<paramref name="manifest"/>) has an element named
    /// <c>ParentSolution</c> at any depth (ignoring case), or its unique name ends with <c>_Patch_</c> and eight hexadecimal
    /// digits (the name pattern is from memory, plan 2.6 checklist).
    /// </summary>
    /// <param name="manifest">The <c>SolutionManifest</c> element of <c>solution.xml</c>; null when the file has none.</param>
    /// <param name="uniqueName">The solution's unique name; null or empty when the file has none.</param>
    public static bool IsPatch(XElement? manifest, string? uniqueName) =>
        (manifest?.Descendants().Any(e => string.Equals(e.Name.LocalName, "ParentSolution", StringComparison.OrdinalIgnoreCase)) ?? false)
        || (uniqueName is { Length: > 0 } && PatchName().IsMatch(uniqueName));

    [GeneratedRegex("_Patch_[0-9a-f]{8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PatchName();
}
