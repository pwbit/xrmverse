using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Webhooks;

namespace Xrmd.WebApi.Connector;

/// <summary>How a delivery to a flow callback ended: the <see cref="Outcome"/>, the last answer's status (null: none) and why.</summary>
/// <param name="Reason">Null when delivered; else what happened, without the URL (e.g. <c>the callback answered 500</c>).</param>
public sealed record FlowCallbackResult(DeliveryOutcome Outcome, int? Status, string? Reason);

/// <summary>
/// X.12b: posts a flow notification to a subscription's callback URL (<c>POST</c>, <c>application/json; charset=utf-8</c>), with the
/// webhook attempt rules (<see cref="DeliveryAttempt"/>): one more attempt at once on 502, 503 or 504; no redirect followed;
/// <see cref="PipelineOptions.WebhookTimeout"/> per attempt. Every connection is opened by <see cref="ConnectAsync"/>, which resolves
/// the host and refuses private and loopback addresses (<see cref="CallbackUrlPolicy.IsPrivate"/>) unless
/// <see cref="ConnectorOptions.AllowPrivateCallbacks"/>; no proxy is used, so that check is made on the receiver's own address. The URL
/// carries a signature: it never reaches a log line, a reason or an exception message.
/// </summary>
public sealed class FlowCallbackSender : IDisposable
{
    /// <summary>The reason of a callback whose host resolves only to addresses that are not allowed.</summary>
    public const string AddressRefused = "The callback URL resolves to an address that is not allowed.";

    private readonly IOptions<PipelineOptions> _pipeline;
    private readonly IOptions<ConnectorOptions> _connector;
    private readonly ILogger<FlowCallbackSender> _logger;
    private readonly HttpClient _client;

    /// <summary>A sender with its own <see cref="SocketsHttpHandler"/>. The options are read at each send (they may change at run time).</summary>
    public FlowCallbackSender(IOptions<PipelineOptions> pipeline, IOptions<ConnectorOptions> connector, ILogger<FlowCallbackSender> logger)
    {
        _pipeline = pipeline;
        _connector = connector;
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectAsync,
        };
        // The timeout is applied per attempt, so a timeout is told from the caller's cancellation.
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Posts <paramref name="body"/> to <paramref name="callback"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<DeliveryOutcome> SendAsync(Uri callback, byte[] body, CancellationToken ct) =>
        (await DeliverAsync(callback, body, ct)).Outcome;

    /// <summary><see cref="SendAsync"/>, with the last status and the reason of a failure.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<FlowCallbackResult> DeliverAsync(Uri callback, byte[] body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(body);
        var attempt = await AttemptAsync(callback, body, ct);
        if (DeliveryAttempt.RetryAtOnce(attempt.Status))
        {
            _logger.LogInformation("A flow callback on {Host} answered {Status}; trying once more.", callback.Host, (int)attempt.Status!.Value);
            attempt = await AttemptAsync(callback, body, ct);
        }
        if (attempt.Refused) return new FlowCallbackResult(DeliveryOutcome.Final, null, AddressRefused);
        var outcome = DeliveryAttempt.Classify(attempt.Status, attempt.Status is null);
        if (outcome != DeliveryOutcome.Delivered)
            _logger.LogWarning("Delivery to a flow callback on {Host} failed: {Reason} ({Outcome}).", callback.Host, attempt.Reason, outcome);
        return new FlowCallbackResult(outcome, attempt.Status is { } s ? (int)s : null, outcome == DeliveryOutcome.Delivered ? null : attempt.Reason);
    }

    /// <summary>One attempt: <see cref="Status"/> null when there was no answer; <see cref="Refused"/> when the address was refused.</summary>
    private sealed record Attempt(HttpStatusCode? Status, string? Reason, bool Refused = false);

    private async Task<Attempt> AttemptAsync(Uri callback, byte[] body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, callback) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        var timeoutAfter = _pipeline.Value.WebhookTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutAfter);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return new Attempt(response.StatusCode, $"the callback answered {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var seconds = timeoutAfter.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            return new Attempt(null, $"the callback did not answer within {seconds} s");
        }
        catch (HttpRequestException ex) when (Find<AddressRefusedException>(ex) is not null)
        {
            return new Attempt(null, AddressRefused, Refused: true);
        }
        catch (HttpRequestException ex)
        {
            // The exception's message may quote the URL: only the kind of error is passed on.
            return new Attempt(null, $"the callback could not be reached ({Describe(ex)})");
        }
    }

    /// <summary>
    /// Opens the connection: resolves the host and connects to its addresses, leaving out the private ones unless
    /// <see cref="ConnectorOptions.AllowPrivateCallbacks"/>; none left is an <see cref="AddressRefusedException"/>.
    /// </summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        if (!_connector.Value.AllowPrivateCallbacks)
            addresses = addresses.Where(a => !CallbackUrlPolicy.IsPrivate(a)).ToArray();
        if (addresses.Length == 0) throw new AddressRefusedException();

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Thrown by <see cref="ConnectAsync"/>; carries no address or host.</summary>
    private sealed class AddressRefusedException() : IOException(AddressRefused);

    private static T? Find<T>(Exception ex) where T : Exception
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is T found) return found;
        return null;
    }

    /// <summary>The kind of a connection error (e.g. <c>ConnectionError</c>, <c>SecureConnectionError</c>, <c>NameResolutionError</c>).</summary>
    private static string Describe(HttpRequestException ex)
    {
        if (ex.HttpRequestError != HttpRequestError.Unknown) return ex.HttpRequestError.ToString();
        if (Find<SocketException>(ex) is { } socket) return socket.SocketErrorCode.ToString();
        return ex.InnerException?.GetType().Name ?? ex.GetType().Name;
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
