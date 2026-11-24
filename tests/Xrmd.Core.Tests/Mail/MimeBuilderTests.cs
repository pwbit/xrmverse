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
    public void Build_SetsInReplyTo_AndReferences_WithoutBrackets()
    {
        var m = MimeBuilder.Build(Mail() with { InReplyTo = "<p2@example.com>", References = ["<p1@example.com>", "p2@example.com"] });

        m.InReplyTo.Should().Be("p2@example.com");
        m.Headers["In-Reply-To"].Should().Be("<p2@example.com>");
        m.References.Should().Equal("p1@example.com", "p2@example.com");
        m.Headers["References"].Should().Contain("<p1@example.com>").And.Contain("<p2@example.com>");
    }

    [Fact]
    public void Build_WithoutReply_SetsNeitherHeader()
    {
        var m = MimeBuilder.Build(Mail());

        m.InReplyTo.Should().BeNull();
        m.References.Should().BeEmpty();
        m.Headers.Contains("In-Reply-To").Should().BeFalse();
        m.Headers.Contains("References").Should().BeFalse();
    }

    [Fact]
    public void InReplyTo_WithLineBreak_IsRefused()
    {
        var act = () => MimeBuilder.Build(Mail() with { InReplyTo = "<a@b>\r\nBcc: x@example.net" });
        act.Should().Throw<MailSendException>();
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
