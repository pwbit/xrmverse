using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The formatted value Dataverse returns beside an attribute value (<see cref="Entity.FormattedValues"/>), in en-US with
/// labels in the caller's language when given (#109), else 1033 (fallback: the first label). No user settings: dates in UTC; money with the symbol and precision of its
/// currency when the caller knows them (<see cref="MoneyFormat"/>, plan 2.10 ruling X11), else <c>N2</c> without symbol.
/// </summary>
public static class FormattedValues
{
    public const int English = 1033;
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

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
    /// A money amount (ruling X11): rounded to <see cref="MoneyFormat.Precision"/> decimals (midpoints away from zero) with en-US
    /// digit grouping, the symbol placed per <see cref="MoneyFormat.FormatCode"/> (0 <c>$123</c>, 1 <c>123$</c>, 2 <c>$ 123</c>,
    /// 3 <c>123 $</c>) and a minus sign first for a negative amount; without <paramref name="money"/> (no currency known) <c>N2</c>
    /// without symbol, as before plan 2.10.
    /// </summary>
    public static string FormatMoney(decimal amount, MoneyFormat? money)
    {
        if (money is null) return amount.ToString("N2", EnUs);
        var precision = Math.Clamp(money.Precision, 0, 10);
        var rounded = Math.Round(amount, precision, MidpointRounding.AwayFromZero);
        var digits = Math.Abs(rounded).ToString("N" + precision.ToString(CultureInfo.InvariantCulture), EnUs);
        var sign = rounded < 0 ? "-" : "";
        return sign + money.FormatCode switch
        {
            1 => digits + money.Symbol,
            2 => money.Symbol + " " + digits,
            3 => digits + " " + money.Symbol,
            _ => money.Symbol + digits,
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
