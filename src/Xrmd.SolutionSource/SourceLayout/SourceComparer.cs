using System.Xml.Linq;
using Xrmd.Core.Errors;

namespace Xrmd.Solution.SourceLayout;

/// <summary>
/// One component that differs between an organization's solution and a source folder.
/// </summary>
/// <param name="Status">
/// <c>changed</c> (in both, some of its files differ or are only on one side), <c>missing</c> (only in the folder) or
/// <c>new</c> (only in the organization).
/// </param>
/// <param name="Type">The kind of component, such as <c>form</c>, <c>view</c>, <c>table</c> or <c>webresource</c>.</param>
/// <param name="Name">The component's display or unique name where its files give one, else its id or file name.</param>
/// <param name="Path">The component's place in the folder: its file, its folder, or for a pair of content and data file the content file.</param>
/// <param name="Files">For <c>changed</c> the files that differ; otherwise all the component's files. In ordinal order.</param>
public sealed record ComponentDifference(string Status, string Type, string Name, string Path, IReadOnlyList<string> Files);

/// <summary>The components that differ, in ordinal order of their <see cref="ComponentDifference.Path"/>.</summary>
public sealed record SourceDiff(IReadOnlyList<ComponentDifference> Items)
{
    /// <summary>Whether anything differs.</summary>
    public bool Any => Items.Count > 0;
}

/// <summary>
/// Compares the unpacked files of an organization's solution with the files of a source folder, component by component.
/// XML files compare by meaning (attribute order, whitespace between elements, the declaration, the byte order mark and the
/// way an empty element is written do not count) less the values an export changes by itself (<see cref="VolatileValues"/>);
/// every other file, and an XML file that does not parse, compares byte for byte.
/// </summary>
public static class SourceComparer
{
    private static readonly string[] XmlExtensions = [".xml", ".xaml", ".resx"];

    /// <summary>The components of <paramref name="organization"/> and <paramref name="folder"/> that differ.</summary>
    public static SourceDiff Compare(SourceFiles organization, SourceFiles folder)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(folder);

        var components = new Dictionary<string, (Component Component, SortedSet<string> Files)>(StringComparer.Ordinal);
        foreach (var path in organization.Keys.Concat(folder.Keys))
        {
            var component = Identify(path);
            if (!components.TryGetValue(component.Path, out var entry))
                components[component.Path] = entry = (component, new SortedSet<string>(StringComparer.Ordinal));
            entry.Files.Add(path);
        }

        var items = new List<ComponentDifference>();
        foreach (var (path, (component, files)) in components.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var inOrganization = files.Any(organization.ContainsKey);
            var inFolder = files.Any(folder.ContainsKey);
            var (status, differing) = (inOrganization, inFolder) switch
            {
                (false, _) => ("missing", files.ToList()),
                (_, false) => ("new", files.ToList()),
                _ => ("changed", files.Where(f => !SameFile(f, organization, folder)).ToList()),
            };
            if (differing.Count == 0) continue;
            var name = NameOf(component, inOrganization ? organization : folder);
            items.Add(new ComponentDifference(status, component.Type, name, path, differing));
        }
        return new SourceDiff(items);
    }

    /// <summary>
    /// Whether two versions of the file <paramref name="path"/> carry the same content: byte for byte, or for an XML file
    /// the same meaning as <see cref="Compare"/> reads it (formatting, attribute order and the order of a table's columns do
    /// not count) with every value kept, the ones <see cref="Compare"/> leaves out included. Sync leaves such a file as it is.
    /// </summary>
    public static bool SameContent(string path, byte[] a, byte[] b)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (a.AsSpan().SequenceEqual(b)) return true;
        if (!XmlExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return false;
        var canonicalA = Canonical(path, a, dropVolatile: false);
        return canonicalA is not null && canonicalA == Canonical(path, b, dropVolatile: false);
    }

    private static bool SameFile(string path, SourceFiles organization, SourceFiles folder)
    {
        if (!organization.TryGetValue(path, out var a) || !folder.TryGetValue(path, out var b)) return false;
        if (a.AsSpan().SequenceEqual(b)) return true;
        if (!XmlExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return false;
        var canonicalA = Canonical(path, a);
        var canonicalB = Canonical(path, b);
        return canonicalA is not null && canonicalA == canonicalB;
    }

    /// <summary>
    /// The XML of <paramref name="bytes"/> written one way only: attributes sorted, namespace declarations left to the
    /// writer, comments and processing instructions dropped, text and CDATA as text, and no formatting. Null when the
    /// bytes are not XML.
    /// </summary>
    private static string? Canonical(string path, byte[] bytes, bool dropVolatile = true)
    {
        var document = TryLoad(path, bytes);
        if (document?.Root is null) return null;
        if (dropVolatile) VolatileValues.Remove(path, document);
        SortColumns(path, document);
        return Canonical(document.Root).ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// In a table's <c>Entity.xml</c>, puts the columns (<c>Entity/EntityInfo/entity/attributes/attribute</c>) in order of
    /// their <c>PhysicalName</c>, ignoring case: the order of the columns means nothing to Dataverse and differs between an
    /// export and the file that was imported, so it is not a difference. Each column still compares in full.
    /// </summary>
    private static void SortColumns(string path, XDocument document)
    {
        var parts = path.Split('/');
        if (parts is not ["Entities", _, "Entity.xml"] || document.Root is not { } root) return;
        foreach (var attributes in root.Elements("EntityInfo").Elements("entity").Elements("attributes"))
        {
            var columns = attributes.Elements("attribute").ToList();
            if (columns.Count < 2) continue;
            var sorted = columns.OrderBy(c => (string?)c.Attribute("PhysicalName") ?? "", StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var column in columns) column.Remove();
            attributes.Add(sorted);
        }
    }

    private static XElement Canonical(XElement element) => new(element.Name,
        element.Attributes().Where(a => !a.IsNamespaceDeclaration)
            .OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal).ThenBy(a => a.Name.LocalName, StringComparer.Ordinal)
            .Select(a => new XAttribute(a.Name, a.Value)),
        element.Nodes().Select(n => n switch
        {
            XElement child => (object?)Canonical(child),
            XText text => text.Value, // XCData is an XText; adjacent strings merge, so CDATA and text compare alike
            _ => null,
        }));

    private static XDocument? TryLoad(string path, byte[] bytes)
    {
        try
        {
            return LayoutXml.Load(bytes, path);
        }
        catch (XrmdFaultException)
        {
            return null;
        }
    }

    /// <summary>What a file belongs to: its component's kind and path, the file that names it, and a name to fall back on.</summary>
    private sealed record Component(string Type, string Path, string? NameFile, string FallbackName);

    /// <summary>
    /// The component a file belongs to, grouped the way unpack writes them: one file per form, view, chart, step, dashboard,
    /// role, global choice, relationship file and <c>Other/</c> file; a content file and its <c>.data.xml</c> for web resources, processes
    /// and plug-in assemblies; a folder for apps, site maps, code components and the folders kept as the zip has them.
    /// </summary>
    private static Component Identify(string path)
    {
        var parts = path.Split('/');
        var file = parts[^1];
        var stem = System.IO.Path.GetFileNameWithoutExtension(file);
        switch (parts[0])
        {
            case "Entities" when parts.Length == 3 && file == "Entity.xml":
                return new("table", path, null, parts[1]);
            case "Entities" when parts.Length == 3 && file == "RibbonDiff.xml":
                return new("ribbon", path, null, parts[1]);
            case "Entities" when parts.Length == 5 && parts[2] == "FormXml":
                return new("form", path, path, stem);
            case "Entities" when parts.Length == 4 && parts[2] == "SavedQueries":
                return new("view", path, path, stem);
            case "Entities" when parts.Length == 4 && parts[2] == "Formulas":
                return new("formula", path, null, stem);
            case "Entities" when parts.Length == 4 && parts[2] == "Visualizations":
                return new("chart", path, path, stem);
            case "OptionSets" when parts.Length == 2:
                return new("choice", path, path, stem);
            case "WebResources" when parts.Length >= 2:
            {
                var content = WithoutDataSuffix(path);
                return new("webresource", content, null, content["WebResources/".Length..]);
            }
            case "Workflows" when parts.Length == 2:
            {
                var content = WithoutDataSuffix(path);
                return new("workflow", content, content + ".data.xml", System.IO.Path.GetFileNameWithoutExtension(content));
            }
            case "PluginAssemblies" when parts.Length >= 2:
            {
                var content = WithoutDataSuffix(path);
                return new("pluginassembly", content, content + ".data.xml", System.IO.Path.GetFileNameWithoutExtension(content));
            }
            case "SdkMessageProcessingSteps" when parts.Length == 2:
                return new("step", path, path, stem);
            case "Dashboards" when parts.Length == 2:
                return new("dashboard", path, path, stem);
            case "Roles" when parts.Length == 2:
                return new("role", path, path, stem);
            case "Assets" when parts.Length == 2:
                return new("asset", path, null, file);
            case "Other" when parts.Length == 3 && parts[1] == "Relationships":
                return new("relationship", path, null, stem);
            case "Other" when parts.Length == 2:
                return new(stem.ToLowerInvariant(), path, null, stem);
        }
        if (parts.Length >= 3 && FolderTypes.TryGetValue(parts[0], out var type))
            return new(type, parts[0] + "/" + parts[1], null, parts[1]);
        return new("file", path, null, path);
    }

    /// <summary>The folders in which every subfolder is one component, and the kind of component it is.</summary>
    private static readonly Dictionary<string, string> FolderTypes = new(StringComparer.Ordinal)
    {
        ["AppModules"] = "app",
        ["AppModuleSiteMaps"] = "sitemap",
        ["Controls"] = "control",
        ["customapis"] = "customapi",
        ["environmentvariabledefinitions"] = "environmentvariable",
        ["bots"] = "bot",
        ["botcomponents"] = "botcomponent",
        ["skills"] = "skill",
        ["appactions"] = "appaction",
    };

    private static string WithoutDataSuffix(string path) =>
        path.EndsWith(".data.xml", StringComparison.Ordinal) ? path[..^".data.xml".Length] : path;

    /// <summary>
    /// The name a person knows the component by: from its naming file, the English (1033) localized name, else the first
    /// one, else a <c>Name</c> or <c>FullName</c> attribute; otherwise the fallback (an id, file or folder name).
    /// </summary>
    private static string NameOf(Component component, SourceFiles files)
    {
        if (component.NameFile is null || !files.TryGetValue(component.NameFile, out var bytes)) return component.FallbackName;
        var root = TryLoad(component.NameFile, bytes)?.Root;
        if (root is null) return component.FallbackName;
        var element = root.Name.LocalName is "forms" or "savedqueries" ? root.Elements().FirstOrDefault() ?? root : root;

        var localized = element.Element("LocalizedNames")?.Elements("LocalizedName").ToList() ?? [];
        var english = localized.FirstOrDefault(l => (string?)l.Attribute("languagecode") == "1033") ?? localized.FirstOrDefault();
        if ((string?)english?.Attribute("description") is { Length: > 0 } description) return description;
        foreach (var attribute in new[] { "Name", "name" })
            if ((string?)element.Attribute(attribute) is { Length: > 0 } value) return value;
        if ((string?)element.Attribute("FullName") is { Length: > 0 } fullName) return fullName.Split(',')[0].Trim();
        return component.FallbackName;
    }
}
