using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.Async;
using Xrmd.Query.Sql;

namespace Xrmd.Core.Rollups;

/// <summary>
/// Runs the Mass Calculate Rollup Field job (operationtype 58) of one rollup column (plan 2.12b, ruling R8): recalculates the column on
/// every row of its table (<see cref="RollupScope.All"/>, as the system: no plug-in, no audit, <c>modifiedon</c> untouched), stamps its
/// <c>completedOn</c> and sleeps about ten years (Learn), until the column's definition changes again. A job whose column is gone ends
/// with a message and is deleted. When the column's definition changed while it ran (the scheduler leaves a running job alone), the
/// run does not count: the job waits <see cref="RollupOptions.MassCalculationDelay"/> again, as after any update, with no
/// <c>completedOn</c>. A transient database failure (<see cref="RollupJobFailures"/>) does not end the job either: it is logged and the
/// job runs again after <see cref="RollupOptions.TransientRetryDelay"/>, its data unchanged (final review I2).
/// </summary>
public sealed class MassCalculateRollupJob(RollupCalculator calculator, IMetadataStore store, RollupOptions? options = null,
    ILogger<MassCalculateRollupJob>? logger = null) : IAsyncOperationHandler
{
    private readonly RollupOptions _options = options ?? new RollupOptions();
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>A test seam: run after the recalculation and before the job compares the definition.</summary>
    internal static readonly AsyncLocal<Func<Task>?> AfterRecalculate = new();

    /// <summary>How long a Mass job sleeps after its run (Learn: about ten years).</summary>
    public static readonly int SleepYears = 10;

    public int OperationType => AsyncOperationQueue.MassCalculateRollupField;

    /// <summary>A recurring job: one interrupted by the host stopping runs again from its data.</summary>
    public bool RestartsWhenInterrupted => true;

    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (RollupJobData.Parse(run.Data) is not { Attribute: { } attribute } data)
            throw XrmdFaultException.InvalidArgument("The Mass Calculate Rollup Field job names no rollup column (data).");
        if (store.GetEntity(data.Entity)?.Attributes?.FirstOrDefault(a => a.LogicalName == attribute) is not { } column
            || !RollupColumns.IsRollup(column))
        {
            run.CompletionMessage = $"The rollup column '{data.Entity}.{attribute}' no longer exists: its calculation job is removed.";
            run.DeleteOnSuccess = true;
            return;
        }

        var stamp = RollupJobData.Stamp(column);
        try
        {
            await calculator.RecalculateAsync(data.Entity, attribute, new RollupScope.All(), ct);
        }
        catch (Exception ex) when (RollupJobFailures.IsTransient(ex, ct))
        {
            _logger.LogWarning(ex, "The calculation of the rollup column {Entity}.{Attribute} hit a transient database failure; it runs again in {Delay}.",
                data.Entity, attribute, _options.TransientRetryDelay);
            run.Suspend(run.Now + _options.TransientRetryDelay, data.ToJson());
            return;
        }
        if (AfterRecalculate.Value is { } hook) await hook();

        var current = store.GetEntity(data.Entity)?.Attributes?.FirstOrDefault(a => a.LogicalName == attribute);
        if (current is not null && RollupColumns.IsRollup(current) && RollupJobData.Stamp(current) != stamp)
        {
            // The definition changed during the run: the values may be the old definition's; the job starts over after the delay.
            run.Suspend(run.Now + _options.MassCalculationDelay,
                (data with { CompletedOnUtc = null, Definition = RollupJobData.Stamp(current) }).ToJson());
            return;
        }
        run.Suspend(run.Now.AddYears(SleepYears), (data with { CompletedOnUtc = run.Now.UtcDateTime, Definition = stamp }).ToJson());
    }
}
