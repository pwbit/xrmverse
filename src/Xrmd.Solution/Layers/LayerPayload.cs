using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Solution.Layers;

/// <summary>
/// The payload of a layer (plan 2.6, ruling M5): the component as its solution delivered it, as JSON
/// <c>{"xml": "&lt;fragment&gt;", "files": {"&lt;archive path&gt;": "&lt;base64&gt;"}, "language": 1033, "prefix": "xv"}</c>. The fragment is a
/// small <c>ImportExportXml</c> holding the component's element of <c>customizations.xml</c> as the reader saw it, inside the
/// containers the reader needs:
/// <list type="bullet">
/// <item>a table: its <c>Entity</c> element without its columns, views and forms (each a layer of its own);</item>
/// <item>a column, view, form or system chart (plan 3.5 UI-1f): its own element inside an <c>Entity</c> that carries only the
/// table's name;</item>
/// <item>a custom lookup and its one-to-many relationships travel together (the reader cannot read one without the
/// other), so the payload of either holds both;</item>
/// <item>a row component (role, profile, web resource, assembly, process, step, site map, app module, system dashboard of
/// plan 3.5 UI-1f): its element; a web resource, an assembly and a process (plan 2.14) with the file they name;</item>
/// <item>a custom API: no element, its <c>customapis/</c> files;</item>
/// <item>an environment variable definition (#167): no element, the files of its <c>environmentvariabledefinitions/</c> folder
/// (its value file with it, when the solution carries one);</item>
/// <item>a custom control (plan 3.5 UI-1d): its <c>CustomControl</c> element with every file of its manifest's folder.</item>
/// </list>
/// <c>language</c> is the base language of the file (the label the reader takes as the user's) and <c>prefix</c> its publisher's
/// customization prefix (the reader takes a many-to-many relationship without <c>IsCustomRelationship</c> as custom when its name
/// has it). <see cref="ToPackage"/>
/// merges payloads back into a package the importer applies (restore mode, <see cref="LayerRestorer"/>).
/// </summary>
public static class LayerPayload
{
    private const string Root = "ImportExportXml";
    private const int DefaultLanguage = 1033;

    /// <summary>The containers of the row components and relationships, in the order <see cref="ToPackage"/> writes them.</summary>
    private static readonly (string Container, string Element)[] Sections =
    [
        ("EntityRelationships", "EntityRelationship"),
        ("optionsets", "optionset"),
        // The system dashboards (plan 3.5 UI-1f, ruling F1): forms of no table, under the root.
        ("Dashboards", "Dashboard"),
        ("CustomControls", "CustomControl"),
        ("Roles", "Role"),
        ("FieldSecurityProfiles", "FieldSecurityProfile"),
        ("SolutionPluginAssemblies", "PluginAssembly"),
        ("Workflows", "Workflow"),
        ("SdkMessageProcessingSteps", "SdkMessageProcessingStep"),
        ("WebResources", "WebResource"),
        ("AppModuleSiteMaps", "AppModuleSiteMap"),
        ("AppModules", "AppModule"),
    ];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Document(
        [property: JsonPropertyName("xml")] string Xml,
        [property: JsonPropertyName("files")] Dictionary<string, string>? Files,
        [property: JsonPropertyName("language")] int? Language,
        [property: JsonPropertyName("prefix")] string? Prefix);

    /// <summary>
    /// The key of a component in <see cref="SolutionPackage.Sources"/>: the type (3 as 10) and the id in <c>D</c> format, or
    /// the lower-cased name (a table's logical name, <c>table.column</c>, a relationship's schema name, an option set's name,
    /// a site map's or app module's unique name, a custom API's unique name, an environment variable's schema name, a custom
    /// control's name).
    /// </summary>
    public static (int Type, string Key) Key(int type, string nameOrId)
    {
        ArgumentNullException.ThrowIfNull(nameOrId);
        return (ComponentType.Normalize(type),
            Guid.TryParse(nameOrId, out var id) ? id.ToString("D") : nameOrId.ToLowerInvariant());
    }

    /// <summary>
    /// The payload of the component with that id: for the components a solution file gives an id (roles, views, forms, web
    /// resources, assemblies, steps, field security profiles) and global option sets (their id derives from the name). A
    /// table, column or relationship has no id in a file (xrmverse gives it its <c>MetadataId</c>): use
    /// <see cref="From(SolutionPackage, int, string)"/> with its name.
    /// </summary>
    /// <exception cref="ArgumentException">For a type the id cannot name, or no such component in the package.</exception>
    public static string From(SolutionPackage package, int type, Guid id)
    {
        ArgumentNullException.ThrowIfNull(package);
        type = ComponentType.Normalize(type);
        var key = type switch
        {
            ComponentType.OptionSet => package.OptionSets.FirstOrDefault(o => o.Name is not null && MetadataStore.OptionSetId(o.Name) == id)?.Name
                                       ?? throw new ArgumentException($"The package has no global option set with id {id}.", nameof(id)),
            ComponentType.Entity or ComponentType.Attribute or ComponentType.EntityRelationship or ComponentType.SiteMap
                or ComponentType.AppModule or ComponentType.CustomApi or ComponentType.EnvironmentVariableDefinition
                or ComponentType.CustomControl =>
                throw new ArgumentException($"A component of type {type} is named by its key in a solution file, not by an id.", nameof(type)),
            _ => id.ToString("D"),
        };
        return From(package, type, key);
    }

    /// <summary>The payload of the component <paramref name="key"/> (see <see cref="Key"/>) of the package.</summary>
    /// <exception cref="ArgumentException">When the package has no source for it.</exception>
    public static string From(SolutionPackage package, int type, string key)
    {
        ArgumentNullException.ThrowIfNull(package);
        var source = Source(package, type, key)
                     ?? throw new ArgumentException($"The package has no component of type {type} named '{key}'.", nameof(key));
        type = ComponentType.Normalize(type);
        var root = new XElement(Root);
        var files = new Dictionary<string, byte[]>(source.Files, StringComparer.Ordinal);

        switch (type)
        {
            case ComponentType.Entity:
            {
                var shell = new XElement(source.SourceXml);
                shell.Element("SavedQueries")?.Remove();
                shell.Element("Visualizations")?.Remove();
                shell.Element("FormXml")?.Remove();
                shell.Element("EntityInfo")?.Element("entity")?.Element("attributes")?.RemoveNodes();
                root.Add(new XElement("Entities", shell));
                break;
            }
            case ComponentType.Attribute:
            {
                var entity = source.Entity!;
                var attribute = AttributeName(key);
                var relationships = LookupRelationships(package, entity, attribute);
                var companions = Companions(package, entity, attribute)
                    .Select(c => Source(package, ComponentType.Attribute, $"{entity}.{c}")?.SourceXml).OfType<XElement>().Select(x => new XElement(x));
                root.Add(new XElement("Entities", Wrapper(package, entity, attributes: [new XElement(source.SourceXml), .. companions])));
                if (relationships.Count > 0) root.Add(new XElement("EntityRelationships", relationships));
                break;
            }
            case ComponentType.EntityRelationship:
            {
                if (package.Relationships.OfType<OneToManyRelationshipMetadata>()
                        .FirstOrDefault(r => string.Equals(r.SchemaName, key, StringComparison.OrdinalIgnoreCase)) is { } oneToMany
                    && Source(package, ComponentType.Attribute, $"{oneToMany.ReferencingEntity}.{oneToMany.ReferencingAttribute}") is { } lookup)
                {
                    root.Add(new XElement("Entities", Wrapper(package, lookup.Entity!, attributes: [new XElement(lookup.SourceXml)])));
                    // The relationship itself first: ToPackage takes the first one as the payload's own component.
                    root.Add(new XElement("EntityRelationships", LookupRelationships(package, oneToMany.ReferencingEntity!, oneToMany.ReferencingAttribute!)
                        .OrderBy(r => string.Equals((string?)r.Attribute("Name"), key, StringComparison.OrdinalIgnoreCase) ? 0 : 1)));
                }
                else root.Add(new XElement("EntityRelationships", new XElement(source.SourceXml)));
                break;
            }
            case ComponentType.SavedQuery:
                root.Add(new XElement("Entities", Wrapper(package, source.Entity!, views: [new XElement(source.SourceXml)])));
                break;
            case ComponentType.SystemForm when source.Entity is not null: // a dashboard (no table) is a row component below
                root.Add(new XElement("Entities", Wrapper(package, source.Entity!, forms: [(source.FormType, new XElement(source.SourceXml))])));
                break;
            case ComponentType.SavedQueryVisualization:
                root.Add(new XElement("Entities", Wrapper(package, source.Entity!, charts: [new XElement(source.SourceXml)])));
                break;
            case ComponentType.CustomApi or ComponentType.EnvironmentVariableDefinition:
                break; // its files only
            default:
                var container = Sections.FirstOrDefault(s => s.Element == source.SourceXml.Name.LocalName).Container
                                ?? throw new ArgumentException($"Components of type {type} have no payload.", nameof(type));
                root.Add(new XElement(container, new XElement(source.SourceXml)));
                break;
        }

        return JsonSerializer.Serialize(new Document(root.ToString(SaveOptions.DisableFormatting),
            files.ToDictionary(f => f.Key, f => Convert.ToBase64String(f.Value), StringComparer.Ordinal),
            package.Solution.LanguageCode, package.Publisher.CustomizationPrefix), Json);
    }

    /// <summary>
    /// A package made of <paramref name="payloads"/> (each written by <see cref="From(SolutionPackage, int, string)"/> or
    /// <see cref="SolutionExporter.WriteComponentFragment"/>), read by <see cref="CustomizationsReader"/>: the tables whose
    /// own payload is in the list are root components, the others are carried only for their columns, views and forms (the
    /// importer's restore mode leaves those tables as they are). A component given twice (by its key: its type and its name
    /// or id; a custom API or an environment variable by its folder) is taken from its own payload over a companion copy in another payload (a lookup's
    /// relationship in the lookup's payload, or the reverse), else from its first payload. A payload's own component is the
    /// first element of its type in its fragment, as <see cref="From(SolutionPackage, int, string)"/> writes it. The publisher and solution are placeholders: the restore
    /// mode writes neither.
    /// </summary>
    /// <exception cref="ArgumentException">For a payload that is not one (not JSON, no fragment).</exception>
    /// <exception cref="XrmdFaultException">400 for payloads of different languages, naming both (ruling R10).</exception>
    public static SolutionPackage ToPackage(IReadOnlyList<(int Type, Guid Id, string Payload)> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var entities = new Dictionary<string, EntityParts>(StringComparer.Ordinal);
        var sections = Sections.ToDictionary(s => s.Container, _ => new List<XElement>(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var apiFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<(bool Own, string Key, Action Add)>();
        int? language = null;
        string? prefix = null;

        foreach (var (type, id, payload) in payloads)
        {
            var (document, fragment) = Load(payload, type, id);
            var payloadFiles = Files(document);
            if (document.Language is { } given && language is { } first && given != first)
                throw XrmdFaultException.InvalidArgument(
                    $"The layers to restore have different languages ({first} and {given}, the latter on component {id} of type {type}): one restore takes one language.");
            language ??= document.Language;
            prefix ??= document.Prefix;
            // A many-to-many relationship read under another publisher's prefix keeps the flag its own file gave it.
            if (document.Prefix is { } own && !string.Equals(own, prefix, StringComparison.OrdinalIgnoreCase))
                foreach (var relationship in fragment.Element("EntityRelationships")?.Elements("EntityRelationship") ?? [])
                    if ((string?)relationship.Element("EntityRelationshipType") == "ManyToMany" && relationship.Element("IsCustomRelationship") is null)
                        relationship.AddFirst(new XElement("IsCustomRelationship",
                            ((string?)relationship.Attribute("Name") ?? "").StartsWith(own + "_", StringComparison.OrdinalIgnoreCase) ? "1" : "0"));
            // A custom API or an environment variable given twice keeps the files of its first payload.
            var folders = payloadFiles.Keys.Select(FileFolder).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, content) in payloadFiles)
            {
                // The formula columns of one table share a formula file; each payload carries its column's line (plan 2.12c): merged.
                if (FormulaFiles.IsFormulaFile(path) && files.TryGetValue(path, out var earlier))
                    files[path] = FormulaFiles.Merge(earlier, content, path);
                else if (FileFolder(path) is not { } folder || !apiFolders.Contains(folder)) files.TryAdd(path, content);
            }
            apiFolders.UnionWith(folders);
            // The payload's own component is the first element of its kind (From writes it first); the rest are companions.
            var kind = ComponentType.Normalize(type);
            var ownFound = false;
            void Add(int elementKind, string key, Action add)
            {
                var own = !ownFound && elementKind == kind;
                ownFound |= own;
                items.Add((own, key, add));
            }
            foreach (var entity in fragment.Element("Entities")?.Elements("Entity") ?? [])
            {
                var info = entity.Element("EntityInfo")?.Element("entity");
                var schema = (string?)info?.Attribute("Name") ?? ((string?)entity.Element("Name"))?.Trim() ?? "";
                var lower = schema.ToLowerInvariant();
                if (!entities.TryGetValue(lower, out var parts)) entities[lower] = parts = new EntityParts(schema);
                if (kind == ComponentType.Entity) Add(ComponentType.Entity, $"Entity|{lower}", () => parts.Shell ??= entity);
                foreach (var attribute in info?.Element("attributes")?.Elements("attribute") ?? [])
                    Add(ComponentType.Attribute, $"attribute|{lower}.{AttributeKey(attribute)}", () => parts.Attributes.TryAdd(AttributeKey(attribute), attribute));
                foreach (var view in entity.Element("SavedQueries")?.Element("savedqueries")?.Elements("savedquery") ?? [])
                    Add(ComponentType.SavedQuery, ElementKey(view), () => parts.Views.Add(view));
                foreach (var forms in entity.Element("FormXml")?.Elements("forms") ?? [])
                    foreach (var form in forms.Elements("systemform"))
                        Add(ComponentType.SystemForm, ElementKey(form), () => parts.Forms.Add(((string?)forms.Attribute("type"), form)));
                foreach (var chart in entity.Element("Visualizations")?.Elements("visualization") ?? [])
                    Add(ComponentType.SavedQueryVisualization, ElementKey(chart), () => parts.Charts.Add(chart));
            }
            foreach (var (container, element) in Sections)
                foreach (var item in fragment.Element(container)?.Elements(element) ?? [])
                {
                    var list = sections[container];
                    Add(element == "EntityRelationship" ? ComponentType.EntityRelationship : kind, ElementKey(item), () => list.Add(item));
                }
        }

        // Each payload's own component first (it wins over a companion copy of it in another payload), then the companions;
        // among equals, the first payload wins.
        foreach (var (_, key, add) in items.Where(i => i.Own).Concat(items.Where(i => !i.Own)))
            if (seen.Add(key)) add();

        var customizations = new XDocument(new XElement(Root,
            new XElement("Entities", entities.Values.Select(e => e.Build())),
            Sections.Select(s => new XElement(s.Container, sections[s.Container]))));
        var lang = language ?? DefaultLanguage;
        var solution = new XDocument(new XElement(Root, new XAttribute("languagecode", lang),
            new XElement("SolutionManifest",
                new XElement("UniqueName", "xrmverse_layers"),
                new XElement("LocalizedNames"),
                new XElement("Version", "1.0.0.0"),
                new XElement("Managed", 1),
                new XElement("Publisher",
                    new XElement("UniqueName", "xrmverselayers"),
                    new XElement("LocalizedNames"),
                    new XElement("CustomizationPrefix", prefix ?? "xrmverse"),
                    new XElement("CustomizationOptionValuePrefix", 10000)),
                new XElement("RootComponents", entities.Where(e => e.Value.Shell is not null).Select(e => new XElement("RootComponent",
                    new XAttribute("type", ComponentType.Entity), new XAttribute("schemaName", e.Key), new XAttribute("behavior", 0)))))));
        return CustomizationsReader.Read(solution, customizations, files);
    }

    /// <summary>The fragment, files and language of a payload.</summary>
    /// <exception cref="ArgumentException">For a payload that is not one.</exception>
    public static (XElement Fragment, IReadOnlyDictionary<string, byte[]> Files, int? Language) Parse(string payload, int type = 0, Guid id = default)
    {
        var (document, fragment) = Load(payload, type, id);
        return (fragment, Files(document), document.Language);
    }

    private static (Document Document, XElement Fragment) Load(string payload, int type, Guid id)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(payload, Json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The layer payload of component {id} of type {type} is not valid JSON: {ex.Message}", nameof(payload));
        }
        if (document?.Xml is not { Length: > 0 } xml)
            throw new ArgumentException($"The layer payload of component {id} of type {type} has no fragment.", nameof(payload));
        try
        {
            return (document, XElement.Parse(xml, LoadOptions.PreserveWhitespace));
        }
        catch (System.Xml.XmlException ex)
        {
            throw new ArgumentException($"The fragment of the layer payload of component {id} of type {type} is not valid XML: {ex.Message}", nameof(payload));
        }
    }

    private static Dictionary<string, byte[]> Files(Document document) =>
        (document.Files ?? []).ToDictionary(f => f.Key, f => Convert.FromBase64String(f.Value), StringComparer.Ordinal);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static PackageComponentSource? Source(SolutionPackage package, int type, string key) =>
        package.Sources?.GetValueOrDefault(Key(type, key));

    /// <summary>
    /// The folder of a custom API (<c>customapis/{uniquename}</c>), of an environment variable
    /// (<c>environmentvariabledefinitions/{schemaname}</c>) or of a custom control (<c>Controls/{name}</c>) a file belongs to; null
    /// for any other file.
    /// </summary>
    private static string? FileFolder(string path)
    {
        foreach (var root in new[] { "customapis/", EnvironmentVariableReader.Folder + "/", "Controls/" })
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path.IndexOf('/', root.Length) is var end and > 0)
                return path[..end];
        return null;
    }

    /// <summary>
    /// The key of a component element for <see cref="ToPackage"/>: its element name and its name or id as the reader takes it
    /// (normalized as <see cref="Key"/> does); the element's text for an element without one.
    /// </summary>
    private static string ElementKey(XElement element)
    {
        string? Attribute(string name) => (string?)element.Attribute(name);
        string? Child(string name) => ((string?)element.Element(name))?.Trim();
        var key = element.Name.LocalName switch
        {
            "EntityRelationship" or "optionset" => Attribute("Name"),
            "Role" => Attribute("id"),
            "FieldSecurityProfile" => Attribute("fieldsecurityprofileid"),
            "PluginAssembly" => Attribute("PluginAssemblyId"),
            "SdkMessageProcessingStep" => Attribute("SdkMessageProcessingStepId"),
            "Workflow" => Attribute("WorkflowId"),
            "WebResource" => Child("WebResourceId"),
            "AppModuleSiteMap" => Child("SiteMapUniqueName"),
            "AppModule" => Child("UniqueName"),
            "CustomControl" => Child("Name"),
            "savedquery" => Child("savedqueryid"),
            "systemform" => Child("formid"),
            "Dashboard" => Child("FormId"),
            "visualization" => Child("savedqueryvisualizationid"),
            _ => null,
        };
        return $"{element.Name.LocalName}|{(string.IsNullOrWhiteSpace(key) ? element.ToString(SaveOptions.DisableFormatting) : Key(0, key.Trim()).Key)}";
    }

    private static string AttributeName(string key) => key[(key.IndexOf('.', StringComparison.Ordinal) + 1)..].ToLowerInvariant();

    private static string AttributeKey(XElement attribute) =>
        (((string?)attribute.Element("LogicalName")) ?? ((string?)attribute.Element("Name")) ?? (string?)attribute.Attribute("PhysicalName") ?? "")
        .Trim().ToLowerInvariant();

    /// <summary>
    /// An <c>Entity</c> carrying only the table's name (from the table's own element when the package has it) and the given
    /// columns, views and forms.
    /// </summary>
    private static XElement Wrapper(SolutionPackage package, string entity, IEnumerable<XElement>? attributes = null,
        IEnumerable<XElement>? views = null, IEnumerable<(string? Type, XElement Form)>? forms = null, IEnumerable<XElement>? charts = null)
    {
        var schema = Source(package, ComponentType.Entity, entity)?.SourceXml is { } table
            ? (string?)table.Element("EntityInfo")?.Element("entity")?.Attribute("Name") ?? ((string?)table.Element("Name"))?.Trim() ?? entity
            : package.Entities.FirstOrDefault(e => e.LogicalName == entity)?.SchemaName ?? entity;
        var parts = new EntityParts(schema);
        foreach (var attribute in attributes ?? []) parts.Attributes.TryAdd(AttributeKey(attribute), attribute);
        parts.Views.AddRange(views ?? []);
        parts.Forms.AddRange(forms ?? []);
        parts.Charts.AddRange(charts ?? []);
        return parts.Build();
    }

    /// <summary>
    /// The columns the reader reads together with <paramref name="attribute"/>: a money column and its generated <c>_base</c>,
    /// a file or image column and its companions (<c>AttributeOf</c>), either way round. They are not components of their own
    /// (the server generates them), but the reader marks them only next to their main column.
    /// </summary>
    private static IEnumerable<string> Companions(SolutionPackage package, string entity, string attribute)
    {
        var attributes = package.Entities.FirstOrDefault(e => e.LogicalName == entity)?.Attributes ?? [];
        var self = attributes.FirstOrDefault(a => a.LogicalName == attribute);
        foreach (var other in attributes.Where(a => a.LogicalName != attribute))
        {
            if (self is MoneyAttributeMetadata && other is MoneyAttributeMetadata
                && (other.LogicalName == attribute + TypeMapping.BaseColumnSuffix || attribute == other.LogicalName + TypeMapping.BaseColumnSuffix))
                yield return other.LogicalName;
            else if (other.AttributeOf == attribute || (self?.AttributeOf is { } main && (other.LogicalName == main || other.AttributeOf == main)))
                yield return other.LogicalName;
        }
    }

    /// <summary>The one-to-many relationships of the lookup <paramref name="entity"/>.<paramref name="attribute"/> in the package.</summary>
    private static List<XElement> LookupRelationships(SolutionPackage package, string entity, string attribute) =>
        package.Relationships.OfType<OneToManyRelationshipMetadata>()
            .Where(r => r.ReferencingEntity == entity && r.ReferencingAttribute == attribute)
            .Select(r => Source(package, ComponentType.EntityRelationship, r.SchemaName))
            .Where(s => s is not null)
            .Select(s => new XElement(s!.SourceXml))
            .ToList();

    /// <summary>What <see cref="ToPackage"/> gathers of one table.</summary>
    private sealed class EntityParts(string schema)
    {
        public XElement? Shell { get; set; }
        public Dictionary<string, XElement> Attributes { get; } = new(StringComparer.Ordinal);
        public List<XElement> Views { get; } = [];
        public List<(string? Type, XElement Form)> Forms { get; } = [];
        public List<XElement> Charts { get; } = [];

        public XElement Build()
        {
            var entity = Shell is not null
                ? new XElement(Shell)
                : new XElement("Entity", new XElement("Name", schema), new XElement("EntityInfo", new XElement("entity", new XAttribute("Name", schema))));
            var info = entity.Element("EntityInfo")!.Element("entity")!;
            if (info.Element("attributes") is not { } attributes) info.Add(attributes = new XElement("attributes"));
            attributes.RemoveNodes();
            attributes.Add(Attributes.Values.Select(a => new XElement(a)));
            entity.Element("SavedQueries")?.Remove();
            entity.Element("Visualizations")?.Remove();
            entity.Element("FormXml")?.Remove();
            if (Views.Count > 0) entity.Add(new XElement("SavedQueries", new XElement("savedqueries", Views.Select(v => new XElement(v)))));
            if (Forms.Count > 0)
                entity.Add(new XElement("FormXml", Forms.GroupBy(f => f.Type ?? "", StringComparer.Ordinal).Select(g =>
                    new XElement("forms", g.Key.Length == 0 ? null : new XAttribute("type", g.Key), g.Select(f => new XElement(f.Form))))));
            if (Charts.Count > 0) entity.Add(new XElement("Visualizations", Charts.Select(c => new XElement(c))));
            return entity;
        }
    }
}
