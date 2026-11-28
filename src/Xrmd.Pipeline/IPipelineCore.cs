using Xrmd.Abstractions;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;

namespace Xrmd.Pipeline;

/// <summary>
/// Runs a message through the pipeline within the current execution (see <see cref="CoreCall.Nested"/>). The model request belongs to
/// the core's chain: its mirror (<c>SdkMirror.For</c>) is the core call's, so its SDK twin is the request the core built.
/// </summary>
public delegate Task<Response> NestedRequest(Request request, CancellationToken ct);

/// <summary>
/// How the core runs a message: as <paramref name="Caller"/>, in <paramref name="Session"/>, with <paramref name="Nested"/> for the
/// messages it writes through the pipeline (null when the caller has no pipeline), and with the columns the server writes on a Create
/// (<paramref name="ServerSetValues"/>, plan 4.1c; null for every other call). S.2e3c3b1 (Decision 3): the values that reach the core
/// are model values; <see cref="InvocationContext.ServerSetValues"/> holds SDK values, which the SDK boundary
/// (<see cref="CoreSdkBoundary.CoreAsync"/>) reads in.
/// </summary>
public sealed record CoreCall(Caller Caller, DataSession Session, NestedRequest? Nested = null,
    IReadOnlyDictionary<string, object?>? ServerSetValues = null);

/// <summary>
/// The data-layer operations the pipeline calls into (stage 30 core and the lookups plugins need), on the model only (S.2e3c3b1,
/// Decision 2). The SDK signatures of the callers that still hold SDK objects are the members of the SDK boundary
/// (<see cref="CoreSdkBoundary"/>).
/// <para>
/// A message on the model (<see cref="AnswersOnModel"/>, and the messages answered outside the pipeline whose body runs on the model)
/// answers with a model response of its own: the core registers no mirror for the request, pairs nothing with the response and keeps
/// no SDK object in step. A bridged message crosses to its SDK handler with the request's mirror (<c>SdkMirror.For(request)</c>, or a
/// new one the core registers): when the caller registered the mirror, the response is usually an unread <em>shell</em>
/// (<c>SdkMirror.Shell</c>) of the handler's typed response, whose results are empty until the twin is read in, so every model read or
/// write of it calls <c>mirror.In(twin)</c> first; when the core registered it, the response comes back already read in.
/// </para>
/// </summary>
public interface IPipelineCore
{
    /// <summary>
    /// Stage 30 of a data message (Create, Update, Delete, Retrieve, RetrieveMultiple, Assign, Associate, Disassociate).
    /// Reads its parameters from <c>request.Parameters</c>, which is the same collection as <c>InputParameters</c>.
    /// <para>
    /// <see cref="CoreCall.Nested"/> runs a message through the pipeline as the same caller, at the same depth, in the session's
    /// transaction (its steps fire and the security guard checks it): a core operation that writes a record the way a message would
    /// (e.g. the Update of a record by CommitFileBlocksUpload) uses it.
    /// </para>
    /// <para>
    /// <see cref="CoreCall.ServerSetValues"/> are the columns the server writes on a Create (plan 4.1c), as model values (S.2e3c3b1; the
    /// SDK values of <see cref="InvocationContext.ServerSetValues"/> are read in by <see cref="CoreSdkBoundary.CoreAsync"/>): set after
    /// the Target is validated, so a column not valid for Create may be among them; the Target never holds them. Null for every other
    /// call.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="CoreCall.ServerSetValues"/> is set on a message other than Create, or
    /// names a column the table does not have; a file message without <see cref="CoreCall.Nested"/>.</exception>
    /// <exception cref="NotSupportedException">This core writes no server values; any message the core does not implement.</exception>
    Task<Response> ExecuteCoreAsync(Request request, CoreCall call, CancellationToken ct);

    /// <summary>
    /// True for the messages <see cref="ExecuteCoreAsync"/> implements; any other message runs only as a custom action
    /// (a custom <c>sdkmessage</c>), without a core operation.
    /// </summary>
    bool IsCoreMessage(string messageName);

    /// <summary>
    /// True for a core message whose body runs on the model request (S.2e3c2b, Decision 8): a message of <see cref="IsCoreMessage"/>
    /// that is not bridged to an SDK handler (any case, as <see cref="IsCoreMessage"/>). The pipeline holds no window at the core for
    /// such a message: the core never works on its SDK request.
    /// </summary>
    bool AnswersOnModel(string messageName);

    /// <summary>
    /// The entities a write message changes besides its primary entity (e.g. the intersect of a security relationship), so
    /// that the caches built on them are invalidated after the commit; empty when there are none.
    /// </summary>
    IReadOnlyCollection<string> WrittenEntities(Request request);

    /// <summary>
    /// True for the messages answered outside the pipeline (no steps, no transaction work): <c>WhoAmI</c>,
    /// <c>RetrieveEntity</c>, <c>RetrieveAttribute</c> and the metadata messages (whose writes a plug-in cannot run).
    /// </summary>
    bool IsNonPipelineMessage(string messageName);

    /// <summary>
    /// Answers a message for which <see cref="IsNonPipelineMessage"/> is true, for <paramref name="caller"/> (WhoAmI: its
    /// user and business unit, and the organization).
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 for a missing parameter; 404 for an unknown entity or attribute.</exception>
    Task<Response> ExecuteNonPipelineAsync(Request request, Caller caller, DataSession session, CancellationToken ct);

    /// <summary>
    /// True when the record <paramref name="target"/> (a standard table, by id) exists. The read takes an update lock held to
    /// the end of <paramref name="session"/>'s transaction (<c>UPDLOCK, HOLDLOCK</c> on the primary key), so a concurrent caller
    /// asking for the same id waits until that transaction ends, whether the row exists or not (#76, ruling B10).
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="session"/> has no transaction (the lock would end at once).</exception>
    Task<bool> LockExistsAsync(RecordReference target, DataSession session, CancellationToken ct);

    /// <summary>Loads a record for an entity image; <c>null</c> when it does not exist.</summary>
    Task<Record?> RetrieveImageAsync(RecordReference target, string[]? attributes, DataSession session, CancellationToken ct);

    /// <summary>
    /// Resolves a <c>systemuser</c> to a <see cref="Caller"/> (its current business unit); 404 when it does not exist, 403
    /// <c>UserDisabled</c> (<c>0x80040225</c>) when it is disabled.
    /// </summary>
    Task<Caller> ResolveUserAsync(Guid systemUserId, DataSession session, CancellationToken ct);

    /// <summary>The id of the built-in SYSTEM user.</summary>
    Task<Guid> SystemUserIdAsync(DataSession session, CancellationToken ct);

    /// <summary>The organization id and name.</summary>
    Task<(Guid Id, string Name)> OrganizationAsync(DataSession session, CancellationToken ct);

    /// <summary>The organization's plugin trace log setting (0 off, 1 exception, 2 all).</summary>
    Task<int> PluginTraceLogSettingAsync(DataSession session, CancellationToken ct);
}
