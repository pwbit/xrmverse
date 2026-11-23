using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core.Mail;

/// <summary>
/// The tracking tokens of outgoing email (plan 4.1b). They are on when the organization has a <c>trackingprefix</c> (off by default
/// in xrmverse; a new Dataverse environment has them on: Known limits). A token is the prefix followed by <c>nexttrackingnumber</c>
/// padded with zeros to <c>trackingtokeniddigits</c>, and the number goes up by one, under a lock of the organization row. The format,
/// and that SendEmail appends the token to the subject, are from memory: checklist 7.
/// </summary>
public static class TrackingTokens
{
    /// <summary>The organization column of the tracking token base, which xrmverse does not support (final review I3).</summary>
    public const string IdBaseColumn = "trackingtokenidbase";

    /// <summary>
    /// Refuses a write of <c>organization.trackingtokenidbase</c> other than empty or 0. Learn describes it as the base number that keeps
    /// the tokens of users in different deployments apart; how Dataverse combines it with the counter is not documented, so a value
    /// xrmverse would ignore is refused instead (Known limits).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>InvalidArgument</c> naming the column.</exception>
    public static void DemandSupportedOrganizationColumns(Entity row, string table)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (table != "organization" || !row.Attributes.TryGetValue(IdBaseColumn, out var raw)) return;
        if (raw is null or 0) return;
        throw XrmdFaultException.InvalidArgument(
            $"'{IdBaseColumn}' of the organization is not supported by xrmverse: only empty or 0 is accepted (got {raw}). Tracking tokens " +
            "are the trackingprefix followed by nexttrackingnumber, padded to trackingtokeniddigits.");
    }

    /// <summary>
    /// The tracking token that <paramref name="subject"/> already carries in the organization's current format (the
    /// <c>trackingprefix</c> followed by at least <c>trackingtokeniddigits</c> digits, the last one in the subject), or null when it
    /// carries none or tracking tokens are off. An email sent again after a failure keeps the token it was given (final review M2).
    /// </summary>
    public static async Task<string?> FindInSubjectAsync(DataSession s, string? subject, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (string.IsNullOrEmpty(subject)) return null;
        string? prefix;
        int digits;
        await using (var cmd = s.Command(s.Dialect.Limit($"SELECT trackingprefix, trackingtokeniddigits FROM {s.DataTable("organization")}", "1")))
        {
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            prefix = rd.IsDBNull(0) ? null : rd.GetString(0);
            digits = rd.IsDBNull(1) ? 0 : Math.Clamp(rd.GetInt32(1), 0, 20);
        }
        if (string.IsNullOrEmpty(prefix)) return null;
        var matches = Regex.Matches(subject, Regex.Escape(prefix) + "[0-9]{" + Math.Max(digits, 1).ToString(CultureInfo.InvariantCulture) + ",}(?![0-9])",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return matches.Count == 0 ? null : matches[^1].Value;
    }

    /// <summary>True when tracking tokens are on (the organization has a <c>trackingprefix</c>).</summary>
    public static async Task<bool> EnabledAsync(DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"SELECT trackingprefix FROM {s.DataTable("organization")}");
        return await cmd.ExecuteScalarAsync(ct) is string { Length: > 0 };
    }

    /// <summary>The next token, taken in <paramref name="s"/>'s transaction; null when tracking tokens are off.</summary>
    /// <exception cref="XrmdFaultException">400 when <c>nexttrackingnumber</c> passed <c>maximumtrackingnumber</c>.</exception>
    public static async Task<string?> NextAsync(DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        Guid organization;
        string? prefix;
        int digits, next;
        int? maximum;
        await using (var cmd = s.Command(
                         $"SELECT organizationid, trackingprefix, trackingtokeniddigits, nexttrackingnumber, maximumtrackingnumber " +
                         $"FROM {s.DataTable("organization")}{s.Dialect.LockHint(RowLock.UpdateRow)}{s.Dialect.LockClause(RowLock.UpdateRow)}"))
        {
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            organization = rd.GetGuid(0);
            prefix = rd.IsDBNull(1) ? null : rd.GetString(1);
            digits = rd.IsDBNull(2) ? 0 : Math.Clamp(rd.GetInt32(2), 0, 20);
            next = rd.IsDBNull(3) ? 1 : Math.Max(rd.GetInt32(3), 1);
            maximum = rd.IsDBNull(4) ? null : rd.GetInt32(4);
        }
        if (string.IsNullOrEmpty(prefix)) return null;
        if ((maximum is > 0 && next > maximum) || next == int.MaxValue)
            throw XrmdFaultException.InvalidArgument(
                $"No tracking token is left: the next tracking number {next} is above the organization's maximumtrackingnumber {maximum}. " +
                "Raise maximumtrackingnumber, or turn tracking tokens off (clear trackingprefix).");
        await using (var update = s.Command($"UPDATE {s.DataTable("organization")} SET nexttrackingnumber = @next WHERE organizationid = @id"))
        {
            update.AddParameter("@next", DbType.Int32, next + 1);
            update.AddParameter("@id", DbType.Guid, organization);
            await update.ExecuteNonQueryAsync(ct);
        }
        return prefix + next.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }
}
