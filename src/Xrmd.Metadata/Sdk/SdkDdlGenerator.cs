using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>The SDK twins of <see cref="DdlGenerator"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class DdlGenerator
{
    /// <summary>The SDK twin of <see cref="CreateTable(TableDefinition, IEnumerable{ColumnDefinition}, ISqlDialect)"/>.</summary>
    public static string CreateTable(EntityMetadata e, IEnumerable<AttributeMetadata> attrs, ISqlDialect d) =>
        CreateTable(SdkTwin.Model(e), SdkTwin.Model(attrs), d);

    /// <summary>The SDK twin of <see cref="CreateIntersectTable(TableDefinition, IEnumerable{ColumnDefinition}, string, string, ISqlDialect)"/>.</summary>
    public static string CreateIntersectTable(EntityMetadata e, IEnumerable<AttributeMetadata> attrs, string first, string second, ISqlDialect d) =>
        CreateIntersectTable(SdkTwin.Model(e), SdkTwin.Model(attrs), first, second, d);

    /// <summary>The SDK twin of <see cref="AddColumn(string, ColumnDefinition, ISqlDialect)"/>.</summary>
    public static string AddColumn(string entityLogicalName, AttributeMetadata a, ISqlDialect d) => AddColumn(entityLogicalName, SdkTwin.Model(a), d);

    /// <summary>The SDK twin of <see cref="DropColumn(string, ColumnDefinition, ISqlDialect)"/>.</summary>
    public static string DropColumn(string entityLogicalName, AttributeMetadata a, ISqlDialect d) => DropColumn(entityLogicalName, SdkTwin.Model(a), d);

    /// <summary>The SDK twin of <see cref="AlterColumn(string, ColumnDefinition, ISqlDialect)"/>.</summary>
    public static string AlterColumn(string entityLogicalName, AttributeMetadata a, ISqlDialect d) => AlterColumn(entityLogicalName, SdkTwin.Model(a), d);
}
