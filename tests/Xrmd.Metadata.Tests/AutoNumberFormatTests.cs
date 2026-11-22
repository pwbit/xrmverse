using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

public class AutoNumberFormatTests
{
    private static readonly DateTime At = new(2026, 11, 10, 7, 2, 40, DateTimeKind.Utc);

    [Theory]
    [InlineData("KA-{SEQNUM:4}", 1, "KA-0001")]
    [InlineData("KA-{SEQNUM:4}", 1000, "KA-1000")]
    [InlineData("KA-{SEQNUM:2}", 100, "KA-100")] // grows past the minimum length
    [InlineData("{SEQNUM:10}", 1234567890, "1234567890")]
    [InlineData("CAS-{SEQNUM:6}-{DATETIMEUTC:yyyyMMddHHmmss}", 2000, "CAS-002000-20261110070240")]
    [InlineData("A{DATETIMEUTC:HH:mm}B", 1, "A07:02B")]
    [InlineData("plain text", 1, "plain text")]
    public void Render_fills_the_sequence_and_the_date(string format, long sequence, string expected) =>
        AutoNumberFormat.Parse(format).Render(sequence, At).Should().Be(expected);

    [Fact]
    public void Random_strings_have_the_asked_length_and_alphabet()
    {
        var value = AutoNumberFormat.Parse("QUO-{SEQNUM:3}#{RANDSTRING:3}#{RANDSTRING:6}").Render(7, At);
        value.Should().MatchRegex("^QUO-007#[A-Z0-9]{3}#[A-Z0-9]{6}$");
    }

    [Theory]
    [InlineData("KA-{SEQNUM:4}", true)]
    [InlineData("{RANDSTRING:4}", false)]
    public void UsesSequence(string format, bool expected) => AutoNumberFormat.Parse(format).UsesSequence.Should().Be(expected);

    [Theory]
    [InlineData("X-{RANDSTRING:7}", "from 1 to 6")]
    [InlineData("X-{RANDSTRING:0}", "from 1 to 6")]
    [InlineData("X-{SEQNUM:0}", "at least 1")]
    [InlineData("X-{SEQNUM:abc}", "at least 1")]
    [InlineData("X-{SEQNUM:3}-{SEQNUM:4}", "more than one {SEQNUM}")]
    [InlineData("X-{FOO:1}", "'{FOO:1}' is not a placeholder")]
    [InlineData("X-{SEQNUM}", "'{SEQNUM}' is not a placeholder")]
    [InlineData("X-{SEQNUM:3", "not closed")]
    [InlineData("X-{DATETIMEUTC:}", "needs a date and time format")]
    public void Parse_refuses_a_bad_format(string format, string why)
    {
        var act = () => AutoNumberFormat.Parse(format);
        act.Should().Throw<XrmdValidationException>().WithMessage($"*'{format}'*{why}*");
    }

    [Fact]
    public void OfColumn_accepts_a_text_string_column_and_treats_empty_as_none()
    {
        AutoNumberFormat.OfColumn("pb_x", new StringAttributeMetadata { LogicalName = "pb_n", AutoNumberFormat = "N-{SEQNUM:3}" })
            .Should().Be("N-{SEQNUM:3}");
        AutoNumberFormat.OfColumn("pb_x", new StringAttributeMetadata { LogicalName = "pb_n", Format = StringFormat.Text, AutoNumberFormat = "N" })
            .Should().Be("N");
        AutoNumberFormat.OfColumn("pb_x", new StringAttributeMetadata { LogicalName = "pb_n", AutoNumberFormat = "" }).Should().BeNull();
        AutoNumberFormat.OfColumn("pb_x", new StringAttributeMetadata { LogicalName = "pb_n" }).Should().BeNull();
    }

    [Fact]
    public void OfColumn_refuses_another_format_or_type()
    {
        var email = () => AutoNumberFormat.OfColumn("pb_x", new StringAttributeMetadata { LogicalName = "pb_n", Format = StringFormat.Email, AutoNumberFormat = "N" });
        email.Should().Throw<XrmdValidationException>().WithMessage("*'pb_x.pb_n'*format is Text*");
        var memo = () => AutoNumberFormat.OfColumn("pb_x", new MemoAttributeMetadata { LogicalName = "pb_n", AutoNumberFormat = "N" });
        memo.Should().Throw<XrmdValidationException>().WithMessage("*only on a string column.");
        // Plan 2.12a: a calculated column's value is computed, never numbered.
        var calculated = () => AutoNumberFormat.OfColumn("pb_x", new StringAttributeMetadata { LogicalName = "pb_n", SourceType = 1, AutoNumberFormat = "N" });
        calculated.Should().Throw<XrmdValidationException>().WithMessage("*'pb_x.pb_n'*calculated column.");
        // Plan 2.12c: nor a formula column's.
        var formula = () => AutoNumberFormat.OfColumn("pb_x", new StringAttributeMetadata { LogicalName = "pb_n", SourceType = 3, AutoNumberFormat = "N" });
        formula.Should().Throw<XrmdValidationException>().WithMessage("*'pb_x.pb_n'*formula column.");
    }
}
