using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Core.Solutions;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Registration;
using Xrmd.Security;
using Xrmd.Solution;

namespace Xrmd.Admin;

/// <summary>The outcome of a committed cascade delete.</summary>
public sealed record CascadeResult(IReadOnlyList<CascadeDeleted> Deleted, IReadOnlyList<CascadeKept> Kept);

/// <summary>
/// The cascade delete of an unmanaged solution (<c>DELETE /admin/solutions/{uniquename}?cascade=true</c>, an explicit
/// xrmverse-only command; the SDK and Web API Delete of a <c>solution</c> removes the container only,
/// <see cref="SolutionDeleter"/>). The solution's components that no other solution holds (other than <c>Default</c> and
/// <c>Active</c>) are deleted, in the reverse of the import order (spec §7), then the <c>solution</c> row with its
/// <c>solutioncomponent</c> rows; the publisher stays.
/// <para>
/// Everything runs in one transaction as the authenticated caller, through the Core's own operations and their checks:
/// rows (step images, steps, plug-in types, assemblies, web resources, apps, site maps, forms, views, roles) through the
/// Core's Delete (its dependent rows and the solution tracking), the metadata through <see cref="MetadataOperations"/>
/// (DeleteRelationship, DeleteAttribute for the columns of the tables that remain, DeleteEntity for the custom tables,
/// which drops them with their data, DeleteOptionSet). The first refusal rolls everything back and is the answer. The
/// caller's privileges are checked before the transaction opens. The cascade plans with its holder rules; the deletes run
/// through the shared <see cref="ComponentRemover"/> (plan 2.6, Task 6), which the uninstall of a managed solution uses too.
/// </para>
/// <para>
/// Lock order. The rows are deleted before the metadata (DeleteEntity refuses a table with a step registered on it), so
/// the metadata write lock is taken first (<see cref="IMetadataStore.BeginWriteAsync"/>): metadata locks always come before
/// data locks in a session.
/// </para>
/// <para>
/// Kept: a component another solution holds (<c>"in solution {uniquename}"</c>; for a column, also when another solution
/// holds its table with its subcomponents), and, transitively, every component whose delete would destroy a kept one
/// (<c>"has component {name} in solution {uniquename}"</c>: a column's table, an image's step, a step's plug-in type, a
/// type's assembly, a view's or form's table, a lookup's relationship, ...): nothing another solution holds is destroyed.
/// Also kept: a system table; the intersect table of a relationship that remains; a system column of a table that remains
/// (system columns are deleted with their table only). The columns of a deleted table are deleted with it, and the lookup
/// of a deleted relationship with it: both are listed as deleted without a request of their own.
/// </para>
/// <para>
/// The plan is made before the transaction (to check the privileges first) and made again in it, once what other
/// solutions hold is read with UPDLOCK, HOLDLOCK: a component taken by another solution in between is kept.
/// </para>
/// </summary>
public sealed class SolutionCascadeDeleter(IMetadataStore store, IDbConnectionFactory factory, IPipelineCore core,
    ComponentRemover remover, SolutionComponentTracker tracker, SecurityGuard guard, SecurityCache securityCache,
    ILogger<SolutionCascadeDeleter> logger)
{
    /// <summary>What the transaction deletes, in order, with the outcome it reports.</summary>
    private sealed class Plan
    {
        public required Guid SolutionId { get; init; }
        public required string UniqueName { get; init; }
        public required IReadOnlyList<(int Type, Guid Id)> Components { get; init; }
        public required Holders Holders { get; init; }
        public List<(string Table, Guid Id)> Rows { get; } = [];

        /// <summary>The tables, columns, relationships and option sets to delete, in order (by type and <c>MetadataId</c>).</summary>
        public List<(int Type, Guid Id)> Metadata { get; } = [];
        public List<CascadeDeleted> Deleted { get; } = [];
        public List<CascadeKept> Kept { get; } = [];

        /// <summary>
        /// The rows of the custom APIs this cascade deletes (plan 15, C10): the system table rules are checked for them as
        /// <see cref="Caller.Internal"/> (which alone may delete them), then the caller's Delete access on the API row itself. Its
        /// message and filters go with it (as its parameters and properties do), so only the rules are checked for them.
        /// </summary>
        public Dictionary<(string Table, Guid Id), bool> ApiRows { get; } = [];

        /// <summary>
        /// A plug-in type or assembly whose every custom API this cascade deletes first: authorized by the Delete access alone,
        /// without the system table rules, whose C5 rule would refuse it while those APIs still exist.
        /// </summary>
        public HashSet<(string Table, Guid Id)> AccessOnly { get; } = [];
    }

    /// <summary>
    /// What the other solutions (not this one, <c>Default</c> or <c>Active</c>) hold: the first holder of each component
    /// (by solution unique name) and of each table held with its subcomponents (<c>rootcomponentbehavior</c> 0).
    /// </summary>
    private sealed record Holders(Dictionary<Guid, (int Type, string Solution)> Components, Dictionary<Guid, string> TablesWithSubcomponents)
    {
        /// <summary>Both holders' components (this one's solution first): a component held in either is held.</summary>
        public Holders Union(Holders other)
        {
            var components = new Dictionary<Guid, (int, string)>(Components);
            foreach (var (id, holder) in other.Components) components.TryAdd(id, holder);
            var tables = new Dictionary<Guid, string>(TablesWithSubcomponents);
            foreach (var (id, holder) in other.TablesWithSubcomponents) tables.TryAdd(id, holder);
            return new Holders(components, tables);
        }
    }

    /// <summary>The links between rows that make deleting one destroy another (an image goes with its step, ...).</summary>
    private sealed class RowLinks
    {
        public Dictionary<Guid, Guid> ImageStep { get; } = [];
        public Dictionary<Guid, (Guid? Type, string? Table)> Step { get; } = [];
        public Dictionary<Guid, Guid> TypeAssembly { get; } = [];
        public Dictionary<Guid, string> ViewTable { get; } = [];
        public Dictionary<Guid, string> FormTable { get; } = [];
        public Dictionary<Guid, string> ChartTable { get; } = [];

        /// <summary>The message of each step.</summary>
        public Dictionary<Guid, Guid> StepMessage { get; } = [];

        /// <summary>Each custom API: its plug-in type, bound table, message and unique name (plan 15).</summary>
        public Dictionary<Guid, (Guid? Type, string? Table, Guid? Message, string UniqueName)> Api { get; } = [];

        /// <summary>The filters of each message that a custom API owns.</summary>
        public Dictionary<Guid, List<Guid>> ApiMessageFilters { get; } = [];
    }

    /// <summary>
    /// Test seam: runs between the plan (no transaction) and the transaction, so that a test can change what other
    /// solutions hold in that window.
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeApply { get; set; }

    /// <summary>
    /// Deletes the solution <paramref name="uniqueName"/> and its components (see the class) as <paramref name="caller"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 for an unknown solution; 403 for <c>Default</c>/<c>Active</c> and for a
    /// missing privilege; 400 for a managed solution and for the first check of a delete that refuses.</exception>
    public async Task<CascadeResult> DeleteAsync(Caller caller, string uniqueName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var conn = await factory.OpenAsync(ct);
        var plan = await PlanAsync(new DataSession(conn, null), caller, uniqueName, ct);
        if (BeforeApply is { } beforeApply) await beforeApply(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        var session = new DataSession(conn, tx);
        Plan applied;
        try
        {
            applied = await ApplyAsync(session, caller, plan, ct);
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

        // Committed: the delete succeeded whatever a callback does (the store published first; all callbacks ran).
        try
        {
            session.Committed();
        }
        catch (AggregateException ex)
        {
            logger.LogError(ex, "A callback failed after the cascade delete of solution {Solution} was committed.", plan.UniqueName);
        }
        return new CascadeResult(applied.Deleted, applied.Kept);
    }

    // ---- plan -------------------------------------------------------------------------------------------------------

    /// <summary>The plan on a session without transaction, and the caller's privileges for it (before the transaction).</summary>
    private async Task<Plan> PlanAsync(DataSession s, Caller caller, string uniqueName, CancellationToken ct)
    {
        var solutionId = await tracker.SolutionIdAsync(s, uniqueName, ct);
        if (!await SolutionDeleter.EnsureDeletableAsync(s, solutionId, ct)) throw UnknownSolution(uniqueName);
        var components = await tracker.ListAsync(s, solutionId, ct);
        var plan = await ClassifyAsync(s, solutionId, uniqueName, components, await OtherHoldersAsync(s, solutionId, locked: false, ct), ct);

        // Privileges, before the transaction (the checks in it then answer from the security cache).
        var systemUserId = await securityCache.SystemUserIdAsync(ct);
        foreach (var (table, id) in plan.Rows)
        {
            var record = new EntityReference(table, id);
            if (plan.ApiRows.TryGetValue((table, id), out var demandAccess))
            {
                await SystemTableRules.EnforceAsync(new DeleteRequest { Target = record }, Caller.Internal, systemUserId, securityCache, store, s, ct);
                if (demandAccess) await guard.DemandAccessAsync(caller, record, Microsoft.Crm.Sdk.Messages.AccessRights.DeleteAccess, s, ct);
            }
            else if (plan.AccessOnly.Contains((table, id)))
                await guard.DemandAccessAsync(caller, record, Microsoft.Crm.Sdk.Messages.AccessRights.DeleteAccess, s, ct);
            else await guard.AuthorizeAsync(new DeleteRequest { Target = record }, caller, s, ct);
        }
        foreach (var privilege in plan.Metadata.Select(m => MetadataPrivileges[m.Type]).Distinct())
            await guard.DemandPrivilegeAsync(caller, privilege.Privilege, privilege.Kind, ct);
        await guard.AuthorizeAsync(new DeleteRequest { Target = new EntityReference(SolutionDeleter.Table, solutionId) }, caller, s, ct);
        return plan;
    }

    /// <summary>
    /// Sorts the components into deleted and kept. Kept first: what another solution holds, then, transitively, every
    /// component whose delete would destroy a kept one (its parents: a column's table, a lookup's relationship and back, a
    /// money column of its <c>_base</c>, a relationship's tables, an image's step, a step's plug-in type and table, a type's
    /// assembly, a view's or form's table). Then the deletes, in order.
    /// </summary>
    private async Task<Plan> ClassifyAsync(DataSession s, Guid solutionId, string uniqueName, IReadOnlyList<(int Type, Guid Id)> components,
        Holders holders, CancellationToken ct)
    {
        var plan = new Plan { SolutionId = solutionId, UniqueName = uniqueName, Components = components, Holders = holders };
        var meta = new ComponentIndex(store);
        var links = await RowLinksAsync(s, ct);
        var kept = KeptClosure(holders, meta, links);
        var reasons = new Dictionary<Guid, string>();
        foreach (var (_, id) in components)
        {
            if (!kept.TryGetValue(id, out var root) || reasons.ContainsKey(id)) continue;
            reasons[id] = root.Id == id
                ? InSolution(root.Solution)
                : $"has component {await remover.NameAsync(s, meta, root.Type, root.Id, ct)} in solution {root.Solution}";
        }
        string? KeptBecause(Guid id) => reasons.GetValueOrDefault(id);

        // Rows, in the reverse import order.
        foreach (var type in ComponentRemover.RowOrder)
        {
            var table = ComponentType.TableOf(type)!;
            foreach (var (_, id) in components.Where(c => c.Type == type))
            {
                if (await remover.RowNameAsync(s, table, id, ct) is not { } name) continue; // nothing left to delete
                if (KeptBecause(id) is { } reason) { plan.Kept.Add(new(type, name, reason)); continue; }
                if (type == ComponentType.FieldSecurityProfile && id == FieldSecurityTables.SystemAdministratorProfileId)
                {
                    plan.Kept.Add(new(type, name, ComponentRemover.BuiltInProfile));
                    continue;
                }
                if (type == ComponentType.CustomApi)
                {
                    await PlanCustomApiAsync(s, plan, links, id, name, KeptBecause, ct);
                    continue;
                }
                plan.Rows.Add((table, id));
                plan.Deleted.Add(new(type, name));
            }
        }
        MarkMainOperationsOfDeletedApis(plan, links);

        // The tables to delete (custom, not kept), needed to classify the other metadata.
        var deletedTables = new HashSet<string>(StringComparer.Ordinal);
        var keptTables = new List<CascadeKept>();
        var intersects = new List<string>();
        foreach (var (_, id) in components.Where(c => c.Type == ComponentType.Entity))
        {
            if (!meta.Entities.TryGetValue(id, out var e)) continue;
            if (KeptBecause(id) is { } reason) keptTables.Add(new(ComponentType.Entity, e.LogicalName, reason));
            else if (MetadataStore.IsSystemEntity(e)) keptTables.Add(new(ComponentType.Entity, e.LogicalName, ComponentRemover.SystemTable));
            else if (e.IsIntersect == true) intersects.Add(e.LogicalName); // goes with its many-to-many relationship only
            else deletedTables.Add(e.LogicalName);
        }

        // Calculated and rollup columns first (plans 2.12a, 2.12b), in the order they read one another, before the relationships,
        // columns and tables they read: the store's dependents check then sees only dependents outside what this cascade removes.
        var calculated = remover.ComputedFirst(meta,
            components.Where(c => c.Type == ComponentType.Attribute && KeptBecause(c.Id) is null).Select(c => c.Id), deletedTables);
        foreach (var (id, listed) in calculated)
        {
            plan.Metadata.Add((ComponentType.Attribute, id));
            if (!listed) continue;
            var (e, a) = meta.Attributes[id];
            plan.Deleted.Add(new(ComponentType.Attribute, $"{e.LogicalName}.{a.LogicalName}"));
        }
        var calculatedIds = calculated.Select(c => c.Id).ToHashSet();

        // Relationships.
        var deletedLookups = new HashSet<(string Entity, string Attribute)>();
        foreach (var (type, id) in components.Where(c => c.Type == ComponentType.EntityRelationship))
        {
            string name;
            if (meta.OneToMany.TryGetValue(id, out var r)) name = r.SchemaName;
            else if (meta.ManyToMany.TryGetValue(id, out var m)) name = m.SchemaName;
            else continue;
            if (KeptBecause(id) is { } reason) { plan.Kept.Add(new(type, name, reason)); continue; }
            if (r is not null) deletedLookups.Add((r.ReferencingEntity, r.ReferencingAttribute));
            plan.Metadata.Add((ComponentType.EntityRelationship, id));
            plan.Deleted.Add(new(type, name));
        }
        foreach (var intersect in intersects)
        {
            var deletedWith = plan.Metadata.Any(d => d.Type == ComponentType.EntityRelationship
                && meta.ManyToMany.TryGetValue(d.Id, out var m) && m.IntersectEntityName == intersect);
            if (deletedWith) plan.Deleted.Add(new(ComponentType.Entity, intersect));
            else keptTables.Add(new(ComponentType.Entity, intersect, ComponentRemover.IntersectTable));
        }

        // Columns: of a deleted table with it, of a remaining table with DeleteAttribute (custom columns only).
        foreach (var (type, id) in components.Where(c => c.Type == ComponentType.Attribute))
        {
            if (calculatedIds.Contains(id) || !meta.Attributes.TryGetValue(id, out var found)) continue;
            var (e, a) = found;
            var name = $"{e.LogicalName}.{a.LogicalName}";
            if (KeptBecause(id) is { } reason)
                plan.Kept.Add(new(type, name, reason));
            else if (deletedTables.Contains(e.LogicalName) || deletedLookups.Contains((e.LogicalName, a.LogicalName))
                     || ComponentRemover.OwnerDeleted(e, a, ownerId => components.Contains((ComponentType.Attribute, ownerId))
                         && meta.Attributes.ContainsKey(ownerId) && !reasons.ContainsKey(ownerId), deletedLookups))
                plan.Deleted.Add(new(type, name));
            else if (MetadataStore.IsSystemAttribute(e, a))
                plan.Kept.Add(new(type, name, ComponentRemover.SystemColumn));
            else
            {
                plan.Metadata.Add((ComponentType.Attribute, id));
                plan.Deleted.Add(new(type, name));
            }
        }

        // Tables: a table referenced by a lookup of another deleted table goes after it (DeleteEntity refuses otherwise).
        foreach (var table in remover.DeletionOrder(deletedTables, deletedLookups))
        {
            plan.Metadata.Add((ComponentType.Entity, meta.EntitiesByName[table].MetadataId!.Value));
            plan.Deleted.Add(new(ComponentType.Entity, table));
        }
        plan.Kept.AddRange(keptTables);

        // Global option sets.
        foreach (var (type, id) in components.Where(c => c.Type == ComponentType.OptionSet))
        {
            if (!meta.OptionSets.TryGetValue(id, out var o) || o.Name is not { } name) continue;
            if (KeptBecause(id) is { } reason) { plan.Kept.Add(new(type, name, reason)); continue; }
            plan.Metadata.Add((ComponentType.OptionSet, id));
            plan.Deleted.Add(new(type, name));
        }
        return plan;
    }

    /// <summary>
    /// A custom API the cascade deletes (plan 15, C11), in order: the steps registered on its message that the plan does not
    /// delete yet (any step that is no other solution's: their images go with them), the API (its parameters and properties go
    /// with it, Delete Cascade), its filters, its message. A step on its message that another solution holds, or that this
    /// cascade keeps, blocks the whole delete with 400 naming it: the message cannot go while a step stays on it.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the step.</exception>
    private async Task PlanCustomApiAsync(DataSession s, Plan plan, RowLinks links, Guid id, string name, Func<Guid, string?> keptBecause,
        CancellationToken ct)
    {
        var message = links.Api.GetValueOrDefault(id).Message;
        if (message is { } messageId)
            foreach (var step in links.StepMessage.Where(m => m.Value == messageId).Select(m => m.Key).Order())
            {
                if (plan.Rows.Contains(("sdkmessageprocessingstep", step))) continue;
                var stepName = await remover.RowNameAsync(s, "sdkmessageprocessingstep", step, ct) ?? step.ToString("D");
                var holder = plan.Holders.Components.TryGetValue(step, out var held) ? InSolution(held.Solution) : keptBecause(step);
                if (holder is not null)
                    throw XrmdFaultException.InvalidArgument(
                        $"The solution '{plan.UniqueName}' cannot be deleted: step '{stepName}' ({holder}) is registered on the message of its custom API '{name}'.");
                plan.Rows.Add(("sdkmessageprocessingstep", step));
                plan.Deleted.Add(new(ComponentType.SdkMessageProcessingStep, stepName));
            }

        plan.Rows.Add((CustomApiTables.Api, id));
        plan.ApiRows[(CustomApiTables.Api, id)] = true;
        plan.Deleted.Add(new(ComponentType.CustomApi, name));
        if (message is not { } apiMessage) return;
        foreach (var filter in links.ApiMessageFilters.GetValueOrDefault(apiMessage) ?? [])
        {
            plan.Rows.Add(("sdkmessagefilter", filter));
            plan.ApiRows[("sdkmessagefilter", filter)] = false;
        }
        plan.Rows.Add(("sdkmessage", apiMessage));
        plan.ApiRows[("sdkmessage", apiMessage)] = false;
    }

    /// <summary>
    /// A plug-in type the plan deletes whose every custom API the plan deletes first, and an assembly all of whose such types
    /// are in that case, are authorized without the C5 rule (which refuses the delete while an API uses the type). A type or
    /// assembly the plan deletes that an API outside the plan still runs is refused here, naming the first such API (the C5
    /// rule could name one this cascade deletes).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the API that keeps the type.</exception>
    private static void MarkMainOperationsOfDeletedApis(Plan plan, RowLinks links)
    {
        var deletedApis = plan.Rows.Where(r => r.Table == CustomApiTables.Api).Select(r => r.Id).ToHashSet();
        bool UsedByApi(Guid type) => links.Api.Values.Any(a => a.Type == type);
        foreach (var (table, id) in plan.Rows.ToList())
        {
            var types = table switch
            {
                "plugintype" => [id],
                "pluginassembly" => links.TypeAssembly.Where(t => t.Value == id).Select(t => t.Key).ToList(),
                _ => new List<Guid>(),
            };
            if (!types.Any(UsedByApi)) continue;
            var outside = links.Api.Where(a => a.Value.Type is { } t && types.Contains(t) && !deletedApis.Contains(a.Key))
                .Select(a => a.Value.UniqueName).Order(StringComparer.Ordinal).FirstOrDefault();
            if (outside is not null)
                throw XrmdFaultException.InvalidArgument(table == "plugintype"
                    ? $"The solution '{plan.UniqueName}' cannot be deleted: its plug-in type {id} is the main operation of custom API '{outside}', which this delete does not remove."
                    : $"The solution '{plan.UniqueName}' cannot be deleted: its plug-in assembly {id} holds the main operation of custom API '{outside}', which this delete does not remove.");
            plan.AccessOnly.Add((table, id));
        }
    }

    /// <summary>
    /// The objects to keep, each with the held component it is kept for (itself when held): what other solutions hold
    /// (with the columns of a table held with its subcomponents), closed over <see cref="Parents"/>.
    /// </summary>
    private static Dictionary<Guid, (int Type, Guid Id, string Solution)> KeptClosure(Holders holders, ComponentIndex meta, RowLinks links)
    {
        var kept = new Dictionary<Guid, (int Type, Guid Id, string Solution)>();
        var queue = new Queue<(int Type, Guid Id)>();
        void Keep(int type, Guid id, (int, Guid, string) root)
        {
            if (kept.TryAdd(id, root)) queue.Enqueue((type, id));
        }

        foreach (var (id, (type, solution)) in holders.Components) Keep(type, id, (type, id, solution));
        foreach (var (tableId, solution) in holders.TablesWithSubcomponents)
            foreach (var a in meta.Entities.GetValueOrDefault(tableId)?.Attributes ?? [])
                if (a.MetadataId is { } attributeId) Keep(ComponentType.Attribute, attributeId, (ComponentType.Attribute, attributeId, solution));
        while (queue.TryDequeue(out var child))
        {
            var root = kept[child.Id];
            foreach (var (type, id) in Parents(child.Type, child.Id, meta, links)) Keep(type, id, root);
        }
        return kept;
    }

    /// <summary>The objects whose delete destroys (or, for a relationship's tables, is refused because of) the object.</summary>
    private static IEnumerable<(int Type, Guid Id)> Parents(int type, Guid id, ComponentIndex meta, RowLinks links)
    {
        switch (ComponentType.Normalize(type))
        {
            case ComponentType.Attribute when meta.Attributes.TryGetValue(id, out var found):
                var (e, a) = found;
                if (e.MetadataId is { } tableId) yield return (ComponentType.Entity, tableId);
                foreach (var r in meta.OneToMany.Values.Where(r => r.ReferencingEntity == e.LogicalName && r.ReferencingAttribute == a.LogicalName))
                    yield return (ComponentType.EntityRelationship, r.MetadataId!.Value);
                if (ComponentRemover.StoredBy(a) is { } owner && (e.Attributes ?? []).FirstOrDefault(x => x.LogicalName == owner)?.MetadataId is { } ownerId)
                    yield return (ComponentType.Attribute, ownerId);
                break;
            case ComponentType.EntityRelationship when meta.OneToMany.TryGetValue(id, out var oneToMany):
                if (meta.EntitiesByName.GetValueOrDefault(oneToMany.ReferencingEntity) is { } referencing
                    && (referencing.Attributes ?? []).FirstOrDefault(x => x.LogicalName == oneToMany.ReferencingAttribute)?.MetadataId is { } lookupId)
                    yield return (ComponentType.Attribute, lookupId);
                foreach (var table in new[] { oneToMany.ReferencingEntity, oneToMany.ReferencedEntity })
                    if (meta.TableId(table) is { } tableOfRelationship) yield return (ComponentType.Entity, tableOfRelationship);
                break;
            case ComponentType.EntityRelationship when meta.ManyToMany.TryGetValue(id, out var manyToMany):
                foreach (var table in new[] { manyToMany.Entity1LogicalName, manyToMany.Entity2LogicalName })
                    if (meta.TableId(table) is { } side) yield return (ComponentType.Entity, side);
                break;
            case ComponentType.SdkMessageProcessingStepImage when links.ImageStep.TryGetValue(id, out var step):
                yield return (ComponentType.SdkMessageProcessingStep, step);
                break;
            case ComponentType.SdkMessageProcessingStep when links.Step.TryGetValue(id, out var registration):
                if (registration.Type is { } pluginType) yield return (ComponentType.PluginType, pluginType);
                if (meta.TableId(registration.Table) is { } stepTable) yield return (ComponentType.Entity, stepTable);
                break;
            case ComponentType.PluginType when links.TypeAssembly.TryGetValue(id, out var assembly):
                yield return (ComponentType.PluginAssembly, assembly);
                break;
            case ComponentType.SavedQuery when meta.TableId(links.ViewTable.GetValueOrDefault(id)) is { } viewTable:
                yield return (ComponentType.Entity, viewTable);
                break;
            case ComponentType.SystemForm when meta.TableId(links.FormTable.GetValueOrDefault(id)) is { } formTable:
                yield return (ComponentType.Entity, formTable);
                break;
            case ComponentType.SavedQueryVisualization when meta.TableId(links.ChartTable.GetValueOrDefault(id)) is { } chartTable:
                yield return (ComponentType.Entity, chartTable);
                break;
            case ComponentType.CustomApi when links.Api.TryGetValue(id, out var api):
                // Deleting its plug-in type or its bound table is refused while the API exists (plan 15, C5).
                if (api.Type is { } apiType) yield return (ComponentType.PluginType, apiType);
                if (meta.TableId(api.Table) is { } boundTable) yield return (ComponentType.Entity, boundTable);
                break;
        }
    }

    /// <summary>The links of <see cref="RowLinks"/>, read on <paramref name="s"/>.</summary>
    private async Task<RowLinks> RowLinksAsync(DataSession s, CancellationToken ct)
    {
        var links = new RowLinks();
        await using var cmd = s.Command($"""
            SELECT sdkmessageprocessingstepimageid, sdkmessageprocessingstepid FROM {s.DataTable("sdkmessageprocessingstepimage")}
                WHERE sdkmessageprocessingstepid IS NOT NULL;
            SELECT st.sdkmessageprocessingstepid,
                    COALESCE(CASE WHEN st.eventhandlertype IS NULL OR st.eventhandlertype = 4602 THEN st.eventhandler END, st.plugintypeid),
                    f.primaryobjecttypecode, st.sdkmessageid
                FROM {s.DataTable("sdkmessageprocessingstep")} AS st
                LEFT JOIN {s.DataTable("sdkmessagefilter")} AS f ON f.sdkmessagefilterid = st.sdkmessagefilterid;
            SELECT plugintypeid, pluginassemblyid FROM {s.DataTable("plugintype")} WHERE pluginassemblyid IS NOT NULL;
            SELECT savedqueryid, returnedtypecode FROM {s.DataTable("savedquery")} WHERE returnedtypecode IS NOT NULL;
            SELECT systemformid, objecttypecode FROM {s.DataTable("systemform")} WHERE objecttypecode IS NOT NULL;
            SELECT savedqueryvisualizationid, primaryentitytypecode FROM {s.DataTable("savedqueryvisualization")} WHERE primaryentitytypecode IS NOT NULL;
            SELECT customapiid, plugintypeid, boundentitylogicalname, sdkmessageid, uniquename FROM {s.DataTable(CustomApiTables.Api)};
            SELECT f.sdkmessagefilterid, f.sdkmessageid FROM {s.DataTable("sdkmessagefilter")} AS f
                WHERE f.sdkmessageid IN (SELECT sdkmessageid FROM {s.DataTable(CustomApiTables.Api)} WHERE sdkmessageid IS NOT NULL);
            """);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) links.ImageStep[rd.GetGuid(0)] = rd.GetGuid(1);
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            links.Step[rd.GetGuid(0)] = (rd.IsDBNull(1) ? null : rd.GetGuid(1), rd.IsDBNull(2) ? null : rd.GetString(2));
            if (!rd.IsDBNull(3)) links.StepMessage[rd.GetGuid(0)] = rd.GetGuid(3);
        }
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct)) links.TypeAssembly[rd.GetGuid(0)] = rd.GetGuid(1);
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct)) links.ViewTable[rd.GetGuid(0)] = rd.GetString(1);
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct)) links.FormTable[rd.GetGuid(0)] = rd.GetString(1);
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct)) links.ChartTable[rd.GetGuid(0)] = rd.GetString(1);
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct))
            links.Api[rd.GetGuid(0)] = (rd.IsDBNull(1) ? null : rd.GetGuid(1), rd.IsDBNull(2) ? null : rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetGuid(3),
                rd.IsDBNull(4) ? rd.GetGuid(0).ToString("D") : rd.GetString(4));
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            if (!links.ApiMessageFilters.TryGetValue(rd.GetGuid(1), out var filters)) links.ApiMessageFilters[rd.GetGuid(1)] = filters = [];
            filters.Add(rd.GetGuid(0));
        }
        return links;
    }

    private static readonly Dictionary<int, (string Privilege, string Kind)> MetadataPrivileges = new()
    {
        [ComponentType.EntityRelationship] = (PrivilegeNames.DeleteRelationship, "RelationshipDefinitions"),
        [ComponentType.Attribute] = (PrivilegeNames.DeleteAttribute, "Attributes"),
        [ComponentType.Entity] = (PrivilegeNames.DeleteEntity, "EntityDefinitions"),
        [ComponentType.OptionSet] = (PrivilegeNames.DeleteOptionSet, "GlobalOptionSetDefinitions"),
    };

    private static string InSolution(string uniqueName) => $"in solution {uniqueName}";

    private static XrmdFaultException UnknownSolution(string uniqueName) =>
        new(ErrorCodes.ObjectDoesNotExist, $"The solution with unique name '{uniqueName}' does not exist.", 404);

    /// <summary>
    /// What the other solutions hold (see <see cref="Holders"/>). <paramref name="locked"/>: read with UPDLOCK, HOLDLOCK
    /// (in the transaction), so that no other solution can take or add a component until the cascade ends.
    /// </summary>
    private static async Task<Holders> OtherHoldersAsync(DataSession s, Guid solutionId, bool locked, CancellationToken ct)
    {
        var d = s.Dialect;
        // PostgreSQL has no key-range half of HOLDLOCK: FOR UPDATE OF sc locks the rows read, and the table write lock keeps
        // every other writer of solutioncomponent (SolutionComponentTracker.AddAsync of another solution, another cascade)
        // waiting to the end of the transaction. Nothing is sent on SQL Server, whose range lock does that.
        if (locked) await d.LockTableWritesAsync(s, DdlGenerator.DataSchema, "solutioncomponent", ct);
        await using var cmd = s.Command($"""
            SELECT sc.objectid, sc.componenttype, {d.IsNull("sc.rootcomponentbehavior", "0")}, so.uniquename
            FROM {s.DataTable("solutioncomponent")} AS sc {(locked ? d.LockHint(RowLock.UpdateRange).TrimStart() : "")}
            JOIN {s.DataTable("solution")} AS so ON so.solutionid = sc.solutionid
            WHERE sc.solutionid NOT IN (@solution, @default, @active) AND sc.objectid IS NOT NULL AND sc.componenttype IS NOT NULL
            ORDER BY so.uniquename{(locked ? d.LockClause(RowLock.UpdateRange, "sc") : "")}
            """);
        cmd.AddParameter("@solution", DbType.Guid, solutionId);
        cmd.AddParameter("@default", DbType.Guid, SystemSeed.DefaultSolutionId);
        cmd.AddParameter("@active", DbType.Guid, SystemSeed.ActiveSolutionId);
        var components = new Dictionary<Guid, (int, string)>();
        var tables = new Dictionary<Guid, string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var (id, type, behavior, name) = (reader.GetGuid(0), ComponentType.Normalize(reader.GetInt32(1)), reader.GetInt32(2), reader.GetString(3));
            components.TryAdd(id, (type, name));
            if (type == ComponentType.Entity && behavior == 0) tables.TryAdd(id, name);
        }
        return new Holders(components, tables);
    }

    // ---- apply (one transaction) ------------------------------------------------------------------------------------

    /// <summary>
    /// Runs the plan in <paramref name="s"/>, after taking the locks and planning again with what other solutions hold
    /// now (locked to the end). A component of the solution can only move from deleted to kept, so the plan's checked
    /// privileges cover it. The one row the new plan can add is a step registered on the message of a deleted custom API
    /// since the first plan (such steps are read live, not held by the solution): its delete was not authorized, so the
    /// delete stops with 409 and the caller retries. Returns the plan run.
    /// </summary>
    /// <exception cref="XrmdFaultException">409 when a step appeared on the message of a deleted custom API since the plan.</exception>
    private async Task<Plan> ApplyAsync(DataSession s, Caller caller, Plan planned, CancellationToken ct)
    {
        // The metadata write lock before any data lock (the rows are deleted first).
        if (planned.Metadata.Count > 0) await store.BeginWriteAsync(s, ct);
        // The solution row is locked and checked again (it may have changed since the plan).
        if (!await SolutionDeleter.EnsureDeletableAsync(s, planned.SolutionId, ct)) throw UnknownSolution(planned.UniqueName);
        var holders = planned.Holders.Union(await OtherHoldersAsync(s, planned.SolutionId, locked: true, ct));
        var plan = await ClassifyAsync(s, planned.SolutionId, planned.UniqueName, planned.Components, holders, ct);
        var authorized = planned.Rows.ToHashSet();
        var grown = plan.Rows.Where(r => !authorized.Contains(r)).ToList();
        if (grown.Count > 0 && grown.All(r => r.Table == "sdkmessageprocessingstep"))
            throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                $"The solution '{plan.UniqueName}' changed while it was being deleted: a processing step was registered on the message of one of its custom APIs. Nothing was deleted; retry the delete.",
                409);
        if (grown.Count > 0)
            throw new InvalidOperationException("The cascade plan grew in its transaction; the rows it deletes were not authorized.");

        // The row components, then the metadata (each metadata delete demands its privilege itself). A custom API's message and
        // filters, which the plan lists after it, are deleted by the remover with the API.
        var ordered = plan.Rows.Where(r => ComponentType.OfTable(r.Table) is not null)
            .Select(r => (ComponentType.OfTable(r.Table)!.Value, r.Id)).Concat(plan.Metadata).ToList();
        await remover.ExecuteAsync(s, caller, ordered, ct);
        await core.ExecuteCoreAsync(new DeleteRequest { Target = new EntityReference(SolutionDeleter.Table, plan.SolutionId) }, caller, s, ct);
        return plan;
    }
}
