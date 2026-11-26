using System.IO.Compression;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Admin.Migration.Mapping;

/// <summary>
/// The values <c>derived</c> entries read (migration table mapping spec §6.5): for each referenced table (a target of a lookup that a
/// <c>derived</c> entry goes through), the referenced columns of its rows, by id, read once from the package's raw chunks. Also how
/// many source rows take derived values from each referenced row, for <c>derived-shared</c>. Holds only what the maps need.
/// </summary>
public sealed class DerivedIndex : IDerivedValues
{
    private readonly Dictionary<(string Table, Guid Id), Dictionary<string, MigrationValue>> _rows;
    private readonly Dictionary<(string Table, Guid Id), int> _references;

    private DerivedIndex(Dictionary<(string Table, Guid Id), Dictionary<string, MigrationValue>> rows, Dictionary<(string Table, Guid Id), int> references)
    {
        _rows = rows;
        _references = references;
    }

    /// <summary>An index with no rows: every derived value defaults.</summary>
    public static DerivedIndex Empty { get; } = new([], []);

    public MigrationValue? Get(string sourceTable, Guid id, string column) =>
        _rows.TryGetValue((sourceTable, id), out var values) ? values.GetValueOrDefault(column) ?? NullValue.Instance : null;

    public bool IsShared(string sourceTable, Guid id) => _references.GetValueOrDefault((sourceTable, id)) > 1;

    /// <summary>
    /// Reads the referencing tables (the mapped tables with <c>derived</c> entries) for the reference counts and the referenced tables
    /// they name, then the referenced tables for the needed columns. Reads the raw chunks, whatever translator the package has.
    /// </summary>
    public static async Task<DerivedIndex> BuildAsync(PackageReader package, MapSet maps, SourceCatalog catalog, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(catalog);
        var referencing = maps.Tables.Where(t => t.Derived.Count > 0).ToList();
        if (referencing.Count == 0) return Empty;

        var tables = package.RawManifest.Tables.ToDictionary(t => t.LogicalName, StringComparer.Ordinal);
        var lookupTargets = catalog.Tables.SelectMany(t => t.Columns.Select(c => (Key: (t.LogicalName, c.LogicalName), c.Targets)))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Targets);

        // The columns needed per referenced table: the catalog's lookup targets, and the tables the exported lookups actually name.
        var needed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Need(string table, string column)
        {
            if (!needed.TryGetValue(table, out var columns)) needed[table] = columns = new HashSet<string>(StringComparer.Ordinal);
            columns.Add(column);
        }
        foreach (var entry in referencing)
            foreach (var derived in entry.Derived.Values)
                foreach (var target in lookupTargets.GetValueOrDefault((entry.Source, derived.LookupColumn)) ?? [])
                    Need(target, derived.Column);

        var references = new Dictionary<(string Table, Guid Id), int>();
        foreach (var entry in referencing)
        {
            if (!tables.TryGetValue(entry.Source, out var table)) continue;
            var lookups = entry.Derived.Values.GroupBy(d => d.LookupColumn, StringComparer.Ordinal).ToList();
            await foreach (var record in ReadAsync(package, table.Chunks, ct))
            {
                var referenced = new HashSet<(string Table, Guid Id)>();
                foreach (var lookup in lookups)
                {
                    if (record.Values.GetValueOrDefault(lookup.Key) is not LookupValue value) continue;
                    referenced.Add((value.Table, value.Id));
                    foreach (var derived in lookup) Need(value.Table, derived.Column);
                }
                foreach (var key in referenced) references[key] = references.GetValueOrDefault(key) + 1;
            }
        }

        var rows = new Dictionary<(string Table, Guid Id), Dictionary<string, MigrationValue>>();
        foreach (var (name, columns) in needed)
        {
            if (!tables.TryGetValue(name, out var table)) continue;
            await foreach (var record in ReadAsync(package, table.Chunks, ct))
            {
                var values = new Dictionary<string, MigrationValue>(StringComparer.Ordinal);
                foreach (var column in columns)
                    if (record.Values.TryGetValue(column, out var value)) values[column] = value;
                rows[(name, record.Id)] = values;
            }
        }
        return new DerivedIndex(rows, references);
    }

    /// <summary>The records of <paramref name="chunks"/> as exported (no translator).</summary>
    internal static async IAsyncEnumerable<MigrationRecord> ReadAsync(PackageReader package, IEnumerable<string> chunks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var chunk in chunks)
        {
            await using var entry = package.OpenEntry(chunk);
            await using var gzip = new GZipStream(entry, CompressionMode.Decompress);
            using var text = new StreamReader(gzip);
            while (await text.ReadLineAsync(ct) is { } line)
                if (line.Length > 0) yield return RecordLineCodec.Read(line);
        }
    }
}
