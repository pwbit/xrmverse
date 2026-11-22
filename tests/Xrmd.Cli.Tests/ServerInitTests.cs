using System.Text.RegularExpressions;
using FluentAssertions;
using Xrmd.Cli.Server;

namespace Xrmd.Cli.Tests;

/// <summary><c>xrmd server init --auth local</c> (plan 2.8, Task 5): Local mode, the generated administrator and the one-shot password file.</summary>
public sealed partial class ServerInitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xrmd-server-init-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static Dictionary<string, string> ReadEnv(string dir) =>
        File.ReadAllLines(Path.Combine(dir, ".env")).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    private Task<int> InitAsync(TestCli cli, params string[] extra) =>
        cli.RunAsync(["server", "init", "--dir", _dir, .. TestCli.Eula(extra), "--auth", "local"]);

    [GeneratedRegex(@"Initial admin password for (\w+) \(shown once\): (\S+)")]
    private static partial Regex PasswordLine();

    [Fact]
    public async Task Init_local_writes_the_mode_the_admin_and_a_one_shot_password_file()
    {
        using var cli = new TestCli();

        (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal")).Should().Be(0, cli.Err.ToString());

        var env = ReadEnv(_dir);
        env["AUTH_MODE"].Should().Be("Local");
        env["AUTH_TENANT_ID"].Should().BeEmpty();
        env["AUTH_DEV_SIGNING_KEY"].Should().BeEmpty();
        Guid.TryParse(env["ORG_ACME_ADMIN_OBJECT_ID"], out var oid).Should().BeTrue();
        oid.Should().NotBe(Guid.Empty);

        var shown = PasswordLine().Match(cli.Out.ToString());
        shown.Success.Should().BeTrue(cli.Out.ToString());
        shown.Groups[1].Value.Should().Be("acme");
        var password = shown.Groups[2].Value;
        password.Should().HaveLength(20).And.MatchRegex("^[A-Za-z0-9]+$");

        var file = Path.Combine(_dir, "secrets", "acme", "admin-password");
        File.ReadAllText(file).TrimEnd('\n').Should().Be(password);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().NotContain(password, "the password never reaches .env");
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().NotContain(password);
    }

    [Fact]
    public async Task Init_local_compose_mounts_the_secrets_and_names_the_file_per_org()
    {
        using var cli = new TestCli();

        (await InitAsync(cli, "--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal")).Should().Be(0, cli.Err.ToString());

        var compose = File.ReadAllText(Path.Combine(_dir, "docker-compose.yml"));
        compose.Should().Contain("Auth__Mode: ${AUTH_MODE:-Development}");
        Regex.Matches(compose, Regex.Escape("Idp__InitialAdminPasswordFile: /run/xrmd/secrets/admin-password")).Should().HaveCount(2);
        compose.Should().Contain("- ./secrets/acme:/run/xrmd/secrets").And.Contain("- ./secrets/zenith:/run/xrmd/secrets");
        compose.Should().Contain("- ./secrets:/run/xrmd/secrets", "only the secrets-init service sees every organization's folder");
        Regex.Matches(compose, Regex.Escape("- ./secrets:/run/xrmd/secrets")).Should().HaveCount(1);
        compose.Should().Contain("secrets-init:");
        File.Exists(Path.Combine(_dir, "secrets", "zenith", "admin-password")).Should().BeTrue();
        PasswordLine().Matches(cli.Out.ToString()).Select(m => m.Groups[2].Value).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task Other_auth_modes_have_no_secrets_in_their_compose()
    {
        using var cli = new TestCli();

        (await cli.RunAsync(TestCli.Eula(["server", "init", "--dir", _dir]))).Should().Be(0, cli.Err.ToString());

        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().NotContain("secrets").And.NotContain("Idp__");
        Directory.Exists(Path.Combine(_dir, "secrets")).Should().BeFalse();
    }

    [Fact]
    public async Task Init_local_needs_an_https_url_and_writes_nothing_without_one()
    {
        using var cli = new TestCli();

        (await InitAsync(cli, "--org", "acme")).Should().Be(2);

        cli.Err.ToString().Should().Contain("https").And.Contain("--domain");
        File.Exists(Path.Combine(_dir, ".env")).Should().BeFalse();
        Directory.Exists(Path.Combine(_dir, "secrets")).Should().BeFalse();
    }

    [Fact]
    public async Task Init_local_refuses_a_tenant_id()
    {
        using var cli = new TestCli();

        (await InitAsync(cli, "--domain", "xrm.test", "--tls", "internal", "--tenant-id", Guid.NewGuid().ToString())).Should().Be(2);
        cli.Err.ToString().Should().Contain("--tenant-id");
    }

    [Fact]
    public async Task Org_add_in_local_mode_generates_the_admin_and_a_new_password_file()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal")).Should().Be(0, cli.Err.ToString());
        FakeSdk.Install(_dir);

        await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir);

        // docker is not available to the test: the organization is saved before its service starts
        Installation.Load(_dir).Orgs.Should().Contain(o => o.Name == "zenith");
        File.Exists(Path.Combine(_dir, "secrets", "zenith", "admin-password")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().Contain("/run/xrmd/secrets/admin-password");
    }

    [Fact]
    public async Task Init_local_with_an_unwritable_secrets_folder_fails_and_changes_nothing()
    {
        using var cli = new TestCli();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "secrets"), "in the way"); // a folder cannot be created (also works as root)

        var code = await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal");

        code.Should().NotBe(0);
        File.Exists(Path.Combine(_dir, ".env")).Should().BeFalse();
        File.Exists(Path.Combine(_dir, "docker-compose.yml")).Should().BeFalse();
        cli.Out.ToString().Should().NotContain("Initial admin password");
    }

    [Fact]
    public async Task Org_add_with_an_unwritable_secrets_folder_fails_before_saving()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal")).Should().Be(0, cli.Err.ToString());
        FakeSdk.Install(_dir);
        var envBefore = File.ReadAllText(Path.Combine(_dir, ".env"));
        var composeBefore = File.ReadAllText(Path.Combine(_dir, "docker-compose.yml"));
        Directory.Delete(Path.Combine(_dir, "secrets"), recursive: true);
        File.WriteAllText(Path.Combine(_dir, "secrets"), "in the way");

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir)).Should().NotBe(0);

        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(envBefore);
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().Be(composeBefore);
        cli.Out.ToString().Should().NotContain("Initial admin password");
    }

    [Fact]
    public async Task The_secrets_folders_are_private_and_secrets_init_hands_each_org_folder_to_group_1654_only()
    {
        using var cli = new TestCli();

        (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal")).Should().Be(0, cli.Err.ToString());

        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(Path.Combine(_dir, "secrets")).Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, "no one else may plant a file");
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(Path.Combine(_dir, "secrets", "acme")).Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var compose = File.ReadAllText(Path.Combine(_dir, "docker-compose.yml"));
        compose.Should().Contain("find -P /run/xrmd/secrets -mindepth 1 -maxdepth 1 -type d -user")
            .And.Contain("chgrp 1654").And.Contain("chmod 2770")
            .And.Contain("find -P /run/xrmd/secrets -mindepth 2 -maxdepth 2 -type f -links 1 -user")
            .And.NotContain("1777");
    }

    [Fact]
    public async Task A_failed_init_removes_the_secrets_folder_it_created()
    {
        using var cli = new TestCli();
        Directory.CreateDirectory(_dir);
        // the compose file is foreign and --force is absent: the save fails after the password was written
        File.WriteAllText(Path.Combine(_dir, "docker-compose.yml"), "services: {}\n");

        (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal")).Should().NotBe(0);

        Directory.Exists(Path.Combine(_dir, "secrets")).Should().BeFalse();
    }

    [Fact]
    public async Task Init_local_over_a_development_env_gives_every_org_a_new_admin_and_password()
    {
        using var cli = new TestCli();
        (await cli.RunAsync(TestCli.Eula(["server", "init", "--dir", _dir, "--org", "acme", "--domain", "xrm.test", "--tls", "internal"])))
            .Should().Be(0, cli.Err.ToString());
        var oldOid = ReadEnv(_dir)["ORG_ACME_ADMIN_OBJECT_ID"];

        (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal", "--force")).Should().Be(0, cli.Err.ToString());

        ReadEnv(_dir)["AUTH_MODE"].Should().Be("Local");
        ReadEnv(_dir)["ORG_ACME_ADMIN_OBJECT_ID"].Should().NotBe(oldOid);
        PasswordLine().Matches(cli.Out.ToString()).Select(m => m.Groups[1].Value).Should().Equal("acme");
        File.Exists(Path.Combine(_dir, "secrets", "acme", "admin-password")).Should().BeTrue();
    }

    [Fact]
    public async Task Reinit_with_a_different_admin_object_id_is_a_new_admin_with_a_password()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal")).Should().Be(0, cli.Err.ToString());
        File.Delete(Path.Combine(_dir, "secrets", "acme", "admin-password"));
        var oldOid = ReadEnv(_dir)["ORG_ACME_ADMIN_OBJECT_ID"];

        // the same object id: nothing new
        (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal", "--force", "--admin-object-id", oldOid)).Should().Be(0, cli.Err.ToString());
        PasswordLine().IsMatch(cli.Out.ToString()).Should().BeFalse();
        cli.Out.ToString().Should().Contain("acme keeps its administrator");

        var other = Guid.NewGuid().ToString();
        (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal", "--force", "--admin-object-id", other)).Should().Be(0, cli.Err.ToString());

        ReadEnv(_dir)["ORG_ACME_ADMIN_OBJECT_ID"].Should().Be(other);
        PasswordLine().Matches(cli.Out.ToString()).Select(m => m.Groups[1].Value).Should().Equal("acme");
        cli.Out.ToString().Should().NotContain("keeps its administrator");
        File.Exists(Path.Combine(_dir, "secrets", "acme", "admin-password")).Should().BeTrue();
    }

    [Fact]
    public async Task Reinit_keeps_the_administrator_and_prints_no_password_for_existing_orgs()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "internal")).Should().Be(0, cli.Err.ToString());
        var oid = ReadEnv(_dir)["ORG_ACME_ADMIN_OBJECT_ID"];
        File.Delete(Path.Combine(_dir, "secrets", "acme", "admin-password")); // the server used and deleted it

        (await InitAsync(cli, "--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal", "--force")).Should().Be(0, cli.Err.ToString());

        ReadEnv(_dir)["ORG_ACME_ADMIN_OBJECT_ID"].Should().Be(oid, "a new object id would seed a second administrator");
        var shown = PasswordLine().Matches(cli.Out.ToString()).Select(m => m.Groups[1].Value).ToList();
        shown.Should().Equal("zenith");
        File.Exists(Path.Combine(_dir, "secrets", "acme", "admin-password")).Should().BeFalse();
        File.Exists(Path.Combine(_dir, "secrets", "zenith", "admin-password")).Should().BeTrue();
        cli.Out.ToString().Should().Contain("acme keeps its administrator").And.Contain("xrmd idp user set-password");
    }

    [Fact]
    public async Task Org_remove_deletes_that_orgs_secrets_folder_only()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, "--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal")).Should().Be(0, cli.Err.ToString());
        FakeSdk.Install(_dir);
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "org", "remove", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        Installation.Load(_dir).Orgs.Should().NotContain(o => o.Name == "zenith");
        Directory.Exists(Path.Combine(_dir, "secrets", "zenith")).Should().BeFalse();
        File.Exists(Path.Combine(_dir, "secrets", "acme", "admin-password")).Should().BeTrue();
    }

    // ---- Plan 3.3, Task 5: the SQL (TDS) endpoint of each organization ----

    private static readonly string[] Internal = ["--domain", "xrm.test", "--tls", "internal"];

    private const string LocalOnly = "The SQL endpoint needs the local identity provider (--auth local)";

    private static readonly UnixFileMode Mode0750 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    /// <summary>A certificate for <paramref name="hosts"/> valid until <paramref name="notAfter"/>, and its private key, as PEM files in <paramref name="dir"/>.</summary>
    internal static (string Cert, string Key) WriteTdsPair(string dir, DateTimeOffset notAfter, params string[] hosts)
    {
        Directory.CreateDirectory(dir);
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={hosts[0]}", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        foreach (var host in hosts) san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(notAfter.AddDays(-90), notAfter);
        var cert = Path.Combine(dir, hosts[0] + ".crt");
        var keyPath = Path.Combine(dir, hosts[0] + ".key");
        File.WriteAllText(cert, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return (cert, keyPath);
    }

    [Fact]
    public async Task Init_with_tds_port_writes_env_and_compose()
    {
        using var cli = new TestCli();

        (await InitAsync(cli, ["--org", "acme", "--org", "zenith", .. Internal, "--tds-port", "5558"])).Should().Be(0, cli.Err.ToString());

        var env = ReadEnv(_dir);
        env["ORG_ACME_TDS_PORT"].Should().Be("5558");
        env["ORG_ZENITH_TDS_PORT"].Should().Be("5559", "the next organizations take the next ports");
        var services = ComposeYaml.Resolve(_dir);
        var acme = services["xrmd-acme"];
        acme.Ports.Should().Equal("0.0.0.0:5558:5558");
        acme.Environment["Tds__Port"].Should().Be("5558");
        acme.Environment["Tds__CertificatePath"].Should().Be("/run/xrmd/tds/acme.xrm.test.crt");
        acme.Environment["Tds__PrivateKeyPath"].Should().Be("/run/xrmd/tds/acme.xrm.test.key");
        acme.Volumes.Should().Contain("./tds/acme:/run/xrmd/tds:ro").And.NotContain(v => v.StartsWith("./tds/zenith", StringComparison.Ordinal));
        services["xrmd-zenith"].Ports.Should().Equal("0.0.0.0:5559:5558");
        services["xrmd-zenith"].Volumes.Should().Contain("./tds/zenith:/run/xrmd/tds:ro");

        var certs = services["tds-certs"];
        certs.Image.Should().Be(env["XRMD_IMAGE"]);
        certs.Volumes.Should().Equal("caddy-data:/data:ro", "./tds:/out");
        certs.Environment["TDS_ORGS"].Should().Be("acme=acme.xrm.test zenith=zenith.xrm.test");
        certs.Environment["TDS_PAIR"].Should().BeEmpty("Caddy's storage is the source");
        var raw = (Dictionary<object, object>)((Dictionary<object, object>)ComposeYaml.Raw(_dir)["services"])["tds-certs"];
        raw["user"].Should().Be("0:0");

        foreach (var org in new[] { "acme", "zenith" })
        {
            Directory.Exists(Path.Combine(_dir, "tds", org)).Should().BeTrue();
            if (!OperatingSystem.IsWindows()) File.GetUnixFileMode(Path.Combine(_dir, "tds", org)).Should().Be(Mode0750);
        }
        cli.Out.ToString().Should().Contain("5558").And.Contain("acme.xrm.test,5558");
    }

    [Fact]
    public async Task Init_with_tds_port_binds_where_xrmd_tds_bind_says()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", .. Internal, "--tds-port", "5558"])).Should().Be(0, cli.Err.ToString());
        File.AppendAllText(Path.Combine(_dir, ".env"), "XRMD_TDS_BIND=10.1.2.3\n");

        ComposeYaml.Resolve(_dir)["xrmd-acme"].Ports.Should().Equal("10.1.2.3:5558:5558");
    }

    [Fact]
    public async Task Init_local_without_tds_port_has_no_sql_endpoint()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", .. Internal])).Should().Be(0, cli.Err.ToString());

        var compose = File.ReadAllText(Path.Combine(_dir, "docker-compose.yml"));
        compose.Should().NotContain("tds-certs").And.NotContain("Tds__").And.NotContain("TDS");
        ReadEnv(_dir).Keys.Should().NotContain(k => k.Contains("TDS", StringComparison.Ordinal));
        Directory.Exists(Path.Combine(_dir, "tds")).Should().BeFalse();
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("entra")]
    public async Task Init_tds_port_needs_the_local_identity_provider(string auth)
    {
        using var cli = new TestCli();
        string[] extra = auth == "entra" ? ["--tenant-id", Guid.NewGuid().ToString(), "--admin-object-id", Guid.NewGuid().ToString()] : [];

        (await cli.RunAsync(TestCli.Eula(["server", "init", "--dir", _dir, "--auth", auth, "--org", "acme", .. Internal, "--tds-port", "5558", .. extra])))
            .Should().Be(2);

        cli.Err.ToString().Should().Contain(LocalOnly);
        File.Exists(Path.Combine(_dir, ".env")).Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    public async Task Init_tds_port_must_be_a_port(string port)
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", .. Internal, "--tds-port", port])).Should().Be(2);
        cli.Err.ToString().Should().Contain("--tds-port");
    }

    [Fact]
    public async Task Init_tds_port_must_not_be_an_http_port()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", .. Internal, "--port", "5558", "--tds-port", "5558"])).Should().Be(2);
        cli.Err.ToString().Should().Contain("TDS port 5558 is already used by acme");
    }

    [Fact]
    public async Task No_proxy_requires_tds_cert_and_key()
    {
        var (cert, key) = WriteTdsPair(Path.Combine(_dir + "-pair"), DateTimeOffset.UtcNow.AddDays(90), "acme.xrm.test");
        string[] noProxy = ["--org", "acme", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "10.0.0.5", "--tds-port", "5558"];
        try
        {
            using (var cli = new TestCli())
            {
                (await InitAsync(cli, noProxy)).Should().Be(2);
                cli.Err.ToString().Should().Contain("--tds-cert");
                File.Exists(Path.Combine(_dir, ".env")).Should().BeFalse();
            }
            using (var cli = new TestCli())
            {
                (await InitAsync(cli, [.. noProxy, "--tds-cert", cert])).Should().Be(2);
                cli.Err.ToString().Should().Contain("--tds-key");
            }
            using (var cli = new TestCli())
            {
                // the generated proxy has its own certificates
                (await InitAsync(cli, ["--org", "acme", .. Internal, "--tds-port", "5558", "--tds-cert", cert, "--tds-key", key])).Should().Be(2);
                cli.Err.ToString().Should().Contain("--tds-cert").And.Contain("--no-proxy");
            }
            using (var cli = new TestCli())
            {
                (await InitAsync(cli, [.. noProxy, "--tds-cert", cert, "--tds-key", key])).Should().Be(0, cli.Err.ToString());

                File.ReadAllText(Path.Combine(_dir, "certs", "tds.crt")).Should().Be(File.ReadAllText(cert));
                File.ReadAllText(Path.Combine(_dir, "certs", "tds.key")).Should().Be(File.ReadAllText(key));
                if (!OperatingSystem.IsWindows())
                    File.GetUnixFileMode(Path.Combine(_dir, "certs", "tds.key")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var services = ComposeYaml.Resolve(_dir);
                services["tds-certs"].Volumes.Should().Equal("./certs:/certs:ro", "./tds:/out");
                services["tds-certs"].Environment["TDS_PAIR"].Should().Be("tds");
                services["xrmd-acme"].Ports.Should().Contain("0.0.0.0:5558:5558");
                services["xrmd-acme"].Environment["Tds__CertificatePath"].Should().Be("/run/xrmd/tds/acme.xrm.test.crt");
            }
        }
        finally
        {
            Directory.Delete(_dir + "-pair", recursive: true);
        }
    }

    [Fact]
    public async Task Tls_files_mode_uses_given_files()
    {
        var (cert, key) = WriteTdsPair(Path.Combine(_dir + "-pair"), DateTimeOffset.UtcNow.AddDays(90), "*.xrm.test");
        try
        {
            using var cli = new TestCli();
            (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--tls", "files", "--tls-cert", cert, "--tls-key", key, "--tds-port", "5558"))
                .Should().Be(0, cli.Err.ToString());

            // The given pair (certs/server.crt and server.key, 0600 for the host user) is what the endpoint serves: tds-certs
            // hands a copy to uid 1654, since the server cannot read the host user's 0600 key.
            var services = ComposeYaml.Resolve(_dir);
            services["tds-certs"].Volumes.Should().Equal("./certs:/certs:ro", "./tds:/out");
            services["tds-certs"].Environment["TDS_PAIR"].Should().Be("server");
            services["xrmd-acme"].Volumes.Should().Contain("./tds/acme:/run/xrmd/tds:ro");
            services["xrmd-acme"].Environment["Tds__CertificatePath"].Should().Be("/run/xrmd/tds/acme.xrm.test.crt");
            services["xrmd-acme"].Environment["Tds__PrivateKeyPath"].Should().Be("/run/xrmd/tds/acme.xrm.test.key");
        }
        finally
        {
            Directory.Delete(_dir + "-pair", recursive: true);
        }
    }

    [Fact]
    public async Task Status_shows_tds_port()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", "--org", "zenith", .. Internal, "--tds-port", "5558"])).Should().Be(0, cli.Err.ToString());
        var notAfter = new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero);
        WriteTdsPair(Path.Combine(_dir, "tds", "acme"), notAfter, "acme.xrm.test");

        (await cli.RunAsync("server", "status", "--dir", _dir, "-o", "json")).Should().Be(1, "no organization has a profile yet");

        using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
        var rows = json.RootElement.EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("tdsPort").GetInt32()).Should().Equal(5558, 5559);
        rows[0].GetProperty("tdsCertificateExpires").GetString().Should().Be("2027-03-01");
        rows[1].GetProperty("tdsCertificateExpires").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null, "tds-certs has not copied it yet");

        (await cli.RunAsync("server", "status", "--dir", _dir)).Should().Be(1);
        var lines = cli.Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        lines.Should().Contain(l => l.StartsWith("org", StringComparison.Ordinal) && l.Contains("tdsPort") && l.Contains("tdsCertificate"));
        lines.Should().Contain(l => l.StartsWith("acme", StringComparison.Ordinal) && l.Contains("5558") && l.Contains("2027-03-01"));
    }

    [Fact]
    public async Task Status_without_tds_has_no_tds_columns()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", .. Internal])).Should().Be(0, cli.Err.ToString());

        await cli.RunAsync("server", "status", "--dir", _dir, "-o", "json");

        cli.Out.ToString().Should().NotContain("tds");
    }

    [Fact]
    public async Task Doctor_checks_the_tds_certificate()
    {
        using var cli = new TestCli();
        cli.ResolveHost = (_, _) => Task.FromException<System.Net.IPAddress[]>(
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
        (await InitAsync(cli, ["--org", "acme", "--org", "zenith", "--org", "orbit", .. Internal, "--tds-port", "5558"])).Should().Be(0, cli.Err.ToString());
        var now = cli.Clock.GetUtcNow();
        WriteTdsPair(Path.Combine(_dir, "tds", "acme"), now.AddDays(60), "acme.xrm.test");
        WriteTdsPair(Path.Combine(_dir, "tds", "zenith"), now.AddDays(-1), "zenith.xrm.test");

        await cli.RunAsync("server", "doctor", "--dir", _dir, "-o", "json");

        using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
        var rows = json.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("org").GetString()!);
        System.Text.Json.JsonElement Check(string org) => rows[org].GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "tds-certificate");
        rows["acme"].GetProperty("tdsPort").GetInt32().Should().Be(5558);
        Check("acme").GetProperty("status").GetString().Should().Be("ok");
        Check("acme").GetProperty("detail").GetString().Should().Contain(now.AddDays(60).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).And.Contain("5558");
        Check("zenith").GetProperty("status").GetString().Should().Be("error");
        Check("zenith").GetProperty("detail").GetString().Should().Contain("expired");
        Check("orbit").GetProperty("status").GetString().Should().Be("error");
        Check("orbit").GetProperty("detail").GetString().Should().Contain("tds-certs");
    }

    [Fact]
    public async Task Org_remove_deletes_that_orgs_tds_folder_and_refreshes_tds_certs()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", "--org", "zenith", .. Internal, "--tds-port", "5558"])).Should().Be(0, cli.Err.ToString());
        FakeSdk.Install(_dir);
        WriteTdsPair(Path.Combine(_dir, "tds", "zenith"), DateTimeOffset.UtcNow.AddDays(30), "zenith.xrm.test");
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "remove", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        Directory.Exists(Path.Combine(_dir, "tds", "zenith")).Should().BeFalse();
        Directory.Exists(Path.Combine(_dir, "tds", "acme")).Should().BeTrue();
        ReadEnv(_dir).Keys.Should().NotContain("ORG_ZENITH_TDS_PORT");
        ComposeYaml.Resolve(_dir)["tds-certs"].Environment["TDS_ORGS"].Should().Be("acme=acme.xrm.test");
        runner.Calls.Select(c => string.Join(' ', c.Arguments.Skip(5))).Should().Contain("up -d --no-deps tds-certs");
    }

    [Fact]
    public async Task Org_remove_of_the_last_tds_org_removes_tds_certs()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", .. Internal, "--tds-port", "5558"])).Should().Be(0, cli.Err.ToString());
        FakeSdk.Install(_dir);
        cli.Process = new FakeProcessRunner();
        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "org", "remove", "acme", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        runner.Calls.Select(c => string.Join(' ', c.Arguments.Skip(5))).Should().Contain("rm -s -f tds-certs");
        ComposeYaml.Resolve(_dir).Keys.Should().NotContain("tds-certs");
        Directory.Exists(Path.Combine(_dir, "tds", "acme")).Should().BeFalse();
    }

    [Fact]
    public async Task A_tds_port_without_local_mode_is_inconsistent()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", .. Internal, "--tds-port", "5558"])).Should().Be(0, cli.Err.ToString());
        var env = Path.Combine(_dir, ".env");
        File.WriteAllText(env, File.ReadAllText(env).Replace("AUTH_MODE=Local", "AUTH_MODE=Entra", StringComparison.Ordinal));

        var act = () => Installation.Load(_dir);

        act.Should().Throw<CliException>().Where(e => e.ExitCode == ExitCodes.Rejected && e.Message.Contains("ORG_ACME_TDS_PORT") && e.Message.Contains("AUTH_MODE=Local"));
    }

    // ---- Plan 3.3, Task 5, fix round 1 ----

    [Fact]
    public async Task Tds_certs_interval_follows_the_pair_source()
    {
        var (cert, key) = WriteTdsPair(_dir + "-pair", DateTimeOffset.UtcNow.AddDays(90), "*.xrm.test");
        try
        {
            async Task<string> IntervalAsync(params string[] extra)
            {
                using var cli = new TestCli();
                (await InitAsync(cli, ["--org", "acme", "--domain", "xrm.test", "--tds-port", "5558", "--force", .. extra])).Should().Be(0, cli.Err.ToString());
                return ComposeYaml.Resolve(_dir)["tds-certs"].Environment["TDS_INTERVAL"];
            }

            (await IntervalAsync("--tls", "acme", "--acme-email", "ops@example.com")).Should().Be("43200", "Caddy renews ACME certificates long before they expire");
            (await IntervalAsync("--tls", "internal")).Should().Be("3600", "the internal CA's leaves live 12 hours");
            (await IntervalAsync("--tls", "files", "--tls-cert", cert, "--tls-key", key)).Should().Be("300", "a pair in certs/ is replaced by hand");
            (await IntervalAsync("--no-proxy", "--trusted-proxy", "10.0.0.5", "--tds-cert", cert, "--tds-key", key)).Should().Be("300");
        }
        finally
        {
            Directory.Delete(_dir + "-pair", recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_tds_hint_behind_your_own_proxy_names_the_tds_pair()
    {
        var (cert, key) = WriteTdsPair(_dir + "-pair", DateTimeOffset.UtcNow.AddDays(90), "*.xrm.test");
        try
        {
            using var cli = new TestCli();
            cli.ResolveHost = (_, _) => Task.FromException<System.Net.IPAddress[]>(
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
            (await InitAsync(cli, "--org", "acme", "--domain", "xrm.test", "--no-proxy", "--trusted-proxy", "10.0.0.5", "--tds-port", "5558",
                "--tds-cert", cert, "--tds-key", key)).Should().Be(0, cli.Err.ToString());
            WriteTdsPair(Path.Combine(_dir, "tds", "acme"), cli.Clock.GetUtcNow().AddDays(5), "acme.xrm.test");

            await cli.RunAsync("server", "doctor", "--dir", _dir, "-o", "json");

            using var json = System.Text.Json.JsonDocument.Parse(cli.Out.ToString());
            var check = json.RootElement[0].GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "tds-certificate");
            check.GetProperty("status").GetString().Should().Be("warning");
            check.GetProperty("detail").GetString().Should().Contain("Replace certs/tds.crt and certs/tds.key; tds-certs copies it within 5 minutes.")
                .And.NotContain("Renew it on your proxy");
        }
        finally
        {
            Directory.Delete(_dir + "-pair", recursive: true);
        }
    }

    [Theory]
    [InlineData("80")]
    [InlineData("443")]
    public async Task Init_refuses_the_proxy_ports_as_tds_ports(string port)
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", .. Internal, "--tds-port", port])).Should().Be(2);
        cli.Err.ToString().Should().Contain($"TDS port {port} is the proxy's port");
        File.Exists(Path.Combine(_dir, ".env")).Should().BeFalse();
    }

    [Fact]
    public async Task Init_names_the_org_whose_tds_port_would_overflow()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", "--org", "zenith", "--org", "orbit", .. Internal, "--tds-port", "65534"])).Should().Be(2);
        cli.Err.ToString().Should().Contain("the TDS port of 'orbit' would be 65536, above 65535");
        File.Exists(Path.Combine(_dir, ".env")).Should().BeFalse();
    }

    [Fact]
    public async Task Org_add_automatic_port_skips_a_tds_port()
    {
        using var cli = new TestCli();
        (await InitAsync(cli, ["--org", "acme", .. Internal, "--port", "5557", "--tds-port", "5558"])).Should().Be(0, cli.Err.ToString());
        FakeSdk.Install(_dir);
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        ReadEnv(_dir)["ORG_ZENITH_PORT"].Should().Be("5559", "5558 is acme's TDS port");
    }
}
