using FluentAssertions;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;

namespace Xrmd.Audit.Tests;

/// <summary>The row values the writer derives without a database: the time to live and the action mark of a converted request.</summary>
public sealed class AuditWriterTests
{
    [Theory]
    [InlineData(-1, -1)]
    [InlineData(1, 86_400)]
    [InlineData(30, 2_592_000)]
    [InlineData(24_855, 2_147_472_000)]
    [InlineData(365_000, int.MaxValue)] // the integer column holds about 68 years at most
    public void Time_to_live_is_the_retention_in_seconds(int days, int seconds) =>
        AuditWriter.TimeToLive(days).Should().Be(seconds);

    [Fact]
    public void Action_hint_marks_the_request_without_touching_its_parameters()
    {
        var request = new UpdateRequest();
        var before = request.Parameters.Keys.ToList();

        AuditActionHint.Of(request).Should().BeNull();
        AuditActionHint.Mark(request, AuditActions.SetState);

        AuditActionHint.Of(request).Should().Be(41);
        AuditActionHint.Of(new UpdateRequest()).Should().BeNull("the mark belongs to one request");
        request.Parameters.Keys.Should().Equal(before, "plug-ins see the same InputParameters (ruling A6)");
    }
}
