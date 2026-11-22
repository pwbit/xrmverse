using System.CommandLine;
using Xrmd.Abstractions.Sdk;
using Xrmd.Cli.Sdk;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd sdk install|status</c>: the customer's own copy of the Microsoft SDK assemblies (Plan 12), with the optional workflow
/// SDK that custom workflow activities need (plan 2.14, W18). Nothing is downloaded or extracted before the user has accepted
/// Microsoft's license terms (flag or prompt); one acceptance covers both packages.
/// </summary>
public static class SdkCommands
{
    public static Command Build(CliContext ctx)
    {
        var group = new Command("sdk", "Install and inspect the Microsoft SDK assemblies that xrmverse needs.");

        var dir = new Option<string?>("--dir") { Description = "An xrmd installation folder: installs into <dir>/sdk (mounted into the organization containers)." };
        var user = new Option<bool>("--user") { Description = "Install into the user folder (default ~/.xrmd/sdk, or XRMD_SDK_DIR); the default without --dir." };
        var from = new Option<string?>("--from") { Description = $"Install {SdkPackage.Id} from a local .nupkg file instead of downloading from nuget.org." };
        var fromWorkflow = new Option<string?>("--from-workflow")
        {
            Description = $"Install {SdkPackage.Workflow.Id} (custom workflow activities) from a local .nupkg file. Without --from and --from-workflow, both packages are downloaded; with --from alone, the workflow SDK is not installed.",
        };
        var accept = new Option<bool>("--accept-microsoft-license")
        {
            Description = $"Accept Microsoft's license terms ({SdkPackage.LicenseUrl}) for {Packages(withWorkflow: true)} without the prompt; required when stdin is not a terminal.",
        };
        var install = new Command("install",
            $"Install {SdkPackage.Id} {SdkPackage.Version} and {SdkPackage.Workflow.Id} {SdkPackage.Workflow.Version} after you accept Microsoft's license terms.")
        {
            dir, user, from, fromWorkflow, accept,
        };
        install.SetAction(async (parse, ct) =>
        {
            var dirValue = parse.GetValue(dir);
            var targets = new List<string>();
            if (dirValue is not null) targets.Add(Path.Combine(Path.GetFullPath(dirValue), "sdk"));
            if (dirValue is null || parse.GetValue(user)) targets.Add(UserFolder(ctx));
            var fromValue = parse.GetValue(from);
            if (fromValue is not null && !File.Exists(fromValue))
                throw new CliException(ExitCodes.Usage, $"File not found: '{fromValue}'.");
            var fromWorkflowValue = parse.GetValue(fromWorkflow);
            if (fromWorkflowValue is not null && !File.Exists(fromWorkflowValue))
                throw new CliException(ExitCodes.Usage, $"File not found: '{fromWorkflowValue}'.");

            // --from alone stays offline and installs without the workflow SDK, so the acceptance names only the Dataverse SDK.
            var withWorkflow = fromValue is null || fromWorkflowValue is not null;
            var how = await AcceptAsync(ctx, parse.GetValue(accept), withWorkflow);
            await InstallAsync(ctx, targets, fromValue, fromWorkflowValue, how, ct);
            return ExitCodes.Ok;
        });

        var statusDir = new Option<string?>("--dir") { Description = "An xrmd installation folder: looks in <dir>/sdk." };
        var status = new Command("status",
            "Show the installed SDK version, the workflow SDK (or missing), the folder and the license acceptance record.") { statusDir };
        status.SetAction(parse =>
        {
            var dirValue = parse.GetValue(statusDir);
            var path = dirValue is not null ? Path.Combine(Path.GetFullPath(dirValue), "sdk") : UserFolder(ctx);
            var record = SdkFolder.TryRead(path);
            if (record is null) throw new CliException(ExitCodes.Unexpected, SdkPackage.MissingMessage(path));
            ctx.Write(new
            {
                package = record.Package,
                version = record.Version,
                path,
                sha512 = record.Sha512,
                licenseUrl = record.LicenseUrl,
                acceptedAt = record.AcceptedAt,
                acceptedBy = record.AcceptedBy,
                workflowSdk = record.WorkflowVersion is null ? "missing (run 'xrmd sdk install')" : $"{record.WorkflowPackage} {record.WorkflowVersion}",
                workflowVersion = record.WorkflowVersion,
                workflowSha512 = record.WorkflowSha512,
            });
            return ExitCodes.Ok;
        });

        group.Add(install);
        group.Add(status);
        return group;
    }

    private static string UserFolder(CliContext ctx) =>
        SdkFolder.Resolve(null, ctx.Env.GetEnvironmentVariable, SdkFolder.DefaultPath);

    /// <summary>
    /// Gets the packages and installs them into each folder; the user has accepted already. The Dataverse SDK comes from
    /// <paramref name="from"/>, else nuget.org. The workflow SDK comes from <paramref name="fromWorkflow"/>, else from nuget.org
    /// when <paramref name="from"/> is not given either (an offline install with <c>--from</c> alone stays offline and installs
    /// without it).
    /// </summary>
    internal static async Task InstallAsync(CliContext ctx, IEnumerable<string> targets, string? from, string? fromWorkflow, AcceptanceKind how,
        CancellationToken ct)
    {
        byte[] bytes;
        if (from is not null) bytes = await File.ReadAllBytesAsync(from, ct);
        else
        {
            await ctx.Env.Err.WriteLineAsync($"Downloading {SdkPackage.Id} {SdkPackage.Version} from nuget.org...");
            bytes = await DownloadAsync(ctx, SdkPackage.DownloadUrl, ct);
        }

        byte[]? workflow = null;
        if (fromWorkflow is not null) workflow = await File.ReadAllBytesAsync(fromWorkflow, ct);
        else if (from is null)
        {
            await ctx.Env.Err.WriteLineAsync($"Downloading {SdkPackage.Workflow.Id} {SdkPackage.Workflow.Version} from nuget.org...");
            workflow = await DownloadAsync(ctx, SdkPackage.Workflow.DownloadUrl, ct);
        }

        foreach (var target in targets.Distinct())
        {
            var hadWorkflow = workflow is null && HasWorkflowSdk(target);
            await SdkInstaller.InstallAsync(new MemoryStream(bytes), workflow is null ? null : new MemoryStream(workflow), target, how, ct, ctx.Env.Clock);
            var installed = workflow is null
                ? $"{SdkPackage.Id} {SdkPackage.Version}"
                : $"{SdkPackage.Id} {SdkPackage.Version} and {SdkPackage.Workflow.Id} {SdkPackage.Workflow.Version}";
            ctx.WriteResult(new
                {
                    installed = true, package = SdkPackage.Id, version = SdkPackage.Version, path = target, acceptedBy = how == AcceptanceKind.Flag ? "flag" : "prompt",
                    workflowPackage = workflow is null ? null : SdkPackage.Workflow.Id, workflowVersion = workflow is null ? null : SdkPackage.Workflow.Version,
                },
                $"Installed {installed} in {target}.");
            if (workflow is null)
            {
                var what = hadWorkflow ? $"{SdkPackage.Workflow.Id} was removed from {target}" : $"{SdkPackage.Workflow.Id} is not installed in {target}";
                await ctx.Env.Err.WriteLineAsync(
                    $"Note: {what}: custom workflow activities need it. Install it with --from-workflow <file>, or run 'xrmd sdk install' without --from to download both packages.");
            }
        }
    }

    /// <summary>Whether <paramref name="target"/> holds a workflow SDK now (recorded in sdk.json or present as a file).</summary>
    private static bool HasWorkflowSdk(string target)
    {
        try
        {
            return SdkFolder.TryRead(target)?.WorkflowVersion is not null
                   || File.Exists(Path.Combine(target, SdkPackage.Workflow.AssemblyFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>The Microsoft packages an acceptance covers, with their versions.</summary>
    internal static string Packages(bool withWorkflow) =>
        withWorkflow
            ? $"{SdkPackage.Id} {SdkPackage.Version} and {SdkPackage.Workflow.Id} {SdkPackage.Workflow.Version}"
            : $"{SdkPackage.Id} {SdkPackage.Version}";

    /// <summary>
    /// The explicit acceptance of Microsoft's terms: the flag, or a prompt whose default is no. Runs before any download. The
    /// prompt names every package it covers: the Dataverse SDK, and the workflow SDK when <paramref name="withWorkflow"/>.
    /// </summary>
    internal static async Task<AcceptanceKind> AcceptAsync(CliContext ctx, bool flag, bool withWorkflow = true)
    {
        if (flag) return AcceptanceKind.Flag;
        var packages = Packages(withWorkflow);
        if (!ctx.Env.IsInputTty)
            throw new CliException(ExitCodes.Usage,
                $"Installing the Microsoft SDK ({packages}) requires accepting Microsoft's license terms ({SdkPackage.LicenseUrl}). Pass --accept-microsoft-license, or run in a terminal to be asked.");
        var verb = withWorkflow ? "are" : "is";
        await ctx.Env.Err.WriteLineAsync($"{packages} {verb} licensed by Microsoft. Read the terms at {SdkPackage.LicenseUrl}.");
        await ctx.Env.Err.WriteAsync("Do you accept Microsoft's license terms? [y/N] ");
        await ctx.Env.Err.FlushAsync();
        var answer = (await ctx.Env.In.ReadLineAsync())?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            throw new CliException(ExitCodes.Unexpected, "Aborted: Microsoft's license terms were not accepted. Nothing was installed.");
        return AcceptanceKind.Prompt;
    }

    private static async Task<byte[]> DownloadAsync(CliContext ctx, string url, CancellationToken ct)
    {
        try
        {
            using var response = await ctx.Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                throw new CliException(ExitCodes.Unexpected, $"Downloading the package failed: HTTP {(int)response.StatusCode} from {url}.");
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CliException(ExitCodes.Unreachable, "Cannot reach nuget.org: the request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new CliException(ExitCodes.Unreachable, $"Cannot reach nuget.org: {ex.Message}", ex);
        }
    }
}
