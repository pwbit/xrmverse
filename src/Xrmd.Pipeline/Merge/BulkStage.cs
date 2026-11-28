using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Metadata;
using Xrmd.Pipeline.Bulk;

namespace Xrmd.Pipeline.Merge;

/// <summary>
/// Stage 30 of <c>CreateMultiple</c>, <c>UpdateMultiple</c> and <c>UpsertMultiple</c> on a standard table (#76, rulings B1, B4 and
/// B14). There is no core operation: each target, in order, runs the single-record message (<c>Create</c>, <c>Update</c>,
/// <c>Upsert</c>) through the whole pipeline as an inner execution, in the bulk's transaction, so the first failing target fails
/// the request and the bulk execution rolls everything back. The targets are read from the request as the PreOperation steps
/// left them, so they are checked again: every target must still be a record of the table, and an UpdateMultiple target
/// repeating an earlier id is skipped again (ruling B3). An elastic table keeps its own bulk path (plan 3.4, ruling E5: <c>ElasticBulkMessages</c>), which never reaches
/// this class.
/// </summary>
/// <remarks>
/// S.2e3c3a (Decisions 3, 5 and 8): the stage runs on the pipeline's model request (<see cref="Request"/>, <see cref="TableDefinition"/>)
/// and answers the model response of each target; the pipeline builds the typed bulk response from their SDK twins.
/// </remarks>
internal static class BulkStage
{
    /// <summary>True for a bulk message this stage runs (ignoring case): CreateMultiple, UpdateMultiple or UpsertMultiple.</summary>
    public static bool Handles(string? message) =>
        BulkTargets.MessageOf(message) is BulkTargets.CreateMultiple or BulkTargets.UpdateMultiple or BulkTargets.UpsertMultiple;

    /// <summary>True when <paramref name="message"/> is one of <see cref="Handles"/> on the standard table <paramref name="table"/>.</summary>
    public static bool Applies(string? message, TableDefinition? table) => Handles(message) && table is not null && !ElasticTables.IsElastic(table);

    /// <summary>
    /// Runs every target of <paramref name="bulk"/> through <paramref name="runInner"/>, in order, each request carrying the bulk's
    /// optional parameters (e.g. <c>SuppressDuplicateDetection</c>; <see cref="StandardUpsert.CarryParameters"/>).
    /// </summary>
    /// <param name="table">The standard table of the bulk message (its primary entity).</param>
    /// <param name="runInner">Runs a single-record request through the pipeline as an inner execution of the bulk message; answers
    /// its model response.</param>
    /// <returns>The model response of each target, in target order; null for a repeated UpdateMultiple target, which did not run.</returns>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 when the targets are missing or empty or a target is of
    /// another table (a PreOperation step changed <c>Targets</c>); else the first failing target's fault, unchanged.</exception>
    public static async Task<Response?[]> ExecuteAsync(Request bulk, TableDefinition table, Func<Request, Task<Response>> runInner,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(runInner);
        var targets = BulkTargets.Read(bulk);
        BulkTargets.DemandOneTable(targets, table.LogicalName);
        var seen = new HashSet<Guid>();
        var singles = new Response?[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (targets.Message == BulkTargets.UpdateMultiple && BulkTargets.IsRepeated(BulkTargets.IdOf(table, targets.Items[i]), seen)) continue;
            singles[i] = await runInner(StandardUpsert.CarryParameters(bulk, targets.Single(i)));
        }
        return singles;
    }

    /// <summary>The text of a bulk message issued from a plug-in (ruling B12), for any table.</summary>
    public static string FromPluginMessage(string message) => $"{message} cannot be called from plug-in code.";
}
