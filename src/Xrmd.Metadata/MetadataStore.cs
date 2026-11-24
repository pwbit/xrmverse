using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xrmd.Model.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Metadata;

/// <summary>
/// <see cref="IMetadataStore"/> over <c>meta.entity</c> / <c>meta.attribute</c> / <c>meta.version_stamp</c>.
/// <c>meta.entity.definition</c> holds the TableDefinition JSON without <c>Attributes</c>; each attribute is a
/// <c>meta.attribute</c> row. Every change bumps the version stamp, writes <c>meta.*</c> and runs the DDL in the
/// transaction of a <see cref="DataSession"/> (the version-stamp row lock also serializes writers of other processes;
/// <c>meta.*</c> is always written before <c>data.*</c>). A session's first write takes the write lock and copies the
/// cache; its changes go to the copy, which <see cref="DataSession.Committed"/> publishes in one step and
/// <see cref="DataSession.RolledBack"/> discards, both releasing the lock. Published caches are never modified.
/// The cache holds the model (<c>Xrmd.Model.Metadata</c>, read and written as <see cref="MetadataJson"/>); a read returns the cache's
/// own instance, which no one changes (a write member copies its argument and replaces the definition), so the SDK view of
/// an instance (<c>SdkMetadataView</c>) is built once and stays valid.
/// A call that would wait for the write lock held by a session on its own call path (<see cref="LoadAsync"/>, a non-session
/// overload or another session's first write, made from a handler for instance) throws <see cref="InvalidOperationException"/>
/// instead. Global option sets (<c>meta.optionset</c>) are in <c>OptionSetStore.cs</c>, relationships (<c>meta.relationship</c>)
/// in <c>RelationshipStore.cs</c>.
/// </summary>
public sealed partial class MetadataStore(IDbConnectionFactory factory, IEnumerable<IEntityCreatedHandler>? createdHandlers = null,
    IEnumerable<IEntityDeletedHandler>? deletedHandlers = null, IEnumerable<IAttributeDeletedHandler>? attributeDeletedHandlers = null,
    IEnumerable<IAttributeSecurityChangedHandler>? securityChangedHandlers = null, IComputedColumns? computed = null,
    IRollupColumns? rollups = null, IRollupJobs? rollupJobs = null, ILogger<MetadataStore>? logger = null)
    : IMetadataStore
{
    private const int FirstCustomObjectTypeCode = 10000;
    private readonly IEnumerable<IAttributeSecurityChangedHandler> _securityChangedHandlers = securityChangedHandlers ?? [];
    private readonly IComputedColumns? _computed = computed;
    private readonly IRollupColumns? _rollups = rollups;
    private readonly IRollupJobs? _rollupJobs = rollupJobs;
    private readonly ILogger<MetadataStore>? _logger = logger;

    /// <summary>The metadata cache: a published instance is read-only; a session changes a copy of its own.</summary>
    private sealed class CacheState
    {
        public Dictionary<string, TableDefinition> ByLogicalName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TableDefinition> BySetName { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, TableDefinition> ByObjectTypeCode { get; } = new();

        /// <summary>Global option sets by Name, ignoring case (names are unique ignoring case).</summary>
        public Dictionary<string, ChoiceSet> OptionSets { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Saved many-to-many relationships by SchemaName, ignoring case (names are unique ignoring case).</summary>
        public Dictionary<string, ManyToManyRelationship> ManyToMany { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Saved one-to-many relationships (CreateOneToMany: a lookup with its own SchemaName) by SchemaName, ignoring case.
        /// </summary>
        public Dictionary<string, OneToManyRelationship> SavedOneToMany { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The associated menu configuration (opaque XML) of saved relationships, by SchemaName, ignoring case.</summary>
        public Dictionary<string, string> RelationshipMenus { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The unmodelled XML of entities, attributes, relationships and option sets (see UnmodelledXmlStore).</summary>
        public Dictionary<(int, string), string> Unmodelled { get; } = new();

        /// <summary>The creation order of the image columns, by (entity, attribute) (stored with the definition, not in the metadata).</summary>
        public Dictionary<(string, string), int> ImageOrder { get; } = new();

        /// <summary>ByLogicalName values sorted by name, rebuilt by <see cref="RefreshAll"/> before publishing.</summary>
        public IReadOnlyList<TableDefinition> All { get; private set; } = [];

        /// <summary>OptionSets values sorted by name, rebuilt by <see cref="RefreshAll"/> before publishing.</summary>
        public IReadOnlyList<ChoiceSetBase> AllOptionSets { get; private set; } = [];

        /// <summary>ManyToMany values sorted by SchemaName, rebuilt by <see cref="RefreshAll"/> before publishing.</summary>
        public IReadOnlyList<ManyToManyRelationship> AllManyToMany { get; private set; } = [];

        /// <summary>Every one-to-many relationship (lookups, under their saved names), rebuilt by <see cref="RefreshAll"/> before publishing.</summary>
        public IReadOnlyList<OneToManyRelationship> AllOneToMany { get; private set; } = [];

        /// <summary>The hierarchical relationship of each table that has one, from AllOneToMany, rebuilt by <see cref="RefreshAll"/>.</summary>
        public IReadOnlyDictionary<string, OneToManyRelationship> Hierarchical { get; private set; } =
            new Dictionary<string, OneToManyRelationship>(StringComparer.Ordinal);

        /// <summary>The tables with <c>AutoCreateAccessTeams</c>, rebuilt by <see cref="RefreshAll"/> before publishing.</summary>
        public IReadOnlySet<string> AccessTeamTables { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The tables with <c>ChangeTrackingEnabled</c>, rebuilt by <see cref="RefreshAll"/> before publishing.</summary>
        public IReadOnlySet<string> ChangeTrackingTables { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The activity tables (<c>IsActivity</c>, not the pointer), rebuilt by <see cref="RefreshAll"/> before publishing.</summary>
        public IReadOnlySet<string> ActivityTables { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The tables with <c>HasActivities</c>, rebuilt by <see cref="RefreshAll"/> before publishing.</summary>
        public IReadOnlySet<string> RegardingTables { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The tables with <c>HasNotes</c>, rebuilt by <see cref="RefreshAll"/> before publishing.</summary>
        public IReadOnlySet<string> NotesTables { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The tables with <c>IsValidForQueue</c> (plan 4.1a), rebuilt by <see cref="RefreshAll"/> before publishing.</summary>
        public IReadOnlySet<string> QueueTables { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The tables with <c>IsDuplicateDetectionEnabled</c>, rebuilt by <see cref="RefreshAll"/> before publishing.</summary>
        public IReadOnlySet<string> DuplicateDetectionTables { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The one-to-many relationships of the current (possibly unpublished) content.</summary>
        public IEnumerable<OneToManyRelationship> OneToMany() =>
            EntityMetadataExtensions.OneToManyRelationships(ByLogicalName.Values, SavedOneToMany.Values);

        public CacheState Clone()
        {
            var copy = new CacheState
            {
                All = All, AllOptionSets = AllOptionSets, AllManyToMany = AllManyToMany, AllOneToMany = AllOneToMany,
                Hierarchical = Hierarchical, AccessTeamTables = AccessTeamTables, ChangeTrackingTables = ChangeTrackingTables,
                ActivityTables = ActivityTables, RegardingTables = RegardingTables, NotesTables = NotesTables, QueueTables = QueueTables,
                DuplicateDetectionTables = DuplicateDetectionTables,
            };
            foreach (var (k, v) in ByLogicalName) copy.ByLogicalName[k] = v;
            foreach (var (k, v) in BySetName) copy.BySetName[k] = v;
            foreach (var (k, v) in ByObjectTypeCode) copy.ByObjectTypeCode[k] = v;
            foreach (var (k, v) in OptionSets) copy.OptionSets[k] = v;
            foreach (var (k, v) in ManyToMany) copy.ManyToMany[k] = v;
            foreach (var (k, v) in SavedOneToMany) copy.SavedOneToMany[k] = v;
            foreach (var (k, v) in RelationshipMenus) copy.RelationshipMenus[k] = v;
            foreach (var (k, v) in Unmodelled) copy.Unmodelled[k] = v;
            foreach (var (k, v) in ImageOrder) copy.ImageOrder[k] = v;
            return copy;
        }

        /// <summary>Adds <paramref name="entity"/>, replacing <paramref name="previous"/> (the instance it is a new version of).</summary>
        public void Put(TableDefinition entity, TableDefinition? previous)
        {
            if (previous is not null) Remove(previous);
            ByLogicalName[entity.LogicalName!] = entity;
            if (entity.EntitySetName is not null) BySetName[entity.EntitySetName] = entity;
            if (entity.ObjectTypeCode is { } code) ByObjectTypeCode[code] = entity;
        }

        public void Remove(TableDefinition entity)
        {
            ByLogicalName.Remove(entity.LogicalName!);
            if (entity.EntitySetName is not null) BySetName.Remove(entity.EntitySetName);
            if (entity.ObjectTypeCode is { } code) ByObjectTypeCode.Remove(code);
        }

        public void RefreshAll()
        {
            All = ByLogicalName.Values.OrderBy(e => e.LogicalName, StringComparer.Ordinal).ToList().AsReadOnly();
            AllOptionSets = OptionSets.Values.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .Cast<ChoiceSetBase>().ToList().AsReadOnly();
            AllManyToMany = ManyToMany.Values.OrderBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
            AllOneToMany = OneToMany().OrderBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
            // At most one per table (CreateOneToMany and the solution import refuse a second one); the first by name otherwise.
            var hierarchical = new Dictionary<string, OneToManyRelationship>(StringComparer.Ordinal);
            foreach (var r in AllOneToMany)
                if (IsHierarchicalRelationship(r)) hierarchical.TryAdd(r.ReferencingEntity!, r);
            Hierarchical = hierarchical;
            AccessTeamTables = ByLogicalName.Values.Where(e => e.AutoCreateAccessTeams == true).Select(e => e.LogicalName!)
                .ToHashSet(StringComparer.Ordinal);
            ChangeTrackingTables = ByLogicalName.Values.Where(e => e.ChangeTrackingEnabled == true).Select(e => e.LogicalName!)
                .ToHashSet(StringComparer.Ordinal);
            ActivityTables = ByLogicalName.Values.Where(ActivitySchema.IsActivityTable).Select(e => e.LogicalName!).ToHashSet(StringComparer.Ordinal);
            RegardingTables = ByLogicalName.Values.Where(e => e.HasActivities == true).Select(e => e.LogicalName!).ToHashSet(StringComparer.Ordinal);
            NotesTables = ByLogicalName.Values.Where(e => e.HasNotes == true).Select(e => e.LogicalName!).ToHashSet(StringComparer.Ordinal);
            QueueTables = ByLogicalName.Values.Where(e => e.IsValidForQueue?.Value == true).Select(e => e.LogicalName!).ToHashSet(StringComparer.Ordinal);
            DuplicateDetectionTables = ByLogicalName.Values.Where(e => e.IsDuplicateDetectionEnabled?.Value == true).Select(e => e.LogicalName!)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>The write state of a session holding the write lock: its cache copy and the last version stamp it wrote.</summary>
    private sealed class PendingChanges(CacheState working)
    {
        public CacheState Working { get; } = working;
        public long? Stamp { get; set; }
    }

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConditionalWeakTable<DataSession, PendingChanges> _pending = new();

    /// <summary>
    /// True on the call path of a session write that holds the write lock (handlers it calls, for instance): a call there
    /// that needs the lock again (LoadAsync, a non-session overload, another session's first write) would wait forever.
    /// </summary>
    private readonly AsyncLocal<bool> _sessionOnCallPath = new();
    private volatile CacheState _cache = new();
    private long _versionStamp;

    public long VersionStamp => Interlocked.Read(ref _versionStamp);

    /// <inheritdoc />
    public IReadOnlySet<string> ChangeTrackingTables => _cache.ChangeTrackingTables;

    // The reads give the cache's own instances: a published instance is never changed (writes replace it with a changed copy),
    // so a reader may keep it, and its SDK view (SdkViewCache) is never stale.
    public TableDefinition? GetTable(string logicalName) =>
        _cache.ByLogicalName.TryGetValue(logicalName, out var e) ? e : null;

    public TableDefinition? GetTable(DataSession session, string logicalName)
    {
        ArgumentNullException.ThrowIfNull(session);
        var cache = _pending.TryGetValue(session, out var pending) ? pending.Working : _cache;
        return cache.ByLogicalName.GetValueOrDefault(logicalName) is { } e ? e : null;
    }

    public TableDefinition? GetTableBySetName(string entitySetName) =>
        _cache.BySetName.TryGetValue(entitySetName, out var e) ? e : null;

    public TableDefinition? GetTableByObjectTypeCode(int objectTypeCode) =>
        _cache.ByObjectTypeCode.TryGetValue(objectTypeCode, out var e) ? e : null;

    public IReadOnlyList<TableDefinition> GetAllTables() => _cache.All;

    /// <summary>
    /// Test only: replaces the cache's definition of table <paramref name="name"/> by a copy changed by <paramref name="change"/>,
    /// in memory (no <c>meta</c> row, no DDL, no version stamp), for a test that gives a definition a setting the API refuses
    /// (securing a seeded column, a third state). The read, the copy, the change and the publish happen under the write lock, so
    /// a concurrent change is not lost. A later <see cref="LoadAsync"/> reads the stored definition again.
    /// </summary>
    /// <exception cref="InvalidOperationException">No table is named <paramref name="name"/>.</exception>
    internal void ReplaceInMemory(string name, Action<TableDefinition> change)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(change);
        EnsureNoSessionOnCallPath();
        _writeLock.Wait();
        try
        {
            var state = _cache.Clone();
            var current = state.ByLogicalName.GetValueOrDefault(name) ?? throw new InvalidOperationException($"No table '{name}'.");
            var copy = MetadataJson.Clone(current);
            change(copy);
            state.Put(copy, current);
            state.RefreshAll();
            _cache = state;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        EnsureNoSessionOnCallPath();
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = await factory.OpenAsync(ct);

            var state = new CacheState();
            await LoadOptionSetsAsync(conn, state, ct);
            await LoadRelationshipsAsync(conn, state, ct);

            var attributes = new Dictionary<string, List<ColumnDefinition>>(StringComparer.Ordinal);
            await using (var cmd = conn.Command(
                             "SELECT entity_logical_name, definition, unmodelled_xml FROM meta.attribute ORDER BY entity_logical_name, logical_name"))
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
            {
                while (await rd.ReadAsync(ct))
                {
                    var entityName = rd.GetString(0);
                    if (!attributes.TryGetValue(entityName, out var list)) attributes[entityName] = list = [];
                    var attribute = MetadataJson.DeserializeColumn(rd.GetString(1));
                    ClearComputedSecurability(attribute); // computed on read (FieldSecurability), never taken from storage
                    if (attribute is ImageColumn && StoredImageOrdinal(rd.GetString(1)) is { } ordinal)
                        state.ImageOrder[(entityName, attribute.LogicalName!)] = ordinal;
                    ResolveGlobalOptionSet(state, attribute, required: false);
                    list.Add(attribute);
                    if (!rd.IsDBNull(2)) state.Unmodelled[(AttributeComponent, $"{entityName}.{attribute.LogicalName}")] = rd.GetString(2);
                }
            }

            await using (var cmd = conn.Command("SELECT definition, unmodelled_xml FROM meta.entity"))
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
            {
                while (await rd.ReadAsync(ct))
                {
                    var entity = MetadataJson.DeserializeTable(rd.GetString(0));
                    entity.CanChangeTrackingBeEnabled = null; // computed on read (ChangeTrackingSchema), never taken from storage
                    entity.SetAttributes(attributes.GetValueOrDefault(entity.LogicalName!) ?? []);
                    // A column stored secured before the securable rule existed, which the rule refuses, is read unsecured.
                    foreach (var attribute in entity.Attributes!.Where(a => a.IsSecured == true && !FieldSecurability.IsSecurable(entity, a)))
                        attribute.IsSecured = false;
                    state.Put(entity, previous: null);
                    if (!rd.IsDBNull(1)) state.Unmodelled[(EntityComponent, entity.LogicalName!)] = rd.GetString(1);
                }
            }
            // SourceTypeMask is computed on every read (ruling K12), here once the whole cache is known; stores never keep it.
            foreach (var table in state.ByLogicalName.Values)
                foreach (var attribute in table.Attributes ?? [])
                    ComputedColumnRules.ApplyMask(table, attribute, n => state.ByLogicalName.GetValueOrDefault(n), _computed);
            state.RefreshAll();

            long stamp;
            await using (var cmd = conn.Command($"SELECT {SqlDialects.For(conn).Quote("current")} FROM meta.version_stamp"))
                stamp = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

            // Cache first, then the stamp (as the writers do): a reader seeing the new stamp also sees the new cache.
            _cache = state;
            Interlocked.Exchange(ref _versionStamp, stamp);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // The write members work on a copy of what they are given (a null stays null, so the write reports it as before): the
    // caller keeps its instance, and nothing a caller holds is ever put in the cache.
    private static T CopyOf<T>(T? definition) where T : class => definition is null ? null! : MetadataJson.Clone(definition);

    public Task CreateEntityAsync(TableDefinition entity, ColumnDefinition primaryName, CancellationToken ct = default) =>
        InOwnSessionAsync(session => CreateEntityCoreAsync(session, CopyOf(entity), CopyOf(primaryName), seed: false, ct), ct);

    public Task CreateEntityAsync(DataSession session, TableDefinition entity, ColumnDefinition primaryName, CancellationToken ct) =>
        CreateEntityCoreAsync(session, CopyOf(entity), CopyOf(primaryName), seed: false, ct);

    /// <summary>
    /// Seed-only path: like <see cref="CreateEntityAsync(TableDefinition, ColumnDefinition, CancellationToken)"/> but accepts
    /// caller-defined <c>statecode</c>/<c>statuscode</c> attributes (entity-specific option sets). The public API keeps
    /// rejecting them as reserved names. A seeded entity without a primary name (intersect entities,
    /// <c>principalobjectaccess</c>) passes null.
    /// </summary>
    internal Task CreateSeedEntityAsync(TableDefinition entity, ColumnDefinition? primaryName, CancellationToken ct = default) =>
        InOwnSessionAsync(session => CreateEntityCoreAsync(session, entity, primaryName, seed: true, ct), ct);

    /// <summary>
    /// Upgrade path for <see cref="SystemSeed"/> only: renames the entity set of a seeded system entity whose earlier seed used a
    /// name that differs from Dataverse's. The public <see cref="UpdateEntityAsync(DataSession, TableDefinition, CancellationToken)"/>
    /// keeps refusing any EntitySetName change.
    /// Persisted in <c>meta.entity</c> and reflected in the cache's set-name index.
    /// </summary>
    internal Task RenameSeedEntitySetAsync(string entityLogicalName, string entitySetName, CancellationToken ct = default) =>
        InOwnSessionAsync(async session =>
        {
            if (!LogicalNameValidator.IsValidEntitySetName(entitySetName))
                throw new XrmdValidationException(LogicalNameValidator.InvalidEntitySetNameMessage(entitySetName));
            var pending = await EnterAsync(session, ct);
            var current = pending.Working.ByLogicalName.GetValueOrDefault(entityLogicalName)
                          ?? throw new XrmdValidationException($"Entity '{entityLogicalName}' does not exist.");
            if (pending.Working.BySetName.TryGetValue(entitySetName, out var holder) && holder.LogicalName != current.LogicalName)
                throw new MetadataComponentExistsException($"EntitySetName '{entitySetName}' is already used by another entity.");

            var updated = MetadataJson.Clone(current);
            updated.EntitySetName = entitySetName;
            var stamp = await BumpVersionStampAsync(session, pending, ct);
            await ExecuteAsync(session, "UPDATE meta.entity SET definition = @definition, version_stamp = @stamp WHERE logical_name = @name", ct,
                ("@definition", SerializeWithoutAttributes(updated)), ("@stamp", stamp), ("@name", updated.LogicalName!));
            pending.Working.Put(updated, previous: current);
        }, ct);

    /// <summary>
    /// Upgrade path of a seeded table whose earlier seed got its object type code or primary key name wrong (#105:
    /// <c>appmoduleroles</c>): renames the key column and its attribute, keeping the rows, and sets the type code. A code
    /// another table holds is refused.
    /// </summary>
    internal Task CorrectSeedEntityAsync(string entityLogicalName, int objectTypeCode, string primaryId, CancellationToken ct = default) =>
        InOwnSessionAsync(async session =>
        {
            var pending = await EnterAsync(session, ct);
            var current = pending.Working.ByLogicalName.GetValueOrDefault(entityLogicalName)
                          ?? throw new XrmdValidationException($"Entity '{entityLogicalName}' does not exist.");
            if (pending.Working.ByObjectTypeCode.TryGetValue(objectTypeCode, out var holder) && holder.LogicalName != current.LogicalName)
                throw new InvalidOperationException($"Seed type code {objectTypeCode} of '{entityLogicalName}' is held by '{holder.LogicalName}'.");
            var updated = MetadataJson.Clone(current);
            var stamp = await BumpVersionStampAsync(session, pending, ct);
            var oldId = SystemAttributes.PrimaryIdName(current);
            if (oldId != primaryId)
            {
                var key = (current.Attributes ?? []).Single(a => a.LogicalName == oldId);
                var renamed = MetadataJson.Clone(key);
                renamed.LogicalName = primaryId;
                renamed.SchemaName = primaryId;
                await ExecuteAsync(session, session.Dialect.RenameColumn(DdlGenerator.DataSchema, entityLogicalName, oldId, primaryId), ct);
                await ExecuteAsync(session,
                    "UPDATE meta.attribute SET logical_name = @new, definition = @definition, version_stamp = @stamp WHERE entity_logical_name = @entity AND logical_name = @old", ct,
                    ("@new", primaryId), ("@definition", StoredAttributeJson(renamed, null)), ("@stamp", stamp), ("@entity", entityLogicalName), ("@old", oldId));
                updated.PrimaryIdAttribute = primaryId;
                updated.SetAttributes((current.Attributes ?? []).Select(a => a.LogicalName == oldId ? renamed : a));
            }
            updated.ObjectTypeCode = objectTypeCode;
            await ExecuteAsync(session, "UPDATE meta.entity SET definition = @definition, version_stamp = @stamp WHERE logical_name = @name", ct,
                ("@definition", SerializeWithoutAttributes(updated)), ("@stamp", stamp), ("@name", updated.LogicalName!));
            pending.Working.Put(updated, previous: current);
        }, ct);

    /// <summary>
    /// Upgrade path for the startup upgrades (audit settings, currency columns, <c>overriddencreatedon</c>): runs
    /// <paramref name="write"/> in a session on a connection and
    /// transaction of its own (committed when it succeeds, rolled back otherwise).
    /// </summary>
    internal Task InUpgradeSessionAsync(Func<DataSession, Task> write, CancellationToken ct = default) => InOwnSessionAsync(write, ct);

    /// <summary>
    /// Upgrade path for the audit settings upgrade only: stores the <c>IsAuditEnabled</c> setting of tables and columns exactly
    /// as given (the public updates apply the <c>CanBeChanged</c> rule, which an upgrade of an existing organization must not).
    /// </summary>
    /// <param name="settings">The table (<c>Attribute</c> null) or column to set, with its setting; one that does not exist is skipped.</param>
    internal async Task SetAuditSettingsAsync(DataSession session, IReadOnlyList<(string Entity, string? Attribute, ManagedFlag Setting)> settings,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Count == 0) return;
        var pending = await EnterAsync(session, ct);
        var stamp = await BumpVersionStampAsync(session, pending, ct);
        foreach (var group in settings.GroupBy(s => s.Entity, StringComparer.Ordinal))
        {
            if (pending.Working.ByLogicalName.GetValueOrDefault(group.Key) is not { } entity) continue;
            var copy = MetadataJson.Clone(entity);
            foreach (var (_, attribute, setting) in group)
            {
                if (attribute is null)
                {
                    copy.IsAuditEnabled = setting;
                    await ExecuteAsync(session, "UPDATE meta.entity SET definition = @definition, version_stamp = @stamp WHERE logical_name = @name", ct,
                        ("@definition", SerializeWithoutAttributes(copy)), ("@stamp", stamp), ("@name", copy.LogicalName!));
                    continue;
                }
                if ((copy.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attribute) is not { } target) continue;
                target.IsAuditEnabled = setting;
                await ExecuteAsync(session,
                    "UPDATE meta.attribute SET definition = @definition, version_stamp = @stamp WHERE entity_logical_name = @entity AND logical_name = @name", ct,
                    ("@definition", StoredAttributeJson(target, OrdinalOf(pending.Working.ImageOrder, copy.LogicalName!, target))), ("@stamp", stamp),
                    ("@entity", copy.LogicalName!), ("@name", target.LogicalName!));
            }
            pending.Working.Put(copy, previous: entity);
        }
    }

    public Task AddAttributeAsync(string entityLogicalName, ColumnDefinition attribute, CancellationToken ct = default) =>
        InOwnSessionAsync(session => AddAttributeAsync(session, entityLogicalName, attribute, ct), ct);

    /// <summary>
    /// Runs <paramref name="write"/> in a session on a connection and transaction of its own, committed when it succeeds and
    /// rolled back otherwise; the session's outcome is reported (publishing or discarding its cache changes). The write
    /// lock is taken before a pooled connection is opened, so waiting writers do not hold connections and transactions.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on the call path of a session holding the write lock.</exception>
    private async Task InOwnSessionAsync(Func<DataSession, Task> write, CancellationToken ct)
    {
        EnsureNoSessionOnCallPath();
        await _writeLock.WaitAsync(ct);
        DbConnection? opened = null;
        DbTransaction tx;
        try
        {
            opened = await factory.OpenAsync(ct);
            tx = await opened.BeginTransactionAsync(ct);
        }
        catch
        {
            if (opened is not null) await opened.DisposeAsync();
            _writeLock.Release();
            throw;
        }

        await using var conn = opened;
        await using (tx)
        {
            var session = new DataSession(conn, tx);
            Register(session); // from here the session's outcome releases the lock
            try
            {
                await write(session);
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
                    // The transaction is already gone (e.g. doomed and rolled back by the server): nothing is committed.
                }
                session.RolledBack();
                throw;
            }
            session.Committed();
        }
    }

    /// <summary>
    /// The write state of <paramref name="session"/>: on its first write the write lock is taken, the cache copied and the
    /// publish/discard registered with the session's outcome. Marks the caller's call path as holding a session (this
    /// method is not async, so the mark set here reaches the calling write and what it calls, and ends with it).
    /// </summary>
    /// <exception cref="InvalidOperationException">The session has no transaction, or another session holds the write lock
    /// on this call path (waiting for it would never end).</exception>
    private Task<PendingChanges> EnterAsync(DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Transaction is null)
            throw new InvalidOperationException("Metadata writes need a transaction: the session has none.");
        if (_pending.TryGetValue(session, out var pending))
        {
            _sessionOnCallPath.Value = true;
            return Task.FromResult(pending);
        }

        EnsureNoSessionOnCallPath();
        _sessionOnCallPath.Value = true;
        return AcquireAsync();

        async Task<PendingChanges> AcquireAsync()
        {
            await _writeLock.WaitAsync(ct);
            return Register(session);
        }
    }

    /// <summary>Starts the write state of <paramref name="session"/>; the caller holds the write lock.</summary>
    private PendingChanges Register(DataSession session)
    {
        var pending = new PendingChanges(_cache.Clone());
        _pending.Add(session, pending);
        session.OnCommitted(() => Complete(session, pending, publish: true));
        session.OnRolledBack(() => Complete(session, pending, publish: false));
        return pending;
    }

    private void EnsureNoSessionOnCallPath()
    {
        if (_sessionOnCallPath.Value)
            throw new InvalidOperationException("A metadata session is already in progress on this call path.");
    }

    private void Complete(DataSession session, PendingChanges pending, bool publish)
    {
        try
        {
            _pending.Remove(session);
            if (publish && pending.Stamp is { } stamp)
            {
                pending.Working.RefreshAll();
                // Cache first, then the stamp: a reader seeing the new stamp also sees the new cache.
                _cache = pending.Working;
                Interlocked.Exchange(ref _versionStamp, stamp);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<long> BumpVersionStampAsync(DataSession session, PendingChanges pending, CancellationToken ct)
    {
        var stamp = await ScalarAsync<long>(session, VersionStampSql(session.Dialect), ct);
        pending.Stamp = stamp;
        return stamp;
    }

    /// <param name="seed">A seeded entity: may define <c>statecode</c>/<c>statuscode</c> and have no primary name.</param>
    /// <param name="intersectPair">An intersect entity (no primary name): its two columns get a unique index.</param>
    private async Task CreateEntityCoreAsync(DataSession session, TableDefinition entity, ColumnDefinition? primaryName, bool seed,
        CancellationToken ct, (string First, string Second)? intersectPair = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (primaryName is null && !seed && intersectPair is null)
            throw new ArgumentNullException(nameof(primaryName));

        // AutoCreateAccessTeams is checked (DemandAccessTeamsAllowed) and given its regarding target only on the ordinary create
        // path. The seed and intersect paths are internal (seed files, the intersect of a new many-to-many relationship), and
        // none of them asks for the flag today; one that did would get an unchecked flag with no regarding target, so it fails
        // instead of persisting it silently.
        if ((seed || intersectPair is not null) && entity.AutoCreateAccessTeams == true)
            throw new XrmdValidationException(
                $"AutoCreateAccessTeams cannot be set on entity '{entity.LogicalName}' when it is created as a seeded or intersect table.");

        // Work on copies: the caller's objects stay untouched, also when validation or the transaction fails.
        var e = MetadataJson.Clone(entity);
        e.CanChangeTrackingBeEnabled = null; // computed on read (ChangeTrackingSchema), never stored
        e.Keys = null; // an elastic table's key is computed on read (ElasticTables.NoSqlKey), never stored
        var name = primaryName is null ? null : MetadataJson.Clone(primaryName);
        // Plan 2.11: a custom activity (ruling A4) and the settings HasActivities and HasNotes (A5) on the ordinary path only; a
        // seeded table carries its own, an intersect table none.
        var ordinary = !seed && intersectPair is null;
        var activity = ordinary && e.IsActivity == true;
        if (activity) PrepareCustomActivity(e, name);
        else if (ordinary) DemandNoActivitySettings(e);
        if (intersectPair is not null && (e.HasActivities == true || e.HasNotes == true || e.IsValidForQueue?.Value == true))
            throw new XrmdValidationException($"Entity '{e.LogicalName}' is an intersect entity: it cannot have HasActivities, HasNotes or IsValidForQueue.");
        // IsValidForQueue (plan 4.1a): stored in one shape (the value, changeable, its managed property name).
        if (e.IsValidForQueue is { } queueFlag) e.IsValidForQueue = QueueSchema.SettingManagedFlag(queueFlag.Value);
        LogicalNameValidator.EnsureValid(e.LogicalName, "entity");
        if (name is not null) LogicalNameValidator.EnsureValid(name.LogicalName, "attribute");

        var given = new List<ColumnDefinition>();
        if (name is not null) given.Add(name);
        given.AddRange((e.Attributes ?? []).Where(a => a.LogicalName != name?.LogicalName));
        if (!seed && intersectPair is null)
        {
            // Plan 3.4: the type is Standard (stored as no type) or Elastic, and an elastic table takes no column of ruling E9.
            ElasticTables.DemandValidEntity(e);
            foreach (var a in given) ElasticTables.DemandValidAttribute(e, a);
            e.TableType = ElasticTables.IsElastic(e) ? ElasticTables.TableTypeElastic : null;
        }
        foreach (var a in given)
        {
            LogicalNameValidator.EnsureValid(a.LogicalName, "attribute");
            // A seed definition may carry its own state and status, the companions it reads through AttributeOf (the EntityName
            // and name columns of plan 2.11) and the read-only owner copy of activityparty (ruling A10).
            if (!(seed && (a.LogicalName is "statecode" or "statuscode" || a.AttributeOf is not null
                           || (e.LogicalName == ActivitySchema.Party && a.LogicalName == "ownerid"))))
                EnsureDefinableByCaller(e.LogicalName!, a);
            if (!seed) EnsureNotCustomer(e.LogicalName!, a);
            if (!seed) EnsureNotSeedOnlyType(e.LogicalName!, a);
            if (!seed) EnsureNotCurrencyPair(e.LogicalName!, a);
            if (activity && a != name && ActivitySchema.IsGeneratedActivityAttribute(a.LogicalName))
                throw new XrmdValidationException(
                    $"Attribute '{a.LogicalName}' is a system attribute of custom activity '{e.LogicalName}' and cannot be defined.");
        }
        // The columns a custom activity gets from the server: they are not the caller's, so the rules above do not apply to them.
        if (activity) given.AddRange(ActivitySchema.CustomActivityColumns());
        EnsureDistinct(e.LogicalName!, given);
        EnsureNoBaseCompanionClash(e.LogicalName!, given);
        // Rollup columns bring their _date and _state (plan 2.12b, ruling R2).
        if (!seed) given.AddRange(RollupCompanions(e.LogicalName!, [], given));
        // File and image columns bring their companions, as CreateAttribute gives them.
        var withFiles = !seed && given.Any(FileColumns.IsFileOrImage);
        if (withFiles)
        {
            if (e.LogicalName == FileColumns.AttachmentEntity)
                throw new XrmdValidationException($"Entity '{FileColumns.AttachmentEntity}' holds the files: it cannot have a file or image column itself.");
            foreach (var main in given.Where(FileColumns.IsFileOrImage).ToList())
            {
                main.MetadataId ??= Guid.NewGuid();
                given.AddRange(FileColumns.Companions(main));
            }
            EnsureDistinct(e.LogicalName!, given);
        }

        // The primary key is server-owned: a caller-supplied PrimaryIdAttribute is ignored. A seed definition may name its own
        // (usersettings is keyed by systemuserid, plan 2.10).
        e.PrimaryIdAttribute =
            seed && entity.PrimaryIdAttribute is { Length: > 0 } seededId ? seededId
            : activity ? ActivitySchema.ActivityId
            : SystemAttributes.PrimaryIdName(e.LogicalName!);
        e.PrimaryNameAttribute = name?.LogicalName;
        e.MetadataId ??= Guid.NewGuid();
        e.SchemaName ??= e.LogicalName;
        e.EntitySetName ??= e.LogicalName + "s";
        if (!LogicalNameValidator.IsValidEntitySetName(e.EntitySetName))
            throw new XrmdValidationException(LogicalNameValidator.InvalidEntitySetNameMessage(e.EntitySetName));
        e.OwnershipType ??= TableOwnership.UserOwned;
        // A seeded table carries its own setting (the seed file) or the default of a seeded table; a table made in xrmverse
        // starts off and can change (a solution file may say otherwise); a many-to-many's intersect table is fixed.
        e.IsAuditEnabled = seed ? AuditDefaults.ManagedFlag(e.IsAuditEnabled?.Value ?? AuditDefaults.ForEntityFlag(e.LogicalName!, seed: true).Value,
                e.IsAuditEnabled?.CanBeChanged ?? AuditDefaults.ForEntityFlag(e.LogicalName!, seed: true).CanBeChanged)
            : intersectPair is not null ? AuditDefaults.ManagedFlag(false, canBeChanged: false)
            : AuditDefaults.ManagedFlag(e.IsAuditEnabled?.Value ?? false, e.IsAuditEnabled?.CanBeChanged ?? true);

        var attributes = SystemAttributes.Complete(e, given);
        foreach (var a in attributes)
        {
            LogicalNameValidator.EnsureValid(a.LogicalName, "attribute");
            Prepare(e.LogicalName!, a, e.PrimaryIdAttribute, seed);
        }
        var inlineOrder = new Dictionary<(string, string), int>();
        if (withFiles)
        {
            // The first image column is the primary image; a later one asking for it is not (ignored on create, ruling F5).
            var seen = new List<ColumnDefinition>();
            foreach (var image in attributes.OfType<ImageColumn>())
            {
                ClaimPrimaryImage(e.LogicalName!, image, seen, inlineOrder);
                seen.Add(image);
            }
        }
        // The currency pair Complete added for a money column comes with its relationship (ruling X4). A seed definition declares
        // its own pair and relationship (account, contact).
        var withCurrency = !seed && intersectPair is null && (activity || attributes.Any(a => a is MoneyColumn { IsBaseCurrency: not true }));
        attributes.Single(a => a.LogicalName == e.PrimaryIdAttribute).IsPrimaryId = true;
        if (name is not null) name.IsPrimaryName = true;
        e.SetAttributes(attributes);
        EnsureSecurable(e, attributes);

        var ddl = GenerateDdl(() => intersectPair is { } pair
            ? DdlGenerator.CreateIntersectTable(e, attributes, pair.First, pair.Second, session.Dialect)
            : DdlGenerator.CreateTable(e, attributes, session.Dialect));

        var pending = await EnterAsync(session, ct);
        var cache = pending.Working;
        foreach (var a in attributes) ResolveGlobalOptionSet(cache, a, required: true);
        foreach (var a in attributes)
            ValidateComputed(cache, e, a, null, n => n == e.LogicalName ? e : cache.ByLogicalName.GetValueOrDefault(n), creatingEntity: true);
        // A seeded entity may name tables seeded after it; the generated system lookups target system tables.
        if (!seed) EnsureLookupTargets(cache, e.LogicalName!, given, creating: e.LogicalName);
        if (cache.ByLogicalName.ContainsKey(e.LogicalName!))
            throw new MetadataComponentExistsException($"Entity '{e.LogicalName}' already exists.");
        if (cache.BySetName.ContainsKey(e.EntitySetName))
            throw new MetadataComponentExistsException($"EntitySetName '{e.EntitySetName}' is already used by another entity.");
        if (withFiles) EnsureAttachmentLinkable(cache, e.LogicalName!);
        if (withCurrency) EnsureCurrencyLinkable(cache, e.LogicalName!);
        var accessTeams = e.AutoCreateAccessTeams == true; // only on the ordinary path (refused above otherwise)
        if (accessTeams) DemandAccessTeamsAllowed(cache, e);
        // ChangeTrackingEnabled (plan 2.13, ruling C2): an intersect table or one of the ineligible tables cannot have it.
        var tracking = e.ChangeTrackingEnabled == true;
        if (tracking && (intersectPair is not null || !ChangeTrackingSchema.CanBeEnabled(e))) throw TrackingNotAllowed(e.LogicalName!);
        var queues = e.IsValidForQueue?.Value == true;
        var activityLinks = ordinary && (activity || e.HasActivities == true || e.HasNotes == true || queues);
        if (ordinary) DemandActivitySettingsAllowed(cache, e, e.HasActivities == true, e.HasNotes == true);
        if (ordinary && queues) DemandQueueSettingAllowed(cache, e);
        // IsDuplicateDetectionEnabled (plan 2.15): stored as a flag; an intersect table or a seeded system table other than
        // account and contact cannot have it.
        var duplicates = e.IsDuplicateDetectionEnabled?.Value == true;
        e.IsDuplicateDetectionEnabled = e.IsDuplicateDetectionEnabled is null ? null : DuplicateDetectionFlag(duplicates);
        if (duplicates && (intersectPair is not null || !DuplicateDetectionSchema.CanBeEnabled(e))) throw DuplicateDetectionNotAllowed(e.LogicalName!);

        var stamp = await BumpVersionStampAsync(session, pending, ct);
        if (await ScalarAsync<int>(session, "SELECT COUNT(*) FROM meta.entity WHERE logical_name = @name", ct, ("@name", e.LogicalName!)) > 0)
            throw new MetadataComponentExistsException($"Entity '{e.LogicalName}' already exists.");

        if (e.ObjectTypeCode is null)
            e.ObjectTypeCode = (await NextObjectTypeCodeAsync(session, ct));

        await ExecuteAsync(session,
            "INSERT INTO meta.entity (metadata_id, logical_name, definition, version_stamp) VALUES (@id, @name, @definition, @stamp)", ct,
            ("@id", e.MetadataId.Value), ("@name", e.LogicalName!), ("@definition", SerializeWithoutAttributes(e)), ("@stamp", stamp));
        foreach (var (key, value) in inlineOrder) cache.ImageOrder[key] = value;
        await InsertAttributesAsync(session, e.LogicalName!, attributes, stamp, ct, cache.ImageOrder);
        await ExecuteAsync(session, session.Dialect.CreateSchemaIfMissing(DdlGenerator.DataSchema), ct);
        await ExecuteAsync(session, ddl, ct);
        foreach (var a in attributes.Where(a => a.AutoNumberFormat is not null))
            await ExecuteAsync(session, AutoNumberSequences.EnsureSql(a, session.Dialect), ct);

        e.SetAttributes(attributes);
        cache.Put(e, previous: null);
        if (withFiles) await LinkFileAttachmentsAsync(session, pending, e.LogicalName!, stamp, ct);
        if (withCurrency) await EnsureCurrencyRelationshipAsync(session, e.LogicalName!, ct);
        if (accessTeams) await AddRegardingTargetAsync(session, cache, e.LogicalName!, stamp, ct);
        if (tracking) await StartTrackingAsync(session, e.LogicalName!, ct);
        if (activityLinks) await SyncActivityLinksAsync(session, pending, ct);
        if (duplicates) await AddDuplicateRecordTargetAsync(session, cache, e.LogicalName!, stamp, ct);

        foreach (var secured in attributes.Where(a => a.IsSecured == true)) NotifySecurityChanged(session, e.LogicalName!, secured.LogicalName!);
        foreach (var rollup in attributes.Where(RollupColumns.IsRollup)) NotifyRollupJobs(session, e.LogicalName!, rollup.LogicalName!, RollupChange.Added);
        foreach (var handler in createdHandlers ?? [])
            await handler.OnEntityCreatedAsync(SdkViewCache.Of(e), session, ct);
    }

    public Task AddAttributeAsync(DataSession session, string entityLogicalName, ColumnDefinition attribute, CancellationToken ct) =>
        AddAttributeCoreAsync(session, entityLogicalName, CopyOf(attribute), customerAllowed: false, ct);

    /// <summary>
    /// Seed-only path: like <see cref="AddAttributeAsync(string, ColumnDefinition, CancellationToken)"/> but accepts a Customer
    /// column (a seed table that gained one after it was first created). The public API creates Customer columns only with
    /// <see cref="CreateCustomerRelationshipsAsync"/>.
    /// </summary>
    internal Task AddSeedAttributeAsync(string entityLogicalName, ColumnDefinition attribute, CancellationToken ct = default) =>
        InOwnSessionAsync(session => AddAttributeCoreAsync(session, entityLogicalName, attribute, customerAllowed: true, ct), ct);

    /// <summary>
    /// Seed-only path: the options of the seed definition's local choice <paramref name="seeded"/> that the stored column lacks
    /// are added (an organization seeded before them: <c>asyncoperation.operationtype</c> 7 and 8, plan 2.15). Stored options
    /// are never changed or removed. Does nothing when none is missing.
    /// </summary>
    internal Task EnsureSeedOptionsAsync(string entityLogicalName, ChoiceColumn seeded, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seeded);
        if (_cache.ByLogicalName.GetValueOrDefault(entityLogicalName)?.Attributes?.FirstOrDefault(a => a.LogicalName == seeded.LogicalName)
                is not ChoiceColumn { OptionSet: { IsGlobal: not true } published }
            || (seeded.OptionSet?.Options ?? []).All(o => published.Options!.Any(p => p.Value == o.Value)))
            return Task.CompletedTask;
        return InOwnSessionAsync(async session =>
        {
            var pending = await EnterAsync(session, ct);
            var (entity, attribute) = FindAttribute(pending.Working, entityLogicalName, seeded.LogicalName!);
            var updated = (ChoiceColumn)MetadataJson.Clone(attribute);
            var options = updated.OptionSet!.Options!;
            foreach (var option in seeded.OptionSet!.Options!.Where(o => options.All(p => p.Value != o.Value)))
                options.Add(option);
            var sorted = options.OrderBy(o => o.Value).ToList();
            options.Clear();
            foreach (var option in sorted) options.Add(option);
            var stamp = await BumpVersionStampAsync(session, pending, ct);
            await StoreAttributeAsync(session, entityLogicalName, updated, stamp, ct, pending.Working.ImageOrder);
            var copy = MetadataJson.Clone(entity);
            copy.SetAttributes((entity.Attributes ?? []).Select(a => a.LogicalName == updated.LogicalName ? updated : a));
            pending.Working.Put(copy, previous: entity);
        }, ct);
    }

    /// <summary>
    /// Seed-only path (plan 3.2): gives the seeded lookup <paramref name="lookup"/> of <paramref name="entity"/> the targets
    /// <paramref name="targets"/> of its seed definition (an organization made before <c>sdkmessageprocessingstep.eventhandler</c>
    /// took <c>serviceendpoint</c>). When a single-target lookup becomes polymorphic, the <c>&lt;lookup&gt;type</c> column is
    /// added and filled, where the lookup is not null, with the object type code of the old target. The relationships the seed
    /// declares for the new targets are saved by the seed afterwards; the others follow from the targets. Idempotent: nothing is
    /// written when the stored targets are already <paramref name="targets"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The attribute is not a plain lookup, or a target does not exist.</exception>
    public Task SetSeedLookupTargetsAsync(string entity, string lookup, string[] targets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (_cache.ByLogicalName.GetValueOrDefault(entity)?.Attributes?.FirstOrDefault(a => a.LogicalName == lookup)
                is LookupColumn published && (published.Targets ?? []).SequenceEqual(targets))
            return Task.CompletedTask;
        return InOwnSessionAsync(async session =>
        {
            var pending = await EnterAsync(session, ct);
            var (table, attribute) = FindAttribute(pending.Working, entity, lookup);
            // A party list (plan 4.1b: the email party lists gain queue) has no data column: only its targets change.
            if (attribute is not LookupColumn { AttributeType: null or ColumnTypeCode.Lookup or ColumnTypeCode.PartyList } stored)
                throw new InvalidOperationException($"Seed targets of '{entity}.{lookup}': the attribute is not a lookup.");
            if ((stored.Targets ?? []).SequenceEqual(targets)) return;
            var updated = (LookupColumn)MetadataJson.Clone(stored);
            updated.Targets = [.. targets];
            try
            {
                EnsureLookupTargets(pending.Working, entity, [updated], creating: null);
            }
            catch (XrmdValidationException e)
            {
                throw new InvalidOperationException($"Seed targets of '{entity}.{lookup}': {e.Message}", e);
            }

            var stamp = await BumpVersionStampAsync(session, pending, ct);
            await StoreAttributeAsync(session, entity, updated, stamp, ct, pending.Working.ImageOrder);
            if (stored.AttributeType != ColumnTypeCode.PartyList && !TypeMapping.IsPolymorphicLookup(stored) && TypeMapping.IsPolymorphicLookup(updated))
            {
                var typeColumn = lookup + TypeMapping.TypeColumnSuffix;
                await ExecuteAsync(session,
                    session.Dialect.AddColumnIfMissing(DdlGenerator.DataSchema, entity, typeColumn, session.Dialect.ColumnType(SqlColumnKind.Int32)), ct);
                if (stored.Targets is [var old] && pending.Working.ByLogicalName.GetValueOrDefault(old)?.ObjectTypeCode is { } code)
                    await ExecuteAsync(session,
                        $"UPDATE {session.DataTable(entity)} SET {session.Quote(typeColumn)} = @code WHERE {session.Quote(lookup)} IS NOT NULL AND {session.Quote(typeColumn)} IS NULL", ct,
                        ("@code", code));
            }
            var copy = MetadataJson.Clone(table);
            copy.SetAttributes((table.Attributes ?? []).Select(a => a.LogicalName == updated.LogicalName ? updated : a));
            pending.Working.Put(copy, previous: table);
        }, ct);
    }

    private async Task AddAttributeCoreAsync(DataSession session, string entityLogicalName, ColumnDefinition attribute, bool customerAllowed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        LogicalNameValidator.EnsureValid(entityLogicalName, "entity");

        var a = MetadataJson.Clone(attribute);
        LogicalNameValidator.EnsureValid(a.LogicalName, "attribute");
        EnsureDefinableByCaller(entityLogicalName, a);
        EnsureNotSeedOnlyType(entityLogicalName, a);
        if (!customerAllowed) EnsureNotCustomer(entityLogicalName, a);
        List<ColumnDefinition> added = [a];
        if (a is MoneyColumn { IsBaseCurrency: not true } money) added.Add(SystemAttributes.MoneyBase(money));
        if (FileColumns.IsFileOrImage(a))
        {
            if (entityLogicalName == FileColumns.AttachmentEntity)
                throw new XrmdValidationException($"Entity '{FileColumns.AttachmentEntity}' holds the files: it cannot have a file or image column itself.");
            a.MetadataId ??= Guid.NewGuid();
            added.AddRange(FileColumns.Companions(a));
        }
        // A rollup column brings its _date and _state (plan 2.12b, ruling R2); their names are checked against the table below.
        if (RollupColumns.IsRollup(a)) added.AddRange(RollupCompanions(entityLogicalName, [], [a]));
        foreach (var x in added)
        {
            LogicalNameValidator.EnsureValid(x.LogicalName, "attribute");
            Prepare(entityLogicalName, x);
        }

        var pending = await EnterAsync(session, ct);
        var entity = pending.Working.ByLogicalName.GetValueOrDefault(entityLogicalName)
                     ?? throw new XrmdValidationException($"Entity '{entityLogicalName}' does not exist.");
        EnsureNotIntersect(entity);
        if (RollupColumns.IsRollup(a)) RollupCompanions(entityLogicalName, entity.Attributes ?? [], [a]); // a name clash: 0x80060556
        ValidateComputed(pending.Working, entity, a, null, n => pending.Working.ByLogicalName.GetValueOrDefault(n));
        ElasticTables.DemandValidAttribute(entity, a);
        // The currency table's own key and rate carry the pair's names: it cannot get a money column (and so a currency pair).
        if (entityLogicalName == CurrencySchema.Table && a is MoneyColumn)
            throw new XrmdValidationException(
                $"Entity '{CurrencySchema.Table}' cannot have a money column: its own '{CurrencySchema.CurrencyId}' and '{CurrencySchema.ExchangeRate}' " +
                "are the names of the currency columns a money column brings.");
        // The pair is generated with the first money column; only a seed definition declares it (account, contact: SystemSeed adds
        // it to a table made before it).
        if (!(IsSystemEntity(entity) && SystemSeed.Defines(entityLogicalName, a.LogicalName!))) EnsureNotCurrencyPair(entityLogicalName, a);
        // The first money column of a table brings the currency pair and its relationship (ruling X4).
        var withCurrency = a is MoneyColumn { IsBaseCurrency: not true } && !CurrencySchema.HasCurrencyColumns(entity);
        if (withCurrency)
        {
            foreach (var x in (ColumnDefinition[])[SystemAttributes.CurrencyLookupColumn(), SystemAttributes.ExchangeRateColumn()])
            {
                Prepare(entityLogicalName, x);
                added.Add(x);
            }
            EnsureCurrencyLinkable(pending.Working, entityLogicalName);
        }

        foreach (var x in added.Where(x => x != a))
            ComputedColumnRules.ApplyMask(entity, x, n => pending.Working.ByLogicalName.GetValueOrDefault(n), _computed);
        var ddl = GenerateDdl(() => string.Join("\n",
            added.Where(x => TypeMapping.ToColumns(x).Count > 0).Select(x => DdlGenerator.AddColumn(entityLogicalName, x, session.Dialect))));
        EnsureSecurable(entity, added);
        ResolveGlobalOptionSet(pending.Working, a, required: true);
        EnsureLookupTargets(pending.Working, entityLogicalName, added, creating: null);
        var existing = entity.Attributes ?? [];
        EnsureNoBaseCompanionClash(entityLogicalName, existing.Concat(added));
        EnsureDistinct(entityLogicalName, existing.Concat(added));
        var taken = existing.SelectMany(TypeMapping.ToColumns).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (added.SelectMany(TypeMapping.ToColumns).FirstOrDefault(c => taken.Contains(c.Name)) is { Name: not null } clash)
            throw new XrmdValidationException($"Column '{clash.Name}' already exists on entity '{entityLogicalName}'.");

        // The first file or image column of a table links it to fileattachment; only then can the relationship name clash.
        // A permanent target of fileattachment.objectid (plan 2.11 A16) is linked already, whatever its columns.
        var links = FileColumns.IsFileOrImage(a) && !existing.Any(FileColumns.IsFileOrImage) && !FileColumns.IsPermanentTarget(entityLogicalName);
        if (links) EnsureAttachmentLinkable(pending.Working, entityLogicalName);
        if (a is ImageColumn image) ClaimPrimaryImage(entityLogicalName, image, existing, pending.Working.ImageOrder);

        var stamp = await BumpVersionStampAsync(session, pending, ct);
        await InsertAttributesAsync(session, entityLogicalName, added, stamp, ct, pending.Working.ImageOrder);
        if (!string.IsNullOrWhiteSpace(ddl)) await ExecuteAsync(session, ddl, ct); // a calculated column adds no column
        if (a.AutoNumberFormat is not null) await ExecuteAsync(session, AutoNumberSequences.EnsureSql(a, session.Dialect), ct);
        if (RollupColumns.IsRollup(a)) await InitializeRollupRowsAsync(session, pending.Working, entity, a, ct); // ruling R7

        // Copy-on-write: the published cache keeps its own instance.
        var updated = MetadataJson.Clone(entity);
        updated.SetAttributes(existing.Concat(added));
        pending.Working.Put(updated, previous: entity);

        if (links) await LinkFileAttachmentsAsync(session, pending, entityLogicalName, stamp, ct);
        if (withCurrency) await EnsureCurrencyRelationshipAsync(session, entityLogicalName, ct);
        if (a.IsSecured == true) NotifySecurityChanged(session, entityLogicalName, a.LogicalName!);
        if (RollupColumns.IsRollup(a)) NotifyRollupJobs(session, entityLogicalName, a.LogicalName!, RollupChange.Added);
    }

    /// <summary>
    /// Upgrade path for <c>CurrencyColumnsUpgrade</c> only (ruling X14): gives the money table <paramref name="entityLogicalName"/>,
    /// made before plan 2.10, the currency pair (nullable columns) and its relationship when it lacks them (either), and its <c>_base</c>
    /// attributes the metadata of <see cref="SystemAttributes.MoneyBase"/>. Writes nothing for a table that is already up to date.
    /// </summary>
    /// <returns>True when the pair was added (its rows have no currency yet).</returns>
    /// <exception cref="InvalidOperationException">The table holds an attribute named like the pair that is not the pair.</exception>
    internal async Task<bool> EnsureCurrencyColumnsAsync(DataSession session, string entityLogicalName, CancellationToken ct)
    {
        var pending = await EnterAsync(session, ct);
        var entity = pending.Working.ByLogicalName.GetValueOrDefault(entityLogicalName)
                     ?? throw new XrmdValidationException($"Entity '{entityLogicalName}' does not exist.");
        if (CurrencySchema.MoneyAttributes(entity).Count == 0) return false;
        var existing = entity.Attributes ?? [];

        List<ColumnDefinition> added = [];
        if (!CurrencySchema.HasCurrencyColumns(entity))
        {
            if (existing.FirstOrDefault(x => CurrencySchema.IsPairName(x.LogicalName)) is { } clash)
                throw new InvalidOperationException(
                    $"Entity '{entityLogicalName}' has money columns and an attribute '{clash.LogicalName}' that is not its currency pair: " +
                    "the currency columns cannot be added.");
            foreach (var x in (ColumnDefinition[])[SystemAttributes.CurrencyLookupColumn(), SystemAttributes.ExchangeRateColumn()])
            {
                Prepare(entityLogicalName, x);
                added.Add(x);
            }
            EnsureCurrencyLinkable(pending.Working, entityLogicalName);
        }
        var fixedBases = existing.OfType<MoneyColumn>().Where(m => m.IsBaseCurrency == true)
            .Select(MetadataJson.Clone).Where(SystemAttributes.SetBaseMoneyMetadata).ToList();
        var related = pending.Working.SavedOneToMany.Values.Any(r => r.ReferencingEntity == entityLogicalName
                                                                    && r.ReferencingAttribute == CurrencySchema.CurrencyId && r.ReferencedEntity == CurrencySchema.Table);
        if (added.Count == 0 && fixedBases.Count == 0 && related) return false;

        var stamp = await BumpVersionStampAsync(session, pending, ct);
        foreach (var b in fixedBases) await StoreAttributeAsync(session, entityLogicalName, b, stamp, ct, pending.Working.ImageOrder);
        if (added.Count > 0)
        {
            await InsertAttributesAsync(session, entityLogicalName, added, stamp, ct, pending.Working.ImageOrder);
            await ExecuteAsync(session, GenerateDdl(() => string.Join("\n", added.Select(x => DdlGenerator.AddColumn(entityLogicalName, x, session.Dialect)))), ct);
        }

        var updated = MetadataJson.Clone(entity);
        updated.SetAttributes(existing.Select(x => fixedBases.FirstOrDefault(b => b.LogicalName == x.LogicalName) ?? x).Concat(added));
        pending.Working.Put(updated, previous: entity);
        if (!related) await EnsureCurrencyRelationshipAsync(session, entityLogicalName, ct);
        return added.Count > 0;
    }

    /// <summary>
    /// Upgrade path for <c>OverriddenCreatedOnUpgrade</c> only (#343.5): gives the table <paramref name="entityLogicalName"/>, made
    /// before the column, <c>overriddencreatedon</c> (<see cref="SystemAttributes.OverriddenCreatedOn"/>, a nullable column; its rows
    /// keep null). Writes nothing for a table that has it or has no <c>createdon</c>, nor for the audit table.
    /// </summary>
    /// <returns>True when the column was added.</returns>
    /// <exception cref="InvalidOperationException">The table holds an attribute named <c>overriddencreatedon</c> that is not a DateTime.</exception>
    internal async Task<bool> EnsureOverriddenCreatedOnAsync(DataSession session, string entityLogicalName, CancellationToken ct)
    {
        var pending = await EnterAsync(session, ct);
        var entity = pending.Working.ByLogicalName.GetValueOrDefault(entityLogicalName)
                     ?? throw new XrmdValidationException($"Entity '{entityLogicalName}' does not exist.");
        if (!SystemAttributes.NeedsOverriddenCreatedOn(entity)) return false;

        var added = SystemAttributes.OverriddenCreatedOn();
        Prepare(entityLogicalName, added);
        var stamp = await BumpVersionStampAsync(session, pending, ct);
        await InsertAttributesAsync(session, entityLogicalName, [added], stamp, ct, pending.Working.ImageOrder);
        await ExecuteAsync(session, GenerateDdl(() => DdlGenerator.AddColumn(entityLogicalName, added, session.Dialect)), ct);

        var updated = MetadataJson.Clone(entity);
        updated.SetAttributes((entity.Attributes ?? []).Append(added));
        pending.Working.Put(updated, previous: entity);
        return true;
    }

    public Task UpdateEntityAsync(DataSession session, TableDefinition changes, CancellationToken ct) =>
        UpdateEntityAsync(session, changes, replaceLabels: false, ct);

    public Task UpdateEntityAsync(DataSession session, TableDefinition changes, bool replaceLabels, CancellationToken ct) =>
        UpdateEntityCoreAsync(session, CopyOf(changes), replaceLabels, ct);

    private async Task UpdateEntityCoreAsync(DataSession session, TableDefinition changes, bool replaceLabels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        LogicalNameValidator.EnsureValid(changes.LogicalName, "entity");

        var pending = await EnterAsync(session, ct);
        var current = pending.Working.ByLogicalName.GetValueOrDefault(changes.LogicalName!)
                      ?? throw new XrmdValidationException($"Entity '{changes.LogicalName}' does not exist.");
        if (changes.EntitySetName is not null && changes.EntitySetName != current.EntitySetName)
            throw new XrmdValidationException(
                $"The EntitySetName of entity '{current.LogicalName}' cannot change (it is '{current.EntitySetName}').");

        // TableType (plan 3.4): fixed at creation; the same value, or a retrieved definition sent back, passes.
        if (changes.TableType is not null && ElasticTables.NormalizeTableType(changes.TableType) != ElasticTables.NormalizeTableType(current.TableType))
            throw XrmdFaultException.InvalidArgument(
                $"TableType of entity '{current.LogicalName}' cannot change (it is {ElasticTables.NormalizeTableType(current.TableType)}).");

        var updated = MetadataJson.Clone(current);
        if (Applies(changes.DisplayName, replaceLabels)) updated.DisplayName = UpdatedLabel(changes.DisplayName!);
        if (Applies(changes.DisplayCollectionName, replaceLabels)) updated.DisplayCollectionName = UpdatedLabel(changes.DisplayCollectionName!);
        if (Applies(changes.Description, replaceLabels)) updated.Description = UpdatedLabel(changes.Description!);
        if (changes.IsAuditEnabled is { } audit)
        {
            var now = current.IsAuditEnabled ?? AuditDefaults.ForEntityFlag(current.LogicalName!, IsSystemEntity(current));
            if (audit.Value != now.Value)
            {
                if (!now.CanBeChanged)
                    throw new XrmdValidationException($"IsAuditEnabled of entity '{current.LogicalName}' cannot change: its CanBeChanged is false.");
                updated.IsAuditEnabled = AuditDefaults.ManagedFlag(audit.Value, now.CanBeChanged);
            }
            else updated.IsAuditEnabled ??= now;
        }
        // AutoCreateAccessTeams (plan 2.16, ruling A1): only a change is checked, so a retrieved definition sent back passes.
        var accessTeams = changes.AutoCreateAccessTeams is { } flag && flag != (current.AutoCreateAccessTeams == true) ? flag : (bool?)null;
        if (accessTeams == true) ElasticTables.DemandNotElastic(current, "Access teams");
        if (accessTeams == true) DemandAccessTeamsAllowed(pending.Working, current);
        else if (accessTeams == false)
            await DemandNoTemplatesAsync(session, pending.Working, current, $"AutoCreateAccessTeams cannot be disabled on entity '{current.LogicalName}'", ct);
        if (accessTeams is { } value) updated.AutoCreateAccessTeams = value;
        // ChangeTrackingEnabled (plan 2.13): one-way; only a change is checked, so true sent again (or a retrieved definition) passes.
        var tracking = changes.ChangeTrackingEnabled is { } trackingFlag && trackingFlag != (current.ChangeTrackingEnabled == true) ? trackingFlag : (bool?)null;
        if (tracking == false)
            throw new XrmdValidationException(
                $"ChangeTrackingEnabled cannot be disabled on entity '{current.LogicalName}': once change tracking is on for a table, it stays on.");
        if (tracking == true && !ChangeTrackingSchema.CanBeEnabled(current)) throw TrackingNotAllowed(current.LogicalName!);
        if (tracking == true) updated.ChangeTrackingEnabled = true;
        // Plan 2.11: IsActivity and ActivityTypeMask are fixed at creation (A4); HasActivities and HasNotes are one-way (A5).
        var (regardingOn, notesOn) = ActivitySettingChanges(pending.Working, current, changes);
        if (regardingOn) updated.HasActivities = true;
        if (notesOn) updated.HasNotes = true;
        // IsValidForQueue (plan 4.1a): one-way like HasNotes.
        var queuesOn = QueueSettingChange(pending.Working, current, changes);
        if (queuesOn) updated.IsValidForQueue = QueueSchema.SettingManagedFlag(true);
        // A table without a value that is given false stores it (as IsAuditEnabled does), so that the startup default of the activity
        // tables (QueueSettingsUpgrade) no longer applies to it (final review I3).
        else if (changes.IsValidForQueue is { Value: false }) updated.IsValidForQueue ??= QueueSchema.SettingManagedFlag(false);
        // IsDuplicateDetectionEnabled (plan 2.15): only a change is checked, so a retrieved definition sent back passes.
        var duplicates = changes.IsDuplicateDetectionEnabled is { } duplicateFlag
                         && duplicateFlag.Value != (current.IsDuplicateDetectionEnabled?.Value == true) ? duplicateFlag.Value : (bool?)null;
        if (duplicates == true) ElasticTables.DemandNotElastic(current, "Duplicate detection settings");
        if (duplicates == true && !DuplicateDetectionSchema.CanBeEnabled(current)) throw DuplicateDetectionNotAllowed(current.LogicalName!);
        if (duplicates is { } duplicateValue) updated.IsDuplicateDetectionEnabled = DuplicateDetectionFlag(duplicateValue);

        var stamp = await BumpVersionStampAsync(session, pending, ct);
        await ExecuteAsync(session, "UPDATE meta.entity SET definition = @definition, version_stamp = @stamp WHERE logical_name = @name", ct,
            ("@definition", SerializeWithoutAttributes(updated)), ("@stamp", stamp), ("@name", updated.LogicalName!));
        pending.Working.Put(updated, previous: current);
        if (accessTeams == true) await AddRegardingTargetAsync(session, pending.Working, updated.LogicalName!, stamp, ct);
        if (tracking == true) await StartTrackingAsync(session, updated.LogicalName!, ct);
        if (regardingOn || notesOn || queuesOn) await SyncActivityLinksAsync(session, pending, ct);
        if (duplicates == true) await AddDuplicateRecordTargetAsync(session, pending.Working, updated.LogicalName!, stamp, ct);
        // Ruling D11: turning the flag off unpublishes the rules whose base or matching table it is, in this transaction.
        if (duplicates == false) await DuplicateDetectionSchema.UnpublishRulesAsync(session, updated.LogicalName, ct);
    }

    /// <summary>
    /// Startup path for the change tracking upgrade only: each tracked table of the published cache gets its index on
    /// <c>versionnumber</c> (ruling C11) and its <c>meta.change_tracking_table</c> row when either is missing (an organization
    /// that lost them; a missing row starts at the current version, so older tokens are expired). Writes no metadata.
    /// </summary>
    internal async Task EnsureChangeTrackingStorageAsync(CancellationToken ct = default)
    {
        var tables = _cache.ChangeTrackingTables;
        if (tables.Count == 0) return;
        await using var conn = await factory.OpenAsync(ct);
        foreach (var table in tables.Order(StringComparer.Ordinal))
        {
            var dialect = SqlDialects.For(conn);
            await using (var index = conn.Command(ChangeTrackingSchema.EnsureIndexSql(table, dialect)))
                await index.ExecuteNonQueryAsync(ct);
            await using var cmd = conn.Command(StartTrackingRowSql(dialect));
            cmd.AddParameter("@name", DbType.String, table);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Inserts the <c>meta.change_tracking_table</c> row of the table <c>@name</c> when it is missing, starting at
    /// <see cref="ISqlDialect.RowVersionStart"/> (plan 2.7a, Task 7: one statement on both engines; the callers hold the metadata
    /// lock or run at startup under the instance lock, so two never race on one table).
    /// </summary>
    private static string StartTrackingRowSql(ISqlDialect dialect) => $"""
        INSERT INTO meta.change_tracking_table (entity_logical_name, enabled_version)
        SELECT @name, {dialect.RowVersionStart}
        WHERE NOT EXISTS (SELECT 1 FROM meta.change_tracking_table WHERE entity_logical_name = @name);
        """;

    /// <summary>The refusal of <c>ChangeTrackingEnabled</c> on a table that cannot be tracked (ruling C2).</summary>
    private static XrmdValidationException TrackingNotAllowed(string entityLogicalName) => new(
        $"ChangeTrackingEnabled cannot be enabled on entity '{entityLogicalName}': intersect tables and the system tables whose rows xrmverse deletes directly cannot be tracked.");

    /// <summary>
    /// Starts tracking <paramref name="entityLogicalName"/> in the session's transaction: its index on <c>versionnumber</c>
    /// (ruling C11) and its <c>meta.change_tracking_table</c> row, whose <c>enabled_version</c> is the lowest version a
    /// transaction still active may write (<c>MIN_ACTIVE_ROWVERSION</c>), so that no later change is left out.
    /// </summary>
    private static async Task StartTrackingAsync(DataSession session, string entityLogicalName, CancellationToken ct)
    {
        await ExecuteAsync(session, ChangeTrackingSchema.EnsureIndexSql(entityLogicalName, session.Dialect), ct);
        await ExecuteAsync(session, StartTrackingRowSql(session.Dialect), ct, ("@name", entityLogicalName));
    }

    public async Task SetIsManagedAsync(DataSession session, string entityLogicalName, string? attributeLogicalName, bool? isManaged,
        CancellationToken ct)
    {
        LogicalNameValidator.EnsureValid(entityLogicalName, "entity");
        var pending = await EnterAsync(session, ct);
        if (attributeLogicalName is null)
        {
            var current = pending.Working.ByLogicalName.GetValueOrDefault(entityLogicalName)
                          ?? throw new XrmdValidationException($"Entity '{entityLogicalName}' does not exist.");
            // The columns a custom table is generated with (primary key, system columns) follow the table.
            var generated = IsSystemEntity(current) ? []
                : (current.Attributes ?? []).Where(a => IsSystemAttribute(current, a) && a.IsManaged != isManaged).Select(a => a.LogicalName)
                .ToHashSet(StringComparer.Ordinal);
            if (current.IsManaged == isManaged && generated.Count == 0) return;
            var updated = MetadataJson.Clone(current);
            updated.IsManaged = isManaged;
            foreach (var column in (updated.Attributes ?? []).Where(a => generated.Contains(a.LogicalName)))
                column.IsManaged = isManaged;
            var stamp = await BumpVersionStampAsync(session, pending, ct);
            await ExecuteAsync(session, "UPDATE meta.entity SET definition = @definition, version_stamp = @stamp WHERE logical_name = @name", ct,
                ("@definition", SerializeWithoutAttributes(updated)), ("@stamp", stamp), ("@name", updated.LogicalName!));
            foreach (var column in (updated.Attributes ?? []).Where(a => generated.Contains(a.LogicalName)))
                await ExecuteAsync(session,
                    "UPDATE meta.attribute SET definition = @definition, version_stamp = @stamp WHERE entity_logical_name = @entity AND logical_name = @name", ct,
                    ("@definition", StoredAttributeJson(column, OrdinalOf(pending.Working.ImageOrder, entityLogicalName, column))), ("@stamp", stamp),
                    ("@entity", entityLogicalName), ("@name", column.LogicalName!));
            pending.Working.Put(updated, previous: current);
            return;
        }

        var (entity, attribute) = FindAttribute(pending.Working, entityLogicalName, attributeLogicalName);
        if (attribute.IsManaged == isManaged) return;
        var changed = MetadataJson.Clone(attribute);
        changed.IsManaged = isManaged;
        var attributeStamp = await BumpVersionStampAsync(session, pending, ct);
        await ExecuteAsync(session,
            "UPDATE meta.attribute SET definition = @definition, version_stamp = @stamp WHERE entity_logical_name = @entity AND logical_name = @name", ct,
            ("@definition", StoredAttributeJson(changed, OrdinalOf(pending.Working.ImageOrder, entityLogicalName, changed))), ("@stamp", attributeStamp),
            ("@entity", entityLogicalName), ("@name", changed.LogicalName!));
        var entityCopy = MetadataJson.Clone(entity);
        entityCopy.SetAttributes((entity.Attributes ?? []).Select(a => a.LogicalName == changed.LogicalName ? changed : a));
        pending.Working.Put(entityCopy, previous: entity);
    }

    public Task UpdateAttributeAsync(DataSession session, string entityLogicalName, ColumnDefinition changes, CancellationToken ct) =>
        UpdateAttributeAsync(session, entityLogicalName, changes, replaceLabels: false, ct);

    public Task UpdateAttributeAsync(DataSession session, string entityLogicalName, ColumnDefinition changes, bool replaceLabels,
        CancellationToken ct) =>
        UpdateAttributeCoreAsync(session, entityLogicalName, CopyOf(changes), replaceLabels, ct);

    private async Task UpdateAttributeCoreAsync(DataSession session, string entityLogicalName, ColumnDefinition changes, bool replaceLabels,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        LogicalNameValidator.EnsureValid(entityLogicalName, "entity");
        LogicalNameValidator.EnsureValid(changes.LogicalName, "attribute");

        if (entityLogicalName == FileColumns.AttachmentEntity && changes.LogicalName == FileColumns.AttachmentLookup)
            throw AttachmentLookupIsManaged($"Attribute '{entityLogicalName}.{changes.LogicalName}'");

        var pending = await EnterAsync(session, ct);
        var (entity, current) = FindAttribute(pending.Working, entityLogicalName, changes.LogicalName!);
        EnsureNotIntersect(entity);
        DemandNotRollupCompanion(entity, current);
        if (current is LookupColumn && ActivitySchema.IsManagedLookup(entityLogicalName, current.LogicalName))
            throw ActivityLookupIsManaged($"Attribute '{entityLogicalName}.{current.LogicalName}'");
        if (changes.GetType() != current.GetType())
            throw new XrmdValidationException(
                $"Attribute '{current.LogicalName}' is a {SdkTwin.Sdk(current).GetType().Name}: its type cannot change to {SdkTwin.Sdk(changes).GetType().Name}.");

        var updated = MetadataJson.Clone(current);
        if (Applies(changes.DisplayName, replaceLabels)) updated.DisplayName = UpdatedLabel(changes.DisplayName!);
        if (Applies(changes.Description, replaceLabels)) updated.Description = UpdatedLabel(changes.Description!);
        if (changes.RequiredLevel is { } required) updated.RequiredLevel = MetadataJson.Clone(required);
        if (changes.IsAuditEnabled is { } audit)
        {
            var now = current.IsAuditEnabled ?? AuditDefaults.ForAttribute(current, entity.PrimaryIdAttribute);
            if (audit.Value != now.Value)
            {
                if (!now.CanBeChanged)
                    throw new XrmdValidationException(
                        $"IsAuditEnabled of attribute '{entityLogicalName}.{current.LogicalName}' cannot change: its CanBeChanged is false.");
                updated.IsAuditEnabled = AuditDefaults.ManagedFlag(audit.Value, now.CanBeChanged);
            }
            else updated.IsAuditEnabled ??= now;
        }
        // An empty format removes it; changing it keeps the sequence, so the numbering goes on (ruling N3).
        if (changes.AutoNumberFormat is { } format)
        {
            updated.AutoNumberFormat = format;
            updated.AutoNumberFormat = AutoNumberFormat.OfColumn(entityLogicalName, updated);
        }
        // IsSecured (ruling F7): turned on only where the rule allows it; turned off, the column's permissions and shares go.
        bool? securedChange = null;
        if (changes.IsSecured is { } secured && secured != (current.IsSecured == true))
        {
            if (secured && !FieldSecurability.IsSecurable(entity, current))
                throw XrmdFaultException.NotSecurable($"{entityLogicalName}.{current.LogicalName}");
            updated.IsSecured = secured;
            securedChange = secured;
        }

        // A calculated column: SourceType cannot change, the definition is re-validated (ComputedColumnRules).
        if (changes.SourceType is { } sourceType) updated.SourceType = sourceType;
        if (ComputedColumnRules.FormulaOf(changes) is { } formula) ComputedColumnRules.SetFormula(updated, formula);
        ValidateComputed(pending.Working, entity, updated, current, n => pending.Working.ByLogicalName.GetValueOrDefault(n));

        string? ddl = null;
        List<ColumnDefinition> demoted = [];
        switch (changes)
        {
            case FileColumn { MaxSizeInKB: { } size }:
                ((FileColumn)updated).MaxSizeInKB = CheckedMaxSize(current, size, size, FileColumns.MaxFileSizeInKB);
                break;
            case ImageColumn image:
                var target = (ImageColumn)updated;
                if (image.MaxSizeInKB is { } imageSize)
                    target.MaxSizeInKB = CheckedMaxSize(current, imageSize, imageSize, FileColumns.MaxImageSizeInKB);
                if (image.CanStoreFullImage is { } full) target.CanStoreFullImage = full;
                // IsPrimaryImage = false on a column that is not primary is an accepted no-op: the value is already false.
                if (image.IsPrimaryImage == false && target.IsPrimaryImage == true)
                    throw new XrmdValidationException(
                        $"Attribute '{current.LogicalName}' is the primary image of '{entityLogicalName}': IsPrimaryImage cannot be set to false; make another image column primary instead.");
                if (image.IsPrimaryImage == true && target.IsPrimaryImage != true)
                {
                    target.IsPrimaryImage = true;
                    demoted = (entity.Attributes ?? []).OfType<ImageColumn>()
                        .Where(o => o.LogicalName != current.LogicalName && o.IsPrimaryImage == true).Select(Demote).ToList();
                }
                break;
            case TextColumn { MaxLength: { } length }:
                var s = (TextColumn)updated;
                EnsureGrows(current.LogicalName!, s.MaxLength, length);
                if (length > TypeMapping.MaxNvarcharLength)
                    throw new XrmdValidationException(
                        $"Attribute '{current.LogicalName}': MaxLength {length} is outside 1..{TypeMapping.MaxNvarcharLength}.");
                if (length != s.MaxLength)
                {
                    s.MaxLength = length;
                    if (!ComputedColumnRules.IsComputed(s)) ddl = DdlGenerator.AlterColumn(entityLogicalName, s, session.Dialect); // a computed column has no column
                }
                break;
            case MemoColumn { MaxLength: { } length }:
                var m = (MemoColumn)updated;
                EnsureGrows(current.LogicalName!, m.MaxLength, length);
                m.MaxLength = length; // nvarchar(max) holds any length
                break;
        }
        if (changes is ChoiceColumnBase { OptionSet.Options: { Count: > 0 } options })
            MergeLocalOptions(entity, current.LogicalName!, (ChoiceColumnBase)updated, options, replaceLabels);
        if (_computed is not null && MayAffectDependents(changes))
            DemandDependentsStayValid(pending.Working, entity, updated, demoted, _computed);
        DemandRollupDependentsStayValid(pending.Working, entity, current, updated, changes);

        var stamp = await BumpVersionStampAsync(session, pending, ct);
        await ExecuteAsync(session,
            "UPDATE meta.attribute SET definition = @definition, version_stamp = @stamp WHERE entity_logical_name = @entity AND logical_name = @name", ct,
            ("@definition", StoredAttributeJson(updated, OrdinalOf(pending.Working.ImageOrder, entityLogicalName, updated))), ("@stamp", stamp), ("@entity", entityLogicalName),
            ("@name", updated.LogicalName!));
        foreach (var d in demoted) await StoreAttributeAsync(session, entityLogicalName, d, stamp, ct, pending.Working.ImageOrder);
        if (ddl is not null) await ExecuteAsync(session, ddl, ct);
        if (securedChange is { } nowSecured)
        {
            if (!nowSecured) await DeleteFieldSecurityRowsAsync(session, pending.Working, entityLogicalName, current, ct);
            NotifySecurityChanged(session, entityLogicalName, current.LogicalName!);
        }
        if (updated.AutoNumberFormat is not null) await ExecuteAsync(session, AutoNumberSequences.EnsureSql(updated, session.Dialect), ct);
        else if (current.AutoNumberFormat is not null) await ExecuteAsync(session, AutoNumberSequences.DropSql(current, session.Dialect), ct);

        var entityCopy = MetadataJson.Clone(entity);
        entityCopy.SetAttributes((entity.Attributes ?? []).Select(a => a.LogicalName == updated.LogicalName ? updated
            : demoted.FirstOrDefault(d => d.LogicalName == a.LogicalName) ?? a));
        pending.Working.Put(entityCopy, previous: entity);
        // Any update of a rollup, even one that changes nothing, resets its mass calculation (plan 2.12b, ruling R8).
        if (RollupColumns.IsRollup(updated)) NotifyRollupJobs(session, entityLogicalName, updated.LogicalName!, RollupChange.Updated);
    }

    /// <summary>
    /// True when an UpdateAttribute carrying <paramref name="changes"/> can change what a calculated column reading the column
    /// accepts: its formula or source type, its options, its lookup targets or its date and time behavior. Labels, description,
    /// audit, required level, security and lengths cannot, so those updates translate nothing (a definition elsewhere that cannot
    /// be read then does not block them).
    /// </summary>
    private static bool MayAffectDependents(ColumnDefinition changes) =>
        changes.SourceType is not null || ComputedColumnRules.FormulaOf(changes) is not null
        || changes is ChoiceColumnBase { OptionSet.Options.Count: > 0 }
        || changes is LookupColumn { Targets.Count: > 0 }
        || changes is DateTimeColumn { DateTimeBehavior: not null };

    /// <summary>
    /// Re-translates the calculated columns that read <paramref name="updated"/> (directly or through other calculated columns,
    /// <see cref="ComputedColumnRules.Dependents"/>) as they would be after the update: a change that makes one of them invalid
    /// (a chain longer than the limit, a parent's calculated column that now reads a third table, a cycle, a formula column whose
    /// SQL would be too complex, ...) is refused naming that column (plan 2.12a, ruling K4; final review M2).
    /// </summary>
    /// <exception cref="XrmdValidationException">A dependent calculated column would become invalid.</exception>
    private static void DemandDependentsStayValid(CacheState working, TableDefinition entity, ColumnDefinition updated,
        IReadOnlyList<ColumnDefinition> demoted, IComputedColumns computed)
    {
        var after = MetadataJson.Clone(entity);
        after.SetAttributes((entity.Attributes ?? []).Select(a => a.LogicalName == updated.LogicalName ? updated
            : demoted.FirstOrDefault(d => d.LogicalName == a.LogicalName) ?? a));
        TableDefinition? Find(string name) => name == after.LogicalName ? after : working.ByLogicalName.GetValueOrDefault(name);

        var all = working.ByLogicalName.Values.ToList();
        // The column's old definition, which may be one that cannot be read and is being repaired, is not translated (final review I1).
        var replaced = $"{entity.LogicalName}.{updated.LogicalName}";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(string Entity, string Attribute)>([(entity.LogicalName!, updated.LogicalName!)]);
        while (pending.TryDequeue(out var source))
            foreach (var dependent in ComputedColumnRules.Dependents(source.Entity, source.Attribute, all, computed, replaced))
            {
                if (!seen.Add(dependent)) continue;
                var dot = dependent.IndexOf('.', StringComparison.Ordinal);
                var (table, column) = (dependent[..dot], dependent[(dot + 1)..]);
                var owner = Find(table);
                var attribute = owner?.Attributes?.FirstOrDefault(a => a.LogicalName == column);
                if (owner is null || attribute is null || (owner == after && column == updated.LogicalName)) continue;
                try
                {
                    // The definition-time check too (final review M2): a formula column whose SQL would pass its bounds through
                    // the change is refused now, not on every read.
                    computed.Check(owner, attribute, Find);
                }
                catch (XrmdValidationException ex)
                {
                    // A formula dependent is named as a formula column (as ComputedColumnRules.DeleteRefusal names it).
                    var kind = attribute.SourceType == ComputedColumnRules.Formula ? "formula" : "calculated";
                    throw new XrmdValidationException(
                        $"The change of '{entity.LogicalName}.{updated.LogicalName}' would make the {kind} column '{dependent}' invalid: {ex.Message}", ex);
                }
                pending.Enqueue((table, column));
            }
    }

    public async Task DeleteAttributeAsync(DataSession session, string entityLogicalName, string attributeLogicalName, CancellationToken ct)
    {
        LogicalNameValidator.EnsureValid(entityLogicalName, "entity");
        LogicalNameValidator.EnsureValid(attributeLogicalName, "attribute");

        var pending = await EnterAsync(session, ct);
        var (entity, attribute) = FindAttribute(pending.Working, entityLogicalName, attributeLogicalName);
        EnsureNotIntersect(entity);
        DemandNotRollupCompanion(entity, attribute);
        if (attribute is MoneyColumn { IsBaseCurrency: true })
            throw new XrmdFaultException(ErrorCodes.CannotDeleteBaseMoneyCalculationAttribute,
                $"Attribute '{attribute.LogicalName}' of entity '{entity.LogicalName}' holds the base currency value of money attribute " +
                $"'{attribute.LogicalName![..^TypeMapping.BaseColumnSuffix.Length]}' and cannot be deleted on its own: delete the money attribute.", 400);
        if (IsSystemAttribute(entity, attribute))
            throw new XrmdFaultException(ErrorCodes.PrivilegeDenied,
                $"Attribute '{attribute.LogicalName}' of entity '{entity.LogicalName}' is a system attribute and cannot be deleted.", 403);
        // A column named by a duplicate rule condition stays (plan 2.15, ruling D6).
        await DemandNoDuplicateConditionAsync(session, pending.Working, entityLogicalName, attributeLogicalName, ct);
        // A column a calculated column reads stays (plan 2.12a, ruling K4).
        if (_computed is not null)
            DemandNoCalculatedDependents(ComputedColumnRules.Dependents(entityLogicalName, attributeLogicalName, pending.Working.ByLogicalName.Values, _computed),
                pending.Working.ByLogicalName.Values);
        // A column a rollup column reads stays (plan 2.12b, ruling R13).
        DemandNoRollupDependents(pending.Working, entityLogicalName, attributeLogicalName);

        // The attributes stored in its columns (money _base, AttributeOf) go with it.
        var removed = (entity.Attributes ?? [])
            .Where(a => a.LogicalName == attribute.LogicalName || a.AttributeOf == attribute.LogicalName
                        || (attribute is MoneyColumn && a is MoneyColumn { IsBaseCurrency: true }
                            && a.LogicalName == attribute.LogicalName + TypeMapping.BaseColumnSuffix)
                        // A lookup to an elastic table takes its partition column with it (plan 3.4).
                        || (attribute is LookupColumn { Targets: [var target] }
                            && pending.Working.ByLogicalName.GetValueOrDefault(target) is { } referenced && ElasticTables.IsElastic(referenced)
                            && a is TextColumn && a.LogicalName == ElasticTables.PartitionColumnOf(attribute.LogicalName!)))
            .Select(a => a.LogicalName!)
            .ToHashSet(StringComparer.Ordinal);
        // What goes with it (a rollup's _date and _state, a money column's _base, ...) is checked like the column itself.
        DemandNoDependentsOfCompanions(pending.Working, entityLogicalName, attributeLogicalName, removed);
        var ddl = GenerateDdl(() => string.Join("\n",
            (entity.Attributes ?? []).Where(a => a.LogicalName != attribute.LogicalName && removed.Contains(a.LogicalName!) && TypeMapping.ToColumns(a).Count > 0)
                .Select(a => DdlGenerator.DropColumn(entityLogicalName, a, session.Dialect))
                .Prepend(ComputedColumnRules.IsComputed(attribute) ? "" : DdlGenerator.DropColumn(entityLogicalName, attribute, session.Dialect))
                .Where(statement => statement.Length > 0)));

        var stamp = await BumpVersionStampAsync(session, pending, ct);
        foreach (var name in removed)
            await ExecuteAsync(session, "DELETE FROM meta.attribute WHERE entity_logical_name = @entity AND logical_name = @name", ct,
                ("@entity", entityLogicalName), ("@name", name!));
        await DeleteSavedOneToManyAsync(session, pending.Working,
            r => r.ReferencingEntity == entityLogicalName && removed.Contains(r.ReferencingAttribute!), ct);
        foreach (var handler in attributeDeletedHandlers ?? [])
            await handler.OnAttributeDeletingAsync(SdkViewCache.Of(entity), SdkViewCache.Of(entity, attribute), session, ct);
        if (!string.IsNullOrWhiteSpace(ddl)) await ExecuteAsync(session, ddl, ct); // a calculated column has no column to drop
        if (attribute.AutoNumberFormat is not null) await ExecuteAsync(session, AutoNumberSequences.DropSql(attribute, session.Dialect), ct);

        // Deleting the primary image makes the oldest remaining image column primary (ruling F5).
        var remaining = (entity.Attributes ?? []).Where(a => !removed.Contains(a.LogicalName!)).ToList();
        if (attribute is ImageColumn { IsPrimaryImage: true } && remaining.OfType<ImageColumn>().OrderBy(i => OrdinalOf(pending.Working.ImageOrder, entityLogicalName, i) ?? int.MaxValue).FirstOrDefault() is { } oldest)
        {
            var promoted = MetadataJson.Clone(oldest);
            promoted.IsPrimaryImage = true;
            await StoreAttributeAsync(session, entityLogicalName, promoted, stamp, ct, pending.Working.ImageOrder);
            remaining[remaining.FindIndex(a => a.LogicalName == oldest.LogicalName)] = promoted;
        }

        var entityCopy = MetadataJson.Clone(entity);
        entityCopy.SetAttributes(remaining);
        pending.Working.Put(entityCopy, previous: entity);
        ForgetAttributes(pending.Working, entityLogicalName, removed);
        foreach (var gone in removed) pending.Working.ImageOrder.Remove((entityLogicalName, gone!));
        if (FileColumns.IsFileOrImage(attribute) && !remaining.Any(FileColumns.IsFileOrImage))
            await UnlinkFileAttachmentsAsync(session, pending, entityLogicalName, stamp, ct);
        // Its permissions and field shares go with it (final review I2): a column created later with the same name starts with none.
        await DeleteFieldSecurityRowsAsync(session, pending.Working, entityLogicalName, attribute, ct);
        // A secured column that goes away leaves the secured-column lookups (field permissions) after the commit.
        if (attribute.IsSecured == true) NotifySecurityChanged(session, entityLogicalName, attribute.LogicalName!);
        if (RollupColumns.IsRollup(attribute)) NotifyRollupJobs(session, entityLogicalName, attribute.LogicalName!, RollupChange.Deleted);
    }

    /// <exception cref="XrmdFaultException">400 <c>0x80040203</c> naming the calculated and formula columns that read the component.</exception>
    private static void DemandNoCalculatedDependents(IReadOnlyList<string> dependents, IEnumerable<TableDefinition> all)
    {
        if (ComputedColumnRules.DeleteRefusal(dependents, all) is { } refusal)
            throw new XrmdFaultException(ErrorCodes.InvalidArgument, refusal, 400);
    }

    public async Task BeginWriteAsync(DataSession session, CancellationToken ct)
    {
        var pending = await EnterAsync(session, ct);
        await BumpVersionStampAsync(session, pending, ct);
    }

    public async Task DeleteEntityAsync(DataSession session, string entityLogicalName, CancellationToken ct)
    {
        LogicalNameValidator.EnsureValid(entityLogicalName, "entity");

        var pending = await EnterAsync(session, ct);
        var entity = pending.Working.ByLogicalName.GetValueOrDefault(entityLogicalName)
                     ?? throw new XrmdValidationException($"Entity '{entityLogicalName}' does not exist.");
        // A system entity is the 403 first, even when a seeded many-to-many relationship (queuemembership_association) names it.
        if (!IsSystemEntity(entity) && ManyToManyOf(pending.Working, entity.LogicalName!) is { } relationship)
            throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                $"The component cannot be deleted because it is referenced by relationship '{relationship.SchemaName}'.", 400);
        await DeleteEntityCoreAsync(session, pending, entity, ct);
    }

    /// <summary>Deletes <paramref name="entity"/> (see <see cref="DeleteEntityAsync"/>) once the relationships allow it.</summary>
    private async Task DeleteEntityCoreAsync(DataSession session, PendingChanges pending, TableDefinition entity, CancellationToken ct)
    {
        if (IsSystemEntity(entity))
            throw new XrmdFaultException(ErrorCodes.PrivilegeDenied,
                $"Entity '{entity.LogicalName}' is a system entity and cannot be deleted.", 403);
        var referencing = pending.Working.ByLogicalName.Values
            .Where(e => e.LogicalName != entity.LogicalName)
            .SelectMany(e => (e.Attributes ?? []).OfType<LookupColumn>()
                .Where(l => (l.Targets ?? []).Contains(entity.LogicalName, StringComparer.Ordinal)
                            && !(e.LogicalName == FileColumns.AttachmentEntity && l.LogicalName == FileColumns.AttachmentLookup)
                            && !(e.LogicalName == AccessTeamSchema.Team && l.LogicalName == AccessTeamSchema.RegardingObjectId)
                            && !ActivitySchema.IsManagedLookup(e.LogicalName, l.LogicalName)
                            && !DuplicateDetectionSchema.IsRecordLookup(l))
                .Select(l => $"{e.LogicalName}.{l.LogicalName}"))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (referencing is not null)
            throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                $"The component cannot be deleted because it is referenced by attribute '{referencing}'.", 400);
        // A calculated column of another table that reads this one stays (plan 2.12a, ruling K4).
        if (_computed is not null)
            DemandNoCalculatedDependents(ComputedColumnRules.DependentsOfTable(entity, pending.Working.ByLogicalName.Values, _computed),
                pending.Working.ByLogicalName.Values);
        // A rollup column of another table that reads this one stays (plan 2.12b, ruling R13).
        DemandNoRollupDependentsOfTable(pending.Working, entity.LogicalName!);
        var ddl = DdlGenerator.DropTable(entity.LogicalName!, session.Dialect);
        var regarding = await PrepareRegardingUnlinkAsync(session, pending.Working, entity, ct);
        var activityLinks = await PrepareActivityUnlinkAsync(session, pending.Working, entity, ct);

        // meta.* first, then the handlers' data.* rows, then the table.
        var stamp = await BumpVersionStampAsync(session, pending, ct);
        await UnlinkFileAttachmentsAsync(session, pending, entity.LogicalName!, stamp, ct);
        if (regarding) await RemoveRegardingTargetAsync(session, pending.Working, entity.LogicalName!, stamp, ct);
        await RemoveDuplicateRecordTargetAsync(session, pending.Working, entity.LogicalName!, stamp, ct);
        await ExecuteAsync(session, "DELETE FROM meta.attribute WHERE entity_logical_name = @name", ct, ("@name", entity.LogicalName!));
        await ExecuteAsync(session, "DELETE FROM meta.entity WHERE logical_name = @name", ct, ("@name", entity.LogicalName!));
        // Its change tracking state and deletion log go with it (plan 2.13, ruling C14).
        await ExecuteAsync(session, """
            DELETE FROM meta.deleted_record WHERE entity_logical_name = @name;
            DELETE FROM meta.change_tracking_table WHERE entity_logical_name = @name;
            """, ct, ("@name", entity.LogicalName!));
        await DeleteSavedOneToManyAsync(session, pending.Working,
            r => r.ReferencingEntity == entity.LogicalName || r.ReferencedEntity == entity.LogicalName, ct);
        foreach (var handler in deletedHandlers ?? [])
            await handler.OnEntityDeletingAsync(SdkViewCache.Of(entity), session, ct);
        if (ActivitySchema.IsActivityTable(entity)) await DeleteActivityRowsAsync(session, pending.Working, entity, ct);
        await DeletePostRegardingRowsAsync(session, pending.Working, entity, ct);
        // Its duplicate rules and the duplicate records of its rows go with it (plan 2.15, ruling D6).
        await DeleteDuplicateDetectionRowsAsync(session, pending.Working, entity, ct);
        await ExecuteAsync(session, ddl, ct);
        // The field permissions on its columns and the field shares of its records go with it (final review I2).
        await DeleteTableFieldSecurityRowsAsync(session, pending.Working, entity.LogicalName!, ct);
        if ((entity.Attributes ?? []).FirstOrDefault(a => a.IsSecured == true) is { } secured)
            NotifySecurityChanged(session, entity.LogicalName!, secured.LogicalName!);
        foreach (var a in (entity.Attributes ?? []).Where(a => a.AutoNumberFormat is not null))
            await ExecuteAsync(session, AutoNumberSequences.DropSql(a, session.Dialect), ct);

        pending.Working.Remove(entity);
        ForgetEntity(pending.Working, entity.LogicalName!);
        foreach (var key in pending.Working.ImageOrder.Keys.Where(k => k.Item1 == entity.LogicalName).ToList()) pending.Working.ImageOrder.Remove(key);
        if (activityLinks) await SyncActivityLinksAsync(session, pending, ct);
        foreach (var rollup in (entity.Attributes ?? []).Where(RollupColumns.IsRollup))
            NotifyRollupJobs(session, entity.LogicalName!, rollup.LogicalName!, RollupChange.Deleted);
    }

    /// <summary>The entity and its attribute in <paramref name="cache"/>.</summary>
    /// <exception cref="XrmdValidationException">Either does not exist.</exception>
    private static (TableDefinition Entity, ColumnDefinition Attribute) FindAttribute(CacheState cache, string entityLogicalName,
        string attributeLogicalName)
    {
        var entity = cache.ByLogicalName.GetValueOrDefault(entityLogicalName)
                     ?? throw new XrmdValidationException($"Entity '{entityLogicalName}' does not exist.");
        var attribute = (entity.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attributeLogicalName)
                        ?? throw new XrmdValidationException($"Attribute '{attributeLogicalName}' does not exist on entity '{entityLogicalName}'.");
        return (entity, attribute);
    }

    /// <summary>
    /// The attributes of an intersect entity are its relationship's (written with it, deleted with it): no attribute is added
    /// to, changed on or deleted from it.
    /// </summary>
    /// <exception cref="XrmdValidationException">400 naming the entity.</exception>
    private static void EnsureNotIntersect(TableDefinition entity)
    {
        if (entity.IsIntersect == true)
            throw new XrmdValidationException(
                $"Entity '{entity.LogicalName}' is an intersect entity: its attributes belong to its many-to-many relationship and cannot be added, changed or deleted.");
    }

    /// <summary>
    /// Every target of a lookup of <paramref name="attributes"/> is an entity of <paramref name="cache"/> (the session's view)
    /// or <paramref name="creating"/> (the entity being created, for a self-referential lookup), and not an intersect entity
    /// (as CreateOneToMany requires of its sides).
    /// </summary>
    /// <exception cref="XrmdValidationException">400 naming the attribute and the target.</exception>
    private static void EnsureLookupTargets(CacheState cache, string entityLogicalName, IEnumerable<ColumnDefinition> attributes, string? creating)
    {
        foreach (var lookup in attributes.OfType<LookupColumn>())
        {
            foreach (var target in lookup.Targets ?? [])
            {
                if (target == creating) continue;
                var entity = cache.ByLogicalName.GetValueOrDefault(target)
                             ?? throw new XrmdValidationException(
                                 $"Lookup attribute '{entityLogicalName}.{lookup.LogicalName}' targets entity '{target}', which does not exist.");
                if (entity.IsIntersect == true)
                    throw new XrmdValidationException(
                        $"Lookup attribute '{entityLogicalName}.{lookup.LogicalName}' targets entity '{target}', an intersect entity, which a lookup cannot target.");
            }
        }
    }

    /// <summary>Entities of the system (seeded) have object type codes below the custom range.</summary>
    public static bool IsSystemEntity(TableDefinition e) => e.ObjectTypeCode is < FirstCustomObjectTypeCode;

    /// <summary>
    /// Attributes the server owns: primary key and name, generated attributes (system columns, money <c>_base</c>,
    /// <c>AttributeOf</c>, the currency pair) and the attributes of a system entity's seed definition.
    /// </summary>
    public static bool IsSystemAttribute(TableDefinition e, ColumnDefinition a) =>
        SystemAttributes.IsReservedName(e.LogicalName!, a.LogicalName)
        || a.LogicalName == e.PrimaryIdAttribute || a.LogicalName == e.PrimaryNameAttribute
        || TypeMapping.IsStoredByOtherAttribute(a) || a.AttributeOf is not null
        || CurrencySchema.IsPairAttribute(e, a)
        || (ActivitySchema.IsActivityTable(e) && ActivitySchema.IsGeneratedActivityAttribute(a.LogicalName))
        || (IsSystemEntity(e) && SystemSeed.Defines(e.LogicalName!, a.LogicalName!));

    /// <summary>
    /// The Dataverse <c>IsCustomAttribute</c> of an attribute of the cache (which does not keep it): false for the primary
    /// key and the system columns xrmverse generates, for the currency pair, for the attributes of a system entity's seed definition, and for the
    /// <c>_base</c> of a system money attribute; true for every other (an attribute a caller or an import created, and the
    /// <c>_base</c> of such a money attribute, as Dataverse marks it).
    /// </summary>
    public static bool IsCustomAttribute(TableDefinition e, ColumnDefinition a)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(a);
        if (TypeMapping.IsStoredByOtherAttribute(a) && a.LogicalName!.EndsWith(TypeMapping.BaseColumnSuffix, StringComparison.Ordinal)
            && (e.Attributes ?? []).FirstOrDefault(m => m.LogicalName == a.LogicalName[..^TypeMapping.BaseColumnSuffix.Length]) is { } money)
            return IsCustomAttribute(e, money);
        return !SystemAttributes.IsReservedName(e.LogicalName!, a.LogicalName)
               && a.LogicalName != e.PrimaryIdAttribute
               && !CurrencySchema.IsPairAttribute(e, a)
               && !(ActivitySchema.IsActivityTable(e) && ActivitySchema.IsGeneratedActivityAttribute(a.LogicalName))
               && !(IsSystemEntity(e) && SystemSeed.Defines(e.LogicalName!, a.LogicalName!));
    }

    private static bool HasText(LabelSet? label) => label is not null && (label.LocalizedLabels is { Count: > 0 } || label.UserLocalizedLabel is not null);

    /// <summary>
    /// A copy of a label an update applies, with its <c>UserLocalizedLabel</c> set when the caller gave only localized labels (a
    /// Web API body usually has none): the 1033 label, else the first one. A read then finds the label as Dataverse returns it.
    /// </summary>
    private static LabelSet UpdatedLabel(LabelSet label)
    {
        var copy = MetadataJson.Clone(label);
        if (copy.UserLocalizedLabel is null && copy.LocalizedLabels is { Count: > 0 })
            copy.UserLocalizedLabel = copy.LocalizedLabels.FirstOrDefault(l => l.LanguageCode == 1033) ?? copy.LocalizedLabels[0];
        return copy;
    }

    /// <summary>Whether a label of an update is applied: one with text, or with <paramref name="replace"/> any given one (an empty one clears).</summary>
    private static bool Applies(LabelSet? label, bool replace) => HasText(label) || (replace && label is not null);

    private static void EnsureGrows(string attributeLogicalName, int? current, int requested)
    {
        if (current is { } length && requested < length)
            throw new XrmdValidationException(
                $"Attribute '{attributeLogicalName}': MaxLength can only grow (it is {length}, {requested} was requested).");
    }

    /// <summary>
    /// Adds the options of <paramref name="changes"/> whose value is new to the local option set of <paramref name="target"/>
    /// (Picklist, MultiSelect) and relabels the others; none is removed.
    /// </summary>
    /// <remarks>
    /// A picklist's options are added or relabelled. A <c>statecode</c>'s are only relabelled (Dataverse has a fixed set of
    /// states). A <c>statuscode</c> gets new status reasons, each of a state of the entity's <c>statecode</c>.
    /// </remarks>
    private static void MergeLocalOptions(TableDefinition entity, string attributeLogicalName, ChoiceColumnBase target,
        List<ChoiceOption> changes, bool replaceLabels = false)
    {
        switch (target)
        {
            case ChoiceColumn or MultiChoiceColumn:
                break;
            case StateColumn:
                if (changes.FirstOrDefault(o => target.OptionSet?.Options?.Any(e => e.Value == o.Value) != true) is { } state)
                    throw new XrmdValidationException(
                        $"Attribute '{attributeLogicalName}': the state {state.Value} cannot be added (the states of a table are fixed).");
                break;
            case StatusColumn:
                var states = (entity.Attributes ?? []).OfType<StateColumn>().SingleOrDefault()?.OptionSet?.Options?
                    .Select(o => o.Value).ToHashSet() ?? [];
                foreach (var option in changes.Where(o => target.OptionSet?.Options?.Any(e => e.Value == o.Value) != true))
                {
                    if (option is not StatusOption { State: { } of } || !states.Contains(of))
                        throw new XrmdValidationException(
                            $"Attribute '{attributeLogicalName}': the status reason {option.Value} needs the state of an existing statecode option.");
                }
                break;
            default:
                throw new XrmdValidationException($"The options of attribute '{attributeLogicalName}' cannot be changed.");
        }
        if (target.OptionSet is not { IsGlobal: not true } optionSet)
            throw new XrmdValidationException(
                $"Attribute '{attributeLogicalName}' uses a global option set: its options are changed on the option set.");
        MergeOptions($"Attribute '{attributeLogicalName}'", optionSet, changes, replaceLabels);
    }

    /// <summary>Adds the options of <paramref name="changes"/> whose value is new to <paramref name="target"/> and relabels
    /// the others; none is removed.</summary>
    /// <param name="owner">Names the attribute or option set in error messages.</param>
    /// <param name="replaceLabels">
    /// The labels and the colour of each option given replace the current ones; an empty or missing one clears it. Otherwise a
    /// colour given replaces the current one and a missing one leaves it.
    /// </param>
    /// <exception cref="XrmdValidationException">An option has a colour that is not <c>#RRGGBB</c>.</exception>
    private static void MergeOptions(string owner, ChoiceSet target, IEnumerable<ChoiceOption> changes, bool replaceLabels = false)
    {
        foreach (var option in changes)
        {
            if (option.Value is not { } value)
                throw new XrmdValidationException($"{owner}: every option needs a value.");
            OptionColor.Demand(owner, option);
            var existing = target.Options!.FirstOrDefault(o => o.Value == value);
            if (existing is null)
            {
                target.Options!.Add(MetadataJson.Clone(option));
                continue;
            }
            // Replacing, an option given without a label clears its label: the option given is the whole option.
            if (replaceLabels) existing.Label = option.Label is null ? new LabelSet() : MetadataJson.Clone(option.Label);
            else if (HasText(option.Label)) existing.Label = MetadataJson.Clone(option.Label!);
            if (replaceLabels) existing.Description = option.Description is null ? new LabelSet() : MetadataJson.Clone(option.Description);
            else if (HasText(option.Description)) existing.Description = MetadataJson.Clone(option.Description!);
            if (replaceLabels) existing.Color = string.IsNullOrEmpty(option.Color) ? null : option.Color;
            else if (!string.IsNullOrEmpty(option.Color)) existing.Color = option.Color;
        }
    }

    /// <summary>Fills server defaults and rejects definitions whose values the column could not hold.</summary>
    /// <param name="seed">
    /// A column of a seed definition created with its table (<see cref="CreateSeedEntityAsync"/>): its decimal Precision may go up
    /// to <see cref="TypeMapping.SeedMaxDecimalPrecision"/> (12, <c>transactioncurrency.exchangerate</c>; pre-flight C14). This is
    /// the only place the public cap <see cref="TypeMapping.MaxDecimalPrecision"/> (10) is bypassed: CreateEntity, CreateAttribute,
    /// UpdateAttribute, solution import and the seed's later addition of a column to an existing table all keep it.
    /// </param>
    private static void Prepare(string entityLogicalName, ColumnDefinition a, string? primaryId = null, bool seed = false)
    {
        var maxDecimalPrecision = seed ? TypeMapping.SeedMaxDecimalPrecision : TypeMapping.MaxDecimalPrecision;
        // Only a Text string column takes a format; an empty one is none (autonumber plan, ruling N1).
        a.AutoNumberFormat = AutoNumberFormat.OfColumn(entityLogicalName, a);
        switch (a)
        {
            case TextColumn when entityLogicalName == ActivitySchema.Annotation && a.LogicalName == ActivitySchema.DocumentBody:
                // The note body has no column (plan 2.11 A16): its MaxLength is Dataverse's, not a column length.
                break;
            case TextColumn s:
                s.MaxLength ??= TypeMapping.DefaultStringLength;
                if (s.MaxLength is <= 0 or > TypeMapping.MaxNvarcharLength)
                    throw new XrmdValidationException(
                        $"Attribute '{a.LogicalName}': MaxLength {s.MaxLength} is outside 1..{TypeMapping.MaxNvarcharLength}.");
                break;
            case DecimalColumn d when d.Precision < TypeMapping.MinDecimalPrecision || d.Precision > maxDecimalPrecision:
                throw new XrmdValidationException(
                    $"Attribute '{a.LogicalName}': Precision {d.Precision} is outside {TypeMapping.MinDecimalPrecision}..{maxDecimalPrecision}.");
            case LookupColumn when entityLogicalName == FileColumns.AttachmentEntity && a.LogicalName == FileColumns.AttachmentLookup:
                // The one lookup that starts without targets: the file and image columns add their tables (LinkFileAttachmentsAsync).
                break;
            case LookupColumn when entityLogicalName == AuditTable.Name
                                              && a.LogicalName is AuditTable.ObjectId or AuditTable.RegardingObjectId:
                // The audit table's polymorphic lookups name the record of any table: no fixed targets.
                break;
            case LookupColumn when entityLogicalName == FieldSecurityTables.Share && a.LogicalName == FieldSecurityTables.ShareObjectId:
                // A field share names the record of any table: no fixed targets.
                break;
            case LookupColumn when ActivitySchema.IsManagedLookup(entityLogicalName, a.LogicalName):
                // Plan 2.11 (pre-flight C1): regardingobjectid and annotation.objectid start without targets; the metadata store
                // maintains the targets of these lookups.
                break;
            case LookupColumn when entityLogicalName == AccessTeamSchema.Team && a.LogicalName == AccessTeamSchema.RegardingObjectId:
                // Starts without targets: each table with AutoCreateAccessTeams adds itself (AddRegardingTargetAsync).
                break;
            case LookupColumn when entityLogicalName == DuplicateDetectionSchema.Record
                                              && a.LogicalName is DuplicateDetectionSchema.BaseRecordId or DuplicateDetectionSchema.DuplicateRecordId:
                // Start without targets: each table with IsDuplicateDetectionEnabled adds itself (AddDuplicateRecordTargetAsync).
                break;
            case FileColumn file:
                file.MaxSizeInKB = CheckedMaxSize(a, file.MaxSizeInKB, FileColumns.DefaultFileMaxSizeInKB, FileColumns.MaxFileSizeInKB);
                break;
            case ImageColumn image:
                image.MaxSizeInKB = CheckedMaxSize(a, image.MaxSizeInKB, FileColumns.DefaultImageMaxSizeInKB, FileColumns.MaxImageSizeInKB);
                image.CanStoreFullImage ??= false;
                foreach (var (side, value) in (ReadOnlySpan<(string, short?)>)[(nameof(ImageColumn.MaxHeight), image.MaxHeight), (nameof(ImageColumn.MaxWidth), image.MaxWidth)])
                {
                    if (value is { } given && given != FileColumns.ThumbnailSize)
                        throw new XrmdValidationException(
                            $"Attribute '{a.LogicalName}': {side} is {given} but thumbnails are always {FileColumns.ThumbnailSize}x{FileColumns.ThumbnailSize} (read-only).");
                }
                image.MaxHeight = image.MaxWidth = (short)FileColumns.ThumbnailSize;
                break;
            case LookupColumn { AttributeType: ColumnTypeCode.Customer } customer:
                if (!CustomerColumns.AreCustomerTargets(customer.Targets))
                    throw new XrmdValidationException(CustomerColumns.TargetsMessage($"Attribute '{entityLogicalName}.{a.LogicalName}'"));
                break;
            case LookupColumn { AttributeType: ColumnTypeCode.Lookup } lookup:
                // More than one target: a polymorphic lookup, with a <name>type column (TypeMapping.IsPolymorphicLookup).
                if (lookup.Targets is not { Count: > 0 } targets)
                    throw new XrmdValidationException($"Lookup attribute '{a.LogicalName}' must have at least one target.");
                if (targets.FirstOrDefault(t => !LogicalNameValidator.IsValid(t)) is { } invalid)
                    throw new XrmdValidationException($"Lookup attribute '{a.LogicalName}': invalid target '{invalid}'.");
                if (targets.Distinct(StringComparer.Ordinal).Count() != targets.Count)
                    throw new XrmdValidationException($"Lookup attribute '{a.LogicalName}' names a target more than once.");
                break;
        }

        if (a is ChoiceColumnBase { OptionSet.Options: { } options })
            foreach (var option in options) OptionColor.Demand($"Attribute '{entityLogicalName}.{a.LogicalName}'", option);
        a.MetadataId ??= Guid.NewGuid();
        a.SchemaName ??= a.LogicalName;
        a.EntityLogicalName = entityLogicalName;
        a.IsAuditEnabled = AppliedAudit(AuditDefaults.ForAttribute(a, primaryId), a.IsAuditEnabled?.Value);
        // Unsecured unless asked (the securable rule is checked with the table, EnsureSecurable); a generated companion never is.
        a.IsSecured = a.AttributeOf is null && !TypeMapping.IsStoredByOtherAttribute(a) && a.IsSecured == true;
        ClearComputedSecurability(a);
        // SourceTypeMask is the server's (ComputedColumnRules sets it for a calculated column).
        if (ComputedColumnRules.MaskOf(a) is not null) a.GetType().GetProperty("SourceTypeMask")!.SetValue(a, null);
    }

    /// <summary>
    /// Drops <c>CanBeSecuredForCreate/Read/Update</c>: they are computed by every read from <see cref="FieldSecurability"/>, so a
    /// value given by a caller or kept in an old definition is never stored or used.
    /// </summary>
    private static void ClearComputedSecurability(ColumnDefinition a)
    {
        a.CanBeSecuredForCreate = null;
        a.CanBeSecuredForRead = null;
        a.CanBeSecuredForUpdate = null;
    }

    /// <summary>Every column of <paramref name="attributes"/> that asks for <c>IsSecured</c> can be secured on <paramref name="entity"/>.</summary>
    /// <exception cref="XrmdFaultException">400 <c>0x8004F501</c> naming the column.</exception>
    private static void EnsureSecurable(TableDefinition entity, IEnumerable<ColumnDefinition> attributes)
    {
        foreach (var a in attributes)
            if (a.IsSecured == true && !FieldSecurability.IsSecurable(entity, a))
                throw XrmdFaultException.NotSecurable($"{entity.LogicalName}.{a.LogicalName}");
    }

    /// <summary>
    /// Tells the <see cref="IAttributeSecurityChangedHandler"/>s, once <paramref name="session"/> commits, that the
    /// <c>IsSecured</c> of <paramref name="entity"/>.<paramref name="attribute"/> changed (after the cache is published).
    /// </summary>
    private void NotifySecurityChanged(DataSession session, string entity, string attribute)
    {
        foreach (var handler in _securityChangedHandlers)
            session.OnCommitted(() => handler.OnAttributeSecurityChanged(entity, attribute));
    }

    /// <summary>
    /// The rows that give access to an unsecured column, deleted in the transaction that unsecures it: its permissions in every
    /// profile (<c>fieldpermission</c> by table and column name) and its field shares (<c>principalobjectattributeaccess</c> by
    /// the column's MetadataId and the table name). Direct deletes, as for the other rows that hang from a change.
    /// </summary>
    private static async Task DeleteFieldSecurityRowsAsync(DataSession session, CacheState cache, string entity, ColumnDefinition attribute,
        CancellationToken ct)
    {
        if (cache.ByLogicalName.ContainsKey(FieldSecurityTables.Permission))
            await ExecuteAsync(session,
                $"DELETE FROM {session.DataTable(FieldSecurityTables.Permission)} WHERE {session.Quote(FieldSecurityTables.EntityName)} = @entity AND {session.Quote(FieldSecurityTables.AttributeLogicalName)} = @name",
                ct, ("@entity", entity), ("@name", attribute.LogicalName!));
        if (cache.ByLogicalName.ContainsKey(FieldSecurityTables.Share) && attribute.MetadataId is { } id)
            await ExecuteAsync(session,
                $"DELETE FROM {session.DataTable(FieldSecurityTables.Share)} WHERE {session.Quote(FieldSecurityTables.ShareAttributeId)} = @id AND {session.Quote(FieldSecurityTables.ShareObjectTypeCodeColumn)} = @entity",
                ct, ("@id", id), ("@entity", entity));
    }

    /// <summary>
    /// The rows that give access to the columns of a deleted table: every <c>fieldpermission</c> naming the table and every
    /// field share (<c>principalobjectattributeaccess</c>) of its records, deleted in the transaction that deletes it.
    /// </summary>
    private static async Task DeleteTableFieldSecurityRowsAsync(DataSession session, CacheState cache, string entity, CancellationToken ct)
    {
        if (cache.ByLogicalName.ContainsKey(FieldSecurityTables.Permission))
            await ExecuteAsync(session,
                $"DELETE FROM {session.DataTable(FieldSecurityTables.Permission)} WHERE {session.Quote(FieldSecurityTables.EntityName)} = @entity",
                ct, ("@entity", entity));
        if (cache.ByLogicalName.ContainsKey(FieldSecurityTables.Share))
            await ExecuteAsync(session,
                $"DELETE FROM {session.DataTable(FieldSecurityTables.Share)} WHERE {session.Quote(FieldSecurityTables.ShareObjectTypeCodeColumn)} = @entity",
                ct, ("@entity", entity));
    }

    /// <summary>
    /// The setting a column gets: the value asked for when the column can change it, else the fixed value of
    /// <paramref name="rule"/> (whether it can change is the rule's, never the request's).
    /// </summary>
    private static ManagedFlag AppliedAudit(ManagedFlag rule, bool? asked) =>
        rule.CanBeChanged ? AuditDefaults.ManagedFlag(asked ?? rule.Value) : rule;

    /// <summary><paramref name="value"/> or its default; 400 outside 1..<paramref name="max"/>.</summary>
    private static int CheckedMaxSize(ColumnDefinition a, int? value, int defaultValue, int max)
    {
        var size = value ?? defaultValue;
        if (size < FileColumns.MinSizeInKB || size > max)
            throw new XrmdValidationException($"Attribute '{a.LogicalName}': MaxSizeInKB {size} is outside {FileColumns.MinSizeInKB}..{max}.");
        return size;
    }

    /// <summary>Rejects attributes that would replace server-generated ones (system attributes, money <c>_base</c>, AttributeOf).</summary>
    private static void EnsureDefinableByCaller(string entityLogicalName, ColumnDefinition a)
    {
        if (SystemAttributes.IsReservedName(entityLogicalName, a.LogicalName))
            throw new XrmdValidationException(
                $"Attribute '{a.LogicalName}' is a system attribute of entity '{entityLogicalName}' and cannot be defined.");
        if (a.AttributeOf is not null || a is MoneyColumn { IsBaseCurrency: true })
            throw new XrmdValidationException(
                $"Attribute '{a.LogicalName}' cannot set AttributeOf or IsBaseCurrency: such attributes are generated by the server.");
    }

    /// <summary>
    /// EntityName and PartyList columns exist only on the seeded tables of the activity model (plan 2.11 A7, A8): as in
    /// Dataverse, CreateEntity, CreateAttribute and solution import cannot create one.
    /// </summary>
    /// <exception cref="XrmdValidationException">400 naming the column and its type.</exception>
    private static void EnsureNotSeedOnlyType(string entityLogicalName, ColumnDefinition a)
    {
        if (a is EntityNameColumn || ActivitySchema.IsPartyList(a))
            throw new XrmdValidationException(
                $"Attribute '{entityLogicalName}.{a.LogicalName}' has type {(a is EntityNameColumn ? "EntityName" : "PartyList")}, which " +
                "only system tables have: such a column cannot be created.");
    }

    /// <summary>The currency pair is generated with the first money column of a table (ruling X4): a caller cannot define it.</summary>
    private static void EnsureNotCurrencyPair(string entityLogicalName, ColumnDefinition a)
    {
        if (CurrencySchema.IsPairName(a.LogicalName) && entityLogicalName != CurrencySchema.Table)
            throw new XrmdValidationException(
                $"Attribute '{a.LogicalName}' of entity '{entityLogicalName}' is generated by the server with the first money column of the table and cannot be defined.");
    }

    /// <summary>A Customer column is created only by <see cref="CreateCustomerRelationshipsAsync"/>, with its relationships.</summary>
    /// <exception cref="XrmdValidationException">400 naming <c>CreateCustomerRelationships</c>.</exception>
    private static void EnsureNotCustomer(string entityLogicalName, ColumnDefinition a)
    {
        if (CustomerColumns.IsCustomer(a))
            throw new XrmdValidationException(CustomerColumns.OnlyViaMessage($"Attribute '{entityLogicalName}.{a.LogicalName}'"));
    }

    /// <summary>The name <c>{money}_base</c> belongs to the generated base-currency companion of a money attribute.</summary>
    private static void EnsureNoBaseCompanionClash(string entityLogicalName, IEnumerable<ColumnDefinition> attributes)
    {
        var list = attributes.ToList();
        foreach (var money in list.OfType<MoneyColumn>().Where(m => m.IsBaseCurrency != true))
        {
            var companion = money.LogicalName + TypeMapping.BaseColumnSuffix;
            if (list.Any(x => x.LogicalName == companion && x is not MoneyColumn { IsBaseCurrency: true }))
                throw new XrmdValidationException(
                    $"Attribute '{companion}' on entity '{entityLogicalName}' clashes with the base-currency companion of money attribute '{money.LogicalName}'.");
        }
    }

    private static void EnsureDistinct(string entityLogicalName, IEnumerable<ColumnDefinition> attributes)
    {
        var duplicate = attributes.GroupBy(a => a.LogicalName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new MetadataComponentExistsException($"Attribute '{duplicate.Key}' already exists on entity '{entityLogicalName}'.");
    }

    private static string GenerateDdl(Func<string> generate)
    {
        try
        {
            return generate();
        }
        catch (NotSupportedException ex)
        {
            throw new XrmdValidationException(ex.Message, ex);
        }
    }

    private static string SerializeWithoutAttributes(TableDefinition e)
    {
        var node = JsonSerializer.SerializeToNode(e, MetadataJson.Options)!.AsObject();
        node.Remove(nameof(TableDefinition.Attributes));
        node.Remove(nameof(TableDefinition.CanChangeTrackingBeEnabled)); // computed by every read (ChangeTrackingSchema), never stored
        return node.ToJsonString(MetadataJson.Options);
    }

    private static async Task InsertAttributesAsync(
        DataSession session, string entityLogicalName, IEnumerable<ColumnDefinition> attributes, long stamp, CancellationToken ct,
        IReadOnlyDictionary<(string, string), int>? imageOrder = null)
    {
        foreach (var a in attributes)
        {
            await ExecuteAsync(session,
                "INSERT INTO meta.attribute (metadata_id, entity_logical_name, logical_name, definition, version_stamp) " +
                "VALUES (@id, @entity, @name, @definition, @stamp)", ct,
                ("@id", a.MetadataId!.Value), ("@entity", entityLogicalName), ("@name", a.LogicalName!),
                ("@definition", StoredAttributeJson(a, imageOrder is null ? null : OrdinalOf(imageOrder, entityLogicalName, a))), ("@stamp", stamp));
        }
    }

    /// <summary>The statement that raises the metadata version stamp and returns the new value.</summary>
    private static string VersionStampSql(ISqlDialect d)
    {
        var current = d.Quote("current");
        return $"UPDATE meta.version_stamp SET {current} = {current} + 1{d.ReturningInline([current])}{d.ReturningTrailing([current])}";
    }

    /// <summary>Custom entities get object type codes from 10000 up: max existing custom code + 1 (ruling R2).</summary>
    private static Task<int> NextObjectTypeCodeAsync(DataSession session, CancellationToken ct) =>
        ScalarAsync<int>(session, $"""
            SELECT {session.Dialect.IsNull("MAX(otc) + 1", FirstCustomObjectTypeCode.ToString(System.Globalization.CultureInfo.InvariantCulture))}
            FROM (SELECT {session.Dialect.TryCastInteger(session.Dialect.JsonValue("definition", "$.ObjectTypeCode"), SqlColumnKind.Int32)} AS otc FROM meta.entity) AS e
            WHERE otc >= {FirstCustomObjectTypeCode}
            """, ct);

    private static async Task ExecuteAsync(DataSession session, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Command(session, sql, parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(DataSession session, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Command(session, sql, parameters);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync(ct))!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DbCommand Command(DataSession session, string sql, (string Name, object Value)[] parameters)
    {
        var cmd = session.Command(sql);
        foreach (var (pName, value) in parameters) cmd.AddParameter(pName, DbValueTypes.Of(value), value);
        return cmd;
    }
}
