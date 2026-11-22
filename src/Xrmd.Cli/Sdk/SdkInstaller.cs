using System.IO.Compression;
using System.Security.Cryptography;
using Xrmd.Abstractions.Sdk;

namespace Xrmd.Cli.Sdk;

/// <summary>How the user accepted Microsoft's license terms; recorded in <c>sdk.json</c> as "flag" or "prompt".</summary>
public enum AcceptanceKind
{
    Flag,
    Prompt,
}

/// <summary>
/// Installs the pinned Microsoft SDK assemblies from a .nupkg, and optionally the pinned workflow SDK
/// (<see cref="SdkPackage.Workflow"/>, plan 2.14 W18) from a second one, into one folder. The caller has already obtained the
/// user's acceptance (one acceptance covers both packages: they share Microsoft's license terms). Both package hashes are
/// checked on the bytes before anything is extracted; the files are extracted next to the target and moved into place, so a
/// failure leaves no partial folder and keeps the previous one.
/// </summary>
public static class SdkInstaller
{
    private const string LibFolder = "lib/net10.0/";

    public static Task InstallAsync(Stream nupkg, string target, AcceptanceKind how, CancellationToken cancellationToken = default,
        TimeProvider? clock = null) =>
        InstallAsync(nupkg, null, target, how, cancellationToken, clock);

    /// <param name="nupkg">The Dataverse SDK package (<see cref="SdkPackage.Id"/>).</param>
    /// <param name="workflowNupkg">The workflow SDK package (<see cref="SdkPackage.Workflow.Id"/>); null installs without it.</param>
    /// <param name="target">The SDK folder.</param>
    /// <param name="how">How the user accepted Microsoft's license terms.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="clock">The clock of the acceptance date.</param>
    public static Task InstallAsync(Stream nupkg, Stream? workflowNupkg, string target, AcceptanceKind how,
        CancellationToken cancellationToken = default, TimeProvider? clock = null) =>
        InstallAsync(nupkg, workflowNupkg, target, how, SdkPackage.Sha512Base64, SdkPackage.Workflow.Sha512Base64, cancellationToken, clock);

    /// <summary>The pinned hash is a parameter so that a test can reach the layout check with a package it built itself.</summary>
    internal static Task InstallAsync(Stream nupkg, string target, AcceptanceKind how, string expectedSha512Base64,
        CancellationToken cancellationToken = default, TimeProvider? clock = null) =>
        InstallAsync(nupkg, null, target, how, expectedSha512Base64, SdkPackage.Workflow.Sha512Base64, cancellationToken, clock);

    /// <summary>The pinned hashes are parameters so that a test can reach the layout checks with packages it built itself.</summary>
    internal static async Task InstallAsync(Stream nupkg, Stream? workflowNupkg, string target, AcceptanceKind how, string expectedSha512Base64,
        string expectedWorkflowSha512Base64, CancellationToken cancellationToken = default, TimeProvider? clock = null)
    {
        var bytes = await ReadVerifiedAsync(nupkg, expectedSha512Base64, SdkPackage.Id, SdkPackage.Version, cancellationToken);
        var workflowBytes = workflowNupkg is null
            ? null
            : await ReadVerifiedAsync(workflowNupkg, expectedWorkflowSha512Base64, SdkPackage.Workflow.Id, SdkPackage.Workflow.Version, cancellationToken);

        target = Path.GetFullPath(target);
        var parent = Path.GetDirectoryName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? throw new CliException(ExitCodes.Usage, $"'{target}' is not a valid SDK folder.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && SdkFolder.TryRead(target) is null)
            throw new CliException(ExitCodes.Unexpected,
                $"'{target}' exists, is not empty and is not an SDK folder (no valid {SdkFolder.RecordFileName}); refusing to replace it. Choose another folder or empty it first. Nothing was changed.");
        Directory.CreateDirectory(parent);
        var name = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var staging = Path.Combine(parent, $"{name}.tmp-{Guid.NewGuid():N}");
        var previous = Path.Combine(parent, $"{name}.old-{Guid.NewGuid():N}");
        var movedPrevious = false;
        try
        {
            Directory.CreateDirectory(staging);
            using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            {
                foreach (var assembly in SdkPackage.Assemblies)
                    await ExtractAsync(zip, LibFolder + assembly, Path.Combine(staging, assembly), cancellationToken);
            }

            if (workflowBytes is not null)
            {
                using var zip = new ZipArchive(new MemoryStream(workflowBytes), ZipArchiveMode.Read);
                await ExtractAsync(zip, SdkPackage.Workflow.Entry, Path.Combine(staging, SdkPackage.Workflow.AssemblyFileName), cancellationToken);
            }

            SdkFolder.Write(staging, new SdkRecord(SdkPackage.Id, SdkPackage.Version, SdkPackage.Sha512Base64, SdkPackage.LicenseUrl,
                (clock ?? TimeProvider.System).GetUtcNow().ToUniversalTime(), how == AcceptanceKind.Flag ? "flag" : "prompt",
                workflowBytes is null ? null : SdkPackage.Workflow.Id,
                workflowBytes is null ? null : SdkPackage.Workflow.Version,
                workflowBytes is null ? null : SdkPackage.Workflow.Sha512Base64));

            if (Directory.Exists(target))
            {
                Directory.Move(target, previous);
                movedPrevious = true;
            }
            Directory.Move(staging, target);
            movedPrevious = false; // the swap is done: from here a failure must not roll back
            try { Directory.Delete(previous, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // best effort: the SDK is installed
        }
        catch
        {
            if (movedPrevious && !Directory.Exists(target) && Directory.Exists(previous))
                Directory.Move(previous, target);
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>The bytes of <paramref name="nupkg"/> once its SHA-512 is the pinned one; nothing is written before.</summary>
    private static async Task<byte[]> ReadVerifiedAsync(Stream nupkg, string expectedSha512Base64, string id, string version,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await nupkg.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        var actual = Convert.ToBase64String(SHA512.HashData(bytes));
        if (!string.Equals(actual, expectedSha512Base64, StringComparison.Ordinal))
            throw new CliException(ExitCodes.Unexpected,
                $"The package does not match the pinned {id} {version} (SHA-512 {actual}, expected {expectedSha512Base64}). Nothing was installed.");
        return bytes;
    }

    /// <summary>
    /// Writes the entry named exactly <paramref name="entryName"/> to <paramref name="destination"/>. Matched by exact entry name and
    /// written under our own file name: nothing from the archive decides a path, so an entry such as "../x" cannot escape the
    /// staging folder.
    /// </summary>
    private static async Task ExtractAsync(ZipArchive zip, string entryName, string destination, CancellationToken cancellationToken)
    {
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, entryName, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException(ExitCodes.Unexpected, $"The package has no {entryName}. Nothing was installed.");
        await using var input = entry.Open();
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
    }
}
