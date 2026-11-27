using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Pipeline;

namespace Xrmd.Core;

/// <summary>
/// S.2e3a: the core's entry adapter, the model members of <see cref="IPipelineCore"/> over the SDK handlers of
/// <see cref="CoreOperations"/>, whose bodies have not moved yet (S.2e3c moves them). S.2e3b: the eight CRUD messages are dispatched on
/// the model request instead (<see cref="ExecuteModelCoreAsync"/>): nothing below applies to them but the typed response. S.2e3c1: every
/// message enters the model dispatch (<see cref="ExecuteMessageAsync"/>, <see cref="ExecuteNonPipelineMessageAsync"/>), which hands a
/// bridged message to <see cref="CoreSdkMessages"/>: that is where the request crosses to the SDK handler, as described there.
/// </summary>
public sealed partial class CoreOperations
{
    /// <inheritdoc />
    /// <remarks>
    /// S.2e3c1 (Decision 2): every message enters the model dispatch (<see cref="ExecuteMessageAsync"/>). A message on the model is
    /// paired here with today's typed SDK response (<see cref="ExecuteModelCoreAsync"/>); a bridged message, or a name that is no core
    /// message, crosses unchanged and the dispatch hands it to <see cref="CoreSdkMessages"/>.
    /// </remarks>
    public async Task<Response> ExecuteCoreAsync(Request request, CoreCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);
        var canonical = CoreMessages.TryGetValue(request.Name ?? "", out var name) ? name : request.Name ?? "";
        return IsOnModel(canonical)
            ? await ExecuteModelCoreAsync(request, canonical, call, ct)
            : await ExecuteMessageAsync(request, canonical, call, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// S.2e3c1 (Decision 2): every message enters the model dispatch (<see cref="ExecuteNonPipelineMessageAsync"/>). A message on the
    /// model (the access reads, Task 3; RetrieveUserPrivileges, Task 4; the reads of Task 6) is paired here with today's typed SDK response, as <see cref="ExecuteModelCoreAsync"/> does; a
    /// bridged message, or a name that is no such message, crosses unchanged and the dispatch hands it to <see cref="CoreSdkMessages"/>.
    /// </remarks>
    public async Task<Response> ExecuteNonPipelineAsync(Request request, Caller caller, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = NonPipelineMessages.TryGetValue(request.Name ?? "", out var name) ? name : request.Name ?? "";
        if (!IsNonPipelineOnModel(canonical)) return await ExecuteNonPipelineMessageAsync(request, canonical, caller, session, ct);
        var mirror = ChainMirror(request);
        var response = await ExecuteNonPipelineMessageAsync(request, canonical, caller, session, ct);
        mirror.Pair(response, CoreResponses.Typed(canonical));
        mirror.Out(response);
        return response;
    }

    /// <summary>The request's chain mirror, registered now when it has none.</summary>
    private static SdkMirror ChainMirror(Request request)
    {
        var mirror = SdkMirror.For(request);
        if (mirror is null)
        {
            mirror = new SdkMirror();
            SdkMirror.Register(request, mirror);
        }
        return mirror;
    }

    /// <inheritdoc />
    /// <remarks>
    /// S.2e3c1 (Task 6): the image is read on the model (<see cref="ReadImageAsync"/>). When the pipeline named a mirror for
    /// <paramref name="target"/> (<see cref="CoreHandOff.ReadImagesWith"/>), the record is written out into it once, so the record's SDK
    /// twin there is the core's own <c>Entity</c> (the stage 20 checks and the SDK extension read it); otherwise nothing is converted.
    /// </remarks>
    public async Task<Record?> RetrieveImageAsync(RecordReference target, string[]? attributes, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var record = await ReadImageAsync(target, attributes, session, ct);
        if (record is null || CoreHandOff.ImageMirror(target) is not { } mirror) return record;
        var image = (Entity)mirror.Out(record)!;
        CoreHandOff.ImageRead.Value?.Invoke(image, record);
        return record;
    }

    /// <summary>
    /// S.2e3b (Task 8, Decisions 7 and 8): a CRUD message on the model; S.2e3c1 (Task 3, Decision 7): also a share or record team
    /// message. The request is not written out and read back (as a bridged message is, <see cref="CoreSdkMessages"/>; a window the
    /// pipeline holds is not used): the model dispatch (<see cref="ExecuteMessageAsync"/>, its CRUD branch <see cref="ExecuteCrudAsync"/>
    /// or its access branch <see cref="ExecuteAccessAsync"/>) runs on it and its model response is paired with today's typed SDK response (<see cref="CoreResponses.Typed"/>) in the request's chain mirror
    /// (registered now when it has none) and written out once, so a caller that takes the SDK response (the pipeline, an extension
    /// method) gets the typed object filled. The model response returned is that ordinary twin, not a shell.
    /// </summary>
    private async Task<Response> ExecuteModelCoreAsync(Request request, string canonical, CoreCall call, CancellationToken ct)
    {
        var mirror = ChainMirror(request);
        // Decision 6: the query model a front end built rides on the request's SDK twin while that still holds its placeholder.
        if (canonical == "RetrieveMultiple" && mirror.TryGetSdk(request, out var twin) && twin is RetrieveMultipleModelRequest { UsesModel: true } withModel)
            QueryModelHint.Attach(request, withModel.Model);
        Response response;
        try
        {
            // The server values of a Create are SDK values (the pipeline's InvocationContext.ServerSetValues): read in as model values.
            response = await ExecuteMessageAsync(request, canonical,
                call with { Nested = CrudNested(mirror, call.Nested), ServerSetValues = ModelValues(call.ServerSetValues) }, ct);
        }
        finally
        {
            KeepCallerTarget(mirror, request, canonical);
        }
        var typed = CoreResponses.Typed(canonical);
        mirror.Pair(response, typed);
        if (canonical is "Retrieve" or "RetrieveMultiple") CopyReadResults(response, typed);
        else mirror.Out(response);
        return response;
    }

    /// <summary>
    /// Ruling 8a: the rows of a Retrieve or RetrieveMultiple answer go into the typed SDK response as plain copies
    /// (<see cref="SdkRecords.ValueToSdk"/>), as the SDK handler built them before S.2e3b, not through the chain mirror: an SDK caller
    /// that only reads them (the Web API, the organization service, a plug-in's own call) pays one copy per row and no mirror pairing.
    /// The model response stays paired with the typed one, so a reader that needs the same instances back (the observed pipeline: a step
    /// at stage 40, a workflow, <c>FinishResponse</c>) gets the model rows and writes them out through the mirror itself, which replaces
    /// the copies with mirror twins.
    /// </summary>
    private static void CopyReadResults(Response response, OrganizationResponse typed)
    {
        typed.ResponseName = response.Name;
        typed.Results.Clear();
        foreach (var (key, value) in response.Results) typed.Results[key] = SdkRecords.ValueToSdk(value);
    }

    /// <summary>
    /// The in-place changes a write makes to its Target, carried to the Target's SDK twin, the caller's own instance (an SDK caller's
    /// <c>Entity</c> through the pipeline or <see cref="PipelineCoreSdk"/>): before S.2e3b the SDK handler ran on it and changed it in
    /// place, as the SDK twins still do (<see cref="WriteTwin"/>). The validation removes the values of calculated columns (removed from
    /// the twin, which writes nothing); a write by key (an elastic table, key attributes on the Target or on a reference value) gets ids and
    /// its <c>partitionid</c>, so the Target is written out into its twin. An Assign by key gets the assignee's id filled in the same way.
    /// A Target without an SDK twin (a model caller) has nothing to keep.
    /// Invariant: the model bodies do change the Target in place (validation strips calculated values, a write by key fills ids and
    /// <c>partitionid</c>), and this method is what copies exactly those changes onto the caller's <c>Entity</c>; the request itself
    /// is otherwise current as it is, so the SDK request is not written out again.
    /// </summary>
    private void KeepCallerTarget(SdkMirror mirror, Request request, string canonical)
    {
        if (canonical is "Create" or "Update" && request.Parameters.TryGetValue("Target", out var target) && target is Record record
            && mirror.TryGetSdk(record, out var twin) && twin is Entity entity)
        {
            if ((store.GetTable(record.LogicalName ?? "") is { } table && ElasticTables.IsElastic(table)) || record.KeyAttributes.Count > 0
                || record.Attributes.Values.Any(v => v is RecordReference { KeyAttributes.Count: > 0 }))
                mirror.Out(record);
            else
                foreach (var name in entity.Attributes.Keys.Where(name => !record.Contains(name)).ToList()) entity.Attributes.Remove(name);
        }
        else if (canonical == "Assign" && request.Parameters.TryGetValue("Assignee", out var assignee)
                 && assignee is RecordReference { KeyAttributes.Count: > 0 } byKey && mirror.TryGetSdk(byKey, out _))
            mirror.Out(byKey);
    }

    /// <summary>
    /// The model nested delegate a CRUD body calls (the cascaded children of a Delete, <see cref="DeleteRecordAsync"/>), through the
    /// chain mirror: the request the core built is registered with the mirror and written out into today's typed SDK request
    /// (<see cref="CoreResponses.TypedRequest"/>, a <see cref="DeleteRequest"/> for a cascaded child) carrying the SDK cascade mark, so the
    /// pipeline, the security guard and a plug-in that casts the request get what the core built before S.2e3b. The model delegate runs
    /// on the model request itself.
    /// </summary>
    private static NestedRequest? CrudNested(SdkMirror mirror, NestedRequest? nested) =>
        nested is null
            ? null
            : (request, ct) =>
            {
                SdkMirror.Register(request, mirror);
                if (!mirror.TryGetSdk(request, out _)) mirror.Pair(request, CoreResponses.TypedRequest(request.Name));
                var sdk = mirror.Out(request)!;
                if (CascadeHint.IsMarked(request)) CascadeHint.Mark(sdk);
                return nested(request, ct);
            };
}
