using FluentAssertions;
using Xrmd.Cli.Server;

namespace Xrmd.Cli.Tests;

/// <summary>
/// Review Focus 1: an installation without public hosts renders exactly as before host routing (the 2.17 rendering, plus the
/// pinned image of Task 1 and ACCEPT_EULA read from .env, issue #16). The golden files under Golden/ were rendered by the CLI before host routing existed.
/// </summary>
public class GoldenComposeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xrmd-golden-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static string Golden(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    [Theory]
    [InlineData("bundled")]
    [InlineData("external")]
    public void Installation_without_hosts_renders_as_before(string fixture)
    {
        Directory.CreateDirectory(_dir);
        var env = Golden(fixture + ".env");
        File.WriteAllText(Path.Combine(_dir, ".env"), env);

        var installation = Installation.Load(_dir);
        installation.Proxy.Should().Be(ProxyMode.None);
        installation.Orgs.Should().OnlyContain(o => o.Host == null && o.PublicUrl == null && o.ClientUrl == o.Url);
        installation.Save();

        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().Be(Golden(fixture + ".compose.yml"));
        File.ReadAllText(Path.Combine(_dir, ".env")).Should().Be(env, "saving an installation without hosts adds no key");
    }
}
