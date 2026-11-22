using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.WebApi;

namespace Xrmd.Host;

/// <summary>
/// <c>GET /Image/download.aspx?Entity=&lt;entity&gt;&amp;Attribute=&lt;image&gt;&amp;Id=&lt;image id&gt;&amp;Timestamp=&lt;ts&gt;[&amp;Full=true]</c>
/// (ruling F10; Learn, image column data, "Download URL"): the relative url an image column's <c>&lt;image&gt;_url</c> holds.
/// It serves the thumbnail, or with <c>Full=true</c> the full-sized image; a column that keeps no full image "returns no data"
/// (204, as the Web API's <c>?size=full</c>). It requires the Web API's authentication. <c>Id</c> is the image id
/// (<c>&lt;image&gt;id</c>), not the record id; an id no record holds any more (the image was replaced or deleted) is a 404.
/// <c>Timestamp</c> only makes the url change with the image (it defeats caches) and is not checked. Once the record is found,
/// the image is read through the pipeline as the Web API's <c>$value</c> reads it (<see cref="FileDownloads"/>): the thumbnail
/// with a Retrieve of the column, the full image with InitializeFileBlocksDownload and DownloadBlock, so Read is demanded and
/// plug-in steps run. The response's type is the one the image's content shows (GIF, JPEG, BMP or PNG), never a client-sent
/// MIME type, with <c>X-Content-Type-Options: nosniff</c>.
/// </summary>
internal static class ImageDownloadEndpoint
{
    public static void MapImageDownload(this IEndpointRouteBuilder app) =>
        app.MapGet(FileColumns.DownloadPath, DownloadAsync).RequireAuthorization();

    private static async Task<IResult> DownloadAsync(HttpContext context, FileColumnService files, IDbConnectionFactory factory)
    {
        var ct = context.RequestAborted;
        var query = context.Request.Query;
        var entity = Required(query, "Entity");
        var attribute = Required(query, "Attribute");
        if (!Guid.TryParse(Required(query, "Id"), out var imageId))
            throw XrmdFaultException.InvalidArgument("The query parameter 'Id' is not a valid GUID.");
        var full = query["Full"].ToString() switch
        {
            "" => false,
            var value when bool.TryParse(value, out var parsed) => parsed,
            var value => throw XrmdFaultException.InvalidArgument($"The query parameter 'Full' is '{value}'; it is 'true' or 'false'."),
        };

        Guid recordId;
        await using (var conn = await factory.OpenAsync(ct))
            recordId = await files.FindImageRecordAsync(entity, attribute, imageId, new DataSession(conn, null), ct)
                       ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist,
                           $"No record of {entity} holds the image with Id = {imageId} in its column '{attribute}'.", 404);
        var record = new EntityReference(entity, recordId);
        var headers = context.Response.Headers;

        if (!full)
        {
            var thumbnail = await FileDownloads.ThumbnailAsync(context, record, attribute, ct);
            headers.XContentTypeOptions = "nosniff";
            // The url changes with the image (its Timestamp): the caller's browser may keep a response for a day.
            headers.CacheControl = "private, max-age=86400";
            return Results.Bytes(thumbnail, FileColumnService.ThumbnailMimeType(thumbnail));
        }

        BlockDownload download;
        try
        {
            download = await FileDownloads.StartAsync(context, record, attribute, null, ct);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.ObjectDoesNotExist)
        {
            // The image was set while CanStoreFullImage was false (or is gone since): no full-sized image to return.
            return Results.NoContent();
        }
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = "private, max-age=86400";
        return new FullImageResult(download, ImageType(download.FirstBlock));
    }

    /// <summary>The MIME type the image's first bytes show (the full image passed the thumbnailer's type check when it was set).</summary>
    private static string ImageType(ReadOnlyMemory<byte> head)
    {
        try
        {
            return Thumbnailer.MimeTypeOf(head.ToArray());
        }
        catch (ImageProcessingException)
        {
            return "application/octet-stream";
        }
    }

    /// <summary>The full image, streamed block by block.</summary>
    private sealed class FullImageResult(BlockDownload download, string contentType) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = contentType;
            response.ContentLength = download.Length;
            await download.CopyToAsync(response.Body, httpContext.RequestAborted);
        }
    }

    private static string Required(IQueryCollection query, string name) =>
        query[name].ToString() is { Length: > 0 } value
            ? value
            : throw XrmdFaultException.InvalidArgument($"The query parameter '{name}' is required.");
}
