using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Migration.Records;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Phase 6 of an import (spec §7.6 step 6, plan M4a task 4): the lookups the earlier phases left out because their row was not
/// written yet. Each record gets one update with those columns and its exported <c>modifiedon</c>, in batches of 1000 records per
/// table (<c>deferred/&lt;table&gt;#&lt;n&gt;</c>). A lookup whose row never came is left empty and reported (an error with
/// <c>strictLookups</c>).
/// </summary>
public sealed class DeferredLookupsPhase : IMigrationPhase
{
    public const int PhaseOrder = 6;

    public int Order => PhaseOrder;

    public string Name => "deferred-lookups";

    public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.Services;
        var metadata = services.GetRequiredService<IMetadataStore>();
        var core = services.GetRequiredService<CoreOperations>();
        var ledger = context.Ledger;
        var strict = context.Options.TryGetValue("strictLookups", out var s0) && s0 == "true";
        var chunks = new ImportChunkWriter(services.GetRequiredService<IDbConnectionFactory>(), context.Store, ledger);

        // table → record → its deferred lookups, in the order they were recorded.
        var byTable = new SortedDictionary<string, Dictionary<Guid, List<DeferredLookup>>>(StringComparer.Ordinal);
        await foreach (var d in ledger.DeferredAsync(ct))
        {
            if (!byTable.TryGetValue(d.Table, out var records)) byTable[d.Table] = records = [];
            if (!records.TryGetValue(d.RecordId, out var list)) records[d.RecordId] = list = [];
            list.Add(d);
        }
        foreach (var (table, records) in byTable)
        {
            context.Progress.Report($"deferred-lookups: {table} ({records.Count})");
            // The chunk writer works on records: one per updated row, its values the deferred lookups.
            var rows = records.OrderBy(r => r.Value[0].RecordId).Select(r => new MigrationRecord(r.Key,
                r.Value.ToDictionary(d => d.Column, d => (MigrationValue)new LookupValue(d.RefTable, d.RefId), StringComparer.Ordinal))).ToList();
            var n = 0;
            foreach (var slice in ImportChunkWriter.Slices(rows))
                await chunks.WriteAsync(PhaseOrder, $"deferred/{table}#{n++}", table, slice, async (s, part, token) =>
                {
                    var existing = (await ImportRecordBuilder.ExistingAsync(s, metadata,
                        [.. part.SelectMany(p => p.Values.Values.Cast<LookupValue>().Select(l => (l.Table, l.Id)))], token)).ToHashSet();
                    foreach (var row in part)
                    {
                        var entity = new Entity(table, row.Id);
                        foreach (var (column, value) in row.Values)
                        {
                            var l = (LookupValue)value;
                            if (existing.Contains((l.Table, l.Id)))
                            {
                                entity[column] = new EntityReference(l.Table, l.Id);
                                continue;
                            }
                            if (strict)
                                throw new ImportValueException($"The column '{column}' references {l.Table} {l.Id}, which is not in the organization.", "0x80040217");
                            // The owner keeps the importing user (a record has one); another lookup is left empty.
                            await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "lookup-nulled", table, row.Id, column,
                                $"The column '{column}' referenced {l.Table} {l.Id}, which is not in the organization: it is left empty."), s, token);
                        }
                        if (entity.Attributes.Count == 0) continue;
                        if (records[row.Id][0].ModifiedOn is { } modified) entity["modifiedon"] = modified;
                        await ImportRecordWriter.KeepModifiedAsync(s, metadata, entity, token);
                        await core.UpdateAsync(s, context.Caller, entity, token);
                    }
                }, ct);
        }
    }
}
