using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata.Computed;

/// <summary>
/// Rollup columns (<c>SourceType</c> 2, plan 2.12b) and the companions the server generates for each (ruling R2):
/// <c>&lt;column&gt;_date</c> (Date and time, when the value was last calculated) and <c>&lt;column&gt;_state</c> (Whole number,
/// a <see cref="RollupState"/>), each with a column of its own, <c>AttributeOf</c> the rollup and <c>LinkedAttributeId</c> its
/// MetadataId, read-only. A Currency rollup also has its ordinary <c>_base</c>.
/// </summary>
public static class RollupColumns
{
    /// <summary><c>SourceType</c> of a rollup column.</summary>
    public const int Rollup = 2;

    /// <summary>The suffixes of the companions (logical names; the schema names take <c>_Date</c> and <c>_State</c>).</summary>
    public const string DateSuffix = "_date", StateSuffix = "_state";

    /// <summary>The companions of <paramref name="rollup"/> (its MetadataId set): <c>_date</c> and <c>_state</c>.</summary>
    public static IReadOnlyList<AttributeMetadata> Companions(AttributeMetadata rollup)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        var schema = rollup.SchemaName ?? rollup.LogicalName;
        var label = rollup.DisplayName?.UserLocalizedLabel;

        AttributeMetadata Companion(AttributeMetadata a, string suffix, string schemaSuffix, string labelSuffix)
        {
            a.LogicalName = rollup.LogicalName + suffix;
            a.SchemaName = schema + schemaSuffix;
            a.DisplayName = label is null ? null : SystemAttributes.CreateLabel($"{label.Label} ({labelSuffix})", label.LanguageCode);
            a.IsValidForCreate = false;
            a.IsValidForUpdate = false;
            a.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);
            SdkProperty.Set(a, nameof(AttributeMetadata.IsCustomAttribute), rollup.IsCustomAttribute);
            SdkProperty.Set(a, nameof(AttributeMetadata.AttributeOf), rollup.LogicalName);
            SdkProperty.Set(a, nameof(AttributeMetadata.LinkedAttributeId), rollup.MetadataId);
            return a;
        }

        return
        [
            Companion(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { DateTimeBehavior = DateTimeBehavior.UserLocal },
                DateSuffix, "_Date", "calculated on"),
            Companion(new IntegerAttributeMetadata
            {
                Format = IntegerFormat.None,
                MinValue = (int)RollupState.NotCalculated, MaxValue = (int)RollupState.CurrencyMissing,
            }, StateSuffix, "_State", "calculation state"),
        ];
    }

    /// <summary>
    /// The value a row holds before <paramref name="rollup"/> is first calculated (ruling R7, as the parity export shows it): 0 for a
    /// COUNT and for a SUM that is neither over the hierarchy nor Currency (an <c>int</c> or a <c>decimal</c>, the column's type),
    /// else null. Its <c>_state</c> is then 0 (NotCalculated) and its <c>_date</c> the time the row or the column was made.
    /// </summary>
    public static object? InitialValue(AttributeMetadata rollup, RollupDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        ArgumentNullException.ThrowIfNull(definition);
        var zero = definition.Aggregate == RollupAggregate.Count
                   || (definition.Aggregate == RollupAggregate.Sum && definition.HierarchicalRelationship is null && rollup is not MoneyAttributeMetadata);
        if (!zero) return null;
        return rollup switch
        {
            IntegerAttributeMetadata => 0,
            DecimalAttributeMetadata => 0m,
            _ => null,
        };
    }

    /// <summary>
    /// The value of <paramref name="rollup"/> over an empty set, as Dataverse writes it with state 1 (parity run, plan 2.12b Task 7):
    /// 0 for COUNT, SUM and AVG (a <see cref="Money"/> for Currency), null for MIN and MAX.
    /// </summary>
    public static object? EmptySetValue(AttributeMetadata rollup, RollupDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Aggregate is RollupAggregate.Min or RollupAggregate.Max) return null;
        return rollup switch
        {
            IntegerAttributeMetadata => 0,
            DecimalAttributeMetadata => 0m,
            MoneyAttributeMetadata => new Microsoft.Xrm.Sdk.Money(0m),
            _ => null,
        };
    }

    /// <summary>True for a rollup column itself (not its <c>_base</c>, which a solution file also marks with <c>SourceType</c> 2).</summary>
    public static bool IsRollup(AttributeMetadata a) =>
        a is not null && a.SourceType == Rollup && a is not MoneyAttributeMetadata { IsBaseCurrency: true };

    /// <summary>
    /// True for a column whose value only a rollup calculation writes (ruling R11): a rollup column, one of its companions
    /// (<c>_date</c>, <c>_state</c>) or the <c>_base</c> of a Currency rollup. <paramref name="column"/> finds a column of the same
    /// table by logical name (the main column of a companion or a base).
    /// </summary>
    public static bool IsRollupValue(AttributeMetadata a, Func<string, AttributeMetadata?> column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (a is null) return false;
        if (IsRollup(a)) return true;
        if (IsCompanion(a)) return column(a.AttributeOf!) is { } main && IsRollup(main);
        return a is MoneyAttributeMetadata { IsBaseCurrency: true } && a.LogicalName is { } name
               && name.EndsWith(TypeMapping.BaseColumnSuffix, StringComparison.Ordinal)
               && column(name[..^TypeMapping.BaseColumnSuffix.Length]) is { } money && IsRollup(money);
    }

    /// <summary>True for a companion made by <see cref="Companions"/> (by its shape: <c>AttributeOf</c>, link, suffix and type).</summary>
    /// <remarks>
    /// The test looks at the column alone, not at its table: it assumes that no other generated column has an <c>AttributeOf</c>, a
    /// <c>LinkedAttributeId</c> and the name <c>&lt;AttributeOf&gt;_date</c> (Date and time) or <c>&lt;AttributeOf&gt;_state</c> (Whole
    /// number). True today: the only other linked companions are those of file and image columns (<c>_name</c>, <c>id</c>,
    /// <c>_timestamp</c>, <c>_url</c>), and a caller cannot set <c>AttributeOf</c>. Where the main column is at hand, also check it is a
    /// rollup (as the metadata store does before refusing a change of a companion).
    /// </remarks>
    public static bool IsCompanion(AttributeMetadata a) =>
        a is not null && a.AttributeOf is { } main && a.LinkedAttributeId is not null
        && ((a is DateTimeAttributeMetadata && a.LogicalName == main + DateSuffix)
            || (a is IntegerAttributeMetadata && a.LogicalName == main + StateSuffix));
}
