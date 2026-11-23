using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;
using static Xrmd.Integration.Tests.WorkflowTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1c, task 7: the server-only delivery identity in the pipeline. <see cref="InvocationContext.ServerSharedVariables"/> reaches
/// every stage of the execution and of the nested requests the core issues for it (the <c>queueitem</c> Create of AddToQueue), never
/// a request a plug-in makes; a plug-in that adds, changes or removes <see cref="MailSchema.MailboxDeliveryVariable"/> fails the
/// message with 400; <see cref="InvocationContext.ServerSetValues"/> are written on a Create, outside the Target, and seen in the post image.
/// </summary>
[Collection("xrmd")]
public sealed class MailboxDeliveryContextTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Plugin = "Xrmd.TestPlugins.SharedVariablePlugin";
    private const string Key = MailSchema.MailboxDeliveryVariable;
    private const string Reserved = "the shared variable 'xrmverse_MailboxDeliveryId' is reserved";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private Guid _type;
    private Guid _assembly;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await WorkflowTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}; DELETE FROM data.{TestEntities.Pipe}; DELETE FROM data.asyncoperation;");
        _assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        _type = await PluginTestHelper.RegisterTypeAsync(Admin, _assembly, Plugin);
    }

    public async Task DisposeAsync()
    {
        await WorkflowTestHelper.ClearAsync(fx);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}; DELETE FROM data.{TestEntities.Pipe};");
    }

    // ---- helpers ----

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> LogAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name FROM data.{TestEntities.PipeLog}");
        var names = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) names.Add(rd.GetString(0));
        return names;
    }

    private Task<Guid> StepAsync(string message, string? entity, int stage, string config, int rank = 1) =>
        PluginTestHelper.RegisterStepAsync(Admin, _type, message, entity, stage, rank: rank, config: config);

    private Task<Guid> RecordStepAsync(string message, string? entity, int stage) =>
        StepAsync(message, entity, stage, $"record|{TestEntities.PipeLog}|{Key}");

    private async Task<(Guid Email, Guid Queue)> EmailAndQueueAsync()
    {
        var queue = await Admin.CreateAsync(new Entity("queue")
        {
            ["name"] = $"Delivery {_run}", ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1),
        });
        var email = await Admin.CreateAsync(new Entity("email") { ["subject"] = $"Delivered {_run}" });
        return (email, queue);
    }

    private static AddToQueueRequest Add(Guid email, Guid queue) =>
        new() { Target = new EntityReference("email", email), DestinationQueueId = queue };

    /// <summary>Runs <paramref name="request"/> through the pipeline as the admin, at depth 1, with the given server values.</summary>
    private async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, IReadOnlyDictionary<string, object>? shared = null,
        IReadOnlyDictionary<string, object?>? setValues = null)
    {
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        using var scope = fx.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PipelineExecutor>().ExecuteAsync(request,
            new InvocationContext(caller, caller.UserId, 1, null, null, Guid.NewGuid()) { ServerSharedVariables = shared, ServerSetValues = setValues },
            default);
    }

    private static Dictionary<string, object> Delivery(Guid mailbox) => new() { [Key] = mailbox.ToString("D") };

    // ---- tests ----

    [Fact]
    public async Task ServerSharedVariable_SeenAtEveryStage_AndInNestedCoreRequest()
    {
        var (email, queue) = await EmailAndQueueAsync();
        foreach (var stage in new[] { 10, 20, 40 })
        {
            await RecordStepAsync("AddToQueue", null, stage);
            await RecordStepAsync("Create", "queueitem", stage);
        }
        var mailbox = Guid.NewGuid();

        await ExecuteAsync(Add(email, queue), Delivery(mailbox));

        var value = mailbox.ToString("D");
        (await LogAsync()).Should().BeEquivalentTo(
            $"AddToQueue|10|1|{value}", $"AddToQueue|20|1|{value}", $"AddToQueue|40|1|{value}",
            $"Create|10|2|{value}", $"Create|20|2|{value}", $"Create|40|2|{value}");
    }

    [Fact]
    public async Task WithoutServerSharedVariable_NoStageSeesIt()
    {
        var (email, queue) = await EmailAndQueueAsync();
        await RecordStepAsync("AddToQueue", null, 20);
        await RecordStepAsync("Create", "queueitem", 20);

        await ExecuteAsync(Add(email, queue));

        (await LogAsync()).Should().BeEquivalentTo("AddToQueue|20|1|<none>", "Create|20|2|<none>");
    }

    [Fact]
    public async Task PluginNestedCall_DoesNotInherit()
    {
        var (email, queue) = await EmailAndQueueAsync();
        // The AddToQueue step creates a pb_pipe record through its own organization service; that Create records what it sees.
        var creator = await PluginTestHelper.RegisterTypeAsync(Admin, _assembly, "Xrmd.TestPlugins.CreateRecordPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, creator, "AddToQueue", null, 20, config: TestEntities.Pipe);
        await RecordStepAsync("Create", TestEntities.Pipe, 20);
        await RecordStepAsync("AddToQueue", null, 40);

        await ExecuteAsync(Add(email, queue), Delivery(Guid.NewGuid()));

        var log = await LogAsync();
        log.Should().Contain("Create|20|2|<none>", "a request a plug-in makes is not a request of the delivery");
        log.Should().Contain(l => l.StartsWith("AddToQueue|40|1|", StringComparison.Ordinal) && !l.EndsWith("<none>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReservedSharedVariable_SetByPlugin_Is400()
    {
        var (email, queue) = await EmailAndQueueAsync();
        await StepAsync("AddToQueue", null, 10, $"set|{Key}|{Guid.NewGuid():D}");

        var fault = await FaultAsync(() => Admin.ExecuteAsync(Add(email, queue)));

        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain(Reserved);
        (await CountItemsAsync(email)).Should().Be(0);
    }

    [Theory]
    [InlineData("set", 10)]
    [InlineData("set", 20)]
    [InlineData("set", 40)]
    [InlineData("remove", 20)]
    public async Task ReservedSharedVariable_ChangedOrRemovedByPlugin_Is400_AndRollsBack(string mode, int stage)
    {
        var (email, queue) = await EmailAndQueueAsync();
        await StepAsync("AddToQueue", null, stage, mode == "set" ? $"set|{Key}|{Guid.NewGuid():D}" : $"remove|{Key}");

        var fault = await FaultAsync(() => ExecuteAsync(Add(email, queue), Delivery(Guid.NewGuid())));

        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain(Reserved);
        (await CountItemsAsync(email)).Should().Be(0, "the delivery rolls back");
    }

    [Fact]
    public async Task ReservedSharedVariable_SetToSameValueByPlugin_IsAllowed()
    {
        var (email, queue) = await EmailAndQueueAsync();
        var mailbox = Guid.NewGuid();
        await StepAsync("AddToQueue", null, 20, $"set|{Key}|{mailbox:D}");

        await ExecuteAsync(Add(email, queue), Delivery(mailbox));

        (await CountItemsAsync(email)).Should().Be(1);
    }

    [Fact]
    public async Task ReservedSharedVariable_SetByNestedPlugin_OnQueueItemCreate_Is400()
    {
        var (email, queue) = await EmailAndQueueAsync();
        await StepAsync("Create", "queueitem", 20, $"set|{Key}|{Guid.NewGuid():D}");

        var fault = await FaultAsync(() => ExecuteAsync(Add(email, queue), Delivery(Guid.NewGuid())));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(Reserved);
    }

    [Fact]
    public async Task ReservedSharedVariable_ChangedInParentContext_Is400()
    {
        var (email, queue) = await EmailAndQueueAsync();
        // A step of the nested queueitem Create rewrites the variable of the AddToQueue it runs under.
        await StepAsync("Create", "queueitem", 20, $"setparent|{Key}|{Guid.NewGuid():D}");

        var fault = await FaultAsync(() => ExecuteAsync(Add(email, queue), Delivery(Guid.NewGuid())));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(Reserved);
        (await CountItemsAsync(email)).Should().Be(0);
    }

    [Fact]
    public async Task ReservedSharedVariable_SetByPlugin_ThenNestedCall_IsRefusedBeforeItRuns()
    {
        var (email, queue) = await EmailAndQueueAsync();
        // Stage 10 runs outside the transaction: a nested Create made there would be written even though the step fails afterwards.
        await StepAsync("AddToQueue", null, 10, $"setandcreate|{Key}|{Guid.NewGuid():D}|{TestEntities.Pipe}");

        var fault = await FaultAsync(() => Admin.ExecuteAsync(Add(email, queue)));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(Reserved);
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM data.{TestEntities.Pipe}");
        Convert.ToInt64(await cmd.ExecuteScalarAsync()).Should().Be(0, "the nested Create is refused before it runs");
    }

    [Fact]
    public async Task OtherSharedVariables_StayFreeForPlugins()
    {
        var (email, queue) = await EmailAndQueueAsync();
        await StepAsync("AddToQueue", null, 10, "set|my_variable|x");

        await Admin.ExecuteAsync(Add(email, queue));

        (await CountItemsAsync(email)).Should().Be(1);
    }

    [Fact]
    public async Task ServerSetValues_WrittenOnCreate_NotInTarget_InPostImage()
    {
        var recorder = await PluginTestHelper.RegisterTypeAsync(Admin, _assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, recorder, "Create", "email", 20, config: TestEntities.PipeLog);
        var post = await PluginTestHelper.RegisterStepAsync(Admin, recorder, "Create", "email", 40, config: TestEntities.PipeLog);
        await PluginTestHelper.RegisterImageAsync(Admin, post, 1, "post", "subject,senton,correlationmethod,emailsender");
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        var sentOn = new DateTime(2026, 11, 20, 8, 30, 0, DateTimeKind.Utc);

        var response = await ExecuteAsync(new CreateRequest { Target = new Entity("email") { ["subject"] = $"Received {_run}" } },
            setValues: new Dictionary<string, object?>
            {
                ["senton"] = sentOn,
                ["correlationmethod"] = new OptionSetValue(MailSchema.CorrelationInReplyTo),
                ["emailsender"] = new EntityReference("systemuser", caller.UserId),
            });

        var id = (Guid)response.Results["id"];
        var stored = await Admin.RetrieveAsync("email", id, new ColumnSet("senton", "correlationmethod", "emailsender"));
        stored.GetAttributeValue<DateTime>("senton").Should().Be(sentOn);
        stored.GetAttributeValue<OptionSetValue>("correlationmethod").Value.Should().Be(MailSchema.CorrelationInReplyTo);
        stored.GetAttributeValue<EntityReference>("emailsender").Id.Should().Be(caller.UserId);

        var data = await RecorderDataAsync();
        data.Should().HaveCount(2);
        foreach (var lines in data)
            lines.Single(l => l.StartsWith("target=", StringComparison.Ordinal)).Should()
                .NotContain("senton").And.NotContain("correlationmethod").And.NotContain("emailsender");
        data.Single(d => d.Any(l => l.StartsWith("post=post:", StringComparison.Ordinal)))
            .Single(l => l.StartsWith("post=", StringComparison.Ordinal)).Should()
            .Contain("senton").And.Contain("correlationmethod").And.Contain("emailsender");
        stored.GetAttributeValue<EntityReference>("emailsender").LogicalName.Should().Be("systemuser");
        // The post image's values are the written ones (ContextRecorderPlugin lists the post image's scalar values).
        data.Single(d => d.Any(l => l.StartsWith("post=post:", StringComparison.Ordinal)))
            .Single(l => l.StartsWith("postvalues=", StringComparison.Ordinal)).Should().Contain($"subject=Received {_run}");
        var image = await PostImageAsync(id);
        image.GetAttributeValue<DateTime>("senton").Should().Be(sentOn);
        image.GetAttributeValue<OptionSetValue>("correlationmethod").Value.Should().Be(MailSchema.CorrelationInReplyTo);
        image.GetAttributeValue<EntityReference>("emailsender").Should().Match<EntityReference>(r => r.Id == caller.UserId && r.LogicalName == "systemuser");
    }

    [Fact]
    public async Task ServerSetValues_OnAnotherMessage_IsRefused()
    {
        var (email, _) = await EmailAndQueueAsync();

        var act = () => ExecuteAsync(new UpdateRequest { Target = new Entity("email", email) { ["subject"] = "x" } },
            setValues: new Dictionary<string, object?> { ["senton"] = DateTime.UtcNow });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// The post image a Create step of <c>email</c> gets, as the pipeline builds it (<see cref="IPipelineCore.RetrieveImageAsync"/>
    /// with the step's attributes): the values ContextRecorderPlugin cannot print (dates, options, references).
    /// </summary>
    private async Task<Entity> PostImageAsync(Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        using var scope = fx.App.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IPipelineCore>().RetrieveImageAsync(new EntityReference("email", id),
            ["subject", "senton", "correlationmethod", "emailsender"], new DataSession(conn, null), default))!;
    }

    private AsyncOperationWorker NewWorker() =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions
            {
                AsyncPollInterval = TimeSpan.FromMilliseconds(100), AsyncRetryDelay = TimeSpan.FromMilliseconds(100), AsyncMaxRetries = 0,
            }));

    /// <summary>Runs the queued jobs until none is left to take.</summary>
    private async Task RunJobsAsync()
    {
        var worker = NewWorker();
        for (var i = 0; i < 20 && await worker.RunOnceAsync(default); i++) { }
    }

    /// <summary>The (statecode, statuscode) of every job, as "state/status".</summary>
    private async Task<List<string>> JobStatesAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT statecode, statuscode FROM data.asyncoperation");
        var states = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) states.Add($"{rd.GetInt32(0)}/{rd.GetInt32(1)}");
        return states;
    }

    private async Task<long> PipeCountAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM data.{TestEntities.Pipe}");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AsyncStep_DuringDelivery_SeesTheVariable_CallsTheService_WithoutPassingItOn()
    {
        var (email, queue) = await EmailAndQueueAsync();
        // Async: one step records what it sees (a service call), one creates a pb_pipe record, whose sync step records what it sees.
        await PluginTestHelper.RegisterStepAsync(Admin, _type, "AddToQueue", null, 40, mode: 1, config: $"record|{TestEntities.PipeLog}|{Key}");
        var creator = await PluginTestHelper.RegisterTypeAsync(Admin, _assembly, "Xrmd.TestPlugins.CreateRecordPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, creator, "AddToQueue", null, 40, mode: 1, rank: 2, config: TestEntities.Pipe);
        await RecordStepAsync("Create", TestEntities.Pipe, 20);
        var mailbox = Guid.NewGuid();

        await ExecuteAsync(Add(email, queue), Delivery(mailbox));
        await RunJobsAsync();

        (await JobStatesAsync()).Should().Equal(["3/30", "3/30"], "both asynchronous steps succeed");
        var log = await LogAsync();
        log.Should().ContainSingle(l => l.StartsWith("AddToQueue|40|", StringComparison.Ordinal))
            .Which.Should().EndWith($"|{mailbox:D}", "the asynchronous context carries the delivery's variable");
        log.Should().ContainSingle(l => l.StartsWith("Create|20|", StringComparison.Ordinal))
            .Which.Should().EndWith("|<none>", "a request an asynchronous plug-in makes is not a request of the delivery");
    }

    [Fact]
    public async Task AsyncStep_ForgingTheVariable_ThenCallingTheService_IsRefused()
    {
        var (email, queue) = await EmailAndQueueAsync();
        await PluginTestHelper.RegisterStepAsync(Admin, _type, "AddToQueue", null, 40, mode: 1,
            config: $"setandcreate|{Key}|{Guid.NewGuid():D}|{TestEntities.Pipe}");

        await ExecuteAsync(Add(email, queue), Delivery(Guid.NewGuid()));
        await RunJobsAsync();

        (await JobStatesAsync()).Should().Equal(["3/31"], "the job fails");
        (await PipeCountAsync()).Should().Be(0, "the forged request never runs");
    }

    [Fact]
    public async Task RealtimeWorkflow_SeesNoVariableInItsRequests()
    {
        // A real-time workflow on account Create creates a task; the task's Create step records what it sees.
        var subject = $"Delivery task {_run}";
        var xaml = Remove(Replace(Without(WorkflowFixtures.Xaml("realtime-create-update"), "UpdateStep2"), "\"Call the new customer\"", $"\"{subject}\""),
            "<mxswa:SetEntityProperty Attribute=\"regardingobjectid\"[^>]*/>");
        await WorkflowTestHelper.ActivateAsync(fx, Admin, Realtime($"Delivery {_run}", xaml, "account") with { TriggerOnCreate = true, CreateStage = 40 });
        await RecordStepAsync("Create", "account", 40);
        await RecordStepAsync("Create", "task", 20);
        var mailbox = Guid.NewGuid();

        await ExecuteAsync(new CreateRequest { Target = new Entity("account") { ["name"] = $"Delivered {_run}" } }, Delivery(mailbox));

        var log = await LogAsync();
        log.Should().Contain($"Create|40|1|{mailbox:D}");
        log.Should().ContainSingle(l => l.StartsWith("Create|20|2|", StringComparison.Ordinal))
            .Which.Should().Be("Create|20|2|<none>", "a request a workflow makes is not a request of the delivery");
    }

    private async Task<long> CountItemsAsync(Guid email)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM data.queueitem WHERE objectid = '{email}'");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>The pb_data of each ContextRecorderPlugin record, as lines.</summary>
    private async Task<List<string[]>> RecorderDataAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_data FROM data.{TestEntities.PipeLog} WHERE pb_data IS NOT NULL");
        var data = new List<string[]>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) data.Add(rd.GetString(0).Split('\n'));
        return data;
    }
}
