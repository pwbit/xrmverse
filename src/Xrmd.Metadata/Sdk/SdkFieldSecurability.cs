using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>The SDK twins of <see cref="FieldSecurability"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class FieldSecurability
{
    /// <summary>The SDK twin of <see cref="IsSecurable(TableDefinition, ColumnDefinition)"/>.</summary>
    public static bool IsSecurable(EntityMetadata entity, AttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        return IsSecurable(SdkTwin.Model(entity), SdkTwin.Model(attribute));
    }

    /// <summary>The SDK twin of <see cref="MainColumnOf(TableDefinition, ColumnDefinition)"/>.</summary>
    public static string? MainColumnOf(EntityMetadata entity, AttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        return MainColumnOf(SdkTwin.Model(entity), SdkTwin.Model(attribute));
    }

    /// <summary>
    /// Sets <c>CanBeSecuredForCreate</c>, <c>CanBeSecuredForRead</c> and <c>CanBeSecuredForUpdate</c> of
    /// <paramref name="attribute"/> (a copy a read hands out) from <see cref="IsSecurable"/>.
    /// The SDK twin of <see cref="Apply(TableDefinition, ColumnDefinition)"/>; it changes <paramref name="attribute"/> in place, so it
    /// keeps its SDK body (S.2b2 ruling R3).
    /// </summary>
    public static void Apply(EntityMetadata entity, AttributeMetadata attribute)
    {
        var securable = IsSecurable(entity, attribute);
        SdkProperty.Set(attribute, nameof(AttributeMetadata.CanBeSecuredForCreate), securable);
        SdkProperty.Set(attribute, nameof(AttributeMetadata.CanBeSecuredForRead), securable);
        SdkProperty.Set(attribute, nameof(AttributeMetadata.CanBeSecuredForUpdate), securable);
    }
}
