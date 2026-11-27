using System.Xml.Linq;

namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>
/// Processes, each as its definition file at the path the zip gives it, byte for byte, with the process XML beside it as
/// <c>&lt;file&gt;.data.xml</c>: cloud flows (a JSON file, <c>JsonFileName</c>) and classic workflows (a XAML file,
/// <c>XamlFileName</c>, category 0), as the reference folders show. Every other kind of process (business rules, business
/// process flows, actions, dialogs and anything without a category) has no reference folder yet and is refused.
/// </summary>
internal static class WorkflowWriter
{
    private const string ClassicWorkflowCategory = "0";

    public static void Write(UnpackContext context)
    {
        var workflows = context.Collection("Workflows");
        if (workflows is null) return;
        foreach (var workflow in workflows.Elements())
        {
            if (workflow.Name != "Workflow") throw UnpackRules.Unsupported(workflow);
            var name = UnpackRules.NameOf(workflow);
            var (_, fileName) = Definition(workflow, name);
            var (path, bytes) = context.TakeEntry(fileName, $"process '{name}'");
            context.Files.Add(path, bytes);
            context.Write(path + ".data.xml", LayoutXml.RootFrom("Workflow", workflow));
        }
        workflows.RemoveNodes();
    }

    /// <summary>Pack: each <c>.data.xml</c> under <c>Workflows/</c> gives a process, with its definition file beside it.</summary>
    public static void Read(PackContext context)
    {
        var definitions = context.Unclaimed("Workflows/").Where(k => k.EndsWith(".data.xml", StringComparison.Ordinal)).ToList();
        if (definitions.Count == 0) return;
        var container = context.Container("Workflows");
        foreach (var dataPath in definitions)
        {
            var workflow = PackContext.FromRoot("Workflow", context.Load(dataPath, "Workflow", "the processes"));
            var (element, _) = Definition(workflow, UnpackRules.NameOf(workflow));
            context.AddPayload(workflow, element, dataPath[..^".data.xml".Length], dataPath);
            container.Add(workflow);
        }
    }

    /// <summary>
    /// The child naming the process's definition file (<c>JsonFileName</c> or <c>XamlFileName</c>) and its value; a process
    /// with both, with neither, or with XAML of a category other than a classic workflow is refused.
    /// </summary>
    private static (string Element, string FileName) Definition(XElement workflow, string name)
    {
        var json = workflow.Element("JsonFileName")?.Value;
        var xaml = workflow.Element("XamlFileName")?.Value;
        if (!string.IsNullOrEmpty(json) && xaml is null) return ("JsonFileName", json);
        if (!string.IsNullOrEmpty(xaml) && json is null && workflow.Element("Category")?.Value.Trim() == ClassicWorkflowCategory)
            return ("XamlFileName", xaml);
        throw UnpackRules.Unsupported("process that is neither a cloud flow nor a classic workflow (business rule, business process flow, action or dialog)", name);
    }
}
