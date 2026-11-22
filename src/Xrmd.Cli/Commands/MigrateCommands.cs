using System.CommandLine;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xrmd.Cli.Auth;
using Xrmd.Cli.Output;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Source;
using Xrmd.Migration.Users;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd migrate …</c>: the one-off migration from Dataverse (spec 2026-11-16): <c>export</c> reads a Dataverse
/// environment into an encrypted package, <c>inspect</c> verifies a package offline, <c>users</c> writes the users mapping template;
/// <c>import</c>, <c>status</c>, <c>report</c>, <c>cancel</c> and <c>finalize</c> drive an import run on the server (plan M4b).
/// </summary>
public static class MigrateCommands
{
    public static Command Build(CliContext ctx) => new("migrate", "Migrate data from Dataverse: export an environment into an encrypted package, inspect it, map its users, import it.")
    {
        Export(ctx),
        Inspect(ctx),
        Users(ctx),
        Import(ctx),
        Status(ctx),
        Report(ctx),
        Cancel(ctx),
        Finalize(ctx),
        Activate(ctx),
        Run(ctx),
    };

    private static Option<bool> PassphraseStdin() => new("--passphrase-stdin")
    {
        Description = $"Read the package passphrase (one line) from standard input, when {MigratePassphrase.Variable} is not set. A passphrase is never a command-line argument.",
    };

    private static Command Inspect(CliContext ctx)
    {
        var package = new Argument<string>("package") { Description = "The .xrmpkg file, or the folder an export writes." };
        var noVerify = new Option<bool>("--no-verify") { Description = "Only read the manifest; do not decrypt and check every entry." };
        var stdin = PassphraseStdin();
        var command = new Command("inspect", "Show what a migration package holds and verify its entries (offline).") { package, noVerify, stdin };
        command.SetAction(async (parse, ct) =>
        {
            var path = parse.GetValue(package)!;
            if (!File.Exists(path) && !Directory.Exists(path)) throw new CliException(ExitCodes.Usage, $"'{path}' does not exist.");
            var passphrase = await MigratePassphrase.ReadAsync(ctx, parse.GetValue(stdin), ct);
            try
            {
                using var reader = PackageReader.Open(path, passphrase);
                PackageVerification? verification = parse.GetValue(noVerify) ? null : await reader.VerifyAsync(null, ct);
                Print(ctx, reader.Manifest, verification);
                if (verification is { Problems.Count: > 0 })
                {
                    foreach (var p in verification.Problems) await ctx.Env.Err.WriteLineAsync($"Entry '{p.Path}': {p.Problem}");
                    throw new CliException(ExitCodes.PackageInvalid, $"The package failed verification: {verification.Problems.Count} entries cannot be trusted.");
                }
                return ExitCodes.Ok;
            }
            catch (PackagePassphraseException e) { throw new CliException(ExitCodes.Auth, e.Message, e); }
            catch (PackageFormatException e) { throw new CliException(ExitCodes.PackageInvalid, e.Message, e); }
            catch (PackageIntegrityException e) { throw new CliException(ExitCodes.PackageInvalid, e.Message, e); }
        });
        return command;
    }

    /// <summary>The options of <c>export</c> that <c>run</c> shares: what to read from Dataverse and how to sign in.</summary>
    private sealed class ExportArgs
    {
        public Option<string> Source { get; } = new("--source") { Description = "URL of the Dataverse environment, e.g. https://contoso.crm4.dynamics.com.", Required = true };
        public Option<string[]> Solutions { get; } = new("--solution") { Description = "Solutions to export by unique name (comma-separated or repeated)." };
        public Option<bool> AllUnmanaged { get; } = new("--all-unmanaged") { Description = "Export every unmanaged solution (Microsoft ones excepted)." };
        public Option<string[]> SolutionFiles { get; } = new("--solution-file") { Description = "The original file of a managed solution to carry (repeatable): Dataverse cannot export managed solutions." };
        public Option<string[]> Include { get; } = new("--include") { Description = "Optional tables to export anyway (comma-separated or repeated)." };
        public Option<string[]> Exclude { get; } = new("--exclude") { Description = "Tables to leave out (comma-separated or repeated)." };
        public Option<int> Parallel { get; } = new("--parallel") { Description = "Tables exported at the same time.", DefaultValueFactory = _ => 4 };
        public Option<bool> RequireAdmin { get; } = new("--require-admin") { Description = "Stop unless the signed-in user is System Administrator." };
        public Option<string> Auth { get; } = new("--auth") { Description = "Sign-in to Dataverse: browser, devicecode, secret or certificate.", DefaultValueFactory = _ => "browser" };
        public Option<string?> ClientId { get; } = new("--client-id") { Description = "Application (client) id of your own app registration." };
        public Option<string?> Tenant { get; } = new("--tenant") { Description = "Microsoft Entra tenant id or domain." };
        public Option<string?> CertPath { get; } = new("--certificate-path") { Description = $"PFX file for --auth certificate (password in {SourceTokenProvider.CertificatePasswordVariable})." };
        public Option<string?> CertThumbprint { get; } = new("--certificate-thumbprint") { Description = "Thumbprint of a certificate in the current user's store, for --auth certificate." };

        public IEnumerable<Option> All => [Source, Solutions, AllUnmanaged, SolutionFiles, Include, Exclude, Parallel, RequireAdmin, Auth, ClientId, Tenant, CertPath, CertThumbprint];

        /// <summary>Checks the options that need no network (exit 2) and returns the source and the export options.</summary>
        public (Uri Source, ExportOptions Options) Check(ParseResult parse)
        {
            if (!Uri.TryCreate(parse.GetValue(Source), UriKind.Absolute, out var sourceUri) || sourceUri.Scheme != Uri.UriSchemeHttps)
                throw new CliException(ExitCodes.Usage, "--source must be the https URL of the Dataverse environment.");
            var names = List(parse.GetValue(Solutions));
            if (names.Count == 0 && !parse.GetValue(AllUnmanaged))
                throw new CliException(ExitCodes.Usage, "Choose what to export: --solution <name>[,<name>…] and/or --all-unmanaged.");
            return (sourceUri, new ExportOptions(names, parse.GetValue(AllUnmanaged), ManagedFiles(parse.GetValue(SolutionFiles) ?? []), List(parse.GetValue(Include)),
                List(parse.GetValue(Exclude)), Math.Max(1, parse.GetValue(Parallel)), parse.GetValue(RequireAdmin)));
        }

        /// <summary>
        /// Exports into <paramref name="folder"/> (an empty or missing folder, or one with an unfinished export, which resumes), and
        /// packs it into <paramref name="packFile"/> when given.
        /// </summary>
        public async Task<PackageManifest> RunAsync(CliContext ctx, ParseResult parse, Uri sourceUri, ExportOptions options, string folder, string? packFile,
            string passphrase, CancellationToken ct)
        {
            var tokens = SourceTokenProvider.Create(ctx.Env, sourceUri, parse.GetValue(Auth), parse.GetValue(ClientId), parse.GetValue(Tenant),
                parse.GetValue(CertPath), parse.GetValue(CertThumbprint));
            var resume = File.Exists(Path.Combine(folder, PackageHeader.FileName));
            if (!resume && Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                throw new CliException(ExitCodes.Usage, $"'{folder}' is not empty and holds no export to resume: choose an empty folder.");
            PackageManifest manifest;
            try
            {
                using var writer = resume ? PackageWriter.OpenForResume(folder, passphrase) : PackageWriter.Create(folder, passphrase);
                if (resume) await ctx.Env.Err.WriteLineAsync($"Resuming the export in {folder}");
                var handler = ctx.Env.Handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
                try
                {
                    using var client = new DataverseClient(handler, sourceUri, tokens.GetTokenAsync);
                    manifest = await new Exporter(client, options, new LineProgress(ctx.Env.Err)).RunAsync(writer, ct);
                }
                finally
                {
                    if (ctx.Env.Handler is null) handler.Dispose();
                }
            }
            catch (PackagePassphraseException e) { throw new CliException(resume ? ExitCodes.Auth : ExitCodes.Usage, e.Message, e); }
            catch (PackageFormatException e) { throw new CliException(ExitCodes.PackageInvalid, e.Message, e); }
            catch (MigrationExportException e) { throw new CliException(ExitCodes.Rejected, e.Message, e); }
            catch (DataverseException e)
            {
                throw new CliException(e.Status switch
                {
                    null => ExitCodes.Unreachable,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ExitCodes.Auth,
                    _ => ExitCodes.Unexpected,
                }, $"{e.Message} Run the same command again to resume.", e);
            }
            if (packFile is not null) PackageWriter.Pack(folder, packFile);
            return manifest;
        }
    }

    private static Command Export(CliContext ctx)
    {
        var args = new ExportArgs();
        var output = new Option<string>("--out") { Description = "Folder the export writes (empty or missing; a folder with an unfinished export resumes it).", Required = true };
        var pack = new Option<string?>("--pack") { Description = "When the export is complete, also write this .xrmpkg file." };
        var stdin = PassphraseStdin();
        var command = new Command("export", "Read a Dataverse environment (read only) into an encrypted migration package.");
        foreach (var option in args.All.Append(output).Append(pack).Append(stdin)) command.Options.Add(option);
        command.SetAction(async (parse, ct) =>
        {
            var (sourceUri, options) = args.Check(parse);
            var folder = Path.GetFullPath(parse.GetValue(output)!);
            var packFile = parse.GetValue(pack) is { } p ? Path.GetFullPath(p) : null;
            if (packFile is not null && File.Exists(packFile)) throw new CliException(ExitCodes.Usage, $"'{packFile}' already exists.");
            var passphrase = await MigratePassphrase.ReadAsync(ctx, parse.GetValue(stdin), ct);
            var manifest = await args.RunAsync(ctx, parse, sourceUri, options, folder, packFile, passphrase, ct);
            PrintExport(ctx, manifest, folder, packFile);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Users(CliContext ctx)
    {
        var package = new Argument<string>("package") { Description = "The .xrmpkg file, or the folder an export writes." };
        var stdin = PassphraseStdin();
        var command = new Command("users", "Write the users mapping template (YAML) of a package to standard output.") { package, stdin };
        command.SetAction(async (parse, ct) =>
        {
            var path = parse.GetValue(package)!;
            if (!File.Exists(path) && !Directory.Exists(path)) throw new CliException(ExitCodes.Usage, $"'{path}' does not exist.");
            var passphrase = await MigratePassphrase.ReadAsync(ctx, parse.GetValue(stdin), ct);
            try
            {
                using var reader = PackageReader.Open(path, passphrase);
                await UserMappingTemplate.WriteAsync(reader, ctx.Env.Out, ct);
                return ExitCodes.Ok;
            }
            catch (PackagePassphraseException e) { throw new CliException(ExitCodes.Auth, e.Message, e); }
            catch (PackageFormatException e) { throw new CliException(ExitCodes.PackageInvalid, e.Message, e); }
            catch (PackageIntegrityException e) { throw new CliException(ExitCodes.PackageInvalid, e.Message, e); }
        });
        return command;
    }

    private static List<string> List(string[]? values) =>
        [.. (values ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Unique name → file, read from each file's <c>solution.xml</c>.</summary>
    private static Dictionary<string, string> ManagedFiles(string[] files)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var full = Path.GetFullPath(file);
            if (!File.Exists(full)) throw new CliException(ExitCodes.Usage, $"'{file}' does not exist.");
            string? name;
            try
            {
                using var zip = ZipFile.OpenRead(full);
                using var s = (zip.GetEntry("solution.xml") ?? throw new InvalidDataException("solution.xml is missing")).Open();
                name = XDocument.Load(s).Root?.Element("SolutionManifest")?.Element("UniqueName")?.Value;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException)
            {
                throw new CliException(ExitCodes.Usage, $"'{file}' is not a solution file: {e.Message}", e);
            }
            if (string.IsNullOrEmpty(name)) throw new CliException(ExitCodes.Usage, $"'{file}' is not a solution file: no unique name.");
            result[name] = full;
        }
        return result;
    }

    private static void PrintExport(CliContext ctx, PackageManifest manifest, string folder, string? packFile)
    {
        if (ctx.Format == OutputFormat.Json)
        {
            ctx.Write(new
            {
                manifest.FormatVersion, manifest.CreatedOn, manifest.ToolVersion, manifest.Source, manifest.Solutions,
                manifest.Tables, manifest.Relationships, manifest.Warnings, folder, package = packFile,
            });
            return;
        }
        var o = ctx.Env.Out;
        o.WriteLine($"Exported {manifest.Tables.Count} tables, {manifest.Tables.Sum(t => t.Records)} records, " +
            $"{manifest.Relationships.Count} relationships, {manifest.Solutions.Count} solutions.");
        foreach (var t in manifest.Tables.OrderBy(t => t.LogicalName, StringComparer.Ordinal))
            o.WriteLine($"  {t.LogicalName,-40} {t.Records,10}");
        foreach (var w in manifest.Warnings) o.WriteLine($"Warning {w.Code}: {w.Message}");
        o.WriteLine($"Folder:  {folder}");
        if (packFile is not null) o.WriteLine($"Package: {packFile}");
    }

    /// <summary>Progress lines on stderr, written as they come (not through a synchronization context).</summary>
    private sealed class LineProgress(TextWriter err) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (err) err.WriteLine(value);
        }
    }

    private static void Print(CliContext ctx, PackageManifest manifest, PackageVerification? verification)
    {
        if (ctx.Format == OutputFormat.Json)
        {
            ctx.Write(new { manifest, verification });
            return;
        }
        var o = ctx.Env.Out;
        o.WriteLine($"Format version: {manifest.FormatVersion}");
        o.WriteLine($"Created on:     {manifest.CreatedOn:u}");
        o.WriteLine($"Tool version:   {manifest.ToolVersion}");
        o.WriteLine($"Source:         {manifest.Source.Url}");
        o.WriteLine();
        o.WriteLine($"Solutions ({manifest.Solutions.Count})");
        foreach (var s in manifest.Solutions.OrderBy(s => s.Order))
            o.WriteLine($"  {s.Order,3}  {s.UniqueName,-40} {s.Version,-14} {(s.Managed ? "managed" : "unmanaged")}");
        o.WriteLine();
        o.WriteLine($"Tables ({manifest.Tables.Count})              records  chunks");
        foreach (var t in manifest.Tables.OrderBy(t => t.LogicalName, StringComparer.Ordinal))
            o.WriteLine($"  {t.LogicalName,-30} {t.Records,10} {t.Chunks.Count,7}");
        o.WriteLine();
        o.WriteLine($"Relationships ({manifest.Relationships.Count})       pairs");
        foreach (var r in manifest.Relationships.OrderBy(r => r.SchemaName, StringComparer.Ordinal))
            o.WriteLine($"  {r.SchemaName,-30} {r.Pairs,10}");
        foreach (var w in manifest.Warnings) o.WriteLine($"Warning {w.Code}: {w.Message}");
        o.WriteLine();
        o.WriteLine(verification is null
            ? $"Entries: {manifest.Entries.Count} (not verified)"
            : $"Entries: {verification.EntriesChecked} checked, {verification.BytesChecked} bytes, {verification.Problems.Count} problems");
    }

    // ---- import on the server (plan M4b task 4) -------------------------------------------------------------------------

    private static Argument<string> RunIdArgument() => new("runId") { Description = "The id of the migration run." };

    private static Guid RunIdOf(string text) =>
        Guid.TryParse(text, out var id) ? id : throw new CliException(ExitCodes.Usage, $"'{text}' is not a run id (a GUID).");

    private static Command Import(CliContext ctx)
    {
        var package = new Argument<string?>("package") { Description = "The .xrmpkg file to upload (not with --server-path or --resume).", Arity = ArgumentArity.ZeroOrOne };
        var users = new Option<string?>("--users") { Description = "The users mapping: the YAML of 'xrmd migrate users', edited." };
        var strict = new Option<bool>("--strict") { Description = "Fail the schema phase on an item that cannot be created or is not supported." };
        var strictLookups = new Option<bool>("--strict-lookups") { Description = "Fail a row whose lookup points to a record that is not in the organization, instead of leaving it empty." };
        var currencyMap = new Option<bool>("--base-currency-map") { Description = "Map the source's base currency onto the organization's when their ISO codes differ." };
        var keep = new Option<bool>("--keep-package") { Description = "Keep the uploaded package on the server after the run." };
        var serverPath = new Option<string?>("--server-path") { Description = "A package already on the server, under its import folder: nothing is uploaded." };
        var noWait = new Option<bool>("--no-wait") { Description = "Start the run and return; follow it with 'xrmd migrate status <runId> --wait'." };
        var resume = new Option<string?>("--resume") { Description = "Resume this failed, cancelled or partly failed run from its first phase not completed." };
        var stdin = PassphraseStdin();
        var command = new Command("import", "Import a migration package into the organization: upload, start the run on the server and follow it.")
        {
            package, users, strict, strictLookups, currencyMap, keep, serverPath, noWait, resume, stdin,
        };
        command.SetAction(async (parse, ct) =>
        {
            var file = parse.GetValue(package);
            var onServer = parse.GetValue(serverPath);
            var resumeId = parse.GetValue(resume) is { } r ? RunIdOf(r) : (Guid?)null;
            if (resumeId is null && (file is null) == (onServer is null))
                throw new CliException(ExitCodes.Usage, "Give the package file, or --server-path for a package on the server, or --resume <runId>.");
            if (file is not null && !File.Exists(file)) throw new CliException(ExitCodes.Usage, $"'{file}' does not exist.");
            // Checked before anything is sent: a bad users file (exit 2), then the passphrase on the local package (exit 8).
            var usersJson = parse.GetValue(users) is { } yaml ? MigrateImport.UsersJson(yaml) : null;
            var passphrase = await MigratePassphrase.ReadAsync(ctx, parse.GetValue(stdin), ct);
            var client = ctx.CreateClient();

            Guid runId;
            if (resumeId is { } id)
            {
                await client.AdminAsync(HttpMethod.Post, $"migrate/runs/{id}/resume", Http.XrmdClient.JsonContent(new { passphrase }), ct);
                runId = id;
                await ctx.Env.Err.WriteLineAsync($"Run {runId} resumed.");
            }
            else
            {
                var options = MigrateImport.Options(parse.GetValue(strict), parse.GetValue(strictLookups), parse.GetValue(currencyMap), parse.GetValue(keep));
                if (file is not null) MigrateImport.CheckPackage(file, passphrase);
                runId = await MigrateImport.StartAsync(ctx, client, file, onServer, usersJson, options, passphrase, ct);
                await ctx.Env.Err.WriteLineAsync($"Run {runId} started.");
            }
            if (parse.GetValue(noWait))
            {
                ctx.WriteResult(new { runId }, $"Run {runId} started. Follow it with: xrmd migrate status {runId} --wait");
                return ExitCodes.Ok;
            }
            return await MigrateImport.WaitAsync(ctx, client, runId, ct);
        });
        return command;
    }

    private static Command Status(CliContext ctx)
    {
        var run = RunIdArgument();
        var wait = new Option<bool>("--wait") { Description = "Wait for the run's end, printing each phase; the exit code follows its final state." };
        var command = new Command("status", "Show a migration run: its state and the last phase completed.") { run, wait };
        command.SetAction(async (parse, ct) =>
        {
            var id = RunIdOf(parse.GetValue(run)!);
            var client = ctx.CreateClient();
            if (parse.GetValue(wait)) return await MigrateImport.WaitAsync(ctx, client, id, ct);
            ctx.Write((await client.AdminAsync(HttpMethod.Get, $"migrate/runs/{id}", cancellationToken: ct)).Json
                ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET /admin/migrate/runs/{id}."));
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Report(CliContext ctx)
    {
        var run = RunIdArgument();
        var format = new Option<string>("--format") { Description = "md (default), json, or csv (the issues).", DefaultValueFactory = _ => "md" };
        format.AcceptOnlyFromAmong("md", "json", "csv");
        var command = new Command("report", "Print the report of a migration run: tables, issues, lookups left empty, users, phases.") { run, format };
        command.SetAction(async (parse, ct) =>
        {
            var id = RunIdOf(parse.GetValue(run)!);
            var report = (await ctx.CreateClient().AdminAsync(HttpMethod.Get, $"migrate/runs/{id}/report", cancellationToken: ct)).Json
                ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET /admin/migrate/runs/{id}/report.");
            await ctx.Env.Out.WriteAsync(parse.GetValue(format) switch
            {
                "json" => JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
                "csv" => MigrateReportFormat.Csv(report),
                _ => MigrateReportFormat.Markdown(report),
            });
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Cancel(CliContext ctx)
    {
        var run = RunIdArgument();
        var command = new Command("cancel", "Cancel a queued or running migration run (the lock stays until it is resumed or finalized).") { run };
        command.SetAction(async (parse, ct) =>
        {
            var id = RunIdOf(parse.GetValue(run)!);
            ctx.Write((await ctx.CreateClient().AdminAsync(HttpMethod.Post, $"migrate/runs/{id}/cancel", Http.XrmdClient.JsonContent(new { }), ct)).Json
                ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for POST /admin/migrate/runs/{id}/cancel."));
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Finalize(CliContext ctx)
    {
        var run = RunIdArgument();
        var keep = new Option<bool>("--keep-package") { Description = "Keep the uploaded package on the server." };
        var command = new Command("finalize", "Accept the result of a failed, cancelled or partly failed run: the organization's lock lifts.") { run, keep };
        command.SetAction(async (parse, ct) =>
        {
            var id = RunIdOf(parse.GetValue(run)!);
            ctx.Write((await ctx.CreateClient().AdminAsync(HttpMethod.Post, $"migrate/runs/{id}/finalize",
                    Http.XrmdClient.JsonContent(new { keepPackage = parse.GetValue(keep) }), ct)).Json
                ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for POST /admin/migrate/runs/{id}/finalize."));
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Activate(CliContext ctx)
    {
        var run = RunIdArgument();
        var all = new Option<bool>("--all") { Description = "Switch on everything the import left off that was on in the source." };
        var ids = new Option<string[]>("--id") { Description = "Switch on these items only (step or process ids; comma-separated or repeated)." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Only list what would be switched on." };
        var command = new Command("activate", "Switch on the plug-in steps, webhook steps and processes a finalized import left off.") { run, all, ids, dryRun };
        command.SetAction(async (parse, ct) =>
        {
            var id = RunIdOf(parse.GetValue(run)!);
            var chosen = List(parse.GetValue(ids));
            if (parse.GetValue(all) == (chosen.Count > 0)) throw new CliException(ExitCodes.Usage, "Give either --all or --id <id>[,<id>…].");
            foreach (var item in chosen)
                if (!Guid.TryParse(item, out _)) throw new CliException(ExitCodes.Usage, $"'{item}' is not an id (a GUID).");
            var body = new JsonObject { ["dryRun"] = parse.GetValue(dryRun) };
            if (parse.GetValue(all)) body["all"] = true;
            else body["ids"] = new JsonArray([.. chosen.Select(c => (JsonNode)c)]);
            var result = (await ctx.CreateClient().AdminAsync(HttpMethod.Post, $"migrate/runs/{id}/activate",
                    new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct)).Json
                ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for POST /admin/migrate/runs/{id}/activate.");
            var items = result.GetProperty("items").EnumerateArray().ToList();
            var text = new System.Text.StringBuilder();
            foreach (var item in items)
            {
                var message = item.GetProperty("message").ValueKind == JsonValueKind.String ? $"  ({item.GetProperty("message").GetString()})" : "";
                text.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                    $"{item.GetProperty("action").GetString(),-15} {item.GetProperty("table").GetString(),-25} {item.GetProperty("name").GetString()}  {item.GetProperty("id").GetString()}{message}");
            }
            if (items.Count == 0) text.AppendLine("Nothing to switch on.");
            ctx.WriteResult(result, text.ToString().TrimEnd());
            return items.Any(i => i.GetProperty("action").GetString() == "failed") ? ExitCodes.Rejected : ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>
    /// <c>xrmd migrate run</c> (spec §9): <c>export</c> into a work folder, then <c>import</c> of the package it packs. The work folder
    /// stays when anything fails, so the same command resumes the export instead of reading Dataverse again; it is deleted after a run
    /// that completes, unless <c>--keep-export</c>.
    /// </summary>
    private static Command Run(CliContext ctx)
    {
        var args = new ExportArgs();
        var work = new Option<string?>("--work") { Description = "Folder of the export (default: a folder per source under the CLI's configuration folder)." };
        var keepExport = new Option<bool>("--keep-export") { Description = "Keep the export folder and package after a run that completes." };
        var users = new Option<string?>("--users") { Description = "The users mapping: the YAML of 'xrmd migrate users', edited." };
        var strict = new Option<bool>("--strict") { Description = "Stop before any solution is imported when the package has items xrmverse cannot import." };
        var strictLookups = new Option<bool>("--strict-lookups") { Description = "Fail a row whose lookup points to a record that is not in the organization, instead of leaving it empty." };
        var currencyMap = new Option<bool>("--base-currency-map") { Description = "Map the source's base currency onto the organization's when their ISO codes differ." };
        var keep = new Option<bool>("--keep-package") { Description = "Keep the uploaded package on the server after the run." };
        var noWait = new Option<bool>("--no-wait") { Description = "Start the run and return; follow it with 'xrmd migrate status <runId> --wait'." };
        var stdin = PassphraseStdin();
        var command = new Command("run", "Export a Dataverse environment and import it into the organization, in one command.");
        foreach (var option in args.All.Concat([work, keepExport, users, strict, strictLookups, currencyMap, keep, noWait, stdin])) command.Options.Add(option);
        command.SetAction(async (parse, ct) =>
        {
            // Everything that needs no network first: a bad users file or option never costs an export.
            var (sourceUri, options) = args.Check(parse);
            var usersJson = parse.GetValue(users) is { } yaml ? MigrateImport.UsersJson(yaml) : null;
            var passphrase = await MigratePassphrase.ReadAsync(ctx, parse.GetValue(stdin), ct);
            var client = ctx.CreateClient();
            var folder = Path.GetFullPath(parse.GetValue(work) ?? Path.Combine(ctx.Env.ConfigDir, "migrate-work", sourceUri.Host));
            var packFile = folder.TrimEnd(Path.DirectorySeparatorChar) + ".xrmpkg";
            if (File.Exists(packFile)) File.Delete(packFile);

            PackageManifest manifest;
            if (File.Exists(Path.Combine(folder, PackageWriter.ManifestFile)))
            {
                // A complete export from an earlier run whose import failed: imported again as it is, Dataverse is not read again.
                try
                {
                    using (var done = PackageReader.Open(folder, passphrase)) manifest = done.Manifest;
                }
                catch (PackagePassphraseException e) { throw new CliException(ExitCodes.Auth, e.Message, e); }
                await ctx.Env.Err.WriteLineAsync($"Using the complete export in {folder}");
                PackageWriter.Pack(folder, packFile);
            }
            else manifest = await args.RunAsync(ctx, parse, sourceUri, options, folder, packFile, passphrase, ct);
            await ctx.Env.Err.WriteLineAsync($"Exported {manifest.Tables.Count} tables, {manifest.Tables.Sum(t => t.Records)} records into {folder}.");
            var runId = await MigrateImport.StartAsync(ctx, client, packFile, null, usersJson,
                MigrateImport.Options(parse.GetValue(strict), parse.GetValue(strictLookups), parse.GetValue(currencyMap), parse.GetValue(keep)), passphrase, ct);
            await ctx.Env.Err.WriteLineAsync($"Run {runId} started.");
            if (parse.GetValue(noWait))
            {
                ctx.WriteResult(new { runId, folder, package = packFile },
                    $"Run {runId} started. Follow it with: xrmd migrate status {runId} --wait (the export stays in {folder}).");
                return ExitCodes.Ok;
            }
            var exit = await MigrateImport.WaitAsync(ctx, client, runId, ct);
            // Ctrl-C while waiting detaches (exit 0) from a run that goes on: its package may still be needed for a resume.
            if (exit == ExitCodes.Ok && !ct.IsCancellationRequested && !parse.GetValue(keepExport))
            {
                Directory.Delete(folder, recursive: true);
                File.Delete(packFile);
            }
            else await ctx.Env.Err.WriteLineAsync($"The export stays in {folder} ({packFile}).");
            return exit;
        });
        return command;
    }
}
