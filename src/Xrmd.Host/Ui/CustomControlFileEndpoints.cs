using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Host.Ui;

/// <summary>
/// <c>GET /Controls/{name}/{*path}</c> (plan 3.5 UI-1d, task 3, ruling R5): serves one file of the custom control whose <c>name</c>
/// is <c>{name}</c> (case-insensitive), at <c>{path}</c> relative to its manifest folder as the manifest writes it (e.g.
/// <c>bundle.js</c>, <c>strings/Name.1033.resx</c>, <c>ControlManifest.xml</c>). The <c>customcontrol</c> row is read through
/// <see cref="OrganizationService"/> as the caller (403 without read on <c>customcontrol</c>), the file from
/// <see cref="ICustomControlFiles"/>; an unknown control or path, or a path that tries to leave the folder, is a 404. The ETag is the
/// SHA-256 of the content, so a re-imported bundle gets a new one.
/// </summary>
internal static class CustomControlFileEndpoints
{
    private const string CacheControl = "private, max-age=0, must-revalidate";

    // Extension -> content type (Global Constraints of plan UI-1d); anything else is application/octet-stream.
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".resx"] = "application/xml",
        [".xml"] = "application/xml",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".gif"] = "image/gif",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };

    public static void MapCustomControlFiles(this IEndpointRouteBuilder app) =>
        app.MapGet("/Controls/{name}/{**path}", (HttpContext context, string name, string? path, OrganizationService service, ICustomControlFiles files) =>
            ServeAsync(context, name, path, service, files)).RequireAuthorization();

    /// <summary>
    /// A path the manifest could name: relative, segments separated by <c>/</c> only, no empty, <c>.</c> or <c>..</c> segment, no
    /// backslash or drive colon, and no encoded separator (Kestrel leaves <c>%2F</c> undecoded).
    /// </summary>
    internal static bool IsSafePath(string? path) =>
        !string.IsNullOrEmpty(path)
        && path.Length <= CustomControlFiles.MaxPathLength
        && path.IndexOfAny(['\\', ':', '\0']) < 0
        && !path.Contains("%2F", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("%5C", StringComparison.OrdinalIgnoreCase)
        && path.Split('/').All(segment => segment is not ("" or "." or ".."));

    private static async Task<IResult> ServeAsync(HttpContext context, string name, string? path, OrganizationService service, ICustomControlFiles files)
    {
        if (!IsSafePath(path)) return Results.NotFound();
        var ct = context.RequestAborted;

        Entity? row;
        try
        {
            var query = new QueryExpression("customcontrol") { ColumnSet = new ColumnSet("name"), TopCount = 2 };
            query.Criteria.AddCondition("name", ConditionOperator.Equal, name);
            row = (await service.RetrieveMultipleAsync(query, ct)).Entities
                .FirstOrDefault(e => string.Equals(e.GetAttributeValue<string>("name"), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (XrmdFaultException ex) when (ex.HttpStatus == 403)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        if (row is null) return Results.NotFound();

        if (await files.GetAsync(row.Id, path!, ct) is not { } file) return Results.NotFound();

        var etag = "\"" + file.Sha256 + "\"";
        var headers = context.Response.Headers;
        headers.ETag = etag;
        headers.CacheControl = CacheControl;
        headers.XContentTypeOptions = "nosniff";
        if (context.Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',').Select(t => t.Trim()).Any(t => t == etag || t == "W/" + etag)))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        var type = ContentTypes.GetValueOrDefault(Path.GetExtension(path!), "application/octet-stream");
        return Results.Bytes(file.Content, type);
    }
}
