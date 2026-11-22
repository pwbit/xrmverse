using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Abstractions.Storage.SqlServer;

namespace Xrmd.Query.Tests.Dialects;

/// <summary>
/// Plan 2.7a, Task 2: the SQL Server dialect writes exactly the T-SQL xrmverse wrote before the dialect existed, so that moving
/// a fragment behind <see cref="ISqlDialect"/> changes nothing on SQL Server.
/// </summary>
public sealed class SqlServerDialectTests
{
    private static readonly ISqlDialect D = SqlServerDialect.Instance;

    [Fact]
    public void The_provider_is_sql_server() => D.Provider.Should().Be(StorageProvider.SqlServer);

    [Theory]
    [InlineData("name", "[name]")]
    [InlineData("data", "[data]")]
    [InlineData("a]b", "[a]]b]")]
    public void Identifiers_are_bracket_quoted(string identifier, string expected) => D.Quote(identifier).Should().Be(expected);

    [Theory]
    [InlineData(SqlColumnKind.Guid, 0, 0, 0, "uniqueidentifier")]
    [InlineData(SqlColumnKind.String, 100, 0, 0, "nvarchar(100)")]
    [InlineData(SqlColumnKind.String, 4000, 0, 0, "nvarchar(4000)")]
    [InlineData(SqlColumnKind.String, 4001, 0, 0, "nvarchar(max)")]
    [InlineData(SqlColumnKind.String, 5000, 0, 0, "nvarchar(max)")]
    [InlineData(SqlColumnKind.String, -1, 0, 0, "nvarchar(max)")]
    [InlineData(SqlColumnKind.Text, 0, 0, 0, "nvarchar(max)")]
    [InlineData(SqlColumnKind.Int32, 0, 0, 0, "int")]
    [InlineData(SqlColumnKind.Int64, 0, 0, 0, "bigint")]
    [InlineData(SqlColumnKind.Decimal, 0, 23, 2, "decimal(23,2)")]
    [InlineData(SqlColumnKind.Decimal, 0, 19, 4, "decimal(19,4)")]
    [InlineData(SqlColumnKind.Double, 0, 0, 0, "float")]
    [InlineData(SqlColumnKind.Boolean, 0, 0, 0, "bit")]
    [InlineData(SqlColumnKind.DateTime, 0, 0, 0, "datetime2")]
    [InlineData(SqlColumnKind.Binary, 0, 0, 0, "varbinary(max)")]
    [InlineData(SqlColumnKind.Binary, 32, 0, 0, "binary(32)")]
    public void Column_types_are_the_T_SQL_types(SqlColumnKind kind, int length, int precision, int scale, string expected) =>
        D.ColumnType(kind, length, precision, scale).Should().Be(expected);

    [Fact]
    public void The_version_column_is_a_rowversion() => D.RowVersionType.Should().Be("rowversion");

    [Theory]
    [InlineData("SELECT x FROM t", "1", "SELECT TOP 1 x FROM t")]
    [InlineData("SELECT x FROM t ORDER BY x", "(@top)", "SELECT TOP (@top) x FROM t ORDER BY x")]
    [InlineData("SELECT DISTINCT x FROM t", "(@top)", "SELECT DISTINCT TOP (@top) x FROM t")]
    [InlineData("SELECT x FROM t", null, "SELECT x FROM t")]
    public void Limit_writes_TOP_after_SELECT(string sql, string? count, string expected) => D.Limit(sql, count).Should().Be(expected);

    [Fact]
    public void Limit_refuses_a_statement_that_is_not_a_select() =>
        FluentActions.Invoking(() => D.Limit("UPDATE t SET x = 1", "1")).Should().Throw<ArgumentException>();

    [Fact]
    public void Page_writes_OFFSET_FETCH()
    {
        D.Page(0, 10).Should().Be("OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY");
        D.Page("(@offset)", "(@fetch)").Should().Be("OFFSET (@offset) ROWS FETCH NEXT (@fetch) ROWS ONLY");
    }

    [Fact]
    public void Functions_are_the_T_SQL_functions()
    {
        D.UtcNow.Should().Be("SYSUTCDATETIME()");
        D.NewGuid.Should().Be("NEWID()");
        D.IsNull("a.x", "0").Should().Be("ISNULL(a.x, 0)");
        D.BooleanLiteral(true).Should().Be("1");
        D.BooleanLiteral(false).Should().Be("0");
        D.TryCastInteger("JSON_VALUE(d, '$.a')", SqlColumnKind.Int32).Should().Be("TRY_CAST(JSON_VALUE(d, '$.a') AS int)");
        D.TryCastInteger("x", SqlColumnKind.Int64).Should().Be("TRY_CAST(x AS bigint)");
        FluentActions.Invoking(() => D.TryCastInteger("x", SqlColumnKind.Decimal)).Should().Throw<NotSupportedException>();
        D.AddInterval("day", "-@days", "@now").Should().Be("DATEADD(day, -@days, @now)");
        D.StringAgg("x", "N', '").Should().Be("STRING_AGG(x, N', ')");
        D.Concat("'data/'", "t.tablename", "'/%'").Should().Be("'data/' + t.tablename + '/%'");
        FluentActions.Invoking(() => D.Concat("a")).Should().Throw<ArgumentException>();
        D.NextSequenceValue("[autonumber].[s]").Should().Be("NEXT VALUE FOR [autonumber].[s]");
    }

    [Theory]
    [InlineData(RowLock.Shared, " WITH (HOLDLOCK)")]
    [InlineData(RowLock.Update, " WITH (UPDLOCK)")]
    [InlineData(RowLock.UpdateRange, " WITH (UPDLOCK, HOLDLOCK)")]
    [InlineData(RowLock.UpdateRow, " WITH (UPDLOCK, ROWLOCK)")]
    [InlineData(RowLock.UpdateRowRange, " WITH (UPDLOCK, ROWLOCK, HOLDLOCK)")]
    [InlineData(RowLock.UpdateSerializable, " WITH (UPDLOCK, SERIALIZABLE)")]
    [InlineData(RowLock.UpdateSkipLocked, " WITH (UPDLOCK, READPAST, ROWLOCK)")]
    [InlineData(RowLock.Exclusive, " WITH (XLOCK, ROWLOCK, HOLDLOCK)")]
    [InlineData(RowLock.TableShared, " WITH (TABLOCK, HOLDLOCK)")]
    [InlineData(RowLock.ReadCommittedLock, " WITH (READCOMMITTEDLOCK)")]
    [InlineData(RowLock.RepeatableRead, " WITH (REPEATABLEREAD)")]
    public void Locks_are_inline_table_hints_with_an_empty_trailing_clause(RowLock mode, string hint)
    {
        D.LockHint(mode).Should().Be(hint);
        D.LockClause(mode).Should().BeEmpty();
        D.LockClause(mode, "c").Should().BeEmpty();
    }

    [Fact]
    public void Every_row_lock_has_a_hint()
    {
        foreach (var mode in Enum.GetValues<RowLock>()) D.LockHint(mode).Should().StartWith(" WITH (", mode.ToString());
        var unknown = () => D.LockHint((RowLock)99);
        unknown.Should().Throw<ArgumentOutOfRangeException>();
        var unknownClause = () => D.LockClause((RowLock)99);
        unknownClause.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task The_key_lock_sends_nothing_where_the_hint_locks_the_key_range()
    {
        // A closed connection: any command would fail, so completing proves no statement is sent (fix round 1, I1).
        using var connection = new SqlConnection();
        await D.LockKeyAsync(new DataSession(connection, null), "team", Guid.Empty, CancellationToken.None);
        D.KeyLocksNeedTransaction.Should().BeFalse("no key lock is taken, so none needs a transaction (final review I2)");
    }

    [Fact]
    public async Task The_table_share_lock_sends_nothing_where_the_hint_locks_the_table()
    {
        // Plan 2.7b, ruling Q4: the read keeps WITH (TABLOCK, HOLDLOCK); a closed connection proves no statement is sent.
        using var connection = new SqlConnection();
        await D.LockTableSharedAsync(new DataSession(connection, null), "data", "account", CancellationToken.None);
    }

    [Fact]
    public async Task The_table_write_lock_sends_nothing_where_the_range_hint_locks_the_rows_read()
    {
        // Plan 2.7b, Task 3 fix round 1: the read keeps UPDLOCK, HOLDLOCK; a closed connection proves no statement is sent.
        using var connection = new SqlConnection();
        await D.LockTableWritesAsync(new DataSession(connection, null), "data", "solutioncomponent", CancellationToken.None);
    }

    [Fact]
    public async Task The_table_row_write_lock_sends_nothing_where_the_update_locks_its_rows()
    {
        // Plan 2.7b, Task 4 fix round 3: a closed connection proves no statement is sent.
        using var connection = new SqlConnection();
        await D.LockTableRowWritesAsync(new DataSession(connection, null), "data", "duplicaterule", CancellationToken.None);
    }

    [Fact]
    public void Json_is_read_with_OPENJSON_and_JSON_VALUE()
    {
        D.JsonArray("@ids").Should().Be("OPENJSON(@ids)");
        D.JsonRecords("@json", [("id", SqlColumnKind.Guid), ("depth", SqlColumnKind.Int32)])
            .Should().Be("OPENJSON(@json) WITH (id uniqueidentifier, depth int)");
        D.JsonRecords("@json", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$.id"), new JsonColumn("name", SqlColumnKind.String, 100, "$.name")])
            .Should().Be("OPENJSON(@json) WITH (id uniqueidentifier '$.id', name nvarchar(100) '$.name')");
        D.JsonRecords("@p0", [new JsonColumn("value", SqlColumnKind.Decimal, Path: "$", Precision: 38, Scale: 10)])
            .Should().Be("OPENJSON(@p0) WITH (value decimal(38,10) '$')");
        D.JsonValue("definition", "$.ObjectTypeCode").Should().Be("JSON_VALUE(definition, '$.ObjectTypeCode')");
    }

    [Fact]
    public void Json_members_are_listed_with_OPENJSON_behind_ISJSON()
    {
        D.IsJson("c").Should().Be("ISJSON(c) = 1");
        D.IsJson("c", valid: false).Should().Be("ISJSON(c) = 0");
        D.JsonMember("c", "@p0", "@p1", keyIsIndex: true).Should().Be(
            "SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(c) = 1 THEN JSON_QUERY(c, @p0) END) AS j WHERE j.[key] = @p1 AND DATALENGTH(j.[key]) = DATALENGTH(@p1)"
            + " AND ISJSON(CASE WHEN ISJSON(c) = 1 THEN JSON_QUERY(c, @p0) END, ARRAY) = 1");
    }

    [Fact]
    public void Returning_is_an_inline_OUTPUT_clause_and_an_empty_trailing_one()
    {
        D.ReturningInline(["[current]"]).Should().Be(" OUTPUT inserted.[current]");
        D.ReturningInline(["[id]", "name"]).Should().Be(" OUTPUT inserted.[id], inserted.name");
        D.ReturningInline(["accessrightsmask"], RowImage.Old).Should().Be(" OUTPUT deleted.accessrightsmask");
        D.ReturningInline(["accessrightsmask"], RowImage.Deleted).Should().Be(" OUTPUT deleted.accessrightsmask");
        D.ReturningTrailing(["[current]"]).Should().BeEmpty();
        D.ReturningTrailing(["id"], RowImage.Old).Should().BeEmpty();
        var none = () => D.ReturningInline([]);
        none.Should().Throw<ArgumentException>();
        var noneTrailing = () => D.ReturningTrailing([]);
        noneTrailing.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Upsert_is_a_MERGE_with_HOLDLOCK()
    {
        D.Upsert("[data].[activitypointer]", ["activityid"], ["activitytypecode", "subject"], "SELECT a.[activityid] FROM x a")
            .Should().Be("""
                MERGE [data].[activitypointer] WITH (HOLDLOCK) AS p
                USING (SELECT a.[activityid] FROM x a) AS a
                ON p.[activityid] = a.[activityid]
                WHEN MATCHED THEN UPDATE SET p.[activitytypecode] = a.[activitytypecode], p.[subject] = a.[subject]
                WHEN NOT MATCHED THEN INSERT ([activityid], [activitytypecode], [subject])
                    VALUES (a.[activityid], a.[activitytypecode], a.[subject]);
                """);
    }

    [Fact]
    public void Text_keys_are_the_expression_itself()
    {
        // Plan 2.7b, ruling Q1 (#299): the _CI_AS collation of the columns already compares like this.
        D.TextKey("t.[name]").Should().Be("t.[name]");
        D.TextKey("a + b").Should().Be("a + b");
        D.TextKeyMember("t.[name]").Should().Be("t.[name]");
        D.TextKeyDistinctMember("t.[name]").Should().Be("t.[name]");
        D.PlainTextKeyIndexes.Should().BeNull();
    }

    [Fact]
    public void Binary_comparisons_use_BIN2_and_the_byte_length()
    {
        D.BinaryEquals("t0.[name]", "@p0")
            .Should().Be("(t0.[name] COLLATE Latin1_General_BIN2 = @p0 AND DATALENGTH(t0.[name]) = DATALENGTH(@p0))");
        D.BinaryCollate("x").Should().Be("x COLLATE Latin1_General_BIN2");
        D.DefaultCollate("x").Should().Be("x COLLATE DATABASE_DEFAULT");
    }

    [Theory]
    [InlineData(2627, DbErrorKind.UniqueViolation)]
    [InlineData(2601, DbErrorKind.UniqueViolation)]
    [InlineData(547, DbErrorKind.ForeignKeyViolation)]
    [InlineData(1205, DbErrorKind.Deadlock)]
    [InlineData(-2, DbErrorKind.Timeout)]
    [InlineData(1222, DbErrorKind.LockTimeout)]
    [InlineData(10054, DbErrorKind.ConnectionLost)]
    [InlineData(4060, DbErrorKind.Unavailable)]
    [InlineData(40613, DbErrorKind.Unavailable)]
    [InlineData(18456, DbErrorKind.LoginFailed)]
    [InlineData(208, DbErrorKind.UndefinedObject)]
    [InlineData(8115, DbErrorKind.NumericOverflow)]
    [InlineData(50000, DbErrorKind.Other)]
    public void Errors_are_classified_by_number(int number, DbErrorKind expected) =>
        D.Classify(CreateSqlException(number)).Should().Be(expected);

    [Fact]
    public void A_network_wait_timeout_is_a_timeout() =>
        D.Classify(CreateSqlException(0, new Win32Exception(258))).Should().Be(DbErrorKind.Timeout);

    [Fact]
    public void Only_error_2627_is_a_primary_key_violation()
    {
        D.IsPrimaryKeyViolation(CreateSqlException(2627)).Should().BeTrue();
        D.IsPrimaryKeyViolation(CreateSqlException(2601)).Should().BeFalse();
    }

    [Fact]
    public void Sql_server_connections_have_the_sql_server_dialect()
    {
        using var connection = new SqlConnection();
        SqlDialects.For(connection).Should().BeSameAs(D);
        new DataSession(connection, null).Dialect.Should().BeSameAs(D);
        SqlDialects.For(StorageProvider.SqlServer).Should().BeSameAs(D);
        SqlDialects.Classify(CreateSqlException(1205)).Should().Be(DbErrorKind.Deadlock);
    }

    /// <summary>SqlException has no public constructor: it is built through SqlClient's internal factory, as SqlClient does.</summary>
    internal static SqlException CreateSqlException(int number, Exception? inner = null)
    {
        const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Instance;
        var errorCtor = typeof(SqlError).GetConstructors(Internal)
            .Where(c => c.GetParameters() is [{ ParameterType: var first }, ..] && first == typeof(int))
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var args = errorCtor.GetParameters()
            .Select((p, i) => i == 0 ? number : p.ParameterType == typeof(Exception) ? inner : Default(p.ParameterType)).ToArray();
        var error = (SqlError)errorCtor.Invoke(args);

        var errors = (SqlErrorCollection)typeof(SqlErrorCollection).GetConstructor(Internal, Type.EmptyTypes)!.Invoke(null);
        typeof(SqlErrorCollection).GetMethod("Add", Internal, [typeof(SqlError)])!.Invoke(errors, [error]);

        var create = typeof(SqlException).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == "CreateException" && m.GetParameters() is [{ ParameterType: var e }, { ParameterType: var v }, ..]
                        && e == typeof(SqlErrorCollection) && v == typeof(string))
            .OrderBy(m => m.GetParameters().Length)
            .ToList();
        var withInner = create.FirstOrDefault(m => m.GetParameters().Any(p => p.ParameterType == typeof(Exception)));
        if (inner is not null && withInner is not null)
        {
            var parameters = withInner.GetParameters();
            var values = parameters.Select((p, i) => i == 0 ? errors : i == 1 ? "16.00.0000"
                : p.ParameterType == typeof(Exception) ? inner : Default(p.ParameterType)).ToArray();
            return (SqlException)withInner.Invoke(null, values)!;
        }
        return (SqlException)create[0].Invoke(null, [errors, "16.00.0000"])!;
    }

    private static object? Default(Type type) => type == typeof(string) ? "test" : type.IsValueType ? Activator.CreateInstance(type) : null;
}

/// <summary>Plan 2.7a, Global Constraints: the <c>Storage:Provider</c> setting.</summary>
public sealed class StorageSettingsTests
{
    [Theory]
    [InlineData(null, StorageProvider.SqlServer)]
    [InlineData("", StorageProvider.SqlServer)]
    [InlineData("SqlServer", StorageProvider.SqlServer)]
    [InlineData("sqlserver", StorageProvider.SqlServer)]
    [InlineData("PostgreSql", StorageProvider.PostgreSql)]
    [InlineData("POSTGRESQL", StorageProvider.PostgreSql)]
    public void The_provider_is_parsed_ignoring_case(string? value, StorageProvider expected) =>
        StorageSettings.ParseProvider(value).Should().Be(expected);

    [Fact]
    public void Another_provider_is_refused() =>
        FluentActions.Invoking(() => StorageSettings.ParseProvider("Oracle")).Should().Throw<InvalidOperationException>()
            .WithMessage("Storage:Provider must be SqlServer or PostgreSql.");
    // ---- SqlDialects.Of (plan 2.7a, fix round 1, m1): the server's dialect is the one of its connections ---------------

    private sealed class Factory(ISqlDialect dialect) : IDbConnectionFactory
    {
        public ISqlDialect Dialect => dialect;

        public Type ConnectionType => typeof(SqlConnection);

        public Task<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>A dialect no connection type is registered with: every member but <see cref="ISqlDialect.Provider"/> throws.</summary>
    public class UnregisteredDialect : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_Provider" ? StorageProvider.SqlServer : throw new NotSupportedException();
    }

    [Fact]
    public void The_dialect_of_the_connection_factory_is_the_servers() =>
        SqlDialects.Of(new Factory(SqlServerDialect.Instance), StorageProvider.SqlServer).Should().BeSameAs(SqlServerDialect.Instance);

    [Fact]
    public void A_factory_of_another_engine_than_the_configured_one_is_refused() =>
        FluentActions.Invoking(() => SqlDialects.Of(new Factory(SqlServerDialect.Instance), StorageProvider.PostgreSql)).Should().Throw<InvalidOperationException>()
            .WithMessage("Storage:Provider is PostgreSql, but the organization connections are SqlServer connections.");

    [Fact]
    public void A_factory_dialect_not_registered_with_its_connections_is_refused() =>
        FluentActions.Invoking(() => SqlDialects.Of(new Factory(DispatchProxy.Create<ISqlDialect, UnregisteredDialect>()), StorageProvider.SqlServer))
            .Should().Throw<InvalidOperationException>().WithMessage("*not the one registered*");
}
