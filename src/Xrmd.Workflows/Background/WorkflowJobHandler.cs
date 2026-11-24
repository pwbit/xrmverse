using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Workflows.Engine;
using Xrmd.Workflows.Xaml;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows.Background;

/// <summary>
/// Runs the background workflow jobs (<c>asyncoperation</c> operationtype 10, plan 2.14, rulings W7, W9, W10, W12 and W20) for the
/// <see cref="AsyncOperationWorker"/>, as the job's owner (the workflow's owner). The run starts at the beginning, or at the step a
/// suspended run stopped at (<see cref="WorkflowJobState.ResumePath"/>, with its variables). Each step's request runs in a transaction
/// of its own (W9): a failing step fails the job (3/31) and the steps before it stay.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Completed: the job succeeds (3/30), or is deleted with <c>asyncautodelete</c>. A stop step that cancels: 3/32 with its message.</item>
/// <item>A wait: the job is suspended (1/10) with <c>postponeuntil</c> = the timeout or postpone date, and, when the condition reads a
/// record, a <c>meta.workflow_wait</c> row so that a write of the record wakes it (W10).</item>
/// <item>The activation no longer exists: 3/32 "The workflow was deleted." A deactivated definition's activation that a job still
/// uses is kept until its last job ends (W4); this handler deletes it then.</item>
/// <item>A job deeper than <see cref="PipelineExecutor.MaxDepth"/> (a chain of workflows that trigger each other) fails with the
/// pipeline's infinite loop fault before any step runs.</item>
/// </list>
/// The <see cref="PipelineExecutor"/> is the scope's: the handler is not part of the pipeline hook, so it closes no cycle.
/// </remarks>
public sealed class WorkflowJobHandler(PipelineExecutor executor, IMetadataStore metadata, WorkflowRegistry registry, WorkflowStore store,
    WorkflowWaits waits, WorkflowJobs jobs, IDbConnectionFactory factory, ILogger<WorkflowJobHandler>? logger = null,
    Custom.CustomActivityRunner? activities = null) : IAsyncOperationHandler
{
    /// <summary>
    /// A test seam: run after the engine has read a wait's condition and suspended, before the wait is registered (the window a
    /// write of the record can fall into).
    /// </summary>
    internal static readonly AsyncLocal<Func<CancellationToken, Task>?> BeforeRegister = new();

    /// <summary>A test seam: run once the suspension is decided and checked, before the worker records it (the job is still 2/20).</summary>
    internal static readonly AsyncLocal<Func<CancellationToken, Task>?> AfterRegister = new();

    /// <summary>A test seam: run at the start of the clean-up after the job ended (<see cref="EndAsync"/>).</summary>
    internal static readonly AsyncLocal<Func<Task>?> BeforeEnd = new();

    /// <summary>A test seam: run once the clean-up after the job ended is done, before the worker records the outcome.</summary>
    internal static readonly AsyncLocal<Func<Guid, Task>?> AfterEnd = new();

    public int OperationType => S.WorkflowOperationType;

    /// <summary>
    /// An interrupted run starts again from the job's last saved state (its start, or its last wait): the steps since then run
    /// again (at least once, as after a crash: <see cref="AsyncOperationWorker.RecoverAsync"/>).
    /// </summary>
    public bool RestartsWhenInterrupted => true;

    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var state = WorkflowJobState.Parse(run.Data ?? throw XrmdFaultException.InvalidArgument("The workflow job has no data."));
        var suspended = false;
        var interrupted = false;
        try
        {
            var (depth, messageName, correlationId, token, seen) = await JobAsync(run.AsyncOperationId, ct);
            if (depth > PipelineExecutor.MaxDepth) throw WorkflowJobs.InfiniteLoop();
            var (row, definition) = await LoadAsync(state.ActivationId, ct)
                                    ?? throw new AsyncOperationCanceledException(WorkflowJobs.WorkflowDeletedMessage);

            var workflowRun = new WorkflowRun
            {
                Workflow = row,
                Primary = state.Primary,
                Caller = run.Owner,
                // W9: no session, so each step's request runs in its own transaction, at the job's depth.
                Invocation = new InvocationContext(run.Owner, run.InitiatingUserId, depth - 1, null, null, correlationId),
                Stage = 0,
                MessageName = messageName,
                Now = run.Now.UtcDateTime,
                ResumePath = state.ResumePath,
            };
            state.RestoreInto(workflowRun.Variables);

            switch (await new WorkflowEngine(executor, metadata, jobs, activities).RunAsync(definition, workflowRun, ct))
            {
                case WorkflowOutcome.Completed:
                    run.DeleteOnSuccess = row.AsyncAutoDelete;
                    return;
                case WorkflowOutcome.Canceled canceled:
                    throw new AsyncOperationCanceledException(canceled.Message.Length > 0
                        ? canceled.Message
                        : $"The workflow '{row.Name}' was canceled by its stop step.");
                case WorkflowOutcome.Suspended wait:
                    {
                        var next = state with
                        {
                            ResumePath = wait.ResumePath,
                            Variables = WorkflowJobState.ToJson(workflowRun.Variables, row.Name ?? row.Id.ToString()),
                        };
                        if (BeforeRegister.Value is { } hook) await hook(ct);
                        await using var conn = await factory.OpenAsync(ct);
                        var s = new DataSession(conn, null);
                        if (wait.WaitRecord is { } record)
                        {
                            await waits.RegisterAsync(s, run.AsyncOperationId, record, wait.Until,
                                seen is { } known && SameRecord(known.Record, record) ? known.Version : null, ct);
                            // A write of the record after the condition read, but before the registration was visible, found no wait
                            // to wake: the row's version tells (W10).
                            if (!await waits.RecheckAsync(s, run.AsyncOperationId, record, metadata.GetEntity(record.LogicalName), token, ct))
                            {
                                await waits.RemoveAsync(s, run.AsyncOperationId, ct);
                                throw XrmdFaultException.InvalidArgument(WorkflowJobs.WaitRecordDeletedMessage);
                            }
                        }
                        // W4: a workflow deleted while this run went on canceled the jobs that were not running; this one ends the same
                        // way rather than wait for nothing. A delete after this check wakes the suspension (WorkflowJobs.CancelJobsOfAsync).
                        if (!await ActivationExistsAsync(s, state.ActivationId, ct))
                        {
                            await waits.RemoveAsync(s, run.AsyncOperationId, ct);
                            throw new AsyncOperationCanceledException(WorkflowJobs.WorkflowDeletedMessage);
                        }
                        if (AfterRegister.Value is { } after) await after(ct);
                        run.Suspend(wait.Until, next.Serialize());
                        suspended = true;
                        return;
                    }
                default:
                    throw XrmdFaultException.InvalidArgument($"Workflow '{row.Name}' ended in a way xrmverse does not support.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            interrupted = true; // the job goes back to the queue: it has not ended
            // A wait it registered goes: its checks may not have run, and the job is ready again from its saved state.
            try
            {
                await using var conn = await factory.OpenAsync(CancellationToken.None);
                await waits.RemoveAsync(new DataSession(conn, null), run.AsyncOperationId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogError(ex, "The wait of interrupted workflow job {JobId} could not be removed.", run.AsyncOperationId);
            }
            throw;
        }
        finally
        {
            if (!suspended && !interrupted)
            {
                // The job's outcome stands whatever happens here (its own error is not replaced by the clean-up's).
                try
                {
                    await EndAsync(run.AsyncOperationId, state.ActivationId);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "The clean-up after workflow job {JobId} failed.", run.AsyncOperationId);
                }
            }
        }
    }

    private static bool SameRecord(EntityReference a, EntityReference b) =>
        a.Id == b.Id && string.Equals(a.LogicalName, b.LogicalName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The job's <c>depth</c>, <c>messagename</c> and <c>correlationid</c>, and what its run will have seen before it reads anything:
    /// <c>MIN_ACTIVE_ROWVERSION()</c> (a write the run cannot see has a version at least this) and, when it resumes a wait on a record,
    /// that record's current version (a later write has a greater one). Its wait (it runs now) is removed.
    /// </summary>
    private async Task<(int Depth, string? MessageName, Guid CorrelationId, long Token, (EntityReference Record, long Version)? Seen)> JobAsync(
        Guid id, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var s = new DataSession(conn, null);
        (EntityReference, long)? seen = null;
        if (await waits.TakeAsync(s, id, ct) is { } taken
            && await WorkflowWaits.VersionOfAsync(s, taken, metadata.GetEntity(taken.LogicalName), ct) is { } version)
            seen = (taken, version);
        await using var cmd = s.Command(
            $"SELECT depth, messagename, correlationid, {s.Dialect.RowVersionStart} FROM data.asyncoperation WHERE asyncoperationid = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw new AsyncOperationCanceledException("The system job was deleted.");
        return (rd.IsDBNull(0) ? 1 : rd.GetInt32(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? Guid.NewGuid() : rd.GetGuid(2),
            rd.GetInt64(3), seen);
    }

    /// <summary>
    /// The activation and its step tree: from the registry while the definition is activated, else from the stored row (a deactivated
    /// definition's activation kept for its jobs, W4). Null when it no longer exists. XAML that no longer parses gives a definition whose
    /// only step cannot run, so the job fails explicitly (W20).
    /// </summary>
    private async Task<(WorkflowRow Row, WorkflowDefinition Definition)?> LoadAsync(Guid activationId, CancellationToken ct)
    {
        if (await registry.FindAsync(activationId, ct) is { } activated) return activated;
        await using var conn = await factory.OpenAsync(ct);
        if (await store.GetAsync(new DataSession(conn, null), activationId, ct) is not { } row) return null;
        try
        {
            return (row, XamlWorkflowParser.Parse(row.Xaml ?? throw new XamlWorkflowException("the activation has no XAML"), row.Category));
        }
        catch (XamlWorkflowException ex)
        {
            return (row, WorkflowRegistry.Unparsable(row, ex.Message));
        }
    }

    /// <summary>
    /// The activation still exists (read with <c>READCOMMITTEDLOCK</c>, <c>FOR SHARE</c> on PostgreSQL: a delete in flight is waited
    /// for). Plan 2.7b, Task 7: the row is read, not counted (PostgreSQL locks no aggregate).
    /// </summary>
    private static async Task<bool> ActivationExistsAsync(DataSession s, Guid activationId, CancellationToken ct)
    {
        var d = s.Dialect;
        await using var cmd = s.Command(
            $"SELECT 1 FROM {s.DataTable(S.Table)}{d.LockHint(RowLock.ReadCommittedLock)} WHERE {s.Quote(S.WorkflowId)} = @id{d.LockClause(RowLock.ReadCommittedLock)}");
        cmd.AddParameter("@id", DbType.Guid, activationId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// The job has ended: a wait a delete gave it while it ran (<see cref="WorkflowWaits.WakeRunningAsync"/>) goes, and so does a kept
    /// activation whose last job this was (W4).
    /// </summary>
    private async Task EndAsync(Guid job, Guid activationId)
    {
        if (BeforeEnd.Value is { } hook) await hook();
        await using var conn = await factory.OpenAsync(CancellationToken.None);
        var s = new DataSession(conn, null);
        await waits.RemoveAsync(s, job, CancellationToken.None);
        await jobs.ReleaseActivationAsync(s, activationId, job, CancellationToken.None);
        if (AfterEnd.Value is { } after) await after(job);
    }
}
