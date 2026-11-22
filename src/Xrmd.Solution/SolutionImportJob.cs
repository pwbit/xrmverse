using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.Async;

namespace Xrmd.Solution;

/// <summary>
/// Runs the queued solution imports (<c>asyncoperation.operationtype</c> 203, <see cref="AsyncOperationQueue.ImportSolution"/>)
/// for the <see cref="AsyncOperationWorker"/>: the file of the row's <c>data</c> is imported with
/// <see cref="SolutionImporter.ImportQueuedAsync"/> as the row's owner, into the <c>importjob</c> named by
/// <c>xrmverse_importjobid</c>. The import runs on connections of its own, in a transaction of its own.
/// </summary>
public sealed class SolutionImportJob(SolutionImporter importer, IDbConnectionFactory factory) : IAsyncOperationHandler
{
    /// <inheritdoc />
    public int OperationType => AsyncOperationQueue.ImportSolution;

    /// <inheritdoc />
    /// <exception cref="XrmdFaultException">400 when the row names no import job; the faults of the import.</exception>
    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        Guid? importJobId;
        await using (var conn = await factory.OpenAsync(ct))
            importJobId = await AsyncOperationQueue.ImportJobIdAsync(new DataSession(conn, null), run.AsyncOperationId, ct);
        if (importJobId is not { } id)
            throw XrmdFaultException.InvalidArgument("The system job names no import job (xrmverse_importjobid).");
        await importer.ImportQueuedAsync(run.Owner, run.Data, id, ct);
    }
}
