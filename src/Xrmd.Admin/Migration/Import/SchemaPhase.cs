using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration.Mapping;
using Xrmd.Migration.Mapping;
using Xrmd.Solution;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Phase 2 of an import (spec §7.6 step 2, plan M4b task 1): the package's solutions imported one at a time in manifest order, each
/// in its own transaction through the solution importer (unmanaged customizations overwritten, processes not published). After each
/// one, the plug-in steps, webhook steps and processes it brought in active are switched off and recorded
/// (<see cref="BusinessLogicSwitch"/>), and the solution is marked in the ledger (<c>schema/solution/&lt;uniquename&gt;</c>) in the
/// same transaction. A crash between the import and the mark imports the solution again, as an overwrite. A solution that fails to
/// import fails the run: the data depends on the schema. Before the solutions, the archive tables the maps ask for
/// (<see cref="ArchiveSchema"/>, table mapping spec §8.7); after the solutions and the delta, their lookups to tables those brought.
/// </summary>
public sealed class SchemaPhase : IMigrationPhase
{
    public const int PhaseOrder = 2;

    public int Order => PhaseOrder;

    public string Name => "schema";

    public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var scope = context.Services.CreateScope();
        var services = scope.ServiceProvider;
        var factory = services.GetRequiredService<IDbConnectionFactory>();
        var strict = context.Options.TryGetValue("strict", out var value) && value == "true";
        var delta = new SchemaDeltaApplier(context, services, strict);
        var maps = await MigrationMaps.LoadAsync(context.Package, context.Options, ct);
        // Spec §6.2.5: with strict, what xrmverse cannot import stops the run before anything is imported.
        await delta.ReportUnsupportedAsync(maps, ct);
        // Spec §8.7: the archive tables first, so that the solutions' lookups to an archived table find it (R5); their lookups to tables
        // the solutions or the delta bring come after those (the same planned set as the dry run's and the translator's).
        var metadata = services.GetRequiredService<IMetadataStore>();
        var archiveCatalog = maps.Tables.Any(t => t.Archive) ? await PackageMaps.ReadCatalogAsync(context.Package, ct) : null;
        IReadOnlySet<string> planned = archiveCatalog is null ? new HashSet<string>()
            : await DryRunPreview.PlannedTablesAsync(context.Package, maps, metadata, null, ct);
        if (archiveCatalog is not null) await ArchiveSchema.CreateAsync(context, maps, archiveCatalog, planned, ct);
        var isMissingTable = MissingTable(metadata, maps);
        foreach (var solution in context.Package.Manifest.Solutions.OrderBy(x => x.Order))
        {
            var key = $"schema/solution/{solution.UniqueName}";
            if (await context.Store.IsChunkDoneAsync(context.RunId, key, ct)) continue;
            context.Progress.Report($"schema: solution {solution.UniqueName} {solution.Version}");
            var file = await ReadSolutionAsync(context.Package, solution, ct);
            // R11, R11b (migration table mapping spec §8.3): the components of mapped and missing tables out of the solution.
            var rewrite = SolutionMapRewriter.Rewrite(file, maps, isMissingTable);
            if (IsRefused(solution, rewrite)) throw new MigrationPhaseException(RefusedMessage(solution, rewrite));
            // Written before the import: a run cancelled between the import's commit and the switch-off below is switched off at finalize.
            if (!await context.Store.IsChunkDoneAsync(context.RunId, BusinessLogicSwitch.PendingKey(solution.UniqueName), ct))
                await MarkAsync(factory, context, BusinessLogicSwitch.PendingKey(solution.UniqueName), ct);
            ImportResult result;
            try
            {
                result = await services.GetRequiredService<SolutionImporter>().ImportInOwnTransactionAsync(context.Caller, rewrite.Zip,
                    new ImportOptions(null, OverwriteUnmanagedCustomizations: true, PublishWorkflows: false), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new MigrationPhaseException($"The solution '{solution.UniqueName}' {solution.Version} could not be imported: {ex.Message}");
            }

            // The import has committed: its logic is switched off even when the run is being cancelled.
            var none = CancellationToken.None;
            await using var conn = await factory.OpenAsync(none);
            await using var tx = conn.BeginTransaction();
            var s = new DataSession(conn, tx);
            var off = await BusinessLogicSwitch.SwitchOffAsync(s, services, context.Caller, result.SolutionId, context.Ledger, none);
            foreach (var warning in result.Warnings)
                await context.Ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Warning, "solution-warning", "solution", result.SolutionId, null,
                    $"{solution.UniqueName}: {warning}"), s, none);
            if (off > 0)
                await context.Ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Info, "logic-deactivated", "solution", result.SolutionId, null,
                    $"{solution.UniqueName}: {off} plug-in step(s), webhook step(s) or process(es) left off until 'xrmd migrate activate'."), s, none);
            foreach (var removal in rewrite.Removals)
                await context.Ledger.IssueAsync(RemovalIssue(solution.UniqueName, removal), s, none);
            await context.Store.MarkChunkAsync(context.RunId, PhaseOrder, key, 1, 0, s, none);
            await tx.CommitAsync(none);
            s.Committed();
        }

        // Plan M4b task 2: the custom data schema no solution carries, then reload the published metadata once.
        await delta.ApplyAsync(maps, ct);
        if (archiveCatalog is not null) await ArchiveSchema.CreateLookupsAsync(context, maps, archiveCatalog, planned, ct);
    }

    /// <summary>
    /// R11b: a table that is not custom, not in the organization (after the solutions imported so far: the metadata is read at each
    /// call) and not mapped.
    /// </summary>
    internal static Func<string, bool> MissingTable(IMetadataStore metadata, MapSet maps) => table =>
        !SolutionMapRewriter.IsCustomTableName(table) && metadata.GetTable(table) is null && maps.BySource(table) is null;

    /// <summary>The file of a solution of the package.</summary>
    internal static async Task<byte[]> ReadSolutionAsync(Xrmd.Migration.Packaging.PackageReader package, Xrmd.Migration.Packaging.PackageSolution solution,
        CancellationToken ct)
    {
        await using var entry = package.OpenEntry(solution.Path);
        using var buffer = new MemoryStream();
        await entry.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>True when the rewrite changed a managed solution: a managed solution is not changed, so the run fails (§8.3).</summary>
    internal static bool IsRefused(Xrmd.Migration.Packaging.PackageSolution solution, SolutionRewrite rewrite) =>
        rewrite.Changed && (rewrite.Managed || solution.Managed);

    /// <summary>Why a managed solution the rewrite would change fails the run, with every component concerned.</summary>
    internal static string RefusedMessage(Xrmd.Migration.Packaging.PackageSolution solution, SolutionRewrite rewrite) =>
        $"The managed solution '{solution.UniqueName}' {solution.Version} has components of tables that are mapped or that this organization does not have, or relationships to mapped tables, " +
        "and a managed solution is not changed: " +
        string.Join("; ", rewrite.Removed.Concat(rewrite.Retargeted)) + ".";

    /// <summary>The issue of a component R11 or R11b took out of a solution: under the target table when mapped, else the missing one.</summary>
    internal static MigrationIssue RemovalIssue(string solution, SolutionRemoval removal) => removal.Target is { } target
        ? new MigrationIssue(PhaseOrder, MigrationIssue.Info, "component-of-mapped-table", target, null, removal.Column,
            $"{solution}: {removal.Component} removed ({removal.Cause} is mapped to {target}).")
        : new MigrationIssue(PhaseOrder, MigrationIssue.Info, "component-of-missing-table", removal.Cause, null, removal.Column,
            $"{solution}: {removal.Component} removed ({removal.Cause} is not in this organization and no map names it).");

    private static async Task MarkAsync(IDbConnectionFactory factory, MigrationPhaseContext context, string key, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        await context.Store.MarkChunkAsync(context.RunId, PhaseOrder, key, 1, 0, new DataSession(conn, tx), ct);
        await tx.CommitAsync(ct);
    }
}
