using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata.Computed;

/// <summary>
/// Model overloads of the translators (<see cref="IComputedColumns"/>, <see cref="IRollupColumns"/>), which are implemented outside
/// this project on SDK types (S.2b2, Task 4). <c>Translate</c> and <c>Check</c> take tables that may still change (a table being
/// defined, a cache being loaded): every table is converted afresh for the call, once per call, and never gets a cached view.
/// <c>TranslatePublished</c> takes tables of a metadata snapshot, which are never changed once put in it, so they are passed as
/// their cached views. Transitional: removed when the translators take the model (S.2c, S.2h).
/// </summary>
internal static class SdkComputedCalls
{
    public static ComputedDefinition Translate(this IComputedColumns computed, TableDefinition entity, ColumnDefinition attribute,
        Func<string, TableDefinition?> entities)
    {
        var (sdkEntity, sdkAttribute, sdkEntities) = Fresh(entity, attribute, entities);
        return computed.Translate(sdkEntity, sdkAttribute, sdkEntities);
    }

    public static ComputedDefinition Check(this IComputedColumns computed, TableDefinition entity, ColumnDefinition attribute,
        Func<string, TableDefinition?> entities)
    {
        var (sdkEntity, sdkAttribute, sdkEntities) = Fresh(entity, attribute, entities);
        return computed.Check(sdkEntity, sdkAttribute, sdkEntities);
    }

    public static ComputedDefinition TranslatePublished(this IComputedColumns computed, TableDefinition entity, ColumnDefinition attribute,
        Func<string, TableDefinition?> entities) =>
        computed.Translate(SdkViewCache.Of(entity), SdkViewCache.Of(entity, attribute), Views(entities));

    public static RollupDefinition Translate(this IRollupColumns rollups, TableDefinition entity, ColumnDefinition attribute,
        Func<string, TableDefinition?> entities, Func<string, OneToManyRelationship?> relationships)
    {
        var (sdkEntity, sdkAttribute, sdkEntities) = Fresh(entity, attribute, entities);
        return rollups.Translate(sdkEntity, sdkAttribute, sdkEntities, Views(relationships));
    }

    public static RollupDefinition TranslatePublished(this IRollupColumns rollups, TableDefinition entity, ColumnDefinition attribute,
        Func<string, TableDefinition?> entities, Func<string, OneToManyRelationship?> relationships) =>
        rollups.Translate(SdkViewCache.Of(entity), SdkViewCache.Of(entity, attribute), Views(entities), Views(relationships));

    private static (EntityMetadata Entity, AttributeMetadata Attribute, Func<string, EntityMetadata?> Entities) Fresh(TableDefinition entity,
        ColumnDefinition attribute, Func<string, TableDefinition?> entities)
    {
        var sdkEntity = SdkMetadataBridge.ToSdk(entity);
        var index = entity.Attributes?.IndexOf(attribute) ?? -1;
        var sdkAttribute = index >= 0 && sdkEntity.Attributes is { } columns && index < columns.Length
            ? columns[index]
            : SdkMetadataBridge.ToSdk(attribute);
        var converted = new Dictionary<TableDefinition, EntityMetadata>(ReferenceEqualityComparer.Instance) { [entity] = sdkEntity };
        return (sdkEntity, sdkAttribute, name => entities(name) is { } table
            ? converted.TryGetValue(table, out var sdk) ? sdk : converted[table] = SdkMetadataBridge.ToSdk(table)
            : null);
    }

    private static Func<string, EntityMetadata?> Views(Func<string, TableDefinition?> entities) =>
        name => entities(name) is { } table ? SdkViewCache.Of(table) : null;

    private static Func<string, OneToManyRelationshipMetadata?> Views(Func<string, OneToManyRelationship?> relationships) =>
        name => relationships(name) is { } relationship ? (OneToManyRelationshipMetadata)SdkViewCache.Of(relationship) : null;
}
