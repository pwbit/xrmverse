using System.Net;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xrmd.Cli.Server;
using YamlDotNet.Serialization;

namespace Xrmd.Cli.Tests;

public class ComposeRendererTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xrmd-compose-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string EnvPath => Path.Combine(_dir, ".env");
    private string ComposePath => Path.Combine(_dir, "docker-compose.yml");

    private async Task InitAsync(params string[] extra)
    {
        using var cli = new TestCli();
        (await cli.RunAsync(["server", "init", "--dir", _dir, .. TestCli.Eula(extra)])).Should().Be(0, cli.Err.ToString());
        FakeSdk.Install(_dir); // `server org add` and `up` start containers, which needs the SDK
    }

    private void EditEnv(Func<string, string> edit) => File.WriteAllText(EnvPath, edit(File.ReadAllText(EnvPath)));

    public static IEnumerable<object[]> InconsistentEdits() =>
    [
        // An org removed from XRMD_ORGS whose ORG_* keys are still there.
        ["XRMD_ORGS=acme,zenith", "XRMD_ORGS=acme", "ORG_ZENITH_PORT"],
        // An org listed in XRMD_ORGS without its port or admin object id.
        ["ORG_ZENITH_PORT=5002\n", "", "ORG_ZENITH_PORT"],
        ["ORG_ZENITH_ADMIN_OBJECT_ID=00000000-0000-0000-0000-000000000001\n", "", "ORG_ZENITH_ADMIN_OBJECT_ID"],
        // Keys of an org that was never listed.
        ["XRMD_ORGS=acme,zenith", "XRMD_ORGS=acme,zenith\nORG_ORBIT_PORT=5003", "ORG_ORBIT_PORT"],
        // Invalid values.
        ["ORG_ZENITH_PORT=5002", "ORG_ZENITH_PORT=5001", "ORG_ZENITH_PORT"],
        ["ORG_ZENITH_PORT=5002", "ORG_ZENITH_PORT=http", "ORG_ZENITH_PORT"],
        ["ORG_ZENITH_ADMIN_OBJECT_ID=00000000-0000-0000-0000-000000000001", "ORG_ZENITH_ADMIN_OBJECT_ID=admin", "ORG_ZENITH_ADMIN_OBJECT_ID"],
        ["XRMD_ORGS=acme,zenith", "XRMD_ORGS=acme,Zenith", "XRMD_ORGS"],
        ["XRMD_ORGS=acme,zenith", "XRMD_ORGS=acme,zenith,acme", "XRMD_ORGS"],
        ["SQL_CONNECTION=", "SQL_CONNECTION=Server=x;Database=xrmverse", "SQL_CONNECTION"],
        ["SQL_CONNECTION=\n", "", "SQL_CONNECTION"],
        ["XRMD_ORGS=acme,zenith", "XRMD_ORGS=acme,zenith\nXRMD_ORGS=acme", "XRMD_ORGS"],
        // M10: the optional audience key of an org that is not listed, and any audience in dev mode (dev tokens use the profile Url as aud).
        ["XRMD_ORGS=acme,zenith", "XRMD_ORGS=acme,zenith\nORG_ORBIT_AUDIENCE=api://orbit", "ORG_ORBIT_AUDIENCE"],
        ["ORG_ZENITH_AUDIENCE=", "ORG_ZENITH_AUDIENCE=http://localhost:5002/", "ORG_ZENITH_AUDIENCE"],
        // SQL_CONNECTION is empty or single-quoted, so that $ and # stay literal for Compose.
        ["SQL_CONNECTION=", "SQL_CONNECTION=\"Server=x;Password=a$b\"", "SQL_CONNECTION"],
        ["SQL_CONNECTION=", "SQL_CONNECTION=Server=x", "SQL_CONNECTION"],
        // P5: MSSQL_PID is an accepted edition or a product key; empty is not "the image's default".
        ["MSSQL_PID=Express", "MSSQL_PID=Web", "MSSQL_PID"],
        ["MSSQL_PID=Express", "MSSQL_PID=", "MSSQL_PID"],
        ["MSSQL_PID=Express", "MSSQL_PID=Express\nMSSQL_PID=Developer", "MSSQL_PID"],
    ];

    [Theory]
    [MemberData(nameof(InconsistentEdits))]
    public async Task Inconsistent_env_is_rejected_naming_the_key(string find, string replace, string key)
    {
        await InitAsync("--org", "acme", "--org", "zenith");
        var compose = File.ReadAllText(ComposePath);
        File.ReadAllText(EnvPath).Should().Contain(find);
        EditEnv(text => text.Replace(find, replace));

        var act = () => Installation.Load(_dir);

        var error = act.Should().Throw<CliException>().Which;
        error.ExitCode.Should().Be(ExitCodes.Rejected);
        error.Message.Should().Contain(key).And.Contain(EnvPath);
        File.ReadAllText(ComposePath).Should().Be(compose, "nothing is rendered from an inconsistent .env");
    }

    [Fact]
    public async Task Every_org_mounts_the_sdk_folder_read_only()
    {
        foreach (var extra in new[] { Array.Empty<string>(), ["--sql-connection", "Server=sql;User Id=xrmd;Password=x"] })
        {
            var dir = Path.Combine(_dir, extra.Length == 0 ? "internal" : "external");
            using var cli = new TestCli();
            (await cli.RunAsync(["server", "init", "--dir", dir, "--org", "acme", "--org", "zenith", .. TestCli.Eula(extra)])).Should().Be(0, cli.Err.ToString());
            var services = ComposeYaml.Resolve(dir);
            foreach (var org in new[] { "acme", "zenith" })
                services[$"xrmd-{org}"].Volumes.Should().Contain("./sdk:/var/lib/xrmd/sdk:ro", org);
            services.Where(kv => !kv.Key.StartsWith("xrmd-")).SelectMany(kv => kv.Value.Volumes)
                .Should().NotContain(v => v.Contains("/var/lib/xrmd/sdk"), "only the xrmd services get the SDK");
        }
    }

    [Fact]
    public async Task Long_running_services_come_back_after_a_reboot()
    {
        await InitAsync("--org", "acme");
        var services = (Dictionary<object, object>)ComposeYaml.Raw(_dir)["services"];
        foreach (var name in new[] { "sqlserver", "xrmd-acme" })
            ((Dictionary<object, object>)services[name])["restart"].Should().Be("unless-stopped", name);
        ((Dictionary<object, object>)services["sqlserver"])["stop_grace_period"].Should().Be("60s");
    }

    [Fact]
    public async Task Rendered_org_service_requires_the_image_key()
    {
        await InitAsync("--org", "acme");
        File.ReadAllText(ComposePath).Should().Contain("image: ${XRMD_IMAGE:?set XRMD_IMAGE in .env}").And.NotContain("ghcr.io/xrmverse");
        ComposeYaml.Resolve(_dir)["xrmd-acme"].Image.Should().Be("xrmd:local");

        EditEnv(text => string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("XRMD_IMAGE="))));
        FluentActions.Invoking(() => ComposeYaml.Resolve(_dir)).Should().Throw<InvalidOperationException>().WithMessage("*XRMD_IMAGE*");
        var installation = Installation.Load(_dir);
        installation.EnsureImage("ghcr.io/pwbit/xrmd:1.4.2").Should().BeTrue();
        installation.EnsureImage("other:1").Should().BeFalse("the key is there now");
        installation.Image.Should().Be("ghcr.io/pwbit/xrmd:1.4.2");
        installation.Save();
        ComposeYaml.Resolve(_dir)["xrmd-acme"].Image.Should().Be("ghcr.io/pwbit/xrmd:1.4.2");
    }

    [Fact]
    public async Task Rendered_compose_requires_mssql_pid()
    {
        await InitAsync("--org", "acme");
        var compose = File.ReadAllText(ComposePath);
        compose.Should().Contain("MSSQL_PID: ${MSSQL_PID:?set MSSQL_PID in .env}");
        ComposeYaml.Resolve(_dir)["sqlserver"].Environment["MSSQL_PID"].Should().Be("Express");
        Installation.Load(_dir).SqlEdition.Should().Be("Express");

        // Without the key Compose refuses to start: the edition is never the image's default by accident.
        EditEnv(text => text.Replace("MSSQL_PID=Express\n", ""));
        FluentActions.Invoking(() => ComposeYaml.Resolve(_dir)).Should().Throw<InvalidOperationException>().WithMessage("*MSSQL_PID*");
        var installation = Installation.Load(_dir);
        installation.SqlEdition.Should().BeEmpty();
        installation.EnsureSqlEdition().Should().BeTrue();
        installation.SqlEdition.Should().Be("Developer");
        installation.EnsureSqlEdition().Should().BeFalse("the key is there now");
        installation.Save();
        Env()["MSSQL_PID"].Should().Be("Developer");

        // With an external SQL Server there is no sqlserver service: no key, and none is added.
        var external = Path.Combine(_dir, "external");
        using var cli = new TestCli();
        (await cli.RunAsync("server", "init", "--dir", external, "--org", "acme", "--sql-connection", "Server=sql;User Id=xrmd;Password=x")).Should().Be(0, cli.Err.ToString());
        File.ReadAllText(Path.Combine(external, "docker-compose.yml")).Should().NotContain("MSSQL_PID");
        var externalInstallation = Installation.Load(external);
        externalInstallation.EnsureSqlEdition().Should().BeFalse();
        externalInstallation.SqlEdition.Should().BeEmpty();
        // A MSSQL_PID next to SQL_CONNECTION would have no effect: refused rather than ignored.
        File.AppendAllText(Path.Combine(external, ".env"), "MSSQL_PID=Standard\n");
        FluentActions.Invoking(() => Installation.Load(external)).Should().Throw<CliException>()
            .Which.Message.Should().Contain("MSSQL_PID").And.Contain("SQL_CONNECTION");
    }

    [Fact]
    public void Legacy_installation_is_rejected_with_the_hint()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(EnvPath, "SA_PASSWORD=x\nAUTH_MODE=Development\nXRMD_PORT=5000\nSEED_ORG_NAME=xrmd\nSEED_ADMIN_OBJECT_ID=00000000-0000-0000-0000-000000000001\n");
        File.WriteAllText(ComposePath, "# Generated by `xrmd server init`. Start with: xrmd server up\nservices:\n  xrmd:\n    image: x\n");

        var error = FluentActions.Invoking(() => Installation.Load(_dir)).Should().Throw<CliException>().Which;
        error.ExitCode.Should().Be(ExitCodes.Rejected);
        error.Message.Should().Contain("predates").And.Contain("server init --org").And.Contain("--force").And.Contain("org restore");
    }

    [Fact]
    public void Missing_env_is_a_usage_error()
    {
        var error = FluentActions.Invoking(() => Installation.Load(_dir)).Should().Throw<CliException>().Which;
        error.ExitCode.Should().Be(ExitCodes.Usage);
        error.Message.Should().Contain("xrmd server init");
    }

    [Fact]
    public async Task Add_takes_the_next_free_port_and_save_keeps_hand_edited_keys()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--port", "7000");
        EditEnv(text => text.Replace("UI_ENTRA_CLIENT_ID=", "UI_ENTRA_CLIENT_ID=spa-client") + "# my note\n");
        var signingKey = Env()["AUTH_DEV_SIGNING_KEY"];

        var installation = Installation.Load(_dir);
        installation.Orgs.Select(o => (o.Name, o.Port)).Should().Equal(("acme", 7000), ("zenith", 7001));
        installation.ExternalSql.Should().BeFalse();

        var oid = Guid.NewGuid().ToString();
        installation.Add("orbit", null, oid).Should().Be(new OrgEntry("orbit", 7002, oid));
        installation.Remove("acme");
        installation.Add("adventure", null, oid).Port.Should().Be(7003, "the next free port above the highest one in use");
        installation.Save();

        var env = Env();
        env["XRMD_ORGS"].Should().Be("zenith,orbit,adventure");
        env.Keys.Should().NotContain(["ORG_ACME_PORT", "ORG_ACME_ADMIN_OBJECT_ID"]);
        env["ORG_ORBIT_PORT"].Should().Be("7002");
        env["ORG_ORBIT_ADMIN_OBJECT_ID"].Should().Be(oid);
        env["UI_ENTRA_CLIENT_ID"].Should().Be("spa-client");
        env["XRMD_IMAGE"].Should().Be("xrmd:local");
        env["AUTH_DEV_SIGNING_KEY"].Should().Be(signingKey);
        File.ReadAllText(EnvPath).Should().Contain("# my note");

        ComposeYaml.Resolve(_dir).Keys.Where(k => k.StartsWith("xrmd", StringComparison.Ordinal))
            .Should().Equal("xrmd-zenith", "xrmd-orbit", "xrmd-adventure");
        Installation.Load(_dir).Orgs.Select(o => o.Name).Should().Equal("zenith", "orbit", "adventure");
    }

    [Fact]
    public async Task Add_rejects_invalid_names_duplicates_and_used_ports_with_usage()
    {
        await InitAsync("--org", "acme");
        var installation = Installation.Load(_dir);
        var oid = Guid.NewGuid().ToString();

        foreach (var act in new Action[]
        {
            () => installation.Add("Acme", null, oid),
            () => installation.Add("acme", null, oid),
            () => installation.Add("msdb", null, oid),
            () => installation.Add("zenith", 5001, oid),
            () => installation.Add("zenith", 0, oid),
            () => installation.Add("zenith", null, "admin"),
        })
            act.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Usage);
        installation.Orgs.Should().ContainSingle();
        FluentActions.Invoking(() => installation.Remove("zenith")).Should().Throw<CliException>();
    }

    [Fact]
    public async Task Load_rejects_a_compose_file_it_did_not_generate()
    {
        await InitAsync("--org", "acme");
        File.WriteAllText(ComposePath, "services:\n  mine: {}\n");
        var env = File.ReadAllText(EnvPath);

        var error = FluentActions.Invoking(() => Installation.Load(_dir)).Should().Throw<CliException>().Which;

        error.ExitCode.Should().Be(ExitCodes.Rejected);
        error.Message.Should().Contain(ComposePath);
        File.ReadAllText(ComposePath).Should().Be("services:\n  mine: {}\n");
        File.ReadAllText(EnvPath).Should().Be(env);
    }

    [Fact]
    public async Task Save_is_atomic_and_leaves_no_temporary_files()
    {
        await InitAsync("--org", "acme");
        var installation = Installation.Load(_dir);
        installation.Add("zenith", null, Guid.NewGuid().ToString());
        installation.Save();

        System.IO.Directory.GetFiles(_dir).Select(Path.GetFileName).Should().BeEquivalentTo([".env", "docker-compose.yml"]);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(EnvPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Installation.Load(_dir).Orgs.Select(o => o.Name).Should().Equal("acme", "zenith");
    }

    [Fact]
    public async Task An_installation_without_organizations_is_refused()
    {
        await InitAsync("--org", "acme");
        var compose = File.ReadAllText(ComposePath);
        var env = File.ReadAllText(EnvPath);

        var installation = Installation.Load(_dir);
        installation.Remove("acme");
        var save = FluentActions.Invoking(installation.Save).Should().Throw<CliException>().Which;
        save.ExitCode.Should().Be(ExitCodes.Rejected);
        save.Message.Should().Contain("at least one organization");
        File.ReadAllText(ComposePath).Should().Be(compose);
        File.ReadAllText(EnvPath).Should().Be(env);

        EditEnv(text => Regex.Replace(text, "(?m)^(XRMD_ORGS=.*|ORG_ACME_.*)$", m => m.Value.StartsWith("XRMD_ORGS") ? "XRMD_ORGS=" : ""));
        var load = FluentActions.Invoking(() => Installation.Load(_dir)).Should().Throw<CliException>().Which;
        load.ExitCode.Should().Be(ExitCodes.Rejected);
        load.Message.Should().Contain("XRMD_ORGS");
    }

    private async Task InitEntraAsync() =>
        await InitAsync("--auth", "entra", "--tenant-id", Guid.NewGuid().ToString(), "--org", "acme", "--org", "zenith",
            "--admin-object-id", Guid.NewGuid().ToString(), "--admin-object-id", Guid.NewGuid().ToString());

    [Fact]
    public async Task Audience_defaults_to_the_local_url_and_an_explicit_value_is_kept()
    {
        await InitEntraAsync();
        Env()["ORG_ACME_AUDIENCE"].Should().BeEmpty();
        Env()["ORG_ZENITH_AUDIENCE"].Should().BeEmpty();
        File.ReadAllText(ComposePath).Should().Contain(
            "Auth__Audience: \"${ORG_ACME_AUDIENCE:-http://localhost:${ORG_ACME_PORT:?set ORG_ACME_PORT in .env}/}\"");

        EditEnv(text => text.Replace("ORG_ZENITH_AUDIENCE=", "ORG_ZENITH_AUDIENCE=api://zenith-api"));
        var installation = Installation.Load(_dir);
        installation.Orgs.Select(o => o.Audience).Should().Equal(null, "api://zenith-api");
        installation.Add("orbit", null, Guid.NewGuid().ToString());
        installation.Save();

        var env = Env();
        env["ORG_ZENITH_AUDIENCE"].Should().Be("api://zenith-api", "a value the user set is kept");
        env["ORG_ORBIT_AUDIENCE"].Should().BeEmpty();
        var services = ComposeYaml.Resolve(_dir);
        services["xrmd-acme"].Environment["Auth__Audience"].Should().Be("http://localhost:5001/");
        services["xrmd-zenith"].Environment["Auth__Audience"].Should().Be("api://zenith-api");
        services["xrmd-orbit"].Environment["Auth__Audience"].Should().Be("http://localhost:5003/");

        installation = Installation.Load(_dir);
        installation.Remove("zenith");
        installation.Save();
        Env().Keys.Should().NotContain(["ORG_ZENITH_PORT", "ORG_ZENITH_ADMIN_OBJECT_ID", "ORG_ZENITH_AUDIENCE"]);
    }

    [Theory]
    [InlineData("AUTH_MODE=\n")]
    [InlineData("")]
    public async Task Load_with_empty_auth_mode_applies_the_dev_rules(string authModeLine)
    {
        // The compose file applies ${AUTH_MODE:-Development}: the model must apply the same default (m5).
        await InitAsync("--org", "acme", "--org", "zenith");
        EditEnv(text => text.Replace("AUTH_MODE=Development\n", authModeLine));
        Installation.Load(_dir).AuthMode.Should().Be("Development");

        EditEnv(text => text.Replace("ORG_ZENITH_AUDIENCE=", "ORG_ZENITH_AUDIENCE=api://zenith-api"));
        var error = FluentActions.Invoking(() => Installation.Load(_dir)).Should().Throw<CliException>().Which;
        error.ExitCode.Should().Be(ExitCodes.Rejected);
        error.Message.Should().Contain("ORG_ZENITH_AUDIENCE");

        EditEnv(text => text.Replace("ORG_ZENITH_AUDIENCE=api://zenith-api", "ORG_ZENITH_AUDIENCE="));
        using var cli = new TestCli { Process = new FakeProcessRunner() };
        (await cli.RunAsync("server", "org", "add", "orbit", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        ComposeYaml.ReadEnv(_dir)["ORG_ORBIT_ADMIN_OBJECT_ID"].Should().Be("00000000-0000-0000-0000-000000000001");
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("/relative")]
    public async Task Audience_that_is_not_an_absolute_uri_is_rejected(string value)
    {
        await InitEntraAsync();
        EditEnv(text => text.Replace("ORG_ZENITH_AUDIENCE=", "ORG_ZENITH_AUDIENCE=" + value));

        var error = FluentActions.Invoking(() => Installation.Load(_dir)).Should().Throw<CliException>().Which;
        error.ExitCode.Should().Be(ExitCodes.Rejected);
        error.Message.Should().Contain("ORG_ZENITH_AUDIENCE");
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("a")]
    [InlineData("abcdefghijabcdefghijabcdefghij12")]
    public void ValidateOrgName_accepts_the_rule(string name) => Installation.ValidateOrgName(name);

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task Generated_compose_is_valid()
    {
        foreach (var (sub, extra) in new[]
        {
            ("internal", new[] { "--org", "acme", "--org", "zenith" }),
            ("external", new[] { "--org", "acme", "--org", "zenith", "--sql-connection", "Server=sql.example.com;User Id=xrmd;Password=\"p$a#ss;w\";Encrypt=True" }),
            ("entra", new[] { "--org", "acme", "--org", "zenith", "--auth", "entra", "--tenant-id", Guid.NewGuid().ToString(),
                "--admin-object-id", Guid.NewGuid().ToString(), "--admin-object-id", Guid.NewGuid().ToString() }),
        })
        {
            var dir = Path.Combine(_dir, sub);
            using var cli = new TestCli();
            (await cli.RunAsync(["server", "init", "--dir", dir, .. TestCli.Eula(extra)])).Should().Be(0, cli.Err.ToString());
            if (sub == "entra") // M10: an explicit audience for zenith; acme keeps the nested default.
            {
                var envFile = Path.Combine(dir, ".env");
                File.WriteAllText(envFile, File.ReadAllText(envFile).Replace("ORG_ZENITH_AUDIENCE=", "ORG_ZENITH_AUDIENCE=api://zenith-api"));
                Installation.Load(dir);
            }

            Directory.CreateDirectory(Path.Combine(dir, "sdk")); // the folder `xrmd sdk install --dir` fills
            var quiet = await DockerComposeAsync(dir, "config", "-q");
            quiet.ExitCode.Should().Be(0, $"{sub}: {quiet.StdErr}");
            quiet.StdErr.Should().BeEmpty($"{sub}: no warning (unset variable, obsolete key) is expected");

            var json = await DockerComposeAsync(dir, "config", "--format", "json");
            json.ExitCode.Should().Be(0, json.StdErr);
            using var doc = JsonDocument.Parse(json.StdOut);
            var services = doc.RootElement.GetProperty("services");
            foreach (var (org, port) in new[] { ("acme", 5001), ("zenith", 5002) })
            {
                var service = services.GetProperty($"xrmd-{org}");
                var environment = service.GetProperty("environment");
                environment.GetProperty("Auth__Audience").GetString().Should().Be(
                    sub == "entra" && org == "zenith" ? "api://zenith-api" : $"http://localhost:{port}/");
                environment.GetProperty("Seed__OrgName").GetString().Should().Be(org);
                new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(environment.GetProperty("ConnectionStrings__Org").GetString())
                    .InitialCatalog.Should().Be(org);
                service.GetProperty("ports")[0].GetProperty("published").GetString().Should().Be(port.ToString());
                var sdkMount = service.GetProperty("volumes").EnumerateArray().Single(v => v.GetProperty("target").GetString() == "/var/lib/xrmd/sdk");
                sdkMount.GetProperty("type").GetString().Should().Be("bind");
                sdkMount.GetProperty("source").GetString().Should().Be(Path.Combine(dir, "sdk"));
                sdkMount.GetProperty("read_only").GetBoolean().Should().BeTrue();
            }
            if (sub != "external")
                services.GetProperty("sqlserver").GetProperty("environment").GetProperty("MSSQL_PID").GetString().Should().Be("Express");
            if (sub == "external")
            {
                services.TryGetProperty("sqlserver", out _).Should().BeFalse();
                // `config` prints a re-loadable file, so a literal $ of a value comes out escaped as $$.
                new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
                    services.GetProperty("xrmd-acme").GetProperty("environment").GetProperty("ConnectionStrings__Org").GetString()!.Replace("$$", "$"))
                    .Password.Should().Be("p$a#ss;w");
            }
        }
    }

    /// <summary>The real <c>docker compose config</c> accepts the proxy mode in every TLS mode, without a warning.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task Generated_proxy_compose_is_valid()
    {
        foreach (var (sub, extra) in new[]
        {
            ("internal", new[] { "--org", "acme", "--org", "zenith", "--org", "orbit", "--domain", "xrm.test", "--public-host", "zenith=crm.example.com", "--tls", "internal" }),
            ("acme", new[] { "--org", "acme", "--domain", "xrm.test", "--tls", "acme", "--acme-email", "ops@example.com", "--expose-ports" }),
            ("files", new[] { "--org", "acme", "--domain", "xrm.test", "--tls", "internal", "--edge-subnet", "10.20.30.0/24" }),
        })
        {
            var dir = Path.Combine(_dir, sub);
            using var cli = new TestCli();
            (await cli.RunAsync(["server", "init", "--dir", dir, .. TestCli.Eula(extra)])).Should().Be(0, cli.Err.ToString());
            if (sub == "files")
            {
                var envFile = Path.Combine(dir, ".env");
                File.WriteAllText(envFile, File.ReadAllText(envFile).Replace("TLS_MODE=internal", "TLS_MODE=files"));
                Installation.Load(dir).Save();
            }
            Directory.CreateDirectory(Path.Combine(dir, "sdk"));

            var quiet = await DockerComposeAsync(dir, "config", "-q");
            quiet.ExitCode.Should().Be(0, $"{sub}: {quiet.StdErr}");
            quiet.StdErr.Should().BeEmpty($"{sub}: no warning (unset variable, obsolete key) is expected");

            var json = await DockerComposeAsync(dir, "config", "--format", "json");
            json.ExitCode.Should().Be(0, json.StdErr);
            using var doc = JsonDocument.Parse(json.StdOut);
            var proxy = doc.RootElement.GetProperty("services").GetProperty("proxy");
            proxy.GetProperty("image").GetString().Should().Be(ComposeRenderer.CaddyImage);
            proxy.GetProperty("ports").EnumerateArray().Select(p => (p.GetProperty("host_ip").GetString(), p.GetProperty("published").GetString(), p.GetProperty("target").GetInt32()))
                .Should().Equal(("0.0.0.0", "80", 80), ("0.0.0.0", "443", 443));
            var address = Installation.Load(dir).ProxyAddress;
            proxy.GetProperty("networks").GetProperty("edge").GetProperty("ipv4_address").GetString().Should().Be(address);
            var ipam = doc.RootElement.GetProperty("networks").GetProperty("edge").GetProperty("ipam").GetProperty("config")[0];
            ipam.GetProperty("subnet").GetString().Should().Be(Installation.Load(dir).EdgeSubnet);
            ipam.GetProperty("ip_range").GetString().Should().Be(Installation.Load(dir).EdgeIpRange);
            var acme = doc.RootElement.GetProperty("services").GetProperty("xrmd-acme");
            acme.GetProperty("networks").EnumerateObject().Select(n => n.Name).Should().BeEquivalentTo("default", "edge");
            acme.GetProperty("environment").GetProperty("Proxy__TrustedProxies").GetString().Should().Be(address);
            var mounts = proxy.GetProperty("volumes").EnumerateArray().Select(v => v.GetProperty("target").GetString()).ToList();
            mounts.Should().Contain("/etc/caddy/Caddyfile").And.Contain("/data");
            if (sub == "files") mounts.Should().Contain("/certs");
            else mounts.Should().NotContain("/certs");
        }
    }

    [Fact]
    public async Task Caddy_mode_publishes_no_org_port()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal");

        var services = ComposeYaml.Resolve(_dir);
        foreach (var org in new[] { "acme", "zenith" })
        {
            var service = services[$"xrmd-{org}"];
            service.Ports.Should().BeEmpty("the proxy reaches the organization on the compose network (H6)");
            service.Environment["Proxy__TrustedProxies"].Should().Be("172.31.250.10", "only the proxy's fixed address is trusted (H3)");
            service.Environment["Proxy__AllowedHosts"].Should().Be($"{org}.xrm.test");
        }
        Regex.Matches(File.ReadAllText(ComposePath), "ports:").Should().ContainSingle("only the proxy publishes ports (H6)");
        ComposeYaml.ReadEnv(_dir)["ORG_ACME_PORT"].Should().Be("5001", "ORG_<ORG>_PORT stays in .env in every mode (H6)");
    }

    [Fact]
    public async Task Edge_subnet_moves_the_trusted_proxy_address()
    {
        await InitAsync("--org", "acme", "--domain", "xrm.test", "--tls", "internal", "--edge-subnet", "10.20.30.0/24");

        Installation.Load(_dir).ProxyAddress.Should().Be("10.20.30.10");
        ComposeYaml.Resolve(_dir)["xrmd-acme"].Environment["Proxy__TrustedProxies"].Should().Be("10.20.30.10");
    }

    [Theory]
    [InlineData("", "172.31.250.10", "172.31.250.128/25")] // the default of existing installations
    [InlineData("10.20.0.0/16", "10.20.0.10", "10.20.128.0/17")]
    [InlineData("10.20.30.32/27", "10.20.30.42", "10.20.30.48/28")]
    public async Task Edge_ip_range_keeps_the_proxy_address_out_of_dynamic_assignment(string edgeSubnet, string proxyAddress, string ipRange)
    {
        // Docker assigns container addresses from the start of the subnet: without an ip_range the ninth organization
        // behind the proxy would take .10, the proxy's fixed and trusted address.
        var subnetArgs = edgeSubnet.Length == 0 ? Array.Empty<string>() : ["--edge-subnet", edgeSubnet];
        await InitAsync(["--org", "acme", "--domain", "xrm.test", "--tls", "internal", .. subnetArgs]);

        var installation = Installation.Load(_dir);
        installation.ProxyAddress.Should().Be(proxyAddress);
        installation.EdgeIpRange.Should().Be(ipRange);
        IPNetwork.Parse(ipRange).Contains(IPAddress.Parse(proxyAddress)).Should().BeFalse();
        var edge = (Dictionary<object, object>)((Dictionary<object, object>)ComposeYaml.Raw(_dir)["networks"])["edge"];
        var config = (Dictionary<object, object>)((List<object>)((Dictionary<object, object>)edge["ipam"])["config"])[0];
        config["subnet"].Should().Be(installation.EdgeSubnet);
        config["ip_range"].Should().Be(ipRange);
    }

    [Fact]
    public async Task Expose_ports_keeps_the_loopback_port()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal", "--expose-ports");

        var services = ComposeYaml.Resolve(_dir);
        services["xrmd-acme"].Ports.Should().Equal("127.0.0.1:5001:5000");
        services["xrmd-zenith"].Ports.Should().Equal("127.0.0.1:5002:5000");
        services["xrmd-acme"].Environment["Proxy__TrustedProxies"].Should().Be("172.31.250.10");
        ComposeYaml.ReadEnv(_dir)["EXPOSE_PORTS"].Should().Be("true");
    }

    [Fact]
    public async Task External_mode_publishes_the_port_and_trusts_the_given_proxies()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--no-proxy",
            "--trusted-proxy", "10.0.0.5", "--trusted-proxy", "10.1.0.0/16");

        Installation.Load(_dir).Proxy.Should().Be(ProxyMode.External);
        var env = ComposeYaml.ReadEnv(_dir);
        env["PROXY_MODE"].Should().Be("external");
        env["TRUSTED_PROXIES"].Should().Be("10.0.0.5,10.1.0.0/16");
        env["TLS_MODE"].Should().BeEmpty("TLS is the customer's proxy's business");
        var services = ComposeYaml.Resolve(_dir);
        services["xrmd-acme"].Ports.Should().Equal("127.0.0.1:5001:5000");
        services["xrmd-acme"].Environment["Proxy__TrustedProxies"].Should().Be("10.0.0.5,10.1.0.0/16");
        services["xrmd-acme"].Environment["Proxy__AllowedHosts"].Should().Be("acme.xrm.test");
        services["xrmd-acme"].Environment["Auth__Audience"].Should().Be("https://acme.xrm.test/");

        EditEnv(text => text + "XRMD_BIND=0.0.0.0\n");
        ComposeYaml.Resolve(_dir)["xrmd-zenith"].Ports.Should().Equal(new[] { "0.0.0.0:5002:5000" }, "external mode publishes on XRMD_BIND (H6)");
    }

    [Fact]
    public async Task Org_without_a_host_keeps_its_port_beside_a_proxied_one()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--public-host", "acme=acme.example.com", "--tls", "internal");

        var services = ComposeYaml.Resolve(_dir);
        services["xrmd-acme"].Ports.Should().BeEmpty();
        services["xrmd-zenith"].Ports.Should().Equal("127.0.0.1:5002:5000");
        services["xrmd-zenith"].Environment.Keys.Should().NotContain(["Proxy__TrustedProxies", "Proxy__AllowedHosts"]);
        services["xrmd-zenith"].Environment["Auth__Audience"].Should().Be("http://localhost:5002/");
    }

    [Fact]
    public async Task No_host_no_proxy_service()
    {
        await InitAsync("--org", "acme", "--org", "zenith");

        ComposeYaml.Resolve(_dir).Keys.Should().NotContain("proxy");
        var raw = ComposeYaml.Raw(_dir);
        raw.Keys.Should().NotContain("networks", "Review Focus 1: no edge network without a host");
        File.ReadAllText(ComposePath).Should().NotContain("caddy").And.NotContain("edge");
        File.Exists(Path.Combine(_dir, "Caddyfile")).Should().BeFalse();
    }

    [Fact]
    public async Task External_mode_no_proxy_service()
    {
        await InitAsync("--org", "acme", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "10.0.0.5");

        ComposeYaml.Resolve(_dir).Keys.Should().NotContain("proxy");
        ComposeYaml.Raw(_dir).Keys.Should().NotContain("networks");
        File.ReadAllText(ComposePath).Should().NotContain("caddy").And.NotContain("edge");
        File.Exists(Path.Combine(_dir, "Caddyfile")).Should().BeFalse("your own proxy has its own configuration");
    }

    [Fact]
    public async Task Proxy_service_binds_80_and_443_on_the_edge_address()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--org", "orbit", "--domain", "xrm.test", "--public-host", "orbit=crm.example.com",
            "--tls", "internal", "--edge-subnet", "10.20.30.0/24");

        var proxy = ComposeYaml.Resolve(_dir)["proxy"];
        proxy.Image.Should().Be(ComposeRenderer.CaddyImage).And.MatchRegex(@"^caddy:2\.\d+\.\d+$", "H10: a pinned 2.x patch tag");
        proxy.Ports.Should().Equal("0.0.0.0:80:80", "0.0.0.0:443:443");
        proxy.Volumes.Should().Equal("./Caddyfile:/etc/caddy/Caddyfile:ro", "caddy-data:/data");
        proxy.DependsOn.Should().BeEquivalentTo("xrmd-acme", "xrmd-zenith", "xrmd-orbit");

        var raw = ComposeYaml.Raw(_dir);
        var services = (Dictionary<object, object>)raw["services"];
        var proxyNetworks = (Dictionary<object, object>)((Dictionary<object, object>)services["proxy"])["networks"];
        ((Dictionary<object, object>)proxyNetworks["edge"])["ipv4_address"].Should().Be("10.20.30.10");
        foreach (var org in new[] { "acme", "zenith", "orbit" })
            ((List<object>)((Dictionary<object, object>)services[$"xrmd-{org}"])["networks"]).Should().Equal(["default", "edge"],
                "the proxy reaches the organization on edge; SQL Server stays on default");
        var edge = (Dictionary<object, object>)((Dictionary<object, object>)raw["networks"])["edge"];
        var subnet = ((Dictionary<object, object>)((List<object>)((Dictionary<object, object>)edge["ipam"])["config"])[0])["subnet"];
        subnet.Should().Be("10.20.30.0/24");
        var ipRange = ((Dictionary<object, object>)((List<object>)((Dictionary<object, object>)edge["ipam"])["config"])[0])["ip_range"];
        ipRange.Should().Be("10.20.30.128/25", "Docker assigns the other containers from the upper half, never the proxy's .10");
        ((Dictionary<object, object>)raw["volumes"]).Keys.Should().Contain("caddy-data");

        EditEnv(text => text + "PROXY_BIND=192.0.2.7\n");
        ComposeYaml.Resolve(_dir)["proxy"].Ports.Should().Equal("192.0.2.7:80:80", "192.0.2.7:443:443");
    }

    [Fact]
    public async Task Files_mode_mounts_the_certificates_read_only()
    {
        await InitAsync("--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        EditEnv(text => text.Replace("TLS_MODE=internal", "TLS_MODE=files"));
        Installation.Load(_dir).Save();

        ComposeYaml.Resolve(_dir)["proxy"].Volumes.Should().Equal("./Caddyfile:/etc/caddy/Caddyfile:ro", "caddy-data:/data", "./certs:/certs:ro");
    }

    [Fact]
    public async Task Org_services_trust_only_the_proxy_address()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--org", "orbit", "--public-host", "acme=acme.example.com",
            "--public-host", "zenith=zenith.example.com", "--tls", "internal");

        var installation = Installation.Load(_dir);
        var services = ComposeYaml.Resolve(_dir);
        foreach (var org in new[] { "acme", "zenith" })
            services[$"xrmd-{org}"].Environment["Proxy__TrustedProxies"].Should().Be(installation.ProxyAddress);
        var raw = (Dictionary<object, object>)ComposeYaml.Raw(_dir)["services"];
        var proxyNetworks = (Dictionary<object, object>)((Dictionary<object, object>)raw["proxy"])["networks"];
        ((Dictionary<object, object>)proxyNetworks["edge"])["ipv4_address"].Should().Be(installation.ProxyAddress);
        // An organization without a host is not behind the proxy: no forwarded headers, not on edge, not a dependency of the proxy.
        services["xrmd-orbit"].Environment.Keys.Should().NotContain("Proxy__TrustedProxies");
        ((Dictionary<object, object>)raw["xrmd-orbit"]).Keys.Should().NotContain("networks");
        services["proxy"].DependsOn.Should().BeEquivalentTo("xrmd-acme", "xrmd-zenith");
    }

    [Fact]
    public async Task Dev_audience_message_names_the_public_url()
    {
        await InitAsync("--org", "acme", "--domain", "xrm.test", "--tls", "internal");
        EditEnv(text => text.Replace("ORG_ACME_AUDIENCE=", "ORG_ACME_AUDIENCE=api://acme"));

        var act = () => Installation.Load(_dir);
        act.Should().Throw<CliException>().Which.Message.Should().Contain("ORG_ACME_AUDIENCE").And.Contain("(https://acme.xrm.test/)")
            .And.NotContain("localhost");
    }

    /// <summary>The shell script of the <c>tds-certs</c> service, as bash runs it (Compose's <c>$$</c> unescaped).</summary>
    private string TdsCertsScript()
    {
        var service = (Dictionary<object, object>)((Dictionary<object, object>)ComposeYaml.Raw(_dir)["services"])["tds-certs"];
        var entrypoint = ((List<object>)service["entrypoint"]).Cast<string>().ToList();
        entrypoint.Take(2).Should().Equal("bash", "-c");
        return entrypoint[2].Replace("$$", "$", StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the <c>tds-certs</c> script once with bash, its paths moved into <paramref name="root"/> (<c>caddy/</c>, <c>certs/</c>,
    /// <c>out/</c>) and, unless the test runs as root, the server's group replaced by the test user's.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunTdsCertsAsync(string script, string root, string orgs, string pair)
    {
        var gid = IsRoot ? "1654" : (await RunAsync("id", "-g")).Trim();
        script = script
            .Replace("caddy=/data/caddy/certificates\n", $"caddy={root}/caddy\n", StringComparison.Ordinal)
            .Replace("certs=/certs\n", $"certs={root}/certs\n", StringComparison.Ordinal)
            .Replace("out=/out\n", $"out={root}/out\n", StringComparison.Ordinal)
            .Replace("gid=1654\n", $"gid={gid}\n", StringComparison.Ordinal);
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.Environment["TDS_ORGS"] = orgs;
        start.Environment["TDS_PAIR"] = pair;
        start.Environment["TDS_INTERVAL"] = "43200";
        start.Environment["TDS_ONCE"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static bool IsRoot => !OperatingSystem.IsWindows() && Environment.UserName == "root";

    private static async Task<string> RunAsync(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    private static readonly UnixFileMode Mode0640 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    [Fact]
    public async Task Tds_certs_service_copies_only_regular_files()
    {
        if (OperatingSystem.IsWindows()) return; // bash, symbolic links and POSIX modes
        await InitAsync("--auth", "local", "--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal", "--tds-port", "5558");
        var script = TdsCertsScript();
        var root = Path.Combine(_dir, "tree");
        const string host = "acme.xrm.test";
        // Caddy's storage: a regular pair under one issuer, and under a newer issuer a pair whose files are symbolic links to a
        // secret elsewhere; an issuer folder that is itself a symbolic link to another tree.
        var regular = Path.Combine(root, "caddy", "acme-v02.api.letsencrypt.org-directory", host);
        Directory.CreateDirectory(regular);
        File.WriteAllText(Path.Combine(regular, host + ".crt"), "regular certificate\n");
        File.WriteAllText(Path.Combine(regular, host + ".key"), "regular key\n");
        File.SetLastWriteTimeUtc(Path.Combine(regular, host + ".crt"), DateTime.UtcNow.AddDays(-10));
        var secret = Path.Combine(root, "secret");
        File.WriteAllText(secret, "not a certificate\n");
        var linked = Path.Combine(root, "caddy", "local", host);
        Directory.CreateDirectory(linked);
        File.CreateSymbolicLink(Path.Combine(linked, host + ".crt"), secret);
        File.CreateSymbolicLink(Path.Combine(linked, host + ".key"), secret);
        var elsewhere = Path.Combine(root, "elsewhere", host);
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, host + ".crt"), "linked folder certificate\n");
        File.WriteAllText(Path.Combine(elsewhere, host + ".key"), "linked folder key\n");
        Directory.CreateSymbolicLink(Path.Combine(root, "caddy", "zz-linked"), Path.Combine(root, "elsewhere"));
        // ./tds: acme's folder as `xrmd server init` creates it; zenith's is a symbolic link to a folder outside ./tds.
        var outAcme = Path.Combine(root, "out", "acme");
        Directory.CreateDirectory(outAcme, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "out", "zenith"), outside);
        var zenithStore = Path.Combine(root, "caddy", "acme-v02.api.letsencrypt.org-directory", "zenith.xrm.test");
        Directory.CreateDirectory(zenithStore);
        File.WriteAllText(Path.Combine(zenithStore, "zenith.xrm.test.crt"), "zenith certificate\n");
        File.WriteAllText(Path.Combine(zenithStore, "zenith.xrm.test.key"), "zenith key\n");

        var (code, output) = await RunTdsCertsAsync(script, root, "acme=acme.xrm.test zenith=zenith.xrm.test", "");

        code.Should().Be(0, output);
        File.ReadAllText(Path.Combine(outAcme, host + ".crt")).Should().Be("regular certificate\n", output);
        File.ReadAllText(Path.Combine(outAcme, host + ".key")).Should().Be("regular key\n");
        foreach (var name in new[] { host + ".crt", host + ".key" })
        {
            var copy = Path.Combine(outAcme, name);
            new FileInfo(copy).LinkTarget.Should().BeNull();
            File.GetUnixFileMode(copy).Should().Be(Mode0640);
            if (IsRoot) (await RunAsync("stat", "-c", "%g", copy)).Trim().Should().Be("1654");
        }
        if (IsRoot) (await RunAsync("stat", "-c", "%g", outAcme)).Trim().Should().Be("1654");
        Directory.EnumerateFileSystemEntries(outAcme).Select(Path.GetFileName).Should().BeEquivalentTo([host + ".crt", host + ".key"], "no temporary file is left");
        Directory.EnumerateFileSystemEntries(outside).Should().BeEmpty("a symbolic link in ./tds is never followed");
        output.Should().Contain("zenith");

        // A second run with nothing new changes nothing; a renewal (a newer regular pair) replaces the copy.
        var before = File.GetLastWriteTimeUtc(Path.Combine(outAcme, host + ".crt"));
        (await RunTdsCertsAsync(script, root, "acme=acme.xrm.test", "")).ExitCode.Should().Be(0);
        File.GetLastWriteTimeUtc(Path.Combine(outAcme, host + ".crt")).Should().Be(before);
        var renewed = Path.Combine(root, "caddy", "acme-v02-staging", host);
        Directory.CreateDirectory(renewed);
        File.WriteAllText(Path.Combine(renewed, host + ".crt"), "renewed certificate\n");
        File.WriteAllText(Path.Combine(renewed, host + ".key"), "renewed key\n");
        (await RunTdsCertsAsync(script, root, "acme=acme.xrm.test", "")).ExitCode.Should().Be(0);
        File.ReadAllText(Path.Combine(outAcme, host + ".crt")).Should().Be("renewed certificate\n");
        File.ReadAllText(Path.Combine(outAcme, host + ".key")).Should().Be("renewed key\n");
    }

    [Fact]
    public async Task Tds_certs_service_copies_the_given_pair_and_refuses_a_linked_one()
    {
        if (OperatingSystem.IsWindows()) return;
        await InitAsync("--auth", "local", "--org", "acme", "--domain", "xrm.test", "--tls", "internal", "--tds-port", "5558");
        var script = TdsCertsScript();
        var root = Path.Combine(_dir, "tree");
        Directory.CreateDirectory(Path.Combine(root, "certs"));
        Directory.CreateDirectory(Path.Combine(root, "out", "acme"));
        File.WriteAllText(Path.Combine(root, "certs", "server.crt"), "given certificate\n");
        File.WriteAllText(Path.Combine(root, "secret"), "secret\n");
        File.CreateSymbolicLink(Path.Combine(root, "certs", "server.key"), Path.Combine(root, "secret"));

        (await RunTdsCertsAsync(script, root, "acme=acme.xrm.test", "server")).ExitCode.Should().Be(0);
        Directory.EnumerateFileSystemEntries(Path.Combine(root, "out", "acme")).Should().BeEmpty("the key is a symbolic link");

        File.Delete(Path.Combine(root, "certs", "server.key"));
        File.WriteAllText(Path.Combine(root, "certs", "server.key"), "given key\n");
        (await RunTdsCertsAsync(script, root, "acme=acme.xrm.test", "server")).ExitCode.Should().Be(0);
        File.ReadAllText(Path.Combine(root, "out", "acme", "acme.xrm.test.crt")).Should().Be("given certificate\n");
        File.ReadAllText(Path.Combine(root, "out", "acme", "acme.xrm.test.key")).Should().Be("given key\n");
        File.GetUnixFileMode(Path.Combine(root, "out", "acme", "acme.xrm.test.key")).Should().Be(Mode0640);
    }

    // ---- plan 2.7a, Task 8: PostgreSQL ----------------------------------------------------------------------------------------

    private async Task<TestCli> InitPostgresAsync(params string[] extra)
    {
        var cli = new TestCli();
        (await cli.RunAsync(["server", "init", "--dir", _dir, "--database", "postgresql", .. extra])).Should().Be(0, cli.Err.ToString());
        return cli;
    }

    [Fact]
    public async Task Database_postgresql_renders_a_postgres_service_and_no_sql_server()
    {
        using var cli = await InitPostgresAsync("--org", "acme", "--org", "zenith");

        var env = Env();
        env["STORAGE_PROVIDER"].Should().Be("PostgreSql");
        env["POSTGRES_PASSWORD"].Should().MatchRegex("^[A-Za-z0-9]{20,}$");
        env.Keys.Should().NotContain(["SA_PASSWORD", "MSSQL_PID", "ACCEPT_EULA", "SQL_BACKUP_DIRECTORY"]);
        (cli.Out.ToString() + cli.Err.ToString()).Should().NotContain("SQL Server", "no SQL Server license is asked for or accepted");

        var raw = ComposeYaml.Raw(_dir);
        var services = (Dictionary<object, object>)raw["services"];
        services.Keys.Cast<string>().Should().BeEquivalentTo(["postgres", "xrmd-acme", "xrmd-zenith"]);
        var postgres = (Dictionary<object, object>)services["postgres"];
        postgres["image"].Should().Be("postgres:16-alpine");
        postgres["restart"].Should().Be("unless-stopped");
        ((List<object>)postgres["volumes"]).Should().Contain("pg-data:/var/lib/postgresql/data");
        string.Join(' ', ((List<object>)((Dictionary<object, object>)postgres["healthcheck"])["test"]).Cast<string>()).Should().Contain("pg_isready");
        // Plan 2.7b, ruling Q8: the backups volume, where pg_dump writes in each organization's container; no backups-init (above).
        ((Dictionary<object, object>)raw["volumes"]).Keys.Cast<string>().Should().Contain(["pg-data", "backups"]).And.NotContain("mssql-data");

        var resolved = ComposeYaml.Resolve(_dir);
        resolved["postgres"].Environment["POSTGRES_PASSWORD"].Should().Be(env["POSTGRES_PASSWORD"]);
        foreach (var org in new[] { "acme", "zenith" })
        {
            var service = resolved["xrmd-" + org];
            service.Environment["Storage__Provider"].Should().Be("PostgreSql");
            service.DependsOn.Should().Contain("postgres").And.NotContain("sqlserver");
            // An Npgsql connection string per organization: its database is the organization.
            var connection = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = service.Environment["ConnectionStrings__Org"] };
            connection["Host"].Should().Be("postgres");
            connection["Database"].Should().Be(org);
            connection["Username"].Should().Be("postgres");
            connection["Password"].Should().Be(env["POSTGRES_PASSWORD"]);
            service.Volumes.Should().Contain("backups:/var/lib/xrmd/backups").And.NotContain(v => v.Contains("/var/opt/mssql"));
            service.Environment.Keys.Should().NotContain("Admin__BackupDirectory", "the server's default is /var/lib/xrmd/backups");
            service.DependsOn.Should().NotContain("backups-init");
        }

        var installation = Installation.Load(_dir);
        installation.Provider.Should().Be(Xrmd.Abstractions.Storage.StorageProvider.PostgreSql);
        installation.SqlEulaAccepted.Should().BeTrue("there is no SQL Server to accept terms for");
    }

    [Fact]
    public async Task Database_postgresql_with_an_external_server_renders_no_postgres_service()
    {
        using var cli = await InitPostgresAsync("--org", "acme", "--sql-connection", "Host=pg.example.com;Username=xrmd;Password=s3cret");

        var env = Env();
        env["STORAGE_PROVIDER"].Should().Be("PostgreSql");
        env.Keys.Should().NotContain(["POSTGRES_PASSWORD", "SA_PASSWORD"]);
        var resolved = ComposeYaml.Resolve(_dir);
        resolved.Keys.Should().BeEquivalentTo(["xrmd-acme"]);
        resolved["xrmd-acme"].Environment["Storage__Provider"].Should().Be("PostgreSql");
        resolved["xrmd-acme"].Environment["ConnectionStrings__Org"].Should().Be("Host=pg.example.com;Username=xrmd;Password=s3cret;Database=acme");
        // Plan 2.7b, ruling Q8: pg_dump runs in the organization's container, into the backups volume; no SQL Server .bak directory.
        resolved["xrmd-acme"].Volumes.Should().Contain("backups:/var/lib/xrmd/backups");
        resolved["xrmd-acme"].Environment.Keys.Should().NotContain("Admin__BackupDirectory");
    }

    [Fact]
    public async Task The_default_database_stays_sql_server()
    {
        await InitAsync("--org", "acme");

        Env().Keys.Should().NotContain(["STORAGE_PROVIDER", "POSTGRES_PASSWORD"]);
        var resolved = ComposeYaml.Resolve(_dir);
        resolved.Keys.Should().Contain("sqlserver").And.NotContain("postgres");
        resolved["xrmd-acme"].Environment.Keys.Should().NotContain("Storage__Provider");
        Installation.Load(_dir).Provider.Should().Be(Xrmd.Abstractions.Storage.StorageProvider.SqlServer);
    }

    [Theory]
    [InlineData("--accept-sql-server-eula")]
    [InlineData("--sql-edition", "Express")]
    public async Task Database_postgresql_refuses_the_sql_server_options(params string[] option)
    {
        using var cli = new TestCli();
        (await cli.RunAsync(["server", "init", "--dir", _dir, "--database", "postgresql", .. option])).Should().Be(2, cli.Err.ToString());
        cli.Err.ToString().Should().Contain(option[0]);
        File.Exists(EnvPath).Should().BeFalse();
    }

    /// <summary>
    /// Plan 2.7b, Task 8: the local identity provider and the SQL endpoint run on PostgreSQL organizations, so
    /// <c>--database postgresql --auth local --tds-port</c> renders them as on SQL Server, beside the postgres service.
    /// </summary>
    [Fact]
    public async Task Database_postgresql_renders_the_local_identity_provider_and_the_sql_endpoint()
    {
        using var cli = await InitPostgresAsync("--auth", "local", "--org", "acme", "--domain", "xrm.test", "--tls", "internal", "--tds-port", "1433");

        var env = Env();
        env["STORAGE_PROVIDER"].Should().Be("PostgreSql");
        env["AUTH_MODE"].Should().Be("Local");
        env["ORG_ACME_TDS_PORT"].Should().Be("1433");
        File.Exists(Path.Combine(_dir, "secrets", "acme", "admin-password")).Should().BeTrue();

        var services = ComposeYaml.Resolve(_dir);
        services.Keys.Should().Contain(["postgres", "secrets-init", "tds-certs", "xrmd-acme"]).And.NotContain("sqlserver");
        var acme = services["xrmd-acme"];
        acme.Environment["Storage__Provider"].Should().Be("PostgreSql");
        acme.Environment["Auth__Mode"].Should().Be("Local");
        acme.Environment["Idp__InitialAdminPasswordFile"].Should().Be("/run/xrmd/secrets/admin-password");
        acme.Environment["Tds__Port"].Should().Be("5558");
        acme.Environment["Tds__CertificatePath"].Should().Be("/run/xrmd/tds/acme.xrm.test.crt");
        acme.Ports.Should().Equal("0.0.0.0:1433:5558");
        acme.Volumes.Should().Contain(["./secrets/acme:/run/xrmd/secrets", "./tds/acme:/run/xrmd/tds:ro"]);
        acme.DependsOn.Should().Contain(["postgres", "secrets-init"]).And.NotContain("sqlserver");
        services["tds-certs"].Environment["TDS_ORGS"].Should().Be("acme=acme.xrm.test");
        Installation.Load(_dir).IsLocalAuth.Should().BeTrue();
    }

    [Fact]
    public async Task A_sql_server_connection_string_names_the_database_option()
    {
        using var cli = new TestCli();
        (await cli.RunAsync(["server", "init", "--dir", _dir, "--sql-connection", "Host=pg.example.com;Username=xrmd;Password=p"])).Should().Be(2);
        cli.Err.ToString().Should().Contain("--database postgresql").And.NotContain("Keyword not supported");
    }

    /// <summary>Task 8 review, Minor 2: `--force` without `--database` keeps the engine of the existing .env, and its password.</summary>
    [Fact]
    public async Task Force_without_database_keeps_the_postgresql_engine()
    {
        using (await InitPostgresAsync("--org", "acme")) { }
        var password = Env()["POSTGRES_PASSWORD"];

        using var cli = new TestCli();
        (await cli.RunAsync(["server", "init", "--dir", _dir, "--force", "--org", "acme"])).Should().Be(0, cli.Err.ToString());

        Env()["STORAGE_PROVIDER"].Should().Be("PostgreSql");
        Env()["POSTGRES_PASSWORD"].Should().Be(password);
        ComposeYaml.Resolve(_dir).Keys.Should().Contain("postgres").And.NotContain("sqlserver");
    }

    /// <summary>
    /// Task 8 review, Minor 2: an explicit `--database` other than the engine of the existing .env is refused before anything is
    /// written: the other engine's data volume would be left behind with every organization in it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Force_with_another_database_is_refused(bool fromPostgres)
    {
        if (fromPostgres) using (await InitPostgresAsync("--org", "acme")) { }
        else await InitAsync("--org", "acme");
        var before = File.ReadAllText(EnvPath);

        using var cli = new TestCli();
        string[] args = fromPostgres
            ? ["server", "init", "--dir", _dir, "--force", "--org", "acme", "--database", "sqlserver", "--accept-sql-server-eula"]
            : ["server", "init", "--dir", _dir, "--force", "--org", "acme", "--database", "postgresql"];
        (await cli.RunAsync(args)).Should().Be(ExitCodes.Rejected, cli.Err.ToString());

        cli.Err.ToString().Should().Contain("STORAGE_PROVIDER").And.Contain(fromPostgres ? "PostgreSQL" : "SQL Server");
        File.ReadAllText(EnvPath).Should().Be(before);
    }

    [Theory]
    [InlineData("STORAGE_PROVIDER=PostgreSql", "STORAGE_PROVIDER=Oracle", "STORAGE_PROVIDER")]
    [InlineData("POSTGRES_PASSWORD=", "POSTGRES_PASSWORD_OLD=", "POSTGRES_PASSWORD")]
    [InlineData("XRMD_ORGS=", "ACCEPT_EULA=Y\nXRMD_ORGS=", "ACCEPT_EULA")]
    public async Task Inconsistent_postgresql_env_is_rejected_naming_the_key(string find, string replace, string key)
    {
        using (await InitPostgresAsync("--org", "acme")) { }
        EditEnv(text => text.Replace(find, replace, StringComparison.Ordinal));

        var ex = FluentActions.Invoking(() => Installation.Load(_dir)).Should().Throw<CliException>().Which;
        ex.ExitCode.Should().Be(ExitCodes.Rejected);
        ex.Message.Should().Contain(key);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> DockerComposeAsync(string dir, params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var arg in ServerCommandsArgs(dir, args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static List<string> ServerCommandsArgs(string dir, string[] args) => Xrmd.Cli.Commands.ServerCommands.ComposeArguments(dir, args);

    private Dictionary<string, string> Env() => ComposeYaml.ReadEnv(_dir);
}

/// <summary>A parsed xrmd compose service, with Compose interpolation applied from the directory's <c>.env</c>.</summary>
internal sealed record ComposeService(Dictionary<string, string> Environment, List<string> Ports, List<string> Volumes, List<string> DependsOn, string Image = "");

/// <summary>Reads a generated <c>docker-compose.yml</c> as YAML and resolves <c>${VAR}</c>, <c>${VAR:-x}</c>, <c>${VAR:?x}</c> from <c>.env</c>.</summary>
internal static class ComposeYaml
{
    public static Dictionary<string, string> ReadEnv(string dir) =>
        File.ReadAllLines(Path.Combine(dir, ".env"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1].Length >= 2 && p[1][0] == '\'' && p[1][^1] == '\'' ? p[1][1..^1] : p[1]);

    /// <summary>The generated compose file as parsed YAML, without interpolation.</summary>
    public static Dictionary<string, object> Raw(string dir) =>
        new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dir, "docker-compose.yml")));

    public static Dictionary<string, ComposeService> Resolve(string dir)
    {
        var env = ReadEnv(dir);
        var root = new DeserializerBuilder().Build()
            .Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dir, "docker-compose.yml")));
        var services = (Dictionary<object, object>)root["services"];
        var result = new Dictionary<string, ComposeService>();
        foreach (var (name, value) in services)
        {
            var service = (Dictionary<object, object>)value;
            List<string> Strings(string key) => service.TryGetValue(key, out var v) && v is List<object> list
                ? list.Select(x => Interpolate((string)x, env)).ToList()
                : [];
            var environment = service.TryGetValue("environment", out var e) && e is Dictionary<object, object> map
                ? map.ToDictionary(kv => (string)kv.Key, kv => Interpolate((string)kv.Value, env))
                : [];
            var dependsOn = service.TryGetValue("depends_on", out var d)
                ? d switch
                {
                    Dictionary<object, object> deps => deps.Keys.Cast<string>().ToList(),
                    List<object> list => list.Cast<string>().ToList(),
                    _ => [],
                }
                : [];
            result[(string)name] = new ComposeService(environment, Strings("ports"), Strings("volumes"), dependsOn,
                service.TryGetValue("image", out var img) && img is string imageText ? Interpolate(imageText, env) : "");
        }
        return result;
    }

    /// <summary>Innermost first, so that a default holding another variable (<c>${A:-x${B}y}</c>) resolves like Compose.</summary>
    private static string Interpolate(string text, Dictionary<string, string> env)
    {
        var pattern = new Regex(@"\$\{([A-Za-z0-9_]+)(?:(:-|:\?)([^${}]*))?\}");
        while (pattern.IsMatch(text))
            text = pattern.Replace(text, m =>
            {
                var value = env.TryGetValue(m.Groups[1].Value, out var v) ? v : "";
                if (value.Length > 0) return value;
                if (m.Groups[2].Value == ":?") throw new InvalidOperationException($"{m.Groups[1].Value} is required: {m.Groups[3].Value}");
                return m.Groups[3].Value;
            });
        return text.Replace("$$", "$");
    }
}
