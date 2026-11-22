using System.Collections.Concurrent;
using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Files;

/// <summary>Writes a record the way an Update message does (through the pipeline: steps and security), in the current transaction.</summary>
public delegate Task RecordUpdate(Entity target, CancellationToken ct);

/// <summary>
/// The value a commit gives a file column, handed to the Core's Update of the record (the only way a file column gets a value:
/// ruling F4 refuses a value from any other Update).
/// </summary>
public sealed record FileCommitGrant(string Entity, Guid RecordId, string Attribute, Guid FileId, string FileName);

/// <summary>An upload session as <see cref="FileColumnService.DescribeUploadAsync"/> reads it.</summary>
public sealed record FileUploadSession(string Entity, Guid RecordId, string Attribute, string FileName, IReadOnlyList<string> BlockIds);

/// <summary>
/// The image a commit gives an image column: the Update of the record carries the image bytes as an Update to a byte[] does,
/// and this grant names the image id the commit answers with and the file name and MIME type of its full image.
/// </summary>
internal sealed record ImageCommitGrant(string Entity, Guid RecordId, string Attribute, Guid ImageId, string FileName, string MimeType);

/// <summary>
/// The upload, commit, download and delete logic of file and image columns, shared by the Core's messages and the Web API
/// (rulings F2, F3, F4, F6, F7, F8). An image column holds its thumbnail in the row (F2) and, when <c>CanStoreFullImage</c>, its
/// full image as a file: a <c>fileattachment</c> whose id is the image id (<c>&lt;image&gt;id</c>).
/// Every operation runs on the caller's <see cref="DataSession"/>: the session rows (<c>meta.filesession</c>), the
/// <c>fileattachment</c> rows (written here, the internal data path: the public messages refuse that table) and the record's
/// Update share the transaction of the message. Blobs live in the <see cref="IFileStore"/> under
/// <c>{database}/{fileattachmentid}</c>, staged blocks under <c>{database}/_upload/{sessionid}/{blockindex}</c>. A blob that a
/// commit writes is deleted if its transaction rolls back; a blob that a commit, a delete or a cascade releases is deleted after
/// its transaction commits. Those deletions never run on the request thread: they are handed to the
/// <see cref="FileDeletionQueue"/>, which <see cref="ProcessDeletionsAsync"/> drains in the background. A blob deletion that
/// fails, or is never run, is logged and left to <see cref="SweepAsync"/>.
/// </summary>
public sealed class FileColumnService(IFileStore store, IMetadataStore metadata, IDbConnectionFactory factory,
    ILogger<FileColumnService>? logger = null, TimeProvider? time = null, FileDeletionQueue? deletions = null, FileStoreOptions? options = null)
    : IEntityDeletedHandler, IAttributeDeletedHandler
{
    /// <summary>The largest block UploadBlock accepts (Dataverse: 4 MB).</summary>
    public const int MaxBlockSize = 4194304;

    /// <summary>The longest block id, decoded (as Azure block blobs: 64 bytes).</summary>
    public const int MaxBlockIdBytes = 64;

    /// <summary>
    /// The most blocks an upload session stages before its commit (as an Azure block blob's committed block list: 50,000), a
    /// server-side bound on the session row and on the commit's work; a new block beyond it is refused explicitly.
    /// </summary>
    public const int MaxUncommittedBlocks = 50000;

    /// <summary>A blob without a <c>fileattachment</c> row is deleted by the sweep once it has been an orphan this long.</summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(24);

    /// <summary>
    /// The one answer to a token that does not open a session of the caller: unknown, tampered, expired, of another kind or of
    /// another caller. The same text whatever the reason, so that it does not reveal whether a session exists.
    /// </summary>
    public const string InvalidTokenMessage =
        "The file continuation token is not valid: it is malformed, expired, or was not issued to this user.";

    private const string Attachment = FileColumns.AttachmentEntity;
    private const string AttachmentId = "fileattachmentid";
    private const string UploadFolder = "_upload";

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly FileDeletionQueue _deletions = deletions ?? new FileDeletionQueue();
    private readonly ConditionalWeakTable<DataSession, List<FileCommitGrant>> _grants = new();
    private readonly ConditionalWeakTable<DataSession, List<ImageCommitGrant>> _imageGrants = new();
    private readonly ConcurrentDictionary<string, FileSessionTokens> _tokens = new(StringComparer.Ordinal);

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    private AnnotationBodies? _annotations;

    /// <summary>
    /// The note bodies (<c>annotation.documentbody</c>, plan 2.11 ruling A16), kept as files of this service: a <c>fileattachment</c>
    /// row and a blob each, under the same transaction rules, sweep and deletion queue (pre-flight C18: the Core reaches them here).
    /// </summary>
    public AnnotationBodies Annotations => _annotations ??= new AnnotationBodies(this);

    /// <summary>The blob store (for <see cref="AnnotationBodies"/>).</summary>
    internal IFileStore Store => store;

    /// <summary>The metadata store (for <see cref="AnnotationBodies"/>).</summary>
    internal IMetadataStore Metadata => metadata;

    /// <summary>The file settings (for <see cref="AnnotationBodies"/>: <see cref="FileStoreOptions.MaxNoteBodyBytesPerRead"/>).</summary>
    internal FileStoreOptions Options { get; } = options ?? new FileStoreOptions();

    /// <summary>
    /// Drops the cached token key of <paramref name="databaseName"/>: the next call derives it again from <c>meta.filesecret</c>.
    /// To be called when that database is replaced (a restore brings another secret); tokens issued before then stop working.
    /// </summary>
    /// <returns>True when a key was cached.</returns>
    public bool ForgetDatabase(string databaseName)
    {
        ArgumentNullException.ThrowIfNull(databaseName);
        return _tokens.TryRemove(databaseName, out _);
    }

    // ---- upload -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// InitializeFileBlocksUpload: opens an upload session on the file or image column of an existing record; returns its token.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown entity or attribute, a column that is neither a file nor an image
    /// column, or a missing file name; 404 when the record does not exist.</exception>
    public async Task<string> InitializeUploadAsync(Caller caller, EntityReference target, string attribute, string fileName,
        DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(s);
        var m = Entity(target.LogicalName);
        var column = UploadColumn(m, attribute);
        CheckFileName(fileName);
        await EnsureRecordExistsAsync(s, m, target.Id, ct);

        var now = UtcNow;
        var session = new FileSession(Guid.NewGuid(), FileSessionKind.Upload, caller.UserId, m.LogicalName, target.Id, column.LogicalName,
            fileName, now, now, null, []);
        await FileSessionStore.InsertAsync(s, session, ct);
        return (await TokensAsync(s, ct)).Issue(session.Id, caller.UserId);
    }

    /// <summary>
    /// UploadBlock: stages <paramref name="data"/> as the block <paramref name="blockId"/> of the session (the same id again
    /// replaces the block: the new data is staged under a fresh index, so a rolled-back UploadBlock leaves the block the session
    /// row names untouched; the replaced index is deleted once the UploadBlock commits). A session stages at most
    /// <see cref="MaxUncommittedBlocks"/> blocks. The running total of the staged blocks is checked against the column's <c>MaxSizeInKB</c>: the block
    /// that crosses it fails with 0x80044a02 (an image column: 0x80072553 ProcessImageFailure) and ends the session (its staged
    /// blocks are deleted).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for an invalid token, block id or a block over 4 MB; 400
    /// 0x80044a02 (file) or 0x80072553 (image) when the file grows over <c>MaxSizeInKB</c>.</exception>
    public async Task UploadBlockAsync(Caller caller, string token, string blockId, Stream data, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        var idLength = BlockIdLength(blockId);
        var bytes = await ReadBlockAsync(data, ct);
        var session = await ResolveAsync(caller, token, FileSessionKind.Upload, s, ct);
        if (session.Blocks.Count > 0 && BlockIdLength(session.Blocks[0].Id) != idLength)
            throw XrmdFaultException.InvalidArgument(
                $"BlockId '{blockId}' is {idLength} bytes long once decoded: every block id of a file must have the same length ({BlockIdLength(session.Blocks[0].Id)} bytes).");
        if (TryReadChunkBlockId(blockId, out var chunkOffset, out var chunkTotal)) CheckChunk(session, chunkOffset, chunkTotal, bytes.Length);
        var column = SessionColumn(session);

        var replaced = session.Blocks.FirstOrDefault(b => b.Id == blockId);
        var replaces = replaced is not null;
        if (!replaces && session.Blocks.Count >= MaxUncommittedBlocks)
            throw XrmdFaultException.InvalidArgument(
                $"This upload session already holds {session.Blocks.Count} uncommitted blocks, the most this server stages ({MaxUncommittedBlocks}): commit them with CommitFileBlocksUpload, or upload the file in larger blocks.");
        var total = session.Blocks.Where(b => b.Id != blockId).Sum(b => b.Size) + bytes.Length;
        if (total > MaxBytes(column))
        {
            EndOnRollback(s, session.Id);
            throw TooBig(column, total);
        }

        // Always a fresh index, never the replaced block's: the write is outside the transaction, which may still roll back.
        var index = session.Blocks.Count == 0 ? 0 : session.Blocks.Max(b => b.Index) + 1;
        await store.WriteAsync(StagingKey(s, session.Id, index), new MemoryStream(bytes, writable: false), ct);
        var blocks = session.Blocks.Where(b => b.Id != blockId).Append(new StagedBlock(blockId, index, bytes.Length)).ToList();
        await FileSessionStore.UpdateAsync(s, session with { Blocks = blocks, LastUsedOn = UtcNow }, ct);
        // The replaced index is named by no session row once this commits (indexes only grow: it is never staged again).
        if (replaced is not null)
        {
            var old = StagingKey(s, session.Id, replaced.Index);
            s.OnCommitted(() => Enqueue(old));
        }
    }

    /// <summary>
    /// Ends the upload session of <paramref name="token"/> and deletes its staged blocks, for a caller whose upload failed after
    /// InitializeFileBlocksUpload (the Web API's single request). Best effort and idempotent: a token that is not the caller's, or
    /// a session already gone, is ignored, and so is any failure (logged; the cleanup sweep deletes what is left), so that it
    /// never masks the error being handled.
    /// </summary>
    public async Task AbortUploadAsync(Caller caller, string? token)
    {
        ArgumentNullException.ThrowIfNull(caller);
        try
        {
            await using var conn = await factory.OpenAsync(CancellationToken.None);
            var s = new DataSession(conn, null);
            if (!(await TokensAsync(s, CancellationToken.None)).TryRead(token, caller.UserId, out var id)) return;
            await FileSessionStore.DeleteAsync(s, id, CancellationToken.None);
            await store.DeletePrefixAsync(StagingPrefix(s, id), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aborting the upload session of a failed request failed; the cleanup sweep will delete it.");
        }
    }

    /// <summary>
    /// CommitFileBlocksUpload (ruling F7): the blocks in <paramref name="blockList"/> order become the file (every staged block
    /// exactly once), a <c>fileattachment</c> row records it and <paramref name="update"/> sets the column (and its <c>_name</c>)
    /// on the record, which replaces any previous file (its blob is deleted after the commit). The new blob is written before
    /// the transaction commits and deleted if it rolls back (e.g. a plug-in on the Update throws). On an image column the blocks
    /// are the image, which <paramref name="update"/> sets as an Update to a byte[] does (thumbnail, companions and, when
    /// <c>CanStoreFullImage</c>, the full image with this file name and MIME type: <see cref="ApplyImagesAsync"/>).
    /// </summary>
    /// <returns>The file id (<c>fileattachmentid</c>; for an image the image id) and its size in bytes.</returns>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for an invalid token or block list, or a missing or empty
    /// <c>FileName</c> or <c>MimeType</c>; 0x80044a02 over <c>MaxSizeInKB</c>; the faults of the Update.</exception>
    public async Task<(Guid FileId, long Size)> CommitAsync(Caller caller, string token, string[] blockList, string? fileName,
        string? mimeType, DataSession s, RecordUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        // FileName and MimeType are required (Nullable=False on CommitFileBlocksUploadRequest): no default is made up here.
        CheckFileName(fileName);
        if (string.IsNullOrEmpty(mimeType)) throw XrmdFaultException.InvalidArgument("Required parameter 'MimeType' is missing.");
        var session = await ResolveAsync(caller, token, FileSessionKind.Upload, s, ct);
        var blocks = OrderedBlocks(session, blockList);
        var name = fileName!;
        var m = Entity(session.Entity);
        var column = SessionColumn(session);
        var size = blocks.Sum(b => b.Size);
        if (size > MaxBytes(column))
        {
            EndOnRollback(s, session.Id);
            throw TooBig(column, size);
        }
        if (column is ImageAttributeMetadata)
            return (await CommitImageAsync(s, m, session, blocks, name, mimeType, update, ct), size);

        var fileId = Guid.NewGuid();
        var key = FileKey(s, fileId);
        s.OnRolledBack(() => Enqueue(key));
        if (blocks.Count == 0) await store.WriteAsync(key, new MemoryStream([], writable: false), ct);
        else await store.ConcatAsync(blocks.Select(b => StagingKey(s, session.Id, b.Index)).ToList(), key, ct);

        await InsertAttachmentAsync(s, m, session.RecordId, column.LogicalName, fileId, name, mimeType, size, ct);
        await UpdateWithGrantAsync(s, new FileCommitGrant(m.LogicalName, session.RecordId, column.LogicalName, fileId, name), update, ct);
        await EndCommittedSessionAsync(s, session, ct);
        return (fileId, size);
    }

    /// <summary>The commit of an image column: the blocks, in order, are the image the record's Update sets.</summary>
    private async Task<Guid> CommitImageAsync(DataSession s, EntityMetadata m, FileSession session, List<StagedBlock> blocks, string fileName,
        string mimeType, RecordUpdate update, CancellationToken ct)
    {
        using var image = new MemoryStream();
        foreach (var block in blocks)
        {
            await using var part = await store.OpenReadAsync(StagingKey(s, session.Id, block.Index), 0, null, ct);
            await part.CopyToAsync(image, ct);
        }
        var grant = new ImageCommitGrant(m.LogicalName, session.RecordId, session.Attribute, Guid.NewGuid(), fileName, mimeType);
        var grants = _imageGrants.GetValue(s, static _ => []);
        grants.Add(grant);
        try
        {
            await update(new Entity(m.LogicalName, session.RecordId) { [session.Attribute] = image.ToArray() }, ct);
        }
        finally
        {
            grants.Remove(grant);
        }
        await EndCommittedSessionAsync(s, session, ct);
        return grant.ImageId;
    }

    /// <summary>A committed session: its row goes in the transaction, its staged blocks after the commit.</summary>
    private async Task EndCommittedSessionAsync(DataSession s, FileSession session, CancellationToken ct)
    {
        await FileSessionStore.DeleteAsync(s, session.Id, ct);
        var staging = StagingPrefix(s, session.Id);
        s.OnCommitted(() => Enqueue(staging));
    }

    /// <summary>
    /// The block id of the Web API's chunked upload for the chunk at <paramref name="offset"/> of a file of
    /// <paramref name="total"/> bytes: the marker <c>xrmdchnk</c> followed by the two values as little-endian 64-bit integers
    /// (24 bytes, base64). UploadBlock checks a block of this form against the session (<see cref="UploadBlockAsync"/>).
    /// </summary>
    public static string ChunkBlockId(long offset, long total)
    {
        var bytes = new byte[ChunkIdLength];
        ChunkMarker.CopyTo(bytes, 0);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), offset);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 8), total);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The offset and total of a <see cref="ChunkBlockId"/>; false for a block id of any other form.</summary>
    public static bool TryReadChunkBlockId(string? blockId, out long offset, out long total)
    {
        offset = total = 0;
        var bytes = new byte[ChunkIdLength];
        if (blockId is not { Length: 32 } || !Convert.TryFromBase64String(blockId, bytes, out var written) || written != ChunkIdLength
            || !bytes.AsSpan(0, 8).SequenceEqual(ChunkMarker))
            return false;
        offset = BitConverter.ToInt64(bytes, 8);
        total = BitConverter.ToInt64(bytes, 16);
        return true;
    }

    private const int ChunkIdLength = 24;
    private static readonly byte[] ChunkMarker = "xrmdchnk"u8.ToArray();

    /// <summary>
    /// The chunk rules of a <see cref="ChunkBlockId"/> block, checked under the session row's lock: the Web API cannot reorder
    /// chunks, so each one starts where the received bytes end, names the total of the earlier chunks and ends within it; a
    /// session holding blocks of another form is not continued with chunks.
    /// </summary>
    private static void CheckChunk(FileSession session, long offset, long total, long length)
    {
        long? earlierTotal = null;
        foreach (var block in session.Blocks)
        {
            if (!TryReadChunkBlockId(block.Id, out _, out var t))
                throw XrmdFaultException.InvalidArgument(
                    "This upload session holds blocks sent with UploadBlock: continue it with UploadBlock and CommitFileBlocksUpload.");
            earlierTotal ??= t;
        }
        var range = $"bytes {offset}-{offset + length - 1}/{total}";
        if (earlierTotal is { } named && named != total)
            throw XrmdFaultException.InvalidArgument(
                $"Content-Range '{range}' names a total of {total} bytes; the earlier chunks of this upload named {named}.");
        var next = session.Blocks.Sum(b => b.Size);
        if (offset != next)
            throw XrmdFaultException.InvalidArgument(
                $"Content-Range '{range}' does not start at byte {next}, the next byte of the file: chunks must be sent in order, without gaps or overlaps.");
        if (length == 0 || offset + length > total)
            throw XrmdFaultException.InvalidArgument($"Content-Range '{range}' is not a valid range.");
    }

    /// <summary>
    /// The upload session of <paramref name="token"/> (a read: its record, column, file name and staged block ids, in staging
    /// order). The Web API's chunked upload checks the session against its url with it, and orders its chunks for the commit.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <see cref="InvalidTokenMessage"/>.</exception>
    public async Task<FileUploadSession> DescribeUploadAsync(Caller caller, string? token, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        FileSession session;
        try
        {
            session = await ResolveAsync(caller, token, FileSessionKind.Upload, s, ct);
        }
        catch when (s.Transaction is null)
        {
            s.RolledBack(); // an expired session ends now (the refusal has nothing to roll back)
            throw;
        }
        return new FileUploadSession(session.Entity, session.RecordId, session.Attribute, session.FileName ?? "",
            session.Blocks.OrderBy(b => b.Index).Select(b => b.Id).ToList());
    }

    // ---- download -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// InitializeFileBlocksDownload: opens a download session on the file column of an existing record, or on the full image of
    /// an image column (never the thumbnail: Learn, image column data); returns its token with the size and name of the file.
    /// The caller's Read on the record is demanded by the security guard (as Write is for an upload): every download, the Web
    /// API's <c>$value</c> and <c>/Image/download.aspx</c> included, runs this message and DownloadBlock through the pipeline.
    /// Chunking is always supported.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown entity or attribute or a column that is neither a file nor an
    /// image column; 404 when the record does not exist; 404 0x80040217 ObjectDoesNotExist when the column is empty or the
    /// image has no full image (<c>CanStoreFullImage</c> was false when it was set).</exception>
    public async Task<(string Token, long Size, string FileName, bool IsChunkingSupported)> InitializeDownloadAsync(Caller caller,
        EntityReference target, string attribute, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(s);
        var m = Entity(target.LogicalName);
        var column = DownloadColumn(m, attribute);
        var file = await StoredFileAsync(s, m, target.Id, column, ct);

        var now = UtcNow;
        var session = new FileSession(Guid.NewGuid(), FileSessionKind.Download, caller.UserId, m.LogicalName, target.Id, column.LogicalName,
            file.FileName, now, now, file.FileId, []);
        await FileSessionStore.InsertAsync(s, session, ct);
        return ((await TokensAsync(s, ct)).Issue(session.Id, caller.UserId), file.Size, file.FileName, true);
    }

    /// <summary>
    /// DownloadBlock: <paramref name="length"/> bytes of the session's file from <paramref name="offset"/> (the remainder when
    /// the file ends before). The token is bound to its caller (the Read on the record was demanded when it was issued); each
    /// use renews the session's 24 hours. A block is at most <see cref="MaxBlockSize"/> bytes: a server-side bound on the memory
    /// of one response (the size of an upload block), not a Dataverse rule, refused explicitly beyond it.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for an invalid token, a negative offset, an offset past the end of
    /// the file, or a length that is not between 1 and <see cref="MaxBlockSize"/>; 404 ObjectDoesNotExist when the file is gone
    /// (replaced or deleted since the session opened).</exception>
    public async Task<Stream> DownloadBlockAsync(Caller caller, string token, long offset, long length, DataSession s, CancellationToken ct)
    {
        var session = await ResolveAsync(caller, token, FileSessionKind.Download, s, ct);
        if (offset < 0) throw XrmdFaultException.InvalidArgument($"Offset {offset} is negative.");
        if (length < 1 || length > MaxBlockSize)
            throw XrmdFaultException.InvalidArgument(
                $"BlockLength {length} is not valid: this server returns download blocks of 1 to {MaxBlockSize} bytes (4 MB); read a larger file in several blocks.");
        var fileId = session.FileAttachmentId ?? throw XrmdFaultException.InvalidArgument(InvalidTokenMessage);
        // A replaced or deleted file loses its fileattachment row at once, while its blob is removed later by the deletion
        // queue: the row, not the blob, decides whether the session's file still exists.
        if ((await ExistingAsync(s, s.DataTable(Attachment), AttachmentId, [fileId], ct)).Count == 0)
            throw XrmdFaultException.ObjectDoesNotExist(Attachment, fileId);
        var key = FileKey(s, fileId);
        var size = await store.GetLengthAsync(key, ct) ?? throw XrmdFaultException.ObjectDoesNotExist(Attachment, fileId);
        if (offset > size || (offset == size && size > 0))
            throw XrmdFaultException.InvalidArgument($"Offset {offset} is past the end of the file, which is {size} bytes long.");
        await FileSessionStore.UpdateAsync(s, session with { LastUsedOn = UtcNow }, ct);
        return await OpenBlobAsync(key, fileId, offset, Math.Min(length, size - offset), ct);
    }

    /// <summary>
    /// Opens a blob whose length was just read: one deleted in between (its file replaced or deleted by another transaction)
    /// is the 404 of a missing file, not an unexpected error.
    /// </summary>
    internal async Task<Stream> OpenBlobAsync(string key, Guid fileId, long offset, long? length, CancellationToken ct)
    {
        try
        {
            return await store.OpenReadAsync(key, offset, length, ct);
        }
        catch (FileNotFoundException)
        {
            throw XrmdFaultException.ObjectDoesNotExist(Attachment, fileId);
        }
    }

    /// <summary>
    /// The MIME type of the file a download session reads (the <c>mimetype</c> of its <c>fileattachment</c>), for the Web API's
    /// <c>$value</c>, which answers it in its <c>mimetype</c> header (InitializeFileBlocksDownload does not return it). A read
    /// bound to the caller's token: the Read on the record was demanded when InitializeFileBlocksDownload issued it.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <see cref="InvalidTokenMessage"/>; 404 ObjectDoesNotExist when the file is gone
    /// (replaced or deleted since the session opened).</exception>
    public async Task<string> DownloadMimeTypeAsync(Caller caller, string? token, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(s);
        if (!(await TokensAsync(s, ct)).TryRead(token, caller.UserId, out var id)
            || await FileSessionStore.ReadForUpdateAsync(s, id, ct) is not { } session
            || session.CallerId != caller.UserId || session.Kind != FileSessionKind.Download || session.FileAttachmentId is not { } fileId)
            throw XrmdFaultException.InvalidArgument(InvalidTokenMessage);
        await using var cmd = s.Command($"SELECT mimetype FROM {s.DataTable(Attachment)} WHERE {s.Dialect.Quote(AttachmentId)} = @file");
        cmd.AddParameter("@file", DbType.Guid, fileId);
        return await cmd.ExecuteScalarAsync(ct) switch
        {
            null => throw XrmdFaultException.ObjectDoesNotExist(Attachment, fileId),
            DBNull => "application/octet-stream",
            var value => (string)value,
        };
    }

    /// <summary>
    /// Ends the caller's download session of <paramref name="token"/> once the Web API has served its file (Dataverse has no
    /// message for that: an SDK client's session simply expires). Best effort: a token that is not the caller's, or a session
    /// already gone, is ignored, and so is any failure (logged; the cleanup sweep deletes the session when it expires).
    /// </summary>
    public async Task EndDownloadAsync(Caller caller, string? token)
    {
        ArgumentNullException.ThrowIfNull(caller);
        try
        {
            await using var conn = await factory.OpenAsync(CancellationToken.None);
            var s = new DataSession(conn, null);
            if (!(await TokensAsync(s, CancellationToken.None)).TryRead(token, caller.UserId, out var id)) return;
            if (await FileSessionStore.ReadForUpdateAsync(s, id, CancellationToken.None) is not { Kind: FileSessionKind.Download } session
                || session.CallerId != caller.UserId)
                return;
            await FileSessionStore.DeleteAsync(s, id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ending a served download session failed; the cleanup sweep deletes it when it expires.");
        }
    }

    /// <summary>The MIME type of a thumbnail (an image column's value): <c>image/jpeg</c> for JPEG content, <c>image/png</c> otherwise (ruling F3).</summary>
    public static string ThumbnailMimeType(byte[] thumbnail)
    {
        ArgumentNullException.ThrowIfNull(thumbnail);
        return thumbnail is [0xFF, 0xD8, ..] ? "image/jpeg" : "image/png";
    }

    /// <summary>
    /// The record of <paramref name="entity"/> whose image column <paramref name="attribute"/> holds the image
    /// <paramref name="imageId"/> (<c>&lt;image&gt;id</c>), as <c>/Image/download.aspx</c> names it; null when none does (the
    /// image was replaced or deleted). An internal read: the caller's Read on the record is demanded afterwards.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown entity or a column that is not an image column.</exception>
    public async Task<Guid?> FindImageRecordAsync(string entity, string attribute, Guid imageId, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var m = Entity(entity);
        if ((m.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attribute) is not ImageAttributeMetadata image)
            throw XrmdFaultException.InvalidArgument($"Attribute '{attribute}' of entity '{m.LogicalName}' is not an image column.");
        var d = s.Dialect;
        await using var cmd = s.Command(d.Limit(
            $"SELECT {d.Quote(SystemAttributes.PrimaryIdName(m))} FROM {s.DataTable(m.LogicalName)} WHERE {d.Quote(image.LogicalName + FileColumns.ImageIdSuffix)} = @id",
            "1"));
        cmd.AddParameter("@id", DbType.Guid, imageId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>
    /// The file a record's file column points to, or the full image of an image column, from its <c>fileattachment</c> row (no
    /// lock: a read).
    /// </summary>
    private async Task<(Guid FileId, string FileName, string MimeType, long Size)> StoredFileAsync(DataSession s, EntityMetadata m, Guid id,
        AttributeMetadata column, CancellationToken ct)
    {
        Guid? fileId;
        var d = s.Dialect;
        await using (var cmd = s.Command(
                         $"SELECT {d.Quote(IdColumn(column))} FROM {s.DataTable(m.LogicalName)} WHERE {d.Quote(SystemAttributes.PrimaryIdName(m))} = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, id);
            fileId = rd.IsDBNull(0) ? null : rd.GetGuid(0);
        }
        // Dataverse's text for a column without a file is not published: this one says what is missing.
        if (fileId is not { } file)
            throw column is ImageAttributeMetadata
                ? NoImage(m, column, id)
                : new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"The file column '{column.LogicalName}' of {m.LogicalName} With Id = {id} has no file.", 404);
        await using var row = s.Command(
            $"SELECT filename, mimetype, filesizeinbytes FROM {s.DataTable(Attachment)} WHERE {d.Quote(AttachmentId)} = @file");
        row.AddParameter("@file", DbType.Guid, file);
        await using var reader = await row.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw column is ImageAttributeMetadata
                // Learn (image column data): the image was set while CanStoreFullImage was false, so only its thumbnail exists.
                ? new XrmdFaultException(ErrorCodes.ObjectDoesNotExist,
                    $"No FileAttachment records found for imagedescriptorId: {file} for image attribute: {column.LogicalName} of {m.LogicalName} record with id {id}.", 404)
                : XrmdFaultException.ObjectDoesNotExist(Attachment, file);
        return (file, reader.IsDBNull(0) ? "" : reader.GetString(0), reader.IsDBNull(1) ? "application/octet-stream" : reader.GetString(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt64(2));
    }

    /// <summary>The file or image column the download messages read (the same columns the upload messages write).</summary>
    private static AttributeMetadata DownloadColumn(EntityMetadata m, string? attribute) => UploadColumn(m, attribute);

    /// <summary>404 ObjectDoesNotExist for an empty image column (Dataverse's text for that case is not published).</summary>
    public static XrmdFaultException NoImage(string entity, string column, Guid id) =>
        new(ErrorCodes.ObjectDoesNotExist, $"The image column '{column}' of {entity} With Id = {id} has no image.", 404);

    private static XrmdFaultException NoImage(EntityMetadata m, AttributeMetadata column, Guid id) => NoImage(m.LogicalName, column.LogicalName, id);

    // ---- delete -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// DeleteFile: the file's column is cleared with <paramref name="update"/> (which deletes the row, and the blob after the
    /// commit); a file its column no longer points to is deleted directly.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 when the file does not exist; the faults of the Update.</exception>
    public async Task DeleteAsync(Caller caller, Guid fileId, DataSession s, RecordUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(update);
        Entity(Attachment);
        Guid recordId;
        int objectTypeCode;
        string attribute;
        // Plan 2.7b, Task 6: the dialect's update lock; a missing row is the 404 below, so PostgreSQL needs no key lock here.
        var d = s.Dialect;
        await using (var cmd = s.Command(
                         $"SELECT objectid, objectidtype, regardingfieldname FROM {s.DataTable(Attachment)}{d.LockHint(RowLock.UpdateRange)} " +
                         $"WHERE {d.Quote(AttachmentId)} = @id{d.LockClause(RowLock.UpdateRange)}"))
        {
            cmd.AddParameter("@id", DbType.Guid, fileId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct) || rd.IsDBNull(0) || rd.IsDBNull(1) || rd.IsDBNull(2))
                throw XrmdFaultException.ObjectDoesNotExist(Attachment, fileId);
            (recordId, objectTypeCode, attribute) = (rd.GetGuid(0), rd.GetInt32(1), rd.GetString(2));
        }
        var m = metadata.GetEntityByObjectTypeCode(objectTypeCode) ?? throw XrmdFaultException.ObjectDoesNotExist(Attachment, fileId);
        // A note body (plan 2.11 A16) is not a file column's file: it goes with an Update of the note.
        if (m.LogicalName == ActivitySchema.Annotation && attribute == AnnotationBodies.Attribute)
            throw XrmdFaultException.InvalidArgument(
                $"The file {fileId} is the body of a note: clear it with an Update of the note's '{AnnotationBodies.Attribute}' to null.");
        var column = UploadColumn(m, attribute);
        // An image kept without its full image has no fileattachment row, so DeleteFile on its image id is the 404 above (what
        // Dataverse answers then is not documented); an Update to null clears such an image.
        if (await ColumnValueAsync(s, m, recordId, IdColumn(column), ct) == fileId)
            await update(new Entity(m.LogicalName, recordId) { [column.LogicalName] = null }, ct);
        else
            await ReleaseAsync(s, fileId, ct);
    }

    // ---- the Core's Update, Delete and the metadata cascades -------------------------------------------------------------

    /// <summary>The commit whose Update of <paramref name="target"/> is running in <paramref name="s"/>, if any.</summary>
    public FileCommitGrant? PendingCommit(DataSession s, Entity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _grants.TryGetValue(s, out var grants)
            ? grants.LastOrDefault(g => g.Entity == target.LogicalName && g.RecordId == target.Id)
            : null;
    }

    /// <summary>
    /// The file values of an Update of the record <paramref name="id"/> (ruling F4), applied to <paramref name="row"/> before it is
    /// written: a file column set to null loses its file (row deleted, blob deleted after the commit) and its <c>_name</c>; the
    /// column of <paramref name="commit"/> gets the new file id and name, and its previous file is released the same way.
    /// </summary>
    public async Task ApplyOnUpdateAsync(DataSession s, EntityMetadata m, Guid id, Entity row, FileCommitGrant? commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(row);
        var attributes = (m.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        foreach (var name in row.Attributes.Where(kv => kv.Value is null && attributes.GetValueOrDefault(kv.Key) is FileAttributeMetadata)
                     .Select(kv => kv.Key).ToList())
        {
            if (await ColumnValueAsync(s, m, id, name, ct) is { } current) await ReleaseAsync(s, current, ct);
            if (attributes.ContainsKey(NameColumn(name))) row[NameColumn(name)] = null;
        }
        if (commit is null) return;
        if (await ColumnValueAsync(s, m, id, commit.Attribute, ct) is { } previous && previous != commit.FileId)
            await ReleaseAsync(s, previous, ct);
        row[commit.Attribute] = commit.FileId;
        if (attributes.ContainsKey(NameColumn(commit.Attribute))) row[NameColumn(commit.Attribute)] = commit.FileName;
    }

    /// <summary>
    /// The image values of a Create or an Update of the record <paramref name="id"/> (ruling F4), applied to <paramref name="row"/>
    /// before it is written. An image column set to a byte[] gets its thumbnail (<see cref="Thumbnailer"/>), a new image id and
    /// the timestamp (UTC ticks); when <c>CanStoreFullImage</c> the image itself is kept as the file of a <c>fileattachment</c>
    /// whose id is the image id (its blob written now and deleted if the transaction rolls back). Set to null, the column, its id
    /// and timestamp are cleared. On Update the previous full image is released (row deleted, blob deleted after the commit).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 0x80090487 for an image other than the primary one on Create; 400 0x80072553
    /// ProcessImageFailure for an image over <c>MaxSizeInKB</c> or one that is not a GIF, JPEG, BMP or PNG image.</exception>
    public async Task ApplyImagesAsync(DataSession s, EntityMetadata m, Guid id, Entity row, bool isCreate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(row);
        var attributes = (m.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        foreach (var (name, value) in row.Attributes.Where(kv => attributes.GetValueOrDefault(kv.Key) is ImageAttributeMetadata).ToList())
        {
            var image = (ImageAttributeMetadata)attributes[name];
            var previous = isCreate ? null : await ColumnValueAsync(s, m, id, name + FileColumns.ImageIdSuffix, ct);
            if (value is null)
            {
                if (previous is { } released) await ReleaseAsync(s, released, ct);
                SetCompanion(row, attributes, name + FileColumns.ImageIdSuffix, null);
                SetCompanion(row, attributes, name + FileColumns.TimestampSuffix, null);
                continue;
            }
            if (isCreate && image.IsPrimaryImage != true) throw XrmdFaultException.NonPrimaryImageOnCreate(name, m.LogicalName);
            var bytes = (byte[])value;
            if (bytes.LongLength > MaxBytes(image)) throw TooBig(image, bytes.LongLength);

            byte[] thumbnail;
            string sourceType;
            try
            {
                thumbnail = (await Thumbnailer.CreateAsync(bytes, ct)).Bytes;
                sourceType = Thumbnailer.MimeTypeOf(bytes);
            }
            catch (ImageProcessingException ex)
            {
                throw XrmdFaultException.ImageNotProcessed(id, m.LogicalName, name, ex.Message);
            }

            var grant = PendingImage(s, m.LogicalName, id, name);
            var imageId = grant?.ImageId ?? Guid.NewGuid();
            row[name] = thumbnail;
            SetCompanion(row, attributes, name + FileColumns.ImageIdSuffix, imageId);
            SetCompanion(row, attributes, name + FileColumns.TimestampSuffix, UtcNow.Ticks);
            if (previous is { } replaced && replaced != imageId) await ReleaseAsync(s, replaced, ct);
            if (image.CanStoreFullImage != true) continue;

            var key = FileKey(s, imageId);
            s.OnRolledBack(() => Enqueue(key));
            await store.WriteAsync(key, new MemoryStream(bytes, writable: false), ct);
            // An image set with a byte[] has no file name: it is named after its column, with the extension of its type.
            await InsertAttachmentAsync(s, m, id, name, imageId, grant?.FileName ?? $"{name}.{sourceType["image/".Length..]}",
                grant?.MimeType ?? sourceType, bytes.LongLength, ct);
        }
    }

    private static void SetCompanion(Entity row, Dictionary<string, AttributeMetadata> attributes, string name, object? value)
    {
        if (attributes.ContainsKey(name)) row[name] = value;
    }

    /// <summary>The image commit whose Update of the record's <paramref name="attribute"/> is running in <paramref name="s"/>, if any.</summary>
    private ImageCommitGrant? PendingImage(DataSession s, string entity, Guid recordId, string attribute) =>
        _imageGrants.TryGetValue(s, out var grants)
            ? grants.LastOrDefault(g => g.Entity == entity && g.RecordId == recordId && g.Attribute == attribute)
            : null;

    /// <summary>The files of a deleted record (ruling F8): rows deleted in its transaction, blobs after its commit.</summary>
    public Task DeleteRecordFilesAsync(DataSession s, EntityMetadata m, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m);
        // A note's body is a file of the note (plan 2.11 A16), whatever its columns.
        if (!((m.Attributes ?? []).Any(FileColumns.IsFileOrImage) || FileColumns.IsPermanentTarget(m.LogicalName))
            || m.ObjectTypeCode is not { } otc) return Task.CompletedTask;
        return DeleteAttachmentsAsync(s, "objectid = @id AND objectidtype = @otc", ct,
            ("@id", DbType.Guid, id), ("@otc", DbType.Int32, otc));
    }

    /// <summary>A deleted table takes its files with it (ruling F8; solution cascade deletes included).</summary>
    public Task OnEntityDeletingAsync(EntityMetadata entity, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.LogicalName == Attachment || entity.ObjectTypeCode is not { } otc) return Task.CompletedTask;
        return DeleteAttachmentsAsync(session, "objectidtype = @otc", ct, ("@otc", DbType.Int32, otc));
    }

    /// <summary>A dropped file or image column takes its files with it (ruling F8).</summary>
    public Task OnAttributeDeletingAsync(EntityMetadata entity, AttributeMetadata attribute, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        if (!FileColumns.IsFileOrImage(attribute) || entity.ObjectTypeCode is not { } otc) return Task.CompletedTask;
        return DeleteAttachmentsAsync(session, "objectidtype = @otc AND regardingfieldname = @attribute", ct,
            ("@otc", DbType.Int32, otc), ("@attribute", DbType.String, attribute.LogicalName));
    }

    // ---- cleanup ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The cleanup sweep of the organization database: deletes the sessions unused for 24 hours (<see cref="FileSessionStore.Lifetime"/>)
    /// with their staged blocks, the staged blocks that have had no visible session for <see cref="OrphanGrace"/>, and the blobs
    /// that have had no <c>fileattachment</c> row for <see cref="OrphanGrace"/>. Both are first recorded in <c>meta.fileorphan</c>
    /// and deleted only once still orphaned after the grace: under READ_COMMITTED_SNAPSHOT the sweep does not see the rows of
    /// transactions in progress (a session opened and used in one transaction, as by a
    /// plug-in; a commit that has written its blob before its row). On the file-system store, stale temporary files go too.
    /// </summary>
    /// <remarks>
    /// It covers only the organization database this host serves (the one <see cref="IDbConnectionFactory"/> opens, and the
    /// blobs under its name): the blobs of any other database sharing the store are never listed nor deleted.
    /// </remarks>
    public async Task SweepAsync(DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var s = new DataSession(conn, null);
        var database = Database(s);

        foreach (var id in await FileSessionStore.DeleteExpiredAsync(s, nowUtc - FileSessionStore.Lifetime, ct))
            await store.DeletePrefixAsync(StagingPrefix(s, id), ct);

        var staged = new HashSet<Guid>();
        var blobs = new Dictionary<Guid, string>();
        await foreach (var key in store.ListAsync(database + "/", ct))
        {
            var segments = key.Split('/');
            if (segments.Length >= 3 && segments[1] == UploadFolder && Guid.TryParse(segments[2], out var session)) staged.Add(session);
            else if (segments.Length == 2 && Guid.TryParse(segments[1], out var file)) blobs[file] = key;
        }
        var orphanStaging = staged.Except(await ExistingAsync(s, "meta.filesession", "id", staged, ct)).Select(id => StagingPrefix(s, id));

        var orphans = metadata.GetEntity(Attachment) is null
            ? blobs.Keys.ToHashSet()
            : blobs.Keys.Except(await ExistingAsync(s, s.DataTable(Attachment), AttachmentId, blobs.Keys, ct)).ToHashSet();
        await SweepOrphansAsync(s, [.. orphans.Select(o => blobs[o]), .. orphanStaging], nowUtc, ct);

        if (store is FileSystemFileStore fileSystem) await fileSystem.PurgeStaleTempFilesAsync(OrphanGrace, ct);
    }

    private async Task SweepOrphansAsync(DataSession s, IReadOnlyList<string> orphanKeys, DateTime nowUtc, CancellationToken ct)
    {
        // Forget the recorded orphans (blobs and staging prefixes) that have a row again or are gone, record the new ones
        // (meta.fileorphan is a table of the organization database: every row is of this database's prefix).
        var keys = s.Dialect.JsonArray("@keys");
        await using (var cmd = s.Command($"""
            DELETE FROM meta.fileorphan WHERE blob_key NOT IN (SELECT value FROM {keys});
            INSERT INTO meta.fileorphan (blob_key, first_seen)
            SELECT k.value, @now FROM {keys} AS k WHERE NOT EXISTS (SELECT 1 FROM meta.fileorphan AS o WHERE o.blob_key = k.value);
            """))
        {
            cmd.AddParameter("@keys", DbType.String, System.Text.Json.JsonSerializer.Serialize(orphanKeys), -1);
            cmd.AddParameter("@now", DbType.DateTime2, nowUtc);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var due = new List<string>();
        await using (var cmd = s.Command("SELECT blob_key FROM meta.fileorphan WHERE first_seen <= @cutoff"))
        {
            cmd.AddParameter("@cutoff", DbType.DateTime2, nowUtc - OrphanGrace);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) due.Add(rd.GetString(0));
        }
        foreach (var key in due)
        {
            // The row (session or fileattachment) may have appeared since the listing: check once more right before deleting.
            var segments = key.Split('/');
            if (segments.Length >= 3 && segments[1] == UploadFolder)
            {
                if ((await ExistingAsync(s, "meta.filesession", "id", [Guid.Parse(segments[2])], ct)).Count > 0) continue;
                await store.DeletePrefixAsync(key, ct);
            }
            else
            {
                var id = Guid.Parse(segments[^1]);
                if (metadata.GetEntity(Attachment) is not null
                    && (await ExistingAsync(s, s.DataTable(Attachment), AttachmentId, [id], ct)).Count > 0)
                    continue;
                await store.DeleteAsync(key, ct);
            }
            await using var cmd = s.Command("DELETE FROM meta.fileorphan WHERE blob_key = @key");
            cmd.AddParameter("@key", DbType.String, key, 450);
            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Deleted the orphan blob or staging {Key}.", key);
        }
    }

    /// <summary>
    /// The ids among <paramref name="ids"/> that exist in <paramref name="column"/> (a bare name, quoted here) of
    /// <paramref name="table"/> (a qualified name as the dialect writes it).
    /// </summary>
    private static async Task<HashSet<Guid>> ExistingAsync(DataSession s, string table, string column, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var result = new HashSet<Guid>();
        var json = System.Text.Json.JsonSerializer.Serialize(ids);
        if (json == "[]") return result;
        var d = s.Dialect;
        await using var cmd = s.Command(
            $"SELECT t.{d.Quote(column)} FROM {table} AS t WHERE t.{d.Quote(column)} IN (SELECT CAST(value AS {d.ColumnType(SqlColumnKind.Guid)}) FROM {d.JsonArray("@ids")})");
        cmd.AddParameter("@ids", DbType.String, json, -1);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) result.Add(rd.GetGuid(0));
        return result;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The session of <paramref name="token"/> for <paramref name="caller"/>, locked for this transaction. An expired one is
    /// deleted with its staged blocks once this transaction (which the refusal rolls back) has ended.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <see cref="InvalidTokenMessage"/>.</exception>
    private async Task<FileSession> ResolveAsync(Caller caller, string? token, string kind, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(s);
        if (!(await TokensAsync(s, ct)).TryRead(token, caller.UserId, out var id)
            || await FileSessionStore.ReadForUpdateAsync(s, id, ct) is not { } session
            || session.CallerId != caller.UserId || session.Kind != kind)
            throw XrmdFaultException.InvalidArgument(InvalidTokenMessage);
        if (session.LastUsedOn + FileSessionStore.Lifetime < UtcNow)
        {
            EndOnRollback(s, session.Id);
            throw XrmdFaultException.InvalidArgument(InvalidTokenMessage);
        }
        return session;
    }

    /// <summary>
    /// Ends the session after the refusal being thrown has rolled its transaction back (the refusal would roll back a delete made
    /// in it): its row is deleted on a connection of its own, its staged blocks by the deletion queue.
    /// </summary>
    /// <remarks>
    /// The row's delete stays on the thread that reports the rollback, synchronously: the session must be gone before the
    /// refusal reaches the caller (a retry with the same token must fail), and <see cref="DataSession"/> runs its callbacks as
    /// plain actions, which cannot be awaited. It is one indexed DELETE; the blob work goes to the queue.
    /// </remarks>
    private void EndOnRollback(DataSession s, Guid sessionId)
    {
        var prefix = StagingPrefix(s, sessionId);
        void End()
        {
            try
            {
                using var conn = factory.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
                FileSessionStore.DeleteAsync(new DataSession(conn, null), sessionId, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ending the file session {Session} failed; the cleanup sweep will delete it.", sessionId);
            }
            Enqueue(prefix);
        }
        if (s.Transaction is null) End();
        else s.OnRolledBack(End);
    }

    /// <summary>Runs <paramref name="update"/> with <paramref name="grant"/> pending, so that the Core's Update writes the file values.</summary>
    private async Task UpdateWithGrantAsync(DataSession s, FileCommitGrant grant, RecordUpdate update, CancellationToken ct)
    {
        var grants = _grants.GetValue(s, static _ => []);
        grants.Add(grant);
        try
        {
            await update(new Entity(grant.Entity, grant.RecordId) { [grant.Attribute] = grant.FileId }, ct);
        }
        finally
        {
            grants.Remove(grant);
        }
    }

    /// <summary>Deletes the <c>fileattachment</c> row in the transaction and its blob after the commit.</summary>
    internal async Task ReleaseAsync(DataSession s, Guid fileId, CancellationToken ct)
    {
        await using (var cmd = s.Command($"DELETE FROM {s.DataTable(Attachment)} WHERE {s.Dialect.Quote(AttachmentId)} = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, fileId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var key = FileKey(s, fileId);
        s.OnCommitted(() => Enqueue(key));
    }

    private async Task DeleteAttachmentsAsync(DataSession s, string where, CancellationToken ct,
        params (string Name, DbType Type, object Value)[] parameters)
    {
        if (metadata.GetEntity(Attachment) is null) return;
        var ids = new List<Guid>();
        // Plan 2.7a, Task 6: on the path of every table and record delete (a P11 path), so in the dialect's SQL.
        var d = s.Dialect;
        await using (var cmd = s.Command(
                         $"DELETE FROM {s.DataTable(Attachment)}{d.ReturningInline([d.Quote(AttachmentId)], RowImage.Deleted)} " +
                         $"WHERE {where}{d.ReturningTrailing([d.Quote(AttachmentId)], RowImage.Deleted)}"))
        {
            foreach (var (name, type, value) in parameters) cmd.AddParameter(name, type, value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        }
        var keys = ids.Select(id => FileKey(s, id)).ToList();
        if (keys.Count > 0) s.OnCommitted(() => keys.ForEach(Enqueue));
    }

    internal async Task InsertAttachmentAsync(DataSession s, EntityMetadata m, Guid recordId, string attribute, Guid fileId, string? fileName,
        string mimeType, long size, CancellationToken ct)
    {
        var attachment = Entity(Attachment);
        var known = (attachment.Attributes ?? []).Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);
        var now = UtcNow;
        var values = new List<(string Column, DbType Type, object? Value)>
        {
            (AttachmentId, DbType.Guid, fileId),
            ("filename", DbType.String, fileName),
            ("mimetype", DbType.String, mimeType),
            ("filesizeinbytes", DbType.Int64, size),
            ("regardingfieldname", DbType.String, attribute),
            ("objecttypecode", DbType.String, m.LogicalName),
            (FileColumns.AttachmentLookup, DbType.Guid, recordId),
            (FileColumns.AttachmentLookup + TypeMapping.TypeColumnSuffix, DbType.Int32, m.ObjectTypeCode),
            ("createdon", DbType.DateTime2, now),
            ("modifiedon", DbType.DateTime2, now),
            ("statecode", DbType.Int32, 0),
            ("statuscode", DbType.Int32, 1),
        };
        // The system columns the table has (createdon, statecode, ...), the file columns always.
        values = values.Where(v => known.Contains(v.Column) || v.Column == FileColumns.AttachmentLookup + TypeMapping.TypeColumnSuffix).ToList();
        await using var cmd = s.Command(
            $"INSERT INTO {s.DataTable(Attachment)} ({string.Join(", ", values.Select(v => s.Dialect.Quote(v.Column)))}) " +
            $"VALUES ({string.Join(", ", values.Select((_, i) => "@p" + i))})");
        for (var i = 0; i < values.Count; i++)
            cmd.AddParameter("@p" + i, values[i].Type, values[i].Value, values[i].Type == DbType.String ? -1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The current value of a uniqueidentifier column (a file column, an image id), read with an update lock; null when empty or
    /// when the record does not exist.
    /// </summary>
    private static async Task<Guid?> ColumnValueAsync(DataSession s, EntityMetadata m, Guid id, string attribute, CancellationToken ct)
    {
        // Plan 2.7b, Task 6: on PostgreSQL the record's key lock comes first, as the Core's row lock takes it (key lock, then row
        // lock), so that a file write and an Update of the same record take their locks in the same order.
        await s.KeyLockAsync(m.LogicalName, id, ct);
        var d = s.Dialect;
        var key = d.Quote(SystemAttributes.PrimaryIdName(m));
        await using var cmd = s.Command(
            $"SELECT {d.Quote(attribute)} FROM {s.DataTable(m.LogicalName)}{d.LockHint(RowLock.UpdateRange)} WHERE {key} = @id{d.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter("@id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    private static async Task EnsureRecordExistsAsync(DataSession s, EntityMetadata m, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT 1 FROM {s.DataTable(m.LogicalName)} WHERE {s.Dialect.Quote(SystemAttributes.PrimaryIdName(m))} = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        if (await cmd.ExecuteScalarAsync(ct) is null) throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, id);
    }

    /// <summary>The blocks of <paramref name="blockList"/>, in its order: each one staged, each staged one listed, none twice.</summary>
    private static List<StagedBlock> OrderedBlocks(FileSession session, string[]? blockList)
    {
        if (blockList is null) throw XrmdFaultException.InvalidArgument("Required parameter 'BlockList' of request 'CommitFileBlocksUpload' is missing.");
        var staged = session.Blocks.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<StagedBlock>();
        foreach (var id in blockList)
        {
            if (!staged.TryGetValue(id ?? "", out var block))
                throw XrmdFaultException.InvalidArgument($"BlockList names the block '{id}', which was not uploaded in this session.");
            if (!seen.Add(block.Id))
                throw XrmdFaultException.InvalidArgument($"BlockList names the block '{id}' more than once.");
            result.Add(block);
        }
        if (result.Count != staged.Count)
            throw XrmdFaultException.InvalidArgument(
                $"BlockList omits {staged.Count - result.Count} of the uploaded blocks: it must name every block uploaded in this session.");
        return result;
    }

    /// <summary>The decoded length of a block id: valid Base64 of 1 to <see cref="MaxBlockIdBytes"/> bytes.</summary>
    private static int BlockIdLength(string? blockId)
    {
        if (string.IsNullOrEmpty(blockId)) throw XrmdFaultException.InvalidArgument("Required parameter 'BlockId' of request 'UploadBlock' is missing.");
        var buffer = new byte[blockId.Length];
        if (!Convert.TryFromBase64String(blockId, buffer, out var length) || length == 0)
            throw XrmdFaultException.InvalidArgument($"BlockId '{blockId}' is not a valid Base64 string.");
        if (length > MaxBlockIdBytes)
            throw XrmdFaultException.InvalidArgument($"BlockId '{blockId}' is {length} bytes long once decoded; a block id is at most {MaxBlockIdBytes} bytes.");
        return length;
    }

    private static async Task<byte[]> ReadBlockAsync(Stream data, CancellationToken ct)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await data.ReadAsync(buffer, ct)) > 0)
        {
            copy.Write(buffer, 0, read);
            if (copy.Length > MaxBlockSize)
                throw XrmdFaultException.InvalidArgument($"A block is at most {MaxBlockSize} bytes (4 MB).");
        }
        return copy.ToArray();
    }

    private static void CheckFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) throw XrmdFaultException.InvalidArgument("Required parameter 'FileName' is missing.");
        if (fileName.Length > FileColumns.FileNameLength)
            throw XrmdFaultException.InvalidArgument($"The file name is {fileName.Length} characters long; it is at most {FileColumns.FileNameLength}.");
    }

    private EntityMetadata Entity(string? logicalName) =>
        (logicalName is null ? null : metadata.GetEntity(logicalName))
        ?? throw XrmdFaultException.InvalidArgument($"The entity with a name = '{logicalName}' with namemapping = 'Logical' was not found in the MetadataCache.");

    /// <summary>The file or image column the upload messages write.</summary>
    /// <exception cref="XrmdFaultException">400 for a missing attribute or one of another type.</exception>
    private static AttributeMetadata UploadColumn(EntityMetadata m, string? attribute) =>
        (m.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attribute) switch
        {
            { } column when FileColumns.IsFileOrImage(column) => column,
            null => throw XrmdFaultException.InvalidArgument($"Attribute '{attribute}' not found on entity '{m.LogicalName}'."),
            _ => throw XrmdFaultException.InvalidArgument($"Attribute '{attribute}' of entity '{m.LogicalName}' is not a file or image column."),
        };

    /// <summary>The file or image column of a session (it may have been deleted since the session opened).</summary>
    private AttributeMetadata SessionColumn(FileSession session) => UploadColumn(Entity(session.Entity), session.Attribute);

    private static long MaxBytes(AttributeMetadata column) => column switch
    {
        ImageAttributeMetadata image => (long)(image.MaxSizeInKB ?? FileColumns.DefaultImageMaxSizeInKB) * 1024,
        FileAttributeMetadata file => (long)(file.MaxSizeInKB ?? FileColumns.DefaultFileMaxSizeInKB) * 1024,
        _ => throw new ArgumentException($"'{column.LogicalName}' is not a file or image column.", nameof(column)),
    };

    /// <summary>The refusal of a file (0x80044a02) or an image (0x80072553) over the column's <c>MaxSizeInKB</c>.</summary>
    private static XrmdFaultException TooBig(AttributeMetadata column, long size) =>
        column is ImageAttributeMetadata ? XrmdFaultException.ImageTooBig(MaxBytes(column), size) : XrmdFaultException.AttachmentFileSizeTooBig();

    /// <summary>The uniqueidentifier column that names a column's file: the file column itself, the image id of an image column.</summary>
    private static string IdColumn(AttributeMetadata column) =>
        column is ImageAttributeMetadata ? column.LogicalName + FileColumns.ImageIdSuffix : column.LogicalName;

    private static string NameColumn(string fileColumn) => fileColumn + "_name";

    /// <summary>The token key of the session's organization database (derived once per database).</summary>
    private async Task<FileSessionTokens> TokensAsync(DataSession s, CancellationToken ct)
    {
        var database = Database(s);
        if (_tokens.TryGetValue(database, out var tokens)) return tokens;
        var secret = await FileSessionStore.SecretAsync(s, ct);
        await using var cmd = s.Command(s.Dialect.Limit($"SELECT organizationid FROM {s.DataTable("organization")}", "1"));
        var organization = await cmd.ExecuteScalarAsync(ct) as Guid?
                           ?? throw new InvalidOperationException("The organization has not been seeded (data.organization is empty).");
        return _tokens.GetOrAdd(database, FileSessionTokens.Derive(secret, organization));
    }

    /// <summary>The organization database of the session: the first segment of its blob keys.</summary>
    /// <exception cref="InvalidOperationException">A database name that is not one key segment (<see cref="FileKeys"/>).</exception>
    private static string Database(DataSession s)
    {
        var database = s.Connection.Database;
        try
        {
            if (database.Contains('/')) throw new ArgumentException("'/' separates key segments.", nameof(s));
            FileKeys.ValidateKey(database);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"The organization database name '{database}' cannot prefix the file store keys: use letters, digits, '.', '_' or '-'.", ex);
        }
        return database;
    }

    internal static string FileKey(DataSession s, Guid fileId) => $"{Database(s)}/{fileId}";

    private static string StagingPrefix(DataSession s, Guid sessionId) => $"{Database(s)}/{UploadFolder}/{sessionId}/";

    private static string StagingKey(DataSession s, Guid sessionId, int index) =>
        StagingPrefix(s, sessionId) + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Hands the deletion of a blob or a prefix (ending with <c>/</c>) to the background queue, after a commit or a rollback;
    /// never blocks nor throws. A full queue leaves it to the orphan sweep.
    /// </summary>
    internal void Enqueue(string keyOrPrefix)
    {
        if (!_deletions.TryEnqueue(keyOrPrefix))
            _logger.LogWarning("The file deletion queue is full: {Key} is left to the cleanup sweep.", keyOrPrefix);
    }

    /// <summary>
    /// Waits until the queued blob deletions have run (at most <paramref name="timeout"/>), for an operation that must not race
    /// with them: a restore writes blobs whose keys a queued deletion may name (a file released after the backup was taken).
    /// </summary>
    /// <returns>False when deletions are still pending after the timeout.</returns>
    public async Task<bool> WhenDeletionsIdleAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _deletions.WhenIdleAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Drains the <see cref="FileDeletionQueue"/> until <paramref name="ct"/> is cancelled: each blob or prefix is deleted from the
    /// store, one at a time. A failed deletion is logged and left to <see cref="SweepAsync"/>; items still queued when the host
    /// stops are left to it too.
    /// </summary>
    public async Task ProcessDeletionsAsync(CancellationToken ct)
    {
        while (true)
        {
            string item;
            try
            {
                item = await _deletions.ReadAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            try
            {
                if (item.EndsWith('/')) await store.DeletePrefixAsync(item, ct);
                else await store.DeleteAsync(item, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deleting the blob or staged blocks {Key} failed; the cleanup sweep will delete them.", item);
            }
            finally
            {
                _deletions.Processed();
            }
        }
    }
}
