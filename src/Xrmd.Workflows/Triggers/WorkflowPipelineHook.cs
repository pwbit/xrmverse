using System.Data;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Pipeline.Workflows;
using Xrmd.Security;
using Xrmd.Workflows.Actions;
using Xrmd.Workflows.Engine;
using Xrmd.Workflows.OnDemand;
using Xrmd.Workflows.Xaml;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows;

/// <summary>
/// The classic workflows' side of the pipeline (plan 2.14): the checks of a public write of a process (plan 2.21, ruling A16),
/// activation on SetState, the registry's invalidation (Task 2), the
/// real-time workflows and server-side business rules a stage runs (Task 5, rulings W5–W8, W11, W15, W19, W20), and the background
/// jobs a write queues and wakes (Task 6, rulings W4, W9, W10), the XAML actions and <c>ExecuteWorkflow</c> (Task 7, rulings W14, W21).
/// </summary>
/// <remarks>
/// The engine is created per run on the <see cref="PipelineExecutor"/> that runs the message (passed to
/// <see cref="RunRealtimeAsync"/>), never resolved from the container: the executor depends on this hook, so an engine injected
/// here would close the cycle executor → hook → engine → executor.
/// </remarks>
public sealed class WorkflowPipelineHook(WorkflowRegistry registry, WorkflowActivation activation, IPipelineCore core,
    IMetadataStore metadata, SecurityCache security, Background.WorkflowJobs jobs, WorkflowStore store, SecurityGuard guard,
    Custom.CustomActivityRunner? activities = null) : IWorkflowPipelineHook
{
    private const string Target = "Target";

    private ExecuteWorkflowMessage? _executeWorkflow;

    private ExecuteWorkflowMessage ExecuteWorkflow => _executeWorkflow ??= new(registry, store, guard, jobs, metadata, core, activities);

    public void Invalidate() => registry.Invalidate();

    /// <summary>The columns an Update may change on an Activated definition: its state (SetState) and its owner (plan 2.21, A16).</summary>
    private static readonly HashSet<string> StateAndOwnerColumns = new(StringComparer.Ordinal)
    {
        S.WorkflowId, "statecode", "statuscode", "ownerid", "owneridtype",
    };

    /// <summary>
    /// The columns the parser or the validator reads (ruling R9, which narrows A16): an Update that sends none of them is not
    /// validated again. <c>category</c>, <c>primaryentity</c>, <c>uniquename</c> and <c>type</c> are immutable or refused anyway.
    /// </summary>
    private static readonly HashSet<string> DefinitionColumns = new(StringComparer.Ordinal)
    {
        S.Xaml, S.ModeColumn, S.TriggerOnCreate, S.TriggerOnDelete, S.TriggerOnUpdateAttributeList, S.CreateStage, S.UpdateStage,
        S.DeleteStage, S.CategoryColumn, S.PrimaryEntity, S.UniqueName, S.TypeColumn,
    };

    /// <remarks>
    /// Ruling R9 (narrowing A16): an Update of a Draft definition is validated again (XAML, validator, unsupported steps) only when it
    /// sends a column of <see cref="DefinitionColumns"/>. An edit of other columns (a rename, a description) of an imported Draft that
    /// has unsupported steps, or is of a category xrmverse does not run (1, 4, 5), is accepted: SetState validates the definition again
    /// before anything runs, so nothing invalid can execute, and Dataverse lets a deactivated definition be edited. The Draft check,
    /// the immutable columns, the server-written columns and the sent <c>category</c> are checked on every Update.
    /// </remarks>
    public async Task ValidateWriteAsync(DataSession s, Caller caller, string message, Entity target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);
        if (caller == Caller.Internal || target.LogicalName != S.Table) return;

        WorkflowRow row;
        var problems = new List<string>();
        var revalidate = true;
        var stale = false;
        if (message == "Create")
        {
            row = WorkflowStore.FromEntity(target);
        }
        else
        {
            if (target.Attributes.Keys.All(StateAndOwnerColumns.Contains)) return;
            // The record is named by the Target's Id or, as the pipeline and the core read it, by its workflowid attribute.
            var id = target.Id != Guid.Empty ? target.Id
                : target.Attributes.TryGetValue(S.WorkflowId, out var key) && key is Guid g ? g
                : Guid.Empty;
            if (id == Guid.Empty)
                throw XrmdFaultException.InvalidArgument("An Update of a process names it by its workflowid.");
            if (target.Contains("statecode") || target.Contains("statuscode"))
                throw XrmdFaultException.InvalidArgument(
                    $"The Update of the process {id} changes its state with other columns: change the state alone, with SetState.");
            // Locked until the end of the transaction, as an activation locks it: the Draft state checked here is the one written over.
            if (await store.GetAsync(s, id, lockRow: true, ct) is not { } stored) return; // the core operation's 404
            if (stored.Type != (int)S.Type.Definition)
                throw XrmdFaultException.InvalidArgument(
                    $"The process {id} is not a definition (type {stored.Type}): only a definition (type 1) is edited.");
            if (stored.StateCode == S.ActivatedState)
                throw XrmdFaultException.InvalidArgument(
                    $"The process '{stored.Name ?? stored.Id.ToString()}' is Activated: a process is edited only while Draft; deactivate it with SetState first.");
            var merged = WorkflowStore.ToEntity(stored);
            foreach (var (column, value) in target.Attributes) merged[column] = value;
            row = WorkflowStore.FromEntity(merged) with { Id = id, OwnerId = stored.OwnerId };
            if (row.Category != stored.Category) problems.Add($"The column {S.CategoryColumn} cannot change ({stored.Category} to {row.Category}).");
            if (!string.Equals(row.PrimaryEntity, stored.PrimaryEntity, StringComparison.Ordinal))
                problems.Add($"The column {S.PrimaryEntity} cannot change ('{stored.PrimaryEntity}' to '{row.PrimaryEntity}').");
            if (row.Type != stored.Type) problems.Add($"The column {S.TypeColumn} cannot change ({stored.Type} to {row.Type}).");
            if (stored.Category == (int)S.Category.Action && !string.Equals(row.UniqueName, stored.UniqueName, StringComparison.Ordinal))
                problems.Add($"The column {S.UniqueName} of an action, its message name, cannot change ('{stored.UniqueName}' to '{row.UniqueName}').");
            revalidate = target.Attributes.Keys.Any(DefinitionColumns.Contains);
            // R10: what the labels are made of changes (the name, the description, the step labels of the XAML).
            stale = !string.Equals(row.Name, stored.Name, StringComparison.Ordinal)
                    || !string.Equals(row.Description ?? "", stored.Description ?? "", StringComparison.Ordinal)
                    || !string.Equals(row.Xaml, stored.Xaml, StringComparison.Ordinal);
        }

        if (revalidate) problems.AddRange(DefinitionProblems(row));
        if (problems.Count > 0)
            throw XrmdFaultException.InvalidArgument(
                $"The {message} of the process '{row.Name ?? row.Id.ToString()}' is refused: {string.Join(" ", problems)}");
        if (stale) await MarkLabelsStaleAsync(s, row.Id, ct);
    }

    /// <summary>
    /// Ruling R10: the kept XML of an imported process whose name, description or XAML an accepted Update changes is marked
    /// (<see cref="S.StaleLabelsAttribute"/>), so that the solution export writes its labels from the row and the XAML, not the labels the
    /// import kept. Written in the Update's transaction (rolled back with it); nothing for a process without kept XML.
    /// </summary>
    private static async Task MarkLabelsStaleAsync(DataSession s, Guid id, CancellationToken ct)
    {
        // On the path of every rename of a Draft definition (P11), so in the dialect's SQL (plan 2.7a, final review I1).
        string? kept;
        await using (var read = s.Command(
                         $"SELECT {s.Quote(SystemTableRules.ComponentXml)} FROM {s.DataTable(S.Table)} WHERE {s.Quote(S.WorkflowId)} = @id"))
        {
            read.AddParameter("@id", DbType.Guid, id);
            kept = await read.ExecuteScalarAsync(ct) as string;
        }
        if (string.IsNullOrWhiteSpace(kept)) return;
        System.Xml.Linq.XElement root;
        try
        {
            // No DTD (final review): as the other parses of stored XML. IgnoreWhitespace keeps XElement.Parse's tree.
            using var reader = System.Xml.XmlReader.Create(new StringReader(kept), new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true,
            });
            root = System.Xml.Linq.XElement.Load(reader);
        }
        catch (System.Xml.XmlException)
        {
            return;
        }
        if (root.Attribute(S.StaleLabelsAttribute) is not null) return;
        root.SetAttributeValue(S.StaleLabelsAttribute, "1");
        await using var write = s.Command(
            $"UPDATE {s.DataTable(S.Table)} SET {s.Quote(SystemTableRules.ComponentXml)} = @xml WHERE {s.Quote(S.WorkflowId)} = @id");
        write.AddParameter("@id", DbType.Guid, id);
        write.AddParameter("@xml", DbType.String, root.ToString(System.Xml.Linq.SaveOptions.DisableFormatting), -1);
        await write.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// What makes <paramref name="row"/> refused as written (A16): a category xrmverse does not run, no or malformed XAML, an
    /// activation validation error (<see cref="WorkflowValidator"/>), and each step xrmverse cannot run (W16, W20).
    /// </summary>
    private static IEnumerable<string> DefinitionProblems(WorkflowRow row)
    {
        if (row.Category is not ((int)S.Category.Workflow or (int)S.Category.BusinessRule or (int)S.Category.Action))
            return [$"Processes of category {row.Category} are not written."];
        if (string.IsNullOrWhiteSpace(row.Xaml)) return ["The process has no XAML."];
        WorkflowDefinition definition;
        try
        {
            definition = XamlWorkflowParser.Parse(row.Xaml, row.Category);
        }
        catch (XamlWorkflowException ex)
        {
            return [ex.Message];
        }
        return WorkflowValidator.Validate(definition, row)
            .Concat(definition.Unsupported.Select(u => $"The step {u.Label} cannot be run by xrmverse: {u.Reason}."))
            .ToList();
    }

    /// <summary>State 1 activates the definition, 0 deactivates it; any other state is left to the core operation's 400.</summary>
    public Task OnSetStateAsync(DataSession s, Caller caller, Guid workflowId, int state, CancellationToken ct) => state switch
    {
        S.ActivatedState => activation.ActivateAsync(s, caller, workflowId, ct),
        S.DraftState => activation.DeactivateAsync(s, caller, workflowId, ct),
        _ => Task.CompletedTask,
    };

    public Task OnAssignedAsync(DataSession s, Guid workflowId, CancellationToken ct) => activation.OnAssignedAsync(s, workflowId, ct);

    public async Task<bool> HasRealtimeAsync(string entity, CancellationToken ct) =>
        (await registry.GetActivatedForEntityAsync(entity, ct)).Any(a => IsRealtime(a.Row));

    /// <summary>A real-time workflow or a server-side (Entity scope) business rule: what can run in the pipeline.</summary>
    private static bool IsRealtime(WorkflowRow row) =>
        row.Category switch
        {
            (int)S.Category.Workflow => row.Mode == (int)S.Mode.Realtime,
            (int)S.Category.BusinessRule => row.EntityScopeRule,
            _ => false,
        };

    public async Task<IReadOnlyList<RealtimeWorkflow>> GetRealtimeAsync(string message, string? entity, int stage,
        IReadOnlyCollection<string> sentColumns, RecordOwner? recordOwner, CancellationToken ct) =>
        (await MatchAsync(message, entity, stage, (int)S.Mode.Realtime, IsRealtime, sentColumns, recordOwner, ct))
        .Select(row => new RealtimeWorkflow(row.Id, row.Name ?? row.Id.ToString(), row.Rank))
        .ToList();

    /// <summary>A background workflow (category 0, mode 0): what a post-operation queues as a job (W9).</summary>
    private static bool IsBackground(WorkflowRow row) => row.Category == (int)S.Category.Workflow && row.Mode == (int)S.Mode.Background;

    public async Task<bool> HasBackgroundAsync(string entity, CancellationToken ct) =>
        (await registry.GetActivatedForEntityAsync(entity, ct)).Any(a => IsBackground(a.Row));

    public async Task QueueBackgroundAsync(string message, string? entity, IReadOnlyCollection<string> sentColumns, RecordOwner? recordOwner,
        XrmdPluginExecutionContext context, InvocationContext invocation, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        // W5: the writes the core makes as a cascade trigger no workflow.
        if (invocation.IsCoreCascade || entity is null || context.PrimaryEntityId == Guid.Empty) return;
        var rows = await MatchAsync(message, entity, null, (int)S.Mode.Background, IsBackground, sentColumns, recordOwner, ct);
        foreach (var row in rows)
            await jobs.QueueAsync(s, row, new EntityReference(entity, context.PrimaryEntityId), message,
                invocation.Caller.UserId, invocation.Depth + 1, ct);
    }

    /// <summary>The activated processes of <paramref name="entity"/> that <paramref name="isCandidate"/> keeps and the message triggers.</summary>
    private async Task<IReadOnlyList<WorkflowRow>> MatchAsync(string message, string? entity, int? stage, int mode, Func<WorkflowRow, bool> isCandidate,
        IReadOnlyCollection<string> sentColumns, RecordOwner? recordOwner, CancellationToken ct)
    {
        if (entity is null) return [];
        var candidates = (await registry.GetActivatedForEntityAsync(entity, ct)).Select(a => a.Row).Where(isCandidate).ToList();
        if (candidates.Count == 0) return [];

        // The business units the scopes compare: the record owner's and the workflows' owners' (W5), and the parents of units (scope 3).
        var units = new Dictionary<Guid, Guid>();
        foreach (var row in candidates)
            if (row.OwnerId is { } owner && row.OwningBusinessUnitId is { } unit) units[owner.Id] = unit;
        if (recordOwner is not null) units[recordOwner.OwnerId] = recordOwner.BusinessUnitId;
        var tree = recordOwner is not null && candidates.Any(r => r.Scope == 3) ? await security.GetBusinessUnitTreeAsync(ct) : null;
        Guid BusinessUnitOf(Guid id) =>
            units.TryGetValue(id, out var unit) ? unit : tree?.AncestorsOf(id) is [var parent, ..] ? parent : Guid.Empty;

        return Triggers.WorkflowTriggerMatcher
            .Match(candidates, message, entity, sentColumns, recordOwner?.OwnerId ?? Guid.Empty, BusinessUnitOf, stage, mode);
    }

    public async Task RunRealtimeAsync(PipelineExecutor executor, RealtimeWorkflow w, XrmdPluginExecutionContext context,
        InvocationContext invocation, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        // Deactivated between the match and the run (a step of an earlier process of the stage did it): it no longer runs.
        if (await registry.FindAsync(w.ActivationId, ct) is not { } activated) return;
        var (row, definition) = activated;

        var run = new WorkflowRun
        {
            Workflow = row,
            Primary = new EntityReference(context.PrimaryEntityName, context.PrimaryEntityId),
            // W8 and W15: at stage 20 the steps read and write the message's Target (shared with the plug-ins of the stage).
            Target = context.Stage == (int)S.Stage.Pre && context.InputParameters.TryGetValue(Target, out var t) ? t as Entity : null,
            Caller = await CallerAsync(row, invocation.Caller, s, ct),
            // W19: the message's transaction; the steps' requests are nested in the message (its context as their parent).
            Invocation = invocation with { Session = s, Parent = context.Model! },
            Stage = context.Stage,
            MessageName = context.MessageName,
        };

        switch (await new WorkflowEngine(executor, metadata, jobs, activities).RunAsync(definition, run, ct))
        {
            case WorkflowOutcome.Completed:
                return;
            case WorkflowOutcome.Canceled canceled:
                // W11, W15: InvalidPluginExecutionException semantics; the whole operation rolls back.
                throw PluginRunner.Aborted(canceled.Message.Length > 0
                    ? canceled.Message
                    : $"The workflow '{row.Name}' canceled the operation.");
            default:
                // A wait cannot be activated in a real-time workflow (W4, W10): never reached, but never silently ignored either.
                throw XrmdFaultException.InvalidArgument($"Workflow '{row.Name}' cannot wait: it runs in real time.");
        }
    }

    // ---- actions (W14) and ExecuteWorkflow (W21) ---------------------------------------------------------------------------

    public async Task<ActionDefinition?> FindActionAsync(string message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (await registry.FindActionMessageAsync(message, ct) is not { } action) return null;
        if (action.ActivationId is not { } activationId || await registry.FindAsync(activationId, ct) is not var (row, definition))
            throw XrmdFaultException.InvalidArgument(
                $"The action '{action.Name ?? action.DefinitionId.ToString()}' of the message '{action.Message}' is not activated, so the message cannot run.");
        var (inputs, outputs) = ActionMessages.Fields(row, definition);
        return new ActionDefinition(row.Id, row.Name ?? row.Id.ToString(), action.Message, ActionMessages.BoundEntityOf(row), inputs, outputs);
    }

    /// <remarks>
    /// The run's variables start as the input arguments the request carries (an absent optional one is null) and the bound record as
    /// <c>Target</c>; the primary record is the bound record (none for a global action). An output argument the steps did not set is
    /// left out of the response (from memory, checklist 1d); one they set must be of its declared type, with no conversion (400).
    /// </remarks>
    public async Task RunActionAsync(PipelineExecutor executor, ActionDefinition action, XrmdPluginExecutionContext context,
        InvocationContext invocation, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        // Deactivated between the lookup and stage 30 (a plug-in of stage 20 did it): explicit, not a silent no-op.
        if (await registry.FindAsync(action.ActivationId, ct) is not var (row, definition))
            throw XrmdFaultException.InvalidArgument($"The action '{action.Name}' of the message '{action.Message}' is no longer activated.");

        var run = new WorkflowRun
        {
            Workflow = row,
            Primary = action.BoundEntity is { } bound
                ? new EntityReference(bound, context.PrimaryEntityId)
                : new EntityReference(S.NoPrimaryEntity, Guid.Empty),
            // Learn (create-actions): "Actions always run in the context of the calling user": runas is ignored for an action.
            Caller = invocation.Caller,
            // W14: the caller's transaction; the steps' requests are nested in the action's message.
            Invocation = invocation with { Session = s, Parent = context.Model! },
            Stage = 30,
            MessageName = action.Message,
        };
        foreach (var argument in definition.Arguments.Where(a => a.IsInput))
            run.Variables[argument.Name] = context.InputParameters.TryGetValue(argument.Name, out var value) ? value : null;

        switch (await new WorkflowEngine(executor, metadata, jobs, activities).RunAsync(definition, run, ct))
        {
            case WorkflowOutcome.Completed:
                break;
            case WorkflowOutcome.Canceled canceled:
                throw PluginRunner.Aborted(canceled.Message.Length > 0 ? canceled.Message : $"The action '{action.Name}' canceled the operation.");
            default:
                throw XrmdFaultException.InvalidArgument($"The action '{action.Name}' cannot wait: an action runs in real time.");
        }

        foreach (var output in action.Outputs)
        {
            if (!run.Variables.TryGetValue(output.UniqueName, out var value)) continue;
            if (value is not null && !Pipeline.CustomApis.CustomApiValidator.Fits(output, value))
                throw XrmdFaultException.InvalidArgument(
                    $"The action '{action.Name}' set its output argument '{output.UniqueName}' to a value of type {value.GetType().Name}, " +
                    $"not {Pipeline.CustomApis.CustomApiValidator.Describe(output.Type)}. xrmverse converts no value.");
            context.OutputParameters[output.UniqueName] = value;
        }
    }

    public Task<Guid> ExecuteWorkflowAsync(PipelineExecutor executor, OrganizationRequest request, XrmdPluginExecutionContext context,
        InvocationContext invocation, DataSession s, CancellationToken ct) =>
        ExecuteWorkflow.ExecuteAsync(executor, request, context, invocation, s, ct);

    /// <summary>W7: <c>runas</c> 0 runs as the workflow's owner, 1 as the caller of the message.</summary>
    private Task<Caller> CallerAsync(WorkflowRow row, Caller caller, DataSession s, CancellationToken ct) => RunAsCallerAsync(core, row, caller, s, ct);

    /// <summary>W7: <c>runas</c> 0 runs as the process's owner (a user), 1 as the caller of the message.</summary>
    /// <exception cref="XrmdFaultException">400 when the process runs as its owner and that owner is not a user.</exception>
    internal static async Task<Caller> RunAsCallerAsync(IPipelineCore core, WorkflowRow row, Caller caller, DataSession s, CancellationToken ct)
    {
        if (row.RunAs != (int)S.RunAs.Owner) return caller;
        return row.OwnerId switch
        {
            { LogicalName: "systemuser" } owner => await core.ResolveUserAsync(owner.Id, s, ct),
            _ => throw XrmdFaultException.InvalidArgument(
                $"Workflow '{row.Name}' runs as its owner, which is not a user; xrmverse runs a real-time workflow only as a user."),
        };
    }
}
