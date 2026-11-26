using Xrmd.Core.Errors;
using System.Xml.Linq;

namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>
/// Relationships: <c>Other/Relationships.xml</c> lists every relationship by name, and
/// <c>Other/Relationships/&lt;table&gt;.xml</c> holds the definitions, grouped by the referenced table (one-to-many) or the
/// first table (many-to-many).
/// </summary>
internal static class RelationshipWriter
{
    public static void Write(UnpackContext context)
    {
        var relationships = context.Collection("EntityRelationships");
        if (relationships is null || !relationships.HasElements) return;

        var groups = new SortedDictionary<string, List<XElement>>(StringComparer.Ordinal);
        foreach (var relationship in relationships.Elements())
        {
            if (relationship.Name != "EntityRelationship") throw UnpackRules.Unsupported(relationship);
            var name = UnpackRules.NameOf(relationship);
            var type = relationship.Element("EntityRelationshipType")?.Value;
            var table = type switch
            {
                "OneToMany" => relationship.Element("ReferencedEntityName")?.Value,
                "ManyToMany" => relationship.Element("FirstEntityName")?.Value,
                _ => throw UnpackRules.Unsupported($"relationship of type {type ?? "(none)"}", name),
            };
            if (string.IsNullOrEmpty(table)) throw UnpackRules.Unsupported("relationship without its table", name);
            UnpackRules.SafeSegment(table, "table");
            if (!groups.TryGetValue(table, out var list)) groups[table] = list = [];
            list.Add(relationship);
        }

        context.Write("Other/Relationships.xml", LayoutXml.Root("EntityRelationships",
            relationships.Elements().Select(r => new XElement("EntityRelationship", new XAttribute("Name", UnpackRules.NameOf(r))))));
        foreach (var (table, list) in groups)
            context.Write($"Other/Relationships/{table}.xml", LayoutXml.Root("EntityRelationships", list));
        relationships.RemoveNodes();
    }

    /// <summary>Pack: the relationships in the order <c>Other/Relationships.xml</c> lists them, each defined exactly once.</summary>
    public static void Read(PackContext context)
    {
        const string list = "Other/Relationships.xml";
        if (!context.Has(list)) return;
        var names = context.Load(list, "EntityRelationships", "the relationships").Elements()
            .Select(e => (string?)e.Attribute("Name") ?? throw XrmdFaultException.InvalidArgument($"The file {list} lists a relationship without a Name."))
            .ToList();

        var defined = new Dictionary<string, (XElement Element, string Path)>(StringComparer.Ordinal);
        foreach (var path in context.Unclaimed("Other/Relationships/").Where(k => !k["Other/Relationships/".Length..].Contains('/')))
        {
            foreach (var relationship in context.Load(path, "EntityRelationships", list).Elements())
            {
                var name = (string?)relationship.Attribute("Name") ?? "";
                if (!defined.TryAdd(name, (relationship, path)))
                    throw XrmdFaultException.InvalidArgument($"The relationship '{name}' is defined twice: in {defined[name].Path} and in {path}.");
            }
        }

        var container = context.Container("EntityRelationships");
        foreach (var name in names)
        {
            if (!defined.Remove(name, out var found))
                throw XrmdFaultException.InvalidArgument($"The file {list} lists the relationship '{name}', but no file under Other/Relationships/ defines it.");
            container.Add(new XElement(found.Element));
        }
        if (defined.Count > 0)
        {
            var (name, (_, path)) = defined.OrderBy(d => d.Key, StringComparer.Ordinal).First();
            throw XrmdFaultException.InvalidArgument($"The file {path} defines the relationship '{name}', which {list} does not list.");
        }
    }
}
