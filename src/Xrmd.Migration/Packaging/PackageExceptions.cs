namespace Xrmd.Migration.Packaging;

/// <summary>The passphrase is too short, or does not open the package.</summary>
public sealed class PackagePassphraseException(string message) : Exception(message);

/// <summary>
/// An entry of the package was changed, cut, swapped with another entry or is missing: its content cannot be trusted.
/// </summary>
public sealed class PackageIntegrityException(string? logicalPath, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The logical path of the entry (for example <c>data/account/00001.jsonl.gz</c>), when known.</summary>
    public string? LogicalPath { get; } = logicalPath;
}

/// <summary>The file is not a package this version can read (no header, unknown format or version, unknown value tag).</summary>
public sealed class PackageFormatException(string message) : Exception(message);
