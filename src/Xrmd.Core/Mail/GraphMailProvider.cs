using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using MimeKit;

namespace Xrmd.Core.Mail;

/// <summary>
/// Sends through Microsoft Graph with an application token (client credentials, scope <c>https://graph.microsoft.com/.default</c>).
/// The MIME message we built is posted as a draft (<c>POST /users/{address}/messages</c>, <c>text/plain</c> base64), so its
/// Message-ID is kept, then sent (<c>POST /users/{address}/messages/{id}/send</c>). HTTP 408, 429 and 5xx and network failures are
/// transient; other failures (401, 403, 400, 404) are permanent. TLS is the platform's; there is no insecure switch.
/// </summary>
public sealed class GraphMailProvider : IMailProvider
{
    /// <summary>The default value of the <c>Mail:GraphBaseUrl</c> setting.</summary>
    public const string DefaultBaseUrl = "https://graph.microsoft.com/v1.0";

    private const string Scope = "https://graph.microsoft.com/.default";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<HttpClient> _http;
    private readonly string _baseUrl;
    private readonly IMsalHttpClientFactory _msalFactory;
    private readonly ConcurrentDictionary<string, IConfidentialClientApplication> _apps = new();

    /// <summary>Creates the provider.</summary>
    /// <param name="http">Gives the HttpClient of the Graph calls (the named client <c>xrmd-mail-graph</c> in the host); not disposed here.</param>
    /// <param name="baseUrl">The Graph base URL (<c>Mail:GraphBaseUrl</c>); null uses <see cref="DefaultBaseUrl"/>.</param>
    /// <param name="msalFactory">The HttpClient factory of MSAL; null makes MSAL use the same client as <paramref name="http"/>.</param>
    public GraphMailProvider(Func<HttpClient> http, string? baseUrl = null, IMsalHttpClientFactory? msalFactory = null)
    {
        _http = http;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
        _msalFactory = msalFactory ?? new ClientFactory(http);
    }

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

    /// <inheritdoc />
    public async Task SendAsync(MimeMessage message, MailProfile profile, MailCredentials credentials, CancellationToken ct)
    {
        var address = message.From.Mailboxes.FirstOrDefault()?.Address ?? throw new MailSendException("The message has no sender.", false) { Scope = MailFailureScope.Message };
        await Guarded(ct, async token =>
        {
            var bearer = await TokenAsync(profile, credentials, token).ConfigureAwait(false);
            byte[] mime;
            using (var ms = new MemoryStream())
            {
                message.WriteTo(ms, token);
                mime = ms.ToArray();
            }

            var user = $"{_baseUrl}/users/{Uri.EscapeDataString(address)}/messages";
            using var create = new HttpRequestMessage(HttpMethod.Post, user)
            {
                Content = new StringContent(Convert.ToBase64String(mime), Encoding.ASCII, "text/plain"),
            };
            create.Headers.Authorization = bearer;
            string id;
            using (var response = await _http().SendAsync(create, token).ConfigureAwait(false))
            {
                await EnsureAsync(response, token).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                id = doc.RootElement.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
                if (id.Length == 0)
                {
                    throw new MailSendException("Microsoft Graph answered without a message id.", transient: false);
                }
            }

            try
            {
                using var send = new HttpRequestMessage(HttpMethod.Post, $"{user}/{Uri.EscapeDataString(id)}/send");
                send.Headers.Authorization = bearer;
                send.Content = new ByteArrayContent([]);
                using var sent = await _http().SendAsync(send, token).ConfigureAwait(false);
                await EnsureAsync(sent, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: do not leave the draft behind, or every retry adds another one. A failed delete is ignored.
                // (If the send outcome is ambiguous, such as a timeout after Graph accepted it, a retry can still duplicate the email.)
                await DeleteDraftAsync($"{user}/{Uri.EscapeDataString(id)}", bearer).ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TestAsync(string address, MailProfile profile, MailCredentials credentials, CancellationToken ct)
    {
        await Guarded(ct, async token =>
        {
            var bearer = await TokenAsync(profile, credentials, token).ConfigureAwait(false);
            // Reading the drafts folder needs the permission that creating a draft needs, and sends nothing.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/users/{Uri.EscapeDataString(address)}/mailFolders/drafts?$select=id");
            request.Headers.Authorization = bearer;
            using var response = await _http().SendAsync(request, token).ConfigureAwait(false);
            await EnsureAsync(response, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task DeleteDraftAsync(string url, AuthenticationHeaderValue bearer)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var delete = new HttpRequestMessage(HttpMethod.Delete, url);
            delete.Headers.Authorization = bearer;
            using var _ = await _http().SendAsync(delete, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Swallowed on purpose: the original failure is what the caller must see.
        }
    }

    /// <summary>
    /// Whether a failure of MSAL's token request may pass on a retry: a timeout, an unavailable service, HTTP 408/429/5xx, or a
    /// network error inside (a cancelled or timed-out request, an HttpRequestException, an IOException).
    /// </summary>
    public static bool IsTransientTokenFailure(MsalServiceException e) =>
        e.ErrorCode is MsalError.RequestTimeout or MsalError.ServiceNotAvailable
        || e.StatusCode is 408 or 429 or >= 500
        || e.InnerException is TaskCanceledException or OperationCanceledException or TimeoutException or HttpRequestException or IOException;

    private static async Task Guarded(CancellationToken ct, Func<CancellationToken, Task> action)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            await action(timeout.Token).ConfigureAwait(false);
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

    private async Task<AuthenticationHeaderValue> TokenAsync(MailProfile profile, MailCredentials credentials, CancellationToken ct)
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

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = (int)response.StatusCode;
        var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || code >= 500;
        var detail = string.Empty;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
            {
                detail = ": " + c.GetString();
            }
        }
        catch (JsonException)
        {
        }

        // 400 (the message refused: a recipient, its content) and 413 (too large) are about the message; 401, 403, 404 (no such
        // mailbox) and the rest about the mailbox.
        var scope = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge
            ? MailFailureScope.Message
            : MailFailureScope.Mailbox;
        throw new MailSendException($"Microsoft Graph answered {code}{detail}.", transient) { Scope = scope };
    }

    private sealed class ClientFactory(Func<HttpClient> http) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => http();
    }
}
