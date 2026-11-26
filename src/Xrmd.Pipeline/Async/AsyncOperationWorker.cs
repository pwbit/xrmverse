using System.Data;
using System.Data.Common;
using System.Runtime.Serialization;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Sandbox;

namespace Xrmd.Pipeline.Async;

/// <summary>
/// Runs the queued <c>asyncoperation</c> jobs of asynchronous plug-in steps, one at a time, until the host stops. A job is
/// claimed atomically to Locked/In Progress by one statement (a CTE selecting the oldest ready row <c>TOP(1) … WITH
/// (UPDLOCK, READPAST, ROWLOCK) ORDER BY createdon</c>, updated with <c>OUTPUT</c>), so concurrent claims never take the
/// same job. Outcomes (statecode/statuscode): success 3/30; <see cref="InvalidPluginExecutionException"/>
/// (or an organization service fault the plug-in let through) 3/31 without retry; any other failure is retried
/// <see cref="PipelineOptions.AsyncMaxRetries"/> times (Suspended/Waiting 1/10 with
/// <c>postponeuntil = now + AsyncRetryDelay * 2^(retrycount-1)</c>) and then fails 3/31; a missing or unreadable payload
/// fails 3/31 at once; a deleted or disabled step cancels the job 3/32. A job the user cancelled (3/32) is never taken.
/// A job runs as the owner of its row (<c>ownerid</c>, with the owner's current business unit; the initiating user is
/// <c>createdby</c>), never as the users its payload names; an owner that does not exist or is disabled fails the job 3/31
/// with <see cref="OwnerUnavailableMessage"/> before any plug-in runs, and a step whose impersonating user does not exist
/// or is disabled fails it 3/31 at once (no retry) with that error. The payload is read through the
/// <see cref="Serialization.SdkTypeAllowlist"/>: a type outside it fails the job 3/31 at once.
/// Ready jobs (0, or 1/10 once postponeuntil has passed) are taken oldest <c>createdon</c> first.
/// A job of another operation type (e.g. a queued solution import, 203) runs with the <see cref="IAsyncOperationHandler"/>
/// registered for that type, as the owner of its row and without retry (see <see cref="RunHandlerJobAsync"/>).
/// </summary>
/// <remarks>
/// Each job runs in a new DI scope with the step as it is now (<see cref="StepRegistry"/>), on a connection without a
/// transaction: every call of the plug-in's organization service runs in a transaction of its own. A step job interrupted by
/// the host stopping goes back to Ready (0/0); a handler job (e.g. a queued solution import) interrupted the same way ends
/// Failed (3/31) with <see cref="InterruptedMessage"/>, because its work cannot be started over (an interrupted import has
/// already written its failed <c>importjob</c>), unless its handler can start it over from its data
/// (<see cref="IAsyncOperationHandler.RestartsWhenInterrupted"/>, a background workflow): then it goes back to Ready too. On its first successful iteration the worker also puts back to Ready every job
/// left Locked/In Progress (2/20) by a previous process (<see cref="RecoverAsync"/>), handler jobs included: for a solution
/// import the crashed process committed nothing unless it crashed between writing the <c>importjob</c> (the import's commit,
/// or the failed log) and recording the outcome; then the re-run fails 3/31 with "An import job with id … already exists"
/// (explicit, but the job's first outcome is lost).
/// <para><b>Recovery relies on one xrmd process per database</b>: it cannot tell a job of a crashed process from one that
/// another live process is running. The host enforces it with its instance lock (the <c>xrmd-instance</c> applock, taken in
/// the organization database before any hosted service starts and held for the process lifetime): a second process on the
/// same database fails startup before this worker runs, and a process that loses the lock to another stops.</para>
/// The worker does nothing when <see cref="PipelineOptions.AsyncWorkerEnabled"/> is false (the host sets it so when <c>ConnectionStrings:Org</c> is not configured).
/// </remarks>
public sealed class AsyncOperationWorker(IServiceProvider services, AsyncOperationQueue queue, IOptions<PipelineOptions> options,
    ILogger<AsyncOperationWorker>? logger = null, TimeProvider? time = null) : BackgroundService
{
    private const int Ready = 0;
    private const int Suspended = 1;
    private const int Locked = 2;
    private const int Completed = 3;
    private const int WaitingForResources = 0;
    private const int Waiting = 10;
    private const int InProgress = 20;
    private const int Succeeded = 30;
    private const int Failed = 31;
    private const int Canceled = 32;
    private const int PostOperation = 40;
    private const int Asynchronous = 1;

    /// <summary><c>message</c> of a job whose step no longer exists.</summary>
    public const string StepDeletedMessage = "The plug-in step was deleted.";

    /// <summary><c>message</c> of a job whose step is disabled.</summary>
    public const string StepDisabledMessage = "The plug-in step is disabled.";

    /// <summary><c>message</c> of a job whose owner does not exist or is disabled.</summary>
    public const string OwnerUnavailableMessage = "The owner of the system job does not exist or is disabled.";

    /// <summary><c>message</c> of a job that is not a plug-in step, interrupted by the host stopping.</summary>
    public const string InterruptedMessage = "The system job was interrupted by the server stopping.";

    private readonly PipelineOptions _options = options.Value;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private bool _recovered;
    private bool _failing;

    internal sealed record Job(Guid Id, Guid? StepId, string? Data, int? Depth, int RetryCount, Guid? CorrelationId, Guid? OwnerId,
        Guid? CreatedBy, int? OperationType);

    /// <summary>What happened to a job: its final (or retry) state, status and error.</summary>
    /// <param name="ClearData">True to drop the row's <c>data</c> (a handler job's payload, e.g. a solution file) as it completes.</param>
    /// <param name="PostponeUntil">A suspended handler job (1/10): its <c>postponeuntil</c>.</param>
    /// <param name="NewData">A suspended handler job: its new <c>data</c>.</param>
    /// <param name="Delete">A succeeded job deleted instead of kept: a step with <c>asyncautodelete</c>, or a handler job's
    /// <see cref="AsyncOperationRun.DeleteOnSuccess"/>.</param>
    private sealed record Outcome(int State, int Status, string? Message, int? ErrorCode, bool Retry = false, bool ClearData = false,
        DateTimeOffset? PostponeUntil = null, string? NewData = null, bool Delete = false);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AsyncWorkerEnabled)
        {
            _logger.LogInformation("The asynchronous job worker is disabled.");
            return;
        }

        await Task.Yield(); // never hold up the host's startup
        while (!stoppingToken.IsCancellationRequested)
        {
            bool ran;
            try
            {
                // Recovery is retried at every iteration until it succeeds once (the database may be down at start).
                if (!_recovered)
                {
                    await RecoverAsync(stoppingToken);
                    _recovered = true;
                }
                ran = await RunOnceAsync(stoppingToken);
                if (_failing) _logger.LogInformation("The asynchronous job worker works again.");
                _failing = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The first failure with its stack; repeated ones (e.g. the database is down) briefly, until a success.
                if (!_failing) _logger.LogError(ex, "The asynchronous job worker failed; retrying every {Interval}.", _options.AsyncPollInterval);
                else _logger.LogDebug("The asynchronous job worker failed again: {Message}", ex.Message);
                _failing = true;
                ran = false;
            }

            if (ran) continue;
            try
            {
                await queue.WaitAsync(_options.AsyncPollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Claims one ready job and runs it to an outcome.</summary>
    /// <returns>True when a job was taken (whatever its outcome); false when none is ready.</returns>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var factory = services.GetRequiredService<IDbConnectionFactory>();
        // Migration plan M3 (R4): while the organization is locked for a migration, only the migration's own job runs.
        var onlyType = services.GetService<IMigrationLock>()?.ActiveRunId is null ? (int?)null : AsyncOperationQueue.MigrationImport;
        var job = await ClaimAsync(factory, _time.GetUtcNow().UtcDateTime, onlyType, ct);
        if (job is null) return false;

        if (job.OperationType is { } type && type != AsyncOperationQueue.SystemEvent)
        {
            Outcome handled;
            try
            {
                handled = await RunHandlerJobAsync(job, type, factory, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The host is stopping and the handler can start the job over: it goes back to the queue, with its data.
                await ReleaseAsync(factory, job.Id);
                throw;
            }
            // A completed handler job no longer needs its payload (a queued solution file can be large).
            await RecordAsync(factory, job, handled with { ClearData = true });
            return true;
        }

        Outcome outcome;
        try
        {
            outcome = await RunJobAsync(job, factory, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is stopping: the job goes back to the queue (it may run again from the start).
            await ReleaseAsync(factory, job.Id);
            throw;
        }
        catch (Exception ex) when (ex is SerializationException or XmlException)
        {
            // A missing or corrupt payload will not get better: no retry.
            _logger.LogWarning(ex, "Asynchronous job {JobId} has an unreadable execution context.", job.Id);
            outcome = Failure(ErrorCodes.Unexpected, $"The execution context of the job cannot be read: {ex.Message}");
        }
        catch (PluginExecutionException ex)
        {
            outcome = ex.Unexpected ? Retry(job, ex.Fault.ErrorCode, ex.Fault.Message) : Failure(ex.Fault.ErrorCode, ex.Fault.Message);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode is ErrorCodes.ObjectDoesNotExist or ErrorCodes.UserDisabled)
        {
            // The step's impersonating user does not exist or is disabled: no plug-in ran and a retry would not help.
            outcome = Failure(ex.ErrorCode, ex.Message);
        }
        catch (Exception ex)
        {
            // Not the plug-in itself (an unreadable payload, a database error...): retried like an unexpected plug-in error.
            _logger.LogWarning(ex, "Asynchronous job {JobId} failed outside its plug-in.", job.Id);
            outcome = Retry(job, ErrorCodes.Unexpected, ex.Message);
        }

        await RecordAsync(factory, job, outcome);
        return true;
    }

    /// <summary>How many times the outcome of a job is written before giving up.</summary>
    private const int OutcomeAttempts = 5;

    /// <summary>
    /// <see cref="CompleteAsync"/>, retried (<see cref="OutcomeAttempts"/> attempts, 0.2 s, 0.4 s… apart) when the database
    /// fails: the job ran, so a transient error must not leave it Locked / In Progress. When every attempt fails the job
    /// stays 2/20 until the next start puts it back to Ready (<see cref="RecoverAsync"/>), and the failure is logged.
    /// </summary>
    private async Task RecordAsync(IDbConnectionFactory factory, Job job, Outcome outcome)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await CompleteAsync(factory, job, outcome);
                return;
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException or TimeoutException)
            {
                if (attempt >= OutcomeAttempts)
                {
                    _logger.LogError(ex, "Could not record the outcome {State}/{Status} of asynchronous job {JobId} after {Attempts} attempts; " +
                                         "it stays In Progress until the next start.", outcome.State, outcome.Status, job.Id, attempt);
                    return;
                }

                _logger.LogWarning("Recording the outcome of asynchronous job {JobId} failed (attempt {Attempt}/{Attempts}): {Message}",
                    job.Id, attempt, OutcomeAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << (attempt - 1))), CancellationToken.None);
            }
        }
    }

    private Outcome Retry(Job job, int errorCode, string message) =>
        job.RetryCount + 1 <= _options.AsyncMaxRetries
            ? new Outcome(Suspended, Waiting, message, errorCode, Retry: true)
            : Failure(errorCode, message);

    private static Outcome Failure(int errorCode, string message) => new(Completed, Failed, message, errorCode);

    /// <summary>The <c>operationtype</c> of a workflow job (plan 2.14).</summary>
    private const int WorkflowOperationType = 10;

    /// <summary>
    /// A workflow job (operationtype 10) put back to Ready starts again from its saved state at once: the <c>postponeuntil</c> of
    /// the wait it was woken from (or that passed) is cleared, or it would sleep until that timeout (plan 2.14, W10).
    /// </summary>
    private const string WorkflowPostponeCleared = "postponeuntil = CASE WHEN operationtype = 10 THEN NULL ELSE postponeuntil END";

    /// <summary>
    /// Puts every job left Locked / In Progress (2/20) back to Ready / Waiting For Resources (0/0, <c>startedon</c> NULL):
    /// with one xrmd process per database (the host's instance lock), such a job was interrupted by a crash of a previous process. The worker loop
    /// calls it before its first job (and again until it succeeds once). A workflow job (operationtype 10) put back loses, in the same
    /// transaction, the <c>meta.workflow_wait</c> row its run may have registered before the crash (the worker had not yet recorded the
    /// suspension): the claim would otherwise wait for that row's <c>wake_at</c>, perhaps forever. The job runs again from its saved
    /// state and registers its wait again (plan 2.14, W10; final review I3).
    /// </summary>
    /// <returns>The number of jobs put back.</returns>
    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        await using var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
        // Plan 2.7a, Task 7: commands of their own in one transaction (was one T-SQL batch).
        await using var tx = await conn.BeginTransactionAsync(ct);
        var session = new DataSession(conn, tx);
        await using (var waits = session.Command(
                         "DELETE FROM meta.workflow_wait WHERE asyncoperationid IN (SELECT a.asyncoperationid FROM data.asyncoperation a " +
                         $"WHERE a.operationtype = {WorkflowOperationType} AND a.statecode = @locked AND a.statuscode = @inprogress)"))
        {
            waits.AddParameter("@locked", DbType.Int32, Locked);
            waits.AddParameter("@inprogress", DbType.Int32, InProgress);
            await waits.ExecuteNonQueryAsync(ct);
        }
        int count;
        await using (var cmd = session.Command(
                         "UPDATE data.asyncoperation SET statecode = @ready, statuscode = @waiting, startedon = NULL, modifiedon = @now, " +
                         WorkflowPostponeCleared + " WHERE statecode = @locked AND statuscode = @inprogress"))
        {
            cmd.AddParameter("@ready", DbType.Int32, Ready);
            cmd.AddParameter("@waiting", DbType.Int32, WaitingForResources);
            cmd.AddParameter("@locked", DbType.Int32, Locked);
            cmd.AddParameter("@inprogress", DbType.Int32, InProgress);
            cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
            count = await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (count > 0) _logger.LogWarning("Recovered {Count} asynchronous job(s) interrupted by a previous process.", count);
        return count;
    }

    /// <summary>
    /// Takes the oldest ready job (statecode 0, or Suspended/Waiting 1/10, and not postponed) to Locked / In Progress with
    /// <c>startedon</c>; READPAST skips jobs another worker is claiming. A workflow job waiting on a record (a row of
    /// <c>meta.workflow_wait</c>, plan 2.14 W10) is ready when its <c>wake_at</c> has come, whatever its <c>postponeuntil</c> (a write
    /// of the record wakes it before its timeout).
    /// </summary>
    /// <remarks>Internal for the claim test of two workers (plan 2.7b, Task 7).</remarks>
    internal static async Task<Job?> ClaimAsync(IDbConnectionFactory factory, DateTime now, int? onlyType, CancellationToken ct)
    {
        // Plan 2.7a, Task 7: the oldest ready job is chosen by a subquery that skips the rows another worker locked
        // (RowLock.UpdateSkipLocked: READPAST on SQL Server, SKIP LOCKED on PostgreSQL), then updated by its key.
        await using var conn = await factory.OpenAsync(ct);
        var d = SqlDialects.For(conn);
        string[] returned = ["asyncoperationid", "owningextensionid", "data", "depth", "retrycount", "correlationid", "ownerid", "createdby", "operationtype"];
        var next = d.Limit(
            $"SELECT a.asyncoperationid FROM data.asyncoperation a{d.LockHint(RowLock.UpdateSkipLocked)}\n" +
            """
                WHERE (statecode = @ready OR (statecode = @suspended AND statuscode = @waiting))
                    AND (@only IS NULL OR operationtype = @only)
                    AND ((NOT EXISTS (SELECT 1 FROM meta.workflow_wait w WHERE w.asyncoperationid = a.asyncoperationid)
                            AND (postponeuntil IS NULL OR postponeuntil <= @now))
                        OR EXISTS (SELECT 1 FROM meta.workflow_wait w WHERE w.asyncoperationid = a.asyncoperationid AND w.wake_at <= @now))
                ORDER BY createdon, asyncoperationid
            """, "(1)") + d.LockClause(RowLock.UpdateSkipLocked, "a");
        var sql = "UPDATE data.asyncoperation\nSET statecode = @locked, statuscode = @inprogress, startedon = @now, modifiedon = @now\n" +
                  d.ReturningInline(returned) + $"\nWHERE asyncoperationid = ({next})" + d.ReturningTrailing(returned);
        await using var cmd = conn.Command(sql);
        cmd.AddParameter("@locked", DbType.Int32, Locked);
        cmd.AddParameter("@inprogress", DbType.Int32, InProgress);
        cmd.AddParameter("@ready", DbType.Int32, Ready);
        cmd.AddParameter("@suspended", DbType.Int32, Suspended);
        cmd.AddParameter("@waiting", DbType.Int32, Waiting);
        cmd.AddParameter("@now", DbType.DateTime2, now);
        cmd.AddParameter("@only", DbType.Int32, (object?)onlyType ?? DBNull.Value);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new Job(rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetGuid(1), rd.IsDBNull(2) ? null : rd.GetString(2),
            rd.IsDBNull(3) ? null : rd.GetInt32(3), rd.IsDBNull(4) ? 0 : rd.GetInt32(4), rd.IsDBNull(5) ? null : rd.GetGuid(5),
            rd.IsDBNull(6) ? null : rd.GetGuid(6), rd.IsDBNull(7) ? null : rd.GetGuid(7), rd.IsDBNull(8) ? null : rd.GetInt32(8));
    }

    /// <summary>
    /// Runs the job's step as it is now, as the job's owner; a deleted or disabled step cancels the job, an owner that does
    /// not exist or is disabled fails it.
    /// </summary>
    /// <exception cref="PluginExecutionException">The plug-in failed.</exception>
    private async Task<Outcome> RunJobAsync(Job job, IDbConnectionFactory factory, CancellationToken ct)
    {
        var registry = services.GetRequiredService<StepRegistry>();
        var step = job.StepId is { } stepId ? await registry.GetStepAsync(stepId, ct) : null;
        if (step is null) return new Outcome(Completed, Canceled, StepDeletedMessage, null);
        if (!await registry.IsEnabledAsync(step.Id, ct)) return new Outcome(Completed, Canceled, StepDisabledMessage, null);

        if (job.OwnerId is not { } ownerId) return Failure(ErrorCodes.ObjectDoesNotExist, OwnerUnavailableMessage);
        await using var scope = services.CreateAsyncScope();
        await using var conn = await factory.OpenAsync(ct);
        var session = new DataSession(conn, null);
        Caller owner;
        try
        {
            owner = await scope.ServiceProvider.GetRequiredService<IPipelineCore>().ResolveUserAsync(ownerId, session, ct);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode is ErrorCodes.ObjectDoesNotExist or ErrorCodes.UserDisabled)
        {
            return Failure(ex.ErrorCode, OwnerUnavailableMessage);
        }

        if (string.IsNullOrEmpty(job.Data)) throw new SerializationException("The job has no execution context (data).");
        var initiating = job.CreatedBy ?? owner.UserId;
        var objectIds = await ObjectIdsAsync(session, owner.UserId, initiating, ct);
        var mirror = new SdkMirror();
        var context = Rebuild(AsyncContextSerializer.DeserializeModel(job.Data, mirror), job, step, owner, initiating, objectIds);
        await scope.ServiceProvider.GetRequiredService<PipelineExecutor>().RunAsyncStepAsync(step, context, mirror, session, ct);
        return new Outcome(Completed, Succeeded, null, null, Delete: step.AsyncAutoDelete);
    }

    /// <summary>
    /// Runs a job that is not a plug-in step with the <see cref="IAsyncOperationHandler"/> of its operation type, as the
    /// job's owner, without retry: success 3/30 (with the handler's <see cref="AsyncOperationRun.CompletionMessage"/>); any failure 3/31 with its message (and the fault's error code); an operation
    /// type no handler runs, or an owner that does not exist or is disabled, 3/31 before anything runs. A job interrupted by
    /// the host stopping fails 3/31 with <see cref="InterruptedMessage"/> instead of going back to the queue: a handler's work
    /// (a solution import writes its <c>importjob</c> log even when cancelled) cannot be started over; unless its handler
    /// <see cref="IAsyncOperationHandler.RestartsWhenInterrupted"/>: then the cancellation propagates and the job is released.
    /// </summary>
    private async Task<Outcome> RunHandlerJobAsync(Job job, int operationType, IDbConnectionFactory factory, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetServices<IAsyncOperationHandler>().FirstOrDefault(h => h.OperationType == operationType);
        if (handler is null)
            return Failure(ErrorCodes.Unexpected, $"System jobs of operation type {operationType} are not supported by xrmverse.");
        if (job.OwnerId is not { } ownerId) return await FailedBeforeRunAsync(handler, job, Failure(ErrorCodes.ObjectDoesNotExist, OwnerUnavailableMessage));

        try
        {
            Caller owner;
            // The connection is closed before the handler runs: it opens its own (a solution import takes the metadata lock).
            await using (var conn = await factory.OpenAsync(ct))
            {
                try
                {
                    owner = await scope.ServiceProvider.GetRequiredService<IPipelineCore>().ResolveUserAsync(ownerId, new DataSession(conn, null), ct);
                }
                catch (XrmdFaultException ex) when (ex.ErrorCode is ErrorCodes.ObjectDoesNotExist or ErrorCodes.UserDisabled)
                {
                    return await FailedBeforeRunAsync(handler, job, Failure(ex.ErrorCode, OwnerUnavailableMessage));
                }
            }

            var run = new AsyncOperationRun(job.Id, owner, job.CreatedBy ?? owner.UserId, job.Data) { Now = _time.GetUtcNow() };
            await handler.RunAsync(run, ct);
            if (run.Suspension is { } suspension)
                return new Outcome(Suspended, Waiting, null, null, PostponeUntil: suspension.Until, NewData: suspension.Data);
            return new Outcome(Completed, Succeeded, run.CompletionMessage, null, Delete: run.DeleteOnSuccess);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !handler.RestartsWhenInterrupted)
        {
            return Failure(ErrorCodes.Unexpected, InterruptedMessage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AsyncOperationCanceledException ex)
        {
            return new Outcome(Completed, Canceled, ex.Message, null);
        }
        catch (XrmdFaultException ex)
        {
            return Failure(ex.ErrorCode, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "System job {JobId} of operation type {OperationType} failed.", job.Id, operationType);
            return Failure(ErrorCodes.Unexpected, ex.Message);
        }
    }

    /// <summary>
    /// Lets <paramref name="handler"/> undo what queueing the job set up (<see cref="IAsyncOperationHandler.OnFailedBeforeRunAsync"/>)
    /// when the job fails before it runs; a failure of that clean-up is logged and the job still fails with <paramref name="outcome"/>.
    /// </summary>
    private async Task<Outcome> FailedBeforeRunAsync(IAsyncOperationHandler handler, Job job, Outcome outcome)
    {
        try
        {
            await handler.OnFailedBeforeRunAsync(job.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The clean-up of system job {JobId}, failed before it ran, failed.", job.Id);
        }
        return outcome;
    }

    /// <summary>
    /// The plug-in context of a job: the queued one, as Mode 1 / Stage 40 outside any transaction, with the identity of the
    /// row: <c>UserId</c> and <c>BusinessUnitId</c> of <paramref name="owner"/>, <c>InitiatingUserId</c> = <c>createdby</c>
    /// (the owner when not set), and their Entra object ids as stored in their <c>systemuser</c> rows
    /// (<paramref name="objectIds"/>). The users, business unit and object ids of the payload are ignored.
    /// </summary>
    /// <remarks>
    /// The model context is <see cref="StepContext.FromRemote"/> of the payload read into the model
    /// (<see cref="AsyncContextSerializer.DeserializeModel(string, SdkMirror)"/>) with exactly the members above overridden; its parent
    /// levels stand for the deserialized remote contexts (lent as <c>RemoteExecutionContext</c>). A payload only the SDK reader reads
    /// keeps its deserialized instances as the twins of the values in the job's mirror, so the plug-in sees the objects the payload holds,
    /// as it did when it was given the deserialized context.
    /// </remarks>
    internal static StepContext Rebuild(RemoteContext queued, Job job, PluginStep step, Caller owner,
        Guid initiating, IReadOnlyDictionary<Guid, Guid> objectIds)
    {
        var context = StepContext.FromRemote(queued);
        context.Stage = PostOperation;
        context.Mode = Asynchronous;
        context.Depth = job.Depth ?? queued.Depth;
        context.UserId = owner.UserId;
        context.InitiatingUserId = initiating;
        context.BusinessUnitId = owner.BusinessUnitId;
        context.CorrelationId = job.CorrelationId ?? queued.CorrelationId;
        context.OperationId = job.Id;
        context.OwningExtension = new RecordReference { LogicalName = "sdkmessageprocessingstep", Id = step.Id, Name = step.Name };
        context.IsInTransaction = false;
        // No image collection: the job's plug-in context has one empty array in both members, as the rebuilt context always had.
        context.PostEntityImagesCollection = context.PreEntityImagesCollection;
        context.UserAzureActiveDirectoryObjectId = objectIds.GetValueOrDefault(owner.UserId);
        context.InitiatingUserAzureActiveDirectoryObjectId = objectIds.GetValueOrDefault(initiating);
        // Plan 4.1c: the queued row is written by the server only, so the reserved shared variables it holds (a mailbox delivery's) are
        // the server's: the plug-in may read them, not change them; its own requests never carry them (they start a new invocation).
        ReservedSharedVariables.SnapshotChain(context);
        return context;
    }

    /// <summary>The <c>azureactivedirectoryobjectid</c> of each of the two users that has one.</summary>
    private static async Task<Dictionary<Guid, Guid>> ObjectIdsAsync(DataSession session, Guid first, Guid second, CancellationToken ct)
    {
        await using var cmd = session.Command(
            "SELECT systemuserid, azureactivedirectoryobjectid FROM data.systemuser " +
            "WHERE systemuserid IN (@first, @second) AND azureactivedirectoryobjectid IS NOT NULL");
        cmd.AddParameter("@first", DbType.Guid, first);
        cmd.AddParameter("@second", DbType.Guid, second);
        var ids = new Dictionary<Guid, Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids[rd.GetGuid(0)] = rd.GetGuid(1);
        return ids;
    }

    /// <summary>
    /// Writes the outcome, unless the job left Locked meanwhile (e.g. the user cancelled it while it ran); a successful job of a
    /// step with <c>asyncautodelete</c>, or one its handler deletes, is deleted instead (<see cref="DeleteSucceededAsync"/>). A retry sets
    /// <c>retrycount + 1</c> and <c>postponeuntil</c>; a completed job gets <c>completedon</c> and, when the outcome says so
    /// (handler jobs), loses its <c>data</c>.
    /// </summary>
    private async Task CompleteAsync(IDbConnectionFactory factory, Job job, Outcome outcome)
    {
        const string sql = """
            UPDATE data.asyncoperation
            SET statecode = @state, statuscode = @status, message = @message, errorcode = @errorcode, retrycount = @retrycount,
                postponeuntil = @postponeuntil, completedon = @completedon, modifiedon = @now,
                data = CASE WHEN @newdata IS NOT NULL THEN @newdata WHEN @cleardata = 1 THEN NULL ELSE data END
            WHERE asyncoperationid = @id AND statecode = @locked
            """;
        var now = DateTime.UtcNow;
        var retryCount = outcome.Retry ? job.RetryCount + 1 : job.RetryCount;
        object postponeUntil = outcome.Retry
            ? now + _options.AsyncRetryDelay * Math.Pow(2, Math.Min(retryCount - 1, 20))
            : outcome.PostponeUntil is { } until ? until.UtcDateTime : DBNull.Value;

        // CancellationToken.None: the outcome of a job that ran must be recorded.
        if (outcome.Delete)
        {
            try
            {
                await DeleteSucceededAsync(factory, job.Id);
                return;
            }
            catch (Exception ex)
            {
                // The job succeeded: it is kept 3/30 rather than left Locked (a restart would run it again).
                _logger.LogError(ex, "Asynchronous job {JobId} succeeded but could not be deleted; it is kept as succeeded.", job.Id);
            }
        }
        await using var conn = await factory.OpenAsync(CancellationToken.None);
        // A workflow job that ended keeps no wait (one a delete gave it while it ran, plan 2.14 W10): it goes with the outcome.
        await using var cmd = conn.Command(job.OperationType == WorkflowOperationType && outcome.State == Completed
            ? sql + "; DELETE FROM meta.workflow_wait WHERE asyncoperationid = @id"
            : sql);
        cmd.AddParameter("@id", DbType.Guid, job.Id);
        cmd.AddParameter("@locked", DbType.Int32, Locked);
        cmd.AddParameter("@state", DbType.Int32, outcome.State);
        cmd.AddParameter("@status", DbType.Int32, outcome.Status);
        cmd.AddParameter("@message", DbType.String, (object?)outcome.Message ?? DBNull.Value, -1);
        cmd.AddParameter("@errorcode", DbType.Int32, (object?)outcome.ErrorCode ?? DBNull.Value);
        cmd.AddParameter("@retrycount", DbType.Int32, retryCount);
        cmd.AddParameter("@postponeuntil", DbType.DateTime2, postponeUntil);
        cmd.AddParameter("@completedon", DbType.DateTime2, outcome.State == Completed ? now : DBNull.Value);
        cmd.AddParameter("@now", DbType.DateTime2, now);
        cmd.AddParameter("@cleardata", DbType.Int32, outcome.ClearData && outcome.State == Completed ? 1 : 0); // an integer: PostgreSQL has no boolean = 1
        cmd.AddParameter("@newdata", DbType.String, (object?)outcome.NewData ?? DBNull.Value, -1);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        if (outcome.Status is not (Succeeded or Waiting) || outcome.Retry)
            _logger.LogInformation("Asynchronous job {JobId} ended {State}/{Status}: {Message}", job.Id, outcome.State, outcome.Status,
                outcome.Message);
    }

    /// <summary>
    /// A succeeded job of a step with <c>asyncautodelete</c>, or one its handler deletes (<see cref="AsyncOperationRun.DeleteOnSuccess"/>,
    /// a workflow's <c>asyncautodelete</c>):
    /// deleted through the core, in a transaction, unless it left Locked meanwhile (a client's cancel), so the delete is a record
    /// delete like any other (the deletion log of a tracked table, the core's write observer).
    /// </summary>
    private async Task DeleteSucceededAsync(IDbConnectionFactory factory, Guid id)
    {
        if (BeforeAutoDelete.Value is { } hook) await hook();
        await using var scope = services.CreateAsyncScope();
        await using var conn = await factory.OpenAsync(CancellationToken.None);
        await using var tx = await conn.BeginTransactionAsync(CancellationToken.None);
        var session = new DataSession(conn, tx);
        var d = session.Dialect;
        await using (var cmd = session.Command(
                         d.Limit($"SELECT 1 FROM data.asyncoperation{d.LockHint(RowLock.UpdateRow)} WHERE asyncoperationid = @id AND statecode = @locked", "1")
                         + d.LockClause(RowLock.UpdateRow)))
        {
            cmd.AddParameter("@id", DbType.Guid, id);
            cmd.AddParameter("@locked", DbType.Int32, Locked);
            if (await cmd.ExecuteScalarAsync(CancellationToken.None) is null) return;
        }
        await scope.ServiceProvider.GetRequiredService<IPipelineCore>().ExecuteCoreAsync(
            new Request { Name = "Delete", Parameters = { ["Target"] = new RecordReference { LogicalName = "asyncoperation", Id = id } } },
            new CoreCall(Caller.Internal, session), CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);
        session.Committed();
    }

    /// <summary>A test seam: run before the delete of a succeeded job (<see cref="DeleteSucceededAsync"/>).</summary>
    internal static readonly AsyncLocal<Func<Task>?> BeforeAutoDelete = new();

    /// <summary>Puts a job interrupted by the host stopping back to Ready / Waiting For Resources.</summary>
    private async Task ReleaseAsync(IDbConnectionFactory factory, Guid id)
    {
        try
        {
            await using var conn = await factory.OpenAsync(CancellationToken.None);
            await using var cmd = conn.Command(
                "UPDATE data.asyncoperation SET statecode = @ready, statuscode = @waiting, startedon = NULL, modifiedon = @now, " +
                WorkflowPostponeCleared + " WHERE asyncoperationid = @id AND statecode = @locked");
            cmd.AddParameter("@id", DbType.Guid, id);
            cmd.AddParameter("@ready", DbType.Int32, Ready);
            cmd.AddParameter("@waiting", DbType.Int32, WaitingForResources);
            cmd.AddParameter("@locked", DbType.Int32, Locked);
            cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not release the interrupted asynchronous job {JobId}.", id);
        }
    }
}
