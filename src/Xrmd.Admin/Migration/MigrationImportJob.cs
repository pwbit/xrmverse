using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration.Mapping;
using Xrmd.Files;
using Xrmd.Pipeline.Async;

namespace Xrmd.Admin.Migration;

/// <summary>
/// The system job of a migration import run (operation type 99010, plan M3 R1): takes the run's passphrase from memory, opens its
/// package and runs the phases not completed yet, in order, recording each one in the ledger. All done: the run completes and
/// finalizes itself (the lock lifts, the uploaded package is deleted unless kept). A failure fails the run and keeps the lock; a
/// cancel cancels it and keeps the lock; the host stopping fails it with the message to resume it. A dry run (table mapping spec
/// §8.6) runs phase 1 and <see cref="DryRunPreview"/> only, ends <c>completed</c> or <c>failed</c>, and never touches the lock or
/// its package.
/// </summary>
public sealed class MigrationImportJob(MigrationRunStore store, MigrationPassphrases passphrases, MigrationLock migrationLock,
    MigrationCancellations cancellations, IEnumerable<IMigrationPhase> phases, IFileStore files, IDbConnectionFactory factory,
    ILogger<MigrationImportJob> logger, IServiceProvider services) : IAsyncOperationHandler
{
    public int OperationType => AsyncOperationQueue.MigrationImport;

    /// <summary>The message of a run whose passphrase is gone (the host restarted) or whose job the host stopped.</summary>
    public static string ResumeMessage(Guid runId) =>
        $"The run was interrupted; resume it with the package passphrase (xrmd migrate import --resume {runId}).";

    /// <summary>The message of a dry run whose passphrase is gone or whose job the host stopped: it cannot be resumed (spec §8.6).</summary>
    public static string DryRunInterruptedMessage(Guid runId) =>
        $"The dry run {runId} was interrupted; start a new dry run (xrmd migrate import --dry-run).";

    /// <summary>The message of an interrupted run: <see cref="ResumeMessage"/>, or <see cref="DryRunInterruptedMessage"/> for a dry run.</summary>
    public static string InterruptedMessage(MigrationRunRow run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.IsDryRun ? DryRunInterruptedMessage(run.RunId) : ResumeMessage(run.RunId);
    }

    /// <summary>The message of a dry run that ended with errors in its report: what would stop the real run.</summary>
    public const string DryRunErrorsMessage = "The real run would stop: see the errors in the report.";

    /// <summary>The <c>data</c> of the job of <paramref name="runId"/>.</summary>
    public static string Data(Guid runId) => new JsonObject { ["runId"] = runId.ToString() }.ToJsonString();

    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var runId = run.Data is { } data && JsonNode.Parse(data)?["runId"]?.GetValue<string>() is { } text && Guid.TryParse(text, out var parsed)
            ? parsed : throw new InvalidOperationException("The migration job names no run.");
        // Registered first: a cancel that comes after this trips the token; one that came before left the run cancelled, and the
        // compare-and-set below refuses to start it. Every later state change is a compare-and-set from running too, so that a late
        // write of this job never overwrites what an administrator did meanwhile (cancel, finalize).
        using var registration = cancellations.Register(runId);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, registration.Token);
        var row = await store.GetRunAsync(runId, ct) ?? throw new InvalidOperationException($"The migration run {runId} does not exist.");
        if (!passphrases.TryTake(runId, out var passphrase))
        {
            if (row.State != MigrationRunStates.Queued) throw new AsyncOperationCanceledException($"The migration run {runId} is {row.State}: its job does not run.");
            await store.SetStateAsync(runId, MigrationRunStates.Failed, InterruptedMessage(row), CancellationToken.None, MigrationRunStates.Queued);
            throw new InvalidOperationException(InterruptedMessage(row));
        }
        if (!await store.SetStateAsync(runId, MigrationRunStates.Running, null, ct, MigrationRunStates.Queued))
            throw new AsyncOperationCanceledException($"The migration run {runId} is no longer queued: its job does not run.");

        try
        {
            string database;
            await using (var conn = await factory.OpenAsync(cancel.Token)) database = conn.Database;
            using var package = await MigrationPackages.OpenAsync(files, database, row.PackageId, row.ServerPath, passphrase, cancel.Token);
            var options = Options(row.Options);
            // The run's maps (its own, else the package's): every phase, the ledger and a resume see the target names.
            await MigrationMaps.AttachAsync(package, options, services, cancel.Token);
            var progress = new Progress<string>(m => logger.LogInformation("Migration run {RunId}: {Message}", runId, m));
            var ledger = new Import.MigrationLedger(factory, runId);
            await ledger.TablesAsync(package.Manifest.Tables.Select(t => (t.LogicalName, t.Records)), cancel.Token);
            if (row.IsDryRun)
            {
                await DryRunAsync(row, run, package, options, progress, ledger, cancel.Token);
                return;
            }
            foreach (var phase in phases.Where(p => p.Order > row.Phase).OrderBy(p => p.Order))
            {
                cancel.Token.ThrowIfCancellationRequested();
                logger.LogInformation("Migration run {RunId}: phase {Order} {Phase}.", runId, phase.Order, phase.Name);
                await ledger.PhaseStartedAsync(phase.Order, phase.Name, CancellationToken.None);
                // Plan M4a: every write of a phase keeps the exported system columns and runs no business logic.
                using (MigrationScope.Enter())
                    await phase.RunAsync(new MigrationPhaseContext(runId, package, run.Owner, store, options, progress, ledger, services), cancel.Token);
                await ledger.PhaseEndedAsync(phase.Order, CancellationToken.None);
                await store.SetPhaseAsync(runId, phase.Order, CancellationToken.None);
            }
            // Spec §7.7: a failed row keeps the lock on for the administrator to resume or accept (finalize) the result.
            if (await ledger.HasErrorsAsync(CancellationToken.None))
            {
                if (!await store.SetStateAsync(runId, MigrationRunStates.CompletedWithErrors, "Some rows could not be imported: see the report.",
                        CancellationToken.None, MigrationRunStates.Running))
                    throw new AsyncOperationCanceledException($"The migration run {runId} was changed while it ran.");
                run.CompletionMessage = $"The migration run {runId} completed with errors.";
                return;
            }
            if (!await store.SetStateAsync(runId, MigrationRunStates.Completed, null, CancellationToken.None, MigrationRunStates.Running))
                throw new AsyncOperationCanceledException($"The migration run {runId} was changed while it ran.");
            await FinalizeAsync(store, migrationLock, files, runId, database, row.PackageId,
                keepPackage: options.TryGetValue("keepPackage", out var keep) && keep == "true", MigrationRunStates.Completed);
            run.CompletionMessage = $"The migration run {runId} completed.";
        }
        catch (OperationCanceledException) when (registration.IsCancelled && !ct.IsCancellationRequested)
        {
            await store.SetStateAsync(runId, MigrationRunStates.Cancelled, "Cancelled by an administrator.", CancellationToken.None, MigrationRunStates.Running);
            throw new AsyncOperationCanceledException($"The migration run {runId} was cancelled.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await store.SetStateAsync(runId, MigrationRunStates.Failed, InterruptedMessage(row), CancellationToken.None, MigrationRunStates.Running);
            throw;
        }
        catch (AsyncOperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await store.SetStateAsync(runId, MigrationRunStates.Failed, Truncate(ex.Message), CancellationToken.None, MigrationRunStates.Running);
            throw;
        }
    }

    /// <summary>
    /// A dry run (migration table mapping spec §8.6): phase 1 (checksums, map check, census), then the read-only
    /// <see cref="DryRunPreview"/>, never another phase. It ends <c>completed</c> (with <see cref="DryRunErrorsMessage"/> when its
    /// report holds errors) or, through the caller, <c>failed</c>; it is never finalized here: the lock was never taken, and the
    /// package stays for the real run.
    /// </summary>
    private async Task DryRunAsync(MigrationRunRow row, AsyncOperationRun run, Xrmd.Migration.Packaging.PackageReader package,
        IReadOnlyDictionary<string, string> options, IProgress<string> progress, Import.MigrationLedger ledger, CancellationToken ct)
    {
        var context = new MigrationPhaseContext(row.RunId, package, run.Owner, store, options, progress, ledger, services);
        foreach (var phase in phases.Where(p => p.Order == VerifyPackagePhase.PhaseOrder))
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation("Migration dry run {RunId}: phase {Order} {Phase}.", row.RunId, phase.Order, phase.Name);
            await ledger.PhaseStartedAsync(phase.Order, phase.Name, CancellationToken.None);
            await phase.RunAsync(context, ct);
            await ledger.PhaseEndedAsync(phase.Order, CancellationToken.None);
            await store.SetPhaseAsync(row.RunId, phase.Order, CancellationToken.None);
        }
        ct.ThrowIfCancellationRequested();
        logger.LogInformation("Migration dry run {RunId}: preview.", row.RunId);
        await ledger.PhaseStartedAsync(DryRunPreview.PhaseOrder, DryRunPreview.Name, CancellationToken.None);
        await DryRunPreview.RunAsync(context, ct);
        await ledger.PhaseEndedAsync(DryRunPreview.PhaseOrder, CancellationToken.None);
        var message = await ledger.HasErrorsAsync(CancellationToken.None) ? DryRunErrorsMessage : null;
        if (!await store.SetStateAsync(row.RunId, MigrationRunStates.Completed, message, CancellationToken.None, MigrationRunStates.Running))
            throw new AsyncOperationCanceledException($"The migration run {row.RunId} was changed while it ran.");
        run.CompletionMessage = $"The migration dry run {row.RunId} completed.";
    }

    /// <summary>
    /// Finalizes a run in one of the states <paramref name="from"/> (compare and set): the lock lifts and the uploaded package is
    /// deleted unless kept (migration spec §7.7, §8.1). False, with nothing done, when the run was in another state. The state, the
    /// package's row and the lock change together: whoever reads the run <c>finalized</c> finds the lock off and the package gone.
    /// The package's files are deleted inside the transaction, once the run's compare and set has passed and before the commit
    /// (the deletion is idempotent): if it or the commit fails, the run and the package's row stay, so a later finalize or
    /// <c>DELETE /admin/migrate/packages/{id}</c> can retry, and no file is ever left without its row.
    /// </summary>
    internal static async Task<bool> FinalizeAsync(MigrationRunStore store, MigrationLock migrationLock, IFileStore files, Guid runId, string database,
        Guid? packageId, bool keepPackage, params string[] from)
    {
        var deleted = keepPackage ? null : packageId;
        var held = migrationLock.ActiveRunId;
        try
        {
            if (!await store.FinalizeRunAsync(runId, deleted, async () =>
                {
                    if (deleted is { } id) await files.DeletePrefixAsync(MigrationPackageKeys.Folder(database, id), CancellationToken.None);
                    migrationLock.Clear();
                }, CancellationToken.None, from)) return false;
        }
        catch when (held is { } h && migrationLock.ActiveRunId is null)
        {
            // The commit failed after the lock was lifted: the run is not finalized, so the lock is back on.
            migrationLock.Set(h);
            throw;
        }
        return true;
    }

    /// <summary>The run's options (string values) from the ledger's JSON.</summary>
    internal static Dictionary<string, string> Options(string json)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (JsonNode.Parse(json) is JsonObject o)
            foreach (var (name, value) in o)
                if (value is JsonValue v) options[name] = v.ToString();
        return options;
    }

    private static string Truncate(string message) => message.Length <= 2000 ? message : message[..1997] + "...";
}
