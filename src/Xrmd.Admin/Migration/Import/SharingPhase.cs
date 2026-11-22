using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Migration.Records;
using Xrmd.Security;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Phase 7 of an import (spec §7.6 step 6b, plan M4a task 5): the access teams made from a template for a record and their members,
/// then the record shares (<c>principalobjectaccess</c> rows with direct rights) re-applied through the core with their access mask.
/// A share whose record or principal is not in the organization is reported.
/// </summary>
public sealed class SharingPhase : IMigrationPhase
{
    public const int PhaseOrder = 7;

    /// <summary>
    /// Read, Write, Append, AppendTo, Delete, Share and Assign: the rights a share can give. Dataverse keeps Create (0x20) in some
    /// masks, but a share cannot give it (the core refuses it): it is dropped.
    /// </summary>
    private const int ShareableRights = 0x1 | 0x2 | 0x4 | 0x10 | 0x10000 | 0x40000 | 0x80000;

    public int Order => PhaseOrder;

    public string Name => "sharing";

    public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.Services;
        var metadata = services.GetRequiredService<IMetadataStore>();
        var core = services.GetRequiredService<CoreOperations>();
        var security = services.GetRequiredService<SecurityAdministration>();
        var ledger = context.Ledger;
        var package = context.Package;
        var idMap = await ledger.LoadMapAsync(ct);
        var chunks = new ImportChunkWriter(services.GetRequiredService<IDbConnectionFactory>(), context.Store, ledger);
        var builder = new ImportRecordBuilder(metadata, idMap, new HashSet<string>(package.Manifest.Tables.Select(t => t.LogicalName), StringComparer.Ordinal),
            context.Options.TryGetValue("strictLookups", out var strict) && strict == "true");
        var writer = new ImportRecordWriter(core, context.Caller, ledger, builder, PhaseOrder, metadata);

        var accessTeams = (await SecurityPhase.ReadAsync(package, "team", ct))
            .Where(t => t.Values.GetValueOrDefault("teamtype") is OptionValue { Value: SecurityPhase.AccessTeam }).ToList();
        var n = 0;
        foreach (var slice in ImportChunkWriter.Slices(accessTeams))
            await chunks.WriteAsync(PhaseOrder, $"sharing/team#{n++}", "team", slice, (s, part, token) => writer.WriteAsync(s, "team", part, null, token), ct);

        var teamIds = accessTeams.Select(t => t.Id).ToHashSet();
        var members = (await SecurityPhase.ReadAsync(package, "teammembership", ct))
            .Where(m => SecurityPhase.Id(m, "teamid") is { } team && teamIds.Contains(team)).ToList();
        n = 0;
        foreach (var slice in ImportChunkWriter.Slices(members))
            await chunks.WriteAsync(PhaseOrder, $"sharing/teammembership#{n++}", "teammembership", slice, async (s, part, token) =>
            {
                foreach (var m in part)
                {
                    try { await security.AddMemberAsync(s, Map(SecurityPhase.Id(m, "teamid")!.Value), Map(SecurityPhase.Id(m, "systemuserid")!.Value), token); }
                    catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.DuplicateRecord) { }
                }
            }, ct);

        // The shares are read one package chunk at a time (an organization can have millions): each chunk is sliced into sub-chunks.
        IReadOnlyList<string> poa = package.Manifest.Tables.FirstOrDefault(t => t.LogicalName == "principalobjectaccess")?.Chunks ?? [];
        context.Progress.Report($"sharing: {poa.Count} chunks of shares");
        foreach (var chunk in poa)
        {
            var shares = new List<MigrationRecord>();
            await foreach (var p in ChunkReader.ReadAsync(package, [chunk], ct))
                if (p.Values.GetValueOrDefault("accessrightsmask") is IntValue { Value: not 0 }) shares.Add(p);
            n = 0;
            foreach (var slice in ImportChunkWriter.Slices(shares))
                await chunks.WriteAsync(PhaseOrder, $"sharing/{chunk}#{n++}", "principalobjectaccess", slice, async (s, part, token) =>
                {
                    var parsed = part.Select(p => (Row: p, Target: Reference(p, "objectid", "objecttypecode"), Principal: Reference(p, "principalid", "principaltypecode")))
                        .ToList();
                    var existing = (await ImportRecordBuilder.ExistingAsync(s, metadata,
                        [.. parsed.SelectMany(p => new[] { p.Target, p.Principal }).OfType<EntityReference>().Select(r => (r.LogicalName, r.Id))], token)).ToHashSet();
                    foreach (var (row, target, principal) in parsed)
                    {
                        if (target is null || principal is null || !existing.Contains((target.LogicalName, target.Id)) || !existing.Contains((principal.LogicalName, principal.Id)))
                        {
                            await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "share-missing", target?.LogicalName, target?.Id, null,
                                $"A share of {target?.LogicalName} {target?.Id} with {principal?.LogicalName} {principal?.Id} is not imported: the record or the principal is not in the organization."), s, token);
                            continue;
                        }
                        if (metadata.GetEntity(target.LogicalName)?.OwnershipType is not (OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned))
                        {
                            // Dataverse also shares rows of tables without an owner (systemuser, for one); xrmverse shares only user or team owned rows.
                            await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "share-not-shareable", target.LogicalName, target.Id, null,
                                $"A share of {target.LogicalName} {target.Id} with {principal.LogicalName} {principal.Id} is not imported: {target.LogicalName} is not user or team owned, and only those rows can be shared."), s, token);
                            continue;
                        }
                        var mask = (int)((IntValue)row.Values["accessrightsmask"]).Value & ShareableRights;
                        if (mask == 0) continue;
                        await core.ShareAsync(s, context.Caller, target, new PrincipalAccess { Principal = principal, AccessMask = (AccessRights)mask }, replace: true, token);
                    }
                }, ct);
        }

        Guid Map(Guid id) => ImportValueMapper.Map(idMap, id);

        EntityReference? Reference(MigrationRecord row, string idColumn, string typeColumn)
        {
            if (SecurityPhase.Id(row, idColumn) is not { } id) return null;
            var table = row.Values.GetValueOrDefault(typeColumn) switch
            {
                StringValue t => t.Value,
                OptionValue o => metadata.GetEntityByObjectTypeCode(o.Value)?.LogicalName,
                IntValue i => metadata.GetEntityByObjectTypeCode((int)i.Value)?.LogicalName,
                _ => row.Values.GetValueOrDefault(idColumn) is LookupValue l ? l.Table : null,
            };
            return table is null ? null : new EntityReference(table, Map(id));
        }
    }
}
