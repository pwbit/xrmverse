using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xrmd.Abstractions.Sdk;
using Xrmd.TestSupport;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.14, ruling W18: the published host does not need the Microsoft workflow SDK. It loads
/// <c>Microsoft.Xrm.Sdk.Workflow.dll</c> from the SDK folder when <c>xrmd sdk install</c> put it there, reports its version in
/// <c>GET /admin/health</c> (<c>workflowSdkVersion</c>, null when absent), and <c>xrmd server doctor</c> warns
/// (<c>sdk-workflow</c>) when it is absent.
/// </summary>
[Collection("xrmd")]
public sealed class HostSdkTests(XrmdFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly List<RunningProcess> _hosts = [];
    private readonly List<string> _databases = [];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts) host.Dispose();
        _http.Dispose();
        if (_databases.Count == 0) return;
        foreach (var database in _databases) await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
    }

    [Fact]
    public async Task Host_starts_without_workflow_sdk()
    {
        // RF5: an SDK folder installed before the workflow SDK existed (or with --from alone) is enough to run the server.
        var sdk = await PublishedApps.InstalledSdkAsync();
        File.Exists(Path.Combine(sdk, SdkPackage.Workflow.AssemblyFileName)).Should().BeFalse();

        var (host, url) = await StartHostAsync(sdk);

        (await _http.GetAsync(url + "healthz")).StatusCode.Should().Be(HttpStatusCode.OK, host.Output);
        var health = JsonNode.Parse(await GetAsAdminAsync(url, "admin/health"))!.AsObject();
        health["status"]!.GetValue<string>().Should().Be("Healthy");
        health["sdkVersion"]!.GetValue<string>().Should().Be(SdkPackage.XrmSdkFileVersion);
        health.ContainsKey("workflowSdkVersion").Should().BeTrue("the field is reported, null when the workflow SDK is absent");
        health["workflowSdkVersion"].Should().BeNull();
        host.Output.Should().NotContain("   at ", "no stack trace is printed");
    }

    [Fact]
    public async Task Health_reports_workflow_sdk_version()
    {
        var sdk = await PublishedApps.InstalledSdkAsync(withWorkflow: true);
        var expected = FileVersionInfo.GetVersionInfo(Path.Combine(sdk, SdkPackage.Workflow.AssemblyFileName)).FileVersion;
        expected.Should().NotBeNullOrEmpty();

        var (_, url) = await StartHostAsync(sdk);

        var health = JsonNode.Parse(await GetAsAdminAsync(url, "admin/health"))!;
        health["workflowSdkVersion"]!.GetValue<string>().Should().Be(expected);
        var check = Check(await GetAsAdminAsync(url, "admin/doctor"), "sdk-workflow");
        check["status"]!.GetValue<string>().Should().Be("ok");
        check["detail"]!.GetValue<string>().Should().Contain(expected).And.Contain(sdk);
    }

    [Fact]
    public async Task Doctor_warns_when_workflow_sdk_missing()
    {
        var sdk = await PublishedApps.InstalledSdkAsync();

        var (_, url) = await StartHostAsync(sdk);

        var doctor = await GetAsAdminAsync(url, "admin/doctor");
        Check(doctor, "sdk")["status"]!.GetValue<string>().Should().Be("ok", "the Dataverse SDK is installed");
        var check = Check(doctor, "sdk-workflow");
        check["status"]!.GetValue<string>().Should().Be("warning");
        check["detail"]!.GetValue<string>().Should().Contain(SdkPackage.Workflow.AssemblyFileName).And.Contain(sdk)
            .And.Contain("custom workflow activities").And.Contain("xrmd sdk install");
    }

    [Fact]
    public async Task Doctor_warns_when_workflow_sdk_is_not_the_pinned_one()
    {
        // Review M1: a workflow SDK that is not the pinned package is loaded (the host never requires it), and the doctor
        // warns, naming what it found.
        var sdk = await PublishedApps.InstalledSdkAsync(withWorkflow: true);
        var record = SdkFolder.TryRead(sdk)!;
        SdkFolder.Write(sdk, record with { WorkflowVersion = "9.0.2.59" });
        var expected = FileVersionInfo.GetVersionInfo(Path.Combine(sdk, SdkPackage.Workflow.AssemblyFileName)).FileVersion;

        var (host, url) = await StartHostAsync(sdk);

        var health = JsonNode.Parse(await GetAsAdminAsync(url, "admin/health"))!;
        health["status"]!.GetValue<string>().Should().Be("Healthy");
        health["workflowSdkVersion"]!.GetValue<string>().Should().Be(expected);
        var doctor = await GetAsAdminAsync(url, "admin/doctor");
        Check(doctor, "sdk")["status"]!.GetValue<string>().Should().Be("ok");
        var check = Check(doctor, "sdk-workflow");
        check["status"]!.GetValue<string>().Should().Be("warning");
        check["detail"]!.GetValue<string>().Should().Contain("9.0.2.59").And.Contain(SdkPackage.Workflow.Version)
            .And.Contain("xrmd sdk install");
        host.Output.Should().Contain("9.0.2.59", "the host also warns at startup");
    }

    [Theory]
    [InlineData(null, null, null, SdkPackage.Workflow.FileVersion, null)]
    [InlineData(SdkPackage.Workflow.Id, SdkPackage.Workflow.Version, SdkPackage.Workflow.Sha512Base64, SdkPackage.Workflow.FileVersion, null)]
    [InlineData(null, null, null, "9.2.49.1", "Microsoft.Xrm.Sdk.Workflow 9.2.49.1 (expected " + SdkPackage.Workflow.FileVersion + ")")]
    [InlineData(SdkPackage.Workflow.Id, "9.0.2.59", SdkPackage.Workflow.Sha512Base64, SdkPackage.Workflow.FileVersion, "Microsoft.CrmSdk.Workflow 9.0.2.59")]
    [InlineData(SdkPackage.Workflow.Id, SdkPackage.Workflow.Version, "AAAA", SdkPackage.Workflow.FileVersion, "another package hash (AAAA)")]
    public void Workflow_sdk_mismatch_names_what_differs_from_the_pinned_package(
        string? package, string? version, string? sha512, string fileVersion, string? expected)
    {
        var record = new SdkRecord(SdkPackage.Id, SdkPackage.Version, SdkPackage.Sha512Base64, SdkPackage.LicenseUrl, DateTimeOffset.UnixEpoch, "flag",
            package, version, sha512);

        var problem = Xrmd.Host.SdkBootstrap.WorkflowSdkMismatch(record, fileVersion);

        if (expected is null) problem.Should().BeNull();
        else problem.Should().Contain(expected);
        Xrmd.Host.SdkBootstrap.WorkflowSdkMismatch(null, SdkPackage.Workflow.FileVersion).Should().BeNull("an SDK in the application folder has no record");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static JsonNode Check(string doctor, string name) =>
        JsonNode.Parse(doctor)!["checks"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!;

    /// <summary>Starts the published host on a new database with the SDK of <paramref name="sdk"/> and waits for /healthz.</summary>
    private async Task<(RunningProcess Host, string Url)> StartHostAsync(string sdk)
    {
        var database = "hostsdk_" + Guid.NewGuid().ToString("N")[..10];
        _databases.Add(database);
        var url = $"http://127.0.0.1:{FreePort()}/";
        var psi = await PublishedApps.HostStartInfoAsync(sdk);
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
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        while (DateTime.UtcNow < deadline)
        {
            if (host.Process.HasExited) throw new Xunit.Sdk.XunitException($"The host exited with {host.Process.ExitCode}:\n{host.Output}");
            try
            {
                if ((await _http.GetAsync(url + "healthz")).StatusCode == HttpStatusCode.OK) return (host, url);
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }

        throw new Xunit.Sdk.XunitException("The host did not answer /healthz:\n" + host.Output);
    }

    private async Task<string> GetAsAdminAsync(string url, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
