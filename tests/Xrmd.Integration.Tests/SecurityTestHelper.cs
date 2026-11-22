using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Core;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Users, business units, teams and roles written with direct INSERTs (no pipeline, no integrity rules); every helper
/// ends with <see cref="SecurityCache.Invalidate"/> on the host's cache.
/// </summary>
public static class SecurityTestHelper
{
    public static SecurityCache Cache(XrmdFixture fx) => fx.App.Services.GetRequiredService<SecurityCache>();

    /// <summary>The root business unit.</summary>
    public static Task<Guid> RootBusinessUnitAsync(XrmdFixture fx) => RootBusinessUnitAsync(fx.ConnectionString);

    /// <summary>The root business unit of the database at <paramref name="connectionString"/>.</summary>
    public static async Task<Guid> RootBusinessUnitAsync(string connectionString)
    {
        // Plan 2.7a, Task 5 fix round 1: the run's provider (TestProvider), not SqlClient on a PostgreSQL string.
        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var cmd = TestSql.Command(conn, SqlDialects.For(conn).Limit(
            "SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL ORDER BY createdon", "1"));
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>A user in <paramref name="businessUnitId"/> with Entra object id <paramref name="objectId"/> (a new one when null); returns its systemuserid.</summary>
    public static Task<Guid> CreateUserAsync(XrmdFixture fx, Guid businessUnitId, Guid? objectId = null, bool disabled = false) =>
        CreateUserAsync(fx.ConnectionString, Cache(fx), businessUnitId, objectId, disabled);

    /// <summary>As <see cref="CreateUserAsync(XrmdFixture, Guid, Guid?, bool)"/> on any database, with an optional user name (domainname).</summary>
    public static async Task<Guid> CreateUserAsync(string connectionString, SecurityCache cache, Guid businessUnitId, Guid? objectId = null,
        bool disabled = false, string? userName = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync(connectionString, """
            INSERT INTO data.systemuser (systemuserid, fullname, domainname, azureactivedirectoryobjectid, businessunitid, isdisabled,
                owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
            VALUES (@id, @name, @name, @oid, @bu, @disabled, @bu, SYSUTCDATETIME(), @id, SYSUTCDATETIME(), @id, 0, 1)
            """, ("@id", id), ("@name", userName ?? "user-" + id.ToString("N")[..8]), ("@oid", objectId ?? Guid.NewGuid()),
            ("@bu", businessUnitId), ("@disabled", disabled));
        cache.Invalidate();
        return id;
    }

    public static Task<Guid> CreateBusinessUnitAsync(XrmdFixture fx, Guid parentId, string name) =>
        CreateBusinessUnitAsync(fx.ConnectionString, Cache(fx), parentId, name);

    public static async Task<Guid> CreateBusinessUnitAsync(string connectionString, SecurityCache cache, Guid parentId, string name)
    {
        var id = Guid.NewGuid();
        await ExecAsync(connectionString, """
            INSERT INTO data.businessunit (businessunitid, name, parentbusinessunitid, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            VALUES (@id, @name, @parent, @id, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
            """, ("@id", id), ("@name", name), ("@parent", parentId));
        cache.Invalidate();
        return id;
    }

    /// <summary>A team of <paramref name="teamType"/> (0 owner, 1 access) with <paramref name="members"/>.</summary>
    public static async Task<Guid> CreateTeamAsync(XrmdFixture fx, Guid businessUnitId, int teamType = 0, params Guid[] members)
    {
        var id = Guid.NewGuid();
        await ExecAsync(fx, """
            INSERT INTO data.team (teamid, name, businessunitid, teamtype, isdefault, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            VALUES (@id, @name, @bu, @type, @isdefault, @bu, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
            """, ("@id", id), ("@name", "team-" + id.ToString("N")[..8]), ("@bu", businessUnitId), ("@type", teamType), ("@isdefault", false));
        foreach (var member in members)
            await ExecAsync(fx, """
                INSERT INTO data.teammembership (teammembershipid, teamid, systemuserid, createdon, modifiedon, statecode, statuscode)
                VALUES (NEWID(), @team, @user, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                """, ("@team", id), ("@user", member));
        Cache(fx).Invalidate();
        return id;
    }

    /// <summary>A role in the root business unit with the given privileges (by name) and depths.</summary>
    public static Task<Guid> CreateRoleAsync(XrmdFixture fx, string name, params (string Privilege, DepthMask Depth)[] privileges) =>
        CreateRoleAsync(fx.ConnectionString, Cache(fx), name, privileges);

    public static async Task<Guid> CreateRoleAsync(string connectionString, SecurityCache cache, string name,
        params (string Privilege, DepthMask Depth)[] privileges)
    {
        var id = Guid.NewGuid();
        var root = await RootBusinessUnitAsync(connectionString);
        await ExecAsync(connectionString, """
            INSERT INTO data.role (roleid, name, businessunitid, isinherited, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            VALUES (@id, @name, @bu, 1, @bu, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
            """, ("@id", id), ("@name", name), ("@bu", root));
        foreach (var (privilege, depth) in privileges)
            await AddPrivilegeAsync(connectionString, id, privilege, depth);
        cache.Invalidate();
        return id;
    }

    /// <summary>Adds a <c>roleprivileges</c> row WITHOUT invalidating the cache (tests of invalidation use it).</summary>
    public static Task AddPrivilegeAsync(XrmdFixture fx, Guid roleId, string privilege, DepthMask depth) =>
        AddPrivilegeAsync(fx.ConnectionString, roleId, privilege, depth);

    private static Task AddPrivilegeAsync(string connectionString, Guid roleId, string privilege, DepthMask depth) =>
        ExecAsync(connectionString, """
            INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
            VALUES (NEWID(), @role, @privilege, @depth, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
            """, ("@role", roleId), ("@privilege", PrivilegeNames.IdOf(privilege)), ("@depth", (int)depth));

    /// <summary>Assigns <paramref name="roleId"/> to a user (<c>systemuserroles</c>) or, with <paramref name="team"/>, to a team (<c>teamroles</c>).</summary>
    public static Task AssignRoleAsync(XrmdFixture fx, Guid principalId, Guid roleId, bool team = false) =>
        AssignRoleAsync(fx.ConnectionString, Cache(fx), principalId, roleId, team);

    public static async Task AssignRoleAsync(string connectionString, SecurityCache cache, Guid principalId, Guid roleId, bool team = false)
    {
        await ExecAsync(connectionString, team
            ? "INSERT INTO data.teamroles (teamrolesid, teamid, roleid, createdon, modifiedon, statecode, statuscode) VALUES (NEWID(), @principal, @role, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)"
            : "INSERT INTO data.systemuserroles (systemuserrolesid, systemuserid, roleid, createdon, modifiedon, statecode, statuscode) VALUES (NEWID(), @principal, @role, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)",
            ("@principal", principalId), ("@role", roleId));
        cache.Invalidate();
    }

    /// <summary>Assigns a new role with <paramref name="privileges"/> to a user or, with <paramref name="team"/>, to a team.</summary>
    public static async Task GrantAsync(XrmdFixture fx, Guid principalId, bool team, params (string Privilege, DepthMask Depth)[] privileges) =>
        await AssignRoleAsync(fx, principalId, await CreateRoleAsync(fx, "grant-" + Guid.NewGuid().ToString("N")[..8], privileges), team);

    /// <summary>An <see cref="OrganizationService"/> for the user with Entra object id <paramref name="objectId"/>.</summary>
    public static OrganizationService ServiceFor(XrmdFixture fx, Guid objectId) => TestServices.NewOrganizationService(fx.App.Services, objectId);

    /// <summary>
    /// Removes the four audit privileges from every role: in the shared database only the System Administrator holds them (through
    /// its profile, not <c>roleprivileges</c>; AuditTableTests asserts that). Call it in <c>finally</c> after a test that grants them.
    /// </summary>
    public static async Task RevokeAuditPrivilegesAsync(XrmdFixture fx)
    {
        foreach (var name in PrivilegeNames.Audit)
            await ExecAsync(fx, "DELETE FROM data.roleprivileges WHERE privilegeid = @id", ("@id", PrivilegeNames.IdOf(name)));
        Cache(fx).Invalidate();
    }

    private static Task ExecAsync(XrmdFixture fx, string sql, params (string Name, object Value)[] parameters) =>
        ExecAsync(fx.ConnectionString, sql, parameters);

    private static async Task ExecAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        // Plan 2.7a, Task 5 fix round 1: the run's provider; the helpers' SYSUTCDATETIME() and NEWID() become the dialect's.
        await using var conn = await TestProvider.OpenAsync(connectionString);
        var d = SqlDialects.For(conn);
        await using var cmd = TestSql.Command(conn, sql.Replace("SYSUTCDATETIME()", d.UtcNow).Replace("NEWID()", d.NewGuid));
        foreach (var (name, value) in parameters) cmd.AddParameter(name, DbValueTypes.Of(value), value);
        await cmd.ExecuteNonQueryAsync();
    }
}
