using System.Text;
using System.Xml.Linq;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Solution;

/// <summary>
/// Reads the <c>CustomControls</c> node of <c>customizations.xml</c> and the files it names (plan 3.5 UI-1d, ruling R3): for each
/// <c>&lt;CustomControl&gt;&lt;Name/&gt;&lt;FileName/&gt;&lt;/CustomControl&gt;</c> the manifest at <c>FileName</c> (leading slash
/// dropped, as in <c>Controls/{name}/ControlManifest.xml</c>) and every file of the manifest's folder. Every file the manifest's
/// <c>resources</c> names (<c>code</c>, <c>css</c>, <c>resx</c>, <c>img</c>, <c>html</c>), relative to that folder, must be in the
/// archive. No database access.
/// </summary>
internal static class CustomControlReader
{
    /// <summary>The <c>resources</c> children that name a file of the control.</summary>
    private static readonly HashSet<string> FileResources = new(StringComparer.Ordinal) { "code", "css", "resx", "img", "html" };

    /// <summary>The <c>compatibledatatypes</c> entry of a control with a <c>data-set</c>.</summary>
    internal const string DataSet = "dataset";

    /// <summary>The controls of <paramref name="node"/>, in the order of the node, and the archive paths read.</summary>
    /// <param name="node">The <c>CustomControls</c> element.</param>
    /// <param name="files">The archive entries.</param>
    /// <exception cref="XrmdFaultException">
    /// 400 InvalidArgument for a <c>CustomControl</c> without <c>Name</c> or <c>FileName</c>, two with one name (ignoring case), a
    /// manifest missing from the archive or that is not a control manifest, a file the manifest names that the archive lacks, and a
    /// file whose path relative to the manifest folder is longer than <see cref="CustomControlFiles.MaxPathLength"/> (naming the
    /// control and the file).
    /// </exception>
    public static (List<PackageCustomControl> Controls, List<string> Read) Read(XElement node, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(files);
        var controls = new List<PackageCustomControl>();
        var read = new List<string>();
        // The manifest folders of every control: a folder nested in another control's folder holds that control's files only.
        var folders = node.Elements("CustomControl")
            .Select(e => CustomizationsReader.Value(e, "FileName") is { } f ? FolderOf(Normalize(f)) : "")
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var element in node.Elements("CustomControl"))
        {
            var name = CustomizationsReader.Value(element, "Name")
                       ?? throw XrmdFaultException.InvalidArgument("customizations.xml: a CustomControl has no Name.");
            if (controls.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw XrmdFaultException.InvalidArgument($"customizations.xml has the custom control '{name}' twice (ignoring case).");
            var fileName = CustomizationsReader.Value(element, "FileName")
                           ?? throw XrmdFaultException.InvalidArgument($"Custom control '{name}' has no FileName.");
            var manifestPath = Normalize(fileName);
            if (!files.TryGetValue(manifestPath, out var manifestBytes))
                throw XrmdFaultException.InvalidArgument(
                    $"Custom control '{name}': the solution file has no manifest '{manifestPath}' (its FileName).");
            var folder = FolderOf(manifestPath);

            var root = SolutionZip.Xml(manifestBytes, $"'{manifestPath}'").Root!;
            var control = root.Name.LocalName == "manifest" ? root.Element("control") : null;
            if (control is null)
                throw XrmdFaultException.InvalidArgument(
                    $"Custom control '{name}': '{manifestPath}' is not a control manifest (no manifest/control element).");

            foreach (var resource in control.Element("resources")?.Elements().Where(e => FileResources.Contains(e.Name.LocalName)) ?? [])
            {
                var path = ((string?)resource.Attribute("path"))?.Trim();
                if (string.IsNullOrEmpty(path))
                    throw XrmdFaultException.InvalidArgument($"Custom control '{name}': its manifest has a {resource.Name.LocalName} resource without a path.");
                var relative = Normalize(path);
                if (!files.ContainsKey(Join(folder, relative)))
                    throw XrmdFaultException.InvalidArgument(
                        $"Custom control '{name}': the solution file has no '{relative}' that its manifest names ('{Join(folder, relative)}').");
            }

            // Every file of the manifest's folder, byte for byte (the manifest included): the export writes them back.
            var prefix = folder.Length == 0 ? "" : folder + "/";
            var nested = folders.Where(f => f.Length > folder.Length && f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(f => f + "/").ToList();
            var own = files.Keys.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && p.Length > prefix.Length
                                            && !nested.Any(n => p.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
                .Order(StringComparer.Ordinal).ToList();
            if (folder.Length == 0) own = [manifestPath];
            if (own.FirstOrDefault(p => p.Length - prefix.Length > CustomControlFiles.MaxPathLength) is { } tooLong)
                throw XrmdFaultException.InvalidArgument(
                    $"Custom control '{name}': the path of '{tooLong[prefix.Length..]}' is longer than {CustomControlFiles.MaxPathLength} characters (relative to its manifest folder '{folder}').");
            read.AddRange(own);
            controls.Add(new PackageCustomControl(
                name,
                manifestPath,
                Encoding.UTF8.GetString(manifestBytes).TrimStart('﻿'),
                ((string?)control.Attribute("version"))?.Trim() ?? "",
                CompatibleDataTypes(control),
                own.ToDictionary(p => p[prefix.Length..], p => files[p], StringComparer.Ordinal),
                new XElement(element)));
        }
        return (controls, read);
    }

    /// <summary>
    /// The <c>of-type</c> of each bound <c>property</c> (the types of its <c>type-group</c> for an <c>of-type-group</c>), in
    /// manifest order without repeats, then <see cref="DataSet"/> for a control with a <c>data-set</c>; comma-joined.
    /// </summary>
    internal static string CompatibleDataTypes(XElement control)
    {
        var types = new List<string>();
        foreach (var property in control.Elements("property").Where(p => string.Equals((string?)p.Attribute("usage"), "bound", StringComparison.OrdinalIgnoreCase)))
        {
            if (((string?)property.Attribute("of-type"))?.Trim() is { Length: > 0 } type) types.Add(type);
            else if (((string?)property.Attribute("of-type-group"))?.Trim() is { Length: > 0 } group)
                types.AddRange(control.Elements("type-group").Where(g => (string?)g.Attribute("name") == group)
                    .Elements("type").Select(t => t.Value.Trim()).Where(t => t.Length > 0));
        }
        if (control.Elements("data-set").Any()) types.Add(DataSet);
        return string.Join(",", types.Distinct(StringComparer.Ordinal));
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    private static string FolderOf(string manifestPath) => manifestPath.LastIndexOf('/') is > 0 and var slash ? manifestPath[..slash] : "";

    private static string Join(string folder, string relative) => folder.Length == 0 ? relative : $"{folder}/{relative}";
}
