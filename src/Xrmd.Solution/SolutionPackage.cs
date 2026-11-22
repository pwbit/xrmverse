using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Security;

namespace Xrmd.Solution;

/// <summary>
/// The content of a solution file (<c>solution.xml</c> + <c>customizations.xml</c> + the files they name), as SDK
/// metadata types and package records, in no particular application order (the importer applies them in the §7 order).
/// </summary>
/// <param name="Publisher">The publisher of <c>solution.xml</c>.</param>
/// <param name="Solution">The solution manifest of <c>solution.xml</c>.</param>
/// <param name="Entities">
/// The entities, each with its non-lookup attributes in <c>Attributes</c> (system attributes included, with
/// <c>IsCustomAttribute</c> false); their lookups are in <paramref name="Lookups"/>.
/// </param>
/// <param name="Lookups">
/// The lookup attributes of every entity (Lookup, Customer and Owner types, <c>EntityLogicalName</c> set), with their
/// <c>Targets</c> taken from the one-to-many relationships of the file (Owner: <c>systemuser</c>, <c>team</c>).
/// </param>
/// <param name="Relationships">The one-to-many and many-to-many relationships of <c>EntityRelationships</c>.</param>
/// <param name="OptionSets">The global option sets of <c>optionsets</c>.</param>
/// <param name="Roles">The security roles and their privileges.</param>
/// <param name="Opaque">Views, forms, system charts (plan 3.5 UI-1f), site maps and app modules, kept as XML (plan ruling).</param>
/// <param name="WebResources">The web resources with their content.</param>
/// <param name="Assemblies">The plug-in assemblies with their content and plug-in types.</param>
/// <param name="Steps">The SDK message processing steps with their images.</param>
/// <param name="Unsupported">The top-level nodes of components xrmverse does not support (workflows, templates, ...).</param>
/// <param name="EntityUnsupported">
/// The non-empty entity-level nodes xrmverse does not apply (<c>RibbonDiffXml</c>, <c>CustomControlDefaultConfigs</c>, …), with
/// their entity, kept so that an export can emit them again. <c>Visualizations</c> is applied since plan 3.5 UI-1f (charts in
/// <see cref="Opaque"/>).
/// </param>
/// <param name="SkippedAttributes">
/// The system attributes (<c>IsCustomField</c> 0) of a type xrmverse does not support, left out of the entities: the
/// platform owns them, so they do not fail the read (a custom attribute of such a type does).
/// </param>
/// <param name="RelationshipMenus">
/// The associated menu configuration of the relationships that have one, by SchemaName (ignoring case), kept opaque:
/// their <c>EntityRelationshipRoles</c> without the navigation property names (the navigation pane settings).
/// </param>
/// <param name="RelationshipDescriptions">
/// The <c>RelationshipDescription</c> of the relationships that have a non-empty one, by SchemaName (ignoring case). The SDK
/// has no relationship description and xrmverse does not keep it: the import logs it as a warning; the writer emits it.
/// </param>
/// <param name="Unmodelled">
/// What the file gives each component that xrmverse does not model (<see cref="UnmodelledXml"/>), by the keys of
/// <see cref="UnmodelledXml"/> (entities, attributes, relationships, global option sets, steps with their images, web
/// resources); kept for the export. Missing for a component: the writer writes the defaults of one created through the SDK.
/// </param>
/// <param name="MissingDependencies">
/// The <c>MissingDependencies</c> of <c>solution.xml</c> that the model can represent (both sides with a numeric
/// <c>type</c> and a <c>schemaName</c>); the others are skipped (informational: xrmverse checks dependencies itself).
/// </param>
/// <param name="KeptFiles">
/// The archive entries that no reader consumes (agents, formulas, the files of
/// unsupported components, ...), keyed by their path in the archive: kept for the export, not applied.
/// </param>
/// <param name="DeclaredMissingDependencies">How many <c>MissingDependency</c> elements <c>solution.xml</c> declares.</param>
/// <param name="FieldSecurityProfiles">
/// The field security profiles of <c>FieldSecurityProfiles</c> (component type 70) with their column permissions; never their
/// users or teams, which do not travel in a solution (plan 14, ruling F8).
/// </param>
/// <param name="CustomApis">
/// The custom APIs of <c>customapis/</c> (plan 15) with their request parameters and response properties. Their files are
/// no <paramref name="KeptFiles"/>; a file under <c>customapis/</c> outside the three paths of an API stays one.
/// </param>
/// <param name="RootComponents">
/// The root components of <c>solution.xml</c>: the type, the id (when given), else the schema name, else the
/// <c>parentId</c> (a role's root in recent exports), and the <c>behavior</c> (0 include subcomponents, 1 do not include subcomponents, 2 include as shell only).
/// </param>
/// <param name="Sources">
/// Each component's source as the reader saw it (plan 2.6, ruling M5: the payload of a layer), by component type and key
/// (<see cref="Layers.LayerPayload.Key"/>): its element of <c>customizations.xml</c> and the archive files it needs. Null for
/// a package that was not read from a file (an export).
/// </param>
public sealed record SolutionPackage(
    PackagePublisher Publisher,
    PackageSolution Solution,
    IReadOnlyList<EntityMetadata> Entities,
    IReadOnlyList<LookupAttributeMetadata> Lookups,
    IReadOnlyList<RelationshipMetadataBase> Relationships,
    IReadOnlyList<OptionSetMetadataBase> OptionSets,
    IReadOnlyList<PackageRole> Roles,
    IReadOnlyList<OpaqueComponent> Opaque,
    IReadOnlyList<PackageWebResource> WebResources,
    IReadOnlyList<PackageAssembly> Assemblies,
    IReadOnlyList<PackageStep> Steps,
    IReadOnlyList<XElement> Unsupported,
    IReadOnlyList<(string EntityLogicalName, XElement Xml)> EntityUnsupported,
    IReadOnlyList<(string EntityLogicalName, string LogicalName, string Type)> SkippedAttributes,
    IReadOnlyList<(int Type, string SchemaNameOrId, int Behavior)> RootComponents,
    IReadOnlyDictionary<string, XElement> RelationshipMenus,
    IReadOnlyDictionary<string, Label>? RelationshipDescriptions = null,
    IReadOnlyDictionary<(int Type, string Key), XElement>? Unmodelled = null,
    IReadOnlyList<PackageMissingDependency>? MissingDependencies = null,
    IReadOnlyDictionary<string, byte[]>? KeptFiles = null,
    int DeclaredMissingDependencies = 0,
    IReadOnlyList<PackageFieldSecurityProfile>? FieldSecurityProfiles = null,
    IReadOnlyList<PackageCustomApi>? CustomApis = null,
    IReadOnlyDictionary<(int Type, string Key), PackageComponentSource>? Sources = null)
{
    /// <summary>
    /// The classic processes of <c>Workflows</c> whose category xrmverse runs (0, 2 and 3; plan 2.14, rulings W2 and W22), each with
    /// its XAML. A process of any other category stays in the kept <c>Workflows</c> node of <see cref="Unsupported"/>.
    /// </summary>
    public IReadOnlyList<PackageWorkflow> Workflows { get; init; } = [];

    /// <summary>
    /// The user maps of <c>EntityMaps</c> (#143), table and column names lower-cased. The node itself also stays in
    /// <see cref="Unsupported"/>, so that an export writes it back as the file gave it; the import applies these maps and does not
    /// log the node as not applied.
    /// </summary>
    public IReadOnlyList<PackageEntityMap> EntityMaps { get; init; } = [];

    /// <summary>
    /// The environment variables of <c>environmentvariabledefinitions/</c> (#167), by schema name: each definition with the value
    /// the file carries. Their files are no <see cref="KeptFiles"/>; a file of the folder on another path stays one.
    /// </summary>
    public IReadOnlyList<PackageEnvironmentVariable> EnvironmentVariables { get; init; } = [];

    /// <summary>
    /// The custom controls of <c>CustomControls</c> (plan 3.5 UI-1d, ruling R3), each with the files of its manifest's folder. Those
    /// files are no <see cref="KeptFiles"/>.
    /// </summary>
    public IReadOnlyList<PackageCustomControl> CustomControls { get; init; } = [];
}

/// <summary>
/// A custom control (code component) of a solution file (plan 3.5 UI-1d, ruling R3): a <c>CustomControl</c> of
/// <c>customizations.xml</c> with the manifest its <c>FileName</c> names and the files of the manifest's folder.
/// </summary>
/// <param name="Name">The <c>Name</c> (the row's <c>name</c>, e.g. <c>prefix_Namespace.Constructor</c>).</param>
/// <param name="ManifestPath">The archive path of the manifest: the <c>FileName</c> without its leading slash.</param>
/// <param name="Manifest">The manifest as text (UTF-8, without a byte order mark).</param>
/// <param name="Version">The <c>version</c> of the manifest's <c>control</c> (empty when it has none).</param>
/// <param name="CompatibleDataTypes">
/// The <c>of-type</c> of each bound property, comma-joined in manifest order, then <c>dataset</c> for a control with a
/// <c>data-set</c>.
/// </param>
/// <param name="Files">
/// Every file of the manifest's folder by its path relative to that folder (the manifest itself included, e.g.
/// <c>ControlManifest.xml</c>, <c>bundle.js</c>, <c>strings/Name.1033.resx</c>), byte for byte.
/// </param>
/// <param name="Source">The <c>CustomControl</c> element as the file gives it (kept for the export).</param>
public sealed record PackageCustomControl(string Name, string ManifestPath, string Manifest, string Version, string CompatibleDataTypes,
    IReadOnlyDictionary<string, byte[]> Files, XElement Source)
{
    /// <summary>The archive folder of the control's files: the folder of <see cref="ManifestPath"/> (no trailing slash).</summary>
    public string Folder => ManifestPath.LastIndexOf('/') is > 0 and var slash ? ManifestPath[..slash] : "";
}

/// <summary>
/// An environment variable of a solution file (#167): <c>environmentvariabledefinitions/{schemaname}/environmentvariabledefinition.xml</c>
/// and, when present, the <c>environmentvariablevalues.json</c> beside it.
/// </summary>
/// <param name="SchemaName">The <c>schemaname</c> attribute of the root (the folder's name, ignoring case).</param>
/// <param name="Type">The <c>type</c> (a <see cref="Xrmd.Metadata.VariableType"/>; String when missing).</param>
/// <param name="DefaultValue">The <c>defaultvalue</c>; null when missing or empty.</param>
/// <param name="IsRequired">The <c>isrequired</c> flag (false when missing).</param>
/// <param name="SecretStore">The <c>secretstore</c> (0 Azure Key Vault, also when missing; 1 Microsoft Dataverse).</param>
/// <param name="ValueSchema">The <c>valueschema</c>; null when missing or empty.</param>
/// <param name="IntroducedVersion">The <c>introducedversion</c>; null when missing or empty.</param>
/// <param name="Hint">The <c>hint</c>; null when missing or empty.</param>
/// <param name="Value">The <c>value</c> of the value file; null without one or when it gives none.</param>
/// <param name="HasValueFile">True when the folder has <c>environmentvariablevalues.json</c>.</param>
/// <param name="Source">
/// What the file gives the definition that xrmverse does not model (<see cref="UnmodelledXml.Capture"/>): the root with each
/// modelled child replaced by a marker, and as they are <c>iscustomizable</c>, the full <c>displayname</c> and <c>description</c>
/// label sets and any other child. A definition that did not come from a file has the template of
/// <c>EnvironmentVariableReader.NewSource</c>.
/// </param>
public sealed record PackageEnvironmentVariable(string SchemaName, int Type, string? DefaultValue, bool IsRequired, int SecretStore,
    string? ValueSchema, string? IntroducedVersion, string? Hint, string? Value, bool HasValueFile, XElement Source)
{
    /// <summary>
    /// The <c>iscustomizable</c> of the value file: "1" (an unmanaged export), "0" (a managed one), or null when the value file
    /// gives none or there is no value file. Written back as it was read.
    /// </summary>
    public string? ValueIsCustomizable { get; init; }
}

/// <summary>An <c>EntityMap</c> of <c>customizations.xml</c> (#143): the maps from a source table to a target table.</summary>
/// <param name="Source">The <c>EntitySource</c> (logical name).</param>
/// <param name="Target">The <c>EntityTarget</c> (logical name).</param>
/// <param name="Attributes">The <c>AttributeMaps/AttributeMap</c> elements, in file order.</param>
public sealed record PackageEntityMap(string Source, string Target, IReadOnlyList<PackageAttributeMap> Attributes);

/// <summary>An <c>AttributeMap</c> of an entity map: the <c>AttributeSource</c> and <c>AttributeTarget</c> column names.</summary>
public sealed record PackageAttributeMap(string Source, string Target);

/// <summary>
/// A classic process of <c>customizations.xml</c> (a <c>Workflow</c> element of category 0, 2 or 3; plan 2.14, ruling W22).
/// </summary>
/// <param name="Id">The <c>WorkflowId</c> attribute.</param>
/// <param name="Name">The <c>Name</c> attribute.</param>
/// <param name="Category">0 Workflow, 2 Business Rule, 3 Action.</param>
/// <param name="Mode">0 Background, 1 Real-time (0 when missing).</param>
/// <param name="Scope">1 User, 2 Business Unit, 3 Parent: Child Business Units, 4 Organization (1 when missing).</param>
/// <param name="PrimaryEntity">The <c>PrimaryEntity</c> as written (<c>none</c> for a global action); null when missing.</param>
/// <param name="XamlPath">The <c>XamlFileName</c> as written (a path from the archive root with a leading <c>/</c>).</param>
/// <param name="Xaml">The text of that file (UTF-8, its byte order mark kept), stored and exported byte for byte.</param>
/// <param name="StateCode">The <c>StateCode</c> the file asks for: 0 Draft, 1 Activated (0 when missing).</param>
/// <param name="Fields">
/// The other child elements with a text value (<c>Type</c>, <c>TriggerOnCreate</c>, <c>CreateStage</c>, <c>UniqueName</c>, …), by their
/// name as written (looked up ignoring case), kept for the row and the export.
/// </param>
/// <param name="ProcessTriggers">The <c>ProcessTriggers/ProcessTrigger</c> elements (a business rule's), copies.</param>
/// <param name="Source">The <c>Workflow</c> element as the file gives it (a copy): the export writes it again with the values
/// xrmverse keeps set in it.</param>
public sealed record PackageWorkflow(Guid Id, string Name, int Category, int Mode, int Scope, string? PrimaryEntity, string XamlPath,
    string Xaml, int StateCode, IReadOnlyDictionary<string, string> Fields, IReadOnlyList<XElement> ProcessTriggers, XElement Source);

/// <summary>
/// The source of one component of a solution file (plan 2.6, ruling M5): its element as the reader saw it and the archive
/// files it needs (a web resource's content, an assembly's bytes, a custom API's <c>customapis/</c> files).
/// </summary>
/// <param name="SourceXml">
/// The component's element of <c>customizations.xml</c> (the <c>Entity</c> of a table, the <c>attribute</c> of a column, the
/// <c>EntityRelationship</c>, <c>optionset</c>, <c>savedquery</c>, <c>systemform</c>, <c>Role</c>, …); for a custom API the
/// root of its <c>customapi.xml</c>. The element of the file read, not a copy: never change it.
/// </param>
/// <param name="Files">The archive entries the component needs, by their path in the archive (no leading <c>/</c>).</param>
/// <param name="Entity">The logical name of the table of a column, view or form; null otherwise (a system dashboard has none).</param>
/// <param name="FormType">The <c>type</c> of the <c>forms</c> element of a form; null otherwise.</param>
public sealed record PackageComponentSource(XElement SourceXml, IReadOnlyDictionary<string, byte[]> Files, string? Entity = null,
    string? FormType = null);

/// <summary>A custom API (<c>customapis/{uniquename}/customapi.xml</c>).</summary>
/// <param name="UniqueName">The <c>uniquename</c> attribute of the root, the message name.</param>
/// <param name="Name">The <c>name</c>.</param>
/// <param name="BindingType">The <c>bindingtype</c> as written: 0 global, 1 entity, 2 entity collection (checked at import).</param>
/// <param name="BoundEntity">The <c>boundentitylogicalname</c>; null when missing or empty.</param>
/// <param name="AllowedStepType">The <c>allowedcustomprocessingsteptype</c> as written: 0 none, 1 asynchronous only, 2 both.</param>
/// <param name="IsFunction">The <c>isfunction</c> flag.</param>
/// <param name="IsPrivate">The <c>isprivate</c> flag.</param>
/// <param name="WorkflowEnabled">The <c>workflowsdkstepenabled</c> flag.</param>
/// <param name="ExecutePrivilegeName">The <c>executeprivilegename</c>; null when missing or empty.</param>
/// <param name="PluginTypeKey">
/// The plug-in type of the main operation: the <c>plugintypeexportkey</c> of an export, or the nested <c>plugintypeid</c>
/// of a hand-written file; null when missing or empty.
/// </param>
/// <param name="DisplayName">The labels of <c>displayname</c>, every language of the file.</param>
/// <param name="Description">The labels of <c>description</c>, every language of the file.</param>
/// <param name="RequestParameters">The request parameters, by unique name.</param>
/// <param name="ResponseProperties">The response properties, by unique name.</param>
/// <param name="Kept">
/// What the file gives the API that xrmverse does not model (<see cref="UnmodelledXml.Capture"/>): the root with each modelled
/// child replaced by a marker, and as they are <c>iscustomizable</c>, the full <c>description</c> and <c>displayname</c>
/// label sets (C11) and any other child, such as a Power Fx <c>powerfxruleid</c> the import refuses. Null for an API with
/// nothing kept: the writer writes <c>iscustomizable</c> 1 and one label in the base language.
/// </param>
public sealed record PackageCustomApi(string UniqueName, string Name, int BindingType, string? BoundEntity, int AllowedStepType,
    bool IsFunction, bool IsPrivate, bool WorkflowEnabled, string? ExecutePrivilegeName, Guid? PluginTypeKey, Label DisplayName,
    Label Description, IReadOnlyList<PackageCustomApiField> RequestParameters, IReadOnlyList<PackageCustomApiField> ResponseProperties,
    XElement? Kept);

/// <summary>
/// A request parameter (<c>customapirequestparameters/{uniquename}/customapirequestparameter.xml</c>) or a response property
/// (<c>customapiresponseproperties/{uniquename}/customapiresponseproperty.xml</c>) of a custom API.
/// </summary>
/// <param name="UniqueName">The <c>uniquename</c> attribute of the root.</param>
/// <param name="Name">The <c>name</c>.</param>
/// <param name="Type">The <c>type</c> as written (<c>customapifieldtype</c>, 0 to 12; checked at import).</param>
/// <param name="LogicalEntityName">The <c>logicalentityname</c>; null when missing or empty.</param>
/// <param name="IsOptional">
/// The <c>isoptional</c> flag of a request parameter (false when missing); null for a response property, whose
/// <c>isoptional</c>, if a file has one, stays in <paramref name="Kept"/> for the import to refuse.
/// </param>
/// <param name="DisplayName">The labels of <c>displayname</c>.</param>
/// <param name="Description">The labels of <c>description</c>.</param>
/// <param name="Kept">What xrmverse does not model, as for <see cref="PackageCustomApi.Kept"/>; null when nothing is kept.</param>
public sealed record PackageCustomApiField(string UniqueName, string Name, int Type, string? LogicalEntityName, bool? IsOptional,
    Label DisplayName, Label Description, XElement? Kept);

/// <summary>A field security profile (<c>FieldSecurityProfile</c> of <c>customizations.xml</c>).</summary>
/// <param name="Id">The <c>fieldsecurityprofileid</c>.</param>
/// <param name="Name">The profile name.</param>
/// <param name="Description">The description; null when the file gives none.</param>
/// <param name="Permissions">The permissions of the profile, one per column.</param>
/// <param name="UnwrappedPermissions">
/// How many <c>FieldPermission</c> elements sit directly under the profile, without the <c>FieldPermissions</c> element a
/// Dataverse export puts around them: not read (the import log warns about them).
/// </param>
public sealed record PackageFieldSecurityProfile(Guid Id, string Name, string? Description, IReadOnlyList<PackageFieldPermission> Permissions,
    int UnwrappedPermissions = 0);

/// <summary>A column permission of a field security profile (<c>FieldPermission</c>).</summary>
/// <param name="Entity">The table logical name (<c>entityname</c>).</param>
/// <param name="Attribute">The column logical name (<c>attributelogicalname</c>).</param>
/// <param name="CanRead">0 not allowed, 4 allowed.</param>
/// <param name="CanCreate">0 not allowed, 4 allowed.</param>
/// <param name="CanUpdate">0 not allowed, 4 allowed.</param>
/// <param name="CanReadUnmasked">Kept as written; xrmverse does not support masking (ruling F5).</param>
public sealed record PackageFieldPermission(string Entity, string Attribute, int CanRead, int CanCreate, int CanUpdate, int CanReadUnmasked);

/// <summary>A <c>MissingDependency</c> of <c>solution.xml</c>: a component the solution needs but does not hold.</summary>
/// <param name="Required">The component needed (<c>Required</c>).</param>
/// <param name="Dependent">The component of the solution that needs it (<c>Dependent</c>).</param>
public sealed record PackageMissingDependency(PackageDependencyNode Required, PackageDependencyNode Dependent);

/// <summary>A side of a <see cref="PackageMissingDependency"/> (the XML attributes of <c>Required</c> or <c>Dependent</c>).</summary>
/// <param name="Type">The component type (<c>type</c>).</param>
/// <param name="SchemaName">The component's name (<c>schemaName</c>).</param>
/// <param name="DisplayName">Its display name (<c>displayName</c>).</param>
/// <param name="Solution">The solution that holds it (<c>solution</c>, a required component's).</param>
/// <param name="ParentSchemaName">The name of its entity (<c>parentSchemaName</c>, an attribute's).</param>
/// <param name="ParentDisplayName">The display name of its entity (<c>parentDisplayName</c>).</param>
public sealed record PackageDependencyNode(int Type, string SchemaName, string? DisplayName, string? Solution = null,
    string? ParentSchemaName = null, string? ParentDisplayName = null);

/// <summary>The publisher of the solution.</summary>
/// <param name="UniqueName"><c>Publisher/UniqueName</c>.</param>
/// <param name="CustomizationPrefix"><c>CustomizationPrefix</c> (e.g. <c>xv</c>).</param>
/// <param name="CustomizationOptionValuePrefix"><c>CustomizationOptionValuePrefix</c> (e.g. 10000).</param>
/// <param name="FriendlyName">The <c>LocalizedNames</c>.</param>
/// <param name="Xml">
/// A copy of the file's <c>Publisher</c> element, for the details xrmverse keeps only to emit them again (descriptions in
/// every language, e-mail, web site, addresses); null when the publisher was not read from a file.
/// </param>
public sealed record PackagePublisher(string UniqueName, string CustomizationPrefix, int CustomizationOptionValuePrefix, Label FriendlyName,
    XElement? Xml = null);

/// <summary>The solution manifest.</summary>
/// <param name="UniqueName"><c>SolutionManifest/UniqueName</c>.</param>
/// <param name="Version"><c>Version</c> as written (e.g. <c>1.0.0.0</c>).</param>
/// <param name="Managed"><c>Managed</c> = 1.</param>
/// <param name="FriendlyName">The <c>LocalizedNames</c>.</param>
/// <param name="LanguageCode">The base language of the file (<c>languagecode</c> of the root, 1033 when missing).</param>
/// <param name="Description">The <c>Descriptions</c> of the manifest; null when none is given.</param>
public sealed record PackageSolution(string UniqueName, string Version, bool Managed, Label FriendlyName, int LanguageCode,
    Label? Description = null);

/// <summary>A security role and its privileges, by name (resolved against the catalog at import).</summary>
/// <param name="Id">The <c>roleid</c>.</param>
/// <param name="Name">The role name.</param>
/// <param name="Privileges">Each privilege name (e.g. <c>prvCreatexv_Project</c>) with its depth.</param>
public sealed record PackageRole(Guid Id, string Name, IReadOnlyList<(string Name, DepthMask Depth)> Privileges);

/// <summary>A component kept as XML: a view, a form (a system dashboard among them, plan 3.5 UI-1f), a system chart, a site map or an app module.</summary>
/// <param name="Type">The <see cref="ComponentType"/>.</param>
/// <param name="Id">The id (GUID, <c>D</c> format) or, for site maps and app modules, the unique name.</param>
/// <param name="EntityLogicalName">
/// The entity of a view, form or chart; null otherwise, and for a system dashboard (a form of no table, whose element is a root
/// <c>Dashboard</c>, ruling F1).
/// </param>
/// <param name="Xml">A copy of the component's element.</param>
/// <param name="FormType">
/// For a form, the <c>type</c> of its parent <c>&lt;forms&gt;</c> element as written (<c>main</c>, <c>quick</c>,
/// <c>card</c>, …); null otherwise.
/// </param>
public sealed record OpaqueComponent(int Type, string Id, string? EntityLogicalName, XElement Xml, string? FormType = null);

/// <summary>A web resource (stored, not served).</summary>
/// <param name="Id">The <c>webresourceid</c>.</param>
/// <param name="Name">The unique name (e.g. <c>xv_/scripts/project.js</c>).</param>
/// <param name="DisplayName">The display name, if any.</param>
/// <param name="WebResourceType">The <c>webresourcetype</c> (3 = JScript).</param>
/// <param name="Content">The bytes of the file the element names.</param>
/// <param name="Description">The description, if any.</param>
public sealed record PackageWebResource(Guid Id, string Name, string? DisplayName, int WebResourceType, byte[] Content,
    string? Description = null);

/// <summary>A plug-in assembly.</summary>
/// <param name="Id">The <c>pluginassemblyid</c>.</param>
/// <param name="Name">The simple name (first part of <paramref name="FullName"/>).</param>
/// <param name="FullName">The display name (<c>Name, Version=…, Culture=…, PublicKeyToken=…</c>).</param>
/// <param name="IsolationMode">1 none, 2 sandbox.</param>
/// <param name="SourceType">0 database.</param>
/// <param name="Content">The bytes of the dll the element names.</param>
/// <param name="Types">The plug-in types.</param>
public sealed record PackageAssembly(Guid Id, string Name, string FullName, int IsolationMode, int SourceType, byte[] Content,
    IReadOnlyList<PackagePluginType> Types);

/// <summary>A plug-in type of an assembly.</summary>
/// <param name="Id">The <c>plugintypeid</c>.</param>
/// <param name="TypeName">The full type name (<c>Name</c>), e.g. <c>Xrmd.TestPlugins.SetAttributePlugin</c>.</param>
/// <param name="AssemblyQualifiedName">The assembly-qualified type name.</param>
/// <param name="FriendlyName">The friendly name, if any.</param>
/// <param name="WorkflowActivityGroupName">The <c>WorkflowActivityGroupName</c> of a custom workflow activity (plan 2.14, W17); null for a
/// plug-in.</param>
public sealed record PackagePluginType(Guid Id, string TypeName, string AssemblyQualifiedName, string? FriendlyName,
    string? WorkflowActivityGroupName = null);

/// <summary>An SDK message processing step.</summary>
/// <param name="Id">The <c>sdkmessageprocessingstepid</c>.</param>
/// <param name="Name">The step name.</param>
/// <param name="Description">The description, if any.</param>
/// <param name="SdkMessageId">The <c>sdkmessageid</c> as written by the exporting organization.</param>
/// <param name="SdkMessageFilterId">The <c>sdkmessagefilterid</c> as written, if any.</param>
/// <param name="PrimaryEntity">The entity logical name; null for a step on every entity (<c>none</c> or missing).</param>
/// <param name="PluginTypeId">The <c>plugintypeid</c>.</param>
/// <param name="PluginTypeName">The assembly-qualified plug-in type name.</param>
/// <param name="Stage">10, 20, 40 (or 30 for the main operation of a custom API).</param>
/// <param name="Mode">0 synchronous, 1 asynchronous.</param>
/// <param name="Rank">The execution order.</param>
/// <param name="InvocationSource">The (deprecated) invocation source.</param>
/// <param name="SupportedDeployment">0 server.</param>
/// <param name="AsyncAutoDelete">Whether a successful asynchronous job is deleted.</param>
/// <param name="FilteringAttributes">The comma-separated filtering attributes; null when empty.</param>
/// <param name="Configuration">The unsecure configuration (<c>CustomConfiguration</c>); null when empty.</param>
/// <param name="ImpersonatingUserId">The user the step runs as, if any.</param>
/// <param name="Disabled">True when <c>StateCode</c> is 1.</param>
/// <param name="Images">The step images.</param>
public sealed record PackageStep(Guid Id, string Name, string? Description, Guid SdkMessageId, Guid? SdkMessageFilterId,
    string? PrimaryEntity, Guid PluginTypeId, string PluginTypeName, int Stage, int Mode, int Rank, int InvocationSource,
    int SupportedDeployment, bool AsyncAutoDelete, string? FilteringAttributes, string? Configuration, Guid? ImpersonatingUserId,
    bool Disabled, IReadOnlyList<PackageStepImage> Images);

/// <summary>An image of a step.</summary>
/// <param name="Id">The <c>sdkmessageprocessingstepimageid</c>.</param>
/// <param name="Name">The image name.</param>
/// <param name="EntityAlias">The key of the image in the execution context.</param>
/// <param name="ImageType">0 pre-image, 1 post-image, 2 both.</param>
/// <param name="MessagePropertyName">The message property (e.g. <c>Target</c>, <c>Id</c>).</param>
/// <param name="Attributes">The comma-separated attributes; null for every attribute.</param>
public sealed record PackageStepImage(Guid Id, string Name, string EntityAlias, int ImageType, string MessagePropertyName, string? Attributes);
