using System.Xml.Linq;
using Xrmd.Core.Errors;
using Xrmd.Solution.SourceLayout.Writers;

namespace Xrmd.Solution.SourceLayout;

/// <summary>
/// Turns the files of an unpacked source folder back into a solution zip: the inverse of <see cref="SolutionUnpacker"/>.
/// Every file must be claimed by the layout: a file the layout names but the folder lacks, and a file the layout has no
/// place for, both fail rather than give a zip that differs silently from the folder.
/// </summary>
public static class SolutionPacker
{
    /// <summary>The solution zip of the source folder <paramref name="files"/>.</summary>
    /// <exception cref="XrmdFaultException">
    /// 400 InvalidArgument naming the path when a file the layout needs is missing, a file is not valid XML (DTDs are
    /// refused), a file does not match its place, or a file has no place in the layout.
    /// </exception>
    public static byte[] Pack(SourceFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var context = new PackContext(files);
        var solution = SolutionManifestWriter.Read(context);
        context.Customizations = context.Load("Other/Customizations.xml", "ImportExportXml", "every solution");

        EntityWriter.Read(context);
        RelationshipWriter.Read(context);
        SecurityWriter.Read(context);
        WorkflowWriter.Read(context);
        OptionSetWriter.Read(context);
        DashboardWriter.Read(context);
        WebResourceWriter.Read(context);
        CustomControlWriter.Read(context);
        PluginWriter.Read(context);
        AppWriter.Read(context);

        foreach (var folder in LayoutFolders.PassThrough)
            foreach (var path in context.Unclaimed(folder))
                context.AddEntry(path, context.Take(path, "the zip"), path);

        var unexpected = context.Unclaimed("");
        if (unexpected.Count > 0)
            throw XrmdFaultException.InvalidArgument(
                $"The source folder holds files that have no place in a solution: {string.Join(", ", unexpected.Take(5))}{(unexpected.Count > 5 ? $" and {unexpected.Count - 5} more" : "")}.");

        return SolutionZip.Write(new XDocument(solution), new XDocument(context.Customizations), context.Entries);
    }
}
