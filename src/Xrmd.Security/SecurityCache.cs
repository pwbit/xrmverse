using System.Collections.Concurrent;
using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Security;

/// <summary>
/// Process-wide cache of the <see cref="SecurityProfile"/>s, of the <see cref="BusinessUnitTree"/>, of the
/// <see cref="ReportingTree"/> and of the <see cref="HierarchySettings"/>, dropped as a whole by <see cref="Invalidate"/> (after
/// the commit of any write to one of the <see cref="SecurityEntities"/> or to <c>organization</c>, of a change of the hierarchy
/// exclusions, and of the deletion of an entity, whose privileges and exclusion go with it).
/// </summary>
public sealed class SecurityCache(IDbConnectionFactory factory) : IEntityDeletedHandler
{
    /// <summary>The logical names of the tables a profile or the tree is read from; a write to one invalidates the cache.</summary>
    public static readonly IReadOnlySet<string> SecurityEntities = FieldSecurability.SecurityEntities;

    /// <summary>What was loaded under one version; it is never read again once the version moves on.</summary>
    private sealed class Entries(int version)
    {
        public int Version { get; } = version;
        public ConcurrentDictionary<Guid, SecurityProfile> Profiles { get; } = new();
        public BusinessUnitTree? Tree { get; set; }
        public ReportingTree? Reporting { get; set; }
        public HierarchySettings? Hierarchy { get; set; }
    }

    private int _version;
    private Entries _entries = new(0);
    private volatile object? _systemUserId; // boxed Guid: a reference is written atomically

    /// <summary>
    /// Drops everything cached: the next read reloads. A load that started before this call stores its result in the
    /// entries of the old version, which no later read uses, so no invalidation can be lost.
    /// </summary>
    public void Invalidate() => Interlocked.Increment(ref _version);

    /// <summary>
    /// The current version, bumped by every <see cref="Invalidate"/>. Caches derived from the security tables (the field
    /// permissions of <see cref="FieldSecurity"/>) tag their entries with it, so one <see cref="Invalidate"/> drops them too.
    /// </summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Removes the hierarchy exclusion of <paramref name="entity"/> in the session deleting it, and invalidates the cache after
    /// its commit.
    /// </summary>
    public async Task OnEntityDeletingAsync(Microsoft.Xrm.Sdk.Metadata.EntityMetadata entity, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(session);
        await using (var cmd = session.Command($"DELETE FROM {ExclusionTable} WHERE entity_logical_name = @name"))
        {
            cmd.AddParameter("@name", DbType.String, entity.LogicalName ?? string.Empty, 128);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        session.OnCommitted(Invalidate);
    }

    /// <summary>The table of the hierarchy exclusions (migration 0005).</summary>
    private const string ExclusionTable = "meta.hierarchy_security_exclusion";

    /// <summary>The entries of the current version (new, empty ones after an <see cref="Invalidate"/>).</summary>
    private Entries Current()
    {
        var version = Volatile.Read(ref _version);
        var entries = Volatile.Read(ref _entries);
        if (entries.Version == version) return entries;
        var fresh = new Entries(version);
        var previous = Interlocked.CompareExchange(ref _entries, fresh, entries);
        // Another thread replaced the entries first: use them when they are of this version (otherwise the fresh ones,
        // which stay detached, are only used by this call).
        return previous.Version == version ? previous : fresh;
    }

    /// <summary>The user's profile, with its <see cref="SecurityProfile.Hierarchy"/>.</summary>
    /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist when the <c>systemuser</c> does not exist.</exception>
    public async Task<SecurityProfile> GetProfileAsync(Guid userId, CancellationToken ct)
    {
        var entries = Current();
        if (entries.Profiles.TryGetValue(userId, out var cached)) return cached;
        var profile = await LoadProfileAsync(userId, ct);
        profile = profile with { Hierarchy = await ReachOfAsync(profile, ct) };
        entries.Profiles[userId] = profile;
        return profile;
    }

    /// <summary>The hierarchy security settings: the two <c>organization</c> columns and the excluded tables.</summary>
    public async Task<HierarchySettings> GetHierarchySettingsAsync(CancellationToken ct)
    {
        var entries = Current();
        if (entries.Hierarchy is { } cached) return cached;
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.Command(factory.Dialect.Limit(
            "SELECT ishierarchicalsecuritymodelenabled, maxdepthforhierarchicalsecuritymodel FROM data.organization", "1") + $"""
            ;
            SELECT entity_logical_name FROM {ExclusionTable};
            """);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var enabled = false;
        var depth = HierarchySettings.DefaultDepth;
        if (await rd.ReadAsync(ct))
        {
            enabled = !rd.IsDBNull(0) && rd.GetBoolean(0);
            if (!rd.IsDBNull(1)) depth = rd.GetInt32(1);
        }
        await rd.NextResultAsync(ct);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await rd.ReadAsync(ct)) excluded.Add(rd.GetString(0));
        var settings = new HierarchySettings(enabled, depth, excluded);
        entries.Hierarchy = settings;
        return settings;
    }

    /// <summary>The manager hierarchy of every user, with their business units and teams.</summary>
    public async Task<ReportingTree> GetReportingTreeAsync(CancellationToken ct)
    {
        var entries = Current();
        if (entries.Reporting is { } cached) return cached;
        var users = new Dictionary<Guid, (Guid? Manager, Guid BusinessUnit)>();
        var teams = new Dictionary<Guid, HashSet<Guid>>();
        await using (var conn = await factory.OpenAsync(ct))
        await using (var cmd = conn.Command("""
            SELECT systemuserid, parentsystemuserid, businessunitid FROM data.systemuser;
            SELECT systemuserid, teamid FROM data.teammembership WHERE systemuserid IS NOT NULL AND teamid IS NOT NULL;
            """))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
                users[rd.GetGuid(0)] = (rd.IsDBNull(1) ? null : rd.GetGuid(1), rd.IsDBNull(2) ? Guid.Empty : rd.GetGuid(2));
            await rd.NextResultAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var user = rd.GetGuid(0);
                if (!teams.TryGetValue(user, out var set)) teams[user] = set = [];
                set.Add(rd.GetGuid(1));
            }
        }
        var tree = new ReportingTree(users, teams.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<Guid>)kv.Value));
        entries.Reporting = tree;
        return tree;
    }

    /// <summary>
    /// The user and its reports down to <c>maxdepthforhierarchicalsecuritymodel</c> levels, without the business unit rule,
    /// and with <paramref name="includeTeams"/> the teams of all of them (the operators <c>eq-useroruserhierarchy</c> and
    /// <c>eq-useroruserhierarchyandteams</c>, plan 2.5 ruling H7). Whether the model is enabled is the caller's check.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>> ReportingHierarchyAsync(Guid userId, bool includeTeams, CancellationToken ct)
    {
        var settings = await GetHierarchySettingsAsync(ct);
        var tree = await GetReportingTreeAsync(ct);
        var users = new List<Guid> { userId };
        users.AddRange(tree.ReportsOf(userId, settings.Depth).Select(r => r.UserId));
        var result = new HashSet<Guid>(users);
        if (includeTeams)
            foreach (var user in users) result.UnionWith(tree.TeamsOf(user));
        return result;
    }

    /// <summary>
    /// Replaces the tables excluded from hierarchy security with <paramref name="tables"/> (logical names, compared
    /// case-insensitively) in <paramref name="session"/>'s transaction, and invalidates the cache after its commit. No
    /// privilege or existence check: the caller (the Admin API) does them.
    /// </summary>
    /// <exception cref="ArgumentException">A name is empty or longer than 128 characters.</exception>
    public async Task SetHierarchyExclusionsAsync(DataSession session, IEnumerable<string> tables, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tables);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            if (string.IsNullOrWhiteSpace(table) || table.Length > 128)
                throw new ArgumentException($"'{table}' is not a valid table logical name.", nameof(tables));
            names.Add(table);
        }
        await using (var delete = session.Command($"DELETE FROM {ExclusionTable}"))
            await delete.ExecuteNonQueryAsync(ct);
        foreach (var name in names)
        {
            await using var insert = session.Command($"INSERT INTO {ExclusionTable} (entity_logical_name) VALUES (@name)");
            insert.AddParameter("@name", DbType.String, name, 128);
            await insert.ExecuteNonQueryAsync(ct);
        }
        session.OnCommitted(Invalidate);
    }

    /// <summary>
    /// The reach of <paramref name="manager"/> (plan 2.5, ruling H4); null when the model is disabled or no report counts.
    /// A report counts when the manager's business unit is the report's or its parent; the walk goes on below a report that
    /// does not count. Direct reports give write rights, the others read rights only.
    /// </summary>
    private async Task<HierarchyReach?> ReachOfAsync(SecurityProfile manager, CancellationToken ct)
    {
        var settings = await GetHierarchySettingsAsync(ct);
        if (!settings.Enabled) return null;
        var tree = await GetReportingTreeAsync(ct);
        var reports = tree.ReportsOf(manager.UserId, settings.Depth);
        if (reports.Count == 0) return null;
        var units = await GetBusinessUnitTreeAsync(ct);
        var read = new HashSet<Guid>();
        var write = new HashSet<Guid>();
        foreach (var (report, level) in reports)
        {
            if (tree.BusinessUnitOf(report) is not { } unit || unit == Guid.Empty || manager.BusinessUnitId == Guid.Empty) continue;
            Guid? parent = units.AncestorsOf(unit) is [var first, ..] ? first : null;
            if (unit != manager.BusinessUnitId && parent != manager.BusinessUnitId) continue;
            read.Add(report);
            read.UnionWith(tree.TeamsOf(report));
            if (level != 1) continue;
            write.Add(report);
            write.UnionWith(tree.TeamsOf(report));
        }
        return read.Count == 0 ? null : new HierarchyReach(read, write, settings.ExcludedTables);
    }

    /// <summary>The business unit hierarchy.</summary>
    public async Task<BusinessUnitTree> GetBusinessUnitTreeAsync(CancellationToken ct)
    {
        var entries = Current();
        if (entries.Tree is { } cached) return cached;
        var parents = new Dictionary<Guid, Guid?>();
        await using (var conn = await factory.OpenAsync(ct))
        await using (var cmd = conn.Command("SELECT businessunitid, parentbusinessunitid FROM data.businessunit"))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                parents[rd.GetGuid(0)] = rd.IsDBNull(1) ? null : rd.GetGuid(1);
        var tree = new BusinessUnitTree(parents);
        entries.Tree = tree;
        return tree;
    }

    /// <summary>The id of the organization's SYSTEM user (derived from <c>organization.name</c>; read once).</summary>
    public async Task<Guid> SystemUserIdAsync(CancellationToken ct)
    {
        if (_systemUserId is Guid known) return known;
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.Command(factory.Dialect.Limit("SELECT name FROM data.organization", "1"));
        var name = await cmd.ExecuteScalarAsync(ct) switch
        {
            string n => n,
            DBNull => string.Empty,
            _ => throw new InvalidOperationException("The organization has not been seeded (data.organization is empty)."),
        };
        var id = SeedOptions.DeriveSystemUserId(name);
        _systemUserId = id;
        return id;
    }

    /// <summary>
    /// The profiles every check of <paramref name="caller"/> holds for: the user and, when impersonating, the impersonator
    /// (unless it is SYSTEM, which no check applies to). The user comes first.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist when a <c>systemuser</c> does not exist.</exception>
    public async Task<IReadOnlyList<SecurityProfile>> PrincipalsAsync(Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var user = await GetProfileAsync(caller.UserId, ct);
        if (caller.ImpersonatorId is not { } impersonator || impersonator == await SystemUserIdAsync(ct)) return [user];
        return [user, await GetProfileAsync(impersonator, ct)];
    }

    /// <summary>True for callers no privilege check applies to: <see cref="Caller.Internal"/> and the SYSTEM user.</summary>
    public bool IsUnrestricted(Caller caller, Guid systemUserId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller == Caller.Internal || (caller.UserId == systemUserId && systemUserId != Guid.Empty);
    }

    /// <summary>
    /// The profile of a team as a principal of an access check (plan 3.5 UI-1b task 2, RetrievePrincipalAccess), not cached: its
    /// business unit, the privileges and depths of the team's own roles (any team type), and the team itself as the owner it can be
    /// (<see cref="SecurityProfile.TeamIds"/> and <see cref="SecurityProfile.OwnerTeamIds"/> hold only the team). It has no manager
    /// hierarchy reach (<see cref="SecurityProfile.Hierarchy"/> is null: a hierarchy is of users).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 when the team does not exist.</exception>
    public async Task<SecurityProfile> GetTeamProfileAsync(Guid teamId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.Command("""
            SELECT businessunitid FROM data.team WHERE teamid = @team;

            SELECT r.roletemplateid FROM data.teamroles AS tr JOIN data.role AS r ON r.roleid = tr.roleid WHERE tr.teamid = @team;

            SELECT p.name, rp.privilegedepthmask
            FROM data.teamroles AS tr
            JOIN data.roleprivileges AS rp ON rp.roleid = tr.roleid
            JOIN data.privilege AS p ON p.privilegeid = rp.privilegeid
            WHERE tr.teamid = @team;
            """);
        cmd.AddParameter("@team", DbType.Guid, teamId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist("team", teamId);
        var businessUnitId = rd.IsDBNull(0) ? Guid.Empty : rd.GetGuid(0);

        await rd.NextResultAsync(ct);
        bool hasRoles = false, administrator = false;
        while (await rd.ReadAsync(ct))
        {
            hasRoles = true;
            administrator |= !rd.IsDBNull(0) && rd.GetGuid(0) == StandardRoles.SystemAdministrator;
        }

        await rd.NextResultAsync(ct);
        var privileges = new Dictionary<string, DepthMask>(StringComparer.OrdinalIgnoreCase);
        while (await rd.ReadAsync(ct))
        {
            if (rd.IsDBNull(0) || rd.IsDBNull(1)) continue;
            var name = rd.GetString(0);
            privileges[name] = privileges.GetValueOrDefault(name) | (DepthMask)rd.GetInt32(1);
        }
        HashSet<Guid> self = [teamId];
        return new SecurityProfile(teamId, businessUnitId, false, administrator, hasRoles, self, self, privileges, new HashSet<string>());
    }

    /// <summary>
    /// The roles of the user <c>@user</c> (<c>direct</c> true) and of its owner teams (<c>direct</c> false), as a derived table
    /// (<c>roleid</c>, <c>direct</c>) to alias (plan 2.7a, Task 5 fix round 1: the T-SQL table variable it replaces).
    /// </summary>
    internal static string UserRoles(ISqlDialect d)
    {
        var boolean = d.ColumnType(SqlColumnKind.Boolean);
        return $"""
            (SELECT roleid, CAST({d.BooleanLiteral(true)} AS {boolean}) AS direct FROM data.systemuserroles WHERE systemuserid = @user
            UNION ALL
            SELECT tr.roleid, CAST({d.BooleanLiteral(false)} AS {boolean})
            FROM data.teamroles AS tr
            JOIN data.teammembership AS tm ON tm.teamid = tr.teamid AND tm.systemuserid = @user
            JOIN data.team AS t ON t.teamid = tr.teamid AND {d.IsNull("t.teamtype", "0")} = 0)
            """;
    }

    private async Task<SecurityProfile> LoadProfileAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var roles = UserRoles(SqlDialects.For(conn));
        await using var cmd = conn.Command($"""
            SELECT businessunitid, isdisabled FROM data.systemuser WHERE systemuserid = @user;

            SELECT tm.teamid, t.teamtype
            FROM data.teammembership AS tm JOIN data.team AS t ON t.teamid = tm.teamid
            WHERE tm.systemuserid = @user;

            -- The roles of the user (direct = 1) and of its owner teams (direct = 0).
            SELECT x.direct, r.roletemplateid FROM {roles} AS x JOIN data.role AS r ON r.roleid = x.roleid;

            SELECT x.direct, p.name, rp.privilegedepthmask
            FROM {roles} AS x
            JOIN data.roleprivileges AS rp ON rp.roleid = x.roleid
            JOIN data.privilege AS p ON p.privilegeid = rp.privilegeid;
            """);
        cmd.AddParameter("@user", DbType.Guid, userId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);

        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist("systemuser", userId);
        var businessUnitId = rd.IsDBNull(0) ? Guid.Empty : rd.GetGuid(0);
        var disabled = !rd.IsDBNull(1) && rd.GetBoolean(1);

        await rd.NextResultAsync(ct);
        var teams = new HashSet<Guid>();
        var ownerTeams = new HashSet<Guid>();
        while (await rd.ReadAsync(ct))
        {
            teams.Add(rd.GetGuid(0));
            if (rd.IsDBNull(1) || rd.GetInt32(1) == 0) ownerTeams.Add(rd.GetGuid(0));
        }

        await rd.NextResultAsync(ct);
        bool hasRoles = false, administrator = false, directAdministrator = false;
        while (await rd.ReadAsync(ct))
        {
            hasRoles = true;
            if (rd.IsDBNull(1) || rd.GetGuid(1) != StandardRoles.SystemAdministrator) continue;
            administrator = true;
            directAdministrator |= rd.GetBoolean(0);
        }

        await rd.NextResultAsync(ct);
        var privileges = new Dictionary<string, DepthMask>(StringComparer.OrdinalIgnoreCase);
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await rd.ReadAsync(ct))
        {
            if (rd.IsDBNull(1) || rd.IsDBNull(2)) continue;
            var name = rd.GetString(1);
            privileges[name] = privileges.GetValueOrDefault(name) | (DepthMask)rd.GetInt32(2);
            if (rd.GetBoolean(0)) direct.Add(name);
        }
        if (directAdministrator) direct.Add(PrivilegeNames.ActOnBehalfOfAnotherUser);

        return new SecurityProfile(userId, businessUnitId, disabled, administrator, hasRoles, ownerTeams, teams, privileges, direct);
    }
}
