using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Xrmd.Model.Metadata;

/// <summary>
/// JSON (de)serialization of the own metadata model in the Dataverse Web API shape, byte for byte the JSON the SDK-based
/// serializer writes (the golden files pin it): PascalCase property names, enums as strings, null properties omitted, and
/// subtypes of <see cref="ColumnDefinition"/>, <see cref="ChoiceOption"/>, <see cref="ChoiceSetBase"/> and
/// <see cref="RelationshipDefinition"/> discriminated by <c>"@odata.type":"Microsoft.Dynamics.CRM.&lt;WireType&gt;"</c>.
/// </summary>
/// <remarks>
/// Property order is the declaration order of the model types, derived members before inherited ones (System.Text.Json walks
/// from the runtime type up to its bases). The discriminator is written first; the reader also takes it elsewhere
/// (<c>AllowOutOfOrderMetadataProperties</c>), as a <c>jsonb</c> column orders keys its own way. An unknown runtime type is
/// written as its nearest known ancestor; an unknown discriminator fails to read with a <see cref="JsonException"/>, as
/// MetadataSerializer does.
/// </remarks>
public static class MetadataJson
{
    public const string ODataTypePrefix = "Microsoft.Dynamics.CRM.";
    public const string ODataTypeProperty = "@odata.type";

    private static readonly Assembly ModelAssembly = typeof(TableDefinition).Assembly;

    private static readonly Type[] PolymorphicRoots =
        [typeof(ColumnDefinition), typeof(ChoiceOption), typeof(ChoiceSetBase), typeof(RelationshipDefinition)];

    /// <summary>Shared, read-only options.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(TableDefinition table) => JsonSerializer.Serialize(table, Options);

    public static TableDefinition DeserializeTable(string json) => Deserialize<TableDefinition>(json);

    /// <summary>Serializes as <see cref="ColumnDefinition"/> so the <c>@odata.type</c> discriminator is written.</summary>
    public static string SerializeColumn(ColumnDefinition column) => JsonSerializer.Serialize(column, Options);

    public static ColumnDefinition DeserializeColumn(string json) => Deserialize<ColumnDefinition>(json);

    /// <summary>Serializes as <see cref="ChoiceSetBase"/> so the <c>@odata.type</c> discriminator is written.</summary>
    public static string SerializeChoiceSet(ChoiceSetBase choiceSet) => JsonSerializer.Serialize(choiceSet, Options);

    public static ChoiceSetBase DeserializeChoiceSet(string json) => Deserialize<ChoiceSetBase>(json);

    /// <summary>Serializes as <see cref="RelationshipDefinition"/> so the <c>@odata.type</c> discriminator is written.</summary>
    public static string SerializeRelationship(RelationshipDefinition relationship) => JsonSerializer.Serialize(relationship, Options);

    public static RelationshipDefinition DeserializeRelationship(string json) => Deserialize<RelationshipDefinition>(json);

    /// <summary>Deep copy through JSON, keeping the runtime type of polymorphic members.</summary>
    public static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    private static T Deserialize<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"JSON 'null' is not a {typeof(T).Name}.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
            Converters = { new JsonStringEnumConverter() },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ConfigurePolymorphism } },
        };
        options.MakeReadOnly();
        return options;
    }

    private static void ConfigurePolymorphism(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || Array.IndexOf(PolymorphicRoots, typeInfo.Type) < 0) return;

        var polymorphism = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = ODataTypeProperty,
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor,
        };
        foreach (var (derived, wire) in ModelAssembly.GetTypes()
                     .Where(t => t.IsPublic && !t.IsAbstract && t != typeInfo.Type && typeInfo.Type.IsAssignableFrom(t))
                     .Select(t => (Type: t, Wire: WireName(t)))
                     .OrderBy(d => d.Wire, StringComparer.Ordinal))
        {
            polymorphism.DerivedTypes.Add(new JsonDerivedType(derived, ODataTypePrefix + wire));
        }
        typeInfo.PolymorphismOptions = polymorphism;
    }

    private static string WireName(Type type) =>
        type.GetCustomAttribute<WireTypeAttribute>()?.Name
        ?? throw new InvalidOperationException($"{type.Name} derives from a polymorphic metadata type but has no [WireType].");
}
