using System.Globalization;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Xrmd.Core.Mail;

/// <summary>
/// Reads a mailbox's INBOX over IMAP with MailKit, without ever changing it: the folder is opened read-only (<c>EXAMINE</c>) and messages
/// are fetched with <c>BODY.PEEK[]</c>, so no flag is set. UseSsl true is TLS on connect (port 993 by default); false REQUIRES STARTTLS
/// (port 143): a server without it is a permanent failure before any credential is sent, unless <c>Mail:AllowInsecureImap</c> is true,
/// which allows plaintext (for test servers; STARTTLS is still used when offered). Server certificates are always validated. Login is
/// Basic (IMAP LOGIN or SASL PLAIN, as MailKit picks).
/// <para>
/// The cursor is <c>imap:{uidvalidity}:{lastuid}</c>: a listing from it returns the UIDs after <c>lastuid</c> in UID order. A cursor of
/// another UIDVALIDITY (the server renumbered the folder), or none, restarts from the messages whose internal date is at or after
/// <c>since</c>. Every item is a safe resume point. An item's server id is <c>{uidvalidity}:{uid}</c>: a fetch in a session of another
/// UIDVALIDITY returns null (the message is gone) and never reads a different message under the same UID.
/// </para>
/// Authentication, TLS and configuration failures are permanent; network failures, an IMAP <c>NO</c> and an unexpected <c>BYE</c> are
/// transient. All are about the mailbox, except a fetch the server refuses (<c>NO</c> or <c>BAD</c>) or a message that is not readable
/// MIME, which are permanent and about the message.
/// </summary>
public sealed class ImapMailProvider : IIncomingMailProvider
{
    /// <summary>Connect, read and write timeout of the IMAP conversation.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string CursorPrefix = "imap:";

    private readonly bool _allowInsecure;

    /// <summary>Creates the provider.</summary>
    /// <param name="allowInsecure">The server setting <c>Mail:AllowInsecureImap</c> (default false): allows a plaintext IMAP session when the server has no STARTTLS.</param>
    public ImapMailProvider(bool allowInsecure = false) => _allowInsecure = allowInsecure;

    /// <inheritdoc />
    public async Task<IIncomingSession> OpenAsync(string address, MailProfile profile, MailCredentials credentials, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.ServerLocation))
        {
            throw new MailSendException("The email server profile has no incoming server, so its inbox cannot be read.", transient: false);
        }

        if (string.IsNullOrEmpty(credentials.UserName))
        {
            throw new MailSendException("The IMAP user name is empty.", transient: false);
        }

        var client = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            var port = profile.Port ?? (profile.UseSsl ? 993 : 143);
            var options = profile.UseSsl ? SecureSocketOptions.SslOnConnect : _allowInsecure ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.StartTls;
            await client.ConnectAsync(profile.ServerLocation.Trim(), port, options, ct).ConfigureAwait(false);
            await client.AuthenticateAsync(credentials.UserName, credentials.Password ?? string.Empty, ct).ConfigureAwait(false);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            return new Session(client);
        }
        catch (Exception e)
        {
            client.Dispose();
            if (e is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            throw Map(e);
        }
    }

    /// <summary>Maps a MailKit or network failure to a <see cref="MailSendException"/> about the mailbox; the text never holds the credentials.</summary>
    internal static MailSendException Map(Exception e) => e switch
    {
        MailSendException m => m,
        NotSupportedException => new("TLS is required but the IMAP server does not offer STARTTLS (use SSL, or set Mail:AllowInsecureImap to true to allow plaintext).", false, e),
        AuthenticationException => new("IMAP authentication failed.", false, e),
        SslHandshakeException => new("The IMAP server's TLS certificate or handshake was refused.", false, e),
        ImapCommandException { Response: ImapCommandResponse.No } c => new("The IMAP server answered NO: " + c.ResponseText, true, e),
        ImapCommandException c => new("The IMAP server refused a command: " + c.ResponseText, false, e),
        ImapProtocolException => new("The IMAP server ended the session or answered unexpectedly: " + e.Message, true, e),
        SocketException or IOException or TimeoutException or OperationCanceledException or ServiceNotConnectedException or ServiceNotAuthenticatedException
            => new("The IMAP server could not be reached: " + e.Message, true, e),
        _ => new("Reading the IMAP inbox failed: " + e.Message, false, e),
    };

    /// <summary>
    /// Final review I2: whether a failure of one message's fetch is about that message: the server refused the fetch of this UID (an IMAP
    /// <c>NO</c> or <c>BAD</c>, as Exchange and Dovecot answer for a damaged item), or what it gave is not readable MIME.
    /// </summary>
    internal static bool IsAboutTheMessage(Exception e) => e is ImapCommandException or FormatException or ParseException;

    /// <summary>A failure of one message's fetch for which <see cref="IsAboutTheMessage"/> holds: permanent, with Message scope.</summary>
    internal static MailSendException MapFetch(Exception e) => e switch
    {
        ImapCommandException c => new("The IMAP server refused to give the message: " + c.ResponseText, false, e) { Scope = MailFailureScope.Message },
        _ => new("The IMAP server gave a message that is not readable MIME: " + e.Message, false, e) { Scope = MailFailureScope.Message },
    };

    /// <summary>Parses <c>imap:{uidvalidity}:{lastuid}</c>.</summary>
    internal static bool TryParseCursor(string? cursor, out uint validity, out uint last)
    {
        validity = 0;
        last = 0;
        if (cursor is null || !cursor.StartsWith(CursorPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = cursor[CursorPrefix.Length..].Split(':');
        return parts.Length == 2
            && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out validity)
            && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out last);
    }

    /// <summary>The server id of a message: <c>{uidvalidity}:{uid}</c>.</summary>
    internal static string ServerId(uint validity, uint uid) =>
        string.Create(CultureInfo.InvariantCulture, $"{validity}:{uid}");

    /// <summary>Parses <c>{uidvalidity}:{uid}</c>; the UID must not be 0.</summary>
    internal static bool TryParseServerId(string? serverId, out uint validity, out uint uid)
    {
        validity = 0;
        uid = 0;
        var parts = serverId?.Split(':');
        return parts is { Length: 2 }
            && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out validity)
            && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uid)
            && uid != 0;
    }

    internal static string Cursor(uint validity, uint uid) =>
        string.Create(CultureInfo.InvariantCulture, $"{CursorPrefix}{validity}:{uid}");

    /// <summary>The open, read-only INBOX of one connection. Not thread-safe: one call at a time.</summary>
    private sealed class Session(ImapClient client) : IIncomingSession
    {
        private const MessageSummaryItems ListItems = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate;

        private IMailFolder Inbox => client.Inbox;

        public Task<IncomingBatch> ListAsync(string? cursor, DateTime since, int max, CancellationToken ct)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
            return Run(ct, async () =>
            {
                var validity = Inbox.UidValidity;
                return TryParseCursor(cursor, out var cursorValidity, out var last) && cursorValidity == validity
                    ? await FromUidAsync(validity, last, max, ct).ConfigureAwait(false)
                    : await FromDateAsync(validity, since, max, ct).ConfigureAwait(false);
            });
        }

        public Task<MimeMessage?> FetchAsync(IncomingRef item, CancellationToken ct)
        {
            if (!TryParseServerId(item.ServerId, out var validity, out var uid))
            {
                return Task.FromException<MimeMessage?>(new MailSendException(
                    $"'{item.ServerId}' is not an IMAP message id ({{uidvalidity}}:{{uid}}).", transient: false) { Scope = MailFailureScope.Message });
            }

            return Run<MimeMessage?>(ct, async () =>
            {
                if (validity != Inbox.UidValidity)
                {
                    // The folder was renumbered: this UID may now name another message.
                    return null;
                }

                try
                {
                    // MailKit fetches BODY.PEEK[]: the \Seen flag is left as it is.
                    return await Inbox.GetMessageAsync(new UniqueId(Inbox.UidValidity, uid), ct).ConfigureAwait(false);
                }
                catch (MessageNotFoundException)
                {
                    return null;
                }
                catch (Exception e) when (IsAboutTheMessage(e))
                {
                    throw MapFetch(e);
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (client.IsConnected)
                {
                    await client.DisconnectAsync(true, cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Nothing was changed on the server; a failing LOGOUT only drops the connection.
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <summary>The UIDs after <paramref name="last"/>, in UID order. <c>{last+1}:*</c> also returns the highest UID when it is lower: it is dropped.</summary>
        private async Task<IncomingBatch> FromUidAsync(uint validity, uint last, int max, CancellationToken ct)
        {
            if (last == uint.MaxValue)
            {
                return new IncomingBatch([], Cursor(validity, last), false);
            }

            var found = await Inbox.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId(validity, last + 1), UniqueId.MaxValue)), ct)
                .ConfigureAwait(false);
            var uids = found.Where(u => u.Id > last).OrderBy(u => u.Id).ToList();
            var taken = uids.Take(max).ToList();
            var items = new List<IncomingRef>(taken.Count);
            if (taken.Count > 0)
            {
                // A message expunged between the search and the fetch is simply missing from the summaries.
                foreach (var summary in (await Inbox.FetchAsync(taken, ListItems, ct).ConfigureAwait(false)).OrderBy(s => s.UniqueId.Id))
                {
                    items.Add(Ref(validity, summary));
                }
            }

            var end = taken.Count > 0 ? taken[^1].Id : last;
            return new IncomingBatch(items, Cursor(validity, end), uids.Count > taken.Count);
        }

        /// <summary>
        /// The messages whose internal date is at or after <paramref name="since"/>, in UID order: IMAP <c>SINCE</c> compares whole dates in
        /// the server's time zone, so it searches from the day before and the internal date filters exactly.
        /// </summary>
        private async Task<IncomingBatch> FromDateAsync(uint validity, DateTime since, int max, CancellationToken ct)
        {
            var sinceUtc = since.Kind == DateTimeKind.Local ? since.ToUniversalTime() : DateTime.SpecifyKind(since, DateTimeKind.Utc);
            var query = sinceUtc.Date > DateTime.MinValue.AddDays(1) ? SearchQuery.DeliveredAfter(sinceUtc.Date.AddDays(-1)) : SearchQuery.All;
            var candidates = (await Inbox.SearchAsync(query, ct).ConfigureAwait(false)).OrderBy(u => u.Id).ToList();
            var items = new List<IncomingRef>();
            uint? examined = null;
            var next = 0;
            while (next < candidates.Count && items.Count < max)
            {
                var chunk = candidates.Skip(next).Take(max).ToList();
                var summaries = (await Inbox.FetchAsync(chunk, ListItems, ct).ConfigureAwait(false)).ToDictionary(s => s.UniqueId.Id);
                foreach (var uid in chunk)
                {
                    next++;
                    examined = uid.Id;
                    if (summaries.TryGetValue(uid.Id, out var summary) && summary.InternalDate is { } received && received.UtcDateTime >= sinceUtc)
                    {
                        items.Add(Ref(validity, summary));
                        if (items.Count == max)
                        {
                            break;
                        }
                    }
                }
            }

            return new IncomingBatch(items, examined is { } end ? Cursor(validity, end) : null, next < candidates.Count);
        }

        private static IncomingRef Ref(uint validity, IMessageSummary summary) => new(
            ServerId(validity, summary.UniqueId.Id),
            summary.Envelope?.MessageId,
            null,
            summary.InternalDate?.UtcDateTime ?? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
            Cursor(validity, summary.UniqueId.Id));

        private static async Task<T> Run<T>(CancellationToken ct, Func<Task<T>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                throw Map(e);
            }
        }
    }
}
