namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>Global option sets (choices): each in <c>OptionSets/&lt;name&gt;.xml</c>, as the reference folders show.</summary>
internal static class OptionSetWriter
{
    public static void Write(UnpackContext context)
    {
        var optionSets = context.Collection("optionsets");
        if (optionSets is null) return;
        foreach (var optionSet in optionSets.Elements())
        {
            // Only the plain option set has a reference folder; any other kind of element here is refused, not guessed.
            if (optionSet.Name != "optionset") throw UnpackRules.Unsupported(optionSet);
            var name = UnpackRules.SafeSegment((string?)optionSet.Attribute("Name") ?? "", "option set name");
            context.Write($"OptionSets/{name}.xml", LayoutXml.RootFrom("optionset", optionSet));
        }
        optionSets.RemoveNodes();
    }

    /// <summary>Pack: one <c>optionset</c> per file.</summary>
    public static void Read(PackContext context)
    {
        var files = context.Unclaimed("OptionSets/").Where(k => !k["OptionSets/".Length..].Contains('/')).ToList();
        if (files.Count == 0) return;
        var container = context.Container("optionsets");
        foreach (var path in files) container.Add(PackContext.FromRoot("optionset", context.Load(path, "optionset", "the option sets")));
    }
}
