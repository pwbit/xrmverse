using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.Bulk;
using static Xrmd.Pipeline.Bulk.BulkTargets;

namespace Xrmd.Core.Elastic;

/// <summary>
/// CreateMultiple, UpdateMultiple, UpsertMultiple and DeleteMultiple (plan 3.4, ruling E5): on an elastic table each target runs,
/// in order, as the single-record message (Create, Update, Upsert, Delete) through the same entry as a client's single message,
/// so each is a first-level message with its own steps, security checks and per-stage commits (ruling E3). A failed target
/// does not stop the others; when any failed the request fails with the first failure's code and status, and
/// <c>ErrorDetails["Plugin.BulkApiErrorDetails"]</c> lists the failed targets (<see cref="BulkApiErrorDetail"/>). On a standard
/// table DeleteMultiple stays refused; the other three run in the pipeline, all or nothing (#76, ruling B1), and never reach
/// this class. Reading the targets and building the response is <see cref="BulkTargets"/>, shared with that path.
/// </summary>
public static class ElasticBulkMessages
{
    /// <summary>The error detail that lists the failed targets, a JSON array of <see cref="BulkApiErrorDetail"/>.</summary>
    public const string ErrorDetailsKey = "Plugin.BulkApiErrorDetails";

    /// <summary>True for one of the four bulk messages (<see cref="SdkMessageCatalog.BulkMessages"/>, case-insensitive).</summary>
    public static bool IsBulkMessage(string? name) => BulkTargets.MessageOf(name) is not null;

    /// <summary>
    /// Runs the bulk <paramref name="request"/>: every target through <paramref name="runSingle"/>, in order.
    /// </summary>
    /// <param name="request">A CreateMultiple, UpdateMultiple or UpsertMultiple (<c>Targets</c>: an <see cref="EntityCollection"/>)
    /// or a DeleteMultiple (<c>Targets</c>: an <see cref="EntityReferenceCollection"/>).</param>
    /// <param name="runSingle">The entry of a single-record message (the organization service's).</param>
    /// <param name="store">The metadata, to tell an elastic table from a standard one.</param>
    /// <param name="ct">Cancellation, checked between targets.</param>
    /// <returns><see cref="CreateMultipleResponse"/> (<c>Ids</c> in the targets' order), <see cref="UpdateMultipleResponse"/>,
    /// <see cref="UpsertMultipleResponse"/> (one <see cref="UpsertResponse"/> per target) or an empty <c>DeleteMultiple</c>
    /// response; the writes carry the session token of the last target written.</returns>
    /// <exception cref="XrmdFaultException">400 for missing or empty targets, targets of another table, an unknown table, a
    /// DeleteMultiple on a standard table, an UpsertMultiple naming the same id twice (all before any write); else the first
    /// failed target's fault, with the error detail of every failed target.</exception>
    /// <exception cref="InvalidOperationException">Another bulk message on a standard table (it belongs to the pipeline).</exception>
    public static async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request,
        Func<OrganizationRequest, Task<OrganizationResponse>> runSingle, IMetadataStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runSingle);
        ArgumentNullException.ThrowIfNull(store);
        var targets = BulkTargets.Read(request);
        var message = targets.Message;
        var table = BulkTargets.TableOf(targets, store);
        if (!ElasticTables.IsElastic(table))
        {
            // Exact Dataverse text: the documented outcome of DeleteMultiple on a standard table.
            if (message == DeleteMultiple) throw XrmdFaultException.InvalidArgument("DeleteMultiple has not yet been implemented.");
            // The other three run through the pipeline on a standard table (#76): the caller routes them there.
            throw new InvalidOperationException($"{message} on the standard table '{table.LogicalName}' runs through the pipeline.");
        }
        var ids = Enumerable.Range(0, targets.Count).Select(i => BulkTargets.IdOf(table, targets.Items[i])).ToArray();
        if (message == UpsertMultiple) BulkTargets.DemandDistinctIds(table, ids);

        var seen = new HashSet<Guid>();
        var responses = new OrganizationResponse?[targets.Count];
        var failures = new List<BulkApiErrorDetail>();
        XrmdFaultException? first = null;
        OrganizationResponse? lastWrite = null;
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var id = ids[i];
            // UpdateMultiple: a later target with an id already seen is ignored (the first one wins).
            if (message == UpdateMultiple && BulkTargets.IsRepeated(id, seen)) continue;
            try
            {
                responses[i] = await runSingle(ElasticRecords.CarryParameters(request, targets.Single(i)));
                if (message != DeleteMultiple) lastWrite = responses[i];
            }
            catch (Exception ex) when (ex is XrmdFaultException or XrmdValidationException)
            {
                // A validation failure is a 400 InvalidArgument, as every front end answers it.
                var fault = ex as XrmdFaultException ?? XrmdFaultException.InvalidArgument(ex.Message);
                first ??= fault;
                failures.Add(new BulkApiErrorDetail(i, id == Guid.Empty ? "" : id.ToString(), fault.HttpStatus));
            }
        }

        if (first is not null)
        {
            var failed = new XrmdFaultException(first.ErrorCode, first.Message, first.HttpStatus);
            failed.ErrorDetails[ErrorDetailsKey] = JsonSerializer.Serialize(failures);
            throw failed;
        }

        var response = BulkTargets.Response(message, responses);
        return lastWrite is null ? response : ElasticRecords.CarrySessionToken(lastWrite, response);
    }
}
