using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xrmd.Files;

namespace Xrmd.Integration.Tests;

/// <summary><c>GET /admin/doctor</c>: database, migrations, auth, async worker and the references of plug-in assemblies.</summary>
[Collection("xrmd")]
public sealed class AdminDoctorTests(XrmdFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    private static async Task<List<(string Name, string Status, string Detail)>> ChecksAsync(HttpClient client, Guid admin, string baseUrl)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/admin/doctor");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(admin, baseUrl));
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!["checks"]!.AsArray()
            .Select(c => (c!["name"]!.GetValue<string>(), c["status"]!.GetValue<string>(), c["detail"]!.GetValue<string>())).ToList();
    }

    private Task<List<(string Name, string Status, string Detail)>> ChecksAsync() => ChecksAsync(fx.Client, fx.AdminObjectId, fx.BaseUrl);

    [Fact]
    public async Task Doctor_is_ok_on_a_healthy_org()
    {
        await using var app = fx.WithOwnDatabase(b => b.UseSetting("Pipeline:AsyncWorkerEnabled", "true"));
        using var client = app.CreateClient();

        var checks = await ChecksAsync(client, fx.AdminObjectId, fx.BaseUrl);

        checks.Select(c => c.Name).Should().Equal("database", "migrations", "auth", "asyncWorker", "sdk", "sdk-workflow", "fileStore");
        checks.Where(c => c.Name is "database" or "migrations" or "asyncWorker" or "sdk").Should().OnlyContain(c => c.Status == "ok");
        // In tests the SDK resolves from the application folder (Plan 12, B4); the published host's folder case is in SdkBootstrapTests.
        checks.Single(c => c.Name == "sdk").Detail.Should().Contain(Xrmd.Abstractions.Sdk.SdkPackage.XrmSdkFileVersion);
        checks.Single(c => c.Name == "auth").Status.Should().NotBe("error"); // Development mode in tests: a warning
    }

    [Fact]
    public async Task Doctor_reports_the_file_store()
    {
        var check = (await ChecksAsync()).Single(c => c.Name == "fileStore");
        check.Status.Should().Be("ok");
        check.Detail.Should().Contain("Local directory");
    }

    [Fact]
    public async Task Doctor_reports_a_file_store_error_without_leaking_secrets()
    {
        await using var app = fx.WithOwnDatabase(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IFileStore>();
            s.AddSingleton<IFileStore>(new FailingFileStore());
        }));
        using var client = app.CreateClient();

        var check = (await ChecksAsync(client, fx.AdminObjectId, fx.BaseUrl)).Single(c => c.Name == "fileStore");

        check.Status.Should().Be("error");
        check.Detail.Should().NotContain("AccountKey=hunter2").And.NotContain("hunter2");
        check.Detail.Should().Contain("InvalidOperationException").And.Contain("file store");
    }

    [Fact]
    public async Task Doctor_maps_a_file_store_warning_to_warning()
    {
        await using var app = fx.WithOwnDatabase(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IFileStore>();
            s.AddSingleton<IFileStore>(new WarningFileStore());
        }));
        using var client = app.CreateClient();

        var check = (await ChecksAsync(client, fx.AdminObjectId, fx.BaseUrl)).Single(c => c.Name == "fileStore");

        check.Status.Should().Be("warning");
        check.Detail.Should().Contain("first write");
    }

    [Fact]
    public async Task Doctor_reports_a_file_store_that_does_not_answer_as_an_error()
    {
        await using var app = fx.WithOwnDatabase(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IFileStore>();
            s.AddSingleton<IFileStore>(new HangingFileStore());
        }));
        app.Services.GetRequiredService<Xrmd.Admin.DoctorReport>().FileStoreTimeout = TimeSpan.FromMilliseconds(200);
        using var client = app.CreateClient();

        var check = (await ChecksAsync(client, fx.AdminObjectId, fx.BaseUrl)).Single(c => c.Name == "fileStore");

        check.Status.Should().Be("error");
        check.Detail.Should().Contain("did not answer within").And.Contain("FileSystem");
    }

    private sealed class HangingFileStore : IFileStore
    {
        public Task<FileStoreCheck> CheckAsync(CancellationToken ct) => new TaskCompletionSource<FileStoreCheck>().Task;
        public Task WriteAsync(string key, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> GetLengthAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task DeletePrefixAsync(string prefix, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct) => throw new NotSupportedException();
        public Task ConcatAsync(IReadOnlyList<string> sourceKeys, string targetKey, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class WarningFileStore : IFileStore
    {
        public Task<FileStoreCheck> CheckAsync(CancellationToken ct) => Task.FromResult(FileStoreCheck.Warn("will be created on the first write"));
        public Task WriteAsync(string key, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> GetLengthAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task DeletePrefixAsync(string prefix, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct) => throw new NotSupportedException();
        public Task ConcatAsync(IReadOnlyList<string> sourceKeys, string targetKey, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FailingFileStore : IFileStore
    {
        public Task<FileStoreCheck> CheckAsync(CancellationToken ct) =>
            throw new InvalidOperationException("boom AccountKey=hunter2;");

        public Task WriteAsync(string key, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> GetLengthAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task DeletePrefixAsync(string prefix, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct) => throw new NotSupportedException();
        public Task ConcatAsync(IReadOnlyList<string> sourceKeys, string targetKey, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Doctor_reports_missing_reference_of_a_plugin_assembly()
    {
        var svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        await PluginTestHelper.RegisterAssemblyAsync(svc, PluginTestHelper.TestPluginBytes);

        var checks = await ChecksAsync();

        checks.Select(c => c.Name).Should().Equal("database", "migrations", "auth", "asyncWorker", "sdk", "sdk-workflow", "fileStore", "pluginAssembly:Xrmd.TestPlugins");
        var plugin = checks.Last();
        plugin.Status.Should().Be("error");
        plugin.Detail.Should().Be("Xrmd.TestPlugins.Dependency");
        // The check must not leave the assembly loaded in the default context.
        System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies.Should().NotContain(a => a.GetName().Name == "Xrmd.TestPlugins");
    }

    [Fact]
    public async Task Doctor_warns_about_plugin_assemblies_that_share_a_name()
    {
        var svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        var first = await PluginTestHelper.RegisterAssemblyAsync(svc, PluginTestHelper.EarlyBoundPluginBytes, "Shared");
        // Written before names were unique (issue #70): only SQL can still add such a row.
        var second = Guid.NewGuid();
        await TestSql.ExecAsync(fx.ConnectionString,
            "INSERT INTO data.pluginassembly (pluginassemblyid, name, content) SELECT @id, N'SHARED', content FROM data.pluginassembly WHERE pluginassemblyid = @first",
            ("@id", second), ("@first", first));

        var checks = await ChecksAsync();

        var duplicates = checks.Single(c => c.Name == "pluginAssemblies");
        duplicates.Status.Should().Be("warning");
        duplicates.Detail.Should().ContainEquivalentOf("'Shared'").And.Contain(first.ToString()).And.Contain(second.ToString());
    }

    [Fact]
    public async Task Doctor_warns_when_async_worker_disabled()
    {
        // The fixture's host runs with Pipeline:AsyncWorkerEnabled=false.
        var checks = await ChecksAsync();

        var worker = checks.Single(c => c.Name == "asyncWorker");
        worker.Status.Should().Be("warning");
        worker.Detail.Should().Contain("Pipeline:AsyncWorkerEnabled");
    }
}
