using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.WebApi.Connector;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan X.12b, task 5: the flow notification jobs (operationtype 99014) are posted to the subscription's callback with the webhook
/// retry rules, to a local receiver (<see cref="ConnectorOptions.AllowPrivateCallbacks"/> on and <c>localhost</c> allowlisted, unless a
/// test says otherwise).
/// </summary>
[Collection("xrmd")]
public sealed class FlowNotificationDeliveryTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Signature = "s1gnatur3-v4lue";
    private readonly List<Guid> _subscriptions = [];
    private readonly List<Guid> _accounts = [];
    private readonly List<Receiver> _receivers = [];
    private int _maxRetries;
    private TimeSpan _retryDelay;

    private static readonly (string, DepthMask)[] Subscriber =
    [
        ("prvCreatexrmverse_FlowSubscription", DepthMask.Basic), ("prvReadxrmverse_FlowSubscription", DepthMask.Basic),
        ("prvReadAccount", DepthMask.Global), ("prvCreateAccount", DepthMask.Basic),
    ];

    /// <summary>The configured callback hosts, restored after each test.</summary>
    private string[] _originalHosts = [];

    private ConnectorOptions Options => fx.App.Services.GetRequiredService<IOptions<ConnectorOptions>>().Value;
    private PipelineOptions HostOptions => fx.App.Services.GetRequiredService<IOptions<PipelineOptions>>().Value;
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        _originalHosts = Options.CallbackHosts;
        Options.CallbackHosts = ["localhost"];
        Options.AllowPrivateCallbacks = true;
        _maxRetries = HostOptions.AsyncMaxRetries;
        _retryDelay = HostOptions.AsyncRetryDelay;
        HostOptions.AsyncMaxRetries = 2;
        HostOptions.AsyncRetryDelay = TimeSpan.FromMilliseconds(100);
        await ClearJobsAsync();
    }

    public async Task DisposeAsync()
    {
        HostOptions.AsyncMaxRetries = _maxRetries;
        HostOptions.AsyncRetryDelay = _retryDelay;
        using (var scope = fx.App.Services.CreateScope())
            foreach (var id in _subscriptions) await scope.ServiceProvider.GetRequiredService<FlowSubscriptions>().EndAsync(id, default);
        _subscriptions.Clear();
        foreach (var id in _accounts)
            try { await Admin.DeleteAsync("account", id); }
            catch (Xrmd.Core.Errors.XrmdFaultException) { } // already deleted
        await ClearJobsAsync();
        foreach (var receiver in _receivers) receiver.Dispose();
        Options.CallbackHosts = _originalHosts;
        Options.AllowPrivateCallbacks = false;
        _receivers.SelectMany(r => r.Errors).Should().BeEmpty("the receiver handled every request");
    }

    private Task ClearJobsAsync() =>
        TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.asyncoperation WHERE operationtype = {AsyncOperationQueue.FlowNotification}");

    // ---- receiver ----

    private sealed record Received(string Query, string? ContentType, byte[] Body);

    /// <summary>An <see cref="HttpListener"/> on a free port, for any host name; answers <see cref="Status"/> to every request.</summary>
    private sealed class Receiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public ConcurrentQueue<Received> Requests { get; } = new();
        public ConcurrentQueue<Exception> Errors { get; } = new();
        public int Status { get; set; } = 200;
        public int Port { get; }

        public Receiver()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://+:{Port}/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        /// <summary>The callback URL of this receiver, signed with <see cref="Signature"/>.</summary>
        public string Callback => $"http://localhost:{Port}/workflows/run?sig={Signature}";

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                try
                {
                    using var buffer = new MemoryStream();
                    await ctx.Request.InputStream.CopyToAsync(buffer);
                    Requests.Enqueue(new Received(ctx.Request.Url!.Query, ctx.Request.Headers["Content-Type"], buffer.ToArray()));
                    ctx.Response.StatusCode = Status;
                    if (Status is >= 300 and < 400) ctx.Response.RedirectLocation = $"http://localhost:{Port}/elsewhere";
                }
                catch (Exception ex)
                {
                    Errors.Enqueue(ex);
                    ctx.Response.StatusCode = 500;
                }
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            if (!_listener.IsListening) return;
            _listener.Stop();
            _listener.Close();
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
    }

    private Receiver NewReceiver(int status = 200)
    {
        var receiver = new Receiver { Status = status };
        _receivers.Add(receiver);
        return receiver;
    }

    // ---- helpers ----

    private async Task<(Guid UserId, Guid Oid)> UserAsync()
    {
        var oid = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await AssignRoleAsync(fx, userId, await CreateRoleAsync(fx, "flowdeliver-" + Guid.NewGuid().ToString("N")[..8], Subscriber));
        return (userId, oid);
    }

    private async Task<Guid> SubscribeAsync(string callback, Guid? userId = null)
    {
        var user = userId ?? (await UserAsync()).UserId;
        using var scope = fx.App.Services.CreateScope();
        var id = await scope.ServiceProvider.GetRequiredService<FlowSubscriptions>().SubscribeAsync(
            new Caller(user, await RootBusinessUnitAsync(fx)), new SubscribeBody("account", "create,update,delete", null, callback), default);
        _subscriptions.Add(id);
        return id;
    }

    private async Task<Guid> CreateAccountAsync(string name)
    {
        var id = await Admin.CreateAsync(new Entity("account") { ["name"] = name });
        _accounts.Add(id);
        return id;
    }

    private sealed record Job(Guid Id, int State, int Status, string? Message, string? Data, DateTime? PostponeUntil);

    private async Task<List<Job>> JobsAsync(Guid subscription) =>
        (await TestSql.RowsAsync(fx.ConnectionString,
            "SELECT asyncoperationid, statecode, statuscode, message, data, postponeuntil FROM data.asyncoperation "
            + $"WHERE operationtype = {AsyncOperationQueue.FlowNotification} AND data LIKE @sub ORDER BY createdon",
            ("@sub", $"%{subscription}%")))
        .Select(r => new Job((Guid)r[0]!, Convert.ToInt32(r[1]), Convert.ToInt32(r[2]), r[3] as string, r[4] as string, r[5] as DateTime?))
        .ToList();

    private async Task<Job?> JobAsync(Guid id) =>
        (await TestSql.RowsAsync(fx.ConnectionString,
            "SELECT asyncoperationid, statecode, statuscode, message, data, postponeuntil FROM data.asyncoperation WHERE asyncoperationid = @id",
            ("@id", id)))
        .Select(r => new Job((Guid)r[0]!, Convert.ToInt32(r[1]), Convert.ToInt32(r[2]), r[3] as string, r[4] as string, r[5] as DateTime?))
        .SingleOrDefault();

    private AsyncOperationWorker NewWorker() =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Microsoft.Extensions.Options.Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100) }));

    /// <summary>Runs ready jobs until the job <paramref name="id"/> leaves Ready (it ran once); fails after 10 s.</summary>
    private async Task<Job?> RunOnceAsync(Guid id)
    {
        var worker = NewWorker();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var before = await JobAsync(id);
        while (true)
        {
            await worker.RunOnceAsync(default);
            var job = await JobAsync(id);
            if (job is null || job.State == 3 || (job.State == 1 && job.Data != before?.Data)) return job;
            DateTime.UtcNow.Should().BeBefore(deadline, "the job should run");
            await Task.Delay(20);
        }
    }

    /// <summary>Runs the job <paramref name="id"/> until it completes or is deleted; fails after 10 s.</summary>
    private async Task<Job?> RunToEndAsync(Guid id, List<Job>? seen = null)
    {
        var worker = NewWorker();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string? lastData = null;
        while (true)
        {
            await worker.RunOnceAsync(default);
            var job = await JobAsync(id);
            if (job is null || job.State == 3) return job;
            if (job.State == 1 && job.Data != lastData)
            {
                seen?.Add(job);
                lastData = job.Data;
            }
            DateTime.UtcNow.Should().BeBefore(deadline, "the job should end");
            await Task.Delay(20);
        }
    }

    private static string BodyText(Job job)
    {
        using var doc = JsonDocument.Parse(job.Data!);
        return doc.RootElement.GetProperty("body").GetRawText();
    }

    private static int Attempt(Job job) => (int)JsonNode.Parse(job.Data!)!["attempt"]!;

    private Task<int> SubscriptionRowsAsync(Guid id) =>
        TestSql.RowsAsync(fx.ConnectionString,
                $"SELECT 1 FROM data.{FlowSubscriptionSchema.Table} WHERE {FlowSubscriptionSchema.Id} = @id "
                + "UNION ALL SELECT 1 FROM meta.flow_subscription_callback WHERE subscriptionid = @id", ("@id", id))
            .ContinueWith(t => t.Result.Count, TaskScheduler.Default);

    // ---- tests ----

    [Fact]
    public async Task Delivers_body_and_deletes_job()
    {
        var receiver = NewReceiver();
        var sub = await SubscribeAsync(receiver.Callback);
        await CreateAccountAsync("Delivered");
        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;
        var expected = BodyText(job);

        (await RunToEndAsync(job.Id)).Should().BeNull("a delivered notification is deleted");

        var request = receiver.Requests.Should().ContainSingle().Subject;
        request.ContentType.Should().Be("application/json; charset=utf-8");
        Encoding.UTF8.GetString(request.Body).Should().Be(expected);
        request.Query.Should().Be($"?sig={Signature}");
    }

    [Fact]
    public async Task Gone_ends_subscription()
    {
        var receiver = NewReceiver(410);
        var sub = await SubscribeAsync(receiver.Callback);
        await CreateAccountAsync("Gone 1");
        await CreateAccountAsync("Gone 2");
        var jobs = await JobsAsync(sub);
        jobs.Should().HaveCount(2);

        var first = await RunToEndAsync(jobs[0].Id);
        (first!.State, first.Status).Should().Be((3, 30));
        first.Message.Should().Be("The flow subscription ended: the callback answered 410.");
        (await SubscriptionRowsAsync(sub)).Should().Be(0);
        receiver.Requests.Should().ContainSingle();

        var second = await RunToEndAsync(jobs[1].Id);
        (second!.State, second.Status).Should().Be((3, 30));
        second.Message.Should().Be("The flow subscription no longer exists.");
        receiver.Requests.Should().ContainSingle("the second notification is not posted");
    }

    [Fact]
    public async Task Missing_callback_ends_subscription()
    {
        var receiver = NewReceiver();
        var sub = await SubscribeAsync(receiver.Callback);
        await CreateAccountAsync("No callback");
        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM meta.flow_subscription_callback WHERE subscriptionid = @id", ("@id", sub));
        (await SubscriptionRowsAsync(sub)).Should().Be(1, "the subscription row is left without its callback");

        var done = await RunToEndAsync(job.Id);
        (done!.State, done.Status).Should().Be((3, 30));
        done.Message.Should().Be(FlowNotificationHandler.NoCallbackMessage).And.NotContain("localhost");
        (await SubscriptionRowsAsync(sub)).Should().Be(0, "the subscription is ended");
        receiver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Retryable_failures_suspend_then_fail()
    {
        var receiver = NewReceiver(500);
        var sub = await SubscribeAsync(receiver.Callback);
        await CreateAccountAsync("Retried");
        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;
        var body = BodyText(job);

        var first = await RunOnceAsync(job.Id);
        (first!.State, first.Status).Should().Be((1, 10));
        Attempt(first).Should().Be(2);
        BodyText(first).Should().Be(body);
        first.PostponeUntil.Should().NotBeNull();

        var second = await RunOnceAsync(job.Id);
        (second!.State, second.Status).Should().Be((1, 10));
        Attempt(second).Should().Be(3);
        second.PostponeUntil.Should().BeAfter(first.PostponeUntil!.Value);

        var last = await RunToEndAsync(job.Id);
        (last!.State, last.Status).Should().Be((3, 31));
        last.Message.Should().Contain("500").And.NotContain(Signature);
        receiver.Requests.Should().HaveCount(3, "one attempt and AsyncMaxRetries (2) retries");
    }

    [Fact]
    public async Task Overloaded_receiver_gets_one_immediate_extra_attempt()
    {
        var receiver = NewReceiver(503);
        var sub = await SubscribeAsync(receiver.Callback);
        await CreateAccountAsync("Overloaded");
        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;

        var first = await RunOnceAsync(job.Id);
        (first!.State, first.Status).Should().Be((1, 10));
        receiver.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(302)]
    public async Task Final_failure_fails_at_once(int status)
    {
        var receiver = NewReceiver(status);
        var sub = await SubscribeAsync(receiver.Callback);
        await CreateAccountAsync("Final " + status);
        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;

        var done = await RunOnceAsync(job.Id);
        (done!.State, done.Status).Should().Be((3, 31));
        done.Message.Should().Contain(status.ToString(System.Globalization.CultureInfo.InvariantCulture)).And.NotContain(Signature);
        receiver.Requests.Should().ContainSingle(status == 302 ? "the redirect is not followed" : "a 4xx is not retried");
        (await SubscriptionRowsAsync(sub)).Should().Be(2, "only 404 and 410 end the subscription");
    }

    [Fact]
    public async Task Private_address_is_refused_at_connect()
    {
        Options.AllowPrivateCallbacks = false;
        // https on 443 and an allowlisted name: the policy accepts it; localhost resolves to a loopback address.
        var sub = await SubscribeAsync($"https://localhost/workflows/run?sig={Signature}");
        await CreateAccountAsync("Private");
        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;

        var done = await RunOnceAsync(job.Id);
        (done!.State, done.Status).Should().Be((3, 31));
        done.Message.Should().Be("The callback URL resolves to an address that is not allowed.");
    }

    [Fact]
    public async Task Narrowed_allowlist_refuses_at_send()
    {
        var receiver = NewReceiver();
        var sub = await SubscribeAsync(receiver.Callback);
        await CreateAccountAsync("Narrowed");
        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;
        Options.CallbackHosts = ["*.logic.azure.com"];

        var done = await RunOnceAsync(job.Id);
        (done!.State, done.Status).Should().Be((3, 31));
        done.Message.Should().Be("The callback URL host is not allowed.");
        receiver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Callback_url_never_in_logs_or_job_message()
    {
        // A connection refused (the receiver is gone): the transport error's own message may quote the URL.
        HostOptions.AsyncMaxRetries = 0;
        var receiver = NewReceiver();
        var sub = await SubscribeAsync(receiver.Callback);
        receiver.Dispose();
        await CreateAccountAsync("Unreachable");
        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;

        var done = await RunOnceAsync(job.Id);
        (done!.State, done.Status).Should().Be((3, 31));
        done.Message.Should().NotBeNullOrEmpty().And.NotContain(Signature).And.NotContain("localhost:" + receiver.Port);
        done.Data.Should().NotContain(Signature);
    }

    /// <summary>
    /// The production wiring: subscribe and write through the hosted Web API (the host's own DI builds the hook and the handler),
    /// then the worker delivers the notification.
    /// </summary>
    [Fact]
    public async Task Web_api_write_is_delivered_end_to_end()
    {
        var receiver = NewReceiver();
        var (_, oid) = await UserAsync();

        var subscribe = new HttpRequestMessage(HttpMethod.Post, "/api/connector/v1/subscriptions")
        {
            Content = new StringContent(new JsonObject { ["table"] = "account", ["changes"] = "create", ["callbackUrl"] = receiver.Callback }.ToJsonString(),
                Encoding.UTF8, "application/json"),
        };
        subscribe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        var subscribed = await fx.Client.SendAsync(subscribe);
        subscribed.StatusCode.Should().Be(HttpStatusCode.Created, await subscribed.Content.ReadAsStringAsync());
        var sub = Guid.Parse((string)JsonNode.Parse(await subscribed.Content.ReadAsStringAsync())!["id"]!);
        _subscriptions.Add(sub);

        var create = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/accounts")
        {
            Content = new StringContent("""{"name":"Through the Web API"}""", Encoding.UTF8, "application/json"),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        var created = await fx.Client.SendAsync(create);
        created.StatusCode.Should().Be(HttpStatusCode.NoContent, await created.Content.ReadAsStringAsync());
        var location = created.Headers.GetValues("OData-EntityId").Single();
        var id = Guid.Parse(location[(location.LastIndexOf('(') + 1)..^1]);
        _accounts.Add(id);

        var job = (await JobsAsync(sub)).Should().ContainSingle().Subject;
        (await RunToEndAsync(job.Id)).Should().BeNull();

        var request = receiver.Requests.Should().ContainSingle().Subject;
        var body = JsonNode.Parse(request.Body)!;
        ((string)body["change"]!).Should().Be("create");
        ((string)body["table"]!).Should().Be("account");
        ((string)body["id"]!).Should().Be(id.ToString());
        ((string)body["row"]!["name"]!).Should().Be("Through the Web API");
    }
}
