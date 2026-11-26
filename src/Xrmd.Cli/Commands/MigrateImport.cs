using Xrmd.Migration.Users;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xrmd.Cli.Http;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Packaging;
using YamlDotNet.RepresentationModel;

namespace Xrmd.Cli.Commands;

/// <summary>
/// The client half of <c>xrmd migrate import</c> (plan M4b task 4): the users YAML read into the start body's <c>users</c>, the
/// package uploaded in chunks of <see cref="DefaultChunkBytes"/> (each retried up to <see cref="ChunkAttempts"/> times) with a local
/// record of the upload so that an interrupted one resumes, and the wait that prints each phase once.
/// </summary>
internal static class MigrateImport
{
    /// <summary>The Admin API's largest chunk (8 MiB).</summary>
    public const int DefaultChunkBytes = 8 * 1024 * 1024;

    /// <summary>Attempts per chunk before the upload gives up.</summary>
    public const int ChunkAttempts = 3;

    private const int PollSeconds = 2;

    /// <summary>The upload chunk size; tests make it smaller.</summary>
    internal static int ChunkBytes { get; set; } = DefaultChunkBytes;

    /// <summary>The actions of a users mapping (<c>xrmd migrate users</c>).</summary>

    /// <summary>The final states of a run and the exit code each one gives.</summary>
    public static int? ExitCodeOf(string state) => state switch
    {
        "completed" or "finalized" => ExitCodes.Ok,
        "completed_with_errors" or "failed" or "cancelled" => ExitCodes.Rejected,
        _ => null,
    };

    /// <summary>
    /// The users YAML of <c>xrmd migrate users</c> as the JSON of the start body (every scalar a string), checked here with the
    /// server's own rules so that a bad file fails before an upload (exit 2).
    /// </summary>
    public static JsonObject UsersJson(string file)
    {
        if (!File.Exists(file)) throw new CliException(ExitCodes.Usage, $"'{file}' does not exist.");
        var yaml = new YamlStream();
        try
        {
            using var reader = new StreamReader(file);
            yaml.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new CliException(ExitCodes.Usage, $"'{file}' is not valid YAML: {e.Message}", e);
        }
        if (yaml.Documents.Count == 0 || ToJson(yaml.Documents[0].RootNode) is not JsonObject json)
            throw new CliException(ExitCodes.Usage, $"'{file}' is not a users mapping: it has no 'users' list.");
        // The server's own check (Xrmd.Migration), so that the two never disagree.
        try { UserMapping.Parse(json); }
        catch (UserMappingException e) { throw new CliException(ExitCodes.Usage, $"'{file}': {e.Message}", e); }
        return json;
    }

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(c => KeyValuePair.Create(((YamlScalarNode)c.Key).Value ?? "", ToJson(c.Value)))),
        YamlSequenceNode s => new JsonArray([.. s.Children.Select(ToJson)]),
        YamlScalarNode { Value: { } v } => JsonValue.Create(v),
        _ => null,
    };

    /// <summary>The run options of the start body (each one the string "true" when set).</summary>
    public static JsonObject Options(bool strict, bool strictLookups, bool baseCurrencyMap, bool keepPackage, bool dryRun = false)
    {
        var options = new JsonObject();
        if (dryRun) options["dryRun"] = "true";
        if (strict) options["strict"] = "true";
        if (strictLookups) options["strictLookups"] = "true";
        if (baseCurrencyMap) options["baseCurrencyMap"] = "true";
        if (keepPackage) options["keepPackage"] = "true";
        return options;
    }

    /// <summary>
    /// Uploads <paramref name="file"/> (resuming an interrupted upload), or names the package at <paramref name="serverPath"/>, and
    /// starts the run: returns its id. <paramref name="maps"/> (YAML texts), when given, replace the package's maps for the run.
    /// </summary>
    public static async Task<Guid> StartAsync(CliContext ctx, Http.XrmdClient client, string? file, string? serverPath, JsonObject? usersJson,
        IReadOnlyList<string>? maps, JsonObject options, string passphrase, CancellationToken ct)
    {
        var body = new JsonObject { ["passphrase"] = passphrase };
        if (file is not null) body["packageId"] = (await UploadAsync(ctx, client, file, ct)).ToString();
        else body["serverPath"] = serverPath;
        if (usersJson is not null) body["users"] = usersJson;
        if (maps is { Count: > 0 }) body["maps"] = new JsonArray([.. maps.Select(m => (JsonNode)m)]);
        body["options"] = options;
        var started = (await client.AdminAsync(HttpMethod.Post, "migrate/runs",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct)).Json
            ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for POST /admin/migrate/runs.");
        return Guid.Parse(started.GetProperty("runId").GetString()!);
    }

    /// <summary>Checks the passphrase on the package before anything is sent (exit 3 or 8).</summary>
    public static void CheckPackage(string file, string passphrase)
    {
        try
        {
            using var _ = PackageReader.Open(file, passphrase);
        }
        catch (PackagePassphraseException e) { throw new CliException(ExitCodes.PackageInvalid, e.Message, e); }
        catch (PackageFormatException e) { throw new CliException(ExitCodes.PackageInvalid, e.Message, e); }
        catch (PackageIntegrityException e) { throw new CliException(ExitCodes.PackageInvalid, e.Message, e); }
    }

    /// <summary>
    /// Uploads <paramref name="file"/> and returns the package id. The upload is recorded in the CLI's folder by server and SHA-256,
    /// so that a second import of the same file sends only the chunks the server lacks, or none when the server holds it complete.
    /// </summary>
    public static async Task<Guid> UploadAsync(CliContext ctx, XrmdClient client, string file, CancellationToken ct)
    {
        string sha;
        await using (var s = File.OpenRead(file)) sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
        var length = new FileInfo(file).Length;
        var chunks = (int)Math.Max(1, (length + ChunkBytes - 1) / ChunkBytes);
        var key = $"{client.BaseUrl}|{sha}|{ChunkBytes}";
        var uploads = LoadUploads(ctx);

        Guid? id = uploads[key] is JsonValue v && Guid.TryParse(v.GetValue<string>(), out var known) ? known : null;
        var done = new HashSet<int>();
        if (id is { } existing)
        {
            try
            {
                var package = (await client.AdminAsync(HttpMethod.Get, $"migrate/packages/{existing}", cancellationToken: ct)).Json;
                var state = package is { } p && p.TryGetProperty("state", out var st) ? st.GetString() : null;
                if (state == "complete")
                {
                    await ctx.Env.Err.WriteLineAsync($"The server already holds this package ({existing}).");
                    return existing;
                }
                if (package is { } q && q.TryGetProperty("chunks", out var have) && have.ValueKind == JsonValueKind.Array)
                    foreach (var c in have.EnumerateArray()) done.Add(c.GetInt32());
                await ctx.Env.Err.WriteLineAsync($"Resuming the upload of package {existing} ({done.Count} of {chunks} chunks on the server).");
            }
            catch (CliException e) when (e.HttpStatus == (int)HttpStatusCode.NotFound)
            {
                id = null;
            }
        }
        if (id is null)
        {
            var created = (await client.AdminAsync(HttpMethod.Post, "migrate/packages", cancellationToken: ct)).Json
                ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for POST /admin/migrate/packages.");
            id = Guid.Parse(created.GetProperty("packageId").GetString()!);
            uploads[key] = id.Value.ToString();
            SaveUploads(ctx, uploads);
        }

        var buffer = new byte[ChunkBytes];
        await using (var s = File.OpenRead(file))
        {
            for (var n = 0; n < chunks; n++)
            {
                s.Position = (long)n * ChunkBytes;
                var read = await s.ReadAtLeastAsync(buffer, ChunkBytes, throwOnEndOfStream: false, ct);
                if (done.Contains(n)) continue;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        var content = new ByteArrayContent(buffer, 0, read);
                        content.Headers.ContentType = new("application/octet-stream");
                        await client.AdminAsync(HttpMethod.Put, $"migrate/packages/{id}/chunks/{n}", content, ct);
                        break;
                    }
                    catch (CliException e) when (attempt < ChunkAttempts && (e.ExitCode == ExitCodes.Unreachable || e.HttpStatus >= 500) && !ct.IsCancellationRequested)
                    {
                        await ctx.Env.Err.WriteLineAsync($"(chunk {n + 1} of {chunks} failed: {e.Message}; retrying)");
                        await Task.Delay(TimeSpan.FromSeconds(attempt), ctx.Env.Clock, ct);
                    }
                }
                await ctx.Env.Err.WriteLineAsync($"Uploaded {n + 1} of {chunks} chunks.");
            }
        }
        await client.AdminAsync(HttpMethod.Post, $"migrate/packages/{id}/complete", XrmdClient.JsonContent(new { chunks, sha256 = sha }), ct);
        return id.Value;
    }

    /// <summary>
    /// Waits for the run's end, printing each phase once on standard error; returns the exit code of its final state. Ctrl-C
    /// stops waiting and leaves the run going.
    /// </summary>
    /// <remarks>
    /// A <paramref name="dryRun"/> ends without the resume and finalize hints (it needs neither) and writes no result: the caller
    /// prints its report (<see cref="PrintDryRunReportAsync"/>).
    /// </remarks>
    public static async Task<int> WaitAsync(CliContext ctx, XrmdClient client, Guid runId, CancellationToken ct, bool dryRun = false)
    {
        var printed = -1;
        var lastState = "";
        try
        {
            while (true)
            {
                JsonElement run;
                try
                {
                    run = (await client.AdminAsync(HttpMethod.Get, $"migrate/runs/{runId}", cancellationToken: ct)).Json
                        ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET /admin/migrate/runs/{runId}.");
                }
                catch (CliException e) when ((e.ExitCode == ExitCodes.Unreachable || e.HttpStatus >= 500) && !ct.IsCancellationRequested)
                {
                    await ctx.Env.Err.WriteLineAsync($"(could not read the run: {e.Message}; retrying)");
                    await Task.Delay(TimeSpan.FromSeconds(PollSeconds), ctx.Env.Clock, ct);
                    continue;
                }
                var state = run.GetProperty("state").GetString() ?? "";
                var phase = run.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                var names = run.TryGetProperty("phases", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                // "phase" is the order of the last phase completed: print every phase up to the one running now.
                var running = state == "running" ? Math.Min(names.Count, CountUpTo(names, phase) + 1) : CountUpTo(names, phase);
                for (var i = printed + 1; i < running; i++)
                    await ctx.Env.Err.WriteLineAsync($"[{i + 1}/{names.Count}] {names[i]}");
                printed = Math.Max(printed, running - 1);
                if (state != lastState && state is not ("running" or "queued")) await ctx.Env.Err.WriteLineAsync($"Run {runId}: {state}.");
                lastState = state;
                if (ExitCodeOf(state) is { } code)
                {
                    if (run.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } message) await ctx.Env.Err.WriteLineAsync(message);
                    if (dryRun) return code;
                    if (code != ExitCodes.Ok)
                    {
                        await ctx.Env.Err.WriteLineAsync($"See what failed with: xrmd migrate report {runId}");
                        // The run stays open (and the organization locked) until it is resumed or finalized.
                        await ctx.Env.Err.WriteLineAsync($"Resume it with: xrmd migrate import --resume {runId}, or close it with: xrmd migrate finalize {runId}");
                    }
                    ctx.WriteResult(JsonSerializer.Deserialize<JsonElement>(run.GetRawText()), $"Run {runId}: {state}.");
                    return code;
                }
                await Task.Delay(TimeSpan.FromSeconds(PollSeconds), ctx.Env.Clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ctx.Env.Err.WriteLineAsync($"Stopped waiting; the run {runId} goes on. Follow it with: xrmd migrate status {runId} --wait");
            return ExitCodes.Ok;
        }
    }

    /// <summary>
    /// Prints the report of the dry run <paramref name="runId"/> on standard output (Markdown, or JSON with the JSON output format)
    /// and returns the exit code: <paramref name="waited"/> when it is not OK, else <see cref="ExitCodes.Rejected"/> when the report
    /// says it has errors (<c>hasErrors</c>: the real run would stop), whatever the cap on its listed issues; and the report.
    /// </summary>
    public static async Task<(int Exit, JsonElement Report)> PrintDryRunReportAsync(CliContext ctx, XrmdClient client, Guid runId, int waited, CancellationToken ct)
    {
        var report = (await client.AdminAsync(HttpMethod.Get, $"migrate/runs/{runId}/report", cancellationToken: ct)).Json
            ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET /admin/migrate/runs/{runId}/report.");
        if (ctx.Format == Output.OutputFormat.Json) ctx.Write(report);
        else await ctx.Env.Out.WriteAsync(MigrateReportFormat.Markdown(report));
        if (waited != ExitCodes.Ok) return (waited, report);
        return (report.TryGetProperty("hasErrors", out var errors) && errors.ValueKind == JsonValueKind.True ? ExitCodes.Rejected : ExitCodes.Ok, report);
    }

    /// <summary>
    /// The archive choice of a dry run (table mapping spec §8.7): the tables of <c>--archive</c> (null: ask on a terminal), the file the
    /// choices are added to (null: none can be, several maps or the package's without <c>--save-map</c>), the local package (null with
    /// <c>--server-path</c>, for the source catalog) and how the import names it.
    /// </summary>
    public sealed record ArchiveChoice(IReadOnlyList<string>? Tables, string? Target, string? PackageFile, string PackageArgument);

    /// <summary>
    /// The file the archive tables are added to: <c>--save-map</c> when given, else the single <c>--map</c>; null when there are several
    /// maps, or none (the package's), without <c>--save-map</c>.
    /// </summary>
    public static string? ArchiveTarget(IReadOnlyList<string> maps, string? saveMap) => saveMap ?? (maps.Count == 1 ? maps[0] : null);

    /// <summary>The message when no file can take the choices.</summary>
    public const string SaveMapNeeded = "Give --save-map <file> to save the choices.";

    /// <summary>The message when the choices were asked for but the dry run did not end well.</summary>
    public const string NotSaved = "The choices were not saved: the dry run failed.";

    /// <summary>
    /// After a dry run's report: the archive candidates it lists, largest first, the owner's choice (on a terminal, or
    /// <see cref="ArchiveChoice.Tables"/>), and the chosen tables added to the map file as text (<see cref="ArchiveMapWriter"/>).
    /// The file is the one map to import with (<c>--map &lt;file&gt;</c> alone): a new file starts as the map the run used (the report's
    /// <c>maps</c>), and an existing one must already hold every table and relationship of the maps the run used. Exit 2 otherwise, or
    /// for a bad <c>--archive</c>; the file is written only when something is added.
    /// </summary>
    public static async Task ChooseArchiveAsync(CliContext ctx, JsonElement report, ArchiveChoice choice, string passphrase, CancellationToken ct)
    {
        var candidates = report.TryGetProperty("archiveCandidates", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(c => (Table: c.GetProperty("table").GetString() ?? "", Rows: c.TryGetProperty("rows", out var r) && r.TryGetInt64(out var n) ? n : 0))
                .Where(c => c.Table.Length > 0).OrderByDescending(c => c.Rows).ThenBy(c => c.Table, StringComparer.Ordinal).ToList()
            : [];
        if (choice.Tables is null && (!ctx.Env.IsInputTty || candidates.Count == 0)) return;
        if (choice.Target is not { } target)
        {
            await ctx.Env.Err.WriteLineAsync(SaveMapNeeded);
            return;
        }

        var used = report.TryGetProperty("maps", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Select(t => t.GetString() ?? "").ToList() : [];
        string? existing;
        if (File.Exists(target))
        {
            existing = await File.ReadAllTextAsync(target, ct);
            DemandHolds(target, existing, used);
        }
        else if (used.Count > 1)
            throw new CliException(ExitCodes.Usage, $"The dry run used {used.Count} maps, and an import with --map {target} alone would use only it: "
                                                    + $"merge them into one file and give it as --save-map, or as the only --map.");
        else existing = used.Count == 1 ? used[0] : null;
        var named = Parse(existing, target);
        var archived = named?.Tables.Where(t => t.Archive).Select(t => t.Source).ToList() ?? [];
        if (archived.Count > 0)
            await ctx.Env.Err.WriteLineAsync(File.Exists(target)
                ? $"Already archive tables in {target}: {string.Join(", ", archived)}"
                : $"Already archive tables in the map: {string.Join(", ", archived)}");

        var tables = choice.Tables;
        if (tables is null)
        {
            for (var i = 0; i < candidates.Count; i++) await ctx.Env.Err.WriteLineAsync($"{i + 1}. {candidates[i].Table} ({candidates[i].Rows} rows)");
            var picked = await AskAsync(ctx, candidates.Count, ct);
            if (picked.Count == 0) return;
            tables = [.. picked.Select(i => candidates[i - 1].Table)];
        }

        var inMap = named?.Tables.Select(t => t.Source).ToHashSet(StringComparer.Ordinal) ?? [];
        var added = tables.Distinct(StringComparer.Ordinal).Count(t => !inMap.Contains(t));
        if (added == 0)
        {
            await ctx.Env.Err.WriteLineAsync($"{target} already has these tables.");
            return;
        }
        string yaml;
        try
        {
            yaml = ArchiveMapWriter.Append(existing, tables, await ReadCatalogAsync(choice.PackageFile, passphrase, ct),
                candidates.ToDictionary(c => c.Table, c => c.Rows, StringComparer.Ordinal));
        }
        catch (MapFormatException e) { throw new CliException(ExitCodes.Usage, $"{target}: {e.Message}", e); }
        catch (MapValidationException e) { throw new CliException(ExitCodes.Usage, string.Join(Environment.NewLine, e.Problems), e); }
        await File.WriteAllTextAsync(target, yaml, ct);
        await ctx.Env.Err.WriteLineAsync($"Updated {target}: {added} archive table(s) added. Import with: xrmd migrate import {choice.PackageArgument} --map {target}");
    }

    /// <summary>Exit 2 unless the existing <paramref name="target"/> holds every table and relationship of the maps the dry run used.</summary>
    private static void DemandHolds(string target, string existing, IReadOnlyList<string> used)
    {
        var file = Parse(existing, target);
        var tables = file?.Tables.Select(t => t.Source).ToHashSet(StringComparer.Ordinal) ?? [];
        var relationships = file?.Relationships.Select(r => r.Source).ToHashSet(StringComparer.Ordinal) ?? [];
        var missing = new List<string>();
        for (var i = 0; i < used.Count; i++)
        {
            MapFile map;
            try { map = MapReader.Parse(used[i], $"map {i + 1}"); }
            catch (MapFormatException e) { throw new CliException(ExitCodes.Usage, $"A map of the dry run is not valid: {e.Message}", e); }
            missing.AddRange(map.Tables.Select(t => t.Source).Where(t => !tables.Contains(t)).Select(t => $"table '{t}'"));
            missing.AddRange(map.Relationships.Select(r => r.Source).Where(r => !relationships.Contains(r)).Select(r => $"relationship '{r}'"));
        }
        if (missing.Count > 0)
            throw new CliException(ExitCodes.Usage, $"{target} does not hold {string.Join(", ", missing.Distinct(StringComparer.Ordinal))} of the map(s) the dry run "
                                                    + $"used, and an import with --map {target} alone would leave them out: add them to it, or give a new --save-map file.");
    }

    /// <summary>The map of <paramref name="yaml"/>; null for no text or a file of comments only. Exit 2 for a bad map.</summary>
    private static MapFile? Parse(string? yaml, string target)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        try
        {
            return MapReader.Parse(yaml, Path.GetFileName(target));
        }
        catch (MapFormatException e)
        {
            if (new YamlStream() is var stream && TryLoad(stream, yaml) && stream.Documents.Count == 0) return null;
            throw new CliException(ExitCodes.Usage, $"{target}: {e.Message}", e);
        }
    }

    private static bool TryLoad(YamlStream stream, string yaml)
    {
        try
        {
            stream.Load(new StringReader(yaml));
            return true;
        }
        catch (YamlDotNet.Core.YamlException) { return false; }
    }

    /// <summary>Asks for the numbers of the tables (<c>1,3-5</c>) until the answer is valid; empty (or the end of the input) is none.</summary>
    private static async Task<List<int>> AskAsync(CliContext ctx, int count, CancellationToken ct)
    {
        while (true)
        {
            await ctx.Env.Err.WriteAsync("Create which as archive tables? (e.g. 1,3-5; empty = none): ");
            var line = (await ctx.Env.In.ReadLineAsync(ct))?.Trim();
            if (string.IsNullOrEmpty(line)) return [];
            if (Selection(line, count, out var picked, out var error)) return picked;
            await ctx.Env.Err.WriteLineAsync(error);
        }
    }

    /// <summary>Parses <c>1,3-5</c> into the numbers 1 to <paramref name="count"/>, in order, once each.</summary>
    internal static bool Selection(string text, int count, out List<int> picked, out string error)
    {
        picked = [];
        error = "";
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            var (a, b) = dash > 0 ? (part[..dash].Trim(), part[(dash + 1)..].Trim()) : (part, part);
            if (!int.TryParse(a, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var from)
                || !int.TryParse(b, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var to)
                || from < 1 || to > count || from > to)
            {
                error = $"'{part}' is not a number from 1 to {count}.";
                return false;
            }
            for (var i = from; i <= to; i++) if (!picked.Contains(i)) picked.Add(i);
        }
        return true;
    }

    /// <summary>The source catalog of the local package (for the shortened names); empty for a package on the server.</summary>
    private static async Task<SourceCatalog> ReadCatalogAsync(string? file, string passphrase, CancellationToken ct)
    {
        if (file is null) return SourceCatalog.Empty;
        try
        {
            using var package = PackageReader.Open(file, passphrase);
            return await PackageMaps.ReadCatalogAsync(package, ct);
        }
        catch (PackageFormatException e) { throw new CliException(ExitCodes.PackageInvalid, e.Message, e); }
    }

    /// <summary>
    /// How many phases the run has completed: the server gives the order of the last one, and the import's phases are numbered 1 to n
    /// in the order of its <c>phases</c> list.
    /// </summary>
    private static int CountUpTo(List<string> names, int completedOrder) => Math.Clamp(completedOrder, 0, names.Count);

    private static string UploadsFile(CliContext ctx) => Path.Combine(ctx.Env.ConfigDir, "migrate-uploads.json");

    private static JsonObject LoadUploads(CliContext ctx)
    {
        try
        {
            return File.Exists(UploadsFile(ctx)) && JsonNode.Parse(File.ReadAllText(UploadsFile(ctx))) is JsonObject o ? o : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void SaveUploads(CliContext ctx, JsonObject uploads)
    {
        Directory.CreateDirectory(ctx.Env.ConfigDir);
        File.WriteAllText(UploadsFile(ctx), uploads.ToJsonString());
    }
}
