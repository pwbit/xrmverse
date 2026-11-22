using Xrmd.Host.Auth;

namespace Xrmd.Host.Idp;

/// <summary>
/// <c>/.well-known/webfinger</c> (RFC 7033) as MSAL's ADFS authority validation reads it: for the environment URL it answers a
/// <c>trusted-realm</c> link to that URL, which tells MSAL that <c>&lt;environment&gt;/adfs/</c> is a valid authority without asking
/// Microsoft (plan 2.8, L1). Any other resource is unknown here.
/// </summary>
internal static class WebFingerEndpoint
{
    public const string TrustedRealm = "http://schemas.microsoft.com/rel/trusted-realm";

    public static IResult Handle(string? resource, IConfiguration configuration)
    {
        // MSAL asks for https://<authority host> and compares the link's href with that exact string.
        var environment = JwtSetup.LocalAudience(configuration.GetSection("Auth")).TrimEnd('/');
        if (resource is null || !string.Equals(resource.TrimEnd('/'), environment, StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();
        // Serialized with the web defaults: subject, links, rel, href.
        return Results.Json(new WebFingerResponse(resource, [new WebFingerLink(TrustedRealm, environment)]),
            contentType: "application/jrd+json");
    }

    private sealed record WebFingerResponse(string Subject, WebFingerLink[] Links);

    private sealed record WebFingerLink(string Rel, string Href);
}
