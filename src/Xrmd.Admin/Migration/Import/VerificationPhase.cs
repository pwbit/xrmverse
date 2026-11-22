using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Phase 10 of an import (spec §7.6 step 10, plan M5 task 1), read only: every record of the package's data tables is looked for in
/// the organization (in batches of 1 000 ids), the first <c>verifySample</c> records of each table are compared column by column with
/// what the package says (after the same mapping the records phase applies, and a normalization of dates, decimals and option sets),
/// and every file of the package is read back and compared with the package's SHA-256. What differs is an error issue
/// (<c>verify-missing</c>, <c>verify-mismatch</c>, <c>verify-file-missing</c>, <c>verify-file-mismatch</c>), so the run ends
/// <c>completed_with_errors</c> and keeps the lock (spec §7.7). Each table, then the files, is marked in the ledger once checked.
/// </summary>
public sealed class VerificationPhase : IMigrationPhase
{
    public const int PhaseOrder = 10;

    public const int DefaultSample = 20;

    private const int Batch = 1000;

    public int Order => PhaseOrder;

    public string Name => "verification";

    public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.Services;
        var metadata = services.GetRequiredService<IMetadataStore>();
        var factory = services.GetRequiredService<IDbConnectionFactory>();
        var core = services.GetRequiredService<CoreOperations>();
        var package = context.Package;
        var manifest = package.Manifest;
        var sample = context.Options.TryGetValue("verifySample", out var text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : DefaultSample;
        var idMap = await context.Ledger.LoadMapAsync(ct);
        var builder = new ImportRecordBuilder(metadata, idMap, new HashSet<string>(manifest.Tables.Select(t => t.LogicalName), StringComparer.Ordinal), false);

        foreach (var table in RecordsPhase.DataTables(manifest).OrderBy(t => t.LogicalName, StringComparer.Ordinal))
        {
            var key = $"verification/{table.LogicalName}";
            if (await context.Store.IsChunkDoneAsync(context.RunId, key, ct)) continue;
            if (metadata.GetEntity(table.LogicalName) is null)
            {
                await MarkAsync(factory, context, key, ct);
                continue; // reported by the records phase (table-missing)
            }
            context.Progress.Report($"verification: {table.LogicalName}");
            await using var conn = await factory.OpenAsync(ct);
            var s = new DataSession(conn, null);
            var failed = await FailedRowsAsync(s, context.RunId, table.LogicalName, ct);
            long exported = 0, present = 0;
            var sampled = new List<MigrationRecord>();
            var batch = new List<MigrationRecord>(Batch);
            async Task CheckAsync()
            {
                var found = (await ImportRecordBuilder.ExistingAsync(s, metadata, batch.Select(r => (table.LogicalName, builder.Map(r.Id))), ct))
                    .Select(f => f.Id).ToHashSet();
                foreach (var r in batch)
                {
                    if (found.Contains(builder.Map(r.Id))) present++;
                    else if (!failed.Contains(r.Id) && !failed.Contains(builder.Map(r.Id)))
                        await context.Ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Error, "verify-missing", table.LogicalName, r.Id, null,
                            $"The {table.LogicalName} {r.Id} of the package is not in the organization."), ct);
                }
                batch.Clear();
            }
            await foreach (var record in ChunkReader.ReadAsync(package, table.Chunks, ct))
            {
                exported++;
                if (sampled.Count < sample) sampled.Add(record);
                batch.Add(record);
                if (batch.Count == Batch) await CheckAsync();
            }
            if (batch.Count > 0) await CheckAsync();

            var compared = 0;
            if (sampled.Count > 0)
            {
                builder.Reset();
                await builder.LoadAsync(s, table.LogicalName, sampled, ct);
                foreach (var record in sampled)
                {
                    // A row mapped onto one the organization already had (base currency, root business unit, …) keeps the
                    // organization's values: it counts as present, but there is nothing of the package's to compare.
                    if (builder.Map(record.Id) != record.Id || !builder.Exists(table.LogicalName, record.Id)) continue;
                    compared++;
                    await CompareAsync(context, core, metadata, s, table.LogicalName, record, builder, ct);
                }
            }
            await context.Ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Info, "verify-table", table.LogicalName, null, null,
                $"{table.LogicalName}: {exported} exported, {present} present, {compared} sampled"), ct);
            await MarkAsync(factory, context, key, ct);
        }

        if (!await context.Store.IsChunkDoneAsync(context.RunId, "verification/files", ct))
        {
            await VerifyFilesAsync(context, services, metadata, factory, idMap, ct);
            await MarkAsync(factory, context, "verification/files", ct);
        }
    }

    /// <summary>The ids of the rows of <paramref name="table"/> that a phase reported as failed (an error issue on the record).</summary>
    private static async Task<HashSet<Guid>> FailedRowsAsync(DataSession s, Guid runId, string table, CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        await using var cmd = s.Command("""
            SELECT DISTINCT recordid FROM meta.migration_issue
            WHERE runid = @run AND tablename = @table AND severity = 'error' AND recordid IS NOT NULL AND code NOT LIKE 'verify-%'
            """);
        cmd.AddParameter("@run", DbType.Guid, runId);
        cmd.AddParameter("@table", DbType.String, table, 128);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return ids;
    }

    private static async Task CompareAsync(MigrationPhaseContext context, CoreOperations core, IMetadataStore metadata, DataSession s, string table,
        MigrationRecord record, ImportRecordBuilder builder, CancellationToken ct)
    {
        BuiltRecord expected;
        try { expected = builder.Build(table, record); }
        catch (ImportValueException) { return; } // the records phase reported the row
        var attrs = (metadata.GetEntity(table)!.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        // Party lists are written through their activity's parties and compared there: not here.
        var columns = expected.Entity.Attributes.Where(a => a.Value is not EntityCollection && attrs.ContainsKey(a.Key)).Select(a => a.Key).ToArray();
        if (columns.Length == 0) return;
        Entity actual;
        try { actual = await core.RetrieveAsync(s, Caller.Internal, table, expected.Entity.Id, new ColumnSet(columns), ct); }
        catch (XrmdFaultException ex)
        {
            await context.Ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Error, "verify-mismatch", table, record.Id, null,
                $"The {table} {record.Id} could not be read back: {ex.Message}"), ct);
            return;
        }
        foreach (var column in columns)
        {
            var want = expected.Entity[column];
            var got = actual.Contains(column) ? actual[column] : null;
            if (want is null && FilledOnCreate.Contains(column)) continue;
            if (Same(attrs[column], want, got)) continue;
            await context.Ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Error, "verify-mismatch", table, record.Id, column,
                $"{table}.{column} of {record.Id}: expected {Show(want)}, found {Show(got)}."), ct);
        }
    }

    /// <summary>
    /// The columns the core fills on create when they are empty, as Dataverse does (the base currency and its rate, the caller):
    /// an empty exported value there is not a mismatch (M6 run).
    /// </summary>
    private static readonly HashSet<string> FilledOnCreate = new(StringComparer.Ordinal)
    {
        "transactioncurrencyid", "exchangerate", "createdby", "modifiedby",
    };

    /// <summary>Whether a value the import wrote reads back the same, after the normalization a column's storage applies.</summary>
    internal static bool Same(AttributeMetadata column, object? expected, object? actual)
    {
        if (expected is null || actual is null) return expected is null && actual is null;
        switch (expected)
        {
            case EntityReference e when actual is EntityReference a:
                return e.Id == a.Id;
            case Money e when actual is Money a:
                return Round(e.Value, column) == Round(a.Value, column);
            case decimal e when actual is decimal a:
                return Round(e, column) == Round(a, column);
            case double e when actual is double a:
                return Math.Abs(e - a) <= 1e-9 * Math.Max(1, Math.Abs(e));
            case DateTime e when actual is DateTime a:
                return Seconds(e) == Seconds(a);
            case OptionSetValueCollection e when actual is OptionSetValueCollection a:
                return e.Select(o => o.Value).ToHashSet().SetEquals(a.Select(o => o.Value));
            case OptionSetValue e when actual is OptionSetValue a:
                return e.Value == a.Value;
            default:
                return Equals(expected, actual);
        }
    }

    private static decimal Round(decimal value, AttributeMetadata column)
    {
        var precision = column switch
        {
            MoneyAttributeMetadata m => m.Precision ?? 2,
            DecimalAttributeMetadata d => d.Precision ?? 2,
            _ => 10,
        };
        return Math.Round(value, Math.Clamp(precision, 0, 10), MidpointRounding.AwayFromZero);
    }

    /// <summary>A date and time to the second, in UTC (a value with no kind is taken as UTC, as the store keeps it).</summary>
    private static DateTime Seconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    private static string Show(object? value) => value switch
    {
        null => "null",
        EntityReference r => $"{r.LogicalName} {r.Id}",
        Money m => m.Value.ToString(CultureInfo.InvariantCulture),
        OptionSetValue o => o.Value.ToString(CultureInfo.InvariantCulture),
        OptionSetValueCollection c => "[" + string.Join(",", c.Select(o => o.Value).OrderBy(v => v)) + "]",
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => $"'{value}'",
    };

    /// <summary>Every <c>files/…</c> entry: the content the organization holds against the package's SHA-256.</summary>
    private static async Task VerifyFilesAsync(MigrationPhaseContext context, IServiceProvider services, IMetadataStore metadata, IDbConnectionFactory factory,
        IReadOnlyDictionary<Guid, Guid> idMap, CancellationToken ct)
    {
        var files = services.GetRequiredService<FileColumnService>();
        var core = services.GetRequiredService<CoreOperations>();
        var entries = context.Package.Manifest.Entries.Where(e => e.Path.StartsWith("files/", StringComparison.Ordinal)).ToList();
        if (entries.Count > 0) context.Progress.Report($"verification: {entries.Count} files");
        foreach (var entry in entries)
        {
            var parts = entry.Path.Split('/');
            if (parts.Length != 4 || !Guid.TryParse(parts[3], out var sourceId)) continue;
            var (table, column) = (parts[1], parts[2]);
            if (metadata.GetEntity(table) is not { } m || m.Attributes?.FirstOrDefault(a => a.LogicalName == column) is not { } attribute) continue;
            var target = new EntityReference(table, ImportValueMapper.Map(idMap, sourceId));
            await using var conn = await factory.OpenAsync(ct);
            var s = new DataSession(conn, null);
            // A record that is not there is reported by the presence check, or as a failed row.
            if ((await ImportRecordBuilder.ExistingAsync(s, metadata, [(table, target.Id)], ct)).Count == 0) continue;
            byte[]? content;
            if (attribute is ImageAttributeMetadata or FileAttributeMetadata)
                content = await DownloadAsync(files, context.Caller, target, column, s, ct);
            else
            {
                var read = await core.RetrieveAsync(s, Caller.Internal, table, target.Id, new ColumnSet(column), ct);
                content = read.GetAttributeValue<string>(column) is { Length: > 0 } body ? Convert.FromBase64String(body) : null;
            }
            if (content is null)
            {
                if (attribute is ImageAttributeMetadata && await HasImageAsync(core, s, table, target.Id, column, ct)) continue; // no full image kept
                await context.Ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Error, "verify-file-missing", table, sourceId, column,
                    $"The file {entry.Path} of the package is not in the organization."), ct);
            }
            else if (Convert.ToHexStringLower(SHA256.HashData(content)) != entry.Sha256)
                await context.Ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Error, "verify-file-mismatch", table, sourceId, column,
                    $"The file {entry.Path} differs from the package ({content.Length} bytes in the organization, {entry.Bytes} in the package)."), ct);
        }
    }

    /// <summary>The content of a file or full image column, or null when the column holds none.</summary>
    private static async Task<byte[]?> DownloadAsync(FileColumnService files, Caller caller, EntityReference target, string column, DataSession s, CancellationToken ct)
    {
        string token;
        long size;
        try { (token, size, _, _) = await files.InitializeDownloadAsync(caller, target, column, s, ct); }
        catch (XrmdFaultException ex) when (ex.HttpStatus == 404) { return null; }
        try
        {
            using var content = new MemoryStream();
            for (long offset = 0; offset < size; offset += FileColumnService.MaxBlockSize)
            {
                await using var block = await files.DownloadBlockAsync(caller, token, offset, Math.Min(FileColumnService.MaxBlockSize, size - offset), s, ct);
                await block.CopyToAsync(content, ct);
            }
            return content.ToArray();
        }
        finally
        {
            await files.EndDownloadAsync(caller, token);
        }
    }

    private static async Task<bool> HasImageAsync(CoreOperations core, DataSession s, string table, Guid id, string column, CancellationToken ct)
    {
        var read = await core.RetrieveAsync(s, Caller.Internal, table, id, new ColumnSet(column + "id"), ct);
        return read.Contains(column + "id") && read[column + "id"] is not null;
    }

    private static async Task MarkAsync(IDbConnectionFactory factory, MigrationPhaseContext context, string key, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        await context.Store.MarkChunkAsync(context.RunId, PhaseOrder, key, 1, 0, new DataSession(conn, tx), ct);
        await tx.CommitAsync(ct);
    }
}
