using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Merge;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Pipeline.Tracing;
using Xrmd.Pipeline.Workflows;
using Xrmd.Pipeline.Webhooks;
using Xrmd.Security;

namespace Xrmd.Pipeline;

/// <summary>
/// Who runs a message and where: the effective <paramref name="Caller"/>, the user that started the whole chain, the
/// call <paramref name="Depth"/> (1 for a front-end call), the model context of the plug-in or workflow that issued it (nested calls) and the
/// <paramref name="Session"/> to reuse (nested calls; null at the first level, where the executor opens a connection).
/// </summary>
public sealed record InvocationContext(Caller Caller, Guid InitiatingUserId, int Depth, StepContext? Parent,
    DataSession? Session, Guid CorrelationId)
{
    /// <summary>
    /// The <see cref="SdkMirror"/> of the execution chain: the twins of the SDK objects of every level, which the plug-in views of
    /// the chain lend and read back. Null at the first SDK entry, which creates one; every nested invocation inherits it (through
    /// <c>with</c>), so a request crossing into the model again finds the twins its plug-in already holds.
    /// </summary>
    public SdkMirror? Mirror { get; init; }

    /// <summary>
    /// True for a write the core makes as the cascade of another (a request marked with <see cref="CascadeHint"/>, such as the child
    /// Delete of a Cascade delete relationship). Its plug-in steps run as for any nested request, but it triggers no workflow (plan
    /// 2.14, ruling W5): no real-time run and no background job. Not inherited: the requests of its plug-ins and of a workflow are
    /// new invocations.
    /// </summary>
    public bool IsCoreCascade { get; init; }

    /// <summary>
    /// Plan 4.1c: shared variables only the server sets (the <see cref="MailSchema.MailboxDeliveryVariable"/> of a mailbox delivery),
    /// copied into the <c>SharedVariables</c> of this execution and of the nested requests the core issues for it (such as the
    /// <c>queueitem</c> Create of AddToQueue), never into a request a plug-in or a workflow makes. Built by server code only: no SDK,
    /// SOAP or Web API parameter and no plug-in reaches it.
    /// </summary>
    public IReadOnlyDictionary<string, object>? ServerSharedVariables { get; init; }

    /// <summary>
    /// Plan 4.1c: columns the core writes on a Create after the validation of its Target (such as <c>email.senton</c>, which is not
    /// valid for Create); plug-ins see them in the post image, never in the Target. Only server code supplies them (no front end and no
    /// plug-in can), and they skip <c>ValidateWrite</c> (valid-for-Create, read-only and type checks): the server is trusted with the
    /// values; only a name that is not a column of the table fails. Only for a Create (any other message fails with
    /// <see cref="InvalidOperationException"/>), and not inherited by the nested requests of the execution.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ServerSetValues { get; init; }
}

/// <summary>
/// The event pipeline of a message: PreValidation (10, outside the transaction at the first level), PreOperation (20),
/// the core operation (30) and PostOperation (40; synchronous steps run, asynchronous ones are queued), stages 20–40 in
/// one transaction (a first-level write on an elastic table commits after each of stages 20, 30 and 40, see
/// <see cref="ElasticStages"/>). Entity images, filtering attributes, impersonation, plug-in faults and <c>plugintracelog</c> are
/// handled here. The execution owning the transaction reports its outcome to the session (<see cref="DataSession.Committed"/>
/// / <see cref="DataSession.RolledBack"/>). After the commit of its own transaction, a write to a registration entity invalidates the
/// <see cref="StepRegistry"/> and the <see cref="CustomApiRegistry"/>, one to a security entity the <see cref="SecurityCache"/>, one to a publisher the <see cref="PublisherPrefixRule"/> and
/// one to the organization the <see cref="AuditSettings"/> (also after a rollback: a read in the transaction may have cached its uncommitted value), and one to
/// <c>workflow</c> or <c>processtrigger</c> the activated processes (<see cref="IWorkflowPipelineHook.Invalidate"/>); a public Create/Update of a
/// <c>workflow</c> is validated before and after the PreOperation plug-ins (<see cref="IWorkflowPipelineHook.ValidateWriteAsync"/>, plan 2.21 A16) and a
/// SetState of one activates or deactivates it at the internal check of stage 20 (<see cref="IWorkflowPipelineHook.OnSetStateAsync"/>). A Create/Update of a
/// registration entity is checked by the <see cref="RegistrationValidator"/> right before the core operation (after the PreOperation plug-ins, so it sees their changes). SetState runs as an Update of
/// <c>statecode</c>/<c>statuscode</c> (marked with the Set State audit action, <see cref="AuditActionHint"/>); a custom <c>sdkmessage</c> (custom action) runs its steps without a core operation.
/// A custom API (<see cref="CustomApiRegistry"/>) runs its plug-in type as the main operation, its request checked by the
/// <see cref="CustomApiValidator"/> and its execute privilege demanded before stage 10, its response completed after stage 30 and 40.
/// A Create, Update or Delete of the custom API tables by a caller other than <see cref="Caller.Internal"/> goes through the
/// <see cref="CustomApiAuthoring"/> rules right before the core operation (and, for the Delete of an API, right after it).
/// Before stage 10, at every depth, the <see cref="SecurityGuard"/> checks the caller's privileges and access for the
/// message (not for custom actions and custom APIs): a denied message runs no plug-in, and a denied nested call dooms the transaction.
/// </summary>
public sealed partial class PipelineExecutor(IPipelineCore core, StepRegistry registry, PluginAssemblyLoader loader, PluginRunner runner,
    PluginTraceWriter traces, AsyncOperationQueue asyncQueue, IDbConnectionFactory factory, IOptions<PipelineOptions> options,
    RegistrationValidator validator, SecurityCache security, SecurityGuard guard, PublisherPrefixRule publisherPrefixes,
    AuditSettings auditSettings, CustomApiRegistry customApis, CustomApiAuthoring customApiAuthoring,
    IMetadataStore metadata, WebhookSecrets webhookSecrets, WebhookSender webhookSender, ILogger<PipelineExecutor>? logger = null,
    IWorkflowPipelineHook? workflows = null)
{
    /// <summary>The deepest call chain allowed; a call at <c>Depth</c> 9 fails as an infinite loop.</summary>
    public const int MaxDepth = 8;

    private const string Target = "Target";
    private const string Organization = "organization";

    /// <summary>The primary entity name of a message that has none (<c>PrimaryEntityName</c>, the filter of a global message).</summary>
    private const string NoEntity = "none";
    private const int PreValidation = 10;
    private const int PreOperation = 20;
    private const int MainOperation = 30;
    private const int PostOperation = 40;
    private const int Synchronous = 0;
    private const int Asynchronous = 1;
    private const int TraceException = 1;
    private const int TraceAll = 2;

    /// <summary>How long a step waits for a cancelled nested call of its (ended) plug-in before going on.</summary>
    private static readonly TimeSpan RevocationDrainTimeout = TimeSpan.FromSeconds(30);

    // SendEmail (plan 2.11, review I2): the images of the email named by EmailId (its primary entity).
    private static readonly HashSet<string> PreImageMessages = new(StringComparer.OrdinalIgnoreCase)
        { "Update", "Delete", "Assign", ActivitySchema.SendEmailMessage };
    private static readonly HashSet<string> PostImageMessages = new(StringComparer.OrdinalIgnoreCase)
        { "Create", "Update", "Assign", ActivitySchema.SendEmailMessage };
    /// <summary>
    /// Response keys of the SDK messages → the keys plug-ins see in <c>OutputParameters</c> (Dataverse names the
    /// Retrieve/RetrieveMultiple outputs <c>BusinessEntity</c>/<c>BusinessEntityCollection</c>).
    /// </summary>
    private static readonly Dictionary<string, string> PluginOutputKeys = new(StringComparer.Ordinal)
    {
        ["Entity"] = "BusinessEntity",
        ["EntityCollection"] = "BusinessEntityCollection",
    };

    private static readonly Dictionary<string, string> ResponseKeys =
        PluginOutputKeys.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    /// <summary>The data reads: no transaction of their own unless a step is registered, and no written entity.</summary>
    private static readonly HashSet<string> ReadMessages = new(StringComparer.OrdinalIgnoreCase) { "Retrieve", "RetrieveMultiple", "RetrieveEntityChanges", "RetrieveExchangeRate", "ExecuteCosmosSqlQuery" };

    /// <summary>State of one execution (one message at one depth): the trace setting, read once before the first step.</summary>
    private sealed class ExecutionState
    {
        public int? TraceSetting { get; set; }
    }

    /// <summary>What the transaction owned by a first-level execution has done, shared with the executions nested in it.</summary>
    private sealed class TransactionState
    {
        public HashSet<string> WrittenEntities { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Enqueued { get; set; }

        /// <summary>A nested call failed in this transaction: it can only be rolled back (as in Dataverse).</summary>
        public bool Doomed { get; set; }
    }

    /// <summary>The message of the failure of an owning execution whose transaction a failed nested call doomed.</summary>
    public const string NoActiveTransactionMessage =
        "There is no active transaction. This error is usually caused by custom plug-ins that ignore errors from service calls and continue processing.";

    private readonly ConditionalWeakTable<DataSession, TransactionState> _states = new();
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly IWorkflowPipelineHook _workflows = workflows ?? NoWorkflowPipelineHook.Instance;
    private (Guid Id, string Name)? _organization;

    /// <summary>The pipeline options in effect.</summary>
    public PipelineOptions Options { get; } = options.Value;

    /// <summary>
    /// The messages answered by the organization service itself, outside the pipeline's message table (plan 15, C2). The
    /// autonumber messages are <c>Xrmd.Core.AutoNumberMessages.Messages</c>, which this project cannot reference; an
    /// integration test keeps the two lists equal.
    /// </summary>
    private static readonly HashSet<string> ServiceMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        "SetState", "SetStateDynamicEntity", "ExecuteAsync", "ImportSolutionAsync", "ExportSolution",
        "SetAutoNumberSeed", "GetAutoNumberSeed", "GetNextAutoNumberValue",
    };

    /// <summary>
    /// True when <paramref name="name"/> (ignoring case) is a message the server handles itself, which no custom API may take
    /// (plan 15, C2): a core message, a message answered outside the pipeline, a message of the <see cref="SdkMessageCatalog"/>,
    /// SetState, SetStateDynamicEntity, ExecuteAsync, ImportSolutionAsync, ExportSolution, an autonumber message or an activity or
    /// note message xrmverse refuses (plan 2.11, A18: <see cref="ActivitySchema.RefusedMessages"/>). Only the names known in memory:
    /// an <c>sdkmessage</c> row of the organization (a custom action) is checked by the solution import against the database.
    /// </summary>
    public bool IsReservedMessageName(string name) => IsReservedMessageName(core, name);

    /// <summary><see cref="IsReservedMessageName(string)"/> against the messages of <paramref name="core"/>.</summary>
    public static bool IsReservedMessageName(IPipelineCore core, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return core.IsCoreMessage(name) || core.IsNonPipelineMessage(name) || SdkMessageCatalog.IsCatalogMessage(name)
               || ServiceMessages.Contains(name) || ActivitySchema.IsRefusedMessage(name);
    }

    /// <summary>The message that runs a workflow on demand (plan 2.14, W21): a core message whose stage 30 is the workflows' hook.</summary>
    public const string ExecuteWorkflowMessage = "ExecuteWorkflow";

    /// <summary>
    /// The stages of one message on its model context (<see cref="Run.Context"/>): PreValidation, the transaction, the images, the
    /// PreOperation steps and real-time workflows, the stage-20/30 boundary (<see cref="HoldAtCoreAsync"/> to
    /// <see cref="ReleaseCore"/>: the core works on the SDK request, the model takes its state back once after it), the
    /// PostOperation steps and workflows, the background workflows and asynchronous steps queued, the commit or rollback. The
    /// response is finished by the caller (<see cref="FinishResponse"/>).
    /// </summary>
    private async Task RunStagesAsync(Run run, CancellationToken ct)
    {
        var message = run.Message;
        var entityName = run.EntityName;
        var session = run.Session;
        var context = run.Context;
        var execution = new ExecutionState();
        // The response keys a custom API does not declare, removed after stage 30 and after stage 40: logged once.
        var dropped = new SortedSet<string>(StringComparer.Ordinal);

        try
        {
            using var stage = PipelineTelemetry.Stage("prevalidation", message, entityName);
            await RunStageAsync(await StepsAsync(run, PreValidation, Synchronous, ct), PreValidation, run, StepImages.None, execution, ct);
        }
        catch (PluginExecutionException ex)
        {
            throw ex.Fault;
        }

        var pre = await StepsAsync(run, PreOperation, Synchronous, ct);
        var post = await StepsAsync(run, PostOperation, Synchronous, ct);
        var postAsync = await StepsAsync(run, PostOperation, Asynchronous, ct);

        var owned = session.Transaction is null
                    && (!ReadMessages.Contains(message) || pre.Count > 0 || post.Count > 0 || postAsync.Count > 0);
        if (owned) session.Transaction = await session.Connection.BeginTransactionAsync(ct);
        var state = session.Transaction is null ? null : _states.GetValue(session, static _ => new TransactionState());
        // A first-level write on an elastic table commits after stages 20, 30 and 40 (ruling E3); nested, it joins (E4).
        var perStage = CommitsPerStage(run, owned);

        try
        {
            var images = await PreImagesAsync(run, [pre, post, postAsync], ct);
            // Plan 2.14 (W5): what the real-time workflows of stages 20 and 40 are matched on, read before the operation.
            var trigger = run.CustomAction || run.Invocation.IsCoreCascade ? null : await WorkflowTriggerAsync(run, ct);
            // Decision 3: who reads the response after stage 30. Nobody: the core's response is returned as it is, never converted.
            run.Observed = post.Count > 0 || postAsync.Count > 0 || trigger is not null || run.Api is not null;

            try
            {
                using (PipelineTelemetry.Stage("preoperation", message, entityName))
                {
                    // Plan 2.21 (A16): a public write of a process is checked before the pre-operation plug-ins see it, and again after.
                    if (!run.CustomAction) await ValidateProcessWriteAsync(run, ct);
                    await RunStageAsync(pre, await RealtimeWorkflowsAsync(trigger, entityName, PreOperation, ct), PreOperation, run,
                        images, execution, ct);
                    await HoldAtCoreAsync(run, ct);
                }
                if (perStage) state = await CommitStageAsync(session, state!, ct);

                using (PipelineTelemetry.Stage("mainoperation", message, entityName))
                    await MainOperationAsync(run, execution, ct);
            }
            finally
            {
                ReleaseCore(run);
            }

            ResponseToContext(run, dropped);
            if (!ReadMessages.Contains(message) && state is not null) AddWrittenEntities(run, state.WrittenEntities);
            if (perStage) state = await CommitStageAsync(session, state!, ct);

            images = await PostImagesAsync(run, images, [post, postAsync], ct);

            using (PipelineTelemetry.Stage("postoperation", message, entityName))
            {
                await RunStageAsync(post, await RealtimeWorkflowsAsync(trigger, entityName, PostOperation, ct), PostOperation, run,
                    images, execution, ct);
                if (run.Api is not null)
                {
                    CompleteCustomApi(run, dropped);
                    if (dropped.Count > 0) CustomApiValidator.LogDropped(_logger, run.Api, dropped);
                }

                // Plan 2.14 (W9): the background workflows the write triggers, queued in its transaction.
                if (trigger is not null)
                    await LentAsync(context.Clone(context.Stage), run.Mirror, view => _workflows.QueueBackgroundAsync(trigger.Message,
                        entityName, trigger.Columns, trigger.Owner, view, WithoutServerValues(run.Invocation), session, ct));

                foreach (var entry in postAsync)
                {
                    var queued = ContextFor(entry, context, run.Request).Clone(PostOperation);
                    queued.Mode = Asynchronous;
                    queued.OwningExtension = StepReference(entry.Step);
                    AddImages(queued, entry, images, post: true, run.Mirror);
                    SyncBoundaries(queued, run.Mirror);
                    await asyncQueue.EnqueueAsync(session, entry.Step, queued, run.Mirror, ct);
                    if (state is not null) state.Enqueued = true;
                }
            }

            if (owned) await CommitOwnedAsync(session, state!);
        }
        catch (DbException) when (owned && state is { Doomed: true } && session.Dialect.FailedStatementAbortsTransaction)
        {
            // Plan 2.7a, Task 6: a nested call that failed doomed the transaction. On an engine where a failed statement aborts
            // the whole transaction (PostgreSQL), the next statement fails before the commit's check: the request fails as that
            // check would.
            await RollbackAsync(session, state);
            throw new XrmdFaultException(ErrorCodes.UnexpectedPluginException, NoActiveTransactionMessage, 500);
        }
        catch (PluginExecutionException ex)
        {
            if (owned) await RollbackAsync(session, state);
            throw ex.Fault;
        }
        catch
        {
            if (owned) await RollbackAsync(session, state);
            throw;
        }
    }

    /// <summary>
    /// The steps a stage runs (<see cref="MergedSteps.ForStageAsync"/>: the message's own steps whose filtering attributes match,
    /// merged with the bulk message's on a standard table); none when the message is no event (Upsert on a standard table).
    /// </summary>
    private async Task<IReadOnlyList<StageStep>> StepsAsync(Run run, int stage, int mode, CancellationToken ct) =>
        run.IsEvent ? await MergedSteps.ForStageAsync(registry, run.Request, run.EntityName, stage, mode, run.StandardTable, run.BulkInner, ct) : [];


    /// <summary>
    /// Records that a call nested in <paramref name="session"/>'s transaction failed: the execution owning the transaction
    /// then rolls it back and fails with <see cref="NoActiveTransactionMessage"/> even if the plug-in caught the fault.
    /// Nothing happens outside a transaction.
    /// </summary>
    internal void MarkDoomed(DataSession session)
    {
        if (session.Transaction is not null && _states.TryGetValue(session, out var state)) state.Doomed = true;
    }

    /// <summary>
    /// Commits the transaction an execution owns and reports it: the session's deferred work runs, then the caches built on
    /// the written entities are invalidated and the asynchronous queue is signalled when a step was queued.
    /// </summary>
    /// <exception cref="XrmdFaultException">500 <see cref="NoActiveTransactionMessage"/> when a nested call doomed the transaction.</exception>
    private async Task CommitOwnedAsync(DataSession session, TransactionState state)
    {
        // A plug-in swallowed the failure of one of its calls: Dataverse refuses to commit what is left.
        if (state.Doomed)
            throw new XrmdFaultException(ErrorCodes.UnexpectedPluginException, NoActiveTransactionMessage, 500);
        // CancellationToken.None: once the stages have run, a cancelled request must not leave the commit half done.
        await session.Transaction!.CommitAsync(CancellationToken.None);
        EndOwnedTransaction(session);
        // What the transaction's work deferred to its commit (e.g. publishing metadata changes).
        session.Committed();
        if (state.WrittenEntities.Overlaps(StepRegistry.RegistrationEntities))
        {
            registry.Invalidate();
            customApis.Invalidate();
        }
        // organization holds the hierarchy security settings (plan 2.5, ruling H10); it stays out of SecurityEntities,
        // whose columns cannot be secured.
        if (state.WrittenEntities.Overlaps(SecurityCache.SecurityEntities) || state.WrittenEntities.Contains(Organization))
            security.Invalidate();
        if (state.WrittenEntities.Contains("publisher")) publisherPrefixes.Invalidate();
        if (state.WrittenEntities.Contains(Organization)) auditSettings.Invalidate();
        if (state.WrittenEntities.Contains(WorkflowSchema.Table) || state.WrittenEntities.Contains(WorkflowSchema.TriggerTable))
        {
            _workflows.Invalidate();
            // An action's activation (or the delete of its definition) writes its sdkmessage and filter (plan 2.14, W14).
            registry.Invalidate();
        }
        if (state.Enqueued) asyncQueue.Signal();
    }

    /// <summary>
    /// Opens a transaction on <paramref name="session"/> that the caller owns (a Web API change set, #130): every execution given
    /// this session joins it, and <see cref="CommitTransactionAsync"/> or <see cref="RollbackTransactionAsync"/> ends it with the
    /// work a first-level execution does at its own commit or rollback (cache invalidation, the asynchronous queue's signal).
    /// </summary>
    /// <exception cref="InvalidOperationException">The session already has a transaction.</exception>
    public async Task BeginTransactionAsync(DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Transaction is not null) throw new InvalidOperationException("The session already has a transaction.");
        session.Transaction = await session.Connection.BeginTransactionAsync(ct);
        _states.GetValue(session, static _ => new TransactionState());
    }

    /// <summary>Commits the transaction <see cref="BeginTransactionAsync"/> opened.</summary>
    /// <exception cref="XrmdFaultException">500 <see cref="NoActiveTransactionMessage"/> when a nested call doomed the transaction
    /// (it is rolled back).</exception>
    public async Task CommitTransactionAsync(DataSession session)
    {
        var state = _states.GetValue(session, static _ => new TransactionState());
        try
        {
            await CommitOwnedAsync(session, state);
        }
        catch
        {
            await RollbackAsync(session, state);
            throw;
        }
    }

    /// <summary>Rolls back the transaction <see cref="BeginTransactionAsync"/> opened; nothing happens without one.</summary>
    public Task RollbackTransactionAsync(DataSession session) =>
        session.Transaction is null ? Task.CompletedTask : RollbackAsync(session, _states.TryGetValue(session, out var state) ? state : null);

    /// <summary>
    /// The end of one stage of an elastic write (ruling E3): commits the stage's transaction (<see cref="CommitOwnedAsync"/>)
    /// and opens the next stage's, with a state of its own.
    /// </summary>
    private async Task<TransactionState> CommitStageAsync(DataSession session, TransactionState state, CancellationToken ct)
    {
        await CommitOwnedAsync(session, state);
        session.Transaction = await session.Connection.BeginTransactionAsync(ct);
        return _states.GetValue(session, static _ => new TransactionState());
    }

    private void EndOwnedTransaction(DataSession session)
    {
        session.Transaction?.Dispose();
        session.Transaction = null;
        _states.Remove(session);
    }

    private async Task RollbackAsync(DataSession session, TransactionState? state)
    {
        try
        {
            if (session.Transaction?.Connection is not null) await session.Transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Rollback of the pipeline transaction failed.");
        }
        finally
        {
            EndOwnedTransaction(session);
            // A read of the audit settings in this transaction may have cached the uncommitted value of the organization row.
            if (state?.WrittenEntities.Contains(Organization) == true) auditSettings.Invalidate();
            // What the transaction's work deferred to its outcome is discarded (e.g. metadata changes, their lock released).
            session.RolledBack();
        }
    }

    private async Task<StepContext> NewContextAsync(Request request, InvocationContext invocation,
        DataSession session, string? entityName, Guid entityId, CancellationToken ct)
    {
        _organization ??= await core.OrganizationAsync(session, ct);
        var now = DateTime.UtcNow;
        var context = new StepContext
        {
            MessageName = request.Name,
            InputParameters = request.Parameters,
            PrimaryEntityName = entityName ?? NoEntity,
            PrimaryEntityId = entityId,
            UserId = invocation.Caller.UserId,
            BusinessUnitId = invocation.Caller.BusinessUnitId,
            InitiatingUserId = invocation.InitiatingUserId,
            // The user the request was authenticated as (the impersonator when impersonating); nested calls keep the parent's.
            AuthenticatedUserId = invocation.Parent?.AuthenticatedUserId ?? invocation.Caller.ImpersonatorId ?? invocation.Caller.UserId,
            Depth = invocation.Depth,
            ParentContext = invocation.Parent,
            CorrelationId = invocation.CorrelationId,
            OrganizationId = _organization.Value.Id,
            OrganizationName = _organization.Value.Name,
            RequestId = request.RequestId ?? Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            OperationCreatedOn = now,
            Mode = Synchronous,
        };
        // Plan 4.1c: the server's shared variables (a mailbox delivery's), at every stage of this execution.
        if (invocation.ServerSharedVariables is { } shared)
            foreach (var (key, value) in shared) context.SharedVariables[key] = value;
        // What a plug-in or workflow must leave as it is: this snapshot (a parent's was taken when the parent was built).
        ReservedSharedVariables.Snapshot(context);
        return context;
    }

    /// <summary>
    /// <paramref name="invocation"/> as a workflow gets it (plan 4.1c): without the server shared variables and server values, which
    /// the requests of a workflow, like those of a plug-in, never carry. Its context still shows the variables.
    /// </summary>
    private static InvocationContext WithoutServerValues(InvocationContext invocation) =>
        invocation.ServerSharedVariables is null && invocation.ServerSetValues is null
            ? invocation
            : invocation with { ServerSharedVariables = null, ServerSetValues = null };

    /// <summary>
    /// The main operation of a custom API (plan 15, C7): its plug-in type, when it has one, run once as a synthetic step of
    /// stage 30, synchronous, with no configuration, no images and no impersonation, inside the transaction and with the
    /// <c>InputParameters</c> the pre-stages saw. Its failure follows the plug-in rules (400 for an
    /// <c>InvalidPluginExecutionException</c>, 500 otherwise).
    /// </summary>
    /// <exception cref="PluginExecutionException">The plug-in failed (also when it could not be loaded or created).</exception>
    private Task RunMainOperationAsync(CustomApiDefinition api, Run run, ExecutionState execution, CancellationToken ct)
    {
        if (api.MainOperation is not { } main) return Task.CompletedTask;
        // No step row exists for the main operation: its id is empty, so its traces and OwningExtension name no step.
        var step = new PluginStep(Guid.Empty, api.UniqueName, api.UniqueName, api.BoundEntity, MainOperation, Synchronous, 0, null, null, null, null,
            main.PluginTypeId, main.TypeName, main.AssemblyId, main.AssemblyVersion, []);
        return RunStepAsync(new StageStep(step, false), MainOperation, run.Context, run, StepImages.None, execution, ct);
    }

    private static bool Wants(PluginStepImage image, bool pre) => image.ImageType == 2 || image.ImageType == (pre ? 0 : 1);

    /// <summary>
    /// The images of an execution: the single record's (<paramref name="Pre"/>, <paramref name="Post"/>), or, for a bulk
    /// execution, one per target (<paramref name="PreList"/>, <paramref name="PostList"/>, index-aligned with <c>Targets</c>). Each
    /// is the model twin of the record the core read; a step's projection is taken from that record (<see cref="AddImages"/>).
    /// </summary>
    private sealed record StepImages(Record? Pre, Record? Post, Record?[]? PreList, Record?[]? PostList)
    {
        public static readonly StepImages None = new(null, null, null, null);
    }

    /// <summary>The context <paramref name="entry"/>'s step sees: <see cref="MergedSteps.MultipleView"/> for a merged step.</summary>
    private static StepContext ContextFor(StageStep entry, StepContext context, Request request) =>
        entry.MultipleView ? MergedSteps.MultipleView(context, request) : context;

    private static RecordReference StepReference(PluginStep step) =>
        new() { LogicalName = "sdkmessageprocessingstep", Id = step.Id, Name = step.Name };

    private async Task RunStageAsync(IReadOnlyList<StageStep> steps, int stage, Run run, StepImages images, ExecutionState execution,
        CancellationToken ct)
    {
        foreach (var step in steps)
            await RunStepAsync(step, stage, run.Context, run, images, execution, ct);
    }

    /// <summary>
    /// Runs a stage of synchronous plug-in steps and real-time workflows as one list (plan 2.14, ruling W6): ordered by rank, then
    /// plug-ins before workflows at equal rank, then id. A workflow gets the plug-in view of a copy of the stage's context
    /// (<see cref="LentAsync"/>), read back into the model when it ends.
    /// </summary>
    private async Task RunStageAsync(IReadOnlyList<StageStep> steps, IReadOnlyList<RealtimeWorkflow> workflows, int stage, Run run,
        StepImages images, ExecutionState execution, CancellationToken ct)
    {
        if (workflows.Count == 0)
        {
            await RunStageAsync(steps, stage, run, images, execution, ct);
            return;
        }
        var ordered = steps.Select(s => (s.Step.Rank, IsWorkflow: false, s.Step.Id, Step: (StageStep?)s, Workflow: (RealtimeWorkflow?)null))
            .Concat(workflows.Select(w => (w.Rank, IsWorkflow: true, Id: w.ActivationId, Step: (StageStep?)null, Workflow: (RealtimeWorkflow?)w)))
            .OrderBy(i => i.Rank).ThenBy(i => i.IsWorkflow).ThenBy(i => i.Id)
            .ToList();
        foreach (var item in ordered)
        {
            if (item.Step is { } step)
            {
                await RunStepAsync(step, stage, run.Context, run, images, execution, ct);
                continue;
            }
            var workflowContext = run.Context.Clone(stage);
            workflowContext.Mode = Synchronous;
            workflowContext.IsInTransaction = run.Session.Transaction is not null;
            workflowContext.OwningExtension = new RecordReference { LogicalName = WorkflowSchema.Table, Id = item.Workflow!.ActivationId, Name = item.Workflow.Name };
            await LentAsync(workflowContext, run.Mirror,
                view => _workflows.RunRealtimeAsync(this, item.Workflow, view, WithoutServerValues(run.Invocation), run.Session, ct));
            ReservedSharedVariables.DemandKept(workflowContext, $"The real-time workflow '{item.Workflow.Name}'");
        }
    }

    /// <summary>The real-time workflows <paramref name="trigger"/> runs at <paramref name="stage"/> (none without a trigger).</summary>
    private async Task<IReadOnlyList<RealtimeWorkflow>> RealtimeWorkflowsAsync(WorkflowTrigger? trigger, string? entityName, int stage,
        CancellationToken ct) =>
        trigger is null ? [] : await _workflows.GetRealtimeAsync(trigger.Message, entityName, stage, trigger.Columns, trigger.Owner, ct);

    /// <summary>
    /// Runs <paramref name="action"/> with the plug-in view of <paramref name="context"/> (a hook of the workflows), then reads the view
    /// back into the model, also when it fails. A level the core holds is read into the model before it is lent (<see cref="HeldLevels"/>).
    /// </summary>
    private static async Task LentAsync(StepContext context, SdkMirror mirror, Func<XrmdPluginExecutionContext, Task> action)
    {
        var view = PluginContextAdapter.Lend(context, mirror, HeldLevels(mirror));
        try
        {
            await action(view);
        }
        finally
        {
            PluginContextAdapter.Return(view, mirror);
        }
    }

    /// <inheritdoc cref="LentAsync(StepContext, SdkMirror, Func{XrmdPluginExecutionContext, Task})"/>
    private static async Task<T> LentAsync<T>(StepContext context, SdkMirror mirror, Func<XrmdPluginExecutionContext, Task<T>> action)
    {
        var view = PluginContextAdapter.Lend(context, mirror, HeldLevels(mirror));
        try
        {
            return await action(view);
        }
        finally
        {
            PluginContextAdapter.Return(view, mirror);
        }
    }

    /// <summary>Runs one synchronous step, writes its trace per the organization setting and rethrows its failure.</summary>
    /// <exception cref="PluginExecutionException">The plug-in failed (also when it could not be loaded or created).</exception>
    private async Task RunStepAsync(StageStep entry, int stage, StepContext baseContext, Run run, StepImages images,
        ExecutionState execution, CancellationToken ct)
    {
        var context = ContextFor(entry, baseContext, run.Request).Clone(stage);
        context.Mode = Synchronous;
        context.IsInTransaction = run.Session.Transaction is not null;
        context.OwningExtension = StepReference(entry.Step);
        if (stage != PreValidation) AddImages(context, entry, images, post: stage == PostOperation, run.Mirror);
        await ExecuteStepAsync(entry.Step, context, run.Invocation.Caller, run.Session, run.Mirror, execution, ct);
        // A webhook step has no access to the context; a plug-in's changes are checked against the server's values.
        if (entry.Step.Endpoint is null)
        {
            var who = $"The plug-in step '{entry.Step.Name}'";
            ReservedSharedVariables.DemandKept(context, who);
            if (!ReferenceEquals(context.SharedVariables, baseContext.SharedVariables)) ReservedSharedVariables.DemandKept(baseContext, who);
        }
    }

    /// <summary>
    /// Runs one asynchronous step for the async job worker: <paramref name="context"/> is the job's context, already
    /// rebuilt (Mode 1, Stage 40, <c>IsInTransaction</c> false, its images). <paramref name="session"/> has no transaction,
    /// so every call of the plug-in's organization service runs in a transaction of its own. The step's impersonating
    /// user, the trace setting and the error mapping apply as for a synchronous step.
    /// <paramref name="mirror"/> already holds the twins of the job's context (the worker's, which adopted the deserialized values).
    /// </summary>
    /// <exception cref="PluginExecutionException">The plug-in failed (also when it could not be loaded or created).</exception>
    internal Task RunAsyncStepAsync(PluginStep step, StepContext context, SdkMirror mirror, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(session);
        return ExecuteStepAsync(step, context, new Caller(context.UserId, context.BusinessUnitId), session, mirror, new ExecutionState(), ct);
    }

    /// <summary>
    /// The per-step core shared by synchronous and asynchronous steps: impersonation, the plug-in's services, the run,
    /// the trace (per the organization setting) and the failure. A step with an impersonating user runs as that user (its
    /// <c>UserId</c> and business unit), whose security then applies to the plug-in's calls; no plug-in runs when that user
    /// does not exist (404) or is disabled (403 <c>0x80040225</c>). The plug-in gets the view <see cref="PluginContextAdapter.Lend"/>
    /// gives of <paramref name="context"/>, read back into the model (<see cref="PluginContextAdapter.Return"/>) once its nested calls
    /// are drained, whether it succeeded or not.
    /// </summary>
    /// <exception cref="PluginExecutionException">The plug-in failed (also when it could not be loaded or created).</exception>
    /// <exception cref="XrmdFaultException">The impersonating user does not exist or is disabled.</exception>
    private async Task ExecuteStepAsync(PluginStep step, StepContext context, Caller caller, DataSession session, SdkMirror mirror,
        ExecutionState execution, CancellationToken ct)
    {
        // A layer operation (plan 2.6, ruling R2) calls the core operations outside the pipeline: no step may run inside one,
        // or the plug-in's own requests would inherit its bypass of the managed rules.
        Debug.Assert(!LayerOperationScope.IsActive, "A plug-in step runs inside a solution layer operation.");
        if (step.ImpersonatingUserId is { } impersonated)
        {
            caller = await core.ResolveUserAsync(impersonated, session, ct);
            context.UserId = caller.UserId;
            context.BusinessUnitId = caller.BusinessUnitId;
        }

        if (step.Endpoint is { } endpoint)
        {
            await SendWebhookAsync(endpoint, context, mirror, ct);
            return;
        }

        // Read before the first plug-in runs (a failing plug-in may doom the transaction), once per execution.
        var setting = execution.TraceSetting ??= await core.PluginTraceLogSettingAsync(session, ct);
        var tracing = new TracingService();
        // The plug-in's organization service works only while this execution runs: revoked when the plug-in returns,
        // times out (its thread may live on) or the request is cancelled, before the transaction is rolled back.
        using var revocation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var factory = new PluginOrganizationServiceFactory(this, core, context, mirror, session, revocation.Token);
        var view = PluginContextAdapter.Lend(context, mirror, HeldLevels(mirror));
        var services = new PluginServiceProvider(view, tracing, factory, new PluginLogger(tracing, _logger));

        var start = DateTime.UtcNow;
        var watch = Stopwatch.StartNew();
        PluginExecutionException? failure = null;
        try
        {
            var plugin = await InstantiateAsync(step, ct);
            await runner.RunAsync(plugin, services, step.TypeName, ct);
        }
        catch (PluginExecutionException ex)
        {
            failure = ex;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The plug-in could not be loaded or created (bad assembly, missing type or dependency, no constructor...).
            failure = LoadFailure(step, ex);
        }
        finally
        {
            await revocation.CancelAsync();
            if (!await factory.RevokeAsync(RevocationDrainTimeout))
                _logger.LogWarning("A nested call of step {StepId} ({StepName}) was still running {Seconds} s after its plug-in ended.",
                    step.Id, step.Name, RevocationDrainTimeout.TotalSeconds);
            PluginContextAdapter.Return(view, mirror);
        }

        watch.Stop();
        if (setting == TraceAll || (failure is not null && (setting == TraceException || failure.Unexpected)))
        {
            try
            {
                await traces.WriteAsync(step, context, tracing.Text, failure is null ? null : failure.Detail ?? failure.Message,
                    start, (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds), ct);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                _logger.LogError(ex, "Could not write the plug-in trace of step {StepId} ({StepName}).", step.Id, step.Name);
            }
        }

        if (failure is not null) throw failure;
    }

    /// <summary>
    /// A plug-in that could not be loaded or created: unexpected 500 <c>0x80040224</c> with a message of its own (no
    /// internal type names), traced; the trace's details are the loader's message, or the full text of any other exception.
    /// </summary>
    private static PluginExecutionException LoadFailure(PluginStep step, Exception ex)
    {
        var message = $"The plug-in type '{step.TypeName}' could not be loaded: {ex.Message}";
        return new PluginExecutionException(new XrmdFaultException(ErrorCodes.UnexpectedPluginException, message, 500), true,
            ex is XrmdFaultException ? message : ex.ToString());
    }
}
