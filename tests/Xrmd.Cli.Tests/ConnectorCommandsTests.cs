using FluentAssertions;
using Xrmd.Cli.Config;

namespace Xrmd.Cli.Tests;

/// <summary><c>xrmd connector export</c>: the custom connector package of one organization, written without a server.</summary>
public class ConnectorCommandsTests : IDisposable
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private readonly string _out = Path.Combine(Path.GetTempPath(), "xrmd-connector-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_out, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static TestCli WithProfile(string url, string mode = AuthModes.Browser, string? idp = null)
    {
        var cli = new TestCli();
        new ProfileStore(cli.ConfigDir).Save(new CliConfig
        {
            CurrentProfile = "p",
            Profiles = { ["p"] = new Profile { Url = url, Mode = mode, Idp = idp } },
        });
        return cli;
    }

    private Task<int> ExportAsync(TestCli cli, params string[] extra) =>
        cli.RunAsync(["connector", "export", "--out", _out, "--client-id", ClientId, .. extra]);

    [Fact]
    public async Task Export_writes_three_files_with_host_resource_and_client_id()
    {
        using var cli = WithProfile("https://acme.example.com/");
        (await ExportAsync(cli)).Should().Be(0, cli.Err.ToString());

        Directory.GetFiles(_out).Select(Path.GetFileName).Should().BeEquivalentTo("apiDefinition.swagger.json", "apiProperties.json", "icon.png");
        File.ReadAllText(Path.Combine(_out, "apiDefinition.swagger.json")).Should().Contain("\"host\": \"acme.example.com\"");
        var props = File.ReadAllText(Path.Combine(_out, "apiProperties.json"));
        props.Should().Contain(ClientId).And.Contain("https://acme.example.com");
        props.Should().NotContain("https://acme.example.com/");
        File.ReadAllBytes(Path.Combine(_out, "icon.png")).Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47);
    }

    [Fact]
    public async Task Export_uses_resource_option()
    {
        using var cli = WithProfile("https://acme.example.com/");
        (await ExportAsync(cli, "--resource", "api://xrmverse-acme")).Should().Be(0, cli.Err.ToString());
        File.ReadAllText(Path.Combine(_out, "apiProperties.json")).Should().Contain("api://xrmverse-acme");
    }

    [Fact]
    public async Task Export_keeps_non_default_port_in_host()
    {
        using var cli = WithProfile("https://acme.example.com:8443/");
        (await ExportAsync(cli)).Should().Be(0, cli.Err.ToString());
        File.ReadAllText(Path.Combine(_out, "apiDefinition.swagger.json")).Should().Contain("\"host\": \"acme.example.com:8443\"");
        File.ReadAllText(Path.Combine(_out, "apiProperties.json")).Should().Contain("https://acme.example.com:8443");
    }

    [Fact]
    public async Task Export_refuses_local_idp_profile()
    {
        using var cli = WithProfile("https://acme.example.com/", idp: IdentityProviders.Local);
        (await ExportAsync(cli)).Should().Be(2);
        cli.Err.ToString().Should().Contain("Local identity provider organizations are not supported by the connector yet.");
        Directory.Exists(_out).Should().BeFalse();
    }

    [Fact]
    public async Task Export_refuses_dev_mode_profile()
    {
        using var cli = WithProfile("https://acme.example.com/", mode: AuthModes.Dev);
        (await ExportAsync(cli)).Should().Be(2);
        Directory.Exists(_out).Should().BeFalse();
    }

    [Fact]
    public async Task Export_refuses_http_url()
    {
        using var cli = WithProfile("http://acme.example.com/");
        (await ExportAsync(cli)).Should().Be(2);
        cli.Err.ToString().Should().Contain("https");
        Directory.Exists(_out).Should().BeFalse();
    }

    [Fact]
    public async Task Export_refuses_existing_files_without_force()
    {
        using var cli = WithProfile("https://acme.example.com/");
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "apiProperties.json"), "old");

        (await ExportAsync(cli)).Should().Be(2);
        File.ReadAllText(Path.Combine(_out, "apiProperties.json")).Should().Be("old");

        (await ExportAsync(cli, "--force")).Should().Be(0, cli.Err.ToString());
        File.ReadAllText(Path.Combine(_out, "apiProperties.json")).Should().Contain(ClientId);
    }

    [Fact]
    public async Task Export_output_has_no_placeholder_left()
    {
        using var cli = WithProfile("https://acme.example.com/");
        (await ExportAsync(cli)).Should().Be(0, cli.Err.ToString());
        foreach (var name in new[] { "apiDefinition.swagger.json", "apiProperties.json" })
            File.ReadAllText(Path.Combine(_out, name)).Should().NotContain("{{");
    }
}
