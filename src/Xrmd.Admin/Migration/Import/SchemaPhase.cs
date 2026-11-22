using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Solution;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Phase 2 of an import (spec §7.6 step 2, plan M4b task 1): the package's solutions imported one at a time in manifest order, each
/// in its own transaction through the solution importer (unmanaged customizations overwritten, processes not published). After each
/// one, the plug-in steps, webhook steps and processes it brought in active are switched off and recorded
/// (<see cref="BusinessLogicSwitch"/>), and the solution is marked in the ledger (<c>schema/solution/&lt;uniquename&gt;</c>) in the
/// same transaction. A crash between the import and the mark imports the solution again, as an overwrite. A solution that fails to
/// import fails the run: the data depends on the schema.
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
        // Spec §6.2.5: with strict, what xrmverse cannot import stops the run before anything is imported.
        await delta.ReportUnsupportedAsync(ct);
        foreach (var solution in context.Package.Manifest.Solutions.OrderBy(x => x.Order))
        {
            var key = $"schema/solution/{solution.UniqueName}";
            if (await context.Store.IsChunkDoneAsync(context.RunId, key, ct)) continue;
            context.Progress.Report($"schema: solution {solution.UniqueName} {solution.Version}");
            // Written before the import: a run cancelled between the import's commit and the switch-off below is switched off at finalize.
            if (!await context.Store.IsChunkDoneAsync(context.RunId, BusinessLogicSwitch.PendingKey(solution.UniqueName), ct))
                await MarkAsync(factory, context, BusinessLogicSwitch.PendingKey(solution.UniqueName), ct);
            ImportResult result;
            try
            {
                byte[] file;
                await using (var entry = context.Package.OpenEntry(solution.Path))
                using (var buffer = new MemoryStream())
                {
                    await entry.CopyToAsync(buffer, ct);
                    file = buffer.ToArray();
                }
                result = await services.GetRequiredService<SolutionImporter>().ImportInOwnTransactionAsync(context.Caller, file,
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
            await context.Store.MarkChunkAsync(context.RunId, PhaseOrder, key, 1, 0, s, none);
            await tx.CommitAsync(none);
            s.Committed();
        }

        // Plan M4b task 2: the custom data schema no solution carries, then reload the published metadata once.
        await delta.ApplyAsync(ct);
    }

    private static async Task MarkAsync(IDbConnectionFactory factory, MigrationPhaseContext context, string key, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        await context.Store.MarkChunkAsync(context.RunId, PhaseOrder, key, 1, 0, new DataSession(conn, tx), ct);
        await tx.CommitAsync(ct);
    }
}
