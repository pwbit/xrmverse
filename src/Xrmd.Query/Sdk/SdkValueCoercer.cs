using System.Data;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Query.Sql;

/// <summary>
/// The SDK twins of <see cref="ValueCoercer"/> (S.2c, R1, R2): the model of the attribute is looked up when it is a view of the store
/// (<see cref="SdkMetadataView.ToModel(AttributeMetadata)"/>), converted afresh otherwise. Transitional: removed with the SDK view (S.2i).
/// </summary>
public static partial class ValueCoercer
{
    /// <summary>The SDK twin of <see cref="Coerce(Xrmd.Model.Metadata.ColumnDefinition, object?)"/>.</summary>
    /// <exception cref="XrmdValidationException">See the model member.</exception>
    public static object Coerce(AttributeMetadata a, object? value) => Coerce(a.ToModel(), value);

    /// <summary>The SDK twin of <see cref="DbTypeOf(Xrmd.Model.Metadata.ColumnDefinition)"/>.</summary>
    public static DbType DbTypeOf(AttributeMetadata a) => DbTypeOf(a.ToModel());
}
