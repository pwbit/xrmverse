using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Security;

namespace Xrmd.Pipeline;

/// <summary>
/// The SDK form of <see cref="NestedRequest"/>: runs a message through the pipeline within the current execution (see
/// <see cref="CoreSdkBoundary.ExecuteCoreAsync"/>).
/// </summary>
public delegate Task<OrganizationResponse> SdkNestedRequest(OrganizationRequest request, CancellationToken ct);

/// <summary>
/// S.2e3c3b1 (Decision 3): the core's SDK boundary, the one place where a call of <see cref="IPipelineCore"/> crosses between the SDK
/// and the model. The core answers on the model only (Decision 2); what its entry adapter did for an SDK caller (S.2e3a, S.2e3b,
/// S.2e3c1) happens here, in the same order and with the same mirror, so an SDK caller sees the same responses (CLR types included),
/// faults, stored rows and in-place changes to its request as before the move.
/// <para>
/// Two layers. The internal members (<see cref="CoreAsync"/>, <see cref="NonPipelineAsync"/>, <see cref="ImageAsync"/>) work on a model
/// request and a mirror the caller holds: the pipeline's stage 30 calls them with its chain mirror. The public members take the SDK
/// objects of a caller that still holds them (Workflows, Solution, Admin, Core.Solutions, the plug-in service): each crosses its
/// arguments into the model with a mirror of the call, which it registers for the model request (<see cref="SdkMirror.Register"/>),
/// calls the internal member and crosses the answer back with the same mirror. The request a caller passes is therefore the SDK twin
/// the core works on, and the response it gets is the core's typed instance (a <c>CreateResponse</c> stays a <c>CreateResponse</c>).
/// They are plain static methods, not extensions: a caller names the boundary, so the SDK crossing is visible at the call.
/// </para>
/// </summary>
public static class CoreSdkBoundary
{
    /// <summary>
    /// Test seam: called by <see cref="ImageAsync"/> with each image record it wrote out into the mirror named for it and that SDK
    /// twin (S.2e3c3b1, Decision 3: <c>CoreHandOff.ImageRead</c>, moved).
    /// </summary>
    internal static readonly AsyncLocal<Action<Entity, Record>?> ImageRead = new();

    /// <summary>
    /// <see cref="IPipelineCore.ExecuteCoreAsync"/> for a caller that holds <paramref name="mirror"/>, the mirror registered for
    /// <paramref name="request"/> (the pipeline's chain mirror, or the call's own mirror of <see cref="ExecuteCoreAsync"/>). A message
    /// on the model (<see cref="IPipelineCore.AnswersOnModel"/>) runs as the core's entry adapter ran it (S.2e3b, Decisions 7 and 8;
    /// S.2e3c1, Decision 7): the nested delegate through <see cref="CrudNested"/>, the server values read in as model values, the
    /// caller's Target kept in step (<see cref="KeepCallerTarget"/>, also when the core fails), and the model response paired with
    /// today's typed SDK response (<see cref="CoreResponses.Typed"/>) in <paramref name="mirror"/>, either filled with plain copies of
    /// the rows of a Retrieve or RetrieveMultiple (<see cref="CopyReadResults"/>) or written out once. The model response returned is
    /// that ordinary twin, not a shell. Any other message goes to the core unchanged: the core's bridge crosses it with the request's
    /// chain mirror.
    /// </summary>
    /// <remarks><see cref="CoreCall.ServerSetValues"/> of <paramref name="call"/> are SDK values, as
    /// <see cref="InvocationContext.ServerSetValues"/> are.</remarks>
    /// <inheritdoc cref="IPipelineCore.ExecuteCoreAsync" path="/exception"/>
    internal static async Task<Response> CoreAsync(IPipelineCore core, Request request, SdkMirror mirror, CoreCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(call);
        if (!core.AnswersOnModel(request.Name ?? "")) return await core.ExecuteCoreAsync(request, call, ct);
        Response response;
        try
        {
            // The server values of a Create are SDK values (the pipeline's InvocationContext.ServerSetValues): read in as model values.
            response = await core.ExecuteCoreAsync(request,
                call with { Nested = CrudNested(mirror, call.Nested), ServerSetValues = ModelValues(call.ServerSetValues) }, ct);
        }
        finally
        {
            KeepCallerTarget(mirror, request);
        }
        // The core answers a message on the model with its canonical name (S.2e3c3b1 Task 2, F2).
        var typed = CoreResponses.Typed(response.Name ?? "");
        mirror.Pair(response, typed);
        if (response.Name is "Retrieve" or "RetrieveMultiple") CopyReadResults(response, typed);
        else mirror.Out(response);
        return response;
    }

    /// <summary>
    /// <see cref="IPipelineCore.ExecuteNonPipelineAsync"/> for a caller that holds <paramref name="mirror"/>, the mirror registered for
    /// <paramref name="request"/>. A message on the model (the access reads, RetrieveUserPrivileges and the reads of S.2e3c1 Task 6)
    /// answers with a model response of its own: it is paired with today's typed SDK response (<see cref="CoreResponses.Typed"/>) and
    /// written out once, as the core's entry adapter did. A bridged message crossed with the request's mirror, so its response already
    /// has its SDK twin there and is returned as it is.
    /// </summary>
    /// <remarks>Precondition: <paramref name="mirror"/> is <c>SdkMirror.For(request)</c>, the mirror registered for
    /// <paramref name="request"/> (a bridged message crosses with it, and the twin test relies on it).</remarks>
    /// <inheritdoc cref="IPipelineCore.ExecuteNonPipelineAsync" path="/exception"/>
    internal static async Task<Response> NonPipelineAsync(IPipelineCore core, Request request, SdkMirror mirror, Caller caller,
        DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(mirror);
        var response = await core.ExecuteNonPipelineAsync(request, caller, session, ct);
        if (mirror.TryGetSdk(response, out _)) return response;
        mirror.Pair(response, CoreResponses.Typed(response.Name ?? ""));
        mirror.Out(response);
        return response;
    }

    /// <summary>
    /// <see cref="IPipelineCore.RetrieveImageAsync"/> with the record written out into <paramref name="mirror"/> once (S.2e3c1 Task 6,
    /// moved from the core), so the record's SDK twin there is the core's own <c>Entity</c> (the steps' images and the SDK callers read
    /// it); <see cref="ImageRead"/> is called with both. Null, with nothing converted, when the record does not exist.
    /// </summary>
    internal static async Task<Record?> ImageAsync(IPipelineCore core, RecordReference target, string[]? attributes, DataSession session,
        SdkMirror mirror, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(mirror);
        var record = await core.RetrieveImageAsync(target, attributes, session, ct);
        if (record is null) return record;
        var image = (Entity)mirror.Out(record)!;
        ImageRead.Value?.Invoke(image, record);
        return record;
    }

    /// <summary>
    /// <see cref="IPipelineCore.ExecuteCoreAsync"/> on an SDK request. <paramref name="nested"/> runs a message through the pipeline as
    /// the same caller, at the same depth, in <paramref name="session"/>'s transaction (its steps fire and the security guard checks
    /// it): a core operation that writes a record the way a message would (e.g. the Update of a record by CommitFileBlocksUpload) uses
    /// it, and gets the SDK request the core built (its CLR type and the marks keyed on it, such as <see cref="CascadeHint"/>, are
    /// kept); without it a file message is refused (<see cref="InvalidOperationException"/>). <paramref name="serverSetValues"/> are the
    /// columns the server writes on a Create (plan 4.1c, <see cref="InvocationContext.ServerSetValues"/>), as SDK values: set after the
    /// Target is validated, so a column not valid for Create may be among them; the Target never holds them. Null for every other call.
    /// </summary>
    /// <inheritdoc cref="IPipelineCore.ExecuteCoreAsync" path="/exception"/>
    public static async Task<OrganizationResponse> ExecuteCoreAsync(IPipelineCore core, OrganizationRequest request, Caller caller,
        DataSession session, SdkNestedRequest? nested = null, IReadOnlyDictionary<string, object?>? serverSetValues = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(core);
        var (mirror, model) = Cross(request);
        var response = await CoreAsync(core, model, mirror, new CoreCall(caller, session, Nested(mirror, nested), serverSetValues), ct);
        return mirror.Out(response)!;
    }

    /// <inheritdoc cref="IPipelineCore.WrittenEntities"/>
    /// <remarks>
    /// The answer depends only on the message name and the <c>Relationship</c> parameter, so the model request holds those two: the rest
    /// of the request (a bulk request's targets) is not converted.
    /// </remarks>
    public static IReadOnlyCollection<string> WrittenEntities(IPipelineCore core, OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(request);
        var model = new Request { Name = request.RequestName };
        if (request.Parameters.TryGetValue("Relationship", out var relationship))
            model.Parameters["Relationship"] = relationship is Relationship sdk ? sdk.ToModel() : relationship;
        return core.WrittenEntities(model);
    }

    /// <inheritdoc cref="IPipelineCore.ExecuteNonPipelineAsync"/>
    public static async Task<OrganizationResponse> ExecuteNonPipelineAsync(IPipelineCore core, OrganizationRequest request,
        Caller caller, DataSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(core);
        var (mirror, model) = Cross(request);
        return mirror.Out(await NonPipelineAsync(core, model, mirror, caller, session, ct))!;
    }

    /// <inheritdoc cref="IPipelineCore.LockExistsAsync"/>
    public static Task<bool> LockExistsAsync(IPipelineCore core, EntityReference target, DataSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(target);
        return core.LockExistsAsync(target.ToModel(), session, ct);
    }

    /// <inheritdoc cref="IPipelineCore.RetrieveImageAsync"/>
    /// <remarks>
    /// The image is converted once, into the call's mirror (<see cref="ImageAsync"/>), so the caller gets the core's own <c>Entity</c>,
    /// the record's twin there, and nothing is converted back.
    /// </remarks>
    public static Task<Entity?> RetrieveImageAsync(IPipelineCore core, EntityReference target, string[]? attributes,
        DataSession session, CancellationToken ct = default) =>
        RetrieveImageAsync(core, target, attributes, session, new SdkMirror(), ct);

    /// <summary><see cref="RetrieveImageAsync(IPipelineCore, EntityReference, string[], DataSession, CancellationToken)"/> with the
    /// call's mirror named by the caller (tests count what crosses it).</summary>
    internal static async Task<Entity?> RetrieveImageAsync(IPipelineCore core, EntityReference target, string[]? attributes,
        DataSession session, SdkMirror mirror, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(target);
        var model = (RecordReference)mirror.In(target)!;
        var record = await ImageAsync(core, model, attributes, session, mirror, ct);
        if (record is null) return null;
        return mirror.TryGetSdk(record, out var sdk) ? (Entity)sdk! : (Entity?)mirror.Out(record);
    }

    /// <summary>
    /// The model response of an SDK response that crosses back to a model caller (the core's bridge and the nested delegates use it):
    /// an unread shell (<see cref="SdkMirror.Shell"/>) when it has no model twin yet or its twin is still an unread shell; otherwise its
    /// twin, read in first (in before out), so an SDK change made since it last crossed is kept.
    /// </summary>
    public static Response ModelResponse(SdkMirror mirror, OrganizationResponse sdk)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(sdk);
        return mirror.TryGetModel(sdk, out var model) && model is Response twin && !mirror.IsUnread(twin)
            ? mirror.In(sdk)!
            : mirror.Shell(sdk);
    }

    /// <summary>
    /// The model twin of an SDK request in a mirror of its own, registered for it. An SDK request has no chain mirror of its own (only a
    /// model request is registered), so every call gets a fresh one; both directions of the call use it. A record team mark the caller
    /// left on the SDK request goes onto the model request (S.2e3c2a, Decision 6: <see cref="AccessTeamRules.CopyMark"/>), as the
    /// pipeline copies the audit action at its crossing.
    /// </summary>
    private static (SdkMirror Mirror, Request Model) Cross(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mirror = new SdkMirror();
        var model = mirror.In(request)!;
        SdkMirror.Register(model, mirror);
        AccessTeamRules.CopyMark(request, model);
        return (mirror, model);
    }

    /// <summary>
    /// An SDK nested delegate as a model one, through the call's mirror: the model request the core hands over is written out into its
    /// SDK twin (the core's own request), and the SDK response comes back as an unread shell (or, when it already crossed, read in).
    /// </summary>
    private static NestedRequest? Nested(SdkMirror mirror, SdkNestedRequest? nested) =>
        nested is null
            ? null
            : async (request, ct) => ModelResponse(mirror, await nested(mirror.Out(request)!, ct));

    /// <summary>The server values of an SDK caller as model values (<see cref="SdkRecords.ValueToModel"/>).</summary>
    private static Dictionary<string, object?>? ModelValues(IReadOnlyDictionary<string, object?>? values) =>
        values?.ToDictionary(kv => kv.Key, kv => SdkRecords.ValueToModel(kv.Value), StringComparer.Ordinal);

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
    /// <c>Entity</c> through the pipeline or <see cref="ExecuteCoreAsync"/>): before S.2e3b the SDK handler ran on it and changed it in
    /// place, as the SDK twins still do. The validation removes the values of calculated columns (removed from the twin, which writes
    /// nothing); a write by key (an elastic table, which the core marks on the Target, <see cref="CoreTargetMarks.IsElastic"/>; key
    /// attributes on the Target or on a reference value) gets ids and its <c>partitionid</c>, so the Target is written out into its twin.
    /// An Assign by key gets the assignee's id filled in the same way. A Target without an SDK twin (a model caller) has nothing to keep.
    /// Invariant: the model bodies do change the Target in place (validation strips calculated values, a write by key fills ids and
    /// <c>partitionid</c>), and this method is what copies exactly those changes onto the caller's <c>Entity</c>; the request itself
    /// is otherwise current as it is, so the SDK request is not written out again.
    /// </summary>
    private static void KeepCallerTarget(SdkMirror mirror, Request request)
    {
        if ((IsMessage(request, "Create") || IsMessage(request, "Update")) && request.Parameters.TryGetValue("Target", out var target)
            && target is Record record && mirror.TryGetSdk(record, out var twin) && twin is Entity entity)
        {
            if (CoreTargetMarks.IsElastic(record) || record.KeyAttributes.Count > 0
                || record.Attributes.Values.Any(v => v is RecordReference { KeyAttributes.Count: > 0 }))
                mirror.Out(record);
            else
                foreach (var name in entity.Attributes.Keys.Where(name => !record.Contains(name)).ToList()) entity.Attributes.Remove(name);
        }
        else if (IsMessage(request, "Assign") && request.Parameters.TryGetValue("Assignee", out var assignee)
                 && assignee is RecordReference { KeyAttributes.Count: > 0 } byKey && mirror.TryGetSdk(byKey, out _))
            mirror.Out(byKey);
    }

    /// <summary>True when <paramref name="request"/> is the core message <paramref name="canonicalName"/> (any case, as the core's
    /// lookup of its canonical name).</summary>
    private static bool IsMessage(Request request, string canonicalName) =>
        string.Equals(request.Name, canonicalName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The model nested delegate a CRUD body calls (the cascaded children of a Delete), through the chain mirror: the request the core
    /// built is registered with the mirror and written out into today's typed SDK request (<see cref="CoreResponses.TypedRequest"/>, a
    /// <see cref="DeleteRequest"/> for a cascaded child) carrying the SDK cascade mark. The guard and the pipeline read the cascade mark
    /// on the model request; the SDK copy serves only the SDK-typed callers of <see cref="ExecuteCoreAsync"/> and a plug-in that casts
    /// the request. The model delegate runs on the model request itself.
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

/// <summary>
/// The typed SDK response of each message on the model (S.2e3b, Decision 7; S.2e3c1, Decision 4): the boundary (<see cref="CoreSdkBoundary"/>;
/// the core's entry adapter before S.2e3c3b1, Decision 3) pairs the model response of a handler with one of these and writes it out
/// once, so a caller that takes the SDK response (the pipeline, an SDK caller of the boundary) gets the typed object filled. The core's
/// SDK twins reach it through <c>InternalsVisibleTo</c>.
/// </summary>
internal static class CoreResponses
{
    /// <summary>A new typed response for <paramref name="canonicalName"/>, its <c>ResponseName</c> set.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The name is not a message on the model (ordinal, canonical case).</exception>
    public static OrganizationResponse Typed(string canonicalName)
    {
        OrganizationResponse response = canonicalName switch
        {
            "Create" => new CreateResponse(),
            "Retrieve" => new RetrieveResponse(),
            "Update" => new UpdateResponse(),
            "Delete" => new DeleteResponse(),
            "RetrieveMultiple" => new RetrieveMultipleResponse(),
            "Assign" => new AssignResponse(),
            "Associate" => new AssociateResponse(),
            "Disassociate" => new DisassociateResponse(),
            // S.2e3c1 Task 3: access, sharing and record teams.
            "GrantAccess" => new GrantAccessResponse(),
            "ModifyAccess" => new ModifyAccessResponse(),
            "RevokeAccess" => new RevokeAccessResponse(),
            "RetrievePrincipalAccess" => new RetrievePrincipalAccessResponse(),
            "RetrieveSharedPrincipalsAndAccess" => new RetrieveSharedPrincipalsAndAccessResponse(),
            "AddUserToRecordTeam" => new AddUserToRecordTeamResponse(),
            "RemoveUserFromRecordTeam" => new RemoveUserFromRecordTeamResponse(),
            // S.2e3c1 Task 4: role privileges, exchange rate, change tracking.
            "AddPrivilegesRole" => new AddPrivilegesRoleResponse(),
            "ReplacePrivilegesRole" => new ReplacePrivilegesRoleResponse(),
            "RemovePrivilegeRole" => new RemovePrivilegeRoleResponse(),
            "RetrieveUserPrivileges" => new RetrieveUserPrivilegesResponse(),
            "RetrieveExchangeRate" => new RetrieveExchangeRateResponse(),
            "RetrieveEntityChanges" => new RetrieveEntityChangesResponse(),
            // S.2e3c1 Task 5: queues.
            "AddToQueue" => new AddToQueueResponse(),
            "RemoveFromQueue" => new RemoveFromQueueResponse(),
            "RouteTo" => new RouteToResponse(),
            "AddPrincipalToQueue" => new AddPrincipalToQueueResponse(),
            "PickFromQueue" => new PickFromQueueResponse(),
            "ReleaseToQueue" => new ReleaseToQueueResponse(),
            "RetrieveUserQueues" => new RetrieveUserQueuesResponse(),
            // S.2e3c1 Task 6: the non-pipeline reads.
            "WhoAmI" => new WhoAmIResponse(),
            "RetrieveAppComponents" => new RetrieveAppComponentsResponse(),
            "RetrieveUnpublished" => new RetrieveUnpublishedResponse(),
            "InitializeFrom" => new InitializeFromResponse(),
            "RetrieveFormattedImportJobResults" => new RetrieveFormattedImportJobResultsResponse(),
            _ => throw new ArgumentOutOfRangeException(nameof(canonicalName), canonicalName, "Not a message on the model."),
        };
        response.ResponseName = canonicalName;
        return response;
    }

    /// <summary>
    /// A new typed request for <paramref name="name"/> (S.2e3b Task 8: the twin of a request the core issues on the model, such as the
    /// Delete of a cascaded child, which the security guard and the plug-ins see as before; S.2e3c1 Task 5, Decision 4: the Create,
    /// Update and Delete of <c>queueitem</c> and the Associate of <c>queuemembership_association</c> the queue messages issue): one of
    /// the eight CRUD requests, or a plain <see cref="OrganizationRequest"/> of that name for any other message.
    /// </summary>
    public static OrganizationRequest TypedRequest(string? name) => name switch
    {
        "Create" => new CreateRequest(),
        "Retrieve" => new RetrieveRequest(),
        "Update" => new UpdateRequest(),
        "Delete" => new DeleteRequest(),
        "RetrieveMultiple" => new RetrieveMultipleRequest(),
        "Assign" => new AssignRequest(),
        "Associate" => new AssociateRequest(),
        "Disassociate" => new DisassociateRequest(),
        _ => new OrganizationRequest(name),
    };

    /// <summary>True for the eight CRUD names <see cref="Typed"/> answers.</summary>
    public static bool IsCrud(string canonicalName) =>
        canonicalName is "Create" or "Retrieve" or "Update" or "Delete" or "RetrieveMultiple" or "Assign" or "Associate" or "Disassociate";
}
