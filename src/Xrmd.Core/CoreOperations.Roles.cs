using Xrmd.Abstractions;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Model.Security;
using Xrmd.Pipeline;
using Xrmd.Security;
using Xrmd.Solution;

namespace Xrmd.Core;

/// <summary>
/// S.2e3c1 (Task 4, Decisions 1 and 5): the role privilege messages (AddPrivilegesRole, ReplacePrivilegesRole, RemovePrivilegeRole and,
/// outside the pipeline, RetrieveUserPrivileges), RetrieveExchangeRate (<see cref="CurrencyMessages"/>) and RetrieveEntityChanges
/// (<see cref="Core.ChangeTracker"/>) on the model request. The privileges are model <see cref="PrivilegeGrant"/>s; the role's privileges
/// before and after a change and the audit row still go through the SDK members of <see cref="SecurityAdministration"/> and the audit
/// writer, reached through <see cref="CoreSdkBridges"/> (Decision 6). The SDK twin <c>RetrieveUserPrivilegesAsync</c> keeps today's
/// name for the organization service.
/// </summary>
public sealed partial class CoreOperations
{
    /// <summary>True for a message of <see cref="ExecuteRoleCurrencyOrChangeAsync"/> (canonical case).</summary>
    private static bool IsRoleCurrencyOrChangeMessage(string canonicalName) =>
        canonicalName is "AddPrivilegesRole" or "ReplacePrivilegesRole" or "RemovePrivilegeRole"
            or CurrencyMessages.MessageName or ChangeTracker.MessageName;

    /// <summary>
    /// Stage 30 of AddPrivilegesRole (the depth of each privilege listed is set), ReplacePrivilegesRole (the role gets exactly these
    /// privileges), RemovePrivilegeRole, RetrieveExchangeRate and RetrieveEntityChanges on the model request, named
    /// <paramref name="canonicalName"/>. The reads and checks come in the order of the SDK branches they replace, so the same fault
    /// comes first.
    /// </summary>
    /// <exception cref="InvalidOperationException">Server values (written on a Create only).</exception>
    /// <exception cref="XrmdFaultException">400 for a missing parameter or one of another type (named with today's SDK type); the
    /// faults of the bodies.</exception>
    private async Task<Response> ExecuteRoleCurrencyOrChangeAsync(Request request, string canonicalName, CoreCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);
        if (call.ServerSetValues is not null)
            throw new InvalidOperationException($"Server values are written on a Create only, not on '{request.Name}'.");
        var (caller, session) = (call.Caller, call.Session);
        switch (canonicalName)
        {
            case CurrencyMessages.MessageName:
                return await CurrencyMessages.ExecuteAsync(request, session, ct);
            case ChangeTracker.MessageName:
                UnmaskedData.Refuse(request);
                return ChangeTracker.ToResponse(await ChangeTracker.RetrieveAsync(session, caller, ChangeTracker.ReadRequest(request), ct));
            case "AddPrivilegesRole":
            case "ReplacePrivilegesRole":
                var replace = canonicalName == "ReplacePrivilegesRole";
                var roleId = GuidParameter(request, "RoleId");
                var requested = SecurityAdministration.FromGrants(Parameter<PrivilegeGrant[]>(request, "Privileges", "RolePrivilege[]"));
                await DemandRowUpdateAllowedAsync(session, caller, Role, roleId, ct);
                var auditRole = await RolePrivilegesBeforeAsync(session, roleId, ct);
                await security.SetRolePrivilegesAsync(session, roleId, requested, replace, ct);
                await MarkChangedAsync(session, caller, ComponentType.Role, roleId, ct);
                await AuditRolePrivilegesAsync(session, caller, roleId, auditRole,
                    replace ? AuditActions.ReplacePrivilegesInRole : AuditActions.AddPrivilegesToRole, ct);
                return new Response { Name = canonicalName };
            case "RemovePrivilegeRole":
                var removedFrom = GuidParameter(request, "RoleId");
                await DemandRowUpdateAllowedAsync(session, caller, Role, removedFrom, ct);
                var removeBefore = await RolePrivilegesBeforeAsync(session, removedFrom, ct);
                await security.RemoveRolePrivilegeAsync(session, removedFrom, GuidParameter(request, "PrivilegeId"), ct);
                await MarkChangedAsync(session, caller, ComponentType.Role, removedFrom, ct);
                await AuditRolePrivilegesAsync(session, caller, removedFrom, removeBefore, AuditActions.RemovePrivilegesFromRole, ct);
                return new Response { Name = canonicalName };
            default:
                throw new ArgumentOutOfRangeException(nameof(canonicalName), canonicalName, "Not a role privilege, currency or change tracking message.");
        }
    }

    /// <summary>
    /// RetrieveUserPrivileges (plan 3.5 UI-1a task 3) on the model request: the user's privileges (<see cref="UserPrivileges"/>, as
    /// <see cref="PrivilegeGrant"/>s) once <paramref name="caller"/> has read the <c>systemuser</c> row as a Retrieve would (403 without
    /// read access, 404 when the user does not exist).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 without <c>UserId</c>; the Retrieve's faults.</exception>
    public async Task<Response> RetrieveUserPrivilegesAsync(Request request, Caller caller, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = request.Parameters.TryGetValue("UserId", out var value) && value is Guid id && id != Guid.Empty
            ? id
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'UserId' of request '{request.Name}' is missing.");
        await RetrieveRecordAsync(session, caller, SystemUser, userId, new ColumnSelection { Columns = { SystemUserIdColumn } }, ct);
        return new Response
        {
            Name = "RetrieveUserPrivileges",
            Results = { ["RolePrivileges"] = (await UserPrivileges.GrantsForUserAsync(userId, session, ct)).ToArray() },
        };
    }

    /// <summary>A required <see cref="Guid"/> parameter of the model <paramref name="request"/>; InvalidArgument (400) when missing or of
    /// another type, with today's text.</summary>
    internal static Guid GuidParameter(Request request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && value is Guid id
            ? id
            : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.Name}' is missing or not a Guid.");

    /// <summary>The role's privileges before a change, read only when the <c>role</c> table is audited (null otherwise).</summary>
    private async Task<IReadOnlyList<PrivilegeGrant>?> RolePrivilegesBeforeAsync(DataSession s, Guid roleId, CancellationToken ct) =>
        await CoreSdkBridges.IsAuditedAsync(audit, s, TableOf(Role), ct) ? await security.RoleGrantsAsync(s, roleId, ct) : null;

    /// <summary>
    /// The audit row of a role privilege message on the role (ruling A1): the privileges before (<paramref name="old"/>, null when
    /// the table is not audited) and after; no row when the role's privileges did not change.
    /// </summary>
    private async Task AuditRolePrivilegesAsync(DataSession s, Caller caller, Guid roleId, IReadOnlyList<PrivilegeGrant>? old, int action,
        CancellationToken ct)
    {
        if (old is null) return;
        var now = await security.RoleGrantsAsync(s, roleId, ct);
        var same = old.Count == now.Count && old.Zip(now).All(p => p.First.PrivilegeId == p.Second.PrivilegeId && p.First.Depth == p.Second.Depth);
        if (same) return;
        await CoreSdkBridges.WriteRolePrivilegesAuditAsync(audit, s, caller, action, roleId, old, now, ct);
    }
}
