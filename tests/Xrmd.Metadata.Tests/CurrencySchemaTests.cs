using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>The currency names and limits (plan 2.10, task 1), the base currency of the seed and the seeded currency pair.</summary>
public class CurrencySchemaTests
{
    [Theory]
    [InlineData("account", "transactioncurrency_account")]
    [InlineData("contact", "transactioncurrency_contact")]
    [InlineData("xv_project", "TransactionCurrency_xv_project")]
    [InlineData("activitypointer", "TransactionCurrency_ActivityPointer")]
    [InlineData("email", "TransactionCurrency_Email")]
    [InlineData("task", "TransactionCurrency_Task")]
    [InlineData("phonecall", "TransactionCurrency_PhoneCall")]
    [InlineData("appointment", "TransactionCurrency_Appointment")]
    [InlineData("xv_sms", "TransactionCurrency_xv_sms")]
    [InlineData("new_invoice", "TransactionCurrency_new_invoice")]
    public void Relationship_names(string entity, string expected) =>
        CurrencySchema.RelationshipName(entity).Should().Be(expected);

    [Fact]
    public void Iso_list_has_EUR_USD_and_not_XYZ()
    {
        IsoCurrencyCodes.Contains("EUR").Should().BeTrue();
        IsoCurrencyCodes.Contains("USD").Should().BeTrue();
        IsoCurrencyCodes.Contains("JPY").Should().BeTrue();
        IsoCurrencyCodes.Contains("XYZ").Should().BeFalse();
        IsoCurrencyCodes.Contains("eur").Should().BeFalse("ISO codes are upper case");
        IsoCurrencyCodes.Contains("").Should().BeFalse();
        IsoCurrencyCodes.Contains(null!).Should().BeFalse();
    }

    [Fact]
    public void Limits_are_the_plan_values()
    {
        (CurrencySchema.MinPrecision, CurrencySchema.MaxPrecision).Should().Be((0, 4));
        (CurrencySchema.MinRate, CurrencySchema.MaxRate).Should().Be((0.0000000001m, 100000000000m));
        CurrencySchema.RecordRateDecimals.Should().Be(10);
        CurrencySchema.DefaultPricingPrecision.Should().Be(2);
    }

    [Fact]
    public void Default_base_currency_is_the_euro()
    {
        BaseCurrencySeed.Default.Should().Be(new BaseCurrencySeed("EUR", "Euro", "€", 2));
        // Not given: the euro applies only when the organization gets its first base currency (fix round 1).
        new SeedOptions("org", Guid.NewGuid(), Guid.NewGuid(), "Ada").BaseCurrency.Should().BeNull();
        BaseCurrencySeed.From(null, null, null, null).Should().Be(BaseCurrencySeed.Default);
        BaseCurrencySeed.From("", "", "", null).Should().Be(BaseCurrencySeed.Default);
        BaseCurrencySeed.From("EUR", null, null, 3).Should().Be(new BaseCurrencySeed("EUR", "Euro", "€", 3));
    }

    [Fact]
    public void Other_codes_default_name_and_symbol_to_the_code()
    {
        BaseCurrencySeed.From("CHF", null, null, null).Should().Be(new BaseCurrencySeed("CHF", "CHF", "CHF", 2));
        BaseCurrencySeed.From(" usd ", "US Dollar", "$", 2).Should().Be(new BaseCurrencySeed("USD", "US Dollar", "$", 2));
    }

    [Theory]
    [InlineData("XYZ", null, null, null, "Seed:BaseCurrency")]
    [InlineData("EURO", null, null, null, "Seed:BaseCurrency")]
    [InlineData("USD", null, null, 5, "Seed:BaseCurrencyPrecision")]
    [InlineData("USD", null, null, -1, "Seed:BaseCurrencyPrecision")]
    [InlineData("USD", null, "$$$$$$$$$$$", null, "Seed:BaseCurrencySymbol")]
    public void Invalid_settings_name_the_setting(string? iso, string? name, string? symbol, int? precision, string setting)
    {
        var act = () => BaseCurrencySeed.From(iso, name, symbol, precision);
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(setting);
    }

    [Fact]
    public void A_base_currency_built_directly_is_checked_too()
    {
        var act = () => new BaseCurrencySeed("XYZ", "X", "X", 2).Validate();
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Seed:BaseCurrency");
    }

    private static EntityMetadata SeedDefinition(string name)
    {
        using var stream = typeof(SystemSeed).Assembly.GetManifestResourceStream("Xrmd.Metadata.Seed." + name + ".json")!;
        using var reader = new StreamReader(stream);
        return MetadataSerializer.Deserialize(reader.ReadToEnd());
    }

    /// <summary>
    /// Pre-flight C6: the pair is defined once, in <see cref="SystemAttributes"/>; the seed files of account and contact carry
    /// the same definition (and their relationship, Delete Restrict).
    /// </summary>
    [Theory]
    [InlineData("account")]
    [InlineData("contact")]
    public void Seeded_currency_pair_matches_the_system_definition(string entity)
    {
        var definition = SeedDefinition(entity);
        var lookup = definition.Attributes.Single(a => a.LogicalName == CurrencySchema.CurrencyId)
            .Should().BeOfType<LookupAttributeMetadata>().Subject;
        var rate = definition.Attributes.Single(a => a.LogicalName == CurrencySchema.ExchangeRate)
            .Should().BeOfType<DecimalAttributeMetadata>().Subject;

        var expectedLookup = SystemAttributes.CurrencyLookup();
        var expectedRate = SystemAttributes.ExchangeRate();
        MetadataSerializer.SerializeAttribute(lookup).Should().Be(MetadataSerializer.SerializeAttribute(expectedLookup));
        MetadataSerializer.SerializeAttribute(rate).Should().Be(MetadataSerializer.SerializeAttribute(expectedRate));

        expectedLookup.Targets.Should().Equal(CurrencySchema.Table);
        expectedLookup.SchemaName.Should().Be("TransactionCurrencyId");
        expectedLookup.IsValidForCreate.Should().BeTrue();
        expectedLookup.IsValidForUpdate.Should().BeTrue();
        expectedLookup.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.None);
        expectedRate.Precision.Should().Be(CurrencySchema.RecordRateDecimals);
        (expectedRate.MinValue, expectedRate.MaxValue).Should().Be((CurrencySchema.MinRate, CurrencySchema.MaxRate));
        expectedRate.IsValidForCreate.Should().BeFalse();
        expectedRate.IsValidForUpdate.Should().BeFalse();

        var relationship = definition.ManyToOneRelationships.Single(r => r.ReferencingAttribute == CurrencySchema.CurrencyId);
        relationship.SchemaName.Should().Be(CurrencySchema.RelationshipName(entity));
        relationship.ReferencedEntity.Should().Be(CurrencySchema.Table);
        relationship.CascadeConfiguration.Delete.Should().Be(CascadeType.Restrict);
    }

    /// <summary>Pre-flight C6: completing a definition that already holds the pair keeps one of each.</summary>
    [Fact]
    public void Complete_keeps_a_seeded_currency_pair_once()
    {
        var definition = SeedDefinition("account");
        var completed = SystemAttributes.Complete(definition, definition.Attributes);
        completed.Count(a => a.LogicalName == CurrencySchema.CurrencyId).Should().Be(1);
        completed.Count(a => a.LogicalName == CurrencySchema.ExchangeRate).Should().Be(1);
        SystemAttributes.Complete(definition, completed).Select(a => a.LogicalName).Should().Equal(completed.Select(a => a.LogicalName));
    }

    /// <summary>
    /// The seed path gives a seed table neither the currency pair's relationship nor checks it (ruling X4: the seed file declares
    /// them, as account and contact do): every seed table with a money column declares the pair and its relationship.
    /// </summary>
    [Fact]
    public void Every_seed_table_with_money_declares_the_currency_pair()
    {
        const string prefix = "Xrmd.Metadata.Seed.";
        var names = typeof(SystemSeed).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[prefix.Length..^".json".Length]).ToList();
        names.Should().Contain(["account", "contact", CurrencySchema.Table]);
        foreach (var name in names)
        {
            var definition = SeedDefinition(name);
            if (CurrencySchema.MoneyAttributes(definition).Count == 0) continue;
            CurrencySchema.HasCurrencyColumns(definition).Should().BeTrue(name);
            (definition.ManyToOneRelationships ?? []).Should().Contain(r => r.ReferencingAttribute == CurrencySchema.CurrencyId
                && r.ReferencedEntity == CurrencySchema.Table && r.SchemaName == CurrencySchema.RelationshipName(name), name);
        }
    }
}
