using System.Data;

namespace Xrmd.Abstractions.Storage;

/// <summary>
/// The <see cref="DbType"/> of a value whose type the code only knows at run time (plan 2.7a, ruling P3): the helpers that take
/// <c>(name, value)</c> pairs type each parameter with it instead of letting the provider infer one. The types are the ones
/// SqlClient inferred for these values before (<c>AddWithValue</c>), so SQL Server receives the same parameter types.
/// </summary>
public static class DbValueTypes
{
    /// <summary>The type of <paramref name="value"/>; the size, precision and scale stay the provider's, from the value.</summary>
    /// <exception cref="ArgumentException">A value of a type no helper passes.</exception>
    public static DbType Of(object? value) => value switch
    {
        null or DBNull => DbType.String,
        Guid => DbType.Guid,
        string => DbType.String,
        int => DbType.Int32,
        long => DbType.Int64,
        short => DbType.Int16,
        byte => DbType.Byte,
        bool => DbType.Boolean,
        DateTime => DbType.DateTime,
        DateTimeOffset => DbType.DateTimeOffset,
        decimal => DbType.Decimal,
        double => DbType.Double,
        float => DbType.Single,
        byte[] => DbType.Binary,
        Enum e => Of(Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), System.Globalization.CultureInfo.InvariantCulture)),
        _ => throw new ArgumentException($"No parameter type for a value of type {value.GetType()}.", nameof(value)),
    };
}
