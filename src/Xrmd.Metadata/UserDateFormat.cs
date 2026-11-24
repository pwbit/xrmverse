using System.Globalization;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The date and time formats and the time zone of a user (<c>usersettings</c>, plan "personal date format", ruling F4): the
/// formatted value of a date and time column is written with them. Patterns are .NET custom patterns, as Dataverse stores them:
/// <c>/</c> stands for <see cref="DateSeparator"/>, <c>:</c> for <see cref="TimeSeparator"/> and <c>tt</c> for the designators.
/// </summary>
public sealed partial record UserDateFormat(
    int TimeZoneCode, string DateFormat, string TimeFormat, string DateSeparator, string TimeSeparator, string AmDesignator, string PmDesignator)
{
    /// <summary>The settings of a new user (ruling Z2): UTC and the en-US formats, which is also what internal callers get.</summary>
    public static UserDateFormat Default { get; } = new(TimeZoneCatalog.Utc, "M/d/yyyy", "h:mm tt", "/", ":", "AM", "PM");

    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// The settings of a <c>usersettings</c> row: each unset (null or empty) column takes <see cref="Default"/>'s; a time zone
    /// code that is not one keeps UTC.
    /// </summary>
    public static UserDateFormat From(int? timeZoneCode, string? dateFormat, string? timeFormat, string? dateSeparator, string? timeSeparator,
        string? am, string? pm)
    {
        var d = Default;
        static string Or(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;
        return new(timeZoneCode is { } code && TimeZoneCatalog.Contains(code) ? code : d.TimeZoneCode,
            Or(dateFormat, d.DateFormat), Or(timeFormat, d.TimeFormat), Or(dateSeparator, d.DateSeparator), Or(timeSeparator, d.TimeSeparator),
            Or(am, d.AmDesignator), Or(pm, d.PmDesignator));
    }

    /// <summary>
    /// The formatted value of <paramref name="value"/> in column <paramref name="a"/>: a User Local value is converted from UTC
    /// to <see cref="TimeZoneCode"/> first (Date only and Time zone independent values are shown as stored); a Date only format
    /// shows the date pattern, any other the date and the time pattern. A pattern .NET cannot apply falls back to the default one.
    /// </summary>
    public string Format(DateTimeColumn a, DateTime value)
    {
        ArgumentNullException.ThrowIfNull(a);
        var behavior = a.DateTimeBehavior?.Value;
        var shown = behavior is null || behavior == DateTimeBehaviors.UserLocal ? Local(value) : value;
        var dateOnly = a.Format == DateTimeDisplayFormat.DateOnly || behavior == DateTimeBehaviors.DateOnly;
        var info = (DateTimeFormatInfo)EnUs.DateTimeFormat.Clone();
        info.DateSeparator = DateSeparator;
        info.TimeSeparator = TimeSeparator;
        info.AMDesignator = AmDesignator;
        info.PMDesignator = PmDesignator;
        var pattern = dateOnly ? DateFormat : DateFormat + " " + TimeFormat;
        try
        {
            return shown.ToString(pattern, info);
        }
        catch (FormatException)
        {
            return shown.ToString(dateOnly ? Default.DateFormat : Default.DateFormat + " " + Default.TimeFormat, info);
        }
    }

    private DateTime Local(DateTime utc)
    {
        if (TimeZoneCode == TimeZoneCatalog.Utc) return utc;
        try
        {
            return TimeZoneCatalog.LocalFromUtc(TimeZoneCode, utc);
        }
        catch (Xrmd.Core.Errors.XrmdFaultException)
        {
            return utc; // a zone the host's tz database lacks: shown in UTC, as the defaults
        }
    }
}
