using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Source;
using Xrmd.Model.Metadata;

namespace Xrmd.Admin.Migration.Mapping;

/// <summary>
/// The map check of an import (R9, migration table mapping spec §8.2), run in phase 1 before anything is written: the required
/// solutions, the target tables, columns, options, status pairs and N:N relationships the maps name, the column types, the kind and
/// ownership of the tables (§6 rules 7 and 8), and carried custom columns that clash with a target column of another type. An archive
/// entry (§8.7) is checked the other way round: its table must not exist yet (unless this run created it), and its source must be a
/// table the package holds for an archive. A table only the import's maps rename must find the custom columns it carries on the target
/// or in the package's schema.
/// </summary>
public static class MapCheck
{
    /// <summary>Every problem found, one sentence each (no final period); empty when the maps fit the organization.</summary>
    /// <param name="catalog">The source as exported; a table or column it lacks is not type-checked.</param>
    /// <param name="archiveCandidates">The source tables the package holds for an archive (its candidates, and the tables a map of the export
    /// archived): an archive entry must name one.</param>
    /// <param name="createdByThisRun">For an archive table: true when this run created it (its ledger chunk is done), so a resume finds it.</param>
    /// <param name="exported">Whether the package holds the schema of a source column (table, column): true when the package's own maps
    /// renamed its table (the export then put all its custom columns in the delta) or <c>schema/delta.json</c> has it. A custom column a
    /// renamed table carries must be on the target already or in the package; null skips that check.</param>
    public static async Task<IReadOnlyList<string>> RunAsync(MapSet maps, SourceCatalog catalog, IServiceProvider services, CancellationToken ct,
        IReadOnlyCollection<string>? archiveCandidates = null, Func<string, Task<bool>>? createdByThisRun = null,
        Func<string, string, bool>? exported = null)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(services);
        var problems = new List<string>();
        var metadata = services.GetRequiredService<IMetadataStore>();
        var target = new MetadataMapTarget(metadata);

        var versions = await SolutionVersionsAsync(services.GetRequiredService<IDbConnectionFactory>(), ct);
        foreach (var required in maps.Requires)
        {
            if (!versions.TryGetValue(required.Solution, out var installed))
                problems.Add($"solution '{required.Solution}' is not installed (the map requires version {required.MinVersion} or later)");
            else if (!Version.TryParse(installed, out var version) || Full(version) < Full(required.MinVersion))
                problems.Add($"solution '{required.Solution}' is at version {installed}, the map requires {required.MinVersion} or later");
        }

        var sourceOptions = catalog.Options.Select(o => (o.Table, o.Column)).ToHashSet();
        var sourceTables = catalog.Tables.GroupBy(t => t.LogicalName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var candidates = new HashSet<string>(archiveCandidates ?? [], StringComparer.Ordinal);
        foreach (var entry in maps.Tables)
        {
            if (entry.Archive)
            {
                await CheckArchiveAsync(entry, metadata, candidates, createdByThisRun, problems);
                if (sourceTables.GetValueOrDefault(entry.Source) is { } archivedSource) CheckArchiveColumns(entry, archivedSource, catalog, problems);
                continue;
            }
            var table = metadata.GetTable(entry.Target);
            if (table is null)
            {
                problems.Add($"table '{entry.Target}' (target of '{entry.Source}') does not exist");
                continue;
            }
            var source = sourceTables.GetValueOrDefault(entry.Source);
            if (entry.IsRenamed && source is not null) CheckKind(entry, source, table, problems);

            foreach (var (name, column) in entry.Columns)
            {
                var targetColumn = ColumnOf(table, column.Target);
                if (targetColumn is null)
                {
                    problems.Add($"column '{entry.Target}.{column.Target}' (target of '{entry.Source}.{name}') does not exist");
                    continue;
                }
                var hasRule = column.Values is not null || column.ByLabel || column.Fallback.IsSet;
                if (source?.Columns.FirstOrDefault(c => c.LogicalName == name) is { } sourceColumn
                    && !Compatible(sourceColumn.AttributeType, sourceOptions.Contains((entry.Source, name)), targetColumn, hasRule))
                    problems.Add($"column '{entry.Source}.{name}' ({sourceColumn.AttributeType}) cannot be written to '{entry.Target}.{column.Target}' ({TypeOf(targetColumn)})");
                CheckOptions(target, entry.Target, targetColumn, column.Values, column.Fallback, $"'{entry.Source}.{name}'", problems);
            }

            foreach (var (name, derived) in entry.Derived)
            {
                var targetColumn = ColumnOf(table, name);
                if (targetColumn is null)
                {
                    problems.Add($"derived column '{entry.Target}.{name}' does not exist");
                    continue;
                }
                CheckOptions(target, entry.Target, targetColumn, derived.Values, derived.Fallback, $"derived '{entry.Target}.{name}'", problems);
            }

            var statuses = target.Statuses(entry.Target).Select(s => (s.State, s.Status)).ToHashSet();
            foreach (var pair in entry.State.Select(p => p.To).Distinct())
                if (!statuses.Contains(pair))
                    problems.Add($"status pair [{pair.State}, {pair.Status}] of '{entry.Target}' does not exist (state of '{entry.Source}')");
            foreach (var pair in entry.StateFallback.Values.Distinct())
                if (!statuses.Contains(pair))
                    problems.Add($"status pair [{pair.State}, {pair.Status}] of '{entry.Target}' does not exist (stateFallback of '{entry.Source}')");

            // A table renamed only by an import map: the export left its custom columns out of the delta (it scopes them by its own
            // map) and R11 strips its node from the customer's solutions, so a carried column the target lacks would not exist (R7).
            if (entry.IsRenamed && entry.CarryCustomColumns && source is not null && exported is not null
                && source.Columns.Where(c => IsCarried(entry, source, c) && ColumnOf(table, c.LogicalName) is null && !exported(entry.Source, c.LogicalName))
                    .Select(c => $"'{c.LogicalName}'").ToList() is { Count: > 0 } notExported)
                problems.Add($"table '{entry.Source}': its custom columns {string.Join(", ", notExported)} were not exported with a map; export again with this map");

            if (entry.IsRenamed && entry.CarryCustomColumns && source is not null)
                foreach (var carried in source.Columns.Where(c => IsCarried(entry, source, c)))
                {
                    if (ColumnOf(table, carried.LogicalName) is not { } existing) continue;
                    if (!Compatible(carried.AttributeType, sourceOptions.Contains((entry.Source, carried.LogicalName)), existing, hasRule: false))
                        problems.Add($"carried custom column '{entry.Source}.{carried.LogicalName}' ({carried.AttributeType}) clashes with '{entry.Target}.{carried.LogicalName}' ({TypeOf(existing)})");
                    // A carried lookup kept on an existing lookup must point to the same tables (the source's under their target names, R5).
                    else if (existing is LookupColumn lookup && Lookups.Contains(carried.AttributeType))
                    {
                        var wanted = carried.Targets.Select(t => maps.TargetOf(t) ?? t).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                        var actual = (lookup.Targets ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                        if (wanted.Count > 0 && !wanted.SequenceEqual(actual, StringComparer.Ordinal))
                            problems.Add($"carried custom lookup '{entry.Source}.{carried.LogicalName}' (to {string.Join(", ", wanted)}) clashes with '{entry.Target}.{carried.LogicalName}' (to {string.Join(", ", actual)})");
                    }
                }
        }

        foreach (var relationship in maps.Files.SelectMany(f => f.Relationships).DistinctBy(r => r.Source))
            if (metadata.GetRelationshipDefinition(relationship.Target) is not ManyToManyRelationship)
                problems.Add($"relationship '{relationship.Target}' (target of '{relationship.Source}') does not exist");

        return problems;
    }

    /// <summary>
    /// An archive entry (spec §8.7) inverts the existence check: its table is created by the run, so it must not exist yet, unless this run
    /// created it (a resume). Its source must be an archive candidate of the package: only their rows and column details were exported.
    /// </summary>
    private static async Task CheckArchiveAsync(MapTable entry, IMetadataStore metadata, HashSet<string> candidates, Func<string, Task<bool>>? createdByThisRun,
        List<string> problems)
    {
        if (!candidates.Contains(entry.Source))
            problems.Add($"table '{entry.Source}' is not an archive candidate of the package (archive of '{entry.Source}' to '{entry.Target}')");
        if (!LogicalNameValidator.IsValid(entry.Target) || entry.Target.Length + 2 > ArchiveNames.MaxLength)
            problems.Add($"table name '{entry.Target}' (archive of '{entry.Source}') is not a valid table name of at most {ArchiveNames.MaxLength - 2} characters");
        if (metadata.GetTable(entry.Target) is not null && (createdByThisRun is null || !await createdByThisRun(entry.Target)))
            problems.Add($"table '{entry.Target}' (archive of '{entry.Source}') already exists");
    }

    /// <summary>
    /// The columns an archive entry renames (spec §8.7): each target is an archive name (<c>xrmva_</c>, a valid logical name leaving room
    /// for the <c>_base</c> companion of a money column) and no two archive columns (renamed, generated, lookup text columns, the key) share
    /// a name.
    /// </summary>
    private static void CheckArchiveColumns(MapTable entry, SourceCatalogTable source, SourceCatalog catalog, List<string> problems)
    {
        foreach (var (name, column) in entry.Columns.Where(c => !c.Value.Drop))
            if (!column.Target.StartsWith(ArchiveNames.Prefix, StringComparison.Ordinal) || !LogicalNameValidator.IsValid(column.Target)
                || column.Target.Length + "_base".Length > ArchiveNames.MaxLength)
                problems.Add($"column '{entry.Target}.{column.Target}' (archive of '{entry.Source}.{name}') is not an archive column name: " +
                             $"'{ArchiveNames.Prefix}' and at most {ArchiveNames.MaxLength - "_base".Length} characters");

        // Every name the archive may create: a lookup takes its lookup or its text column, so both are reserved.
        var names = new List<(string Name, string Of)> { (source.IsActivity ? "activityid" : entry.Target + "id", "the primary key") };
        foreach (var column in ArchiveColumns.Plan(entry, source, catalog, _ => true))
        {
            if (column.Kind is not (ArchiveColumnKind.Copied or ArchiveColumnKind.Lookup or ArchiveColumnKind.LookupAsText)) continue;
            names.Add((column.Target!, $"'{entry.Source}.{column.Source.LogicalName}'"));
            if (column.TextTarget is { } text) names.Add((text, $"the name of '{entry.Source}.{column.Source.LogicalName}'"));
        }
        foreach (var clash in names.GroupBy(n => n.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"column '{entry.Target}.{clash.Key}' is the archive column of {string.Join(" and ", clash.Select(c => c.Of))}");
    }

    /// <summary>Rule 8: activity ↔ activity, and user-owned ↔ user-owned, organization-owned ↔ organization-owned.</summary>
    private static void CheckKind(MapTable entry, SourceCatalogTable source, TableDefinition table, List<string> problems)
    {
        var targetIsActivity = table.IsActivity == true;
        if (source.IsActivity && !targetIsActivity)
            problems.Add($"table '{entry.Source}' is an activity table but '{entry.Target}' is not");
        else if (!source.IsActivity && targetIsActivity)
            problems.Add($"table '{entry.Source}' is not an activity table but '{entry.Target}' is");

        var targetOwnership = table.OwnershipType == TableOwnership.OrganizationOwned ? "organization" : "user";
        if (!string.Equals(source.Ownership, targetOwnership, StringComparison.Ordinal))
            problems.Add($"table '{entry.Source}' is {source.Ownership}-owned but '{entry.Target}' is {targetOwnership}-owned");
    }

    /// <summary>Rule 7: the values and the fallback written to a choice column are options of it; a whole-number column takes any number.</summary>
    private static void CheckOptions(MetadataMapTarget target, string table, ColumnDefinition column, IReadOnlyDictionary<int, int>? values,
        MapFallback fallback, string of, List<string> problems)
    {
        if (column is not (ChoiceColumn or MultiChoiceColumn or StatusColumn or StateColumn)) return;
        var options = column is StatusColumn
            ? target.Statuses(table).Select(s => s.Status).ToHashSet()
            : target.Options(table, column.LogicalName!).Select(o => o.Value).ToHashSet();
        foreach (var value in (values?.Values ?? []).Distinct())
            if (!options.Contains(value))
                problems.Add($"option {value} of '{table}.{column.LogicalName}' does not exist (values of {of})");
        if (fallback is { IsSet: true, Value: int f } && !options.Contains(f))
            problems.Add($"option {f} of '{table}.{column.LogicalName}' does not exist (fallback of {of})");
    }

    /// <summary>A custom column of the customer that a renamed table with <c>customColumns: carry</c> takes along (spec §6.1).</summary>
    private static bool IsCarried(MapTable entry, SourceCatalogTable source, SourceCatalogColumn column) =>
        column.IsCustom
        && column.LogicalName != source.PrimaryKey
        && !entry.Columns.ContainsKey(column.LogicalName)
        && !ExclusionCatalog.FirstPartyPrefixes.Any(p => column.LogicalName.StartsWith(p, StringComparison.Ordinal));

    private static readonly HashSet<string> Choices = new(StringComparer.Ordinal) { "Picklist", "State", "Status" };
    private static readonly HashSet<string> Texts = new(StringComparer.Ordinal) { "String", "Memo" };
    private static readonly HashSet<string> Lookups = new(StringComparer.Ordinal) { "Lookup", "Customer", "Owner" };

    /// <summary>
    /// The conversions §6.2 allows: text ↔ multi-line text; a choice, status reason or state into another of them or into a whole
    /// number (rule 7) only with a <c>values</c>, <c>byLabel</c> or <c>fallback</c> rule (a whole number never into a choice); a lookup,
    /// customer or owner into a lookup or customer column; otherwise the same type. A <c>Virtual</c> source column (Dataverse's type
    /// for multi-select choices, images and files) is a multi-select choice when the catalog has options for it, else an image or a file.
    /// </summary>
    private static bool Compatible(string sourceType, bool sourceHasOptions, ColumnDefinition target, bool hasRule)
    {
        var targetType = TypeOf(target);
        if (sourceType == targetType) return true;
        if (Texts.Contains(sourceType) && Texts.Contains(targetType)) return true;
        if (Choices.Contains(sourceType) && (Choices.Contains(targetType) || targetType == "Integer")) return hasRule;
        if (Lookups.Contains(sourceType) && targetType is "Lookup" or "Customer") return true;
        if (sourceType == "Virtual") return sourceHasOptions ? targetType == "MultiSelectPicklist" : targetType is "Image" or "File";
        return false;
    }

    /// <summary>The type of a target column, named as the source catalog names types (the Dataverse <c>AttributeType</c>).</summary>
    private static string TypeOf(ColumnDefinition column) => column switch
    {
        MultiChoiceColumn => "MultiSelectPicklist",
        StatusColumn => "Status",
        StateColumn => "State",
        EntityNameColumn => "EntityName",
        ChoiceColumn => "Picklist",
        MemoColumn => "Memo",
        TextColumn => "String",
        IntegerColumn => "Integer",
        FileColumn => "File",
        ImageColumn => "Image",
        _ => column.AttributeType?.ToString() ?? column.GetType().Name,
    };

    /// <summary>The version with its missing components as 0, so that 1.0 and 1.0.0.0 compare equal.</summary>
    private static Version Full(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static ColumnDefinition? ColumnOf(TableDefinition table, string column) =>
        table.Attributes?.FirstOrDefault(a => a.LogicalName == column);

    /// <summary>The installed solutions and their versions, by unique name (ignoring case).</summary>
    private static async Task<Dictionary<string, string>> SolutionVersionsAsync(IDbConnectionFactory factory, CancellationToken ct)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await factory.OpenAsync(ct);
        var s = new DataSession(conn, null);
        await using var cmd = s.Command($"SELECT uniquename, version FROM {s.DataTable("solution")}");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (!reader.IsDBNull(0)) versions[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        return versions;
    }
}
