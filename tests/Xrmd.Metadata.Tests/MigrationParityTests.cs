using System.Text.RegularExpressions;
using FluentAssertions;
using Xrmd.Abstractions.Storage.SqlServer;
using Xrmd.Storage.PostgreSql;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// Plan 2.7a, ruling P6: the PostgreSQL meta schema starts from <c>Migrations/PostgreSql/0018_baseline.sql</c> (the meta schema
/// as SQL Server has it after 0018, recording versions 1–18); from 0019 on every migration has a file in both folders.
/// </summary>
public partial class MigrationParityTests
{
    [Fact]
    public void Every_migration_after_the_baseline_has_both_files() =>
        SchemaMigrator.UnpairedMigrations(
                SchemaMigrator.Migrations(SqlServerDialect.Instance).Select(m => m.FileName),
                SchemaMigrator.Migrations(PostgresDialect.Instance).Select(m => m.FileName))
            .Should().BeEmpty("from 0019 on a migration needs Migrations/NNNN_x.sql and Migrations/PostgreSql/NNNN_x.sql (ruling P6)");

    [Fact]
    public void The_rule_flags_a_migration_with_one_file_or_two_names()
    {
        string[] sqlServer = [.. Enumerable.Range(1, 18).Select(v => $"{v:D4}_x.sql"), "0019_new_table.sql", "0020_index.sql", "0021_both.sql"];
        string[] postgres = ["0018_baseline.sql", "0019_new_table.sql", "0020_index_renamed.sql", "0021_both.sql", "0022_pg_only.sql"];

        SchemaMigrator.UnpairedMigrations(sqlServer, postgres).Should().BeEquivalentTo(
            "0020_index.sql", "0020_index_renamed.sql", "0022_pg_only.sql");
        SchemaMigrator.UnpairedMigrations(sqlServer[..18], ["0018_baseline.sql"]).Should().BeEmpty(
            "the versions the baseline covers have one file per engine");
    }

    [Fact]
    public void The_postgresql_folder_starts_with_the_baseline_and_sql_server_has_its_versions()
    {
        var postgres = SchemaMigrator.Migrations(PostgresDialect.Instance);
        postgres.Should().NotBeEmpty();
        postgres[0].Should().Be(new MigrationScript(18, "0018_baseline.sql", "Xrmd.Metadata.Migrations.PostgreSql.0018_baseline.sql"));
        postgres.Skip(1).Where(m => m.Version <= SchemaMigrator.PostgresBaselineVersion).Should().BeEmpty();

        var sqlServer = SchemaMigrator.Migrations(SqlServerDialect.Instance);
        sqlServer.Select(m => m.Version).Take(18).Should().Equal(Enumerable.Range(1, 18), "the baseline stands for 0001–0018");
        sqlServer.Should().OnlyContain(m => !m.Resource.Contains(".PostgreSql.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_baseline_records_versions_1_to_18()
    {
        var baseline = Read("Xrmd.Metadata.Migrations.PostgreSql.0018_baseline.sql");
        baseline.Should().Contain("INSERT INTO meta.schema_version (version, applied_on)")
            .And.Contain("generate_series(1, 18)");
        baseline.Should().NotMatchRegex(@"(?m)^\s*GO\s*$", "PostgreSQL scripts have no GO batches");
    }

    /// <summary>
    /// The text half of <c>MetaSchemaParityTests</c> (which compares the columns on live databases): every table the SQL Server
    /// scripts 0001–0018 create is created by the baseline.
    /// </summary>
    [Fact]
    public void The_baseline_creates_every_table_of_0001_to_0018()
    {
        var baseline = Read("Xrmd.Metadata.Migrations.PostgreSql.0018_baseline.sql");
        var created = SchemaMigrator.Migrations(SqlServerDialect.Instance).Where(m => m.Version <= 18)
            .SelectMany(m => CreateTable().Matches(Read(m.Resource)).Select(x => $"{x.Groups[1].Value}.{x.Groups[2].Value}".ToLowerInvariant()))
            .Distinct()
            .ToList();
        created.Should().HaveCountGreaterThan(30);
        var inBaseline = PgCreateTable().Matches(baseline).Select(x => $"{x.Groups[1].Value}.{x.Groups[2].Value}".ToLowerInvariant()).ToHashSet();
        inBaseline.Should().Contain(created);
    }

    private static string Read(string resource)
    {
        using var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"Missing resource {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"CREATE TABLE \[?(\w+)\]?\.\[?(\w+)\]?", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"CREATE TABLE (?:IF NOT EXISTS )?""?(\w+)""?\.""?(\w+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex PgCreateTable();
}
