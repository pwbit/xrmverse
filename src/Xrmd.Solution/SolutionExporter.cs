using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.Solution;

/// <summary>
/// ExportSolution (unmanaged, spec §7): the solution file of a solution, read from what xrmverse holds now. The components
/// are the solution's <c>solutioncomponent</c> rows; their metadata comes from the published cache, their rows from the
/// database; what an import kept without applying (<c>solution.xrmverse_unsupportedxml</c>: unsupported nodes, the solution's
/// names in every language, the publisher's details) is written again in place. A process (component type 29, plan 2.14) is
/// written in the <c>Workflow</c> element its import kept, with its row's values and its stored XAML byte for byte. The export
/// reads only: it writes no metadata and takes no metadata lock.
/// </summary>
/// <remarks>
/// <para>Subcomponents. An entity with <c>rootcomponentbehavior</c> 0 (include subcomponents; the Default solution records
/// entities this way, without attribute rows) is written with all its attributes, the custom relationships it references
/// with a custom lookup or is a side of, and its views and forms. With behavior 1 or 2 it is written with its primary key,
/// its primary name and the attributes the solution holds. A relationship, view, form or attribute the solution holds whose
/// entity it does not hold brings that entity as a shell (primary key, primary name, the lookup a relationship needs).</para>
/// <para>Not written: the system relationships (xrmverse derives them from the lookups with names of its own, which a
/// Dataverse organization does not have; an import there creates its own) and the global option sets the solution does not
/// hold (a missing dependency of the file, as in a Dataverse export).</para>
/// <para>Refused (400, nothing read): <c>Managed</c> true, every <c>Export*</c> setting set to true, a
/// <c>TargetVersion</c>, <c>ExportComponentsParams</c>, any other parameter; the system solutions; a step on a message
/// whose Dataverse id xrmverse does not know (<see cref="DataverseSdkMessages"/>); content the solution file cannot carry.</para>
/// </remarks>
public sealed class SolutionExporter(IMetadataStore store, IDbConnectionFactory factory, SecurityGuard guard, SolutionComponentTracker tracker)
{
    /// <summary>The files of the custom controls (plan 3.5 UI-1d, R4), read in the export's session.</summary>
    private readonly ICustomControlFiles controlFiles = new CustomControlFiles(factory);

    /// <summary>The base language of the rows of the organization (their names have no translations).</summary>
    private const int OrganizationLanguage = 1033;

    /// <summary>
    /// The <c>Export*</c> settings of <c>ExportSolutionRequest</c> (Learn, ExportSolutionRequest properties): each tells
    /// Dataverse to add organization settings xrmverse does not have to the file. False (the default ServiceClient sends,
    /// or missing) is accepted; true is refused.
    /// </summary>
    private static readonly string[] Settings =
    [
        "ExportAutoNumberingSettings", "ExportCalendarSettings", "ExportCustomizationSettings", "ExportEmailTrackingSettings",
        "ExportExternalApplications", "ExportGeneralSettings", "ExportIsvConfig", "ExportMarketingSettings",
        "ExportOutlookSynchronizationSettings", "ExportRelationshipRoles", "ExportSales",
    ];

    /// <summary>The <c>systemform.type</c> values and the <c>forms/@type</c> an export writes (reverse of the importer's; not verified on Learn).</summary>
    private static readonly Dictionary<int, string> FormTypes = new()
    {
        [0] = "dashboard", [2] = "main", [5] = "mobile", [6] = "quick", [7] = "quickCreate", [11] = "card",
    };

    /// <summary>
    /// The solution's unique name of an <c>ExportSolution</c> request, after checking every option: <c>SolutionName</c>
    /// (required), <c>Managed</c> (false only), each <c>Export*</c> setting (false only), <c>TargetVersion</c> (none),
    /// <c>ExportComponentsParams</c> (none); any other parameter is refused too.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the option xrmverse cannot honor or the missing name.</exception>
    public static string ReadRequest(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var (name, value) in request.Parameters)
        {
            switch (name)
            {
                case "SolutionName":
                    break;
                case "Managed":
                    if (Flag(name, value))
                        throw XrmdFaultException.InvalidArgument("Managed is not supported: xrmverse exports unmanaged solutions only.");
                    break;
                case var setting when Settings.Contains(setting, StringComparer.Ordinal):
                    if (Flag(setting, value))
                        throw XrmdFaultException.InvalidArgument(
                            $"{setting} is not supported: xrmverse has no such organization settings to export.");
                    break;
                case "TargetVersion":
                    if (value is string { Length: > 0 } version)
                        throw XrmdFaultException.InvalidArgument(
                            $"TargetVersion is not supported: xrmverse writes one solution format, not one for version '{version}'.");
                    break;
                case "ExportComponentsParams":
                    if (value is not null)
                        throw XrmdFaultException.InvalidArgument(
                            "ExportComponentsParams is not supported: xrmverse exports every component of the solution.");
                    break;
                default:
                    throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of ExportSolution is not supported by xrmverse.");
            }
        }
        return request.Parameters.TryGetValue("SolutionName", out var solution) && solution is string { Length: > 0 } uniqueName
            ? uniqueName
            : throw XrmdFaultException.InvalidArgument("Required parameter 'SolutionName' of request 'ExportSolution' is missing or empty.");
    }

    /// <summary>The value of a boolean option (null: not set, false); 400 naming it for any other type.</summary>
    private static bool Flag(string name, object? value) => value switch
    {
        null => false,
        bool flag => flag,
        _ => throw XrmdFaultException.InvalidArgument(
            $"The parameter '{name}' of ExportSolution must be a Boolean, not {value.GetType().Name} ('{value}')."),
    };

    /// <summary>
    /// ExportSolution for <paramref name="caller"/>: reads the request (<see cref="ReadRequest"/>), demands
    /// <c>prvExportCustomization</c> before opening any connection, then exports on a connection of its own with no
    /// transaction (a plain data read).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a refused option; 403 without the privilege; the faults of
    /// <see cref="ExportAsync(DataSession, string, CancellationToken)"/>.</exception>
    public async Task<byte[]> ExportAsync(Caller caller, OrganizationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var uniqueName = ReadRequest(request);
        await guard.DemandPrivilegeAsync(caller, PrivilegeNames.ExportCustomization, "solution", ct);
        await using var conn = await factory.OpenAsync(ct);
        return await ExportAsync(new DataSession(conn, null), uniqueName, ct);
    }

    /// <summary>The solution file (<c>solution.zip</c>) of the solution <paramref name="uniqueName"/>, read in <paramref name="s"/>.</summary>
    /// <exception cref="XrmdFaultException">404 for an unknown solution; 400 for a system solution, a managed solution (plan 2.6,
    /// ruling M14) or content the file cannot carry.</exception>
    public async Task<byte[]> ExportAsync(DataSession s, string uniqueName, CancellationToken ct)
    {
        var package = await PackageAsync(s, uniqueName, ct);
        var (solution, customizations) = CustomizationsWriter.Write(package);
        return SolutionZip.Write(solution, customizations, CustomizationsWriter.Files(package));
    }

    /// <summary>The package of the solution <paramref name="uniqueName"/> (see the class remarks).</summary>
    /// <exception cref="XrmdFaultException">See <see cref="ExportAsync(DataSession, string, CancellationToken)"/>.</exception>
    public async Task<SolutionPackage> PackageAsync(DataSession s, string uniqueName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(uniqueName);
        if (string.Equals(uniqueName, SystemSeed.DefaultSolutionName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uniqueName, SystemSeed.ActiveSolutionName, StringComparison.OrdinalIgnoreCase))
            throw XrmdFaultException.InvalidArgument($"The system solution '{uniqueName}' cannot be exported.");
        var solutionId = await tracker.SolutionIdAsync(s, uniqueName, ct);
        await DemandUnmanagedAsync(s, solutionId, uniqueName, ct);
        var header = await SolutionRowAsync(s, solutionId, ct);
        var components = await ComponentsAsync(s, solutionId, ct);
        return await ComposeAsync(s, header, components, solutionId, ct);
    }

    /// <summary>
    /// The payload of the component <paramref name="id"/> of type <paramref name="type"/> as it is now (plan 2.6, ruling M5: the
    /// System layer of a seeded component, captured the first time a managed solution layers it): the component is written
    /// alone with the exporter's writer (a table as a shell, without its columns), read back with
    /// <see cref="CustomizationsReader"/> and turned into a payload by <see cref="Layers.LayerPayload"/>, so the fragment has the
    /// shape the reader parses and a restore (<see cref="Layers.LayerRestorer"/>) applies.
    /// </summary>
    /// <returns>The payload JSON (<see cref="Layers.LayerPayload"/>).</returns>
    /// <exception cref="XrmdFaultException">400 for a component type xrmverse does not export or content the file cannot carry.</exception>
    /// <exception cref="InvalidOperationException">When no component of that type has that id.</exception>
    public async Task<string> WriteComponentFragment(DataSession s, int type, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        type = ComponentType.Normalize(type);
        if (!ComponentType.IsSupported(type) || type is ComponentType.RolePrivilege)
            throw XrmdFaultException.InvalidArgument($"A component of type {type} ({id}) has no layer payload: xrmverse cannot export it.");
        var components = new List<Component> { new(type, id, type == ComponentType.Entity ? 1 : 0) };
        // A custom lookup is read only with its relationships (the reader refuses one without): they travel together.
        if (type == ComponentType.Attribute && store.GetAllEntities().SelectMany(e => e.Attributes ?? []).FirstOrDefault(a => a.MetadataId == id)
                is LookupAttributeMetadata { EntityLogicalName: { } lookupEntity, LogicalName: { } lookupName })
            components.AddRange(store.GetOneToManyRelationships()
                .Where(r => r.IsCustomRelationship == true && r.ReferencingEntity == lookupEntity && r.ReferencingAttribute == lookupName)
                .Select(r => new Component(ComponentType.EntityRelationship, r.MetadataId!.Value, 0)));
        var header = new SolutionRow(FragmentSolution, FragmentSolution, "1.0.0.0", null, null, FragmentSolution, null, FragmentPrefix, 10000);
        var package = await ComposeAsync(s, header, components, solutionId: null, ct);
        var (solutionXml, customizations) = CustomizationsWriter.Write(package);
        var read = CustomizationsReader.Read(solutionXml, customizations, CustomizationsWriter.Files(package));
        return Layers.LayerPayload.From(read, type, FragmentKey(type, id, read));
    }

    /// <summary>The placeholder solution and publisher name of <see cref="WriteComponentFragment"/>'s file.</summary>
    private const string FragmentSolution = "xrmverselayer";

    /// <summary>The placeholder publisher prefix of <see cref="WriteComponentFragment"/>'s file.</summary>
    private const string FragmentPrefix = "xrmverse";

    /// <summary>The key (<see cref="Layers.LayerPayload.Key"/>) of the component written alone in <paramref name="read"/>.</summary>
    private string FragmentKey(int type, Guid id, SolutionPackage read) => type switch
    {
        ComponentType.Entity => store.GetAllEntities().First(e => e.MetadataId == id).LogicalName,
        ComponentType.Attribute => store.GetAllEntities()
            .SelectMany(e => (e.Attributes ?? []).Select(a => (Entity: e.LogicalName, Attribute: a)))
            .Where(p => p.Attribute.MetadataId == id).Select(p => $"{p.Entity}.{p.Attribute.LogicalName}").First(),
        ComponentType.EntityRelationship => read.Relationships.Single().SchemaName,
        ComponentType.OptionSet => read.OptionSets.Single().Name,
        ComponentType.SiteMap or ComponentType.AppModule => read.Opaque.Single(o => o.Type == type).Id,
        ComponentType.CustomApi => read.CustomApis!.Single().UniqueName,
        ComponentType.EnvironmentVariableDefinition => read.EnvironmentVariables.Single().SchemaName,
        ComponentType.CustomControl => read.CustomControls.Single().Name,
        _ => id.ToString("D"),
    };

    /// <summary>The package of <paramref name="components"/> under <paramref name="header"/>.</summary>
    /// <param name="solutionId">The solution exported, for its missing dependencies; null for none (a fragment).</param>
    private async Task<SolutionPackage> ComposeAsync(DataSession s, SolutionRow header, List<Component> components, Guid? solutionId,
        CancellationToken ct)
    {
        var kept = KeptXml.Parse(header.KeptXml);
        var language = kept.LanguageCode ?? OrganizationLanguage;
        var builder = new Builder(store);
        builder.AddMetadata(components);
        var roles = await RolesAsync(s, components, ct);
        var profiles = await FieldSecurityProfilesAsync(s, components, ct);
        var keys = new Dictionary<Guid, string>();
        var opaque = await OpaqueAsync(s, components, builder, language, keys, ct);
        builder.AddViewsAndFormsOf(opaque);
        var unmodelled = new Dictionary<(int Type, string Key), XElement>();
        var webResources = await WebResourcesAsync(s, components, unmodelled, ct);
        var (assemblies, typeAssemblies) = await AssembliesAsync(s, components, unmodelled, ct);
        var steps = await StepsAsync(s, components, assemblies, unmodelled, ct);
        var customApis = await CustomApisAsync(s, components, language, ct);
        var environmentVariables = await EnvironmentVariablesAsync(s, components, language, ct);
        var customControls = await CustomControlsAsync(s, components, ct);
        var workflows = await WorkflowsAsync(s, components, language, ct);
        var entities = builder.Entities();
        var lookups = builder.Lookups();
        var entityNames = entities.Select(e => e.LogicalName).ToHashSet(StringComparer.Ordinal);
        // What xrmverse does not model of the metadata written, as the store keeps it (UnmodelledXml).
        void KeepMetadata(int type, string key, (int, string) packageKey, string name, string component)
        {
            if (Kept(store.GetUnmodelledXml(type, key), name, component) is { } xml) unmodelled[packageKey] = xml;
        }
        foreach (var entity in entities)
        {
            KeepMetadata(ComponentType.Entity, entity.LogicalName, UnmodelledXml.EntityKey(entity.LogicalName), "entity", $"entity '{entity.LogicalName}'");
            foreach (var attribute in entity.Attributes ?? [])
                KeepMetadata(ComponentType.Attribute, $"{entity.LogicalName}.{attribute.LogicalName}",
                    UnmodelledXml.AttributeKey(entity.LogicalName, attribute.LogicalName), "attribute",
                    $"attribute '{entity.LogicalName}.{attribute.LogicalName}'");
        }
        foreach (var lookup in lookups)
            KeepMetadata(ComponentType.Attribute, $"{lookup.EntityLogicalName}.{lookup.LogicalName}",
                UnmodelledXml.AttributeKey(lookup.EntityLogicalName!, lookup.LogicalName), "attribute",
                $"attribute '{lookup.EntityLogicalName}.{lookup.LogicalName}'");
        var relationships = builder.Relationships();
        foreach (var relationship in relationships)
            KeepMetadata(ComponentType.EntityRelationship, relationship.SchemaName, UnmodelledXml.RelationshipKey(relationship.SchemaName),
                "EntityRelationship", $"relationship '{relationship.SchemaName}'");
        var optionSets = builder.OptionSets();
        foreach (var optionSet in optionSets.Where(o => o.Name is not null))
            KeepMetadata(ComponentType.OptionSet, optionSet.Name!, UnmodelledXml.OptionSetKey(optionSet.Name!), "optionset",
                $"option set '{optionSet.Name}'");
        var missing = solutionId is { } exported ? await MissingDependenciesAsync(s, exported, builder, ct) : [];

        var package = new SolutionPackage(
            new PackagePublisher(header.PublisherName, header.Prefix, header.OptionValuePrefix,
                WithBase(kept.Publisher?.Element("LocalizedNames") is { } names ? PublisherNames(names, language) : null, header.PublisherFriendlyName, language),
                kept.Publisher is { } publisher && string.Equals((string?)publisher.Element("UniqueName"), header.PublisherName, StringComparison.OrdinalIgnoreCase)
                    ? publisher
                    : null),
            new PackageSolution(header.UniqueName, header.Version, false, WithBase(kept.SolutionNames, header.FriendlyName, language), language,
                WithBase(kept.SolutionDescriptions, header.Description, language)),
            entities,
            lookups,
            relationships,
            optionSets,
            roles,
            opaque.Where(o => o.EntityLogicalName is null || entityNames.Contains(o.EntityLogicalName)).ToList(),
            webResources,
            assemblies,
            steps,
            // A FieldSecurityProfiles node an older import kept as unsupported gives way to the profiles the solution holds.
            // So does a CustomControls node to the custom controls (plan 3.5 UI-1d).
            kept.Unsupported.Where(u => !(profiles.Count > 0 && u.Name.LocalName == "FieldSecurityProfiles")
                                        && !(customControls.Count > 0 && u.Name.LocalName == "CustomControls")).ToList(),
            kept.EntityUnsupported.Where(e => entityNames.Contains(e.EntityLogicalName)).ToList(),
            [],
            RootComponents(components, builder, opaque, keys, webResources, typeAssemblies, steps, environmentVariables, customControls),
            builder.Menus(),
            Unmodelled: unmodelled,
            MissingDependencies: missing,
            KeptFiles: kept.Files,
            FieldSecurityProfiles: profiles,
            CustomApis: customApis)
        {
            Workflows = workflows,
            EnvironmentVariables = environmentVariables.Select(v => v.Variable).ToList(),
            CustomControls = customControls.Select(c => c.Control).ToList(),
        };
        return package;
    }

    // ---- the solution, its publisher and its components ---------------------------------------------------------------

    private sealed record SolutionRow(string UniqueName, string FriendlyName, string Version, string? Description, string? KeptXml,
        string PublisherName, string? PublisherFriendlyName, string Prefix, int OptionValuePrefix);

    /// <summary>
    /// 400 for a managed solution (plan 2.6, ruling M14): a managed solution is not exported, and xrmverse produces no managed
    /// file (ruling M17). An unmanaged solution holding managed components exports as any other.
    /// </summary>
    private static async Task DemandUnmanagedAsync(DataSession s, Guid solutionId, string uniqueName, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT CAST({s.Dialect.IsNull("ismanaged", s.Dialect.BooleanLiteral(false))} AS {s.Dialect.ColumnType(SqlColumnKind.Boolean)}) FROM {s.DataTable("solution")} WHERE solutionid = @id");
        cmd.AddParameter("@id", DbType.Guid, solutionId);
        if (await cmd.ExecuteScalarAsync(ct) is true)
            throw XrmdFaultException.InvalidArgument(
                $"The solution '{uniqueName}' is managed: a managed solution cannot be exported (export the unmanaged solution it was built from).");
    }

    private static async Task<SolutionRow> SolutionRowAsync(DataSession s, Guid solutionId, CancellationToken ct)
    {
        await using var cmd = s.Command($"""
            SELECT s.uniquename, s.friendlyname, s.version, s.description, s.xrmverse_unsupportedxml,
                   p.uniquename, p.friendlyname, p.customizationprefix, p.customizationoptionvalueprefix
            FROM {s.DataTable("solution")} AS s LEFT JOIN {s.DataTable("publisher")} AS p ON p.publisherid = s.publisherid
            WHERE s.solutionid = @id
            """);
        cmd.AddParameter("@id", DbType.Guid, solutionId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist("solution", solutionId);
        string? Text(int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
        var uniqueName = rd.GetString(0);
        if (rd.IsDBNull(5))
            throw XrmdFaultException.InvalidArgument($"The solution '{uniqueName}' has no publisher: a solution file needs one.");
        return new SolutionRow(uniqueName, Text(1) ?? uniqueName, Text(2) ?? "1.0.0.0", Text(3), Text(4), rd.GetString(5), Text(6),
            Text(7) ?? throw XrmdFaultException.InvalidArgument($"The publisher '{rd.GetString(5)}' has no customization prefix."),
            rd.IsDBNull(8) ? throw XrmdFaultException.InvalidArgument($"The publisher '{rd.GetString(5)}' has no option value prefix.") : rd.GetInt32(8));
    }

    /// <summary>A <c>solutioncomponent</c> row (type 3 read as 10).</summary>
    private sealed record Component(int Type, Guid ObjectId, int Behavior);

    private static async Task<List<Component>> ComponentsAsync(DataSession s, Guid solutionId, CancellationToken ct)
    {
        var components = new List<Component>();
        await using var cmd = s.Command(
            $"SELECT componenttype, objectid, rootcomponentbehavior FROM {s.DataTable("solutioncomponent")} WHERE solutionid = @id");
        cmd.AddParameter("@id", DbType.Guid, solutionId);
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
                components.Add(new(ComponentType.Normalize(rd.GetInt32(0)), rd.GetGuid(1), rd.IsDBNull(2) ? 0 : rd.GetInt32(2)));
        }
        foreach (var component in components.Where(c => !ComponentType.IsSupported(c.Type) && c.Type != ComponentType.RolePrivilege))
            throw XrmdFaultException.InvalidArgument(
                $"The solution holds a component of type {component.Type} ({component.ObjectId}), which xrmverse cannot export.");
        return components;
    }

    private static IEnumerable<Guid> Of(IEnumerable<Component> components, int type) =>
        components.Where(c => c.Type == type).Select(c => c.ObjectId);

    private static Label WithBase(Label? kept, string? baseText, int language)
    {
        // The row's name (or description) is the base-language label: it wins over the kept one, which gives the others.
        var labels = (kept?.LocalizedLabels ?? []).Where(l => l.LanguageCode != language).ToList();
        if (!string.IsNullOrEmpty(baseText)) labels.Insert(0, new LocalizedLabel(baseText, language));
        if (labels.Count == 0) return new Label();
        return new Label(labels.FirstOrDefault(l => l.LanguageCode == language) ?? labels[0], labels.ToArray());
    }

    private static Label PublisherNames(XElement names, int language)
    {
        var labels = names.Elements("LocalizedName")
            .Where(n => !string.IsNullOrEmpty((string?)n.Attribute("description")))
            .Select(n => new LocalizedLabel((string)n.Attribute("description")!, int.Parse((string)n.Attribute("languagecode")!, CultureInfo.InvariantCulture)))
            .ToArray();
        return labels.Length == 0 ? new Label() : new Label(labels.FirstOrDefault(l => l.LanguageCode == language) ?? labels[0], labels);
    }

    // ---- metadata (published cache) -----------------------------------------------------------------------------------

    /// <summary>Collects the entities (with the attributes written), relationships and option sets of the export.</summary>
    private sealed class Builder(IMetadataStore store)
    {
        private readonly Dictionary<string, (EntityMetadata Entity, bool All, HashSet<string> Attributes)> _entities = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RelationshipMetadataBase> _relationships = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<OptionSetMetadataBase> _optionSets = [];

        /// <summary>The entities the solution holds with behavior 0 (include subcomponents).</summary>
        public HashSet<string> Full { get; } = new(StringComparer.Ordinal);

        /// <summary>The entities the solution holds as components (whatever the behavior).</summary>
        public HashSet<string> Held { get; } = new(StringComparer.Ordinal);

        /// <summary>The relationships the solution holds as components, by SchemaName.</summary>
        public HashSet<string> HeldRelationships { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The attribute components the solution holds whose entity it does not hold (root components of type 2).</summary>
        public List<Guid> RootAttributes { get; } = [];

        public void AddMetadata(List<Component> components)
        {
            var entities = store.GetAllEntities();
            EntityMetadata EntityOf(Guid id, int type) => entities.FirstOrDefault(e => e.MetadataId == id)
                ?? throw new InvalidOperationException($"Component {id} of type {type} of the solution is not an entity of the metadata.");

            foreach (var component in components.Where(c => c.Type == ComponentType.Entity))
            {
                var entity = EntityOf(component.ObjectId, component.Type);
                Held.Add(entity.LogicalName);
                if (component.Behavior == 0)
                {
                    Full.Add(entity.LogicalName);
                    Include(entity, all: true);
                }
                else Include(entity, all: false);
            }

            foreach (var component in components.Where(c => c.Type == ComponentType.Attribute))
            {
                var entity = entities.FirstOrDefault(e => (e.Attributes ?? []).Any(a => a.MetadataId == component.ObjectId))
                             ?? throw new InvalidOperationException($"Component {component.ObjectId} of type 2 of the solution is not an attribute of the metadata.");
                var attribute = entity.Attributes.First(a => a.MetadataId == component.ObjectId);
                Include(entity, all: false, attribute.LogicalName);
                if (!Held.Contains(entity.LogicalName)) RootAttributes.Add(component.ObjectId);
                Require(entity.LogicalName, new PackageDependencyNode(ComponentType.Attribute, attribute.LogicalName, Base(attribute.DisplayName),
                    ParentSchemaName: entity.LogicalName, ParentDisplayName: Base(entity.DisplayName)));
            }

            var oneToMany = store.GetOneToManyRelationships();
            var manyToMany = store.GetManyToManyRelationships();
            foreach (var component in components.Where(c => c.Type == ComponentType.EntityRelationship))
            {
                RelationshipMetadataBase relationship = oneToMany.FirstOrDefault(r => r.MetadataId == component.ObjectId) as RelationshipMetadataBase
                    ?? manyToMany.FirstOrDefault(r => r.MetadataId == component.ObjectId)
                    ?? throw new InvalidOperationException($"Component {component.ObjectId} of type 10 of the solution is not a relationship of the metadata.");
                HeldRelationships.Add(relationship.SchemaName);
                AddRelationship(relationship);
            }

            // The subcomponents of the entities held with behavior 0: their custom relationships, on either side.
            foreach (var relationship in oneToMany.Where(r => (Full.Contains(r.ReferencingEntity!) || Full.Contains(r.ReferencedEntity!)) && IsCustom(r)))
                AddRelationship(relationship);
            foreach (var relationship in manyToMany.Where(r => Full.Contains(r.Entity1LogicalName!) || Full.Contains(r.Entity2LogicalName!)))
                AddRelationship(relationship);

            var optionSets = store.GetAllOptionSets();
            foreach (var id in Of(components, ComponentType.OptionSet))
                _optionSets.Add(optionSets.FirstOrDefault(o => o.MetadataId == id || MetadataStore.OptionSetId(o.Name) == id)
                                ?? throw new InvalidOperationException($"Component {id} of type 9 of the solution is not a global option set of the metadata."));
        }

        /// <summary>
        /// A 1:N is custom when it was created (CreateOneToMany, an import): the cache derives the others from the lookups,
        /// with names of its own and no <c>IsCustomRelationship</c>.
        /// </summary>
        private static bool IsCustom(OneToManyRelationshipMetadata relationship) => relationship.IsCustomRelationship == true;

        private void AddRelationship(RelationshipMetadataBase relationship)
        {
            var node = new PackageDependencyNode(ComponentType.EntityRelationship, relationship.SchemaName, relationship.SchemaName);
            switch (relationship)
            {
                case OneToManyRelationshipMetadata oneToMany:
                    if (!IsCustom(oneToMany))
                        throw XrmdFaultException.InvalidArgument(
                            $"Relationship '{relationship.SchemaName}' is a system relationship, which xrmverse does not export.");
                    Include(store.GetEntity(oneToMany.ReferencingEntity!)!, all: false, oneToMany.ReferencingAttribute!);
                    Require(oneToMany.ReferencingEntity!, node);
                    Require(oneToMany.ReferencedEntity!, node);
                    break;
                case ManyToManyRelationshipMetadata manyToMany:
                    Include(store.GetEntity(manyToMany.Entity1LogicalName!)!, all: false);
                    Include(store.GetEntity(manyToMany.Entity2LogicalName!)!, all: false);
                    Require(manyToMany.Entity1LogicalName!, node);
                    Require(manyToMany.Entity2LogicalName!, node);
                    break;
            }
            _relationships.TryAdd(relationship.SchemaName, relationship);
        }

        /// <summary>The entity of a view or form held by the solution: written as a shell when the solution does not hold it.</summary>
        public void IncludeEntity(string logicalName) =>
            Include(store.GetEntity(logicalName) ?? throw XrmdFaultException.InvalidArgument(
                $"A view or form of the solution belongs to entity '{logicalName}', which is not in the metadata: it cannot be exported."), all: false);

        public void AddViewsAndFormsOf(List<OpaqueComponent> opaque)
        {
            foreach (var component in opaque.Where(o => o.EntityLogicalName is not null))
            {
                IncludeEntity(component.EntityLogicalName!);
                var name = component.Xml.Element("LocalizedNames")?.Elements("LocalizedName").FirstOrDefault()?.Attribute("description")?.Value;
                Require(component.EntityLogicalName!, new PackageDependencyNode(component.Type, component.Id, name));
            }
        }

        /// <summary>The components of the export that need each entity, by its logical name.</summary>
        public Dictionary<string, List<PackageDependencyNode>> Dependents { get; } = new(StringComparer.Ordinal);

        private void Require(string entity, PackageDependencyNode dependent)
        {
            if (!Dependents.TryGetValue(entity, out var list)) Dependents[entity] = list = [];
            if (!list.Contains(dependent)) list.Add(dependent);
        }

        /// <summary>
        /// The global option sets the attributes written use that the solution does not hold, each with the attributes that
        /// use it (logical names).
        /// </summary>
        public IEnumerable<(OptionSetMetadataBase OptionSet, EntityMetadata Entity, AttributeMetadata Attribute)> UnheldOptionSets()
        {
            var held = _optionSets.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _entities.Values)
                foreach (var attribute in Selected(entry))
                {
                    var optionSet = attribute switch
                    {
                        EnumAttributeMetadata e => e.OptionSet,
                        _ => null,
                    };
                    if (optionSet is { IsGlobal: true, Name: { } name } && !held.Contains(name))
                        yield return (store.GetOptionSet(name) ?? optionSet, entry.Entity, attribute);
                }
        }

        private void Include(EntityMetadata entity, bool all, params string[] attributes)
        {
            // Ruling E8: the customizations XML of an elastic table has not been observed, so no part of one is exported.
            if (ElasticTables.IsElastic(entity))
                throw XrmdFaultException.InvalidArgument(
                    $"Table '{entity.LogicalName}' is an elastic table: exporting elastic tables in a solution is not supported yet (issue #77).");
            if (!_entities.TryGetValue(entity.LogicalName, out var entry))
            {
                entry = (entity, all, new HashSet<string>(StringComparer.Ordinal));
                if (SystemAttributes.PrimaryIdName(entity) is { } primaryId) entry.Attributes.Add(primaryId);
                if (entity.PrimaryNameAttribute is { } primaryName) entry.Attributes.Add(primaryName);
            }
            entry.All |= all;
            foreach (var attribute in attributes) entry.Attributes.Add(attribute);
            _entities[entity.LogicalName] = entry;
        }

        /// <summary>The entities, each with its non-lookup attributes (lookups: <see cref="Lookups"/>), as the cache orders them.</summary>
        public List<EntityMetadata> Entities() => _entities.Values.Select(e =>
        {
            var copy = new EntityMetadata
            {
                LogicalName = e.Entity.LogicalName,
                SchemaName = e.Entity.SchemaName,
                DisplayName = e.Entity.DisplayName,
                DisplayCollectionName = e.Entity.DisplayCollectionName,
                Description = e.Entity.Description,
                OwnershipType = e.Entity.OwnershipType,
                IsActivity = e.Entity.IsActivity,
                ActivityTypeMask = e.Entity.ActivityTypeMask,
                EntitySetName = e.Entity.EntitySetName,
                IsAuditEnabled = e.Entity.IsAuditEnabled,
                AutoCreateAccessTeams = e.Entity.AutoCreateAccessTeams == true,
                ChangeTrackingEnabled = e.Entity.ChangeTrackingEnabled == true,
                IsDuplicateDetectionEnabled = new BooleanManagedProperty(e.Entity.IsDuplicateDetectionEnabled?.Value == true),
                IsValidForQueue = new BooleanManagedProperty(e.Entity.IsValidForQueue?.Value == true),
                CanChangeTrackingBeEnabled = new BooleanManagedProperty(ChangeTrackingSchema.CanBeEnabled(e.Entity)),
            };
            SdkProperty.Set(copy, nameof(EntityMetadata.PrimaryIdAttribute), e.Entity.PrimaryIdAttribute);
            SdkProperty.Set(copy, nameof(EntityMetadata.PrimaryNameAttribute), e.Entity.PrimaryNameAttribute);
            // The writer writes the table's notes and activities from these (plan 2.11 A19).
            SdkProperty.Set(copy, nameof(EntityMetadata.HasNotes), e.Entity.HasNotes);
            SdkProperty.Set(copy, nameof(EntityMetadata.HasActivities), e.Entity.HasActivities);
            copy.SetAttributes(InImageCreationOrder(e.Entity.LogicalName, Written(e).Where(a => a is not LookupAttributeMetadata).ToList()));
            return copy;
        }).ToList();

        /// <summary>
        /// <paramref name="attributes"/> with the image columns, among the places they take, in their creation order
        /// (<see cref="IMetadataStore.GetImageOrdinal"/>): an import creates them in the file's order, and that order decides
        /// which one becomes primary when the primary one is deleted.
        /// </summary>
        private List<AttributeMetadata> InImageCreationOrder(string entity, List<AttributeMetadata> attributes)
        {
            var images = new Queue<ImageAttributeMetadata>(attributes.OfType<ImageAttributeMetadata>()
                .OrderBy(i => store.GetImageOrdinal(entity, i.LogicalName) ?? int.MaxValue).ThenBy(i => i.LogicalName, StringComparer.Ordinal));
            return attributes.Select(a => a is ImageAttributeMetadata ? images.Dequeue() : a).ToList();
        }

        public List<LookupAttributeMetadata> Lookups() => _entities.Values.SelectMany(Written).OfType<LookupAttributeMetadata>().ToList();

        /// <summary>The attributes written, as copies carrying the Dataverse <c>IsCustomAttribute</c> (<see cref="MetadataStore.IsCustomAttribute"/>).</summary>
        private static IEnumerable<AttributeMetadata> Written((EntityMetadata Entity, bool All, HashSet<string> Attributes) e) =>
            Selected(e).Select(a =>
            {
                var copy = MetadataSerializer.Clone(a);
                SdkProperty.Set(copy, nameof(AttributeMetadata.IsCustomAttribute), MetadataStore.IsCustomAttribute(e.Entity, a));
                return copy;
            });

        private static IEnumerable<AttributeMetadata> Selected((EntityMetadata Entity, bool All, HashSet<string> Attributes) e)
        {
            var attributes = e.Entity.Attributes ?? [];
            if (e.All) return attributes;
            // A money attribute written brings its generated _base.
            var names = new HashSet<string>(e.Attributes, StringComparer.Ordinal);
            foreach (var money in attributes.OfType<MoneyAttributeMetadata>().Where(m => names.Contains(m.LogicalName) && m.IsBaseCurrency != true))
                names.Add(money.LogicalName + TypeMapping.BaseColumnSuffix);
            return attributes.Where(a => names.Contains(a.LogicalName));
        }

        public List<RelationshipMetadataBase> Relationships() => _relationships.Values.ToList();

        public List<OptionSetMetadataBase> OptionSets() => _optionSets;

        /// <summary>The kept menu configuration of each relationship written (T8: stored opaque with the relationship).</summary>
        public Dictionary<string, XElement> Menus()
        {
            var menus = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var relationship in _relationships.Values)
                if (store.GetRelationshipMenuXml(relationship.SchemaName) is { } xml) menus[relationship.SchemaName] = XElement.Parse(xml);
            return menus;
        }

        public bool IsWritten(string entity) => _entities.ContainsKey(entity);
    }

    // ---- rows (database) ----------------------------------------------------------------------------------------------

    private async Task<List<PackageRole>> RolesAsync(DataSession s, List<Component> components, CancellationToken ct)
    {
        var roles = new List<PackageRole>();
        foreach (var id in Of(components, ComponentType.Role).Distinct())
        {
            var name = await ScalarAsync(s, $"SELECT name FROM {s.DataTable("role")} WHERE roleid = @id", ct, id) as string
                       ?? throw new InvalidOperationException($"Component {id} of type 20 of the solution is not a role.");
            var privileges = new List<(string, DepthMask)>();
            await using var cmd = s.Command($"""
                SELECT p.name, rp.privilegedepthmask FROM {s.DataTable("roleprivileges")} AS rp
                JOIN {s.DataTable("privilege")} AS p ON p.privilegeid = rp.privilegeid
                WHERE rp.roleid = @id ORDER BY p.name
                """);
            cmd.AddParameter("@id", DbType.Guid, id);
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
            {
                while (await rd.ReadAsync(ct)) privileges.Add((rd.GetString(0), (DepthMask)rd.GetInt32(1)));
            }
            roles.Add(new PackageRole(id, name, privileges));
        }
        return roles;
    }

    /// <summary>
    /// The field security profiles the solution holds (type 70) with their permissions, by table and column; never their
    /// users or teams (ruling F8).
    /// </summary>
    private static async Task<List<PackageFieldSecurityProfile>> FieldSecurityProfilesAsync(DataSession s, List<Component> components,
        CancellationToken ct)
    {
        var profiles = new List<PackageFieldSecurityProfile>();
        // The built-in System Administrator profile is never packaged, even when a solution component row names it.
        foreach (var id in Of(components, ComponentType.FieldSecurityProfile).Distinct()
                     .Where(id => id != FieldSecurityTables.SystemAdministratorProfileId))
        {
            string name;
            string? description;
            await using (var cmd = s.Command(
                             $"SELECT name, description FROM {s.DataTable(FieldSecurityTables.Profile)} WHERE {s.Dialect.Quote(FieldSecurityTables.ProfileId)} = @id"))
            {
                cmd.AddParameter("@id", DbType.Guid, id);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct))
                    throw new InvalidOperationException($"Component {id} of type 70 of the solution is not a field security profile.");
                name = rd.IsDBNull(0) ? id.ToString("D") : rd.GetString(0);
                description = rd.IsDBNull(1) ? null : rd.GetString(1);
            }
            var permissions = new List<PackageFieldPermission>();
            await using (var cmd = s.Command($"""
                             SELECT entityname, attributelogicalname, canread, cancreate, canupdate, canreadunmasked
                             FROM {s.DataTable(FieldSecurityTables.Permission)} WHERE {s.Dialect.Quote(FieldSecurityTables.ProfileId)} = @id
                             ORDER BY entityname, attributelogicalname
                             """))
            {
                cmd.AddParameter("@id", DbType.Guid, id);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    if (rd.IsDBNull(0) || rd.IsDBNull(1)) continue;
                    int Value(int i) => rd.IsDBNull(i) ? 0 : Convert.ToInt32(rd.GetValue(i), CultureInfo.InvariantCulture);
                    permissions.Add(new PackageFieldPermission(rd.GetString(0), rd.GetString(1), Value(2), Value(3), Value(4), Value(5)));
                }
            }
            profiles.Add(new PackageFieldSecurityProfile(id, name, description, permissions));
        }
        return profiles;
    }

    /// <summary>
    /// The views, forms, site maps and app modules: the XML the import kept, with what their columns say now put in (the
    /// columns win: a row changed since the import is written as it is); without kept XML, the XML of the columns.
    /// </summary>
    /// <param name="keys">Filled with the unique name of each site map and app module written, by row id.</param>
    private async Task<List<OpaqueComponent>> OpaqueAsync(DataSession s, List<Component> components, Builder builder, int language,
        Dictionary<Guid, string> keys, CancellationToken ct)
    {
        var opaque = new List<OpaqueComponent>();

        // Views and forms: those the solution holds and those of the entities it holds with their subcomponents.
        var viewIds = Of(components, ComponentType.SavedQuery).ToHashSet();
        await using (var cmd = s.Command($"""
                         SELECT savedqueryid, name, returnedtypecode, fetchxml, layoutxml, querytype, isdefault, xrmverse_componentxml
                         FROM {s.DataTable("savedquery")}
                         """))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetGuid(0);
                var entity = Text(rd, 2);
                if (!viewIds.Contains(id) && (entity is null || !builder.Full.Contains(entity))) continue;
                if (entity is null) throw XrmdFaultException.InvalidArgument($"View {id} has no entity (returnedtypecode): it cannot be exported.");
                var xml = Kept(Text(rd, 7), "savedquery", $"view {id:D}") ?? new XElement("savedquery", new XElement("savedqueryid", id.ToString("B")));
                SetXml(xml, "layoutxml", Text(rd, 4));
                SetValue(xml, "querytype", rd.IsDBNull(5) ? null : rd.GetInt32(5).ToString(CultureInfo.InvariantCulture));
                SetXml(xml, "fetchxml", Text(rd, 3));
                SetValue(xml, "isdefault", rd.IsDBNull(6) ? null : rd.GetBoolean(6) ? "1" : "0");
                SetName(xml, Text(rd, 1), language);
                opaque.Add(new(ComponentType.SavedQuery, id.ToString("D"), entity, xml));
            }
        }

        var formIds = Of(components, ComponentType.SystemForm).ToHashSet();
        await using (var cmd = s.Command($"SELECT systemformid, name, objecttypecode, formxml, type, xrmverse_componentxml, description FROM {s.DataTable("systemform")}"))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetGuid(0);
                var entity = Text(rd, 2);
                if (!formIds.Contains(id) && (entity is null || !builder.Full.Contains(entity))) continue;
                if (entity is null) throw XrmdFaultException.InvalidArgument($"Form {id} has no entity (objecttypecode): it cannot be exported.");
                var type = rd.IsDBNull(4) ? 2 : rd.GetInt32(4);
                if (!FormTypes.TryGetValue(type, out var formType))
                    throw XrmdFaultException.InvalidArgument($"Form {id} has the type {type}, which xrmverse cannot write in a solution file.");
                if (entity == ComponentType.DashboardObjectTypeCode)
                {
                    opaque.Add(new(ComponentType.SystemForm, id.ToString("D"), null, Dashboard(rd, id, formType, language), formType));
                    continue;
                }
                var xml = Kept(Text(rd, 5), "systemform", $"form {id:D}") ?? new XElement("systemform", new XElement("formid", id.ToString("B")));
                if (Text(rd, 3) is { } formXml)
                {
                    var form = Parse(formXml, $"the formxml of form {id}");
                    if (xml.Element("form") is { } current) current.ReplaceWith(form);
                    else xml.Add(form);
                }
                SetName(xml, Text(rd, 1), language);
                opaque.Add(new(ComponentType.SystemForm, id.ToString("D"), entity, xml, formType));
            }
        }

        // The system charts (plan 3.5 UI-1f, ruling F2), as the views: those the solution holds and those of the entities it holds.
        var chartIds = Of(components, ComponentType.SavedQueryVisualization).ToHashSet();
        await using (var cmd = s.Command($"""
                         SELECT savedqueryvisualizationid, name, primaryentitytypecode, datadescription, presentationdescription, isdefault,
                             description, charttype, type, webresourceid, introducedversion, xrmverse_componentxml
                         FROM {s.DataTable(ChartSchema.Table)}
                         """))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetGuid(0);
                var entity = Text(rd, 2);
                if (!chartIds.Contains(id) && (entity is null || !builder.Full.Contains(entity))) continue;
                if (entity is null) throw XrmdFaultException.InvalidArgument($"Chart {id} has no entity (primaryentitytypecode): it cannot be exported.");
                var xml = Kept(Text(rd, 11), "visualization", $"chart {id:D}") ?? new XElement("visualization", new XElement(ChartSchema.Id, id.ToString("B")));
                SetXml(xml, ChartSchema.DataDescription, Text(rd, 3));
                SetXml(xml, ChartSchema.PresentationDescription, Text(rd, 4));
                SetValue(xml, ChartSchema.IsDefault, rd.IsDBNull(5) ? null : rd.GetBoolean(5) ? "1" : "0");
                SetName(xml, Text(rd, 1), language);
                SetLabel(xml, "Descriptions", "Description", Text(rd, 6), language);
                // The choices and the web resource are written where the file had them, or when they differ from Learn's defaults.
                foreach (var (column, ordinal) in new[] { (ChartSchema.ChartType, 7), (ChartSchema.Type, 8) })
                    if (!rd.IsDBNull(ordinal) && (xml.Element(column) is not null || rd.GetInt32(ordinal) != 0))
                        SetValue(xml, column, rd.GetInt32(ordinal).ToString(CultureInfo.InvariantCulture));
                // An id is written as it came (braces and case): the kept value stays when it names the same web resource.
                if (!rd.IsDBNull(9) && !(Guid.TryParse((string?)xml.Element(ChartSchema.WebResourceId), out var keptResource)
                                          && keptResource == rd.GetGuid(9)))
                    SetValue(xml, ChartSchema.WebResourceId, rd.GetGuid(9).ToString("B"));
                else if (rd.IsDBNull(9)) xml.Element(ChartSchema.WebResourceId)?.Remove(); // cleared since the import
                SetValue(xml, "IntroducedVersion", Text(rd, 10));
                opaque.Add(new(ComponentType.SavedQueryVisualization, id.ToString("D"), entity, xml));
            }
        }

        foreach (var id in Of(components, ComponentType.SiteMap))
        {
            await using var cmd = s.Command(
                $"SELECT sitemapnameunique, sitemapname, sitemapxml, xrmverse_componentxml FROM {s.DataTable("sitemap")} WHERE sitemapid = @id");
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw new InvalidOperationException($"Component {id} of type 62 of the solution is not a site map.");
            var uniqueName = Text(rd, 0) ?? throw XrmdFaultException.InvalidArgument($"Site map {id} has no unique name: it cannot be exported.");
            var xml = Kept(Text(rd, 3), "AppModuleSiteMap", $"site map '{uniqueName}' ({id:D})") ?? new XElement("AppModuleSiteMap");
            SetValue(xml, "SiteMapUniqueName", uniqueName);
            if (Text(rd, 2) is { } siteMapXml)
            {
                var siteMap = Parse(siteMapXml, $"the sitemapxml of site map '{uniqueName}'");
                if (xml.Element("SiteMap") is { } current) current.ReplaceWith(siteMap);
                else xml.Element("SiteMapUniqueName")!.AddAfterSelf(siteMap);
            }
            SetName(xml, Text(rd, 1), language);
            opaque.Add(new(ComponentType.SiteMap, uniqueName, null, xml));
            keys[id] = uniqueName;
        }

        foreach (var id in Of(components, ComponentType.AppModule))
        {
            await using var cmd = s.Command($"SELECT uniquename, name, descriptor FROM {s.DataTable("appmodule")} WHERE appmoduleid = @id");
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw new InvalidOperationException($"Component {id} of type 80 of the solution is not an app module.");
            var uniqueName = Text(rd, 0) ?? throw XrmdFaultException.InvalidArgument($"App module {id} has no unique name: it cannot be exported.");
            // The descriptor of an imported app module is its solution XML; any other (an app made in xrmverse) cannot be written.
            var xml = AppModuleXml(Text(rd, 2))
                      ?? throw XrmdFaultException.InvalidArgument(
                          $"App module '{uniqueName}' has no solution XML (descriptor): xrmverse can export only the app modules it imported.");
            SetValue(xml, "UniqueName", uniqueName);
            SetName(xml, Text(rd, 1), language);
            opaque.Add(new(ComponentType.AppModule, uniqueName, null, xml));
            keys[id] = uniqueName;
        }
        return opaque;
    }

    private async Task<List<PackageWebResource>> WebResourcesAsync(DataSession s, List<Component> components,
        Dictionary<(int Type, string Key), XElement> unmodelled, CancellationToken ct)
    {
        var resources = new List<PackageWebResource>();
        foreach (var id in Of(components, ComponentType.WebResource).Distinct())
        {
            await using var cmd = s.Command(
                $"SELECT name, displayname, webresourcetype, content, description, xrmverse_componentxml FROM {s.DataTable("webresource")} WHERE webresourceid = @id");
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw new InvalidOperationException($"Component {id} of type 61 of the solution is not a web resource.");
            var name = rd.GetString(0);
            resources.Add(new PackageWebResource(id, name, Text(rd, 1),
                rd.IsDBNull(2) ? throw XrmdFaultException.InvalidArgument($"Web resource '{name}' has no type: it cannot be exported.") : rd.GetInt32(2),
                Base64(Text(rd, 3), $"web resource '{name}'"), Text(rd, 4)));
            if (Kept(Text(rd, 5), "WebResource", $"web resource '{name}'") is { } xml) unmodelled[UnmodelledXml.WebResourceKey(id)] = xml;
        }
        return resources;
    }

    /// <summary>The assemblies the solution holds and those of the plug-in types it holds, each with all its types.</summary>
    private async Task<(List<PackageAssembly> Assemblies, Dictionary<Guid, Guid> TypeAssemblies)> AssembliesAsync(DataSession s,
        List<Component> components, Dictionary<(int Type, string Key), XElement> unmodelled, CancellationToken ct)
    {
        var ids = Of(components, ComponentType.PluginAssembly).ToHashSet();
        var typeAssemblies = new Dictionary<Guid, Guid>(); // plug-in type held -> its assembly
        foreach (var typeId in Of(components, ComponentType.PluginType))
        {
            var assemblyId = await ScalarAsync(s, $"SELECT pluginassemblyid FROM {s.DataTable("plugintype")} WHERE plugintypeid = @id", ct, typeId) as Guid?
                             ?? throw new InvalidOperationException($"Component {typeId} of type 90 of the solution is not a plug-in type.");
            typeAssemblies[typeId] = assemblyId;
        }
        ids.UnionWith(typeAssemblies.Values);

        var assemblies = new List<PackageAssembly>();
        foreach (var id in ids)
        {
            string name;
            string fullName;
            int isolation, source;
            byte[] content;
            await using (var cmd = s.Command($"""
                             SELECT name, version, culture, publickeytoken, isolationmode, sourcetype, content, xrmverse_componentxml
                             FROM {s.DataTable("pluginassembly")} WHERE pluginassemblyid = @id
                             """))
            {
                cmd.AddParameter("@id", DbType.Guid, id);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) throw new InvalidOperationException($"Component {id} of type 91 of the solution is not a plug-in assembly.");
                name = rd.GetString(0);
                fullName = $"{name}, Version={Text(rd, 1) ?? "0.0.0.0"}, Culture={Text(rd, 2) ?? "neutral"}, PublicKeyToken={Text(rd, 3) ?? "null"}";
                isolation = rd.IsDBNull(4) ? 2 : rd.GetInt32(4);
                source = rd.IsDBNull(5) ? 0 : rd.GetInt32(5);
                if (source != 0)
                    throw XrmdFaultException.InvalidArgument($"Plug-in assembly '{name}' is not stored in the database (sourcetype {source}): it cannot be exported.");
                content = Base64(Text(rd, 6), $"plug-in assembly '{name}'");
                if (Kept(Text(rd, 7), "PluginAssembly", $"plug-in assembly '{name}'") is { } xml) unmodelled[UnmodelledXml.AssemblyKey(id)] = xml;
            }
            var types = new List<PackagePluginType>();
            await using (var cmd = s.Command(
                $"SELECT plugintypeid, typename, friendlyname, workflowactivitygroupname FROM {s.DataTable("plugintype")} WHERE pluginassemblyid = @id"))
            {
                cmd.AddParameter("@id", DbType.Guid, id);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var typeName = rd.GetString(1);
                    types.Add(new PackagePluginType(rd.GetGuid(0), typeName, $"{typeName}, {fullName}", Text(rd, 2), Text(rd, 3)));
                }
            }
            assemblies.Add(new PackageAssembly(id, name, fullName, isolation, source, content, types));
        }
        return (assemblies, typeAssemblies);
    }

    /// <summary>
    /// The steps the solution holds and those of the images it holds, each with all its images. A step is written with the
    /// Dataverse id of its message (<see cref="DataverseSdkMessages"/>): the catalog's ids are xrmverse's own, never written;
    /// a custom message (an <c>sdkmessage</c> row xrmverse did not derive) keeps its id.
    /// </summary>
    private async Task<List<PackageStep>> StepsAsync(DataSession s, List<Component> components, List<PackageAssembly> assemblies,
        Dictionary<(int Type, string Key), XElement> unmodelled, CancellationToken ct)
    {
        var ids = Of(components, ComponentType.SdkMessageProcessingStep).ToHashSet();
        foreach (var imageId in Of(components, ComponentType.SdkMessageProcessingStepImage))
            ids.Add(await ScalarAsync(s, $"SELECT sdkmessageprocessingstepid FROM {s.DataTable("sdkmessageprocessingstepimage")} WHERE sdkmessageprocessingstepimageid = @id",
                        ct, imageId) as Guid?
                    ?? throw new InvalidOperationException($"Component {imageId} of type 93 of the solution is not a step image."));

        var steps = new List<PackageStep>();
        foreach (var id in ids)
        {
            PackageStep step;
            await using (var cmd = s.Command($"""
                             SELECT st.name, st.description, st.sdkmessageid, m.name, st.sdkmessagefilterid, f.primaryobjecttypecode,
                                    st.plugintypeid, t.typename, a.name, a.version, a.culture, a.publickeytoken,
                                    st.stage, st.mode, st.rank, st.invocationsource, st.supporteddeployment, st.asyncautodelete,
                                    st.filteringattributes, st.configuration, st.impersonatinguserid, st.statecode, st.xrmverse_componentxml,
                                    st.eventhandlertype
                             FROM {s.DataTable("sdkmessageprocessingstep")} AS st
                             LEFT JOIN {s.DataTable("sdkmessage")} AS m ON m.sdkmessageid = st.sdkmessageid
                             LEFT JOIN {s.DataTable("sdkmessagefilter")} AS f ON f.sdkmessagefilterid = st.sdkmessagefilterid
                             LEFT JOIN {s.DataTable("plugintype")} AS t ON t.plugintypeid = st.plugintypeid
                             LEFT JOIN {s.DataTable("pluginassembly")} AS a ON a.pluginassemblyid = t.pluginassemblyid
                             WHERE st.sdkmessageprocessingstepid = @id
                             """))
            {
                cmd.AddParameter("@id", DbType.Guid, id);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) throw new InvalidOperationException($"Component {id} of type 92 of the solution is not a step.");
                var name = Text(rd, 0) ?? id.ToString("D");
                // Plan 3.2: a step on a webhook never reaches a solution; one that did (written outside the rules) is not exported.
                if (Int(rd, 23) == ServiceEndpointSchema.ObjectTypeCode) throw XrmdFaultException.InvalidArgument(ServiceEndpointSchema.NotInSolution);
                var messageName = Text(rd, 3) ?? throw XrmdFaultException.InvalidArgument($"Step '{name}' has no message: it cannot be exported.");
                var messageId = DataverseMessageId(name, rd.GetGuid(2), messageName);
                if (rd.IsDBNull(6) || Text(rd, 7) is not { } typeName)
                    throw XrmdFaultException.InvalidArgument($"Step '{name}' has no plug-in type: it cannot be exported.");
                var assembly = assemblies.SelectMany(a => a.Types.Select(t => (Type: t, Assembly: a))).FirstOrDefault(p => p.Type.Id == rd.GetGuid(6));
                var qualifiedName = assembly.Type?.AssemblyQualifiedName
                                    ?? $"{typeName}, {Text(rd, 8)}, Version={Text(rd, 9) ?? "0.0.0.0"}, Culture={Text(rd, 10) ?? "neutral"}, "
                                    + $"PublicKeyToken={Text(rd, 11) ?? "null"}";
                step = new PackageStep(id, name, Text(rd, 1), messageId, rd.IsDBNull(4) ? null : rd.GetGuid(4), Text(rd, 5),
                    rd.GetGuid(6), qualifiedName, Int(rd, 12) ?? 40, Int(rd, 13) ?? 0, Int(rd, 14) ?? 1, Int(rd, 15) ?? 0, Int(rd, 16) ?? 0,
                    !rd.IsDBNull(17) && rd.GetBoolean(17), Text(rd, 18), Text(rd, 19), rd.IsDBNull(20) ? null : rd.GetGuid(20),
                    Int(rd, 21) == 1, []);
                if (Kept(Text(rd, 22), "SdkMessageProcessingStep", $"step '{name}'") is { } xml) unmodelled[UnmodelledXml.StepKey(id)] = xml;
            }
            var images = new List<PackageStepImage>();
            await using (var cmd = s.Command($"""
                             SELECT sdkmessageprocessingstepimageid, name, entityalias, imagetype, messagepropertyname, attributes
                             FROM {s.DataTable("sdkmessageprocessingstepimage")} WHERE sdkmessageprocessingstepid = @id
                             """))
            {
                cmd.AddParameter("@id", DbType.Guid, id);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var alias = Text(rd, 2) ?? throw XrmdFaultException.InvalidArgument($"An image of step '{step.Name}' has no entity alias.");
                    images.Add(new PackageStepImage(rd.GetGuid(0), Text(rd, 1) ?? alias, alias, Int(rd, 3) ?? 0, Text(rd, 4) ?? "Target", Text(rd, 5)));
                }
            }
            steps.Add(step with { Images = images });
        }
        return steps;
    }

    /// <summary>
    /// The custom APIs the solution holds (plan 15, C11), each with its request parameters and response properties, by unique
    /// name: the modelled columns from the rows, the plug-in type as <c>plugintypeexportkey</c> (its <c>plugintypeid</c>: xrmverse
    /// has no separate export key), the base-language labels from <c>displayname</c> and <c>description</c>, and what the
    /// import kept (<c>xrmverse_componentxml</c>: <c>iscustomizable</c>, every label) for the writer to merge.
    /// </summary>
    private static async Task<List<PackageCustomApi>> CustomApisAsync(DataSession s, List<Component> components, int language,
        CancellationToken ct)
    {
        var ids = Of(components, ComponentType.CustomApi).ToHashSet();
        var apis = new List<PackageCustomApi>();
        if (ids.Count == 0) return apis;
        Label Labeled(string? text)
        {
            if (string.IsNullOrEmpty(text)) return new Label();
            var label = new LocalizedLabel(text, language);
            return new Label(label, new[] { label });
        }

        foreach (var id in ids)
        {
            await using var cmd = s.Command($"""
                SELECT uniquename, name, bindingtype, boundentitylogicalname, allowedcustomprocessingsteptype, isfunction, isprivate,
                       workflowsdkstepenabled, executeprivilegename, plugintypeid, displayname, description, xrmverse_componentxml
                FROM {s.DataTable(CustomApiTables.Api)} WHERE customapiid = @id;
                SELECT uniquename, name, type, logicalentityname, isoptional, displayname, description, xrmverse_componentxml
                FROM {s.DataTable(CustomApiTables.RequestParameter)} WHERE customapiid = @id AND uniquename IS NOT NULL;
                SELECT uniquename, name, type, logicalentityname, CAST(NULL AS {s.Dialect.ColumnType(SqlColumnKind.Boolean)}), displayname, description, xrmverse_componentxml
                FROM {s.DataTable(CustomApiTables.ResponseProperty)} WHERE customapiid = @id AND uniquename IS NOT NULL;
                """);
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw new InvalidOperationException($"Component {id} of type {ComponentType.CustomApi} of the solution is not a custom API.");
            var uniqueName = Text(rd, 0) ?? throw XrmdFaultException.InvalidArgument($"The custom API {id} has no unique name: it cannot be exported.");
            var what = $"custom API '{uniqueName}'";
            var api = new PackageCustomApi(uniqueName, Text(rd, 1) ?? uniqueName, Int(rd, 2) ?? 0, Text(rd, 3), Int(rd, 4) ?? 0,
                !rd.IsDBNull(5) && rd.GetBoolean(5), !rd.IsDBNull(6) && rd.GetBoolean(6), !rd.IsDBNull(7) && rd.GetBoolean(7), Text(rd, 8),
                rd.IsDBNull(9) ? null : rd.GetGuid(9), Labeled(Text(rd, 10)), Labeled(Text(rd, 11)), [], [], Kept(Text(rd, 12), "customapi", what));

            async Task<List<PackageCustomApiField>> FieldsAsync(bool request)
            {
                await rd.NextResultAsync(ct);
                var fields = new List<PackageCustomApiField>();
                var root = request ? "customapirequestparameter" : "customapiresponseproperty";
                while (await rd.ReadAsync(ct))
                {
                    var name = rd.GetString(0);
                    fields.Add(new PackageCustomApiField(name, Text(rd, 1) ?? name, Int(rd, 2) ?? 0, Text(rd, 3),
                        request ? !rd.IsDBNull(4) && rd.GetBoolean(4) : null, Labeled(Text(rd, 5)), Labeled(Text(rd, 6)),
                        Kept(Text(rd, 7), root, $"{(request ? "request parameter" : "response property")} '{name}' of {what}")));
                }
                return fields.OrderBy(f => f.UniqueName, StringComparer.Ordinal).ToList();
            }

            var parameters = await FieldsAsync(request: true);
            var properties = await FieldsAsync(request: false);
            apis.Add(api with { RequestParameters = parameters, ResponseProperties = properties });
        }
        return apis.OrderBy(a => a.UniqueName, StringComparer.Ordinal).ToList();
    }

    // ---- custom controls (plan 3.5 UI-1d, ruling R4) ------------------------------------------------------------------

    /// <summary>
    /// The custom controls the solution holds (component type 66), by name, each with its id: the row's columns, its kept
    /// <c>CustomControl</c> element (<c>xrmverse_componentxml</c>; a new one naming <c>Controls/{name}/ControlManifest.xml</c>
    /// otherwise) and its files from <c>meta.customcontrol_file</c>, byte for byte. A control without a stored manifest file gets
    /// its <c>manifest</c> column at the manifest path, as UTF-8.
    /// </summary>
    private async Task<List<(Guid Id, PackageCustomControl Control)>> CustomControlsAsync(DataSession s, List<Component> components,
        CancellationToken ct)
    {
        var controls = new List<(Guid, PackageCustomControl)>();
        foreach (var id in Of(components, ComponentType.CustomControl).Distinct())
        {
            string name;
            string? manifest, version, compatible, kept;
            await using (var cmd = s.Command($"""
                             SELECT {CustomControlSchema.Name}, {CustomControlSchema.Manifest}, {CustomControlSchema.Version},
                                    {CustomControlSchema.CompatibleDataTypes}, {SystemTableRules.ComponentXml}
                             FROM {s.DataTable(CustomControlSchema.Table)} WHERE {CustomControlSchema.Table}id = @id
                             """))
            {
                cmd.AddParameter("@id", DbType.Guid, id);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct))
                    throw new InvalidOperationException($"Component {id} of type {ComponentType.CustomControl} of the solution is not a custom control.");
                name = Text(rd, 0) ?? throw XrmdFaultException.InvalidArgument($"The custom control {id} has no name: it cannot be exported.");
                (manifest, version, compatible, kept) = (Text(rd, 1), Text(rd, 2), Text(rd, 3), Text(rd, 4));
            }
            var source = Kept(kept, "CustomControl", $"custom control '{name}'")
                         ?? new XElement("CustomControl", new XElement("Name", name), new XElement("FileName", $"/Controls/{name}/ControlManifest.xml"));
            var manifestPath = (((string?)source.Element("FileName"))?.Trim() is { Length: > 0 } fileName ? fileName : $"/Controls/{name}/ControlManifest.xml")
                .Replace('\\', '/').TrimStart('/');
            var files = new Dictionary<string, byte[]>(await controlFiles.AllAsync(s, id, ct), StringComparer.Ordinal);
            var manifestFile = manifestPath[(manifestPath.LastIndexOf('/') + 1)..];
            if (!files.ContainsKey(manifestFile) && manifest is not null) files[manifestFile] = System.Text.Encoding.UTF8.GetBytes(manifest);
            controls.Add((id, new PackageCustomControl(name, manifestPath, manifest ?? "", version ?? "", compatible ?? "", files, source)));
        }
        return controls.OrderBy(c => c.Item2.Name, StringComparer.Ordinal).ToList();
    }

    // ---- environment variables (#167, ruling E4) ----------------------------------------------------------------------

    /// <summary>The children an export writes only when they have a value, in the alphabetical order of a Dataverse export.</summary>
    private static readonly string[] OptionalVariableChildren =
    [
        EnvironmentVariableSchema.DefaultValue, EnvironmentVariableSchema.Hint, EnvironmentVariableSchema.IntroducedVersion,
        EnvironmentVariableSchema.ValueSchema,
    ];

    /// <summary>
    /// The environment variable definitions the solution holds (component type 380), by schema name, each with its id: the modelled
    /// columns from the row, its value row (when it has one) as the value file, and what the import kept
    /// (<c>xrmverse_componentxml</c>: <c>iscustomizable</c>, the label sets, any other child) for the writer to merge; a definition
    /// without kept XML gets the template of an export. The label sets are brought in line with the row's <c>displayname</c> and
    /// <c>description</c> in the base language. The value file's <c>iscustomizable</c> is "1" (an unmanaged export).
    /// </summary>
    private static async Task<List<(Guid Id, PackageEnvironmentVariable Variable)>> EnvironmentVariablesAsync(DataSession s,
        List<Component> components, int language, CancellationToken ct)
    {
        var variables = new List<(Guid, PackageEnvironmentVariable)>();
        // The first value of the definition (OUTER APPLY on SQL Server, LEFT JOIN LATERAL on PostgreSQL).
        var firstValue = s.Dialect.Apply("(" + s.Dialect.Limit(
            $"SELECT x.{EnvironmentVariableSchema.Value}id, x.{EnvironmentVariableSchema.ValueColumn}\n" +
            $"             FROM {s.DataTable(EnvironmentVariableSchema.Value)} AS x\n" +
            $"             WHERE x.{EnvironmentVariableSchema.DefinitionLookup} = d.{EnvironmentVariableSchema.Definition}id\n" +
            "             ORDER BY x.createdon", "1") + ")", "v", outer: true);
        foreach (var id in Of(components, ComponentType.EnvironmentVariableDefinition).Distinct())
        {
            await using var cmd = s.Command($"""
                SELECT d.{EnvironmentVariableSchema.SchemaName}, d.{EnvironmentVariableSchema.Type}, d.{EnvironmentVariableSchema.DefaultValue},
                       d.{EnvironmentVariableSchema.IsRequired}, d.{EnvironmentVariableSchema.SecretStore}, d.{EnvironmentVariableSchema.ValueSchema},
                       d.{EnvironmentVariableSchema.IntroducedVersion}, d.{EnvironmentVariableSchema.Hint}, d.{EnvironmentVariableSchema.DisplayName},
                       d.{EnvironmentVariableSchema.Description}, d.{SystemTableRules.ComponentXml}, v.{EnvironmentVariableSchema.ValueColumn},
                       CAST(CASE WHEN v.{EnvironmentVariableSchema.Value}id IS NULL THEN {s.Dialect.BooleanLiteral(false)} ELSE {s.Dialect.BooleanLiteral(true)} END AS {s.Dialect.ColumnType(SqlColumnKind.Boolean)})
                FROM {s.DataTable(EnvironmentVariableSchema.Definition)} AS d
                {firstValue}
                WHERE d.{EnvironmentVariableSchema.Definition}id = @id
                """);
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct))
                throw new InvalidOperationException($"Component {id} of type {ComponentType.EnvironmentVariableDefinition} of the solution is not an environment variable definition.");
            var name = Text(rd, 0) ?? throw XrmdFaultException.InvalidArgument($"The environment variable definition {id} has no schema name: it cannot be exported.");
            var source = Kept(Text(rd, 10), "environmentvariabledefinition", $"environment variable '{name}'") ?? EnvironmentVariableReader.NewSource();
            var (defaultValue, valueSchema, introducedVersion, hint) = (Text(rd, 2), Text(rd, 5), Text(rd, 6), Text(rd, 7));
            foreach (var (child, value) in OptionalVariableChildren.Zip([defaultValue, hint, introducedVersion, valueSchema]))
                if (value is not null && source.Element(child) is null) source = WithMarkerInOrder(source, child);
            RefreshLabels(source, EnvironmentVariableSchema.DisplayName, Text(rd, 8), language);
            RefreshLabels(source, EnvironmentVariableSchema.Description, Text(rd, 9), language);
            var hasValue = rd.GetBoolean(12);
            variables.Add((id, new PackageEnvironmentVariable(name, Int(rd, 1) ?? (int)VariableType.String, defaultValue,
                !rd.IsDBNull(3) && rd.GetBoolean(3), Int(rd, 4) ?? 0, valueSchema, introducedVersion, hint, Text(rd, 11), hasValue, source)
            {
                ValueIsCustomizable = hasValue ? "1" : null,
            }));
        }
        return variables.OrderBy(v => v.Item2.SchemaName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// <paramref name="source"/> with a marker of the modelled child <paramref name="name"/> it lacks, before its first child whose
    /// name comes after (ordinal), else last: so a value the file did not have is written in the alphabetical place of an export.
    /// </summary>
    private static XElement WithMarkerInOrder(XElement source, string name)
    {
        var copy = UnmodelledXml.WithMarkerAtEnd(source, name);
        var marker = copy.Elements().Last();
        marker.Remove();
        InsertInOrder(copy, marker);
        return copy;
    }

    private static void InsertInOrder(XElement parent, XElement child)
    {
        if (parent.Elements().FirstOrDefault(e => string.CompareOrdinal(e.Name.LocalName, child.Name.LocalName) > 0) is { } next)
            next.AddBeforeSelf(child);
        else parent.Add(child);
    }

    /// <summary>
    /// Brings the label set <paramref name="element"/> (<c>displayname</c> or <c>description</c>) of a definition's kept XML in line
    /// with the row's <paramref name="text"/> in the base <paramref name="language"/>: nothing when they agree; removed when the row
    /// has none; else its <c>default</c> and its base-language <c>label</c> set to the text (the label added in language order, the
    /// element added in alphabetical order when missing). The labels of the other languages are kept.
    /// </summary>
    private static void RefreshLabels(XElement source, string element, string? text, int language)
    {
        var current = source.Element(element) is { } found && !UnmodelledXml.IsMarker(found) ? found : null;
        var currentText = current is null ? null : CustomApiReader.Labels(current, language).UserLocalizedLabel?.Label;
        if (string.Equals(currentText ?? "", text ?? "", StringComparison.Ordinal)) return;
        if (string.IsNullOrEmpty(text))
        {
            current?.Remove();
            return;
        }
        if (current is null) InsertInOrder(source, current = new XElement(element));
        current.SetAttributeValue("default", text);
        var code = language.ToString(CultureInfo.InvariantCulture);
        if (current.Elements("label").FirstOrDefault(l => (string?)l.Attribute("languagecode") == code) is { } label)
        {
            label.SetAttributeValue("description", text);
            return;
        }
        label = new XElement("label", new XAttribute("description", text), new XAttribute("languagecode", code));
        if (current.Elements("label").FirstOrDefault(l => int.TryParse((string?)l.Attribute("languagecode"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var other) && other > language) is { } after)
            after.AddBeforeSelf(label);
        else current.Add(label);
    }

    // ---- processes (plan 2.14, ruling W22) ----------------------------------------------------------------------------

    /// <summary>The <c>Workflow</c> element names an export writes for the columns of a process (looked up ignoring case).</summary>
    private static readonly (string Element, string Column)[] WorkflowColumns =
    [
        ("Type", "type"), ("Subprocess", "subprocess"), ("OnDemand", "ondemand"), ("TriggerOnCreate", "triggeroncreate"),
        ("TriggerOnDelete", "triggerondelete"), ("TriggerOnUpdateAttributeList", "triggeronupdateattributelist"),
        ("AsyncAutodelete", "asyncautodelete"), ("SyncWorkflowLogOnFailure", "syncworkflowlogonfailure"), ("StatusCode", "statuscode"),
        ("CreateStage", "createstage"), ("UpdateStage", "updatestage"), ("DeleteStage", "deletestage"), ("RunAs", "runas"),
        ("Rank", "rank"), ("IsTransacted", "istransacted"), ("IntroducedVersion", "introducedversion"), ("IsCustomizable", "iscustomizable"),
        ("UniqueName", "uniquename"), ("InputParameters", "inputparameters"), ("ProcessTriggerScope", "processtriggerscope"),
    ];

    /// <summary>
    /// The processes the solution holds (component type 29): each row with its XAML and its triggers, written in the element the
    /// import kept (<c>xrmverse_componentxml</c>) with the row's values set in it, so an imported process is exported as it was
    /// imported (ruling W22); a process without kept XML gets the element of an export (from memory, checklist 1h).
    /// </summary>
    private static async Task<List<PackageWorkflow>> WorkflowsAsync(DataSession s, List<Component> components, int language, CancellationToken ct)
    {
        var workflows = new List<PackageWorkflow>();
        foreach (var id in Of(components, ComponentType.Workflow).Distinct())
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            string name, xaml;
            string? description, primaryEntity, componentXml;
            int category, mode, scope, state;
            object type;
            Guid? messageId;
            await using (var cmd = s.Command($"""
                             SELECT name, category, mode, scope, primaryentity, xaml, statecode, description, {SystemTableRules.ComponentXml},
                                    {string.Join(", ", WorkflowColumns.Select(c => c.Column))}, {WorkflowSchema.SdkMessageId}
                             FROM {s.DataTable(WorkflowSchema.Table)} WHERE workflowid = @id
                             """))
            {
                cmd.AddParameter("@id", DbType.Guid, id);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) throw new InvalidOperationException($"Component {id} of type 29 of the solution is not a process.");
                name = Text(rd, 0) ?? id.ToString("D");
                (category, mode, scope) = (Int(rd, 1) ?? 0, Int(rd, 2) ?? 0, Int(rd, 3) ?? 1);
                primaryEntity = Text(rd, 4);
                xaml = Text(rd, 5) ?? throw XrmdFaultException.InvalidArgument($"The process '{name}' has no XAML: it cannot be exported.");
                state = Int(rd, 6) ?? WorkflowSchema.DraftState;
                description = Text(rd, 7);
                componentXml = Text(rd, 8);
                type = rd.GetValue(9); // WorkflowColumns[0], "type"
                for (var i = 0; i < WorkflowColumns.Length; i++)
                    values[WorkflowColumns[i].Element] = rd.GetValue(9 + i) switch
                    {
                        DBNull => null,
                        bool flag => flag ? "1" : "0",
                        int number => number.ToString(CultureInfo.InvariantCulture),
                        var other => Convert.ToString(other, CultureInfo.InvariantCulture),
                    };
                messageId = rd.GetValue(9 + WorkflowColumns.Length) as Guid?;
            }

            // Only a definition is a component (controller ruling): an activation row is never written.
            if (!ComponentType.IsWorkflowDefinition(type)) continue;
            var kept = Kept(componentXml, "Workflow", $"process '{name}'");
            if (kept is not null) RefreshStaleLabels(kept, name, description, language, xaml);
            var source = kept ?? WorkflowElement(id, name, description, category, mode, scope, primaryEntity, state, values, messageId, language, xaml);
            var xamlPath = CustomizationsReader.Value(source, "XamlFileName") ?? WorkflowXamlPath(id, name);
            // The leaf elements of the source, each with the row's value when it holds one (an empty text for a null).
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in source.Elements().Where(e => !e.HasElements))
            {
                var element = child.Name.LocalName;
                if (element is "XamlFileName" or "Category" or "Mode" or "Scope" or "PrimaryEntity" or "StateCode" or "ProcessTriggers") continue;
                fields.TryAdd(element, values.TryGetValue(element, out var value) ? value ?? "" : CustomizationsReader.IsNil(child) ? "" : child.Value);
            }
            workflows.Add(new PackageWorkflow(id, name, category, mode, scope, primaryEntity, xamlPath, xaml, state, fields,
                await ProcessTriggersAsync(s, id, source, ct), source));
        }
        return workflows;
    }

    /// <summary>The <c>XamlFileName</c> of a process without kept XML: <c>/Workflows/{name without spaces}-{ID}.xaml</c>.</summary>
    private static string WorkflowXamlPath(Guid id, string name) =>
        $"/Workflows/{new string(name.Where(char.IsLetterOrDigit).ToArray())}-{id.ToString("D").ToUpperInvariant()}.xaml";

    /// <summary>
    /// The <c>Workflow</c> element of a process without kept XML, e.g. one compiled from YAML (plan 2.21, A15), in the element order
    /// and with the constant elements of the real export <c>xrmdWfTest_1_0_0_2.zip</c> (plan 2.21, Task 8): <c>SdkMessageId</c> and
    /// <c>UniqueName</c> of an action before <c>IsTransacted</c>, <c>TriggerOnUpdateAttributeList</c> before <c>TriggerOnCreate</c>;
    /// <c>BusinessProcessType</c> 0, <c>IsCustomProcessingStepAllowedForOtherPublishers</c> 1 and <c>ModernFlowType</c> 0 for every
    /// process of category 0, 2 and 3, and <c>FormId</c> the empty id for an action; after its <c>LocalizedNames</c>, its <c>Descriptions</c> when it has a description
    /// and the <c>labels</c> of the <c>mcwo:StepLabel</c> elements of its XAML, each label with <c>languagecode</c> before
    /// <c>description</c>, as that export writes a business rule. <c>Rank</c> is written only when it is not 0 (the export's
    /// processes, of rank 0, have none); its place and that of <c>InputParameters</c>, which the export does not show, are from memory
    /// (checklist 1h).
    /// </summary>
    private static XElement WorkflowElement(Guid id, string name, string? description, int category, int mode, int scope, string? primaryEntity,
        int state, IReadOnlyDictionary<string, string?> values, Guid? messageId, int language, string xaml)
    {
        XElement? Optional(string element) => values.GetValueOrDefault(element) is { } value ? new XElement(element, value) : null;
        XElement Required(string element, string fallback) => new(element, values.GetValueOrDefault(element) ?? fallback);
        return new XElement("Workflow",
            new XAttribute("WorkflowId", id.ToString("B")),
            new XAttribute("Name", name),
            description is null ? null : new XAttribute("Description", description),
            new XElement("XamlFileName", WorkflowXamlPath(id, name)),
            Required("Type", "1"),
            Required("Subprocess", "0"),
            new XElement("Category", category.ToString(CultureInfo.InvariantCulture)),
            new XElement("Mode", mode.ToString(CultureInfo.InvariantCulture)),
            new XElement("Scope", scope.ToString(CultureInfo.InvariantCulture)),
            Required("OnDemand", "0"),
            Optional("TriggerOnUpdateAttributeList"),
            Required("TriggerOnCreate", "0"),
            Required("TriggerOnDelete", "0"),
            Required("AsyncAutodelete", "0"),
            Required("SyncWorkflowLogOnFailure", "0"),
            new XElement("StateCode", state.ToString(CultureInfo.InvariantCulture)),
            Required("StatusCode", state == WorkflowSchema.ActivatedState ? "2" : "1"),
            Optional("CreateStage"),
            Optional("UpdateStage"),
            Optional("DeleteStage"),
            Required("RunAs", "1"),
            values.GetValueOrDefault("Rank") is { } rank and not "0" ? new XElement("Rank", rank) : null,
            messageId is { } message ? new XElement("SdkMessageId", message.ToString("B")) : null,
            Optional("UniqueName"),
            Optional("InputParameters"),
            Required("IsTransacted", "1"),
            Optional("IntroducedVersion"),
            Required("IsCustomizable", "1"),
            new XElement("BusinessProcessType", "0"),
            category == (int)WorkflowSchema.Category.Action ? new XElement("FormId", Guid.Empty.ToString("B")) : null,
            new XElement("IsCustomProcessingStepAllowedForOtherPublishers", "1"),
            Optional("ProcessTriggerScope"),
            new XElement("ModernFlowType", "0"),
            primaryEntity is null ? null : new XElement("PrimaryEntity", primaryEntity),
            ProcessLabels("LocalizedNames", "LocalizedName", name, language),
            string.IsNullOrEmpty(description) ? null : ProcessLabels("Descriptions", "Description", description, language),
            StepLabels(xaml));
    }

    /// <summary>
    /// The <c>LocalizedNames</c> or <c>Descriptions</c> of a process in the base language: <c>languagecode</c> before <c>description</c>,
    /// as the real export writes a process's labels (other components write <c>description</c> first, <see cref="KeptXml.Labels"/>).
    /// </summary>
    private static XElement ProcessLabels(string container, string item, string text, int language) =>
        new(container, string.IsNullOrEmpty(text)
            ? null
            : new XElement(item, new XAttribute("languagecode", language.ToString(CultureInfo.InvariantCulture)), new XAttribute("description", text)));

    /// <summary>
    /// Ruling R10 (plan 2.21): the kept XML of a process whose name, description or XAML an accepted Update changed carries
    /// <see cref="WorkflowSchema.StaleLabelsAttribute"/>. Its <c>LocalizedNames</c>, <c>Descriptions</c> and <c>labels</c> are then
    /// replaced by those of the row and the XAML (as a compiled process's, <see cref="WorkflowElement"/>), its <c>Description</c>
    /// attribute set from the row and the mark removed; every other element the import kept is written as it was. A kept element
    /// without the mark is left as imported (ruling W22).
    /// </summary>
    private static void RefreshStaleLabels(XElement kept, string name, string? description, int language, string xaml)
    {
        if (kept.Attribute(WorkflowSchema.StaleLabelsAttribute) is not { } mark) return;
        mark.Remove();
        kept.SetAttributeValue("Description", string.IsNullOrEmpty(description) ? null : description);
        var old = kept.Elements().Where(e => e.Name.LocalName is "LocalizedNames" or "Descriptions" or "labels").ToList();
        // In the place of the first stale label element (after PrimaryEntity in an export), else at the end.
        var anchor = old.FirstOrDefault()?.PreviousNode;
        var fresh = new object?[]
        {
            ProcessLabels("LocalizedNames", "LocalizedName", name, language),
            string.IsNullOrEmpty(description) ? null : ProcessLabels("Descriptions", "Description", description, language),
            StepLabels(xaml),
        };
        var hadOld = old.Count > 0;
        old.ForEach(e => e.Remove());
        if (anchor is not null) anchor.AddAfterSelf(fresh);
        else if (hadOld) kept.AddFirst(fresh);
        else kept.Add(fresh);
    }

    /// <summary>The namespace of <c>mcwo:StepLabel</c> (the designer's object model).</summary>
    private const string StepLabelNamespace = "clr-namespace:Microsoft.Crm.Workflow.ObjectModel;assembly=Microsoft.Crm";

    /// <summary>
    /// The <c>labels</c> element of the step labels of <paramref name="xaml"/>: one <c>steplabels</c> per label id (<c>{id}</c>), with a
    /// <c>label</c> per language (<c>languagecode</c>, <c>description</c>), as the real export writes a business rule's error message;
    /// null when the XAML has none (or does not parse: the XAML file is exported as it is).
    /// </summary>
    private static XElement? StepLabels(string xaml)
    {
        XDocument document;
        try
        {
            // No DTD (final review): as the other parses of stored XML. IgnoreWhitespace keeps XDocument.Parse's tree.
            using var reader = XmlReader.Create(new StringReader(xaml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
        var labels = document.Descendants()
            .Where(e => e.Name.LocalName == "StepLabel" && Xrmd.Workflows.Xaml.ActivityCatalog.NormalizeNamespace(e.Name.NamespaceName) == StepLabelNamespace)
            .Select(e => (Id: Guid.TryParse((string?)e.Attribute("LabelId"), out var labelId) ? labelId : (Guid?)null,
                Language: (string?)e.Attribute("LanguageCode"), Text: (string?)e.Attribute("Description")))
            .Where(l => l.Id is not null && !string.IsNullOrEmpty(l.Language))
            .GroupBy(l => l.Id!.Value)
            .Select(g => new XElement("steplabels", new XAttribute("id", g.Key.ToString("B")),
                g.Select(l => new XElement("label", new XAttribute("languagecode", l.Language!), new XAttribute("description", l.Text ?? "")))))
            .ToList();
        return labels.Count == 0 ? null : new XElement("labels", labels);
    }

    /// <summary>The trigger columns an export writes, as the child elements of a <c>ProcessTrigger</c> (from memory, checklist 1h).</summary>
    private static readonly string[] TriggerColumns =
    [
        WorkflowSchema.ControlName, WorkflowSchema.ControlType, WorkflowSchema.Event, WorkflowSchema.FormId, WorkflowSchema.MethodId,
        WorkflowSchema.PipelineStage, WorkflowSchema.PrimaryEntityTypeCode, WorkflowSchema.TriggerScope,
    ];

    /// <summary>
    /// The <c>ProcessTrigger</c> elements of a process's <c>processtrigger</c> rows: the element of the kept XML for a trigger it has
    /// (in its order), an element of the row's values for another one (by id).
    /// </summary>
    private static async Task<List<XElement>> ProcessTriggersAsync(DataSession s, Guid processId, XElement source, CancellationToken ct)
    {
        var rows = new SortedDictionary<Guid, XElement>();
        await using (var cmd = s.Command($"""
                         SELECT {WorkflowSchema.ProcessTriggerId}, {string.Join(", ", TriggerColumns)}
                         FROM {s.DataTable(WorkflowSchema.TriggerTable)} WHERE {WorkflowSchema.ProcessId} = @id
                         """))
        {
            cmd.AddParameter("@id", DbType.Guid, processId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetGuid(0);
                var element = new XElement("ProcessTrigger", new XAttribute(WorkflowSchema.ProcessTriggerId, id.ToString("B")));
                for (var i = 0; i < TriggerColumns.Length; i++)
                {
                    var value = rd.GetValue(1 + i);
                    if (value is DBNull) continue;
                    element.Add(new XElement(TriggerColumns[i], value is Guid guid ? guid.ToString("B") : Convert.ToString(value, CultureInfo.InvariantCulture)));
                }
                rows[id] = element;
            }
        }
        var triggers = new List<XElement>();
        foreach (var kept in source.Element("ProcessTriggers")?.Elements("ProcessTrigger") ?? [])
            if (CustomizationsReader.ProcessTriggerId(kept) is { } id && rows.Remove(id))
                triggers.Add(new XElement(kept));
        triggers.AddRange(rows.Values);
        return triggers;
    }

    /// <summary>The id a step's message is written with (carry-forward of the T7 ruling): 400 naming it when there is none.</summary>
    private static Guid DataverseMessageId(string step, Guid id, string message)
    {
        // A custom action or custom API message may have the same derived id (plan 15): only the catalog's messages map.
        var isCatalogRow = SdkMessageCatalog.IsCatalogMessage(message) && SdkMessageCatalog.MessageId(message) == id;
        if (!isCatalogRow) return id; // a custom message keeps its own id
        return DataverseSdkMessages.IdOf(message)
               ?? throw XrmdFaultException.InvalidArgument(
                   $"Step '{step}' is registered on the message '{message}', whose Dataverse sdkmessageid xrmverse does not know: it cannot be exported.");
    }

    // ---- missing dependencies -----------------------------------------------------------------------------------------

    /// <summary>
    /// The missing dependencies of the file (shape from memory, not verified on a real export): each entity written but not
    /// held by the solution (a shell, or the other side of a relationship), with each component of the export that needs it,
    /// and each global option set an attribute written uses that the solution does not hold, with that attribute. The system
    /// entities (which every organization has) are not listed. <c>solution</c>: another solution holding the component, else
    /// <c>Active</c>.
    /// </summary>
    private async Task<List<PackageMissingDependency>> MissingDependenciesAsync(DataSession s, Guid solutionId, Builder builder,
        CancellationToken ct)
    {
        var missing = new List<PackageMissingDependency>();
        foreach (var (logicalName, dependents) in builder.Dependents)
        {
            if (builder.Held.Contains(logicalName) || store.GetEntity(logicalName) is not { } entity || MetadataStore.IsSystemEntity(entity)) continue;
            var required = new PackageDependencyNode(ComponentType.Entity, logicalName, Base(entity.DisplayName),
                await HoldingSolutionAsync(s, solutionId, entity.MetadataId!.Value, ct));
            missing.AddRange(dependents.Select(d => new PackageMissingDependency(required, d)));
        }
        foreach (var (optionSet, entity, attribute) in builder.UnheldOptionSets())
        {
            var id = optionSet.MetadataId ?? MetadataStore.OptionSetId(optionSet.Name);
            missing.Add(new PackageMissingDependency(
                new PackageDependencyNode(ComponentType.OptionSet, optionSet.Name, Base(optionSet.DisplayName),
                    await HoldingSolutionAsync(s, solutionId, id, ct)),
                new PackageDependencyNode(ComponentType.Attribute, attribute.LogicalName, Base(attribute.DisplayName),
                    ParentSchemaName: entity.LogicalName, ParentDisplayName: Base(entity.DisplayName))));
        }
        return missing.Distinct()
            .OrderBy(m => m.Required.Type).ThenBy(m => m.Required.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Dependent.Type).ThenBy(m => m.Dependent.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Another (not a system) solution holding the component; <c>Active</c> when there is none.</summary>
    private static async Task<string> HoldingSolutionAsync(DataSession s, Guid solutionId, Guid objectId, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit($"""
            SELECT so.uniquename FROM {s.DataTable("solutioncomponent")} AS sc
            JOIN {s.DataTable("solution")} AS so ON so.solutionid = sc.solutionid
            WHERE sc.objectid = @id AND sc.solutionid NOT IN (@solution, @default, @active)
            ORDER BY so.uniquename
            """, "1"));
        cmd.AddParameter("@id", DbType.Guid, objectId);
        cmd.AddParameter("@solution", DbType.Guid, solutionId);
        cmd.AddParameter("@default", DbType.Guid, SystemSeed.DefaultSolutionId);
        cmd.AddParameter("@active", DbType.Guid, SystemSeed.ActiveSolutionId);
        return await cmd.ExecuteScalarAsync(ct) as string ?? SystemSeed.ActiveSolutionName;
    }

    private static string? Base(Label? label) => label?.UserLocalizedLabel?.Label ?? label?.LocalizedLabels.FirstOrDefault()?.Label;

    // ---- root components ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The root components: every component the solution holds but its subcomponents (an attribute, relationship, view, chart or
    /// form of an entity it holds, a plug-in type of an assembly it holds, an image of a step it holds), each by the key a
    /// solution file gives it, ordered by type then key.
    /// </summary>
    private List<(int Type, string SchemaNameOrId, int Behavior)> RootComponents(List<Component> components, Builder builder,
        List<OpaqueComponent> opaque, Dictionary<Guid, string> keys, List<PackageWebResource> webResources,
        Dictionary<Guid, Guid> typeAssemblies, List<PackageStep> steps, List<(Guid Id, PackageEnvironmentVariable Variable)> environmentVariables,
        List<(Guid Id, PackageCustomControl Control)> customControls)
    {
        var variableNames = environmentVariables.ToDictionary(v => v.Id, v => v.Variable.SchemaName);
        var controlNames = customControls.ToDictionary(c => c.Id, c => c.Control.Name);
        var roots = new List<(int Type, string Key, int Behavior)>();
        var entities = store.GetAllEntities();
        var heldAssemblies = Of(components, ComponentType.PluginAssembly).ToHashSet();
        var heldSteps = Of(components, ComponentType.SdkMessageProcessingStep).ToHashSet();
        foreach (var component in components)
        {
            string? key = component.Type switch
            {
                ComponentType.Entity => entities.First(e => e.MetadataId == component.ObjectId).LogicalName,
                ComponentType.Attribute => builder.RootAttributes.Contains(component.ObjectId) ? component.ObjectId.ToString("D") : null,
                ComponentType.EntityRelationship => RelationshipRoot(component.ObjectId, builder),
                ComponentType.OptionSet => builder.OptionSets()
                    .First(o => o.MetadataId == component.ObjectId || MetadataStore.OptionSetId(o.Name) == component.ObjectId).Name,
                ComponentType.SavedQuery or ComponentType.SystemForm or ComponentType.SavedQueryVisualization =>
                    opaque.First(o => o.Id == component.ObjectId.ToString("D")).EntityLogicalName is { } entity && builder.Held.Contains(entity)
                        ? null
                        : component.ObjectId.ToString("D"),
                ComponentType.WebResource => webResources.First(w => w.Id == component.ObjectId).Name,
                ComponentType.SiteMap or ComponentType.AppModule => keys[component.ObjectId],
                ComponentType.PluginType => heldAssemblies.Contains(typeAssemblies[component.ObjectId]) ? null : component.ObjectId.ToString("D"),
                ComponentType.SdkMessageProcessingStepImage =>
                    steps.Any(st => heldSteps.Contains(st.Id) && st.Images.Any(i => i.Id == component.ObjectId)) ? null : component.ObjectId.ToString("D"),
                ComponentType.RolePrivilege => null, // written with its role
                ComponentType.CustomApi => null, // a real export lists no custom API under RootComponents (plan 15, C11)
                // #167 (E4): Dataverse's own shape, by schema name.
                ComponentType.EnvironmentVariableDefinition => variableNames[component.ObjectId],
                // Plan 3.5 UI-1d (R4): by name, as the OpenProject365 export lists them.
                ComponentType.CustomControl => controlNames[component.ObjectId],
                _ => component.ObjectId.ToString("D"),
            };
            if (key is not null) roots.Add((component.Type, key, component.Behavior));
        }
        return roots.Distinct().OrderBy(r => r.Type).ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string? RelationshipRoot(Guid id, Builder builder)
    {
        RelationshipMetadataBase? relationship = store.GetOneToManyRelationships().FirstOrDefault(r => r.MetadataId == id) as RelationshipMetadataBase
                                                 ?? store.GetManyToManyRelationships().FirstOrDefault(r => r.MetadataId == id);
        if (relationship is null) return null;
        var sides = relationship switch
        {
            OneToManyRelationshipMetadata o => new[] { o.ReferencingEntity, o.ReferencedEntity },
            ManyToManyRelationshipMetadata m => [m.Entity1LogicalName, m.Entity2LogicalName],
            _ => [],
        };
        return sides.Any(side => side is not null && builder.Held.Contains(side)) ? null : relationship.SchemaName;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static string? Text(DbDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);

    private static int? Int(DbDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetInt32(i);

    private static byte[] Base64(string? content, string owner)
    {
        if (string.IsNullOrEmpty(content)) throw XrmdFaultException.InvalidArgument($"The {owner} has no content: it cannot be exported.");
        try
        {
            return Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            throw XrmdFaultException.InvalidArgument($"The content of the {owner} is not valid base64: it cannot be exported.");
        }
    }

    private static XElement Parse(string xml, string what)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XElement.Load(reader);
        }
        catch (XmlException e)
        {
            throw XrmdFaultException.InvalidArgument($"The {what} is not valid XML: {e.Message}");
        }
    }

    /// <summary>
    /// The kept XML of <paramref name="component"/> (the element <paramref name="name"/>); null when nothing is kept. Kept XML
    /// that is not valid XML or has another root was corrupted: 500 naming the component, never a silent fallback.
    /// </summary>
    /// <exception cref="XrmdFaultException">500 Unexpected naming the component.</exception>
    internal static XElement? Kept(string? xml, string name, string component)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        XElement element;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            element = XElement.Load(reader);
        }
        catch (XmlException e)
        {
            throw new XrmdFaultException(ErrorCodes.Unexpected, $"The kept XML of {component} is not valid XML: {e.Message}", 500);
        }
        return element.Name.LocalName == name
            ? element
            : throw new XrmdFaultException(ErrorCodes.Unexpected,
                $"The kept XML of {component} has the root '{element.Name.LocalName}', not '{name}'.", 500);
    }

    /// <summary>
    /// The descriptor of an app module when it is the solution XML an import kept (<c>AppModule</c>); null for any other
    /// descriptor (an app made in xrmverse has its own, which is not solution XML).
    /// </summary>
    private static XElement? AppModuleXml(string? descriptor)
    {
        if (string.IsNullOrEmpty(descriptor) || !descriptor.TrimStart().StartsWith('<')) return null;
        try
        {
            var element = XElement.Parse(descriptor);
            return element.Name.LocalName == "AppModule" ? element : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static void SetValue(XElement parent, string name, string? value)
    {
        if (value is null) return;
        if (parent.Element(name) is { } element) element.Value = value;
        else parent.Add(new XElement(name, value));
    }

    private static void SetXml(XElement parent, string name, string? xml)
    {
        if (xml is null) return;
        var content = Parse(xml, $"{name} of {parent.Name.LocalName}");
        if (parent.Element(name) is { } element) element.ReplaceNodes(content);
        else parent.Add(new XElement(name, content));
    }

    /// <summary>
    /// The root <c>Dashboard</c> element of a system dashboard (plan 3.5 UI-1f, ruling F1), a <c>systemform</c> row of no table read
    /// by <paramref name="rd"/>: the element the import kept (its ids as they came), with the row's <c>formxml</c> as its
    /// <c>FormXml/forms/form</c>, its name and its description put in; without kept XML, the element of the columns.
    /// </summary>
    private static XElement Dashboard(DbDataReader rd, Guid id, string formType, int language)
    {
        var xml = Kept(Text(rd, 5), "Dashboard", $"dashboard {id:D}")
                  ?? new XElement("Dashboard", new XElement("LocalizedNames"), new XElement("Descriptions"), new XElement("FormId", id.ToString("B")),
                      new XElement("FormXml", new XElement("forms", new XAttribute("type", formType))));
        if (Text(rd, 3) is { } formXml)
        {
            var form = Parse(formXml, $"the formxml of dashboard {id}");
            var container = xml.Element("FormXml") ?? new XElement("FormXml");
            if (container.Parent is null) xml.Add(container);
            var forms = container.Element("forms") ?? new XElement("forms", new XAttribute("type", formType));
            if (forms.Parent is null) container.Add(forms);
            if (forms.Element("form") is { } current) current.ReplaceWith(form);
            else forms.Add(form);
        }
        SetName(xml, Text(rd, 1), language);
        SetLabel(xml, "Descriptions", "Description", Text(rd, 6), language);
        return xml;
    }

    /// <summary>The row's name is the base-language <c>LocalizedName</c> (the other languages stay as kept).</summary>
    private static void SetName(XElement component, string? name, int language) =>
        SetLabel(component, "LocalizedNames", "LocalizedName", name, language);

    /// <summary>
    /// The base-language label <c>container/element</c> of <paramref name="component"/> is <paramref name="text"/> (the other
    /// languages stay as kept); nothing changes when <paramref name="text"/> is empty.
    /// </summary>
    private static void SetLabel(XElement component, string container, string element, string? text, int language)
    {
        if (string.IsNullOrEmpty(text)) return;
        var code = language.ToString(CultureInfo.InvariantCulture);
        var labels = component.Element(container);
        if (labels is null)
        {
            labels = new XElement(container);
            component.Add(labels);
        }
        if (labels.Elements(element).FirstOrDefault(n => (string?)n.Attribute("languagecode") == code) is { } entry)
            entry.SetAttributeValue("description", text);
        else labels.AddFirst(new XElement(element, new XAttribute("description", text), new XAttribute("languagecode", code)));
    }

    private static async Task<object?> ScalarAsync(DataSession s, string sql, CancellationToken ct, Guid id)
    {
        await using var cmd = s.Command(sql);
        cmd.AddParameter("@id", DbType.Guid, id);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull ? null : result;
    }
}
