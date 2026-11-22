using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;

namespace Xrmd.Query.Sql;

/// <summary>
/// The SQL of a calculated column (plan 2.12a, rulings K6–K8, K10; plan 2.7a: in the SQL of <paramref name="dialect"/>): its <see cref="ComputedDefinition"/> written as an expression
/// over the row's columns, cast to the column's type. A column of the parent record, the precision of the record's currency and the
/// organization's pricing precision are read by an <c>OUTER APPLY</c> of the row (aliases <c>c0..cn</c>, unique per compilation), so
/// that the expression holds no subquery and can be grouped and aggregated. The parent record is read with system rights (K10): the
/// apply carries no read restriction, and a missing parent (null lookup, deleted row) gives null columns. Every string or date
/// constant is a parameter; whole and decimal constants are written as numbers (they are typed values, never caller text).
/// </summary>
internal sealed partial class ComputedSql(QueryParameters parameters, Func<string, EntityMetadata?> entities, IComputedColumns computed,
    ISqlDialect dialect)
{
    /// <summary>The quoted identifier in the dialect's SQL.</summary>
    private string Q(string identifier) => dialect.Quote(identifier);

    /// <summary>The quoted data table of <paramref name="entity"/>.</summary>
    private string DataTable(string entity) => DdlGenerator.Table(entity, dialect);

    /// <summary>The most calculated columns a chain may hold (the definition check refuses more; a guard here).</summary>
    public const int MaxDepth = 5;

    /// <summary>The most columns a chain that holds a formula column may hold (plan 2.12c, F10; a guard here).</summary>
    public const int MaxFormulaDepth = 10;

    /// <summary>The applies of a scope's row, in the order they were needed (an apply of a parent's row follows that parent's).</summary>
    private readonly Dictionary<string, List<string>> _applies = new(StringComparer.Ordinal);

    /// <summary>The apply alias already given to (owner alias, what it reads).</summary>
    private readonly Dictionary<(string Owner, string Key), string> _aliases = [];

    /// <summary>The scope alias (<c>t0</c>, <c>p0</c>) whose row an apply alias belongs to.</summary>
    private readonly Dictionary<string, string> _scopeOf = new(StringComparer.Ordinal);

    /// <summary>
    /// The value of the calculated column <paramref name="attribute"/> of <paramref name="entity"/> on the row <paramref name="sqlAlias"/>,
    /// cast to its type (K8). <paramref name="depth"/> is 1 for a column the query reads, one more per chained calculated column.
    /// </summary>
    /// <exception cref="XrmdValidationException">A definition this translation cannot write (refused at definition time otherwise).</exception>
    public string Expression(string sqlAlias, EntityMetadata entity, AttributeMetadata attribute, ComputedDefinition definition, int depth)
    {
        BeginColumn();
        return Expression(sqlAlias, entity, attribute, definition, depth, formulaChain: false);
    }

    /// <summary>
    /// <see cref="Expression(string, EntityMetadata, AttributeMetadata, ComputedDefinition, int)"/> of a column of a chain;
    /// <paramref name="formulaChain"/>: a formula column is in the chain before it (F10: the chain may then hold 10 columns).
    /// </summary>
    private string Expression(string sqlAlias, EntityMetadata entity, AttributeMetadata attribute, ComputedDefinition definition, int depth,
        bool formulaChain)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(definition);
        formulaChain |= attribute.SourceType == ComputedColumnRules.Formula;
        if (formulaChain && depth > MaxFormulaDepth)
            throw new XrmdValidationException(
                $"The {Kind(attribute)} column '{entity.LogicalName}.{attribute.LogicalName}' chains more than {MaxFormulaDepth} formula or calculated columns.");
        if (!formulaChain && depth > MaxDepth)
            throw new XrmdValidationException(
                $"The calculated column '{entity.LogicalName}.{attribute.LogicalName}' chains more than {MaxDepth} calculated columns.");
        var row = new Row(sqlAlias, entity, attribute, depth) { FormulaChain = formulaChain };
        _column ??= row;
        if (row.Formula) Share(definition.Body);
        var value = Eval(row, definition.Body);
        // Plan 2.12c: a decimal formula result outside ±100,000,000,000 is null, as its intermediates are (Limit).
        if (row.Formula && attribute is DecimalAttributeMetadata && !value.InRange) value = Limit(row, value);
        return Result(row, attribute, value.Sql);
    }

    private static string Kind(AttributeMetadata attribute) => attribute.SourceType == ComputedColumnRules.Formula ? "formula" : "calculated";

    /// <summary>
    /// The <c>{money}_base</c> value of the calculated money column <paramref name="money"/>: its value divided by the row's
    /// <c>exchangerate</c>, rounded to 4 decimals as a stored base value is (K8); null without a rate.
    /// </summary>
    public string BaseExpression(string sqlAlias, EntityMetadata entity, MoneyAttributeMetadata money, ComputedDefinition definition, int depth) =>
        ToBase(sqlAlias, Expression(sqlAlias, entity, money, definition, depth));

    /// <summary>
    /// The applies the row <paramref name="scopeAlias"/> needs, each as <c>OUTER APPLY (…) AS cN</c> preceded by a space; empty when
    /// none. Written after the scope's table (inside the parentheses of a link-entity's join, so that its <c>ON</c> clause sees them).
    /// </summary>
    public string Applies(string scopeAlias) =>
        _applies.TryGetValue(scopeAlias, out var applies) ? string.Concat(applies.Select(a => " " + a)) : "";

    /// <summary>
    /// For a rollup (plan 2.12b): the value of the column <paramref name="column"/> of <paramref name="entity"/> on the row
    /// <paramref name="sqlAlias"/>: a calculated column as its expression (cast to its type), any other one as its column. A refusal
    /// names the rollup column <paramref name="rollupTable"/>.<paramref name="rollup"/> (a calculated column it reads names itself).
    /// </summary>
    internal string ColumnValue(string sqlAlias, EntityMetadata entity, AttributeMetadata column, EntityMetadata rollupTable, AttributeMetadata rollup)
    {
        BeginColumn();
        return Value(RollupRow(sqlAlias, entity, rollupTable, rollup), new ComputedExpr.Column(null, column.LogicalName));
    }

    /// <summary>
    /// For a rollup (plan 2.12b): <paramref name="condition"/> on the columns of the row <paramref name="sqlAlias"/> of
    /// <paramref name="entity"/>, with the operators and parameters of a calculated column's conditions. A refusal names the rollup
    /// column <paramref name="rollupTable"/>.<paramref name="rollup"/>.
    /// </summary>
    internal string Predicate(string sqlAlias, EntityMetadata entity, ComputedCondition condition, EntityMetadata rollupTable, AttributeMetadata rollup)
    {
        BeginColumn();
        return Condition(RollupRow(sqlAlias, entity, rollupTable, rollup), condition);
    }

    private static Row RollupRow(string sqlAlias, EntityMetadata entity, EntityMetadata rollupTable, AttributeMetadata rollup)
    {
        ArgumentNullException.ThrowIfNull(rollupTable);
        ArgumentNullException.ThrowIfNull(rollup);
        return new Row(sqlAlias, entity, rollup, 1, $"'{rollupTable.LogicalName}.{rollup.LogicalName}'");
    }

    private string ToBase(string sqlAlias, string value) =>
        dialect.TryCast(dialect.Round($"{value} / NULLIF({sqlAlias}.{Q(CurrencySchema.ExchangeRate)}, 0)",
            CurrencySchema.BaseMoneyPrecision.ToString(CultureInfo.InvariantCulture)), $"decimal(19, {TypeMapping.MoneyScale})");

    /// <summary>
    /// The row a definition is evaluated on: its alias and table, and the calculated column being written (for messages); for a
    /// rollup's aggregated column or filter, <paramref name="Rollup"/> names the rollup column instead (its table is not the row's).
    /// </summary>
    private sealed record Row(string Alias, EntityMetadata Entity, AttributeMetadata Column, int Depth, string? Rollup = null)
    {
        public string Where => Rollup ?? $"'{Entity.LogicalName}.{Column.LogicalName}'";

        /// <summary>The row computes a formula column (plan 2.12c): Power Fx semantics apply (<c>ComputedSql.Formula.cs</c>).</summary>
        public bool Formula => Rollup is null && Column.SourceType == ComputedColumnRules.Formula;

        /// <summary>A formula column is in the chain of this row (F10).</summary>
        public bool FormulaChain { get; init; }
    }

    private static XrmdValidationException Refuse(Row row, string reason) =>
        new($"The {(row.Rollup is not null ? "rollup" : row.Formula ? "formula" : "calculated")} column {row.Where} {reason}.");

    // ---- result type (K8) ----------------------------------------------------------------------------------------------

    private string Result(Row row, AttributeMetadata attribute, string value) => attribute switch
    {
        StringAttributeMetadata s => dialect.DatabaseText(
            dialect.Left(value, (s.MaxLength ?? TypeMapping.DefaultStringLength).ToString(CultureInfo.InvariantCulture))),
        IntegerAttributeMetadata => dialect.TryCast(value, "int"),
        DecimalAttributeMetadata d => Decimal(value, d.Precision ?? TypeMapping.DefaultDecimalPrecision),
        MoneyAttributeMetadata m => dialect.TryCast(dialect.Round(value, MoneyPrecision(row, m)), $"decimal(19, {TypeMapping.MoneyScale})"),
        BooleanAttributeMetadata => $"CAST({value} AS {dialect.ColumnType(SqlColumnKind.Boolean)})",
        DoubleAttributeMetadata => $"CAST({value} AS float)",
        PicklistAttributeMetadata => dialect.TryCast(value, "int"),
        DateTimeAttributeMetadata { DateTimeBehavior: var b } when b == DateTimeBehavior.DateOnly => $"CAST({value} AS date)",
        DateTimeAttributeMetadata => $"CAST({value} AS {dialect.ColumnType(SqlColumnKind.DateTime)})",
        _ => throw Refuse(row, $"has the type {attribute.AttributeType}, which cannot be calculated"),
    };

    private string Decimal(string value, int precision)
    {
        var p = precision.ToString(CultureInfo.InvariantCulture);
        return dialect.TryCast(dialect.Round(value, p), $"decimal(38, {p})");
    }

    /// <summary>
    /// The decimals a money value is rounded to (<see cref="CurrencyMath.EffectivePrecision"/>): the column's <c>Precision</c>
    /// (<c>PrecisionSource</c> 0), the organization's pricing precision (1) or the precision of the row's currency (2, the default).
    /// </summary>
    private string MoneyPrecision(Row row, MoneyAttributeMetadata m)
    {
        switch (m.PrecisionSource)
        {
            case 0:
                return Math.Clamp(m.Precision ?? CurrencyMath.DefaultPrecision, CurrencySchema.MinPrecision, CurrencySchema.MaxPrecision)
                    .ToString(CultureInfo.InvariantCulture);
            case 1:
                var organization = Apply(row.Alias, "organization",
                    $"({dialect.Limit($"SELECT {Q(CurrencySchema.PricingPrecision)} FROM {DataTable("organization")}", "(1)")})");
                return $"COALESCE({organization}.{Q(CurrencySchema.PricingPrecision)}, {CurrencySchema.DefaultPricingPrecision})";
            default:
                var currency = Apply(row.Alias, "currency",
                    $"(SELECT {Q(CurrencySchema.Precision)} FROM {DataTable(CurrencySchema.Table)} " +
                    $"WHERE {Q(CurrencySchema.CurrencyId)} = {row.Alias}.{Q(CurrencySchema.CurrencyId)})");
                return $"COALESCE({currency}.{Q(CurrencySchema.Precision)}, {CurrencyMath.DefaultPrecision})";
        }
    }

    /// <summary>The alias of the apply <paramref name="key"/> of the row <paramref name="owner"/>, added the first time it is needed.</summary>
    private string Apply(string owner, string key, string source)
    {
        if (_aliases.TryGetValue((owner, key), out var alias)) return alias;
        alias = "c" + _aliases.Count.ToString(CultureInfo.InvariantCulture);
        _aliases[(owner, key)] = alias;
        var scope = _scopeOf.GetValueOrDefault(owner, owner);
        _scopeOf[alias] = scope;
        if (!_applies.TryGetValue(scope, out var list)) _applies[scope] = list = [];
        list.Add(dialect.Apply(source, alias, outer: true));
        return alias;
    }

    // ---- values --------------------------------------------------------------------------------------------------------

    /// <summary>The numeric SQL type of a value: an <c>int</c>-like integer, a <c>decimal(p, s)</c> or a <c>float</c>.</summary>
    private enum NumKind { Integer, Decimal, Float }

    /// <summary>A numeric type; for <see cref="NumKind.Integer"/>, <paramref name="Precision"/> is its digits (10 int, 19 bigint).</summary>
    private readonly record struct Num(NumKind Kind, int Precision, int Scale)
    {
        public int IntegerDigits => Precision - Scale;

        public static Num Dec(int precision, int scale) => new(NumKind.Decimal, Math.Max(1, precision), scale);
    }

    /// <summary>
    /// A value's SQL and, for a number, its SQL type (null: not a number, or an untyped NULL); <paramref name="Constant"/> is the
    /// text of a number constant (<c>2.5</c>), which a cast writes directly.
    /// </summary>
    private sealed record Typed(string Sql, Num? Type = null, string? Constant = null)
    {
        /// <summary>A formula value already checked against the range of decimals (<see cref="Limit"/>).</summary>
        public bool InRange { get; init; }

        /// <summary>A yes/no value (SQL <c>bit</c>), which <see cref="Once"/> computes through <c>tinyint</c> (<c>MAX</c> takes no bit).</summary>
        public bool Bit { get; init; }
    }

    private static readonly Num Int = new(NumKind.Integer, 10, 0);
    private static readonly Num BigInt = new(NumKind.Integer, 19, 0);
    private static readonly Num Float = new(NumKind.Float, 53, 0);
    private static readonly Num MoneyType = Num.Dec(19, TypeMapping.MoneyScale);

    /// <summary>The widest decimal type SQL Server computes in.</summary>
    private const int MaxDigits = 38;

    /// <summary>
    /// The most decimals an operand of the arithmetic keeps: more than a column holds (10), few enough that a product of two
    /// operands keeps 13 integer digits within 38.
    /// </summary>
    private const int MaxOperandScale = 12;

    /// <summary>The type of the operands of a division, which SQL Server divides with 10 decimals (the most a column keeps).</summary>
    private static readonly Num Division = Num.Dec(28, 10);

    private string Value(Row row, ComputedExpr e) => Eval(row, e).Sql;

    /// <summary>
    /// The value of <paramref name="e"/> on <paramref name="row"/>. On a formula row (final review C1, I1): a node of the definition
    /// that occurs more than once (<see cref="Share"/>) is computed once, in an apply, and read as <c>cN.[v]</c> wherever it occurs;
    /// a value whose <c>CASE</c> nesting passes <see cref="MaxInlineNesting"/> is moved to an apply of its own, so that no expression
    /// nests <c>CASE</c> more than 10 deep (SQL Server error 125); a value longer than <see cref="MaxSqlLength"/> is refused.
    /// </summary>
    private Typed Eval(Row row, ComputedExpr e)
    {
        if (!row.Formula) return Node(row, e);
        var shared = _shared.Contains(e);
        if (shared && _values.TryGetValue((row.Alias, e), out var known)) return known;
        var value = Node(row, e);
        if (value.Sql.Length > MaxSqlLength) throw TooComplex(row);
        if (shared || CaseNesting(value.Sql) > MaxInlineNesting) value = Once(row, value);
        if (shared) _values[(row.Alias, e)] = value;
        return value;
    }

    private Typed Node(Row row, ComputedExpr e) => e switch
    {
        ComputedExpr.Literal literal => Literal(row, literal.Value),
        ComputedExpr.Column column => Column(row, column),
        ComputedExpr.Now => new Typed(dialect.UtcNow),
        // Plan 2.12c: a decimal intermediate of a formula outside ±100,000,000,000 is null.
        ComputedExpr.Binary binary => row.Formula ? Limit(row, Binary(row, binary)) : Binary(row, binary),
        ComputedExpr.Function function => Function(row, function),
        ComputedExpr.Conditional conditional => Conditional(row, conditional),
        _ => throw Refuse(row, $"uses an expression that cannot be computed ({e.GetType().Name})"),
    };

    private Typed Literal(Row row, object? value) => value switch
    {
        null => new Typed("NULL"),
        string s => new Typed(parameters.Add(DbType.String, s)),
        bool b => new Typed($"CAST({dialect.BooleanLiteral(b)} AS {dialect.ColumnType(SqlColumnKind.Boolean)})") { Bit = true },
        int i => WholeLiteral(i.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture)),
        long l => WholeLiteral($"CAST({l.ToString(CultureInfo.InvariantCulture)} AS bigint)", l.ToString(CultureInfo.InvariantCulture)),
        decimal d => DecimalLiteral(d),
        double f when double.IsFinite(f) => new Typed($"CAST({f.ToString("R", CultureInfo.InvariantCulture)} AS float)", Float),
        DateTime t => new Typed(parameters.Add(DbType.DateTime2, t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t)),
        _ => throw Refuse(row, $"uses a constant of type {value.GetType().Name}, which cannot be computed"),
    };

    /// <summary>A whole constant, typed by its own digits (<c>40</c>: 2 digits).</summary>
    private static Typed WholeLiteral(string sql, string text) => new(sql, new Num(NumKind.Integer, text.TrimStart('-').Length, 0), text);

    /// <summary>A decimal constant as a <c>decimal</c> of its own digits and scale (<c>2.5</c> → <c>decimal(2, 1)</c>).</summary>
    private static Typed DecimalLiteral(decimal d)
    {
        var text = d.ToString(CultureInfo.InvariantCulture);
        var digits = text.TrimStart('-');
        var point = digits.IndexOf('.', StringComparison.Ordinal);
        var scale = point < 0 ? 0 : digits.Length - point - 1;
        var integer = (point < 0 ? digits : digits[..point]).TrimStart('0').Length;
        var type = Num.Dec(integer + scale, scale);
        return new Typed($"CAST({text} AS {SqlType(type)})", type, text);
    }

    /// <summary>The SQL type of a column's value (null for a column that is not a number).</summary>
    private static Num? TypeOf(AttributeMetadata a) => a switch
    {
        IntegerAttributeMetadata => Int,
        BigIntAttributeMetadata => BigInt,
        MoneyAttributeMetadata => MoneyType,
        DecimalAttributeMetadata d => Num.Dec(TypeMapping.DecimalDigits(d.Precision ?? TypeMapping.DefaultDecimalPrecision),
            d.Precision ?? TypeMapping.DefaultDecimalPrecision),
        DoubleAttributeMetadata => Float,
        _ => null,
    };

    /// <summary>The SQL type of a calculated column's value, as its K8 cast leaves it (<see cref="Result"/>).</summary>
    private static Num? ResultTypeOf(AttributeMetadata a) => a switch
    {
        IntegerAttributeMetadata or PicklistAttributeMetadata => Int,
        DecimalAttributeMetadata d => Num.Dec(MaxDigits, d.Precision ?? TypeMapping.DefaultDecimalPrecision),
        MoneyAttributeMetadata => MoneyType,
        DoubleAttributeMetadata => Float,
        _ => null,
    };

    /// <summary>
    /// A column of the row, or of the parent record its lookup points to. A calculated column enters as its own value (cast to its
    /// type), a <c>{money}_base</c> as its stored column (or the base of a calculated money column), a money column of the parent
    /// record as its base value times the row's exchange rate (K8).
    /// </summary>
    private Typed Column(Row row, ComputedExpr.Column column)
    {
        var (alias, owner) = column.Lookup is null ? (row.Alias, row.Entity) : Parent(row, column.Lookup);
        var a = owner.Attributes?.FirstOrDefault(x => x.LogicalName == column.Attribute)
                ?? throw Refuse(row, $"reads the column '{owner.LogicalName}.{column.Attribute}', which does not exist");
        if (ComputedColumnRules.IsComputed(a))
        {
            var value = Chained(row, alias, owner, a);
            // A formula row reads a chained column once (final review C1): read 4 times by each link, it was written 4^n times.
            if (row.Formula || row.FormulaChain) value = Once(row, value, bit: a is BooleanAttributeMetadata);
            return column.Lookup is not null && a is MoneyAttributeMetadata
                ? InRowCurrency(row, ToBase(alias, value))
                : new Typed(value, ResultTypeOf(a)) { Bit = a is BooleanAttributeMetadata };
        }
        if (a is MoneyAttributeMetadata { IsBaseCurrency: true } baseMoney)
        {
            // A base value is in the base currency, of the row or of its parent: it is read as is, from the column of a stored
            // money column or computed from a calculated one.
            var main = owner.Attributes?.FirstOrDefault(x => x.LogicalName + TypeMapping.BaseColumnSuffix == baseMoney.LogicalName);
            if (main is MoneyAttributeMetadata { SourceType: ComputedColumnRules.Calculated } calculatedMoney)
                return new Typed(ToBase(alias, Chained(row, alias, owner, calculatedMoney)), MoneyType);
            return new Typed($"{alias}.{Q(a.LogicalName)}", MoneyType);
        }
        // The definition check refuses a column without a value of its own (CalculatedTranslator); a guard here.
        if (TypeMapping.ToColumns(a).Count == 0)
            throw Refuse(row, $"reads the column '{owner.LogicalName}.{a.LogicalName}', which has no value of its own");
        if (column.Lookup is not null && a is MoneyAttributeMetadata)
            return InRowCurrency(row, $"{alias}.{Q(a.LogicalName + TypeMapping.BaseColumnSuffix)}");
        return new Typed($"{alias}.{Q(a.LogicalName)}", TypeOf(a)) { Bit = a is BooleanAttributeMetadata };
    }

    /// <summary>
    /// The value of the computed column <paramref name="a"/> of <paramref name="owner"/> on the row <paramref name="alias"/>, read by
    /// <paramref name="row"/>: written once per compilation for each row, depth and kind of chain (final review C1), the checks of the
    /// chain (depth, F10) being the same for the same depth.
    /// </summary>
    private string Chained(Row row, string alias, EntityMetadata owner, AttributeMetadata a)
    {
        var key = (alias, owner.LogicalName, a.LogicalName, row.Depth + 1, row.FormulaChain);
        if (_chained.TryGetValue(key, out var known)) return known;
        return _chained[key] = Expression(alias, owner, a, computed.Translate(owner, a, entities), row.Depth + 1, row.FormulaChain);
    }

    /// <summary>
    /// A base-currency value in the row's currency: times its exchange rate, as a <c>decimal(28, 10)</c> (null when it does not fit);
    /// as is when the row's table has no currency.
    /// </summary>
    private Typed InRowCurrency(Row row, string baseValue) =>
        CurrencySchema.HasCurrencyColumns(row.Entity)
            ? new Typed(dialect.TryCast($"({baseValue} * {row.Alias}.{Q(CurrencySchema.ExchangeRate)})", SqlType(Division)), Division)
            : new Typed(baseValue, MoneyType);

    /// <summary>The apply of the parent record a single-target lookup of the row points to, and its table.</summary>
    private (string Alias, EntityMetadata Entity) Parent(Row row, string lookup)
    {
        var a = row.Entity.Attributes?.FirstOrDefault(x => x.LogicalName == lookup);
        if (a is not LookupAttributeMetadata { Targets: [var target] })
            throw Refuse(row, $"reads a parent record through '{lookup}', which is not a lookup to one table");
        var parent = entities(target) ?? throw Refuse(row, $"reads a parent record of the table '{target}', which does not exist");
        var alias = Apply(row.Alias, "parent:" + lookup,
            $"(SELECT * FROM {DataTable(parent.LogicalName)} WHERE {Q(SystemAttributes.PrimaryIdName(parent))} = {row.Alias}.{Q(lookup)})");
        return (alias, parent);
    }

    /// <summary>
    /// The arithmetic, in exact decimals whose result type never exceeds 38 digits, so that SQL Server never cuts the scale of a
    /// result (to 6 decimals) and never overflows (error 8115): each operand is cast to a type chosen for the operation, with
    /// <c>TRY_CAST</c> when its value may not fit (null then, K8). A division is <c>decimal(28, 10) / decimal(28, 10)</c>, which
    /// gives 10 decimals (the most a column keeps); a product or a sum keeps the scales of its operands (at most
    /// <see cref="MaxOperandScale"/>) and narrows their integer digits when the result would need more than 38. A <c>float</c>
    /// operand makes the operation a <c>float</c> one. A whole number is never divided as one (K7).
    /// </summary>
    private Typed Binary(Row row, ComputedExpr.Binary binary)
    {
        if (binary.Operator is not ("Add" or "Subtract" or "Multiply" or "Divide"))
            throw Refuse(row, $"uses the operator '{binary.Operator}', which cannot be computed");
        var left = Eval(row, binary.Left);
        var right = Eval(row, binary.Right);
        var op = binary.Operator switch { "Add" => "+", "Subtract" => "-", "Multiply" => "*", _ => "/" };
        // K7: a division by zero gives null.
        string Write(string l, string r) => op == "/" ? $"({l} / NULLIF({r}, 0))" : $"({l} {op} {r})";

        if (left.Type is { Kind: NumKind.Float } || right.Type is { Kind: NumKind.Float })
            return new Typed(Write(left.Sql, right.Sql), Float);

        var l = Operand(left);
        var r = Operand(right);
        switch (op)
        {
            case "/":
                // (28, 10) / (28, 10): precision 67 and scale 39, which SQL Server reduces to (38, 10).
                return new Typed(Write(Cast(left, Division), Cast(right, Division)), Num.Dec(MaxDigits, Division.Scale));
            case "*":
            {
                // (p1, s1) * (p2, s2) is (p1 + p2 + 1, s1 + s2).
                var scale = l.Scale + r.Scale;
                var budget = MaxDigits - 1 - scale; // the integer digits the two operands may have together
                var (li, ri) = (l.IntegerDigits, r.IntegerDigits);
                if (li + ri > budget)
                {
                    // The narrower operand keeps its digits (up to half the budget), the other one gets the rest.
                    ri = Math.Min(ri, Math.Max(budget - li, budget / 2));
                    li = Math.Min(li, budget - ri);
                }
                var lt = Num.Dec(li + l.Scale, l.Scale);
                var rt = Num.Dec(ri + r.Scale, r.Scale);
                return new Typed(Write(Cast(left, lt), Cast(right, rt)), Num.Dec(lt.Precision + rt.Precision + 1, scale));
            }
            default:
            {
                // (p1, s1) ± (p2, s2) is (max(s1, s2) + max(p1 - s1, p2 - s2) + 1, max(s1, s2)).
                var scale = Math.Max(l.Scale, r.Scale);
                var integer = Math.Min(Math.Max(l.IntegerDigits, r.IntegerDigits), MaxDigits - 1 - scale);
                var lt = Num.Dec(Math.Min(l.IntegerDigits, integer) + l.Scale, l.Scale);
                var rt = Num.Dec(Math.Min(r.IntegerDigits, integer) + r.Scale, r.Scale);
                return new Typed(Write(Cast(left, lt), Cast(right, rt)), Num.Dec(integer + scale + 1, scale));
            }
        }
    }

    /// <summary>
    /// The decimal type a value enters the arithmetic as: its own digits (a whole number with scale 0), at most
    /// <see cref="MaxOperandScale"/> decimals; an untyped value (NULL) as one digit.
    /// </summary>
    private static Num Operand(Typed value)
    {
        if (value.Type is not { } t) return Num.Dec(1, 0);
        var scale = Math.Min(t.Scale, MaxOperandScale);
        return Num.Dec(Math.Min(t.IntegerDigits, MaxDigits - scale) + scale, scale);
    }

    private static string SqlType(Num type) =>
        $"decimal({type.Precision.ToString(CultureInfo.InvariantCulture)}, {type.Scale.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>
    /// <paramref name="value"/> as the decimal <paramref name="target"/>: as is when it has that type or no type (NULL); a
    /// <c>CAST</c> when every value of its type fits (a constant written directly as the target); a <c>TRY_CAST</c> otherwise, null
    /// when the value does not fit (K8).
    /// </summary>
    private string Cast(Typed value, Num target)
    {
        if (value.Type is not { } type) return value.Sql;
        if (type == target) return value.Sql;
        var fits = type.IntegerDigits <= target.IntegerDigits && type.Scale <= target.Scale;
        if (!fits) return dialect.TryCast(value.Sql, SqlType(target));
        return $"CAST({value.Constant ?? value.Sql} AS {SqlType(target)})";
    }

    private Typed Function(Row row, ComputedExpr.Function f)
    {
        var name = f.Name;
        if (name.StartsWith(FormulaPrefix, StringComparison.Ordinal)) return FormulaFunction(row, f);
        if (Shift(name) is { } shift)
        {
            Arity(row, f, 2);
            var count = Value(row, f.Args[0]);
            var date = Value(row, f.Args[1]);
            return new Typed(dialect.AddInterval(shift, name.StartsWith("SUBTRACT", StringComparison.Ordinal) ? $"-({count})" : count, date));
        }
        if (name.StartsWith("DIFFIN", StringComparison.Ordinal) && Part(name[6..]) is { } part)
        {
            Arity(row, f, 2);
            // K7: the number of boundaries crossed (the same day, month or year gives zero).
            return new Typed(dialect.DateDiff(part, Value(row, f.Args[0]), Value(row, f.Args[1])), Int);
        }
        switch (name)
        {
            case "CONCAT":
                if (f.Args.Count < 2) throw Refuse(row, "uses CONCAT with fewer than 2 arguments");
                // K7: a null part is empty (T-SQL CONCAT).
                return new Typed(dialect.DatabaseText($"CONCAT({string.Join(", ", f.Args.Select(a => Value(row, a)))})"));
            case "TRIMLEFT":
            {
                Arity(row, f, 2);
                return new Typed(dialect.DatabaseText(dialect.Substring(Value(row, f.Args[0]), $"{Value(row, f.Args[1])} + 1",
                    TypeMapping.MaxNvarcharLength.ToString(CultureInfo.InvariantCulture))));
            }
            case "TRIMRIGHT":
            {
                Arity(row, f, 2);
                var text = Value(row, f.Args[0]);
                var count = Value(row, f.Args[1]);
                // LEN ignores trailing spaces: the length of the text with one more character, less one. Removing as many
                // characters as the text has, or more, leaves it empty (K7).
                var length = $"{dialect.TextLength($"CONCAT({text}, {dialect.TextLiteral(".")})")} - 1";
                return new Typed(dialect.DatabaseText($"CASE WHEN {text} IS NULL THEN NULL WHEN {length} <= {count} THEN {dialect.TextLiteral("")} " +
                                                      $"ELSE {dialect.Left(text, $"{length} - {count}")} END"));
            }
            default:
                throw Refuse(row, $"uses the function '{name}', which cannot be computed");
        }
    }

    private static string? Shift(string name)
    {
        var unit = name.StartsWith("ADD", StringComparison.Ordinal) ? name[3..]
            : name.StartsWith("SUBTRACT", StringComparison.Ordinal) ? name[8..] : null;
        return unit is "HOURS" or "DAYS" or "WEEKS" or "MONTHS" or "YEARS" ? Part(unit) : null;
    }

    private static string? Part(string unit) => unit switch
    {
        "MINUTES" => "minute",
        "HOURS" => "hour",
        "DAYS" => "day",
        "WEEKS" => "week",
        "MONTHS" => "month",
        "YEARS" => "year",
        _ => null,
    };

    private static void Arity(Row row, ComputedExpr.Function f, int count)
    {
        if (f.Args.Count != count) throw Refuse(row, $"uses {f.Name} with {f.Args.Count} argument(s): it needs {count}");
    }

    /// <summary>
    /// A <c>CASE WHEN</c>. When its values are decimals, each one is cast to one type that holds them all (at most 38 digits), so
    /// that SQL Server does not reduce the scale of the result; whole numbers and floats are left to SQL Server.
    /// </summary>
    private Typed Conditional(Row row, ComputedExpr.Conditional conditional)
    {
        var whens = conditional.Branches.Select(b => (When: Condition(row, b.When), Then: Eval(row, b.Then))).ToList();
        var otherwise = conditional.Otherwise is { } o ? Eval(row, o) : null;
        var values = whens.Select(w => w.Then).Append(otherwise).OfType<Typed>().ToList();
        var types = values.Where(v => v.Type is not null).Select(v => v.Type!.Value).ToList();
        Num? type = null;
        if (types.Count > 0 && values.All(v => v.Type is not null || v.Sql == "NULL"))
        {
            if (types.Any(t => t.Kind == NumKind.Float)) type = Float;
            else if (types.All(t => t.Kind == NumKind.Integer)) type = types.MaxBy(t => t.Precision);
            else
            {
                var scale = types.Max(t => Math.Min(t.Scale, MaxOperandScale));
                type = Num.Dec(Math.Min(types.Max(t => t.IntegerDigits), MaxDigits - scale) + scale, scale);
            }
        }
        string Write(Typed v) => type is { Kind: NumKind.Decimal } t ? Cast(v, t) : v.Sql;

        var sql = new StringBuilder("CASE");
        foreach (var (when, then) in whens) sql.Append(" WHEN ").Append(when).Append(" THEN ").Append(Write(then));
        if (otherwise is not null) sql.Append(" ELSE ").Append(Write(otherwise));
        // A yes/no result when every value is one (or NULL).
        var bit = values.Count > 0 && values.Any(v => v.Bit) && values.All(v => v.Bit || v.Sql == "NULL");
        return new Typed(sql.Append(" END").ToString(), type) { Bit = bit };
    }

    // ---- conditions ----------------------------------------------------------------------------------------------------

    private string Condition(Row row, ComputedCondition c) => c switch
    {
        ComputedCondition.And and => $"({Condition(row, and.Left)} AND {Condition(row, and.Right)})",
        ComputedCondition.Or or => $"({Condition(row, or.Left)} OR {Condition(row, or.Right)})",
        ComputedCondition.Not not => $"(NOT ({Condition(row, not.Operand)}))",
        ComputedCondition.Compare compare => Compare(row, compare),
        _ => throw Refuse(row, $"uses a condition that cannot be computed ({c.GetType().Name})"),
    };

    /// <summary>A comparison, with the SQL operators <see cref="ConditionCompiler"/> gives the same <c>ConditionOperator</c> names.</summary>
    private string Compare(Row row, ComputedCondition.Compare c)
    {
        if (row.Formula && EmptyTextComparison(row, c) is { } empty) return empty;
        var left = Value(row, c.Left);
        if (c.Operator is "Null") return $"{left} IS NULL";
        if (c.Operator is "NotNull") return $"{left} IS NOT NULL";
        var operand = c.Right ?? throw Refuse(row, $"uses {c.Operator} without a value");
        return c.Operator switch
        {
            "Equal" => $"{left} = {Value(row, operand)}",
            "NotEqual" => $"{left} <> {Value(row, operand)}",
            "GreaterThan" => $"{left} > {Value(row, operand)}",
            "GreaterEqual" => $"{left} >= {Value(row, operand)}",
            "LessThan" => $"{left} < {Value(row, operand)}",
            "LessEqual" => $"{left} <= {Value(row, operand)}",
            "Contains" => $"{left} LIKE {Pattern(row, operand, "%", "%")}",
            "DoesNotContain" => $"{left} NOT LIKE {Pattern(row, operand, "%", "%")}",
            "BeginsWith" => $"{left} LIKE {Pattern(row, operand, "", "%")}",
            "EndsWith" => $"{left} LIKE {Pattern(row, operand, "%", "")}",
            var op => throw Refuse(row, $"uses the condition operator '{op}', which cannot be computed"),
        };
    }

    /// <summary>
    /// The LIKE pattern of a text: a constant is escaped (<see cref="ConditionCompiler.EscapeLike"/>) into one parameter; any other
    /// value is escaped by the query.
    /// </summary>
    private string Pattern(Row row, ComputedExpr operand, string before, string after)
    {
        if (operand is ComputedExpr.Literal { Value: string text })
            return parameters.Add(DbType.String, dialect.LikePattern(before + ConditionCompiler.EscapeLike(text) + after));
        var escaped = dialect.EscapeLike(Value(row, operand));
        var parts = new List<string>(3);
        if (before.Length > 0) parts.Add(dialect.TextLiteral(before));
        parts.Add(escaped);
        if (after.Length > 0) parts.Add(dialect.TextLiteral(after));
        return $"CONCAT({string.Join(", ", parts)})";
    }
}
