using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>The SDK twins of <see cref="ChangeTrackingSchema"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class ChangeTrackingSchema
{
    /// <summary>The SDK twin of <see cref="CanBeEnabled(TableDefinition)"/>.</summary>
    public static bool CanBeEnabled(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return CanBeEnabled(SdkTwin.Model(entity));
    }
}

/// <summary>The SDK twins of <see cref="CurrencyMath"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class CurrencyMath
{
    /// <summary>The SDK twin of <see cref="EffectivePrecision(MoneyColumn, int, int)"/>.</summary>
    public static int EffectivePrecision(MoneyAttributeMetadata a, int currencyPrecision, int pricingPrecision)
    {
        ArgumentNullException.ThrowIfNull(a);
        return EffectivePrecision((MoneyColumn)SdkTwin.Model(a), currencyPrecision, pricingPrecision);
    }
}

/// <summary>The SDK twins of <see cref="CurrencySchema"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class CurrencySchema
{
    /// <summary>The SDK twin of <see cref="HasCurrencyColumns(TableDefinition)"/>.</summary>
    public static bool HasCurrencyColumns(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return HasCurrencyColumns(SdkTwin.Model(entity));
    }

    /// <summary>The SDK twin of <see cref="IsPairAttribute(TableDefinition, ColumnDefinition)"/>.</summary>
    public static bool IsPairAttribute(EntityMetadata entity, AttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return IsPairName(attribute.LogicalName) && HasCurrencyColumns(entity);
    }

    /// <summary>
    /// The SDK twin of <see cref="MoneyAttributes(TableDefinition)"/>: the money attributes of <paramref name="entity"/> itself (same
    /// instances), in their order.
    /// </summary>
    public static IReadOnlyList<MoneyAttributeMetadata> MoneyAttributes(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var names = MoneyAttributes(SdkTwin.Model(entity)).Select(m => m.LogicalName).ToHashSet(StringComparer.Ordinal);
        return (entity.Attributes ?? []).OfType<MoneyAttributeMetadata>().Where(m => names.Contains(m.LogicalName)).ToList();
    }
}

/// <summary>The SDK twins of <see cref="CustomerColumns"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class CustomerColumns
{
    /// <summary>The SDK twin of <see cref="IsCustomer(ColumnDefinition?)"/>.</summary>
    public static bool IsCustomer(AttributeMetadata? attribute) => attribute is LookupAttributeMetadata { AttributeType: AttributeTypeCode.Customer };
}

/// <summary>The SDK twins of <see cref="DuplicateDetectionSchema"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class DuplicateDetectionSchema
{
    /// <summary>The SDK twin of <see cref="CanBeEnabled(TableDefinition)"/>.</summary>
    public static bool CanBeEnabled(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return CanBeEnabled(SdkTwin.Model(entity));
    }

    /// <summary>The SDK twin of <see cref="IsRecordLookup(ColumnDefinition)"/>.</summary>
    public static bool IsRecordLookup(AttributeMetadata a) =>
        a is LookupAttributeMetadata && a.EntityLogicalName == Record && a.LogicalName is BaseRecordId or DuplicateRecordId;
}

/// <summary>The SDK twins of <see cref="MailSchema"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class MailSchema
{
    /// <summary>The SDK twin of <see cref="IsSecret(ColumnDefinition)"/>.</summary>
    public static bool IsSecret(AttributeMetadata a) =>
        a.EntityLogicalName is { } table && SecretColumns.ContainsKey(table + "." + a.LogicalName);
}

/// <summary>The SDK twins of <see cref="OptionColor"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class OptionColor
{
    /// <summary>The SDK twin of <see cref="Demand(string, ChoiceOption)"/>.</summary>
    public static void Demand(string owner, OptionMetadata option)
    {
        ArgumentNullException.ThrowIfNull(option);
        Demand(owner, new ChoiceOption { Value = option.Value, Color = option.Color });
    }
}

/// <summary>The SDK twins of <see cref="PostSchema"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class PostSchema
{
    /// <summary>The SDK twin of <see cref="PostRegardingsRelationship(TableDefinition)"/>.</summary>
    public static OneToManyRelationshipMetadata PostRegardingsRelationship(EntityMetadata target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return SdkTwin.Sdk(PostRegardingsRelationship(SdkTwin.Model(target)));
    }
}

/// <summary>The SDK twins of <see cref="QueueSchema"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class QueueSchema
{
    /// <summary>The SDK twin of <see cref="SettingManagedFlag"/>.</summary>
    public static BooleanManagedProperty SettingFlag(bool value) => SdkTwin.Sdk(SettingManagedFlag(value));

    /// <summary>The SDK twin of <see cref="ItemRelationship(TableDefinition)"/>.</summary>
    public static OneToManyRelationshipMetadata ItemRelationship(EntityMetadata target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return SdkTwin.Sdk(ItemRelationship(SdkTwin.Model(target)));
    }
}

/// <summary>The SDK twins of <see cref="PublisherPrefixRule"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public sealed partial class PublisherPrefixRule
{
    /// <summary>The SDK twin of <see cref="DemandEntity(TableDefinition, ColumnDefinition?, IEnumerable{string}?)"/>.</summary>
    public void DemandEntity(EntityMetadata entity, AttributeMetadata? primaryName, IEnumerable<string>? extraPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        DemandEntity(SdkTwin.Model(entity), SdkTwin.ModelOrNull(primaryName), extraPrefixes);
    }
}

/// <summary>The SDK twins of <see cref="UserDateFormat"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public sealed partial record UserDateFormat
{
    /// <summary>The SDK twin of <see cref="Format(DateTimeColumn, DateTime)"/>.</summary>
    public string Format(DateTimeAttributeMetadata a, DateTime value)
    {
        ArgumentNullException.ThrowIfNull(a);
        return Format((DateTimeColumn)SdkTwin.Model(a), value);
    }
}
