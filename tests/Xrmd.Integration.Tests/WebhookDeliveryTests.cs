using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Webhooks;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.2, task 4: the delivery of webhook steps, synchronously in the pipeline (a failure rolls the operation back) and through
/// the async queue (retryable and final failures, auto-delete, deleted endpoint), to a local receiver on 127.0.0.1. The host's
/// <see cref="PipelineOptions.WebhookAllowAnyPort"/> is turned on for the duration of each test.
/// </summary>
[Collection("xrmd")]
public sealed class WebhookDeliveryTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Secret = "d3livery-k3y";
    private const int Sync = 0;
    private const int Async = 1;

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private PipelineOptions HostOptions => fx.App.Services.GetRequiredService<IOptions<PipelineOptions>>().Value;
    private readonly List<Receiver> _receivers = [];
    private object _traceSetting = DBNull.Value;

    public async Task InitializeAsync()
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync("DELETE FROM data.serviceendpoint; DELETE FROM meta.serviceendpoint_secret; DELETE FROM data.asyncoperation; DELETE FROM data.plugintracelog;");
        _traceSetting = await ScalarAsync("SELECT plugintracelogsetting FROM data.organization") ?? DBNull.Value;
        HostOptions.WebhookAllowAnyPort = true;
    }

    public async Task DisposeAsync()
    {
        HostOptions.WebhookAllowAnyPort = false;
        await ScalarAsync("UPDATE data.organization SET plugintracelogsetting = @setting", ("@setting", _traceSetting));
        foreach (var receiver in _receivers) receiver.Dispose();
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync("DELETE FROM data.serviceendpoint; DELETE FROM meta.serviceendpoint_secret; DELETE FROM data.asyncoperation;");
        _receivers.SelectMany(r => r.Errors).Should().BeEmpty("the receiver handled every request");
    }

    // ---- receiver ----

    internal sealed record Received(string Path, string Query, IReadOnlyDictionary<string, string> Headers, JsonElement Body);

    /// <summary>An <see cref="HttpListener"/> on 127.0.0.1 and a free port; answers <see cref="Status"/> to every POST.</summary>
    internal sealed class Receiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public ConcurrentQueue<Received> Requests { get; } = new();
        public ConcurrentQueue<Exception> Errors { get; } = new();
        public int Status { get; set; } = 200;
        public Func<Task>? OnRequest { get; set; }
        public string Url { get; }

        public Receiver()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/hook/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

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
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    var text = await reader.ReadToEndAsync();
                    var headers = ctx.Request.Headers.AllKeys.Where(k => k is not null)
                        .ToDictionary(k => k!, k => ctx.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase);
                    if (OnRequest is { } callback) await callback();
                    Requests.Enqueue(new Received(ctx.Request.Url!.AbsolutePath, ctx.Request.Url.Query, headers,
                        JsonDocument.Parse(text).RootElement.Clone()));
                    ctx.Response.StatusCode = Status;
                }
                catch (Exception ex)
                {
                    // A bug of the receiver answers at once (rather than leaving the sender to its timeout) and is reported.
                    Errors.Enqueue(ex);
                    ctx.Response.StatusCode = 500;
                }
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
    }

    private Receiver NewReceiver()
    {
        var receiver = new Receiver();
        _receivers.Add(receiver);
        return receiver;
    }

    // ---- helpers ----

    private Task<Guid> CreateEndpointAsync(Receiver receiver, string name = "delivery hook") =>
        Admin.CreateAsync(new Entity(ServiceEndpointSchema.Table)
        {
            ["name"] = name, ["contract"] = new OptionSetValue(ServiceEndpointSchema.WebhookContract),
            ["authtype"] = new OptionSetValue(ServiceEndpointSchema.WebhookKey), ["url"] = receiver.Url,
            [ServiceEndpointSchema.AuthValue] = Secret,
        });

    private Task<Guid> CreateStepAsync(Guid endpoint, string message, int stage, int mode, bool autoDelete = false)
    {
        var step = new Entity("sdkmessageprocessingstep")
        {
            ["name"] = $"hook {message} {stage} {mode}",
            ["sdkmessageid"] = new EntityReference("sdkmessage", SdkMessageCatalog.MessageId(message)),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", SdkMessageCatalog.FilterId(message, "account")),
            ["eventhandler"] = new EntityReference(ServiceEndpointSchema.Table, endpoint),
            ["stage"] = new OptionSetValue(stage),
            ["mode"] = new OptionSetValue(mode),
            ["rank"] = 1,
        };
        if (autoDelete) step["asyncautodelete"] = true;
        return Admin.CreateAsync(step);
    }

    /// <summary>
    /// A webhook step of <paramref name="endpoint"/> on <paramref name="message"/>, filtered on <paramref name="entity"/> when given (plan 4.1c:
    /// AddToQueue has no table filter).
    /// </summary>
    internal static Task<Guid> CreateWebhookStepAsync(OrganizationService admin, Guid endpoint, string message, string? entity, int stage, int mode = Sync)
    {
        var step = new Entity("sdkmessageprocessingstep")
        {
            ["name"] = $"hook {message} {entity} {stage} {mode}",
            ["sdkmessageid"] = new EntityReference("sdkmessage", SdkMessageCatalog.MessageId(message)),
            ["eventhandler"] = new EntityReference(ServiceEndpointSchema.Table, endpoint),
            ["stage"] = new OptionSetValue(stage),
            ["mode"] = new OptionSetValue(mode),
            ["rank"] = 1,
        };
        if (entity is not null) step["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", SdkMessageCatalog.FilterId(message, entity));
        return admin.CreateAsync(step);
    }

    private AsyncOperationWorker NewWorker(int maxRetries = 3) =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions
            {
                AsyncPollInterval = TimeSpan.FromMilliseconds(100),
                AsyncRetryDelay = TimeSpan.FromMilliseconds(100),
                AsyncMaxRetries = maxRetries,
            }));

    /// <summary>Runs jobs until one is taken (a retried job waits for its postponeuntil); fails after 10 s.</summary>
    private static async Task RunNextAsync(AsyncOperationWorker worker)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await worker.RunOnceAsync(default))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "a job should become ready");
            await Task.Delay(20);
        }
    }

    private sealed record Job(Guid Id, int StateCode, int StatusCode, Guid? OwningExtensionId, string? Message);

    private async Task<Job> SingleJobAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT asyncoperationid, statecode, statuscode, owningextensionid, message FROM data.asyncoperation", conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue("one asyncoperation is expected");
        var job = new Job(rd.GetGuid(0), rd.GetInt32(1), rd.GetInt32(2), rd.IsDBNull(3) ? null : rd.GetGuid(3), rd.IsDBNull(4) ? null : rd.GetString(4));
        (await rd.ReadAsync()).Should().BeFalse("only one asyncoperation is expected");
        return job;
    }

    private Task ExecAsync(string sql) => ScalarAsync(sql);

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<int> AccountCountAsync(Guid id) =>
        // A dirty read on SQL Server; PostgreSQL has none, and its plain read sees the committed rows without waiting.
        Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM data.account{(TestProvider.IsPostgres ? "" : " WITH (READUNCOMMITTED)")} WHERE accountid = @id",
            ("@id", id)));

    // ---- tests ----

    [Fact]
    public async Task Sync_webhook_on_create_posts_context()
    {
        var receiver = NewReceiver();
        var step = await CreateStepAsync(await CreateEndpointAsync(receiver), "Create", 40, Sync);
        await ExecAsync("UPDATE data.organization SET plugintracelogsetting = 2"); // All: a plug-in step would be traced

        var id = await Admin.CreateAsync(new Entity("account") { ["name"] = "Quillmoor Rope" });

        var request = receiver.Requests.Should().ContainSingle().Which;
        request.Headers["x-ms-dynamics-request-name"].Should().Be("Create");
        request.Headers["x-ms-dynamics-entity-name"].Should().Be("account");
        request.Query.Should().Contain("code=" + Secret);
        request.Body.GetProperty("PrimaryEntityId").GetGuid().Should().Be(id);
        request.Body.GetProperty("Mode").GetInt32().Should().Be(0);
        request.Body.GetProperty("Stage").GetInt32().Should().Be(40);
        request.Body.GetProperty("OwningExtension").GetProperty("Id").GetGuid().Should().Be(step);
        (await ScalarAsync("SELECT COUNT(*) FROM data.plugintracelog")).Should().Be(0, "Dataverse writes no trace for a webhook step");
    }

    /// <summary>
    /// Plan 4.1c, task 8 (the contact center contract): the synchronous stage 20 webhook of a mailbox delivery's AddToQueue gets the
    /// <c>SharedVariables</c> array with <see cref="MailSchema.MailboxDeliveryVariable"/>, the mailbox id; so does the email Create's, whose
    /// body is trimmed when the email is too large for it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delivery_SharedVariable_InBody(bool trimmed)
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        var kit = new IncomingKit(fx, Guid.NewGuid().ToString("N")[..8]);
        var (_, mailbox, _) = await kit.QueueMailboxAsync();
        var receiver = NewReceiver();
        var endpoint = await CreateEndpointAsync(receiver);
        await CreateWebhookStepAsync(Admin, endpoint, "AddToQueue", null, 20);
        await CreateWebhookStepAsync(Admin, endpoint, "Create", "email", 20);
        var html = trimmed ? "<p>" + new string('x', WebhookPayload.SizeLimit) + "</p>" : "<p>Hi</p>";

        (await kit.ImportAsync(mailbox, kit.Message(kit.Address("Desk"), html: html))).Should().Be(Xrmd.Core.Mail.ImportOutcome.Created);

        var requests = receiver.Requests.ToList();
        requests.Select(r => r.Headers["x-ms-dynamics-request-name"]).Should().BeEquivalentTo(["Create", "AddToQueue"]);
        foreach (var request in requests)
        {
            var shared = request.Body.GetProperty("SharedVariables");
            shared.ValueKind.Should().Be(JsonValueKind.Array);
            shared.EnumerateArray().Should().ContainSingle(p => p.GetProperty("key").GetString() == MailSchema.MailboxDeliveryVariable)
                .Which.GetProperty("value").GetString().Should().Be(mailbox.ToString("D"));
            request.Body.GetProperty("Stage").GetInt32().Should().Be(20);
        }
        var create = requests.Single(r => r.Headers["x-ms-dynamics-request-name"] == "Create");
        create.Headers.ContainsKey("x-ms-dynamics-msg-size-exceeded").Should().Be(trimmed);
        requests.Single(r => r.Headers["x-ms-dynamics-request-name"] == "AddToQueue").Headers.ContainsKey("x-ms-dynamics-msg-size-exceeded")
            .Should().BeFalse();
    }

    [Fact]
    public async Task Sync_webhook_failure_rolls_back()
    {
        var receiver = NewReceiver();
        receiver.Status = 400;
        await CreateStepAsync(await CreateEndpointAsync(receiver), "Create", 40, Sync);
        var id = Guid.NewGuid();

        var fault = await FaultAsync(() => Admin.CreateAsync(new Entity("account", id) { ["name"] = "Brackwater Mills" }));

        fault.HttpStatus.Should().Be(500);
        fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.Message.Should().StartWith("Endpoint unavailable: webhook 'delivery hook' returned 400").And.NotContain(Secret);
        receiver.Requests.Should().ContainSingle();
        (await AccountCountAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task Prevalidation_webhook_runs_before_write()
    {
        var receiver = NewReceiver();
        await CreateStepAsync(await CreateEndpointAsync(receiver), "Create", 10, Sync);
        var id = Guid.NewGuid();
        int? seen = null;
        receiver.OnRequest = async () => seen = await AccountCountAsync(id);

        await Admin.CreateAsync(new Entity("account", id) { ["name"] = "Ostler Glass" });

        receiver.Requests.Should().ContainSingle().Which.Body.GetProperty("Stage").GetInt32().Should().Be(10);
        seen.Should().Be(0, "the row is written after stage 10");
        (await AccountCountAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task Async_webhook_posts_after_commit()
    {
        var receiver = NewReceiver();
        var step = await CreateStepAsync(await CreateEndpointAsync(receiver), "Create", 40, Async);

        var id = await Admin.CreateAsync(new Entity("account") { ["name"] = "Lanternfield" });
        receiver.Requests.Should().BeEmpty("an asynchronous step runs after the commit, from the queue");
        await RunNextAsync(NewWorker());

        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((3, 30));
        job.OwningExtensionId.Should().Be(step);
        var body = receiver.Requests.Should().ContainSingle().Which.Body;
        body.GetProperty("Mode").GetInt32().Should().Be(1);
        body.GetProperty("OperationId").GetGuid().Should().Be(job.Id);
        body.GetProperty("PrimaryEntityId").GetGuid().Should().Be(id);
    }

    [Fact]
    public async Task Async_webhook_5xx_is_retried_then_fails()
    {
        var receiver = NewReceiver();
        receiver.Status = 500;
        await CreateStepAsync(await CreateEndpointAsync(receiver), "Create", 40, Async);
        await Admin.CreateAsync(new Entity("account") { ["name"] = "Saltmarsh Ink" });
        var worker = NewWorker(maxRetries: 1);

        await RunNextAsync(worker);
        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((1, 10));

        await RunNextAsync(worker);
        job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((3, 31));
        job.Message.Should().Contain("returned 500").And.NotContain(Secret);
        receiver.Requests.Should().HaveCount(2, "no extra attempt at once for 500, only for 502, 503 and 504");
    }

    [Fact]
    public async Task Async_webhook_4xx_fails_at_once()
    {
        var receiver = NewReceiver();
        receiver.Status = 404;
        await CreateStepAsync(await CreateEndpointAsync(receiver), "Create", 40, Async);
        await Admin.CreateAsync(new Entity("account") { ["name"] = "Copperwell" });

        await RunNextAsync(NewWorker());

        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((3, 31));
        job.Message.Should().Contain("returned 404");
        receiver.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Async_autodelete_removes_job()
    {
        var receiver = NewReceiver();
        await CreateStepAsync(await CreateEndpointAsync(receiver), "Create", 40, Async, autoDelete: true);
        await Admin.CreateAsync(new Entity("account") { ["name"] = "Hollin Weave" });
        (await ScalarAsync("SELECT COUNT(*) FROM data.asyncoperation")).Should().Be(1);

        await RunNextAsync(NewWorker());

        receiver.Requests.Should().ContainSingle();
        (await ScalarAsync("SELECT COUNT(*) FROM data.asyncoperation")).Should().Be(0);
    }

    [Fact]
    public async Task Images_are_sent()
    {
        var receiver = NewReceiver();
        var step = await CreateStepAsync(await CreateEndpointAsync(receiver), "Update", 40, Sync);
        await PluginTestHelper.RegisterImageAsync(Admin, step, 1, "post", "name");
        var id = await Admin.CreateAsync(new Entity("account") { ["name"] = "Fernhollow" });

        await Admin.UpdateAsync(new Entity("account", id) { ["name"] = "Fernhollow Ltd" });

        var images = receiver.Requests.Should().ContainSingle().Which.Body.GetProperty("PostEntityImages");
        var post = images.EnumerateArray().Should().ContainSingle().Which;
        post.GetProperty("key").GetString().Should().Be("post");
        post.GetProperty("value").GetProperty("Id").GetGuid().Should().Be(id);
        post.GetProperty("value").GetRawText().Should().Contain("Fernhollow Ltd");
    }

    [Fact]
    public async Task Endpoint_update_applies_at_once()
    {
        var first = NewReceiver();
        var second = NewReceiver();
        var endpoint = await CreateEndpointAsync(first);
        await CreateStepAsync(endpoint, "Create", 40, Sync);
        await Admin.CreateAsync(new Entity("account") { ["name"] = "Wexford Slate" });
        first.Requests.Should().ContainSingle();

        await Admin.UpdateAsync(new Entity(ServiceEndpointSchema.Table, endpoint) { ["url"] = second.Url });
        await Admin.CreateAsync(new Entity("account") { ["name"] = "Wexford Slate II" });

        first.Requests.Should().ContainSingle();
        second.Requests.Should().ContainSingle().Which.Query.Should().Contain("code=" + Secret);
    }

    [Fact]
    public async Task Queued_job_of_deleted_endpoint_is_cancelled()
    {
        var receiver = NewReceiver();
        var endpoint = await CreateEndpointAsync(receiver);
        await CreateStepAsync(endpoint, "Create", 40, Async);
        await Admin.CreateAsync(new Entity("account") { ["name"] = "Morrow Kiln" });

        await Admin.DeleteAsync(ServiceEndpointSchema.Table, endpoint);
        await RunNextAsync(NewWorker());

        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((3, 32));
        job.Message.Should().Be("The plug-in step was deleted.");
        receiver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task No_step_runs_on_a_serviceendpoint_message()
    {
        // Global steps (no table filter) on Create: a synchronous stage 10 webhook, an asynchronous one and a stage 20 plug-in.
        var receiver = NewReceiver();
        var endpoint = await CreateEndpointAsync(receiver);
        foreach (var (stage, mode) in new[] { (10, Sync), (40, Async) })
            await Admin.CreateAsync(new Entity("sdkmessageprocessingstep")
            {
                ["name"] = $"global hook {stage} {mode}",
                ["sdkmessageid"] = new EntityReference("sdkmessage", SdkMessageCatalog.MessageId("Create")),
                ["eventhandler"] = new EntityReference(ServiceEndpointSchema.Table, endpoint),
                ["stage"] = new OptionSetValue(stage), ["mode"] = new OptionSetValue(mode), ["rank"] = 1,
            });
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", null, 20, config: "invalid:global plug-in ran");
        while (receiver.Requests.TryDequeue(out _)) { }
        await ExecAsync("DELETE FROM data.asyncoperation");

        const string other = "0ther-s3cret";
        await Admin.CreateAsync(new Entity(ServiceEndpointSchema.Table)
        {
            ["name"] = "second hook", ["contract"] = new OptionSetValue(ServiceEndpointSchema.WebhookContract),
            ["authtype"] = new OptionSetValue(ServiceEndpointSchema.WebhookKey), ["url"] = receiver.Url,
            [ServiceEndpointSchema.AuthValue] = other,
        });

        receiver.Requests.Should().BeEmpty("the messages of serviceendpoint are not events: no step sees its authvalue");
        (await ScalarAsync("SELECT COUNT(*) FROM data.asyncoperation")).Should().Be(0);

        // The same global steps run on any other table.
        var fault = await FaultAsync(() => Admin.CreateAsync(new Entity("account") { ["name"] = "Gristle Yard" }));
        fault.Message.Should().Be("global plug-in ran");
        receiver.Requests.Should().ContainSingle().Which.Body.GetProperty("Stage").GetInt32().Should().Be(10);
    }

    /// <summary>A value of a type that cannot be a data contract (no parameterless constructor).</summary>
    private sealed class Opaque(string value)
    {
        public string Value { get; } = value;
        public override string ToString() => Value;
    }

    /// <summary>A value of a type that could be a data contract but is not one of the payload's known types.</summary>
    public sealed class Unlisted
    {
        public string Value { get; set; } = "";
        public override string ToString() => Value;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unserializable_context_fails_final_without_the_value(bool invalidContract)
    {
        const string value = "0paque-v4lue";
        var receiver = NewReceiver();
        var stepId = await CreateStepAsync(await CreateEndpointAsync(receiver), "Create", 40, Async);
        var step = (await fx.App.Services.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>().GetStepAsync(stepId, default))!;
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        var context = new XrmdPluginExecutionContext
        {
            MessageName = "Create", PrimaryEntityName = "account", PrimaryEntityId = Guid.NewGuid(), Stage = 40, Mode = Async,
            UserId = caller.UserId, InitiatingUserId = caller.UserId, BusinessUnitId = caller.BusinessUnitId, OperationId = Guid.NewGuid(),
            OrganizationName = "org", OwningExtension = new EntityReference("sdkmessageprocessingstep", stepId),
        };
        context.InputParameters["Opaque"] = invalidContract ? new Opaque(value) : new Unlisted { Value = value };

        await using var scope = fx.App.Services.CreateAsyncScope();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var act = () => scope.ServiceProvider.GetRequiredService<PipelineExecutor>()
            .RunAsyncStepAsync(step, context, new Xrmd.Abstractions.DataSession(conn, null), default);

        var failure = (await act.Should().ThrowAsync<Xrmd.Pipeline.Sandbox.PluginExecutionException>()).Which;
        failure.Unexpected.Should().BeFalse("a context that cannot be serialized will not serialize on a retry");
        failure.Fault.HttpStatus.Should().Be(500);
        failure.Fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        failure.Fault.Message.Should().Be(
            "Endpoint unavailable: webhook 'delivery hook' could not be sent the context: it holds a value that cannot be serialized.");
        new[] { failure.Message, failure.Fault.Message, failure.Detail ?? "" }.Should().NotContain(m => m.Contains(value) || m.Contains(nameof(Opaque)) || m.Contains(nameof(Unlisted)));
        receiver.Requests.Should().BeEmpty();
    }
}
