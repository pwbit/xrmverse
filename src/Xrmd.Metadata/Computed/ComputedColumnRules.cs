using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;

namespace Xrmd.Metadata.Computed;

/// <summary>The metadata rules of calculated columns (plan 2.12a, Global Constraints and rulings K4, K12).</summary>
public static class ComputedColumnRules
{
    /// <summary><c>SourceType</c> of a calculated column.</summary>
    public const int Calculated = 1;

    /// <summary><c>SourceType</c> of a formula column (Power Fx, plan 2.12c).</summary>
    public const int Formula = 3;

    /// <summary><c>SourceType</c> of a prompt column (not supported).</summary>
    public const int Prompt = 4;

    private const int OwnColumn = 1, ParentColumn = 2, ChainedColumn = 8, RollupColumn = 16;

    /// <summary>
    /// True for a column the server computes on read, with no SQL column of its own and never written: a calculated
    /// (<c>SourceType</c> 1) or formula (3) column. A rollup (2) is stored by its jobs and is not one.
    /// </summary>
    public static bool IsComputed(AttributeMetadata? a) => a?.SourceType is Calculated or Formula;

    /// <summary>
    /// Checks the definition of <paramref name="a"/> (which is changed in place: read-only flags, <c>SourceTypeMask</c>). With
    /// <paramref name="current"/> (an update) the <c>SourceType</c> cannot change. Does nothing for a simple column. A rollup column
    /// (<c>SourceType</c> 2) is checked by <see cref="RollupColumnRules.ValidateDefinition"/> with <paramref name="rollups"/>,
    /// <paramref name="relationships"/> and the counts of the other rollups of the table and of the organization.
    /// </summary>
    /// <exception cref="XrmdValidationException">The definition is refused.</exception>
    /// <exception cref="XrmdFaultException">400: a rollup definition is refused (its rollup error code).</exception>
    public static void ValidateDefinition(EntityMetadata entity, AttributeMetadata a, AttributeMetadata? current,
        Func<string, EntityMetadata?> entities, IComputedColumns? computed, IRollupColumns? rollups = null,
        Func<string, OneToManyRelationshipMetadata?>? relationships = null, int otherRollupsOnTable = 0, int otherRollupsInOrg = 0)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(a);
        var where = $"'{entity.LogicalName}.{a.LogicalName}'";
        var sourceType = a.SourceType ?? 0;
        if (current is not null && a.SourceType is { } requested && requested != (current.SourceType ?? 0))
            throw new XrmdValidationException($"The SourceType of a column cannot change: {where}.");
        switch (sourceType)
        {
            case 0:
                if (!string.IsNullOrWhiteSpace(FormulaOf(a)))
                    throw new XrmdValidationException($"A FormulaDefinition needs SourceType 1 (calculated), 2 (rollup) or 3 (formula): {where}.");
                ApplyMask(entity, a, entities, computed);
                return;
            case Calculated:
                break;
            case RollupColumns.Rollup:
                RollupColumnRules.ValidateDefinition(entity, a, current, entities, relationships ?? (_ => null), rollups,
                    otherRollupsOnTable, otherRollupsInOrg, computed);
                return;
            case Formula:
                FormulaColumnRules.Validate(entity, a, current, entities, computed);
                return;
            case Prompt:
                throw new XrmdValidationException($"Prompt columns are not supported: {where}.");
            default:
                throw new XrmdValidationException($"SourceType {sourceType} is not valid: {where}.");
        }

        if (computed is null) throw new XrmdValidationException("Calculated columns are not available on this server.");
        // A lookup reads its target's name from the stored primary name column (final review I3): a calculated one has none.
        if (a.LogicalName == entity.PrimaryNameAttribute)
            throw new XrmdValidationException($"The primary name column of a table cannot be a calculated column: {where}.");
        if (!IsAllowedType(a))
            throw new XrmdValidationException($"A calculated column cannot be of type {a.AttributeType}: {where}.");
        if (HasLimits(a))
            throw new XrmdValidationException($"MinValue and MaxValue cannot be set on a calculated column: {where}.");

        // A calculated column is computed by the server: it is never written, and it cannot be required.
        a.IsValidForCreate = false;
        a.IsValidForUpdate = false;
        a.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);

        var definition = computed.Translate(entity, a, entities);
        SetMask(a, definition.SourceTypeMask);
    }

    /// <summary>
    /// Sets the <c>SourceTypeMask</c> of <paramref name="a"/> as a read computes it (ruling K12): 0 for a simple column of a type that
    /// has the property, the mask of its <see cref="ComputedDefinition"/> for a calculated or formula one, null when the definition cannot be
    /// translated (or there is no translator). Types without the property are left alone. The value lives in the cache only: stores
    /// clear it (<see cref="WithoutMask"/>).
    /// </summary>
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

    /// <summary><paramref name="a"/> without its <c>SourceTypeMask</c> (a copy when it has one): what a store writes.</summary>
    public static AttributeMetadata WithoutMask(AttributeMetadata a)
    {
        if (MaskOf(a) is null) return a;
        var copy = MetadataSerializer.Clone(a);
        SetMask(copy, null);
        return copy;
    }

    private static void SetMask(AttributeMetadata a, int? mask) => SdkProperty.Set(a, "SourceTypeMask", mask);

    /// <summary>
    /// The <c>SourceTypeMask</c> of a calculated column: 1 when it reads a simple column of its own record, 2 a simple column of the
    /// parent record, 8 another calculated column (of either record), 16 a rollup column or one of its companions (plan 2.12b,
    /// ruling R13); the bits are combined.
    /// </summary>
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

    /// <summary>
    /// The calculated and formula columns (as <c>table.column</c>, ordered) that read <paramref name="attribute"/> of
    /// <paramref name="entity"/>, or name it as the lookup they follow to a parent record. The column itself is not translated (a column never reads itself), so
    /// a calculated column whose stored definition can no longer be read can still be deleted or given a new definition. Nor is
    /// <paramref name="replaced"/> (<c>table.column</c>), a column whose definition an update is replacing.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>0x80040203</c>: another calculated column's definition cannot be read.</exception>
    public static IReadOnlyList<string> Dependents(string entity, string attribute, IEnumerable<EntityMetadata> all, IComputedColumns computed,
        string? replaced = null) =>
        DependentsCore(entity, new HashSet<string>(StringComparer.Ordinal) { attribute }, all, computed,
            (table, column) => (table == entity && column == attribute) || (replaced is not null && replaced == $"{table}.{column}"));

    /// <summary>
    /// The text of the refusal to delete a component that <paramref name="dependents"/> (<c>table.column</c>, from
    /// <see cref="Dependents"/> or <see cref="DependentsOfTable"/>) read, or null when there are none. Calculated dependents keep the
    /// text of plan 2.12a (ruling K4); formula dependents are named as formula columns (plan 2.12c).
    /// </summary>
    public static string? DeleteRefusal(IReadOnlyList<string> dependents, IEnumerable<EntityMetadata> all)
    {
        ArgumentNullException.ThrowIfNull(dependents);
        ArgumentNullException.ThrowIfNull(all);
        if (dependents.Count == 0) return null;
        var tables = all.GroupBy(e => e.LogicalName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        bool IsFormula(string name)
        {
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            return dot > 0 && tables.TryGetValue(name[..dot], out var table)
                && table.Attributes?.FirstOrDefault(a => a.LogicalName == name[(dot + 1)..])?.SourceType == Formula;
        }
        static string Names(IEnumerable<string> names) => "'" + string.Join("', '", names) + "'";
        var calculated = dependents.Where(d => !IsFormula(d)).ToList();
        var formulas = dependents.Where(IsFormula).ToList();
        var parts = new List<string>(2);
        if (calculated.Count > 0) parts.Add($"calculated column {Names(calculated)}");
        if (formulas.Count > 0) parts.Add($"formula column {Names(formulas)}");
        return $"The component cannot be deleted because it is referenced by {string.Join(" and ", parts)}.";
    }

    /// <summary>
    /// The calculated and formula columns of other tables that read a column of <paramref name="entity"/> (what deleting the table
    /// would break). The table's own computed columns go with it and are not translated.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>0x80040203</c>: another table's calculated column's definition cannot be read.</exception>
    public static IReadOnlyList<string> DependentsOfTable(EntityMetadata entity, IEnumerable<EntityMetadata> all, IComputedColumns computed)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var prefix = entity.LogicalName + ".";
        return DependentsCore(entity.LogicalName, (entity.Attributes ?? []).Select(x => x.LogicalName).ToHashSet(StringComparer.Ordinal), all,
                computed, (table, _) => table == entity.LogicalName)
            .Where(d => !d.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// The calculated and formula columns that read one of <paramref name="attributes"/> of <paramref name="entity"/> (or follow it as
    /// their lookup), the ones <paramref name="skip"/> names left out untranslated.
    /// </summary>
    private static IReadOnlyList<string> DependentsCore(string entity, IReadOnlySet<string> attributes, IEnumerable<EntityMetadata> all,
        IComputedColumns computed, Func<string, string, bool> skip)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(computed);
        var tables = all.ToList();
        var byName = tables.Where(t => t.LogicalName is not null).ToDictionary(t => t.LogicalName, StringComparer.Ordinal);
        EntityMetadata? Find(string name) => byName.GetValueOrDefault(name);
        List<string> found = [];
        foreach (var table in tables)
            foreach (var column in (table.Attributes ?? []).Where(x => IsComputed(x) && !skip(table.LogicalName, x.LogicalName)))
            {
                IReadOnlyList<ComputedExpr.Column> sources;
                try
                {
                    sources = computed.Translate(table, column, Find).Sources;
                }
                catch (XrmdValidationException ex)
                {
                    throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                        $"The {(column.SourceType == Formula ? "formula" : "calculated")} column '{table.LogicalName}.{column.LogicalName}' has a definition that cannot be read: {ex.Message.TrimEnd('.')}.", 400);
                }
                var reads = sources.Any(source =>
                    source.Lookup is null
                        ? table.LogicalName == entity && attributes.Contains(source.Attribute)
                        : (table.LogicalName == entity && attributes.Contains(source.Lookup))
                          || (attributes.Contains(source.Attribute) && ParentOf(table, source.Lookup, Find)?.LogicalName == entity));
                if (reads) found.Add($"{table.LogicalName}.{column.LogicalName}");
            }
        return found.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The <c>FormulaDefinition</c> of <paramref name="a"/>; null when the type has none. The SDK declares the property on each
    /// column type that can be calculated, not on <see cref="AttributeMetadata"/>.
    /// </summary>
    public static string? FormulaOf(AttributeMetadata a) =>
        a.GetType().GetProperty("FormulaDefinition")?.GetValue(a) as string;

    /// <summary>Sets the <c>FormulaDefinition</c> of <paramref name="a"/> (a type that can be calculated).</summary>
    public static void SetFormula(AttributeMetadata a, string? formula) => SdkProperty.Set(a, "FormulaDefinition", formula);

    /// <summary>The <c>SourceTypeMask</c> of <paramref name="a"/>; null when the type has none or it is not set.</summary>
    public static int? MaskOf(AttributeMetadata a) => a.GetType().GetProperty("SourceTypeMask")?.GetValue(a) as int?;

    /// <summary>True when <paramref name="entity"/> has a calculated column.</summary>
    public static bool HasCalculated(EntityMetadata entity) => (entity.Attributes ?? []).Any(x => x.SourceType == Calculated);

    private static EntityMetadata? ParentOf(EntityMetadata entity, string lookup, Func<string, EntityMetadata?> entities) =>
        entity.Attributes?.FirstOrDefault(x => x.LogicalName == lookup) is LookupAttributeMetadata { Targets: [var target] }
            ? entities(target)
            : null;

    private static bool IsAllowedType(AttributeMetadata a) => a is StringAttributeMetadata or PicklistAttributeMetadata
        or BooleanAttributeMetadata or IntegerAttributeMetadata or DecimalAttributeMetadata or MoneyAttributeMetadata
        or DateTimeAttributeMetadata;

    /// <summary>
    /// A range narrower than the type's widest one. The widest range is no limit: the 2.12a parity export writes it on every calculated
    /// Whole number (-2147483648 to 2147483647), Decimal (±100000000000) and Currency (±922337203685477) column.
    /// </summary>
    internal static bool HasLimits(AttributeMetadata a) => a switch
    {
        IntegerAttributeMetadata i => i.MinValue is not (null or int.MinValue) || i.MaxValue is not (null or int.MaxValue),
        DecimalAttributeMetadata d => d.MinValue is not null && d.MinValue != -WidestDecimal || d.MaxValue is not null && d.MaxValue != WidestDecimal,
        MoneyAttributeMetadata m => m.MinValue is not null && m.MinValue != -WidestMoney || m.MaxValue is not null && m.MaxValue != WidestMoney,
        _ => false,
    };

    private const decimal WidestDecimal = 100000000000m;

    private const double WidestMoney = 922337203685477d;
}
