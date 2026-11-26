using System.Net;
using FluentAssertions;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Tests;
using Xrmd.Migration.Tests.Source;

namespace Xrmd.Cli.Tests;

/// <summary>
/// <c>xrmd migrate run</c> (plan M5 task 3; spec §9): the export of a (fake) Dataverse environment, then the import of its package on a
/// scripted Admin API; the work folder kept when the import fails, so the same command resumes instead of exporting again.
/// </summary>
[Collection("migrate-import")]
public sealed class MigrateRunCommandTests : IDisposable
{
    private const string Url = "http://xrmd.test:5000";
    private static readonly Guid PackageId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RunId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-run-cli-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static FakeEnvironment Environment()
    {
        var env = new FakeEnvironment();
        env.Add("account", "accountid", false, false, ("name", "String", []))
            .Rows.Add("""{"accountid":"00000000-0000-0000-0000-000000000101","name":"Contoso Example"}""");
        env.Add("systemuser", "systemuserid", false, false, ("fullname", "String", []), ("domainname", "String", []), ("isdisabled", "Boolean", []))
            .Rows.Add("""{"systemuserid":"00000000-0000-0000-0000-000000000201","fullname":"Anna","domainname":"anna@example.com","isdisabled":true}""");
        return env.Build();
    }

    /// <summary>The Admin API: takes the upload, starts the run (or answers <paramref name="start"/>), and reports it <c>completed</c>.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Xrmd(Func<HttpResponseMessage>? start = null) => req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        var method = req.Method.Method;
        if (path == "/admin/migrate/packages" && method == "POST") return FakeHandler.Json(HttpStatusCode.Created, $$"""{"packageId":"{{PackageId}}"}""");
        if (path.StartsWith($"/admin/migrate/packages/{PackageId}/chunks/", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (path == $"/admin/migrate/packages/{PackageId}/complete") return FakeHandler.Json(HttpStatusCode.OK, $$"""{"packageId":"{{PackageId}}","state":"complete"}""");
        if (path == "/admin/migrate/runs" && method == "POST")
            return start?.Invoke() ?? FakeHandler.Json(HttpStatusCode.Accepted, $$"""{"runId":"{{RunId}}","asyncOperationId":"{{Guid.NewGuid()}}"}""");
        if (path == $"/admin/migrate/runs/{RunId}") return FakeHandler.Json(HttpStatusCode.OK, $$"""{"runId":"{{RunId}}","state":"finalized","phase":10,"phases":["verify"]}""");
        if (path == $"/admin/migrate/runs/{RunId}/report")
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"runId":"{{RunId}}","state":"completed","dryRun":true,"tables":[],"issues":[],"phases":[]}""");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    };

    private static async Task<TestCli> CliAsync(FakeEnvironment env, Func<HttpRequestMessage, HttpResponseMessage> xrmd)
    {
        var invoker = new HttpMessageInvoker(env.Fake);
        var cli = new TestCli(request => request.RequestUri!.Host == "contoso.example.com"
            ? invoker.SendAsync(request, CancellationToken.None).GetAwaiter().GetResult()
            : xrmd(request))
        {
            SourceToken = (_, _) => Task.FromResult("source-token"),
        };
        cli.Vars["XRMD_MIGRATE_PASSPHRASE"] = SyntheticPackage.Passphrase;
        cli.Vars["XRMD_MIGRATE_CLIENT_SECRET"] = "s3cret";
        (await cli.LoginDevAsync(Url)).Should().Be(0, cli.Err.ToString());
        return cli;
    }

    private string[] Run(string work, params string[] extra) =>
    [
        "migrate", "run", "--source", "https://contoso.example.com", "--solution", "xv_core", "--work", work,
        "--auth", "secret", "--client-id", "11111111-2222-3333-4444-555555555555", "--tenant", "contoso.example.com", "--parallel", "1", .. extra,
    ];

    private static List<string> Paths(TestCli cli) => [.. cli.Handler.Requests.Select(r => $"{r.RequestUri!.Host}{r.RequestUri.AbsolutePath}")];

    [Fact]
    public async Task Run_exports_then_imports()
    {
        var env = Environment();
        using var cli = await CliAsync(env, Xrmd());
        var work = Path.Combine(_root, "work");

        (await cli.RunAsync(Run(work))).Should().Be(0, cli.Err.ToString());

        var paths = Paths(cli);
        var lastSource = paths.FindLastIndex(p => p.StartsWith("contoso.example.com", StringComparison.Ordinal));
        var firstAdmin = paths.FindIndex(p => p.Contains("/admin/migrate", StringComparison.Ordinal));
        lastSource.Should().BeGreaterThan(-1);
        firstAdmin.Should().BeGreaterThan(lastSource, "the import starts once the export is complete");
        paths.Count(p => p.EndsWith("/admin/migrate/runs", StringComparison.Ordinal)).Should().Be(1);
        paths.Count(p => p.EndsWith("/complete", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Run_with_map_stores_it_and_sends_it_with_the_start()
    {
        using var cli = await CliAsync(Environment(), Xrmd());
        var map = Path.Combine(_root, "sales-like.yaml");
        await File.WriteAllTextAsync(map, SyntheticPackage.SalesLikeYaml);

        (await cli.RunAsync(Run(Path.Combine(_root, "work"), "--map", map, "--keep-export"))).Should().Be(0, cli.Err.ToString());

        var start = cli.Handler.Requests.Select((r, i) => (r, i)).Single(x => x.r.Method == HttpMethod.Post && x.r.RequestUri!.AbsolutePath == "/admin/migrate/runs");
        var body = System.Text.Json.Nodes.JsonNode.Parse(cli.Handler.Bodies[start.i]!)!;
        body["maps"]!.AsArray().Select(m => m!.GetValue<string>()).Should().Equal(SyntheticPackage.SalesLikeYaml);
        using var reader = PackageReader.Open(Path.Combine(_root, "work"), SyntheticPackage.Passphrase);
        reader.Manifest.Maps.Should().Equal("map/1-sales-like.yaml");
    }

    [Fact]
    public async Task Run_deletes_the_export_after_a_completed_run()
    {
        using (var cli = await CliAsync(Environment(), Xrmd()))
            (await cli.RunAsync(Run(Path.Combine(_root, "a")))).Should().Be(0, cli.Err.ToString());
        Directory.Exists(Path.Combine(_root, "a")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "a.xrmpkg")).Should().BeFalse();

        using (var cli = await CliAsync(Environment(), Xrmd()))
            (await cli.RunAsync(Run(Path.Combine(_root, "b"), "--keep-export"))).Should().Be(0, cli.Err.ToString());
        File.Exists(Path.Combine(_root, "b", PackageHeader.FileName)).Should().BeTrue();
    }

    [Fact]
    public async Task Run_dry_run_prints_the_report_and_keeps_the_export()
    {
        var work = Path.Combine(_root, "work");
        using var cli = await CliAsync(Environment(), Xrmd());

        (await cli.RunAsync(Run(work, "--dry-run"))).Should().Be(0, cli.Err.ToString());

        var start = cli.Handler.Requests.Select((r, i) => (r, i)).Single(x => x.r.Method == HttpMethod.Post && x.r.RequestUri!.AbsolutePath == "/admin/migrate/runs");
        System.Text.Json.Nodes.JsonNode.Parse(cli.Handler.Bodies[start.i]!)!["options"]!["dryRun"]!.GetValue<string>().Should().Be("true");
        cli.Out.ToString().Should().StartWith("# Dry run report");
        File.Exists(Path.Combine(work, PackageHeader.FileName)).Should().BeTrue("the real run that follows imports the same export");
    }

    [Fact]
    public async Task Run_dry_run_no_wait_points_to_the_report()
    {
        using var cli = await CliAsync(Environment(), Xrmd());
        (await cli.RunAsync(Run(Path.Combine(_root, "work"), "--dry-run", "--no-wait"))).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain($"xrmd migrate report {RunId}");
    }

    [Fact]
    public async Task Run_keeps_the_export_when_the_import_fails()
    {
        var work = Path.Combine(_root, "work");
        using (var cli = await CliAsync(Environment(), Xrmd(() => FakeHandler.Json(HttpStatusCode.InternalServerError, """{"error":{"code":"0x80040216","message":"boom"}}"""))))
            (await cli.RunAsync(Run(work))).Should().NotBe(0);
        File.Exists(Path.Combine(work, PackageHeader.FileName)).Should().BeTrue();
        File.Exists(work + ".xrmpkg").Should().BeTrue();

        var env = Environment();
        using var again = await CliAsync(env, Xrmd());
        (await again.RunAsync(Run(work))).Should().Be(0, again.Err.ToString());
        env.Fake.Requests.Should().NotContain(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/accounts", StringComparison.Ordinal),
            "the finished export is not read again");
    }

    [Fact]
    public async Task Run_bad_users_file_fails_before_export()
    {
        var users = Path.Combine(_root, "users.yaml");
        await File.WriteAllTextAsync(users, "defaults:\n  licensed: teleport\n");
        var env = Environment();
        using var cli = await CliAsync(env, Xrmd());
        (await cli.RunAsync(Run(Path.Combine(_root, "work"), "--users", users))).Should().Be(2, cli.Err.ToString());
        env.Fake.Requests.Should().BeEmpty();
        Paths(cli).Should().NotContain(p => p.Contains("/admin/migrate", StringComparison.Ordinal));
    }
}
