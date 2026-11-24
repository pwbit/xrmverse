using System.Data;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using MimeKit;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Core.Mail;

/// <summary>The result of correlating an incoming email (<c>email.correlationmethod</c>): 0 None, 3 InReplyTo, 4 TrackingToken, 5 ConversationIndex.</summary>
/// <param name="Method">The correlation method; 0 when nothing matched (then the other two are null). Never 6.</param>
/// <param name="ParentEmailId">The earlier email that matched.</param>
/// <param name="Regarding">The regarding record of that email; null when it has none.</param>
public sealed record Correlation(int Method, Guid? ParentEmailId, EntityReference? Regarding);

/// <summary>
/// An earlier email that an incoming one correlates with names a regarding record whose table cannot be resolved (plan 4.1c, ruling T8-a):
/// caused by the stored data, so importing the message again fails again.
/// </summary>
public sealed class UnresolvedRegardingException(string message) : InvalidOperationException(message);

/// <summary>
/// Plan 4.1c, task 6: correlates an incoming email with an earlier one, first hit wins: In-Reply-To then References (last to first)
/// by message key (method 3), then the tracking tokens of the subject, last first (method 4), then the parent of the conversation
/// index (method 5). The header and index steps run only when <c>organization.emailcorrelationenabled</c> is true, the token step
/// only when tracking tokens are on.
/// </summary>
public static class EmailCorrelator
{
    /// <summary>
    /// Correlates <paramref name="message"/>. In-Reply-To, References and Subject are read from the message itself.
    /// </summary>
    /// <param name="conversationIndex">Graph's <c>conversationIndex</c>; when null, the message's MIME <c>Thread-Index</c> header (IMAP).</param>
    /// <exception cref="UnresolvedRegardingException">The earlier email that matched names a regarding record of no known table.</exception>
    public static async Task<Correlation> CorrelateAsync(DataSession s, IMetadataStore metadata, MimeMessage message, string? conversationIndex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(message);
        var enabled = await CorrelationEnabledAsync(s, ct);

        if (enabled)
        {
            var ids = new List<string>();
            if (message.InReplyTo is { } replyTo) ids.Add(replyTo);
            ids.AddRange(message.References.Reverse());
            foreach (var raw in ids)
            {
                if (MailMessageKeys.Normalize(raw) is not { } id) continue;
                if (await MailMessageKeys.FindAsync(s, MailMessageKeys.KeyOf(id), ct) is not { } owner) continue;
                if (await ParentAsync(s, metadata, owner.EmailId, ct) is { } hit) return hit with { Method = MailSchema.CorrelationInReplyTo };
            }
        }

        if (await TrackingTokens.FormatAsync(s, ct) is var (prefix, digits))
            foreach (var token in TrackingTokens.FindAllInSubject(message.Subject, prefix, digits))
                if (await FindStoredAsync(s, metadata, "trackingtoken", token, ct) is { } hit)
                    return hit with { Method = MailSchema.CorrelationTrackingToken };

        if (enabled)
        {
            var index = conversationIndex ?? message.Headers["Thread-Index"];
            if (ConversationIndex.Parent(index) is { } parentIndex && await FindStoredAsync(s, metadata, "conversationindex", parentIndex, ct) is { } hit)
                return hit with { Method = MailSchema.CorrelationConversationIndex };
        }

        return new Correlation(MailSchema.CorrelationNone, null, null);
    }

    private static async Task<bool> CorrelationEnabledAsync(DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit($"SELECT {s.Quote(MailSchema.CorrelationEnabledColumn)} FROM {s.DataTable("organization")}", "1"));
        var value = await cmd.ExecuteScalarAsync(ct);
        // A NULL switch counts as enabled: the seed default is true and is backfilled, so NULL only means an unseeded row.
        return value is null or DBNull || Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    /// <summary>The newest email whose <paramref name="column"/> equals <paramref name="value"/> exactly (case kept), with its regarding record.</summary>
    private static async Task<Correlation?> FindStoredAsync(DataSession s, IMetadataStore metadata, string column, string value, CancellationToken ct)
    {
        (Guid Id, Guid? Regarding, int? TypeCode)? found = null;
        await using (var cmd = s.Command(
            $"SELECT activityid, regardingobjectid, regardingobjectidtype, {s.Quote(column)} FROM {s.DataTable("email")} " +
            $"WHERE {s.Quote(column)} = @v ORDER BY createdon DESC, activityid DESC"))
        {
            cmd.AddParameter("@v", DbType.String, value, 2048);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            // The column compares case-insensitively on some engines; a token and an index are case-sensitive, so the match is made here.
            while (found is null && await rd.ReadAsync(ct))
                if (string.Equals(rd.GetString(3), value, StringComparison.Ordinal)) found = Row(rd);
        }
        return found is { } row ? ToCorrelation(metadata, row) : null;
    }

    private static async Task<Correlation?> ParentAsync(DataSession s, IMetadataStore metadata, Guid emailId, CancellationToken ct)
    {
        (Guid Id, Guid? Regarding, int? TypeCode)? found = null;
        await using (var cmd = s.Command($"SELECT activityid, regardingobjectid, regardingobjectidtype FROM {s.DataTable("email")} WHERE activityid = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, emailId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) found = Row(rd);
        }
        return found is { } row ? ToCorrelation(metadata, row) : null;
    }

    private static (Guid Id, Guid? Regarding, int? TypeCode) Row(IDataRecord rd) =>
        (rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetGuid(1), rd.IsDBNull(2) ? null : Convert.ToInt32(rd.GetValue(2), CultureInfo.InvariantCulture));

    /// <summary>
    /// The regarding lookup stores the target's object type code beside its id (<c>regardingobjectidtype</c>); the table comes from the
    /// metadata store.
    /// </summary>
    /// <exception cref="UnresolvedRegardingException">The email has a regarding id whose type is missing or names no table.</exception>
    private static Correlation ToCorrelation(IMetadataStore metadata, (Guid Id, Guid? Regarding, int? TypeCode) row)
    {
        if (row.Regarding is not { } regardingId) return new Correlation(MailSchema.CorrelationNone, row.Id, null);
        var table = row.TypeCode is { } code ? metadata.GetEntityByObjectTypeCode(code)?.LogicalName : null;
        return table is null
            ? throw new UnresolvedRegardingException(
                $"The email {row.Id} is regarding record {regardingId}, but its object type code {row.TypeCode?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} names no table.")
            : new Correlation(MailSchema.CorrelationNone, row.Id, new EntityReference(table, regardingId));
    }
}
