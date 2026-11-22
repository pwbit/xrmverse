using System.Data;
using System.Data.Common;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.Solution.Layers;

namespace Xrmd.Core.Solutions;

/// <summary>The options of an uninstall (plan 2.6, rulings M9 and M10).</summary>
/// <param name="DropData">The caller confirms that the tables and columns with data may be dropped (Admin API only).</param>
/// <param name="DryRun">Return the plan without writing anything.</param>
/// <param name="FromSdk">The request came through the SDK, SOAP or Web API (Delete of the solution row, UninstallSolutionAsync):
/// any data loss is refused there, whatever <paramref name="DropData"/> says.</param>
/// <param name="ExpectedDataLoss">The data loss the caller confirms, as its dry run showed it (each table, or table and column);
/// null confirms whatever the plan drops. A loss of the plan made under the lock that is not in it is a 409 (final review M1).</param>
public sealed record UninstallOptions(bool DropData, bool DryRun, bool FromSdk, IReadOnlyCollection<DataLossKey>? ExpectedDataLoss = null);

/// <summary>
/// What an uninstall (or a promote) deletes, restores and keeps, and the data the deletes drop.
/// </summary>
/// <param name="Deleted">The components deleted (a table's columns and a relationship's lookup listed with it).</param>
/// <param name="Kept">The components that stay as they are, with the reason, and the warnings of the restores (each with the
/// component restored).</param>
/// <param name="Restored">The components restored from the layer below (by name).</param>
/// <param name="DataLoss">The tables with rows and the columns with values the deletes drop.</param>
public sealed record RemovalPlan(IReadOnlyList<CascadeDeleted> Deleted, IReadOnlyList<CascadeKept> Kept, IReadOnlyList<string> Restored,
    IReadOnlyList<DataLoss> DataLoss);

/// <summary>
/// The uninstall of a managed solution (plan 2.6, ruling M9): SDK and Web API Delete of the managed <c>solution</c> row,
/// <c>UninstallSolutionAsync</c> (system job 208) and the Admin API's <c>POST /admin/solutions/{uniquename}/uninstall</c>. In
/// one transaction:
/// <list type="number">
/// <item>refused with 0x8004F01D when another managed solution has a layer on a component this solution created (its lowest
/// Managed layer is this solution's and the component has no System layer), naming the first such component and solution;</item>
/// <item>the layers of its holding solution <c>{name}_Upgrade</c> (if any) and its own are removed;</item>
/// <item>a component left with no Managed and no System layer is deleted, its Active marker with it, through
/// <see cref="ComponentRemover"/> in the reverse import order, behind the <see cref="DataLossGuard"/> (ruling M10);</item>
/// <item>a component whose removed layer was its top non-Active layer and that has no Active marker is restored from its new
/// top layer (<see cref="LayerRestorer"/>); with an Active marker it keeps its state; a component left with only its System
/// layer is no longer managed;</item>
/// <item>the <c>solutioncomponent</c> rows and the <c>solution</c> row(s) are deleted; the publisher stays.</item>
/// </list>
/// The writes run as <see cref="Caller.Internal"/> (ruling R2: the managed-component rules do not apply to a layer operation);
/// the caller's Delete privilege on <c>solution</c> and its privileges for each delete (ruling M15) are checked first, outside
/// the transaction. The caches are published after the commit; a failure rolls everything back and changes no cache.
/// </summary>
public sealed class SolutionUninstaller(IMetadataStore store, IDbConnectionFactory factory, ComponentLayerStore layers,
    LayerRestorer restorer, ComponentRemover remover, DataLossGuard dataLoss, SecurityGuard guard, IPipelineCore core,
    AsyncOperationQueue queue, ILogger<SolutionUninstaller> logger)
{
    /// <summary>The error code of an uninstall refused because of dependencies (plan 2.6, Global Constraints).</summary>
    public const int DependencyErrorCode = unchecked((int)0x8004F01D);

    /// <summary>What a planned uninstall does (internal to one call).</summary>
    private sealed class Plan
    {
        public required Guid SolutionId { get; init; }
        public required string UniqueName { get; init; }
        public required Guid? HoldingId { get; init; }
        public required RemoverPlan Removal { get; init; }
        public List<(ComponentLayer Layer, string Name)> Restores { get; } = [];
        public List<(int Type, Guid Id)> Unmanaged { get; } = [];

        /// <summary>The components the remover keeps although no layer is left on them: their Active marker goes.</summary>
        public List<(int Type, Guid Id)> Orphans { get; } = [];
        public List<CascadeKept> Kept { get; } = [];
        public IReadOnlyList<DataLoss> DataLoss { get; set; } = [];

        public RemovalPlan Result(IEnumerable<CascadeKept>? warnings = null) => new(Removal.Deleted,
            [.. Removal.Kept, .. Kept, .. warnings ?? []], Restores.Select(r => r.Name).ToList(), DataLoss);
    }

    /// <summary>
    /// The unique name of the solution <paramref name="solutionId"/> when it is managed; null when it is unmanaged or does not
    /// exist (the container delete then answers).
    /// </summary>
    public async Task<string?> ManagedNameAsync(Guid solutionId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        // On the path of every solution delete (an unmanaged one is P11), so in the dialect's SQL (plan 2.7a, Task 6).
        var d = SqlDialects.For(conn);
        await using var cmd = conn.Command($"SELECT uniquename FROM {d.DataTable("solution")} WHERE solutionid = @id AND ismanaged = {d.BooleanLiteral(true)}");
        cmd.AddParameter("@id", DbType.Guid, solutionId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// Uninstalls the managed solution <paramref name="uniqueName"/> as <paramref name="caller"/> (see the class). With
    /// <see cref="UninstallOptions.DryRun"/>, returns the plan and writes nothing. Data loss is refused unless the options carry
    /// <see cref="UninstallOptions.DropData"/> and the call does not come from the SDK (ruling M10).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 for an unknown solution; 403 for <c>Default</c>/<c>Active</c> and a missing
    /// privilege; 400 for an unmanaged solution, for data loss (naming each table and column and the Admin command), for a
    /// restore the store cannot apply and for the first refusal of a delete; 0x8004F01D (400) when another managed solution
    /// depends on a component; 409 when the solution changed between the privilege check and the transaction.</exception>
    public async Task<RemovalPlan> UninstallAsync(Caller caller, string uniqueName, UninstallOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(uniqueName);
        ArgumentNullException.ThrowIfNull(options);
        await using var conn = await factory.OpenAsync(ct);

        // The plan and the privileges, outside the transaction (the security checks answer from the cache inside it). The
        // activity and notes refusal names row counts, so it comes only after the privileges (final review I1).
        var planned = await PlanAsync(new DataSession(conn, null), uniqueName, locked: false, ct);
        await AuthorizeAsync(new DataSession(conn, null), caller, planned, ct);
        await DemandNoActivityBlockersAsync(new DataSession(conn, null), planned, ct);
        if (options.DryRun) return planned.Result();

        await using var tx = await conn.BeginTransactionAsync(ct);
        var session = new DataSession(conn, tx);
        RemovalPlan result;
        try
        {
            // The metadata write lock before any data lock; then the plan again, with the solution rows locked.
            await store.BeginWriteAsync(session, ct);
            var plan = await PlanAsync(session, uniqueName, locked: true, ct);
            var authorized = planned.Removal.Ordered.ToHashSet();
            if (plan.Removal.Ordered.Any(c => !authorized.Contains(c)))
                throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                    $"The solution '{uniqueName}' changed while it was being uninstalled. Nothing was changed; retry the uninstall.", 409);
            await DemandNoActivityBlockersAsync(session, plan, ct);
            if (plan.DataLoss.Count > 0 && (options.FromSdk || !options.DropData))
                throw XrmdFaultException.InvalidArgument(DataLossRefusal(uniqueName, plan.DataLoss, options.FromSdk));
            DataLossGuard.DemandExpected(plan.DataLoss, options.ExpectedDataLoss, $"The uninstall of '{uniqueName}'", "uninstall");
            result = await ApplyAsync(session, plan, ct);
            // CancellationToken.None: once everything is written, a cancelled caller must not leave the commit half done.
            await tx.CommitAsync(CancellationToken.None);
        }
        catch
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // The transaction is already gone (doomed and rolled back by the server): nothing is committed.
            }
            session.RolledBack();
            throw;
        }

        // Committed: the uninstall succeeded whatever a callback does (the store published first; all callbacks ran).
        try
        {
            session.Committed();
        }
        catch (AggregateException ex)
        {
            logger.LogError(ex, "A callback failed after the uninstall of solution {Solution} was committed.", uniqueName);
        }
        return result;
    }

    /// <summary>
    /// Queues the uninstall of <paramref name="uniqueName"/> (<c>UninstallSolutionAsync</c>): plans it (the solution exists, is
    /// managed, no other managed solution depends on it) and checks that <paramref name="caller"/> holds the privileges of the
    /// plan (ruling M15), then writes the system job (operationtype 208, the unique name
    /// in <c>data</c>), which runs as the caller with the SDK's rules (<see cref="SolutionUninstallJob"/>), and wakes the worker.
    /// </summary>
    /// <returns>The <c>asyncoperationid</c> of the job.</returns>
    /// <exception cref="XrmdFaultException">404 for an unknown solution; 403 for <c>Default</c>/<c>Active</c> and a missing
    /// privilege; 400 for an unmanaged solution; 0x8004F01D (400) for a dependency (as <see cref="UninstallAsync"/>).</exception>
    public async Task<Guid> EnqueueAsync(Caller caller, string uniqueName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(uniqueName);
        Guid id;
        await using (var conn = await factory.OpenAsync(ct))
        {
            var s = new DataSession(conn, null);
            // The plan's refusals and the caller's privileges for it, before anything is queued (the job checks them again);
            // the activity and notes refusal only after the privileges (final review I1).
            var plan = await PlanAsync(s, uniqueName, locked: false, ct);
            await AuthorizeAsync(s, caller, plan, ct);
            await DemandNoActivityBlockersAsync(s, plan, ct);
            id = await queue.EnqueueJobAsync(s, caller, AsyncOperationQueue.UninstallSolution, AsyncOperationQueue.UninstallSolutionMessage, uniqueName, ct);
        }
        queue.Signal();
        return id;
    }

    /// <summary>The 400 of an uninstall that would drop data without the Admin API's confirmation (ruling M10).</summary>
    public static string DataLossRefusal(string uniqueName, IReadOnlyList<DataLoss> losses, bool fromSdk)
    {
        ArgumentNullException.ThrowIfNull(losses);
        var how = $"POST /admin/solutions/{uniqueName}/uninstall with {{\"dropData\": true}} (xrmd solution uninstall {uniqueName} --drop-data)";
        return fromSdk
            ? $"The managed solution '{uniqueName}' was not uninstalled: it would drop data: {DataLossGuard.Describe(losses)}. xrmverse drops data only when asked explicitly: uninstall it with the Admin API, {how}. Nothing was changed."
            : $"The managed solution '{uniqueName}' was not uninstalled: it would drop data: {DataLossGuard.Describe(losses)}. Confirm the loss to uninstall it: {how}. Nothing was changed.";
    }

    // ---- plan -------------------------------------------------------------------------------------------------------

    private async Task<Plan> PlanAsync(DataSession s, string uniqueName, bool locked, CancellationToken ct)
    {
        var (solutionId, managed) = await SolutionAsync(s, uniqueName, locked, ct);
        if (!managed) throw NotManaged(uniqueName);
        Guid? holdingId = null;
        var d = s.Dialect;
        if (locked) await s.KeyLockAsync("solution", uniqueName + ManagedImport.HoldingSuffix, ct);
        await using (var cmd = s.Command(
            $"SELECT solutionid FROM {s.DataTable("solution")} {(locked ? d.LockHint(RowLock.UpdateRange).TrimStart() : "")} " +
            $"WHERE {d.TextKey("uniquename")} = {d.TextKey("@name")} AND ismanaged = {d.BooleanLiteral(true)}{(locked ? d.LockClause(RowLock.UpdateRange) : "")}"))
        {
            cmd.AddParameter("@name", DbType.String, uniqueName + ManagedImport.HoldingSuffix, SolutionComponentTracker.UniqueNameMaxLength + ManagedImport.HoldingSuffix.Length);
            holdingId = await cmd.ExecuteScalarAsync(ct) as Guid?;
        }
        var removing = new HashSet<Guid> { solutionId };
        if (holdingId is { } holding) removing.Add(holding);

        var components = new SortedSet<(int Type, Guid Id)>();
        foreach (var owner in removing)
            foreach (var layer in await layers.OfSolutionAsync(s, owner, ct))
                components.Add((ComponentType.Normalize(layer.Type), layer.ObjectId));

        var meta = new ComponentIndex(store);
        var stacks = new Dictionary<(int Type, Guid Id), IReadOnlyList<ComponentLayer>>();
        foreach (var component in components)
            stacks[component] = await layers.StackAsync(s, component.Type, component.Id, ct);

        // 1. Another managed solution layered over a component this one created: refused, naming the first such component.
        foreach (var (component, stack) in stacks)
        {
            if (stack.Any(l => l.Kind == LayerKind.System)) continue;
            var managedLayers = stack.Where(l => l.Kind == LayerKind.Managed).OrderBy(l => l.Order).ToList();
            if (managedLayers.Count == 0 || !removing.Contains(managedLayers[0].SolutionId)) continue;
            if (managedLayers.FirstOrDefault(l => !removing.Contains(l.SolutionId)) is not { } other) continue;
            var otherName = await SolutionNameAsync(s, other.SolutionId, ct) ?? other.SolutionId.ToString("D");
            throw new XrmdFaultException(DependencyErrorCode,
                $"The solution '{uniqueName}' cannot be uninstalled: the managed solution '{otherName}' has a layer on its component {await remover.NameAsync(s, meta, component.Type, component.Id, ct)}. Uninstall '{otherName}' first.",
                400);
        }

        // 3. and 4. Deleted, restored or kept, per component.
        var toDelete = new List<(int, Guid)>();
        var restores = new List<(ComponentLayer, (int, Guid))>();
        var kept = new List<CascadeKept>();
        var unmanaged = new List<(int, Guid)>();
        foreach (var (component, stack) in stacks)
        {
            var remaining = stack.Where(l => !removing.Contains(l.SolutionId)).ToList();
            var below = remaining.Where(l => l.Kind != LayerKind.Active).ToList();
            if (below.Count == 0)
            {
                toDelete.Add(component);
                continue;
            }
            if (below.All(l => l.Kind != LayerKind.Managed)) unmanaged.Add(component);
            var top = stack.First(l => l.Kind != LayerKind.Active);
            var hasActive = stack.Any(l => l.Kind == LayerKind.Active);
            if (!removing.Contains(top.SolutionId))
            {
                var above = await SolutionNameAsync(s, top.SolutionId, ct) ?? top.SolutionId.ToString("D");
                kept.Add(new(component.Type, await remover.NameAsync(s, meta, component.Type, component.Id, ct),
                    $"the layer of solution {above} is above"));
            }
            else if (hasActive)
                kept.Add(new(component.Type, await remover.NameAsync(s, meta, component.Type, component.Id, ct),
                    "unmanaged customizations are above"));
            else if (below[0].Payload is null)
                kept.Add(new(component.Type, await remover.NameAsync(s, meta, component.Type, component.Id, ct),
                    "the layer below has no content to restore"));
            else restores.Add((below[0], component));
        }

        var plan = new Plan
        {
            SolutionId = solutionId, UniqueName = uniqueName, HoldingId = holdingId, Removal = await remover.PlanAsync(s, toDelete, ct),
        };
        foreach (var (layer, (type, id)) in restores)
            plan.Restores.Add((layer, await remover.NameAsync(s, meta, type, id, ct)));
        plan.Unmanaged.AddRange(unmanaged);
        // What the remover keeps (a system column of a table that remains, an intersect table, the built-in profile) is left
        // with no layer of a managed solution: no longer managed, and its Active marker has nothing left to sit on.
        plan.Unmanaged.AddRange(plan.Removal.KeptComponents);
        plan.Orphans.AddRange(plan.Removal.KeptComponents);
        plan.Kept.AddRange(kept);
        plan.DataLoss = await dataLoss.FindAsync(s, plan.Removal.Ordered, ct);
        return plan;
    }

    /// <summary>
    /// Plan 2.11 (ruling Z6): a table the uninstall deletes cannot be deleted while notes are attached to its records or
    /// activities of a remaining table are regarding them (the store's refusal; notes are not cascaded in 2.11). Checked for every
    /// plan, a dry run included, before anything is written and whatever dropData says, but only after
    /// <see cref="AuthorizeAsync"/>: the refusal names row counts, which a caller without the privileges must not learn (final
    /// review I1).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the table and what blocks it.</exception>
    private async Task DemandNoActivityBlockersAsync(DataSession s, Plan plan, CancellationToken ct)
    {
        var meta = new ComponentIndex(store);
        var droppedTables = plan.Removal.Ordered.Where(c => ComponentType.Normalize(c.Type) == ComponentType.Entity)
            .Select(c => meta.Entities.TryGetValue(c.Id, out var e) ? e.LogicalName : null).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var table in droppedTables.Order(StringComparer.Ordinal))
            if (await store.ActivityDeleteRefusalAsync(s, table, droppedTables, ct) is { } refusal)
                throw XrmdFaultException.InvalidArgument(
                    $"The managed solution '{plan.UniqueName}' cannot be uninstalled: {refusal} Nothing was changed.");
    }

    /// <summary>
    /// The solution row by unique name: its id and whether it is managed. <paramref name="locked"/>: read with UPDLOCK, HOLDLOCK
    /// (in the transaction).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 for an unknown solution; 403 for <c>Default</c> and <c>Active</c>.</exception>
    private static async Task<(Guid Id, bool Managed)> SolutionAsync(DataSession s, string uniqueName, bool locked, CancellationToken ct)
    {
        if (uniqueName.Length > SolutionComponentTracker.UniqueNameMaxLength + ManagedImport.HoldingSuffix.Length) throw Unknown(uniqueName);
        var d = s.Dialect;
        // The range of the name is locked (UPDLOCK, HOLDLOCK); on PostgreSQL a key lock of the name and FOR UPDATE.
        if (locked) await s.KeyLockAsync("solution", uniqueName, ct);
        await using var cmd = s.Command(
            $"SELECT solutionid, {d.IsNull("ismanaged", d.BooleanLiteral(false))} FROM {s.DataTable("solution")} {(locked ? d.LockHint(RowLock.UpdateRange).TrimStart() : "")} " +
            $"WHERE {d.TextKey("uniquename")} = {d.TextKey("@name")}{(locked ? d.LockClause(RowLock.UpdateRange) : "")}");
        cmd.AddParameter("@name", DbType.String, uniqueName, SolutionComponentTracker.UniqueNameMaxLength + ManagedImport.HoldingSuffix.Length);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw Unknown(uniqueName);
        var id = reader.GetGuid(0);
        if (id == SystemSeed.DefaultSolutionId || id == SystemSeed.ActiveSolutionId)
            throw new XrmdFaultException(ErrorCodes.PrivilegeDenied, SolutionDeleter.SystemSolutionFixed, 403);
        return (id, reader.GetBoolean(1));
    }

    /// <summary>The 400 for the uninstall of an unmanaged solution.</summary>
    private static XrmdFaultException NotManaged(string uniqueName) => XrmdFaultException.InvalidArgument(
        $"The solution '{uniqueName}' is unmanaged: only a managed solution is uninstalled. Delete the container with the SDK or Web API, or the solution with its components with DELETE /admin/solutions/{uniqueName}?cascade=true.");

    private static XrmdFaultException Unknown(string uniqueName) =>
        new(ErrorCodes.ObjectDoesNotExist, $"The solution with unique name '{uniqueName}' does not exist.", 404);

    /// <summary>The unique name of the solution <paramref name="solutionId"/>; null when it does not exist.</summary>
    internal static async Task<string?> SolutionNameAsync(DataSession s, Guid solutionId, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT uniquename FROM {s.DataTable("solution")} WHERE solutionid = @id");
        cmd.AddParameter("@id", DbType.Guid, solutionId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// The caller's privileges for the plan (ruling M15): Delete of the solution row, Delete access on each row component the
    /// plan deletes, and the metadata privilege of each table, column, relationship and option set it deletes.
    /// </summary>
    private async Task AuthorizeAsync(DataSession s, Caller caller, Plan plan, CancellationToken ct)
    {
        await guard.AuthorizeAsync(new DeleteRequest { Target = new EntityReference(SolutionDeleter.Table, plan.SolutionId) }, caller, s, ct);
        var demanded = new HashSet<int>();
        foreach (var (type, id) in plan.Removal.Ordered)
        {
            if (ComponentType.TableOf(type) is { } table)
                await guard.DemandAccessAsync(caller, new EntityReference(table, id), AccessRights.DeleteAccess, s, ct);
            else if (demanded.Add(type) && MetadataPrivileges.TryGetValue(type, out var privilege))
                await guard.DemandPrivilegeAsync(caller, privilege.Privilege, privilege.Kind, ct);
        }
    }

    private static readonly Dictionary<int, (string Privilege, string Kind)> MetadataPrivileges = new()
    {
        [ComponentType.EntityRelationship] = (PrivilegeNames.DeleteRelationship, "RelationshipDefinitions"),
        [ComponentType.Attribute] = (PrivilegeNames.DeleteAttribute, "Attributes"),
        [ComponentType.Entity] = (PrivilegeNames.DeleteEntity, "EntityDefinitions"),
        [ComponentType.OptionSet] = (PrivilegeNames.DeleteOptionSet, "GlobalOptionSetDefinitions"),
    };

    // ---- apply (one transaction) ------------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="plan"/> in <paramref name="s"/> (see the class), as <see cref="Caller.Internal"/>.</summary>
    private async Task<RemovalPlan> ApplyAsync(DataSession s, Plan plan, CancellationToken ct)
    {
        // 3. The deletes (their layer rows go with the components), before any metadata write: the remover names the
        // components from the published metadata.
        await remover.ExecuteAsync(s, Caller.Internal, plan.Removal.Ordered, ct);

        // 4. The restores, each on its own so that its warnings name it.
        var warnings = new List<CascadeKept>();
        foreach (var (layer, name) in plan.Restores)
            foreach (var warning in await restorer.RestoreAsync(s, [layer], ct))
                warnings.Add(new(ComponentType.Normalize(layer.Type), name, warning));

        // 2. The layers of the solution (and of its holding solution).
        if (plan.HoldingId is { } holding) await layers.RemoveSolutionAsync(s, holding, ct);
        await layers.RemoveSolutionAsync(s, plan.SolutionId, ct);
        foreach (var (type, id) in plan.Orphans) await layers.RemoveActiveAsync(s, type, id, ct);
        foreach (var (type, id) in plan.Unmanaged) await ClearManagedAsync(store, s, type, id, ct);

        // 5. The solution rows (their solutioncomponent rows go with them); the publisher stays.
        if (plan.HoldingId is { } holdingRow) await DeleteSolutionRowAsync(s, holdingRow, ct);
        await DeleteSolutionRowAsync(s, plan.SolutionId, ct);
        return plan.Result(warnings);
    }

    private Task DeleteSolutionRowAsync(DataSession s, Guid solutionId, CancellationToken ct) =>
        core.ExecuteCoreAsync(new DeleteRequest { Target = new EntityReference(SolutionDeleter.Table, solutionId) }, Caller.Internal, s, ct);

    /// <summary>
    /// A component left with no Managed layer (only its System layer, and maybe an Active marker) is no longer managed: its
    /// <c>IsManaged</c> (definition) or <c>ismanaged</c> (row) goes back to how the seed has it (not set).
    /// </summary>
    internal static async Task ClearManagedAsync(IMetadataStore store, DataSession s, int type, Guid id, CancellationToken ct)
    {
        var meta = new ComponentIndex(store);
        switch (type)
        {
            case ComponentType.Entity when meta.Entities.TryGetValue(id, out var e):
                await store.SetIsManagedAsync(s, e.LogicalName, null, null, ct);
                break;
            case ComponentType.Attribute when meta.Attributes.TryGetValue(id, out var a):
                await store.SetIsManagedAsync(s, a.Entity.LogicalName, a.Attribute.LogicalName, null, ct);
                break;
            case ComponentType.OptionSet when meta.OptionSets.TryGetValue(id, out var o) && o.Name is { } name:
                await store.SetOptionSetIsManagedAsync(s, name, null, ct);
                break;
            case ComponentType.EntityRelationship when meta.OneToMany.TryGetValue(id, out var r):
                await store.SetRelationshipIsManagedAsync(s, r.SchemaName, null, ct);
                break;
            case ComponentType.EntityRelationship when meta.ManyToMany.TryGetValue(id, out var m):
                await store.SetRelationshipIsManagedAsync(s, m.SchemaName, null, ct);
                break;
            default:
                if (ComponentType.TableOf(type) is { } table && store.GetEntity(table) is { } entity)
                {
                    await using var cmd = s.Command(
                        $"UPDATE {s.DataTable(table)} SET {s.Dialect.Quote(SystemTableRules.IsManaged)} = NULL WHERE {s.Dialect.Quote(SystemAttributes.PrimaryIdName(entity))} = @id");
                    cmd.AddParameter("@id", DbType.Guid, id);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                break;
        }
    }
}
