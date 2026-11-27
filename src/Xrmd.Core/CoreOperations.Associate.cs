using System.Data;
using System.Data.Common;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Core.Validation;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;
using Xrmd.Security;

namespace Xrmd.Core;

/// <summary>
/// S.2e3b: the Assign, Associate and Disassociate bodies on the model. The SDK twins <c>AssignAsync</c> and <c>AssociateAsync</c>
/// (<c>Sdk/CoreOperations.Records.Sdk.cs</c>) read their parameters in and run the same bodies; <see cref="SecurityAdministration"/> and
/// the audit writer, which still take SDK references, are reached through <see cref="CoreSdkBridges"/>.
/// </summary>
public sealed partial class CoreOperations
{
    /// <summary>
    /// Assign: the Update of <c>ownerid</c> to <paramref name="assignee"/> (same rules as an Update of the owner: a
    /// <c>systemuser</c> or an owner <c>team</c>, the owning columns follow, modifiedon/modifiedby set).
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) when the entity has no owner or the assignee is not a
    /// systemuser or owner team; ObjectDoesNotExist (404) when the record or the user does not exist.</exception>
    public Task AssignRecordAsync(DataSession s, Caller caller, RecordReference target, RecordReference assignee, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(assignee);
        return UpdateRecordAsync(s, caller, new Record(target.LogicalName, target.Id) { [OwnerId] = assignee }, AuditActions.Assign, null,
            NoMailSecrets, null, ct);
    }

    /// <summary>
    /// Associate/Disassociate over a security relationship (<see cref="SecurityAdministration.FindRelationship"/>:
    /// <c>systemuserroles_association</c>, <c>teamroles_association</c>, <c>teammembership_association</c>, the target on
    /// either side) writes its intersect through <see cref="SecurityAdministration"/>. Over a saved many-to-many relationship
    /// (<see cref="IMetadataStore.GetRelationshipDefinition"/>) it inserts or deletes the intersect rows of (target, related record),
    /// see <see cref="AssociateManyToManyAsync"/>. Otherwise over a one-to-many relationship derived from a lookup
    /// (<see cref="IMetadataStore.GetOneToManyDefinitions"/>): the lookup of the N side is set to the 1 side
    /// (Associate) or cleared (Disassociate; only where it points to that record, the target must exist), with an Update
    /// per record on <paramref name="s"/> (one transaction). Owner lookups (ownerid/owninguser/owningteam) are excluded. The
    /// target is either the referenced record (the related records are the referencing ones) or the referencing record
    /// (then exactly one related record, the referenced one). The reads of Disassociate run as <paramref name="caller"/>.
    /// The privilege checks (Append, AppendTo) belong to the pipeline's security guard, not to this operation.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>Relationship '{name}' is not supported</c> for an unknown, many-to-many or
    /// read-only or owner relationship; 400 when the records are not of the relationship's entities; 404 for a missing record;
    /// for a security or many-to-many relationship, 412 DuplicateRecord for an association that already exists; for a security
    /// relationship, 400 for a role assigned to a team that is not an owner team;
    /// 403 PrivilegeDenied/AccessDenied (Read) when Disassociate reads a record the caller cannot see.</exception>
    public async Task AssociateRecordsAsync(DataSession s, Caller caller, RecordReference target, RelationshipName relationship,
        RecordReferenceCollection related, bool associate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(relationship);
        ArgumentNullException.ThrowIfNull(related);
        if (SecurityAdministration.FindRelationship(relationship.SchemaName) is { } securityRelationship)
        {
            await AssociateSecurityAsync(s, caller, securityRelationship, target, related, associate, ct);
            return;
        }
        if (store.GetRelationshipDefinition(relationship.SchemaName ?? "") is ManyToManyRelationship manyToMany)
        {
            await AssociateManyToManyAsync(s, caller, manyToMany, target, related, associate, ct);
            return;
        }

        var r = store.GetRelationshipDefinition(relationship.SchemaName ?? "") as OneToManyRelationship;
        var lookup = r is null ? null : store.GetTable(r.ReferencingEntity!)?.Attributes?.FirstOrDefault(a => a.LogicalName == r.ReferencingAttribute);
        // Ownership goes through Assign, never through a relationship.
        if (r is null || lookup is null || !EntityValidator.IsWritable(lookup, isCreate: false)
            || r.ReferencingAttribute is OwnerId or "owninguser" or "owningteam")
            throw XrmdFaultException.InvalidArgument($"Relationship '{relationship.SchemaName}' is not supported");
        TableOf(target.LogicalName);
        // 404 when missing; read as the caller, so a record it cannot see is 403 AccessDenied (Read).
        if (!associate) await RetrieveRecordAsync(s, caller, target.LogicalName, target.Id, null, ct);

        if (target.LogicalName == r.ReferencedEntity)
        {
            foreach (var record in related)
            {
                if (record.LogicalName != r.ReferencingEntity)
                    throw XrmdFaultException.InvalidArgument(
                        $"Entity '{record.LogicalName}' is not the referencing entity of relationship '{r.SchemaName}'.");
                await SetLookupAsync(record, target);
            }
        }
        else if (target.LogicalName == r.ReferencingEntity)
        {
            if (related.Count != 1 || related[0].LogicalName != r.ReferencedEntity)
                throw XrmdFaultException.InvalidArgument(
                    $"Relationship '{r.SchemaName}' from its referencing entity '{r.ReferencingEntity}' takes exactly one related " +
                    $"'{r.ReferencedEntity}' record.");
            await SetLookupAsync(target, related[0]);
        }
        else
        {
            throw XrmdFaultException.InvalidArgument($"Entity '{target.LogicalName}' is not part of relationship '{r.SchemaName}'.");
        }

        // Associate points the lookup of record to parent; Disassociate clears it only when it points to parent.
        async Task SetLookupAsync(RecordReference record, RecordReference parent)
        {
            var column = r.ReferencingAttribute!;
            if (!associate)
            {
                var current = (await RetrieveRecordAsync(s, caller, record.LogicalName, record.Id, new ColumnSelection { Columns = { column } }, ct))
                    .GetValue<RecordReference>(column);
                if (current is null || current.Id != parent.Id) return;
            }

            await UpdateRecordAsync(s, caller, new Record(record.LogicalName, record.Id)
            {
                [column] = associate ? new RecordReference { LogicalName = parent.LogicalName, Id = parent.Id } : null,
            }, ct);
        }
    }

    /// <summary>
    /// Associate/Disassociate over a saved many-to-many relationship: one intersect row per (target, related record), the
    /// target on either side (a self-referential relationship puts it in <c>Entity1IntersectAttribute</c>). Every record must
    /// exist (read with HOLDLOCK). Associate locks the pair (UPDLOCK, HOLDLOCK) and inserts it; Disassociate deletes it (in
    /// both orders for a self-referential relationship; nothing to delete is not an error).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when a record is not of the relationship's entities; 404 for a missing record;
    /// 412 DuplicateRecord when the pair is already associated.</exception>
    private async Task AssociateManyToManyAsync(DataSession s, Caller caller, ManyToManyRelationship r, RecordReference target,
        RecordReferenceCollection related, bool associate, CancellationToken ct)
    {
        var targetIsFirst = target.LogicalName == r.Entity1LogicalName;
        if (!targetIsFirst && target.LogicalName != r.Entity2LogicalName)
            throw XrmdFaultException.InvalidArgument($"Entity '{target.LogicalName}' is not part of relationship '{r.SchemaName}'.");
        var other = targetIsFirst ? r.Entity2LogicalName : r.Entity1LogicalName;
        if (related.FirstOrDefault(e => e.LogicalName != other) is { } wrong)
            throw XrmdFaultException.InvalidArgument(
                $"Relationship '{r.SchemaName}' associates a '{target.LogicalName}' with a '{other}', not a '{wrong.LogicalName}'.");

        var intersect = TableOf(r.IntersectEntityName);
        var table = $"{s.DataTable(intersect.LogicalName!)}";
        string first = s.Quote(r.Entity1IntersectAttribute!), second = $"{s.Quote(r.Entity2IntersectAttribute!)}";
        foreach (var record in related.Prepend(target))
        {
            var m = TableOf(record.LogicalName);
            if (!await ExistsWhereAsync(s, m.LogicalName!, SystemAttributes.PrimaryIdName(m), record.Id, ct))
                throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName!, record.Id);
        }

        var changed = new List<RecordReference>();
        foreach (var record in related)
        {
            var (one, two) = targetIsFirst ? (target.Id, record.Id) : (record.Id, target.Id);
            DbParameter[] parameters =
            [
                new DetachedParameter("@first", DbType.Guid, one),
                new DetachedParameter("@second", DbType.Guid, two),
                new DetachedParameter("@caller", DbType.Guid, caller.UserId),
            ];
            if (!associate)
            {
                var reverse = r.Entity1LogicalName == r.Entity2LogicalName ? $" OR ({first} = @second AND {second} = @first)" : "";
                // Only a pair that existed is a change: deleting nothing writes no audit row.
                if (await ExecuteNonQueryAsync(s, $"DELETE FROM {table} WHERE ({first} = @first AND {second} = @second){reverse}", parameters, ct) > 0)
                    changed.Add(record);
                continue;
            }

            int inserted;
            try
            {
                // Plan 2.7a, Task 6: the key lock of the pair, then one insert that checks for it under the range lock.
                await s.KeyLockAsync(intersect.LogicalName!, $"{one:N}:{two:N}", ct);
                inserted = await ExecuteNonQueryAsync(s, $"""
                    INSERT INTO {table} ({s.Quote(SystemAttributes.PrimaryIdName(intersect))}, {first}, {second},
                        createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                    SELECT {s.Dialect.NewGuid}, @first, @second, {s.Dialect.UtcNow}, @caller, {s.Dialect.UtcNow}, @caller, 0, 1
                    WHERE NOT EXISTS (SELECT 1 FROM {table}{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE {first} = @first AND {second} = @second)
                    """, parameters, ct);
            }
            catch (DbException ex) when (s.Dialect.Classify(ex) == DbErrorKind.UniqueViolation)
            {
                inserted = 0; // a concurrent Associate inserted the pair first
            }
            if (inserted != 1) throw XrmdFaultException.DuplicateAssociation();
            changed.Add(record);
        }

        // One row on the Target (ruling A1), in the transaction of the operation.
        await AuditRelationshipAsync(s, caller, target, r.SchemaName!,
            associate ? AuditActions.AssociateEntities : AuditActions.DisassociateEntities, changed, ct);
    }

    /// <summary>
    /// Writes the audit row of an Associate or Disassociate on <paramref name="target"/> (a relationship change of the
    /// records that really changed); nothing when none did, or when the table or the organization is not audited.
    /// </summary>
    private Task AuditRelationshipAsync(DataSession s, Caller caller, RecordReference target, string relationship, int action,
        IReadOnlyList<RecordReference> changed, CancellationToken ct) =>
        changed.Count == 0
            ? Task.CompletedTask
            : CoreSdkBridges.WriteRelationshipAuditAsync(audit, s, caller, action, target, relationship, changed, ct);

    /// <summary>Associate/Disassociate over a security relationship: one intersect row per (target, related record).</summary>
    /// <exception cref="XrmdFaultException">400 when a record is not of the relationship's entities, plus the faults of
    /// <see cref="SecurityAdministration"/>.</exception>
    private async Task AssociateSecurityAsync(DataSession s, Caller caller, SecurityRelationship relationship, RecordReference target,
        RecordReferenceCollection related, bool associate, CancellationToken ct)
    {
        if (!relationship.TryGetPairs(target, related, out var pairs))
            throw XrmdFaultException.InvalidArgument(
                $"Relationship '{relationship.SchemaName}' associates a '{relationship.Entity1}' with a '{relationship.Entity2}'.");
        var changed = new List<RecordReference>();
        foreach (var (first, second) in pairs)
        {
            if (relationship.SchemaName == SecurityAdministration.TeamMembership)
            {
                if (associate) await security.AddMemberAsync(s, first.Id, second.Id, ct);
                else await security.RemoveMemberAsync(s, first.Id, second.Id, ct);
            }
            else if (SecurityAdministration.IsProfileRelationship(relationship.SchemaName))
            {
                // The principal (systemuser or team) is Entity1, the field security profile Entity2.
                if (associate) await security.AddProfileAsync(s, first, second.Id, ct);
                else await security.RemoveProfileAsync(s, first, second.Id, ct);
            }
            else if (associate)
            {
                await security.AddRoleAsync(s, first, second.Id, ct);
                changed.Add(Other(first, second));
            }
            else if (await security.RemoveRoleAsync(s, first, second.Id, ct))
            {
                changed.Add(Other(first, second));
            }
        }

        // Role assignments are audited on the side the request names; team membership is not (ruling A2).
        var action = relationship.SchemaName switch
        {
            SecurityAdministration.SystemUserRoles => associate ? AuditActions.AssignRoleToUser : AuditActions.RemoveRoleFromUser,
            SecurityAdministration.TeamRoles => associate ? AuditActions.AssignRoleToTeam : AuditActions.RemoveRoleFromTeam,
            _ => 0,
        };
        if (action != 0) await AuditRelationshipAsync(s, caller, target, relationship.SchemaName, action, changed, ct);

        // The record on the other side of the pair from the target.
        RecordReference Other(RecordReference first, RecordReference second) =>
            first.Id == target.Id && first.LogicalName == target.LogicalName ? second : first;
    }
}
