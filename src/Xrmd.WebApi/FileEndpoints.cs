using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Elastic;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.WebApi;

/// <summary>
/// The file and image column properties of a record (Learn, "Use file column data" and "Use image column data"), on
/// <c>{entityset}({id})/{column}</c>, through the shared <see cref="FileColumnService"/>:
/// <list type="bullet">
/// <item><c>PATCH</c> with the file as an <c>application/octet-stream</c> body: a single-request upload of at most 128 MB
/// (413 beyond); the name is the <c>x-ms-file-name</c> header (ASCII only) or, without it, the <c>x-ms-file-name</c> query
/// parameter. 204.</item>
/// <item><c>PATCH</c> with <c>x-ms-transfer-mode: chunked</c> and no body: opens a chunked upload; 200 with
/// <c>Location: …?sessiontoken=…</c> (the session's file continuation token, ruling F6), <c>x-ms-chunk-size: 4194304</c>,
/// <c>Accept-Ranges: bytes</c> and <c>Access-Control-Expose-Headers: x-ms-chunk-size</c>. Each <c>PATCH</c> on the
/// <c>Location</c> carries <c>Content-Range: bytes a-b/total</c> and that chunk (at most 4 MB); the chunks must come in order
/// (see <see cref="FileColumnService.ChunkBlockId"/>). 206 until the last byte, then 204.</item>
/// <item><c>GET …/$value</c>: the file, with <c>x-ms-file-size</c>, <c>x-ms-file-name</c>, <c>mimetype</c> and
/// <c>Access-Control-Expose-Headers</c>; 200, or 206 for a <c>Range: bytes=a-b</c> (adding <c>x-ms-chunk-size</c>). On an image
/// column the thumbnail, or with <c>?size=full</c> the full image (204 when none was kept).</item>
/// <item><c>GET</c> without <c>$value</c>: the property as JSON (the file id; the thumbnail of an image, base64); 204 when
/// empty.</item>
/// <item><c>DELETE</c>: clears the column (the file, or the image and its full image). 204.</item>
/// </list>
/// The MIME type of an upload is the <c>mimetype</c> request header when sent; otherwise it follows the file name's extension
/// (<c>application/octet-stream</c> for an unknown one), as Learn's examples show Dataverse answering <c>text/plain</c> for a
/// <c>.txt</c> sent as <c>application/octet-stream</c>. Every write is the SDK path's: an upload is the messages
/// InitializeFileBlocksUpload (Write demanded), UploadBlock and CommitFileBlocksUpload, and a delete an Update of the column
/// to null, each executed by <see cref="OrganizationService"/> through the pipeline. Every read is too (<see cref="FileDownloads"/>):
/// a file or full image is InitializeFileBlocksDownload (Read demanded) and DownloadBlock, a thumbnail a Retrieve of the column.
/// Any other column of the record is refused explicitly (400). A file request in a <c>$batch</c> change set is refused (400,
/// ruling F12 and #130): these endpoints have no <see cref="ChangeSetEndpoint"/> metadata.
/// </summary>
public static partial class FileEndpoints
{
    public const string FileNameHeader = "x-ms-file-name";
    public const string TransferModeHeader = "x-ms-transfer-mode";
    public const string ChunkSizeHeader = "x-ms-chunk-size";
    public const string FileSizeHeader = "x-ms-file-size";
    public const string MimeTypeHeader = "mimetype";
    public const string SessionTokenParameter = "sessiontoken";
    private const string ExposeHeaders = "Access-Control-Expose-Headers";
    private const string OctetStream = "application/octet-stream";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        // A single-request upload takes up to Dataverse's 128 MB (Learn); a chunk is refused beyond 4 MB by the service.
        SolutionFileSizeLimit.ApplyTo(routes.MapPatch("/{segment}/{property}", PatchAsync));
        routes.MapGet("/{segment}/{property}", GetPropertyAsync);
        routes.MapGet("/{segment}/{property}/$value", GetValueAsync);
        routes.MapDelete("/{segment}/{property}", DeleteAsync);
        return parent;
    }

    // ---- upload -------------------------------------------------------------------------------------------------------

    private static async Task<IResult> PatchAsync(HttpContext context, IMetadataStore store, OrganizationService service,
        string segment, string property)
    {
        var (_, target) = Record(store, segment, property);
        var request = context.Request;
        var token = request.Query[SessionTokenParameter].ToString();
        if (token.Length > 0) return await UploadChunkAsync(context, service, target, property, token);

        var mode = request.Headers[TransferModeHeader].ToString();
        if (string.Equals(mode, "chunked", StringComparison.OrdinalIgnoreCase)) return await StartChunkedAsync(context, service, target, property);
        if (mode.Length > 0)
            throw XrmdFaultException.InvalidArgument($"The {TransferModeHeader} header is '{mode}': the only transfer mode is 'chunked'.");
        return await UploadSingleAsync(context, service, target, property);
    }

    /// <summary>
    /// A single-request upload, as the SDK messages a client would send: InitializeFileBlocksUpload, one UploadBlock per 4 MB of
    /// the body, CommitFileBlocksUpload, each through the pipeline in a transaction of its own. A request that fails before the
    /// commit ends its session and deletes the staged blocks (best effort; the cleanup sweep covers a failure of that too).
    /// </summary>
    private static async Task<IResult> UploadSingleAsync(HttpContext context, OrganizationService service, EntityReference target, string property)
    {
        var request = context.Request;
        var ct = context.RequestAborted;
        if (request.ContentLength > SolutionFileSizeLimit.MaxBytes) throw TooLarge();
        if (request.ContentType is { } type && type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            throw XrmdFaultException.InvalidArgument(
                $"A file or image column is uploaded with an {OctetStream} body; a JSON property value cannot be written to '{property}'.");
        var name = FileName(request) ?? throw MissingFileName();
        var mimeType = MimeType(name, request.Headers[MimeTypeHeader].ToString());

        var token = await InitializeAsync(service, target, property, name, ct);
        try
        {
            var body = new LimitedStream(request.Body, SolutionFileSizeLimit.MaxBytes);
            var ids = new List<string>();
            var buffer = new byte[FileColumnService.MaxBlockSize];
            while (true)
            {
                var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
                if (read == 0) break;
                var id = Convert.ToBase64String(BitConverter.GetBytes((long)ids.Count));
                await service.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = id, BlockData = buffer[..read] }, ct);
                ids.Add(id);
                if (read < buffer.Length) break;
            }
            await service.ExecuteAsync(new CommitFileBlocksUploadRequest
            {
                FileContinuationToken = token, BlockList = [.. ids], FileName = name, MimeType = mimeType,
            }, ct);
        }
        catch
        {
            // The request failed after the session was opened (over 128 MB, a dropped connection, a refused block or commit):
            // end the session and delete its staged blocks, whatever the outcome, and let the original error through.
            await AbortAsync(context, token);
            throw;
        }
        return NoContent(context);
    }

    /// <summary>Best-effort end of the caller's upload session <paramref name="token"/>; never throws.</summary>
    private static async Task AbortAsync(HttpContext context, string token)
    {
        try
        {
            var services = context.RequestServices;
            var caller = await services.GetRequiredService<CallerResolver>().ResolveAsync(CancellationToken.None);
            await services.GetRequiredService<FileColumnService>().AbortUploadAsync(caller, token);
        }
        catch
        {
            // The cleanup sweep deletes an expired session and its blocks.
        }
    }

    private static async Task<string> InitializeAsync(OrganizationService service, EntityReference target, string property, string name,
        CancellationToken ct) =>
        ((InitializeFileBlocksUploadResponse)await service.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = target, FileAttributeName = property, FileName = name,
        }, ct)).FileContinuationToken;

    /// <summary>The first request of a chunked upload: InitializeFileBlocksUpload (Write demanded by the guard).</summary>
    private static async Task<IResult> StartChunkedAsync(HttpContext context, OrganizationService service, EntityReference target, string property)
    {
        var request = context.Request;
        if (request.ContentLength > 0)
            throw XrmdFaultException.InvalidArgument(
                $"The first request of a chunked upload ({TransferModeHeader}: chunked) has no body: send the chunks to the Location it returns.");
        var name = FileName(request) ?? throw MissingFileName();
        var token = await InitializeAsync(service, target, property, name, context.RequestAborted);

        var headers = context.Response.Headers;
        headers.Location = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}?{SessionTokenParameter}={Uri.EscapeDataString(token)}";
        headers[ChunkSizeHeader] = FileColumnService.MaxBlockSize.ToString(CultureInfo.InvariantCulture);
        headers.AcceptRanges = "bytes";
        headers[ExposeHeaders] = ChunkSizeHeader;
        headers["OData-Version"] = "4.0";
        return Results.Ok();
    }

    /// <summary>
    /// A chunk: an UploadBlock whose block id records the chunk's offset and total (<see cref="FileColumnService.ChunkBlockId"/>),
    /// which UploadBlock checks under the session's lock (in order, no gap, no overlap, same total). The chunk that ends at the
    /// last byte is followed by CommitFileBlocksUpload of the chunks in offset order, with the session's file name. The session
    /// is first read to check that it belongs to the url's record and column.
    /// </summary>
    private static async Task<IResult> UploadChunkAsync(HttpContext context, OrganizationService service, EntityReference target, string property,
        string token)
    {
        var request = context.Request;
        var ct = context.RequestAborted;
        var range = request.Headers.ContentRange.ToString();
        if (range.Length == 0)
            throw XrmdFaultException.InvalidArgument(
                $"A request of a chunked upload ({SessionTokenParameter}) carries its chunk with a Content-Range header: 'bytes <from>-<to>/<size>'.");
        var (from, to, total) = ParseContentRange(range);
        var length = to - from + 1;
        if (length > FileColumnService.MaxBlockSize)
            throw XrmdFaultException.InvalidArgument(
                $"Content-Range '{range}' is {length} bytes long: a chunk is at most {FileColumnService.MaxBlockSize} bytes ({ChunkSizeHeader}).");
        var data = new byte[length];
        var read = await request.Body.ReadAtLeastAsync(data, data.Length, throwOnEndOfStream: false, ct);
        if (read != length || await request.Body.ReadAsync(new byte[1], ct) != 0)
            throw XrmdFaultException.InvalidArgument($"The request body is not the {length} bytes Content-Range '{range}' announces.");

        var session = await DescribeAsync(context, token);
        if (session.Entity != target.LogicalName || session.RecordId != target.Id || session.Attribute != property)
            throw XrmdFaultException.InvalidArgument(FileColumnService.InvalidTokenMessage);

        await service.ExecuteAsync(new UploadBlockRequest
        {
            FileContinuationToken = token, BlockId = FileColumnService.ChunkBlockId(from, total), BlockData = data,
        }, ct);
        if (to + 1 < total)
        {
            context.Response.Headers["OData-Version"] = "4.0";
            return Results.StatusCode(StatusCodes.Status206PartialContent);
        }

        session = await DescribeAsync(context, token);
        var chunks = session.BlockIds.Select(id => FileColumnService.TryReadChunkBlockId(id, out var offset, out _)
            ? (Id: id, Offset: offset)
            : throw XrmdFaultException.InvalidArgument(
                "This upload session holds blocks sent with UploadBlock: commit it with CommitFileBlocksUpload."));
        await service.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [.. chunks.OrderBy(c => c.Offset).Select(c => c.Id)], FileName = session.FileName,
            MimeType = MimeType(session.FileName, request.Headers[MimeTypeHeader].ToString()),
        }, ct);
        return NoContent(context);
    }

    /// <summary>The caller's upload session of <paramref name="token"/> (a read).</summary>
    private static async Task<FileUploadSession> DescribeAsync(HttpContext context, string token)
    {
        var services = context.RequestServices;
        var ct = context.RequestAborted;
        var caller = await services.GetRequiredService<CallerResolver>().ResolveAsync(ct);
        await using var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
        return await services.GetRequiredService<FileColumnService>().DescribeUploadAsync(caller, token, new DataSession(conn, null), ct);
    }

    // ---- download -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>GET …/$value</c>, through the pipeline (<see cref="FileDownloads"/>): a file, or with <c>size=full</c> the full image of
    /// an image column, is read with InitializeFileBlocksDownload and DownloadBlock (a <c>Range</c> is the offset and length of
    /// the blocks); the thumbnail of an image column with a Retrieve of the column.
    /// </summary>
    private static async Task<IResult> GetValueAsync(HttpContext context, IMetadataStore store, string segment, string property)
    {
        var request = context.Request;
        UnmaskedData.RefuseQueryOptions(request.Query.Select(q => KeyValuePair.Create(q.Key, q.Value.ToString())));
        var (m, target) = Record(store, segment, property);
        var full = request.Query["size"].ToString() switch
        {
            "" => false,
            var value when string.Equals(value, "full", StringComparison.OrdinalIgnoreCase) => true,
            var value => throw XrmdFaultException.InvalidArgument($"The query option 'size' is '{value}': its only value is 'full'."),
        };
        var rangeHeader = request.Headers.Range.ToString();
        var range = rangeHeader.Length > 0 ? ParseRange(rangeHeader) : ((long From, long? To)?)null;
        var ct = context.RequestAborted;

        if (IsImage(m, property) && !full)
        {
            var thumbnail = await FileDownloads.ThumbnailAsync(context, target, property, ct);
            var mimeType = FileColumnService.ThumbnailMimeType(thumbnail);
            var (from, length) = FileDownloads.Range(range, thumbnail.Length);
            return Download(context, thumbnail.Length, $"{property}.{(mimeType == "image/jpeg" ? "jpg" : "png")}", mimeType, range is null ? null : (from, length),
                new MemoryStream(thumbnail, (int)from, (int)length, writable: false).CopyToAsync);
        }

        BlockDownload download;
        try
        {
            download = await FileDownloads.StartAsync(context, target, property, range, ct);
        }
        catch (XrmdFaultException ex) when (full && ex.ErrorCode == ErrorCodes.ObjectDoesNotExist && IsImage(m, property))
        {
            // No full image (Learn: CanStoreFullImage false, or false when the image was set): 204 when the record has an image
            // at all; a missing record or an empty column keeps its 404.
            await FileDownloads.ThumbnailAsync(context, target, property, ct);
            return NoContent(context);
        }

        string fileMimeType;
        try
        {
            var services = context.RequestServices;
            var caller = await services.GetRequiredService<CallerResolver>().ResolveAsync(ct);
            await using var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
            fileMimeType = await services.GetRequiredService<FileColumnService>().DownloadMimeTypeAsync(caller, download.Token, new DataSession(conn, null), ct);
        }
        catch
        {
            await download.EndAsync();
            throw;
        }
        return Download(context, download.FileSize, download.FileName, fileMimeType, range is null ? null : (download.From, download.Length),
            download.CopyToAsync);
    }

    /// <summary>
    /// The response of a <c>$value</c>: the file's headers, then 200 with the whole file or 206 with the range
    /// (<paramref name="range"/> is its offset and length; null without a <c>Range</c> request).
    /// </summary>
    private static IResult Download(HttpContext context, long fileSize, string fileName, string mimeType, (long From, long Length)? range,
        Func<Stream, CancellationToken, Task> copy)
    {
        var headers = context.Response.Headers;
        headers[FileSizeHeader] = fileSize.ToString(CultureInfo.InvariantCulture);
        headers[FileNameHeader] = HeaderValue(fileName);
        headers[MimeTypeHeader] = mimeType;
        headers["OData-Version"] = "4.0";
        // The body is sent as application/octet-stream: a browser must not sniff it into something it would render or run.
        headers.XContentTypeOptions = "nosniff";
        // An empty file has no byte a range could name (HTTP: a Range may be ignored): it is the whole, empty, file with 200.
        if (range is not { } r || fileSize == 0)
        {
            headers[ExposeHeaders] = $"{FileSizeHeader}; {FileNameHeader}; {MimeTypeHeader}";
            return new FileContentResult(copy, fileSize, StatusCodes.Status200OK, null);
        }
        headers[ChunkSizeHeader] = FileColumnService.MaxBlockSize.ToString(CultureInfo.InvariantCulture);
        headers[ExposeHeaders] = $"{FileSizeHeader}; {FileNameHeader}; {ChunkSizeHeader}; {MimeTypeHeader}";
        return new FileContentResult(copy, r.Length, StatusCodes.Status206PartialContent,
            string.Create(CultureInfo.InvariantCulture, $"bytes {r.From}-{r.From + r.Length - 1}/{fileSize}"));
    }

    /// <summary>
    /// <c>GET {entityset}({id})/{column}</c>: the property read with a Retrieve of that column (the pipeline, Read checked), as
    /// <c>{ "@odata.context": "…$metadata#{entityset}({id})/{column}", "value": … }</c>; 204 when it is null (OData).
    /// </summary>
    private static async Task<IResult> GetPropertyAsync(HttpContext context, IMetadataStore store, OrganizationService service,
        string segment, string property)
    {
        UnmaskedData.RefuseQueryOptions(context.Request.Query.Select(q => KeyValuePair.Create(q.Key, q.Value.ToString())));
        var (m, target) = Record(store, segment, property);
        var retrieve = new RetrieveRequest { Target = target, ColumnSet = new ColumnSet(property) };
        var entity = (Entity)(await service.ExecuteAsync(await InPartitionAsync(service, retrieve, target, context.RequestAborted),
            context.RequestAborted)).Results["Entity"];
        JsonNode? value = entity.Attributes.TryGetValue(property, out var v) ? v switch
        {
            null => null,
            Guid id => id.ToString(),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => throw new InvalidOperationException($"The column '{property}' has an unexpected value of type {v.GetType().Name}."),
        } : null;
        if (value is null) return NoContent(context);
        var json = new JsonObject
        {
            ["@odata.context"] = $"{MetadataController.ServiceRoot(context.Request)}$metadata#{m.EntitySetName}({target.Id})/{property}",
            ["value"] = value,
        };
        return ODataJson.Result(context, json);
    }

    // ---- delete -------------------------------------------------------------------------------------------------------

    /// <summary>The column set to null by an Update through the pipeline (ruling F4: the file, or the image, is deleted).</summary>
    private static async Task<IResult> DeleteAsync(HttpContext context, IMetadataStore store, OrganizationService service,
        string segment, string property)
    {
        var (_, target) = Record(store, segment, property);
        var update = new UpdateRequest { Target = new Entity(target.LogicalName, target.Id) { [property] = null } };
        await service.ExecuteAsync(await InPartitionAsync(service, update, target, context.RequestAborted), context.RequestAborted);
        return NoContent(context);
    }

    /// <summary>
    /// <paramref name="request"/> on the record <paramref name="target"/>, named by id alone: on an elastic table it carries the
    /// partition the row is stored in as the <c>partitionId</c> parameter, so a row stored with a partition is reached (plan 3.4,
    /// final review I2: the id is unique, ruling E1).
    /// </summary>
    private static async Task<OrganizationRequest> InPartitionAsync(OrganizationService service, OrganizationRequest request,
        EntityReference target, CancellationToken ct)
    {
        if (await service.StoredPartitionAsync(target.LogicalName, target.Id, ct) is { } partition)
            request[ElasticRecordKey.PartitionIdParameter] = partition;
        return request;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The entity and record of <c>{entityset}({id})</c>, once <paramref name="property"/> is known to be one of its file or image
    /// columns.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 for an unknown entity set; 400 for a segment without a key, an unknown property
    /// or a property that is not a file or image column.</exception>
    private static (EntityMetadata Entity, EntityReference Record) Record(IMetadataStore store, string segment, string property)
    {
        if (property.StartsWith('$'))
            throw XrmdFaultException.InvalidArgument(
                $"The path segment '{property}' is not supported by this server after '{segment}': only file and image columns are served as '{{entityset}}({{id}})/{{column}}'.");
        var (m, id, partition) = EntitySetController.Resolve(store, segment);
        // Plan 3.4: a file or image column is addressed by id alone (the row's stored partition is used), so a key naming a
        // partition is refused rather than dropped.
        if (partition is not null)
            throw XrmdFaultException.InvalidArgument(
                $"File and image columns of the elastic table '{m.LogicalName}' are not served by a key with partitionid: use '{m.EntitySetName}(<id>)/{property}'.");
        if (IsNavigationProperty(store, m, property)) throw NavigationPathSegment(property);
        var key = id ?? throw XrmdFaultException.InvalidArgument(
            $"A file or image column is addressed on a record: '{m.EntitySetName}(<id>)/{property}'.");
        var column = (m.Attributes ?? []).FirstOrDefault(a => a.LogicalName == property)
                     ?? throw XrmdFaultException.InvalidArgument(
                         $"Could not find a property named '{property}' on type '{ODataEdmModelBuilder.Namespace}.{m.LogicalName}'.");
        if (!FileColumns.IsFileOrImage(column))
            throw XrmdFaultException.InvalidArgument(
                $"The property '{property}' of {m.LogicalName} is not a file or image column: this server serves only file and image columns as properties.");
        return (m, new EntityReference(m.LogicalName, key));
    }

    /// <summary>
    /// A navigation property of <paramref name="m"/>: a lookup column (single-valued), or a one-to-many or many-to-many
    /// relationship of the entity by its schema name or navigation property name (collection-valued).
    /// </summary>
    internal static bool IsNavigationProperty(IMetadataStore store, EntityMetadata m, string property)
    {
        if ((m.Attributes ?? []).FirstOrDefault(a => a.LogicalName == property) is LookupAttributeMetadata) return true;
        if (store.GetOneToManyRelationships().Any(r =>
                (r.ReferencedEntity == m.LogicalName && (r.SchemaName == property || r.ReferencedEntityNavigationPropertyName == property))
                || (r.ReferencingEntity == m.LogicalName && r.ReferencingEntityNavigationPropertyName == property)))
            return true;
        return store.GetManyToManyRelationships().Any(r =>
            (r.Entity1LogicalName == m.LogicalName || r.Entity2LogicalName == m.LogicalName) && r.SchemaName == property);
    }

    /// <summary>400: a navigation property after a record (<c>{set}({id})/{nav}</c>, also with a key) is not served.</summary>
    internal static XrmdFaultException NavigationPathSegment(string property) =>
        XrmdFaultException.InvalidArgument(
            $"The navigation property '{property}' is not supported as a path segment by this server: read related records with $expand.");

    private static bool IsImage(EntityMetadata m, string property) =>
        (m.Attributes ?? []).FirstOrDefault(a => a.LogicalName == property) is ImageAttributeMetadata;

    private static IResult NoContent(HttpContext context)
    {
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.NoContent();
    }

    private static XrmdFaultException MissingFileName() =>
        XrmdFaultException.InvalidArgument($"The file name is missing: send it in the {FileNameHeader} header or the {FileNameHeader} query parameter.");

    private static XrmdFaultException TooLarge() =>
        new(ErrorCodes.InvalidArgument,
            $"The request body is larger than 128 MB ({SolutionFileSizeLimit.MaxBytes} bytes), the most a single-request upload carries: upload the file in chunks ({TransferModeHeader}: chunked).",
            StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// The file name of an upload request: the <c>x-ms-file-name</c> header, which takes precedence (Learn) and must be ASCII, or
    /// the <c>x-ms-file-name</c> query parameter (any character, URL-encoded); null when neither is sent.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a header with a character outside printable ASCII.</exception>
    public static string? FileName(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Headers[FileNameHeader].ToString();
        if (header.Length > 0)
            return header.All(c => c is >= ' ' and <= '~')
                ? header
                : throw XrmdFaultException.InvalidArgument(
                    $"The {FileNameHeader} header must be ASCII: send a file name with other characters in the {FileNameHeader} query parameter.");
        var query = request.Query[FileNameHeader].ToString();
        return query.Length > 0 ? query : null;
    }

    /// <summary>
    /// The MIME type of an upload: the <c>mimetype</c> header when sent, otherwise the type of the file name's extension, or
    /// <c>application/octet-stream</c> for an unknown one.
    /// </summary>
    public static string MimeType(string fileName, string? header) =>
        !string.IsNullOrWhiteSpace(header) ? header.Trim()
        : ContentTypes.TryGetContentType(fileName, out var type) ? type
        : OctetStream;

    /// <summary>
    /// A file name as a response header value: as it is when printable ASCII without <c>%</c>; percent-encoded (UTF-8) when it
    /// has another character or a <c>%</c>, so that a client decoding the header always gets the name back (an ASCII name that
    /// itself contains <c>%xx</c> would otherwise be decoded into another name).
    /// </summary>
    private static string HeaderValue(string fileName) =>
        fileName.All(c => c is >= ' ' and <= '~' and not '%') ? fileName : Uri.EscapeDataString(fileName);

    /// <summary><c>Content-Range: bytes from-to/total</c> of a chunk (both ends inclusive, within the total).</summary>
    /// <exception cref="XrmdFaultException">400 for any other form.</exception>
    public static (long From, long To, long Total) ParseContentRange(string header)
    {
        var match = ContentRangePattern().Match(header ?? "");
        if (match.Success
            && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var from)
            && long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var to)
            && long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            && from <= to && to < total)
            return (from, to, total);
        throw XrmdFaultException.InvalidArgument(
            $"Content-Range '{header}' is not valid: a chunk is sent with 'bytes <from>-<to>/<size>', <from> <= <to> < <size>.");
    }

    /// <summary><c>Range: bytes=from-to</c> or <c>bytes=from-</c> of a download (one range; <c>to</c> inclusive).</summary>
    /// <exception cref="XrmdFaultException">400 for any other form (several ranges, a suffix range, another unit).</exception>
    public static (long From, long? To) ParseRange(string header)
    {
        var match = RangePattern().Match(header ?? "");
        if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var from))
        {
            if (match.Groups[2].Value.Length == 0) return (from, null);
            if (long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var to) && to >= from) return (from, to);
        }
        throw XrmdFaultException.InvalidArgument(
            $"Range '{header}' is not supported: a download reads one range, 'bytes=<from>-<to>' or 'bytes=<from>-'.");
    }

    [GeneratedRegex(@"\Abytes (\d{1,18})-(\d{1,18})/(\d{1,18})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContentRangePattern();

    [GeneratedRegex(@"\Abytes=(\d{1,18})-(\d{0,18})\z", RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();

    /// <summary>The file's bytes as the response body (<c>application/octet-stream</c>), with its status and Content-Range.</summary>
    private sealed class FileContentResult(Func<Stream, CancellationToken, Task> copy, long length, int status, string? contentRange) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = status;
            response.ContentType = OctetStream;
            if (contentRange is not null) response.Headers.ContentRange = contentRange;
            response.ContentLength = length;
            await copy(response.Body, httpContext.RequestAborted);
        }
    }

    /// <summary>A request body read up to <paramref name="max"/> bytes: one byte more is the 413 of a single-request upload.</summary>
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            _read += read;
            return _read > max ? throw TooLarge() : read;
        }
    }
}
