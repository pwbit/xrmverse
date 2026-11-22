using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Host.Auth;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Xrmd.Host.Idp;

/// <summary>
/// The passthrough part of <c>/adfs/oauth2/token</c>: OpenIddict has already authenticated the client and validated the request
/// (scopes included, see <see cref="IdpScopes"/>); this decides which user the token is for. Client credentials: the client's
/// application user. Authorization code, device code and refresh token: the user the grant was issued to, read again.
/// </summary>
internal static class TokenEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, IOpenIddictApplicationManager applications,
        IDbConnectionFactory database, CredentialService credentials, IConfiguration configuration, CancellationToken ct)
    {
        var request = context.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");
        var audience = JwtSetup.LocalAudience(configuration.GetSection("Auth"));

        if (request.IsClientCredentialsGrantType())
        {
            // The client is authenticated: it exists and its secret matched.
            var client = await applications.FindByClientIdAsync(request.ClientId!, ct)
                         ?? throw new InvalidOperationException("The authenticated client no longer exists.");
            var properties = await applications.GetPropertiesAsync(client, ct);
            if (BoundUser(properties) is not { } systemUserId)
                return Refuse(Errors.UnauthorizedClient, "The client is not bound to an application user.");

            var user = await FindUserAsync(database, systemUserId, ct);
            if (user is not { Disabled: false, ObjectId: { } objectId })
                return Refuse(Errors.InvalidGrant, "The application user of the client is disabled or does not exist.");

            if (!IdpScopes.TryResolve(request.GetScopes(), audience, out var granted))
                return Refuse(Errors.InvalidScope, "The requested scope is not supported by this environment.");
            return Results.SignIn(IdpPrincipal.ForUser(systemUserId, objectId, user.Name, granted, audience),
                authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsDeviceCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            // OpenIddict has validated the code or refresh token (signature, expiry, revocation, client, PKCE) and restored the
            // principal it was issued for. The user is read again: a disabled user or a removed credential gets no new token.
            var stored = (await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
            if (!Guid.TryParse(stored?.GetClaim(Claims.Subject), out var systemUserId))
                return Refuse(Errors.InvalidGrant, "The grant is no longer valid.");
            if (await credentials.FindActiveUserAsync(systemUserId, stored!.GetClaim(IdpPrincipal.StampClaim), ct) is not { } active)
                return Refuse(Errors.InvalidGrant, "The user is disabled, can no longer sign in, or changed the credential.");

            // Never more than the original grant. OpenIddict refuses a wider refresh first (invalid_grant); the scope of a code
            // redemption is checked only here (see IdpSetup.MsalTokenRequest).
            var original = stored.GetScopes();
            var requested = context.Items.TryGetValue(IdpSetup.RequestedScopeProperty, out var moved)
                ? new OpenIddictRequest { Scope = (string?)moved }.GetScopes()
                : request.GetScopes();
            if (!requested.All(s => original.Contains(s, StringComparer.Ordinal)))
                return Refuse(Errors.InvalidGrant, "The requested scope exceeds the scope originally granted.");
            if (!IdpScopes.TryResolve(requested.IsEmpty ? original : requested, audience, out var granted))
                return Refuse(Errors.InvalidScope, "The requested scope is not supported by this environment.");

            var principal = IdpPrincipal.ForUser(active, granted, audience);
            if (stored.GetAuthorizationId() is { } authorizationId) principal.SetAuthorizationId(authorizationId);
            return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Refuse(Errors.UnsupportedGrantType, "The grant type is not supported.");
    }

    private static Guid? BoundUser(IReadOnlyDictionary<string, JsonElement> properties) =>
        properties.TryGetValue(IdpClients.SystemUserIdProperty, out var bound) && bound.ValueKind == JsonValueKind.String
        && Guid.TryParse(bound.GetString(), out var id) ? id : null;

    /// <summary>An OAuth error response (OpenIddict redirects it to the client at the authorize endpoint, JSON at the token endpoint).</summary>
    internal static IResult Refuse(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    private sealed record User(string Name, bool Disabled, Guid? ObjectId);

    private static async Task<User?> FindUserAsync(IDbConnectionFactory database, Guid systemUserId, CancellationToken ct)
    {
        var d = database.Dialect;
        await using var conn = await database.OpenAsync(ct);
        await using var cmd = conn.Command($"""
            SELECT fullname, CAST({d.IsNull("isdisabled", d.BooleanLiteral(false))} AS {d.ColumnType(SqlColumnKind.Boolean)}), azureactivedirectoryobjectid
            FROM data.systemuser WHERE systemuserid = @id
            """);
        cmd.AddParameter("@id", DbType.Guid, systemUserId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new User(rd.IsDBNull(0) ? "" : rd.GetString(0), rd.GetBoolean(1), rd.IsDBNull(2) ? null : rd.GetGuid(2));
    }
}
