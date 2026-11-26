using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin.Migration.Import;
using Xrmd.Admin.Migration.Mapping;
using Xrmd.Migration.Mapping;

namespace Xrmd.Admin.Migration;

/// <summary>
/// Phase 1 (migration spec §7.6): every entry of the package matches its checksum in the manifest. With maps, then the map check
/// (R9, table mapping spec §8.2, the spec's "phase 1b": kept in phase 1 so that runs started by an older xrmd resume with the same
/// phase numbers) and the census of the translation (R10, <see cref="MapCensus"/>).
/// </summary>
public sealed class VerifyPackagePhase : IMigrationPhase
{
    public const int PhaseOrder = 1;

    /// <summary>The most problems listed in the run's message.</summary>
    public const int MaxListed = 10;

    public int Order => PhaseOrder;

    public string Name => "verify";

    /// <exception cref="MigrationPhaseException">An entry is missing or does not match its checksum, or the maps do not fit the organization.</exception>
    public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await context.Package.VerifyAsync(null, ct);
        if (result.Problems.Count > 0)
            throw new MigrationPhaseException("The package is damaged: " + List(result.Problems.Select(p => $"{p.Path}: {p.Problem}").ToList()));
        context.Progress.Report($"Verified {result.EntriesChecked} entries ({result.BytesChecked} bytes).");

        var maps = await MigrationMaps.LoadAsync(context.Package, context.Options, ct);
        if (maps.IsEmpty) return;
        // The tables an archive may be made of: the package's candidates, and those a map of the export archived already (their rows and
        // column details were exported for it, spec §8.7).
        var (packageMaps, catalog) = await PackageMaps.ReadAsync(context.Package, ct);
        var archivable = context.Package.RawManifest.ArchiveCandidates.Select(t => t.LogicalName)
            .Concat(packageMaps.SelectMany(f => f.Tables).Where(t => t.Archive).Select(t => t.Source)).ToList();
        // The source columns whose schema the package holds: all custom columns of a table its own maps renamed, and the delta's.
        var renamedAtExport = packageMaps.SelectMany(f => f.Tables).Where(t => t.IsRenamed && !t.Archive).Select(t => t.Source)
            .ToHashSet(StringComparer.Ordinal);
        var deltaColumns = await SchemaDeltaApplier.DeltaColumnsAsync(context.Package, ct);
        var problems = await MapCheck.RunAsync(maps, catalog, context.Services, ct, archivable,
            table => context.Store.IsChunkDoneAsync(context.RunId, ArchiveSchema.ChunkKey(table), ct),
            (table, column) => renamedAtExport.Contains(table) || deltaColumns.Contains((table, column)));
        await RecordMapCheckAsync(context, problems, ct);
        if (problems.Count > 0) throw new MigrationPhaseException("The map does not fit this organization: " + List(problems));
        await MapCensus.RunAsync(context, ct);
    }

    /// <summary>
    /// Replaces the run's <c>map-check</c> issues with <paramref name="problems"/> in one transaction: a resumed run checks again, and
    /// keeps only what is still wrong (none when the organization was fixed meanwhile, so no stale error marks the run).
    /// </summary>
    private static async Task RecordMapCheckAsync(MigrationPhaseContext context, IReadOnlyList<string> problems, CancellationToken ct)
    {
        await using var conn = await context.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        await using (var cmd = conn.Command("DELETE FROM meta.migration_issue WHERE runid = @run AND phase = @phase AND code = 'map-check'", tx))
        {
            cmd.AddParameter("@run", DbType.Guid, context.RunId);
            cmd.AddParameter("@phase", DbType.Int32, PhaseOrder);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var session = new DataSession(conn, tx);
        foreach (var problem in problems)
            await context.Ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Error, "map-check", null, null, null, problem), session, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>The first <see cref="MaxListed"/> problems, joined, and how many more.</summary>
    private static string List(IReadOnlyList<string> problems) =>
        string.Join("; ", problems.Take(MaxListed)) + (problems.Count > MaxListed ? $"; and {problems.Count - MaxListed} more." : ".");
}
