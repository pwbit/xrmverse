using System.Xml.Linq;
using Xrmd.Core.Errors;

namespace Xrmd.Solution.SourceLayout;

/// <summary>
/// The state shared by the writers while a zip is unpacked: the customizations document (each writer takes out what it
/// places in a file of its own), the zip entries not yet placed, and the files written so far.
/// </summary>
internal sealed class UnpackContext(XElement customizations, Dictionary<string, byte[]> entries)
{
    /// <summary>The root of <c>customizations.xml</c>; what is left in it at the end becomes <c>Other/Customizations.xml</c>.</summary>
    public XElement Customizations { get; } = customizations;

    /// <summary>The files written so far.</summary>
    public SourceFiles Files { get; } = new();

    /// <summary>Writes the XML file <paramref name="path"/> with <paramref name="root"/> as its document element.</summary>
    public void Write(string path, XElement root) => Files.Add(path, LayoutXml.Bytes(root));

    /// <summary>
    /// Takes the zip entry that <paramref name="fileName"/> names (as a component's <c>FileName</c> does, with a leading
    /// <c>/</c>) out of the entries still to place: its path as the zip spells it, and its bytes.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument when the zip has no such entry.</exception>
    public (string Path, byte[] Bytes) TakeEntry(string fileName, string component)
    {
        var path = fileName.TrimStart('/');
        var key = entries.Keys.FirstOrDefault(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase));
        if (key is null || !entries.Remove(key, out var bytes))
            throw XrmdFaultException.InvalidArgument($"The solution names the file '{fileName}' for {component}, but the zip does not hold it.");
        return (key, bytes);
    }

    /// <summary>
    /// A component kept in a zip entry of its own, named by its <c>FileName</c> child: the entry is written at the same path,
    /// byte for byte, and the component's XML beside it as <c>&lt;path&gt;.data.xml</c> with <paramref name="rootName"/> as root.
    /// </summary>
    public void WriteWithDataFile(XElement component, string rootName)
    {
        var name = UnpackRules.NameOf(component);
        var fileName = UnpackRules.Required(component.Element("FileName"), $"the FileName of {component.Name.LocalName} '{name}'").Value;
        var (path, bytes) = TakeEntry(fileName, $"{component.Name.LocalName} '{name}'");
        Files.Add(path, bytes);
        Write(path + ".data.xml", LayoutXml.RootFrom(rootName, component));
    }

    /// <summary>The zip entries not placed by a writer, keyed by their path in the zip.</summary>
    public IReadOnlyDictionary<string, byte[]> RemainingEntries => entries;

    /// <summary>The top-level collection of <c>customizations.xml</c> named <paramref name="name"/>, if present.</summary>
    public XElement? Collection(string name) => Customizations.Element(name);
}
