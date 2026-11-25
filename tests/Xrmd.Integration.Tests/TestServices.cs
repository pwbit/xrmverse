using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Query;
using Xrmd.Core.Solutions;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Pipeline.Tracing;
using Xrmd.Query.Sql;
using Xrmd.Security;
using Xrmd.Solution;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Builds an <see cref="OrganizationService"/> for a fixed caller outside an HTTP request, on the host's singletons
/// (metadata store, step registry, plug-in loader/runner, trace writer, async queue, security cache and guard) so plug-in steps fire
/// and security writes invalidate as in the host.
/// </summary>
public static class TestServices
{
    private sealed record StubUser(Guid ObjectId, string Name = "stub", Guid? CallerId = null, Guid? CallerObjectId = null) : ICurrentUser;

    /// <summary>The <see cref="Caller"/> the Core resolves for the user with Entra object id <paramref name="objectId"/>.</summary>
    public static Task<Caller> CallerAsync(IServiceProvider sp, Guid objectId) =>
        new CallerResolver(sp.GetRequiredService<IDbConnectionFactory>(), new StubUser(objectId), sp.GetRequiredService<SecurityCache>())
            .ResolveAsync();

    /// <summary>
    /// A service for the user with Entra object id <paramref name="objectId"/>, impersonating the <c>systemuser</c>
    /// <paramref name="callerId"/> when given (as the <c>MSCRMCallerID</c> header does). <paramref name="files"/> replaces the
    /// host's <see cref="Xrmd.Files.FileColumnService"/> (e.g. one on a spy file store).
    /// </summary>
    public static OrganizationService NewOrganizationService(IServiceProvider sp, Guid objectId, Guid? callerId = null,
        Xrmd.Files.FileColumnService? files = null, ILogger<Xrmd.Pipeline.CustomApis.CustomApiAuthoring>? authoringLogger = null)
    {
        var (ops, executor) = NewCore(sp, files: files, authoringLogger: authoringLogger);
        var factory = sp.GetRequiredService<IDbConnectionFactory>();
        var importer = NewImporter(sp, ops, executor);
        return new OrganizationService(factory,
            new CallerResolver(factory, new StubUser(objectId, CallerId: callerId), sp.GetRequiredService<SecurityCache>()), ops, executor,
            sp.GetRequiredService<MetadataOperations>(), importer, NewExporter(sp),
            sp.GetRequiredService<AuditMessages>(), sp.GetRequiredService<FieldSecurity>(), sp.GetRequiredService<AutoNumberMessages>(),
            NewUninstaller(sp, ops, importer), NewPromoter(sp, ops, importer), sp.GetRequiredService<AccessMessages>());
    }

    /// <summary>A <see cref="SolutionPromoter"/> on the host's singletons, <paramref name="core"/> and <paramref name="importer"/>.</summary>
    public static SolutionPromoter NewPromoter(IServiceProvider sp, IPipelineCore core, SolutionImporter importer)
    {
        var store = sp.GetRequiredService<IMetadataStore>();
        var remover = new ComponentRemover(store, core, sp.GetRequiredService<MetadataOperations>(), sp.GetRequiredService<SecurityCache>(),
            sp.GetRequiredService<StepRegistry>(), sp.GetRequiredService<Xrmd.Pipeline.CustomApis.CustomApiRegistry>(), NewWorkflowHook(sp, core),
            sp.GetService<Xrmd.Metadata.Computed.IComputedColumns>(), sp.GetService<Xrmd.Metadata.Computed.IRollupColumns>());
        return new SolutionPromoter(store, sp.GetRequiredService<IDbConnectionFactory>(),
            sp.GetRequiredService<Xrmd.Solution.Layers.ComponentLayerStore>(), new Xrmd.Solution.Layers.LayerRestorer(importer), remover,
            sp.GetRequiredService<DataLossGuard>(), sp.GetRequiredService<SecurityGuard>(), core, sp.GetRequiredService<AsyncOperationQueue>(),
            importer, sp.GetRequiredService<ILogger<SolutionPromoter>>());
    }

    /// <summary>A <see cref="SolutionUninstaller"/> on the host's singletons, <paramref name="core"/> and <paramref name="importer"/>.</summary>
    public static SolutionUninstaller NewUninstaller(IServiceProvider sp, IPipelineCore core, SolutionImporter importer)
    {
        var store = sp.GetRequiredService<IMetadataStore>();
        var remover = new ComponentRemover(store, core, sp.GetRequiredService<MetadataOperations>(), sp.GetRequiredService<SecurityCache>(),
            sp.GetRequiredService<StepRegistry>(), sp.GetRequiredService<Xrmd.Pipeline.CustomApis.CustomApiRegistry>(), NewWorkflowHook(sp, core),
            sp.GetService<Xrmd.Metadata.Computed.IComputedColumns>(), sp.GetService<Xrmd.Metadata.Computed.IRollupColumns>());
        return new SolutionUninstaller(store, sp.GetRequiredService<IDbConnectionFactory>(),
            sp.GetRequiredService<Xrmd.Solution.Layers.ComponentLayerStore>(), new Xrmd.Solution.Layers.LayerRestorer(importer), remover,
            sp.GetRequiredService<DataLossGuard>(), sp.GetRequiredService<SecurityGuard>(), core, sp.GetRequiredService<AsyncOperationQueue>(),
            sp.GetRequiredService<ILogger<SolutionUninstaller>>());
    }

    /// <summary>A <see cref="SolutionExporter"/> on the host's singletons.</summary>
    public static SolutionExporter NewExporter(IServiceProvider sp) => new(
        sp.GetRequiredService<IMetadataStore>(), sp.GetRequiredService<IDbConnectionFactory>(), sp.GetRequiredService<SecurityGuard>(),
        sp.GetRequiredService<SolutionComponentTracker>());

    /// <summary>A <see cref="SolutionImporter"/> on the host's singletons, <paramref name="core"/> and <paramref name="executor"/>.</summary>
    public static SolutionImporter NewImporter(IServiceProvider sp, IPipelineCore core, PipelineExecutor executor) => new(
        sp.GetRequiredService<IMetadataStore>(), sp.GetRequiredService<IDbConnectionFactory>(), core,
        sp.GetRequiredService<SolutionComponentTracker>(), sp.GetRequiredService<SecurityAdministration>(),
        sp.GetRequiredService<SecurityCache>(), sp.GetRequiredService<SecurityGuard>(), sp.GetRequiredService<RegistrationValidator>(),
        sp.GetRequiredService<StepRegistry>(), sp.GetRequiredService<AsyncOperationQueue>(),
        sp.GetRequiredService<Xrmd.Metadata.PublisherPrefixRule>(), sp.GetRequiredService<Xrmd.Pipeline.CustomApis.CustomApiRegistry>(),
        executor, sp.GetRequiredService<Xrmd.Solution.Layers.ManagedImport>(), sp.GetRequiredService<Xrmd.Workflows.WorkflowRegistry>(),
        sp.GetRequiredService<ILogger<SolutionImporter>>(), sp.GetService<Xrmd.Metadata.Computed.IComputedColumns>(),
        sp.GetService<Xrmd.Metadata.Computed.IRollupColumns>());

    /// <summary>
    /// A <see cref="PipelineExecutor"/> on the host's singletons and a new <see cref="CoreOperations"/>; <paramref name="runner"/>
    /// replaces the host's <see cref="PluginRunner"/> (e.g. a shorter plug-in timeout); <paramref name="logger"/> receives the
    /// executor's log (none when null).
    /// </summary>
    public static PipelineExecutor NewPipelineExecutor(IServiceProvider sp, PluginRunner? runner = null, ILogger<PipelineExecutor>? logger = null) =>
        NewCore(sp, runner, logger: logger).Executor;

    /// <summary>The classic workflows' pipeline hook (plan 2.14) on the host's registry, guard and caches, reading and writing through <paramref name="core"/>.</summary>
    public static Xrmd.Workflows.WorkflowPipelineHook NewWorkflowHook(IServiceProvider sp, IPipelineCore core)
    {
        var registry = sp.GetRequiredService<Xrmd.Workflows.WorkflowRegistry>();
        var store = new Xrmd.Workflows.WorkflowStore(core, registry);
        return new Xrmd.Workflows.WorkflowPipelineHook(registry,
            new Xrmd.Workflows.WorkflowActivation(store, sp.GetRequiredService<SecurityGuard>(),
                new Xrmd.Workflows.Actions.ActionMessages(core, sp.GetRequiredService<IMetadataStore>()), sp.GetRequiredService<IMetadataStore>()),
            core, sp.GetRequiredService<IMetadataStore>(), sp.GetRequiredService<SecurityCache>(), NewWorkflowJobs(sp, core), store,
            sp.GetRequiredService<SecurityGuard>());
    }

    /// <summary>The background workflow jobs (plan 2.14) reading and writing through <paramref name="core"/>.</summary>
    public static Xrmd.Workflows.Background.WorkflowJobs NewWorkflowJobs(IServiceProvider sp, IPipelineCore core)
    {
        var registry = sp.GetRequiredService<Xrmd.Workflows.WorkflowRegistry>();
        return new Xrmd.Workflows.Background.WorkflowJobs(sp.GetRequiredService<AsyncOperationQueue>(), registry,
            new Xrmd.Workflows.WorkflowStore(core, registry), sp.GetRequiredService<Xrmd.Workflows.Background.WorkflowWaits>(),
            sp.GetRequiredService<IDbConnectionFactory>());
    }

    private static (CoreOperations Ops, PipelineExecutor Executor) NewCore(IServiceProvider sp, PluginRunner? runner = null,
        Xrmd.Files.FileColumnService? files = null, ILogger<PipelineExecutor>? logger = null,
        ILogger<Xrmd.Pipeline.CustomApis.CustomApiAuthoring>? authoringLogger = null)
    {
        var store = sp.GetRequiredService<IMetadataStore>();
        var factory = sp.GetRequiredService<IDbConnectionFactory>();
        CoreOperations ops = null!;
        ops = new CoreOperations(store,
            new QueryExecutor(sp.GetRequiredService<SqlQueryCompiler>(), factory, store, TimeProvider.System,
                sp.GetRequiredService<SecurityCache>(), sp.GetRequiredService<FieldSecurity>(), sp.GetRequiredService<ICurrencyCatalog>(),
                files ?? sp.GetRequiredService<Xrmd.Files.FileColumnService>()),
            sp.GetRequiredService<SecurityAdministration>(),
            sp.GetRequiredService<Xrmd.Solution.SolutionComponentTracker>(), files ?? sp.GetRequiredService<Xrmd.Files.FileColumnService>(),
            sp.GetRequiredService<Xrmd.Audit.AuditWriter>(), sp.GetRequiredService<AuditMessages>(),
            sp.GetRequiredService<Xrmd.Core.Duplicates.DuplicateRulePublisher>(), sp.GetRequiredService<ICurrencyCatalog>(), sp.GetRequiredService<AccessMessages>(), null,
            // Plan 2.14 (W10): the core's writes wake the background jobs waiting on their record.
            new Xrmd.Workflows.Background.WorkflowWriteObserver(sp.GetRequiredService<Xrmd.Workflows.Background.WorkflowWaits>(),
                () => NewWorkflowJobs(sp, ops), () => new Xrmd.Workflows.Actions.ActionMessages(ops, store)),
            // Plan 2.12b (R7): the initial values of the rollups of a new row.
            sp.GetService<Xrmd.Core.Rollups.RollupCalculator>());
        var executor = new PipelineExecutor(ops, sp.GetRequiredService<StepRegistry>(), sp.GetRequiredService<PluginAssemblyLoader>(),
            runner ?? sp.GetRequiredService<PluginRunner>(), sp.GetRequiredService<PluginTraceWriter>(), sp.GetRequiredService<AsyncOperationQueue>(),
            factory, sp.GetRequiredService<IOptions<PipelineOptions>>(),
            sp.GetRequiredService<RegistrationValidator>(), sp.GetRequiredService<SecurityCache>(), sp.GetRequiredService<SecurityGuard>(),
            sp.GetRequiredService<Xrmd.Metadata.PublisherPrefixRule>(), sp.GetRequiredService<Xrmd.Audit.AuditSettings>(),
            sp.GetRequiredService<Xrmd.Pipeline.CustomApis.CustomApiRegistry>(),
            new Xrmd.Pipeline.CustomApis.CustomApiAuthoring(ops, store, sp.GetRequiredService<Xrmd.Metadata.PublisherPrefixRule>(), authoringLogger), store,
            sp.GetRequiredService<Xrmd.Pipeline.Webhooks.WebhookSecrets>(), sp.GetRequiredService<Xrmd.Pipeline.Webhooks.WebhookSender>(), logger,
            NewWorkflowHook(sp, ops),
            // X.12b: the flow notifications, reading through these core operations (as the host's scoped hook reads through its scope's).
            new Xrmd.WebApi.Connector.FlowSubscriptionHook(new CoreOverride(sp, ops), sp.GetRequiredService<Xrmd.WebApi.Connector.FlowSubscriptionCache>(),
                sp.GetRequiredService<AsyncOperationQueue>()));
        return (ops, executor);
    }

    /// <summary>The host's services, with <paramref name="ops"/> as the <see cref="CoreOperations"/>.</summary>
    private sealed class CoreOverride(IServiceProvider inner, CoreOperations ops) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(CoreOperations) ? ops : inner.GetService(serviceType);
    }
}
