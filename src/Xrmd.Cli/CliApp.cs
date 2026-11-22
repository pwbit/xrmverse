using System.CommandLine;
using Xrmd.Abstractions.Sdk;
using Xrmd.Cli.Commands;

namespace Xrmd.Cli;

/// <summary>
/// Entry point of <c>xrmd</c>.
/// <para>
/// Adding a command group: create <c>Commands/XxxCommands.cs</c> with
/// <c>public static Command Build(CliContext ctx)</c> and add <c>XxxCommands.Build</c> to <see cref="Groups"/>.
/// Actions use <c>SetAction(async (parse, ct) =&gt; ...)</c> returning an exit code; they get clients from
/// <see cref="CliContext.CreateClient"/> / <see cref="CliContext.CreateAnonymousClient"/>, print results with
/// <see cref="CliContext.Write"/> (stdout, honours <c>--output</c>), diagnostics on <c>ctx.Env.Err</c>, ask
/// with <see cref="CliContext.ConfirmDestructiveAsync"/>, and fail by throwing <see cref="CliException"/>
/// (HTTP failures already are). <c>--output</c>, <c>--url</c> and <c>--profile</c> are recursive root options:
/// groups must not declare them again.
/// </para>
/// </summary>
public static class CliApp
{
    private static readonly Func<CliContext, Command>[] Groups =
    [
        AuthCommands.Build,
        IdpCommands.Build,
        ServerCommands.Build,
        OrgCommands.Build,
        AuditCommands.Build,
        CustomApiCommands.Build,
        WorkflowCommands.Build,
        UserCommands.Build,
        RoleCommands.Build,
        FieldSecurityCommands.Build,
        HierarchySecurityCommands.Build,
        AccessTeamCommands.Build,
        CurrencyCommands.Build,
        EnvVarCommands.Build,
        ChangeTrackingCommands.Build,
        DuplicateDetectionCommands.Build,
        PublisherCommands.Build,
        LogsCommands.Build,
        TableCommands.Build,
        SolutionCommands.Build,
        PluginCommands.Build,
        WebhookCommands.Build,
        DataCommands.Build,
        DevCommands.Build,
        ApplyCommands.Build,
        McpCommands.Build,
        SdkCommands.Build,
        MigrateCommands.Build,
    ];

    public static Task<int> RunAsync(string[] args, CliEnvironment env) => RunAsync(args, env, []);

    /// <summary>As <see cref="RunAsync(string[], CliEnvironment)"/> with extra groups (tests).</summary>
    internal static async Task<int> RunAsync(string[] args, CliEnvironment env, IEnumerable<Func<CliContext, Command>> extraGroups)
    {
        // The Microsoft SDK is not shipped with the CLI (Plan 12, B5): the commands that use it load it from the SDK folder.
        // Nothing is loaded here, so the commands that do not use it work without it.
        var sdkFolder = SdkFolder.Resolve(null, env.GetEnvironmentVariable, SdkFolder.DefaultPath);
        SdkAssemblyResolver.Install(sdkFolder);

        using var ctx = new CliContext(env);
        var root = new RootCommand("xrmd: administer an xrmverse server.")
        {
            ctx.OutputOption,
            ctx.UrlOption,
            ctx.ProfileOption,
        };
        foreach (var build in Groups.Concat(extraGroups)) root.Add(build(ctx));

        // "@file" is an argument value (data query --fetch @file), not a response file.
        var parse = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        ctx.ParseResult = parse;
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors) await env.Err.WriteLineAsync(error.Message);
            await env.Err.WriteLineAsync("Run 'xrmd --help' for usage.");
            return ExitCodes.Usage;
        }

        try
        {
            return await parse.InvokeAsync(new InvocationConfiguration
            {
                Output = env.Out,
                Error = env.Err,
                EnableDefaultExceptionHandler = false,
            });
        }
        catch (Exception ex) when (SdkAssemblyResolver.MissingAssembly(ex) is not null)
        {
            await env.Err.WriteLineAsync(SdkPackage.MissingMessage(sdkFolder));
            return ExitCodes.Unexpected;
        }
        catch (CliException ex)
        {
            await env.Err.WriteLineAsync(ex.Message);
            return ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            await env.Err.WriteLineAsync("Cancelled.");
            return ExitCodes.Unexpected;
        }
        catch (Exception ex)
        {
            await env.Err.WriteLineAsync($"Unexpected error: {ex.Message}");
            return ExitCodes.Unexpected;
        }
    }
}
