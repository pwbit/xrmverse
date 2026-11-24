using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The arithmetic of Dataverse multi-currency (plan 2.10, rulings X9 and X10): the effective precision of a money attribute,
/// rounding, the exchange rate stored on a record and the conversion to and from the base currency. Pure functions, shared by the
/// write path and the rollups (plan 2.12).
/// </summary>
public static partial class CurrencyMath
{
    /// <summary>The bound of a <c>{money}_base</c> value (±922337203685477, observed).</summary>
    public const decimal BaseLimit = 922337203685477m;

    /// <summary>The precision a money attribute takes when its <c>Precision</c> is not set (Learn: default 2).</summary>
    public const int DefaultPrecision = 2;

    /// <summary>
    /// The number of decimals a value of <paramref name="a"/> is rounded to: <c>PrecisionSource</c> 0 → the attribute's
    /// <c>Precision</c> (2 when not set); 1 → <paramref name="pricingPrecision"/> (<c>organization.pricingdecimalprecision</c>);
    /// 2 or not set → <paramref name="currencyPrecision"/> (the record currency's <c>currencyprecision</c>). Never more than the
    /// money column scale (<see cref="CurrencySchema.MaxPrecision"/>, decision D2) nor less than 0.
    /// </summary>
    public static int EffectivePrecision(MoneyColumn a, int currencyPrecision, int pricingPrecision)
    {
        ArgumentNullException.ThrowIfNull(a);
        var precision = a.PrecisionSource switch
        {
            0 => a.Precision ?? DefaultPrecision,
            1 => pricingPrecision,
            _ => currencyPrecision,
        };
        return Math.Clamp(precision, CurrencySchema.MinPrecision, CurrencySchema.MaxPrecision);
    }

    /// <summary><paramref name="value"/> rounded to <paramref name="precision"/> decimals, midpoints away from zero.</summary>
    public static decimal Round(decimal value, int precision) => Math.Round(value, precision, MidpointRounding.AwayFromZero);

    /// <summary>The exchange rate stored on a record for a currency at <paramref name="currencyRate"/>: rounded to 10 decimals.</summary>
    public static decimal RecordRate(decimal currencyRate) => Round(currencyRate, CurrencySchema.RecordRateDecimals);

    /// <summary>
    /// <paramref name="value"/> in the base currency: <c>value / rate</c> rounded to 4 decimals (the column scale), with
    /// <paramref name="rate"/> the record's stored rate (<see cref="RecordRate"/>), so that the result is reproducible from the row.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">For a rate that is not positive.</exception>
    public static decimal ToBase(decimal value, decimal rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        return Round(value / rate, CurrencySchema.BaseMoneyPrecision);
    }

    /// <summary>A base-currency value in a currency at <paramref name="rate"/>: <c>baseValue * rate</c> rounded to 4 decimals.</summary>
    /// <exception cref="ArgumentOutOfRangeException">For a rate that is not positive.</exception>
    public static decimal FromBase(decimal baseValue, decimal rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        return Round(baseValue * rate, CurrencySchema.BaseMoneyPrecision);
    }
}
