using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd solution</c>: solutions through the Web API solution actions (<c>ImportSolution</c>, <c>ImportSolutionAsync</c>,
/// <c>StageAndUpgrade</c>, <c>StageAndUpgradeAsync</c>, <c>ExportSolution</c>, <c>AddSolutionComponent</c>, <c>PublishAllXml</c>) as the
/// signed-in user. <c>import --mode</c> picks the message: <c>update</c> is <c>ImportSolution</c>, <c>upgrade</c> is <c>StageAndUpgrade</c>
/// and <c>stage</c> is <c>ImportSolution</c> with <c>HoldingSolution</c> (plan 2.6, ruling M16). There is no <c>--managed</c> option on
/// <c>export</c>: xrmverse cannot produce managed files, so the parser refuses it (exit 2).
/// The cascade delete, <c>uninstall</c>, <c>promote</c> and <c>layers</c> are the Admin API's (<c>DELETE /admin/solutions/{uniquename}?cascade=true</c>,
/// <c>POST /admin/solutions/{uniquename}/uninstall</c> and <c>/promote</c>, <c>GET /admin/solutions/{uniquename}/layers</c>,
/// <c>GET /admin/layers/{componenttype}/{objectid}</c>).
/// </summary>
public static class SolutionCommands
{
    /// <summary>Seconds between two polls of an asynchronous import.</summary>
    private const int PollSeconds = 2;

    /// <summary>Consecutive failed reads of the system job (unreachable, 5xx, 404) tolerated while polling.</summary>
    internal const int MaxConsecutivePollFailures = 5;

    /// <summary>Default of <c>--timeout</c> (minutes) for <c>solution import --async</c>.</summary>
    private const int DefaultTimeoutMinutes = 60;

    // asyncoperation outcome (Dataverse): statecode 3 = Completed; statuscode 30 = Succeeded, 31 = Failed, 32 = Canceled.
    private const int Completed = 3;
    private const int Succeeded = 30;
    private const int Failed = 31;
    private const int Canceled = 32;

    /// <summary>The component type names <c>add-component --type</c> accepts, with their <c>componenttype</c> values.</summary>
    public static readonly (string Name, int Value)[] ComponentTypes =
    [
        ("entity", 1), ("attribute", 2), ("relationship", 3), ("optionset", 9), ("entityrelationship", 10), ("role", 20), ("roleprivilege", 21),
        ("savedquery", 26), ("workflow", 29), ("systemform", 60), ("webresource", 61), ("sitemap", 62), ("fieldsecurityprofile", 70), ("appmodule", 80),
        ("plugintype", 90), ("pluginassembly", 91), ("sdkmessageprocessingstep", 92), ("sdkmessageprocessingstepimage", 93),
        ("customapi", 9990), ("environmentvariabledefinition", 380), ("customcontrol", 66), ("savedqueryvisualization", 59),
    ];

    public static Command Build(CliContext ctx) => new("solution", "Create, import (update, upgrade or stage), export, list, uninstall, promote and delete solutions, and list component layers.")
    {
        Create(ctx),
        Import(ctx),
        Export(ctx),
        List(ctx),
        AddComponent(ctx),
        Publish(ctx),
        Delete(ctx),
        Uninstall(ctx),
        Promote(ctx),
        Layers(ctx),
    };

    private static string SolutionPath(string uniqueName) => $"solutions?$select=solutionid,uniquename&$filter=uniquename eq {UserRoleLookup.Literal(uniqueName)}";

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    private static async Task<Guid> SolutionIdAsync(XrmdClient client, string uniqueName, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync(SolutionPath(uniqueName), ct);
        return rows.Count > 0 && Guid.TryParse(Str(rows[0], "solutionid"), out var id)
            ? id
            : throw new CliException(ExitCodes.NotFound, $"Solution '{uniqueName}' not found.");
    }

    // ---- create -------------------------------------------------------------------------------------------------------

    private static Command Create(CliContext ctx)
    {
        var name = new Argument<string>("uniquename") { Description = "Unique name of the new solution." };
        var publisher = new Option<string>("--publisher") { Description = "Unique name of the publisher.", Required = true };
        var displayName = new Option<string?>("--display-name") { Description = "Display name; default: the unique name." };
        var version = new Option<string>("--version") { Description = "Solution version.", DefaultValueFactory = _ => "1.0.0.0" };
        var command = new Command("create", "Create an unmanaged solution.") { name, publisher, displayName, version };
        command.SetAction(async (parse, ct) =>
        {
            var uniqueName = parse.GetValue(name)!;
            var publisherName = parse.GetValue(publisher)!;
            var client = ctx.CreateClient();
            var publishers = await client.GetAllPagesAsync(
                $"publishers?$select=publisherid&$filter=uniquename eq {UserRoleLookup.Literal(publisherName)}", ct);
            if (publishers.Count == 0 || Str(publishers[0], "publisherid") is not { } publisherId)
                throw new CliException(ExitCodes.NotFound, $"Publisher '{publisherName}' not found.");
            var versionText = parse.GetValue(version)!;
            var response = await client.SendJsonAsync(HttpMethod.Post, "solutions", new Dictionary<string, object?>
            {
                ["uniquename"] = uniqueName,
                ["friendlyname"] = parse.GetValue(displayName) ?? uniqueName,
                ["version"] = versionText,
                ["publisherid@odata.bind"] = $"/publishers({publisherId})",
            }, cancellationToken: ct);
            ctx.WriteResult(new { uniquename = uniqueName, solutionId = response.EntityId, publisher = publisherName, version = versionText },
                $"Created solution '{uniqueName}' (publisher '{publisherName}', version {versionText}).");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- import -------------------------------------------------------------------------------------------------------

    private static Command Import(CliContext ctx)
    {
        var file = new Argument<FileInfo>("zip") { Description = "The solution zip file." };
        var mode = new Option<string>("--mode")
        {
            Description = "update (default): import over the installed version; upgrade: import and apply it at once (StageAndUpgrade; refused when it would drop data); "
                          + "stage: import as the holding solution {name}_Upgrade, to apply later with 'solution promote'.",
            DefaultValueFactory = _ => "update",
        };
        mode.AcceptOnlyFromAmong("update", "upgrade", "stage");
        var async = new Option<bool>("--async") { Description = "Queue the import as a system job and wait for it (polls every 2 s, progress on stderr)." };
        var timeout = new Option<int>("--timeout")
        {
            Description = $"With --async: minutes to wait for the system job before giving up (exit 6; the import keeps running). Default {DefaultTimeoutMinutes}.",
            DefaultValueFactory = _ => DefaultTimeoutMinutes,
        };
        var overwrite = new Option<bool>("--overwrite") { Description = "Overwrite unmanaged customizations." };
        var command = new Command("import", "Import a solution zip: update, upgrade or stage.") { file, mode, async, timeout, overwrite };
        command.SetAction(async (parse, ct) =>
        {
            var info = parse.GetValue(file)!;
            var isAsync = parse.GetValue(async);
            var minutes = parse.GetValue(timeout);
            if (minutes <= 0) throw new CliException(ExitCodes.Usage, "--timeout must be a positive number of minutes.");
            if (!isAsync && parse.GetResult(timeout) is { Implicit: false }) throw new CliException(ExitCodes.Usage, "--timeout applies only with --async.");
            if (!info.Exists) throw new CliException(ExitCodes.Usage, $"File '{info.FullName}' not found.");
            var body = new Dictionary<string, object?>
            {
                ["CustomizationFile"] = Convert.ToBase64String(await File.ReadAllBytesAsync(info.FullName, ct)),
                ["OverwriteUnmanagedCustomizations"] = parse.GetValue(overwrite),
                ["PublishWorkflows"] = false,
            };
            var importMode = parse.GetValue(mode)!;
            if (importMode == "stage") body["HoldingSolution"] = true;
            var messageName = (importMode == "upgrade" ? "StageAndUpgrade" : "ImportSolution") + (isAsync ? "Async" : "");
            var done = importMode switch
            {
                "upgrade" => "Imported and upgraded the solution",
                "stage" => StagedMessage(UniqueNameOf(info.FullName)),
                _ => "Imported the solution",
            };
            var client = ctx.CreateClient();
            if (isAsync) await ImportAsyncAsync(ctx, client, messageName, done, body, TimeSpan.FromMinutes(minutes), ct);
            else await ImportSyncAsync(ctx, client, messageName, done, body, ct);
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>The outcome line of <c>import --mode stage</c>, naming the holding solution and the promote command.</summary>
    /// <param name="uniqueName">The solution's unique name from its <c>solution.xml</c>; null when the file does not say.</param>
    internal static string StagedMessage(string? uniqueName) => uniqueName is null
        ? "Staged the solution as its holding solution (its unique name with the suffix _Upgrade); apply it with 'xrmd solution promote <uniquename>'"
        : $"Staged the solution as its holding solution {uniqueName}_Upgrade; apply it with 'xrmd solution promote {uniqueName}'";

    /// <summary>
    /// The unique name in <c>solution.xml</c> of the solution zip <paramref name="path"/>
    /// (<c>ImportExportXml/SolutionManifest/UniqueName</c>); null when the file is not such a zip (the server judges the file).
    /// </summary>
    internal static string? UniqueNameOf(string path)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, "solution.xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            using var stream = entry.Open();
            var name = (string?)XDocument.Load(stream).Root?.Element("SolutionManifest")?.Element("UniqueName");
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task ImportSyncAsync(CliContext ctx, XrmdClient client, string messageName, string done, Dictionary<string, object?> body,
        CancellationToken ct)
    {
        var jobId = Guid.NewGuid();
        body["ImportJobId"] = jobId;
        try
        {
            await client.SendJsonAsync(HttpMethod.Post, messageName, body, cancellationToken: ct);
        }
        catch (CliException ex) when (ex.ExitCode is ExitCodes.Unexpected or ExitCodes.NotFound or ExitCodes.Rejected)
        {
            // A failed import leaves its log in the importjob: show it, and exit 5 whatever the status of the refusal was.
            if (await PrintImportLogAsync(ctx, client, jobId, ct)) throw new CliException(ExitCodes.Rejected, $"Solution import failed: {ex.Message}", ex);
            throw;
        }
        ctx.WriteResult(new { importJobId = jobId, status = "completed" }, $"{done} (import job {jobId}).");
    }

    /// <summary>
    /// Queues the import and polls its system job on the injected clock until it completes or <paramref name="timeout"/>
    /// passes. A failed read of the system job (unreachable, 5xx, 404) is retried up to
    /// <see cref="MaxConsecutivePollFailures"/> times in a row; 401/403 and any other error end the command at once. The
    /// import progress is a courtesy: its read failing (except 401/403) only drops the percentage. On timeout the command
    /// exits 6 without claiming anything about the import, which keeps running on the server.
    /// </summary>
    private static async Task ImportAsyncAsync(CliContext ctx, XrmdClient client, string messageName, string done, Dictionary<string, object?> body,
        TimeSpan timeout, CancellationToken ct)
    {
        var started = await client.SendJsonAsync(HttpMethod.Post, messageName, body, cancellationToken: ct);
        var json = started.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for {messageName}.");
        var operationId = Guid.Parse(Str(json, "AsyncOperationId") ?? throw new CliException(ExitCodes.Unexpected, $"{messageName} returned no AsyncOperationId."));
        var jobKey = Str(json, "ImportJobKey");
        await ctx.Env.Err.WriteLineAsync($"Queued import (system job {operationId}).");

        var deadline = ctx.Env.Clock.GetUtcNow() + timeout;
        var failures = 0;
        string? last = null;
        while (true)
        {
            JsonElement? read = null;
            try
            {
                read = await client.GetJsonAsync($"asyncoperations({operationId})?$select=statecode,statuscode,message", ct);
                failures = 0;
            }
            catch (CliException ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                if (++failures >= MaxConsecutivePollFailures)
                    throw new CliException(ex.ExitCode,
                        $"Reading system job {operationId} failed {failures} times in a row ({ex.Message}). The import may still be running; check asyncoperation {operationId}.",
                        ex) { HttpStatus = ex.HttpStatus };
                await ctx.Env.Err.WriteLineAsync($"(could not read the system job: {ex.Message}; retrying)");
            }

            if (read is { } operation)
            {
                var state = operation.GetProperty("statecode").GetInt32();
                var status = operation.GetProperty("statuscode").GetInt32();
                var progress = jobKey is not null && Guid.TryParse(jobKey, out var key) ? await ProgressAsync(client, key, ct) : null;
                var line = $"{StatusText(state, status)}{(progress is { } p ? $", import progress {p.ToString("0.##", CultureInfo.InvariantCulture)}%" : "")}";
                if (line != last)
                {
                    await ctx.Env.Err.WriteLineAsync(line);
                    last = line;
                }
                if (state == Completed)
                {
                    if (status == Succeeded)
                    {
                        ctx.WriteResult(new { importJobId = jobKey, asyncOperationId = operationId, status = "completed" },
                            $"{done} (import job {jobKey}).");
                        return;
                    }
                    var message = Str(operation, "message") ?? StatusText(state, status);
                    if (jobKey is not null && Guid.TryParse(jobKey, out var failedKey)) await PrintImportLogAsync(ctx, client, failedKey, ct);
                    throw new CliException(ExitCodes.Rejected, $"Solution import {(status == Canceled ? "was canceled" : "failed")}: {message}");
                }
            }

            if (ctx.Env.Clock.GetUtcNow() >= deadline)
                throw new CliException(ExitCodes.Unreachable,
                    $"Import still running after {timeout.TotalMinutes:0.##} minute(s); stopped waiting. Check asyncoperation {operationId}" +
                    $"{(jobKey is null ? "" : $" (import job {jobKey})")} for its outcome.");
            await Task.Delay(TimeSpan.FromSeconds(PollSeconds), ctx.Env.Clock, ct);
        }
    }

    /// <summary>A failed read worth retrying while polling: unreachable server, a 5xx or a 404 (never 401/403 or another 4xx).</summary>
    private static bool IsTransient(CliException ex) =>
        ex.ExitCode == ExitCodes.Unreachable || ex.HttpStatus is >= 500 or 404;

    private static string StatusText(int state, int status) => (state, status) switch
    {
        (0, _) => "Waiting",
        (1, _) => "Waiting",
        (2, _) => "In progress",
        (3, Succeeded) => "Succeeded",
        (3, Failed) => "Failed",
        (3, Canceled) => "Canceled",
        _ => $"State {state}, status {status}",
    };

    private static async Task<double?> ProgressAsync(XrmdClient client, Guid importJobId, CancellationToken ct)
    {
        try
        {
            var job = await client.GetJsonAsync($"importjobs({importJobId})?$select=progress", ct);
            return job.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
        }
        catch (CliException ex) when (ex.ExitCode != ExitCodes.Auth && !ct.IsCancellationRequested)
        {
            return null; // 404: the job has not started yet; anything else but 401/403: the percentage is a courtesy
        }
    }

    /// <summary>Writes the failing entries of an import job's log to stderr; false when the import job does not exist.</summary>
    private static async Task<bool> PrintImportLogAsync(CliContext ctx, XrmdClient client, Guid importJobId, CancellationToken ct)
    {
        JsonElement job;
        try
        {
            job = await client.GetJsonAsync($"importjobs({importJobId})?$select=data,progress", ct);
        }
        catch (CliException ex)
        {
            // The log is a courtesy: whatever went wrong fetching it must not replace the import's own error.
            await ctx.Env.Err.WriteLineAsync($"(import log not available: {ex.Message})");
            return false;
        }
        await ctx.Env.Err.WriteLineAsync($"Import job {importJobId} log:");
        await ctx.Env.Err.WriteLineAsync(Str(job, "data") is { Length: > 0 } data ? SummarizeLog(data) : "  (the import job has no log)");
        return true;
    }

    /// <summary>The failures (and the number of warnings) of an import log, one line per failing component; the raw text if it is not XML.</summary>
    internal static string SummarizeLog(string logXml)
    {
        XElement root;
        try
        {
            root = XElement.Parse(logXml);
        }
        catch (System.Xml.XmlException)
        {
            return logXml;
        }
        var lines = new List<string>();
        foreach (var result in root.Descendants("result"))
        {
            var kind = (string?)result.Attribute("result");
            if (kind != "failure") continue;
            var owner = result.Parent;
            var component = owner is null || owner.Name == "solutionManifest" && owner.Parent?.Name == "solutionManifests"
                ? "solution"
                : $"{owner.Name.LocalName} {(string?)owner.Attribute("LocalizedName") ?? (string?)owner.Attribute("id")}";
            var code = (string?)result.Attribute("errorcode");
            lines.Add($"  failure: {component}: {(string?)result.Attribute("errortext")}{(code is { Length: > 1 } && code != "0" ? $" ({code})" : "")}");
        }
        var warnings = root.Descendants("result").Count(r => (string?)r.Attribute("result") == "warning");
        if (warnings > 0) lines.Add($"  ({warnings} warning(s) in the log)");
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "  (the log lists no failure)";
    }

    // ---- export / list ------------------------------------------------------------------------------------------------

    private static Command Export(CliContext ctx)
    {
        var name = new Argument<string>("uniquename") { Description = "Unique name of the solution." };
        var output = new Option<FileInfo>("--out") { Description = "File to write the zip to.", Required = true };
        var command = new Command("export", "Export an unmanaged solution to a zip file.") { name, output };
        command.SetAction(async (parse, ct) =>
        {
            var uniqueName = parse.GetValue(name)!;
            var target = parse.GetValue(output)!;
            var response = await ctx.CreateClient().SendJsonAsync(HttpMethod.Post, "ExportSolution",
                new Dictionary<string, object?> { ["SolutionName"] = uniqueName, ["Managed"] = false }, cancellationToken: ct);
            var file = response.Json is { } json && Str(json, "ExportSolutionFile") is { } base64
                ? Convert.FromBase64String(base64)
                : throw new CliException(ExitCodes.Unexpected, "The server returned no ExportSolutionFile.");
            await File.WriteAllBytesAsync(target.FullName, file, ct);
            ctx.WriteResult(new { solution = uniqueName, file = target.FullName, bytes = file.LongLength },
                $"Exported '{uniqueName}' to {target.FullName} ({file.Length} bytes).");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command List(CliContext ctx)
    {
        var command = new Command("list", "List the solutions.");
        command.SetAction(async (_, ct) =>
        {
            var client = ctx.CreateClient();
            var publishers = (await client.GetAllPagesAsync("publishers?$select=publisherid,uniquename", ct))
                .Where(p => Str(p, "publisherid") is not null)
                .ToDictionary(p => Str(p, "publisherid")!, p => Str(p, "uniquename"), StringComparer.OrdinalIgnoreCase);
            var rows = await client.GetAllPagesAsync("solutions?$select=uniquename,friendlyname,version,ismanaged,_publisherid_value", ct);
            ctx.Write(rows.OrderBy(r => Str(r, "uniquename"), StringComparer.Ordinal).Select(r => new
            {
                uniquename = Str(r, "uniquename"),
                friendlyname = Str(r, "friendlyname"),
                version = Str(r, "version"),
                managed = r.TryGetProperty("ismanaged", out var m) && m.ValueKind == JsonValueKind.True,
                publisher = Str(r, "_publisherid_value") is { } id && publishers.TryGetValue(id, out var name) ? name : null,
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- add-component / publish --------------------------------------------------------------------------------------

    private static Command AddComponent(CliContext ctx)
    {
        var name = new Argument<string>("uniquename") { Description = "Unique name of the solution." };
        var type = new Option<string>("--type") { Description = "Component type: an integer or " + string.Join(", ", ComponentTypes.Select(t => t.Name)) + ".", Required = true };
        var id = new Option<string>("--id") { Description = "Id of the component (MetadataId or row id).", Required = true };
        var command = new Command("add-component", "Add a component to an unmanaged solution.") { name, type, id };
        command.SetAction(async (parse, ct) =>
        {
            var uniqueName = parse.GetValue(name)!;
            var typeValue = ParseComponentType(parse.GetValue(type)!);
            if (!Guid.TryParse(parse.GetValue(id), out var componentId))
                throw new CliException(ExitCodes.Usage, $"--id: '{parse.GetValue(id)}' is not a GUID.");
            var response = await ctx.CreateClient().SendJsonAsync(HttpMethod.Post, "AddSolutionComponent", new Dictionary<string, object?>
            {
                ["ComponentId"] = componentId,
                ["ComponentType"] = typeValue,
                ["SolutionUniqueName"] = uniqueName,
                ["AddRequiredComponents"] = false,
                ["DoNotIncludeSubcomponents"] = false,
            }, cancellationToken: ct);
            var solutionComponentId = response.Json is { } json ? Str(json, "id") : null;
            ctx.WriteResult(new { solution = uniqueName, componentType = typeValue, componentId, solutionComponentId },
                $"Added component {componentId} (type {typeValue}) to '{uniqueName}'.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static int ParseComponentType(string text)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        foreach (var (name, number) in ComponentTypes)
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase)) return number;
        throw new CliException(ExitCodes.Usage,
            $"--type: unknown component type '{text}'. Use an integer or one of: {string.Join(", ", ComponentTypes.Select(t => t.Name))}.");
    }

    private static Command Publish(CliContext ctx)
    {
        var command = new Command("publish", "Publish all customizations (PublishAllXml).");
        command.SetAction(async (_, ct) =>
        {
            await ctx.CreateClient().SendJsonAsync(HttpMethod.Post, "PublishAllXml", cancellationToken: ct);
            ctx.WriteResult(new { published = true }, "Published all customizations.");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- uninstall / promote / layers ---------------------------------------------------------------------------------

    private static Command Uninstall(CliContext ctx)
    {
        var name = new Argument<string>("uniquename") { Description = "Unique name of the managed solution." };
        var dropData = new Option<bool>("--drop-data")
        {
            Description = "Confirm the loss of data (tables with rows, columns with values) the uninstall would drop; without it such an uninstall is refused.",
        };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print what the uninstall would delete, restore and keep, and change nothing." };
        var command = new Command("uninstall", "Uninstall a managed solution: delete what only it delivered, restore the layer below for the rest.")
            { name, dropData, dryRun, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            await RemoveAsync(ctx, "uninstall", parse.GetValue(name)!, parse.GetValue(dropData), parse.GetValue(dryRun), parse.GetValue(ctx.YesOption), "Uninstalled", ct);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Promote(CliContext ctx)
    {
        var name = new Argument<string>("uniquename") { Description = "Unique name of the managed solution whose staged upgrade {name}_Upgrade is applied." };
        var dropData = new Option<bool>("--drop-data")
        {
            Description = "Confirm the loss of data (tables with rows, columns with values) the upgrade would drop; without it such a promote is refused.",
        };
        var command = new Command("promote", "Apply the staged upgrade {name}_Upgrade of a managed solution (DeleteAndPromote).") { name, dropData, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            await RemoveAsync(ctx, "promote", parse.GetValue(name)!, parse.GetValue(dropData), dryRun: false, parse.GetValue(ctx.YesOption), "Promoted", ct);
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>
    /// <c>uninstall</c> and <c>promote</c> (Admin API): with <paramref name="dryRun"/> the plan is the output; without <c>--yes</c> the
    /// plan is printed to stderr and confirmed (a plan that drops data without <c>--drop-data</c> is refused, exit 5, before the
    /// question); then the operation runs. The server refuses a data loss without <c>dropData</c> whatever the CLI did.
    /// </summary>
    private static async Task RemoveAsync(CliContext ctx, string action, string uniqueName, bool dropData, bool dryRun, bool yes, string past,
        CancellationToken ct)
    {
        var client = ctx.CreateClient();
        var path = $"solutions/{Uri.EscapeDataString(uniqueName)}/{action}";

        async Task<JsonElement> PostAsync(bool dry)
        {
            var response = await client.AdminAsync(HttpMethod.Post, path,
                XrmdClient.JsonContent(new Dictionary<string, object?> { ["dropData"] = dropData, ["dryRun"] = dry }), ct);
            return response.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for the {action}.");
        }

        if (dryRun)
        {
            var plan = await PostAsync(true);
            if (ctx.Format == Output.OutputFormat.Json) ctx.Write(plan);
            else WritePlan(ctx, ctx.Env.Out, plan, $"Dry run: '{uniqueName}' would be {past.ToLowerInvariant()}");
            return;
        }
        if (!yes)
        {
            var plan = await PostAsync(true);
            WritePlan(ctx, ctx.Env.Err, plan, $"'{uniqueName}' would be {past.ToLowerInvariant()}");
            if (DataLoss(plan).Count > 0 && !dropData)
                throw new CliException(ExitCodes.Rejected,
                    $"This {action} would drop data (listed above). Nothing was changed. Run it again with --drop-data to confirm the loss.");
            await ctx.ConfirmDestructiveAsync($"This {action}s the managed solution '{uniqueName}'{(DataLoss(plan).Count > 0 ? " and drops the data listed above" : "")}. Continue?");
        }
        var result = await PostAsync(false);
        if (ctx.Format == Output.OutputFormat.Json) ctx.Write(result);
        else WritePlan(ctx, ctx.Env.Out, result, $"{past} '{uniqueName}'");
    }

    private static List<JsonElement> Items(JsonElement plan, string name) =>
        plan.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];

    private static List<JsonElement> DataLoss(JsonElement plan) => Items(plan, "dataLoss");

    /// <summary>The plan of an uninstall or promote (<c>{deleted, restored, kept, dataLoss}</c>) as text: a summary line and one table per list.</summary>
    private static void WritePlan(CliContext ctx, TextWriter writer, JsonElement plan, string title)
    {
        var deleted = Items(plan, "deleted");
        var restored = Items(plan, "restored");
        var kept = Items(plan, "kept");
        var loss = DataLoss(plan);
        writer.WriteLine($"{title}: {deleted.Count} component(s) deleted, {restored.Count} restored, {kept.Count} kept.");
        var renderer = new Output.Renderer(writer);
        void Section(string heading, IReadOnlyList<object> rows)
        {
            if (rows.Count == 0) return;
            writer.WriteLine();
            writer.WriteLine(heading);
            renderer.Write(rows, Output.OutputFormat.Table);
        }
        Section("Deleted", deleted.Select(c => (object)new { type = TypeText(c), name = Str(c, "name") }).ToList());
        Section("Restored", restored.Select(c => (object)new { name = c.ValueKind == JsonValueKind.String ? c.GetString() : Str(c, "name") }).ToList());
        Section("Kept", kept.Select(c => (object)new { type = TypeText(c), name = Str(c, "name"), reason = Str(c, "reason") }).ToList());
        Section("Data loss", loss.Select(l => (object)new
        {
            table = Str(l, "table"),
            column = Str(l, "column"),
            rows = l.TryGetProperty("rows", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt64() : 0,
        }).ToList());
    }

    private static Command Layers(CliContext ctx)
    {
        var name = new Argument<string?>("uniquename")
        {
            Description = "Unique name of a solution: lists the layers it owns. Omit it and give --type and --id for the stack of one component.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var type = new Option<string?>("--type") { Description = "Component type: an integer or " + string.Join(", ", ComponentTypes.Select(t => t.Name)) + "." };
        var id = new Option<string?>("--id") { Description = "Id of the component (MetadataId or row id)." };
        var command = new Command("layers", "List the layers of a solution's components, or the stack of one component (top layer first).") { name, type, id };
        command.SetAction(async (parse, ct) =>
        {
            var uniqueName = parse.GetValue(name);
            var typeText = parse.GetValue(type);
            var idText = parse.GetValue(id);
            string path;
            if (uniqueName is not null)
            {
                if (typeText is not null || idText is not null) throw new CliException(ExitCodes.Usage, "Give a solution name, or --type with --id, not both.");
                path = $"solutions/{Uri.EscapeDataString(uniqueName)}/layers";
            }
            else
            {
                if (typeText is null || idText is null) throw new CliException(ExitCodes.Usage, "Give a solution name, or both --type and --id.");
                if (!Guid.TryParse(idText, out var componentId)) throw new CliException(ExitCodes.Usage, $"--id: '{idText}' is not a GUID.");
                path = $"layers/{ParseComponentType(typeText).ToString(CultureInfo.InvariantCulture)}/{componentId}";
            }
            var response = await ctx.CreateClient().AdminAsync(HttpMethod.Get, path, cancellationToken: ct);
            var rows = response.Json is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToList()
                : throw new CliException(ExitCodes.Unexpected, "The server returned no list of layers.");
            ctx.Write(rows.Select(r => new
            {
                type = TypeText(r, "componentType"),
                objectId = Str(r, "objectId"),
                solution = Str(r, "solution"),
                kind = Str(r, "kind"),
                order = r.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt64() : 0,
                version = Str(r, "version"),
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- delete -------------------------------------------------------------------------------------------------------

    private static Command Delete(CliContext ctx)
    {
        var name = new Argument<string>("uniquename") { Description = "Unique name of the solution." };
        var cascade = new Option<bool>("--cascade")
        {
            Description = "Also delete the components no other solution holds (tables and their data, columns, relationships, choices, ...) through the Admin API. "
                          + "Without it only the solution is deleted and its components stay in the system.",
        };
        var command = new Command("delete", "Delete a solution; with --cascade also its components.") { name, cascade, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var uniqueName = parse.GetValue(name)!;
            var client = ctx.CreateClient();
            if (!parse.GetValue(cascade))
            {
                await ctx.ConfirmDestructiveAsync($"This deletes the solution '{uniqueName}'; its components stay in the system. Continue?");
                var id = await SolutionIdAsync(client, uniqueName, ct);
                await client.SendJsonAsync(HttpMethod.Delete, $"solutions({id})", cancellationToken: ct);
                ctx.WriteResult(new { deleted = uniqueName }, $"Deleted solution '{uniqueName}' (its components were kept).");
                return ExitCodes.Ok;
            }

            await ctx.ConfirmDestructiveAsync(
                $"This permanently deletes the solution '{uniqueName}' and every component no other solution holds, including tables and their data. Continue?");
            var response = await client.AdminAsync(HttpMethod.Delete, $"solutions/{Uri.EscapeDataString(uniqueName)}?cascade=true", cancellationToken: ct);
            var result = response.Json ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for the cascade delete.");
            var deleted = result.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray().ToList() : [];
            var kept = result.TryGetProperty("kept", out var k) && k.ValueKind == JsonValueKind.Array ? k.EnumerateArray().ToList() : [];
            if (ctx.Format == Output.OutputFormat.Json)
            {
                ctx.Write(result);
            }
            else
            {
                ctx.Env.Out.WriteLine($"Deleted solution '{uniqueName}' and {deleted.Count} component(s); kept {kept.Count}.");
                if (deleted.Count > 0)
                {
                    ctx.Env.Out.WriteLine();
                    ctx.Env.Out.WriteLine("Deleted");
                    ctx.Write(deleted.Select(c => new { type = TypeText(c), name = Str(c, "name") }).ToList());
                }
                if (kept.Count > 0)
                {
                    ctx.Env.Out.WriteLine();
                    ctx.Env.Out.WriteLine("Kept");
                    ctx.Write(kept.Select(c => new { type = TypeText(c), name = Str(c, "name"), reason = Str(c, "reason") }).ToList());
                }
            }
            return ExitCodes.Ok;
        });
        return command;
    }

    private static string TypeText(JsonElement component, string property = "type")
    {
        if (!component.TryGetProperty(property, out var t) || t.ValueKind != JsonValueKind.Number) return "";
        var value = t.GetInt32();
        var name = ComponentTypes.FirstOrDefault(c => c.Value == value).Name;
        return name is null ? value.ToString(CultureInfo.InvariantCulture) : $"{name} ({value})";
    }
}
