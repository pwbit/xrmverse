using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Xrmd.Host.Auth;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Xrmd.Host.Idp;

/// <summary>
/// The passthrough part of <c>/adfs/oauth2/authorize</c>. OpenIddict has validated the client, the redirect URI, PKCE and the
/// scopes (L9) before this runs; an invalid client or redirect URI never reaches it and gets a 400 page, never a redirect.
/// Redirect URIs match exactly, except that a native client's loopback URI (<c>xrmd-cli</c>: <c>http://localhost</c>) accepts
/// any port (RFC 8252 §7.3; OpenIddict's own rule): scheme, host, path and query still match exactly. This signs the browser's
/// IdP session user in, or sends the browser to the login page first.
/// </summary>
internal static class AuthorizeEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, CredentialService credentials, IConfiguration configuration,
        CancellationToken ct)
    {
        var request = context.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");
        var audience = JwtSetup.LocalAudience(configuration.GetSection("Auth"));

        var session = await IdpSession.CurrentUserAsync(context, credentials, ct);
        if (session is null)
        {
            if (request.HasPromptValue(PromptValues.None))
                return TokenEndpoint.Refuse(Errors.LoginRequired, "The user is not signed in.");

            // Back here after the login page, with the same parameters (a POSTed request comes back as a GET).
            var parameters = context.Request.HasFormContentType
                ? (await context.Request.ReadFormAsync(ct)).AsEnumerable()
                : context.Request.Query.AsEnumerable();
            var returnUrl = context.Request.Path + QueryString.Create(parameters.SelectMany(p => p.Value.Select(v =>
                new KeyValuePair<string, string?>(p.Key, v))));
            return Results.Challenge(new AuthenticationProperties { RedirectUri = returnUrl }, [IdpSession.Scheme]);
        }

        if (!IdpScopes.TryResolve(request.GetScopes(), audience, out var granted))
            return TokenEndpoint.Refuse(Errors.InvalidScope, "The requested scope is not supported by this environment.");

        return Results.SignIn(IdpPrincipal.ForUser(session, granted, audience),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}

/// <summary>
/// The browser session of the local identity provider (L11): cookie <c>xrmd.idp</c> (HttpOnly, Secure, SameSite=Lax, path
/// <c>/adfs</c>, 8 hours), holding the <c>systemuserid</c> and the credential's stamp. The user is read again on every use, and a
/// session from before a password or TOTP change is over.
/// </summary>
internal static class IdpSession
{
    public const string Scheme = "xrmd.idp";

    public static ClaimsPrincipal Principal(ActiveUser user) => new(new ClaimsIdentity(
        [new Claim(Claims.Subject, user.SystemUserId.ToString()), new Claim(IdpPrincipal.StampClaim, user.Stamp)],
        Scheme, Claims.Subject, Claims.Role));

    /// <summary>The signed-in user when still enabled with a credential; otherwise null, and a stale session is ended.</summary>
    public static async Task<ActiveUser?> CurrentUserAsync(HttpContext context,
        CredentialService credentials, CancellationToken ct)
    {
        var result = await context.AuthenticateAsync(Scheme);
        if (!result.Succeeded || !Guid.TryParse(result.Principal.FindFirstValue(Claims.Subject), out var systemUserId)) return null;
        var stamp = result.Principal.FindFirstValue(IdpPrincipal.StampClaim);
        if (await credentials.FindActiveUserAsync(systemUserId, stamp, ct) is { } active) return active;
        await context.SignOutAsync(Scheme);
        return null;
    }
}
