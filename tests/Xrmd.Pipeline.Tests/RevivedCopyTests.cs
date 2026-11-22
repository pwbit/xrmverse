using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Pipeline.Sandbox;

namespace Xrmd.Pipeline.Tests;

/// <summary>
/// Runs alone, after the parallel collections: its test loads <c>Xrmd.TestPlugins.Dependency</c> into the default load
/// context for the rest of the process (the plug-in context resolves dependencies there only, and the default context
/// never unloads), and it loads <see cref="TestPluginAssembly.Bytes"/>, whose copies <see cref="SandboxTests"/> counts.
/// </summary>
[CollectionDefinition("default-context-dependency", DisableParallelization = true)]
public class DefaultContextDependencyCollection;

[Collection("default-context-dependency")]
public class RevivedCopyTests
{
    private sealed class NullFactory : IOrganizationServiceFactory
    {
        public IOrganizationService CreateOrganizationService(Guid? userId) => throw new NotSupportedException();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadWithoutHolding(PluginAssemblyLoader loader, Guid id, long version, byte[] content) =>
        loader.Load(id, version, () => content);

    [Fact]
    public async Task A_revived_copy_still_resolves_a_dependency_it_had_not_loaded()
    {
        // Issue #70, item 5: a revived copy is a context whose Unload() was already called.
        var path = Path.Combine(AppContext.BaseDirectory, "dependency", "Xrmd.TestPlugins.Dependency.dll");
        Assembly? Provide(AssemblyLoadContext context, AssemblyName name) =>
            name.Name == "Xrmd.TestPlugins.Dependency" ? context.LoadFromAssemblyPath(path) : null;
        AssemblyLoadContext.Default.Resolving += Provide;
        try
        {
            var loader = new PluginAssemblyLoader();
            var id = Guid.NewGuid();
            var first = loader.Load(id, 1, () => TestPluginAssembly.Bytes); // held, so that it is still alive when asked for again
            LoadWithoutHolding(loader, id, 2, TestPluginAssembly.Rebuilt(TestPluginAssembly.Bytes));

            const string typeName = "Xrmd.TestPlugins.MissingDependencyPlugin";
            var type = loader.ResolvePluginType(id, 3, () => TestPluginAssembly.Bytes, typeName);
            type.Assembly.Should().BeSameAs(first, "the released copy is revived");
            var runner = new PluginRunner(new PipelineOptions());
            var ctx = new XrmdPluginExecutionContext();
            var tracing = new TracingService();
            var provider = new PluginServiceProvider(ctx, tracing, new NullFactory(),
                new PluginLogger(tracing, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
            var target = new Entity("pb_thing");
            ctx.InputParameters["Target"] = target;
            await runner.RunAsync(runner.CreateInstance(type, null, null), provider, typeName, CancellationToken.None);

            target["pb_data"].Should().Be("dependency");
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= Provide;
        }
    }
}
