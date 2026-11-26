using System.Xml.Linq;
using Xrmd.Core.Errors;

namespace Xrmd.Solution.SourceLayout;

/// <summary>
/// The state shared by the readers while a source folder is packed: the folder's files and which of them a reader has
/// claimed, the customizations document being filled, and the zip entries collected so far.
/// </summary>
internal sealed class PackContext(SourceFiles files)
{
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

    /// <summary>The folder's files.</summary>
    public SourceFiles Files { get; } = files;

    /// <summary>The root of <c>customizations.xml</c> (read from <c>Other/Customizations.xml</c>, then filled by the readers).</summary>
    public XElement Customizations { get; set; } = null!;

    /// <summary>The zip entries other than <c>solution.xml</c> and <c>customizations.xml</c>, keyed by their path in the zip.</summary>
    public Dictionary<string, byte[]> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>The source file behind each zip entry, keyed by entry path ignoring case.</summary>
    private readonly Dictionary<string, string> _entryOwners = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ReservedEntries =
        [SolutionZip.SolutionEntry, SolutionZip.CustomizationsEntry, SolutionZip.ContentTypesEntry];

    /// <summary>
    /// Adds the zip entry <paramref name="path"/>, which the source file <paramref name="owner"/> gives.
    /// </summary>
    /// <exception cref="XrmdFaultException">
    /// 400 InvalidArgument when the path is not a safe relative path, is one of the entries pack writes itself, or equals
    /// (ignoring case) an entry another source file already gives; the message names the files involved.
    /// </exception>
    public void AddEntry(string path, byte[] bytes, string owner)
    {
        SourceFiles.ValidateKey(path);
        if (ReservedEntries.Any(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase)))
            throw XrmdFaultException.InvalidArgument($"The file {owner} names the zip entry '{path}', which pack writes itself.");
        if (_entryOwners.TryGetValue(path, out var other))
            throw XrmdFaultException.InvalidArgument(
                $"The files {other} and {owner} both give the zip entry '{path}' (zip entry names are compared ignoring case).");
        _entryOwners.Add(path, owner);
        Entries.Add(path, bytes);
    }

    /// <summary>The files no reader has claimed yet whose path starts with <paramref name="prefix"/>, in ordinal order.</summary>
    public List<string> Unclaimed(string prefix) =>
        Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !_claimed.Contains(k)).Order(StringComparer.Ordinal).ToList();

    /// <summary>Whether the folder holds <paramref name="path"/>.</summary>
    public bool Has(string path) => Files.ContainsKey(path);

    /// <summary>Claims the file <paramref name="path"/> and returns its bytes.</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the path when the folder does not hold it.</exception>
    public byte[] Take(string path, string neededBy)
    {
        if (!Files.TryGetValue(path, out var bytes))
            throw XrmdFaultException.InvalidArgument($"The file {path} is missing from the source folder; {neededBy} needs it.");
        _claimed.Add(path);
        return bytes;
    }

    /// <summary>Claims the XML file <paramref name="path"/> and returns its root, which must be named <paramref name="rootName"/>.</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument when it is missing, not valid XML or has another root.</exception>
    public XElement Load(string path, string rootName, string neededBy)
    {
        var root = LayoutXml.Load(Take(path, neededBy), path).Root!;
        if (root.Name != rootName)
            throw XrmdFaultException.InvalidArgument($"The file {path} must have a {rootName} root element, not {root.Name.LocalName}.");
        return root;
    }

    /// <summary>
    /// The top-level part <paramref name="name"/> of the customizations, added before <c>Languages</c> when
    /// <c>Other/Customizations.xml</c> lacks it.
    /// </summary>
    public XElement Container(string name)
    {
        var container = Customizations.Element(name);
        if (container is not null) return container;
        container = new XElement(name);
        if (Customizations.Element("Languages") is { } languages) languages.AddBeforeSelf(container);
        else Customizations.Add(container);
        return container;
    }

    /// <summary>
    /// The zip entry a component names in its <c>FileName</c> (or <c>JsonFileName</c>) child: the source file at the same
    /// path, which must be <paramref name="expectedPath"/> (where unpack puts it), is claimed and added to the entries.
    /// </summary>
    public void AddPayload(XElement component, string fileNameElement, string expectedPath, string dataPath)
    {
        var fileName = component.Element(fileNameElement)?.Value
                       ?? throw XrmdFaultException.InvalidArgument($"The file {dataPath} has no {fileNameElement}.");
        var path = fileName.TrimStart('/');
        if (path != expectedPath)
            throw XrmdFaultException.InvalidArgument($"The file {dataPath} names the file '{fileName}', but it must name the file beside it, {expectedPath}.");
        AddEntry(path, Take(path, dataPath), dataPath);
    }

    /// <summary>A new element named <paramref name="name"/> with the attributes (no namespace declarations) and nodes of <paramref name="root"/>.</summary>
    public static XElement FromRoot(string name, XElement root) =>
        new(name, root.Attributes().Where(a => !a.IsNamespaceDeclaration), root.Nodes());

    /// <summary>Fills the empty element <paramref name="placeholder"/> with the attributes and nodes of <paramref name="root"/>.</summary>
    public static void Fill(XElement placeholder, XElement root)
    {
        placeholder.Add(root.Attributes().Where(a => !a.IsNamespaceDeclaration));
        placeholder.Add(root.Nodes());
    }

    /// <summary>Path segments of <paramref name="path"/> after <paramref name="prefix"/>.</summary>
    public static string[] Rest(string path, string prefix) => path[prefix.Length..].Split('/');
}
