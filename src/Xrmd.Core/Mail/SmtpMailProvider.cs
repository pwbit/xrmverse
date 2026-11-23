using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Xrmd.Core.Mail;

/// <summary>
/// Sends through SMTP with MailKit. UseSsl true is TLS on connect (port 465 by default), false REQUIRES STARTTLS (port 587): a server
/// without it is a permanent failure before any credential is sent, unless <c>Mail:AllowInsecureSmtp</c> is true, which allows
/// plaintext (STARTTLS still used when offered). Server certificates are always validated. SMTP 4xx and network failures are transient; 5xx, authentication and TLS
/// failures are permanent.
/// </summary>
public sealed class SmtpMailProvider : IMailProvider
{
    /// <summary>Connect, read and write timeout of the SMTP conversation.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly bool _allowInsecure;

    /// <summary>Creates the provider.</summary>
    /// <param name="allowInsecure">The server setting <c>Mail:AllowInsecureSmtp</c> (default false): allows a plaintext SMTP session when the server has no STARTTLS.</param>
    public SmtpMailProvider(bool allowInsecure = false) => _allowInsecure = allowInsecure;

    /// <inheritdoc />
    public async Task SendAsync(MimeMessage message, MailProfile profile, MailCredentials credentials, CancellationToken ct)
    {
        await Run(profile, credentials, ct, (client, token) => client.SendAsync(message, token)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TestAsync(string address, MailProfile profile, MailCredentials credentials, CancellationToken ct)
    {
        await Run(profile, credentials, ct, (_, _) => Task.CompletedTask).ConfigureAwait(false);
    }

    private async Task Run(MailProfile profile, MailCredentials credentials, CancellationToken ct, Func<SmtpClient, CancellationToken, Task> action)
    {
        if (string.IsNullOrWhiteSpace(profile.ServerLocation))
        {
            throw new MailSendException("The email server profile has no outgoing server location.", transient: false);
        }

        if (string.IsNullOrEmpty(credentials.UserName) && !string.IsNullOrEmpty(credentials.Password))
        {
            throw new MailSendException("The SMTP password is set but the user name is empty.", transient: false);
        }

        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            var port = profile.Port ?? (profile.UseSsl ? 465 : 587);
            var options = profile.UseSsl ? SecureSocketOptions.SslOnConnect : _allowInsecure ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.StartTls;
            await client.ConnectAsync(profile.ServerLocation, port, options, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(credentials.UserName))
            {
                await client.AuthenticateAsync(credentials.UserName, credentials.Password ?? string.Empty, ct).ConfigureAwait(false);
            }

            await action(client, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            throw Map(e);
        }

        try
        {
            await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The message is already accepted: a failing QUIT must not turn into a retry (a duplicate email).
        }
    }

    /// <summary>Maps a MailKit or network failure to a <see cref="MailSendException"/>; the text never holds the credentials.</summary>
    internal static MailSendException Map(Exception e) => e switch
    {
        MailSendException m => m,
        NotSupportedException => new("TLS is required but the SMTP server does not offer STARTTLS (use SSL, or set Mail:AllowInsecureSmtp to true to allow plaintext).", false, e),
        AuthenticationException => new("SMTP authentication failed.", false, e),
        SslHandshakeException => new("The SMTP server's TLS certificate or handshake was refused.", false, e),
        SmtpCommandException c when (int)c.StatusCode is >= 400 and < 500
            => new($"SMTP server answered {(int)c.StatusCode}: {c.Message}", true, e) { Scope = ScopeOf(c) },
        SmtpCommandException c => new($"SMTP server answered {(int)c.StatusCode}: {c.Message}", false, e) { Scope = ScopeOf(c) },
        SmtpProtocolException => new("SMTP protocol error: " + e.Message, true, e),
        SocketException or IOException or TimeoutException or OperationCanceledException or ServiceNotConnectedException
            => new("The SMTP server could not be reached: " + e.Message, true, e),
        _ => new("SMTP send failed: " + e.Message, false, e),
    };

    /// <summary>
    /// A recipient refused (RCPT TO) or the message refused (after DATA: its size, its content) is about this message only; a refused
    /// sender (MAIL FROM) or any other command is about the mailbox.
    /// </summary>
    private static MailFailureScope ScopeOf(SmtpCommandException c) =>
        c.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.MessageNotAccepted ? MailFailureScope.Message : MailFailureScope.Mailbox;
}
