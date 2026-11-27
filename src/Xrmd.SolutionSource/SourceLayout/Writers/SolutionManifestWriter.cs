using System.Globalization;
using System.Xml.Linq;

namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>
/// <c>Other/Solution.xml</c>: the solution manifest as the zip holds it, with the missing dependencies in a stable order
/// (an export lists them in no fixed order; the reference folders sort them) and the root components grouped by their
/// <c>type</c> compared as text, as the reference folders show (type 29 before type 9); within a type the zip's order stays.
/// </summary>
internal static class SolutionManifestWriter
{
    public static void Write(UnpackContext context, XElement solution)
    {
        var roots = solution.Element("SolutionManifest")?.Element("RootComponents");
        if (roots is not null)
        {
            // Root components are grouped by type, the type compared as text ("29" before "9"). This is inferred from the
            // reference folders: XrmSharedTest's zip lists types 1, 9, 29 and both clones list 1, 29, 9; OpenProject365 agrees.
            // The order within one type is not shown by them; OrderBy is stable, so it stays as the zip gives it. Import looks
            // root components up by type and key, so this order only affects how Other/Solution.xml reads in git.
            var grouped = roots.Elements().OrderBy(r => (string?)r.Attribute("type") ?? "", StringComparer.Ordinal).ToList();
            roots.ReplaceNodes(grouped);
        }
        var missing = solution.Element("SolutionManifest")?.Element("MissingDependencies");
        if (missing is not null)
        {
            var sorted = missing.Elements().OrderBy(d => d, Order).ToList();
            missing.ReplaceNodes(sorted);
        }
        context.Write("Other/Solution.xml", solution);
    }

    private static readonly Comparer<XElement> Order = Comparer<XElement>.Create(Compare);

    private static int Compare(XElement x, XElement y)
    {
        foreach (var part in new[] { "Required", "Dependent" })
        {
            var a = x.Element(part);
            var b = y.Element(part);
            var c = CompareType((string?)a?.Attribute("type"), (string?)b?.Attribute("type"));
            if (c != 0) return c;
            foreach (var attribute in new[] { "schemaName", "displayName", "parentSchemaName", "id" })
            {
                c = string.CompareOrdinal((string?)a?.Attribute(attribute), (string?)b?.Attribute(attribute));
                if (c != 0) return c;
            }
        }
        return 0;
    }

    /// <summary>Component types are numbers, with a few named types; numbers come first, in numeric order.</summary>
    private static int CompareType(string? x, string? y)
    {
        var xNumber = int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var xn);
        var yNumber = int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var yn);
        if (xNumber && yNumber) return xn.CompareTo(yn);
        if (xNumber != yNumber) return xNumber ? -1 : 1;
        return string.CompareOrdinal(x, y);
    }

    /// <summary>Pack: the solution manifest, as the folder holds it.</summary>
    public static XElement Read(PackContext context) =>
        context.Load("Other/Solution.xml", "ImportExportXml", "every solution");
}
