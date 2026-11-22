using FluentAssertions;
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
using Xrmd.Pipeline.Registration;
using Xrmd.Schema;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.Workflows;
using Xrmd.Workflows.Authoring;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.21, Task 6: the authoring service (<see cref="WorkflowAuthoring"/>, A13–A15, A19). It runs on the database of the parity export
/// (the "real-export" collection): its <c>xrmv_order</c>, its publisher <c>xrmv</c> and its five processes, imported once. Each test
/// authors processes of names (and so ids) of its own, as a System Customizer unless stated.
/// </summary>
[Collection("real-export")]
public sealed class WorkflowAuthoringTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Order = "xrmv_order";
    private static readonly Guid ImportedOnCreate = Guid.Parse("3e1f0307-d8de-f111-84e8-002248872b66");

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private Caller? _customizer;

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await AuthoringTestHelper.EnsureParityAsync(fx);
        await WorkflowTestHelper.ClearAsync(fx);
        await AuthoringTestHelper.ExecAsync(fx, "DELETE FROM meta.workflow_wait; DELETE FROM data.asyncoperation;");
    }

    public async Task DisposeAsync()
    {
        await WorkflowTestHelper.ClearAsync(fx);
        await AuthoringTestHelper.ExecAsync(fx, "DELETE FROM meta.workflow_wait; DELETE FROM data.asyncoperation;");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------

    private string Name(string what) => $"{what} {_run}";

    private async Task<Caller> CustomizerAsync() =>
        _customizer ??= await AuthoringTestHelper.UserWithRoleAsync(fx, StandardRoles.SystemCustomizer);

    private Task<Caller> AdminAsync() => TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);

    private async Task<ApplyResult> ApplyAsync(string yaml, Caller? caller = null, string? solution = null, bool activate = false,
        bool dryRun = false, IServiceProvider? services = null)
    {
        await using var scope = (services ?? fx.App.Services).CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowAuthoring>()
            .ApplyAsync(caller ?? await CustomizerAsync(), yaml, new ApplyOptions(solution, activate, dryRun), default);
    }

    private async Task<T> AuthoringAsync<T>(Func<WorkflowAuthoring, Task<T>> call)
    {
        await using var scope = fx.App.Services.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<WorkflowAuthoring>());
    }

    /// <summary>The compiled result of <paramref name="yaml"/> in this organization (what an apply writes).</summary>
    private async Task<CompileResult> CompileAsync(string yaml, string? solution = null)
    {
        await using var scope = fx.App.Services.CreateAsyncScope();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var env = await scope.ServiceProvider.GetRequiredService<WorkflowAuthoring>()
            .EnvironmentAsync(new DataSession(conn, null), await AdminAsync(), default);
        return await WorkflowCompiler.CompileAsync(yaml, env, solution, default);
    }

    /// <summary>A real-time workflow on create of an order that creates a task regarding it.</summary>
    private static string Realtime(string name, Guid? id = null, string subject = "Hi", string entity = Order) => $$"""
        kind: workflow
        {{(id is { } given ? $"id: {given:D}\n" : "")}}name: {{name}}
        entity: {{entity}}
        mode: realtime
        trigger:
          create: true
        steps:
          - create: { entity: task, set: { subject: {{subject}}, regardingobjectid: { record: primary } } }

        """;

    /// <summary>A background workflow on update of the status of an order: waits until its amount is above 100, then renames it.</summary>
    private static string Waiting(string name, string rename) => $$"""
        kind: workflow
        name: {{name}}
        entity: {{Order}}
        mode: background
        trigger:
          update: { columns: [xrmv_status] }
        steps:
          - wait:
              until: { field: xrmv_amount, op: gt, value: 100 }
              then:
                - update: { record: primary, set: { xrmv_name: {{rename}} } }

        """;

    private static string Rule(string name, Guid? id = null, string entity = Order) => $$"""
        kind: businessRule
        {{(id is { } given ? $"id: {given:D}\n" : "")}}name: {{name}}
        entity: {{entity}}
        steps:
          - if:
              when: { field: xrmv_amount, op: null }
              then:
                - showError: { field: xrmv_status, message: Amount is missing }

        """;

    private static string Action(string name, string message, Guid? id = null) => $$"""
        kind: action
        {{(id is { } given ? $"id: {given:D}\n" : "")}}name: {{name}}
        entity: {{Order}}
        message: {{message}}
        steps:
          - update: { record: primary, set: { xrmv_name: acted } }

        """;

    private async Task<WorkflowRow?> RowAsync(Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT COUNT(*) FROM data.workflow WHERE workflowid = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0) return null;
        return WorkflowStore.FromEntity(await Admin.RetrieveAsync(S.Table, id, new ColumnSet(true)));
    }

    private Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters) =>
        AuthoringTestHelper.ScalarAsync(fx, sql, parameters);

    private async Task<long> VersionAsync(Guid id) => (long)(await ScalarAsync("SELECT CAST(versionnumber AS bigint) FROM data.workflow WHERE workflowid = @id", ("@id", id)))!;

    private async Task<int> InSolutionAsync(string solution, Guid id) => Convert.ToInt32(await ScalarAsync("""
        SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
        WHERE s.uniquename = @s AND sc.objectid = @id AND sc.componenttype = 29
        """, ("@s", solution), ("@id", id)));

    private async Task<(Guid User, Caller Caller)> UserAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, team: false, privileges);
        return (user, await TestServices.CallerAsync(fx.App.Services, objectId));
    }

    private AsyncOperationWorker NewWorker() =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100) }), null, TimeProvider.System);

    private async Task<int> RunJobsAsync()
    {
        var worker = NewWorker();
        var ran = 0;
        while (ran < 50 && await worker.RunOnceAsync(default)) ran++;
        return ran;
    }

    private async Task<Entity> JobOfAsync(Guid order) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression("asyncoperation")
        {
            ColumnSet = new ColumnSet("statecode", "statuscode", "message", S.WorkflowActivationId),
            Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, order) } },
        })).Entities.Should().ContainSingle().Subject;

    // ---- apply: create, activate, unchanged (A13, A14, RF1, RF2) --------------------------------------------------------------

    [Fact]
    public async Task Apply_creates_draft_with_compiled_row()
    {
        var yaml = Realtime(Name("Created"));
        var customizer = await CustomizerAsync();

        var result = await ApplyAsync(yaml);

        result.Errors.Should().BeEmpty();
        (result.Action, result.State, result.StateChanged, result.Name).Should().Be((ApplyAction.Created, "draft", false, Name("Created")));
        var expected = (await CompileAsync(yaml)).Row;
        result.Id.Should().Be(expected.Id).And.Be(WorkflowIdentity.IdOf(WorkflowKind.Workflow, Order, Name("Created")));
        var stored = (await RowAsync(result.Id))!;
        stored.OwnerId!.Id.Should().Be(customizer.UserId, "the process is written as the caller");
        (stored with { OwnerId = null, OwningBusinessUnitId = null }).Should().Be(expected);
        (await InSolutionAsync("Default", result.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Apply_with_activate_activates()
    {
        var result = await ApplyAsync(Realtime(Name("Activated")), activate: true);

        (result.Action, result.State, result.StateChanged).Should().Be((ApplyAction.Created, "activated", true));
        var row = (await RowAsync(result.Id))!;
        row.StateCode.Should().Be(S.ActivatedState);
        row.ActiveWorkflowId.Should().NotBeNull();
        (await RowAsync(row.ActiveWorkflowId!.Value))!.ParentWorkflowId.Should().Be(result.Id);

        // Activate again: already activated, nothing changes.
        var again = await ApplyAsync(Realtime(Name("Activated")), activate: true);
        (again.Action, again.State, again.StateChanged).Should().Be((ApplyAction.Unchanged, "activated", false));
        (await RowAsync(result.Id))!.ActiveWorkflowId.Should().Be(row.ActiveWorkflowId);
    }

    [Fact]
    public async Task Apply_unchanged_file_writes_nothing()
    {
        // RF1: an unchanged file re-applied to an Activated process writes nothing at all.
        var yaml = Realtime(Name("Unchanged"));
        var created = await ApplyAsync(yaml, activate: true);
        var before = (await RowAsync(created.Id))!;
        var version = await VersionAsync(created.Id);
        var modified = await ScalarAsync("SELECT modifiedon FROM data.workflow WHERE workflowid = @id", ("@id", created.Id));
        var audits = await ScalarAsync("SELECT COUNT(*) FROM data.audit WHERE objectid = @id", ("@id", created.Id));

        var result = await ApplyAsync(yaml);

        (result.Action, result.State, result.StateChanged).Should().Be((ApplyAction.Unchanged, "activated", false));
        result.Errors.Should().BeEmpty();
        (await VersionAsync(created.Id)).Should().Be(version, "no Update and no SetState ran");
        (await ScalarAsync("SELECT modifiedon FROM data.workflow WHERE workflowid = @id", ("@id", created.Id))).Should().Be(modified);
        (await ScalarAsync("SELECT COUNT(*) FROM data.audit WHERE objectid = @id", ("@id", created.Id))).Should().Be(audits);
        (await RowAsync(created.Id))!.ActiveWorkflowId.Should().Be(before.ActiveWorkflowId, "the activation is untouched");
    }

    [Fact]
    public async Task Apply_changed_activated_workflow_keeps_waiting_job_on_old_activation()
    {
        // RF2, as the administrator (the background job runs as the process's owner, who must be able to rename the order).
        var admin = await AdminAsync();
        var created = await ApplyAsync(Waiting(Name("Waiting"), "old"), admin, activate: true);
        var old = (await RowAsync(created.Id))!.ActiveWorkflowId!.Value;
        var order = await Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Order"), ["xrmv_amount"] = 50m });
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_status"] = new OptionSetValue(128830001) });
        (await RunJobsAsync()).Should().Be(1);
        var job = await JobOfAsync(order);
        (job.GetAttributeValue<OptionSetValue>("statecode").Value, job.GetAttributeValue<OptionSetValue>("statuscode").Value).Should().Be((1, 10));

        var changed = await ApplyAsync(Waiting(Name("Waiting"), "new"), admin);

        (changed.Action, changed.State, changed.StateChanged).Should().Be((ApplyAction.Updated, "activated", false));
        var definition = (await RowAsync(created.Id))!;
        definition.StateCode.Should().Be(S.ActivatedState);
        definition.ActiveWorkflowId.Should().NotBe(old, "the changed definition is activated again");
        (await RowAsync(old)).Should().NotBeNull("the waiting job keeps its activation");
        (await JobOfAsync(order)).GetAttributeValue<EntityReference>(S.WorkflowActivationId).Id.Should().Be(old);

        // The waiting job completes with the old activation.
        await Admin.UpdateAsync(new Entity(Order, order) { ["xrmv_amount"] = 150m });
        (await RunJobsAsync()).Should().Be(1);
        (await Admin.RetrieveAsync(Order, order, new ColumnSet("xrmv_name"))).GetAttributeValue<string>("xrmv_name").Should().Be("old");

        // A new trigger uses the new activation.
        var other = await Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Other"), ["xrmv_amount"] = 150m });
        await Admin.UpdateAsync(new Entity(Order, other) { ["xrmv_status"] = new OptionSetValue(128830001) });
        (await RunJobsAsync()).Should().BeGreaterThan(0);
        (await Admin.RetrieveAsync(Order, other, new ColumnSet("xrmv_name"))).GetAttributeValue<string>("xrmv_name").Should().Be("new");
    }

    // ---- identity (A13) ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_without_id_gives_same_id_in_two_organizations()
    {
        var yaml = Realtime(Name("Portable"), entity: "account");
        var first = await ApplyAsync(yaml, await AdminAsync());

        await using var app = fx.WithOwnDatabase(_ => { });
        var second = await ApplyAsync(yaml, await TestServices.CallerAsync(app.Services, fx.AdminObjectId), services: app.Services);

        first.Errors.Should().BeEmpty();
        second.Errors.Should().BeEmpty();
        second.Id.Should().Be(first.Id).And.Be(WorkflowIdentity.IdOf(WorkflowKind.Workflow, "account", Name("Portable")));
    }

    [Fact]
    public async Task Apply_refuses_same_name_with_other_id()
    {
        var existing = await ApplyAsync(Realtime(Name("Twin")));
        var other = Guid.NewGuid();

        var result = await ApplyAsync(Realtime(Name("Twin"), other));

        result.Errors.Should().ContainSingle().Which.Message.Should().Contain(existing.Id.ToString("D")).And.Contain("already exists");
        (await RowAsync(other)).Should().BeNull();
    }

    [Fact]
    public async Task Apply_with_explicit_id_updates_that_process()
    {
        var id = Guid.NewGuid();
        (await ApplyAsync(Realtime(Name("Explicit"), id))).Action.Should().Be(ApplyAction.Created);

        var renamed = Realtime(Name("Explicit renamed"), id, subject: "Changed");
        var result = await ApplyAsync(renamed);

        (result.Id, result.Action).Should().Be((id, ApplyAction.Updated));
        var row = (await RowAsync(id))!;
        row.Name.Should().Be(Name("Explicit renamed"));
        row.Xaml.Should().Be((await CompileAsync(renamed)).Xaml);
    }

    [Fact]
    public async Task Apply_cannot_change_kind_entity_or_message()
    {
        var solution = await AuthoringTestHelper.SolutionAsync(fx, "xa_kind" + _run);
        var id = Guid.NewGuid();
        await ApplyAsync(Realtime(Name("Fixed"), id));
        var before = await VersionAsync(id);

        (await ApplyAsync(Rule(Name("Fixed"), id))).Errors.Should().ContainSingle().Which.Message.Should().Contain("category");
        (await ApplyAsync(Realtime(Name("Fixed"), id, entity: "account"))).Errors.Should().ContainSingle().Which.Message.Should().Contain("table");
        (await VersionAsync(id)).Should().Be(before);

        var action = Guid.NewGuid();
        (await ApplyAsync(Action(Name("Act"), $"xrmv_Act{_run}", action), solution: solution)).Errors.Should().BeEmpty();
        (await ApplyAsync(Action(Name("Act"), $"xrmv_Other{_run}", action), solution: solution)).Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("message");
        (await RowAsync(action))!.UniqueName.Should().Be($"Act{_run}");
    }

    [Fact]
    public async Task Perform_action_resolves_actions_custom_apis_and_core_messages()
    {
        // The messages a 2.14 action step executes (carried from Task 3, ruling R6): a XAML action (the imported Recalculate, Draft),
        // a custom API and a core message, each with its arguments typed for the step's inputs.
        var api = $"new_Score{_run}";
        var apiId = await Admin.CreateAsync(new Entity(CustomApiTables.Api)
        {
            ["uniquename"] = api, ["name"] = api, ["bindingtype"] = new OptionSetValue(0), ["isfunction"] = false,
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(0),
        });
        await Admin.CreateAsync(new Entity(CustomApiTables.RequestParameter)
        {
            ["uniquename"] = "Factor", ["name"] = "Factor", ["type"] = new OptionSetValue((int)CustomApiType.Decimal), ["isoptional"] = false,
            ["customapiid"] = new EntityReference(CustomApiTables.Api, apiId),
        });
        await Admin.CreateAsync(new Entity(CustomApiTables.ResponseProperty)
        {
            ["uniquename"] = "Score", ["name"] = "Score", ["type"] = new OptionSetValue((int)CustomApiType.Decimal),
            ["customapiid"] = new EntityReference(CustomApiTables.Api, apiId),
        });
        fx.App.Services.GetRequiredService<Xrmd.Pipeline.CustomApis.CustomApiRegistry>().Invalidate();
        string Calling(string steps) => Realtime(Name("Caller")).Replace(
            "  - create: { entity: task, set: { subject: Hi, regardingobjectid: { record: primary } } }", steps, StringComparison.Ordinal);
        try
        {
            var compiled = await CompileAsync(Calling($$"""
                  - performAction: { message: xrmv_Recalculate, target: primary, inputs: { Increment: 5 } }
                  - performAction: { message: {{api}}, inputs: { Factor: 2.5 } }
                  - performAction: { message: Assign, target: primary, inputs: { Assignee: { field: ownerid } } }
                  - performAction: { message: SetState, inputs: { EntityMoniker: { record: primary }, State: { field: statecode }, Status: { field: statuscode } } }
                """));
            compiled.Definition.Unsupported.Should().BeEmpty();

            var thrown = await ((Func<Task>)(() => CompileAsync(Calling($$"""
                  - performAction: { message: xrmv_Recalculate, target: primary, inputs: { Nope: 5 } }
                  - performAction: { message: {{api}}, inputs: { Nope: 2.5 } }
                  - performAction: { message: Assign, target: primary, inputs: { Nope: { field: ownerid } } }
                  - performAction: { message: SetState, inputs: { Nope: { field: statecode } } }
                  - performAction: { message: xrmv_Nothing{{_run}} }
                """)))).Should().ThrowAsync<WorkflowCompileException>();
            thrown.Which.Errors.Select(e => e.Message).Should().SatisfyRespectively(
                e => e.Should().Contain("Increment"),
                e => e.Should().Contain("Factor"),
                e => e.Should().Contain("Assignee"),
                e => e.Should().Contain("EntityMoniker"),
                e => e.Should().Contain($"xrmv_Nothing{_run}"));
        }
        finally
        {
            await Admin.DeleteAsync(CustomApiTables.Api, apiId);
        }
    }

    // ---- solutions (A15) --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_with_solution_adds_component_29()
    {
        var solution = await AuthoringTestHelper.SolutionAsync(fx, "xa_add" + _run, version: "1.2.0.0");

        var result = await ApplyAsync(Realtime(Name("In solution")), solution: solution);

        result.Errors.Should().BeEmpty();
        (await InSolutionAsync(solution, result.Id)).Should().Be(1);
        (await RowAsync(result.Id))!.IntroducedVersion.Should().Be("1.2.0.0");
    }

    [Fact]
    public async Task Apply_names_the_solution_in_another_casing_and_with_trailing_spaces()
    {
        // Final review I4: the solution's unique name is compared as SQL Server's collation compares it, on both engines (case and
        // trailing spaces ignored): its version, its publisher's prefix, the component check and the AddSolutionComponent.
        var solution = await AuthoringTestHelper.SolutionAsync(fx, "xa_Case" + _run, version: "1.4.0.0");
        var other = solution.ToUpperInvariant() + "  ";

        var result = await ApplyAsync(Realtime(Name("Cased")), solution: other);

        result.Errors.Should().BeEmpty();
        (await InSolutionAsync(solution, result.Id)).Should().Be(1);
        (await RowAsync(result.Id))!.IntroducedVersion.Should().Be("1.4.0.0", "the version is the named solution's");
        (await ApplyAsync(Realtime(Name("Cased")), solution: solution.ToLowerInvariant())).Action.Should().Be(ApplyAction.Unchanged);
        (await InSolutionAsync(solution, result.Id)).Should().Be(1, "the process already is in the solution");

        var id = Guid.NewGuid();
        (await ApplyAsync(Action(Name("Cased action"), $"xrmv_Cased{_run}", id), solution: other)).Errors
            .Should().BeEmpty("the prefix is the named solution's publisher's (xrmv), not the Default solution's (new)");
    }

    [Fact]
    public async Task Apply_existing_process_to_other_solution_adds_it()
    {
        var first = await AuthoringTestHelper.SolutionAsync(fx, "xa_first" + _run);
        var second = await AuthoringTestHelper.SolutionAsync(fx, "xa_second" + _run);
        var created = await ApplyAsync(Realtime(Name("Moved")), solution: first);

        var result = await ApplyAsync(Realtime(Name("Moved")), solution: second);

        result.Errors.Should().BeEmpty();
        result.Action.Should().Be(ApplyAction.Unchanged);
        (await InSolutionAsync(first, created.Id)).Should().Be(1);
        (await InSolutionAsync(second, created.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Action_prefix_must_match_solution_publisher()
    {
        var solution = await AuthoringTestHelper.SolutionAsync(fx, "xa_prefix" + _run);
        var id = Guid.NewGuid();

        var result = await ApplyAsync(Action(Name("Prefixed"), $"zz_Act{_run}", id), solution: solution);

        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("'zz'").And.Contain("'xrmv'");
        (await RowAsync(id)).Should().BeNull();
        (await ApplyAsync(Action(Name("Prefixed"), $"xrmv_Act{_run}", id), solution: solution)).Errors.Should().BeEmpty();
        (await RowAsync(id))!.UniqueName.Should().Be($"Act{_run}");
    }

    // ---- dry run and errors (A19, A10) -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dry_run_writes_nothing_and_reports_action_and_assumed_shapes()
    {
        var yaml = Realtime(Name("Dry")).Replace("  - create:", "  - stop: { status: succeeded }\n  - create:", StringComparison.Ordinal);

        var dry = await ApplyAsync(yaml, dryRun: true);

        (dry.Action, dry.State, dry.StateChanged).Should().Be((ApplyAction.Created, "draft", false));
        dry.AssumedShapes.Should().Contain("stop:succeeded");
        (await RowAsync(dry.Id)).Should().BeNull();

        await ApplyAsync(yaml);
        var version = await VersionAsync(dry.Id);
        var changed = await ApplyAsync(yaml.Replace("subject: Hi", "subject: Changed", StringComparison.Ordinal), activate: true, dryRun: true);
        (changed.Action, changed.State, changed.StateChanged).Should().Be((ApplyAction.Updated, "activated", true));
        (await VersionAsync(dry.Id)).Should().Be(version);
        (await RowAsync(dry.Id))!.StateCode.Should().Be(S.DraftState);
    }

    [Fact]
    public async Task Compile_errors_return_all_errors_and_write_nothing()
    {
        var yaml = Realtime(Name("Broken")).Replace("subject: Hi", "subjet: Hi, xrmv_nope: 1", StringComparison.Ordinal);

        var result = await ApplyAsync(yaml);

        result.Errors.Should().HaveCount(2);
        result.Errors.Should().OnlyContain(e => e.At != null);
        result.Id.Should().Be(WorkflowIdentity.IdOf(WorkflowKind.Workflow, Order, Name("Broken")));
        (await RowAsync(result.Id)).Should().BeNull();
    }

    // ---- privileges (A17, RF5) ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_as_read_only_user_is_403_and_writes_nothing()
    {
        var (_, reader) = await UserAsync(("prvReadWorkflow", DepthMask.Global));

        var thrown = await ((Func<Task>)(() => ApplyAsync(Realtime(Name("Read only")), reader))).Should().ThrowAsync<XrmdFaultException>();

        thrown.Which.HttpStatus.Should().Be(403);
        (await RowAsync(WorkflowIdentity.IdOf(WorkflowKind.Workflow, Order, Name("Read only")))).Should().BeNull();
    }

    [Fact]
    public async Task Dry_run_without_read_privilege_is_403()
    {
        // Fix round 1: prvReadWorkflow is demanded before the compile, dry run or not.
        var basic = await AuthoringTestHelper.UserWithRoleAsync(fx, StandardRoles.BasicUser);

        var thrown = await ((Func<Task>)(() => ApplyAsync(Realtime(Name("Basic dry")), basic, dryRun: true))).Should().ThrowAsync<XrmdFaultException>();

        (thrown.Which.HttpStatus, thrown.Which.ErrorCode).Should().Be((403, ErrorCodes.PrivilegeDenied));
        thrown.Which.Message.Should().Contain("prvReadWorkflow");
    }

    [Fact]
    public async Task Lookup_of_unreadable_record_reads_as_missing()
    {
        // Fix round 1: a lookup literal's record is retrieved as the caller; one the caller cannot read is reported as a missing id,
        // in the same words, and its name never reaches the XAML.
        var secret = $"Secret account {_run}";
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = secret });
        var missing = Guid.NewGuid();
        var (_, author) = await UserAsync(("prvCreateWorkflow", DepthMask.Global), ("prvReadWorkflow", DepthMask.Global),
            ("prvWriteWorkflow", DepthMask.Global));
        string Regarding(string name, Guid id) => Realtime(name).Replace("regardingobjectid: { record: primary }",
            $"regardingobjectid: {{ lookup: account, id: {id:D} }}", StringComparison.Ordinal);

        var unreadable = await ApplyAsync(Regarding(Name("Unreadable"), account), author);
        var absent = await ApplyAsync(Regarding(Name("Absent"), missing), author);

        var error = unreadable.Errors.Should().ContainSingle().Subject.Message;
        error.Should().Be($"No account record has the id {account:D}.");
        error.Replace(account.ToString("D"), missing.ToString("D"), StringComparison.Ordinal)
            .Should().Be(absent.Errors.Should().ContainSingle().Subject.Message);
        (await RowAsync(unreadable.Id)).Should().BeNull();
        (await ScalarAsync("SELECT COUNT(*) FROM data.workflow WHERE xaml LIKE @name", ("@name", $"%{secret}%"))).Should().Be(0);

        // The administrator reads it: the same file compiles, with the record's name.
        var admin = await ApplyAsync(Regarding(Name("Readable"), account), await AdminAsync());
        admin.Errors.Should().BeEmpty();
        (await RowAsync(admin.Id))!.Xaml.Should().Contain(secret);
    }

    [Fact]
    public async Task Update_trigger_columns_compare_as_a_set()
    {
        // Fix round 1, minor 4: the stored list in another order, case or spacing is not a change.
        var yaml = Waiting(Name("Columns"), "renamed").Replace("columns: [xrmv_status]", "columns: [xrmv_status, xrmv_amount]", StringComparison.Ordinal);
        var created = await ApplyAsync(yaml);
        created.Errors.Should().BeEmpty();
        await AuthoringTestHelper.ExecAsync(fx,
            $"UPDATE data.workflow SET triggeronupdateattributelist = ' XRMV_AMOUNT,xrmv_status ' WHERE workflowid = '{created.Id:D}'");

        (await ApplyAsync(yaml)).Action.Should().Be(ApplyAction.Unchanged);
    }

    [Fact]
    public async Task Apply_activate_without_privilege_writes_nothing()
    {
        // RF5: Create and Write, but not prvActivateSynchronousWorkflow: the Create is rolled back with the refused activation.
        var (_, author) = await UserAsync(("prvCreateWorkflow", DepthMask.Global), ("prvReadWorkflow", DepthMask.Global),
            ("prvWriteWorkflow", DepthMask.Global));

        var thrown = await ((Func<Task>)(() => ApplyAsync(Realtime(Name("No activation")), author, activate: true)))
            .Should().ThrowAsync<XrmdFaultException>();

        thrown.Which.HttpStatus.Should().Be(403);
        thrown.Which.Message.Should().Contain("prvActivateSynchronousWorkflow");
        (await RowAsync(WorkflowIdentity.IdOf(WorkflowKind.Workflow, Order, Name("No activation")))).Should().BeNull();
    }

    [Fact]
    public async Task Plugin_on_workflow_create_sees_authored_process()
    {
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await AuthoringTestHelper.EnsureTypeAsync(fx, Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        var step = await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", S.Table, 20, config: "description=set by a plug-in");
        try
        {
            var result = await ApplyAsync(Realtime(Name("Plugged")));

            (await RowAsync(result.Id))!.Description.Should().Be("set by a plug-in", "the Create ran through the pipeline");
        }
        finally
        {
            await Admin.DeleteAsync("sdkmessageprocessingstep", step);
            // The type goes too: RealExportImportTests counts the plug-in types of this collection's database (plan 2.21, Task 8).
            await Admin.DeleteAsync("plugintype", type);
            fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();
        }
    }

    // ---- export, list, resolve (A12, A18) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_yaml_of_applied_file_is_the_file()
    {
        // The canonical form of the parity OnCreate, under an id and a name of this test.
        var id = Guid.NewGuid();
        var yaml = AuthoringTestHelper.Fixture("oncreate.yaml")
            .Replace($"id: {ImportedOnCreate:D}", $"id: {id:D}", StringComparison.Ordinal)
            .Replace("name: OnCreate", $"name: {Name("OnCreate")}", StringComparison.Ordinal);
        (await ApplyAsync(yaml)).Errors.Should().BeEmpty();

        var exported = await AuthoringAsync(async a => await a.ExportAsync(await CustomizerAsync(), Name("OnCreate"), "yaml", default));

        exported.Should().Be(yaml);
    }

    [Fact]
    public async Task Export_xaml_is_stored_xaml()
    {
        var result = await ApplyAsync(Realtime(Name("Xaml")));

        var xaml = await AuthoringAsync(async a => await a.ExportAsync(await CustomizerAsync(), result.Id.ToString(), "xaml", default));

        xaml.Should().Be((await RowAsync(result.Id))!.Xaml);
    }

    [Fact]
    public async Task Export_of_imported_real_process_is_its_fixture()
    {
        // OnUpdate is left out: its email names a currency of the exporting organization, which this one does not have (RF4).
        foreach (var (id, fixture) in new[]
                 {
                     ("3e1f0307-d8de-f111-84e8-002248872b66", "oncreate.yaml"), ("0fe1b782-e5de-f111-84e8-002248872b66", "child.yaml"),
                     ("216fa344-e5de-f111-84e8-002248872b66", "business-rule.yaml"), ("9df696a1-e6de-f111-84e8-002248872b66", "recalculate.yaml"),
                 })
            (await AuthoringAsync(async a => await a.ExportAsync(await AdminAsync(), id, "yaml", default)))
                .Should().Be(AuthoringTestHelper.Fixture(fixture), "the export of {0} is its fixture", fixture);
    }

    [Fact]
    public async Task Export_not_representable_lists_reasons()
    {
        var row = WorkflowFixtures.Definition("unsupported-expression", Name("Unsupported"));
        await WorkflowTestHelper.SaveAsync(fx, row);

        var message = await TestHelpers.BadRequestAsync(() => AuthoringAsync(async a => await a.ExportAsync(await AdminAsync(), row.Id.ToString(), "yaml", default)));

        message.Should().Contain("not representable").And.Contain("not supported");
        // The XAML is still exported.
        (await AuthoringAsync(async a => await a.ExportAsync(await AdminAsync(), row.Id.ToString(), "xaml", default))).Should().Be(row.Xaml);
    }

    [Fact]
    public async Task List_filters_by_kind_and_entity()
    {
        var workflow = await ApplyAsync(Realtime(Name("Listed")));
        var onAccount = await ApplyAsync(Realtime(Name("Listed account"), entity: "account"));
        var rule = await ApplyAsync(Rule(Name("Listed rule")), activate: true);

        var all = await AuthoringAsync(async a => await a.ListAsync(await CustomizerAsync(), null, null, default));
        var rules = await AuthoringAsync(async a => await a.ListAsync(await CustomizerAsync(), "businessRule", null, default));
        var orders = await AuthoringAsync(async a => await a.ListAsync(await CustomizerAsync(), "workflow", Order, default));

        all.Select(s => s.Id).Should().Contain([workflow.Id, onAccount.Id, rule.Id]);
        all.Single(s => s.Id == workflow.Id).Should().Be(new WorkflowSummary(workflow.Id, Name("Listed"), "workflow", Order, "realtime", "draft", 0));
        all.Single(s => s.Id == rule.Id).Should().Be(new WorkflowSummary(rule.Id, Name("Listed rule"), "businessRule", Order, null, "activated", 0));
        rules.Should().OnlyContain(s => s.Kind == "businessRule").And.Contain(s => s.Id == rule.Id);
        orders.Should().OnlyContain(s => s.Kind == "workflow" && s.Entity == Order).And.Contain(s => s.Id == workflow.Id)
            .And.NotContain(s => s.Id == onAccount.Id);
        all.Select(s => s.Id).Should().NotContain((await RowAsync(rule.Id))!.ActiveWorkflowId!.Value, "an activation is not a definition");
        (await TestHelpers.BadRequestAsync(() => AuthoringAsync(async a => await a.ListAsync(await CustomizerAsync(), "dialog", null, default))))
            .Should().Contain("dialog");
    }

    [Fact]
    public async Task Ambiguous_name_is_400_with_ids()
    {
        var a = await ApplyAsync(Realtime(Name("Same")));
        var b = await ApplyAsync(Realtime(Name("Same"), entity: "account"));

        var message = await TestHelpers.BadRequestAsync(() => AuthoringAsync(async s => await s.ExportAsync(await CustomizerAsync(), Name("Same"), "yaml", default)));

        message.Should().Contain(a.Id.ToString("D")).And.Contain(b.Id.ToString("D"));
    }

    [Fact]
    public async Task Unknown_name_is_404()
    {
        var thrown = await ((Func<Task>)(() => AuthoringAsync(async a => await a.ExportAsync(await CustomizerAsync(), Name("Nobody"), "yaml", default))))
            .Should().ThrowAsync<XrmdFaultException>();
        (thrown.Which.HttpStatus, thrown.Which.ErrorCode).Should().Be((404, ErrorCodes.ObjectDoesNotExist));
        (await ((Func<Task>)(() => AuthoringAsync(async a => await a.SetStateAsync(await CustomizerAsync(), Guid.NewGuid().ToString(), true, default))))
            .Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task Names_resolve_among_processes_the_caller_reads()
    {
        // Fix round 1, minor 1: a name is looked up as the caller (RetrieveMultiple), so a process the caller cannot read is not found
        // by its name; its id is retrieved as the caller (403).
        var created = await ApplyAsync(Realtime(Name("Owned")), await AdminAsync());
        var (_, reader) = await UserAsync(("prvReadWorkflow", DepthMask.Basic));

        var byName = await ((Func<Task>)(() => AuthoringAsync(a => a.ExportAsync(reader, Name("Owned"), "yaml", default))))
            .Should().ThrowAsync<XrmdFaultException>();
        (byName.Which.HttpStatus, byName.Which.ErrorCode).Should().Be((404, ErrorCodes.ObjectDoesNotExist));
        (await ((Func<Task>)(() => AuthoringAsync(a => a.SetStateAsync(reader, created.Id.ToString(), false, default))))
            .Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(403);
    }

    [Fact]
    public async Task Apply_of_unreadable_process_id_is_one_generic_refusal()
    {
        // Final review I1: the process an id names is retrieved as the caller first. One the caller cannot read gives one generic error,
        // dry run or not, whatever the document (same kind, another kind, another table, a compile error): no stored name, state or
        // category reaches the result.
        var id = Guid.NewGuid();
        var secret = Name("Hidden");
        (await ApplyAsync(Realtime(secret, id), await AdminAsync(), activate: true)).Errors.Should().BeEmpty();
        var before = await VersionAsync(id);
        var (_, reader) = await UserAsync(("prvReadWorkflow", DepthMask.Basic), ("prvCreateWorkflow", DepthMask.Basic),
            ("prvWriteWorkflow", DepthMask.Basic));
        var documents = new[]
        {
            Realtime(Name("Mine"), id), Rule(Name("Mine"), id), Realtime(Name("Mine"), id, entity: "account"),
            Realtime(Name("Mine"), id).Replace("subject: Hi", "xrmv_nosuchcolumn: Hi", StringComparison.Ordinal),
        };

        foreach (var yaml in documents)
        foreach (var dryRun in new[] { true, false })
        {
            var result = await ApplyAsync(yaml, reader, dryRun: dryRun);

            result.Errors.Should().ContainSingle().Which.Should().Be(new CompileError(null, $"The process with id {id:D} cannot be read or changed by you."));
            (result.Id, result.Name, result.Action, result.State, result.StateChanged).Should()
                .Be((id, Name("Mine"), ApplyAction.Unchanged, "draft", false), "dry run {0}:\n{1}", dryRun, yaml);
            result.AssumedShapes.Should().BeEmpty();
            System.Text.Json.JsonSerializer.Serialize(result).Should().NotContain(secret).And.NotContain("activated").And.NotContain("categor");
        }
        (await VersionAsync(id)).Should().Be(before);
        (await RowAsync(id))!.Name.Should().Be(secret);
    }

    [Fact]
    public async Task Name_clash_with_unreadable_process_does_not_name_its_id()
    {
        // Final review I1 (A13): a clash with a process the caller cannot read refuses the apply without its id; a readable one names it.
        var other = await ApplyAsync(Realtime(Name("Clash")), await AdminAsync());
        var (_, reader) = await UserAsync(("prvReadWorkflow", DepthMask.Basic), ("prvCreateWorkflow", DepthMask.Basic),
            ("prvWriteWorkflow", DepthMask.Basic));
        var mine = Guid.NewGuid();

        foreach (var dryRun in new[] { true, false })
        {
            var result = await ApplyAsync(Realtime(Name("Clash"), mine), reader, dryRun: dryRun);

            var message = result.Errors.Should().ContainSingle().Subject.Message;
            message.Should().Contain("already exists").And.NotContain(other.Id.ToString("D")).And.NotContain(other.Id.ToString("N"));
        }
        (await RowAsync(mine)).Should().BeNull();

        var own = await ApplyAsync(Realtime(Name("Own clash")), reader);
        own.Errors.Should().BeEmpty();
        (await ApplyAsync(Realtime(Name("Own clash"), Guid.NewGuid()), reader, dryRun: true)).Errors.Should().ContainSingle()
            .Which.Message.Should().Contain(own.Id.ToString("D"));
    }

    [Fact]
    public async Task Set_state_activates_and_deactivates()
    {
        var created = await ApplyAsync(Realtime(Name("Toggled")));

        var on = await AuthoringAsync(async a => await a.SetStateAsync(await CustomizerAsync(), Name("Toggled"), true, default));
        on.State.Should().Be("activated");
        (await RowAsync(created.Id))!.StateCode.Should().Be(S.ActivatedState);
        var off = await AuthoringAsync(async a => await a.SetStateAsync(await CustomizerAsync(), created.Id.ToString(), false, default));
        off.Should().Be(new WorkflowSummary(created.Id, Name("Toggled"), "workflow", Order, "realtime", "draft", 0));
    }

    // ---- the applied process runs (A20 preview) ----------------------------------------------------------------------------------

    [Fact]
    public async Task Applied_process_runs_like_imported()
    {
        // The parity OnCreate (and its child), applied under names of this test and activated: creating an order creates the task,
        // as ServiceClient_parity_realtime_creates_task asserts for the imported process.
        var child = AuthoringTestHelper.Fixture("child.yaml")
            .Replace("id: 0fe1b782-e5de-f111-84e8-002248872b66\n", "", StringComparison.Ordinal)
            .Replace("name: Child", $"name: {Name("Child")}", StringComparison.Ordinal);
        var onCreate = AuthoringTestHelper.Fixture("oncreate.yaml")
            .Replace($"id: {ImportedOnCreate:D}\n", "", StringComparison.Ordinal)
            .Replace("name: OnCreate", $"name: {Name("OnCreate")}", StringComparison.Ordinal)
            .Replace("workflow: Child", $"workflow: {Name("Child")}", StringComparison.Ordinal);
        (await ApplyAsync(child, activate: true)).Errors.Should().BeEmpty();
        (await ApplyAsync(onCreate, activate: true)).Errors.Should().BeEmpty();

        var order = await Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Order") });

        var task = (await Admin.RetrieveMultipleAsync(new QueryExpression("task")
        {
            ColumnSet = new ColumnSet("subject", "regardingobjectid", "prioritycode", "isworkflowcreated"),
            Criteria = { Conditions = { new ConditionExpression("regardingobjectid", ConditionOperator.Equal, order) } },
        })).Entities.Should().ContainSingle("the real-time workflow created the task in the request").Subject;
        task.GetAttributeValue<string>("subject").Should().Be("TEST");
        task.GetAttributeValue<EntityReference>("regardingobjectid").Should().Be(new EntityReference(Order, order));
        task.GetAttributeValue<OptionSetValue>("prioritycode").Value.Should().Be(1);
        task.GetAttributeValue<bool>("isworkflowcreated").Should().BeTrue();
        // The child workflow step queued the child (children run in the background, W12).
        (await ScalarAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE regardingobjectid = @id AND operationtype = 10", ("@id", order)))
            .Should().Be(1);
    }
}
