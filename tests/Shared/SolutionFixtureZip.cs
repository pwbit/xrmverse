using System.IO.Compression;
using System.Xml.Linq;

namespace Xrmd.TestSupport;

/// <summary>
/// Builds <c>solution.zip</c> files from the hand-written fixture folder <c>tests/fixtures/solutions/XrmverseFixture</c>
/// (copied to the test output as <c>fixtures/solutions/</c>): every file of the folder, plus the net462
/// <c>Xrmd.TestPlugins.dll</c> of the test output at the path each <c>PluginAssembly/FileName</c> of
/// <c>customizations.xml</c> names. Linked into every test project that imports solutions (Xrmd.Solution.Tests,
/// Xrmd.Integration.Tests); the project must import <c>tests/TestPlugins.props</c> and copy the fixture folder.
/// </summary>
public static class SolutionFixtureZip
{
    /// <summary>The unique name of the fixture solution.</summary>
    public const string FixtureName = "XrmverseFixture";

    /// <summary>The fixture folder in the test output.</summary>
    public static string FixtureDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", FixtureName);

    /// <summary>The bytes of the net462 <c>Xrmd.TestPlugins.dll</c> copied next to the test assembly.</summary>
    public static byte[] TestPluginBytes => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.dll"));

    /// <summary>Reads a file of the fixture folder (<paramref name="relativePath"/> with <c>/</c> separators).</summary>
    public static byte[] ReadFixtureFile(string relativePath) =>
        File.ReadAllBytes(Path.Combine(FixtureDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// The fixture as a zip; <paramref name="customizations"/> and <paramref name="solution"/> may alter the two XML
    /// documents first (e.g. a failing step, a newer version), <paramref name="omit"/> leaves out entries by path.
    /// </summary>
    public static byte[] Build(Action<XDocument>? customizations = null, Action<XDocument>? solution = null,
        params string[] omit) => Build(customizations, solution, null, omit);

    /// <summary>
    /// The fixture as a zip with <paramref name="files"/> added (archive paths without a leading <c>/</c>, e.g. the formula files of
    /// calculated columns); <paramref name="customizations"/> may alter <c>customizations.xml</c> first, <paramref name="omit"/>
    /// leaves out entries by path.
    /// </summary>
    public static byte[] Build(Action<XDocument>? customizations, IReadOnlyDictionary<string, byte[]> files, params string[] omit) =>
        Build(customizations, null, files, omit);

    /// <summary>The fixture as a zip with <paramref name="files"/> added, and both XML documents altered first.</summary>
    public static byte[] BuildWithFiles(Action<XDocument>? customizations, Action<XDocument>? solution, IReadOnlyDictionary<string, byte[]> files,
        params string[] omit) => Build(customizations, solution, files, omit);

    private static byte[] Build(Action<XDocument>? customizations, Action<XDocument>? solution, IReadOnlyDictionary<string, byte[]>? files,
        string[] omit)
    {
        var customizationsXml = XDocument.Load(Path.Combine(FixtureDirectory, "customizations.xml"));
        customizations?.Invoke(customizationsXml);
        var solutionXml = XDocument.Load(Path.Combine(FixtureDirectory, "solution.xml"));
        solution?.Invoke(solutionXml);

        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(FixtureDirectory, "*", SearchOption.AllDirectories))
            entries[Path.GetRelativePath(FixtureDirectory, path).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllBytes(path);
        entries["customizations.xml"] = ToBytes(customizationsXml);
        entries["solution.xml"] = ToBytes(solutionXml);
        foreach (var fileName in customizationsXml.Descendants("PluginAssembly").Elements("FileName"))
            entries[fileName.Value.TrimStart('/')] = TestPluginBytes;
        foreach (var (path, content) in files ?? new Dictionary<string, byte[]>()) entries[path] = content;
        foreach (var name in omit) entries.Remove(name);

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>The overlay folder with the shapes of a real export (<c>tests/fixtures/solutions/RealExportShapes</c>).</summary>
    public static string RealShapesDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExportShapes");

    /// <summary>
    /// The archive entries of the overlay, keyed by their path in the archive (<c>RealExportShapes/entries/**</c>): the ones no
    /// xrmverse reader consumes, one custom API (<c>customapis/</c>, read since plan 15) and one environment variable
    /// (<c>environmentvariabledefinitions/</c>, read since #167).
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> RealShapesEntries()
    {
        var root = Path.Combine(RealShapesDirectory, "entries");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal);
    }

    /// <summary>
    /// The fixture with the shapes of a real export (<c>RealExportShapes/README.md</c>): the role's root component in the
    /// <c>parentId</c> form, the real <c>MissingDependencies</c> shapes and the entries of <see cref="RealShapesEntries"/>.
    /// </summary>
    public static byte[] BuildWithRealShapes(Action<XDocument>? customizations = null)
    {
        var overlay = XDocument.Load(Path.Combine(RealShapesDirectory, "manifest.xml")).Root!;
        var zip = Build(customizations, solution =>
        {
            var manifest = solution.Root!.Element("SolutionManifest")!;
            var parentId = (string)overlay.Element("RootComponent")!.Attribute("parentId")!;
            manifest.Element("RootComponents")!.Elements("RootComponent")
                .Single(r => (string?)r.Attribute("type") == "20" && string.Equals((string?)r.Attribute("id"), parentId, StringComparison.OrdinalIgnoreCase))
                .ReplaceWith(new XElement(overlay.Element("RootComponent")!));
            manifest.Element("MissingDependencies")!.ReplaceWith(new XElement(overlay.Element("MissingDependencies")!));
        });

        using var stream = new MemoryStream();
        stream.Write(zip);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach (var (name, content) in RealShapesEntries())
            {
                using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(content);
            }
        }
        return stream.ToArray();
    }

    /// <summary>The formula file of <c>xv_project</c> in the overlay (<c>RealExportShapes/entries</c>), as an archive path.</summary>
    public const string RealShapesFormulaPath = "Formulas/xv_project-FormulaDefinitions.yaml";

    /// <summary>
    /// Adds the formula column <c>xv_project.xv_label</c> (plan 2.12c: <c>SourceType</c> 3) whose formula is the line of the overlay's
    /// <see cref="RealShapesFormulaPath"/>, in the shape of the parity export's formula columns.
    /// </summary>
    public static void AddFormulaLabel(XDocument customizations) => AddFormulaColumn(customizations, "xv_Project", "xv_Label");

    /// <summary>
    /// Adds a String formula column <paramref name="schemaName"/> (<c>SourceType</c> 3, read-only, its <c>FormulaDefinitionFileName</c>
    /// the table's <c>Formulas/&lt;table&gt;-FormulaDefinitions.yaml</c>, last as the parity export writes it) to the table
    /// <paramref name="entityName"/> of the fixture: a copy of its primary name column with the names changed.
    /// </summary>
    public static void AddFormulaColumn(XDocument customizations, string entityName, string schemaName)
    {
        ArgumentNullException.ThrowIfNull(customizations);
        var attributes = customizations.Descendants("entity").Single(e => (string?)e.Attribute("Name") == entityName).Element("attributes")!;
        var column = new XElement(attributes.Elements("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Name"));
        var name = schemaName.ToLowerInvariant();
        column.SetAttributeValue("PhysicalName", schemaName);
        column.Element("Name")!.Value = name;
        column.Element("LogicalName")!.Value = name;
        column.Element("RequiredLevel")!.Value = "none";
        column.Element("DisplayMask")!.Value = "ValidForAdvancedFind|ValidForForm|ValidForGrid";
        column.Element("ValidForUpdateApi")!.Value = "0";
        column.Element("ValidForCreateApi")!.Value = "0";
        column.Element("IsAuditEnabled")!.Value = "0";
        column.Element("SourceType")!.Value = "3";
        foreach (var label in column.Element("displaynames")!.Elements("displayname")) label.SetAttributeValue("description", schemaName[3..]);
        column.Add(new XElement("FormulaDefinitionFileName", $"/Formulas/{entityName.ToLowerInvariant()}-FormulaDefinitions.yaml"));
        attributes.Add(column);
    }

    /// <summary>The ids of the two system charts <see cref="AddCharts"/> gives <c>xv_project</c> (plan 3.5 UI-1f).</summary>
    public static readonly Guid AmountChartId = Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e61"),
        CountChartId = Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e62");

    /// <summary>
    /// Adds <c>Visualizations</c> to <c>xv_Project</c> after its views, as an export places them (plan 3.5 UI-1f): a Bar chart of
    /// the sum of <c>xv_budget</c> by <c>xv_priority</c> and a default Pie chart of the count of projects by <c>statuscode</c>.
    /// Written by hand with neutral names (the shape of the plan's Appendix A).
    /// </summary>
    public static void AddCharts(XDocument customizations)
    {
        ArgumentNullException.ThrowIfNull(customizations);
        static XElement Chart(Guid id, string name, string description, string aggregate, string groupBy, string chartType, bool isDefault) =>
            new("visualization",
                new XElement("savedqueryvisualizationid", id.ToString("B").ToUpperInvariant()),
                new XElement("datadescription", new XElement("datadefinition",
                    new XElement("fetchcollection", new XElement("fetch", new XAttribute("mapping", "logical"), new XAttribute("aggregate", "true"),
                        new XElement("entity", new XAttribute("name", "xv_project"),
                            new XElement("attribute", new XAttribute("name", aggregate), new XAttribute("aggregate", aggregate == "xv_projectid" ? "count" : "sum"),
                                new XAttribute("alias", "aggregate_column")),
                            new XElement("attribute", new XAttribute("name", groupBy), new XAttribute("groupby", "true"), new XAttribute("alias", "groupby_column"))))),
                    new XElement("categorycollection", new XElement("category",
                        new XElement("measurecollection", new XElement("measure", new XAttribute("alias", "aggregate_column"))))))),
                new XElement("presentationdescription", new XElement("Chart", new XAttribute("Palette", "None"),
                    new XAttribute("PaletteCustomColors", "15,108,189; 0,120,212"),
                    new XElement("Series", new XElement("Series", new XAttribute("ChartType", chartType), new XAttribute("IsValueShownAsLabel", "True"))),
                    new XElement("ChartAreas", new XElement("ChartArea", new XElement("AxisY"), new XElement("AxisX"))))),
                new XElement("isdefault", isDefault ? "1" : "0"),
                new XElement("LocalizedNames",
                    new XElement("LocalizedName", new XAttribute("description", name), new XAttribute("languagecode", "1033")),
                    new XElement("LocalizedName", new XAttribute("description", name + " (it)"), new XAttribute("languagecode", "1040"))),
                new XElement("Descriptions",
                    new XElement("Description", new XAttribute("description", description), new XAttribute("languagecode", "1033"))),
                new XElement("IsCustomizable", "1"),
                new XElement("IntroducedVersion", "1.0.0.0"),
                new XElement("CanBeDeleted", "1"));

        var project = customizations.Descendants("Entity").Single(e => e.Element("Name")!.Value == "xv_Project");
        project.Element("SavedQueries")!.AddAfterSelf(new XElement("Visualizations",
            Chart(AmountChartId, "Budget by Priority", "Budget of the projects by priority", "xv_budget", "xv_priority", "Bar", false),
            Chart(CountChartId, "Projects by Status", "Projects by status reason", "xv_projectid", "statuscode", "Pie", true)));
    }

    /// <summary>The fixture's web resource (<c>xv_/scripts/project.js</c>) as an export writes an id: braces, upper case.</summary>
    public const string WebResourceIdAsWritten = "{6B1C3F4E-1A2B-4C3D-8E9F-0A1B2C3D4E41}";

    /// <summary>
    /// <see cref="AddCharts"/>, with the Pie chart naming the fixture's web resource in <c>webresourceid</c>, written as
    /// <see cref="WebResourceIdAsWritten"/> (upper case in braces, as a real export writes ids).
    /// </summary>
    public static void AddChartsWithWebResource(XDocument customizations)
    {
        AddCharts(customizations);
        var pie = customizations.Descendants("visualization")
            .Single(v => Guid.Parse(v.Element("savedqueryvisualizationid")!.Value) == CountChartId);
        pie.Element("isdefault")!.AddAfterSelf(new XElement("webresourceid", WebResourceIdAsWritten));
    }

    /// <summary>The ids of the two dashboards <see cref="AddDashboards"/> gives the fixture (plan 3.5 UI-1f).</summary>
    public static readonly Guid OverviewDashboardId = Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e71"),
        ChartsDashboardId = Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e72");

    /// <summary>
    /// Adds a root <c>Dashboards</c> after <c>optionsets</c>, where an export places it (plan 3.5 UI-1f, ruling F1): "Project
    /// Overview", a grid of the fixture's view of <c>xv_project</c>, and "Project Charts", a chart of <see cref="AmountChartId"/>
    /// over that view. Written by hand with neutral names, in the shape of a real export's <c>Dashboard</c> (ids upper case in braces).
    /// </summary>
    public static void AddDashboards(XDocument customizations)
    {
        ArgumentNullException.ThrowIfNull(customizations);
        static XElement Labels(string container, string element, string text) => new(container,
            new XElement(element, new XAttribute("description", text), new XAttribute("languagecode", "1033")),
            new XElement(element, new XAttribute("description", text + " (it)"), new XAttribute("languagecode", "1040")));
        // The id of a tab, section or cell of a dashboard: the dashboard's id with a tag in its last group.
        static string Part(Guid id, string tag) => $"{{{id.ToString("D")[..^4]}{tag}{id.ToString("D")[^2..]}}}";
        static XElement Dashboard(Guid id, string name, string description, string grid, bool chart) => new("Dashboard",
            Labels("LocalizedNames", "LocalizedName", name),
            Labels("Descriptions", "Description", description),
            new XElement("FormId", id.ToString("B").ToUpperInvariant()),
            new XElement("IsCustomizable", "1"),
            new XElement("IsDefault", "0"),
            new XElement("FormXml", new XElement("forms", new XAttribute("type", "dashboard"), new XElement("form",
                new XElement("tabs", new XElement("tab", new XAttribute("name", "main"), new XAttribute("id", Part(id, "a0")),
                    new XAttribute("showlabel", "false"), new XAttribute("verticallayout", "true"),
                    Labels("labels", "label", name),
                    new XElement("columns", new XElement("column", new XAttribute("width", "100%"), new XElement("sections",
                        new XElement("section", new XAttribute("name", "main"), new XAttribute("showlabel", "false"), new XAttribute("columns", "1"),
                            new XAttribute("id", Part(id, "b0")),
                            Labels("labels", "label", name),
                            new XElement("rows", new XElement("row", new XElement("cell", new XAttribute("id", Part(id, "c0")),
                                new XAttribute("showlabel", "true"), new XAttribute("rowspan", "12"), new XAttribute("colspan", "1"),
                                Labels("labels", "label", grid),
                                new XElement("control", new XAttribute("id", grid), new XAttribute("classid", "{E7A81278-8635-4d9e-8D4D-59480B391C5B}"),
                                    new XAttribute("indicationOfSubgrid", "true"),
                                    new XElement("parameters",
                                        new XElement("ViewId", "{6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e31}"),
                                        new XElement("IsUserView", "false"),
                                        new XElement("TargetEntityType", "xv_project"),
                                        new XElement("RecordsPerPage", "10"),
                                        new XElement("EnableViewPicker", "false"),
                                        new XElement("ChartGridMode", chart ? "Chart" : "Grid"),
                                        chart ? new XElement("VisualizationId", AmountChartId.ToString("B").ToUpperInvariant()) : null))))))))))),
                new XElement("Navigation")))),
            new XElement("IntroducedVersion", "1.0.0.0"),
            new XElement("IsTabletEnabled", "0"));

        customizations.Root!.Element("optionsets")!.AddAfterSelf(new XElement("Dashboards",
            Dashboard(OverviewDashboardId, "Project Overview", "The open projects", "Projects", chart: false),
            Dashboard(ChartsDashboardId, "Project Charts", "Budget of the projects", "Budget", chart: true)));
    }

    /// <summary>The two dashboards of <see cref="AddDashboards"/> as root components (type 60) of <c>solution.xml</c>, as an export lists them.</summary>
    public static void AddDashboardRoots(XDocument solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        var roots = solution.Descendants("RootComponents").Single();
        foreach (var id in new[] { OverviewDashboardId, ChartsDashboardId })
            roots.Add(new XElement("RootComponent", new XAttribute("type", "60"), new XAttribute("id", id.ToString("B")), new XAttribute("behavior", "0")));
    }

    private static byte[] ToBytes(XDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }
}
