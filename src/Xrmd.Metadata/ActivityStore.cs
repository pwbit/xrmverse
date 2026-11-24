using Xrmd.Model.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>
/// The activity model of <see cref="MetadataStore"/> (plan 2.11, rulings A4, A5, A6, A10 and A15). Custom activities
/// (<c>IsActivity</c>) are created with the common columns, their party lists and their states, and with <c>activityid</c> as
/// their key. <c>HasActivities</c> and <c>HasNotes</c> are table settings that go from false to true only. The store keeps the
/// targets of these lookups and their relationships, in the session of the table change that moves them:
/// <list type="bullet">
/// <item><c>regardingobjectid</c> of <c>activitypointer</c> and of every activity table: the tables with <c>HasActivities</c>
/// (<see cref="ActivitySchema.RegardingRelationship"/>);</item>
/// <item><c>annotation.objectid</c>: the tables with <c>HasNotes</c> (<see cref="ActivitySchema.NotesRelationship"/>);</item>
/// <item><c>postregarding.regardingobjectid</c> and <c>post.regardingobjectid</c> (plan 3.5 UI-1g): the tables with <c>HasNotes</c> or
/// <c>HasActivities</c>, with <see cref="PostSchema.PostRegardingsRelationship"/> for the first and no relationship for the second;</item>
/// <item><c>activityparty.activityid</c>: <c>activitypointer</c> and every activity table (<see cref="ActivitySchema.PartiesRelationship"/>);</item>
/// <item><c>queueitem.objectid</c> (plan 4.1a): <c>activitypointer</c> and the tables with <c>IsValidForQueue</c>, a setting that goes
/// from false to true only (<see cref="QueueSchema.ItemRelationship"/>).</item>
/// </list>
/// Only these paths change those targets and relationships; the public messages refuse it. Deleting a table is refused while an
/// activity, a note, a post or a queue item refers to one of its records; deleting a custom activity deletes its pointer and party rows.
/// </summary>
public sealed partial class MetadataStore
{
    /// <inheritdoc />
    public IReadOnlySet<string> ActivityTables => _cache.ActivityTables;

    /// <inheritdoc />
    public IReadOnlySet<string> RegardingTables => _cache.RegardingTables;

    /// <inheritdoc />
    public IReadOnlySet<string> NotesTables => _cache.NotesTables;

    /// <inheritdoc />
    public IReadOnlySet<string> QueueTables => _cache.QueueTables;

    /// <summary>
    /// The rules of a custom activity created by a caller (A4), applied to the copy <paramref name="e"/> it is created from: its
    /// primary column has the SchemaName <c>Subject</c>; <c>HasActivities</c> is false and <c>HasNotes</c> true (both set when absent);
    /// it is owned by a user (team-owned custom activities are a known limit); <c>ActivityTypeMask</c> is 0 or 1 (1 when absent);
    /// mail merge is off.
    /// </summary>
    /// <exception cref="XrmdValidationException">400 naming the setting.</exception>
    private static void PrepareCustomActivity(TableDefinition e, ColumnDefinition? primaryName)
    {
        var what = $"Custom activity '{e.LogicalName}'";
        if (primaryName is null || !string.Equals(primaryName.SchemaName, "Subject", StringComparison.Ordinal)
                                || primaryName.LogicalName != ActivitySchema.Subject)
            throw new XrmdValidationException(
                $"{what}: its primary column must have the SchemaName 'Subject' (logical name 'subject'), not '{primaryName?.SchemaName}'.");
        if (e.HasActivities == true)
            throw new XrmdValidationException($"{what}: HasActivities must be false (an activity cannot be the regarding record of other activities).");
        if (e.HasNotes == false)
            throw new XrmdValidationException($"{what}: HasNotes must be true (every activity can have notes).");
        if (e.OwnershipType is not (null or TableOwnership.UserOwned))
            throw new XrmdValidationException(
                $"{what}: the ownership '{e.OwnershipType}' is not supported: xrmverse creates custom activities owned by a user only.");
        if (e.ActivityTypeMask is not (null or 0 or 1))
            throw new XrmdValidationException($"{what}: ActivityTypeMask must be 0 or 1, not {e.ActivityTypeMask}.");
        if (e.IsMailMergeEnabled?.Value == true)
            throw new XrmdValidationException($"{what}: IsMailMergeEnabled must be false.");
        e.HasActivities = false;
        e.HasNotes = true;
        // Plan 4.1a: a custom activity is queue-enabled when created, unless the caller says otherwise.
        e.IsValidForQueue ??= QueueSchema.SettingManagedFlag(true);
        e.ActivityTypeMask ??= 1;
        e.OwnershipType = TableOwnership.UserOwned;
    }

    /// <summary>A table that is not an activity has no <c>ActivityTypeMask</c> (A4: it is the setting of an activity).</summary>
    /// <exception cref="XrmdValidationException">400 naming <c>ActivityTypeMask</c>.</exception>
    private static void DemandNoActivitySettings(TableDefinition e)
    {
        if (e.ActivityTypeMask is not null)
            throw new XrmdValidationException(
                $"Entity '{e.LogicalName}': ActivityTypeMask is a setting of activity tables (IsActivity true) only.");
    }

    /// <summary>
    /// What an update asks of the activity settings of <paramref name="current"/>: <c>IsActivity</c> and <c>ActivityTypeMask</c>
    /// cannot change (A4); <c>HasActivities</c> and <c>HasNotes</c> can go from false to true only (A5), where
    /// <see cref="DemandActivitySettingsAllowed"/> allows them. Only a change is checked, so a retrieved definition sent back passes.
    /// </summary>
    /// <returns>Which of <c>HasActivities</c> and <c>HasNotes</c> the update turns on.</returns>
    /// <exception cref="XrmdValidationException">400 naming the setting.</exception>
    private static (bool Activities, bool Notes) ActivitySettingChanges(CacheState cache, TableDefinition current, TableDefinition changes)
    {
        var name = current.LogicalName;
        if (changes.IsActivity is { } isActivity && isActivity != (current.IsActivity == true))
            throw new XrmdValidationException($"IsActivity of entity '{name}' cannot change: a table is an activity table or not from its creation.");
        if (changes.ActivityTypeMask is { } mask && mask != current.ActivityTypeMask)
            throw new XrmdValidationException($"ActivityTypeMask of entity '{name}' cannot change: it is set when the activity table is created.");
        bool? activities = changes.HasActivities is { } a && a != (current.HasActivities == true) ? a : null;
        bool? notes = changes.HasNotes is { } n && n != (current.HasNotes == true) ? n : null;
        if (activities == false)
            throw new XrmdValidationException($"HasActivities cannot be disabled on entity '{name}': once a table has activities, it keeps them.");
        if (notes == false)
            throw new XrmdValidationException($"HasNotes cannot be disabled on entity '{name}': once a table has notes, it keeps them.");
        DemandActivitySettingsAllowed(cache, current, activities == true, notes == true);
        return (activities == true, notes == true);
    }

    /// <summary>
    /// <paramref name="entity"/> can have the settings asked for (A5): <c>HasActivities</c> neither on an activity table, the pointer,
    /// <c>activityparty</c> or <c>annotation</c>, nor on an intersect table; <c>HasNotes</c> neither on an intersect table,
    /// <c>activityparty</c> or <c>annotation</c>; and the organization has the tables they link to.
    /// </summary>
    /// <exception cref="XrmdValidationException">400 naming the setting.</exception>
    private static void DemandActivitySettingsAllowed(CacheState cache, TableDefinition entity, bool activities, bool notes)
    {
        if (ActivitySettingRefusal(cache.ByLogicalName.ContainsKey, entity, activities, notes) is { } refusal)
            throw new XrmdValidationException(refusal);
    }

    /// <summary>
    /// Why <paramref name="entity"/> cannot have the settings asked for in this organization (the rule of
    /// <see cref="DemandActivitySettingsAllowed"/>), or null when it can. The startup upgrade reports a kept flag with it.
    /// </summary>
    internal string? ActivitySettingRefusal(TableDefinition entity, bool activities, bool notes) =>
        ActivitySettingRefusal(_cache.ByLogicalName.ContainsKey, entity, activities, notes);

    private static string? ActivitySettingRefusal(Func<string, bool> hasTable, TableDefinition entity, bool activities, bool notes)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var name = entity.LogicalName;
        if (activities)
        {
            if (entity.IsIntersect == true)
                return $"HasActivities cannot be enabled on entity '{name}': it is an intersect entity.";
            if (entity.IsActivity == true || name is ActivitySchema.Pointer or ActivitySchema.Party or ActivitySchema.Annotation)
                return $"HasActivities cannot be enabled on entity '{name}': an activity, an activity party or a note cannot be the regarding record of activities.";
            if (!hasTable(ActivitySchema.Pointer))
                return $"HasActivities needs the table '{ActivitySchema.Pointer}', which this organization does not have yet.";
        }
        if (notes)
        {
            if (entity.IsIntersect == true)
                return $"HasNotes cannot be enabled on entity '{name}': it is an intersect entity.";
            if (name is ActivitySchema.Party or ActivitySchema.Annotation)
                return $"HasNotes cannot be enabled on entity '{name}': an activity party or a note cannot have notes.";
            if (!hasTable(ActivitySchema.Annotation))
                return $"HasNotes needs the table '{ActivitySchema.Annotation}', which this organization does not have yet.";
        }
        return null;
    }

    /// <summary>The tables whose <c>regardingobjectid</c> the store keeps: the pointer first, then the activity tables by name.</summary>
    private static List<TableDefinition> RegardingHolders(CacheState cache) =>
        cache.ByLogicalName.Values.Where(e => e.LogicalName == ActivitySchema.Pointer || ActivitySchema.IsActivityTable(e))
            .OrderBy(e => e.LogicalName == ActivitySchema.Pointer ? 0 : 1).ThenBy(e => e.LogicalName, StringComparer.Ordinal).ToList();

    private static bool Targets(CacheState cache, string entity, string attribute, string target) =>
        cache.ByLogicalName.GetValueOrDefault(entity)?.Attributes?.OfType<LookupColumn>()
            .FirstOrDefault(a => a.LogicalName == attribute)?.Targets?.Contains(target, StringComparer.Ordinal) == true;

    /// <summary>
    /// Checks what deleting <paramref name="entity"/> needs of the activity model: no activity and no post is regarding one of its
    /// records and no note is attached to one (the rows read stay locked until the session ends). Returns whether the targets and
    /// relationships are to be synchronized once the table is gone.
    /// </summary>
    /// <exception cref="XrmdValidationException">400 naming the number of activities, notes or posts.</exception>
    private static async Task<bool> PrepareActivityUnlinkAsync(DataSession session, CacheState cache, TableDefinition entity, CancellationToken ct)
    {
        var (regarding, notes, refusal) = await ActivityDeleteBlockersAsync(session, cache, entity, locked: true, ignored: null, ct);
        if (refusal is not null) throw new XrmdValidationException(refusal);
        return regarding || notes || PostTarget(cache, entity.LogicalName!) || ActivitySchema.IsActivityTable(entity)
               || entity.HasActivities == true || entity.HasNotes == true || entity.IsValidForQueue?.Value == true
               || Targets(cache, QueueSchema.QueueItem, QueueSchema.ObjectId, entity.LogicalName!);
    }

    /// <summary>
    /// Why deleting the table <paramref name="logicalName"/> would be refused by the activity model now (the refusal of
    /// <c>DeleteEntity</c>: activities or posts regarding its records, notes attached to them), read on the published metadata without
    /// locks; null when nothing blocks it or the table does not exist. Activities of the tables in <paramref name="dropped"/> are
    /// not counted (an operation that deletes those tables first deletes their rows).
    /// </summary>
    public async Task<string?> ActivityDeleteRefusalAsync(DataSession session, string logicalName, IReadOnlySet<string>? dropped, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var cache = _cache;
        if (!cache.ByLogicalName.TryGetValue(logicalName, out var entity)) return null;
        return (await ActivityDeleteBlockersAsync(session, cache, entity, locked: false, dropped, ct)).Refusal;
    }

    private static async Task<(bool Regarding, bool Notes, string? Refusal)> ActivityDeleteBlockersAsync(DataSession session, CacheState cache,
        TableDefinition entity, bool locked, IReadOnlySet<string>? ignored, CancellationToken ct)
    {
        var name = entity.LogicalName;
        var regarding = RegardingHolders(cache).Any(h => Targets(cache, h.LogicalName!, ActivitySchema.RegardingObjectId, name!));
        var notes = Targets(cache, ActivitySchema.Annotation, ActivitySchema.ObjectId, name!);
        var hint = locked ? $"{session.Dialect.LockHint(RowLock.UpdateRange)}" : "";
        if (entity.ObjectTypeCode is { } code)
        {
            if (regarding)
            {
                var count = 0;
                // Each activity is a row of its own table (the pointer repeats them).
                foreach (var table in cache.ByLogicalName.Values.Where(ActivitySchema.IsActivityTable).Select(e => e.LogicalName)
                             .Where(t => ignored?.Contains(t!) != true).Order(StringComparer.Ordinal))
                {
                    if (locked) await session.KeyLockAsync(table!, code, ct);
                    count += await ScalarAsync<int>(session,
                        $"SELECT COUNT(*) FROM {session.DataTable(table!)}{hint} WHERE {session.Quote(ActivitySchema.RegardingObjectId + TypeMapping.TypeColumnSuffix)} = @code",
                        ct, ("@code", code));
                }
                if (count > 0)
                    return (regarding, notes, $"Entity '{name}' cannot be deleted: {count} activities are regarding its records. Delete them first.");
            }
            if (notes)
            {
                if (locked) await session.KeyLockAsync(ActivitySchema.Annotation, code, ct);
                var count = await ScalarAsync<int>(session,
                    $"SELECT COUNT(*) FROM {session.DataTable(ActivitySchema.Annotation)}{hint} WHERE {session.Quote(ActivitySchema.ObjectId + TypeMapping.TypeColumnSuffix)} = @code",
                    ct, ("@code", code));
                if (count > 0)
                    return (regarding, notes, $"Entity '{name}' cannot be deleted: {count} notes are attached to its records. Delete them first.");
            }
            // Plan 3.5 UI-1g: the posts regarding its records (their postregarding rows are deleted with the table).
            if (PostTarget(cache, name!) && cache.ByLogicalName.ContainsKey(PostSchema.Post))
            {
                if (locked) await session.KeyLockAsync(PostSchema.Post, code, ct);
                var count = await ScalarAsync<int>(session,
                    $"SELECT COUNT(*) FROM {session.DataTable(PostSchema.Post)}{hint} WHERE {session.Quote(ActivitySchema.RegardingObjectId + TypeMapping.TypeColumnSuffix)} = @code",
                    ct, ("@code", code));
                if (count > 0)
                    return (regarding, notes, $"Entity '{name}' cannot be deleted: {count} posts are regarding its records. Delete them first.");
            }
            // Plan 4.1a: the queue items of its records, refused as notes are (a raw delete would bypass the queue items' own
            // deletion log; removing them first goes through the messages).
            if (Targets(cache, QueueSchema.QueueItem, QueueSchema.ObjectId, name!))
            {
                if (locked) await session.KeyLockAsync(QueueSchema.QueueItem, code, ct);
                var count = await ScalarAsync<int>(session,
                    $"SELECT COUNT(*) FROM {session.DataTable(QueueSchema.QueueItem)}{hint} WHERE {session.Quote(QueueSchema.ObjectId + TypeMapping.TypeColumnSuffix)} = @code",
                    ct, ("@code", code));
                if (count > 0)
                    return (regarding, notes, $"Entity '{name}' cannot be deleted: {count} queue items refer to its records. Remove them from their queues first.");
            }
        }
        return (regarding, notes, null);
    }

    /// <summary>
    /// True when the table <paramref name="name"/> is a target of <c>post.regardingobjectid</c> or <c>postregarding.regardingobjectid</c>.
    /// </summary>
    private static bool PostTarget(CacheState cache, string name) =>
        Targets(cache, PostSchema.Post, ActivitySchema.RegardingObjectId, name)
        || Targets(cache, PostSchema.Regarding, ActivitySchema.RegardingObjectId, name);

    /// <summary>
    /// Deletes the <c>postregarding</c> rows of the records of <paramref name="entity"/> (plan 3.5 UI-1g), in the session that drops its
    /// table: no post regards them any more (<see cref="ActivityDeleteBlockersAsync"/>), and nothing else would remove them.
    /// </summary>
    private static async Task DeletePostRegardingRowsAsync(DataSession session, CacheState cache, TableDefinition entity, CancellationToken ct)
    {
        if (entity.ObjectTypeCode is not { } code || !Targets(cache, PostSchema.Regarding, ActivitySchema.RegardingObjectId, entity.LogicalName!))
            return;
        await ExecuteAsync(session,
            $"DELETE FROM {session.DataTable(PostSchema.Regarding)} WHERE {session.Quote(ActivitySchema.RegardingObjectId + TypeMapping.TypeColumnSuffix)} = @code",
            ct, ("@code", code));
    }

    /// <summary>
    /// Deletes the pointer rows and the party rows of the custom activity <paramref name="entity"/> (A4), in the session that drops
    /// its table.
    /// </summary>
    private static async Task DeleteActivityRowsAsync(DataSession session, CacheState cache, TableDefinition entity, CancellationToken ct)
    {
        if (entity.ObjectTypeCode is not { } code) return;
        // Neither table can be tracked (ChangeTrackingSchema.IneligibleTables, A13): no deletion log to write.
        if (cache.ByLogicalName.ContainsKey(ActivitySchema.Pointer))
            await ExecuteAsync(session,
                $"DELETE FROM {session.DataTable(ActivitySchema.Pointer)} WHERE {session.Quote(ActivitySchema.ActivityTypeCode)} = @code", ct, ("@code", code));
        if (cache.ByLogicalName.ContainsKey(ActivitySchema.Party))
            await ExecuteAsync(session,
                $"DELETE FROM {session.DataTable(ActivitySchema.Party)} WHERE {session.Quote(ActivitySchema.ActivityId + TypeMapping.TypeColumnSuffix)} = @code",
                ct, ("@code", code));
    }

    /// <summary>
    /// Startup path for <c>ActivitySettingsUpgrade</c> only (A20): gives the managed lookups of the activity model the targets and
    /// relationships the tables of <paramref name="session"/>'s view call for. Writes nothing when they are up to date.
    /// </summary>
    internal async Task EnsureActivityLinksAsync(DataSession session, CancellationToken ct)
    {
        var pending = await EnterAsync(session, ct);
        await SyncActivityLinksAsync(session, pending, ct);
    }

    /// <summary>
    /// Brings the targets and the relationships of the managed lookups of the activity model in line with the tables of
    /// <paramref name="pending"/>'s view: a target is added for each table that calls for one (in the order of the tables' names,
    /// after the targets already there) and removed for each table that no longer does, each target with its relationship.
    /// Bumps the version stamp only when something is written.
    /// </summary>
    private async Task SyncActivityLinksAsync(DataSession session, PendingChanges pending, CancellationToken ct)
    {
        var cache = pending.Working;
        var holders = RegardingHolders(cache);
        var regarding = cache.ByLogicalName.Values.Where(e => e.HasActivities == true && e.IsIntersect != true)
            .OrderBy(e => e.LogicalName, StringComparer.Ordinal).ToList();
        var notes = cache.ByLogicalName.Values.Where(e => e.HasNotes == true && e.IsIntersect != true)
            .OrderBy(e => e.LogicalName, StringComparer.Ordinal).ToList();
        // Plan 3.5 UI-1g: the posts of any table with a timeline, that is with notes or activities.
        var posts = cache.ByLogicalName.Values.Where(e => (e.HasNotes == true || e.HasActivities == true) && e.IsIntersect != true)
            .OrderBy(e => e.LogicalName, StringComparer.Ordinal).ToList();
        foreach (var holder in holders)
            await SyncManagedLookupAsync(session, pending, holder.LogicalName!, ActivitySchema.RegardingObjectId, regarding,
                target => ActivitySchema.RegardingRelationship(target, holder), ct);
        await SyncManagedLookupAsync(session, pending, ActivitySchema.Annotation, ActivitySchema.ObjectId, notes, ActivitySchema.NotesRelationship, ct);
        await SyncManagedLookupAsync(session, pending, ActivitySchema.Party, ActivitySchema.ActivityId, holders, ActivitySchema.PartiesRelationship, ct);
        // Plan 4.1b (ruling P4): an email's attachments, with the Delete cascade of the email (and of the pointer, which no Delete reaches).
        await SyncManagedLookupAsync(session, pending, MailSchema.Attachment, ActivitySchema.ObjectId,
            cache.ByLogicalName.Values.Where(e => e.LogicalName is ActivitySchema.Pointer or ActivitySchema.Email)
                .OrderBy(e => e.LogicalName == ActivitySchema.Pointer ? 0 : 1).ToList(),
            ActivitySchema.AttachmentRelationship, ct);
        await SyncManagedLookupAsync(session, pending, PostSchema.Regarding, ActivitySchema.RegardingObjectId, posts,
            PostSchema.PostRegardingsRelationship, ct);
        // post.regardingobjectid has the same targets and no relationships, as on Learn.
        await SyncManagedLookupAsync(session, pending, PostSchema.Post, ActivitySchema.RegardingObjectId, posts, relationship: null, ct);
        // Plan 4.1a: queueitem.objectid targets the pointer (as on Learn) and the queue-enabled tables.
        await SyncManagedLookupAsync(session, pending, QueueSchema.QueueItem, QueueSchema.ObjectId, QueueItemTargets(cache),
            QueueSchema.ItemRelationship, ct);
        // Plan 2.11 A16: note bodies are fileattachment rows, so annotation is a target of fileattachment.objectid.
        await EnsurePermanentAttachmentTargetsAsync(session, pending, ct);
    }

    /// <param name="relationship">The relationship of each target, or null for a lookup that has none (its stray ones are removed).</param>
    private async Task SyncManagedLookupAsync(DataSession session, PendingChanges pending, string entityLogicalName, string attribute,
        IReadOnlyList<TableDefinition> wanted, Func<TableDefinition, OneToManyRelationship>? relationship, CancellationToken ct)
    {
        var cache = pending.Working;
        if (cache.ByLogicalName.GetValueOrDefault(entityLogicalName) is not { } owner
            || (owner.Attributes ?? []).OfType<LookupColumn>().FirstOrDefault(a => a.LogicalName == attribute) is not { } lookup)
            return;
        var names = wanted.Select(w => w.LogicalName!).ToList();
        var current = lookup.Targets ?? [];
        var targets = current.Where(names.Contains).Concat(names.Where(n => !current.Contains(n, StringComparer.Ordinal))).ToArray();
        bool Mine(OneToManyRelationship r) => r.ReferencingEntity == entityLogicalName && r.ReferencingAttribute == attribute;
        var stale = cache.SavedOneToMany.Values
            .Where(r => Mine(r) && (relationship is null || !targets.Contains(r.ReferencedEntity, StringComparer.Ordinal))).ToList();
        var missing = relationship is null ? []
            : wanted.Where(w => !cache.SavedOneToMany.Values.Any(r => Mine(r) && r.ReferencedEntity == w.LogicalName)).ToList();
        var retarget = !targets.SequenceEqual(current, StringComparer.Ordinal);
        if (!retarget && stale.Count == 0 && missing.Count == 0) return;

        var stamp = pending.Stamp ?? await BumpVersionStampAsync(session, pending, ct);
        if (stale.Count > 0) await DeleteSavedOneToManyAsync(session, cache, stale.Contains, ct);
        if (retarget)
        {
            var updated = MetadataJson.Clone(lookup);
            updated.Targets = [.. targets];
            await StoreAttributeAsync(session, entityLogicalName, updated, stamp, ct);
            var copy = MetadataJson.Clone(owner);
            copy.SetAttributes((owner.Attributes ?? []).Select(a => a.LogicalName == updated.LogicalName ? updated : a));
            cache.Put(copy, previous: owner);
        }
        foreach (var target in missing) await EnsureNamedRelationshipAsync(session, relationship!(target), ct);
    }

    /// <summary>The public messages never write the targets or the relationships of the managed lookups of the activity model.</summary>
    private static XrmdValidationException ActivityLookupIsManaged(string what) => new(
        $"{what} belongs to the activity model: its targets and relationships follow the tables' IsActivity, HasActivities, HasNotes " +
        "and IsValidForQueue settings and cannot be changed directly.");

    // ---- IsValidForQueue (plan 4.1a) ------------------------------------------------------------------------------------

    /// <summary>
    /// The targets of <c>queueitem.objectid</c> in <paramref name="cache"/>: <c>activitypointer</c> first (Learn lists it, with
    /// <c>ActivityPointer_QueueItem</c>), then the tables with <c>IsValidForQueue</c> by name.
    /// </summary>
    private static List<TableDefinition> QueueItemTargets(CacheState cache) =>
        cache.ByLogicalName.Values
            .Where(e => e.IsIntersect != true && (e.LogicalName == ActivitySchema.Pointer || e.IsValidForQueue?.Value == true))
            .OrderBy(e => e.LogicalName == ActivitySchema.Pointer ? 0 : 1).ThenBy(e => e.LogicalName, StringComparer.Ordinal).ToList();

    /// <summary>
    /// What an update asks of <c>IsValidForQueue</c> of <paramref name="current"/>: one-way, false to true only, where
    /// <see cref="QueueSettingRefusal(Func{string, bool}, TableDefinition)"/> allows it. Only a change is checked, so a retrieved
    /// definition sent back passes.
    /// </summary>
    /// <returns>True when the update turns the setting on.</returns>
    /// <exception cref="XrmdValidationException">400 naming <c>IsValidForQueue</c>.</exception>
    private static bool QueueSettingChange(CacheState cache, TableDefinition current, TableDefinition changes)
    {
        if (changes.IsValidForQueue is not { } flag || flag.Value == (current.IsValidForQueue?.Value == true)) return false;
        if (!flag.Value)
            throw new XrmdValidationException(
                $"IsValidForQueue cannot be disabled on entity '{current.LogicalName}': once a table is enabled for queues, it stays enabled.");
        DemandQueueSettingAllowed(cache, current);
        return true;
    }

    /// <summary><paramref name="entity"/> can be enabled for queues (<see cref="QueueSettingRefusal(Func{string, bool}, TableDefinition)"/>).</summary>
    /// <exception cref="XrmdValidationException">400 naming <c>IsValidForQueue</c>.</exception>
    private static void DemandQueueSettingAllowed(CacheState cache, TableDefinition entity)
    {
        if (QueueSettingRefusal(cache.ByLogicalName.ContainsKey, entity) is { } refusal) throw new XrmdValidationException(refusal);
    }

    /// <summary>
    /// Why <paramref name="entity"/> cannot be enabled for queues in this organization, or null when it can. The startup upgrade
    /// reports a kept flag with it.
    /// </summary>
    internal string? QueueSettingRefusal(TableDefinition entity) => QueueSettingRefusal(_cache.ByLogicalName.ContainsKey, entity);

    /// <summary>
    /// The rule of <c>IsValidForQueue</c> (plan 4.1a): neither an intersect table, nor the queue tables, the pointer, an activity
    /// party or a note can be put in a queue; the organization needs <c>queueitem</c>.
    /// </summary>
    private static string? QueueSettingRefusal(Func<string, bool> hasTable, TableDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var name = entity.LogicalName;
        if (entity.IsIntersect == true)
            return $"IsValidForQueue cannot be enabled on entity '{name}': it is an intersect entity.";
        if (name is QueueSchema.Queue or QueueSchema.QueueItem or QueueSchema.QueueMembership or ActivitySchema.Pointer
            or ActivitySchema.Party or ActivitySchema.Annotation)
            return $"IsValidForQueue cannot be enabled on entity '{name}': a queue, a queue item, the activity pointer, an activity party or a note cannot be put in a queue.";
        if (!hasTable(QueueSchema.QueueItem))
            return $"IsValidForQueue needs the table '{QueueSchema.QueueItem}', which this organization does not have yet.";
        return null;
    }

}
