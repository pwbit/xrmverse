namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>
/// Code components: the manifest, bundle and resource files stay at their zip paths under <c>Controls/&lt;name&gt;/</c>, byte
/// for byte; the component's definition goes beside the manifest as <c>ControlManifest.xml.data.xml</c>.
/// </summary>
internal static class CustomControlWriter
{
    public static void Write(UnpackContext context)
    {
        var controls = context.Collection("CustomControls");
        if (controls is null) return;
        foreach (var control in controls.Elements())
        {
            if (control.Name != "CustomControl") throw UnpackRules.Unsupported(control);
            context.WriteWithDataFile(control, "CustomControl");
        }
        controls.RemoveNodes();
    }

    /// <summary>Pack: each <c>.data.xml</c> under <c>Controls/</c> gives a code component, its manifest beside it.</summary>
    public static void Read(PackContext context)
    {
        var definitions = context.Unclaimed("Controls/").Where(k => k.EndsWith(".data.xml", StringComparison.Ordinal)).ToList();
        if (definitions.Count == 0) return;
        var container = context.Container("CustomControls");
        foreach (var dataPath in definitions)
        {
            var control = PackContext.FromRoot("CustomControl", context.Load(dataPath, "CustomControl", "the code components"));
            context.AddPayload(control, "FileName", dataPath[..^".data.xml".Length], dataPath);
            container.Add(control);
        }
    }
}
