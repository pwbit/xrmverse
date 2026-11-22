using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Xrmd.Host.Idp;

/// <summary>The clients of the local identity provider that xrmd itself uses (plan 2.8, L10), and the client properties it reads.</summary>
public static class IdpClients
{
    /// <summary>The <c>xrmd</c> command line: public; device code and authorization code with PKCE, redirect <c>http://localhost</c>.</summary>
    public const string Cli = "xrmd-cli";

    /// <summary>The web admin: public single-page app; authorization code with PKCE, redirect the environment URL.</summary>
    public const string Web = "xrmd-web";

    /// <summary>
    /// The OpenIddict application property that binds a confidential client to its application user (<c>systemuserid</c>):
    /// the client-credentials grant issues tokens for that user.
    /// </summary>
    public const string SystemUserIdProperty = "xrmd_systemuserid";
}

/// <summary>Creates the built-in clients at startup, or brings them back to their definition (idempotent).</summary>
public static class IdpClientSeeder
{
    public static async Task SeedAsync(IServiceProvider services, string audience, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        foreach (var descriptor in new[] { Cli(), Web(audience) })
        {
            var existing = await manager.FindByClientIdAsync(descriptor.ClientId!, ct);
            if (existing is null) await manager.CreateAsync(descriptor, ct);
            else await manager.UpdateAsync(existing, descriptor, ct);
        }
    }

    private static OpenIddictApplicationDescriptor Cli() => new()
    {
        ClientId = IdpClients.Cli,
        DisplayName = "xrmd command line",
        ClientType = ClientTypes.Public,
        ApplicationType = ApplicationTypes.Native,
        RedirectUris = { new Uri("http://localhost") },
        Permissions =
        {
            Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.DeviceAuthorization,
            Permissions.Endpoints.EndSession,
            Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.DeviceCode, Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
        },
        Requirements = { Requirements.Features.ProofKeyForCodeExchange },
    };

    private static OpenIddictApplicationDescriptor Web(string audience) => new()
    {
        ClientId = IdpClients.Web,
        DisplayName = "xrmd web admin",
        ClientType = ClientTypes.Public,
        ApplicationType = ApplicationTypes.Web,
        RedirectUris = { new Uri(audience) },
        PostLogoutRedirectUris = { new Uri(audience) },
        Permissions =
        {
            Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession,
            Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
        },
        Requirements = { Requirements.Features.ProofKeyForCodeExchange },
    };
}
