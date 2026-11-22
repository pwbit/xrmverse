using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata.Computed;

namespace Xrmd.Query.Sql;

/// <summary>
/// The T-SQL of the <c>fx.*</c> functions of formula columns (plan 2.12c, Task 3): the Power Fx functions <c>FormulaTranslator</c>
/// writes into a <see cref="ComputedExpr"/>, with Power Fx semantics where T-SQL differs (Mod takes the divisor's sign, Len counts
/// trailing spaces, Trim collapses inner spaces, Weekday does not depend on <c>@@DATEFIRST</c>, Text formats without <c>FORMAT()</c>,
/// Substitute is case-sensitive). A value outside a function's domain (Ln of 0, Sqrt of -1, a zero divisor, text that is not a
/// number) gives null, never a SQL error.
/// </summary>
/// <remarks>
/// The SQL of a formula grows with the formula, never exponentially (final review C1), and nests <c>CASE</c> at most 10 deep (SQL
/// Server error 125; I1): a value a function writes more than once (<see cref="Once"/>), a node of the definition that occurs more
/// than once — the sides of a blank-safe <c>=</c>, the subject of <c>Switch</c>, a yes/no value tested for blank and for true —
/// (<see cref="Share"/>, <see cref="Eval"/>) and a chained computed column (<see cref="Chained"/>) are each computed once per row, by
/// an apply that SQL Server cannot inline (<see cref="Once"/>) read as <c>cN.[v]</c>; a value nested deeper than <see cref="MaxInlineNesting"/> is moved
/// to an apply of its own (an apply starts a new expression). A formula whose SQL would still pass <see cref="MaxSqlLength"/> or
/// hold more than <see cref="MaxApplies"/> applies, whose compilation grows faster than they do (final review N1), is refused in
/// our words (<see cref="FormulaSqlCheck"/> when it is defined), never left to run out of memory or of stack space; the bounds are
/// per column, and a query reading several columns has twice them (final review M1).
/// </remarks>
internal sealed partial class ComputedSql
{
    /// <summary>The prefix of the function names of formula columns.</summary>
    private const string FormulaPrefix = "fx.";

    /// <summary>The largest magnitude of a decimal value of a formula (Global Constraints: ±100,000,000,000).</summary>
    private const string FormulaRange = "100000000000";

    /// <summary>The integer digits of a value within <see cref="FormulaRange"/>.</summary>
    private const int FormulaIntegerDigits = 12;

    /// <summary>The type a text becomes as a number (<c>Value</c>, <c>Decimal</c>): 12 integer digits, 10 decimals.</summary>
    private static readonly Num TextNumber = Num.Dec(FormulaIntegerDigits + 10, 10);

    /// <summary>
    /// The first and the last value of <c>datetime2</c>, the edges of <c>DateAdd</c> (on every engine: SQL Server's range; the last one
    /// to the microsecond on PostgreSQL, <see cref="ISqlDialect.LastDateTime"/>).
    /// </summary>
    private string FirstDate => $"CAST('0001-01-01' AS {dialect.ColumnType(SqlColumnKind.DateTime)})";

    private string LastDate => dialect.LastDateTime;

    /// <summary>A length no text reaches: the rest of a text for <c>SUBSTRING</c>.</summary>
    private const string Rest = "2147483647";

    /// <summary>
    /// The longest SQL a formula column may compile to: one value, or its applies together (final review C1). SQL Server's compile
    /// time grows faster than the text (SQL Server 2022, Substitute searches: 120,000 characters 0.2 s, 330,000 1.4 s, 690,000
    /// 6.3 s), so the bound keeps one column within about a second (final review N1).
    /// </summary>
    internal const int MaxSqlLength = 300_000;

    /// <summary>
    /// The most applies (intermediate values) the SQL of one formula column may hold (final review N1). SQL Server's compile time
    /// grows faster than the applies of a query (SQL Server 2022, chained sums: 90 applies 0.1 s, 177 0.4 s, 264 0.9 s, 351 2.1 s,
    /// 438 3.5 s, 873 25 s; about 2,900 ran out of stack space, Msg 8621), so 250 keeps one column within about a second.
    /// </summary>
    internal const int MaxApplies = 250;

    /// <summary>
    /// The most applies of all the calculated and formula columns a query reads together, each within <see cref="MaxApplies"/>
    /// (final review M1): two columns at the bound. SQL Server 2022 compiles 525 applies of chained sums in 5.7 s (873 in 25 s),
    /// well within the 30 s a command may run (SqlClient's default <c>CommandTimeout</c>, which xrmverse keeps).
    /// </summary>
    internal const int MaxQueryApplies = 2 * MaxApplies;

    /// <summary>
    /// The longest SQL of the applies of all the calculated and formula columns a query reads together: two columns at
    /// <see cref="MaxSqlLength"/> (SQL Server 2022: 487,000 characters 2.9 s, 690,000 6.3 s).
    /// </summary>
    internal const int MaxQuerySqlLength = 2 * MaxSqlLength;

    /// <summary>
    /// The deepest <c>CASE</c> nesting (<see cref="CaseNesting"/>) a value of a formula keeps inline: deeper, it moves to an apply. The
    /// node above it adds at most 3 levels, the result cast and the query 2 more: within the 10 of SQL Server. An apply's own
    /// expression can nest one level more than this (6, measured with the error tests of Task 6b: a value inlined into a
    /// <c>CASE WHEN … IS NULL THEN NULL …</c> of a condition that can be an error); <see cref="FormulaSqlCheck"/> refuses any formula
    /// past <see cref="MaxCaseNesting"/> when it is defined, and FormulaSqlSizeTests assert the bound.
    /// </summary>
    private const int MaxInlineNesting = 5;

    /// <summary>The deepest <c>CASE</c> nesting SQL Server takes in one expression (error 125 beyond).</summary>
    internal const int MaxCaseNesting = 10;

    /// <summary>The nodes of the formula definitions written so far that occur more than once, by reference.</summary>
    private readonly HashSet<ComputedExpr> _shared = new(ReferenceEqualityComparer.Instance);

    /// <summary>The nodes and conditions <see cref="Share"/> has walked, by reference.</summary>
    private readonly HashSet<object> _walked = new(ReferenceEqualityComparer.Instance);

    /// <summary>The value of a shared node on a row (<see cref="Eval"/>).</summary>
    private readonly Dictionary<(string Alias, ComputedExpr Node), Typed> _values = new(new NodeOnRow());

    /// <summary>The value of a chained computed column on a row, by depth and kind of chain (<see cref="Chained"/>).</summary>
    private readonly Dictionary<(string Alias, string Entity, string Column, int Depth, bool FormulaChain), string> _chained = [];

    /// <summary>The column a query reads that is being written (<see cref="BeginColumn"/>), named by a refusal of its bounds.</summary>
    private Row? _column;

    /// <summary>The number of applies when the column being written began.</summary>
    private int _columnStart;

    /// <summary>The length of the SQL of the value applies of the column being written (<see cref="Once"/>).</summary>
    private int _columnLength;

    /// <summary>The length of the SQL of the value applies of the query (<see cref="Once"/>).</summary>
    private int _queryLength;

    /// <summary>A (row alias, node) key, the node compared by reference (a record's own equality walks the whole tree).</summary>
    private sealed class NodeOnRow : IEqualityComparer<(string Alias, ComputedExpr Node)>
    {
        public bool Equals((string Alias, ComputedExpr Node) x, (string Alias, ComputedExpr Node) y) =>
            string.Equals(x.Alias, y.Alias, StringComparison.Ordinal) && ReferenceEquals(x.Node, y.Node);

        public int GetHashCode((string Alias, ComputedExpr Node) key) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(key.Alias), RuntimeHelpers.GetHashCode(key.Node));
    }

    /// <summary>
    /// Marks the nodes of the formula definition <paramref name="body"/> that occur more than once (FormulaTranslator writes one node
    /// for each use of a value: both sides of a blank-safe <c>=</c> three times, the subject of <c>Switch</c> once per branch). The walk
    /// visits each node once. A condition met again marks the values it compares.
    /// </summary>
    private void Share(ComputedExpr body)
    {
        if (_walked.Contains(body)) return;
        Walk(body);

        void Walk(ComputedExpr e)
        {
            if (!_walked.Add(e))
            {
                _shared.Add(e);
                return;
            }
            switch (e)
            {
                case ComputedExpr.Binary b:
                    Walk(b.Left);
                    Walk(b.Right);
                    break;
                case ComputedExpr.Function f:
                    foreach (var arg in f.Args) Walk(arg);
                    break;
                case ComputedExpr.Conditional c:
                    foreach (var branch in c.Branches)
                    {
                        Condition(branch.When);
                        Walk(branch.Then);
                    }
                    if (c.Otherwise is { } otherwise) Walk(otherwise);
                    break;
            }
        }

        void Condition(ComputedCondition c)
        {
            if (!_walked.Add(c))
            {
                Operands(c);
                return;
            }
            switch (c)
            {
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
                case ComputedCondition.Compare compare:
                    Walk(compare.Left);
                    if (compare.Right is { } right) Walk(right);
                    break;
            }
        }

        void Operands(ComputedCondition c)
        {
            switch (c)
            {
                case ComputedCondition.And and:
                    Operands(and.Left);
                    Operands(and.Right);
                    break;
                case ComputedCondition.Or or:
                    Operands(or.Left);
                    Operands(or.Right);
                    break;
                case ComputedCondition.Not not:
                    Operands(not.Operand);
                    break;
                case ComputedCondition.Compare compare:
                    _shared.Add(compare.Left);
                    if (compare.Right is { } right) _shared.Add(right);
                    break;
            }
        }
    }

    private static XrmdValidationException TooComplex(Row row) =>
        Refuse(row, "is too complex to compute: its SQL would exceed " +
                    $"{MaxSqlLength.ToString("N0", CultureInfo.InvariantCulture)} characters; simplify the formula");

    private static XrmdValidationException TooManyValues(Row row) =>
        Refuse(row, $"is too complex to compute: its SQL would need more than {MaxApplies} intermediate values; simplify the formula");

    private static XrmdValidationException QueryTooComplex(string what) =>
        new($"The query is too complex to compute: the calculated and formula columns it reads would together need more than {what}; " +
            "read fewer formula columns in one query.");

    /// <summary>
    /// Starts a column a query reads (or a rollup's value or filter): the bounds of one column (<see cref="MaxSqlLength"/>,
    /// <see cref="MaxApplies"/>) count from here, the bounds of the query (<see cref="MaxQuerySqlLength"/>,
    /// <see cref="MaxQueryApplies"/>) from the first column (final review M1). The computed columns it chains count with it.
    /// </summary>
    private void BeginColumn()
    {
        _column = null;
        _columnStart = _aliases.Count;
        _columnLength = 0;
    }

    /// <summary>Counts a new value apply of <paramref name="length"/> characters against the bounds of the column and of the query.</summary>
    private void Count(Row row, int length)
    {
        var column = _column ?? row;
        _columnLength += length;
        _queryLength += length;
        if (_columnLength > MaxSqlLength) throw TooComplex(column);
        if (_aliases.Count - _columnStart > MaxApplies) throw TooManyValues(column);
        if (_aliases.Count > MaxQueryApplies) throw QueryTooComplex($"{MaxQueryApplies.ToString("N0", CultureInfo.InvariantCulture)} intermediate values");
        if (_queryLength > MaxQuerySqlLength)
            throw QueryTooComplex($"{MaxQuerySqlLength.ToString("N0", CultureInfo.InvariantCulture)} characters of SQL");
    }

    /// <summary>
    /// The deepest nesting of <c>CASE</c> in one expression of <paramref name="sql"/>, counting <c>COALESCE</c>, <c>IIF</c> and
    /// <c>NULLIF</c> as SQL Server does (it rewrites them as <c>CASE</c>; checked on SQL Server 2022). Quoted text and bracketed names
    /// are skipped. An apply is never inside an expression, so the deepest nesting of a query text is the deepest of its expressions.
    /// </summary>
    internal static int CaseNesting(string sql)
    {
        var (depth, max) = (0, 0);
        var parens = new Stack<bool>();
        var counted = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            switch (c)
            {
                case '\'':
                    i = Skip(sql.IndexOf('\'', i + 1));
                    continue;
                case '[':
                    i = Skip(sql.IndexOf(']', i + 1));
                    continue;
                case '"': // a PostgreSQL quoted name (plan 2.7a, Task 7)
                    i = Skip(sql.IndexOf('"', i + 1));
                    continue;
                case '(':
                    parens.Push(counted);
                    if (counted) max = Math.Max(max, ++depth);
                    counted = false;
                    continue;
                case ')':
                    if (parens.Count > 0 && parens.Pop()) depth--;
                    continue;
            }
            if (!char.IsAsciiLetter(c) || (i > 0 && (char.IsAsciiLetterOrDigit(sql[i - 1]) || sql[i - 1] is '_' or '@' or '#'))) continue;
            var end = i;
            while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
            var word = sql.AsSpan(i, end - i);
            if (word.SequenceEqual("CASE")) max = Math.Max(max, ++depth);
            else if (word.SequenceEqual("END")) depth--;
            else counted = (word.SequenceEqual("COALESCE") || word.SequenceEqual("IIF") || word.SequenceEqual("NULLIF"))
                           && end < sql.Length && sql[end] == '(';
            i = end - 1;
        }
        return max;

        int Skip(int close) => close < 0 ? sql.Length : close;
    }

    /// <summary>A SQL text that is cheap and safe to write more than once: a column, a parameter or a constant.</summary>
    [GeneratedRegex(@"^(@\w+|\w+\.\[[^\]]+\]|\w+\.""[^""]+""|-?\d+(\.\d+)?|NULL|CAST\((-?[\d.]+|true|false) AS (decimal\(\d+, \d+\)|int|bigint|float|bit|boolean)\))$")]
    private static partial Regex SimpleSql();

    /// <summary>
    /// <paramref name="sql"/> as a value to write more than once: as is when it is simple, otherwise computed once per row by an
    /// apply of the row (written after the applies it reads) and read as <c>cN.[v]</c>. The apply is
    /// <c>OUTER APPLY (SELECT MAX(x.v) AS [v] FROM (VALUES (…)) AS x(v))</c>: SQL Server writes the value of a plain
    /// <c>OUTER APPLY (SELECT … AS [v])</c> into each expression that reads it while it compiles the query, so a chain of values each
    /// read twice took 2^n to compile (40 sums: no plan within minutes, SQL Server 2022), where the aggregate of the one-row
    /// <c>VALUES</c> is computed once and read as a column (60 links: a few milliseconds). <c>MAX</c> keeps the type of its value; it
    /// takes no <c>bit</c>, so a yes/no value (<paramref name="bit"/>) goes through <c>tinyint</c>.
    /// </summary>
    private string Once(Row row, string sql, bool bit = false)
    {
        if (SimpleSql().IsMatch(sql)) return sql;
        var applies = _aliases.Count;
        var max = bit ? dialect.MaxBoolean("x.v") : "MAX(x.v)";
        var alias = Apply(row.Alias, "value:" + sql, $"(SELECT {max} AS {Q("v")} FROM (VALUES ({sql})) AS x(v))");
        if (_aliases.Count > applies) Count(row, sql.Length);
        return alias + "." + Q("v");
    }

    private Typed Once(Row row, Typed value) =>
        value with { Sql = Once(row, value.Sql, value.Bit), Constant = SimpleSql().IsMatch(value.Sql) ? value.Constant : null };

    /// <summary>
    /// A decimal value of a formula, null outside ±100,000,000,000 (Global Constraints), typed with 12 integer digits; a float or
    /// untyped value as is.
    /// </summary>
    private Typed Limit(Row row, Typed value)
    {
        if (value.InRange || value.Type is not { Kind: not NumKind.Float } type) return value;
        var v = Once(row, value.Sql);
        var scale = Math.Min(type.Scale, MaxDigits - FormulaIntegerDigits);
        var target = Num.Dec(FormulaIntegerDigits + scale, scale);
        var cast = type == target ? v : $"CAST({v} AS {SqlType(target)})";
        return new Typed($"CASE WHEN ABS({v}) <= {FormulaRange} THEN {cast} END", target) { InRange = true };
    }

    /// <summary>
    /// <paramref name="c"/> when it compares a value with the empty text: <c>DATALENGTH(x) = 0</c>, because <c>N' ' = N''</c> is
    /// true in T-SQL (trailing spaces) and false in Power Fx (<c>IsBlank(" ")</c>); null for any other comparison.
    /// </summary>
    private string? EmptyTextComparison(Row row, ComputedCondition.Compare c)
    {
        if (c.Operator is not ("Equal" or "NotEqual") || c.Right is null) return null;
        var other = IsEmptyText(c.Right) ? c.Left : IsEmptyText(c.Left) ? c.Right : null;
        if (other is null) return null;
        return $"{dialect.DataLength(Value(row, other))} {(c.Operator == "Equal" ? "=" : "<>")} 0";
    }

    private static bool IsEmptyText(ComputedExpr e) => e is ComputedExpr.Literal { Value: "" };

    private Typed FormulaFunction(Row row, ComputedExpr.Function f)
    {
        var name = f.Name[FormulaPrefix.Length..];
        Typed A(int i) => Eval(row, f.Args[i]);
        switch (name)
        {
            // ---- blank values (F5) ----
            case "Zero":
            {
                Arity(row, f, 1);
                var x = A(0);
                return x.Sql == "NULL" ? WholeLiteral("0", "0") : new Typed($"COALESCE({x.Sql}, 0)", x.Type) { InRange = x.InRange };
            }
            case "Empty":
                Arity(row, f, 1);
                return new Typed($"COALESCE({A(0).Sql}, {dialect.TextLiteral("")})");

            // ---- text ----
            case "Concat":
                Arity(row, f, 2);
                return new Typed(dialect.DatabaseText($"CONCAT({A(0).Sql}, {A(1).Sql})"));
            case "Concatenate":
                Arities(row, f, 1, int.MaxValue);
                return new Typed(f.Args.Count == 1 ? $"COALESCE({A(0).Sql}, {dialect.TextLiteral("")})"
                    : dialect.DatabaseText($"CONCAT({string.Join(", ", f.Args.Select(a => Value(row, a)))})"));
            case "Upper" or "Lower" or "TrimEnds":
                Arity(row, f, 1);
                return new Typed(dialect.DatabaseText($"{(name == "TrimEnds" ? "TRIM" : name.ToUpperInvariant())}({A(0).Sql})"));
            case "Trim":
                // Power Fx Trim also collapses each run of inner spaces to one: ' ' → ' ' + U+0001, then U+0001 + ' ' → '', then
                // U+0001 → '' (binary collation: U+0001 is not ignorable there).
                Arity(row, f, 1);
                return new Typed(dialect.CollapseSpaces(A(0).Sql));
            case "Len":
                Arity(row, f, 1);
                return new Typed(Length(A(0).Sql), BigInt);
            case "Left" or "Right":
                Arity(row, f, 2);
                return new Typed(dialect.DatabaseText(name == "Left" ? dialect.Left(A(0).Sql, Count(row, A(1), 0)) : dialect.Right(A(0).Sql, Count(row, A(1), 0))));
            case "Mid":
                Arities(row, f, 2, 3);
                return new Typed(dialect.DatabaseText(dialect.Substring(A(0).Sql, Count(row, A(1), 1), f.Args.Count == 3 ? Count(row, A(2), 0) : Rest)));
            case "Replace":
            {
                Arity(row, f, 4);
                var text = Once(row, A(0).Sql);
                var start = Once(row, Count(row, A(1), 1));
                var count = Once(row, Count(row, A(2), 0));
                // CONCAT reads a null part as '': a null start or count (below its minimum) is checked first.
                var replaced = $"CONCAT({dialect.Left(text, $"{start} - 1")}, {A(3).Sql}, {dialect.Substring(text, $"{start} + {count}", Rest)})";
                return new Typed(dialect.DatabaseText(IsDigits(start) && IsDigits(count) ? replaced
                    : $"CASE WHEN {start} IS NULL OR {count} IS NULL THEN NULL ELSE {replaced} END"));
            }
            case "Substitute":
                Arities(row, f, 3, 4);
                return Substitute(row, f);
            case "Char":
                Arity(row, f, 1);
                return new Typed(dialect.CharFromCode(Whole(A(0), "int")));
            case "Text":
                Arity(row, f, 2);
                return f.Args[1] is ComputedExpr.Literal { Value: string format }
                    ? Text(row, A(0), format)
                    : throw Refuse(row, "uses Text with a format that is not a constant text");
            case "In" or "ExactIn":
            {
                Arity(row, f, 2);
                var part = Once(row, A(0).Sql);
                var searched = name == "ExactIn" ? dialect.BinaryCollate(part) : part;
                return Bit($"{dialect.DataLength(part)} = 0 OR {dialect.IndexOf(searched, A(1).Sql)} > 0");
            }
            case "StartsWith" or "EndsWith":
            {
                // Equal lengths compare without the trailing-space padding of '='; case-insensitive, as Power Fx.
                Arity(row, f, 2);
                var text = Once(row, A(0).Sql);
                var part = Once(row, A(1).Sql);
                var count = $"{dialect.DataLength(part)} / 2";
                var side = dialect.DatabaseText(name == "StartsWith" ? dialect.Left(text, count) : dialect.Right(text, count));
                return Bit($"{dialect.DataLength(text)} >= {dialect.DataLength(part)} AND {side} = {part}");
            }

            // ---- numbers ----
            case "Abs":
            {
                Arity(row, f, 1);
                var x = A(0);
                return x.Type is { Kind: NumKind.Integer } ? new Typed($"ABS(CAST({x.Sql} AS bigint))", BigInt) : new Typed($"ABS({x.Sql})", x.Type);
            }
            case "Int" or "Trunc" or "Round" or "RoundDown" or "RoundUp":
                if (name == "Int") Arity(row, f, 1);
                else if (name == "Trunc") Arities(row, f, 1, 2);
                else Arity(row, f, 2);
                return Rounded(row, name, A(0), f.Args.Count == 2 ? A(1) : null);
            case "Mod":
                Arity(row, f, 2);
                return Mod(row, A(0), A(1));
            case "Power":
            {
                Arity(row, f, 2);
                var x = Once(row, ToFloat(A(0)));
                var y = Once(row, ToFloat(A(1)));
                return new Typed($"CASE WHEN {x} = 0 AND {y} < 0 THEN NULL WHEN {x} < 0 AND {y} <> FLOOR({y}) THEN NULL " +
                                 $"WHEN {y} * LOG10(ABS(NULLIF({x}, 0))) > 308 THEN NULL ELSE POWER({x}, {y}) END", Float);
            }
            case "Exp":
            {
                Arity(row, f, 1);
                var x = Once(row, A(0));
                return new Typed($"CASE WHEN {x.Sql} > 709 THEN NULL ELSE EXP({ToFloat(x)}) END", Float);
            }
            case "Ln" or "Sqrt":
            {
                Arity(row, f, 1);
                var x = Once(row, A(0));
                var argument = $"CASE WHEN {x.Sql} {(name == "Ln" ? ">" : ">=")} 0 THEN {ToFloat(x)} END";
                return new Typed(name == "Ln" ? dialect.NaturalLog(argument) : $"SQRT({argument})", Float);
            }
            case "Sum" or "Average":
            {
                Arities(row, f, 1, int.MaxValue);
                var sum = f.Args.Skip(1).Aggregate(f.Args[0], (l, r) => new ComputedExpr.Binary("Add", l, r));
                return Eval(row, name == "Sum" ? sum : new ComputedExpr.Binary("Divide", sum, new ComputedExpr.Literal((decimal)f.Args.Count)));
            }
            case "Max" or "Min":
            {
                Arities(row, f, 1, int.MaxValue);
                var values = f.Args.Select(a => Eval(row, a)).ToList();
                if (values.Count == 1) return values[0];
                var (sqls, type) = Common(values);
                return new Typed($"{(name == "Max" ? "GREATEST" : "LEAST")}({string.Join(", ", sqls)})", type);
            }
            case "Value" or "Decimal":
            {
                Arity(row, f, 1);
                var x = A(0);
                return x.Type switch
                {
                    { Kind: NumKind.Float } => Limit(row, new Typed(dialect.TryCast(x.Sql, SqlType(TextNumber)), TextNumber)),
                    not null => x,
                    // Text: null when it is not a number (TRY_CAST gives null for '' and 'abc').
                    null => x.Sql == "NULL" ? x : Limit(row, new Typed(dialect.TryCastText(x.Sql, SqlType(TextNumber)), TextNumber)),
                };
            }
            case "Float":
            {
                Arity(row, f, 1);
                var x = A(0);
                return x.Type is not null || x.Sql == "NULL"
                    ? new Typed(ToFloat(x), Float)
                    : new Typed(dialect.TryCastText($"NULLIF(TRIM({x.Sql}), {dialect.TextLiteral("")})", "float"), Float);
            }

            // ---- dates ----
            case "DateAdd":
            {
                // A result before 0001-01-01 or after 9999-12-31 is null, not error 517: the count must lie between the counts
                // of unit boundaries from the date to those edges (adding a boundary count never passes the edge).
                Arity(row, f, 3);
                var unit = Unit(row, f.Args[2], name);
                var count = Once(row, Whole(A(1), "int"));
                var date = Once(row, A(0).Sql);
                return new Typed(count == "NULL" ? "NULL"
                    : $"CASE WHEN {count} BETWEEN {dialect.DateDiff(unit, date, FirstDate, big: true)} AND {dialect.DateDiff(unit, date, LastDate, big: true)} " +
                      $"THEN {dialect.AddInterval(unit, count, date)} END");
            }
            case "DateDiff":
            {
                Arity(row, f, 3);
                // K7: the number of boundaries crossed; below a minute the count may pass an int.
                var unit = Unit(row, f.Args[2], name);
                var big = unit is "millisecond" or "second" or "minute";
                return new Typed(dialect.DateDiff(unit, A(0).Sql, A(1).Sql, big), big ? BigInt : Int);
            }
            case "Day" or "Month" or "Year" or "Hour" or "Minute" or "Second":
                Arity(row, f, 1);
                return new Typed(dialect.DatePart(name.ToLowerInvariant(), A(0).Sql), Int);
            case "Weekday":
                // Sunday is 1 whatever SET DATEFIRST is.
                Arity(row, f, 1);
                return new Typed(dialect.Weekday(A(0).Sql), Int);
            case "WeekNum":
            {
                // The week of 1 January is week 1; weeks start on Sunday (DATEDIFF(week) counts Sundays whatever DATEFIRST is).
                Arity(row, f, 1);
                var date = Once(row, A(0).Sql);
                return new Typed($"({dialect.DateDiff("week", dialect.YearStart(date), date)} + 1)", Int);
            }
            case "ISOWeekNum":
                Arity(row, f, 1);
                return new Typed(dialect.DatePart("iso_week", A(0).Sql), Int);
            case "UTCToday":
                Arity(row, f, 0);
                // A date and time at midnight: DATEPART(hour, <date>) is error 9810, Hour(UTCToday()) is 0 in Power Fx.
                return new Typed($"CAST(CAST({dialect.UtcNow} AS date) AS {dialect.ColumnType(SqlColumnKind.DateTime)})");
            case "IsUTCToday":
                Arity(row, f, 1);
                return Bit($"CAST({A(0).Sql} AS date) = CAST({dialect.UtcNow} AS date)");
            default:
                throw Refuse(row, $"uses the function '{f.Name}', which cannot be computed");
        }
    }

    private static bool IsDigits(string sql) => sql.Length > 0 && sql.All(char.IsAsciiDigit);

    private static void Arities(Row row, ComputedExpr.Function f, int min, int max)
    {
        if (f.Args.Count < min || f.Args.Count > max)
            throw Refuse(row, $"uses {f.Name} with {f.Args.Count} argument(s)");
    }

    /// <summary>A yes/no value of a condition, never null.</summary>
    private Typed Bit(string condition) => new($"CAST(CASE WHEN {condition} THEN 1 ELSE 0 END AS {dialect.ColumnType(SqlColumnKind.Boolean)})") { Bit = true };

    /// <summary>The length of a text with its trailing spaces (<c>LEN</c> ignores them).</summary>
    private string Length(string text) => $"({dialect.TextLength($"CONCAT({text}, {dialect.TextLiteral(".")})")} - 1)";

    private static string ToFloat(Typed x) =>
        x.Type is { Kind: NumKind.Float } || x.Sql == "NULL" ? x.Sql : $"CAST({x.Constant ?? x.Sql} AS float)";

    /// <summary>
    /// A number as a whole number of the SQL type <paramref name="type"/> (<c>int</c> or <c>bigint</c>), its decimals cut: a constant
    /// as its digits, a whole number column as is, anything else by a <c>TRY_CAST</c> (null when it does not fit).
    /// </summary>
    private string Whole(Typed n, string type)
    {
        if (n.Constant is { } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
        {
            // A constant that does not fit the type is null, as a TRY_CAST would give (a written constant overflows: error 8115).
            var whole = decimal.Truncate(d);
            var fits = type == "int" ? whole is >= int.MinValue and <= int.MaxValue : whole is >= long.MinValue and <= long.MaxValue;
            return fits ? whole.ToString(CultureInfo.InvariantCulture) : "NULL";
        }
        if (n.Type is { Kind: NumKind.Integer, Precision: <= 10 }) return n.Sql;
        return dialect.TryCast(n.Sql, type);
    }

    /// <summary>
    /// A count or a position of a text function: a whole number of at least <paramref name="min"/>, null below (Power Fx gives an
    /// error, a formula column a blank).
    /// </summary>
    private string Count(Row row, Typed n, int min)
    {
        if (n.Constant is { } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return d >= min ? decimal.Truncate(d).ToString(CultureInfo.InvariantCulture) : "NULL";
        var x = Once(row, n);
        return $"CASE WHEN {x.Sql} >= {min.ToString(CultureInfo.InvariantCulture)} THEN {(x.Type is { Kind: NumKind.Integer } ? $"CAST({x.Sql} AS bigint)" : dialect.TryCast(x.Sql, "bigint"))} END";
    }

    /// <summary>The <c>DATEADD</c>/<c>DATEDIFF</c> part of a <c>TimeUnit</c> (a constant of the translator, never caller text).</summary>
    private static string Unit(Row row, ComputedExpr unit, string function) => unit switch
    {
        ComputedExpr.Literal { Value: "Milliseconds" } => "millisecond",
        ComputedExpr.Literal { Value: "Seconds" } => "second",
        ComputedExpr.Literal { Value: "Minutes" } => "minute",
        ComputedExpr.Literal { Value: "Hours" } => "hour",
        ComputedExpr.Literal { Value: "Days" } => "day",
        ComputedExpr.Literal { Value: "Months" } => "month",
        ComputedExpr.Literal { Value: "Quarters" } => "quarter",
        ComputedExpr.Literal { Value: "Years" } => "year",
        _ => throw Refuse(row, $"uses {function} with a unit that cannot be computed"),
    };

    /// <summary>
    /// The SQL of numbers made one type: float when one is, the widest whole number when all are, otherwise one decimal type that
    /// holds them all (as <see cref="Conditional"/>).
    /// </summary>
    private (List<string> Sqls, Num? Type) Common(List<Typed> values)
    {
        var types = values.Where(v => v.Type is not null).Select(v => v.Type!.Value).ToList();
        if (types.Count == 0) return (values.Select(v => v.Sql).ToList(), null);
        if (types.Any(t => t.Kind == NumKind.Float)) return (values.Select(ToFloat).ToList(), Float);
        if (types.All(t => t.Kind == NumKind.Integer) && values.All(v => v.Type is not null))
        {
            var widest = types.MaxBy(t => t.Precision);
            var sql = widest.Precision > 10 ? "bigint" : "int";
            return (values.Select(v => v.Type == widest ? v.Sql : $"CAST({v.Constant ?? v.Sql} AS {sql})").ToList(), widest.Precision > 10 ? BigInt : Int);
        }
        var type = Shared(values);
        return (values.Select(v => Cast(v, type)).ToList(), type);
    }

    /// <summary>The decimal type the operands of a sum would share: the largest scale and integer digits, within 37 digits.</summary>
    private static Num Shared(IEnumerable<Typed> values)
    {
        var operands = values.Select(Operand).ToList();
        var scale = operands.Max(t => t.Scale);
        var integer = Math.Min(operands.Max(t => t.IntegerDigits), MaxDigits - 1 - scale);
        return Num.Dec(integer + scale, scale);
    }

    /// <summary>
    /// <c>Mod(n, d)</c>: the remainder with the divisor's sign (Power Fx), where T-SQL <c>%</c> takes the dividend's:
    /// <c>((n % d) + d) % d</c>; float has no <c>%</c>: <c>n - d * FLOOR(n / d)</c>. A zero divisor gives null.
    /// </summary>
    private Typed Mod(Row row, Typed n, Typed d)
    {
        if (n.Type is { Kind: NumKind.Float } || d.Type is { Kind: NumKind.Float })
        {
            var x = Once(row, ToFloat(n));
            var y = Once(row, ToFloat(d));
            return new Typed($"({x} - {y} * FLOOR({x} / NULLIF({y}, 0)))", Float);
        }
        var type = Shared([n, d]);
        var divisor = Once(row, Cast(d, type));
        return new Typed($"((({Cast(n, type)} % NULLIF({divisor}, 0)) + {divisor}) % NULLIF({divisor}, 0))", type);
    }

    /// <summary>
    /// <c>Int</c> (floor), <c>Trunc</c>/<c>RoundDown</c> (toward zero), <c>Round</c> (half away from zero, as T-SQL <c>ROUND</c>
    /// of a decimal) and <c>RoundUp</c> (away from zero). A rounded decimal gets one more integer digit (9.99 → 10.0 overflows its
    /// type, error 8115); a whole number rounded to 0 or more digits is itself.
    /// </summary>
    private Typed Rounded(Row row, string name, Typed x, Typed? digitsArg)
    {
        var digits = digitsArg is null ? "0" : Whole(digitsArg, "int");
        var constant = int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var k) ? k : (int?)null;
        if (x.Type is { Kind: NumKind.Integer } && (name == "Int" || constant >= 0)) return x;
        if (x.Type is { Kind: NumKind.Float } || x.Type is null)
        {
            switch (name)
            {
                case "Int": return new Typed($"FLOOR({x.Sql})", x.Type);
                case "Round": return new Typed(dialect.Round(x.Sql, digits), x.Type);
                case "RoundUp":
                {
                    var v = Once(row, x.Sql);
                    var d = Once(row, digits);
                    var unit = $"POWER(CAST(10 AS float), -({d}))";
                    var cut = dialect.Truncate(v, d);
                    return new Typed($"CASE WHEN {cut} = {v} THEN {v} WHEN {v} > 0 THEN {cut} + {unit} " +
                                     $"ELSE {cut} - {unit} END", x.Type);
                }
                default: return new Typed(dialect.Truncate(x.Sql, digits), x.Type);
            }
        }
        var own = Operand(x);
        var wide = Num.Dec(Math.Min(own.Precision + 1, MaxDigits), own.Scale);
        switch (name)
        {
            case "Int":
                return new Typed($"FLOOR({Cast(x, own)})", Num.Dec(own.Precision, 0));
            case "Round":
                return new Typed(dialect.Round(Cast(x, wide), digits), wide);
            case "RoundUp":
            {
                var v = Once(row, Cast(x, wide));
                var d = Once(row, digits);
                var unit = dialect.TryCast($"POWER(CAST(10 AS float), -({d}))",
                    $"decimal({(MaxDigits - 1).ToString(CultureInfo.InvariantCulture)}, {own.Scale.ToString(CultureInfo.InvariantCulture)})");
                var cut = dialect.Truncate(v, d);
                return new Typed(dialect.TryCast($"CASE WHEN {cut} = {v} THEN {v} WHEN {v} > 0 THEN {cut} + {unit} " +
                                                 $"ELSE {cut} - {unit} END", SqlType(wide)), wide);
            }
            default:
                return new Typed(dialect.Truncate(Cast(x, own), digits), own);
        }
    }

    /// <summary>
    /// <c>Substitute(text, old, new, [instance])</c>, case-sensitive (Power Fx): every match, or the <c>instance</c>-th one
    /// (a constant: its position is found by one <c>CHARINDEX</c> per match before it).
    /// </summary>
    private Typed Substitute(Row row, ComputedExpr.Function f)
    {
        var text = Eval(row, f.Args[0]).Sql;
        var old = Eval(row, f.Args[1]).Sql;
        var replacement = Eval(row, f.Args[2]).Sql;
        if (f.Args.Count == 3)
            return new Typed(dialect.DefaultCollate($"REPLACE({dialect.BinaryCollate(text)}, {old}, {replacement})"));
        var instance = Eval(row, f.Args[3]);
        if (instance.Constant is not { } constant || !decimal.TryParse(constant, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
            throw Refuse(row, "uses Substitute with an instance that is not a constant number, which cannot be computed");
        var k = decimal.Truncate(n);
        if (k < 1) return new Typed("NULL");
        if (k > FormulaColumnRules.MaxSubstituteInstance)
            throw Refuse(row, $"uses Substitute with the instance {k.ToString(CultureInfo.InvariantCulture)}, " +
                              $"above {FormulaColumnRules.MaxSubstituteInstance}");
        var t = Once(row, text);
        var o = Once(row, old);
        var length = k > 1 ? Once(row, Length(o)) : Length(o);
        // The position of the k-th match, each searched after the one before (matches do not overlap, as Power Fx), or NotFound.
        // Each search reads the one before once and nests no CASE, so the k searches are one value, one apply: an apply per
        // search (NULLIF reads its value twice, so they could not be one) made Substitute(x, "a", "b", 100) 101 applies, and SQL
        // Server's compile time grows faster than the applies (final review N1).
        var searched = dialect.BinaryCollate(o);
        var position = Found(dialect.IndexOf(searched, t));
        for (var i = 2; i <= k; i++) position = Found(dialect.IndexOf(searched, t, $"{position} + {length}"));
        position = Once(row, position);
        return new Typed(dialect.DatabaseText($"CASE WHEN {dialect.DataLength(o)} = 0 OR COALESCE({position}, {NotFound}) = {NotFound} THEN {t} " +
                                              $"ELSE {dialect.Stuff(t, position, length, replacement)} END"));

        // CHARINDEX's 0 (no match) as NotFound, a position as itself; a search from NotFound + length finds nothing, so every match
        // after a missing one is NotFound too.
        static string Found(string charIndex) => $"({charIndex} + {NotFound - 1}) % {NotFound} + 1";
    }

    /// <summary>
    /// The position of a match that is not there (<see cref="Substitute"/>): 2^30, past any text (an <c>nvarchar(max)</c> holds at
    /// most 2^30 - 1 characters), and small enough that it and a text length add up within <c>int</c>.
    /// </summary>
    private const int NotFound = 1 << 30;

    /// <summary>
    /// <c>Text(number, format)</c> without <c>FORMAT()</c>: the number rounded half away from zero to a whole number (the format
    /// has no decimal point, F6), its digits laid into the placeholders as .NET custom formats do: <c>0</c> is a digit or a padding
    /// zero, <c>#</c> a digit or nothing (a zero value has no digit), the first placeholder takes the digits that do not fit, the
    /// sign comes first, any other character is written as is (a parameter).
    /// </summary>
    private Typed Text(Row row, Typed number, string format)
    {
        var whole = number.Type switch
        {
            { Kind: NumKind.Integer } => $"CAST({number.Sql} AS bigint)",
            { Kind: NumKind.Decimal, Scale: 0 } => dialect.TryCast(number.Sql, "bigint"),
            { Kind: NumKind.Decimal } t => dialect.TryCast(dialect.Round(Cast(number, Num.Dec(Math.Min(t.Precision + 1, MaxDigits), t.Scale)), "0"), "bigint"),
            _ => dialect.TryCast(dialect.Round(number.Sql, "0"), "bigint"),
        };
        var text20 = dialect.ColumnType(SqlColumnKind.String, 20);
        if (format == "0") return new Typed($"CAST({whole} AS {text20})");

        var placeholders = format.Count(c => c is '0' or '#');
        var firstZero = format.Where(c => c is '0' or '#').ToList().IndexOf('0');
        var zeros = firstZero < 0 ? 0 : placeholders - firstZero;
        var p = placeholders.ToString(CultureInfo.InvariantCulture);
        var v = Once(row, whole);
        var empty = dialect.TextLiteral("");
        var sign = $"CASE WHEN {v} < 0 THEN {dialect.TextLiteral("-")} ELSE {empty} END";
        var digits = zeros > 0 ? $"CAST(ABS({v}) AS {text20})" : $"CASE WHEN {v} = 0 THEN {empty} ELSE CAST(ABS({v}) AS {text20}) END";

        // The runs of the format: literal text or placeholders.
        var runs = Regex.Matches(format, "[0#]+|[^0#]+").Select(m => m.Value).ToList();
        var parts = new List<string> { sign };
        if (runs.Count(r => r[0] is '0' or '#') == 1)
        {
            var z = zeros.ToString(CultureInfo.InvariantCulture);
            var padded = zeros <= 1 ? digits
                : $"CASE WHEN {dialect.TextLength(digits)} >= {z} THEN {digits} ELSE {dialect.Right($"CONCAT({dialect.TextLiteral(new string('0', zeros))}, {digits})", z)} END";
            parts.AddRange(runs.Select(r => r[0] is '0' or '#' ? padded : Literal(row, r).Sql));
        }
        else
        {
            // The digits, padded on the left to the placeholders (spaces for '#', zeros for '0'), read one placeholder at a time;
            // a space is no digit.
            var width = Once(row, $"CASE WHEN {dialect.TextLength(digits)} > {p} THEN {dialect.TextLength(digits)} ELSE {p} END");
            var laid = Once(row, dialect.Right($"CONCAT({dialect.TextLiteral(new string(' ', placeholders - zeros) + new string('0', zeros))}, {digits})", width));
            var index = 0;
            foreach (var run in runs)
            {
                if (run[0] is not ('0' or '#'))
                {
                    parts.Add(Literal(row, run).Sql);
                    continue;
                }
                foreach (var _ in run)
                {
                    var back = (placeholders - 1 - index).ToString(CultureInfo.InvariantCulture);
                    parts.Add(index == 0 ? $"NULLIF({dialect.Left(laid, $"{width} - {back}")}, {dialect.TextLiteral(" ")})"
                        : $"NULLIF({dialect.Substring(laid, $"{width} - {back}", "1")}, {dialect.TextLiteral(" ")})");
                    index++;
                }
            }
        }
        return new Typed(dialect.DatabaseText($"CONCAT({string.Join(", ", parts)})"));
    }
}
