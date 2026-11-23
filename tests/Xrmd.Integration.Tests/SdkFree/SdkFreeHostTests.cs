using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Xrmd.Abstractions.Storage;
using Xrmd.TestSupport;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests.SdkFree;

/// <summary>
/// Plan S.2a, Task 2: the published host with <c>Sdk:Mode</c> <c>none</c> loads no SDK assembly; while the rest of the host still
/// needs the SDK it stops at startup, naming the component that asked for it. Mode <c>standard</c> is unchanged.
/// </summary>
[Collection("xrmd")]
public sealed class SdkFreeHostTests(XrmdFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private const string DocsLink = "https://xrmverse.com/docs/microsoft-sdk#sdk-free-servers";

    private readonly List<RunningProcess> _hosts = [];
    private readonly List<string> _databases = [];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts) host.Dispose();
        _http.Dispose();
        foreach (var database in _databases) await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
    }

    [Fact]
    public async Task Host_in_sdk_free_mode_names_the_component_that_needs_the_sdk()
    {
        var (host, _) = await StartHostAsync(PublishedApps.NewFolder("sdk-free"), "none");

        (await host.WaitForExitAsync(TimeSpan.FromMinutes(4))).Should().BeTrue("the host stops at startup\n" + host.Output);

        host.Process.ExitCode.Should().Be(3, host.Output);
        host.Output.Should().Contain("sdk-free: refused Microsoft.Xrm.Sdk requested by Xrmd.")
            .And.Contain("This build cannot run as an SDK-free server yet:")
            .And.Contain(DocsLink);
        host.Output.Should().NotContain("   at ", "no stack trace is printed");
    }

    [Fact]
    public async Task Host_refuses_an_invalid_sdk_mode()
    {
        var (host, _) = await StartHostAsync(PublishedApps.NewFolder("sdk-mode-off"), "off");

        (await host.WaitForExitAsync(TimeSpan.FromMinutes(4))).Should().BeTrue("the host refuses to start\n" + host.Output);

        host.Process.ExitCode.Should().Be(3, host.Output);
        host.Output.Should().Contain("Sdk:Mode 'off' is not valid: use 'standard' or 'none'.");
        host.Output.Should().NotContain("   at ", "no stack trace is printed");
    }

    [Fact]
    public async Task Standard_mode_is_unchanged()
    {
        var sdk = await PublishedApps.InstalledSdkAsync();
        var (host, url) = await StartHostAsync(sdk, "standard");

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        while (true)
        {
            if (host.Process.HasExited) throw new Xunit.Sdk.XunitException($"The host exited with {host.Process.ExitCode}:\n{host.Output}");
            try
            {
                if ((await _http.GetAsync(url + "healthz")).StatusCode == HttpStatusCode.OK) break;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException("The host did not answer /healthz:\n" + host.Output);
            await Task.Delay(500);
        }

        host.Output.Should().NotContain("sdk-free:").And.NotContain("   at ");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>Starts the published host on a new database with the SDK folder <paramref name="sdk"/> and <c>Sdk:Mode</c> <paramref name="mode"/>.</summary>
    private async Task<(RunningProcess Host, string Url)> StartHostAsync(string sdk, string mode)
    {
        var database = "sdkfree_" + Guid.NewGuid().ToString("N")[..10];
        _databases.Add(database);
        var url = $"http://127.0.0.1:{FreePort()}/";
        var psi = await PublishedApps.HostStartInfoAsync(sdk);
        psi.Environment.Remove("XRMD_SDK_MODE");
        psi.Environment["Sdk__Mode"] = mode;
        psi.Environment["ASPNETCORE_URLS"] = url;
        psi.Environment["ConnectionStrings__Org"] = TestProvider.WithDatabase(fx.ConnectionString, database);
        psi.Environment["Storage__Provider"] = TestProvider.Setting(fx.Provider);
        psi.Environment["Auth__Mode"] = "Development";
        psi.Environment["Auth__Audience"] = fx.BaseUrl;
        psi.Environment["Auth__DevSigningKey"] = DevTokenFactory.SigningKey;
        psi.Environment["Seed__OrgName"] = XrmdFixture.OrgName;
        psi.Environment["Seed__AdminObjectId"] = fx.AdminObjectId.ToString();
        psi.Environment["Seed__AdminName"] = XrmdFixture.AdminName;
        psi.Environment["Pipeline__AsyncWorkerEnabled"] = "false";
        psi.Environment["Files__FileSystem__Root"] = PublishedApps.NewFolder("files");

        var host = new RunningProcess(psi, line =>
        {
            try { output.WriteLine(line); } catch (InvalidOperationException) { }
        });
        _hosts.Add(host);
        return (host, url);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
