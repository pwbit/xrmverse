using System.Xml.Linq;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

public sealed class PackageMetadataTests
{
    private const string Notice =
        "xrmverse is an independent project and is not affiliated with, endorsed by or sponsored by Microsoft. "
        + "Microsoft, Dataverse, Dynamics 365 and Power Apps are trademarks of the Microsoft group of companies.";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Xrmd.Cli", "Xrmd.Cli.csproj"))) dir = dir.Parent;
        dir.Should().NotBeNull("the test runs inside the repository");
        return dir!.FullName;
    }

    [Fact]
    public void Cli_package_description_contains_the_trademark_notice()
    {
        var csproj = XDocument.Load(Path.Combine(RepoRoot(), "src", "Xrmd.Cli", "Xrmd.Cli.csproj"));
        var description = csproj.Descendants("Description").Single().Value;
        description.Should().EndWith(Notice);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docker/Dockerfile")]
    [InlineData("packaging/homebrew/xrmd.rb.template")]
    [InlineData("ui/package.json")]
    public void Other_distribution_surfaces_contain_the_trademark_notice(string file)
    {
        File.ReadAllText(Path.Combine(RepoRoot(), file)).Should().Contain(Notice);
    }

    [Fact]
    public void Repository_is_licensed_under_apache_2_with_a_notice()
    {
        var root = RepoRoot();
        File.ReadAllText(Path.Combine(root, "LICENSE")).Should().Contain("Apache License").And.Contain("Version 2.0, January 2004");
        File.ReadAllText(Path.Combine(root, "NOTICE")).Should().Contain("Copyright 2026 Power Builders Italia S.r.l.");
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        props.Descendants("PackageLicenseExpression").Single().Value.Should().Be("Apache-2.0");
    }

    [Theory]
    [InlineData("docker/Dockerfile", "org.opencontainers.image.licenses=\"Apache-2.0\"")]
    [InlineData("packaging/homebrew/xrmd.rb.template", "license \"Apache-2.0\"")]
    [InlineData("ui/package.json", "\"license\": \"Apache-2.0\"")]
    [InlineData("site/package.json", "\"license\": \"Apache-2.0\"")]
    public void Distribution_surfaces_declare_the_apache_license(string file, string declaration)
    {
        File.ReadAllText(Path.Combine(RepoRoot(), file)).Should().Contain(declaration);
    }
}
