using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Model.Security;

namespace Xrmd.Security;

/// <summary>
/// The privileges of a user, as RetrieveUserPrivileges returns them (plan 3.5 UI-1a task 3): one <see cref="PrivilegeGrant"/>
/// per privilege. A privilege of the user's own roles keeps its depth (the widest when several roles grant it); one that
/// comes only through the roles of an owner team is Basic, as Learn states. Computed from the same role and team-role
/// links as <see cref="SecurityCache"/>'s profiles, read from the database (no cache of its own).
/// </summary>
public static partial class UserPrivileges
{
    /// <summary>One privilege granted by one role: <paramref name="Direct"/> when the role is assigned to the user itself.</summary>
    public readonly record struct Row(Guid PrivilegeId, string PrivilegeName, DepthMask Mask, bool Direct);

    /// <summary>
    /// The privileges of the user <paramref name="userId"/> as <paramref name="session"/> sees them (SQL Server).
    /// A System Administrator role (its privileges are implicit) grants every privilege at Global.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist when the <c>systemuser</c> does not exist.</exception>
    public static async Task<IReadOnlyList<PrivilegeGrant>> GrantsForUserAsync(Guid userId, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var roles = SecurityCache.UserRoles(session.Dialect);
        await using var cmd = session.Command($"""
            SELECT businessunitid FROM data.systemuser WHERE systemuserid = @user;

            -- The roles of the user (direct) and of its owner teams, as the security profile reads them.
            SELECT x.direct, p.privilegeid, p.name, rp.privilegedepthmask
            FROM {roles} AS x
            JOIN data.roleprivileges AS rp ON rp.roleid = x.roleid
            JOIN data.privilege AS p ON p.privilegeid = rp.privilegeid
            UNION ALL
            SELECT x.direct, p.privilegeid, p.name, 8
            FROM {roles} AS x
            JOIN data.role AS r ON r.roleid = x.roleid AND r.roletemplateid = @administrator
            CROSS JOIN data.privilege AS p;
            """);
        cmd.AddParameter("@user", DbType.Guid, userId);
        cmd.AddParameter("@administrator", DbType.Guid, StandardRoles.SystemAdministrator);
        await using var rd = await cmd.ExecuteReaderAsync(ct);

        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist("systemuser", userId);
        var businessUnitId = rd.IsDBNull(0) ? Guid.Empty : rd.GetGuid(0);

        await rd.NextResultAsync(ct);
        var rows = new List<Row>();
        while (await rd.ReadAsync(ct))
        {
            if (rd.IsDBNull(2) || rd.IsDBNull(3)) continue;
            rows.Add(new Row(rd.GetGuid(1), rd.GetString(2), (DepthMask)rd.GetInt32(3), rd.GetBoolean(0)));
        }
        return CombineGrants(businessUnitId, rows);
    }

    /// <summary>
    /// One <see cref="PrivilegeGrant"/> per privilege of <paramref name="rows"/>, in the user's business unit
    /// <paramref name="businessUnitId"/>: the widest depth of the direct rows, or Basic when only team roles grant it; a
    /// privilege without depth is left out. Ordered by name.
    /// </summary>
    public static IReadOnlyList<PrivilegeGrant> CombineGrants(Guid businessUnitId, IEnumerable<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var result = new List<PrivilegeGrant>();
        foreach (var group in rows.GroupBy(r => r.PrivilegeId))
        {
            var direct = group.Where(r => r.Direct).Aggregate(DepthMask.None, (mask, r) => mask | r.Mask);
            var any = group.Aggregate(DepthMask.None, (mask, r) => mask | r.Mask);
            if (any == DepthMask.None) continue;
            var depth = direct != DepthMask.None ? DepthMasks.Highest(direct) : DepthMask.Basic;
            result.Add(new PrivilegeGrant(group.Key, group.First().PrivilegeName, businessUnitId, ToScope(depth)));
        }
        return [.. result.OrderBy(p => p.PrivilegeName, StringComparer.Ordinal)];
    }

    private static PrivilegeScope ToScope(DepthMask depth) => depth switch
    {
        DepthMask.Global => PrivilegeScope.Global,
        DepthMask.Deep => PrivilegeScope.Deep,
        DepthMask.Local => PrivilegeScope.Local,
        _ => PrivilegeScope.Basic,
    };
}
