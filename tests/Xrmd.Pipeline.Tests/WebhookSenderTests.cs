using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Pipeline.Webhooks;

namespace Xrmd.Pipeline.Tests;

public class WebhookSenderTests
{
    private const string Settings = "<settings><setting name=\"x-api-key\" value=\"k 1&amp;2\" /><setting name=\"tenant\" value=\"a/b\" /></settings>";

    private static WebhookEndpoint Endpoint(string url, int authType = 4, string? authValue = "k") =>
        new(Guid.NewGuid(), "Order hook", new Uri(url), authType, authValue);

    private static (WebhookSender Sender, FakeHandler Handler, ListLogger Logger) Create(Func<HttpRequestMessage, CancellationToken,
        Task<HttpResponseMessage>> respond, TimeSpan? timeout = null)
    {
        var handler = new FakeHandler(respond);
        var logger = new ListLogger();
        var options = new PipelineOptions();
        if (timeout is { } t) options.WebhookTimeout = t;
        return (new WebhookSender(options, logger, handler), handler, logger);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Statuses(params HttpStatusCode[] statuses)
    {
        var next = 0;
        return (_, _) => Task.FromResult(new HttpResponseMessage(statuses[Math.Min(next++, statuses.Length - 1)]));
    }

    private static RemoteExecutionContext Context() => WebhookPayloadTests.UpdateContext(out _);

    [Fact]
    public async Task Sends_headers_and_body()
    {
        var (sender, handler, _) = Create(Statuses(HttpStatusCode.OK));
        var context = Context();

        await sender.SendAsync(Endpoint("https://hooks.example.test/in"), context, "contoso", CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.ContentType.Should().Be("application/json; charset=utf-8");
        request.Headers["x-ms-dynamics-organization"].Should().Be("contoso");
        request.Headers["x-ms-dynamics-entity-name"].Should().Be("contact");
        request.Headers["x-ms-dynamics-request-name"].Should().Be("Update");
        request.Headers["x-ms-correlation-request-id"].Should().Be(context.CorrelationId.ToString());
        request.Headers.Should().NotContainKey("x-ms-dynamics-msg-size-exceeded");
        request.Body.Should().Equal(WebhookPayload.Serialize(context));
    }

    [Fact]
    public async Task Oversized_body_is_sent_trimmed_with_the_size_header()
    {
        var (sender, handler, _) = Create(Statuses(HttpStatusCode.OK));
        var context = WebhookPayloadTests.UpdateContext(out var target);
        target["description"] = new string('x', 300 * 1024);

        await sender.SendAsync(Endpoint("https://hooks.example.test/in"), context, "contoso", CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Headers["x-ms-dynamics-msg-size-exceeded"].Should().Be("true");
        request.Body.Should().Equal(WebhookPayload.Build(context).Body);
    }

    [Fact]
    public async Task Webhook_key_adds_code()
    {
        var (sender, handler, _) = Create(Statuses(HttpStatusCode.OK));

        await sender.SendAsync(Endpoint("https://hooks.example.test/in?a=1"), Context(), "contoso", CancellationToken.None);

        handler.Requests.Single().Uri.AbsoluteUri.Should().Be("https://hooks.example.test/in?a=1&code=k");
    }

    [Fact]
    public async Task Header_auth_adds_headers()
    {
        var (sender, handler, _) = Create(Statuses(HttpStatusCode.OK));

        await sender.SendAsync(Endpoint("https://hooks.example.test/in", 5, Settings), Context(), "contoso", CancellationToken.None);

        var request = handler.Requests.Single();
        request.Headers["x-api-key"].Should().Be("k 1&2");
        request.Headers["tenant"].Should().Be("a/b");
        request.Uri.AbsoluteUri.Should().Be("https://hooks.example.test/in");
    }

    [Fact]
    public async Task Query_auth_adds_parameters()
    {
        var (sender, handler, _) = Create(Statuses(HttpStatusCode.OK));

        await sender.SendAsync(Endpoint("https://hooks.example.test/in?a=1", 6, Settings), Context(), "contoso", CancellationToken.None);

        var request = handler.Requests.Single();
        request.Uri.AbsoluteUri.Should().Be("https://hooks.example.test/in?a=1&x-api-key=k%201%262&tenant=a%2Fb");
        request.Headers.Should().NotContainKey("x-api-key");
    }

    [Fact]
    public async Task Retries_once_on_503_then_succeeds()
    {
        var (sender, handler, _) = Create(Statuses(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK));

        await sender.SendAsync(Endpoint("https://hooks.example.test/in"), Context(), "contoso", CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Fails_after_second_502_as_retryable()
    {
        var (sender, handler, _) = Create(Statuses(HttpStatusCode.BadGateway));

        var act = () => sender.SendAsync(Endpoint("https://hooks.example.test/in"), Context(), "contoso", CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
        handler.Requests.Should().HaveCount(2);
        failure.Unexpected.Should().BeTrue();
        failure.Fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        failure.Fault.HttpStatus.Should().Be(500);
        failure.Message.Should().Be("Endpoint unavailable: webhook 'Order hook' returned 502 Bad Gateway.");
    }

    [Fact]
    public async Task Fails_on_400_as_final_without_retry()
    {
        var (sender, handler, _) = Create(Statuses(HttpStatusCode.BadRequest));

        var act = () => sender.SendAsync(Endpoint("https://hooks.example.test/in"), Context(), "contoso", CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
        handler.Requests.Should().ContainSingle();
        failure.Unexpected.Should().BeFalse();
        failure.Message.Should().Be("Endpoint unavailable: webhook 'Order hook' returned 400 Bad Request.");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "500 Internal Server Error")]
    [InlineData(HttpStatusCode.NotImplemented, "501 Not Implemented")]
    [InlineData(HttpStatusCode.HttpVersionNotSupported, "505 Http Version Not Supported")]
    public async Task Fails_on_500_as_retryable_without_extra_attempt(HttpStatusCode status, string returned)
    {
        var (sender, handler, _) = Create(Statuses(status));

        var act = () => sender.SendAsync(Endpoint("https://hooks.example.test/in"), Context(), "contoso", CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
        handler.Requests.Should().ContainSingle();
        failure.Unexpected.Should().BeTrue();
        failure.Message.Should().Be($"Endpoint unavailable: webhook 'Order hook' returned {returned}.");
    }

    [Fact]
    public async Task Second_attempt_answering_400_is_final()
    {
        var (sender, handler, _) = Create(Statuses(HttpStatusCode.GatewayTimeout, HttpStatusCode.BadRequest));

        var act = () => sender.SendAsync(Endpoint("https://hooks.example.test/in"), Context(), "contoso", CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
        handler.Requests.Should().HaveCount(2);
        failure.Unexpected.Should().BeFalse();
    }

    [Fact]
    public async Task Header_value_the_http_stack_refuses_is_final_and_not_sent()
    {
        var (sender, handler, _) = Create(Statuses(HttpStatusCode.OK));
        var endpoint = Endpoint("https://hooks.example.test/in", 5, "<settings><setting name=\"token\" value=\"caf\u00e9&#13;&#10;x\" /></settings>");

        var act = () => sender.SendAsync(endpoint, Context(), "contoso", CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
        handler.Requests.Should().BeEmpty();
        failure.Unexpected.Should().BeFalse();
        failure.Message.Should().Be("Endpoint unavailable: webhook 'Order hook' has no valid authvalue for authtype 5.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue + 1L)]
    public void Timeout_must_be_positive_and_at_most_int_max_milliseconds(long milliseconds)
    {
        var act = () => new WebhookSender(new PipelineOptions { WebhookTimeout = TimeSpan.FromMilliseconds(milliseconds) }, new ListLogger());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Pipeline:WebhookTimeout must be a positive time span of at most 24.20:31:23.647, such as 00:01:00.");
    }

    [Fact]
    public void Timeout_of_int_max_milliseconds_is_accepted()
    {
        WebhookSender.IsValidTimeout(TimeSpan.FromMilliseconds(int.MaxValue)).Should().BeTrue();
        WebhookSender.IsValidTimeout(TimeSpan.FromMilliseconds(int.MaxValue + 1L)).Should().BeFalse();
        using var sender = new WebhookSender(new PipelineOptions { WebhookTimeout = TimeSpan.FromMilliseconds(int.MaxValue) }, new ListLogger());
    }

    [Fact]
    public async Task Real_handler_does_not_follow_a_redirect()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var seen = 0;
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext request;
                try { request = await listener.GetContextAsync(); }
                catch (Exception) when (stop.IsCancellationRequested || !listener.IsListening) { return; }
                Interlocked.Increment(ref seen);
                request.Response.StatusCode = 302;
                request.Response.RedirectLocation = $"http://127.0.0.1:{port}/elsewhere";
                request.Response.Close();
            }
        });
        using var sender = new WebhookSender(new PipelineOptions { WebhookTimeout = TimeSpan.FromSeconds(10) }, new ListLogger());

        var act = () => sender.SendAsync(Endpoint($"http://127.0.0.1:{port}/hook"), Context(), "contoso", CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
        failure.Unexpected.Should().BeFalse();
        failure.Message.Should().StartWith("Endpoint unavailable: webhook 'Order hook' returned 302");
        Volatile.Read(ref seen).Should().Be(1);
        stop.Cancel();
        listener.Stop();
        await serving;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task Redirect_is_a_failure()
    {
        var (sender, handler, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://elsewhere.example.test/") },
        }));

        var act = () => sender.SendAsync(Endpoint("https://hooks.example.test/in"), Context(), "contoso", CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
        handler.Requests.Should().ContainSingle();
        failure.Unexpected.Should().BeFalse();
        failure.Message.Should().Be("Endpoint unavailable: webhook 'Order hook' returned 302 Found.");
    }

    [Fact]
    public async Task Hanging_receiver_times_out()
    {
        var (sender, _, _) = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, TimeSpan.FromSeconds(1));

        var act = () => sender.SendAsync(Endpoint("https://hooks.example.test/in"), Context(), "contoso", CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
        failure.Unexpected.Should().BeTrue();
        failure.Message.Should().Be("Endpoint unavailable: webhook 'Order hook' did not answer within 1 s.");
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_timeout()
    {
        var (sender, _, _) = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => sender.SendAsync(Endpoint("https://hooks.example.test/in"), Context(), "contoso", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Unreachable_receiver_is_retryable()
    {
        var (sender, handler, _) = Create((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError,
            "Connection refused (hooks.example.test:443)", new SocketException((int)SocketError.ConnectionRefused)));

        var act = () => sender.SendAsync(Endpoint("https://hooks.example.test/in"), Context(), "contoso", CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
        handler.Requests.Should().ContainSingle();
        failure.Unexpected.Should().BeTrue();
        failure.Message.Should().Be("Endpoint unavailable: webhook 'Order hook' could not be reached (ConnectionError).");
    }

    [Fact]
    public async Task Fault_never_contains_query_or_secret()
    {
        var failures = new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
        {
            Statuses(HttpStatusCode.BadGateway),
            Statuses(HttpStatusCode.Unauthorized),
            (r, _) => throw new HttpRequestException($"Cannot reach {r.RequestUri}", new SocketException((int)SocketError.HostNotFound)),
        };
        foreach (var respond in failures)
        {
            var (sender, _, logger) = Create(respond);
            var endpoint = Endpoint("https://hooks.example.test/in?code=s3cr3t", 6,
                "<settings><setting name=\"token\" value=\"t0p\" /></settings>");

            var act = () => sender.SendAsync(endpoint, Context(), "contoso", CancellationToken.None);

            var failure = (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
            failure.ToString().Should().NotContain("s3cr3t").And.NotContain("t0p").And.NotContain("code=");
            logger.Lines.Should().NotBeEmpty();
            logger.Lines.Should().AllSatisfy(l => l.Should().NotContain("s3cr3t").And.NotContain("t0p").And.NotContain("code="));
            logger.Lines.Should().Contain(l => l.Contains("Order hook") && l.Contains("https://hooks.example.test/in"));
        }
    }

    private sealed record SeenRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? ContentType, byte[] Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<SeenRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Enqueue(new SeenRequest(request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType?.ToString(), body));
            var response = await respond(request, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class ListLogger : ILogger<WebhookSender>
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
