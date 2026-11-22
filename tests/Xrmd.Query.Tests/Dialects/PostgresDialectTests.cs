using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Abstractions.Storage.SqlServer;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Query.Tests.Dialects;

/// <summary>
/// Plan 2.7a, Task 3: the PostgreSQL text of every engine-specific fragment (Global Constraints type mapping, rulings P7 locks,
/// P8 JSON, P9 errors).
/// </summary>
public sealed class PostgresDialectTests
{
    private static readonly ISqlDialect D = PostgresDialect.Instance;

    [Fact]
    public void The_provider_is_postgresql() => D.Provider.Should().Be(StorageProvider.PostgreSql);

    [Theory]
    [InlineData("name", "\"name\"")]
    [InlineData("data", "\"data\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    public void Identifiers_are_double_quoted(string identifier, string expected) => D.Quote(identifier).Should().Be(expected);

    [Theory]
    [InlineData(SqlColumnKind.Guid, 0, 0, 0, "uuid")]
    [InlineData(SqlColumnKind.String, 100, 0, 0, "citext")]
    [InlineData(SqlColumnKind.String, 5000, 0, 0, "citext")]
    [InlineData(SqlColumnKind.String, -1, 0, 0, "citext")]
    [InlineData(SqlColumnKind.Text, 0, 0, 0, "citext")]
    [InlineData(SqlColumnKind.Int32, 0, 0, 0, "integer")]
    [InlineData(SqlColumnKind.Int64, 0, 0, 0, "bigint")]
    [InlineData(SqlColumnKind.Decimal, 0, 23, 2, "numeric(23,2)")]
    [InlineData(SqlColumnKind.Double, 0, 0, 0, "double precision")]
    [InlineData(SqlColumnKind.Boolean, 0, 0, 0, "boolean")]
    [InlineData(SqlColumnKind.DateTime, 0, 0, 0, "timestamptz")]
    [InlineData(SqlColumnKind.Binary, 0, 0, 0, "bytea")]
    [InlineData(SqlColumnKind.Binary, 32, 0, 0, "bytea")]
    public void Column_types_follow_the_global_constraints(SqlColumnKind kind, int length, int precision, int scale, string expected) =>
        D.ColumnType(kind, length, precision, scale).Should().Be(expected);

    [Fact]
    public void The_version_column_is_a_bigint() => D.RowVersionType.Should().Be("bigint");

    [Theory]
    [InlineData("SELECT x FROM t", "1", "SELECT x FROM t LIMIT 1")]
    [InlineData("SELECT x FROM t ORDER BY x", "(@top)", "SELECT x FROM t ORDER BY x LIMIT (@top)")]
    [InlineData("SELECT DISTINCT x FROM t", "(@top)", "SELECT DISTINCT x FROM t LIMIT (@top)")]
    [InlineData("SELECT x FROM t", null, "SELECT x FROM t")]
    public void Limit_appends_LIMIT(string sql, string? count, string expected) => D.Limit(sql, count).Should().Be(expected);

    [Fact]
    public void Limit_refuses_a_statement_that_is_not_a_select() =>
        FluentActions.Invoking(() => D.Limit("UPDATE t SET x = 1", "1")).Should().Throw<ArgumentException>();

    [Fact]
    public void Limit_refuses_a_terminated_statement() =>
        FluentActions.Invoking(() => D.Limit("SELECT x FROM t;", "1")).Should().Throw<NotSupportedException>().WithMessage("*Limit*");

    [Fact]
    public void Page_writes_OFFSET_LIMIT()
    {
        D.Page(20, 10).Should().Be("OFFSET 20 LIMIT 10");
        D.Page("(@offset)", "(@fetch)").Should().Be("OFFSET (@offset) LIMIT (@fetch)");
    }

    [Fact]
    public void Functions_are_the_postgresql_functions()
    {
        D.UtcNow.Should().Be("statement_timestamp()");
        D.NewGuid.Should().Be("gen_random_uuid()");
        D.IsNull("a.x", "0").Should().Be("COALESCE(a.x, 0)");
        D.BooleanLiteral(true).Should().Be("true");
        D.BooleanLiteral(false).Should().Be("false");
        D.TryCastInteger("x", SqlColumnKind.Int32).Should().Be(
            "(CASE WHEN CAST(x AS text) ~ '^\\s*[-+]?[0-9]+\\s*$' THEN CASE WHEN CAST(trim(CAST(x AS text)) AS numeric) BETWEEN -2147483648 AND 2147483647 "
            + "THEN CAST(CAST(trim(CAST(x AS text)) AS numeric) AS integer) END END)");
        FluentActions.Invoking(() => D.TryCastInteger("x", SqlColumnKind.Text)).Should().Throw<NotSupportedException>();
        D.StringAgg("x", "N', '").Should().Be("string_agg(CAST(x AS text), ', ')");
        D.StringAgg("x", "';'").Should().Be("string_agg(CAST(x AS text), ';')");
        D.Concat("'data/'", "t.tablename", "'/%'").Should().Be("'data/' || t.tablename || '/%'");
        FluentActions.Invoking(() => D.Concat("a", " ")).Should().Throw<ArgumentException>();
        D.NextSequenceValue("\"autonumber\".\"s\"").Should().Be("nextval('\"autonumber\".\"s\"')");
        D.NextSequenceValue("\"a\".\"o'k\"").Should().Be("nextval('\"a\".\"o''k\"')");
    }

    [Theory]
    [InlineData("day", "-@days", "@now", "(@now + (-@days) * INTERVAL '1 day')")]
    [InlineData("dd", "1", "x", "(x + (1) * INTERVAL '1 day')")]
    [InlineData("year", "2", "x", "(x + (2) * INTERVAL '1 year')")]
    [InlineData("quarter", "1", "x", "(x + (1) * INTERVAL '3 months')")]
    [InlineData("month", "1", "x", "(x + (1) * INTERVAL '1 month')")]
    [InlineData("week", "1", "x", "(x + (1) * INTERVAL '7 days')")]
    [InlineData("hour", "1", "x", "(x + (1) * INTERVAL '1 hour')")]
    [InlineData("minute", "1", "x", "(x + (1) * INTERVAL '1 minute')")]
    [InlineData("second", "-\"ttl\"", "@now", "(@now + (-\"ttl\") * INTERVAL '1 second')")]
    [InlineData("millisecond", "5", "x", "(x + (5) * INTERVAL '1 millisecond')")]
    public void Intervals_are_added_as_multiples_of_an_interval(string unit, string amount, string expr, string expected) =>
        D.AddInterval(unit, amount, expr).Should().Be(expected);

    [Fact]
    public void An_unknown_interval_unit_is_refused() =>
        FluentActions.Invoking(() => D.AddInterval("fortnight", "1", "x")).Should().Throw<ArgumentException>();

    [Fact]
    public void Locks_have_no_inline_hint()
    {
        foreach (var mode in Enum.GetValues<RowLock>()) D.LockHint(mode).Should().BeEmpty(mode.ToString());
        FluentActions.Invoking(() => D.LockHint((RowLock)99)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(RowLock.Shared, " FOR SHARE")]
    [InlineData(RowLock.Update, " FOR UPDATE")]
    [InlineData(RowLock.UpdateRange, " FOR UPDATE")]
    [InlineData(RowLock.UpdateRow, " FOR UPDATE")]
    [InlineData(RowLock.UpdateRowRange, " FOR UPDATE")]
    [InlineData(RowLock.UpdateSerializable, " FOR UPDATE")]
    [InlineData(RowLock.UpdateSkipLocked, " FOR UPDATE SKIP LOCKED")]
    [InlineData(RowLock.Exclusive, " FOR UPDATE")]
    [InlineData(RowLock.ReadCommittedLock, " FOR SHARE")]
    [InlineData(RowLock.RepeatableRead, " FOR SHARE")]
    public void Locks_are_trailing_locking_clauses(RowLock mode, string clause) => D.LockClause(mode).Should().Be(clause);

    [Fact]
    public void A_locking_clause_names_the_alias_it_locks()
    {
        D.LockClause(RowLock.UpdateSkipLocked, "a").Should().Be(" FOR UPDATE OF a SKIP LOCKED");
        D.LockClause(RowLock.Update, "t0").Should().Be(" FOR UPDATE OF t0");
        D.LockClause(RowLock.Shared, "c").Should().Be(" FOR SHARE OF c");
    }

    [Fact]
    public void A_table_lock_is_not_a_locking_clause()
    {
        FluentActions.Invoking(() => D.LockClause(RowLock.TableShared)).Should().Throw<NotSupportedException>()
            .WithMessage("*LockClause*TableShared*");
        FluentActions.Invoking(() => D.LockClause((RowLock)99)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_table_share_lock_is_a_lock_table_statement()
    {
        // Plan 2.7b, ruling Q4.
        PostgresDialect.LockTableSharedStatement("data", "account").Should().Be("LOCK TABLE data.\"account\" IN SHARE MODE");
        PostgresDialect.LockTableSharedStatement("data", "a\"b").Should().Be("LOCK TABLE data.\"a\"\"b\" IN SHARE MODE");
        FluentActions.Invoking(() => PostgresDialect.LockTableSharedStatement("da ta", "account")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_table_write_lock_is_a_share_row_exclusive_lock_table_statement()
    {
        // Plan 2.7b, Task 3 fix round 1.
        PostgresDialect.LockTableWritesStatement("data", "solutioncomponent").Should().Be("LOCK TABLE data.\"solutioncomponent\" IN SHARE ROW EXCLUSIVE MODE");
        FluentActions.Invoking(() => PostgresDialect.LockTableWritesStatement("Data", "solutioncomponent")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_table_row_write_lock_is_a_row_exclusive_lock_table_statement()
    {
        // Plan 2.7b, Task 4 fix round 3.
        PostgresDialect.LockTableRowWritesStatement("data", "duplicaterule").Should().Be("LOCK TABLE data.\"duplicaterule\" IN ROW EXCLUSIVE MODE");
        FluentActions.Invoking(() => PostgresDialect.LockTableRowWritesStatement("Data", "duplicaterule")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_key_lock_is_a_transaction_advisory_lock_on_the_lower_case_key()
    {
        // Fix round 1 (I1, m2, m3): its own statement, seed 1 (application locks use 0), the key folded to lower case.
        PostgresDialect.KeyLockStatement.Should().Be("SELECT pg_advisory_xact_lock(hashtextextended(@_key, 1))");
        PostgresDialect.KeyLockText("account", Guid.Parse("A1B2C3D4-0000-0000-0000-00000000000F"))
            .Should().Be("account:a1b2c3d4-0000-0000-0000-00000000000f");
        PostgresDialect.KeyLockText("transactioncurrency", "EUR").Should().Be("transactioncurrency:eur");
        PostgresDialect.KeyLockText("teamtemplate", 10012).Should().Be("teamtemplate:10012");
        PostgresDialect.KeyLockText("businessunit", null).Should().Be("businessunit:");
        D.KeyLocksNeedTransaction.Should().BeTrue("a transaction advisory lock is released at once on an autocommit session (final review I2)");
    }

    [Fact]
    public void Json_is_read_with_the_jsonb_functions()
    {
        D.JsonArray("@ids").Should().Be("jsonb_array_elements_text(@ids::jsonb)");
        D.JsonRecords("@json", [("id", SqlColumnKind.Guid), ("depth", SqlColumnKind.Int32)])
            .Should().Be("(SELECT CAST(e.value ->> 'id' AS uuid) AS \"id\", CAST(e.value ->> 'depth' AS integer) AS \"depth\" " +
                         "FROM jsonb_array_elements(@json::jsonb) AS e(value))");
        D.JsonRecords("@json", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$.id"), new JsonColumn("name", SqlColumnKind.String, 100, "$.name")])
            .Should().Be("(SELECT CAST(e.value ->> 'id' AS uuid) AS \"id\", CAST(e.value ->> 'name' AS citext) AS \"name\" " +
                         "FROM jsonb_array_elements(@json::jsonb) AS e(value))");
        D.JsonRecords("@p0", [new JsonColumn("value", SqlColumnKind.Decimal, Path: "$", Precision: 38, Scale: 10)])
            .Should().Be("(SELECT CAST(e.value #>> '{}' AS numeric(38,10)) AS \"value\" FROM jsonb_array_elements(@p0::jsonb) AS e(value))");
        D.JsonRecords("@p", [new JsonColumn("x", SqlColumnKind.Int32, Path: "$.a.b[2]")])
            .Should().Be("(SELECT CAST(e.value #>> '{a,b,2}' AS integer) AS \"x\" FROM jsonb_array_elements(@p::jsonb) AS e(value))");
    }

    [Fact]
    public void Json_shapes_postgresql_cannot_read_alike_are_refused()
    {
        FluentActions.Invoking(() => D.JsonRecords("@p", [new JsonColumn("x", SqlColumnKind.Binary)])).Should().Throw<NotSupportedException>()
            .WithMessage("*JsonRecords*Binary*");
        FluentActions.Invoking(() => D.JsonRecords("@p", [new JsonColumn("x", SqlColumnKind.Int32, Path: "strict $.x")]))
            .Should().Throw<NotSupportedException>().WithMessage("*strict*");
        FluentActions.Invoking(() => D.JsonValue("c", "strict $.x")).Should().Throw<NotSupportedException>().WithMessage("*JsonValue*strict*");
        FluentActions.Invoking(() => D.JsonRecords("@p", Array.Empty<JsonColumn>())).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("definition", "$.ObjectTypeCode", "(CAST(definition AS jsonb) ->> 'ObjectTypeCode')")]
    [InlineData("definition", "lax $.a", "(CAST(definition AS jsonb) ->> 'a')")]
    [InlineData("c", "$.a.b", "(CAST(c AS jsonb) #>> '{a,b}')")]
    [InlineData("c", "$.a[0]", "(CAST(c AS jsonb) #>> '{a,0}')")]
    [InlineData("c", "$.\"odd key\"", "(CAST(c AS jsonb) ->> 'odd key')")]
    [InlineData("c", "$.it's", "(CAST(c AS jsonb) ->> 'it''s')")]
    [InlineData("c", "$", "(CAST(c AS jsonb) #>> '{}')")]
    public void Json_values_are_read_with_the_text_operators(string expr, string path, string expected) =>
        D.JsonValue(expr, path).Should().Be(expected);

    [Fact]
    public void Json_members_are_listed_from_a_strict_silent_path_behind_pg_input_is_valid()
    {
        D.IsJson("c").Should().Be("pg_input_is_valid(CAST(c AS text), 'jsonb')");
        D.IsJson("c", valid: false).Should().Be("NOT pg_input_is_valid(CAST(c AS text), 'jsonb')");
        var member = D.JsonMember("c", "@p0", "@p1", keyIsIndex: false);
        member.Should().StartWith(
            "SELECT 1 FROM (SELECT CASE WHEN pg_input_is_valid(CAST(c AS text), 'jsonb') THEN jsonb_path_query_first(CAST(c AS jsonb), CAST('strict ' || @p0 AS jsonpath), '{}', true) END AS p) AS jp CROSS JOIN LATERAL ")
            .And.Contain("FROM jsonb_each(CASE WHEN jsonb_typeof(jp.p) = 'object' THEN jp.p END) AS e(k, v)) AS j")
            .And.EndWith("WHERE (CAST(j.\"key\" AS text) COLLATE \"C\" = CAST(@p1 AS text) COLLATE \"C\")");
        D.JsonMember("c", "@p0", "@p1", keyIsIndex: true).Should()
            .Contain("FROM jsonb_array_elements(CASE WHEN jsonb_typeof(jp.p) = 'array' THEN jp.p END) WITH ORDINALITY AS e(v, n)");
    }

    [Fact]
    public void Returning_is_a_trailing_RETURNING_clause_and_an_empty_inline_one()
    {
        D.ReturningInline(["\"current\""]).Should().BeEmpty();
        D.ReturningInline(["id"], RowImage.Deleted).Should().BeEmpty();
        D.ReturningTrailing(["\"current\""]).Should().Be(" RETURNING \"current\"");
        D.ReturningTrailing(["\"id\"", "name"]).Should().Be(" RETURNING \"id\", name");
        D.ReturningTrailing(["accessrightsmask"], RowImage.Deleted).Should().Be(" RETURNING accessrightsmask");
        // The old values of an UPDATE need PostgreSQL 18: refused, not approximated (fix round 1).
        FluentActions.Invoking(() => D.ReturningTrailing(["a"], RowImage.Old)).Should().Throw<NotSupportedException>().WithMessage("*RowImage.Old*");
        FluentActions.Invoking(() => D.ReturningInline(["a"], RowImage.Old)).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => D.ReturningInline([])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => D.ReturningTrailing([])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => D.ReturningTrailing(["a"], (RowImage)9)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Upsert_is_an_INSERT_ON_CONFLICT()
    {
        D.Upsert("\"data\".\"activitypointer\"", ["activityid"], ["activitytypecode", "subject"], "SELECT a.\"activityid\" FROM x a")
            .Should().Be("""
                INSERT INTO "data"."activitypointer" ("activityid", "activitytypecode", "subject")
                SELECT a."activityid", a."activitytypecode", a."subject" FROM (SELECT a."activityid" FROM x a) AS a WHERE true
                ON CONFLICT ("activityid") DO UPDATE SET "activitytypecode" = EXCLUDED."activitytypecode", "subject" = EXCLUDED."subject";
                """);
        D.Upsert("t", ["k"], [], "SELECT 1 AS k").Should().EndWith("ON CONFLICT (\"k\") DO NOTHING;");
    }

    [Fact]
    public void Text_keys_trim_trailing_spaces_and_compare_in_the_ci_as_collation()
    {
        // Plan 2.7b, ruling Q1 (#299) and its Task 2 amendment: case, width and kana type ignored, accents kept, trailing spaces
        // trimmed, ß and ẞ made ss.
        D.TextKey("t.\"name\"").Should().Be("(replace(replace(rtrim(t.\"name\"::text), '\u00DF', 'ss'), '\u1E9E', 'ss') COLLATE data.xrmv_ci_as)");
        D.TextKey("@p0").Should().Be("(replace(replace(rtrim(@p0::text), '\u00DF', 'ss'), '\u1E9E', 'ss') COLLATE data.xrmv_ci_as)");
        D.TextKey("value").Should().Be("(replace(replace(rtrim(value::text), '\u00DF', 'ss'), '\u1E9E', 'ss') COLLATE data.xrmv_ci_as)");
        // An expression other than a column or a parameter is parenthesized before the cast binds to its last operand.
        D.TextKey("a || b").Should().Be("(replace(replace(rtrim((a || b)::text), '\u00DF', 'ss'), '\u1E9E', 'ss') COLLATE data.xrmv_ci_as)");
    }

    [Fact]
    public void Text_key_members_show_the_smallest_stored_value()
    {
        D.TextKeyMember("t0.\"name\"").Should().Be("CAST(MIN(CAST(t0.\"name\" AS text) COLLATE \"C\") AS citext)");
        D.TextKeyDistinctMember("t0.\"name\"").Should().Be(
            "CAST(MIN(CAST(t0.\"name\" AS text) COLLATE \"C\") OVER (PARTITION BY " + D.TextKey("t0.\"name\"") + ") AS citext)");
    }

    [Fact]
    public void Plain_text_key_indexes_are_read_from_the_catalog() =>
        D.PlainTextKeyIndexes.Should().NotBeNull().And.Contain("pg_index").And.Contain("citext");

    [Fact]
    public void Binary_comparisons_compare_the_text_in_the_C_collation()
    {
        D.BinaryEquals("t0.\"name\"", "@p0").Should().Be("(CAST(t0.\"name\" AS text) COLLATE \"C\" = CAST(@p0 AS text) COLLATE \"C\")");
        D.BinaryCollate("x").Should().Be("CAST(x AS text) COLLATE \"C\"");
        D.DefaultCollate("x").Should().Be("CAST(x AS citext)");
    }

    [Theory]
    [InlineData("23505", DbErrorKind.UniqueViolation)]
    [InlineData("23503", DbErrorKind.ForeignKeyViolation)]
    [InlineData("40P01", DbErrorKind.Deadlock)]
    [InlineData("40001", DbErrorKind.Deadlock)]
    [InlineData("57014", DbErrorKind.Timeout)]
    [InlineData("55P03", DbErrorKind.LockTimeout)]
    [InlineData("08006", DbErrorKind.ConnectionLost)]
    [InlineData("08001", DbErrorKind.ConnectionLost)]
    [InlineData("57P01", DbErrorKind.ConnectionLost)]
    [InlineData("28P01", DbErrorKind.LoginFailed)]
    [InlineData("28000", DbErrorKind.LoginFailed)]
    [InlineData("42P01", DbErrorKind.UndefinedObject)]
    [InlineData("57P03", DbErrorKind.Unavailable)]
    [InlineData("53300", DbErrorKind.Unavailable)]
    [InlineData("3D000", DbErrorKind.Unavailable)]
    [InlineData("22003", DbErrorKind.NumericOverflow)]
    [InlineData("P0001", DbErrorKind.Other)]
    public void Errors_are_classified_by_sqlstate(string sqlState, DbErrorKind expected) =>
        D.Classify(Postgres(sqlState)).Should().Be(expected);

    [Fact]
    public void Client_side_failures_are_classified()
    {
        D.Classify(new NpgsqlException("Exception while reading from stream", new TimeoutException())).Should().Be(DbErrorKind.Timeout);
        D.Classify(new NpgsqlException("Exception while reading from stream", new IOException("reset"))).Should().Be(DbErrorKind.ConnectionLost);
        D.Classify(new NpgsqlException("Failed to connect", new SocketException(111))).Should().Be(DbErrorKind.ConnectionLost);
        D.Classify(new NpgsqlException("other")).Should().Be(DbErrorKind.Other);
        D.Classify(SqlServerDialectTests.CreateSqlException(1205)).Should().Be(DbErrorKind.Other);
    }

    [Fact]
    public void A_primary_key_violation_is_told_apart_from_another_unique_index()
    {
        D.IsPrimaryKeyViolation(Postgres("23505", "PK_account")).Should().BeTrue();
        D.IsPrimaryKeyViolation(Postgres("23505", "account_pkey")).Should().BeTrue();
        D.IsPrimaryKeyViolation(Postgres("23505", "UQ_account_name")).Should().BeFalse();
        D.IsPrimaryKeyViolation(Postgres("23503", "PK_account")).Should().BeFalse();
    }

    [Fact]
    public void Npgsql_connections_have_the_postgresql_dialect()
    {
        PostgresStorage.Register();
        using var connection = new NpgsqlConnection();
        SqlDialects.For(connection).Should().BeSameAs(D);
        new DataSession(connection, null).Dialect.Should().BeSameAs(D);
        SqlDialects.For(StorageProvider.PostgreSql).Should().BeSameAs(D);
        SqlDialects.Classify(Postgres("40P01")).Should().Be(DbErrorKind.Deadlock);
        SqlDialects.IsPrimaryKeyViolation(Postgres("23505", "PK_x")).Should().BeTrue();
        // The SQL Server dialect keeps its own connections and errors.
        SqlDialects.Classify(SqlServerDialectTests.CreateSqlException(1205)).Should().Be(DbErrorKind.Deadlock);
    }

    [Fact]
    public void The_npgsql_factory_is_the_postgresql_servers_and_not_a_sql_server_one()
    {
        var factory = new NpgsqlConnectionFactory("Host=localhost;Database=x;Username=u;Password=p");
        factory.ConnectionType.Should().Be(typeof(NpgsqlConnection));
        SqlDialects.Of(factory, StorageProvider.PostgreSql).Should().BeSameAs(D);
        FluentActions.Invoking(() => SqlDialects.Of(factory, StorageProvider.SqlServer)).Should().Throw<InvalidOperationException>()
            .WithMessage("Storage:Provider is SqlServer, but the organization connections are PostgreSql connections.");
    }

    [Fact]
    public void Sessions_run_in_utc() =>
        new NpgsqlConnectionStringBuilder(NpgsqlConnectionFactory.Normalize("Host=h;Database=x;Timezone=Europe/Rome")).Timezone.Should().Be("UTC");

    [Fact]
    public void Date_parameters_are_sent_as_utc_timestamptz()
    {
        using var cmd = new NpgsqlCommand();
        var unspecified = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Unspecified);
        var p = (NpgsqlParameter)cmd.AddParameter("@a", DbType.DateTime2, unspecified);
        D.ConfigureParameter(p);
        p.NpgsqlDbType.Should().Be(NpgsqlDbType.TimestampTz);
        ((DateTime)p.Value!).Kind.Should().Be(DateTimeKind.Utc);
        ((DateTime)p.Value!).Ticks.Should().Be(unspecified.Ticks);

        var legacy = (NpgsqlParameter)cmd.AddParameter("@b", DbType.DateTime, DBNull.Value);
        D.ConfigureParameter(legacy);
        legacy.NpgsqlDbType.Should().Be(NpgsqlDbType.TimestampTz);
        legacy.Value.Should().Be(DBNull.Value);

        var local = DateTime.SpecifyKind(unspecified, DateTimeKind.Local);
        var l = (NpgsqlParameter)cmd.AddParameter("@c", DbType.DateTime2, local);
        D.ConfigureParameter(l);
        ((DateTime)l.Value!).Should().Be(local.ToUniversalTime());

        // Fix round 1 (I2): strings go as citext, so that they compare with citext columns ignoring case.
        foreach (var type in new[] { DbType.String, DbType.StringFixedLength, DbType.AnsiString, DbType.AnsiStringFixedLength })
        {
            var text = (NpgsqlParameter)cmd.AddParameter("@d" + type, type, "x");
            D.ConfigureParameter(text);
            text.NpgsqlDbType.Should().Be(NpgsqlDbType.Citext);
        }
        var guid = (NpgsqlParameter)cmd.AddParameter("@g", DbType.Guid, Guid.Empty);
        D.ConfigureParameter(guid);
        guid.NpgsqlDbType.Should().Be(NpgsqlDbType.Uuid);
    }

    [Fact]
    public void Parameters_added_on_an_npgsql_connection_are_configured()
    {
        PostgresStorage.Register();
        using var connection = new NpgsqlConnection();
        using var cmd = connection.Command("SELECT @a");
        var p = (NpgsqlParameter)cmd.AddParameter("@a", DbType.DateTime2, new DateTime(2026, 1, 1));
        p.NpgsqlDbType.Should().Be(NpgsqlDbType.TimestampTz);
    }

    private static PostgresException Postgres(string sqlState, string? constraint = null) =>
        new("message", "ERROR", "ERROR", sqlState, constraintName: constraint);
}

/// <summary>Plan 2.7a, carry-over N2: the server's dialect is the one registered for the factory's own connection type.</summary>
public sealed class SqlDialectsConnectionTypeTests
{
    private sealed class Factory(ISqlDialect dialect, Type connectionType) : IDbConnectionFactory
    {
        public ISqlDialect Dialect => dialect;
        public Type ConnectionType => connectionType;
        public Task<DbConnection> OpenAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public void A_factory_whose_dialect_is_not_its_connections_is_refused()
    {
        PostgresStorage.Register();
        FluentActions.Invoking(() => SqlDialects.Of(new Factory(PostgresDialect.Instance, typeof(Microsoft.Data.SqlClient.SqlConnection)), StorageProvider.PostgreSql))
            .Should().Throw<InvalidOperationException>().WithMessage("*not the one registered*");
        FluentActions.Invoking(() => SqlDialects.Of(new Factory(SqlServerDialect.Instance, typeof(NpgsqlConnection)), StorageProvider.SqlServer))
            .Should().Throw<InvalidOperationException>().WithMessage("*not the one registered*");
    }
}
