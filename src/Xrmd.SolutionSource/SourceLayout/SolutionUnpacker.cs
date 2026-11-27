using System.Xml.Linq;
using Xrmd.Core.Errors;
using Xrmd.Solution.SourceLayout.Writers;

namespace Xrmd.Solution.SourceLayout;

/// <summary>
/// Turns an unmanaged solution zip into the files of its unpacked source folder, in the layout the reference folders of
/// the Power Platform CLI show: <c>Other/Solution.xml</c>, <c>Other/Customizations.xml</c> with what has no file of its
/// own, and one folder per kind of component. The same zip always gives the same bytes. Files carried in the zip as
/// entries of their own (web resource content, plug-in assemblies, control bundles, …) are copied byte for byte.
/// </summary>
/// <remarks>
/// What the reference folders show and this follows: connection references, team templates and the language list stay in
/// <c>Other/Customizations.xml</c>; custom APIs, environment variable definitions, agents (bots, bot components, skills),
/// app actions and assets come as zip entries of their own and keep their zip path (<see cref="LayoutFolders.PassThrough"/>);
/// an entry in any other folder that no writer places is refused. Every part the reference folders hold nothing for
/// (business rules, business process flows and other processes that are neither cloud flows nor classic workflows among
/// them) is refused with <see cref="ErrorCodes.UnsupportedComponent"/>.
/// </remarks>
public static class SolutionUnpacker
{
    /// <summary>
    /// The top-level parts of <c>customizations.xml</c> that stay in <c>Other/Customizations.xml</c> with their content, as
    /// the reference folders show. Any other part must be emptied by a writer; one that still has content fails the unpack.
    /// </summary>
    private static readonly HashSet<string> KeptInCustomizations = ["teamtemplates", "connectionreferences", "Languages"];

    /// <summary>The files of the source folder for the unmanaged solution zip <paramref name="zip"/>.</summary>
    /// <exception cref="XrmdFaultException">
    /// 400 InvalidArgument when the zip cannot be read (see <see cref="SolutionZip.Read"/>);
    /// 400 <see cref="ErrorCodes.ManagedNotSupported"/> for a managed solution;
    /// 400 <see cref="ErrorCodes.UnsupportedComponent"/> naming the first component with no place in the layout.
    /// </exception>
    public static SourceFiles Unpack(byte[] zip)
    {
        var entries = SolutionZip.ReadRaw(zip);
        var solution = Document(entries, SolutionZip.SolutionEntry);
        var customizations = Document(entries, SolutionZip.CustomizationsEntry);
        entries.Remove(SolutionZip.SolutionEntry);
        entries.Remove(SolutionZip.CustomizationsEntry);
        entries.Remove(SolutionZip.ContentTypesEntry); // packaging only: pack writes it anew

        var manifest = solution.Root?.Element("SolutionManifest")
                       ?? throw XrmdFaultException.InvalidArgument("The solution.xml of the zip has no SolutionManifest.");
        // 0 is unmanaged; 1 (managed) and any other value are refused rather than unpacked as if they were unmanaged.
        if (manifest.Element("Managed")?.Value.Trim() != "0")
            throw XrmdFaultException.ManagedNotSupported(manifest.Element("UniqueName")?.Value ?? "(unnamed)");
        var root = customizations.Root
                   ?? throw XrmdFaultException.InvalidArgument("The customizations.xml of the zip is empty.");

        var context = new UnpackContext(root, entries);
        EntityWriter.Write(context);
        RelationshipWriter.Write(context);
        SecurityWriter.Write(context);
        WorkflowWriter.Write(context);
        OptionSetWriter.Write(context);
        DashboardWriter.Write(context);
        WebResourceWriter.Write(context);
        CustomControlWriter.Write(context);
        PluginWriter.Write(context);
        AppWriter.Write(context);

        foreach (var part in root.Elements())
        {
            if (KeptInCustomizations.Contains(part.Name.LocalName)) continue;
            var first = part.Elements().FirstOrDefault();
            if (first is not null) throw UnpackRules.Unsupported(first);
            if (part.Nodes().Any()) throw UnpackRules.Unsupported(part.Name.LocalName, "(text content)");
            part.RemoveNodes(); // an emptied part is written as an empty element, as the reference folders show
        }
        context.Write("Other/Customizations.xml", root);
        SolutionManifestWriter.Write(context, solution.Root!);

        foreach (var (path, bytes) in context.RemainingEntries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            // An entry in a folder the reference folders never show has no known place: refuse it rather than guess.
            // An entry in a dot-folder would be written but never read back by pack, diff or sync: refuse it as well.
            if (!LayoutFolders.IsPassThrough(path) || !SourceFolder.InComponentFolder(path)) throw UnpackRules.Unsupported("zip entry", path);
            context.Files.Add(path, bytes);
        }
        return context.Files;
    }

    private static XDocument Document(Dictionary<string, byte[]> entries, string name)
    {
        if (!entries.TryGetValue(name, out var bytes))
            throw XrmdFaultException.InvalidArgument($"The solution file is missing {name}.");
        return LayoutXml.Load(bytes, name);
    }
}
