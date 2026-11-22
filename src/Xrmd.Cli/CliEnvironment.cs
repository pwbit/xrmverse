using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Xrmd.Cli;

/// <summary>
/// Everything the CLI takes from the outside world, so tests run in-process: streams, whether stdin is a
/// terminal, the HTTP handler (null = real network), the config dir (<c>~/.xrmd</c>), processes and the clock.
/// </summary>
public sealed record CliEnvironment(
    TextWriter Out,
    TextWriter Err,
    TextReader In,
    bool IsInputTty,
    HttpMessageHandler? Handler,
    string ConfigDir,
    IProcessRunner Process,
    TimeProvider Clock)
{
    public const string ConfigDirVariable = "XRMD_CONFIG_DIR";

    /// <summary>Environment variable lookup (signing key, client secret); replaceable in tests.</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The CLI's own version (null = unknown, a development build); <c>server init</c> pins the image to it. Replaceable in tests.</summary>
    public string? CliVersion { get; init; } = Xrmd.Cli.Server.ServerImage.CliVersion();

    /// <summary>Raw stdin of <c>xrmd mcp</c> (the MCP channel); null = the process's standard input. Replaceable in tests.</summary>
    public Stream? McpInput { get; init; }

    /// <summary>Raw stdout of <c>xrmd mcp</c> (the MCP channel); null = the process's standard output. Replaceable in tests.</summary>
    public Stream? McpOutput { get; init; }

    /// <summary>How long <c>xrmd mcp</c> waits for the remote server at startup; null = 30 seconds. Replaceable in tests.</summary>
    public TimeSpan? McpStartupTimeout { get; init; }

    /// <summary>How long one organization's probe (<c>server status|doctor|upgrade</c>) may take; null = 15 seconds. Replaceable in tests.</summary>
    public TimeSpan? OrgProbeTimeout { get; init; }

    /// <summary>Host name resolution of <c>server doctor</c>'s <c>dns</c> and <c>certificate</c> checks. Replaceable in tests.</summary>
    public Func<string, CancellationToken, Task<System.Net.IPAddress[]>> ResolveHost { get; init; } =
        (host, ct) => System.Net.Dns.GetHostAddressesAsync(host, ct);

    /// <summary>The port of <c>server doctor</c>'s <c>certificate</c> check; null = 443. Replaceable in tests.</summary>
    public int? TlsCheckPort { get; init; }

    /// <summary>How long <c>server up</c> retries the export of Caddy's root certificate; null = 10 seconds. Replaceable in tests.</summary>
    public TimeSpan? CaddyRootTimeout { get; init; }

    /// <summary>
    /// Test seam: the bearer token of <c>migrate export</c> for the source environment, used once the sign-in options are
    /// validated; null = Microsoft Entra ID through MSAL.
    /// </summary>
    public Func<Uri, CancellationToken, Task<string>>? SourceToken { get; init; }

    /// <summary>Test seam: runs before the RESTORE of <c>org restore --direct</c>, with the open master connection it runs on.</summary>
    public Func<System.Data.Common.DbConnection, CancellationToken, Task>? BeforeDirectRestore { get; init; }

    /// <summary>The real console, network, processes and clock; config dir from <c>XRMD_CONFIG_DIR</c> or <c>~/.xrmd</c>.</summary>
    public static CliEnvironment CreateDefault()
    {
        var dir = Environment.GetEnvironmentVariable(ConfigDirVariable);
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xrmd");
        return new CliEnvironment(Console.Out, Console.Error, Console.In, !Console.IsInputRedirected, null, dir,
            new SystemProcessRunner(), TimeProvider.System);
    }
}

/// <summary>Runs external programs (e.g. <c>docker compose</c>); faked in tests.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default);
}

/// <summary>
/// A program to run. Output lines go to <see cref="OnOutputLine"/>/<see cref="OnErrorLine"/> as they arrive
/// (for streaming) and are also collected in <see cref="ProcessResult"/>.
/// </summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    public string? StandardInput { get; init; }
    public Action<string>? OnOutputLine { get; init; }
    public Action<string>? OnErrorLine { get; init; }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>The executable could not be started (not installed or not on PATH).</summary>
public sealed class ProcessNotFoundException(string fileName, Exception? inner = null)
    : Exception($"Cannot run '{fileName}': executable not found.", inner)
{
    public string FileName { get; } = fileName;
}

/// <summary><see cref="IProcessRunner"/> over <see cref="Process"/>; the process tree is killed on cancellation.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.StandardInput is not null,
            UseShellExecute = false,
        };
        foreach (var arg in spec.Arguments) psi.ArgumentList.Add(arg);
        if (spec.WorkingDirectory is not null) psi.WorkingDirectory = spec.WorkingDirectory;
        if (spec.Environment is not null)
            foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (stdout) stdout.AppendLine(e.Data); spec.OnOutputLine?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (stderr) stderr.AppendLine(e.Data); spec.OnErrorLine?.Invoke(e.Data); };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ProcessNotFoundException(spec.FileName, ex);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (spec.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(spec.StandardInput);
            process.StandardInput.Close();
        }
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        process.WaitForExit(); // flushes the asynchronous output readers
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
