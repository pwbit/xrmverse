using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Xrmd.Abstractions;
using Xrmd.Host.Idp;
using Xrmd.WebApi;

namespace Xrmd.Host.Auth;

public static class JwtSetup
{
    public const string DevIssuer = "xrmverse-dev";

    /// <summary>
    /// JWT bearer authentication; every endpoint requires an authenticated user unless it allows anonymous access
    /// (fallback policy). A host without Auth settings still starts (e.g. /healthz) and rejects every token; when
    /// Auth:Mode is set, an invalid configuration fails at startup instead of on the first request.
    /// </summary>
    public static IServiceCollection AddXrmdAuth(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);
        services.AddSingleton<SigningKeyStore>();
        services.AddSingleton<LocalSigningKeys>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration, LocalSigningKeys>((o, cfg, keys) => Configure(o, cfg.GetSection("Auth"), keys))
            .ValidateOnStart(); // builds the options at startup, so Configure's checks throw there
        return services;
    }

    /// <summary>The issuer of the tokens of the local identity provider (plan 2.8, L2): the audience's <c>/adfs</c> path.</summary>
    public static string LocalIssuer(string audience) => audience.TrimEnd('/') + "/adfs";

    /// <summary>The audience of Local mode (L12): <c>Auth:Audience</c> with exactly one trailing slash; it must be an https URL, the host refuses to start otherwise.</summary>
    public static string LocalAudience(IConfiguration auth)
    {
        var audience = auth["Audience"];
        if (!Uri.TryCreate(audience, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Auth:Audience must be an https URL when Auth:Mode is Local.");
        return audience!.TrimEnd('/') + "/";
    }

    /// <summary>
    /// The authorize endpoint the 401 challenge names (L13), or null when the mode names none (Development, not configured).
    /// </summary>
    public static string? AuthorizeUri(IConfiguration auth) => auth["Mode"] switch
    {
        "Local" when !string.IsNullOrWhiteSpace(auth["Audience"]) => auth["Audience"]!.TrimEnd('/') + "/adfs/oauth2/authorize",
        "Entra" when !string.IsNullOrEmpty(auth["TenantId"]) => $"https://login.microsoftonline.com/{auth["TenantId"]}/oauth2/authorize",
        _ => null,
    };

    private static readonly string[] ChallengePrefixes = ["/api/data", "/XRMServices/2011/Organization.svc", "/admin"];

    internal static void Configure(JwtBearerOptions o, IConfiguration auth, LocalSigningKeys? localKeys = null)
    {
        o.MapInboundClaims = false;
        o.IncludeErrorDetails = false; // 401s must not reveal configuration
        o.RequireHttpsMetadata = true;

        var mode = auth["Mode"];
        var audience = auth["Audience"];
        var tp = o.TokenValidationParameters;
        tp.ValidateIssuer = true;
        tp.ValidateAudience = true;
        tp.ValidateLifetime = true;
        tp.ValidateIssuerSigningKey = true;
        tp.RequireSignedTokens = true;
        tp.ClockSkew = TimeSpan.FromMinutes(1);
        tp.ValidAudiences = string.IsNullOrWhiteSpace(audience)
            ? ["<unconfigured>"]
            : [audience.TrimEnd('/'), audience.TrimEnd('/') + "/"];

        switch (mode)
        {
            case null or "":
                // Not configured: reject every token.
                tp.ValidIssuer = "<unconfigured>";
                tp.IssuerSigningKey = new SymmetricSecurityKey(Guid.NewGuid().ToByteArray());
                break;
            case "Development":
                var key = auth["DevSigningKey"];
                if (string.IsNullOrEmpty(key) || key.Length < 32)
                    throw new InvalidOperationException("Auth:DevSigningKey must be at least 32 characters.");
                tp.ValidIssuer = DevIssuer;
                tp.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
                break;
            case "Entra":
                var tenant = auth["TenantId"];
                if (string.IsNullOrEmpty(tenant))
                    throw new InvalidOperationException("Auth:TenantId is required when Auth:Mode is Entra.");
                o.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
                tp.ValidIssuer = o.Authority;
                break;
            case "Local":
                tp.ValidIssuer = LocalIssuer(LocalAudience(auth));
                // Synchronous by contract: the key ring is loaded at startup (UseXrmdAsync), the resolver only reads it.
                tp.IssuerSigningKeyResolver = (_, _, _, _) => localKeys?.Keys ?? [];
                break;
            default:
                throw new InvalidOperationException($"Unknown Auth:Mode '{mode}'.");
        }

        // The web session cookie (plan 3.5 UI-1c, ruling C1) stands in for a missing bearer header on the paths it is accepted on;
        // its token is validated exactly as a bearer token. Composed with whatever handler is already set.
        o.Events ??= new JwtBearerEvents();
        var received = o.Events.OnMessageReceived;
        o.Events.OnMessageReceived = async context =>
        {
            await received(context);
            await SessionCookie.ReadToken(context);
        };

        // Dataverse-shaped challenge (L13): clients (ServiceClient) discover the authority from this header. Other paths keep
        // JwtBearer's plain `Bearer`; the 401 body stays empty.
        if (AuthorizeUri(auth) is { } authorize)
        {
            var header = $"Bearer authorization_uri={authorize}"
                         + (string.IsNullOrWhiteSpace(audience) ? "" : $", resource_id={audience.TrimEnd('/')}/");
            o.Events ??= new JwtBearerEvents();
            o.Events.OnChallenge = context =>
            {
                var path = context.Request.Path;
                if (!ChallengePrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))) return Task.CompletedTask;
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = header;
                return Task.CompletedTask;
            };
        }
    }
}
