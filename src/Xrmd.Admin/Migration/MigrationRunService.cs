using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Files;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Users;
using Xrmd.Pipeline.Async;
using static Xrmd.Admin.Migration.MigrationPackageEndpoints;

namespace Xrmd.Admin.Migration;

/// <summary>An answer of <see cref="MigrationRunService"/>: the JSON the Admin API returns, with its HTTP status.</summary>
public sealed record MigrationResult(JsonObject Body, int Status);

/// <summary>
/// The operations on migration runs (migration spec §7.1, §7.7–§7.9), shared by the Admin API routes and the MCP tools (plan M5 task 4):
/// start, status, list, report, cancel, resume, finalize and activate of a run, and the inspection of an uploaded package. A request
/// error is an <see cref="Core.Errors.XrmdFaultException"/> with its HTTP status (400, 404 or 409), which both callers map.
/// </summary>
public static class MigrationRunService
{
    /// <summary>The minimum length of a package passphrase (migration M1).</summary>
    public const int MinPassphraseLength = 12;

    /// <summary>
    /// Starts an import: <c>{packageId | serverPath, passphrase, users?, options?}</c>. 202 with the run and its job; 409 with the busy
    /// tables when the organization already holds business data.
    /// </summary>
    public static async Task<MigrationResult> StartAsync(IServiceProvider services, Caller caller, JsonObject body, CancellationToken ct)
    {
        var packageId = body["packageId"] is JsonValue p && p.TryGetValue<string>(out var pText)
            ? Guid.TryParse(pText, out var pid) ? pid : throw BadRequest("'packageId' is not a GUID.") : (Guid?)null;
        var serverPath = body["serverPath"] is JsonValue s && s.TryGetValue<string>(out var sText) && sText.Length > 0 ? sText : null;
        if ((packageId is null) == (serverPath is null)) throw BadRequest("Give exactly one of 'packageId' and 'serverPath'.");
        var passphrase = Passphrase(body);
        var options = body["options"] is JsonObject o ? o.DeepClone().AsObject() : [];
        // Plan M4a: the users mapping is checked now (400 before anything is queued) and kept in the run's options; it holds no secret.
        UserMapping users;
        try { users = UserMapping.Parse(body["users"]); }
        catch (UserMappingException ex) { throw BadRequest(ex.Message); }
        if (body["users"] is not null) options["users"] = users.ToJson().ToJsonString();

        var store = services.GetRequiredService<MigrationRunStore>();
        if (packageId is { } id)
        {
            var package = await PackageAsync(services, id, ct);
            if (package.State != MigrationPackageStates.Complete) throw Conflict($"The migration package {id} is not complete.");
        }
        else
        {
            try { serverPath = MigrationPackages.CheckServerPath(services.GetRequiredService<IConfiguration>(), serverPath!); }
            catch (MigrationRequestException ex) { throw BadRequest(ex.Message); }
            if (!File.Exists(serverPath) && !Directory.Exists(serverPath)) throw BadRequest($"No package at the server path '{serverPath}'.");
        }

        // Open the package now: a wrong passphrase or a file that is no package is a 400 before anything is queued.
        var database = await DatabaseAsync(services, ct);
        var tables = await ManifestTablesAsync(services, database, packageId, serverPath, passphrase, ct);
        await DemandExistingTargetsAsync(services, users, ct);
        if (await store.GetOpenRunAsync(ct) is { } open) throw Conflict($"Another migration run is open: {open.RunId}.");

        // The run and the lock first (the unique open-run index settles two starts), then the checks: no write can land between
        // the "no business data" check and the lock. A refused start removes the run and lifts the lock.
        var runId = Guid.NewGuid();
        try
        {
            await store.CreateRunAsync(new MigrationRunRow(runId, packageId, serverPath, MigrationRunStates.Queued, 0, options.ToJsonString(), null, null,
                caller.UserId, DateTime.UtcNow, null, null, null), ct);
        }
        catch (MigrationRunConflictException ex)
        {
            throw Conflict(ex.Message);
        }
        var migrationLock = services.GetRequiredService<MigrationLock>();
        migrationLock.Set(runId);
        try
        {
            // A job the worker took before the lock keeps running (the lock stops only new claims): it could write business data.
            if (await store.JobsInProgressAsync(ct) is { Count: > 0 } running)
                throw Conflict($"System jobs are in progress ({string.Join(", ", running)}): start the migration when they have finished.");
            List<string> busy;
            await using (var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct))
                busy = await MigrationPreconditions.TablesWithDataAsync(new DataSession(conn, null), services.GetRequiredService<IMetadataStore>(), tables, ct);
            if (busy.Count > 0)
            {
                await AbandonAsync(store, migrationLock, runId);
                return new(new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["code"] = "0x80040203",
                        ["message"] = $"The organization already holds business data, in: {string.Join(", ", busy)}. A migration imports into an organization without it.",
                    },
                    ["tables"] = new JsonArray([.. busy.Select(t => (JsonNode)t)]),
                }, StatusCodes.Status409Conflict);
            }
        }
        catch
        {
            await AbandonAsync(store, migrationLock, runId);
            throw;
        }
        var job = await EnqueueAsync(services, caller, runId, passphrase, ct);
        return new(new JsonObject { ["runId"] = runId.ToString(), ["asyncOperationId"] = job.ToString() }, StatusCodes.Status202Accepted);
    }

    /// <summary>400 when a user of the mapping is mapped onto a user the organization does not have.</summary>
    private static async Task DemandExistingTargetsAsync(IServiceProvider services, UserMapping users, CancellationToken ct)
    {
        var targets = users.Users.Values.Where(u => u.Action == UserActions.Existing).Select(u => ("systemuser", u.Target!.Value)).ToList();
        if (targets.Count == 0) return;
        await using var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
        var found = await Import.ImportRecordBuilder.ExistingAsync(new DataSession(conn, null), services.GetRequiredService<IMetadataStore>(), targets, ct);
        if (targets.Select(t => t.Item2).Except(found.Select(f => f.Id)).FirstOrDefault() is var missing && missing != Guid.Empty)
            throw BadRequest($"The users mapping maps a user onto the user {missing}, which does not exist in the organization.");
    }

    /// <summary>Removes a run whose start was refused, and lifts its lock.</summary>
    private static async Task AbandonAsync(MigrationRunStore store, MigrationLock migrationLock, Guid runId)
    {
        await store.DeleteRunAsync(runId, CancellationToken.None);
        migrationLock.Clear();
    }

    /// <summary>The passphrase of a start or resume body: a string of at least <see cref="MinPassphraseLength"/> characters.</summary>
    internal static string Passphrase(JsonObject body) =>
        body["passphrase"] is JsonValue v && v.TryGetValue<string>(out var passphrase) && passphrase.Length >= MinPassphraseLength
            ? passphrase
            : throw BadRequest($"'passphrase' is required: the package passphrase (at least {MinPassphraseLength} characters).");

    /// <summary>The tables of the package's manifest; 400 when the passphrase is wrong or the file is no package.</summary>
    internal static async Task<List<string>> ManifestTablesAsync(IServiceProvider services, string database, Guid? packageId, string? serverPath,
        string passphrase, CancellationToken ct) =>
        [.. (await ManifestAsync(services, database, packageId, serverPath, passphrase, ct)).Tables.Select(t => t.LogicalName)];

    /// <summary>The package's manifest; 400 when the passphrase is wrong or the file is no package.</summary>
    private static async Task<PackageManifest> ManifestAsync(IServiceProvider services, string database, Guid? packageId, string? serverPath,
        string passphrase, CancellationToken ct)
    {
        try
        {
            using var package = await MigrationPackages.OpenAsync(services.GetRequiredService<IFileStore>(), database, packageId, serverPath, passphrase, ct);
            return package.Manifest;
        }
        catch (PackagePassphraseException)
        {
            throw BadRequest("The passphrase is wrong for this package.");
        }
        catch (PackageFormatException ex)
        {
            throw BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// What an uploaded, complete package holds, read from its manifest with the passphrase: source, solutions, tables with their record
    /// counts, relationships, warnings and the number of entries. The entries are not decrypted one by one (the import's first phase does).
    /// </summary>
    public static async Task<JsonObject> InspectPackageAsync(IServiceProvider services, Guid packageId, string? passphrase, CancellationToken ct)
    {
        var text = Passphrase(new JsonObject { ["passphrase"] = passphrase });
        var package = await PackageAsync(services, packageId, ct);
        if (package.State != MigrationPackageStates.Complete) throw Conflict($"The migration package {packageId} is not complete.");
        var m = await ManifestAsync(services, await DatabaseAsync(services, ct), packageId, null, text, ct);
        return new JsonObject
        {
            ["packageId"] = packageId.ToString(),
            ["formatVersion"] = m.FormatVersion,
            ["createdOn"] = m.CreatedOn,
            ["toolVersion"] = m.ToolVersion,
            ["source"] = new JsonObject { ["url"] = m.Source.Url, ["environmentId"] = m.Source.EnvironmentId, ["organizationId"] = m.Source.OrganizationId },
            ["solutions"] = new JsonArray([.. m.Solutions.OrderBy(s => s.Order).Select(s => (JsonNode)new JsonObject
            {
                ["order"] = s.Order, ["uniqueName"] = s.UniqueName, ["version"] = s.Version, ["managed"] = s.Managed,
            })]),
            ["tables"] = new JsonArray([.. m.Tables.OrderBy(t => t.LogicalName, StringComparer.Ordinal).Select(t => (JsonNode)new JsonObject
            {
                ["logicalName"] = t.LogicalName, ["records"] = t.Records,
            })]),
            ["relationships"] = new JsonArray([.. m.Relationships.OrderBy(r => r.SchemaName, StringComparer.Ordinal).Select(r => (JsonNode)new JsonObject
            {
                ["schemaName"] = r.SchemaName, ["pairs"] = r.Pairs,
            })]),
            ["warnings"] = new JsonArray([.. m.Warnings.Select(w => (JsonNode)new JsonObject { ["code"] = w.Code, ["message"] = w.Message })]),
            ["entries"] = m.Entries.Count,
            ["bytes"] = package.Bytes,
        };
    }

    /// <summary>Keeps the passphrase in memory, queues the run's job (operation type 99010), records it on the run and wakes the worker.</summary>
    internal static async Task<Guid> EnqueueAsync(IServiceProvider services, Caller caller, Guid runId, string passphrase, CancellationToken ct)
    {
        var queue = services.GetRequiredService<AsyncOperationQueue>();
        var passphrases = services.GetRequiredService<MigrationPassphrases>();
        // In memory before the job is queued: the worker may take it at once.
        passphrases.Put(runId, passphrase);
        Guid job;
        try
        {
            await using var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
            job = await queue.EnqueueSystemJobAsync(new DataSession(conn, null), caller, AsyncOperationQueue.MigrationImport, "Migration import",
                AsyncOperationQueue.MigrationImportMessage, null, runId, MigrationImportJob.Data(runId), ct);
        }
        catch
        {
            passphrases.Remove(runId);
            throw;
        }
        await services.GetRequiredService<MigrationRunStore>().SetJobAsync(runId, job, ct);
        queue.Signal();
        return job;
    }

    /// <summary>The run <paramref name="id"/>; 404 when there is none.</summary>
    private static async Task<MigrationRunRow> RunAsync(IServiceProvider services, Guid id, CancellationToken ct) =>
        await services.GetRequiredService<MigrationRunStore>().GetRunAsync(id, ct)
        ?? throw NotFound($"No migration run with id {id}.");

    /// <summary>The status of the run <paramref name="id"/>; 404 when there is none.</summary>
    public static async Task<JsonObject> StatusAsync(IServiceProvider services, Guid id, CancellationToken ct) =>
        Status(services, await RunAsync(services, id, ct));

    /// <summary>The last 20 runs, newest first.</summary>
    public static async Task<JsonObject> ListAsync(IServiceProvider services, CancellationToken ct)
    {
        var runs = await services.GetRequiredService<MigrationRunStore>().ListRunsAsync(20, ct);
        return new JsonObject { ["value"] = new JsonArray([.. runs.Select(r => (JsonNode)Status(services, r))]) };
    }

    /// <summary>The report of the run (migration spec §7.8): tables, issues, nulled lookups, users by action, phase timings.</summary>
    public static async Task<JsonObject> ReportAsync(IServiceProvider services, Guid id, CancellationToken ct) =>
        await MigrationReport.BuildAsync(services.GetRequiredService<IDbConnectionFactory>(), await RunAsync(services, id, ct), ct);

    /// <summary>
    /// Cancels a run: a running job is told to stop (202; the run ends <c>cancelled</c> at the job's next check); a queued one is taken
    /// off the queue and the run is <c>cancelled</c> at once (200). Any other state is a 409.
    /// </summary>
    public static async Task<MigrationResult> CancelAsync(IServiceProvider services, Guid id, CancellationToken ct)
    {
        var run = await RunAsync(services, id, ct);
        var store = services.GetRequiredService<MigrationRunStore>();
        var cancellations = services.GetRequiredService<MigrationCancellations>();
        const string Cancelled = "Cancelled by an administrator.";
        // Queued: cancelled here (compare and set), then taken off the queue. A job the worker took already finds the run no longer
        // queued and does not run.
        if (run.State == MigrationRunStates.Queued && await store.SetStateAsync(id, MigrationRunStates.Cancelled, Cancelled, ct, MigrationRunStates.Queued))
        {
            if (run.AsyncOperationId is { } job) await store.CancelQueuedJobAsync(job, ct);
            services.GetRequiredService<MigrationPassphrases>().Remove(id);
            return new(await StatusAsync(services, id, ct), StatusCodes.Status200OK);
        }
        // Running (or it started meanwhile): the job is told to stop and records the cancel itself.
        if (cancellations.Cancel(id)) return new(await StatusAsync(services, id, ct), StatusCodes.Status202Accepted);
        // Running with no job in this process (the host restarted under it; start-up recovery normally fails such a run).
        if (await store.SetStateAsync(id, MigrationRunStates.Cancelled, Cancelled, ct, MigrationRunStates.Running))
            return new(await StatusAsync(services, id, ct), StatusCodes.Status200OK);
        var now = await RunAsync(services, id, ct);
        throw Conflict($"The migration run {id} is {now.State}: only a queued or running run can be cancelled.");
    }

    /// <summary>
    /// Resumes a failed, cancelled or partly failed run from its first phase not completed, with the package passphrase of
    /// <paramref name="body"/> (never stored): 202 with the new job.
    /// </summary>
    public static async Task<MigrationResult> ResumeAsync(IServiceProvider services, Caller caller, Guid id, JsonObject body, CancellationToken ct)
    {
        var run = await RunAsync(services, id, ct);
        if (run.State is not (MigrationRunStates.Failed or MigrationRunStates.Cancelled or MigrationRunStates.CompletedWithErrors))
            throw Conflict($"The migration run {id} is {run.State}: only a failed, cancelled or completed_with_errors run can be resumed.");
        var passphrase = Passphrase(body);
        await ManifestTablesAsync(services, await DatabaseAsync(services, ct), run.PackageId, run.ServerPath, passphrase, ct);

        if (!await services.GetRequiredService<MigrationRunStore>().SetStateAsync(id, MigrationRunStates.Queued, null, ct,
                MigrationRunStates.Failed, MigrationRunStates.Cancelled, MigrationRunStates.CompletedWithErrors))
            throw Conflict($"The migration run {id} changed meanwhile: read its state and try again.");
        services.GetRequiredService<MigrationLock>().Set(id);
        var job = await EnqueueAsync(services, caller, id, passphrase, ct);
        return new(new JsonObject { ["runId"] = id.ToString(), ["asyncOperationId"] = job.ToString() }, StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Finalizes a run that will not finalize on its own (failed, cancelled, completed_with_errors, or completed when the host stopped
    /// before the job finalized it): the lock lifts, and the uploaded package is deleted unless <c>keepPackage</c>. A queued or running
    /// run must be cancelled first (409).
    /// </summary>
    public static async Task<JsonObject> FinalizeAsync(IServiceProvider services, Guid id, JsonObject body, CancellationToken ct)
    {
        var run = await RunAsync(services, id, ct);
        if (run.State is MigrationRunStates.Queued or MigrationRunStates.Running)
            throw Conflict($"The migration run {id} is {run.State}: cancel it before finalizing it.");
        if (run.State == MigrationRunStates.Finalized) throw Conflict($"The migration run {id} is already finalized.");
        // Not `completed`: the job finalizes a completed run itself (with the run's own keepPackage).
        var keepPackage = Flag(body, "keepPackage");
        // Spec §7.9: a solution imported but not yet switched off (the run stopped in between) is switched off before the lock lifts.
        await Import.BusinessLogicSwitch.SwitchOffPendingAsync(services, id, ct);
        if (!await MigrationImportJob.FinalizeAsync(services.GetRequiredService<MigrationRunStore>(), services.GetRequiredService<MigrationLock>(),
                services.GetRequiredService<IFileStore>(), id, await DatabaseAsync(services, ct), run.PackageId, keepPackage,
                MigrationRunStates.Failed, MigrationRunStates.Cancelled, MigrationRunStates.CompletedWithErrors, StrandedCompleted(run)))
            throw Conflict($"The migration run {id} changed meanwhile: read its state and try again.");
        services.GetRequiredService<MigrationPassphrases>().Remove(id);
        return await StatusAsync(services, id, ct);
    }

    /// <summary>
    /// Switches back on the business logic the import of a finalized run left off (migration spec §7.9): <c>{"all": true}</c> or
    /// <c>{"ids": [...]}</c>, with <c>dryRun</c> to only list what would change. 409 until the run is finalized.
    /// </summary>
    public static async Task<JsonObject> ActivateAsync(IServiceProvider services, Caller caller, Guid id, JsonObject body, CancellationToken ct)
    {
        var run = await RunAsync(services, id, ct);
        if (run.State != MigrationRunStates.Finalized)
            throw Conflict($"The migration run {id} is {run.State}: finalize the run first (xrmd migrate finalize {id}).");
        var all = Flag(body, "all");
        List<Guid>? ids = null;
        if (body["ids"] is JsonArray array)
            ids = [.. array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var t) && Guid.TryParse(t, out var g) ? g : throw BadRequest("'ids' must be a list of GUIDs."))];
        else if (body["ids"] is not null) throw BadRequest("'ids' must be a list of GUIDs.");
        if (all == (ids is { Count: > 0 })) throw BadRequest("Give either \"all\": true or a non-empty \"ids\" list.");
        List<Import.ActivationResult> results;
        try { results = await Import.BusinessLogicActivation.ActivateAsync(services, caller, id, all ? null : ids, Flag(body, "dryRun"), ct); }
        catch (MigrationRequestException ex) { throw BadRequest(ex.Message); }
        return new JsonObject
        {
            ["runId"] = id.ToString(),
            ["items"] = new JsonArray([.. results.Select(r => (JsonNode)new JsonObject
            {
                ["table"] = r.Table, ["id"] = r.Id.ToString(), ["name"] = r.Name, ["action"] = r.Action, ["message"] = r.Message,
            })]),
        };
    }

    private static bool Flag(JsonObject body, string name) => body[name] switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        _ => throw BadRequest($"'{name}' must be true or false."),
    };

    /// <summary>
    /// <c>completed</c> when the run completed but its job is gone (the host stopped before the job finalized it), so that finalize
    /// can close it; otherwise a state no run has, so that finalize leaves a completed run to its job.
    /// </summary>
    private static string StrandedCompleted(MigrationRunRow run) =>
        run.State == MigrationRunStates.Completed && run.EndedOn is { } ended && DateTime.UtcNow - ended > TimeSpan.FromMinutes(1)
            ? MigrationRunStates.Completed : "-";

    /// <summary>The status of a run as the API shows it.</summary>
    internal static JsonObject Status(IServiceProvider services, MigrationRunRow run) => new()
    {
        ["runId"] = run.RunId.ToString(),
        ["packageId"] = run.PackageId?.ToString(),
        ["serverPath"] = run.ServerPath,
        ["state"] = run.State,
        ["phase"] = run.Phase,
        ["phases"] = new JsonArray([.. services.GetServices<IMigrationPhase>().OrderBy(p => p.Order).Select(p => (JsonNode)p.Name)]),
        ["message"] = run.Message,
        ["createdOn"] = run.CreatedOn,
        ["startedOn"] = run.StartedOn,
        ["endedOn"] = run.EndedOn,
        ["finalizedOn"] = run.FinalizedOn,
        ["asyncOperationId"] = run.AsyncOperationId?.ToString(),
    };
}
