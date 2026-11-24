using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The SDK twins of <see cref="FileColumns"/> (S.2b2; removed with the SDK view, S.2i). The predicates keep their SDK bodies: they
/// read the type and two or three properties and run for every column a query or a write touches, so they do not convert it.
/// </summary>
public static partial class FileColumns
{
    /// <summary>The SDK twin of <see cref="IsAttachmentObjectId(ColumnDefinition)"/>.</summary>
    public static bool IsAttachmentObjectId(AttributeMetadata a) =>
        a is LookupAttributeMetadata && a.LogicalName == AttachmentLookup && a.EntityLogicalName == AttachmentEntity;

    /// <summary>The SDK twin of <see cref="IsFileOrImage(ColumnDefinition)"/>.</summary>
    public static bool IsFileOrImage(AttributeMetadata a) => a is FileAttributeMetadata or ImageAttributeMetadata;

    /// <summary>The SDK twin of <see cref="IsStoredCompanion(ColumnDefinition)"/>.</summary>
    public static bool IsStoredCompanion(AttributeMetadata a) =>
        a.AttributeOf is { } main && a.LinkedAttributeId is not null
        && ((a is StringAttributeMetadata && a.LogicalName == main + "_name")
            || (a is UniqueIdentifierAttributeMetadata && a.LogicalName == main + "id")
            || (a is BigIntAttributeMetadata && a.LogicalName == main + "_timestamp"));

    /// <summary>The SDK twin of <see cref="IsUrlCompanion(ColumnDefinition)"/>.</summary>
    public static bool IsUrlCompanion(AttributeMetadata a) =>
        a is StringAttributeMetadata && a.AttributeOf is { } main && a.LinkedAttributeId is not null && a.LogicalName == main + UrlSuffix;

    /// <summary>The SDK twin of <see cref="IsStoredCompanion(ColumnDefinition, IEnumerable{ColumnDefinition})"/>.</summary>
    public static bool IsStoredCompanion(AttributeMetadata a, IEnumerable<AttributeMetadata> siblings) =>
        IsStoredCompanion(a)
        && siblings.FirstOrDefault(x => x.LogicalName == a.AttributeOf) is { } main
        && IsFileOrImage(main) && main.MetadataId == a.LinkedAttributeId;

    /// <summary>The SDK twin of <see cref="Companions(ColumnDefinition)"/>: fresh SDK companions the caller owns.</summary>
    public static IReadOnlyList<AttributeMetadata> Companions(AttributeMetadata main)
    {
        ArgumentNullException.ThrowIfNull(main);
        return SdkTwin.Sdk<AttributeMetadata>(Companions(SdkTwin.Model(main)));
    }
}
