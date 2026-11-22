using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;

namespace Xrmd.Storage.PostgreSql;

/// <summary>
/// The PostgreSQL text of every engine-specific fragment (plan 2.7a, Task 3; ruling P3): the Global Constraints type mapping,
/// ruling P7 locks (row-locking clauses and advisory locks), ruling P8 JSON (<c>jsonb</c> functions) and ruling P9 SQLSTATE
/// classes. A shape PostgreSQL cannot write with the same meaning is refused with <see cref="NotSupportedException"/>, never
/// approximated.
/// </summary>
/// <remarks>
/// Sessions run in UTC (<see cref="NpgsqlConnectionFactory"/> sets <c>Timezone=UTC</c>), so <c>timestamptz</c> arithmetic and
/// date parts match SQL Server's UTC <c>datetime2</c>.
/// </remarks>
public sealed partial class PostgresDialect : ISqlDialect
{
    private PostgresDialect()
    {
    }

    /// <summary>The only instance.</summary>
    public static PostgresDialect Instance { get; } = new();

    /// <summary>The type of the connections whose engine this dialect writes for (<see cref="SqlDialects"/>).</summary>
    public static Type ConnectionType => typeof(NpgsqlConnection);

    public StorageProvider Provider => StorageProvider.PostgreSql;

    public string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// The Global Constraints mapping: text is <c>citext</c> at every length (case-insensitive and accent-sensitive like
    /// <c>_CI_AS</c>, ruling P5; <c>MaxLength</c> is enforced by validation, not by the column).
    /// </summary>
    public string ColumnType(SqlColumnKind kind, int length = 0, int precision = 0, int scale = 0) => kind switch
    {
        SqlColumnKind.Guid => "uuid",
        SqlColumnKind.String or SqlColumnKind.Text => "citext",
        SqlColumnKind.Int32 => "integer",
        SqlColumnKind.Int64 => "bigint",
        SqlColumnKind.Decimal => $"numeric({Num(precision)},{Num(scale)})",
        SqlColumnKind.Double => "double precision",
        SqlColumnKind.Boolean => "boolean",
        SqlColumnKind.DateTime => "timestamptz",
        SqlColumnKind.Binary => "bytea",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown column kind."),
    };

    /// <summary>
    /// <c>bigint</c>, set by the organization's <c>data.set_versionnumber()</c> trigger from <c>data.versionnumber_seq</c>
    /// (ruling P4; the DDL is Task 4's).
    /// </summary>
    public string RowVersionType => "bigint";

    public string Limit(string selectSql, string? countExpr)
    {
        ArgumentNullException.ThrowIfNull(selectSql);
        if (countExpr is null) return selectSql;
        if (!selectSql.StartsWith("SELECT ", StringComparison.Ordinal))
            throw new ArgumentException("The statement to limit must start with 'SELECT '.", nameof(selectSql));
        // The LIMIT goes at the end: a terminated statement would get it after its terminator.
        if (selectSql.TrimEnd().EndsWith(';'))
            throw new NotSupportedException("Limit: PostgreSQL limits with a trailing LIMIT, which cannot follow a terminated statement.");
        return selectSql + " LIMIT " + countExpr;
    }

    public string Page(int offset, int fetch) => Page(Num(offset), Num(fetch));

    public string Page(string offsetExpr, string fetchExpr) => $"OFFSET {offsetExpr} LIMIT {fetchExpr}";

    /// <summary>
    /// <c>statement_timestamp()</c>: a <c>timestamptz</c>, one value per statement that moves between statements, like
    /// <c>SYSUTCDATETIME()</c> (plan 2.7a, Task 3 fix round 1 controller ruling; <c>now()</c> would be the transaction start).
    /// </summary>
    public string UtcNow => "statement_timestamp()";

    public string NewGuid => "gen_random_uuid()";

    /// <summary>Empty: PostgreSQL locks rows with the trailing <see cref="LockClause"/>.</summary>
    public string LockHint(RowLock mode) =>
        Enum.IsDefined(mode) ? "" : throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown row lock.");

    /// <summary>
    /// <c> FOR UPDATE [OF a] [SKIP LOCKED]</c> for the update and exclusive modes, <c> FOR SHARE [OF a]</c> for the shared ones
    /// (ruling P7). The key-range half of a <c>HOLDLOCK</c> mode is <see cref="LockKeyAsync"/>'s.
    /// </summary>
    /// <exception cref="NotSupportedException"><see cref="RowLock.TableShared"/>: PostgreSQL locks a table with a
    /// <c>LOCK TABLE … IN SHARE MODE</c> statement of its own, not with a clause of the read.</exception>
    public string LockClause(RowLock mode, string? ofAlias = null)
    {
        var of = string.IsNullOrEmpty(ofAlias) ? "" : " OF " + ofAlias;
        return mode switch
        {
            RowLock.Update or RowLock.UpdateRange or RowLock.UpdateRow or RowLock.UpdateRowRange or RowLock.UpdateSerializable
                or RowLock.Exclusive => " FOR UPDATE" + of,
            RowLock.UpdateSkipLocked => " FOR UPDATE" + of + " SKIP LOCKED",
            RowLock.Shared or RowLock.ReadCommittedLock or RowLock.RepeatableRead => " FOR SHARE" + of,
            RowLock.TableShared => throw new NotSupportedException(
                "LockClause(TableShared): PostgreSQL locks a whole table with a LOCK TABLE … IN SHARE MODE statement before the read, not with a clause of it."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown row lock."),
        };
    }

    /// <summary>
    /// The key lock statement (ruling P7; plan 2.7a, Task 3 fix round 1): a transaction advisory lock on the 64-bit hash, with
    /// seed 1 (application locks use seed 0, so the two never share a key), of <c>@_key</c> =
    /// <see cref="KeyLockText"/>. It returns one row, which <see cref="LockKeyAsync"/> discards: it is never a prefix of a read.
    /// </summary>
    public const string KeyLockStatement = "SELECT pg_advisory_xact_lock(hashtextextended(@_key, 1))";

    /// <summary>
    /// The text whose hash <see cref="KeyLockStatement"/> locks: <c>scope:key</c> in lower case, since SQL Server's key-range
    /// locks on the case-insensitive indexes cover every casing of a key. A <see cref="Guid"/> is written <c>D</c>, a number
    /// in the invariant culture, a null as an empty key.
    /// </summary>
    public static string KeyLockText(string scope, object? key)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        var text = key switch
        {
            null or DBNull => "",
            Guid g => g.ToString("D"),
            string str => str,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => key.ToString() ?? "",
        };
        return (scope + ":" + text).ToLowerInvariant();
    }

    /// <summary>True: <see cref="KeyLockStatement"/> holds to the end of the transaction (plan 2.7b, final review I2).</summary>
    public bool KeyLocksNeedTransaction => true;

    /// <summary>
    /// Runs <see cref="KeyLockStatement"/> as a command of its own in the session's transaction: the read that follows takes
    /// its snapshot after the lock is granted (READ COMMITTED), and its results are its command's only ones.
    /// </summary>
    public async Task LockKeyAsync(DataSession s, string scope, object? key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command(KeyLockStatement);
        AddText(cmd, "@_key", KeyLockText(scope, key));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The table share lock statement (plan 2.7b, ruling Q4): <c>LOCK TABLE &lt;schema&gt;."&lt;table&gt;" IN SHARE MODE</c>.
    /// Other transactions keep reading the table; their writes wait until the locking transaction ends.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain lower-case identifier.</exception>
    public static string LockTableSharedStatement(string schema, string table) => LockTableStatement(schema, table, "SHARE");

    /// <summary>
    /// The table write lock statement (plan 2.7b, Task 3): <c>LOCK TABLE &lt;schema&gt;."&lt;table&gt;" IN SHARE ROW EXCLUSIVE
    /// MODE</c>. Other transactions keep reading the table; their writes, and another such lock, wait until the locking
    /// transaction ends.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain lower-case identifier.</exception>
    public static string LockTableWritesStatement(string schema, string table) => LockTableStatement(schema, table, "SHARE ROW EXCLUSIVE");

    /// <summary>
    /// The table row-write lock statement (plan 2.7b, Task 4 fix round 3): <c>LOCK TABLE &lt;schema&gt;."&lt;table&gt;" IN ROW
    /// EXCLUSIVE MODE</c>, the lock an <c>UPDATE</c> takes. Other writers do not wait for it; <see cref="LockTableWritesStatement"/>
    /// and <see cref="LockTableSharedStatement"/> do.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="schema"/> is not a plain lower-case identifier.</exception>
    public static string LockTableRowWritesStatement(string schema, string table) => LockTableStatement(schema, table, "ROW EXCLUSIVE");

    private static string LockTableStatement(string schema, string table, string mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentException.ThrowIfNullOrEmpty(table);
        if (!schema.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') || char.IsDigit(schema[0]))
            throw new ArgumentException($"'{schema}' is not a plain schema name.", nameof(schema));
        return $"LOCK TABLE {schema}.{Instance.Quote(table)} IN {mode} MODE";
    }

    /// <summary>Runs <see cref="LockTableSharedStatement"/> as a command of its own in the session's transaction (ruling Q4).</summary>
    public async Task LockTableSharedAsync(DataSession s, string schema, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command(LockTableSharedStatement(schema, table));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Runs <see cref="LockTableWritesStatement"/> as a command of its own in the session's transaction (plan 2.7b, Task 3).</summary>
    public async Task LockTableWritesAsync(DataSession s, string schema, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command(LockTableWritesStatement(schema, table));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Runs <see cref="LockTableRowWritesStatement"/> as a command of its own in the session's transaction (plan 2.7b, Task 4 fix round 3).</summary>
    public async Task LockTableRowWritesAsync(DataSession s, string schema, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command(LockTableRowWritesStatement(schema, table));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- application locks (ruling P7): advisory locks on hashtextextended(lower(resource), 0) ---------------------------------
    //
    // A session owner takes pg_advisory_lock (held until released or the connection closes), a transaction owner
    // pg_advisory_xact_lock (held until the end of the transaction; PostgreSQL has no early release of it). A bounded wait polls
    // pg_try_advisory_[xact_]lock until the deadline (a monotonic clock) rather than setting lock_timeout: SET LOCAL needs a transaction (a session
    // lock may be taken outside one) and would stay in force for the rest of the transaction; the poll changes no setting of
    // the connection. The poll is not a fair queue: a waiter may be overtaken by a later one. The resource is lower-cased (SQL
    // Server's names compare like its collation) and sent as text, so the locks work before the citext extension exists.

    private const string KeyOf = "hashtextextended(@resource, 0)";

    public Task AcquireAppLockAsync(DataSession s, string resource, bool session, CancellationToken ct)
    {
        Check(s, resource, session);
        return ExecuteAsync(s, $"SELECT {(session ? "pg_advisory_lock" : "pg_advisory_xact_lock")}({KeyOf})", resource, ct);
    }

    public async Task AcquireAppLockAsync(DataSession s, string resource, bool session, TimeSpan timeout, CancellationToken ct)
    {
        Check(s, resource, session);
        var limit = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;
        var clock = Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(5);
        while (!await TryAsync(s, resource, session, ct))
        {
            var left = limit - clock.Elapsed;
            if (left <= TimeSpan.Zero)
                throw new AppLockException(resource, $"{TryFunction(session)} did not grant it within {Num((long)timeout.TotalMilliseconds)} ms");
            await Task.Delay(delay < left ? delay : left, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 100));
        }
    }

    public Task<bool> TryAcquireAppLockAsync(DataSession s, string resource, bool session, CancellationToken ct)
    {
        Check(s, resource, session);
        return TryAsync(s, resource, session, ct);
    }

    /// <summary>Releases a session lock (<c>pg_advisory_unlock</c>; not holding it is not an error, as on SQL Server).</summary>
    /// <exception cref="NotSupportedException">A transaction lock: PostgreSQL releases it only at the end of its transaction.</exception>
    public Task ReleaseAppLockAsync(DataSession s, string resource, bool session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(resource);
        if (!session)
            throw new NotSupportedException(
                "ReleaseAppLockAsync(session: false): PostgreSQL releases a transaction advisory lock only at the end of its transaction.");
        return ExecuteAsync(s, $"SELECT pg_advisory_unlock({KeyOf})", resource, ct);
    }

    /// <summary>
    /// True when this connection holds the exclusive advisory lock of <paramref name="resource"/> in the current database
    /// (<c>pg_locks</c>: a 64-bit key is stored as <c>classid</c> (high half), <c>objid</c> (low half), <c>objsubid = 1</c>).
    /// PostgreSQL does not tell a session lock from a transaction one there.
    /// </summary>
    public async Task<bool> HoldsAppLockAsync(DataSession s, string resource, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(resource);
        await using var cmd = s.Command($"""
            SELECT EXISTS (SELECT 1 FROM pg_locks
                WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND granted AND mode = 'ExclusiveLock' AND objsubid = 1
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND ((classid::bigint << 32) | objid::bigint) = {KeyOf})
            """);
        AddText(cmd, "@resource", resource.ToLowerInvariant());
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static void Check(DataSession s, string resource, bool session)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrEmpty(resource);
        // SQL Server refuses a transaction owner outside a transaction (sp_getapplock -999); here the lock would end with the
        // statement's own implicit transaction, so it is refused the same way.
        if (!session && s.Transaction is null)
            throw new AppLockException(resource, "a transaction advisory lock needs a transaction");
    }

    private static string TryFunction(bool session) => session ? "pg_try_advisory_lock" : "pg_try_advisory_xact_lock";

    private static async Task<bool> TryAsync(DataSession s, string resource, bool session, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT {TryFunction(session)}({KeyOf})");
        AddText(cmd, "@resource", resource.ToLowerInvariant());
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task ExecuteAsync(DataSession s, string sql, string resource, CancellationToken ct)
    {
        await using var cmd = s.Command(sql);
        AddText(cmd, "@resource", resource.ToLowerInvariant());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A parameter pinned to <c>text</c> (not <c>citext</c>): the locks run before the extension may exist.</summary>
    private static void AddText(DbCommand cmd, string name, string value)
    {
        var p = cmd.AddParameter(name, DbType.String, value);
        if (p is NpgsqlParameter np) np.NpgsqlDbType = NpgsqlDbType.Text;
    }

    // ---- JSON (ruling P8) --------------------------------------------------------------------------------------------------

    /// <summary><c>jsonb_array_elements_text(@p::jsonb)</c>: one text column named <c>value</c>.</summary>
    public string JsonArray(string param)
    {
        ArgumentException.ThrowIfNullOrEmpty(param);
        return $"jsonb_array_elements_text({param}::jsonb)";
    }

    public string JsonRecords(string param, IReadOnlyList<(string Name, SqlColumnKind Kind)> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return JsonRecords(param, columns.Select(c => new JsonColumn(c.Name, c.Kind)).ToList());
    }

    /// <summary>
    /// A derived table over the elements of the array: <c>(SELECT CAST(e.value -&gt;&gt; 'a' AS type) AS "a", … FROM
    /// jsonb_array_elements(@p::jsonb) AS e(value))</c>. Each column reads its path (lax, like <c>OPENJSON … WITH</c>: a missing
    /// property is null); <c>$</c> reads the element itself. The caller's alias follows the parenthesis.
    /// </summary>
    /// <exception cref="NotSupportedException">A <see cref="SqlColumnKind.Binary"/> column (OPENJSON converts the text to bytes,
    /// PostgreSQL has no equal cast) or a <c>strict</c> path.</exception>
    public string JsonRecords(string param, IReadOnlyList<JsonColumn> columns)
    {
        ArgumentException.ThrowIfNullOrEmpty(param);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0) throw new ArgumentException("At least one column is required.", nameof(columns));
        var list = columns.Select(c =>
        {
            if (c.Kind == SqlColumnKind.Binary)
                throw new NotSupportedException($"JsonRecords: the Binary column '{c.Name}' has no PostgreSQL equivalent of OPENJSON's conversion.");
            var path = c.Path is null ? [c.Name] : ParsePath(c.Path, nameof(JsonRecords));
            return $"CAST(e.value {TextAt(path)} AS {ColumnType(c.Kind, c.Length, c.Precision, c.Scale)}) AS {Quote(c.Name)}";
        });
        return $"(SELECT {string.Join(", ", list)} FROM jsonb_array_elements({param}::jsonb) AS e(value))";
    }

    /// <summary>
    /// <c>(CAST(expr AS jsonb) -&gt;&gt; 'a')</c>, or <c>#&gt;&gt; '{a,b}'</c> for a deeper path. Unlike <c>JSON_VALUE</c>, an
    /// object or array at the path is returned as its JSON text rather than null, and text that is not JSON fails the cast.
    /// </summary>
    /// <exception cref="NotSupportedException">A <c>strict</c> path.</exception>
    public string JsonValue(string expr, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        return $"(CAST({expr} AS jsonb) {TextAt(ParsePath(path, nameof(JsonValue)))})";
    }

    /// <summary>
    /// <c>pg_input_is_valid(CAST(expr AS text), 'jsonb')</c> (PostgreSQL 16): the check never raises, and it accepts exactly the
    /// texts that the <c>jsonb</c> cast of <see cref="JsonMember"/> accepts. Unlike SQL Server's <c>ISJSON</c>, a scalar
    /// (<c>5</c>, <c>"a"</c>) is valid JSON; it has no members, so <see cref="JsonMember"/> finds none either way.
    /// </summary>
    public string IsJson(string expr, bool valid = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        var check = $"pg_input_is_valid(CAST({expr} AS text), 'jsonb')";
        return valid ? check : $"NOT {check}";
    }

    /// <summary>
    /// The value at the path with <c>jsonb_path_query_first</c> in <c>strict</c> mode with errors silenced (SQL Server's lax
    /// <c>JSON_QUERY</c> neither unwraps arrays nor fails: a missing step gives NULL), behind a <c>CASE</c> on <see cref="IsJson"/>
    /// so that the <c>jsonb</c> cast never fails; then <c>jsonb_each</c> (an object) or <c>jsonb_array_elements … WITH
    /// ORDINALITY</c> (an array, keys from 0) of that value when it is of the kind the key needs, its members typed with
    /// <c>OPENJSON</c>'s numbers, the key compared in the <c>"C"</c> collation (<see cref="BinaryEquals"/>).
    /// </summary>
    public string JsonMember(string expr, string pathParam, string keyParam, bool keyIsIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        ArgumentException.ThrowIfNullOrEmpty(pathParam);
        ArgumentException.ThrowIfNullOrEmpty(keyParam);
        var parent = $"CASE WHEN {IsJson(expr)} THEN jsonb_path_query_first(CAST({expr} AS jsonb), CAST('strict ' || {pathParam} AS jsonpath), '{{}}', true) END";
        var kind = keyIsIndex ? "array" : "object";
        var members = $"CASE WHEN jsonb_typeof(jp.p) = '{kind}' THEN jp.p END";
        var (source, key) = keyIsIndex
            ? ($"jsonb_array_elements({members}) WITH ORDINALITY AS e(v, n)", "CAST(e.n - 1 AS text)")
            : ($"jsonb_each({members}) AS e(k, v)", "e.k");
        const string type = "CASE jsonb_typeof(e.v) WHEN 'null' THEN 0 WHEN 'string' THEN 1 WHEN 'number' THEN 2 WHEN 'boolean' THEN 3 WHEN 'array' THEN 4 ELSE 5 END";
        return $"SELECT 1 FROM (SELECT {parent} AS p) AS jp CROSS JOIN LATERAL " +
               $"(SELECT {key} AS {Quote("key")}, {type} AS {Quote("type")}, e.v #>> '{{}}' AS {Quote("value")} FROM {source}) AS j " +
               $"WHERE {BinaryEquals($"j.{Quote("key")}", keyParam)}";
    }

    /// <summary>The text operator that reads <paramref name="path"/>: <c>-&gt;&gt; 'a'</c> for one property, <c>#&gt;&gt; '{…}'</c> otherwise.</summary>
    private static string TextAt(IReadOnlyList<string> path)
    {
        if (path.Count == 1 && !IsIndex(path[0])) return $"->> {Literal(path[0])}";
        var elements = path.Select(p => p.Length > 0 && p.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')
            ? p
            : "\"" + p.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"");
        return $"#>> {Literal("{" + string.Join(",", elements) + "}")}";
    }

    private static bool IsIndex(string segment) => segment.Length > 0 && segment.All(char.IsAsciiDigit);

    /// <summary>The segments of a SQL Server JSON path (<c>[lax ]$.a.b[0]."c d"</c>); array indexes are digit segments.</summary>
    private static List<string> ParsePath(string path, string member)
    {
        ArgumentNullException.ThrowIfNull(path);
        var p = path.Trim();
        if (p.StartsWith("strict ", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{member}: the strict JSON path '{path}' (an error where the value is missing) has no PostgreSQL operator.");
        if (p.StartsWith("lax ", StringComparison.OrdinalIgnoreCase)) p = p[4..].TrimStart();
        if (!p.StartsWith('$')) throw new ArgumentException($"'{path}' is not a JSON path.", nameof(path));
        var segments = new List<string>();
        var i = 1;
        while (i < p.Length)
        {
            if (p[i] == '.')
            {
                i++;
                if (i < p.Length && p[i] == '"')
                {
                    var end = p.IndexOf('"', i + 1);
                    if (end < 0) throw new ArgumentException($"'{path}' is not a JSON path.", nameof(path));
                    segments.Add(p[(i + 1)..end]);
                    i = end + 1;
                }
                else
                {
                    var start = i;
                    while (i < p.Length && p[i] is not ('.' or '[')) i++;
                    if (i == start) throw new ArgumentException($"'{path}' is not a JSON path.", nameof(path));
                    segments.Add(p[start..i]);
                }
            }
            else if (p[i] == '[')
            {
                var end = p.IndexOf(']', i);
                if (end < 0 || !IsIndex(p[(i + 1)..end])) throw new ArgumentException($"'{path}' is not a JSON path.", nameof(path));
                segments.Add(p[(i + 1)..end]);
                i = end + 1;
            }
            else throw new ArgumentException($"'{path}' is not a JSON path.", nameof(path));
        }
        return segments;
    }

    // ---- returning, upsert ----------------------------------------------------------------------------------------------------

    /// <summary>Empty: PostgreSQL returns rows with the trailing <see cref="ReturningTrailing"/>.</summary>
    public string ReturningInline(IReadOnlyList<string> columns, RowImage image = RowImage.New)
    {
        CheckReturning(columns, image);
        return "";
    }

    /// <summary>
    /// <c> RETURNING a, …</c>: the written row (<see cref="RowImage.New"/>) or the removed row of a <c>DELETE</c>
    /// (<see cref="RowImage.Deleted"/>).
    /// </summary>
    /// <exception cref="NotSupportedException"><see cref="RowImage.Old"/>: the old values of an <c>UPDATE</c> need PostgreSQL 18
    /// (<c>RETURNING old.a</c>).</exception>
    public string ReturningTrailing(IReadOnlyList<string> columns, RowImage image = RowImage.New)
    {
        CheckReturning(columns, image);
        return " RETURNING " + string.Join(", ", columns);
    }

    private static void CheckReturning(IReadOnlyList<string> columns, RowImage image)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0) throw new ArgumentException("At least one column is required.", nameof(columns));
        if (!Enum.IsDefined(image)) throw new ArgumentOutOfRangeException(nameof(image), image, "Unknown row image.");
        if (image == RowImage.Old)
            throw new NotSupportedException(
                "ReturningInline/ReturningTrailing(RowImage.Old): the old values of an UPDATE need PostgreSQL 18 (RETURNING old.a); read them first.");
    }

    /// <summary>
    /// <c>INSERT INTO t (k…, c…) SELECT a.k…, a.c… FROM (source) AS a WHERE true ON CONFLICT (k…) DO UPDATE SET c = EXCLUDED.c, …;</c>
    /// (<c>DO NOTHING</c> without columns to update). The key columns need a unique index or primary key. <c>WHERE true</c> keeps
    /// <c>ON CONFLICT</c> from being read as the <c>ON</c> of a join in the source.
    /// </summary>
    public string Upsert(string table, IReadOnlyList<string> keyColumns, IReadOnlyList<string> columns, string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(table);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentException.ThrowIfNullOrEmpty(source);
        if (keyColumns.Count == 0) throw new ArgumentException("At least one key column is required.", nameof(keyColumns));
        var insert = keyColumns.Concat(columns).ToList();
        var action = columns.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", columns.Select(c => $"{Quote(c)} = EXCLUDED.{Quote(c)}"));
        return $"INSERT INTO {table} ({string.Join(", ", insert.Select(Quote))})\n" +
               $"SELECT {string.Join(", ", insert.Select(c => "a." + Quote(c)))} FROM ({source}) AS a WHERE true\n" +
               $"ON CONFLICT ({string.Join(", ", keyColumns.Select(Quote))}) {action};";
    }

    // ---- text, dates, functions -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Equality of the texts in the <c>"C"</c> collation: byte by byte, case, accents and trailing spaces counting (PostgreSQL
    /// keeps trailing spaces of <c>text</c>, so no length check is needed).
    /// </summary>
    public string BinaryEquals(string left, string right) => $"({BinaryCollate(left)} = {BinaryCollate(right)})";

    /// <summary><c>CAST(expr AS text) COLLATE "C"</c>: out of <c>citext</c> (whose functions ignore case) into byte order.</summary>
    public string BinaryCollate(string expr) => $"CAST({expr} AS text) COLLATE \"C\"";

    /// <summary><c>CAST(expr AS citext)</c>: back to the case-insensitive comparison of the database's text columns.</summary>
    public string DefaultCollate(string expr) => $"CAST({expr} AS citext)";

    /// <summary>The collation of <see cref="TextKey"/> (plan 2.7b, ruling Q1), created by <see cref="PrepareDatabaseAsync"/>.</summary>
    public const string TextKeyCollation = "data.xrmv_ci_as";

    /// <summary>
    /// <c>(replace(replace(rtrim(expr::text), 'ß', 'ss'), 'ẞ', 'ss') COLLATE data.xrmv_ci_as)</c> (plan 2.7b, ruling Q1 and its
    /// Task 2 amendment): out of <c>citext</c>, trailing spaces trimmed (SQL Server pads the shorter value), <c>ß</c> and <c>ẞ</c>
    /// made <c>ss</c> (ICU's level 2 keeps them apart, SQL Server's <c>_CI_AS</c> does not, and PostgreSQL 16 drops the strength of
    /// a collation with tailoring rules), compared at ICU level 2 (case, width and kana type ignored, accents kept). A column (qualified
    /// or not) or a parameter is cast as it is; any other expression is parenthesized first, so the cast applies to all of it.
    /// </summary>
    public string TextKey(string expr)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        var operand = SimpleOperand().IsMatch(expr) ? expr : $"({expr})";
        return $"(replace(replace(rtrim({operand}::text), '\u00DF', 'ss'), '\u1E9E', 'ss') COLLATE {TextKeyCollation})";
    }

    /// <summary>The smallest stored value in byte order, back to <c>citext</c> (plan 2.7b, Task 2 fix round 1).</summary>
    public string TextKeyMember(string expr) => $"CAST(MIN({BinaryCollate(expr)}) AS citext)";

    /// <summary><see cref="TextKeyMember"/> over the rows of the same text key (plan 2.7b, Task 2 fix round 1).</summary>
    public string TextKeyDistinctMember(string expr) => $"CAST(MIN({BinaryCollate(expr)}) OVER (PARTITION BY {TextKey(expr)}) AS citext)";

    /// <summary>A column (<c>a</c>, <c>t0."b"</c>) or a parameter (<c>@p0</c>), which <c>::</c> casts as a whole.</summary>
    [GeneratedRegex("""^(@\w+|("([^"]|"")+"|[A-Za-z_]\w*)(\.("([^"]|"")+"|[A-Za-z_]\w*))*)$""")]
    private static partial Regex SimpleOperand();

    /// <summary>
    /// From the catalog (plan 2.7b, ruling Q3): the unique indexes of the <c>data</c> schema without an expression, not backing a
    /// constraint, without included columns, with a <c>citext</c> key column; one row per key column.
    /// </summary>
    public string? PlainTextKeyIndexes => """
        SELECT t.relname, i.relname, a.attname, a.atttypid = 'citext'::regtype, pg_catalog.pg_get_expr(x.indpred, x.indrelid)
        FROM pg_catalog.pg_index x
        JOIN pg_catalog.pg_class i ON i.oid = x.indexrelid
        JOIN pg_catalog.pg_class t ON t.oid = x.indrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = t.relnamespace
        CROSS JOIN LATERAL unnest(CAST(x.indkey AS int2[])) WITH ORDINALITY AS k(attnum, ord)
        JOIN pg_catalog.pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
        WHERE n.nspname = 'data' AND x.indisunique AND NOT x.indisprimary AND x.indexprs IS NULL AND x.indnkeyatts = x.indnatts
          AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c WHERE c.conindid = x.indexrelid)
          AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute ta WHERE ta.attrelid = t.oid AND ta.attnum = ANY (CAST(x.indkey AS int2[]))
                      AND ta.atttypid = 'citext'::regtype)
        ORDER BY t.relname, i.relname, k.ord
        """;

    /// <summary>
    /// <c>(expr + (amount) * INTERVAL '1 unit')</c>, with SQL Server's unit names and abbreviations (a quarter is 3 months, a
    /// week 7 days; <c>dayofyear</c> and <c>weekday</c> add days, as <c>DATEADD</c> does). Month arithmetic clamps to the last day
    /// of the month, as <c>DATEADD</c> does.
    /// </summary>
    /// <exception cref="ArgumentException">A unit <c>DATEADD</c> does not have.</exception>
    public string AddInterval(string unit, string amount, string expr)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var interval = unit.ToLowerInvariant() switch
        {
            "year" or "yy" or "yyyy" => "1 year",
            "quarter" or "qq" or "q" => "3 months",
            "month" or "mm" or "m" => "1 month",
            "week" or "wk" or "ww" => "7 days",
            "day" or "dd" or "d" or "dayofyear" or "dy" or "y" or "weekday" or "dw" or "w" => "1 day",
            "hour" or "hh" => "1 hour",
            "minute" or "mi" or "n" => "1 minute",
            "second" or "ss" or "s" => "1 second",
            "millisecond" or "ms" => "1 millisecond",
            "microsecond" or "mcs" => "1 microsecond",
            _ => throw new ArgumentException($"'{unit}' is not a date part of DATEADD.", nameof(unit)),
        };
        return $"({expr} + ({amount}) * INTERVAL '{interval}')";
    }

    public string Concat(params string[] parts) => string.Join(" || ", SqlDialects.ConcatParts(parts));

    /// <summary>
    /// <c>string_agg(CAST(expr AS text), separator)</c> (SQL Server's <c>STRING_AGG</c> converts any value to text). A T-SQL
    /// <c>N'…'</c> separator loses its <c>N</c>: PostgreSQL reads <c>N'…'</c> as <c>character</c>, whose trailing spaces the
    /// conversion to text drops (<c>N', '</c> would join with <c>','</c>).
    /// </summary>
    public string StringAgg(string expr, string separator)
    {
        ArgumentException.ThrowIfNullOrEmpty(separator);
        var text = separator.StartsWith("N'", StringComparison.OrdinalIgnoreCase) ? separator[1..] : separator;
        return $"string_agg(CAST({expr} AS text), {text})";
    }

    public string IsNull(string a, string b) => $"COALESCE({a}, {b})";

    /// <summary><c>true</c>/<c>false</c>: a <c>boolean</c> takes no integer.</summary>
    public string BooleanLiteral(bool value) => value ? "true" : "false";

    /// <summary>
    /// <c>EXTRACT</c>; <c>week</c> is SQL Server's (us_english, weeks from Sunday, 1 January in week 1): the day of the year plus
    /// the weekday of 1 January, in whole weeks; <c>iso_week</c> is <c>EXTRACT(week …)</c>.
    /// </summary>
    public string DatePart(string unit, string expr)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        if (!DateParts.Contains(unit)) throw new ArgumentException($"'{unit}' is not a date part.", nameof(unit));
        return unit switch
        {
            "week" => $"(CAST(floor((EXTRACT(doy FROM {expr}) + EXTRACT(dow FROM date_trunc('year', {expr})) - 1) / 7) AS integer) + 1)",
            "iso_week" => $"CAST(EXTRACT(week FROM {expr}) AS integer)",
            _ => $"CAST(EXTRACT({unit} FROM {expr}) AS integer)",
        };
    }

    private static readonly HashSet<string> DateParts = new(StringComparer.Ordinal)
        { "year", "quarter", "month", "week", "iso_week", "day", "hour", "minute", "second" };

    // ---- query, computed column and rollup expressions (plan 2.7a, Task 7) ----------------------------------------------------

    public string RecursiveWith => "WITH RECURSIVE ";

    /// <summary>Empty: PostgreSQL has no recursion limit; the recursive CTEs bound their own depth.</summary>
    public string RecursionLimit(int levels) => "";

    public string Apply(string source, string alias, bool outer) =>
        outer ? $"LEFT JOIN LATERAL {source} AS {alias} ON true" : $"CROSS JOIN LATERAL {source} AS {alias}";

    /// <summary>
    /// A scalar subquery over one value, which reads <paramref name="expr"/> once: <c>(SELECT CASE WHEN … END FROM (VALUES (…)) AS tc(v))</c>.
    /// A whole type: the value truncated when it lies within the type's range (SQL Server truncates a decimal cast to an integer,
    /// PostgreSQL rounds); a decimal type: the value when it rounds to the scale within the precision, cast (half away from zero);
    /// a float: a plain cast.
    /// A type that is not a number (a date, a text): a plain cast, which a value of the column it comes from never fails.
    /// </summary>
    public string TryCast(string expr, string type)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        if (!IsNumberType(type)) return $"CAST({expr} AS {type})";
        var (kind, precision, scale) = NumberType(type);
        return kind switch
        {
            SqlColumnKind.Double => $"CAST({expr} AS double precision)",
            _ => $"(SELECT {NumberCase("tc.v", kind, precision, scale)} FROM (VALUES (CAST({expr} AS numeric))) AS tc(v))",
        };
    }

    /// <summary>
    /// A regular expression check of the text before its cast (a <c>CASE</c> evaluates its branches in order, so a text that is not
    /// a number never reaches a cast): spaces around, a sign, digits with an optional point (and an exponent for a float); then
    /// the range of <see cref="TryCast"/>; a float through the bootstrap's <c>data.try_float</c> (fix round 1: a plain cast errors
    /// past the largest double, where <c>TRY_CAST</c> gives null, and a constant is folded by the planner before any <c>CASE</c>).
    /// </summary>
    public string TryCastText(string expr, string type)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        var (kind, precision, scale) = NumberType(type);
        if (kind is SqlColumnKind.Int32 or SqlColumnKind.Int64) return TryCastInteger(expr, kind);
        const string number = "[-+]?([0-9]+[.]?[0-9]*|[.][0-9]+)";
        return kind == SqlColumnKind.Double
            ? $"(SELECT CASE WHEN tt.t ~ '^ *{number}([eE][-+]?[0-9]+)? *$' THEN \"data\".\"try_float\"(trim(tt.t)) END " +
              $"FROM (VALUES (CAST({expr} AS text))) AS tt(t))"
            : $"(SELECT CASE WHEN tt.t ~ '^ *{number} *$' THEN (SELECT {NumberCase("tc.v", kind, precision, scale)} " +
              $"FROM (VALUES (CAST(trim(tt.t) AS numeric))) AS tc(v)) END FROM (VALUES (CAST({expr} AS text))) AS tt(t))";
    }

    /// <summary>The <c>CASE</c> that gives the numeric <paramref name="v"/> as the type, or null out of its range.</summary>
    private static string NumberCase(string v, SqlColumnKind kind, int precision, int scale) => kind switch
    {
        SqlColumnKind.Int32 => $"CASE WHEN {v} > -2147483649 AND {v} < 2147483648 THEN CAST(trunc({v}) AS integer) END",
        SqlColumnKind.Int64 => $"CASE WHEN {v} > -9223372036854775809 AND {v} < 9223372036854775808 THEN CAST(trunc({v}) AS bigint) END",
        _ => $"CASE WHEN abs(round({v}, {Num(scale)})) < 1{new string('0', precision - scale)} THEN CAST({v} AS numeric({Num(precision)},{Num(scale)})) END",
    };

    private static bool IsNumberType(string type) =>
        type.Trim().ToLowerInvariant() is "int" or "integer" or "bigint" or "float" or "double precision"
        || System.Text.RegularExpressions.Regex.IsMatch(type.Trim(), @"^(?i:decimal|numeric)\(");

    /// <summary>The number type <paramref name="type"/> names: <c>int</c>/<c>integer</c>, <c>bigint</c>, <c>float</c>/<c>double precision</c>, <c>decimal(p, s)</c>/<c>numeric(p,s)</c>.</summary>
    private static (SqlColumnKind Kind, int Precision, int Scale) NumberType(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var t = type.Trim().ToLowerInvariant();
        switch (t)
        {
            case "int" or "integer": return (SqlColumnKind.Int32, 0, 0);
            case "bigint": return (SqlColumnKind.Int64, 0, 0);
            case "float" or "double precision": return (SqlColumnKind.Double, 0, 0);
        }
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^(decimal|numeric)\( *(\d+) *, *(\d+) *\)$");
        if (!m.Success) throw new NotSupportedException($"TryCast converts to a number type, not '{type}'.");
        var precision = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var scale = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (precision < 1 || scale > precision) throw new NotSupportedException($"'{type}' is not a valid decimal type.");
        return (SqlColumnKind.Decimal, precision, scale);
    }

    public string CountBig(string expr) => $"count({expr})";

    public string SumBigint(string expr, bool bigint = false) =>
        bigint ? $"CAST(SUM({expr}) AS bigint)" : $"CAST(SUM(CAST({expr} AS bigint)) AS bigint)";

    /// <summary><c>round(numeric, integer)</c>, half away from zero (the <c>double precision</c> one rounds half to even).</summary>
    public string Round(string expr, string digits) => $"round(CAST({expr} AS numeric), {Digits(digits)})";

    public string Truncate(string expr, string digits) => $"trunc(CAST({expr} AS numeric), {Digits(digits)})";

    /// <summary>A decimal count as the <c>integer</c> <c>round</c>/<c>trunc</c> take: a written number as is.</summary>
    private static string Digits(string digits) =>
        System.Text.RegularExpressions.Regex.IsMatch(digits, @"^-?\d{1,9}$") ? digits : $"CAST({digits} AS integer)";

    public string NaturalLog(string expr) => $"ln({expr})";

    public string TextLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Literal(value);
    }

    public string DatabaseText(string expr) => $"CAST({expr} AS citext)";

    public string Left(string text, string count) => $"left({text}, {Position(count)})";

    public string Right(string text, string count) => $"right({text}, {Position(count)})";

    public string Substring(string text, string start, string count) => $"substring({text}, {Position(start)}, {Position(count)})";

    /// <summary>A count or a position as the <c>integer</c> the text functions take: a written number as is, else cut to the <c>integer</c> range.</summary>
    private static string Position(string expr) =>
        System.Text.RegularExpressions.Regex.IsMatch(expr, @"^-?\d{1,9}$") || expr == "2147483647" ? expr
            : $"(SELECT CAST(CASE WHEN ps.v > 2147483647 THEN 2147483647 WHEN ps.v < -2147483648 THEN -2147483648 ELSE ps.v END AS integer) FROM (VALUES (CAST({expr} AS numeric))) AS ps(v))";

    /// <summary>Two bytes per character, as SQL Server's <c>nvarchar</c>: <c>2 * length(expr)</c>.</summary>
    public string DataLength(string expr) => $"(2 * length({expr}))";

    public string TextLength(string expr) => $"length(rtrim({expr}, ' '))";

    /// <summary>
    /// <c>strpos(text, find)</c>; from a start, <c>data.strpos(text, find, start)</c> (<see cref="BootstrapSql"/>): the position
    /// in the text after the start (a start below 1 is 1), 0 for an empty find, as <c>CHARINDEX</c>. The function compares as
    /// <c>text</c> (case-sensitive): its one caller searches a binary-collated find (Substitute). A function, not an inline
    /// scalar subquery, so that a Substitute of 100 instances keeps its SQL close to SQL Server's length (the bound of
    /// ComputedSql's MaxSqlLength holds the same formulas on both engines).
    /// </summary>
    public string IndexOf(string find, string text, string? start = null) => start is null
        ? $"strpos({text}, {find})"
        : $"data.strpos({text}, {find}, {start})";

    /// <summary><c>overlay</c> with an <c>integer</c> start and length, as <c>STUFF</c> takes them (past <c>int</c>: an error on both engines).</summary>
    public string Stuff(string text, string start, string length, string replacement) =>
        $"overlay({text} placing {replacement} from {Int(start)} for {Int(length)})";

    private static string Int(string expr) =>
        System.Text.RegularExpressions.Regex.IsMatch(expr, @"^-?\d{1,9}$") ? expr : $"CAST({expr} AS integer)";

    /// <summary>
    /// <c>chr</c> of a code of the Basic Multilingual Plane that is a character (not 0, not a surrogate), else null: as <c>NCHAR</c>
    /// under the organization's non-SC collation, null above 65535 (final review m1).
    /// </summary>
    public string CharFromCode(string code) =>
        $"(SELECT CASE WHEN ch.v BETWEEN 1 AND 65535 AND NOT ch.v BETWEEN 55296 AND 57343 THEN chr(CAST(ch.v AS integer)) END FROM (VALUES (CAST({code} AS numeric))) AS ch(v))";

    /// <summary>
    /// Boundaries crossed, as <c>DATEDIFF</c> counts them: the difference of the calendar parts (year, quarter, month), of the
    /// dates (day), of the weeks counted from a Sunday (week), or of the times cut to the unit (hour and below).
    /// </summary>
    public string DateDiff(string unit, string start, string end, bool big = false)
    {
        string Part(string part, string e) => $"EXTRACT({part} FROM {e})";
        string Epoch(string field, string e) => $"EXTRACT(epoch FROM date_trunc('{field}', {e}))";
        var diff = unit switch
        {
            "year" => $"{Part("year", end)} - {Part("year", start)}",
            "quarter" => $"({Part("year", end)} - {Part("year", start)}) * 4 + {Part("quarter", end)} - {Part("quarter", start)}",
            "month" => $"({Part("year", end)} - {Part("year", start)}) * 12 + {Part("month", end)} - {Part("month", start)}",
            "week" => $"floor((CAST({end} AS date) - DATE '1900-01-07') / 7.0) - floor((CAST({start} AS date) - DATE '1900-01-07') / 7.0)",
            "day" => $"CAST({end} AS date) - CAST({start} AS date)",
            "hour" => $"({Epoch("hour", end)} - {Epoch("hour", start)}) / 3600",
            "minute" => $"({Epoch("minute", end)} - {Epoch("minute", start)}) / 60",
            "second" => $"{Epoch("second", end)} - {Epoch("second", start)}",
            "millisecond" => $"({Epoch("milliseconds", end)} - {Epoch("milliseconds", start)}) * 1000",
            _ => throw new ArgumentException($"'{unit}' is not a unit of DateDiff.", nameof(unit)),
        };
        return $"CAST({diff} AS {(big ? "bigint" : "integer")})";
    }

    public string Weekday(string expr) => $"(CAST(EXTRACT(dow FROM {expr}) AS integer) + 1)";

    public string YearStart(string expr) => $"CAST(date_trunc('year', {expr}) AS date)";

    /// <summary>The spaces at both ends trimmed, each run of two or more inner spaces made one, back to <c>citext</c>.</summary>
    public string CollapseSpaces(string expr) => $"CAST(regexp_replace(trim(CAST({expr} AS text)), ' {{2,}}', ' ', 'g') AS citext)";

    public string MaxBoolean(string expr) => $"bool_or({expr})";

    /// <summary>
    /// SQL Server's <c>LIKE</c> syntax to PostgreSQL's (whose escape character is the backslash): a bracketed single character is
    /// that character, escaped when it is <c>%</c>, <c>_</c> or <c>\</c>; <c>[^]</c> is <c>_</c>; a backslash is escaped; <c>%</c>
    /// and <c>_</c> stay wildcards.
    /// </summary>
    /// <exception cref="NotSupportedException">A set of no character or of more than one (<see cref="Like"/> matches them).</exception>
    /// <exception cref="XrmdFaultException">A range (ruling P15).</exception>
    public string LikePattern(string pattern) =>
        LikeText(ParseLike(pattern)) ?? throw new NotSupportedException(
            $"The like pattern '{pattern}' has a character set, which PostgreSQL's LIKE does not have; Like matches it.");

    /// <summary>
    /// <c>LIKE</c> (<see cref="LikePattern"/>) when it can say the pattern; otherwise <c>~*</c>, the case-insensitive regular
    /// expression match (accents kept: under the ICU root collation <c>é</c> and <c>É</c> are one letter, <c>e</c> another),
    /// anchored to the whole value, whose <c>.</c> matches a line break as <c>_</c> does.
    /// </summary>
    public string Like(string expr, string pattern, bool negate, Func<string, string> parameter)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        ArgumentNullException.ThrowIfNull(parameter);
        var tokens = ParseLike(pattern);
        return LikeText(tokens) is { } like
            ? $"{expr} {(negate ? "NOT LIKE" : "LIKE")} {parameter(like)}"
            : $"{expr} {(negate ? "!~*" : "~*")} {parameter(LikeRegex(tokens))}";
    }

    /// <summary>The feature name of ruling P15 (a pattern with a range), in the text of the 501.</summary>
    public const string LikeRangesFeature = "Like patterns with character ranges";

    private enum LikeKind { Char, One, Many, Set }

    /// <summary>A piece of a <c>LIKE</c> pattern: a character, <c>_</c>, <c>%</c> or a set (its members, negated or not).</summary>
    private readonly record struct LikeToken(LikeKind Kind, char Char = '\0', string Members = "", bool Negated = false);

    /// <summary>
    /// The pieces of a pattern as SQL Server 2022 reads it (checked on <c>SQL_Latin1_General_CP1_CI_AS</c>): a set closes at the
    /// first <c>]</c> after <c>[</c> (or <c>[^</c>), and runs to the end of the pattern without one; a <c>^</c> right after
    /// <c>[^</c> is dropped; inside a set <c>%</c>, <c>_</c> and <c>[</c> are themselves, and so is a <c>-</c> at either end.
    /// </summary>
    /// <exception cref="XrmdFaultException">501 <see cref="ErrorCodes.Unexpected"/>: a <c>-</c> between two members, a range (ruling P15).</exception>
    private static List<LikeToken> ParseLike(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var tokens = new List<LikeToken>(pattern.Length);
        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '%':
                    tokens.Add(new LikeToken(LikeKind.Many));
                    break;
                case '_':
                    tokens.Add(new LikeToken(LikeKind.One));
                    break;
                case '[':
                    var first = i + 1;
                    var negated = first < pattern.Length && pattern[first] == '^';
                    if (negated && ++first < pattern.Length && pattern[first] == '^') first++;
                    var close = first < pattern.Length ? pattern.IndexOf(']', first) : -1;
                    var end = close < 0 ? pattern.Length : close;
                    var members = pattern[first..end];
                    if (members.Length > 2 && members.AsSpan(1, members.Length - 2).Contains('-'))
                        throw ProviderGuard.Unavailable(LikeRangesFeature);
                    tokens.Add(new LikeToken(LikeKind.Set, Members: members, Negated: negated));
                    i = end;
                    break;
                default:
                    tokens.Add(new LikeToken(LikeKind.Char, pattern[i]));
                    break;
            }
        }
        return tokens;
    }

    /// <summary>The pattern as PostgreSQL's <c>LIKE</c> reads it, or null when a set is not one character (nor <c>[^]</c>).</summary>
    private static string? LikeText(List<LikeToken> tokens)
    {
        var sb = new StringBuilder(tokens.Count + 8);
        foreach (var t in tokens)
        {
            switch (t)
            {
                case { Kind: LikeKind.Many }:
                    sb.Append('%');
                    break;
                case { Kind: LikeKind.One } or { Kind: LikeKind.Set, Negated: true, Members.Length: 0 }:
                    sb.Append('_');
                    break;
                case { Kind: LikeKind.Char }:
                    Literal(t.Char);
                    break;
                case { Kind: LikeKind.Set, Negated: false, Members.Length: 1 }:
                    Literal(t.Members[0]);
                    break;
                default:
                    return null;
            }
        }
        return sb.ToString();

        void Literal(char c)
        {
            if (c is '%' or '_' or '\\') sb.Append('\\');
            sb.Append(c);
        }
    }

    /// <summary>
    /// The pattern as an anchored regular expression (an advanced one, PostgreSQL's default): <c>%</c> is <c>.*</c>, <c>_</c>
    /// <c>.</c>, a set a bracket expression with its special characters escaped, <c>[]</c> the empty lookahead <c>(?!)</c> that
    /// never matches, every other character itself.
    /// </summary>
    private static string LikeRegex(List<LikeToken> tokens)
    {
        var sb = new StringBuilder(tokens.Count * 2 + 2).Append('^');
        foreach (var t in tokens)
        {
            switch (t.Kind)
            {
                case LikeKind.Many:
                    sb.Append(".*");
                    break;
                case LikeKind.One:
                    sb.Append('.');
                    break;
                case LikeKind.Char:
                    if (@"\.^$|?*+()[]{}".Contains(t.Char)) sb.Append('\\');
                    sb.Append(t.Char);
                    break;
                case LikeKind.Set when t.Members.Length == 0:
                    sb.Append(t.Negated ? "." : "(?!)");
                    break;
                default:
                    sb.Append('[');
                    if (t.Negated) sb.Append('^');
                    foreach (var c in t.Members)
                    {
                        if (c is '\\' or ']' or '[' or '^' or '-') sb.Append('\\');
                        sb.Append(c);
                    }
                    sb.Append(']');
                    break;
            }
        }
        return sb.Append('$').ToString();
    }

    public string LastDateTime => "CAST('9999-12-31T23:59:59.999999' AS timestamptz)";

    public string EscapeLike(string expr) =>
        $"replace(replace(replace(CAST({expr} AS text), '\\', '\\\\'), '%', '\\%'), '_', '\\_')";

    public string ToText(string expr, int length) => $"CAST({expr} AS text)";

    public string UpdateFrom(string table, string alias, IReadOnlyList<(string Column, string Value)> set, string source, string on,
        string? where, IReadOnlyList<Returned> returning)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(returning);
        return $"UPDATE {table} AS {alias} SET {string.Join(", ", set.Select(x => $"{x.Column} = {x.Value}"))}\n" +
               $"FROM {source}\nWHERE {on}" + (where is null ? "" : $" AND ({where})") +
               $"\nRETURNING {string.Join(", ", returning.Select(r => r.Column ? $"{alias}.{r.Sql}" : r.Sql))}";
    }


    /// <summary>
    /// A nested <c>CASE</c>: the text must be an optionally signed run of digits between optional spaces (as SQL Server's
    /// <c>TRY_CAST</c> accepts), then its <c>numeric</c> value within the type's range, before the cast; a <c>CASE</c> evaluates its
    /// branches in order, so a text that is not a number never reaches a cast.
    /// </summary>
    public string TryCastInteger(string expr, SqlColumnKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        var (type, min, max) = kind switch
        {
            SqlColumnKind.Int32 => ("integer", "-2147483648", "2147483647"),
            SqlColumnKind.Int64 => ("bigint", "-9223372036854775808", "9223372036854775807"),
            _ => throw new NotSupportedException($"TryCastInteger converts to an integer kind, not {kind}."),
        };
        var text = $"CAST({expr} AS text)";
        return $"(CASE WHEN {text} ~ '^\\s*[-+]?[0-9]+\\s*$' THEN CASE WHEN CAST(trim({text}) AS numeric) BETWEEN {min} AND {max} "
               + $"THEN CAST(CAST(trim({text}) AS numeric) AS {type}) END END)";
    }

    // ---- row versions and change tracking (plan 2.7a, Task 7; ruling P4) -----------------------------------------------------

    /// <summary>The expression itself: the row version columns are <c>bigint</c> (ruling P4).</summary>
    public string RowVersionValue(string expr)
    {
        ArgumentException.ThrowIfNullOrEmpty(expr);
        return expr;
    }

    /// <summary>
    /// The bound of <see cref="MinActiveRowVersionAsync"/> as an expression: the organization's function
    /// <c>data.row_version_bound()</c> (<see cref="BootstrapSql"/>), which reads the two values in order (fix round 1, review
    /// Minor 2: one <c>LEAST</c> statement did not order them and could give a value above the bound).
    /// </summary>
    public string RowVersionStart => "\"data\".\"row_version_bound\"()";

    /// <summary>
    /// The bound of ruling P4's sequence (plan 2.7a, Task 7), as <c>MIN_ACTIVE_ROWVERSION()</c>, read by
    /// <c>data.row_version_bound()</c>: <c>B</c>, the value of the sequence's next draw, is read first; then the lowest key of the
    /// locks the transactions in flight took before their first draw (<c>data.next_versionnumber()</c> reads the next value,
    /// locks it, reads it again and draws, so each such key is at most the transaction's lowest version and at least every bound
    /// a reader gave before the lock); the bound is the lower of the two. A transaction whose lock the second read misses took it
    /// after that read, so it draws after the first one: at or above <c>B</c>. Every version below the bound is committed or
    /// rolled back, the session's own transaction counts, as on SQL Server, and the bound never goes down. Nothing waits.
    /// </summary>
    public async Task<long> MinActiveRowVersionAsync(DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command("SELECT " + RowVersionStart);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>The organization's version function: it takes the lock <see cref="MinActiveRowVersionAsync"/> reads, then draws.</summary>
    public string? NextRowVersion => "\"data\".\"next_versionnumber\"()";

    public string NextSequenceValue(string sequence)
    {
        ArgumentException.ThrowIfNullOrEmpty(sequence);
        return $"nextval({Literal(sequence)})";
    }

    // ---- errors (ruling P9) ----------------------------------------------------------------------------------------------------

    public DbErrorKind Classify(DbException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is PostgresException pg)
        {
            return pg.SqlState switch
            {
                "23505" => DbErrorKind.UniqueViolation,
                "23503" => DbErrorKind.ForeignKeyViolation,
                "40P01" or "40001" => DbErrorKind.Deadlock,
                "57014" => DbErrorKind.Timeout,
                "55P03" => DbErrorKind.LockTimeout,
                // 08xxx: connection exceptions; 57P01/57P02: the server terminated the connection.
                var s when s.StartsWith("08", StringComparison.Ordinal) || s is "57P01" or "57P02" => DbErrorKind.ConnectionLost,
                "28P01" or "28000" => DbErrorKind.LoginFailed,
                "42P01" => DbErrorKind.UndefinedObject,
                // Cannot connect now (starting up), too many connections, the database does not exist (SQL Server 4060).
                "57P03" or "53300" or "3D000" => DbErrorKind.Unavailable,
                "22003" => DbErrorKind.NumericOverflow,
                _ => DbErrorKind.Other,
            };
        }
        if (ex is not NpgsqlException) return DbErrorKind.Other;
        // Client-side failures: the command timeout (Npgsql cancels and reports a TimeoutException), or a broken socket.
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is TimeoutException) return DbErrorKind.Timeout;
            if (inner is IOException or SocketException) return DbErrorKind.ConnectionLost;
        }
        return DbErrorKind.Other;
    }

    /// <summary>
    /// A unique violation of a primary key: the constraint xrmverse names <c>PK_&lt;table&gt;</c>, or PostgreSQL's default
    /// <c>&lt;table&gt;_pkey</c>.
    /// </summary>
    public bool IsPrimaryKeyViolation(DbException ex) =>
        ex is PostgresException { SqlState: "23505", ConstraintName: { } name }
        && (name.StartsWith("PK_", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_pkey", StringComparison.Ordinal));

    // ---- DDL and catalog checks (plan 2.7a, Task 4) -----------------------------------------------------------------------------

    /// <summary>PostgreSQL's identifier length (<c>NAMEDATALEN</c> - 1): a longer name is silently cut by the server.</summary>
    public const int MaxIdentifierBytes = 63;

    /// <summary>The bytes of a long name that <see cref="ObjectName"/> keeps before <c>_</c> and its hash.</summary>
    private const int KeptBytes = MaxIdentifierBytes - 9;

    /// <summary>
    /// The name, or for a name over <see cref="MaxIdentifierBytes"/> bytes its first 54 bytes (whole characters), <c>_</c> and the
    /// first 8 lowercase hex digits of the SHA-256 of its UTF-8 bytes (plan 2.7a, Task 4): the server would cut it at 63 bytes
    /// itself, so two names sharing their first 63 bytes would collide.
    /// </summary>
    public string ObjectName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length <= MaxIdentifierBytes) return name;
        var kept = new StringBuilder();
        var length = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (length + rune.Utf8SequenceLength > KeptBytes) break;
            length += rune.Utf8SequenceLength;
            kept.Append(rune.ToString());
        }
        return kept.Append('_').Append(Convert.ToHexStringLower(SHA256.HashData(bytes))[..8]).ToString();
    }

    /// <summary>
    /// Refuses a name over <see cref="MaxIdentifierBytes"/> bytes (plan 2.7a, Task 4 fix round 1): PostgreSQL would store it cut,
    /// with a notice only, and the catalog checks and result aliases would then no longer match it.
    /// </summary>
    public void EnsureIdentifier(string name, string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (Encoding.UTF8.GetByteCount(name) > MaxIdentifierBytes)
            throw new XrmdValidationException(
                $"The {kind} name '{name}' is longer than the {MaxIdentifierBytes} bytes PostgreSQL allows for a table or column name.");
    }

    public string MigrationFolder => "PostgreSql";

    public bool FailedStatementAbortsTransaction => true;

    /// <summary>
    /// <c>CREATE TRIGGER "trg_&lt;table&gt;_versionnumber" BEFORE INSERT OR UPDATE … EXECUTE FUNCTION &lt;schema&gt;.set_versionnumber()</c>
    /// (ruling P4): the function, created by the organization's bootstrap (<c>SchemaMigrator</c>), draws the row's version from
    /// the organization's one sequence <c>&lt;schema&gt;.versionnumber_seq</c>, like a database's <c>rowversion</c> counter.
    /// </summary>
    public string RowVersionTrigger(string schema, string table) =>
        $"CREATE TRIGGER {Quote(ObjectName($"trg_{table}_versionnumber"))} BEFORE INSERT OR UPDATE ON {Name(schema, table)} " +
        $"FOR EACH ROW EXECUTE FUNCTION {Name(schema, "set_versionnumber")}();";

    /// <summary><c>"c" bigint NOT NULL</c> (ruling P4): the trigger sets it before the constraint is checked.</summary>
    public string RowVersionColumn(string column) => $"{Quote(column)} {RowVersionType} NOT NULL";

    public string TableExists(string schema, string table) =>
        $"EXISTS (SELECT 1 FROM pg_catalog.pg_tables WHERE schemaname = {Literal(schema)} AND tablename = {Literal(table)})";

    public string ColumnExists(string schema, string table, string column) =>
        "EXISTS (SELECT 1 FROM information_schema.columns WHERE " +
        $"table_schema = {Literal(schema)} AND table_name = {Literal(table)} AND column_name = {Literal(column)})";

    public string IndexExists(string schema, string table, string index) =>
        "EXISTS (SELECT 1 FROM pg_catalog.pg_indexes WHERE " +
        $"schemaname = {Literal(schema)} AND tablename = {Literal(table)} AND indexname = {Literal(ObjectName(index))})";

    /// <summary>A <c>DO</c> block: PostgreSQL has no conditional statement outside PL/pgSQL.</summary>
    public string When(string condition, string statement) =>
        $"DO $xrmd$ BEGIN IF {condition} THEN EXECUTE {Literal(statement)}; END IF; END $xrmd$;";

    public string CreateSchemaIfMissing(string schema) => $"CREATE SCHEMA IF NOT EXISTS {Quote(schema)};";

    /// <summary>
    /// <c>CREATE [UNIQUE] INDEX IF NOT EXISTS</c>. An index name is unique in its schema on PostgreSQL (in its table on SQL
    /// Server): the names xrmverse builds carry their table's name.
    /// </summary>
    public string CreateIndexIfMissing(string schema, string table, string index, string columns, bool unique = false, string? filter = null) =>
        $"CREATE {(unique ? "UNIQUE " : "")}INDEX IF NOT EXISTS {Quote(ObjectName(index))} ON {Name(schema, table)} ({columns})" +
        $"{(filter is null ? "" : " WHERE " + filter)};";

    public string DropIndexIfExists(string schema, string table, string index) => $"DROP INDEX IF EXISTS {Name(schema, ObjectName(index))};";

    public string AddColumns(string table, IReadOnlyList<string> definitions) =>
        $"ALTER TABLE {table} {string.Join(", ", definitions.Select(d => "ADD COLUMN " + d))};";

    public string AddColumnIfMissing(string schema, string table, string column, string type) =>
        $"ALTER TABLE {Name(schema, table)} ADD COLUMN IF NOT EXISTS {Quote(column)} {type} NULL;";

    public string DropColumns(string table, IReadOnlyList<string> columns) =>
        $"ALTER TABLE {table} {string.Join(", ", columns.Select(c => "DROP COLUMN " + c))};";

    /// <summary><c>ALTER COLUMN … TYPE</c>: the nullability is kept (the data columns are nullable).</summary>
    public string AlterColumnType(string table, string column, string type) => $"ALTER TABLE {table} ALTER COLUMN {column} TYPE {type};";

    public string RenameColumn(string schema, string table, string column, string newName) =>
        $"ALTER TABLE {Name(schema, table)} RENAME COLUMN {Quote(column)} TO {Quote(newName)};";

    public string CreateSequenceIfMissing(string schema, string name, long start) =>
        CreateSchemaIfMissing(schema) + "\n" + SequenceDefinition("CREATE SEQUENCE IF NOT EXISTS", schema, name, start) + ";";

    public string CreateSequence(string schema, string name, long start) => SequenceDefinition("CREATE SEQUENCE", schema, name, start) + ";";

    public string DropSequenceIfExists(string schema, string name) => $"DROP SEQUENCE IF EXISTS {Name(schema, name)};";

    /// <summary>From <c>pg_sequences</c>, whose <c>last_value</c> is null until the first draw.</summary>
    public string ReadSequence(string schema, string name) =>
        "SELECT start_value, CASE WHEN last_value IS NULL THEN start_value ELSE last_value + increment_by END " +
        $"FROM pg_catalog.pg_sequences WHERE schemaname = {Literal(schema)} AND sequencename = {Literal(name)};";

    /// <summary>The lowest <c>server_version_num</c> xrmverse runs on (Global Constraints: PostgreSQL 16).</summary>
    public const int MinServerVersionNum = 160000;

    /// <summary>
    /// The organization bootstrap (ruling P4, Global Constraints), in one transaction serialized by an advisory lock: the server
    /// version check, the schemas <c>meta</c>, <c>data</c> and <c>auth</c>, the <c>citext</c> extension, the collation
    /// <c>data.xrmv_ci_as</c> of <see cref="TextKey"/> (plan 2.7b, ruling Q1), the sequence
    /// <c>data.versionnumber_seq</c> and the function <c>data.set_versionnumber()</c> that every data table's trigger calls
    /// (<see cref="RowVersionTrigger"/>), with its helpers <c>data.next_versionnumber()</c> and <c>data.row_version_bound()</c>
    /// (change tracking's bound), <c>data.try_float(text)</c> and <c>data.strpos(text, text, numeric)</c>. No parameter is sent. Then the data source's types are reloaded (Npgsql loads them once,
    /// and sends string parameters as <c>citext</c>) and its idle connections are dropped, whoever created the extension.
    /// </summary>
    public async Task PrepareDatabaseAsync(DbConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not NpgsqlConnection npgsql)
            throw new ArgumentException($"{connection.GetType().FullName} is not a PostgreSQL connection.", nameof(connection));

        await using (var tx = await npgsql.BeginTransactionAsync(ct))
        {
            await using (var check = new NpgsqlCommand(
                             "SELECT pg_advisory_xact_lock(hashtextextended('xrmd:bootstrap', 0)), current_setting('server_version_num')::integer, " +
                             "current_setting('server_version')", npgsql, tx))
            await using (var rd = await check.ExecuteReaderAsync(ct))
            {
                await rd.ReadAsync(ct);
                EnsureSupportedVersion(rd.GetInt32(1), rd.GetString(2));
            }
            await using (var bootstrap = new NpgsqlCommand(BootstrapSql, npgsql, tx))
                await bootstrap.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Always (fix round 1, review Minor 1): whether this transaction created citext or another process did, the type cache
        // of this process's data source may predate it (a connection opened before the extension existed).
        await npgsql.ReloadTypesAsync(ct);
        NpgsqlConnection.ClearPool(npgsql);
    }

    /// <summary>The Global Constraints check of the server version (<c>server_version_num</c>, <c>server_version</c>).</summary>
    /// <exception cref="InvalidOperationException">The server is older than PostgreSQL 16.</exception>
    public static void EnsureSupportedVersion(int serverVersionNum, string serverVersion)
    {
        if (serverVersionNum < MinServerVersionNum)
            throw new InvalidOperationException($"xrmverse needs PostgreSQL 16 or later (found {serverVersion}).");
    }

    /// <summary>The statements of <see cref="PrepareDatabaseAsync"/> after the version check; each is a no-op when done before.</summary>
    private const string BootstrapSql = """
        CREATE SCHEMA IF NOT EXISTS "meta";
        CREATE SCHEMA IF NOT EXISTS "data";
        CREATE SCHEMA IF NOT EXISTS "auth";
        CREATE EXTENSION IF NOT EXISTS citext;
        -- Plan 2.7b, ruling Q1 (#299): the collation of TextKey, SQL Server's _CI_AS for keys and equality (case, width and kana
        -- type ignored, accents kept). Nondeterministic, so not the columns' own (PostgreSQL 16 refuses LIKE on it).
        CREATE COLLATION IF NOT EXISTS data.xrmv_ci_as (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
        CREATE SEQUENCE IF NOT EXISTS "data"."versionnumber_seq" AS bigint;
        CREATE OR REPLACE FUNCTION "data"."next_versionnumber"() RETURNS bigint LANGUAGE plpgsql AS $xrmd$
        DECLARE
            r bigint;
        BEGIN
            -- Plan 2.7a, Task 7 (MIN_ACTIVE_ROWVERSION): before its first draw a transaction holds, until it ends, a shared
            -- advisory lock whose two keys carry the value the sequence's next draw takes now (at most its own first version;
            -- key 1 = high 32 bits + 2^30, a mark no other lock of xrmverse uses; key 2 = low 32 bits).
            -- Fix round 1 (review Minor 1): the peek and the lock happen inside the gate (1073741823, 0), held shared at session
            -- level for those two steps only; data.row_version_bound() reads under the same gate held exclusive, so a reader never
            -- passes a value a writer has peeked and not yet published (the bound never goes down). The gate is given back on
            -- every error, the cancel of a statement included.
            IF current_setting('xrmd.version_draw', true) IS DISTINCT FROM 'on' THEN
                BEGIN
                    PERFORM pg_advisory_lock_shared(1073741823, 0);
                    SELECT CASE WHEN is_called THEN last_value + 1 ELSE last_value END INTO r FROM "data"."versionnumber_seq";
                    PERFORM pg_advisory_xact_lock_shared(CAST((r >> 32) + 1073741824 AS integer),
                        CAST((r & 4294967295) - CASE WHEN (r & 4294967295) >= 2147483648 THEN 4294967296 ELSE 0 END AS integer));
                    PERFORM pg_advisory_unlock_shared(1073741823, 0);
                EXCEPTION WHEN OTHERS OR query_canceled THEN
                    IF EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid = pg_backend_pid() AND locktype = 'advisory'
                               AND classid = 1073741823 AND objid = 0 AND objsubid = 2 AND granted) THEN
                        PERFORM pg_advisory_unlock_shared(1073741823, 0);
                    END IF;
                    RAISE;
                END;
                PERFORM set_config('xrmd.version_draw', 'on', true);
            END IF;
            RETURN nextval('"data"."versionnumber_seq"');
        END
        $xrmd$;
        -- MIN_ACTIVE_ROWVERSION (fix round 1, review Minor 1, 2): under the gate of data.next_versionnumber() held exclusive, the
        -- sequence's next value first, then the lowest key published by a transaction in flight in this database, in that order
        -- (one statement would not order them). The gate is a transaction lock of a subtransaction that the function rolls back
        -- to give it back at once (so an error cannot leave it held).
        CREATE OR REPLACE FUNCTION "data"."row_version_bound"() RETURNS bigint LANGUAGE plpgsql VOLATILE AS $xrmd$
        DECLARE
            bound bigint;
            lowest bigint;
        BEGIN
            BEGIN
                PERFORM pg_advisory_xact_lock(1073741823, 0);
                SELECT CASE WHEN is_called THEN last_value + 1 ELSE last_value END INTO bound FROM "data"."versionnumber_seq";
                SELECT min(((CAST(classid AS bigint) - 1073741824) << 32) | CAST(objid AS bigint)) INTO lowest FROM pg_catalog.pg_locks
                    WHERE locktype = 'advisory' AND objsubid = 2 AND classid >= 1073741824 AND classid < 2147483648
                    AND database = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database());
                RAISE EXCEPTION USING ERRCODE = 'XRV01';
            EXCEPTION WHEN SQLSTATE 'XRV01' THEN
                NULL;
            END;
            RETURN LEAST(bound, lowest);
        END
        $xrmd$;
        -- TRY_CAST(text AS float) (fix round 1, review Minor 4): null past the largest double, 0 below the smallest one, as SQL
        -- Server; no error, even when the planner folds a constant (plpgsql is not inlined).
        CREATE OR REPLACE FUNCTION "data"."try_float"(t text) RETURNS double precision
            LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE AS $xrmd$
        BEGIN
            IF pg_input_is_valid(t, 'double precision') THEN
                RETURN CAST(t AS double precision);
            END IF;
            IF pg_input_is_valid(t, 'numeric') THEN
                RETURN CASE WHEN abs(CAST(t AS numeric)) < 1 THEN 0 END;
            END IF;
            RETURN NULL;
        END
        $xrmd$;
        CREATE OR REPLACE FUNCTION "data"."strpos"(txt text, find text, start numeric) RETURNS integer
            LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $xrmd$
            SELECT CASE WHEN find = '' THEN 0
                ELSE COALESCE(NULLIF(strpos(substr(txt, CAST(GREATEST(1, LEAST(start, 2147483647)) AS integer)), find), 0)
                    + CAST(GREATEST(1, LEAST(start, 2147483647)) AS integer) - 1, 0) END
            $xrmd$;
        CREATE OR REPLACE FUNCTION "data"."set_versionnumber"() RETURNS trigger LANGUAGE plpgsql AS $xrmd$
        BEGIN
            NEW.versionnumber := "data"."next_versionnumber"();
            RETURN NEW;
        END
        $xrmd$;
        """;

    private string Name(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    private string SequenceDefinition(string create, string schema, string name, long start) =>
        $"{create} {Name(schema, name)} AS bigint START WITH {Num(start)} INCREMENT BY 1 MINVALUE 0 CACHE 1";

    // ---- parameters ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Dates go as UTC <c>timestamptz</c> (Global Constraints; Npgsql maps <see cref="DbType.DateTime2"/> to <c>timestamp</c> and
    /// refuses a non-UTC <see cref="DateTime"/> for <c>timestamptz</c>): an unspecified kind is UTC (the values xrmverse stores
    /// are UTC), a local one is converted (a PostgreSQL-only normalisation: SqlClient stores a local value's ticks as they are).
    /// A <see cref="DateTimeOffset"/> is sent at offset 0. Strings go as <c>citext</c>.
    /// </summary>
    public void ConfigureParameter(DbParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter is not NpgsqlParameter p) return;
        switch (p.DbType)
        {
            case DbType.DateTime or DbType.DateTime2:
                var value = p.Value;
                p.NpgsqlDbType = NpgsqlDbType.TimestampTz;
                p.Value = value is DateTime d
                    ? d.Kind switch
                    {
                        DateTimeKind.Utc => d,
                        DateTimeKind.Local => d.ToUniversalTime(),
                        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
                    }
                    : value;
                break;
            case DbType.DateTimeOffset when p.Value is DateTimeOffset o && o.Offset != TimeSpan.Zero:
                p.Value = o.ToUniversalTime();
                break;
            // Fix round 1 (review I2): citext, so that `col = @p`, IN and LIKE compare like SQL Server's _CI_AS columns (a text
            // parameter turns a citext comparison into a case-sensitive text one, which also skips the citext indexes). Needs the
            // extension before Npgsql loads the database's types.
            case DbType.String or DbType.StringFixedLength or DbType.AnsiString or DbType.AnsiStringFixedLength:
                p.NpgsqlDbType = NpgsqlDbType.Citext;
                break;
        }
    }

    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
}
