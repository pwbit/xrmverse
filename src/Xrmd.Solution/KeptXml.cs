using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;

namespace Xrmd.Solution;

/// <summary>
/// The XML an import keeps in <c>solution.xrmverse_unsupportedxml</c> for the export to emit again:
/// <c>&lt;xrmverse_unsupported&gt;</c> with <c>ImportExportXml</c> (the root nodes of unsupported components), one
/// <c>Entity Name="…"</c> per entity with kept entity-level nodes (ribbons, charts, control configurations), and
/// <c>SolutionManifest languagecode="…"</c> (the solution's names and descriptions in every language, the file's
/// <c>Publisher</c>), and <c>Files</c> (the archive entries no reader consumed, <c>File path="…"</c> with the content in
/// base64).
/// </summary>
public sealed record KeptXml(
    IReadOnlyList<XElement> Unsupported,
    IReadOnlyList<(string EntityLogicalName, XElement Xml)> EntityUnsupported,
    int? LanguageCode,
    Label? SolutionNames,
    Label? SolutionDescriptions,
    XElement? Publisher,
    IReadOnlyDictionary<string, byte[]>? Files = null)
{
    /// <summary>The name of the root element.</summary>
    public const string Root = "xrmverse_unsupported";

    /// <summary>Nothing kept (a solution not imported from a file).</summary>
    public static KeptXml None { get; } = new([], [], null, null, null, null);

    /// <summary>Reads the column's value; <see cref="None"/> for null.</summary>
    /// <exception cref="InvalidOperationException">The value is not the XML an import writes.</exception>
    public static KeptXml Parse(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) return None;
        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            root = XElement.Load(reader);
        }
        catch (XmlException e)
        {
            throw new InvalidOperationException($"The kept solution XML (xrmverse_unsupportedxml) is not valid XML: {e.Message}", e);
        }
        if (root.Name.LocalName != Root)
            throw new InvalidOperationException($"The kept solution XML (xrmverse_unsupportedxml) has the root '{root.Name.LocalName}', not '{Root}'.");

        var manifest = root.Element("SolutionManifest");
        int? language = int.TryParse((string?)manifest?.Attribute("languagecode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? code
            : null;
        return new KeptXml(
            root.Element("ImportExportXml")?.Elements().Select(e => new XElement(e)).ToList() ?? [],
            root.Elements("Entity").SelectMany(e => e.Elements().Select(n => ((string)e.Attribute("Name")!, new XElement(n)))).ToList(),
            language,
            manifest?.Element("LocalizedNames") is { } names ? ReadLabels(names, "LocalizedName", language) : null,
            manifest?.Element("Descriptions") is { } descriptions ? ReadLabels(descriptions, "Description", language) : null,
            manifest?.Element("Publisher") is { } publisher ? new XElement(publisher) : null,
            root.Element("Files")?.Elements("File").ToDictionary(
                f => (string?)f.Attribute("path") ?? throw new InvalidOperationException("The kept solution XML (xrmverse_unsupportedxml) has a File without a path."),
                f => Convert.FromBase64String(f.Value), StringComparer.Ordinal));
    }

    /// <summary>The <c>Files</c> element of the kept entries (<c>File path="…"</c>, base64 content), ordered by path.</summary>
    public static XElement FilesElement(IReadOnlyDictionary<string, byte[]>? files) =>
        new("Files", (files ?? new Dictionary<string, byte[]>()).OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new XElement("File", new XAttribute("path", f.Key), Convert.ToBase64String(f.Value))));

    /// <summary>A label container (<c>&lt;LocalizedNames&gt;&lt;LocalizedName description=".." languagecode=".."/&gt;</c>).</summary>
    public static XElement Labels(string container, string item, Label? label) =>
        new(container, (label?.LocalizedLabels ?? []).Where(l => !string.IsNullOrEmpty(l.Label)).Select(l =>
            new XElement(item, new XAttribute("description", l.Label),
                new XAttribute("languagecode", l.LanguageCode.ToString(CultureInfo.InvariantCulture)))));

    private static Label ReadLabels(XElement container, string item, int? baseLanguage)
    {
        var labels = container.Elements(item)
            .Select(e => (Text: (string?)e.Attribute("description"),
                Language: int.TryParse((string?)e.Attribute("languagecode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : (int?)null))
            .Where(l => !string.IsNullOrEmpty(l.Text) && l.Language is not null)
            .Select(l => new LocalizedLabel(l.Text, l.Language!.Value))
            .ToArray();
        if (labels.Length == 0) return new Label();
        return new Label(labels.FirstOrDefault(l => l.LanguageCode == baseLanguage) ?? labels[0], labels);
    }
}
