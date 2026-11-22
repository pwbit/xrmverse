using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Abstractions.Sdk;

namespace Xrmd.Repository.Tests;

/// <summary>Plan 12, Task 1: the pinned SDK package and its compile-only references.</summary>
public sealed class SdkPackageTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Pinned_constants_are_the_ones_of_the_plan()
    {
        SdkPackage.Id.Should().Be("Microsoft.PowerPlatform.Dataverse.Client");
        SdkPackage.Version.Should().Be("1.2.27");
        SdkPackage.Sha512Base64.Should().Be("20F8F6UyuLk+awBMheqKkcDFfoZmT0tBijhJHm39zW2lq2LM9PKs7/xk/SxKB0BjZi96p+y1jD4aGWqlAcJ/XA==");
        SdkPackage.LicenseUrl.Should().Be("https://go.microsoft.com/fwlink/?linkid=2108407");
        SdkPackage.DownloadUrl.Should().Be("https://api.nuget.org/v3-flatcontainer/microsoft.powerplatform.dataverse.client/1.2.27/microsoft.powerplatform.dataverse.client.1.2.27.nupkg");
        SdkPackage.Assemblies.Should().Equal("Microsoft.Xrm.Sdk.dll", "Microsoft.Crm.Sdk.Proxy.dll", "Microsoft.PowerPlatform.Dataverse.Client.dll");
        SdkPackage.MissingMessage("/x/y").Should().Be(
            "The Microsoft SDK is not installed (looked in /x/y). Run 'xrmd sdk install' to download it from nuget.org after accepting Microsoft's license terms.");
    }

    [Fact]
    public void Pinned_workflow_constants_are_the_ones_of_the_plan()
    {
        // Plan 2.14, Global Constraints and ruling W18 (issue #45): the hash was read from the nuget.org catalog and checked
        // on the downloaded bytes.
        SdkPackage.Workflow.Id.Should().Be("Microsoft.CrmSdk.Workflow");
        SdkPackage.Workflow.Version.Should().Be("9.0.2.60");
        SdkPackage.Workflow.Sha512Base64.Should().Be("RHB5/GGSncAjfjIkrw7krJtPdILjH5NejRzN3/5DFVVH/PpnfxWvevb+J5+PEbAZaspKUZs3YoIQTt58XQxchQ==");
        SdkPackage.Workflow.DownloadUrl.Should().Be("https://api.nuget.org/v3-flatcontainer/microsoft.crmsdk.workflow/9.0.2.60/microsoft.crmsdk.workflow.9.0.2.60.nupkg");
        SdkPackage.Workflow.Entry.Should().Be("lib/net462/Microsoft.Xrm.Sdk.Workflow.dll");
        SdkPackage.Workflow.AssemblyFileName.Should().Be("Microsoft.Xrm.Sdk.Workflow.dll");
        SdkPackage.Workflow.LicenseUrl.Should().Be("https://go.microsoft.com/fwlink/?linkid=2108407").And.Be(SdkPackage.LicenseUrl);
        SdkPackage.Workflow.FileVersion.Should().Be("9.2.49.18751");
    }

    [Fact]
    public void Pinned_workflow_file_version_is_the_one_of_the_package()
    {
        // The test activities restore the pinned package, so the NuGet cache has it whenever the solution is built.
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages
            ? packages
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var dll = Path.Combine(cache, SdkPackage.Workflow.Id.ToLowerInvariant(), SdkPackage.Workflow.Version, SdkPackage.Workflow.Entry);

        File.Exists(dll).Should().BeTrue(dll);
        System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).FileVersion.Should().Be(SdkPackage.Workflow.FileVersion);
    }

    [Fact]
    public void Pinned_workflow_version_matches_the_test_activities_reference()
    {
        var reference = XDocument.Load(Path.Combine(RepoRoot, "tests/Xrmd.TestWorkflowActivities/Xrmd.TestWorkflowActivities.csproj"))
            .Descendants("PackageReference").Single(e => (string?)e.Attribute("Include") == SdkPackage.Workflow.Id);
        reference.Attribute("Version")!.Value.Should().Be(SdkPackage.Workflow.Version);
    }

    [Fact]
    public void Pinned_version_matches_the_package_reference()
    {
        foreach (var file in new[] { "src/Xrmd.Abstractions/Xrmd.Abstractions.csproj", "src/Xrmd.Schema/Xrmd.Schema.csproj", "tests/Directory.Build.props" })
        {
            Reference(file).Attribute("Version")!.Value.Should().Be(SdkPackage.Version, file);
        }
    }

    [Fact]
    public void Abstractions_and_schema_reference_the_sdk_compile_only()
    {
        foreach (var file in new[] { "src/Xrmd.Abstractions/Xrmd.Abstractions.csproj", "src/Xrmd.Schema/Xrmd.Schema.csproj" })
        {
            Reference(file).Attribute("ExcludeAssets")!.Value.Should().Be("runtime", file);
        }

        Reference("tests/Directory.Build.props").Attribute("ExcludeAssets").Should().BeNull("tests need the SDK at runtime");
    }

    private static XElement Reference(string relativePath) =>
        XDocument.Load(Path.Combine(RepoRoot, relativePath)).Descendants("PackageReference")
            .Single(e => (string?)e.Attribute("Include") == SdkPackage.Id);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
