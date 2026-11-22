using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xrmd.Abstractions.Sdk;
using Xrmd.Cli;
using Xrmd.Cli.Auth;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The plan 9 model (ruling M9) shared by the multi-organization acceptance tests: the real CLI in-process, the real
/// <c>docker compose config</c> of the generated installation, and one <c>Xrmd.Host</c> process per <c>xrmd-&lt;org&gt;</c>
/// service with that service's resolved environment.
/// </summary>
internal static class MultiOrgHarness
{
    /// <summary>The <c>.env</c> of an installation (single-quoted values unquoted).</summary>
    public static Dictionary<string, string> ReadEnv(string installDir) =>
        File.ReadAllLines(Path.Combine(installDir, ".env"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1].Length >= 2 && p[1][0] == '\'' && p[1][^1] == '\'' ? p[1][1..^1] : p[1]);

    /// <summary>The services of <c>docker compose config --format json</c> of the installation (the real Compose, with interpolation).</summary>
    public static async Task<Dictionary<string, JsonElement>> ResolvedServicesAsync(string installDir)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = installDir };
        // The arguments of ServerCommands.ComposeArguments: the generated files, explicitly.
        foreach (var arg in new[] { "compose", "-f", Path.Combine(installDir, "docker-compose.yml"), "--env-file", Path.Combine(installDir, ".env"), "config", "--format", "json" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, await stderr);
        using var doc = JsonDocument.Parse(await stdout);
        return doc.RootElement.GetProperty("services").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path && File.Exists(path) ? path : "dotnet";

    /// <summary>
    /// Starts <c>Xrmd.Host</c> from <paramref name="hostFolder"/> with the resolved <c>environment</c> of a compose
    /// <paramref name="service"/>, then what the container runtime would provide: <c>Server=</c> of
    /// <c>ConnectionStrings__Org</c> is <paramref name="sqlDataSource"/>, <c>ASPNETCORE_URLS</c> is <paramref name="listenUrl"/>,
    /// <c>Files__FileSystem__Root</c> is <paramref name="filesRoot"/> and <c>XRMD_SDK_DIR</c> is <paramref name="sdk"/>.
    /// <paramref name="overrides"/> replace further variables (each caller documents why).
    /// </summary>
    public static HostProcess StartHost(string name, int port, JsonElement service, string hostFolder, string sqlDataSource,
        string listenUrl, string filesRoot, string sdk, ITestOutputHelper output, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var psi = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = hostFolder,
        };
        psi.ArgumentList.Add(Path.Combine(hostFolder, "Xrmd.Host.dll"));
        // Only the service's environment (plus what the runtime needs): nothing of the test runner's configuration leaks in.
        foreach (var key in psi.Environment.Keys.ToList())
            if (key.StartsWith("ASPNETCORE_", StringComparison.Ordinal) || key.StartsWith("DOTNET_", StringComparison.Ordinal) || key.Contains("__"))
                psi.Environment.Remove(key);
        foreach (var variable in service.GetProperty("environment").EnumerateObject())
            psi.Environment[variable.Name] = variable.Value.GetString()!.Replace("$$", "$"); // `config` escapes a literal $ as $$

        var connection = new SqlConnectionStringBuilder(psi.Environment["ConnectionStrings__Org"]) { DataSource = sqlDataSource };
        psi.Environment["ConnectionStrings__Org"] = connection.ConnectionString;
        psi.Environment["ASPNETCORE_URLS"] = listenUrl;
        psi.Environment["Files__FileSystem__Root"] = filesRoot;
        psi.Environment[SdkFolder.DirectoryVariable] = sdk; // what the image's bind mount of <installation>/sdk provides
        foreach (var (key, value) in overrides ?? new Dictionary<string, string>())
            psi.Environment[key] = value;

        return new HostProcess(name, port, Process.Start(psi)!, output);
    }
}

/// <summary>The real CLI in-process, on the real network, with a private config dir.</summary>
internal sealed class MultiOrgCli(string configDir)
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public string? SigningKey { get; set; }
    public IProcessRunner Process { get; set; } = new RecordingProcessRunner();
    public string Out => _out.ToString();
    public string Err => _err.ToString();

    public CliEnvironment Env() =>
        new(_out, _err, new StringReader(""), false, null, configDir, Process, TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == DevTokenProvider.DefaultSigningKeyEnv ? SigningKey : null,
        };

    public Task<int> RunAsync(params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        return CliApp.RunAsync(args, Env());
    }
}

/// <summary>Stands in for <c>docker</c>: records the calls and succeeds without running anything.</summary>
internal sealed class RecordingProcessRunner : IProcessRunner
{
    public List<ProcessSpec> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        Calls.Add(spec);
        return Task.FromResult(new ProcessResult(0, "", ""));
    }
}

/// <summary>Polls <c>/healthz</c> URLs until stopped, recording every answer that is not 200.</summary>
internal sealed class HealthWatch : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _failures = new();
    private readonly Task _loop;
    private int _probes;

    public HealthWatch(IReadOnlyList<(string Name, HttpClient Http, string Url)> targets)
    {
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                foreach (var (name, http, url) in targets)
                {
                    try
                    {
                        var status = (await http.GetAsync(url, _stop.Token)).StatusCode;
                        if (status != HttpStatusCode.OK) _failures.Enqueue($"{name}: {(int)status}");
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _failures.Enqueue($"{name}: {ex.Message}");
                    }
                    Interlocked.Increment(ref _probes);
                }
                try { await Task.Delay(200, _stop.Token); } catch (OperationCanceledException) { return; }
            }
        });
    }

    public int Probes => Volatile.Read(ref _probes);

    public async Task<List<string>> StopAsync()
    {
        _stop.Cancel();
        await _loop;
        return [.. _failures];
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}

/// <summary>One <c>Xrmd.Host</c> process, its combined output, killed on dispose.</summary>
internal sealed class HostProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    public HostProcess(string name, int port, Process process, ITestOutputHelper log)
    {
        Name = name;
        Port = port;
        _process = process;
        StartTime = process.StartTime;
        process.OutputDataReceived += (_, e) => Append(e.Data, log);
        process.ErrorDataReceived += (_, e) => Append(e.Data, log);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public string Name { get; }
    public int Port { get; }
    public int Id => _process.Id;
    public DateTime StartTime { get; }
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    public string Output
    {
        get { lock (_output) return _output.ToString(); }
    }

    private void Append(string? line, ITestOutputHelper log)
    {
        if (line is null) return;
        lock (_output) _output.AppendLine(line);
        try { log.WriteLine($"[{Name}] {line}"); } catch (InvalidOperationException) { } // after the test ended
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException) { }
        _process.Dispose();
    }
}
