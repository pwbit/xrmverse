using System.Data;
using System.Data.Common;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.Core;

/// <summary>
/// Maps <see cref="ICurrentUser.ObjectId"/> to the caller's <c>systemuser</c> (by <c>azureactivedirectoryobjectid</c>),
/// which must be enabled and have at least one role (direct or from an owner team, per the <see cref="SecurityCache"/>),
/// then applies the impersonation the request asks for (<see cref="ICurrentUser.CallerObjectId"/>, else
/// <see cref="ICurrentUser.CallerId"/>, or the SOAP <c>CallerId</c> header through <see cref="ImpersonateAsync"/>).
/// Scoped: the result is cached for the instance. Every front end requires this mapping before doing anything.
/// </summary>
/// <remarks>
/// Impersonation: the authenticated user must hold <c>prvActOnBehalfOfAnotherUser</c> from a role assigned directly (or a
/// direct System Administrator role); the impersonated user must exist, not be SYSTEM, be enabled and have a role. The
/// result is <c>Caller(impersonated, its business unit, ImpersonatorId = authenticated)</c>; impersonating oneself is no
/// impersonation.
/// </remarks>
public sealed class CallerResolver(IDbConnectionFactory factory, ICurrentUser currentUser, SecurityCache cache)
{
    private const string SystemUser = "systemuser";
    private const string BySystemUserId = "systemuserid";
    private const string ByObjectId = "azureactivedirectoryobjectid";

    /// <summary>The authenticated user (no impersonation).</summary>
    private Caller? _authenticated;

    /// <summary>The caller of the request (the impersonated user when impersonating).</summary>
    private Caller? _caller;

    /// <summary>The authenticated user (member, enabled, with roles), then the impersonation of the HTTP headers if present. Cached.</summary>
    /// <exception cref="XrmdFaultException">403 NotMemberOfOrganization when no systemuser matches; 403 UserDisabled
    /// (<c>0x80040225</c>) when it is disabled; 403 UserNotAssignedRoles (<c>0x80042F09</c>) when it has no role; 400 for an
    /// impersonation header that is not a GUID; the faults of <see cref="ImpersonateAsync"/>.</exception>
    public async Task<Caller> ResolveAsync(CancellationToken ct = default)
    {
        if (_caller is not null) return _caller;
        await using var conn = await factory.OpenAsync(ct);
        return await ResolveAsync(conn, ct);
    }

    /// <summary>As <see cref="ResolveAsync(CancellationToken)"/>, on an already open connection.</summary>
    /// <exception cref="XrmdFaultException">As <see cref="ResolveAsync(CancellationToken)"/>.</exception>
    public async Task<Caller> ResolveAsync(DbConnection conn, CancellationToken ct = default)
    {
        if (_caller is not null) return _caller;
        var authenticated = await AuthenticatedAsync(conn, ct);
        return _caller = await ImpersonateAsync(conn, authenticated, currentUser.CallerId, currentUser.CallerObjectId, ct);
    }

    /// <summary>
    /// Impersonation from the SOAP <c>CallerId</c> header (read before the body): replaces the cached caller with
    /// <paramref name="systemUserId"/> (or, winning over it, the user with Entra object id <paramref name="objectId"/>)
    /// impersonated by the authenticated user. Neither given (null or <see cref="Guid.Empty"/>): the caller of
    /// <see cref="ResolveAsync(CancellationToken)"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 CannotActOnBehalfOfAnotherUser (<c>0x8004A110</c>) when the authenticated user
    /// lacks <c>prvActOnBehalfOfAnotherUser</c> held directly, or the target is SYSTEM; 404 ObjectDoesNotExist for an unknown
    /// <paramref name="systemUserId"/>; 403 NotMemberOfOrganization for an unknown <paramref name="objectId"/>; 403
    /// UserDisabled or UserNotAssignedRoles for the impersonated user; the faults of
    /// <see cref="ResolveAsync(CancellationToken)"/>.</exception>
    public async Task<Caller> ImpersonateAsync(Guid? systemUserId, Guid? objectId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await ResolveAsync(conn, ct);
        if (IsNone(systemUserId) && IsNone(objectId)) return _caller!;
        return _caller = await ImpersonateAsync(conn, _authenticated!, systemUserId, objectId, ct);
    }

    private async Task<Caller> AuthenticatedAsync(DbConnection conn, CancellationToken ct)
    {
        if (_authenticated is not null) return _authenticated;
        Guid objectId;
        try
        {
            objectId = currentUser.ObjectId;
        }
        catch (InvalidOperationException ex)
        {
            throw new XrmdFaultException(ErrorCodes.NotMemberOfOrganization, $"The caller could not be identified: {ex.Message}", 403);
        }

        var (caller, disabled) = await FindUserAsync(conn, ByObjectId, objectId, ct) ?? throw NotMember(objectId);
        await DemandEnabledWithRolesAsync(caller.UserId, disabled, ct);
        return _authenticated = caller;
    }

    private async Task<Caller> ImpersonateAsync(DbConnection conn, Caller authenticated, Guid? systemUserId, Guid? objectId,
        CancellationToken ct)
    {
        if (IsNone(systemUserId) && IsNone(objectId)) return authenticated;
        if (!(await cache.GetProfileAsync(authenticated.UserId, ct)).DirectPrivileges.Contains(PrivilegeNames.ActOnBehalfOfAnotherUser))
            throw XrmdFaultException.CannotActOnBehalfOfAnotherUser();

        var (target, disabled) = !IsNone(objectId)
            ? await FindUserAsync(conn, ByObjectId, objectId!.Value, ct) ?? throw NotMember(objectId.Value)
            : await FindUserAsync(conn, BySystemUserId, systemUserId!.Value, ct)
              ?? throw XrmdFaultException.ObjectDoesNotExist(SystemUser, systemUserId.Value);
        if (target.UserId == authenticated.UserId) return authenticated;
        if (target.UserId == await cache.SystemUserIdAsync(ct)) throw XrmdFaultException.CannotActOnBehalfOfAnotherUser();
        await DemandEnabledWithRolesAsync(target.UserId, disabled, ct);
        return target with { ImpersonatorId = authenticated.UserId };
    }

    private static bool IsNone(Guid? id) => id is null || id == Guid.Empty;

    /// <summary>The user whose <paramref name="column"/> (a constant of this class) is <paramref name="value"/>, and whether it is disabled; null when none.</summary>
    /// <exception cref="XrmdFaultException">403 NotMemberOfOrganization when it has no business unit.</exception>
    private static async Task<(Caller User, bool Disabled)?> FindUserAsync(DbConnection conn, string column, Guid value, CancellationToken ct)
    {
        var d = SqlDialects.For(conn);
        await using var cmd = conn.Command(
            $"SELECT systemuserid, businessunitid, isdisabled FROM {d.DataTable(SystemUser)} WHERE {d.Quote(column)} = @value");
        cmd.AddParameter("@value", DbType.Guid, value);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        if (rd.IsDBNull(1))
            throw new XrmdFaultException(ErrorCodes.NotMemberOfOrganization, $"The {SystemUser} {rd.GetGuid(0)} has no business unit.", 403);
        return (new Caller(rd.GetGuid(0), rd.GetGuid(1)), !rd.IsDBNull(2) && rd.GetBoolean(2));
    }

    /// <exception cref="XrmdFaultException">403 UserDisabled; 403 UserNotAssignedRoles.</exception>
    private async Task DemandEnabledWithRolesAsync(Guid userId, bool disabled, CancellationToken ct)
    {
        if (disabled) throw XrmdFaultException.UserDisabled(userId);
        if (!(await cache.GetProfileAsync(userId, ct)).HasRoles) throw XrmdFaultException.UserNotAssignedRoles(userId);
    }

    private static XrmdFaultException NotMember(Guid objectId) =>
        new(ErrorCodes.NotMemberOfOrganization,
            $"The user with ObjectId = {objectId} is not a member of the organization (no {SystemUser} found).", 403);
}
