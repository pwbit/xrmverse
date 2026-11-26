using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Phase 4 of an import (spec §7.6 step 4, plan M4a task 3): the data tables of the package in dependency order
/// (<see cref="TableOrder"/>), each package chunk in sub-chunks of at most 1000 rows, one transaction each, recorded in the ledger
/// as <c>&lt;chunk&gt;#&lt;n&gt;</c>. Records keep their ids, system columns and state; a lookup to a row not written yet is deferred,
/// one to a record outside the package is nulled (or fails the row with <c>strictLookups</c>). A table the organization lacks is
/// reported and skipped.
/// </summary>
public sealed class RecordsPhase : IMigrationPhase
{
    public const int PhaseOrder = 4;

    public int Order => PhaseOrder;

    public string Name => "records";

    public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.Services;
        var metadata = services.GetRequiredService<IMetadataStore>();
        var factory = services.GetRequiredService<IDbConnectionFactory>();
        var ledger = context.Ledger;
        var manifest = context.Package.Manifest;
        var tables = DataTables(manifest).ToDictionary(t => t.LogicalName, StringComparer.Ordinal);

        var present = new List<string>();
        var candidates = context.Package.RawManifest.ArchiveCandidates.Select(c => c.LogicalName).ToHashSet(StringComparer.Ordinal);
        foreach (var table in tables.Values.OrderBy(t => t.LogicalName, StringComparer.Ordinal))
        {
            if (metadata.GetEntity(table.LogicalName) is not null)
            {
                present.Add(table.LogicalName);
                continue;
            }
            if (await context.Store.IsChunkDoneAsync(context.RunId, $"records/{table.LogicalName}#missing", ct)) continue;
            await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "table-missing", table.LogicalName, null, null,
                $"The table '{table.LogicalName}' does not exist in the organization: its {table.Records} records are not imported." +
                (candidates.Contains(table.LogicalName)
                    ? $" It is an archive candidate of the package: a map entry '{{ source: {table.LogicalName}, archive: true }}' keeps its records in an archive table."
                    : "")), ct);
            await using var conn = await factory.OpenAsync(ct);
            await context.Store.MarkChunkAsync(context.RunId, PhaseOrder, $"records/{table.LogicalName}#missing", 0, 0, new DataSession(conn, null), ct);
        }

        var builder = new ImportRecordBuilder(metadata, await ledger.LoadMapAsync(ct),
            new HashSet<string>(manifest.Tables.Select(t => t.LogicalName), StringComparer.Ordinal),
            context.Options.TryGetValue("strictLookups", out var strict) && strict == "true");
        var writer = new ImportRecordWriter(services.GetRequiredService<CoreOperations>(), context.Caller, ledger, builder, PhaseOrder, metadata);
        var chunks = new ImportChunkWriter(factory, context.Store, ledger);
        foreach (var table in TableOrder.Sort(present, t => Lookups(metadata, t)))
        {
            context.Progress.Report($"records: {table} ({tables[table].Records})");
            foreach (var chunk in tables[table].Chunks)
            {
                var rows = new List<MigrationRecord>();
                await foreach (var r in ChunkReader.ReadAsync(context.Package, [chunk], ct)) rows.Add(r);
                var n = 0;
                foreach (var slice in ImportChunkWriter.Slices(rows))
                    await chunks.WriteAsync(PhaseOrder, $"{chunk}#{n++}", table, slice, (s, part, token) => writer.WriteAsync(s, table, part, null, token), ct);
            }
        }
    }

    /// <summary>The data tables of the package: those under <c>data/</c> (not the security tables, not the N:N rows).</summary>
    public static IEnumerable<PackageTable> DataTables(PackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Tables.Where(t => t.Chunks.Count > 0 && t.Chunks.All(c => c.StartsWith("data/", StringComparison.Ordinal)));
    }

    /// <summary>The lookups of a table and the tables they reference, from the organization's metadata.</summary>
    private static IEnumerable<(string Column, string Target)> Lookups(IMetadataStore metadata, string table) =>
        (metadata.GetEntity(table)?.Attributes ?? []).OfType<LookupAttributeMetadata>()
            .Where(a => a.LogicalName is not ("createdby" or "modifiedby" or "createdonbehalfby" or "modifiedonbehalfby" or "ownerid" or "owninguser"
                or "owningteam" or "owningbusinessunit"))
            .SelectMany(a => (a.Targets ?? []).Select(t => (a.LogicalName, t)));
}
