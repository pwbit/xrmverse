using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin;
using Xrmd.Audit;
using Xrmd.Core;
using Xrmd.Core.Duplicates;
using Xrmd.Core.Query;
using Xrmd.Files;
using Xrmd.Host.Auth;
using Xrmd.Host.Idp;
using Xrmd.Host.Ui;
using Xrmd.Mcp;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Pipeline.Tracing;
using Xrmd.Query.Sql;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.Storage.PostgreSql;
using Xrmd.Tds;
using Xrmd.WebApi;

namespace Xrmd.Host;

/// <summary>
/// Composition of the xrmd host, shared by <c>Program</c> and by tests that need the host on a real socket
/// (clients such as ServiceClient open their own connections).
/// </summary>
public static class XrmdHost
{
    private static readonly string[] WebApiVersions = ["v9.2", "v9.1", "v9.0"];

    /// <summary>The refusal of a negative <c>Rollups:MassCalculationDelay</c> at startup (plan 2.12b).</summary>
    public const string RollupDelayNegative =
        "Rollups:MassCalculationDelay must be zero or more (a time span such as 12:00:00): it is how long after a rollup column is created or updated its Mass Calculate Rollup Field job runs.";

    /// <summary>The refusal of a negative <c>Rollups:TransientRetryDelay</c> at startup (plan 2.12b).</summary>
    public const string RollupRetryDelayNegative =
        "Rollups:TransientRetryDelay must be zero or more (a time span such as 00:05:00): it is how long a rollup calculation job waits before it runs again after a transient database failure.";

    public static WebApplicationBuilder AddXrmd(this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        // Keeps the instance lock taken by UseXrmdAsync. Registered before every other hosted service of xrmd: hosted services stop
        // in reverse order, so it releases the lock only after the others (the async worker among them) have stopped.
        services.AddSingleton<InstanceLockWatchdog>();
        services.AddHostedService(sp => sp.GetRequiredService<InstanceLockWatchdog>());

        // Plan 2.7a, ruling P3: the engine of the organizations (Storage:Provider, refused at startup when unknown) and its SQL dialect.
        var storageProvider = StorageSettings.ParseProvider(builder.Configuration[StorageSettings.ProviderKey]);
        // Resolved lazily so the host still starts (e.g. /healthz) when ConnectionStrings:Org is not configured.
        static string OrgConnectionString(IServiceProvider sp) =>
            sp.GetRequiredService<IConfiguration>().GetConnectionString("Org")
            ?? throw new InvalidOperationException("ConnectionStrings:Org is not configured.");
        if (storageProvider == StorageProvider.PostgreSql)
        {
            // Plan 2.7a, Task 3: an Npgsql connection string; the factory and its dialect come from the PostgreSQL provider.
            services.AddPostgresStorage(OrgConnectionString);
        }
        else
        {
            services.AddSingleton<IDbConnectionFactory>(sp => new SqlConnectionFactory(OrgConnectionString(sp)));
            // One source for the dialect (fix round 1, m1): the one of the factory's connections, refused when it is not the configured one.
            services.AddSingleton(sp => SqlDialects.Of(sp.GetRequiredService<IDbConnectionFactory>(), storageProvider));
        }

        // Calculated columns (plan 2.12a): the store takes the translator through its optional IComputedColumns parameter.
        services.AddSingleton<Xrmd.Metadata.Computed.IComputedColumns, Xrmd.Workflows.Calculated.CalculatedColumns>();
        // Rollup columns (plan 2.12b): the store takes the translator through its optional IRollupColumns parameter.
        services.AddSingleton<Xrmd.Metadata.Computed.IRollupColumns, Xrmd.Workflows.Rollup.RollupColumnsService>();
        // Rollup values (plan 2.12b): computed in SQL as the system, for the rollup jobs and CalculateRollupField.
        services.AddSingleton<Xrmd.Core.Rollups.RollupCalculator>();
        // Rollup jobs (plan 2.12b, Task 4): the scheduler the store tells of each rollup change (resolved on first use: it depends on the
        // store), its Rollups settings, and the reconcile at host start (idle without an organization database).
        services.AddOptions<RollupOptions>()
            .Bind(builder.Configuration.GetSection("Rollups"))
            .Validate(o => o.MassCalculationDelay >= TimeSpan.Zero, RollupDelayNegative)
            .Validate(o => o.TransientRetryDelay >= TimeSpan.Zero, RollupRetryDelayNegative)
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RollupOptions>>().Value);
        services.AddSingleton(sp => new RollupJobScheduler(sp.GetRequiredService<IDbConnectionFactory>(), sp.GetRequiredService<IMetadataStore>(),
            sp.GetRequiredService<RollupOptions>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<AsyncOperationQueue>(),
            sp.GetRequiredService<ILogger<RollupJobScheduler>>()));
        services.AddSingleton<Xrmd.Metadata.Computed.IRollupJobs>(sp => new LazyRollupJobs(sp.GetRequiredService<RollupJobScheduler>));
        services.AddHostedService(sp => new RollupJobsStartup(sp.GetRequiredService<RollupJobScheduler>,
            !string.IsNullOrWhiteSpace(sp.GetRequiredService<IConfiguration>().GetConnectionString("Org")),
            sp.GetRequiredService<ILogger<RollupJobsStartup>>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<IMigrationLock>()));
        services.AddSingleton<IMetadataStore, MetadataStore>();
        services.AddSingleton<SdkMessageCatalog>();
        // Resolved on first use: the catalog depends on the store, which receives the handlers.
        services.AddSingleton<IEntityCreatedHandler>(sp => new LazyEntityCreatedHandler(() => sp.GetRequiredService<SdkMessageCatalog>()));
        services.AddSingleton<PrivilegeCatalog>();
        services.AddSingleton<IEntityCreatedHandler>(sp => new LazyEntityCreatedHandler(() => sp.GetRequiredService<PrivilegeCatalog>()));
        // Deletion: the catalogs drop their rows in the deleting session; the caches are invalidated after its commit.
        services.AddSingleton<IEntityDeletedHandler>(sp => new LazyEntityDeletedHandler(() => sp.GetRequiredService<SdkMessageCatalog>()));
        services.AddSingleton<IEntityDeletedHandler>(sp => new LazyEntityDeletedHandler(() => sp.GetRequiredService<PrivilegeCatalog>()));
        services.AddSingleton<IEntityDeletedHandler>(sp => sp.GetRequiredService<StepRegistry>());
        services.AddSingleton<IEntityDeletedHandler>(sp => sp.GetRequiredService<SecurityCache>());
        services.AddSingleton<SecurityBootstrap>();
        services.AddSingleton<SecurityCache>();
        // Field security: permissions cached with the SecurityCache's version; a change of a column's IsSecured invalidates them
        // after its commit (resolved lazily: the service depends on the metadata store, which receives the handlers).
        services.AddSingleton<FieldSecurity>();
        services.AddSingleton<IAttributeSecurityChangedHandler>(sp => new LazyAttributeSecurityChangedHandler(() => sp.GetRequiredService<FieldSecurity>()));
        services.AddSingleton<SecurityGuard>();
        services.AddSingleton<SecurityAdministration>();
        services.AddSingleton<SystemSeed>();
        services.AddSingleton<SchemaMigrator>();
        services.AddScoped<CallerResolver>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SqlQueryCompiler>();
        services.AddScoped<QueryExecutor>();
        // Auditing: the organization's settings are cached process-wide (the pipeline invalidates them after a commit that wrote
        // the organization); the writer adds the audit rows in the operations' transactions.
        services.AddSingleton<AuditSettings>();
        services.AddSingleton<AuditWriter>();
        services.AddSingleton<AuditReader>();
        services.AddSingleton<AuditMessages>();
        services.AddSingleton<AutoNumberMessages>();
        services.AddSingleton<AccessMessages>();
        // Deletes the expired audit rows every hour; idle without an organization database.
        services.AddHostedService(sp => new AuditRetentionService(sp.GetRequiredService<IDbConnectionFactory>,
            !string.IsNullOrWhiteSpace(sp.GetRequiredService<IConfiguration>().GetConnectionString("Org")),
            sp.GetRequiredService<ILogger<AuditRetentionService>>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<IMigrationLock>()));
        // Deletes the expired change tracking deletion log rows every hour; idle without an organization database.
        services.AddHostedService(sp => new ChangeTrackingRetentionService(sp.GetRequiredService<IDbConnectionFactory>,
            !string.IsNullOrWhiteSpace(sp.GetRequiredService<IConfiguration>().GetConnectionString("Org")),
            sp.GetRequiredService<ILogger<ChangeTrackingRetentionService>>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<IMigrationLock>()));
        // Deletes the expired elastic rows (plan 3.4, ruling E7) every Elastic:TtlSweepSeconds seconds (default 60, 0 disables); idle without an organization database.
        services.AddHostedService(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var seconds = string.IsNullOrWhiteSpace(configuration.GetConnectionString("Org"))
                ? 0
                : configuration.GetValue("Elastic:TtlSweepSeconds", 60);
            return new ElasticTtlService(sp.GetRequiredService<IDbConnectionFactory>, seconds, sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<ElasticTtlService>>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<IMigrationLock>());
        });
        // The cached currencies the read path formats money with (plan 2.10), invalidated by currency and organization writes.
        services.AddSingleton<ICurrencyCatalog, CurrencyCatalog>();
        services.AddScoped<CoreOperations>();
        services.AddScoped<IPipelineCore>(sp => sp.GetRequiredService<CoreOperations>());

        // Plug-in pipeline: registry, sandbox and trace writer are process-wide; the executor follows the request scope.
        services.AddOptions<PipelineOptions>()
            .Bind(builder.Configuration.GetSection("Pipeline"))
            .PostConfigure<IConfiguration>((options, configuration) =>
            {
                if (int.TryParse(configuration["Pipeline:PluginTimeoutSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                    && seconds > 0)
                    options.PluginTimeout = TimeSpan.FromSeconds(seconds);
                // No organization database, no async jobs (a bare host still serves /healthz).
                if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Org"))) options.AsyncWorkerEnabled = false;
            })
            .Validate(options => Xrmd.Pipeline.Webhooks.WebhookSender.IsValidTimeout(options.WebhookTimeout),
                Xrmd.Pipeline.Webhooks.WebhookSender.TimeoutOutOfRange)
            .ValidateOnStart();
        services.AddSingleton<StepRegistry>();
        services.AddSingleton<CustomApiRegistry>();
        // The custom API authoring rules (plan 3.1) write the API's message through the core operations: scoped like them.
        services.AddScoped<CustomApiAuthoring>();
        services.AddSingleton<PluginAssemblyLoader>();
        services.AddSingleton<RegistrationValidator>();
        // The write-only authvalue of the webhook endpoints (plan 3.2): taken at stage 20, read for the delivery.
        services.AddSingleton<Xrmd.Pipeline.Webhooks.WebhookSecrets>();
        // The files of the custom controls (plan 3.5 UI-1d): written by the solution import, served by /Controls.
        services.AddSingleton<ICustomControlFiles, CustomControlFiles>();
        // Posts the context to a webhook (plan 3.2), with its own handler: no redirects, connections renewed every 5 minutes.
        services.AddSingleton(sp => new Xrmd.Pipeline.Webhooks.WebhookSender(sp.GetRequiredService<IOptions<PipelineOptions>>().Value,
            sp.GetRequiredService<ILogger<Xrmd.Pipeline.Webhooks.WebhookSender>>()));
        services.AddSingleton(sp => new PluginRunner(sp.GetRequiredService<IOptions<PipelineOptions>>().Value));
        services.AddSingleton<PluginTraceWriter>();
        services.AddSingleton<AsyncOperationQueue>();
        // Runs the queued asynchronous steps; idle when Pipeline:AsyncWorkerEnabled is false or there is no database.
        services.AddHostedService<AsyncOperationWorker>();
        // Classic workflows (plan 2.14): the activated processes cached process-wide, the store and activation scoped like the core
        // operations they write through, and the pipeline's hook. The engine is not registered: each run builds one on the executor
        // that runs it (the hook's, or the job handler's), since the executor depends on the hook.
        services.AddSingleton<Xrmd.Workflows.WorkflowRegistry>();
        services.AddScoped<Xrmd.Workflows.WorkflowStore>();
        services.AddScoped<Xrmd.Workflows.Actions.ActionMessages>();
        services.AddScoped<Xrmd.Workflows.WorkflowActivation>();
        // Plan 2.14, W17: runs the custom workflow activities; the engine passes it the executor of the run (never resolved here).
        services.AddScoped<Xrmd.Workflows.Custom.CustomActivityRunner>();
        services.AddScoped<Xrmd.Pipeline.Workflows.IWorkflowPipelineHook, Xrmd.Workflows.WorkflowPipelineHook>();
        // Workflow authoring as code (plan 2.21, A18, A19): the service runs every write as the caller through the executor; the
        // solution component writes it needs outside the pipeline go through the metadata operations (Xrmd.Admin).
        services.AddScoped<Xrmd.Workflows.Authoring.WorkflowAuthoring>();
        services.AddScoped<Xrmd.Workflows.Authoring.IAuthoringSolutionComponents, WorkflowSolutionComponents>();
        // Background workflows (plan 2.14, Task 6): the waits of suspended jobs (process-wide: a cached "any wait" keeps writes free of
        // SQL while none waits), the queueing used by the hook and the engine, and the job handler (operationtype 10).
        services.AddSingleton<Xrmd.Workflows.Background.WorkflowWaits>();
        services.AddScoped<Xrmd.Workflows.Background.WorkflowJobs>();
        services.AddScoped<IAsyncOperationHandler, Xrmd.Workflows.Background.WorkflowJobHandler>();
        // W10: every core write wakes the jobs waiting on its record. The jobs are resolved on first need: they reach the core.
        services.AddScoped<IRecordWriteObserver>(sp => new Xrmd.Workflows.Background.WorkflowWriteObserver(
            sp.GetRequiredService<Xrmd.Workflows.Background.WorkflowWaits>(), sp.GetRequiredService<Xrmd.Workflows.Background.WorkflowJobs>,
            sp.GetRequiredService<Xrmd.Workflows.Actions.ActionMessages>));
        services.AddScoped<PipelineExecutor>();
        services.AddSingleton<Xrmd.Solution.Layers.ComponentLayerStore>();
        services.AddSingleton<SolutionComponentTracker>();
        services.AddSingleton<PublisherPrefixRule>();
        services.AddSingleton<MetadataOperations>();
        services.AddScoped<SolutionImporter>();
        services.AddScoped<Xrmd.Solution.Layers.LayerRestorer>();
        services.AddSingleton<SolutionExporter>();
        services.AddSingleton<Xrmd.Solution.Layers.ManagedImport>();
        // Runs the queued solution imports (asyncoperation operationtype 203) for the AsyncOperationWorker.
        services.AddScoped<IAsyncOperationHandler, SolutionImportJob>();
        // Duplicate detection (plan 2.15): the published rules cached process-wide, their match code tables, the publishing
        // messages and the publish jobs (operationtype 7).
        services.AddSingleton<DuplicateRuleCache>();
        services.AddSingleton<MatchCodeStore>();
        services.AddSingleton<DuplicateRulePublisher>();
        services.AddScoped<IAsyncOperationHandler, DuplicateRulePublishJob>();
        // BulkDetectDuplicates (plan 2.15, ruling D15): the bulk detection jobs (operationtype 8).
        services.AddScoped<IAsyncOperationHandler, BulkDetectDuplicatesJob>();
        // The shared component deletes, the data-loss guard and the uninstall of managed solutions (plan 2.6, Task 6); the
        // queued uninstalls (asyncoperation operationtype 208) for the AsyncOperationWorker.
        services.AddScoped<Xrmd.Core.Solutions.ComponentRemover>();
        services.AddSingleton<Xrmd.Core.Solutions.DataLossGuard>();
        services.AddScoped<Xrmd.Core.Solutions.SolutionUninstaller>();
        services.AddScoped<IAsyncOperationHandler, Xrmd.Core.Solutions.SolutionUninstallJob>();
        // The promote of a holding solution and StageAndUpgrade (plan 2.6, Task 7); their queued forms (operationtypes 207, 211).
        services.AddScoped<Xrmd.Core.Solutions.SolutionPromoter>();
        services.AddScoped<IAsyncOperationHandler, Xrmd.Core.Solutions.SolutionPromoteJob>();
        services.AddScoped<IAsyncOperationHandler, Xrmd.Core.Solutions.SolutionStageAndUpgradeJob>();
        services.AddScoped<Xrmd.Core.ChangeSet>();
        // Migration plan M3 (R4): the organization's migration lock, one per process (one process per organization database).
        services.AddSingleton<Xrmd.Admin.Migration.MigrationLock>();
        services.AddSingleton<IMigrationLock>(sp => sp.GetRequiredService<Xrmd.Admin.Migration.MigrationLock>());
        services.AddSingleton<Xrmd.Admin.Migration.MigrationPassphrases>();
        services.AddSingleton<Xrmd.Admin.Migration.MigrationCancellations>();
        services.AddSingleton<Xrmd.Admin.Migration.IMigrationPhase, Xrmd.Admin.Migration.VerifyPackagePhase>();
        services.AddSingleton<Xrmd.Admin.Migration.IMigrationPhase, Xrmd.Admin.Migration.Import.SchemaPhase>();
        services.AddSingleton<Xrmd.Admin.Migration.IMigrationPhase, Xrmd.Admin.Migration.Import.SecurityPhase>();
        services.AddSingleton<Xrmd.Admin.Migration.IMigrationPhase, Xrmd.Admin.Migration.Import.RecordsPhase>();
        services.AddSingleton<Xrmd.Admin.Migration.IMigrationPhase, Xrmd.Admin.Migration.Import.RelationshipsPhase>();
        services.AddSingleton<Xrmd.Admin.Migration.IMigrationPhase, Xrmd.Admin.Migration.Import.DeferredLookupsPhase>();
        services.AddSingleton<Xrmd.Admin.Migration.IMigrationPhase, Xrmd.Admin.Migration.Import.SharingPhase>();
        services.AddSingleton<Xrmd.Admin.Migration.IMigrationPhase, Xrmd.Admin.Migration.Import.FilesPhase>();
        services.AddSingleton<Xrmd.Admin.Migration.IMigrationPhase, Xrmd.Admin.Migration.Import.PostProcessingPhase>();
        services.AddSingleton<Xrmd.Admin.Migration.IMigrationPhase, Xrmd.Admin.Migration.Import.VerificationPhase>();
        services.AddScoped<IAsyncOperationHandler, Xrmd.Admin.Migration.MigrationImportJob>();
        // The rollup calculation jobs (plan 2.12b, R8): Mass Calculate Rollup Field (58) and Calculate Rollup Field (57).
        services.AddScoped<IAsyncOperationHandler, Xrmd.Core.Rollups.MassCalculateRollupJob>();
        services.AddScoped<IAsyncOperationHandler, Xrmd.Core.Rollups.CalculateRollupJob>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<IOrganizationService>(sp => sp.GetRequiredService<OrganizationService>());
        services.AddSingleton<ODataEdmModelBuilder>();
        services.AddSingleton<ODataQueryParser>();

        services.AddXrmdAuth();
        // The local identity provider (plan 2.8) exists only in Local mode; its EF Core store runs on the organization's engine
        // (plan 2.7b, Task 8).
        if (builder.Configuration["Auth:Mode"] == "Local")
            services.AddXrmdIdp(storageProvider == StorageProvider.PostgreSql
                ? (o, connectionString) => o.UseXrmdPostgres(connectionString)
                : (o, connectionString) => o.UseSqlServer(connectionString));
        builder.AddXrmdAdmin();
        services.AddXrmdMcp();
        // The SQL (TDS) endpoint (plan 3.3): idle without Tds:Port. Queries run as the caller through the query executor. The
        // authenticator is registered in every mode and refuses every login outside Auth:Mode=Local (rulings T4, T5).
        services.AddScoped<ITdsAuthenticator, Xrmd.Host.Tds.TdsAuthenticator>();
        services.AddScoped<ITdsQueryHandler, Xrmd.Tds.Execution.TdsQueryHandler>();
        services.AddXrmdTds(builder.Configuration);
        // Files:Store is validated here, so a bad value fails startup.
        services.AddXrmdFiles(builder.Configuration);
        // File columns: the upload/delete logic, the cascades of a dropped column or table (resolved lazily: the service
        // depends on the metadata store, which receives the handlers), the background blob deletions and the hourly sweep.
        // Blob deletions after a commit or rollback run off the request thread, drained by FileCleanupService.
        services.AddSingleton<FileDeletionQueue>(_ => new FileDeletionQueue());
        services.AddSingleton<FileColumnService>();
        services.AddSingleton<IEntityDeletedHandler>(sp => new LazyEntityDeletedHandler(() => sp.GetRequiredService<FileColumnService>()));
        services.AddSingleton<IAttributeDeletedHandler>(sp => new LazyAttributeDeletedHandler(() => sp.GetRequiredService<FileColumnService>()));
        services.AddHostedService(sp => new FileCleanupService(sp.GetRequiredService<FileColumnService>,
            !string.IsNullOrWhiteSpace(sp.GetRequiredService<IConfiguration>().GetConnectionString("Org")),
            sp.GetRequiredService<ILogger<FileCleanupService>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddXrmdUi();
        // The reverse proxy in front of the host (ruling H4); an invalid entry fails at startup (Program exits with 3 before this).
        services.AddSingleton(sp => ProxyOptions.Parse(sp.GetRequiredService<IConfiguration>()));
        return builder;
    }

    /// <summary>
    /// The startup recovery of the features after the populate: stranded duplicate rule publishes and orphan match code tables
    /// (every engine since plan 2.7b, Task 4), and the open migration run (every engine since plan 2.7b, Task 10).
    /// </summary>
    private static async Task RecoverFeaturesAsync(WebApplication app, IServiceProvider services)
    {
        // Plan 2.15, ruling D10: rules left Publishing with no publish job still to run go back to Unpublished.
        var reset = await DuplicateRulePublisher.ResetStrandedPublishesAsync(services.GetRequiredService<IDbConnectionFactory>(), CancellationToken.None);
        if (reset > 0) app.Logger.LogWarning("Reset {Count} duplicate detection rule(s) left Publishing without a publish job.", reset);
        // Plan 2.15, ruling D9: match code tables whose rule is no longer published (e.g. a crash before a cleanup).
        var dropped = await MatchCodeStore.DropOrphanTablesAsync(services.GetRequiredService<IDbConnectionFactory>(), CancellationToken.None);
        if (dropped > 0) app.Logger.LogWarning("Dropped {Count} match code table(s) whose duplicate detection rule is not published.", dropped);

        // Migration plan M3 (R4): an open migration run keeps the organization locked across a restart.
        var migrationLock = services.GetRequiredService<Xrmd.Admin.Migration.MigrationLock>();
        var migrationRuns = new Xrmd.Admin.Migration.MigrationRunStore(services.GetRequiredService<IDbConnectionFactory>());
        await migrationLock.LoadAsync(migrationRuns, CancellationToken.None);
        // A run left running by a stopped process lost its passphrase with it: it fails with the message to resume it.
        if (await migrationRuns.GetOpenRunAsync(CancellationToken.None) is { State: Xrmd.Admin.Migration.MigrationRunStates.Running } stranded)
            await migrationRuns.SetStateAsync(stranded.RunId, Xrmd.Admin.Migration.MigrationRunStates.Failed,
                Xrmd.Admin.Migration.MigrationImportJob.ResumeMessage(stranded.RunId), CancellationToken.None);
        if (migrationLock.ActiveRunId is { } run)
            app.Logger.LogWarning("The organization is locked by the migration run {RunId}: writes are refused until it is finalized.", run);
    }

    /// <summary>Runs the startup database work (migrations, metadata load, seed) and maps the request pipeline.</summary>
    public static async Task UseXrmdAsync(this WebApplication app)
    {
        var storageProvider = StorageSettings.ParseProvider(app.Configuration[StorageSettings.ProviderKey]);
        // Read before any database work, so an invalid Proxy entry fails before anything is touched.
        var proxy = app.Services.GetRequiredService<ProxyOptions>();

        // The SDK's known-types initialization, before any plug-in assembly can load (see PluginAssemblyLoader).
        PluginAssemblyLoader.Initialize();

        // Startup DB work only when an organization database is configured (a bare host still serves /healthz).
        if (!string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Org")))
        {
            var services = app.Services;
            await DatabaseBootstrapper.RunExclusiveAsync(app.Configuration, app.Logger, async () =>
            {
                var seed = app.Configuration.GetSection("Seed");
                var orgName = seed["OrgName"] is { Length: > 0 } n ? n : "xrmd";
                var rootBu = Guid.TryParse(seed["RootBusinessUnitId"], out var r) ? r : SeedOptions.DeriveRootBusinessUnitId(orgName);
                if (!Guid.TryParse(seed["AdminObjectId"], out var adminOid))
                    throw new InvalidOperationException("Seed:AdminObjectId must be configured with the administrator's object id (GUID).");
                var adminName = seed["AdminName"] is { Length: > 0 } a ? a : "Administrator";
                // The base currency of a new organization (plan 2.10, ruling X1): an invalid setting stops the startup here.
                // Null when none of the settings is given: the euro then applies only to a new organization, and no warning is logged.
                var currencyGiven = new[] { "BaseCurrency", "BaseCurrencyName", "BaseCurrencySymbol", "BaseCurrencyPrecision" }
                    .Any(k => !string.IsNullOrWhiteSpace(seed[k]));
                var baseCurrency = !currencyGiven ? null : BaseCurrencySeed.From(seed["BaseCurrency"], seed["BaseCurrencyName"], seed["BaseCurrencySymbol"],
                    seed["BaseCurrencyPrecision"] is { Length: > 0 } precisionText
                        ? int.TryParse(precisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var precision)
                            ? precision
                            : throw new InvalidOperationException($"{BaseCurrencySeed.SettingName}Precision '{precisionText}' is not a whole number.")
                        : null);

                // One xrmd process per organization database (ruling L1): taken once the database exists and before it is migrated
                // or seeded, so a refused process (InstanceLockException) never touches the database another process serves. Held
                // for the process lifetime and before any hosted service starts (the async worker's recovery puts back the In
                // Progress jobs, which is right only when no other process runs them).
                var instanceLock = await InstanceLock.AcquireAsync(app.Configuration.GetConnectionString("Org")!, app.Logger, CancellationToken.None,
                    storageProvider);
                try
                {
                    await OrganizationDatabase.PopulateAsync(services, new SeedOptions(orgName, rootBu, adminOid, adminName) { BaseCurrency = baseCurrency });
                    await RecoverFeaturesAsync(app, services);
                }
                catch
                {
                    await instanceLock.DisposeAsync();
                    throw;
                }
                services.GetRequiredService<InstanceLockWatchdog>().Attach(instanceLock);
            });
        }

        // Local mode: the org's key ring is loaded now (JwtBearer's key resolver is synchronous, OpenIddict's options read it), so a
        // key that cannot be loaded fails the start, not the first request; then the built-in clients are seeded.
        var localIdp = app.Configuration["Auth:Mode"] == "Local";
        if (localIdp)
        {
            if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Org")))
                throw new InvalidOperationException("ConnectionStrings:Org is required when Auth:Mode is Local.");
            await app.InitializeIdpAsync(CancellationToken.None);
        }

        // First: every absolute URL (OData context and next links, file Location, RetrieveCurrentOrganization's endpoints) is built
        // from the request, so the public scheme and host must be in place before metrics, logging, authentication and routing.
        // Without a trusted proxy, forwarded headers are ignored.
        if (proxy.Enabled)
        {
            app.UseMiddleware<ForwardedHostGuard>(proxy);
            app.UseForwardedHeaders(proxy.ToForwardedHeadersOptions());
        }

        app.UseXrmdRequestMetrics();
        app.UseMiddleware<RequestLoggingMiddleware>();
        if (localIdp) app.UseXrmdIdpHeaders();
        app.UseAuthentication();
        // Cookie-authenticated writes from another origin, or with a content type an HTML form can send, stop here (ruling C1).
        app.UseXrmdSessionCsrf();
        // $batch (#130) before routing: each of its requests runs through routing, authorization and the endpoints below.
        app.UseXrmdBatch();
        app.UseRouting();
        app.UseAuthorization();
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/admin")
                           || ctx.Request.Path.StartsWithSegments("/authoring") || ctx.Request.Path.StartsWithSegments("/Image"),
            api => api.UseMiddleware<ODataErrorMiddleware>());
        app.UseXrmdChangeSetGuard();
        // Migration plan M3 (R4): while a migration run is open, writes are 503 (each part of a $batch comes through here too).
        app.UseMiddleware<Xrmd.Admin.Migration.MigrationLockMiddleware>();

        app.MapGet("/healthz", () => "ok").AllowAnonymous();
        app.MapXrmdMetrics(app.Configuration);

        // The Admin API: valid token and System Administrator role.
        app.MapAdminEndpoints().MapIdpAdminEndpoints();

        // The authoring API beside it (plan 2.21, A18): a valid token and no role, every operation a pipeline request as the caller.
        app.MapWorkflowAuthoringEndpoints();

        // SOAP organization service (Execute) for clients that do not use the Web API, e.g. ServiceClient by default.
        app.MapOrganizationSoapEndpoint();

        // The MCP server (streamable HTTP, stateless): the same token as the Web API, every tool as the token's user.
        app.MapXrmdMcp("/mcp");

        // The web session cookie: POST sets it from the bearer header, DELETE clears it.
        app.MapSessionEndpoints();

        // Everything under /api requires a valid token.
        var api = app.MapGroup("/api").RequireAuthorization();

        // v9.2 is the canonical service root; v9.0/v9.1 are served too because ServiceClient's login flow calls
        // /api/data/v9.0/RetrieveCurrentOrganization before it knows the server version (Dataverse accepts all three).
        foreach (var version in WebApiVersions)
        {
            // Fixed routes ($metadata, EntityDefinitions, functions, file properties) take precedence over the generic {entityset} routes.
            api.MapGroup("/data/" + version).MapWebApiEndpoints();
        }

        // The local identity provider's passthrough endpoints and WebFinger (OpenIddict serves discovery and keys itself).
        if (localIdp) app.MapXrmdIdp();

        // The download url of image columns (<image>_url): the Web API's token and Read on the record.
        app.MapImageDownload();

        // Web resources (form scripts, HTML pages): the Web API's token and Read on webresource.
        app.MapWebResources();

        // Files of the custom (PCF) controls: the Web API's token and Read on customcontrol.
        app.MapCustomControlFiles();

        // The entity ribbon diffs the imported solutions kept (plan 3.5 UI-1e, ruling E1): the Web API's token and Read on solution.
        app.MapRibbon();

        // GetGlobalContext() for HTML web resources: our own script, never Microsoft's.
        app.MapClientGlobalContextScript();

        // The web admin shell and /ui/config.json: last, so the SPA fallback never answers a server path.
        app.MapXrmdUi();
    }
}
