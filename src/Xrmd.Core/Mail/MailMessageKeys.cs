using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Core.Mail;

/// <summary>
/// The organization-wide key of a mail message (plan 4.1c): the SHA-256 of its normalized <c>Message-ID</c>, or of its Date, From, To,
/// Subject and body length when it has none. <c>meta.mail_message</c> holds one row per key with the email it became, and whether it
/// came in or went out: a message is imported once whatever mailbox it reaches, and a copy of an email sent from here is never imported.
/// </summary>
public static class MailMessageKeys
{
    private const string Table = "meta.mail_message";

    /// <summary>The longest <c>Message-ID</c> kept, without its angle brackets (RFC 5322 line length).</summary>
    private const int MaxMessageIdLength = 998;

    /// <summary>
    /// The <c>Message-ID</c> as it is keyed: trimmed, without one pair of angle brackets; null when nothing is left or it is longer than
    /// 998 characters. The case is kept (a <c>Message-ID</c> compares case-sensitively).
    /// </summary>
    public static string? Normalize(string? messageId)
    {
        var value = messageId?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>') value = value[1..^1].Trim();
        return value.Length is 0 or > MaxMessageIdLength ? null : value;
    }

    /// <summary>The key of a normalized <c>Message-ID</c> (<see cref="Normalize"/>): the SHA-256 of its UTF-8 text, case kept.</summary>
    public static byte[] KeyOf(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
    }

    /// <summary>
    /// The key of a message without a <c>Message-ID</c> (ruling PF3): the SHA-256 of the UTF-8 text
    /// <c>"{sentOn, round-trip format in UTC}\n{from, lower case}\n{to, lower case, ;-joined}\n{subject}\n{bodyLength}"</c>. It does not
    /// depend on the mailbox or the server's id of the message, so it survives a UIDVALIDITY reset.
    /// </summary>
    /// <param name="sentOn">The message's Date (empty when it has none); a local or unspecified time is taken as UTC once converted.</param>
    /// <param name="bodyLength">The length in UTF-8 bytes of the body, as <c>IncomingMessageReader</c> reads it.</param>
    /// <remarks>
    /// Known limit (plan 4.1c, task 11): two different messages with the same Date, From, To, Subject and body length share a key, and the
    /// second is not imported.
    /// </remarks>
    public static byte[] KeyOf(DateTime? sentOn, string from, IReadOnlyList<string> to, string? subject, int bodyLength)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var date = sentOn is { } d
            ? (d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture)
            : "";
        var text = string.Join('\n', date, from.ToLowerInvariant(), string.Join(';', to.Select(a => a.ToLowerInvariant())), subject ?? "",
            bodyLength.ToString(CultureInfo.InvariantCulture));
        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Registers <paramref name="key"/> for <paramref name="emailId"/> in <paramref name="s"/>'s transaction unless it is registered
    /// already (ruling PF10: an insert-if-absent of the dialect, <see cref="ISqlDialect.InsertIfAbsent"/>). A key a concurrent
    /// transaction holds makes the call wait for that transaction's outcome. A duplicate key is never an error, so the caller's
    /// transaction stays usable.
    /// </summary>
    /// <returns>Null when the key was inserted; otherwise the email that owns it.</returns>
    public static async Task<Guid?> TryRegisterAsync(DataSession s, byte[] key, Guid emailId, bool incoming, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        DemandKey(key);
        // A key found at the insert can be gone at the read (its email deleted meanwhile): then the insert runs again, once.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using (var cmd = s.Command(s.Dialect.InsertIfAbsent(Table, ["messagekey"], ["emailid", "incoming", "createdon"],
                             $"SELECT @key AS messagekey, @email AS emailid, @incoming AS incoming, {s.Dialect.UtcNow} AS createdon")))
            {
                cmd.AddParameter("@key", DbType.Binary, key, key.Length);
                cmd.AddParameter("@email", DbType.Guid, emailId);
                cmd.AddParameter("@incoming", DbType.Boolean, incoming);
                if (await cmd.ExecuteNonQueryAsync(ct) > 0) return null;
            }
            if (await FindAsync(s, key, ct) is { } owner) return owner.EmailId;
        }
        throw new InvalidOperationException("The message key was neither inserted nor found: its owner is deleted and inserted again meanwhile.");
    }

    /// <summary>The email that owns <paramref name="key"/> and whether it came in; null when the key is not registered.</summary>
    public static async Task<(Guid EmailId, bool Incoming)?> FindAsync(DataSession s, byte[] key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        DemandKey(key);
        await using var cmd = s.Command($"SELECT emailid, incoming FROM {Table} WHERE messagekey = @key");
        cmd.AddParameter("@key", DbType.Binary, key, key.Length);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return (rd.GetGuid(0), Convert.ToBoolean(rd.GetValue(1), CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Registers the key of the <c>Message-ID</c> of an email sent from here (outgoing), unless it is empty or the key is registered
    /// already: by the send job when it stores the email's <c>messageid</c>, and by the startup upgrade for emails sent before plan 4.1c.
    /// </summary>
    /// <returns>True when a key row was inserted.</returns>
    public static async Task<bool> RegisterOutgoingAsync(DataSession s, Guid emailId, string? messageId, CancellationToken ct) =>
        Normalize(messageId) is { } normalized && await TryRegisterAsync(s, KeyOf(normalized), emailId, incoming: false, ct) is null;

    /// <summary>Deletes the key rows of <paramref name="emailId"/> in <paramref name="s"/>'s transaction (the email's Delete).</summary>
    public static async Task DeleteAsync(DataSession s, Guid emailId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"DELETE FROM {Table} WHERE emailid = @email");
        cmd.AddParameter("@email", DbType.Guid, emailId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void DemandKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("A message key is 32 bytes (SHA-256).", nameof(key));
    }
}
