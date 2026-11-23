using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.Abstractions.Sdk;

namespace Xrmd.Core.Tests.Sdk;

/// <summary>S.2a: the <c>Sdk:Mode</c> setting, its refusal texts and the error code of a refused feature.</summary>
public class SdkModeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("standard")]
    [InlineData("STANDARD")]
    public void TryParse_standard_or_absent(string? value)
    {
        SdkModes.TryParse(value, out var mode).Should().BeTrue();
        mode.Should().Be(SdkMode.Standard);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    public void TryParse_none(string value)
    {
        SdkModes.TryParse(value, out var mode).Should().BeTrue();
        mode.Should().Be(SdkMode.None);
    }

    [Fact]
    public void TryParse_rejects_unknown_value() => SdkModes.TryParse("off", out _).Should().BeFalse();

    [Fact]
    public void ToSetting_gives_the_lowercase_value()
    {
        SdkModes.ToSetting(SdkMode.None).Should().Be("none");
        SdkModes.ToSetting(SdkMode.Standard).Should().Be("standard");
    }

    [Fact]
    public void InvalidMessage_names_the_value() =>
        SdkModes.InvalidMessage("off").Should().Be("Sdk:Mode 'off' is not valid: use 'standard' or 'none'.");

    [Fact]
    public void AssemblyNames_are_the_four_in_order() =>
        SdkModes.AssemblyNames.Should().Equal(
            "Microsoft.Xrm.Sdk", "Microsoft.Crm.Sdk.Proxy", "Microsoft.PowerPlatform.Dataverse.Client", "Microsoft.Xrm.Sdk.Workflow");

    [Fact]
    public void SdkModeInfo_reports_sdk_free()
    {
        new SdkModeInfo(SdkMode.None).IsSdkFree.Should().BeTrue();
        new SdkModeInfo(SdkMode.Standard).IsSdkFree.Should().BeFalse();
    }

    [Fact]
    public void Refusals_carry_the_docs_link()
    {
        const string link = "https://xrmverse.com/docs/microsoft-sdk#sdk-free-servers";
        SdkFreeMessages.Soap.Should().Contain(link);
        SdkFreeMessages.PluginAssembly.Should().Contain(link);
        SdkFreeMessages.Migration.Should().Contain(link);
    }

    [Fact]
    public void FeatureNotAvailable_code() =>
        ErrorCodes.FeatureNotAvailable.Should().Be(unchecked((int)0x80090900));
}
