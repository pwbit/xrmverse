using System.Data;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.Workflows.Actions;
using Xrmd.Workflows.Xaml;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows;

/// <summary>
/// Activation and deactivation of a process definition (plan 2.14, ruling W4), called by the pipeline hook when a SetState
/// changes a <c>workflow</c>'s state (inside its transaction), and by the solution import. The caller needs Write on the
/// definition and, unless it is <see cref="Caller.Internal"/>, the activation rights of plan 2.21's A17
/// (<see cref="DemandActivationRightsAsync"/>). An action (category 3, W14) gets its <c>sdkmessage</c> and filter at its activation when it has none yet
/// (<see cref="ActionMessages"/>); its deactivation keeps them.
/// </summary>
public sealed class WorkflowActivation(WorkflowStore store, SecurityGuard guard, ActionMessages actions, IMetadataStore metadata)
{
    /// <summary>
    /// Parses and validates the definition's XAML (<see cref="XamlWorkflowParser"/>, <see cref="WorkflowValidator"/>), then creates
    /// its activation: a copy of the definition with <c>type</c> 2, <c>parentworkflowid</c> the definition and the state Activated;
    /// the definition becomes Activated with <c>activeworkflowid</c> the activation. Steps xrmverse cannot run do not prevent the
    /// activation: they are listed in <c>xrmverse_unsupportedsteps</c> of both rows (W4, W20). An already activated definition
    /// returns its activation.
    /// </summary>
    /// <returns>The id of the activation.</returns>
    /// <exception cref="XrmdFaultException">404 when the definition does not exist; 403 without Write on it or the activation
    /// rights (A17); 400 when it is not a definition (type 1) or cannot be activated (its category, no or malformed XAML, a validation error), the reason named.</exception>
    public async Task<Guid> ActivateAsync(DataSession s, Caller caller, Guid definitionId, CancellationToken ct) =>
        await ActivateAsync(s, await ValidateAsync(s, caller, definitionId, ct), ct);

    /// <summary>
    /// A definition checked for activation by <see cref="ValidateAsync"/>: its row and its unsupported steps, or the activation it
    /// already has.
    /// </summary>
    /// <param name="ActionPrefix">For an action: the publisher prefix its message is named with (W14).</param>
    public sealed record ValidatedActivation(WorkflowRow Row, string? UnsupportedSteps, Guid? ExistingActivation, string? ActionPrefix = null);

    /// <summary>
    /// The checks of <see cref="ActivateAsync(DataSession, Caller, Guid, CancellationToken)"/>, without any write: every 400 of an
    /// activation comes from here, so a caller that turns a refusal into a warning (the solution import) leaves nothing behind.
    /// </summary>
    /// <exception cref="XrmdFaultException">As <see cref="ActivateAsync(DataSession, Caller, Guid, CancellationToken)"/>.</exception>
    /// <param name="publisherPrefix">For an action: the prefix of its message's name (the import's publisher); null reads it from the
    /// solution that holds the process (<see cref="ActionMessages.PrefixAsync"/>).</param>
    public async Task<ValidatedActivation> ValidateAsync(DataSession s, Caller caller, Guid definitionId, CancellationToken ct,
        string? publisherPrefix = null)
    {
        var row = await DefinitionAsync(s, caller, definitionId, ct);
        if (row.StateCode == S.ActivatedState && row.ActiveWorkflowId is { } active) return new(row, row.UnsupportedSteps, active);

        var definition = Parse(row);
        var errors = WorkflowValidator.Validate(definition, row);
        if (errors.Count > 0) throw CannotActivate(row, string.Join(" ", errors));
        DemandNoRollupInWaits(definition, row);
        string? prefix = null;
        if (row.Category == (int)S.Category.Action)
        {
            // W14: the arguments must be of types the request check knows, and the message name must be free (else 400).
            ActionMessages.Fields(row, definition);
            prefix = publisherPrefix ?? await ActionMessages.PrefixAsync(s, row.Id, ct);
            await actions.DemandNameAsync(s, row, prefix, ct);
        }
        var unsupported = definition.Unsupported.Count == 0
            ? null
            : string.Join("\n", definition.Unsupported.Select(u => $"{u.Label}: {u.Reason}"));
        return new(row, unsupported, null, prefix);
    }

    /// <summary>The writes of an activation <see cref="ValidateAsync"/> accepted; returns the id of the activation.</summary>
    public async Task<Guid> ActivateAsync(DataSession s, ValidatedActivation validated, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(validated);
        if (validated.ExistingActivation is { } existing) return existing;
        var (row, unsupported) = (validated.Row, validated.UnsupportedSteps);
        // W14: the action's message first, so that the activation (a copy of the definition) has its sdkmessageid too.
        if (validated.ActionPrefix is { } prefix) row = await actions.EnsureAsync(s, row, prefix, null, ct);
        var activationId = Guid.NewGuid();
        await store.SaveAsync(s, row with
        {
            Id = activationId,
            Type = (int)S.Type.Activation,
            ParentWorkflowId = row.Id,
            ActiveWorkflowId = null,
            StateCode = S.ActivatedState,
            UnsupportedSteps = unsupported,
            OwnerId = row.OwnerId,
        }, [], ct);
        await store.SetStateAsync(s, row.Id, S.ActivatedState, activationId, unsupported, ct);
        return activationId;
    }

    /// <summary>
    /// Makes the definition Draft without an activation and deletes its activation, unless a system job that has not ended
    /// still refers to it (that job keeps its activation, ruling W4).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 when the definition does not exist; 403 without Write on it or the activation
    /// rights (A17); 400 when it is not a definition (type 1).</exception>
    public async Task DeactivateAsync(DataSession s, Caller caller, Guid definitionId, CancellationToken ct)
    {
        var row = await DefinitionAsync(s, caller, definitionId, ct);
        await store.SetStateAsync(s, row.Id, S.DraftState, null, row.UnsupportedSteps, ct);
        if (row.ActiveWorkflowId is { } activation && !await HasPendingJobAsync(s, activation, ct))
            await store.DeleteAsync(s, activation, ct);
    }

    /// <summary>
    /// After an Assign of <paramref name="workflowId"/>, in its transaction: when it is a definition with a current activation, the
    /// activation gets the definition's owner (the activation is a copy of the definition, W4; a kept activation of a job that
    /// has not ended is left as it is, the job keeps its owner).
    /// </summary>
    public async Task OnAssignedAsync(DataSession s, Guid workflowId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (await store.GetAsync(s, workflowId, lockRow: true, ct) is not { Type: (int)S.Type.Definition, ActiveWorkflowId: { } current } row
            || row.OwnerId is not { } owner) return;
        if (await store.GetAsync(s, current, ct) is { } active
            && (active.OwnerId?.Id != owner.Id || active.OwnerId?.LogicalName != owner.LogicalName))
            await store.AssignAsync(s, current, owner, ct);
    }

    private async Task<WorkflowRow> DefinitionAsync(DataSession s, Caller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        var row = await store.GetAsync(s, id, lockRow: true, ct) ?? throw XrmdFaultException.ObjectDoesNotExist(S.Table, id);
        await guard.DemandAccessAsync(caller, new EntityReference(S.Table, id), AccessRights.WriteAccess, s, ct);
        if (row.Type != (int)S.Type.Definition)
            throw XrmdFaultException.InvalidArgument(
                $"The process {id} is not a definition (type {row.Type}): only a definition (type 1) is activated or deactivated.");
        await DemandActivationRightsAsync(s, caller, row, ct);
        return row;
    }

    /// <summary>
    /// Plan 2.21, ruling A17: activating or deactivating <paramref name="definition"/> (a SetState from any surface) demands, besides
    /// Write on it, that the caller be its owner or hold <c>prvActOnBehalfOfAnotherUser</c> (Learn), and
    /// <c>prvActivateSynchronousWorkflow</c> for a real-time workflow (mode 1) or an action, <c>prvActivateBusinessRule</c> for a
    /// business rule; a background workflow needs neither. A definition owned by a team is not the caller's own. A System
    /// Administrator holds every privilege by its template; <see cref="Caller.Internal"/> (the solution import) and SYSTEM are exempt.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied naming the missing privilege.</exception>
    public async Task DemandActivationRightsAsync(DataSession s, Caller caller, WorkflowRow definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(definition);
        if (caller == Caller.Internal) return;
        if (definition.OwnerId is not { LogicalName: "systemuser" } owner || owner.Id != caller.UserId)
            await guard.DemandPrivilegeAsync(caller, PrivilegeNames.ActOnBehalfOfAnotherUser, S.Table, ct);
        var privilege = definition.Category switch
        {
            (int)S.Category.Workflow when definition.Mode == (int)S.Mode.Realtime => PrivilegeNames.ActivateSynchronousWorkflow,
            (int)S.Category.Action => PrivilegeNames.ActivateSynchronousWorkflow,
            (int)S.Category.BusinessRule => PrivilegeNames.ActivateBusinessRule,
            _ => null,
        };
        if (privilege is not null) await guard.DemandPrivilegeAsync(caller, privilege, S.Table, ct);
    }

    /// <summary>The definition's step tree; 400 for a category xrmverse does not run (W2), no XAML or malformed XAML.</summary>
    private static WorkflowDefinition Parse(WorkflowRow row)
    {
        if (row.Category is not ((int)S.Category.Workflow or (int)S.Category.BusinessRule or (int)S.Category.Action))
            throw CannotActivate(row, $"processes of category {row.Category} are not run by xrmverse.");
        if (string.IsNullOrWhiteSpace(row.Xaml)) throw CannotActivate(row, "it has no XAML.");
        try
        {
            return XamlWorkflowParser.Parse(row.Xaml, row.Category);
        }
        catch (XamlWorkflowException ex)
        {
            throw CannotActivate(row, ex.Message);
        }
    }

    /// <summary>
    /// Plan 2.12b, ruling R11 (Learn: a workflow wait condition can't use a rollup column): a wait whose condition or timeout reads a
    /// rollup column or one of its companions (<c>_date</c>, <c>_state</c>, the <c>_base</c> of a Currency rollup) is 400
    /// <c>0x80060557</c>, naming the step and the column. The column is looked up on the table of the record it is read from: the
    /// primary entity, the target of a lookup of it, or the table of an earlier create step. The same code refuses an update trigger
    /// listing such a column (Learn: a rollup column cannot be a workflow event; its changes are the system's).
    /// </summary>
    private void DemandNoRollupInWaits(WorkflowDefinition definition, WorkflowRow row)
    {
        // Learn: a rollup column cannot be used as a workflow event either. The update trigger's columns are of the primary table.
        if (row.TriggerOnUpdateAttributes is { } triggers && row.PrimaryEntity is { Length: > 0 } primaryTable
            && metadata.GetEntity(primaryTable) is { } primaryEntity)
        {
            var primaryColumns = Columns(primaryEntity);
            foreach (var name in triggers.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0))
                if (primaryColumns.GetValueOrDefault(name) is { } column && RollupColumns.IsRollupValue(column, primaryColumns.GetValueOrDefault))
                    throw new XrmdFaultException(ErrorCodes.RollupOrCalcNotAllowedInWorkflowWaitCondition,
                        $"The process '{row.Name ?? row.Id.ToString()}' cannot be activated: its update trigger lists the rollup column " +
                        $"'{primaryEntity.LogicalName}.{column.LogicalName}', whose changes start no process.", 400);
        }
        foreach (var (step, record, attribute) in WorkflowValidator.WaitColumns(definition))
        {
            foreach (var table in TablesOf(definition, row, record))
            {
                if (metadata.GetEntity(table) is not { } entity) continue;
                var columns = Columns(entity);
                if (columns.GetValueOrDefault(attribute) is { } column && RollupColumns.IsRollupValue(column, columns.GetValueOrDefault))
                    throw new XrmdFaultException(ErrorCodes.RollupOrCalcNotAllowedInWorkflowWaitCondition,
                        $"The process '{row.Name ?? row.Id.ToString()}' cannot be activated: the wait {step} reads the rollup column " +
                        $"'{entity.LogicalName}.{column.LogicalName}', which a wait condition cannot use.", 400);
            }
        }
    }

    private static Dictionary<string, AttributeMetadata> Columns(EntityMetadata entity) =>
        (entity.Attributes ?? []).Where(a => a.LogicalName is not null).ToDictionary(a => a.LogicalName, StringComparer.OrdinalIgnoreCase);

    /// <summary>The tables <paramref name="record"/> can be a row of in <paramref name="definition"/>.</summary>
    private IEnumerable<string> TablesOf(WorkflowDefinition definition, WorkflowRow row, RecordRef record)
    {
        switch (record)
        {
            case RecordRef.Primary when row.PrimaryEntity is { Length: > 0 } primary:
                return [primary];
            case RecordRef.Related related when row.PrimaryEntity is { Length: > 0 } primary
                && metadata.GetEntity(primary)?.Attributes?.FirstOrDefault(a => a.LogicalName == related.LookupAttribute) is LookupAttributeMetadata lookup:
                return lookup.Targets ?? [];
            case RecordRef.Created created:
                return definition.Root.DepthFirst().OfType<CreateStep>().Where(c => c.Variable == created.Variable).Select(c => c.Entity).ToList();
            default:
                return [];
        }
    }

    private static XrmdFaultException CannotActivate(WorkflowRow row, string reason) =>
        XrmdFaultException.InvalidArgument($"The process '{row.Name ?? row.Id.ToString()}' cannot be activated: {reason}");

    /// <summary>True when a system job that has not ended (state other than 3, Completed) runs this activation.</summary>
    private static async Task<bool> HasPendingJobAsync(DataSession s, Guid activationId, CancellationToken ct)
    {
        // The EXISTS reads the jobs with an update lock. On SQL Server (UPDLOCK, HOLDLOCK) it also holds the key range, so a job of
        // this activation inserted later waits for this transaction. On PostgreSQL the subquery's FOR UPDATE locks only the jobs it
        // finds (a found job cannot end meanwhile); it does not lock a missing one, so a job inserted concurrently is not seen and
        // does not wait.
        await using var cmd = s.Command(
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {s.DataTable("asyncoperation")}{s.Dialect.LockHint(RowLock.UpdateRange)} " +
            $"WHERE {s.Quote(S.WorkflowActivationId)} = @id AND statecode <> 3{s.Dialect.LockClause(RowLock.UpdateRange)}) THEN 1 ELSE 0 END");
        cmd.AddParameter("@id", DbType.Guid, activationId);
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }
}
