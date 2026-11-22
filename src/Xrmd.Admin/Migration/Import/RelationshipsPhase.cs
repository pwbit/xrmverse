using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Migration.Records;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Phase 5 of an import (spec §7.6 step 5, plan M4a task 4): the N:N rows of the package, associated through the core in sub-chunks
/// of 1000 (<c>nn/&lt;schema&gt;#&lt;n&gt;</c>). A pair that already exists is done; a pair whose record is not in the organization, or a
/// relationship the organization lacks, is reported.
/// </summary>
public sealed class RelationshipsPhase : IMigrationPhase
{
    public const int PhaseOrder = 5;

    public int Order => PhaseOrder;

    public string Name => "nn";

    public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.Services;
        var metadata = services.GetRequiredService<IMetadataStore>();
        var core = services.GetRequiredService<CoreOperations>();
        var ledger = context.Ledger;
        var idMap = await ledger.LoadMapAsync(ct);
        var chunks = new ImportChunkWriter(services.GetRequiredService<IDbConnectionFactory>(), context.Store, ledger);
        foreach (var relationship in context.Package.Manifest.Relationships)
        {
            if (metadata.GetRelationship(relationship.SchemaName) is not ManyToManyRelationshipMetadata r)
            {
                if (!await context.Store.IsChunkDoneAsync(context.RunId, $"nn/{relationship.SchemaName}#missing", ct))
                {
                    await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "relationship-missing", null, null, relationship.SchemaName,
                        $"The relationship '{relationship.SchemaName}' does not exist in the organization: its {relationship.Pairs} pairs are not imported."), ct);
                    await using var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
                    await context.Store.MarkChunkAsync(context.RunId, PhaseOrder, $"nn/{relationship.SchemaName}#missing", 0, 0, new DataSession(conn, null), ct);
                }
                continue;
            }
            context.Progress.Report($"nn: {r.SchemaName} ({relationship.Pairs})");
            var rows = new List<MigrationRecord>();
            await foreach (var row in ChunkReader.ReadAsync(context.Package, relationship.Chunks, ct)) rows.Add(row);
            var n = 0;
            foreach (var slice in ImportChunkWriter.Slices(rows))
                await chunks.WriteAsync(PhaseOrder, $"nn/{r.SchemaName}#{n++}", r.IntersectEntityName, slice, async (s, part, token) =>
                {
                    var pairs = part.Select(p => (Row: p, One: SecurityPhase.Id(p, r.Entity1IntersectAttribute), Two: SecurityPhase.Id(p, r.Entity2IntersectAttribute)))
                        .Where(p => p.One is not null && p.Two is not null)
                        .Select(p => (p.Row, One: ImportValueMapper.Map(idMap, p.One!.Value), Two: ImportValueMapper.Map(idMap, p.Two!.Value))).ToList();
                    var existing = (await ImportRecordBuilder.ExistingAsync(s, metadata,
                        [.. pairs.Select(p => (r.Entity1LogicalName, p.One)), .. pairs.Select(p => (r.Entity2LogicalName, p.Two))], token)).ToHashSet();
                    foreach (var (row, one, two) in pairs)
                    {
                        if (!existing.Contains((r.Entity1LogicalName, one)) || !existing.Contains((r.Entity2LogicalName, two)))
                        {
                            await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "nn-record-missing", r.IntersectEntityName, row.Id, null,
                                $"The pair {r.Entity1LogicalName} {one} – {r.Entity2LogicalName} {two} of '{r.SchemaName}' has a record that is not in the organization."), s, token);
                            continue;
                        }
                        try
                        {
                            await core.AssociateAsync(s, context.Caller, new EntityReference(r.Entity1LogicalName, one), new Relationship(r.SchemaName),
                                [new EntityReference(r.Entity2LogicalName, two)], associate: true, token);
                        }
                        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.DuplicateRecord)
                        {
                            // Already associated (a resume).
                        }
                    }
                }, ct);
        }
    }
}
