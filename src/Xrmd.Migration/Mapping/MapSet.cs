using Xrmd.Migration.Source;

namespace Xrmd.Migration.Mapping;

/// <summary>The mapping files of a run break the offline rules of spec §6; <see cref="Problems"/> lists every one found.</summary>
public sealed class MapValidationException(IReadOnlyList<string> problems) : Exception(string.Join("\n", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// The mapping files of one run, combined and checked offline (spec §6 rules 2, 3, 4, 6, 9 and the relationship names).
/// Same-table entries (<c>account → account</c>) of several files are merged into one <see cref="MapTable"/>.
/// </summary>
public sealed class MapSet
{
    private readonly Dictionary<string, MapTable> _bySource;
    private readonly Dictionary<string, string> _relationships;

    private MapSet(IReadOnlyList<MapFile> files, IReadOnlyList<MapRequirement> requires, Dictionary<string, MapTable> bySource, Dictionary<string, string> relationships)
    {
        Files = files;
        Requires = requires;
        _bySource = bySource;
        _relationships = relationships;
    }

    public IReadOnlyList<MapFile> Files { get; }

    /// <summary>The <c>requires</c> entries of all files, one per solution at the highest version asked.</summary>
    public IReadOnlyList<MapRequirement> Requires { get; }

    /// <summary>One entry per source table, in the order the files list them.</summary>
    public IReadOnlyCollection<MapTable> Tables => _bySource.Values;

    public bool IsEmpty => _bySource.Count == 0 && _relationships.Count == 0;

    /// <summary>The number of N:N relationships the files rename.</summary>
    public int RelationshipCount => _relationships.Count;

    public MapTable? BySource(string sourceTable) => _bySource.GetValueOrDefault(sourceTable);

    /// <summary>The target of a source table that is renamed; null for a table that is not mapped or is only column-mapped.</summary>
    public string? TargetOf(string sourceTable) => _bySource.TryGetValue(sourceTable, out var t) && t.IsRenamed ? t.Target : null;

    public string? RelationshipTargetOf(string sourceSchemaName) => _relationships.GetValueOrDefault(sourceSchemaName);

    /// <summary>Combines <paramref name="files"/>; throws <see cref="MapValidationException"/> with every problem found.</summary>
    public static MapSet Combine(IReadOnlyList<MapFile> files)
    {
        var problems = new List<string>();

        var requires = files.SelectMany(f => f.Requires)
            .GroupBy(r => r.Solution, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(r => r.MinVersion)!)
            .ToList();

        var entries = files.SelectMany(f => f.Tables.Select(t => (File: f.Name, Table: t))).ToList();

        // Rule 2: a renamed source appears once; a target table has one source.
        var bySource = new Dictionary<string, MapTable>(StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(e => e.Table.Source, StringComparer.Ordinal))
        {
            var list = group.ToList();
            if (list.Any(e => e.Table.IsRenamed) && list.Count > 1)
                problems.Add($"Rule 2: source table '{group.Key}' is mapped to another table and appears more than once ({FileList(list.Select(e => e.File))}).");
            bySource[group.Key] = Merge(group.Key, list, problems);
        }
        foreach (var group in entries.GroupBy(e => e.Table.Target, StringComparer.Ordinal))
        {
            var sources = group.Select(e => e.Table.Source).Distinct(StringComparer.Ordinal).ToList();
            if (sources.Count > 1)
                problems.Add($"Rule 2: target table '{group.Key}' is the target of more than one source table ({string.Join(", ", sources.Select(s => $"'{s}'"))}).");
        }

        // Rule 3 (prefix part): a first-party name is never a target.
        foreach (var target in entries.Select(e => e.Table.Target).Distinct(StringComparer.Ordinal))
            if (IsFirstParty(target)) problems.Add($"Rule 3: target table '{target}' has a Microsoft first-party prefix.");

        var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in files.SelectMany(f => f.Relationships.Select(r => (File: f.Name, Relationship: r))).GroupBy(x => x.Relationship.Source, StringComparer.Ordinal))
        {
            var list = group.ToList();
            if (list.Count > 1) problems.Add($"Relationship '{group.Key}' appears more than once ({FileList(list.Select(x => x.File))}).");
            relationships[group.Key] = list[0].Relationship.Target;
        }
        foreach (var target in relationships.Values.Distinct(StringComparer.Ordinal))
            if (IsFirstParty(target)) problems.Add($"Rule 3: relationship target '{target}' has a Microsoft first-party prefix.");

        if (problems.Count > 0) throw new MapValidationException(problems);
        return new MapSet(files, requires, bySource, relationships);
    }

    /// <summary>Spec §8.7: an archive table is our own <c>xrmva_</c> table and a plain copy, so its entry renames or drops columns and nothing more.</summary>
    private static void CheckArchive(MapTable entry, Dictionary<string, MapColumn> columns, Dictionary<string, MapDerived> derived,
        List<MapStatePair> state, Dictionary<int, (int State, int Status)> stateFallback, List<string> problems)
    {
        var source = entry.Source;
        if (!entry.Target.StartsWith(MapReader.ArchivePrefix, StringComparison.Ordinal))
            problems.Add($"Archive: target table '{entry.Target}' of '{source}' must start with '{MapReader.ArchivePrefix}'.");
        foreach (var (name, column) in columns)
        {
            if (!column.Drop && !column.Target.StartsWith(MapReader.ArchivePrefix, StringComparison.Ordinal))
                problems.Add($"Archive: column '{source}.{name}' can only be renamed to a '{MapReader.ArchivePrefix}' column or dropped ('target: null'), not '{column.Target}'.");
            if (column.Values is not null || column.ByLabel || column.Fallback.IsSet)
                problems.Add($"Archive: column '{source}.{name}' has value translation; an archive table keeps values as they are.");
        }
        if (derived.Count > 0) problems.Add($"Archive: table '{source}' has 'derived' entries; an archive table holds the source columns only.");
        if (state.Count > 0 || stateFallback.Count > 0) problems.Add($"Archive: table '{source}' has 'state' entries; an archive table keeps the status values as they are.");
    }

    private static bool IsFirstParty(string name) =>
        ExclusionCatalog.FirstPartyPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    private static string FileList(IEnumerable<string> names) => string.Join(", ", names.Distinct(StringComparer.Ordinal));

    /// <summary>Merges the entries of one source table by column, and checks rules 2 (columns, state), 4 and 9 on the result.</summary>
    private static MapTable Merge(string source, List<(string File, MapTable Table)> list, List<string> problems)
    {
        var columns = new Dictionary<string, MapColumn>(StringComparer.Ordinal);
        var derived = new Dictionary<string, MapDerived>(StringComparer.Ordinal);
        var state = new List<MapStatePair>();
        var stateFallback = new Dictionary<int, (int State, int Status)>();
        var carry = true;
        var sameTableStateFiles = new List<string>();

        foreach (var (file, table) in list)
        {
            foreach (var (name, column) in table.Columns)
                if (!columns.TryAdd(name, column))
                    problems.Add($"Rule 2: column '{name}' of table '{source}' is mapped more than once ({file}).");
            foreach (var (name, d) in table.Derived)
                if (!derived.TryAdd(name, d))
                    problems.Add($"Rule 2: derived column '{name}' of table '{source}' is mapped more than once ({file}).");
            if (table.State.Count > 0 || table.StateFallback.Count > 0)
            {
                if (!table.IsRenamed) sameTableStateFiles.Add(file);
                state.AddRange(table.State);
                foreach (var (from, to) in table.StateFallback) stateFallback[from] = to;
            }
            carry &= table.CarryCustomColumns;
        }
        // A same-table entry keeps the table's own state: state translation needs a renamed table.
        if (sameTableStateFiles.Count > 0)
            problems.Add($"Table '{source}': 'state' or 'stateFallback' on a same-table entry ({FileList(sameTableStateFiles)}); state translation needs a renamed table.");

        // Rule 4: unique source pairs; one target state per source state.
        foreach (var group in state.GroupBy(p => p.From))
            if (group.Count() > 1)
                problems.Add($"Rule 4: table '{source}': the source state pair [{group.Key.State}, {group.Key.Status}] appears more than once in 'state'.");
        foreach (var group in state.GroupBy(p => p.From.State))
        {
            var targets = group.Select(p => p.To.State).Distinct().Order().ToList();
            if (targets.Count > 1)
                problems.Add($"Rule 4: table '{source}': source statecode {group.Key} maps to more than one target statecode ({string.Join(", ", targets)}).");
        }

        // Rule 9 (listed columns): one target column is written by one listed column.
        var written = columns.Where(c => !c.Value.Drop).Select(c => (Source: c.Key, c.Value.Target)).Concat(derived.Select(d => (Source: d.Key, Target: d.Key)));
        foreach (var group in written.GroupBy(w => w.Target, StringComparer.Ordinal))
            if (group.Count() > 1)
                problems.Add($"Rule 9: table '{source}': target column '{group.Key}' is written by more than one column ({string.Join(", ", group.Select(w => $"'{w.Source}'"))}).");

        var first = list[0].Table;
        if (first.Archive) CheckArchive(first, columns, derived, state, stateFallback, problems);
        return new MapTable(source, first.Target, columns, derived, state, stateFallback, carry, first.Archive);
    }
}
