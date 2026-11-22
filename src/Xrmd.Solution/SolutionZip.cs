using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Xrmd.Core.Errors;

namespace Xrmd.Solution;

/// <summary>
/// Reads a solution file (the <c>solution.zip</c> of §7): <c>solution.xml</c>, <c>customizations.xml</c> and every other
/// entry (plug-in assemblies, web resources, <c>[Content_Types].xml</c>, …). The archive is untrusted input: entries with
/// absolute or parent-relative paths are refused, the uncompressed size is capped and the XML is read without DTDs.
/// </summary>
public static class SolutionZip
{
    /// <summary>The name of the solution manifest entry.</summary>
    public const string SolutionEntry = "solution.xml";

    /// <summary>The name of the customizations entry.</summary>
    public const string CustomizationsEntry = "customizations.xml";

    /// <summary>
    /// The largest total uncompressed size of the entries read (100 MB): bounds the memory a crafted archive (a zip bomb)
    /// can take. Dataverse limits solution files to a lower compressed size, so a real solution stays far below it.
    /// </summary>
    public const long MaxUncompressedBytes = 100L * 1024 * 1024;

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
    };

    /// <summary>
    /// The two XML documents and the other entries of the archive, keyed by their path inside it (<c>/</c> separators, no
    /// leading <c>/</c>, compared ignoring case as the paths written by Windows tools are).
    /// </summary>
    /// <exception cref="XrmdFaultException">
    /// 400 InvalidArgument when the bytes are not a zip archive, an entry path is absolute or leaves the archive, an entry
    /// appears twice, the content exceeds <see cref="MaxUncompressedBytes"/>, <c>solution.xml</c> or
    /// <c>customizations.xml</c> is missing or is not valid XML.
    /// </exception>
    public static (XDocument Solution, XDocument Customizations, IReadOnlyDictionary<string, byte[]> Files) Read(byte[] zip)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var entries = ReadEntries(zip);

        var solution = Document(entries, SolutionEntry);
        var customizations = Document(entries, CustomizationsEntry);
        entries.Remove(SolutionEntry);
        entries.Remove(CustomizationsEntry);
        return (solution, customizations, entries);
    }

    private static Dictionary<string, byte[]> ReadEntries(byte[] zip)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw XrmdFaultException.InvalidArgument("The solution file is not a valid zip archive.");
        }

        using (archive)
        {
            var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var path = NormalizePath(entry.FullName);
                if (path.EndsWith('/')) continue; // a directory entry
                if (entries.ContainsKey(path))
                    throw XrmdFaultException.InvalidArgument($"The solution file contains the entry '{entry.FullName}' more than once.");
                entries[path] = ReadContent(entry, ref total);
            }

            return entries;
        }
    }

    /// <summary>The entry path with <c>/</c> separators; refuses absolute paths and <c>..</c> segments.</summary>
    private static string NormalizePath(string fullName)
    {
        var path = fullName.Replace('\\', '/');
        var invalid = path.Length == 0
                      || path.StartsWith('/')
                      || (path.Length >= 2 && path[1] == ':')
                      || path.Split('/').Any(segment => segment == "..");
        if (invalid) throw XrmdFaultException.InvalidArgument($"The solution file contains an entry with an invalid path: '{fullName}'.");
        return path;
    }

    /// <summary>Reads the entry counting the bytes actually inflated (the sizes in the archive's headers are not trusted).</summary>
    private static byte[] ReadContent(ZipArchiveEntry entry, ref long total)
    {
        try
        {
            using var source = entry.Open();
            using var content = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxUncompressedBytes)
                    throw XrmdFaultException.InvalidArgument(
                        $"The solution file is larger than {MaxUncompressedBytes / (1024 * 1024)} MB uncompressed.");
                content.Write(buffer, 0, read);
            }

            return content.ToArray();
        }
        catch (InvalidDataException)
        {
            throw XrmdFaultException.InvalidArgument("The solution file is not a valid zip archive.");
        }
    }

    /// <summary>The name of the Open Packaging content types entry.</summary>
    public const string ContentTypesEntry = "[Content_Types].xml";

    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>
    /// Writes a solution file: <c>[Content_Types].xml</c>, <c>solution.xml</c>, <c>customizations.xml</c> and
    /// <paramref name="files"/> (keyed by their path in the archive, <c>/</c> separators, no leading <c>/</c>). The content
    /// types declare <c>xml</c> and <c>dll</c> by extension and every other file by its part name, as Dataverse exports do
    /// (all <c>application/octet-stream</c>).
    /// </summary>
    /// <exception cref="ArgumentException">A file path that <see cref="Read"/> would refuse, or one of the three reserved names.</exception>
    public static byte[] Write(XDocument solution, XDocument customizations, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(customizations);
        ArgumentNullException.ThrowIfNull(files);
        string[] defaults = ["xml", "dll"];
        var types = new XElement(ContentTypes + "Types",
            defaults.Select(extension => new XElement(ContentTypes + "Default",
                new XAttribute("Extension", extension), new XAttribute("ContentType", "application/octet-stream"))));
        foreach (var path in files.Keys.Order(StringComparer.Ordinal))
        {
            if (path.Length == 0 || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(segment => segment is ".." or "")
                || path.Equals(SolutionEntry, StringComparison.OrdinalIgnoreCase) || path.Equals(CustomizationsEntry, StringComparison.OrdinalIgnoreCase)
                || path.Equals(ContentTypesEntry, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{path}' is not a valid path for a file of a solution.", nameof(files));
            if (!defaults.Contains(Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
                types.Add(new XElement(ContentTypes + "Override",
                    new XAttribute("PartName", "/" + path), new XAttribute("ContentType", "application/octet-stream")));
        }

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, byte[] content)
            {
                using var stream = archive.CreateEntry(path, CompressionLevel.Optimal).Open();
                stream.Write(content);
            }

            Add(ContentTypesEntry, Bytes(new XDocument(types)));
            Add(SolutionEntry, Bytes(solution));
            Add(CustomizationsEntry, Bytes(customizations));
            foreach (var (path, content) in files.OrderBy(f => f.Key, StringComparer.Ordinal)) Add(path, content);
        }
        return buffer.ToArray();
    }

    /// <summary>The document as UTF-8 (with its BOM, as Dataverse writes it) and the XML declaration.</summary>
    private static byte[] Bytes(XDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(true), Indent = true }))
            document.Save(writer);
        return stream.ToArray();
    }

    private static XDocument Document(Dictionary<string, byte[]> entries, string name)
    {
        if (!entries.TryGetValue(name, out var bytes))
            throw XrmdFaultException.InvalidArgument($"The solution file is missing {name}.");
        return Xml(bytes, name);
    }

    /// <summary>The XML document of an archive entry (no DTD, no external resources, comments dropped).</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming <paramref name="path"/> when it is not valid XML.</exception>
    internal static XDocument Xml(byte[] bytes, string path)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), XmlSettings);
            return XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw XrmdFaultException.InvalidArgument($"The solution file contains a {path} that is not valid XML: {e.Message}");
        }
    }
}
