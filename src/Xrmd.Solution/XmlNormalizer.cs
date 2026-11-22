using System.Xml.Linq;

namespace Xrmd.Solution;

/// <summary>
/// The canonical text of a <c>solution.xml</c> or <c>customizations.xml</c>, to compare a solution file xrmverse writes with
/// the one it read (the export round trip). Two documents with the same canonical text carry the same content for xrmverse.
/// </summary>
/// <remarks>
/// <para>Form: comments, processing instructions, the XML declaration and namespace declarations are dropped; XML attributes
/// are sorted by name; whitespace-only text is dropped and text is trimmed; an element without content is written empty.</para>
/// <para>What is ignored (each one is a ruling of the Task 10 report):</para>
/// <list type="number">
/// <item>The attributes of the root <c>ImportExportXml</c> but <c>languagecode</c>: they describe the build of the
/// organization that wrote the file (<c>version</c>, <c>OrganizationVersion</c>, <c>generatedBy</c>, …).</item>
/// <item>The <c>IntroducedVersion</c> elements (<see cref="Ignored"/>): the build that introduced a component (fix rounds 2
/// and 3: everything else xrmverse does not model is kept and compared, <see cref="UnmodelledXml"/>).</item>
/// <item>A label item whose <c>description</c> is empty: an empty label is no label (the reader and the SDK read none).</item>
/// <item>(Withdrawn in fix round 1: <c>DisplayMask</c> is compared as written; xrmverse keeps it.)</item>
/// <item>The value of a step's <c>SdkMessageFilterId</c>: it is the id of the exporting organization's filter, which every
/// import resolves again from the message and <c>PrimaryEntity</c>.</item>
/// <item>The order of the <c>attribute</c> elements of an entity (sorted by logical name) and of a role's
/// <c>RolePrivilege</c> elements (sorted by name): sets keyed by name, whose order means nothing to an import.</item>
/// <item>The case of the entity and attribute names of a relationship (<c>ReferencingEntityName</c>, …): they are matched
/// ignoring case.</item>
/// </list>
/// </remarks>
public static class XmlNormalizer
{
    /// <summary>
    /// The child elements ignored wherever they are: <c>IntroducedVersion</c>, the version of the build that introduced the
    /// component (form: xrmverse keeps the file's, and gives a component it creates the version of a new solution).
    /// </summary>
    private static readonly HashSet<string> Ignored = Names("IntroducedVersion");

    /// <summary>The label items: an empty <c>description</c> is no label.</summary>
    private static readonly HashSet<string> LabelItems = Names(
        "LocalizedName", "LocalizedCollectionName", "Description", "displayname", "label");

    /// <summary>The relationship elements holding entity or attribute names (compared ignoring case).</summary>
    private static readonly HashSet<string> RelationshipNames = Names(
        "ReferencingEntityName", "ReferencedEntityName", "ReferencingAttributeName", "FirstEntityName", "SecondEntityName",
        "IntersectEntityName");

    /// <summary>The canonical text of <paramref name="document"/>.</summary>
    public static string Normalize(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.Root ?? throw new ArgumentException("The document has no root element.", nameof(document));
        var canonical = Element(root, parent: null);
        // Ruling 1: only the base language of the root is content.
        foreach (var attribute in canonical.Attributes().Where(a => a.Name.LocalName != "languagecode").ToList()) attribute.Remove();
        return canonical.ToString();
    }

    private static XElement Element(XElement source, XElement? parent)
    {

        var result = new XElement(source.Name);
        foreach (var attribute in source.Attributes()
                     .Where(a => !a.IsNamespaceDeclaration)
                     .OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal).ThenBy(a => a.Name.LocalName, StringComparer.Ordinal))
            result.Add(new XAttribute(attribute.Name, attribute.Value));

        if (source.HasElements)
        {
            var children = source.Elements()
                .Where(e => !Ignored.Contains(e.Name.LocalName))
                .Where(e => !IsEmptyLabel(e))
                .Select(e => Element(e, source));
            children = source.Name.LocalName switch
            {
                "attributes" when parent?.Name.LocalName == "entity" =>
                    children.OrderBy(e => ((string?)e.Element("Name") ?? "").ToLowerInvariant(), StringComparer.Ordinal),
                "RolePrivileges" => children.OrderBy(e => (string?)e.Attribute("name") ?? "", StringComparer.Ordinal),
                _ => children,
            };
            foreach (var child in children) result.Add(child);
        }
        else
        {
            var text = source.Value.Trim();
            switch (source.Name.LocalName)
            {
                case "SdkMessageFilterId" when parent?.Name.LocalName == "SdkMessageProcessingStep": // Ruling 5
                    text = "";
                    break;
                case var name when RelationshipNames.Contains(name) && parent?.Name.LocalName == "EntityRelationship": // Ruling 7
                    text = text.ToLowerInvariant();
                    break;
            }
            if (text.Length > 0) result.Add(text);
        }
        return result;
    }

    /// <summary>Ruling 3: a label item (<c>description</c> + <c>languagecode</c>) with an empty description.</summary>
    private static bool IsEmptyLabel(XElement element) =>
        LabelItems.Contains(element.Name.LocalName)
        && element.Attribute("languagecode") is not null
        && element.Attribute("description") is { Value.Length: 0 }
        && !element.HasElements;

    private static HashSet<string> Names(params string[] names) => new(names, StringComparer.Ordinal);
}
