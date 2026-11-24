using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata.Computed;

/// <summary>
/// The SDK twins of <see cref="ComputedColumnRules"/> (S.2b2; removed with the SDK view, S.2i). The helpers that change their argument
/// (<see cref="ApplyMask(EntityMetadata, AttributeMetadata, Func{string, EntityMetadata?}, IComputedColumns?)"/>,
/// <see cref="WithoutMask(AttributeMetadata)"/>, <see cref="SetFormula(AttributeMetadata, string?)"/>) keep their SDK bodies, and so do
/// the reads that run for every column or every translation (<see cref="IsComputed(AttributeMetadata?)"/>,
/// <see cref="SourceTypeMask(EntityMetadata, ComputedDefinition, Func{string, EntityMetadata?})"/>): they do not convert.
/// </summary>
public static partial class ComputedColumnRules
{
    /// <summary>The SDK twin of <see cref="IsComputed(ColumnDefinition?)"/>.</summary>
    public static bool IsComputed(AttributeMetadata? a) => a?.SourceType is Calculated or Formula;

    /// <summary>
    /// The SDK twin of <see cref="ValidateDefinition(TableDefinition, ColumnDefinition, ColumnDefinition?, Func{string, TableDefinition?}, IComputedColumns?, IRollupColumns?, Func{string, OneToManyRelationship?}?, int, int)"/>:
    /// the changes the rules make to the column are copied back into <paramref name="a"/>.
    /// </summary>
    /// <remarks>
    /// Test only (internal; ruling on the Task 4 review). Unlike the other mutating twins, which keep their SDK bodies and change
    /// the caller's objects in place, this one validates a fresh model copy (<see cref="SdkTwin.FreshModel"/>) and copies the
    /// resulting column properties back into <paramref name="a"/> by reflection (<see cref="SdkTwin.CopyInto(AttributeMetadata, ColumnDefinition)"/>).
    /// It is not reference-preserving: a nested object of <paramref name="a"/> (its option set, labels, formula definition) is
    /// replaced by a new instance, not changed in place.
    /// </remarks>
    /// <exception cref="XrmdValidationException">The definition is refused.</exception>
    internal static void ValidateDefinition(EntityMetadata entity, AttributeMetadata a, AttributeMetadata? current,
        Func<string, EntityMetadata?> entities, IComputedColumns? computed, IRollupColumns? rollups = null,
        Func<string, OneToManyRelationshipMetadata?>? relationships = null, int otherRollupsOnTable = 0, int otherRollupsInOrg = 0)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(entities);
        var (table, column) = SdkTwin.FreshModel(entity, a);
        ValidateDefinition(table, column, SdkTwin.ModelOrNull(current), SdkTwin.Memo(entities, (entity, table)), computed, rollups,
            relationships is null ? null : SdkTwin.Model(relationships), otherRollupsOnTable, otherRollupsInOrg);
        SdkTwin.CopyInto(a, column);
    }

    /// <summary>The SDK twin of <see cref="ApplyMask(TableDefinition, ColumnDefinition, Func{string, TableDefinition?}, IComputedColumns?)"/>.</summary>
    public static void ApplyMask(EntityMetadata entity, AttributeMetadata a, Func<string, EntityMetadata?> entities, IComputedColumns? computed)
    {
        if (a.GetType().GetProperty("SourceTypeMask") is null) return;
        if (!IsComputed(a)) { SetMask(a, 0); return; }
        try
        {
            SetMask(a, computed?.Translate(entity, a, entities).SourceTypeMask);
        }
        catch (XrmdValidationException)
        {
            SetMask(a, null);
        }
    }

    /// <summary>The SDK twin of <see cref="WithoutMask(ColumnDefinition)"/>.</summary>
    public static AttributeMetadata WithoutMask(AttributeMetadata a)
    {
        if (MaskOf(a) is null) return a;
        var copy = MetadataSerializer.Clone(a);
        SetMask(copy, null);
        return copy;
    }

    private static void SetMask(AttributeMetadata a, int? mask) => SdkProperty.Set(a, "SourceTypeMask", mask);

    /// <summary>The SDK twin of <see cref="SourceTypeMask(TableDefinition, ComputedDefinition, Func{string, TableDefinition?})"/>.</summary>
    public static int SourceTypeMask(EntityMetadata entity, ComputedDefinition definition, Func<string, EntityMetadata?> entities)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(definition);
        var mask = 0;
        foreach (var source in definition.Sources)
        {
            var owner = source.Lookup is null ? entity : ParentOf(entity, source.Lookup, entities);
            var column = owner?.Attributes?.FirstOrDefault(x => x.LogicalName == source.Attribute);
            mask |= column is not null && (column.SourceType == RollupColumns.Rollup || RollupColumns.IsCompanion(column)) ? RollupColumn
                : column?.SourceType is Calculated or Formula ? ChainedColumn
                : source.Lookup is null ? OwnColumn : ParentColumn;
        }
        return mask;
    }

    private static EntityMetadata? ParentOf(EntityMetadata entity, string lookup, Func<string, EntityMetadata?> entities) =>
        entity.Attributes?.FirstOrDefault(x => x.LogicalName == lookup) is LookupAttributeMetadata { Targets: [var target] }
            ? entities(target)
            : null;

    /// <summary>The SDK twin of <see cref="Dependents(string, string, IEnumerable{TableDefinition}, IComputedColumns, string?)"/>.</summary>
    public static IReadOnlyList<string> Dependents(string entity, string attribute, IEnumerable<EntityMetadata> all, IComputedColumns computed,
        string? replaced = null)
    {
        ArgumentNullException.ThrowIfNull(all);
        return Dependents(entity, attribute, SdkTwin.Model(all).ToList(), computed, replaced);
    }

    /// <summary>The SDK twin of <see cref="DeleteRefusal(IReadOnlyList{string}, IEnumerable{TableDefinition})"/>.</summary>
    public static string? DeleteRefusal(IReadOnlyList<string> dependents, IEnumerable<EntityMetadata> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        return DeleteRefusal(dependents, SdkTwin.Model(all).ToList());
    }

    /// <summary>The SDK twin of <see cref="DependentsOfTable(TableDefinition, IEnumerable{TableDefinition}, IComputedColumns)"/>.</summary>
    public static IReadOnlyList<string> DependentsOfTable(EntityMetadata entity, IEnumerable<EntityMetadata> all, IComputedColumns computed)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(all);
        return DependentsOfTable(SdkTwin.Model(entity), SdkTwin.Model(all).ToList(), computed);
    }

    /// <summary>The SDK twin of <see cref="FormulaOf(ColumnDefinition)"/>.</summary>
    public static string? FormulaOf(AttributeMetadata a) =>
        a.GetType().GetProperty("FormulaDefinition")?.GetValue(a) as string;

    /// <summary>The SDK twin of <see cref="SetFormula(ColumnDefinition, string?)"/>.</summary>
    public static void SetFormula(AttributeMetadata a, string? formula) => SdkProperty.Set(a, "FormulaDefinition", formula);

    /// <summary>The SDK twin of <see cref="MaskOf(ColumnDefinition)"/>.</summary>
    public static int? MaskOf(AttributeMetadata a) => a.GetType().GetProperty("SourceTypeMask")?.GetValue(a) as int?;
}

/// <summary>The SDK twins of <see cref="RollupColumnRules"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class RollupColumnRules
{
    /// <summary>The SDK twin of <see cref="Dependents(string, string, IEnumerable{TableDefinition}, IRollupColumns, Func{string, OneToManyRelationship?}, string?)"/>.</summary>
    public static IReadOnlyList<string> Dependents(string entity, string attribute, IEnumerable<EntityMetadata> all, IRollupColumns rollups,
        Func<string, OneToManyRelationshipMetadata?> relationships, string? replaced = null)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(relationships);
        return Dependents(entity, attribute, SdkTwin.Model(all).ToList(), rollups, SdkTwin.Model(relationships), replaced);
    }

    /// <summary>The SDK twin of <see cref="DependentsOfRelationship(string, IEnumerable{TableDefinition}, IRollupColumns, Func{string, OneToManyRelationship?})"/>.</summary>
    public static IReadOnlyList<string> DependentsOfRelationship(string schemaName, IEnumerable<EntityMetadata> all, IRollupColumns rollups,
        Func<string, OneToManyRelationshipMetadata?> relationships)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(relationships);
        return DependentsOfRelationship(schemaName, SdkTwin.Model(all).ToList(), rollups, SdkTwin.Model(relationships));
    }

    /// <summary>The SDK twin of <see cref="DependentsOfTable(string, IEnumerable{TableDefinition}, IRollupColumns, Func{string, OneToManyRelationship?})"/>.</summary>
    public static IReadOnlyList<string> DependentsOfTable(string entity, IEnumerable<EntityMetadata> all, IRollupColumns rollups,
        Func<string, OneToManyRelationshipMetadata?> relationships)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(relationships);
        return DependentsOfTable(entity, SdkTwin.Model(all).ToList(), rollups, SdkTwin.Model(relationships));
    }

    /// <summary>The SDK twin of <see cref="DataTypeNotValid(ColumnDefinition, string)"/>.</summary>
    public static Xrmd.Core.Errors.XrmdFaultException DataTypeNotValid(AttributeMetadata a, string where)
    {
        ArgumentNullException.ThrowIfNull(a);
        return DataTypeNotValid(SdkTwin.Model(a), where);
    }
}

/// <summary>The SDK twins of <see cref="RollupColumns"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class RollupColumns
{
    /// <summary>The SDK twin of <see cref="Companions(ColumnDefinition)"/>: fresh SDK companions the caller owns.</summary>
    public static IReadOnlyList<AttributeMetadata> Companions(AttributeMetadata rollup)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        return SdkTwin.Sdk<AttributeMetadata>(Companions(SdkTwin.Model(rollup)));
    }

    /// <summary>The SDK twin of <see cref="InitialValue(ColumnDefinition, RollupDefinition)"/>.</summary>
    public static object? InitialValue(AttributeMetadata rollup, RollupDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        return InitialValue(SdkTwin.Model(rollup), definition);
    }

    /// <summary>
    /// The value of <paramref name="rollup"/> over an empty set, as Dataverse writes it with state 1 (parity run, plan 2.12b Task 7):
    /// 0 for COUNT, SUM and AVG (a <see cref="Microsoft.Xrm.Sdk.Money"/> for Currency), null for MIN and MAX. An SDK value, so it
    /// has no model twin until record values leave the SDK.
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

    /// <summary>The SDK twin of <see cref="IsRollup(ColumnDefinition)"/>.</summary>
    public static bool IsRollup(AttributeMetadata a) =>
        a is not null && a.SourceType == Rollup && a is not MoneyAttributeMetadata { IsBaseCurrency: true };

    /// <summary>The SDK twin of <see cref="IsRollupValue(ColumnDefinition, Func{string, ColumnDefinition?})"/>.</summary>
    public static bool IsRollupValue(AttributeMetadata a, Func<string, AttributeMetadata?> column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (a is null) return false;
        if (IsRollup(a)) return true;
        if (IsCompanion(a)) return column(a.AttributeOf) is { } main && IsRollup(main);
        return a is MoneyAttributeMetadata { IsBaseCurrency: true } && a.LogicalName is { } name
               && name.EndsWith(TypeMapping.BaseColumnSuffix, StringComparison.Ordinal)
               && column(name[..^TypeMapping.BaseColumnSuffix.Length]) is { } money && IsRollup(money);
    }

    /// <summary>The SDK twin of <see cref="IsCompanion(ColumnDefinition)"/>.</summary>
    public static bool IsCompanion(AttributeMetadata a) =>
        a is not null && a.AttributeOf is { } main && a.LinkedAttributeId is not null
        && ((a is DateTimeAttributeMetadata && a.LogicalName == main + DateSuffix)
            || (a is IntegerAttributeMetadata && a.LogicalName == main + StateSuffix));
}

/// <summary>The SDK twins of <see cref="ComputedOrder"/> (S.2b2; removed with the SDK view, S.2i).</summary>
public static partial class ComputedOrder
{
    /// <summary>The SDK twin of <see cref="IsComputed(ColumnDefinition)"/>.</summary>
    public static bool IsComputed(AttributeMetadata a) =>
        a is not null && (ComputedColumnRules.IsComputed(a) || RollupColumns.IsRollup(a));

    /// <summary>The SDK twin of <see cref="Order(IReadOnlyList{ValueTuple{string, string}}, Func{string, TableDefinition?}, Func{string, OneToManyRelationship?}, IComputedColumns?, IRollupColumns?, bool)"/>.</summary>
    public static IReadOnlyList<(string Entity, string Attribute)> Order(IReadOnlyList<(string Entity, string Attribute)> columns,
        Func<string, EntityMetadata?> entities, Func<string, OneToManyRelationshipMetadata?> relationships, IComputedColumns? computed,
        IRollupColumns? rollups, bool readersLast)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(relationships);
        return Order(columns, SdkTwin.Memo(entities), SdkTwin.Model(relationships), computed, rollups, readersLast);
    }
}
