using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Migration.Records;

namespace Xrmd.Admin.Migration.Import;

/// <summary>A package value that cannot be written: it does not fit the target column's type, or (strict lookups) its record is missing.</summary>
public sealed class ImportValueException(string message, string code = "0x80040203") : Exception(message)
{
    /// <summary>The error code of the row in the report (InvalidArgument by default).</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Turns a package value into the SDK value of the target column (plan M4a): text, numbers, money, yes/no, dates, choices, GUIDs and
/// lookups (through the run's id map). Party lists are mapped by the record writer; file and image columns are written by the files
/// phase and give <see cref="Skip"/>.
/// </summary>
public static class ImportValueMapper
{
    /// <summary>The value is not written by the records phase (file and image columns).</summary>
    public static readonly object Skip = new();

    /// <summary>The id of <paramref name="id"/> in the target: its id-map entry, else itself.</summary>
    public static Guid Map(IReadOnlyDictionary<Guid, Guid> idMap, Guid id) => idMap.TryGetValue(id, out var target) ? target : id;

    public static object? Map(AttributeMetadata target, MigrationValue value, IReadOnlyDictionary<Guid, Guid> idMap)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(idMap);
        if (value is NullValue) return null;
        if (value is FileValue or ImageValue) return Skip;
        var type = target.AttributeType;
        return (type, value) switch
        {
            (AttributeTypeCode.String or AttributeTypeCode.Memo or AttributeTypeCode.EntityName, StringValue s) => s.Value,
            (AttributeTypeCode.Integer, IntValue i) => checked((int)i.Value),
            (AttributeTypeCode.BigInt, IntValue i) => i.Value,
            (AttributeTypeCode.Decimal, DecimalValue d) => d.Value,
            (AttributeTypeCode.Decimal, IntValue i) => (decimal)i.Value,
            (AttributeTypeCode.Double, DoubleValue d) => d.Value,
            (AttributeTypeCode.Double, IntValue i) => (double)i.Value,
            (AttributeTypeCode.Double, DecimalValue d) => (double)d.Value,
            (AttributeTypeCode.Money, MoneyValue m) => new Money(m.Value),
            (AttributeTypeCode.Money, DecimalValue d) => new Money(d.Value),
            (AttributeTypeCode.Boolean, BoolValue b) => b.Value,
            (AttributeTypeCode.DateTime, DateTimeValue dt) => dt.Utc,
            (AttributeTypeCode.DateTime, DateOnlyValue d) => d.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
            (AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status, OptionValue o) => new OptionSetValue(o.Value),
            (AttributeTypeCode.Virtual, OptionsValue o) when target is MultiSelectPicklistAttributeMetadata =>
                new OptionSetValueCollection([.. o.Values.Select(v => new OptionSetValue(v))]),
            (AttributeTypeCode.Uniqueidentifier, GuidValue g) => Map(idMap, g.Value),
            (AttributeTypeCode.Lookup or AttributeTypeCode.Customer or AttributeTypeCode.Owner, LookupValue l) =>
                new EntityReference(l.Table, Map(idMap, l.Id)),
            _ => throw new ImportValueException(
                $"The value {value.GetType().Name.Replace("Value", "", StringComparison.Ordinal)} does not fit the column '{target.LogicalName}' of type {type}."),
        };
    }
}
