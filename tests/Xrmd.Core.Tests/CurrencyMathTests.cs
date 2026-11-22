using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Core.Tests;

/// <summary>Plan 2.10 rulings X9 and X10: effective precision, rounding, the record rate and the base value of money.</summary>
public class CurrencyMathTests
{
    private static MoneyAttributeMetadata Money(int? source, int? precision = null) =>
        new("xv_Amount") { LogicalName = "xv_amount", PrecisionSource = source, Precision = precision };

    [Fact]
    public void Precision_source_0_1_2_and_null()
    {
        CurrencyMath.EffectivePrecision(Money(0, 3), currencyPrecision: 2, pricingPrecision: 2).Should().Be(3);
        CurrencyMath.EffectivePrecision(Money(0), currencyPrecision: 0, pricingPrecision: 0).Should().Be(2, "Precision defaults to 2");
        CurrencyMath.EffectivePrecision(Money(1, 3), currencyPrecision: 2, pricingPrecision: 1).Should().Be(1);
        CurrencyMath.EffectivePrecision(Money(2, 3), currencyPrecision: 0, pricingPrecision: 2).Should().Be(0);
        CurrencyMath.EffectivePrecision(Money(null, 3), currencyPrecision: 0, pricingPrecision: 2).Should().Be(0);
    }

    [Fact]
    public void Precision_never_exceeds_the_column_scale()
    {
        CurrencyMath.EffectivePrecision(Money(1), currencyPrecision: 2, pricingPrecision: 10).Should().Be(4);
        CurrencyMath.EffectivePrecision(Money(2), currencyPrecision: 6, pricingPrecision: 2).Should().Be(4);
    }

    [Fact]
    public void Round_is_away_from_zero()
    {
        CurrencyMath.Round(2.345m, 2).Should().Be(2.35m);
        CurrencyMath.Round(-2.345m, 2).Should().Be(-2.35m);
        CurrencyMath.Round(1.25m, 1).Should().Be(1.3m);
    }

    [Fact]
    public void ToBase_100_at_1_10_is_90_9091()
    {
        CurrencyMath.ToBase(100m, 1.1m).Should().Be(90.9091m);
        CurrencyMath.ToBase(-100m, 1.1m).Should().Be(-90.9091m);
        CurrencyMath.FromBase(90.9091m, 1.1m).Should().Be(100.0000m);
    }

    [Fact]
    public void RecordRate_rounds_to_10_decimals()
    {
        CurrencyMath.RecordRate(1.123456789012m).Should().Be(1.1234567890m);
        CurrencyMath.RecordRate(0.00000000015m).Should().Be(0.0000000002m);
        CurrencyMath.RecordRate(1.1m).Should().Be(1.1m);
    }

    [Fact]
    public void BaseLimit_is_the_base_money_range()
    {
        CurrencyMath.BaseLimit.Should().Be(922337203685477m);
        CurrencyMath.BaseLimit.Should().Be(CurrencySchema.MaxBaseMoney);
    }
}
