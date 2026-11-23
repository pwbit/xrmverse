using MimeKit;

namespace Xrmd.Core.Mail;

/// <summary>One message of an inbox listing, before its content is fetched.</summary>
/// <param name="ServerId">The server's id of the message: <c>{uidvalidity}:{uid}</c> for IMAP, the message id for Graph.</param>
/// <param name="MessageId">The <c>Message-ID</c> as the server reports it (angle brackets may or may not be there), or null.</param>
/// <param name="ConversationIndex">The base64 <c>Thread-Index</c> Graph reports as <c>conversationIndex</c>; null for IMAP (read it from the MIME).</param>
/// <param name="ReceivedOn">When the server received it (IMAP internal date, Graph <c>receivedDateTime</c>), in UTC.</param>
/// <param name="CursorAfter">The cursor to resume after this item; null when stopping after it is not a safe resume point.</param>
public sealed record IncomingRef(string ServerId, string? MessageId, string? ConversationIndex, DateTime ReceivedOn, string? CursorAfter);

/// <summary>One page of an inbox listing.</summary>
/// <param name="Items">The messages, oldest first in the server's order (UID order for IMAP, page order for Graph).</param>
/// <param name="CursorAtEnd">The cursor to resume after the whole page; null when there is no safe resume point.</param>
/// <param name="More">True when the server has more messages after this page.</param>
public sealed record IncomingBatch(IReadOnlyList<IncomingRef> Items, string? CursorAtEnd, bool More);

/// <summary>An open, read-only connection to one mailbox's inbox. Nothing here changes the server mailbox.</summary>
public interface IIncomingSession : IAsyncDisposable
{
    /// <summary>
    /// Lists the inbox from <paramref name="cursor"/>, or, when the cursor is null or not usable (another IMAP UIDVALIDITY, a Graph link
    /// to another host), from the messages received at or after <paramref name="since"/>. Returns at most <paramref name="max"/> items
    /// for IMAP; Graph returns one page, which <c>Prefer: odata.maxpagesize</c> asks to hold at most <paramref name="max"/>.
    /// </summary>
    /// <exception cref="MailSendException">Any failure.</exception>
    Task<IncomingBatch> ListAsync(string? cursor, DateTime since, int max, CancellationToken ct);

    /// <summary>The full MIME message of <paramref name="item"/>, read without marking it read; null when the message is gone.</summary>
    /// <exception cref="MailSendException">Any failure.</exception>
    Task<MimeMessage?> FetchAsync(IncomingRef item, CancellationToken ct);
}

/// <summary>Reads incoming mail through one transport (IMAP or Microsoft Graph), read-only.</summary>
public interface IIncomingMailProvider
{
    /// <summary>
    /// Opens the inbox of the mailbox <paramref name="address"/>: this is also the incoming test. Authentication, TLS and configuration
    /// failures are permanent with <see cref="MailSendException.Scope"/> Mailbox; network failures, an IMAP <c>NO</c> or <c>BYE</c> on
    /// connect and Graph 429 or 5xx are transient.
    /// </summary>
    /// <param name="address">The mailbox's email address.</param>
    /// <param name="profile">
    /// The profile with its INCOMING fields in <see cref="MailProfile.ServerLocation"/>, <see cref="MailProfile.Port"/>,
    /// <see cref="MailProfile.UseSsl"/> and <see cref="MailProfile.Protocol"/>, as <c>MailConnections.ResolveIncomingAsync</c> builds it. A
    /// profile without an incoming server is refused (IMAP: permanent, Mailbox scope).
    /// </param>
    /// <param name="credentials">The incoming credentials (IMAP user name and password, or the Graph client secret).</param>
    /// <exception cref="MailSendException">Any failure.</exception>
    Task<IIncomingSession> OpenAsync(string address, MailProfile profile, MailCredentials credentials, CancellationToken ct);
}
