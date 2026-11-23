using MimeKit;

namespace Xrmd.Core.Mail;

/// <summary>
/// What a provider needs from an <c>emailserverprofile</c> row (plain data, built by the send job from the row). Nothing here is a secret.
/// </summary>
/// <param name="ServerLocation">Outgoing server host name (<c>outgoingserverlocation</c>); SMTP only.</param>
/// <param name="Port">Outgoing port (<c>outgoingportnumber</c>); null uses 465 with SSL and 587 without.</param>
/// <param name="UseSsl"><c>outgoingusessl</c>: true is TLS from the first byte, false requires STARTTLS (plaintext only with the server setting <c>Mail:AllowInsecureSmtp</c>).</param>
/// <param name="Protocol">Outgoing authentication protocol value (3 Basic, 4 OAuth), informational.</param>
/// <param name="TenantId">Microsoft Entra tenant id (<c>exchangeonlinetenantid</c>); Graph only.</param>
/// <param name="ClientId">Application (client) id (<c>oauthclientid</c>); Graph only.</param>
/// <param name="ServerAuthority"><c>serverauthority</c>: 0 Public, 1 US Government, 2 China, 3 Automatic; Graph only.</param>
public sealed record MailProfile(
    string? ServerLocation, int? Port, bool UseSsl, int? Protocol, string? TenantId, string? ClientId, int ServerAuthority);

/// <summary>The secrets a send uses, read from the secret store by the send job. Never logged; <see cref="ToString"/> hides them.</summary>
/// <param name="UserName">SMTP user name (the mailbox's, or the profile's).</param>
/// <param name="Password">SMTP password.</param>
/// <param name="ClientSecret">Microsoft Entra application secret (<c>oauthclientsecret</c>); Graph only.</param>
public sealed record MailCredentials(string? UserName, string? Password, string? ClientSecret)
{
    /// <inheritdoc />
    public override string ToString() => "MailCredentials { ... }";
}

/// <summary>Every failure of a provider. <see cref="Transient"/> tells the send job whether a retry can help.</summary>
public sealed class MailSendException : Exception
{
    /// <summary>Creates the exception. The message never carries a secret.</summary>
    /// <param name="message">What failed, for the email's failure text.</param>
    /// <param name="transient">True for a failure a later retry may cure (4xx, 429, 5xx of Graph, network), false for a permanent one.</param>
    /// <param name="inner">The original exception, if any.</param>
    public MailSendException(string message, bool transient, Exception? inner = null) : base(message, inner) => Transient = transient;

    /// <summary>True when a retry may succeed.</summary>
    public bool Transient { get; }

    /// <summary>
    /// What the failure is about: the mailbox (its server, credentials, TLS or configuration; the default) or only this message (a
    /// recipient refused, the message too large or malformed). The send job sets the mailbox's outgoing status Failure only for the first.
    /// </summary>
    public MailFailureScope Scope { get; init; } = MailFailureScope.Mailbox;
}

/// <summary>The scope of a <see cref="MailSendException"/>.</summary>
public enum MailFailureScope
{
    /// <summary>The mailbox cannot send (authentication, TLS, configuration, token, connection): every later message would fail too.</summary>
    Mailbox,

    /// <summary>Only this message was refused (a recipient, its size, its content): the mailbox still sends.</summary>
    Message,
}

/// <summary>Sends a built message through one transport.</summary>
public interface IMailProvider
{
    /// <summary>Sends <paramref name="message"/>.</summary>
    /// <exception cref="MailSendException">Any failure.</exception>
    Task SendAsync(MimeMessage message, MailProfile profile, MailCredentials credentials, CancellationToken ct);

    /// <summary>Checks the profile and credentials for the mailbox <paramref name="address"/> without sending anything.</summary>
    /// <exception cref="MailSendException">Any failure.</exception>
    Task TestAsync(string address, MailProfile profile, MailCredentials credentials, CancellationToken ct);
}
