using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using Xrmd.Host.Auth;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Xrmd.Host.Idp;

/// <summary>
/// The local identity provider of <c>Auth:Mode=Local</c> (plan 2.8): an OpenIddict server on the environment host with the
/// ADFS-compatible layout of L1, so MSAL treats <c>&lt;environment&gt;/adfs/</c> as an ADFS authority and never calls Microsoft.
/// </summary>
public static class IdpSetup
{
    private const string Adfs = "adfs/";

    /// <param name="services">The host's services.</param>
    /// <param name="useDatabase">
    /// Configures <see cref="AuthDbContext"/> for the organization's engine on <c>ConnectionStrings:Org</c> (the host picks SQL
    /// Server's or PostgreSQL's provider, plan 2.7b).
    /// </param>
    public static IServiceCollection AddXrmdIdp(this IServiceCollection services, Action<DbContextOptionsBuilder, string> useDatabase)
    {
        ArgumentNullException.ThrowIfNull(useDatabase);
        services.AddDbContext<AuthDbContext>((sp, o) =>
            useDatabase(o, sp.GetRequiredService<IConfiguration>().GetConnectionString("Org")
                           ?? throw new InvalidOperationException("ConnectionStrings:Org is required when Auth:Mode is Local.")));
        services.AddScoped<CredentialService>();
        services.TryAddSingleton<IPasswordHasher<LocalCredential>, PasswordHasher<LocalCredential>>();

        // The browser session of the IdP pages (L11). Not the default scheme: the API keeps JwtBearer.
        services.AddAuthentication().AddCookie(IdpSession.Scheme, o =>
        {
            o.Cookie.Name = IdpSession.Scheme;
            o.Cookie.Path = "/adfs";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = false;
            o.LoginPath = LoginPages.LoginPath;
            o.ReturnUrlParameter = "returnUrl";
        });
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "xrmd.idp.af";
            o.Cookie.Path = "/adfs";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.SuppressXFrameOptionsHeader = true; // DENY is set for every /adfs response (UseXrmdIdpHeaders)
        });

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AuthDbContext>())
            .AddServer(o =>
            {
                // Flows and lifetimes (L8); no implicit or password grant.
                o.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange()
                    .AllowDeviceAuthorizationFlow()
                    .AllowClientCredentialsFlow()
                    .AllowRefreshTokenFlow();
                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(60))
                    .SetRefreshTokenLifetime(TimeSpan.FromDays(14))
                    .SetDeviceCodeLifetime(TimeSpan.FromMinutes(15));

                // JwtBearer and ServiceClient need a readable JWT; codes and refresh tokens stay encrypted (L4).
                o.DisableAccessTokenEncryption();

                // The scopes are the rule of L9 (IdpScopes), not registered scope entities nor per-client scope permissions.
                o.DisableScopeValidation().IgnoreScopePermissions();
                o.AddEventHandler<ValidateAuthorizationRequestContext>(b => b.UseSingletonHandler<ScopeRule>());
                o.AddEventHandler<ValidateDeviceAuthorizationRequestContext>(b => b.UseSingletonHandler<ScopeRule>());
                o.AddEventHandler<ValidateTokenRequestContext>(b => b.UseSingletonHandler<ScopeRule>());
                o.AddEventHandler<ExtractTokenRequestContext>(b => b.UseSingletonHandler<MsalTokenRequest>()
                    .SetOrder(OpenIddictServerAspNetCoreHandlers.ExtractPostRequest<ExtractTokenRequestContext>.Descriptor.Order + 1_000));

                o.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableEndUserVerificationEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();
            });

        // Issuer and endpoints follow Auth:Audience; the certificates come from the org's key ring, loaded at startup by
        // UseXrmdAsync (LocalSigningKeys) before the first request builds these options. A Configure, not a PostConfigure: it
        // runs before OpenIddict's own checks of the options.
        services.AddOptions<OpenIddictServerOptions>().Configure<IConfiguration, LocalSigningKeys>((o, configuration, keys) =>
        {
            var configured = JwtSetup.LocalAudience(configuration.GetSection("Auth"));
            var audience = new Uri(configured.TrimEnd('/') + "/"); // the base the paths below are relative to
            o.Issuer = new Uri(JwtSetup.LocalIssuer(configured));
            Set(o.ConfigurationEndpointUris, audience, Adfs + ".well-known/openid-configuration");
            Set(o.AuthorizationEndpointUris, audience, Adfs + "oauth2/authorize");
            Set(o.TokenEndpointUris, audience, Adfs + "oauth2/token");
            Set(o.DeviceAuthorizationEndpointUris, audience, Adfs + "oauth2/devicecode");
            Set(o.EndUserVerificationEndpointUris, audience, Adfs + "oauth2/deviceauth");
            Set(o.EndSessionEndpointUris, audience, Adfs + "oauth2/logout");
            Set(o.JsonWebKeySetEndpointUris, audience, Adfs + "discovery/keys");
            o.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain); // S256 only

            var (signing, encryption) = keys.Pair
                ?? throw new InvalidOperationException("The local identity provider's key ring is not loaded (UseXrmdAsync loads it at startup).");
            o.SigningCredentials.Add(new SigningCredentials(new X509SecurityKey(signing), SecurityAlgorithms.RsaSha256));
            o.EncryptionCredentials.Add(new EncryptingCredentials(new X509SecurityKey(encryption),
                SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        });
        return services;
    }

    private static void Set(List<Uri> uris, Uri audience, string path)
    {
        uris.Clear();
        uris.Add(new Uri(audience, path));
    }

    /// <summary>Startup work of Local mode, after the database is migrated: the key ring, then the built-in clients, then the initial administrator's password.</summary>
    public static async Task InitializeIdpAsync(this WebApplication app, CancellationToken ct)
    {
        var audience = JwtSetup.LocalAudience(app.Configuration.GetSection("Auth"));
        await app.Services.GetRequiredService<LocalSigningKeys>().LoadAsync(app.Services.GetRequiredService<SigningKeyStore>(), ct);
        await IdpClientSeeder.SeedAsync(app.Services, audience, ct);
        await IdpBootstrap.ApplyInitialAdminPasswordAsync(app.Services, app.Configuration, app.Logger, ct);
    }

    /// <summary>
    /// The headers of L11 on every response under <c>/adfs</c>, OpenIddict's own (errors, discovery) included: registered before
    /// authentication, where OpenIddict answers.
    /// </summary>
    public static WebApplication UseXrmdIdpHeaders(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/adfs"))
                context.Response.OnStarting(() =>
                {
                    var headers = context.Response.Headers;
                    headers.CacheControl = "no-store";
                    headers.Pragma = "no-cache";
                    headers.XFrameOptions = "DENY";
                    headers.ContentSecurityPolicy = "default-src 'self'";
                    headers.XContentTypeOptions = "nosniff";
                    headers["Referrer-Policy"] = "no-referrer";
                    return Task.CompletedTask;
                });
            return next();
        });
        return app;
    }

    /// <summary>
    /// The endpoints OpenIddict passes through to xrmd, the IdP pages and WebFinger. Anonymous: the client authenticates itself,
    /// the pages use the IdP session.
    /// </summary>
    public static WebApplication MapXrmdIdp(this WebApplication app)
    {
        app.MapMethods("/adfs/oauth2/authorize", [HttpMethods.Get, HttpMethods.Post], AuthorizeEndpoint.HandleAsync).AllowAnonymous();
        app.MapPost("/adfs/oauth2/token", TokenEndpoint.HandleAsync).AllowAnonymous();
        LoginPages.Map(app);
        app.MapGet("/.well-known/webfinger", ([FromQuery] string? resource, IConfiguration configuration) =>
            WebFingerEndpoint.Handle(resource, configuration)).AllowAnonymous();
        return app;
    }

    /// <summary>
    /// Token requests as MSAL sends them to an ADFS authority, read as OpenIddict expects them:
    /// <list type="bullet">
    /// <item>the device code grant is <c>grant_type=device_code</c>, the ADFS name, not the RFC 8628 URN;</item>
    /// <item>code and device code redemptions carry <c>scope</c>, which OpenIddict refuses there. It is taken off the request and
    /// kept in <c>HttpContext.Items</c> (<see cref="RequestedScopeProperty"/>); the token endpoint grants it only within the
    /// original grant.</item>
    /// </list>
    /// </summary>
    private sealed class MsalTokenRequest : IOpenIddictServerHandler<ExtractTokenRequestContext>
    {
        public ValueTask HandleAsync(ExtractTokenRequestContext context)
        {
            if (context.Request is not { } request) return default;
            if (request.GrantType == "device_code") request.GrantType = GrantTypes.DeviceCode;
            if ((request.IsAuthorizationCodeGrantType() || request.IsDeviceCodeGrantType()) && request.Scope is { Length: > 0 } scope)
            {
                var http = context.Transaction.GetHttpRequest()?.HttpContext
                           ?? throw new InvalidOperationException("The token request has no HTTP context.");
                http.Items[RequestedScopeProperty] = scope;
                request.Scope = null;
            }
            return default;
        }
    }

    /// <summary>The <c>HttpContext.Items</c> key of the <c>scope</c> of a code or device code redemption (<see cref="MsalTokenRequest"/>).</summary>
    internal static readonly object RequestedScopeProperty = new();

    /// <summary>Refuses with <c>invalid_scope</c> a request with a scope that L9 does not accept.</summary>
    private sealed class ScopeRule(IConfiguration configuration) :
        IOpenIddictServerHandler<ValidateAuthorizationRequestContext>,
        IOpenIddictServerHandler<ValidateDeviceAuthorizationRequestContext>,
        IOpenIddictServerHandler<ValidateTokenRequestContext>
    {
        public ValueTask HandleAsync(ValidateAuthorizationRequestContext context) => Check(context, context.Request.GetScopes());
        public ValueTask HandleAsync(ValidateDeviceAuthorizationRequestContext context) => Check(context, context.Request.GetScopes());
        public ValueTask HandleAsync(ValidateTokenRequestContext context) => Check(context, context.Request.GetScopes());

        private ValueTask Check(BaseValidatingContext context, IEnumerable<string> scopes)
        {
            if (!IdpScopes.TryResolve(scopes, JwtSetup.LocalAudience(configuration.GetSection("Auth")), out _))
                context.Reject(Errors.InvalidScope, "The requested scope is not supported by this environment.");
            return default;
        }
    }
}
