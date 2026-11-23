using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using MimeKit;

namespace Xrmd.Core.Mail;

/// <summary>
/// Sends through Microsoft Graph with an application token (<see cref="GraphTokens"/>: client credentials, scope <c>https://graph.microsoft.com/.default</c>).
/// The MIME message we built is posted as a draft (<c>POST /users/{address}/messages</c>, <c>text/plain</c> base64), so its
/// Message-ID is kept, then sent (<c>POST /users/{address}/messages/{id}/send</c>). HTTP 408, 429 and 5xx and network failures are
/// transient; other failures (401, 403, 400, 404) are permanent. TLS is the platform's; there is no insecure switch.
/// </summary>
public sealed class GraphMailProvider : IMailProvider
{
    /// <summary>The default value of the <c>Mail:GraphBaseUrl</c> setting.</summary>
    public const string DefaultBaseUrl = "https://graph.microsoft.com/v1.0";

    private readonly Func<HttpClient> _http;
    private readonly string _baseUrl;
    private readonly GraphTokens _tokens;

    /// <summary>Creates the provider.</summary>
    /// <param name="http">Gives the HttpClient of the Graph calls (the named client <c>xrmd-mail-graph</c> in the host); not disposed here.</param>
    /// <param name="baseUrl">The Graph base URL (<c>Mail:GraphBaseUrl</c>); null uses <see cref="DefaultBaseUrl"/>.</param>
    /// <param name="msalFactory">The HttpClient factory of MSAL; null makes MSAL use the same client as <paramref name="http"/>.</param>
    public GraphMailProvider(Func<HttpClient> http, string? baseUrl = null, IMsalHttpClientFactory? msalFactory = null)
        : this(http, baseUrl, msalFactory is null ? GraphTokens.Over(http) : new GraphTokens(msalFactory))
    {
    }

    /// <summary>Creates the provider on a token source shared with the incoming provider (<see cref="MailProviders"/>).</summary>
    internal GraphMailProvider(Func<HttpClient> http, string? baseUrl, GraphTokens tokens)
    {
        _http = http;
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _tokens = tokens;
    }

    /// <summary>The base URL of the Graph calls: <c>Mail:GraphBaseUrl</c> without its trailing slash, or <see cref="DefaultBaseUrl"/>.</summary>
    internal static string NormalizeBaseUrl(string? baseUrl) => (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');

    /// <summary>The authority of a profile: Public (0) and Automatic (3) are <c>login.microsoftonline.com/{tenant}</c>.</summary>
    /// <exception cref="MailSendException">Permanent: another authority, or no tenant.</exception>
    public static Uri AuthorityFor(MailProfile profile) => GraphTokens.AuthorityFor(profile);

    /// <inheritdoc />
    public async Task SendAsync(MimeMessage message, MailProfile profile, MailCredentials credentials, CancellationToken ct)
    {
        var address = message.From.Mailboxes.FirstOrDefault()?.Address ?? throw new MailSendException("The message has no sender.", false) { Scope = MailFailureScope.Message };
        await GraphTokens.Guarded(ct, async token =>
        {
            var bearer = await _tokens.BearerAsync(profile, credentials, token).ConfigureAwait(false);
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
                await GraphTokens.EnsureAsync(response, token, messageScoped: true).ConfigureAwait(false);
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
                await GraphTokens.EnsureAsync(sent, token, messageScoped: true).ConfigureAwait(false);
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
        await GraphTokens.Guarded(ct, async token =>
        {
            var bearer = await _tokens.BearerAsync(profile, credentials, token).ConfigureAwait(false);
            // Reading the drafts folder needs the permission that creating a draft needs, and sends nothing.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/users/{Uri.EscapeDataString(address)}/mailFolders/drafts?$select=id");
            request.Headers.Authorization = bearer;
            using var response = await _http().SendAsync(request, token).ConfigureAwait(false);
            await GraphTokens.EnsureAsync(response, token, messageScoped: true).ConfigureAwait(false);
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
    public static bool IsTransientTokenFailure(MsalServiceException e) => GraphTokens.IsTransientTokenFailure(e);
}
