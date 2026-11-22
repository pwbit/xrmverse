using Microsoft.Identity.Client;
using Xrmd.Cli.Config;

namespace Xrmd.Cli.Auth;

/// <summary>
/// Modes <c>device-code</c> and <c>browser</c> (MSAL public client) and <c>client-secret</c> (MSAL confidential
/// client, secret from <c>XRMD_CLIENT_SECRET</c>). Client id from the profile, scope <c>{url}/.default</c>.
/// User tokens are cached in clear text in <c>{ConfigDir}/msal-cache.bin</c> (0600 on Unix, no keychain).
/// Client-secret tokens stay in memory: the secret is at hand on every run, and an application token cache
/// serialized into the same file would overwrite the user tokens.
/// </summary>
public sealed class MsalTokenProvider(Profile profile, CliEnvironment env) : ITokenProvider
{
    public const string ClientSecretEnv = "XRMD_CLIENT_SECRET";
    public const string CacheFileName = "msal-cache.bin";
    /// <summary>Redirect URI of the browser flow; register it on the Entra app as a mobile/desktop platform URI.</summary>
    public const string RedirectUri = "http://localhost";

    internal string[] Scopes { get; } = [profile.Url.TrimEnd('/') + "/.default"];

    /// <summary>Entra: the tenant's authority. Local: the server's ADFS-shaped authority <c>&lt;url&gt;adfs/</c>, which MSAL validates through WebFinger on the server itself.</summary>
    internal string Authority { get; } = profile.IsLocalIdp
        ? profile.Url.TrimEnd('/') + "/adfs/"
        : $"https://login.microsoftonline.com/{profile.TenantId ?? "organizations"}/";

    internal string CachePath { get; } = Path.Combine(env.ConfigDir, CacheFileName);

    // One MSAL app per provider (= per command): the in-memory client-secret token serves every request.
    private IPublicClientApplication? _public;
    private IConfidentialClientApplication? _confidential;

    public async Task<Profile> LoginAsync(CancellationToken cancellationToken)
    {
        if (profile.Mode == AuthModes.ClientSecret)
        {
            await GetTokenAsync(cancellationToken);
            return profile;
        }

        var app = _public ??= BuildPublicClient();
        var result = await CallMsal(() => profile.Mode == AuthModes.Browser
            ? app.AcquireTokenInteractive(Scopes).WithUseEmbeddedWebView(false).ExecuteAsync(cancellationToken)
            : app.AcquireTokenWithDeviceCode(Scopes, code =>
            {
                env.Err.WriteLine(code.Message);
                return Task.CompletedTask;
            }).ExecuteAsync(cancellationToken));
        return profile with { HomeAccountId = result.Account?.HomeAccountId?.Identifier };
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (profile.Mode == AuthModes.ClientSecret)
        {
            var confidential = _confidential ??= BuildConfidentialClient(RequireSecret());
            var appToken = await CallMsal(() => confidential.AcquireTokenForClient(Scopes).ExecuteAsync(cancellationToken));
            return appToken.AccessToken;
        }

        var app = _public ??= BuildPublicClient();
        var account = await FindAccountAsync(app)
            ?? throw new CliException(ExitCodes.Auth, "Sign-in required: run `xrmd auth login`.");
        var result = await CallMsal(() => app.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken));
        return result.AccessToken;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (profile.Mode == AuthModes.ClientSecret) return;
        var app = _public ??= BuildPublicClient();
        if (await FindAccountAsync(app) is { } account) await app.RemoveAsync(account);
    }

    internal IPublicClientApplication BuildPublicClient()
    {
        var builder = PublicClientApplicationBuilder.Create(RequireClientId())
            .WithAuthority(Authority, validateAuthority: true)
            .WithRedirectUri(RedirectUri);
        if (HttpFactory() is { } factory) builder = builder.WithHttpClientFactory(factory);
        var app = builder.Build();
        new MsalCacheFile(CachePath).Bind(app.UserTokenCache);
        return app;
    }

    internal IConfidentialClientApplication BuildConfidentialClient(string secret)
    {
        var builder = ConfidentialClientApplicationBuilder.Create(RequireClientId())
            .WithAuthority(Authority, validateAuthority: true)
            .WithClientSecret(secret);
        if (HttpFactory() is { } factory) builder = builder.WithHttpClientFactory(factory);
        return builder.Build();
    }

    private async Task<IAccount?> FindAccountAsync(IPublicClientApplication app)
    {
        var accounts = (await app.GetAccountsAsync()).ToList();
        return profile.HomeAccountId is { } id
            ? accounts.FirstOrDefault(a => a.HomeAccountId?.Identifier == id)
            : accounts.FirstOrDefault();
    }

    /// <summary>The test handler; else, for a local IdP behind a private CA (the profile's <c>caFile</c>), a handler that trusts it for the server's host only; else MSAL's own.</summary>
    private IMsalHttpClientFactory? HttpFactory()
    {
        if (env.Handler is not null) return new HandlerHttpClientFactory(env.Handler);
        if (profile.IsLocalIdp && profile.CaFile is { } caFile && Uri.TryCreate(profile.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            return new HandlerHttpClientFactory(Http.ScopedTrust.CreateHandler(uri.IdnHost, Http.ScopedTrust.LoadRoot(caFile)));
        return null;
    }

    private string RequireClientId() => profile.ClientId
        ?? (profile.IsLocalIdp ? IdentityProviders.LocalCliClientId : null)
        ?? throw new CliException(ExitCodes.Auth, $"The profile has no client id: run `xrmd auth login --mode {profile.Mode} --client-id <id>`.");

    private string RequireSecret()
    {
        var secret = env.GetEnvironmentVariable(ClientSecretEnv);
        return string.IsNullOrEmpty(secret)
            ? throw new CliException(ExitCodes.Auth, $"The client secret is missing: set the environment variable {ClientSecretEnv}.")
            : secret;
    }

    private static async Task<AuthenticationResult> CallMsal(Func<Task<AuthenticationResult>> call)
    {
        try
        {
            return await call();
        }
        catch (MsalUiRequiredException ex)
        {
            throw new CliException(ExitCodes.Auth, $"Sign-in required: run `xrmd auth login`. ({ex.Message})", ex);
        }
        catch (MsalException ex) when (ex.InnerException is HttpRequestException or TaskCanceledException)
        {
            throw new CliException(ExitCodes.Unreachable, $"Cannot reach the identity provider: {ex.InnerException.Message}", ex);
        }
        catch (MsalException ex)
        {
            throw new CliException(ExitCodes.Auth, $"Sign-in failed: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new CliException(ExitCodes.Unreachable, $"Cannot reach the identity provider: {ex.Message}", ex);
        }
    }

    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => new(handler, disposeHandler: false);
    }
}

/// <summary>The MSAL token cache file: clear text, replaced atomically, 0600 on Unix.</summary>
public sealed class MsalCacheFile(string path)
{
    public string Path { get; } = path;

    public void Bind(ITokenCache cache)
    {
        cache.SetBeforeAccess(args =>
        {
            if (Read() is { } bytes) args.TokenCache.DeserializeMsalV3(bytes);
        });
        cache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged) Write(args.TokenCache.SerializeMsalV3());
        });
    }

    public byte[]? Read() => File.Exists(Path) ? File.ReadAllBytes(Path) : null;

    public void Write(byte[] bytes) => ConfigFiles.WriteAllBytes(Path, bytes);
}
