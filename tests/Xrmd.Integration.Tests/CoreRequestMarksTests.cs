using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Audit;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Workflows;
using Xrmd.Security;
using Xrmd.Workflows;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using ModelRequest = Xrmd.Model.Messages.Request;
using ModelResponse = Xrmd.Model.Messages.Response;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3b, Task 2: the request marks of the model (<see cref="CascadeHint"/>, <see cref="AuditActionHint"/>) reach stage 30 through
/// the pipeline's crossings, and the typed CRUD responses of <see cref="CoreResponses"/>. The host's core is a spy that issues a
/// model cascade of its own and records the marks of every model request it gets.
/// </summary>
[Collection("xrmd")]
public sealed class CoreRequestMarksTests(XrmdFixture fx)
{
    private const string Attachment = "activitymimeattachment";

    private static readonly string[] CrudNames =
        ["Create", "Retrieve", "Update", "Delete", "RetrieveMultiple", "Assign", "Associate", "Disassociate"];

    // ---- typed responses ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Create", typeof(CreateResponse))]
    [InlineData("Retrieve", typeof(RetrieveResponse))]
    [InlineData("Update", typeof(UpdateResponse))]
    [InlineData("Delete", typeof(DeleteResponse))]
    [InlineData("RetrieveMultiple", typeof(RetrieveMultipleResponse))]
    [InlineData("Assign", typeof(AssignResponse))]
    [InlineData("Associate", typeof(AssociateResponse))]
    [InlineData("Disassociate", typeof(DisassociateResponse))]
    public void Typed_response_per_crud_name(string name, Type type)
    {
        var response = CoreResponses.Typed(name);

        response.GetType().Should().Be(type);
        response.ResponseName.Should().Be(name);
        CoreResponses.IsCrud(name).Should().BeTrue();
    }

    [Fact]
    public void Crud_names_are_exactly_the_eight()
    {
        CrudNames.Should().OnlyContain(n => CoreResponses.IsCrud(n));
        CoreResponses.IsCrud("create").Should().BeFalse("names are canonical, ordinal");
        CoreResponses.IsCrud("Upsert").Should().BeFalse();
    }

    [Theory]
    [InlineData("Upsert")]
    [InlineData("create")]
    [InlineData("")]
    [InlineData("SetState")]
    public void Typed_response_refuses_other_names(string name)
    {
        var act = () => CoreResponses.Typed(name);

        act.Should().Throw<ArgumentOutOfRangeException>();
        CoreResponses.IsCrud(name).Should().BeFalse();
    }

    [Fact]
    public void Query_model_hint_is_kept_beside_the_request()
    {
        var request = new ModelRequest { Name = "RetrieveMultiple" };
        QueryModelHint.Of(request).Should().BeNull();
        var model = Xrmd.Query.FetchXml.FetchXmlParser.Parse("<fetch><entity name=\"account\"><all-attributes /></entity></fetch>");

        QueryModelHint.Attach(request, model);

        QueryModelHint.Of(request).Should().BeSameAs(model);
        QueryModelHint.Of(new ModelRequest { Name = "RetrieveMultiple" }).Should().BeNull("the mark is per request instance");
    }

    // ---- marks through the pipeline ---------------------------------------------------------------------------------

    /// <summary>
    /// A SetState runs as an Update marked with the Set State audit action on the SDK request. The model request the core gets carries
    /// the same action (copied when the request crosses into the model), so a core on the model reads it there.
    /// </summary>
    [Fact]
    public async Task Set_state_update_carries_its_audit_action_into_the_model()
    {
        var spy = new MarkSpy();
        await using var host = Host(spy);
        var admin = TestServices.NewOrganizationService(host.Services, fx.AdminObjectId);
        var account = await admin.CreateAsync(new Entity("account") { ["name"] = "marks-" + Guid.NewGuid().ToString("N")[..8] });
        spy.UpdateActions.Clear();

        await ExecuteAsync(host, fx.AdminObjectId, new SetStateRequest
        {
            EntityMoniker = new EntityReference("account", account), State = new OptionSetValue(1), Status = new OptionSetValue(2),
        });

        spy.UpdateActions.Should().Equal([AuditActions.SetState]);
    }

    /// <summary>
    /// A core that issues a cascade as a model Delete marked with <see cref="CascadeHint.Mark(Xrmd.Model.Messages.Request)"/>: the mark
    /// reaches the SDK twin before the guard (so a caller without write on the email may delete the attachment) and the invocation is a
    /// core cascade (so no workflow is looked up for the attachment). Unmarked, the same request is refused.
    /// </summary>
    [Fact]
    public async Task Model_cascade_mark_reaches_the_guard_through_the_nested_delegate()
    {
        var spy = new MarkSpy();
        await using var host = Host(spy);
        var cs = host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["ConnectionStrings:Org"]!;
        var cache = host.Services.GetRequiredService<SecurityCache>();
        var admin = TestServices.NewOrganizationService(host.Services, fx.AdminObjectId);
        var root = await RootBusinessUnitAsync(cs);
        var userObjectId = Guid.NewGuid();
        var userId = await CreateUserAsync(cs, cache, root, userObjectId);
        await AssignRoleAsync(cs, cache, userId,
            await CreateRoleAsync(cs, cache, "marks-" + Guid.NewGuid().ToString("N")[..8], ("prvReadAccount", DepthMask.Global)));
        var account = await admin.CreateAsync(new Entity("account") { ["name"] = "marks-cascade" });
        var email = await admin.CreateAsync(new Entity("email") { ["subject"] = "marks" });
        Task<Guid> NewAttachment() => admin.CreateAsync(new Entity(Attachment)
        {
            ["objectid"] = new EntityReference("email", email), ["subject"] = "a", ["filename"] = "a.txt",
            ["body"] = Convert.ToBase64String("marks"u8.ToArray()),
        });
        Task Retrieve() => ExecuteAsync(host, userObjectId, new RetrieveRequest
        {
            Target = new EntityReference("account", account), ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("name"),
        });

        // unmarked: the guard demands write on the parent email, which the user does not hold
        spy.CascadeOf = await NewAttachment();
        spy.Mark = false;
        (await FluentActions.Awaiting(Retrieve).Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>())
            .Which.Message.Should().Contain("prvWriteActivity").And.Contain("email");

        // marked: no demand, and the invocation is a core cascade
        var attachment = spy.CascadeOf = await NewAttachment();
        spy.Mark = true;
        spy.WorkflowLookups.Clear();
        await Retrieve();

        (await ExistsAsync(cs, attachment)).Should().BeFalse("the marked cascade was deleted");
        spy.WorkflowLookups.Should().NotContain(Attachment, "a core cascade triggers no workflow");
    }

    /// <summary>Runs <paramref name="request"/> through the host's executor (and so its spy core) as the user with Entra object id <paramref name="objectId"/>.</summary>
    private static async Task ExecuteAsync(WebApplicationFactory<Program> host, Guid objectId, OrganizationRequest request)
    {
        var caller = await TestServices.CallerAsync(host.Services, objectId);
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PipelineExecutor>()
            .ExecuteAsync(request, new InvocationContext(caller, caller.UserId, 1, null, null, Guid.NewGuid()), default);
    }

    private static async Task<bool> ExistsAsync(string cs, Guid attachment) =>
        await TestSql.ScalarAsync(cs, $"SELECT COUNT(*) FROM data.{Attachment} WHERE activitymimeattachmentid = @id", ("@id", attachment)) is int and > 0;

    private WebApplicationFactory<Program> Host(MarkSpy spy)
    {
        var app = fx.WithOwnDatabase(b => b.ConfigureTestServices(s =>
        {
            s.AddScoped<IPipelineCore>(sp => new SpyCore(sp.GetRequiredService<CoreOperations>(), spy));
            s.AddScoped<IWorkflowPipelineHook>(_ => new RecordingHook(spy));
        }));
        _ = app.Server;
        return app;
    }

    /// <summary>What the spy core saw and what it should issue.</summary>
    private sealed class MarkSpy
    {
        public List<int?> UpdateActions { get; } = [];
        public Guid CascadeOf { get; set; }
        public bool Mark { get; set; }
        public List<string> WorkflowLookups { get; } = [];
    }

    private sealed class SpyCore(CoreOperations inner, MarkSpy spy) : IPipelineCore
    {
        public async Task<ModelResponse> ExecuteCoreAsync(ModelRequest request, CoreCall call, CancellationToken ct)
        {
            if (request.Name == "Update") spy.UpdateActions.Add(AuditActionHint.Of(request));
            if (request.Name == "Retrieve" && spy.CascadeOf != Guid.Empty && call.Nested is { } nested)
            {
                var child = new ModelRequest { Name = "Delete" };
                child.Parameters["Target"] = new RecordReference { LogicalName = Attachment, Id = spy.CascadeOf };
                if (spy.Mark) CascadeHint.Mark(child);
                await nested(child, ct);
            }
            return await inner.ExecuteCoreAsync(request, call, ct);
        }

        public bool IsCoreMessage(string messageName) => inner.IsCoreMessage(messageName);
        public IReadOnlyCollection<string> WrittenEntities(ModelRequest request) => inner.WrittenEntities(request);
        public bool IsNonPipelineMessage(string messageName) => inner.IsNonPipelineMessage(messageName);
        public Task<ModelResponse> ExecuteNonPipelineAsync(ModelRequest request, Caller caller, DataSession session, CancellationToken ct) =>
            inner.ExecuteNonPipelineAsync(request, caller, session, ct);
        public Task<bool> LockExistsAsync(RecordReference target, DataSession session, CancellationToken ct) => inner.LockExistsAsync(target, session, ct);
        public Task<Xrmd.Model.Records.Record?> RetrieveImageAsync(RecordReference target, string[]? attributes, DataSession session, CancellationToken ct) =>
            inner.RetrieveImageAsync(target, attributes, session, ct);
        public Task<Caller> ResolveUserAsync(Guid systemUserId, DataSession session, CancellationToken ct) => inner.ResolveUserAsync(systemUserId, session, ct);
        public Task<Guid> SystemUserIdAsync(DataSession session, CancellationToken ct) => inner.SystemUserIdAsync(session, ct);
        public Task<(Guid Id, string Name)> OrganizationAsync(DataSession session, CancellationToken ct) => inner.OrganizationAsync(session, ct);
        public Task<int> PluginTraceLogSettingAsync(DataSession session, CancellationToken ct) => inner.PluginTraceLogSettingAsync(session, ct);
    }

    /// <summary>The no-workflow hook that records the tables the pipeline asked about (it asks only for a message that is not a core cascade).</summary>
    private sealed class RecordingHook(MarkSpy spy) : IWorkflowPipelineHook
    {
        private readonly IWorkflowPipelineHook _none = NoWorkflowPipelineHook.Instance;
        public void Invalidate() => _none.Invalidate();
        public Task ValidateWriteAsync(DataSession s, Caller caller, string message, Entity target, CancellationToken ct) => _none.ValidateWriteAsync(s, caller, message, target, ct);
        public Task OnSetStateAsync(DataSession s, Caller caller, Guid workflowId, int state, CancellationToken ct) => _none.OnSetStateAsync(s, caller, workflowId, state, ct);
        public Task OnAssignedAsync(DataSession s, Guid workflowId, CancellationToken ct) => _none.OnAssignedAsync(s, workflowId, ct);
        public Task<bool> HasRealtimeAsync(string entity, CancellationToken ct)
        {
            spy.WorkflowLookups.Add(entity);
            return _none.HasRealtimeAsync(entity, ct);
        }
        public Task<IReadOnlyList<RealtimeWorkflow>> GetRealtimeAsync(string message, string? entity, int stage, IReadOnlyCollection<string> sentColumns,
            RecordOwner? recordOwner, CancellationToken ct) => _none.GetRealtimeAsync(message, entity, stage, sentColumns, recordOwner, ct);
        public Task RunRealtimeAsync(PipelineExecutor executor, RealtimeWorkflow w, XrmdPluginExecutionContext context, InvocationContext invocation,
            DataSession s, CancellationToken ct) => _none.RunRealtimeAsync(executor, w, context, invocation, s, ct);
        public Task<bool> HasBackgroundAsync(string entity, CancellationToken ct) => _none.HasBackgroundAsync(entity, ct);
        public Task QueueBackgroundAsync(string message, string? entity, IReadOnlyCollection<string> sentColumns, RecordOwner? recordOwner,
            XrmdPluginExecutionContext context, InvocationContext invocation, DataSession s, CancellationToken ct) =>
            _none.QueueBackgroundAsync(message, entity, sentColumns, recordOwner, context, invocation, s, ct);
        public Task<ActionDefinition?> FindActionAsync(string message, CancellationToken ct) => _none.FindActionAsync(message, ct);
        public Task RunActionAsync(PipelineExecutor executor, ActionDefinition action, XrmdPluginExecutionContext context, InvocationContext invocation,
            DataSession s, CancellationToken ct) => _none.RunActionAsync(executor, action, context, invocation, s, ct);
        public Task<Guid> ExecuteWorkflowAsync(PipelineExecutor executor, OrganizationRequest request, XrmdPluginExecutionContext context,
            InvocationContext invocation, DataSession s, CancellationToken ct) => _none.ExecuteWorkflowAsync(executor, request, context, invocation, s, ct);
    }
}
