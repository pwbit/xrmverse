using System.CommandLine;
using System.Net;
using System.Text;
using Xrmd.Cli;

namespace Xrmd.Cli.Tests;

/// <summary>Records requests and answers them with a delegate; never touches the network.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Request bodies, read at send time (the client disposes requests afterwards).</summary>
    public List<string?> Bodies { get; } = [];

    /// <summary>Simulated server latency.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>Requests for which the server never answers: they end only when cancelled.</summary>
    public Func<HttpRequestMessage, bool>? Hang { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        // The CLI sends in parallel (data import, dev seed): List<T> is not thread safe, so record under a lock.
        lock (Requests)
        {
            Requests.Add(request);
            Bodies.Add(body);
        }
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (Hang?.Invoke(request) == true) await Task.Delay(Timeout.Infinite, cancellationToken);
        return respond(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>One in-process CLI environment with a private config dir, captured output and fake env vars.</summary>
internal sealed class TestCli : IDisposable
{
    public const string SigningKey = "test-signing-key-0123456789-abcdefghijklmnop";

    public StringWriter Out { get; } = new();
    public StringWriter Err { get; } = new();
    public string ConfigDir { get; } = Path.Combine(Path.GetTempPath(), "xrmd-cli-tests", Guid.NewGuid().ToString("N"));
    /// <summary>
    /// Fake env vars. <c>XRMD_SDK_DIR</c> points into the private config dir: the CLI's default SDK folder is the real
    /// <c>~/.xrmd/sdk</c>, independent of the config dir, and a test must never write there.
    /// </summary>
    public Dictionary<string, string?> Vars { get; }
    public FakeHandler Handler { get; set; }
    public bool IsInputTty { get; set; }

    /// <summary>
    /// <c>server init</c> arguments of a test that is not about issue #16: with the bundled SQL Server, the user's acceptance of
    /// its license terms (<c>--accept-sql-server-eula</c>); with <c>--sql-connection</c>, unchanged.
    /// </summary>
    public static string[] Eula(string[] args) => args.Contains("--sql-connection") || args.Contains("postgresql") ? args : [.. args, "--accept-sql-server-eula"];
    public IProcessRunner? Process { get; set; }
    public string Input { get; set; } = "";
    public TimeProvider Clock { get; set; } = new FixedClock(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
    public TimeSpan? OrgProbeTimeout { get; set; }
    /// <summary>Host name resolution of <c>server doctor</c>; null = the real DNS.</summary>
    public Func<string, CancellationToken, Task<IPAddress[]>>? ResolveHost { get; set; }
    /// <summary>The port of <c>server doctor</c>'s certificate check; null = 443.</summary>
    public int? TlsCheckPort { get; set; }
    /// <summary>How long <c>server up</c> retries the export of Caddy's root; null = 10 seconds.</summary>
    public TimeSpan? CaddyRootTimeout { get; set; }
    /// <summary>The CLI's own version; null (a dev build) makes <c>server init</c> write the local image.</summary>
    public string? CliVersion { get; set; }
    /// <summary>The source token of <c>migrate export</c>; null = MSAL.</summary>
    public Func<Uri, CancellationToken, Task<string>>? SourceToken { get; set; }

    public TestCli(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        Handler = new FakeHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Vars = new() { ["XRMD_DEV_SIGNING_KEY"] = SigningKey, ["XRMD_SDK_DIR"] = Path.Combine(ConfigDir, "sdk") };
    }

    public CliEnvironment Env() =>
        new(Out, Err, new StringReader(Input), IsInputTty, Handler, ConfigDir, Process ?? new NoProcess(), Clock)
        {
            GetEnvironmentVariable = name => Vars.TryGetValue(name, out var v) ? v : null,
            OrgProbeTimeout = OrgProbeTimeout,
            CliVersion = CliVersion,
            ResolveHost = ResolveHost ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct)),
            TlsCheckPort = TlsCheckPort,
            CaddyRootTimeout = CaddyRootTimeout,
            SourceToken = SourceToken,
        };

    public async Task<int> RunAsync(params string[] args)
    {
        Out.GetStringBuilder().Clear();
        Err.GetStringBuilder().Clear();
        return await CliApp.RunAsync(args, Env());
    }

    public async Task<int> RunWithAsync(Func<CliContext, Command> group, params string[] args)
    {
        Out.GetStringBuilder().Clear();
        Err.GetStringBuilder().Clear();
        return await CliApp.RunAsync(args, Env(), [group]);
    }

    public Task<int> LoginDevAsync(string url = "http://xrmd.test:5000") =>
        RunAsync("auth", "login", "--url", url, "--mode", "dev");

    public void Dispose()
    {
        try { Directory.Delete(ConfigDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No process may run in unit tests.");
    }
}

/// <summary>Records the processes a command runs and answers them without running anything.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessSpec> Calls { get; } = [];
    public Exception? Throw { get; init; }
    public ProcessResult Result { get; init; } = new(0, "", "");
    public string[] OutputLines { get; init; } = [];
    /// <summary>The answer to one call, by its arguments; null falls back to <see cref="Result"/>.</summary>
    public Func<ProcessSpec, ProcessResult?>? Respond { get; init; }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        Calls.Add(spec);
        if (Throw is not null) throw Throw;
        foreach (var line in OutputLines) spec.OnOutputLine?.Invoke(line);
        return Task.FromResult(Respond?.Invoke(spec) ?? Result);
    }
}

/// <summary>An SDK folder record without the assemblies, for tests of commands that only check that the SDK is installed.</summary>
internal static class FakeSdk
{
    public static void Install(string installationDir)
    {
        var folder = Path.Combine(installationDir, "sdk");
        Directory.CreateDirectory(folder);
        Xrmd.Abstractions.Sdk.SdkFolder.Write(folder, new Xrmd.Abstractions.Sdk.SdkRecord(
            Xrmd.Abstractions.Sdk.SdkPackage.Id, Xrmd.Abstractions.Sdk.SdkPackage.Version, Xrmd.Abstractions.Sdk.SdkPackage.Sha512Base64,
            Xrmd.Abstractions.Sdk.SdkPackage.LicenseUrl, new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero), "flag"));
    }
}
