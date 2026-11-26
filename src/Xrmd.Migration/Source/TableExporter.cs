using System.Security;
using System.Text;
using System.Text.Json;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Migration.Source;

/// <summary>Puts the content of a file, image or note body column in the package and returns the value that points to it.</summary>
public interface IFileExporter
{
    /// <returns>The value for the column, <see cref="NullValue"/> when it is empty, or null to leave the column out.</returns>
    Task<MigrationValue?> ExportAsync(SourceTable table, SourceColumn column, Guid id, JsonElement row, CancellationToken ct);
}

/// <summary>The source refused the first page of a table (no access, or a table that cannot be queried): a warning, not a stop.</summary>
public sealed class TableNotReadableException(string table, DataverseException inner) : Exception($"{table}: {inner.Message}", inner)
{
    public string Table { get; } = table;
}

/// <summary>A file, image or note body that the source no longer has (deleted after its record was read).</summary>
public sealed class SourceFileMissingException(string path, Exception inner) : Exception($"{path} is no longer in the source.", inner);

/// <summary>
/// Where the export of one table stands: <see cref="LastId"/> is the last record of the last committed chunk.
/// <see cref="LookupsOutsideScope"/> carries the counts of <see cref="TableExporter.LookupTargetsOutsideScope"/> across a resume.
/// </summary>
public sealed record TableProgress(string Table, Guid? LastId, IReadOnlyList<string> Chunks, long Records, bool Done,
    IReadOnlyDictionary<string, long>? LookupsOutsideScope = null, IReadOnlyDictionary<string, long>? UnresolvedLookups = null,
    IReadOnlyDictionary<string, long>? MissingFiles = null, IReadOnlyDictionary<string, long>? InvalidValues = null);

/// <summary>
/// Exports the records of one table with keyset paging on the primary key (FetchXML, 5000 per page). Each page becomes one
/// committed chunk, followed by a checkpoint, so a resumed export starts after the last checkpoint and rewrites at most one page.
/// </summary>
/// <param name="tableOfCode">The source table of an object type code (see <see cref="ValueMapper.Map"/>); null keeps the numbers.</param>
public sealed class TableExporter(DataverseClient client, PackageWriter writer, IFileExporter files, Func<int, string?>? tableOfCode = null)
{
    internal const int PageSize = 5000;
    private const string NoteTable = "annotation";
    private const string NoteBody = "documentbody";

    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
    private readonly HashSet<string> _firstParty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _outside = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _unresolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _missing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _invalid = new(StringComparer.Ordinal);

    /// <summary>Columns with values of another JSON type than their metadata says, with how many were left out.</summary>
    public IReadOnlyDictionary<string, long> InvalidValues => _invalid;

    /// <summary>Columns of Microsoft first-party apps (<c>msdyn_</c> …) on the last exported table: not exported.</summary>
    public IReadOnlySet<string> FirstPartyColumns => _firstParty;

    /// <summary>Lookup columns whose target table the source did not tell, with how many values were left out.</summary>
    public IReadOnlyDictionary<string, long> UnresolvedLookups => _unresolved;

    /// <summary>File, image and note body columns whose content the source no longer had, with how many (written as empty).</summary>
    public IReadOnlyDictionary<string, long> MissingFiles => _missing;

    /// <summary>Columns of the last exported table whose type the package does not carry.</summary>
    public IReadOnlySet<string> SkippedColumns => _skipped;

    /// <summary>Lookup columns pointing to tables the migration leaves out, with how many values do (the values are kept).</summary>
    public IReadOnlyDictionary<string, long> LookupTargetsOutsideScope => _outside;

    /// <param name="keepFirstParty">First-party-prefixed columns of this table to export anyway (the ones a mapping file names).</param>
    /// <param name="lookupNames">A table an archive may be made from: each lookup also gets its name (see <see cref="ValueMapper.Map"/>).</param>
    public async Task<TableProgress> ExportAsync(SourceTable table, string prefix, TableProgress? resume, Action<TableProgress> checkpoint, CancellationToken ct,
        IReadOnlySet<string>? keepFirstParty = null, bool lookupNames = false)
    {
        _skipped.Clear();
        _firstParty.Clear();
        _outside.Clear();
        _unresolved.Clear();
        _missing.Clear();
        _invalid.Clear();
        foreach (var c in table.Columns.Where(c => !ValueMapper.IsSupported(c.AttributeType))) _skipped.Add(c.LogicalName);
        foreach (var c in table.Columns.Where(c => ExclusionCatalog.IsFirstParty(c.LogicalName) && keepFirstParty?.Contains(c.LogicalName) != true)) _firstParty.Add(c.LogicalName);
        Load(_outside, resume?.LookupsOutsideScope);
        Load(_unresolved, resume?.UnresolvedLookups);
        Load(_missing, resume?.MissingFiles);
        Load(_invalid, resume?.InvalidValues);
        if (resume is { Done: true }) return resume;
        table = table with { Columns = [.. table.Columns.Where(c => !_firstParty.Contains(c.LogicalName))] };

        var progress = resume ?? new TableProgress(table.LogicalName, null, [], 0, false);
        var isNote = table.LogicalName == NoteTable;
        var fileColumns = table.Columns.Where(c => c.AttributeType is "File" or "Image").ToList();
        var noteBody = isNote ? table.Columns.FirstOrDefault(c => c.LogicalName == NoteBody) : null;
        while (true)
        {
            IReadOnlyList<JsonElement> rows;
            try
            {
                rows = await client.FetchAsync(table.EntitySetName, FetchXml(table, isNote, progress.LastId), ct);
            }
            catch (DataverseException e) when (progress.LastId is null && progress.Chunks.Count == 0
                && e.Status is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound
                    or System.Net.HttpStatusCode.MethodNotAllowed)
            {
                // 405: Dataverse refuses queries on some system tables ("This action is not supported").
                // Only the first page: a refusal after data was read is a real failure and stops the export (resumable).
                throw new TableNotReadableException(table.LogicalName, e);
            }
            if (rows.Count == 0) break;

            using (var chunk = new ChunkWriter(writer, prefix, int.MaxValue, progress.Chunks.Count + 1))
            {
                foreach (var row in rows)
                {
                    var id = row.GetProperty(table.PrimaryKey).GetGuid();
                    var downloaded = new Dictionary<string, MigrationValue?>(StringComparer.Ordinal);
                    foreach (var column in fileColumns)
                        if (row.TryGetProperty(column.LogicalName, out var v) && v.ValueKind != JsonValueKind.Null)
                            downloaded[column.LogicalName] = await FileAsync(table, column, id, row, ct);
                    var record = ValueMapper.Map(table, row, (c, _) => downloaded.GetValueOrDefault(c.LogicalName),
                        column => _unresolved[column] = _unresolved.GetValueOrDefault(column) + 1,
                        column => _invalid[column] = _invalid.GetValueOrDefault(column) + 1, tableOfCode, lookupNames);
                    if (noteBody is not null) record = WithNoteBody(record, await NoteBodyAsync(table, noteBody, id, row, ct));
                    Count(record);
                    chunk.Add(record);
                }
                chunk.Complete();
                progress = progress with
                {
                    LastId = rows[^1].GetProperty(table.PrimaryKey).GetGuid(),
                    Chunks = [.. progress.Chunks, .. chunk.Chunks],
                    Records = progress.Records + chunk.Count,
                    LookupsOutsideScope = new Dictionary<string, long>(_outside, StringComparer.Ordinal),
                    UnresolvedLookups = new Dictionary<string, long>(_unresolved, StringComparer.Ordinal),
                    MissingFiles = new Dictionary<string, long>(_missing, StringComparer.Ordinal),
                    InvalidValues = new Dictionary<string, long>(_invalid, StringComparer.Ordinal),
                };
            }
            checkpoint(progress);
            if (rows.Count < PageSize) break;
        }
        progress = progress with { Done = true };
        checkpoint(progress);
        return progress;
    }

    private async Task<MigrationValue> NoteBodyAsync(SourceTable table, SourceColumn column, Guid id, JsonElement row, CancellationToken ct) =>
        row.TryGetProperty("isdocument", out var d) && d.ValueKind == JsonValueKind.True
            ? await FileAsync(table, column, id, row, ct) ?? NullValue.Instance
            : NullValue.Instance;

    private async Task<MigrationValue?> FileAsync(SourceTable table, SourceColumn column, Guid id, JsonElement row, CancellationToken ct)
    {
        try
        {
            return await files.ExportAsync(table, column, id, row, ct);
        }
        catch (SourceFileMissingException)
        {
            _missing[column.LogicalName] = _missing.GetValueOrDefault(column.LogicalName) + 1;
            return NullValue.Instance;
        }
    }

    private static void Load(Dictionary<string, long> into, IReadOnlyDictionary<string, long>? from)
    {
        foreach (var (k, v) in from ?? new Dictionary<string, long>()) into[k] = v;
    }

    private static MigrationRecord WithNoteBody(MigrationRecord record, MigrationValue body) =>
        record with { Values = new Dictionary<string, MigrationValue>(record.Values, StringComparer.Ordinal) { [NoteBody] = body } };

    private void Count(MigrationRecord record)
    {
        foreach (var (column, value) in record.Values)
            if (value is LookupValue lookup && ExclusionCatalog.Find(lookup.Table) is not null)
                _outside[column] = _outside.GetValueOrDefault(column) + 1;
    }

    /// <summary>The page query; note bodies are left out of the select (they are downloaded one by one).</summary>
    internal static string FetchXml(SourceTable table, bool explicitColumns, Guid? after)
    {
        var xml = new StringBuilder();
        xml.Append($"<fetch count=\"{PageSize}\" no-lock=\"true\"><entity name=\"{Escape(table.LogicalName)}\">");
        if (explicitColumns)
            foreach (var c in table.Columns.Where(c => c.LogicalName != NoteBody && ValueMapper.IsSupported(c.AttributeType)))
                xml.Append($"<attribute name=\"{Escape(c.LogicalName)}\"/>");
        else
            xml.Append("<all-attributes/>");
        xml.Append($"<order attribute=\"{Escape(table.PrimaryKey)}\"/>");
        if (after is { } last)
            xml.Append($"<filter><condition attribute=\"{Escape(table.PrimaryKey)}\" operator=\"gt\" value=\"{last}\"/></filter>");
        return xml.Append("</entity></fetch>").ToString();
    }

    private static string Escape(string name) => SecurityElement.Escape(name);
}
