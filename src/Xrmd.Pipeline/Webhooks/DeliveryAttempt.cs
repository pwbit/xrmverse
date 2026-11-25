using System.Net;

namespace Xrmd.Pipeline.Webhooks;

/// <summary>How one delivery attempt ended. <see cref="Gone"/> is a 404 or 410: the receiver no longer exists.</summary>
public enum DeliveryOutcome { Delivered, Retryable, Final, Gone }

/// <summary>The attempt rules shared by webhook steps and flow subscriptions.</summary>
public static class DeliveryAttempt
{
    /// <summary>2xx delivered; 404 and 410 gone; any other 3xx or 4xx final; 5xx and transport errors (no answer) retryable.</summary>
    public static DeliveryOutcome Classify(HttpStatusCode? status, bool transportError)
    {
        if (transportError || status is null) return DeliveryOutcome.Retryable;
        var code = (int)status.Value;
        if (code is >= 200 and < 300) return DeliveryOutcome.Delivered;
        if (code is 404 or 410) return DeliveryOutcome.Gone;
        return code >= 500 ? DeliveryOutcome.Retryable : DeliveryOutcome.Final;
    }

    /// <summary>True for 502, 503 and 504: one more attempt at once.</summary>
    public static bool RetryAtOnce(HttpStatusCode? status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
