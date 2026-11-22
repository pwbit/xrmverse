using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>The schema version of a database: the highest applied migration and how many embedded ones are still pending.</summary>
public sealed record SchemaStatus(int CurrentVersion, int PendingMigrations);

/// <summary>An embedded migration script: its version, its file name (<c>NNNN_name.sql</c>) and its resource name.</summary>
public sealed record MigrationScript(int Version, string FileName, string Resource);

/// <summary>
/// Applies embedded numbered SQL scripts once each, in order: <c>Migrations/NNNN_name.sql</c> on SQL Server,
/// <c>Migrations/PostgreSql/NNNN_name.sql</c> on PostgreSQL (plan 2.7a, ruling P6), whose first script is the baseline
/// <c>0018_baseline.sql</c> standing for versions 1–18. The engine-specific statements are the dialect's.
/// </summary>
public sealed partial class SchemaMigrator(IDbConnectionFactory factory, ISqlDialect dialect)
{
    private const string ResourcePrefix = "Xrmd.Metadata.Migrations.";

    /// <summary>The version of the PostgreSQL baseline: the meta schema as SQL Server has it after migration 0018 (ruling P6).</summary>
    public const int PostgresBaselineVersion = 18;

    /// <summary>The migrator of the engine of <paramref name="factory"/>'s connections (<see cref="IDbConnectionFactory.Dialect"/>).</summary>
    public SchemaMigrator(IDbConnectionFactory factory) : this(factory, (factory ?? throw new ArgumentNullException(nameof(factory))).Dialect)
    {
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        // Plan 2.7a, Task 4: on PostgreSQL the bootstrap (version check, schemas, citext, the versionnumber sequence and trigger
        // function) runs before anything else, since string parameters are sent as citext.
        await dialect.PrepareDatabaseAsync(conn, ct);
        await EnsureVersionTableAsync(conn, ct);

        var applied = await GetAppliedVersionsAsync(conn, ct);
        if (await UpgradePreFoldingAsync(conn, applied, ct)) applied = await GetAppliedVersionsAsync(conn, ct);
        foreach (var m in Migrations(dialect))
        {
            if (applied.Contains(m.Version)) continue;
            await ApplyAsync(conn, m.Version, m.Resource, ct);
        }
    }

    /// <summary>
    /// Read-only status: the highest applied version (0 when none) and the number of embedded migrations not applied yet.
    /// Applies nothing; a database that has no <c>meta.schema_version</c> table yet reports every migration as pending.
    /// </summary>
    public async Task<SchemaStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var exists = conn.Command($"SELECT CASE WHEN {dialect.TableExists("meta", "schema_version")} THEN 1 ELSE 0 END");
        var applied = Convert.ToInt32(await exists.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1
            ? await GetAppliedVersionsAsync(conn, ct)
            : [];
        return new SchemaStatus(applied.Count == 0 ? 0 : applied.Max(), Migrations(dialect).Count(m => !applied.Contains(m.Version)));
    }

    /// <summary>
    /// The embedded migrations of the engine of <paramref name="d"/>, by version (plan 2.7a, ruling P6): the scripts of
    /// <c>Migrations/</c> plus the dialect's <see cref="ISqlDialect.MigrationFolder"/> (none for SQL Server).
    /// </summary>
    public static IReadOnlyList<MigrationScript> Migrations(ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var prefix = d.MigrationFolder.Length == 0 ? ResourcePrefix : $"{ResourcePrefix}{d.MigrationFolder}.";
        return typeof(SchemaMigrator).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Resource: n, FileName: n[prefix.Length..]))
            // A file of an engine's folder is not one of SQL Server's (its name keeps the folder: "PostgreSql.0018_baseline.sql").
            .Where(n => char.IsAsciiDigit(n.FileName[0]))
            .Select(n => new MigrationScript(int.Parse(n.FileName.Split('_')[0], CultureInfo.InvariantCulture), n.FileName, n.Resource))
            .OrderBy(m => m.Version)
            .ToList();
    }

    /// <summary>
    /// The migration files after the PostgreSQL baseline (<see cref="PostgresBaselineVersion"/>) that are in one folder only
    /// (ruling P6): from 0019 on each migration has <c>Migrations/NNNN_x.sql</c> and <c>Migrations/PostgreSql/NNNN_x.sql</c>,
    /// with the same file name.
    /// </summary>
    public static IReadOnlyList<string> UnpairedMigrations(IEnumerable<string> sqlServerFiles, IEnumerable<string> postgresFiles)
    {
        static HashSet<string> After(IEnumerable<string> files) =>
            files.Where(f => int.Parse(f.Split('_')[0], CultureInfo.InvariantCulture) > PostgresBaselineVersion).ToHashSet(StringComparer.Ordinal);
        var sqlServer = After(sqlServerFiles);
        var postgres = After(postgresFiles);
        return [.. sqlServer.Except(postgres).Concat(postgres.Except(sqlServer)).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A database migrated by the draft 0003 (<c>0003_solution_settings</c>, before it was folded into
    /// <c>0003_unmodelled_xml</c>) has version 3 recorded but <c>meta.entity.solution_settings_xml</c> and
    /// <c>meta.attribute.display_mask</c> instead of the <c>unmodelled_xml</c> columns. Those two columns are dropped (the
    /// values are not carried over: a re-import keeps the settings again) and version 3 is forgotten, so that the current 0003
    /// runs. A database that also ran the draft 0004 already has the current columns and is left alone. (Never the case on
    /// PostgreSQL, whose baseline has the current columns.)
    /// </summary>
    /// <returns>True when the database was upgraded.</returns>
    private async Task<bool> UpgradePreFoldingAsync(DbConnection conn, HashSet<int> applied, CancellationToken ct)
    {
        if (!applied.Contains(3)) return false;
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var check = conn.Command(
                         $"SELECT CASE WHEN NOT {dialect.ColumnExists("meta", "entity", "unmodelled_xml")} " +
                         $"AND {dialect.ColumnExists("meta", "entity", "solution_settings_xml")} THEN 1 ELSE 0 END", tx))
        {
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0) return false;
        }
        var sql = string.Join("\n",
            dialect.When(dialect.ColumnExists("meta", "attribute", "display_mask"), "ALTER TABLE meta.attribute DROP COLUMN display_mask"),
            "ALTER TABLE meta.entity DROP COLUMN solution_settings_xml;",
            "DELETE FROM meta.schema_version WHERE version = 3;");
        await using (var cmd = conn.Command(sql, tx))
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task EnsureVersionTableAsync(DbConnection conn, CancellationToken ct)
    {
        var sql = dialect.CreateSchemaIfMissing("meta") + "\n" + dialect.When(
            $"NOT {dialect.TableExists("meta", "schema_version")}",
            $"CREATE TABLE meta.schema_version (version {dialect.ColumnType(SqlColumnKind.Int32)} NOT NULL " +
            $"CONSTRAINT {dialect.Quote("PK_meta_schema_version")} PRIMARY KEY, applied_on {dialect.ColumnType(SqlColumnKind.DateTime)} NOT NULL)");
        await using var cmd = conn.Command(sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<int>> GetAppliedVersionsAsync(DbConnection conn, CancellationToken ct)
    {
        var set = new HashSet<int>();
        await using var cmd = conn.Command("SELECT version FROM meta.schema_version");
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) set.Add(rd.GetInt32(0));
        return set;
    }

    private static async Task ApplyAsync(DbConnection conn, int version, string resource, CancellationToken ct)
    {
        string script;
        await using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                         ?? throw new InvalidOperationException($"Missing resource {resource}"))
        using (var reader = new StreamReader(stream))
            script = await reader.ReadToEndAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            // No command timeout (final re-review N3): a migration may build an index on a data table (0011 on
            // principalobjectaccess), which on an organization with many rows can outlast the default 30 s, as for the seed's
            // index builds (SystemSeed, timeoutSeconds: 0). A timed-out batch would roll the migration back on every start.
            await using var cmd = conn.Command(batch, tx);
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // A script may record its own versions (the PostgreSQL baseline records 1–18, ruling P6): the version is added when missing.
        await using (var ins = conn.Command(
                         $"INSERT INTO meta.schema_version (version, applied_on) SELECT @v, {SqlDialects.For(conn).UtcNow} " +
                         "WHERE NOT EXISTS (SELECT 1 FROM meta.schema_version WHERE version = @v)", tx))
        {
            ins.AddParameter("@v", DbType.Int32, version);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}
