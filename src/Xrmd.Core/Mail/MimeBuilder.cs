using System.Net.Mail;
using MimeKit;
using MimeKit.Utils;
using Xrmd.Files;

namespace Xrmd.Core.Mail;

/// <summary>An email ready to be sent: the resolved addresses, the HTML body and the attachment contents.</summary>
/// <param name="MessageId">The Message-ID, with angle brackets (<see cref="MimeBuilder.NewMessageId"/>).</param>
public sealed record OutgoingMail(
    string MessageId,
    MailAddress From,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    IReadOnlyList<MailAddress> Bcc,
    string Subject,
    string? HtmlBody,
    IReadOnlyList<AttachmentContent> Attachments);

/// <summary>Builds the MIME message of an <see cref="OutgoingMail"/>. Addresses, subject and file names are checked for line breaks (header injection).</summary>
public static class MimeBuilder
{
    /// <summary>A fresh Message-ID <c>&lt;guid&gt;@domain</c>, the domain being the one of <paramref name="fromAddress"/> (localhost when it has none).</summary>
    public static string NewMessageId(string fromAddress)
    {
        var at = fromAddress?.LastIndexOf('@') ?? -1;
        var domain = at >= 0 && at < fromAddress!.Length - 1 ? fromAddress[(at + 1)..].Trim() : "localhost";
        if (!IsSafe(domain) || domain.Contains('>') || domain.Contains('<'))
        {
            domain = "localhost";
        }

        return $"<{Guid.NewGuid():N}@{domain}>";
    }

    /// <summary>Builds the message: headers, an HTML body and the attachments. The attachment streams are read, not disposed.</summary>
    /// <exception cref="MailSendException">Permanent: an address, the subject or a file name holds a line break or control character.</exception>
    public static MimeMessage Build(OutgoingMail mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        var message = new MimeMessage();
        message.From.Add(Mailbox(mail.From));
        message.To.AddRange(mail.To.Select(Mailbox));
        message.Cc.AddRange(mail.Cc.Select(Mailbox));
        message.Bcc.AddRange(mail.Bcc.Select(Mailbox));
        message.Subject = SafeText(mail.Subject ?? string.Empty, "subject");
        message.MessageId = SafeText(mail.MessageId, "message id").Trim().TrimStart('<').TrimEnd('>');
        message.Date = DateTimeOffset.UtcNow;

        var body = new BodyBuilder { HtmlBody = mail.HtmlBody ?? string.Empty };
        foreach (var a in mail.Attachments)
        {
            var name = SafeText(a.FileName, "attachment file name");
            var type = ContentType.TryParse(a.MimeType ?? string.Empty, out var parsed) ? parsed : new ContentType("application", "octet-stream");
            body.Attachments.Add(name, a.Body, type);
        }

        message.Body = body.ToMessageBody();
        return message;
    }

    private static MailboxAddress Mailbox(MailAddress a)
    {
        var address = SafeText(a.Address, "address");
        if (address.Any(char.IsWhiteSpace) || address.Contains('<') || address.Contains('>') || address.Contains(','))
        {
            throw new MailSendException($"The address '{address}' is not valid.", transient: false);
        }

        return new MailboxAddress(SafeText(a.DisplayName ?? string.Empty, "display name"), address);
    }

    private static string SafeText(string value, string what) =>
        IsSafe(value) ? value : throw new MailSendException($"The {what} contains a line break or control character.", transient: false);

    private static bool IsSafe(string value) => !value.Any(c => c is '\r' or '\n' or '\0' || char.IsControl(c) && c != '\t');
}
