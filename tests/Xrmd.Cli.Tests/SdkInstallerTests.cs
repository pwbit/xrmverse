using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Cli.Sdk;

namespace Xrmd.Cli.Tests;

/// <summary>
/// Plan 2.14, ruling W18: <c>xrmd sdk install</c> installs the Microsoft workflow SDK (<c>Microsoft.CrmSdk.Workflow</c>) next to
/// the Dataverse SDK after the one existing acceptance, checks its pinned hash before anything is written, and records it in
/// <c>sdk.json</c> with three optional fields (old records stay valid).
/// </summary>
public sealed class SdkInstallerTests : IDisposable
{
    /// <summary>The cached workflow SDK package (the test activities reference it, so their restore puts it in the NuGet cache).</summary>
    internal static readonly string WorkflowNupkg = Path.Combine(
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages
            ? packages
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
        "microsoft.crmsdk.workflow", SdkPackage.Workflow.Version, $"microsoft.crmsdk.workflow.{SdkPackage.Workflow.Version}.nupkg");

    private readonly string _parent = Path.Combine(Path.GetTempPath(), "sdk-installer-" + Guid.NewGuid().ToString("N"));

    private string Target => Path.Combine(_parent, "sdk");

    public void Dispose()
    {
        if (Directory.Exists(_parent)) Directory.Delete(_parent, recursive: true);
    }

    /// <summary>A package built here with one entry per name, and its SHA-512 (passed as the pinned hash).</summary>
    private static (MemoryStream Package, string Sha512) Package(params string[] entries)
    {
        var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
                using (var w = zip.CreateEntry(entry).Open()) w.Write(System.Text.Encoding.UTF8.GetBytes("content of " + entry));
        package.Position = 0;
        return (package, Convert.ToBase64String(SHA512.HashData(package.ToArray())));
    }

    private static (MemoryStream Package, string Sha512) CorePackage() =>
        Package(SdkPackage.Assemblies.Select(a => "lib/net10.0/" + a).ToArray());

    private static (MemoryStream Package, string Sha512) WorkflowPackage() => Package(SdkPackage.Workflow.Entry, "lib/net462/Other.xml");

    [Fact]
    public async Task Installs_both_packages_after_one_acceptance()
    {
        // The installer: both packages in one folder, one record.
        var (core, coreHash) = CorePackage();
        var (workflow, workflowHash) = WorkflowPackage();

        await SdkInstaller.InstallAsync(core, workflow, Target, AcceptanceKind.Flag, coreHash, workflowHash);

        Directory.GetFiles(Target).Select(Path.GetFileName).Should().BeEquivalentTo(
            SdkPackage.Assemblies.Append(SdkPackage.Workflow.AssemblyFileName).Append(SdkFolder.RecordFileName));
        File.ReadAllText(Path.Combine(Target, SdkPackage.Workflow.AssemblyFileName)).Should().Be("content of " + SdkPackage.Workflow.Entry);
        var record = SdkFolder.TryRead(Target)!;
        record.Package.Should().Be(SdkPackage.Id);
        record.WorkflowPackage.Should().Be(SdkPackage.Workflow.Id);
        record.WorkflowVersion.Should().Be(SdkPackage.Workflow.Version);
        record.WorkflowSha512.Should().Be(SdkPackage.Workflow.Sha512Base64);
        Directory.GetFileSystemEntries(_parent).Should().ContainSingle("no staging folder is left");

        // The command: one prompt, then both pinned packages are downloaded.
        var coreBytes = await File.ReadAllBytesAsync(SdkCommandsTests.Nupkg);
        var workflowBytes = await File.ReadAllBytesAsync(WorkflowNupkg);
        using var cli = new TestCli(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsoluteUri == SdkPackage.Workflow.DownloadUrl ? workflowBytes : coreBytes),
        }) { IsInputTty = true, Input = "y\n" };

        (await cli.RunAsync("sdk", "install")).Should().Be(0, cli.Err.ToString());

        Regex.Matches(cli.Err.ToString(), Regex.Escape("[y/N]")).Should().ContainSingle("one acceptance covers both packages");
        cli.Err.ToString().Should().Contain(SdkPackage.LicenseUrl);
        ((FakeHandler)cli.Handler).Requests.Select(r => r.RequestUri!.AbsoluteUri).Should().BeEquivalentTo(
            [SdkPackage.DownloadUrl, SdkPackage.Workflow.DownloadUrl]);
        var sdk = cli.Vars[SdkFolder.DirectoryVariable]!;
        File.Exists(Path.Combine(sdk, SdkPackage.Workflow.AssemblyFileName)).Should().BeTrue();
        var installed = SdkFolder.TryRead(sdk)!;
        installed.AcceptedBy.Should().Be("prompt");
        installed.WorkflowVersion.Should().Be(SdkPackage.Workflow.Version);
        installed.WorkflowSha512.Should().Be(SdkPackage.Workflow.Sha512Base64);
    }

    [Fact]
    public async Task Workflow_package_hash_mismatch_installs_nothing()
    {
        // A folder installed before is kept as it was.
        var (first, firstHash) = CorePackage();
        await SdkInstaller.InstallAsync(first, null, Target, AcceptanceKind.Flag, firstHash, SdkPackage.Workflow.Sha512Base64);
        var before = Directory.GetFiles(Target).Select(f => (Path.GetFileName(f), File.ReadAllText(f))).ToList();

        var (core, coreHash) = CorePackage();
        var (workflow, _) = WorkflowPackage();
        var act = () => SdkInstaller.InstallAsync(core, workflow, Target, AcceptanceKind.Flag, coreHash, SdkPackage.Workflow.Sha512Base64);

        (await act.Should().ThrowAsync<CliException>()).Which.Message.Should()
            .Contain(SdkPackage.Workflow.Id).And.Contain(SdkPackage.Workflow.Version).And.Contain("does not match")
            .And.Contain(SdkPackage.Workflow.Sha512Base64).And.Contain("Nothing was installed");
        Directory.GetFiles(Target).Select(f => (Path.GetFileName(f), File.ReadAllText(f))).Should().Equal(before);
        File.Exists(Path.Combine(Target, SdkPackage.Workflow.AssemblyFileName)).Should().BeFalse();
        Directory.GetFileSystemEntries(_parent).Should().ContainSingle("no staging folder is left");

        // The same through the command, into an empty folder: nothing at all is written.
        using var cli = new TestCli();
        var bad = Path.Combine(_parent, "bad.nupkg");
        await File.WriteAllBytesAsync(bad, [1, 2, 3]);
        (await cli.RunAsync("sdk", "install", "--from", SdkCommandsTests.Nupkg, "--from-workflow", bad, "--accept-microsoft-license"))
            .Should().Be(1);
        cli.Err.ToString().Should().Contain(SdkPackage.Workflow.Id).And.Contain("does not match").And.Contain("Nothing was installed");
        (Directory.Exists(cli.ConfigDir) ? Directory.GetFileSystemEntries(cli.ConfigDir) : []).Should().BeEmpty();
    }

    [Fact]
    public async Task Workflow_package_without_the_assembly_installs_nothing()
    {
        var (core, coreHash) = CorePackage();
        var (workflow, workflowHash) = Package("lib/net45/" + SdkPackage.Workflow.AssemblyFileName);
        var act = () => SdkInstaller.InstallAsync(core, workflow, Target, AcceptanceKind.Flag, coreHash, workflowHash);

        (await act.Should().ThrowAsync<CliException>()).Which.Message.Should()
            .Contain(SdkPackage.Workflow.Entry).And.Contain("Nothing was installed");
        Directory.Exists(Target).Should().BeFalse();
        (Directory.Exists(_parent) ? Directory.GetFileSystemEntries(_parent) : []).Should().BeEmpty();
    }

    [Fact]
    public void Old_record_without_workflow_fields_reads()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, SdkFolder.RecordFileName), $$"""
            {
              "package": "{{SdkPackage.Id}}",
              "version": "{{SdkPackage.Version}}",
              "sha512": "{{SdkPackage.Sha512Base64}}",
              "licenseUrl": "{{SdkPackage.LicenseUrl}}",
              "acceptedAt": "2026-11-08T12:00:00+00:00",
              "acceptedBy": "flag"
            }
            """);

        var record = SdkFolder.TryRead(Target);

        record.Should().NotBeNull("a record written before the workflow SDK existed stays valid");
        record!.Version.Should().Be(SdkPackage.Version);
        record.WorkflowPackage.Should().BeNull();
        record.WorkflowVersion.Should().BeNull();
        record.WorkflowSha512.Should().BeNull();
    }

    [Fact]
    public async Task From_workflow_offline()
    {
        using var cli = new TestCli();

        var code = await cli.RunAsync("sdk", "install", "--from", SdkCommandsTests.Nupkg, "--from-workflow", WorkflowNupkg,
            "--accept-microsoft-license");

        code.Should().Be(0, cli.Err.ToString());
        ((FakeHandler)cli.Handler).Requests.Should().BeEmpty("both packages come from local files");
        var sdk = cli.Vars[SdkFolder.DirectoryVariable]!;
        Directory.GetFiles(sdk).Select(Path.GetFileName).Should().BeEquivalentTo(
            SdkPackage.Assemblies.Append(SdkPackage.Workflow.AssemblyFileName).Append(SdkFolder.RecordFileName));
        var record = SdkFolder.TryRead(sdk)!;
        record.WorkflowPackage.Should().Be(SdkPackage.Workflow.Id);
        record.WorkflowVersion.Should().Be(SdkPackage.Workflow.Version);
        record.WorkflowSha512.Should().Be(SdkPackage.Workflow.Sha512Base64);
        record.AcceptedBy.Should().Be("flag");

        // A missing file is a usage error, before anything else.
        (await cli.RunAsync("sdk", "install", "--from-workflow", Path.Combine(_parent, "none.nupkg"), "--accept-microsoft-license"))
            .Should().Be(2);
        ((FakeHandler)cli.Handler).Requests.Should().BeEmpty();

        // --from alone stays offline and installs the Dataverse SDK only (the workflow SDK is optional).
        using var coreOnly = new TestCli();
        (await coreOnly.RunAsync("sdk", "install", "--from", SdkCommandsTests.Nupkg, "--accept-microsoft-license")).Should().Be(0, coreOnly.Err.ToString());
        ((FakeHandler)coreOnly.Handler).Requests.Should().BeEmpty();
        SdkFolder.TryRead(coreOnly.Vars[SdkFolder.DirectoryVariable]!)!.WorkflowVersion.Should().BeNull();
    }
}
