using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration.Import;
using Xrmd.Core.Errors;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Records;
using Xrmd.Solution;

namespace Xrmd.Admin.Migration.Mapping;

/// <summary>
/// The read-only preview of a dry run (migration table mapping spec §8.6), after phase 1 (checksums, map check, census): what the
/// real run would do, written as issues of the run's ledger and nothing else (no solution, schema or row is written). Its issues
/// carry the schema phase's number, so that those the real run writes too are the same:
/// <list type="bullet">
/// <item>the unsupported items of the package (<c>unsupported</c>, as the schema phase reports them);</item>
/// <item>per solution, the components R11 and R11b would take out (<c>component-of-mapped-table</c>,
/// <c>component-of-missing-table</c>), and, as an error <c>dry-run-would-fail</c>, what would stop the real run (a managed solution
/// the rewrite would change; an unsupported item with the option <c>strict</c>);</item>
/// <item><c>dry-run-table-missing</c> per data table of the package (target name) that the organization lacks and that neither the
/// package's solutions (their <c>customizations.xml</c>, after the rewrite) nor its schema delta would create;</item>
/// <item><c>archive-candidate</c> per archive candidate of the package (spec §8.7) that no map names, table = source table, with its rows;
/// the archive tables the maps ask for count as created;</item>
/// <item><c>dry-run-lookup-empty</c> per (table, column) whose lookups or parties point to a table neither in the package nor in the
/// organization, or to a package table reported missing: the real run leaves them empty.</item>
/// </list>
/// The decisions are on table presence only, never on row presence. Parties a map leaves out (<c>party-not-allowed</c>) and columns
/// not mapped come from the census of phase 1.
/// </summary>
public static class DryRunPreview
{
    /// <summary>The phase of the preview's issues and timing: the schema phase's, whose issues it previews.</summary>
    public const int PhaseOrder = SchemaPhase.PhaseOrder;

    /// <summary>The name of the preview in the run's phases.</summary>
    public const string Name = "dry-run";

    /// <summary>The code of a source table the target lacks whose rows the package holds: it can be kept as an archive table (spec §8.7).</summary>
    public const string CandidateCode = "archive-candidate";

    /// <summary>The code of what would stop the real run.</summary>
    public const string WouldFailCode = "dry-run-would-fail";

    public static async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var scope = context.Services.CreateScope();
        var services = scope.ServiceProvider;
        var metadata = services.GetRequiredService<IMetadataStore>();
        var ledger = context.Ledger;
        var package = context.Package;
        var maps = await MigrationMaps.LoadAsync(package, context.Options, ct);

        // Reports only: ApplyAsync, which writes the schema, is never called.
        var strict = context.Options.TryGetValue("strict", out var value) && value == "true";
        try
        {
            await new SchemaDeltaApplier(context, services, strict).ReportUnsupportedAsync(maps, ct);
        }
        catch (MigrationPhaseException ex)
        {
            await WouldFailAsync(ledger, ex.Message, ct);
        }

        // The tables the run would create: those of the solutions as rewritten and the delta's (the set the schema phase and the
        // translator use for archive lookups too).
        var created = await PlannedTablesAsync(package, maps, metadata, async (solution, rewrite) =>
        {
            context.Progress.Report($"dry run: solution {solution.UniqueName} {solution.Version}");
            foreach (var removal in rewrite.Removals)
                await ledger.IssueAsync(SchemaPhase.RemovalIssue(solution.UniqueName, removal), ct);
            if (SchemaPhase.IsRefused(solution, rewrite))
                await WouldFailAsync(ledger, SchemaPhase.RefusedMessage(solution, rewrite), ct);
        }, ct);
        // The archive tables the maps ask for are created by the schema phase (spec §8.7).
        created.UnionWith(maps.Tables.Where(t => t.Archive).Select(t => t.Target));
        bool Present(string table) => created.Contains(table) || metadata.GetTable(table) is not null;

        var tables = RecordsPhase.DataTables(package.Manifest).OrderBy(t => t.LogicalName, StringComparer.Ordinal).ToList();
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables.Where(t => !Present(t.LogicalName)))
        {
            missing.Add(table.LogicalName);
            await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "dry-run-table-missing", table.LogicalName, null, null,
                $"{table.Records} row(s)"), ct);
        }

        // Spec §8.7: the archive candidates no map names yet, for the owner to choose from.
        foreach (var candidate in package.RawManifest.ArchiveCandidates.Where(c => maps.BySource(c.LogicalName) is null)
                     .OrderBy(c => c.LogicalName, StringComparer.Ordinal))
            await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Info, CandidateCode, candidate.LogicalName, null, null,
                $"{candidate.Records} row(s)"), ct);

        // Lookups that would be left empty: to a table neither in the package nor in the organization, or to a table of the package
        // that is missing (its rows are not imported). The rows of a missing table are not imported at all, so their lookups are not
        // counted.
        var inPackage = new HashSet<string>(package.Manifest.Tables.Select(t => t.LogicalName), StringComparer.Ordinal);
        bool Empty(string? table) => table is not null && (missing.Contains(table) || (!inPackage.Contains(table) && !Present(table)));
        var empty = new Dictionary<(string Table, string Column), long>();
        foreach (var table in tables.Where(t => !missing.Contains(t.LogicalName)))
        {
            context.Progress.Report($"dry run: reading the lookups of {table.LogicalName} ({table.Records})");
            await foreach (var record in ChunkReader.ReadAsync(package, table.Chunks, ct))
                foreach (var (column, v) in record.Values)
                {
                    var lost = v switch
                    {
                        LookupValue lookup => Empty(lookup.Table),
                        PartyListValue parties => parties.Parties.Any(p => Empty(p.Table)),
                        _ => false,
                    };
                    if (lost) empty[(table.LogicalName, column)] = empty.GetValueOrDefault((table.LogicalName, column)) + 1;
                }
        }
        foreach (var ((table, column), rows) in empty.OrderBy(e => e.Key.Table, StringComparer.Ordinal).ThenBy(e => e.Key.Column, StringComparer.Ordinal))
            await ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "dry-run-lookup-empty", table, null, column, $"{rows} row(s)"), ct);
    }

    /// <summary>
    /// The tables (target names) the package's solutions and schema delta create in this organization: those of each solution as rewritten
    /// (R11 and R11b took out the others; a solution the rewrite would change and that is managed creates nothing), then the delta's. A
    /// solution sees the tables of the solutions before it, as in the real run, where they are imported by then. One set for the dry run,
    /// the schema phase and the record translator (spec §8.7: a lookup of an archive table to one of them is kept).
    /// </summary>
    /// <param name="each">Called with each solution and its rewrite, in manifest order.</param>
    public static async Task<HashSet<string>> PlannedTablesAsync(Xrmd.Migration.Packaging.PackageReader package, MapSet maps, IMetadataStore metadata,
        Func<Xrmd.Migration.Packaging.PackageSolution, SolutionRewrite, Task>? each, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(metadata);
        var created = new HashSet<string>(StringComparer.Ordinal);
        var isMissing = SchemaPhase.MissingTable(metadata, maps);
        foreach (var solution in package.Manifest.Solutions.OrderBy(s => s.Order))
        {
            var rewrite = SolutionMapRewriter.Rewrite(await SchemaPhase.ReadSolutionAsync(package, solution, ct), maps,
                table => !created.Contains(table) && isMissing(table));
            if (each is not null) await each(solution, rewrite);
            if (!SchemaPhase.IsRefused(solution, rewrite)) created.UnionWith(Entities(rewrite.Zip));
        }
        created.UnionWith(await SchemaDeltaApplier.DeltaTablesAsync(package, maps, ct));
        return created;
    }

    /// <summary>What would stop the real run, as an error with no table (the message names the solution or the items).</summary>
    private static Task WouldFailAsync(MigrationLedger ledger, string message, CancellationToken ct) =>
        ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Error, WouldFailCode, null, null, null, message), ct);

    /// <summary>The tables (lower-case logical names) whose node a solution file's <c>customizations.xml</c> holds.</summary>
    private static IEnumerable<string> Entities(byte[] solutionZip)
    {
        XDocument customizations;
        try
        {
            (_, customizations, _) = SolutionZip.Read(solutionZip);
        }
        catch (XrmdFaultException)
        {
            return [];
        }
        return (customizations.Root?.Element("Entities")?.Elements("Entity") ?? [])
            .Select(e => (string?)e.Element("EntityInfo")?.Element("entity")?.Attribute("Name") ?? e.Element("Name")?.Value)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim().ToLowerInvariant())
            .ToList();
    }
}
