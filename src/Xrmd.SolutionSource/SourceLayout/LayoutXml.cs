using System.Text;
using System.Xml;
using System.Xml.Linq;
using Xrmd.Core.Errors;

namespace Xrmd.Solution.SourceLayout;

/// <summary>
/// How the XML files of a source folder are written, as the reference folders show it: UTF-8 with a byte order mark, an
/// XML declaration, two-space indentation, LF line ends, no line end after the root's closing tag, and the
/// <c>xsi</c> namespace declared on every root element.
/// </summary>
internal static class LayoutXml
{
    /// <summary>The XML Schema instance namespace (<c>xsi:nil</c> appears throughout solution XML).</summary>
    public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly XmlWriterSettings Settings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
    };

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
    };

    /// <summary>
    /// The XML of a zip entry, read for rewriting: no DTD, no external resources, and the whitespace between elements dropped
    /// (the layout indents files itself).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming <paramref name="path"/> when it is not valid XML.</exception>
    public static XDocument Load(byte[] bytes, string path)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), ReaderSettings);
            return XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw XrmdFaultException.InvalidArgument($"The file {path} is not valid XML: {e.Message}");
        }
    }

    /// <summary>A new root element named <paramref name="name"/> that declares <c>xsi</c> first, holding <paramref name="content"/>.</summary>
    public static XElement Root(string name, params object?[] content) =>
        new(name, new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName), content);

    /// <summary>
    /// A root element named <paramref name="name"/> holding copies of the attributes and child nodes of
    /// <paramref name="source"/>: its own attributes first, then the <c>xsi</c> declaration (other namespace declarations of
    /// the source are dropped). With <paramref name="declareXsi"/> false the declaration is left out unless the content
    /// uses the namespace.
    /// </summary>
    public static XElement RootFrom(string name, XElement source, bool declareXsi = true)
    {
        var usesXsi = declareXsi || source.DescendantsAndSelf().Attributes().Any(a => a.Name.Namespace == Xsi);
        return new XElement(name,
            source.Attributes().Where(a => !a.IsNamespaceDeclaration),
            usesXsi ? new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName) : null,
            source.Nodes());
    }

    /// <summary>The bytes of a file whose document element is <paramref name="root"/>.</summary>
    public static byte[] Bytes(XElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, Settings))
        {
            writer.WriteStartDocument();
            root.WriteTo(writer);
            writer.WriteEndDocument();
        }
        return stream.ToArray();
    }
}
