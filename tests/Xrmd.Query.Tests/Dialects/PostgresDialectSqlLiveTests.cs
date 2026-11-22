using System.Data;
using FluentAssertions;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Query.Tests.Dialects;

/// <summary>
/// Plan 2.7a, Task 7: the dialect members of the query compiler, computed and rollup SQL and change tracking, run on a real
/// PostgreSQL 16 with the organization bootstrap: each gives SQL Server's answer (rounding half away from zero, TRY_CAST's null,
/// DATEDIFF's boundaries, DATEPART(week) with Sunday first, CHARINDEX from a start, the bound of MIN_ACTIVE_ROWVERSION).
/// </summary>
[Trait("Category", "Postgres")]
public sealed class PostgresDialectSqlLiveTests(PostgresContainerFixture fx) : IClassFixture<PostgresContainerFixture>, IAsyncLifetime
{
    private static readonly ISqlDialect D = PostgresDialect.Instance;

    public async Task InitializeAsync()
    {
        await using var conn = await fx.Factory.OpenAsync();
        await D.PrepareDatabaseAsync(conn, CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<DataSession> OpenAsync(bool transaction = false)
    {
        var conn = await fx.Factory.OpenAsync();
        return new DataSession(conn, transaction ? await conn.BeginTransactionAsync() : null);
    }

    private async Task<object?> ValueAsync(string expr)
    {
        var s = await OpenAsync();
        await using var _ = s.Connection;
        await using var cmd = s.Command("SELECT " + expr);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    [Theory]
    [InlineData("Acme", "ACME", true)]
    [InlineData("Acme", "Acme  ", true)]
    [InlineData("Acme", "\uFF21\uFF23\uFF2D\uFF25", true)] // full-width ＡＣＭＥ
    [InlineData("\u3042\u3044\u3046", "\u30A2\u30A4\u30A6", true)] // あいう = アイウ
    [InlineData("\uFF71\uFF72\uFF73", "\u30A2\u30A4\u30A6", true)] // half-width ｱｲｳ = アイウ
    [InlineData("\u00E9", "e", false)] // é <> e
    [InlineData("Acme ", " Acme", false)] // leading spaces count
    public async Task Text_keys_compare_like_ci_as(string left, string right, bool equal) =>
        (await ValueAsync($"{D.TextKey(D.TextLiteral(left))} = {D.TextKey(D.TextLiteral(right))}")).Should().Be(equal);

    [Theory]
    [InlineData("stra\u00DFe", "STRASSE")]
    [InlineData("STRA\u1E9EE", "strasse ")]
    public async Task Text_keys_make_sharp_s_ss(string left, string right) =>
        // Plan 2.7b, Task 2 amendment: ICU's level 2 keeps ß apart from ss, so TextKey replaces it (SQL Server's _CI_AS equates them).
        (await ValueAsync($"{D.TextKey(D.TextLiteral(left))} = {D.TextKey(D.TextLiteral(right))}")).Should().Be(true);

    [Theory]
    [InlineData("2.5", "0", 3)]
    [InlineData("-2.5", "0", -3)]
    [InlineData("CAST(0.125 AS double precision)", "2", 0.13)]
    [InlineData("CAST(-0.125 AS double precision)", "2", -0.13)]
    [InlineData("1250", "-2", 1300)]
    public async Task Round_is_half_away_from_zero(string value, string digits, double expected) =>
        Convert.ToDouble(await ValueAsync(D.Round(value, digits))).Should().Be(expected);

    [Fact]
    public async Task Truncate_cuts_toward_zero() =>
        Convert.ToDecimal(await ValueAsync(D.Truncate("CAST(-2.759 AS double precision)", "2"))).Should().Be(-2.75m);

    [Theory]
    [InlineData("3000000000", "int", null)]
    [InlineData("2147483647.9", "int", "2147483647")]
    [InlineData("-7.9", "int", "-7")]
    [InlineData("123.456", "decimal(4,2)", null)]
    [InlineData("12.345", "decimal(4,2)", "12.35")]
    [InlineData("NULL", "bigint", null)]
    public async Task TryCast_gives_null_out_of_range(string value, string type, string? expected) =>
        (await ValueAsync(D.TryCast(value, type)))?.ToString().Should().Be(expected);

    [Theory]
    [InlineData("' 42 '", "int", "42")]
    [InlineData("'4x'", "int", null)]
    [InlineData("'1.5e3'", "float", "1500")]
    [InlineData("'abc'", "float", null)]
    [InlineData("'1e400'", "float", null)] // TRY_CAST's null past the largest double (fix round 1, review Minor 4)
    [InlineData("'-1e400'", "float", null)]
    [InlineData("'1e-400'", "float", "0")] // and 0 below the smallest one
    [InlineData("'Infinity'", "float", null)]
    public async Task TryCastText_reads_numbers_from_text(string value, string type, string? expected) =>
        (await ValueAsync(D.TryCastText(value, type)))?.ToString().Should().Be(expected);

    [Theory]
    [InlineData("year", "2025-12-31 23:59", "2026-01-01 00:00", 1)]
    [InlineData("month", "2026-01-31", "2026-02-01", 1)]
    [InlineData("day", "2026-11-19 23:59", "2026-11-20 00:01", 1)]
    [InlineData("week", "2026-11-21", "2026-11-22", 1)]
    [InlineData("week", "2026-11-22", "2026-11-28", 0)]
    [InlineData("hour", "2026-11-19 10:59", "2026-11-19 11:00", 1)]
    [InlineData("minute", "2026-11-19 11:00", "2026-11-19 10:58", -2)]
    [InlineData("quarter", "2026-03-31", "2026-04-01", 1)]
    public async Task DateDiff_counts_boundaries(string unit, string start, string end, long expected) =>
        Convert.ToInt64(await ValueAsync(D.DateDiff(unit, $"CAST('{start}' AS timestamptz)", $"CAST('{end}' AS timestamptz)")))
            .Should().Be(expected);

    [Theory]
    [InlineData("2026-01-01", 1)] // a Thursday: week 1
    [InlineData("2026-01-03", 1)] // the Saturday after
    [InlineData("2026-01-04", 2)] // the first Sunday starts week 2
    [InlineData("2026-12-31", 53)]
    public async Task Week_starts_on_sunday_from_the_first_of_january(string date, int expected) =>
        Convert.ToInt32(await ValueAsync(D.DatePart("week", $"CAST('{date}' AS timestamptz)"))).Should().Be(expected);

    [Fact]
    public async Task Weekday_is_sunday_one()
    {
        Convert.ToInt32(await ValueAsync(D.Weekday("CAST('2026-11-22' AS timestamptz)"))).Should().Be(1);
        Convert.ToInt32(await ValueAsync(D.Weekday("CAST('2026-11-19' AS timestamptz)"))).Should().Be(5);
    }

    [Theory]
    [InlineData("'a'", "'banana'", null, 2)]
    [InlineData("'a'", "'banana'", "3", 4)]
    [InlineData("'a'", "'banana'", "-5", 2)]
    [InlineData("'a'", "'banana'", "7", 0)]
    [InlineData("'a'", "'banana'", "3000000000", 0)]
    [InlineData("''", "'banana'", "1", 0)]
    public async Task IndexOf_searches_from_a_start(string find, string text, string? start, int expected) =>
        Convert.ToInt32(await ValueAsync(D.IndexOf(find, text, start))).Should().Be(expected);

    [Fact]
    public async Task IndexOf_of_a_null_start_is_null() =>
        (await ValueAsync(D.IndexOf("'a'", "'banana'", "NULL"))).Should().BeNull();

    [Theory]
    [InlineData("65", "A")]
    [InlineData("65535", "\uffff")]
    [InlineData("65536", null)] // NCHAR under the organization's non-SC collation (final review m1)
    [InlineData("128512", null)]
    [InlineData("3000000000", null)]
    [InlineData("55296", null)]
    [InlineData("NULL", null)]
    public async Task CharFromCode_is_null_outside_unicode(string code, string? expected) =>
        (await ValueAsync(D.CharFromCode(code))).Should().Be(expected);

    [Fact]
    public async Task Stuff_and_text_positions()
    {
        (await ValueAsync(D.Stuff("'banana'", "2", "3", "'X'"))).Should().Be("bXna");
        (await ValueAsync(D.Left("'banana'", "3000000000"))).Should().Be("banana");
        (await ValueAsync(D.Substring("'banana'", "2", "NULL"))).Should().BeNull();
        Convert.ToInt32(await ValueAsync(D.TextLength("'ab  '"))).Should().Be(2);
        Convert.ToInt32(await ValueAsync(D.DataLength("'ab  '"))).Should().Be(8);
    }

    [Theory]
    [InlineData("a[_]%", "a_b", true)]
    [InlineData("a[_]%", "ab", false)]
    [InlineData("100[%]", "100%", true)]
    [InlineData("a\\b%", "a\\bc", true)]
    [InlineData("[[]x", "[x", true)]
    public async Task Like_patterns_translate_the_brackets(string pattern, string value, bool expected) =>
        (await ValueAsync($"{D.TextLiteral(value)} LIKE {D.TextLiteral(D.LikePattern(pattern))}")).Should().Be(expected);

    /// <summary>
    /// A like condition with SQL Server's character sets (fix round 1, review Important 1), on <c>citext</c> under the ICU root
    /// collation as an organization's columns: case ignored, accents kept (<c>_CI_AS</c>); the answers are SQL Server 2022's.
    /// </summary>
    [Theory]
    [InlineData("[ae]%", "Acme", true)]
    [InlineData("[ae]%", "ACME Corp", true)]
    [InlineData("[ae]%", "elan", true)]
    [InlineData("[ae]%", "Élan", false)] // accent kept
    [InlineData("[é]lan", "Élan", true)] // case ignored on an accented letter
    [InlineData("[^ae]%", "Élan", true)]
    [InlineData("[^ae]%", "alpha", false)]
    [InlineData("[^ae]%", "Beta", true)]
    [InlineData("_[xy]%", "aYz", true)]
    [InlineData("[ab]%", "a\nb", true)] // % crosses a line break, as LIKE does
    [InlineData("[.*]x", "*x", true)] // regular expression characters in a set are themselves
    [InlineData("[.*]x", "ax", false)]
    [InlineData("[$^]1", "^1", true)]
    [InlineData("[(|)]", "|", true)]
    [InlineData("[\\x]1", "\\1", true)]
    [InlineData("[%_]x", "%x", true)] // % and _ in a set are themselves
    [InlineData("[%_]x", "ax", false)]
    [InlineData("[-a]x", "-x", true)] // a - at either end is itself
    [InlineData("[a-]x", "-x", true)]
    [InlineData("[[][ab]", "[B", true)] // a bracketed [ still escapes it
    [InlineData("a.c[xy]", "abcx", false)] // . outside a set is itself
    [InlineData("a.c[xy]", "a.cx", true)]
    [InlineData("[^]", "x", true)] // SQL Server: [^] is any one character
    [InlineData("[^]", "xy", false)]
    [InlineData("[]]", "]", false)] // and [] matches nothing
    [InlineData("[ab", "b", true)] // a set without ] runs to the end of the pattern
    [InlineData("%[", "xb", false)]
    [InlineData("[^^a]", "^", true)] // SQL Server drops a ^ right after [^
    [InlineData("[^^a]", "a", false)]
    public async Task Like_sets_match_as_sql_server(string pattern, string value, bool expected)
    {
        var sql = D.Like($"CAST({D.TextLiteral(value)} AS citext) COLLATE \"und-x-icu\"", pattern, negate: false, D.TextLiteral);
        (await ValueAsync(sql)).Should().Be(expected, sql);
        (await ValueAsync(D.Like($"CAST({D.TextLiteral(value)} AS citext) COLLATE \"und-x-icu\"", pattern, negate: true, D.TextLiteral)))
            .Should().Be(!expected);
    }

    [Theory]
    [InlineData("[a-c]%")]
    [InlineData("[^a-c]%")]
    [InlineData("x[0-9]")]
    public void Like_ranges_are_not_available_yet(string pattern)
    {
        var fault = FluentActions.Invoking(() => D.Like("name", pattern, negate: false, D.TextLiteral)).Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.Unexpected);
        fault.HttpStatus.Should().Be(501);
        fault.Message.Should().Be("'Like patterns with character ranges' is not available on PostgreSQL organizations yet.");
    }

    [Fact]
    public void Like_without_sets_stays_a_like() =>
        D.Like("name", "a[_]%", negate: true, p => "@p").Should().Be("name NOT LIKE @p");

    [Fact]
    public async Task Last_date_time_is_the_last_microsecond_of_9999()
    {
        Convert.ToDateTime(await ValueAsync(D.LastDateTime)).Should().Be(new DateTime(9999, 12, 31, 23, 59, 59).AddTicks(9999990));
        Convert.ToInt64(await ValueAsync(D.DateDiff("day", "CAST('9999-12-31T10:00:00Z' AS timestamptz)", D.LastDateTime, big: true)))
            .Should().Be(0);
    }

    [Fact]
    public async Task Escaped_like_text_matches_itself() =>
        (await ValueAsync($"'50%_\\x' LIKE {D.EscapeLike("'50%_\\x'")} || '%'")).Should().Be(true);

    [Fact]
    public async Task Row_version_bound_stays_below_an_open_transaction_until_it_ends()
    {
        // MIN_ACTIVE_ROWVERSION's guarantee: a version at or above the bound may still commit; every version below it is
        // committed or rolled back. The sequence is not commit ordered, so the drawing transaction publishes itself first.
        var reader = await OpenAsync();
        await using var rc = reader.Connection;
        var writer = await OpenAsync(transaction: true);
        await using var wc = writer.Connection;

        await using (var draw = writer.Command($"SELECT {D.NextRowVersion}, {D.NextRowVersion}"))
        await using (var rd = await draw.ExecuteReaderAsync())
        {
            await rd.ReadAsync();
            rd.GetInt64(0).Should().BeLessThan(rd.GetInt64(1));
        }
        long first;
        await using (var again = writer.Command("SELECT last_value FROM \"data\".\"versionnumber_seq\""))
            first = Convert.ToInt64(await again.ExecuteScalarAsync()) - 1;

        // Another transaction draws after it and commits: the bound still stays at the open one's first version.
        var other = await OpenAsync(transaction: true);
        await using (var oc = other.Connection)
        {
            await using (var draw = other.Command($"SELECT {D.NextRowVersion}"))
                Convert.ToInt64(await draw.ExecuteScalarAsync()).Should().BeGreaterThan(first + 1);
            await other.Transaction!.CommitAsync();
        }
        (await D.MinActiveRowVersionAsync(reader, CancellationToken.None)).Should().BeLessThanOrEqualTo(first);

        await writer.Transaction!.CommitAsync();
        (await D.MinActiveRowVersionAsync(reader, CancellationToken.None)).Should().BeGreaterThan(first + 2);
    }

    [Fact]
    public async Task Row_version_bound_never_goes_down_under_concurrent_writers()
    {
        // Fix round 1 (review Minor 1, 2): a writer publishes a key no reader has already passed (it publishes again when a draw
        // came between its peek and its lock), and the start of a table's tracking reads the bound in order like the bound itself:
        // a start, then a bound, read one after the other, never go down.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var writers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var w = await OpenAsync(transaction: true);
                await using var wc = w.Connection;
                await using (var draw = w.Command($"SELECT {D.NextRowVersion}, {D.NextRowVersion}"))
                    await draw.ExecuteScalarAsync();
                await w.Transaction!.CommitAsync();
            }
        })).ToList();

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            var r = await OpenAsync();
            await using var rc = r.Connection;
            var (previous, problems) = (0L, new List<string>());
            while (!stop.IsCancellationRequested)
            {
                long start;
                await using (var cmd = r.Command($"SELECT {D.RowVersionStart}"))
                    start = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                var bound = await D.MinActiveRowVersionAsync(r, CancellationToken.None);
                if (start < previous) problems.Add($"start {start} after bound {previous}");
                if (bound < start) problems.Add($"bound {bound} after start {start}");
                previous = bound;
            }
            return problems;
        })).ToList();

        await Task.WhenAll(writers);
        (await Task.WhenAll(readers)).SelectMany(p => p).Should().BeEmpty();
    }

    [Fact]
    public async Task Row_version_start_is_the_next_draw()
    {
        var s = await OpenAsync();
        await using var _ = s.Connection;
        long start;
        await using (var cmd = s.Command($"SELECT {D.RowVersionStart}"))
            start = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        await using (var cmd = s.Command($"SELECT {D.NextRowVersion}"))
            Convert.ToInt64(await cmd.ExecuteScalarAsync()).Should().BeGreaterThanOrEqualTo(start);
    }

    /// <summary>
    /// Final review m3: when the rollback to the savepoint fails too, the work's failure is not lost: both come out, the work's
    /// first, and the transaction is not recovered as if the work alone had failed.
    /// </summary>
    [Fact]
    public async Task RecoverableAsync_keeps_the_failure_when_the_rollback_fails()
    {
        var s = await OpenAsync(transaction: true);
        await using var _ = s.Connection;
        var original = new InvalidOperationException("the work failed");

        var act = () => s.RecoverableAsync<int>(async () =>
        {
            await s.Transaction!.CommitAsync(); // the savepoint goes with the transaction: its rollback fails
            throw original;
        }, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateException>()).Which;
        thrown.InnerExceptions.Should().HaveCount(2);
        thrown.InnerExceptions[0].Should().BeSameAs(original);
    }

    [Fact]
    public async Task RecoverableAsync_rethrows_the_failure_after_a_rollback_to_the_savepoint()
    {
        var s = await OpenAsync(transaction: true);
        await using var _ = s.Connection;

        var act = () => s.RecoverableAsync<int>(async () =>
        {
            await using var cmd = s.Command("SELECT 1 / 0");
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }, CancellationToken.None);

        await act.Should().ThrowAsync<Npgsql.PostgresException>();
        await using var after = s.Command("SELECT 2");
        Convert.ToInt32(await after.ExecuteScalarAsync()).Should().Be(2); // the transaction outlived the failure
    }

    /// <summary>
    /// Final review m2: <see cref="ISqlDialect.TextLiteral"/> doubles quotes only, which is safe while
    /// <c>standard_conforming_strings</c> is on. The connections of the factory pin it on, over a database default and over the
    /// connection string's own options, so a backslash in a constant stays a character.
    /// </summary>
    [Fact]
    public async Task Connections_pin_standard_conforming_strings_on()
    {
        string database;
        await using (var s = await fx.Factory.OpenAsync())
            database = s.Database;
        await ExecuteAsync($"ALTER DATABASE \"{database}\" SET standard_conforming_strings = off");
        try
        {
            var factory = new NpgsqlConnectionFactory(fx.ConnectionString + ";Options=-c standard_conforming_strings=off");
            await using var conn = await factory.OpenAsync();
            await using (var cmd = conn.Command("SHOW standard_conforming_strings", null))
                (await cmd.ExecuteScalarAsync()).Should().Be("on");
            await using (var cmd = conn.Command("SELECT " + D.TextLiteral(@"a\' OR '1'='1\"), null))
                (await cmd.ExecuteScalarAsync()).Should().Be(@"a\' OR '1'='1\");
        }
        finally
        {
            await ExecuteAsync($"ALTER DATABASE \"{database}\" RESET standard_conforming_strings");
        }

        async Task ExecuteAsync(string sql)
        {
            await using var conn = await fx.Factory.OpenAsync();
            await using var cmd = conn.Command(sql, null);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
