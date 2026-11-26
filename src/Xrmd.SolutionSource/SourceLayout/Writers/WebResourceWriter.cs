using Xrmd.Core.Errors;
namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>
/// Web resources: the content, byte for byte, at <c>WebResources/&lt;name&gt;</c> (the name's <c>/</c> parts become folders),
/// and the definition beside it as <c>&lt;name&gt;.data.xml</c>. The definition keeps the file name the zip used.
/// </summary>
internal static class WebResourceWriter
{
    public static void Write(UnpackContext context)
    {
        var webResources = context.Collection("WebResources");
        if (webResources is null) return;
        foreach (var webResource in webResources.Elements())
        {
            if (webResource.Name != "WebResource") throw UnpackRules.Unsupported(webResource);
            var name = UnpackRules.Required(webResource.Element("Name"), "WebResource/Name").Value;
            foreach (var segment in name.Split('/')) UnpackRules.SafeSegment(segment, "web resource name part");
            var fileName = UnpackRules.Required(webResource.Element("FileName"), $"the FileName of web resource '{name}'").Value;
            var (_, content) = context.TakeEntry(fileName, $"web resource '{name}'");

            var path = "WebResources/" + name;
            context.Files.Add(path, content);
            context.Write(path + ".data.xml", LayoutXml.RootFrom("WebResource", webResource));
        }
        webResources.RemoveNodes();
    }

    /// <summary>
    /// Pack: each <c>.data.xml</c> under <c>WebResources/</c> gives a web resource whose content is the file beside it,
    /// written to the zip under the file name the definition keeps.
    /// </summary>
    public static void Read(PackContext context)
    {
        var definitions = context.Unclaimed("WebResources/").Where(k => k.EndsWith(".data.xml", StringComparison.Ordinal)).ToList();
        if (definitions.Count == 0) return;
        var container = context.Container("WebResources");
        foreach (var dataPath in definitions)
        {
            var contentPath = dataPath[..^".data.xml".Length];
            var webResource = PackContext.FromRoot("WebResource", context.Load(dataPath, "WebResource", "the web resources"));
            if ("WebResources/" + webResource.Element("Name")?.Value != contentPath)
                throw XrmdFaultException.InvalidArgument($"The file {dataPath} must name the web resource '{contentPath["WebResources/".Length..]}', as its path does.");
            var fileName = webResource.Element("FileName")?.Value
                           ?? throw XrmdFaultException.InvalidArgument($"The file {dataPath} has no FileName.");
            context.AddEntry(fileName.TrimStart('/'), context.Take(contentPath, dataPath), dataPath);
            container.Add(webResource);
        }
    }
}
