using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The SDK twins of <see cref="EntityMetadataExtensions"/> and the server-side setters for <see cref="EntityMetadata"/> members the
/// SDK exposes read-only (S.2b2; removed with the SDK view, S.2i). The setters change their argument in place and keep their SDK body
/// (ruling R3); on the model the properties are plain setters.
/// </summary>
public static partial class EntityMetadataExtensions
{
    public static void SetAttributes(this EntityMetadata entity, IEnumerable<AttributeMetadata> attributes) =>
        SdkProperty.Set(entity, nameof(EntityMetadata.Attributes), attributes.ToArray());

    /// <summary>
    /// Sets the relationships of <paramref name="entity"/>: copies of the one-to-many relationships of
    /// <paramref name="oneToMany"/> (every one, as <see cref="OneToManyRelationships"/> gives them) it is the referencing
    /// entity of (<c>ManyToOneRelationships</c>) or the referenced entity of (<c>OneToManyRelationships</c>), and of the
    /// saved many-to-many relationships of <paramref name="manyToMany"/> it is a side of.
    /// </summary>
    public static void SetRelationships(this EntityMetadata entity, IEnumerable<OneToManyRelationshipMetadata> oneToMany,
        IEnumerable<ManyToManyRelationshipMetadata> manyToMany)
    {
        var all = oneToMany.ToList();
        var manyToOne = all.Where(r => r.ReferencingEntity == entity.LogicalName).Select(MetadataSerializer.Clone).ToArray();
        var referenced = all.Where(r => r.ReferencedEntity == entity.LogicalName).Select(MetadataSerializer.Clone).ToArray();
        var related = manyToMany
            .Where(r => r.Entity1LogicalName == entity.LogicalName || r.Entity2LogicalName == entity.LogicalName)
            .Select(MetadataSerializer.Clone)
            .ToArray();
        SdkProperty.Set(entity, nameof(EntityMetadata.ManyToOneRelationships), manyToOne);
        SdkProperty.Set(entity, nameof(EntityMetadata.OneToManyRelationships), referenced);
        SdkProperty.Set(entity, nameof(EntityMetadata.ManyToManyRelationships), related);
    }

    /// <summary>The SDK twin of <see cref="ReferencingNavigationName(TableDefinition, LookupColumn, IEnumerable{OneToManyRelationship}?)"/>.</summary>
    public static string ReferencingNavigationName(EntityMetadata entity, LookupAttributeMetadata lookup,
        IEnumerable<OneToManyRelationshipMetadata>? relationships = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(lookup);
        return ReferencingNavigationName(SdkTwin.Model(entity), (LookupColumn)SdkTwin.Model(lookup), relationships?.Select(SdkTwin.Model));
    }

    /// <summary>The SDK twin of <see cref="CustomerNavigations(TableDefinition, LookupColumn, IEnumerable{OneToManyRelationship}?)"/>.</summary>
    public static IEnumerable<(string Name, string Target)> CustomerNavigations(EntityMetadata entity, LookupAttributeMetadata lookup,
        IEnumerable<OneToManyRelationshipMetadata>? relationships = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(lookup);
        return CustomerNavigations(SdkTwin.Model(entity), (LookupColumn)SdkTwin.Model(lookup), relationships?.Select(SdkTwin.Model));
    }
}
