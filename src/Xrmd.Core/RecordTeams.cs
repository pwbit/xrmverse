using System.Data;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.Core;

/// <summary>
/// Stage 30 of AddUserToRecordTeam and RemoveUserFromRecordTeam (plan 2.16, rulings A6, A7 and I1): the record's
/// system-managed access team for a template, its share of the record and its members, written directly by the core in the
/// request's transaction (no nested Create, GrantAccess or Associate message, so no plug-in step of those fires for them).
/// </summary>
/// <remarks>
/// The team of a (record, template) is found with UPDLOCK/HOLDLOCK on <c>team (regardingobjectid, teamtemplateid)</c>, so the
/// calls for one record and template queue up: the first creates the team, the others find it. Before that, the template
/// row and the record are locked until commit (<see cref="AccessTeamRules.DemandRecordTeamAsync"/>), so a template Delete, a
/// mask change or a record Delete waits for the call (or the call for it). The unique index <c>UX_team_record_template</c> is the
/// backstop: a Create that still loses the race reads the team once more.
/// </remarks>
internal sealed class RecordTeams(CoreOperations core, IMetadataStore store, SecurityAdministration security)
{
    private const string Team = AccessTeamSchema.Team;

    /// <summary>Test seam: runs right before the team is created, in the request's session (to force a lost race).</summary>
    internal Func<DataSession, Task>? BeforeCreateTeam { get; set; }

    /// <summary>
    /// AddUserToRecordTeam: finds or creates the team of the request's record for its template (on creation, with the share of
    /// the record carrying the template's mask read under the template's lock; when it exists, after checking that the rights
    /// of its share were checked by the guard) and adds the user when not a member yet.
    /// </summary>
    /// <returns>The team's id (<c>AccessTeamId</c>).</returns>
    /// <exception cref="XrmdFaultException">400 for a missing parameter; the faults of
    /// <see cref="AccessTeamRules.DemandRecordTeamAsync"/> (404, 400), <see cref="AccessTeamRules.DemandSameAsChecked"/> and
    /// <see cref="AccessTeamRules.DemandTeamShareChecked"/> (400).</exception>
    public async Task<Guid> AddUserAsync(DataSession s, Caller caller, OrganizationRequest request, CancellationToken ct)
    {
        var target = await LockedTargetAsync(s, caller, request, ct);
        var teamId = await FindOrCreateTeamAsync(s, caller, request, target, ct);
        if (!await IsMemberAsync(s, teamId, target.UserId, ct)) await security.AddMemberAsync(s, teamId, target.UserId, ct);
        return teamId;
    }

    /// <summary>
    /// RemoveUserFromRecordTeam: removes the user from the record's team for the template. A non-member is a no-op, and the team
    /// and its share stay when the last member leaves.
    /// </summary>
    /// <returns>The team's id, or <see cref="Guid.Empty"/> when the record has no team for the template yet.</returns>
    /// <exception cref="XrmdFaultException">As <see cref="AddUserAsync"/>.</exception>
    public async Task<Guid> RemoveUserAsync(DataSession s, Caller caller, OrganizationRequest request, CancellationToken ct)
    {
        var target = await LockedTargetAsync(s, caller, request, ct);
        if (await LockTeamAsync(s, target, ct) is not { } teamId) return Guid.Empty;
        await security.RemoveMemberAsync(s, teamId, target.UserId, ct);
        return teamId;
    }

    /// <summary>
    /// Steps 1–5 of ruling A6 on the final parameters, with the template and the record locked until commit; then what is about to
    /// be written must be what the <c>SecurityGuard</c> checked (steps 6 and 7).
    /// </summary>
    private async Task<AccessTeamRules.RecordTeamTarget> LockedTargetAsync(DataSession s, Caller caller, OrganizationRequest request,
        CancellationToken ct)
    {
        var (record, userId, templateId) = AccessTeamRules.RecordTeamParameters(request);
        var target = await AccessTeamRules.DemandRecordTeamAsync(s, store, record, userId, templateId, lockRows: true, ct);
        AccessTeamRules.DemandSameAsChecked(request, caller, target);
        return target;
    }

    private async Task<Guid> FindOrCreateTeamAsync(DataSession s, Caller caller, OrganizationRequest request,
        AccessTeamRules.RecordTeamTarget target, CancellationToken ct)
    {
        if (await LockTeamAsync(s, target, ct) is { } existing) return await CheckedTeamAsync(s, caller, request, target, existing, ct);
        if (BeforeCreateTeam is { } hook) await hook(s);
        XrmdFaultException lost;
        try
        {
            // Plan 2.7a, Task 6: under a savepoint where a failed statement aborts the transaction, which the recovery below reads on.
            return await s.RecoverableAsync(() => CreateTeamAsync(s, caller, target, ct), ct);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.DuplicateRecord)
        {
            lost = ex;
        }
        // Only the loss of the race on UX_team_record_template is recovered: the team of the (record, template) now exists. Any
        // other unique value the Create refused leaves no such team, and its fault stands.
        if (await LockTeamAsync(s, target, ct) is { } winner) return await CheckedTeamAsync(s, caller, request, target, winner, ct);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(lost);
        return Guid.Empty; // not reached
    }

    /// <summary>
    /// An existing team, locked: the member receives its share of the record, so the rights of that share, read under the lock,
    /// must be rights the <c>SecurityGuard</c> checked (<see cref="AccessTeamRules.DemandTeamShareChecked"/>).
    /// </summary>
    private static async Task<Guid> CheckedTeamAsync(DataSession s, Caller caller, OrganizationRequest request,
        AccessTeamRules.RecordTeamTarget target, Guid teamId, CancellationToken ct)
    {
        var share = await AccessTeamRules.TeamShareMaskAsync(s, target, lockRows: true, ct) ?? AccessRights.None;
        AccessTeamRules.DemandTeamShareChecked(request, caller, target, share);
        return teamId;
    }

    /// <summary>
    /// The team (Global Constraints of plan 2.16): named <c>{recordid}+{teamtemplateid}</c>, an access team (teamtype 1) the
    /// system manages, in the record's owning business unit, administered (and created) by the caller, SYSTEM for the internal
    /// caller; and its share of the record with the template's mask, written as GrantAccess writes it (audited as a share when
    /// auditing is on).
    /// </summary>
    private async Task<Guid> CreateTeamAsync(DataSession s, Caller caller, AccessTeamRules.RecordTeamTarget target, CancellationToken ct)
    {
        var acting = caller == Caller.Internal ? await core.ResolveUserAsync(await core.SystemUserIdAsync(s, ct), s, ct) : caller;
        var team = new Entity(Team)
        {
            ["name"] = $"{target.RecordId:D}+{target.TemplateId:D}",
            ["teamtype"] = new OptionSetValue(1),
            ["isdefault"] = false,
            [AccessTeamSchema.SystemManaged] = true,
            ["administratorid"] = new EntityReference("systemuser", acting.UserId),
            [AccessTeamSchema.RegardingObjectId] = new EntityReference(target.Table.LogicalName, target.RecordId),
            [AccessTeamSchema.TeamTemplateId] = new EntityReference(AccessTeamSchema.TeamTemplate, target.TemplateId),
        };
        if (target.OwningBusinessUnit is { } businessUnit) team["businessunitid"] = new EntityReference("businessunit", businessUnit);
        var teamId = await core.CreateAsync(s, acting, team, ct);
        await core.ShareAsync(s, acting, new EntityReference(target.Table.LogicalName, target.RecordId),
            new PrincipalAccess { Principal = new EntityReference(Team, teamId), AccessMask = target.Mask }, replace: false, ct);
        return teamId;
    }

    /// <summary>
    /// The team of the (record, template), locked (UPDLOCK, HOLDLOCK: also the key range when it is missing, so a concurrent
    /// call waits for this transaction); null when there is none.
    /// </summary>
    private static async Task<Guid?> LockTeamAsync(DataSession s, AccessTeamRules.RecordTeamTarget target, CancellationToken ct)
    {
        await s.KeyLockAsync(Team, target.RecordId, ct);
        await using var cmd = s.Command($"""
            SELECT {s.Quote(SystemAttributes.PrimaryIdName(Team))} FROM {s.DataTable(Team)}{s.Dialect.LockHint(RowLock.UpdateRange)}
            WHERE {s.Quote(AccessTeamSchema.RegardingObjectId)} = @_record AND {s.Quote(AccessTeamSchema.TeamTemplateId)} = @_template
              AND {s.Quote(AccessTeamSchema.TeamTemplateId)} IS NOT NULL{s.Dialect.LockClause(RowLock.UpdateRange)}
            """);
        cmd.AddParameter("@_record", DbType.Guid, target.RecordId);
        cmd.AddParameter("@_template", DbType.Guid, target.TemplateId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    private static async Task<bool> IsMemberAsync(DataSession s, Guid teamId, Guid userId, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT COUNT(*) FROM {s.DataTable("teammembership")} WHERE {s.Quote("teamid")} = @_team AND {s.Quote("systemuserid")} = @_user");
        cmd.AddParameter("@_team", DbType.Guid, teamId);
        cmd.AddParameter("@_user", DbType.Guid, userId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }
}
