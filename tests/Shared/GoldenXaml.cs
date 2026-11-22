using System.Text;
using System.Xml.Linq;

namespace Xrmd.TestSupport;

/// <summary>
/// The canonical form two XAML texts are compared in (plan 2.21, Task 2): every element on its own line, attributes in document
/// order (namespace declarations included), text kept as it is, indentation and line ends normalized. The step label ids
/// (the <c>Default</c> of the <c>stepLabelLabelId</c> variable and <c>mcwo:StepLabel/@LabelId</c>) become <c>LABEL</c>: they are
/// generated (A9). Nothing else is normalized: a difference from a real export is fixed in the writer, never here. Linked into
/// Xrmd.Workflows.Tests and Xrmd.Integration.Tests (plan 2.21, ruling R2: shared without production code).
/// </summary>
internal static class GoldenXaml
{
    private const string Label = "LABEL";

    public static string Canonical(string xaml)
    {
        var root = XDocument.Parse(xaml).Root!;
        var sb = new StringBuilder();
        Write(root, 0, sb);
        return sb.ToString();
    }

    private static void Write(XElement e, int depth, StringBuilder sb)
    {
        sb.Append(' ', depth * 2).Append('<').Append(QName(e, e.Name));
        var isLabelVariable = e.Name.LocalName == "Variable" && (string?)e.Attribute("Name") == "stepLabelLabelId";
        var isStepLabel = e.Name.LocalName == "StepLabel";
        foreach (var a in e.Attributes())
        {
            var value = (isLabelVariable && a.Name == "Default") || (isStepLabel && a.Name == "LabelId") ? Label : a.Value;
            var name = a.IsNamespaceDeclaration
                ? a.Name.Namespace == XNamespace.None ? "xmlns" : "xmlns:" + a.Name.LocalName
                : QName(e, a.Name);
            sb.Append(' ').Append(name).Append("=\"").Append(Escape(value)).Append('"');
        }
        var children = e.Nodes().ToList();
        if (children.Count == 0)
        {
            sb.Append(" />\n");
            return;
        }
        if (children.All(n => n is XText))
        {
            sb.Append('>').Append(Escape(string.Concat(children.Cast<XText>().Select(t => t.Value)))).Append("</").Append(QName(e, e.Name)).Append(">\n");
            return;
        }
        sb.Append(">\n");
        foreach (var node in children)
        {
            switch (node)
            {
                case XElement child:
                    Write(child, depth + 1, sb);
                    break;
                case XText text when !string.IsNullOrWhiteSpace(text.Value):
                    sb.Append(' ', (depth + 1) * 2).Append("#text ").Append(Escape(text.Value)).Append('\n');
                    break;
                case XComment comment:
                    sb.Append(' ', (depth + 1) * 2).Append("<!--").Append(comment.Value).Append("-->\n");
                    break;
            }
        }
        sb.Append(' ', depth * 2).Append("</").Append(QName(e, e.Name)).Append(">\n");
    }

    private static string QName(XElement scope, XName name)
    {
        if (name.Namespace == XNamespace.None)
            return name.LocalName;
        var prefix = scope.GetPrefixOfNamespace(name.Namespace);
        return prefix is null ? name.LocalName : prefix + ":" + name.LocalName;
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")
        .Replace("\r", "&#xD;").Replace("\n", "&#xA;").Replace("\t", "&#x9;");
}
