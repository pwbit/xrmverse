using System.Diagnostics;
using FluentAssertions;
using Xrmd.TestSupport;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.14, Task 9 (R6, review C1): a <c>xrmd-published-*</c> folder is swept only when no process holds its owner lock. Its owner
/// keeps <see cref="PublishedApps.OwnerLockFile"/> open without sharing for its whole life, and the kernel drops that lock when the
/// owner dies, even when it is killed; a sweeper that can open the file itself knows the owner is gone.
/// </summary>
public sealed class PublishedAppsSweepTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "xrmd-sweep-test-" + Guid.NewGuid().ToString("N"));

    public PublishedAppsSweepTests() => Directory.CreateDirectory(_parent);

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); } catch (IOException) { }
    }

    private string Folder(string name, bool withLockFile)
    {
        var folder = Path.Combine(_parent, "xrmd-published-" + name);
        Directory.CreateDirectory(Path.Combine(folder, "host"));
        File.WriteAllText(Path.Combine(folder, "host", "Xrmd.Host.dll"), "published");
        if (withLockFile) File.WriteAllText(Path.Combine(folder, PublishedApps.OwnerLockFile), "");
        return folder;
    }

    [Fact]
    public async Task Folder_whose_owner_process_holds_its_lock_survives_the_sweep_and_is_swept_once_the_owner_exits()
    {
        // The flock(1) tool takes the same advisory lock as .NET's FileShare.None on Linux (CI runs on Linux).
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/flock")) return;
        var held = Folder("held", withLockFile: true);
        var free = Folder("free", withLockFile: true);
        var legacy = Folder("legacy", withLockFile: false); // no lock file, new: kept by the age rule

        var psi = new ProcessStartInfo("/usr/bin/flock") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in new[] { "-x", Path.Combine(held, PublishedApps.OwnerLockFile), "-c", "echo locked; exec sleep 60" })
            psi.ArgumentList.Add(argument);
        using var owner = Process.Start(psi)!;
        try
        {
            (await owner.StandardOutput.ReadLineAsync()).Should().Be("locked");

            PublishedApps.SweepAbandonedFolders(_parent);

            Directory.Exists(held).Should().BeTrue("its owner, another process, still holds the lock");
            Directory.Exists(free).Should().BeFalse("no process holds its lock: its owner is gone");
            Directory.Exists(legacy).Should().BeTrue("a folder without a lock file is swept only after six hours");
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync();
        }

        // flock(1) runs the command in a child that holds the lock too: the kernel releases it once the last of them is gone, which
        // can come a moment after flock itself is reaped.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            PublishedApps.SweepAbandonedFolders(_parent);
            if (!Directory.Exists(held)) break;
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        Directory.Exists(held).Should().BeFalse("its owner was killed: the kernel released the lock");
        Directory.Exists(legacy).Should().BeTrue();
    }

    [Fact]
    public void Folder_whose_lock_this_process_holds_survives_the_sweep()
    {
        var held = Folder("self", withLockFile: true);
        using (new FileStream(Path.Combine(held, PublishedApps.OwnerLockFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            PublishedApps.SweepAbandonedFolders(_parent);
            Directory.Exists(held).Should().BeTrue();
        }

        PublishedApps.SweepAbandonedFolders(_parent);
        Directory.Exists(held).Should().BeFalse();
    }
}
