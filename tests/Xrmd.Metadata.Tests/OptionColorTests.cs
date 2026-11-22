using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>The background colour of a choice option: <c>#RRGGBB</c> or none.</summary>
public class OptionColorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#0a1B2c")]
    [InlineData("#D13438")]
    public void A_colour_or_none_is_valid(string? color) => OptionColor.IsValid(color).Should().BeTrue();

    [Theory]
    [InlineData("red")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("0A1B2C")]
    [InlineData("#0A1B2C ")]
    [InlineData("#0A1B2C\n")]
    public void Anything_else_is_invalid(string color) => OptionColor.IsValid(color).Should().BeFalse();

    [Fact]
    public void Demand_names_the_owner_the_value_and_the_colour()
    {
        var option = new OptionMetadata(new Label("Red", 1033), 100000001) { Color = "red" };

        var act = () => OptionColor.Demand("Global option set 'xrmv_priority'", option);

        act.Should().Throw<XrmdValidationException>().WithMessage(
            "Global option set 'xrmv_priority': option 100000001 has colour 'red', which is not #RRGGBB.");
    }

    [Fact]
    public void Demand_accepts_an_option_without_colour()
    {
        var act = () => OptionColor.Demand("owner", new OptionMetadata(new Label("Red", 1033), 1));

        act.Should().NotThrow();
    }
}
