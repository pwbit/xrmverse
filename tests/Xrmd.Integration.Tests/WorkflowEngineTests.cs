using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.Workflows;
using Xrmd.Workflows.Engine;
using Xrmd.Workflows.Xaml;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Workflows.Xaml.ValueExpr;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.14, task 4 (W7, W8, W11, W16, W19): <see cref="WorkflowEngine.RunAsync"/> run directly on parsed XAML, with a
/// background-style <see cref="WorkflowRun"/> (no session: each step's request runs in its own transaction) unless a test says
/// otherwise. The parity export (<c>tests/fixtures/workflows/real</c>, table <c>xrmv_order</c>) gives every shape it contains; the
/// provisional fixtures give the others (assign, set state, a stop with a message).
/// </summary>
[Collection("xrmd")]
public sealed class WorkflowEngineTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Order = TestEntities.Order;
    private const string OnCreate = "OnCreate-3E1F0307-D8DE-F111-84E8-002248872B66";
    private const string OnUpdate = "OnUpdate-BC9A625B-E4DE-F111-84E8-002248872B66";
    private const string Recalculate = "Recalculate-9DF696A1-E6DE-F111-84E8-002248872B66";
    private const string BusinessRule = "Newbusinessrule-216FA344-E5DE-F111-84E8-002248872B66";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureOrderAsync(Store);
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static WorkflowDefinition Real(string file, int category = 0) =>
        XamlWorkflowParser.Parse(WorkflowFixtures.Xaml("real/" + file), category, workflowSdkAvailable: false);

    private static WorkflowDefinition Provisional(string file, int category = 0) =>
        XamlWorkflowParser.Parse(WorkflowFixtures.Xaml(file), category, workflowSdkAvailable: false);

    /// <summary>The definition with only <paramref name="steps"/> as its body.</summary>
    private static WorkflowDefinition Only(WorkflowDefinition d, params WorkflowStep[] steps) => d with { Root = new SequenceStep("0", null, steps) };

    private static T Step<T>(WorkflowDefinition d, Func<T, bool>? where = null) where T : WorkflowStep =>
        d.Root.DepthFirst().OfType<T>().First(s => where?.Invoke(s) ?? true);

    private WorkflowEngine Engine() => new(TestServices.NewPipelineExecutor(fx.App.Services), Store);

    private async Task<WorkflowRun> RunAsync(EntityReference primary, Caller? caller = null, int stage = 0, Entity? target = null, string? message = null)
    {
        caller ??= await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        return new WorkflowRun
        {
            Workflow = new WorkflowRow { Id = Guid.NewGuid(), Name = "Engine test " + _run },
            Primary = primary,
            Target = target,
            Caller = caller,
            Invocation = new InvocationContext(caller, caller.UserId, 1, null, null, Guid.NewGuid()),
            Stage = stage,
            MessageName = message,
        };
    }

    private async Task<EntityReference> ContactAsync() =>
        new("contact", await Admin.CreateAsync(new Entity("contact")
        {
            ["firstname"] = "Ada", ["lastname"] = "Example " + _run, ["emailaddress1"] = $"ada.{_run}@example.com",
        }));

    private async Task<EntityReference> AccountAsync(EntityReference? primaryContact = null)
    {
        var account = new Entity("account") { ["name"] = "Contoso " + _run };
        if (primaryContact is not null) account["primarycontactid"] = primaryContact;
        return new EntityReference("account", await Admin.CreateAsync(account));
    }

    private async Task<EntityReference> OrderAsync(decimal? amount, EntityReference? account = null)
    {
        var order = new Entity(Order) { ["xrmv_name"] = "Order " + _run, ["xrmv_amount"] = amount };
        if (account is not null) order["xrmv_accountid"] = account;
        return new EntityReference(Order, await Admin.CreateAsync(order));
    }

    private async Task<List<Entity>> TasksRegardingAsync(Guid id) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression("task")
        {
            ColumnSet = new ColumnSet("subject", "prioritycode", "regardingobjectid", "isworkflowcreated"),
            Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, id) } },
        })).Entities.ToList();

    private async Task<List<string>> PipeLogAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command($"SELECT pb_name FROM data.{TestEntities.PipeLog}", conn);
        var names = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) names.Add(rd.GetString(0));
        return names;
    }

    // ---- record steps -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_step_creates_record_with_dynamic_values()
    {
        var account = await AccountAsync();
        var order = await OrderAsync(10m, account);
        // The export's create step (regarding = the order's primary key as a reference, a literal priority), with its subject made
        // dynamic: the name of the order's account.
        var create = Step<CreateStep>(Real(OnCreate));
        var values = create.Values.ToDictionary(v => v.Key, v => v.Value);
        values["subject"] = new Concat([new Literal("String", "Follow up "), new AttributeOf(new RecordRef.Related("xrmv_accountid"), "name")]);
        var run = await RunAsync(order);

        var outcome = await Engine().RunAsync(Only(Real(OnCreate), create with { Values = values }), run, default);

        outcome.Should().Be(new WorkflowOutcome.Completed());
        var task = (await TasksRegardingAsync(order.Id)).Should().ContainSingle().Subject;
        task.GetAttributeValue<string>("subject").Should().Be("Follow up Contoso " + _run);
        task.GetAttributeValue<OptionSetValue>("prioritycode").Value.Should().Be(1);
        task.GetAttributeValue<EntityReference>("regardingobjectid").Should().BeEquivalentTo(new { LogicalName = Order, order.Id });
        run.Variables[create.Variable].Should().BeEquivalentTo(new { LogicalName = "task", task.Id });
    }

    [Fact]
    public async Task Created_activity_has_isworkflowcreated_true()
    {
        var order = await OrderAsync(1m);
        var d = Real(OnCreate);

        (await Engine().RunAsync(Only(d, Step<CreateStep>(d)), await RunAsync(order), default)).Should().Be(new WorkflowOutcome.Completed());

        var task = (await TasksRegardingAsync(order.Id)).Should().ContainSingle().Subject;
        (task.GetAttributeValue<string>("subject"), task.GetAttributeValue<bool>("isworkflowcreated")).Should().Be(("TEST", true));
    }

    [Fact]
    public async Task Update_primary_issues_update_with_values()
    {
        // The export's action: amount = amount + Increment, Total = the amount, stop canceled when Increment is negative.
        var order = await OrderAsync(10m);
        var run = await RunAsync(order, stage: 30);
        run.Variables["Increment"] = 5m;

        var outcome = await Engine().RunAsync(Real(Recalculate, 3), run, default);

        outcome.Should().Be(new WorkflowOutcome.Completed());
        (await Admin.RetrieveAsync(Order, order.Id, new ColumnSet("xrmv_amount"))).GetAttributeValue<decimal>("xrmv_amount").Should().Be(15m);
        run.Variables["Total"].Should().Be(15m);
    }

    [Fact]
    public async Task Update_primary_at_pre_operation_writes_the_target()
    {
        // W8: stage 20 reads the stored row overlaid with Target and writes into Target instead of issuing an Update.
        var order = await OrderAsync(1m);
        var target = new Entity(Order, order.Id) { ["xrmv_amount"] = 10m };
        var run = await RunAsync(order, stage: 20, target: target, message: "Update");
        run.Variables["Increment"] = 5m;
        var d = Real(Recalculate, 3);

        (await Engine().RunAsync(Only(d, Step<UpdateStep>(d)), run, default)).Should().Be(new WorkflowOutcome.Completed());

        target["xrmv_amount"].Should().Be(15m);
        (await Admin.RetrieveAsync(Order, order.Id, new ColumnSet("xrmv_amount"))).GetAttributeValue<decimal>("xrmv_amount").Should().Be(1m);
    }

    [Fact]
    public async Task Update_primary_at_pre_operation_create_writes_the_target_without_an_id()
    {
        // W8 on a Create: at stage 20 the record has no id yet (the core makes it at stage 30).
        var target = new Entity(Order) { ["xrmv_amount"] = 10m };
        var run = await RunAsync(new EntityReference(Order, Guid.Empty), stage: 20, target: target, message: "Create");
        run.Variables["Increment"] = 5m;
        var d = Real(Recalculate, 3);

        (await Engine().RunAsync(Only(d, Step<UpdateStep>(d)), run, default)).Should().Be(new WorkflowOutcome.Completed());

        target["xrmv_amount"].Should().Be(15m);
        target.Id.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task Pre_operation_create_reads_the_primary_key_the_client_set()
    {
        var id = Guid.NewGuid();
        var target = new Entity(Order) { ["xrmv_orderid"] = id };
        var run = await RunAsync(new EntityReference(Order, Guid.Empty), stage: 20, target: target, message: "Create");
        // The export's primary key read (TargetType EntityReference), written into a column of the Target.
        var self = Step<CreateStep>(Real(OnCreate)).Values["regardingobjectid"];
        var update = new UpdateStep("0/0", "UpdateStep1", new RecordRef.Primary(), new Dictionary<string, ValueExpr> { ["xrmv_self"] = self });

        (await Engine().RunAsync(Only(Real(OnCreate), update), run, default)).Should().Be(new WorkflowOutcome.Completed());

        target["xrmv_self"].Should().BeEquivalentTo(new EntityReference(Order, id));
    }

    [Fact]
    public async Task Update_related_record_through_lookup()
    {
        var account = await AccountAsync();
        var order = await OrderAsync(1m, account);
        var update = new UpdateStep("0/0", "UpdateStep1", new RecordRef.Related("xrmv_accountid"), new Dictionary<string, ValueExpr>
        {
            ["name"] = new Concat([new Literal("String", "Account of "), new AttributeOf(new RecordRef.Primary(), "xrmv_name")]),
        });

        (await Engine().RunAsync(Only(Real(OnCreate), update), await RunAsync(order), default)).Should().Be(new WorkflowOutcome.Completed());

        (await Admin.RetrieveAsync("account", account.Id, new ColumnSet("name"))).GetAttributeValue<string>("name")
            .Should().Be($"Account of Order {_run}");
    }

    [Fact]
    public async Task Assign_step_changes_owner()
    {
        var account = await AccountAsync();
        var root = await RootBusinessUnitAsync(fx);
        var assignee = await CreateUserAsync(fx, root);
        await GrantAsync(fx, assignee, false, ("prvReadAccount", DepthMask.Global));
        var d = Provisional("background-wait");
        var assign = Step<AssignStep>(d) with { Owner = new Literal("EntityReference", new EntityRefValue("systemuser", assignee)) };

        (await Engine().RunAsync(Only(d, assign), await RunAsync(account), default)).Should().Be(new WorkflowOutcome.Completed());

        (await Admin.RetrieveAsync("account", account.Id, new ColumnSet("ownerid"))).GetAttributeValue<EntityReference>("ownerid").Id
            .Should().Be(assignee);
    }

    [Fact]
    public async Task SetState_step_changes_state_and_status()
    {
        var account = await AccountAsync();
        var d = Provisional("background-wait");
        var setState = Step<SetStateStep>(d, s => s.State == 1);

        (await Engine().RunAsync(Only(d, setState), await RunAsync(account), default)).Should().Be(new WorkflowOutcome.Completed());

        var stored = await Admin.RetrieveAsync("account", account.Id, new ColumnSet("statecode", "statuscode"));
        (stored.GetAttributeValue<OptionSetValue>("statecode").Value, stored.GetAttributeValue<OptionSetValue>("statuscode").Value)
            .Should().Be((setState.State, setState.Status));
    }

    // ---- conditions and stop -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Condition_takes_first_true_branch_else_otherwise()
    {
        // The export's business rule at stage 20 of a Create: amount empty → set the status; otherwise → show its error.
        var d = Real(BusinessRule, 2);
        var primary = new EntityReference(Order, Guid.NewGuid());

        var empty = new Entity(Order, primary.Id) { ["xrmv_name"] = "No amount" };
        (await Engine().RunAsync(d, await RunAsync(primary, stage: 20, target: empty, message: "Create"), default))
            .Should().Be(new WorkflowOutcome.Completed());
        empty.GetAttributeValue<OptionSetValue>("xrmv_status").Value.Should().Be(128830001);

        var filled = new Entity(Order, primary.Id) { ["xrmv_amount"] = 5m };
        (await Engine().RunAsync(d, await RunAsync(primary, stage: 20, target: filled, message: "Create"), default))
            .Should().Be(new WorkflowOutcome.Canceled("fdsfds"));
        filled.Contains("xrmv_status").Should().BeFalse();
    }

    [Fact]
    public async Task Stop_succeeded_returns_completed()
    {
        var order = await OrderAsync(1m);
        var d = Real(OnCreate);
        var create = Step<CreateStep>(d) with { Path = "0/1" };

        var outcome = await Engine().RunAsync(Only(d, new StopStep("0/0", "StopWorkflowStep1", Canceled: false, Message: null), create),
            await RunAsync(order), default);

        outcome.Should().Be(new WorkflowOutcome.Completed());
        (await TasksRegardingAsync(order.Id)).Should().BeEmpty("the steps after a stop do not run");
    }

    [Fact]
    public async Task Stop_canceled_returns_canceled_with_message()
    {
        // The export's action with a negative increment: the update runs (and stays: background, W9), then the stop.
        var order = await OrderAsync(10m);
        var run = await RunAsync(order, stage: 0);
        run.Variables["Increment"] = -1m;
        (await Engine().RunAsync(Real(Recalculate, 3), run, default)).Should().Be(new WorkflowOutcome.Canceled(""));
        (await Admin.RetrieveAsync(Order, order.Id, new ColumnSet("xrmv_amount"))).GetAttributeValue<decimal>("xrmv_amount").Should().Be(9m);

        // A stop with a message (provisional shape: the message in the exception).
        var d = Provisional("child-and-stop");
        (await Engine().RunAsync(Only(d, Step<StopStep>(d)), await RunAsync(await AccountAsync()), default))
            .Should().Be(new WorkflowOutcome.Canceled("The credit check failed"));
    }

    // ---- identity, depth and plug-ins -----------------------------------------------------------------------------------

    [Fact]
    public async Task Steps_run_with_run_caller_privileges()
    {
        var order = await OrderAsync(1m);
        var root = await RootBusinessUnitAsync(fx);
        var user = await CreateUserAsync(fx, root);
        await GrantAsync(fx, user, false, ("prvReadxrmv_Order", DepthMask.Global));
        var d = Real(OnCreate);
        var run = await RunAsync(order, new Caller(user, root));

        var fault = await TestHelpers.FaultAsync(() => Engine().RunAsync(Only(d, Step<CreateStep>(d)), run, default));

        fault.HttpStatus.Should().Be(403);
        (await TasksRegardingAsync(order.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Plugin_fires_on_workflow_step_write()
    {
        var order = await OrderAsync(1m);
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.CreateRecordPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", "task", 40, config: TestEntities.PipeLog);
        var d = Real(OnCreate);

        (await Engine().RunAsync(Only(d, Step<CreateStep>(d)), await RunAsync(order), default)).Should().Be(new WorkflowOutcome.Completed());

        var task = (await TasksRegardingAsync(order.Id)).Should().ContainSingle().Subject;
        (await PipeLogAsync()).Should().Contain($"Create|40|2|{task.Id}", "the run is at depth 1 and its steps one level deeper");
    }

    // ---- send email ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Send_email_step_creates_email_and_leaves_it_pending_send()
    {
        var contact = await ContactAsync();
        var order = await OrderAsync(150m, await AccountAsync(contact));
        // The export's send email step: to = the primary contact of the order's account, regarding the order. Its other columns
        // (deliveryprioritycode, notifications, ... and a currency of the exporting organization) are not on xrmverse's email.
        var send = Step<SendEmailStep>(Real(OnUpdate));
        var values = send.Values.Where(v => v.Key is "to" or "regardingobjectid" or "directioncode" or "prioritycode")
            .ToDictionary(v => v.Key, v => v.Value);
        values["subject"] = new Literal("String", "Order " + _run);
        var run = await RunAsync(order);

        (await Engine().RunAsync(Only(Real(OnUpdate), send with { Values = values }), run, default)).Should().Be(new WorkflowOutcome.Completed());

        var emailRef = run.Variables[send.CreatedEmailVariable!].Should().BeOfType<EntityReference>().Subject;
        var email = await Admin.RetrieveAsync("email", emailRef.Id,
            new ColumnSet("statecode", "statuscode", "subject", "to", "regardingobjectid", "isworkflowcreated"));
        (email.GetAttributeValue<OptionSetValue>("statecode").Value, email.GetAttributeValue<OptionSetValue>("statuscode").Value).Should().Be((1, 6));
        email.GetAttributeValue<string>("subject").Should().Be("Order " + _run);
        email.GetAttributeValue<bool>("isworkflowcreated").Should().BeTrue();
        email.GetAttributeValue<EntityReference>("regardingobjectid").Id.Should().Be(order.Id);
        email.GetAttributeValue<EntityCollection>("to").Entities.Should().ContainSingle()
            .Which.GetAttributeValue<EntityReference>("partyid").Id.Should().Be(contact.Id);
    }

    // ---- waits, resume and unsupported steps ----------------------------------------------------------------------------

    [Fact]
    public async Task Wait_suspends_on_the_record_of_its_condition()
    {
        // The export's background workflow waits for amount > 100 before its send email step.
        var order = await OrderAsync(50m);
        var d = Real(OnUpdate);
        var wait = Step<WaitStep>(d);

        var outcome = await Engine().RunAsync(d, await RunAsync(order), default);

        outcome.Should().Be(new WorkflowOutcome.Suspended(null, order, wait.Path));
        var resumed = await RunAsync(order);
        resumed.ResumePath = wait.Path;
        (await Engine().RunAsync(d, resumed, default)).Should().Be(new WorkflowOutcome.Suspended(null, order, wait.Path), "the condition is still false");
    }

    [Fact]
    public async Task Resumed_wait_whose_condition_now_holds_goes_on()
    {
        var contact = await ContactAsync();
        var order = await OrderAsync(150m, await AccountAsync(contact));
        // The export's wait and send email step (its email columns xrmverse has, as in the send email test).
        var d = Real(OnUpdate);
        var block = (SequenceStep)((SequenceStep)d.Root).Steps[0];
        var then = (SequenceStep)block.Steps[1];
        var send = (SendEmailStep)then.Steps[0];
        var values = send.Values.Where(v => v.Key is "to" or "regardingobjectid").ToDictionary(v => v.Key, v => v.Value);
        values["subject"] = new Literal("String", "Resumed " + _run);
        var tree = d with
        {
            Root = (SequenceStep)d.Root with { Steps = [block with { Steps = [block.Steps[0], then with { Steps = [send with { Values = values }] }] }] },
        };
        var run = await RunAsync(order);
        run.ResumePath = block.Steps[0].Path;

        (await Engine().RunAsync(tree, run, default)).Should().Be(new WorkflowOutcome.Completed());

        run.Variables.Should().ContainKey(send.CreatedEmailVariable!);
        var email = await Admin.RetrieveAsync("email", ((EntityReference)run.Variables[send.CreatedEmailVariable!]!).Id, new ColumnSet("subject"));
        email.GetAttributeValue<string>("subject").Should().Be("Resumed " + _run);
    }

    private static UpdateStep SetName(string path, string name) =>
        new(path, null, new RecordRef.Primary(), new Dictionary<string, ValueExpr> { ["xrmv_name"] = new Literal("String", name) });

    private static ConditionExpr AmountAbove(decimal amount) =>
        new ConditionExpr.Compare("GreaterThan", new AttributeOf(new RecordRef.Primary(), "xrmv_amount"), new Literal("Decimal", amount));

    [Fact]
    public async Task Wait_timeout_runs_its_block_and_skips_the_rest_of_its_sequence()
    {
        var order = await OrderAsync(50m);
        var past = new Literal("DateTime", DateTime.UtcNow.AddDays(-1));
        var tree = Only(Real(OnCreate),
            new SequenceStep("0/0", "WaitBlock", [
                new WaitStep("0/0/0", "WaitStep1", AmountAbove(100m), past, new SequenceStep("0/0/0/t", null, [SetName("0/0/0/t/0", "timed out")])),
                SetName("0/0/1", "after the wait"),
            ]),
            new UpdateStep("0/1", null, new RecordRef.Primary(), new Dictionary<string, ValueExpr> { ["xrmv_amount"] = new Literal("Decimal", 1m) }));

        (await Engine().RunAsync(tree, await RunAsync(order), default)).Should().Be(new WorkflowOutcome.Completed());

        var stored = await Admin.RetrieveAsync(Order, order.Id, new ColumnSet("xrmv_name", "xrmv_amount"));
        stored.GetAttributeValue<string>("xrmv_name").Should().Be("timed out", "the steps after the wait in its sequence are skipped");
        stored.GetAttributeValue<decimal>("xrmv_amount").Should().Be(1m, "the steps after the wait's sequence run");
    }

    [Fact]
    public async Task Resume_inside_a_condition_branch_does_not_evaluate_the_condition_again()
    {
        var order = await OrderAsync(50m);
        var tree = Only(Real(OnCreate), new ConditionStep("0/0", "ConditionStep1",
            [new ConditionBranch(AmountAbove(100m), new SequenceStep("0/0/b", null, [
                new WaitStep("0/0/b/0", "WaitStep2", new ConditionExpr.Not(AmountAbove(1000m)), null, null),
                SetName("0/0/b/1", "branch"),
            ]))],
            SetName("0/0/o", "otherwise")));
        var run = await RunAsync(order);
        run.ResumePath = "0/0/b/0";

        (await Engine().RunAsync(tree, run, default)).Should().Be(new WorkflowOutcome.Completed());

        (await Admin.RetrieveAsync(Order, order.Id, new ColumnSet("xrmv_name"))).GetAttributeValue<string>("xrmv_name").Should().Be("branch");
    }

    [Fact]
    public async Task Postpone_with_an_empty_date_fails_naming_the_step()
    {
        var order = await OrderAsync(50m);
        var tree = Only(Real(OnCreate), new WaitStep("0/0", "PostponeStep1", null, new Literal("Null", null), null));
        var run = await RunAsync(order);

        (await TestHelpers.BadRequestAsync(() => Engine().RunAsync(tree, run, default))).Should().Contain("0/0 (PostponeStep1)");
    }

    [Fact]
    public async Task Wait_on_no_record_without_timeout_fails()
    {
        var order = await OrderAsync(50m);
        var until = new ConditionExpr.Compare("GreaterThan", new ExecutionTime(), new Literal("DateTime", DateTime.UtcNow.AddDays(1)));
        var tree = Only(Real(OnCreate), new WaitStep("0/0", "WaitStep1", until, null, null));
        var run = await RunAsync(order);

        (await TestHelpers.BadRequestAsync(() => Engine().RunAsync(tree, run, default))).Should().Contain("0/0 (WaitStep1)").And.Contain("no record");
    }

    [Fact]
    public async Task Resumed_run_skips_completed_steps()
    {
        var order = await OrderAsync(1m);
        var d = Real(OnCreate);
        var create = Step<CreateStep>(d) with { Path = "0/0" };
        var update = new UpdateStep("0/1", "UpdateStep2", new RecordRef.Primary(),
            new Dictionary<string, ValueExpr> { ["xrmv_amount"] = new Literal("Decimal", 7m) });
        var run = await RunAsync(order);
        run.ResumePath = "0/1";

        (await Engine().RunAsync(Only(d, create, update), run, default)).Should().Be(new WorkflowOutcome.Completed());

        (await TasksRegardingAsync(order.Id)).Should().BeEmpty("the create step ran before the run was suspended");
        (await Admin.RetrieveAsync(Order, order.Id, new ColumnSet("xrmv_amount"))).GetAttributeValue<decimal>("xrmv_amount").Should().Be(7m);
        run.ResumePath.Should().BeNull();
    }

    [Fact]
    public async Task Definition_with_unsupported_steps_does_not_run()
    {
        var account = await AccountAsync();
        var d = Provisional("unsupported-expression");
        var run = await RunAsync(account);

        var message = await TestHelpers.BadRequestAsync(() => Engine().RunAsync(d, run, default));

        message.Should().StartWith($"Workflow 'Engine test {_run}' cannot run on xrmverse: ").And.Contain(d.Unsupported[0].Reason);
    }
}
