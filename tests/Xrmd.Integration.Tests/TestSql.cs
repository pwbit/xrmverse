using System.Data.Common;
using System.Text.RegularExpressions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The tests' own SQL on the run's provider (plan 2.7a, Task 6): a connection of the right engine (<see cref="TestProvider.OpenAsync"/>),
/// parameters typed by <see cref="DbValueTypes"/> through the dialect (strings are <c>citext</c> on PostgreSQL), and the few T-SQL
/// spellings the tests write turned into the dialect's (<see cref="Portable(string, ISqlDialect)"/>). Test SQL is written in the
/// common subset otherwise; Boolean literals come from <see cref="True"/> and <see cref="False"/>.
/// </summary>
public static partial class TestSql
{
    /// <summary>The dialect of the run's provider.</summary>
    public static ISqlDialect Dialect => SqlDialects.For(TestProvider.Current);

    /// <summary>The literal <c>true</c> of a Boolean column on the run's provider (<c>1</c> on SQL Server).</summary>
    public static string True => Dialect.BooleanLiteral(true);

    /// <summary>The literal <c>false</c> of a Boolean column on the run's provider (<c>0</c> on SQL Server).</summary>
    public static string False => Dialect.BooleanLiteral(false);

    /// <summary>
    /// <paramref name="sql"/> unchanged on SQL Server; on another engine the T-SQL spellings tests write: <c>[name]</c> quoting,
    /// <c>SYSUTCDATETIME()</c>, <c>NEWID()</c>, <c>ISNULL(</c>, <c>DATALENGTH(</c>, <c>N'…'</c> literals, <c>WITH (NOLOCK)</c>, <c>ALTER TABLE … DROP COLUMN a, b</c>, <c>EXEC sp_rename</c> of a column, a Boolean column's <c>= 0</c>/<c>= 1</c>, a leading
    /// <c>IF [NOT] EXISTS (…) … [ELSE …]</c> around one <c>UPDATE</c> and/or <c>INSERT</c> (<see cref="Conditional"/>),
    /// <c>JSON_VALUE</c> and <c>JSON_MODIFY</c> (<see cref="Json"/>), and a single
    /// statement's leading <c>SELECT TOP n</c> (the dialect's <see cref="ISqlDialect.Limit"/>).
    /// </summary>
    public static string Portable(string sql, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(d);
        if (d.Provider == StorageProvider.SqlServer) return sql;
        // Plan 2.7b, Task 3: LIKE's [_] (a literal underscore) is PostgreSQL's \_, before [name] quoting reads it as a name.
        sql = sql.Replace("[_]", "\\_", StringComparison.Ordinal);
        sql = Bracketed().Replace(sql, m => d.Quote(m.Groups[1].Value));
        // Plan 2.7a, Task 7: DATEADD(unit, n, date) and a JSON_QUERY of one property.
        sql = DateAdd().Replace(sql, m => d.AddInterval(m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim()));
        sql = JsonQueryProperty().Replace(sql, m => $"CAST(CAST({m.Groups[1].Value} AS jsonb) -> '{m.Groups[2].Value}' AS text)");
        sql = sql.Replace("SYSUTCDATETIME()", d.UtcNow, StringComparison.OrdinalIgnoreCase)
            .Replace("NEWID()", d.NewGuid, StringComparison.OrdinalIgnoreCase)
            .Replace("ISNULL(", "COALESCE(", StringComparison.OrdinalIgnoreCase)
            .Replace("DATALENGTH(", "octet_length(", StringComparison.OrdinalIgnoreCase)
            .Replace(" WITH (NOLOCK)", "", StringComparison.OrdinalIgnoreCase);
        sql = NationalLiteral().Replace(sql, "'");
        // Plan 2.7b, Task 3: IF OBJECT_ID('t') IS NOT NULL <one DELETE, UPDATE or INSERT>; as a DO block, and OBJECT_ID('t'[, 'U'])
        // as the table's oid. Any other IF OBJECT_ID form is refused rather than half converted.
        sql = IfObjectExists().Replace(sql, m =>
            $"DO $$ BEGIN IF to_regclass('{m.Groups[1].Value}') IS NOT NULL THEN {m.Groups[2].Value.Trim()} END IF; END $$;");
        if (sql.Contains("IF OBJECT_ID", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "TestSql.Portable converts IF OBJECT_ID('t') IS NOT NULL followed by one DELETE, UPDATE or INSERT ending with ';' only: " + sql);
        sql = ObjectId().Replace(sql, m => $"CAST(to_regclass('{m.Groups[1].Value}') AS oid)");
        // T-SQL's ALTER TABLE t DROP COLUMN a, b (PostgreSQL repeats DROP COLUMN) and sp_rename of a column.
        sql = DropColumnList().Replace(sql, m => d.DropColumns(m.Groups[1].Value,
            m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';') + m.Groups[3].Value);
        sql = RenameColumnProcedure().Replace(sql, m => d.RenameColumn(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value).TrimEnd(';'));
        // A Boolean column compared with or set to 0/1 (the tests' T-SQL bit literals): PostgreSQL's boolean takes no integer.
        sql = BitComparison().Replace(sql, m => NotBoolean(m.Groups[1].Value)
            ? m.Value
            : $"{m.Groups[1].Value} {m.Groups[2].Value} {d.BooleanLiteral(m.Groups[3].Value == "1")}");
        sql = Conditional(sql);
        sql = BooleanValues(sql, d);
        sql = Json(sql, d);
        var top = LeadingTop().Match(sql);
        if (top.Success && !sql.TrimEnd().TrimEnd(';').Contains(';'))
        {
            var rest = sql[top.Length..].TrimEnd().TrimEnd(';');
            sql = d.Limit("SELECT " + rest, top.Groups[1].Value);
        }
        return sql;
    }

    /// <summary>
    /// The tests' T-SQL upserts as plain statements: <c>IF NOT EXISTS (c) INSERT INTO t (…) VALUES (v)</c> (or <c>SELECT …</c>)
    /// becomes <c>INSERT INTO t (…) SELECT … WHERE NOT EXISTS (c)</c>; <c>IF EXISTS (c) UPDATE … ELSE INSERT …</c> becomes the
    /// <c>UPDATE</c>, then that insert. Anything else is left as it is.
    /// </summary>
    private static string Conditional(string sql)
    {
        var text = sql.Trim();
        var negated = text.StartsWith("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase);
        if (!negated && !text.StartsWith("IF EXISTS", StringComparison.OrdinalIgnoreCase)) return sql;
        var open = text.IndexOf('(');
        var close = Closing(text, open);
        if (close < 0) return sql;
        var condition = text[(open + 1)..close];
        var body = text[(close + 1)..].Trim();
        if (negated) return InsertUnless(body, condition) ?? sql;
        var otherwise = IndexOfWord(body, "ELSE");
        if (otherwise < 0 || !body.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)) return sql;
        var insert = InsertUnless(body[(otherwise + 4)..].Trim(), condition);
        return insert is null ? sql : body[..otherwise].Trim().TrimEnd(';') + ";\n" + insert;
    }

    /// <summary><paramref name="insert"/> (<c>INSERT INTO t (…) VALUES (…)|SELECT …</c>) run only when <paramref name="condition"/> finds no row.</summary>
    private static string? InsertUnless(string insert, string condition)
    {
        insert = insert.Trim().TrimEnd(';');
        if (!insert.StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase)) return null;
        var columnsOpen = insert.IndexOf('(');
        var columnsClose = Closing(insert, columnsOpen);
        if (columnsClose < 0) return null;
        var head = insert[..(columnsClose + 1)];
        var rest = insert[(columnsClose + 1)..].Trim();
        if (rest.StartsWith("VALUES", StringComparison.OrdinalIgnoreCase))
        {
            var valuesOpen = rest.IndexOf('(');
            var valuesClose = Closing(rest, valuesOpen);
            if (valuesClose < 0 || rest[(valuesClose + 1)..].Trim().Length > 0) return null;
            return $"{head} SELECT {rest[(valuesOpen + 1)..valuesClose]} WHERE NOT EXISTS ({condition})";
        }

        return rest.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            ? $"{head} SELECT * FROM ({rest}) AS src WHERE NOT EXISTS ({condition})"
            : null;
    }

    /// <summary>
    /// <c>JSON_VALUE(x, '$.p')</c> as the dialect's <see cref="ISqlDialect.JsonValue"/>; <c>JSON_MODIFY(x, '$.p', v)</c> as
    /// <c>jsonb_set</c> (a <c>NULL</c> value removes the key, as SQL Server's lax mode does; <c>JSON_QUERY(j)</c> is JSON,
    /// <c>CAST(0|1 AS bit)</c> a Boolean), back as text. The innermost call first, so nested calls compose.
    /// </summary>
    private static string Json(string sql, ISqlDialect d)
    {
        while (true)
        {
            var modify = sql.LastIndexOf("JSON_MODIFY(", StringComparison.OrdinalIgnoreCase);
            var value = sql.LastIndexOf("JSON_VALUE(", StringComparison.OrdinalIgnoreCase);
            var at = Math.Max(modify, value);
            if (at < 0) return sql;
            var open = sql.IndexOf('(', at);
            var close = Closing(sql, open);
            if (close < 0) return sql;
            var args = Split(sql[(open + 1)..close]).Select(a => a.Trim()).ToList();
            string replacement;
            if (at == value && args.Count == 2)
                replacement = d.JsonValue(args[0], args[1].Trim('\''));
            else if (at == modify && args.Count == 3)
            {
                var path = "'{" + string.Join(",", args[1].Trim('\'').TrimStart('$').TrimStart('.').Split('.')) + "}'";
                var target = $"CAST({args[0]} AS jsonb)";
                var v = args[2];
                var bit = BitCast().Match(v);
                replacement = v.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                    ? $"CAST({target} #- {path} AS text)"
                    : $"CAST(jsonb_set({target}, {path}, {(v.StartsWith("JSON_QUERY(", StringComparison.OrdinalIgnoreCase)
                        ? $"CAST({v[11..^1]} AS jsonb)"
                        : bit.Success ? $"to_jsonb({d.BooleanLiteral(bit.Groups[1].Value == "1")})"
                        : v.StartsWith('\'') ? $"to_jsonb(CAST({v} AS text))" : $"to_jsonb({v})")}) AS text)";
            }
            else return sql;
            sql = sql[..at] + replacement + sql[(close + 1)..];
        }
    }

    /// <summary>The 0/1 values of the Boolean columns (<see cref="IsBooleanName"/>) in each <c>INSERT INTO t (…) VALUES (…), …</c>.</summary>
    private static string BooleanValues(string sql, ISqlDialect d)
    {
        var at = 0;
        while ((at = sql.IndexOf("INSERT INTO", at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var columnsOpen = sql.IndexOf('(', at);
            var columnsClose = Closing(sql, columnsOpen);
            at += "INSERT INTO".Length;
            if (columnsClose < 0) continue;
            var columns = Split(sql[(columnsOpen + 1)..columnsClose]).Select(c => c.Trim().Trim('"', '[', ']')).ToList();
            var cursor = columnsClose + 1;
            while (cursor < sql.Length && char.IsWhiteSpace(sql[cursor])) cursor++;
            if (string.Compare(sql, cursor, "VALUES", 0, 6, StringComparison.OrdinalIgnoreCase) != 0) continue;
            cursor += 6;
            while (true)
            {
                while (cursor < sql.Length && char.IsWhiteSpace(sql[cursor])) cursor++;
                if (cursor >= sql.Length || sql[cursor] != '(') break;
                var close = Closing(sql, cursor);
                if (close < 0) break;
                var values = Split(sql[(cursor + 1)..close]);
                if (values.Count == columns.Count)
                {
                    for (var i = 0; i < values.Count; i++)
                        if (IsBooleanName(columns[i]) && values[i].Trim() is "0" or "1")
                            values[i] = " " + d.BooleanLiteral(values[i].Trim() == "1");
                    var tuple = "(" + string.Join(",", values) + ")";
                    sql = sql[..cursor] + tuple + sql[(close + 1)..];
                    close = cursor + tuple.Length - 1;
                }
                cursor = close + 1;
                while (cursor < sql.Length && char.IsWhiteSpace(sql[cursor])) cursor++;
                if (cursor >= sql.Length || sql[cursor] != ',') break;
                cursor++;
            }
        }
        return sql;
    }

    /// <summary>True for the tests' Boolean column names (<see cref="BitComparison"/>'s).</summary>
    private static bool IsBooleanName(string column)
    {
        var m = BitComparison().Match(column + " = 0");
        return m.Success && m.Index == 0 && !NotBoolean(column);
    }

    /// <summary>The integer and string columns <see cref="BitComparison"/>'s name pattern also matches.</summary>
    private static bool NotBoolean(string column) => column is "isolationmode" or "isocurrencycode" or "isinherited";

    /// <summary><paramref name="list"/> split at its commas of depth 0 outside string literals.</summary>
    private static List<string> Split(string list)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < list.Length; i++)
        {
            var c = list[i];
            if (c == '\'') { i = list.IndexOf('\'', i + 1); if (i < 0) break; }
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(list[start..i]);
                start = i + 1;
            }
        }
        parts.Add(list[start..]);
        return parts;
    }

    /// <summary>The index of the parenthesis closing the one at <paramref name="open"/> (string literals skipped), or -1.</summary>
    private static int Closing(string text, int open)
    {
        if (open < 0) return -1;
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                i = text.IndexOf('\'', i + 1);
                if (i < 0) return -1;
            }
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>The index of the keyword <paramref name="word"/> at depth 0 outside string literals, or -1.</summary>
    private static int IndexOfWord(string text, string word)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'') { i = text.IndexOf('\'', i + 1); if (i < 0) return -1; }
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
                     && (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
                     && (i + word.Length == text.Length || !char.IsLetterOrDigit(text[i + word.Length])))
                return i;
        }
        return -1;
    }

    /// <summary>A command on <paramref name="conn"/> with <paramref name="sql"/> made portable and the parameters typed.</summary>
    public static DbCommand Command(DbConnection conn, string sql, params (string Name, object? Value)[] parameters) =>
        Command(conn, null, sql, parameters);

    /// <summary><see cref="Command(DbConnection, string, ValueTuple{string, object}[])"/> in <paramref name="tx"/>.</summary>
    public static DbCommand Command(DbConnection conn, DbTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var cmd = conn.Command(Portable(sql, SqlDialects.For(conn)), tx);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    /// <summary>
    /// <see cref="Command(DbConnection, DbTransaction, string, ValueTuple{string, object}[])"/> in the argument order of
    /// <c>new SqlCommand(sql, conn[, tx])</c>, the spelling of the tests ported from SQL Server only (plan 2.7b, Task 3).
    /// </summary>
    public static DbCommand Command(string sql, DbConnection conn, DbTransaction? tx = null) => Command(conn, tx, sql);

    /// <summary>Runs <paramref name="sql"/> on the database of <paramref name="connectionString"/>; the rows it changed.</summary>
    public static async Task<int> ExecAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var cmd = Command(conn, sql, parameters);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The first column of the first row of <paramref name="sql"/>, or null (DBNull as null).</summary>
    public static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var cmd = Command(conn, sql, parameters);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>
    /// The first column of the first row of <paramref name="sql"/> as a <see cref="long"/> (a <c>COUNT(*)</c> is <c>int</c> on SQL
    /// Server, <c>bigint</c> on PostgreSQL).
    /// </summary>
    public static async Task<long> CountAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters) =>
        Convert.ToInt64(await ScalarAsync(connectionString, sql, parameters));

    /// <summary>The rows of <paramref name="sql"/>, each as its column values (DBNull as null).</summary>
    public static async Task<List<object?[]>> RowsAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var cmd = Command(conn, sql, parameters);
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await rd.ReadAsync())
        {
            var row = new object?[rd.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = rd.IsDBNull(i) ? null : rd.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>True when the dialect's catalog <paramref name="condition"/> (<see cref="ISqlDialect.TableExists"/> and the like) holds.</summary>
    public static async Task<bool> HoldsAsync(string connectionString, string condition) =>
        Convert.ToInt32(await ScalarAsync(connectionString, $"SELECT CASE WHEN {condition} THEN 1 ELSE 0 END")) == 1;

    /// <summary>True when the table <paramref name="schema"/>.<paramref name="table"/> exists in the database of <paramref name="connectionString"/>.</summary>
    public static Task<bool> TableExistsAsync(string connectionString, string schema, string table) =>
        HoldsAsync(connectionString, Dialect.TableExists(schema, table));

    /// <summary>True when the table <paramref name="schema"/>.<paramref name="table"/> has the column <paramref name="column"/>.</summary>
    public static Task<bool> ColumnExistsAsync(string connectionString, string schema, string table, string column) =>
        HoldsAsync(connectionString, Dialect.ColumnExists(schema, table, column));

    /// <summary>True when the table <paramref name="schema"/>.<paramref name="table"/> has the index <paramref name="index"/>.</summary>
    public static Task<bool> IndexExistsAsync(string connectionString, string schema, string table, string index) =>
        HoldsAsync(connectionString, Dialect.IndexExists(schema, table, index));

    /// <summary>The query of the connection's session id (<c>@@SPID</c>, <c>pg_backend_pid()</c>).</summary>
    public static string SessionIdQuery => TestProvider.IsPostgres ? "SELECT pg_backend_pid()" : "SELECT @@SPID";

    /// <summary>The statement that holds an exclusive lock on <paramref name="table"/> until the transaction ends.</summary>
    public static string ExclusiveTableLock(string table) => TestProvider.IsPostgres
        ? $"LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE"
        : $"SELECT COUNT(*) FROM {table} WITH (TABLOCKX, HOLDLOCK)";

    /// <summary>The query of how many sessions wait on a lock the session <c>@spid</c> holds.</summary>
    public static string BlockedByQuery => TestProvider.IsPostgres
        ? "SELECT COUNT(*) FROM pg_stat_activity WHERE CAST(@spid AS integer) = ANY (pg_blocking_pids(pid))"
        : "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @spid";

    /// <summary>The columns of <paramref name="schema"/>.<paramref name="table"/>, in their order (<c>information_schema</c>, on both engines).</summary>
    public static async Task<List<string>> ColumnsAsync(string connectionString, string schema, string table) =>
        (await RowsAsync(connectionString,
            $"SELECT column_name FROM information_schema.columns WHERE table_schema = {Text(schema)} AND table_name = {Text(table)} ORDER BY ordinal_position"))
        .Select(r => (string)r[0]!).ToList();

    /// <summary>
    /// The type of a column as <c>information_schema.columns</c> names it (<c>data_type</c>, lower case: the engine's own name,
    /// e.g. <c>uniqueidentifier</c> or <c>uuid</c>), with its numeric precision and scale; null when there is no such column.
    /// </summary>
    public static async Task<(string Type, int? Precision, int? Scale)?> ColumnTypeAsync(string connectionString, string schema, string table, string column)
    {
        var rows = await RowsAsync(connectionString, $"""
            SELECT data_type, numeric_precision, numeric_scale FROM information_schema.columns
            WHERE table_schema = {Text(schema)} AND table_name = {Text(table)} AND column_name = {Text(column)}
            """);
        if (rows.Count == 0) return null;
        return (((string)rows[0][0]!).ToLowerInvariant(), rows[0][1] is null ? null : Convert.ToInt32(rows[0][1]),
            rows[0][2] is null ? null : Convert.ToInt32(rows[0][2]));
    }

    /// <summary>An index as the engine's catalog describes it (<see cref="IndexAsync"/>): its key columns in order (a descending one with <c> DESC</c>).</summary>
    public sealed record TestIndex(bool Unique, bool Filtered, IReadOnlyList<string> Keys, IReadOnlyList<string> Included);

    /// <summary>
    /// The index <paramref name="index"/> (its <see cref="ISqlDialect.ObjectName"/>) of <paramref name="schema"/>.<paramref name="table"/>,
    /// or null: <c>sys.indexes</c> on SQL Server, <c>pg_index</c> on PostgreSQL.
    /// </summary>
    public static async Task<TestIndex?> IndexAsync(string connectionString, string schema, string table, string index)
    {
        var name = Dialect.ObjectName(index);
        var rows = await RowsAsync(connectionString, TestProvider.IsPostgres
            ? $"""
              SELECT ix.indisunique, ix.indpred IS NOT NULL, a.attname, k.ord > ix.indnkeyatts, COALESCE(k.opt & 1, 0) = 1
              FROM pg_index ix JOIN pg_class i ON i.oid = ix.indexrelid JOIN pg_class t ON t.oid = ix.indrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
              CROSS JOIN LATERAL unnest(CAST(ix.indkey AS int2[]), CAST(ix.indoption AS int2[])) WITH ORDINALITY AS k(attnum, opt, ord)
              JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
              WHERE n.nspname = {Text(schema)} AND t.relname = {Text(table)} AND i.relname = {Text(name)}
              ORDER BY k.ord
              """
            : $"""
              SELECT i.is_unique, i.has_filter, c.name, ic.is_included_column, ic.is_descending_key
              FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE i.object_id = OBJECT_ID({Text(schema + "." + table)}) AND i.name = {Text(name)}
              ORDER BY ic.is_included_column, ic.key_ordinal, ic.index_column_id
              """);
        if (rows.Count == 0) return null;
        return new TestIndex(Convert.ToBoolean(rows[0][0]), Convert.ToBoolean(rows[0][1]),
            rows.Where(r => !Convert.ToBoolean(r[3])).Select(r => (string)r[2]! + (Convert.ToBoolean(r[4]) ? " DESC" : "")).ToList(),
            rows.Where(r => Convert.ToBoolean(r[3])).Select(r => (string)r[2]!).ToList());
    }

    /// <summary>The names of the indexes of <paramref name="schema"/>.<paramref name="table"/> other than its primary key.</summary>
    public static async Task<List<string>> IndexNamesAsync(string connectionString, string schema, string table) =>
        (await RowsAsync(connectionString, TestProvider.IsPostgres
            ? $"""
              SELECT i.relname FROM pg_index ix JOIN pg_class i ON i.oid = ix.indexrelid JOIN pg_class t ON t.oid = ix.indrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
              WHERE n.nspname = {Text(schema)} AND t.relname = {Text(table)} AND NOT ix.indisprimary ORDER BY i.relname
              """
            : $"""
              SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID({Text(schema + "." + table)}) AND name IS NOT NULL
                AND is_primary_key = 0 ORDER BY name
              """))
        .Select(r => (string)r[0]!).ToList();

    /// <summary>A string literal of <paramref name="value"/> (catalog names; both engines read <c>'…'</c>).</summary>
    public static string Text(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [GeneratedRegex(@"ALTER\s+TABLE\s+([\w.""]+)\s+DROP\s+COLUMN\s+([\w""]+(?:\s*,\s*[\w""]+)+)\s*(;|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DropColumnList();

    [GeneratedRegex(@"EXEC\s+sp_rename\s+N?'(\w+)\.(\w+)\.(\w+)'\s*,\s*N?'(\w+)'\s*,\s*N?'COLUMN'", RegexOptions.IgnoreCase)]
    private static partial Regex RenameColumnProcedure();

    [GeneratedRegex(@"DATEADD\(\s*(\w+)\s*,\s*([^,()]+)\s*,\s*(SYSUTCDATETIME\(\)|[\w.@""]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex DateAdd();

    [GeneratedRegex(@"JSON_QUERY\(\s*([\w.""]+)\s*,\s*'\$\.(\w+)'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex JsonQueryProperty();

    [GeneratedRegex(@"IF\s+OBJECT_ID\(\s*'([^']+)'\s*\)\s+IS\s+NOT\s+NULL\s+((?:DELETE|UPDATE|INSERT)\s[^;]+;)", RegexOptions.IgnoreCase)]
    private static partial Regex IfObjectExists();

    [GeneratedRegex(@"OBJECT_ID\(\s*'([^']+)'\s*(?:,\s*'\w+'\s*)?\)", RegexOptions.IgnoreCase)]
    private static partial Regex ObjectId();

    [GeneratedRegex(@"\[([A-Za-z_][A-Za-z0-9_]*)\]")]
    private static partial Regex Bracketed();

    /// <summary><c>col = 0|1</c> (or <c>&lt;&gt;</c>) for the tests' Boolean columns: <c>is…</c>, <c>can…</c> and a few others.</summary>
    [GeneratedRegex(@"(?<!\w)((?:is|canbe|canmodify)[a-z_]+|readaccess|updateaccess|systemmanaged|workflowsdkstepenabled)\s*(=|<>)\s*([01])(?![\w.])")]
    private static partial Regex BitComparison();

    [GeneratedRegex(@"^CAST\(\s*([01])\s+AS\s+bit\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex BitCast();

    [GeneratedRegex(@"(?<![A-Za-z0-9_'])N'")]
    private static partial Regex NationalLiteral();

    [GeneratedRegex(@"^\s*SELECT\s+TOP\s*\(?\s*(\d+|@\w+)\s*\)?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingTop();
}

/// <summary>
/// The SqlClient parameter calls the tests wrote (<c>AddWithValue</c>, <c>Add(name, SqlDbType)</c>) on a provider-neutral
/// <see cref="DbCommand"/> (plan 2.7a, Task 6): the parameter is created by the command's provider and configured by its dialect.
/// </summary>
public static class TestParameterExtensions
{
    /// <summary>A parameter typed by <see cref="DbValueTypes.Of"/> (SqlClient's <c>AddWithValue</c> inference).</summary>
    public static DbParameter AddWithValue(this DbParameterCollection parameters, string name, object? value)
    {
        // A null on PostgreSQL goes untyped: the server takes the type of the column or expression it meets (a text null is not
        // a uuid). SQL Server converts a null nvarchar to any type.
        if (value is null or DBNull && parameters is Npgsql.NpgsqlParameterCollection)
        {
            var untyped = new Npgsql.NpgsqlParameter { ParameterName = name, Value = DBNull.Value };
            parameters.Add(untyped);
            return untyped;
        }

        var p = Create(parameters, name, DbValueTypes.Of(value));
        p.Value = value ?? DBNull.Value;
        Configure(parameters, p);
        return p;
    }

    /// <summary>A parameter of <paramref name="type"/>, whose value the caller sets.</summary>
    public static DbParameter Add(this DbParameterCollection parameters, string name, System.Data.SqlDbType type)
    {
        var p = Create(parameters, name, type switch
        {
            System.Data.SqlDbType.UniqueIdentifier => System.Data.DbType.Guid,
            System.Data.SqlDbType.Int => System.Data.DbType.Int32,
            System.Data.SqlDbType.BigInt => System.Data.DbType.Int64,
            System.Data.SqlDbType.SmallInt => System.Data.DbType.Int16,
            System.Data.SqlDbType.Bit => System.Data.DbType.Boolean,
            System.Data.SqlDbType.Decimal or System.Data.SqlDbType.Money => System.Data.DbType.Decimal,
            System.Data.SqlDbType.Float => System.Data.DbType.Double,
            System.Data.SqlDbType.DateTime or System.Data.SqlDbType.DateTime2 => System.Data.DbType.DateTime2,
            System.Data.SqlDbType.VarBinary or System.Data.SqlDbType.Binary => System.Data.DbType.Binary,
            _ => System.Data.DbType.String,
        });
        Configure(parameters, p);
        return p;
    }

    private static DbParameter Create(DbParameterCollection parameters, string name, System.Data.DbType type)
    {
        DbParameter p = parameters switch
        {
            Microsoft.Data.SqlClient.SqlParameterCollection => new Microsoft.Data.SqlClient.SqlParameter(),
            Npgsql.NpgsqlParameterCollection => new Npgsql.NpgsqlParameter(),
            _ => throw new NotSupportedException($"No parameters for {parameters.GetType()}."),
        };
        p.ParameterName = name;
        p.DbType = type;
        parameters.Add(p);
        return p;
    }

    /// <summary>The dialect's adjustment (PostgreSQL: strings as <c>citext</c>, dates as UTC), once the type is known.</summary>
    private static void Configure(DbParameterCollection parameters, DbParameter p)
    {
        if (parameters is Npgsql.NpgsqlParameterCollection) Xrmd.Storage.PostgreSql.PostgresDialect.Instance.ConfigureParameter(p);
    }
}
