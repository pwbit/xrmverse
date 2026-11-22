using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Files;
using Xrmd.Pipeline.Async;

namespace Xrmd.Admin.Migration;

/// <summary>
/// The system job of a migration import run (operation type 99010, plan M3 R1): takes the run's passphrase from memory, opens its
/// package and runs the phases not completed yet, in order, recording each one in the ledger. All done: the run completes and
/// finalizes itself (the lock lifts, the uploaded package is deleted unless kept). A failure fails the run and keeps the lock; a
/// cancel cancels it and keeps the lock; the host stopping fails it with the message to resume it.
/// </summary>
public sealed class MigrationImportJob(MigrationRunStore store, MigrationPassphrases passphrases, MigrationLock migrationLock,
    MigrationCancellations cancellations, IEnumerable<IMigrationPhase> phases, IFileStore files, IDbConnectionFactory factory,
    ILogger<MigrationImportJob> logger, IServiceProvider services) : IAsyncOperationHandler
{
    public int OperationType => AsyncOperationQueue.MigrationImport;

    /// <summary>The message of a run whose passphrase is gone (the host restarted) or whose job the host stopped.</summary>
    public static string ResumeMessage(Guid runId) =>
        $"The run was interrupted; resume it with the package passphrase (xrmd migrate import --resume {runId}).";

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
            await store.SetStateAsync(runId, MigrationRunStates.Failed, ResumeMessage(runId), CancellationToken.None, MigrationRunStates.Queued);
            throw new InvalidOperationException(ResumeMessage(runId));
        }
        if (!await store.SetStateAsync(runId, MigrationRunStates.Running, null, ct, MigrationRunStates.Queued))
            throw new AsyncOperationCanceledException($"The migration run {runId} is no longer queued: its job does not run.");

        try
        {
            string database;
            await using (var conn = await factory.OpenAsync(cancel.Token)) database = conn.Database;
            using var package = await MigrationPackages.OpenAsync(files, database, row.PackageId, row.ServerPath, passphrase, cancel.Token);
            var options = Options(row.Options);
            var progress = new Progress<string>(m => logger.LogInformation("Migration run {RunId}: {Message}", runId, m));
            var ledger = new Import.MigrationLedger(factory, runId);
            await ledger.TablesAsync(package.Manifest.Tables.Select(t => (t.LogicalName, t.Records)), cancel.Token);
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
            await store.SetStateAsync(runId, MigrationRunStates.Failed, ResumeMessage(runId), CancellationToken.None, MigrationRunStates.Running);
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
    /// Finalizes a run in one of the states <paramref name="from"/> (compare and set): the lock lifts and the uploaded package is
    /// deleted unless kept (migration spec §7.7, §8.1). False, with nothing done, when the run was in another state.
    /// </summary>
    internal static async Task<bool> FinalizeAsync(MigrationRunStore store, MigrationLock migrationLock, IFileStore files, Guid runId, string database,
        Guid? packageId, bool keepPackage, params string[] from)
    {
        if (!await store.SetStateAsync(runId, MigrationRunStates.Finalized, null, CancellationToken.None, from)) return false;
        migrationLock.Clear();
        if (packageId is { } id && !keepPackage)
        {
            await files.DeletePrefixAsync(MigrationPackageKeys.Folder(database, id), CancellationToken.None);
            await store.DeletePackageAsync(id, CancellationToken.None);
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
