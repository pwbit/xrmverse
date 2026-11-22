using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Pipeline.CustomApis;

namespace Xrmd.Pipeline.Workflows;

/// <summary>
/// An activated XAML action (a process of category 3, plan 2.14, ruling W14) whose message <see cref="Message"/> the pipeline runs:
/// its request is checked against <see cref="Inputs"/> before stage 10 (with <see cref="CustomApiValidator"/>'s type rules), and stage 30
/// runs its steps (<see cref="IWorkflowPipelineHook.RunActionAsync"/>).
/// </summary>
/// <param name="ActivationId">The activation row (<c>workflow</c> of type 2) that runs.</param>
/// <param name="Name">The process's name.</param>
/// <param name="Message">The <c>sdkmessage</c> name the action answers.</param>
/// <param name="BoundEntity">The table of its <c>Target</c>; null for a global action (<c>primaryentity</c> <c>none</c>).</param>
/// <param name="Inputs">Its input arguments but the bound <c>Target</c>, as request parameters (an argument not required is optional).</param>
/// <param name="Outputs">Its output arguments, as response properties.</param>
public sealed record ActionDefinition(Guid ActivationId, string Name, string Message, string? BoundEntity, IReadOnlyList<CustomApiField> Inputs,
    IReadOnlyList<CustomApiField> Outputs);

/// <summary>An activated real-time process (or server-side business rule) a stage runs (plan 2.14, ruling W6).</summary>
/// <param name="ActivationId">The activation row (<c>workflow</c> of type 2) that runs.</param>
/// <param name="Name">The process's name.</param>
/// <param name="Rank">Its <c>rank</c>, which orders it among the stage's synchronous plug-in steps (plug-ins first at equal rank).</param>
public sealed record RealtimeWorkflow(Guid ActivationId, string Name, int Rank);

/// <summary>
/// The owner of the record a message writes, which the <c>scope</c> of a process filters on (ruling W5): a user or team, and its
/// business unit.
/// </summary>
public sealed record RecordOwner(Guid OwnerId, Guid BusinessUnitId);

/// <summary>
/// What the pipeline asks of the classic workflows (plan 2.14), implemented by <c>Xrmd.Workflows</c>, which this project cannot
/// reference. <see cref="NoWorkflowPipelineHook"/> stands in when that project is not registered (pipeline tests).
/// </summary>
public interface IWorkflowPipelineHook
{
    /// <summary>
    /// Called after the commit of a transaction that wrote <c>workflow</c> or <c>processtrigger</c>: the activated processes are
    /// read again on next use.
    /// </summary>
    void Invalidate();

    /// <summary>
    /// Plan 2.21, ruling A16: called for a Create or Update of <c>workflow</c> by a caller other than <see cref="Caller.Internal"/>
    /// (for which it does nothing), inside the message's transaction, before the pre-operation plug-ins and again at the internal
    /// check of stage 20, after them (they could have widened the Target). An Update that changes only <c>statecode</c>,
    /// <c>statuscode</c> or <c>ownerid</c> passes (SetState and the owner have their own rules); any other Update must target a Draft
    /// definition, send no state with it, and leave <c>category</c>, <c>primaryentity</c>, <c>type</c> and an action's
    /// <c>uniquename</c> as stored. The row as it will be (the stored row overlaid with <paramref name="target"/>; the Target alone
    /// for a Create) must have XAML that parses, passes the activation's validation and has no step xrmverse cannot run; on an
    /// Update, only when it sends a column the parser or the validator reads (ruling R9: SetState validates again before anything
    /// runs). The record of an Update is the Target's Id, else its <c>workflowid</c> attribute.
    /// </summary>
    /// <param name="message">Create or Update.</param>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 listing every problem.</exception>
    Task ValidateWriteAsync(DataSession s, Caller caller, string message, Entity target, CancellationToken ct);

    /// <summary>
    /// Called at the internal check of stage 20 of an Update of <c>workflow</c> that changes its <c>statecode</c> (SetState),
    /// inside the Update's transaction: <paramref name="state"/> 1 activates the definition, 0 deactivates it (ruling W4).
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 when the definition cannot be activated (the reason named).</exception>
    Task OnSetStateAsync(DataSession s, Caller caller, Guid workflowId, int state, CancellationToken ct);

    /// <summary>
    /// Called after the core Assign of a <c>workflow</c> row, inside the Assign's transaction: the current activation of a definition
    /// gets the definition's new owner, whom its background jobs, <c>runas</c>-owner runs and scope follow (Learn: background
    /// workflows run as their owner).
    /// </summary>
    Task OnAssignedAsync(DataSession s, Guid workflowId, CancellationToken ct);

    /// <summary>
    /// True when an activated real-time process or server-side business rule has <paramref name="entity"/> as its primary entity: only
    /// then does the pipeline read what <see cref="GetRealtimeAsync"/> needs (the record's owner). Answered from memory once the
    /// activated processes are loaded, so a table without any costs nothing.
    /// </summary>
    Task<bool> HasRealtimeAsync(string entity, CancellationToken ct);

    /// <summary>
    /// The real-time processes and server-side business rules that <paramref name="message"/> (Create, Update, Assign, SetState or
    /// Delete: a SetState arrives as an Update but is named here, ruling W5) triggers at <paramref name="stage"/> (20 or 40), ordered by
    /// rank then id.
    /// </summary>
    /// <param name="sentColumns">The columns of the request's <c>Target</c> as the client sent it.</param>
    /// <param name="recordOwner">The record's owner before the operation; null for a table without owner (no scope applies).</param>
    Task<IReadOnlyList<RealtimeWorkflow>> GetRealtimeAsync(string message, string? entity, int stage, IReadOnlyCollection<string> sentColumns,
        RecordOwner? recordOwner, CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="w"/> in the stage of <paramref name="context"/>, inside the message's transaction (<paramref name="s"/>,
    /// ruling W19). Its steps' requests go through <paramref name="executor"/>, the pipeline running the message, one level deeper
    /// than <paramref name="invocation"/>.
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">A step's fault; the plug-in abort (400, ISVAborted, the step's text) for a
    /// stop step that cancels or a business rule's error (W11, W15); 400 for a process with steps xrmverse cannot run (W20).</exception>
    Task RunRealtimeAsync(PipelineExecutor executor, RealtimeWorkflow w, XrmdPluginExecutionContext context, InvocationContext invocation,
        DataSession s, CancellationToken ct);

    /// <summary>
    /// True when an activated background workflow has <paramref name="entity"/> as its primary entity (answered from memory, as
    /// <see cref="HasRealtimeAsync"/>): only then does the pipeline read what <see cref="QueueBackgroundAsync"/> needs.
    /// </summary>
    Task<bool> HasBackgroundAsync(string entity, CancellationToken ct);

    /// <summary>
    /// At post-operation, inside the message's transaction (<paramref name="s"/>, ruling W9): queues one job per activated background
    /// workflow that <paramref name="message"/> triggers (W5, as for <see cref="GetRealtimeAsync"/>) on the record of
    /// <paramref name="context"/>, owned by the workflow's owner (W7), created by the caller, one level deeper than
    /// <paramref name="invocation"/>. The jobs become visible, and the worker is woken, when the transaction commits; a rollback
    /// queues nothing.
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 when a triggered workflow is not owned by a user.</exception>
    Task QueueBackgroundAsync(string message, string? entity, IReadOnlyCollection<string> sentColumns, RecordOwner? recordOwner,
        XrmdPluginExecutionContext context, InvocationContext invocation, DataSession s, CancellationToken ct);

    /// <summary>
    /// The activated XAML action whose message is <paramref name="message"/> (ignoring case, ruling W14); null when no action has that
    /// message. Answered from memory once the processes are loaded.
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 when the message is an action's whose definition is not activated: a
    /// Draft action is never run as a step-only custom message.</exception>
    Task<ActionDefinition?> FindActionAsync(string message, CancellationToken ct);

    /// <summary>
    /// Stage 30 of a call to <paramref name="action"/> (W14): runs its steps in the call's transaction (<paramref name="s"/>), with its
    /// input arguments read from <paramref name="context"/>'s <c>InputParameters</c>, and writes the output arguments its steps set to
    /// the context's <c>OutputParameters</c>. Its steps' requests go through <paramref name="executor"/>, one level deeper.
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">A step's fault; the plug-in abort (400, ISVAborted) for a stop step that
    /// cancels; 400 for an output of another type than declared or a step xrmverse cannot run.</exception>
    Task RunActionAsync(PipelineExecutor executor, ActionDefinition action, XrmdPluginExecutionContext context, InvocationContext invocation,
        DataSession s, CancellationToken ct);

    /// <summary>
    /// Stage 30 of <c>ExecuteWorkflow</c> (W21), in the message's transaction (<paramref name="s"/>): queues a job of a background
    /// on-demand workflow, or runs a real-time one, on the request's <c>EntityId</c>.
    /// </summary>
    /// <returns>The id of the queued job; <see cref="Guid.Empty"/> for a real-time workflow, which has run.</returns>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 for a request or a workflow that cannot run on demand; 404 or 403 for
    /// the record; the run's faults.</exception>
    Task<Guid> ExecuteWorkflowAsync(PipelineExecutor executor, OrganizationRequest request, XrmdPluginExecutionContext context,
        InvocationContext invocation, DataSession s, CancellationToken ct);
}

/// <summary>
/// The hook when classic workflows are not registered: nothing to invalidate, no write or state change refused (the rules of
/// <c>SystemTableRules.DemandValidProcessWrite</c> still hold), nothing to run.
/// </summary>
public sealed class NoWorkflowPipelineHook : IWorkflowPipelineHook
{
    public static readonly NoWorkflowPipelineHook Instance = new();

    public void Invalidate()
    {
    }

    public Task ValidateWriteAsync(DataSession s, Caller caller, string message, Entity target, CancellationToken ct) => Task.CompletedTask;

    public Task OnSetStateAsync(DataSession s, Caller caller, Guid workflowId, int state, CancellationToken ct) => Task.CompletedTask;

    public Task OnAssignedAsync(DataSession s, Guid workflowId, CancellationToken ct) => Task.CompletedTask;

    public Task<bool> HasRealtimeAsync(string entity, CancellationToken ct) => Task.FromResult(false);

    public Task<IReadOnlyList<RealtimeWorkflow>> GetRealtimeAsync(string message, string? entity, int stage, IReadOnlyCollection<string> sentColumns,
        RecordOwner? recordOwner, CancellationToken ct) => Task.FromResult<IReadOnlyList<RealtimeWorkflow>>([]);

    public Task RunRealtimeAsync(PipelineExecutor executor, RealtimeWorkflow w, XrmdPluginExecutionContext context, InvocationContext invocation,
        DataSession s, CancellationToken ct) => throw new InvalidOperationException("No classic workflow is registered.");

    public Task<bool> HasBackgroundAsync(string entity, CancellationToken ct) => Task.FromResult(false);

    public Task QueueBackgroundAsync(string message, string? entity, IReadOnlyCollection<string> sentColumns, RecordOwner? recordOwner,
        XrmdPluginExecutionContext context, InvocationContext invocation, DataSession s, CancellationToken ct) => Task.CompletedTask;

    public Task<ActionDefinition?> FindActionAsync(string message, CancellationToken ct) => Task.FromResult<ActionDefinition?>(null);

    public Task RunActionAsync(PipelineExecutor executor, ActionDefinition action, XrmdPluginExecutionContext context, InvocationContext invocation,
        DataSession s, CancellationToken ct) => throw new InvalidOperationException("No classic workflow is registered.");

    public Task<Guid> ExecuteWorkflowAsync(PipelineExecutor executor, OrganizationRequest request, XrmdPluginExecutionContext context,
        InvocationContext invocation, DataSession s, CancellationToken ct) =>
        throw new NotSupportedException("ExecuteWorkflow needs the classic workflows, which are not registered.");
}
