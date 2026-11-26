using System.Data.Common;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Core;

/// <summary>
/// S.2e3b: the SDK twins of <see cref="EntityRowMapper"/>, whose bodies are on the model, for the callers still on the SDK (the match
/// codes, S.2e4; tests). Each reads its arguments in (a store view is looked up in the view cache, not converted), calls the model body
/// and writes a value out, so there is one body. Transitional: removed with their callers' moves.
/// </summary>
public static partial class EntityRowMapper
{
    /// <summary>
    /// The SDK twin of <see cref="ToParameters(Model.Records.Record, IReadOnlyDictionary{string, ColumnDefinition}, Func{string, TableDefinition?})"/>:
    /// the entity read in once, with the attributes it holds.
    /// </summary>
    /// <param name="entityOf">Resolves entity metadata by logical name (object type codes of polymorphic targets).</param>
    /// <exception cref="XrmdFaultException">InvalidArgument for a polymorphic target with no metadata.</exception>
    public static IReadOnlyList<DbParameter> ToParameters(
        Entity e, IReadOnlyDictionary<string, AttributeMetadata> attrs, Func<string, EntityMetadata?> entityOf)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(attrs);
        var columns = new Dictionary<string, ColumnDefinition>(StringComparer.Ordinal);
        foreach (var name in e.Attributes.Keys) columns[name] = attrs[name].ToModel();
        return ToParameters(e.ToModel(), columns, name => entityOf(name)?.ToModel());
    }

    /// <summary>The SDK twin of <see cref="IsReadable(ColumnDefinition)"/>.</summary>
    public static bool IsReadable(AttributeMetadata a) => IsReadable(a.ToModel());

    /// <summary>
    /// The SDK twin of <see cref="ReadValue(System.Data.Common.DbDataReader, int, ColumnDefinition, int?, Func{int, TableDefinition?})"/>,
    /// for the readers still on the SDK (the match codes, S.2e4): the model value written out once.
    /// </summary>
    public static object ReadValue(
        DbDataReader rd, int ordinal, AttributeMetadata a, int? typeOrdinal, Func<int, EntityMetadata?> entityByObjectTypeCode) =>
        SdkRecords.ValueToSdk(ReadValue(rd, ordinal, a.ToModel(), typeOrdinal, code => entityByObjectTypeCode(code)?.ToModel()))!;
}
