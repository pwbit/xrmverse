using System.Globalization;
using Xrmd.Core.Errors;

namespace Xrmd.Metadata.Computed;

/// <summary>
/// The recurrence of the Calculate Rollup Field job of a table (plan 2.12b, rulings R8, R9): its <c>asyncoperation.recurrencepattern</c>
/// is <c>FREQ=HOURLY|DAILY|WEEKLY;INTERVAL=n</c> (an iCalendar-style rule; <c>INTERVAL</c> defaults to 1; spaces and one trailing
/// <c>;</c> are accepted), never shorter than an hour nor longer than 365 days.
/// Lives in the metadata layer because both the request rules (<c>Xrmd.Security</c>) and the jobs (<c>Xrmd.Pipeline</c>) read it.
/// </summary>
public static class RollupRecurrence
{
    /// <summary>The pattern of a new Calculate Rollup Field job: every hour.</summary>
    public const string Default = "FREQ=HOURLY;INTERVAL=1";

    private static readonly TimeSpan Minimum = TimeSpan.FromHours(1);

    /// <summary>The longest recurrence taken: 365 days (WEEKLY;INTERVAL=52 is 364 days).</summary>
    private static readonly TimeSpan Maximum = TimeSpan.FromDays(365);

    /// <summary>The interval of <paramref name="pattern"/>.</summary>
    /// <exception cref="XrmdFaultException">400 <c>0x8004D2A2</c> for an interval below one hour; 400 InvalidArgument for any other
    /// pattern than <c>FREQ=HOURLY|DAILY|WEEKLY[;INTERVAL=n]</c> with n a whole number from 1, or a period above 365 days.</exception>
    public static TimeSpan Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var parts = pattern.Split(';').Select(p => p.Trim()).ToList();
        // One trailing ';' is accepted (Learn writes "FREQ=DAILY;INTERVAL=3;"); an empty part anywhere else is not.
        if (parts.Count > 1 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        if (parts.Count is < 1 or > 2) throw Refused(pattern);
        var frequency = Value(parts[0], "FREQ") ?? throw Refused(pattern);
        long interval = 1;
        if (parts.Count == 2
            && (Value(parts[1], "INTERVAL") is not { } text
                || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1))
            throw Refused(pattern);

        TimeSpan unit;
        switch (frequency.ToUpperInvariant())
        {
            case "SECONDLY":
                unit = TimeSpan.FromSeconds(1);
                break;
            case "MINUTELY":
                unit = TimeSpan.FromMinutes(1);
                break;
            case "HOURLY":
                return Within(pattern, TimeSpan.FromHours(1), interval);
            case "DAILY":
                return Within(pattern, TimeSpan.FromDays(1), interval);
            case "WEEKLY":
                return Within(pattern, TimeSpan.FromDays(7), interval);
            default:
                throw Refused(pattern);
        }
        // A rule in seconds or minutes: refused as too frequent when it is, else as a shape xrmverse does not take.
        if (interval < Minimum.Ticks / unit.Ticks)
            throw new XrmdFaultException(ErrorCodes.InvalidRecurrenceIntervalForRollupJobs,
                $"The recurrence '{pattern}' of a rollup calculation job is shorter than one hour.", 400);
        throw Refused(pattern);
    }

    /// <summary>
    /// The first occurrence of <paramref name="pattern"/> counted from <paramref name="startUtc"/> that is after <paramref name="afterUtc"/>
    /// (<paramref name="startUtc"/> itself when it is later); the calendar's last instant when that occurrence would be past it.
    /// </summary>
    public static DateTime Next(string pattern, DateTime startUtc, DateTime afterUtc)
    {
        var interval = Parse(pattern);
        if (startUtc > afterUtc) return startUtc;
        var steps = (afterUtc - startUtc).Ticks / interval.Ticks + 1;
        // interval is at most a year and steps * interval at most one interval past afterUtc: no long overflow, only past MaxValue.
        var ticks = startUtc.Ticks + steps * interval.Ticks;
        return ticks > DateTime.MaxValue.Ticks || ticks < 0
            ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
            : new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary><paramref name="interval"/> times <paramref name="unit"/>, refused above <see cref="Maximum"/>.</summary>
    private static TimeSpan Within(string pattern, TimeSpan unit, long interval)
    {
        if (interval > Maximum.Ticks / unit.Ticks)
            throw XrmdFaultException.InvalidArgument(
                $"The recurrence '{pattern}' of a rollup calculation job is longer than a year: a rollup is recalculated at least once a year.");
        return TimeSpan.FromTicks(unit.Ticks * interval);
    }

    /// <summary>The value of <c>NAME=value</c> when <paramref name="part"/> has that name (ignoring case); null otherwise.</summary>
    private static string? Value(string part, string name)
    {
        var equals = part.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0 || !string.Equals(part[..equals], name, StringComparison.OrdinalIgnoreCase)) return null;
        var value = part[(equals + 1)..];
        return value.Length == 0 ? null : value;
    }

    private static XrmdFaultException Refused(string pattern) =>
        XrmdFaultException.InvalidArgument(
            $"The recurrence '{pattern}' of a rollup calculation job is not supported: use FREQ=HOURLY, FREQ=DAILY or FREQ=WEEKLY, " +
            "optionally followed by ;INTERVAL=n with n a whole number from 1.");
}
