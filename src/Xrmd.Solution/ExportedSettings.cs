using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Solution;

/// <summary>
/// The table and column settings xrmverse does not model (notes, search flags, mobile settings, ...) as the
/// export writes them, put on the copies the metadata reads hand out (RetrieveEntity, RetrieveAllEntities,
/// RetrieveAttribute, Web API <c>EntityDefinitions</c>), so that a read and the export never show different values.
/// </summary>
/// <remarks>
/// <para>The values come from the component's kept XML (<see cref="UnmodelledXml"/>: what its solution file gave it) or,
/// with nothing kept (created in xrmverse), from the defaults the export writes. A setting is shown on the SDK property of
/// its element's name; an element the SDK has no property for is not shown (there is nothing to differ). A flag is <c>1</c>/<c>0</c> (or
/// <c>True</c>/<c>False</c>) on a <see cref="bool"/> or the value of a <see cref="BooleanManagedProperty"/>; an empty
/// element is null.</para>
/// <para>The store's definitions are not changed: the updates compare a request with the definition as the read shows it
/// (a retrieved definition sent back unchanged passes; see <see cref="MetadataRequestRules"/>).</para>
/// </remarks>
public static class ExportedSettings
{
    private static readonly Dictionary<string, string> NoRenames = new(StringComparer.Ordinal);

    /// <summary>A copy of <paramref name="entity"/> with its settings (and, with <paramref name="attributes"/>, its attributes' settings).</summary>
    /// <param name="attributes">Fill the copy's attributes too; false leaves the copy without attributes.</param>
    /// <exception cref="XrmdFaultException">500 Unexpected for a kept setting that cannot be shown on its property (never dropped silently).</exception>
    public static EntityMetadata Entity(IMetadataStore store, EntityMetadata entity, bool attributes = true)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entity);
        var component = $"entity '{entity.LogicalName}'";
        var (type, key) = UnmodelledXml.EntityKey(entity.LogicalName);
        var kept = SolutionExporter.Kept(store.GetUnmodelledXml(type, key), "entity", component);
        var copy = Fill(entity, typeof(EntityMetadata), Settings(kept, UnmodelledXml.EntityShape, new XElement("entity")), NoRenames, component);
        // Server-owned (ServerOwnedProperties): custom unless the table is one of the seeded system tables.
        SdkProperty.Set(copy, nameof(EntityMetadata.IsCustomEntity), !MetadataStore.IsSystemEntity(entity));
        copy.IsAuditEnabled ??= AuditDefaults.ForEntity(entity.LogicalName, MetadataStore.IsSystemEntity(entity)); // before the startup upgrade has run
        copy.AutoCreateAccessTeams ??= false; // modelled (plan 2.16): a table without the setting has none
        copy.ChangeTrackingEnabled ??= false; // modelled (plan 2.13): a table without the setting is not tracked
        // Modelled (plan 2.11 A4, A5): a table without the settings has neither notes nor activities; ActivityTypeMask is an
        // activity's own value, set at creation (null for any other table).
        copy.HasNotes ??= false;
        copy.HasActivities ??= false;
        copy.ActivityTypeMask = entity.IsActivity == true ? entity.ActivityTypeMask : null;
        copy.IsDuplicateDetectionEnabled ??= new BooleanManagedProperty(false); // modelled (plan 2.15): a table without the setting has none
        // Modelled (plan 4.1a): a table without the setting is not queue-enabled; the read shows the stored shape.
        copy.IsValidForQueue = QueueSchema.SettingFlag(entity.IsValidForQueue?.Value == true);
        copy.CanChangeTrackingBeEnabled = new BooleanManagedProperty(ChangeTrackingSchema.CanBeEnabled(entity)); // computed, never stored
        copy.TableType = ElasticTables.NormalizeTableType(entity.TableType); // a table stored without a type is Standard
        if (ElasticTables.IsElastic(entity)) SdkProperty.Set(copy, nameof(EntityMetadata.Keys), new[] { ElasticTables.NoSqlKey(entity) }); // computed, never stored
        copy.SetAttributes(attributes ? (entity.Attributes ?? []).Select(a => Attribute(store, entity.LogicalName, a)).ToArray() : []);
        return copy;
    }

    /// <summary>A copy of the attribute <paramref name="attribute"/> of <paramref name="entity"/> with its settings.</summary>
    /// <exception cref="XrmdFaultException">500 Unexpected for a kept setting that cannot be shown on its property.</exception>
    public static AttributeMetadata Attribute(IMetadataStore store, string entity, AttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(attribute);
        var component = $"attribute '{entity}.{attribute.LogicalName}'";
        var (type, key) = UnmodelledXml.AttributeKey(entity, attribute.LogicalName);
        var kept = SolutionExporter.Kept(store.GetUnmodelledXml(type, key), "attribute", component);
        var copy = Fill(attribute, typeof(AttributeMetadata), Settings(kept, UnmodelledXml.AttributeShape, Written(attribute)), NoRenames, component);
        copy.IsAuditEnabled ??= AuditDefaults.ForAttribute(attribute, store.GetEntity(entity)?.PrimaryIdAttribute);
        copy.IsSecured ??= false; // a column stored before IsSecured was applied
        // Server-owned (ServerOwnedProperties): the Dataverse IsCustomAttribute, see MetadataStore.IsCustomAttribute; the
        // CanBeSecuredFor* flags, computed here from the rule and never from a stored value.
        if (store.GetEntity(entity) is { } owner)
        {
            SdkProperty.Set(copy, nameof(AttributeMetadata.IsCustomAttribute), MetadataStore.IsCustomAttribute(owner, attribute));
            FieldSecurability.Apply(owner, copy);
        }
        return copy;
    }

    /// <summary>
    /// The settings the export writes for a column of <paramref name="attribute"/>'s type created in xrmverse, by SDK
    /// property (only those the SDK type has).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> AttributeDefaults(AttributeMetadata attribute) =>
        Settings(null, UnmodelledXml.AttributeShape, Written(attribute))
            .Where(s => attribute.GetType().GetProperty(s.Name, BindingFlags.Public | BindingFlags.Instance) is not null)
            .ToDictionary(s => s.Name, s => s.Value, StringComparer.Ordinal);

    /// <summary>What the attribute defaults depend on: the type (<c>datetime</c>, <c>lookup</c>).</summary>
    private static XElement Written(AttributeMetadata attribute) => new("attribute", new XElement("Type", attribute switch
    {
        DateTimeAttributeMetadata => "datetime",
        LookupAttributeMetadata => "lookup",
        _ => "",
    }));

    /// <summary>The leaf settings of the kept XML (or the shape's defaults) that xrmverse does not model: (element, value).</summary>
    private static List<(string Name, string Value)> Settings(XElement? kept, UnmodelledShape shape, XElement written) =>
        ((kept ?? shape.Defaults?.Invoke(written))?.Elements() ?? [])
        .Where(e => !UnmodelledXml.IsMarker(e) && !e.HasElements && !shape.Children.Contains(e.Name.LocalName))
        .Select(e => (e.Name.LocalName, e.Value))
        .ToList();

    private static T Fill<T>(T value, Type declared, List<(string Name, string Value)> settings, Dictionary<string, string> renames, string component)
        where T : class
    {
        var node = JsonSerializer.SerializeToNode(value, declared, MetadataSerializer.Options)!.AsObject();
        var shown = new List<(PropertyInfo Property, JsonNode? Value, string Element)>();
        foreach (var (element, text) in settings)
        {
            var property = value.GetType().GetProperty(renames.GetValueOrDefault(element, element), BindingFlags.Public | BindingFlags.Instance);
            if (property is null) continue; // the SDK shows no such setting: nothing to differ
            var json = Json(text, property.PropertyType)
                       ?? throw Unshowable(component, element, text, property);
            node[property.Name] = json.Value;
            shown.Add((property, json.Value, element));
        }
        var copy = (T)JsonSerializer.Deserialize(node, declared, MetadataSerializer.Options)!;
        foreach (var (property, expected, element) in shown)
        {
            var actual = property.GetValue(copy);
            var actualJson = actual is null ? null : JsonSerializer.SerializeToNode(actual, property.PropertyType, MetadataSerializer.Options);
            if (!JsonNode.DeepEquals(actualJson, expected)) throw Unshowable(component, element, expected?.ToJsonString() ?? "", property);
        }
        return copy;
    }

    /// <summary>The JSON of <paramref name="text"/> as <paramref name="type"/>; null when it is not one (Value null: the property is null).</summary>
    private static Shown? Json(string text, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string)) return new Shown(JsonValue.Create(text));
        if (text.Length == 0) return new Shown(null);
        if (underlying == typeof(bool)) return Flag(text) is { } flag ? new Shown(JsonValue.Create(flag)) : null;
        if (underlying == typeof(BooleanManagedProperty))
            return Flag(text) is { } managed
                ? new Shown(JsonSerializer.SerializeToNode(new BooleanManagedProperty(managed), MetadataSerializer.Options))
                : null;
        if (underlying == typeof(int))
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? new Shown(JsonValue.Create(number)) : null;
        if (underlying.IsEnum && Enum.TryParse(underlying, text, ignoreCase: true, out var member) && Enum.IsDefined(underlying, member!))
            return new Shown(JsonSerializer.SerializeToNode(member, underlying, MetadataSerializer.Options));
        return null;
    }

    /// <summary>A JSON value to show (null: the property is null).</summary>
    private readonly record struct Shown(JsonNode? Value);

    private static bool? Flag(string text) => text switch
    {
        "1" => true,
        "0" => false,
        _ when bool.TryParse(text, out var flag) => flag,
        _ => null,
    };

    private static XrmdFaultException Unshowable(string component, string element, string text, PropertyInfo property) =>
        new(ErrorCodes.Unexpected,
            $"The setting {element} = '{text}' of {component} cannot be shown on its property {property.Name} ({property.PropertyType.Name}).",
            500);
}
