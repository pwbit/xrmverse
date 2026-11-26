using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Migration.Mapping;

/// <summary>
/// Lets a <see cref="PackageReader"/> show a package under the names of the target: its manifest and the records of its
/// chunks. The package itself (entries, hashes, chunk paths) is never changed.
/// </summary>
public interface IPackageTranslator
{
    /// <summary>The manifest as the import sees it; <paramref name="raw"/> is the manifest as exported.</summary>
    PackageManifest TranslateManifest(PackageManifest raw);

    /// <summary>The record of the chunk <paramref name="chunkPath"/> as the import writes it.</summary>
    MigrationRecord Translate(string chunkPath, MigrationRecord record);
}

/// <summary>The package translator of a map: <see cref="RecordTranslator"/> for tables, relationship renames for N:N.</summary>
public sealed class MapPackageTranslator(RecordTranslator translator) : IPackageTranslator
{
    /// <summary>The record translator of the map (what the census of phase 1 runs again with notes).</summary>
    public RecordTranslator Records => translator;

    public PackageManifest TranslateManifest(PackageManifest raw) => raw with
    {
        Tables = [.. raw.Tables.Select(t =>
        {
            var target = translator.TableOf(t.LogicalName);
            // The import side resolves the real primary key from the target metadata anyway; "<target>id" is the usual name.
            return target == t.LogicalName ? t : t with { LogicalName = target, PrimaryKey = target + "id" };
        })],
        Relationships = [.. raw.Relationships.Select(r =>
            translator.RelationshipOf(r.SchemaName) is { } target ? r with { SchemaName = target } : r)],
    };

    public MigrationRecord Translate(string chunkPath, MigrationRecord record)
    {
        var parts = chunkPath.Split('/');
        return parts.Length > 2 && parts[0] is "data" or "security"
            ? translator.Translate(parts[1], record, null)
            : record;
    }
}
