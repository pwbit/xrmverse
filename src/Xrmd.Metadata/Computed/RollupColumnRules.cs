using Xrmd.Model.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;

namespace Xrmd.Metadata.Computed;

/// <summary>
/// The metadata rules of rollup columns (plan 2.12b, Global Constraints and rulings R4, R5, R11, R13): types, limits, the aggregate
/// and type matrix, the columns a definition may read, and the rollups that read a component (what blocks its deletion).
/// </summary>
public static partial class RollupColumnRules
{
    /// <summary>The most rollup columns a table can have (newer maker page; the developer page says 10, ruling R13).</summary>
    public const int MaxPerTable = 50;

    /// <summary>The most rollup columns an organization can have (newer maker page; the developer page says 100, ruling R13).</summary>
    public const int MaxPerOrganization = 200;

    /// <summary>
    /// Checks the rollup definition of <paramref name="a"/> (changed in place: read-only flags, required level, <c>SourceTypeMask</c> 0).
    /// <paramref name="current"/> is the stored column on an update (the limits apply to a new rollup only);
    /// <paramref name="otherRollupsOnTable"/> and <paramref name="otherRollupsInOrg"/> count the rollups other than <paramref name="a"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 with the rollup error code of the refused definition; 400 InvalidArgument for a
    /// rollup on an elastic table or over the rows of one.</exception>
    /// <exception cref="XrmdValidationException">No translator, or the translator refuses the definition.</exception>
    public static void ValidateDefinition(TableDefinition entity, ColumnDefinition a, ColumnDefinition? current,
        Func<string, TableDefinition?> entities, Func<string, OneToManyRelationship?> relationships, IRollupColumns? rollups,
        int otherRollupsOnTable, int otherRollupsInOrg, IComputedColumns? computed = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(relationships);
        var where = $"'{entity.LogicalName}.{a.LogicalName}'";

        if (rollups is null) throw new XrmdValidationException("Rollup columns are not available on this server.");
        // Elastic tables have no rollup columns, and a rollup reads standard tables only (explicit, whatever path defines the column).
        if (ElasticTables.IsElastic(entity))
            throw XrmdFaultException.InvalidArgument(
                $"The rollup column {where} cannot be defined on the elastic table '{entity.LogicalName}': elastic tables have no rollup columns.");
        if (!IsAllowedType(a))
            throw DataTypeNotValid(a, where);
        if (current is null || !RollupColumns.IsRollup(current))
        {
            if (otherRollupsOnTable >= MaxPerTable)
                throw Fault(ErrorCodes.ExceededRollupFieldsPerEntityQuota,
                    $"The table '{entity.LogicalName}' already has {MaxPerTable} rollup columns, the most a table can have: {where} cannot be added.");
            if (otherRollupsInOrg >= MaxPerOrganization)
                throw Fault(ErrorCodes.ExceededRollupFieldsPerOrgQuota,
                    $"The organization already has {MaxPerOrganization} rollup columns, the most it can have: {where} cannot be added.");
        }

        // A rollup column is computed by the server: it is never written, and it cannot be required. MinValue and MaxValue are kept
        // (the export writes them) and bound the value (ruling R7).
        a.IsValidForCreate = false;
        a.IsValidForUpdate = false;
        a.RequiredLevel = ModelFactory.Required(RequirementLevel.None);
        a.GetType().GetProperty("SourceTypeMask")?.SetValue(a, 0); // ruling R13

        var definition = rollups.Translate(entity, a, entities, relationships);
        var aggregatedEntity = definition.AggregatedEntity(entity.LogicalName!);
        if (aggregatedEntity != entity.LogicalName && entities(aggregatedEntity) is { } source && ElasticTables.IsElastic(source))
            throw XrmdFaultException.InvalidArgument(
                $"The rollup column {where} cannot aggregate rows of the elastic table '{aggregatedEntity}': a rollup reads standard tables only.");
        var aggregated = Find(entity, a, entities, aggregatedEntity, definition.AggregatedAttribute)
                         ?? throw XrmdFaultException.InvalidArgument(
                             $"The rollup column {where} aggregates '{aggregatedEntity}.{definition.AggregatedAttribute}', which does not exist.");
        var aggregatedName = $"'{aggregatedEntity}.{aggregated.LogicalName}'";
        // The base-currency value is computed by the server from its money column (ruling R7 aggregates the bases itself).
        if (aggregated is MoneyColumn { IsBaseCurrency: true })
            throw Fault(ErrorCodes.RollupFieldsAggregateOnRollupFieldOrComplexCalcFieldNotAllowed,
                $"The rollup column {where} cannot aggregate {aggregatedName}, the base-currency value of " +
                $"'{aggregated.LogicalName![..^TypeMapping.BaseColumnSuffix.Length]}': aggregate the money column itself.");
        DemandMatrix(a, definition.Aggregate, aggregated, aggregatedName, where);

        // Ruling R5: what a rollup reads is a simple column or a basic calculated one (own simple columns only).
        if (!IsSimpleOrBasic(aggregated, Owner(entity, entities, aggregatedEntity), entities, computed))
            throw Fault(ErrorCodes.RollupFieldsAggregateOnRollupFieldOrComplexCalcFieldNotAllowed,
                $"The rollup column {where} cannot aggregate {aggregatedName}: only a simple column, or a calculated or formula column that reads " +
                "simple columns of its own row (a formula without Now, UTCNow, UTCToday or IsUTCToday), can be aggregated.");
        foreach (var column in ColumnsOf(definition.SourceFilter))
            DemandFilterColumn(entity, a, entities, computed, entity.LogicalName!, column, where, "its own rows",
                ErrorCodes.RollupFieldSourceFilterFieldNotAllowed);
        if (definition.Related is { Filter: { } relatedFilter } related)
            foreach (var column in ColumnsOf(relatedFilter))
                DemandFilterColumn(entity, a, entities, computed, related.Entity, column, where, "the related rows",
                    ErrorCodes.RollupFieldTargetFilterFieldNotAllowed);
    }

    /// <summary>
    /// The rollup columns (as <c>table.column</c>, ordered) that read <paramref name="attribute"/> of <paramref name="entity"/>: as the
    /// aggregated column, a filter column, the lookup of their related rows or the lookup of their hierarchy. A rollup never reads
    /// itself, so it is not translated; nor is <paramref name="replaced"/> (<c>table.column</c>), a column being deleted or redefined.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>0x80040203</c>: a rollup's definition cannot be read.</exception>
    public static IReadOnlyList<string> Dependents(string entity, string attribute, IEnumerable<TableDefinition> all, IRollupColumns rollups,
        Func<string, OneToManyRelationship?> relationships, string? replaced = null) =>
        DependentsCore(all, rollups, relationships,
            (table, column) => (table == entity && column == attribute) || (replaced is not null && replaced == $"{table}.{column}"),
            (_, _, reads) => reads.Contains(new RollupSource(entity, attribute)));

    /// <summary>The rollup columns that aggregate over the one-to-many relationship <paramref name="schemaName"/> or name it as their hierarchy.</summary>
    /// <exception cref="XrmdFaultException">400 <c>0x80040203</c>: a rollup's definition cannot be read.</exception>
    public static IReadOnlyList<string> DependentsOfRelationship(string schemaName, IEnumerable<TableDefinition> all, IRollupColumns rollups,
        Func<string, OneToManyRelationship?> relationships) =>
        DependentsCore(all, rollups, relationships, (_, _) => false,
            (_, definition, _) => string.Equals(definition.Related?.Relationship, schemaName, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(definition.HierarchicalRelationship, schemaName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The rollup columns of other tables that read a column of <paramref name="entity"/> (the table's own go with it).</summary>
    /// <exception cref="XrmdFaultException">400 <c>0x80040203</c>: another table's rollup definition cannot be read.</exception>
    public static IReadOnlyList<string> DependentsOfTable(string entity, IEnumerable<TableDefinition> all, IRollupColumns rollups,
        Func<string, OneToManyRelationship?> relationships) =>
        DependentsCore(all, rollups, relationships, (table, _) => table == entity,
            (_, definition, reads) => definition.Related?.Entity == entity || reads.Any(r => r.Entity == entity));

    private static IReadOnlyList<string> DependentsCore(IEnumerable<TableDefinition> all, IRollupColumns rollups,
        Func<string, OneToManyRelationship?> relationships, Func<string, string, bool> skip,
        Func<TableDefinition, RollupDefinition, IReadOnlySet<RollupSource>, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(rollups);
        ArgumentNullException.ThrowIfNull(relationships);
        var tables = all.ToList();
        var byName = tables.Where(t => t.LogicalName is not null).ToDictionary(t => t.LogicalName!, StringComparer.Ordinal);
        TableDefinition? FindTable(string name) => byName.GetValueOrDefault(name);
        List<string> found = [];
        foreach (var table in tables)
            foreach (var column in (table.Attributes ?? []).Where(x => RollupColumns.IsRollup(x) && !skip(table.LogicalName!, x.LogicalName!)))
            {
                var name = $"{table.LogicalName}.{column.LogicalName}";
                RollupDefinition definition;
                try
                {
                    definition = rollups.TranslatePublished(table, column, FindTable, relationships);
                }
                catch (Exception ex) when (ex is XrmdValidationException or XrmdFaultException)
                {
                    throw XrmdFaultException.InvalidArgument(
                        $"The rollup column '{name}' has a definition that cannot be read: {ex.Message.TrimEnd('.')}.");
                }
                if (matches(table, definition, Reads(table.LogicalName!, definition, relationships))) found.Add(name);
            }
        return found.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Every column <paramref name="definition"/> of a rollup of <paramref name="entity"/> reads: its <see cref="RollupDefinition.Sources"/>,
    /// and, should a translator leave them out, the aggregated column, the related lookup and the hierarchy's lookup.
    /// </summary>
    public static IReadOnlySet<RollupSource> Reads(string entity, RollupDefinition definition, Func<string, OneToManyRelationship?> relationships)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(relationships);
        var reads = new HashSet<RollupSource>(definition.Sources)
        {
            new(definition.AggregatedEntity(entity), definition.AggregatedAttribute),
        };
        if (definition.Related is { } related) reads.Add(new RollupSource(related.Entity, related.Lookup));
        if (definition.HierarchicalRelationship is { } hierarchy
            && relationships(hierarchy) is { ReferencingEntity: { } owner, ReferencingAttribute: { } lookup })
            reads.Add(new RollupSource(owner, lookup));
        return reads;
    }

    /// <summary>The columns of the row a condition filters (<c>ComputedExpr.Column</c> with no lookup), in order of appearance.</summary>
    public static IReadOnlyList<string> ColumnsOf(ComputedCondition? condition)
    {
        List<string> columns = [];
        void Expr(ComputedExpr? e)
        {
            switch (e)
            {
                case ComputedExpr.Column column:
                    columns.Add(column.Attribute);
                    break;
                case ComputedExpr.Binary binary:
                    Expr(binary.Left);
                    Expr(binary.Right);
                    break;
                case ComputedExpr.Function function:
                    foreach (var arg in function.Args) Expr(arg);
                    break;
                case ComputedExpr.Conditional conditional:
                    foreach (var branch in conditional.Branches)
                    {
                        Condition(branch.When);
                        Expr(branch.Then);
                    }
                    Expr(conditional.Otherwise);
                    break;
            }
        }
        void Condition(ComputedCondition? c)
        {
            switch (c)
            {
                case ComputedCondition.Compare compare:
                    Expr(compare.Left);
                    Expr(compare.Right);
                    break;
                case ComputedCondition.And and:
                    Condition(and.Left);
                    Condition(and.Right);
                    break;
                case ComputedCondition.Or or:
                    Condition(or.Left);
                    Condition(or.Right);
                    break;
                case ComputedCondition.Not not:
                    Condition(not.Operand);
                    break;
            }
        }
        Condition(condition);
        return columns.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void DemandFilterColumn(TableDefinition entity, ColumnDefinition a, Func<string, TableDefinition?> entities,
        IComputedColumns? computed, string table,
        string column, string where, string rows, int code)
    {
        var found = Find(entity, a, entities, table, column)
                    ?? throw XrmdFaultException.InvalidArgument(
                        $"The filter of the rollup column {where} on {rows} names '{table}.{column}', which does not exist.");
        if (!IsSimpleOrBasic(found, Owner(entity, entities, table), entities, computed))
            throw Fault(code,
                $"The filter of the rollup column {where} on {rows} cannot use '{table}.{column}': only a simple column, or a calculated or " +
                "formula column that reads simple columns of its own row (a formula without Now, UTCNow, UTCToday or IsUTCToday), can be used.");
    }

    /// <summary>
    /// The aggregate and type matrix (Global Constraints): the function against the rollup's type (<c>0x80060545</c>), the aggregated
    /// type against the function (<c>0x8006053a</c>) and against the rollup's type (<c>0x8006053d</c>), and dates of the same behavior
    /// and format (<c>0x80060544</c>).
    /// </summary>
    private static void DemandMatrix(ColumnDefinition rollup, RollupAggregate aggregate, ColumnDefinition aggregated, string aggregatedName,
        string where)
    {
        var functionAllowed = rollup switch
        {
            IntegerColumn => true,
            DecimalColumn or MoneyColumn => aggregate != RollupAggregate.Count,
            DateTimeColumn => aggregate is RollupAggregate.Min or RollupAggregate.Max,
            _ => false,
        };
        if (!functionAllowed)
            throw Fault(ErrorCodes.RollupFieldAggregateFunctionNotAllowedForRollupFieldDataType,
                $"The aggregate {aggregate} cannot give a rollup column of type {TypeName(rollup)}: {where}.");
        if (aggregate == RollupAggregate.Count) return; // COUNT counts the values of any column

        var numeric = aggregated is IntegerColumn or DecimalColumn or MoneyColumn
            or DoubleColumn or BigIntColumn;
        var typeAllowed = aggregate is RollupAggregate.Min or RollupAggregate.Max ? numeric || aggregated is DateTimeColumn : numeric;
        if (!typeAllowed)
            throw Fault(ErrorCodes.RollupFieldsAggregateFunctionTypeMismatch,
                $"The aggregate {aggregate} cannot be applied to {aggregatedName}, a column of type {TypeName(aggregated)}: {where}.");

        var matches = rollup switch
        {
            IntegerColumn => aggregated is IntegerColumn,
            DecimalColumn => aggregated is DecimalColumn or IntegerColumn,
            MoneyColumn => aggregated is MoneyColumn,
            DateTimeColumn => aggregated is DateTimeColumn,
            _ => false,
        };
        if (!matches)
            throw Fault(ErrorCodes.RollupFieldsAggregateFieldDataTypeNotAllowedSimilarRollupFieldDataType,
                $"A rollup column of type {TypeName(rollup)} cannot aggregate {aggregatedName}, a column of type {TypeName(aggregated)}: {where}.");

        if (rollup is DateTimeColumn date && aggregated is DateTimeColumn source
            && (BehaviorOf(date) != BehaviorOf(source) || FormatOf(date) != FormatOf(source)))
            throw Fault(ErrorCodes.RollupFieldAndAggregateFieldDataTypeFormatMismatch,
                $"The rollup column {where} ({BehaviorOf(date)}, {FormatOf(date)}) and the date column it aggregates, {aggregatedName} " +
                $"({BehaviorOf(source)}, {FormatOf(source)}), must have the same behavior and format.");
    }

    /// <summary>The <c>DateTimeBehavior</c> of <paramref name="a"/>; a column without one is UserLocal.</summary>
    public static string BehaviorOf(DateTimeColumn a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return a.DateTimeBehavior?.Value ?? DateTimeBehaviors.UserLocal;
    }

    private static DateTimeDisplayFormat FormatOf(DateTimeColumn a) => a.Format ?? DateTimeDisplayFormat.DateAndTime;

    /// <summary>
    /// A simple column, or a basic calculated or formula column (<c>SourceTypeMask</c> with no bit but 1: own simple columns only); a
    /// formula column also must not read the current time (<c>Now</c>, <c>UTCNow</c>, <c>UTCToday</c>, <c>IsUTCToday</c>; Learn, plan
    /// 2.12c ruling F9), which needs its translation (<paramref name="owner"/>, its table, and <paramref name="computed"/>). A rollup,
    /// its <c>_base</c> and its companions are not.
    /// </summary>
    private static bool IsSimpleOrBasic(ColumnDefinition a, TableDefinition? owner, Func<string, TableDefinition?> entities,
        IComputedColumns? computed) => (a.SourceType ?? 0) switch
    {
        0 => !RollupColumns.IsCompanion(a),
        ComputedColumnRules.Calculated => IsBasicMask(a),
        ComputedColumnRules.Formula => IsBasicMask(a) && owner is not null && computed is not null && !ReadsCurrentTime(owner, a, entities, computed),
        _ => false,
    };

    private static bool IsBasicMask(ColumnDefinition a) => ComputedColumnRules.MaskOf(a) is { } mask && (mask & ~1) == 0;

    /// <summary>True when the formula of <paramref name="a"/> reads the current time, or cannot be translated.</summary>
    private static bool ReadsCurrentTime(TableDefinition owner, ColumnDefinition a, Func<string, TableDefinition?> entities, IComputedColumns computed)
    {
        try
        {
            return FormulaColumnRules.ReadsCurrentTime(computed.Translate(owner, a, entities).Body);
        }
        catch (XrmdValidationException)
        {
            return true;
        }
    }

    /// <summary>The table <paramref name="table"/>: <paramref name="entity"/> itself when it is that one (it may be new).</summary>
    private static TableDefinition? Owner(TableDefinition entity, Func<string, TableDefinition?> entities, string table) =>
        table == entity.LogicalName ? entity : entities(table);

    /// <summary>A column of <paramref name="table"/>: <paramref name="a"/> itself and <paramref name="entity"/> first (they may be new).</summary>
    private static ColumnDefinition? Find(TableDefinition entity, ColumnDefinition a, Func<string, TableDefinition?> entities, string table,
        string column)
    {
        if (table == entity.LogicalName)
            return column == a.LogicalName ? a : entity.Attributes?.FirstOrDefault(x => x.LogicalName == column)
                                                 ?? entities(table)?.Attributes?.FirstOrDefault(x => x.LogicalName == column);
        return entities(table)?.Attributes?.FirstOrDefault(x => x.LogicalName == column);
    }

    private static bool IsAllowedType(ColumnDefinition a) =>
        a is IntegerColumn or DecimalColumn or DateTimeColumn
        || a is MoneyColumn { IsBaseCurrency: not true };

    private static string TypeName(ColumnDefinition a) => a.AttributeType?.ToString() ?? a.GetType().Name;

    /// <summary>
    /// The refusal of a rollup column of a type that cannot be one (400 <c>RollupFieldsDataTypeNotValid</c>), <paramref name="where"/>
    /// naming the column (<c>'table.column'</c>): the store's answer, which a solution file of such a column gets too.
    /// </summary>
    public static XrmdFaultException DataTypeNotValid(ColumnDefinition a, string where) =>
        Fault(ErrorCodes.RollupFieldsDataTypeNotValid, $"A rollup column cannot be of type {TypeName(a)}: {where}.");

    private static XrmdFaultException Fault(int code, string message) => new(code, message, 400);
}
