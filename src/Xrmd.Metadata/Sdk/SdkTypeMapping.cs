using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>The SDK twins of <see cref="TypeMapping"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class TypeMapping
{
    /// <summary>The SDK twin of <see cref="ToSql(ColumnDefinition, ISqlDialect)"/>.</summary>
    public static string ToSql(AttributeMetadata a, ISqlDialect dialect) => ToSql(SdkTwin.Model(a), dialect);

    /// <summary>The SDK twin of <see cref="IsPolymorphicLookup(ColumnDefinition)"/>.</summary>
    public static bool IsPolymorphicLookup(AttributeMetadata a) => a is LookupAttributeMetadata && IsPolymorphicLookup(SdkTwin.Model(a));

    /// <summary>The SDK twin of <see cref="IsStoredByOtherAttribute(ColumnDefinition)"/>.</summary>
    public static bool IsStoredByOtherAttribute(AttributeMetadata a) => IsStoredByOtherAttribute(SdkTwin.Model(a));

    /// <summary>The SDK twin of <see cref="IsReadable(ColumnDefinition)"/>.</summary>
    public static bool IsReadable(AttributeMetadata a) => IsReadable(SdkTwin.Model(a));

    /// <summary>The SDK twin of <see cref="EntityNameColumn(Model.Metadata.EntityNameColumn)"/>.</summary>
    public static string EntityNameColumn(EntityNameAttributeMetadata a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return EntityNameColumn((Model.Metadata.EntityNameColumn)SdkTwin.Model(a));
    }

    /// <summary>The SDK twin of <see cref="ToColumns(ColumnDefinition)"/>.</summary>
    public static IReadOnlyList<SqlColumn> ToColumns(AttributeMetadata a) => ToColumns(SdkTwin.Model(a));
}
