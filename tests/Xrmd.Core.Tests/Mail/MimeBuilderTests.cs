using System.Net.Mail;
using System.Text;
using FluentAssertions;
using Xrmd.Core.Mail;
using Xrmd.Files;

namespace Xrmd.Core.Tests.Mail;

public sealed class MimeBuilderTests
{
    private static OutgoingMail Mail(string subject = "Hello", string from = "sales@example.com", string? name = "Sales") => new(
        "<abc@example.com>",
        new MailAddress(from, name),
        [new MailAddress("to@example.com", "To One")],
        [new MailAddress("cc@example.com")],
        [new MailAddress("bcc@example.com")],
        subject,
        "<p>Hi <b>there</b></p>",
        [new AttachmentContent("a.txt", "text/plain", null, new MemoryStream(Encoding.UTF8.GetBytes("attached")))]);

    [Fact]
    public void Build_SetsMessageId_Recipients_HtmlBody_AndAttachments()
    {
        var m = MimeBuilder.Build(Mail());

        m.MessageId.Should().Be("abc@example.com");
        m.Headers["Message-ID"].Should().Be("<abc@example.com>");
        m.From.Mailboxes.Single().Address.Should().Be("sales@example.com");
        m.To.Mailboxes.Single().Address.Should().Be("to@example.com");
        m.Cc.Mailboxes.Single().Address.Should().Be("cc@example.com");
        m.Bcc.Mailboxes.Single().Address.Should().Be("bcc@example.com");
        m.Subject.Should().Be("Hello");
        m.HtmlBody.Should().Contain("<b>there</b>");
        var a = m.Attachments.Should().ContainSingle().Subject;
        a.ContentDisposition!.FileName.Should().Be("a.txt");
    }

    [Fact]
    public void NewMessageId_UsesSenderDomain()
    {
        var id = MimeBuilder.NewMessageId("sales@contoso.example.com");

        id.Should().MatchRegex("^<[0-9a-f]{32}@contoso\\.example\\.com>$");
        MimeBuilder.NewMessageId("sales@contoso.example.com").Should().NotBe(id);
    }

    [Theory]
    [InlineData("Hi\r\nBcc: evil@example.net")]
    [InlineData("Hi\nX: y")]
    public void Subject_WithLineBreak_IsRefused(string subject)
    {
        var act = () => MimeBuilder.Build(Mail(subject));
        act.Should().Throw<MailSendException>().Which.Transient.Should().BeFalse();
    }

    [Fact]
    public void DisplayName_WithLineBreak_IsRefused()
    {
        var act = () => MimeBuilder.Build(Mail(name: "A\r\nBcc: x@example.net"));
        act.Should().Throw<MailSendException>();
    }
}
