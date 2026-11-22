using FluentAssertions;

namespace Xrmd.Core.Tests;

/// <summary>Plan 2.13: the <c>DataToken</c> of RetrieveEntityChanges, <c>{version}!{yyyy-MM-dd HH:mm:ss}</c> (UTC, invariant).</summary>
public class ChangeTokenTests
{
    [Fact]
    public void Format_then_parse_round_trips()
    {
        var token = ChangeToken.Format(123456789012, new DateTime(2026, 11, 11, 8, 5, 9, DateTimeKind.Utc));

        token.Should().Be("123456789012!2026-11-11 08:05:09");
        ChangeToken.TryParse(token, out var version).Should().BeTrue();
        version.Should().Be(123456789012);
    }

    [Fact]
    public void Parse_reads_only_the_part_before_the_bang()
    {
        ChangeToken.TryParse("42!not a date at all", out var version).Should().BeTrue();
        version.Should().Be(42);
        ChangeToken.TryParse("0!", out var zero).Should().BeTrue();
        zero.Should().Be(0);
    }

    [Fact]
    public void Parse_with_time_reads_the_issue_time_as_utc()
    {
        ChangeToken.TryParse("42!2026-11-11 08:05:09", out var version, out var issued).Should().BeTrue();
        version.Should().Be(42);
        issued.Should().Be(new DateTime(2026, 11, 11, 8, 5, 9, DateTimeKind.Utc));
        issued!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("42!")]
    [InlineData("42!not a date at all")]
    [InlineData("42!2026-11-11T08:05:09")]
    [InlineData("42!2026-11-11 08:05:09!x")]
    public void Parse_with_time_gives_no_time_when_the_time_part_is_not_one(string token)
    {
        ChangeToken.TryParse(token, out var version, out var issued).Should().BeTrue();
        version.Should().Be(42);
        issued.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!2026-11-11 08:05:09")]
    [InlineData("abc!2026-11-11 08:05:09")]
    [InlineData("-5!2026-11-11 08:05:09")]
    [InlineData(" 5!2026-11-11 08:05:09")]
    [InlineData("1.5!2026-11-11 08:05:09")]
    [InlineData("99999999999999999999!2026-11-11 08:05:09")]
    public void Garbage_and_negative_versions_do_not_parse(string? token)
    {
        ChangeToken.TryParse(token, out _).Should().BeFalse();
    }
}
