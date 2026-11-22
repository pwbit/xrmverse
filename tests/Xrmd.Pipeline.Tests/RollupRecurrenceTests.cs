using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;

namespace Xrmd.Pipeline.Tests;

/// <summary>
/// The recurrence of the Calculate Rollup Field job (plan 2.12b, Task 4, ruling R9): <c>FREQ=HOURLY|DAILY|WEEKLY;INTERVAL=n</c>,
/// nothing below one hour (<c>0x8004D2A2</c>), any other shape refused (400).
/// </summary>
public class RollupRecurrenceTests
{
    [Theory]
    [InlineData("FREQ=HOURLY;INTERVAL=1", 1)]
    [InlineData("FREQ=HOURLY;INTERVAL=12", 12)]
    [InlineData("FREQ=DAILY;INTERVAL=1", 24)]
    [InlineData("FREQ=DAILY;INTERVAL=3", 72)]
    [InlineData("FREQ=WEEKLY;INTERVAL=2", 336)]
    [InlineData("FREQ=DAILY", 24)]
    [InlineData("freq=hourly;interval=2", 2)]
    [InlineData("FREQ=HOURLY;", 1)]
    [InlineData("FREQ=DAILY;INTERVAL=3;", 72)]
    [InlineData(" FREQ=HOURLY ; INTERVAL=2 ", 2)]
    [InlineData("FREQ=WEEKLY;INTERVAL=52", 8736)]
    [InlineData("FREQ=DAILY;INTERVAL=365", 8760)]
    [InlineData("FREQ=HOURLY;INTERVAL=8760", 8760)]
    public void Hourly_daily_weekly_parse(string pattern, int hours)
    {
        RollupRecurrence.Parse(pattern).Should().Be(TimeSpan.FromHours(hours));
    }

    [Fact]
    public void Default_is_hourly()
    {
        RollupRecurrence.Default.Should().Be("FREQ=HOURLY;INTERVAL=1");
        RollupRecurrence.Parse(RollupRecurrence.Default).Should().Be(TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData("FREQ=MINUTELY;INTERVAL=30")]
    [InlineData("FREQ=MINUTELY;INTERVAL=59")]
    [InlineData("FREQ=SECONDLY;INTERVAL=600")]
    public void Below_one_hour_is_0x8004D2A2(string pattern)
    {
        var act = () => RollupRecurrence.Parse(pattern);
        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidRecurrenceIntervalForRollupJobs);
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(pattern);
    }

    [Theory]
    [InlineData("")]
    [InlineData("FREQ=MONTHLY;INTERVAL=1")]
    [InlineData("FREQ=YEARLY")]
    [InlineData("FREQ=MINUTELY;INTERVAL=120")]
    [InlineData("FREQ=HOURLY;INTERVAL=0")]
    [InlineData("FREQ=HOURLY;INTERVAL=-1")]
    [InlineData("FREQ=HOURLY;INTERVAL=x")]
    [InlineData("FREQ=HOURLY;INTERVAL=1;COUNT=3")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO")]
    [InlineData("INTERVAL=1;FREQ=HOURLY")]
    [InlineData("FREQ=HOURLY;;INTERVAL=1")]
    [InlineData("hourly")]
    [InlineData("FREQ=HOURLY;;")]
    [InlineData(";FREQ=HOURLY")]
    [InlineData(";")]
    [InlineData("FREQ=HOURLY;INTERVAL=2147483647")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2000000")]
    [InlineData("FREQ=DAILY;INTERVAL=3000000")]
    [InlineData("FREQ=DAILY;INTERVAL=366")]
    [InlineData("FREQ=WEEKLY;INTERVAL=53")]
    [InlineData("FREQ=HOURLY;INTERVAL=99999999999")]
    [InlineData("FREQ=MINUTELY;INTERVAL=2147483647")]
    public void Other_shapes_are_refused(string pattern)
    {
        var act = () => RollupRecurrence.Parse(pattern);
        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.HttpStatus.Should().Be(400);
    }

    [Fact]
    public void Next_occurrence_follows_the_start_time()
    {
        var start = new DateTime(2026, 11, 17, 8, 15, 0, DateTimeKind.Utc);
        RollupRecurrence.Next("FREQ=HOURLY;INTERVAL=1", start, start).Should().Be(start.AddHours(1));
        RollupRecurrence.Next("FREQ=HOURLY;INTERVAL=1", start, start.AddMinutes(130)).Should().Be(start.AddHours(3));
        RollupRecurrence.Next("FREQ=DAILY;INTERVAL=1", start, start.AddHours(3)).Should().Be(start.AddDays(1));
        // Far from the start: no overflow.
        var origin = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        RollupRecurrence.Next("FREQ=WEEKLY;INTERVAL=52", origin, new DateTime(9999, 12, 1, 0, 0, 0, DateTimeKind.Utc))
            .Should().Be(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), "an occurrence past the calendar's end is its last instant");
        // A start time in the future is the next occurrence.
        RollupRecurrence.Next("FREQ=HOURLY;INTERVAL=1", start, start.AddHours(-5)).Should().Be(start);
    }
}
