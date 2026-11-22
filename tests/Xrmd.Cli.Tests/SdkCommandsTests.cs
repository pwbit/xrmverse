using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Cli.Sdk;

namespace Xrmd.Cli.Tests;

public class SdkCommandsTests
{
    internal static readonly string Nupkg = Path.Combine(
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages
            ? packages
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
        "microsoft.powerplatform.dataverse.client", SdkPackage.Version, $"microsoft.powerplatform.dataverse.client.{SdkPackage.Version}.nupkg");

    /// <summary>The user SDK folder of a test: <c>XRMD_SDK_DIR</c>, which <see cref="TestCli"/> points into its config dir.</summary>
    private static string UserSdk(TestCli cli) => cli.Vars[SdkFolder.DirectoryVariable]!;

    private static void AssertNothingWritten(TestCli cli) =>
        Directory.Exists(cli.ConfigDir).Should().BeFalse("a refused install writes nothing");

    [Fact]
    public async Task User_folder_defaults_to_home_xrmd_sdk_like_the_host_whatever_the_config_dir()
    {
        using var cli = new TestCli();
        cli.Vars.Remove(SdkFolder.DirectoryVariable);
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xrmd", "sdk");

        await cli.RunAsync("sdk", "status", "-o", "json");

        (cli.Out.ToString() + cli.Err.ToString()).Should().Contain(home).And.NotContain(cli.ConfigDir,
            "the CLI and the host share one default SDK folder (SdkFolder), not <XRMD_CONFIG_DIR>/sdk");
    }

    [Fact]
    public async Task Install_from_file_with_flag_writes_three_dlls_and_record()
    {
        using var cli = new TestCli();
        var code = await cli.RunAsync("sdk", "install", "--from", Nupkg, "--accept-microsoft-license");

        code.Should().Be(0, cli.Err.ToString());
        foreach (var dll in SdkPackage.Assemblies) File.Exists(Path.Combine(UserSdk(cli), dll)).Should().BeTrue(dll);
        Directory.GetFiles(UserSdk(cli)).Should().HaveCount(4);
        Directory.GetDirectories(cli.ConfigDir).Should().ContainSingle("no temp folder is left behind");
        var record = SdkFolder.TryRead(UserSdk(cli))!;
        record.Version.Should().Be(SdkPackage.Version);
        record.Sha512.Should().Be(SdkPackage.Sha512Base64);
        record.LicenseUrl.Should().Be(SdkPackage.LicenseUrl);
        record.AcceptedBy.Should().Be("flag");
        record.AcceptedAt.Should().Be(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        ((FakeHandler)cli.Handler).Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Install_refuses_a_package_with_a_wrong_hash()
    {
        using var cli = new TestCli();
        var bad = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".nupkg");
        await File.WriteAllBytesAsync(bad, [1, 2, 3]);
        try
        {
            var code = await cli.RunAsync("sdk", "install", "--from", bad, "--accept-microsoft-license");
            code.Should().Be(1);
            cli.Err.ToString().Should().Contain("does not match").And.Contain("Nothing was installed");
            Directory.Exists(UserSdk(cli)).Should().BeFalse();
            (Directory.Exists(cli.ConfigDir) ? Directory.GetFileSystemEntries(cli.ConfigDir) : []).Should().BeEmpty();
        }
        finally { File.Delete(bad); }
    }

    [Fact]
    public async Task Install_refuses_a_valid_zip_with_the_wrong_hash_and_leaves_nothing()
    {
        using var cli = new TestCli();
        var bad = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".nupkg");
        using (var zip = System.IO.Compression.ZipFile.Open(bad, System.IO.Compression.ZipArchiveMode.Create))
            foreach (var dll in SdkPackage.Assemblies)
                await using (var w = zip.CreateEntry("lib/net10.0/" + dll).Open()) await w.WriteAsync(new byte[] { 1 });
        try
        {
            (await cli.RunAsync("sdk", "install", "--from", bad, "--accept-microsoft-license")).Should().Be(1);
            cli.Err.ToString().Should().Contain("does not match");
            (Directory.Exists(cli.ConfigDir) ? Directory.GetFileSystemEntries(cli.ConfigDir) : []).Should().BeEmpty();
        }
        finally { File.Delete(bad); }
    }

    [Fact]
    public async Task Install_refuses_a_package_that_has_only_lib_net8_0_naming_the_net10_path()
    {
        // The hash check comes first, so the package is built here and its own hash is passed as the pinned one.
        using var package = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(package, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var dll in SdkPackage.Assemblies)
                await using (var w = zip.CreateEntry("lib/net8.0/" + dll).Open()) await w.WriteAsync(new byte[] { 1 });
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(package.ToArray()));
        var parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "sdk");
        try
        {
            package.Position = 0;
            Func<Task> act = () => SdkInstaller.InstallAsync(package, target, AcceptanceKind.Flag, hash);

            (await act.Should().ThrowAsync<CliException>())
                .Which.Message.Should().Contain("lib/net10.0/" + SdkPackage.Assemblies[0]).And.Contain("Nothing was installed");
            Directory.Exists(target).Should().BeFalse();
            (Directory.Exists(parent) ? Directory.GetFileSystemEntries(parent) : []).Should().BeEmpty("no staging folder is left");
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task Install_refuses_a_non_empty_folder_that_is_not_an_sdk_folder()
    {
        using var cli = new TestCli();
        var target = UserSdk(cli);
        Directory.CreateDirectory(target);
        var precious = Path.Combine(target, "notes.txt");
        await File.WriteAllTextAsync(precious, "keep");

        var code = await cli.RunAsync("sdk", "install", "--from", Nupkg, "--accept-microsoft-license");

        code.Should().Be(1);
        cli.Err.ToString().Should().Contain(target).And.Contain("not an SDK folder");
        File.ReadAllText(precious).Should().Be("keep");
        Directory.GetFileSystemEntries(target).Should().ContainSingle();
        Directory.GetFileSystemEntries(cli.ConfigDir).Should().ContainSingle("no staging folder is left");
    }

    [Fact]
    public async Task Install_replaces_an_empty_folder_and_an_existing_sdk_folder()
    {
        using var cli = new TestCli();
        Directory.CreateDirectory(UserSdk(cli));
        (await cli.RunAsync("sdk", "install", "--from", Nupkg, "--accept-microsoft-license")).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("sdk", "install", "--from", Nupkg, "--accept-microsoft-license")).Should().Be(0, cli.Err.ToString());
        Directory.GetFileSystemEntries(cli.ConfigDir).Should().ContainSingle();
    }

    [Fact]
    public async Task Install_maps_a_download_timeout_to_unreachable()
    {
        using var cli = new TestCli(_ => throw new TaskCanceledException("timeout", new TimeoutException()));
        (await cli.RunAsync("sdk", "install", "--accept-microsoft-license")).Should().Be(ExitCodes.Unreachable);
        cli.Err.ToString().Should().Contain("timed out");
        AssertNothingWritten(cli);
    }

    [Fact]
    public async Task Install_without_acceptance_downloads_nothing()
    {
        using var cli = new TestCli { IsInputTty = false };
        var code = await cli.RunAsync("sdk", "install");

        code.Should().Be(2);
        cli.Err.ToString().Should().Contain("--accept-microsoft-license").And.Contain(SdkPackage.LicenseUrl);
        ((FakeHandler)cli.Handler).Requests.Should().BeEmpty();
        AssertNothingWritten(cli);

        // The same for the offline variant: no extraction without acceptance.
        (await cli.RunAsync("sdk", "install", "--from", Nupkg)).Should().Be(2);
        AssertNothingWritten(cli);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n")]
    [InlineData("no")]
    [InlineData("maybe")]
    public async Task Install_prompt_defaults_to_no(string answer)
    {
        using var cli = new TestCli { IsInputTty = true, Input = answer + "\n" };
        var code = await cli.RunAsync("sdk", "install");

        code.Should().Be(1);
        cli.Err.ToString().Should().Contain(SdkPackage.LicenseUrl).And.Contain("[y/N]");
        ((FakeHandler)cli.Handler).Requests.Should().BeEmpty();
        AssertNothingWritten(cli);
    }

    private static readonly string Dataverse = $"{SdkPackage.Id} {SdkPackage.Version}";
    private static readonly string Workflow = $"{SdkPackage.Workflow.Id} {SdkPackage.Workflow.Version}";

    [Fact]
    public async Task Install_prompt_names_both_packages_with_their_versions()
    {
        using var cli = new TestCli { IsInputTty = true, Input = "n\n" };
        (await cli.RunAsync("sdk", "install")).Should().Be(1);

        cli.Err.ToString().Should().Contain(Dataverse).And.Contain(Workflow).And.Contain(SdkPackage.LicenseUrl);
        ((FakeHandler)cli.Handler).Requests.Should().BeEmpty();

        (await cli.RunAsync("sdk", "install", "--from", Nupkg, "--from-workflow", SdkInstallerTests.WorkflowNupkg)).Should().Be(1);
        cli.Err.ToString().Should().Contain(Dataverse).And.Contain(Workflow);
        AssertNothingWritten(cli);
    }

    [Fact]
    public async Task Install_prompt_with_from_alone_names_only_the_dataverse_package()
    {
        using var cli = new TestCli { IsInputTty = true, Input = "n\n" };
        (await cli.RunAsync("sdk", "install", "--from", Nupkg)).Should().Be(1);

        cli.Err.ToString().Should().Contain(Dataverse).And.NotContain(SdkPackage.Workflow.Id, "with --from alone the workflow SDK is not installed");
        AssertNothingWritten(cli);
    }

    [Fact]
    public async Task Acceptance_refusal_and_flag_help_name_both_packages()
    {
        using var cli = new TestCli { IsInputTty = false };
        (await cli.RunAsync("sdk", "install")).Should().Be(2);
        cli.Err.ToString().Should().Contain(Dataverse).And.Contain(Workflow);

        await cli.RunAsync("sdk", "install", "--help");
        var help = System.Text.RegularExpressions.Regex.Replace(cli.Out.ToString(), @"\s+", " ");
        var accept = help[help.IndexOf("--accept-microsoft-license", StringComparison.Ordinal)..];
        accept.Should().Contain(Dataverse).And.Contain(Workflow);
    }

    [Fact]
    public async Task Reinstall_with_from_alone_says_that_it_removed_the_workflow_sdk()
    {
        using var cli = new TestCli();
        (await cli.RunAsync("sdk", "install", "--from", Nupkg, "--from-workflow", SdkInstallerTests.WorkflowNupkg, "--accept-microsoft-license"))
            .Should().Be(0, cli.Err.ToString());
        File.Exists(Path.Combine(UserSdk(cli), SdkPackage.Workflow.AssemblyFileName)).Should().BeTrue();

        (await cli.RunAsync("sdk", "install", "--from", Nupkg, "--accept-microsoft-license")).Should().Be(0, cli.Err.ToString());

        File.Exists(Path.Combine(UserSdk(cli), SdkPackage.Workflow.AssemblyFileName)).Should().BeFalse();
        cli.Err.ToString().Should().Contain($"{SdkPackage.Workflow.Id} was removed from {UserSdk(cli)}")
            .And.Contain("--from-workflow");
    }

    [Fact]
    public async Task Install_with_from_alone_notes_that_the_workflow_sdk_is_not_installed()
    {
        using var cli = new TestCli();
        (await cli.RunAsync("sdk", "install", "--from", Nupkg, "--accept-microsoft-license")).Should().Be(0, cli.Err.ToString());

        cli.Err.ToString().Should().Contain($"{SdkPackage.Workflow.Id} is not installed").And.Contain("--from-workflow")
            .And.NotContain("was removed");
    }

    [Fact]
    public async Task Install_prompt_yes_installs_and_records_prompt()
    {
        using var cli = new TestCli { IsInputTty = true, Input = "y\n" };
        (await cli.RunAsync("sdk", "install", "--from", Nupkg)).Should().Be(0, cli.Err.ToString());
        SdkFolder.TryRead(UserSdk(cli))!.AcceptedBy.Should().Be("prompt");
    }

    [Fact]
    public async Task Install_downloads_from_the_pinned_url()
    {
        var bytes = await File.ReadAllBytesAsync(Nupkg);
        var workflow = await File.ReadAllBytesAsync(SdkInstallerTests.WorkflowNupkg);
        using var cli = new TestCli(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsoluteUri == SdkPackage.Workflow.DownloadUrl ? workflow : bytes),
        });
        var code = await cli.RunAsync("sdk", "install", "--accept-microsoft-license");

        code.Should().Be(0, cli.Err.ToString());
        var handler = (FakeHandler)cli.Handler;
        // Plan 2.14, W18: the workflow SDK is downloaded with the Dataverse SDK, each from its pinned URL.
        handler.Requests.Select(r => r.RequestUri!.AbsoluteUri).Should().Equal(SdkPackage.DownloadUrl, SdkPackage.Workflow.DownloadUrl);
        SdkFolder.TryRead(UserSdk(cli)).Should().NotBeNull();
        File.Exists(Path.Combine(UserSdk(cli), SdkPackage.Workflow.AssemblyFileName)).Should().BeTrue();
    }

    [Fact]
    public async Task Install_with_dir_writes_to_installation_sdk_folder()
    {
        using var cli = new TestCli();
        var installation = Path.Combine(cli.ConfigDir, "installation");
        (await cli.RunAsync("sdk", "install", "--from", Nupkg, "--accept-microsoft-license", "--dir", installation)).Should().Be(0, cli.Err.ToString());

        SdkFolder.TryRead(Path.Combine(installation, "sdk")).Should().NotBeNull();
        Directory.Exists(UserSdk(cli)).Should().BeFalse("--dir alone does not install into the user folder");

        // Both targets in one call; an existing folder is replaced.
        (await cli.RunAsync("sdk", "install", "--from", Nupkg, "--accept-microsoft-license", "--dir", installation, "--user")).Should().Be(0, cli.Err.ToString());
        SdkFolder.TryRead(UserSdk(cli)).Should().NotBeNull();
        Directory.GetDirectories(installation).Should().ContainSingle();
    }

    [Fact]
    public async Task Sdk_status_reports_version_path_and_acceptance()
    {
        using var cli = new TestCli();
        (await cli.RunAsync("sdk", "status")).Should().Be(1);
        cli.Err.ToString().Should().Be(SdkPackage.MissingMessage(UserSdk(cli)) + Environment.NewLine);

        await cli.RunAsync("sdk", "install", "--from", Nupkg, "--accept-microsoft-license");
        (await cli.RunAsync("sdk", "status")).Should().Be(0);
        cli.Out.ToString().Should().Contain(SdkPackage.Version).And.Contain(UserSdk(cli)).And.Contain("flag").And.Contain("2026-11-08");

        (await cli.RunAsync("sdk", "status", "-o", "json")).Should().Be(0);
        using var json = JsonDocument.Parse(cli.Out.ToString());
        json.RootElement.GetProperty("version").GetString().Should().Be(SdkPackage.Version);
        json.RootElement.GetProperty("path").GetString().Should().Be(UserSdk(cli));
        json.RootElement.GetProperty("acceptedBy").GetString().Should().Be("flag");
        json.RootElement.TryGetProperty("acceptedAt", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Status_shows_workflow_sdk_or_missing()
    {
        using var cli = new TestCli();
        await cli.RunAsync("sdk", "install", "--from", Nupkg, "--accept-microsoft-license");

        (await cli.RunAsync("sdk", "status", "-o", "json")).Should().Be(0, cli.Err.ToString());
        using (var json = JsonDocument.Parse(cli.Out.ToString()))
        {
            json.RootElement.GetProperty("version").GetString().Should().Be(SdkPackage.Version);
            json.RootElement.GetProperty("workflowSdk").GetString().Should().Be("missing (run 'xrmd sdk install')");
            json.RootElement.GetProperty("workflowVersion").ValueKind.Should().Be(JsonValueKind.Null);
        }
        (await cli.RunAsync("sdk", "status")).Should().Be(0);
        cli.Out.ToString().Should().Contain("missing (run 'xrmd sdk install')");

        await cli.RunAsync("sdk", "install", "--from", Nupkg, "--from-workflow", SdkInstallerTests.WorkflowNupkg, "--accept-microsoft-license");
        (await cli.RunAsync("sdk", "status", "-o", "json")).Should().Be(0, cli.Err.ToString());
        using (var json = JsonDocument.Parse(cli.Out.ToString()))
        {
            json.RootElement.GetProperty("version").GetString().Should().Be(SdkPackage.Version);
            json.RootElement.GetProperty("workflowSdk").GetString().Should().Be($"{SdkPackage.Workflow.Id} {SdkPackage.Workflow.Version}");
            json.RootElement.GetProperty("workflowVersion").GetString().Should().Be(SdkPackage.Workflow.Version);
            json.RootElement.GetProperty("workflowSha512").GetString().Should().Be(SdkPackage.Workflow.Sha512Base64);
        }
        (await cli.RunAsync("sdk", "status")).Should().Be(0);
        cli.Out.ToString().Should().Contain($"{SdkPackage.Workflow.Id} {SdkPackage.Workflow.Version}");
    }

    [Fact]
    public void Resolve_prefers_explicit_then_variable_then_default()
    {
        SdkFolder.Resolve("/a", _ => "/b", "/c").Should().Be("/a");
        SdkFolder.Resolve(null, _ => "/b", "/c").Should().Be("/b");
        SdkFolder.Resolve(null, _ => null, "/c").Should().Be("/c");
    }
}
