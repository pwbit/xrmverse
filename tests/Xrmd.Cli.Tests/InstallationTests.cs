using FluentAssertions;
using Xrmd.Cli.Server;

namespace Xrmd.Cli.Tests;

/// <summary>Public hosts in the installation model (rulings H1, H2, H6, I8).</summary>
public class InstallationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xrmd-installation-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string EnvPath => Path.Combine(_dir, ".env");

    private async Task InitAsync(params string[] extra)
    {
        using var cli = new TestCli();
        (await cli.RunAsync(["server", "init", "--dir", _dir, .. TestCli.Eula(extra)])).Should().Be(0, cli.Err.ToString());
    }

    private void EditEnv(Func<string, string> edit) => File.WriteAllText(EnvPath, edit(File.ReadAllText(EnvPath)));

    [Fact]
    public async Task Domain_gives_each_org_its_host()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal");

        var installation = Installation.Load(_dir);
        installation.Domain.Should().Be("xrm.test");
        installation.Orgs.Select(o => o.Host).Should().Equal("acme.xrm.test", "zenith.xrm.test");
        installation.Orgs.Select(o => o.PublicUrl).Should().Equal("https://acme.xrm.test/", "https://zenith.xrm.test/");
        installation.Orgs.Select(o => o.ClientUrl).Should().Equal("https://acme.xrm.test/", "https://zenith.xrm.test/");
        installation.Orgs.Select(o => o.Url).Should().Equal("http://localhost:5001/", "http://localhost:5002/");
        installation.Proxy.Should().Be(ProxyMode.Caddy, "caddy is the default proxy mode when an organization has a host (H2)");
        installation.ProxyAddress.Should().Be("172.31.250.10");

        var env = ComposeYaml.ReadEnv(_dir);
        env["XRMD_DOMAIN"].Should().Be("xrm.test");
        env["PROXY_MODE"].Should().Be("caddy");
        env["TLS_MODE"].Should().Be("internal");
        env.Keys.Should().NotContain(["ORG_ACME_HOST", "ORG_ZENITH_HOST"], "a host derived from XRMD_DOMAIN is not repeated per organization");
    }

    [Fact]
    public async Task Public_host_overrides_the_domain()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--public-host", "zenith=crm.example.com", "--tls", "internal");

        var installation = Installation.Load(_dir);
        installation.Orgs.Select(o => o.Host).Should().Equal("acme.xrm.test", "crm.example.com");
        var env = ComposeYaml.ReadEnv(_dir);
        env["ORG_ZENITH_HOST"].Should().Be("crm.example.com");

        // By hand, too: ORG_<ORG>_HOST wins over XRMD_DOMAIN (H1), and survives a save.
        EditEnv(text => text.Replace("ORG_ACME_AUDIENCE=\n", "ORG_ACME_AUDIENCE=\nORG_ACME_HOST=sales.example.com\n"));
        installation = Installation.Load(_dir);
        installation.Orgs.Select(o => o.Host).Should().Equal("sales.example.com", "crm.example.com");
        installation.Save();
        ComposeYaml.ReadEnv(_dir)["ORG_ACME_HOST"].Should().Be("sales.example.com");
    }

    [Fact]
    public async Task Public_host_without_a_domain_applies_to_that_organization_only()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--public-host", "acme=acme.example.com", "--tls", "internal");

        var installation = Installation.Load(_dir);
        installation.Domain.Should().BeNull();
        installation.Orgs.Select(o => o.Host).Should().Equal("acme.example.com", null);
        installation.Orgs[1].ClientUrl.Should().Be("http://localhost:5002/");
    }

    [Fact]
    public async Task Org_audience_defaults_to_the_https_host()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal");

        Installation.Load(_dir).Orgs.Select(o => o.EffectiveAudience).Should().Equal("https://acme.xrm.test/", "https://zenith.xrm.test/");
        var services = ComposeYaml.Resolve(_dir);
        services["xrmd-acme"].Environment["Auth__Audience"].Should().Be("https://acme.xrm.test/");
        services["xrmd-zenith"].Environment["Auth__Audience"].Should().Be("https://zenith.xrm.test/");
    }

    [Fact]
    public async Task Explicit_audience_wins()
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal",
            "--auth", "entra", "--tenant-id", Guid.NewGuid().ToString(),
            "--admin-object-id", Guid.NewGuid().ToString(), "--admin-object-id", Guid.NewGuid().ToString());
        EditEnv(text => text.Replace("ORG_ZENITH_AUDIENCE=", "ORG_ZENITH_AUDIENCE=api://zenith-api"));

        Installation.Load(_dir).Orgs.Select(o => o.EffectiveAudience).Should().Equal("https://acme.xrm.test/", "api://zenith-api");
        Installation.Load(_dir).Save();
        var services = ComposeYaml.Resolve(_dir);
        services["xrmd-acme"].Environment["Auth__Audience"].Should().Be("https://acme.xrm.test/");
        services["xrmd-zenith"].Environment["Auth__Audience"].Should().Be("api://zenith-api");
    }

    [Theory]
    [InlineData("http://localhost:5001/;http://xrmd-acme:5000/")]
    [InlineData("http://xrmd-acme:5000, http://localhost:5001")]
    public async Task Development_audience_list_with_the_profile_url_is_accepted_and_passed_through(string list)
    {
        await InitAsync("--org", "acme");
        EditEnv(text => text.Replace("ORG_ACME_AUDIENCE=\n", $"ORG_ACME_AUDIENCE={list}\n"));

        Installation.Load(_dir).Orgs.Single().Audience.Should().Be(list);
        Installation.Load(_dir).Save();
        ComposeYaml.ReadEnv(_dir)["ORG_ACME_AUDIENCE"].Should().Be(list);
        ComposeYaml.Resolve(_dir)["xrmd-acme"].Environment["Auth__Audience"].Should().Be(list, "compose passes the value through unchanged");
    }

    [Theory]
    [InlineData("http://xrmd-acme:5000/", "http://localhost:5001/")]
    [InlineData("http://xrmd-acme:5000/;http://other:5000/", "http://localhost:5001/")]
    [InlineData("http://localhost:5001/;not a uri", "not a uri")]
    [InlineData("http://localhost:5001/;relative/path", "relative/path")]
    public async Task Audience_list_without_the_profile_url_or_with_a_bad_value_is_rejected(string list, string expected)
    {
        await InitAsync("--org", "acme");
        EditEnv(text => text.Replace("ORG_ACME_AUDIENCE=\n", $"ORG_ACME_AUDIENCE={list}\n"));

        var error = FluentActions.Invoking(() => Installation.Load(_dir)).Should().Throw<CliException>().Which;
        error.ExitCode.Should().Be(ExitCodes.Rejected);
        error.Message.Should().Contain("ORG_ACME_AUDIENCE").And.Contain(expected);
    }

    [Fact]
    public async Task Entra_audience_list_is_accepted()
    {
        await InitAsync("--org", "acme", "--auth", "entra", "--tenant-id", Guid.NewGuid().ToString(), "--admin-object-id", Guid.NewGuid().ToString());
        EditEnv(text => text.Replace("ORG_ACME_AUDIENCE=\n", "ORG_ACME_AUDIENCE=api://acme-api;http://xrmd-acme:5000/\n"));

        Installation.Load(_dir).Orgs.Single().Audience.Should().Be("api://acme-api;http://xrmd-acme:5000/");
    }

    public static IEnumerable<object[]> InconsistentHostEdits() =>
    [
        ["XRMD_DOMAIN=xrm.test", "XRMD_DOMAIN=XRM_test", "XRMD_DOMAIN"],
        ["ORG_ZENITH_AUDIENCE=\n", "ORG_ZENITH_AUDIENCE=\nORG_ZENITH_HOST=Not A Host\n", "ORG_ZENITH_HOST"],
        ["ORG_ZENITH_AUDIENCE=\n", "ORG_ZENITH_AUDIENCE=\nORG_ZENITH_HOST=acme.xrm.test\n", "ORG_ZENITH_HOST"],
        ["XRMD_ORGS=acme,zenith", "XRMD_ORGS=acme,zenith\nORG_ORBIT_HOST=orbit.example.com", "ORG_ORBIT_HOST"],
        ["PROXY_MODE=caddy", "PROXY_MODE=nginx", "PROXY_MODE"],
        ["TLS_MODE=internal", "TLS_MODE=", "TLS_MODE"],
        ["TLS_MODE=internal", "TLS_MODE=acme", "TLS_ACME_EMAIL"],
        ["EDGE_SUBNET=", "EDGE_SUBNET=172.31.250.5/24", "EDGE_SUBNET"],
        ["EDGE_SUBNET=", "EDGE_SUBNET=172.31.250.0/30", "EDGE_SUBNET"],
        ["EDGE_SUBNET=", "EDGE_SUBNET=172.31.250.0/28", "EDGE_SUBNET"], // too small to keep .10 out of the dynamic range
        ["EXPOSE_PORTS=", "EXPOSE_PORTS=yes", "EXPOSE_PORTS"],
        ["TRUSTED_PROXIES=", "TRUSTED_PROXIES=10.0.0.1", "TRUSTED_PROXIES"],
        ["PROXY_MODE=caddy", "PROXY_MODE=external", "TRUSTED_PROXIES"],
    ];

    [Theory]
    [MemberData(nameof(InconsistentHostEdits))]
    public async Task Inconsistent_host_settings_are_rejected_naming_the_key(string find, string replace, string key)
    {
        await InitAsync("--org", "acme", "--org", "zenith", "--domain", "xrm.test", "--tls", "internal");
        File.ReadAllText(EnvPath).Should().Contain(find);
        EditEnv(text => text.Replace(find, replace));

        var error = FluentActions.Invoking(() => Installation.Load(_dir)).Should().Throw<CliException>().Which;
        error.ExitCode.Should().Be(ExitCodes.Rejected);
        error.Message.Should().Contain(key);
    }

    [Theory]
    [InlineData("acme.example.com", true)]
    [InlineData("a.b.example.com", true)]
    [InlineData("xrm.test", true)]
    [InlineData("localhost", false)]
    [InlineData("Acme.example.com", false)]
    [InlineData("acme_1.example.com", false)]
    [InlineData("-acme.example.com", false)]
    [InlineData("acme.example.com.", false)]
    [InlineData("acme.example.c0m", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("acme.example.com\n", false)]
    public void ValidateHost_applies_the_host_rule(string host, bool valid)
    {
        var problem = Installation.ValidateHost(host);
        if (valid) problem.Should().BeNull();
        else problem.Should().Contain(Installation.HostRule);
    }
}
