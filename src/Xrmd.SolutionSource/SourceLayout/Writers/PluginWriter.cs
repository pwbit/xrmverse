namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>
/// Plug-ins: each assembly at its zip path, byte for byte, with its definition (plug-in types included) beside it as
/// <c>&lt;assembly&gt;.data.xml</c>; each step, with its images, in <c>SdkMessageProcessingSteps/&lt;step id&gt;.xml</c>.
/// </summary>
internal static class PluginWriter
{
    public static void Write(UnpackContext context)
    {
        var assemblies = context.Collection("SolutionPluginAssemblies");
        if (assemblies is not null)
        {
            foreach (var assembly in assemblies.Elements())
            {
                if (assembly.Name != "PluginAssembly") throw UnpackRules.Unsupported(assembly);
                context.WriteWithDataFile(assembly, "PluginAssembly");
            }
            assemblies.RemoveNodes();
        }

        var steps = context.Collection("SdkMessageProcessingSteps");
        if (steps is not null)
        {
            foreach (var step in steps.Elements())
            {
                if (step.Name != "SdkMessageProcessingStep") throw UnpackRules.Unsupported(step);
                var id = (string?)step.Attribute("SdkMessageProcessingStepId") ?? "";
                context.Write($"SdkMessageProcessingSteps/{UnpackRules.SafeSegment(id, "step id")}.xml",
                    LayoutXml.RootFrom("SdkMessageProcessingStep", step));
            }
            steps.RemoveNodes();
        }
    }

    /// <summary>Pack: the plug-in assemblies (each with the assembly beside its definition) and the steps.</summary>
    public static void Read(PackContext context)
    {
        var definitions = context.Unclaimed("PluginAssemblies/").Where(k => k.EndsWith(".data.xml", StringComparison.Ordinal)).ToList();
        if (definitions.Count > 0)
        {
            var container = context.Container("SolutionPluginAssemblies");
            foreach (var dataPath in definitions)
            {
                var assembly = PackContext.FromRoot("PluginAssembly", context.Load(dataPath, "PluginAssembly", "the plug-in assemblies"));
                context.AddPayload(assembly, "FileName", dataPath[..^".data.xml".Length], dataPath);
                container.Add(assembly);
            }
        }

        var steps = context.Unclaimed("SdkMessageProcessingSteps/").Where(k => !k["SdkMessageProcessingSteps/".Length..].Contains('/')).ToList();
        if (steps.Count > 0)
        {
            var container = context.Container("SdkMessageProcessingSteps");
            foreach (var path in steps)
                container.Add(PackContext.FromRoot("SdkMessageProcessingStep", context.Load(path, "SdkMessageProcessingStep", "the plug-in steps")));
        }
    }
}
