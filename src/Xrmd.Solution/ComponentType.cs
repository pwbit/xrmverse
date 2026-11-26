namespace Xrmd.Solution;

/// <summary>
/// The <c>solutioncomponent.componenttype</c> values xrmverse tracks (Learn, SolutionComponent; AppModule 80 not verified),
/// and the tables whose rows are components.
/// </summary>
public static class ComponentType
{
    public const int Entity = 1;
    public const int Attribute = 2;
    public const int Relationship = 3;
    public const int OptionSet = 9;
    public const int EntityRelationship = 10;
    public const int Role = 20;
    public const int RolePrivilege = 21;
    public const int SavedQuery = 26;

    /// <summary>A classic process (<c>workflow</c> row; plan 2.14, ruling W22; from memory, checklist 1h).</summary>
    public const int Workflow = 29;
    public const int SystemForm = 60;
    public const int WebResource = 61;
    public const int SiteMap = 62;
    public const int FieldSecurityProfile = 70;
    public const int AppModule = 80;
    public const int PluginType = 90;
    public const int PluginAssembly = 91;
    public const int SdkMessageProcessingStep = 92;
    public const int SdkMessageProcessingStepImage = 93;

    /// <summary>
    /// A custom API (plan 15, C11): the object type code of <c>customapi</c>, as Dataverse gives solution-aware tables a
    /// component type equal to their type code (from memory, checked by the plan 15 checklist). Its parameters and properties
    /// travel with it and are not components of their own.
    /// </summary>
    public const int CustomApi = Xrmd.Metadata.CustomApiTables.ApiTypeCode;

    /// <summary>
    /// An environment variable definition (#167, ruling E3): the solution component type 380 of the Learn <c>solutioncomponent</c>
    /// choices, also the object type code of <c>environmentvariabledefinition</c>. Its value (381) travels in the definition's
    /// folder and is not a component of its own.
    /// </summary>
    public const int EnvironmentVariableDefinition = Xrmd.Metadata.EnvironmentVariableSchema.DefinitionTypeCode;

    /// <summary>
    /// A custom control, a code component (plan 3.5 UI-1d, ruling R3): the solution component type 66 "Custom Control" of the Learn
    /// <c>solutioncomponent</c> choices, assumed equal to the object type code of <c>customcontrol</c>. Its files (manifest, bundle,
    /// styles, resource strings) travel with it and are not components of their own.
    /// </summary>
    public const int CustomControl = Xrmd.Metadata.CustomControlSchema.TypeCode;

    /// <summary>
    /// A system chart (plan 3.5 UI-1f, ruling F2): the solution component type 59 "Chart" of the Learn <c>solutioncomponent</c>
    /// choices (<see cref="Xrmd.Metadata.AppModuleSchema.ComponentType.Chart"/> in an app), a <c>savedqueryvisualization</c> row.
    /// Like a view, it belongs to a table and travels under its entity (<c>Visualizations</c>).
    /// </summary>
    public const int SavedQueryVisualization = Xrmd.Metadata.ChartSchema.ComponentType;

    /// <summary>
    /// The <c>systemform.objecttypecode</c> of a system dashboard (plan 3.5 UI-1f, ruling F1): a form of type 0 that belongs to
    /// no table, read from the root <c>Dashboards</c> of a solution file and written back there.
    /// </summary>
    public const string DashboardObjectTypeCode = "none";

    /// <summary>The component types whose components are rows, with their table.</summary>
    private static readonly Dictionary<int, string> RowTables = new()
    {
        [Role] = "role",
        [SavedQuery] = "savedquery",
        [Workflow] = Xrmd.Metadata.WorkflowSchema.Table,
        [SystemForm] = "systemform",
        [WebResource] = "webresource",
        [SiteMap] = "sitemap",
        [FieldSecurityProfile] = "fieldsecurityprofile",
        [AppModule] = "appmodule",
        [PluginType] = "plugintype",
        [PluginAssembly] = "pluginassembly",
        [SdkMessageProcessingStep] = "sdkmessageprocessingstep",
        [SdkMessageProcessingStepImage] = "sdkmessageprocessingstepimage",
        [CustomApi] = Xrmd.Metadata.CustomApiTables.Api,
        [EnvironmentVariableDefinition] = Xrmd.Metadata.EnvironmentVariableSchema.Definition,
        [CustomControl] = Xrmd.Metadata.CustomControlSchema.Table,
        [SavedQueryVisualization] = Xrmd.Metadata.ChartSchema.Table,
    };

    private static readonly Dictionary<string, int> RowTypes = RowTables.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    /// <summary>The component types that are metadata (their <c>objectid</c> is a <c>MetadataId</c>).</summary>
    private static readonly HashSet<int> MetadataTypes = [Entity, Attribute, Relationship, OptionSet, EntityRelationship];

    /// <summary>True for a metadata component type (entity, attribute, relationship, option set).</summary>
    public static bool IsMetadata(int componentType) => MetadataTypes.Contains(componentType);

    /// <summary>The component type of a row of <paramref name="entityLogicalName"/>; null when its rows are not components.</summary>
    public static int? OfTable(string? entityLogicalName) =>
        entityLogicalName is not null && RowTypes.TryGetValue(entityLogicalName, out var type) ? type : null;

    /// <summary>
    /// True when a <c>workflow.type</c> value names a definition, the only process row that is a component: 1, or no value
    /// (a row is a definition unless it says otherwise).
    /// </summary>
    public static bool IsWorkflowDefinition(object? type) => type switch
    {
        null or DBNull => true,
        Microsoft.Xrm.Sdk.OptionSetValue option => option.Value == (int)Xrmd.Metadata.WorkflowSchema.Type.Definition,
        int value => value == (int)Xrmd.Metadata.WorkflowSchema.Type.Definition,
        _ => false,
    };

    /// <summary>The table of a row component type; null for a metadata type or an unsupported one.</summary>
    public static string? TableOf(int componentType) => RowTables.GetValueOrDefault(componentType);

    /// <summary>
    /// The type a component is recorded with: <see cref="Relationship"/> (3) and <see cref="EntityRelationship"/> (10) name
    /// the same components, recorded as 10 (the type the metadata messages use); any other type is itself.
    /// </summary>
    public static int Normalize(int componentType) => componentType == Relationship ? EntityRelationship : componentType;

    /// <summary>True for a type that xrmverse tracks: a metadata type or a row type of <see cref="TableOf"/>.</summary>
    public static bool IsSupported(int componentType) => IsMetadata(componentType) || RowTables.ContainsKey(componentType);
}
