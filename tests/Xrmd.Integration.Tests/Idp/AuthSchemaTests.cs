using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xrmd.Abstractions.Storage;
using Xrmd.Host.Idp;
using Xrmd.Metadata;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Integration.Tests.Idp;

[Collection("xrmd")]
public class AuthSchemaTests(XrmdFixture fx)
{
    [Fact]
    public async Task Migration_matches_the_ef_model()
    {
        await using var db = await IdpDatabase.CreateAsync(fx.ConnectionString);

        var options = new DbContextOptionsBuilder<AuthDbContext>();
        if (TestProvider.IsPostgres) options.UseXrmdPostgres(db.ConnectionString);
        else options.UseSqlServer(db.ConnectionString);
        await using var ctx = new AuthDbContext(options.Options);
        var expected = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entity in ctx.GetService<IDesignTimeModel>().Model.GetEntityTypes())
        {
            var table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)!.Value;
            table.Schema.Should().Be("auth");
            foreach (var property in entity.GetProperties())
            {
                var type = property.GetColumnType(table).ToLowerInvariant();
                expected.Add($"{table.Schema}.{table.Name}.{property.GetColumnName(table)} {type} {(property.IsColumnNullable(table) ? "null" : "not null")}");
            }
        }

        var actual = new SortedSet<string>(StringComparer.Ordinal);
        await using (var conn = await db.Factory.OpenAsync())
        // PostgreSQL (plan 2.7b, Task 8): citext is a USER-DEFINED type of INFORMATION_SCHEMA, named by UDT_NAME.
        await using (var cmd = TestSql.Command(conn, $"""
            SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, {(TestProvider.IsPostgres ? "CASE WHEN DATA_TYPE = 'USER-DEFINED' THEN CAST(UDT_NAME AS text) ELSE CAST(DATA_TYPE AS text) END" : "DATA_TYPE")},
                CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'auth'
            """))
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync())
            {
                var length = rd.IsDBNull(4) ? null : $"({(rd.GetInt32(4) == -1 ? "max" : rd.GetInt32(4).ToString())})";
                actual.Add($"{rd.GetString(0)}.{rd.GetString(1)}.{rd.GetString(2)} {rd.GetString(3)}{length} {(rd.GetString(5) == "YES" ? "null" : "not null")}");
            }
        }

        actual.Should().NotBeEmpty();
        actual.Should().BeEquivalentTo(expected);
        actual.Select(c => c.Split('.')[1]).Distinct().Should().BeEquivalentTo(
            "credential", "signing_key", "application", "authorization", "scope", "token");
    }

    [SqlServerFact("the T-SQL migration 0010_local_idp.sql; PostgreSQL creates the auth schema in its baseline")]
    public async Task Migration_is_idempotent()
    {
        await using var db = await IdpDatabase.CreateAsync(fx.ConnectionString);
        var assembly = typeof(SchemaMigrator).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0010_local_idp.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        var script = await reader.ReadToEndAsync();

        await using var conn = await db.Factory.OpenAsync();
        for (var run = 0; run < 2; run++)
        {
            foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                await using var cmd = TestSql.Command(conn, batch);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}
