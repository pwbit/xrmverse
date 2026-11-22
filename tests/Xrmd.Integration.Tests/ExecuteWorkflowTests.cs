using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.Workflows;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.WorkflowTestHelper;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.14, Task 7 (W21): <c>ExecuteWorkflow</c> runs an activated on-demand workflow on a record. A background one is queued (the
/// response <c>Id</c> is the job); a real-time one runs in the request and answers <see cref="Guid.Empty"/>. Through the SDK and through
/// the Web API bound action <c>workflows({id})/Microsoft.Dynamics.CRM.ExecuteWorkflow</c>. The provisional create step of
/// <c>realtime-create-update</c> (a task with a subject, on <c>account</c>) is the workflow's body.
/// </summary>
[Collection("xrmd")]
public sealed class ExecuteWorkflowTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private Guid _adminUserId;
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        _adminUserId = (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId;
        await ClearAsync(fx);
    }

    public Task DisposeAsync() => ClearAsync(fx);

    private string Name(string what) => $"{what} {_run}";

    /// <summary>The provisional create step alone: a task with the subject <c>Name(subject)</c> (its regarding left out).</summary>
    private string AccountTask(string subject) =>
        Remove(Replace(Without(WorkflowFixtures.Xaml("realtime-create-update"), "UpdateStep2"), "\"Call the new customer\"", $"\"{Name(subject)}\""),
            "<mxswa:SetEntityProperty Attribute=\"regardingobjectid\"[^>]*/>");

    /// <summary>An on-demand workflow on account (no trigger), real-time or background, owned by the administrator.</summary>
    private WorkflowRow OnDemand(string subject, bool background = false, bool onDemand = true) =>
        Realtime(Name(subject), AccountTask(subject), "account") with
        {
            Mode = background ? 0 : 1, OnDemand = onDemand, OwnerId = new EntityReference("systemuser", _adminUserId),
        };

    private Task<WorkflowRow> ActivateAsync(WorkflowRow row) => WorkflowTestHelper.ActivateAsync(fx, Admin, row);

    private async Task<Guid> AccountAsync() => await Admin.CreateAsync(new Entity("account") { ["name"] = Name("Account") });

    private async Task<int> TaskCountAsync(string subject) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression("task")
        {
            Criteria = { Conditions = { new ConditionExpression("subject", ConditionOperator.Equal, Name(subject)) } },
        })).Entities.Count;

    private static async Task<Guid> ExecuteAsync(OrganizationService service, Guid workflow, Guid record) =>
        ((ExecuteWorkflowResponse)await service.ExecuteAsync(new ExecuteWorkflowRequest { WorkflowId = workflow, EntityId = record })).Id;

    private async Task<Guid> ActivationOfAsync(Guid definition) =>
        (await Admin.RetrieveAsync(S.Table, definition, new ColumnSet(S.ActiveWorkflowId))).GetAttributeValue<EntityReference>(S.ActiveWorkflowId).Id;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    [Fact]
    public async Task Background_on_demand_returns_job_id()
    {
        var definition = await ActivateAsync(OnDemand("Queued", background: true));
        var account = await AccountAsync();

        var jobId = await ExecuteAsync(Admin, definition.Id, account);

        jobId.Should().NotBe(Guid.Empty);
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(
            $"SELECT operationtype, [{S.WorkflowActivationId}], regardingobjectid, ownerid FROM data.asyncoperation WHERE asyncoperationid = @id", conn);
        cmd.Parameters.AddWithValue("@id", jobId);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue("the response's Id is the queued job");
        (rd.GetInt32(0), rd.GetGuid(1), rd.GetGuid(2)).Should().Be((S.WorkflowOperationType, await ActivationOfAsync(definition.Id), account));
        rd.GetGuid(3).Should().Be(_adminUserId, "the job is the caller's");
    }

    [Fact]
    public async Task Realtime_on_demand_runs_and_returns_empty_id()
    {
        var definition = await ActivateAsync(OnDemand("Inline"));
        var account = await AccountAsync();

        (await ExecuteAsync(Admin, definition.Id, account)).Should().Be(Guid.Empty);
        (await TaskCountAsync("Inline")).Should().Be(1, "the workflow ran in the request");

        // The activation's id names the same workflow.
        (await ExecuteAsync(Admin, await ActivationOfAsync(definition.Id), account)).Should().Be(Guid.Empty);
        (await TaskCountAsync("Inline")).Should().Be(2);
    }

    [Fact]
    public async Task Not_on_demand_is_400()
    {
        var definition = await ActivateAsync(OnDemand("Triggered only", onDemand: false) with { TriggerOnCreate = false });
        var account = await AccountAsync();

        var fault = await TestHelpers.FaultAsync(() => ExecuteAsync(Admin, definition.Id, account));
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().Contain("on demand");
        (await TaskCountAsync("Triggered only")).Should().Be(0);
    }

    [Fact]
    public async Task Draft_is_400()
    {
        var draft = OnDemand("Draft");
        await SaveAsync(fx, draft);
        var account = await AccountAsync();

        var fault = await TestHelpers.FaultAsync(() => ExecuteAsync(Admin, draft.Id, account));
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().Contain("not activated");

        // An activation that is no longer the current one is gone with its deactivation: 404.
        await ActivateAsync(draft);
        var first = await ActivationOfAsync(draft.Id);
        await DeactivateAsync(Admin, draft.Id);
        var stale = await TestHelpers.FaultAsync(() => ExecuteAsync(Admin, first, account));
        stale.HttpStatus.Should().Be(404);
        (await TaskCountAsync("Draft")).Should().Be(0);
    }

    [Fact]
    public async Task Record_not_readable_is_403()
    {
        var definition = await ActivateAsync(OnDemand("Unreadable"));
        var account = await AccountAsync(); // the administrator's
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, team: false, ("prvReadWorkflow", DepthMask.Global), ("prvWorkflowExecution", DepthMask.Global),
            ("prvReadAccount", DepthMask.Basic));

        var fault = await TestHelpers.FaultAsync(() => ExecuteAsync(ServiceFor(fx, objectId), definition.Id, account));
        fault.HttpStatus.Should().Be(403);
        (await TaskCountAsync("Unreadable")).Should().Be(0);

        // Without Read on the workflow, the request is refused before anything else.
        var stranger = Guid.NewGuid();
        var other = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), stranger);
        await GrantAsync(fx, other, team: false, ("prvReadAccount", DepthMask.Global), ("prvWorkflowExecution", DepthMask.Global));
        (await TestHelpers.FaultAsync(() => ExecuteAsync(ServiceFor(fx, stranger), definition.Id, account))).HttpStatus.Should().Be(403);

        // A record that does not exist is 404.
        (await TestHelpers.FaultAsync(() => ExecuteAsync(Admin, definition.Id, Guid.NewGuid()))).HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task Input_arguments_are_400()
    {
        var definition = await ActivateAsync(OnDemand("Arguments"));
        var account = await AccountAsync();

        var fault = await TestHelpers.FaultAsync(() => Admin.ExecuteAsync(new ExecuteWorkflowRequest
        {
            WorkflowId = definition.Id, EntityId = account,
            InputArguments = new InputArgumentCollection { ["Text"] = "x" },
        }));
        (fault.HttpStatus, fault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument));
        fault.Message.Should().Contain("InputArguments");
        (await TaskCountAsync("Arguments")).Should().Be(0);

        // An empty collection is no argument.
        (await Admin.ExecuteAsync(new ExecuteWorkflowRequest
        {
            WorkflowId = definition.Id, EntityId = account, InputArguments = new InputArgumentCollection(),
        })).Should().BeOfType<ExecuteWorkflowResponse>();
        (await TaskCountAsync("Arguments")).Should().Be(1);
    }

    [Fact]
    public async Task Web_api_bound_execute_workflow_returns_id()
    {
        var realtime = await ActivateAsync(OnDemand("Web"));
        var background = await ActivateAsync(OnDemand("Web queued", background: true));
        var account = await AccountAsync();

        var res = await SendAsync(HttpMethod.Post, $"workflows({realtime.Id})/Microsoft.Dynamics.CRM.ExecuteWorkflow",
            new JsonObject { ["EntityId"] = account.ToString() });
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!;
        body["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.ExecuteWorkflowResponse");
        body["Id"]!.GetValue<Guid>().Should().Be(Guid.Empty);
        (await TaskCountAsync("Web")).Should().Be(1);

        var queued = await SendAsync(HttpMethod.Post, $"workflows({background.Id})/Microsoft.Dynamics.CRM.ExecuteWorkflow",
            new JsonObject { ["EntityId"] = account.ToString() });
        var queuedText = await queued.Content.ReadAsStringAsync();
        queued.StatusCode.Should().Be(HttpStatusCode.OK, queuedText);
        JsonNode.Parse(queuedText)!["Id"]!.GetValue<Guid>().Should().NotBe(Guid.Empty);

        // Another parameter is refused; the action is bound to workflow only.
        (await SendAsync(HttpMethod.Post, $"workflows({realtime.Id})/Microsoft.Dynamics.CRM.ExecuteWorkflow",
            new JsonObject { ["EntityId"] = account.ToString(), ["Other"] = 1 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, $"accounts({account})/Microsoft.Dynamics.CRM.ExecuteWorkflow",
            new JsonObject { ["EntityId"] = account.ToString() })).StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Metadata_declares_ExecuteWorkflow()
    {
        var res = await SendAsync(HttpMethod.Get, "$metadata");
        var csdl = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var start = csdl.IndexOf("<Action Name=\"ExecuteWorkflow\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        var action = csdl[start..csdl.IndexOf("</Action>", start, StringComparison.Ordinal)];
        action.Should().Contain("IsBound=\"true\"")
            .And.Contain("<Parameter Name=\"entity\" Type=\"Microsoft.Dynamics.CRM.workflow\"")
            .And.Contain("<Parameter Name=\"EntityId\" Type=\"Edm.Guid\"")
            .And.Contain("<ReturnType Type=\"Microsoft.Dynamics.CRM.ExecuteWorkflowResponse\"");
        var type = csdl[csdl.IndexOf("<ComplexType Name=\"ExecuteWorkflowResponse\"", StringComparison.Ordinal)..];
        type[..type.IndexOf("</ComplexType>", StringComparison.Ordinal)].Should().Contain("Name=\"Id\"").And.Contain("Edm.Guid");
    }

    // ---- who runs it (Learn: an on-demand workflow runs as the user who starts it) -------------------------------------------

    /// <summary>A user of the root business unit with <paramref name="privileges"/>: its id and its service.</summary>
    private async Task<(Guid User, OrganizationService Service)> UserAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, team: false, privileges);
        return (user, ServiceFor(fx, objectId));
    }

    /// <summary>What a user needs to start the workflow on an account and create its task.</summary>
    private static readonly (string, DepthMask)[] Starter =
    [
        ("prvReadWorkflow", DepthMask.Global), ("prvWorkflowExecution", DepthMask.Global), ("prvReadAccount", DepthMask.Global),
        ("prvCreateActivity", DepthMask.Basic), ("prvReadActivity", DepthMask.Basic),
    ];

    private async Task<List<Entity>> TasksAsync(string subject) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression("task")
        {
            ColumnSet = new ColumnSet("ownerid", "createdby"),
            Criteria = { Conditions = { new ConditionExpression("subject", ConditionOperator.Equal, Name(subject)) } },
        })).Entities.ToList();

    [Fact]
    public async Task Realtime_on_demand_runs_as_the_caller()
    {
        // Owned by the administrator and set to run as its owner: started on demand, it still runs as the user who started it.
        var definition = await ActivateAsync(OnDemand("Caller's task") with { RunAs = 0 });
        var account = await AccountAsync();
        var (user, service) = await UserAsync(Starter);

        (await ExecuteAsync(service, definition.Id, account)).Should().Be(Guid.Empty);
        var task = (await TasksAsync("Caller's task")).Should().ContainSingle().Subject;
        (task.GetAttributeValue<EntityReference>("ownerid").Id, task.GetAttributeValue<EntityReference>("createdby").Id).Should().Be((user, user));

        // The caller's privileges bound the run: without Create on activities, the step fails and nothing stays.
        var (_, limited) = await UserAsync(Starter.Where(p => p.Item1 != "prvCreateActivity").ToArray());
        (await TestHelpers.FaultAsync(() => ExecuteAsync(limited, definition.Id, account))).HttpStatus.Should().Be(403);
        (await TasksAsync("Caller's task")).Should().ContainSingle();
    }

    [Fact]
    public async Task Background_on_demand_is_owned_by_and_runs_as_the_caller()
    {
        var definition = await ActivateAsync(OnDemand("Caller's job", background: true));
        var account = await AccountAsync();
        var (user, service) = await UserAsync(Starter);

        var jobId = await ExecuteAsync(service, definition.Id, account);

        var job = await Admin.RetrieveAsync("asyncoperation", jobId, new ColumnSet("ownerid"));
        job.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(user, "the job is the caller's, not the workflow owner's");
        var worker = new AsyncOperationWorker(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100) }));
        for (var i = 0; i < 50 && await worker.RunOnceAsync(default); i++) { }
        var done = await Admin.RetrieveAsync("asyncoperation", jobId, new ColumnSet("statecode", "statuscode", "message"));
        done.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(30, done.GetAttributeValue<string>("message"));
        (await TasksAsync("Caller's job")).Should().ContainSingle().Which.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(user);

        // Limited to what the caller may do: without Create on activities, the job fails.
        var (_, limited) = await UserAsync(Starter.Where(p => p.Item1 != "prvCreateActivity").ToArray());
        var failing = await ExecuteAsync(limited, definition.Id, account);
        for (var i = 0; i < 50 && await worker.RunOnceAsync(default); i++) { }
        (await Admin.RetrieveAsync("asyncoperation", failing, new ColumnSet("statuscode"))).GetAttributeValue<OptionSetValue>("statuscode").Value
            .Should().Be(31, "the run failed on the caller's missing privilege");
        (await TasksAsync("Caller's job")).Should().ContainSingle();
    }

    [Fact]
    public async Task Without_workflow_execution_privilege_is_403()
    {
        var definition = await ActivateAsync(OnDemand("No execution"));
        var account = await AccountAsync();
        var (_, service) = await UserAsync(Starter.Where(p => p.Item1 != "prvWorkflowExecution").ToArray());

        var fault = await TestHelpers.FaultAsync(() => ExecuteAsync(service, definition.Id, account));
        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain("prvWorkflowExecution");
        (await TasksAsync("No execution")).Should().BeEmpty();

        // System Customizer and Basic User hold it (System Administrator holds every privilege by its template).
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("""
            SELECT rp.roleid FROM data.roleprivileges rp JOIN data.privilege p ON p.privilegeid = rp.privilegeid
            WHERE p.name = 'prvWorkflowExecution'
            """, conn);
        var roles = new List<Guid>();
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) roles.Add(rd.GetGuid(0));
        roles.Should().Contain([StandardRoles.SystemCustomizer, StandardRoles.BasicUser]);
    }
}
