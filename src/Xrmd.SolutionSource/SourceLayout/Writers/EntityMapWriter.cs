using Xrmd.Core.Errors;

namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>
/// Entity maps (the column mappings of relationships): all of them in <c>Other/EntityMaps.xml</c>, in the order of the
/// solution, as the reference folders show. The file is written only when the solution has a map.
/// </summary>
internal static class EntityMapWriter
{
    private const string MapsFile = "Other/EntityMaps.xml";

    public static void Write(UnpackContext context)
    {
        var maps = context.Collection("EntityMaps");
        if (maps is null || !maps.HasElements) return;
        foreach (var map in maps.Elements())
        {
            // Only the plain entity map has a reference folder; any other element here is refused, not guessed.
            if (map.Name != "EntityMap") throw UnpackRules.Unsupported(map);
        }
        context.Write(MapsFile, LayoutXml.Root("EntityMaps", maps.Elements()));
        maps.RemoveNodes();
    }

    /// <summary>Pack: the maps of <c>Other/EntityMaps.xml</c> into the empty <c>EntityMaps</c> of the customizations.</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument when the customizations already hold maps of their own.</exception>
    public static void Read(PackContext context)
    {
        if (!context.Has(MapsFile)) return;
        var root = context.Load(MapsFile, "EntityMaps", "the entity maps");
        var container = context.Container("EntityMaps");
        if (container.HasElements)
            throw XrmdFaultException.InvalidArgument($"Entity maps are in both Other/Customizations.xml and {MapsFile}; keep them in {MapsFile} only.");
        container.Add(root.Elements());
    }
}
