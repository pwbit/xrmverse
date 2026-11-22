using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>Plan "personal date format", rulings F2 and F4: the formatted value of a date and time column in a user's settings.</summary>
public class UserDateFormatTests
{
    private const int WEurope = 110;
    private static readonly DateTime Utc = new(2026, 11, 20, 14, 5, 0, DateTimeKind.Utc);

    private static DateTimeAttributeMetadata Column(DateTimeFormat format, DateTimeBehavior? behavior) =>
        new() { LogicalName = "x", Format = format, DateTimeBehavior = behavior };

    private static readonly UserDateFormat Italian = UserDateFormat.From(WEurope, "dd/MM/yyyy", "HH:mm", ".", ":", "AM", "PM");

    [Fact]
    public void The_defaults_are_en_US_in_UTC()
    {
        UserDateFormat.Default.Format(Column(DateTimeFormat.DateAndTime, DateTimeBehavior.UserLocal), Utc).Should().Be("11/20/2026 2:05 PM");
        UserDateFormat.Default.Format(Column(DateTimeFormat.DateOnly, DateTimeBehavior.DateOnly), Utc.Date).Should().Be("11/20/2026");
    }

    [Fact]
    public void A_user_local_value_is_shown_in_the_users_zone_and_format()
    {
        Italian.Format(Column(DateTimeFormat.DateAndTime, DateTimeBehavior.UserLocal), Utc).Should().Be("20.11.2026 15:05");
        Italian.Format(Column(DateTimeFormat.DateAndTime, null), Utc).Should().Be("20.11.2026 15:05");
        // A User Local column shown as Date only is converted too: 23:30 UTC is the next day in Berlin.
        Italian.Format(Column(DateTimeFormat.DateOnly, DateTimeBehavior.UserLocal), new DateTime(2026, 11, 20, 23, 30, 0, DateTimeKind.Utc))
            .Should().Be("21.11.2026");
    }

    [Fact]
    public void Time_zone_independent_and_date_only_values_are_not_converted()
    {
        Italian.Format(Column(DateTimeFormat.DateAndTime, DateTimeBehavior.TimeZoneIndependent), Utc).Should().Be("20.11.2026 14:05");
        Italian.Format(Column(DateTimeFormat.DateOnly, DateTimeBehavior.DateOnly), new DateTime(2026, 11, 20)).Should().Be("20.11.2026");
    }

    [Fact]
    public void Designators_and_separators_come_from_the_settings()
    {
        var f = UserDateFormat.From(TimeZoneCatalog.Utc, "yyyy/MM/dd", "hh:mm tt", "-", ".", "am", "pm");
        f.Format(Column(DateTimeFormat.DateAndTime, DateTimeBehavior.UserLocal), Utc).Should().Be("2026-11-20 02.05 pm");
    }

    [Fact]
    public void Unset_columns_and_unknown_zones_take_the_defaults()
    {
        UserDateFormat.From(null, null, "", null, null, null, null).Should().Be(UserDateFormat.Default);
        UserDateFormat.From(-7, null, null, null, null, null, null).TimeZoneCode.Should().Be(TimeZoneCatalog.Utc);
    }

    [Fact]
    public void A_pattern_dotnet_cannot_apply_falls_back_to_the_default()
    {
        var f = UserDateFormat.From(TimeZoneCatalog.Utc, "%", "h:mm tt", "/", ":", "AM", "PM");
        f.Format(Column(DateTimeFormat.DateOnly, DateTimeBehavior.DateOnly), new DateTime(2026, 11, 20)).Should().Be("11/20/2026");
    }
}
