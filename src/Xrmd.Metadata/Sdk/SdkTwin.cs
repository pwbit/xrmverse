using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata.Sdk;

/// <summary>
/// Conversions for the SDK twins of the model helpers (S.2b2, Task 4): an SDK argument becomes its model (a view built by
/// <see cref="SdkViewCache"/> is looked up, any other object is converted afresh and not cached, as its owner may change it), and
/// a model result becomes a fresh SDK object (the caller owns it). Transitional: removed with the SDK view (S.2i).
/// </summary>
internal static class SdkTwin
{
    public static TableDefinition Model(EntityMetadata entity) =>
        SdkViewCache.ModelOfView(entity) as TableDefinition ?? SdkMetadataBridge.ToModel(entity);

    public static ColumnDefinition Model(AttributeMetadata attribute) =>
        SdkViewCache.ModelOfView(attribute) as ColumnDefinition ?? SdkMetadataBridge.ToModel(attribute);

    public static ChoiceSetBase Model(OptionSetMetadataBase optionSet) =>
        SdkViewCache.ModelOfView(optionSet) as ChoiceSetBase ?? SdkMetadataBridge.ToModel(optionSet);

    public static RelationshipDefinition Model(RelationshipMetadataBase relationship) =>
        SdkViewCache.ModelOfView(relationship) as RelationshipDefinition ?? SdkMetadataBridge.ToModel(relationship);

    public static OneToManyRelationship Model(OneToManyRelationshipMetadata relationship) =>
        (OneToManyRelationship)Model((RelationshipMetadataBase)relationship);

    public static ColumnDefinition? ModelOrNull(AttributeMetadata? attribute) => attribute is null ? null : Model(attribute);

    public static TableDefinition? ModelOrNull(EntityMetadata? entity) => entity is null ? null : Model(entity);

    public static Func<string, TableDefinition?> Model(Func<string, EntityMetadata?> entities) =>
        name => entities(name) is { } e ? Model(e) : null;

    /// <summary>
    /// The models of the tables <paramref name="entities"/> finds, each SDK instance converted once for the life of the returned
    /// function (so a call sees one model per table); <paramref name="known"/> pairs already converted.
    /// </summary>
    public static Func<string, TableDefinition?> Memo(Func<string, EntityMetadata?> entities, params (EntityMetadata Sdk, TableDefinition Model)[] known)
    {
        var converted = new Dictionary<EntityMetadata, TableDefinition>(ReferenceEqualityComparer.Instance);
        foreach (var (sdk, model) in known) converted[sdk] = model;
        return name => entities(name) is { } e ? converted.TryGetValue(e, out var m) ? m : converted[e] = Model(e) : null;
    }

    /// <summary>
    /// A fresh model of <paramref name="entity"/> (never a cached one: a twin may change it) and the model of
    /// <paramref name="attribute"/>: the matching column of that table when the attribute is one of the entity's, else its own.
    /// </summary>
    public static (TableDefinition Table, ColumnDefinition Column) FreshModel(EntityMetadata entity, AttributeMetadata attribute)
    {
        var table = SdkMetadataBridge.ToModel(entity);
        var index = entity.Attributes is { } columns ? Array.IndexOf(columns, attribute) : -1;
        return (table, index >= 0 && table.Attributes is { } list && index < list.Count ? list[index] : SdkMetadataBridge.ToModel(attribute));
    }

    public static Func<string, OneToManyRelationship?> Model(Func<string, OneToManyRelationshipMetadata?> relationships) =>
        name => relationships(name) is { } r ? Model(r) : null;

    public static IEnumerable<TableDefinition> Model(IEnumerable<EntityMetadata> entities) => entities.Select(Model);

    public static IEnumerable<ColumnDefinition> Model(IEnumerable<AttributeMetadata> attributes) => attributes.Select(Model);

    public static LabelSet? Model(Label? label) =>
        label is null ? null : MetadataJsonOf<LabelSet>(System.Text.Json.JsonSerializer.Serialize(label, MetadataSerializer.Options));

    public static ManagedFlag? Model(BooleanManagedProperty? flag) =>
        flag is null ? null : MetadataJsonOf<ManagedFlag>(System.Text.Json.JsonSerializer.Serialize(flag, MetadataSerializer.Options));

    public static EntityMetadata Sdk(TableDefinition table) => SdkMetadataBridge.ToSdk(table);

    public static AttributeMetadata Sdk(ColumnDefinition column) => SdkMetadataBridge.ToSdk(column);

    public static OptionSetMetadataBase Sdk(ChoiceSetBase choiceSet) => SdkMetadataBridge.ToSdk(choiceSet);

    public static RelationshipMetadataBase Sdk(RelationshipDefinition relationship) => SdkMetadataBridge.ToSdk(relationship);

    public static OneToManyRelationshipMetadata Sdk(OneToManyRelationship relationship) =>
        (OneToManyRelationshipMetadata)SdkMetadataBridge.ToSdk(relationship);

    public static ManyToManyRelationshipMetadata Sdk(ManyToManyRelationship relationship) =>
        (ManyToManyRelationshipMetadata)SdkMetadataBridge.ToSdk(relationship);

    public static TSdk Sdk<TSdk>(ColumnDefinition column) where TSdk : AttributeMetadata => (TSdk)SdkMetadataBridge.ToSdk(column);

    public static Label Sdk(LabelSet label) =>
        System.Text.Json.JsonSerializer.Deserialize<Label>(System.Text.Json.JsonSerializer.Serialize(label, Xrmd.Model.Metadata.MetadataJson.Options),
            MetadataSerializer.Options)!;

    public static BooleanManagedProperty Sdk(ManagedFlag flag) =>
        System.Text.Json.JsonSerializer.Deserialize<BooleanManagedProperty>(
            System.Text.Json.JsonSerializer.Serialize(flag, Xrmd.Model.Metadata.MetadataJson.Options), MetadataSerializer.Options)!;

    public static CascadeConfiguration Sdk(CascadeSettings cascade) =>
        System.Text.Json.JsonSerializer.Deserialize<CascadeConfiguration>(
            System.Text.Json.JsonSerializer.Serialize(cascade, Xrmd.Model.Metadata.MetadataJson.Options), MetadataSerializer.Options)!;

    public static EntityKeyMetadata Sdk(TableKey key) =>
        System.Text.Json.JsonSerializer.Deserialize<EntityKeyMetadata>(
            System.Text.Json.JsonSerializer.Serialize(key, Xrmd.Model.Metadata.MetadataJson.Options), MetadataSerializer.Options)!;

    public static IReadOnlyList<TSdk> Sdk<TSdk>(IEnumerable<ColumnDefinition> columns) where TSdk : AttributeMetadata =>
        columns.Select(c => (TSdk)Sdk(c)).ToList();

    /// <summary>
    /// Copies every property the serializer writes from <paramref name="changed"/> (the model a helper changed) into
    /// <paramref name="target"/>, the SDK object the caller passed: a twin of a helper that changes its argument in place.
    /// </summary>
    public static void CopyInto(AttributeMetadata target, ColumnDefinition changed) => CopyProperties(target, Sdk(changed));

    /// <summary>Same as <see cref="CopyInto(AttributeMetadata, ColumnDefinition)"/> for a table.</summary>
    public static void CopyInto(EntityMetadata target, TableDefinition changed) => CopyProperties(target, Sdk(changed));

    private static void CopyProperties(object target, object source)
    {
        if (target.GetType() != source.GetType())
            throw new InvalidOperationException($"Cannot copy a {source.GetType().Name} into a {target.GetType().Name}.");
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.PropertyType == typeof(System.Runtime.Serialization.ExtensionDataObject))
                continue;
            var setter = property.SetMethod is { IsPublic: true } ? property.SetMethod : SdkProperty.TryGetSetter(property);
            if (setter is null) continue;
            setter.Invoke(target, [property.GetValue(source)]);
        }
    }

    /// <summary>The model of a small SDK value object (a label, a cascade configuration) through its JSON.</summary>
    public static T ModelJson<T>(object sdk) where T : class =>
        MetadataJsonOf<T>(System.Text.Json.JsonSerializer.Serialize(sdk, sdk.GetType(), MetadataSerializer.Options));

    private static T MetadataJsonOf<T>(string json) where T : class =>
        System.Text.Json.JsonSerializer.Deserialize<T>(json, Xrmd.Model.Metadata.MetadataJson.Options)!;
}
