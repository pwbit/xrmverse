using Xrmd.Migration.Users;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;
using Xrmd.Security;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Phase 3 of an import (spec §7.3, §7.4, §7.6 step 3; plan M4a task 2): maps the singletons, then writes the business units (parent
/// first), the users by their mapping action, the teams, the team members, the role assignments and the field security profile
/// assignments, with their exported ids. Access teams (made from a template for a record) and their members are left to the sharing
/// phase; Entra group teams become owner teams with the members of the export.
/// </summary>
public sealed class SecurityPhase : IMigrationPhase
{
    public const int PhaseOrder = 3;

    public int Order => PhaseOrder;

    public string Name => "security";

    public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.Services;
        var core = services.GetRequiredService<CoreOperations>();
        var security = services.GetRequiredService<SecurityAdministration>();
        var factory = services.GetRequiredService<IDbConnectionFactory>();
        var ledger = context.Ledger;
        var package = context.Package;
        var mapping = UsersOf(context.Options);

        var businessUnits = await ReadAsync(package, "businessunit", ct);
        var users = await ReadAsync(package, "systemuser", ct);
        var teams = await ReadAsync(package, "team", ct);
        var roles = await ReadAsync(package, "role", ct);
        var currencies = await ReadAsync(package, "transactioncurrency", ct);

        context.Progress.Report("security: mapping the organization's own rows");
        HashSet<Guid> mappedUsers;
        await using (var conn = await factory.OpenAsync(ct))
        await using (var tx = conn.BeginTransaction())
        {
            mappedUsers = await SingletonMap.BuildAsync(context, core, new DataSession(conn, tx), businessUnits, users, roles, currencies, mapping, ct);
            await tx.CommitAsync(ct);
        }

        var idMap = await ledger.LoadMapAsync(ct);
        var builder = new ImportRecordBuilder(services.GetRequiredService<IMetadataStore>(), idMap,
            new HashSet<string>(package.Manifest.Tables.Select(t => t.LogicalName), StringComparer.Ordinal),
            context.Options.TryGetValue("strictLookups", out var strict) && strict == "true");
        var writer = new ImportRecordWriter(core, context.Caller, ledger, builder, PhaseOrder, services.GetRequiredService<IMetadataStore>());
        var chunks = new ImportChunkWriter(factory, context.Store, ledger);

        async Task WriteTableAsync(string table, IReadOnlyList<MigrationRecord> rows, Func<DataSession, MigrationRecord, Entity, Task<Entity?>>? adjust)
        {
            context.Progress.Report($"security: {table} ({rows.Count})");
            var n = 0;
            foreach (var slice in ImportChunkWriter.Slices(rows))
                await chunks.WriteAsync(PhaseOrder, $"security/{table}#{n++}", table, slice,
                    (s, part, token) => writer.WriteAsync(s, table, part, adjust, token), ct);
        }

        await WriteTableAsync("businessunit", ParentFirst(businessUnits.Where(b => !idMap.ContainsKey(b.Id)).ToList()), null);

        var plans = users.Where(u => !mappedUsers.Contains(u.Id)).ToDictionary(u => u.Id, u => SingletonMap.PlanOf(u, mapping));
        await WriteTableAsync("systemuser", [.. users.Where(u => plans.ContainsKey(u.Id))], async (s, user, entity) =>
        {
            var plan = plans[user.Id];
            switch (plan.Action)
            {
                case UserActions.EntraRemap:
                    entity["azureactivedirectoryobjectid"] = plan.ObjectId;
                    break;
                case UserActions.Local:
                    entity["domainname"] = plan.UserName;
                    entity.Attributes.Remove("azureactivedirectoryobjectid");
                    break;
                case UserActions.Stub:
                    entity["isdisabled"] = true;
                    entity.Attributes.Remove("azureactivedirectoryobjectid");
                    break;
            }
            await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Info, "user-" + plan.Action, "systemuser", entity.Id, null,
                $"Created as {plan.Action}."), s, ct);
            return entity;
        });

        var teamTypes = teams.ToDictionary(t => t.Id, t => t.Values.GetValueOrDefault("teamtype") is OptionValue o ? o.Value : 0);
        await WriteTableAsync("team", [.. teams.Where(t => teamTypes[t.Id] != AccessTeam && !idMap.ContainsKey(t.Id))], async (s, team, entity) =>
        {
            if (teamTypes[team.Id] is 2 or 3)
            {
                entity["teamtype"] = new OptionSetValue(0);
                entity.Attributes.Remove("azureactivedirectoryobjectid");
                await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "team-group-to-owner", "team", entity.Id, null,
                    "An Entra group team becomes an owner team with the members it had at export."), s, ct);
            }
            return entity;
        });

        // A user mapped onto a user the organization already has (existing, the same Entra object id, SYSTEM, INTEGRATION) keeps the
        // memberships, roles and profiles it has in the organization: the source's are not granted to it.
        async Task<bool> SkippedForExistingAsync(DataSession s, string table, MigrationRecord row, string principal, Guid who, CancellationToken token)
        {
            if (principal != "systemuser" || !mappedUsers.Contains(who)) return false;
            await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "grant-skipped-existing", table, row.Id, null,
                $"The user {who} is mapped onto the organization's user {builder.Map(who)}, which keeps its own grants: this one is not imported."), s, token);
            return true;
        }

        var memberships = (await ReadAsync(package, "teammembership", ct))
            .Where(m => Id(m, "teamid") is not { } team || teamTypes.GetValueOrDefault(team) != AccessTeam).ToList();
        await WriteLinksAsync(chunks, "teammembership", memberships, async (s, row, token) =>
        {
            if (Id(row, "teamid") is not { } team || Id(row, "systemuserid") is not { } user) return;
            if (await SkippedForExistingAsync(s, "teammembership", row, "systemuser", user, token)) return;
            await IgnoreDuplicateAsync(security.AddMemberAsync(s, builder.Map(team), builder.Map(user), token));
        }, ct);

        foreach (var (table, principal, column) in new[] { ("systemuserroles", "systemuser", "systemuserid"), ("teamroles", "team", "teamid") })
            await WriteLinksAsync(chunks, table, await ReadAsync(package, table, ct), async (s, row, token) =>
            {
                if (Id(row, column) is not { } who || Id(row, "roleid") is not { } source) return;
                if (await SkippedForExistingAsync(s, table, row, principal, who, token)) return;
                var role = builder.Map(source);
                if ((await ImportRecordBuilder.ExistingAsync(s, services.GetRequiredService<IMetadataStore>(), [("role", role)], token)).Count == 0)
                {
                    await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "role-unknown", table, row.Id, "roleid",
                        $"The role {source} is not in the organization (by id, role template or name): the assignment to {principal} {who} is not imported."), s, token);
                    return;
                }
                await IgnoreDuplicateAsync(security.AddRoleAsync(s, new EntityReference(principal, builder.Map(who)), role, token));
            }, ct);

        foreach (var (table, principal, column) in new[] { ("systemuserprofiles", "systemuser", "systemuserid"), ("teamprofiles", "team", "teamid") })
            await WriteLinksAsync(chunks, table, await ReadAsync(package, table, ct), async (s, row, token) =>
            {
                if (Id(row, column) is not { } who || Id(row, "fieldsecurityprofileid") is not { } profile) return;
                if (await SkippedForExistingAsync(s, table, row, principal, who, token)) return;
                if ((await ImportRecordBuilder.ExistingAsync(s, services.GetRequiredService<IMetadataStore>(), [("fieldsecurityprofile", builder.Map(profile))], token)).Count == 0)
                {
                    await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "profile-unknown", table, row.Id, "fieldsecurityprofileid",
                        $"The field security profile {profile} is not in the organization: the assignment to {principal} {who} is not imported."), s, token);
                    return;
                }
                await IgnoreDuplicateAsync(security.AddProfileAsync(s, new EntityReference(principal, builder.Map(who)), builder.Map(profile), token));
            }, ct);
    }

    /// <summary>The team type of an access team, made from a template for one record (sharing phase).</summary>
    public const int AccessTeam = 1;

    /// <summary>The users mapping stored in the run's options (<c>users</c>, as JSON text); the empty mapping when absent.</summary>
    public static UserMapping UsersOf(IReadOnlyDictionary<string, string> options) =>
        options.TryGetValue("users", out var json) && json.Length > 0 ? UserMapping.Parse(JsonNode.Parse(json)) : UserMapping.Empty;

    /// <summary>All the records of a table of the package (security tables are small); none when the package lacks it.</summary>
    public static async Task<List<MigrationRecord>> ReadAsync(PackageReader package, string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        var rows = new List<MigrationRecord>();
        if (package.Manifest.Tables.FirstOrDefault(t => t.LogicalName == table) is not { } entry) return rows;
        await foreach (var r in ChunkReader.ReadAsync(package, entry.Chunks, ct)) rows.Add(r);
        return rows;
    }

    /// <summary>Business units with each parent before its children (then by id, so that a resume slices them the same way).</summary>
    private static List<MigrationRecord> ParentFirst(List<MigrationRecord> units)
    {
        var parent = units.ToDictionary(u => u.Id, u => Id(u, "parentbusinessunitid"));
        int Depth(Guid id)
        {
            var depth = 0;
            for (var p = parent[id]; p is { } pid && parent.ContainsKey(pid) && depth <= units.Count; p = parent[pid]) depth++;
            return depth;
        }
        return [.. units.OrderBy(u => Depth(u.Id)).ThenBy(u => u.Id)];
    }

    /// <summary>The id a column holds: a lookup's or a GUID's.</summary>
    public static Guid? Id(MigrationRecord row, string column) => row.Values.GetValueOrDefault(column) switch
    {
        LookupValue l => l.Id,
        GuidValue g => g.Value,
        _ => null,
    };

    /// <summary>Writes the rows of an intersect table, one link per row, in sub-chunks.</summary>
    private static async Task WriteLinksAsync(ImportChunkWriter chunks, string table, IReadOnlyList<MigrationRecord> rows,
        Func<DataSession, MigrationRecord, CancellationToken, Task> link, CancellationToken ct)
    {
        var n = 0;
        foreach (var slice in ImportChunkWriter.Slices(rows))
            await chunks.WriteAsync(PhaseOrder, $"security/{table}#{n++}", table, slice, async (s, part, token) =>
            {
                foreach (var row in part) await link(s, row, token);
            }, ct);
    }

    /// <summary>A link that already exists (a resume) is done.</summary>
    private static async Task IgnoreDuplicateAsync(Task add)
    {
        try { await add; }
        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.DuplicateRecord) { }
    }
}
