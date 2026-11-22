using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// Metadata properties the server assigns, ignored when a client creates a component (Web API body or SDK request): a
/// client value could collide, bypass the rules on system components or change how the table is secured
/// (<c>IsIntersect</c> makes a table readable by every member, without privileges). The <c>CanBeSecuredFor*</c> flags of a
/// column are computed by every read (<see cref="FieldSecurability"/>), and so is <c>CanChangeTrackingBeEnabled</c> of a table
/// (<see cref="ChangeTrackingSchema"/>).
/// </summary>
public static class ServerOwnedProperties
{
    /// <summary>The server-owned properties of <see cref="EntityMetadata"/>.</summary>
    public static IReadOnlyList<string> Entity { get; } =
    [
        nameof(EntityMetadata.MetadataId), nameof(EntityMetadata.ObjectTypeCode), nameof(EntityMetadata.IsIntersect),
        nameof(EntityMetadata.IsManaged), nameof(EntityMetadata.IsCustomEntity), nameof(EntityMetadata.IsLogicalEntity),
        nameof(EntityMetadata.IsPrivate), nameof(EntityMetadata.IntroducedVersion), nameof(EntityMetadata.CanChangeTrackingBeEnabled),
    ];

    /// <summary>The server-owned properties of <see cref="AttributeMetadata"/>.</summary>
    public static IReadOnlyList<string> Attribute { get; } =
    [
        nameof(AttributeMetadata.MetadataId), nameof(AttributeMetadata.IsManaged), nameof(AttributeMetadata.IsCustomAttribute),
        nameof(AttributeMetadata.IsLogical), nameof(AttributeMetadata.AttributeOf), nameof(AttributeMetadata.IntroducedVersion),
        nameof(AttributeMetadata.CanBeSecuredForCreate), nameof(AttributeMetadata.CanBeSecuredForRead), nameof(AttributeMetadata.CanBeSecuredForUpdate),
    ];

    /// <summary>Sets the server-owned properties of <paramref name="entity"/> and of its attributes to null.</summary>
    public static void Clear(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        foreach (var name in Entity) SdkProperty.Set(entity, name, null);
        foreach (var attribute in entity.Attributes ?? []) Clear(attribute);
    }

    /// <summary>Sets the server-owned properties of <paramref name="attribute"/> to null.</summary>
    public static void Clear(AttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        foreach (var name in Attribute) SdkProperty.Set(attribute, name, null);
        // The SDK declares SourceTypeMask only on the column types that can be calculated (plan 2.12a, ruling K12).
        if (attribute.GetType().GetProperty(SourceTypeMask) is not null) SdkProperty.Set(attribute, SourceTypeMask, null);
    }

    /// <summary>The server-owned <c>SourceTypeMask</c> of a column that can be calculated; the rules set it from the definition.</summary>
    public const string SourceTypeMask = nameof(SourceTypeMask);
}
