using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The names and limits of Dataverse multi-currency (plan 2.10): the <c>transactioncurrency</c> table, the per-user
/// <c>usersettings</c> table, the currency columns of <c>organization</c> and the pair <c>transactioncurrencyid</c> +
/// <c>exchangerate</c> of a table with money columns.
/// </summary>
public static partial class CurrencySchema
{
    /// <summary>The table of currencies.</summary>
    public const string Table = "transactioncurrency";

    /// <summary>The primary key of <see cref="Table"/>, also the lookup of a record (and of <c>usersettings</c>) to its currency.</summary>
    public const string CurrencyId = "transactioncurrencyid";

    /// <summary>The rate of a currency (<see cref="Table"/>) and the rate stored on a record when it was written.</summary>
    public const string ExchangeRate = "exchangerate";

    /// <summary>The per-user settings table (primary key <c>systemuserid</c>).</summary>
    public const string UserSettings = "usersettings";

    /// <summary>The organization's lookup to its base currency.</summary>
    public const string BaseCurrencyId = "basecurrencyid";

    /// <summary>The organization's pricing precision (money columns with <c>PrecisionSource</c> 1).</summary>
    public const string PricingPrecision = "pricingdecimalprecision";

    /// <summary>The organization's choice between the symbol (0) and the ISO code (1) in formatted money.</summary>
    public const string DisplayOption = "currencydisplayoption";

    /// <summary>The organization's placement of the symbol in formatted money (0 <c>$123</c>, 1 <c>123$</c>, 2 <c>$ 123</c>, 3 <c>123 $</c>).</summary>
    public const string FormatCode = "currencyformatcode";

    /// <summary>The organization's copy of the base currency's symbol (read-only).</summary>
    public const string BaseSymbol = "basecurrencysymbol";

    /// <summary>The organization's copy of the base currency's precision (read-only).</summary>
    public const string BasePrecision = "basecurrencyprecision";

    /// <summary>The ISO 4217 (or custom) code of a currency.</summary>
    public const string IsoCode = "isocurrencycode";

    /// <summary>The symbol of a currency.</summary>
    public const string Symbol = "currencysymbol";

    /// <summary>The number of decimals of a currency.</summary>
    public const string Precision = "currencyprecision";

    /// <summary>The type of a currency: 0 System (an ISO 4217 code), 1 Custom.</summary>
    public const string CurrencyType = "currencytype";

    /// <summary>
    /// The range of a currency's precision and of the organization's pricing precision that xrmverse accepts: money columns are
    /// stored with scale 4 (decision D2: more decimals are refused, not silently rounded).
    /// </summary>
    public const int MinPrecision = 0, MaxPrecision = 4;

    /// <summary>The range of an exchange rate on a record (observed: 1E-10 to 1E11), also the one xrmverse accepts on a currency.</summary>
    public const decimal MinRate = 0.0000000001m, MaxRate = 100000000000m;

    /// <summary>The decimals of the exchange rate stored on a record (its column has Precision 10).</summary>
    public const int RecordRateDecimals = 10;

    /// <summary>The precision of a <c>{money}_base</c> attribute (observed: Accuracy 4, the column scale).</summary>
    public const int BaseMoneyPrecision = 4;

    /// <summary>The <c>PrecisionSource</c> of a <c>{money}_base</c> attribute (2: the currency's precision; observed).</summary>
    public const int BaseMoneyPrecisionSource = 2;

    /// <summary>The range of a <c>{money}_base</c> value (observed: ±922337203685477).</summary>
    public const decimal MinBaseMoney = -922337203685477m, MaxBaseMoney = 922337203685477m;

    /// <summary>The pricing precision the seed gives an organization (from memory: the plan 2.10 checklist verifies it).</summary>
    public const int DefaultPricingPrecision = 2;

    /// <summary>
    /// The name of the relationship from <see cref="Table"/> to the currency lookup of <paramref name="entityLogicalName"/>:
    /// Learn names those of account and contact in lower case, an environment names the others <c>TransactionCurrency_{table}</c>.
    /// The pointer and the four standard activities have Learn's names with their SchemaName (plan 2.11, pre-flight C6); a custom
    /// activity keeps the logical-name rule.
    /// </summary>
    public static string RelationshipName(string entityLogicalName) => entityLogicalName switch
    {
        "account" => "transactioncurrency_account",
        "contact" => "transactioncurrency_contact",
        ActivitySchema.Pointer => "TransactionCurrency_ActivityPointer",
        ActivitySchema.Email => "TransactionCurrency_Email",
        ActivitySchema.Task => "TransactionCurrency_Task",
        ActivitySchema.PhoneCall => "TransactionCurrency_PhoneCall",
        ActivitySchema.Appointment => "TransactionCurrency_Appointment",
        _ => "TransactionCurrency_" + entityLogicalName,
    };

    /// <summary>True for the names of the currency pair (<see cref="CurrencyId"/>, <see cref="ExchangeRate"/>).</summary>
    public static bool IsPairName(string? attributeLogicalName) => attributeLogicalName is CurrencyId or ExchangeRate;

    /// <summary>
    /// True when <paramref name="entity"/> has the currency pair: the lookup <see cref="CurrencyId"/> to <see cref="Table"/> and the
    /// decimal <see cref="ExchangeRate"/>. A table gets it with its first money column (ruling X4); account and contact have it from
    /// their seed definitions. <see cref="Table"/> itself (whose own key and rate have these names) has no pair.
    /// </summary>
    public static bool HasCurrencyColumns(TableDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.LogicalName == Table) return false;
        var attributes = entity.Attributes ?? [];
        return attributes.Any(a => a is LookupColumn { LogicalName: CurrencyId } lookup && (lookup.Targets ?? []).Contains(Table))
               && attributes.Any(a => a is DecimalColumn { LogicalName: ExchangeRate });
    }

    /// <summary>True when <paramref name="attribute"/> is one of the currency pair of <paramref name="entity"/> (a generated system attribute).</summary>
    public static bool IsPairAttribute(TableDefinition entity, ColumnDefinition attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return IsPairName(attribute.LogicalName) && HasCurrencyColumns(entity);
    }

    /// <summary>The money attributes of <paramref name="entity"/> that hold a value in the record's currency (not the <c>_base</c> ones).</summary>
    public static IReadOnlyList<MoneyColumn> MoneyAttributes(TableDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return (entity.Attributes ?? []).OfType<MoneyColumn>().Where(m => m.IsBaseCurrency != true).ToList();
    }
}
