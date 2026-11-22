using System.Globalization;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Microsoft.Net.Http.Headers;

namespace Xrmd.Host.Ui;

/// <summary>
/// <c>GET /WebResources/{name}</c> and <c>GET /%7b{token}%7d/WebResources/{name}</c> (plan 3.5 UI-1c, task 1): serves the
/// <c>content</c> (base64) of the <c>webresource</c> row whose <c>name</c> is the whole path after <c>/WebResources/</c>,
/// case-insensitively. The cache-busting prefix is a path segment in braces (Kestrel decodes <c>%7b</c> and <c>%7d</c> before
/// routing, so the encoded and decoded forms arrive alike); its content is ignored.
/// The row is read through <see cref="OrganizationService"/> as the caller, the same pipeline and Read check as a Web API retrieve;
/// a web resource that is missing or that the caller may not read is a 404. The type comes from <c>webresourcetype</c>.
/// </summary>
internal static class WebResourceEndpoints
{
    // Type -> content type. Types are the values of the webresourcetype choice (Learn: "Web Resource (WebResource) table/entity
    // reference", https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/webresource). Learn gives no
    // content types: those are the standard ones (ruling: ICO is image/x-icon; XSL, RESX and XML are text/xml).
    // Type 8 (Silverlight, XAP) is refused with 415: a browser cannot run it.
    private static readonly Dictionary<int, (string Type, bool Text)> ContentTypes = new()
    {
        [1] = ("text/html", true),
        [2] = ("text/css", true),
        [3] = ("application/javascript", true),
        [4] = ("text/xml", true),
        [5] = ("image/png", false),
        [6] = ("image/jpeg", false),
        [7] = ("image/gif", false),
        [9] = ("text/xml", true),
        [10] = ("image/x-icon", false),
        [11] = ("image/svg+xml", true),
        [12] = ("text/xml", true),
    };

    public static void MapWebResources(this IEndpointRouteBuilder app)
    {
        app.MapGet("/WebResources/{**name}", (HttpContext context, string name, OrganizationService service) =>
            ServeAsync(context, name, service)).RequireAuthorization();
        app.MapGet("/{prefix}/WebResources/{**name}", (HttpContext context, string prefix, string name, OrganizationService service) =>
            IsCacheBustingPrefix(prefix) ? ServeAsync(context, name, service) : Task.FromResult(Results.NotFound()))
            .RequireAuthorization();
    }

    private static bool IsCacheBustingPrefix(string segment) => segment.Length >= 2 && segment[0] == '{' && segment[^1] == '}';

    private static async Task<IResult> ServeAsync(HttpContext context, string name, OrganizationService service)
    {
        var ct = context.RequestAborted;
        Entity? row;
        try
        {
            var query = new QueryExpression("webresource") { ColumnSet = new ColumnSet("name", "webresourcetype", "modifiedon"), TopCount = 2 };
            query.Criteria.AddCondition("name", ConditionOperator.Equal, name);
            row = (await service.RetrieveMultipleAsync(query, ct)).Entities
                .FirstOrDefault(e => string.Equals(e.GetAttributeValue<string>("name"), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (XrmdFaultException ex) when (ex.HttpStatus == 403)
        {
            // Not allowed to read web resources: the same answer as a missing one, so names do not leak.
            return Results.NotFound();
        }
        if (row is null) return Results.NotFound();

        var type = row.GetAttributeValue<OptionSetValue>("webresourcetype")?.Value ?? 0;
        if (!ContentTypes.TryGetValue(type, out var contentType))
            return Results.Problem($"Web resources of type {type} are not served.", statusCode: StatusCodes.Status415UnsupportedMediaType);

        var etag = "W/\"" + row.GetAttributeValue<DateTime>("modifiedon").Ticks.ToString(CultureInfo.InvariantCulture) + "\"";
        var headers = context.Response.Headers;
        headers.ETag = etag;
        headers.CacheControl = "private, no-cache";
        headers.XContentTypeOptions = "nosniff";
        if (context.Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',').Select(t => t.Trim()).Contains(etag)))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        var full = await service.RetrieveAsync("webresource", row.Id, new ColumnSet("content"), ct);
        var bytes = full.GetAttributeValue<string>("content") is { Length: > 0 } base64 ? Convert.FromBase64String(base64) : [];
        return Results.Bytes(bytes, contentType.Text ? contentType.Type + "; charset=utf-8" : contentType.Type);
    }
}
