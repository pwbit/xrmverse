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
/// Reads <c>solution.xml</c> and <c>customizations.xml</c> of an unmanaged solution file (format of
/// <c>CustomizationsSolution.xsd</c> 9.0.0.2090 and of the Dataverse exports) into a <see cref="SolutionPackage"/>.
/// Unknown elements of the supported components are ignored, missing optional elements take their defaults, top-level
/// nodes of unsupported components are kept in <see cref="SolutionPackage.Unsupported"/>. What xrmverse cannot represent
/// faithfully (an attribute type it does not support, an unknown privilege depth, a missing file, …) is a 400
/// InvalidArgument naming the component: nothing is dropped silently. No database access.
/// </summary>
public static class CustomizationsReader
{
    private const int DefaultLanguageCode = 1033;
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Reads the package.</summary>
    /// <param name="solution">The <c>solution.xml</c> document.</param>
    /// <param name="customizations">The <c>customizations.xml</c> document.</param>
    /// <param name="files">The other entries of the archive, as <see cref="SolutionZip.Read"/> returns them.</param>
    /// <exception cref="XrmdFaultException">400 InvalidArgument when the content cannot be read faithfully.</exception>
    public static SolutionPackage Read(XDocument solution, XDocument customizations, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(customizations);
        ArgumentNullException.ThrowIfNull(files);

        var (publisher, manifest, rootComponents) = ReadManifest(solution);
        var missing = ReadMissingDependencies(solution);
        var reader = new Reader(manifest.LanguageCode, publisher.CustomizationPrefix, files);
        reader.ReadCustomizations(customizations);
        var (customApis, customApiFiles) = CustomApiReader.Read(files, manifest.LanguageCode);
        reader.Consume(customApiFiles);
        reader.AddCustomApiSources(customApis, customApiFiles);
        var (environmentVariables, environmentVariableFiles) = EnvironmentVariableReader.Read(files, manifest.LanguageCode);
        reader.Consume(environmentVariableFiles);
        reader.AddEnvironmentVariableSources(environmentVariables, environmentVariableFiles);

        return new SolutionPackage(publisher, manifest, reader.Entities, reader.Lookups, reader.Relationships, reader.OptionSets,
            reader.Roles, reader.Opaque, reader.WebResources, reader.Assemblies, reader.Steps, reader.Unsupported, reader.EntityUnsupported,
            reader.SkippedAttributes, rootComponents, reader.RelationshipMenus, reader.RelationshipDescriptions, reader.Unmodelled,
            missing.Readable, reader.KeptFiles(), missing.Declared, reader.FieldSecurityProfiles, customApis, reader.Sources)
        {
            Workflows = reader.Workflows,
            EntityMaps = reader.EntityMaps,
            EnvironmentVariables = environmentVariables,
            CustomControls = reader.CustomControls,
        };
    }

    /// <summary>
    /// The <c>MissingDependencies</c> of <c>solution.xml</c> the model can represent, and how many the file declares. A
    /// side of a real export may have no <c>schemaName</c> (only <c>id</c>, dotted keys such as <c>id.uniquename</c>) or a
    /// non-numeric <c>type</c> (<c>connectionreference</c>, <c>SettingDefinition</c>): that dependency is skipped, never a
    /// failure, because the import does not use the declared list (it checks the dependencies itself).
    /// </summary>
    private static (List<PackageMissingDependency> Readable, int Declared) ReadMissingDependencies(XDocument solution)
    {
        static PackageDependencyNode? Node(XElement dependency, string side)
        {
            if (dependency.Element(side) is not { } element) return null;
            var name = NonEmpty((string?)element.Attribute("schemaName"));
            if (!int.TryParse((string?)element.Attribute("type"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var type) || name is null)
                return null;
            return new PackageDependencyNode(type, name, NonEmpty((string?)element.Attribute("displayName")),
                NonEmpty((string?)element.Attribute("solution")), NonEmpty((string?)element.Attribute("parentSchemaName")),
                NonEmpty((string?)element.Attribute("parentDisplayName")));
        }

        var declared = (solution.Root?.Element("SolutionManifest")?.Element("MissingDependencies")?.Elements("MissingDependency") ?? []).ToList();
        var readable = declared
            .Select(d => (Required: Node(d, "Required"), Dependent: Node(d, "Dependent")))
            .Where(d => d.Required is not null && d.Dependent is not null)
            .Select(d => new PackageMissingDependency(d.Required!, d.Dependent!))
            .ToList();
        return (readable, declared.Count);
    }

    private static (PackagePublisher, PackageSolution, IReadOnlyList<(int, string, int)>) ReadManifest(XDocument solution)
    {
        var root = solution.Root ?? throw Invalid("solution.xml is empty.");
        var manifest = root.Element("SolutionManifest") ?? throw Invalid("solution.xml has no SolutionManifest.");
        var languageCode = IntAttribute(root, "languagecode") ?? DefaultLanguageCode;

        var publisherElement = manifest.Element("Publisher") ?? throw Invalid("solution.xml has no Publisher.");
        var publisher = new PackagePublisher(
            Required(publisherElement, "UniqueName", "the publisher"),
            Required(publisherElement, "CustomizationPrefix", "the publisher"),
            Int(publisherElement, "CustomizationOptionValuePrefix")
            ?? throw Invalid("solution.xml: the publisher has no CustomizationOptionValuePrefix."),
            Labels(publisherElement.Element("LocalizedNames"), "LocalizedName", languageCode),
            new XElement(publisherElement));

        var package = new PackageSolution(
            Required(manifest, "UniqueName", "the solution"),
            Required(manifest, "Version", "the solution"),
            Bool(manifest, "Managed") ?? false,
            Labels(manifest.Element("LocalizedNames"), "LocalizedName", languageCode),
            languageCode,
            manifest.Element("Descriptions") is { } descriptions ? Labels(descriptions, "Description", languageCode) : null);

        var rootComponents = new List<(int, string, int)>();
        foreach (var component in manifest.Element("RootComponents")?.Elements("RootComponent") ?? [])
        {
            var type = IntAttribute(component, "type") ?? throw Invalid("solution.xml: a RootComponent has no type.");
            var id = GuidAttribute(component, "id");
            // Recent exports give a role's root by its parentId alone (the id of a role the file holds).
            var key = id?.ToString("D") ?? NonEmpty((string?)component.Attribute("schemaName"))
                ?? GuidAttribute(component, "parentId")?.ToString("D")
                ?? throw Invalid($"solution.xml: a RootComponent of type {type} has neither an id, a schemaName nor a parentId.");
            rootComponents.Add((type, key, IntAttribute(component, "behavior") ?? 0));
        }

        return (publisher, package, rootComponents);
    }

    /// <summary>The elements of the ribbon template every export writes for an entity without ribbon customizations.</summary>
    private static readonly HashSet<string> EmptyRibbonSkeleton = new(StringComparer.Ordinal)
    {
        "CustomActions", "Templates", "RibbonTemplates", "CommandDefinitions", "RuleDefinitions", "TabDisplayRules",
        "DisplayRules", "EnableRules", "LocLabels",
    };

    /// <summary>
    /// Whether an entity-level node (a child of <c>Entity</c>) has content. False for an empty node: no child element, or a <c>RibbonDiffXml</c> made only of the empty
    /// template (<c>&lt;CustomActions /&gt;</c>, <c>&lt;RibbonTemplates Id="Mscrm.Templates"&gt;</c>, …).
    /// </summary>
    public static bool HasContent(XElement part) =>
        part.Name.LocalName == "RibbonDiffXml"
            ? part.Descendants().Any(e => !EmptyRibbonSkeleton.Contains(e.Name.LocalName) || !string.IsNullOrWhiteSpace(e.Value))
            : part.HasElements;

    /// <summary>The state of one read of <c>customizations.xml</c>.</summary>
    private sealed class Reader(int languageCode, string publisherPrefix, IReadOnlyDictionary<string, byte[]> files)
    {
        public List<EntityMetadata> Entities { get; } = [];
        public List<LookupAttributeMetadata> Lookups { get; } = [];
        public List<RelationshipMetadataBase> Relationships { get; } = [];
        public Dictionary<string, XElement> RelationshipMenus { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Label> RelationshipDescriptions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(int Type, string Key), XElement> Unmodelled { get; } = new();
        public List<OptionSetMetadataBase> OptionSets { get; } = [];
        public List<PackageRole> Roles { get; } = [];
        public List<PackageFieldSecurityProfile> FieldSecurityProfiles { get; } = [];
        public List<OpaqueComponent> Opaque { get; } = [];
        public List<PackageWebResource> WebResources { get; } = [];
        public List<PackageAssembly> Assemblies { get; } = [];
        public List<PackageStep> Steps { get; } = [];
        public List<PackageWorkflow> Workflows { get; } = [];
        public List<PackageEntityMap> EntityMaps { get; } = [];
        public List<PackageCustomControl> CustomControls { get; } = [];
        public List<XElement> Unsupported { get; } = [];
        public List<(string, XElement)> EntityUnsupported { get; } = [];
        public List<(string, string, string)> SkippedAttributes { get; } = [];

        /// <summary>Each component's source (plan 2.6, ruling M5), by <see cref="Layers.LayerPayload.Key"/>.</summary>
        public Dictionary<(int Type, string Key), PackageComponentSource> Sources { get; } = new();

        private static readonly IReadOnlyDictionary<string, byte[]> NoFiles = new Dictionary<string, byte[]>();

        private void Source(int type, string key, XElement element, IReadOnlyDictionary<string, byte[]>? files = null, string? entity = null,
            string? formType = null) =>
            Sources[Layers.LayerPayload.Key(type, key)] = new PackageComponentSource(element, files ?? NoFiles, entity, formType);

        /// <summary>The file a <c>FileName</c> element names, as a one-entry dictionary (its bytes were read already).</summary>
        private IReadOnlyDictionary<string, byte[]> FileOf(XElement element, string name = "FileName")
        {
            var path = ((string?)element.Element(name) ?? "").Trim().Replace('\\', '/').TrimStart('/');
            return files.TryGetValue(path, out var content) ? new Dictionary<string, byte[]> { [path] = content } : NoFiles;
        }

        /// <summary>
        /// The formula file an attribute's source carries: for a formula column (plan 2.12c) its own line of its table's
        /// <c>FormulaDefinitions.yaml</c> (<see cref="FormulaFiles.Write"/>) at that file's path, for any other column the file its
        /// <c>FormulaDefinitionFileName</c> names (<see cref="FileOf"/>).
        /// </summary>
        private IReadOnlyDictionary<string, byte[]> FormulaFileOf(XElement element, AttributeMetadata attribute)
        {
            if (attribute.SourceType != ComputedColumnRules.Formula || ComputedColumnRules.FormulaOf(attribute) is not { } formula)
                return FileOf(element, "FormulaDefinitionFileName");
            var path = ((string?)element.Element("FormulaDefinitionFileName") ?? "").Trim().Replace('\\', '/').TrimStart('/');
            return new Dictionary<string, byte[]>
            {
                [path] = new System.Text.UTF8Encoding(false).GetBytes(FormulaFiles.Write([(attribute.LogicalName, formula)])),
            };
        }

        /// <summary>The sources of the custom APIs: each API's <c>customapi.xml</c> root and every file of its folder that was read.</summary>
        public void AddCustomApiSources(IReadOnlyList<PackageCustomApi> apis, IReadOnlyList<string> read)
        {
            foreach (var folder in read.GroupBy(path => path.Split('/')[1], StringComparer.OrdinalIgnoreCase))
            {
                var apiPath = folder.FirstOrDefault(path => path.Split('/').Length == 3);
                if (apiPath is null) continue;
                var root = XDocument.Parse(System.Text.Encoding.UTF8.GetString(files[apiPath]).TrimStart('\uFEFF')).Root!;
                var name = (string?)root.Attribute("uniquename");
                if (name is null || !apis.Any(a => string.Equals(a.UniqueName, name, StringComparison.OrdinalIgnoreCase))) continue;
                Source(ComponentType.CustomApi, name, root, folder.ToDictionary(path => path, path => files[path], StringComparer.Ordinal));
            }
        }

        /// <summary>
        /// The layer payload source of each environment variable (#167): its definition element with the files of its folder
        /// (<see cref="EnvironmentVariableReader.Read"/> has read and checked them).
        /// </summary>
        public void AddEnvironmentVariableSources(IReadOnlyList<PackageEnvironmentVariable> variables, IReadOnlyList<string> read)
        {
            foreach (var variable in variables)
            {
                var folder = read.Where(path => string.Equals(path.Split('/')[1], variable.SchemaName, StringComparison.OrdinalIgnoreCase)).ToList();
                var definition = folder.First(path => path.EndsWith("/" + EnvironmentVariableReader.DefinitionFile, StringComparison.OrdinalIgnoreCase));
                var root = XDocument.Parse(System.Text.Encoding.UTF8.GetString(files[definition]).TrimStart('\uFEFF')).Root!;
                Source(ComponentType.EnvironmentVariableDefinition, variable.SchemaName, root,
                    folder.ToDictionary(path => path, path => files[path], StringComparer.Ordinal));
            }
        }

        public void ReadCustomizations(XDocument customizations)
        {
            var root = customizations.Root;
            if (root is null || root.Name.LocalName != "ImportExportXml")
                throw Invalid("customizations.xml is not a solution customizations file (its root is not ImportExportXml).");

            var relationships = new List<XElement>();
            foreach (var node in root.Elements())
            {
                switch (node.Name.LocalName)
                {
                    case "Entities": ReadEntities(node); break;
                    case "EntityRelationships": relationships.AddRange(node.Elements("EntityRelationship")); break;
                    case "optionsets":
                        foreach (var optionSet in node.Elements("optionset"))
                        {
                            var read = OptionSet(optionSet, isGlobal: true);
                            Source(ComponentType.OptionSet, read.Name ?? "", optionSet);
                            Unmodelled[UnmodelledXml.OptionSetKey(read.Name ?? "")] = UnmodelledXml.Capture(optionSet, UnmodelledXml.OptionSetShape);
                            OptionSets.Add(read);
                        }
                        break;
                    case "Roles":
                        foreach (var element in node.Elements("Role"))
                        {
                            var role = Role(element);
                            Source(ComponentType.Role, role.Id.ToString("D"), element);
                            Roles.Add(role);
                        }
                        break;
                    case "FieldSecurityProfiles":
                        foreach (var element in node.Elements("FieldSecurityProfile"))
                        {
                            var profile = FieldSecurityProfile(element);
                            Source(ComponentType.FieldSecurityProfile, profile.Id.ToString("D"), element);
                            FieldSecurityProfiles.Add(profile);
                        }
                        break;
                    case "SolutionPluginAssemblies":
                        foreach (var element in node.Elements("PluginAssembly"))
                        {
                            var assembly = Assembly(element);
                            Unmodelled[UnmodelledXml.AssemblyKey(assembly.Id)] = UnmodelledXml.Capture(element, UnmodelledXml.AssemblyShape);
                            Source(ComponentType.PluginAssembly, assembly.Id.ToString("D"), element, FileOf(element));
                            Assemblies.Add(assembly);
                        }
                        break;
                    case "SdkMessageProcessingSteps":
                        foreach (var element in node.Elements("SdkMessageProcessingStep"))
                        {
                            var step = Step(element);
                            Unmodelled[UnmodelledXml.StepKey(step.Id)] = UnmodelledXml.Capture(element, UnmodelledXml.StepShape);
                            Source(ComponentType.SdkMessageProcessingStep, step.Id.ToString("D"), element);
                            Steps.Add(step);
                        }
                        break;
                    case "WebResources":
                        foreach (var element in node.Elements("WebResource"))
                        {
                            var resource = WebResource(element);
                            Unmodelled[UnmodelledXml.WebResourceKey(resource.Id)] = UnmodelledXml.Capture(element, UnmodelledXml.WebResourceShape);
                            Source(ComponentType.WebResource, resource.Id.ToString("D"), element, FileOf(element));
                            WebResources.Add(resource);
                        }
                        break;
                    case "AppModuleSiteMaps":
                        foreach (var siteMap in node.Elements("AppModuleSiteMap"))
                        {
                            var name = Required(siteMap, "SiteMapUniqueName", "a site map");
                            Source(ComponentType.SiteMap, name, siteMap);
                            Opaque.Add(new(ComponentType.SiteMap, name, null, new XElement(siteMap)));
                        }
                        break;
                    case "AppModules":
                        foreach (var app in node.Elements("AppModule"))
                        {
                            var name = Required(app, "UniqueName", "an app module");
                            Source(ComponentType.AppModule, name, app);
                            Opaque.Add(new(ComponentType.AppModule, name, null, new XElement(app)));
                        }
                        break;
                    case "CustomControls":
                    {
                        // Plan 3.5 UI-1d (R3): each control with the files of its manifest's folder, which are no kept files.
                        var (controls, read) = CustomControlReader.Read(node, files);
                        Consume(read);
                        foreach (var control in controls)
                        {
                            var prefix = control.Folder.Length == 0 ? "" : control.Folder + "/";
                            Source(ComponentType.CustomControl, control.Name, control.Source,
                                control.Files.ToDictionary(f => prefix + f.Key, f => f.Value, StringComparer.Ordinal));
                        }
                        CustomControls.AddRange(controls);
                        break;
                    }
                    case "Dashboards":
                        // Plan 3.5 UI-1f (ruling F1): each system dashboard is a form of no table (component type 60), applied as
                        // a systemform row of type 0; the Dashboard element is kept whole for the export.
                        foreach (var dashboard in node.Elements("Dashboard"))
                        {
                            var formId = RequiredGuid(dashboard, "FormId", "a dashboard").ToString("D");
                            var formType = NonEmpty((string?)dashboard.Element("FormXml")?.Element("forms")?.Attribute("type")) ?? "dashboard";
                            Source(ComponentType.SystemForm, formId, dashboard, formType: formType);
                            Opaque.Add(new(ComponentType.SystemForm, formId, null, new XElement(dashboard), formType));
                        }
                        break;
                    case "Workflows": ReadWorkflows(node); break;
                    case "EntityMaps":
                        // #143: applied by the import, and kept as it is for the export (the maps are not solution components).
                        EntityMaps.AddRange(node.Elements("EntityMap").Select(EntityMap));
                        if (node.HasElements) Unsupported.Add(new XElement(node));
                        break;
                    case "Languages": break; // the languages of the labels: nothing to apply
                    // Webhooks are not solution components yet (plan 3.2): refused rather than kept as unsupported XML.
                    case "ServiceEndpoints" when node.HasElements:
                        throw Invalid(ServiceEndpointSchema.NotInSolution);
                    default:
                        if (node.HasElements) Unsupported.Add(new XElement(node));
                        break;
                }
            }

            // Relationships after every entity: the custom flag of a 1:N follows its lookup; lookups take their targets from them.
            Relationships.AddRange(relationships.Select(Relationship));
            foreach (var relationship in relationships)
            {
                var name = (string)relationship.Attribute("Name")!;
                Source(ComponentType.EntityRelationship, name, relationship);
                Unmodelled[UnmodelledXml.RelationshipKey(name)] = UnmodelledXml.Capture(relationship, UnmodelledXml.RelationshipShape);
                if (MenuXml(relationship) is { } menu) RelationshipMenus[name] = menu;
                var description = Labels(relationship.Element("RelationshipDescription")?.Element("Descriptions"), "Description", languageCode);
                if (description.LocalizedLabels.Count > 0) RelationshipDescriptions[name] = description;
            }
            ResolveLookupTargets();
        }

        // ------------------------------------------------------------------ entities and attributes

        private void ReadEntities(XElement entities)
        {
            foreach (var entity in entities.Elements("Entity"))
            {
                var info = entity.Element("EntityInfo")?.Element("entity")
                           ?? throw Invalid($"Entity '{Value(entity, "Name")}' has no EntityInfo.");
                var schemaName = NonEmpty((string?)info.Attribute("Name")) ?? Value(entity, "Name")
                                 ?? throw Invalid("customizations.xml: an Entity has no name.");
                var logicalName = schemaName.ToLowerInvariant();

                var metadata = new EntityMetadata
                {
                    LogicalName = logicalName,
                    SchemaName = schemaName,
                    DisplayName = Labels(info.Element("LocalizedNames"), "LocalizedName", languageCode),
                    DisplayCollectionName = Labels(info.Element("LocalizedCollectionNames"), "LocalizedCollectionName", languageCode),
                    Description = Labels(info.Element("Descriptions"), "Description", languageCode),
                    OwnershipType = Ownership(logicalName, Value(info, "OwnershipTypeMask")),
                    IsActivity = Bool(info, "IsActivity") ?? false,
                    EntitySetName = Value(info, "EntitySetName"),
                    // Access teams (plan 2.16): null when the file has no element, so that an import leaves the table's setting.
                    AutoCreateAccessTeams = Bool(info, "AutoCreateAccessTeams"),
                    // Change tracking (plan 2.13): null when the file has no element. CanChangeTrackingBeEnabled is computed, never read.
                    ChangeTrackingEnabled = Bool(info, "ChangeTrackingEnabled"),
                    // Plan 2.11 (A19): the flags and the activity setting, null when the file has no element (a real export
                    // writes HasRelatedNotes and HasRelatedActivities only when True: an absent one is false).
                    HasNotes = Bool(info, "HasRelatedNotes"),
                    HasActivities = Bool(info, "HasRelatedActivities"),
                    // Duplicate detection (plan 2.15, ruling D18): null when the file has no element.
                    IsDuplicateDetectionEnabled = Bool(info, "IsDuplicateCheckSupported") is { } duplicates ? new BooleanManagedProperty(duplicates) : null,
                    // Queues (plan 4.1a): null when the file has no element (an absent one is false, as the notes flag).
                    IsValidForQueue = Bool(info, "IsValidForQueue") is { } queues ? new BooleanManagedProperty(queues) : null,
                };
                if (metadata.IsActivity == true) metadata.ActivityTypeMask = Int(info, "ActivityTypeMask");
                // Auditing: the table's setting and whether it can change (a table without them gets the defaults of a created one).
                if (Bool(info, "IsAuditEnabled") is not null || Bool(info, "CanModifyAuditSettings") is not null)
                    metadata.IsAuditEnabled = AuditDefaults.Managed(Bool(info, "IsAuditEnabled") ?? false, Bool(info, "CanModifyAuditSettings") ?? true);

                // What xrmverse does not model, kept for the export as the file has it.
                Unmodelled[UnmodelledXml.EntityKey(logicalName)] = UnmodelledXml.Capture(info, UnmodelledXml.EntityShape);
                Source(ComponentType.Entity, logicalName, entity);

                var attributes = new List<AttributeMetadata>();
                var rollupCompanions = RollupCompanionsOf(info);
                foreach (var element in info.Element("attributes")?.Elements("attribute") ?? [])
                {
                    // The _date and _state of a rollup column are generated by the server with it (plan 2.12b, ruling R14): a file
                    // that carries them has them skipped (a real export writes none).
                    if (rollupCompanions.Contains(AttributeName(element) ?? "")) continue;
                    if (Attribute(logicalName, element, metadata.IsActivity == true) is not { } attribute) continue;
                    // The columns an activity gets from the platform (plan 2.11 A2, the party lists, activityid) are its system
                    // columns, whatever the file says: the import derives them (A19).
                    if (metadata.IsActivity == true && ActivitySchema.IsGeneratedActivityAttribute(attribute.LogicalName))
                        SdkProperty.Set(attribute, nameof(AttributeMetadata.IsCustomAttribute), false);
                    Unmodelled[UnmodelledXml.AttributeKey(logicalName, attribute.LogicalName)] = UnmodelledXml.Capture(element, UnmodelledXml.AttributeShape);
                    // A calculated or rollup column's source carries its formula file (a layer payload restores it with the column); a
                    // formula column's carries only its own line of its table's file, so that layers of several solutions combine.
                    Source(ComponentType.Attribute, $"{logicalName}.{attribute.LogicalName}", element, FormulaFileOf(element, attribute),
                        entity: logicalName);
                    if (attribute.IsPrimaryId == true) SdkProperty.Set(metadata, nameof(EntityMetadata.PrimaryIdAttribute), attribute.LogicalName);
                    if (attribute.IsPrimaryName == true) SdkProperty.Set(metadata, nameof(EntityMetadata.PrimaryNameAttribute), attribute.LogicalName);
                    if (attribute is LookupAttributeMetadata lookup) Lookups.Add(lookup);
                    else attributes.Add(attribute);
                }

                MarkBaseCurrencyAttributes(attributes);
                CheckComputedBases(logicalName, info, attributes);
                MarkFileCompanions(attributes);
                if (attributes.OfType<ImageAttributeMetadata>().Where(i => i.IsPrimaryImage == true).Select(i => i.LogicalName).ToList() is { Count: > 1 } primaries)
                    throw Invalid($"Entity '{logicalName}' has more than one primary image ({string.Join(", ", primaries)}): a table has one primary image.");
                metadata.SetAttributes(attributes);
                Entities.Add(metadata);

                foreach (var view in entity.Element("SavedQueries")?.Element("savedqueries")?.Elements("savedquery") ?? [])
                {
                    var viewId = RequiredGuid(view, "savedqueryid", $"a view of {logicalName}").ToString("D");
                    Source(ComponentType.SavedQuery, viewId, view, entity: logicalName);
                    Opaque.Add(new(ComponentType.SavedQuery, viewId, logicalName, new XElement(view)));
                }
                foreach (var forms in entity.Element("FormXml")?.Elements("forms") ?? [])
                {
                    var formType = NonEmpty((string?)forms.Attribute("type"));
                    foreach (var form in forms.Elements("systemform"))
                    {
                        var formId = RequiredGuid(form, "formid", $"a form of {logicalName}").ToString("D");
                        Source(ComponentType.SystemForm, formId, form, entity: logicalName, formType: formType);
                        Opaque.Add(new(ComponentType.SystemForm, formId, logicalName, new XElement(form), formType));
                    }
                }

                // The system charts (plan 3.5 UI-1f, ruling F2): row components of the table, like its views.
                foreach (var chart in entity.Element("Visualizations")?.Elements("visualization") ?? [])
                {
                    var chartId = RequiredGuid(chart, ChartSchema.Id, $"a chart of {logicalName}").ToString("D");
                    Source(ComponentType.SavedQueryVisualization, chartId, chart, entity: logicalName);
                    Opaque.Add(new(ComponentType.SavedQueryVisualization, chartId, logicalName, new XElement(chart)));
                }

                foreach (var part in entity.Elements().Where(e => !AppliedEntityParts.Contains(e.Name.LocalName)))
                {
                    if (CustomizationsReader.HasContent(part)) EntityUnsupported.Add((logicalName, new XElement(part)));
                }
            }
        }

        /// <summary>The children of an <c>Entity</c> the reader applies; any other one is kept when it has content.</summary>
        private static readonly HashSet<string> AppliedEntityParts = new(StringComparer.Ordinal)
        {
            "Name", "EntityInfo", "FormXml", "SavedQueries", "Visualizations",
        };

        private static OwnershipTypes? Ownership(string entity, string? mask) => mask switch
        {
            null => null,
            "UserOwned" => OwnershipTypes.UserOwned,
            "TeamOwned" => OwnershipTypes.TeamOwned,
            "OrgOwned" or "OrganizationOwned" => OwnershipTypes.OrganizationOwned,
            "BusinessOwned" => OwnershipTypes.BusinessOwned,
            "BusinessParented" => OwnershipTypes.BusinessParented,
            "None" => OwnershipTypes.None,
            _ => throw Invalid($"Entity '{entity}' has the ownership '{mask}', which xrmverse does not support."),
        };

        /// <summary>
        /// The attribute; null for a system attribute of a type xrmverse does not support (recorded in
        /// <see cref="SkippedAttributes"/>: the platform owns it). With <paramref name="activity"/> (the table is an activity), the
        /// system party lists and EntityName columns are read.
        /// </summary>
        private AttributeMetadata? Attribute(string entity, XElement element, bool activity = false)
        {
            var schemaName = NonEmpty((string?)element.Attribute("PhysicalName")) ?? Value(element, "Name");
            var logicalName = (Value(element, "LogicalName") ?? Value(element, "Name") ?? schemaName)?.ToLowerInvariant()
                              ?? throw Invalid($"Entity '{entity}' has an attribute without a name.");
            var type = Value(element, "Type") ?? throw Invalid($"Attribute '{entity}.{logicalName}' has no Type.");
            var where = $"attribute '{entity}.{logicalName}'";
            var isCustom = Bool(element, "IsCustomField") ?? true;
            // A calculated or rollup column (plans 2.12a, 2.12b): its XAML is the file FormulaDefinitionFileName names, kept verbatim;
            // a formula column (plan 2.12c): its line of the table's formula file. A system column keeps being read as a simple one
            // (the platform owns its definition).
            var formulaFile = isCustom ? ComputedFormulaFile(entity, logicalName, element) : null;
            // An empty AutoNumberFormat of a string column says "no format", so that a newer solution can remove one (ruling N7).
            var autoNumber = element.Element("AutoNumberFormat") is { } autoNumberElement ? autoNumberElement.Value.Trim() : null;
            if (!string.IsNullOrEmpty(autoNumber) && !string.Equals(type, "nvarchar", StringComparison.OrdinalIgnoreCase))
                throw Invalid($"Attribute '{entity}.{logicalName}' of type '{type}' has an AutoNumberFormat: only a string column can have one.");

            AttributeMetadata? attribute = type.ToLowerInvariant() switch
            {
                "primarykey" or "uniqueidentifier" => new UniqueIdentifierAttributeMetadata(),
                "nvarchar" => new StringAttributeMetadata
                {
                    MaxLength = Int(element, "MaxLength"),
                    Format = EnumValue<StringFormat>(element, "Format", where),
                    AutoNumberFormat = autoNumber,
                },
                "ntext" => new MemoAttributeMetadata
                {
                    MaxLength = Int(element, "MaxLength"),
                    Format = EnumValue<StringFormat>(element, "Format", where),
                },
                "int" => new IntegerAttributeMetadata
                {
                    MinValue = Int(element, "MinValue"),
                    MaxValue = Int(element, "MaxValue"),
                    Format = EnumValue<IntegerFormat>(element, "Format", where),
                },
                "bigint" => new BigIntAttributeMetadata(), // its range is fixed: MinValue/MaxValue are read-only in the SDK
                "decimal" => new DecimalAttributeMetadata
                {
                    MinValue = Decimal(element, "MinValue"),
                    MaxValue = Decimal(element, "MaxValue"),
                    Precision = Int(element, "Accuracy"),
                },
                "float" => new DoubleAttributeMetadata
                {
                    MinValue = Double(element, "MinValue"),
                    MaxValue = Double(element, "MaxValue"),
                    Precision = Int(element, "Accuracy"),
                },
                "money" => new MoneyAttributeMetadata
                {
                    MinValue = Double(element, "MinValue"),
                    MaxValue = Double(element, "MaxValue"),
                    Precision = Int(element, "Accuracy"),
                    PrecisionSource = Int(element, "AccuracySource"),
                },
                "bit" => new BooleanAttributeMetadata
                {
                    DefaultValue = Int(element, "AppDefaultValue") is { } value ? value == 1 : null,
                    OptionSet = element.Element("optionset") is { } bit ? BooleanOptionSet(bit, isGlobal: false) : null,
                },
                "datetime" => new DateTimeAttributeMetadata
                {
                    Format = Value(element, "Format")?.ToLowerInvariant() switch
                    {
                        null => null,
                        "date" or "dateonly" => DateTimeFormat.DateOnly,
                        "datetime" or "dateandtime" => DateTimeFormat.DateAndTime,
                        var other => throw Invalid($"The {where} has the format '{other}', which xrmverse does not support."),
                    },
                    DateTimeBehavior = Int(element, "Behavior") switch
                    {
                        null => null,
                        1 => DateTimeBehavior.UserLocal,
                        2 => DateTimeBehavior.DateOnly,
                        3 => DateTimeBehavior.TimeZoneIndependent,
                        var other => throw Invalid($"The {where} has the behavior {other}, which xrmverse does not support."),
                    },
                },
                "picklist" => new PicklistAttributeMetadata { OptionSet = AttributeOptionSet(element), DefaultFormValue = DefaultOption(element) },
                "multiselectpicklist" => new MultiSelectPicklistAttributeMetadata
                {
                    OptionSet = AttributeOptionSet(element),
                    DefaultFormValue = DefaultOption(element),
                },
                "state" => new StateAttributeMetadata { OptionSet = AttributeOptionSet(element) },
                "status" => new StatusAttributeMetadata { OptionSet = AttributeOptionSet(element) },
                "file" => new FileAttributeMetadata { MaxSizeInKB = Int(element, "MaxSizeInKB") },
                "image" when ThumbnailSide(element, "MaxHeight") is { } side and not FileColumns.ThumbnailSize
                    => throw Invalid($"The {where} has MaxHeight {side}: thumbnails are always {FileColumns.ThumbnailSize}x{FileColumns.ThumbnailSize}."),
                "image" when ThumbnailSide(element, "MaxWidth") is { } side and not FileColumns.ThumbnailSize
                    => throw Invalid($"The {where} has MaxWidth {side}: thumbnails are always {FileColumns.ThumbnailSize}x{FileColumns.ThumbnailSize}."),
                "image" => new ImageAttributeMetadata
                {
                    MaxSizeInKB = Int(element, "MaxSizeInKB"),
                    CanStoreFullImage = Bool(element, "CanStoreFullImage"),
                    IsPrimaryImage = Bool(element, "IsPrimaryImage"),
                },
                "lookup" => new LookupAttributeMetadata(),
                "customer" => Lookup(AttributeTypeCode.Customer, AttributeTypeDisplayName.CustomerType),
                "owner" => Lookup(AttributeTypeCode.Owner, AttributeTypeDisplayName.OwnerType, "systemuser", "team"),
                // The system party lists and EntityName columns of an activity table (plan 2.11): read so that the import can
                // compare them with the ones the server derives (ruling Z5); of any other table they stay skipped.
                "partylist" when activity && !isCustom => Lookup(AttributeTypeCode.PartyList, AttributeTypeDisplayName.PartyListType),
                "entityname" when activity && !isCustom => new EntityNameAttributeMetadata(),
                _ => null,
            };
            if (attribute is null)
            {
                if (isCustom) throw Invalid($"Attribute '{entity}.{logicalName}' has type '{type}', which xrmverse does not support.");
                SkippedAttributes.Add((entity, logicalName, type));
                return null;
            }

            attribute.LogicalName = logicalName;
            attribute.SchemaName = schemaName;
            if (formulaFile is not null) ReadFormula(entity, attribute, formulaFile, Int(element, "SourceType") ?? 0);
            attribute.DisplayName = Labels(element.Element("displaynames"), "displayname", languageCode);
            attribute.Description = Labels(element.Element("Descriptions"), "Description", languageCode);
            attribute.RequiredLevel = new AttributeRequiredLevelManagedProperty(RequiredLevel(where, Value(element, "RequiredLevel")));
            attribute.IsValidForCreate = Bool(element, "ValidForCreateApi");
            attribute.IsValidForUpdate = Bool(element, "ValidForUpdateApi");
            attribute.IsSecured = Bool(element, "IsSecured");
            if (Bool(element, "IsAuditEnabled") is { } audited) attribute.IsAuditEnabled = AuditDefaults.Managed(audited);
            SdkProperty.Set(attribute, nameof(AttributeMetadata.EntityLogicalName), entity);
            SdkProperty.Set(attribute, nameof(AttributeMetadata.IsCustomAttribute), isCustom);
            if (type.Equals("primarykey", StringComparison.OrdinalIgnoreCase))
                SdkProperty.Set(attribute, nameof(AttributeMetadata.IsPrimaryId), true);
            if ((Value(element, "DisplayMask") ?? "").Split('|').Contains("PrimaryName", StringComparer.Ordinal))
                SdkProperty.Set(attribute, nameof(AttributeMetadata.IsPrimaryName), true);
            return attribute;
        }

        /// <summary>
        /// The <c>FormulaDefinitionFileName</c> of a custom column with <c>SourceType</c> 1 (calculated, plan 2.12a), 2 (rollup, plan
        /// 2.12b) or 3 (formula, plan 2.12c); null for a simple column and for the <c>_base</c> of a calculated or rollup Currency column
        /// (<see cref="ComputedBaseOf"/>; a formula column is never Currency). Prompt (4) columns, a computed column without the file name
        /// and a file name on another column are refused (400).
        /// </summary>
        private static string? ComputedFormulaFile(string entity, string logicalName, XElement element)
        {
            var sourceType = Int(element, "SourceType") ?? 0;
            var fileName = Value(element, "FormulaDefinitionFileName");
            var where = $"'{entity}.{logicalName}'";
            return sourceType switch
            {
                0 when fileName is null => null,
                0 => throw Invalid($"Column {where} has a FormulaDefinitionFileName but SourceType 0: only a calculated (SourceType 1), rollup (SourceType 2) or formula (SourceType 3) column has a formula."),
                // The base-currency companion of a calculated or rollup Currency column (parity export): SourceType 1 or 2, no file,
                // CalculationOf its money column. It is read as the money's _base, which the server generates; ReadEntities checks
                // that the money column has the same SourceType.
                ComputedColumnRules.Calculated or RollupColumns.Rollup when fileName is null && ComputedBaseOf(logicalName, element) is not null => null,
                ComputedColumnRules.Calculated or RollupColumns.Rollup or ComputedColumnRules.Formula
                    => fileName ?? throw Invalid($"{Kind(sourceType)} column {where} has no FormulaDefinitionFileName."),
                ComputedColumnRules.Prompt => throw Invalid($"Prompt columns are not supported: {where}."),
                _ => throw Invalid($"SourceType {sourceType} is not valid: {where}."),
            };
        }

        /// <summary>"Calculated" (<c>SourceType</c> 1), "Rollup" (2) or "Formula" (3), for the texts of the refusals.</summary>
        private static string Kind(int sourceType) => sourceType switch
        {
            RollupColumns.Rollup => "Rollup",
            ComputedColumnRules.Formula => "Formula",
            _ => "Calculated",
        };

        /// <summary>The logical name of an <c>attribute</c> element (<c>LogicalName</c>, else <c>Name</c>), lowercase; null when it has none.</summary>
        private static string? AttributeName(XElement element) => (Value(element, "LogicalName") ?? Value(element, "Name"))?.ToLowerInvariant();

        /// <summary>
        /// The money column whose base-currency companion <paramref name="element"/> is when it is written as a calculated or rollup
        /// column's (parity export: <c>&lt;money&gt;_base</c> of type money with <c>SourceType</c> 1 or 2, no
        /// <c>FormulaDefinitionFileName</c> and <c>CalculationOf</c> naming the money column; a file without <c>CalculationOf</c> is
        /// read the same way); null otherwise.
        /// </summary>
        private static string? ComputedBaseOf(string logicalName, XElement element)
        {
            if (!string.Equals(Value(element, "Type"), "money", StringComparison.OrdinalIgnoreCase)
                || (Int(element, "SourceType") ?? 0) is not (ComputedColumnRules.Calculated or RollupColumns.Rollup)
                || Value(element, "FormulaDefinitionFileName") is not null
                || !logicalName.EndsWith(TypeMapping.BaseColumnSuffix, StringComparison.Ordinal))
                return null;
            var money = logicalName[..^TypeMapping.BaseColumnSuffix.Length];
            return Value(element, "CalculationOf") is not { } of || string.Equals(of, money, StringComparison.OrdinalIgnoreCase) ? money : null;
        }

        /// <summary>
        /// A <c>_base</c> read as a computed column's companion (<see cref="ComputedBaseOf"/>) belongs to a money column of the same table
        /// with the same <c>SourceType</c> (calculated or rollup): anything else is a computed column without its formula file (400).
        /// </summary>
        private static void CheckComputedBases(string entity, XElement info, List<AttributeMetadata> attributes)
        {
            foreach (var element in info.Element("attributes")?.Elements("attribute") ?? [])
            {
                var name = AttributeName(element);
                if (name is null || ComputedBaseOf(name, element) is not { } money) continue;
                var sourceType = Int(element, "SourceType") ?? 0;
                if (!attributes.Any(a => a is MoneyAttributeMetadata && a.LogicalName == money && a.SourceType == sourceType))
                    throw Invalid($"{Kind(sourceType)} column '{entity}.{name}' has no FormulaDefinitionFileName.");
            }
        }

        /// <summary>
        /// The logical names of the companions the server generates for the rollup columns of <paramref name="info"/> (plan 2.12b,
        /// ruling R2): <c>&lt;rollup&gt;_date</c> of type datetime and <c>&lt;rollup&gt;_state</c> of type int. A column of another type
        /// with such a name is read as any column (the store then refuses the name clash).
        /// </summary>
        private static HashSet<string> RollupCompanionsOf(XElement info)
        {
            var elements = info.Element("attributes")?.Elements("attribute").ToList() ?? [];
            var rollups = elements
                .Where(e => (Int(e, "SourceType") ?? 0) == RollupColumns.Rollup && AttributeName(e) is { } n && ComputedBaseOf(n, e) is null)
                .Select(e => AttributeName(e)!)
                .ToHashSet(StringComparer.Ordinal);
            return elements
                .Where(e => AttributeName(e) is { } n && Value(e, "Type")?.ToLowerInvariant() is { } type
                            && ((type == "datetime" && n.EndsWith(RollupColumns.DateSuffix, StringComparison.Ordinal)
                                 && rollups.Contains(n[..^RollupColumns.DateSuffix.Length]))
                                || (type == "int" && n.EndsWith(RollupColumns.StateSuffix, StringComparison.Ordinal)
                                    && rollups.Contains(n[..^RollupColumns.StateSuffix.Length]))))
                .Select(e => AttributeName(e)!)
                .ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>
        /// Makes <paramref name="attribute"/> calculated (<paramref name="sourceType"/> 1), a rollup (2) or a formula column (3): that
        /// <c>SourceType</c> and its <c>FormulaDefinition</c>. A calculated or rollup column's is the text of <paramref name="fileName"/>
        /// (UTF-8, its bytes kept as they are, a byte order mark included, so that an export writes the file back byte for byte); a
        /// formula column's is its line of the table's formula file (<see cref="FormulaFiles.Read"/>, ruling F3). The store checks the
        /// definition when the import creates the column.
        /// </summary>
        private void ReadFormula(string entity, AttributeMetadata attribute, string fileName, int sourceType)
        {
            var where = $"'{entity}.{attribute.LogicalName}'";
            var owner = $"{Kind(sourceType).ToLowerInvariant()} column {where}";
            if (attribute.GetType().GetProperty("FormulaDefinition") is null)
                throw sourceType switch
                {
                    // As the store answers a rollup of a type that cannot be one (plan 2.12b, Global Constraints).
                    RollupColumns.Rollup => RollupColumnRules.DataTypeNotValid(attribute, where),
                    ComputedColumnRules.Formula => Invalid($"A formula column cannot be of type {attribute.AttributeType}: {where}."),
                    _ => Invalid($"A calculated column cannot be of type {attribute.AttributeType}: {where}."),
                };
            var bytes = File(fileName, owner);
            string text;
            try
            {
                text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                throw Invalid($"The formula file '{fileName.Replace('\\', '/').TrimStart('/')}' of {owner} is not UTF-8 text.");
            }
            if (sourceType == ComputedColumnRules.Formula)
            {
                // One file for the formula columns of a table: read once.
                var path = fileName.Replace('\\', '/').TrimStart('/');
                if (!formulaFiles.TryGetValue(path, out var formulas)) formulaFiles[path] = formulas = FormulaFiles.Read(text, fileName);
                text = FormulaFiles.FormulaOf(formulas, entity, attribute.LogicalName, fileName);
            }
            attribute.SourceType = sourceType;
            ComputedColumnRules.SetFormula(attribute, text);
        }

        /// <summary>
        /// The formula files read so far (plan 2.12c), by archive path: each is read once for all the columns that name it. One reader
        /// reads one file set (a solution, or the payloads <see cref="Layers.LayerPayload.ToPackage"/> merged), so a path has one content.
        /// </summary>
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> formulaFiles = new(StringComparer.OrdinalIgnoreCase);

        private static int? ThumbnailSide(XElement element, string name) => Int(element, name);

        private static LookupAttributeMetadata Lookup(AttributeTypeCode type, AttributeTypeDisplayName typeName, params string[] targets)
        {
            var lookup = new LookupAttributeMetadata();
            if (targets.Length > 0) lookup.Targets = targets;
            SdkProperty.Set(lookup, nameof(AttributeMetadata.AttributeType), type);
            SdkProperty.Set(lookup, nameof(AttributeMetadata.AttributeTypeName), typeName);
            return lookup;
        }

        private static AttributeRequiredLevel RequiredLevel(string where, string? level) => level?.ToLowerInvariant() switch
        {
            null or "none" => AttributeRequiredLevel.None,
            "recommended" => AttributeRequiredLevel.Recommended,
            "required" or "applicationrequired" => AttributeRequiredLevel.ApplicationRequired,
            "systemrequired" => AttributeRequiredLevel.SystemRequired,
            _ => throw Invalid($"The {where} has the required level '{level}', which xrmverse does not support."),
        };

        /// <summary><c>AppDefaultValue</c> of a choice: -1 means no default.</summary>
        private static int? DefaultOption(XElement element) => Int(element, "AppDefaultValue") is { } value and not -1 ? value : null;

        /// <summary>A money attribute named <c>&lt;money&gt;_base</c> holds the base-currency value of another money attribute.</summary>
        private static void MarkBaseCurrencyAttributes(List<AttributeMetadata> attributes)
        {
            var money = attributes.OfType<MoneyAttributeMetadata>().Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);
            foreach (var attribute in attributes.OfType<MoneyAttributeMetadata>())
            {
                if (attribute.LogicalName.EndsWith(TypeMapping.BaseColumnSuffix, StringComparison.Ordinal)
                    && money.Contains(attribute.LogicalName[..^TypeMapping.BaseColumnSuffix.Length]))
                    SdkProperty.Set(attribute, nameof(MoneyAttributeMetadata.IsBaseCurrency), true);
            }
        }

        /// <summary>
        /// The companions a file may carry for its file and image columns (<c>&lt;file&gt;_name</c>, <c>&lt;image&gt;id</c>,
        /// <c>&lt;image&gt;_timestamp</c>, <c>&lt;image&gt;_url</c>, as <see cref="FileColumns.Companions"/> names them): the server
        /// creates them with their main column, so they are marked <c>AttributeOf</c> it (without the server's
        /// <c>LinkedAttributeId</c>), which makes them attributes stored by another one (<see cref="TypeMapping.IsStoredByOtherAttribute"/>):
        /// never created by an import, compared with the generated ones like a money <c>_base</c>.
        /// </summary>
        private static void MarkFileCompanions(List<AttributeMetadata> attributes)
        {
            var mains = attributes.Where(FileColumns.IsFileOrImage).ToList();
            foreach (var main in mains)
            {
                foreach (var expected in FileColumns.Companions(main))
                {
                    if (attributes.FirstOrDefault(a => a.LogicalName == expected.LogicalName && a.GetType() == expected.GetType()) is { } companion)
                        SdkProperty.Set(companion, nameof(AttributeMetadata.AttributeOf), main.LogicalName);
                }
            }
        }

        // ------------------------------------------------------------------ option sets

        /// <summary>The option set of a choice attribute: inline, or a reference (<c>OptionSetName</c>) to a global one.</summary>
        private OptionSetMetadata? AttributeOptionSet(XElement attribute)
        {
            if (Value(attribute, "OptionSetName") is { } globalName) return new OptionSetMetadata { Name = globalName, IsGlobal = true };
            return attribute.Element("optionset") is { } inline ? (OptionSetMetadata)OptionSet(inline, isGlobal: false) : null;
        }

        private OptionSetMetadataBase OptionSet(XElement element, bool isGlobal)
        {
            var name = NonEmpty((string?)element.Attribute("Name")) ?? throw Invalid("customizations.xml: an optionset has no Name.");
            var type = Value(element, "OptionSetType")?.ToLowerInvariant();
            if (type == "bit") return BooleanOptionSet(element, isGlobal);

            var options = new OptionMetadataCollection();
            OptionSetType setType;
            switch (type)
            {
                case null or "picklist":
                    setType = OptionSetType.Picklist;
                    foreach (var option in element.Element("options")?.Elements("option") ?? [])
                        options.Add(Option(new OptionMetadata(), option, name));
                    break;
                case "state":
                    setType = OptionSetType.State;
                    foreach (var state in element.Element("states")?.Elements("state") ?? [])
                    {
                        options.Add(Option(new StateOptionMetadata
                        {
                            DefaultStatus = IntAttribute(state, "defaultstatus"),
                            InvariantName = (string?)state.Attribute("invariantname"),
                        }, state, name));
                    }

                    break;
                case "status":
                    setType = OptionSetType.Status;
                    foreach (var status in element.Element("statuses")?.Elements("status") ?? [])
                        options.Add(Option(new StatusOptionMetadata { State = IntAttribute(status, "state") }, status, name));
                    break;
                default:
                    throw Invalid($"Option set '{name}' has the type '{type}', which xrmverse does not support.");
            }

            var set = new OptionSetMetadata(options)
            {
                Name = name,
                IsGlobal = Bool(element, "IsGlobal") ?? isGlobal,
                OptionSetType = setType,
                DisplayName = Labels(element.Element("displaynames"), "displayname", languageCode),
                Description = Labels(element.Element("Descriptions"), "Description", languageCode),
            };
            return set;
        }

        private T Option<T>(T option, XElement element, string optionSet) where T : OptionMetadata
        {
            option.Value = IntAttribute(element, "value") ?? throw Invalid($"Option set '{optionSet}' has an option without a value.");
            option.Label = Labels(element.Element("labels"), "label", languageCode);
            option.Description = Labels(element.Element("Descriptions"), "Description", languageCode);
            option.Color = NonEmpty((string?)element.Attribute("Color"));
            option.ExternalValue = NonEmpty((string?)element.Attribute("ExternalValue"));
            return option;
        }

        private BooleanOptionSetMetadata BooleanOptionSet(XElement element, bool isGlobal)
        {
            var name = NonEmpty((string?)element.Attribute("Name")) ?? "";
            var options = (element.Element("options")?.Elements("option") ?? [])
                .ToDictionary(o => IntAttribute(o, "value") ?? throw Invalid($"Option set '{name}' has an option without a value."));
            OptionMetadata OptionOf(int value) =>
                new(options.TryGetValue(value, out var option) ? Labels(option.Element("labels"), "label", languageCode) : new Label(), value);

            return new BooleanOptionSetMetadata(OptionOf(1), OptionOf(0))
            {
                Name = name,
                IsGlobal = Bool(element, "IsGlobal") ?? isGlobal,
                DisplayName = Labels(element.Element("displaynames"), "displayname", languageCode),
                Description = Labels(element.Element("Descriptions"), "Description", languageCode),
            };
        }

        // ------------------------------------------------------------------ relationships

        private RelationshipMetadataBase Relationship(XElement element)
        {
            var name = NonEmpty((string?)element.Attribute("Name")) ?? throw Invalid("customizations.xml: an EntityRelationship has no Name.");
            var type = Value(element, "EntityRelationshipType");
            switch (type)
            {
                case "OneToMany":
                {
                    var referencing = Lower(Required(element, "ReferencingEntityName", $"relationship '{name}'"));
                    var referenced = Lower(Required(element, "ReferencedEntityName", $"relationship '{name}'"));
                    var attribute = Lower(Required(element, "ReferencingAttributeName", $"relationship '{name}'"));
                    var lookup = Lookups.FirstOrDefault(l => l.EntityLogicalName == referencing && l.LogicalName == attribute);
                    var relationship = new OneToManyRelationshipMetadata
                    {
                        SchemaName = name,
                        ReferencingEntity = referencing,
                        ReferencedEntity = referenced,
                        ReferencingAttribute = attribute,
                        ReferencedAttribute = Entities.FirstOrDefault(e => e.LogicalName == referenced)?.PrimaryIdAttribute
                                              ?? SystemAttributes.PrimaryIdName(referenced),
                        IsHierarchical = Bool(element, "IsHierarchical") ?? false,
                        // A lookup absent from the file is on a platform table (SyncError.RegardingObjectId, …): not custom.
                        // The relationship of a table to fileattachment is the server's, made with the table's first file or
                        // image column (ruling F1): never custom, whatever the file says.
                        // The relationships of the activity model (regarding, notes, parties: plan 2.11 A6, A10, A15) are the
                        // server's, derived from the tables' settings (A19): never custom either.
                        IsCustomRelationship = !IsFileAttachmentLink(referencing, attribute) && !IsActivityModelLink(referencing, attribute)
                                               && (Bool(element, "IsCustomRelationship") ?? lookup?.IsCustomAttribute ?? false),
                        CascadeConfiguration = Cascade(element, name),
                    };
                    foreach (var role in element.Element("EntityRelationshipRoles")?.Elements("EntityRelationshipRole") ?? [])
                    {
                        var navigation = Value(role, "NavigationPropertyName");
                        if (navigation is null) continue;
                        if (Int(role, "RelationshipRoleType") == 1) relationship.ReferencingEntityNavigationPropertyName = navigation;
                        else relationship.ReferencedEntityNavigationPropertyName = navigation;
                    }

                    return relationship;
                }
                case "ManyToMany":
                {
                    // The two roles of a many-to-many relationship are its first and second entity, in that order.
                    var navigation = (element.Element("EntityRelationshipRoles")?.Elements("EntityRelationshipRole") ?? [])
                        .Select(role => Value(role, "NavigationPropertyName")).ToList();
                    return new ManyToManyRelationshipMetadata
                    {
                        SchemaName = name,
                        Entity1LogicalName = Lower(Required(element, "FirstEntityName", $"relationship '{name}'")),
                        Entity2LogicalName = Lower(Required(element, "SecondEntityName", $"relationship '{name}'")),
                        IntersectEntityName = Lower(Required(element, "IntersectEntityName", $"relationship '{name}'")),
                        // No lookup to judge by: custom when named with the publisher prefix (as Dataverse names custom N:N).
                        IsCustomRelationship = Bool(element, "IsCustomRelationship")
                                               ?? name.StartsWith(publisherPrefix + "_", StringComparison.OrdinalIgnoreCase),
                        Entity1NavigationPropertyName = navigation.ElementAtOrDefault(0),
                        Entity2NavigationPropertyName = navigation.ElementAtOrDefault(1),
                    };
                }
                default:
                    throw Invalid($"Relationship '{name}' has the type '{type}', which xrmverse does not support.");
            }
        }

        /// <summary>
        /// The associated menu configuration of a relationship, kept opaque: its <c>EntityRelationshipRoles</c> with every role
        /// element except <c>NavigationPropertyName</c> (applied as the relationship's navigation property names), so the
        /// navigation pane settings (<c>NavPaneDisplayOption</c>, <c>NavPaneArea</c>, <c>NavPaneOrder</c>, <c>CustomLabels</c>,
        /// …) round-trip; null when no role has any.
        /// </summary>
        private static XElement? MenuXml(XElement relationship)
        {
            var roles = relationship.Element("EntityRelationshipRoles")?.Elements("EntityRelationshipRole").ToList() ?? [];
            var kept = roles.Select(role => new XElement(role.Name,
                role.Elements().Where(e => e.Name.LocalName != "NavigationPropertyName").Select(e => new XElement(e)))).ToList();
            return kept.Any(r => r.Elements().Any(e => e.Name.LocalName != "RelationshipRoleType"))
                ? new XElement("EntityRelationshipRoles", kept)
                : null;
        }

        private static CascadeConfiguration Cascade(XElement element, string relationship)
        {
            CascadeType Of(string action) => Value(element, "Cascade" + action) is { } value
                ? Enum.TryParse<CascadeType>(value, ignoreCase: false, out var cascade) && Enum.IsDefined(cascade)
                    ? cascade
                    : throw Invalid($"Relationship '{relationship}' has the cascade '{value}' for {action}, which xrmverse does not support.")
                : CascadeType.NoCascade;

            return new CascadeConfiguration
            {
                Assign = Of("Assign"),
                Delete = Of("Delete"),
                Archive = Of("Archive"),
                Merge = Of("Merge"),
                Reparent = Of("Reparent"),
                Share = Of("Share"),
                Unshare = Of("Unshare"),
                RollupView = Of("RollupView"),
            };
        }

        /// <summary>
        /// True for a relationship of the activity model (plan 2.11): on <c>annotation.objectid</c>, <c>queueitem.objectid</c> (plan 4.1a), <c>activityparty</c>, or the
        /// <c>regardingobjectid</c> of the pointer, a standard activity or an activity table of the file. The <c>regardingobjectid</c>
        /// of another platform table (<c>SyncError</c>, ...) is not one.
        /// </summary>
        private bool IsActivityModelLink(string referencing, string attribute) =>
            ActivitySchema.IsManagedLookup(referencing, attribute)
            && (referencing is ActivitySchema.Annotation or ActivitySchema.Party or ActivitySchema.Pointer or QueueSchema.QueueItem
                || ActivitySchema.StandardActivities.Contains(referencing)
                || Entities.Any(e => e.LogicalName == referencing && e.IsActivity == true));

        private static bool IsFileAttachmentLink(string referencing, string attribute) =>
            referencing == FileColumns.AttachmentEntity && attribute == FileColumns.AttachmentLookup;

        private void ResolveLookupTargets()
        {
            foreach (var lookup in Lookups.Where(l => l.AttributeType != AttributeTypeCode.Owner))
            {
                var targets = Relationships.OfType<OneToManyRelationshipMetadata>()
                    .Where(r => r.ReferencingEntity == lookup.EntityLogicalName && r.ReferencingAttribute == lookup.LogicalName)
                    .Select(r => r.ReferencedEntity)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (targets.Length == 0 && lookup.IsCustomAttribute == true)
                    throw Invalid($"Lookup attribute '{lookup.EntityLogicalName}.{lookup.LogicalName}' has no relationship in the solution file.");
                lookup.Targets = targets;
            }
        }

        // ------------------------------------------------------------------ security roles

        private static PackageRole Role(XElement element)
        {
            var name = NonEmpty((string?)element.Attribute("name")) ?? throw Invalid("customizations.xml: a Role has no name.");
            var id = GuidAttribute(element, "id") ?? throw Invalid($"Role '{name}' has no id.");
            var privileges = new List<(string, DepthMask)>();
            foreach (var privilege in element.Element("RolePrivileges")?.Elements("RolePrivilege") ?? [])
            {
                var privilegeName = NonEmpty((string?)privilege.Attribute("name")) ?? throw Invalid($"Role '{name}' has a privilege without a name.");
                var level = (string?)privilege.Attribute("level");
                var depth = level switch
                {
                    "Basic" => DepthMask.Basic,
                    "Local" => DepthMask.Local,
                    "Deep" => DepthMask.Deep,
                    "Global" => DepthMask.Global,
                    _ => throw Invalid($"Role '{name}': privilege '{privilegeName}' has the level '{level}', which is not Basic, Local, Deep or Global."),
                };
                privileges.Add((privilegeName, depth));
            }

            return new PackageRole(id, name, privileges);
        }

        /// <summary>
        /// A field security profile (ruling F8), in the shape of a real Dataverse export: the attributes <c>name</c>,
        /// <c>fieldsecurityprofileid</c> and <c>description</c>, and <c>FieldPermissions/FieldPermission</c> elements whose
        /// values are child elements (<c>EntityName</c>, <c>AttributeName</c>, <c>CanRead</c>, <c>CanUpdate</c>,
        /// <c>CanCreate</c>, <c>CanReadUnmasked</c>). A missing permission value is 0 (not allowed). A <c>FieldPermission</c>
        /// directly under the profile (no <c>FieldPermissions</c> around it) is not read, only counted for the import log.
        /// </summary>
        private static PackageFieldSecurityProfile FieldSecurityProfile(XElement element)
        {
            var name = NonEmpty((string?)element.Attribute("name")) ?? throw Invalid("customizations.xml: a FieldSecurityProfile has no name.");
            var id = GuidAttribute(element, "fieldsecurityprofileid")
                     ?? throw Invalid($"Field security profile '{name}' has no fieldsecurityprofileid.");
            var permissions = new List<PackageFieldPermission>();
            foreach (var permission in element.Elements("FieldPermissions").Elements("FieldPermission"))
            {
                var entity = Value(permission, "EntityName")
                             ?? throw Invalid($"Field security profile '{name}' has a FieldPermission without an EntityName.");
                var attribute = Value(permission, "AttributeName")
                                ?? throw Invalid($"Field security profile '{name}' has a FieldPermission on '{entity}' without an AttributeName.");
                int Right(string child)
                {
                    var value = Int(permission, child) ?? 0;
                    if (value is not (0 or 4) && child != "CanReadUnmasked")
                        throw Invalid($"Field security profile '{name}': {child} of '{entity}.{attribute}' is {value}; it must be 0 or 4.");
                    return value;
                }
                permissions.Add(new PackageFieldPermission(Lower(entity), Lower(attribute), Right("CanRead"), Right("CanCreate"),
                    Right("CanUpdate"), Right("CanReadUnmasked")));
            }
            return new PackageFieldSecurityProfile(id, name, NonEmpty((string?)element.Attribute("description")), permissions,
                element.Elements("FieldPermission").Count());
        }

        // ------------------------------------------------------------------ web resources and plug-ins

        private PackageWebResource WebResource(XElement element)
        {
            var name = Required(element, "Name", "a web resource");
            return new PackageWebResource(
                RequiredGuid(element, "WebResourceId", $"web resource '{name}'"),
                name,
                Value(element, "DisplayName"),
                Int(element, "WebResourceType") ?? throw Invalid($"Web resource '{name}' has no WebResourceType."),
                File(Required(element, "FileName", $"web resource '{name}'"), $"web resource '{name}'"),
                Value(element, "Description"));
        }

        private PackageAssembly Assembly(XElement element)
        {
            var fullName = NonEmpty((string?)element.Attribute("FullName")) ?? throw Invalid("customizations.xml: a PluginAssembly has no FullName.");
            var id = GuidAttribute(element, "PluginAssemblyId") ?? throw Invalid($"Plug-in assembly '{fullName}' has no PluginAssemblyId.");
            var types = (element.Element("PluginTypes")?.Elements("PluginType") ?? []).Select(type =>
            {
                var typeName = NonEmpty((string?)type.Attribute("Name")) ?? throw Invalid($"Plug-in assembly '{fullName}' has a type without a Name.");
                return new PackagePluginType(
                    GuidAttribute(type, "PluginTypeId") ?? throw Invalid($"Plug-in type '{typeName}' has no PluginTypeId."),
                    typeName,
                    NonEmpty((string?)type.Attribute("AssemblyQualifiedName")) ?? $"{typeName}, {fullName}",
                    Value(type, "FriendlyName"),
                    NonEmpty(Value(type, "WorkflowActivityGroupName")));
            }).ToList();

            return new PackageAssembly(id, fullName.Split(',')[0].Trim(), fullName, Int(element, "IsolationMode") ?? 2,
                Int(element, "SourceType") ?? 0, File(Required(element, "FileName", $"plug-in assembly '{fullName}'"),
                    $"plug-in assembly '{fullName}'"), types);
        }

        private static PackageStep Step(XElement element)
        {
            var name = NonEmpty((string?)element.Attribute("Name")) ?? throw Invalid("customizations.xml: an SdkMessageProcessingStep has no Name.");
            var where = $"step '{name}'";
            // A step on a service endpoint (a webhook, plan 3.2) is not a solution component yet.
            if (Int(element, "EventHandlerTypeCode") == ServiceEndpointSchema.ObjectTypeCode) throw Invalid(ServiceEndpointSchema.NotInSolution);
            var primaryEntity = Value(element, "PrimaryEntity")?.ToLowerInvariant();
            var images = (element.Element("SdkMessageProcessingStepImages")?.Elements("SdkMessageProcessingStepImage") ?? []).Select(image =>
            {
                var imageName = NonEmpty((string?)image.Attribute("Name")) ?? throw Invalid($"The {where} has an image without a Name.");
                var imageWhere = $"image '{imageName}' of {where}";
                return new PackageStepImage(
                    RequiredGuid(image, "SdkMessageProcessingStepImageId", imageWhere),
                    imageName,
                    Required(image, "EntityAlias", imageWhere),
                    Int(image, "ImageType") ?? throw Invalid($"The {imageWhere} has no ImageType."),
                    Required(image, "MessagePropertyName", imageWhere),
                    Value(image, "Attributes"));
            }).ToList();

            return new PackageStep(
                GuidAttribute(element, "SdkMessageProcessingStepId") ?? throw Invalid($"The {where} has no SdkMessageProcessingStepId."),
                name,
                Value(element, "Description"),
                RequiredGuid(element, "SdkMessageId", where),
                Guid(element, "SdkMessageFilterId"),
                primaryEntity is "none" ? null : primaryEntity,
                RequiredGuid(element, "PluginTypeId", where),
                Required(element, "PluginTypeName", where),
                Int(element, "Stage") ?? throw Invalid($"The {where} has no Stage."),
                Int(element, "Mode") ?? 0,
                Int(element, "Rank") ?? 1,
                Int(element, "InvocationSource") ?? 0,
                Int(element, "SupportedDeployment") ?? 0,
                Bool(element, "AsyncAutoDelete") ?? false,
                Value(element, "FilteringAttributes"),
                Value(element, "CustomConfiguration") ?? Value(element, "Configuration"),
                Guid(element, "ImpersonatingUserId"),
                Int(element, "StateCode") == 1,
                images);
        }

        // ------------------------------------------------------------------ entity maps

        /// <summary>
        /// An <c>EntityMap</c> (#143): <c>EntitySource</c>, <c>EntityTarget</c> and the <c>AttributeSource</c>/<c>AttributeTarget</c> of
        /// each <c>AttributeMaps/AttributeMap</c>, lower-cased. A missing name is a 400: a map cannot be applied without it.
        /// </summary>
        private static PackageEntityMap EntityMap(XElement element)
        {
            var source = Lower(Required(element, "EntitySource", "an entity map"));
            var target = Lower(Required(element, "EntityTarget", "an entity map"));
            var owner = $"an attribute map from '{source}' to '{target}'";
            var attributes = (element.Element("AttributeMaps")?.Elements("AttributeMap") ?? [])
                .Select(a => new PackageAttributeMap(Lower(Required(a, "AttributeSource", owner)), Lower(Required(a, "AttributeTarget", owner))))
                .ToList();
            return new PackageEntityMap(source, target, attributes);
        }

        // ------------------------------------------------------------------ processes

        /// <summary>
        /// The <c>Workflows</c> node (plan 2.14, rulings W2 and W22): each <c>Workflow</c> of category 0, 2 or 3 is a
        /// <see cref="PackageWorkflow"/> with its XAML file; any other child (another category, or a process without a
        /// <c>Category</c>, which cannot be classified) stays in the kept node. A node with no process of those categories is kept as
        /// it is (OpenProject365's modern flows).
        /// </summary>
        private void ReadWorkflows(XElement node)
        {
            if (!node.Elements("Workflow").Any(IsRunnableCategory))
            {
                if (node.HasElements) Unsupported.Add(new XElement(node));
                return;
            }
            var kept = new XElement(node.Name, node.Attributes());
            foreach (var child in node.Nodes())
            {
                if (child is XElement { Name.LocalName: "Workflow" } element && IsRunnableCategory(element))
                {
                    var workflow = Workflow(element);
                    Source(ComponentType.Workflow, workflow.Id.ToString("D"), element, FileOf(element, "XamlFileName"));
                    Workflows.Add(workflow);
                }
                else if (child is XElement other) kept.Add(new XElement(other));
            }
            if (kept.HasElements) Unsupported.Add(kept);
        }

        /// <summary>True for a <c>Workflow</c> whose <c>Category</c> is one xrmverse runs (W2).</summary>
        private static bool IsRunnableCategory(XElement workflow) =>
            Value(workflow, "Category") is { } category && WorkflowCategories.Contains(category);

        private static readonly HashSet<string> WorkflowCategories = ["0", "2", "3"];

        /// <summary>The elements of a <c>Workflow</c> that <see cref="PackageWorkflow"/> holds as properties, not in its fields.</summary>
        private static readonly HashSet<string> WorkflowProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "XamlFileName", "Category", "Mode", "Scope", "PrimaryEntity", "StateCode", "ProcessTriggers",
        };

        /// <summary>The elements of a <c>Workflow</c> whose value is a number, checked as the reader checks any other.</summary>
        private static readonly string[] WorkflowNumbers =
            ["Type", "StatusCode", "CreateStage", "UpdateStage", "DeleteStage", "RunAs", "Rank", "ProcessTriggerScope"];

        /// <summary>The elements of a <c>Workflow</c> whose value is a flag.</summary>
        private static readonly string[] WorkflowFlags =
            ["Subprocess", "OnDemand", "TriggerOnCreate", "TriggerOnDelete", "AsyncAutodelete", "SyncWorkflowLogOnFailure", "IsTransacted", "IsCustomizable"];

        private PackageWorkflow Workflow(XElement element)
        {
            var id = GuidAttribute(element, "WorkflowId") ?? throw Invalid("customizations.xml: a Workflow has no WorkflowId.");
            var name = NonEmpty((string?)element.Attribute("Name")) ?? throw Invalid($"The process {id} has no Name.");
            var owner = $"process '{name}'";
            var xamlPath = Required(element, "XamlFileName", owner);
            var bytes = File(xamlPath, owner);
            string xaml;
            try
            {
                xaml = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                throw Invalid($"The XAML file '{xamlPath.TrimStart('/')}' of {owner} is not UTF-8 text.");
            }

            // The other elements with a text value, as written; the numbers and flags are checked here, so the import can trust them.
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in element.Elements().Where(e => !e.HasElements && !WorkflowProperties.Contains(e.Name.LocalName)))
                fields.TryAdd(child.Name.LocalName, IsNil(child) ? "" : child.Value);
            foreach (var number in WorkflowNumbers) Int(element, number);
            foreach (var flag in WorkflowFlags) Bool(element, flag);

            var triggers = (element.Element("ProcessTriggers")?.Elements("ProcessTrigger") ?? []).Select(trigger =>
            {
                if (ProcessTriggerId(trigger) is null)
                    throw Invalid($"A ProcessTrigger of {owner} has no processtriggerid.");
                return new XElement(trigger);
            }).ToList();

            return new PackageWorkflow(id, name,
                Int(element, "Category")!.Value,
                Int(element, "Mode") ?? 0,
                Int(element, "Scope") ?? 1,
                Value(element, "PrimaryEntity"),
                xamlPath,
                xaml,
                Int(element, "StateCode") ?? 0,
                fields,
                triggers,
                new XElement(element));
        }

        /// <summary>The bytes of the archive entry a <c>FileName</c> names (a path from the archive root, with a leading <c>/</c>).</summary>
        private byte[] File(string fileName, string owner)
        {
            var path = fileName.Replace('\\', '/').TrimStart('/');
            if (!files.TryGetValue(path, out var content)) throw Invalid($"The solution file is missing '{path}' of {owner}.");
            consumed.Add(path);
            return content;
        }

        private readonly HashSet<string> consumed = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Marks archive entries another reader consumed (the files of <c>customapis/</c>, <c>environmentvariabledefinitions/</c> and of the custom controls).</summary>
        public void Consume(IEnumerable<string> paths) => consumed.UnionWith(paths);

        /// <summary>The archive entries no component read, except the content types (rewritten by every export).</summary>
        public Dictionary<string, byte[]> KeptFiles() =>
            files.Where(f => !consumed.Contains(f.Key) && !f.Key.Equals(SolutionZip.ContentTypesEntry, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------------- XML values

    /// <summary>The trimmed text of a child element; null when it is missing, empty or <c>xsi:nil</c>.</summary>
    internal static string? Value(XElement parent, string name)
    {
        var element = parent.Element(name);
        if (element is null || IsNil(element)) return null;
        return NonEmpty(element.Value.Trim());
    }

    /// <summary>True for an element with <c>xsi:nil="true"</c>: no value.</summary>
    internal static bool IsNil(XElement element) => (bool?)element.Attribute(Xsi + "nil") == true;

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Lower(string value) => value.ToLowerInvariant();

    private static string Required(XElement parent, string name, string owner) =>
        Value(parent, name) ?? throw Invalid($"The solution file has no {name} for {owner}.");

    private static int? Int(XElement parent, string name) => Parse(Value(parent, name), name,
        v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : (int?)null);

    private static int? IntAttribute(XElement element, string name) => Parse(NonEmpty((string?)element.Attribute(name)), name,
        v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : (int?)null);

    private static decimal? Decimal(XElement parent, string name) => Parse(Value(parent, name), name,
        v => decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : (decimal?)null);

    private static double? Double(XElement parent, string name) => Parse(Value(parent, name), name,
        v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : (double?)null);

    private static bool? Bool(XElement parent, string name) => Parse(Value(parent, name), name, v => v.ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => (bool?)null,
    });

    private static Guid? Guid(XElement parent, string name) => Parse(Value(parent, name), name,
        v => System.Guid.TryParse(v, out var r) ? r : (Guid?)null);

    private static Guid? GuidAttribute(XElement element, string name) => Parse(NonEmpty((string?)element.Attribute(name)), name,
        v => System.Guid.TryParse(v, out var r) ? r : (Guid?)null);

    private static Guid RequiredGuid(XElement parent, string name, string owner) =>
        Guid(parent, name) ?? throw Invalid($"The solution file has no {name} for {owner}.");

    private static T? Parse<T>(string? value, string name, Func<string, T?> parse) where T : struct =>
        value is null ? null : parse(value) ?? throw Invalid($"The solution file has the value '{value}' for {name}, which is not valid.");

    /// <summary>An enum value of an element, by name ignoring case; null when the element is missing.</summary>
    private static T? EnumValue<T>(XElement parent, string name, string where) where T : struct, Enum =>
        Value(parent, name) is { } value
            ? Enum.TryParse<T>(value, ignoreCase: true, out var result) && Enum.IsDefined(result)
                ? result
                : throw Invalid($"The {where} has the {name} '{value}', which xrmverse does not support.")
            : null;

    /// <summary>
    /// The labels of a container (<c>&lt;displaynames&gt;&lt;displayname description=".." languagecode=".."/&gt;</c>);
    /// empty descriptions are no label. <c>UserLocalizedLabel</c> is the one in the base language (else the first).
    /// </summary>
    private static Label Labels(XElement? container, string itemName, int languageCode)
    {
        var labels = (container?.Elements(itemName) ?? [])
            .Select(e => (Text: (string?)e.Attribute("description"), Language: IntAttribute(e, "languagecode")))
            .Where(l => !string.IsNullOrEmpty(l.Text) && l.Language is not null)
            .Select(l => new LocalizedLabel(l.Text, l.Language!.Value))
            .ToArray();
        if (labels.Length == 0) return new Label();
        return new Label(labels.FirstOrDefault(l => l.LanguageCode == languageCode) ?? labels[0], labels);
    }

    /// <summary>
    /// The id of a <c>ProcessTrigger</c>: its <c>processtriggerid</c> attribute, or a child element of that name (the element names of
    /// a real export are not verified, checklist 1h); null when it has none.
    /// </summary>
    internal static Guid? ProcessTriggerId(XElement trigger)
    {
        var text = (string?)trigger.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("processtriggerid", StringComparison.OrdinalIgnoreCase))
                   ?? (string?)trigger.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("processtriggerid", StringComparison.OrdinalIgnoreCase));
        return System.Guid.TryParse(text?.Trim(), out var id) ? id : null;
    }

    private static XrmdFaultException Invalid(string message) => XrmdFaultException.InvalidArgument(message);
}
