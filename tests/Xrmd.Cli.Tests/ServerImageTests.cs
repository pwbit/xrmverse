using FluentAssertions;
using Xrmd.Cli.Server;

namespace Xrmd.Cli.Tests;

public class ServerImageTests
{
    [Fact]
    public void Default_of_a_release_version_is_the_pinned_ghcr_image() =>
        ServerImage.Default("1.4.2").Should().Be("ghcr.io/pwbit/xrmd:1.4.2");

    [Fact]
    public void Default_of_a_prerelease_keeps_the_suffix() =>
        ServerImage.Default("1.5.0-rc.1").Should().Be("ghcr.io/pwbit/xrmd:1.5.0-rc.1");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0")]
    [InlineData("0.0.0-dev")]
    public void Default_of_a_dev_version_is_the_local_image(string? version) =>
        ServerImage.Default(version).Should().Be("xrmd:local");

    [Fact]
    public void Default_of_a_version_that_is_not_semver_is_a_usage_error() =>
        FluentActions.Invoking(() => ServerImage.Default("latest")).Should().Throw<CliException>()
            .Which.ExitCode.Should().Be(ExitCodes.Usage);

    [Fact]
    public void WithTag_replaces_only_the_tag()
    {
        ServerImage.WithTag("ghcr.io/pwbit/xrmd:1.4.2", "1.5.0").Should().Be("ghcr.io/pwbit/xrmd:1.5.0");
        ServerImage.WithTag("ghcr.io/pwbit/xrmd", "1.5.0-rc.1").Should().Be("ghcr.io/pwbit/xrmd:1.5.0-rc.1");
    }

    [Fact]
    public void WithTag_moves_an_image_of_the_old_organization_to_the_new_repository()
    {
        // Installations written before the GitHub organization was renamed name ghcr.io/power-builders-italia/xrmd.
        ServerImage.WithTag("ghcr.io/power-builders-italia/xrmd:0.1.0-preview.4", "0.1.0-preview.5")
            .Should().Be("ghcr.io/pwbit/xrmd:0.1.0-preview.5");
        ServerImage.WithTag("ghcr.io/power-builders-italia/xrmd", "1.5.0").Should().Be("ghcr.io/pwbit/xrmd:1.5.0");
    }

    [Theory]
    [InlineData("xrmd:local")]
    [InlineData("ghcr.io/someone-else/xrmd:1.0.0")]
    [InlineData("ghcr.io/pwbit/xrmd-fork:1.0.0")]
    [InlineData("ghcr.io/power-builders-italia/xrmd-fork:1.0.0")]
    [InlineData("ghcr.io/power-builders-italia/xrmd@sha256:0000")]
    public void WithTag_rejects_a_foreign_image(string image) =>
        FluentActions.Invoking(() => ServerImage.WithTag(image, "1.5.0")).Should().Throw<CliException>()
            .Which.ExitCode.Should().Be(ExitCodes.Rejected);

    [Theory]
    [InlineData("latest")]
    [InlineData("1.5")]
    [InlineData("1.5.0+build")]
    [InlineData("")]
    public void WithTag_rejects_a_version_that_is_not_semver(string version) =>
        FluentActions.Invoking(() => ServerImage.WithTag("ghcr.io/pwbit/xrmd:1.4.2", version)).Should().Throw<CliException>()
            .Which.ExitCode.Should().Be(ExitCodes.Usage);

    [Fact]
    public void A_development_build_of_the_cli_assembly_maps_to_the_local_image()
    {
        // Nothing but the csproj sets the version of a dev build: it must not default to 1.0.0, an image nobody published.
        var informational = typeof(CliApp).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        var version = informational.Split('+')[0];
        version.Should().StartWith("0.0.0-dev");
        ServerImage.Default(version).Should().Be(ServerImage.LocalImage);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("0.1.0-preview.10", "0.1.0-preview.9", 1)]
    [InlineData("0.1.10", "0.1.0-preview.9", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta", "1.0.0-alpha", 1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    public void CompareSemVer_follows_semver_precedence(string a, string b, int expected)
    {
        Math.Sign(ServerImage.CompareSemVer(a, b)).Should().Be(expected);
        Math.Sign(ServerImage.CompareSemVer(b, a)).Should().Be(-expected);
    }

    [Theory]
    [InlineData("ghcr.io/pwbit/xrmd:1.4.2", "1.4.2")]
    [InlineData("ghcr.io/power-builders-italia/xrmd:0.1.0-preview.2", "0.1.0-preview.2")]
    [InlineData("ghcr.io/pwbit/xrmd", null)]
    [InlineData("xrmd:local", null)]
    [InlineData("ghcr.io/pwbit/xrmd@sha256:abc", null)]
    public void TagOf_reads_the_tag_of_the_published_image_only(string image, string? expected) =>
        ServerImage.TagOf(image).Should().Be(expected);
}
