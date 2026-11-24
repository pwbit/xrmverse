using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.Async;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The organization-wide migration lock (migration M3, task 4; spec §7.2, plan ruling R4): while a run is open, writes are 503 from
/// every front end and reads go through; the async worker runs only migration jobs; the lock survives a restart. Each test serves a
/// database of its own, so the shared one is never locked.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationLockTests(XrmdFixture fx)
{
    private const string Api = "api/data/v9.2/";
    private const int Unexpected = unchecked((int)0x80040216);

    private sealed class Host(WebApplicationFactory<Program> app, XrmdFixture fx) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> App { get; } = app;
        public HttpClient Client { get; } = app.CreateClient();
        public string ConnectionString => App.Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!;

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
        {
            var req = new HttpRequestMessage(method, path) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
            return Client.SendAsync(req);
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private Host Start(Action<IWebHostBuilder>? configure = null, bool keepDatabase = false) =>
        new(fx.WithOwnDatabase(b => configure?.Invoke(b), keepDatabase), fx);

    /// <summary>Opens a queued run in the host's database and loads the lock, as the host does at start.</summary>
    private static async Task<Guid> LockAsync(Host host)
    {
        var store = new MigrationRunStore(host.App.Services.GetRequiredService<IDbConnectionFactory>());
        var run = new MigrationRunRow(Guid.NewGuid(), null, "/data/import/locked.xrmpkg", MigrationRunStates.Queued, 0, "{}", null, null,
            Guid.Empty, DateTime.UtcNow, null, null, null);
        await store.CreateRunAsync(run, default);
        await host.App.Services.GetRequiredService<MigrationLock>().LoadAsync(store, default);
        return run.RunId;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task ShouldBeLockedAsync(HttpResponseMessage res, Guid runId)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
        body.Should().Contain("0x80040216").And.Contain($"The organization is being migrated (run {runId})");
    }

    [Fact]
    public async Task WebApi_post_is_503_get_is_200()
    {
        await using var host = Start();
        var run = await LockAsync(host);

        await ShouldBeLockedAsync(await host.SendAsync(HttpMethod.Post, Api + "accounts", Json("""{"name":"locked"}""")), run);
        (await host.SendAsync(HttpMethod.Get, Api + "accounts?$top=1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.SendAsync(HttpMethod.Get, Api + "WhoAmI")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Soap_RetrieveMultiple_allowed_Soap_Create_is_503_fault()
    {
        await using var host = Start();
        await LockAsync(host);
        const string path = "XRMServices/2011/Organization.svc/web";
        StringContent Soap(OrganizationRequest request) =>
            new(OrganizationSoapEndpointTests.Envelope(OrganizationSoapEndpointTests.Execute(request)), Encoding.UTF8, "text/xml");

        var read = await host.SendAsync(HttpMethod.Post, path, Soap(new RetrieveMultipleRequest { Query = new QueryExpression("account") { TopCount = 1 } }));
        read.StatusCode.Should().Be(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());

        var write = await host.SendAsync(HttpMethod.Post, path, Soap(new CreateRequest { Target = new Entity("account") { ["name"] = "locked" } }));
        var fault = await write.Content.ReadAsStringAsync();
        write.StatusCode.Should().Be(HttpStatusCode.InternalServerError, fault);
        fault.Should().Contain(Unexpected.ToString(System.Globalization.CultureInfo.InvariantCulture)).And.Contain("The organization is being migrated");
    }

    [Fact]
    public async Task Batch_with_only_gets_allowed_batch_with_post_is_refused()
    {
        await using var host = Start();
        await LockAsync(host);
        static string Part(string method, string url, string? json = null) =>
            "--b\r\nContent-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\n" +
            $"{method} {url} HTTP/1.1\r\n" + (json is null ? "" : "Content-Type: application/json; type=entry\r\n") + "\r\n" + (json ?? "") + "\r\n";
        StringContent Batch(params string[] parts)
        {
            var content = new StringContent(string.Concat(parts) + "--b--\r\n", Encoding.UTF8);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/mixed; boundary=\"b\"");
            return content;
        }

        var reads = await host.SendAsync(HttpMethod.Post, Api + "$batch", Batch(Part("GET", "/api/data/v9.2/accounts?$top=1")));
        reads.StatusCode.Should().Be(HttpStatusCode.OK);
        (await reads.Content.ReadAsStringAsync()).Should().Contain("HTTP/1.1 200");

        var writes = await host.SendAsync(HttpMethod.Post, Api + "$batch", Batch(Part("POST", "/api/data/v9.2/accounts", """{"name":"x"}""")));
        (await writes.Content.ReadAsStringAsync()).Should().Contain("HTTP/1.1 503").And.Contain("The organization is being migrated");
    }

    [Fact]
    public async Task Admin_schema_apply_is_503_admin_migrate_get_is_200()
    {
        await using var host = Start();
        var run = await LockAsync(host);

        await ShouldBeLockedAsync(await host.SendAsync(HttpMethod.Post, "admin/schema/apply", Json("""{"tables":[]}""")), run);
        (await host.SendAsync(HttpMethod.Post, "admin/migrate/packages")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await host.SendAsync(HttpMethod.Get, "admin/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Mcp_schema_and_workflow_writes_are_refused_while_locked()
    {
        await using var host = Start();
        await LockAsync(host);
        await using var client = await McpTests.ConnectAsync(host.App, fx.BaseUrl, fx.AdminObjectId);
        const string schema = """
            publisher: new
            tables:
              - name: new_locked
                label: Locked
                primaryName: { name: new_name, label: Name }
            """;

        var dry = await client.CallToolAsync("apply_schema", new Dictionary<string, object?> { ["yaml"] = schema });
        dry.IsError.Should().NotBe(true, "a dry run writes nothing: " + McpTests.Text(dry));
        foreach (var (tool, args) in new (string, Dictionary<string, object?>)[]
        {
            ("apply_schema", new() { ["yaml"] = schema, ["dryRun"] = false }),
            ("apply_workflow", new() { ["yaml"] = "name: locked", ["dryRun"] = false }),
            ("set_workflow_state", new() { ["nameOrId"] = Guid.NewGuid().ToString(), ["activated"] = true }),
        })
        {
            var result = await client.CallToolAsync(tool, args);
            result.IsError.Should().BeTrue(tool);
            McpTests.Text(result).Should().Contain("The organization is being migrated", tool);
        }
        host.App.Services.GetRequiredService<IMetadataStore>().GetEntity("new_locked").Should().BeNull();
    }

    [Fact]
    public async Task File_column_upload_is_503()
    {
        await using var host = Start();
        var run = await LockAsync(host);

        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await ShouldBeLockedAsync(await host.SendAsync(HttpMethod.Patch, Api + $"accounts({Guid.NewGuid()})/entityimage", content), run);
    }

    [Fact]
    public async Task Worker_claims_only_migration_jobs_while_locked()
    {
        var probe = new ProbeHandler();
        await using var host = Start(b =>
        {
            b.UseSetting("Pipeline:AsyncWorkerEnabled", "true");
            b.ConfigureServices(s =>
            {
                // The probe takes the place of the real migration job, which would refuse a job naming no run.
                foreach (var d in s.Where(d => d.ImplementationType == typeof(MigrationImportJob)).ToList()) s.Remove(d);
                s.AddScoped<IAsyncOperationHandler>(_ => probe);
            });
        });
        await LockAsync(host);
        var queue = host.App.Services.GetRequiredService<AsyncOperationQueue>();
        var admin = await TestServices.CallerAsync(host.App.Services, fx.AdminObjectId);
        Guid other, migration;
        await using (var conn = await TestProvider.OpenAsync(host.ConnectionString))
        {
            var s = new DataSession(conn, null);
            other = await queue.EnqueueSystemJobAsync(s, admin, AsyncOperationQueue.DuplicateRulePublish, "other", "PublishDuplicateRule", null, null, null, default);
            migration = await queue.EnqueueSystemJobAsync(s, admin, AsyncOperationQueue.MigrationImport, "migration", AsyncOperationQueue.MigrationImportMessage,
                null, null, null, default);
        }
        queue.Signal();

        (await probe.Ran.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(migration);
        await Task.Delay(1000);
        (await TestSql.ScalarAsync(host.ConnectionString, "SELECT statecode FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", other)))
            .Should().Be(0, "a job of another type waits at Ready while the organization is locked");
    }

    private sealed class ProbeHandler : IAsyncOperationHandler
    {
        public TaskCompletionSource<Guid> Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OperationType => AsyncOperationQueue.MigrationImport;

        public Task RunAsync(AsyncOperationRun run, CancellationToken ct)
        {
            Ran.TrySetResult(run.AsyncOperationId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Lock_survives_restart()
    {
        string database;
        Guid run;
        await using (var first = Start(keepDatabase: true))
        {
            database = TestProvider.DatabaseOf(first.ConnectionString);
            run = await LockAsync(first);
        }
        try
        {
            var connection = TestProvider.WithDatabase(fx.ConnectionString, database);
            await using var second = Start(b => b.UseSetting("ConnectionStrings:Org", connection));
            await ShouldBeLockedAsync(await second.SendAsync(HttpMethod.Post, Api + "accounts", Json("""{"name":"after restart"}""")), run);
        }
        finally
        {
            await fx.DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Clear_lifts_lock()
    {
        await using var host = Start();
        await LockAsync(host);

        host.App.Services.GetRequiredService<MigrationLock>().Clear();

        (await host.SendAsync(HttpMethod.Post, Api + "accounts", Json("""{"name":"unlocked"}"""))).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
