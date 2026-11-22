using Xrmd.Core.Errors;
using Xrmd.Pipeline.Async;

namespace Xrmd.Core.Solutions;

/// <summary>
/// Runs the queued uninstalls (<c>UninstallSolutionAsync</c>, <c>asyncoperation.operationtype</c> 208,
/// <see cref="AsyncOperationQueue.UninstallSolution"/>) for the <see cref="AsyncOperationWorker"/>: the solution named by the
/// row's <c>data</c> is uninstalled with <see cref="SolutionUninstaller.UninstallAsync"/> as the row's owner, with the SDK's
/// rules (data loss is refused, ruling M10). A refusal fails the job with its message and code.
/// </summary>
public sealed class SolutionUninstallJob(SolutionUninstaller uninstaller) : IAsyncOperationHandler
{
    /// <inheritdoc />
    public int OperationType => AsyncOperationQueue.UninstallSolution;

    /// <inheritdoc />
    /// <exception cref="XrmdFaultException">400 when the row names no solution; the faults of the uninstall.</exception>
    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrEmpty(run.Data))
            throw XrmdFaultException.InvalidArgument("The system job names no solution to uninstall (data).");
        await uninstaller.UninstallAsync(run.Owner, run.Data, new UninstallOptions(DropData: false, DryRun: false, FromSdk: true), ct);
    }
}
