using System.Data;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Pipeline;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Registration;
using Xrmd.Security;
using Xrmd.Solution;

namespace Xrmd.Core.Solutions;

/// <summary>A component a solution operation deleted (or plans to delete): its <c>componenttype</c> and its name.</summary>
public sealed record CascadeDeleted(int Type, string Name);

/// <summary>A component of the solution a solution operation kept, and why.</summary>
public sealed record CascadeKept(int Type, string Name, string Reason);

/// <summary>
/// What <see cref="ComponentRemover.PlanAsync"/> decided for a set of components to delete: the deletes to run, in order
/// (<see cref="ComponentRemover.ExecuteAsync"/>), every component they delete (a table's columns and a relationship's lookup go
/// with it, without a delete of their own), and the components that cannot be deleted, with the reason.
/// </summary>
/// <param name="Ordered">The components to delete, each with a delete of its own, in the order to run them.</param>
/// <param name="Deleted">Every component deleted, as the response lists it.</param>
/// <param name="Kept">The components of the set that are not deleted.</param>
/// <param name="KeptComponents">The same components as <paramref name="Kept"/>, by type and id.</param>
public sealed record RemoverPlan(IReadOnlyList<(int Type, Guid Id)> Ordered, IReadOnlyList<CascadeDeleted> Deleted, IReadOnlyList<CascadeKept> Kept,
    IReadOnlyList<(int Type, Guid Id)> KeptComponents);

/// <summary>The published metadata, indexed by <c>MetadataId</c> (and the tables by logical name).</summary>
public sealed class ComponentIndex
{
    /// <summary>Indexes what <paramref name="store"/> has published.</summary>
    public ComponentIndex(IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        foreach (var e in store.GetAllEntities())
        {
            EntitiesByName[e.LogicalName] = e;
            if (e.MetadataId is { } entityId) Entities[entityId] = e;
            foreach (var a in e.Attributes ?? [])
                if (a.MetadataId is { } id) Attributes[id] = (e, a);
        }
        foreach (var r in store.GetOneToManyRelationships())
            if (r.MetadataId is { } id) OneToMany[id] = r;
        foreach (var r in store.GetManyToManyRelationships())
            if (r.MetadataId is { } id) ManyToMany[id] = r;
        foreach (var o in store.GetAllOptionSets())
            if (o.MetadataId is { } id) OptionSets[id] = o;
    }

    /// <summary>The tables by <c>MetadataId</c>.</summary>
    public Dictionary<Guid, EntityMetadata> Entities { get; } = [];

    /// <summary>The tables by logical name.</summary>
    public Dictionary<string, EntityMetadata> EntitiesByName { get; } = new(StringComparer.Ordinal);

    /// <summary>The columns by <c>MetadataId</c>, with their table.</summary>
    public Dictionary<Guid, (EntityMetadata Entity, AttributeMetadata Attribute)> Attributes { get; } = [];

    /// <summary>The one-to-many relationships by <c>MetadataId</c>.</summary>
    public Dictionary<Guid, OneToManyRelationshipMetadata> OneToMany { get; } = [];

    /// <summary>The many-to-many relationships by <c>MetadataId</c>.</summary>
    public Dictionary<Guid, ManyToManyRelationshipMetadata> ManyToMany { get; } = [];

    /// <summary>The global option sets by <c>MetadataId</c>.</summary>
    public Dictionary<Guid, OptionSetMetadataBase> OptionSets { get; } = [];

    /// <summary>The <c>MetadataId</c> of the table <paramref name="logicalName"/>; null when unknown.</summary>
    public Guid? TableId(string? logicalName) => logicalName is not null ? EntitiesByName.GetValueOrDefault(logicalName)?.MetadataId : null;
}

/// <summary>
/// The deletion of solution components (plan 2.6, Task 6), shared by the Admin cascade delete of an unmanaged solution, the
/// uninstall of a managed solution and the promote of a holding solution: the order of the deletes (the reverse of the §7
/// import order: rows first, then the calculated and rollup columns (plans 2.12a, 2.12b), relationships, columns, tables and global
/// option sets) and
/// their execution through the
/// Core's own operations, in the caller's transaction. Holds no state.
/// </summary>
/// <remarks>
/// Rows go through the Core's Delete (its dependent rows and the solution tracking); a custom API takes its message and that
/// message's filters with it (plan 15). The metadata goes through <see cref="MetadataOperations"/> (DeleteRelationship,
/// DeleteAttribute, DeleteEntity, which drops the table with its data, DeleteOptionSet), each with its own checks for the
/// caller given; the layer operations pass <see cref="Caller.Internal"/> (ruling R2).
/// </remarks>
/// <param name="workflows">The classic workflows' hook (plan 2.14): the activated processes are read again after a commit that
/// deleted some; none when classic workflows are not registered.</param>
/// <param name="computed">The calculated columns' translator (plan 2.12a): orders the deletes of calculated columns that read one
/// another; without it they run in id order.</param>
/// <param name="rollups">The rollup columns' translator (plan 2.12b): orders the deletes of rollup columns and the calculated columns
/// they read or that read them; without it they run in id order.</param>
public sealed class ComponentRemover(IMetadataStore store, IPipelineCore core, MetadataOperations metadata, SecurityCache securityCache,
    StepRegistry registry, CustomApiRegistry customApis, Xrmd.Pipeline.Workflows.IWorkflowPipelineHook? workflows = null,
    IComputedColumns? computed = null, IRollupColumns? rollups = null)
{
    /// <summary>The reason a system table is kept.</summary>
    public const string SystemTable = "system table";

    /// <summary>The reason the intersect table of a relationship that remains is kept.</summary>
    public const string IntersectTable = "intersect table of a relationship that remains";

    /// <summary>The reason a system column of a table that remains is kept (system columns go with their table only).</summary>
    public const string SystemColumn = "system column of a table that remains";

    /// <summary>The reason the built-in System Administrator field security profile is kept.</summary>
    public const string BuiltInProfile = "built-in profile";

    /// <summary>
    /// The row components in the reverse of the §7 import order: a custom API after the steps (those registered on its
    /// message included) and before the plug-in types (plan 15, ruling X4); a process (plan 2.14) after the steps and before the
    /// custom APIs (it is imported after them: an action call may name one). A process's activations and triggers go with it
    /// (Cascade), and the system jobs of its activations lose their link (RemoveLink). An environment variable definition (#167)
    /// between the processes and the custom APIs, as it is imported; its value goes with it (Delete Cascade). A custom control
    /// (plan 3.5 UI-1d) between the plug-in assemblies and the web resources, as it is imported; its files go with it (the core
    /// deletes them with the row). A system chart (plan 3.5 UI-1f) between the custom controls and the web resources, the reverse of
    /// its import: it may name a web resource of the solution (webresource_savedqueryvisualizations is Delete Restrict).
    /// </summary>
    public static IReadOnlyList<int> RowOrder { get; } =
    [
        ComponentType.SdkMessageProcessingStepImage, ComponentType.SdkMessageProcessingStep, ComponentType.Workflow,
        ComponentType.EnvironmentVariableDefinition, ComponentType.CustomApi,
        ComponentType.PluginType,
        ComponentType.PluginAssembly, ComponentType.CustomControl, ComponentType.SavedQueryVisualization, ComponentType.WebResource,
        ComponentType.AppModule, ComponentType.SiteMap, ComponentType.SystemForm, ComponentType.SavedQuery, ComponentType.FieldSecurityProfile,
        ComponentType.Role,
    ];

    /// <summary>
    /// Plans the deletion of <paramref name="components"/> (none of which another solution keeps: the caller decided that), on
    /// <paramref name="s"/> with the published metadata: the rows in <see cref="RowOrder"/> (a row already gone is skipped), the
    /// relationships, the columns (those of a deleted table, the lookup of a deleted relationship and a column stored by a
    /// deleted one go with them; a system column of a table that remains is kept), the tables (a table after every other deleted
    /// table with a lookup to it; a system table is kept; an intersect table goes with its relationship only) and the global
    /// option sets.
    /// </summary>
    public async Task<RemoverPlan> PlanAsync(DataSession s, IReadOnlyCollection<(int Type, Guid Id)> components, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(components);
        var meta = new ComponentIndex(store);
        var set = components.Select(c => (Type: ComponentType.Normalize(c.Type), c.Id)).Distinct().OrderBy(c => c.Id).ToList();
        var ordered = new List<(int, Guid)>();
        var deleted = new List<CascadeDeleted>();
        var kept = new List<CascadeKept>();
        var keptIds = new List<(int, Guid)>();

        foreach (var type in RowOrder)
        {
            var table = ComponentType.TableOf(type)!;
            foreach (var (_, id) in set.Where(c => c.Type == type))
            {
                if (await RowNameAsync(s, table, id, ct) is not { } name) continue;
                if (type == ComponentType.FieldSecurityProfile && id == FieldSecurityTables.SystemAdministratorProfileId)
                {
                    kept.Add(new(type, name, BuiltInProfile));
                    keptIds.Add((type, id));
                    continue;
                }
                ordered.Add((type, id));
                deleted.Add(new(type, name));
            }
        }

        var deletedTables = new HashSet<string>(StringComparer.Ordinal);
        var intersects = new List<string>();
        foreach (var (_, id) in set.Where(c => c.Type == ComponentType.Entity))
        {
            if (!meta.Entities.TryGetValue(id, out var e)) continue;
            if (MetadataStore.IsSystemEntity(e))
            {
                kept.Add(new(ComponentType.Entity, e.LogicalName, SystemTable));
                keptIds.Add((ComponentType.Entity, id));
            }
            else if (e.IsIntersect == true) intersects.Add(e.LogicalName);
            else deletedTables.Add(e.LogicalName);
        }

        var metadataDeletes = new List<(int, Guid)>();
        // The calculated and rollup columns first (plans 2.12a, 2.12b): those of the set and those of a deleted table, before any
        // relationship, column or table they read, so the store's dependents check sees only dependents outside what is removed.
        var calculated = ComputedFirst(meta, set.Where(c => c.Type == ComponentType.Attribute).Select(c => c.Id), deletedTables);
        foreach (var (id, listed) in calculated)
        {
            metadataDeletes.Add((ComponentType.Attribute, id));
            if (listed)
            {
                var (e, a) = meta.Attributes[id];
                deleted.Add(new(ComponentType.Attribute, $"{e.LogicalName}.{a.LogicalName}"));
            }
        }
        var calculatedIds = calculated.Select(c => c.Id).ToHashSet();
        var deletedLookups = new HashSet<(string Entity, string Attribute)>();
        var deletedManyToMany = new List<ManyToManyRelationshipMetadata>();
        foreach (var (_, id) in set.Where(c => c.Type == ComponentType.EntityRelationship))
        {
            string name;
            if (meta.OneToMany.TryGetValue(id, out var r))
            {
                name = r.SchemaName;
                deletedLookups.Add((r.ReferencingEntity, r.ReferencingAttribute));
            }
            else if (meta.ManyToMany.TryGetValue(id, out var m))
            {
                name = m.SchemaName;
                deletedManyToMany.Add(m);
            }
            else continue;
            metadataDeletes.Add((ComponentType.EntityRelationship, id));
            deleted.Add(new(ComponentType.EntityRelationship, name));
        }
        foreach (var intersect in intersects)
        {
            if (deletedManyToMany.Any(m => m.IntersectEntityName == intersect)) deleted.Add(new(ComponentType.Entity, intersect));
            else
            {
                kept.Add(new(ComponentType.Entity, intersect, IntersectTable));
                keptIds.Add((ComponentType.Entity, meta.EntitiesByName[intersect].MetadataId!.Value));
            }
        }

        var deletedColumns = set.Where(c => c.Type == ComponentType.Attribute).Select(c => c.Id).ToHashSet();
        foreach (var (_, id) in set.Where(c => c.Type == ComponentType.Attribute))
        {
            if (calculatedIds.Contains(id) || !meta.Attributes.TryGetValue(id, out var found)) continue;
            var (e, a) = found;
            var name = $"{e.LogicalName}.{a.LogicalName}";
            if (deletedTables.Contains(e.LogicalName) || deletedLookups.Contains((e.LogicalName, a.LogicalName))
                || OwnerDeleted(e, a, ownerId => deletedColumns.Contains(ownerId), deletedLookups))
                deleted.Add(new(ComponentType.Attribute, name));
            else if (MetadataStore.IsSystemAttribute(e, a))
            {
                kept.Add(new(ComponentType.Attribute, name, SystemColumn));
                keptIds.Add((ComponentType.Attribute, id));
            }
            else
            {
                metadataDeletes.Add((ComponentType.Attribute, id));
                deleted.Add(new(ComponentType.Attribute, name));
            }
        }

        foreach (var table in DeletionOrder(deletedTables, deletedLookups))
        {
            metadataDeletes.Add((ComponentType.Entity, meta.EntitiesByName[table].MetadataId!.Value));
            deleted.Add(new(ComponentType.Entity, table));
        }

        foreach (var (_, id) in set.Where(c => c.Type == ComponentType.OptionSet))
        {
            if (!meta.OptionSets.TryGetValue(id, out var o) || o.Name is not { } name) continue;
            metadataDeletes.Add((ComponentType.OptionSet, id));
            deleted.Add(new(ComponentType.OptionSet, name));
        }
        ordered.AddRange(metadataDeletes);
        return new RemoverPlan(ordered, deleted, kept, keptIds);
    }

    /// <summary>
    /// The calculated (<c>SourceType</c> 1), formula (3) and rollup (2) columns to delete first: the custom ones of <paramref name="columns"/>
    /// (<c>Listed</c>) and every one of a table of <paramref name="deletedTables"/> (which would go with its table, after the
    /// relationships). A column comes before every other one of them it reads (<see cref="ComputedOrder"/>: a calculated column reading
    /// a rollup, then the rollup, then the basic calculated columns it aggregates; Review Focus 5 of plan 2.12b); id order otherwise.
    /// A rollup takes its <c>_date</c>, <c>_state</c> and <c>_base</c> with it. Shared with the Admin cascade delete, which plans its
    /// own order.
    /// </summary>
    public List<(Guid Id, bool Listed)> ComputedFirst(ComponentIndex meta, IEnumerable<Guid> columns, IReadOnlySet<string> deletedTables)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(deletedTables);
        var candidates = new List<(Guid Id, bool Listed, string Entity, string Attribute)>();
        foreach (var id in columns)
            if (meta.Attributes.TryGetValue(id, out var found) && ComputedOrder.IsComputed(found.Attribute)
                && !MetadataStore.IsSystemAttribute(found.Entity, found.Attribute) && candidates.All(c => c.Id != id))
                candidates.Add((id, true, found.Entity.LogicalName, found.Attribute.LogicalName));
        foreach (var table in deletedTables.Order(StringComparer.Ordinal))
            foreach (var a in (meta.EntitiesByName[table].Attributes ?? []).Where(ComputedOrder.IsComputed).OrderBy(a => a.MetadataId))
                if (a.MetadataId is { } id && candidates.All(c => c.Id != id))
                    candidates.Add((id, false, table, a.LogicalName));
        if (candidates.Count < 2 || (computed is null && rollups is null)) return candidates.Select(c => (c.Id, c.Listed)).ToList();

        var byKey = candidates.ToDictionary(c => (c.Entity, c.Attribute));
        return ComputedOrder.Order(candidates.Select(c => (c.Entity, c.Attribute)).ToList(), store.GetEntity, Relationship, computed, rollups,
                readersLast: false)
            .Select(k => (byKey[k].Id, byKey[k].Listed))
            .ToList();
    }

    /// <summary>The one-to-many relationship <paramref name="schemaName"/> (ignoring case); null when there is none.</summary>
    private OneToManyRelationshipMetadata? Relationship(string schemaName) =>
        store.GetRelationship(schemaName) as OneToManyRelationshipMetadata
        ?? store.GetOneToManyRelationships().FirstOrDefault(r => string.Equals(r.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Deletes the components <paramref name="ordered"/> lists, in that order, as <paramref name="caller"/>, in
    /// <paramref name="s"/> (a transaction): a row component through the Core's Delete (a custom API with its message and that
    /// message's filters, after it), a table, column, relationship or global option set through <see cref="MetadataOperations"/>.
    /// The names of the metadata components are read from the published metadata: call this before any metadata write of the
    /// session. Takes the metadata write lock first when there is metadata to delete (metadata locks before data locks). The
    /// security, step and custom API caches are invalidated once <paramref name="s"/> commits.
    /// </summary>
    /// <exception cref="XrmdFaultException">The first refusal of a delete (the caller rolls everything back).</exception>
    /// <exception cref="InvalidOperationException">A metadata component the published metadata does not have.</exception>
    public async Task ExecuteAsync(DataSession s, Caller caller, IReadOnlyList<(int Type, Guid Id)> ordered, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(ordered);
        if (ordered.Count == 0) return;
        var meta = new ComponentIndex(store);
        var requests = new List<OrganizationRequest?>();
        foreach (var (rawType, id) in ordered)
            requests.Add(ComponentType.TableOf(ComponentType.Normalize(rawType)) is null ? MetadataRequest(meta, ComponentType.Normalize(rawType), id) : null);
        if (requests.Any(r => r is not null)) await store.BeginWriteAsync(s, ct);

        for (var i = 0; i < ordered.Count; i++)
        {
            var (type, id) = (ComponentType.Normalize(ordered[i].Type), ordered[i].Id);
            if (requests[i] is { } request)
            {
                await metadata.ExecuteAsync(request, caller, s, ct); // demands its privilege itself
                continue;
            }
            var table = ComponentType.TableOf(type)!;
            if (type != ComponentType.CustomApi)
            {
                await DeleteRowAsync(s, caller, table, id, ct);
                continue;
            }
            // A custom API goes with its message and that message's filters (plan 15, C11): read before the API row goes.
            var (message, filters) = await ApiMessageAsync(s, id, ct);
            await DeleteRowAsync(s, caller, table, id, ct);
            foreach (var filter in filters) await DeleteRowAsync(s, caller, "sdkmessagefilter", filter, ct);
            if (message is { } messageId) await DeleteRowAsync(s, caller, "sdkmessage", messageId, ct);
        }

        // After the store has published the metadata (it registered first): roles, plug-in registrations and processes were deleted.
        s.OnCommitted(() =>
        {
            securityCache.Invalidate();
            registry.Invalidate();
            customApis.Invalidate();
            workflows?.Invalidate();
        });
    }

    private async Task DeleteRowAsync(DataSession s, Caller caller, string table, Guid id, CancellationToken ct)
    {
        // Plan 2.14 (W4): the jobs of a removed process that have not ended are canceled by the core's delete (IRecordWriteObserver).
        await core.ExecuteCoreAsync(new DeleteRequest { Target = new EntityReference(table, id) }, caller, s, ct);
    }

    /// <summary>The delete request of a metadata component, by its name in <paramref name="meta"/>.</summary>
    /// <exception cref="InvalidOperationException">The component is not in <paramref name="meta"/> or not a metadata type.</exception>
    private static OrganizationRequest MetadataRequest(ComponentIndex meta, int type, Guid id) => type switch
    {
        ComponentType.Entity when meta.Entities.TryGetValue(id, out var e) => new DeleteEntityRequest { LogicalName = e.LogicalName },
        ComponentType.Attribute when meta.Attributes.TryGetValue(id, out var a) =>
            new DeleteAttributeRequest { EntityLogicalName = a.Entity.LogicalName, LogicalName = a.Attribute.LogicalName },
        ComponentType.EntityRelationship when meta.OneToMany.TryGetValue(id, out var r) => new DeleteRelationshipRequest { Name = r.SchemaName },
        ComponentType.EntityRelationship when meta.ManyToMany.TryGetValue(id, out var m) => new DeleteRelationshipRequest { Name = m.SchemaName },
        ComponentType.OptionSet when meta.OptionSets.TryGetValue(id, out var o) && o.Name is { } name => new DeleteOptionSetRequest { Name = name },
        _ => throw new InvalidOperationException($"The component {id} of type {type} is not a metadata component xrmverse has."),
    };

    /// <summary>The message a custom API owns and that message's filters (none when it has no message).</summary>
    private static async Task<(Guid? Message, List<Guid> Filters)> ApiMessageAsync(DataSession s, Guid apiId, CancellationToken ct)
    {
        await using var cmd = s.Command($"""
            SELECT sdkmessageid FROM {s.DataTable(CustomApiTables.Api)} WHERE customapiid = @api;
            SELECT f.sdkmessagefilterid FROM {s.DataTable("sdkmessagefilter")} AS f
                WHERE f.sdkmessageid = (SELECT sdkmessageid FROM {s.DataTable(CustomApiTables.Api)} WHERE customapiid = @api)
                ORDER BY f.sdkmessagefilterid;
            """);
        cmd.AddParameter("@api", DbType.Guid, apiId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        Guid? message = await rd.ReadAsync(ct) && !rd.IsDBNull(0) ? rd.GetGuid(0) : null;
        await rd.NextResultAsync(ct);
        var filters = new List<Guid>();
        while (await rd.ReadAsync(ct)) filters.Add(rd.GetGuid(0));
        return (message, filters);
    }

    /// <summary>The column that stores <paramref name="a"/> (money <c>_base</c>, <c>AttributeOf</c>); null for any other.</summary>
    public static string? StoredBy(AttributeMetadata a)
    {
        ArgumentNullException.ThrowIfNull(a);
        // A rollup's _date and _state have columns of their own but go with it (plan 2.12b): DeleteAttribute of the rollup drops them.
        if (RollupColumns.IsCompanion(a)) return a.AttributeOf;
        return !TypeMapping.IsStoredByOtherAttribute(a) ? null
            : a.AttributeOf ?? (a.LogicalName.EndsWith(TypeMapping.BaseColumnSuffix, StringComparison.Ordinal)
                ? a.LogicalName[..^TypeMapping.BaseColumnSuffix.Length]
                : null);
    }

    /// <summary>
    /// A column stored by another one (money <c>_base</c>, <c>AttributeOf</c>) goes with it: true when that column is deleted
    /// (<paramref name="ownerDeleted"/> says so of a custom column, or it is the lookup of a deleted relationship).
    /// </summary>
    public static bool OwnerDeleted(EntityMetadata e, AttributeMetadata a, Func<Guid, bool> ownerDeleted,
        HashSet<(string Entity, string Attribute)> deletedLookups)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(ownerDeleted);
        ArgumentNullException.ThrowIfNull(deletedLookups);
        if (StoredBy(a) is not { } owner) return false;
        if (deletedLookups.Contains((e.LogicalName, owner))) return true;
        var ownerAttribute = (e.Attributes ?? []).FirstOrDefault(x => x.LogicalName == owner);
        return ownerAttribute?.MetadataId is { } ownerId && ownerDeleted(ownerId) && !MetadataStore.IsSystemAttribute(e, ownerAttribute);
    }

    /// <summary>
    /// The deleted tables in an order where a table comes after every other deleted table with a lookup to it (lookups of
    /// deleted relationships excepted, they go first); by name otherwise, and for a cycle (which DeleteEntity then refuses).
    /// </summary>
    public List<string> DeletionOrder(IReadOnlySet<string> tables, IReadOnlySet<(string Entity, string Attribute)> deletedLookups)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(deletedLookups);
        var remaining = tables.Order(StringComparer.Ordinal).ToList();
        var order = new List<string>();
        bool Referenced(string table) => remaining.Any(other => other != table
            && (store.GetEntity(other)?.Attributes ?? []).OfType<LookupAttributeMetadata>()
                .Any(l => !deletedLookups.Contains((other, l.LogicalName)) && (l.Targets ?? []).Contains(table, StringComparer.Ordinal)));
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(t => !Referenced(t)) ?? remaining[0];
            remaining.Remove(next);
            order.Add(next);
        }
        return order;
    }

    /// <summary>The name of the row (its primary name, else its id); null when the row does not exist.</summary>
    public async Task<string?> RowNameAsync(DataSession s, string table, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var entity = store.GetEntity(table) ?? throw new InvalidOperationException($"The system table '{table}' is missing.");
        var nameColumn = entity.PrimaryNameAttribute is { Length: > 0 } primaryName ? $"CAST({s.Dialect.Quote(primaryName)} AS {s.Dialect.ColumnType(SqlColumnKind.String, 4000)})" : "NULL";
        await using var cmd = s.Command(
            $"SELECT {nameColumn}, 1 FROM {s.DataTable(entity.LogicalName)} WHERE {s.Dialect.Quote(SystemAttributes.PrimaryIdName(entity))} = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return reader.IsDBNull(0) ? id.ToString("D") : reader.GetString(0);
    }

    /// <summary>The name of any component (as a response lists it); its id when it cannot be named.</summary>
    public async Task<string> NameAsync(DataSession s, ComponentIndex meta, int type, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(meta);
        switch (ComponentType.Normalize(type))
        {
            case ComponentType.Entity when meta.Entities.TryGetValue(id, out var e): return e.LogicalName;
            case ComponentType.Attribute when meta.Attributes.TryGetValue(id, out var a): return $"{a.Entity.LogicalName}.{a.Attribute.LogicalName}";
            case ComponentType.EntityRelationship when meta.OneToMany.TryGetValue(id, out var r): return r.SchemaName;
            case ComponentType.EntityRelationship when meta.ManyToMany.TryGetValue(id, out var m): return m.SchemaName;
            case ComponentType.OptionSet when meta.OptionSets.TryGetValue(id, out var o) && o.Name is not null: return o.Name;
            case var row when ComponentType.TableOf(row) is { } table: return await RowNameAsync(s, table, id, ct) ?? id.ToString("D");
            default: return id.ToString("D");
        }
    }
}
