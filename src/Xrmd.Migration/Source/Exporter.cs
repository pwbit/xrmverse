using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Source;

/// <summary>Saved after every committed chunk: a rerun on the same folder skips what is done.</summary>
public sealed record ExportProgress
{
    public Dictionary<string, TableProgress> Tables { get; init; } = [];
    public bool SchemaDone { get; init; }
    public List<PackageSolution> Solutions { get; init; } = [];
    public List<PackageWarning> SchemaWarnings { get; init; } = [];
    public PackageSource? Source { get; init; }

    /// <summary>The tables the exported solutions carry (their <c>customizations.xml</c>), kept for a resume.</summary>
    public List<string> SolutionTables { get; init; } = [];

    /// <summary>The solutions, table scope and maps the export started with; a resume must use the same.</summary>
    public string? Options { get; init; }

    /// <summary>The map entries written to the package (empty without maps), kept for a resume.</summary>
    public List<string> MapEntries { get; init; } = [];

    /// <summary>The archive candidates the export decided on (spec §8.7); null before the schema phase is done. A resume keeps them.</summary>
    public List<string>? Candidates { get; init; }
}

/// <summary>
/// Exports a whole environment: solutions and schema delta, security tables under <c>security/</c>, data tables under
/// <c>data/</c>, N:N intersect rows under <c>nn/</c>, files under <c>files/</c>; then the manifest. Read only (GET and ExportSolution).
/// </summary>
public sealed class Exporter(DataverseClient client, ExportOptions options, IProgress<string>? log = null)
{
    internal const string SystemAdministratorTemplate = "627090ff-40a3-4053-8790-584edc5be201";

    /// <summary>Security tables, exported under <c>security/</c> whatever the catalog says (role comes with the solutions but its ids are needed).</summary>
    internal static readonly string[] SecurityTables =
        ["businessunit", "team", "systemuser", "teammembership", "systemuserroles", "teamroles", "role", "principalobjectaccess", "systemuserprofiles", "teamprofiles"];

    private HashSet<string> _solutionTables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The standard tables xrmverse lacks and no map names that have rows and are exported for archive tables (spec §8.7).</summary>
    private HashSet<string> _candidates = new(StringComparer.Ordinal);
    private bool _candidatesDecided;
    private (IReadOnlyDictionary<string, long> Counts, IReadOnlyList<string> Uncounted)? _unmigrated;

    private readonly object _progressLock = new();
    private ExportProgress _progress = new();

    private sealed record TableResult(TableProgress Progress, IReadOnlySet<string> Skipped, IReadOnlySet<string> FirstParty,
        IReadOnlyDictionary<string, long> Outside, IReadOnlyDictionary<string, long> Unresolved, IReadOnlyDictionary<string, long> Missing,
        IReadOnlyDictionary<string, long> Invalid);

    public async Task<PackageManifest> RunAsync(PackageWriter writer, CancellationToken ct)
    {
        if (options.Maps is not null)
        {
            if ((options.MapTexts?.Count ?? 0) == 0)
                throw new MigrationExportException("The maps have no texts to store in the package (ExportOptions.MapTexts).");
            if (writer.FormatVersion < PackageHeader.MappedVersion)
                throw new MigrationExportException($"An export with maps needs a package of format version {PackageHeader.MappedVersion}: create the writer with PackageHeader.MappedVersion.");
        }
        _progress = writer.ReadProgress<ExportProgress>() ?? new ExportProgress();
        var source = await IdentifyAsync(ct);
        var fingerprint = Fingerprint(options);
        if (_progress.Source is { } first)
        {
            if (!string.Equals(first.OrganizationId, source.OrganizationId, StringComparison.OrdinalIgnoreCase))
                throw new MigrationExportException($"This folder holds an export of {first.Url}, not of {source.Url}: resume it with the same --source, or use another folder.");
            if (_progress.Options is { } saved && saved != fingerprint)
                throw new MigrationExportException("The options differ from the ones this export started with (solutions, --include, --exclude, --map): resume with the same options, or use another folder.");
        }
        var warnings = new List<PackageWarning>();
        warnings.AddRange(await CheckAdminAsync(ct));

        log?.Report("Reading metadata");
        var tables = await SourceMetadata.ReadTablesAsync(client, Exported, ct);
        var nn = await SourceMetadata.ReadManyToManyAsync(client, ct);

        if (!_progress.SchemaDone)
        {
            var schemaWarnings = new List<PackageWarning>();
            var solutions = await ChooseSolutionsAsync(ct);
            log?.Report($"Exporting {solutions.Count} solution(s)");
            var coverage = new SolutionCoverage();
            var exported = await SolutionExporter.ExportAsync(client, writer, solutions, options.ManagedSolutionFiles, schemaWarnings.Add, ct, coverage);
            log?.Report("Computing the schema outside solutions");
            _solutionTables = new HashSet<string>(coverage.Tables, StringComparer.OrdinalIgnoreCase);
            await FindCandidatesAsync(tables, ct);
            var inScope = tables.Where(t => Exported(t.LogicalName) && (t.IsIntersect || InXrmverse(t) || IsMapped(t.LogicalName))).ToList();
            var optionValues = await SourceMetadata.ReadOptionsAsync(client, inScope, ct);
            // An archive table is created by the import from the catalog, not by the schema delta: nothing of it goes into the delta.
            var archived = options.Maps?.Tables.Where(t => t.Archive).Select(t => t.Source).ToHashSet(StringComparer.Ordinal) ?? [];
            var deltaScope = inScope.Where(t => !archived.Contains(t.LogicalName)).ToList();
            var deltaOptions = optionValues.Where(o => !archived.Contains(o.Table)).ToList();
            var deltaNn = nn.Where(r => !archived.Contains(r.Entity1) && !archived.Contains(r.Entity2)).ToList();
            var forced = options.Maps?.Tables.Where(t => t.IsRenamed && !t.Archive).Select(t => t.Source).ToHashSet(StringComparer.Ordinal) ?? [];
            var plan = SchemaDelta.Compute(deltaScope, deltaNn, deltaOptions, coverage, forced);
            // A package with maps or archive candidates is version 2 and carries the source catalog (an older xrmd must refuse it).
            if (_candidates.Count > 0) writer.UpgradeFormat(PackageHeader.MappedVersion);
            var mapEntries = options.Maps is null && _candidates.Count == 0 ? [] : await WriteMapsAsync(writer, tables, optionValues, ct);
            await SchemaDelta.WriteAsync(client, writer, plan, ct);
            schemaWarnings.AddRange(plan.Unsupported.Select(u => new PackageWarning("unsupported-metadata", $"{u.Kind} {u.Name}: {u.Reason}")));
            schemaWarnings.AddRange(await DefaultOnlyComponents.FindAsync(client,
                solutions.Where(s => exported.Any(e => e.UniqueName == s.UniqueName)).Select(s => s.SolutionId), ct));
            Save(writer, p => p with { SchemaDone = true, Solutions = [.. exported], SchemaWarnings = schemaWarnings, Source = source, Options = fingerprint, MapEntries = [.. mapEntries], Candidates = [.. _candidates.Order(StringComparer.Ordinal)],
                SolutionTables = [.. coverage.Tables.Order(StringComparer.OrdinalIgnoreCase)] });
        }
        warnings.AddRange(_progress.SchemaWarnings);
        _solutionTables = new HashSet<string>(_progress.SolutionTables, StringComparer.OrdinalIgnoreCase);
        await FindCandidatesAsync(tables, ct);

        var byName = tables.ToDictionary(t => t.LogicalName, StringComparer.Ordinal);
        var jobs = new List<(SourceTable Table, string Prefix)>();
        foreach (var name in SecurityTables)
            if (byName.TryGetValue(name, out var t) && !IsExcludedByOption(name)) jobs.Add((t, $"security/{name}"));
        foreach (var t in tables.Where(t => IsDataTable(t)).OrderBy(t => t.LogicalName, StringComparer.Ordinal))
            jobs.Add((t, $"data/{t.LogicalName}"));
        foreach (var t in tables.Where(t => t.TableType == "Virtual" && Exported(t.LogicalName)).OrderBy(t => t.LogicalName, StringComparer.Ordinal))
            warnings.Add(new PackageWarning("virtual-table", $"{t.LogicalName}: virtual table, its rows stay in the external source and are not exported"));
        var relationships = new List<SourceRelationship>();
        foreach (var r in nn.Where(r => !SecurityTables.Contains(r.IntersectEntity) && byName.ContainsKey(r.IntersectEntity)).OrderBy(r => r.SchemaName, StringComparer.Ordinal))
        {
            bool Migrated(string table) => Exported(table) && byName.TryGetValue(table, out var end) && (InXrmverse(end) || IsMapped(table));
            // A relationship the map names is exported even when the exclusion catalog lists its intersect table (--exclude still wins).
            var named = options.Maps?.RelationshipTargetOf(r.SchemaName) is not null;
            var intersectExported = named ? !IsExcludedByOption(r.IntersectEntity) : Exported(r.IntersectEntity);
            if (Migrated(r.Entity1) && Migrated(r.Entity2) && intersectExported)
            {
                // Of a table that is renamed in the target, only the relationships the map names and the custom ones travel (spec §7).
                if ((IsRenamedMapped(r.Entity1) || IsRenamedMapped(r.Entity2)) && !named && !r.IsCustom)
                    warnings.Add(new PackageWarning("relationship-not-mapped", $"{r.SchemaName}: a table of this relationship ({r.Entity1}, {r.Entity2}) is mapped to another table and the map does not name the relationship, so its pairs are not exported"));
                else relationships.Add(r);
            }
            else if (!ExclusionCatalog.IsFirstParty(r.IntersectEntity) && (Migrated(r.Entity1) || Migrated(r.Entity2)))
                warnings.Add(new PackageWarning("relationship-outside-scope", $"{r.SchemaName}: one of its tables ({r.Entity1}, {r.Entity2}) is not migrated, so its pairs are not exported"));
        }
        foreach (var r in relationships)
            jobs.Add((byName[r.IntersectEntity], $"nn/{r.SchemaName}"));

        var results = new Dictionary<string, TableResult>();
        var failed = new List<PackageWarning>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(Math.Max(1, options.Parallel));
        var files = new FileExporter(client, writer);
        var codes = tables.Where(t => t.ObjectTypeCode is not null).GroupBy(t => t.ObjectTypeCode!.Value).ToDictionary(g => g.Key, g => g.First().LogicalName);
        // Faults win over cancellations in WhenAll: the first real failure is what surfaces.
        await Task.WhenAll(jobs.Select(async job =>
        {
            await gate.WaitAsync(stop.Token);
            try
            {
                log?.Report($"Exporting {job.Prefix}");
                var exporter = new TableExporter(client, writer, files, code => codes.GetValueOrDefault(code));
                var done = await exporter.ExportAsync(job.Table, job.Prefix, Current(job.Prefix), p => Save(writer, x => Put(x, job.Prefix, p)), stop.Token,
                    NamedByMap(job.Table.LogicalName), IsArchivable(job.Table.LogicalName));
                lock (results)
                    results[job.Prefix] = new TableResult(done, exporter.SkippedColumns.ToHashSet(), exporter.FirstPartyColumns.ToHashSet(),
                        exporter.LookupTargetsOutsideScope.ToDictionary(), exporter.UnresolvedLookups.ToDictionary(), exporter.MissingFiles.ToDictionary(),
                        exporter.InvalidValues.ToDictionary());
            }
            catch (TableNotReadableException e)
            {
                lock (failed) failed.Add(new PackageWarning("table-not-readable", e.Message));
            }
            catch (Exception) when (!stop.IsCancellationRequested)
            {
                // The first real failure stops the other tables: the export is resumable, so nothing is gained by going on.
                stop.Cancel();
                throw;
            }
            finally
            {
                gate.Release();
            }
        }));
        warnings.AddRange(failed.OrderBy(w => w.Message, StringComparer.Ordinal));
        warnings.AddRange(await NotSupportedYetAsync(tables, ct));
        warnings.AddRange(await NotInXrmverseAsync(tables, ct));

        var manifestTables = new List<PackageTable>();
        var archiveCandidates = new List<PackageTable>();
        foreach (var (prefix, result) in results.Where(r => !r.Key.StartsWith("nn/", StringComparison.Ordinal)).OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var table = byName[result.Progress.Table];
            var exported = new PackageTable(table.LogicalName, table.PrimaryKey, result.Progress.Records, result.Progress.Chunks);
            manifestTables.Add(exported);
            if (_candidates.Contains(table.LogicalName)) archiveCandidates.Add(exported);
            if (result.Skipped.Count > 0)
                warnings.Add(new PackageWarning("columns-skipped", $"{table.LogicalName}: {string.Join(", ", result.Skipped.Order(StringComparer.Ordinal))} (type not carried)"));
            if (result.FirstParty.Count > 0)
                warnings.Add(new PackageWarning("first-party-columns", $"{table.LogicalName}: {string.Join(", ", result.FirstParty.Order(StringComparer.Ordinal))} (Microsoft first-party app columns, not exported)"));
            foreach (var (column, count) in result.Outside.OrderBy(o => o.Key, StringComparer.Ordinal))
                warnings.Add(new PackageWarning("lookup-outside-scope", $"{table.LogicalName}.{column}: {count} value(s) point to a table that is not migrated"));
            foreach (var (column, count) in result.Unresolved.OrderBy(o => o.Key, StringComparer.Ordinal))
                warnings.Add(new PackageWarning("lookup-unresolved", $"{table.LogicalName}.{column}: {count} value(s) left out, the source did not tell their table"));
            foreach (var (column, count) in result.Missing.OrderBy(o => o.Key, StringComparer.Ordinal))
                warnings.Add(new PackageWarning("file-missing", $"{table.LogicalName}.{column}: {count} file(s) no longer in the source, exported as empty"));
            foreach (var (column, count) in result.Invalid.OrderBy(o => o.Key, StringComparer.Ordinal))
                warnings.Add(new PackageWarning("value-not-readable", $"{table.LogicalName}.{column}: {count} value(s) left out, their type does not match the column's metadata"));
        }
        var manifestRelationships = new List<PackageRelationship>();
        foreach (var r in relationships)
            if (results.TryGetValue($"nn/{r.SchemaName}", out var done))
                manifestRelationships.Add(new PackageRelationship(r.SchemaName, done.Progress.Records, done.Progress.Chunks));

        var manifest = new PackageManifest
        {
            CreatedOn = DateTimeOffset.UtcNow,
            ToolVersion = typeof(Exporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
            Source = _progress.Source ?? source,
            Solutions = _progress.Solutions,
            Tables = manifestTables,
            Relationships = manifestRelationships,
            Maps = [.. _progress.MapEntries],
            ArchiveCandidates = archiveCandidates,
            Warnings = warnings,
        };
        writer.Complete(manifest);
        return manifest;
    }

    /// <summary>Writes the maps and the source catalog; returns the map entry paths.</summary>
    private async Task<IReadOnlyList<string>> WriteMapsAsync(PackageWriter writer, IReadOnlyList<SourceTable> tables, IReadOnlyList<SourceOption> optionValues, CancellationToken ct)
    {
        using var org = await client.GetJsonAsync("organizations?$select=languagecode", ct);
        var language = org.RootElement.GetProperty("value").EnumerateArray().Select(o => o.GetProperty("languagecode").GetInt32()).FirstOrDefault(1033);
        // The tables an archive table may be made from also need their column details and choice values (spec §8.7).
        var archivable = tables.Where(t => IsDataTable(t) && IsArchivable(t.LogicalName)).ToList();
        var detailed = new Dictionary<string, SourceTable>(StringComparer.Ordinal);
        foreach (var t in archivable) detailed[t.LogicalName] = await SourceMetadata.WithDetailsAsync(client, t, language, ct);
        var candidateOptions = await SourceMetadata.ReadOptionsAsync(client, archivable.Where(t => _candidates.Contains(t.LogicalName)), ct);
        var catalog = SourceCatalogBuilder.Build(tables.Where(IsDataTable).Select(t => detailed.GetValueOrDefault(t.LogicalName, t)), [.. optionValues, .. candidateOptions], language);
        return PackageMaps.Write(writer, options.MapTexts ?? [], catalog);
    }

    /// <summary>What a resume must not change: solutions, table scope and maps.</summary>
    internal static string Fingerprint(ExportOptions options)
    {
        var parts = new List<string>
        {
            string.Join(",", options.Solutions.Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal)),
            options.AllUnmanaged ? "all-unmanaged" : "",
            string.Join(",", options.Include.Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal)),
            string.Join(",", options.Exclude.Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal)),
        };
        // Only when off: an export begun by an older build keeps its fingerprint.
        if (!options.ArchiveCandidates) parts.Add("no-archive-candidates");
        // Only with maps: an export begun without them keeps its fingerprint.
        if (options.Maps is not null)
            parts.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", options.MapTexts!.Select(m => $"{m.Name}\0{m.Yaml}"))))));
        return string.Join("|", parts);
    }

    /// <summary>A source table a map lists (renamed or not): it is exported although xrmverse lacks it, unless <c>--exclude</c> names it.</summary>
    private bool IsMapped(string table) => options.Maps?.BySource(table) is not null;

    /// <summary>A mapped source table whose rows go to another table: not a table of xrmverse, so it is never created by the delta.</summary>
    private bool IsRenamedMapped(string table) => options.Maps?.TargetOf(table) is not null;

    /// <summary>The first-party-prefixed columns of a mapped table that the map names (<c>columns</c> keys and the lookups <c>derived</c> reads).</summary>
    /// <summary>A table an archive table may be made from (spec §8.7): an archive candidate, or archived by a map of the export.</summary>
    private bool IsArchivable(string table) => _candidates.Contains(table) || options.Maps?.BySource(table)?.Archive == true;

    private IReadOnlySet<string>? NamedByMap(string table)
    {
        if (options.Maps?.BySource(table) is not { } map) return null;
        return map.Columns.Keys.Concat(map.Derived.Values.Select(d => d.LookupColumn)).ToHashSet(StringComparer.Ordinal);
    }

    private bool IsExcludedByOption(string table) => options.Exclude.Contains(table, StringComparer.OrdinalIgnoreCase);

    /// <summary>A table whose rows the migration reads (data, security or intersect).</summary>
    private bool Exported(string table)
    {
        if (IsExcludedByOption(table)) return false;
        if (SecurityTables.Contains(table) || IsMapped(table)) return true;
        var exclusion = ExclusionCatalog.Find(table);
        return exclusion is null || (exclusion.Optional && options.Include.Contains(table, StringComparer.OrdinalIgnoreCase));
    }

    private bool IsDataTable(SourceTable t) =>
        Exported(t.LogicalName) && !SecurityTables.Contains(t.LogicalName) && !t.IsIntersect
        && t.TableType != "Virtual" && t.EntitySetName.Length > 0
        && (InXrmverse(t) || IsMapped(t.LogicalName) || _candidates.Contains(t.LogicalName));

    /// <summary>
    /// A table that exists in the target after the schema phase (spec §6.3): an unmanaged custom table (an exported solution or the
    /// schema delta creates it), a managed one an exported solution carries, or a standard one xrmverse has out of the box. Dataverse
    /// marks many Microsoft tables as custom and managed (<c>appaction</c>, <c>powerpagecomponent</c>, …): like other standard tables
    /// outside xrmverse, they are counted, never read.
    /// </summary>
    private bool InXrmverse(SourceTable t) =>
        t.IsCustom ? !t.IsManaged || _solutionTables.Contains(t.LogicalName) : XrmverseTables.Contains(t.LogicalName);

    private TableProgress? Current(string prefix)
    {
        lock (_progressLock) return _progress.Tables.GetValueOrDefault(prefix);
    }

    private static ExportProgress Put(ExportProgress p, string prefix, TableProgress table) =>
        p with { Tables = new Dictionary<string, TableProgress>(p.Tables) { [prefix] = table } };

    private void Save(PackageWriter writer, Func<ExportProgress, ExportProgress> change)
    {
        lock (_progressLock)
        {
            _progress = change(_progress);
            writer.WriteProgress(_progress);
        }
    }

    private async Task<PackageSource> IdentifyAsync(CancellationToken ct)
    {
        using var who = await client.GetJsonAsync("WhoAmI", ct);
        var organizationId = who.RootElement.GetProperty("OrganizationId").GetString() ?? "";
        var environmentId = "";
        try
        {
            using var org = await client.GetJsonAsync("RetrieveCurrentOrganization(AccessType=Microsoft.Dynamics.CRM.EndpointAccessType'Default')", ct);
            if (org.RootElement.TryGetProperty("Detail", out var d) && d.TryGetProperty("EnvironmentId", out var e)) environmentId = e.GetString() ?? "";
        }
        catch (DataverseException)
        {
            // Older environments have no environment id: the organization id identifies the source.
        }
        return new PackageSource(client.Source.ToString().TrimEnd('/'), environmentId, organizationId);
    }

    private async Task<IReadOnlyList<PackageWarning>> CheckAdminAsync(CancellationToken ct)
    {
        using var who = await client.GetJsonAsync("WhoAmI", ct);
        var userId = who.RootElement.GetProperty("UserId").GetString();
        var roles = await client.GetAllAsync($"systemusers({userId})/systemuserroles_association?$select=name,_roletemplateid_value", ct);
        if (roles.Any(r => string.Equals(r.TryGetProperty("_roletemplateid_value", out var t) ? t.GetString() : null, SystemAdministratorTemplate, StringComparison.OrdinalIgnoreCase)))
            return [];
        const string message = "The signed-in user is not System Administrator: tables and records it cannot read are left out of the package.";
        if (options.RequireAdmin) throw new MigrationExportException(message);
        return [new PackageWarning("not-system-administrator", message)];
    }

    private async Task<List<SourceSolution>> ChooseSolutionsAsync(CancellationToken ct)
    {
        var all = await SolutionExporter.ListAsync(client, ct);
        var missing = options.Solutions.Where(n => !all.Any(s => string.Equals(s.UniqueName, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0) throw new MigrationExportException($"Solution(s) not found in the source environment: {string.Join(", ", missing)}.");
        // Every installed managed solution (Microsoft's aside) is carried from its file (spec §6.2.3): rebuilding its schema
        // from metadata would lose its layers, so a missing file stops the export with the list.
        return [.. all.Where(s => (s.Managed && !SolutionExporter.IsMicrosoft(s))
            || (options.AllUnmanaged && !s.Managed && !SolutionExporter.IsMicrosoft(s))
            || options.Solutions.Contains(s.UniqueName, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>How many tables RetrieveTotalRecordCount is asked about at once (the names go in the URL).</summary>
    private const int CountBatch = 50;

    /// <summary>Tables that are not migrated and not security tables: standard tables xrmverse lacks, and managed custom ones outside the exported solutions.</summary>
    private IEnumerable<string> Unmigrated(IReadOnlyList<SourceTable> tables) =>
        tables.Where(t => !t.IsIntersect && t.TableType != "Virtual" && t.EntitySetName.Length > 0 && !InXrmverse(t) && !IsMapped(t.LogicalName)
                && !SecurityTables.Contains(t.LogicalName) && Exported(t.LogicalName))
            .Select(t => t.LogicalName).Order(StringComparer.Ordinal);

    /// <summary>The record counts of <see cref="Unmigrated"/> (once per run), and the tables Dataverse would not count (a refused batch, or no answer).</summary>
    private async Task<(IReadOnlyDictionary<string, long> Counts, IReadOnlyList<string> Uncounted)> CountUnmigratedAsync(IReadOnlyList<SourceTable> tables, CancellationToken ct)
    {
        if (_unmigrated is { } known) return known;
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var uncounted = new List<string>();
        foreach (var batch in Unmigrated(tables).Chunk(CountBatch))
        {
            Dictionary<string, long> answer;
            try
            {
                answer = await CountAsync(batch, ct);
            }
            catch (DataverseException)
            {
                uncounted.AddRange(batch);
                continue;
            }
            foreach (var name in batch)
                if (answer.TryGetValue(name, out var c)) counts[name] = c;
                else uncounted.Add(name);
        }
        return (_unmigrated = (counts, uncounted)).Value;
    }

    /// <summary>
    /// Spec §8.7: the standard tables xrmverse lacks, no map names and the exclusion catalog does not list, that have rows. Their rows are
    /// exported, so that an import can keep them in archive tables. Nothing is read for them when <see cref="ExportOptions.ArchiveCandidates"/> is off.
    /// </summary>
    private async Task FindCandidatesAsync(IReadOnlyList<SourceTable> tables, CancellationToken ct)
    {
        if (_candidatesDecided) return;
        _candidatesDecided = true;
        // A resumed export keeps the set its first run chose: a table that gained rows since is not added.
        // An export of an older build (schema done, no set recorded) has none.
        if (_progress.Candidates is { } chosen) { _candidates = chosen.ToHashSet(StringComparer.Ordinal); return; }
        if (_progress.SchemaDone) return;
        if (!options.ArchiveCandidates) return;
        var (counts, _) = await CountUnmigratedAsync(tables, ct);
        var unmigrated = Unmigrated(tables).ToHashSet(StringComparer.Ordinal);
        // The exclusion catalog lists only part of the system and configuration tables: the metadata keeps the rest out (customizable,
        // searchable and not private), and an --include'd optional catalog table is never a candidate.
        _candidates = tables.Where(t => !t.IsCustom && (t.TableType is "Standard" or "Activity") && unmigrated.Contains(t.LogicalName)
                && ExclusionCatalog.Find(t.LogicalName) is null && t.IsCustomizable && t.IsValidForAdvancedFind && !t.IsPrivate
                && counts.GetValueOrDefault(t.LogicalName) > 0)
            .Select(t => t.LogicalName).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The standard tables with data that xrmverse does not have and that are not exported as archive candidates: one warning per table
    /// with records, and one line for the tables Dataverse would not count (a refused batch), so the owner sees what stays behind.
    /// </summary>
    private async Task<IReadOnlyList<PackageWarning>> NotInXrmverseAsync(IReadOnlyList<SourceTable> tables, CancellationToken ct)
    {
        var (counts, uncounted) = await CountUnmigratedAsync(tables, ct);
        var warnings = new List<PackageWarning>();
        foreach (var name in Unmigrated(tables).Where(n => counts.TryGetValue(n, out var c) && c != 0 && !_candidates.Contains(n)))
            warnings.Add(new PackageWarning("table-not-in-xrmverse", $"{name}: {counts[name]} record(s) not migrated (the table does not exist in xrmverse)"));
        if (uncounted.Count > 0)
            warnings.Add(new PackageWarning("table-not-in-xrmverse",
                $"{uncounted.Count} table(s) that do not exist in xrmverse were not read and could not be counted: {string.Join(", ", uncounted)}"));
        return warnings;
    }

    private async Task<Dictionary<string, long>> CountAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync($"RetrieveTotalRecordCount(EntityNames=@p)?@p={Uri.EscapeDataString(JsonSerializer.Serialize(names))}", ct);
        var collection = doc.RootElement.GetProperty("EntityRecordCountCollection");
        var keys = collection.GetProperty("Keys").EnumerateArray().Select(k => k.GetString()!).ToList();
        var values = collection.GetProperty("Values").EnumerateArray().Select(v => v.GetInt64()).ToList();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(keys.Count, values.Count); i++) counts[keys[i]] = values[i];
        return counts;
    }

    private async Task<IReadOnlyList<PackageWarning>> NotSupportedYetAsync(IReadOnlyList<SourceTable> tables, CancellationToken ct)
    {
        var names = tables.Select(t => t.LogicalName).Where(n => ExclusionCatalog.Find(n)?.Category == ExclusionCatalog.NotSupportedYet && !IsMapped(n))
            .Order(StringComparer.Ordinal).ToList();
        if (names.Count == 0) return [];
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            using var doc = await client.GetJsonAsync($"RetrieveTotalRecordCount(EntityNames=@p)?@p={JsonSerializer.Serialize(names)}", ct);
            var collection = doc.RootElement.GetProperty("EntityRecordCountCollection");
            var keys = collection.GetProperty("Keys").EnumerateArray().Select(k => k.GetString()!).ToList();
            var values = collection.GetProperty("Values").EnumerateArray().Select(v => v.GetInt64()).ToList();
            for (var i = 0; i < Math.Min(keys.Count, values.Count); i++) counts[keys[i]] = values[i];
        }
        catch (DataverseException)
        {
            // Counts are informative only.
        }
        return [.. names.Where(n => counts.GetValueOrDefault(n, -1) != 0).Select(n => new PackageWarning(ExclusionCatalog.NotSupportedYet,
            counts.TryGetValue(n, out var c) ? $"{n}: {c} record(s) not migrated (not supported yet by xrmverse)" : $"{n}: records not migrated (not supported yet by xrmverse)"))];
    }
}
