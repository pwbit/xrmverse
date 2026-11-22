using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.Async;
using Xrmd.Solution;

namespace Xrmd.Core.Solutions;

/// <summary>
/// Runs the queued upgrades (<c>StageAndUpgradeAsync</c>, <c>ExecuteAsync</c> of <c>StageAndUpgrade</c>;
/// <c>asyncoperation.operationtype</c> 211, <see cref="AsyncOperationQueue.StageAndUpgrade"/>) for the
/// <see cref="AsyncOperationWorker"/>: the file of the row's <c>data</c> is imported as the row's owner, into the
/// <c>importjob</c> named by <c>xrmverse_importjobid</c>, staged and promoted in one transaction
/// (<see cref="SolutionImporter.ImportQueuedAsync"/> with <see cref="SolutionPromoter.Upgrade"/>), with the SDK's rules.
/// </summary>
public sealed class SolutionStageAndUpgradeJob(SolutionImporter importer, SolutionPromoter promoter, IDbConnectionFactory factory)
    : IAsyncOperationHandler
{
    /// <inheritdoc />
    public int OperationType => AsyncOperationQueue.StageAndUpgrade;

    /// <inheritdoc />
    /// <exception cref="XrmdFaultException">400 when the row names no import job; the faults of the upgrade.</exception>
    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        Guid? importJobId;
        await using (var conn = await factory.OpenAsync(ct))
            importJobId = await AsyncOperationQueue.ImportJobIdAsync(new DataSession(conn, null), run.AsyncOperationId, ct);
        if (importJobId is not { } id)
            throw XrmdFaultException.InvalidArgument("The system job names no import job (xrmverse_importjobid).");
        await importer.ImportQueuedAsync(run.Owner, run.Data, id, ct, promoter.Upgrade);
    }
}
