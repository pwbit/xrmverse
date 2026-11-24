using Xrmd.Model.Records;
using Xrmd.Query.Sdk;

namespace Xrmd.Query.Sql;

/// <summary>
/// The value types a condition may carry, unwrapped to the raw value <see cref="ValueCoercer"/> converts:
/// <see cref="MoneyValue"/> → decimal, <see cref="ChoiceValue"/> → int, <see cref="RecordReference"/> → its id. Any other value
/// (an SDK value type included) goes through <see cref="SdkConditionValues"/>.
/// </summary>
internal static class ConditionValues
{
    /// <summary>The raw value of a value type; any other value (null included) is returned unchanged.</summary>
    internal static object? Unwrap(object? value) => value switch
    {
        MoneyValue m => m.Value,
        ChoiceValue c => c.Value,
        RecordReference r => r.Id,
        _ => SdkConditionValues.Unwrap(value),
    };

    /// <summary>
    /// The SDK type name (its default text) of a model value type, so a fault message prints what it printed for the SDK value;
    /// null for any other value.
    /// </summary>
    internal static string? SdkTypeName(object? value) => value switch
    {
        MoneyValue => "Microsoft.Xrm.Sdk.Money",
        ChoiceValue => "Microsoft.Xrm.Sdk.OptionSetValue",
        RecordReference => "Microsoft.Xrm.Sdk.EntityReference",
        _ => null,
    };

    /// <summary>The text a fault message shows for a condition value: the invariant text, "null" for null.</summary>
    internal static string Display(object? value) =>
        SdkTypeName(value) ?? (value is null ? "null" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
}
