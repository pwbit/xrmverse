using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
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

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.14, Task 5 (W5, W6, W7, W8, W11, W19, W20): activated real-time workflows run inside the pipeline of the message that
/// triggers them. The parity export's OnCreate (<c>xrmv_order</c>, without its child workflow step, which runs from Task 6) and Child
/// (its stop) give the shapes they have; the provisional <c>realtime-create-update</c> (on <c>account</c>) gives a task creation that
/// reads the record and an update of the primary record.
/// </summary>
[Collection("xrmd")]
public sealed class RealtimeWorkflowTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Order = TestEntities.Order;
    private const string OnCreate = "OnCreate-3E1F0307-D8DE-F111-84E8-002248872B66";
    private const string Child = "Child-0FE1B782-E5DE-F111-84E8-002248872B66";
    private const string Plugins = "Xrmd.TestPlugins.";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsureOrderAsync(store);
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ClearAsync(fx);
    }

    public async Task DisposeAsync()
    {
        await ClearAsync(fx);
        await PluginTestHelper.ClearAsync(fx);
    }

    // ---- processes -------------------------------------------------------------------------------------------------------

    /// <summary>The export's OnCreate without its child workflow step: creates a task "TEST" regarding the order.</summary>
    private static string OrderTask() => Without(Real(OnCreate), "ChildWorkflowStep2");

    /// <summary>
    /// The provisional create step alone: a task with the subject <c>Name(subject)</c> and the account's name as description. Its
    /// regarding is left out: the provisional file sets it to the account's primary key, a Guid, which the export writes through a
    /// conversion to a reference (<c>TargetType</c>) that the file lacks.
    /// </summary>
    private string AccountTask(string subject) =>
        Remove(Replace(Without(WorkflowFixtures.Xaml("realtime-create-update"), "UpdateStep2"), "\"Call the new customer\"", $"\"{Name(subject)}\""),
            "<mxswa:SetEntityProperty Attribute=\"regardingobjectid\"[^>]*/>");

    /// <summary>The subjects of the tasks <see cref="AccountTask"/> created with any of <paramref name="subjects"/>.</summary>
    private async Task<List<string>> TaskSubjectsAsync(params string[] subjects)
    {
        var found = new List<string>();
        foreach (var subject in subjects)
            found.AddRange((await TasksAsync("subject", Name(subject))).Select(_ => subject));
        return found;
    }

    /// <summary>
    /// The provisional create-then-update on the columns the seeded account has: its update sets <c>accountnumber</c> to "Reviewed" and
    /// <c>telephone1</c> to "555-0125" (the file's Boolean and Integer literals made strings, its columns renamed).
    /// </summary>
    private static string AccountCreateUpdate()
    {
        var xaml = WorkflowFixtures.Xaml("realtime-create-update");
        xaml = Replace(xaml, "WorkflowPropertyType.Boolean, \"True\", \"Boolean\"", "WorkflowPropertyType.String, \"Reviewed\", \"String\"");
        xaml = Replace(xaml, "WorkflowPropertyType.Integer, \"25\", \"Integer\"", "WorkflowPropertyType.String, \"555-0125\", \"String\"");
        xaml = Replace(xaml, "Attribute=\"creditonhold\"", "Attribute=\"accountnumber\"");
        return Replace(xaml, "Attribute=\"numberofemployees\"", "Attribute=\"telephone1\"");
    }

    /// <summary>The update step of <see cref="AccountCreateUpdate"/> alone.</summary>
    private static string AccountUpdate() => Without(AccountCreateUpdate(), "CreateStep1");

    private string Name(string what) => $"{what} {_run}";

    private Task<WorkflowRow> ActivateAsync(WorkflowRow row) => WorkflowTestHelper.ActivateAsync(fx, Admin, row);

    private Task<WorkflowRow> OnUpdateAsync(string xaml, string columns, int stage = 40, int rank = 0) =>
        ActivateAsync(Realtime(Name("On update"), xaml, "account") with { TriggerOnUpdateAttributes = columns, UpdateStage = stage, Rank = rank });

    // ---- data -------------------------------------------------------------------------------------------------------------

    private async Task<Guid> AccountAsync(string? name = null, OrganizationService? service = null) =>
        await (service ?? Admin).CreateAsync(new Entity("account") { ["name"] = name ?? Name("Contoso") });

    private async Task<List<Entity>> TasksAsync(string column, object value) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression("task")
        {
            ColumnSet = new ColumnSet("subject", "description", "regardingobjectid", "createdby"),
            Criteria = { Conditions = { new ConditionExpression(column, ConditionOperator.Equal, value) } },
        })).Entities.ToList();

    private Task<List<Entity>> TasksRegardingAsync(Guid id) => TasksAsync("regardingobjectid", id);

    private async Task<int> CountAsync(string entity, string column, string value) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = { Conditions = { new ConditionExpression(column, ConditionOperator.Equal, value) } },
        })).Entities.Count;

    private async Task<List<string>> PipeLogAsync(Guid recordId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command($"SELECT pb_name FROM data.{TestEntities.PipeLog} WHERE pb_name LIKE @id", conn);
        cmd.Parameters.AddWithValue("@id", "%|" + recordId);
        var names = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) names.Add(rd.GetString(0));
        return names;
    }

    private async Task RegisterStepAsync(string plugin, string message, string entity, int stage, int rank = 1, string? config = null)
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes, "Xrmd.TestPlugins." + Guid.NewGuid().ToString("N")[..6]);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Plugins + plugin);
        await PluginTestHelper.RegisterStepAsync(Admin, type, message, entity, stage, rank: rank, config: config);
    }

    // ---- triggers ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Post_create_realtime_creates_task_in_same_transaction()
    {
        await ActivateAsync(Realtime(Name("Order follow-up"), OrderTask(), Order) with { TriggerOnCreate = true, CreateStage = 40 });

        var order = await Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Order") });

        // The task exists when Create returns: the workflow ran in the Create's pipeline, not in a background job.
        var task = (await TasksRegardingAsync(order)).Should().ContainSingle().Subject;
        task.GetAttributeValue<string>("subject").Should().Be("TEST");

        // In the Create's transaction (W19): a later post-operation failure takes the workflow's task with it.
        await RegisterStepAsync("ThrowPlugin", "Create", Order, 40, rank: 2, config: "invalid:after the workflow");
        var before = (await TasksAsync("subject", "TEST")).Count;
        var fault = await TestHelpers.FaultAsync(() => Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Rolled back") }));
        fault.Message.Should().Be("after the workflow");
        (await CountAsync(Order, "xrmv_name", Name("Rolled back"))).Should().Be(0);
        (await TasksAsync("subject", "TEST")).Should().HaveCount(before, "the rolled-back order's task was not committed");
    }

    [Fact]
    public async Task Pre_update_realtime_changes_target_without_extra_update()
    {
        await OnUpdateAsync(AccountUpdate(), "name", stage: 20);
        await RegisterStepAsync("CreateRecordPlugin", "Update", "account", 40, config: TestEntities.PipeLog);
        var account = await AccountAsync();

        await Admin.UpdateAsync(new Entity("account", account) { ["name"] = Name("Renamed") });

        // W8: the update step wrote into the Update's Target; no nested Update ran (it would have fired the step at depth 2).
        (await PipeLogAsync(account)).Should().Equal($"Update|40|1|{account}");
        var stored = await Admin.RetrieveAsync("account", account, new ColumnSet("name", "accountnumber", "telephone1"));
        stored.GetAttributeValue<string>("name").Should().Be(Name("Renamed"));
        stored.GetAttributeValue<string>("accountnumber").Should().Be("Reviewed");
        stored.GetAttributeValue<string>("telephone1").Should().Be("555-0125");
    }

    [Fact]
    public async Task Update_without_trigger_column_does_not_run()
    {
        await OnUpdateAsync(AccountTask("Phone changed"), "telephone1");
        var account = await AccountAsync();

        await Admin.UpdateAsync(new Entity("account", account) { ["name"] = Name("Renamed") });
        (await TaskSubjectsAsync("Phone changed")).Should().BeEmpty();

        await Admin.UpdateAsync(new Entity("account", account) { ["telephone1"] = "555-0100" });
        (await TaskSubjectsAsync("Phone changed")).Should().Equal("Phone changed");
    }

    [Fact]
    public async Task Assign_triggers_only_ownerid_lists()
    {
        await OnUpdateAsync(AccountTask("Owner changed"), "ownerid");
        await OnUpdateAsync(AccountTask("Name changed"), "name");
        var account = await AccountAsync();
        var assignee = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await GrantAsync(fx, assignee, false, ("prvReadAccount", DepthMask.Global));

        await Admin.ExecuteAsync(new AssignRequest { Target = new EntityReference("account", account), Assignee = new EntityReference("systemuser", assignee) });

        (await TaskSubjectsAsync("Owner changed", "Name changed")).Should().Equal("Owner changed");
    }

    [Fact]
    public async Task SetState_triggers_statecode_lists()
    {
        await OnUpdateAsync(AccountTask("State changed"), "statecode");
        await OnUpdateAsync(AccountTask("Name changed"), "name");
        var account = await AccountAsync();

        await Admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference("account", account), State = new OptionSetValue(1), Status = new OptionSetValue(2),
        });

        (await TaskSubjectsAsync("State changed", "Name changed")).Should().Equal("State changed");
    }

    [Fact]
    public async Task Unchanged_value_still_triggers()
    {
        await OnUpdateAsync(AccountTask("Name sent"), "name");
        var account = await AccountAsync(Name("Same"));

        // RF2: a column present in the request counts even with its stored value, as for plug-in filtering attributes.
        await Admin.UpdateAsync(new Entity("account", account) { ["name"] = Name("Same") });

        (await TaskSubjectsAsync("Name sent")).Should().Equal("Name sent");
    }

    [Fact]
    public async Task Rank_orders_plugins_and_workflows()
    {
        // Stage 20 of an Update of the name: plug-in rank 1 sets "first", the workflow rank 1 copies the name it sees into its task's
        // description, plug-in rank 2 sets "third". W6: equal rank, the plug-in first.
        await RegisterStepAsync("SetAttributePlugin", "Update", "account", 20, rank: 1, config: "name=first");
        await OnUpdateAsync(AccountTask("Ranked"), "name", stage: 20, rank: 1);
        await RegisterStepAsync("SetAttributePlugin", "Update", "account", 20, rank: 2, config: "name=third");
        var account = await AccountAsync();

        await Admin.UpdateAsync(new Entity("account", account) { ["name"] = Name("Sent") });

        (await TasksAsync("subject", Name("Ranked"))).Should().ContainSingle().Which.GetAttributeValue<string>("description").Should().Be("first");
        (await Admin.RetrieveAsync("account", account, new ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be("third");
    }

    [Fact]
    public async Task Runas_owner_vs_calling_user()
    {
        // A user who may create accounts but not tasks; the workflows belong to the administrator.
        var root = await RootBusinessUnitAsync(fx);
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, root, objectId);
        await GrantAsync(fx, user, false, ("prvCreateAccount", DepthMask.Global), ("prvReadAccount", DepthMask.Global));
        var asUser = ServiceFor(fx, objectId);
        var admin = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        var owner = new EntityReference("systemuser", admin.UserId);

        // W7: runas 0 runs the steps as the workflow's owner.
        var byOwner = await ActivateAsync(Realtime(Name("As owner"), AccountTask("As owner"), "account") with
        {
            TriggerOnCreate = true, CreateStage = 40, RunAs = 0, OwnerId = owner,
        });
        var account = await AccountAsync(service: asUser);
        var task = (await TasksAsync("subject", Name("As owner"))).Should().ContainSingle().Subject;
        task.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(admin.UserId);
        task.GetAttributeValue<string>("description").Should().Be(Name("Contoso"));
        await DeactivateAsync(Admin, byOwner.Id);

        // runas 1 runs them as the caller, who cannot create the task: the Create fails and nothing stays.
        await ActivateAsync(Realtime(Name("As caller"), AccountTask("As caller"), "account") with
        {
            TriggerOnCreate = true, CreateStage = 40, RunAs = 1, OwnerId = owner,
        });
        var fault = await TestHelpers.FaultAsync(() => AccountAsync(Name("Denied"), asUser));
        fault.HttpStatus.Should().Be(403);
        (await CountAsync("account", "name", Name("Denied"))).Should().Be(0);
    }

    // ---- failures -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_canceled_fails_request_with_message()
    {
        // The export's Child (a stop, canceled) with a message instead of its empty one.
        var stop = Replace(Real(Child), "WorkflowPropertyType.String, \"\", \"String\"", "WorkflowPropertyType.String, \"Orders need an approval\", \"String\"");
        await ActivateAsync(Realtime(Name("Stop"), stop, Order) with { TriggerOnCreate = true, CreateStage = 40 });

        var fault = await TestHelpers.FaultAsync(() => Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Stopped") }));

        // W11: InvalidPluginExecutionException semantics (400, the plug-in abort code, the step's message); the Create rolls back.
        (fault.HttpStatus, fault.ErrorCode, fault.Message).Should().Be((400, ErrorCodes.ISVAborted, "Orders need an approval"));
        (await CountAsync(Order, "xrmv_name", Name("Stopped"))).Should().Be(0);
    }

    [Fact]
    public async Task Stop_canceled_without_message_names_the_workflow()
    {
        // The export's Child as it is: its stop has an empty message.
        await ActivateAsync(Realtime(Name("Silent stop"), Real(Child), Order) with { TriggerOnCreate = true, CreateStage = 40 });

        var fault = await TestHelpers.FaultAsync(() => Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Stopped") }));

        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.ISVAborted));
        fault.Message.Should().Contain(Name("Silent stop"));
    }

    [Fact]
    public async Task Failing_realtime_step_rolls_back_everything()
    {
        // The provisional create-then-update, its update made to fail (a column the account does not have).
        var failing = Replace(AccountCreateUpdate(), "Attribute=\"telephone1\"", "Attribute=\"xrmv_nosuchcolumn\"");
        await ActivateAsync(Realtime(Name("Failing"), failing, "account") with { TriggerOnCreate = true, CreateStage = 40 });

        var fault = await TestHelpers.FaultAsync(() => AccountAsync(Name("Rolled back")));

        // RF3: the account, the workflow's task and its nested update are all rolled back.
        fault.HttpStatus.Should().Be(400);
        (await CountAsync("account", "name", Name("Rolled back"))).Should().Be(0);
        (await TasksAsync("description", Name("Rolled back"))).Should().BeEmpty();
    }

    [Fact]
    public async Task Self_updating_realtime_workflow_stops_at_depth_8()
    {
        // Post-operation on its own column: each update the workflow makes triggers it again, one level deeper.
        await OnUpdateAsync(AccountUpdate(), "telephone1");
        var account = await AccountAsync();

        var fault = await TestHelpers.FaultAsync(() => Admin.UpdateAsync(new Entity("account", account) { ["telephone1"] = "555-0100" }));

        // RF1: the depth limit of 8, with the fault already worded for workflows; nothing of the chain stays.
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InfiniteLoopDetected));
        (await Admin.RetrieveAsync("account", account, new ColumnSet("telephone1"))).Contains("telephone1").Should().BeFalse();
    }

    [Fact]
    public async Task Trigger_of_unsupported_workflow_fails_explicitly()
    {
        var row = await ActivateAsync(Realtime(Name("Unsupported"), WorkflowFixtures.Xaml("unsupported-expression"), "account") with
        {
            TriggerOnCreate = true, CreateStage = 40,
        });

        var fault = await TestHelpers.FaultAsync(() => AccountAsync(Name("Never created")));

        // RF5, W20: the trigger fails explicitly rather than skipping the steps xrmverse cannot run.
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().StartWith($"Workflow '{row.Name}' cannot run on xrmverse: ");
        (await CountAsync("account", "name", Name("Never created"))).Should().Be(0);
    }

    [Fact]
    public async Task Delete_pre_operation_realtime_reads_record()
    {
        // A task whose description is the account's name, read at stage 20 of the Delete (the record still exists).
        await ActivateAsync(Realtime(Name("On delete"), AccountTask("Deleted account"), "account") with
        {
            TriggerOnDelete = true, DeleteStage = 20,
        });
        var account = await AccountAsync(Name("Doomed"));

        await Admin.DeleteAsync("account", account);

        (await TasksAsync("subject", Name("Deleted account"))).Should().ContainSingle().Which.GetAttributeValue<string>("description").Should().Be(Name("Doomed"));
        (await CountAsync("account", "name", Name("Doomed"))).Should().Be(0);
    }

    [Fact]
    public async Task Deactivated_workflow_does_not_run()
    {
        var draft = await ActivateAsync(Realtime(Name("Draft"), AccountTask("Draft"), "account") with { TriggerOnCreate = true, CreateStage = 40 });
        await DeactivateAsync(Admin, draft.Id);

        await AccountAsync();

        (await TaskSubjectsAsync("Draft")).Should().BeEmpty();
    }

    // ---- cascades, scope 3 and unparsable XAML (fix round 1) -----------------------------------------------------------

    /// <summary>A new table that <c>account</c> points to with a Cascade delete lookup <c>new_cascadeparentid{run}</c>.</summary>
    private async Task<(string Table, string Lookup)> CascadeParentOfAccountAsync()
    {
        var table = $"new_cascadeparent{_run}";
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = table, SchemaName = table, DisplayName = TestHelpers.Label("Parent " + _run),
                DisplayCollectionName = TestHelpers.Label("Parents " + _run), OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = TestHelpers.Label("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
        });
        var lookup = $"new_cascadeparentid{_run}";
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = $"new_account_{lookup}", ReferencedEntity = table, ReferencingEntity = "account",
                CascadeConfiguration = new CascadeConfiguration { Delete = CascadeType.Cascade },
            },
            Lookup = new LookupAttributeMetadata { LogicalName = lookup, SchemaName = lookup, DisplayName = TestHelpers.Label("Cascade parent") },
        });
        return (table, lookup);
    }

    [Fact]
    public async Task Cascaded_delete_does_not_trigger_the_child_delete_workflow()
    {
        // W5: the writes the core makes as a cascade do not trigger workflows; plug-in steps still fire on them as before.
        var (parentTable, lookup) = await CascadeParentOfAccountAsync();
        await ActivateAsync(Realtime(Name("On delete"), AccountTask("Deleted account"), "account") with { TriggerOnDelete = true, DeleteStage = 20 });
        await RegisterStepAsync("CreateRecordPlugin", "Delete", "account", 20, config: TestEntities.PipeLog);
        var parent = await Admin.CreateAsync(new Entity(parentTable) { ["new_name"] = Name("Parent") });
        var cascaded = await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Cascaded"), [lookup] = new EntityReference(parentTable, parent) });

        await Admin.DeleteAsync(parentTable, parent);

        (await CountAsync("account", "name", Name("Cascaded"))).Should().Be(0, "the cascade deleted the account");
        (await TasksAsync("subject", Name("Deleted account"))).Should().BeEmpty("a cascaded delete triggers no workflow");
        (await PipeLogAsync(cascaded)).Should().Equal($"Delete|20|2|{cascaded}");

        // A direct delete of the child still triggers it.
        var direct = await AccountAsync(Name("Direct"));
        await Admin.DeleteAsync("account", direct);
        (await TasksAsync("subject", Name("Deleted account"))).Should().ContainSingle().Which.GetAttributeValue<string>("description")
            .Should().Be(Name("Direct"));
    }

    [Fact]
    public async Task Scope_parent_child_business_units_through_the_pipeline()
    {
        // W5 scope 3: the workflow's owner is in unit A; a record owned in a child unit of A triggers it, one in a sibling unit of A does not.
        var root = await RootBusinessUnitAsync(fx);
        var unitA = await CreateBusinessUnitAsync(fx, root, "A " + _run);
        var child = await CreateBusinessUnitAsync(fx, unitA, "A child " + _run);
        var sibling = await CreateBusinessUnitAsync(fx, root, "A sibling " + _run);
        var workflowOwner = await CreateUserAsync(fx, unitA);
        var inChild = await CreateUserAsync(fx, child);
        var inSibling = await CreateUserAsync(fx, sibling);
        foreach (var user in new[] { inChild, inSibling })
            await GrantAsync(fx, user, false, ("prvReadAccount", DepthMask.Global));
        await ActivateAsync(Realtime(Name("Parent: child units"), AccountTask("Scoped"), "account") with
        {
            TriggerOnCreate = true, CreateStage = 40, Scope = 3, OwnerId = new EntityReference("systemuser", workflowOwner),
        });

        await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Child unit"), ["ownerid"] = new EntityReference("systemuser", inChild) });
        await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Sibling unit"), ["ownerid"] = new EntityReference("systemuser", inSibling) });

        (await TasksAsync("subject", Name("Scoped"))).Select(t => t.GetAttributeValue<string>("description")).Should().Equal(Name("Child unit"));
    }

    [Fact]
    public async Task Activated_process_whose_xaml_no_longer_parses_fails_explicitly()
    {
        var row = await ActivateAsync(Realtime(Name("Broken"), AccountTask("Broken"), "account") with { TriggerOnCreate = true, CreateStage = 40 });
        // The activation's XAML replaced, as Internal, by text that does not parse (activation would have refused it).
        await using (var scope = fx.App.Services.CreateAsyncScope())
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
                        var session = new DataSession(conn, null);
            var store = scope.ServiceProvider.GetRequiredService<WorkflowStore>();
            var activationId = (await store.GetAsync(session, row.Id, default))!.ActiveWorkflowId!.Value;
            var activation = (await store.GetAsync(session, activationId, default))!;
            await store.SaveAsync(session, activation with { Xaml = "<Activity><not closed" }, [], default);
        }

        var fault = await TestHelpers.FaultAsync(() => AccountAsync(Name("Not created")));

        // Never skipped silently: the trigger fails with the W20 path.
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().StartWith($"Workflow '{row.Name}' cannot run on xrmverse: ").And.Contain("does not parse");
        (await CountAsync("account", "name", Name("Not created"))).Should().Be(0);
    }
}
