using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core.Duplicates;
using Xrmd.Core.Elastic;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Pipeline;
using Xrmd.Security;

namespace Xrmd.Core;

/// <summary>
/// S.2e3c1 (Decision 2): the SDK message bridge. The model dispatch (<c>CoreOperations.ExecuteMessageAsync</c>,
/// <c>ExecuteNonPipelineMessageAsync</c>) hands it every message whose body is still an SDK handler (<see cref="Bridged"/>) and every
/// name that is no core message, which the SDK switches of <c>CoreOperations.cs</c> refuse with today's
/// <see cref="NotSupportedException"/>.
/// <para>
/// A message crosses with the request's chain mirror (<see cref="SdkMirror.For"/>, or a new mirror registered for the request), so
/// the handler gets the SDK twin of the request: the same instance the caller or the pipeline holds, with its CLR type, its
/// early-bound values and the marks keyed on it. While the pipeline holds the request at the core (<see cref="CoreHandOff"/>), the
/// handler runs on the held SDK request and nothing is converted: the pipeline reads it back once. Otherwise the model request is
/// written out into its twin before the handler and read back in after it. The typed SDK response crosses back as an unread shell
/// (<see cref="SdkMirror.Shell"/>), so a response nobody reads is never converted.
/// </para>
/// </summary>
internal static class CoreSdkMessages
{
    /// <summary>
    /// The messages that go to their SDK handler (canonical case; looked up ignoring case): exactly the ones whose bodies belong to
    /// later steps (Decision 1): mail, duplicates, elastic and rollups (S.2e4), files and audit (S.2f), the metadata reads (S.2e3c3).
    /// Every other core message runs on the model (Tasks 3 to 6 removed their names from this list).
    /// </summary>
    public static IReadOnlySet<string> Bridged { get; } = new HashSet<string>(
        [
            EmailMessages.SendEmail, EmailMessages.GetTrackingTokenEmail,
            DuplicateRulePublisher.PublishMessage, DuplicateRulePublisher.UnpublishMessage, DuplicateFinder.RetrieveMessage,
            BulkDetectDuplicatesJob.Message,
            ElasticUpsert.MessageName, ElasticCosmosQuery.MessageName,
            CoreOperations.CalculateRollupFieldMessage,
            "InitializeFileBlocksUpload", "UploadBlock", "CommitFileBlocksUpload", "DeleteFile", "InitializeFileBlocksDownload", "DownloadBlock",
            .. AuditMessages.Messages,
            "RetrieveEntity", "RetrieveAttribute",
            .. MetadataOperations.Messages.Where(name => !MetadataOperations.IsWriteMessage(name)),
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A core message on its SDK handler (<c>CoreOperations.ExecuteCoreSdkAsync</c>), through the request's chain mirror; the model
    /// nested delegate crosses as an SDK one (<see cref="Nested"/>).
    /// </summary>
    public static async Task<Response> ExecuteAsync(CoreOperations core, Request request, CoreCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(call);
        var (mirror, sdk, held, created) = Enter(request);
        var response = await core.ExecuteCoreSdkAsync(sdk, call.Caller, call.Session, Nested(mirror, call.Nested), call.ServerSetValues, ct);
        return Leave(mirror, sdk, held, created, response);
    }

    /// <summary>A message answered outside the pipeline on its SDK handler (<c>CoreOperations.ExecuteNonPipelineSdkAsync</c>).</summary>
    public static async Task<Response> ExecuteNonPipelineAsync(CoreOperations core, Request request, Caller caller, DataSession session,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        var (mirror, sdk, held, created) = Enter(request);
        var response = await core.ExecuteNonPipelineSdkAsync(sdk, caller, session, ct);
        return Leave(mirror, sdk, held, created, response);
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
    /// the mirror (it can read the twin in), or the response read in when the bridge made the mirror (<c>Enter</c>, <paramref name="created"/>),
    /// since such a caller has no mirror to read it with.
    /// </summary>
    private static Response Leave(SdkMirror mirror, OrganizationRequest sdk, bool held, bool created, OrganizationResponse response)
    {
        if (!held) mirror.In(sdk);
        return created ? mirror.In(response)! : CoreSdkBoundary.ModelResponse(mirror, response);
    }

    /// <summary>
    /// The model nested delegate as the SDK one the handlers call, through the chain's mirror: the SDK request the core built is read
    /// into its model twin, which is registered with the mirror, and the model response is written out into the SDK one (an unread
    /// shell gives back its typed twin unchanged).
    /// </summary>
    private static SdkNestedRequest? Nested(SdkMirror mirror, NestedRequest? nested) =>
        nested is null ? null : new ChainNested(mirror, nested).RunAsync;

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
