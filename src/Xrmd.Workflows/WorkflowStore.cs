using System.Data;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows;

/// <summary>
/// Reads and writes <c>workflow</c> rows and their <c>processtrigger</c> rows as <see cref="Caller.Internal"/>, through the core
/// operations (no plug-in step, no security check, none of the rules of a public write: plan 2.14's W3, which plan 2.21's A16
/// replaced with validation for requests). Used by the
/// solution import, the activation and the tests. A write invalidates the <see cref="WorkflowRegistry"/> after the commit of the
/// session's transaction (at once when there is none).
/// </summary>
public sealed class WorkflowStore(IPipelineCore core, WorkflowRegistry registry)
{
    private const string StateCode = "statecode", StatusCode = "statuscode", OwnerId = "ownerid", SystemUser = "systemuser";

    /// <summary>
    /// Creates the row, or updates every column of it but the owner when it exists (an Assign changes the owner; a new row is
    /// <see cref="WorkflowRow.OwnerId"/>'s, or the SYSTEM user's without one), and makes <paramref name="processTriggers"/> the triggers of the process:
    /// each is created or updated by its id with <c>processid</c> set to the row, and the process's other triggers are deleted.
    /// </summary>
    /// <exception cref="ArgumentException">The row has no id, or a trigger is not a <c>processtrigger</c>.</exception>
    /// <exception cref="XrmdFaultException">The core operation's fault (e.g. 400 for an invalid option value).</exception>
    public async Task SaveAsync(DataSession s, WorkflowRow row, IReadOnlyList<Entity> processTriggers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(processTriggers);
        if (row.Id == Guid.Empty) throw new ArgumentException("The workflow row needs an id.", nameof(row));
        if (processTriggers.FirstOrDefault(t => t.LogicalName != S.TriggerTable) is { } wrong)
            throw new ArgumentException($"'{wrong.LogicalName}' is not a {S.TriggerTable}.", nameof(processTriggers));

        var entity = ToEntity(row);
        if (await ExistsAsync(s, S.Table, S.WorkflowId, row.Id, ct))
        {
            if (await GetAsync(s, row.Id, ct) is { } existing) DemandUnchangedAction(existing, row);
            await ExecuteAsync(s, new UpdateRequest { Target = entity }, ct);
        }
        else
        {
            entity[OwnerId] = row.OwnerId ?? new EntityReference(SystemUser, await core.SystemUserIdAsync(s, ct));
            await ExecuteAsync(s, new CreateRequest { Target = entity }, ct);
        }

        var kept = new HashSet<Guid>();
        foreach (var given in processTriggers)
        {
            var trigger = new Entity(S.TriggerTable, given.Id == Guid.Empty ? Guid.NewGuid() : given.Id);
            foreach (var (name, value) in given.Attributes)
                if (name != S.ProcessTriggerId) trigger[name] = value;
            trigger[S.ProcessId] = new EntityReference(S.Table, row.Id);
            kept.Add(trigger.Id);
            if (await ExistsAsync(s, S.TriggerTable, S.ProcessTriggerId, trigger.Id, ct))
                await ExecuteAsync(s, new UpdateRequest { Target = trigger }, ct);
            else
                await ExecuteAsync(s, new CreateRequest { Target = trigger }, ct);
        }
        foreach (var stale in (await TriggerIdsAsync(s, row.Id, ct)).Where(id => !kept.Contains(id)))
            await ExecuteAsync(s, new DeleteRequest { Target = new EntityReference(S.TriggerTable, stale) }, ct);
        InvalidateRegistry(s);
    }

    /// <summary>
    /// Learn: the category of a process and the table of an action cannot change after save. xrmverse refuses a change of
    /// <c>category</c> to or from an action (3), whose message would be left behind, and a change of an action's
    /// <c>primaryentity</c>, whose filter would stay on the former table (plan 2.14, W14).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static void DemandUnchangedAction(WorkflowRow existing, WorkflowRow row)
    {
        var name = row.Name ?? existing.Name ?? row.Id.ToString();
        const int action = (int)S.Category.Action;
        if (existing.Category != row.Category && (existing.Category == action || row.Category == action))
            throw XrmdFaultException.InvalidArgument(
                $"The process '{name}' cannot change its category from {existing.Category} to {row.Category}: the category of a process " +
                "cannot change after it is saved.");
        static string Table(string? entity) => string.IsNullOrEmpty(entity) ? S.NoPrimaryEntity : entity.ToLowerInvariant();
        if (row.Category == action && Table(existing.PrimaryEntity) != Table(row.PrimaryEntity))
            throw XrmdFaultException.InvalidArgument(
                $"The action '{name}' cannot change its table from '{Table(existing.PrimaryEntity)}' to '{Table(row.PrimaryEntity)}': " +
                "the table of an action cannot change after it is saved.");
    }

    /// <summary>
    /// The row, with <see cref="WorkflowRow.EntityScopeRule"/> set (W15 as amended by plan 2.14 Task 3b: its <c>processtriggerscope</c> is 2,
    /// or a trigger of it, an activation's: its definition's, has <c>scope</c> 2); null when it does not exist.
    /// </summary>
    public Task<WorkflowRow?> GetAsync(DataSession s, Guid id, CancellationToken ct) => GetAsync(s, id, lockRow: false, ct);

    /// <summary>
    /// <see cref="GetAsync(DataSession, Guid, CancellationToken)"/>; with <paramref name="lockRow"/>, the row is first update-locked
    /// until the end of the transaction, so the read sees the last committed state and concurrent activations of one definition
    /// queue up (ruling W4).
    /// </summary>
    internal async Task<WorkflowRow?> GetAsync(DataSession s, Guid id, bool lockRow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (lockRow && !await ExistsAsync(s, S.Table, S.WorkflowId, id, ct)) return null;
        if (await core.RetrieveImageAsync(new EntityReference(S.Table, id), null, s, ct) is not { } entity) return null;
        var row = FromEntity(entity);
        return row with
        {
            EntityScopeRule = row.ProcessTriggerScope == S.EntityTriggerScope
                || await HasEntityScopeTriggerAsync(s, row.ParentWorkflowId ?? row.Id, ct),
        };
    }

    /// <summary>
    /// Sets the state of the row (with the state's only status), its activation and its unsupported steps, as one Update
    /// (the activation and deactivation, ruling W4).
    /// </summary>
    internal async Task SetStateAsync(DataSession s, Guid id, int state, Guid? activeWorkflowId, string? unsupportedSteps,
        CancellationToken ct)
    {
        var update = new Entity(S.Table, id)
        {
            [StateCode] = new OptionSetValue(state),
            [StatusCode] = new OptionSetValue(state == S.ActivatedState ? S.ActivatedStatus : S.DraftStatus),
            [S.ActiveWorkflowId] = activeWorkflowId is { } active ? new EntityReference(S.Table, active) : null,
            [S.UnsupportedStepsColumn] = unsupportedSteps,
        };
        await ExecuteAsync(s, new UpdateRequest { Target = update }, ct);
        InvalidateRegistry(s);
    }

    /// <summary>Gives the row to <paramref name="owner"/> (a user or a team) with a core Assign.</summary>
    internal async Task AssignAsync(DataSession s, Guid id, EntityReference owner, CancellationToken ct)
    {
        await ExecuteAsync(s, new AssignRequest { Target = new EntityReference(S.Table, id), Assignee = owner }, ct);
        InvalidateRegistry(s);
    }

    /// <summary>Deletes the row (its triggers and activations go with it, Cascade).</summary>
    internal async Task DeleteAsync(DataSession s, Guid id, CancellationToken ct)
    {
        await ExecuteAsync(s, new DeleteRequest { Target = new EntityReference(S.Table, id) }, ct);
        InvalidateRegistry(s);
    }

    private void InvalidateRegistry(DataSession s)
    {
        if (s.Transaction is null) registry.Invalidate();
        else s.OnCommitted(registry.Invalidate);
    }

    private Task<OrganizationResponse> ExecuteAsync(DataSession s, OrganizationRequest request, CancellationToken ct) =>
        core.ExecuteCoreAsync(request, Caller.Internal, s, ct);

    /// <summary>Every column of the row but the owner and the computed <see cref="WorkflowRow.EntityScopeRule"/>.</summary>
    internal static Entity ToEntity(WorkflowRow row)
    {
        static OptionSetValue? Option(int? value) => value is { } v ? new OptionSetValue(v) : null;
        static EntityReference? Workflow(Guid? id) => id is { } v ? new EntityReference(S.Table, v) : null;
        return new Entity(S.Table, row.Id)
        {
            [S.Name] = row.Name,
            [S.Description] = row.Description,
            [S.UniqueName] = row.UniqueName,
            [S.CategoryColumn] = Option(row.Category),
            [S.ModeColumn] = Option(row.Mode),
            [S.ScopeColumn] = Option(row.Scope),
            [S.TypeColumn] = Option(row.Type),
            [S.PrimaryEntity] = row.PrimaryEntity,
            [S.Xaml] = row.Xaml,
            [S.TriggerOnCreate] = row.TriggerOnCreate,
            [S.TriggerOnDelete] = row.TriggerOnDelete,
            [S.TriggerOnUpdateAttributeList] = row.TriggerOnUpdateAttributes,
            [S.CreateStage] = Option(row.CreateStage),
            [S.UpdateStage] = Option(row.UpdateStage),
            [S.DeleteStage] = Option(row.DeleteStage),
            [S.RunAsColumn] = Option(row.RunAs),
            [S.ProcessTriggerScope] = Option(row.ProcessTriggerScope),
            [S.Rank] = row.Rank,
            [S.OnDemand] = row.OnDemand,
            [S.Subprocess] = row.Subprocess,
            [S.AsyncAutoDelete] = row.AsyncAutoDelete,
            [S.SyncWorkflowLogOnFailure] = row.SyncWorkflowLogOnFailure,
            [S.IsTransacted] = row.IsTransacted,
            [S.InputParameters] = row.InputParameters,
            [S.IntroducedVersion] = row.IntroducedVersion,
            [S.IsCustomizable] = row.IsCustomizable,
            [S.ParentWorkflowId] = Workflow(row.ParentWorkflowId),
            [S.ActiveWorkflowId] = Workflow(row.ActiveWorkflowId),
            [S.SdkMessageId] = row.SdkMessageId is { } message ? new EntityReference("sdkmessage", message) : null,
            [S.UnsupportedStepsColumn] = row.UnsupportedSteps,
            [StateCode] = new OptionSetValue(row.StateCode),
            [StatusCode] = new OptionSetValue(row.StateCode == S.ActivatedState ? S.ActivatedStatus : S.DraftStatus),
        };
    }

    /// <summary>The row of a retrieved <c>workflow</c> record; an empty <c>triggeronupdateattributelist</c> reads as null (no update trigger).</summary>
    internal static WorkflowRow FromEntity(Entity e)
    {
        int? Option(string name) => e.GetAttributeValue<OptionSetValue>(name)?.Value;
        bool Flag(string name, bool fallback = false) => e.GetAttributeValue<bool?>(name) ?? fallback;
        var updateList = e.GetAttributeValue<string>(S.TriggerOnUpdateAttributeList);
        return new WorkflowRow
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(S.Name),
            Description = e.GetAttributeValue<string>(S.Description),
            UniqueName = e.GetAttributeValue<string>(S.UniqueName),
            Category = Option(S.CategoryColumn) ?? 0,
            Mode = Option(S.ModeColumn) ?? 0,
            Scope = Option(S.ScopeColumn) ?? 1,
            Type = Option(S.TypeColumn) ?? (int)S.Type.Definition,
            PrimaryEntity = e.GetAttributeValue<string>(S.PrimaryEntity),
            Xaml = e.GetAttributeValue<string>(S.Xaml),
            TriggerOnCreate = Flag(S.TriggerOnCreate),
            TriggerOnDelete = Flag(S.TriggerOnDelete),
            TriggerOnUpdateAttributes = string.IsNullOrWhiteSpace(updateList) ? null : updateList,
            CreateStage = Option(S.CreateStage),
            UpdateStage = Option(S.UpdateStage),
            DeleteStage = Option(S.DeleteStage),
            RunAs = Option(S.RunAsColumn) ?? 1,
            ProcessTriggerScope = Option(S.ProcessTriggerScope),
            Rank = e.GetAttributeValue<int?>(S.Rank) ?? 0,
            OnDemand = Flag(S.OnDemand),
            Subprocess = Flag(S.Subprocess),
            AsyncAutoDelete = Flag(S.AsyncAutoDelete),
            SyncWorkflowLogOnFailure = Flag(S.SyncWorkflowLogOnFailure),
            IsTransacted = Flag(S.IsTransacted, true),
            InputParameters = e.GetAttributeValue<string>(S.InputParameters),
            IntroducedVersion = e.GetAttributeValue<string>(S.IntroducedVersion),
            IsCustomizable = Flag(S.IsCustomizable, true),
            StateCode = Option(StateCode) ?? S.DraftState,
            OwnerId = e.GetAttributeValue<EntityReference>(OwnerId),
            OwningBusinessUnitId = e.GetAttributeValue<EntityReference>("owningbusinessunit")?.Id,
            ParentWorkflowId = e.GetAttributeValue<EntityReference>(S.ParentWorkflowId)?.Id,
            ActiveWorkflowId = e.GetAttributeValue<EntityReference>(S.ActiveWorkflowId)?.Id,
            SdkMessageId = e.GetAttributeValue<EntityReference>(S.SdkMessageId)?.Id,
            UnsupportedSteps = e.GetAttributeValue<string>(S.UnsupportedStepsColumn),
        };
    }

    private static async Task<bool> ExistsAsync(DataSession s, string table, string idColumn, Guid id, CancellationToken ct)
    {
        // On the path of every workflow write (P11: a Draft definition is edited on PostgreSQL too), so in the dialect's SQL
        // (plan 2.7a, final review I1): the key lock serializes on a row that may be missing, as the range lock does.
        await s.KeyLockAsync(table, id, ct);
        await using var cmd = s.Command(
            $"SELECT 1 FROM {s.DataTable(table)}{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE {s.Quote(idColumn)} = @id{s.Dialect.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter("@id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<List<Guid>> TriggerIdsAsync(DataSession s, Guid processId, CancellationToken ct)
    {
        await s.KeyLockAsync(S.TriggerTable, processId, ct);
        await using var cmd = s.Command(
            $"SELECT {s.Quote(S.ProcessTriggerId)} FROM {s.DataTable(S.TriggerTable)}{s.Dialect.LockHint(RowLock.UpdateRange)} " +
            $"WHERE {s.Quote(S.ProcessId)} = @id{s.Dialect.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter("@id", DbType.Guid, processId);
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return ids;
    }

    private static async Task<bool> HasEntityScopeTriggerAsync(DataSession s, Guid processId, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {s.DataTable(S.TriggerTable)} " +
            $"WHERE {s.Quote(S.ProcessId)} = @id AND {s.Quote(S.TriggerScope)} = {S.EntityTriggerScope}) THEN 1 ELSE 0 END");
        cmd.AddParameter("@id", DbType.Guid, processId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }
}
