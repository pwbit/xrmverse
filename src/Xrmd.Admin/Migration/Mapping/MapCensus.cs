using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration.Import;
using Xrmd.Migration.Mapping;

namespace Xrmd.Admin.Migration.Mapping;

/// <summary>
/// The census of an import with maps (R10, migration table mapping spec §8.5), run in phase 1 after the map check: every record of
/// every mapped table, and of every table whose target is an activity (parties left out), goes through the record translator once,
/// and what it did is counted. The counts go to the report as one issue per (target table, column, code), with the row count in the
/// message, plus one <c>mapped-table</c> issue per mapped table. Written once per run: the issues and the ledger mark
/// <see cref="ChunkKey"/> commit together, and a run that has the mark skips the census.
/// </summary>
public static class MapCensus
{
    /// <summary>The ledger mark of a run whose census is written.</summary>
    public const string ChunkKey = "verify/map-census";

    /// <summary>The codes counted as information; every other code of the translator is a warning.</summary>
    private static readonly HashSet<string> InfoCodes = new(StringComparer.Ordinal) { "value-by-label", "status-by-label" };

    public static async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Package.Translator is not MapPackageTranslator { Records: var translator }) return;
        if (await context.Store.IsChunkDoneAsync(context.RunId, ChunkKey, ct)) return;
        var maps = await MigrationMaps.LoadAsync(context.Package, context.Options, ct);
        var metadata = context.Services.GetRequiredService<IMetadataStore>();

        var notes = new Counts();
        var mapped = new List<(string Source, string Target, long Rows)>();
        foreach (var table in RecordsPhase.DataTables(context.Package.RawManifest))
        {
            var entry = maps.BySource(table.LogicalName);
            var target = translator.TableOf(table.LogicalName);
            if (entry is null && metadata.GetTable(target)?.IsActivity != true) continue;
            context.Progress.Report($"verify: counting the translation of {table.LogicalName} ({table.Records})");
            long rows = 0;
            await foreach (var record in DerivedIndex.ReadAsync(context.Package, table.Chunks, ct))
            {
                translator.Translate(table.LogicalName, record, notes);
                rows++;
            }
            if (entry is not null) mapped.Add((table.LogicalName, target, rows));
        }

        var factory = context.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        var session = new DataSession(conn, tx);
        foreach (var (source, target, rows) in mapped)
            await context.Ledger.IssueAsync(new MigrationIssue(VerifyPackagePhase.PhaseOrder, MigrationIssue.Info, "mapped-table", target, null, null,
                $"{source} → {target}: {rows} rows"), session, ct);
        foreach (var ((table, column, code), count) in notes.All
                     .OrderBy(n => n.Key.Table, StringComparer.Ordinal).ThenBy(n => n.Key.Column, StringComparer.Ordinal).ThenBy(n => n.Key.Code, StringComparer.Ordinal))
            await context.Ledger.IssueAsync(new MigrationIssue(VerifyPackagePhase.PhaseOrder, InfoCodes.Contains(code) ? MigrationIssue.Info : MigrationIssue.Warning,
                code, table, null, column, $"{count} row(s)"), session, ct);
        await context.Store.MarkChunkAsync(context.RunId, VerifyPackagePhase.PhaseOrder, ChunkKey, 0, 0, session, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>The rows noted per (target table, column, code).</summary>
    private sealed class Counts : ITranslationNotes
    {
        private readonly Dictionary<(string Table, string? Column, string Code), long> _counts = [];

        public IReadOnlyDictionary<(string Table, string? Column, string Code), long> All => _counts;

        public void Note(string targetTable, string? column, string code, Guid rowId) =>
            _counts[(targetTable, column, code)] = _counts.GetValueOrDefault((targetTable, column, code)) + 1;
    }
}
