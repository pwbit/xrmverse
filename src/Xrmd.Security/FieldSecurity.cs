using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Security;

/// <summary>
/// Field-level security (plan 14): the secured columns of each table, the permissions a caller holds on them through its field
/// security profiles, and the field shares that apply to it.
/// </summary>
/// <remarks>
/// <para>
/// F1: <see cref="Caller.Internal"/>, the SYSTEM user and any user holding the System Administrator role (directly or through an
/// owner team, <see cref="SecurityProfile.IsSystemAdministrator"/>) bypass field security. Under impersonation the checks hold
/// for every principal of <see cref="SecurityCache.PrincipalsAsync"/> (the user and a non-SYSTEM impersonator), as in
/// <see cref="SecurityGuard"/>: the caller bypasses only when both do, and otherwise gets the AND of their permissions.
/// </para>
/// <para>
/// F2: a user's permissions are the profiles associated with it plus those of every team it is a member of (owner and access
/// teams), merged with OR per column; a NULL right counts as Not Allowed.
/// </para>
/// <para>
/// F10: everything cached here is tagged with <see cref="SecurityCache.Version"/>, so a write to any security table (the five
/// field security tables among them) and a change of a column's <c>IsSecured</c> (<see cref="OnAttributeSecurityChanged"/>)
/// drop it through <see cref="SecurityCache.Invalidate"/>. Invalidation never blocks; a load that started under an older
/// version stores its result where no later read looks.
/// </para>
/// </remarks>
public sealed class FieldSecurity(SecurityCache cache, IMetadataStore metadata, IDbConnectionFactory db) : IAttributeSecurityChangedHandler
{
    private const int SystemUserTypeCode = 8, TeamTypeCode = 9;

    [Flags]
    private enum Rights { None = 0, Read = 1, Create = 2, Update = 4 }

    /// <summary>What was loaded under one version of the <see cref="SecurityCache"/>.</summary>
    private sealed class Entries(int version)
    {
        public int Version { get; } = version;

        /// <summary>Table → its secured columns and their companions (tables that exist only).</summary>
        public ConcurrentDictionary<string, SecuredTable> Secured { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>User → the merged rights of all its profiles, by (table, column).</summary>
        public ConcurrentDictionary<Guid, IReadOnlyDictionary<(string Entity, string Attribute), Rights>> Permissions { get; } = new();

        /// <summary>(User, table) → the set built from the two above.</summary>
        public ConcurrentDictionary<(Guid User, string Entity), FieldPermissionSet> Sets { get; } = new();
    }

    /// <summary>
    /// The secured columns of a table: each column flagged <c>IsSecured</c> and each companion of one (the money <c>_base</c>,
    /// the file name, the image id, timestamp and url, found by <see cref="FieldSecurability.MainColumnOf"/>), with the column
    /// whose permissions and shares apply to each (itself for a main column).
    /// </summary>
    private sealed record SecuredTable(IReadOnlySet<string> Columns, IReadOnlyDictionary<string, string> MainOf)
    {
        public string Main(string column) => MainOf.TryGetValue(column, out var main) ? main : column;
    }

    private static readonly SecuredTable NoColumns = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    private static readonly IReadOnlyDictionary<Guid, FieldShareGrant> NoShares = new Dictionary<Guid, FieldShareGrant>();

    private Entries _entries = new(-1);

    /// <summary>Drops the cached field permissions and secured columns after a column's <c>IsSecured</c> changed.</summary>
    public void OnAttributeSecurityChanged(string entity, string attribute) => cache.Invalidate();

    /// <summary>
    /// True when <paramref name="entity"/>.<paramref name="attribute"/> exists and is secured, or is a companion of a secured
    /// column (which follows its column).
    /// </summary>
    public bool IsSecured(string entity, string attribute)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        return SecuredColumns(Current(), entity).Columns.Contains(attribute);
    }

    /// <summary>
    /// The permissions of <paramref name="caller"/> on the columns of <paramref name="entity"/>: it covers every secured column of
    /// the table; <see cref="FieldPermissionSet.Unrestricted"/> when every principal of the caller bypasses field security (F1).
    /// Under impersonation it is the AND of the sets of the user and of the (non-SYSTEM) impersonator, as in
    /// <see cref="SecurityGuard"/>: a column is allowed only when both may.
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">404 ObjectDoesNotExist when a <c>systemuser</c> of the caller does not exist.</exception>
    public async Task<FieldPermissionSet> ForAsync(Caller caller, string entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(entity);
        var restricted = await RestrictedPrincipalsAsync(caller, ct);
        if (restricted.Count == 0) return FieldPermissionSet.Unrestricted;

        var entries = Current();
        var set = await UserSetAsync(entries, restricted[0], entity, ct);
        for (var i = 1; i < restricted.Count; i++) set = set.And(await UserSetAsync(entries, restricted[i], entity, ct));
        return set;
    }

    /// <summary>
    /// The field shares (<c>principalobjectattributeaccess</c>) of <paramref name="recordIds"/> on <paramref name="attributes"/>
    /// that apply to <paramref name="caller"/>: what they let the caller reach on each record beyond <see cref="ForAsync"/>.
    /// A share applies to a principal when it is given to its user or to a team it is a member of (owner or access team), merged
    /// with OR per record. Under impersonation the principals are ANDed as in <see cref="ForAsync"/>: a column is granted on a
    /// record only when every principal reaches it there, through its profiles or a share, and the set of
    /// <see cref="ForAsync"/> does not already allow it. With one principal this is its shares, less the columns its profiles
    /// already allow. Records granted nothing are absent. Read through <paramref name="session"/> (not cached), so a share
    /// written in the session's transaction is seen. A caller all of whose principals bypass field security (F1) gets none: it
    /// needs none.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, FieldShareGrant>> SharesAsync(Caller caller, string entity, IReadOnlyCollection<Guid> recordIds,
        IReadOnlyCollection<string> attributes, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(recordIds);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(session);
        if (recordIds.Count == 0 || attributes.Count == 0) return NoShares;
        if (metadata.GetEntity(entity) is not { } table) return NoShares;

        // The columns asked for (unknown names are ignored), and the columns whose shares apply to them, by MetadataId
        // (attributeid): a companion of a secured column follows its main column, so a share on the column covers it.
        var entries = Current();
        var secured = SecuredColumns(entries, entity);
        var wanted = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase);
        var asked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // column asked for → its main column
        var mains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in table.Attributes)
            if (a.LogicalName is { } name && wanted.Contains(name))
                mains.Add(asked[name] = secured.Main(name));
        var names = new Dictionary<Guid, string>();
        foreach (var a in table.Attributes)
            if (a.MetadataId is { } id && a.LogicalName is { } name && mains.Contains(name)) names[id] = name;
        if (names.Count == 0) return NoShares;

        var restricted = await RestrictedPrincipalsAsync(caller, ct);
        if (restricted.Count == 0) return NoShares;
        var combined = await ForAsync(caller, entity, ct);

        // Per principal and record: the columns it reaches through a share or its own profiles; then the AND over principals.
        var perPrincipal = new List<(FieldPermissionSet Own, Dictionary<Guid, (HashSet<string> Read, HashSet<string> Update)> Shares)>();
        foreach (var principal in restricted)
            perPrincipal.Add((await UserSetAsync(entries, principal, entity, ct),
                await LoadSharesAsync(principal, table.LogicalName, recordIds, names, session, ct)));

        var result = new Dictionary<Guid, FieldShareGrant>();
        foreach (var record in perPrincipal.SelectMany(p => p.Shares.Keys).ToHashSet())
        {
            var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var update = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, main) in asked)
            {
                // Granted by the shares: every principal reaches it on this record, and the combined set does not already allow it.
                if (!combined.CanRead(column) && perPrincipal.All(p => p.Own.CanRead(column) || Has(p.Shares, record, main, update: false)))
                    read.Add(column);
                if (!combined.CanUpdate(column) && perPrincipal.All(p => p.Own.CanUpdate(column) || Has(p.Shares, record, main, update: true)))
                    update.Add(column);
            }
            if (read.Count > 0 || update.Count > 0) result[record] = new FieldShareGrant(read, update);
        }
        return result;

        static bool Has(Dictionary<Guid, (HashSet<string> Read, HashSet<string> Update)> shares, Guid record, string column, bool update) =>
            shares.TryGetValue(record, out var grant) && (update ? grant.Update : grant.Read).Contains(column);
    }

    /// <summary>The shares given to <paramref name="userId"/> or one of its teams, merged with OR per record (records with none are absent).</summary>
    private async Task<Dictionary<Guid, (HashSet<string> Read, HashSet<string> Update)>> LoadSharesAsync(Guid userId, string entity,
        IReadOnlyCollection<Guid> recordIds, Dictionary<Guid, string> names, DataSession session, CancellationToken ct)
    {
        var profile = await cache.GetProfileAsync(userId, ct);
        var principals = new List<Guid>(profile.TeamIds.Count + 1) { userId };
        principals.AddRange(profile.TeamIds);

        await using var cmd = session.Command($"""
            SELECT objectid, attributeid, MAX(CASE WHEN readaccess = {session.Dialect.BooleanLiteral(true)} THEN 1 ELSE 0 END), MAX(CASE WHEN updateaccess = {session.Dialect.BooleanLiteral(true)} THEN 1 ELSE 0 END)
            FROM data.{FieldSecurityTables.Share}
            WHERE objecttypecode = @entity
              AND objectid IN (SELECT CAST({session.Quote("value")} AS {session.Dialect.ColumnType(SqlColumnKind.Guid)}) FROM {session.Dialect.JsonArray("@records")})
              AND attributeid IN (SELECT CAST({session.Quote("value")} AS {session.Dialect.ColumnType(SqlColumnKind.Guid)}) FROM {session.Dialect.JsonArray("@attributes")})
              AND principalid IN (SELECT CAST({session.Quote("value")} AS {session.Dialect.ColumnType(SqlColumnKind.Guid)}) FROM {session.Dialect.JsonArray("@principals")})
            GROUP BY objectid, attributeid
            """);
        cmd.AddParameter("@entity", DbType.String, entity, 128);
        cmd.AddParameter("@records", DbType.String, JsonSerializer.Serialize(recordIds.Distinct()), -1);
        cmd.AddParameter("@attributes", DbType.String, JsonSerializer.Serialize(names.Keys), -1);
        cmd.AddParameter("@principals", DbType.String, JsonSerializer.Serialize(principals), -1);

        var shares = new Dictionary<Guid, (HashSet<string> Read, HashSet<string> Update)>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            if (rd.IsDBNull(0) || rd.IsDBNull(1) || !names.TryGetValue(rd.GetGuid(1), out var column)) continue;
            var record = rd.GetGuid(0);
            if (!shares.TryGetValue(record, out var grant))
                shares[record] = grant = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (rd.GetInt32(2) == 1) grant.Read.Add(column);
            if (rd.GetInt32(3) == 1) grant.Update.Add(column);
        }
        return shares;
    }

    /// <summary>
    /// The users of <paramref name="caller"/> field security applies to (F1), as <see cref="SecurityCache.PrincipalsAsync"/> lists
    /// them (the user, then a non-SYSTEM impersonator), less those holding the System Administrator role. Empty for
    /// <see cref="Caller.Internal"/>, for the SYSTEM user, and when every principal is a System Administrator. Disabled users are
    /// refused upstream by <c>CallerResolver</c> (the impersonated user and the impersonator alike).
    /// </summary>
    private async Task<IReadOnlyList<Guid>> RestrictedPrincipalsAsync(Caller caller, CancellationToken ct)
    {
        if (caller == Caller.Internal || cache.IsUnrestricted(caller, await cache.SystemUserIdAsync(ct))) return [];
        var restricted = new List<Guid>(2);
        foreach (var principal in await cache.PrincipalsAsync(caller, ct))
            if (!principal.IsSystemAdministrator) restricted.Add(principal.UserId);
        return restricted;
    }

    /// <summary>The set of one user (no bypass check) on <paramref name="entity"/>, cached in <paramref name="entries"/>.</summary>
    private async Task<FieldPermissionSet> UserSetAsync(Entries entries, Guid userId, string entity, CancellationToken ct)
    {
        if (entries.Sets.TryGetValue((userId, entity), out var cached)) return cached;

        var secured = SecuredColumns(entries, entity);
        if (!entries.Permissions.TryGetValue(userId, out var permissions))
            entries.Permissions[userId] = permissions = await LoadPermissionsAsync(userId, ct);

        var readable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var creatable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updatable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in secured.Columns)
        {
            // A companion has the rights of its main column.
            var rights = permissions.GetValueOrDefault((entity, secured.Main(column)));
            if (rights.HasFlag(Rights.Read)) readable.Add(column);
            if (rights.HasFlag(Rights.Create)) creatable.Add(column);
            if (rights.HasFlag(Rights.Update)) updatable.Add(column);
        }
        var set = new FieldPermissionSet(secured.Columns, readable, creatable, updatable);
        entries.Sets[(userId, entity)] = set;
        return set;
    }

    /// <summary>The entries of the current <see cref="SecurityCache.Version"/> (new, empty ones after an invalidation).</summary>
    private Entries Current()
    {
        var version = cache.Version;
        var entries = Volatile.Read(ref _entries);
        if (entries.Version == version) return entries;
        var fresh = new Entries(version);
        var previous = Interlocked.CompareExchange(ref _entries, fresh, entries);
        return previous == entries ? fresh : previous.Version == version ? previous : fresh;
    }

    /// <summary>
    /// The secured columns of <paramref name="entity"/> and their companions (none for a table that does not exist, which is
    /// not cached).
    /// </summary>
    private SecuredTable SecuredColumns(Entries entries, string entity)
    {
        if (entries.Secured.TryGetValue(entity, out var cached)) return cached;
        if (metadata.GetEntity(entity) is not { } table) return NoColumns;
        var secured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in table.Attributes)
            if (a.IsSecured == true && a.LogicalName is { } name) secured.Add(name);
        var mainOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in table.Attributes)
            if (a.LogicalName is { } name && !secured.Contains(name) && FieldSecurability.MainColumnOf(table, a) is { } main
                && secured.Contains(main))
                mainOf[name] = main;
        secured.UnionWith(mainOf.Keys);
        var result = new SecuredTable(secured, mainOf);
        entries.Secured[entity] = result;
        return result;
    }

    /// <summary>The OR of the rights of every profile of the user and of its teams, by (table, column).</summary>
    private async Task<IReadOnlyDictionary<(string Entity, string Attribute), Rights>> LoadPermissionsAsync(Guid userId, CancellationToken ct)
    {
        var allowed = FieldSecurityTables.Allowed;
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command($"""
            SELECT fp.{FieldSecurityTables.EntityName}, fp.{FieldSecurityTables.AttributeLogicalName},
                MAX(CASE WHEN fp.{FieldSecurityTables.CanRead} = {allowed} THEN 1 ELSE 0 END),
                MAX(CASE WHEN fp.{FieldSecurityTables.CanCreate} = {allowed} THEN 1 ELSE 0 END),
                MAX(CASE WHEN fp.{FieldSecurityTables.CanUpdate} = {allowed} THEN 1 ELSE 0 END)
            FROM data.{FieldSecurityTables.Permission} AS fp
            WHERE fp.{FieldSecurityTables.EntityName} IS NOT NULL AND fp.{FieldSecurityTables.AttributeLogicalName} IS NOT NULL
              AND fp.{FieldSecurityTables.ProfileId} IN (
                SELECT up.{FieldSecurityTables.ProfileId} FROM data.{FieldSecurityTables.UserProfiles} AS up WHERE up.systemuserid = @user
                UNION
                SELECT tp.{FieldSecurityTables.ProfileId}
                FROM data.{FieldSecurityTables.TeamProfiles} AS tp
                JOIN data.teammembership AS tm ON tm.teamid = tp.teamid AND tm.systemuserid = @user)
            GROUP BY fp.{FieldSecurityTables.EntityName}, fp.{FieldSecurityTables.AttributeLogicalName}
            """);
        cmd.AddParameter("@user", DbType.Guid, userId);
        var rights = new Dictionary<(string, string), Rights>(EntityAttributeComparer.Instance);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var r = Rights.None;
            if (rd.GetInt32(2) == 1) r |= Rights.Read;
            if (rd.GetInt32(3) == 1) r |= Rights.Create;
            if (rd.GetInt32(4) == 1) r |= Rights.Update;
            var key = (rd.GetString(0), rd.GetString(1));
            rights[key] = rights.GetValueOrDefault(key) | r; // names differing only by case merge too
        }
        return rights;
    }

    /// <summary>(table, column) pairs compared ignoring case.</summary>
    private sealed class EntityAttributeComparer : IEqualityComparer<(string, string)>
    {
        public static readonly EntityAttributeComparer Instance = new();

        public bool Equals((string, string) x, (string, string) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1) && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}
