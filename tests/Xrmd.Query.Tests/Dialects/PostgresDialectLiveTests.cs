using System.Data;
using System.Data.Common;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Query.Tests.Dialects;

/// <summary>A PostgreSQL 16 container for <see cref="PostgresDialectLiveTests"/>, with the <c>citext</c> extension.</summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public NpgsqlConnectionFactory Factory { get; private set; } = null!;

    /// <summary>The container's connection string, as given (not normalized).</summary>
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using (var conn = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS citext", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        Factory = new NpgsqlConnectionFactory(_container.GetConnectionString());
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// Plan 2.7a, Task 3: a sanity run of the dialect's fragments on a real PostgreSQL 16 (advisory locks, JSON, a row-locking
/// clause, returning, upsert, dates, error classes). The integration suite on PostgreSQL is Tasks 5–7's.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class PostgresDialectLiveTests(PostgresContainerFixture fx) : IClassFixture<PostgresContainerFixture>
{
    private static readonly ISqlDialect D = PostgresDialect.Instance;

    private async Task<DataSession> OpenAsync(bool transaction = false)
    {
        var conn = await fx.Factory.OpenAsync();
        return new DataSession(conn, transaction ? await conn.BeginTransactionAsync() : null);
    }

    private static async Task<object?> ScalarAsync(DataSession s, string sql, params (string Name, DbType Type, object? Value)[] ps)
    {
        await using var cmd = s.Command(sql);
        foreach (var (name, type, value) in ps) cmd.AddParameter(name, type, value);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Sessions_use_the_postgresql_dialect_and_run_in_utc()
    {
        var s = await OpenAsync();
        await using var _ = s.Connection;
        s.Dialect.Should().BeSameAs(D);
        (await ScalarAsync(s, "SHOW TimeZone")).Should().Be("UTC");
    }

    [Fact]
    public async Task Application_locks_are_advisory_locks()
    {
        var a = await OpenAsync();
        var b = await OpenAsync();
        await using var ca = a.Connection;
        await using var cb = b.Connection;

        await D.AcquireAppLockAsync(a, "xrmd-live", session: true, CancellationToken.None);
        (await D.HoldsAppLockAsync(a, "xrmd-live", CancellationToken.None)).Should().BeTrue();
        (await D.HoldsAppLockAsync(b, "xrmd-live", CancellationToken.None)).Should().BeFalse();
        (await D.TryAcquireAppLockAsync(b, "xrmd-live", session: true, CancellationToken.None)).Should().BeFalse();
        var timed = () => D.AcquireAppLockAsync(b, "xrmd-live", session: true, TimeSpan.FromMilliseconds(150), CancellationToken.None);
        (await timed.Should().ThrowAsync<AppLockException>()).Which.Resource.Should().Be("xrmd-live");

        await D.ReleaseAppLockAsync(a, "xrmd-live", session: true, CancellationToken.None);
        (await D.HoldsAppLockAsync(a, "xrmd-live", CancellationToken.None)).Should().BeFalse();
        await D.AcquireAppLockAsync(b, "xrmd-live", session: true, TimeSpan.FromSeconds(5), CancellationToken.None);
        await D.ReleaseAppLockAsync(b, "xrmd-live", session: true, CancellationToken.None);

        // A transaction owner needs a transaction, and keeps the lock until its end.
        var outside = () => D.AcquireAppLockAsync(a, "xrmd-tx", session: false, CancellationToken.None);
        await outside.Should().ThrowAsync<AppLockException>();
        a.Transaction = await a.Connection.BeginTransactionAsync();
        await D.AcquireAppLockAsync(a, "xrmd-tx", session: false, CancellationToken.None);
        (await D.TryAcquireAppLockAsync(b, "xrmd-tx", session: true, CancellationToken.None)).Should().BeFalse();
        await a.Transaction.CommitAsync();
        (await D.TryAcquireAppLockAsync(b, "xrmd-tx", session: true, CancellationToken.None)).Should().BeTrue();
        await D.ReleaseAppLockAsync(b, "xrmd-tx", session: true, CancellationToken.None);
    }

    [Fact]
    public async Task The_key_lock_serializes_transactions_and_comes_first_in_the_command()
    {
        var a = await OpenAsync(transaction: true);
        var b = await OpenAsync(transaction: true);
        await using var ca = a.Connection;
        await using var cb = b.Connection;

        // Fix round 1 (I1): the key lock is a command of its own; the read after it gets its own result.
        await D.LockKeyAsync(a, "Account", "ACME", CancellationToken.None);
        (await ScalarAsync(a, "SELECT 42")).Should().Be(42);
        // Another transaction on the same key in another casing waits (its try fails) until the first ends; the application
        // lock of the same text (seed 0) is another key.
        var hash = "hashtextextended(@k, 1)";
        (await ScalarAsync(b, $"SELECT pg_try_advisory_xact_lock({hash})", ("@k", DbType.String, PostgresDialect.KeyLockText("account", "acme"))))
            .Should().Be(false);
        (await D.TryAcquireAppLockAsync(b, "account:acme", session: false, CancellationToken.None)).Should().BeTrue();
        await a.Transaction!.CommitAsync();
        (await ScalarAsync(b, $"SELECT pg_try_advisory_xact_lock({hash})", ("@k", DbType.String, PostgresDialect.KeyLockText("account", "acme"))))
            .Should().Be(true);
        await b.Transaction!.RollbackAsync();
    }

    [Fact]
    public async Task The_table_share_lock_blocks_writers_until_the_transaction_ends()
    {
        // Plan 2.7b, ruling Q4: LockTableSharedAsync(s, "data", "account") runs LOCK TABLE data."account" IN SHARE MODE.
        var setup = await OpenAsync();
        await using (setup.Connection)
        {
            await using var create = setup.Command("CREATE SCHEMA IF NOT EXISTS data; CREATE TABLE IF NOT EXISTS data.\"account\" (id int)");
            await create.ExecuteNonQueryAsync();
        }

        var a = await OpenAsync(transaction: true);
        var b = await OpenAsync(transaction: true);
        await using var ca = a.Connection;
        await using var cb = b.Connection;

        await D.LockTableSharedAsync(a, "data", "account", CancellationToken.None);
        (await ScalarAsync(a, "SELECT count(*) FROM pg_locks WHERE locktype = 'relation' AND mode = 'ShareLock' AND pid = pg_backend_pid() AND relation = 'data.account'::regclass"))
            .Should().Be(1L);
        // Another transaction still reads, but cannot write (its row-exclusive lock conflicts with the share lock).
        (await ScalarAsync(b, "SELECT count(*) FROM data.\"account\"")).Should().Be(0L);
        var write = () => ScalarAsync(b, "LOCK TABLE data.\"account\" IN ROW EXCLUSIVE MODE NOWAIT");
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55P03");
        await b.Transaction!.RollbackAsync();
        await a.Transaction!.CommitAsync();
    }

    [Fact]
    public async Task The_table_write_lock_blocks_writers_and_itself_but_not_readers()
    {
        // Plan 2.7b, Task 3 fix round 1: LockTableWritesAsync runs LOCK TABLE … IN SHARE ROW EXCLUSIVE MODE.
        var setup = await OpenAsync();
        await using (setup.Connection)
        {
            await using var create = setup.Command("CREATE SCHEMA IF NOT EXISTS data; CREATE TABLE IF NOT EXISTS data.\"solutioncomponent\" (id int)");
            await create.ExecuteNonQueryAsync();
        }

        var a = await OpenAsync(transaction: true);
        var b = await OpenAsync(transaction: true);
        await using var ca = a.Connection;
        await using var cb = b.Connection;

        await D.LockTableWritesAsync(a, "data", "solutioncomponent", CancellationToken.None);
        (await ScalarAsync(a, "SELECT count(*) FROM pg_locks WHERE locktype = 'relation' AND mode = 'ShareRowExclusiveLock' AND pid = pg_backend_pid() AND relation = 'data.solutioncomponent'::regclass"))
            .Should().Be(1L);
        (await ScalarAsync(b, "SELECT count(*) FROM data.\"solutioncomponent\"")).Should().Be(0L);
        var write = () => ScalarAsync(b, "LOCK TABLE data.\"solutioncomponent\" IN ROW EXCLUSIVE MODE NOWAIT");
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55P03");
        await b.Transaction!.RollbackAsync();

        var c = await OpenAsync(transaction: true);
        await using var cc = c.Connection;
        var again = () => ScalarAsync(c, "LOCK TABLE data.\"solutioncomponent\" IN SHARE ROW EXCLUSIVE MODE NOWAIT");
        (await again.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55P03", "the lock conflicts with itself");
        await c.Transaction!.RollbackAsync();
        await a.Transaction!.CommitAsync();
    }

    [Fact]
    public async Task The_table_row_write_lock_waits_for_the_table_write_lock_but_not_for_another_writer()
    {
        // Plan 2.7b, Task 4 fix round 3: LockTableRowWritesAsync runs LOCK TABLE … IN ROW EXCLUSIVE MODE, the lock an UPDATE takes.
        var setup = await OpenAsync();
        await using (setup.Connection)
        {
            await using var create = setup.Command("CREATE SCHEMA IF NOT EXISTS data; CREATE TABLE IF NOT EXISTS data.\"duplicaterule\" (id int)");
            await create.ExecuteNonQueryAsync();
        }

        var a = await OpenAsync(transaction: true);
        var b = await OpenAsync(transaction: true);
        await using var ca = a.Connection;
        await using var cb = b.Connection;

        await D.LockTableRowWritesAsync(a, "data", "duplicaterule", CancellationToken.None);
        (await ScalarAsync(a, "SELECT count(*) FROM pg_locks WHERE locktype = 'relation' AND mode = 'RowExclusiveLock' AND pid = pg_backend_pid() AND relation = 'data.duplicaterule'::regclass"))
            .Should().Be(1L);
        (await ScalarAsync(b, "LOCK TABLE data.\"duplicaterule\" IN ROW EXCLUSIVE MODE NOWAIT; SELECT 1")).Should().Be(1, "writers do not wait for each other");
        var tableWrites = () => ScalarAsync(b, "LOCK TABLE data.\"duplicaterule\" IN SHARE ROW EXCLUSIVE MODE NOWAIT");
        (await tableWrites.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55P03");
        await b.Transaction!.RollbackAsync();
        await a.Transaction!.CommitAsync();
    }

    [Fact]
    public async Task Json_arrays_and_records_are_tables()
    {
        var s = await OpenAsync();
        await using var _ = s.Connection;
        var ids = $"[\"{Guid.Empty}\",\"{Guid.AllBitsSet}\"]";
        (await ScalarAsync(s, $"SELECT count(*) FROM {D.JsonArray("@ids")} WHERE CAST(value AS uuid) = @id",
            ("@ids", DbType.String, ids), ("@id", DbType.Guid, Guid.AllBitsSet))).Should().Be(1L);
        (await ScalarAsync(s, $"SELECT string_agg(value, ',') FROM {D.JsonArray("@ids")}", ("@ids", DbType.String, "[\"a\",\"b\"]")))
            .Should().Be("a,b");

        var records = D.JsonRecords("@json", [new JsonColumn("id", SqlColumnKind.Guid), new JsonColumn("name", SqlColumnKind.String, 100),
            new JsonColumn("depth", SqlColumnKind.Int32, Path: "$.d.n"), new JsonColumn("on", SqlColumnKind.Boolean)]);
        (await ScalarAsync(s, $"SELECT j.name || ':' || j.depth || ':' || j.on FROM {records} j WHERE j.id = @id",
            ("@json", DbType.String, $"[{{\"id\":\"{Guid.AllBitsSet}\",\"name\":\"Acme\",\"d\":{{\"n\":3}},\"on\":true}},{{\"id\":\"{Guid.Empty}\"}}]"),
            ("@id", DbType.Guid, Guid.AllBitsSet))).Should().Be("Acme:3:true");
        // The compiler's shape: a typed column over an array of scalars, the subquery without an alias.
        (await ScalarAsync(s, $"SELECT 2.5 IN (SELECT value FROM {D.JsonRecords("@p0", [new JsonColumn("value", SqlColumnKind.Decimal, Path: "$", Precision: 38, Scale: 10)])})",
            ("@p0", DbType.String, "[1, 2.5]"))).Should().Be(true);
        (await ScalarAsync(s, $"SELECT {D.JsonValue("@doc", "$.ObjectTypeCode")}", ("@doc", DbType.String, "{\"ObjectTypeCode\":10012}")))
            .Should().Be("10012");
    }

    [Fact]
    public async Task Row_locks_returning_and_upsert_run()
    {
        var a = await OpenAsync(transaction: true);
        await using var ca = a.Connection;
        await ScalarAsync(a, """
            CREATE TABLE IF NOT EXISTS public.live_t (id integer CONSTRAINT "PK_live_t" PRIMARY KEY, name citext, n integer);
            DELETE FROM public.live_t;
            INSERT INTO public.live_t VALUES (1, 'one', 0), (2, 'two', 0), (5, 'Acme', 0);
            """);
        await a.Transaction!.CommitAsync();
        a.Transaction = await a.Connection.BeginTransactionAsync();

        // A queue claim: the row locked by one transaction is skipped by the other.
        (await ScalarAsync(a, $"SELECT id FROM public.live_t WHERE id = 1{D.LockClause(RowLock.UpdateSkipLocked)}")).Should().Be(1);
        var b = await OpenAsync(transaction: true);
        await using var cb = b.Connection;
        (await ScalarAsync(b, D.Limit("SELECT t.id FROM public.live_t t ORDER BY t.id", "1") + D.LockClause(RowLock.UpdateSkipLocked, "t")))
            .Should().Be(2);
        await b.Transaction!.RollbackAsync();

        (await ScalarAsync(a, $"INSERT INTO public.live_t (id, name, n){D.ReturningInline(["id"])} VALUES (3, 'three', 0){D.ReturningTrailing(["id"])}"))
            .Should().Be(3);
        (await ScalarAsync(a, $"DELETE FROM public.live_t{D.ReturningInline(["name"], RowImage.Deleted)} WHERE id = 3{D.ReturningTrailing(["name"], RowImage.Deleted)}"))
            .Should().Be("three");

        var upsert = D.Upsert("public.live_t", ["id"], ["name", "n"], "SELECT @id AS id, @name AS name, 7 AS n");
        foreach (var name in new[] { "inserted", "updated" })
        {
            await using var cmd = a.Command(upsert);
            cmd.AddParameter("@id", DbType.Int32, 4);
            cmd.AddParameter("@name", DbType.String, name);
            await cmd.ExecuteNonQueryAsync();
        }
        (await ScalarAsync(a, "SELECT name || n FROM public.live_t WHERE id = 4")).Should().Be("updated7");

        // Fix round 1 (I2): string parameters are citext, so eq and IN ignore case as on SQL Server.
        (await ScalarAsync(a, "SELECT id FROM public.live_t WHERE name = @p", ("@p", DbType.String, "ACME"))).Should().Be(5);
        (await ScalarAsync(a, "SELECT id FROM public.live_t WHERE name = @p", ("@p", DbType.String, "ONE"))).Should().Be(1);
        (await ScalarAsync(a, "SELECT count(*) FROM public.live_t WHERE name IN (@p, @q)", ("@p", DbType.String, "ONE"), ("@q", DbType.String, "TWO")))
            .Should().Be(2L);
        (await ScalarAsync(a, "SELECT count(*) FROM public.live_t WHERE @p = name", ("@p", DbType.String, "Two"))).Should().Be(1L);
        // Citext ignores case, BinaryEquals does not.
        (await ScalarAsync(a, $"SELECT count(*) FROM public.live_t WHERE name = CAST('ONE' AS citext)")).Should().Be(1L);
        (await ScalarAsync(a, $"SELECT count(*) FROM public.live_t WHERE {D.BinaryEquals("name", "'ONE'")}")).Should().Be(0L);
        await a.Transaction.RollbackAsync();
    }

    [Fact]
    public async Task Dates_functions_and_errors()
    {
        var s = await OpenAsync();
        await using var _ = s.Connection;
        var unspecified = new DateTime(2026, 1, 31, 10, 0, 0, DateTimeKind.Unspecified);
        var back = (DateTime)(await ScalarAsync(s, $"SELECT {D.AddInterval("month", "1", "@d")}", ("@d", DbType.DateTime2, unspecified)))!;
        back.Should().Be(new DateTime(2026, 2, 28, 10, 0, 0, DateTimeKind.Utc));
        back.Kind.Should().Be(DateTimeKind.Utc);
        (await ScalarAsync(s, $"SELECT {D.UtcNow} >= now() AND {D.NewGuid} IS NOT NULL AND {D.IsNull("NULL", "1")} = 1")).Should().Be(true);
        (await ScalarAsync(s, $"SELECT {D.StringAgg("x", "N', '")} FROM (VALUES (1), (2)) AS v(x)")).Should().Be("1, 2");
        // Concat (2.7b Task 10): binds tighter than LIKE, and a citext part keeps LIKE case-insensitive on the citext side.
        (await ScalarAsync(s, $"SELECT CAST('DATA/Account/1' AS citext) LIKE {D.Concat("'data/'", "CAST('account' AS citext)", "'/%'")}")).Should().Be(true);
        (await ScalarAsync(s, $"SELECT {D.Concat(D.TextLiteral("a "), "CAST(NULL AS text)")} IS NULL")).Should().Be(true);
        // TryCastInteger (Task 6): SQL Server's TRY_CAST answers, null for what is not an integer of the type.
        (await ScalarAsync(s, $"SELECT {D.TryCastInteger("x", SqlColumnKind.Int32)} FROM (VALUES (' 12 ')) AS v(x)")).Should().Be(12);
        (await ScalarAsync(s, $"SELECT {D.TryCastInteger("x", SqlColumnKind.Int32)} FROM (VALUES ('-2147483648')) AS v(x)")).Should().Be(int.MinValue);
        foreach (var text in new[] { "1.5", "abc", "", "2147483648", "1e3" })
            (await ScalarAsync(s, $"SELECT {D.TryCastInteger("x", SqlColumnKind.Int32)} FROM (VALUES (@t)) AS v(x)", ("@t", DbType.String, text)))
                .Should().Be(DBNull.Value, text);
        (await ScalarAsync(s, $"SELECT {D.TryCastInteger("x", SqlColumnKind.Int64)} FROM (VALUES ('2147483648')) AS v(x)")).Should().Be(2147483648L);

        await ScalarAsync(s, """CREATE TABLE IF NOT EXISTS public.live_e (id integer CONSTRAINT "PK_live_e" PRIMARY KEY, code integer CONSTRAINT "UQ_live_e" UNIQUE)""");
        await ScalarAsync(s, "DELETE FROM public.live_e; INSERT INTO public.live_e VALUES (1, 1)");
        var pk = () => ScalarAsync(s, "INSERT INTO public.live_e VALUES (1, 2)");
        var pkError = (await pk.Should().ThrowAsync<DbException>()).Which;
        D.Classify(pkError).Should().Be(DbErrorKind.UniqueViolation);
        D.IsPrimaryKeyViolation(pkError).Should().BeTrue();
        var uq = () => ScalarAsync(s, "INSERT INTO public.live_e VALUES (2, 1)");
        var uqError = (await uq.Should().ThrowAsync<DbException>()).Which;
        D.Classify(uqError).Should().Be(DbErrorKind.UniqueViolation);
        D.IsPrimaryKeyViolation(uqError).Should().BeFalse();
        var missing = () => ScalarAsync(s, "SELECT 1 FROM public.no_such_table");
        D.Classify((await missing.Should().ThrowAsync<DbException>()).Which).Should().Be(DbErrorKind.UndefinedObject);

        await using var slow = s.Command("SELECT pg_sleep(5)");
        slow.CommandTimeout = 1;
        var timeout = () => slow.ExecuteScalarAsync();
        D.Classify((await timeout.Should().ThrowAsync<DbException>()).Which).Should().Be(DbErrorKind.Timeout);
    }
}
