using Microsoft.Xrm.Sdk;

namespace Xrmd.Query.Sdk;

/// <summary>
/// The SDK value types a condition may carry (QueryExpression callers), unwrapped to the raw value <see cref="Sql.ValueCoercer"/>
/// converts: <see cref="Money"/> → decimal, <see cref="OptionSetValue"/> → int, <see cref="EntityReference"/> → its id.
/// </summary>
internal static class SdkConditionValues
{
    /// <summary>The raw value of an SDK value type; any other value (null included) is returned unchanged.</summary>
    internal static object? Unwrap(object? value) => value switch
    {
        Money m => m.Value,
        OptionSetValue o => o.Value,
        EntityReference r => r.Id,
        _ => value,
    };
}
