using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.Solution.Layers;

namespace Xrmd.Core.Solutions;

/// <summary>The options of a promote (plan 2.6, rulings M10 and M11).</summary>
/// <param name="DropData">The caller confirms that the tables and columns with data may be dropped (Admin API only).</param>
/// <param name="DryRun">Return the plan without writing anything.</param>
/// <param name="FromSdk">The request came through the SDK, SOAP or Web API (<c>DeleteAndPromote</c>, <c>StageAndUpgrade</c> and
/// their async forms): any data loss is refused there, whatever <paramref name="DropData"/> says.</param>
/// <param name="Upgrade">The promote is the second half of <c>StageAndUpgrade</c> (its refusals name that operation).</param>
/// <param name="ExpectedDataLoss">The data loss the caller confirms, as its dry run showed it; null confirms whatever the plan
/// drops. A loss of the plan made under the lock that is not in it is a 409 (final review M1).</param>
public sealed record PromoteOptions(bool DropData, bool DryRun, bool FromSdk, bool Upgrade = false,
    IReadOnlyCollection<DataLossKey>? ExpectedDataLoss = null);

/// <summary>
/// The promote of a holding solution (plan 2.6, ruling M11): <c>DeleteAndPromote</c> (system job 207 for its async forms), the
/// second half of <c>StageAndUpgrade</c> (system job 211) and the Admin API's <c>POST /admin/solutions/{uniquename}/promote</c>.
/// The solution <c>{name}</c> must have its holding solution <c>{name}_Upgrade</c>. In one transaction:
/// <list type="number">
/// <item>refused with 0x8004F01D when a component the base created (its lowest Managed layer is the base's, no System layer)
/// is not in the upgrade but another managed solution has a layer on it, naming the component and that solution;</item>
/// <item>a component left with no Managed and no System layer once the base's layers go (the upgrade dropped it) is deleted,
/// its Active marker with it, through <see cref="ComponentRemover"/>, behind the <see cref="DataLossGuard"/> (ruling M10);</item>
/// <item>a component whose top non-Active layer was the base's and that has no Active marker is restored from its new top
/// layer (<see cref="LayerRestorer"/>); a component left with only its System layer is no longer managed;</item>
/// <item>the base's layers, rank and <c>solution</c> row (with its <c>solutioncomponent</c> rows) are removed; the holding row is
/// renamed <c>{name}</c>, keeping its id (returned as <c>SolutionId</c>), and its rank becomes the base's segment with sub 0
/// (ruling M2).</item>
/// </list>
/// The writes run as <see cref="Caller.Internal"/> (ruling R2: the managed-component rules do not apply to a layer operation);
/// the caller needs <c>prvImportCustomization</c> (ruling M15). The caches are published after the commit; a failure rolls
/// everything back and changes no cache.
/// </summary>
public sealed class SolutionPromoter(IMetadataStore store, IDbConnectionFactory factory, ComponentLayerStore layers,
    LayerRestorer restorer, ComponentRemover remover, DataLossGuard dataLoss, SecurityGuard guard, IPipelineCore core,
    AsyncOperationQueue queue, SolutionImporter importer, ILogger<SolutionPromoter> logger)
{
    /// <summary>What a planned promote does (internal to one call).</summary>
    private sealed class Plan
    {
        public required Guid BaseId { get; init; }
        public required Guid HoldingId { get; init; }
        public required string UniqueName { get; init; }
        public required int Segment { get; init; }
        public required RemoverPlan Removal { get; init; }
        public List<(ComponentLayer Layer, string Name)> Restores { get; } = [];
        public List<(int Type, Guid Id)> Unmanaged { get; } = [];
        public List<(int Type, Guid Id)> Orphans { get; } = [];
        public List<CascadeKept> Kept { get; } = [];
        public IReadOnlyList<DataLoss> DataLoss { get; set; } = [];

        public RemovalPlan Result(IEnumerable<CascadeKept>? warnings = null) => new(Removal.Deleted,
            [.. Removal.Kept, .. Kept, .. warnings ?? []], Restores.Select(r => r.Name).ToList(), DataLoss);
    }

    /// <summary>
    /// Promotes the holding solution of <paramref name="uniqueName"/> in <paramref name="s"/> (see the class), as
    /// <paramref name="caller"/>, who must already hold <c>prvImportCustomization</c>. The session must be in a transaction
    /// (the caller commits it), unless <see cref="PromoteOptions.DryRun"/>, which only plans. Data loss is refused unless the
    /// options carry <see cref="PromoteOptions.DropData"/> and the call does not come from the SDK (ruling M10).
    /// </summary>
    /// <returns>The id of the promoted solution (the holding row's) and what the promote deletes, restores and keeps.</returns>
    /// <exception cref="XrmdFaultException">404 for an unknown solution; 403 for <c>Default</c>/<c>Active</c>; 400 for an
    /// unmanaged solution, a solution without a holding solution, data loss (naming each table and column and the Admin
    /// command), a restore the store cannot apply and the first refusal of a delete; 0x8004F01D (400) for a dependency.</exception>
    public Task<(Guid SolutionId, RemovalPlan Plan)> PromoteAsync(DataSession s, Caller caller, string uniqueName, PromoteOptions options,
        CancellationToken ct) =>
        PromoteAsync(s, caller, uniqueName, options, null, ct);

    /// <summary>
    /// <see cref="PromoteAsync(DataSession, Caller, string, PromoteOptions, CancellationToken)"/>; with <paramref name="planned"/>
    /// (the plan made before the transaction), the plan made under the lock must not delete more, nor promote another holding
    /// solution: otherwise a 409 and nothing changes (final review M4, as the uninstall).
    /// </summary>
    private async Task<(Guid SolutionId, RemovalPlan Plan)> PromoteAsync(DataSession s, Caller caller, string uniqueName, PromoteOptions options,
        Plan? planned, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(uniqueName);
        ArgumentNullException.ThrowIfNull(options);
        if (options.DryRun)
        {
            var dryRun = await PlanAsync(s, uniqueName, locked: false, ct);
            return (dryRun.HoldingId, dryRun.Result());
        }
        if (s.Transaction is null) throw new InvalidOperationException("A promote runs in a transaction.");
        // The metadata write lock before any data lock (a no-op when the session holds it already, as StageAndUpgrade's does).
        await store.BeginWriteAsync(s, ct);
        var plan = await PlanAsync(s, uniqueName, locked: true, ct);
        if (planned is not null)
        {
            var authorized = planned.Removal.Ordered.ToHashSet();
            if (plan.HoldingId != planned.HoldingId || plan.Removal.Ordered.Any(c => !authorized.Contains(c)))
                throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                    $"The solution '{uniqueName}' changed while its upgrade was being applied. Nothing was changed; retry the promote.", 409);
        }
        if (plan.DataLoss.Count > 0 && (options.FromSdk || !options.DropData))
            throw XrmdFaultException.InvalidArgument(DataLossRefusal(uniqueName, plan.DataLoss, options.FromSdk, options.Upgrade));
        DataLossGuard.DemandExpected(plan.DataLoss, options.ExpectedDataLoss, $"The promote of '{uniqueName}'", "promote");
        return (plan.HoldingId, await ApplyAsync(s, plan, ct));
    }

    /// <summary>
    /// <c>DeleteAndPromote</c> and the Admin API's promote: demands <c>prvImportCustomization</c>, plans outside the transaction
    /// (its refusals; <see cref="PromoteOptions.DryRun"/> returns the plan here), then promotes in a transaction of its own
    /// (<see cref="PromoteAsync"/>) and publishes the caches after the commit.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 without <c>prvImportCustomization</c>; the faults of <see cref="PromoteAsync"/>.</exception>
    public async Task<(Guid SolutionId, RemovalPlan Plan)> PromoteInOwnTransactionAsync(Caller caller, string uniqueName, PromoteOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(uniqueName);
        ArgumentNullException.ThrowIfNull(options);
        await guard.DemandPrivilegeAsync(caller, PrivilegeNames.ImportCustomization, "solution", ct);
        await using var conn = await factory.OpenAsync(ct);
        var planned = await PlanAsync(new DataSession(conn, null), uniqueName, locked: false, ct);
        if (options.DryRun) return (planned.HoldingId, planned.Result());

        await using var tx = await conn.BeginTransactionAsync(ct);
        var session = new DataSession(conn, tx);
        (Guid, RemovalPlan) result;
        try
        {
            result = await PromoteAsync(session, caller, uniqueName, options, planned, ct);
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
        try
        {
            session.Committed();
        }
        catch (AggregateException ex)
        {
            logger.LogError(ex, "A callback failed after the promote of solution {Solution} was committed.", uniqueName);
        }
        return result;
    }

    /// <summary>
    /// Queues the promote of <paramref name="uniqueName"/> (<c>DeleteAndPromoteAsync</c>, <c>ExecuteAsync</c> of
    /// <c>DeleteAndPromote</c>): demands <c>prvImportCustomization</c> and plans it with the SDK's rules (the solution and its
    /// holding solution exist, no dependency, no data loss), so that its refusals answer the request, then writes the system
    /// job (operationtype 207, the unique name in <c>data</c>), which runs as the caller (<see cref="SolutionPromoteJob"/>), and
    /// wakes the worker.
    /// </summary>
    /// <returns>The <c>asyncoperationid</c> of the job.</returns>
    /// <exception cref="XrmdFaultException">403 without <c>prvImportCustomization</c>; the refusals of <see cref="PromoteAsync"/>.</exception>
    public async Task<Guid> EnqueueAsync(Caller caller, string uniqueName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(uniqueName);
        await guard.DemandPrivilegeAsync(caller, PrivilegeNames.ImportCustomization, "solution", ct);
        Guid id;
        await using (var conn = await factory.OpenAsync(ct))
        {
            var s = new DataSession(conn, null);
            var plan = await PlanAsync(s, uniqueName, locked: false, ct);
            if (plan.DataLoss.Count > 0) throw XrmdFaultException.InvalidArgument(DataLossRefusal(uniqueName, plan.DataLoss, fromSdk: true, upgrade: false));
            id = await queue.EnqueueJobAsync(s, caller, AsyncOperationQueue.DeleteAndPromote, AsyncOperationQueue.DeleteAndPromoteMessage, uniqueName, ct);
        }
        queue.Signal();
        return id;
    }

    /// <summary>
    /// <c>StageAndUpgrade</c> (plan 2.6, ruling M11): the import of <paramref name="file"/> staged and promoted in one
    /// transaction with the SDK's rules (<see cref="SolutionImporter.StageAndUpgradeAsync"/> with <see cref="Upgrade"/>); a
    /// solution that is not installed is installed.
    /// </summary>
    /// <returns>The import's outcome (its <c>SolutionId</c> is the promoted or installed solution's).</returns>
    public Task<ImportResult> StageAndUpgradeAsync(Caller caller, byte[] file, ImportOptions options, CancellationToken ct) =>
        importer.StageAndUpgradeAsync(caller, file, options, Upgrade, ct);

    /// <summary>
    /// <c>StageAndUpgradeAsync</c> and <c>ExecuteAsync</c> of <c>StageAndUpgrade</c>: the cheap checks only, then queued as
    /// system job 211 (<see cref="SolutionImporter.EnqueueStageAndUpgradeAsync"/>; ruling R15: a data loss or a dependency
    /// fails the job, which changes nothing).
    /// </summary>
    public Task<(Guid AsyncOperationId, Guid ImportJobId)> EnqueueStageAndUpgradeAsync(Caller caller, byte[] file, ImportOptions options,
        CancellationToken ct) =>
        importer.EnqueueStageAndUpgradeAsync(caller, file, options, ct);

    /// <summary>The second half of <c>StageAndUpgrade</c>, in the import's transaction, with the SDK's rules.</summary>
    public async Task<Guid> Upgrade(DataSession s, Caller caller, string uniqueName, CancellationToken ct) =>
        (await PromoteAsync(s, caller, uniqueName, new PromoteOptions(DropData: false, DryRun: false, FromSdk: true, Upgrade: true), ct)).SolutionId;

    /// <summary>
    /// The 400 of a promote that would drop data without the Admin API's confirmation (ruling M10). <paramref name="upgrade"/>:
    /// the promote of a <c>StageAndUpgrade</c>, whose way out is to stage the file and promote it with the confirmation.
    /// </summary>
    public static string DataLossRefusal(string uniqueName, IReadOnlyList<DataLoss> losses, bool fromSdk, bool upgrade)
    {
        ArgumentNullException.ThrowIfNull(losses);
        var promote = $"POST /admin/solutions/{uniqueName}/promote with {{\"dropData\": true}} (xrmd solution promote {uniqueName} --drop-data)";
        var what = upgrade ? "StageAndUpgrade" : "DeleteAndPromote";
        if (!fromSdk)
            return $"The pending upgrade of the managed solution '{uniqueName}' was not applied: it would drop data: {DataLossGuard.Describe(losses)}. Confirm the loss to apply it: {promote}. Nothing was changed.";
        var how = upgrade
            ? $"stage the file instead (xrmd solution import --mode stage), then confirm the loss with the Admin API: {promote}"
            : $"confirm the loss with the Admin API: {promote}";
        return $"{what} of the managed solution '{uniqueName}' was refused: it would drop data: {DataLossGuard.Describe(losses)}. xrmverse drops data only when asked explicitly: {how}. Nothing was changed.";
    }

    // ---- plan -------------------------------------------------------------------------------------------------------

    private async Task<Plan> PlanAsync(DataSession s, string uniqueName, bool locked, CancellationToken ct)
    {
        var d = s.Dialect;
        // The range of each name is locked (UPDLOCK, HOLDLOCK); on PostgreSQL a key lock of each name and FOR UPDATE.
        var lockHint = locked ? d.LockHint(RowLock.UpdateRange).TrimStart() : "";
        var lockClause = locked ? d.LockClause(RowLock.UpdateRange) : "";
        if (uniqueName.Length > SolutionComponentTracker.UniqueNameMaxLength) throw Unknown(uniqueName);
        if (locked)
        {
            await s.KeyLockAsync("solution", uniqueName, ct);
            await s.KeyLockAsync("solution", uniqueName + ManagedImport.HoldingSuffix, ct);
        }
        Guid baseId;
        await using (var cmd = s.Command(
                         $"SELECT solutionid, {d.IsNull("ismanaged", d.BooleanLiteral(false))} FROM {s.DataTable("solution")} {lockHint} WHERE {d.TextKey("uniquename")} = {d.TextKey("@name")}{lockClause}"))
        {
            cmd.AddParameter("@name", DbType.String, uniqueName, SolutionComponentTracker.UniqueNameMaxLength);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw Unknown(uniqueName);
            baseId = reader.GetGuid(0);
            if (baseId == SystemSeed.DefaultSolutionId || baseId == SystemSeed.ActiveSolutionId)
                throw new XrmdFaultException(ErrorCodes.PrivilegeDenied, SolutionDeleter.SystemSolutionFixed, 403);
            if (!reader.GetBoolean(1))
                throw XrmdFaultException.InvalidArgument($"The solution '{uniqueName}' is unmanaged: only a managed solution has an upgrade to apply.");
        }
        Guid? holdingId;
        await using (var cmd = s.Command(
                         $"SELECT solutionid FROM {s.DataTable("solution")} {lockHint} WHERE {d.TextKey("uniquename")} = {d.TextKey("@name")} AND ismanaged = {d.BooleanLiteral(true)}{lockClause}"))
        {
            cmd.AddParameter("@name", DbType.String, uniqueName + ManagedImport.HoldingSuffix, SolutionComponentTracker.UniqueNameMaxLength + ManagedImport.HoldingSuffix.Length);
            holdingId = await cmd.ExecuteScalarAsync(ct) as Guid?;
        }
        if (holdingId is not { } holding)
            throw XrmdFaultException.InvalidArgument(
                $"The solution '{uniqueName}' has no pending upgrade '{uniqueName}{ManagedImport.HoldingSuffix}' to apply: import the new version with HoldingSolution first, or use StageAndUpgrade.");
        var order = await layers.RankOrderAsync(s, holding, ct) ?? await layers.RankOrderAsync(s, baseId, ct)
            ?? throw new InvalidOperationException($"The managed solution '{uniqueName}' has no rank.");
        var segment = checked((int)(order / ComponentLayerStore.SubsPerSegment));

        var components = new SortedSet<(int Type, Guid Id)>();
        foreach (var layer in await layers.OfSolutionAsync(s, baseId, ct))
            components.Add((ComponentType.Normalize(layer.Type), layer.ObjectId));
        var meta = new ComponentIndex(store);
        var stacks = new Dictionary<(int Type, Guid Id), IReadOnlyList<ComponentLayer>>();
        foreach (var component in components)
            stacks[component] = await layers.StackAsync(s, component.Type, component.Id, ct);

        // 1. A component the base created, that the upgrade drops but another managed solution layers: refused.
        foreach (var (component, stack) in stacks)
        {
            if (stack.Any(l => l.Kind == LayerKind.System || l.SolutionId == holding)) continue;
            var managedLayers = stack.Where(l => l.Kind == LayerKind.Managed).OrderBy(l => l.Order).ToList();
            if (managedLayers.Count == 0 || managedLayers[0].SolutionId != baseId) continue;
            if (managedLayers.FirstOrDefault(l => l.SolutionId != baseId) is not { } other) continue;
            var otherName = await SolutionUninstaller.SolutionNameAsync(s, other.SolutionId, ct) ?? other.SolutionId.ToString("D");
            throw new XrmdFaultException(SolutionUninstaller.DependencyErrorCode,
                $"The upgrade of the solution '{uniqueName}' cannot be applied: its new version drops the component {await remover.NameAsync(s, meta, component.Type, component.Id, ct)}, on which the managed solution '{otherName}' has a layer. Uninstall '{otherName}' first.",
                400);
        }

        // 2. and 3. Deleted, restored or kept, per component.
        var toDelete = new List<(int, Guid)>();
        var restores = new List<(ComponentLayer, (int, Guid))>();
        var kept = new List<CascadeKept>();
        var unmanaged = new List<(int, Guid)>();
        foreach (var (component, stack) in stacks)
        {
            var below = stack.Where(l => l.SolutionId != baseId && l.Kind != LayerKind.Active).ToList();
            if (below.Count == 0)
            {
                toDelete.Add(component);
                continue;
            }
            if (below.All(l => l.Kind != LayerKind.Managed)) unmanaged.Add(component);
            var top = stack.First(l => l.Kind != LayerKind.Active);
            if (top.SolutionId != baseId) continue; // the upgrade (or a solution above) is on top: nothing changes
            if (stack.Any(l => l.Kind == LayerKind.Active))
                kept.Add(new(component.Type, await remover.NameAsync(s, meta, component.Type, component.Id, ct), "unmanaged customizations are above"));
            else if (below[0].Payload is null)
                kept.Add(new(component.Type, await remover.NameAsync(s, meta, component.Type, component.Id, ct), "the layer below has no content to restore"));
            else restores.Add((below[0], component));
        }

        var plan = new Plan
        {
            BaseId = baseId, HoldingId = holding, UniqueName = uniqueName, Segment = segment, Removal = await remover.PlanAsync(s, toDelete, ct),
        };
        foreach (var (layer, (type, id)) in restores)
            plan.Restores.Add((layer, await remover.NameAsync(s, meta, type, id, ct)));
        plan.Unmanaged.AddRange(unmanaged);
        plan.Unmanaged.AddRange(plan.Removal.KeptComponents);
        plan.Orphans.AddRange(plan.Removal.KeptComponents);
        plan.Kept.AddRange(kept);
        plan.DataLoss = await dataLoss.FindAsync(s, plan.Removal.Ordered, ct);
        return plan;
    }

    private static XrmdFaultException Unknown(string uniqueName) =>
        new(ErrorCodes.ObjectDoesNotExist, $"The solution with unique name '{uniqueName}' does not exist.", 404);

    // ---- apply (in the caller's transaction) ------------------------------------------------------------------------

    private async Task<RemovalPlan> ApplyAsync(DataSession s, Plan plan, CancellationToken ct)
    {
        // The deletes first (their layer rows go with the components): the remover names them from the published metadata.
        await remover.ExecuteAsync(s, Caller.Internal, plan.Removal.Ordered, ct);

        var warnings = new List<CascadeKept>();
        foreach (var (layer, name) in plan.Restores)
            foreach (var warning in await restorer.RestoreAsync(s, [layer], ct))
                warnings.Add(new(ComponentType.Normalize(layer.Type), name, warning));

        // The base's layers and rank, then the components left without a managed layer.
        await layers.RemoveSolutionAsync(s, plan.BaseId, ct);
        foreach (var (type, id) in plan.Orphans) await layers.RemoveActiveAsync(s, type, id, ct);
        foreach (var (type, id) in plan.Unmanaged) await SolutionUninstaller.ClearManagedAsync(store, s, type, id, ct);

        // The base row goes (its solutioncomponent rows with it); the holding row takes its name and its place (sub 0).
        var call = new CoreCall(Caller.Internal, s);
        await core.ExecuteCoreAsync(new Request
        {
            Name = "Delete",
            Parameters = { ["Target"] = new RecordReference { LogicalName = SolutionDeleter.Table, Id = plan.BaseId } },
        }, call, ct);
        await core.ExecuteCoreAsync(new Request
        {
            Name = "Update",
            Parameters = { ["Target"] = new Record(SolutionDeleter.Table, plan.HoldingId) { ["uniquename"] = plan.UniqueName } },
        }, call, ct);
        await layers.SetRankAsync(s, plan.HoldingId, plan.Segment, 0, ct);
        return plan.Result(warnings);
    }
}
