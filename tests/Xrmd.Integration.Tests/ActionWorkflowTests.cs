using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.Workflows;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.WorkflowTestHelper;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.14, Task 7 (W13, W14): XAML actions (category 3) answer their own <c>sdkmessage</c>, created at their activation (or at the
/// import of their solution), run their steps at stage 30 of their message in the caller's transaction, and return their output
/// arguments; the "perform action" step calls a message through the whole pipeline. The parity export's Recalculate (bound to
/// <c>xrmv_order</c>: <c>Target</c>, <c>Increment</c> Decimal in, <c>Total</c> Decimal out; update, output assignment, a stop when the
/// increment is negative) gives the action shapes it has; the provisional <c>action-echo</c> (global) and <c>action-call</c> the others.
/// </summary>
[Collection("xrmd")]
public sealed class ActionWorkflowTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Order = TestEntities.Order;
    private const string Recalculate = "Recalculate-9DF696A1-E6DE-F111-84E8-002248872B66";
    private const string Plugins = "Xrmd.TestPlugins.";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsureOrderAsync(store);
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await ClearActionsAsync(fx);
    }

    public Task DisposeAsync() => ClearActionsAsync(fx);

    /// <summary>
    /// No process left activated, no step, and no action definition (a Draft action keeps its message, whose name a later test's
    /// action would clash with).
    /// </summary>
    internal static async Task ClearActionsAsync(XrmdFixture fx)
    {
        await ClearAsync(fx);
        await PluginTestHelper.ClearAsync(fx);
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var actions = await admin.RetrieveMultipleAsync(new QueryExpression(S.Table)
        {
            ColumnSet = new ColumnSet(false),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression(S.CategoryColumn, ConditionOperator.Equal, (int)S.Category.Action),
                    new ConditionExpression(S.TypeColumn, ConditionOperator.Equal, (int)S.Type.Definition),
                },
            },
        });
        foreach (var action in actions.Entities) await admin.DeleteAsync(S.Table, action.Id);
        fx.App.Services.GetRequiredService<WorkflowRegistry>().Invalidate();
    }

    // ---- processes -------------------------------------------------------------------------------------------------------

    private string Name(string what) => $"{what} {_run}";

    /// <summary>A Draft action (category 3) of <paramref name="xaml"/> named <paramref name="uniqueName"/>, run as the caller.</summary>
    private static WorkflowRow Action(string name, string uniqueName, string xaml, string primaryEntity) => new()
    {
        Id = Guid.NewGuid(), Name = name, Category = 3, Mode = 0, Type = 1, Scope = 4, RunAs = 1, PrimaryEntity = primaryEntity,
        UniqueName = uniqueName, Xaml = xaml,
    };

    /// <summary>The export's Recalculate, saved with a unique name of this run that already carries a prefix (its message name).</summary>
    private WorkflowRow RecalculateAction() => Action(Name("Recalculate"), $"new_Recalc{_run}", Real(Recalculate), Order);

    /// <summary>The provisional action-echo without its own action call: InText → OutText and a task with the text as subject.</summary>
    private WorkflowRow EchoAction(string? uniqueName = null) =>
        Action(Name("Echo"), uniqueName ?? $"new_Echo{_run}", Without(WorkflowFixtures.Xaml("action-echo"), "ActionStep2"), S.NoPrimaryEntity);

    private Task<WorkflowRow> ActivateAsync(WorkflowRow row) => WorkflowTestHelper.ActivateAsync(fx, Admin, row);

    // ---- data -------------------------------------------------------------------------------------------------------------

    private async Task<Guid> OrderAsync(decimal amount, string? name = null) =>
        await Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = name ?? Name("Order"), ["xrmv_amount"] = amount });

    private async Task<decimal?> AmountAsync(Guid order) =>
        (await Admin.RetrieveAsync(Order, order, new ColumnSet("xrmv_amount"))).GetAttributeValue<decimal?>("xrmv_amount");

    private Task<OrganizationResponse> RecalculateAsync(string message, Guid order, object? increment) =>
        Admin.ExecuteAsync(new OrganizationRequest(message) { ["Target"] = new EntityReference(Order, order), ["Increment"] = increment });

    private async Task<List<Entity>> RowsAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<Entity>();
        while (await rd.ReadAsync())
        {
            var row = new Entity();
            for (var i = 0; i < rd.FieldCount; i++) row[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private Task<List<Entity>> MessagesAsync(string name) =>
        RowsAsync("SELECT sdkmessageid, name, categoryname FROM data.sdkmessage WHERE name = @name", ("@name", name));

    private Task<List<Entity>> FiltersAsync(Guid message) =>
        RowsAsync("SELECT sdkmessagefilterid, primaryobjecttypecode, iscustomprocessingstepallowed FROM data.sdkmessagefilter WHERE sdkmessageid = @m",
            ("@m", message));

    private async Task<Guid?> MessageIdOfAsync(Guid workflowId) =>
        (await Admin.RetrieveAsync(S.Table, workflowId, new ColumnSet(S.SdkMessageId))).GetAttributeValue<EntityReference>(S.SdkMessageId)?.Id;

    private async Task<List<string>> PipeLogAsync(string prefix) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression(TestEntities.PipeLog)
        {
            ColumnSet = new ColumnSet("pb_name", "pb_data"),
            Criteria = { Conditions = { new ConditionExpression("pb_name", ConditionOperator.BeginsWith, prefix) } },
            Orders = { new OrderExpression("createdon", OrderType.Ascending) },
        })).Entities.Select(e => e.GetAttributeValue<string>("pb_name") + "\n" + e.GetAttributeValue<string>("pb_data")).ToList();

    private async Task RegisterStepAsync(string plugin, string message, string? entity, int stage, string? config = null)
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes, "Xrmd.TestPlugins." + Guid.NewGuid().ToString("N")[..6]);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Plugins + plugin);
        await PluginTestHelper.RegisterStepAsync(Admin, type, message, entity, stage, config: config);
    }

    // ---- the message ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Activating_action_creates_its_message()
    {
        // The export's Recalculate in a solution of the publisher xw, Draft as the export has it, with an SdkMessageId (as an export
        // carries it): the import gives it the message xw_Recalc… with that id, bound to xrmv_order.
        var preferred = Guid.NewGuid();
        var process = new TestProcess(Name("Recalculate"), "real/" + Recalculate, Category: 3, PrimaryEntity: Order, StateCode: 0,
            TriggerOnCreate: false, UniqueName: $"Recalc{_run}");
        var messageName = $"{WorkflowSolutionBuilder.Prefix}_Recalc{_run}";
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = WorkflowSolutionBuilder.Build($"xw_recalc_{_run}", false, entries =>
            {
                var customizations = XDocument.Load(new MemoryStream(entries["customizations.xml"]));
                customizations.Root!.Element("Workflows")!.Element("Workflow")!.Add(new XElement("SdkMessageId", preferred.ToString("B")));
                entries["customizations.xml"] = Encoding.UTF8.GetBytes(customizations.ToString());
            }, process),
            ImportJobId = Guid.NewGuid(),
        });
        var message = (await MessagesAsync(messageName)).Should().ContainSingle().Subject;
        (message["sdkmessageid"], message["categoryname"]).Should().Be((preferred, "CustomAction"));
        (await MessageIdOfAsync(process.Id)).Should().Be(preferred);
        (await FiltersAsync(preferred)).Should().ContainSingle().Which["primaryobjecttypecode"].Should().Be(Order);

        await WorkflowTestHelper.SetStateAsync(Admin, process.Id, S.ActivatedState, S.ActivatedStatus);
        (await MessagesAsync(messageName)).Should().ContainSingle("the activation keeps the message the import created");
        var activation = (await Admin.RetrieveAsync(S.Table, process.Id, new ColumnSet(S.ActiveWorkflowId))).GetAttributeValue<EntityReference>(S.ActiveWorkflowId);
        (await MessageIdOfAsync(activation.Id)).Should().Be(preferred, "the activation is a copy of the definition");

        var order = await OrderAsync(10m);
        var response = await RecalculateAsync(messageName, order, 5m);
        response.Results["Total"].Should().Be(15m);
        (await AmountAsync(order)).Should().Be(15m);

        // A process saved without a solution: the default publisher's prefix (new) names its message, with an id derived from the name.
        var echo = await ActivateAsync(EchoAction($"Echo{_run}"));
        var echoMessage = (await MessagesAsync($"new_Echo{_run}")).Should().ContainSingle().Subject;
        echoMessage["sdkmessageid"].Should().Be(SdkMessageCatalog.MessageId($"new_Echo{_run}"));
        (await MessageIdOfAsync(echo.Id)).Should().Be(SdkMessageCatalog.MessageId($"new_Echo{_run}"));

        // Deactivation keeps the message (plug-in steps may be registered on it), and a call to it is refused, never run as a
        // message without a main operation.
        await DeactivateAsync(Admin, process.Id);
        (await MessagesAsync(messageName)).Should().ContainSingle();
        var fault = await TestHelpers.FaultAsync(() => RecalculateAsync(messageName, order, 5m));
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().Contain("not activated");
        (await AmountAsync(order)).Should().Be(15m);

        // Activated again, it keeps its message; deleted (Draft), the message and its filter go with it.
        await WorkflowTestHelper.SetStateAsync(Admin, process.Id, S.ActivatedState, S.ActivatedStatus);
        (await MessagesAsync(messageName)).Should().ContainSingle().Which["sdkmessageid"].Should().Be(preferred);
        await DeactivateAsync(Admin, process.Id);
        await Admin.DeleteAsync(S.Table, process.Id);
        (await MessagesAsync(messageName)).Should().BeEmpty();
        (await FiltersAsync(preferred)).Should().BeEmpty();
        // The message is gone: the request is one the server does not know (as any unknown request name).
        await FluentActions.Awaiting(() => RecalculateAsync(messageName, order, 5m)).Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Global_and_bound_actions_get_filters()
    {
        var bound = await ActivateAsync(RecalculateAction());
        var global = await ActivateAsync(EchoAction());

        var boundFilter = (await FiltersAsync((await MessageIdOfAsync(bound.Id))!.Value)).Should().ContainSingle().Subject;
        (boundFilter["primaryobjecttypecode"], boundFilter["iscustomprocessingstepallowed"]).Should().Be((Order, true));
        var globalFilter = (await FiltersAsync((await MessageIdOfAsync(global.Id))!.Value)).Should().ContainSingle().Subject;
        globalFilter["primaryobjecttypecode"].Should().Be("none");
    }

    // ---- a call -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Action_runs_xaml_and_returns_outputs()
    {
        var echo = await ActivateAsync(EchoAction());

        var response = await Admin.ExecuteAsync(new OrganizationRequest(echo.UniqueName) { ["InText"] = Name("Hello") });

        response.ResponseName.Should().Be(echo.UniqueName);
        response.Results.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, object>("OutText", Name("Hello")));
        (await Admin.RetrieveMultipleAsync(new QueryExpression("task")
        {
            Criteria = { Conditions = { new ConditionExpression("subject", ConditionOperator.Equal, Name("Hello")) } },
        })).Entities.Should().ContainSingle("the action's create step ran");

        // The export's Recalculate: its update ran and its output assignment read the updated amount.
        var recalculate = await ActivateAsync(RecalculateAction());
        var order = await OrderAsync(10m);
        (await RecalculateAsync(recalculate.UniqueName!, order, 2.5m)).Results.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, object>("Total", 12.5m));
        (await AmountAsync(order)).Should().Be(12.5m);
    }

    [Fact]
    public async Task Missing_required_input_is_400_0x80040203()
    {
        var recalculate = await ActivateAsync(RecalculateAction());
        var order = await OrderAsync(10m);

        var missing = await TestHelpers.FaultAsync(() => Admin.ExecuteAsync(
            new OrganizationRequest(recalculate.UniqueName) { ["Target"] = new EntityReference(Order, order) }));
        (missing.HttpStatus, missing.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        missing.Message.Should().Contain("'Increment'");

        var noTarget = await TestHelpers.FaultAsync(() => Admin.ExecuteAsync(new OrganizationRequest(recalculate.UniqueName) { ["Increment"] = 1m }));
        (noTarget.HttpStatus, noTarget.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        noTarget.Message.Should().Contain("Target");
        (await AmountAsync(order)).Should().Be(10m, "nothing of the action ran");
    }

    [Fact]
    public async Task Wrong_input_type_is_400()
    {
        var recalculate = await ActivateAsync(RecalculateAction());
        var order = await OrderAsync(10m);

        // Workflows read values, they do not convert them: a text or a double is not a Decimal.
        foreach (var wrong in new object[] { "5", 5d, 5 })
        {
            var fault = await TestHelpers.FaultAsync(() => RecalculateAsync(recalculate.UniqueName!, order, wrong));
            (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
            fault.Message.Should().Contain("'Increment'").And.Contain("Decimal");
        }
        var otherTable = await TestHelpers.FaultAsync(() => Admin.ExecuteAsync(new OrganizationRequest(recalculate.UniqueName)
        {
            ["Target"] = new EntityReference("account", Guid.NewGuid()), ["Increment"] = 1m,
        }));
        (otherTable.HttpStatus, otherTable.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        otherTable.Message.Should().Contain("'Target'").And.Contain($"'{Order}'").And.Contain("'account'");
        var undeclared = await TestHelpers.FaultAsync(() => Admin.ExecuteAsync(new OrganizationRequest(recalculate.UniqueName)
        {
            ["Target"] = new EntityReference(Order, order), ["Increment"] = 1m, ["Other"] = 1m,
        }));
        (undeclared.HttpStatus, undeclared.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        undeclared.Message.Should().Contain("no request parameter named 'Other'");
        (await AmountAsync(order)).Should().Be(10m);
    }

    [Fact]
    public async Task Plugin_steps_on_action_message_fire_around_stage_30()
    {
        var recalculate = await ActivateAsync(RecalculateAction());
        var message = recalculate.UniqueName!;
        await RegisterStepAsync("ContextRecorderPlugin", message, Order, 20, TestEntities.PipeLog);
        await RegisterStepAsync("ContextRecorderPlugin", message, Order, 40, TestEntities.PipeLog);
        var order = await OrderAsync(10m);

        (await RecalculateAsync(message, order, 1m)).Results["Total"].Should().Be(11m);

        // Stage 20 before the XAML (no output yet), stage 40 after it (its output set), each once, synchronous, depth 1.
        var log = await PipeLogAsync(message + "|");
        log.Should().HaveCount(2);
        log[0].Should().StartWith($"{message}|20|0|1\n").And.Contain("\noutput=\n");
        log[1].Should().StartWith($"{message}|40|0|1\n").And.Contain("\noutput=Total\n");
    }

    /// <summary>A user of the root business unit with <paramref name="privileges"/>, and its service.</summary>
    private async Task<(Guid User, OrganizationService Service)> UserAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        if (privileges.Length > 0) await GrantAsync(fx, user, team: false, privileges);
        return (user, ServiceFor(fx, objectId));
    }

    [Fact]
    public async Task Action_runs_as_calling_user_whatever_its_runas()
    {
        // Learn (create-actions): "Actions always run in the context of the calling user." An action saved with runas 0 (owner) and owned
        // by the administrator still runs its update as the caller, who may read orders but not write them.
        var admin = (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId;
        var recalculate = await ActivateAsync(RecalculateAction() with { RunAs = 0, OwnerId = new EntityReference("systemuser", admin) });
        var order = await OrderAsync(10m);
        var (user, caller) = await UserAsync(("prvReadxrmv_Order", DepthMask.Global));

        var fault = await TestHelpers.FaultAsync(() => caller.ExecuteAsync(new OrganizationRequest(recalculate.UniqueName)
        {
            ["Target"] = new EntityReference(Order, order), ["Increment"] = 1m,
        }));
        fault.HttpStatus.Should().Be(403, "the action's update runs with the caller's privileges, not its owner's");
        (await AmountAsync(order)).Should().Be(10m);

        await GrantAsync(fx, user, team: false, ("prvWritexrmv_Order", DepthMask.Global));
        (await caller.ExecuteAsync(new OrganizationRequest(recalculate.UniqueName)
        {
            ["Target"] = new EntityReference(Order, order), ["Increment"] = 1m,
        })).Results["Total"].Should().Be(11m);
        (await Admin.RetrieveAsync(Order, order, new ColumnSet("modifiedby"))).GetAttributeValue<EntityReference>("modifiedby").Id
            .Should().Be(user, "the update was the caller's");
    }

    [Fact]
    public async Task Action_failure_rolls_back_caller_transaction()
    {
        var recalculate = await ActivateAsync(RecalculateAction());
        var order = await OrderAsync(10m);

        // A negative increment: the action updates the amount, then its stop step cancels it (the export's condition).
        var fault = await TestHelpers.FaultAsync(() => RecalculateAsync(recalculate.UniqueName!, order, -1m));
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.ISVAborted));
        (await AmountAsync(order)).Should().Be(10m, "the action's update rolled back with it");

        // Called from a real-time workflow on the Update of the order's status: the whole Update rolls back.
        var call = Replace(Replace(WorkflowFixtures.Xaml("action-call"), "xrmv_Recalculate", recalculate.UniqueName!),
            "WorkflowPropertyType.Decimal, \"5\"", "WorkflowPropertyType.Decimal, \"-1\"");
        await ActivateAsync(Realtime(Name("Call"), call, Order) with { TriggerOnUpdateAttributes = "xrmv_status", UpdateStage = 40 });
        var update = await TestHelpers.FaultAsync(() => Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_status"] = new OptionSetValue(128830001) }));
        (update.HttpStatus, update.ErrorCode).Should().Be((400, ErrorCodes.ISVAborted));
        var stored = await Admin.RetrieveAsync(Order, order, new ColumnSet("xrmv_status", "xrmv_amount"));
        stored.GetAttributeValue<OptionSetValue>("xrmv_status")?.Value.Should().NotBe(128830001);
        stored.GetAttributeValue<decimal?>("xrmv_amount").Should().Be(10m);
    }

    [Fact]
    public async Task Action_call_step_runs_action_from_workflow()
    {
        // W13: the provisional action-call (on the Update of an order's status) calls Recalculate on the order with 5, then creates a
        // copy of the order whose amount is the action's Total output.
        var recalculate = await ActivateAsync(RecalculateAction());
        var message = recalculate.UniqueName!;
        await RegisterStepAsync("ContextRecorderPlugin", message, Order, 40, TestEntities.PipeLog);
        await ActivateAsync(Realtime(Name("Call"), Replace(WorkflowFixtures.Xaml("action-call"), "xrmv_Recalculate", message), Order) with
        {
            TriggerOnUpdateAttributes = "xrmv_status", UpdateStage = 40,
        });
        var name = Name("Called");
        var order = await OrderAsync(10m, name);

        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_status"] = new OptionSetValue(128830001) });

        (await AmountAsync(order)).Should().Be(15m);
        var copies = (await Admin.RetrieveMultipleAsync(new QueryExpression(Order)
        {
            ColumnSet = new ColumnSet("xrmv_amount"),
            Criteria = { Conditions = { new ConditionExpression("xrmv_name", ConditionOperator.Equal, name) } },
        })).Entities.Where(e => e.Id != order).ToList();
        copies.Should().ContainSingle().Which.GetAttributeValue<decimal?>("xrmv_amount").Should().Be(15m, "the later step read the action's output");
        // Through the whole pipeline: the action message's step fired, nested in the Update (depth: Update 1, workflow step 2).
        var log = (await PipeLogAsync(message + "|")).Should().ContainSingle().Subject;
        log.Should().StartWith($"{message}|40|0|2\n").And.Contain("\nparent=Update\n");
    }

    [Fact]
    public async Task Import_of_action_with_step_on_its_message_into_org_without_it()
    {
        // A Dataverse export of an action and of a plug-in step on its message: the step names the action's message by the id the
        // file gives the action (SdkMessageId), a message the organization does not have yet; the import creates it before the step.
        var fileMessage = Guid.NewGuid();
        var process = new TestProcess(Name("Recalculate"), "real/" + Recalculate, Category: 3, PrimaryEntity: Order, StateCode: 1,
            TriggerOnCreate: false, UniqueName: $"Stepped{_run}");
        var messageName = $"{WorkflowSolutionBuilder.Prefix}_Stepped{_run}";
        var (assemblyId, typeId) = (Guid.NewGuid(), Guid.NewGuid());
        const string assemblyName = "Xrmd.TestPlugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=a15f04ad73430aaf";
        const string type = Plugins + "ContextRecorderPlugin";
        var dll = $"PluginAssemblies/XrmdTestPlugins-{assemblyId.ToString("D").ToUpperInvariant()}/XrmdTestPlugins.dll";
        var file = WorkflowSolutionBuilder.Build($"xw_stepped_{_run}", false, entries =>
        {
            var customizations = XDocument.Load(new MemoryStream(entries["customizations.xml"]));
            var root = customizations.Root!;
            root.Element("Workflows")!.Element("Workflow")!.Add(new XElement("SdkMessageId", fileMessage.ToString("B")));
            root.Element("SolutionPluginAssemblies")!.Add(new XElement("PluginAssembly",
                new XAttribute("FullName", assemblyName), new XAttribute("PluginAssemblyId", assemblyId.ToString("D")), new XAttribute("CustomizationLevel", 1),
                new XElement("IsolationMode", 2), new XElement("SourceType", 0), new XElement("IntroducedVersion", "1.0"),
                new XElement("FileName", "/" + dll),
                new XElement("PluginTypes", new XElement("PluginType",
                    new XAttribute("AssemblyQualifiedName", $"{type}, {assemblyName}"), new XAttribute("PluginTypeId", typeId.ToString("D")),
                    new XAttribute("Name", type), new XElement("FriendlyName", typeId.ToString("D"))))));
            root.Element("SdkMessageProcessingSteps")!.Add(new XElement("SdkMessageProcessingStep",
                new XAttribute("Name", $"Recorder {_run}"), new XAttribute("SdkMessageProcessingStepId", Guid.NewGuid().ToString("B")),
                new XElement("SdkMessageId", fileMessage.ToString("B")),
                new XElement("SdkMessageFilterId", Guid.NewGuid().ToString("B")), // Dataverse's filter id: the import names its own
                new XElement("PluginTypeName", $"{type}, {assemblyName}"),
                new XElement("PluginTypeId", typeId.ToString("B")),
                new XElement("PrimaryEntity", Order),
                new XElement("Stage", 40), new XElement("Mode", 0), new XElement("Rank", 1), new XElement("SupportedDeployment", 0),
                new XElement("InvocationSource", 0), new XElement("CustomConfiguration", TestEntities.PipeLog), new XElement("StateCode", 0)));
            entries["customizations.xml"] = Encoding.UTF8.GetBytes(customizations.ToString());
            entries[dll] = PluginTestHelper.TestPluginBytes;
        }, process);

        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = file, ImportJobId = Guid.NewGuid() });

        (await MessagesAsync(messageName)).Should().ContainSingle().Which["sdkmessageid"].Should().Be(fileMessage);
        var step = (await RowsAsync("SELECT sdkmessageid, sdkmessagefilterid FROM data.sdkmessageprocessingstep WHERE plugintypeid = @t",
            ("@t", typeId))).Should().ContainSingle().Subject;
        step["sdkmessageid"].Should().Be(fileMessage);
        step["sdkmessagefilterid"].Should().Be((await FiltersAsync(fileMessage)).Single()["sdkmessagefilterid"]);
        var order = await OrderAsync(10m);
        (await RecalculateAsync(messageName, order, 1m)).Results["Total"].Should().Be(11m);
        (await PipeLogAsync(messageName + "|")).Should().ContainSingle().Which.Should().StartWith($"{messageName}|40|0|1\n");
    }

    [Fact]
    public async Task Action_table_and_category_cannot_change()
    {
        // Learn: an action's table and a process's category cannot change after save. A re-import that would change them is refused,
        // and the action keeps its message and filter (no filter left on a former table, no message left by a former action).
        var name = Name("Fixed");
        TestProcess Process(int category, string entity, string fixture) => new(name, fixture, Category: category, PrimaryEntity: entity, StateCode: 0,
            TriggerOnCreate: false, UniqueName: $"Fixed{_run}");
        byte[] File(TestProcess process) => WorkflowSolutionBuilder.Build($"xw_fixed_{_run}", process);
        var original = Process(3, Order, "real/" + Recalculate);
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = File(original), ImportJobId = Guid.NewGuid() });
        var message = (await MessageIdOfAsync(original.Id))!.Value;

        var table = await TestHelpers.FaultAsync(() => Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = File(Process(3, "account", "real/" + Recalculate)), ImportJobId = Guid.NewGuid(),
        }));
        (table.HttpStatus, table.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        table.Message.Should().Contain(name).And.Contain(Order).And.Contain("account");

        var category = await TestHelpers.FaultAsync(() => Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = File(Process(0, Order, "real/" + Recalculate)), ImportJobId = Guid.NewGuid(),
        }));
        (category.HttpStatus, category.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        category.Message.Should().Contain(name).And.Contain("category");

        (await FiltersAsync(message)).Should().ContainSingle().Which["primaryobjecttypecode"].Should().Be(Order);
        (await Admin.RetrieveAsync(S.Table, original.Id, new ColumnSet(S.CategoryColumn))).GetAttributeValue<OptionSetValue>(S.CategoryColumn).Value
            .Should().Be(3);
    }

    [Fact]
    public async Task Delete_is_refused_while_a_step_is_on_the_message()
    {
        var recalculate = await ActivateAsync(RecalculateAction());
        var message = recalculate.UniqueName!;
        await RegisterStepAsync("ContextRecorderPlugin", message, Order, 40, TestEntities.PipeLog);
        await DeactivateAsync(Admin, recalculate.Id);

        var fault = await TestHelpers.FaultAsync(() => Admin.DeleteAsync(S.Table, recalculate.Id));

        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().Contain(message);
        (await MessagesAsync(message)).Should().ContainSingle("the delete rolled back");
        (await RowsAsync("SELECT workflowid FROM data.workflow WHERE workflowid = @id", ("@id", recalculate.Id))).Should().ContainSingle();
    }

    [Fact]
    public async Task Action_calls_another_action()
    {
        // The inner action: the echo with its argument named Text (no action call). The outer one: the provisional echo as it is, its
        // action call step aimed at the inner action, with Text = InText.
        var innerName = $"new_Inner{_run}";
        var inner = Action(Name("Inner"), innerName, Replace(Without(WorkflowFixtures.Xaml("action-echo"), "ActionStep2"), "InText", "Text"),
            S.NoPrimaryEntity);
        await ActivateAsync(inner);
        var outerXaml = Replace(WorkflowFixtures.Xaml("action-echo"), "xrmv_Notify", innerName);
        var outer = await ActivateAsync(Action(Name("Outer"), $"new_Outer{_run}", outerXaml, S.NoPrimaryEntity));

        var response = await Admin.ExecuteAsync(new OrganizationRequest(outer.UniqueName) { ["InText"] = Name("Nested") });

        response.Results["OutText"].Should().Be(Name("Nested"));
        (await TaskSubjectsAsync(Name("Nested"))).Should().Be(2, "the inner action's create and the outer one's ran");

        // The outer action fails after the inner one ran (its create names a column task does not have): both roll back together.
        var broken = await ActivateAsync(Action(Name("Broken"), $"new_Broken{_run}",
            Replace(outerXaml, "Attribute=\"subject\" Entity=\"[CreatedEntities(&quot;CreateStep3", "Attribute=\"xrmv_nosuchcolumn\" Entity=\"[CreatedEntities(&quot;CreateStep3"),
            S.NoPrimaryEntity));
        var fault = await TestHelpers.FaultAsync(() => Admin.ExecuteAsync(new OrganizationRequest(broken.UniqueName) { ["InText"] = Name("Rolled back") }));
        fault.HttpStatus.Should().Be(400);
        (await TaskSubjectsAsync(Name("Rolled back"))).Should().Be(0, "the inner action's task rolled back with the outer call");
    }

    private async Task<int> TaskSubjectsAsync(string subject) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression("task")
        {
            Criteria = { Conditions = { new ConditionExpression("subject", ConditionOperator.Equal, subject) } },
        })).Entities.Count;

    // ---- names ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Parallel_activations_of_one_action_name_create_one_message()
    {
        // Plan 2.7b, Task 7: the activation's name check reads the custom API, then the message, under a lock that holds the name
        // until the transaction ends (a range lock on SQL Server, the key lock of the name on PostgreSQL). Of four Draft actions with
        // one unique name, activated at once, one gets the message; the others wait for it and are refused with the clash.
        // Final review I1: on SQL Server two of them can also meet in a deadlock (each holds a range lock the other's next read
        // needs); the victim was rolled back and, like a client, tries again (a bounded number of times). The invariant stays: one
        // message, every other activation refused with the clash.
        const int Count = 4;
        var rows = Enumerable.Range(0, Count).Select(_ => EchoAction($"Same{_run}")).ToList();
        foreach (var row in rows) await SaveAsync(fx, row);

        using var start = new ManualResetEventSlim();
        var activations = rows.Select(row => Task.Run(async () =>
        {
            start.Wait();
            return await RefusalOrNullAsync(() => WorkflowTestHelper.SetStateAsync(Admin, row.Id, S.ActivatedState, S.ActivatedStatus));
        })).ToList();
        start.Set();
        var outcomes = await Task.WhenAll(activations);

        outcomes.Count(o => o is null).Should().Be(1, "one activation gets the name");
        outcomes.OfType<XrmdFaultException>().Should().HaveCount(Count - 1).And.AllSatisfy(f =>
        {
            (f.HttpStatus, f.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
            f.Message.Should().Contain($"new_Same{_run}").And.Contain("already has the message");
        });
        (await MessagesAsync($"new_Same{_run}")).Should().ContainSingle();
    }

    /// <summary>The attempts a client makes of a request that keeps being chosen as a deadlock victim.</summary>
    private const int DeadlockAttempts = 5;

    /// <summary>
    /// Runs <paramref name="request"/> as a client would: again when the database chose it as a deadlock victim (rolled back, the
    /// fault says to retry), up to <see cref="DeadlockAttempts"/> times. Null when it succeeded, its fault when it was refused.
    /// </summary>
    private static async Task<XrmdFaultException?> RefusalOrNullAsync(Func<Task> request)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await request();
                return null;
            }
            catch (Exception ex) when (IsDeadlock(ex) && attempt < DeadlockAttempts)
            {
                // Retried, as the fault's text asks.
            }
            catch (XrmdFaultException fault)
            {
                return fault;
            }
        }
    }

    private static bool IsDeadlock(Exception ex) =>
        ex is XrmdFaultException fault && fault.ErrorCode == ErrorCodes.SqlError && fault.Message == SqlFaults.DeadlockMessage
        || ex is System.Data.Common.DbException db && Xrmd.Abstractions.Storage.SqlDialects.Classify(db) == Xrmd.Abstractions.Storage.DbErrorKind.Deadlock
        || ex.InnerException is { } inner && IsDeadlock(inner);

    private static readonly TimeSpan Blocked = TimeSpan.FromSeconds(1);

    /// <summary>A Create of the custom API <paramref name="uniqueName"/> through the pipeline, in <paramref name="s"/>'s transaction.</summary>
    private async Task CreateApiAsync(string uniqueName, DataSession s)
    {
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        using var scope = fx.App.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Xrmd.Pipeline.PipelineExecutor>().ExecuteAsync(
            new CreateRequest { Target = new Entity(CustomApiTables.Api) { ["uniquename"] = uniqueName, ["name"] = uniqueName } },
            new Xrmd.Pipeline.InvocationContext(caller, caller.UserId, 1, null, s, Guid.NewGuid()), default);
    }

    /// <summary>A SetState that activates the action <paramref name="id"/> through the pipeline, in <paramref name="s"/>'s transaction.</summary>
    private async Task ActivateInAsync(Guid id, DataSession s)
    {
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        using var scope = fx.App.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Xrmd.Pipeline.PipelineExecutor>().ExecuteAsync(
            new SetStateRequest
            {
                EntityMoniker = new EntityReference(S.Table, id), State = new OptionSetValue(S.ActivatedState),
                Status = new OptionSetValue(S.ActivatedStatus),
            },
            new Xrmd.Pipeline.InvocationContext(caller, caller.UserId, 1, null, s, Guid.NewGuid()), default);
    }

    [Fact]
    public async Task Activation_waits_for_an_open_custom_api_create_of_its_name_then_is_refused()
    {
        // Final review I1 (the deferred Task 7 interleaving): a custom API of the action's message name is being created in a
        // transaction still open. The activation's name check waits for it (the range lock on SQL Server, the key lock of the
        // API's name on PostgreSQL), then finds the API and is refused; no message of the action is created.
        var action = EchoAction($"ApiFirst{_run}");
        await SaveAsync(fx, action);
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();

        await using var conn = await factory.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await CreateApiAsync($"new_ApiFirst{_run}", new DataSession(conn, tx));

        var activation = Task.Run(() => TestHelpers.FaultAsync(
            () => WorkflowTestHelper.SetStateAsync(Admin, action.Id, S.ActivatedState, S.ActivatedStatus)));
        await Task.Delay(Blocked);
        activation.IsCompleted.Should().BeFalse("the activation waits for the custom API create that holds the name");
        await tx.CommitAsync();

        var fault = await activation;
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().Contain($"new_ApiFirst{_run}").And.Contain("custom API");
        (await Admin.RetrieveAsync(S.Table, action.Id, new ColumnSet("statecode"))).GetAttributeValue<OptionSetValue>("statecode").Value
            .Should().Be(S.DraftState);
        (await MessagesAsync($"new_ApiFirst{_run}")).Should().ContainSingle("only the custom API's message exists");
    }

    [Fact]
    public async Task Custom_api_create_waits_for_an_open_activation_of_its_name_then_is_refused()
    {
        // Final review I1, the other way: an activation of the action is still open; a Create of a custom API of its message name
        // waits for it, then finds the action's message and is refused.
        var action = EchoAction($"ActFirst{_run}");
        await SaveAsync(fx, action);
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();

        await using var conn = await factory.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await ActivateInAsync(action.Id, new DataSession(conn, tx));

        var create = Task.Run(() => TestHelpers.FaultAsync(
            () => Admin.CreateAsync(new Entity(CustomApiTables.Api) { ["uniquename"] = $"new_ActFirst{_run}", ["name"] = $"new_ActFirst{_run}" })));
        await Task.Delay(Blocked);
        create.IsCompleted.Should().BeFalse("the custom API create waits for the activation that holds the name");
        await tx.CommitAsync();

        var fault = await create;
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().Contain($"new_ActFirst{_run}").And.Contain("already has the message");
        (await MessagesAsync($"new_ActFirst{_run}")).Should().ContainSingle("only the action's message exists");
        (await Admin.RetrieveMultipleAsync(new QueryExpression(CustomApiTables.Api)
        {
            Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, $"new_ActFirst{_run}") } },
        })).Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Action_name_clash_with_custom_api_is_import_refusal()
    {
        // At activation: a custom API already has the message name.
        await Admin.CreateAsync(new Entity(CustomApiTables.Api) { ["uniquename"] = $"new_Clash{_run}", ["name"] = $"new_Clash{_run}" });
        var clash = EchoAction($"Clash{_run}");
        await SaveAsync(fx, clash);
        var activation = await TestHelpers.FaultAsync(() => WorkflowTestHelper.SetStateAsync(Admin, clash.Id, S.ActivatedState, S.ActivatedStatus));
        (activation.HttpStatus, activation.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        activation.Message.Should().Contain($"new_Clash{_run}").And.Contain("custom API");
        (await Admin.RetrieveAsync(S.Table, clash.Id, new ColumnSet("statecode"))).GetAttributeValue<OptionSetValue>("statecode").Value
            .Should().Be(S.DraftState);

        // At import (whatever the process's state in the file): the import is refused, naming the process; nothing of it stays.
        var first = new TestProcess(Name("Fine"), "action-echo", Category: 3, PrimaryEntity: S.NoPrimaryEntity, StateCode: 0, TriggerOnCreate: false,
            UniqueName: $"Fine{_run}");
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = WorkflowSolutionBuilder.Build($"xw_fine_{_run}", first), ImportJobId = Guid.NewGuid(),
        });
        (await MessagesAsync($"{WorkflowSolutionBuilder.Prefix}_Fine{_run}")).Should().ContainSingle("the import named the message with its publisher's prefix");
        await Admin.CreateAsync(new Entity(CustomApiTables.Api) { ["uniquename"] = $"xw_Taken{_run}", ["name"] = $"xw_Taken{_run}" });
        var taken = new TestProcess(Name("Taken"), "action-echo", Category: 3, PrimaryEntity: S.NoPrimaryEntity, StateCode: 0, TriggerOnCreate: false,
            UniqueName: $"Taken{_run}");
        var import = await TestHelpers.FaultAsync(() => Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = WorkflowSolutionBuilder.Build($"xw_taken_{_run}", taken), ImportJobId = Guid.NewGuid(),
        }));
        (import.HttpStatus, import.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        import.Message.Should().Contain(Name("Taken")).And.Contain($"xw_Taken{_run}");
        (await RowsAsync("SELECT workflowid FROM data.workflow WHERE workflowid = @id", ("@id", taken.Id))).Should().BeEmpty();
    }
}
