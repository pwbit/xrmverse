using System.Data;
using Microsoft.AspNetCore.StaticFiles;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Files;

/// <summary>The body of a note as stored: its size in bytes, its MIME type and the <c>fileattachment</c> row that holds it.</summary>
public sealed record AnnotationBody(long Size, string MimeType, Guid FileAttachmentId);

/// <summary>
/// The bodies of notes (<c>annotation.documentbody</c>, plan 2.11, ruling A16, decision D3). A body is never a SQL column: it is a
/// <c>fileattachment</c> row (<c>objectid</c> the note, <c>regardingfieldname</c> <c>documentbody</c>, <c>filename</c>,
/// <c>mimetype</c>, <c>filesizeinbytes</c>) and the blob <c>{database}/{fileattachmentid}</c> of the file store, written with the
/// rules of a file column commit (<see cref="FileColumnService"/>): the new blob is written before the transaction commits and
/// deleted if it rolls back, a replaced or cleared body loses its row in the transaction and its blob after the commit. Because the
/// row exists, the 2.2 cleanup sweep never takes the blob for an orphan, the delete of the note (and of the record the note is
/// attached to, by the Delete cascade) releases it, and backups and restores carry it with the other files.
/// </summary>
/// <remarks>
/// Every method runs on the caller's <see cref="DataSession"/> and checks nothing about the caller: the Core calls it after its own
/// checks (the note's privileges on write; on read, only for the notes the caller's query returned).
/// </remarks>
public sealed class AnnotationBodies
{
    /// <summary>The attribute of the note that names its body, and the <c>regardingfieldname</c> of the body's row.</summary>
    public const string Attribute = ActivitySchema.DocumentBody;

    private const string OctetStream = "application/octet-stream";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly FileColumnService _files;

    internal AnnotationBodies(FileColumnService files) => _files = files;

    /// <summary>
    /// The MIME type of a body whose note gives none: the one of the file name's extension, <c>application/octet-stream</c> for
    /// an unknown extension or no file name (from memory: checklist 3g).
    /// </summary>
    public static string MimeTypeOf(string? fileName) =>
        fileName is { Length: > 0 } && ContentTypes.TryGetContentType(fileName, out var type) ? type : OctetStream;

    /// <summary>
    /// Decodes <paramref name="base64"/>, checks it against <paramref name="maxBytes"/> and stores it as the body of the note
    /// <paramref name="annotationId"/>, replacing its current body (released: row now, blob after the commit). The note's row need
    /// not exist yet (a Create writes the body before it).
    /// </summary>
    /// <param name="mimeType">The note's <c>mimetype</c>; when null or empty, <see cref="MimeTypeOf"/> the file name.</param>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for text that is not base64; 400 0x80044a02 AttachmentInvalidFileSize
    /// for a body over <paramref name="maxBytes"/>.</exception>
    public async Task<AnnotationBody> WriteAsync(DataSession s, Guid annotationId, string base64, string? fileName, string? mimeType, int maxBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(base64);
        // The decoded length, from the text's length, is checked before decoding (a body far over the limit is never decoded).
        var decoded = DecodedLength(base64);
        if (decoded > maxBytes) throw XrmdFaultException.NoteBodyTooBig(decoded, maxBytes);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw XrmdFaultException.InvalidArgument(
                $"Attribute '{Attribute}' of entity '{ActivitySchema.Annotation}' is not valid base64: a note body is sent as base64 text.");
        }
        if (bytes.LongLength > maxBytes) throw XrmdFaultException.NoteBodyTooBig(bytes.LongLength, maxBytes);
        var type = string.IsNullOrEmpty(mimeType) ? MimeTypeOf(fileName) : mimeType;
        var note = Annotation();

        await ClearAsync(s, annotationId, ct);
        var fileId = Guid.NewGuid();
        var key = FileColumnService.FileKey(s, fileId);
        s.OnRolledBack(() => _files.Enqueue(key));
        await _files.Store.WriteAsync(key, new MemoryStream(bytes, writable: false), ct);
        await _files.InsertAttachmentAsync(s, note, annotationId, Attribute, fileId, string.IsNullOrEmpty(fileName) ? null : fileName, type,
            bytes.LongLength, ct);
        return new AnnotationBody(bytes.LongLength, type, fileId);
    }

    /// <summary>The body of the note <paramref name="annotationId"/> as base64; null when it has none.</summary>
    /// <exception cref="XrmdFaultException">404 ObjectDoesNotExist when the body's blob is gone (replaced or deleted by a
    /// transaction that committed after this one read its row).</exception>
    public async Task<string?> ReadAsync(DataSession s, Guid annotationId, CancellationToken ct) =>
        (await ReadAsync(s, [annotationId], ct)).GetValueOrDefault(annotationId);

    /// <summary>
    /// The bodies of the notes <paramref name="annotationIds"/> as base64, by note; a note without a body is left out. The rows are
    /// read first with their sizes: when the bodies add up to more than <c>Files:MaxNoteBodyBytesPerRead</c>
    /// (<see cref="FileStoreOptions.MaxNoteBodyBytesPerRead"/>, controller ruling Y5) the read is refused before any blob is opened.
    /// A blob found missing (its body replaced or cleared by a transaction that committed after the row was read) is looked up once
    /// more by its note: no row any more means no body; a row whose blob is still missing is the 404.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument over the cap; 404 ObjectDoesNotExist for a body row whose blob is
    /// missing after the retry.</exception>
    public async Task<IReadOnlyDictionary<Guid, string>> ReadAsync(DataSession s, IReadOnlyCollection<Guid> annotationIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(annotationIds);
        var result = new Dictionary<Guid, string>();
        if (annotationIds.Count == 0 || _files.Metadata.GetEntity(FileColumns.AttachmentEntity) is null) return result;
        var d = s.Dialect;
        var rows = new List<(Guid Note, Guid File, long Size)>();
        await using (var cmd = s.Command(
                         $"SELECT {d.Quote(FileColumns.AttachmentLookup)}, fileattachmentid, filesizeinbytes FROM {s.DataTable(FileColumns.AttachmentEntity)} " +
                         $"WHERE {d.Quote(FileColumns.AttachmentLookup + TypeMapping.TypeColumnSuffix)} = @otc AND regardingfieldname = @field " +
                         $"AND {d.Quote(FileColumns.AttachmentLookup)} IN (SELECT CAST(value AS {d.ColumnType(SqlColumnKind.Guid)}) FROM {d.JsonArray("@ids")})"))
        {
            cmd.AddParameter("@otc", DbType.Int32, ObjectTypeCode());
            cmd.AddParameter("@field", DbType.String, Attribute, 255);
            cmd.AddParameter("@ids", DbType.String, System.Text.Json.JsonSerializer.Serialize(annotationIds.Distinct()), -1);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add((rd.GetGuid(0), rd.GetGuid(1), rd.IsDBNull(2) ? 0 : rd.GetInt64(2)));
        }
        // Known limit (checklist): a server-side bound on the memory of one query, not a Dataverse rule.
        var total = rows.Sum(r => r.Size);
        var cap = _files.Options.MaxNoteBodyBytesPerRead;
        if (total > cap)
            throw XrmdFaultException.InvalidArgument(
                $"This query would read {total} bytes of note bodies ('{Attribute}' of {rows.Count} notes) at once, more than this server " +
                $"reads in one query ({cap} bytes, Files:MaxNoteBodyBytesPerRead). Leave '{Attribute}' out of the ColumnSet or $select, " +
                "or read fewer notes per page (a smaller page size or count).");
        foreach (var (note, file, _) in rows)
            if (await ReadBodyAsync(s, note, file, ct) is { } body) result[note] = body;
        return result;
    }

    /// <summary>One body as base64; on a missing blob, the note's body row read again once (null when it is gone).</summary>
    private async Task<string?> ReadBodyAsync(DataSession s, Guid note, Guid file, CancellationToken ct)
    {
        try
        {
            return await ReadBlobAsync(s, file, ct);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.ObjectDoesNotExist)
        {
            var d = s.Dialect;
            await using var cmd = s.Command(d.Limit(
                $"SELECT fileattachmentid FROM {s.DataTable(FileColumns.AttachmentEntity)} " +
                $"WHERE {d.Quote(FileColumns.AttachmentLookup)} = @id AND {d.Quote(FileColumns.AttachmentLookup + TypeMapping.TypeColumnSuffix)} = @otc AND regardingfieldname = @field",
                "1"));
            cmd.AddParameter("@id", DbType.Guid, note);
            cmd.AddParameter("@otc", DbType.Int32, ObjectTypeCode());
            cmd.AddParameter("@field", DbType.String, Attribute, 255);
            return await cmd.ExecuteScalarAsync(ct) is Guid current ? await ReadBlobAsync(s, current, ct) : null;
        }
    }

    private async Task<string> ReadBlobAsync(DataSession s, Guid file, CancellationToken ct)
    {
        await using var blob = await _files.OpenBlobAsync(FileColumnService.FileKey(s, file), file, 0, null, ct);
        using var copy = new MemoryStream();
        await blob.CopyToAsync(copy, ct);
        return Convert.ToBase64String(copy.GetBuffer(), 0, (int)copy.Length);
    }

    /// <summary>
    /// The number of bytes <paramref name="base64"/> decodes to, from its length: whitespace (which base64 decoding skips) is not
    /// counted and trailing padding is taken off. Exact for valid base64; for other text an estimate (the decoding refuses it).
    /// </summary>
    private static long DecodedLength(string base64)
    {
        long characters = 0, padding = 0;
        foreach (var c in base64)
        {
            if (char.IsWhiteSpace(c)) continue;
            characters++;
            padding = c == '=' ? padding + 1 : 0;
        }
        return Math.Max(0, characters / 4 * 3 + characters % 4 * 3 / 4 - Math.Min(padding, 2));
    }

    /// <summary>Removes the body of the note <paramref name="annotationId"/>, if any: its row now, its blob after the commit.</summary>
    public async Task ClearAsync(DataSession s, Guid annotationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (_files.Metadata.GetEntity(FileColumns.AttachmentEntity) is null) return;
        foreach (var file in await BodyRowsAsync(s, annotationId, ct)) await _files.ReleaseAsync(s, file, ct);
    }

    /// <summary>
    /// Copies the note's new <c>filename</c> and <c>mimetype</c> to the row of its current body (an Update that renames the note
    /// without sending a new body). A null value leaves the column as it is.
    /// </summary>
    public async Task DescribeAsync(DataSession s, Guid annotationId, string? fileName, string? mimeType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (fileName is null && mimeType is null) return;
        if (_files.Metadata.GetEntity(FileColumns.AttachmentEntity) is null) return;
        var d = s.Dialect;
        await using var cmd = s.Command(
            $"UPDATE {s.DataTable(FileColumns.AttachmentEntity)} SET filename = COALESCE(@name, filename), mimetype = COALESCE(@type, mimetype) " +
            $"WHERE {d.Quote(FileColumns.AttachmentLookup)} = @id AND {d.Quote(FileColumns.AttachmentLookup + TypeMapping.TypeColumnSuffix)} = @otc AND regardingfieldname = @field");
        cmd.AddParameter("@name", DbType.String, (object?)fileName ?? DBNull.Value, -1);
        cmd.AddParameter("@type", DbType.String, (object?)mimeType ?? DBNull.Value, -1);
        cmd.AddParameter("@id", DbType.Guid, annotationId);
        cmd.AddParameter("@otc", DbType.Int32, ObjectTypeCode());
        cmd.AddParameter("@field", DbType.String, Attribute, 255);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The ids of the body rows of a note, update-locked for this transaction (one at most, by construction).</summary>
    private async Task<List<Guid>> BodyRowsAsync(DataSession s, Guid annotationId, CancellationToken ct)
    {
        // On the path of every note delete (activities, P11), so in the dialect's SQL (plan 2.7a, Task 6); the key lock of the
        // note's body stands for the key range a missing row leaves unlocked on PostgreSQL.
        var d = s.Dialect;
        await s.KeyLockAsync(FileColumns.AttachmentEntity, $"{annotationId:N}:{Attribute}", ct);
        await using var cmd = s.Command(
            $"SELECT fileattachmentid FROM {s.DataTable(FileColumns.AttachmentEntity)}{d.LockHint(RowLock.UpdateRange)} " +
            $"WHERE {d.Quote(FileColumns.AttachmentLookup)} = @id AND {d.Quote(FileColumns.AttachmentLookup + TypeMapping.TypeColumnSuffix)} = @otc " +
            $"AND regardingfieldname = @field{d.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter("@id", DbType.Guid, annotationId);
        cmd.AddParameter("@otc", DbType.Int32, ObjectTypeCode());
        cmd.AddParameter("@field", DbType.String, Attribute, 255);
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return ids;
    }

    private Microsoft.Xrm.Sdk.Metadata.EntityMetadata Annotation() =>
        _files.Metadata.GetEntity(ActivitySchema.Annotation)
        ?? throw new InvalidOperationException($"The table '{ActivitySchema.Annotation}' does not exist."); // fail closed

    private int ObjectTypeCode() =>
        Annotation().ObjectTypeCode ?? throw new InvalidOperationException($"The table '{ActivitySchema.Annotation}' has no object type code.");
}
