namespace Xrmd.Cli.Commands;

/// <summary>
/// The passphrase of a migration package: <c>XRMD_MIGRATE_PASSPHRASE</c>, else one line on standard input with
/// <c>--passphrase-stdin</c>. Never a command-line argument, never logged.
/// </summary>
internal static class MigratePassphrase
{
    public const string Variable = "XRMD_MIGRATE_PASSPHRASE";

    public static async Task<string> ReadAsync(CliContext ctx, bool fromStdin, CancellationToken ct)
    {
        if (ctx.Env.GetEnvironmentVariable(Variable) is { Length: > 0 } fromEnv) return fromEnv;
        if (!fromStdin)
            throw new CliException(ExitCodes.Usage, $"Set {Variable} or pass --passphrase-stdin: a passphrase is never a command-line argument.");
        var line = (await ctx.Env.In.ReadLineAsync(ct))?.TrimEnd('\r');
        if (string.IsNullOrEmpty(line)) throw new CliException(ExitCodes.Usage, "No passphrase on standard input.");
        return line;
    }
}
