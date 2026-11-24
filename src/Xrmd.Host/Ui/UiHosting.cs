using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Xrmd.Host.Auth;
using Xrmd.Host.Idp;

namespace Xrmd.Host.Ui;

/// <summary>
/// Hosts the web admin shell (a single-page app built to <c>ui/dist</c>) and the anonymous <c>/ui/config.json</c> it boots from.
/// Map it after every API route: the SPA fallback must never answer a server path.
/// </summary>
public static class UiHosting
{
    private const string PlaceholderResource = "Xrmd.Host.Ui.placeholder.index.html";
    private const string NoCache = "no-cache";
    private const string Immutable = "public, max-age=31536000, immutable";

    /// <summary>Paths that belong to the server; the fallback never serves the shell for them.</summary>
    private static readonly string[] ServerPaths = ["/api", "/admin", "/mcp", "/XRMServices", "/metrics", "/healthz", "/ui/config.json", "/WebResources", "/Controls", "/Ribbon", "/ClientGlobalContext.js.aspx", "/Image", "/adfs", "/.well-known"];

    /// <summary>Registers the routing policy that keeps the SPA fallback away from server paths (see <see cref="ServerPathFallbackPolicy"/>).</summary>
    public static IServiceCollection AddXrmdUi(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, ServerPathFallbackPolicy>());
        return services;
    }

    public static WebApplication MapXrmdUi(this WebApplication app)
    {
        var configuration = app.Configuration;
        var rootPath = configuration["Ui:RootPath"] is { Length: > 0 } p ? p : "wwwroot";
        var root = Path.GetFullPath(rootPath, app.Environment.ContentRootPath);
        var indexPath = Path.Combine(root, "index.html");

        app.MapGet("/ui/config.json", () =>
        {
            var auth = configuration.GetSection("Auth");
            var mode = auth["Mode"];
            var tenant = auth["TenantId"];
            // Local mode: the web admin is the built-in public client of the local identity provider (plan 2.8, L12).
            if (mode == "Local")
            {
                var audience = JwtSetup.LocalAudience(auth);
                return Results.Json(new UiConfig("Local", audience, JwtSetup.LocalIssuer(audience), IdpClients.Web, audience + ".default", "v9.2"));
            }
            // Only public values: never Auth:DevSigningKey.
            return Results.Json(new UiConfig(
                AuthMode: mode ?? "",
                Audience: JwtSetup.PrimaryAudience(auth) ?? "",
                Authority: mode == "Entra" && !string.IsNullOrEmpty(tenant) ? $"https://login.microsoftonline.com/{tenant}/v2.0" : null,
                ClientId: NullIfEmpty(configuration["Ui:Entra:ClientId"]),
                Scope: NullIfEmpty(configuration["Ui:Entra:Scope"]),
                ApiVersion: "v9.2"));
        }).AllowAnonymous();

        // Files are served from the fallback itself: the static-files middleware steps aside whenever an endpoint
        // (this fallback) matched the request.
        // Resolved lazily, so a UI built after the host started is served without a restart.
        IFileProvider? physical = null;
        IFileProvider Files()
        {
            if (physical is null && Directory.Exists(root)) physical = new PhysicalFileProvider(root);
            return physical ?? new NullFileProvider();
        }
        var contentTypes = new FileExtensionContentTypeProvider();

        // Server paths are kept out of the fallback's candidate set, so routing answers them itself: 405 for a wrong
        // method (e.g. GET /mcp, PUT on an entity set) and the authorization fallback policy's 401 for an unknown path
        // without a token. A plain MapFallback is a candidate for every method and would turn both into its own 404.
        // Two layers: ServerPathFallbackPolicy drops the fallback before routing's HTTP-method policy runs (a route
        // constraint alone is evaluated after it, too late to restore the 405), and the route constraint rejects a
        // server path wherever the fallback still reaches the candidate set.
        var pattern = RoutePatternFactory.Parse("{*path}", defaults: null, parameterPolicies: new { path = new NotServerPathConstraint() });
        app.Map(pattern, async context =>
        {
            var path = context.Request.Path;
            // The route constraint already excludes server paths; this check is a second line of defence.
            if (ServerPath(path) || IsTraversal(path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var method = context.Request.Method;
            if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "GET, HEAD";
                return;
            }

            // Real files (assets, favicon) win over the shell; anything else is a client-side route.
            var file = Files().GetFileInfo(path.Value ?? "");
            if (path != "/" && file.Exists && file.PhysicalPath is not null)
            {
                context.Response.Headers.CacheControl = path.StartsWithSegments("/assets") ? Immutable : NoCache;
                contentTypes.TryGetContentType(file.Name, out var type);
                await Results.File(file.PhysicalPath, type ?? "application/octet-stream").ExecuteAsync(context);
                return;
            }
            if (!ServesShell(path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.Headers.CacheControl = NoCache;
            context.Response.ContentType = "text/html; charset=utf-8";
            if (HttpMethods.IsHead(method)) return;
            if (File.Exists(indexPath)) await context.Response.SendFileAsync(indexPath);
            else await context.Response.WriteAsync(Placeholder.Value);
        }).AllowAnonymous().WithMetadata(UiFallbackMetadata.Instance).Add(b =>
        {
            // What MapFallback does: the lowest priority, after every other route.
            ((RouteEndpointBuilder)b).Order = int.MaxValue;
            b.DisplayName = "Fallback " + b.DisplayName;
        });
        return app;
    }

    /// <summary>
    /// Client-side routes get the shell; server paths and missing files (a path with an extension, e.g. a stale
    /// /assets/app-old.js, a stale `.js`) get a plain 404 instead. <c>/main.aspx</c> is a reserved shell route.
    /// </summary>
    private static bool ServesShell(PathString path)
    {
        if (ServerPath(path)) return false;
        if (path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)) return false;
        // A dot in the last segment is not a file unless it has a known static extension: deep links such as
        // /manage/plugins/Acme.Plugins must reload into the shell.
        return !StaticExtensions.Contains(Path.GetExtension(path.Value ?? ""));
    }

    private static readonly HashSet<string> StaticExtensions = new(
        [".js", ".css", ".map", ".png", ".jpg", ".svg", ".ico", ".woff", ".woff2", ".json", ".txt"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A parent-directory segment or a backslash, in any decoded form: never a client-side route.</summary>
    private static bool IsTraversal(PathString path) =>
        path.Value is { } v && (v.Contains('\\') || v.Split('/').Contains(".."));

    private static bool ServerPath(PathString path) =>
        ServerPaths.Any(server => path.StartsWithSegments(server, StringComparison.OrdinalIgnoreCase)) || IsPrefixedWebResource(path);

    /// <summary>The cache-busting form <c>/{token}/WebResources/…</c> (Kestrel decodes <c>%7b…%7d</c>).</summary>
    internal static bool IsPrefixedWebResource(PathString path)
    {
        if (path.Value is not { } v || !v.StartsWith("/{", StringComparison.Ordinal)) return false;
        var close = v.IndexOf('/', 1);
        return close > 2 && v[close - 1] == '}' && v.AsSpan(close).StartsWith("/WebResources", StringComparison.OrdinalIgnoreCase)
               && (v.Length == close + 13 || v[close + 13] == '/');
    }

    /// <summary>
    /// Rejects the catch-all value when it is a server path (case-insensitive). As a literal policy it also prunes the
    /// fallback from the routing tree under a server path's first segment, so routing's 405 endpoint is produced there.
    /// </summary>
    private sealed class NotServerPathConstraint : IRouteConstraint, IParameterLiteralNodeMatchingPolicy
    {
        public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection) =>
            !values.TryGetValue(routeKey, out var value) || value is null || !ServerPath(new PathString("/" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));

        public bool MatchesLiteral(string parameterName, string literal) => !ServerPath(new PathString("/" + literal));
    }

    /// <summary>Marks the SPA fallback endpoint for <see cref="ServerPathFallbackPolicy"/>.</summary>
    private sealed class UiFallbackMetadata
    {
        public static readonly UiFallbackMetadata Instance = new();
    }

    /// <summary>
    /// A routing tree policy, ordered before the HTTP-method policy: on a server path the SPA fallback is removed from the
    /// candidates, so the method policy sees only the server's own endpoints and produces its 405 (with Allow) when none
    /// accepts the method; with no endpoint at all the request reaches the authorization fallback policy (401) or a 404.
    /// </summary>
    private sealed class ServerPathFallbackPolicy : MatcherPolicy, INodeBuilderPolicy
    {
        private const int Other = 0;
        private const int Server = 1;

        // HttpMethodMatcherPolicy has order -1000; this one must run first.
        public override int Order => -1100;

        private static bool IsFallback(Endpoint e) => e.Metadata.GetMetadata<UiFallbackMetadata>() is not null;

        public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) => endpoints.Any(IsFallback);

        public IReadOnlyList<PolicyNodeEdge> GetEdges(IReadOnlyList<Endpoint> endpoints) =>
        [
            new PolicyNodeEdge(Other, endpoints),
            new PolicyNodeEdge(Server, endpoints.Where(e => !IsFallback(e)).ToArray()),
        ];

        public PolicyJumpTable BuildJumpTable(int exitDestination, IReadOnlyList<PolicyJumpTableEdge> edges)
        {
            int Destination(int state) => edges.FirstOrDefault(e => (int)e.State == state) is { State: not null } edge ? edge.Destination : exitDestination;
            return new JumpTable(Destination(Other), Destination(Server));
        }

        private sealed class JumpTable(int other, int server) : PolicyJumpTable
        {
            public override int GetDestination(HttpContext httpContext) => ServerPath(httpContext.Request.Path) ? server : other;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static readonly Lazy<string> Placeholder = new(ReadPlaceholder);

    private static string ReadPlaceholder()
    {
        using var stream = typeof(UiHosting).Assembly.GetManifestResourceStream(PlaceholderResource)
            ?? throw new InvalidOperationException($"Embedded resource {PlaceholderResource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record UiConfig(string AuthMode, string Audience, string? Authority, string? ClientId, string? Scope, string ApiVersion);
}
