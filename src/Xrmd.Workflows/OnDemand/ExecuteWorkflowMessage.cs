using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Security;
using Xrmd.Workflows.Engine;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows.OnDemand;

/// <summary>
/// Stage 30 of <c>ExecuteWorkflow</c> (plan 2.14, ruling W21), called by the pipeline through
/// <see cref="Xrmd.Pipeline.Workflows.IWorkflowPipelineHook.ExecuteWorkflowAsync"/> in the message's transaction. The
/// <see cref="SecurityGuard"/> has demanded Read on the process (its definition or activation) before stage 10; this demands Read on
/// the record. A background workflow is queued (the response <c>Id</c> is the job); a real-time workflow runs here, in the message's
/// transaction, and the response <c>Id</c> is <see cref="Guid.Empty"/> (from memory, checklist 1i). Either runs as the user who started
/// it, whatever its <c>runas</c> (Learn: "workflows set to run on-demand always run under the security context of the logged on
/// user"); the background job is that user's. <c>prvWorkflowExecution</c> is demanded too (SecurityGuard).
/// </summary>
/// <remarks>
/// The checks, in this order, each its own fault (codes from memory, checklist 1i): <c>WorkflowId</c> and <c>EntityId</c> given (400);
/// the process exists (404); it is a workflow (category 0) definition or activation (400); <c>InputArguments</c> empty (400); its
/// definition is Activated and that activation is the one named (400); <c>ondemand</c> (400); the record exists (404) and the caller can
/// read it (403).
/// </remarks>
public sealed class ExecuteWorkflowMessage(WorkflowRegistry registry, WorkflowStore store, SecurityGuard guard, Background.WorkflowJobs jobs,
    IMetadataStore metadata, IPipelineCore core, Custom.CustomActivityRunner? activities = null)
{
    private const string WorkflowIdParameter = "WorkflowId", EntityIdParameter = "EntityId", InputArgumentsParameter = "InputArguments";

    /// <summary>Runs <paramref name="request"/>; see the class remarks.</summary>
    /// <returns>The id of the queued job, or <see cref="Guid.Empty"/> for a real-time workflow, which has run.</returns>
    /// <exception cref="XrmdFaultException">The faults of the class remarks; the run's faults; the plug-in abort (400) for a real-time
    /// workflow that a stop step cancels.</exception>
    public async Task<Guid> ExecuteAsync(PipelineExecutor executor, OrganizationRequest request, XrmdPluginExecutionContext context,
        InvocationContext invocation, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(s);
        var workflowId = Id(request, WorkflowIdParameter);
        var entityId = Id(request, EntityIdParameter);

        var named = await store.GetAsync(s, workflowId, ct) ?? throw XrmdFaultException.ObjectDoesNotExist(S.Table, workflowId);
        var definition = named.Type switch
        {
            (int)S.Type.Definition => named,
            (int)S.Type.Activation when named.ParentWorkflowId is { } parent => await store.GetAsync(s, parent, ct),
            _ => null,
        } ?? throw XrmdFaultException.InvalidArgument(
            $"The process {workflowId} cannot run on demand: it is neither a process definition nor the activation of one.");
        var name = definition.Name ?? definition.Id.ToString();
        if (definition.Category != (int)S.Category.Workflow)
            throw XrmdFaultException.InvalidArgument($"The process '{name}' cannot run on demand: only a workflow (category 0) runs with ExecuteWorkflow.");
        if (request.Parameters.TryGetValue(InputArgumentsParameter, out var inputs) && inputs is not null
            && !IsEmpty(inputs))
            throw XrmdFaultException.InvalidArgument(
                $"The workflow '{name}' cannot take input arguments: xrmverse runs ExecuteWorkflow without InputArguments.");
        if (definition.StateCode != S.ActivatedState || definition.ActiveWorkflowId is not { } activationId
            || (named.Type == (int)S.Type.Activation && named.Id != activationId)
            || await registry.FindAsync(activationId, ct) is not (var activation, var steps))
            throw XrmdFaultException.InvalidArgument($"The workflow '{name}' cannot run on demand: it is not activated.");
        if (!definition.OnDemand)
            throw XrmdFaultException.InvalidArgument($"The workflow '{name}' is not available to run on demand.");

        var table = activation.PrimaryEntity is { Length: > 0 } primaryEntity && metadata.GetEntity(s, primaryEntity) is { } entity
            ? entity
            : throw XrmdFaultException.InvalidArgument($"The workflow '{name}' runs on the table '{activation.PrimaryEntity}', which does not exist.");
        var record = new EntityReference(table.LogicalName, entityId);
        if (await core.RetrieveImageAsync(record, [table.PrimaryIdAttribute], s, ct) is null)
            throw XrmdFaultException.ObjectDoesNotExist(table.LogicalName, entityId);
        await guard.DemandAccessAsync(invocation.Caller, record, AccessRights.ReadAccess, s, ct);

        if (activation.Mode == (int)S.Mode.Background)
            return await jobs.QueueAsync(s, activation, record, PipelineExecutor.ExecuteWorkflowMessage, invocation.Caller.UserId,
                invocation.Depth + 1, ct, runAs: invocation.Caller.UserId);

        var run = new WorkflowRun
        {
            Workflow = activation,
            Primary = record,
            // Learn: a workflow run on demand runs as the user who started it, whatever its runas.
            Caller = invocation.Caller,
            // The message's transaction; the steps' requests are nested in the message (its context as their parent).
            Invocation = invocation with { Session = s, Parent = context },
            Stage = 30,
            MessageName = PipelineExecutor.ExecuteWorkflowMessage,
        };
        switch (await new WorkflowEngine(executor, metadata, jobs, activities).RunAsync(steps, run, ct))
        {
            case WorkflowOutcome.Completed:
                return Guid.Empty;
            case WorkflowOutcome.Canceled canceled:
                throw PluginRunner.Aborted(canceled.Message.Length > 0 ? canceled.Message : $"The workflow '{activation.Name}' canceled the operation.");
            default:
                throw XrmdFaultException.InvalidArgument($"Workflow '{activation.Name}' cannot wait: it runs in real time.");
        }
    }

    /// <summary>An empty argument collection is no argument (the SDK's collection is no <see cref="System.Collections.IEnumerable"/>).</summary>
    private static bool IsEmpty(object arguments) => arguments switch
    {
        InputArgumentCollection collection => collection.Count == 0,
        System.Collections.IEnumerable list => !list.GetEnumerator().MoveNext(),
        _ => false,
    };

    private static Guid Id(OrganizationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && value is Guid id && id != Guid.Empty
            ? id
            : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.RequestName}' is missing or not a Guid.");
}
