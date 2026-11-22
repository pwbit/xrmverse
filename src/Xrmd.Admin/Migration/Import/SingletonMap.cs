using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Migration.Records;
using Xrmd.Migration.Users;

namespace Xrmd.Admin.Migration.Import;

/// <summary>What the import does with one exported user (spec §7.4): its action and the action's argument.</summary>
public sealed record UserPlan(string Action, Guid? ObjectId, string? UserName, Guid? Target);

/// <summary>
/// The id map of the rows the target already has (spec §7.3, plan M4a): the root business unit, the SYSTEM and INTEGRATION users,
/// users mapped onto existing users, the base currency and the roles. Built at the start of the security phase and stored in the
/// run's id map, which every later lookup goes through.
/// </summary>
public static class SingletonMap
{
    /// <summary>The plan of every exported user: the mapping's entry, else the default of the user's kind.</summary>
    public static UserPlan PlanOf(MigrationRecord user, UserMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Users.TryGetValue(user.Id, out var e)) return new UserPlan(e.Action, e.ObjectId, e.UserName, e.Target);
        var action = UserMappingTemplate.Kind(user) switch
        {
            UserMappingTemplate.ApplicationKind => mapping.Defaults.Application,
            UserMappingTemplate.DisabledKind => mapping.Defaults.Disabled,
            _ => mapping.Defaults.Licensed,
        };
        // A licensed user without an object id cannot sign in with Entra: the template proposes a local user (spec §7.4).
        if (action == UserActions.Entra && UserMappingTemplate.ObjectId(user) is null) action = UserActions.Local;
        return new UserPlan(action, null, action == UserActions.Local ? UserMappingTemplate.ProposedUserName(user) : null, null);
    }

    /// <summary>Maps the singletons into the run's id map; returns the users mapped onto existing users (not created).</summary>
    /// <exception cref="MigrationPhaseException">An <c>existing</c> target that does not exist; a base currency with another ISO code
    /// without <c>baseCurrencyMap</c>.</exception>
    public static async Task<HashSet<Guid>> BuildAsync(MigrationPhaseContext ctx, CoreOperations core, DataSession s,
        IReadOnlyList<MigrationRecord> businessUnits, IReadOnlyList<MigrationRecord> users, IReadOnlyList<MigrationRecord> roles,
        IReadOnlyList<MigrationRecord> currencies, UserMapping mapping, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(core);
        var ledger = ctx.Ledger;
        var rootBu = (Guid)(await ScalarAsync(s, s.Dialect.Limit("SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL", "1"), ct)
            ?? throw new MigrationPhaseException("The organization has no root business unit."));
        foreach (var root in businessUnits.Where(b => b.Values.GetValueOrDefault("parentbusinessunitid") is null or NullValue))
            await ledger.MapAsync(root.Id, rootBu, s, ct);

        var systemUser = await core.SystemUserIdAsync(s, ct);
        var byObjectId = new Dictionary<Guid, Guid>();
        await using (var cmd = s.Command("SELECT systemuserid, azureactivedirectoryobjectid FROM data.systemuser WHERE azureactivedirectoryobjectid IS NOT NULL"))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) byObjectId[rd.GetGuid(1)] = rd.GetGuid(0);
        var mappedUsers = new HashSet<Guid>();
        foreach (var user in users)
        {
            Guid? target = null;
            if (UserMappingTemplate.IsSingleton(user)) target = systemUser;
            else
            {
                var plan = PlanOf(user, mapping);
                if (plan.Action == UserActions.Existing)
                {
                    target = plan.Target!.Value;
                    if (await ScalarAsync(s, "SELECT 1 FROM data.systemuser WHERE systemuserid = @id", ct, target.Value) is null)
                        throw new MigrationPhaseException($"The user {user.Id} is mapped onto the user {target}, which does not exist in the organization.");
                    await ledger.IssueAsync(new MigrationIssue(3, MigrationIssue.Info, "user-existing", "systemuser", user.Id, null,
                        $"The user is mapped onto the organization's user {target}."), ct);
                }
                else if ((plan.Action == UserActions.EntraRemap ? plan.ObjectId : plan.Action == UserActions.Entra ? UserMappingTemplate.ObjectId(user) : null)
                         is { } oid && byObjectId.TryGetValue(oid, out var existing) && existing != user.Id)
                {
                    // The same Entra user already signs in to the organization (often the administrator importing): one user, not two.
                    // existing == user.Id is the user an earlier attempt of this run created: written again by id, not mapped.
                    target = existing;
                    await ledger.IssueAsync(new MigrationIssue(3, MigrationIssue.Info, "user-existing", "systemuser", user.Id, null,
                        $"The user has the Entra object id {oid} of the organization's user {existing}: it is mapped onto that user."), ct);
                }
            }
            if (target is not { } t) continue;
            await ledger.MapAsync(user.Id, t, s, ct);
            mappedUsers.Add(user.Id);
        }

        await MapBaseCurrencyAsync(ctx, s, currencies, ct);
        await MapRolesAsync(ledger, s, roles, ct);
        return mappedUsers;
    }

    /// <summary>
    /// The source's base currency (the one with exchange rate 1, the target's ISO code first) onto the target's. Another ISO code
    /// fails the phase unless the run option <c>baseCurrencyMap</c> is <c>true</c>.
    /// </summary>
    private static async Task MapBaseCurrencyAsync(MigrationPhaseContext ctx, DataSession s, IReadOnlyList<MigrationRecord> currencies, CancellationToken ct)
    {
        if (currencies.Count == 0) return;
        Guid targetId;
        string targetCode;
        await using (var cmd = s.Command(s.Dialect.Limit("""
            SELECT c.transactioncurrencyid, c.isocurrencycode FROM data.organization o
            JOIN data.transactioncurrency c ON c.transactioncurrencyid = o.basecurrencyid
            """, "1")))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await rd.ReadAsync(ct)) return;
            targetId = rd.GetGuid(0);
            targetCode = rd.GetString(1);
        }
        static string? Code(MigrationRecord c) => c.Values.GetValueOrDefault("isocurrencycode") is StringValue v ? v.Value : null;
        var same = currencies.FirstOrDefault(c => string.Equals(Code(c), targetCode, StringComparison.OrdinalIgnoreCase));
        if (same is not null)
        {
            await ctx.Ledger.MapAsync(same.Id, targetId, s, ct);
            return;
        }
        var baseCandidate = currencies.FirstOrDefault(c => c.Values.GetValueOrDefault("exchangerate") is DecimalValue { Value: 1m } or IntValue { Value: 1 });
        if (baseCandidate is null) return;
        if (!(ctx.Options.TryGetValue("baseCurrencyMap", out var map) && map == "true"))
            throw new MigrationPhaseException(
                $"The package's base currency is {Code(baseCandidate)}, the organization's is {targetCode}: start the import with the option " +
                "baseCurrencyMap to map it onto the organization's base currency, or create the organization with the same base currency.");
        await ctx.Ledger.MapAsync(baseCandidate.Id, targetId, s, ct);
        await ctx.Ledger.IssueAsync(new MigrationIssue(3, MigrationIssue.Warning, "currency-mapped", "transactioncurrency", baseCandidate.Id, null,
            $"The base currency {Code(baseCandidate)} is mapped onto the organization's base currency {targetCode}."), ct);
    }

    /// <summary>Each exported role onto the target role with its id, else its role template, else its name.</summary>
    private static async Task MapRolesAsync(MigrationLedger ledger, DataSession s, IReadOnlyList<MigrationRecord> roles, CancellationToken ct)
    {
        if (roles.Count == 0) return;
        var target = new List<(Guid Id, string Name, Guid? Template)>();
        await using (var cmd = s.Command("SELECT roleid, name, roletemplateid FROM data.role ORDER BY roleid"))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) target.Add((rd.GetGuid(0), rd.IsDBNull(1) ? "" : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetGuid(2)));
        foreach (var role in roles)
        {
            if (target.Any(t => t.Id == role.Id)) continue;
            var template = role.Values.GetValueOrDefault("roletemplateid") switch { LookupValue l => l.Id, GuidValue g => g.Value, _ => (Guid?)null };
            var name = role.Values.GetValueOrDefault("name") is StringValue n ? n.Value : null;
            var match = template is { } tid && target.FirstOrDefault(t => t.Template == tid) is { Id: var byTemplate } && byTemplate != Guid.Empty ? byTemplate
                : name is not null && target.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) is { Id: var byName } && byName != Guid.Empty ? byName
                : (Guid?)null;
            if (match is { } m) await ledger.MapAsync(role.Id, m, s, ct);
        }
    }

    private static async Task<object?> ScalarAsync(DataSession s, string sql, CancellationToken ct, Guid? id = null)
    {
        await using var cmd = s.Command(sql);
        if (id is { } value) cmd.AddParameter("@id", DbType.Guid, value);
        return await cmd.ExecuteScalarAsync(ct);
    }
}
