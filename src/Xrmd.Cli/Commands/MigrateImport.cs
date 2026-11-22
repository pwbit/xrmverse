using Xrmd.Migration.Users;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xrmd.Cli.Http;
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
    public static JsonObject Options(bool strict, bool strictLookups, bool baseCurrencyMap, bool keepPackage)
    {
        var options = new JsonObject();
        if (strict) options["strict"] = "true";
        if (strictLookups) options["strictLookups"] = "true";
        if (baseCurrencyMap) options["baseCurrencyMap"] = "true";
        if (keepPackage) options["keepPackage"] = "true";
        return options;
    }

    /// <summary>
    /// Uploads <paramref name="file"/> (resuming an interrupted upload), or names the package at <paramref name="serverPath"/>, and
    /// starts the run: returns its id.
    /// </summary>
    public static async Task<Guid> StartAsync(CliContext ctx, Http.XrmdClient client, string? file, string? serverPath, JsonObject? usersJson, JsonObject options,
        string passphrase, CancellationToken ct)
    {
        var body = new JsonObject { ["passphrase"] = passphrase };
        if (file is not null) body["packageId"] = (await UploadAsync(ctx, client, file, ct)).ToString();
        else body["serverPath"] = serverPath;
        if (usersJson is not null) body["users"] = usersJson;
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
    public static async Task<int> WaitAsync(CliContext ctx, XrmdClient client, Guid runId, CancellationToken ct)
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
