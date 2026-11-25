using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Pipeline;
using Xrmd.Workflows.Custom;
using Xrmd.Workflows.Engine;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// An interface of the shape of the workflow SDK's <c>IWorkflowContext</c> (the SDK itself is not referenced here), with one member
/// the proxy does not know.
/// </summary>
public interface IFakeWorkflowContext : IExecutionContext
{
    string StageName { get; }

    int WorkflowCategory { get; }

    int WorkflowMode { get; }

    IFakeWorkflowContext ParentContext { get; }

    int SomethingNew { get; }
}

/// <summary>Plan 2.14, Task 8b (W17): the <c>IWorkflowContext</c> a custom workflow activity sees of its run.</summary>
public sealed class WorkflowContextProxyTests
{
    private static readonly Guid UserId = Guid.NewGuid(), UnitId = Guid.NewGuid(), Initiating = Guid.NewGuid(), RecordId = Guid.NewGuid(),
        Correlation = Guid.NewGuid(), WorkflowId = Guid.NewGuid(), OrganizationId = Guid.NewGuid();

    private static WorkflowRun Run(int stage, XrmdPluginExecutionContext? parent = null, SdkMirror? mirror = null) => new()
    {
        Workflow = new WorkflowRow { Id = WorkflowId, Name = "Test", Category = 0 },
        Primary = new EntityReference("account", RecordId),
        Caller = new Caller(UserId, UnitId),
        Invocation = new InvocationContext(new Caller(Initiating, UnitId), Initiating, 2, parent?.Model, null, Correlation) { Mirror = mirror },
        Stage = stage,
        MessageName = "Update",
    };

    private static IFakeWorkflowContext Create(WorkflowRun run) =>
        (IFakeWorkflowContext)WorkflowContextProxy.Create(typeof(IFakeWorkflowContext), run, (OrganizationId, "Org"));

    [Fact]
    public void Real_time_run_context_members_come_from_the_run()
    {
        // The hook's view of the message's context, lent while the run goes on (S.2e2c).
        var mirror = new SdkMirror();
        var parent = PluginContextAdapter.Lend(new StepContext(), mirror);
        parent.InputParameters["Target"] = new Entity("account", RecordId);

        var context = Create(Run(40, parent, mirror));

        context.UserId.Should().Be(UserId);
        context.BusinessUnitId.Should().Be(UnitId);
        context.InitiatingUserId.Should().Be(Initiating);
        context.Depth.Should().Be(2);
        context.PrimaryEntityName.Should().Be("account");
        context.PrimaryEntityId.Should().Be(RecordId);
        context.MessageName.Should().Be("Update");
        context.CorrelationId.Should().Be(Correlation);
        context.OrganizationId.Should().Be(OrganizationId);
        context.OrganizationName.Should().Be("Org");
        context.OwningExtension.Should().BeEquivalentTo(new EntityReference("workflow", WorkflowId));
        context.InputParameters.Should().BeSameAs(parent.InputParameters, "a real-time run sees its message's parameters");
        context.Mode.Should().Be(0);
        context.WorkflowMode.Should().Be(1);
        context.StageName.Should().Be("PostOperation");
        context.WorkflowCategory.Should().Be(0);
        context.ParentContext.Should().BeNull();
    }

    [Theory]
    [InlineData(0, "PostOperation", 1, 0)]
    [InlineData(20, "PreOperation", 0, 1)]
    [InlineData(30, "MainOperation", 0, 1)]
    [InlineData(40, "PostOperation", 0, 1)]
    public void Stage_and_mode_follow_the_run(int stage, string stageName, int mode, int workflowMode)
    {
        var context = Create(Run(stage));

        context.StageName.Should().Be(stageName);
        context.Mode.Should().Be(mode);
        context.WorkflowMode.Should().Be(workflowMode);
    }

    [Fact]
    public void Background_run_has_empty_parameters()
    {
        var context = Create(Run(0));

        context.InputParameters.Should().BeEmpty();
        context.OutputParameters.Should().BeEmpty();
        context.SharedVariables.Should().BeEmpty();
        context.IsInTransaction.Should().BeFalse();
    }

    [Fact]
    public void Unknown_member_fails_explicitly()
    {
        var context = Create(Run(40));

        context.Invoking(c => c.SomethingNew).Should().Throw<NotSupportedException>().WithMessage("*SomethingNew*");
    }

    [Fact]
    public void Interface_that_is_no_execution_context_is_refused() =>
        FluentActions.Invoking(() => WorkflowContextProxy.Create(typeof(IDisposable), Run(40)))
            .Should().Throw<ArgumentException>();
}
