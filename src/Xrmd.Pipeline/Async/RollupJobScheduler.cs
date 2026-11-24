using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Pipeline.Async;

/// <summary>The settings of the rollup calculation jobs (configuration section <c>Rollups</c>, plan 2.12b, ruling R8).</summary>
public sealed class RollupOptions
{
    /// <summary>How long after a rollup column is created or updated its Mass Calculate Rollup Field job runs (Learn: 12 hours).</summary>
    public TimeSpan MassCalculationDelay { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// How long a rollup calculation job waits before it runs again after a transient database failure (a deadlock, a timeout, a lost
    /// connection): an xrmverse setting, Dataverse documents no such delay.
    /// </summary>
    public TimeSpan TransientRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The <c>data</c> of a rollup calculation job (JSON, plan 2.12b, rulings R2 and R8): the table, the column (a Mass Calculate Rollup
/// Field job; null for the Calculate Rollup Field job of the table), when the Mass job last ran (<c>completedOn</c>: the start of its
/// last run; null until it ran, and again after the column's definition changed) and when the incremental job last started.
/// </summary>
public sealed record RollupJobData(
    [property: JsonPropertyName("entity")] string Entity,
    [property: JsonPropertyName("attribute")] string? Attribute,
    [property: JsonPropertyName("completedOn")] DateTime? CompletedOnUtc,
    [property: JsonPropertyName("lastRunStarted")] DateTime? LastRunStartedUtc,
    [property: JsonPropertyName("definition")] string? Definition = null)
{
    /// <summary>
    /// The stamp of a rollup column's definition (a hash of its <c>FormulaDefinition</c>): a Mass job compares the stamp of the
    /// definition it started with to the column's at its end, so an update committed while it ran is not lost.
    /// </summary>
    public static string? Stamp(AttributeMetadata? column) =>
        column is not null && ComputedColumnRules.FormulaOf(column) is { } text
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16]
            : null;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The JSON stored in <c>asyncoperation.data</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The data of <paramref name="json"/>; null when it is empty, not JSON or names no table.</summary>
    public static RollupJobData? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<RollupJobData>(json, Json);
            return string.IsNullOrEmpty(data?.Entity) ? null : data;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Keeps the rollup calculation jobs (plan 2.12b, rulings R8, R9) in step with the rollup columns: one Mass Calculate Rollup Field job
/// (operationtype 58) per rollup column and one Calculate Rollup Field job (57) per table that has rollups, each a recurring handler job
/// owned by SYSTEM that waits Suspended / Waiting (1/10) until its <c>postponeuntil</c>. Told by the metadata store after each commit
/// (<see cref="IRollupJobs"/>): a created or updated column's Mass job is due <see cref="RollupOptions.MassCalculationDelay"/> later (an
/// update, even with no change, resets the delay and the job's last run), an imported column's at once; the table's incremental job is
/// created with its first rollup (<see cref="RollupRecurrence.Default"/> from now) and deleted with its last. <see cref="ReconcileAsync"/>
/// (at host start) creates the missing jobs and deletes those of columns or tables that are gone, the duplicates and the ended ones.
/// The jobs are written in SQL (a job row is the server's: no pipeline), one change at a time.
/// </summary>
public sealed class RollupJobScheduler(IDbConnectionFactory factory, IMetadataStore store, RollupOptions options, TimeProvider time,
    AsyncOperationQueue? queue = null, ILogger<RollupJobScheduler>? logger = null) : IRollupJobs
{
    /// <summary>The <c>messagename</c> of a Mass Calculate Rollup Field job.</summary>
    public const string MassMessage = "MassCalculateRollupField";

    /// <summary>The <c>messagename</c> of a Calculate Rollup Field job.</summary>
    public const string CalculateMessage = "CalculateRollupField";

    private const int Suspended = 1, Waiting = 10, Completed = 3;

    private readonly AsyncOperationQueue _queue = queue ?? new AsyncOperationQueue();
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>One change at a time: a notification and the reconcile never interleave.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Caller? _system;

    /// <summary>The name of the Mass Calculate Rollup Field job of <paramref name="entity"/>.<paramref name="attribute"/>.</summary>
    public static string MassName(string entity, string attribute) => $"Mass Calculate Rollup Field: {entity}.{attribute}";

    /// <summary>The name of the Calculate Rollup Field job of <paramref name="entity"/>.</summary>
    public static string CalculateName(string entity) => $"Calculate Rollup Field: {entity}";

    /// <inheritdoc />
    public async Task DefinitionChangedAsync(string entity, string attribute, RollupChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        var signal = false;
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await factory.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var s = new DataSession(conn, tx);
            var jobs = await JobsAsync(s, entity, ct);
            var now = time.GetUtcNow().UtcDateTime;
            // The store already holds the latest committed metadata: a column deleted (or added) since this change was queued is
            // left to the notification that follows.
            var isRollup = IsRollup(entity, attribute);
            switch (change)
            {
                case RollupChange.Added or RollupChange.Updated or RollupChange.Imported when isRollup:
                    var due = change == RollupChange.Imported ? now : now + options.MassCalculationDelay;
                    await EnsureMassAsync(s, jobs, entity, attribute, due, reset: true, ct);
                    await EnsureCalculateAsync(s, jobs, entity, now, ct);
                    signal = change == RollupChange.Imported;
                    break;
                case RollupChange.Deleted when !isRollup:
                    await DeleteAsync(s, jobs.Where(j => j.Type == AsyncOperationQueue.MassCalculateRollupField && Same(j.Attribute, attribute)), ct);
                    if (!HasRollups(entity))
                        await DeleteAsync(s, jobs.Where(j => j.Type == AsyncOperationQueue.CalculateRollupField), ct);
                    break;
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
        if (signal) _queue.Signal();
    }

    /// <summary>
    /// Creates the missing jobs of the rollups the store holds (a Mass job due after <see cref="RollupOptions.MassCalculationDelay"/>, an
    /// incremental job per table) and deletes the jobs of columns or tables that are gone, duplicates and jobs that ended (a failed or
    /// cancelled rollup job would otherwise leave its column without calculation). Existing jobs are kept as they are, except a Mass job
    /// stamped with another definition than the column's (a change whose notification was lost when the host stopped): it is due after
    /// the delay again and forgets its last run, as after an update.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await factory.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var s = new DataSession(conn, tx);
            var now = time.GetUtcNow().UtcDateTime;
            var all = await JobsAsync(s, null, ct);
            var rollups = store.GetAllEntities()
                .SelectMany(e => (e.Attributes ?? []).Where(RollupColumns.IsRollup).Select(a => (Entity: e.LogicalName, Attribute: a.LogicalName)))
                .ToList();

            var orphans = all.Where(j => j.Type == AsyncOperationQueue.MassCalculateRollupField
                    ? !rollups.Any(r => Same(r.Entity, j.Entity) && Same(r.Attribute, j.Attribute))
                    : !rollups.Any(r => Same(r.Entity, j.Entity)))
                .ToList();
            await DeleteAsync(s, orphans, ct);
            var jobs = all.Except(orphans).ToList();
            foreach (var (entity, attribute) in rollups)
            {
                var own = jobs.Where(j => Same(j.Entity, entity)).ToList();
                // The job EnsureMassAsync keeps: the first live one.
                var stale = own.FirstOrDefault(j => j.Type == AsyncOperationQueue.MassCalculateRollupField && Same(j.Attribute, attribute)
                                                    && j.State != Completed) is { } kept && kept.Data?.Definition != StampOf(entity, attribute);
                await EnsureMassAsync(s, own, entity, attribute, now + options.MassCalculationDelay, reset: stale, ct);
            }
            foreach (var entity in rollups.Select(r => r.Entity).Distinct(StringComparer.OrdinalIgnoreCase))
                await EnsureCalculateAsync(s, jobs.Where(j => Same(j.Entity, entity)).ToList(), entity, now, ct);
            await tx.CommitAsync(ct);
            if (orphans.Count > 0) _logger.LogInformation("Deleted {Count} rollup calculation job(s) of rollup columns that are gone.", orphans.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- jobs -------------------------------------------------------------------------------------------------------------

    /// <summary>A rollup job: its id, type, table, column (58), state and data.</summary>
    private sealed record Job(Guid Id, int Type, string? Entity, string? Attribute, int State, RollupJobData? Data);

    /// <summary>The rollup jobs (of <paramref name="entity"/> when given), read with UPDLOCK.</summary>
    private static async Task<List<Job>> JobsAsync(DataSession s, string? entity, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT asyncoperationid, operationtype, primaryentitytype, data, statecode FROM data.asyncoperation{s.Dialect.LockHint(RowLock.Update)} " +
            "WHERE operationtype IN (@calculate, @mass)" + (entity is null ? "" : " AND primaryentitytype = @entity") +
            " ORDER BY createdon, asyncoperationid" + s.Dialect.LockClause(RowLock.Update));
        cmd.AddParameter("@calculate", DbType.Int32, AsyncOperationQueue.CalculateRollupField);
        cmd.AddParameter("@mass", DbType.Int32, AsyncOperationQueue.MassCalculateRollupField);
        if (entity is not null) cmd.AddParameter("@entity", DbType.String, entity, 64);
        var jobs = new List<Job>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var data = RollupJobData.Parse(rd.IsDBNull(3) ? null : rd.GetString(3));
            jobs.Add(new Job(rd.GetGuid(0), rd.GetInt32(1), data?.Entity ?? (rd.IsDBNull(2) ? null : rd.GetString(2)), data?.Attribute,
                rd.IsDBNull(4) ? -1 : rd.GetInt32(4), data));
        }
        return jobs;
    }

    /// <summary>
    /// Makes sure <paramref name="entity"/>.<paramref name="attribute"/> has one live Mass job (<paramref name="jobs"/> are the table's): a
    /// new one is due at <paramref name="due"/>; with <paramref name="reset"/>, an existing one too, and it forgets its last run. Ended
    /// jobs and duplicates are deleted.
    /// </summary>
    private async Task EnsureMassAsync(DataSession s, List<Job> jobs, string entity, string attribute, DateTime due, bool reset, CancellationToken ct)
    {
        var mine = jobs.Where(j => j.Type == AsyncOperationQueue.MassCalculateRollupField && Same(j.Attribute, attribute)).ToList();
        var live = mine.Where(j => j.State != Completed).ToList();
        await DeleteAsync(s, mine.Except(live.Take(1)), ct);
        if (live.FirstOrDefault() is not { } job)
        {
            var data = new RollupJobData(entity, attribute, null, null, StampOf(entity, attribute));
            var id = await _queue.EnqueueSystemJobAsync(s, await SystemAsync(s, ct), AsyncOperationQueue.MassCalculateRollupField,
                MassName(entity, attribute), MassMessage, entity, null, data.ToJson(), ct);
            await SuspendAsync(s, id, due, null, null, null, Now(), ct);
            return;
        }
        if (!reset) return;
        var fresh = (job.Data ?? new RollupJobData(entity, attribute, null, null)) with { CompletedOnUtc = null, Definition = StampOf(entity, attribute) };
        // A job running now (2/20) records its own outcome when it ends, which would overwrite this: it is left as it is, and the Mass
        // job itself sees the definition changed under it (MassCalculateRollupJob). A Postpone sent while a job runs is overwritten by
        // the run's outcome likewise (a known limit).
        await SuspendAsync(s, job.Id, due, fresh.ToJson(), null, null, Now(), ct, onlyWaiting: true);
    }

    /// <summary>Makes sure <paramref name="entity"/> has one live Calculate Rollup Field job (<paramref name="jobs"/> are the table's).</summary>
    private async Task EnsureCalculateAsync(DataSession s, List<Job> jobs, string entity, DateTime now, CancellationToken ct)
    {
        var mine = jobs.Where(j => j.Type == AsyncOperationQueue.CalculateRollupField).ToList();
        var live = mine.Where(j => j.State != Completed).ToList();
        await DeleteAsync(s, mine.Except(live.Take(1)), ct);
        if (live.Count > 0) return;
        var id = await _queue.EnqueueSystemJobAsync(s, await SystemAsync(s, ct), AsyncOperationQueue.CalculateRollupField,
            CalculateName(entity), CalculateMessage, entity, null, new RollupJobData(entity, null, null, null).ToJson(), ct);
        await SuspendAsync(s, id, RollupRecurrence.Next(RollupRecurrence.Default, now, now), null, RollupRecurrence.Default, now, now, ct);
    }

    /// <summary>
    /// Puts the job <paramref name="id"/> to Suspended / Waiting (1/10) until <paramref name="until"/>, with <paramref name="data"/>,
    /// <paramref name="pattern"/> and <paramref name="start"/> when given (else kept); with <paramref name="onlyWaiting"/>, only a job that
    /// is not running.
    /// </summary>
    private static async Task SuspendAsync(DataSession s, Guid id, DateTime until, string? data, string? pattern, DateTime? start,
        DateTime now, CancellationToken ct, bool onlyWaiting = false)
    {
        await using var cmd = s.Command("""
            UPDATE data.asyncoperation
            SET statecode = @suspended, statuscode = @waiting, postponeuntil = @until, data = COALESCE(@data, data),
                recurrencepattern = COALESCE(@pattern, recurrencepattern), recurrencestarttime = COALESCE(@start, recurrencestarttime),
                modifiedon = @now
            WHERE asyncoperationid = @id
            """ + (onlyWaiting ? " AND statecode IN (0, 1)" : ""));
        cmd.AddParameter("@id", DbType.Guid, id);
        cmd.AddParameter("@suspended", DbType.Int32, Suspended);
        cmd.AddParameter("@waiting", DbType.Int32, Waiting);
        cmd.AddParameter("@until", DbType.DateTime2, until);
        cmd.AddParameter("@data", DbType.String, (object?)data ?? DBNull.Value, -1);
        cmd.AddParameter("@pattern", DbType.String, (object?)pattern ?? DBNull.Value, 256);
        cmd.AddParameter("@start", DbType.DateTime2, (object?)start ?? DBNull.Value);
        cmd.AddParameter("@now", DbType.DateTime2, now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteAsync(DataSession s, IEnumerable<Job> jobs, CancellationToken ct)
    {
        foreach (var job in jobs)
        {
            await using var cmd = s.Command("DELETE FROM data.asyncoperation WHERE asyncoperationid = @id");
            cmd.AddParameter("@id", DbType.Guid, job.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The SYSTEM user of the organization (its id derives from the organization's name) and its business unit.</summary>
    private async Task<Caller> SystemAsync(DataSession s, CancellationToken ct)
    {
        if (_system is { } cached) return cached;
        await using var cmd = s.Command(s.Dialect.Limit("SELECT name FROM data.organization", "1"));
        var name = await cmd.ExecuteScalarAsync(ct) as string
                   ?? throw new InvalidOperationException("The organization has not been seeded (data.organization is empty).");
        var id = SeedOptions.DeriveSystemUserId(name);
        await using var user = s.Command("SELECT businessunitid FROM data.systemuser WHERE systemuserid = @id");
        user.AddParameter("@id", DbType.Guid, id);
        var businessUnit = await user.ExecuteScalarAsync(ct) as Guid?
                           ?? throw new InvalidOperationException($"The SYSTEM user {id} of the organization does not exist.");
        _system = new Caller(id, businessUnit);
        return _system;
    }

    // ---- metadata ---------------------------------------------------------------------------------------------------------

    private bool IsRollup(string entity, string attribute) =>
        store.GetEntity(entity)?.Attributes?.FirstOrDefault(a => Same(a.LogicalName, attribute)) is { } a && RollupColumns.IsRollup(a);

    private string? StampOf(string entity, string attribute) =>
        RollupJobData.Stamp(store.GetEntity(entity)?.Attributes?.FirstOrDefault(a => Same(a.LogicalName, attribute)));

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    private bool HasRollups(string entity) => store.GetEntity(entity)?.Attributes?.Any(RollupColumns.IsRollup) == true;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
