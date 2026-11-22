using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.Sandbox;

namespace Xrmd.Pipeline.Tests;

/// <remarks>
/// The loader's copies are process-wide (static). These tests count the copies listed in the process, so they rely on this
/// being the only class of Xrmd.Pipeline.Tests that loads <see cref="TestPluginAssembly.Bytes"/> or
/// <see cref="TestPluginAssembly.EarlyBoundBytes"/> while it runs (xUnit runs the tests of one class one at a time;
/// <see cref="RevivedCopyTests"/> loads them too, in a collection that runs alone).
/// </remarks>
public class SandboxTests
{
    private sealed class NullFactory : IOrganizationServiceFactory
    {
        public IOrganizationService CreateOrganizationService(Guid? userId) => throw new NotSupportedException();
    }

    private static readonly Func<byte[]> Content = () => TestPluginAssembly.Bytes;

    private static (XrmdPluginExecutionContext Ctx, TracingService Tracing, PluginServiceProvider Provider) NewProvider()
    {
        var ctx = new XrmdPluginExecutionContext();
        var tracing = new TracingService();
        var logger = new PluginLogger(tracing, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return (ctx, tracing, new PluginServiceProvider(ctx, tracing, new NullFactory(), logger));
    }

    private static async Task<PluginExecutionException> RunThrowing(string config, TimeSpan? timeout = null)
    {
        var loader = new PluginAssemblyLoader();
        var runner = new PluginRunner(new PipelineOptions { PluginTimeout = timeout ?? TimeSpan.FromSeconds(30) });
        var type = loader.ResolvePluginType(Guid.NewGuid(), 1, Content, "Xrmd.TestPlugins.ThrowPlugin");
        var plugin = runner.CreateInstance(type, config, null);
        var (_, _, provider) = NewProvider();
        var act = () => runner.RunAsync(plugin, provider, type.FullName!, CancellationToken.None);
        return (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
    }

    [Theory]
    [InlineData("Xrmd.TestPlugins.SetAttributePlugin", true)]
    [InlineData("Xrmd.TestPlugins.NotAPlugin", false)]
    [InlineData("Xrmd.TestPlugins.Missing", false)]
    public void IsPluginType_checks_the_type_in_the_shared_copy(string typeName, bool expected)
    {
        new PluginAssemblyLoader().IsPluginType(TestPluginAssembly.Bytes, typeName).Should().Be(expected);
        new PluginAssemblyLoader().IsPluginType([1, 2, 3], typeName).Should().BeFalse();
    }

    /// <summary>Hands out a service that records the entities it is asked to create.</summary>
    private sealed class CapturingFactory : IOrganizationServiceFactory, IOrganizationService
    {
        public List<Entity> Created { get; } = [];
        public IOrganizationService CreateOrganizationService(Guid? userId) => this;
        public Guid Create(Entity entity) { Created.Add(entity); return Guid.NewGuid(); }
        public Entity Retrieve(string entityName, Guid id, Microsoft.Xrm.Sdk.Query.ColumnSet columnSet) => throw new NotSupportedException();
        public void Update(Entity entity) => throw new NotSupportedException();
        public void Delete(string entityName, Guid id) => throw new NotSupportedException();
        public OrganizationResponse Execute(OrganizationRequest request) => throw new NotSupportedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            throw new NotSupportedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            throw new NotSupportedException();
        public EntityCollection RetrieveMultiple(Microsoft.Xrm.Sdk.Query.QueryBase query) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Early_bound_plugin_assembly_loads_and_runs()
    {
        const string typeName = "Xrmd.TestPlugins.EarlyBound.EarlyBoundLogPlugin";
        new PluginAssemblyLoader().IsPluginType(TestPluginAssembly.EarlyBoundBytes, typeName).Should().BeTrue();

        var type = new PluginAssemblyLoader().ResolvePluginType(Guid.NewGuid(), 1, () => TestPluginAssembly.EarlyBoundBytes, typeName);
        var plugin = new PluginRunner(new PipelineOptions()).CreateInstance(type, null, null);
        var ctx = new XrmdPluginExecutionContext { Depth = 1 };
        ctx.InputParameters["Target"] = new Entity("pb_pipe") { ["pb_name"] = "x" };
        var factory = new CapturingFactory();
        var provider = new PluginServiceProvider(ctx, new TracingService(), factory,
            new PluginLogger(new TracingService(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));

        await new PluginRunner(new PipelineOptions()).RunAsync(plugin, provider, typeName, CancellationToken.None);

        var created = factory.Created.Should().ContainSingle().Which;
        created.LogicalName.Should().Be("pb_pipelog");
        created.GetType().FullName.Should().Be("Xrmd.TestPlugins.EarlyBound.PipeLog");
        created["pb_name"].Should().Be("early|x|1");
    }

    [Fact]
    public void Loads_net462_plugin_and_resolves_sdk_from_host()
    {
        var type = new PluginAssemblyLoader().ResolvePluginType(Guid.NewGuid(), 1, Content, "Xrmd.TestPlugins.SetAttributePlugin");
        type.GetInterfaces().Should().Contain(typeof(IPlugin));
        typeof(IPlugin).IsAssignableFrom(type).Should().BeTrue();
        type.Assembly.GetName().GetPublicKeyToken().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateInstance_passes_configurations()
    {
        var loader = new PluginAssemblyLoader();
        var runner = new PluginRunner(new PipelineOptions());
        var type = loader.ResolvePluginType(Guid.NewGuid(), 1, Content, "Xrmd.TestPlugins.SetAttributePlugin");
        var plugin = runner.CreateInstance(type, "pb_name=x", "secret");
        var (ctx, _, provider) = NewProvider();
        var target = new Entity("pb_thing");
        ctx.InputParameters["Target"] = target;
        await runner.RunAsync(plugin, provider, type.FullName!, CancellationToken.None);
        target["pb_name"].Should().Be("x");
    }

    [Fact]
    public async Task Parameterless_constructor_plugin_is_created_and_runs()
    {
        var loader = new PluginAssemblyLoader();
        var runner = new PluginRunner(new PipelineOptions());
        var type = loader.ResolvePluginType(Guid.NewGuid(), 1, Content, "Xrmd.TestPlugins.EchoActionPlugin");
        var plugin = runner.CreateInstance(type, null, null);
        var (ctx, _, provider) = NewProvider();
        ctx.InputParameters["Text"] = "abc";
        await runner.RunAsync(plugin, provider, type.FullName!, CancellationToken.None);
        ctx.OutputParameters["Result"].Should().Be("cba");
    }

    [Fact]
    public void Unknown_type_is_rejected()
    {
        var act = () => new PluginAssemblyLoader().ResolvePluginType(Guid.NewGuid(), 1, Content, "Xrmd.TestPlugins.Nope");
        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message.Contains("Xrmd.TestPlugins.Nope"));
    }

    [Fact]
    public void NotAPlugin_is_rejected()
    {
        var act = () => new PluginAssemblyLoader().ResolvePluginType(Guid.NewGuid(), 1, Content, "Xrmd.TestPlugins.NotAPlugin");
        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message.Contains("Xrmd.TestPlugins.NotAPlugin"));
    }

    [Fact]
    public void Corrupt_assembly_is_rejected()
    {
        var act = () => new PluginAssemblyLoader().Load(Guid.NewGuid(), 1, () => new byte[] { 1, 2, 3 });
        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message.Contains("not a valid plug-in assembly"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadV1(PluginAssemblyLoader loader, Guid id)
    {
        var asm = loader.Load(id, 1, Content);
        return new WeakReference(AssemblyLoadContext.GetLoadContext(asm));
    }

    private static int CopiesOf(string name) => AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == name);

    [Fact]
    public void New_content_unloads_the_previous_copy_of_the_same_assembly()
    {
        var loader = new PluginAssemblyLoader();
        var id = Guid.NewGuid();
        var v1 = LoadV1(loader, id);
        var asm2 = loader.Load(id, 2, () => TestPluginAssembly.Rebuilt(TestPluginAssembly.Bytes));

        // The previous copy was collected before the new one was loaded.
        v1.IsAlive.Should().BeFalse();
        AssemblyLoadContext.GetLoadContext(asm2).Should().NotBeNull();
        CopiesOf("Xrmd.TestPlugins").Should().Be(1);
    }

    [Fact]
    public void Same_content_reuses_the_loaded_copy_whatever_the_version_or_registration()
    {
        var loader = new PluginAssemblyLoader();
        var id = Guid.NewGuid();
        var first = loader.Load(id, 1, Content);

        loader.Load(id, 2, Content).Should().BeSameAs(first);
        loader.IsLoaded(id, 2).Should().BeTrue();
        new PluginAssemblyLoader().Load(Guid.NewGuid(), 1, Content).Should().BeSameAs(first);
        loader.IsPluginType(TestPluginAssembly.Bytes, "Xrmd.TestPlugins.SetAttributePlugin").Should().BeTrue();
        CopiesOf("Xrmd.TestPlugins").Should().Be(1);
    }

    [Fact]
    public void Early_bound_registration_check_and_execution_share_one_copy()
    {
        const string typeName = "Xrmd.TestPlugins.EarlyBound.EarlyBoundLogPlugin";
        var loader = new PluginAssemblyLoader();

        loader.IsPluginType(TestPluginAssembly.EarlyBoundBytes, typeName).Should().BeTrue();
        var afterCheck = CopiesOf("Xrmd.TestPlugins.EarlyBound");
        loader.ResolvePluginType(Guid.NewGuid(), 1, () => TestPluginAssembly.EarlyBoundBytes, typeName);
        loader.ResolvePluginType(Guid.NewGuid(), 2, () => TestPluginAssembly.EarlyBoundBytes, typeName);

        CopiesOf("Xrmd.TestPlugins.EarlyBound").Should().Be(afterCheck, "the executions run the copy the check loaded");
    }

    [Fact]
    public void New_content_of_a_loaded_early_bound_assembly_is_loaded_although_the_old_copy_stays_alive()
    {
        const string typeName = "Xrmd.TestPlugins.EarlyBound.EarlyBoundLogPlugin";
        var loader = new PluginAssemblyLoader();
        var id = Guid.NewGuid();
        var v1 = loader.ResolvePluginType(id, 1, () => TestPluginAssembly.EarlyBoundBytes, typeName);

        // The SDK keeps every early-bound assembly alive: the released copy is not collected, the new one is loaded anyway.
        var v2 = loader.ResolvePluginType(id, 2, () => TestPluginAssembly.Rebuilt(TestPluginAssembly.EarlyBoundBytes), typeName);

        v2.Should().NotBeSameAs(v1);
        loader.TryResolvePluginType(id, 2, null, typeName).Should().BeSameAs(v2);
        loader.IsLoaded(id, 1).Should().BeFalse();
        AppDomain.CurrentDomain.GetAssemblies().Should().Contain(v1.Assembly, "the SDK keeps the old early-bound copy alive");
    }

    [Fact]
    public void Switching_between_two_builds_of_an_early_bound_assembly_does_not_load_more_copies()
    {
        const string typeName = "Xrmd.TestPlugins.EarlyBound.EarlyBoundLogPlugin";
        var loader = new PluginAssemblyLoader();
        var a = TestPluginAssembly.EarlyBoundBytes;
        var b = TestPluginAssembly.Rebuilt(a);
        Guid one = Guid.NewGuid(), two = Guid.NewGuid(), switching = Guid.NewGuid();

        void Round(int i)
        {
            // Two registrations with the same identity and different builds, and one registration updated back and forth.
            loader.ResolvePluginType(one, 1, () => a, typeName);
            loader.ResolvePluginType(two, 1, () => b, typeName);
            loader.ResolvePluginType(switching, i, () => i % 2 == 0 ? a : b, typeName);
            loader.IsPluginType(i % 2 == 0 ? b : a, typeName).Should().BeTrue();
        }

        Round(0);
        Round(1);
        var copies = CopiesOf("Xrmd.TestPlugins.EarlyBound");
        for (var i = 2; i < 8; i++) Round(i);

        CopiesOf("Xrmd.TestPlugins.EarlyBound").Should().Be(copies, "a released copy still alive is revived, not loaded again");
    }

    [Fact]
    public void A_previous_copy_still_in_use_is_waited_for_then_the_new_content_is_loaded_beside_it()
    {
        var logger = new CapturingLogger();
        var loader = new PluginAssemblyLoader(logger);
        var id = Guid.NewGuid();
        var v1 = loader.Load(id, 1, Content); // held: the copy cannot be collected

        var v2 = loader.Load(id, 2, () => TestPluginAssembly.Rebuilt(TestPluginAssembly.Bytes));

        v2.Should().NotBeSameAs(v1);
        logger.Messages.Should().ContainSingle(m => m.Contains("still alive") && m.Contains("after 10 collections"));
        GC.KeepAlive(v1);
    }

    /// <summary>Full collections until <paramref name="done"/> holds (at most 20); its last value.</summary>
    private static bool Collected(Func<bool> done)
    {
        for (var i = 0; i < 20 && !done(); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return done();
    }

    [Fact]
    public void A_copy_loaded_only_by_the_registration_check_is_released_after_it()
    {
        // Issue #70, item 2: nothing runs a copy the check loaded for a content no registration runs.
        var loader = new PluginAssemblyLoader();
        var live = loader.Load(Guid.NewGuid(), 1, Content); // held: a registration runs it
        var build = TestPluginAssembly.Rebuilt(TestPluginAssembly.Bytes);
        Collected(() => false);
        var before = CopiesOf("Xrmd.TestPlugins");

        loader.IsPluginType(build, "Xrmd.TestPlugins.SetAttributePlugin").Should().BeTrue();

        Collected(() => CopiesOf("Xrmd.TestPlugins") <= before).Should().BeTrue("the check's copy is unloaded once it is done");
        GC.KeepAlive(live);
    }

    [Fact]
    public void The_registration_check_of_a_new_build_leaves_the_live_copy_running()
    {
        // Issue #70, item 3: the check runs inside the registration's transaction, which may roll back.
        const string typeName = "Xrmd.TestPlugins.SetAttributePlugin";
        var loader = new PluginAssemblyLoader();
        var id = Guid.NewGuid();
        var live = loader.Load(id, 1, Content);

        loader.IsPluginType(TestPluginAssembly.Rebuilt(TestPluginAssembly.Bytes), typeName).Should().BeTrue();

        loader.IsLoaded(id, 1).Should().BeTrue();
        loader.Load(id, 1, () => throw new InvalidOperationException("not reloaded")).Should().BeSameAs(live);
    }

    [Fact]
    public void The_registration_check_of_a_new_early_bound_build_leaves_the_live_copy_and_the_execution_reuses_its_copy()
    {
        const string typeName = "Xrmd.TestPlugins.EarlyBound.EarlyBoundLogPlugin";
        var loader = new PluginAssemblyLoader();
        var id = Guid.NewGuid();
        var live = loader.ResolvePluginType(id, 1, () => TestPluginAssembly.EarlyBoundBytes, typeName);
        var build = TestPluginAssembly.Rebuilt(TestPluginAssembly.EarlyBoundBytes);

        loader.IsPluginType(build, typeName).Should().BeTrue();

        loader.TryResolvePluginType(id, 1, null, typeName).Should().BeSameAs(live);
        var afterCheck = CopiesOf("Xrmd.TestPlugins.EarlyBound");
        var next = loader.ResolvePluginType(id, 2, () => build, typeName);
        next.Should().NotBeSameAs(live);
        CopiesOf("Xrmd.TestPlugins.EarlyBound").Should().Be(afterCheck, "the execution runs the copy the check loaded");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadWithoutHolding(PluginAssemblyLoader loader, Guid id, long version, byte[] content) =>
        loader.Load(id, version, () => content);

    [Fact]
    public void A_release_waits_only_for_the_copies_it_released()
    {
        // Issue #70, item 4: a copy an earlier update left alive does not make every later update wait in vain.
        var logger = new CapturingLogger();
        var loader = new PluginAssemblyLoader(logger);
        var id = Guid.NewGuid();
        var leaked = loader.Load(id, 1, Content); // held: never collected while this test runs
        LoadWithoutHolding(loader, id, 2, TestPluginAssembly.Rebuilt(TestPluginAssembly.Bytes));

        LoadWithoutHolding(loader, id, 3, TestPluginAssembly.Rebuilt(TestPluginAssembly.Bytes));

        logger.Messages.Should().HaveCount(2);
        logger.Messages[0].Should().Contain("still alive");
        logger.Messages[1].Should().Contain(": collected after");
        GC.KeepAlive(leaked);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<PluginAssemblyLoader>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void TryResolvePluginType_needs_content_only_for_a_version_not_loaded()
    {
        var loader = new PluginAssemblyLoader();
        var id = Guid.NewGuid();
        const string typeName = "Xrmd.TestPlugins.SetAttributePlugin";

        loader.TryResolvePluginType(id, 1, null, typeName).Should().BeNull();
        var type = loader.TryResolvePluginType(id, 1, TestPluginAssembly.Bytes, typeName);
        type!.FullName.Should().Be(typeName);
        loader.TryResolvePluginType(id, 1, null, typeName).Should().BeSameAs(type);
        loader.TryResolvePluginType(id, 2, null, typeName).Should().BeNull();
    }

    [Fact]
    public void Same_version_returns_cached_assembly()
    {
        var loader = new PluginAssemblyLoader();
        var id = Guid.NewGuid();
        loader.Load(id, 1, Content).Should().BeSameAs(loader.Load(id, 1, () => throw new InvalidOperationException("not reloaded")));
    }

    [Fact]
    public async Task InvalidPluginExecutionException_maps_to_400_with_message()
    {
        var ex = await RunThrowing("invalid:nope");
        ex.Unexpected.Should().BeFalse();
        ex.Fault.HttpStatus.Should().Be(400);
        ex.Fault.ErrorCode.Should().Be(ErrorCodes.ISVAborted);
        ex.Fault.Message.Should().Be("nope");
    }

    [Fact]
    public async Task Other_exception_maps_to_500_unexpected()
    {
        var ex = await RunThrowing("crash:boom");
        ex.Unexpected.Should().BeTrue();
        ex.Fault.HttpStatus.Should().Be(500);
        ex.Fault.ErrorCode.Should().Be(unchecked((int)0x80040224));
        ex.Fault.Message.Should().Be("Unexpected exception from plug-in (Execute): Xrmd.TestPlugins.ThrowPlugin: System.InvalidOperationException: boom");
    }

    [Fact]
    public async Task Timeout_maps_to_500()
    {
        var ex = await RunThrowing("sleep:2000", TimeSpan.FromMilliseconds(200));
        ex.Unexpected.Should().BeTrue();
        ex.Fault.HttpStatus.Should().Be(500);
        ex.Fault.ErrorCode.Should().Be(unchecked((int)0x80040224));
        ex.Fault.Message.Should().Be("The plug-in execution failed because the operation has timed-out at the Sandbox Host.");
    }

    [Fact]
    public async Task FaultException_from_plugin_maps_to_400_with_detail()
    {
        var runner = new PluginRunner(new PipelineOptions());
        var plugin = new FaultingPlugin();
        var (_, _, provider) = NewProvider();
        var act = () => runner.RunAsync(plugin, provider, "X", CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<PluginExecutionException>()).Which;
        ex.Unexpected.Should().BeFalse();
        ex.Fault.ErrorCode.Should().Be(unchecked((int)0x80040217));
        ex.Fault.Message.Should().Be("gone");
        ex.Fault.HttpStatus.Should().Be(400);
    }

    private sealed class FaultingPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider) =>
            throw new System.ServiceModel.FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040217), Message = "gone" }, "gone");
    }

    public sealed class AbortingCtorPlugin : IPlugin
    {
        public AbortingCtorPlugin(string unsecure) => throw new InvalidPluginExecutionException("bad config " + unsecure);
        public void Execute(IServiceProvider serviceProvider) { }
    }

    public sealed class CrashingCtorPlugin : IPlugin
    {
        public CrashingCtorPlugin() => throw new FormatException("ctor boom");
        public void Execute(IServiceProvider serviceProvider) { }
    }

    [Fact]
    public void Constructor_InvalidPluginExecutionException_maps_to_400()
    {
        var act = () => new PluginRunner(new PipelineOptions()).CreateInstance(typeof(AbortingCtorPlugin), "x", null);

        var ex = act.Should().Throw<PluginExecutionException>().Which;
        ex.Unexpected.Should().BeFalse();
        ex.Fault.HttpStatus.Should().Be(400);
        ex.Fault.ErrorCode.Should().Be(ErrorCodes.ISVAborted);
        ex.Fault.Message.Should().Be("bad config x");
    }

    [Fact]
    public void Constructor_crash_maps_to_unexpected_500_with_detail()
    {
        var act = () => new PluginRunner(new PipelineOptions()).CreateInstance(typeof(CrashingCtorPlugin), null, null);

        var ex = act.Should().Throw<PluginExecutionException>().Which;
        ex.Unexpected.Should().BeTrue();
        ex.Fault.HttpStatus.Should().Be(500);
        ex.Fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        ex.Fault.Message.Should().Be(
            $"Unexpected exception from plug-in (Execute): {typeof(CrashingCtorPlugin).FullName}: System.FormatException: ctor boom");
        ex.Detail.Should().Contain("System.FormatException: ctor boom").And.Contain(nameof(CrashingCtorPlugin));
        ex.InnerException.Should().BeSameAs(ex.Fault);
    }

    [Fact]
    public async Task Execute_failure_keeps_the_full_exception_text_as_detail()
    {
        var ex = await RunThrowing("crash:boom");

        ex.Detail.Should().StartWith("System.InvalidOperationException: boom").And.Contain("ThrowPlugin.Execute");
    }
}
