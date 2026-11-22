using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.Workflows;
using Xrmd.Workflows.Background;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.WorkflowTestHelper;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.14, Task 6 (rulings W4, W7, W9, W10, W12): background workflows queued by the writes that trigger them and run by the
/// worker (here a worker of the test's own, run with <see cref="AsyncOperationWorker.RunOnceAsync"/> until idle, on a clock the test
/// moves), their waits, and child workflows. The parity export gives the shapes: OnCreate (a task, then a child workflow), OnUpdate (a
/// wait on the order's amount, then an email; without its lookup literal to the exporting organization's currency, which this
/// organization lacks: WorkflowParityTests runs it unmodified) and Child (a stop, canceled). The provisional <c>background-wait</c> gives a postpone and a wait with a timeout.
/// </summary>
[Collection("xrmd")]
public sealed class BackgroundWorkflowTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Order = TestEntities.Order;
    private const string OnCreate = "OnCreate-3E1F0307-D8DE-F111-84E8-002248872B66";
    private const string OnUpdate = "OnUpdate-BC9A625B-E4DE-F111-84E8-002248872B66";
    private const string Child = "Child-0FE1B782-E5DE-F111-84E8-002248872B66";

    /// <summary>The id OnCreate's child workflow step names (the export's Child definition).</summary>
    private static readonly Guid ChildId = Guid.Parse("0fe1b782-e5de-f111-84e8-002248872b66");

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly ShiftedClock _clock = new();
    private Guid _adminUserId;

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private WorkflowWaits Waits => fx.App.Services.GetRequiredService<WorkflowWaits>();

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsureOrderAsync(store);
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ClearJobsAsync();
        await ClearAsync(fx);
        _adminUserId = (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId;
    }

    public async Task DisposeAsync()
    {
        await ClearJobsAsync();
        await ClearAsync(fx);
        await PluginTestHelper.ClearAsync(fx);
    }

    private async Task ClearJobsAsync()
    {
        await ExecAsync("DELETE FROM meta.workflow_wait; DELETE FROM data.asyncoperation;");
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await Waits.RefreshAsync(new DataSession(conn, null), default);
    }

    // ---- the worker and its clock ------------------------------------------------------------------------------------------

    /// <summary>The system clock moved forward by <see cref="Shift"/> (a wake stamps the real time, which stays in its past).</summary>
    private sealed class ShiftedClock : TimeProvider
    {
        public TimeSpan Shift { get; set; }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Shift;
    }

    private AsyncOperationWorker NewWorker() =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100) }), null, _clock);

    /// <summary>Runs ready jobs until none is (at most 50); returns how many ran.</summary>
    private async Task<int> RunJobsAsync(AsyncOperationWorker? worker = null)
    {
        worker ??= NewWorker();
        var ran = 0;
        while (ran < 50 && await worker.RunOnceAsync(default)) ran++;
        return ran;
    }

    // ---- processes -------------------------------------------------------------------------------------------------------

    private string Name(string what) => $"{what} {_run}";

    /// <summary>A Draft background definition of <paramref name="xaml"/>, owned by the administrator.</summary>
    private WorkflowRow Background(string name, string xaml, string primaryEntity) =>
        Realtime(Name(name), xaml, primaryEntity) with { Mode = 0, OwnerId = new EntityReference("systemuser", _adminUserId) };

    private Task<WorkflowRow> ActivateAsync(WorkflowRow row) => WorkflowTestHelper.ActivateAsync(fx, Admin, row);

    /// <summary>The export's OnCreate without its child workflow step: creates a task "TEST" regarding the order.</summary>
    private static string OrderTask() => Without(Real(OnCreate), "ChildWorkflowStep2");

    /// <summary>
    /// The export's OnUpdate (wait until the amount is above 100, then email the primary contact of the order's account) without the
    /// email's currency, a currency of the exporting organization that this one does not have (Task 9: WorkflowParityTests runs it
    /// unmodified, with and without that currency).
    /// </summary>
    private static string WaitThenEmail() =>
        Remove(Real(OnUpdate), "<mxswa:SetEntityProperty Attribute=\"transactioncurrencyid\"[\\s\\S]*?</mxswa:SetEntityProperty>");

    private static string AccountCreateUpdate()
    {
        var xaml = WorkflowFixtures.Xaml("realtime-create-update");
        xaml = Replace(xaml, "WorkflowPropertyType.Boolean, \"True\", \"Boolean\"", "WorkflowPropertyType.String, \"Reviewed\", \"String\"");
        xaml = Replace(xaml, "WorkflowPropertyType.Integer, \"25\", \"Integer\"", "WorkflowPropertyType.String, \"555-0125\", \"String\"");
        xaml = Replace(xaml, "Attribute=\"creditonhold\"", "Attribute=\"accountnumber\"");
        return Replace(xaml, "Attribute=\"numberofemployees\"", "Attribute=\"telephone1\"");
    }

    /// <summary>The provisional update of the account alone: <c>accountnumber</c> "Reviewed", <c>telephone1</c> "555-0125".</summary>
    private static string AccountUpdate() => Without(AccountCreateUpdate(), "CreateStep1");

    /// <summary>A <c>CreateCrmType</c> of a date, bound to <paramref name="variable"/> (replaces a read of a column the account lacks).</summary>
    private static string DateLiteral(string variable, DateTime date) => $"""
        <mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.EvaluateExpression, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" DisplayName="EvaluateExpression">
          <mxswa:ActivityReference.Arguments>
            <InArgument x:TypeArguments="x:String" x:Key="ExpressionOperator">CreateCrmType</InArgument>
            <InArgument x:TypeArguments="s:Object[]" x:Key="Parameters">[New Object() {"{"} Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.DateTime, "{date:yyyy-MM-ddTHH:mm:ssZ}", "DateTime" {"}"}]</InArgument>
            <OutArgument x:TypeArguments="x:Object" x:Key="Result">[{variable}]</OutArgument>
          </mxswa:ActivityReference.Arguments>
        </mxswa:ActivityReference>
        """;

    private static string ReadOf(string variable) =>
        $"<mxswa:GetEntityProperty Attribute=\"lastusedincampaign\" Entity=\"[InputEntities(&quot;primaryEntity&quot;)]\" EntityName=\"account\" Value=\"[{variable}]\" />";

    /// <summary>The provisional plain postpone (until <paramref name="until"/>), then the update of <see cref="AccountUpdate"/>.</summary>
    private static string PostponeThenUpdate(DateTime until)
    {
        var xaml = Replace(WorkflowFixtures.Xaml("background-wait"), ReadOf("WaitStep14_1"), DateLiteral("WaitStep14_1", until));
        xaml = Without(Without(xaml, "ConditionStep1"), "WaitStep9");
        return Append(xaml, AccountUpdate(), "UpdateStep2");
    }

    /// <summary>
    /// The provisional wait with a timeout: until the account's status reason is 2, or <paramref name="timeout"/>, when its timeout
    /// block deactivates the account.
    /// </summary>
    private static string WaitWithTimeout(DateTime timeout)
    {
        var xaml = Replace(WorkflowFixtures.Xaml("background-wait"), ReadOf("WaitStep9_3"), DateLiteral("WaitStep9_3", timeout));
        return Without(Without(xaml, "ConditionStep1"), "WaitStep14");
    }

    /// <summary><paramref name="xaml"/> with the top-level step of <paramref name="from"/> whose name starts with <paramref name="displayName"/> added last.</summary>
    private static string Append(string xaml, string from, string displayName)
    {
        static XElement Workflow(XDocument d) => d.Descendants().First(e => e.Name.LocalName == "Workflow");
        var doc = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace);
        var step = Workflow(XDocument.Parse(from)).Elements()
            .Single(e => ((string?)e.Attribute("DisplayName"))?.StartsWith(displayName, StringComparison.Ordinal) == true);
        Workflow(doc).Add(new XElement(step));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    // ---- data ----------------------------------------------------------------------------------------------------------------

    private async Task<Guid> OrderAsync(decimal? amount, OrganizationService? service = null, bool withAccount = true)
    {
        var order = new Entity(Order) { ["xrmv_name"] = Name("Order"), ["xrmv_amount"] = amount };
        if (withAccount)
        {
            var contact = await Admin.CreateAsync(new Entity("contact")
            {
                ["firstname"] = "Ada", ["lastname"] = Name("Example"), ["emailaddress1"] = $"ada.{_run}@example.com",
            });
            order["xrmv_accountid"] = new EntityReference("account",
                await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Contoso"), ["primarycontactid"] = new EntityReference("contact", contact) }));
        }
        return await (service ?? Admin).CreateAsync(order);
    }

    private async Task<List<Entity>> RegardingAsync(string entity, Guid id) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet("subject", "createdby", "statecode"),
            Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, id) } },
        })).Entities.ToList();

    private sealed record Job(Guid Id, string? Name, string? MessageName, string? PrimaryEntityType, Guid? RegardingObjectId,
        Guid? ActivationId, Guid? OwnerId, Guid? CreatedBy, int? Depth, int StateCode, int StatusCode, string? Message, int? ErrorCode,
        DateTime? PostponeUntil, string? Data, int OperationType);

    private async Task<List<Job>> JobsAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(
            $"SELECT asyncoperationid, name, messagename, primaryentitytype, regardingobjectid, {S.WorkflowActivationId}, ownerid, createdby, " +
            "depth, statecode, statuscode, message, errorcode, postponeuntil, data, operationtype FROM data.asyncoperation ORDER BY createdon, depth", conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        var jobs = new List<Job>();
        T? N<T>(int i) => rd.IsDBNull(i) ? default : rd.GetFieldValue<T>(i);
        while (await rd.ReadAsync())
            jobs.Add(new Job(rd.GetGuid(0), N<string>(1), N<string>(2), N<string>(3), N<Guid?>(4), N<Guid?>(5), N<Guid?>(6), N<Guid?>(7),
                N<int?>(8), rd.GetInt32(9), rd.GetInt32(10), N<string>(11), N<int?>(12), N<DateTime?>(13), N<string>(14), rd.GetInt32(15)));
        return jobs;
    }

    private async Task<Job> SingleJobAsync() => (await JobsAsync()).Should().ContainSingle().Subject;

    private sealed record Wait(Guid Job, string Entity, Guid ObjectId, DateTime WakeAt);

    private async Task<List<Wait>> WaitsAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT asyncoperationid, entity, objectid, wake_at FROM meta.workflow_wait", conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        var waits = new List<Wait>();
        while (await rd.ReadAsync()) waits.Add(new Wait(rd.GetGuid(0), rd.GetString(1), rd.GetGuid(2), rd.GetDateTime(3)));
        return waits;
    }

    private async Task<Guid> ActivationIdAsync(Guid definition)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command($"SELECT {S.ActiveWorkflowId} FROM data.workflow WHERE workflowid = @id", conn);
        cmd.Parameters.AddWithValue("@id", definition);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<bool> WorkflowExistsAsync(Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT COUNT(*) FROM data.workflow WHERE workflowid = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>An order waiting (1/10) on the export's OnUpdate wait: the job, the order and the definition.</summary>
    private async Task<(Job Job, Guid Order, WorkflowRow Definition)> WaitingOrderAsync()
    {
        var definition = await ActivateAsync(Background("Wait for the amount", WaitThenEmail(), Order) with { TriggerOnCreate = true });
        var order = await OrderAsync(50m);
        (await RunJobsAsync()).Should().Be(1);
        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((1, 10), "the amount is not above 100");
        return (job, order, definition);
    }

    // ---- queueing and running -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_queues_job_with_operationtype_10_and_activation()
    {
        var definition = await ActivateAsync(Background("Order follow-up", OrderTask(), Order) with { TriggerOnCreate = true });
        var activation = await ActivationIdAsync(definition.Id);

        var order = await OrderAsync(10m, withAccount: false);

        // W9: queued in the Create's transaction, nothing run yet.
        var job = await SingleJobAsync();
        (job.OperationType, job.Name, job.MessageName, job.PrimaryEntityType).Should().Be((10, definition.Name, "Create", Order));
        (job.RegardingObjectId, job.ActivationId, job.OwnerId, job.CreatedBy).Should().Be((order, activation, _adminUserId, _adminUserId));
        (job.Depth, job.StateCode, job.StatusCode).Should().Be((2, 0, 0));
        (await RegardingAsync("task", order)).Should().BeEmpty();

        (await RunJobsAsync()).Should().Be(1);

        (await SingleJobAsync()).Should().Match<Job>(j => j.StateCode == 3 && j.StatusCode == 30);
        (await RegardingAsync("task", order)).Should().ContainSingle().Which.GetAttributeValue<string>("subject").Should().Be("TEST");
    }

    [Fact]
    public async Task Job_runs_as_workflow_owner()
    {
        // A caller who may create orders but not tasks; the workflow belongs to the administrator (W7).
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvCreatexrmv_Order", DepthMask.Global), ("prvReadxrmv_Order", DepthMask.Global));
        await ActivateAsync(Background("As owner", OrderTask(), Order) with { TriggerOnCreate = true });

        var order = await OrderAsync(10m, ServiceFor(fx, objectId), withAccount: false);

        var job = await SingleJobAsync();
        (job.OwnerId, job.CreatedBy).Should().Be((_adminUserId, user));
        await RunJobsAsync();
        (await SingleJobAsync()).StatusCode.Should().Be(30);
        (await RegardingAsync("task", order)).Should().ContainSingle()
            .Which.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(_adminUserId, "the steps run as the workflow's owner");
    }

    [Fact]
    public async Task Assign_of_activated_definition_moves_its_activation_to_the_new_owner()
    {
        // A User-scope (1) workflow of the administrator, assigned to another user while Activated: the activation follows in the
        // Assign's transaction, so the scope and the job owner are the new owner at once (final review I2; Learn: background
        // workflows run as their owner).
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await AssignRoleAsync(fx, user, StandardRoles.SystemAdministrator);
        var service = ServiceFor(fx, objectId);
        var definition = await ActivateAsync(Background("Reassigned", OrderTask(), Order) with { TriggerOnCreate = true, Scope = 1 });
        var activation = await ActivationIdAsync(definition.Id);

        await OrderAsync(10m, service, withAccount: false);
        (await JobsAsync()).Should().BeEmpty("the user's order is not in the administrator's scope");

        await Admin.ExecuteAsync(new AssignRequest
        {
            Target = new EntityReference(S.Table, definition.Id), Assignee = new EntityReference("systemuser", user),
        });

        (await ActivationIdAsync(definition.Id)).Should().Be(activation, "the Assign keeps the activation");
        (await Admin.RetrieveAsync(S.Table, activation, new ColumnSet("ownerid"))).GetAttributeValue<EntityReference>("ownerid").Id
            .Should().Be(user);
        await OrderAsync(10m, withAccount: false);
        (await JobsAsync()).Should().BeEmpty("the administrator's order is no longer in the owner's scope");
        var order = await OrderAsync(10m, service, withAccount: false);
        var job = await SingleJobAsync();
        (job.ActivationId, job.OwnerId, job.CreatedBy).Should().Be((activation, user, user));
        await RunJobsAsync();
        (await SingleJobAsync()).StatusCode.Should().Be(30);
        (await RegardingAsync("task", order)).Should().ContainSingle()
            .Which.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(user, "the steps run as the new owner");
    }

    [Fact]
    public async Task Asyncautodelete_removes_succeeded_job()
    {
        await ActivateAsync(Background("Auto delete", OrderTask(), Order) with { TriggerOnCreate = true, AsyncAutoDelete = true });
        var order = await OrderAsync(10m, withAccount: false);

        (await RunJobsAsync()).Should().Be(1);

        (await JobsAsync()).Should().BeEmpty("a succeeded job of an asyncautodelete workflow is deleted");
        (await RegardingAsync("task", order)).Should().ContainSingle();
    }

    [Fact]
    public async Task Failed_auto_delete_keeps_the_job_succeeded()
    {
        await ActivateAsync(Background("Auto delete", OrderTask(), Order) with { TriggerOnCreate = true, AsyncAutoDelete = true });
        await OrderAsync(10m, withAccount: false);

        AsyncOperationWorker.BeforeAutoDelete.Value = () => throw new InvalidOperationException("The delete failed.");
        try
        {
            (await RunJobsAsync()).Should().Be(1);
        }
        finally
        {
            AsyncOperationWorker.BeforeAutoDelete.Value = null;
        }

        // The delete is logged and the job is kept succeeded (3/30), never left Locked to run again after a restart.
        (await SingleJobAsync()).Should().Match<Job>(j => j.StateCode == 3 && j.StatusCode == 30);
    }

    [Fact]
    public async Task Failing_step_fails_job_and_keeps_earlier_steps()
    {
        // The provisional create-then-update, its update made to fail (a column the account does not have).
        // Its task's regarding is left out (the provisional file sets it to a Guid, see RealtimeWorkflowTests.AccountTask).
        var failing = Remove(Replace(Replace(AccountCreateUpdate(), "Attribute=\"telephone1\"", "Attribute=\"xrmv_nosuchcolumn\""),
            "\"Call the new customer\"", $"\"{Name("Kept")}\""), "<mxswa:SetEntityProperty Attribute=\"regardingobjectid\"[^>]*/>");
        await ActivateAsync(Background("Failing", failing, "account") with { TriggerOnCreate = true });
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Account") });

        await RunJobsAsync();

        // W9: no retry; the step before the failure ran in its own transaction and stays.
        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((3, 31));
        job.ErrorCode.Should().NotBeNull();
        job.Message.Should().Contain("xrmv_nosuchcolumn");
        (await Admin.RetrieveMultipleAsync(new QueryExpression("task")
        {
            Criteria = { Conditions = { new ConditionExpression("subject", ConditionOperator.Equal, Name("Kept")) } },
        })).Entities.Should().ContainSingle();
        (await Admin.RetrieveAsync("account", account, new ColumnSet("accountnumber"))).Contains("accountnumber").Should().BeFalse();
    }

    [Fact]
    public async Task Team_owned_workflow_fails_trigger_with_400()
    {
        var team = await CreateTeamAsync(fx, await RootBusinessUnitAsync(fx));
        var definition = await ActivateAsync(Background("Team owned", OrderTask(), Order) with
        {
            TriggerOnCreate = true, OwnerId = new EntityReference("team", team),
        });

        var fault = await TestHelpers.FaultAsync(() => OrderAsync(10m, withAccount: false));

        // W7: a job runs as its owner, a user; a team-owned background workflow fails its trigger explicitly.
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().Contain(definition.Name).And.Contain("owned by a team");
        (await JobsAsync()).Should().BeEmpty();
    }

    /// <summary>A custom table whose delete cascades (Cascade) to the accounts that point to it (a new lookup on account).</summary>
    private async Task<(string Table, string Lookup)> CascadeParentOfAccountAsync()
    {
        var table = $"new_bgcascade{_run}";
        await Admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateEntityRequest
        {
            Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
            {
                LogicalName = table, SchemaName = table, DisplayName = TestHelpers.Label("Parent " + _run),
                DisplayCollectionName = TestHelpers.Label("Parents " + _run), OwnershipType = Microsoft.Xrm.Sdk.Metadata.OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = TestHelpers.Label("Name"),
                RequiredLevel = new Microsoft.Xrm.Sdk.Metadata.AttributeRequiredLevelManagedProperty(Microsoft.Xrm.Sdk.Metadata.AttributeRequiredLevel.None),
            },
        });
        var lookup = $"new_bgcascadeid{_run}";
        await Admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateOneToManyRequest
        {
            OneToManyRelationship = new Microsoft.Xrm.Sdk.Metadata.OneToManyRelationshipMetadata
            {
                SchemaName = $"new_account_{lookup}", ReferencedEntity = table, ReferencingEntity = "account",
                CascadeConfiguration = new Microsoft.Xrm.Sdk.Metadata.CascadeConfiguration { Delete = Microsoft.Xrm.Sdk.Metadata.CascadeType.Cascade },
            },
            Lookup = new Microsoft.Xrm.Sdk.Metadata.LookupAttributeMetadata
            {
                LogicalName = lookup, SchemaName = lookup, DisplayName = TestHelpers.Label("Cascade parent"),
            },
        });
        return (table, lookup);
    }

    [Fact]
    public async Task Cascaded_delete_queues_no_background_job()
    {
        var (parentTable, lookup) = await CascadeParentOfAccountAsync();
        await ActivateAsync(Background("On delete", AccountUpdate(), "account") with { TriggerOnDelete = true });
        var parent = await Admin.CreateAsync(new Entity(parentTable) { ["new_name"] = Name("Parent") });
        await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Cascaded"), [lookup] = new EntityReference(parentTable, parent) });

        await Admin.DeleteAsync(parentTable, parent);

        // W5: the writes the core makes as a cascade trigger no workflow, background ones included.
        (await JobsAsync()).Should().BeEmpty();

        // A direct delete of an account still queues one.
        await Admin.DeleteAsync("account", await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Direct") }));
        (await SingleJobAsync()).MessageName.Should().Be("Delete");
    }

    [Fact]
    public async Task Job_of_rolled_back_create_never_runs()
    {
        await ActivateAsync(Background("Rolled back", OrderTask(), Order) with { TriggerOnCreate = true });
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes, "Xrmd.TestPlugins." + _run[..6]);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", Order, 40, rank: 2, config: "invalid:after the queueing");

        var fault = await TestHelpers.FaultAsync(() => OrderAsync(10m, withAccount: false));

        // RF3: queued in the Create's transaction, the job went with its rollback.
        fault.Message.Should().Be("after the queueing");
        (await JobsAsync()).Should().BeEmpty();
        (await RunJobsAsync()).Should().Be(0);
    }

    // ---- waits ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Postpone_suspends_with_postponeuntil_then_resumes()
    {
        var until = DateTime.UtcNow.AddHours(1);
        await ActivateAsync(Background("Postpone", PostponeThenUpdate(until), "account") with { TriggerOnCreate = true });
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Postponed") });

        (await RunJobsAsync()).Should().Be(1);

        // W10: a plain postpone suspends the job until its date, with no wait row.
        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((1, 10));
        job.PostponeUntil.Should().BeCloseTo(until, TimeSpan.FromSeconds(1));
        (await WaitsAsync()).Should().BeEmpty();
        (await RunJobsAsync()).Should().Be(0, "the date has not come");
        (await Admin.RetrieveAsync("account", account, new ColumnSet("accountnumber"))).Contains("accountnumber").Should().BeFalse();

        _clock.Shift = TimeSpan.FromHours(2);
        (await RunJobsAsync()).Should().Be(1);

        (await SingleJobAsync()).StatusCode.Should().Be(30);
        (await Admin.RetrieveAsync("account", account, new ColumnSet("accountnumber"))).GetAttributeValue<string>("accountnumber")
            .Should().Be("Reviewed", "the step after the postpone ran");
    }

    [Fact]
    public async Task Wait_condition_resumes_when_record_changes()
    {
        var (job, order, _) = await WaitingOrderAsync();

        job.PostponeUntil.Should().BeNull("the wait has no timeout");
        var wait = (await WaitsAsync()).Should().ContainSingle().Subject;
        (wait.Job, wait.Entity, wait.ObjectId).Should().Be((job.Id, Order, order));
        wait.WakeAt.Should().Be(WorkflowWaits.Never);
        (await RunJobsAsync()).Should().Be(0, "nothing woke it");

        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_amount"] = 150m });

        (await RunJobsAsync()).Should().Be(1);
        (await SingleJobAsync()).StatusCode.Should().Be(30);
        (await WaitsAsync()).Should().BeEmpty();
        (await RegardingAsync("email", order)).Should().ContainSingle("the step after the wait sent the email");
    }

    [Fact]
    public async Task Wait_condition_still_false_suspends_again()
    {
        var (job, order, _) = await WaitingOrderAsync();

        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_name"] = Name("Renamed") });
        (await WaitsAsync()).Should().ContainSingle().Which.WakeAt.Should().BeBefore(DateTime.UtcNow.AddSeconds(1), "the write woke it");

        (await RunJobsAsync()).Should().Be(1);

        (await SingleJobAsync()).Should().Match<Job>(j => j.Id == job.Id && j.StateCode == 1 && j.StatusCode == 10);
        (await WaitsAsync()).Should().ContainSingle().Which.WakeAt.Should().Be(WorkflowWaits.Never);
        (await RegardingAsync("email", order)).Should().BeEmpty();
    }

    [Fact]
    public async Task Wait_timeout_branch_runs_when_time_passes()
    {
        var timeout = DateTime.UtcNow.AddHours(1);
        await ActivateAsync(Background("Timeout", WaitWithTimeout(timeout), "account") with { TriggerOnCreate = true });
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Waiting") });

        (await RunJobsAsync()).Should().Be(1);
        var job = await SingleJobAsync();
        (job.StateCode, job.StatusCode).Should().Be((1, 10));
        job.PostponeUntil.Should().BeCloseTo(timeout, TimeSpan.FromSeconds(1));
        (await WaitsAsync()).Should().ContainSingle().Which.WakeAt.Should().BeCloseTo(timeout, TimeSpan.FromSeconds(1));

        _clock.Shift = TimeSpan.FromHours(2);
        (await RunJobsAsync()).Should().Be(1);

        // The timeout block: the account is deactivated.
        (await SingleJobAsync()).StatusCode.Should().Be(30);
        (await Admin.RetrieveAsync("account", account, new ColumnSet("statecode"))).GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
    }

    [Fact]
    public async Task Wait_record_deleted_fails_job()
    {
        var (job, order, _) = await WaitingOrderAsync();

        await Admin.DeleteAsync(Order, order);

        // RF4: in the Delete's transaction.
        var failed = await SingleJobAsync();
        (failed.Id, failed.StateCode, failed.StatusCode, failed.ErrorCode).Should().Be((job.Id, 3, 31, ErrorCodes.InvalidArgument));
        failed.Message.Should().Be(WorkflowJobs.WaitRecordDeletedMessage);
        (await WaitsAsync()).Should().BeEmpty();
        (await RunJobsAsync()).Should().Be(0);
    }

    /// <summary>
    /// Runs the ready jobs, with <paramref name="write"/> done once during the first run that suspends: after the engine read the
    /// condition and before the wait is registered (the job's earlier wait row is already gone then), or, with
    /// <paramref name="afterRegister"/>, once the wait is registered and checked and before the worker records the suspension.
    /// </summary>
    private async Task<int> RunWithWriteBeforeRegisterAsync(Func<Task> write, bool afterRegister = false)
    {
        var fired = 0;
        var seam = afterRegister ? WorkflowJobHandler.AfterRegister : WorkflowJobHandler.BeforeRegister;
        seam.Value = async _ =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0) await write();
        };
        try
        {
            return await RunJobsAsync();
        }
        finally
        {
            seam.Value = null;
        }
    }

    [Fact]
    public async Task Definition_deleted_before_registration_cancels_running_job()
    {
        var (job, order, definition) = await WaitingOrderAsync();
        await DeactivateAsync(Admin, definition.Id);
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_name"] = Name("Renamed") });

        var ran = await RunWithWriteBeforeRegisterAsync(() => Admin.DeleteAsync(S.Table, definition.Id));

        // W4: the job was running (2/20) when its workflow went: it ends 3/32 instead of waiting for a workflow that is gone.
        ran.Should().Be(1);
        var canceled = await SingleJobAsync();
        (canceled.Id, canceled.StateCode, canceled.StatusCode, canceled.Message).Should().Be((job.Id, 3, 32, WorkflowJobs.WorkflowDeletedMessage));
        (await WaitsAsync()).Should().BeEmpty();
        (await WorkflowExistsAsync(job.ActivationId!.Value)).Should().BeFalse();
    }

    [Fact]
    public async Task Definition_deleted_after_registration_cancels_running_job()
    {
        var (job, order, definition) = await WaitingOrderAsync();
        await DeactivateAsync(Admin, definition.Id);
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_name"] = Name("Renamed") });

        var ran = await RunWithWriteBeforeRegisterAsync(() => Admin.DeleteAsync(S.Table, definition.Id), afterRegister: true);

        // W4: the suspension recorded after the delete is woken, and the next run finds no workflow: 3/32.
        ran.Should().Be(2);
        var canceled = await SingleJobAsync();
        (canceled.Id, canceled.StateCode, canceled.StatusCode, canceled.Message).Should().Be((job.Id, 3, 32, WorkflowJobs.WorkflowDeletedMessage));
        (await WaitsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Record_deleted_after_registration_fails_running_job()
    {
        var (job, order, _) = await WaitingOrderAsync();
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_name"] = Name("Renamed") });

        var ran = await RunWithWriteBeforeRegisterAsync(() => Admin.DeleteAsync(Order, order), afterRegister: true);

        // W10: the suspension recorded after the delete is woken, and the next run finds the record gone: 3/31.
        ran.Should().Be(2);
        var failed = await SingleJobAsync();
        (failed.Id, failed.StateCode, failed.StatusCode).Should().Be((job.Id, 3, 31));
        (await WaitsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Write_between_condition_read_and_registration_wakes_job()
    {
        var (job, order, _) = await WaitingOrderAsync();
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_name"] = Name("Renamed") }); // wakes it; still false

        var ran = await RunWithWriteBeforeRegisterAsync(() => Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_amount"] = 150m }));

        // W10: the write landed after the condition read, while the job had no wait row: it is woken all the same.
        ran.Should().Be(2, "the run that missed the write suspends woken, and the next one sees the amount");
        (await SingleJobAsync()).Should().Match<Job>(j => j.Id == job.Id && j.StateCode == 3 && j.StatusCode == 30);
        (await WaitsAsync()).Should().BeEmpty();
        (await RegardingAsync("email", order)).Should().ContainSingle();
    }

    [Fact]
    public async Task Woken_job_still_false_runs_once_while_a_long_transaction_is_open()
    {
        var (job, order, _) = await WaitingOrderAsync();
        var bystander = await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Bystander") });
        await ActivateAsync(Background("Account follow-up", AccountUpdate(), "account") with { TriggerOnCreate = true });

        // A long transaction (a synchronous import, say) holds MIN_ACTIVE_ROWVERSION() below every later write.
        await using var open = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await open.BeginTransactionAsync();
        await using (var hold = TestSql.Command("UPDATE data.account SET name = name WHERE accountid = @id", open, tx))
        {
            hold.Parameters.AddWithValue("@id", bystander);
            await hold.ExecuteNonQueryAsync();
        }
        try
        {
            await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_name"] = Name("Renamed") }); // wakes it; still false
            await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Newer") }); // a newer job behind it

            // W10: the woken run sees the write, suspends again and stays asleep; the queue goes on to the newer job.
            (await RunJobsAsync()).Should().Be(2);
            var jobs = await JobsAsync();
            jobs.Should().ContainSingle(j => j.Id == job.Id).Which.Should().Match<Job>(j => j.StateCode == 1 && j.StatusCode == 10);
            jobs.Should().ContainSingle(j => j.Id != job.Id).Which.StatusCode.Should().Be(30);
            (await WaitsAsync()).Should().ContainSingle().Which.WakeAt.Should().Be(WorkflowWaits.Never);
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task Delete_between_condition_read_and_registration_fails_job()
    {
        var (job, order, _) = await WaitingOrderAsync();
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_name"] = Name("Renamed") });

        var ran = await RunWithWriteBeforeRegisterAsync(() => Admin.DeleteAsync(Order, order));

        // W10: the record of the wait is gone: the job fails explicitly instead of waiting on nothing.
        ran.Should().Be(1);
        var failed = await SingleJobAsync();
        (failed.Id, failed.StateCode, failed.StatusCode, failed.ErrorCode).Should().Be((job.Id, 3, 31, ErrorCodes.InvalidArgument));
        failed.Message.Should().Be(WorkflowJobs.WaitRecordDeletedMessage);
        (await WaitsAsync()).Should().BeEmpty();
    }

    /// <summary>A workflow job suspended (1/10) on a wait without a timeout that reads <paramref name="record"/>.</summary>
    private async Task<Guid> WaitOnAsync(EntityReference record)
    {
        var job = Guid.NewGuid();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using (var cmd = TestSql.Command($"""
                         INSERT INTO data.asyncoperation (asyncoperationid, name, operationtype, statecode, statuscode, createdon, modifiedon)
                         VALUES (@id, 'Waiting workflow job', {S.WorkflowOperationType}, 1, 10, SYSUTCDATETIME(), SYSUTCDATETIME());
                         """, conn))
        {
            cmd.Parameters.AddWithValue("@id", job);
            await cmd.ExecuteNonQueryAsync();
        }
        await Waits.RegisterAsync(new DataSession(conn, null), job, record, null, default);
        return job;
    }

    private async Task<DateTime> WakeAtAsync(Guid job) => (await WaitsAsync()).Single(w => w.Job == job).WakeAt;

    [Fact]
    public async Task SendEmail_wakes_job_waiting_on_email()
    {
        var contact = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = Name("Ada"), ["emailaddress1"] = $"ada.{_run}@example.com" });
        var email = await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = Name("Welcome"),
            ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", contact) }]),
        });
        var job = await WaitOnAsync(new EntityReference("email", email));

        await Admin.ExecuteAsync(new SendEmailRequest { EmailId = email, IssueSend = false, TrackingToken = "" });

        // W10: SendEmail changes the email's state without an Update message; it is a core write of the record all the same.
        (await WakeAtAsync(job)).Should().BeBefore(DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task RemoveLink_clear_wakes_job_waiting_on_referencing_record()
    {
        var parent = await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Parent") });
        var child = await Admin.CreateAsync(new Entity("account")
        {
            ["name"] = Name("Child"), ["parentaccountid"] = new EntityReference("account", parent),
        });
        var job = await WaitOnAsync(new EntityReference("account", child));

        await Admin.DeleteAsync("account", parent);

        // W10: the delete clears the child's parentaccountid (account_parent_account, RemoveLink): a write of the child.
        (await Admin.RetrieveAsync("account", child, new ColumnSet("parentaccountid"))).Contains("parentaccountid").Should().BeFalse();
        (await WakeAtAsync(job)).Should().BeBefore(DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task RemoveLink_clear_wakes_only_the_cleared_records()
    {
        var parent = await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Parent") });
        var child = await Admin.CreateAsync(new Entity("account")
        {
            ["name"] = Name("Child"), ["parentaccountid"] = new EntityReference("account", parent),
        });
        var other = await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Other") });
        var onChild = await WaitOnAsync(new EntityReference("account", child));
        var onOther = await WaitOnAsync(new EntityReference("account", other));

        await Admin.DeleteAsync("account", parent);

        // Only the records whose link was cleared are written: a job waiting on another account sleeps on.
        (await WakeAtAsync(onChild)).Should().BeBefore(DateTime.UtcNow.AddSeconds(1));
        (await WakeAtAsync(onOther)).Should().Be(WorkflowWaits.Never);
    }

    [Fact]
    public async Task Deactivation_keeps_waiting_job_running()
    {
        var (job, order, definition) = await WaitingOrderAsync();
        var activation = job.ActivationId!.Value;

        await DeactivateAsync(Admin, definition.Id);

        // RF4, W4: the activation stays while the job waits, and the job goes on.
        (await WorkflowExistsAsync(activation)).Should().BeTrue();
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_amount"] = 150m });
        (await RunJobsAsync()).Should().Be(1);
        (await SingleJobAsync()).StatusCode.Should().Be(30);
        (await RegardingAsync("email", order)).Should().ContainSingle();
        (await WorkflowExistsAsync(activation)).Should().BeFalse("its last waiting job has ended");
    }

    [Fact]
    public async Task Draft_definition_delete_cancels_waiting_jobs()
    {
        var (job, _, definition) = await WaitingOrderAsync();
        var activation = job.ActivationId!.Value;
        await DeactivateAsync(Admin, definition.Id);

        await Admin.DeleteAsync(S.Table, definition.Id);

        // RF4, W4: the activation goes with its definition, and the jobs still waiting on it are canceled.
        (await WorkflowExistsAsync(activation)).Should().BeFalse();
        var canceled = await SingleJobAsync();
        (canceled.Id, canceled.StateCode, canceled.StatusCode).Should().Be((job.Id, 3, 32));
        canceled.Message.Should().Be(WorkflowJobs.WorkflowDeletedMessage);
        (await WaitsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Waiting_job_survives_restart()
    {
        var (job, order, _) = await WaitingOrderAsync();
        var state = WorkflowJobState.Parse(job.Data!);
        state.ResumePath.Should().NotBeNull("the job's data holds the step it waits at");

        // RF4: a new process: a new worker, the activated processes read again, the waits counted from the database.
        fx.App.Services.GetRequiredService<WorkflowRegistry>().Invalidate();
        Waits.Reset();
        Waits.HasAny.Should().BeTrue("unknown until counted");
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_amount"] = 150m });

        (await RunJobsAsync(NewWorker())).Should().Be(1);
        (await SingleJobAsync()).StatusCode.Should().Be(30);
        (await RegardingAsync("email", order)).Should().ContainSingle();
    }

    [Fact]
    public async Task Count_during_uncommitted_registration_does_not_cache_no_wait()
    {
        var earlier = await WaitOnAsync(new EntityReference("account", Guid.NewGuid()));
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var other = await TestProvider.OpenAsync(fx.ConnectionString);
        var others = new DataSession(other, null);

        // A registration in a transaction not yet committed; meanwhile a wait is removed (the state becomes unknown) and a write
        // counts the waits under row versioning: it sees neither.
        await using var tx = await conn.BeginTransactionAsync();
        var s = new DataSession(conn, tx);
        await Waits.RegisterAsync(s, Guid.NewGuid(), new EntityReference("account", Guid.NewGuid()), null, default);
        await Waits.RemoveAsync(others, earlier, default);
        await Waits.WakeAsync(others, "account", Guid.NewGuid(), default);
        await tx.CommitAsync();
        s.Committed();

        // W10: the committed wait must be seen by later writes.
        Waits.HasAny.Should().BeTrue();
    }

    [Fact]
    public async Task Job_interrupted_by_stop_goes_back_to_queue()
    {
        var (job, order, definition) = await WaitingOrderAsync();
        await DeactivateAsync(Admin, definition.Id); // its activation is kept while the job has not ended
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_name"] = Name("Renamed") });

        // The host stops while the job runs.
        using var stop = new CancellationTokenSource();
        WorkflowJobHandler.BeforeRegister.Value = ct =>
        {
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        try
        {
            await NewWorker().RunOnceAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            // the worker's loop ends on it
        }
        finally
        {
            WorkflowJobHandler.BeforeRegister.Value = null;
        }

        // At least once: the job is ready again, from its last saved state, as after a crash; its activation stays.
        var requeued = await SingleJobAsync();
        (requeued.Id, requeued.StateCode, requeued.StatusCode, requeued.Message).Should().Be((job.Id, 0, 0, (string?)null));
        requeued.Data.Should().Be(job.Data);
        (await WorkflowExistsAsync(job.ActivationId!.Value)).Should().BeTrue();
        (await RunJobsAsync()).Should().Be(1);
        (await SingleJobAsync()).Should().Match<Job>(j => j.StateCode == 1 && j.StatusCode == 10, "the amount is still not above 100");
    }

    [Fact]
    public async Task Deleting_job_removes_its_wait()
    {
        // Only a completed job can be deleted; one may keep a wait row (its end raced with a cancel, an older process).
        var job = await WaitOnAsync(new EntityReference("account", Guid.NewGuid()));
        await ExecAsync($"UPDATE data.asyncoperation SET statecode = 3, statuscode = 30 WHERE asyncoperationid = '{job}'");

        await Admin.DeleteAsync("asyncoperation", job);

        (await JobsAsync()).Should().BeEmpty();
        (await WaitsAsync()).Should().BeEmpty("a deleted job's wait goes with it");
    }

    /// <summary>Runs one job with the host stopping in <paramref name="seam"/>; the worker's loop ends on the cancellation.</summary>
    private async Task RunStoppingAtAsync(AsyncLocal<Func<CancellationToken, Task>?> seam)
    {
        using var stop = new CancellationTokenSource();
        seam.Value = ct =>
        {
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        try
        {
            await NewWorker().RunOnceAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            // the worker's loop ends on it
        }
        finally
        {
            seam.Value = null;
        }
    }

    [Fact]
    public async Task Job_stopped_after_registration_keeps_no_wait()
    {
        var (job, order, _) = await WaitingOrderAsync();
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_name"] = Name("Renamed") });

        await RunStoppingAtAsync(WorkflowJobHandler.AfterRegister);

        // The job goes back to the queue without the wait it had registered: it is ready, and runs again from its saved state.
        (await SingleJobAsync()).Should().Match<Job>(j => j.Id == job.Id && j.StateCode == 0 && j.StatusCode == 0);
        (await WaitsAsync()).Should().BeEmpty();
        (await RunJobsAsync()).Should().Be(1);
        (await SingleJobAsync()).Should().Match<Job>(j => j.StateCode == 1 && j.StatusCode == 10);
    }

    /// <summary>An account waiting (1/10) on the wait with a timeout an hour away, woken by a write and its condition still false.</summary>
    private async Task<(Job Job, Guid Account)> WokenTimeoutJobAsync()
    {
        await ActivateAsync(Background("Timeout", WaitWithTimeout(DateTime.UtcNow.AddHours(1)), "account") with { TriggerOnCreate = true });
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Waiting") });
        (await RunJobsAsync()).Should().Be(1);
        var job = await SingleJobAsync();
        job.PostponeUntil.Should().NotBeNull();
        await Admin.UpdateAsync(new Entity("account", account) { ["name"] = Name("Renamed") });
        return (job, account);
    }

    [Fact]
    public async Task Woken_job_stopped_runs_again_before_its_timeout()
    {
        var (job, _) = await WokenTimeoutJobAsync();

        await RunStoppingAtAsync(WorkflowJobHandler.BeforeRegister);

        // Back to the queue without the timeout of its last wait: it runs again now, not in an hour.
        (await SingleJobAsync()).Should().Match<Job>(j => j.Id == job.Id && j.StateCode == 0 && j.PostponeUntil == null);
        (await RunJobsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Job_recovered_after_crash_between_registration_and_suspension_runs_again()
    {
        // A crash after the wait row committed (no timeout: wake_at is the far future) and before the worker recorded 1/10: the job
        // is left Locked with its wait row (final review I3).
        var (job, _, _) = await WaitingOrderAsync();
        (await WaitsAsync()).Should().ContainSingle().Which.Job.Should().Be(job.Id);
        await ExecAsync($"UPDATE data.asyncoperation SET statecode = 2, statuscode = 20 WHERE asyncoperationid = '{job.Id}'");

        (await NewWorker().RecoverAsync(default)).Should().Be(1);

        // Recovered without the wait it had registered: ready now, it runs again from its saved state and waits again.
        (await SingleJobAsync()).Should().Match<Job>(j => j.Id == job.Id && j.StateCode == 0 && j.StatusCode == 0);
        (await WaitsAsync()).Should().BeEmpty();
        (await RunJobsAsync()).Should().Be(1);
        (await SingleJobAsync()).Should().Match<Job>(j => j.StateCode == 1 && j.StatusCode == 10);
        (await WaitsAsync()).Should().ContainSingle().Which.Job.Should().Be(job.Id);
    }

    [Fact]
    public async Task Woken_job_recovered_after_crash_runs_again_before_its_timeout()
    {
        var (job, _) = await WokenTimeoutJobAsync();
        // A crash while the woken job ran: Locked, its wait row taken by the run.
        await ExecAsync($"UPDATE data.asyncoperation SET statecode = 2, statuscode = 20 WHERE asyncoperationid = '{job.Id}'; " +
                        $"DELETE FROM meta.workflow_wait WHERE asyncoperationid = '{job.Id}';");

        (await NewWorker().RecoverAsync(default)).Should().Be(1);

        (await SingleJobAsync()).Should().Match<Job>(j => j.Id == job.Id && j.StateCode == 0 && j.PostponeUntil == null);
        (await RunJobsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Wait_given_to_a_job_as_it_ends_does_not_outlive_it()
    {
        var definition = await ActivateAsync(Background("Order follow-up", OrderTask(), Order) with { TriggerOnCreate = true });
        await OrderAsync(10m, withAccount: false);

        // Its workflow is deleted after the run's clean-up and before the worker records the outcome: the delete gives the job, still
        // running, a wait that is ready now (WakeRunningAsync).
        WorkflowJobHandler.AfterEnd.Value = async job =>
        {
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await Waits.WakeRunningAsync(new DataSession(conn, null), [job], S.Table, definition.Id, default);
        };
        try
        {
            (await RunJobsAsync()).Should().Be(1);
        }
        finally
        {
            WorkflowJobHandler.AfterEnd.Value = null;
        }

        // The job ended: its wait goes with its outcome, and the waits are counted as none again.
        (await SingleJobAsync()).StatusCode.Should().Be(30);
        (await WaitsAsync()).Should().BeEmpty();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await Waits.RefreshAsync(new DataSession(conn, null), default);
        }
        Waits.HasAny.Should().BeFalse();
    }

    [Fact]
    public async Task Client_cancel_removes_wait()
    {
        var (job, _, definition) = await WaitingOrderAsync();
        await DeactivateAsync(Admin, definition.Id);

        await Admin.UpdateAsync(new Entity("asyncoperation", job.Id) { ["statecode"] = new OptionSetValue(3), ["statuscode"] = new OptionSetValue(32) });

        (await SingleJobAsync()).Should().Match<Job>(j => j.StateCode == 3 && j.StatusCode == 32);
        (await WaitsAsync()).Should().BeEmpty();
        (await WorkflowExistsAsync(job.ActivationId!.Value)).Should().BeFalse("the kept activation's last job has ended");
        (await RunJobsAsync()).Should().Be(0);
    }

    // ---- child workflows --------------------------------------------------------------------------------------------------

    private async Task<WorkflowRow> ActivateChildAsync(bool subprocess)
    {
        // The export's Child (a stop, canceled) with the id OnCreate names; children always run in the background (W12).
        var child = Realtime(Name("Child"), Real(Child), Order) with
        {
            Id = ChildId, Subprocess = subprocess, OwnerId = new EntityReference("systemuser", _adminUserId),
        };
        return await ActivateAsync(child);
    }

    [Fact]
    public async Task Child_workflow_queues_child_job()
    {
        var child = await ActivateChildAsync(subprocess: true);
        var childActivation = await ActivationIdAsync(child.Id);
        // The export's OnCreate as it is: real-time, a task then the child workflow.
        await ActivateAsync(Realtime(Name("Parent"), Real(OnCreate), Order) with { TriggerOnCreate = true, CreateStage = 40 });

        var order = await OrderAsync(10m, withAccount: false);

        (await RegardingAsync("task", order)).Should().ContainSingle();
        var job = await SingleJobAsync();
        (job.OperationType, job.ActivationId, job.RegardingObjectId, job.PrimaryEntityType).Should().Be((10, childActivation, order, Order));
        (job.Depth, job.OwnerId, job.StateCode).Should().Be((3, _adminUserId, 0));

        (await RunJobsAsync()).Should().Be(1);

        // The child's stop cancels: its job ends 3/32 (W11), naming the workflow (its stop has no message).
        var ended = await SingleJobAsync();
        (ended.StateCode, ended.StatusCode).Should().Be((3, 32));
        ended.Message.Should().Contain(child.Name);
    }

    [Fact]
    public async Task Child_must_be_subprocess()
    {
        await ActivateChildAsync(subprocess: false);
        await ActivateAsync(Realtime(Name("Parent"), Real(OnCreate), Order) with { TriggerOnCreate = true, CreateStage = 40 });

        var fault = await TestHelpers.FaultAsync(() => OrderAsync(10m, withAccount: false));

        // W12: the step fails (400) and the real-time parent rolls the Create back.
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().Contain("subprocess");
        (await JobsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Child_not_activated_fails_parent_step()
    {
        var child = await ActivateChildAsync(subprocess: true);
        await DeactivateAsync(Admin, child.Id);
        await ActivateAsync(Realtime(Name("Parent"), Real(OnCreate), Order) with { TriggerOnCreate = true, CreateStage = 40 });

        var fault = await TestHelpers.FaultAsync(() => OrderAsync(10m, withAccount: false));

        // W12: the child must be Activated.
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().Contain("is not activated");
        (await JobsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Child_on_another_table_fails_parent_step()
    {
        var child = Realtime(Name("Child"), Real(Child), "account") with
        {
            Id = ChildId, Subprocess = true, OwnerId = new EntityReference("systemuser", _adminUserId),
        };
        await ActivateAsync(child);
        await ActivateAsync(Realtime(Name("Parent"), Real(OnCreate), Order) with { TriggerOnCreate = true, CreateStage = 40 });

        var fault = await TestHelpers.FaultAsync(() => OrderAsync(10m, withAccount: false));

        // W12: the child runs on the record it is given, which must be of its primary entity.
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().Contain($"runs on 'account', not on the '{Order}' record");
        (await JobsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Child_queued_above_depth_8_fails_parent_job()
    {
        await ActivateChildAsync(subprocess: true);
        // The export's OnCreate as a background workflow (a task, then the child), run by a job at depth 8: its child would be at 9.
        var parent = await ActivateAsync(Background("Parent", Real(OnCreate), Order));
        var activation = await ActivationIdAsync(parent.Id);
        var order = await OrderAsync(10m, withAccount: false);
        await using (var scope = fx.App.Services.CreateAsyncScope())
        {
            var row = (await scope.ServiceProvider.GetRequiredService<WorkflowRegistry>().FindAsync(activation, default))!.Value.Row;
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await scope.ServiceProvider.GetRequiredService<WorkflowJobs>().QueueAsync(new DataSession(conn, null), row,
                new EntityReference(Order, order), "Create", _adminUserId, depth: 8, default);
        }

        (await RunJobsAsync()).Should().Be(1);

        // W12, RF1: the child step fails the parent job with the infinite loop fault; the task before it stays (W9); no child job.
        var failed = await SingleJobAsync();
        (failed.Depth, failed.StateCode, failed.StatusCode, failed.ErrorCode).Should().Be((8, 3, 31, ErrorCodes.InfiniteLoopDetected));
        (await RegardingAsync("task", order)).Should().ContainSingle();
    }

    [Fact]
    public async Task Self_triggering_background_chain_fails_at_depth_8()
    {
        // On an update of telephone1, the workflow updates telephone1: each job queues the next one, one level deeper.
        await ActivateAsync(Background("Self", AccountUpdate(), "account") with { TriggerOnUpdateAttributes = "telephone1" });
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Loop") });
        await Admin.UpdateAsync(new Entity("account", account) { ["telephone1"] = "555-0100" });

        await RunJobsAsync();

        // RF1: the jobs of depth 2 to 8 run; the one queued at depth 9 fails with the infinite loop fault and queues nothing.
        var jobs = await JobsAsync();
        jobs.Select(j => j.Depth).Should().Equal(2, 3, 4, 5, 6, 7, 8, 9);
        jobs.Take(7).Should().OnlyContain(j => j.StateCode == 3 && j.StatusCode == 30);
        (jobs[^1].StateCode, jobs[^1].StatusCode, jobs[^1].ErrorCode).Should().Be((3, 31, ErrorCodes.InfiniteLoopDetected));
    }

    [Fact]
    public async Task Unsupported_background_workflow_fails_job_with_text()
    {
        var row = await ActivateAsync(Background("Unsupported", WorkflowFixtures.Xaml("unsupported-expression"), "account") with
        {
            TriggerOnCreate = true,
        });
        await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Created") });

        (await RunJobsAsync()).Should().Be(1);

        // W20: the job fails explicitly (3/31), with the text of the step xrmverse cannot run.
        var failed = await SingleJobAsync();
        (failed.StateCode, failed.StatusCode, failed.ErrorCode).Should().Be((3, 31, ErrorCodes.InvalidArgument));
        failed.Message.Should().StartWith($"Workflow '{row.Name}' cannot run on xrmverse: ");
    }

    [Fact]
    public async Task Failed_cleanup_keeps_the_job_outcome()
    {
        var row = await ActivateAsync(Background("Unsupported", WorkflowFixtures.Xaml("unsupported-expression"), "account") with
        {
            TriggerOnCreate = true,
        });
        await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Created") });
        await ActivateAsync(Background("Order follow-up", OrderTask(), Order) with { TriggerOnCreate = true });
        await OrderAsync(10m, withAccount: false);

        WorkflowJobHandler.BeforeEnd.Value = () => throw new InvalidOperationException("The clean-up failed.");
        try
        {
            (await RunJobsAsync()).Should().Be(2);
        }
        finally
        {
            WorkflowJobHandler.BeforeEnd.Value = null;
        }

        // The clean-up after a job is logged when it fails; the job keeps its own outcome: its failure, or its success.
        var jobs = await JobsAsync();
        jobs.Should().ContainSingle(j => j.StatusCode == 31).Which.Message.Should().StartWith($"Workflow '{row.Name}' cannot run on xrmverse: ");
        jobs.Should().ContainSingle(j => j.StatusCode == 30);
    }

    // ---- cost --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Records the text of the SQL commands run on the fixture's database: SqlClient's diagnostic events, and on PostgreSQL the
    /// activities of Npgsql's <c>ActivitySource</c> (plan 2.7b, Task 7), whose query text and database are tags.
    /// </summary>
    private sealed class CommandRecorder : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _database;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _all;
        private readonly ActivityListener _npgsql;

        public List<string> Commands { get; } = [];

        public CommandRecorder(string database)
        {
            _database = database;
            _all = DiagnosticListener.AllListeners.Subscribe(this);
            _npgsql = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    var text = activity.GetTagItem("db.query.text") ?? activity.GetTagItem("db.statement");
                    var db = activity.GetTagItem("db.namespace") ?? activity.GetTagItem("db.name");
                    if (text is not string sql || (db is string name && name != _database)) return;
                    lock (Commands) Commands.Add(sql);
                },
            };
            ActivitySource.AddActivityListener(_npgsql);
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "SqlClientDiagnosticListener")
                lock (_subscriptions) _subscriptions.Add(listener.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> e)
        {
            if (!e.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal) || e.Value is null) return;
            if (e.Value.GetType().GetProperty("Command")?.GetValue(e.Value) is not SqlCommand command
                || command.Connection?.Database != _database) return;
            lock (Commands) Commands.Add(command.CommandText);
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            _npgsql.Dispose();
            _all.Dispose();
            lock (_subscriptions) _subscriptions.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public async Task Writes_without_waits_do_not_touch_wait_table()
    {
        await ActivateAsync(Background("Queued", OrderTask(), Order) with { TriggerOnCreate = true });
        Waits.HasAny.Should().BeFalse("no job waits");
        var database = TestProvider.DatabaseOf(fx.ConnectionString);

        List<string> commands;
        using (var recorder = new CommandRecorder(database))
        {
            var order = await OrderAsync(10m, withAccount: false);
            await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_amount"] = 20m });
            await Admin.DeleteAsync(Order, order);
            lock (recorder.Commands) commands = [.. recorder.Commands];
        }

        commands.Should().Contain(c => c.Contains("INSERT INTO data.asyncoperation"), "the recorder sees the writes' commands");
        commands.Should().NotContain(c => c.Contains("workflow_wait"));
    }

    [Fact]
    public async Task Create_does_not_touch_wait_table()
    {
        await WaitOnAsync(new EntityReference("account", Guid.NewGuid()));
        Waits.HasAny.Should().BeTrue("a job waits");
        var database = TestProvider.DatabaseOf(fx.ConnectionString);

        List<string> commands;
        using (var recorder = new CommandRecorder(database))
        {
            await Admin.CreateAsync(new Entity("account") { ["name"] = Name("New") });
            lock (recorder.Commands) commands = [.. recorder.Commands];
        }

        // No wait can be on a record that did not exist.
        commands.Should().Contain(c => c.Contains($"INSERT INTO {TestSql.Dialect.DataTable("account")}"), "the recorder sees the Create");
        commands.Should().NotContain(c => c.Contains("workflow_wait"));
    }
}
