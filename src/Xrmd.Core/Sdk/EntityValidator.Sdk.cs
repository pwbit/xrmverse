using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;

namespace Xrmd.Core.Validation;

/// <summary>
/// S.2e3b: the SDK twins of <see cref="EntityValidator"/>, whose bodies are on the model, for the callers still on the SDK (the migration
/// import's <c>ImportRecordBuilder</c>, tests). Each reads its arguments in and calls the model body. Transitional: removed with their
/// callers' moves.
/// </summary>
public static partial class EntityValidator
{
    /// <summary>
    /// The SDK twin of <see cref="Validate(Record, TableDefinition, IReadOnlyDictionary{string, ColumnDefinition}, bool)"/>: the entity
    /// read in through a mirror (which carries a null item of a party list, so the validation refuses it as before), the metadata looked up.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) naming the offending attribute.</exception>
    public static void Validate(Entity e, EntityMetadata m, IReadOnlyDictionary<string, AttributeMetadata> attrs, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(attrs);
        var columns = new Dictionary<string, ColumnDefinition>(StringComparer.Ordinal);
        foreach (var (name, a) in attrs) columns[name] = a.ToModel();
        Validate((Record)new SdkMirror().In(e)!, m.ToModel(), columns, isCreate);
    }

    /// <summary>The SDK twin of <see cref="IsWritable(ColumnDefinition, bool)"/>.</summary>
    public static bool IsWritable(AttributeMetadata a, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(a);
        return IsWritable(a.ToModel(), isCreate);
    }
}
