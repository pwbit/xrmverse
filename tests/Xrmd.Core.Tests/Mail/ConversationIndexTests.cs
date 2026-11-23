using FluentAssertions;
using Xrmd.Core.Mail;

namespace Xrmd.Core.Tests.Mail;

/// <summary>Plan 4.1c, task 6: the parent of a conversation index (<c>Thread-Index</c>): a 22-byte header and 5-byte child blocks.</summary>
public sealed class ConversationIndexTests
{
    private static byte[] Bytes(int length) => [.. Enumerable.Range(1, length).Select(i => (byte)i)];

    [Fact]
    public void Parent_DropsLastChildBlock()
    {
        var child = Bytes(22 + 5 + 5);
        ConversationIndex.Parent(Convert.ToBase64String(child)).Should().Be(Convert.ToBase64String(child[..^5]));
        ConversationIndex.Parent(Convert.ToBase64String(Bytes(22 + 5))).Should().Be(Convert.ToBase64String(Bytes(22)));
    }

    [Fact]
    public void Parent_HeaderOnly_IsNull()
    {
        ConversationIndex.Parent(Convert.ToBase64String(Bytes(22))).Should().BeNull();
        ConversationIndex.Parent(Convert.ToBase64String(Bytes(10))).Should().BeNull();
        ConversationIndex.Parent(null).Should().BeNull();
        ConversationIndex.Parent("").Should().BeNull();
    }

    [Fact]
    public void Parent_BadLength_IsNull() =>
        ConversationIndex.Parent(Convert.ToBase64String(Bytes(22 + 7))).Should().BeNull();

    [Fact]
    public void Parent_NotBase64_IsNull()
    {
        ConversationIndex.Parent("not base64 !!").Should().BeNull();
        ConversationIndex.Parent("abc").Should().BeNull();
    }
}
