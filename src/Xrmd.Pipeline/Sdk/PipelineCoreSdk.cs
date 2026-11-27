using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Security;

namespace Xrmd.Pipeline;

/// <summary>
/// The SDK form of <see cref="NestedRequest"/>: runs a message through the pipeline within the current execution (see
/// <see cref="PipelineCoreSdk.ExecuteCoreAsync(IPipelineCore, OrganizationRequest, Caller, DataSession, SdkNestedRequest?, CancellationToken)"/>).
/// </summary>
public delegate Task<OrganizationResponse> SdkNestedRequest(OrganizationRequest request, CancellationToken ct);

/// <summary>
/// S.2e3a: the SDK signatures of <see cref="IPipelineCore"/>, for the callers that still hold SDK objects (Workflows, Solution, Admin,
/// Core.Solutions, the custom API authoring, the plug-in service and the tests; S.2e3c moves them). Each method crosses its arguments
/// into the model with a mirror of the call, which it registers for the model request (<see cref="SdkMirror.Register"/>), calls the
/// model member and crosses the answer back with the same mirror. The request a caller passes is therefore the SDK twin the core
/// works on, and the response it gets is the core's own typed instance (a <c>CreateResponse</c> stays a <c>CreateResponse</c>): it
/// sees the same objects, types and in-place changes as before the move.
/// </summary>
public static class PipelineCoreSdk
{
    /// <summary>
    /// <see cref="IPipelineCore.ExecuteCoreAsync"/> on an SDK request, without a nested pipeline: a file message, which writes its
    /// record through the pipeline, is refused (<see cref="InvalidOperationException"/>).
    /// </summary>
    /// <inheritdoc cref="IPipelineCore.ExecuteCoreAsync" path="/exception"/>
    public static Task<OrganizationResponse> ExecuteCoreAsync(this IPipelineCore core, OrganizationRequest request, Caller caller,
        DataSession session, CancellationToken ct) =>
        ExecuteCoreAsync(core, request, caller, session, null, null, ct);

    /// <summary>
    /// <see cref="IPipelineCore.ExecuteCoreAsync"/> on an SDK request with <paramref name="nested"/>, which runs a message through the
    /// pipeline as the same caller, at the same depth, in <paramref name="session"/>'s transaction (its steps fire and the security
    /// guard checks it): a core operation that writes a record the way a message would (e.g. the Update of a record by
    /// CommitFileBlocksUpload) uses it. It gets the SDK request the core built (its CLR type and the marks keyed on it, such as
    /// <see cref="CascadeHint"/>, are kept).
    /// </summary>
    /// <inheritdoc cref="IPipelineCore.ExecuteCoreAsync" path="/exception"/>
    public static Task<OrganizationResponse> ExecuteCoreAsync(this IPipelineCore core, OrganizationRequest request, Caller caller,
        DataSession session, SdkNestedRequest? nested, CancellationToken ct) =>
        ExecuteCoreAsync(core, request, caller, session, nested, null, ct);

    /// <summary>
    /// <see cref="ExecuteCoreAsync(IPipelineCore, OrganizationRequest, Caller, DataSession, SdkNestedRequest?, CancellationToken)"/>
    /// with the columns the server writes on a Create (plan 4.1c, <see cref="InvocationContext.ServerSetValues"/>): set after the
    /// Target is validated, so a column not valid for Create may be among them; the Target never holds them. Null for every other call.
    /// </summary>
    /// <inheritdoc cref="IPipelineCore.ExecuteCoreAsync" path="/exception"/>
    public static async Task<OrganizationResponse> ExecuteCoreAsync(this IPipelineCore core, OrganizationRequest request, Caller caller,
        DataSession session, SdkNestedRequest? nested, IReadOnlyDictionary<string, object?>? serverSetValues, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        var (mirror, model) = Cross(request);
        var response = await core.ExecuteCoreAsync(model, new CoreCall(caller, session, Nested(mirror, nested), serverSetValues), ct);
        return mirror.Out(response)!;
    }

    /// <inheritdoc cref="IPipelineCore.WrittenEntities"/>
    /// <remarks>
    /// The answer depends only on the message name and the <c>Relationship</c> parameter, so the model request holds those two: the rest
    /// of the request (a bulk request's targets) is not converted.
    /// </remarks>
    public static IReadOnlyCollection<string> WrittenEntities(this IPipelineCore core, OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(request);
        var model = new Request { Name = request.RequestName };
        if (request.Parameters.TryGetValue("Relationship", out var relationship))
            model.Parameters["Relationship"] = relationship is Relationship sdk ? sdk.ToModel() : relationship;
        return core.WrittenEntities(model);
    }

    /// <inheritdoc cref="IPipelineCore.ExecuteNonPipelineAsync"/>
    public static async Task<OrganizationResponse> ExecuteNonPipelineAsync(this IPipelineCore core, OrganizationRequest request,
        Caller caller, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        var (mirror, model) = Cross(request);
        return mirror.Out(await core.ExecuteNonPipelineAsync(model, caller, session, ct))!;
    }

    /// <inheritdoc cref="IPipelineCore.LockExistsAsync"/>
    public static Task<bool> LockExistsAsync(this IPipelineCore core, EntityReference target, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(target);
        return core.LockExistsAsync(target.ToModel(), session, ct);
    }

    /// <inheritdoc cref="IPipelineCore.RetrieveImageAsync"/>
    /// <remarks>
    /// The core converts the image once, into the call's mirror (<see cref="CoreHandOff.ReadImagesWith"/>), so the caller gets the core's
    /// own <c>Entity</c>, the record's twin there, and nothing is converted back.
    /// </remarks>
    public static Task<Entity?> RetrieveImageAsync(this IPipelineCore core, EntityReference target, string[]? attributes,
        DataSession session, CancellationToken ct) =>
        RetrieveImageAsync(core, target, attributes, session, new SdkMirror(), ct);

    /// <summary><see cref="RetrieveImageAsync(IPipelineCore, EntityReference, string[], DataSession, CancellationToken)"/> with the
    /// call's mirror named by the caller (tests count what crosses it).</summary>
    internal static async Task<Entity?> RetrieveImageAsync(IPipelineCore core, EntityReference target, string[]? attributes,
        DataSession session, SdkMirror mirror, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(target);
        var model = (RecordReference)mirror.In(target)!;
        CoreHandOff.ReadImagesWith(model, mirror);
        var record = await core.RetrieveImageAsync(model, attributes, session, ct);
        if (record is null) return null;
        return mirror.TryGetSdk(record, out var sdk) ? (Entity)sdk! : (Entity?)mirror.Out(record);
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

    /// <summary>
    /// The model response of an SDK response that crosses back to a model caller (the core's entry adapter and the nested delegates
    /// use it): an unread shell (<see cref="SdkMirror.Shell"/>) when it has no model twin yet or its twin is still an unread shell;
    /// otherwise its twin, read in first (in before out), so an SDK change made since it last crossed is kept.
    /// </summary>
    public static Response ModelResponse(SdkMirror mirror, OrganizationResponse sdk)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(sdk);
        return mirror.TryGetModel(sdk, out var model) && model is Response twin && !mirror.IsUnread(twin)
            ? mirror.In(sdk)!
            : mirror.Shell(sdk);
    }
}
