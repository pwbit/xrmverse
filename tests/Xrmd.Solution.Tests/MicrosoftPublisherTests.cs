using FluentAssertions;
using Xrmd.Solution;

namespace Xrmd.Solution.Tests;

public sealed class MicrosoftPublisherTests
{
    [Theory]
    [InlineData("anything", "msdyn")]
    [InlineData("anything", "MSDYNCE")]
    [InlineData("anything", "msdynmkt")]
    [InlineData("anything", "mspp")]
    [InlineData("anything", "Adx")]
    [InlineData("MicrosoftDynamics", "xv")]
    [InlineData("microsoft", "xv")]
    [InlineData("Dynamics365", "xv")]
    [InlineData("dynamics365", "")]
    public void Microsoft_publisher_is_detected(string uniqueName, string prefix) =>
        MicrosoftPublisher.Matches(uniqueName, prefix).Should().BeTrue();

    [Theory]
    [InlineData("xrmverse", "xv")]
    [InlineData("contoso", "new")]
    [InlineData("contoso", "msdyn2")]
    [InlineData("contoso", "adxx")]
    [InlineData("acme-microsoft", "xv")]
    [InlineData("dynamics365partner", "xv")]
    [InlineData("", "")]
    public void Other_publishers_are_not_detected(string uniqueName, string prefix) =>
        MicrosoftPublisher.Matches(uniqueName, prefix).Should().BeFalse();
}
