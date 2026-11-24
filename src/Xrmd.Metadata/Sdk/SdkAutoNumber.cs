using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>The SDK twins of <see cref="AutoNumberFormat"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public sealed partial class AutoNumberFormat
{
    /// <summary>The SDK twin of <see cref="OfColumn(string, ColumnDefinition)"/>.</summary>
    public static string? OfColumn(string entityLogicalName, AttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return OfColumn(entityLogicalName, SdkTwin.Model(attribute));
    }
}

/// <summary>The SDK twins of <see cref="AutoNumberSequences"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class AutoNumberSequences
{
    /// <summary>The SDK twin of <see cref="EnsureSql(ColumnDefinition, ISqlDialect)"/>.</summary>
    public static string EnsureSql(AttributeMetadata attribute, ISqlDialect d) => EnsureSql(ModelOf(attribute), d);

    /// <summary>The SDK twin of <see cref="DropSql(ColumnDefinition, ISqlDialect)"/>.</summary>
    public static string DropSql(AttributeMetadata attribute, ISqlDialect d) => DropSql(ModelOf(attribute), d);

    /// <summary>The SDK twin of <see cref="ResetSql(ColumnDefinition, long, ISqlDialect)"/>.</summary>
    public static string ResetSql(AttributeMetadata attribute, long seed, ISqlDialect d) => ResetSql(ModelOf(attribute), seed, d);

    /// <summary>The SDK twin of <see cref="NextValueSql(ColumnDefinition, ISqlDialect)"/>.</summary>
    public static string NextValueSql(AttributeMetadata attribute, ISqlDialect dialect) => NextValueSql(ModelOf(attribute), dialect);

    /// <summary>The SDK twin of <see cref="ReadSql(ColumnDefinition, ISqlDialect)"/>.</summary>
    public static string ReadSql(AttributeMetadata attribute, ISqlDialect d) => ReadSql(ModelOf(attribute), d);

    private static ColumnDefinition ModelOf(AttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return SdkTwin.Model(attribute);
    }
}
