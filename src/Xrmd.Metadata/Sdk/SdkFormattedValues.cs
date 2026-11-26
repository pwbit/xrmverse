using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Sdk;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// SDK twins of <see cref="FormattedValues"/> (S.2b2, Task 4). Since S.2e3b <c>Format</c> runs the model body: it reads the
/// column in through the view cache (no conversion for a store view) and the SDK value with <c>SdkRecords.ValueToModel</c>.
/// Transitional: removed with the SDK view (S.2i).
/// </summary>
public static partial class FormattedValues
{
    /// <summary>
    /// The SDK twin of <see cref="Format(ColumnDefinition, object, MoneyFormat?, Func{string, TableDefinition?}?, int)"/>: the column
    /// read as the model (a view of the store is looked up, not converted), the value and the tables read in, the model body run.
    /// </summary>
    public static string? Format(AttributeMetadata a, object sdkValue, MoneyFormat? money = null, Func<string, EntityMetadata?>? tables = null,
        int languageCode = English)
    {
        ArgumentNullException.ThrowIfNull(a);
        return Format(SdkTwin.Model(a), SdkRecords.ValueToModel(sdkValue)!, money, tables is null ? null : SdkTwin.Model(tables), languageCode);
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
}
