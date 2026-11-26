namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>Dashboards (system forms not bound to a table): <c>Dashboards/&lt;form id&gt;.xml</c>.</summary>
internal static class DashboardWriter
{
    public static void Write(UnpackContext context)
    {
        var dashboards = context.Collection("Dashboards");
        if (dashboards is null) return;
        foreach (var dashboard in dashboards.Elements())
        {
            if (dashboard.Name != "Dashboard") throw UnpackRules.Unsupported(dashboard);
            var id = UnpackRules.Required(dashboard.Element("FormId"), "Dashboard/FormId").Value;
            context.Write($"Dashboards/{UnpackRules.SafeSegment(id, "dashboard id")}.xml", LayoutXml.RootFrom("Dashboard", dashboard));
        }
        dashboards.RemoveNodes();
    }

    /// <summary>Pack: one <c>Dashboard</c> per file.</summary>
    public static void Read(PackContext context)
    {
        var files = context.Unclaimed("Dashboards/").Where(k => !k["Dashboards/".Length..].Contains('/')).ToList();
        if (files.Count == 0) return;
        var container = context.Container("Dashboards");
        foreach (var path in files) container.Add(PackContext.FromRoot("Dashboard", context.Load(path, "Dashboard", "the dashboards")));
    }
}
