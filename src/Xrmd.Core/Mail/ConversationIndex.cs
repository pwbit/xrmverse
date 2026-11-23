namespace Xrmd.Core.Mail;

/// <summary>
/// The conversation index (<c>Thread-Index</c>, Graph's <c>conversationIndex</c>): a 22-byte header followed by one 5-byte block per
/// reply. The layout is from memory (checklist 5).
/// </summary>
public static class ConversationIndex
{
    private const int HeaderLength = 22, BlockLength = 5;

    /// <summary>
    /// The index of the message this one replies to: <paramref name="base64"/> without its last child block, in base64. Null when the
    /// text is not base64, has no child block (22 bytes or fewer), or does not end on a block boundary.
    /// </summary>
    public static string? Parent(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        var buffer = new byte[base64.Length * 3 / 4 + 3];
        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out var length)) return null;
        if (length <= HeaderLength || (length - HeaderLength) % BlockLength != 0) return null;
        return Convert.ToBase64String(buffer, 0, length - BlockLength);
    }
}
