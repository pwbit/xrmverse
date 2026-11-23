using System.Text;
using FluentAssertions;
using MimeKit;
using Xrmd.Core.Mail;

namespace Xrmd.Core.Tests.Mail;

/// <summary>Plan 4.1c, task 8: what an incoming MIME message gives the email it becomes.</summary>
public sealed class IncomingMessageReaderTests
{
    private static MimeMessage Message(MimeEntity body, string subject = "Hello")
    {
        var m = new MimeMessage { Subject = subject, Body = body, MessageId = "abc.123@example.com" };
        m.From.Add(new MailboxAddress("Ada", "ada@example.com"));
        m.To.Add(new MailboxAddress("Desk", "desk@example.com"));
        m.To.Add(MailboxAddress.Parse("second@example.com"));
        m.Cc.Add(MailboxAddress.Parse("cc@example.com"));
        m.Bcc.Add(MailboxAddress.Parse("bcc@example.com"));
        m.Date = new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.FromHours(2));
        return m;
    }

    [Fact]
    public void Headers_AreRead()
    {
        var mail = IncomingMessageReader.Read(Message(new TextPart("html") { Text = "<p>Hi</p>" }));

        mail.MessageId.Should().Be("abc.123@example.com");
        mail.Subject.Should().Be("Hello");
        mail.FromAddress.Should().Be("ada@example.com");
        mail.To.Should().Equal("desk@example.com", "second@example.com");
        mail.Cc.Should().Equal("cc@example.com");
        mail.Bcc.Should().Equal("bcc@example.com");
        mail.SentOn.Should().Be(new DateTime(2026, 11, 20, 7, 30, 0, DateTimeKind.Utc));
        mail.SentOn!.Value.Kind.Should().Be(DateTimeKind.Utc);
        mail.HtmlBody.Should().Be("<p>Hi</p>");
        mail.Attachments.Should().BeEmpty();
    }

    [Fact]
    public void NoDate_NoMessageId_NoSubject_AreNullOrEmpty()
    {
        var m = new MimeMessage { Body = new TextPart("plain") { Text = "x" } };
        m.From.Add(MailboxAddress.Parse("ada@example.com"));
        m.Headers.Remove(HeaderId.Date);
        m.Headers.Remove(HeaderId.MessageId);

        var mail = IncomingMessageReader.Read(m);

        mail.SentOn.Should().BeNull();
        mail.MessageId.Should().BeNull();
        mail.Subject.Should().BeEmpty();
        mail.To.Should().BeEmpty();
    }

    [Fact]
    public void TextOnly_IsEncodedHtml()
    {
        var mail = IncomingMessageReader.Read(Message(new TextPart("plain") { Text = "a < b & c\r\nsecond line\nthird" }));

        mail.HtmlBody.Should().Be("a &lt; b &amp; c<br>second line<br>third");
    }

    [Fact]
    public void HtmlAndText_HtmlWins()
    {
        var alternative = new MultipartAlternative
        {
            new TextPart("plain") { Text = "plain text" },
            new TextPart("html") { Text = "<p>rich</p>" },
        };

        IncomingMessageReader.Read(Message(alternative)).HtmlBody.Should().Be("<p>rich</p>");
    }

    [Fact]
    public void NoBody_IsNull()
    {
        var m = new MimeMessage { Subject = "Empty" };
        m.From.Add(MailboxAddress.Parse("ada@example.com"));

        IncomingMessageReader.Read(m).HtmlBody.Should().BeNull();
    }

    [Fact]
    public void Attachments_AreRead()
    {
        var builder = new BodyBuilder { HtmlBody = "<p>See attached</p>" };
        builder.Attachments.Add("report.txt", Encoding.UTF8.GetBytes("the report"), new ContentType("text", "plain"));

        var mail = IncomingMessageReader.Read(Message(builder.ToMessageBody()));

        var attachment = mail.Attachments.Should().ContainSingle().Subject;
        attachment.FileName.Should().Be("report.txt");
        attachment.MimeType.Should().Be("text/plain");
        Encoding.UTF8.GetString(attachment.Body).Should().Be("the report");
        mail.HtmlBody.Should().Be("<p>See attached</p>");
    }

    [Fact]
    public void UnnamedAttachment_GetsName()
    {
        var mixed = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "two files" },
            new MimePart("application", "pdf")
            {
                Content = new MimeContent(new MemoryStream([1, 2, 3])), ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
            },
            new MimePart("application", "x-unknown-type")
            {
                Content = new MimeContent(new MemoryStream([4])), ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };

        var mail = IncomingMessageReader.Read(Message(mixed));

        mail.Attachments.Select(a => a.FileName).Should().Equal("attachment1.pdf", "attachment2");
        mail.Attachments[0].Body.Should().Equal(1, 2, 3);
        mail.Attachments[1].MimeType.Should().Be("application/x-unknown-type");
    }

    [Fact]
    public void AttachedMessage_IsStoredAsEml()
    {
        var inner = new MimeMessage { Subject = "Forwarded", Body = new TextPart("plain") { Text = "inner" } };
        inner.From.Add(MailboxAddress.Parse("inner@example.com"));
        var mixed = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "see the forwarded message" },
            new MessagePart { Message = inner, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) },
        };

        var mail = IncomingMessageReader.Read(Message(mixed));

        var attachment = mail.Attachments.Should().ContainSingle().Subject;
        attachment.FileName.Should().Be("attachment1.eml");
        attachment.MimeType.Should().Be("message/rfc822");
        Encoding.UTF8.GetString(attachment.Body).Should().Contain("Subject: Forwarded");
    }

    /// <summary>Ruling T11-a (final review I4): an image embedded by <c>cid:</c> becomes a plain attachment; the body keeps its reference.</summary>
    [Fact]
    public void InlineImage_IsAPlainAttachment()
    {
        var builder = new BodyBuilder { TextBody = "logo below" };
        var logo = builder.LinkedResources.Add("logo.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }, new ContentType("image", "png"));
        logo.ContentId = "logo@example.com";
        builder.HtmlBody = "<p>Hi</p><img src=\"cid:logo@example.com\">";

        var mail = IncomingMessageReader.Read(Message(builder.ToMessageBody()));

        var attachment = mail.Attachments.Should().ContainSingle("the plain and HTML alternatives are the body").Subject;
        attachment.FileName.Should().Be("logo.png");
        attachment.MimeType.Should().Be("image/png");
        attachment.Body.Should().Equal(0x89, 0x50, 0x4E, 0x47);
        mail.HtmlBody.Should().Contain("cid:logo@example.com", "the body is not rewritten");
    }

    /// <summary>Final review I4: an inline part with a content id and no name, inside multipart/related, gets the default name.</summary>
    [Fact]
    public void InlineUnnamedPart_GetsDefaultName()
    {
        var related = new MultipartRelated
        {
            new TextPart("html") { Text = "<img src=\"cid:img1\">" },
            new MimePart("image", "gif")
            {
                Content = new MimeContent(new MemoryStream([7, 8])), ContentId = "img1", ContentTransferEncoding = ContentEncoding.Base64,
                ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
            },
        };

        var mail = IncomingMessageReader.Read(Message(related));

        var attachment = mail.Attachments.Should().ContainSingle().Subject;
        attachment.FileName.Should().Be("attachment1.gif");
        attachment.Body.Should().Equal(7, 8);
        mail.HtmlBody.Should().Be("<img src=\"cid:img1\">");
    }

    /// <summary>Final review I4: a part that is not the body and has no Content-Disposition header is a real attachment.</summary>
    [Fact]
    public void PartWithoutDisposition_IsAnAttachment()
    {
        var mixed = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "the invoice" },
            new MimePart("application", "pdf") { Content = new MimeContent(new MemoryStream([1, 2])), ContentTransferEncoding = ContentEncoding.Base64 },
        };
        mixed[1].ContentType.Name = "invoice.pdf";
        mixed[1].Headers.Contains(HeaderId.ContentDisposition).Should().BeFalse();

        var mail = IncomingMessageReader.Read(Message(mixed));

        var attachment = mail.Attachments.Should().ContainSingle().Subject;
        attachment.FileName.Should().Be("invoice.pdf");
        attachment.MimeType.Should().Be("application/pdf");
        mail.HtmlBody.Should().Be("the invoice");
    }

    /// <summary>Final review I4: neither text of a multipart/alternative body is an attachment, nor a text part attached by name is lost.</summary>
    [Fact]
    public void AlternativeBody_IsNotAnAttachment_NamedTextIs()
    {
        var mixed = new Multipart("mixed")
        {
            new MultipartAlternative { new TextPart("plain") { Text = "plain" }, new TextPart("html") { Text = "<p>rich</p>" } },
            new TextPart("plain") { Text = "notes", ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = "notes.txt" } },
        };

        var mail = IncomingMessageReader.Read(Message(mixed));

        mail.Attachments.Should().ContainSingle().Which.FileName.Should().Be("notes.txt");
        mail.HtmlBody.Should().Be("<p>rich</p>");
    }
}
