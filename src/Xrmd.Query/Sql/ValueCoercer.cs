using System.Data;
using System.Globalization;
using Xrmd.Abstractions;
using Xrmd.Model.Metadata;
using Xrmd.Query.Sdk;

namespace Xrmd.Query.Sql;

/// <summary>
/// Converts condition values (FetchXML strings or typed SDK values from QueryExpression) to the CLR type of the
/// attribute's column, so they can travel as typed <c>DbParameter</c>s.
/// </summary>
public static partial class ValueCoercer
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// String/Memo → string; Integer → int; BigInt → long; Decimal/Money → decimal; Double → double; Boolean → bool;
    /// Picklist/State/Status → int; DateTime → UTC <see cref="DateTime"/>; Lookup/UniqueIdentifier → Guid.
    /// </summary>
    /// <exception cref="XrmdValidationException">The value is null, cannot be converted, or the attribute type cannot be
    /// used in conditions; the message names the attribute and the value.</exception>
    public static object Coerce(ColumnDefinition a, object? value)
    {
        if (value is null) throw Invalid(a, value);
        object? result = a switch
        {
            TextColumn or MemoColumn => ToStringValue(value),
            IntegerColumn => ToInt(value),
            BigIntColumn => ToLong(value),
            DecimalColumn or MoneyColumn => ToDecimal(value),
            DoubleColumn => ToDouble(value),
            BooleanColumn => ToBool(value),
            MultiChoiceColumn => throw new XrmdValidationException(
                $"Attribute '{a.LogicalName}' of type {a.AttributeType} cannot be compared with this operator."),
            ChoiceColumnBase => ToInt(SdkConditionValues.Unwrap(value) is int o ? o : value), // OptionSetValue
            DateTimeColumn => ToDateTime(value),
            LookupColumn or UniqueIdentifierColumn => ToGuid(value),
            _ => throw new XrmdValidationException(
                $"Attribute '{a.LogicalName}' of type {a.AttributeType} cannot be used in a condition."),
        };
        return result ?? throw Invalid(a, value);
    }

    /// <summary>The parameter type for values coerced for <paramref name="a"/>.</summary>
    public static DbType DbTypeOf(ColumnDefinition a) => a switch
    {
        TextColumn or MemoColumn => DbType.String,
        IntegerColumn or ChoiceColumnBase => DbType.Int32,
        BigIntColumn => DbType.Int64,
        DecimalColumn or MoneyColumn => DbType.Decimal,
        DoubleColumn => DbType.Double,
        BooleanColumn => DbType.Boolean,
        DateTimeColumn => DbType.DateTime2,
        LookupColumn or UniqueIdentifierColumn => DbType.Guid,
        _ => throw new XrmdValidationException($"Attribute '{a.LogicalName}' of type {a.AttributeType} cannot be used in a condition."),
    };

    private static XrmdValidationException Invalid(ColumnDefinition a, object? value) =>
        new($"Invalid value '{(value is null ? "null" : Convert.ToString(value, Invariant))}' for attribute '{a.LogicalName}' of type {a.AttributeType}.");

    private static string? ToStringValue(object value) => value switch
    {
        string s => s,
        IFormattable f => f.ToString(null, Invariant),
        _ => null,
    };

    private static int? ToInt(object value) => value switch
    {
        int i => i,
        long or short or byte or sbyte or ushort or uint => ToLong(value) is { } l and >= int.MinValue and <= int.MaxValue ? (int)l : null,
        Enum e => Convert.ToInt32(e, Invariant),
        decimal or double => ToLong(value) is { } w and >= int.MinValue and <= int.MaxValue ? (int)w : null,
        string s => int.TryParse(s, NumberStyles.Integer, Invariant, out var r) ? r : null,
        _ => null,
    };

    private static long? ToLong(object value) => value switch
    {
        long l => l,
        int or short or byte or sbyte or ushort or uint => Convert.ToInt64(value, Invariant),
        // A whole number within range only (a Cosmos SQL literal is a decimal); a fraction stays a type mismatch.
        decimal m => m == decimal.Truncate(m) && m >= long.MinValue && m <= long.MaxValue ? (long)m : null,
        double d => double.IsFinite(d) && d == Math.Truncate(d) && d >= -9.2233720368547758E+18 && d < 9.2233720368547758E+18 ? (long)d : null,
        string s => long.TryParse(s, NumberStyles.Integer, Invariant, out var r) ? r : null,
        _ => null,
    };

    private static decimal? ToDecimal(object value) => value switch
    {
        decimal d => d,
        _ when SdkConditionValues.Unwrap(value) is decimal m => m, // Money
        int or long or short or byte => Convert.ToDecimal(value, Invariant),
        double or float => double.IsFinite(Convert.ToDouble(value, Invariant))
            && Math.Abs(Convert.ToDouble(value, Invariant)) < (double)decimal.MaxValue ? Convert.ToDecimal(value, Invariant) : null,
        string s => decimal.TryParse(s, NumberStyles.Float, Invariant, out var r) ? r : null,
        _ => null,
    };

    private static double? ToDouble(object value) => value switch
    {
        double d => d,
        float or int or long or short or byte or decimal => Convert.ToDouble(value, Invariant),
        string s => double.TryParse(s, NumberStyles.Float, Invariant, out var r) && double.IsFinite(r) ? r : null,
        _ => null,
    };

    private static bool? ToBool(object value) => value switch
    {
        bool b => b,
        int i when i is 0 or 1 => i == 1,
        string s when s.Trim() == "1" => true,
        string s when s.Trim() == "0" => false,
        string s => bool.TryParse(s.Trim(), out var r) ? r : null,
        _ => null,
    };

    private static DateTime? ToDateTime(object value) => value switch
    {
        DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        DateTime other => DateTime.SpecifyKind(other, DateTimeKind.Utc),
        DateTimeOffset offset => offset.UtcDateTime,
        string s => DateTime.TryParse(s, Invariant,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var r)
            ? DateTime.SpecifyKind(r, DateTimeKind.Utc)
            : null,
        _ => null,
    };

    private static Guid? ToGuid(object value) => value switch
    {
        Guid g => g,
        _ when SdkConditionValues.Unwrap(value) is Guid r => r, // EntityReference
        string s => Guid.TryParse(s.Trim(), out var r) ? r : null,
        _ => null,
    };
}
