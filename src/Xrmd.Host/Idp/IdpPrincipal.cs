using System.Security.Claims;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Xrmd.Host.Idp;

/// <summary>The principal the local identity provider signs in for a <c>systemuser</c>.</summary>
public static class IdpPrincipal
{
    /// <summary>The credential's stamp (<see cref="ActiveUser.Stamp"/>) in IdP sessions, codes and refresh tokens.</summary>
    public const string StampClaim = "xrmd_stamp";

    /// <summary>The principal of an interactive grant for <paramref name="user"/>, with its credential stamp.</summary>
    public static ClaimsPrincipal ForUser(ActiveUser user, IEnumerable<string> scopes, string audience) =>
        ForUser(user.SystemUserId, user.ObjectId, user.Name, scopes, audience, user.Stamp);

    /// <summary>
    /// <c>sub</c> = <paramref name="systemUserId"/>, <c>oid</c> = <paramref name="objectId"/> (the <c>azureactivedirectoryobjectid</c>
    /// the host maps callers by, L5) and <c>name</c>, all in the access and identity tokens; the granted scopes; the audience as the
    /// only resource, so the access token's <c>aud</c> is always <c>Auth:Audience</c> (L9).
    /// </summary>
    public static ClaimsPrincipal ForUser(Guid systemUserId, Guid objectId, string name, IEnumerable<string> scopes, string audience,
        string? stamp = null)
    {
        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, systemUserId.ToString());
        identity.SetClaim("oid", objectId.ToString());
        identity.SetClaim(Claims.Name, name);
        if (stamp is not null) identity.SetClaim(StampClaim, stamp); // no destination: kept in codes and refresh tokens only
        identity.SetScopes(scopes);
        identity.SetResources(audience);
        identity.SetDestinations(claim => claim.Type is Claims.Subject or "oid" or Claims.Name
            ? [Destinations.AccessToken, Destinations.IdentityToken]
            : []);
        return new ClaimsPrincipal(identity);
    }
}
