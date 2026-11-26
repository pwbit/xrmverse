using System.Globalization;
using System.Xml.Linq;

namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>
/// Model-driven apps: <c>AppModules/&lt;unique name&gt;/AppModule.xml</c> and their site maps in
/// <c>AppModuleSiteMaps/&lt;unique name&gt;/AppModuleSiteMap.xml</c>. The app file declares no <c>xsi</c> namespace, as the
/// reference folders show, unless its content uses it. The app's components are put in a stable order (by component type,
/// then by schema name; components named by id keep the order of the zip), as the reference folders show.
/// </summary>
internal static class AppWriter
{
    public static void Write(UnpackContext context)
    {
        var apps = context.Collection("AppModules");
        if (apps is not null)
        {
            foreach (var app in apps.Elements())
            {
                if (app.Name != "AppModule") throw UnpackRules.Unsupported(app);
                var name = UnpackRules.Required(app.Element("UniqueName"), "AppModule/UniqueName").Value;
                SortComponents(app.Element("AppModuleComponents"));
                context.Write($"AppModules/{UnpackRules.SafeSegment(name, "app name")}/AppModule.xml",
                    LayoutXml.RootFrom("AppModule", app, declareXsi: false));
            }
            apps.RemoveNodes();
        }

        var siteMaps = context.Collection("AppModuleSiteMaps");
        if (siteMaps is not null)
        {
            foreach (var siteMap in siteMaps.Elements())
            {
                if (siteMap.Name != "AppModuleSiteMap") throw UnpackRules.Unsupported(siteMap);
                var name = UnpackRules.Required(siteMap.Element("SiteMapUniqueName"), "AppModuleSiteMap/SiteMapUniqueName").Value;
                context.Write($"AppModuleSiteMaps/{UnpackRules.SafeSegment(name, "site map name")}/AppModuleSiteMap.xml",
                    LayoutXml.RootFrom("AppModuleSiteMap", siteMap));
            }
            siteMaps.RemoveNodes();
        }
    }

    private static void SortComponents(XElement? components)
    {
        if (components is null) return;
        var sorted = components.Elements()
            .OrderBy(c => int.TryParse((string?)c.Attribute("type"), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : int.MaxValue)
            .ThenBy(c => (string?)c.Attribute("schemaName") ?? "", StringComparer.Ordinal)
            .ToList();
        components.ReplaceNodes(sorted);
    }

    /// <summary>Pack: the apps and their site maps, one folder each.</summary>
    public static void Read(PackContext context)
    {
        var apps = context.Unclaimed("AppModules/").Where(k => k.EndsWith("/AppModule.xml", StringComparison.Ordinal) && PackContext.Rest(k, "AppModules/").Length == 2).ToList();
        if (apps.Count > 0)
        {
            var container = context.Container("AppModules");
            foreach (var path in apps) container.Add(PackContext.FromRoot("AppModule", context.Load(path, "AppModule", "the apps")));
        }

        var siteMaps = context.Unclaimed("AppModuleSiteMaps/").Where(k => k.EndsWith("/AppModuleSiteMap.xml", StringComparison.Ordinal) && PackContext.Rest(k, "AppModuleSiteMaps/").Length == 2).ToList();
        if (siteMaps.Count > 0)
        {
            var container = context.Container("AppModuleSiteMaps");
            foreach (var path in siteMaps)
                container.Add(PackContext.FromRoot("AppModuleSiteMap", context.Load(path, "AppModuleSiteMap", "the site maps")));
        }
    }
}
