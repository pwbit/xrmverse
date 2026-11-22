using System.Globalization;

namespace Xrmd.Core;

/// <summary>
/// The <c>DataToken</c> of change tracking (plan 2.13): <c>{version}!{yyyy-MM-dd HH:mm:ss}</c>, where <c>version</c> is a
/// decimal <see cref="long"/> (the highest database version the read covered) and the time is UTC, invariant culture, to the
/// second. A token is read by its version, the part before the first <c>!</c>; the time is the issue time, which the expiry by
/// age and by restore reads (<see cref="ChangeTracker"/>).
/// </summary>
public static class ChangeToken
{
    /// <summary>The separator between the version and the time.</summary>
    public const char Separator = '!';

    /// <summary>The format of the time part.</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>The token of <paramref name="version"/> issued at <paramref name="utc"/>.</summary>
    public static string Format(long version, DateTime utc) =>
        version.ToString(CultureInfo.InvariantCulture) + Separator
        + utc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the version of <paramref name="token"/>: the part before the first <c>!</c> (the whole text without one) must be
    /// decimal digits only that fit a <see cref="long"/>; false otherwise (empty, signed, spaced or garbage).
    /// </summary>
    public static bool TryParse(string? token, out long version)
    {
        version = 0;
        if (string.IsNullOrEmpty(token)) return false;
        var bang = token.IndexOf(Separator);
        var part = bang < 0 ? token.AsSpan() : token.AsSpan(0, bang);
        return part.Length > 0 && long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    /// <summary>
    /// Like <see cref="TryParse(string?, out long)"/>, and also reads the issue time: the part after the first <c>!</c> in
    /// exactly <see cref="TimeFormat"/>, as UTC; <paramref name="issuedUtc"/> is null when it is missing or not that.
    /// </summary>
    public static bool TryParse(string? token, out long version, out DateTime? issuedUtc)
    {
        issuedUtc = null;
        if (!TryParse(token, out version)) return false;
        var bang = token!.IndexOf(Separator);
        if (bang >= 0 && DateTime.TryParseExact(token.AsSpan(bang + 1), TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var issued))
            issuedUtc = issued;
        return true;
    }
}
