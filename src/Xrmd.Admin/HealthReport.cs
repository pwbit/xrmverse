using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Workflows.Custom;

namespace Xrmd.Admin;

/// <summary>Counts of the asynchronous jobs (<c>asyncoperation</c>) by state.</summary>
public sealed record AsyncQueueCounts(int Waiting, int InProgress);

/// <summary>
/// Reads the queue of asynchronous jobs: waiting = Ready (0), or Suspended (1) in the Waiting status (10); in progress = Locked (2).
/// <see cref="Cached"/> serves the metrics gauge without a database round trip per scrape.
/// </summary>
public sealed class AsyncQueueStats(IServiceProvider services, TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private AsyncQueueCounts _last = new(0, 0);
    private DateTimeOffset _at = DateTimeOffset.MinValue;

    public async Task<AsyncQueueCounts> GetAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT
                COALESCE(SUM(CASE WHEN statecode = 0 OR (statecode = 1 AND statuscode = 10) THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN statecode = 2 THEN 1 ELSE 0 END), 0)
            FROM data.asyncoperation
            """;
        // Resolved here: a host without an organization database has no connection factory, and must still start.
        await using var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
        await using var cmd = conn.Command(sql);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        return new AsyncQueueCounts(rd.GetInt32(0), rd.GetInt32(1));
    }

    /// <summary>The counts as of at most a few seconds ago; zeros while the database is unavailable. For the metrics callback.</summary>
    public (int Waiting, int InProgress) Cached()
    {
        lock (_gate)
        {
            if (time.GetUtcNow() - _at < CacheFor) return (_last.Waiting, _last.InProgress);
            try
            {
                _last = GetAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // No database (not configured, down): the gauge reports what it last knew.
            }

            _at = time.GetUtcNow();
            return (_last.Waiting, _last.InProgress);
        }
    }
}

/// <summary>The body of <c>GET /admin/health</c>.</summary>
/// <param name="SdkVersion">The file version of the loaded <c>Microsoft.Xrm.Sdk</c> (installed with <c>xrmd sdk install</c>).</param>
/// <param name="WorkflowSdkVersion">The file version of the loaded <c>Microsoft.Xrm.Sdk.Workflow</c> (plan 2.14, W18); null when the
/// SDK folder has none (custom workflow activities cannot run).</param>
/// <param name="TextKeyConflicts">The alternate keys of a 2.7a PostgreSQL organization whose rows collide under the text keys of
/// plan 2.7b (ruling Q3, #299), so that the key still compares the stored text; empty otherwise.</param>
public sealed record HealthStatus(string Status, string Version, string Organization, string Database, int SchemaVersion,
    int PendingMigrations, AsyncQueueCounts AsyncQueue, string AuthMode, string SdkVersion, string? WorkflowSdkVersion,
    IReadOnlyList<TextKeyConflict> TextKeyConflicts);

/// <summary>
/// An alternate (unique) key on a text column that the seed step <see cref="SystemSeed.TextKeysStep"/> could not rebuild on text keys,
/// because the rows of <paramref name="Table"/> already hold values the text key treats as equal: they differ only by case, trailing
/// spaces, width, kana type or ß/ss (plan 2.7b, ruling Q3; case alone the old <c>citext</c> key already refused). The organization
/// keeps serving with the old key.
/// </summary>
/// <param name="Table">The data table.</param>
/// <param name="Key">The key's index.</param>
public sealed record TextKeyConflict(string Table, string Key)
{
    /// <summary>What to do (<see cref="TextKeys.ConflictMessage"/>).</summary>
    public string Message => TextKeys.ConflictMessage(Table, Key);
}

/// <summary>Builds the <see cref="HealthStatus"/>: the database, the schema and the queue, read now.</summary>
public sealed class HealthReport(IServiceProvider services, AsyncQueueStats queue, IConfiguration configuration)
{
    public const string Healthy = "Healthy";
    public const string Unhealthy = "Unhealthy";

    /// <summary>The informational version of the xrmd assembly (all projects of the solution are built with the same version).</summary>
    public static string Version { get; } =
        typeof(HealthReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HealthReport).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>The <c>Microsoft.Xrm.Sdk</c> the host runs with (from the SDK folder, or the application folder in tests).</summary>
    public static Assembly SdkAssembly => typeof(Microsoft.Xrm.Sdk.Entity).Assembly;

    /// <summary>The file version of <see cref="SdkAssembly"/>.</summary>
    public static string SdkVersion { get; } = SdkAssemblyResolver.VersionOf(SdkAssembly);

    public async Task<HealthStatus> GetAsync(CancellationToken ct)
    {
        var authMode = configuration["Auth:Mode"] is { Length: > 0 } mode ? mode : "None";
        var database = DatabaseName();
        try
        {
            var schema = await services.GetRequiredService<SchemaMigrator>().GetStatusAsync(ct);
            var counts = await queue.GetAsync(ct);
            var organization = await OrganizationNameAsync(ct);
            var conflicts = await TextKeyConflictsAsync(ct);
            return new HealthStatus(Healthy, Version, organization, database, schema.CurrentVersion, schema.PendingMigrations, counts, authMode, SdkVersion,
                WorkflowSdk.Version, conflicts);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // The database is unreachable or not set up: the report says so instead of failing.
            return new HealthStatus(Unhealthy, Version, "", database, 0, 0, new AsyncQueueCounts(0, 0), authMode, SdkVersion, WorkflowSdk.Version, []);
        }
    }

    private string DatabaseName()
    {
        try
        {
            return StorageConnectionStrings.DatabaseName(configuration.GetConnectionString("Org"));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return "";
        }
    }

    /// <summary>
    /// The keys the seed step left on the stored text (plan 2.7b, ruling Q3): while the step <see cref="SystemSeed.TextKeysStep"/>
    /// is not recorded, the plain text keys, which after a start are the ones whose rows collide, since the step rebuilds every
    /// other one; none once it is recorded (fix round 1: an index added later is not a failed rebuild). None on SQL Server.
    /// </summary>
    private async Task<IReadOnlyList<TextKeyConflict>> TextKeyConflictsAsync(CancellationToken ct)
    {
        var factory = services.GetRequiredService<IDbConnectionFactory>();
        if (factory.Dialect.PlainTextKeyIndexes is null) return [];
        await using var conn = await factory.OpenAsync(ct);
        await using (var step = conn.Command("SELECT COUNT(*) FROM meta.upgrade_step WHERE name = @step"))
        {
            step.AddParameter("@step", DbType.String, SystemSeed.TextKeysStep);
            if (Convert.ToInt64(await step.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0) return [];
        }
        return (await TextKeys.ReadPlainAsync(conn, null, ct)).Select(k => new TextKeyConflict(k.Table, k.Index)).ToList();
    }

    private async Task<string> OrganizationNameAsync(CancellationToken ct)
    {
        var factory = services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.Command(factory.Dialect.Limit("SELECT name FROM data.organization", "1"));
        return await cmd.ExecuteScalarAsync(ct) as string ?? "";
    }
}
