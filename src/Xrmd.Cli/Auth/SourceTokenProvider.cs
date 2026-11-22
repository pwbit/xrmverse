using System.Security.Cryptography.X509Certificates;
using Microsoft.Identity.Client;

namespace Xrmd.Cli.Auth;

/// <summary>
/// Signs in to the Dataverse environment a migration reads (not to xrmverse), with the customer's own app registration:
/// <c>browser</c> and <c>devicecode</c> (public client, tokens cached in <c>{ConfigDir}/msal-migrate-cache.bin</c>),
/// <c>secret</c> (<c>XRMD_MIGRATE_CLIENT_SECRET</c>) and <c>certificate</c> (a PFX file, password in
/// <c>XRMD_MIGRATE_CERTIFICATE_PASSWORD</c>, or a thumbprint in the current user's certificate store). Scope <c>{source}/.default</c>.
/// </summary>
public sealed class SourceTokenProvider
{
    public const string SecretVariable = "XRMD_MIGRATE_CLIENT_SECRET";
    public const string CertificatePasswordVariable = "XRMD_MIGRATE_CERTIFICATE_PASSWORD";
    public const string CacheFileName = "msal-migrate-cache.bin";
    public static readonly string[] Modes = ["browser", "devicecode", "secret", "certificate"];

    private readonly CliEnvironment _env;
    private readonly string _mode;
    private readonly string _clientId;
    private readonly string _authority;
    private readonly string[] _scopes;
    private readonly Uri _source;
    private readonly string? _certificatePath;
    private readonly string? _certificateThumbprint;
    private readonly string _tenant;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPublicClientApplication? _public;
    private IConfidentialClientApplication? _confidential;

    private SourceTokenProvider(CliEnvironment env, Uri source, string mode, string clientId, string tenant, string? certificatePath, string? certificateThumbprint)
    {
        _env = env;
        _source = source;
        _mode = mode;
        _clientId = clientId;
        _tenant = tenant;
        _authority = $"https://login.microsoftonline.com/{tenant}/";
        _scopes = [source.GetLeftPart(UriPartial.Authority) + "/.default"];
        _certificatePath = certificatePath;
        _certificateThumbprint = certificateThumbprint;
    }

    /// <summary>Checks the options (exit code 2 on a missing one) before any network call.</summary>
    public static SourceTokenProvider Create(CliEnvironment env, Uri source, string? mode, string? clientId, string? tenant,
        string? certificatePath, string? certificateThumbprint)
    {
        mode ??= "browser";
        if (!Modes.Contains(mode)) throw new CliException(ExitCodes.Usage, $"Unknown --auth '{mode}': use {string.Join(", ", Modes)}.");
        if (string.IsNullOrWhiteSpace(clientId))
            throw new CliException(ExitCodes.Usage, "--client-id is required: the application (client) id of your own app registration with access to the environment.");
        if (string.IsNullOrWhiteSpace(tenant)) throw new CliException(ExitCodes.Usage, "--tenant is required: your Microsoft Entra tenant id or domain.");
        if (mode == "secret" && string.IsNullOrEmpty(env.GetEnvironmentVariable(SecretVariable)))
            throw new CliException(ExitCodes.Usage, $"--auth secret reads the client secret from {SecretVariable}, which is not set.");
        if (mode == "certificate" && (certificatePath is null) == (certificateThumbprint is null))
            throw new CliException(ExitCodes.Usage, "--auth certificate needs either --certificate-path or --certificate-thumbprint.");
        return new SourceTokenProvider(env, source, mode, clientId, tenant, certificatePath, certificateThumbprint);
    }

    /// <summary>
    /// A token for the source. Calls are serialized: the export asks from several tables at once, and one MSAL app, one
    /// cache file and at most one interactive sign-in must serve them all (MSAL answers from its cache after the first).
    /// </summary>
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_env.SourceToken is { } fixedToken) return await fixedToken(_source, ct);
        await _gate.WaitAsync(ct);
        try
        {
            return await AcquireAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> AcquireAsync(CancellationToken ct)
    {
        if (_mode is "secret" or "certificate")
        {
            var app = _confidential ??= BuildConfidential();
            return (await CallMsal(() => app.AcquireTokenForClient(_scopes).ExecuteAsync(ct))).AccessToken;
        }

        var client = _public ??= BuildPublic();
        if (FindAccount(await client.GetAccountsAsync()) is { } account)
        {
            try
            {
                return (await client.AcquireTokenSilent(_scopes, account).ExecuteAsync(ct)).AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Fall through to an interactive sign-in.
            }
        }
        var result = await CallMsal(() => _mode == "browser"
            ? client.AcquireTokenInteractive(_scopes).WithUseEmbeddedWebView(false).ExecuteAsync(ct)
            : client.AcquireTokenWithDeviceCode(_scopes, code =>
            {
                _env.Err.WriteLine(code.Message);
                return Task.CompletedTask;
            }).ExecuteAsync(ct));
        return result.AccessToken;
    }

    /// <summary>A cached account of the requested tenant (by id, or by the domain of the user name): never another tenant's.</summary>
    private IAccount? FindAccount(IEnumerable<IAccount> accounts) => accounts.FirstOrDefault(a =>
        string.Equals(a.HomeAccountId?.TenantId, _tenant, StringComparison.OrdinalIgnoreCase)
        || (a.Username?.EndsWith("@" + _tenant, StringComparison.OrdinalIgnoreCase) ?? false));

    private IPublicClientApplication BuildPublic()
    {
        var builder = PublicClientApplicationBuilder.Create(_clientId).WithAuthority(_authority).WithRedirectUri(MsalTokenProvider.RedirectUri);
        if (_env.Handler is not null) builder = builder.WithHttpClientFactory(new HandlerFactory(_env.Handler));
        var app = builder.Build();
        new MsalCacheFile(Path.Combine(_env.ConfigDir, CacheFileName)).Bind(app.UserTokenCache);
        return app;
    }

    private IConfidentialClientApplication BuildConfidential()
    {
        var builder = ConfidentialClientApplicationBuilder.Create(_clientId).WithAuthority(_authority);
        builder = _mode == "secret"
            ? builder.WithClientSecret(_env.GetEnvironmentVariable(SecretVariable)!)
            : builder.WithCertificate(LoadCertificate());
        if (_env.Handler is not null) builder = builder.WithHttpClientFactory(new HandlerFactory(_env.Handler));
        return builder.Build();
    }

    private X509Certificate2 LoadCertificate()
    {
        try
        {
            if (_certificatePath is not null)
                return X509CertificateLoader.LoadPkcs12FromFile(_certificatePath, _env.GetEnvironmentVariable(CertificatePasswordVariable));
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, _certificateThumbprint!, validOnly: false);
            return found.Count > 0 ? found[0]
                : throw new CliException(ExitCodes.Usage, $"No certificate with thumbprint {_certificateThumbprint} in the current user's store.");
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            throw new CliException(ExitCodes.Usage, $"Cannot load the certificate: {e.Message}", e);
        }
    }

    private static async Task<AuthenticationResult> CallMsal(Func<Task<AuthenticationResult>> call)
    {
        try
        {
            return await call();
        }
        catch (MsalException ex) when (ex.InnerException is HttpRequestException or TaskCanceledException)
        {
            throw new CliException(ExitCodes.Unreachable, $"Cannot reach Microsoft Entra ID: {ex.InnerException.Message}", ex);
        }
        catch (MsalException ex)
        {
            throw new CliException(ExitCodes.Auth, $"Sign-in to the source environment failed: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new CliException(ExitCodes.Unreachable, $"Cannot reach Microsoft Entra ID: {ex.Message}", ex);
        }
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => new(handler, disposeHandler: false);
    }
}
