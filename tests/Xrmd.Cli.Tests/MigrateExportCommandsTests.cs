using System.Text.Json;
using FluentAssertions;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Tests;
using Xrmd.Migration.Tests.Source;

namespace Xrmd.Cli.Tests;

public sealed class MigrateExportCommandsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-cli-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static FakeEnvironment Environment()
    {
        var env = new FakeEnvironment();
        var account = env.Add("account", "accountid", false, false, ("name", "String", []));
        account.Rows.Add("""{"accountid":"00000000-0000-0000-0000-000000000101","name":"Contoso Example"}""");
        env.Add("systemuser", "systemuserid", false, false, ("fullname", "String", []), ("domainname", "String", []), ("isdisabled", "Boolean", []))
            .Rows.Add("""{"systemuserid":"00000000-0000-0000-0000-000000000201","fullname":"Anna","domainname":"anna@example.com","isdisabled":true}""");
        return env.Build();
    }

    private TestCli Cli(FakeEnvironment env)
    {
        var invoker = new HttpMessageInvoker(env.Fake);
        var cli = new TestCli(request => invoker.SendAsync(request, CancellationToken.None).GetAwaiter().GetResult())
        {
            SourceToken = (_, _) => Task.FromResult("source-token"),
        };
        cli.Vars["XRMD_MIGRATE_PASSPHRASE"] = SyntheticPackage.Passphrase;
        cli.Vars["XRMD_MIGRATE_CLIENT_SECRET"] = "s3cret";
        return cli;
    }

    private string[] Export(string folder, params string[] extra) =>
    [
        "migrate", "export", "--source", "https://contoso.example.com", "--out", folder, "--solution", "xv_core",
        "--auth", "secret", "--client-id", "11111111-2222-3333-4444-555555555555", "--tenant", "contoso.example.com", "--parallel", "1", .. extra,
    ];

    [Fact]
    public async Task Export_with_map_writes_a_format_2_package_with_the_map()
    {
        var env = Environment();
        using var cli = Cli(env);
        var map = Path.Combine(_root, "Sales Like.yaml");
        await File.WriteAllTextAsync(map, SyntheticPackage.SalesLikeYaml);
        var folder = Path.Combine(_root, "out");
        (await cli.RunAsync(Export(folder, "--map", map))).Should().Be(0, cli.Err.ToString());

        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        reader.Manifest.FormatVersion.Should().Be(PackageHeader.MappedVersion);
        reader.Manifest.Maps.Should().Equal("map/1-sales-like.yaml");
        reader.Manifest.Entries.Should().Contain(e => e.Path == "map/source.json");
    }

    [Fact]
    public async Task Export_with_an_invalid_map_exits_2_before_any_request()
    {
        var env = Environment();
        using var cli = Cli(env);
        var map = Path.Combine(_root, "bad.yaml");
        await File.WriteAllTextAsync(map, "version: 1\ntables:\n  - source: a\n    target: b\n  - source: c\n    target: b\n");
        (await cli.RunAsync(Export(Path.Combine(_root, "out"), "--map", map))).Should().Be(2);
        cli.Err.ToString().Should().Contain("Rule 2");
        env.Fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Export_with_secret_auth_writes_a_verifiable_package()
    {
        var env = Environment();
        using var cli = Cli(env);
        var folder = Path.Combine(_root, "out");
        var file = Path.Combine(_root, "contoso.xrmpkg");
        (await cli.RunAsync(Export(folder, "--pack", file))).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain("account").And.Contain(file);
        cli.Err.ToString().Should().Contain("data/account");
        env.Fake.Requests.Should().OnlyContain(r => r.Request.Headers.Authorization!.Parameter == "source-token");

        using var reader = PackageReader.Open(file, SyntheticPackage.Passphrase);
        reader.Manifest.Tables.Should().Contain(t => t.LogicalName == "account" && t.Records == 1);
        (await reader.VerifyAsync(null, CancellationToken.None)).Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task Export_json_prints_the_manifest_without_entries()
    {
        using var cli = Cli(Environment());
        (await cli.RunAsync([.. Export(Path.Combine(_root, "out")), "-o", "json"])).Should().Be(0, cli.Err.ToString());
        using var doc = JsonDocument.Parse(cli.Out.ToString());
        doc.RootElement.GetProperty("tables").GetArrayLength().Should().BeGreaterThan(0);
        doc.RootElement.TryGetProperty("entries", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Export_without_client_id_is_a_usage_error()
    {
        var env = Environment();
        using var cli = Cli(env);
        (await cli.RunAsync("migrate", "export", "--source", "https://contoso.example.com", "--out", Path.Combine(_root, "out"),
            "--auth", "secret", "--tenant", "contoso.example.com", "--all-unmanaged")).Should().Be(2);
        cli.Err.ToString().Should().Contain("--client-id");
        env.Fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Secret_auth_without_env_var_is_a_usage_error_naming_it()
    {
        using var cli = Cli(Environment());
        cli.Vars["XRMD_MIGRATE_CLIENT_SECRET"] = null;
        (await cli.RunAsync(Export(Path.Combine(_root, "out")))).Should().Be(2);
        cli.Err.ToString().Should().Contain("XRMD_MIGRATE_CLIENT_SECRET");
    }

    [Fact]
    public async Task Export_needs_solutions_or_all_unmanaged()
    {
        using var cli = Cli(Environment());
        (await cli.RunAsync("migrate", "export", "--source", "https://contoso.example.com", "--out", Path.Combine(_root, "out"),
            "--auth", "secret", "--client-id", "x", "--tenant", "t")).Should().Be(2);
        cli.Err.ToString().Should().Contain("--all-unmanaged");
    }

    [Fact]
    public async Task Export_into_non_empty_folder_without_header_is_refused()
    {
        var folder = Path.Combine(_root, "out");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine");
        var env = Environment();
        using var cli = Cli(env);
        (await cli.RunAsync(Export(folder))).Should().Be(2);
        cli.Err.ToString().Should().Contain("not empty");
        env.Fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Export_resumes_in_existing_folder()
    {
        var folder = Path.Combine(_root, "out");
        var broken = Environment();
        broken.FailFromRow["account"] = 0;
        broken.FailStatus = System.Net.HttpStatusCode.Unauthorized;
        using (var cli = Cli(broken))
            (await cli.RunAsync(Export(folder))).Should().Be(3);
        File.Exists(Path.Combine(folder, "header.json")).Should().BeTrue();

        var env = Environment();
        using var again = Cli(env);
        (await again.RunAsync(Export(folder))).Should().Be(0, again.Err.ToString());
        again.Err.ToString().Should().Contain("Resuming");
        env.Fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post, "the solutions were exported by the first run");
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        reader.Manifest.Tables.Should().Contain(t => t.LogicalName == "account" && t.Records == 1);
    }

    [Fact]
    public async Task Resume_with_another_passphrase_exits_3()
    {
        var folder = Path.Combine(_root, "out");
        var broken = Environment();
        broken.FailFromRow["account"] = 0;
        broken.FailStatus = System.Net.HttpStatusCode.Unauthorized;
        using (var cli = Cli(broken)) await cli.RunAsync(Export(folder));
        using var again = Cli(Environment());
        again.Vars["XRMD_MIGRATE_PASSPHRASE"] = "another long passphrase";
        (await again.RunAsync(Export(folder))).Should().Be(3);
    }

    [Fact]
    public async Task Managed_solution_without_file_exits_5()
    {
        var env = Environment();
        env.SolutionsJson = """
            {"value":[{"solutionid":"00000000-0000-0000-0000-0000000000d2","uniquename":"isv_app","version":"1.0.0.0","ismanaged":true,"publisherid":{"uniquename":"isv","customizationprefix":"isv"}}]}
            """;
        using var cli = Cli(env);
        (await cli.RunAsync([.. Export(Path.Combine(_root, "out")).Select(a => a == "xv_core" ? "isv_app" : a)])).Should().Be(5);
    }

    [Fact]
    public async Task Users_writes_the_template()
    {
        var folder = Path.Combine(_root, "out");
        using var cli = Cli(Environment());
        (await cli.RunAsync(Export(folder))).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("migrate", "users", folder)).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain("id: 00000000-0000-0000-0000-000000000201").And.Contain("action: stub");
    }
}
