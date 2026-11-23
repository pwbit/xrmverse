using FluentAssertions;
using Xrmd.Core.Mail;

namespace Xrmd.Core.Tests.Mail;

/// <summary>Plan 4.1c, task 1: the organization-wide key of a mail message (its <c>Message-ID</c>, or a content hash without one).</summary>
public sealed class MailMessageKeysTests
{
    [Fact]
    public void Normalize_StripsBracketsAndSpace()
    {
        MailMessageKeys.Normalize(" <a@b> ").Should().Be("a@b");
        MailMessageKeys.Normalize("a@b").Should().Be("a@b");
        MailMessageKeys.Normalize("<<a@b>>").Should().Be("<a@b>", "one pair of brackets is stripped");
    }

    [Fact]
    public void Normalize_EmptyOrOver998_IsNull()
    {
        MailMessageKeys.Normalize(null).Should().BeNull();
        MailMessageKeys.Normalize("").Should().BeNull();
        MailMessageKeys.Normalize("   ").Should().BeNull();
        MailMessageKeys.Normalize("<>").Should().BeNull();
        MailMessageKeys.Normalize(" < > ").Should().BeNull();
        var longest = new string('a', 994) + "@b.c";
        MailMessageKeys.Normalize($"<{longest}>").Should().Be(longest);
        MailMessageKeys.Normalize($"<{longest}x>").Should().BeNull();
    }

    [Fact]
    public void KeyOf_IsCaseSensitive()
    {
        var lower = MailMessageKeys.KeyOf("a@b");
        lower.Should().HaveCount(32);
        lower.Should().Equal(System.Security.Cryptography.SHA256.HashData("a@b"u8));
        MailMessageKeys.KeyOf("a@B").Should().NotEqual(lower);
    }

    [Fact]
    public void Key_MissingMessageId_UsesContentHash()
    {
        var sent = new DateTime(2026, 11, 23, 9, 30, 0, DateTimeKind.Utc);
        var key = MailMessageKeys.KeyOf(sent, "Ada@Example.com", ["Grace@example.com", "bob@example.com"], "Hello", 120);

        key.Should().HaveCount(32);
        // The same Date, From, To, Subject and body length give the same key: nothing of the mailbox or the server id goes in, and
        // the addresses compare without case; a local time is the same instant in UTC.
        MailMessageKeys.KeyOf(sent.ToLocalTime(), "ada@example.com", ["grace@EXAMPLE.com", "BOB@example.com"], "Hello", 120).Should().Equal(key);
        MailMessageKeys.KeyOf(sent, "ada@example.com", ["grace@example.com", "bob@example.com"], "Hello", 121).Should().NotEqual(key);
        MailMessageKeys.KeyOf(sent, "ada@example.com", ["grace@example.com", "bob@example.com"], "hello", 120).Should().NotEqual(key);
        MailMessageKeys.KeyOf(sent.AddSeconds(1), "ada@example.com", ["grace@example.com", "bob@example.com"], "Hello", 120).Should().NotEqual(key);
        MailMessageKeys.KeyOf(null, "ada@example.com", [], null, 0).Should().HaveCount(32);
        MailMessageKeys.KeyOf(sent, "ada@example.com", ["grace@example.com", "bob@example.com"], "Hello", 120)
            .Should().Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                "2026-11-23T09:30:00.0000000Z\nada@example.com\ngrace@example.com;bob@example.com\nHello\n120")));
    }
}
