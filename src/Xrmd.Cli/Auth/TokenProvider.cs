using Xrmd.Cli.Config;

namespace Xrmd.Cli.Auth;

/// <summary>Obtains bearer tokens for one profile. Failures are <see cref="CliException"/> with exit code 3.</summary>
public interface ITokenProvider
{
    /// <summary>
    /// Signs in (interactively for device-code/browser) and returns the profile to save, which may carry
    /// identifiers learned at sign-in (e.g. <see cref="Profile.HomeAccountId"/>).
    /// </summary>
    Task<Profile> LoginAsync(CancellationToken cancellationToken);

    /// <summary>A token for the profile's URL, without user interaction.</summary>
    Task<string> GetTokenAsync(CancellationToken cancellationToken);

    /// <summary>Forgets cached credentials of the profile.</summary>
    Task LogoutAsync(CancellationToken cancellationToken);
}

public static class TokenProviders
{
    public static ITokenProvider Create(Profile profile, CliEnvironment env) => profile.Mode switch
    {
        AuthModes.Dev => new DevTokenProvider(profile, env),
        AuthModes.DeviceCode or AuthModes.Browser or AuthModes.ClientSecret => new MsalTokenProvider(profile, env),
        _ => throw new CliException(ExitCodes.Auth, $"Unknown authentication mode '{profile.Mode}': run `xrmd auth login`."),
    };
}
