using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Core.Mail;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;
using Xrmd.Pipeline;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.Solution.Layers;

namespace Xrmd.Core;

/// <summary>
/// S.2e3b: the Delete body and its cascades on the model. The SDK twin <c>DeleteAsync(... string, Guid ...)</c>
/// (<c>Sdk/CoreOperations.Records.Sdk.cs</c>) runs the same body; a helper of another step that is still on the SDK is reached through
/// <see cref="CoreSdkBridges"/>. A cascaded child goes as a model <see cref="Request"/> named Delete, marked with
/// <see cref="CascadeHint.Mark(Request)"/>, through the model <see cref="NestedRequest"/>.
/// </summary>
public sealed partial class CoreOperations
{
    /// <summary>
    /// Deletes the record and applies the delete cascade of every one-to-many relationship whose referenced entity is the
    /// record's (in the transaction of the Delete): Restrict refuses the Delete while records point to it, Cascade deletes those
    /// records first (each through <paramref name="nested"/>, so as a Delete message of the pipeline with its own privilege
    /// check, plug-in steps and cascades; directly through this method when there is no pipeline), RemoveLink clears their lookup.
    /// A record already being deleted by the same cascade (a cycle, a self-reference) is not deleted twice.
    /// </summary>
    /// <param name="nested">Runs a cascaded child through the pipeline: a model request <c>{ Name = "Delete", ["Target"] =
    /// RecordReference }</c> marked with <see cref="CascadeHint.Mark(Request)"/> (plan 2.14, ruling W5: a core cascade fires its plug-in
    /// steps but triggers no workflow). Null outside a pipeline.</param>
    /// <exception cref="XrmdFaultException">ObjectDoesNotExist (404) when the record does not exist; for a <c>solution</c>, the
    /// refusals of <see cref="SolutionDeleter.RemoveContainerAsync"/> (system solution 403, managed solution 400); InvalidArgument
    /// (400) when a one-to-many relationship with Delete Restrict still has records pointing to the record.</exception>
    public async Task DeleteRecordAsync(DataSession s, Caller caller, string entityName, Guid id, NestedRequest? nested, CancellationToken ct)
    {
        var m = TableOf(entityName);
        var table = m.LogicalName!;
        await DemandRowDeleteAllowedAsync(s, caller, table, id, ct);
        // Plan 4.1a: before the rule that a team owning records stays (its default queue is not one that keeps it).
        if (table == Team) await DeleteDefaultQueueAsync(s, caller, id, ct);
        // Plan 4.1a: a queue goes only once its items are inactive (they then go by the cascade of queue_entries).
        if (table == QueueSchema.Queue && !MigrationScope.IsActive) await QueueMessages.DemandNoActiveItemsAsync(s, id, ct);
        // Plan 4.1b: a queue goes with its mailbox and the mailbox's secrets (internal deletes: the caller's rights are those on the queue).
        if (table == QueueSchema.Queue && store.GetTable(MailSchema.Mailbox) is not null)
            foreach (var mailbox in await Mailboxes.DetachAsync(s, table, id, ct)) await DeleteRecordAsync(s, Caller.Internal, MailSchema.Mailbox, mailbox, null, ct);
        await CheckDeleteRulesAsync(s, table, id, ct);
        // Plan 2.14 (W14): an action's message goes with its definition, before the cascades (its activations) run.
        if (writes is not null && !MigrationScope.IsActive) await writes.OnDeletingAsync(s, table, id, ct);
        // Plan 2.10, final review I1: the base currency and a currency in a user's settings, read under locks in the transaction.
        if (table == CurrencySchema.Table) await CurrencyRules.EnforceDeleteAsync(id, s, ct);
        var deleting = DeleteCascades.GetValue(s, static _ => new DeleteCascade());
        deleting.Depth++;
        try
        {
            deleting.Records.Add((table, id));
            // Plan 2.15, ruling D4: a condition of a published rule goes only with its rule.
            if (table == DuplicateDetectionSchema.Condition)
                await DuplicateRuleRules.DemandConditionDeleteAsync(s, id, rule => deleting.Records.Contains((DuplicateDetectionSchema.Rule, rule)), ct);
            await ApplyDeleteCascadesAsync(s, caller, m, id, deleting, nested, ct);
            await DeleteAccessTeamsAsync(s, caller, m, id, ct);
            // A managed solution's row goes only as the last step of its uninstall (a layer operation, ruling R2).
            if (table == SolutionDeleter.Table) await _solutionDeleter.RemoveContainerAsync(s, id, ManagedProperties.Bypassed(caller), ct);
            var parentComponent = caller == Caller.Internal ? null : await ParentComponentAsync(s, table, id, ct);
            // Plan 2.11 (A1, A10): an activity's party rows and pointer row go first, then its own row, in this transaction.
            if (ActivitySchema.IsActivityTable(m))
            {
                // The activity row is locked first, as an Update locks it before its party and pointer rows (no lock-order inversion).
                await LockRowAsync(s, m, id, ct);
                await ActivityParties.DeleteAsync(s, id, ct);
                await ActivityPointerSync.DeleteAsync(s, id, ct);
            }
            // The audited columns of the record, read before it goes under an update lock (only when the table is audited).
            Record? before = null;
            if (await CoreSdkBridges.IsAuditedAsync(audit, s, m, ct) && await LockRowAsync(s, m, id, ct))
                before = await AuditImageAsync(s, m, id, CoreSdkBridges.AuditedColumns(m), ct);
            // Plan 4.1b: an attachment's email counts its attachments (unless the email is the one being deleted); locked before the row goes.
            Guid? attachedTo = null;
            if (table == MailSchema.Attachment && await CoreSdkBridges.StoredEmailAsync(s, m, id, ct) is { } owner
                && !deleting.Records.Contains((ActivitySchema.Email, owner)))
            {
                attachedTo = owner;
                await LockRowAsync(s, TableOf(ActivitySchema.Email), owner, ct);
            }
            var sql = $"DELETE FROM {s.DataTable(table)} WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}";
            var parameters = new[] { new DetachedParameter(IdParameter, DbType.Guid, id) };
            if (await ExecuteNonQueryAsync(s, sql, parameters, ct) == 0)
                throw XrmdFaultException.ObjectDoesNotExist(table, id);
            if (attachedTo is { } counted) await CoreSdkBridges.RecountAttachmentsAsync(s, TableOf(ActivitySchema.Email), m, counted, adjust: 0, ct);
            // Plan 2.14 (W4, W10): before the links to the record are cleared (a job still names its deleted activation).
            if (writes is not null && !MigrationScope.IsActive) await writes.OnDeletedAsync(s, table, id, ct);
            if (CurrencyMessages.InvalidatesCatalog(table)) CurrencyMessages.InvalidateAfterTransaction(currencies, s);
            // Plan 2.13: a tracked table's deletion is logged in this transaction (cascaded children come through here too).
            if (store.ChangeTrackingTables.Contains(table)) await ChangeTrackingLog.RecordDeleteAsync(s, table, id, ct);
            // Plan 2.15: the record's match codes go with it (cascaded children too); a rule's match code table goes with the
            // rule, whatever its state (ruling D4).
            InvalidateDuplicateRulesOnWrite(s, table);
            await duplicates.MatchCodes.RemoveAsync(s, table, id, ct);
            if (table == DuplicateDetectionSchema.Rule) await duplicates.OnRuleDeletedAsync(s, id, ct);
            if (table == AsyncOperation) await duplicates.OnJobEndedAsync(s, id, ct);
            if (before is not null)
                await CoreSdkBridges.WriteAuditAsync(audit, s, caller, AuditActions.Delete, AuditOperations.Delete, m, id, before, null, ct);
            await DeleteDependentRowsAsync(s, table, id, ct);
            // Plan 2.11 (A10, pre-flight C10): a deleted party record keeps its party rows, marked ispartydeleted (users are never deleted).
            if (ActivitySchema.PartyRecordTables.Contains(table) || table == Team)
                await _parties.MarkPartyDeletedAsync(s, table, id, ct);
            await RemoveLinksAsync(s, table, id, ct);
            await CoreSdkBridges.DeleteRecordFilesAsync(files, s, m, id, ct); // ruling F8: the blobs go after the commit
            // Plan 3.5 UI-1d (ruling R2): a custom control's files go with its row, in this transaction.
            if (table == CustomControlSchema.Table) await CustomControlFiles.DeleteRowsAsync(s, id, ct);
            // Plan 4.1b: the write-only mail secrets go with their record, in this transaction.
            if (MailSecrets.HasSecrets(table)) await MailSecrets.DeleteAsync(s, id, ct);
            // Plan 4.1c: an email's message keys go with it, in this transaction.
            if (table == ActivitySchema.Email) await MailMessageKeys.DeleteAsync(s, id, ct);
            // Plan 4.1c, task 9: a mailbox's sync state goes with it (its sync job ends at its next run).
            if (table == MailSchema.Mailbox) await MailboxSyncState.DeleteAsync(s, id, ct);
            var images = table == StepTable ? await DeleteStepImagesAsync(s, id, ct) : [];
            if (ComponentType.OfTable(table) is not null) await tracker.RemoveDeletedAsync(s, [id, .. images], ct);
            if (parentComponent is not null) await MarkParentChangedAsync(s, caller, table, id, parentComponent, ct);
        }
        finally
        {
            if (--deleting.Depth == 0) DeleteCascades.Remove(s);
        }
    }

    /// <summary>
    /// Plan 4.1a: the default queue goes with its team, in the team's Delete, unless it holds active queue items (400, nothing is
    /// deleted). Only the queue the team owns and points to is deleted; its inactive items go by the cascade of <c>queue_entries</c>.
    /// Users are never deleted, so a user's default queue has no such step.
    /// </summary>
    private async Task DeleteDefaultQueueAsync(DataSession s, Caller caller, Guid teamId, CancellationToken ct)
    {
        if (store.GetTable(QueueSchema.Queue) is null || store.GetTable(QueueSchema.QueueItem) is null) return;
        Guid? queueId;
        await using (var cmd = s.Command(
                         $"SELECT t.{s.Quote(QueueSchema.QueueIdColumn)} FROM {s.DataTable(Team)} t " +
                         $"JOIN {s.DataTable(QueueSchema.Queue)} q ON q.{s.Quote(QueueSchema.QueueIdColumn)} = t.{s.Quote(QueueSchema.QueueIdColumn)} " +
                         $"AND q.{s.Quote(OwnerId)} = t.{s.Quote("teamid")} WHERE t.{s.Quote("teamid")} = {IdParameter}"))
        {
            cmd.AddParameter(IdParameter, DbType.Guid, teamId);
            queueId = await cmd.ExecuteScalarAsync(ct) is Guid g ? g : null;
        }
        if (queueId is not { } queue) return;
        await using (var cmd = s.Command(
                         $"SELECT COUNT(*) FROM {s.DataTable(QueueSchema.QueueItem)} WHERE {s.Quote(QueueSchema.QueueIdColumn)} = {IdParameter} " +
                         $"AND {s.Quote("statecode")} = 0"))
        {
            cmd.AddParameter(IdParameter, DbType.Guid, queue);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0)
                throw XrmdFaultException.InvalidArgument(
                    $"The team {teamId} cannot be deleted: its default queue {queue} still holds active queue items. Remove or deactivate them first.");
        }
        await DeleteRecordAsync(s, caller, QueueSchema.Queue, queue, null, ct);
    }

    /// <summary>
    /// Plan 2.16, ruling A8: the system-managed access teams of a deleted record go with it, before its dependent rows. They are
    /// plain team Deletes of this class (no nested Delete message: the record's Delete is the message), so their memberships
    /// and shares go as for any team (<see cref="DeleteDependentRowsAsync"/>). Only for a table <c>team.regardingobjectid</c>
    /// targets (a target is never removed while a team refers to it). The record's row is update-locked first, then the teams
    /// are read on <c>UX_team_record_template</c> (its filter repeated, so the seek and its key-range lock use that index): the
    /// order AddUserToRecordTeam locks them in, so the two queue up on the record instead of deadlocking.
    /// </summary>
    private async Task DeleteAccessTeamsAsync(DataSession s, Caller caller, TableDefinition m, Guid id, CancellationToken ct)
    {
        var table = m.LogicalName!;
        if (table == Team
            || store.GetTable(Team)?.Attributes?.OfType<LookupColumn>()
                .FirstOrDefault(a => a.LogicalName == AccessTeamSchema.RegardingObjectId)?.Targets?.Contains(table) != true)
            return;
        var teams = new List<Guid>();
        await s.KeyLockAsync(table, id, ct);
        await using (var cmd = s.Command(
                         $"SELECT COUNT(*) FROM {s.DataTable(table)}{s.Dialect.LockHint(RowLock.UpdateRowRange)} " +
                         $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}; " +
                         $"SELECT {s.Quote(SystemAttributes.PrimaryIdName(Team))} FROM {s.DataTable(Team)}{s.Dialect.LockHint(RowLock.UpdateRange)} " +
                         $"WHERE {s.Quote(AccessTeamSchema.RegardingObjectId)} = {IdParameter} AND {s.Quote(AccessTeamSchema.TeamTemplateId)} IS NOT NULL " +
                         $"AND {s.Quote(AccessTeamSchema.SystemManaged)} = {s.Dialect.BooleanLiteral(true)} " +
                         // Final re-review N2: the teams of this table's record; a record of another table with the same id keeps its own.
                         $"AND {s.Quote(AccessTeamSchema.RegardingObjectId + TypeMapping.TypeColumnSuffix)} = @otc{s.Dialect.LockClause(RowLock.UpdateRange)}"))
        {
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            cmd.AddParameter("@otc", DbType.Int32, m.ObjectTypeCode ?? 0);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            await rd.NextResultAsync(ct);
            while (await rd.ReadAsync(ct)) teams.Add(rd.GetGuid(0));
        }
        if (teams.Count == 0) return;
        foreach (var team in teams) await DeleteRecordAsync(s, caller, Team, team, null, ct);
        security.InvalidateOnCommit(s);
    }

    /// <summary>The records a Delete and the deletes it cascades to are removing, shared by the nested Deletes of one session.</summary>
    private sealed class DeleteCascade
    {
        public HashSet<(string Entity, Guid Id)> Records { get; } = [];
        public int Depth { get; set; }
    }

    private static readonly ConditionalWeakTable<DataSession, DeleteCascade> DeleteCascades = new();

    /// <summary>
    /// Delete Restrict and Delete Cascade of the one-to-many relationships whose referenced entity is <paramref name="m"/>, before
    /// the record's row goes: first every Restrict (a record that points to it and is not itself being deleted refuses the
    /// Delete), then every Cascade (the records that point to it are deleted). The record must exist (404 otherwise), so that
    /// nothing is deleted on behalf of a missing record.
    /// </summary>
    private async Task ApplyDeleteCascadesAsync(DataSession s, Caller caller, TableDefinition m, Guid id, DeleteCascade deleting,
        NestedRequest? nested, CancellationToken ct)
    {
        var table = m.LogicalName!;
        var relationships = store.GetOneToManyDefinitions().Where(r => r.ReferencedEntity == table
            && r.CascadeConfiguration?.Delete is CascadeAction.Restrict or CascadeAction.Cascade).ToList();
        if (relationships.Count == 0) return;
        if (!await ExistsWhereAsync(s, table, SystemAttributes.PrimaryIdName(m), id, ct))
            throw XrmdFaultException.ObjectDoesNotExist(table, id);

        foreach (var r in relationships.Where(r => r.CascadeConfiguration!.Delete == CascadeAction.Restrict))
        {
            var children = await ReferencingRecordsAsync(s, r, id, ct);
            if (children.Any(child => !deleting.Records.Contains((r.ReferencingEntity!, child))))
                throw XrmdFaultException.InvalidArgument(
                    $"The {table} record {id} cannot be deleted: {r.ReferencingEntity} records still point to it through the " +
                    $"relationship '{r.SchemaName}', whose delete behavior is Restrict. Delete them or clear their '{r.ReferencingAttribute}' first.");
        }
        foreach (var r in relationships.Where(r => r.CascadeConfiguration!.Delete == CascadeAction.Cascade))
            foreach (var child in await ReferencingRecordsAsync(s, r, id, ct))
            {
                var referencing = r.ReferencingEntity!;
                if (deleting.Records.Contains((referencing, child))) continue; // a cycle or a self-reference
                // The rows of duplicaterecord (plan 2.15, ruling D15) and processtrigger (plan 2.14, ruling W3) refuse a Delete request:
                // they go with their job or their process in the core, so on purpose no Delete step runs for them (the table refuses
                // every client write, a step registered on it included). The same pattern for appmodulecomponent (plan 3.5 UI-1a final
                // review, I3): the table is import-only for requests (AppModuleSchema.ImportOnly), so its rows go with their app module
                // in the core, not through a nested Delete. Likewise postregarding (plan 3.5 UI-1g), which refuses every client write;
                // its posts then go as nested Deletes (their steps fire), through the nested Delete passed on below. The attachments of an email
                // (plan 4.1b) go as nested Deletes too (their steps fire); the guard accepts that marked cascade without Write on the email.
                if (nested is not null && referencing is not (DuplicateDetectionSchema.Record or WorkflowSchema.TriggerTable
                        or AppModuleSchema.Component or PostSchema.Regarding))
                {
                    // A core cascade (plan 2.14, ruling W5): its plug-in steps fire, but it triggers no workflow.
                    var cascaded = new Request { Name = "Delete", ["Target"] = new RecordReference { LogicalName = referencing, Id = child } };
                    CascadeHint.Mark(cascaded);
                    await nested(cascaded, ct);
                }
                else
                    await DeleteRecordAsync(s, caller, referencing, child, referencing == PostSchema.Regarding ? nested : null, ct);
            }
    }

    /// <summary>The ids of the records of <paramref name="r"/>'s referencing entity whose lookup holds <paramref name="id"/>.</summary>
    private async Task<List<Guid>> ReferencingRecordsAsync(DataSession s, OneToManyRelationship r, Guid id, CancellationToken ct)
    {
        var referencing = r.ReferencingEntity is null ? null : store.GetTable(r.ReferencingEntity);
        var lookup = referencing?.Attributes?.FirstOrDefault(a => a.LogicalName == r.ReferencingAttribute);
        if (referencing is null || lookup is null) return [];
        var (where, parameters) = LookupMatch(s.Dialect, lookup, r.ReferencedEntity!, id);
        await using var cmd = s.Command(
            $"SELECT {s.Quote(SystemAttributes.PrimaryIdName(referencing))} FROM {s.DataTable(referencing.LogicalName!)}{s.Dialect.LockHint(RowLock.UpdateRange)} " +
            $"WHERE {where}{s.Dialect.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameters(parameters);
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// Final re-review N4: the condition that <paramref name="lookup"/> names the record <paramref name="id"/> of
    /// <paramref name="referencedEntity"/>. A polymorphic lookup (Customer, Owner, regarding, a note's <c>objectid</c>, …) also
    /// matches its type column, as <see cref="ActivityParties.MarkPartyDeletedAsync"/> does: a record of another of its targets
    /// with the same id is not the deleted one.
    /// </summary>
    private (string Sql, DbParameter[] Parameters) LookupMatch(ISqlDialect d, ColumnDefinition lookup, string referencedEntity, Guid id)
    {
        var idParameter = new DetachedParameter(IdParameter, DbType.Guid, id);
        if (!TypeMapping.IsPolymorphicLookup(lookup)) return ($"{d.Quote(lookup.LogicalName!)} = {IdParameter}", [idParameter]);
        return ($"{d.Quote(lookup.LogicalName!)} = {IdParameter} AND {d.Quote(lookup.LogicalName + TypeMapping.TypeColumnSuffix)} = @otc",
            [idParameter, new DetachedParameter("@otc", DbType.Int32, TableOf(referencedEntity).ObjectTypeCode ?? 0)]);
    }

    private const string StepTable = "sdkmessageprocessingstep";

    /// <summary>
    /// Deletes the images of a deleted step (Dataverse deletes them with it) and returns their ids, to leave every solution.
    /// </summary>
    private static async Task<Guid[]> DeleteStepImagesAsync(DataSession s, Guid stepId, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"DELETE FROM {s.DataTable("sdkmessageprocessingstepimage")}{s.Dialect.ReturningInline(["sdkmessageprocessingstepimageid"], RowImage.Deleted)} " +
            $"WHERE sdkmessageprocessingstepid = {IdParameter}{s.Dialect.ReturningTrailing(["sdkmessageprocessingstepimageid"], RowImage.Deleted)}");
        cmd.AddParameter(IdParameter, DbType.Guid, stepId);
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return [.. ids];
    }

    /// <summary>
    /// The integrity rules of Delete that depend on the entity: users are never deleted; a business unit with users, teams or
    /// child business units, and a team that owns records, cannot be deleted.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) when a rule forbids the Delete.</exception>
    private async Task CheckDeleteRulesAsync(DataSession s, string entity, Guid id, CancellationToken ct)
    {
        switch (entity)
        {
            case SystemUser:
                throw XrmdFaultException.InvalidArgument("Users cannot be deleted; set isdisabled instead.");
            case BusinessUnit:
                if (await ExistsWhereAsync(s, SystemUser, BusinessUnitId, id, ct)
                    || await ExistsWhereAsync(s, Team, BusinessUnitId, id, ct)
                    || await ExistsWhereAsync(s, BusinessUnit, ParentBusinessUnitId, id, ct))
                    throw XrmdFaultException.InvalidArgument(
                        $"The business unit {id} has users, teams or child business units and cannot be deleted.");
                break;
            case Team:
                foreach (var owned in store.GetAllTables().Where(e =>
                             e.OwnershipType is TableOwnership.UserOwned or TableOwnership.TeamOwned
                             && e.Attributes?.Any(a => a.LogicalName == "owningteam") == true))
                    if (await ExistsWhereAsync(s, owned.LogicalName!, "owningteam", id, ct))
                        throw XrmdFaultException.InvalidArgument($"The team {id} owns records and cannot be deleted.");
                break;
        }
    }

    /// <summary>
    /// The rows that hang from a deleted record, deleted in the transaction of the Delete: the shares and field shares of the
    /// record and, for a team or a role, membership, role assignments, privileges, field security profile links and the shares
    /// and field shares a team holds; for a role or an app module, its app links (<c>appmoduleroles</c>); for a field security profile, its user and team links (its permissions go by the cascade
    /// of <c>lk_fieldpermission_fieldsecurityprofileid</c>); the record's associations in the
    /// intersect of every saved many-to-many relationship it is a side of (both columns of a self-referential one).
    /// </summary>
    private async Task DeleteDependentRowsAsync(DataSession s, string entity, Guid id, CancellationToken ct)
    {
        (string Table, string Column)[] dependents = entity switch
        {
            Team => [("teammembership", "teamid"), ("teamroles", "teamid"), (PrincipalObjectAccess, "principalid"),
                (FieldSecurityTables.TeamProfiles, "teamid"), (FieldSecurityTables.Share, FieldSecurityTables.SharePrincipalId)],
            // Users are never deleted (CheckDeleteRulesAsync); listed so that the rule holds should that change.
            SystemUser => [(FieldSecurityTables.UserProfiles, "systemuserid"), (FieldSecurityTables.Share, FieldSecurityTables.SharePrincipalId)],
            // appmoduleroles has no many-to-many metadata (ruling R1'), so the association cleanup below does not see it.
            Role => [("systemuserroles", "roleid"), ("teamroles", "roleid"), ("roleprivileges", "roleid"), (AppModuleSchema.RolesIntersect, "roleid")],
            AppModule => [(AppModuleSchema.RolesIntersect, "appmoduleid")],
            FieldSecurityTables.Profile => [(FieldSecurityTables.UserProfiles, FieldSecurityTables.ProfileId),
                (FieldSecurityTables.TeamProfiles, FieldSecurityTables.ProfileId)],
            _ => [],
        };
        var associations = store.GetManyToManyDefinitions().SelectMany(r =>
            (r.Entity1LogicalName == entity ? [(r.IntersectEntityName!, r.Entity1IntersectAttribute!)] : Array.Empty<(string, string)>())
            .Concat(r.Entity2LogicalName == entity ? [(r.IntersectEntityName!, r.Entity2IntersectAttribute!)] : Array.Empty<(string, string)>()));
        // Final review C2: the record's own shares and field shares are those of its id and its table; a record of another table
        // with the same id keeps its own. principalobjectaccess holds the table's object type code, a field share its logical name.
        if (store.GetTable(PrincipalObjectAccess) is not null)
            await ExecuteNonQueryAsync(s,
                $"DELETE FROM {s.DataTable(PrincipalObjectAccess)} WHERE {s.Quote("objectid")} = {IdParameter} AND {s.Quote("objecttypecode")} = @otc",
                [
                    new DetachedParameter(IdParameter, DbType.Guid, id),
                    new DetachedParameter("@otc", DbType.Int32, TableOf(entity).ObjectTypeCode ?? 0),
                ], ct);
        if (store.GetTable(FieldSecurityTables.Share) is not null)
            await ExecuteNonQueryAsync(s,
                $"DELETE FROM {s.DataTable(FieldSecurityTables.Share)} " +
                $"WHERE {s.Quote(FieldSecurityTables.ShareObjectId)} = {IdParameter} AND {s.Quote(FieldSecurityTables.ShareObjectTypeCodeColumn)} = @table",
                [
                    new DetachedParameter(IdParameter, DbType.Guid, id),
                    new DetachedParameter("@table", DbType.String, entity, 128),
                ], ct);
        foreach (var (table, column) in dependents.Concat(associations))
        {
            if (store.GetTable(table) is null) continue;
            await ExecuteNonQueryAsync(s, $"DELETE FROM {s.DataTable(table)} WHERE {s.Quote(column)} = {IdParameter}",
                [new DetachedParameter(IdParameter, DbType.Guid, id)], ct);
        }
    }

    /// <summary>Above this many records cleared by one RemoveLink, the table is woken at once rather than record by record.</summary>
    private const int RemoveLinkWakeBatch = 100;

    /// <summary>
    /// The delete cascade RemoveLink of the one-to-many relationships whose referenced entity is <paramref name="entity"/>
    /// (those of Customer columns, the seeded <c>contact_customer_contacts</c> and <c>account_primary_contact</c> among them): the records that pointed to the
    /// deleted one keep their row and lose the link (the lookup and its type column are set to null), in the transaction of
    /// the Delete. The regarding relationships to <c>activitypointer</c> are skipped (plan 2.11, A6).
    /// </summary>
    private async Task RemoveLinksAsync(DataSession s, string entity, Guid id, CancellationToken ct)
    {
        foreach (var r in store.GetOneToManyDefinitions().Where(r =>
                     r.ReferencedEntity == entity && r.CascadeConfiguration?.Delete == CascadeAction.RemoveLink))
        {
            // Plan 2.11 (A6, pre-flight C12): the regarding relationships to activitypointer (Account_ActivityPointers, …) carry
            // RemoveLink, but the pointer row is kept by ActivityPointerSync with its activity, which the Cascade of the activity
            // table's own relationship deletes: the cascade engine leaves it alone.
            if (r.ReferencingEntity == ActivitySchema.Pointer && r.ReferencingAttribute == ActivitySchema.RegardingObjectId) continue;
            var referencing = r.ReferencingEntity is null ? null : store.GetTable(r.ReferencingEntity);
            var lookup = referencing?.Attributes?.FirstOrDefault(a => a.LogicalName == r.ReferencingAttribute);
            if (referencing is null || lookup is null) continue;
            var referencingTable = r.ReferencingEntity!;
            // Plan 2.15: the rows whose cleared lookup a rule matches on get their codes again once it is cleared.
            var coded = await CoreSdkBridges.RowsReferencingAsync(duplicates.MatchCodes, s, referencing, r.ReferencingAttribute!, id, ct);
            var columns = TypeMapping.ToColumns(lookup).Select(c => c.Name).ToList();
            var (where, parameters) = LookupMatch(s.Dialect, lookup, entity, id);
            var update = $"UPDATE {s.DataTable(referencingTable)} SET {string.Join(", ", columns.Select(c => $"{s.Quote(c)} = NULL"))}";
            if (writes is null)
                await ExecuteNonQueryAsync(s, update + $" WHERE {where}", parameters, ct);
            else
            {
                // Plan 2.14 (W10): each cleared record is a write of it; past a batch, every job waiting on the table wakes at once.
                var cleared = new List<Guid>();
                string[] returned = [s.Quote(SystemAttributes.PrimaryIdName(referencing))];
                await using (var cmd = s.Command(update + $"{s.Dialect.ReturningInline(returned)} WHERE {where}{s.Dialect.ReturningTrailing(returned)}"))
                {
                    cmd.AddParameters(parameters);
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct)) cleared.Add(rd.GetGuid(0));
                }
                if (cleared.Count > RemoveLinkWakeBatch) await writes.OnWrittenAsync(s, referencingTable, null, ct);
                else
                    foreach (var record in cleared)
                        await writes.OnWrittenAsync(s, referencingTable, record, ct);
            }
            await CoreSdkBridges.RefreshManyMatchCodesAsync(duplicates.MatchCodes, s, referencing, coded, ct);
        }
    }
}
