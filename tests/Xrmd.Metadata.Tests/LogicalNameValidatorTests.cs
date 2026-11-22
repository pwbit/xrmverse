using FluentAssertions;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

public class LogicalNameValidatorTests
{
    [Theory]
    [InlineData("pb_project", true)]
    [InlineData("a", true)]
    [InlineData("PB_Project", false)]
    [InlineData("1abc", false)]
    [InlineData("a-b", false)]
    [InlineData("_abc", false)]
    [InlineData("", false)]
    [InlineData("abc\n", false)]
    [InlineData("a]; DROP TABLE x--", false)]
    public void IsValid(string name, bool expected) =>
        LogicalNameValidator.IsValid(name).Should().Be(expected);

    [Fact]
    public void IsValid_accepts_64_chars_and_rejects_65()
    {
        LogicalNameValidator.IsValid("a" + new string('b', 63)).Should().BeTrue();
        LogicalNameValidator.IsValid("a" + new string('b', 64)).Should().BeFalse();
    }

    [Fact]
    public void IsValid_rejects_null() => LogicalNameValidator.IsValid(null).Should().BeFalse();
}
