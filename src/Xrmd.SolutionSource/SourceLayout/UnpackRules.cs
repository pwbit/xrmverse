using System.Xml.Linq;
using Xrmd.Core.Errors;

namespace Xrmd.Solution.SourceLayout;

/// <summary>Checks shared by the unpack writers.</summary>
internal static class UnpackRules
{
    private static readonly string[] NameAttributes = ["Name", "name", "uniquename", "UniqueName", "LogicalName", "logicalname"];
    private static readonly string[] NameElements = ["Name", "name", "UniqueName", "uniquename", "LogicalName", "logicalname", "schemaname", "SchemaName"];

    /// <summary>The fault for an element of the solution that has no place in the layout, named as well as it can be.</summary>
    public static XrmdFaultException Unsupported(XElement component) =>
        XrmdFaultException.UnsupportedComponent(component.Name.LocalName, NameOf(component));

    /// <summary>The fault for a component of kind <paramref name="kind"/> named <paramref name="name"/> with no place in the layout.</summary>
    public static XrmdFaultException Unsupported(string kind, string name) => XrmdFaultException.UnsupportedComponent(kind, name);

    /// <summary>The best name of a component element: a name attribute, a name child, else its first attribute or child value.</summary>
    public static string NameOf(XElement component)
    {
        foreach (var a in NameAttributes)
            if (component.Attribute(a) is { } attribute && attribute.Value.Length > 0) return attribute.Value;
        foreach (var e in NameElements)
            if (component.Element(e) is { } element && element.Value.Length > 0) return element.Value;
        var first = component.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration)?.Value
                    ?? component.Elements().FirstOrDefault()?.Value;
        return string.IsNullOrEmpty(first) ? "(no name)" : first;
    }

    /// <summary><paramref name="element"/>, or a 400 fault saying that <paramref name="what"/> is missing.</summary>
    public static XElement Required(XElement? element, string what) =>
        element ?? throw XrmdFaultException.InvalidArgument($"The solution's customizations lack {what}.");

    /// <summary>
    /// <paramref name="value"/> when it can be one segment of a relative path on every file system the folder may be checked
    /// out on (see <see cref="IsPortableSegment"/>); otherwise a 400 fault naming <paramref name="what"/>.
    /// </summary>
    public static string SafeSegment(string value, string what)
    {
        if (!IsPortableSegment(value)) throw XrmdFaultException.InvalidArgument($"The {what} '{value}' cannot be used as a file or folder name.");
        return value;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Whether <paramref name="segment"/> can be a file or folder name on Windows as well as elsewhere: not empty, not
    /// <c>.</c> or <c>..</c>, none of <c>/ \ : * ? " &lt; &gt; |</c> or a control character, no trailing dot or space, and
    /// not a device name (CON, PRN, AUX, NUL, COM1–9, LPT1–9), with or without an extension.
    /// </summary>
    public static bool IsPortableSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Length == 0 || segment is "." or "..") return false;
        if (segment.Any(c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c))) return false;
        if (segment[^1] is '.' or ' ') return false;
        var dot = segment.IndexOf('.');
        return !ReservedNames.Contains(dot < 0 ? segment : segment[..dot]);
    }
}
