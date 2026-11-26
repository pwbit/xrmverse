namespace Xrmd.Migration.Packaging;

/// <summary>
/// <c>manifest.json</c> (encrypted): what the package holds. Unknown properties are ignored, so a later xrmd can add
/// properties without changing the format version.
/// </summary>
public sealed record PackageManifest
{
    public int FormatVersion { get; init; } = PackageHeader.CurrentVersion;
    public DateTimeOffset CreatedOn { get; init; }
    public string ToolVersion { get; init; } = "";
    public PackageSource Source { get; init; } = new("", "", "");
    public IReadOnlyList<PackageSolution> Solutions { get; init; } = [];
    public IReadOnlyList<PackageTable> Tables { get; init; } = [];
    public IReadOnlyList<PackageRelationship> Relationships { get; init; } = [];
    public IReadOnlyList<PackageEntry> Entries { get; init; } = [];
    public IReadOnlyList<PackageWarning> Warnings { get; init; } = [];
    /// <summary>The entry paths of the map files (format version 2); empty for a package without maps.</summary>
    public IReadOnlyList<string> Maps { get; init; } = [];
    /// <summary>
    /// The standard tables xrmverse lacks and no map names, whose rows are in the package so that an import can create archive
    /// tables for them (spec §8.7). They are also in <see cref="Tables"/>; the record count is the same.
    /// </summary>
    public IReadOnlyList<PackageTable> ArchiveCandidates { get; init; } = [];
}

public sealed record PackageSource(string Url, string EnvironmentId, string OrganizationId);

public sealed record PackageSolution(int Order, string UniqueName, string Version, bool Managed, string Path);

public sealed record PackageTable(string LogicalName, string PrimaryKey, long Records, IReadOnlyList<string> Chunks);

public sealed record PackageRelationship(string SchemaName, long Pairs, IReadOnlyList<string> Chunks);

/// <summary>An entry: its logical path, the SHA-256 (lowercase hex) and the size of its plaintext.</summary>
public sealed record PackageEntry(string Path, string Sha256, long Bytes);

public sealed record PackageWarning(string Code, string Message);
