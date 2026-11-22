using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core;

/// <summary>
/// Converts between SDK attribute values and the physical columns of <c>data.[entity]</c> (see <see cref="TypeMapping"/>).
/// Column names come from attribute metadata only (logical names validated when the metadata was created), so they can be
/// bracket-quoted into SQL; values always travel as typed parameters named <c>@{column}</c>.
/// </summary>
public static class EntityRowMapper
{
    /// <summary>
    /// One typed parameter per physical column for every attribute of <paramref name="e"/>: Money → decimal (its own column
    /// only: a <c>&lt;name&gt;_base</c> is written when the row holds it, as computed by <see cref="CurrencyStamp"/>), OptionSetValue → int, OptionSetValueCollection →
    /// CSV, EntityReference → id (plus <c>&lt;name&gt;type</c> = target object type code for polymorphic lookups, ruling R1),
    /// DateTime → datetime2 UTC. Null values become typed NULLs.
    /// </summary>
    /// <param name="entityOf">Resolves entity metadata by logical name (object type codes of polymorphic targets).</param>
    /// <exception cref="XrmdFaultException">InvalidArgument for a polymorphic target with no metadata.</exception>
    public static IReadOnlyList<DbParameter> ToParameters(
        Entity e, IReadOnlyDictionary<string, AttributeMetadata> attrs, Func<string, EntityMetadata?> entityOf)
    {
        var result = new List<DbParameter>();
        foreach (var (name, value) in e.Attributes)
        {
            var a = attrs[name];
            switch (a)
            {
                case MoneyAttributeMetadata:
                    // A money column and its _base are two values of the row: the Core computes the _base (CurrencyStamp).
                    result.Add(Parameter(a.LogicalName, DbType.Decimal, (value as Money)?.Value));
                    break;
                case EntityNameAttributeMetadata when value is string table:
                    // An EntityName column holds the table's object type code (plan 2.11 A7).
                    result.Add(Parameter(a.LogicalName, DbType.Int32, entityOf(table)?.ObjectTypeCode
                        ?? throw XrmdFaultException.InvalidArgument($"Attribute '{a.LogicalName}' names table '{table}', which does not exist.")));
                    break;
                case LookupAttributeMetadata when TypeMapping.IsPolymorphicLookup(a):
                    var reference = value as EntityReference;
                    result.Add(Parameter(a.LogicalName, DbType.Guid, reference?.Id));
                    result.Add(Parameter(a.LogicalName + TypeMapping.TypeColumnSuffix, DbType.Int32,
                        reference is null ? null : ObjectTypeCodeOf(entityOf, a, reference)));
                    break;
                default:
                    result.Add(Parameter(a.LogicalName, DbTypeOf(a), ToColumnValue(value)));
                    break;
            }
        }
        return result;
    }

    /// <summary>Attributes that can be read back (<see cref="TypeMapping.IsReadable"/>): those with their own column, money <c>_base</c>
    /// attributes, the url of an image column, EntityName companions and calculated columns (plan 2.12a).</summary>
    public static bool IsReadable(AttributeMetadata a) => TypeMapping.IsReadable(a);

    /// <summary>
    /// The SDK value of the (non-null) column at <paramref name="ordinal"/> holding attribute <paramref name="a"/>
    /// (null columns are omitted from entities, as Dataverse does): Money → Money, lookup → EntityReference,
    /// Picklist/State/Status → OptionSetValue, multi-select → OptionSetValueCollection, EntityName → the table's logical name (from
    /// the object type code in its column, or in its lookup's type column for a companion), DateTime → UTC, anything else the provider value
    /// (<c>versionnumber</c> is read as bigint → long). Lookup targets: the single/first target, or from
    /// <c>&lt;name&gt;type</c> for polymorphic lookups.
    /// </summary>
    /// <param name="typeOrdinal">Ordinal of the <c>&lt;name&gt;type</c> column of a polymorphic lookup, when read.</param>
    /// <param name="entityByObjectTypeCode">Resolves the target entity of a polymorphic lookup.</param>
    public static object ReadValue(
        DbDataReader rd, int ordinal, AttributeMetadata a, int? typeOrdinal, Func<int, EntityMetadata?> entityByObjectTypeCode) =>
        a switch
        {
            MoneyAttributeMetadata => new Money(rd.GetDecimal(ordinal)),
            LookupAttributeMetadata lookup => new EntityReference(TargetOf(rd, lookup, typeOrdinal, entityByObjectTypeCode), rd.GetGuid(ordinal)),
            MultiSelectPicklistAttributeMetadata => ParseOptions(rd.GetString(ordinal)),
            // The logical name of the table whose object type code the column holds (plan 2.11 A7); a code no table has (never
            // written by the server) reads as its number.
            EntityNameAttributeMetadata => entityByObjectTypeCode(rd.GetInt32(ordinal))?.LogicalName
                                           ?? rd.GetInt32(ordinal).ToString(CultureInfo.InvariantCulture),
            EnumAttributeMetadata => new OptionSetValue(rd.GetInt32(ordinal)),
            DateTimeAttributeMetadata => DateTime.SpecifyKind(rd.GetDateTime(ordinal), DateTimeKind.Utc),
            _ => rd.GetValue(ordinal),
        };

    private static int ObjectTypeCodeOf(Func<string, EntityMetadata?> entityOf, AttributeMetadata a, EntityReference reference) =>
        entityOf(reference.LogicalName)?.ObjectTypeCode
        ?? throw XrmdFaultException.InvalidArgument(
            $"Attribute '{a.LogicalName}' references entity '{reference.LogicalName}', which does not exist.");

    private static string TargetOf(
        DbDataReader rd, LookupAttributeMetadata lookup, int? typeOrdinal, Func<int, EntityMetadata?> entityByObjectTypeCode)
    {
        if (typeOrdinal is { } type && !rd.IsDBNull(type) && entityByObjectTypeCode(rd.GetInt32(type)) is { } target)
            return target.LogicalName;
        return lookup.Targets?.FirstOrDefault() ?? "";
    }

    private static OptionSetValueCollection ParseOptions(string csv) =>
        new(csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => new OptionSetValue(int.Parse(v, CultureInfo.InvariantCulture))).ToList());

    private static object? ToColumnValue(object? value) => value switch
    {
        null => null,
        OptionSetValueCollection options => string.Join(",", options.Select(o => o.Value.ToString(CultureInfo.InvariantCulture))),
        OptionSetValue option => option.Value,
        EntityReference reference => reference.Id,
        DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        DateTime other => DateTime.SpecifyKind(other, DateTimeKind.Utc),
        _ => value,
    };

    private static DbType DbTypeOf(AttributeMetadata a) => a switch
    {
        StringAttributeMetadata or MemoAttributeMetadata or MultiSelectPicklistAttributeMetadata => DbType.String,
        IntegerAttributeMetadata or EnumAttributeMetadata => DbType.Int32,
        BigIntAttributeMetadata => DbType.Int64,
        DecimalAttributeMetadata or MoneyAttributeMetadata => DbType.Decimal,
        DoubleAttributeMetadata => DbType.Double,
        BooleanAttributeMetadata => DbType.Boolean,
        DateTimeAttributeMetadata => DbType.DateTime2,
        // A file column holds the fileattachmentid of its file (ruling F1).
        LookupAttributeMetadata or UniqueIdentifierAttributeMetadata or FileAttributeMetadata => DbType.Guid,
        // An image column holds its thumbnail (ruling F2).
        ImageAttributeMetadata => DbType.Binary,
        _ => throw XrmdFaultException.InvalidArgument($"Attribute '{a.LogicalName}' of type {a.AttributeType} cannot be written."),
    };

    private static DbParameter Parameter(string column, DbType type, object? value) =>
        new DetachedParameter("@" + column, type, value, type is DbType.String or DbType.Binary ? -1 : 0);
}
