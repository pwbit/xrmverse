using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xrmd.Abstractions.Sdk;
using Xrmd.TestSupport;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests.SdkFree;

/// <summary>
/// Plan S.2a, Task 6: the SDK-free probe. <see cref="SdkFreeTour"/> runs on a published host in mode <c>standard</c> (every step
/// must pass, so the probe cannot pass on a broken tour) and in mode <c>none</c> with an empty SDK folder, whose failing steps must be
/// exactly <c>tests/fixtures/sdk-free-baseline.json</c>; the published <c>Xrmd.*</c> assemblies that reference an SDK assembly must be
/// exactly <c>tests/fixtures/sdk-free-assemblies.json</c>. Both baselines only shrink: later plans remove names, never add them.
/// </summary>
[Collection("xrmd")]
public sealed class SdkFreeProbeTests(XrmdFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private const string StepBaseline = "tests/fixtures/sdk-free-baseline.json";
    private const string AssemblyBaseline = "tests/fixtures/sdk-free-assemblies.json";

    private readonly List<RunningProcess> _hosts = [];
    private readonly List<string> _databases = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts) host.Dispose();
        foreach (var database in _databases)
        {
            // One failed drop must not leave the other databases behind.
            try { await TestProvider.DropDatabaseAsync(fx.ConnectionString, database); }
            catch (Exception ex) { Echo($"Could not drop the database {database}: {ex.Message}"); }
        }
    }

    [Fact]
    public async Task Tour_passes_on_a_standard_server()
    {
        using var ctx = await StartAsync(await PublishedApps.InstalledSdkAsync(withWorkflow: true), "standard");

        var results = await RunTourAsync(ctx);

        var failed = results.Where(r => r.Error is not null).ToList();
        if (failed.Count > 0)
            throw new Xunit.Sdk.XunitException("The tour must pass on a standard server; failed steps:\n"
                + string.Join("\n\n", failed.Select(f => $"{f.Name}: {f.Error}")) + "\n\nHost output (tail):\n" + Tail(ctx.Host.Output));
        ctx.Host.Output.Should().NotContain("sdk-free:");
    }

    [Fact]
    public async Task Sdk_free_server_matches_the_baseline()
    {
        using var ctx = await StartAsync(PublishedApps.NewFolder("sdk-free-empty"), "none");

        var results = await RunTourAsync(ctx);

        var failing = results.Where(r => r.Error is not null).Select(r => r.Name).ToList();
        var resolver = ctx.Host.Output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains("sdk-free:", StringComparison.Ordinal));
        var problems = BaselineProblems(failing, ReadBaseline(StepBaseline, "needsSdk"), StepBaseline);
        if (problems.Count > 0)
            throw new Xunit.Sdk.XunitException(string.Join("\n", problems)
                + "\n\nFailing steps: " + JsonArrayOf(failing)
                + "\n\nErrors:\n" + string.Join("\n", results.Where(r => r.Error is not null).Select(r => $"{r.Name}: {FirstLine(r.Error!)}"))
                + "\n\nSdkFreeResolver lines of the host:\n" + string.Join("\n", resolver));
    }

    [Fact]
    public async Task Published_assemblies_that_reference_the_sdk_match_the_baseline()
    {
        var folder = await PublishedApps.HostAsync();
        var referencing = new List<string>();
        foreach (var path in Directory.GetFiles(folder, "Xrmd.*.dll").Order(StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly) continue;
            var references = reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name));
            if (references.Any(r => SdkModes.AssemblyNames.Contains(r, StringComparer.Ordinal)))
                referencing.Add(reader.GetString(reader.GetAssemblyDefinition().Name));
        }

        var problems = BaselineProblems(referencing, ReadBaseline(AssemblyBaseline, "referenceSdk"), AssemblyBaseline);
        if (problems.Count > 0)
            throw new Xunit.Sdk.XunitException(string.Join("\n", problems) + "\n\nAssemblies that reference the SDK: " + JsonArrayOf(referencing));
    }

    // ---- the tour ---------------------------------------------------------------------------------------------------------

    private sealed record StepResult(string Name, string? Error, TimeSpan Elapsed);

    /// <summary>
    /// Runs every step in order and records each outcome. When <see cref="SdkFreeTour.HostStart"/> fails, the later steps are recorded
    /// as failed without running.
    /// </summary>
    private async Task<List<StepResult>> RunTourAsync(TourContext ctx)
    {
        var results = new List<StepResult>();
        var hostStarted = true;
        foreach (var step in SdkFreeTour.Steps)
        {
            if (!hostStarted)
            {
                results.Add(new StepResult(step.Name, "not run: the host did not start", TimeSpan.Zero));
                continue;
            }
            var started = DateTime.UtcNow;
            string? error = null;
            try
            {
                await step.Run(ctx);
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                if (step.Name == SdkFreeTour.HostStart) hostStarted = false;
            }
            var result = new StepResult(step.Name, error, DateTime.UtcNow - started);
            results.Add(result);
            Echo($"probe step {result.Name}: {(error is null ? "pass" : "FAIL")} ({result.Elapsed.TotalSeconds:0.0} s)"
                 + (error is null ? "" : "\n" + error));
        }
        return results;
    }

    /// <summary>
    /// Starts the published host on a new database with the SDK folder <paramref name="sdk"/>, <c>Sdk:Mode</c> <paramref name="mode"/>,
    /// the SQL endpoint on a free port and webhooks allowed on any port; does not wait for it (the tour's first step does).
    /// </summary>
    private async Task<TourContext> StartAsync(string sdk, string mode)
    {
        var database = "sdkprobe_" + Guid.NewGuid().ToString("N")[..10];
        _databases.Add(database);
        var url = $"http://127.0.0.1:{SdkFreeTour.FreePort()}/";
        var tdsPort = SdkFreeTour.FreePort();
        var work = PublishedApps.NewFolder("sdk-probe");
        var certificate = Path.Combine(work, "tds.crt");
        var key = Path.Combine(work, "tds.key");
        SdkFreeTour.WriteCertificate(certificate, key);

        var psi = await PublishedApps.HostStartInfoAsync(sdk);
        psi.Environment.Remove("XRMD_SDK_MODE");
        psi.Environment["Sdk__Mode"] = mode;
        psi.Environment["ASPNETCORE_URLS"] = url;
        psi.Environment["ConnectionStrings__Org"] = TestProvider.WithDatabase(fx.ConnectionString, database);
        psi.Environment["Storage__Provider"] = TestProvider.Setting(fx.Provider);
        psi.Environment["Auth__Mode"] = "Development";
        psi.Environment["Auth__Audience"] = url;
        psi.Environment["Auth__DevSigningKey"] = DevTokenFactory.SigningKey;
        psi.Environment["Seed__OrgName"] = XrmdFixture.OrgName;
        psi.Environment["Seed__AdminObjectId"] = fx.AdminObjectId.ToString();
        psi.Environment["Seed__AdminName"] = XrmdFixture.AdminName;
        psi.Environment["Pipeline__AsyncWorkerEnabled"] = "false";
        psi.Environment["Pipeline__WebhookAllowAnyPort"] = "true";
        psi.Environment["Files__FileSystem__Root"] = PublishedApps.NewFolder("files");
        psi.Environment["Tds__Port"] = tdsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["Tds__CertificatePath"] = certificate;
        psi.Environment["Tds__PrivateKeyPath"] = key;

        var host = new RunningProcess(psi, Echo);
        _hosts.Add(host);
        var cliSdk = Path.Combine(work, "no-sdk");
        return new TourContext(host, url, fx.AdminObjectId, tdsPort, await PublishedApps.SingleFileCliAsync(), cliSdk, work);
    }

    private void Echo(string line)
    {
        try { output.WriteLine(line); } catch (InvalidOperationException) { }
    }

    // ---- baselines --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The differences between what was found and the baseline: a name found but not listed is a regression, a listed name not
    /// found is progress the baseline must record (it only shrinks).
    /// </summary>
    private static List<string> BaselineProblems(IReadOnlyCollection<string> found, IReadOnlyCollection<string> baseline, string file)
    {
        var problems = new List<string>();
        foreach (var name in found.Where(n => !baseline.Contains(n)))
            problems.Add($"regression: {name} needs the Microsoft SDK");
        foreach (var name in baseline.Where(n => !found.Contains(n)))
            problems.Add($"{name} no longer needs the SDK: remove it from {file}");
        return problems;
    }

    private static List<string> ReadBaseline(string relativePath, string property)
    {
        var path = Path.Combine(PublishedApps.RepositoryRoot, relativePath);
        var json = JsonNode.Parse(File.ReadAllText(path))
                   ?? throw new InvalidOperationException($"{relativePath} is empty.");
        return json[property]?.AsArray().Select(n => n!.GetValue<string>()).ToList()
               ?? throw new InvalidOperationException($"{relativePath} has no '{property}' array.");
    }

    private static string JsonArrayOf(IEnumerable<string> names) => new JsonArray(names.Select(n => (JsonNode)n).ToArray()).ToJsonString();

    private static string FirstLine(string text) => text.Split('\n')[0].TrimEnd('\r');

    private static string Tail(string text, int lines = 80)
    {
        var all = text.Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }
}
