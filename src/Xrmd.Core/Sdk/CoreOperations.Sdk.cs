using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Pipeline;

namespace Xrmd.Core;

/// <summary>
/// S.2e3a: the core's entry adapter, the model members of <see cref="IPipelineCore"/> over the SDK handlers of
/// <see cref="CoreOperations"/>, whose bodies have not moved yet (S.2e3b and S.2e3c move them).
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
        nested is null
            ? null
            : async (sdk, ct) =>
            {
                var model = mirror.In(sdk)!;
                SdkMirror.Register(model, mirror);
                return mirror.Out(await nested(model, ct))!;
            };
}
