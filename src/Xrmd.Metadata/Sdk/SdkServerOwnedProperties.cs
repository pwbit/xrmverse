using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The SDK twins of <see cref="ServerOwnedProperties"/> (S.2b2; removed with the SDK view, S.2i). They change their argument in place,
/// so they keep their SDK body (ruling R3).
/// </summary>
public static partial class ServerOwnedProperties
{
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
}
