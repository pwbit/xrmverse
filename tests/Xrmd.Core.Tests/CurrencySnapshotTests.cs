using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core;
using Xrmd.Metadata;

namespace Xrmd.Core.Tests;

/// <summary>The money format of a value from the cached currencies (plan 2.10, ruling X11).</summary>
public sealed class CurrencySnapshotTests
{
    private static readonly Guid Euro = Guid.NewGuid(), Dollar = Guid.NewGuid(), Yen = Guid.NewGuid();

    private static CurrencySnapshot Snapshot(int displayOption = 0, int formatCode = 0, int pricing = 3) => new(Euro, pricing, displayOption, formatCode,
        new Dictionary<Guid, CurrencyInfo>
        {
            [Euro] = new(Euro, "EUR", "€", 2, 1m, true),
            [Dollar] = new(Dollar, "USD", "$", 2, 1.1m, true),
            [Yen] = new(Yen, "JPY", "¥", 0, 160m, false),
        });

    private static MoneyAttributeMetadata Money(int? precisionSource = 2, int? precision = 2) =>
        new() { LogicalName = "xv_amount", PrecisionSource = precisionSource, Precision = precision };

    private static MoneyAttributeMetadata Base() => SystemAttributes.MoneyBase(Money());

    [Fact]
    public void Record_currency_gives_its_symbol_and_precision()
    {
        Snapshot().FormatFor(Money(), Dollar).Should().Be(new MoneyFormat("$", 2, 0));
        Snapshot(formatCode: 3).FormatFor(Money(), Yen).Should().Be(new MoneyFormat("¥", 0, 3)); // inactive: still formatted
    }

    [Fact]
    public void Precision_source_0_and_1()
    {
        Snapshot().FormatFor(Money(precisionSource: 0, precision: 1), Yen).Should().Be(new MoneyFormat("¥", 1, 0));
        Snapshot(pricing: 3).FormatFor(Money(precisionSource: 1), Dollar).Should().Be(new MoneyFormat("$", 3, 0));
    }

    [Fact]
    public void Base_money_uses_the_base_currency_whatever_the_record_currency()
    {
        Snapshot().FormatFor(Base(), Dollar).Should().Be(new MoneyFormat("€", 2, 0));
        Snapshot().FormatFor(Base(), null).Should().Be(new MoneyFormat("€", 2, 0));
    }

    [Fact]
    public void Display_option_1_uses_the_iso_code() =>
        Snapshot(displayOption: 1).FormatFor(Money(), Dollar).Should().Be(new MoneyFormat("USD", 2, 0));

    [Fact]
    public void No_or_unknown_currency_gives_no_format()
    {
        Snapshot().FormatFor(Money(), null).Should().BeNull();
        Snapshot().FormatFor(Money(), Guid.NewGuid()).Should().BeNull();
        CurrencySnapshot.Empty.FormatFor(Base(), null).Should().BeNull();
    }
}
