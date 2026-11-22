using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// JSON (de)serialization of SDK metadata classes in the Dataverse Web API shape:
/// PascalCase property names as in <c>EntityDefinitions</c>, enums as strings (e.g. <c>"AttributeType":"String"</c>,
/// <c>"OwnershipType":"UserOwned"</c>), null properties omitted, <c>Label</c> as
/// <c>{"LocalizedLabels":[{"Label","LanguageCode"}],"UserLocalizedLabel":...}</c>, and subtypes of
/// <see cref="AttributeMetadata"/> (also <see cref="OptionMetadata"/>, <see cref="OptionSetMetadataBase"/>,
/// <see cref="RelationshipMetadataBase"/>)
/// discriminated by <c>"@odata.type":"Microsoft.Dynamics.CRM.XxxAttributeMetadata"</c>.
/// Internal/private SDK setters are populated via reflection so that server-owned values round-trip.
/// </summary>
/// <remarks>
/// System.Text.Json (.NET 8 and later) requires <c>@odata.type</c> to be the first property of a polymorphic object
/// when deserializing; the serializer always writes it first. The reader also takes it elsewhere
/// (<c>AllowOutOfOrderMetadataProperties</c>, plan 2.7a, Task 6): a definition edited through PostgreSQL's <c>jsonb</c>, which
/// orders keys its own way, still reads.
/// </remarks>
public static class MetadataSerializer
{
    public const string ODataTypePrefix = "Microsoft.Dynamics.CRM.";
    public const string ODataTypeProperty = "@odata.type";

    private static readonly Assembly SdkAssembly = typeof(EntityMetadata).Assembly;

    private static readonly Type[] PolymorphicRoots =
        [typeof(AttributeMetadata), typeof(OptionMetadata), typeof(OptionSetMetadataBase), typeof(RelationshipMetadataBase)];

    /// <summary>Shared, read-only options (reusable by the Web API layer).</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(EntityMetadata entity) => JsonSerializer.Serialize(entity, Options);

    public static EntityMetadata Deserialize(string json) =>
        JsonSerializer.Deserialize<EntityMetadata>(json, Options)
        ?? throw new JsonException("JSON 'null' is not an EntityMetadata.");

    /// <summary>Serializes as <see cref="AttributeMetadata"/> so the <c>@odata.type</c> discriminator is written.</summary>
    public static string SerializeAttribute(AttributeMetadata attribute) =>
        JsonSerializer.Serialize(attribute, Options);

    public static AttributeMetadata DeserializeAttribute(string json) =>
        JsonSerializer.Deserialize<AttributeMetadata>(json, Options)
        ?? throw new JsonException("JSON 'null' is not an AttributeMetadata.");

    /// <summary>Serializes as <see cref="OptionSetMetadataBase"/> so the <c>@odata.type</c> discriminator is written.</summary>
    public static string SerializeOptionSet(OptionSetMetadataBase optionSet) =>
        JsonSerializer.Serialize(optionSet, Options);

    public static OptionSetMetadataBase DeserializeOptionSet(string json) =>
        JsonSerializer.Deserialize<OptionSetMetadataBase>(json, Options)
        ?? throw new JsonException("JSON 'null' is not an OptionSetMetadataBase.");

    /// <summary>Serializes as <see cref="RelationshipMetadataBase"/> so the <c>@odata.type</c> discriminator is written.</summary>
    public static string SerializeRelationship(RelationshipMetadataBase relationship) =>
        JsonSerializer.Serialize(relationship, Options);

    public static RelationshipMetadataBase DeserializeRelationship(string json) =>
        JsonSerializer.Deserialize<RelationshipMetadataBase>(json, Options)
        ?? throw new JsonException("JSON 'null' is not a RelationshipMetadataBase.");

    /// <summary>Deep copy through JSON, keeping the runtime type of polymorphic members.</summary>
    public static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
            Converters = { new JsonStringEnumConverter() },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ConfigureSdkType } },
        };
        options.MakeReadOnly();
        return options;
    }

    private static void ConfigureSdkType(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Type.Assembly != SdkAssembly) return;

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            var property = typeInfo.Properties[i];
            if (property.PropertyType == typeof(ExtensionDataObject))
            {
                typeInfo.Properties.RemoveAt(i);
                continue;
            }

            if (property.Set is null
                && property.AttributeProvider is PropertyInfo info
                && SdkProperty.TryGetSetter(info) is { } setter)
            {
                property.Set = (target, value) => setter.Invoke(target, [value]);
            }
        }

        if (Array.IndexOf(PolymorphicRoots, typeInfo.Type) >= 0)
        {
            var polymorphism = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = ODataTypeProperty,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor,
            };
            foreach (var derived in SdkAssembly.GetTypes()
                         .Where(t => t.IsPublic && !t.IsAbstract && t != typeInfo.Type && typeInfo.Type.IsAssignableFrom(t))
                         .OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                polymorphism.DerivedTypes.Add(new JsonDerivedType(derived, ODataTypePrefix + derived.Name));
            }
            typeInfo.PolymorphismOptions = polymorphism;
        }
    }
}
