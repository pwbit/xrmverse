using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.TestSupport;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 12, B4/B8: the published host (no Microsoft SDK in it) loads the SDK from the SDK folder (<c>XRMD_SDK_DIR</c>), refuses
/// to start without it or with another version, reports it in health and doctor, and hands it to plug-ins.
/// </summary>
[Collection("xrmd")]
public sealed class SdkBootstrapTests(XrmdFixture fx, ITestOutputHelper output) : IAsyncLifetime
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
    public async Task Host_without_sdk_refuses_to_start()
    {
        var empty = PublishedApps.NewFolder("no-sdk");
        var psi = await PublishedApps.HostStartInfoAsync(empty);
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{FreePort()}";

        var host = Start(psi);
        (await host.WaitForExitAsync(TimeSpan.FromMinutes(2))).Should().BeTrue("the host refuses to start\n" + host.Output);

        host.Process.ExitCode.Should().Be(3, host.Output);
        host.Output.Should().Contain(SdkPackage.MissingMessage(empty));
        host.Output.Should().NotContain("TypeLoadException").And.NotContain("FileNotFoundException")
            .And.NotContain("Unhandled exception").And.NotContain("   at ", "no stack trace is printed");
    }

    [Fact]
    public async Task Host_refuses_a_mismatched_sdk_version()
    {
        var sdk = await PublishedApps.InstalledSdkAsync();
        var record = SdkFolder.TryRead(sdk)!;
        SdkFolder.Write(sdk, record with { Version = "1.2.26" });
        var psi = await PublishedApps.HostStartInfoAsync(sdk);
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{FreePort()}";

        var host = Start(psi);
        (await host.WaitForExitAsync(TimeSpan.FromMinutes(2))).Should().BeTrue("the host refuses to start\n" + host.Output);

        host.Process.ExitCode.Should().Be(3, host.Output);
        host.Output.Should().Contain("1.2.26").And.Contain(SdkPackage.Version).And.Contain(sdk).And.Contain("xrmd sdk install");
        host.Output.Should().NotContain("   at ", "no stack trace is printed");
    }

    [Fact]
    public async Task Host_with_sdk_folder_starts_and_reports_the_version()
    {
        var sdk = await PublishedApps.InstalledSdkAsync();
        var (host, url) = await StartHostAsync(sdk, NewDatabase());

        (await _http.GetAsync(url + "healthz")).StatusCode.Should().Be(HttpStatusCode.OK, host.Output);

        var health = JsonNode.Parse(await GetAsAdminAsync(url, "admin/health"))!;
        health["status"]!.GetValue<string>().Should().Be("Healthy");
        health["sdkVersion"]!.GetValue<string>().Should().Be(SdkPackage.XrmSdkFileVersion);

        var checks = JsonNode.Parse(await GetAsAdminAsync(url, "admin/doctor"))!["checks"]!.AsArray();
        var check = checks.Single(c => c!["name"]!.GetValue<string>() == "sdk")!;
        check["status"]!.GetValue<string>().Should().Be("ok");
        check["detail"]!.GetValue<string>().Should().Contain(SdkPackage.XrmSdkFileVersion).And.Contain(sdk);
    }

    [Fact]
    public async Task Plugins_share_the_sdk_loaded_from_the_sdk_folder()
    {
        // The organization, its tables and the early-bound plug-in step are prepared by an in-process host on a database of
        // its own, stopped before the published host serves that database (one process per database).
        string connectionString;
        await using (var prepare = fx.WithOwnDatabase(_ => { }, keepDatabase: true))
        {
            connectionString = prepare.Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!;
            _databases.Add(TestProvider.DatabaseOf(connectionString)); // dropped by DisposeAsync
            await TestPublishers.EnsureAsync(connectionString);
            await TestEntities.EnsurePipeAsync(prepare.Services.GetRequiredService<IMetadataStore>());
            await prepare.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
            var svc = TestServices.NewOrganizationService(prepare.Services, fx.AdminObjectId);
            var assemblyId = await PluginTestHelper.RegisterAssemblyAsync(svc, PluginTestHelper.EarlyBoundPluginBytes, "Xrmd.TestPlugins.EarlyBound");
            var typeId = await PluginTestHelper.RegisterTypeAsync(svc, assemblyId, "Xrmd.TestPlugins.EarlyBound.EarlyBoundLogPlugin");
            await PluginTestHelper.RegisterStepAsync(svc, typeId, "Create", TestEntities.Pipe, 40);
        }

        var sdk = await PublishedApps.InstalledSdkAsync();
        var (host, url) = await StartHostAsync(sdk, TestProvider.DatabaseOf(connectionString));

        var create = new HttpRequestMessage(HttpMethod.Post, url + "api/data/v9.2/" + TestEntities.PipeSet)
        {
            Content = new StringContent("""{"pb_name":"p"}""", Encoding.UTF8, "application/json"),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await _http.SendAsync(create);
        response.IsSuccessStatusCode.Should().BeTrue($"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{host.Output}");

        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name FROM data.{TestEntities.PipeLog}");
        (await cmd.ExecuteScalarAsync()).Should().Be("early|p|1",
            "the early-bound plug-in ran with the host's Microsoft.Xrm.Sdk from the SDK folder (ToEntity<T> and the proxy types)\n" + host.Output);
        host.Output.Should().NotContain("deadlock");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private RunningProcess Start(ProcessStartInfo psi)
    {
        var host = new RunningProcess(psi, line =>
        {
            try { output.WriteLine(line); } catch (InvalidOperationException) { }
        });
        _hosts.Add(host);
        return host;
    }

    /// <summary>A database name on the fixture's SQL Server; the published host creates, migrates and seeds it.</summary>
    private string NewDatabase()
    {
        var database = "sdkboot_" + Guid.NewGuid().ToString("N")[..10];
        _databases.Add(database);
        return database;
    }

    /// <summary>Starts the published host on <paramref name="database"/> with the SDK of <paramref name="sdk"/> and waits for /healthz.</summary>
    private async Task<(RunningProcess Host, string Url)> StartHostAsync(string sdk, string database)
    {
        var port = FreePort();
        var url = $"http://127.0.0.1:{port}/";
        var psi = await PublishedApps.HostStartInfoAsync(sdk);
        psi.Environment["ASPNETCORE_URLS"] = url;
        psi.Environment["Storage__Provider"] = TestProvider.Setting(fx.Provider);
        psi.Environment["ConnectionStrings__Org"] = TestProvider.WithDatabase(fx.ConnectionString, database);
        psi.Environment["Auth__Mode"] = "Development";
        psi.Environment["Auth__Audience"] = fx.BaseUrl;
        psi.Environment["Auth__DevSigningKey"] = DevTokenFactory.SigningKey;
        psi.Environment["Seed__OrgName"] = XrmdFixture.OrgName;
        psi.Environment["Seed__AdminObjectId"] = fx.AdminObjectId.ToString();
        psi.Environment["Seed__AdminName"] = XrmdFixture.AdminName;
        psi.Environment["Pipeline__AsyncWorkerEnabled"] = "false";
        psi.Environment["Files__FileSystem__Root"] = PublishedApps.NewFolder("files");

        var host = Start(psi);
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
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
