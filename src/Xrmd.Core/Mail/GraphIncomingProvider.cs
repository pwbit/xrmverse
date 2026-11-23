using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Identity.Client;
using MimeKit;

namespace Xrmd.Core.Mail;

/// <summary>
/// Reads a mailbox's inbox through Microsoft Graph, read-only (<c>Mail.Read</c>, covered by the <c>Mail.ReadWrite</c> the send needs),
/// with the application token of <see cref="GraphTokens"/>. Opening reads the inbox folder (the incoming test). The listing is the
/// delta query of the inbox's messages, first filtered on <c>receivedDateTime ge {since}</c>; its cursor is the page's
/// <c>@odata.nextLink</c> or <c>@odata.deltaLink</c>, accepted only when it starts with the Graph base URL (<c>Mail:GraphBaseUrl</c>), so a
/// stored cursor can never send the token to another host. A stored cursor Graph no longer accepts (410 Gone, or <c>syncStateNotFound</c>,
/// <c>syncStateInvalid</c>, <c>resyncRequired</c>) restarts the listing from <c>since</c> in the same call. A message's MIME is read from
/// <c>/messages/{id}/$value</c>. HTTP 408, 429, 5xx and network failures are transient, the rest permanent; a refused fetch (a 4xx but 401,
/// 403 and 404) and unreadable MIME are about the message, every other failure is about the mailbox.
/// </summary>
/// <remarks>
/// A cursor is tied to the Graph base URL, not to a mailbox: Graph's next and delta links may name the user by object id rather than by
/// address, so the provider cannot check that a link belongs to the mailbox it was opened for. The sync job stores one cursor per mailbox.
/// </remarks>
public sealed class GraphIncomingProvider : IIncomingMailProvider
{
    private readonly Func<HttpClient> _http;
    private readonly string _baseUrl;
    private readonly GraphTokens _tokens;

    /// <summary>Creates the provider.</summary>
    /// <param name="http">Gives the HttpClient of the Graph calls (the named client <c>xrmd-mail-graph</c> in the host); not disposed here.</param>
    /// <param name="baseUrl">The Graph base URL (<c>Mail:GraphBaseUrl</c>); null uses <see cref="GraphMailProvider.DefaultBaseUrl"/>.</param>
    /// <param name="msalFactory">The HttpClient factory of MSAL; null makes MSAL use the same client as <paramref name="http"/>.</param>
    public GraphIncomingProvider(Func<HttpClient> http, string? baseUrl = null, IMsalHttpClientFactory? msalFactory = null)
        : this(http, baseUrl, msalFactory is null ? GraphTokens.Over(http) : new GraphTokens(msalFactory))
    {
    }

    /// <summary>Creates the provider on a token source shared with the send provider (<see cref="MailProviders"/>).</summary>
    internal GraphIncomingProvider(Func<HttpClient> http, string? baseUrl, GraphTokens tokens)
    {
        _http = http;
        _baseUrl = GraphMailProvider.NormalizeBaseUrl(baseUrl);
        _tokens = tokens;
    }

    /// <inheritdoc />
    public async Task<IIncomingSession> OpenAsync(string address, MailProfile profile, MailCredentials credentials, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new MailSendException("The mailbox has no email address.", transient: false);
        }

        var session = new Session(this, address.Trim(), profile, credentials);
        await GraphTokens.Guarded(ct, async token =>
        {
            using var response = await session.GetAsync($"{session.User}/mailFolders/inbox?$select=id", null, token).ConfigureAwait(false);
            await GraphTokens.EnsureAsync(response, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return session;
    }

    private sealed class Session(GraphIncomingProvider provider, string address, MailProfile profile, MailCredentials credentials) : IIncomingSession
    {
        public string User { get; } = $"{provider._baseUrl}/users/{Uri.EscapeDataString(address)}";

        public Task<IncomingBatch> ListAsync(string? cursor, DateTime since, int max, CancellationToken ct)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
            var stored = IsOwnLink(cursor);
            var prefer = $"odata.maxpagesize={max.ToString(CultureInfo.InvariantCulture)}";
            return GraphTokens.Guarded(ct, async token =>
            {
                using var response = await GetAsync(stored ? cursor! : FirstUrl(since), prefer, token).ConfigureAwait(false);
                if (stored && await IsExpiredCursorAsync(response, token).ConfigureAwait(false))
                {
                    // Final review I1: the delta token expired or the synchronization was reset. The listing starts again from the
                    // received date; the message keys keep what was imported from being imported again.
                    using var restarted = await GetAsync(FirstUrl(since), prefer, token).ConfigureAwait(false);
                    return await PageAsync(restarted, token).ConfigureAwait(false);
                }

                return await PageAsync(response, token).ConfigureAwait(false);
            });
        }

        public Task<MimeMessage?> FetchAsync(IncomingRef item, CancellationToken ct) =>
            GraphTokens.Guarded(ct, async token =>
            {
                using var response = await GetAsync($"{User}/messages/{Uri.EscapeDataString(item.ServerId)}/$value", null, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                // Final review I2: a refusal of this message's content (400, 413, 422, ...) is about the message; 401 and 403 (the token,
                // the application's access to the mailbox) stay about the mailbox.
                await GraphTokens.EnsureAsync(response, token,
                    status => status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && (int)status is >= 400 and < 500).ConfigureAwait(false);
                await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                try
                {
                    return await MimeMessage.LoadAsync(body, token).ConfigureAwait(false);
                }
                catch (FormatException e)
                {
                    throw new MailSendException("Microsoft Graph returned a message that is not readable MIME.", false, e) { Scope = MailFailureScope.Message };
                }
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<HttpResponseMessage> GetAsync(string url, string? prefer, CancellationToken ct)
        {
            var bearer = await provider._tokens.BearerAsync(profile, credentials, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = bearer;
            if (prefer is not null)
            {
                request.Headers.TryAddWithoutValidation("Prefer", prefer);
            }

            return await provider._http().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Whether Graph refused a stored cursor because it can no longer be resumed: <c>410 Gone</c> (synchronization reset), or a 4xx whose
        /// error code is <c>syncStateNotFound</c> (the delta token expired from Outlook's token cache), <c>syncStateInvalid</c> or
        /// <c>resyncRequired</c> (Microsoft Graph, "Use delta query to track changes", Limitations).
        /// </summary>
        private static async Task<bool> IsExpiredCursorAsync(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.StatusCode == HttpStatusCode.Gone)
            {
                return true;
            }

            if ((int)response.StatusCode is < 400 or >= 500)
            {
                return false;
            }

            return await GraphTokens.ErrorCodeAsync(response, ct).ConfigureAwait(false) is { } code
                && ExpiredCursorCodes.Contains(code, StringComparer.OrdinalIgnoreCase);
        }

        private static readonly string[] ExpiredCursorCodes = ["syncStateNotFound", "syncStateInvalid", "resyncRequired"];

        private static async Task<IncomingBatch> PageAsync(HttpResponseMessage response, CancellationToken ct)
        {
            await GraphTokens.EnsureAsync(response, ct).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            return Page(doc.RootElement);
        }

        /// <summary>A stored cursor is used only when it is an absolute link under the Graph base URL; anything else restarts the listing.</summary>
        private bool IsOwnLink(string? cursor) =>
            cursor is not null
            && Uri.TryCreate(cursor, UriKind.Absolute, out _)
            && cursor.StartsWith(provider._baseUrl + "/", StringComparison.OrdinalIgnoreCase);

        private string FirstUrl(DateTime since)
        {
            var utc = since.Kind == DateTimeKind.Local ? since.ToUniversalTime() : DateTime.SpecifyKind(since, DateTimeKind.Utc);
            var filter = Uri.EscapeDataString("receivedDateTime ge " + utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            return $"{User}/mailFolders/inbox/messages/delta?$select=internetMessageId,conversationIndex,receivedDateTime&$filter={filter}";
        }

        /// <summary>
        /// The messages of one page; entries marked <c>@removed</c> are skipped. Only the last kept item carries the page's link as its cursor:
        /// a delta page can only be resumed as a whole.
        /// </summary>
        private static IncomingBatch Page(JsonElement root)
        {
            var next = Link(root, "@odata.nextLink");
            var end = next ?? Link(root, "@odata.deltaLink");
            var items = new List<IncomingRef>();
            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in value.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || entry.TryGetProperty("@removed", out _) || Text(entry, "id") is not { Length: > 0 } id)
                    {
                        continue;
                    }

                    var received = entry.TryGetProperty("receivedDateTime", out var r) && r.ValueKind == JsonValueKind.String
                        && r.TryGetDateTimeOffset(out var at) ? at.UtcDateTime : DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
                    items.Add(new IncomingRef(id, Text(entry, "internetMessageId"), Text(entry, "conversationIndex"), received, null));
                }
            }

            if (items.Count > 0)
            {
                items[^1] = items[^1] with { CursorAfter = end };
            }

            return new IncomingBatch(items, end, next is not null);
        }

        private static string? Link(JsonElement root, string name) => Text(root, name) is { Length: > 0 } link ? link : null;

        private static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }
}
