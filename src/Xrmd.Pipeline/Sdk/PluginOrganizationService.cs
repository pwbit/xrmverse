using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Model.Messages;

namespace Xrmd.Pipeline;

/// <summary>
/// The <see cref="IOrganizationServiceFactory"/> handed to one plug-in execution: its services run their messages
/// through the pipeline with <c>PipelineExecutor.ExecuteAsync(request, new InvocationContext(user, initiating,
/// parent.Depth + 1, parent, session, parent.CorrelationId) { Mirror = mirror })</c> on the plug-in's <see cref="DataSession"/> (same
/// transaction), synchronously (<paramref name="parent"/> is the model context of the calling step, whose view the executor reads
/// back before the call and writes again after it; <paramref name="mirror"/> is the chain's), and turn an <see cref="XrmdFaultException"/> into a
/// <see cref="FaultException{OrganizationServiceFault}"/> with its code (any other non-cancellation exception: code
/// <c>0x80040216</c> and its message). A failed call inside a transaction dooms it: the owning execution then rolls back
/// and fails with <see cref="PipelineExecutor.NoActiveTransactionMessage"/>, as in Dataverse. Once <paramref name="pluginToken"/> is cancelled (the plug-in
/// timed out, the request was cancelled or the plug-in returned) every call fails with a <see cref="FaultException"/>
/// without touching the connection.
/// </summary>
public sealed class PluginOrganizationServiceFactory(PipelineExecutor executor, IPipelineCore core, StepContext parent, SdkMirror mirror,
    DataSession session, CancellationToken pluginToken) : IOrganizationServiceFactory
{
    private const string RevokedMessage =
        "The plug-in execution has ended (it timed out or the request was cancelled); its organization service can no longer be used.";

    private readonly SandboxCallGate _gate = new(pluginToken, RevokedMessage);

    /// <summary>
    /// A service acting as <paramref name="userId"/>: the built-in SYSTEM user when null; the user of the execution
    /// (<c>context.UserId</c>) when it is that user or <see cref="Guid.Empty"/> (Learn: "a Guid.Empty value indicates the same
    /// user as IPluginExecutionContext.UserId"); else that <c>systemuser</c> (a fault with <c>ObjectDoesNotExist</c> when it
    /// does not exist, <c>UserDisabled</c> <c>0x80040225</c> when it is disabled). Its security applies to every call (the
    /// <c>SecurityGuard</c> runs for each nested call with its <see cref="Caller"/>). No <c>prvActOnBehalfOfAnotherUser</c> is
    /// needed: the plug-in was registered by an administrator. The initiating user stays the first level's.
    /// </summary>
    /// <remarks>
    /// A message the pipeline runs as <see cref="Caller.Internal"/> hands its plug-ins <c>UserId</c> = <see cref="Guid.Empty"/>.
    /// Plug-in code never acts as <see cref="Caller.Internal"/> (which the internal-only rules exempt, e.g. the system-managed
    /// teams of plan 2.16): there, "the user of the execution" is SYSTEM.
    /// </remarks>
    /// <exception cref="FaultException{OrganizationServiceFault}">The user does not exist or is disabled, or the service is revoked.</exception>
    public IOrganizationService CreateOrganizationService(Guid? userId)
    {
        var internalParent = parent.UserId == Guid.Empty;
        var sameUser = userId is { } id && (id == Guid.Empty || id == parent.UserId);
        var acting = sameUser && !internalParent
            ? new Caller(parent.UserId, parent.BusinessUnitId)
            : Call(async ct => await core.ResolveUserAsync(
                sameUser || userId is null ? await core.SystemUserIdAsync(session, ct) : userId.Value, session, ct));
        return new PluginOrganizationService(this, acting);
    }

    /// <summary>
    /// Waits (at most <paramref name="timeout"/>) for the call in flight to end, then keeps every later call out. The
    /// caller cancels the plug-in token first, so an in-flight call is being cancelled.
    /// </summary>
    /// <returns>False when a call was still in flight after <paramref name="timeout"/>.</returns>
    internal Task<bool> RevokeAsync(TimeSpan timeout) => _gate.RevokeAsync(timeout);

    /// <summary>
    /// Runs <paramref name="request"/> as <paramref name="caller"/> one level deeper. A failure inside the transaction
    /// dooms it (see <see cref="PipelineExecutor.MarkDoomed"/>), whether or not the plug-in catches the fault.
    /// </summary>
    internal OrganizationResponse Execute(OrganizationRequest request, Caller caller)
    {
        ArgumentNullException.ThrowIfNull(request);
        // WhoAmI, RetrieveEntity, RetrieveAttribute and the metadata messages have no pipeline (as for the front ends), answered
        // for the acting user (the metadata writes are refused: see IPipelineCore.ExecuteNonPipelineAsync).
        if (core.IsNonPipelineMessage(request.RequestName))
            return Call(ct => core.ExecuteNonPipelineAsync(request, caller, session, ct));
        var invocation = new InvocationContext(caller, parent.InitiatingUserId, parent.Depth + 1, parent, session, parent.CorrelationId)
        {
            Mirror = mirror,
        };
        return Call(async ct =>
        {
            try
            {
                return await executor.ExecuteAsync(request, invocation, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                executor.MarkDoomed(session);
                throw;
            }
        });
    }

    /// <summary>Runs <paramref name="operation"/> through the plug-in's <see cref="SandboxCallGate"/>.</summary>
    private T Call<T>(Func<CancellationToken, Task<T>> operation) => _gate.Call(operation);
}

/// <summary>
/// The <see cref="IOrganizationService"/> of a plug-in: every method becomes the corresponding SDK request and runs as
/// a nested pipeline execution whose parent context is the calling plug-in's (see <see cref="PluginOrganizationServiceFactory"/>).
/// </summary>
public sealed class PluginOrganizationService : IOrganizationService
{
    private readonly PluginOrganizationServiceFactory _factory;
    private readonly Caller _caller;

    internal PluginOrganizationService(PluginOrganizationServiceFactory factory, Caller caller)
    {
        _factory = factory;
        _caller = caller;
    }

    /// <inheritdoc />
    public Guid Create(Entity entity) => (Guid)Execute(new CreateRequest { Target = entity }).Results["id"];

    /// <inheritdoc />
    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
        (Entity)Execute(new RetrieveRequest { Target = new EntityReference(entityName, id), ColumnSet = columnSet }).Results["Entity"];

    /// <inheritdoc />
    public void Update(Entity entity) => Execute(new UpdateRequest { Target = entity });

    /// <inheritdoc />
    public void Delete(string entityName, Guid id) => Execute(new DeleteRequest { Target = new EntityReference(entityName, id) });

    /// <inheritdoc />
    public EntityCollection RetrieveMultiple(QueryBase query) =>
        (EntityCollection)Execute(new RetrieveMultipleRequest { Query = query }).Results["EntityCollection"];

    /// <inheritdoc />
    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        Execute(new AssociateRequest
        {
            Target = new EntityReference(entityName, entityId), Relationship = relationship, RelatedEntities = relatedEntities,
        });

    /// <inheritdoc />
    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        Execute(new DisassociateRequest
        {
            Target = new EntityReference(entityName, entityId), Relationship = relationship, RelatedEntities = relatedEntities,
        });

    /// <inheritdoc />
    /// <exception cref="FaultException{OrganizationServiceFault}">The message failed, or the plug-in execution has ended.</exception>
    public OrganizationResponse Execute(OrganizationRequest request) => _factory.Execute(request, _caller);
}
