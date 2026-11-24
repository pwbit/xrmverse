using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>The SDK twins of <see cref="SystemAttributes"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class SystemAttributes
{
    /// <summary>The SDK twin of <see cref="PrimaryIdName(TableDefinition)"/>.</summary>
    public static string PrimaryIdName(EntityMetadata e) => e.PrimaryIdAttribute ?? PrimaryIdName(e.LogicalName);

    /// <summary>
    /// The SDK twin of <see cref="Complete(TableDefinition, IEnumerable{ColumnDefinition})"/>: the given attributes come back as the
    /// same instances, the generated ones as new SDK objects.
    /// </summary>
    public static IReadOnlyList<AttributeMetadata> Complete(EntityMetadata e, IEnumerable<AttributeMetadata> attrs)
    {
        var given = attrs.ToList();
        var models = given.Select(a => SdkMetadataBridge.ToModel(a)).ToList();
        var originals = new Dictionary<ColumnDefinition, AttributeMetadata>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < models.Count; i++) originals[models[i]] = given[i];
        return Complete(SdkTwin.Model(e), models).Select(c => originals.TryGetValue(c, out var original) ? original : SdkTwin.Sdk(c)).ToList();
    }

    /// <summary>The SDK twin of <see cref="PrimaryIdColumn"/>.</summary>
    public static UniqueIdentifierAttributeMetadata PrimaryId(string entityLogicalName, string? logicalName = null) =>
        SdkTwin.Sdk<UniqueIdentifierAttributeMetadata>(PrimaryIdColumn(entityLogicalName, logicalName));

    /// <summary>The SDK twin of <see cref="OwnerColumn"/>.</summary>
    public static LookupAttributeMetadata Owner() => SdkTwin.Sdk<LookupAttributeMetadata>(OwnerColumn());

    /// <summary>The SDK twin of <see cref="MoneyBase(MoneyColumn)"/>.</summary>
    public static MoneyAttributeMetadata MoneyBase(MoneyAttributeMetadata money) =>
        SdkTwin.Sdk<MoneyAttributeMetadata>(MoneyBase((MoneyColumn)SdkTwin.Model(money)));

    /// <summary>The SDK twin of <see cref="CurrencyLookupColumn"/>.</summary>
    public static LookupAttributeMetadata CurrencyLookup() => SdkTwin.Sdk<LookupAttributeMetadata>(CurrencyLookupColumn());

    /// <summary>The SDK twin of <see cref="ExchangeRateColumn"/>.</summary>
    public static DecimalAttributeMetadata ExchangeRate() => SdkTwin.Sdk<DecimalAttributeMetadata>(ExchangeRateColumn());

    /// <summary>The SDK twin of <see cref="CreateLabelSet"/>.</summary>
    public static Label CreateLabel(string text, int languageCode) => SdkTwin.Sdk(CreateLabelSet(text, languageCode));

    /// <summary>
    /// The SDK twin of <see cref="SetBaseMoneyMetadata(MoneyColumn)"/>; it changes <paramref name="baseMoney"/> in place, so it keeps its
    /// SDK body (S.2b2 ruling R3).
    /// </summary>
    public static bool SetBaseMoneyMetadata(MoneyAttributeMetadata baseMoney)
    {
        ArgumentNullException.ThrowIfNull(baseMoney);
        var changed = baseMoney.Precision != CurrencySchema.BaseMoneyPrecision
                      || baseMoney.PrecisionSource != CurrencySchema.BaseMoneyPrecisionSource
                      || baseMoney.MinValue != (double)CurrencySchema.MinBaseMoney
                      || baseMoney.MaxValue != (double)CurrencySchema.MaxBaseMoney;
        baseMoney.Precision = CurrencySchema.BaseMoneyPrecision;
        baseMoney.PrecisionSource = CurrencySchema.BaseMoneyPrecisionSource;
        baseMoney.MinValue = (double)CurrencySchema.MinBaseMoney;
        baseMoney.MaxValue = (double)CurrencySchema.MaxBaseMoney;
        return changed;
    }
}
