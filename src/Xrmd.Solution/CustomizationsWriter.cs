using System.Globalization;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Security;

namespace Xrmd.Solution;

/// <summary>
/// Writes a <see cref="SolutionPackage"/> as the <c>solution.xml</c> and <c>customizations.xml</c> of an unmanaged solution
/// file, in the shape of the Dataverse exports that <see cref="CustomizationsReader"/> reads (<c>CustomizationsSolution.xsd</c>
/// 9.0.0.2090). It writes what the package holds and nothing it does not: what a file gave a component that xrmverse does not
/// model is written again in its place (<see cref="SolutionPackage.Unmodelled"/>, <see cref="UnmodelledXml"/>), a component
/// with nothing kept gets the SDK defaults (not verified), the components kept as XML (views, forms, site maps, app modules, the nodes of
/// unsupported components) are written as they are. Components are written in a fixed order (entities, relationships, option
/// sets, web resources by name, the one-to-many relationships before the many-to-many ones; roles by name; assemblies,
/// types, steps, views and forms by id).
/// </summary>
/// <remarks>
/// Written from memory of real exports, not verified against one (deferred to the step with a real solution): the element
/// order inside a component, the GUID formats (braces or not) of each element, the <c>FileName</c> of assemblies and web
/// resources, the empty ribbon of an entity, the <c>forms/@type</c> names, the attributes of the root of <c>solution.xml</c>
/// and the shape of an <c>AppModule</c> (written as the package keeps it).
/// </remarks>
public static class CustomizationsWriter
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>The root nodes of <c>customizations.xml</c>, in the order of an export; the unsupported ones are written empty
    /// unless the package keeps one with content, but <see cref="WrittenWhenPresent"/>.</summary>
    private static readonly string[] RootOrder =
    [
        "Entities", "Roles", "Workflows", "FieldSecurityProfiles", "Templates", "EntityMaps", "EntityRelationships",
        "OrganizationSettings", "optionsets", "Dashboards", "CustomControls", "SolutionPluginAssemblies", "SdkMessageProcessingSteps",
        "WebResources", "AppModuleSiteMaps", "AppModules", "Languages",
    ];

    /// <summary>
    /// The root nodes written only when the package has content for them: <c>Dashboards</c> (plan 3.5 UI-1f) after
    /// <c>optionsets</c>, where the OpenProject365 export has it; an export without dashboards (xrmdWfTest) has no node.
    /// </summary>
    private static readonly HashSet<string> WrittenWhenPresent = new(StringComparer.Ordinal) { "Dashboards" };

    /// <summary>The entity-level nodes written before the other kept ones, in the order of an export.</summary>
    private const string Ribbon = "RibbonDiffXml";
    private const string Visualizations = "Visualizations";

    /// <summary>The two documents of the package.</summary>
    /// <exception cref="XrmdFaultException">400 for content the format cannot carry (an attribute type xrmverse does not write).</exception>
    public static (XDocument Solution, XDocument Customizations) Write(SolutionPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var customizations = Customizations(package);
        return (Solution(package), new XDocument(new XDeclaration("1.0", "utf-8", null), customizations));
    }

    /// <summary>
    /// The files of the package by their path in the archive: each plug-in assembly at
    /// <c>PluginAssemblies/&lt;name without dots&gt;-&lt;ID&gt;/&lt;name without dots&gt;.dll</c>, each web resource at
    /// <c>WebResources/&lt;name&gt;&lt;ID&gt;</c> (the <c>FileName</c>s written in <c>customizations.xml</c>), and the
    /// entries an import kept unread (<see cref="SolutionPackage.KeptFiles"/>) at their own paths, and the files of the
    /// custom APIs (<see cref="CustomApiFiles"/>), of the environment variables (<see cref="EnvironmentVariableFiles"/>), of the
    /// custom controls (under the folder of each manifest), the XAML of each calculated or rollup column (<see cref="FormulaPath"/>) and
    /// the formula file of each table with formula columns (<see cref="FormulaFiles.PathOf"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> Files(SolutionPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        // The entries an import kept unread first: a file the export generates at the same path wins.
        foreach (var (path, content) in package.KeptFiles ?? new Dictionary<string, byte[]>()) files[path] = content;
        foreach (var assembly in package.Assemblies) files[AssemblyPath(assembly)] = assembly.Content;
        foreach (var resource in package.WebResources) files[WebResourcePath(resource)] = resource.Content;
        // The XAML of each process at the path its XamlFileName names, byte for byte as stored (plan 2.14, ruling W22).
        foreach (var workflow in package.Workflows) files[XamlPath(workflow)] = XamlEncoding.GetBytes(workflow.Xaml);
        foreach (var (path, content) in CustomApiFiles(package)) files[path] = content;
        foreach (var entity in package.Entities)
        {
            foreach (var attribute in (entity.Attributes ?? []).Where(a => IsComputed(a) && !IsFormula(a)))
                files[FormulaPath(entity.LogicalName, attribute.LogicalName)] = XamlEncoding.GetBytes(ComputedColumnRules.FormulaOf(attribute)!);
            // One formula file for the table's formula columns, in attribute order (plan 2.12c, ruling F3).
            var formulas = (entity.Attributes ?? []).Where(IsFormula).Select(a => (a.LogicalName, ComputedColumnRules.FormulaOf(a)!)).ToList();
            if (formulas.Count > 0) files[FormulaFiles.PathOf(entity.LogicalName)] = XamlEncoding.GetBytes(FormulaFiles.Write(formulas));
        }
        foreach (var (path, content) in EnvironmentVariableFiles(package)) files[path] = content;
        // The files of each custom control under its manifest's folder, byte for byte (plan 3.5 UI-1d, R4).
        foreach (var control in package.CustomControls)
            foreach (var (path, content) in control.Files)
                files[control.Folder.Length == 0 ? path : $"{control.Folder}/{path}"] = content;
        return files;
    }

    /// <summary>
    /// The calculated or rollup money column of <paramref name="entity"/> whose <c>_base</c> companion <paramref name="attribute"/> is;
    /// null otherwise.
    /// </summary>
    private static AttributeMetadata? ComputedBaseOf(EntityMetadata entity, AttributeMetadata attribute) =>
        attribute is MoneyAttributeMetadata { IsBaseCurrency: true } && attribute.LogicalName.EndsWith(TypeMapping.BaseColumnSuffix, StringComparison.Ordinal)
            ? (entity.Attributes ?? []).FirstOrDefault(a => a is MoneyAttributeMetadata && IsComputed(a)
                && a.LogicalName + TypeMapping.BaseColumnSuffix == attribute.LogicalName)
            : null;

    /// <summary>
    /// A calculated (plan 2.12a: <c>SourceType</c> 1), rollup (plan 2.12b: <c>SourceType</c> 2, not its <c>_base</c>) or formula (plan
    /// 2.12c: <c>SourceType</c> 3) column with its definition (<c>FormulaDefinition</c>).
    /// </summary>
    private static bool IsComputed(AttributeMetadata attribute) =>
        (ComputedColumnRules.IsComputed(attribute) || RollupColumns.IsRollup(attribute))
        && ComputedColumnRules.FormulaOf(attribute) is not null;

    /// <summary>A formula column (plan 2.12c: <c>SourceType</c> 3) with its formula: its file is the table's <see cref="FormulaFiles.PathOf"/>.</summary>
    private static bool IsFormula(AttributeMetadata attribute) =>
        attribute.SourceType == ComputedColumnRules.Formula && ComputedColumnRules.FormulaOf(attribute) is not null;

    /// <summary>
    /// True for a companion the server generates with a rollup column of <paramref name="attributes"/> (<c>_date</c>, <c>_state</c>,
    /// plan 2.12b, ruling R14): an export writes none, an import has the server generate them.
    /// </summary>
    private static bool IsRollupCompanion(List<AttributeMetadata> attributes, AttributeMetadata a) =>
        RollupColumns.IsCompanion(a) && attributes.Any(m => m.LogicalName == a.AttributeOf && RollupColumns.IsRollup(m));

    /// <summary>
    /// The archive path (no leading <c>/</c>) of a calculated or rollup column's XAML: <c>Formulas/&lt;entity&gt;-&lt;column&gt;.xaml</c> (ruling
    /// K13, assumed until the parity export). The text is written as it was read (UTF-8, its byte order mark kept as a character).
    /// </summary>
    private static string FormulaPath(string entity, string attribute) => $"Formulas/{entity}-{attribute}.xaml";

    private static string AssemblyPath(PackageAssembly assembly)
    {
        var name = assembly.Name.Replace(".", "", StringComparison.Ordinal);
        return $"PluginAssemblies/{name}-{Upper(assembly.Id)}/{name}.dll";
    }

    private static string WebResourcePath(PackageWebResource resource) => $"WebResources/{resource.Name}{Upper(resource.Id)}";

    /// <summary>The archive path of a process's XAML: its <c>XamlFileName</c> without the leading <c>/</c>.</summary>
    private static string XamlPath(PackageWorkflow workflow) => workflow.XamlPath.Trim().Replace('\\', '/').TrimStart('/');

    /// <summary>UTF-8 without a byte order mark of its own: a stored XAML that starts with one keeps it as its first character.</summary>
    private static readonly System.Text.UTF8Encoding XamlEncoding = new(false);

    // ---- solution.xml -----------------------------------------------------------------------------------------------

    private static XDocument Solution(SolutionPackage package)
    {
        var language = package.Solution.LanguageCode;
        var manifest = new XElement("SolutionManifest",
            new XElement("UniqueName", package.Solution.UniqueName),
            KeptXml.Labels("LocalizedNames", "LocalizedName", package.Solution.FriendlyName),
            KeptXml.Labels("Descriptions", "Description", package.Solution.Description),
            new XElement("Version", package.Solution.Version),
            new XElement("Managed", package.Solution.Managed ? "1" : "0"),
            Publisher(package.Publisher),
            new XElement("RootComponents", package.RootComponents.Select(root => RootComponent(package, root))),
            new XElement("MissingDependencies", (package.MissingDependencies ?? []).Select(d => new XElement("MissingDependency",
                DependencyNode("Required", d.Required), DependencyNode("Dependent", d.Dependent)))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("ImportExportXml",
                new XAttribute("version", "9.2.0.0"),
                new XAttribute("SolutionPackageVersion", "9.2"),
                new XAttribute("languagecode", Text(language)),
                new XAttribute("generatedBy", "xrmverse"),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                manifest));
    }

    /// <summary>A side of a <c>MissingDependency</c> (shape from memory, not verified on a real export).</summary>
    private static XElement DependencyNode(string name, PackageDependencyNode node) => new(name,
        new XAttribute("type", Text(node.Type)),
        new XAttribute("schemaName", node.SchemaName),
        node.DisplayName is null ? null : new XAttribute("displayName", node.DisplayName),
        node.Solution is null ? null : new XAttribute("solution", node.Solution),
        node.ParentSchemaName is null ? null : new XAttribute("parentSchemaName", node.ParentSchemaName),
        node.ParentDisplayName is null ? null : new XAttribute("parentDisplayName", node.ParentDisplayName));

    /// <summary>The publisher: the file's element when the package keeps it (its details), with the values xrmverse keeps set.</summary>
    private static XElement Publisher(PackagePublisher publisher)
    {
        if (publisher.Xml is { } kept)
        {
            var element = new XElement(kept);
            Set(element, "UniqueName", publisher.UniqueName);
            Replace(element, KeptXml.Labels("LocalizedNames", "LocalizedName", publisher.FriendlyName));
            Set(element, "CustomizationPrefix", publisher.CustomizationPrefix);
            Set(element, "CustomizationOptionValuePrefix", Text(publisher.CustomizationOptionValuePrefix));
            return element;
        }
        return new XElement("Publisher",
            new XElement("UniqueName", publisher.UniqueName),
            KeptXml.Labels("LocalizedNames", "LocalizedName", publisher.FriendlyName),
            new XElement("Descriptions"),
            Nil("EMailAddress"),
            Nil("SupportingWebsiteUrl"),
            new XElement("CustomizationPrefix", publisher.CustomizationPrefix),
            new XElement("CustomizationOptionValuePrefix", Text(publisher.CustomizationOptionValuePrefix)));
    }

    private static XElement RootComponent(SolutionPackage package, (int Type, string SchemaNameOrId, int Behavior) root)
    {
        var element = new XElement("RootComponent", new XAttribute("type", Text(root.Type)));
        if (Guid.TryParse(root.SchemaNameOrId, out var id))
        {
            element.Add(new XAttribute("id", Braced(id)));
            if (root.Type == ComponentType.PluginAssembly && package.Assemblies.FirstOrDefault(a => a.Id == id) is { } assembly)
                element.Add(new XAttribute("schemaName", assembly.FullName));
        }
        else element.Add(new XAttribute("schemaName", root.SchemaNameOrId));
        element.Add(new XAttribute("behavior", Text(root.Behavior)));
        return element;
    }

    // ---- customizations.xml -----------------------------------------------------------------------------------------

    private static XElement Customizations(SolutionPackage package)
    {
        var nodes = new Dictionary<string, XElement>(StringComparer.Ordinal)
        {
            ["Entities"] = new("Entities", package.Entities.OrderBy(e => e.LogicalName, StringComparer.Ordinal).Select(e => Entity(package, e))),
            ["Roles"] = new("Roles", package.Roles.OrderBy(r => r.Name, StringComparer.Ordinal).Select(Role)),
            ["EntityRelationships"] = new("EntityRelationships", package.Relationships
                .OrderBy(r => r is ManyToManyRelationshipMetadata ? 1 : 0)
                .ThenBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase).Select(r => UnmodelledXml.Merge(Relationship(package, r), Kept(package, UnmodelledXml.RelationshipKey(r.SchemaName)),
                    UnmodelledXml.RelationshipShape, $"relationship '{r.SchemaName}'"))),
            ["optionsets"] = new("optionsets", package.OptionSets.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .Select(o => UnmodelledXml.Merge(OptionSet(o, global: true, null), Kept(package, UnmodelledXml.OptionSetKey(o.Name ?? "")),
                    UnmodelledXml.OptionSetShape, $"option set '{o.Name}'"))),
            ["SolutionPluginAssemblies"] = new("SolutionPluginAssemblies", package.Assemblies.OrderBy(a => a.Id.ToString("D"), StringComparer.Ordinal)
                .Select(a => UnmodelledXml.Merge(Assembly(a), Kept(package, UnmodelledXml.AssemblyKey(a.Id)), UnmodelledXml.AssemblyShape,
                    $"plug-in assembly '{a.FullName}'"))),
            ["SdkMessageProcessingSteps"] = new("SdkMessageProcessingSteps", package.Steps.OrderBy(s => s.Id.ToString("D"), StringComparer.Ordinal)
                .Select(s => UnmodelledXml.Merge(Step(s), Kept(package, UnmodelledXml.StepKey(s.Id)), UnmodelledXml.StepShape, $"step '{s.Name}'"))),
            ["WebResources"] = new("WebResources", package.WebResources.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).Select(w => UnmodelledXml.Merge(WebResource(w), Kept(package, UnmodelledXml.WebResourceKey(w.Id)), UnmodelledXml.WebResourceShape,
                    $"web resource '{w.Name}'"))),
            ["AppModuleSiteMaps"] = new("AppModuleSiteMaps", Opaque(package, ComponentType.SiteMap)),
            ["AppModules"] = new("AppModules", Opaque(package, ComponentType.AppModule)),
        };
        // The system dashboards (plan 3.5 UI-1f, ruling F1): the forms of no table, each its kept Dashboard element, by id (as the
        // forms of an entity; a Known limit: a real export's order is not known).
        var dashboards = package.Opaque.Where(o => o.Type == ComponentType.SystemForm && o.EntityLogicalName is null)
            .OrderBy(o => o.Id, StringComparer.OrdinalIgnoreCase).Select(o => new XElement(o.Xml)).ToList();
        // An import from before UI-1f kept the node as unsupported XML: its dashboards that are no row of the package stay.
        var keptDashboards = package.Unsupported.FirstOrDefault(u => u.Name.LocalName == "Dashboards");
        if (dashboards.Count > 0)
        {
            var ids = dashboards.Select(d => Guid.TryParse((string?)d.Element("FormId"), out var id) ? id : Guid.Empty).ToHashSet();
            nodes["Dashboards"] = new("Dashboards", dashboards.Concat((keptDashboards?.Elements("Dashboard") ?? [])
                .Where(d => !Guid.TryParse((string?)d.Element("FormId"), out var id) || !ids.Contains(id)).Select(d => new XElement(d))));
        }
        // The custom controls (plan 3.5 UI-1d, R4), written only when the package has some: an older import kept the node as
        // unsupported XML.
        if (package.CustomControls.Count > 0)
            nodes["CustomControls"] = new("CustomControls", package.CustomControls.OrderBy(c => c.Name, StringComparer.Ordinal).Select(CustomControl));
        // Written only when the package has profiles: an older import may have kept the node as unsupported XML.
        if (package.FieldSecurityProfiles is { Count: > 0 } profiles)
            nodes["FieldSecurityProfiles"] = new("FieldSecurityProfiles",
                profiles.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id).Select(FieldSecurityProfile));
        // The processes (plan 2.14, ruling W22), with the kept Workflow elements of the categories xrmverse does not run.
        var keptWorkflows = package.Unsupported.FirstOrDefault(u => u.Name.LocalName == "Workflows");
        if (package.Workflows.Count > 0)
            nodes["Workflows"] = Workflows(package.Workflows, keptWorkflows);
        foreach (var kept in package.Unsupported.Where(u => !(ReferenceEquals(u, keptWorkflows) && package.Workflows.Count > 0)
                                                           && !(ReferenceEquals(u, keptDashboards) && dashboards.Count > 0)))
        {
            // A kept node never replaces one xrmverse writes (as UnmodelledXml.Merge): it was not kept from a solution file.
            if (nodes.ContainsKey(kept.Name.LocalName) || kept.Name.LocalName == "Languages")
                throw new XrmdFaultException(ErrorCodes.Unexpected,
                    $"The kept XML of the solution carries the root node '{kept.Name.LocalName}', which xrmverse writes itself: it was not kept from a solution file and is not exported.",
                    500);
            nodes[kept.Name.LocalName] = new XElement(kept);
        }

        var root = new XElement("ImportExportXml", new XAttribute(XNamespace.Xmlns + "xsi", Xsi));
        foreach (var name in RootOrder.Where(n => n != "Languages"))
            if (nodes.GetValueOrDefault(name) is { } node) root.Add(node);
            else if (!WrittenWhenPresent.Contains(name)) root.Add(new XElement(name));
        // A kept node of a component the export order does not name goes before the languages, as it was kept.
        foreach (var kept in package.Unsupported.Where(u => !RootOrder.Contains(u.Name.LocalName, StringComparer.Ordinal)))
            root.Add(nodes[kept.Name.LocalName]);

        // The languages of the file: its base language and every language of a label written.
        var languages = root.Descendants().Attributes("languagecode").Select(a => a.Value)
            .Append(Text(package.Solution.LanguageCode))
            .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : (int?)null)
            .OfType<int>().Distinct().Order();
        root.Add(new XElement("Languages", languages.Select(code => new XElement("Language", Text(code)))));
        return root;
    }

    // ---- processes --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>Workflows</c> node: the kept elements first (the processes of a category xrmverse does not run, as they were imported;
    /// one with the id of a process of the package gives way to it), then the processes by name and id.
    /// </summary>
    private static XElement Workflows(IReadOnlyList<PackageWorkflow> workflows, XElement? kept)
    {
        var ids = workflows.Select(w => w.Id).ToHashSet();
        var node = new XElement("Workflows");
        foreach (var element in kept?.Elements() ?? [])
            if (!(element.Name.LocalName == "Workflow" && Guid.TryParse((string?)element.Attribute("WorkflowId"), out var id) && ids.Contains(id)))
                node.Add(new XElement(element));
        node.Add(workflows.OrderBy(w => w.Name, StringComparer.Ordinal).ThenBy(w => w.Id).Select(Workflow));
        return node;
    }

    /// <summary>
    /// A process: its <see cref="PackageWorkflow.Source"/> with the values the package holds set in it (an element whose value is
    /// already the one held is left untouched, so an imported process is written as it was read; a value without an element is
    /// added at the end), and its <c>ProcessTriggers</c>.
    /// </summary>
    private static XElement Workflow(PackageWorkflow workflow)
    {
        var element = new XElement(workflow.Source);
        if (!Guid.TryParse((string?)element.Attribute("WorkflowId"), out var id) || id != workflow.Id)
            element.SetAttributeValue("WorkflowId", Braced(workflow.Id));
        if ((string?)element.Attribute("Name") != workflow.Name) element.SetAttributeValue("Name", workflow.Name);
        SetValue(element, "XamlFileName", workflow.XamlPath);
        SetValue(element, "Category", Text(workflow.Category));
        SetValue(element, "Mode", Text(workflow.Mode));
        SetValue(element, "Scope", Text(workflow.Scope));
        // The row holds the logical name in lower case: the file's spelling is kept when only the case differs.
        if (workflow.PrimaryEntity is { } primary) SetValue(element, "PrimaryEntity", primary, StringComparison.OrdinalIgnoreCase);
        SetValue(element, "StateCode", Text(workflow.StateCode));
        foreach (var (name, value) in workflow.Fields) SetValue(element, name, value);

        var triggers = element.Elements().FirstOrDefault(e => e.Name.LocalName == "ProcessTriggers");
        if (workflow.ProcessTriggers.Count > 0 || triggers?.HasElements == true)
        {
            var written = workflow.ProcessTriggers.Select(t => new XElement(t)).ToList();
            if (triggers is null) element.Add(new XElement("ProcessTriggers", written));
            else if (!triggers.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting))
                         .SequenceEqual(written.Select(e => e.ToString(SaveOptions.DisableFormatting))))
            {
                triggers.RemoveNodes();
                triggers.Add(written);
            }
        }
        return element;
    }

    /// <summary>
    /// Sets the text of the child <paramref name="name"/> (found ignoring case) of a process to <paramref name="value"/> when it differs
    /// (an empty or <c>xsi:nil</c> element counts as the empty text; compared with <paramref name="comparison"/>); adds the child at the
    /// end when there is none.
    /// </summary>
    private static void SetValue(XElement workflow, string name, string value, StringComparison comparison = StringComparison.Ordinal)
    {
        var child = workflow.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (child is null)
        {
            workflow.Add(new XElement(name, value));
            return;
        }
        var current = CustomizationsReader.IsNil(child) ? "" : child.Value;
        if (string.Equals(current, value, comparison)) return;
        child.RemoveAttributes();
        child.Value = value;
    }

    private static IEnumerable<XElement> Opaque(SolutionPackage package, int type) =>
        package.Opaque.Where(o => o.Type == type).OrderBy(o => o.Id, StringComparer.OrdinalIgnoreCase).Select(o => new XElement(o.Xml));

    // ---- entities ---------------------------------------------------------------------------------------------------

    private static XElement Entity(SolutionPackage package, EntityMetadata entity)
    {
        var displayName = Base(entity.DisplayName) ?? entity.SchemaName ?? entity.LogicalName;
        var attributes = (entity.Attributes ?? []).Cast<AttributeMetadata>()
            .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName))
            .ToList();
        // The export order: the primary key, the system attributes by name, then the custom ones as the entity has them.
        // The companions of the file and image columns are the server's: an import creates them with their main column.
        // So are the _date and _state of a rollup column (plan 2.12b, ruling R14).
        var companions = attributes.Where(a => IsFileCompanion(attributes, a) || IsRollupCompanion(attributes, a)).ToHashSet();
        attributes.RemoveAll(companions.Contains);
        var ordered = attributes.Where(a => a.IsPrimaryId == true)
            .Concat(attributes.Where(a => a.IsPrimaryId != true && a.IsCustomAttribute != true)
                .OrderBy(a => a.LogicalName, StringComparer.Ordinal))
            .Concat(attributes.Where(a => a.IsPrimaryId != true && a.IsCustomAttribute == true));

        var audit = entity.IsAuditEnabled ?? AuditDefaults.ForEntity(entity.LogicalName, MetadataStore.IsSystemEntity(entity));
        var keptInfo = Kept(package, UnmodelledXml.EntityKey(entity.LogicalName));
        var written = new XElement("entity", new XAttribute("Name", entity.SchemaName ?? entity.LogicalName),
            KeptXml.Labels("LocalizedNames", "LocalizedName", entity.DisplayName),
            KeptXml.Labels("LocalizedCollectionNames", "LocalizedCollectionName", entity.DisplayCollectionName),
            KeptXml.Labels("Descriptions", "Description", entity.Description),
            new XElement("attributes", ordered.Select(a => Attribute(package, entity, a))),
            Optional("EntitySetName", entity.EntitySetName),
            // Duplicate detection (plan 2.15, ruling D18): the table flag.
            new XElement("IsDuplicateCheckSupported", Bit(entity.IsDuplicateDetectionEnabled?.Value == true)),
            new XElement("AutoCreateAccessTeams", Bit(entity.AutoCreateAccessTeams == true)),
            Optional("OwnershipTypeMask", entity.OwnershipType switch
            {
                null => null,
                OwnershipTypes.UserOwned => "UserOwned",
                OwnershipTypes.TeamOwned => "TeamOwned",
                OwnershipTypes.OrganizationOwned => "OrgOwned",
                OwnershipTypes.BusinessOwned => "BusinessOwned",
                OwnershipTypes.BusinessParented => "BusinessParented",
                _ => "None",
            }),
            new XElement("IsActivity", Bit(entity.IsActivity == true)),
            // Plan 2.11 (A19): an activity's own value, empty for any other table (as real exports write it).
            new XElement("ActivityTypeMask", entity.IsActivity == true && entity.ActivityTypeMask is { } mask ? Text(mask) : ""),
            new XElement("IsAuditEnabled", Bit(audit.Value)),
            new XElement("CanModifyAuditSettings", Bit(audit.CanBeChanged)),
            // Change tracking (plan 2.13): the flag, and whether the table can have it (the exporter's computed value, else the rule).
            new XElement("ChangeTrackingEnabled", Bit(entity.ChangeTrackingEnabled == true)),
            new XElement("CanChangeTrackingBeEnabled", Bit(entity.CanChangeTrackingBeEnabled?.Value ?? ChangeTrackingSchema.CanBeEnabled(entity))),
            // Plan 2.11 (A5, A19): the table's notes and activities from the metadata. A real export writes them only when True
            // (tests/fixtures/solutions/RealExports); False is written where the file had the element, or for a table created in
            // xrmverse (the defaults have its place).
            Flag("HasRelatedNotes", entity.HasNotes == true, keptInfo),
            Flag("HasRelatedActivities", entity.HasActivities == true, keptInfo));
        // A flag switched on that the file did not have goes last, where a real export writes it (notes, then activities).
        var placed = keptInfo;
        foreach (var (flag, on) in new[] { ("HasRelatedNotes", entity.HasNotes == true), ("HasRelatedActivities", entity.HasActivities == true) })
            if (on && placed is not null && placed.Element(flag) is null) placed = UnmodelledXml.WithMarkerAtEnd(placed, flag);
        var info = UnmodelledXml.Merge(written, placed, UnmodelledXml.EntityShape,
            $"entity '{entity.LogicalName}'");

        var element = new XElement("Entity",
            new XElement("Name", new XAttribute("LocalizedName", displayName), new XAttribute("OriginalName", displayName),
                entity.SchemaName ?? entity.LogicalName),
            new XElement("EntityInfo", info));

        var forms = package.Opaque.Where(o => o.Type == ComponentType.SystemForm && o.EntityLogicalName == entity.LogicalName).ToList();
        if (forms.Count > 0)
            element.Add(new XElement("FormXml", forms.GroupBy(f => f.FormType ?? "main", StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new XElement("forms", new XAttribute("type", g.Key),
                    g.OrderBy(f => f.Id, StringComparer.OrdinalIgnoreCase).Select(f => new XElement(f.Xml))))));
        var views = package.Opaque.Where(o => o.Type == ComponentType.SavedQuery && o.EntityLogicalName == entity.LogicalName).ToList();
        if (views.Count > 0)
            element.Add(new XElement("SavedQueries", new XElement("savedqueries",
                views.OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase).Select(v => new XElement(v.Xml)))));
        var kept = package.EntityUnsupported.Where(u => u.EntityLogicalName == entity.LogicalName).Select(u => u.Xml).ToList();
        // The system charts (plan 3.5 UI-1f) after the views, as a real export places them. An import from before UI-1f kept the
        // node as unsupported XML: its charts that are no row of the package stay, after the rows (as the kept dashboards).
        var charts = package.Opaque.Where(o => o.Type == ComponentType.SavedQueryVisualization && o.EntityLogicalName == entity.LogicalName)
            .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Select(c => new XElement(c.Xml)).ToList();
        var keptCharts = kept.FirstOrDefault(k => k.Name.LocalName == Visualizations);
        if (charts.Count > 0)
        {
            var ids = charts.Select(ChartId).ToHashSet();
            element.Add(new XElement(Visualizations, charts.Concat((keptCharts?.Elements("visualization") ?? [])
                .Where(v => ChartId(v) is not { } id || !ids.Contains(id)).Select(v => new XElement(v)))));
        }
        else if (keptCharts is not null)
            element.Add(new XElement(keptCharts));

        element.Add(kept.FirstOrDefault(k => k.Name.LocalName == Ribbon) is { } ribbon ? new XElement(ribbon) : EmptyRibbon());
        foreach (var node in kept.Where(k => k.Name.LocalName != Ribbon && k.Name.LocalName != Visualizations)) element.Add(new XElement(node));
        return element;
    }

    /// <summary>The <c>savedqueryvisualizationid</c> of a <c>visualization</c> element; null when it has none or it is no Guid.</summary>
    private static Guid? ChartId(XElement visualization) =>
        Guid.TryParse((string?)visualization.Element("savedqueryvisualizationid"), out var id) ? id : null;

    /// <summary>
    /// The table setting <paramref name="name"/> (<c>True</c>/<c>False</c>): written when on, and when off only where
    /// <paramref name="kept"/> has its place (null: nothing kept, the defaults have it).
    /// </summary>
    private static XElement? Flag(string name, bool on, XElement? kept) =>
        on || kept is null || kept.Element(name) is not null ? new XElement(name, on ? "True" : "False") : null;

    /// <summary>
    /// True for a companion of a file or image column of <paramref name="attributes"/> (<see cref="FileColumns.Companions"/>):
    /// <c>AttributeOf</c> a file or image column, with the name and type the server gives it.
    /// </summary>
    private static bool IsFileCompanion(List<AttributeMetadata> attributes, AttributeMetadata a) =>
        a.AttributeOf is { } of && attributes.FirstOrDefault(m => m.LogicalName == of) is { } main && FileColumns.IsFileOrImage(main)
        && FileColumns.Companions(MetadataSerializer.Clone(main)).Any(c => c.LogicalName == a.LogicalName && c.GetType() == a.GetType());

    /// <summary>What the package keeps of the component <paramref name="key"/> (<see cref="SolutionPackage.Unmodelled"/>).</summary>
    private static XElement? Kept(SolutionPackage package, (int, string) key) => package.Unmodelled?.GetValueOrDefault(key);

    /// <summary>The ribbon of an entity without ribbon customizations, as an export writes it.</summary>
    private static XElement EmptyRibbon() => new(Ribbon,
        new XElement("CustomActions"),
        new XElement("Templates", new XElement("RibbonTemplates", new XAttribute("Id", "Mscrm.Templates"))),
        new XElement("CommandDefinitions"),
        new XElement("RuleDefinitions", new XElement("TabDisplayRules"), new XElement("DisplayRules"), new XElement("EnableRules")),
        new XElement("LocLabels"));

    private static XElement Attribute(SolutionPackage package, EntityMetadata entity, AttributeMetadata attribute)
    {
        var where = $"{entity.LogicalName}.{attribute.LogicalName}";
        var kept = Kept(package, UnmodelledXml.AttributeKey(entity.LogicalName, attribute.LogicalName));
        var computedBase = ComputedBaseOf(entity, attribute);
        var element = new XElement("attribute", new XAttribute("PhysicalName", attribute.SchemaName ?? attribute.LogicalName),
            new XElement("Type", TypeName(attribute, where)),
            new XElement("Name", attribute.LogicalName),
            new XElement("LogicalName", attribute.LogicalName),
            new XElement("RequiredLevel", (attribute.RequiredLevel?.Value ?? AttributeRequiredLevel.None) switch
            {
                AttributeRequiredLevel.SystemRequired => "systemrequired",
                AttributeRequiredLevel.ApplicationRequired => "required",
                AttributeRequiredLevel.Recommended => "recommended",
                _ => "none",
            }));
        element.Add(
            // Not set means valid: xrmverse refuses a value only for an attribute marked not valid.
            new XElement("ValidForUpdateApi", Bit(attribute.IsValidForUpdate != false)),
            new XElement("ValidForCreateApi", Bit(attribute.IsValidForCreate != false)),
            new XElement("IsCustomField", Bit(attribute.IsCustomAttribute == true)),
            new XElement("IsAuditEnabled", Bit((attribute.IsAuditEnabled ?? AuditDefaults.ForAttribute(attribute, entity.PrimaryIdAttribute)).Value)),
            new XElement("IsSecured", Bit(attribute.IsSecured == true)),
            // A calculated (plan 2.12a, ruling K13), rollup (plan 2.12b, ruling R14) or formula (plan 2.12c, ruling F3) column: its
            // SourceType and the file of its XAML or of its table's formulas (Files writes it). The _base of a calculated or rollup
            // Currency column is written with the SourceType of its money column and no file (parity export).
            new XElement("SourceType", IsComputed(computedBase ?? attribute) ? Text((computedBase ?? attribute).SourceType!.Value) : "0"),
            IsComputed(attribute)
                ? new XElement("FormulaDefinitionFileName",
                    "/" + (IsFormula(attribute) ? FormulaFiles.PathOf(entity.LogicalName) : FormulaPath(entity.LogicalName, attribute.LogicalName)))
                : null,
            new XElement("AutoNumberFormat", attribute.AutoNumberFormat ?? ""));
        element.Add(TypeSpecific(entity, attribute));
        // ... and CalculationOf, the SchemaName of its money column, after the type's elements (parity export), unless the kept XML of
        // an imported _base has it already.
        if (computedBase is not null && kept?.Element("CalculationOf") is null)
            element.Add(new XElement("CalculationOf", computedBase.SchemaName ?? computedBase.LogicalName));
        element.Add(KeptXml.Labels("displaynames", "displayname", attribute.DisplayName), KeptXml.Labels("Descriptions", "Description", attribute.Description));
        // The kind of the attribute, for the defaults of one with nothing kept (its DisplayMask).
        if (attribute.IsPrimaryId == true) element.SetAttributeValue(UnmodelledXml.Namespace + "kind", "primarykey");
        else if (attribute.IsPrimaryName == true || attribute.LogicalName == entity.PrimaryNameAttribute)
            element.SetAttributeValue(UnmodelledXml.Namespace + "kind", "primaryname");
        return UnmodelledXml.Merge(element, kept, UnmodelledXml.AttributeShape, $"attribute '{entity.LogicalName}.{attribute.LogicalName}'");
    }

    private static string TypeName(AttributeMetadata attribute, string where) => attribute switch
    {
        UniqueIdentifierAttributeMetadata when attribute.IsPrimaryId == true => "primarykey",
        UniqueIdentifierAttributeMetadata => "uniqueidentifier",
        StringAttributeMetadata => "nvarchar",
        MemoAttributeMetadata => "ntext",
        IntegerAttributeMetadata => "int",
        BigIntAttributeMetadata => "bigint",
        DecimalAttributeMetadata => "decimal",
        DoubleAttributeMetadata => "float",
        MoneyAttributeMetadata => "money",
        BooleanAttributeMetadata => "bit",
        DateTimeAttributeMetadata => "datetime",
        MultiSelectPicklistAttributeMetadata => "multiselectpicklist",
        PicklistAttributeMetadata => "picklist",
        StateAttributeMetadata => "state",
        StatusAttributeMetadata => "status",
        LookupAttributeMetadata when attribute.AttributeType == AttributeTypeCode.Owner => "owner",
        // Plan 2.11: the party lists and EntityName columns of an activity (system columns; the type names are from memory: checklist).
        LookupAttributeMetadata when attribute.AttributeType == AttributeTypeCode.PartyList => "partylist",
        EntityNameAttributeMetadata => "entityname",
        LookupAttributeMetadata when attribute.AttributeType == AttributeTypeCode.Customer => "customer",
        LookupAttributeMetadata => "lookup",
        FileAttributeMetadata => "file",
        ImageAttributeMetadata => "image",
        _ => throw XrmdFaultException.InvalidArgument(
            $"Attribute '{where}' has the type '{attribute.AttributeType}', which xrmverse cannot write in a solution file."),
    };

    private static IEnumerable<XElement?> TypeSpecific(EntityMetadata entity, AttributeMetadata attribute)
    {
        switch (attribute)
        {
            case StringAttributeMetadata s:
                yield return Optional("Format", s.Format?.ToString().ToLowerInvariant());
                if (s.MaxLength is { } length)
                {
                    yield return new XElement("MaxLength", Text(length));
                    yield return new XElement("Length", Text(length * 2)); // the column's size in bytes (nvarchar)
                }
                break;
            case MemoAttributeMetadata m:
                yield return Optional("Format", m.Format?.ToString().ToLowerInvariant());
                if (m.MaxLength is { } memoLength) yield return new XElement("MaxLength", Text(memoLength));
                break;
            case IntegerAttributeMetadata i:
                yield return new XElement("Format", i.Format?.ToString().ToLowerInvariant());
                yield return Optional("MinValue", i.MinValue is { } min ? Text(min) : null);
                yield return Optional("MaxValue", i.MaxValue is { } max ? Text(max) : null);
                break;
            case BigIntAttributeMetadata:
                yield return new XElement("MinValue", Text(long.MinValue)); // the range of a bigint is fixed
                yield return new XElement("MaxValue", Text(long.MaxValue));
                break;
            case DecimalAttributeMetadata d:
                yield return Optional("MinValue", d.MinValue is { } dMin ? Text(dMin) : null);
                yield return Optional("MaxValue", d.MaxValue is { } dMax ? Text(dMax) : null);
                yield return Optional("Accuracy", d.Precision is { } dPrecision ? Text(dPrecision) : null);
                break;
            case DoubleAttributeMetadata f:
                yield return Optional("MinValue", f.MinValue is { } fMin ? Text(fMin) : null);
                yield return Optional("MaxValue", f.MaxValue is { } fMax ? Text(fMax) : null);
                yield return Optional("Accuracy", f.Precision is { } fPrecision ? Text(fPrecision) : null);
                break;
            case MoneyAttributeMetadata money:
                yield return Optional("MinValue", money.MinValue is { } mMin ? Text(mMin) : null);
                yield return Optional("MaxValue", money.MaxValue is { } mMax ? Text(mMax) : null);
                yield return Optional("Accuracy", money.Precision is { } mPrecision ? Text(mPrecision) : null);
                yield return Optional("AccuracySource", money.PrecisionSource is { } source ? Text(source) : null);
                break;
            case BooleanAttributeMetadata b:
                if (b.DefaultValue is { } value) yield return new XElement("AppDefaultValue", Bit(value));
                if (b.OptionSet is { } bit) yield return BooleanOptionSet(bit, LocalName(entity, attribute));
                break;
            case DateTimeAttributeMetadata dt:
                yield return Optional("Format", dt.Format switch
                {
                    DateTimeFormat.DateOnly => "date",
                    DateTimeFormat.DateAndTime => "datetime",
                    _ => null,
                });
                // No behavior is UserLocal (the Dataverse default; xrmverse's generated createdon/modifiedon have none).
                var behavior = dt.DateTimeBehavior?.Value ?? DateTimeBehavior.UserLocal.Value;
                yield return new XElement("Behavior", behavior == DateTimeBehavior.DateOnly.Value ? "2"
                    : behavior == DateTimeBehavior.TimeZoneIndependent.Value ? "3" : "1");
                break;
            case EntityNameAttributeMetadata: // the table names it holds are the platform's: nothing of its own to write
                break;
            case StateAttributeMetadata or StatusAttributeMetadata:
                yield return new XElement("AppDefaultValue", "-1");
                if (((EnumAttributeMetadata)attribute).OptionSet is { } stateSet) yield return OptionSet(stateSet, global: false, LocalName(entity, attribute));
                break;
            case EnumAttributeMetadata choice: // Picklist, MultiSelectPicklist
                var defaultValue = choice switch
                {
                    PicklistAttributeMetadata p => p.DefaultFormValue,
                    MultiSelectPicklistAttributeMetadata ms => ms.DefaultFormValue,
                    _ => null,
                };
                yield return new XElement("AppDefaultValue", Text(defaultValue ?? -1));
                if (choice.OptionSet is { IsGlobal: true, Name: { } global }) yield return new XElement("OptionSetName", global);
                else if (choice.OptionSet is { } local) yield return OptionSet(local, global: false, LocalName(entity, attribute));
                break;
            case LookupAttributeMetadata:
                yield return new XElement("LookupTypes"); // the targets are written as the relationships
                break;
            // Ruling F11: the element names (MaxSizeInKB, CanStoreFullImage, IsPrimaryImage, MaxHeight, MaxWidth) are the SDK
            // properties, written where the other type-specific elements go; no Dataverse export sample was found to confirm
            // the names or the position.
            case FileAttributeMetadata file:
                yield return Optional("MaxSizeInKB", file.MaxSizeInKB is { } fileSize ? Text(fileSize) : null);
                break;
            case ImageAttributeMetadata image:
                yield return Optional("MaxSizeInKB", image.MaxSizeInKB is { } imageSize ? Text(imageSize) : null);
                yield return Optional("CanStoreFullImage", image.CanStoreFullImage is { } full ? Bit(full) : null);
                yield return Optional("IsPrimaryImage", image.IsPrimaryImage is { } primary ? Bit(primary) : null);
                yield return new XElement("MaxHeight", Text(FileColumns.ThumbnailSize)); // thumbnails are always 144x144 (read-only)
                yield return new XElement("MaxWidth", Text(FileColumns.ThumbnailSize));
                break;
        }
    }

    /// <summary>The name Dataverse gives a local option set: <c>&lt;entity&gt;_&lt;attribute&gt;</c>.</summary>
    private static string LocalName(EntityMetadata entity, AttributeMetadata attribute) => $"{entity.LogicalName}_{attribute.LogicalName}";

    // ---- option sets ------------------------------------------------------------------------------------------------

    private static XElement OptionSet(OptionSetMetadataBase set, bool global, string? localName)
    {
        if (set is BooleanOptionSetMetadata boolean) return BooleanOptionSet(boolean, localName ?? boolean.Name ?? "");
        var options = (OptionSetMetadata)set;
        var name = global ? options.Name : options.Name ?? localName;
        var element = new XElement("optionset", new XAttribute("Name", name ?? ""));
        if (global)
        {
            element.Add(new XAttribute("localizedName", Base(options.DisplayName) ?? ""), new XAttribute("description", Base(options.Description) ?? ""));
        }
        var type = options.OptionSetType ?? OptionSetType.Picklist;
        element.Add(new XElement("OptionSetType", type.ToString().ToLowerInvariant()));
        if (type == OptionSetType.Picklist) element.Add(new XElement("IsGlobal", Bit(global || options.IsGlobal == true)));
        element.Add(KeptXml.Labels("displaynames", "displayname", options.DisplayName), KeptXml.Labels("Descriptions", "Description", options.Description));
        switch (type)
        {
            case OptionSetType.State:
                element.Add(new XElement("states", options.Options.Select(o =>
                {
                    var state = (StateOptionMetadata)o;
                    var item = new XElement("state", new XAttribute("value", Text(o.Value ?? 0)));
                    if (state.DefaultStatus is { } defaultStatus) item.Add(new XAttribute("defaultstatus", Text(defaultStatus)));
                    if (state.InvariantName is { } invariant) item.Add(new XAttribute("invariantname", invariant));
                    return OptionContent(item, o);
                })));
                break;
            case OptionSetType.Status:
                element.Add(new XElement("statuses", options.Options.Select(o =>
                {
                    var item = new XElement("status", new XAttribute("value", Text(o.Value ?? 0)));
                    if (((StatusOptionMetadata)o).State is { } state) item.Add(new XAttribute("state", Text(state)));
                    return OptionContent(item, o);
                })));
                break;
            default:
                element.Add(new XElement("options", options.Options.Select(Option)));
                break;
        }
        return element;
    }

    private static XElement BooleanOptionSet(BooleanOptionSetMetadata set, string name)
    {
        var element = new XElement("optionset", new XAttribute("Name", string.IsNullOrEmpty(set.Name) ? name : set.Name),
            new XElement("OptionSetType", "bit"),
            KeptXml.Labels("displaynames", "displayname", set.DisplayName),
            KeptXml.Labels("Descriptions", "Description", set.Description),
            new XElement("options", new[] { set.TrueOption, set.FalseOption }.OfType<OptionMetadata>().Select(Option)));
        return element;
    }

    private static XElement Option(OptionMetadata option)
    {
        var item = new XElement("option", new XAttribute("value", Text(option.Value ?? 0)), new XAttribute("ExternalValue", option.ExternalValue ?? ""));
        if (option.Color is { } color) item.Add(new XAttribute("Color", color));
        return OptionContent(item, option);
    }

    private static XElement OptionContent(XElement item, OptionMetadata option)
    {
        item.Add(KeptXml.Labels("labels", "label", option.Label));
        if (option.Description?.LocalizedLabels is { Count: > 0 }) item.Add(KeptXml.Labels("Descriptions", "Description", option.Description));
        return item;
    }

    // ---- relationships ----------------------------------------------------------------------------------------------

    private static XElement Relationship(SolutionPackage package, RelationshipMetadataBase relationship)
    {
        var element = new XElement("EntityRelationship", new XAttribute("Name", relationship.SchemaName));
        switch (relationship)
        {
            case OneToManyRelationshipMetadata oneToMany:
            {
                var lookup = package.Lookups.FirstOrDefault(l =>
                    l.EntityLogicalName == oneToMany.ReferencingEntity && l.LogicalName == oneToMany.ReferencingAttribute);
                element.Add(new XElement("EntityRelationshipType", "OneToMany"));
                // The reader takes a 1:N as custom when its lookup is: written only when that would read otherwise.
                if ((relationship.IsCustomRelationship == true) != (lookup?.IsCustomAttribute == true))
                    element.Add(new XElement("IsCustomRelationship", Bit(relationship.IsCustomRelationship == true)));
                element.Add(
                    new XElement("IsHierarchical", Bit(oneToMany.IsHierarchical == true)),
                    new XElement("ReferencingEntityName", EntityName(package, oneToMany.ReferencingEntity)),
                    new XElement("ReferencedEntityName", EntityName(package, oneToMany.ReferencedEntity)));
                element.Add(Cascades(oneToMany.CascadeConfiguration));
                element.Add(new XElement("ReferencingAttributeName", lookup?.SchemaName ?? oneToMany.ReferencingAttribute));
                element.Add(Description(package, relationship));
                element.Add(Roles(package, relationship,
                [
                    (1, oneToMany.ReferencingEntityNavigationPropertyName),
                    (0, oneToMany.ReferencedEntityNavigationPropertyName),
                ]));
                break;
            }
            case ManyToManyRelationshipMetadata manyToMany:
            {
                element.Add(new XElement("EntityRelationshipType", "ManyToMany"));
                // The reader takes an N:N as custom when its name has the publisher prefix: written only when that would read otherwise.
                var prefixed = relationship.SchemaName.StartsWith(package.Publisher.CustomizationPrefix + "_", StringComparison.OrdinalIgnoreCase);
                if ((relationship.IsCustomRelationship == true) != prefixed)
                    element.Add(new XElement("IsCustomRelationship", Bit(relationship.IsCustomRelationship == true)));
                element.Add(
                    new XElement("IsHierarchical", "0"),
                    new XElement("FirstEntityName", EntityName(package, manyToMany.Entity1LogicalName)),
                    new XElement("SecondEntityName", EntityName(package, manyToMany.Entity2LogicalName)),
                    new XElement("IntersectEntityName", manyToMany.IntersectEntityName));
                element.Add(Cascades(null)); // xrmverse has no cascade on a many-to-many relationship
                element.Add(Description(package, relationship));
                element.Add(Roles(package, relationship,
                [
                    (0, manyToMany.Entity1NavigationPropertyName),
                    (0, manyToMany.Entity2NavigationPropertyName),
                ]));
                break;
            }
            default:
                throw XrmdFaultException.InvalidArgument(
                    $"Relationship '{relationship.SchemaName}' is of a kind xrmverse cannot write in a solution file.");
        }
        return element;
    }

    private static string? EntityName(SolutionPackage package, string? logicalName) =>
        package.Entities.FirstOrDefault(e => e.LogicalName == logicalName)?.LogicalName ?? logicalName;

    private static IEnumerable<XElement> Cascades(CascadeConfiguration? cascade)
    {
        string Of(CascadeType? type) => (type ?? CascadeType.NoCascade).ToString();
        yield return new XElement("CascadeAssign", Of(cascade?.Assign));
        yield return new XElement("CascadeDelete", Of(cascade?.Delete));
        if (cascade?.Merge is { } merge && merge != CascadeType.NoCascade) yield return new XElement("CascadeMerge", Of(merge));
        yield return new XElement("CascadeArchive", Of(cascade?.Archive));
        yield return new XElement("CascadeReparent", Of(cascade?.Reparent));
        yield return new XElement("CascadeShare", Of(cascade?.Share));
        yield return new XElement("CascadeUnshare", Of(cascade?.Unshare));
        yield return new XElement("CascadeRollupView", Of(cascade?.RollupView));
    }

    private static XElement Description(SolutionPackage package, RelationshipMetadataBase relationship) =>
        new("RelationshipDescription", KeptXml.Labels("Descriptions", "Description",
            package.RelationshipDescriptions?.GetValueOrDefault(relationship.SchemaName)));

    /// <summary>
    /// The <c>EntityRelationshipRoles</c>: the kept menu configuration (the roles without their navigation property names)
    /// with each role's navigation property name put back before its <c>RelationshipRoleType</c> (a one-to-many role by its
    /// type, 1 referencing and 0 referenced; a many-to-many role by its position, first and second entity); without a kept
    /// configuration, one role per side with its navigation property name and type.
    /// </summary>
    private static XElement Roles(SolutionPackage package, RelationshipMetadataBase relationship, (int Type, string? Navigation)[] sides)
    {
        var oneToMany = relationship is OneToManyRelationshipMetadata;
        if (package.RelationshipMenus.GetValueOrDefault(relationship.SchemaName) is not { } menu)
            return new XElement("EntityRelationshipRoles", sides.Select(side => new XElement("EntityRelationshipRole",
                Optional("NavigationPropertyName", side.Navigation), new XElement("RelationshipRoleType", Text(side.Type)))));

        var roles = new XElement(menu);
        var index = 0;
        foreach (var role in roles.Elements("EntityRelationshipRole"))
        {
            var navigation = oneToMany
                ? sides.FirstOrDefault(s => Text(s.Type) == ((string?)role.Element("RelationshipRoleType"))?.Trim()).Navigation
                : sides.ElementAtOrDefault(index).Navigation;
            index++;
            if (navigation is null) continue;
            var element = new XElement("NavigationPropertyName", navigation);
            if (role.Element("RelationshipRoleType") is { } type) type.AddBeforeSelf(element);
            else role.Add(element);
        }
        return roles;
    }

    // ---- roles, plug-ins, web resources -----------------------------------------------------------------------------

    private static XElement Role(PackageRole role) =>
        new("Role", new XAttribute("id", Braced(role.Id)), new XAttribute("name", role.Name),
            new XElement("RolePrivileges", role.Privileges.Select(p => new XElement("RolePrivilege",
                new XAttribute("name", p.Name), new XAttribute("level", Level(role, p))))));

    private static string Level(PackageRole role, (string Name, DepthMask Depth) privilege) => privilege.Depth switch
    {
        DepthMask.Basic => "Basic",
        DepthMask.Local => "Local",
        DepthMask.Deep => "Deep",
        DepthMask.Global => "Global",
        _ => throw XrmdFaultException.InvalidArgument(
            $"Role '{role.Name}': privilege '{privilege.Name}' has the depth {privilege.Depth}, which a solution file cannot carry."),
    };

    /// <summary>
    /// A field security profile (ruling F8) in the shape of a real Dataverse export: <c>name</c>, <c>description</c> and
    /// <c>fieldsecurityprofileid</c> attributes, its permissions under <c>FieldPermissions</c> (by table then column) with
    /// their values as child elements in the export's order. The users and teams of the profile never travel.
    /// </summary>
    private static XElement FieldSecurityProfile(PackageFieldSecurityProfile profile) =>
        new("FieldSecurityProfile",
            new XAttribute("name", profile.Name),
            profile.Description is null ? null : new XAttribute("description", profile.Description),
            new XAttribute("fieldsecurityprofileid", Braced(profile.Id)),
            new XElement("FieldPermissions", profile.Permissions
                .OrderBy(p => p.Entity, StringComparer.Ordinal).ThenBy(p => p.Attribute, StringComparer.Ordinal)
                .Select(p => new XElement("FieldPermission",
                    new XElement("EntityName", p.Entity),
                    new XElement("AttributeName", p.Attribute),
                    new XElement("CanRead", Text(p.CanRead)),
                    new XElement("CanUpdate", Text(p.CanUpdate)),
                    new XElement("CanCreate", Text(p.CanCreate)),
                    new XElement("CanReadUnmasked", Text(p.CanReadUnmasked))))));

    private static XElement Assembly(PackageAssembly assembly) =>
        new("PluginAssembly", new XAttribute("FullName", assembly.FullName), new XAttribute("PluginAssemblyId", assembly.Id.ToString("D")),
            new XElement("IsolationMode", Text(assembly.IsolationMode)),
            new XElement("SourceType", Text(assembly.SourceType)),
            new XElement("FileName", "/" + AssemblyPath(assembly)),
            new XElement("PluginTypes", assembly.Types.OrderBy(t => t.Id.ToString("D"), StringComparer.Ordinal).Select(type =>
                new XElement("PluginType", new XAttribute("AssemblyQualifiedName", type.AssemblyQualifiedName),
                    new XAttribute("PluginTypeId", type.Id.ToString("D")), new XAttribute("Name", type.TypeName),
                    Optional("FriendlyName", type.FriendlyName),
                    Optional("WorkflowActivityGroupName", type.WorkflowActivityGroupName)))));

    private static XElement Step(PackageStep step)
    {
        var element = new XElement("SdkMessageProcessingStep", new XAttribute("Name", step.Name),
            new XAttribute("SdkMessageProcessingStepId", Braced(step.Id)),
            new XElement("SdkMessageId", Braced(step.SdkMessageId)),
            new XElement("PluginTypeName", step.PluginTypeName),
            new XElement("PluginTypeId", step.PluginTypeId.ToString("D")),
            new XElement("PrimaryEntity", step.PrimaryEntity ?? "none"),
            new XElement("AsyncAutoDelete", Bit(step.AsyncAutoDelete)),
            Optional("Description", step.Description),
            new XElement("FilteringAttributes", step.FilteringAttributes),
            Optional("ImpersonatingUserId", step.ImpersonatingUserId?.ToString("D")),
            new XElement("InvocationSource", Text(step.InvocationSource)),
            new XElement("Mode", Text(step.Mode)),
            new XElement("Rank", Text(step.Rank)),
            Optional("SdkMessageFilterId", step.SdkMessageFilterId?.ToString("D")),
            new XElement("Stage", Text(step.Stage)),
            new XElement("SupportedDeployment", Text(step.SupportedDeployment)),
            Optional("CustomConfiguration", step.Configuration),
            new XElement("StateCode", step.Disabled ? "1" : "0"),
            new XElement("StatusCode", step.Disabled ? "2" : "1"));
        if (step.Images.Count > 0)
            element.Add(new XElement("SdkMessageProcessingStepImages", step.Images.OrderBy(i => i.Id.ToString("D"), StringComparer.Ordinal)
                .Select(image => new XElement("SdkMessageProcessingStepImage", new XAttribute("Name", image.Name),
                    new XElement("SdkMessageProcessingStepImageId", Braced(image.Id)),
                    Optional("Attributes", image.Attributes),
                    new XElement("EntityAlias", image.EntityAlias),
                    new XElement("ImageType", Text(image.ImageType)),
                    new XElement("MessagePropertyName", image.MessagePropertyName)))));
        return element;
    }

    private static XElement WebResource(PackageWebResource resource) =>
        new("WebResource",
            new XElement("WebResourceId", Braced(resource.Id)),
            new XElement("Name", resource.Name),
            Optional("DisplayName", resource.DisplayName),
            Optional("Description", resource.Description),
            new XElement("WebResourceType", Text(resource.WebResourceType)),
            new XElement("FileName", "/" + WebResourcePath(resource)));

    // ---- custom APIs ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The files of the custom APIs of the package (<see cref="SolutionPackage.CustomApis"/>), by their path in the archive:
    /// <c>customapis/{api}/customapi.xml</c> and a folder per request parameter and response property, each named by its
    /// unique name. The children are written in the export order, what the API keeps (<see cref="PackageCustomApi.Kept"/>) in
    /// its place; UTF-8 without a byte order mark or declaration, indented, with CRLF line ends, as in a Dataverse export.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> CustomApiFiles(SolutionPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var language = package.Solution.LanguageCode;
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var api in package.CustomApis ?? [])
        {
            var folder = $"{CustomApiReader.Folder}/{api.UniqueName}";
            files[$"{folder}/{CustomApiReader.ApiFile}"] = Utf8(CustomApi(api, language));
            foreach (var parameter in api.RequestParameters)
                files[$"{folder}/{CustomApiReader.ParametersFolder}/{parameter.UniqueName}/{CustomApiReader.ParameterFile}"] =
                    Utf8(CustomApiField(parameter, request: true, api.UniqueName, language));
            foreach (var property in api.ResponseProperties)
                files[$"{folder}/{CustomApiReader.PropertiesFolder}/{property.UniqueName}/{CustomApiReader.PropertyFile}"] =
                    Utf8(CustomApiField(property, request: false, api.UniqueName, language));
        }
        return files;
    }

    // ---- custom controls ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>CustomControl</c> element of a control: the element the file gave it (<see cref="PackageCustomControl.Source"/>,
    /// whatever else it carries kept) with its <c>Name</c> and <c>FileName</c> (<c>/</c> and the manifest path) set.
    /// </summary>
    private static XElement CustomControl(PackageCustomControl control)
    {
        var element = new XElement(control.Source);
        if (element.Element("Name") is null) element.AddFirst(new XElement("Name"));
        Set(element, "Name", control.Name);
        if (element.Element("FileName") is null) element.Element("Name")!.AddAfterSelf(new XElement("FileName"));
        Set(element, "FileName", "/" + control.ManifestPath);
        return element;
    }

    // ---- environment variables ----------------------------------------------------------------------------------------

    /// <summary>
    /// The files of the environment variables of the package (<see cref="SolutionPackage.EnvironmentVariables"/>), by their path in
    /// the archive: <c>environmentvariabledefinitions/{schemaname}/environmentvariabledefinition.xml</c> with what the definition
    /// keeps (<see cref="PackageEnvironmentVariable.Source"/>) in its place, and <c>environmentvariablevalues.json</c> beside it
    /// when the definition has a value file. The XML is written as for custom APIs; the JSON on one line, UTF-8 without a byte
    /// order mark, in the shape of a Dataverse export.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> EnvironmentVariableFiles(SolutionPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in package.EnvironmentVariables)
        {
            var folder = $"{EnvironmentVariableReader.Folder}/{variable.SchemaName}";
            files[$"{folder}/{EnvironmentVariableReader.DefinitionFile}"] = Utf8(EnvironmentVariable(variable));
            if (variable.HasValueFile)
                files[$"{folder}/{EnvironmentVariableReader.ValueFile}"] = EnvironmentVariableValue(variable.Value, variable.ValueIsCustomizable);
        }
        return files;
    }

    private static XElement EnvironmentVariable(PackageEnvironmentVariable variable)
    {
        var kept = variable.Source;
        // The export order is alphabetical; Merge puts each child at its marker, a child without one after those written before it.
        var written = new XElement("environmentvariabledefinition", new XAttribute(EnvironmentVariableSchema.SchemaName, variable.SchemaName),
            Optional(EnvironmentVariableSchema.DefaultValue, variable.DefaultValue, kept),
            Optional(EnvironmentVariableSchema.Hint, variable.Hint, kept),
            Optional(EnvironmentVariableSchema.IntroducedVersion, variable.IntroducedVersion, kept),
            new XElement(EnvironmentVariableSchema.IsRequired, Bit(variable.IsRequired)),
            new XElement(EnvironmentVariableSchema.SecretStore, Text(variable.SecretStore)),
            new XElement(EnvironmentVariableSchema.Type, Text(variable.Type)),
            Optional(EnvironmentVariableSchema.ValueSchema, variable.ValueSchema, kept));
        return UnmodelledXml.Merge(written, kept, EnvironmentVariableReader.Shape, $"environment variable '{variable.SchemaName}'");
    }

    /// <summary>
    /// <c>{"environmentvariablevalues":{"environmentvariablevalue":{"iscustomizable":"1","value":…}}}</c>; no <c>value</c> key for
    /// null, no <c>iscustomizable</c> key when <paramref name="isCustomizable"/> is null.
    /// </summary>
    private static byte[] EnvironmentVariableValue(string? value, string? isCustomizable)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream,
                   new System.Text.Json.JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("environmentvariablevalues");
            writer.WriteStartObject("environmentvariablevalue");
            if (isCustomizable is not null) writer.WriteString("iscustomizable", isCustomizable);
            if (value is not null) writer.WriteString("value", value);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>The file of a custom API element: UTF-8 without a byte order mark or declaration, indented by two spaces, CRLF.</summary>
    private static byte[] Utf8(XElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(stream, new System.Xml.XmlWriterSettings
               {
                   Indent = true, IndentChars = "  ", NewLineChars = "\r\n", NewLineHandling = System.Xml.NewLineHandling.Replace,
                   OmitXmlDeclaration = true, Encoding = new System.Text.UTF8Encoding(false),
               }))
            element.Save(writer);
        return stream.ToArray();
    }

    private static XElement CustomApi(PackageCustomApi api, int language)
    {
        // Nothing kept (an API that did not come from a file): the markers of the export order and iscustomizable 1. The
        // optional elements have no marker, so that a missing value writes no element.
        var kept = api.Kept ?? UnmodelledXml.Template("customapi", ("allowedcustomprocessingsteptype", null), ("bindingtype", null),
            ("iscustomizable", "1"), ("isfunction", null), ("isprivate", null), ("name", null),
            ("workflowsdkstepenabled", null));
        var written = new XElement("customapi", new XAttribute("uniquename", api.UniqueName),
            new XElement("allowedcustomprocessingsteptype", Text(api.AllowedStepType)),
            new XElement("bindingtype", Text(api.BindingType)),
            Optional("boundentitylogicalname", api.BoundEntity, kept),
            Optional("executeprivilegename", api.ExecutePrivilegeName, kept),
            new XElement("isfunction", Bit(api.IsFunction)),
            new XElement("isprivate", Bit(api.IsPrivate)),
            new XElement("name", api.Name),
            api.PluginTypeKey is { } key
                ? new XElement("plugintypeid", new XElement("plugintypeexportkey", key.ToString("D")))
                : Optional("plugintypeid", null, kept),
            new XElement("workflowsdkstepenabled", Bit(api.WorkflowEnabled)));
        var element = UnmodelledXml.Merge(written, kept, CustomApiReader.ApiShape, $"custom API '{api.UniqueName}'");
        SetLabels(element, "description", api.Description, language, CustomApiReader.ApiOrder);
        SetLabels(element, "displayname", api.DisplayName, language, CustomApiReader.ApiOrder);
        return element;
    }

    private static XElement CustomApiField(PackageCustomApiField field, bool request, string api, int language)
    {
        var root = request ? "customapirequestparameter" : "customapiresponseproperty";
        var kept = field.Kept ?? (request
            ? UnmodelledXml.Template(root, ("iscustomizable", "1"), ("isoptional", null), ("name", null), ("type", null))
            : UnmodelledXml.Template(root, ("iscustomizable", "1"), ("name", null), ("type", null)));
        var written = new XElement(root, new XAttribute("uniquename", field.UniqueName),
            request ? new XElement("isoptional", Bit(field.IsOptional == true)) : null,
            Optional("logicalentityname", field.LogicalEntityName, kept),
            new XElement("name", field.Name),
            new XElement("type", Text(field.Type)));
        var element = UnmodelledXml.Merge(written, kept, request ? CustomApiReader.ParameterShape : CustomApiReader.PropertyShape,
            $"{(request ? "request parameter" : "response property")} '{field.UniqueName}' of custom API '{api}'");
        SetLabels(element, "description", field.Description, language, CustomApiReader.FieldOrder);
        SetLabels(element, "displayname", field.DisplayName, language, CustomApiReader.FieldOrder);
        return element;
    }

    /// <summary>An optional element: its value, or empty when the kept XML has its marker (a file that wrote it empty); else none.</summary>
    private static XElement? Optional(string name, string? value, XElement kept) =>
        value is not null ? new XElement(name, value) : kept.Element(name) is not null ? new XElement(name) : null;

    /// <summary>
    /// Puts <paramref name="label"/> in the <c>description</c> or <c>displayname</c> element: each language's <c>label</c>
    /// replaced or added (the kept ones of other languages stay, C11) and <c>default</c> set to the base language's text. An
    /// element the kept XML does not have is added in the export order <paramref name="order"/>; an empty label changes nothing.
    /// </summary>
    private static void SetLabels(XElement element, string name, Label label, int language, string[] order)
    {
        var labels = (label.LocalizedLabels ?? []).Where(l => !string.IsNullOrEmpty(l.Label)).ToList();
        if (labels.Count == 0) return;
        if (element.Element(name) is not { } container)
        {
            container = new XElement(name);
            var index = Array.IndexOf(order, name);
            var next = element.Elements().FirstOrDefault(e => Array.IndexOf(order, e.Name.LocalName) > index);
            if (next is null) element.Add(container);
            else next.AddBeforeSelf(container);
        }
        foreach (var localized in labels)
        {
            var code = Text(localized.LanguageCode);
            if (container.Elements("label").FirstOrDefault(e => (string?)e.Attribute("languagecode") == code) is { } existing)
                existing.SetAttributeValue("description", localized.Label);
            else
                container.Add(new XElement("label", new XAttribute("description", localized.Label), new XAttribute("languagecode", code)));
        }
        var main = labels.FirstOrDefault(l => l.LanguageCode == language) ?? label.UserLocalizedLabel ?? labels[0];
        container.SetAttributeValue("default", main.Label);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>The label of the base language (the user's, else the first).</summary>
    private static string? Base(Label? label) => label?.UserLocalizedLabel?.Label ?? label?.LocalizedLabels.FirstOrDefault()?.Label;

    private static XElement? Optional(string name, string? value) => value is null ? null : new XElement(name, value);

    private static XElement Nil(string name) => new(name, new XAttribute(Xsi + "nil", "true"));

    private static void Set(XElement parent, string name, string value)
    {
        if (parent.Element(name) is { } element)
        {
            element.RemoveAttributes();
            element.Value = value;
        }
        else parent.Add(new XElement(name, value));
    }

    private static void Replace(XElement parent, XElement replacement)
    {
        if (parent.Element(replacement.Name) is { } element) element.ReplaceWith(replacement);
        else parent.Add(replacement);
    }

    private static string Bit(bool value) => value ? "1" : "0";

    private static string Braced(Guid id) => id.ToString("B");

    private static string Upper(Guid id) => id.ToString("D").ToUpperInvariant();

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
