using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Phase 5 of an import (spec §7.6 step 5, plan M4a task 4): the N:N rows of the package, associated through the core in sub-chunks
/// of 1000 (<c>nn/&lt;schema&gt;#&lt;n&gt;</c>). A pair that already exists is done; a pair whose record is not in the organization, or a
/// relationship the organization lacks, is reported, and so are the pairs with an end that cannot be read (counted per relationship).
/// The pairs of a relationship a map renamed (R8) go to its target relationship.
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
            var (key1, key2) = SourceKeys(context.Package, r);
            // A pair with an end that cannot be read is not imported: counted once per relationship, never dropped silently.
            var unreadable = rows.Count(p => End(p, r.Entity1IntersectAttribute, key1) is null || End(p, r.Entity2IntersectAttribute, key2) is null);
            if (unreadable > 0 && !await context.Store.IsChunkDoneAsync(context.RunId, $"nn/{r.SchemaName}#unreadable", ct))
            {
                await using var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
                await using var tx = conn.BeginTransaction();
                var session = new DataSession(conn, tx);
                await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "nn-pair-unreadable", r.IntersectEntityName, null, r.SchemaName,
                    $"{unreadable} row(s)"), session, ct);
                await context.Store.MarkChunkAsync(context.RunId, PhaseOrder, $"nn/{r.SchemaName}#unreadable", 0, 0, session, ct);
                await tx.CommitAsync(ct);
            }
            var n = 0;
            foreach (var slice in ImportChunkWriter.Slices(rows))
                await chunks.WriteAsync(PhaseOrder, $"nn/{r.SchemaName}#{n++}", r.IntersectEntityName, slice, async (s, part, token) =>
                {
                    var pairs = part.Select(p => (Row: p, One: End(p, r.Entity1IntersectAttribute, key1), Two: End(p, r.Entity2IntersectAttribute, key2)))
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

    /// <summary>The id of one end of a pair: under the target's intersect column, else under the source key of its table.</summary>
    private static Guid? End(MigrationRecord pair, string? intersectColumn, string? sourceKey) =>
        SecurityPhase.Id(pair, intersectColumn!) ?? (sourceKey is null ? null : SecurityPhase.Id(pair, sourceKey));

    /// <summary>
    /// The intersect columns, as exported, of the two ends of <paramref name="r"/> when a map renamed their tables. The pairs of a
    /// mapped relationship (R8) keep the source's intersect columns, which Dataverse names after the key of each end's table
    /// (<c>leadid</c>), not the target's (<c>xv_leadid</c>); for a relationship of a table with itself, after the key with
    /// <c>one</c> and <c>two</c> (<c>leadidone</c>, <c>leadidtwo</c>). Null for an end whose table keeps its name.
    /// </summary>
    private static (string? One, string? Two) SourceKeys(PackageReader package, ManyToManyRelationshipMetadata r)
    {
        // The translated manifest lists the tables in the order of the raw one: the same index is the same table.
        string? Key(string? target) => package.RawManifest.Tables.Zip(package.Manifest.Tables)
            .Where(t => t.Second.LogicalName == target && t.First.LogicalName != target).Select(t => t.First.PrimaryKey).FirstOrDefault();
        if (r.Entity1LogicalName != r.Entity2LogicalName) return (Key(r.Entity1LogicalName), Key(r.Entity2LogicalName));
        return Key(r.Entity1LogicalName) is { } key ? (key + "one", key + "two") : (null, null);
    }
}
