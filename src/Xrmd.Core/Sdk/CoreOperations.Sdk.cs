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
/// the model request instead (<see cref="ExecuteCrudCoreAsync"/>): nothing below applies to them but the typed response.
/// <para>
/// A message crosses with the request's chain mirror (<see cref="SdkMirror.For"/>, or a new mirror registered for the request), so
/// the handler gets the SDK twin of the request: the same instance the caller or the pipeline holds, with its CLR type, its
/// early-bound values and the marks keyed on it. While the pipeline holds the request at the core (<see cref="CoreHandOff"/>), the
/// handler runs on the held SDK request and nothing is converted: the pipeline reads it back once. Otherwise the model request is
/// written out into its twin before the handler and read back in after it. The typed SDK response crosses back as an unread shell
/// (<see cref="SdkMirror.Shell"/>), so a response nobody reads is never converted.
/// </para>
/// </summary>
public sealed partial class CoreOperations
{
    /// <inheritdoc />
    public async Task<Response> ExecuteCoreAsync(Request request, CoreCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);
        if (CoreMessages.TryGetValue(request.Name ?? "", out var canonical) && CoreResponses.IsCrud(canonical))
            return await ExecuteCrudCoreAsync(request, canonical, call, ct);
        var (mirror, sdk, held, created) = Enter(request);
        var response = await ExecuteCoreSdkAsync(sdk, call.Caller, call.Session, Nested(mirror, call.Nested), call.ServerSetValues, ct);
        return Leave(mirror, sdk, held, created, response);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only the message name and the <c>Relationship</c> parameter are read, so outside a held window the handler gets a request of those
    /// two: nothing is written into the request's SDK twin and the rest of the request is not converted.
    /// </remarks>
    public IReadOnlyCollection<string> WrittenEntities(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Defensive: the pipeline asks only after its held window closed (ReleaseCore), so this branch is not taken today; inside a
        // window the held request would give the same answer.
        if (CoreHandOff.TryGetHeld(request, out var held)) return WrittenEntitiesSdk(held!);
        var sdk = new OrganizationRequest(request.Name);
        if (request.Parameters.TryGetValue("Relationship", out var relationship))
            sdk.Parameters["Relationship"] = relationship is RelationshipName name ? name.ToSdk() : relationship;
        return WrittenEntitiesSdk(sdk);
    }

    /// <inheritdoc />
    public async Task<Response> ExecuteNonPipelineAsync(Request request, Caller caller, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (mirror, sdk, held, created) = Enter(request);
        var response = await ExecuteNonPipelineSdkAsync(sdk, caller, session, ct);
        return Leave(mirror, sdk, held, created, response);
    }

    /// <inheritdoc />
    public Task<bool> LockExistsAsync(RecordReference target, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        return LockExistsSdkAsync(target.ToSdk(), session, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The image is converted once, into the mirror the pipeline named for <paramref name="target"/>
    /// (<see cref="CoreHandOff.ReadImagesWith"/>), where the record's SDK twin is then the core's own <c>Entity</c>; into a mirror of
    /// its own otherwise.
    /// </remarks>
    public async Task<Record?> RetrieveImageAsync(RecordReference target, string[]? attributes, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var image = await RetrieveImageSdkAsync(target.ToSdk(), attributes, session, ct);
        if (image is null) return null;
        var record = (Record)(CoreHandOff.ImageMirror(target) ?? new SdkMirror()).In(image)!;
        CoreHandOff.ImageRead.Value?.Invoke(image, record);
        return record;
    }

    /// <summary>
    /// The request's chain mirror (registered now when it had none; <c>Created</c> is then true) and the SDK request the handler runs
    /// on: the one the pipeline holds (<paramref name="request"/> is then not converted), or the request's twin, written out from the
    /// model.
    /// </summary>
    private static (SdkMirror Mirror, OrganizationRequest Sdk, bool Held, bool Created) Enter(Request request)
    {
        var registered = SdkMirror.For(request);
        var mirror = registered ?? new SdkMirror();
        SdkMirror.Register(request, mirror);
        var sdk = CoreHandOff.TryGetHeld(request, out var held) ? held! : mirror.Out(request)!;
        CoreHandOff.HandlerRequest.Value?.Invoke(sdk);
        return (mirror, sdk, held is not null, registered is null);
    }

    /// <summary>
    /// Reads what the handler changed in the request back into the model (in before out; not in a held window, where the pipeline
    /// reads it back) and answers with the model response of the handler's typed response: an unread shell when the caller brought
    /// the mirror (it can read the twin in), or the response read in when the adapter made the mirror (<paramref name="created"/>),
    /// since such a caller has no mirror to read it with.
    /// </summary>
    private static Response Leave(SdkMirror mirror, OrganizationRequest sdk, bool held, bool created, OrganizationResponse response)
    {
        if (!held) mirror.In(sdk);
        return created ? mirror.In(response)! : PipelineCoreSdk.ModelResponse(mirror, response);
    }

    /// <summary>
    /// The model nested delegate as the SDK one the handlers call, through the chain's mirror: the SDK request the core built is read
    /// into its model twin, which is registered with the mirror, and the model response is written out into the SDK one (an unread
    /// shell gives back its typed twin unchanged).
    /// </summary>
    private static SdkNestedRequest? Nested(SdkMirror mirror, NestedRequest? nested) =>
        nested is null ? null : new ChainNested(mirror, nested).RunAsync;

    /// <summary>
    /// S.2e3b (Task 8, Decisions 7 and 8): a CRUD message on the model. The request is not written out and read back (no
    /// <see cref="Enter"/>; a window the pipeline holds is not used): the model handler (<see cref="ExecuteCrudAsync"/>) runs on it and
    /// its model response is paired with today's typed SDK response (<see cref="CoreResponses.Typed"/>) in the request's chain mirror
    /// (registered now when it has none) and written out once, so a caller that takes the SDK response (the pipeline, an extension
    /// method) gets the typed object filled. The model response returned is that ordinary twin, not a shell.
    /// </summary>
    private async Task<Response> ExecuteCrudCoreAsync(Request request, string canonical, CoreCall call, CancellationToken ct)
    {
        var mirror = SdkMirror.For(request);
        if (mirror is null)
        {
            mirror = new SdkMirror();
            SdkMirror.Register(request, mirror);
        }
        // Decision 6: the query model a front end built rides on the request's SDK twin while that still holds its placeholder.
        if (canonical == "RetrieveMultiple" && mirror.TryGetSdk(request, out var twin) && twin is RetrieveMultipleModelRequest { UsesModel: true } withModel)
            QueryModelHint.Attach(request, withModel.Model);
        Response response;
        try
        {
            // The server values of a Create are SDK values (the pipeline's InvocationContext.ServerSetValues): read in as model values.
            response = await ExecuteCrudAsync(request, canonical,
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

    /// <summary>A model nested delegate and the chain mirror it crosses with (<see cref="Nested"/>).</summary>
    private sealed class ChainNested(SdkMirror mirror, NestedRequest nested)
    {
        public async Task<OrganizationResponse> RunAsync(OrganizationRequest sdk, CancellationToken ct)
        {
            var model = mirror.In(sdk)!;
            SdkMirror.Register(model, mirror);
            return mirror.Out(await nested(model, ct))!;
        }
    }
}
