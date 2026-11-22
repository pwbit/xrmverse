using System.Runtime.Loader;
using FluentAssertions;
using Xrmd.Pipeline.Sandbox;

namespace Xrmd.Pipeline.Tests;

/// <summary>
/// Plan 2.14, W17: a custom workflow activity is recognised by the full name of a base type
/// (<c>System.Activities.CodeActivity</c>), read through a <c>MetadataLoadContext</c>: the assembly is never loaded for it.
/// </summary>
[Collection(WorkflowActivityAssemblyCollection.Name)]
public class WorkflowActivityTypeTests
{
    private static int LoadedCopies() =>
        AssemblyLoadContext.All.SelectMany(c => c.Assemblies).Count(a => a.GetName().Name == "Xrmd.TestWorkflowActivities");

    [Theory]
    [InlineData("Xrmd.TestWorkflowActivities.CreateTaskActivity", true)]
    [InlineData("Xrmd.TestWorkflowActivities.DerivedActivity", true)]
    [InlineData("Xrmd.TestWorkflowActivities.ActivityBase", false)]
    [InlineData("Xrmd.TestWorkflowActivities.NotAnActivity", false)]
    [InlineData("Xrmd.TestWorkflowActivities.Missing", false)]
    public void IsWorkflowActivityType_checks_the_base_type_by_full_name(string typeName, bool expected)
    {
        // Another class of the collection may have loaded a copy (WorkflowActivityResolveTests): none may be added here.
        var before = LoadedCopies();

        PluginAssemblyLoader.IsWorkflowActivityType(TestPluginAssembly.WorkflowActivityBytes, typeName).Should().Be(expected);

        LoadedCopies().Should().BeLessThanOrEqualTo(before, "only the metadata is read");
    }

    [Fact]
    public void Reference_paths_are_scanned_once_per_sdk_folder()
    {
        // Review M5: every plugintype validation reads the reference paths; the folders are scanned once, not on each call.
        PluginAssemblyLoader.ReferencePaths().Should().BeSameAs(PluginAssemblyLoader.ReferencePaths());
        PluginAssemblyLoader.ReferencePaths().Should().ContainKey("System.Runtime");
    }

    [Fact]
    public void A_plugin_or_garbage_is_not_a_workflow_activity()
    {
        PluginAssemblyLoader.IsWorkflowActivityType(TestPluginAssembly.Bytes, "Xrmd.TestPlugins.SetAttributePlugin").Should().BeFalse();
        PluginAssemblyLoader.IsWorkflowActivityType([1, 2, 3], "Xrmd.TestWorkflowActivities.CreateTaskActivity").Should().BeFalse();
        PluginAssemblyLoader.IsWorkflowActivityType([], "x").Should().BeFalse();
    }
}
