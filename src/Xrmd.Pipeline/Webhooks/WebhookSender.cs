using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.Sandbox;

namespace Xrmd.Pipeline.Webhooks;

/// <summary>
/// Delivers an execution context to a webhook endpoint (plan 3.2): <c>POST {url}</c> with the <see cref="WebhookPayload"/> body,
/// the <c>x-ms-*</c> headers and the endpoint's authentication; one more attempt at once on 502, 503 or 504; no redirect followed;
/// <see cref="PipelineOptions.WebhookTimeout"/> per attempt. A failure is a <see cref="PluginExecutionException"/> with the fault
/// 500 <see cref="ErrorCodes.WebhooksNonSuccessHttpResponse"/> (a non-2xx answer) or <see cref="ErrorCodes.WebhooksPostRequestFailed"/>
/// (a timeout or a connection error), retryable (<c>Unexpected</c> true) for any 5xx (502, 503 and 504
/// after the extra attempt), a timeout or a connection error, final otherwise (3xx, 4xx). Faults and log lines name the endpoint and <c>scheme://host/path</c> of its url
/// only, never its query, headers or auth value.
/// </summary>
public sealed class WebhookSender : IDisposable
{
    private readonly PipelineOptions _options;
    private readonly ILogger<WebhookSender> _logger;
    private readonly HttpClient _client;

    /// <summary>
    /// The refusal of a <see cref="PipelineOptions.WebhookTimeout"/> that is not positive or exceeds <see cref="int.MaxValue"/>
    /// milliseconds (the largest delay a cancellation timer takes).
    /// </summary>
    public const string TimeoutOutOfRange =
        "Pipeline:WebhookTimeout must be a positive time span of at most 24.20:31:23.647, such as 00:01:00.";

    /// <summary>True when <paramref name="timeout"/> is a valid <see cref="PipelineOptions.WebhookTimeout"/>.</summary>
    public static bool IsValidTimeout(TimeSpan timeout) => timeout > TimeSpan.Zero && timeout.TotalMilliseconds <= int.MaxValue;

    /// <summary>A sender with its own <see cref="SocketsHttpHandler"/> (no redirects, connections renewed every 5 minutes).</summary>
    public WebhookSender(PipelineOptions options, ILogger<WebhookSender> logger)
        : this(options, logger, new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <summary>A sender over <paramref name="handler"/> (tests).</summary>
    /// <exception cref="InvalidOperationException"><see cref="PipelineOptions.WebhookTimeout"/> is out of range (<see cref="IsValidTimeout"/>).</exception>
    internal WebhookSender(PipelineOptions options, ILogger<WebhookSender> logger, HttpMessageHandler handler)
    {
        if (!IsValidTimeout(options.WebhookTimeout))
            throw new InvalidOperationException(TimeoutOutOfRange);
        _options = options;
        _logger = logger;
        // The timeout is applied per attempt by SendAsync, so it can tell a timeout from the caller's cancellation.
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Posts <paramref name="context"/> to <paramref name="endpoint"/>; returns when the receiver answered 2xx.</summary>
    /// <param name="organizationName">The <c>x-ms-dynamics-organization</c> header.</param>
    /// <exception cref="PluginExecutionException">The delivery failed (see the class remarks).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task SendAsync(WebhookEndpoint endpoint, RemoteExecutionContext context, string organizationName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(context);
        var (body, trimmed) = WebhookPayload.Build(context);
        var (url, headers) = Authenticate(endpoint);
        var where = SafeUrl(endpoint.Url);
        if (trimmed)
            _logger.LogInformation("The context sent to webhook {Endpoint} ({Url}) exceeds {Limit} bytes; it is sent without its parameters and images.",
                endpoint.Name, where, WebhookPayload.SizeLimit);

        var attempt = await AttemptAsync(endpoint, url, headers, body, trimmed, context, organizationName, ct);
        if (attempt.Status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            _logger.LogInformation("Webhook {Endpoint} ({Url}) returned {Status}; trying once more.", endpoint.Name, where, (int)attempt.Status);
            attempt = await AttemptAsync(endpoint, url, headers, body, trimmed, context, organizationName, ct);
        }
        // Any 5xx (after the extra attempt for 502, 503 and 504) is retryable; 3xx and 4xx are final.
        if (attempt.Status is { } status && (int)status >= 500) attempt = attempt with { Retryable = true };
        if (attempt.Reason is null) return;

        _logger.LogWarning("Delivery to webhook {Endpoint} ({Url}) failed: {Reason} ({Kind}).", endpoint.Name, where, attempt.Reason,
            attempt.Retryable ? "retryable" : "final");
        // As in Dataverse: a non-2xx answer and a failed post are told apart by the code; the response body is never read.
        var code = attempt.Status is null ? ErrorCodes.WebhooksPostRequestFailed : ErrorCodes.WebhooksNonSuccessHttpResponse;
        var fault = new XrmdFaultException(code,
            $"Endpoint unavailable: webhook '{endpoint.Name}' {attempt.Reason}.", 500);
        throw new PluginExecutionException(fault, attempt.Retryable);
    }

    /// <summary>The outcome of one attempt: <see cref="Reason"/> null on 2xx; <see cref="Status"/> null when there was no answer.</summary>
    private sealed record Attempt(HttpStatusCode? Status, string? Reason, bool Retryable);

    private async Task<Attempt> AttemptAsync(WebhookEndpoint endpoint, Uri url, IReadOnlyList<KeyValuePair<string, string>> authHeaders,
        byte[] body, bool trimmed, RemoteExecutionContext context, string organizationName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("x-ms-dynamics-organization", organizationName);
        request.Headers.TryAddWithoutValidation("x-ms-dynamics-entity-name", context.PrimaryEntityName);
        request.Headers.TryAddWithoutValidation("x-ms-dynamics-request-name", context.MessageName);
        request.Headers.TryAddWithoutValidation("x-ms-correlation-request-id", context.CorrelationId.ToString());
        if (trimmed) request.Headers.TryAddWithoutValidation("x-ms-dynamics-msg-size-exceeded", "true");
        foreach (var (name, value) in authHeaders) request.Headers.TryAddWithoutValidation(name, value);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.WebhookTimeout);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode) return new Attempt(response.StatusCode, null, false);
            var phrase = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? "" : " " + Printable(response.ReasonPhrase);
            return new Attempt(response.StatusCode, $"returned {(int)response.StatusCode}{phrase}", false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var seconds = _options.WebhookTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            return new Attempt(null, $"did not answer within {seconds} s", true);
        }
        catch (HttpRequestException ex)
        {
            // The exception's message may quote the url with its query: only the kind of error is passed on.
            return new Attempt(null, $"could not be reached ({Describe(ex)})", true);
        }
    }

    /// <summary>The url with the endpoint's query authentication, and the headers of its header authentication.</summary>
    private static (Uri Url, IReadOnlyList<KeyValuePair<string, string>> Headers) Authenticate(WebhookEndpoint endpoint)
    {
        IReadOnlyList<KeyValuePair<string, string>> settings = [];
        switch (endpoint.AuthType)
        {
            case ServiceEndpointSchema.WebhookKey when !string.IsNullOrWhiteSpace(endpoint.AuthValue):
                return (WithQuery(endpoint.Url, [new("code", endpoint.AuthValue)]), []);
            case ServiceEndpointSchema.HttpHeader or ServiceEndpointSchema.HttpQueryString
                when endpoint.AuthValue is not null && ServiceEndpointRules.TryParseSettings(endpoint.AuthValue, out settings, out _)
                     && (endpoint.AuthType == ServiceEndpointSchema.HttpQueryString
                         || settings.All(h => ServiceEndpointRules.IsToken(h.Key) && ServiceEndpointRules.IsHeaderValue(h.Value))):
                return endpoint.AuthType == ServiceEndpointSchema.HttpHeader ? (endpoint.Url, settings) : (WithQuery(endpoint.Url, settings), []);
            default:
                // Unreachable for an endpoint accepted by ServiceEndpointRules; never sent without its authentication, and never
                // with a header the HTTP stack would refuse at send time (which would read as a retryable connection error).
                var fault = new XrmdFaultException(ErrorCodes.UnexpectedPluginException,
                    $"Endpoint unavailable: webhook '{endpoint.Name}' has no valid authvalue for authtype {endpoint.AuthType}.", 500);
                throw new PluginExecutionException(fault, unexpected: false);
        }
    }

    /// <summary><paramref name="url"/> with <paramref name="parameters"/> (escaped) after its own query parameters.</summary>
    private static Uri WithQuery(Uri url, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var query = new StringBuilder(url.Query.TrimStart('?'));
        foreach (var (name, value) in parameters)
        {
            if (query.Length > 0) query.Append('&');
            query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return new UriBuilder(url) { Query = query.ToString() }.Uri;
    }

    /// <summary><c>scheme://host[:port]/path</c> of <paramref name="url"/>: no user information, query or fragment.</summary>
    private static string SafeUrl(Uri url) => $"{url.Scheme}://{url.Authority}{url.AbsolutePath}";

    /// <summary>The kind of a connection error (e.g. <c>ConnectionError</c>, <c>SecureConnectionError</c>, <c>HostNotFound</c>).</summary>
    private static string Describe(HttpRequestException ex)
    {
        if (ex.HttpRequestError != HttpRequestError.Unknown) return ex.HttpRequestError.ToString();
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is SocketException socket) return socket.SocketErrorCode.ToString();
        return ex.InnerException?.GetType().Name ?? ex.GetType().Name;
    }

    private static string Printable(string text) => new(text.Where(c => !char.IsControl(c)).ToArray());

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
