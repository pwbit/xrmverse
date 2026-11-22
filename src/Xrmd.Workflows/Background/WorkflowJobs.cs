using System.Data;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Workflows.Engine;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows.Background;

/// <summary>
/// The background workflow jobs (plan 2.14, rulings W4, W9, W10 and W12): queueing a job of an activation (triggered or a child), and
/// what ends jobs from outside their run: the delete of their activation (3/32), the delete of the record their wait reads (3/31), a
/// client's cancel; and the delete of a kept activation (W4) once its last job has ended.
/// </summary>
/// <remarks>Never depends on the <see cref="PipelineExecutor"/> (the pipeline hook uses it: executor → hook → this).</remarks>
public sealed class WorkflowJobs(AsyncOperationQueue queue, WorkflowRegistry registry, WorkflowStore store, WorkflowWaits waits,
    IDbConnectionFactory factory)
{
    private const int Running = 2, Completed = 3, Failed = 31, Canceled = 32;
    internal const string AsyncOperation = "asyncoperation";

    /// <summary><c>message</c> of a job whose workflow was deleted.</summary>
    public const string WorkflowDeletedMessage = "The workflow was deleted.";

    /// <summary><c>message</c> of a job whose wait reads a record that was deleted (W10, checklist 1e).</summary>
    public const string WaitRecordDeletedMessage = "The record of the wait was deleted.";

    /// <summary>
    /// Queues the job of <paramref name="activation"/> on <paramref name="primary"/> in <paramref name="s"/> (the triggering
    /// transaction, W9); the worker is woken once it commits.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when the workflow is not owned by a user (a job runs as its owner, W7).</exception>
    public async Task<Guid> QueueAsync(DataSession s, WorkflowRow activation, EntityReference primary, string messageName, Guid createdBy,
        int depth, CancellationToken ct, Guid? runAs = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(primary);
        var name = activation.Name ?? activation.Id.ToString();
        // runAs (an on-demand start, W21): the job is the starting user's and runs as that user; else the workflow owner's.
        var ownerId = runAs ?? (activation.OwnerId is { LogicalName: "systemuser" } owner
            ? owner.Id
            : throw XrmdFaultException.InvalidArgument(
                $"Workflow '{name}' cannot be queued: it is owned by a {activation.OwnerId?.LogicalName ?? "nobody"}; xrmverse runs a background workflow only as a user."));
        var id = await queue.EnqueueWorkflowAsync(s, new WorkflowJobRequest(activation.Id, name, messageName, primary, ownerId, createdBy,
            depth, WorkflowJobState.Start(activation.Id, primary).Serialize()), ct);
        if (s.Transaction is null) queue.Signal();
        else s.OnCommitted(queue.Signal);
        return id;
    }

    /// <summary>
    /// "Start child workflow" (W12): queues a background job of the target's activation on <paramref name="record"/>, one level deeper
    /// than the step's request. The target (its definition or activation id) must be Activated, a workflow (category 0) and a child
    /// process (<c>subprocess</c>), with <paramref name="record"/>'s table as its primary entity. A real-time or action run queues it in
    /// its transaction; a background run in a transaction of its own.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the reason the child cannot start; <c>InfiniteLoopDetected</c> above depth 8.</exception>
    public async Task<Guid> QueueChildAsync(Guid workflowId, EntityReference record, WorkflowRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(run);
        var parent = run.Workflow.Name;
        var (row, _) = (await registry.GetActivatedAsync(ct)).FirstOrDefault(a => a.Row.Id == workflowId || a.Row.ParentWorkflowId == workflowId) is
            { Row: not null } found
            ? found
            : throw XrmdFaultException.InvalidArgument($"Workflow '{parent}': the child workflow {workflowId} is not activated.");
        var child = row.Name ?? row.Id.ToString();
        if (row.Category != (int)S.Category.Workflow || !row.Subprocess)
            throw XrmdFaultException.InvalidArgument(
                $"Workflow '{parent}': the workflow '{child}' cannot start as a child workflow: it is not available to run as a child process (subprocess).");
        if (!string.Equals(row.PrimaryEntity, record.LogicalName, StringComparison.OrdinalIgnoreCase))
            throw XrmdFaultException.InvalidArgument(
                $"Workflow '{parent}': the child workflow '{child}' runs on '{row.PrimaryEntity}', not on the '{record.LogicalName}' record it was given.");
        var depth = run.Invocation.Depth + 2; // the step's request is one level deeper than the run, the child one more
        if (depth > PipelineExecutor.MaxDepth) throw InfiniteLoop();

        if (run.Invocation.Session is { } session)
            return await QueueAsync(session, row, record, run.MessageName ?? "", run.Caller.UserId, depth, ct);
        await using var conn = await factory.OpenAsync(ct);
        return await QueueAsync(new DataSession(conn, null), row, record, run.MessageName ?? "", run.Caller.UserId, depth, ct);
    }

    /// <summary>The fault of a run deeper than <see cref="PipelineExecutor.MaxDepth"/> (the pipeline's text, already worded for workflows).</summary>
    internal static XrmdFaultException InfiniteLoop() =>
        new(ErrorCodes.InfiniteLoopDetected,
            "This workflow job was canceled because the workflow that started it included an infinite loop. Correct the workflow logic and try again.",
            400);

    /// <summary>
    /// A core write of a record, in its transaction (W4, W10; <see cref="WorkflowWriteObserver"/>): a write wakes the jobs waiting on
    /// the record (<paramref name="id"/> null: on any row of the table); its delete fails them (3/31); the delete of a <c>workflow</c>
    /// row cancels (3/32) the jobs of that activation that have not ended; a client's cancel of a workflow job removes its wait and,
    /// when it was the last job of a kept activation, that activation.
    /// </summary>
    public async Task OnRecordWrittenAsync(DataSession s, string entity, Guid? id, bool deleted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(entity);
        if (id == Guid.Empty) return;
        if (!deleted && entity == AsyncOperation && id is { } written)
        {
            await OnJobWrittenAsync(s, written, ct);
            return;
        }
        if (!deleted)
        {
            // The woken jobs are ready once the write commits: the worker is told then.
            if (await waits.WakeAsync(s, entity, id, ct) > 0)
            {
                if (s.Transaction is null) queue.Signal();
                else s.OnCommitted(queue.Signal);
            }
            return;
        }
        var record = id!.Value;
        if (entity == AsyncOperation) await waits.RemoveAsync(s, record, ct); // a deleted job's own wait goes with it
        if (entity == S.Table) await CancelJobsOfAsync(s, record, ct);
        var jobs = await waits.RemoveForRecordAsync(s, entity, record, ct);
        if (jobs.Count == 0) return;
        foreach (var activation in await EndJobsAsync(s, jobs, Failed, WaitRecordDeletedMessage, ErrorCodes.InvalidArgument, ct))
            await ReleaseActivationAsync(s, activation, null, ct);
        // A job of these running now (woken earlier) may suspend on the record again: it runs at once then, and meets the delete.
        await waits.WakeRunningAsync(s, jobs, entity, record, ct);
    }

    /// <summary>A job a client changed: when it is a completed workflow job (a cancel), its wait and its kept activation go.</summary>
    private async Task OnJobWrittenAsync(DataSession s, Guid job, CancellationToken ct)
    {
        // On the path of every client update of a job (P11), so in the dialect's SQL (plan 2.7a, Task 6).
        await using var cmd = s.Command(
            $"SELECT statecode, {s.Dialect.Quote(S.WorkflowActivationId)} FROM {s.DataTable(AsyncOperation)} WHERE asyncoperationid = @id AND operationtype = {S.WorkflowOperationType}");
        cmd.AddParameter("@id", DbType.Guid, job);
        int? state = null;
        Guid? activation = null;
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct))
            {
                state = rd.IsDBNull(0) ? null : rd.GetInt32(0);
                activation = rd.IsDBNull(1) ? null : rd.GetGuid(1);
            }
        if (state != Completed) return;
        await waits.RemoveAsync(s, job, ct);
        if (activation is { } a) await ReleaseActivationAsync(s, a, null, ct);
    }

    /// <summary>
    /// The jobs that have not started or wait, of the activation <paramref name="activationId"/> or of the activations of the definition
    /// <paramref name="activationId"/>, end Canceled (W4). A job running now gets a wait that is ready at once
    /// (<see cref="WorkflowWaits.WakeRunningAsync"/>): if it suspends, it runs again and finds its workflow gone (3/32).
    /// </summary>
    private async Task CancelJobsOfAsync(DataSession s, Guid activationId, CancellationToken ct)
    {
        var jobs = new List<Guid>();
        var running = new List<Guid>();
        var d = s.Dialect;
        await using (var cmd = s.Command(
                         $"SELECT asyncoperationid, statecode FROM data.asyncoperation{d.LockHint(RowLock.Update)} WHERE operationtype = {S.WorkflowOperationType} " +
                         $"AND statecode IN (0, 1, 2) AND {s.Quote(S.WorkflowActivationId)} IN (SELECT @id UNION ALL SELECT {s.Quote(S.WorkflowId)} " +
                         $"FROM {s.DataTable(S.Table)} WHERE {s.Quote(S.ParentWorkflowId)} = @id){d.LockClause(RowLock.Update)}"))
        {
            cmd.AddParameter("@id", DbType.Guid, activationId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) (rd.GetInt32(1) == Running ? running : jobs).Add(rd.GetGuid(0));
        }
        // Plan 2.7b, final review M9: on PostgreSQL this takes the jobs' asyncoperation row locks (above), then the key locks of
        // their waits (WakeRunningAsync): row, then key. No path takes a wait's key and then waits for its job's asyncoperation row
        // lock (a registration or a wake holds the key over meta.workflow_wait alone), so the order has no opposite to meet.
        if (running.Count > 0) await waits.WakeRunningAsync(s, running, S.Table, activationId, ct);
        if (jobs.Count == 0) return;
        foreach (var job in jobs) await waits.RemoveAsync(s, job, ct);
        await EndJobsAsync(s, jobs, Canceled, WorkflowDeletedMessage, null, ct);
    }

    /// <summary>Ends the suspended or ready jobs among <paramref name="jobs"/>; returns their activations.</summary>
    private static async Task<IReadOnlyList<Guid>> EndJobsAsync(DataSession s, IReadOnlyList<Guid> jobs, int status, string message, int? errorCode,
        CancellationToken ct)
    {
        var activations = new HashSet<Guid>();
        foreach (var job in jobs)
        {
            // The OUTPUT line of SQL Server, the trailing RETURNING of PostgreSQL (the SQL Server text is unchanged).
            string[] activation = [s.Dialect.Quote(S.WorkflowActivationId)];
            var output = s.Dialect.ReturningInline(activation) is { Length: > 0 } inline ? "\n" + inline.TrimStart() : "";
            await using var cmd = s.Command($"""
                UPDATE data.asyncoperation
                SET statecode = {Completed}, statuscode = @status, message = @message, errorcode = @errorcode, completedon = @now,
                    modifiedon = @now, postponeuntil = NULL{output}
                WHERE asyncoperationid = @id AND statecode IN (0, 1){s.Dialect.ReturningTrailing(activation)}
                """);
            cmd.AddParameter("@id", DbType.Guid, job);
            cmd.AddParameter("@status", DbType.Int32, status);
            cmd.AddParameter("@message", DbType.String, message, -1);
            cmd.AddParameter("@errorcode", DbType.Int32, (object?)errorCode ?? DBNull.Value);
            cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct) && !rd.IsDBNull(0)) activations.Add(rd.GetGuid(0));
        }
        return activations.ToList();
    }

    /// <summary>
    /// Deletes the activation <paramref name="activationId"/> when it was kept for its jobs (W4: its definition no longer has it as
    /// its activation) and no job of it other than <paramref name="exceptJob"/> has not ended.
    /// </summary>
    public async Task ReleaseActivationAsync(DataSession s, Guid activationId, Guid? exceptJob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var table = s.DataTable(S.Table);
        string Q(string column) => s.Quote(column);
        // The NOT EXISTS reads the jobs with an update lock. On SQL Server (UPDLOCK, HOLDLOCK) it also holds the key range, so a job
        // of this activation inserted later waits for this transaction. On PostgreSQL the subquery's FOR UPDATE locks only the jobs
        // it finds (a found job cannot change state meanwhile); it does not lock a missing one, so a job inserted concurrently is
        // not seen and does not wait.
        await using var cmd = s.Command($"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM {table} a
                LEFT JOIN {table} d ON d.{Q(S.WorkflowId)} = a.{Q(S.ParentWorkflowId)}
                WHERE a.{Q(S.WorkflowId)} = @id AND a.{Q(S.TypeColumn)} = {(int)S.Type.Activation}
                    AND (d.{Q(S.WorkflowId)} IS NULL OR d.{Q(S.ActiveWorkflowId)} IS NULL OR d.{Q(S.ActiveWorkflowId)} <> a.{Q(S.WorkflowId)})
                    AND NOT EXISTS (SELECT 1 FROM data.asyncoperation j{s.Dialect.LockHint(RowLock.UpdateRange)}
                        WHERE j.{Q(S.WorkflowActivationId)} = a.{Q(S.WorkflowId)} AND j.statecode <> {Completed}
                            AND (@except IS NULL OR j.asyncoperationid <> @except){s.Dialect.LockClause(RowLock.UpdateRange)}))
            THEN 1 ELSE 0 END
            """);
        cmd.AddParameter("@id", DbType.Guid, activationId);
        cmd.AddParameter("@except", DbType.Guid, (object?)exceptJob ?? DBNull.Value);
        if ((int)(await cmd.ExecuteScalarAsync(ct))! == 1) await store.DeleteAsync(s, activationId, ct);
    }
}
