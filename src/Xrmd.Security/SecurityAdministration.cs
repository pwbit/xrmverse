using System.Data;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Security;

/// <summary>
/// A security relationship: the many-to-many relationship <paramref name="SchemaName"/> between <paramref name="Entity1"/>
/// and <paramref name="Entity2"/>, stored in the intersect entity <paramref name="IntersectEntity"/>.
/// </summary>
public sealed record SecurityRelationship(string SchemaName, string Entity1, string Entity2, string IntersectEntity)
{
    /// <summary>
    /// The (<see cref="Entity1"/>, <see cref="Entity2"/>) pairs an Associate/Disassociate names: the target on either side,
    /// every related record on the other. False when a record is not of the expected entity.
    /// </summary>
    public bool TryGetPairs(EntityReference target, EntityReferenceCollection related,
        out IReadOnlyList<(EntityReference First, EntityReference Second)> pairs)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(related);
        pairs = [];
        var targetIsFirst = target.LogicalName == Entity1;
        if (!targetIsFirst && target.LogicalName != Entity2) return false;
        var other = targetIsFirst ? Entity2 : Entity1;
        if (related.Any(r => r.LogicalName != other)) return false;
        pairs = related.Select(r => targetIsFirst ? (target, r) : (r, target)).ToList();
        return true;
    }
}

/// <summary>
/// Writes to the security intersects (<c>systemuserroles</c>, <c>teamroles</c>, <c>teammembership</c>, <c>roleprivileges</c>,
/// and the field security profile links <c>systemuserprofiles</c> and <c>teamprofiles</c>)
/// on the given session; no privilege check (the <see cref="SecurityGuard"/> does them). Used by the core operations and,
/// without the pipeline, by the solution import and the CLI.
/// </summary>
/// <remarks>
/// Every method writes to one of the <see cref="SecurityCache.SecurityEntities"/> and registers the invalidation of the
/// <see cref="SecurityCache"/> with <see cref="DataSession.OnCommitted"/>: whoever owns the transaction (the pipeline, the
/// Admin API, the CLI) only reports its commit with <see cref="DataSession.Committed"/>. Existence checks and duplicate checks read with UPDLOCK/HOLDLOCK in the session's transaction.
/// </remarks>
public sealed class SecurityAdministration(IMetadataStore store, SecurityCache cache)
{
    private const string SystemUser = "systemuser";
    private const string Team = "team";
    private const string Role = "role";
    private const string Privilege = "privilege";
    private const string SystemUserRolesEntity = "systemuserroles";
    private const string TeamRolesEntity = "teamroles";
    private const string TeamMembershipEntity = "teammembership";
    private const string RolePrivilegesEntity = "roleprivileges";
    private const string IdParameter = "@_id";

    /// <summary>systemuser ↔ role.</summary>
    public const string SystemUserRoles = "systemuserroles_association";

    /// <summary>team ↔ role (owner teams only).</summary>
    public const string TeamRoles = "teamroles_association";

    /// <summary>team ↔ systemuser.</summary>
    public const string TeamMembership = "teammembership_association";

    /// <summary>systemuser ↔ fieldsecurityprofile.</summary>
    public const string UserProfiles = FieldSecurityTables.UserProfilesRelationship;

    /// <summary>team ↔ fieldsecurityprofile (owner and access teams).</summary>
    public const string TeamProfiles = FieldSecurityTables.TeamProfilesRelationship;

    /// <summary>The relationships that give a field security profile to a user or a team.</summary>
    public static bool IsProfileRelationship(string? schemaName) =>
        string.Equals(schemaName, UserProfiles, StringComparison.OrdinalIgnoreCase)
        || string.Equals(schemaName, TeamProfiles, StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, SecurityRelationship> Relationships = new SecurityRelationship[]
    {
        new(SystemUserRoles, SystemUser, Role, SystemUserRolesEntity),
        new(TeamRoles, Team, Role, TeamRolesEntity),
        new(TeamMembership, Team, SystemUser, TeamMembershipEntity),
        new(UserProfiles, SystemUser, FieldSecurityTables.Profile, FieldSecurityTables.UserProfiles),
        new(TeamProfiles, Team, FieldSecurityTables.Profile, FieldSecurityTables.TeamProfiles),
    }.ToDictionary(r => r.SchemaName, StringComparer.OrdinalIgnoreCase);

    /// <summary>The security relationship named <paramref name="schemaName"/> (case-insensitive); null for any other.</summary>
    public static SecurityRelationship? FindRelationship(string? schemaName) =>
        schemaName is not null && Relationships.TryGetValue(schemaName, out var r) ? r : null;

    /// <summary>
    /// The privileges of SDK <see cref="RolePrivilege"/>s: <c>PrivilegeId</c>, or (when empty) the id of <c>PrivilegeName</c>
    /// (<see cref="PrivilegeNames.IdOf"/>); the depth through <see cref="DepthMasks.FromSdk"/> (<see cref="DepthMask.None"/>
    /// for a value that is not an SDK depth, which <see cref="SetRolePrivilegesAsync"/> rejects).
    /// </summary>
    public static IReadOnlyList<(Guid PrivilegeId, DepthMask Depth)> FromSdk(IEnumerable<RolePrivilege?> privileges)
    {
        ArgumentNullException.ThrowIfNull(privileges);
        return privileges.Select(p =>
        {
            if (p is null) return (Guid.Empty, DepthMask.None);
            var id = p.PrivilegeId != Guid.Empty ? p.PrivilegeId
                : string.IsNullOrEmpty(p.PrivilegeName) ? Guid.Empty
                : PrivilegeNames.IdOf(p.PrivilegeName);
            var depth = Enum.IsDefined(p.Depth) ? DepthMasks.FromSdk(p.Depth) : DepthMask.None;
            return (id, depth);
        }).ToList();
    }

    /// <summary>Assigns the role to a <c>systemuser</c> (<c>systemuserroles</c>) or an owner <c>team</c> (<c>teamroles</c>).</summary>
    /// <exception cref="XrmdFaultException">400 for a principal of another type or a team that is not an owner team; 404 when
    /// the principal or the role does not exist; 412 DuplicateRecord when the role is already assigned.</exception>
    public async Task AddRoleAsync(DataSession s, EntityReference principal, Guid roleId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.OnCommitted(cache.Invalidate);
        ArgumentNullException.ThrowIfNull(principal);
        var (intersect, column) = RoleIntersect(principal);
        await EnsureExistsAsync(s, Role, roleId, ct);
        if (principal.LogicalName == Team)
        {
            if (await TeamTypeAsync(s, principal.Id, ct) != 0)
                throw XrmdFaultException.InvalidArgument("Roles can be assigned only to owner teams.");
        }
        else
        {
            await EnsureExistsAsync(s, SystemUser, principal.Id, ct);
        }
        await InsertAssociationAsync(s, intersect, (column, principal.Id), ("roleid", roleId), ct);
    }

    /// <summary>Removes the role from the user or team (nothing to remove is not an error); true when an assignment was removed.</summary>
    /// <exception cref="XrmdFaultException">400 for a principal of another type; 404 when the principal or the role does not exist.</exception>
    public async Task<bool> RemoveRoleAsync(DataSession s, EntityReference principal, Guid roleId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.OnCommitted(cache.Invalidate);
        ArgumentNullException.ThrowIfNull(principal);
        var (intersect, column) = RoleIntersect(principal);
        await EnsureExistsAsync(s, principal.LogicalName, principal.Id, ct);
        await EnsureExistsAsync(s, Role, roleId, ct);
        return await DeleteAssociationAsync(s, intersect, (column, principal.Id), ("roleid", roleId), ct);
    }

    /// <summary>Adds the user to the team (owner or access team).</summary>
    /// <exception cref="XrmdFaultException">404 when the team or the user does not exist; 412 DuplicateRecord when it is
    /// already a member.</exception>
    public async Task AddMemberAsync(DataSession s, Guid teamId, Guid systemUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.OnCommitted(cache.Invalidate);
        await EnsureExistsAsync(s, Team, teamId, ct);
        await EnsureExistsAsync(s, SystemUser, systemUserId, ct);
        await InsertAssociationAsync(s, TeamMembershipEntity, ("teamid", teamId), ("systemuserid", systemUserId), ct);
    }

    /// <summary>
    /// Invalidates the security cache once <paramref name="s"/> commits: for a write of a security row this class does not make
    /// itself (e.g. the access teams a record delete removes, plan 2.16 ruling A8).
    /// </summary>
    public void InvalidateOnCommit(DataSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.OnCommitted(cache.Invalidate);
    }

    /// <summary>Removes the user from the team (not a member is not an error).</summary>
    /// <exception cref="XrmdFaultException">404 when the team or the user does not exist.</exception>
    public async Task RemoveMemberAsync(DataSession s, Guid teamId, Guid systemUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.OnCommitted(cache.Invalidate);
        await EnsureExistsAsync(s, Team, teamId, ct);
        await EnsureExistsAsync(s, SystemUser, systemUserId, ct);
        await DeleteAssociationAsync(s, TeamMembershipEntity, ("teamid", teamId), ("systemuserid", systemUserId), ct);
    }

    /// <summary>Gives the field security profile to a <c>systemuser</c> (<c>systemuserprofiles</c>) or a <c>team</c> (<c>teamprofiles</c>, any type).</summary>
    /// <exception cref="XrmdFaultException">400 for a principal of another type; 404 when the principal or the profile does not
    /// exist; 412 DuplicateRecord when the principal already has the profile.</exception>
    public async Task AddProfileAsync(DataSession s, EntityReference principal, Guid profileId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.OnCommitted(cache.Invalidate);
        ArgumentNullException.ThrowIfNull(principal);
        var (intersect, column) = ProfileIntersect(principal);
        await EnsureExistsAsync(s, principal.LogicalName, principal.Id, ct);
        await EnsureExistsAsync(s, FieldSecurityTables.Profile, profileId, ct);
        await InsertAssociationAsync(s, intersect, (column, principal.Id), (FieldSecurityTables.ProfileId, profileId), ct);
    }

    /// <summary>Takes the field security profile from the user or team (nothing to remove is not an error).</summary>
    /// <exception cref="XrmdFaultException">400 for a principal of another type; 404 when the principal or the profile does not exist.</exception>
    public async Task RemoveProfileAsync(DataSession s, EntityReference principal, Guid profileId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.OnCommitted(cache.Invalidate);
        ArgumentNullException.ThrowIfNull(principal);
        var (intersect, column) = ProfileIntersect(principal);
        await EnsureExistsAsync(s, principal.LogicalName, principal.Id, ct);
        await EnsureExistsAsync(s, FieldSecurityTables.Profile, profileId, ct);
        await DeleteAssociationAsync(s, intersect, (column, principal.Id), (FieldSecurityTables.ProfileId, profileId), ct);
    }

    /// <summary>
    /// Sets the depth of each of <paramref name="privileges"/> in the role (a privilege listed twice: the last depth wins);
    /// with <paramref name="replaceAll"/> the role keeps no other privilege.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 when the role does not exist; 400 <c>0x80090903</c> for a privilege not in
    /// the catalog; 400 InvalidArgument for a depth that is not exactly one of Basic, Local, Deep, Global or that the
    /// privilege's <c>canbe*</c> columns do not allow. Nothing is written when one privilege is refused.</exception>
    public async Task SetRolePrivilegesAsync(DataSession s, Guid roleId, IEnumerable<(Guid PrivilegeId, DepthMask Depth)> privileges,
        bool replaceAll, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.OnCommitted(cache.Invalidate);
        ArgumentNullException.ThrowIfNull(privileges);
        var requested = privileges.ToList();
        await EnsureExistsAsync(s, Role, roleId, ct);

        var catalog = await ReadPrivilegesAsync(s, requested.Select(p => p.PrivilegeId), ct);
        var rows = new Dictionary<Guid, DepthMask>();
        foreach (var (id, depth) in requested)
        {
            if (!catalog.TryGetValue(id, out var privilege)) throw XrmdFaultException.InvalidPrivilege(id);
            if (!privilege.Allows(depth))
                throw XrmdFaultException.InvalidArgument($"Privilege {privilege.Name} (Id={id}) cannot be granted at depth {depth}.");
            rows[id] = depth;
        }

        var entity = Definition(RolePrivilegesEntity);
        var table = $"{s.DataTable(entity.LogicalName)}";
        // Plan 2.7a, Task 6: the table variable became the JSON rows read twice, the batch two statements.
        var d = s.Dialect;
        var json = JsonSerializer.Serialize(rows.Select(r => new { id = r.Key, depth = (int)r.Value }));
        var source = d.JsonRecords("@json", [new JsonColumn("privilegeid", SqlColumnKind.Guid, Path: "$.id"), new JsonColumn("depth", SqlColumnKind.Int32, Path: "$.depth")]);
        await using (var delete = s.Command(replaceAll
                         ? $"DELETE FROM {table} WHERE roleid = @role"
                         : $"DELETE FROM {table} WHERE roleid = @role AND privilegeid IN (SELECT j.privilegeid FROM {source} AS j)"))
        {
            delete.AddParameter("@role", DbType.Guid, roleId);
            if (!replaceAll) delete.AddParameter("@json", DbType.String, json, -1);
            await delete.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = s.Command($"""
            INSERT INTO {table} ({s.Quote(SystemAttributes.PrimaryIdName(entity))}, roleid, privilegeid, privilegedepthmask,
                owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            SELECT {d.NewGuid}, @role, r.privilegeid, r.depth, {RootBusinessUnitSql(d)}, {d.UtcNow}, {d.UtcNow}, 0, 1 FROM {source} AS r
            """);
        cmd.AddParameter("@json", DbType.String, json, -1);
        cmd.AddParameter("@role", DbType.Guid, roleId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The privileges of the role as SDK <see cref="RolePrivilege"/>s (privilege id, depth, the role's business unit), ordered by
    /// privilege id. A row without a depth is not a granted privilege and is left out.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 when the role does not exist.</exception>
    public async Task<IReadOnlyList<RolePrivilege>> RolePrivilegesAsync(DataSession s, Guid roleId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await EnsureExistsAsync(s, Role, roleId, ct);
        var entity = Definition(RolePrivilegesEntity);
        await using var cmd = s.Command($"""
            SELECT rp.privilegeid, rp.privilegedepthmask, r.businessunitid
            FROM {s.DataTable(entity.LogicalName)} AS rp
            JOIN {s.DataTable("role")} AS r ON r.roleid = rp.roleid
            WHERE rp.roleid = @role ORDER BY rp.privilegeid
            """);
        cmd.AddParameter("@role", DbType.Guid, roleId);
        var result = new List<RolePrivilege>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            // Several bits in a mask are read as their widest depth, as the security checks do (ReadSecurity); the SDK's
            // RolePrivilege carries one depth, so the audit cannot show more than that either.
            var depth = DepthMasks.Highest(rd.IsDBNull(1) ? DepthMask.None : (DepthMask)rd.GetInt32(1));
            if (depth == DepthMask.None) continue;
            result.Add(new RolePrivilege((int)ToSdk(depth), rd.GetGuid(0), rd.IsDBNull(2) ? Guid.Empty : rd.GetGuid(2)));
        }
        return result;
    }

    private static PrivilegeDepth ToSdk(DepthMask depth) => depth switch
    {
        DepthMask.Basic => PrivilegeDepth.Basic,
        DepthMask.Local => PrivilegeDepth.Local,
        DepthMask.Deep => PrivilegeDepth.Deep,
        _ => PrivilegeDepth.Global,
    };

    /// <summary>Removes the privilege from the role (not in the role is not an error).</summary>
    /// <exception cref="XrmdFaultException">404 when the role does not exist; 400 <c>0x80090903</c> for a privilege not in the
    /// catalog.</exception>
    public async Task RemoveRolePrivilegeAsync(DataSession s, Guid roleId, Guid privilegeId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.OnCommitted(cache.Invalidate);
        await EnsureExistsAsync(s, Role, roleId, ct);
        if (!(await ReadPrivilegesAsync(s, [privilegeId], ct)).ContainsKey(privilegeId))
            throw XrmdFaultException.InvalidPrivilege(privilegeId);
        await DeleteAssociationAsync(s, RolePrivilegesEntity, ("roleid", roleId), ("privilegeid", privilegeId), ct);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>The root business unit, as the <c>owningbusinessunit</c> of an intersect row (as the seed writes them).</summary>
    private static string RootBusinessUnitSql(ISqlDialect d) =>
        "(" + d.Limit($"SELECT businessunitid FROM {d.DataTable("businessunit")} WHERE parentbusinessunitid IS NULL ORDER BY createdon", "1") + ")";

    /// <summary>A catalog privilege: its name and the depths it may be granted at.</summary>
    private sealed record PrivilegeRow(string Name, bool Basic, bool Local, bool Deep, bool Global)
    {
        public bool Allows(DepthMask depth) => depth switch
        {
            DepthMask.Basic => Basic,
            DepthMask.Local => Local,
            DepthMask.Deep => Deep,
            DepthMask.Global => Global,
            _ => false,
        };
    }

    private EntityMetadata Definition(string logicalName) =>
        store.GetEntity(logicalName) ?? throw new InvalidOperationException($"Entity '{logicalName}' is not defined.");

    /// <summary>The intersect and principal column of a role assignment.</summary>
    /// <exception cref="XrmdFaultException">400 for a principal that is not a systemuser or team.</exception>
    private static (string Intersect, string Column) RoleIntersect(EntityReference principal) => principal.LogicalName switch
    {
        SystemUser => (SystemUserRolesEntity, "systemuserid"),
        Team => (TeamRolesEntity, "teamid"),
        _ => throw XrmdFaultException.InvalidArgument(
            $"Principal of type '{principal.LogicalName}' is not supported: a role is assigned to a {SystemUser} or a {Team}."),
    };

    /// <summary>The intersect and principal column of a profile link.</summary>
    /// <exception cref="XrmdFaultException">400 for a principal that is not a systemuser or team.</exception>
    private static (string Intersect, string Column) ProfileIntersect(EntityReference principal) => principal.LogicalName switch
    {
        SystemUser => (FieldSecurityTables.UserProfiles, "systemuserid"),
        Team => (FieldSecurityTables.TeamProfiles, "teamid"),
        _ => throw XrmdFaultException.InvalidArgument(
            $"Principal of type '{principal.LogicalName}' is not supported: a field security profile is given to a {SystemUser} or a {Team}."),
    };

    /// <exception cref="XrmdFaultException">404 when the record does not exist.</exception>
    private async Task EnsureExistsAsync(DataSession s, string entityName, Guid id, CancellationToken ct)
    {
        var entity = Definition(entityName);
        await using var cmd = s.Command(
            $"SELECT 1 FROM {s.DataTable(entity.LogicalName)}{s.Dialect.LockHint(RowLock.Shared)} " +
            $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(entity))} = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        if (await cmd.ExecuteScalarAsync(ct) is null) throw XrmdFaultException.ObjectDoesNotExist(entity.LogicalName, id);
    }

    /// <summary>The <c>teamtype</c> of the team (NULL counts as an owner team).</summary>
    /// <exception cref="XrmdFaultException">404 when the team does not exist.</exception>
    private async Task<int> TeamTypeAsync(DataSession s, Guid teamId, CancellationToken ct)
    {
        var entity = Definition(Team);
        await using var cmd = s.Command(
            $"SELECT {s.Dialect.IsNull("teamtype", "0")} FROM {s.DataTable(entity.LogicalName)}{s.Dialect.LockHint(RowLock.Shared)} " +
            $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(entity))} = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
        cmd.AddParameter(IdParameter, DbType.Guid, teamId);
        return await cmd.ExecuteScalarAsync(ct) as int? ?? throw XrmdFaultException.ObjectDoesNotExist(entity.LogicalName, teamId);
    }

    /// <summary>The catalog rows of <paramref name="ids"/> that exist, by id.</summary>
    private async Task<Dictionary<Guid, PrivilegeRow>> ReadPrivilegesAsync(DataSession s, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var entity = Definition(Privilege);
        await using var cmd = s.Command($"""
            SELECT p.privilegeid, p.name, {s.Dialect.IsNull("p.canbebasic", s.Dialect.BooleanLiteral(false))}, {s.Dialect.IsNull("p.canbelocal", s.Dialect.BooleanLiteral(false))}, {s.Dialect.IsNull("p.canbedeep", s.Dialect.BooleanLiteral(false))}, {s.Dialect.IsNull("p.canbeglobal", s.Dialect.BooleanLiteral(false))}
            FROM {s.DataTable(entity.LogicalName)} AS p
            WHERE p.privilegeid IN (SELECT j.id FROM {s.Dialect.JsonRecords("@ids", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$")])} AS j)
            """);
        cmd.AddParameter("@ids", DbType.String, JsonSerializer.Serialize(ids.Distinct()), -1);
        var rows = new Dictionary<Guid, PrivilegeRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            rows[rd.GetGuid(0)] = new PrivilegeRow(rd.IsDBNull(1) ? string.Empty : rd.GetString(1),
                rd.GetBoolean(2), rd.GetBoolean(3), rd.GetBoolean(4), rd.GetBoolean(5));
        return rows;
    }

    /// <summary>Inserts the intersect row of the pair, locking the pair first (UPDLOCK, HOLDLOCK).</summary>
    /// <exception cref="XrmdFaultException">412 DuplicateRecord when the pair is already there.</exception>
    private async Task InsertAssociationAsync(DataSession s, string intersect, (string Column, Guid Value) first,
        (string Column, Guid Value) second, CancellationToken ct)
    {
        var entity = Definition(intersect);
        var table = $"{s.DataTable(entity.LogicalName)}";
        // Plan 2.7a, Task 6: the key lock of the pair, then one insert that checks for it under the range lock.
        var d = s.Dialect;
        await s.KeyLockAsync(entity.LogicalName, $"{first.Value:N}:{second.Value:N}", ct);
        await using var cmd = s.Command($"""
            INSERT INTO {table} ({s.Quote(SystemAttributes.PrimaryIdName(entity))}, {s.Quote(first.Column)}, {s.Quote(second.Column)},
                owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            SELECT {d.NewGuid}, @first, @second, {RootBusinessUnitSql(d)}, {d.UtcNow}, {d.UtcNow}, 0, 1
            WHERE NOT EXISTS (SELECT 1 FROM {table}{d.LockHint(RowLock.UpdateRange)} WHERE {s.Quote(first.Column)} = @first AND {s.Quote(second.Column)} = @second)
            """);
        cmd.AddParameter("@first", DbType.Guid, first.Value);
        cmd.AddParameter("@second", DbType.Guid, second.Value);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw XrmdFaultException.DuplicateAssociation();
    }

    private async Task<bool> DeleteAssociationAsync(DataSession s, string intersect, (string Column, Guid Value) first,
        (string Column, Guid Value) second, CancellationToken ct)
    {
        var entity = Definition(intersect);
        await using var cmd = s.Command(
            $"DELETE FROM {s.DataTable(entity.LogicalName)} WHERE {s.Quote(first.Column)} = @first AND {s.Quote(second.Column)} = @second");
        cmd.AddParameter("@first", DbType.Guid, first.Value);
        cmd.AddParameter("@second", DbType.Guid, second.Value);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}
