using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;
using Xrmd.Pipeline.Async;
using Xrmd.Query.Sql;

namespace Xrmd.Core.Rollups;

/// <summary>
/// Runs the Calculate Rollup Field job (operationtype 57) of one table (plan 2.12b, ruling R8): the incremental recalculation of each
/// rollup column of the table whose Mass Calculate Rollup Field job has run (<c>completedOn</c> set), on the rows whose value or state
/// would change and the rows touched since the previous run (<see cref="RollupScope.ChangedSince"/>), as the system. A column that fails
/// is logged and the others still run. The job then sleeps until the next occurrence of its <c>recurrencepattern</c> counted from its
/// <c>recurrencestarttime</c> (<see cref="RollupRecurrence"/>). A job whose table has no rollup left ends with a message and is deleted.
/// When its own reads hit a transient database failure (<see cref="RollupJobFailures"/>) the job runs again after
/// <see cref="RollupOptions.TransientRetryDelay"/>, its data unchanged (final review I2).
/// </summary>
public sealed class CalculateRollupJob(RollupCalculator calculator, IMetadataStore store, IDbConnectionFactory factory,
    ILogger<CalculateRollupJob>? logger = null, RollupOptions? options = null) : IAsyncOperationHandler
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly RollupOptions _options = options ?? new RollupOptions();

    public int OperationType => AsyncOperationQueue.CalculateRollupField;

    /// <summary>A recurring job: one interrupted by the host stopping runs again from its data.</summary>
    public bool RestartsWhenInterrupted => true;

    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var data = RollupJobData.Parse(run.Data)
                   ?? throw XrmdFaultException.InvalidArgument("The Calculate Rollup Field job names no table (data).");
        var rollups = store.GetEntity(data.Entity)?.Attributes?.Where(RollupColumns.IsRollup).Select(a => a.LogicalName).ToList() ?? [];
        if (rollups.Count == 0)
        {
            run.CompletionMessage = $"The table '{data.Entity}' has no rollup column left: its calculation job is removed.";
            run.DeleteOnSuccess = true;
            return;
        }

        string? pattern;
        DateTime? start;
        Dictionary<string, DateTime> massRuns;
        try
        {
            (pattern, start, massRuns) = await ReadAsync(run.AsyncOperationId, data.Entity, ct);
        }
        catch (Exception ex) when (RollupJobFailures.IsTransient(ex, ct))
        {
            _logger.LogWarning(ex, "The rollup calculation job of {Entity} hit a transient database failure; it runs again in {Delay}.",
                data.Entity, _options.TransientRetryDelay);
            run.Suspend(run.Now + _options.TransientRetryDelay, run.Data!);
            return;
        }
        foreach (var rollup in rollups)
        {
            // Ruling R8: a column whose Mass job has not run yet keeps its values (state 0) until it does.
            if (!massRuns.TryGetValue(rollup, out var massRun)) continue;
            // The rows touched since the previous run of this job, or since the Mass run when this job has not covered the column since.
            var since = data.LastRunStartedUtc is { } last && massRun <= last ? last : massRun;
            try
            {
                await calculator.RecalculateAsync(data.Entity, rollup, new RollupScope.ChangedSince(since), ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "The incremental calculation of the rollup column {Entity}.{Attribute} failed; it is tried again at the next run.",
                    data.Entity, rollup);
            }
        }

        var now = run.Now.UtcDateTime;
        DateTime next;
        try
        {
            next = RollupRecurrence.Next(pattern ?? RollupRecurrence.Default, start ?? now, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever is wrong with the stored pattern or start time, the job keeps recurring: hourly from now.
            _logger.LogWarning(ex, "The recurrence of the rollup calculation job {JobId} cannot be used; it runs hourly.", run.AsyncOperationId);
            next = RollupRecurrence.Next(RollupRecurrence.Default, now, now);
        }
        run.Suspend(new DateTimeOffset(next, TimeSpan.Zero), (data with { LastRunStartedUtc = now }).ToJson());
    }

    /// <summary>The job's recurrence and start time, and the last run of the Mass job of each rollup of <paramref name="entity"/>.</summary>
    private async Task<(string? Pattern, DateTime? Start, Dictionary<string, DateTime> MassRuns)> ReadAsync(Guid id, string entity, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var session = new DataSession(conn, null);
        string? pattern = null;
        DateTime? start = null;
        await using (var cmd = session.Command("SELECT recurrencepattern, recurrencestarttime FROM data.asyncoperation WHERE asyncoperationid = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                pattern = rd.IsDBNull(0) ? null : rd.GetString(0);
                start = rd.IsDBNull(1) ? null : DateTime.SpecifyKind(rd.GetDateTime(1), DateTimeKind.Utc);
            }
        }

        var runs = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = session.Command(
                         "SELECT data FROM data.asyncoperation WHERE operationtype = @mass AND primaryentitytype = @entity AND statecode <> 3"))
        {
            cmd.AddParameter("@mass", DbType.Int32, AsyncOperationQueue.MassCalculateRollupField);
            cmd.AddParameter("@entity", DbType.String, entity, 64);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                if (RollupJobData.Parse(rd.IsDBNull(0) ? null : rd.GetString(0)) is { Attribute: { } attribute, CompletedOnUtc: { } completed })
                    runs[attribute] = DateTime.SpecifyKind(completed, DateTimeKind.Utc);
        }
        return (pattern, start, runs);
    }
}
