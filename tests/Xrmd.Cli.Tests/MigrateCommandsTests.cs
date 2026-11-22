using System.Text.Json;
using FluentAssertions;
using Xrmd.Migration.Tests;

namespace Xrmd.Cli.Tests;

public sealed class MigrateCommandsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-cli-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static TestCli Cli(string? passphrase = SyntheticPackage.Passphrase)
    {
        var cli = new TestCli();
        if (passphrase is not null) cli.Vars["XRMD_MIGRATE_PASSPHRASE"] = passphrase;
        return cli;
    }

    [Fact]
    public async Task Inspect_prints_tables_and_counts()
    {
        var file = SyntheticPackage.BuildFile(_root);
        using var cli = Cli();
        (await cli.RunAsync("migrate", "inspect", file)).Should().Be(0, cli.Err.ToString());
        var text = cli.Out.ToString();
        text.Should().Contain("account").And.Contain("contact").And.Contain("xv_account_contact").And.Contain("https://contoso.example.com");
        text.Should().MatchRegex(@"account\s+1\s+1");
    }

    [Fact]
    public async Task Inspect_json_contains_manifest_and_verification()
    {
        var file = SyntheticPackage.BuildFile(_root);
        using var cli = Cli();
        (await cli.RunAsync("migrate", "inspect", file, "-o", "json")).Should().Be(0, cli.Err.ToString());
        using var doc = JsonDocument.Parse(cli.Out.ToString());
        doc.RootElement.GetProperty("manifest").GetProperty("tables").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("verification").GetProperty("problems").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Inspect_reads_passphrase_from_stdin()
    {
        var file = SyntheticPackage.BuildFile(_root);
        using var cli = Cli(passphrase: null);
        cli.Input = SyntheticPackage.Passphrase + "\n";
        (await cli.RunAsync("migrate", "inspect", file, "--passphrase-stdin")).Should().Be(0, cli.Err.ToString());
    }

    [Fact]
    public async Task Inspect_without_passphrase_is_a_usage_error()
    {
        var file = SyntheticPackage.BuildFile(_root);
        using var cli = Cli(passphrase: null);
        (await cli.RunAsync("migrate", "inspect", file)).Should().Be(2);
        cli.Err.ToString().Should().Contain("XRMD_MIGRATE_PASSPHRASE");
    }

    [Fact]
    public async Task Inspect_with_wrong_passphrase_exits_3()
    {
        var file = SyntheticPackage.BuildFile(_root);
        using var cli = Cli("another passphrase");
        (await cli.RunAsync("migrate", "inspect", file)).Should().Be(3);
        cli.Err.ToString().Should().Contain("passphrase is wrong");
    }

    [Fact]
    public async Task Inspect_of_tampered_package_exits_8_and_names_the_entry()
    {
        var folder = SyntheticPackage.BuildFolder(Path.Combine(_root, "pkg"));
        var blob = SyntheticPackage.BlobOf(folder, SyntheticPackage.AccountChunk);
        var bytes = File.ReadAllBytes(blob);
        bytes[20] ^= 1;
        File.WriteAllBytes(blob, bytes);

        using var cli = Cli();
        (await cli.RunAsync("migrate", "inspect", folder)).Should().Be(8);
        cli.Err.ToString().Should().Contain(SyntheticPackage.AccountChunk);
        (await cli.RunAsync("migrate", "inspect", folder, "--no-verify")).Should().Be(0, cli.Err.ToString());
    }

    [Fact]
    public async Task Inspect_of_a_file_that_is_not_a_package_exits_8()
    {
        var file = Path.Combine(_root, "x.xrmpkg");
        await File.WriteAllTextAsync(file, "not a zip");
        using var cli = Cli();
        (await cli.RunAsync("migrate", "inspect", file)).Should().Be(8);
    }

    [Fact]
    public async Task Inspect_of_a_missing_file_is_a_usage_error()
    {
        using var cli = Cli();
        (await cli.RunAsync("migrate", "inspect", Path.Combine(_root, "nope.xrmpkg"))).Should().Be(2);
    }
}
