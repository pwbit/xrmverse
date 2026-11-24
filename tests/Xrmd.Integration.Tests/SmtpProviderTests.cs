using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Xrmd.Core.Mail;
using Xrmd.Files;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The SMTP provider against <see cref="GreenMail"/> (one container for the class, a fresh recipient per test), and against
/// <see cref="FakeSmtpServer"/> for what GreenMail cannot do.
/// </summary>
public sealed class SmtpProviderTests(GreenMailFixture mail) : IClassFixture<GreenMailFixture>
{
    private GreenMail GreenMail => mail.Server;

    private MailProfile Profile(int? port = null) =>
        new(GreenMail.Hostname, port ?? GreenMail.SmtpPort, UseSsl: false, Protocol: 3, null, null, 0);

    private static MailCredentials Creds(string password = "secret") => new("sender", password, null);

    [Fact]
    public async Task Send_ArrivesAtGreenMail_WithAttachment()
    {
        var rcpt = await GreenMail.NewUserAsync();
        var id = MimeBuilder.NewMessageId("sender@example.com");
        var message = MimeBuilder.Build(new OutgoingMail(
            id, new MailAddress("sender@example.com", "Sender"), [new MailAddress(rcpt.Address)], [], [], "Quarterly numbers",
            "<p>See attached</p>", [new AttachmentContent("n.txt", "text/plain", null, new MemoryStream(Encoding.UTF8.GetBytes("42")))]));

        await new SmtpMailProvider(allowInsecure: true).SendAsync(message, Profile(), Creds(), default);

        var inbox = await GreenMail.InboxAsync(rcpt);

        inbox.Should().ContainSingle();
        var received = inbox[0];
        received.Subject.Should().Be("Quarterly numbers");
        ("<" + received.MessageId + ">").Should().Be(id);
        received.HtmlBody.Should().Contain("See attached");
        received.Attachments.Should().ContainSingle().Which.ContentDisposition!.FileName.Should().Be("n.txt");
    }

    [Fact]
    public async Task WrongPassword_IsPermanent()
    {
        var message = MimeBuilder.Build(new OutgoingMail(
            MimeBuilder.NewMessageId("sender@example.com"), new MailAddress("sender@example.com"), [new MailAddress("rcpt@example.com")], [], [], "x", "x", []));

        await using var server = FakeSmtpServer.Start(mailReply: "250 ok");
        var act = () => new SmtpMailProvider(allowInsecure: true).SendAsync(message, new MailProfile("127.0.0.1", server.Port, false, 3, null, null, 0), Creds("wrong"), default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeFalse();
        e.Scope.Should().Be(MailFailureScope.Mailbox);
        e.Message.Should().NotContain("wrong");
    }

    [Theory]
    [InlineData("451 4.3.0 try later", true)]
    [InlineData("550 5.1.1 no such user", false)]
    public async Task ServerReply_4xxIsTransient_5xxIsPermanent(string reply, bool transient)
    {
        await using var server = FakeSmtpServer.Start(mailReply: reply, authOk: true);
        var message = MimeBuilder.Build(new OutgoingMail(
            MimeBuilder.NewMessageId("sender@example.com"), new MailAddress("sender@example.com"), [new MailAddress("rcpt@example.com")], [], [], "x", "x", []));

        var act = () => new SmtpMailProvider(allowInsecure: true).SendAsync(message, new MailProfile("127.0.0.1", server.Port, false, 3, null, null, 0), Creds(), default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().Be(transient);
        e.Scope.Should().Be(MailFailureScope.Mailbox, "a refused sender (MAIL FROM) is about the mailbox");
    }

    [Fact]
    public async Task RecipientRejected_IsPermanent_AndAboutTheMessageOnly()
    {
        await using var server = FakeSmtpServer.Start(mailReply: "250 ok", authOk: true, rcptReply: "550 5.1.1 no such user");
        var message = MimeBuilder.Build(new OutgoingMail(
            MimeBuilder.NewMessageId("sender@example.com"), new MailAddress("sender@example.com"), [new MailAddress("rcpt@example.com")], [], [], "x", "x", []));

        var act = () => new SmtpMailProvider(allowInsecure: true).SendAsync(message, new MailProfile("127.0.0.1", server.Port, false, 3, null, null, 0), Creds(), default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeFalse();
        e.Scope.Should().Be(MailFailureScope.Message);
        e.Message.Should().Contain("550");
    }

    [Fact]
    public async Task NoStartTls_SettingOff_IsPermanent_AndNoCredentialsAreSent()
    {
        await using var server = FakeSmtpServer.Start(mailReply: "250 ok", authOk: true);
        var message = MimeBuilder.Build(new OutgoingMail(
            MimeBuilder.NewMessageId("sender@example.com"), new MailAddress("sender@example.com"), [new MailAddress("rcpt@example.com")], [], [], "x", "x", []));

        var act = () => new SmtpMailProvider().SendAsync(message, new MailProfile("127.0.0.1", server.Port, false, 3, null, null, 0), Creds(), default);

        var e = (await act.Should().ThrowAsync<MailSendException>()).Which;
        e.Transient.Should().BeFalse();
        e.Message.Should().Contain("STARTTLS").And.Contain("Mail:AllowInsecureSmtp");
        server.Commands.Should().NotContain("AUTH");
    }

    [Fact]
    public async Task NoStartTls_SettingOn_Works()
    {
        await using var server = FakeSmtpServer.Start(mailReply: "250 ok", authOk: true);

        await new SmtpMailProvider(allowInsecure: true).TestAsync("sender@example.com", new MailProfile("127.0.0.1", server.Port, false, 3, null, null, 0), Creds(), default);

        server.Commands.Should().Contain("AUTH");
    }

    [Fact]
    public async Task EmptyUserName_WithPassword_IsPermanent()
    {
        var act = () => new SmtpMailProvider(allowInsecure: true).TestAsync("a@example.com", Profile(), new MailCredentials("", "pw", null), default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Transient.Should().BeFalse();
    }

    [Fact]
    public async Task Send_Reply_ArrivesWithInReplyTo_AndReferences()
    {
        var rcpt = await GreenMail.NewUserAsync();
        var message = MimeBuilder.Build(new OutgoingMail(
            MimeBuilder.NewMessageId("sender@example.com"), new MailAddress("sender@example.com"), [new MailAddress(rcpt.Address)], [], [], "Re: thread", "x", [],
            InReplyTo: "<two@customer.example>", References: ["<one@customer.example>", "<two@customer.example>"]));

        await new SmtpMailProvider(allowInsecure: true).SendAsync(message, Profile(), Creds(), default);

        var received = (await GreenMail.InboxAsync(rcpt)).Should().ContainSingle().Subject;
        received.InReplyTo.Should().Be("two@customer.example");
        received.References.Should().Equal("one@customer.example", "two@customer.example");
    }

    [Fact]
    public async Task ConnectionRefused_IsTransient()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var message = MimeBuilder.Build(new OutgoingMail(
            MimeBuilder.NewMessageId("sender@example.com"), new MailAddress("sender@example.com"), [new MailAddress("rcpt@example.com")], [], [], "x", "x", []));

        var act = () => new SmtpMailProvider(allowInsecure: true).SendAsync(message, new MailProfile("127.0.0.1", freePort, false, 3, null, null, 0), Creds(), default);

        (await act.Should().ThrowAsync<MailSendException>()).Which.Transient.Should().BeTrue();
    }

    [Fact]
    public async Task Test_ChecksConnectAndLogin_WithoutSending()
    {
        // The mailbox under test is a fresh GreenMail user: a probe message sent to (or from) its address would land in its inbox. No other
        // test of this class sends to sender@example.com either, so a probe to the sender's own address would show there.
        var mailbox = await GreenMail.NewUserAsync();
        await new SmtpMailProvider(allowInsecure: true).TestAsync(mailbox.Address, Profile(), Creds(), default);

        await using var server = FakeSmtpServer.Start(mailReply: "250 ok");
        var bad = () => new SmtpMailProvider(allowInsecure: true).TestAsync(mailbox.Address, new MailProfile("127.0.0.1", server.Port, false, 3, null, null, 0), Creds("wrong"), default);
        (await bad.Should().ThrowAsync<MailSendException>()).Which.Transient.Should().BeFalse();

        (await GreenMail.InboxAsync(mailbox, atLeast: 1, TimeSpan.FromSeconds(1))).Should().BeEmpty("the test sends nothing to the mailbox");
        (await GreenMail.InboxAsync(new MailUser(GreenMail.SenderUser, GreenMail.SenderPassword, "sender@example.com"), atLeast: 1, TimeSpan.FromSeconds(1)))
            .Should().BeEmpty("the test sends nothing to the sender either");
        server.Commands.Should().NotContain(["MAIL", "RCPT", "DATA"]);
    }
}
