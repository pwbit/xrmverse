using Xrmd.Abstractions;

namespace Xrmd.Pipeline.Async;

/// <summary>A claimed system job that is not a plug-in step: its row and the identity it runs as.</summary>
/// <param name="AsyncOperationId">The <c>asyncoperationid</c> of the job.</param>
/// <param name="Owner">The owner of the row (<c>ownerid</c>, with the owner's current business unit): the job runs as this user.</param>
/// <param name="InitiatingUserId">The <c>createdby</c> of the row (the owner when not set).</param>
/// <param name="Data">The row's <c>data</c>.</param>
public sealed record AsyncOperationRun(Guid AsyncOperationId, Caller Owner, Guid InitiatingUserId, string? Data)
{
    /// <summary>
    /// Set by the handler: the <c>message</c> of a job that succeeded (3/30), e.g. to say it stopped at a limit. Null: none.
    /// </summary>
    public string? CompletionMessage { get; set; }

    /// <summary>The worker's time when it claimed the job (UTC): the clock a handler runs on (a workflow's execution time, W10).</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Set by the handler (<see cref="Suspend"/>): the job is not over. It goes to Suspended / Waiting (1/10) with
    /// <c>postponeuntil</c> = <c>Until</c> and <c>data</c> = <c>Data</c>, and runs again from that data once ready (plan 2.14, W10).
    /// Null: the job ended.
    /// </summary>
    public (DateTimeOffset? Until, string Data)? Suspension { get; private set; }

    /// <summary>
    /// Set by the handler: a job that succeeded is deleted instead of kept 3/30 (a workflow with <c>asyncautodelete</c>, W9).
    /// </summary>
    public bool DeleteOnSuccess { get; set; }

    /// <summary>
    /// Ends this run of the job without ending the job: it is suspended (1/10) until <paramref name="postponeUntil"/> (null: until
    /// something else makes it ready, e.g. a write of the record a workflow waits on), with <paramref name="data"/> as its new
    /// <c>data</c>.
    /// </summary>
    public void Suspend(DateTimeOffset? postponeUntil, string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Suspension = (postponeUntil, data);
    }
}

/// <summary>
/// Thrown by an <see cref="IAsyncOperationHandler"/> to end its job Canceled (3/32) with <see cref="Exception.Message"/> as the
/// job's <c>message</c> (e.g. a workflow whose stop step cancels, or whose definition was deleted; plan 2.14, W9 and W11).
/// </summary>
public sealed class AsyncOperationCanceledException(string message) : Exception(message);

/// <summary>
/// Runs the system jobs of one <c>asyncoperation.operationtype</c> other than System Event (registered in DI as a scoped
/// service; <see cref="AsyncOperationWorker"/> resolves the one whose <see cref="OperationType"/> matches the claimed row).
/// A job that returns has succeeded (3/30), with its <see cref="AsyncOperationRun.CompletionMessage"/> as <c>message</c> (or is deleted,
/// <see cref="AsyncOperationRun.DeleteOnSuccess"/>), unless the handler suspended it (<see cref="AsyncOperationRun.Suspend"/>, 1/10); an
/// <see cref="AsyncOperationCanceledException"/> cancels it (3/32); any other exception fails it (3/31) with the exception's message and,
/// for an <see cref="Xrmd.Core.Errors.XrmdFaultException"/>, its error code. Such jobs are never retried.
/// </summary>
public interface IAsyncOperationHandler
{
    /// <summary>The <c>asyncoperation.operationtype</c> this handler runs.</summary>
    int OperationType { get; }

    /// <summary>Runs the job to its end.</summary>
    Task RunAsync(AsyncOperationRun run, CancellationToken ct);

    /// <summary>
    /// True when a job interrupted by the host stopping can run again from its <c>data</c>: it goes back to Ready (0/0), as after a
    /// crash (at least once; a background workflow, plan 2.14). False (the default): it fails 3/31 with
    /// <see cref="AsyncOperationWorker.InterruptedMessage"/> (a solution import cannot be started over).
    /// </summary>
    bool RestartsWhenInterrupted => false;

    /// <summary>
    /// Called when the worker fails the job (3/31) before <see cref="RunAsync"/> runs (its owner does not exist or is
    /// disabled), so the handler can undo the state queueing it set up. Nothing by default.
    /// </summary>
    Task OnFailedBeforeRunAsync(Guid asyncOperationId, CancellationToken ct) => Task.CompletedTask;
}
