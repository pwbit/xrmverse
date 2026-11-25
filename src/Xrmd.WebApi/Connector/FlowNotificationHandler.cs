using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Webhooks;

namespace Xrmd.WebApi.Connector;

/// <summary>
/// X.12b: runs the flow notification jobs (<see cref="AsyncOperationQueue.FlowNotification"/>) <see cref="FlowSubscriptionHook"/>
/// queues, data <c>{ "subscriptionId": "&lt;guid&gt;", "attempt": n, "body": {…} }</c>: posts <c>body</c>, as it is in the data, to the
/// subscription's callback (<see cref="FlowCallbackSender"/>).
/// </summary>
/// <remarks>
/// A subscription that no longer exists completes the job (<see cref="SubscriptionGoneMessage"/>); one whose row is there without its
/// callback is ended and completes the job (<see cref="NoCallbackMessage"/>). The stored callback is checked again
/// with <see cref="CallbackUrlPolicy.Problem"/> (the allowlist may have been narrowed since): a refused one fails the job with the
/// policy's message. Delivered: the job is deleted. 404 or 410: the subscription is ended (<see cref="FlowSubscriptions.EndAsync"/>) and the
/// job completes. Retryable (any 5xx, a timeout, a connection error) while <c>attempt</c> &lt;= <see cref="PipelineOptions.AsyncMaxRetries"/>:
/// the job is suspended until now + <see cref="PipelineOptions.AsyncRetryDelay"/> * 2^min(attempt-1, 20) with <c>attempt</c> + 1; otherwise it
/// fails with the reason. The callback URL never reaches a log line or a job message.
/// </remarks>
public sealed class FlowNotificationHandler(IDbConnectionFactory factory, FlowSubscriptions subscriptions, FlowCallbackSender sender,
    IOptions<PipelineOptions> pipeline, IOptions<ConnectorOptions> connector, ILogger<FlowNotificationHandler> logger) : IAsyncOperationHandler
{
    /// <summary>The <c>message</c> of a job whose subscription was ended before it ran.</summary>
    public const string SubscriptionGoneMessage = "The flow subscription no longer exists.";

    /// <summary>The <c>message</c> of a job whose subscription had lost its callback: the subscription is ended.</summary>
    public const string NoCallbackMessage = "The flow subscription had no callback and was ended.";

    /// <inheritdoc />
    public int OperationType => AsyncOperationQueue.FlowNotification;

    /// <inheritdoc />
    /// <exception cref="XrmdFaultException">The data is not a flow notification; or the notification could not be delivered.</exception>
    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (subscriptionId, attempt, body) = Parse(run.Data);

        string? url;
        var exists = false;
        await using (var conn = await factory.OpenAsync(ct))
        {
            var s = new DataSession(conn, null);
            url = await subscriptions.CallbackAsync(s, subscriptionId, ct);
            if (url is null) exists = await subscriptions.ExistsAsync(s, subscriptionId, ct);
        }
        if (url is null)
        {
            if (exists)
            {
                // The row is there but its callback is not: the subscription cannot be delivered to again, so it is ended.
                logger.LogWarning("Flow subscription {SubscriptionId} has no callback: the subscription is ended.", subscriptionId);
                await subscriptions.EndAsync(subscriptionId, ct);
                run.CompletionMessage = NoCallbackMessage;
                return;
            }
            run.CompletionMessage = SubscriptionGoneMessage;
            return;
        }
        if (CallbackUrlPolicy.Problem(url, connector.Value) is { } problem)
            throw new XrmdFaultException(ErrorCodes.WebhooksPostRequestFailed, problem, 500);

        var result = await sender.DeliverAsync(new Uri(url, UriKind.Absolute), Encoding.UTF8.GetBytes(body), ct);
        switch (result.Outcome)
        {
            case DeliveryOutcome.Delivered:
                run.DeleteOnSuccess = true;
                return;
            case DeliveryOutcome.Gone:
                logger.LogInformation("The callback of flow subscription {SubscriptionId} answered {Status}: the subscription is ended.",
                    subscriptionId, result.Status);
                await subscriptions.EndAsync(subscriptionId, ct);
                run.CompletionMessage = $"The flow subscription ended: the callback answered {result.Status}.";
                return;
            case DeliveryOutcome.Retryable when attempt <= pipeline.Value.AsyncMaxRetries:
                // The exponent is clamped as the asynchronous worker clamps its own (a high attempt never overflows the delay).
                var delay = pipeline.Value.AsyncRetryDelay * Math.Pow(2, Math.Min(attempt - 1, 20));
                run.Suspend(run.Now + delay, Data(subscriptionId, attempt + 1, body));
                return;
            default:
                var code = result.Status is null ? ErrorCodes.WebhooksPostRequestFailed : ErrorCodes.WebhooksNonSuccessHttpResponse;
                throw new XrmdFaultException(code, result.Reason == FlowCallbackSender.AddressRefused
                    ? FlowCallbackSender.AddressRefused
                    : $"The flow notification could not be delivered: {result.Reason}.", 500);
        }
    }

    /// <summary>The subscription, the attempt (1 when absent) and the raw text of <c>body</c>.</summary>
    private static (Guid SubscriptionId, int Attempt, string Body) Parse(string? data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data ?? "");
            var root = doc.RootElement;
            if (root.TryGetProperty("subscriptionId", out var sub) && sub.ValueKind == JsonValueKind.String && sub.TryGetGuid(out var id)
                && root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
            {
                var attempt = root.TryGetProperty("attempt", out var a) && a.TryGetInt32(out var n) && n > 0 ? n : 1;
                return (id, attempt, body.GetRawText());
            }
        }
        catch (JsonException)
        {
        }
        throw new XrmdFaultException(ErrorCodes.InvalidArgument, "The system job's data is not a flow notification.", 400);
    }

    private static string Data(Guid subscriptionId, int attempt, string body) =>
        new JsonObject { ["subscriptionId"] = subscriptionId.ToString(), ["attempt"] = attempt, ["body"] = JsonNode.Parse(body) }.ToJsonString();
}
