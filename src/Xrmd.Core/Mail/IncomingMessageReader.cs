using System.Net;
using MimeKit;

namespace Xrmd.Core.Mail;

/// <summary>An attachment of an incoming message.</summary>
/// <param name="FileName">The file name the message gives, else <c>attachment{n}</c> plus the extension of its MIME type.</param>
/// <param name="MimeType">The MIME type of the part, such as <c>application/pdf</c>.</param>
/// <param name="Body">The decoded content.</param>
public sealed record IncomingAttachment(string FileName, string MimeType, byte[] Body);

/// <summary>What an incoming message gives the email it becomes (plan 4.1c, task 8).</summary>
/// <param name="MessageId">The MIME <c>Message-ID</c> without its angle brackets; null when the message has none.</param>
/// <param name="Subject">The subject; empty when the message has none.</param>
/// <param name="FromAddress">The address of the first <c>From</c> mailbox; empty when there is none.</param>
/// <param name="To">The addresses of <c>To</c>, in order.</param>
/// <param name="Cc">The addresses of <c>Cc</c>, in order.</param>
/// <param name="Bcc">The addresses of <c>Bcc</c>, in order (a received message rarely has any).</param>
/// <param name="SentOn">The <c>Date</c> header in UTC; null when the message has none.</param>
/// <param name="HtmlBody">The HTML body, else the text body HTML-encoded with its line breaks as <c>&lt;br&gt;</c>; null when it has neither.</param>
/// <param name="Attachments">The attachments, in order: every part but the text of the body, inline (content id) parts and parts without
/// a <c>Content-Disposition</c> included.</param>
public sealed record IncomingMail(string? MessageId, string Subject, string FromAddress, IReadOnlyList<string> To, IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc, DateTime? SentOn, string? HtmlBody, IReadOnlyList<IncomingAttachment> Attachments);

/// <summary>
/// Reads an incoming MIME message into an <see cref="IncomingMail"/> (plan 4.1c, task 8). Correlation reads the
/// <see cref="MimeMessage"/> itself (<see cref="EmailCorrelator"/>), so <c>In-Reply-To</c> and <c>References</c> are not read here.
/// </summary>
public static class IncomingMessageReader
{
    /// <summary>The MIME type of an attached message: its file name gets the <c>.eml</c> extension.</summary>
    private const string MessageMimeType = "message/rfc822";

    public static IncomingMail Read(MimeMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var sentOn = m.Headers.Contains(HeaderId.Date) && m.Date != DateTimeOffset.MinValue ? m.Date.UtcDateTime : (DateTime?)null;
        return new IncomingMail(
            string.IsNullOrWhiteSpace(m.MessageId) ? null : m.MessageId,
            m.Subject ?? "",
            m.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            Addresses(m.To), Addresses(m.Cc), Addresses(m.Bcc),
            sentOn,
            Body(m),
            Attachments(m));
    }

    private static string[] Addresses(InternetAddressList list) =>
        [.. list.Mailboxes.Select(a => a.Address).Where(a => !string.IsNullOrWhiteSpace(a))];

    private static string? Body(MimeMessage m)
    {
        if (m.HtmlBody is { } html) return html;
        if (m.TextBody is not { } text) return null;
        return WebUtility.HtmlEncode(text.Replace("\r\n", "\n", StringComparison.Ordinal)).Replace("\n", "<br>", StringComparison.Ordinal);
    }

    private static List<IncomingAttachment> Attachments(MimeMessage m)
    {
        var attachments = new List<IncomingAttachment>();
        foreach (var entity in m.BodyParts.Where(e => !IsBodyText(e)))
        {
            var n = attachments.Count + 1;
            var mimeType = entity.ContentType.MimeType.ToLowerInvariant();
            byte[] body;
            using (var content = new MemoryStream())
            {
                switch (entity)
                {
                    case MessagePart { Message: { } attached }:
                        attached.WriteTo(content);
                        break;
                    case MimePart { Content: { } part }:
                        part.DecodeTo(content);
                        break;
                    default:
                        continue;
                }
                body = content.ToArray();
            }
            var name = entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
            attachments.Add(new IncomingAttachment(string.IsNullOrWhiteSpace(name) ? DefaultName(n, mimeType) : name.Trim(), mimeType, body));
        }
        return attachments;
    }

    /// <summary>
    /// Whether <paramref name="entity"/> is text of the body: a <c>text/plain</c> or <c>text/html</c> part that is not disposed as an
    /// attachment and has no file name (the body itself, the other half of a multipart/alternative). Every other part is an attachment
    /// (ruling T11-a, final review I4): an inline part referenced by <c>cid:</c> from the HTML (the body keeps its <c>cid:</c> reference, it
    /// is not rewritten), and a part without a <c>Content-Disposition</c> header.
    /// </summary>
    private static bool IsBodyText(MimeEntity entity) =>
        entity is TextPart { IsAttachment: false } text && (text.IsPlain || text.IsHtml) && string.IsNullOrWhiteSpace(text.FileName);

    /// <summary><c>attachment{n}</c> plus the extension of <paramref name="mimeType"/> (none when the type is unknown).</summary>
    private static string DefaultName(int n, string mimeType)
    {
        if (mimeType == MessageMimeType) return $"attachment{n}.eml";
        return MimeTypes.TryGetExtension(mimeType, out var extension) ? $"attachment{n}{extension}" : $"attachment{n}";
    }
}
