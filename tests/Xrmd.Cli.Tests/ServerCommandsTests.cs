using System.Net;
using FluentAssertions;
using Xrmd.Cli;

namespace Xrmd.Cli.Tests;

public class ServerCommandsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xrmd-server-tests", Guid.NewGuid().ToString("N"));
    /// <summary>The certificate pairs a test writes (outside the installation, as a customer's files would be).</summary>
    private readonly string _certDir = Path.Combine(Path.GetTempPath(), "xrmd-cert-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var dir in new[] { _dir, _certDir })
            try { Directory.Delete(dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static Dictionary<string, string> ReadEnv(string dir) =>
        File.ReadAllLines(Path.Combine(dir, ".env"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1]);

    /// <summary>Init, then a stand-in SDK record in the installation (as after `xrmd sdk install --dir`), which `server up` requires.</summary>
    private async Task InitAsync(TestCli cli, params string[] extra)
    {
        await InitWithoutSdkAsync(cli, extra);
        FakeSdk.Install(_dir);
    }

    private async Task InitWithoutSdkAsync(TestCli cli, params string[] extra)
    {
        (await cli.RunAsync(["server", "init", "--dir", _dir, .. TestCli.Eula(extra)])).Should().Be(0, cli.Err.ToString());
    }

    [Fact]
    public async Task Init_writes_the_image_of_the_cli_version()
    {
        using var cli = new TestCli { CliVersion = "1.4.2" };
        await InitAsync(cli);

        ReadEnv(_dir)["XRMD_IMAGE"].Should().Be("ghcr.io/pwbit/xrmd:1.4.2");
        ComposeYaml.Resolve(_dir)["xrmd-xrmd"].Image.Should().Be("ghcr.io/pwbit/xrmd:1.4.2");
        cli.Out.ToString().Should().NotContain("docker build");
    }

    [Fact]
    public async Task Init_with_a_dev_cli_writes_the_local_image_and_the_build_hint()
    {
        using var cli = new TestCli { CliVersion = "0.0.0-dev" };
        await InitAsync(cli);

        ReadEnv(_dir)["XRMD_IMAGE"].Should().Be("xrmd:local");
        cli.Out.ToString().Should().Contain("docker build -f docker/Dockerfile -t xrmd:local .");
    }

    [Fact]
    public async Task Init_image_option_wins()
    {
        using var cli = new TestCli { CliVersion = "1.4.2" };
        await InitAsync(cli, "--image", "registry.example.com/xrmd:9");

        ReadEnv(_dir)["XRMD_IMAGE"].Should().Be("registry.example.com/xrmd:9");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("quo\"te")]
    public async Task Init_rejects_an_unusable_image_before_writing(string image)
    {
        using var cli = new TestCli();
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir, "--image", image)).Should().Be(2);
        File.Exists(Path.Combine(_dir, ".env")).Should().BeFalse();
    }

    [Fact]
    public async Task Up_adds_the_image_to_an_env_without_it()
    {
        using var cli = new TestCli { CliVersion = "1.4.2" };
        await InitAsync(cli);
        var envPath = Path.Combine(_dir, ".env");
        File.WriteAllLines(envPath, File.ReadAllLines(envPath).Where(l => !l.StartsWith("XRMD_IMAGE=")));
        ReadEnv(_dir).Should().NotContainKey("XRMD_IMAGE");
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        ReadEnv(_dir)["XRMD_IMAGE"].Should().Be("ghcr.io/pwbit/xrmd:1.4.2");
        ComposeYaml.Resolve(_dir)["xrmd-xrmd"].Image.Should().Be("ghcr.io/pwbit/xrmd:1.4.2");
        cli.Err.ToString().Should().Contain("Added XRMD_IMAGE=ghcr.io/pwbit/xrmd:1.4.2");
    }

    [Fact]
    public async Task Init_force_keeps_an_existing_pinned_image()
    {
        using var cli = new TestCli { CliVersion = "1.4.2" };
        await InitAsync(cli);
        cli.CliVersion = "1.5.0";
        await InitWithoutSdkAsync(cli, "--force", "--image", "ghcr.io/pwbit/xrmd:1.3.0");
        cli.CliVersion = "1.6.0";

        await InitWithoutSdkAsync(cli, "--force");

        ReadEnv(_dir)["XRMD_IMAGE"].Should().Be("ghcr.io/pwbit/xrmd:1.3.0");
        var keptImageLine = cli.Out.ToString().Split('\n').Single(l => l.StartsWith("Kept XRMD_IMAGE"));
        keptImageLine.Should().Contain("ghcr.io/pwbit/xrmd:1.3.0").And.NotContain("sa password");
    }

    [Fact]
    public async Task Init_force_with_image_overrides_the_existing_one()
    {
        using var cli = new TestCli { CliVersion = "1.4.2" };
        await InitAsync(cli);

        await InitWithoutSdkAsync(cli, "--force", "--image", "registry.example.com/xrmd:9");

        ReadEnv(_dir)["XRMD_IMAGE"].Should().Be("registry.example.com/xrmd:9");
    }

    [Fact]
    public async Task Init_force_with_an_empty_image_value_falls_back_to_the_default()
    {
        using var cli = new TestCli { CliVersion = "1.4.2" };
        await InitAsync(cli);
        var envPath = Path.Combine(_dir, ".env");
        File.WriteAllText(envPath, File.ReadAllText(envPath).Replace("XRMD_IMAGE=ghcr.io/pwbit/xrmd:1.4.2", "XRMD_IMAGE="));

        await InitWithoutSdkAsync(cli, "--force");

        ReadEnv(_dir)["XRMD_IMAGE"].Should().Be("ghcr.io/pwbit/xrmd:1.4.2");
    }

    [Fact]
    public async Task Upgrade_to_on_an_env_without_the_key_prints_one_accurate_notice()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, """{"status":"Healthy"}"""));
        (await cli.LoginDevAsync()).Should().Be(0);
        cli.CliVersion = "1.4.2";
        await InitAsync(cli);
        var envPath = Path.Combine(_dir, ".env");
        File.WriteAllLines(envPath, File.ReadAllLines(envPath).Where(l => !l.StartsWith("XRMD_IMAGE=")));
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "upgrade", "--dir", _dir, "--to", "1.5.0")).Should().Be(0, cli.Err.ToString());

        ReadEnv(_dir)["XRMD_IMAGE"].Should().Be("ghcr.io/pwbit/xrmd:1.5.0");
        var err = cli.Err.ToString();
        err.Should().Contain("Added XRMD_IMAGE=ghcr.io/pwbit/xrmd:1.5.0").And.NotContain("xrmd:1.4.2");
    }

    [Fact]
    public async Task Up_keeps_an_image_that_is_there()
    {
        using var cli = new TestCli { CliVersion = "1.4.2" };
        await InitAsync(cli, "--image", "xrmd:local");
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        ReadEnv(_dir)["XRMD_IMAGE"].Should().Be("xrmd:local");
        cli.Err.ToString().Should().NotContain("XRMD_IMAGE");
    }

    [Fact]
    public async Task Upgrade_to_rewrites_the_tag_before_pulling()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, """{"status":"Healthy"}"""));
        (await cli.LoginDevAsync()).Should().Be(0);
        cli.CliVersion = "1.4.2";
        await InitAsync(cli);
        string? imageAtPull = null;
        var runner = new FakeProcessRunner();
        cli.Process = new PullSpy(runner, () => { if (imageAtPull is null) imageAtPull = ReadEnv(_dir)["XRMD_IMAGE"]; });

        (await cli.RunAsync("server", "upgrade", "--dir", _dir, "--to", "1.5.0")).Should().Be(0, cli.Err.ToString());

        imageAtPull.Should().Be("ghcr.io/pwbit/xrmd:1.5.0");
        ComposeYaml.Resolve(_dir)["xrmd-xrmd"].Image.Should().Be("ghcr.io/pwbit/xrmd:1.5.0");
    }

    [Fact]
    public async Task Upgrade_to_refuses_a_foreign_image_and_a_bad_version_without_touching_anything()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, """{"status":"Healthy"}"""));
        (await cli.LoginDevAsync()).Should().Be(0);
        await InitAsync(cli); // dev CLI: xrmd:local
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        var before = File.ReadAllText(Path.Combine(_dir, ".env"));

        (await cli.RunAsync("server", "upgrade", "--dir", _dir, "--to", "1.5.0")).Should().Be(5);
        (await cli.RunAsync("server", "upgrade", "--dir", _dir, "--to", "1.5")).Should().Be(2);

        runner.Calls.Should().BeEmpty();
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(before);
    }

    /// <summary>Releases as GitHub lists them: unordered, with a draft, a non-release tag and a newer pre-release.</summary>
    private const string ReleasesJson = """
        [
          {"tag_name":"v0.1.0-preview.4","draft":false,"prerelease":true,"published_at":"2026-11-17T19:10:07Z"},
          {"tag_name":"v9.0.0","draft":true,"prerelease":false,"published_at":null},
          {"tag_name":"v0.1.0-preview.10","draft":false,"prerelease":true,"published_at":"2026-11-25T10:00:00Z"},
          {"tag_name":"nightly","draft":false,"prerelease":true,"published_at":"2026-11-20T10:00:00Z"},
          {"tag_name":"v0.1.0-preview.9","draft":false,"prerelease":true,"published_at":"2026-11-24T10:00:00Z"}
        ]
        """;

    /// <summary>GitHub's releases list on api.github.com (or <paramref name="github"/>), a healthy server elsewhere.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> GitHubAndHealthy(Func<HttpResponseMessage>? github = null) => request =>
        request.RequestUri!.Host == "api.github.com"
            ? github?.Invoke() ?? FakeHandler.Json(HttpStatusCode.OK, ReleasesJson)
            : FakeHandler.Json(HttpStatusCode.OK, """{"status":"Healthy"}""");

    private async Task<(TestCli Cli, FakeProcessRunner Runner, string EnvBefore)> InstalledAsync(string cliVersion,
        Func<HttpResponseMessage>? github = null)
    {
        var cli = new TestCli(GitHubAndHealthy(github));
        (await cli.LoginDevAsync()).Should().Be(0);
        cli.CliVersion = "0.1.0-preview.4";
        await InitAsync(cli);
        cli.CliVersion = cliVersion;
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        return (cli, runner, File.ReadAllText(Path.Combine(_dir, ".env")));
    }

    [Theory]
    [InlineData("--to", "latest")]
    [InlineData("--to", "LATEST")]
    [InlineData("--latest", null)]
    public async Task Upgrade_to_latest_writes_the_exact_newest_version(string option, string? value)
    {
        var (cli, runner, _) = await InstalledAsync("0.1.0-preview.10");
        using var _cli = cli;
        string[] args = value is null ? ["server", "upgrade", "--dir", _dir, option] : ["server", "upgrade", "--dir", _dir, option, value];

        (await cli.RunAsync(args)).Should().Be(0, cli.Err.ToString());

        ReadEnv(_dir)["XRMD_IMAGE"].Should().Be("ghcr.io/pwbit/xrmd:0.1.0-preview.10");
        ComposeYaml.Resolve(_dir)["xrmd-xrmd"].Image.Should().Be("ghcr.io/pwbit/xrmd:0.1.0-preview.10");
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().NotContain("xrmd:latest");
        runner.Calls.Should().NotBeEmpty();
        cli.Err.ToString().Should().Contain("The newest release is 0.1.0-preview.10");
        var github = cli.Handler.Requests.Where(r => r.RequestUri!.Host == "api.github.com").ToList();
        github.Select(r => r.RequestUri!.AbsolutePath).Should()
            .Equal("/repos/pwbit/xrmverse/releases", "/repos/pwbit/xrmverse-releases/releases");
        github.Should().AllSatisfy(r =>
        {
            r.Headers.UserAgent.ToString().Should().Be("xrmd-cli/1.0");
            r.Headers.Authorization.Should().BeNull();
        });
    }

    [Fact]
    public async Task Upgrade_to_latest_refuses_a_cli_older_than_the_release_without_touching_anything()
    {
        var (cli, runner, before) = await InstalledAsync("0.1.0-preview.9");
        using var _cli = cli;

        (await cli.RunAsync("server", "upgrade", "--dir", _dir, "--to", "latest")).Should().Be(5);

        cli.Err.ToString().Should().Contain("install the CLI 0.1.0-preview.10 first").And.Contain("--allow-cli-mismatch");
        runner.Calls.Should().BeEmpty();
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(before);
    }

    [Fact]
    public async Task Upgrade_to_latest_with_allow_cli_mismatch_goes_on()
    {
        var (cli, _, _) = await InstalledAsync("0.1.0-preview.9");
        using var _cli = cli;

        (await cli.RunAsync("server", "upgrade", "--dir", _dir, "--latest", "--allow-cli-mismatch")).Should().Be(0, cli.Err.ToString());

        ReadEnv(_dir)["XRMD_IMAGE"].Should().Be("ghcr.io/pwbit/xrmd:0.1.0-preview.10");
    }

    public static TheoryData<int, string> GitHubFailures => new()
    {
        { 6, "unreachable" },
        { 1, "rate-limited" },
        { 1, "garbage" },
        { 4, "empty" },
    };

    [Theory]
    [MemberData(nameof(GitHubFailures))]
    public async Task Upgrade_to_latest_without_a_usable_answer_asks_for_an_exact_version(int exitCode, string failure)
    {
        var (cli, runner, before) = await InstalledAsync("0.1.0-preview.10", () => failure switch
        {
            "unreachable" => throw new HttpRequestException("Name or service not known"),
            "rate-limited" => FakeHandler.Json(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}"""),
            "garbage" => FakeHandler.Json(HttpStatusCode.OK, "<html>"),
            _ => FakeHandler.Json(HttpStatusCode.OK, "[]"),
        });
        using var _cli = cli;

        (await cli.RunAsync("server", "upgrade", "--dir", _dir, "--to", "latest")).Should().Be(exitCode);

        cli.Err.ToString().Should().Contain("xrmd server upgrade --to <version>");
        runner.Calls.Should().BeEmpty();
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(before);
    }

    [Fact]
    public async Task Upgrade_latest_options_are_checked_before_anything()
    {
        var (cli, runner, before) = await InstalledAsync("0.1.0-preview.10");
        using var _cli = cli;

        (await cli.RunAsync("server", "upgrade", "--dir", _dir, "--latest", "--to", "1.5.0")).Should().Be(2);
        (await cli.RunAsync("server", "upgrade", "--dir", _dir, "--to", "1.5.0", "--allow-cli-mismatch")).Should().Be(2);

        cli.Handler.Requests.Should().NotContain(r => r.RequestUri!.Host == "api.github.com");
        runner.Calls.Should().BeEmpty();
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(before);
    }

    [Fact]
    public async Task Upgrade_to_latest_refuses_a_foreign_image_without_asking_github()
    {
        using var cli = new TestCli(GitHubAndHealthy());
        (await cli.LoginDevAsync()).Should().Be(0);
        await InitAsync(cli); // dev CLI: xrmd:local
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "upgrade", "--dir", _dir, "--to", "latest")).Should().Be(5);

        cli.Handler.Requests.Should().NotContain(r => r.RequestUri!.Host == "api.github.com");
    }

    [Fact]
    public async Task Versions_lists_releases_newest_first_with_installed_and_cli_marks()
    {
        var (cli, _, _) = await InstalledAsync("0.1.0-preview.9");
        using var _cli = cli;

        (await cli.RunAsync("server", "versions", "--dir", _dir, "-o", "json")).Should().Be(0, cli.Err.ToString());

        using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
        var rows = json.RootElement.EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("version").GetString()).Should()
            .Equal("0.1.0-preview.10", "0.1.0-preview.9", "0.1.0-preview.4");
        rows[0].GetProperty("latest").GetBoolean().Should().BeTrue();
        rows[0].GetProperty("published").GetString().Should().Be("2026-11-25");
        rows[1].GetProperty("cli").GetBoolean().Should().BeTrue();
        rows[2].GetProperty("installed").GetBoolean().Should().BeTrue();
        rows.Count(r => r.GetProperty("installed").GetBoolean()).Should().Be(1);
    }

    /// <summary>Runs <paramref name="onPull"/> when <c>docker compose pull</c> is asked for, then delegates.</summary>
    private sealed class PullSpy(IProcessRunner inner, Action onPull) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            if (spec.Arguments.Contains("pull")) onPull();
            return inner.RunAsync(spec, cancellationToken);
        }
    }

    [Fact]
    public async Task Init_writes_compose_and_env_with_random_secrets()
    {
        using var cli = new TestCli();
        await InitAsync(cli);
        var env = ReadEnv(_dir);

        env["AUTH_DEV_SIGNING_KEY"].Should().HaveLength(48);
        var password = env["SA_PASSWORD"];
        password.Should().MatchRegex("[A-Z]").And.MatchRegex("[a-z]").And.MatchRegex("[0-9]").And.MatchRegex("[^A-Za-z0-9]");
        password.Length.Should().BeGreaterOrEqualTo(16);
        env["AUTH_MODE"].Should().Be("Development");
        File.Exists(Path.Combine(_dir, "docker-compose.yml")).Should().BeTrue();

        // Secrets are never printed; only where the file went.
        cli.Out.ToString().Should().NotContain(password).And.NotContain(env["AUTH_DEV_SIGNING_KEY"]).And.Contain(".env");

        // A second init generates different secrets.
        var second = Path.Combine(_dir, "second");
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", second)).Should().Be(0);
        ReadEnv(second)["SA_PASSWORD"].Should().NotBe(password);
    }

    /// <summary>nuget.org with the two pinned packages (the workflow SDK is downloaded with the Dataverse SDK, plan 2.14 W18).</summary>
    private static FakeHandler NupkgHandler() => new(request => request.RequestUri!.ToString() switch
    {
        Xrmd.Abstractions.Sdk.SdkPackage.DownloadUrl =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(SdkCommandsTests.Nupkg)) },
        Xrmd.Abstractions.Sdk.SdkPackage.Workflow.DownloadUrl =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(SdkInstallerTests.WorkflowNupkg)) },
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });

    [Fact]
    public async Task Init_never_installs_without_acceptance()
    {
        // No terminal and no flag: skipped, with the command to run later; nothing is downloaded or written.
        using var cli = new TestCli { Handler = NupkgHandler() };
        await InitWithoutSdkAsync(cli);
        Directory.Exists(Path.Combine(_dir, "sdk")).Should().BeFalse();
        ((FakeHandler)cli.Handler).Requests.Should().BeEmpty();
        cli.Err.ToString().Should().Contain($"xrmd sdk install --dir {_dir}");

        // A terminal: the first question's default is no.
        var declined = Path.Combine(_dir, "declined");
        using var tty = new TestCli { Handler = NupkgHandler(), IsInputTty = true, Input = "\n" };
        (await tty.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", declined)).Should().Be(0, tty.Err.ToString());
        tty.Err.ToString().Should().Contain("Install the Microsoft SDK now? [y/N]");
        Directory.Exists(Path.Combine(declined, "sdk")).Should().BeFalse();
        ((FakeHandler)tty.Handler).Requests.Should().BeEmpty();

        // Yes to installing, no to Microsoft's terms: the same acceptance prompt as `sdk install`, nothing installed, init still done.
        var refused = Path.Combine(_dir, "refused");
        using var terms = new TestCli { Handler = NupkgHandler(), IsInputTty = true, Input = "y\nn\n" };
        (await terms.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", refused)).Should().Be(0, terms.Err.ToString());
        terms.Err.ToString().Should().Contain("Do you accept Microsoft's license terms? [y/N]").And.Contain($"xrmd sdk install --dir {refused}");
        Directory.Exists(Path.Combine(refused, "sdk")).Should().BeFalse();
        File.Exists(Path.Combine(refused, ".env")).Should().BeTrue();
        ((FakeHandler)terms.Handler).Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Init_with_flag_installs_into_the_installation()
    {
        using var cli = new TestCli { Handler = NupkgHandler() };
        await InitWithoutSdkAsync(cli, "--accept-microsoft-license");
        var folder = Path.Combine(_dir, "sdk");
        foreach (var dll in Xrmd.Abstractions.Sdk.SdkPackage.Assemblies.Append(Xrmd.Abstractions.Sdk.SdkPackage.Workflow.AssemblyFileName))
            File.Exists(Path.Combine(folder, dll)).Should().BeTrue(dll);
        Xrmd.Abstractions.Sdk.SdkFolder.TryRead(folder)!.AcceptedBy.Should().Be("flag");
        Directory.Exists(Path.Combine(cli.ConfigDir, "sdk")).Should().BeFalse("init installs into the installation only");

        // At a terminal, yes and yes: accepted by the prompt.
        var second = Path.Combine(_dir, "second");
        using var tty = new TestCli { Handler = NupkgHandler(), IsInputTty = true, Input = "y\ny\n" };
        (await tty.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", second)).Should().Be(0, tty.Err.ToString());
        Xrmd.Abstractions.Sdk.SdkFolder.TryRead(Path.Combine(second, "sdk"))!.AcceptedBy.Should().Be("prompt");
    }

    [Fact]
    public async Task Up_refuses_without_sdk()
    {
        using var cli = new TestCli();
        await InitWithoutSdkAsync(cli);
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(1);

        runner.Calls.Should().BeEmpty("no container starts without the SDK");
        cli.Err.ToString().Should().Contain(Xrmd.Abstractions.Sdk.SdkPackage.MissingMessage(Path.Combine(_dir, "sdk")))
            .And.Contain($"xrmd sdk install --dir {_dir}");

        FakeSdk.Install(_dir);
        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Should().ContainSingle();

        // A directory without compose file and .env is still the usage error of `server init` first.
        (await cli.RunAsync("server", "up", "--dir", Path.Combine(_dir, "nothing"))).Should().Be(2);
    }

    [Fact]
    public async Task Org_add_mounts_the_sdk_folder()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme");
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        var services = ComposeYaml.Resolve(_dir);
        services["xrmd-orbit"].Volumes.Should().Contain("./sdk:/var/lib/xrmd/sdk:ro");
        services["xrmd-acme"].Volumes.Should().Contain("./sdk:/var/lib/xrmd/sdk:ro");
    }

    [Fact]
    public async Task Init_refuses_to_overwrite_without_force()
    {
        using var cli = new TestCli();
        await InitAsync(cli);
        var first = ReadEnv(_dir)["SA_PASSWORD"];

        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir)).Should().Be(5);
        ReadEnv(_dir)["SA_PASSWORD"].Should().Be(first);

        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir, "--force")).Should().Be(0);
        ReadEnv(_dir)["SA_PASSWORD"].Should().Be(first, "init --force keeps the SA password of the existing .env (I1)");
    }

    [Fact]
    public async Task Init_force_over_an_existing_env_keeps_the_sa_password_and_signing_key()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme");
        var before = ReadEnv(_dir);

        // Over a multi-org installation, e.g. to change the list of organizations.
        await InitAsync(cli, "--org", "zenith", "--org", "orbit", "--force");
        var after = ReadEnv(_dir);
        after["SA_PASSWORD"].Should().Be(before["SA_PASSWORD"]);
        after["AUTH_DEV_SIGNING_KEY"].Should().Be(before["AUTH_DEV_SIGNING_KEY"]);
        after["XRMD_ORGS"].Should().Be("zenith,orbit");
        cli.Out.ToString().Should().Contain("Kept SA_PASSWORD and AUTH_DEV_SIGNING_KEY").And.NotContain(before["SA_PASSWORD"]);

        // Over a legacy installation (M2 re-create): the bundled SQL Server's data volume keeps the legacy sa password.
        var legacy = Path.Combine(_dir, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, ".env"),
            "SA_PASSWORD=Legacy-Pa55word!\nAUTH_MODE=Development\nAUTH_DEV_SIGNING_KEY=legacysigningkey0123456789\nXRMD_PORT=5000\nSEED_ORG_NAME=xrmd\n");
        File.WriteAllText(Path.Combine(legacy, "docker-compose.yml"), "# Generated by `xrmd server init`. Start with: xrmd server up\nservices:\n  xrmd:\n    image: x\n");
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", legacy, "--org", "setup", "--force")).Should().Be(0, cli.Err.ToString());
        var migrated = ReadEnv(legacy);
        migrated["SA_PASSWORD"].Should().Be("Legacy-Pa55word!");
        migrated["AUTH_DEV_SIGNING_KEY"].Should().Be("legacysigningkey0123456789");
        migrated["XRMD_ORGS"].Should().Be("setup");

        // A new directory still gets fresh secrets; Entra has no signing key to keep.
        var entra = Path.Combine(_dir, "entra");
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", entra, "--org", "acme")).Should().Be(0);
        ReadEnv(entra)["SA_PASSWORD"].Should().NotBe(before["SA_PASSWORD"]);
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", entra, "--org", "acme", "--force", "--auth", "entra", "--tenant-id", Guid.NewGuid().ToString(),
            "--admin-object-id", Guid.NewGuid().ToString())).Should().Be(0, cli.Err.ToString());
        ReadEnv(entra)["AUTH_DEV_SIGNING_KEY"].Should().BeEmpty();
    }

    [Fact]
    public async Task Up_and_upgrade_remove_orphans_of_a_multi_org_installation_only()
    {
        // After a re-create, the legacy `xrmd` service is no longer in the compose file: its container must not keep serving.
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, """{"status":"Healthy"}"""));
        await InitAsync(cli, "--org", "acme");
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        cli.Clock = new AdvancingClock();

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Select(c => string.Join(' ', c.Arguments.Skip(5)))
            .Should().Equal("up -d --remove-orphans", "pull", "up -d --remove-orphans");

        // A legacy directory is refused (B7): its compose file does not mount the SDK folder.
        var legacy = Path.Combine(_dir, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, ".env"), "SA_PASSWORD=x\n");
        File.WriteAllText(Path.Combine(legacy, "docker-compose.yml"), "services:\n  xrmd:\n    image: x\n");
        runner.Calls.Clear();
        (await cli.RunAsync("server", "up", "--dir", legacy)).Should().Be(1, "a legacy directory is refused: it does not mount the SDK folder");
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Legacy_message_describes_the_steps_that_work()
    {
        var message = Xrmd.Cli.Server.Installation.LegacyMessage("/srv/xrmd");
        foreach (var step in new[]
        {
            "xrmd org backup xrmverse", "xrmd server down --dir /srv/xrmd", "(without --volumes)",
            "xrmd server init --org setup --force --dir /srv/xrmd", "keeps SA_PASSWORD", "xrmd server up --dir /srv/xrmd",
            "xrmd org restore <name> --file", "SEED_ORG_NAME", "xrmd server org add <name>",
        })
            message.Should().Contain(step);
        message.IndexOf("server down", StringComparison.Ordinal).Should().BeLessThan(message.IndexOf("server init", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Init_entra_requires_tenant_id()
    {
        using var cli = new TestCli();
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir, "--auth", "entra", "--admin-object-id", Guid.NewGuid().ToString())).Should().Be(2);
        cli.Err.ToString().Should().Contain("--tenant-id");
        File.Exists(Path.Combine(_dir, ".env")).Should().BeFalse();

        var tenant = Guid.NewGuid().ToString();
        await InitAsync(cli, "--auth", "entra", "--tenant-id", tenant, "--admin-object-id", Guid.NewGuid().ToString());
        var env = ReadEnv(_dir);
        env["AUTH_MODE"].Should().Be("Entra");
        env["AUTH_TENANT_ID"].Should().Be(tenant);
        env["AUTH_DEV_SIGNING_KEY"].Should().BeEmpty();
        env.Should().ContainKey("UI_ENTRA_CLIENT_ID").WhoseValue.Should().BeEmpty();
        env.Should().ContainKey("UI_ENTRA_SCOPE").WhoseValue.Should().BeEmpty();
        var compose = File.ReadAllText(Path.Combine(_dir, "docker-compose.yml"));
        compose.Should().Contain("Ui__Entra__ClientId: ${UI_ENTRA_CLIENT_ID:-}");
        compose.Should().Contain("Ui__Entra__Scope: ${UI_ENTRA_SCOPE:-}");
    }

    [Fact]
    public async Task Init_dev_refuses_tenant_id()
    {
        using var cli = new TestCli();
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir, "--tenant-id", Guid.NewGuid().ToString())).Should().Be(2);
    }

    [Fact]
    public async Task Generated_compose_mounts_backups_volume_for_sql_server()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        XrmdServices(File.ReadAllText(Path.Combine(_dir, "docker-compose.yml"))).Should().Equal("xrmd-acme", "xrmd-zenith");
        AssertBackupsVolumeWritableBySqlServer(File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")));

        // The repo's own compose file carries the same mount and init step.
        var repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "docker", "docker-compose.yml"))) repo = Path.GetDirectoryName(repo);
        repo.Should().NotBeNull();
        AssertBackupsVolumeWritableBySqlServer(File.ReadAllText(Path.Combine(repo!, "docker", "docker-compose.yml")));
    }

    /// <summary>
    /// A named volume on a path the mssql image does not have is created root-owned (0755) while SQL Server runs as
    /// uid 10001, so BACKUP fails with OS error 5 (verified with docker). The compose must chown the volume in a one-shot
    /// root service that SQL Server waits for.
    /// </summary>
    private static void AssertBackupsVolumeWritableBySqlServer(string compose)
    {
        compose = compose.Replace("\r\n", "\n");
        compose.Should().MatchRegex(@"(?m)^volumes:\s*\n(\s+.*\n)*\s+backups:");

        var sql = ServiceBlock(compose, "sqlserver");
        sql.Should().Contain("- backups:/var/opt/mssql/backup");
        sql.Should().MatchRegex(@"depends_on:\s*\n\s+backups-init:\s*\n\s+condition: service_completed_successfully");

        var init = ServiceBlock(compose, "backups-init");
        init.Should().Contain("- backups:/var/opt/mssql/backup");
        init.Should().MatchRegex(@"user: ""0:0""");
        // SQL Server (uid 10001) and xrmd ($APP_UID, which writes the .files.tar) both write the directory: owner mssql, group 1654, setgid and group-writable.
        init.Should().Contain(@"entrypoint: [""sh"", ""-c"", ""chown 10001:1654 /var/opt/mssql/backup && chmod 2770 /var/opt/mssql/backup""]");

        // Every xrmd service mounts the same volume at the same path (the archive of an org backup is written beside the .bak) and waits for the init.
        foreach (var service in XrmdServices(compose))
        {
            var xrmd = ServiceBlock(compose, service);
            xrmd.Should().Contain("- backups:/var/opt/mssql/backup");
            xrmd.Should().Contain(@"user: ""1654:1654""");
            xrmd.Should().MatchRegex(@"backups-init:\s*\n\s+condition: service_completed_successfully");
        }
    }

    /// <summary>The xrmd services of a compose file: <c>xrmd-&lt;org&gt;</c> (multi-org) or the single <c>xrmd</c> (the repo's own file).</summary>
    private static List<string> XrmdServices(string compose)
    {
        var services = compose.Replace("\r\n", "\n").Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^  (xrmd(-[a-z0-9]+)?):$"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();
        services.Should().NotBeEmpty("the compose file must define at least one xrmd service");
        return services;
    }

    [Fact]
    public async Task Generated_compose_gives_xrmd_a_files_volume_and_the_files_settings()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        var repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "docker", "docker-compose.yml"))) repo = Path.GetDirectoryName(repo);
        repo.Should().NotBeNull();

        foreach (var (compose, env) in new[]
        {
            (File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")), File.ReadAllText(Path.Combine(_dir, ".env"))),
            (File.ReadAllText(Path.Combine(repo!, "docker", "docker-compose.yml")), File.ReadAllText(Path.Combine(repo!, "docker", ".env.example"))),
        })
        {
            var text = compose.Replace("\r\n", "\n");
            text.Should().MatchRegex(@"(?m)^volumes:\s*\n(\s+.*\n)*\s+files:");
            foreach (var service in XrmdServices(text))
            {
                var xrmd = ServiceBlock(text, service);
                xrmd.Should().Contain("- files:/var/lib/xrmd/files");
                foreach (var key in new[]
                {
                    "Files__Store: ${FILES_STORE:-}", "Files__FileSystem__Root: ${FILES_ROOT:-}", "Files__S3__ServiceUrl: ${FILES_S3_SERVICE_URL:-}",
                    "Files__S3__Bucket: ${FILES_S3_BUCKET:-}", "Files__S3__AccessKey: ${FILES_S3_ACCESS_KEY:-}",
                    "Files__S3__SecretKey: ${FILES_S3_SECRET_KEY:-}", "Files__S3__Region: ${FILES_S3_REGION:-}",
                    "Files__S3__ForcePathStyle: ${FILES_S3_FORCE_PATH_STYLE:-}",
                    "Files__Azure__ConnectionString: ${FILES_AZURE_CONNECTION_STRING:-}", "Files__Azure__ServiceUri: ${FILES_AZURE_SERVICE_URI:-}",
                    "Files__Azure__Container: ${FILES_AZURE_CONTAINER:-}",
                    "AZURE_CLIENT_ID: ${AZURE_CLIENT_ID:-}",
                })
                    xrmd.Should().Contain(key);
            }
            // Empty by default: a setting left empty is ignored by the host, so the file-system store at its default root applies.
            foreach (var name in new[] { "FILES_STORE", "FILES_ROOT", "FILES_S3_SERVICE_URL", "FILES_S3_BUCKET", "FILES_S3_ACCESS_KEY", "FILES_S3_SECRET_KEY", "FILES_S3_REGION", "FILES_S3_FORCE_PATH_STYLE", "FILES_AZURE_CONNECTION_STRING", "FILES_AZURE_SERVICE_URI", "FILES_AZURE_CONTAINER", "AZURE_CLIENT_ID" })
                env.Should().MatchRegex($@"(?m)^(# )?{name}=\s*$");
        }
    }

    [Fact]
    public void Repository_compose_defaults_mssql_pid_to_express_and_requires_accept_eula()
    {
        // P5: the "without the CLI" path runs Express unless docker/.env says otherwise.
        var repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "docker", "docker-compose.yml"))) repo = Path.GetDirectoryName(repo);
        repo.Should().NotBeNull();
        var sqlserver = ServiceBlock(File.ReadAllText(Path.Combine(repo!, "docker", "docker-compose.yml")).Replace("\r\n", "\n"), "sqlserver");
        // #16: ACCEPT_EULA is the user's, from docker/.env; the example leaves it empty.
        sqlserver.Should().Contain("MSSQL_PID: ${MSSQL_PID:-Express}").And.Contain("ACCEPT_EULA: ${ACCEPT_EULA:?").And.NotContain("ACCEPT_EULA: \"Y\"");
        File.ReadAllLines(Path.Combine(repo!, "docker", ".env.example")).Should().Contain("MSSQL_PID=Express").And.Contain("ACCEPT_EULA=");
    }

    private static string ServiceBlock(string compose, string service)
    {
        var lines = compose.Split('\n');
        var start = Array.FindIndex(lines, l => l == $"  {service}:");
        start.Should().BeGreaterOrEqualTo(0, $"the compose file must define service '{service}'");
        var block = lines.Skip(start + 1)
            .TakeWhile(l => l.Length == 0 || l.StartsWith("   ", StringComparison.Ordinal) || l.TrimStart().StartsWith('#'));
        return string.Join('\n', block);
    }

    [Fact]
    public async Task Up_runs_docker_compose_up_detached()
    {
        using var cli = new TestCli();
        await InitAsync(cli);
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        var call = runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("docker");
        call.Arguments.Should().Equal("compose", "-f", Path.Combine(_dir, "docker-compose.yml"), "--env-file", Path.Combine(_dir, ".env"), "up", "-d", "--remove-orphans");
    }

    [Fact]
    public async Task Up_re_renders_the_compose_file_from_env_before_starting()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme");
        var envPath = Path.Combine(_dir, ".env");
        File.WriteAllText(envPath, File.ReadAllText(envPath).Replace("XRMD_ORGS=acme",
            $"XRMD_ORGS=acme,orbit\nORG_ORBIT_PORT=5009\nORG_ORBIT_ADMIN_OBJECT_ID={DevObjectId}\nORG_ORBIT_AUDIENCE="));
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        ComposeYaml.Resolve(_dir).Keys.Should().Contain("xrmd-orbit");
        runner.Calls.Single().Arguments.Skip(5).Should().Equal("up", "-d", "--remove-orphans");
    }

    [Fact]
    public async Task Up_and_upgrade_refuse_an_inconsistent_env_or_a_foreign_compose_before_docker()
    {
        using var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0);
        await InitAsync(cli, "--org", "acme");
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        var envPath = Path.Combine(_dir, ".env");
        var composePath = Path.Combine(_dir, "docker-compose.yml");
        var env = File.ReadAllText(envPath);

        File.WriteAllText(envPath, env + "ORG_GHOST_PORT=5010\n");
        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(5);
        cli.Err.ToString().Should().Contain("ORG_GHOST_PORT");
        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(5);

        File.WriteAllText(envPath, env);
        File.WriteAllText(composePath, "services:\n  mine:\n    image: x\n");
        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(5);
        cli.Err.ToString().Should().Contain(composePath);
        File.ReadAllText(composePath).Should().Be("services:\n  mine:\n    image: x\n");

        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Init_stores_admin_object_ids_in_canonical_form()
    {
        using var cli = new TestCli();
        var oid = Guid.NewGuid();
        await InitAsync(cli, "--org", "acme", "--admin-object-id", oid.ToString("B").ToUpperInvariant());

        ReadEnv(_dir)["ORG_ACME_ADMIN_OBJECT_ID"].Should().Be(oid.ToString("D"));
        new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load().Profiles["acme"].ObjectId.Should().Be(oid.ToString("D"));
    }

    [Fact]
    public async Task Down_and_logs_keep_working_on_a_legacy_installation()
    {
        // M2: a Plan 6 directory (single xrmd service, no XRMD_ORGS) is driven by docker compose unchanged.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ".env"), "SA_PASSWORD=x\nXRMD_PORT=5000\nSEED_ORG_NAME=xrmd\n");
        File.WriteAllText(Path.Combine(_dir, "docker-compose.yml"), "# Generated by `xrmd server init`. Start with: xrmd server up\nservices:\n  xrmd:\n    image: x\n");
        using var cli = new TestCli();
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "logs", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("server", "down", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        runner.Calls.Select(c => string.Join(' ', c.Arguments.Skip(5))).Should().Equal("logs --no-color", "down");
    }

    [Fact]
    public async Task Up_refuses_a_legacy_installation_and_names_the_migration()
    {
        // The legacy compose file never mounts the SDK folder: refused with or without <dir>/sdk.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ".env"), "SA_PASSWORD=x\nXRMD_PORT=5000\nSEED_ORG_NAME=xrmd\n");
        File.WriteAllText(Path.Combine(_dir, "docker-compose.yml"), "# Generated by `xrmd server init`. Start with: xrmd server up\nservices:\n  xrmd:\n    image: x\n");
        using var cli = new TestCli();
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        foreach (var withSdk in new[] { false, true })
        {
            if (withSdk) FakeSdk.Install(_dir);
            foreach (var command in new[] { "up", "upgrade" })
            {
                (await cli.RunAsync("server", command, "--dir", _dir)).Should().Be(1, $"{command}, sdk folder: {withSdk}");
                cli.Err.ToString().Should().Contain("The Microsoft SDK is not installed")
                    .And.Contain("old single-service layout, which does not mount the SDK folder")
                    .And.Contain($"xrmd server init --force --dir {_dir}").And.Contain($"xrmd sdk install --dir {_dir}");
            }
        }
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Upgrade_refuses_without_sdk()
    {
        using var cli = new TestCli();
        await InitWithoutSdkAsync(cli);
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(1);

        runner.Calls.Should().BeEmpty("no pull and no compose call");
        cli.Err.ToString().Should().Contain(Xrmd.Abstractions.Sdk.SdkPackage.MissingMessage(Path.Combine(_dir, "sdk")))
            .And.Contain($"xrmd sdk install --dir {_dir}");
    }

    [Fact]
    public async Task Up_without_init_is_a_usage_error()
    {
        // A missing local input file is 2, as for every other command; 4 is only a server 404.
        using var cli = new TestCli();
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(2);
        cli.Err.ToString().Should().Contain("xrmd server init");
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Down_with_volumes_needs_confirmation()
    {
        using var cli = new TestCli();
        await InitAsync(cli);
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "down", "--dir", _dir, "--volumes")).Should().Be(2);
        runner.Calls.Should().BeEmpty();

        (await cli.RunAsync("server", "down", "--dir", _dir, "--volumes", "--yes")).Should().Be(0);
        runner.Calls.Single().Arguments.Skip(5).Should().Equal("down", "--volumes");

        runner.Calls.Clear();
        (await cli.RunAsync("server", "down", "--dir", _dir)).Should().Be(0);
        runner.Calls.Single().Arguments.Skip(5).Should().Equal("down");
    }

    [Fact]
    public async Task Logs_passes_follow_and_tail_through_and_streams_output()
    {
        using var cli = new TestCli();
        await InitAsync(cli);
        var runner = new FakeProcessRunner { OutputLines = ["xrmd  | started"] };
        cli.Process = runner;

        (await cli.RunAsync("server", "logs", "--dir", _dir, "--follow", "--tail", "50")).Should().Be(0, cli.Err.ToString());

        runner.Calls.Single().Arguments.Skip(5).Should().Equal("logs", "--no-color", "--follow", "--tail", "50");
        cli.Out.ToString().Should().Contain("xrmd  | started");
    }

    [Fact]
    public async Task Status_prints_health()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK,
            """{"status":"Healthy","version":"1.2.3","organization":"xrmd","database":"ok","schemaVersion":7,"pendingMigrations":0,"asyncQueue":{"waiting":0,"inProgress":0},"authMode":"Development","sdkVersion":"9.2.49.17970"}"""));
        (await cli.LoginDevAsync()).Should().Be(0);

        (await cli.RunAsync("server", "status")).Should().Be(0, cli.Err.ToString());

        cli.Out.ToString().Should().Contain("Healthy").And.Contain("1.2.3");
        cli.Out.ToString().Should().Contain("9.2.49.17970", "the SDK version of the health report is shown as is (Plan 12, B8)");
        cli.Handler.Requests.Last().RequestUri!.AbsolutePath.Should().Be("/admin/health");
    }

    // ---- Several organizations (Task 4) ----

    private static string Health(string org, string status = "Healthy") =>
        $$"""{"status":"{{status}}","version":"1.2.3","organization":"{{org}}","database":"ok"}""";

    /// <summary>A server per port: 5001 acme, 5002 zenith, 5003 orbit; <paramref name="byPort"/> answers or throws.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> ByPort(Func<int, HttpRequestMessage, HttpResponseMessage> byPort) =>
        request => byPort(request.RequestUri!.Port, request);

    private static HttpResponseMessage Unreachable() => throw new HttpRequestException("Connection refused");

    private static void RemoveProfile(TestCli cli, string name)
    {
        var store = new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir);
        var config = store.Load();
        config.Profiles.Remove(name);
        store.Save(config);
    }

    [Fact]
    public async Task Status_reports_every_org_of_the_installation()
    {
        using var cli = new TestCli(ByPort((port, _) => FakeHandler.Json(HttpStatusCode.OK, Health(port == 5001 ? "acme" : "zenith"))));
        await InitAsync(cli, "--org", "acme", "--org", "zenith");

        (await cli.RunAsync("server", "status", "--dir", _dir, "-o", "json")).Should().Be(0, cli.Err.ToString());

        using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
        var rows = json.RootElement.EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("org").GetString()).Should().Equal("acme", "zenith");
        rows.Select(r => r.GetProperty("url").GetString()).Should().Equal("http://localhost:5001/", "http://localhost:5002/");
        rows.Select(r => r.GetProperty("health").GetProperty("organization").GetString()).Should().Equal("acme", "zenith");
        cli.Handler.Requests.Select(r => r.RequestUri!.ToString())
            .Should().Equal("http://localhost:5001/admin/health", "http://localhost:5002/admin/health");
        cli.Handler.Requests.Should().OnlyContain(r => r.Headers.Authorization != null);

        // Table: one row per org.
        (await cli.RunAsync("server", "status", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        var lines = cli.Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        lines[0].Should().MatchRegex(@"^org\s+url\s+status$");
        lines.Skip(1).Should().HaveCount(2);
        lines[1].Should().MatchRegex(@"^acme\s+http://localhost:5001/\s+Healthy$");
        lines[2].Should().MatchRegex(@"^zenith\s+http://localhost:5002/\s+Healthy$");
    }

    [Fact]
    public async Task Status_reports_an_unreachable_org_without_failing_the_others()
    {
        using var cli = new TestCli(ByPort((port, _) => port switch
        {
            5001 => FakeHandler.Json(HttpStatusCode.OK, Health("acme")),
            5002 => Unreachable(),
            _ => FakeHandler.Json(HttpStatusCode.ServiceUnavailable, Health("orbit", "Unhealthy")),
        }));
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--org", "orbit", "--org", "adventure");
        RemoveProfile(cli, "adventure");

        (await cli.RunAsync("server", "status", "--dir", _dir, "-o", "json")).Should().Be(1);

        using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
        var rows = json.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("org").GetString()!);
        rows["acme"].GetProperty("health").GetProperty("status").GetString().Should().Be("Healthy");
        rows["zenith"].GetProperty("error").GetString().Should().Contain("Cannot reach http://localhost:5002/");
        rows["zenith"].TryGetProperty("health", out _).Should().BeFalse();
        // An unhealthy server answers 503 with its health: reported as such, not as an error.
        rows["orbit"].GetProperty("health").GetProperty("status").GetString().Should().Be("Unhealthy");
        rows["adventure"].GetProperty("error").GetString().Should().Contain("no CLI profile 'adventure'").And.Contain("xrmd auth login --profile adventure");
        rows["adventure"].GetProperty("url").GetString().Should().Be("http://localhost:5004/");
        cli.Err.ToString().Should().Contain("zenith").And.Contain("orbit").And.Contain("adventure").And.NotContain("acme");
        cli.Handler.Requests.Should().HaveCount(3, "an org without a profile is not called");
    }

    [Fact]
    public async Task Doctor_exit_1_names_the_failing_orgs()
    {
        static string Checks(string worker) =>
            $$"""{"checks":[{"name":"database","status":"ok","detail":"fine"},{"name":"worker","status":"{{worker}}","detail":"x"}]}""";
        using var cli = new TestCli(ByPort((port, _) => port switch
        {
            5001 => FakeHandler.Json(HttpStatusCode.OK, Checks("warning")),
            5002 => FakeHandler.Json(HttpStatusCode.OK, Checks("error")),
            _ => Unreachable(),
        }));
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--org", "orbit");

        (await cli.RunAsync("server", "doctor", "--dir", _dir, "-o", "json")).Should().Be(1);

        using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
        var rows = json.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("org").GetString()!);
        rows["acme"].GetProperty("checks").GetArrayLength().Should().Be(2);
        rows["zenith"].GetProperty("checks")[1].GetProperty("status").GetString().Should().Be("error");
        rows["orbit"].GetProperty("error").GetString().Should().Contain("Cannot reach");
        cli.Handler.Requests.Should().OnlyContain(r => r.RequestUri!.AbsolutePath == "/admin/doctor");
        var err = cli.Err.ToString();
        err.Should().Contain("zenith (worker)").And.Contain("orbit").And.NotContain("acme");

        // Table: one row per org with a summary status.
        (await cli.RunAsync("server", "doctor", "--dir", _dir)).Should().Be(1);
        var table = cli.Out.ToString();
        table.Should().MatchRegex(@"(?m)^acme\s+http://localhost:5001/\s+warning: worker\s*$");
        table.Should().MatchRegex(@"(?m)^zenith\s+http://localhost:5002/\s+error: worker\s*$");
        table.Should().MatchRegex(@"(?m)^orbit\s+http://localhost:5003/\s+error: Cannot reach");
    }

    [Fact]
    public async Task Status_and_doctor_time_out_a_hung_org_and_report_the_others()
    {
        using var cli = new TestCli();
        cli.Handler = new FakeHandler(request => request.RequestUri!.AbsolutePath == "/admin/doctor"
            ? FakeHandler.Json(HttpStatusCode.OK, """{"checks":[{"name":"database","status":"ok","detail":"fine"}]}""")
            : FakeHandler.Json(HttpStatusCode.OK, Health("acme")))
        {
            Hang = request => request.RequestUri!.Port == 5002,
        };
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        cli.OrgProbeTimeout = TimeSpan.FromSeconds(1);

        foreach (var verb in new[] { "status", "doctor" })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var run = cli.RunAsync("server", verb, "--dir", _dir, "-o", "json");
            (await run.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(1, verb);
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), $"{verb}: one hung org must not hold the command");

            using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
            var rows = json.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("org").GetString()!);
            rows["zenith"].GetProperty("error").GetString().Should().Be("timed out after 1s", verb);
            if (verb == "status") rows["acme"].GetProperty("health").GetProperty("status").GetString().Should().Be("Healthy");
            else rows["acme"].GetProperty("checks").GetArrayLength().Should().Be(1);
            cli.Err.ToString().Should().Contain("zenith").And.NotContain("acme");
        }
    }

    [Fact]
    public async Task Upgrade_bounds_a_hung_org_by_the_probe_timeout_and_names_it()
    {
        using var cli = new TestCli();
        cli.Handler = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.OK, Health("acme"))) { Hang = r => r.RequestUri!.Port == 5002 };
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        cli.OrgProbeTimeout = TimeSpan.FromMilliseconds(50);
        cli.Process = new FakeProcessRunner();
        cli.Clock = new AdvancingClock();

        var run = cli.RunAsync("server", "upgrade", "--dir", _dir);
        (await run.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(1);

        cli.Err.ToString().Should().Contain("did not become healthy").And.Contain("zenith").And.Contain("(last: timed out after").And.NotContain("acme");
    }

    [Fact]
    public async Task Upgrade_fails_fast_on_an_auth_error()
    {
        using var cli = new TestCli(ByPort((port, _) => port == 5002
            ? FakeHandler.Json(HttpStatusCode.Unauthorized, """{"error":{"code":"0x80040220","message":"The token audience is invalid."}}""")
            : FakeHandler.Json(HttpStatusCode.OK, Health("acme"))));
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        cli.Process = new FakeProcessRunner();
        cli.Clock = new AdvancingClock();

        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(3);

        cli.Err.ToString().Should().Contain("zenith").And.Contain("The token audience is invalid.");
        cli.Handler.Requests.Count(r => r.RequestUri!.Port == 5002).Should().Be(1, "an auth error is not retried until the deadline");
    }

    [Fact]
    public void Help_documents_the_multi_org_output_and_upgrade_targets()
    {
        using var cli = new TestCli();
        var ctx = new CliContext(cli.Env());
        var server = Xrmd.Cli.Commands.ServerCommands.Build(ctx);
        string Help(string name) => server.Subcommands.Single(c => c.Name == name).Description!;
        Help("status").Should().Contain("JSON array").And.Contain("object");
        Help("doctor").Should().Contain("JSON array").And.Contain("object");
        Help("upgrade").Should().Contain("--profile").And.Contain("--url").And.Contain("every organization");
    }

    [Fact]
    public async Task Doctor_of_healthy_orgs_is_exit_0()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, """{"checks":[{"name":"database","status":"ok","detail":"fine"}]}"""));
        await InitAsync(cli, "--org", "acme", "--org", "zenith");

        (await cli.RunAsync("server", "doctor", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().MatchRegex(@"(?m)^zenith\s+http://localhost:5002/\s+ok\s*$");
    }

    [Fact]
    public async Task Status_without_installation_uses_the_current_profile()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, Health("xrmd")));
        (await cli.LoginDevAsync()).Should().Be(0);
        Directory.CreateDirectory(_dir);

        (await cli.RunAsync("server", "status", "--dir", _dir, "-o", "json")).Should().Be(0, cli.Err.ToString());

        using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
        json.RootElement.GetProperty("organization").GetString().Should().Be("xrmd");
        cli.Handler.Requests.Last().RequestUri!.ToString().Should().Be("http://xrmd.test:5000/admin/health");
    }

    [Fact]
    public async Task Status_with_an_explicit_profile_queries_only_that_profile()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, Health("zenith")));
        await InitAsync(cli, "--org", "acme", "--org", "zenith");

        (await cli.RunAsync("server", "status", "--dir", _dir, "--profile", "zenith", "-o", "json")).Should().Be(0, cli.Err.ToString());

        using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
        json.RootElement.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
        cli.Handler.Requests.Should().ContainSingle().Which.RequestUri!.ToString().Should().Be("http://localhost:5002/admin/health");
    }

    [Fact]
    public async Task Upgrade_waits_for_every_org_to_be_healthy()
    {
        var zenithCalls = 0;
        using var cli = new TestCli(ByPort((port, _) => port == 5002 && ++zenithCalls < 3
            ? FakeHandler.Json(HttpStatusCode.ServiceUnavailable, Health("zenith", "Unhealthy"))
            : FakeHandler.Json(HttpStatusCode.OK, Health("x"))));
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        cli.Clock = new AdvancingClock();

        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        runner.Calls.Select(c => string.Join(' ', c.Arguments.Skip(5))).Should().Equal("pull", "up -d --remove-orphans");
        zenithCalls.Should().Be(3);
        cli.Handler.Requests.Count(r => r.RequestUri!.Port == 5001).Should().Be(1, "a healthy org is not polled again");
    }

    [Fact]
    public async Task Upgrade_names_the_org_that_never_becomes_healthy()
    {
        using var cli = new TestCli(ByPort((port, _) => port == 5002
            ? FakeHandler.Json(HttpStatusCode.ServiceUnavailable, Health("zenith", "Unhealthy"))
            : FakeHandler.Json(HttpStatusCode.OK, Health("acme"))));
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        cli.Process = new FakeProcessRunner();
        cli.Clock = new AdvancingClock();

        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(1);

        cli.Err.ToString().Should().Contain("did not become healthy").And.Contain("zenith").And.NotContain("acme")
            .And.Contain("Unhealthy", "the last state of each org is kept");
    }

    [Fact]
    public async Task Upgrade_keeps_the_last_health_when_a_later_probe_times_out()
    {
        var zenithCalls = 0;
        using var cli = new TestCli(ByPort((port, _) => port == 5002
            ? FakeHandler.Json(HttpStatusCode.ServiceUnavailable, Health("zenith", "Unhealthy"))
            : FakeHandler.Json(HttpStatusCode.OK, Health("acme"))));
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        cli.Handler = new FakeHandler(ByPort((port, _) => port == 5002
            ? FakeHandler.Json(HttpStatusCode.ServiceUnavailable, Health("zenith", "Unhealthy"))
            : FakeHandler.Json(HttpStatusCode.OK, Health("acme"))))
        {
            Hang = r => r.RequestUri!.Port == 5002 && ++zenithCalls > 1,
        };
        cli.OrgProbeTimeout = TimeSpan.FromMilliseconds(50);
        cli.Process = new FakeProcessRunner();
        cli.Clock = new AdvancingClock();

        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(1);

        cli.Err.ToString().Should().Contain("zenith (last: status Unhealthy, then timed out after");
    }

    [Fact]
    public async Task Upgrade_reports_an_org_without_profile_and_exits_1()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, Health("x")));
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        RemoveProfile(cli, "zenith");
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        cli.Clock = new AdvancingClock();

        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(1);

        runner.Calls.Select(c => string.Join(' ', c.Arguments.Skip(5))).Should().Equal("pull", "up -d --remove-orphans");
        cli.Handler.Requests.Should().OnlyContain(r => r.RequestUri!.Port == 5001);
        cli.Err.ToString().Should().Contain("zenith").And.Contain("xrmd auth login --profile zenith");
    }

    [Fact]
    public async Task Status_without_profile_is_auth_error()
    {
        using var cli = new TestCli();
        (await cli.RunAsync("server", "status")).Should().Be(3);
    }

    [Theory]
    [InlineData("error", 1)]
    [InlineData("warning", 0)]
    [InlineData("ok", 0)]
    public async Task Doctor_exit_1_on_any_error_check(string status, int expected)
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK,
            $$"""{"checks":[{"name":"database","status":"ok","detail":"fine"},{"name":"worker","status":"{{status}}","detail":"x"}]}"""));
        (await cli.LoginDevAsync()).Should().Be(0);

        (await cli.RunAsync("server", "doctor", "--output", "json")).Should().Be(expected, cli.Err.ToString());

        cli.Out.ToString().Should().Contain("database");
        cli.Handler.Requests.Last().RequestUri!.AbsolutePath.Should().Be("/admin/doctor");
    }

    [Fact]
    public async Task Upgrade_pulls_ups_and_waits_for_health()
    {
        var healthCalls = 0;
        using var cli = new TestCli(_ => ++healthCalls < 3
            ? FakeHandler.Json(HttpStatusCode.ServiceUnavailable, """{"status":"Unhealthy"}""")
            : FakeHandler.Json(HttpStatusCode.OK, """{"status":"Healthy"}"""));
        (await cli.LoginDevAsync()).Should().Be(0);
        healthCalls = 0;
        await InitAsync(cli);
        File.AppendAllText(Path.Combine(_dir, "docker-compose.yml"), "# stale\n");
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        cli.Clock = new AdvancingClock();

        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        runner.Calls.Select(c => string.Join(' ', c.Arguments.Skip(5))).Should().Equal("pull", "up -d --remove-orphans");
        healthCalls.Should().Be(3);
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().NotContain("# stale", "upgrade re-renders compose from .env");
    }

    [Fact]
    public async Task Upgrade_gives_up_when_the_server_never_becomes_healthy()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.ServiceUnavailable, """{"status":"Unhealthy"}"""));
        (await cli.LoginDevAsync()).Should().Be(0);
        await InitAsync(cli);
        cli.Process = new FakeProcessRunner();
        cli.Clock = new AdvancingClock();

        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(1);
        cli.Err.ToString().Should().Contain("did not become healthy");
    }

    [Fact]
    public async Task Docker_missing_is_exit_1_with_hint()
    {
        using var cli = new TestCli();
        await InitAsync(cli);
        cli.Process = new FakeProcessRunner { Throw = new ProcessNotFoundException("docker") };

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(1);
        cli.Err.ToString().Should().Contain("Install Docker");
    }

    [Fact]
    public async Task A_failing_docker_command_is_exit_1_with_its_stderr()
    {
        using var cli = new TestCli();
        await InitAsync(cli);
        cli.Process = new FakeProcessRunner { Result = new ProcessResult(17, "", "boom") };

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(1);
        cli.Err.ToString().Should().Contain("boom");
    }

    [Fact]
    public async Task Init_with_two_orgs_writes_two_services_ports_5001_5002_and_databases()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith");

        var services = ComposeYaml.Resolve(_dir);
        services.Keys.Where(k => k.StartsWith("xrmd", StringComparison.Ordinal)).Should().Equal("xrmd-acme", "xrmd-zenith");
        services.Keys.Should().Contain(["sqlserver", "backups-init"]);
        foreach (var (org, port) in new[] { ("acme", 5001), ("zenith", 5002) })
        {
            var service = services[$"xrmd-{org}"];
            var environment = service.Environment;
            environment["ConnectionStrings__Org"].Should().StartWith("Server=sqlserver;").And.Contain($";Database={org};");
            environment["Seed__OrgName"].Should().Be(org);
            environment["Seed__AdminObjectId"].Should().Be(DevObjectId);
            environment["Auth__Audience"].Should().Be($"http://localhost:{port}/");
            environment.Should().NotContainKey("Admin__FilesBackupDirectory");
            service.Ports.Should().Equal($"127.0.0.1:{port}:5000");
        }
    }

    private const string DevObjectId = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task Init_writes_env_keys_per_org_and_XRMD_ORGS_in_order()
    {
        using var cli = new TestCli();
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await InitAsync(cli, "--org", "zenith", "--org", "acme", "--port", "6000", "--admin-object-id", first, "--admin-object-id", second);

        var env = ReadEnv(_dir);
        env["XRMD_ORGS"].Should().Be("zenith,acme");
        env["ORG_ZENITH_PORT"].Should().Be("6000");
        env["ORG_ZENITH_ADMIN_OBJECT_ID"].Should().Be(first);
        env["ORG_ACME_PORT"].Should().Be("6001");
        env["ORG_ACME_ADMIN_OBJECT_ID"].Should().Be(second);
        env["SQL_CONNECTION"].Should().BeEmpty();
        env["AUTH_DEV_SIGNING_KEY"].Should().HaveLength(48);
        // The single-org keys are gone: every per-org value has its own ORG_<ORG>_* key.
        env.Keys.Should().NotContain(["AUTH_AUDIENCE", "XRMD_PORT", "SEED_ORG_NAME", "SEED_ADMIN_OBJECT_ID"]);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(Path.Combine(_dir, ".env")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().NotContain("# stale", "upgrade re-renders compose from .env");
    }

    [Fact]
    public async Task Init_creates_a_dev_profile_per_org_and_selects_the_first()
    {
        using var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0);
        var oid = Guid.NewGuid().ToString();
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--admin-object-id", oid, "--admin-object-id", DevObjectId);

        var config = new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load();
        config.CurrentProfile.Should().Be("acme");
        config.Profiles.Keys.Should().Contain(["default", "acme", "zenith"]);
        config.Profiles["acme"].Should().BeEquivalentTo(new Xrmd.Cli.Config.Profile
        {
            Url = "http://localhost:5001/", Mode = "dev", ObjectId = oid, SigningKeyEnv = "XRMD_DEV_SIGNING_KEY",
        });
        config.Profiles["zenith"].Should().BeEquivalentTo(new Xrmd.Cli.Config.Profile
        {
            Url = "http://localhost:5002/", Mode = "dev", ObjectId = DevObjectId, SigningKeyEnv = "XRMD_DEV_SIGNING_KEY",
        });
        cli.Out.ToString().Should().Contain("acme").And.Contain("http://localhost:5002/").And.Contain("XRMD_DEV_SIGNING_KEY");
    }

    [Fact]
    public async Task Init_entra_creates_no_profile_and_prints_the_login_command()
    {
        using var cli = new TestCli();
        var tenant = Guid.NewGuid().ToString();
        await InitAsync(cli, "--auth", "entra", "--tenant-id", tenant, "--org", "acme", "--org", "zenith",
            "--admin-object-id", Guid.NewGuid().ToString(), "--admin-object-id", Guid.NewGuid().ToString());

        new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load().Profiles.Should().BeEmpty();
        var output = cli.Out.ToString();
        output.Should().Contain("xrmd auth login --profile acme --url http://localhost:5001/");
        output.Should().Contain("xrmd auth login --profile zenith --url http://localhost:5002/");
        output.Should().Contain($"--tenant-id {tenant}");
        output.Should().Contain("audience http://localhost:5001/").And.Contain("ORG_ACME_AUDIENCE");
        output.Should().Contain("the CLI's `auth login` then cannot sign in");

        // Entra needs one admin object id per org.
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", Path.Combine(_dir, "x"), "--auth", "entra", "--tenant-id", tenant,
            "--org", "acme", "--org", "zenith", "--admin-object-id", Guid.NewGuid().ToString())).Should().Be(2);
    }

    [Theory]
    [InlineData("Acme")]
    [InlineData("ACME")]
    [InlineData("1abc")]
    [InlineData("a-b")]
    [InlineData("a_b")]
    [InlineData("abcdefghijabcdefghijabcdefghijabc")] // 33 characters
    [InlineData("master")]
    [InlineData("tempdb")]
    [InlineData("model")]
    [InlineData("msdb")]
    public async Task Init_rejects_invalid_and_reserved_org_names(string name)
    {
        using var cli = new TestCli();
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir, "--org", "acme", "--org", name)).Should().Be(2);

        var error = cli.Err.ToString();
        error.Should().Contain($"'{name}'");
        if (name is "master" or "tempdb" or "model" or "msdb") error.Should().Contain("reserved");
        else error.Should().Contain("^[a-z][a-z0-9]{0,31}$");
        // Never lower-cased silently: nothing is written, no profile is created.
        File.Exists(Path.Combine(_dir, ".env")).Should().BeFalse();
        File.Exists(Path.Combine(_dir, "docker-compose.yml")).Should().BeFalse();
        new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load().Profiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Init_accepts_a_32_character_org_name()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "abcdefghijabcdefghijabcdefghij12");
        ReadEnv(_dir)["XRMD_ORGS"].Should().Be("abcdefghijabcdefghijabcdefghij12");
    }

    [Fact]
    public async Task Init_rejects_duplicate_orgs_and_mismatched_admin_object_ids()
    {
        using var cli = new TestCli();
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir, "--org", "acme", "--org", "acme")).Should().Be(2);
        cli.Err.ToString().Should().Contain("acme");

        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir, "--org", "acme", "--org", "zenith",
            "--admin-object-id", Guid.NewGuid().ToString())).Should().Be(2);
        cli.Err.ToString().Should().Contain("--admin-object-id");

        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir, "--org", "acme",
            "--admin-object-id", Guid.NewGuid().ToString(), "--admin-object-id", Guid.NewGuid().ToString())).Should().Be(2);
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir, "--org", "acme", "--admin-object-id", "not-a-guid")).Should().Be(2);
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir, "--org", "acme", "--port", "65535", "--org", "zenith")).Should().Be(2);
        File.Exists(Path.Combine(_dir, ".env")).Should().BeFalse();
    }

    [Fact]
    public async Task Init_with_sql_connection_has_no_sqlserver_service_and_no_sa_password()
    {
        using var cli = new TestCli();
        (await cli.RunAsync("server", "init", "--dir", _dir, "--org", "acme", "--org", "zenith",
            "--sql-connection", "Server=sql.example.com,1433;User Id=xrmd;Password=\"p@ss;word!\";Encrypt=True")).Should().Be(0, cli.Err.ToString());

        var env = ReadEnv(_dir);
        env.Should().NotContainKey("SA_PASSWORD");
        var services = ComposeYaml.Resolve(_dir);
        services.Keys.Should().Equal("xrmd-acme", "xrmd-zenith");
        foreach (var org in new[] { "acme", "zenith" })
        {
            var service = services[$"xrmd-{org}"];
            var built = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(service.Environment["ConnectionStrings__Org"]);
            built.InitialCatalog.Should().Be(org);
            built.DataSource.Should().Be("sql.example.com,1433");
            built.Password.Should().Be("p@ss;word!");
            service.Environment["Admin__FilesBackupDirectory"].Should().Be("/var/lib/xrmd/backups");
            service.Environment["Admin__BackupDirectory"].Should().Be("/var/opt/mssql/backup", "the default when SQL_BACKUP_DIRECTORY is empty");
            service.Volumes.Should().Contain("backups:/var/lib/xrmd/backups").And.Contain("files:/var/lib/xrmd/files");
            service.DependsOn.Should().BeEmpty();
        }
        env["SQL_BACKUP_DIRECTORY"].Should().BeEmpty();
        var envFile = Path.Combine(_dir, ".env");
        File.WriteAllText(envFile, File.ReadAllText(envFile).Replace("SQL_BACKUP_DIRECTORY=", @"SQL_BACKUP_DIRECTORY=D:\Backups"));
        ComposeYaml.Resolve(_dir)["xrmd-acme"].Environment["Admin__BackupDirectory"].Should().Be(@"D:\Backups");
        Xrmd.Cli.Server.Installation.Load(_dir).Orgs.Should().HaveCount(2, "the key is kept as an ordinary .env key");
        var compose = File.ReadAllText(Path.Combine(_dir, "docker-compose.yml"));
        compose.Should().NotContain("SA_PASSWORD").And.NotContain("mssql-data");

        // A connection string that names a database is refused: the database is the org.
        foreach (var bad in new[] { "Server=x;Database=xrmverse", "Server=x;Initial Catalog=xrmverse", "Server=x;bogus-key=1", "Server=x;Password='a'" })
        {
            (await cli.RunAsync("server", "init", "--dir", Path.Combine(_dir, "bad"), "--org", "acme", "--sql-connection", bad)).Should().Be(2, bad);
            cli.Err.ToString().Should().Contain("--sql-connection");
        }
        Directory.Exists(Path.Combine(_dir, "bad")).Should().BeFalse();
    }

    private const string DeveloperWarning =
        "This installation runs the SQL Server Developer edition, which is not licensed for production. Set MSSQL_PID in .env (see README, \"SQL Server editions\").";

    [Fact]
    public async Task Init_writes_mssql_pid_express_by_default()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme");

        ReadEnv(_dir)["MSSQL_PID"].Should().Be("Express");
        ComposeYaml.Resolve(_dir)["sqlserver"].Environment["MSSQL_PID"].Should().Be("Express");
        Xrmd.Cli.Server.Installation.Load(_dir).SqlEdition.Should().Be("Express");
        cli.Err.ToString().Should().NotContain("Developer");
    }

    [Theory]
    [InlineData("Express", "Express")]
    [InlineData("Developer", "Developer")]
    [InlineData("Standard", "Standard")]
    [InlineData("Enterprise", "Enterprise")]
    [InlineData("EnterpriseCore", "EnterpriseCore")]
    [InlineData("standard", "Standard")]
    [InlineData("ABCDE-12345-FGHIJ-67890-KLMNO", "ABCDE-12345-FGHIJ-67890-KLMNO")]
    [InlineData("abcde-12345-fghij-67890-klmno", "ABCDE-12345-FGHIJ-67890-KLMNO")]
    public async Task Init_sql_edition_accepts_editions_and_product_keys_and_rejects_others(string given, string written)
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--sql-edition", given);
        ReadEnv(_dir)["MSSQL_PID"].Should().Be(written);
        ComposeYaml.Resolve(_dir)["sqlserver"].Environment["MSSQL_PID"].Should().Be(written);
        if (written == "Developer") cli.Err.ToString().Should().Contain(DeveloperWarning);
        else cli.Err.ToString().Should().NotContain(DeveloperWarning);
        cli.Out.ToString().Should().NotContain("12345", "a product key is never printed");

        foreach (var bad in new[] { "", "Web", "Evaluation", "Express2", "ABCDE-12345-FGHIJ-67890", "ABCDE-12345-FGHIJ-67890-KLMNOP", "ABCDE_12345_FGHIJ_67890_KLMNO" })
        {
            var target = Path.Combine(_dir, "bad");
            (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", target, "--org", "acme", "--sql-edition", bad)).Should().Be(2, bad);
            cli.Err.ToString().Should().Contain("--sql-edition");
            Directory.Exists(target).Should().BeFalse(bad);
        }
    }

    [Fact]
    public async Task Init_sql_edition_with_sql_connection_is_exit_2()
    {
        using var cli = new TestCli();
        (await cli.RunAsync("server", "init", "--dir", _dir, "--org", "acme", "--sql-connection", "Server=sql.example.com;User Id=xrmd;Password=x",
            "--sql-edition", "Standard")).Should().Be(2);
        cli.Err.ToString().Should().Contain("--sql-edition").And.Contain("--sql-connection");
        Directory.Exists(_dir).Should().BeFalse();

        // Without --sql-edition: no MSSQL_PID at all, the external SQL Server's edition is its own.
        await InitAsync(cli, "--org", "acme", "--sql-connection", "Server=sql.example.com;User Id=xrmd;Password=x");
        ReadEnv(_dir).Should().NotContainKey("MSSQL_PID");
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().NotContain("MSSQL_PID");
    }

    [Fact]
    public async Task Init_force_keeps_the_sql_edition_of_an_existing_env()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--sql-edition", "Standard");
        await InitAsync(cli, "--org", "acme", "--force");
        ReadEnv(_dir)["MSSQL_PID"].Should().Be("Standard", "the data volume keeps running the edition it was set up with");

        // An explicit --sql-edition wins.
        await InitAsync(cli, "--org", "acme", "--force", "--sql-edition", "Enterprise");
        ReadEnv(_dir)["MSSQL_PID"].Should().Be("Enterprise");

        // Over a .env from before MSSQL_PID (legacy or Plan 9): that SQL Server ran the image's default, Developer.
        var legacy = Path.Combine(_dir, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, ".env"), "SA_PASSWORD=Legacy-Pa55word!\nAUTH_MODE=Development\nAUTH_DEV_SIGNING_KEY=legacysigningkey0123456789\n");
        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", legacy, "--org", "setup", "--force")).Should().Be(0, cli.Err.ToString());
        ReadEnv(legacy)["MSSQL_PID"].Should().Be("Developer");
        cli.Err.ToString().Should().Contain(DeveloperWarning);
    }

    [Fact]
    public void Env_template_contains_the_sql_edition_comment_as_a_whole_line()
    {
        var template = Xrmd.Cli.Server.ComposeRenderer.ReadTemplate("env.template").Replace("\r\n", "\n").Split('\n');
        template.Should().Contain(Xrmd.Cli.Server.Installation.SqlEditionComment);
    }

    [Fact]
    public async Task Up_on_env_without_mssql_pid_keeps_developer_and_warns()
    {
        // Review Focus 1: a Plan 9 installation ran the image's default edition, Developer; it never becomes Express silently.
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, """{"status":"Healthy"}"""));
        await InitAsync(cli, "--org", "acme");
        var envPath = Path.Combine(_dir, ".env");
        var plan9 = string.Join('\n', File.ReadAllLines(envPath).Where(l => !l.Contains("MSSQL_PID") && l != Xrmd.Cli.Server.Installation.SqlEditionComment)) + "\n";
        File.WriteAllText(envPath, plan9);
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        cli.Clock = new AdvancingClock();

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        ReadEnv(_dir)["MSSQL_PID"].Should().Be("Developer");
        ComposeYaml.Resolve(_dir)["sqlserver"].Environment["MSSQL_PID"].Should().Be("Developer");
        cli.Err.ToString().Should().Contain(DeveloperWarning);
        runner.Calls.Should().ContainSingle("the key is written before docker compose runs");
        // Everything else of .env is kept as it was.
        File.ReadAllText(envPath).Replace(Xrmd.Cli.Server.Installation.SqlEditionComment + "\nMSSQL_PID=Developer\n", "")
            .Should().Be(plan9);

        // The same for upgrade; it keeps warning while the edition is Developer.
        File.WriteAllText(envPath, plan9);
        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        ReadEnv(_dir)["MSSQL_PID"].Should().Be("Developer");
        cli.Err.ToString().Should().Contain(DeveloperWarning);
        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        cli.Err.ToString().Should().Contain(DeveloperWarning);

        // Once Express is chosen in .env, no warning.
        File.WriteAllText(envPath, File.ReadAllText(envPath).Replace("MSSQL_PID=Developer", "MSSQL_PID=Express"));
        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        cli.Err.ToString().Should().NotContain(DeveloperWarning);
        ComposeYaml.Resolve(_dir)["sqlserver"].Environment["MSSQL_PID"].Should().Be("Express");

        // An external SQL Server gets no key (no sqlserver service), and a legacy installation is not touched (M2).
        var external = Path.Combine(_dir, "external");
        (await cli.RunAsync("server", "init", "--dir", external, "--org", "acme", "--sql-connection", "Server=sql;User Id=xrmd;Password=x")).Should().Be(0);
        FakeSdk.Install(external);
        (await cli.RunAsync("server", "up", "--dir", external)).Should().Be(0, cli.Err.ToString());
        ReadEnv(external).Should().NotContainKey("MSSQL_PID");
        cli.Err.ToString().Should().NotContain(DeveloperWarning);

        var legacy = Path.Combine(_dir, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, ".env"), "SA_PASSWORD=x\n");
        File.WriteAllText(Path.Combine(legacy, "docker-compose.yml"), "services:\n  xrmd:\n    image: x\n");
        (await cli.RunAsync("server", "up", "--dir", legacy)).Should().Be(1);
        File.ReadAllText(Path.Combine(legacy, ".env")).Should().Be("SA_PASSWORD=x\n");
    }

    [Fact]
    public async Task Org_add_on_env_without_mssql_pid_writes_developer_too()
    {
        // org add re-renders the compose file as well: it must not render a sqlserver service that Compose then refuses.
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme");
        var envPath = Path.Combine(_dir, ".env");
        File.WriteAllLines(envPath, File.ReadAllLines(envPath).Where(l => !l.Contains("MSSQL_PID")));
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        ReadEnv(_dir)["MSSQL_PID"].Should().Be("Developer");
        cli.Err.ToString().Should().Contain(DeveloperWarning);
    }

    // Public hosts (H1, H2, H5, H6, I8). Every refusal is a usage error and writes nothing (Review Focus 5).

    private async Task InitRefusedAsync(string expected, params string[] args)
    {
        using var cli = new TestCli();
        (await cli.RunAsync(["server", "init", "--accept-sql-server-eula", "--dir", _dir, "--org", "acme", "--org", "zenith", .. args])).Should().Be(ExitCodes.Usage, cli.Err.ToString());
        cli.Err.ToString().Should().Contain(expected);
        Directory.Exists(_dir).Should().BeFalse("nothing is written when init refuses its options");
        new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load().Profiles.Should().BeEmpty();
    }

    [Fact]
    public Task Init_with_hosts_requires_tls_mode() => InitRefusedAsync("--tls", "--domain", "xrm.test");

    [Fact]
    public Task Init_acme_requires_email() => InitRefusedAsync("--acme-email", "--domain", "xrm.test", "--tls", "acme");

    [Fact]
    public Task Init_files_requires_cert_and_key() =>
        InitRefusedAsync("--tls-key", "--domain", "xrm.test", "--tls", "files", "--tls-cert", Path.Combine(Path.GetTempPath(), "server.crt"));

    [Fact]
    public Task Init_rejects_a_host_for_an_unknown_org() =>
        InitRefusedAsync("'orbit'", "--public-host", "orbit=orbit.example.com", "--tls", "internal");

    [Fact]
    public async Task Init_rejects_the_same_host_twice()
    {
        await InitRefusedAsync("crm.example.com",
            "--public-host", "acme=crm.example.com", "--public-host", "zenith=crm.example.com", "--tls", "internal");
        await InitRefusedAsync("acme.xrm.test", "--domain", "xrm.test", "--public-host", "zenith=acme.xrm.test", "--tls", "internal");
    }

    [Fact]
    public async Task Init_rejects_an_invalid_host()
    {
        await InitRefusedAsync(Xrmd.Cli.Server.Installation.HostRule, "--public-host", "acme=Acme_CRM", "--tls", "internal");
        await InitRefusedAsync(Xrmd.Cli.Server.Installation.HostRule, "--domain", "localhost", "--tls", "internal");
        await InitRefusedAsync("--public-host", "--public-host", "acme", "--tls", "internal");
    }

    [Fact]
    public Task Init_no_proxy_requires_trusted_proxy() => InitRefusedAsync("--trusted-proxy", "--domain", "xrm.test", "--no-proxy");

    [Fact]
    public async Task Init_rejects_an_invalid_trusted_proxy()
    {
        await InitRefusedAsync("300.1.1.1", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "300.1.1.1");
        await InitRefusedAsync("10.1.0.5/16", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "10.1.0.5/16");
        await InitRefusedAsync("'1'", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "1");
    }

    [Fact]
    public async Task Init_refuses_proxy_options_that_do_not_apply()
    {
        await InitRefusedAsync("--tls", "--tls", "internal"); // no host
        await InitRefusedAsync("--no-proxy", "--no-proxy", "--trusted-proxy", "10.0.0.5"); // no host
        await InitRefusedAsync("--tls", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "10.0.0.5", "--tls", "internal");
        await InitRefusedAsync("--trusted-proxy", "--domain", "xrm.test", "--tls", "internal", "--trusted-proxy", "10.0.0.5");
        await InitRefusedAsync("--expose-ports", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "10.0.0.5", "--expose-ports");
        var (cert, key) = WriteCertificatePair(Path.Combine(_certDir, Guid.NewGuid().ToString("N")));
        await InitRefusedAsync("--tls-cert", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "10.0.0.5", "--tls-cert", cert);
        await InitRefusedAsync("--tls-key", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "10.0.0.5", "--tls-key", key);
        await InitRefusedAsync("--acme-email", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "10.0.0.5", "--acme-email", "ops@example.com");
        await InitRefusedAsync("--edge-subnet", "--domain", "xrm.test", "--tls", "internal", "--edge-subnet", "10.20.30.0/30");
        await InitRefusedAsync("--edge-subnet", "--domain", "xrm.test", "--tls", "internal", "--edge-subnet", "10.20.30.0/28");
        await InitRefusedAsync("--acme-email", "--domain", "xrm.test", "--tls", "internal", "--acme-email", "ops@example.com");
        await InitRefusedAsync("--acme-email", "--domain", "xrm.test", "--tls", "acme", "--acme-email", "not an email");
    }

    [Fact]
    public async Task Init_files_refuses_a_missing_or_mismatched_certificate_pair()
    {
        var (cert, key) = WriteCertificatePair(Path.Combine(_certDir, Guid.NewGuid().ToString("N")));
        var (_, otherKey) = WriteCertificatePair(Path.Combine(_certDir, Guid.NewGuid().ToString("N")));
        await InitRefusedAsync("missing.crt", "--domain", "xrm.test", "--tls", "files", "--tls-cert", cert + ".missing.crt", "--tls-key", key);
        await InitRefusedAsync("--tls-key", "--domain", "xrm.test", "--tls", "files", "--tls-cert", cert, "--tls-key", otherKey);
    }

    [Fact]
    public async Task Init_files_copies_the_certificate_pair_with_private_key_mode_600()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX file modes only
        var (cert, key) = WriteCertificatePair(Path.Combine(_certDir, Guid.NewGuid().ToString("N")));
        using var cli = new TestCli();
        await InitWithoutSdkAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "files", "--tls-cert", cert, "--tls-key", key);

        var certs = Path.Combine(_dir, "certs");
        File.ReadAllText(Path.Combine(certs, "server.crt")).Should().Be(File.ReadAllText(cert));
        File.ReadAllText(Path.Combine(certs, "server.key")).Should().Be(File.ReadAllText(key));
        File.GetUnixFileMode(Path.Combine(certs, "server.key")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(Path.Combine(certs, "server.crt")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        ReadEnv(_dir)["TLS_MODE"].Should().Be("files");
    }

    [Fact]
    public async Task Init_acme_writes_the_email()
    {
        using var cli = new TestCli();
        await InitWithoutSdkAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "acme", "--acme-email", "ops@example.com");

        ReadEnv(_dir)["TLS_MODE"].Should().Be("acme");
        ReadEnv(_dir)["TLS_ACME_EMAIL"].Should().Be("ops@example.com");
    }

    [Fact]
    public async Task Dev_profiles_use_the_public_url()
    {
        using var cli = new TestCli();
        await InitWithoutSdkAsync(cli, "--org", "acme", "--org", "zenith", "--public-host", "acme=acme.example.com", "--tls", "internal");

        var profiles = new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load().Profiles;
        profiles["acme"].Url.Should().Be("https://acme.example.com/");
        profiles["zenith"].Url.Should().Be("http://localhost:5002/", "an organization without a host keeps its local URL");
        cli.Out.ToString().Should().Contain("acme: https://acme.example.com/ (profile 'acme')");
    }

    [Fact]
    public async Task Entra_login_hints_use_the_public_url()
    {
        using var cli = new TestCli();
        await InitWithoutSdkAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal",
            "--auth", "entra", "--tenant-id", Guid.NewGuid().ToString(), "--admin-object-id", Guid.NewGuid().ToString());

        cli.Out.ToString().Should().Contain("--url https://acme.xrm.test/ ").And.Contain("token audience https://acme.xrm.test/");
    }

    [Fact]
    public async Task Init_prints_the_dns_records_to_create()
    {
        using (var cli = new TestCli())
        {
            await InitWithoutSdkAsync(cli, "--org", "acme", "--org", "zenith", "--org", "orbit", "--domain", "xrm.test",
                "--public-host", "orbit=crm.example.com", "--tls", "internal");
            var lines = cli.Out.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            lines.Should().Contain("  *.xrm.test", "one wildcard record covers every <org>.<domain>");
            lines.Should().Contain("  crm.example.com");
            lines.Should().NotContain("  acme.xrm.test", "the wildcard covers it");
        }

        Directory.Delete(_dir, recursive: true);
        using (var cli = new TestCli())
        {
            await InitWithoutSdkAsync(cli, "--org", "acme", "--org", "zenith",
                "--public-host", "acme=acme.example.com", "--public-host", "zenith=zenith.example.com", "--tls", "internal");
            var lines = cli.Out.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            lines.Should().Contain("  acme.example.com").And.Contain("  zenith.example.com");
            lines.Should().NotContain(l => l.Contains('*'));
        }
    }

    [Fact]
    public async Task Init_without_hosts_prints_no_dns_records()
    {
        using var cli = new TestCli();
        await InitWithoutSdkAsync(cli, "--org", "acme");
        cli.Out.ToString().Should().NotContain("DNS");
        ReadEnv(_dir)["PROXY_MODE"].Should().BeEmpty();
        ReadEnv(_dir)["XRMD_DOMAIN"].Should().BeEmpty();
    }

    [Fact]
    public async Task Org_add_on_a_domain_installation_gets_its_host()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        var installation = Xrmd.Cli.Server.Installation.Load(_dir);
        installation.Orgs.Single(o => o.Name == "orbit").Host.Should().Be("orbit.xrm.test");
        ReadEnv(_dir).Keys.Should().NotContain("ORG_ORBIT_HOST");
        new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load().Profiles["orbit"].Url.Should().Be("https://orbit.xrm.test/");
        cli.Out.ToString().Should().Contain("https://orbit.xrm.test/");
        ComposeYaml.Resolve(_dir)["xrmd-orbit"].Ports.Should().BeEmpty();

        (await cli.RunAsync("server", "org", "add", "zenith", "--public-host", "crm.example.com", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        ReadEnv(_dir)["ORG_ZENITH_HOST"].Should().Be("crm.example.com");
        cli.Out.ToString().Should().Contain("crm.example.com").And.Contain("DNS");
    }

    [Fact]
    public async Task Org_add_refuses_a_bad_or_taken_host_and_writes_nothing()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        cli.Process = new FakeProcessRunner();
        var env = File.ReadAllText(Path.Combine(_dir, ".env"));

        (await cli.RunAsync("server", "org", "add", "orbit", "--public-host", "acme.xrm.test", "--dir", _dir)).Should().Be(ExitCodes.Usage);
        (await cli.RunAsync("server", "org", "add", "orbit", "--public-host", "Not_A_Host", "--dir", _dir)).Should().Be(ExitCodes.Usage);
        cli.Err.ToString().Should().Contain(Xrmd.Cli.Server.Installation.HostRule);
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(env);
    }

    [Fact]
    public async Task Org_add_with_a_host_on_an_installation_without_tls_settings_is_refused()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme");
        cli.Process = new FakeProcessRunner();
        var env = File.ReadAllText(Path.Combine(_dir, ".env"));

        (await cli.RunAsync("server", "org", "add", "orbit", "--public-host", "orbit.example.com", "--dir", _dir)).Should().Be(ExitCodes.Usage);
        cli.Err.ToString().Should().Contain("TLS_MODE").And.Contain("xrmd server init --force").And.Contain("--tls");
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(env);
        cli.Process.As<FakeProcessRunner>().Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Init_files_leaves_no_certificate_when_saving_fails()
    {
        var (cert, key) = WriteCertificatePair(Path.Combine(_certDir, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(_dir, "docker-compose.yml")); // the compose file cannot be written
        using var cli = new TestCli();

        (await cli.RunAsync("server", "init", "--accept-sql-server-eula", "--dir", _dir, "--org", "acme", "--domain", "xrm.test", "--tls", "files",
            "--tls-cert", cert, "--tls-key", key, "--force")).Should().Be(ExitCodes.Unexpected);
        cli.Err.ToString().Should().StartWith("Unexpected error: ").And.Contain("docker-compose.yml");
        var certs = Path.Combine(_dir, "certs");
        (Directory.Exists(certs) ? Directory.GetFiles(certs) : []).Should().BeEmpty("the pair is copied only once the installation is saved");
    }

    /// <summary>Init in <c>files</c> mode with a generated pair, plus the SDK.</summary>
    private async Task<TestCli> InitFilesModeAsync()
    {
        var (cert, key) = WriteCertificatePair(Path.Combine(_certDir, Guid.NewGuid().ToString("N")));
        var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "files", "--tls-cert", cert, "--tls-key", key);
        return cli;
    }

    [Fact]
    public async Task Up_in_files_mode_refuses_missing_certificates_before_docker()
    {
        using var cli = await InitFilesModeAsync();
        File.Delete(Path.Combine(_dir, "certs", "server.crt"));
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        var compose = File.ReadAllText(Path.Combine(_dir, "docker-compose.yml"));

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(ExitCodes.Rejected);
        cli.Err.ToString().Should().StartWith("certs/server.crt not found in ");
        runner.Calls.Should().BeEmpty("Review Focus 6: nothing reaches Docker");
        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(ExitCodes.Rejected);
        runner.Calls.Should().BeEmpty();
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().Be(compose);
    }

    [Fact]
    public async Task Up_in_files_mode_with_the_certificates_starts()
    {
        using var cli = await InitFilesModeAsync();
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Hand_edited_caddyfile_is_not_overwritten()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        var caddyfile = Path.Combine(_dir, "Caddyfile");
        File.WriteAllText(caddyfile, "acme.xrm.test {\n\treverse_proxy xrmd-acme:5000\n}\n");
        var env = File.ReadAllText(Path.Combine(_dir, ".env"));
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(ExitCodes.Rejected);
        cli.Err.ToString().Should().Contain(caddyfile).And.Contain("not generated by xrmd");
        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(ExitCodes.Rejected);
        runner.Calls.Should().BeEmpty();
        File.ReadAllText(caddyfile).Should().Be("acme.xrm.test {\n\treverse_proxy xrmd-acme:5000\n}\n");
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(env, "nothing is written when the Caddyfile is refused");
    }

    private static string Command(ProcessSpec spec) => string.Join(' ', spec.Arguments.SkipWhile(a => a != "--env-file").Skip(2));

    private const string Reload = "exec -T proxy caddy reload --config /etc/caddy/Caddyfile";
    private const string ProxyPs = "ps -q proxy";

    /// <summary>A runner whose <c>ps -q proxy</c> names a running container; <paramref name="reload"/> answers the reload.</summary>
    private static FakeProcessRunner ProxyRunning(ProcessResult? reload = null) => new()
    {
        Respond = spec => Command(spec) switch
        {
            ProxyPs => new ProcessResult(0, "4f3c2a1b0e9d\n", ""),
            Reload => reload,
            _ => null,
        },
    };

    [Fact]
    public async Task Org_add_reloads_caddy_without_restarting_others()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        var runner = ProxyRunning();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Select(Command).Should().Equal("up -d xrmd-zenith", ProxyPs, Reload);
        File.ReadAllText(Path.Combine(_dir, "Caddyfile")).Should().Contain("zenith.xrm.test {").And.Contain("reverse_proxy xrmd-zenith:5000");
    }

    [Fact]
    public async Task Org_add_without_a_host_does_not_touch_the_proxy()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--public-host", "acme=acme.example.com", "--tls", "internal");
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        var caddyfile = File.ReadAllText(Path.Combine(_dir, "Caddyfile"));

        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Select(Command).Should().Equal("up -d xrmd-orbit");
        File.ReadAllText(Path.Combine(_dir, "Caddyfile")).Should().Be(caddyfile);
    }

    [Fact]
    public async Task Org_add_of_the_first_host_starts_the_proxy_only()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme");
        var envPath = Path.Combine(_dir, ".env");
        File.WriteAllText(envPath, File.ReadAllText(envPath).Replace("TLS_MODE=", "TLS_MODE=internal"));
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "add", "orbit", "--public-host", "orbit.example.com", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Select(Command).Should().Equal("up -d xrmd-orbit", "up -d --no-deps --remove-orphans proxy");
        File.ReadAllText(Path.Combine(_dir, "Caddyfile")).Should().Contain("orbit.example.com {");
    }

    [Fact]
    public async Task Org_add_in_files_mode_refuses_missing_certificates_before_writing()
    {
        using var cli = await InitFilesModeAsync();
        File.Delete(Path.Combine(_dir, "certs", "server.key"));
        var env = File.ReadAllText(Path.Combine(_dir, ".env"));
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir)).Should().Be(ExitCodes.Rejected);
        cli.Err.ToString().Should().StartWith("certs/server.key not found in ");
        runner.Calls.Should().BeEmpty();
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(env);
    }

    [Fact]
    public async Task Org_remove_of_the_last_host_removes_the_proxy()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--public-host", "zenith=zenith.example.com", "--tls", "internal");
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "remove", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Select(Command).Should().Equal("rm -s -f xrmd-zenith", "rm -s -f proxy");
        File.Exists(Path.Combine(_dir, "Caddyfile")).Should().BeFalse("Review Focus 4: no host, no proxy, no Caddyfile");
        ComposeYaml.Resolve(_dir).Keys.Should().Equal("backups-init", "sqlserver", "xrmd-acme");
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().NotContain("edge");
    }

    [Fact]
    public async Task Org_remove_of_one_of_two_only_reloads()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal");
        var runner = ProxyRunning();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "remove", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Select(Command).Should().Equal("rm -s -f xrmd-zenith", ProxyPs, Reload);
        var caddyfile = File.ReadAllText(Path.Combine(_dir, "Caddyfile"));
        caddyfile.Should().Contain("acme.xrm.test {").And.NotContain("zenith");
        ComposeYaml.Resolve(_dir)["proxy"].DependsOn.Should().Equal("xrmd-acme");
    }

    [Fact]
    public async Task Caddyfile_is_rewritten_in_place_for_the_bind_mount()
    {
        if (OperatingSystem.IsWindows()) return; // the inode of a single-file bind mount is a Linux matter
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        cli.Process = new FakeProcessRunner();
        // The proxy container sees the file it was started with: a rename would leave it on the old content.
        using var mounted = new FileStream(Path.Combine(_dir, "Caddyfile"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        using var reader = new StreamReader(mounted);
        reader.ReadToEnd().Should().Contain("zenith.xrm.test {");
    }

    [Fact]
    public async Task Up_restarts_the_proxy_when_the_caddyfile_changed()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        using var root = TestCertificates.CreateRoot(cli.Clock.GetUtcNow());
        var runner = CaddyRootExporter(root);
        cli.Process = runner;

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Select(Command2).Should().Equal("up -d --remove-orphans", CaddyRootCopy);

        var envPath = Path.Combine(_dir, ".env");
        File.WriteAllText(envPath, File.ReadAllText(envPath).Replace("TLS_MODE=internal", "TLS_MODE=acme").Replace("TLS_ACME_EMAIL=", "TLS_ACME_EMAIL=ops@example.com"));
        runner.Calls.Clear();
        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Select(Command).Should().Equal("up -d --remove-orphans", "restart proxy");
        File.ReadAllText(Path.Combine(_dir, "Caddyfile")).Should().Contain("email ops@example.com");
    }

    /// <summary>A self-signed certificate for *.xrm.test and its private key, as PEM files in <paramref name="dir"/>.</summary>
    private static (string Cert, string Key) WriteCertificatePair(string dir)
    {
        Directory.CreateDirectory(dir);
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=*.xrm.test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
        var cert = Path.Combine(dir, "wildcard.crt");
        var key = Path.Combine(dir, "wildcard.key");
        File.WriteAllText(cert, certificate.ExportCertificatePem());
        File.WriteAllText(key, rsa.ExportPkcs8PrivateKeyPem());
        return (cert, key);
    }

    /// <summary>Time that moves 10 seconds per reading and whose delays complete at once.</summary>
    private sealed class AdvancingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(10);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, TimeSpan.Zero, period);
    }
    // ---- Proxy reload when the stack is not running, and its failures (Task 5 review) ----

    [Fact]
    public async Task Org_add_skips_the_reload_when_the_proxy_is_not_running()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        var runner = new FakeProcessRunner(); // `ps -q proxy` prints nothing: the stack was never started
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Select(Command).Should().Equal("up -d xrmd-zenith", ProxyPs);
        cli.Out.ToString().Should().Contain($"The proxy is not running: `xrmd server up --dir {_dir}` starts it with the new Caddyfile.");
        File.ReadAllText(Path.Combine(_dir, "Caddyfile")).Should().Contain("zenith.xrm.test {");
    }

    [Fact]
    public async Task Org_remove_skips_the_reload_when_the_proxy_is_not_running()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal");
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "remove", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Select(Command).Should().Equal("rm -s -f xrmd-zenith", ProxyPs);
        cli.Out.ToString().Should().Contain("The proxy is not running").And.NotContain("The proxy reloaded");
    }

    [Fact]
    public async Task Org_add_names_the_saved_state_and_the_fix_when_the_reload_fails()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        var runner = ProxyRunning(reload: new ProcessResult(1, "", "Error: loading new config: bad"));
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir)).Should().Be(ExitCodes.Unexpected);
        var restart = $"docker compose -f {Path.Combine(_dir, "docker-compose.yml")} --env-file {Path.Combine(_dir, ".env")} restart proxy";
        cli.Err.ToString().Should().StartWith("The organization 'zenith' is saved and started. The proxy did not reload the new Caddyfile: ")
            .And.Contain("Error: loading new config: bad").And.Contain($"Apply it with `{restart}`.");
        Xrmd.Cli.Server.Installation.Load(_dir).Orgs.Select(o => o.Name).Should().Contain("zenith");
    }

    [Fact]
    public async Task Org_remove_refuses_a_hand_edited_caddyfile_before_stopping_the_org()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal");
        File.WriteAllText(Path.Combine(_dir, "Caddyfile"), "acme.xrm.test {\n\treverse_proxy xrmd-acme:5000\n}\n");
        var env = File.ReadAllText(Path.Combine(_dir, ".env"));
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "remove", "zenith", "--dir", _dir)).Should().Be(ExitCodes.Rejected);
        cli.Err.ToString().Should().Contain("not generated by xrmd");
        runner.Calls.Should().BeEmpty("xrmd-zenith is not stopped when the Caddyfile is refused");
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(env);
        new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load().Profiles.Should().ContainKey("zenith");
    }

    [Fact]
    public async Task A_caddyfile_that_cannot_be_staged_leaves_env_and_compose_as_they_were()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        Directory.CreateDirectory(Path.Combine(_dir, "Caddyfile.tmp")); // the staged Caddyfile cannot be written
        var env = File.ReadAllText(Path.Combine(_dir, ".env"));
        var compose = File.ReadAllText(Path.Combine(_dir, "docker-compose.yml"));
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir)).Should().Be(ExitCodes.Unexpected);
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(env, "every file is staged before any target is replaced");
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().Be(compose);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty("the staged .env and compose file are removed");
        runner.Calls.Should().BeEmpty();
    }

    // ---- Internal CA trust, status and doctor (Task 6, H7, H9) ----

    private const string CaddyRootCopy = "cp proxy:/data/caddy/pki/authorities/local/root.crt {0}";

    /// <summary>
    /// A runner whose <c>docker compose cp</c> writes <paramref name="root"/> as PEM to its destination, after
    /// <paramref name="failures"/> failed attempts (Caddy has not created it yet).
    /// </summary>
    private FakeProcessRunner CaddyRootExporter(System.Security.Cryptography.X509Certificates.X509Certificate2 root, int failures = 0)
    {
        var attempts = 0;
        return new FakeProcessRunner
        {
            Respond = spec =>
            {
                if (spec.Arguments.SkipWhile(a => a != "--env-file").Skip(2).FirstOrDefault() != "cp") return null;
                if (attempts++ < failures)
                    return new ProcessResult(1, "", "Error response from daemon: Could not find the file /data/caddy/pki/authorities/local/root.crt in container");
                TestCertificates.WritePem(root, spec.Arguments[^1]);
                return new ProcessResult(0, "", "");
            },
        };
    }

    private string CaddyRootPath => Path.Combine(_dir, "certs", "caddy-root.crt");

    private string Command2(ProcessSpec spec) => Command(spec).Replace(CaddyRootPath, "{0}");

    [Fact]
    public async Task Up_internal_sets_ca_file_on_local_idp_profiles_too()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--auth", "local", "--org", "acme", "--public-host", "acme=acme.xrm.test", "--tls", "internal");
        cli.Out.ToString().Should().Contain($"--ca-file {CaddyRootPath}");
        var store = new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir);
        var config = store.Load();
        config.Profiles["acme"] = new Xrmd.Cli.Config.Profile
        {
            Url = "https://acme.xrm.test/", Mode = Xrmd.Cli.Config.AuthModes.DeviceCode, Idp = Xrmd.Cli.Config.IdentityProviders.Local,
        };
        store.Save(config);
        using var root = TestCertificates.CreateRoot(cli.Clock.GetUtcNow());
        cli.Process = CaddyRootExporter(root, failures: 0);

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        store.Load().Profiles["acme"].CaFile.Should().Be(CaddyRootPath);
    }

    [Fact]
    public async Task Up_internal_exports_the_root_and_sets_ca_file()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--public-host", "acme=acme.xrm.test", "--tls", "internal");
        using var root = TestCertificates.CreateRoot(cli.Clock.GetUtcNow());
        var runner = CaddyRootExporter(root, failures: 1);
        cli.Process = runner;

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        runner.Calls.Select(Command2).Should().Equal("up -d --remove-orphans", CaddyRootCopy, CaddyRootCopy);
        File.ReadAllText(CaddyRootPath).Should().Be(root.ExportCertificatePem());
        var profiles = new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load().Profiles;
        profiles["acme"].CaFile.Should().Be(CaddyRootPath);
        profiles["zenith"].CaFile.Should().BeNull("an organization without a host is reached over http://localhost");
        cli.Out.ToString().Should().Contain(CaddyRootPath).And.Contain("acme");

        // A profile created later for a host of the installation records it too.
        runner.Calls.Clear();
        (await cli.RunAsync("server", "org", "add", "orbit", "--public-host", "orbit.xrm.test", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load().Profiles["orbit"].CaFile.Should().Be(CaddyRootPath);
    }

    [Fact]
    public async Task Up_in_acme_mode_exports_nothing()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "acme", "--acme-email", "ops@example.com");
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        runner.Calls.Select(Command).Should().Equal("up -d --remove-orphans");
        new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load().Profiles["acme"].CaFile.Should().BeNull();
    }

    [Fact]
    public async Task Up_internal_reports_a_root_that_never_appears()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        using var root = TestCertificates.CreateRoot(cli.Clock.GetUtcNow());
        cli.Process = CaddyRootExporter(root, failures: int.MaxValue);
        cli.CaddyRootTimeout = TimeSpan.FromMilliseconds(300);

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(ExitCodes.Unexpected);
        cli.Err.ToString().Should().StartWith("The server started, but Caddy's root certificate could not be exported to " + CaddyRootPath)
            .And.Contain("Could not find the file").And.Contain($"xrmd server up --dir {_dir}");
        new Xrmd.Cli.Config.ProfileStore(cli.ConfigDir).Load().Profiles["acme"].CaFile.Should().BeNull();
    }

    [Fact]
    public async Task Status_shows_the_public_url()
    {
        using var cli = new TestCli(request => FakeHandler.Json(HttpStatusCode.OK, Health(request.RequestUri!.Host == "acme.xrm.test" ? "acme" : "zenith")));
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--public-host", "acme=acme.xrm.test", "--tls", "internal");

        (await cli.RunAsync("server", "status", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        var lines = cli.Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        lines[0].Should().MatchRegex(@"^org\s+url\s+publicUrl\s+status$");
        lines[1].Should().MatchRegex(@"^acme\s+https://acme\.xrm\.test/\s+https://acme\.xrm\.test/\s+Healthy$");
        lines[2].Should().MatchRegex(@"^zenith\s+http://localhost:5002/\s+Healthy$");
        cli.Handler.Requests.Select(r => r.RequestUri!.ToString())
            .Should().Equal("https://acme.xrm.test/admin/health", "http://localhost:5002/admin/health");

        (await cli.RunAsync("server", "status", "--dir", _dir, "-o", "json")).Should().Be(0, cli.Err.ToString());
        using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
        var rows = json.RootElement.EnumerateArray().ToList();
        rows[0].GetProperty("publicUrl").GetString().Should().Be("https://acme.xrm.test/");
        rows[1].GetProperty("publicUrl").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    private const string ServerChecks = """{"checks":[{"name":"database","status":"ok","detail":"fine"}]}""";

    /// <summary>A resolver that knows only <paramref name="known"/>, at 127.0.0.1.</summary>
    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolver(params string[] known) => (host, _) =>
        known.Contains(host)
            ? Task.FromResult(new[] { IPAddress.Loopback })
            : Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));

    private static System.Text.Json.JsonElement Check(System.Text.Json.JsonElement row, string name) =>
        row.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    [Fact]
    public async Task Doctor_reports_an_unresolvable_host()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, ServerChecks));
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--public-host", "acme=acme.xrm.test", "--tls", "internal");
        cli.ResolveHost = Resolver();

        (await cli.RunAsync("server", "doctor", "--dir", _dir, "-o", "json")).Should().Be(1);

        using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
        var rows = json.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("org").GetString()!);
        var dns = Check(rows["acme"], "dns");
        dns.GetProperty("status").GetString().Should().Be("error");
        dns.GetProperty("detail").GetString().Should().Contain("acme.xrm.test does not resolve on this machine")
            .And.Contain("DNS record").And.Contain("hosts file");
        rows["acme"].GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .Should().Equal("database", "dns");
        rows["zenith"].GetProperty("checks").GetArrayLength().Should().Be(1, "an organization without a host has no client-side checks");
        cli.Err.ToString().Should().Contain("acme (dns)").And.NotContain("zenith");
    }

    /// <summary>The intermediates the proxy of <see cref="DoctorCertificateAsync"/> serves with its leaf.</summary>
    private System.Security.Cryptography.X509Certificates.X509Certificate2[] _servedIntermediates = [];

    /// <summary>Doctor of one organization acme.xrm.test in internal mode whose proxy serves <paramref name="leaf"/>, trusting <paramref name="caddyRoot"/>.</summary>
    private async Task<(int ExitCode, System.Text.Json.JsonElement Certificate, string Table)> DoctorCertificateAsync(
        System.Security.Cryptography.X509Certificates.X509Certificate2 caddyRoot,
        System.Security.Cryptography.X509Certificates.X509Certificate2 leaf, params string[] init)
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, ServerChecks));
        await InitAsync(cli, init.Length > 0 ? init : ["--org", "acme", "--domain", "xrm.test", "--tls", "internal"]);
        TestCertificates.WritePem(caddyRoot, CaddyRootPath);
        await using var proxy = new TlsTestServer(leaf, "{}", _servedIntermediates);
        cli.ResolveHost = Resolver("acme.xrm.test");
        cli.TlsCheckPort = proxy.Port;

        await cli.RunAsync("server", "doctor", "--dir", _dir);
        var table = cli.Out.ToString();
        var exit = await cli.RunAsync("server", "doctor", "--dir", _dir, "-o", "json");
        using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
        return (exit, Check(json.RootElement[0], "certificate").Clone(), table);
    }

    [Fact]
    public async Task Doctor_certificate_expiring_is_a_warning()
    {
        // Internal mode: Caddy renews its 12-hour leaves itself; what a human must renew is the CA root of caddy-root.crt,
        // so H9's 14-day window applies to it (in acme, files and external mode it applies to the served certificate).
        var now = new TestCli().Clock.GetUtcNow();
        using var root = TestCertificates.CreateRoot(now, notAfter: now.AddDays(5));
        using var leaf = TestCertificates.CreateLeaf(root, now.AddHours(-1), now.AddHours(11), "acme.xrm.test");

        var (exit, certificate, table) = await DoctorCertificateAsync(root, leaf);

        exit.Should().Be(0, "a warning is not an error");
        certificate.GetProperty("status").GetString().Should().Be("warning");
        certificate.GetProperty("detail").GetString().Should().Contain("expires in 5 days").And.Contain(root.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd"))
            .And.Contain("root");
        table.Should().MatchRegex(@"(?m)^acme\s+https://acme\.xrm\.test/\s+https://acme\.xrm\.test/\s+warning: certificate\s*$");
    }

    [Fact]
    public async Task Doctor_certificate_valid_is_ok()
    {
        // A leaf as short-lived as Caddy's internal ones is not a warning: Caddy renews it.
        var now = new TestCli().Clock.GetUtcNow();
        using var root = TestCertificates.CreateRoot(now);
        using var leaf = TestCertificates.CreateLeaf(root, now.AddHours(-1), now.AddHours(11), "acme.xrm.test");

        var (exit, certificate, _) = await DoctorCertificateAsync(root, leaf);

        exit.Should().Be(0);
        certificate.GetProperty("status").GetString().Should().Be("ok");
        certificate.GetProperty("detail").GetString().Should().Contain("Caddy renews it").And.Contain("root valid until");
    }

    [Fact]
    public async Task Doctor_certificate_valid_through_the_served_intermediate_is_ok()
    {
        // Caddy's local CA: root -> intermediate -> leaf; the proxy sends the leaf and the intermediate, caddy-root.crt is the root.
        var now = new TestCli().Clock.GetUtcNow();
        using var root = TestCertificates.CreateRoot(now);
        using var intermediate = TestCertificates.CreateIntermediate(root, now.AddDays(-7), now.AddDays(180));
        using var leaf = TestCertificates.CreateLeaf(intermediate, now.AddHours(-1), now.AddDays(30), "acme.xrm.test");
        _servedIntermediates = [intermediate];

        var (exit, certificate, _) = await DoctorCertificateAsync(root, leaf);

        exit.Should().Be(0, certificate.ToString());
        certificate.GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Doctor_certificate_under_an_expired_intermediate_is_an_error()
    {
        var now = new TestCli().Clock.GetUtcNow();
        using var root = TestCertificates.CreateRoot(now);
        using var intermediate = TestCertificates.CreateIntermediate(root, now.AddDays(-30), now.AddDays(-1), "expired intermediate");
        using var leaf = TestCertificates.CreateLeaf(intermediate, now.AddDays(-1), now.AddDays(30), "acme.xrm.test");
        _servedIntermediates = [intermediate];

        var (exit, certificate, _) = await DoctorCertificateAsync(root, leaf);

        exit.Should().Be(1);
        certificate.GetProperty("status").GetString().Should().Be("error");
        certificate.GetProperty("detail").GetString().Should().Contain("CN=expired intermediate").And.Contain("expired on");
    }

    [Fact]
    public async Task Doctor_certificate_not_covering_the_host_is_an_error()
    {
        var now = new TestCli().Clock.GetUtcNow();
        using var root = TestCertificates.CreateRoot(now);
        using var leaf = TestCertificates.CreateLeaf(root, now.AddDays(-1), now.AddDays(60), "zenith.xrm.test");

        var (exit, certificate, table) = await DoctorCertificateAsync(root, leaf);

        exit.Should().Be(1);
        certificate.GetProperty("status").GetString().Should().Be("error");
        certificate.GetProperty("detail").GetString().Should().Contain("does not cover acme.xrm.test");
        table.Should().MatchRegex(@"(?m)^acme\s+.*error: certificate\s*$");
    }

    [Fact]
    public async Task Doctor_certificate_expired_or_untrusted_is_an_error()
    {
        var now = new TestCli().Clock.GetUtcNow();
        using var root = TestCertificates.CreateRoot(now);
        using (var expired = TestCertificates.CreateLeaf(root, now.AddDays(-90), now.AddDays(-2), "acme.xrm.test"))
        {
            var (exit, certificate, _) = await DoctorCertificateAsync(root, expired);
            exit.Should().Be(1);
            certificate.GetProperty("status").GetString().Should().Be("error");
            certificate.GetProperty("detail").GetString().Should().Contain("expired");
        }

        Directory.Delete(_dir, recursive: true);
        using var other = TestCertificates.CreateRoot(now, "another root");
        using var foreign = TestCertificates.CreateLeaf(other, now.AddDays(-1), now.AddDays(60), "acme.xrm.test");
        var (exit2, certificate2, _) = await DoctorCertificateAsync(root, foreign);
        exit2.Should().Be(1);
        certificate2.GetProperty("status").GetString().Should().Be("error");
        certificate2.GetProperty("detail").GetString().Should().Contain("not trusted").And.Contain(CaddyRootPath);
    }

    [Fact]
    public async Task Doctor_in_external_mode_reads_what_the_customer_proxy_serves()
    {
        // No caddy-root.crt in external mode: the certificate is checked against the machine's trust store, where the
        // test root is not, so a certificate that names the host and is in date is still reported as not trusted.
        var now = new TestCli().Clock.GetUtcNow();
        using var root = TestCertificates.CreateRoot(now);
        using var leaf = TestCertificates.CreateLeaf(root, now.AddDays(-1), now.AddDays(60), "acme.xrm.test");

        var (exit, certificate, _) = await DoctorCertificateAsync(root, leaf,
            "--org", "acme", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "10.0.0.5");

        exit.Should().Be(1);
        certificate.GetProperty("detail").GetString().Should().Contain("not trusted by this machine").And.NotContain("caddy-root.crt");
    }

    [Fact]
    public async Task Renewal_hint_in_files_mode_forces_the_proxy_to_load_the_new_pair()
    {
        // `caddy reload` skips a configuration that has not changed, and replacing the certificate files does not change the
        // Caddyfile; `server up` does not re-create a running proxy either. Only a forced reload (or a restart) loads the new pair.
        using var cli = new TestCli();
        var (cert, key) = WriteCertificatePair(Path.Combine(_certDir, Guid.NewGuid().ToString("N")));
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "files", "--tls-cert", cert, "--tls-key", key);

        var hint = Xrmd.Cli.Commands.ServerCommands.RenewalHint(Xrmd.Cli.Server.Installation.Load(_dir));

        hint.Should().Contain("caddy reload --config /etc/caddy/Caddyfile --force").And.Contain("docker compose restart proxy")
            .And.NotContain("xrmd server up");
    }
}
