using System.Data.Common;

namespace Xrmd.Abstractions.Storage;

/// <summary>The engine-neutral kind of a column type, mapped to the engine's type by <see cref="ISqlDialect.ColumnType"/>.</summary>
public enum SqlColumnKind
{
    Guid,
    /// <summary>Bounded text: <c>length</c> characters, or unbounded above the engine's bound (<c>nvarchar(max)</c>).</summary>
    String,
    /// <summary>Unbounded text.</summary>
    Text,
    Int32,
    Int64,
    /// <summary>Exact numeric with <c>precision</c> total digits and <c>scale</c> decimals.</summary>
    Decimal,
    Double,
    Boolean,
    /// <summary>A UTC date and time.</summary>
    DateTime,
    /// <summary>Bytes: unbounded, or of a fixed length (<see cref="ISqlDialect.ColumnType"/>).</summary>
    Binary,
}

/// <summary>
/// The lock a read takes on the rows it reads (ruling P7; plan 2.7a, fix round 1), until the end of its transaction unless said
/// otherwise. Each value is one SQL Server table hint used by the code (<see cref="ISqlDialect.LockHint"/>) and its PostgreSQL
/// row-locking clause (<see cref="ISqlDialect.LockClause"/>).
/// </summary>
public enum RowLock
{
    /// <summary>A shared lock that keeps the rows (and, on SQL Server, the key range) as read: <c>HOLDLOCK</c>; <c>FOR SHARE</c>.</summary>
    Shared,
    /// <summary>An update lock: <c>UPDLOCK</c>; <c>FOR UPDATE</c>.</summary>
    Update,
    /// <summary>An update lock on the rows and the key range (check then insert): <c>UPDLOCK, HOLDLOCK</c>; <c>FOR UPDATE</c> (with <see cref="ISqlDialect.LockKeyAsync"/> for a row that may be missing).</summary>
    UpdateRange,
    /// <summary>An update lock on the rows only: <c>UPDLOCK, ROWLOCK</c>; <c>FOR UPDATE</c>.</summary>
    UpdateRow,
    /// <summary>An update lock on the rows and the key range, at row granularity: <c>UPDLOCK, ROWLOCK, HOLDLOCK</c>; <c>FOR UPDATE</c>.</summary>
    UpdateRowRange,
    /// <summary>An update lock under serializable isolation (an <c>UPDATE</c> target): <c>UPDLOCK, SERIALIZABLE</c>; <c>FOR UPDATE</c>.</summary>
    UpdateSerializable,
    /// <summary>An update lock that skips rows another transaction locked (a queue claim): <c>UPDLOCK, READPAST, ROWLOCK</c>; <c>FOR UPDATE SKIP LOCKED</c>.</summary>
    UpdateSkipLocked,
    /// <summary>An exclusive lock on the rows and the key range: <c>XLOCK, ROWLOCK, HOLDLOCK</c>; <c>FOR UPDATE</c>.</summary>
    Exclusive,
    /// <summary>A shared lock on the whole table: <c>TABLOCK, HOLDLOCK</c>; <c>LOCK TABLE … IN SHARE MODE</c> (a statement of its own).</summary>
    TableShared,
    /// <summary>A read that waits for writers instead of reading the snapshot: <c>READCOMMITTEDLOCK</c>; <c>FOR SHARE</c>.</summary>
    ReadCommittedLock,
    /// <summary>Shared locks held until the end of the transaction: <c>REPEATABLEREAD</c>; <c>FOR SHARE</c>.</summary>
    RepeatableRead,
}

/// <summary>The row image a returning clause reads (plan 2.7a, fix round 1).</summary>
public enum RowImage
{
    /// <summary>The row as written (<c>inserted.</c> on SQL Server).</summary>
    New,
    /// <summary>
    /// The old values of the rows an <c>UPDATE</c> changed (<c>deleted.</c> on SQL Server). Refused on PostgreSQL before 18
    /// (plan 2.7a, Task 3 fix round 1): a caller that needs them reads them first.
    /// </summary>
    Old,
    /// <summary>The rows a <c>DELETE</c> removed (<c>deleted.</c> on SQL Server; the plain <c>RETURNING</c> of a <c>DELETE</c> on PostgreSQL).</summary>
    Deleted,
}

/// <summary>A column of <see cref="ISqlDialect.JsonRecords(string, IReadOnlyList{JsonColumn})"/>.</summary>
/// <param name="Name">The column name of the rows (a constant of the caller, never caller-supplied text).</param>
/// <param name="Kind">The type of the column.</param>
/// <param name="Length">The length of a <see cref="SqlColumnKind.String"/> column (see <see cref="ISqlDialect.ColumnType"/>).</param>
/// <param name="Path">The JSON path of the value (<c>$</c>: the array element itself, <c>$.name</c>: a property); null: the
/// property named <paramref name="Name"/>, without writing the path.</param>
/// <param name="Precision">The total digits of a <see cref="SqlColumnKind.Decimal"/> column.</param>
/// <param name="Scale">The decimals of a <see cref="SqlColumnKind.Decimal"/> column.</param>
public readonly record struct JsonColumn(string Name, SqlColumnKind Kind, int Length = 0, string? Path = null, int Precision = 0, int Scale = 0);

/// <summary>
/// Every piece of SQL text that differs between the storage engines (plan 2.7a, ruling P3): identifiers, types, paging, locks
/// (ruling P7), JSON (ruling P8), dates, upsert and error classes (ruling P9). One instance per engine; the code that builds SQL
/// asks it for each engine-specific fragment and never checks <see cref="Provider"/> itself.
/// </summary>
/// <remarks>
/// A fragment is text the caller splices into its statement: names and expressions passed in are already safe SQL (constants of
/// the caller, validated logical names, parameter names), never caller-supplied values.
/// </remarks>
public interface ISqlDialect
{
    /// <summary>The engine this dialect writes for.</summary>
    StorageProvider Provider { get; }

    /// <summary>The quoted identifier (<c>[name]</c> on SQL Server).</summary>
    string Quote(string identifier);

    /// <summary>
    /// The column type of <paramref name="kind"/>: <paramref name="length"/> for <see cref="SqlColumnKind.String"/> (0, a negative
    /// value or more than the engine's bound: unbounded) and for <see cref="SqlColumnKind.Binary"/> (more than 0: exactly that many
    /// bytes, <c>binary(n)</c> on SQL Server; PostgreSQL's <c>bytea</c> has no length), <paramref name="precision"/> and
    /// <paramref name="scale"/> for <see cref="SqlColumnKind.Decimal"/>; the other kinds ignore them.
    /// </summary>
    string ColumnType(SqlColumnKind kind, int length = 0, int precision = 0, int scale = 0);

    /// <summary>
    /// The type of the <c>versionnumber</c> column of a data table (<c>rowversion</c> on SQL Server, which the engine sets on
    /// every write; plan 2.7a, Task 2 addition).
    /// </summary>
    string RowVersionType { get; }

    /// <summary>
    /// <paramref name="selectSql"/> (a statement starting with <c>SELECT </c> or <c>SELECT DISTINCT </c>) limited to the rows
    /// <paramref name="countExpr"/> evaluates to (<c>SELECT TOP {countExpr} …</c> on SQL Server: the caller writes the expression
    /// as it wants it, <c>1</c> or <c>(@top)</c>); unchanged when <paramref name="countExpr"/> is null.
    /// </summary>
    /// <remarks>
    /// <paramref name="selectSql"/> is one whole statement (its <c>ORDER BY</c> included), without a terminator, an
    /// <c>OPTION</c> or a <see cref="LockClause"/>: an engine that limits with a trailing <c>LIMIT</c> appends it, and the
    /// caller appends the lock clause after the result.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="selectSql"/> does not start with <c>SELECT </c>.</exception>
    string Limit(string selectSql, string? countExpr);

    /// <summary>The paging clause after the <c>ORDER BY</c> of a statement: skip <paramref name="offset"/> rows, return <paramref name="fetch"/>.</summary>
    string Page(int offset, int fetch);

    /// <summary>
    /// <see cref="Page(int, int)"/> with the counts as SQL expressions (parameters, written as the caller wants them, e.g.
    /// <c>(@offset)</c>; plan 2.7a, Task 2 addition).
    /// </summary>
    string Page(string offsetExpr, string fetchExpr);

    /// <summary>The current UTC date and time of the engine (<c>SYSUTCDATETIME()</c> on SQL Server).</summary>
    string UtcNow { get; }

    /// <summary>A new random id (<c>NEWID()</c> on SQL Server).</summary>
    string NewGuid { get; }

    /// <summary>
    /// The inline slot of a locking read (ruling P7; plan 2.7a, fix round 1): the table hint written right after the table (or its
    /// alias) that <paramref name="mode"/> reads, with its leading space (<c> WITH (UPDLOCK, HOLDLOCK)</c> on SQL Server), or empty
    /// where the engine locks with <see cref="LockClause"/>. A locking read writes both slots; one of them is empty on each engine.
    /// </summary>
    string LockHint(RowLock mode);

    /// <summary>
    /// The trailing slot of a locking read (ruling P7; plan 2.7a, fix round 1): the clause that ends the <c>SELECT</c> (before a
    /// <c>;</c>, or before the <c>)</c> of a subquery), with its leading space: <c> FOR UPDATE [OF a] [SKIP LOCKED]</c>,
    /// <c> FOR SHARE [OF a]</c> where the engine has it; empty on SQL Server, where <see cref="LockHint"/> locks the rows.
    /// <paramref name="ofAlias"/> names the table (alias) whose rows the read locks when it joins others: required on a join, where
    /// PostgreSQL would lock every table (and refuses the nullable side of an outer join).
    /// </summary>
    /// <remarks>
    /// PostgreSQL refuses a locking clause with aggregates, <c>DISTINCT</c>, <c>GROUP BY</c> or <c>UNION</c>, and a missing row
    /// locks nothing there: such reads (<c>COUNT(*)</c>, <c>NOT EXISTS</c>, check then insert) run <see cref="LockKeyAsync"/>
    /// first, as a command of its own, and write only the <see cref="LockHint"/> slot.
    /// </remarks>
    string LockClause(RowLock mode, string? ofAlias = null);

    /// <summary>
    /// Serializes the transaction of <paramref name="s"/> on the key <paramref name="key"/> of <paramref name="scope"/> (ruling
    /// P7; plan 2.7a, Task 3 fix round 1), before a read that must serialize on rows it may not find (a count, an existence check,
    /// a check then insert): the transactions that lock the same scope and key wait for each other until their end. Runs as a
    /// command of its own in the session's transaction, so the read after it sees what the previous holder committed and its
    /// results are the command's only ones. Scope and key are compared ignoring case (<paramref name="key"/> is a value: a
    /// <see cref="Guid"/>, a number or a string). Nothing on SQL Server, where the read's <see cref="LockHint"/> (a
    /// <c>HOLDLOCK</c> mode) locks the key range: no statement is sent.
    /// </summary>
    Task LockKeyAsync(DataSession s, string scope, object? key, CancellationToken ct);

    /// <summary>
    /// True when <see cref="LockKeyAsync"/> takes a lock of its own, held to the end of the session's transaction (PostgreSQL's
    /// transaction-level advisory locks; plan 2.7b, final review I2): a caller on an autocommit session opens a transaction to
    /// hold the key across statements, and a caller that takes key locks next to row locks takes the keys first (it reads which
    /// keys it needs before it writes). False on SQL Server, where <see cref="LockKeyAsync"/> sends no statement and the reads'
    /// range locks do it.
    /// </summary>
    bool KeyLocksNeedTransaction { get; }

    /// <summary>
    /// Takes a shared lock on the whole table <paramref name="schema"/>.<paramref name="table"/> to the end of the transaction of
    /// <paramref name="s"/> (plan 2.7b, ruling Q4), before a read that must see the data a later change of the table finds
    /// (other transactions read but do not write it meanwhile). Nothing on SQL Server, where the read's
    /// <see cref="LockHint"/>(<see cref="RowLock.TableShared"/>) (<c>WITH (TABLOCK, HOLDLOCK)</c>) takes the lock: no statement
    /// is sent. PostgreSQL runs <c>LOCK TABLE &lt;schema&gt;."&lt;table&gt;" IN SHARE MODE</c> as a command of its own.
    /// </summary>
    /// <param name="schema">A plain schema name (<c>data</c>, <c>meta</c>).</param>
    /// <param name="table">The table name, quoted by the dialect.</param>
    Task LockTableSharedAsync(DataSession s, string schema, string table, CancellationToken ct);

    /// <summary>
    /// Keeps every other writer of the table <paramref name="schema"/>.<paramref name="table"/>, and every other caller of this
    /// method on it, waiting to the end of the transaction of <paramref name="s"/> (plan 2.7b, Task 3), before a read whose
    /// result must still hold when the transaction ends: no row is added, changed or removed by another transaction meanwhile;
    /// other transactions keep reading. Nothing on SQL Server, where the read's <see cref="LockHint"/>
    /// (<see cref="RowLock.UpdateRange"/>, <c>UPDLOCK, HOLDLOCK</c>) locks the key range it reads: no statement is sent.
    /// PostgreSQL runs <c>LOCK TABLE &lt;schema&gt;."&lt;table&gt;" IN SHARE ROW EXCLUSIVE MODE</c> as a command of its own
    /// (it has no range lock; that mode conflicts with every row write and with itself).
    /// </summary>
    /// <param name="schema">A plain schema name (<c>data</c>, <c>meta</c>).</param>
    /// <param name="table">The table name, quoted by the dialect.</param>
    Task LockTableWritesAsync(DataSession s, string schema, string table, CancellationToken ct);

    /// <summary>
    /// Takes, to the end of the transaction of <paramref name="s"/>, the table lock that a write of rows of
    /// <paramref name="schema"/>.<paramref name="table"/> takes (plan 2.7b, Task 4 fix round 3), before a locking read of a row
    /// that the transaction then updates: the table lock comes before the row lock, the order of the callers of
    /// <see cref="LockTableWritesAsync"/>, so such a caller and this transaction wait for each other instead of deadlocking.
    /// Other writers do not wait for it. Nothing on SQL Server, whose row and range locks need no such step: no statement is
    /// sent. PostgreSQL runs <c>LOCK TABLE &lt;schema&gt;."&lt;table&gt;" IN ROW EXCLUSIVE MODE</c> as a command of its own.
    /// </summary>
    /// <param name="schema">A plain schema name (<c>data</c>, <c>meta</c>).</param>
    /// <param name="table">The table name, quoted by the dialect.</param>
    Task LockTableRowWritesAsync(DataSession s, string schema, string table, CancellationToken ct);

    /// <summary>
    /// Takes the exclusive application lock <paramref name="resource"/> in <paramref name="s"/> (ruling P7), held until the end of
    /// its transaction, or until it is released or the connection closes with <paramref name="session"/>; waits as long as the
    /// engine's lock wait allows.
    /// </summary>
    /// <exception cref="AppLockException">The engine did not grant the lock.</exception>
    Task AcquireAppLockAsync(DataSession s, string resource, bool session, CancellationToken ct);

    /// <summary>
    /// <see cref="AcquireAppLockAsync(DataSession, string, bool, CancellationToken)"/> waiting at most <paramref name="timeout"/>
    /// (plan 2.7a, Task 2 addition); the command itself does not time out first.
    /// </summary>
    /// <exception cref="AppLockException">The engine did not grant the lock in time.</exception>
    Task AcquireAppLockAsync(DataSession s, string resource, bool session, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Takes the exclusive application lock <paramref name="resource"/> without waiting (plan 2.7a, fix round 1): false when
    /// another session holds it. A zero wait means "do not wait" on every engine.
    /// </summary>
    /// <exception cref="AppLockException">The engine refused the lock for another reason.</exception>
    Task<bool> TryAcquireAppLockAsync(DataSession s, string resource, bool session, CancellationToken ct);

    /// <summary>Releases the application lock <paramref name="resource"/> this session holds (plan 2.7a, fix round 1).</summary>
    Task ReleaseAppLockAsync(DataSession s, string resource, bool session, CancellationToken ct);

    /// <summary>
    /// True when the session of <paramref name="s"/> holds the exclusive session application lock <paramref name="resource"/>
    /// (plan 2.7a, fix round 1: <c>APPLOCK_MODE</c> on SQL Server).
    /// </summary>
    Task<bool> HoldsAppLockAsync(DataSession s, string resource, CancellationToken ct);

    /// <summary>
    /// A table source over the JSON array of scalars in <paramref name="param"/>, one row per element in a text column named
    /// <c>value</c> (ruling P8: <c>OPENJSON(@p)</c> on SQL Server).
    /// </summary>
    string JsonArray(string param);

    /// <summary>
    /// A table source over the JSON array of objects in <paramref name="param"/>, one row per element with the typed
    /// <paramref name="columns"/> read from the properties of the same names (ruling P8: <c>OPENJSON … WITH</c> on SQL Server).
    /// </summary>
    string JsonRecords(string param, IReadOnlyList<(string Name, SqlColumnKind Kind)> columns);

    /// <summary>
    /// <see cref="JsonRecords(string, IReadOnlyList{ValueTuple{string, SqlColumnKind}})"/> with lengths, decimal sizes and paths
    /// (plan 2.7a, Task 2 addition): <c>$</c> reads the element itself, so an array of scalars gives one typed column.
    /// </summary>
    string JsonRecords(string param, IReadOnlyList<JsonColumn> columns);

    /// <summary>The scalar at <paramref name="path"/> (<c>$.a</c>) of the JSON text <paramref name="expr"/>, as text (ruling P8).</summary>
    string JsonValue(string expr, string path);

    /// <summary>
    /// The text <paramref name="expr"/> is valid JSON (<paramref name="valid"/> true) or is not (false); null for a null text
    /// (plan 2.7b, Task 5): <c>ISJSON(expr) = 1</c> / <c>= 0</c> on SQL Server.
    /// </summary>
    string IsJson(string expr, bool valid = true);

    /// <summary>
    /// A <c>SELECT 1</c> over the member named by the parameter <paramref name="keyParam"/> of the JSON value found at the
    /// SQL/JSON path in the parameter <paramref name="pathParam"/> (<c>$.a."b c"[2]</c>, as <c>JsonPaths.Format</c> writes it)
    /// of the JSON text <paramref name="expr"/> (plan 2.7b, Task 5): a property of an object, or with
    /// <paramref name="keyIsIndex"/> the element of that 0-based index of an array. No row when the text is not JSON, the path
    /// leads nowhere or to another kind of value, or the member is not there; the key matches exactly (case, accents and
    /// trailing spaces counting). The row reads the member as <c>j.type</c> (0 null, 1 string, 2 number, 3 true or false,
    /// 4 array, 5 object) and <c>j.value</c> (its text: a string unquoted, an array or object as JSON), both written with
    /// <see cref="Quote"/>; the caller appends <c> AND …</c> conditions on them. SQL Server lists the members with
    /// <c>OPENJSON(JSON_QUERY(expr, path))</c>.
    /// </summary>
    string JsonMember(string expr, string pathParam, string keyParam, bool keyIsIndex);

    /// <summary>
    /// The inline slot of the clause that returns <paramref name="columns"/> (column references as the caller writes them: a bare
    /// lower-case name or a <see cref="Quote"/> result) of the rows an
    /// <c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c> wrote, with its leading space (plan 2.7a, fix round 1): on SQL Server
    /// <c> OUTPUT inserted.[a], …</c> (<c>deleted.</c> for <see cref="RowImage.Old"/> and <see cref="RowImage.Deleted"/>), written before the <c>VALUES</c>/<c>SELECT</c>
    /// of an <c>INSERT</c> and before the <c>FROM</c>/<c>WHERE</c> of an <c>UPDATE</c>/<c>DELETE</c>; empty where
    /// <see cref="ReturningTrailing"/> returns them. A statement writes both slots.
    /// </summary>
    string ReturningInline(IReadOnlyList<string> columns, RowImage image = RowImage.New);

    /// <summary>
    /// The trailing slot of <see cref="ReturningInline"/>, ending the statement, with its leading space: <c> RETURNING a, …</c>
    /// on PostgreSQL; empty on SQL Server. On PostgreSQL a <c>DELETE</c> returns the removed row (<see cref="RowImage.Deleted"/>);
    /// the old values of an <c>UPDATE</c> (<see cref="RowImage.Old"/>) need PostgreSQL 18 (<c>RETURNING old.a</c>) and are
    /// refused with <see cref="NotSupportedException"/> (plan 2.7a, Task 3 fix round 1).
    /// </summary>
    string ReturningTrailing(IReadOnlyList<string> columns, RowImage image = RowImage.New);

    /// <summary>
    /// The statement that inserts the rows of <paramref name="source"/> (a <c>SELECT</c> whose columns are named like the table's)
    /// into <paramref name="table"/> (quoted), or updates <paramref name="columns"/> of the row with the same
    /// <paramref name="keyColumns"/> when it exists, atomically (<c>MERGE … WITH (HOLDLOCK)</c> on SQL Server). Column names are
    /// quoted here.
    /// </summary>
    string Upsert(string table, IReadOnlyList<string> keyColumns, IReadOnlyList<string> columns, string source);

    /// <summary>
    /// <paramref name="left"/> equals <paramref name="right"/> exactly: case, accents and trailing spaces count (SQL Server: a
    /// <c>Latin1_General_BIN2</c> comparison and equal byte lengths).
    /// </summary>
    string BinaryEquals(string left, string right);

    /// <summary>
    /// <paramref name="expr"/> compared or searched byte by byte, case and accents counting (plan 2.7a, Task 2 addition):
    /// <c>{expr} COLLATE Latin1_General_BIN2</c> on SQL Server.
    /// </summary>
    string BinaryCollate(string expr);

    /// <summary>
    /// <paramref name="expr"/> back in the database's own collation after <see cref="BinaryCollate"/> (plan 2.7a, Task 2
    /// addition): <c>{expr} COLLATE DATABASE_DEFAULT</c> on SQL Server.
    /// </summary>
    string DefaultCollate(string expr);

    /// <summary>
    /// The key a text value <paramref name="expr"/> (a text column, a parameter or a text expression) is compared and grouped by
    /// like SQL Server's <c>_CI_AS</c> columns (plan 2.7b, ruling Q1, #299): case, width, kana type and trailing spaces ignored,
    /// <c>ß</c> equal to <c>ss</c>, accents kept. The query compiler wraps both sides of text equality, <c>IN</c>, text joins, <c>distinct</c> and group-by in it,
    /// and a unique (alternate) key on a text column is an index on it. SQL Server: <paramref name="expr"/> itself, whose
    /// collation does it. PostgreSQL: <c>(replace(replace(rtrim(expr::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as)</c>,
    /// the nondeterministic ICU collation
    /// <see cref="PrepareDatabaseAsync"/> creates (the columns stay <c>citext</c>, since PostgreSQL 16 refuses <c>LIKE</c> on a
    /// nondeterministic collation); an expression other than a column or a parameter is parenthesized first.
    /// </summary>
    string TextKey(string expr);

    /// <summary>
    /// The value a group-by on the text key of <paramref name="expr"/> shows (plan 2.7b, Task 2 fix round 1): one stored value of
    /// the group, with its spaces and characters, the same at every run. SQL Server: <paramref name="expr"/> itself (grouped by
    /// itself). PostgreSQL: <c>CAST(MIN(CAST(expr AS text) COLLATE "C") AS citext)</c>, the smallest stored value in byte order.
    /// </summary>
    string TextKeyMember(string expr);

    /// <summary>
    /// The value a <c>SELECT DISTINCT</c> projects for the text <paramref name="expr"/>, so that the rows whose text keys are equal
    /// make one row that shows a stored value (plan 2.7b, Task 2 fix round 1). SQL Server: <paramref name="expr"/> itself.
    /// PostgreSQL: <see cref="TextKeyMember"/> as a window over the rows of the same text key
    /// (<c>… OVER (PARTITION BY TextKey(expr))</c>), a function of the key, so <c>DISTINCT</c> keeps one row per key.
    /// </summary>
    string TextKeyDistinctMember(string expr);

    /// <summary>
    /// The query of the unique indexes of the <c>data</c> schema whose key holds a text column compared as stored instead of
    /// through <see cref="TextKey"/> (plan 2.7b, ruling Q3: a 2.7a organization's keys, which the seed step <c>pg-text-keys-299</c>
    /// rebuilds), one row per key column in key order: table, index, column, true when the column is text,
    /// the filter predicate of a filtered index or null. Null where <see cref="TextKey"/> is the column itself (SQL Server).
    /// </summary>
    string? PlainTextKeyIndexes { get; }

    /// <summary>
    /// <paramref name="expr"/> moved by <paramref name="amount"/> (an integer expression, negative to go back) units of
    /// <paramref name="unit"/> (<c>year</c>, <c>quarter</c>, <c>month</c>, <c>week</c>, <c>day</c>, <c>hour</c>, <c>minute</c>,
    /// <c>second</c>): <c>DATEADD(unit, amount, expr)</c> on SQL Server.
    /// </summary>
    string AddInterval(string unit, string amount, string expr);

    /// <summary>
    /// The integer <paramref name="unit"/> (<c>year</c>, <c>quarter</c>, <c>month</c>, <c>day</c>, <c>hour</c>, <c>minute</c>,
    /// <c>second</c>; Task 7: <c>week</c>, the week of the year whose weeks start on Sunday, 1 January in week 1, and
    /// <c>iso_week</c>) of the date <paramref name="expr"/> (plan 2.7a, Task 6): <c>DATEPART(unit, expr)</c> on SQL Server,
    /// <c>CAST(EXTRACT(unit FROM expr) AS integer)</c> on PostgreSQL (whose sessions run in UTC).
    /// </summary>
    /// <exception cref="ArgumentException">Another unit.</exception>
    string DatePart(string unit, string expr);

    /// <summary>The aggregate that concatenates <paramref name="expr"/> separated by <paramref name="separator"/> (a SQL string literal).</summary>
    string StringAgg(string expr, string separator);

    /// <summary>
    /// The text expressions <paramref name="parts"/> joined into one text (plan 2.7b, Task 10): <c>a + b + …</c> on SQL Server,
    /// <c>a || b || …</c> on PostgreSQL, unparenthesized (both bind tighter than a comparison or a <c>LIKE</c>). A null part
    /// makes the result null on both engines (SQL Server's default <c>CONCAT_NULL_YIELDS_NULL</c>).
    /// </summary>
    /// <exception cref="ArgumentException">Fewer than two parts.</exception>
    string Concat(params string[] parts);

    /// <summary><paramref name="a"/>, or <paramref name="b"/> when it is null (<c>ISNULL(a, b)</c> on SQL Server).</summary>
    string IsNull(string a, string b);

    /// <summary>
    /// The literal of <paramref name="value"/> for a Boolean column (<see cref="SqlColumnKind.Boolean"/>; plan 2.7a, Task 5 fix
    /// round 1): <c>1</c>/<c>0</c> on SQL Server (<c>bit</c>), <c>true</c>/<c>false</c> on PostgreSQL, where an integer is not a
    /// <c>boolean</c>.
    /// </summary>
    string BooleanLiteral(bool value);

    /// <summary>
    /// <paramref name="expr"/> (text) converted to the integer <paramref name="kind"/> (<see cref="SqlColumnKind.Int32"/> or
    /// <see cref="SqlColumnKind.Int64"/>), or null when it is not an integer of that type (plan 2.7a, Task 6): <c>TRY_CAST(expr AS
    /// int)</c> on SQL Server; on PostgreSQL, which has no <c>TRY_CAST</c>, a <c>CASE</c> that casts only digits within the range
    /// (<paramref name="expr"/> is evaluated more than once: a column or an expression without side effects). The query compiler's
    /// other conversions are Task 7's.
    /// </summary>
    /// <exception cref="NotSupportedException"><paramref name="kind"/> is not an integer kind.</exception>
    string TryCastInteger(string expr, SqlColumnKind kind);

    // ---- row versions and change tracking (plan 2.7a, Task 7; ruling P4) -----------------------------------------------------

    /// <summary>
    /// The <c>bigint</c> expression <paramref name="expr"/> as a value of a row version column (<c>versionnumber</c>,
    /// <c>meta.deleted_record.version</c>), so that a comparison with the column seeks on its index: <c>CAST(expr AS binary(8))</c>
    /// on SQL Server (a <c>rowversion</c>, whose big-endian bytes order like the numbers); the expression itself on PostgreSQL,
    /// whose column is a <c>bigint</c>.
    /// </summary>
    string RowVersionValue(string expr);

    /// <summary>
    /// A <c>bigint</c> expression of the bound of <see cref="MinActiveRowVersionAsync"/>, at or below every version written after
    /// the statement: where change tracking of a table starts (<c>enabled_version</c>). SQL Server:
    /// <c>CAST(MIN_ACTIVE_ROWVERSION() AS bigint)</c>. PostgreSQL: the organization's function <c>data.row_version_bound()</c>, the
    /// same bound as <see cref="MinActiveRowVersionAsync"/> (its two reads in order: never above the bound).
    /// </summary>
    string RowVersionStart { get; }

    /// <summary>
    /// The upper bound of a change tracking read (plan 2.13): every version below it is committed or rolled back, and every write
    /// still in flight or to come has a version at or above it (SQL Server's <c>MIN_ACTIVE_ROWVERSION()</c>). Runs as commands of
    /// its own in the session of <paramref name="s"/>, before the rows are read. On PostgreSQL, whose sequence is not commit
    /// ordered (ruling P4): the lower of the sequence's next value and the lowest version a transaction in flight may hold, which
    /// each one publishes as an advisory lock before its first draw (<c>data.next_versionnumber()</c>). It never waits on a
    /// transaction; it waits only for the few statements of another session that peek the sequence and publish (a gate lock), so
    /// the bound never goes down, as on SQL Server (Task 7 fix round 1).
    /// </summary>
    Task<long> MinActiveRowVersionAsync(DataSession s, CancellationToken ct);

    /// <summary>
    /// The expression that draws a row version for an <c>INSERT</c> that writes it itself (the deletion log), or null where the
    /// engine sets the column (SQL Server's <c>rowversion</c>): <c>data.next_versionnumber()</c> on PostgreSQL, which publishes the
    /// transaction to <see cref="MinActiveRowVersionAsync"/> before it draws.
    /// </summary>
    string? NextRowVersion { get; }

    // ---- query, computed column and rollup expressions (plan 2.7a, Task 7) ----------------------------------------------------
    // The SQL Server text of each member is the one the query compiler, ComputedSql and RollupSql wrote before (pinned by their
    // tests). Expressions passed in are safe SQL of the caller; "a type" is one of the portable spellings int, bigint,
    // decimal(p, s), float, or a type this dialect writes (ColumnType, TypeMapping.ToSql).

    /// <summary>The keyword that starts a <c>WITH</c> whose CTEs may refer to themselves, with its trailing space: <c>WITH </c> on SQL Server, <c>WITH RECURSIVE </c> on PostgreSQL.</summary>
    string RecursiveWith { get; }

    /// <summary>
    /// The query hint that lets the recursive CTEs of a statement go <paramref name="levels"/> deep, with its leading line break,
    /// written before the statement's terminator: <c>\nOPTION (MAXRECURSION n)</c> on SQL Server; empty on PostgreSQL, which has no
    /// such limit (the CTEs bound their own depth).
    /// </summary>
    string RecursionLimit(int levels);

    /// <summary>
    /// A lateral join of the table source <paramref name="source"/> (a parenthesized subquery) as <paramref name="alias"/>, written
    /// after a table of the <c>FROM</c> clause: <c>OUTER APPLY … AS a</c> (<paramref name="outer"/>) or <c>CROSS APPLY … AS a</c> on
    /// SQL Server; <c>LEFT JOIN LATERAL … AS a ON true</c> or <c>CROSS JOIN LATERAL … AS a</c> on PostgreSQL.
    /// </summary>
    string Apply(string source, string alias, bool outer);

    /// <summary>
    /// The number <paramref name="expr"/> as <paramref name="type"/>, or null when it does not fit (<c>TRY_CAST(expr AS type)</c> on
    /// SQL Server): a whole type truncates the decimals, a decimal type rounds half away from zero to its scale. PostgreSQL, which
    /// has no <c>TRY_CAST</c>, checks the range in a scalar subquery that reads <paramref name="expr"/> once.
    /// </summary>
    string TryCast(string expr, string type);

    /// <summary>
    /// The text <paramref name="expr"/> as the number <paramref name="type"/>, or null when it is not a number of that type
    /// (<c>TRY_CAST(expr AS type)</c> on SQL Server: spaces around it allowed; an exponent only for <c>float</c>, null past the
    /// largest double and 0 below the smallest one: <c>data.try_float</c> on PostgreSQL).
    /// </summary>
    string TryCastText(string expr, string type);

    /// <summary>The <c>bigint</c> count of the non-null <paramref name="expr"/> (<c>*</c> for rows): <c>COUNT_BIG(expr)</c> on SQL Server.</summary>
    string CountBig(string expr);

    /// <summary>
    /// The sum of the whole numbers <paramref name="expr"/> as a <c>bigint</c>, an overflow being an error
    /// (<see cref="DbErrorKind.NumericOverflow"/>): <c>SUM(CAST(expr AS bigint))</c> on SQL Server (<c>SUM(expr)</c> when
    /// <paramref name="bigint"/>: the values are <c>bigint</c> already); on PostgreSQL, whose <c>SUM(bigint)</c> is a
    /// <c>numeric</c> that never overflows, that sum cast back to <c>bigint</c>.
    /// </summary>
    string SumBigint(string expr, bool bigint = false);

    /// <summary><paramref name="expr"/> rounded half away from zero to <paramref name="digits"/> decimals (<c>ROUND(expr, digits)</c>; a <c>numeric</c> on PostgreSQL, whose <c>round</c> of a <c>double</c> rounds half to even).</summary>
    string Round(string expr, string digits);

    /// <summary><paramref name="expr"/> cut toward zero to <paramref name="digits"/> decimals (<c>ROUND(expr, digits, 1)</c> on SQL Server, <c>trunc</c> on PostgreSQL).</summary>
    string Truncate(string expr, string digits);

    /// <summary>The natural logarithm (<c>LOG(expr)</c> on SQL Server, <c>ln(expr)</c> on PostgreSQL, whose <c>log</c> is base 10).</summary>
    string NaturalLog(string expr);

    /// <summary>
    /// The text literal of <paramref name="value"/>: <c>N'…'</c> on SQL Server, <c>'…'</c> on PostgreSQL (where <c>N'…'</c> is a
    /// <c>character</c> value, whose trailing spaces a conversion to text drops).
    /// </summary>
    string TextLiteral(string value);

    /// <summary>
    /// The text <paramref name="expr"/> (the result of a text function) compared as the database's text columns, ignoring case:
    /// itself on SQL Server, whose text functions keep the collation; <c>CAST(expr AS citext)</c> on PostgreSQL, whose text functions
    /// return <c>text</c>.
    /// </summary>
    string DatabaseText(string expr);

    /// <summary>The first <paramref name="count"/> characters of <paramref name="text"/> (<c>LEFT</c>; a whole-number count of any size).</summary>
    string Left(string text, string count);

    /// <summary>The last <paramref name="count"/> characters of <paramref name="text"/> (<c>RIGHT</c>).</summary>
    string Right(string text, string count);

    /// <summary><paramref name="count"/> characters of <paramref name="text"/> from the 1-based <paramref name="start"/> (<c>SUBSTRING</c>).</summary>
    string Substring(string text, string start, string count);

    /// <summary>The bytes of the text <paramref name="expr"/>, two per character, trailing spaces counted (<c>DATALENGTH(expr)</c> on SQL Server).</summary>
    string DataLength(string expr);

    /// <summary>The characters of the text <paramref name="expr"/>, trailing spaces not counted (<c>LEN(expr)</c> on SQL Server).</summary>
    string TextLength(string expr);

    /// <summary>
    /// The 1-based position of <paramref name="find"/> in <paramref name="text"/>, searched from <paramref name="start"/> when given,
    /// 0 when it is not there (<c>CHARINDEX(find, text[, start])</c> on SQL Server); the comparison is the one of the operands'
    /// types and collations.
    /// </summary>
    string IndexOf(string find, string text, string? start = null);

    /// <summary><paramref name="text"/> with <paramref name="length"/> characters from <paramref name="start"/> replaced by <paramref name="replacement"/> (<c>STUFF</c> on SQL Server, <c>overlay</c> on PostgreSQL).</summary>
    string Stuff(string text, string start, string length, string replacement);

    /// <summary>The character of the Unicode code <paramref name="code"/>, null outside the characters (<c>NCHAR(code)</c> on SQL Server).</summary>
    string CharFromCode(string code);

    /// <summary>
    /// The number of <paramref name="unit"/> boundaries (<c>year</c>, <c>quarter</c>, <c>month</c>, <c>week</c> (Sundays),
    /// <c>day</c>, <c>hour</c>, <c>minute</c>, <c>second</c>, <c>millisecond</c>) crossed from <paramref name="start"/> to
    /// <paramref name="end"/>, an <c>int</c> (<c>bigint</c> with <paramref name="big"/>): <c>DATEDIFF(unit, start, end)</c>
    /// (<c>DATEDIFF_BIG</c>) on SQL Server.
    /// </summary>
    /// <exception cref="ArgumentException">Another unit.</exception>
    string DateDiff(string unit, string start, string end, bool big = false);

    /// <summary>The day of the week of the date <paramref name="expr"/>, Sunday 1 to Saturday 7, whatever the session's first day of the week.</summary>
    string Weekday(string expr);

    /// <summary>The first day of the year of the date <paramref name="expr"/> (<c>DATEFROMPARTS(YEAR(expr), 1, 1)</c> on SQL Server).</summary>
    string YearStart(string expr);

    /// <summary>
    /// Power Fx <c>Trim</c> of the text <paramref name="expr"/>: the spaces at both ends removed and each run of inner spaces made
    /// one, compared as the database's text afterwards.
    /// </summary>
    string CollapseSpaces(string expr);

    /// <summary>
    /// The <c>MAX</c> of the yes/no <paramref name="expr"/> as a yes/no value (SQL Server's <c>MAX</c> takes no <c>bit</c>:
    /// <c>CAST(MAX(CAST(expr AS tinyint)) AS bit)</c>; <c>bool_or</c> on PostgreSQL).
    /// </summary>
    string MaxBoolean(string expr);

    /// <summary>
    /// The pattern <paramref name="pattern"/> of a <c>like</c> condition, written with SQL Server's <c>LIKE</c> syntax (Dataverse's:
    /// <c>%</c>, <c>_</c>, a bracketed character such as <c>[%]</c>), as this engine's <c>LIKE</c> reads it: unchanged on SQL Server;
    /// on PostgreSQL a bracketed single character becomes that character escaped with a backslash, and a backslash is escaped.
    /// </summary>
    /// <exception cref="NotSupportedException">PostgreSQL: a character set (<c>[ab]</c>, <c>[^a]</c>), which its <c>LIKE</c> does not
    /// have (<see cref="Like"/> matches them).</exception>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">PostgreSQL: a range, as <see cref="Like"/>.</exception>
    string LikePattern(string pattern);

    /// <summary>
    /// The condition <paramref name="expr"/> <c>LIKE</c> (or, with <paramref name="negate"/>, <c>NOT LIKE</c>) the pattern
    /// <paramref name="pattern"/>, written with SQL Server's <c>LIKE</c> syntax (Dataverse's: <c>%</c>, <c>_</c> and bracketed
    /// character sets), case ignored and accents kept (<c>_CI_AS</c>); <paramref name="parameter"/> turns the pattern this engine
    /// reads into the SQL of its value (a parameter). SQL Server: <c>expr [NOT] LIKE @p</c> unchanged. PostgreSQL: a <c>LIKE</c>
    /// (<see cref="LikePattern"/>) when the pattern has no set of other than one character; otherwise the case-insensitive
    /// regular expression match <c>expr ~* @p</c> (<c>!~*</c>) of the whole value, with SQL Server's reading of a set: <c>[^]</c> is
    /// any one character, <c>[]</c> none, a set without <c>]</c> runs to the end of the pattern, a <c>^</c> right after
    /// <c>[^</c> is dropped, a <c>-</c> at either end is itself (plan 2.7a, Task 7 fix round 1).
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">PostgreSQL: 501 <see cref="Xrmd.Core.Errors.ErrorCodes.Unexpected"/>, a range (<c>[a-c]</c>,
    /// <c>[^0-9]</c>): SQL Server's ranges follow the collation's order (<c>[a-c]</c> matches <c>á</c> and <c>B</c>), a regular
    /// expression's follow code points (plan 2.7a, ruling P15).</exception>
    string Like(string expr, string pattern, bool negate, Func<string, string> parameter);

    /// <summary>
    /// The last value of the date and time column type (<see cref="SqlColumnKind.DateTime"/>), the upper edge of <c>DateAdd</c>:
    /// <c>9999-12-31T23:59:59.9999999</c> as <c>datetime2</c> on SQL Server; <c>9999-12-31T23:59:59.999999</c> on PostgreSQL, whose
    /// <c>timestamptz</c> keeps microseconds and rounds a seventh digit up to the year 10000.
    /// </summary>
    string LastDateTime { get; }

    /// <summary>
    /// The text <paramref name="expr"/> with the characters that are special in a <c>LIKE</c> pattern escaped, so that it matches
    /// itself (<c>REPLACE</c>s that bracket <c>[</c>, <c>%</c> and <c>_</c> on SQL Server; backslashes on PostgreSQL).
    /// </summary>
    string EscapeLike(string expr);

    /// <summary>The value <paramref name="expr"/> as text of at most <paramref name="length"/> characters (<c>CONVERT(nvarchar(n), expr)</c> on SQL Server).</summary>
    string ToText(string expr, int length);

    /// <summary>
    /// An <c>UPDATE</c> of <paramref name="table"/> (quoted, qualified) as <paramref name="alias"/> joined with
    /// <paramref name="source"/> on <paramref name="on"/>, setting <paramref name="set"/> (quoted column, value), filtered by
    /// <paramref name="where"/> when given, returning <paramref name="returning"/> of the written rows, without terminator. SQL
    /// Server: <c>UPDATE a SET a.c = v\nOUTPUT inserted.c, …\nFROM table AS a JOIN source ON on\nWHERE where</c>; PostgreSQL:
    /// <c>UPDATE table AS a SET c = v\nFROM source\nWHERE on AND (where)\nRETURNING a.c, …</c>.
    /// </summary>
    string UpdateFrom(string table, string alias, IReadOnlyList<(string Column, string Value)> set, string source, string on,
        string? where, IReadOnlyList<Returned> returning);

    /// <summary>The class of a database failure (ruling P9); <see cref="DbErrorKind.Other"/> for a failure of another engine.</summary>
    DbErrorKind Classify(DbException ex);

    /// <summary>
    /// True when <paramref name="ex"/> is a <see cref="DbErrorKind.UniqueViolation"/> of a table's primary key, not of another
    /// unique index (plan 2.7a, Task 2 addition: SQL Server error 2627).
    /// </summary>
    bool IsPrimaryKeyViolation(DbException ex);

    /// <summary>The expression that draws the next value of the sequence <paramref name="sequence"/> (a quoted, qualified name).</summary>
    string NextSequenceValue(string sequence);

    // ---- DDL and catalog checks (plan 2.7a, Task 4) ------------------------------------------------------------------------
    // Names passed to these members are constants of the caller or validated logical names, unquoted unless said otherwise;
    // the member quotes them.

    /// <summary>
    /// The name the engine stores for an index, a constraint or a trigger named <paramref name="name"/> (plan 2.7a, Task 4): the
    /// name itself on SQL Server (128 characters); on PostgreSQL, whose identifiers hold 63 bytes, a longer name becomes its first
    /// 54 bytes, <c>_</c> and the first 8 hex digits of the SHA-256 of the whole name (unique and the same at every call).
    /// </summary>
    string ObjectName(string name);

    /// <summary>
    /// Refuses a table or column name the engine would not store as written (plan 2.7a, Task 4 fix round 1): over 128 characters
    /// on SQL Server, over 63 bytes of UTF-8 on PostgreSQL, which would otherwise cut it silently. <paramref name="kind"/> names
    /// what it is in the message (<c>table</c>, <c>column</c>).
    /// </summary>
    /// <exception cref="XrmdValidationException">The name is too long for the engine.</exception>
    void EnsureIdentifier(string name, string kind);

    /// <summary>
    /// The folder of the embedded meta migrations of this engine under <c>Migrations/</c> (plan 2.7a, ruling P6; Task 4 fix
    /// round 1): empty for SQL Server (the folder itself), <c>PostgreSql</c> for PostgreSQL.
    /// </summary>
    string MigrationFolder { get; }

    /// <summary>
    /// True when a failed statement aborts the whole transaction, so that a caller that recovers from the failure (a lost race
    /// on a unique index) must have run the statement under a savepoint (plan 2.7a, Task 6): false on SQL Server, where a failed
    /// statement rolls back alone; true on PostgreSQL. <c>DataSessionSql.RecoverableAsync</c> reads it.
    /// </summary>
    bool FailedStatementAbortsTransaction { get; }

    /// <summary>
    /// The statement that runs the trigger function which sets the <c>versionnumber</c> column of the data table
    /// <paramref name="schema"/>.<paramref name="table"/> on every insert and update, after its <c>CREATE TABLE</c> (ruling P4):
    /// empty on SQL Server, where the column is a <c>rowversion</c>; on PostgreSQL the trigger
    /// <c>trg_&lt;table&gt;_versionnumber</c> calling <c>&lt;schema&gt;.set_versionnumber()</c>.
    /// </summary>
    string RowVersionTrigger(string schema, string table);

    /// <summary>
    /// The definition of the row version column <paramref name="column"/> in a <c>CREATE TABLE</c>: <c>[c] rowversion</c> on
    /// SQL Server, <c>"c" bigint NOT NULL</c> on PostgreSQL (ruling P4; its trigger sets it, <see cref="RowVersionTrigger"/>).
    /// </summary>
    string RowVersionColumn(string column);

    /// <summary>A condition (a boolean SQL expression) true when the table <paramref name="schema"/>.<paramref name="table"/> exists.</summary>
    string TableExists(string schema, string table);

    /// <summary>A condition true when the table <paramref name="schema"/>.<paramref name="table"/> has the column <paramref name="column"/>.</summary>
    string ColumnExists(string schema, string table, string column);

    /// <summary>A condition true when the table <paramref name="schema"/>.<paramref name="table"/> has the index <paramref name="index"/> (its <see cref="ObjectName"/>).</summary>
    string IndexExists(string schema, string table, string index);

    /// <summary>
    /// The statement that runs <paramref name="statement"/> (one statement without parameters and without a terminator) only when
    /// <paramref name="condition"/> holds. The statement is compiled when it runs (<c>EXEC</c> on SQL Server, <c>EXECUTE</c> in a
    /// <c>DO</c> block on PostgreSQL), so it may name a table or a column that the condition checks for.
    /// </summary>
    string When(string condition, string statement);

    /// <summary>The statement that creates the schema <paramref name="schema"/> when it is missing.</summary>
    string CreateSchemaIfMissing(string schema);

    /// <summary>
    /// The statement that creates the index <paramref name="index"/> (its <see cref="ObjectName"/>) on
    /// <paramref name="schema"/>.<paramref name="table"/> when the table does not have it: <paramref name="columns"/> is the
    /// column list as the caller writes it (quoted names, <c>DESC</c>), <paramref name="filter"/> the predicate of a filtered index.
    /// </summary>
    string CreateIndexIfMissing(string schema, string table, string index, string columns, bool unique = false, string? filter = null);

    /// <summary>The statement that drops the index <paramref name="index"/> of <paramref name="schema"/>.<paramref name="table"/> when it exists.</summary>
    string DropIndexIfExists(string schema, string table, string index);

    /// <summary>
    /// <c>ALTER TABLE</c> <paramref name="table"/> (quoted, qualified) adding the columns <paramref name="definitions"/> (each
    /// <c>"name" type NULL</c>): one <c>ADD</c> on SQL Server, one <c>ADD COLUMN</c> per column on PostgreSQL.
    /// </summary>
    string AddColumns(string table, IReadOnlyList<string> definitions);

    /// <summary>
    /// The statement that adds the nullable column <paramref name="column"/> of type <paramref name="type"/> to
    /// <paramref name="schema"/>.<paramref name="table"/> when the table does not have it.
    /// </summary>
    string AddColumnIfMissing(string schema, string table, string column, string type);

    /// <summary><c>ALTER TABLE</c> <paramref name="table"/> (quoted, qualified) dropping the quoted <paramref name="columns"/>.</summary>
    string DropColumns(string table, IReadOnlyList<string> columns);

    /// <summary>
    /// <c>ALTER TABLE</c> <paramref name="table"/> (quoted, qualified) giving the quoted <paramref name="column"/> the type
    /// <paramref name="type"/>, nullable (SQL Server restates the nullability, PostgreSQL keeps it).
    /// </summary>
    string AlterColumnType(string table, string column, string type);

    /// <summary>The statement that renames the column <paramref name="column"/> of <paramref name="schema"/>.<paramref name="table"/> to <paramref name="newName"/>.</summary>
    string RenameColumn(string schema, string table, string column, string newName);

    /// <summary>
    /// The statements that create the schema <paramref name="schema"/> and the <c>bigint</c> sequence
    /// <paramref name="schema"/>.<paramref name="name"/> starting at <paramref name="start"/> (step 1, minimum 0, no cache), each
    /// when it is missing.
    /// </summary>
    string CreateSequenceIfMissing(string schema, string name, long start);

    /// <summary>The statement that creates the sequence of <see cref="CreateSequenceIfMissing"/>, which must not exist.</summary>
    string CreateSequence(string schema, string name, long start);

    /// <summary>The statement that drops the sequence <paramref name="schema"/>.<paramref name="name"/> when it exists.</summary>
    string DropSequenceIfExists(string schema, string name);

    /// <summary>
    /// The query that reads the sequence <paramref name="schema"/>.<paramref name="name"/>: one row with its start value and the
    /// value its next draw takes (both <c>bigint</c>), or no row when it does not exist.
    /// </summary>
    string ReadSequence(string schema, string name);

    /// <summary>
    /// Prepares an organization database for its meta migrations, on <paramref name="connection"/> before the first one
    /// (plan 2.7a, Task 4): nothing on SQL Server; on PostgreSQL the bootstrap of ruling P4 and the Global Constraints (the
    /// server is PostgreSQL 16 or later, the schemas <c>meta</c>, <c>data</c> and <c>auth</c>, the <c>citext</c> extension, the
    /// collation <c>data.xrmv_ci_as</c> of <see cref="TextKey"/> (plan 2.7b, ruling Q1), the
    /// sequence <c>data.versionnumber_seq</c> and the trigger function <c>data.set_versionnumber()</c>), each when missing.
    /// Sends no string parameter: the driver sends them as <c>citext</c>, which may not exist yet.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server is older than the engine version xrmverse needs.</exception>
    Task PrepareDatabaseAsync(DbConnection connection, CancellationToken ct);

    /// <summary>
    /// Adjusts a parameter that <see cref="DbCommandExtensions.AddParameter"/> created on a connection of this engine, after its
    /// type and value are set (plan 2.7a, Task 3 addition): on PostgreSQL a date (<see cref="System.Data.DbType.DateTime"/>,
    /// <see cref="System.Data.DbType.DateTime2"/>) is sent as a UTC <c>timestamptz</c>, since Npgsql refuses an unspecified
    /// <see cref="DateTimeKind"/> there, and a string as <c>citext</c>, so that it compares with the text columns ignoring case
    /// (fix round 1). Nothing on SQL Server.
    /// </summary>
    void ConfigureParameter(DbParameter parameter);
}

/// <summary>An item of <see cref="ISqlDialect.UpdateFrom"/>'s returning list (plan 2.7a, Task 7).</summary>
/// <param name="Sql">A quoted column of the updated table, or an expression that reads no column (<paramref name="Column"/> false).</param>
/// <param name="Column">True when <paramref name="Sql"/> is a column of the written row.</param>
public readonly record struct Returned(string Sql, bool Column = true);
