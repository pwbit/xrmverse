using Xrmd.Core.Errors;
using Xrmd.Pipeline.Async;

namespace Xrmd.Core.Solutions;

/// <summary>
/// Runs the queued promotes (<c>DeleteAndPromoteAsync</c>, <c>ExecuteAsync</c> of <c>DeleteAndPromote</c>;
/// <c>asyncoperation.operationtype</c> 207, <see cref="AsyncOperationQueue.DeleteAndPromote"/>) for the
/// <see cref="AsyncOperationWorker"/>: the holding solution of the solution named by the row's <c>data</c> is promoted with
/// <see cref="SolutionPromoter.PromoteInOwnTransactionAsync"/> as the row's owner, with the SDK's rules (data loss is refused,
/// ruling M10). A refusal fails the job with its message and code.
/// </summary>
public sealed class SolutionPromoteJob(SolutionPromoter promoter) : IAsyncOperationHandler
{
    /// <inheritdoc />
    public int OperationType => AsyncOperationQueue.DeleteAndPromote;

    /// <inheritdoc />
    /// <exception cref="XrmdFaultException">400 when the row names no solution; the faults of the promote.</exception>
    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrEmpty(run.Data))
            throw XrmdFaultException.InvalidArgument("The system job names no solution to promote (data).");
        await promoter.PromoteInOwnTransactionAsync(run.Owner, run.Data, new PromoteOptions(DropData: false, DryRun: false, FromSdk: true), ct);
    }
}
