using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;

namespace Xrmd.Core.Mail;

/// <summary>
/// The Microsoft Graph plumbing shared by the send and the incoming providers: the application token (client credentials, scope
/// <c>https://graph.microsoft.com/.default</c>, one MSAL application and token cache per authority, client id and secret), the mapping of
/// MSAL and network failures, and the mapping of a failed Graph answer to a <see cref="MailSendException"/>.
/// </summary>
internal sealed class GraphTokens
{
    private const string Scope = "https://graph.microsoft.com/.default";

    /// <summary>The time limit of one provider call (token and Graph requests together).</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    private readonly IMsalHttpClientFactory _msalFactory;
    private readonly ConcurrentDictionary<string, IConfidentialClientApplication> _apps = new();

    /// <summary>Creates the token source.</summary>
    /// <param name="msalFactory">The HttpClient factory of MSAL.</param>
    public GraphTokens(IMsalHttpClientFactory msalFactory) => _msalFactory = msalFactory;

    /// <summary>A token source whose MSAL calls use the client that <paramref name="http"/> gives.</summary>
    public static GraphTokens Over(Func<HttpClient> http) => new(new ClientFactory(http));

    /// <summary>The authority of a profile: Public (0) and Automatic (3) are <c>login.microsoftonline.com/{tenant}</c>.</summary>
    /// <exception cref="MailSendException">Permanent: another authority, or no tenant.</exception>
    public static Uri AuthorityFor(MailProfile profile)
    {
        if (profile.ServerAuthority is not (0 or 3))
        {
            throw new MailSendException($"Server authority {profile.ServerAuthority} is not supported for Exchange Online.", transient: false);
        }

        if (string.IsNullOrWhiteSpace(profile.TenantId))
        {
            throw new MailSendException("The email server profile has no Microsoft Entra tenant id.", transient: false);
        }

        var tenantText = Uri.EscapeDataString(profile.TenantId.Trim());
        return new Uri($"https://login.microsoftonline.com/{tenantText}");
    }

    /// <summary>
    /// Whether a failure of MSAL's token request may pass on a retry: a timeout, an unavailable service, HTTP 408/429/5xx, or a
    /// network error inside (a cancelled or timed-out request, an HttpRequestException, an IOException).
    /// </summary>
    public static bool IsTransientTokenFailure(MsalServiceException e) =>
        e.ErrorCode is MsalError.RequestTimeout or MsalError.ServiceNotAvailable
        || e.StatusCode is 408 or 429 or >= 500
        || e.InnerException is TaskCanceledException or OperationCanceledException or TimeoutException or HttpRequestException or IOException;

    /// <summary>The bearer header of an application token for <paramref name="profile"/> (MSAL caches it until it nears expiry).</summary>
    /// <exception cref="MailSendException">Permanent: no client id or secret, or an unsupported authority.</exception>
    public async Task<AuthenticationHeaderValue> BearerAsync(MailProfile profile, MailCredentials credentials, CancellationToken ct)
    {
        var authority = AuthorityFor(profile);
        if (string.IsNullOrWhiteSpace(profile.ClientId) || string.IsNullOrEmpty(credentials.ClientSecret))
        {
            throw new MailSendException("The email server profile needs an application (client) id and secret.", transient: false);
        }

        var key = authority + "|" + profile.ClientId + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.ClientSecret)));
        var app = _apps.GetOrAdd(key, _ => ConfidentialClientApplicationBuilder.Create(profile.ClientId)
            .WithClientSecret(credentials.ClientSecret)
            .WithAuthority(authority, validateAuthority: false)
            .WithHttpClientFactory(_msalFactory)
            .Build());
        var result = await app.AcquireTokenForClient([Scope]).ExecuteAsync(ct).ConfigureAwait(false);
        return new AuthenticationHeaderValue("Bearer", result.AccessToken);
    }

    /// <summary>
    /// Runs one provider call within <see cref="CallTimeout"/> and maps its failures: MSAL (transient per <see cref="IsTransientTokenFailure"/>),
    /// network and timeout (transient), unreadable JSON (permanent). A cancellation of <paramref name="ct"/> itself is passed through.
    /// </summary>
    public static async Task<T> Guarded<T>(CancellationToken ct, Func<CancellationToken, Task<T>> action)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            return await action(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (MailSendException)
        {
            throw;
        }
        catch (MsalServiceException e)
        {
            var transient = IsTransientTokenFailure(e);
            throw new MailSendException($"The Microsoft Entra token request failed ({e.ErrorCode}).", transient, e);
        }
        catch (MsalClientException e)
        {
            throw new MailSendException($"The token request failed ({e.ErrorCode}).", false, e);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
        {
            throw new MailSendException("Microsoft Graph could not be reached: " + e.Message, true, e);
        }
        catch (JsonException e)
        {
            throw new MailSendException("Microsoft Graph answered with unreadable JSON.", false, e);
        }
    }

    /// <inheritdoc cref="Guarded{T}"/>
    public static Task Guarded(CancellationToken ct, Func<CancellationToken, Task> action) =>
        Guarded<bool>(ct, async token =>
        {
            await action(token).ConfigureAwait(false);
            return true;
        });

    /// <summary>
    /// Throws for a failed answer: 408, 429 and 5xx are transient, the rest permanent. With <paramref name="messageScoped"/>, 400 (the message
    /// refused: a recipient, its content) and 413 (too large) are about the message; everything else (401, 403, 404 no such mailbox) is
    /// about the mailbox.
    /// </summary>
    public static Task EnsureAsync(HttpResponseMessage response, CancellationToken ct, bool messageScoped = false) =>
        EnsureAsync(response, ct, messageScoped
            ? status => status is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge
            : null);

    /// <summary>
    /// Throws for a failed answer: 408, 429 and 5xx are transient, the rest permanent; a permanent answer for which
    /// <paramref name="aboutTheMessage"/> is true has <see cref="MailSendException.Scope"/> Message, any other Mailbox.
    /// </summary>
    public static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct, Func<HttpStatusCode, bool>? aboutTheMessage)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = (int)response.StatusCode;
        var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || code >= 500;
        var error = await ErrorCodeAsync(response, ct).ConfigureAwait(false);
        var detail = error is null ? string.Empty : ": " + error;
        var scope = !transient && aboutTheMessage?.Invoke(response.StatusCode) == true ? MailFailureScope.Message : MailFailureScope.Mailbox;
        throw new MailSendException($"Microsoft Graph answered {code}{detail}.", transient) { Scope = scope };
    }

    /// <summary>The <c>error.code</c> of a Graph error answer, or null when the body has none.</summary>
    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
            {
                return c.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private sealed class ClientFactory(Func<HttpClient> http) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => http();
    }
}
