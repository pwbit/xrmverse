using System.Data;
using System.Data.Common;
using System.Globalization;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;

namespace Xrmd.Core;

/// <summary>
/// Converts between model record values and the physical columns of <c>data.[entity]</c> (see <see cref="TypeMapping"/>).
/// Column names come from attribute metadata only (logical names validated when the metadata was created), so they can be
/// bracket-quoted into SQL; values always travel as typed parameters named <c>@{column}</c>. On the model since S.2e3b; the SDK twins
/// of its public members are in <c>Sdk/EntityRowMapper.Sdk.cs</c>.
/// </summary>
public static partial class EntityRowMapper
{
    /// <summary>
    /// One typed parameter per physical column for every value of <paramref name="row"/>: money → decimal (its own column only: a
    /// <c>&lt;name&gt;_base</c> is written when the row holds it, as computed by <see cref="CurrencyStamp"/>), choice → int, choices →
    /// CSV, reference → id (plus <c>&lt;name&gt;type</c> = target object type code for polymorphic lookups, ruling R1), DateTime →
    /// datetime2 UTC. Null values become typed NULLs.
    /// </summary>
    /// <param name="columns">The columns of the row's table by logical name.</param>
    /// <param name="tables">Resolves a table by logical name (object type codes of polymorphic targets).</param>
    /// <exception cref="XrmdFaultException">InvalidArgument for a polymorphic target with no metadata.</exception>
    public static IReadOnlyList<DbParameter> ToParameters(
        Record row, IReadOnlyDictionary<string, ColumnDefinition> columns, Func<string, TableDefinition?> tables)
    {
        ArgumentNullException.ThrowIfNull(row);
        var result = new List<DbParameter>();
        foreach (var (name, value) in row.Attributes)
        {
            var a = columns[name];
            switch (a)
            {
                case MoneyColumn:
                    // A money column and its _base are two values of the row: the Core computes the _base (CurrencyStamp).
                    result.Add(Parameter(a.LogicalName!, DbType.Decimal, (value as MoneyValue)?.Value));
                    break;
                case EntityNameColumn when value is string table:
                    // An EntityName column holds the table's object type code (plan 2.11 A7).
                    result.Add(Parameter(a.LogicalName!, DbType.Int32, tables(table)?.ObjectTypeCode
                        ?? throw XrmdFaultException.InvalidArgument($"Attribute '{a.LogicalName}' names table '{table}', which does not exist.")));
                    break;
                case LookupColumn when TypeMapping.IsPolymorphicLookup(a):
                    var reference = value as RecordReference;
                    result.Add(Parameter(a.LogicalName!, DbType.Guid, reference?.Id));
                    result.Add(Parameter(a.LogicalName + TypeMapping.TypeColumnSuffix, DbType.Int32,
                        reference is null ? null : ObjectTypeCodeOf(tables, a, reference)));
                    break;
                default:
                    result.Add(Parameter(a.LogicalName!, DbTypeOf(a), ToColumnValue(value)));
                    break;
            }
        }
        return result;
    }

    /// <summary>Columns that can be read back (<see cref="TypeMapping.IsReadable(ColumnDefinition)"/>): those with their own SQL column,
    /// money <c>_base</c> columns, the url of an image column, EntityName companions and calculated columns (plan 2.12a).</summary>
    public static bool IsReadable(ColumnDefinition a) => TypeMapping.IsReadable(a);

    /// <summary>
    /// The model value of the (non-null) column at <paramref name="ordinal"/> holding column <paramref name="a"/>
    /// (null columns are omitted from records, as Dataverse does): Money → <see cref="MoneyValue"/>, lookup →
    /// <see cref="RecordReference"/>, Picklist/State/Status → <see cref="ChoiceValue"/>, multi-select → <see cref="ChoiceValues"/>,
    /// EntityName → the table's logical name (from the object type code in its column, or in its lookup's type column for a companion),
    /// DateTime → UTC, anything else the provider value (<c>versionnumber</c> is read as bigint → long). Lookup targets: the
    /// single/first target, or from <c>&lt;name&gt;type</c> for polymorphic lookups.
    /// </summary>
    /// <param name="typeOrdinal">Ordinal of the <c>&lt;name&gt;type</c> column of a polymorphic lookup, when read.</param>
    /// <param name="tableByObjectTypeCode">Resolves the target table of a polymorphic lookup.</param>
    public static object ReadValue(
        DbDataReader rd, int ordinal, ColumnDefinition a, int? typeOrdinal, Func<int, TableDefinition?> tableByObjectTypeCode) =>
        a switch
        {
            MoneyColumn => new MoneyValue(rd.GetDecimal(ordinal)),
            LookupColumn lookup => new RecordReference
            {
                LogicalName = TargetOf(rd, lookup, typeOrdinal, tableByObjectTypeCode), Id = rd.GetGuid(ordinal),
            },
            MultiChoiceColumn => ParseChoices(rd.GetString(ordinal)),
            // The logical name of the table whose object type code the column holds (plan 2.11 A7); a code no table has (never
            // written by the server) reads as its number.
            EntityNameColumn => tableByObjectTypeCode(rd.GetInt32(ordinal))?.LogicalName
                                ?? rd.GetInt32(ordinal).ToString(CultureInfo.InvariantCulture),
            ChoiceColumnBase => new ChoiceValue(rd.GetInt32(ordinal)),
            DateTimeColumn => DateTime.SpecifyKind(rd.GetDateTime(ordinal), DateTimeKind.Utc),
            _ => rd.GetValue(ordinal),
        };

    private static int ObjectTypeCodeOf(Func<string, TableDefinition?> tables, ColumnDefinition a, RecordReference reference) =>
        tables(reference.LogicalName)?.ObjectTypeCode
        ?? throw XrmdFaultException.InvalidArgument(
            $"Attribute '{a.LogicalName}' references entity '{reference.LogicalName}', which does not exist.");

    private static string TargetOf(
        DbDataReader rd, LookupColumn lookup, int? typeOrdinal, Func<int, TableDefinition?> tableByObjectTypeCode)
    {
        if (typeOrdinal is { } type && !rd.IsDBNull(type) && tableByObjectTypeCode(rd.GetInt32(type)) is { } target)
            return target.LogicalName!;
        return lookup.Targets?.FirstOrDefault() ?? "";
    }

    private static ChoiceValues ParseChoices(string csv)
    {
        var result = new ChoiceValues();
        foreach (var v in csv.Split(',', StringSplitOptions.RemoveEmptyEntries)) result.Add(new ChoiceValue(int.Parse(v, CultureInfo.InvariantCulture)));
        return result;
    }

    private static object? ToColumnValue(object? value) => value switch
    {
        null => null,
        ChoiceValues options => string.Join(",", options.Select(o => o.Value.ToString(CultureInfo.InvariantCulture))),
        ChoiceValue option => option.Value,
        RecordReference reference => reference.Id,
        DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        DateTime other => DateTime.SpecifyKind(other, DateTimeKind.Utc),
        _ => value,
    };

    private static DbType DbTypeOf(ColumnDefinition a) => a switch
    {
        TextColumn or MemoColumn or MultiChoiceColumn => DbType.String,
        IntegerColumn or ChoiceColumnBase => DbType.Int32,
        BigIntColumn => DbType.Int64,
        DecimalColumn or MoneyColumn => DbType.Decimal,
        DoubleColumn => DbType.Double,
        BooleanColumn => DbType.Boolean,
        DateTimeColumn => DbType.DateTime2,
        // A file column holds the fileattachmentid of its file (ruling F1).
        LookupColumn or UniqueIdentifierColumn or FileColumn => DbType.Guid,
        // An image column holds its thumbnail (ruling F2).
        ImageColumn => DbType.Binary,
        _ => throw XrmdFaultException.InvalidArgument($"Attribute '{a.LogicalName}' of type {a.AttributeType} cannot be written."),
    };

    private static DbParameter Parameter(string column, DbType type, object? value) =>
        new DetachedParameter("@" + column, type, value, type is DbType.String or DbType.Binary ? -1 : 0);
}
