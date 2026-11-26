using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xrmd.Admin.Migration;
using Xrmd.Core.Errors;

namespace Xrmd.Mcp.Tools;

/// <summary>
/// The migration tools (migration spec §9, plan M5 task 4), System Administrator only: inspect an uploaded package, start an import,
/// follow it, read its report, cancel, finalize, and switch back on the business logic it left off. Each answers the JSON of its
/// Admin API route, through <see cref="MigrationRunService"/>; the passphrase is never part of an answer.
/// </summary>
[McpServerToolType]
public static class MigrationTools
{
    private const string PassphraseDescription = "The package passphrase (at least 12 characters). It is used to open the package and never stored or returned.";
    private const string RunDescription = "The migration run's id (a GUID).";

    [McpServerTool(Name = "migrate_inspect_package", ReadOnly = true, Idempotent = true)]
    [Description("Shows what an uploaded migration package holds (System Administrator only): source environment, solutions, tables with their record counts, "
                 + "relationships, export warnings, the table maps it carries and the number of entries. Upload the package first with `xrmd migrate import` or the Admin API.")]
    public static Task<string> InspectPackage(IServiceProvider services,
        [Description("The uploaded package's id (a GUID).")] string packageId,
        [Description(PassphraseDescription)] string passphrase,
        CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, async (request, _) =>
            await MigrationRunService.InspectPackageAsync(request, Id(packageId, "packageId"), passphrase, cancellationToken),
            cancellationToken, adminOnly: true);

    [McpServerTool(Name = "migrate_start_import", Destructive = true)]
    [Description("Starts the import of a migration package into this organization (System Administrator only), which must hold no business data. "
                 + "Give packageId (an uploaded package) or serverPath (a package under the server's import root). Returns {runId, asyncOperationId}: "
                 + "follow it with migrate_get_status. The run switches business logic off; it stays off until migrate_activate. "
                 + "maps (YAML texts) replace the table maps the package carries. dryRun true (default false) only reads: no lock, the organization may hold data, "
                 + "nothing is imported; its report (migrate_get_report) lists what the real run would remove, leave empty or not import. "
                 + "archive (only with dryRun) names archive candidates of the package to keep as xrmva_ archive tables: the answer's map is the map "
                 + "(the one given in maps, else the package's, else a new one) with them added, to save and pass in maps to the real import.")]
    public static Task<string> StartImport(IServiceProvider services,
        [Description(PassphraseDescription)] string passphrase,
        [Description("The uploaded package's id (a GUID); or give serverPath.")] string? packageId = null,
        [Description("The package's path on the server, under its import root; or give packageId.")] string? serverPath = null,
        [Description("The users mapping as a JSON object (the users.yaml of `xrmd migrate users`, as JSON).")] JsonElement? users = null,
        [Description("Import options as a JSON object, e.g. {\"strict\": \"true\", \"verifySample\": \"50\"}.")] JsonElement? options = null,
        [Description("Table mapping files, each as its YAML text (format version 1): they replace the maps stored in the package for this run.")] string[]? maps = null,
        [Description("false (default): import. true: a dry run that writes nothing but its report; it needs no finalize and cannot be resumed.")] bool dryRun = false,
        [Description("Only with dryRun: source tables of the package's archive candidates (e.g. [\"quote\"]) to add to the map as archive tables; the answer's map is the updated YAML text.")] string[]? archive = null,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, caller) =>
        {
            var body = new JsonObject { ["passphrase"] = passphrase, ["packageId"] = packageId, ["serverPath"] = serverPath };
            if (Node(users, "users") is { } u) body["users"] = u;
            if (maps is not null) body["maps"] = new JsonArray([.. maps.Select(m => (JsonNode?)m)]);
            if (archive is not null) body["archive"] = new JsonArray([.. archive.Select(t => (JsonNode?)t)]);
            if (Node(options, "options") is { } o) body["options"] = o is JsonObject ? o : throw XrmdFaultException.InvalidArgument("'options' must be a JSON object.");
            if (dryRun) ((JsonObject)(body["options"] ??= new JsonObject()))[MigrationRunRow.DryRunOption] = true;
            return Answer(await MigrationRunService.StartAsync(request, caller, body, cancellationToken));
        }, cancellationToken, adminOnly: true);

    [McpServerTool(Name = "migrate_get_status", ReadOnly = true, Idempotent = true)]
    [Description("The state of a migration run (System Administrator only): queued, running, completed, completed_with_errors, failed, cancelled "
                 + "or finalized, with its current phase and the list of phases.")]
    public static Task<string> GetStatus(IServiceProvider services,
        [Description(RunDescription)] string runId, CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, async (request, _) =>
            await MigrationRunService.StatusAsync(request, Id(runId, "runId"), cancellationToken), cancellationToken, adminOnly: true);

    [McpServerTool(Name = "migrate_get_report", ReadOnly = true, Idempotent = true)]
    [Description("The report of a migration run (System Administrator only): tables with counts, issues, nulled lookups, users by action, phase timings, "
                 + "and the business logic the import left off.")]
    public static Task<string> GetReport(IServiceProvider services,
        [Description(RunDescription)] string runId, CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, async (request, _) =>
            await MigrationRunService.ReportAsync(request, Id(runId, "runId"), cancellationToken), cancellationToken, adminOnly: true);

    [McpServerTool(Name = "migrate_cancel", Destructive = true)]
    [Description("Cancels a queued or running migration run (System Administrator only). A running run stops at its next check; resume it later with "
                 + "`xrmd migrate import --resume`, or finalize it.")]
    public static Task<string> Cancel(IServiceProvider services,
        [Description(RunDescription)] string runId, CancellationToken cancellationToken) =>
        McpCall.ToolAsync(services, async (request, _) =>
            Answer(await MigrationRunService.CancelAsync(request, Id(runId, "runId"), cancellationToken)), cancellationToken, adminOnly: true);

    [McpServerTool(Name = "migrate_finalize", Destructive = true)]
    [Description("Finalizes a failed, cancelled or completed_with_errors migration run (System Administrator only): the organization leaves migration mode "
                 + "and the uploaded package is deleted unless keepPackage. A completed run finalizes itself.")]
    public static Task<string> Finalize(IServiceProvider services,
        [Description(RunDescription)] string runId,
        [Description("Keep the uploaded package instead of deleting it.")] bool keepPackage = false,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, _) =>
            await MigrationRunService.FinalizeAsync(request, Id(runId, "runId"), new JsonObject { ["keepPackage"] = keepPackage }, cancellationToken),
            cancellationToken, adminOnly: true);

    [McpServerTool(Name = "migrate_activate", Destructive = true)]
    [Description("Switches back on the plug-in steps, webhooks and processes a finalized migration run left off, only those that were on in the source "
                 + "(System Administrator only). Give all=true or ids. dryRun defaults to true: it lists what would change; call again with dryRun=false to change it.")]
    public static Task<string> Activate(IServiceProvider services,
        [Description(RunDescription)] string runId,
        [Description("Switch on every item the run left off.")] bool all = false,
        [Description("The ids of the items to switch on (GUIDs); or give all=true.")] string[]? ids = null,
        [Description("Only list what would change (default true).")] bool dryRun = true,
        CancellationToken cancellationToken = default) =>
        McpCall.ToolAsync(services, async (request, caller) =>
        {
            var body = new JsonObject { ["all"] = all, ["dryRun"] = dryRun };
            if (ids is not null) body["ids"] = new JsonArray([.. ids.Select(i => (JsonNode?)i)]);
            return await MigrationRunService.ActivateAsync(request, caller, Id(runId, "runId"), body, cancellationToken);
        }, cancellationToken, adminOnly: true);

    private static Guid Id(string? text, string name) =>
        Guid.TryParse(text, out var id) ? id : throw XrmdFaultException.InvalidArgument($"'{name}' must be a GUID, got '{text}'.");

    private static JsonNode? Node(JsonElement? value, string name) => value switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.Object } v => JsonNode.Parse(v.GetRawText()),
        _ => throw XrmdFaultException.InvalidArgument($"'{name}' must be a JSON object."),
    };

    /// <summary>The body of a 2xx answer; an error answer (the start's 409, whose message names the busy tables) as the tool's error.</summary>
    private static JsonNode Answer(MigrationResult result)
    {
        if (result.Status < 400) return result.Body;
        var error = result.Body["error"];
        throw new McpException($"{error?["code"]}: {error?["message"]}");
    }
}
