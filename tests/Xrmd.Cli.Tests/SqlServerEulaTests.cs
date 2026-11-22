using FluentAssertions;
using Xrmd.Cli.Server;

namespace Xrmd.Cli.Tests;

/// <summary>
/// Issue #16, option 1: the bundled SQL Server's license terms are accepted by the user, with <c>--accept-sql-server-eula</c>
/// or at the prompt, and only then is <c>ACCEPT_EULA=Y</c> written to <c>.env</c>. Nothing is ever accepted for the user.
/// </summary>
public class SqlServerEulaTests : IDisposable
{
    private const string Flag = "--accept-sql-server-eula";
    private const string External = "Server=sql.example.com;User Id=xrmd;Password=x";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xrmd-eula-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string EnvPath => Path.Combine(_dir, ".env");

    private Dictionary<string, string> ReadEnv() =>
        File.ReadAllLines(EnvPath)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1]);

    /// <summary>An installation accepted at init, whose ACCEPT_EULA line is then removed: a .env from before the key.</summary>
    private async Task InitWithoutEulaAsync(TestCli cli, params string[] extra)
    {
        (await cli.RunAsync(["server", "init", "--dir", _dir, "--org", "acme", Flag, .. extra])).Should().Be(0, cli.Err.ToString());
        FakeSdk.Install(_dir);
        File.WriteAllLines(EnvPath, File.ReadAllLines(EnvPath).Where(l => !l.StartsWith("ACCEPT_EULA=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("Express", SqlServerLicense.ExpressTermsUrl)]
    [InlineData("Developer", SqlServerLicense.DownloadsUrl)]
    [InlineData("Standard", SqlServerLicense.VolumeLicensingUrl)]
    public async Task Init_with_bundled_sql_requires_eula_acceptance(string edition, string terms)
    {
        using var cli = new TestCli { IsInputTty = false };

        (await cli.RunAsync("server", "init", "--dir", _dir, "--org", "acme", "--sql-edition", edition)).Should().Be(ExitCodes.Usage);

        cli.Err.ToString().Should().Contain(Flag).And.Contain($"the {edition} edition").And.Contain(terms)
            .And.Contain(SqlServerLicense.ContainerTermsUrl).And.Contain("Nothing was written");
        Directory.Exists(_dir).Should().BeFalse("nothing is written before the terms are accepted");
    }

    [Fact]
    public async Task Init_never_prints_a_product_key()
    {
        using var cli = new TestCli { IsInputTty = false };
        (await cli.RunAsync("server", "init", "--dir", _dir, "--sql-edition", "ABCDE-12345-FGHIJ-67890-KLMNO")).Should().Be(ExitCodes.Usage);
        cli.Err.ToString().Should().Contain("the edition of your product key").And.NotContain("12345");
    }

    [Fact]
    public async Task Init_accepting_writes_accept_eula_y()
    {
        using var cli = new TestCli();
        (await cli.RunAsync("server", "init", "--dir", _dir, "--org", "acme", Flag)).Should().Be(0, cli.Err.ToString());

        ReadEnv()["ACCEPT_EULA"].Should().Be("Y");
        ComposeYaml.Resolve(_dir)["sqlserver"].Environment["ACCEPT_EULA"].Should().Be("Y");
        cli.Out.ToString().Should().Contain("Wrote ACCEPT_EULA=Y").And.Contain(SqlServerLicense.ExpressTermsUrl);
    }

    [Fact]
    public async Task Init_at_a_terminal_asks_default_no()
    {
        using (var no = new TestCli { IsInputTty = true, Input = "\n" })
        {
            (await no.RunAsync("server", "init", "--dir", _dir, "--org", "acme")).Should().Be(ExitCodes.Usage);
            no.Err.ToString().Should().Contain("Do you accept the SQL Server license terms? [y/N]").And.Contain(SqlServerLicense.ExpressTermsUrl);
            Directory.Exists(_dir).Should().BeFalse();
        }

        // Yes, then no to the Microsoft SDK question that follows.
        using var yes = new TestCli { IsInputTty = true, Input = "y\nn\n" };
        (await yes.RunAsync("server", "init", "--dir", _dir, "--org", "acme")).Should().Be(0, yes.Err.ToString());
        ReadEnv()["ACCEPT_EULA"].Should().Be("Y");
    }

    [Fact]
    public async Task Init_with_external_sql_needs_no_eula_flag_and_refuses_it()
    {
        using var cli = new TestCli { IsInputTty = false };
        (await cli.RunAsync("server", "init", "--dir", _dir, "--org", "acme", "--sql-connection", External, Flag)).Should().Be(ExitCodes.Usage);
        cli.Err.ToString().Should().Contain(Flag).And.Contain("--sql-connection");
        Directory.Exists(_dir).Should().BeFalse();

        (await cli.RunAsync("server", "init", "--dir", _dir, "--org", "acme", "--sql-connection", External)).Should().Be(0, cli.Err.ToString());
        ReadEnv().Should().NotContainKey("ACCEPT_EULA");
        File.ReadAllText(EnvPath).Should().NotContain("ACCEPT_EULA");
        File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")).Should().NotContain("ACCEPT_EULA");
    }

    [Fact]
    public async Task Init_force_keeps_an_accepted_eula()
    {
        using var cli = new TestCli { IsInputTty = false };
        (await cli.RunAsync("server", "init", "--dir", _dir, "--org", "acme", Flag)).Should().Be(0, cli.Err.ToString());

        (await cli.RunAsync("server", "init", "--dir", _dir, "--org", "acme", "--force")).Should().Be(0, cli.Err.ToString());

        ReadEnv()["ACCEPT_EULA"].Should().Be("Y");
        cli.Out.ToString().Should().Contain("Kept ACCEPT_EULA=Y");
    }

    [Fact]
    public void Rendered_sqlserver_reads_accept_eula_from_env()
    {
        var template = ComposeRenderer.ReadTemplate("docker-compose.yml");
        template.Should().Contain("ACCEPT_EULA: ${ACCEPT_EULA:?").And.NotContain("ACCEPT_EULA: \"Y\"");
    }

    [Fact]
    public async Task Up_on_an_installation_without_accept_eula_exits_5_naming_the_flag()
    {
        using var cli = new TestCli { IsInputTty = false };
        await InitWithoutEulaAsync(cli);
        var runner = new FakeProcessRunner();
        cli.Process = runner;
        var env = File.ReadAllText(EnvPath);

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(ExitCodes.Rejected);
        cli.Err.ToString().Should().Contain("has no ACCEPT_EULA=Y").And.Contain($"xrmd server up --dir {_dir} {Flag}")
            .And.Contain(SqlServerLicense.ExpressTermsUrl);
        (await cli.RunAsync("server", "upgrade", "--dir", _dir)).Should().Be(ExitCodes.Rejected);
        (await cli.RunAsync("server", "org", "add", "zenith", "--dir", _dir)).Should().Be(ExitCodes.Rejected);

        runner.Calls.Should().BeEmpty("nothing starts before the terms are accepted");
        File.ReadAllText(EnvPath).Should().Be(env);
    }

    [Fact]
    public async Task Up_accepting_writes_the_key_and_starts()
    {
        using var cli = new TestCli { IsInputTty = false };
        await InitWithoutEulaAsync(cli);
        var runner = new FakeProcessRunner();
        cli.Process = runner;

        (await cli.RunAsync("server", "up", "--dir", _dir, Flag)).Should().Be(0, cli.Err.ToString());

        ReadEnv()["ACCEPT_EULA"].Should().Be("Y");
        File.ReadAllText(EnvPath).Should().Contain(Installation.SqlEulaComment + "\nACCEPT_EULA=Y");
        cli.Err.ToString().Should().Contain("Wrote ACCEPT_EULA=Y");
        runner.Calls.Should().ContainSingle().Which.Arguments.Should().Contain("up");
    }

    [Fact]
    public async Task Up_at_a_terminal_asks_and_a_yes_writes_the_key()
    {
        using var cli = new TestCli { IsInputTty = true, Input = "y\n" };
        await InitWithoutEulaAsync(cli);
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "up", "--dir", _dir)).Should().Be(0, cli.Err.ToString());

        cli.Err.ToString().Should().Contain("Do you accept the SQL Server license terms? [y/N]");
        ReadEnv()["ACCEPT_EULA"].Should().Be("Y");
    }

    [Theory]
    [InlineData("ACCEPT_EULA=N")]
    [InlineData("ACCEPT_EULA=yes")]
    public async Task An_accept_eula_other_than_y_is_inconsistent(string line)
    {
        using var cli = new TestCli();
        await InitWithoutEulaAsync(cli);
        File.AppendAllText(EnvPath, line + "\n");
        cli.Process = new FakeProcessRunner();

        (await cli.RunAsync("server", "up", "--dir", _dir, Flag)).Should().Be(ExitCodes.Rejected);
        cli.Err.ToString().Should().Contain("ACCEPT_EULA must be Y");
    }
}
