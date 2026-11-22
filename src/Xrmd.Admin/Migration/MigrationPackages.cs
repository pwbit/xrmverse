using Microsoft.Extensions.Configuration;
using Xrmd.Files;
using Xrmd.Migration.Packaging;

namespace Xrmd.Admin.Migration;

/// <summary>Opens the package of a run: an uploaded one, read in place from the file store, or a file under <c>Migration:ImportRoot</c>.</summary>
public static class MigrationPackages
{
    public const string ImportRootSetting = "Migration:ImportRoot";

    /// <summary>
    /// The full path of <paramref name="serverPath"/> when it lies under <c>Migration:ImportRoot</c> (migration spec §7.1).
    /// </summary>
    /// <exception cref="MigrationRequestException">No root is configured, or the path is outside it.</exception>
    public static string CheckServerPath(IConfiguration configuration, string serverPath)
    {
        if (configuration[ImportRootSetting] is not { Length: > 0 } root)
            throw new MigrationRequestException("Server paths are disabled (Migration:ImportRoot is not set).");
        var full = Path.GetFullPath(serverPath);
        var rootFull = Path.GetFullPath(root);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar)) rootFull += Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.Ordinal))
            throw new MigrationRequestException($"The server path must be under Migration:ImportRoot ({root}).");
        return full;
    }

    /// <summary>Opens the package of <paramref name="packageId"/> (uploaded) or <paramref name="serverPath"/>.</summary>
    /// <exception cref="PackagePassphraseException">The passphrase is wrong.</exception>
    /// <exception cref="PackageFormatException">Not a package.</exception>
    public static async Task<PackageReader> OpenAsync(IFileStore files, string database, Guid? packageId, string? serverPath, string passphrase,
        CancellationToken ct)
    {
        if (packageId is { } id)
        {
            var stream = await FileStoreReadStream.OpenAsync(files, MigrationPackageKeys.Package(database, id), ct);
            return PackageReader.Open(stream, passphrase);
        }
        return PackageReader.Open(serverPath ?? throw new ArgumentException("A run names a package or a server path."), passphrase);
    }
}

/// <summary>A request about a migration run that cannot be honored as sent (400).</summary>
public sealed class MigrationRequestException(string message) : Exception(message);
