using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Net.Http.Headers;
using Xrmd.Core.Errors;
using Xrmd.Host.Ui;
using Xrmd.WebApi;

namespace Xrmd.Host.Auth;

/// <summary>
/// The web session cookie (plan 3.5 UI-1c, task 2, ruling C1). The shell posts its access token to <c>POST /api/session</c> (bearer
/// header), which sets it as the <c>xrmd.session</c> cookie, so that requests a browser makes without the shell's help (a form
/// library's <c>&lt;script src&gt;</c>, an HTML web resource in an IFrame, a script's <c>fetch</c> to the Web API) carry the same
/// token. The cookie is read only when there is no <c>Authorization</c> header and only on the paths of <see cref="AcceptedOn"/>;
/// a cookie-authenticated write is refused (<see cref="UseXrmdSessionCsrf"/>) from another origin or with a content type that an
/// HTML form can send.
/// </summary>
public static class SessionCookie
{
    public const string Name = "xrmd.session";

    /// <summary>The <see cref="HttpContext.Items"/> key set when the request's token came from the cookie.</summary>
    internal const string FromCookieKey = "xrmd.session.cookie";

    /// <summary>Browsers drop a cookie whose name, value and attributes exceed 4096 bytes; the attributes need about 120.</summary>
    private const int MaxTokenLength = 3900;

    private static readonly string[] AcceptedPrefixes = ["/api/data", "/WebResources", "/Controls", "/Ribbon", "/ClientGlobalContext.js.aspx"];
    private static readonly string[] UnsafeMethods = [HttpMethods.Post, HttpMethods.Patch, HttpMethods.Put, HttpMethods.Delete];
    private static readonly string[] FormContentTypes = ["application/x-www-form-urlencoded", "multipart/form-data", "text/plain"];

    /// <summary>
    /// The paths on which the cookie stands in for the bearer header: the Web API, web resources (with or without the cache-busting
    /// prefix), custom control files (<c>/Controls</c>, plan 3.5 UI-1d), entity ribbon diffs (<c>/Ribbon</c>, UI-1e) and <c>ClientGlobalContext.js.aspx</c>. Never the Admin API, MCP, SOAP or <c>/api/session</c> itself.
    /// </summary>
    public static bool AcceptedOn(PathString path) =>
        AcceptedPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)) || UiHosting.IsPrefixedWebResource(path);

    /// <summary>
    /// <see cref="JwtBearerEvents.OnMessageReceived"/>: the cookie's token, when the request has no <c>Authorization</c> header and
    /// its path accepts the cookie. The token is then validated exactly as a bearer token.
    /// </summary>
    internal static Task ReadToken(MessageReceivedContext context)
    {
        var request = context.Request;
        if (context.Token is not null || request.Headers.ContainsKey(HeaderNames.Authorization) || !AcceptedOn(request.Path))
            return Task.CompletedTask;
        if (request.Cookies.TryGetValue(Name, out var token) && !string.IsNullOrEmpty(token))
        {
            context.Token = token;
            context.HttpContext.Items[FromCookieKey] = true;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// CSRF rules of a cookie-authenticated <c>POST</c>, <c>PATCH</c>, <c>PUT</c> or <c>DELETE</c>: 403 when its <c>Origin</c> is
    /// present and is not the request's own origin, or when its <c>Content-Type</c> is one an HTML form can send. After
    /// <c>UseAuthentication</c>, before the endpoint.
    /// </summary>
    public static IApplicationBuilder UseXrmdSessionCsrf(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Items.ContainsKey(FromCookieKey) && context.User.Identity?.IsAuthenticated == true
            && UnsafeMethods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase)
            && Refusal(context.Request) is { } reason)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; odata.metadata=minimal";
            context.Response.Headers["OData-Version"] = "4.0";
            var error = new JsonObject { ["code"] = ODataErrorMiddleware.FormatCode(ErrorCodes.AccessDenied), ["message"] = reason };
            await JsonSerializer.SerializeAsync(context.Response.Body, new JsonObject { ["error"] = error }, cancellationToken: context.RequestAborted);
            return;
        }
        await next(context);
    });

    private static string? Refusal(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (request.Headers.ContainsKey(HeaderNames.Origin) && !IsOwnOrigin(request, origin))
            return "A request authenticated by the session cookie must come from this origin.";
        if (MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            && FormContentTypes.Any(t => type.MediaType.Equals(t, StringComparison.OrdinalIgnoreCase)))
            return $"A request authenticated by the session cookie cannot have the content type {type.MediaType}.";
        return null;
    }

    /// <summary>The scheme, host and port the request arrived on (after the trusted proxy's forwarded headers, when configured).</summary>
    private static bool IsOwnOrigin(HttpRequest request, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" || origin.EndsWith('/')) return false;
        var scheme = request.Scheme;
        var port = request.Host.Port ?? (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        return string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
               && uri.Port == port;
    }

    /// <summary>
    /// <c>POST /api/session</c> and <c>DELETE /api/session</c>, both with the bearer header only (the cookie is not read on this
    /// path, so it can neither renew nor clear itself). A shell without a usable token skips the DELETE: the cookie then lapses at the
    /// token's <c>exp</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/session", (HttpContext context) => Set(context)).RequireAuthorization();
        app.MapDelete("/api/session", (HttpContext context) =>
        {
            context.Response.Cookies.Delete(Name, Options(context, expires: null));
            return Results.NoContent();
        }).RequireAuthorization();
        return app;
    }

    private static IResult Set(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        var token = header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : "";
        if (!long.TryParse(context.User.FindFirst("exp")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp) || token.Length == 0)
            throw new XrmdFaultException(ErrorCodes.InvalidArgument, "The session needs the access token in the Authorization header.", StatusCodes.Status400BadRequest);
        if (token.Length > MaxTokenLength)
            throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                $"The access token is {token.Length} characters long; a browser cannot keep more than {MaxTokenLength} in a cookie.",
                StatusCodes.Status400BadRequest);
        context.Response.Cookies.Append(Name, token, Options(context, DateTimeOffset.FromUnixTimeSeconds(exp)));
        return Results.NoContent();
    }

    /// <summary>No <c>Domain</c>: organizations are told apart by host name, so the cookie stays with the host that set it.</summary>
    private static CookieOptions Options(HttpContext context, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
        Path = "/",
        Secure = context.Request.IsHttps,
        Expires = expires,
        IsEssential = true,
    };
}
