using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Model.Metadata;
using Xrmd.Query.Sdk;

namespace Xrmd.Query.Sql;

/// <summary>The rows of a table a rollup recalculation covers (plan 2.12b, ruling R8).</summary>
public abstract record RollupScope
{
    private RollupScope()
    {
    }

    /// <summary>Every row (the mass calculation job).</summary>
    public sealed record All : RollupScope;

    /// <summary>
    /// The incremental job: every row whose value or state would change, and the rows a change since <paramref name="SinceUtc"/>
    /// touches (the row itself, the parent of a related row, and over a hierarchy their ancestors), whose <c>_date</c> moves.
    /// </summary>
    public sealed record ChangedSince(DateTime SinceUtc) : RollupScope;

    /// <summary>One row (<c>CalculateRollupField</c>), always written.</summary>
    public sealed record One(Guid Id) : RollupScope;
}

/// <summary>
/// A statement of <see cref="RollupSql"/>. <see cref="RollupSql.Recalculate"/>'s returns one row per updated row: its id, value,
/// <c>_base</c> (null but for a Currency rollup), <c>_date</c> and <c>_state</c>.
/// </summary>
public sealed record RollupCommand(string Sql, IReadOnlyList<DbParameter> Parameters);

/// <summary>
/// The SQL of a rollup column (plan 2.12b, rulings R6, R7, R8; plan 2.7a: in the SQL of <paramref name="dialect"/>): one set-based statement per rollup and <see cref="RollupScope"/>.
/// The target rows (<c>tg</c>) each get the aggregate of the related rows whose lookup points to them, or, over the table's
/// hierarchy, to the row or one of its descendants (a recursive CTE that walks at most <see cref="MaxJobDepth"/> levels: deeper
/// gives state 5, coming back to the row gives state 6 and leaves every other row computed). Each value is cut to the rollup's
/// precision before it is aggregated; a Currency rollup aggregates the <c>_base</c> values and converts at the target row's rate
/// (no currency: state 7); a value outside the column's range or type is an overflow (state 2). An error state stores no value.
/// Only the rollup's columns are written: <c>modifiedon</c>/<c>modifiedby</c> stay as they are. Table and column names come from
/// metadata; constants of the definition are written by <see cref="ComputedSql"/> (texts and dates as parameters), the rest
/// (time, ids, ranges) as parameters.
/// </summary>
public sealed partial class RollupSql(IMetadataStore store, ISqlDialect dialect, IComputedColumns? computed)
{
    /// <summary>The most hierarchy levels below a row a job walks (R6); deeper gives state 5.</summary>
    public const int MaxJobDepth = 100;

    /// <summary>
    /// The statement that recalculates <paramref name="rollup"/> of <paramref name="table"/> (definition
    /// <paramref name="definition"/>) on the rows of <paramref name="scope"/>, stamping <c>_date</c> with <paramref name="nowUtc"/>.
    /// </summary>
    /// <exception cref="XrmdValidationException">A definition this translation cannot write (refused at definition time otherwise).</exception>
    public RollupCommand Recalculate(TableDefinition table, ColumnDefinition rollup, RollupDefinition definition, RollupScope scope, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new Builder(store, dialect, computed, table, rollup, definition).Recalculate(scope, nowUtc);
    }

    /// <summary>
    /// The query that measures the row <paramref name="id"/> before an online calculation of <paramref name="rollup"/> (R6): one row with <c>found</c> (0 when the row does not exist),
    /// <c>depth</c> (the deepest level below the row, walked up to <paramref name="maxDepth"/> + 1) and <c>rows</c> (the rows the
    /// aggregate reads within <paramref name="maxDepth"/> levels).
    /// </summary>
    public RollupCommand Measure(TableDefinition table, ColumnDefinition rollup, RollupDefinition definition, Guid id, int maxDepth) =>
        new Builder(store, dialect, computed, table, rollup, definition).Measure(id, maxDepth);

    /// <summary>The kind of value a rollup holds.</summary>
    private enum Kind { Whole, Decimal, Money, Date }

    private sealed class Builder
    {
        private readonly IMetadataStore _store;
        private readonly ISqlDialect _dialect;
        private readonly TableDefinition _table;
        private readonly ColumnDefinition _rollup;
        private readonly RollupDefinition _definition;
        private readonly QueryParameters _parameters = new();
        private readonly ComputedSql _computed;
        private readonly Kind _kind;
        private readonly string _pk;
        private readonly string _tableSql;
        private readonly OneToManyRelationship? _hierarchy;
        private readonly TableDefinition? _related;
        private readonly string? _lookup;

        public Builder(IMetadataStore store, ISqlDialect dialect, IComputedColumns? computed, TableDefinition table, ColumnDefinition rollup, RollupDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(rollup);
            ArgumentNullException.ThrowIfNull(definition);
            _store = store;
            _dialect = dialect;
            _table = table;
            _rollup = rollup;
            _definition = definition;
            _computed = new ComputedSql(_parameters, store.GetTable, SdkQueryCallbacks.Published(computed ?? new NoComputedColumns(), store), dialect);
            _kind = rollup switch
            {
                IntegerColumn => Kind.Whole,
                DecimalColumn => Kind.Decimal,
                MoneyColumn => Kind.Money,
                DateTimeColumn => Kind.Date,
                _ => throw Refuse($"has the type {rollup.AttributeType}, which a rollup cannot have"),
            };
            _pk = SystemAttributes.PrimaryIdName(table);
            _tableSql = DataTable(table.LogicalName!);
            if (definition.HierarchicalRelationship is { } name)
            {
                _hierarchy = store.GetRelationshipDefinition(name) as OneToManyRelationship
                             ?? store.GetOneToManyDefinitions().FirstOrDefault(r => string.Equals(r.SchemaName, name, StringComparison.OrdinalIgnoreCase));
                if (_hierarchy?.ReferencingAttribute is null || _hierarchy.ReferencingEntity != table.LogicalName!)
                    throw Refuse($"walks the hierarchy '{name}', which is not a relationship of the table to itself");
            }
            if (definition.Related is { } related)
            {
                _related = store.GetTable(related.Entity) ?? throw Refuse($"reads the table '{related.Entity}', which does not exist");
                _lookup = _related.Attributes?.FirstOrDefault(a => a.LogicalName == related.Lookup && a is LookupColumn)?.LogicalName
                          ?? throw Refuse($"follows the lookup '{_related.LogicalName}.{related.Lookup}', which does not exist");
            }
            DemandAggregateFitsType();
            if (_hierarchy is null && _related is null)
                throw Refuse("has neither related rows nor a hierarchy");
        }

        private string Where => $"'{_table.LogicalName}.{_rollup.LogicalName}'";

        /// <summary>
        /// A guard of the definition rules (<c>RollupColumnRules</c>, code <c>0x80060545</c>): COUNT gives a Whole number, a Date and
        /// time rollup takes MIN or MAX only.
        /// </summary>
        private void DemandAggregateFitsType()
        {
            var aggregate = _definition.Aggregate;
            var fits = _kind switch
            {
                Kind.Date => aggregate is RollupAggregate.Min or RollupAggregate.Max,
                Kind.Whole => true,
                _ => aggregate is not RollupAggregate.Count,
            };
            if (!fits)
                throw new Xrmd.Core.Errors.XrmdFaultException(Xrmd.Core.Errors.ErrorCodes.RollupFieldAggregateFunctionNotAllowedForRollupFieldDataType,
                    $"The rollup column {Where} is of type {_rollup.AttributeType} and cannot be calculated with {aggregate.ToString().ToUpperInvariant()}.", 400);
        }

        private XrmdValidationException Refuse(string reason) => new($"The rollup column {Where} {reason}.");

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>The decimals of the rollup's value (Whole number 0); for Currency the expression <c>tg.prec</c>.</summary>
        private string Precision => _kind switch
        {
            Kind.Whole => "0",
            Kind.Decimal => Num(((DecimalColumn)_rollup).Precision ?? TypeMapping.DefaultDecimalPrecision),
            _ => "tg.prec",
        };

        private int DecimalScale => _kind == Kind.Decimal ? ((DecimalColumn)_rollup).Precision ?? TypeMapping.DefaultDecimalPrecision : 0;

        private string Column(string suffix = "") => Q(_rollup.LogicalName + suffix);

        /// <summary>The quoted identifier in the dialect's SQL.</summary>
        private string Q(string identifier) => _dialect.Quote(identifier);

        /// <summary>The quoted data table of <paramref name="entity"/>.</summary>
        private string DataTable(string entity) => DdlGenerator.Table(entity, _dialect);

        // ---- the target rows ---------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>tg</c>: the target rows (id), with, for Currency, the decimals of the value (<c>prec</c>), the row's rate and whether
        /// it has a currency.
        /// </summary>
        private string Targets(string? where)
        {
            var sql = new StringBuilder($"tg AS (SELECT t.{Q(_pk)} AS id");
            if (_kind == Kind.Money)
            {
                sql.Append(", ").Append(MoneyPrecision()).Append(" AS prec")
                    .Append($", t.{Q(CurrencySchema.ExchangeRate)} AS rate")
                    .Append($", CASE WHEN t.{Q(CurrencySchema.CurrencyId)} IS NULL OR t.{Q(CurrencySchema.ExchangeRate)} IS NULL THEN 0 ELSE 1 END AS hascurrency");
            }
            sql.Append($" FROM {_tableSql} AS t");
            if (_kind == Kind.Money && ((MoneyColumn)_rollup).PrecisionSource is not (0 or 1))
                sql.Append($" LEFT JOIN {DataTable(CurrencySchema.Table)} AS cur ON cur.{Q(CurrencySchema.CurrencyId)} = t.{Q(CurrencySchema.CurrencyId)}");
            if (where is not null) sql.Append(" WHERE ").Append(where);
            return sql.Append(')').ToString();
        }

        /// <summary>The decimals of a Currency value: the column's (PrecisionSource 0), the organization's pricing ones (1), the row currency's (2).</summary>
        private string MoneyPrecision()
        {
            var m = (MoneyColumn)_rollup;
            return m.PrecisionSource switch
            {
                0 => Num(Math.Clamp(m.Precision ?? CurrencyMath.DefaultPrecision, CurrencySchema.MinPrecision, CurrencySchema.MaxPrecision)),
                1 => $"COALESCE(({_dialect.Limit($"SELECT {Q(CurrencySchema.PricingPrecision)} FROM {DataTable("organization")}", "(1)")}), {Num(CurrencySchema.DefaultPricingPrecision)})",
                _ => $"COALESCE(cur.{Q(CurrencySchema.Precision)}, {Num(CurrencyMath.DefaultPrecision)})",
            };
        }

        /// <summary>
        /// <c>nodes</c> (root, node): each target row and the rows whose values count for it; over a hierarchy also <c>tree</c> (the
        /// walk, with each row's depth and whether it came back to the root) and <c>walk</c> (per root: loop and deepest level).
        /// </summary>
        private string Nodes(int maxDepth)
        {
            if (_hierarchy is null) return "nodes AS (SELECT id AS root, id AS node FROM tg)";
            var depth = Num(maxDepth);
            return
                "tree AS (SELECT tg.id AS root, tg.id AS node, 0 AS depth, 0 AS revisit FROM tg UNION ALL " +
                $"SELECT tree.root, ch.{Q(_pk)}, tree.depth + 1, CASE WHEN ch.{Q(_pk)} = tree.root THEN 1 ELSE 0 END " +
                $"FROM tree JOIN {_tableSql} AS ch ON ch.{Q(_hierarchy.ReferencingAttribute!)} = tree.node " +
                $"WHERE tree.revisit = 0 AND tree.depth <= {depth}),\n" +
                $"nodes AS (SELECT root, node FROM tree WHERE revisit = 0 AND depth <= {depth})";
        }

        /// <summary>
        /// The rows the aggregate reads, from <c>nodes AS n</c>: the table's own row (<c>s</c>) when a source filter or the
        /// aggregated column needs it, the related rows (<c>r</c>) when there are some; and the filters.
        /// </summary>
        private (string From, string Value) Rows(bool count)
        {
            var related = _definition.Related;
            var aggregatedEntity = _related ?? _table;
            var aggregatedAlias = related is null ? "s" : "r";
            var aggregated = aggregatedEntity.Attributes?.FirstOrDefault(a => a.LogicalName == _definition.AggregatedAttribute)
                             ?? throw Refuse($"aggregates the column '{aggregatedEntity.LogicalName}.{_definition.AggregatedAttribute}', which does not exist");

            // The expressions first: a calculated column may need applies on its row (written after its table).
            var value = count ? Plain(aggregatedAlias, aggregatedEntity, aggregated) : Aggregated(aggregatedAlias, aggregatedEntity, aggregated);
            var filters = new List<string>();
            if (_definition.SourceFilter is { } source) filters.Add(_computed.Predicate("s", _table, source, _table, _rollup));
            if (related?.Filter is { } filter) filters.Add(_computed.Predicate("r", _related!, filter, _table, _rollup));

            var from = new StringBuilder("FROM nodes AS n");
            if (_kind == Kind.Money) from.Append(" JOIN tg ON tg.id = n.root");
            if (_definition.SourceFilter is not null || related is null)
                from.Append(" JOIN ").Append(Table(_table, "s")).Append($" ON s.{Q(_pk)} = n.node");
            if (related is not null)
                from.Append(" JOIN ").Append(Table(_related!, "r")).Append($" ON r.{Q(_lookup!)} = n.node");
            if (filters.Count > 0) from.Append(" WHERE ").Append(string.Join(" AND ", filters.Select(f => $"({f})")));
            return (from.ToString(), value);
        }

        /// <summary>A table with its alias, in parentheses with the applies its calculated columns need.</summary>
        private string Table(TableDefinition entity, string alias)
        {
            var applies = _computed.Applies(alias);
            var table = $"{DataTable(entity.LogicalName!)} AS {alias}";
            return applies.Length == 0 ? table : $"({table}{applies})";
        }

        /// <summary>The value of a column as is (what COUNT counts: a money column's own value, not its base).</summary>
        private string Plain(string alias, TableDefinition entity, ColumnDefinition a) => _computed.ColumnValue(alias, entity, a, _table, _rollup);

        /// <summary>
        /// The value a SUM, MIN, MAX or AVG aggregates: a money column's base value for a Currency rollup, cut to the rollup's
        /// precision (R7) for a number.
        /// </summary>
        private string Aggregated(string alias, TableDefinition entity, ColumnDefinition a)
        {
            if (_kind == Kind.Money)
            {
                if (a is not MoneyColumn money)
                    throw Refuse($"aggregates '{entity.LogicalName}.{a.LogicalName}', which is not a Currency column");
                // Dataverse converts each row to the base currency from its full value and its own rate, not from its rounded
                // _base, and does not round the sum (parity run, plan 2.12b Task 7: 200/12 + 80/12 + 40 = 63.333332).
                if (money.SourceType == ComputedColumnRules.Calculated)
                    return _computed.ColumnValue(alias, entity,
                        entity.Attributes!.First(x => x.LogicalName == a.LogicalName + TypeMapping.BaseColumnSuffix), _table, _rollup);
                return $"(CAST({alias}.{Q(a.LogicalName!)} AS decimal(38, 10)) / NULLIF(CAST({alias}.{Q(CurrencySchema.ExchangeRate)} AS decimal(38, 10)), 0))";
            }
            var value = Plain(alias, entity, a);
            return _kind == Kind.Decimal ? _dialect.Truncate(value, Precision) : value;
        }

        /// <summary><c>agg</c> (id, value): the aggregate of each target row that has rows to aggregate.</summary>
        private string Aggregate()
        {
            var aggregate = _definition.Aggregate;
            var (from, value) = Rows(aggregate == RollupAggregate.Count);
            // Currency rows are summed unrounded (each one is its value over its rate) and cut to the money scale once, on the sum.
            var scale = _kind == Kind.Money ? 10 : DecimalScale;
            var typed = $"CAST({value} AS decimal(38, {Num(scale)}))";
            var function = aggregate switch
            {
                RollupAggregate.Count => _dialect.CountBig(value),
                RollupAggregate.Sum => $"SUM({typed})",
                // Dataverse averages a row whose value is empty as 0 (parity run, plan 2.12b Task 7).
                RollupAggregate.Avg => $"AVG(CAST(COALESCE({value}, 0) AS decimal(38, 10)))",
                RollupAggregate.Min => _kind == Kind.Date ? $"MIN({value})" : $"MIN({typed})",
                RollupAggregate.Max => _kind == Kind.Date ? $"MAX({value})" : $"MAX({typed})",
                _ => throw Refuse($"uses the aggregate {aggregate}, which cannot be computed"),
            };
            return $"agg AS (SELECT n.root AS id, {function} AS value {from} GROUP BY n.root)";
        }

        /// <summary>The aggregate as the rollup's number (or date), before the range check: the empty set gives 0 for COUNT and SUM.</summary>
        private string RawValue(int scale)
        {
            var type = $"decimal(38, {Num(scale)})";
            return _definition.Aggregate switch
            {
                RollupAggregate.Count or RollupAggregate.Sum => $"CAST(COALESCE(agg.value, 0) AS {type})",
                RollupAggregate.Avg => $"CAST({_dialect.Round("COALESCE(agg.value, 0)", Num(scale))} AS {type})",
                _ when _kind == Kind.Date => "agg.value",
                _ => $"CAST(agg.value AS {type})",
            };
        }

        /// <summary>
        /// <c>res</c> (id, value, base, state): the state of each target row (R6, R7) and the value it stores (none in an error state).
        /// </summary>
        private string Result()
        {
            var sql = new StringBuilder("res AS (SELECT tg.id, ");
            var columnType = TypeMapping.ToSql(_rollup, _dialect);
            var state = new StringBuilder("CASE");
            if (_hierarchy is not null) state.Append($" WHEN walk.revisit = 1 THEN 6 WHEN walk.depth > {Num(MaxJobDepth)} THEN 5");
            var applies = new StringBuilder();
            if (_kind == Kind.Money)
            {
                state.Append(" WHEN tg.hascurrency = 0 THEN 7");
                applies.Append(' ').Append(_dialect.Apply($"(SELECT {RawValue(TypeMapping.MoneyScale)} AS b)", "xb", outer: false))
                    .Append(' ').Append(_dialect.Apply($"(SELECT {_dialect.TryCast("xb.b", "decimal(28, 4)")} * CAST(tg.rate AS decimal(28, 10)) AS v)", "x", outer: false));
                state.Append($" WHEN xb.b < {Parameter("@basemin", CurrencySchema.MinBaseMoney)} OR xb.b > {Parameter("@basemax", CurrencySchema.MaxBaseMoney)} THEN 2");
            }
            else
            {
                applies.Append(' ').Append(_dialect.Apply($"(SELECT {RawValue(DecimalScale)} AS v)", "x", outer: false));
            }
            if (_kind != Kind.Date)
            {
                var (min, max) = Range();
                if (min is not null) state.Append($" WHEN x.v < {Parameter("@min", min.Value)} THEN 2");
                if (max is not null) state.Append($" WHEN x.v > {Parameter("@max", max.Value)} THEN 2");
                state.Append($" WHEN x.v IS NOT NULL AND {_dialect.TryCast("x.v", columnType)} IS NULL THEN 2");
            }
            // A date over related rows has no error state: always 1.
            var stateSql = state.Length == "CASE".Length ? "1" : state.Append(" ELSE 1 END").ToString();
            applies.Append(' ').Append(_dialect.Apply($"(SELECT {stateSql} AS state)", "st", outer: false));

            sql.Append($"CASE WHEN st.state = 1 THEN {_dialect.TryCast("x.v", columnType)} END AS value, ");
            if (_kind == Kind.Money) sql.Append($"CASE WHEN st.state = 1 THEN CAST(xb.b AS decimal(19, {Num(TypeMapping.MoneyScale)})) END AS base, ");
            sql.Append("st.state AS state FROM tg LEFT JOIN agg ON agg.id = tg.id");
            if (_hierarchy is not null) sql.Append(" LEFT JOIN walk ON walk.root = tg.id");
            return sql.Append(applies).Append(')').ToString();
        }

        /// <summary>The column's MinValue and MaxValue (R7), when it has them.</summary>
        private (decimal? Min, decimal? Max) Range() => _rollup switch
        {
            IntegerColumn i => (i.MinValue, i.MaxValue),
            DecimalColumn d => (d.MinValue, d.MaxValue),
            MoneyColumn m => (ToDecimal(m.MinValue), ToDecimal(m.MaxValue)),
            _ => (null, null),
        };

        private static decimal? ToDecimal(double? value) =>
            value is { } v && v >= (double)decimal.MinValue && v <= (double)decimal.MaxValue ? (decimal)v : null;

        private string Parameter(string name, object value)
        {
            if (_parameters.All.All(p => p.ParameterName != name))
                _parameters.AddNamed(name, value switch
                {
                    DateTime => DbType.DateTime2,
                    Guid => DbType.Guid,
                    _ => DbType.Decimal,
                }, value);
            return name;
        }

        /// <summary>
        /// <c>touched</c>: the rows a change since <c>@since</c> touches: the row itself, the target of a related row, and over a
        /// hierarchy every ancestor of those (at most <see cref="MaxJobDepth"/> levels up).
        /// </summary>
        private string Touched()
        {
            var changed = new StringBuilder($"SELECT t.{Q(_pk)} AS id FROM {_tableSql} AS t WHERE t.{Q("modifiedon")} >= @since");
            if (_definition.Related is not null)
                changed.Append($" UNION SELECT r.{Q(_lookup!)} FROM {DataTable(_related!.LogicalName!)} AS r WHERE r.{Q("modifiedon")} >= @since AND r.{Q(_lookup!)} IS NOT NULL");
            if (_hierarchy is null) return $"touched AS ({changed})";
            var parent = _hierarchy.ReferencingAttribute!;
            return
                $"changed AS ({changed}),\n" +
                $"up AS (SELECT id, 0 AS depth FROM changed UNION ALL SELECT p.{Q(parent)}, up.depth + 1 FROM up " +
                $"JOIN {_tableSql} AS p ON p.{Q(_pk)} = up.id WHERE p.{Q(parent)} IS NOT NULL AND up.depth < {Num(MaxJobDepth)}),\n" +
                "touched AS (SELECT DISTINCT id FROM up)";
        }

        public RollupCommand Recalculate(RollupScope scope, DateTime nowUtc)
        {
            var money = _kind == Kind.Money;
            // The aggregate first: it adds the parameters of the filters' constants.
            var aggregate = Aggregate();
            var ctes = new List<string>
            {
                Targets(scope is RollupScope.One ? $"t.{Q(_pk)} = @id" : null),
                Nodes(MaxJobDepth),
            };
            if (_hierarchy is not null) ctes.Add("walk AS (SELECT root, MAX(revisit) AS revisit, MAX(depth) AS depth FROM tree GROUP BY root)");
            ctes.Add(aggregate);
            ctes.Add(Result());
            if (scope is RollupScope.ChangedSince) ctes.Add(Touched());

            var sql = new StringBuilder(_dialect.RecursiveWith).AppendJoin(",\n", ctes).Append('\n');
            var set = new List<(string Column, string Value)> { (Column(), "res.value") };
            if (money) set.Add((Column(TypeMapping.BaseColumnSuffix), "res.base"));
            set.Add((Column(RollupColumns.DateSuffix), "@now"));
            set.Add((Column(RollupColumns.StateSuffix), "res.state"));
            var returning = new List<Returned>
            {
                new(Q(_pk)), new(Column()),
                money ? new(Column(TypeMapping.BaseColumnSuffix)) : new($"CAST(NULL AS decimal(19, {Num(TypeMapping.MoneyScale)}))", Column: false),
                new(Column(RollupColumns.DateSuffix)), new(Column(RollupColumns.StateSuffix)),
            };
            string? where = null;
            if (scope is not RollupScope.One)
            {
                // Only the rows whose value or state changes (EXCEPT compares nulls as equal), and the touched ones.
                var stored = money
                    ? $"t.{Column()}, t.{Column(TypeMapping.BaseColumnSuffix)}, t.{Column(RollupColumns.StateSuffix)}"
                    : $"t.{Column()}, t.{Column(RollupColumns.StateSuffix)}";
                var computed = money ? "res.value, res.base, res.state" : "res.value, res.state";
                where = $"EXISTS (SELECT {stored} EXCEPT SELECT {computed})";
                if (scope is RollupScope.ChangedSince) where += $" OR t.{Q(_pk)} IN (SELECT id FROM touched)";
            }
            sql.Append(_dialect.UpdateFrom(_tableSql, "t", set, "res", $"res.id = t.{Q(_pk)}", where, returning));
            sql.Append(_dialect.RecursionLimit(MaxJobDepth + 2)).Append(';');

            Parameter("@now", nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : nowUtc);
            switch (scope)
            {
                case RollupScope.One one:
                    Parameter("@id", one.Id);
                    break;
                case RollupScope.ChangedSince since:
                    Parameter("@since", since.SinceUtc.Kind == DateTimeKind.Local ? since.SinceUtc.ToUniversalTime() : since.SinceUtc);
                    break;
            }
            return new RollupCommand(sql.ToString(), _parameters.All);
        }

        public RollupCommand Measure(Guid id, int maxDepth)
        {
            var (from, _) = Rows(count: true);
            var sql = new StringBuilder(_dialect.RecursiveWith)
                .Append(Targets($"t.{Q(_pk)} = @id")).Append(",\n")
                .Append(Nodes(maxDepth)).Append('\n')
                .Append("SELECT (SELECT COUNT(*) FROM tg) AS found, ")
                .Append(_hierarchy is null ? "0" : "COALESCE((SELECT MAX(depth) FROM tree), 0)").Append(" AS depth, ")
                .Append($"(SELECT {_dialect.CountBig("*")} {from}) AS rows")
                .Append(_dialect.RecursionLimit(Math.Max(maxDepth, 0) + 2)).Append(';');
            Parameter("@id", id);
            return new RollupCommand(sql.ToString(), _parameters.All);
        }
    }
}
