using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Xrmd.Abstractions.Sdk;
using Xrmd.Cli.Sdk;

namespace Xrmd.TestSupport;

/// <summary>
/// The host and the CLI as customers get them (Plan 12): published into a temporary folder, without the Microsoft SDK, and
/// run as processes. Linked into Xrmd.Integration.Tests and Xrmd.Cli.Tests. Each publish runs once per test process; the
/// folders are deleted when it exits. Publishes of both test processes are serialized by a lock file, because
/// <c>dotnet publish</c> of one project can restore the projects the other reads.
/// <para>
/// A test host killed by the runner never runs its exit handler, so its folder (a published host is several hundred MB) would stay
/// behind (plan 2.14, Task 9: 33 of them filled the disk). Each folder's owner therefore keeps <see cref="OwnerLockFile"/> open without
/// sharing for its whole life; the kernel releases that lock when the owner dies, even when it is killed. Each test process sweeps the
/// folders whose lock it can take itself (<see cref="SweepAbandonedFolders"/>), so the test processes running at the same time (CI
/// runs Xrmd.Cli.Tests and Xrmd.Integration.Tests in parallel) never delete each other's folders. Fixtures delete their own SDK
/// folders with <see cref="DeleteFolder"/> when they are disposed.
/// </para>
/// </summary>
public static class PublishedApps
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "xrmd-published-" + Guid.NewGuid().ToString("N"));
    private static readonly Lazy<Task<string>> Host = new(() => PublishAsync("host", "src/Xrmd.Host/Xrmd.Host.csproj",
        "-c", Configuration!, "--no-build"));
    private static readonly Lazy<Task<string>> SingleFileCli = new(() => PublishAsync("cli", "src/Xrmd.Cli/Xrmd.Cli.csproj",
        "-c", "Release", "-r", RuntimeInformation.RuntimeIdentifier, "--self-contained", "-p:PublishSingleFile=true"));

    /// <summary>How old a folder without <see cref="OwnerLockFile"/> must be before <see cref="SweepAbandonedFolders"/> removes it.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    /// <summary>The file of a folder that its owner process holds open, unshared, for its whole life.</summary>
    public const string OwnerLockFile = "owner.lock";

    /// <summary>This process's hold on <see cref="Root"/>'s <see cref="OwnerLockFile"/>: open until the process ends.</summary>
    private static readonly FileStream? OwnerLock;

    static PublishedApps()
    {
        // The sweep runs before this process's own folder exists, so it cannot see it.
        SweepAbandonedFolders(Path.GetTempPath());
        try
        {
            Directory.CreateDirectory(Root);
            OwnerLock = new FileStream(Path.Combine(Root, OwnerLockFile), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteRoot();
    }

    /// <summary>
    /// Deletes <see cref="Root"/> at exit, <see cref="OwnerLockFile"/> last: when a deletion fails (a host process still writing in
    /// its files folder), the lock file stays, so another test process sweeps the rest once this one is gone. Three attempts.
    /// </summary>
    private static void DeleteRoot()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (var entry in new DirectoryInfo(Root).EnumerateFileSystemInfos())
                {
                    if (entry.Name == OwnerLockFile) continue;
                    if (entry is DirectoryInfo directory) directory.Delete(recursive: true);
                    else entry.Delete();
                }
                OwnerLock?.Dispose();
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// Removes the <c>xrmd-published-*</c> folders of <paramref name="parent"/> whose owner is gone (a killed test host never ran its
    /// exit handler): those whose <see cref="OwnerLockFile"/> this process can open without sharing, which it holds while it deletes
    /// the folder; and those without the file (older folders, or an owner killed before it opened it) last written more than
    /// <see cref="StaleAfter"/> ago. A folder whose lock another process holds is never touched. Best effort: a folder in use or not
    /// ours to delete is skipped.
    /// </summary>
    public static void SweepAbandonedFolders(string parent)
    {
        try
        {
            var cutoff = DateTime.UtcNow - StaleAfter;
            foreach (var folder in new DirectoryInfo(parent).EnumerateDirectories("xrmd-published-*"))
            {
                try
                {
                    var lockPath = Path.Combine(folder.FullName, OwnerLockFile);
                    if (!File.Exists(lockPath))
                    {
                        if (folder.LastWriteTimeUtc < cutoff) folder.Delete(recursive: true);
                        continue;
                    }
                    FileStream owner;
                    try
                    {
                        owner = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (IOException ex) when (PublishLock.IsHeldByAnother(ex))
                    {
                        continue; // its owner runs
                    }
                    using (owner)
                    {
                        foreach (var entry in folder.EnumerateFileSystemInfos())
                        {
                            if (entry.Name == OwnerLockFile) continue;
                            if (entry is DirectoryInfo directory) directory.Delete(recursive: true);
                            else entry.Delete();
                        }
                    }
                    folder.Delete(recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    /// <summary>
    /// Deletes the folder <see cref="NewFolder"/> made that holds <paramref name="path"/> (an SDK folder of
    /// <see cref="InstalledSdkAsync"/> is one level below it). Best effort; a path outside <see cref="Root"/> is left alone.
    /// </summary>
    public static void DeleteFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.Ordinal)) return;
        var top = full[root.Length..].Split(Path.DirectorySeparatorChar)[0];
        if (top.Length == 0 || top is "host" or "cli" || top == OwnerLockFile) return;
        try { Directory.Delete(Path.Combine(root, top), recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The repository root (the folder of <c>xrmverse.sln</c>).</summary>
    public static string RepositoryRoot { get; } = FindRoot();

    /// <summary>The build configuration of the running tests (the <c>bin/&lt;Configuration&gt;/</c> they run from).</summary>
    public static string Configuration { get; } =
        new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Debug";

    /// <summary>The cached SDK package (the test projects reference it, so it is in the NuGet cache).</summary>
    public static string Nupkg { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages
            ? packages
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
        "microsoft.powerplatform.dataverse.client", SdkPackage.Version, $"microsoft.powerplatform.dataverse.client.{SdkPackage.Version}.nupkg");

    /// <summary>
    /// The cached workflow SDK package (plan 2.14, W18): the restore of <c>Xrmd.TestWorkflowActivities</c>, which
    /// <c>tests/TestPlugins.props</c> references, puts it in the NuGet cache.
    /// </summary>
    public static string WorkflowNupkg { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages
            ? packages
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
        "microsoft.crmsdk.workflow", SdkPackage.Workflow.Version, $"microsoft.crmsdk.workflow.{SdkPackage.Workflow.Version}.nupkg");

    /// <summary>The folder of the published (framework-dependent) host, with <c>Xrmd.Host.dll</c> and its own deps.json.</summary>
    public static Task<string> HostAsync() => Host.Value;

    /// <summary>The published single-file, self-contained <c>xrmd</c> executable for the running platform's RID.</summary>
    public static async Task<string> SingleFileCliAsync() =>
        Path.Combine(await SingleFileCli.Value, OperatingSystem.IsWindows() ? "xrmd.exe" : "xrmd");

    /// <summary>A new empty folder under the published apps' temporary root.</summary>
    public static string NewFolder(string name)
    {
        var path = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A new SDK folder filled by <see cref="SdkInstaller"/> from the cached package; with <paramref name="withWorkflow"/>, also
    /// from the cached workflow SDK package.
    /// </summary>
    public static async Task<string> InstalledSdkAsync(bool withWorkflow = false)
    {
        var folder = Path.Combine(NewFolder("sdk"), "sdk");
        await using var package = File.OpenRead(Nupkg);
        await using var workflow = withWorkflow ? File.OpenRead(WorkflowNupkg) : null;
        await SdkInstaller.InstallAsync(package, workflow, folder, AcceptanceKind.Flag);
        return folder;
    }

    /// <summary>The dotnet executable that runs the tests.</summary>
    public static string Dotnet() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path && File.Exists(path) ? path : "dotnet";

    /// <summary>
    /// A <see cref="ProcessStartInfo"/> that runs the published host (<c>dotnet Xrmd.Host.dll</c>, its own deps.json) with none of
    /// the test runner's <c>ASPNETCORE_*</c>, <c>DOTNET_*</c> or <c>Section__Key</c> variables, and <c>XRMD_SDK_DIR</c> =
    /// <paramref name="sdkFolder"/>.
    /// </summary>
    public static async Task<ProcessStartInfo> HostStartInfoAsync(string sdkFolder)
    {
        var folder = await HostAsync();
        var psi = new ProcessStartInfo(Dotnet())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = folder,
        };
        psi.ArgumentList.Add(Path.Combine(folder, "Xrmd.Host.dll"));
        foreach (var key in psi.Environment.Keys.ToList())
            if (key.StartsWith("ASPNETCORE_", StringComparison.Ordinal) || key.StartsWith("DOTNET_", StringComparison.Ordinal)
                || key.Contains("__", StringComparison.Ordinal) || key == SdkFolder.DirectoryVariable)
                psi.Environment.Remove(key);
        psi.Environment[SdkFolder.DirectoryVariable] = sdkFolder;
        return psi;
    }

    private static async Task<string> PublishAsync(string name, string project, params string[] options)
    {
        var output = Path.Combine(Root, name);
        using (await PublishLock.AcquireAsync())
        {
            var psi = new ProcessStartInfo(Dotnet())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryRoot,
            };
            foreach (var argument in new[] { "publish", Path.Combine(RepositoryRoot, project), "--nologo", "-v", "q", "-o", output }.Concat(options))
                psi.ArgumentList.Add(argument);
            // The test runner's MSBuild variables would leak into the nested build.
            foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
                psi.Environment.Remove(key);
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"dotnet publish {project} {string.Join(' ', options)} failed with exit code {process.ExitCode}:\n{Tail(await stdout + await stderr)}");
        }

        foreach (var assembly in SdkPackage.Assemblies.Append(SdkPackage.Workflow.AssemblyFileName))
            if (File.Exists(Path.Combine(output, assembly)))
                throw new InvalidOperationException($"The published {name} contains {assembly}: it must not ship the Microsoft SDK.");
        return output;
    }

    /// <summary>The last lines of the publish output (where MSBuild puts its errors).</summary>
    private static string Tail(string output, int lines = 60)
    {
        var all = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) return dir.FullName;
        throw new InvalidOperationException("xrmverse.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>An exclusive lock file shared by every test process of the machine.</summary>
    private sealed class PublishLock(FileStream stream) : IDisposable
    {
        private static readonly string LockPath = Path.Combine(Path.GetTempPath(), "xrmd-tests-publish.lock");

        public static async Task<PublishLock> AcquireAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
            while (true)
            {
                try
                {
                    return new PublishLock(new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException ex) when (IsHeldByAnother(ex))
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new TimeoutException($"The publish lock {LockPath} was still held by another test process after 15 minutes: {ex.Message}", ex);
                    await Task.Delay(500);
                }
            }
        }

        /// <summary>
        /// A sharing or lock violation, meaning another process holds the file: on Windows ERROR_SHARING_VIOLATION (32) or
        /// ERROR_LOCK_VIOLATION (33) in the HRESULT; on Unix .NET throws an IOException whose HResult is the errno of the
        /// failed advisory lock, EWOULDBLOCK (11 on Linux, 35 on macOS). Any other I/O error (a missing folder, no
        /// permission) fails at once instead of being retried for 15 minutes.
        /// </summary>
        public static bool IsHeldByAnother(IOException ex) =>
            OperatingSystem.IsWindows() ? (ex.HResult & 0xFFFF) is 32 or 33 : ex.HResult is 11 or 35;

        public void Dispose() => stream.Dispose();
    }
}

/// <summary>A started process whose output (stdout and stderr, interleaved) is collected.</summary>
public sealed class RunningProcess : IDisposable
{
    private readonly StringBuilder _output = new();

    public RunningProcess(ProcessStartInfo psi, Action<string>? echo = null)
    {
        Process = Process.Start(psi)!;
        DataReceivedEventHandler append = (_, e) =>
        {
            if (e.Data is null) return;
            lock (_output) _output.AppendLine(e.Data);
            echo?.Invoke(e.Data);
        };
        Process.OutputDataReceived += append;
        Process.ErrorDataReceived += append;
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
    }

    public Process Process { get; }

    public string Output
    {
        get { lock (_output) return _output.ToString(); }
    }

    /// <summary>True when the process exited within <paramref name="timeout"/> (its output is then complete).</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await Process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        Process.Dispose();
    }
}
