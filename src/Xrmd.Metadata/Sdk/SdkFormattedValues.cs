using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// SDK twins of <see cref="FormattedValues"/> (S.2b2, Task 4). <see cref="Format"/> keeps its SDK body: it formats SDK record
/// values (<see cref="OptionSetValue"/>, <see cref="Money"/>, <see cref="EntityReference"/>) and runs for every value, so it does
/// not convert its column. Transitional: removed with the SDK view (S.2i).
/// </summary>
public static partial class FormattedValues
{
    /// <summary>
    /// Picklist/State/Status → option label (unknown option → null); multi-select → labels joined by <c>"; "</c>;
    /// Boolean → TrueOption/FalseOption label (default "Yes"/"No"); Money → <see cref="FormatMoney"/> with <paramref name="money"/>
    /// (<c>N2</c> without it); Decimal → <c>N{Precision}</c>; Double → <c>N{Precision ?? 2}</c>; Integer/BigInt → <c>N0</c>;
    /// DateTime → <c>M/d/yyyy h:mm tt</c> (<c>M/d/yyyy</c> for DateOnly); lookup → <see cref="EntityReference.Name"/>. Other types,
    /// and <c>versionnumber</c>: null. <paramref name="money"/> is ignored for the other types. EntityName (plan 2.11 A7: the value
    /// is a table's logical name) → the table's display name from <paramref name="tables"/>, else its SchemaName, else the name
    /// itself.
    /// </summary>
    public static string? Format(AttributeMetadata a, object sdkValue, MoneyFormat? money = null, Func<string, EntityMetadata?>? tables = null,
        int languageCode = English)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.LogicalName == TypeMapping.VersionNumber) return null; // Dataverse does not format the row version
        return (a, sdkValue) switch
        {
            (MultiSelectPicklistAttributeMetadata m, OptionSetValueCollection options) => JoinLabels(m.OptionSet, options, languageCode),
            (EnumAttributeMetadata e, OptionSetValue option) => OptionLabel(e.OptionSet, option.Value, languageCode),
            (BooleanAttributeMetadata b, bool flag) => BooleanLabel(b, flag, languageCode),
            (MoneyAttributeMetadata, Money amount) => FormatMoney(amount.Value, money),
            (DecimalAttributeMetadata d, decimal number) => number.ToString("N" + (d.Precision ?? 2).ToString(CultureInfo.InvariantCulture), EnUs),
            (DoubleAttributeMetadata d, double number) => number.ToString("N" + (d.Precision ?? 2).ToString(CultureInfo.InvariantCulture), EnUs),
            (IntegerAttributeMetadata, int number) => number.ToString("N0", EnUs),
            (BigIntAttributeMetadata, long number) => number.ToString("N0", EnUs),
            (DateTimeAttributeMetadata d, DateTime date) =>
                date.ToString(d.Format == DateTimeFormat.DateOnly ? "M/d/yyyy" : "M/d/yyyy h:mm tt", EnUs),
            (LookupAttributeMetadata, EntityReference reference) => reference.Name,
            (EntityNameAttributeMetadata, string table) => TableName(table, tables?.Invoke(table), languageCode),
            _ => null,
        };
    }

    /// <summary>
    /// The label of an <c>activitypointer</c> row's <c>statuscode</c> (plan 2.11, ruling A11, checklist 3f): the pointer keeps the
    /// activity's own status, so the label comes from the <c>statuscode</c> option set of the activity's table
    /// (<paramref name="activityTable"/>, the row's <c>activitytypecode</c>), e.g. 3 on a task is "In Progress". Null when the table or
    /// the option is unknown.
    /// </summary>
    public static string? ActivityStatus(int status, string? activityTable, Func<string, EntityMetadata?> tables, int languageCode = English)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (activityTable is null || tables(activityTable)?.Attributes?.OfType<StatusAttributeMetadata>().FirstOrDefault() is not { } statusCode)
            return null;
        return OptionLabel(statusCode.OptionSet, status, languageCode);
    }

    /// <summary>The formatted value of an EntityName value: the table's display name, else its SchemaName, else the logical name.</summary>
    public static string TableName(string logicalName, EntityMetadata? table, int languageCode = English) =>
        LabelText(table?.DisplayName, languageCode) ?? table?.SchemaName ?? logicalName;

    /// <summary>
    /// The label text in <paramref name="languageCode"/> (#109: the caller's <c>usersettings.uilanguageid</c>) when the label
    /// has one; else <c>UserLocalizedLabel</c>, else the 1033 label, else the first one.
    /// </summary>
    public static string? LabelText(Label? label, int languageCode = English) =>
        label is null ? null
        : (languageCode != English ? label.LocalizedLabels?.FirstOrDefault(l => l.LanguageCode == languageCode)?.Label : null)
          ?? label.UserLocalizedLabel?.Label
          ?? label.LocalizedLabels?.FirstOrDefault(l => l.LanguageCode == English)?.Label
          ?? label.LocalizedLabels?.FirstOrDefault()?.Label;

    private static string? OptionLabel(OptionSetMetadata? optionSet, int value, int languageCode) =>
        LabelText(optionSet?.Options?.FirstOrDefault(o => o.Value == value)?.Label, languageCode);

    private static string? JoinLabels(OptionSetMetadata? optionSet, OptionSetValueCollection options, int languageCode)
    {
        var labels = options.Select(o => OptionLabel(optionSet, o.Value, languageCode)).OfType<string>().ToList();
        return labels.Count == 0 ? null : string.Join("; ", labels);
    }

    private static string BooleanLabel(BooleanAttributeMetadata b, bool flag, int languageCode) =>
        LabelText((flag ? b.OptionSet?.TrueOption : b.OptionSet?.FalseOption)?.Label, languageCode) ?? (flag ? "Yes" : "No");
}
