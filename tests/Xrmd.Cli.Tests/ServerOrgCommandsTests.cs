using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xrmd.Cli.Config;

namespace Xrmd.Cli.Tests;

public class ServerOrgCommandsTests : IDisposable
{
    private const string DevObjectId = "00000000-0000-0000-0000-000000000001";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xrmd-server-org-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string EnvPath => Path.Combine(_dir, ".env");
    private string ComposePath => Path.Combine(_dir, "docker-compose.yml");

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
    public async Task Org_add_saves_the_org_but_refuses_to_start_without_sdk()
    {
        using var cli = new TestCli();
        await InitWithoutSdkAsync(cli, "--org", "acme");
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(1);

        runner.Calls.Should().BeEmpty();
        cli.Err.ToString().Should().Contain(Xrmd.Abstractions.Sdk.SdkPackage.MissingMessage(Path.Combine(_dir, "sdk")))
            .And.Contain($"xrmd sdk install --dir {_dir}").And.Contain("saved").And.Contain("server up");
        File.ReadAllText(EnvPath).Should().Contain("XRMD_ORGS=acme,orbit");
        Profiles(cli).Profiles.Should().ContainKey("orbit");
    }

    private static CliConfig Profiles(TestCli cli) => new ProfileStore(cli.ConfigDir).Load();

    private List<string> ComposeTail(ProcessSpec call) => call.Arguments.Skip(5).ToList();

    private void AssertComposePrefix(ProcessSpec call)
    {
        call.FileName.Should().Be("docker");
        call.Arguments.Take(5).Should().Equal("compose", "-f", ComposePath, "--env-file", EnvPath);
    }

    [Fact]
    public async Task Org_add_starts_only_the_new_service()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        // Never a bare `up -d`, which could recreate the running organizations (Review Focus 2).
        var call = runner.Calls.Should().ContainSingle().Subject;
        AssertComposePrefix(call);
        ComposeTail(call).Should().Equal("up", "-d", "xrmd-orbit");
        ComposeYaml.Resolve(_dir).Keys.Where(k => k.StartsWith("xrmd", StringComparison.Ordinal))
            .Should().Equal("xrmd-acme", "xrmd-zenith", "xrmd-orbit");
    }

    [Fact]
    public async Task Org_add_picks_the_next_free_port_and_creates_the_profile()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--port", "6000");
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain("orbit").And.Contain("http://localhost:6002/").And.Contain("profile");

        var env = ComposeYaml.ReadEnv(_dir);
        env["XRMD_ORGS"].Should().Be("acme,zenith,orbit");
        env["ORG_ORBIT_PORT"].Should().Be("6002");
        env["ORG_ORBIT_ADMIN_OBJECT_ID"].Should().Be(DevObjectId);
        var config = Profiles(cli);
        config.Profiles["orbit"].Should().BeEquivalentTo(new Profile
        {
            Url = "http://localhost:6002/", Mode = "dev", ObjectId = DevObjectId, SigningKeyEnv = "XRMD_DEV_SIGNING_KEY",
        });
        config.CurrentProfile.Should().Be("acme", "adding an organization does not change the current profile");

        // An explicit port and admin.
        var oid = Guid.NewGuid().ToString();
        (await cli.RunAsync("server", "org", "add", "adventure", "--dir", _dir, "--port", "7000", "--admin-object-id", oid, "-o", "json"))
            .Should().Be(0, cli.Err.ToString());
        using var json = JsonDocument.Parse(cli.Out.ToString());
        json.RootElement.GetProperty("org").GetString().Should().Be("adventure");
        json.RootElement.GetProperty("url").GetString().Should().Be("http://localhost:7000/");
        json.RootElement.GetProperty("profile").GetString().Should().Be("adventure");
        Profiles(cli).Profiles["adventure"].ObjectId.Should().Be(oid);
        ComposeYaml.ReadEnv(_dir)["ORG_ADVENTURE_PORT"].Should().Be("7000");
    }

    [Fact]
    public async Task Org_add_on_a_legacy_installation_is_rejected_with_the_hint()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(EnvPath, "SA_PASSWORD=x\nAUTH_MODE=Development\nXRMD_PORT=5000\nSEED_ORG_NAME=xrmd\nSEED_ADMIN_OBJECT_ID=" + DevObjectId + "\n");
        File.WriteAllText(ComposePath, "# Generated by `xrmd server init`. Start with: xrmd server up\nservices:\n  xrmd:\n    image: x\n");
        var env = File.ReadAllText(EnvPath);
        using var cli = new TestCli();
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        foreach (var args in new[] { new[] { "add", "orbit" }, ["remove", "xrmd"] })
        {
            (await cli.RunAsync(["server", "org", .. args, "--dir", _dir])).Should().Be(5);
            cli.Err.ToString().Should().Contain("predates").And.Contain("server down").And.Contain("server init --org setup --force").And.Contain("org restore");
        }
        runner.Calls.Should().BeEmpty();
        File.ReadAllText(EnvPath).Should().Be(env);
    }

    [Fact]
    public async Task Org_add_existing_org_or_used_port_is_exit_2()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        var env = File.ReadAllText(EnvPath);
        var compose = File.ReadAllText(ComposePath);

        foreach (var args in new[]
        {
            new[] { "acme" },
            ["orbit", "--port", "5002"],
            ["Orbit"],
            ["master"],
            ["orbit", "--admin-object-id", "admin"],
            ["orbit", "--port", "0"],
        })
        {
            (await cli.RunAsync(["server", "org", "add", .. args, "--dir", _dir])).Should().Be(2, string.Join(' ', args));
        }
        cli.Err.ToString().Should().NotBeEmpty();
        runner.Calls.Should().BeEmpty();
        File.ReadAllText(EnvPath).Should().Be(env);
        File.ReadAllText(ComposePath).Should().Be(compose);
        Profiles(cli).Profiles.Keys.Should().BeEquivalentTo(["acme", "zenith"]);
    }

    [Fact]
    public async Task Org_add_when_docker_fails_keeps_the_installation_and_its_profile()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme");
        cli.Process = new FakeProcessRunner { Result = new ProcessResult(1, "", "boom") };

        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(1);

        cli.Err.ToString().Should().Contain("boom").And.Contain($"re-run `xrmd server up --dir {_dir}`").And.Contain("with its CLI profile 'orbit'");
        ComposeYaml.ReadEnv(_dir)["XRMD_ORGS"].Should().Be("acme,orbit");
        Profiles(cli).Profiles.Should().ContainKey("orbit", "the profile goes with the saved organization (m4)");
    }

    [Fact]
    public async Task Org_add_in_entra_mode_creates_no_profile_and_prints_the_login_hint()
    {
        using var cli = new TestCli();
        var tenant = Guid.NewGuid().ToString();
        await InitAsync(cli, "--auth", "entra", "--tenant-id", tenant, "--org", "acme", "--admin-object-id", Guid.NewGuid().ToString());
        cli.Process = new FakeProcessRunner();

        // Entra has no dev default administrator.
        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(2);
        cli.Err.ToString().Should().Contain("--admin-object-id");

        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir, "--admin-object-id", Guid.NewGuid().ToString()))
            .Should().Be(0, cli.Err.ToString());
        Profiles(cli).Profiles.Should().BeEmpty();
        cli.Out.ToString().Should().Contain($"xrmd auth login --profile orbit --url http://localhost:5002/")
            .And.Contain($"--tenant-id {tenant}").And.Contain("audience http://localhost:5002/").And.Contain("ORG_ORBIT_AUDIENCE")
            .And.Contain("the CLI's `auth login` then cannot sign in");
    }

    [Fact]
    public async Task Org_remove_stops_and_removes_only_that_service_and_keeps_the_database()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "remove", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        var call = runner.Calls.Should().ContainSingle().Subject;
        AssertComposePrefix(call);
        ComposeTail(call).Should().Equal("rm", "-s", "-f", "xrmd-zenith");
        cli.Handler.Requests.Should().BeEmpty("the database is kept without --drop-database");
        var env = ComposeYaml.ReadEnv(_dir);
        env["XRMD_ORGS"].Should().Be("acme");
        env.Keys.Should().NotContain(k => k.StartsWith("ORG_ZENITH_", StringComparison.Ordinal));
        ComposeYaml.Resolve(_dir).Keys.Should().NotContain("xrmd-zenith");
        Profiles(cli).Profiles.Should().NotContainKey("zenith");
        cli.Out.ToString().Should().Contain("zenith");
    }

    [Fact]
    public async Task Org_remove_of_an_unknown_org_is_exit_4()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "remove", "orbit", "--dir", _dir)).Should().Be(4);
        cli.Err.ToString().Should().Contain("orbit");
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Org_remove_when_docker_fails_leaves_the_installation_unchanged()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        cli.Process = new FakeProcessRunner { Result = new ProcessResult(1, "", "boom") };
        var env = File.ReadAllText(EnvPath);

        (await cli.RunAsync("server", "org", "remove", "zenith", "--dir", _dir)).Should().Be(1);

        cli.Err.ToString().Should().Contain("boom");
        File.ReadAllText(EnvPath).Should().Be(env);
        Profiles(cli).Profiles.Should().ContainKey("zenith");
    }

    [Fact]
    public async Task Org_remove_drop_database_asks_confirmation_and_calls_admin_delete_through_another_org()
    {
        using var cli = new TestCli(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--org", "orbit");
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        var env = File.ReadAllText(EnvPath);

        // Without --yes and without a terminal: refused before anything changes.
        (await cli.RunAsync("server", "org", "remove", "acme", "--dir", _dir, "--drop-database")).Should().Be(2);
        cli.Err.ToString().Should().Contain("--yes");
        runner.Calls.Should().BeEmpty();
        File.ReadAllText(EnvPath).Should().Be(env);

        // Declined at the terminal: aborted, nothing changes.
        cli.IsInputTty = true;
        cli.Input = "n\n";
        (await cli.RunAsync("server", "org", "remove", "acme", "--dir", _dir, "--drop-database")).Should().Be(1);
        runner.Calls.Should().BeEmpty();
        File.ReadAllText(EnvPath).Should().Be(env);

        cli.Input = "y\n";
        (await cli.RunAsync("server", "org", "remove", "acme", "--dir", _dir, "--drop-database")).Should().Be(0, cli.Err.ToString());

        ComposeTail(runner.Calls.Single()).Should().Equal("rm", "-s", "-f", "xrmd-acme");
        var request = cli.Handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Delete);
        request.RequestUri!.ToString().Should().Be("http://localhost:5002/admin/orgs/acme", "through the first remaining org, zenith");
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        ComposeYaml.ReadEnv(_dir)["XRMD_ORGS"].Should().Be("zenith,orbit");
    }

    [Fact]
    public async Task Org_remove_drop_database_needs_a_profile_for_the_remaining_org()
    {
        using var cli = new TestCli(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        var store = new ProfileStore(cli.ConfigDir);
        var config = store.Load();
        config.Profiles.Remove("acme");
        store.Save(config);
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        var env = File.ReadAllText(EnvPath);

        (await cli.RunAsync("server", "org", "remove", "zenith", "--dir", _dir, "--drop-database", "--yes")).Should().Be(3);

        cli.Err.ToString().Should().Contain("xrmd auth login --profile acme");
        runner.Calls.Should().BeEmpty();
        File.ReadAllText(EnvPath).Should().Be(env);
    }

    [Fact]
    public async Task Org_remove_drop_database_of_the_last_org_is_rejected()
    {
        using var cli = new TestCli(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await InitAsync(cli, "--org", "acme");
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        var env = File.ReadAllText(EnvPath);

        foreach (var extra in new[] { Array.Empty<string>(), ["--drop-database", "--yes"] })
        {
            (await cli.RunAsync(["server", "org", "remove", "acme", "--dir", _dir, .. extra])).Should().Be(5);
            cli.Err.ToString().Should().Contain("xrmd server down").And.Contain("--volumes");
        }
        runner.Calls.Should().BeEmpty();
        cli.Handler.Requests.Should().BeEmpty();
        File.ReadAllText(EnvPath).Should().Be(env);
    }

    [Fact]
    public async Task Org_remove_moves_the_current_profile()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith", "--org", "orbit");
        cli.Process = new FakeProcessRunner();
        Profiles(cli).CurrentProfile.Should().Be("acme");

        // The current profile's org is removed: the first remaining org becomes current (Review Focus 5).
        (await cli.RunAsync("server", "org", "remove", "acme", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        var config = Profiles(cli);
        config.Profiles.Should().NotContainKey("acme");
        config.CurrentProfile.Should().Be("zenith");

        // Another current profile is left alone.
        (await cli.LoginDevAsync()).Should().Be(0);
        (await cli.RunAsync("server", "org", "remove", "orbit", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        Profiles(cli).CurrentProfile.Should().Be("default");
    }

    [Fact]
    public async Task Org_remove_clears_the_current_profile_when_the_remaining_org_has_none()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        cli.Process = new FakeProcessRunner();
        var store = new ProfileStore(cli.ConfigDir);
        var config = store.Load();
        config.Profiles.Remove("zenith");
        store.Save(config);

        (await cli.RunAsync("server", "org", "remove", "acme", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        config = Profiles(cli);
        config.CurrentProfile.Should().BeNull("no command may target the removed org's dead URL");
        config.Profiles.Should().BeEmpty();
    }

    [Theory]
    [InlineData("cli_x")]
    [InlineData("Acme")]
    public async Task Org_create_of_a_name_server_org_add_refuses_says_it_cannot_be_served_under_that_name(string db)
    {
        // M11: the Admin API accepts names that `server org add` refuses; the hint must not print a command that would fail.
        var oid = Guid.NewGuid();
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.Created, $$"""{"database":"{{db}}","organizationId":"x"}"""));
        (await cli.LoginDevAsync()).Should().Be(0);

        (await cli.RunAsync("org", "create", db, "--admin-object-id", oid.ToString())).Should().Be(0, cli.Err.ToString());
        var table = cli.Out.ToString();
        table.Should().NotContain("xrmd server org add " + db);
        var line = table.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith($"The database '{db}' has no xrmd process serving it.", StringComparison.Ordinal));
        line.Should().Contain("server org add").And.Contain("^[a-z][a-z0-9]{0,31}$").And.Contain("cannot be served under that name");

        (await cli.RunAsync("org", "create", db, "--admin-object-id", oid.ToString(), "-o", "json")).Should().Be(0, cli.Err.ToString());
        using var json = JsonDocument.Parse(cli.Out.ToString());
        json.RootElement.GetProperty("hint").GetString().Should().Be(line);
    }

    [Fact]
    public async Task Org_remove_reports_a_failed_profile_cleanup_after_the_org_was_removed()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--org", "zenith");
        cli.Process = new FakeProcessRunner();
        File.WriteAllText(new ProfileStore(cli.ConfigDir).FilePath, "{ not json");

        (await cli.RunAsync("server", "org", "remove", "zenith", "--dir", _dir)).Should().NotBe(0);

        cli.Err.ToString().Should().Contain("'zenith' was removed from the installation but the profile cleanup failed");
        ComposeYaml.ReadEnv(_dir)["XRMD_ORGS"].Should().Be("acme");
    }

    [Fact]
    public async Task Org_add_when_docker_is_missing_is_exit_1_and_says_to_rerun_server_up()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme");
        cli.Process = new FakeProcessRunner { Throw = new ProcessNotFoundException("docker") };

        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(1);

        cli.Err.ToString().Should().Contain("Install Docker").And.Contain($"re-run `xrmd server up --dir {_dir}`").And.Contain("with its CLI profile 'orbit'");
        ComposeYaml.ReadEnv(_dir)["XRMD_ORGS"].Should().Be("acme,orbit");
        Profiles(cli).Profiles.Should().ContainKey("orbit", "the profile goes with the saved organization (m4)");
    }

    [Fact]
    public async Task Org_create_prints_the_server_org_add_hint()
    {
        var oid = Guid.NewGuid();
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.Created, """{"database":"orbit","organizationId":"x"}"""));
        (await cli.LoginDevAsync()).Should().Be(0);
        const string hintStart = "The database 'orbit' has no xrmd process serving it. To serve it, run: ";
        var hint = $"{hintStart}xrmd server org add orbit --admin-object-id {oid}";

        (await cli.RunAsync("org", "create", "orbit", "--admin-object-id", oid.ToString().ToUpperInvariant())).Should().Be(0, cli.Err.ToString());
        cli.Out.ToString().Should().Contain("orbit");
        cli.Out.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Should().Contain(hint);

        (await cli.RunAsync("org", "create", "orbit", "--admin-object-id", oid.ToString(), "-o", "json")).Should().Be(0, cli.Err.ToString());
        using var json = JsonDocument.Parse(cli.Out.ToString());
        json.RootElement.GetProperty("database").GetString().Should().Be("orbit");
        json.RootElement.GetProperty("hint").GetString().Should().Be(hint);
    }

    // ---- Plan 3.3, Task 5: TDS port per organization ----

    private static readonly string[] LocalTds = ["--auth", "local", "--org", "acme", "--domain", "xrm.test", "--tls", "internal", "--tds-port", "5558"];

    [Fact]
    public async Task Org_add_tds_port_must_be_free()
    {
        using var cli = new TestCli();
        await InitAsync(cli, LocalTds);
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        var envBefore = File.ReadAllText(EnvPath);

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir, "--tds-port", "5558")).Should().Be(2);
        cli.Err.ToString().Should().Contain("TDS port 5558 is already used by acme");
        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir, "--tds-port", "5001")).Should().Be(2, "acme's HTTP port");
        cli.Err.ToString().Should().Contain("TDS port 5001 is already used by acme");
        File.ReadAllText(EnvPath).Should().Be(envBefore);
        runner.Calls.Should().BeEmpty();
        Directory.Exists(Path.Combine(_dir, "tds", "zenith")).Should().BeFalse();

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir, "--tds-port", "5559")).Should().Be(0, cli.Err.ToString());

        File.ReadAllText(EnvPath).Should().Contain("ORG_ZENITH_TDS_PORT=5559\n");
        // the first call starts the organization (the next ones apply its host to the proxy)
        ComposeTail(runner.Calls[0]).Should().Equal("up", "-d", "tds-certs", "xrmd-zenith");
        Directory.Exists(Path.Combine(_dir, "tds", "zenith")).Should().BeTrue();
        var services = ComposeYaml.Resolve(_dir);
        services["xrmd-zenith"].Ports.Should().Equal("0.0.0.0:5559:5558");
        services["tds-certs"].Environment["TDS_ORGS"].Should().Be("acme=acme.xrm.test zenith=zenith.xrm.test");
        cli.Out.ToString().Should().Contain("zenith.xrm.test,5559");
    }

    [Fact]
    public async Task Org_add_without_tds_port_starts_only_its_service()
    {
        using var cli = new TestCli();
        await InitAsync(cli, LocalTds);
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        ComposeTail(runner.Calls[0]).Should().Equal("up", "-d", "xrmd-zenith");
        File.ReadAllText(EnvPath).Should().NotContain("ORG_ZENITH_TDS_PORT");
        Directory.Exists(Path.Combine(_dir, "tds", "zenith")).Should().BeFalse();
    }

    [Fact]
    public async Task Org_add_tds_port_needs_the_local_identity_provider()
    {
        using var cli = new TestCli();
        await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir, "--tds-port", "5558")).Should().Be(2);

        cli.Err.ToString().Should().Contain("The SQL endpoint needs the local identity provider (--auth local)");
        File.ReadAllText(EnvPath).Should().NotContain("zenith");
    }

    [Fact]
    public async Task Org_add_tds_port_behind_your_own_proxy_needs_the_tds_pair()
    {
        using var cli = new TestCli();
        var pairDir = _dir + "-pair";
        var (cert, key) = ServerInitTests.WriteTdsPair(pairDir, DateTimeOffset.UtcNow.AddDays(90), "acme.xrm.test", "zenith.xrm.test");
        try
        {
            await InitAsync(cli, "--auth", "local", "--org", "acme", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "10.0.0.5");
            var runner = new FakeProcessRunner();
            cli.Process = runner;

            (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir, "--tds-port", "5558")).Should().Be(2);
            cli.Err.ToString().Should().Contain(Path.Combine("certs", "tds.crt"));
            File.ReadAllText(EnvPath).Should().NotContain("zenith");

            Directory.CreateDirectory(Path.Combine(_dir, "certs"));
            File.Copy(cert, Path.Combine(_dir, "certs", "tds.crt"));
            File.Copy(key, Path.Combine(_dir, "certs", "tds.key"));
            (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir, "--tds-port", "5558")).Should().Be(0, cli.Err.ToString());
            ComposeYaml.Resolve(_dir)["tds-certs"].Environment["TDS_PAIR"].Should().Be("tds");
        }
        finally
        {
            Directory.Delete(pairDir, recursive: true);
        }
    }
}
