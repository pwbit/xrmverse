namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>
/// Processes stored as JSON (cloud flows): the JSON file at the path the zip gives it, byte for byte, and the definition
/// beside it as <c>&lt;file&gt;.data.xml</c>. Processes of other kinds have no reference folder yet and are refused.
/// </summary>
internal static class WorkflowWriter
{
    public static void Write(UnpackContext context)
    {
        var workflows = context.Collection("Workflows");
        if (workflows is null) return;
        foreach (var workflow in workflows.Elements())
        {
            if (workflow.Name != "Workflow") throw UnpackRules.Unsupported(workflow);
            var name = UnpackRules.NameOf(workflow);
            var json = workflow.Element("JsonFileName")?.Value;
            if (string.IsNullOrEmpty(json) || workflow.Element("XamlFileName") is not null)
                throw UnpackRules.Unsupported("process without a JSON definition (classic workflow, business rule, action or business process flow)", name);
            var (path, bytes) = context.TakeEntry(json, $"process '{name}'");
            context.Files.Add(path, bytes);
            context.Write(path + ".data.xml", LayoutXml.RootFrom("Workflow", workflow));
        }
        workflows.RemoveNodes();
    }

    /// <summary>Pack: each <c>.data.xml</c> under <c>Workflows/</c> gives a process, with the JSON file beside it.</summary>
    public static void Read(PackContext context)
    {
        var definitions = context.Unclaimed("Workflows/").Where(k => k.EndsWith(".data.xml", StringComparison.Ordinal)).ToList();
        if (definitions.Count == 0) return;
        var container = context.Container("Workflows");
        foreach (var dataPath in definitions)
        {
            var workflow = PackContext.FromRoot("Workflow", context.Load(dataPath, "Workflow", "the processes"));
            context.AddPayload(workflow, "JsonFileName", dataPath[..^".data.xml".Length], dataPath);
            container.Add(workflow);
        }
    }
}
