using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FluentAssertions;

namespace Xrmd.Repository.Tests;

/// <summary>
/// Self-test of eng/check-no-sdk.sh on prebuilt layouts (XRMD_CHECK_ARTIFACTS_DIR) and on release assets (<c>--artifacts</c>),
/// without publishing.
/// </summary>
public sealed class CheckNoSdkScriptTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
    private readonly string _dir = Directory.CreateTempSubdirectory("check-no-sdk").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Clean_layout_passes()
    {
        Layout();
        Run().ExitCode.Should().Be(0);
    }

    [Theory]
    [InlineData("Microsoft.Xrm.Sdk.dll")]
    [InlineData("Microsoft.Crm.Sdk.Proxy.dll")]
    [InlineData("Microsoft.PowerPlatform.Dataverse.Client.dll")]
    [InlineData("Microsoft.Xrm.Sdk.Workflow.dll")]
    [InlineData("Microsoft.SqlServer.Management.SqlParser.dll")]
    [InlineData("Microsoft.Data.SqlClient.SNI.dll")]
    public void Planted_sdk_assembly_fails_in_every_artifact(string name)
    {
        Layout(hostFile: name);
        Run().ExitCode.Should().Be(1, "a DLL in the host folder");

        Layout(nupkgEntry: "tools/net8.0/any/" + name);
        Run().ExitCode.Should().Be(1, "a DLL inside the tool package");

        Layout(xrmdBytes: Bundle(name));
        Run().ExitCode.Should().Be(1, "a DLL embedded in the single-file CLI");
    }

    // SqlClient names the native SNI library as a P/Invoke target, so its file name is in every Windows single-file CLI;
    // only the package id in the embedded deps.json shows that the native library itself ships (issue #15).
    private static string Bundle(string name) => name == "Microsoft.Data.SqlClient.SNI.dll"
        ? "deps.json \"Microsoft.Data.SqlClient.SNI.runtime/7.1.0\": { native } end"
        : "bundle manifest " + name + " end";

    [Fact]
    public void Sni_pinvoke_name_alone_passes()
    {
        Layout(xrmdBytes: "kernel32.dll\0Microsoft.Data.SqlClient.SNI.dll\0ntdll.dll");
        Run().ExitCode.Should().Be(0, "the P/Invoke name of SqlClient in the single-file CLI");

        Assets(xrmdExeBytes: "MZ kernel32.dll\0Microsoft.Data.SqlClient.SNI.dll\0ntdll.dll");
        var run = Run("--artifacts", _dir);
        run.ExitCode.Should().Be(0, run.Output);
    }

    [Fact]
    public void Missing_or_empty_artifacts_fail_closed()
    {
        Layout();
        File.Delete(Path.Combine(_dir, "cli", "xrmd"));
        Run().ExitCode.Should().Be(1, "no CLI executable");

        Layout(xrmdBytes: "");
        Run().ExitCode.Should().Be(1, "empty CLI executable");

        Layout();
        File.Delete(Path.Combine(_dir, "tool", "xrmd.1.0.0.nupkg"));
        Run().ExitCode.Should().Be(1, "no tool package");

        Layout();
        foreach (var f in Directory.GetFiles(Path.Combine(_dir, "host"))) File.Delete(f);
        Run().ExitCode.Should().Be(1, "empty host folder");
    }

    // ---- --tree <dir>: an exported image filesystem (docker export), looked at by file name ----

    [Fact]
    public void Clean_exported_image_tree_passes()
    {
        Tree("app/Xrmd.Host.dll");
        var run = Run("--tree", _dir);
        run.ExitCode.Should().Be(0, run.Output);
    }

    [Theory]
    [InlineData("Microsoft.Xrm.Sdk.dll")]
    [InlineData("Microsoft.Crm.Sdk.Proxy.dll")]
    [InlineData("Microsoft.PowerPlatform.Dataverse.Client.dll")]
    [InlineData("Microsoft.Xrm.Sdk.Workflow.dll")]
    [InlineData("Microsoft.SqlServer.Management.SqlParser.dll")]
    [InlineData("Microsoft.Data.SqlClient.SNI.dll")]
    public void Planted_sdk_assembly_fails_in_an_exported_image_tree(string name)
    {
        Tree("app/Xrmd.Host.dll", "app/" + name.ToLowerInvariant());
        Run("--tree", _dir).ExitCode.Should().Be(1);
    }

    [Fact]
    public void Empty_or_missing_exported_image_tree_fails_closed()
    {
        Run("--tree", _dir).ExitCode.Should().Be(1, "an empty tree was not checked");
        Run("--tree", Path.Combine(_dir, "nope")).ExitCode.Should().Be(1);
        Run("--tree").ExitCode.Should().Be(1);
    }

    private void Tree(params string[] files)
    {
        foreach (var f in files)
        {
            var path = Path.Combine(_dir, f);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    // ---- --artifacts <dir>: the files the release attaches (RID archives with the single-file xrmd, the tool package) ----

    [Fact]
    public void Clean_release_assets_pass()
    {
        Assets();
        var run = Run("--artifacts", _dir);
        run.ExitCode.Should().Be(0, run.Output);
        run.Output.Should().Contain("xrmd-linux-x64.tar.gz").And.Contain("xrmd-win-x64.zip").And.Contain("xrmd.1.0.0.nupkg");
    }

    [Theory]
    [InlineData("Microsoft.Xrm.Sdk.dll")]
    [InlineData("Microsoft.Crm.Sdk.Proxy.dll")]
    [InlineData("Microsoft.PowerPlatform.Dataverse.Client.dll")]
    [InlineData("Microsoft.Xrm.Sdk.Workflow.dll")]
    [InlineData("Microsoft.SqlServer.Management.SqlParser.dll")]
    [InlineData("Microsoft.Data.SqlClient.SNI.dll")]
    public void Planted_sdk_assembly_fails_in_every_release_asset(string name)
    {
        Assets(tarEntry: name);
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "a DLL inside a .tar.gz archive");

        Assets(zipEntry: name);
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "a DLL inside the .zip archive");

        Assets(nupkgEntry: "tools/net8.0/any/" + name);
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "a DLL inside the tool package");

        Assets(xrmdBytes: Bundle(name));
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "a DLL embedded in the single-file xrmd of a .tar.gz");

        Assets(xrmdExeBytes: Bundle(name));
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "a DLL embedded in the single-file xrmd.exe of the .zip");
    }

    [Fact]
    public void Release_assets_check_fails_closed()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.CreateDirectory(_dir);
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "an empty folder");

        File.WriteAllText(Path.Combine(_dir, "THIRD_PARTY_NOTICES.md"), "notices");
        File.WriteAllText(Path.Combine(_dir, "xrmd-cli-1.0.0.cdx.json"), "{}");
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "no archive and no package");

        Run("--artifacts", Path.Combine(_dir, "missing")).ExitCode.Should().Be(1, "no such folder");
        Run("--artifacts").ExitCode.Should().Be(1, "no folder given");

        Assets();
        File.Delete(Path.Combine(_dir, "xrmd.1.0.0.nupkg"));
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "no tool package");

        Assets();
        foreach (var f in Directory.GetFiles(_dir, "xrmd-*")) File.Delete(f);
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "no RID archive");

        Assets(xrmdBytes: null);
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "an archive without the xrmd executable");

        Assets();
        File.WriteAllBytes(Path.Combine(_dir, "xrmd-osx-x64.tar.gz"), "not a tar"u8.ToArray());
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "an unreadable archive");

        Assets();
        File.WriteAllText(Path.Combine(_dir, "Microsoft.Xrm.Sdk.dll"), "loose");
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "a loose file the scan cannot inspect");

        Assets();
        File.WriteAllText(Path.Combine(_dir, "xrmd-linux-arm64.tgz"), "other archive type");
        Run("--artifacts", _dir).ExitCode.Should().Be(1, "an archive type the scan does not open");
    }

    /// <summary>The release layout: two RID archives, the tool package, an SBOM and the notices (the release's assets/ folder).</summary>
    private void Assets(string? tarEntry = null, string? zipEntry = null, string? nupkgEntry = null,
        string? xrmdBytes = "ELF clean", string xrmdExeBytes = "MZ clean")
    {
        Directory.Delete(_dir, recursive: true);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "THIRD_PARTY_NOTICES.md"), "notices");
        File.WriteAllText(Path.Combine(_dir, "xrmd-cli-1.0.0.cdx.json"), "{}");

        using (var file = File.Create(Path.Combine(_dir, "xrmd-linux-x64.tar.gz")))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip))
        {
            if (xrmdBytes is not null) TarFile(tar, "xrmd", xrmdBytes);
            TarFile(tar, "THIRD_PARTY_NOTICES.md", "notices");
            if (tarEntry is not null) TarFile(tar, tarEntry, "x");
        }

        using (var zip = ZipFile.Open(Path.Combine(_dir, "xrmd-win-x64.zip"), ZipArchiveMode.Create))
        {
            ZipText(zip, "xrmd.exe", xrmdExeBytes);
            ZipText(zip, "THIRD_PARTY_NOTICES.md", "notices");
            if (zipEntry is not null) ZipText(zip, zipEntry, "x");
        }

        using (var nupkg = ZipFile.Open(Path.Combine(_dir, "xrmd.1.0.0.nupkg"), ZipArchiveMode.Create))
        {
            ZipText(nupkg, "tools/net8.0/any/xrmd.dll", "x");
            if (nupkgEntry is not null) ZipText(nupkg, nupkgEntry, "x");
        }
    }

    private static void TarFile(TarWriter tar, string name, string content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(new UTF8Encoding(false).GetBytes(content)) };
        tar.WriteEntry(entry);
    }

    private static void ZipText(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(new UTF8Encoding(false).GetBytes(content));
    }

    private void Layout(string? hostFile = null, string? nupkgEntry = null, string xrmdBytes = "ELF clean")
    {
        Directory.Delete(_dir, recursive: true);
        Directory.CreateDirectory(Path.Combine(_dir, "host"));
        Directory.CreateDirectory(Path.Combine(_dir, "cli"));
        Directory.CreateDirectory(Path.Combine(_dir, "tool"));
        File.WriteAllText(Path.Combine(_dir, "host", "Xrmd.Host.dll"), "x");
        if (hostFile is not null) File.WriteAllText(Path.Combine(_dir, "host", hostFile), "x");
        File.WriteAllText(Path.Combine(_dir, "cli", "xrmd"), xrmdBytes, new UTF8Encoding(false));
        using var zip = ZipFile.Open(Path.Combine(_dir, "tool", "xrmd.1.0.0.nupkg"), ZipArchiveMode.Create);
        zip.CreateEntry("tools/net8.0/any/xrmd.dll");
        if (nupkgEntry is not null) zip.CreateEntry(nupkgEntry);
    }

    /// <summary>Runs the script: on the prebuilt layout (XRMD_CHECK_ARTIFACTS_DIR) without arguments, else with them only.</summary>
    private (int ExitCode, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = RepoRoot };
        psi.ArgumentList.Add(Path.Combine(RepoRoot, "eng", "check-no-sdk.sh"));
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (args.Length == 0) psi.Environment["XRMD_CHECK_ARTIFACTS_DIR"] = _dir;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + err.Result);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
