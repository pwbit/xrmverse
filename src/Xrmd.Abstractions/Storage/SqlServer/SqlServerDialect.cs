using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Xrmd.Abstractions.Storage.SqlServer;

/// <summary>
/// The T-SQL of every engine-specific fragment (plan 2.7a, Task 2): the exact text xrmverse wrote before the dialect existed,
/// so that SQL Server runs the same statements.
/// </summary>
public sealed class SqlServerDialect : ISqlDialect
{
    /// <summary>The longest bounded <c>nvarchar</c>; longer strings are <c>nvarchar(max)</c>.</summary>
    public const int MaxNvarcharLength = 4000;

    private const int DeadlockVictim = 1205;
    private const int CommandTimeout = -2;
    private const int Win32WaitTimeout = 258;
    private const int LockRequestTimeout = 1222;
    private const int UniqueIndexViolation = 2601;
    private const int PrimaryKeyViolation = 2627;
    private const int ForeignKeyViolation = 547;
    private const int InvalidObjectName = 208;
    private const int LoginFailed = 18456;
    private const int ArithmeticOverflow = 8115;

    /// <summary>Connection refused, reset or lost: 233, 64, 10053, 10054, 10060.</summary>
    private static readonly HashSet<int> ConnectionLost = [233, 64, 10053, 10054, 10060];

    /// <summary>The database cannot be opened (4060) or the service is busy or unavailable for now (40197, 40501, 40613, 49918–49920).</summary>
    private static readonly HashSet<int> Unavailable = [4060, 40197, 40501, 40613, 49918, 49919, 49920];

    private SqlServerDialect()
    {
    }

    /// <summary>The only instance.</summary>
    public static SqlServerDialect Instance { get; } = new();

    /// <summary>The type of the connections whose engine this dialect writes for (<see cref="SqlDialects"/>).</summary>
    public static Type ConnectionType => typeof(SqlConnection);

    public StorageProvider Provider => StorageProvider.SqlServer;

    public string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    public string ColumnType(SqlColumnKind kind, int length = 0, int precision = 0, int scale = 0) => kind switch
    {
        SqlColumnKind.Guid => "uniqueidentifier",
        SqlColumnKind.String => length is <= 0 or > MaxNvarcharLength ? "nvarchar(max)" : $"nvarchar({Num(length)})",
        SqlColumnKind.Text => "nvarchar(max)",
        SqlColumnKind.Int32 => "int",
        SqlColumnKind.Int64 => "bigint",
        SqlColumnKind.Decimal => $"decimal({Num(precision)},{Num(scale)})",
        SqlColumnKind.Double => "float",
        SqlColumnKind.Boolean => "bit",
        SqlColumnKind.DateTime => "datetime2",
        SqlColumnKind.Binary => length > 0 ? $"binary({Num(length)})" : "varbinary(max)",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown column kind."),
    };

    public string RowVersionType => "rowversion";

    public string Limit(string selectSql, string? countExpr)
    {
        ArgumentNullException.ThrowIfNull(selectSql);
        if (countExpr is null) return selectSql;
        const string select = "SELECT ", distinct = "SELECT DISTINCT ";
        if (selectSql.StartsWith(distinct, StringComparison.Ordinal))
            return distinct + "TOP " + countExpr + " " + selectSql[distinct.Length..];
        if (selectSql.StartsWith(select, StringComparison.Ordinal))
            return select + "TOP " + countExpr + " " + selectSql[select.Length..];
        throw new ArgumentException("The statement to limit must start with 'SELECT '.", nameof(selectSql));
    }

    public string Page(int offset, int fetch) => Page(Num(offset), Num(fetch));

    public string Page(string offsetExpr, string fetchExpr) => $"OFFSET {offsetExpr} ROWS FETCH NEXT {fetchExpr} ROWS ONLY";

    public string UtcNow => "SYSUTCDATETIME()";

    public string NewGuid => "NEWID()";

    public string LockHint(RowLock mode) => mode switch
    {
        RowLock.Shared => " WITH (HOLDLOCK)",
        RowLock.Update => " WITH (UPDLOCK)",
        RowLock.UpdateRange => " WITH (UPDLOCK, HOLDLOCK)",
        RowLock.UpdateRow => " WITH (UPDLOCK, ROWLOCK)",
        RowLock.UpdateRowRange => " WITH (UPDLOCK, ROWLOCK, HOLDLOCK)",
        RowLock.UpdateSerializable => " WITH (UPDLOCK, SERIALIZABLE)",
        RowLock.UpdateSkipLocked => " WITH (UPDLOCK, READPAST, ROWLOCK)",
        RowLock.Exclusive => " WITH (XLOCK, ROWLOCK, HOLDLOCK)",
        RowLock.TableShared => " WITH (TABLOCK, HOLDLOCK)",
        RowLock.ReadCommittedLock => " WITH (READCOMMITTEDLOCK)",
        RowLock.RepeatableRead => " WITH (REPEATABLEREAD)",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown row lock."),
    };

    public string LockClause(RowLock mode, string? ofAlias = null) =>
        Enum.IsDefined(mode) ? "" : throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown row lock.");

    /// <summary>False: <see cref="LockKeyAsync"/> sends nothing (plan 2.7b, final review I2).</summary>
    public bool KeyLocksNeedTransaction => false;

    /// <summary>Nothing: the read's HOLDLOCK hint locks the key range (plan 2.7a, Task 3 fix round 1); no statement is sent.</summary>
    public Task LockKeyAsync(DataSession s, string scope, object? key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(scope);
        return Task.CompletedTask;
    }

    /// <summary>Nothing: the read's <c>WITH (TABLOCK, HOLDLOCK)</c> hint locks the table (plan 2.7b, ruling Q4); no statement is sent.</summary>
    public Task LockTableSharedAsync(DataSession s, string schema, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentException.ThrowIfNullOrEmpty(table);
        return Task.CompletedTask;
    }

    /// <summary>Nothing: the read's <c>UPDLOCK, HOLDLOCK</c> hint locks the key range it reads (plan 2.7b, Task 3); no statement is sent.</summary>
    public Task LockTableWritesAsync(DataSession s, string schema, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentException.ThrowIfNullOrEmpty(table);
        return Task.CompletedTask;
    }

    /// <summary>Nothing: the row and range locks of the read and the update order themselves (plan 2.7b, Task 4 fix round 3); no statement is sent.</summary>
    public Task LockTableRowWritesAsync(DataSession s, string schema, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentException.ThrowIfNullOrEmpty(table);
        return Task.CompletedTask;
    }

    public Task AcquireAppLockAsync(DataSession s, string resource, bool session, CancellationToken ct) =>
        AcquireAsync(s, resource, session, timeoutMs: null, ct);

    public Task AcquireAppLockAsync(DataSession s, string resource, bool session, TimeSpan timeout, CancellationToken ct) =>
        AcquireAsync(s, resource, session, (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue), ct);

    public async Task<bool> TryAcquireAppLockAsync(DataSession s, string resource, bool session, CancellationToken ct)
    {
        var code = await GetAppLockAsync(s, resource, session, timeoutMs: 0, ct);
        // -1: not granted within the timeout (0), i.e. another session holds it.
        return code >= 0 || (code == -1 ? false : throw Refused(resource, code));
    }

    /// <summary><c>sp_getapplock</c> (exclusive); a negative return code is a lock not granted.</summary>
    private static async Task AcquireAsync(DataSession s, string resource, bool session, int? timeoutMs, CancellationToken ct)
    {
        var code = await GetAppLockAsync(s, resource, session, timeoutMs, ct);
        if (code < 0) throw Refused(resource, code);
    }

    private static AppLockException Refused(string resource, int code) => new(resource, $"sp_getapplock returned {code}");

    /// <summary>The return code of <c>sp_getapplock</c> (exclusive); with a timeout, the command waits as long as the lock does.</summary>
    private static async Task<int> GetAppLockAsync(DataSession s, string resource, bool session, int? timeoutMs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(resource);
        await using var cmd = s.Command("sp_getapplock");
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.AddParameter("@Resource", DbType.String, resource);
        cmd.AddParameter("@LockMode", DbType.String, "Exclusive");
        cmd.AddParameter("@LockOwner", DbType.String, Owner(session));
        if (timeoutMs is { } ms)
        {
            cmd.AddParameter("@LockTimeout", DbType.Int32, ms);
            if (ms > 0) cmd.CommandTimeout = 0;
        }
        var result = cmd.AddParameter("@result", DbType.Int32, null);
        result.Direction = ParameterDirection.ReturnValue;
        await cmd.ExecuteNonQueryAsync(ct);
        return (int)result.Value!;
    }

    public async Task ReleaseAppLockAsync(DataSession s, string resource, bool session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(resource);
        await using var cmd = s.Command("sp_releaseapplock");
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.AddParameter("@Resource", DbType.String, resource);
        cmd.AddParameter("@LockOwner", DbType.String, Owner(session));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> HoldsAppLockAsync(DataSession s, string resource, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(resource);
        await using var cmd = s.Command("SELECT APPLOCK_MODE(N'public', @resource, N'Session')");
        cmd.AddParameter("@resource", DbType.String, resource);
        return await cmd.ExecuteScalarAsync(ct) as string == "Exclusive";
    }

    private static string Owner(bool session) => session ? "Session" : "Transaction";

    public string JsonArray(string param) => $"OPENJSON({param})";

    public string JsonRecords(string param, IReadOnlyList<(string Name, SqlColumnKind Kind)> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return JsonRecords(param, columns.Select(c => new JsonColumn(c.Name, c.Kind)).ToList());
    }

    public string JsonRecords(string param, IReadOnlyList<JsonColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0) throw new ArgumentException("At least one column is required.", nameof(columns));
        var list = columns.Select(c =>
            $"{c.Name} {ColumnType(c.Kind, c.Length, c.Precision, c.Scale)}{(c.Path is null ? "" : $" '{c.Path}'")}");
        return $"OPENJSON({param}) WITH ({string.Join(", ", list)})";
    }

    public string JsonValue(string expr, string path) => $"JSON_VALUE({expr}, '{path}')";

    public string IsJson(string expr, bool valid = true) => $"ISJSON({expr}) = {(valid ? 1 : 0)}";

    /// <summary>
    /// <c>OPENJSON</c> over the value at the path (lax <c>JSON_QUERY</c>: a missing path or a scalar gives NULL, and
    /// <c>OPENJSON(NULL)</c> has no rows), behind a <c>CASE</c> so that <c>JSON_QUERY</c> never sees a text that is not JSON
    /// (AND/OR order is not guaranteed); the key compared in <c>OPENJSON</c>'s binary collation with equal byte lengths, and
    /// the value's kind checked (<c>OPENJSON</c> lists an array's elements and an object's properties alike).
    /// </summary>
    public string JsonMember(string expr, string pathParam, string keyParam, bool keyIsIndex)
    {
        var parent = $"CASE WHEN {IsJson(expr)} THEN JSON_QUERY({expr}, {pathParam}) END";
        return $"SELECT 1 FROM OPENJSON({parent}) AS j WHERE j.[key] = {keyParam} AND DATALENGTH(j.[key]) = DATALENGTH({keyParam})"
               + $" AND ISJSON({parent}, {(keyIsIndex ? "ARRAY" : "OBJECT")}) = 1";
    }

    public string ReturningInline(IReadOnlyList<string> columns, RowImage image = RowImage.New)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0) throw new ArgumentException("At least one column is required.", nameof(columns));
        var prefix = image switch
        {
            RowImage.New => "inserted.",
            RowImage.Old or RowImage.Deleted => "deleted.",
            _ => throw new ArgumentOutOfRangeException(nameof(image), image, "Unknown row image."),
        };
        return " OUTPUT " + string.Join(", ", columns.Select(c => prefix + c));
    }

    public string ReturningTrailing(IReadOnlyList<string> columns, RowImage image = RowImage.New)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return columns.Count == 0 ? throw new ArgumentException("At least one column is required.", nameof(columns)) : "";
    }

    public string Upsert(string table, IReadOnlyList<string> keyColumns, IReadOnlyList<string> columns, string source)
    {
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(columns);
        if (keyColumns.Count == 0) throw new ArgumentException("At least one key column is required.", nameof(keyColumns));
        var insert = keyColumns.Concat(columns).ToList();
        return $"MERGE {table} WITH (HOLDLOCK) AS p\n" +
               $"USING ({source}) AS a\n" +
               $"ON {string.Join(" AND ", keyColumns.Select(k => $"p.{Quote(k)} = a.{Quote(k)}"))}\n" +
               $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", columns.Select(c => $"p.{Quote(c)} = a.{Quote(c)}"))}\n" +
               $"WHEN NOT MATCHED THEN INSERT ({string.Join(", ", insert.Select(Quote))})\n" +
               $"    VALUES ({string.Join(", ", insert.Select(c => "a." + Quote(c)))});";
    }

    public string InsertIfAbsent(string table, IReadOnlyList<string> keyColumns, IReadOnlyList<string> columns, string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(table);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentException.ThrowIfNullOrEmpty(source);
        if (keyColumns.Count == 0) throw new ArgumentException("At least one key column is required.", nameof(keyColumns));
        var insert = keyColumns.Concat(columns).ToList();
        return $"INSERT INTO {table} ({string.Join(", ", insert.Select(Quote))})\n" +
               $"SELECT {string.Join(", ", insert.Select(c => "a." + Quote(c)))} FROM ({source}) AS a\n" +
               $"WHERE NOT EXISTS (SELECT 1 FROM {table} p WITH (UPDLOCK, HOLDLOCK) WHERE {string.Join(" AND ", keyColumns.Select(k => $"p.{Quote(k)} = a.{Quote(k)}"))});";
    }

    public string BinaryEquals(string left, string right) =>
        $"({left} COLLATE Latin1_General_BIN2 = {right} AND DATALENGTH({left}) = DATALENGTH({right}))";

    public string BinaryCollate(string expr) => $"{expr} COLLATE Latin1_General_BIN2";

    public string DefaultCollate(string expr) => $"{expr} COLLATE DATABASE_DEFAULT";

    /// <summary>The expression itself: the database's <c>_CI_AS</c> collation compares text like this (plan 2.7b, ruling Q1).</summary>
    public string TextKey(string expr) => expr;

    /// <summary>The expression itself: a group-by on it shows the group's value (plan 2.7b, Task 2 fix round 1).</summary>
    public string TextKeyMember(string expr) => expr;

    /// <summary>The expression itself: <c>DISTINCT</c> compares through the collation (plan 2.7b, Task 2 fix round 1).</summary>
    public string TextKeyDistinctMember(string expr) => expr;

    /// <summary>None: a unique index on a text column already compares through the collation (plan 2.7b, ruling Q3).</summary>
    public string? PlainTextKeyIndexes => null;

    public string AddInterval(string unit, string amount, string expr) => $"DATEADD({unit}, {amount}, {expr})";

    public string StringAgg(string expr, string separator) => $"STRING_AGG({expr}, {separator})";

    public string Concat(params string[] parts) => string.Join(" + ", SqlDialects.ConcatParts(parts));

    public string IsNull(string a, string b) => $"ISNULL({a}, {b})";

    public string BooleanLiteral(bool value) => value ? "1" : "0";

    public string DatePart(string unit, string expr)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        if (!DateParts.Contains(unit)) throw new ArgumentException($"'{unit}' is not a date part.", nameof(unit));
        return $"DATEPART({unit}, {expr})";
    }

    private static readonly HashSet<string> DateParts = new(StringComparer.Ordinal) { "year", "quarter", "month", "week", "iso_week", "day", "hour", "minute", "second" };


    public string TryCastInteger(string expr, SqlColumnKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        return kind switch
        {
            SqlColumnKind.Int32 => $"TRY_CAST({expr} AS int)",
            SqlColumnKind.Int64 => $"TRY_CAST({expr} AS bigint)",
            _ => throw new NotSupportedException($"TryCastInteger converts to an integer kind, not {kind}."),
        };
    }

    // ---- row versions and change tracking (plan 2.7a, Task 7) ----------------------------------------------------------------

    public string RowVersionValue(string expr)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        return $"CAST({expr} AS binary(8))";
    }

    public string RowVersionStart => "CAST(MIN_ACTIVE_ROWVERSION() AS bigint)";

    /// <summary>Null: the engine sets a <c>rowversion</c> column itself.</summary>
    public string? NextRowVersion => null;

    /// <summary><c>MIN_ACTIVE_ROWVERSION()</c>.</summary>
    public async Task<long> MinActiveRowVersionAsync(DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command("SELECT CAST(MIN_ACTIVE_ROWVERSION() AS bigint);");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    // ---- query, computed column and rollup expressions (plan 2.7a, Task 7): the text written before ---------------------------

    public string RecursiveWith => "WITH ";

    public string RecursionLimit(int levels) => $"\nOPTION (MAXRECURSION {Num(levels)})";

    public string Apply(string source, string alias, bool outer) => $"{(outer ? "OUTER" : "CROSS")} APPLY {source} AS {alias}";

    public string TryCast(string expr, string type) => $"TRY_CAST({expr} AS {type})";

    public string TryCastText(string expr, string type) => $"TRY_CAST({expr} AS {type})";

    public string CountBig(string expr) => $"COUNT_BIG({expr})";

    public string SumBigint(string expr, bool bigint = false) => bigint ? $"SUM({expr})" : $"SUM(CAST({expr} AS bigint))";

    public string Round(string expr, string digits) => $"ROUND({expr}, {digits})";

    public string Truncate(string expr, string digits) => $"ROUND({expr}, {digits}, 1)";

    public string NaturalLog(string expr) => $"LOG({expr})";

    public string TextLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    public string DatabaseText(string expr) => expr;

    public string Left(string text, string count) => $"LEFT({text}, {count})";

    public string Right(string text, string count) => $"RIGHT({text}, {count})";

    public string Substring(string text, string start, string count) => $"SUBSTRING({text}, {start}, {count})";

    public string DataLength(string expr) => $"DATALENGTH({expr})";

    public string TextLength(string expr) => $"LEN({expr})";

    public string IndexOf(string find, string text, string? start = null) =>
        start is null ? $"CHARINDEX({find}, {text})" : $"CHARINDEX({find}, {text}, {start})";

    public string Stuff(string text, string start, string length, string replacement) => $"STUFF({text}, {start}, {length}, {replacement})";

    public string CharFromCode(string code) => $"NCHAR({code})";

    public string DateDiff(string unit, string start, string end, bool big = false)
    {
        if (!DiffUnits.Contains(unit)) throw new ArgumentException($"'{unit}' is not a unit of DateDiff.", nameof(unit));
        return $"{(big ? "DATEDIFF_BIG" : "DATEDIFF")}({unit}, {start}, {end})";
    }

    private static readonly HashSet<string> DiffUnits = new(StringComparer.Ordinal)
        { "year", "quarter", "month", "week", "day", "hour", "minute", "second", "millisecond" };

    public string Weekday(string expr) => $"((DATEPART(weekday, {expr}) + @@DATEFIRST - 1) % 7 + 1)";

    public string YearStart(string expr) => $"DATEFROMPARTS(YEAR({expr}), 1, 1)";

    /// <summary>
    /// ' ' → ' ' + U+0001, then U+0001 + ' ' → '', then U+0001 → '', in a binary collation (U+0001 is not ignorable there).
    /// </summary>
    public string CollapseSpaces(string expr) => DefaultCollate(
        $"REPLACE(REPLACE(REPLACE({BinaryCollate($"TRIM({expr})")}, N' ', N' ' + NCHAR(1)), NCHAR(1) + N' ', N''), NCHAR(1), N'')");

    public string MaxBoolean(string expr) => $"CAST(MAX(CAST({expr} AS tinyint)) AS bit)";

    public string LikePattern(string pattern) => pattern ?? throw new ArgumentNullException(nameof(pattern));

    public string Like(string expr, string pattern, bool negate, Func<string, string> parameter)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(parameter);
        return $"{expr} {(negate ? "NOT LIKE" : "LIKE")} {parameter(pattern)}";
    }

    public string LastDateTime => "CAST('9999-12-31T23:59:59.9999999' AS datetime2)";

    public string EscapeLike(string expr) => $"REPLACE(REPLACE(REPLACE({expr}, N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]')";

    public string ToText(string expr, int length) => $"CONVERT(nvarchar({Num(length)}), {expr})";

    public string UpdateFrom(string table, string alias, IReadOnlyList<(string Column, string Value)> set, string source, string on,
        string? where, IReadOnlyList<Returned> returning)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(returning);
        return $"UPDATE {alias} SET {string.Join(", ", set.Select(x => $"{alias}.{x.Column} = {x.Value}"))}\n" +
               $"OUTPUT {string.Join(", ", returning.Select(r => r.Column ? "inserted." + r.Sql : r.Sql))}\n" +
               $"FROM {table} AS {alias} JOIN {source} ON {on}" + (where is null ? "" : "\nWHERE " + where);
    }

    public DbErrorKind Classify(DbException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is not SqlException sql) return DbErrorKind.Other;
        if (sql.Number == DeadlockVictim) return DbErrorKind.Deadlock;
        // The command timeout, or the client that gave up waiting for the server (the wait timeout of the network read).
        if (sql.Number == CommandTimeout || sql.InnerException is Win32Exception { NativeErrorCode: Win32WaitTimeout }) return DbErrorKind.Timeout;
        return sql.Number switch
        {
            LockRequestTimeout => DbErrorKind.LockTimeout,
            UniqueIndexViolation or PrimaryKeyViolation => DbErrorKind.UniqueViolation,
            ForeignKeyViolation => DbErrorKind.ForeignKeyViolation,
            InvalidObjectName => DbErrorKind.UndefinedObject,
            LoginFailed => DbErrorKind.LoginFailed,
            ArithmeticOverflow => DbErrorKind.NumericOverflow,
            var n when ConnectionLost.Contains(n) => DbErrorKind.ConnectionLost,
            var n when Unavailable.Contains(n) => DbErrorKind.Unavailable,
            _ => DbErrorKind.Other,
        };
    }

    public bool IsPrimaryKeyViolation(DbException ex) => ex is SqlException { Number: PrimaryKeyViolation };

    public string NextSequenceValue(string sequence) => $"NEXT VALUE FOR {sequence}";

    // ---- DDL and catalog checks (plan 2.7a, Task 4): the text the callers wrote before ------------------------------------

    /// <summary>The name itself: SQL Server identifiers hold 128 characters, more than any name xrmverse builds.</summary>
    public string ObjectName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return name;
    }

    /// <summary>The longest SQL Server identifier (<c>sysname</c>), in characters.</summary>
    public const int MaxIdentifierLength = 128;

    public void EnsureIdentifier(string name, string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Length > MaxIdentifierLength)
            throw new XrmdValidationException(
                $"The {kind} name '{name}' is longer than the {MaxIdentifierLength} characters SQL Server allows for a table or column name.");
    }

    public string MigrationFolder => "";

    public bool FailedStatementAbortsTransaction => false;

    /// <summary>Nothing: the <c>rowversion</c> column is set by the engine.</summary>
    public string RowVersionTrigger(string schema, string table) => "";

    public string RowVersionColumn(string column) => $"{Quote(column)} {RowVersionType}";

    public string TableExists(string schema, string table) => $"OBJECT_ID(N'{Text(Name(schema, table))}', N'U') IS NOT NULL";

    public string ColumnExists(string schema, string table, string column) =>
        $"COL_LENGTH(N'{Text(Name(schema, table))}', N'{Text(column)}') IS NOT NULL";

    public string IndexExists(string schema, string table, string index) =>
        $"EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{Text(ObjectName(index))}' AND object_id = OBJECT_ID(N'{Text(Name(schema, table))}'))";

    public string When(string condition, string statement) => $"IF {condition} EXEC(N'{Text(statement)}');";

    public string CreateSchemaIfMissing(string schema) => $"IF SCHEMA_ID(N'{Text(schema)}') IS NULL EXEC(N'CREATE SCHEMA {Text(Quote(schema))}');";

    public string CreateIndexIfMissing(string schema, string table, string index, string columns, bool unique = false, string? filter = null) =>
        $"IF NOT {IndexExists(schema, table, index)}\n    CREATE {(unique ? "UNIQUE " : "")}INDEX {Quote(ObjectName(index))} ON {Name(schema, table)} ({columns})" +
        $"{(filter is null ? "" : " WHERE " + filter)};";

    public string DropIndexIfExists(string schema, string table, string index) =>
        $"IF {IndexExists(schema, table, index)} DROP INDEX {Quote(ObjectName(index))} ON {Name(schema, table)};";

    public string AddColumns(string table, IReadOnlyList<string> definitions) => $"ALTER TABLE {table} ADD {string.Join(", ", definitions)};";

    public string AddColumnIfMissing(string schema, string table, string column, string type) =>
        $"IF COL_LENGTH(N'{Text(Name(schema, table))}', N'{Text(column)}') IS NULL ALTER TABLE {Name(schema, table)} ADD {Quote(column)} {type} NULL;";

    public string DropColumns(string table, IReadOnlyList<string> columns) => $"ALTER TABLE {table} DROP COLUMN {string.Join(", ", columns)};";

    public string AlterColumnType(string table, string column, string type) => $"ALTER TABLE {table} ALTER COLUMN {column} {type} NULL;";

    public string RenameColumn(string schema, string table, string column, string newName) =>
        $"EXEC sp_rename N'{Text($"{schema}.{table}.{column}")}', N'{Text(newName)}', N'COLUMN';";

    public string CreateSequenceIfMissing(string schema, string name, long start) =>
        CreateSchemaIfMissing(schema) + "\n" +
        $"IF OBJECT_ID(N'{Text(Name(schema, name))}', N'SO') IS NULL EXEC(N'{Text(SequenceDefinition(schema, name, start))}');";

    public string CreateSequence(string schema, string name, long start) => SequenceDefinition(schema, name, start) + ";";

    public string DropSequenceIfExists(string schema, string name) =>
        $"IF OBJECT_ID(N'{Text(Name(schema, name))}', N'SO') IS NOT NULL DROP SEQUENCE {Name(schema, name)};";

    public string ReadSequence(string schema, string name) =>
        "SELECT CAST(start_value AS bigint), " +
        "CASE WHEN last_used_value IS NULL THEN CAST(start_value AS bigint) ELSE CAST(current_value AS bigint) + 1 END " +
        $"FROM sys.sequences WHERE object_id = OBJECT_ID(N'{Text(Name(schema, name))}', N'SO');";

    /// <summary>Nothing: the migrations create what they need (<c>meta.schema_version</c> is the migrator's).</summary>
    public Task PrepareDatabaseAsync(DbConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Task.CompletedTask;
    }

    private string Name(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    private string SequenceDefinition(string schema, string name, long start) =>
        $"CREATE SEQUENCE {Name(schema, name)} AS bigint START WITH {start.ToString(CultureInfo.InvariantCulture)} INCREMENT BY 1 MINVALUE 0 NO CACHE";

    /// <summary><paramref name="sql"/> inside a <c>N'…'</c> literal.</summary>
    private static string Text(string sql) => sql.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Nothing: SqlClient sends the parameter as typed (plan 2.7a, Task 3).</summary>
    public void ConfigureParameter(DbParameter parameter) => ArgumentNullException.ThrowIfNull(parameter);

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
}
