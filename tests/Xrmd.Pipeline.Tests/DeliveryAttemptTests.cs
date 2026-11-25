using System.Net;
using FluentAssertions;
using Xrmd.Pipeline.Webhooks;

namespace Xrmd.Pipeline.Tests;

public class DeliveryAttemptTests
{
    [Theory]
    [InlineData(200, DeliveryOutcome.Delivered)]
    [InlineData(204, DeliveryOutcome.Delivered)]
    [InlineData(404, DeliveryOutcome.Gone)]
    [InlineData(410, DeliveryOutcome.Gone)]
    [InlineData(400, DeliveryOutcome.Final)]
    [InlineData(302, DeliveryOutcome.Final)]
    [InlineData(500, DeliveryOutcome.Retryable)]
    [InlineData(503, DeliveryOutcome.Retryable)]
    public void Classifies_a_status(int status, DeliveryOutcome expected) =>
        DeliveryAttempt.Classify((HttpStatusCode)status, false).Should().Be(expected);

    [Fact]
    public void A_transport_error_is_retryable() =>
        DeliveryAttempt.Classify(null, true).Should().Be(DeliveryOutcome.Retryable);

    [Theory]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(500, false)]
    [InlineData(200, false)]
    public void RetryAtOnce(int status, bool expected) => DeliveryAttempt.RetryAtOnce((HttpStatusCode)status).Should().Be(expected);

    [Fact]
    public void No_status_is_not_retried_at_once() => DeliveryAttempt.RetryAtOnce(null).Should().BeFalse();
}
