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
/// (<paramref name="ServerSetValues"/>, plan 4.1c, <see cref="InvocationContext.ServerSetValues"/>; null for every other call).
/// </summary>
public sealed record CoreCall(Caller Caller, DataSession Session, NestedRequest? Nested = null,
    IReadOnlyDictionary<string, object?>? ServerSetValues = null);

/// <summary>
/// The data-layer operations the pipeline calls into (stage 30 core and the lookups plugins need), on the model. The SDK signatures
/// of the callers that still hold SDK objects are extension methods of the same names (<see cref="PipelineCoreSdk"/>).
/// <para>
/// A response of <see cref="ExecuteCoreAsync"/> or <see cref="ExecuteNonPipelineAsync"/> is the model twin of the core's typed SDK
/// response in the request's mirror (<c>SdkMirror.For(request)</c>), and <c>mirror.Out(response)</c> gives back the core's typed
/// instance. When the caller registered a mirror for the request (the pipeline and the SDK extension methods do), the response is
/// usually an unread <em>shell</em> (<c>SdkMirror.Shell</c>): its results are empty until the twin is read in, so every model read or
/// write of it calls <c>mirror.In(twin)</c> first, and a response nobody observes is never converted. When the request has no
/// registered mirror, the core registers a new one and returns the response already read in, so a model caller without a mirror can
/// read its results directly.
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
    /// <see cref="CoreCall.ServerSetValues"/> are the columns the server writes on a Create (plan 4.1c,
    /// <see cref="InvocationContext.ServerSetValues"/>): set after the Target is validated, so a column not valid for Create may be
    /// among them; the Target never holds them. Null for every other call.
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
