using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Xrmd.Solution.SourceLayout;

/// <summary>
/// The values an export changes by itself, which the comparison of an organization's solution with a source folder leaves
/// out so that a solution nobody touched shows no differences. The list is fixed and short on purpose: each entry names the
/// files it applies to and the element it drops, and says why that value is noise.
/// </summary>
internal static class VolatileValues
{
    /// <summary>
    /// One ignored value: the elements at <paramref name="Element"/> (element names from the root, separated by <c>/</c>) in
    /// the files whose path matches <paramref name="Files"/> (a path relative to the folder, where <c>*</c> stands for any
    /// part of one path segment); with <paramref name="Attribute"/>, only that attribute of those elements.
    /// </summary>
    internal sealed record Rule(string Files, string Element, string? Attribute = null)
    {
        private readonly Regex _files = new(
            "^" + Regex.Escape(Files).Replace(@"\*", "[^/]*", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant);

        public bool Matches(string path) => _files.IsMatch(path);
    }

    /// <summary>The ignored values.</summary>
    public static readonly IReadOnlyList<Rule> Rules =
    [
        // The solution version is raised on every release, often by the export itself; comparing it would report the
        // solution as changed after every release even when no component moved.
        new("Other/Solution.xml", "ImportExportXml/SolutionManifest/Version"),

        // The root of the solution and customizations files carries stamps of the server that exported them: the version of
        // its file format, its product name and build, its schema kind. A folder written by one server and an export from
        // another differ in all of them although no component does. The language (languagecode) is the solution's own and
        // still counts.
        .. RootStamps("Other/Solution.xml"),
        .. RootStamps("Other/Customizations.xml"),
    ];

    private static IEnumerable<Rule> RootStamps(string file) =>
        new[] { "version", "SolutionPackageVersion", "generatedBy", "OrganizationVersion", "OrganizationSchemaType", "CRMServerServiceabilityVersion" }
            .Select(attribute => new Rule(file, "ImportExportXml", attribute));

    /// <summary>Removes from <paramref name="document"/>, read from <paramref name="path"/>, every value a rule ignores.</summary>
    public static void Remove(string path, XDocument document)
    {
        foreach (var rule in Rules)
        {
            if (!rule.Matches(path) || document.Root is not { } root) continue;
            var names = rule.Element.Split('/');
            if (root.Name.LocalName != names[0]) continue;
            IEnumerable<XElement> found = [root];
            foreach (var name in names.Skip(1)) found = found.Elements().Where(e => e.Name.LocalName == name);
            foreach (var element in found.ToList())
            {
                if (rule.Attribute is null) element.Remove();
                else element.Attributes().Where(a => a.Name.LocalName == rule.Attribute && a.Name.Namespace == XNamespace.None).Remove();
            }
        }
    }
}
