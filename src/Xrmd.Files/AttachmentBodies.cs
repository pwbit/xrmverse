using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Files;

/// <summary>The body of an email attachment as stored: its size in bytes, its MIME type and the <c>fileattachment</c> row that holds it.</summary>
public sealed record AttachmentBody(long Size, string MimeType, Guid FileAttachmentId);

/// <summary>
/// One attachment of an email as the send reads it (plan 4.1b): its file name, MIME type and the open stream of its body. The caller
/// disposes the stream. <see cref="ContentId"/> is null: the table has no content id column, so inline images are not modelled.
/// </summary>
public sealed record AttachmentContent(string FileName, string MimeType, string? ContentId, Stream Body);

/// <summary>
/// The bodies of email attachments (<c>activitymimeattachment.body</c>, plan 4.1b), kept as the bodies of notes are
/// (<see cref="AnnotationBodies"/>, ruling A16): never a SQL column, a <c>fileattachment</c> row (<c>objectid</c> the attachment,
/// <c>regardingfieldname</c> <c>body</c>) and the blob <c>{database}/{fileattachmentid}</c> of the file store, with the transaction
/// rules of a file column commit (the new blob is written before the commit and deleted if it rolls back; a replaced or cleared body
/// loses its row in the transaction and its blob after the commit). The delete of the attachment, and of its email by the Delete
/// cascade, releases it.
/// </summary>
/// <remarks>
/// Every method runs on the caller's <see cref="DataSession"/> and checks nothing about the caller: the Core calls it after its own
/// checks. The bodies of a query count toward <c>Files:MaxNoteBodyBytesPerRead</c>, as the note bodies do.
/// </remarks>
public sealed class AttachmentBodies
{
    /// <summary>The attribute of the attachment that names its body, and the <c>regardingfieldname</c> of the body's row.</summary>
    public const string Attribute = MailSchema.AttachmentBody;

    private readonly FileColumnService _files;

    internal AttachmentBodies(FileColumnService files) => _files = files;

    /// <summary>
    /// Decodes <paramref name="base64"/>, checks it against <paramref name="maxBytes"/> and stores it as the body of the attachment
    /// <paramref name="attachmentId"/>, replacing its current body. The attachment's row need not exist yet (a Create writes the body
    /// before it).
    /// </summary>
    /// <param name="mimeType">The attachment's <c>mimetype</c>; when null or empty, the one of the file name's extension.</param>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for text that is not base64; 400 0x80044a02 for a body over
    /// <paramref name="maxBytes"/>.</exception>
    public async Task<AttachmentBody> WriteAsync(DataSession s, Guid attachmentId, string base64, string? fileName, string? mimeType, int maxBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(base64);
        var decoded = AnnotationBodies.DecodedLength(base64);
        if (decoded > maxBytes) throw XrmdFaultException.AttachmentBodyTooBig(decoded, maxBytes);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw XrmdFaultException.InvalidArgument(
                $"Attribute '{Attribute}' of entity '{MailSchema.Attachment}' is not valid base64: an attachment body is sent as base64 text.");
        }
        if (bytes.LongLength > maxBytes) throw XrmdFaultException.AttachmentBodyTooBig(bytes.LongLength, maxBytes);
        var type = string.IsNullOrEmpty(mimeType) ? AnnotationBodies.MimeTypeOf(fileName) : mimeType;
        var table = Table();

        await ClearAsync(s, attachmentId, ct);
        var fileId = Guid.NewGuid();
        var key = FileColumnService.FileKey(s, fileId);
        s.OnRolledBack(() => _files.Enqueue(key));
        await _files.Store.WriteAsync(key, new MemoryStream(bytes, writable: false), ct);
        await _files.InsertAttachmentAsync(s, table, attachmentId, Attribute, fileId, string.IsNullOrEmpty(fileName) ? null : fileName, type,
            bytes.LongLength, ct);
        return new AttachmentBody(bytes.LongLength, type, fileId);
    }

    /// <summary>
    /// The bodies of the attachments <paramref name="attachmentIds"/> as base64, by attachment; one without a body is left out. The
    /// rows are read first with their sizes: bodies adding up to more than <c>Files:MaxNoteBodyBytesPerRead</c> are refused before
    /// any blob is opened. A blob found missing (its body replaced or cleared by a transaction that committed after the row was read)
    /// is looked up once more by its attachment.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument over the cap; 404 ObjectDoesNotExist for a body row whose blob is
    /// missing after the retry.</exception>
    public async Task<IReadOnlyDictionary<Guid, string>> ReadAsync(DataSession s, IReadOnlyCollection<Guid> attachmentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(attachmentIds);
        var result = new Dictionary<Guid, string>();
        if (attachmentIds.Count == 0 || _files.Metadata.GetEntity(FileColumns.AttachmentEntity) is null) return result;
        var d = s.Dialect;
        var rows = new List<(Guid Attachment, Guid File, long Size)>();
        await using (var cmd = s.Command(
                         $"SELECT {d.Quote(FileColumns.AttachmentLookup)}, fileattachmentid, filesizeinbytes FROM {s.DataTable(FileColumns.AttachmentEntity)} " +
                         $"WHERE {d.Quote(FileColumns.AttachmentLookup + TypeMapping.TypeColumnSuffix)} = @otc AND regardingfieldname = @field " +
                         $"AND {d.Quote(FileColumns.AttachmentLookup)} IN (SELECT CAST(value AS {d.ColumnType(SqlColumnKind.Guid)}) FROM {d.JsonArray("@ids")})"))
        {
            cmd.AddParameter("@otc", DbType.Int32, ObjectTypeCode());
            cmd.AddParameter("@field", DbType.String, Attribute, 255);
            cmd.AddParameter("@ids", DbType.String, System.Text.Json.JsonSerializer.Serialize(attachmentIds.Distinct()), -1);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add((rd.GetGuid(0), rd.GetGuid(1), rd.IsDBNull(2) ? 0 : rd.GetInt64(2)));
        }
        var total = rows.Sum(r => r.Size);
        var cap = _files.Options.MaxNoteBodyBytesPerRead;
        if (total > cap)
            throw XrmdFaultException.InvalidArgument(
                $"This query would read {total} bytes of attachment bodies ('{Attribute}' of {rows.Count} attachments) at once, more than this server " +
                $"reads in one query ({cap} bytes, Files:MaxNoteBodyBytesPerRead). Leave '{Attribute}' out of the ColumnSet or $select, " +
                "or read fewer attachments per page (a smaller page size or count).");
        foreach (var (attachment, file, _) in rows)
            if (await ReadBodyAsync(s, attachment, file, ct) is { } body) result[attachment] = body;
        return result;
    }

    /// <summary>
    /// The attachments of the email <paramref name="emailId"/> with their bodies, in the order of their <c>attachmentnumber</c> (then
    /// their file name): what the send attaches to the message. The streams are open on the file store; the caller disposes them. An
    /// attachment without a body has an empty stream. Reads as the session's connection, whatever the caller's rights (the send
    /// checked them).
    /// </summary>
    public async Task<IReadOnlyList<AttachmentContent>> ReadAllAsync(DataSession s, Guid emailId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var rows = new List<(Guid Id, string FileName, string? MimeType)>();
        if (_files.Metadata.GetEntity(MailSchema.Attachment) is not { } table) return [];
        var d = s.Dialect;
        await using (var cmd = s.Command(
                         $"SELECT {d.Quote(SystemAttributes.PrimaryIdName(table))}, {d.Quote(ActivitySchema.FileName)}, {d.Quote(ActivitySchema.MimeType)} " +
                         $"FROM {s.DataTable(MailSchema.Attachment)} WHERE {d.Quote(ActivitySchema.ObjectId)} = @email " +
                         $"AND {d.Quote(ActivitySchema.ObjectId + TypeMapping.TypeColumnSuffix)} = @otc " +
                         $"ORDER BY {d.Quote(MailSchema.AttachmentNumber)}, {d.Quote(ActivitySchema.FileName)}, {d.Quote(SystemAttributes.PrimaryIdName(table))}"))
        {
            cmd.AddParameter("@email", DbType.Guid, emailId);
            cmd.AddParameter("@otc", DbType.Int32, ActivitySchema.EmailObjectTypeCode);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add((rd.GetGuid(0), rd.IsDBNull(1) ? "" : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2)));
        }
        var files = new Dictionary<Guid, Guid>();
        foreach (var (id, file) in await BodyFilesAsync(s, rows.Select(r => r.Id).ToList(), ct)) files[id] = file;

        var result = new List<AttachmentContent>();
        try
        {
            foreach (var (id, fileName, mimeType) in rows)
            {
                Stream body = files.TryGetValue(id, out var file)
                    ? await _files.OpenBlobAsync(FileColumnService.FileKey(s, file), file, 0, null, ct)
                    : new MemoryStream([], writable: false);
                result.Add(new AttachmentContent(fileName, string.IsNullOrEmpty(mimeType) ? AnnotationBodies.MimeTypeOf(fileName) : mimeType, null, body));
            }
        }
        catch
        {
            foreach (var opened in result) await opened.Body.DisposeAsync();
            throw;
        }
        return result;
    }

    /// <summary>The <c>fileattachment</c> id of the body of each of <paramref name="attachmentIds"/> that has one.</summary>
    private async Task<List<(Guid Attachment, Guid File)>> BodyFilesAsync(DataSession s, IReadOnlyCollection<Guid> attachmentIds, CancellationToken ct)
    {
        var result = new List<(Guid, Guid)>();
        if (attachmentIds.Count == 0 || _files.Metadata.GetEntity(FileColumns.AttachmentEntity) is null) return result;
        var d = s.Dialect;
        await using var cmd = s.Command(
            $"SELECT {d.Quote(FileColumns.AttachmentLookup)}, fileattachmentid FROM {s.DataTable(FileColumns.AttachmentEntity)} " +
            $"WHERE {d.Quote(FileColumns.AttachmentLookup + TypeMapping.TypeColumnSuffix)} = @otc AND regardingfieldname = @field " +
            $"AND {d.Quote(FileColumns.AttachmentLookup)} IN (SELECT CAST(value AS {d.ColumnType(SqlColumnKind.Guid)}) FROM {d.JsonArray("@ids")})");
        cmd.AddParameter("@otc", DbType.Int32, ObjectTypeCode());
        cmd.AddParameter("@field", DbType.String, Attribute, 255);
        cmd.AddParameter("@ids", DbType.String, System.Text.Json.JsonSerializer.Serialize(attachmentIds.Distinct()), -1);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) result.Add((rd.GetGuid(0), rd.GetGuid(1)));
        return result;
    }

    /// <summary>One body as base64; on a missing blob, the attachment's body row read again once (null when it is gone).</summary>
    private async Task<string?> ReadBodyAsync(DataSession s, Guid attachment, Guid file, CancellationToken ct)
    {
        try
        {
            return await ReadBlobAsync(s, file, ct);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.ObjectDoesNotExist)
        {
            return await BodyFilesAsync(s, [attachment], ct) is [(_, var current)] ? await ReadBlobAsync(s, current, ct) : null;
        }
    }

    private async Task<string> ReadBlobAsync(DataSession s, Guid file, CancellationToken ct)
    {
        await using var blob = await _files.OpenBlobAsync(FileColumnService.FileKey(s, file), file, 0, null, ct);
        using var copy = new MemoryStream();
        await blob.CopyToAsync(copy, ct);
        return Convert.ToBase64String(copy.GetBuffer(), 0, (int)copy.Length);
    }

    /// <summary>Removes the body of the attachment <paramref name="attachmentId"/>, if any: its row now, its blob after the commit.</summary>
    public async Task ClearAsync(DataSession s, Guid attachmentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (_files.Metadata.GetEntity(FileColumns.AttachmentEntity) is null) return;
        foreach (var file in await BodyRowsAsync(s, attachmentId, ct)) await _files.ReleaseAsync(s, file, ct);
    }

    /// <summary>
    /// Copies the attachment's new <c>filename</c> and <c>mimetype</c> to the row of its current body (an Update that renames the
    /// attachment without sending a new body). A null value leaves the column as it is.
    /// </summary>
    public async Task DescribeAsync(DataSession s, Guid attachmentId, string? fileName, string? mimeType, CancellationToken ct)
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
        cmd.AddParameter("@id", DbType.Guid, attachmentId);
        cmd.AddParameter("@otc", DbType.Int32, ObjectTypeCode());
        cmd.AddParameter("@field", DbType.String, Attribute, 255);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The ids of the body rows of an attachment, update-locked for this transaction (one at most, by construction).</summary>
    private async Task<List<Guid>> BodyRowsAsync(DataSession s, Guid attachmentId, CancellationToken ct)
    {
        var d = s.Dialect;
        await s.KeyLockAsync(FileColumns.AttachmentEntity, $"{attachmentId:N}:{Attribute}", ct);
        await using var cmd = s.Command(
            $"SELECT fileattachmentid FROM {s.DataTable(FileColumns.AttachmentEntity)}{d.LockHint(RowLock.UpdateRange)} " +
            $"WHERE {d.Quote(FileColumns.AttachmentLookup)} = @id AND {d.Quote(FileColumns.AttachmentLookup + TypeMapping.TypeColumnSuffix)} = @otc " +
            $"AND regardingfieldname = @field{d.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter("@id", DbType.Guid, attachmentId);
        cmd.AddParameter("@otc", DbType.Int32, ObjectTypeCode());
        cmd.AddParameter("@field", DbType.String, Attribute, 255);
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return ids;
    }

    private Microsoft.Xrm.Sdk.Metadata.EntityMetadata Table() =>
        _files.Metadata.GetEntity(MailSchema.Attachment)
        ?? throw new InvalidOperationException($"The table '{MailSchema.Attachment}' does not exist."); // fail closed

    private int ObjectTypeCode() =>
        Table().ObjectTypeCode ?? throw new InvalidOperationException($"The table '{MailSchema.Attachment}' has no object type code.");
}
