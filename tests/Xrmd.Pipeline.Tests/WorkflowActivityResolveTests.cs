using System.Runtime.Loader;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.Sandbox;

namespace Xrmd.Pipeline.Tests;

/// <summary>
/// The classes that load or inspect the custom workflow activity assembly run one after the other: one of them checks that reading
/// its metadata loads no copy of it, which another's loading would break if they ran at the same time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkflowActivityAssemblyCollection
{
    public const string Name = "Workflow activity assembly";
}

/// <summary>
/// Plan 2.14, Task 8b (fix I3): the failures of <see cref="PluginAssemblyLoader.TryResolveWorkflowActivityType"/>. This test project
/// has no <c>System.Activities</c> (the host's comes with Xrmd.Workflows), so an activity type itself cannot be resolved here: the
/// failure to load its base type is the 500 of a missing dependency, as it would be on a host without CoreWF. The 400 of an abstract
/// activity type needs <c>System.Activities</c> and is not reachable here.
/// </summary>
[Collection(WorkflowActivityAssemblyCollection.Name)]
public sealed class WorkflowActivityResolveTests
{
    private static XrmdFaultException Resolve(string typeName)
    {
        var act = () => new PluginAssemblyLoader()
            .TryResolveWorkflowActivityType(Guid.NewGuid(), 1, TestPluginAssembly.WorkflowActivityBytes, typeName);
        return act.Should().Throw<XrmdFaultException>().Which;
    }

    [Fact]
    public void Nothing_is_resolved_without_content_when_the_assembly_is_not_loaded() =>
        new PluginAssemblyLoader().TryResolveWorkflowActivityType(Guid.NewGuid(), 1, null, "Xrmd.TestWorkflowActivities.CreateTaskActivity")
            .Should().BeNull();

    [Fact]
    public void A_type_that_is_not_an_activity_is_a_400()
    {
        var fault = Resolve("Xrmd.TestWorkflowActivities.NotAnActivity");

        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Be("Type 'Xrmd.TestWorkflowActivities.NotAnActivity' is not a custom workflow activity: " +
                                  "it does not derive from System.Activities.CodeActivity.");
    }

    [Fact]
    public void A_missing_type_is_a_400()
    {
        var fault = Resolve("Xrmd.TestWorkflowActivities.Missing");

        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Be("Type 'Xrmd.TestWorkflowActivities.Missing' was not found in the plug-in assembly.");
    }

    [Theory]
    [InlineData("Xrmd.TestWorkflowActivities.CreateTaskActivity")]
    [InlineData("Xrmd.TestWorkflowActivities.ActivityBase")]
    public void An_activity_whose_base_type_cannot_be_loaded_is_a_500_missing_dependency(string typeName)
    {
        var fault = Resolve(typeName);

        fault.Message.Should().StartWith("Could not load the plug-in assembly dependency 'System.Activities");
        fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.HttpStatus.Should().Be(500);
    }

    [Fact]
    public void The_assembly_is_loaded_in_a_collectible_context()
    {
        _ = Resolve("Xrmd.TestWorkflowActivities.NotAnActivity");

        AssemblyLoadContext.All.Where(c => c.Assemblies.Any(a => a.GetName().Name == "Xrmd.TestWorkflowActivities"))
            .Should().NotBeEmpty().And.OnlyContain(c => c.IsCollectible);
    }
}
