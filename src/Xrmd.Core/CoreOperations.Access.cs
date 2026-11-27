using System.Data;
using System.Data.Common;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;
using Xrmd.Model.Security;
using Xrmd.Pipeline;
using Xrmd.Security;

namespace Xrmd.Core;

/// <summary>
/// S.2e3c1 (Task 3, Decisions 1 and 5): the share and record team messages on the model request (GrantAccess, ModifyAccess,
/// RevokeAccess, AddUserToRecordTeam, RemoveUserFromRecordTeam) and the share bodies on model values (<see cref="ShareRecordAsync"/>,
/// <see cref="RevokeRecordAccessAsync"/>). The SDK twin <c>ShareAsync</c> keeps today's name for the
/// caller still on the SDK (the migration's sharing phase). The audit writer and the security rules stay on the SDK until their
/// steps: they are reached through <see cref="CoreSdkBridges"/>.
/// </summary>
public sealed partial class CoreOperations
{
    /// <summary>The rights a share may carry (<c>principalobjectaccess.accessrightsmask</c>); Create is not one of them.</summary>
    private const AccessMask ShareableRights = AccessMask.ReadAccess | AccessMask.WriteAccess | AccessMask.AppendAccess
        | AccessMask.AppendToAccess | AccessMask.DeleteAccess | AccessMask.ShareAccess | AccessMask.AssignAccess;

    /// <summary>True for a message of <see cref="ExecuteAccessAsync"/> (canonical case).</summary>
    private static bool IsAccessMessage(string canonicalName) =>
        canonicalName is "GrantAccess" or "ModifyAccess" or "RevokeAccess" or AccessTeamRules.AddUserToRecordTeam
            or AccessTeamRules.RemoveUserFromRecordTeam;

    /// <summary>
    /// Stage 30 of GrantAccess, ModifyAccess, RevokeAccess, AddUserToRecordTeam and RemoveUserFromRecordTeam
    /// (<see cref="Core.RecordTeams"/>) on the model request, named <paramref name="canonicalName"/>. The reads and checks come in the
    /// order of the SDK branches they replace, so the same fault comes first.
    /// </summary>
    /// <exception cref="InvalidOperationException">Server values (written on a Create only).</exception>
    /// <exception cref="XrmdFaultException">400 for a missing parameter or one of another type (named with today's SDK type); the
    /// faults of the bodies.</exception>
    private async Task<Response> ExecuteAccessAsync(Request request, string canonicalName, CoreCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);
        if (call.ServerSetValues is not null)
            throw new InvalidOperationException($"Server values are written on a Create only, not on '{request.Name}'.");
        var (caller, session) = (call.Caller, call.Session);
        switch (canonicalName)
        {
            case "GrantAccess":
            case "ModifyAccess":
                var replace = canonicalName == "ModifyAccess";
                await ShareRecordAsync(session, caller, Parameter<RecordReference>(request, "Target", "EntityReference"),
                    Parameter<PrincipalAccessValue>(request, "PrincipalAccess", "PrincipalAccess"), replace, ct);
                return new Response { Name = canonicalName };
            case "RevokeAccess":
                await RevokeRecordAccessAsync(session, caller, Parameter<RecordReference>(request, "Target", "EntityReference"),
                    Parameter<RecordReference>(request, "Revokee", "EntityReference"), ct);
                return new Response { Name = canonicalName };
            case AccessTeamRules.AddUserToRecordTeam:
                return new Response { Name = canonicalName, Results = { ["AccessTeamId"] = await RecordTeams.AddUserAsync(session, caller, request, ct) } };
            case AccessTeamRules.RemoveUserFromRecordTeam:
                return new Response { Name = canonicalName, Results = { ["AccessTeamId"] = await RecordTeams.RemoveUserAsync(session, caller, request, ct) } };
            default:
                throw new ArgumentOutOfRangeException(nameof(canonicalName), canonicalName, "Not an access message.");
        }
    }

    /// <summary>
    /// GrantAccess (<paramref name="replace"/> false: the rights are added to the share) and ModifyAccess (true: the share gets
    /// exactly these rights): the <c>principalobjectaccess</c> row of the record and the principal, created when missing.
    /// The row is read with UPDLOCK/HOLDLOCK, so concurrent shares of the same pair queue up; an insert that still loses the
    /// race against the unique index is retried once as an update.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an entity that is not user or team owned, a principal that is not a
    /// systemuser or team, or an invalid access mask; 404 when the record or the principal does not exist.</exception>
    public async Task ShareRecordAsync(DataSession s, Caller caller, RecordReference target, PrincipalAccessValue access, bool replace,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(access);
        var principal = access.Principal ?? throw XrmdFaultException.InvalidArgument("Required parameter 'PrincipalAccess.Principal' is missing.");
        var mask = access.AccessMask;
        var m = ShareableTable(target, principal);
        // Plan 3.4, ruling E9 (ElasticWriteRules.DemandShareable): an elastic table has no shares.
        ElasticTables.DemandNotElastic(m, "Record shares (GrantAccess, ModifyAccess)");
        if (mask == 0 || (mask & ~ShareableRights) != 0) throw XrmdFaultException.InvalidArgument($"Invalid access mask {mask}.");
        await EnsureShareRecordsExistAsync(s, m, target.Id, principal, ct);

        for (var attempt = 0; ; attempt++)
        {
            if (await UpdateShareAsync(s, target.Id, m.ObjectTypeCode ?? 0, principal.Id, mask, replace, ct) is { } old)
            {
                var now = replace ? mask : old | mask;
                // A grant of rights the principal already holds changes nothing: no row.
                if (now != old) await AuditShareAsync(s, caller, replace ? AuditActions.ModifyShare : AuditActions.Share, target, principal, old, now, ct);
                return;
            }
            try
            {
                // Plan 2.7a, Task 6: under a savepoint where a failed statement aborts the transaction (the next round reads on).
                await s.RecoverableAsync(() => ExecuteNonQueryAsync(s, $"""
                    INSERT INTO {s.DataTable(PrincipalObjectAccess)}
                        ({s.Quote(PrincipalObjectAccess + "id")}, principalid, principaltypecode, objectid, objecttypecode, accessrightsmask,
                         inheritedaccessrightsmask, changedon)
                    VALUES ({s.Dialect.NewGuid}, @principal, @principaltype, {IdParameter}, @otc, @mask, 0, {s.Dialect.UtcNow})
                    """,
                    [
                        new DetachedParameter("@principal", DbType.Guid, principal.Id),
                        new DetachedParameter("@principaltype", DbType.Int32, principal.LogicalName == Team ? 9 : 8),
                        new DetachedParameter(IdParameter, DbType.Guid, target.Id),
                        new DetachedParameter("@otc", DbType.Int32, m.ObjectTypeCode ?? 0),
                        new DetachedParameter("@mask", DbType.Int32, (int)mask),
                    ], ct), ct);
                await AuditShareAsync(s, caller, replace ? AuditActions.ModifyShare : AuditActions.Share, target, principal, AccessMask.None, mask, ct);
                return;
            }
            catch (DbException ex) when (s.Dialect.Classify(ex) == DbErrorKind.UniqueViolation && attempt == 0)
            {
                // A concurrent share inserted the row first: the next round updates it.
            }
        }
    }

    /// <summary>RevokeAccess: deletes the share of the record with the principal (nothing to delete is not an error).</summary>
    /// <exception cref="XrmdFaultException">400 for an entity that is not user or team owned or a principal that is not a
    /// systemuser or team; 404 when the record or the principal does not exist.</exception>
    public async Task RevokeRecordAccessAsync(DataSession s, Caller caller, RecordReference target, RecordReference revokee, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(revokee);
        var m = ShareableTable(target, revokee);
        await EnsureShareRecordsExistAsync(s, m, target.Id, revokee, ct);
        await using var cmd = s.Command(
            $"DELETE FROM {s.DataTable(PrincipalObjectAccess)}{s.Dialect.ReturningInline(["accessrightsmask"], RowImage.Deleted)} " +
            $"WHERE objectid = {IdParameter} AND objecttypecode = @otc AND principalid = @principal{s.Dialect.ReturningTrailing(["accessrightsmask"], RowImage.Deleted)}");
        cmd.AddParameter(IdParameter, DbType.Guid, target.Id);
        // Final review C2: the record is its id and its table; another table's record with the same id keeps its shares.
        cmd.AddParameter("@otc", DbType.Int32, m.ObjectTypeCode ?? 0);
        cmd.AddParameter("@principal", DbType.Guid, revokee.Id);
        var old = AccessMask.None;
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) old |= rd.IsDBNull(0) ? AccessMask.None : (AccessMask)rd.GetInt32(0);
        // Nothing to revoke is not a change: no row.
        if (old != AccessMask.None) await AuditShareAsync(s, caller, AuditActions.Unshare, target, revokee, old, AccessMask.None, ct);
    }

    /// <summary>
    /// The share's audit row on the shared record (ruling A1): the action follows the message (GrantAccess 14, ModifyAccess 48,
    /// RevokeAccess 49); the rights before and after are in the share change (<see cref="CoreSdkBridges.WriteShareAuditAsync"/>).
    /// </summary>
    private Task AuditShareAsync(DataSession s, Caller caller, int action, RecordReference target, RecordReference principal,
        AccessMask old, AccessMask now, CancellationToken ct) =>
        CoreSdkBridges.WriteShareAuditAsync(audit, s, caller, action, target, principal, old, now, ct);

    /// <summary>The table of a shared record: user or team owned, and the principal a systemuser or a team.</summary>
    /// <exception cref="XrmdFaultException">400 otherwise.</exception>
    private TableDefinition ShareableTable(RecordReference target, RecordReference principal)
    {
        var m = TableOf(target.LogicalName);
        if (m.OwnershipType is not (TableOwnership.UserOwned or TableOwnership.TeamOwned))
            throw XrmdFaultException.InvalidArgument($"Entity '{m.LogicalName}' is not user or team owned and cannot be shared.");
        if (principal.LogicalName is not (SystemUser or Team))
            throw XrmdFaultException.InvalidArgument(
                $"Principal of type '{principal.LogicalName}' is not supported: a share must name a {SystemUser} or a {Team}.");
        return m;
    }

    /// <summary>The shared record and the principal must exist (read with HOLDLOCK in the share's transaction).</summary>
    /// <exception cref="XrmdFaultException">404 for the first one missing.</exception>
    private async Task EnsureShareRecordsExistAsync(DataSession s, TableDefinition m, Guid id, RecordReference principal, CancellationToken ct)
    {
        if (!await ExistsWhereAsync(s, m.LogicalName!, SystemAttributes.PrimaryIdName(m), id, ct))
            throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName!, id);
        var principalTable = TableOf(principal.LogicalName);
        if (!await ExistsWhereAsync(s, principalTable.LogicalName!, SystemAttributes.PrimaryIdName(principalTable), principal.Id, ct))
            throw XrmdFaultException.ObjectDoesNotExist(principalTable.LogicalName!, principal.Id);
    }

    /// <summary>
    /// Locks the share of the pair (UPDLOCK, HOLDLOCK: also the key range when it is missing) and, when it exists, sets its
    /// mask (<paramref name="replace"/>) or adds <paramref name="mask"/> to it; the mask it had before, or null when there is no share yet.
    /// The record is its id and its table's <paramref name="objectTypeCode"/> (final review C2): a record of another table with the
    /// same id has its own share, which this never reads or changes.
    /// </summary>
    private static async Task<AccessMask?> UpdateShareAsync(DataSession s, Guid objectId, int objectTypeCode, Guid principalId, AccessMask mask,
        bool replace, CancellationToken ct)
    {
        // Plan 2.7a, Task 6: the batch became the key lock of the share, its locking read, then its update.
        var d = s.Dialect;
        var table = s.DataTable(PrincipalObjectAccess);
        await s.KeyLockAsync(PrincipalObjectAccess, $"{objectId:N}:{objectTypeCode}:{principalId:N}", ct);
        int? current;
        await using (var read = s.Command($"""
            SELECT {d.IsNull("accessrightsmask", "0")} FROM {table}{d.LockHint(RowLock.UpdateRange)}
            WHERE objectid = {IdParameter} AND objecttypecode = @otc AND principalid = @principal{d.LockClause(RowLock.UpdateRange)}
            """))
        {
            read.AddParameter(IdParameter, DbType.Guid, objectId);
            read.AddParameter("@otc", DbType.Int32, objectTypeCode);
            read.AddParameter("@principal", DbType.Guid, principalId);
            current = await read.ExecuteScalarAsync(ct) is { } value and not DBNull ? Convert.ToInt32(value) : null;
        }
        if (current is null) return null;
        await using var cmd = s.Command($"""
            UPDATE {table} SET accessrightsmask = @mask, changedon = {d.UtcNow}
            WHERE objectid = {IdParameter} AND objecttypecode = @otc AND principalid = @principal
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, objectId);
        cmd.AddParameter("@otc", DbType.Int32, objectTypeCode);
        cmd.AddParameter("@principal", DbType.Guid, principalId);
        cmd.AddParameter("@mask", DbType.Int32, replace ? (int)mask : current.Value | (int)mask);
        await cmd.ExecuteNonQueryAsync(ct);
        return (AccessMask)current.Value;
    }
}
