using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Pipeline;
using Xrmd.Workflows.Engine;

namespace Xrmd.Workflows.Custom;

/// <summary>
/// The <see cref="IOrganizationServiceFactory"/> of one custom workflow activity run (plan 2.14, W17): its services run every request
/// through the pipeline as the built-in steps of the run do (<see cref="WorkflowEngine"/>: one level deeper than the run, in the run's
/// session when it has one, else each in its own transaction), synchronously, and turn a fault into a
/// <see cref="FaultException{OrganizationServiceFault}"/> with its code. In a real-time run or an action a failed call dooms the
/// transaction, as a plug-in's does. Once the activity has returned (or timed out) every call fails.
/// </summary>
/// <remarks>
/// <c>CreateOrganizationService(null)</c> and <c>CreateOrganizationService(Guid.Empty)</c> act as the run's user (Task 8b ruling, as the
/// plug-in fix of 2.16 reads <see cref="Guid.Empty"/>), as does the run user's own id; any other id acts as that user (a fault when it
/// does not exist or is disabled).
/// </remarks>
internal sealed class WorkflowActivityServices(PipelineExecutor executor, IPipelineCore core, IDbConnectionFactory factory, WorkflowRun run,
    CancellationToken token) : IOrganizationServiceFactory
{
    private const string RevokedMessage =
        "The custom workflow activity has ended (it timed out or the run was cancelled); its organization service can no longer be used.";

    private readonly SandboxCallGate _gate = new(token, RevokedMessage);

    /// <summary>
    /// Waits (at most <paramref name="timeout"/>) for the call in flight to end, then keeps every later call out (the runner cancels the
    /// token first): the run never goes on, or rolls back, while a call of the activity still uses its session.
    /// </summary>
    /// <returns>False when a call was still in flight after <paramref name="timeout"/>.</returns>
    internal Task<bool> RevokeAsync(TimeSpan timeout) => _gate.RevokeAsync(timeout);

    public IOrganizationService CreateOrganizationService(Guid? userId)
    {
        var acting = userId is null || userId == Guid.Empty || userId == run.Caller.UserId
            ? run.Caller
            : Call(ct => WithSessionAsync((s, c) => core.ResolveUserAsync(userId.Value, s, c), ct));
        return new WorkflowActivityService(this, acting);
    }

    /// <summary>Runs <paramref name="request"/> as <paramref name="caller"/>, one level deeper than the run.</summary>
    internal OrganizationResponse Execute(OrganizationRequest request, Caller caller)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (core.IsNonPipelineMessage(request.RequestName))
            return Call(ct => WithSessionAsync((s, c) => CoreSdkBoundary.ExecuteNonPipelineAsync(core, request, caller, s, c), ct));
        var invocation = run.Invocation with { Caller = caller, Depth = run.Invocation.Depth + 1 };
        return Call(async ct =>
        {
            try
            {
                return await executor.ExecuteAsync(request, invocation, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested && run.Invocation.Session is { } session)
            {
                executor.MarkDoomed(session);
                throw;
            }
        });
    }

    /// <summary>The run's session, or a connection of its own for a background run (no session).</summary>
    private async Task<T> WithSessionAsync<T>(Func<DataSession, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        if (run.Invocation.Session is { } session) return await operation(session, ct);
        await using var conn = await factory.OpenAsync(ct);
        return await operation(new DataSession(conn, null), ct);
    }

    /// <summary>Runs <paramref name="operation"/> through the shared <see cref="SandboxCallGate"/> (the plug-ins' gate).</summary>
    private T Call<T>(Func<CancellationToken, Task<T>> operation) => _gate.Call(operation);
}

/// <summary>The <see cref="IOrganizationService"/> of a custom workflow activity (see <see cref="WorkflowActivityServices"/>).</summary>
internal sealed class WorkflowActivityService(WorkflowActivityServices services, Caller caller) : IOrganizationService
{
    public Guid Create(Entity entity) => (Guid)Execute(new CreateRequest { Target = entity }).Results["id"];

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
        (Entity)Execute(new RetrieveRequest { Target = new EntityReference(entityName, id), ColumnSet = columnSet }).Results["Entity"];

    public void Update(Entity entity) => Execute(new UpdateRequest { Target = entity });

    public void Delete(string entityName, Guid id) => Execute(new DeleteRequest { Target = new EntityReference(entityName, id) });

    public EntityCollection RetrieveMultiple(QueryBase query) =>
        (EntityCollection)Execute(new RetrieveMultipleRequest { Query = query }).Results["EntityCollection"];

    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        Execute(new AssociateRequest { Target = new EntityReference(entityName, entityId), Relationship = relationship, RelatedEntities = relatedEntities });

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        Execute(new DisassociateRequest { Target = new EntityReference(entityName, entityId), Relationship = relationship, RelatedEntities = relatedEntities });

    public OrganizationResponse Execute(OrganizationRequest request) => services.Execute(request, caller);
}
