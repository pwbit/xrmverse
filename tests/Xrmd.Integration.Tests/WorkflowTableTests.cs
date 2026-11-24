using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.Workflows;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.14, task 2: the <c>workflow</c> and <c>processtrigger</c> seed tables (W1), the public writes (W3, lifted by plan 2.21 A16), activation and
/// deactivation through SetState (W4), the Delete rules (W4), the privileges (W24) and the registry of activated processes.
/// </summary>
[Collection("xrmd")]
public sealed class WorkflowTableTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    // Its activated real-time processes (on account) would run in later classes of the collection (plan 2.14, Task 5).
    public Task InitializeAsync() => WorkflowTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => WorkflowTestHelper.ClearAsync(fx);

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    /// <summary>Saves <paramref name="row"/> (and its triggers) through the <see cref="WorkflowStore"/>, as the solution import does.</summary>
    private async Task<WorkflowRow> SaveAsync(WorkflowRow row, params Entity[] triggers)
    {
        await using var scope = fx.App.Services.CreateAsyncScope();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await scope.ServiceProvider.GetRequiredService<WorkflowStore>().SaveAsync(new DataSession(conn, null), row, triggers, default);
        return row;
    }

    private Task<WorkflowRow> DefinitionAsync(string fixture, int category = 0, int mode = 0) =>
        SaveAsync(WorkflowFixtures.Definition(fixture, $"Process {fixture} {_run}", category: category, mode: mode));

    private static Task SetStateAsync(OrganizationService service, Guid id, int state, int status) =>
        service.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(S.Table, id), State = new OptionSetValue(state), Status = new OptionSetValue(status),
        });

    private Task ActivateAsync(Guid id, OrganizationService? service = null) =>
        SetStateAsync(service ?? Admin, id, S.ActivatedState, S.ActivatedStatus);

    private Task<Entity> RetrieveAsync(Guid id) => Admin.RetrieveAsync(S.Table, id, new ColumnSet(true));

    private async Task<List<Entity>> ActivationsOfAsync(Guid definitionId) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression(S.Table)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = { Conditions = { new ConditionExpression(S.ParentWorkflowId, ConditionOperator.Equal, definitionId) } },
        })).Entities.ToList();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string json)
    {
        var req = new HttpRequestMessage(method, Api + path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    private static Entity EntityTrigger() => new(S.TriggerTable)
    {
        [S.Event] = "change", [S.PrimaryEntityTypeCode] = "account", [S.TriggerScope] = new OptionSetValue(S.EntityTriggerScope),
    };

    // ---- seed ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Seed_creates_workflow_and_processtrigger()
    {
        var workflow = Store.GetEntity(S.Table)!;
        var trigger = Store.GetEntity(S.TriggerTable)!;
        (workflow.EntitySetName, workflow.ObjectTypeCode, workflow.OwnershipType, workflow.PrimaryIdAttribute, workflow.PrimaryNameAttribute)
            .Should().Be(("workflows", 4703, OwnershipTypes.UserOwned, "workflowid", "name"));
        (trigger.EntitySetName, trigger.ObjectTypeCode, trigger.OwnershipType, trigger.PrimaryIdAttribute)
            .Should().Be(("processtriggers", 4712, OwnershipTypes.OrganizationOwned, "processtriggerid"));

        int[] Options(EntityMetadata e, string column) =>
            ((EnumAttributeMetadata)e.Attributes.Single(a => a.LogicalName == column)).OptionSet.Options.Select(o => o.Value!.Value).ToArray();
        Options(workflow, S.CategoryColumn).Should().Equal(0, 1, 2, 3, 4, 5, 6, 7);
        Options(workflow, S.ModeColumn).Should().Equal(0, 1);
        Options(workflow, S.TypeColumn).Should().Equal(1, 2, 3);
        Options(workflow, S.ScopeColumn).Should().Equal(1, 2, 3, 4);
        Options(workflow, S.RunAsColumn).Should().Equal(0, 1);
        foreach (var stage in new[] { S.CreateStage, S.UpdateStage, S.DeleteStage }) Options(workflow, stage).Should().Equal(20, 40);
        Options(workflow, "statecode").Should().Equal(0, 1);
        ((StatusAttributeMetadata)workflow.Attributes.Single(a => a.LogicalName == "statuscode")).OptionSet.Options
            .Cast<StatusOptionMetadata>().Select(o => (o.Value, o.State)).Should().Equal((1, 0), (2, 1));
        Options(trigger, S.TriggerScope).Should().Equal(1, 2);
        Options(trigger, S.ControlType).Should().Equal(1, 2);
        workflow.Attributes.Single(a => a.LogicalName == S.UnsupportedStepsColumn).Should().BeOfType<MemoAttributeMetadata>();
        ((MemoAttributeMetadata)workflow.Attributes.Single(a => a.LogicalName == S.Xaml)).MaxLength.Should().Be(1073741823);

        CascadeType DeleteCascade(string name) => ((OneToManyRelationshipMetadata)Store.GetRelationship(name)!).CascadeConfiguration.Delete!.Value;
        DeleteCascade(S.AsyncOperationsRelationship).Should().Be(CascadeType.RemoveLink);
        DeleteCascade(S.ParentRelationship).Should().Be(CascadeType.Cascade);
        DeleteCascade(S.ActiveRelationship).Should().Be(CascadeType.RemoveLink);
        DeleteCascade(S.TriggersRelationship).Should().Be(CascadeType.Cascade);
        var job = Store.GetEntity("asyncoperation")!;
        job.Attributes.Single(a => a.LogicalName == S.WorkflowActivationId).Should().BeOfType<LookupAttributeMetadata>()
            .Which.Targets.Should().Equal(S.Table);
        Options(job, "operationtype").Should().Contain(AsyncOperationQueue.Workflow);
        AsyncOperationQueue.Workflow.Should().Be(S.WorkflowOperationType).And.Be(10);
    }

    // ---- W3 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Public_create_of_processtrigger_is_refused()
    {
        // Plan 2.21 (A16): processtrigger stays refused for every public write.
        const string refusal = "processtrigger";
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity(S.TriggerTable) { [S.Event] = "load" }))).Should().Contain(refusal);

        var row = await DefinitionAsync("realtime-create-update");
        var trigger = new Entity(S.TriggerTable, Guid.NewGuid()) { [S.Event] = "save" };
        await SaveAsync(row, trigger);
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.TriggerTable, trigger.Id) { [S.Event] = "load" }))).Should().Contain(refusal);

        var trigger400 = await SendAsync(HttpMethod.Post, "processtriggers", """{"event":"load"}""");
        trigger400.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await trigger400.Content.ReadAsStringAsync()).Should().Contain(refusal);

        // A trigger goes only with its process: no direct Delete, and no Associate/Disassociate over process_processtrigger.
        (await BadRequestAsync(() => Admin.DeleteAsync(S.TriggerTable, trigger.Id))).Should().Contain(refusal);
        var other = await DefinitionAsync("realtime-create-update");
        EntityReferenceCollection Trigger() => [new EntityReference(S.TriggerTable, trigger.Id)];
        (await BadRequestAsync(() => Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference(S.Table, other.Id), Relationship = new Relationship(S.TriggersRelationship), RelatedEntities = Trigger(),
        }))).Should().Contain(refusal);
        (await BadRequestAsync(() => Admin.ExecuteAsync(new DisassociateRequest
        {
            Target = new EntityReference(S.Table, row.Id), Relationship = new Relationship(S.TriggersRelationship), RelatedEntities = Trigger(),
        }))).Should().Contain(refusal);
        var webDelete = await SendAsync(HttpMethod.Delete, $"processtriggers({trigger.Id})", "");
        webDelete.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ScalarAsync($"SELECT {S.ProcessId} FROM data.{S.TriggerTable} WHERE {S.ProcessTriggerId} = @id", ("@id", trigger.Id)))
            .Should().Be(row.Id);
    }

    [Fact]
    public async Task Public_create_of_invalid_workflow_is_refused()
    {
        // Plan 2.21 (A16): a public write of workflow is validated, no longer refused as such (2.14's W3).
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity(S.Table) { [S.Name] = "Authored " + _run }))).Should().Contain("category");
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity(S.Table)
        {
            [S.Name] = "Authored " + _run, [S.CategoryColumn] = new OptionSetValue(0),
        }))).Should().Contain("XAML");

        var row = await DefinitionAsync("realtime-create-update");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, row.Id) { ["statecode"] = new OptionSetValue(1), [S.Xaml] = "<x/>" })))
            .Should().Contain("SetState");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, row.Id) { [S.Xaml] = "<x/>" }))).Should().Contain("Activity");

        var post = await SendAsync(HttpMethod.Post, "workflows", """{"name":"Authored","category":0}""");
        post.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await post.Content.ReadAsStringAsync()).Should().Contain("XAML");
        var patch = await SendAsync(HttpMethod.Patch, $"workflows({row.Id})", """{"xaml":"<Activity"}""");
        patch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await patch.Content.ReadAsStringAsync()).Should().Contain("not well-formed");

        (await RetrieveAsync(row.Id)).GetAttributeValue<string>(S.Xaml).Should().Be(row.Xaml);
    }

    [Fact]
    public async Task Pre_operation_plugin_cannot_widen_a_SetState()
    {
        var row = await DefinitionAsync("realtime-create-update");
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetTypedAttributePlugin");
            await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", S.Table, 20, config: $"{S.ModeColumn}=option:1");

            // Plan 2.21 (A16): the widened Target changes content along with the state, which a SetState never does.
            (await BadRequestAsync(() => ActivateAsync(row.Id))).Should().Contain("SetState");
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }

        var definition = await RetrieveAsync(row.Id);
        definition.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        definition.GetAttributeValue<OptionSetValue>(S.ModeColumn).Value.Should().Be(0);
        (await ActivationsOfAsync(row.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Assign_of_a_definition_is_allowed()
    {
        var row = await DefinitionAsync("realtime-create-update");
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        var role = await CreateRoleAsync(fx, "Process owner " + _run, ("prvReadWorkflow", DepthMask.Global));
        await AssignRoleAsync(fx, user, role);

        await Admin.ExecuteAsync(new AssignRequest { Target = new EntityReference(S.Table, row.Id), Assignee = new EntityReference("systemuser", user) });

        (await RetrieveAsync(row.Id)).GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(user);
    }

    // ---- W4 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetState_activates_and_creates_activation_row()
    {
        var row = await DefinitionAsync("realtime-create-update", mode: 1);

        await ActivateAsync(row.Id);

        var definition = await RetrieveAsync(row.Id);
        (definition.GetAttributeValue<OptionSetValue>("statecode").Value, definition.GetAttributeValue<OptionSetValue>("statuscode").Value)
            .Should().Be((1, 2));
        definition.GetAttributeValue<string>(S.UnsupportedStepsColumn).Should().BeNull();
        var activation = (await ActivationsOfAsync(row.Id)).Should().ContainSingle().Subject;
        definition.GetAttributeValue<EntityReference>(S.ActiveWorkflowId).Id.Should().Be(activation.Id);
        activation.GetAttributeValue<OptionSetValue>(S.TypeColumn).Value.Should().Be(2);
        (activation.GetAttributeValue<OptionSetValue>("statecode").Value, activation.GetAttributeValue<OptionSetValue>("statuscode").Value)
            .Should().Be((1, 2));
        activation.GetAttributeValue<string>(S.Xaml).Should().Be(row.Xaml);
        activation.GetAttributeValue<string>(S.Name).Should().Be(row.Name);
        activation.GetAttributeValue<string>(S.PrimaryEntity).Should().Be("account");
        activation.GetAttributeValue<OptionSetValue>(S.ModeColumn).Value.Should().Be(1);
        activation.GetAttributeValue<OptionSetValue>(S.CreateStage).Value.Should().Be(40);
        activation.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(definition.GetAttributeValue<EntityReference>("ownerid").Id);

        // Activating again changes nothing (the state does not change).
        await ActivateAsync(row.Id);
        (await ActivationsOfAsync(row.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Activation_of_invalid_definition_is_400()
    {
        // A wait in a real-time workflow (W10).
        var row = await DefinitionAsync("background-wait", mode: 1);

        (await BadRequestAsync(() => ActivateAsync(row.Id))).Should().Contain("wait").And.Contain(row.Name);

        var definition = await RetrieveAsync(row.Id);
        definition.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        definition.GetAttributeValue<EntityReference>(S.ActiveWorkflowId).Should().BeNull();
        (await ActivationsOfAsync(row.Id)).Should().BeEmpty();

        // Malformed XAML.
        var malformed = await SaveAsync(row with { Id = Guid.NewGuid(), Mode = 0, Xaml = "<Activity" });
        (await BadRequestAsync(() => ActivateAsync(malformed.Id))).Should().Contain("cannot be activated");
        (await ActivationsOfAsync(malformed.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Activation_with_unsupported_steps_succeeds_and_lists_them()
    {
        var row = await DefinitionAsync("unsupported-expression");

        await ActivateAsync(row.Id);

        var definition = await RetrieveAsync(row.Id);
        definition.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        var listed = definition.GetAttributeValue<string>(S.UnsupportedStepsColumn);
        listed.Should().NotBeNull();
        listed!.Split('\n').Select(line => line.Split(' ', ':')[0]).Should().Equal("0/0", "0/1", "0/2");
        (await ActivationsOfAsync(row.Id)).Should().ContainSingle().Which.GetAttributeValue<string>(S.UnsupportedStepsColumn).Should().Be(listed);
    }

    [Fact]
    public async Task Activation_of_category_5_is_400()
    {
        var row = await SaveAsync(WorkflowFixtures.Definition("realtime-create-update", "Modern flow " + _run, category: 5) with { Xaml = null });

        (await BadRequestAsync(() => ActivateAsync(row.Id))).Should().Contain("category 5");

        (await RetrieveAsync(row.Id)).GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        (await ActivationsOfAsync(row.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Deactivation_deletes_activation_row()
    {
        var row = await DefinitionAsync("realtime-create-update");
        await ActivateAsync(row.Id);
        var activation = (await ActivationsOfAsync(row.Id)).Single().Id;

        await SetStateAsync(Admin, row.Id, S.DraftState, S.DraftStatus);

        var definition = await RetrieveAsync(row.Id);
        (definition.GetAttributeValue<OptionSetValue>("statecode").Value, definition.GetAttributeValue<OptionSetValue>("statuscode").Value)
            .Should().Be((0, 1));
        definition.GetAttributeValue<EntityReference>(S.ActiveWorkflowId).Should().BeNull();
        (await ActivationsOfAsync(row.Id)).Should().BeEmpty();
        (await FaultAsync(() => RetrieveAsync(activation))).HttpStatus.Should().Be(404);

        // It can be activated again: a new activation.
        await ActivateAsync(row.Id);
        (await ActivationsOfAsync(row.Id)).Should().ContainSingle().Which.Id.Should().NotBe(activation);
    }

    [Fact]
    public async Task Deactivation_keeps_activation_while_a_job_is_pending()
    {
        var row = await DefinitionAsync("realtime-create-update");
        await ActivateAsync(row.Id);
        var activation = (await ActivationsOfAsync(row.Id)).Single().Id;
        var job = await QueueJobAsync(activation, state: 0, status: 0);

        await SetStateAsync(Admin, row.Id, S.DraftState, S.DraftStatus);

        var definition = await RetrieveAsync(row.Id);
        definition.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        definition.GetAttributeValue<EntityReference>(S.ActiveWorkflowId).Should().BeNull();
        (await ActivationsOfAsync(row.Id)).Should().ContainSingle().Which.Id.Should().Be(activation);
        (await ScalarAsync($"SELECT {S.WorkflowActivationId} FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", job)))
            .Should().Be(activation);
    }

    [Fact]
    public async Task SetState_of_an_activation_is_400()
    {
        var row = await DefinitionAsync("realtime-create-update");
        await ActivateAsync(row.Id);
        var activation = (await ActivationsOfAsync(row.Id)).Single().Id;

        (await BadRequestAsync(() => SetStateAsync(Admin, activation, S.DraftState, S.DraftStatus))).Should().Contain("not a definition");
        // The state it already has is refused too, not a silent no-op (final review M5), and so is a status alone.
        (await BadRequestAsync(() => SetStateAsync(Admin, activation, S.ActivatedState, S.ActivatedStatus))).Should().Contain("not a definition");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(S.Table, activation) { ["statuscode"] = new OptionSetValue(S.ActivatedStatus) })))
            .Should().Contain("not a definition");

        (await RetrieveAsync(activation)).GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        (await RetrieveAsync(row.Id)).GetAttributeValue<EntityReference>(S.ActiveWorkflowId).Id.Should().Be(activation);
    }

    [Fact]
    public async Task Concurrent_activations_create_one_activation_row()
    {
        for (var i = 0; i < 5; i++)
        {
            var row = await DefinitionAsync("realtime-create-update");

            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => ActivateAsync(row.Id, ServiceFor(fx, fx.AdminObjectId)))));

            var activation = (await ActivationsOfAsync(row.Id)).Should().ContainSingle().Subject;
            (await RetrieveAsync(row.Id)).GetAttributeValue<EntityReference>(S.ActiveWorkflowId).Id.Should().Be(activation.Id);
        }
    }

    [Fact]
    public async Task Delete_activated_definition_is_400()
    {
        var row = await DefinitionAsync("realtime-create-update");
        await ActivateAsync(row.Id);
        var activation = (await ActivationsOfAsync(row.Id)).Single().Id;

        (await BadRequestAsync(() => Admin.DeleteAsync(S.Table, row.Id))).Should().Contain("deactivate it");
        (await BadRequestAsync(() => Admin.DeleteAsync(S.Table, activation))).Should().Contain("deactivate it");

        (await RetrieveAsync(row.Id)).GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        (await ActivationsOfAsync(row.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_draft_definition_deletes_activations()
    {
        var row = await SaveAsync(WorkflowFixtures.Definition("realtime-create-update", $"Draft {_run}"), EntityTrigger());
        // An activation a deactivation kept (a job still waited on it, W4).
        var kept = await SaveAsync(row with { Id = Guid.NewGuid(), Type = 2, ParentWorkflowId = row.Id, StateCode = 1 });
        var job = await QueueJobAsync(kept.Id);

        await Admin.DeleteAsync(S.Table, row.Id);

        (await FaultAsync(() => RetrieveAsync(row.Id))).HttpStatus.Should().Be(404);
        (await FaultAsync(() => RetrieveAsync(kept.Id))).HttpStatus.Should().Be(404);
        (await Admin.RetrieveMultipleAsync(new QueryExpression(S.TriggerTable)
        {
            Criteria = { Conditions = { new ConditionExpression(S.ProcessId, ConditionOperator.Equal, row.Id) } },
        })).Entities.Should().BeEmpty();
        // Workflow_AsyncOperations, RemoveLink: the job stays, without its activation.
        (await ScalarAsync($"SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id AND {S.WorkflowActivationId} IS NULL", ("@id", job)))
            .Should().Be(1);
    }

    /// <summary>A workflow job of <paramref name="activationId"/> (by default completed), written with SQL (no job handler exists yet).</summary>
    private async Task<Guid> QueueJobAsync(Guid activationId, int state = 3, int status = 30)
    {
        var id = Guid.NewGuid();
        await ScalarAsync($"""
            INSERT INTO data.asyncoperation (asyncoperationid, name, operationtype, {S.WorkflowActivationId}, statecode, statuscode, createdon, modifiedon)
            VALUES (@id, 'Workflow job', 10, @activation, @state, @status, SYSUTCDATETIME(), SYSUTCDATETIME())
            """, ("@id", id), ("@activation", activationId), ("@state", state), ("@status", status));
        return id;
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync();
    }

    // ---- W24 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Activate_needs_write_on_definition()
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        // Plan 2.21 (A17): the user owns the (background) definition, so neither prvActOnBehalfOfAnotherUser nor an activation
        // privilege is needed.
        var row = await SaveAsync(WorkflowFixtures.Definition("realtime-create-update", $"Process {_run}") with
        {
            OwnerId = new EntityReference("systemuser", user),
        });
        var role = await CreateRoleAsync(fx, "Process reader " + _run, ("prvReadWorkflow", DepthMask.Global));
        await AssignRoleAsync(fx, user, role);
        var reader = ServiceFor(fx, objectId);

        (await FaultAsync(() => ActivateAsync(row.Id, reader))).HttpStatus.Should().Be(403);

        (await RetrieveAsync(row.Id)).GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        (await ActivationsOfAsync(row.Id)).Should().BeEmpty();

        // The pipeline's Update check (Write on the record) answers first: the hook's own Write demand is the same check, kept for
        // the solution import, which activates without a request.

        // With Write (and nothing else), the activation goes through.
        await AddPrivilegeAsync(fx, role, "prvWriteWorkflow", DepthMask.Global);
        Cache(fx).Invalidate();
        await ActivateAsync(row.Id, reader);
        (await ActivationsOfAsync(row.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Customizer_has_workflow_privileges()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("""
            SELECT p.name, rp.privilegedepthmask FROM data.roleprivileges AS rp JOIN data.privilege AS p ON p.privilegeid = rp.privilegeid
            WHERE rp.roleid = @role
            """, conn);
        cmd.Parameters.AddWithValue("@role", StandardRoles.SystemCustomizer);
        var customizer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) customizer.Add(rd.GetString(0), rd.GetInt32(1));

        foreach (var table in new[] { "Workflow", "ProcessTrigger" })
            foreach (var verb in new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo" })
                customizer.Should().ContainKey($"prv{verb}{table}").WhoseValue.Should().Be((int)DepthMask.Global, $"prv{verb}{table}");
        foreach (var verb in new[] { "Assign", "Share" })
            customizer.Should().ContainKey($"prv{verb}Workflow").WhoseValue.Should().Be((int)DepthMask.Global);
    }

    // ---- registry -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Plan 2.14, Task 3b (W15 as amended): a business rule runs on the server when its own <c>processtriggerscope</c> is 2 (Entity),
    /// as the parity export writes it (<c>&lt;ProcessTriggerScope&gt;</c> and no <c>ProcessTriggers</c>), or when one of its triggers
    /// has <c>scope</c> 2. The column is stored and read back.
    /// </summary>
    [Fact]
    public async Task Business_rule_scope_comes_from_its_process_trigger_scope_or_an_entity_trigger()
    {
        var entity = await SaveAsync(WorkflowFixtures.Definition("business-rule-entity", $"Entity rule {_run}", primaryEntity: "contact", category: 2)
            with { ProcessTriggerScope = S.EntityTriggerScope });
        var form = await SaveAsync(WorkflowFixtures.Definition("business-rule-entity", $"Form rule {_run}", primaryEntity: "contact", category: 2)
            with { ProcessTriggerScope = 1 });
        var byTrigger = await SaveAsync(WorkflowFixtures.Definition("business-rule-entity", $"Trigger rule {_run}", primaryEntity: "contact", category: 2),
            EntityTrigger());

        (await RetrieveAsync(entity.Id)).GetAttributeValue<OptionSetValue>(S.ProcessTriggerScope).Value.Should().Be(S.EntityTriggerScope);
        (await RetrieveAsync(byTrigger.Id)).GetAttributeValue<OptionSetValue>(S.ProcessTriggerScope).Should().BeNull();
        await using var scope = fx.App.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<WorkflowStore>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var session = new DataSession(conn, null);
        (await store.GetAsync(session, entity.Id, default))!.EntityScopeRule.Should().BeTrue("its processtriggerscope is 2 (Entity)");
        (await store.GetAsync(session, entity.Id, default))!.ProcessTriggerScope.Should().Be(S.EntityTriggerScope);
        (await store.GetAsync(session, form.Id, default))!.EntityScopeRule.Should().BeFalse("its processtriggerscope is 1 (Form) and it has no trigger");
        (await store.GetAsync(session, byTrigger.Id, default))!.EntityScopeRule.Should().BeTrue("a trigger of it has scope 2");

        await ActivateAsync(entity.Id);
        var activation = (await ActivationsOfAsync(entity.Id)).Should().ContainSingle().Subject;
        (await store.GetAsync(session, activation.Id, default))!.EntityScopeRule.Should().BeTrue("the activation is a copy of the rule");
    }

    [Fact]
    public async Task Registry_reloads_after_activation_commit()
    {
        var registry = fx.App.Services.GetRequiredService<WorkflowRegistry>();
        var row = await SaveAsync(WorkflowFixtures.Definition("business-rule-entity", $"Rule {_run}", primaryEntity: "contact", category: 2),
            EntityTrigger());
        (await registry.GetActivatedAsync(default)).Should().NotContain(a => a.Row.ParentWorkflowId == row.Id);

        await ActivateAsync(row.Id);

        var (activation, definition) = (await registry.GetActivatedAsync(default)).Should().ContainSingle(a => a.Row.ParentWorkflowId == row.Id).Subject;
        activation.Type.Should().Be(2);
        activation.Category.Should().Be(2);
        activation.PrimaryEntity.Should().Be("contact");
        activation.EntityScopeRule.Should().BeTrue();
        definition.Category.Should().Be(2);
        registry.Activated.Should().Contain(a => a.Row.Id == activation.Id);

        await SetStateAsync(Admin, row.Id, S.DraftState, S.DraftStatus);

        (await registry.GetActivatedAsync(default)).Should().NotContain(a => a.Row.ParentWorkflowId == row.Id);
    }
}
