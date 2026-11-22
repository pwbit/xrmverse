using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Xrmd.TestSupport;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The managed properties a component carries in a solution file, as the elements a Dataverse export writes
/// (<c>IsCustomizable</c>, <c>IsRenameable</c>, <c>CanBeDeleted</c>, <c>CanModify*Settings</c>, <c>CanCreateAttributes</c>);
/// each component kind writes the ones it has. A default instance leaves everything allowed.
/// </summary>
/// <param name="IsCustomizable">Whether unmanaged customizations may change the component.</param>
/// <param name="IsRenameable">Whether its display name may change (tables, columns).</param>
/// <param name="CanBeDeleted">Whether it may be deleted (views, web resources).</param>
/// <param name="CanModifyRequirementLevelSettings">Whether the required level of a column may change.</param>
/// <param name="CanModifySearchSettings">Whether the search settings of a column may change.</param>
/// <param name="CanModifyAdditionalSettings">Whether the other settings of a table or column may change.</param>
/// <param name="CanCreateAttributes">Whether columns may be added to a table.</param>
/// <param name="CanCreateForms">Whether forms may be created for a table.</param>
/// <param name="CanCreateViews">Whether views may be created for a table; null follows <paramref name="IsCustomizable"/>.</param>
public sealed record TestManagedProps(bool IsCustomizable = true, bool IsRenameable = true, bool CanBeDeleted = true,
    bool CanModifyRequirementLevelSettings = true, bool CanModifySearchSettings = true, bool CanModifyAdditionalSettings = true,
    bool CanCreateAttributes = true, bool CanCreateForms = true, bool? CanCreateViews = null)
{
    /// <summary>Everything allowed (the default of a component with no explicit properties).</summary>
    public static TestManagedProps Open { get; } = new();

    /// <summary>Everything refused.</summary>
    public static TestManagedProps Locked { get; } = new(false, false, false, false, false, false, false, false, false);
}

/// <summary>A column of a <see cref="TestTable"/>.</summary>
/// <param name="Name">The logical name (e.g. <c>xm_code</c>).</param>
/// <param name="Label">The display name in the base language.</param>
/// <param name="Type">The file's attribute type: <c>nvarchar</c> (default) or <c>int</c>.</param>
/// <param name="MaxLength">The <c>MaxLength</c> of an <c>nvarchar</c> column.</param>
/// <param name="PrimaryName">Whether this is the table's primary name column.</param>
/// <param name="Props">The managed properties; null for all allowed.</param>
public sealed record TestColumn(string Name, string Label, string Type = "nvarchar", int MaxLength = 100, bool PrimaryName = false,
    TestManagedProps? Props = null);

/// <summary>A user-owned-free (organization-owned) table of a <see cref="ManagedSpec"/>; its primary key is <c>{Name}id</c>.</summary>
/// <param name="Name">The logical name (e.g. <c>xm_item</c>).</param>
/// <param name="Label">The display name in the base language.</param>
/// <param name="Columns">The custom columns, one of them the primary name.</param>
/// <param name="Props">The managed properties of the table; null for all allowed.</param>
public sealed record TestTable(string Name, string Label, IReadOnlyList<TestColumn> Columns, TestManagedProps? Props = null);

/// <summary>A global option set (picklist).</summary>
/// <param name="Name">The name (e.g. <c>xm_status</c>).</param>
/// <param name="Label">The display name.</param>
/// <param name="Options">Each value with its label.</param>
/// <param name="Props">The managed properties; null for all allowed.</param>
public sealed record TestOptionSet(string Name, string Label, IReadOnlyList<(int Value, string Label)> Options, TestManagedProps? Props = null);

/// <summary>A system view of a table.</summary>
/// <param name="Name">The view name, also what its id derives from.</param>
/// <param name="Table">The table's logical name.</param>
/// <param name="Columns">The columns the view shows; the first one is the jump column.</param>
/// <param name="Props">The managed properties; null for all allowed.</param>
public sealed record TestView(string Name, string Table, IReadOnlyList<string> Columns, TestManagedProps? Props = null);

/// <summary>A security role.</summary>
/// <param name="Name">The role name, also what its id derives from.</param>
/// <param name="Privileges">The privileges, each with the level <c>Basic</c>, <c>Local</c>, <c>Deep</c> or <c>Global</c>.</param>
public sealed record TestRole(string Name, IReadOnlyList<(string Privilege, string Level)> Privileges);

/// <summary>A script web resource.</summary>
/// <param name="Name">The unique name (e.g. <c>xm_/scripts/item.js</c>).</param>
/// <param name="Content">The text of the script.</param>
/// <param name="Props">The managed properties; null for all allowed.</param>
public sealed record TestWebResource(string Name, string Content, TestManagedProps? Props = null);

/// <summary>A synchronous step of a plug-in type of <c>Xrmd.TestPlugins</c>.</summary>
/// <param name="Table">The table's logical name.</param>
/// <param name="Message">The message: <c>Create</c> or <c>Update</c>.</param>
/// <param name="PluginTypeName">The full name of the plug-in type.</param>
/// <param name="Stage">10, 20 or 40.</param>
/// <param name="Configuration">The unsecure configuration; null for none.</param>
/// <param name="Props">The managed properties; null for all allowed.</param>
public sealed record TestStep(string Table, string Message, string PluginTypeName, int Stage, string? Configuration = null,
    TestManagedProps? Props = null);

/// <summary>What <see cref="ManagedTestSolution.Build"/> builds a solution file from (plan 2.6, task 1).</summary>
/// <param name="Name">The solution's unique name (e.g. <c>xm_base</c>).</param>
/// <param name="Version">The version (e.g. <c>1.0.0.0</c>).</param>
/// <param name="Managed">Whether <c>solution.xml</c> says managed.</param>
/// <param name="Tables">The tables.</param>
/// <param name="OptionSets">The global option sets.</param>
/// <param name="Views">The views.</param>
/// <param name="Roles">The roles.</param>
/// <param name="WebResources">The web resources.</param>
/// <param name="Steps">The plug-in steps; the plug-in assembly is in the file when there is one.</param>
public sealed record ManagedSpec(string Name, string Version, bool Managed, IReadOnlyList<TestTable> Tables,
    IReadOnlyList<TestOptionSet>? OptionSets = null, IReadOnlyList<TestView>? Views = null, IReadOnlyList<TestRole>? Roles = null,
    IReadOnlyList<TestWebResource>? WebResources = null, IReadOnlyList<TestStep>? Steps = null);

/// <summary>
/// Builds managed (and unmanaged) test solution files for the managed solutions scenarios (plan 2.6): publisher
/// <see cref="PublisherName"/>, prefix <see cref="Prefix"/>, ids derived from the names (two builds of the same component are
/// the same component, as in <see cref="CustomApiTestSolution"/>), in the shape the reader accepts and a Dataverse export
/// writes, managed-property elements included.
/// </summary>
public static class ManagedTestSolution
{
    /// <summary>The publisher of the built solutions.</summary>
    public const string PublisherName = "xrmversemanaged";

    /// <summary>The customization prefix of <see cref="PublisherName"/>.</summary>
    public const string Prefix = "xm";

    /// <summary>The table of the fixed specs.</summary>
    public const string Table = "xm_item";

    /// <summary>The plug-in type of the step of the fixed specs: records the context of an Update into a <c>pb_pipelog</c> row.</summary>
    public const string StepPlugin = "Xrmd.TestPlugins.ContextRecorderPlugin";

    private const string AssemblyFullName = "Xrmd.TestPlugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=a15f04ad73430aaf";
    private const string CreateMessage = "9ebdbb1b-ea3e-db11-86a7-000a3a5473e8";
    private const string UpdateMessage = "20bebb1b-ea3e-db11-86a7-000a3a5473e8";

    private static readonly TestWebResource ItemScript = new("xm_/scripts/item.js", "// xrmverse managed test script\n");

    /// <summary>The first version: <c>xm_item</c> with <c>xm_name</c>, <c>xm_code</c> and <c>xm_legacy</c>, a view, a role, a script and a step.</summary>
    public static ManagedSpec V1 { get; } = new("xm_base", "1.0.0.0", true,
        [new(Table, "Item", [
            new("xm_name", "Name", PrimaryName: true),
            new("xm_code", "Code"),
            new("xm_legacy", "Legacy"),
        ])],
        OptionSets: [new("xm_status", "Status", [(100000000, "Open"), (100000001, "Closed")])],
        Views: [new("Active Items", Table, ["xm_name", "xm_code"])],
        Roles: [new("xm_reader", [("prvReadxm_Item", "Global")])],
        WebResources: [ItemScript],
        Steps: [new(Table, "Update", StepPlugin, 40, "pb_pipelog")]);

    /// <summary>
    /// The second version: drops the column <c>xm_legacy</c>, relabels <c>xm_name</c>, adds <c>xm_extra</c>, drops the role
    /// <c>xm_reader</c>, keeps the step (and the rest).
    /// </summary>
    public static ManagedSpec V2 { get; } = V1 with
    {
        Version = "2.0.0.0",
        Tables = [new(Table, "Item", [
            new("xm_name", "Title", PrimaryName: true),
            new("xm_code", "Code"),
            new("xm_extra", "Extra"),
        ])],
        Roles = [],
    };

    /// <summary>Another solution <c>xm_other</c> layering the label of <c>xm_code</c> of <see cref="Table"/> ("Code (other)"): a restore never has to shorten a column.</summary>
    public static ManagedSpec Other { get; } = new("xm_other", "1.0.0.0", true,
        [new(Table, "Item", [
            new("xm_name", "Name", PrimaryName: true),
            new("xm_code", "Code (other)"),
        ])]);

    /// <summary>The id of the component <paramref name="kind"/> (<c>role</c>, <c>step</c>, <c>view</c>, <c>webresource</c>, <c>assembly</c>) <paramref name="name"/>.</summary>
    public static Guid IdOf(string kind, string name) => new(MD5.HashData(Encoding.UTF8.GetBytes($"xrmverse-managed-tests:{kind}:{name}")));

    /// <summary>The solution file of <paramref name="spec"/>.</summary>
    public static byte[] Build(ManagedSpec spec)
    {
        var steps = spec.Steps ?? [];
        var webResources = spec.WebResources ?? [];
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["solution.xml"] = Bytes(Solution(spec)),
            ["customizations.xml"] = Bytes(Customizations(spec)),
            ["[Content_Types].xml"] = Encoding.UTF8.GetBytes(
                """<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/octet-stream" /><Default Extension="dll" ContentType="application/octet-stream" /></Types>"""),
        };
        if (steps.Count > 0) entries[AssemblyPath(spec)] = SolutionFixtureZip.TestPluginBytes;
        foreach (var resource in webResources)
            entries[WebResourcePath(resource).TrimStart('/')] = Encoding.UTF8.GetBytes(resource.Content);

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

    // ------------------------------------------------------------------------------------------ solution.xml

    private static Guid AssemblyId(ManagedSpec spec) => IdOf("assembly", spec.Name);

    private static string AssemblyPath(ManagedSpec spec) =>
        $"PluginAssemblies/XrmdTestPlugins-{AssemblyId(spec).ToString("D").ToUpperInvariant()}/XrmdTestPlugins.dll";

    private static string WebResourcePath(TestWebResource resource) =>
        $"/WebResources/{resource.Name}{IdOf("webresource", resource.Name).ToString("D").ToUpperInvariant()}";

    private static XDocument Solution(ManagedSpec spec)
    {
        var roots = new List<XElement>();
        roots.AddRange(spec.Tables.Select(t => Root(1, schemaName: t.Name)));
        roots.AddRange((spec.OptionSets ?? []).Select(o => Root(9, schemaName: o.Name)));
        roots.AddRange((spec.Roles ?? []).Select(r => Root(20, id: IdOf("role", r.Name))));
        roots.AddRange((spec.WebResources ?? []).Select(w => Root(61, schemaName: w.Name)));
        if ((spec.Steps ?? []).Count > 0)
            roots.Add(Root(91, id: AssemblyId(spec), schemaName: AssemblyFullName));
        roots.AddRange((spec.Steps ?? []).Select(s => Root(92, id: StepId(s))));

        return new(new XElement("ImportExportXml",
            new XAttribute("version", "9.2.24093.200"), new XAttribute("SolutionPackageVersion", "9.2"), new XAttribute("languagecode", 1033),
            new XAttribute("generatedBy", "CrmLive"),
            new XElement("SolutionManifest",
                new XElement("UniqueName", spec.Name),
                new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("description", spec.Name), new XAttribute("languagecode", 1033))),
                new XElement("Descriptions"),
                new XElement("Version", spec.Version),
                new XElement("Managed", spec.Managed ? 1 : 0),
                new XElement("Publisher",
                    new XElement("UniqueName", PublisherName),
                    new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("description", "xrmverse managed tests"), new XAttribute("languagecode", 1033))),
                    new XElement("Descriptions"),
                    new XElement("CustomizationPrefix", Prefix),
                    new XElement("CustomizationOptionValuePrefix", 10002)),
                new XElement("RootComponents", roots),
                new XElement("MissingDependencies"))));
    }

    private static XElement Root(int type, Guid? id = null, string? schemaName = null) => new("RootComponent",
        new XAttribute("type", type),
        id is null ? null : new XAttribute("id", id.Value.ToString("B")),
        schemaName is null ? null : new XAttribute("schemaName", schemaName),
        new XAttribute("behavior", 0));

    // ------------------------------------------------------------------------------------------ customizations.xml

    private static Guid StepId(TestStep step) => IdOf("step", $"{step.PluginTypeName}:{step.Message}:{step.Table}");

    private static Guid PluginTypeId(string typeName) => IdOf("plugintype", typeName);

    private static XDocument Customizations(ManagedSpec spec)
    {
        var steps = spec.Steps ?? [];
        var types = steps.Select(s => s.PluginTypeName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var views = spec.Views ?? [];

        return new(new XElement("ImportExportXml",
            new XElement("Entities", spec.Tables.Select(t => Entity(t, views.Where(v => v.Table == t.Name).ToList()))),
            new XElement("Roles", (spec.Roles ?? []).Select(Role)),
            new XElement("Workflows"), new XElement("FieldSecurityProfiles"), new XElement("Templates"), new XElement("EntityMaps"),
            new XElement("EntityRelationships"), new XElement("OrganizationSettings"),
            new XElement("optionsets", (spec.OptionSets ?? []).Select(OptionSet)),
            new XElement("CustomControls"),
            new XElement("SolutionPluginAssemblies", steps.Count == 0 ? null : new XElement("PluginAssembly",
                new XAttribute("FullName", AssemblyFullName), new XAttribute("PluginAssemblyId", AssemblyId(spec).ToString("D")),
                new XAttribute("CustomizationLevel", 1),
                new XElement("IsolationMode", 2),
                new XElement("SourceType", 0),
                new XElement("IntroducedVersion", "1.0"),
                new XElement("FileName", "/" + AssemblyPath(spec)),
                new XElement("PluginTypes", types.Select(t => new XElement("PluginType",
                    new XAttribute("AssemblyQualifiedName", $"{t}, {AssemblyFullName}"), new XAttribute("PluginTypeId", PluginTypeId(t).ToString("D")),
                    new XAttribute("Name", t),
                    new XElement("FriendlyName", PluginTypeId(t).ToString("D"))))))),
            new XElement("SdkMessageProcessingSteps", steps.Select(Step)),
            new XElement("WebResources", (spec.WebResources ?? []).Select(WebResource)),
            new XElement("Languages", new XElement("Language", 1033))));
    }

    private static XElement Entity(TestTable table, IReadOnlyList<TestView> views)
    {
        var props = table.Props ?? TestManagedProps.Open;
        var schema = Schema(table.Name);
        var primaryName = table.Columns.FirstOrDefault(c => c.PrimaryName)?.Name;
        var idName = table.Name + "id";

        var attributes = new List<XElement> { PrimaryKey(table.Name, schema, idName) };
        attributes.AddRange(table.Columns.Select(c => Column(c)));

        return new("Entity",
            new XElement("Name", new XAttribute("LocalizedName", table.Label), new XAttribute("OriginalName", table.Label), schema),
            new XElement("EntityInfo", new XElement("entity", new XAttribute("Name", schema),
                new XElement("LocalizedNames", Label("LocalizedName", table.Label)),
                new XElement("LocalizedCollectionNames", Label("LocalizedCollectionName", table.Label + "s")),
                new XElement("Descriptions", Label("Description", $"The test table {table.Name}.")),
                new XElement("attributes", attributes),
                new XElement("EntitySetName", table.Name + "s"),
                new XElement("IsDuplicateCheckSupported", 0),
                new XElement("IsBusinessProcessEnabled", 0),
                new XElement("IsRequiredOffline", 0),
                new XElement("IsInteractionCentricEnabled", 0),
                new XElement("IsCollaboration", 0),
                new XElement("AutoRouteToOwnerQueue", 0),
                new XElement("IsConnectionsEnabled", 0),
                new XElement("OwnershipTypeMask", "OrgOwned"),
                new XElement("IsAuditEnabled", 0),
                new XElement("IntroducedVersion", "1.0.0.0"),
                Flag("IsCustomizable", props.IsCustomizable),
                Flag("IsRenameable", props.IsRenameable),
                new XElement("CanModifyAuditSettings", 1),
                Flag("CanCreateAttributes", props.CanCreateAttributes),
                Flag("CanCreateForms", props.CanCreateForms),
                new XElement("CanCreateCharts", 1),
                Flag("CanCreateViews", props.CanCreateViews ?? props.IsCustomizable),
                Flag("CanModifyAdditionalSettings", props.CanModifyAdditionalSettings))),
            new XElement("SavedQueries", new XElement("savedqueries", views.Select(v => View(v, idName, primaryName)))));
    }

    private static XElement PrimaryKey(string table, string schema, string logicalName) => new("attribute",
        new XAttribute("PhysicalName", schema + "Id"),
        new XElement("Type", "primarykey"),
        new XElement("Name", logicalName),
        new XElement("LogicalName", logicalName),
        new XElement("RequiredLevel", "systemrequired"),
        new XElement("DisplayMask", "ValidForAdvancedFind|RequiredForGrid"),
        new XElement("ValidForUpdateApi", 0),
        new XElement("ValidForReadApi", 1),
        new XElement("ValidForCreateApi", 1),
        new XElement("IsCustomField", 0),
        new XElement("IsAuditEnabled", 1),
        new XElement("IsSecured", 0),
        new XElement("IntroducedVersion", "1.0.0.0"),
        new XElement("IsCustomizable", 1),
        new XElement("IsRenameable", 1),
        new XElement("CanModifySearchSettings", 1),
        new XElement("CanModifyRequirementLevelSettings", 1),
        new XElement("CanModifyAdditionalSettings", 1),
        new XElement("SourceType", 0),
        new XElement("displaynames", Label("displayname", table)),
        new XElement("Descriptions", Label("Description", "")));

    private static XElement Column(TestColumn column)
    {
        var props = column.Props ?? TestManagedProps.Open;
        var schema = Schema(column.Name);
        return new("attribute",
            new XAttribute("PhysicalName", schema),
            new XElement("Type", column.Type),
            new XElement("Name", column.Name),
            new XElement("LogicalName", column.Name),
            new XElement("RequiredLevel", column.PrimaryName ? "required" : "none"),
            new XElement("DisplayMask", column.PrimaryName
                ? "PrimaryName|ValidForAdvancedFind|ValidForForm|ValidForGrid|RequiredForForm"
                : "ValidForAdvancedFind|ValidForForm|ValidForGrid"),
            new XElement("ValidForUpdateApi", 1),
            new XElement("ValidForReadApi", 1),
            new XElement("ValidForCreateApi", 1),
            new XElement("IsCustomField", 1),
            new XElement("IsAuditEnabled", 1),
            new XElement("IsSecured", 0),
            new XElement("IntroducedVersion", "1.0.0.0"),
            Flag("IsCustomizable", props.IsCustomizable),
            Flag("IsRenameable", props.IsRenameable),
            Flag("CanModifySearchSettings", props.CanModifySearchSettings),
            Flag("CanModifyRequirementLevelSettings", props.CanModifyRequirementLevelSettings),
            Flag("CanModifyAdditionalSettings", props.CanModifyAdditionalSettings),
            new XElement("SourceType", 0),
            column.Type == "nvarchar" ? new XElement("Format", "text") : null,
            column.Type == "nvarchar" ? new XElement("MaxLength", column.MaxLength) : null,
            column.Type == "nvarchar" ? new XElement("Length", column.MaxLength * 2) : null,
            new XElement("displaynames", Label("displayname", column.Label)),
            new XElement("Descriptions", Label("Description", "")));
    }

    private static XElement View(TestView view, string idName, string? primaryName)
    {
        var props = view.Props ?? TestManagedProps.Open;
        var id = IdOf("view", view.Name);
        return new("savedquery",
            Flag("IsCustomizable", props.IsCustomizable),
            Flag("CanBeDeleted", props.CanBeDeleted),
            new XElement("isquickfindquery", 0),
            new XElement("isprivate", 0),
            new XElement("isdefault", 1),
            new XElement("savedqueryid", id.ToString("B")),
            new XElement("layoutxml", new XElement("grid", new XAttribute("name", "resultset"), new XAttribute("jump", primaryName ?? view.Columns[0]),
                new XAttribute("select", 1), new XAttribute("icon", 1), new XAttribute("preview", 1),
                new XElement("row", new XAttribute("name", "result"), new XAttribute("id", idName),
                    view.Columns.Select(c => new XElement("cell", new XAttribute("name", c), new XAttribute("width", 150)))))),
            new XElement("querytype", 0),
            new XElement("fetchxml", new XElement("fetch", new XAttribute("version", "1.0"), new XAttribute("mapping", "logical"),
                new XElement("entity", new XAttribute("name", view.Table),
                    new XElement("attribute", new XAttribute("name", idName)),
                    view.Columns.Select(c => new XElement("attribute", new XAttribute("name", c))),
                    new XElement("order", new XAttribute("attribute", view.Columns[0]), new XAttribute("descending", "false")),
                    new XElement("filter", new XAttribute("type", "and"),
                        new XElement("condition", new XAttribute("attribute", "statecode"), new XAttribute("operator", "eq"), new XAttribute("value", 0)))))),
            new XElement("IntroducedVersion", "1.0.0.0"),
            new XElement("LocalizedNames", Label("LocalizedName", view.Name)));
    }

    private static XElement Role(TestRole role) => new("Role",
        new XAttribute("id", IdOf("role", role.Name).ToString("B")), new XAttribute("name", role.Name),
        new XElement("RolePrivileges", role.Privileges.Select(p =>
            new XElement("RolePrivilege", new XAttribute("name", p.Privilege), new XAttribute("level", p.Level)))));

    private static XElement OptionSet(TestOptionSet set)
    {
        var props = set.Props ?? TestManagedProps.Open;
        return new("optionset", new XAttribute("Name", set.Name), new XAttribute("localizedName", set.Label), new XAttribute("description", ""),
            new XElement("OptionSetType", "picklist"),
            new XElement("IsGlobal", 1),
            new XElement("IntroducedVersion", "1.0.0.0"),
            Flag("IsCustomizable", props.IsCustomizable),
            new XElement("displaynames", Label("displayname", set.Label)),
            new XElement("Descriptions", Label("Description", "")),
            new XElement("options", set.Options.Select(o => new XElement("option",
                new XAttribute("value", o.Value), new XAttribute("ExternalValue", ""), new XAttribute("IsHidden", 0),
                new XElement("labels", Label("label", o.Label))))));
    }

    private static XElement Step(TestStep step)
    {
        var props = step.Props ?? TestManagedProps.Open;
        var name = $"{step.PluginTypeName}: {step.Message} of {step.Table}";
        return new("SdkMessageProcessingStep", new XAttribute("Name", name), new XAttribute("SdkMessageProcessingStepId", StepId(step).ToString("B")),
            new XElement("SdkMessageId", "{" + (step.Message == "Create" ? CreateMessage : UpdateMessage) + "}"),
            new XElement("PluginTypeName", $"{step.PluginTypeName}, {AssemblyFullName}"),
            new XElement("PluginTypeId", PluginTypeId(step.PluginTypeName).ToString("D")),
            new XElement("PrimaryEntity", step.Table),
            new XElement("AsyncAutoDelete", 0),
            new XElement("Description", name),
            new XElement("FilteringAttributes"),
            new XElement("InvocationSource", 0),
            new XElement("Mode", 0),
            new XElement("Rank", 1),
            new XElement("SdkMessageFilterId", IdOf("filter", $"{step.Message}:{step.Table}").ToString("D")),
            new XElement("EventHandlerTypeCode", 4602),
            new XElement("Stage", step.Stage),
            Flag("IsCustomizable", props.IsCustomizable),
            new XElement("IsHidden", 0),
            new XElement("SupportedDeployment", 0),
            new XElement("IntroducedVersion", "1.0"),
            step.Configuration is null ? null : new XElement("CustomConfiguration", step.Configuration),
            new XElement("StateCode", 0),
            new XElement("StatusCode", 1));
    }

    private static XElement WebResource(TestWebResource resource)
    {
        var props = resource.Props ?? TestManagedProps.Open;
        return new("WebResource",
            new XElement("WebResourceId", IdOf("webresource", resource.Name).ToString("B")),
            new XElement("Name", resource.Name),
            new XElement("DisplayName", resource.Name.Split('/')[^1]),
            new XElement("Description", $"The test script {resource.Name}."),
            new XElement("WebResourceType", 3),
            new XElement("IntroducedVersion", "1.0.0.0"),
            new XElement("IsEnabledForMobileClient", 0),
            new XElement("IsAvailableForMobileOffline", 0),
            Flag("IsCustomizable", props.IsCustomizable),
            Flag("CanBeDeleted", props.CanBeDeleted),
            new XElement("IsHidden", 0),
            new XElement("FileName", WebResourcePath(resource)));
    }

    // ------------------------------------------------------------------------------------------ helpers

    private static XElement Flag(string name, bool value) => new(name, value ? 1 : 0);

    private static XElement Label(string element, string text) =>
        new(element, new XAttribute("description", text), new XAttribute("languagecode", 1033));

    /// <summary><c>xm_item</c> → <c>xm_Item</c>: the schema name keeps the prefix and capitalizes the rest.</summary>
    private static string Schema(string logicalName)
    {
        var parts = logicalName.Split('_', 2);
        return parts.Length == 2 ? parts[0] + "_" + char.ToUpperInvariant(parts[1][0]) + parts[1][1..] : logicalName;
    }

    private static byte[] Bytes(XDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }
}
