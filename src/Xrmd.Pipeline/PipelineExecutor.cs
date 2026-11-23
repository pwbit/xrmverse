using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.Mail;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Bulk;
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
/// call <paramref name="Depth"/> (1 for a front-end call), the context of the plug-in that issued it (nested calls) and the
/// <paramref name="Session"/> to reuse (nested calls; null at the first level, where the executor opens a connection).
/// </summary>
public sealed record InvocationContext(Caller Caller, Guid InitiatingUserId, int Depth, XrmdPluginExecutionContext? Parent,
    DataSession? Session, Guid CorrelationId)
{
    /// <summary>
    /// True for a write the core makes as the cascade of another (a request marked with <see cref="CascadeHint"/>, such as the child
    /// Delete of a Cascade delete relationship). Its plug-in steps run as for any nested request, but it triggers no workflow (plan
    /// 2.14, ruling W5): no real-time run and no background job. Not inherited: the requests of its plug-ins and of a workflow are
    /// new invocations.
    /// </summary>
    public bool IsCoreCascade { get; init; }
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
public sealed class PipelineExecutor(IPipelineCore core, StepRegistry registry, PluginAssemblyLoader loader, PluginRunner runner,
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
    /// Runs <paramref name="request"/> through the pipeline and returns the response, whose results are the final
    /// <c>OutputParameters</c> (a post-operation plug-in may change them; Retrieve/RetrieveMultiple results appear to plug-ins
    /// as <c>BusinessEntity</c>/<c>BusinessEntityCollection</c> and are mapped back to <c>Entity</c>/<c>EntityCollection</c>). The first level (no
    /// <see cref="InvocationContext.Session"/>) opens a connection; a nested call reuses the session and its transaction.
    /// </summary>
    /// <remarks>
    /// <c>SetState</c> becomes an <see cref="UpdateRequest"/> of <c>statecode</c>/<c>statuscode</c> run as the message
    /// <c>Update</c> (its steps fire), answered with a <see cref="SetStateResponse"/>. A message the core does not implement
    /// runs as a custom action when it is a custom <c>sdkmessage</c> row: its steps fire around a no-op stage 30 and the
    /// response is an <see cref="OrganizationResponse"/> named after the message whose results are the
    /// <c>OutputParameters</c>. A custom API is looked up first: its request must match its definition (400 <c>0x80040203</c>),
    /// the caller must hold its <c>executeprivilegename</c> (403 <c>0x80040220</c>), its plug-in type runs at stage 30, and its
    /// response holds exactly its declared properties (unset ones at their type's default; an undeclared key is dropped and
    /// logged; a value of the wrong type is 500 <c>0x80040224</c>).
    /// </remarks>
    /// <exception cref="XrmdFaultException">The plug-in's fault (400 abort, 500 unexpected), the core operation's fault, the
    /// <see cref="SecurityGuard"/>'s 403/404, 400 <c>0x80044182</c> when <see cref="InvocationContext.Depth"/> exceeds
    /// <see cref="MaxDepth"/>, or 400 <c>0x80040203</c> for a bulk message issued from a plug-in (#76, ruling B12).</exception>
    /// <exception cref="NotSupportedException">The message is neither a core message nor a custom <c>sdkmessage</c>, or it is an
    /// activity or note message xrmverse refuses (plan 2.11, A18), whatever <c>sdkmessage</c> rows the organization holds.</exception>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, InvocationContext invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invocation);
        // Migration spec §7.5: the writes of a migration call the core directly; no step, workflow or webhook may run inside one.
        if (MigrationScope.IsActive) throw new InvalidOperationException("The pipeline cannot run inside a migration scope.");
        if (invocation.Depth > MaxDepth)
            throw new XrmdFaultException(ErrorCodes.InfiniteLoopDetected,
                "This workflow job was canceled because the workflow that started it included an infinite loop. Correct the workflow logic and try again.",
                400);

        // #76, ruling B12: no bulk message from a plug-in, whatever the table type.
        if (invocation.Parent is not null && BulkTargets.MessageOf(request.RequestName) is { } bulkMessage)
            throw XrmdFaultException.InvalidArgument(BulkStage.FromPluginMessage(bulkMessage));

        if (string.Equals(request.RequestName, "SetState", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAsync(SetStateAsUpdate(request), invocation, ct);
            return new SetStateResponse { ResponseName = "SetState" };
        }

        // Plan 2.11 (A18): the activity and note messages xrmverse refuses, with their reason, before any custom message.
        if (ActivitySchema.IsRefusedMessage(request.RequestName))
            throw new NotSupportedException(ActivitySchema.RefusedMessageText(request.RequestName));

        // A bulk message on a standard table (#76) has no core operation but is no custom action: BulkStage runs its stage 30.
        var customAction = !core.IsCoreMessage(request.RequestName) && !BulkStage.Handles(request.RequestName);
        // A custom API first (plan 15): its message is also an sdkmessage row, but it runs with its definition.
        var api = customAction ? await customApis.FindAsync(request.RequestName, ct) : null;
        // Then a XAML action (plan 2.14, W14): its message is an sdkmessage row too, but its process runs at stage 30.
        var action = customAction && api is null ? await _workflows.FindActionAsync(request.RequestName, ct) : null;
        if (customAction && api is null && action is null && !await registry.IsCustomMessageAsync(request.RequestName, ct))
            throw new NotSupportedException($"Request '{request.RequestName}' is not supported yet.");

        if (invocation.Session is { } session) return await RunAsync(request, invocation, session, customAction, api, action, ct);
        await using var conn = await factory.OpenAsync(ct);
        return await RunAsync(request, invocation, new DataSession(conn, null), customAction, api, action, ct);
    }

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

    /// <summary>
    /// The <see cref="UpdateRequest"/> of <c>EntityMoniker</c> setting <c>statecode = State</c> and <c>statuscode = Status</c>
    /// (no statuscode when <c>Status</c> is -1: the state's default status).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when a parameter is missing.</exception>
    private static UpdateRequest SetStateAsUpdate(OrganizationRequest request)
    {
        T Parameter<T>(string name) where T : class =>
            request.Parameters.TryGetValue(name, out var value) && value is T typed
                ? typed
                : throw XrmdFaultException.InvalidArgument(
                    $"Required parameter '{name}' of request '{request.RequestName}' is missing or not a {typeof(T).Name}.");

        var moniker = Parameter<EntityReference>("EntityMoniker");
        var target = new Entity(moniker.LogicalName, moniker.Id) { ["statecode"] = Parameter<OptionSetValue>("State") };
        var status = Parameter<OptionSetValue>("Status");
        // Status -1 means "the state's default status": leaving statuscode out lets the Update apply that default.
        if (status.Value != -1) target["statuscode"] = status;
        var update = new UpdateRequest { RequestId = request.RequestId, Target = target };
        // Audited as Set State, not Update; the mark lives beside the request, so plug-ins see the same InputParameters (ruling A6).
        AuditActionHint.Mark(update, AuditActions.SetState);
        return update;
    }

    /// <summary>
    /// Runs the stages of one message. For a custom API (<paramref name="api"/> set), before anything of the message runs:
    /// the request is checked against the definition (<see cref="CustomApiValidator.Validate"/>), then the caller must hold the
    /// API's execute privilege; stage 30 runs its plug-in type (<see cref="RunMainOperationAsync"/>), and the outputs are brought
    /// to the declared response properties after stage 30 and again after the synchronous PostOperation steps.
    /// </summary>
    /// <remarks>
    /// For a XAML action (<paramref name="action"/> set, plan 2.14, W14) the request is checked against its arguments with the same
    /// rules before stage 10 (400 <c>0x80040203</c>), and stage 30 runs its process (<see cref="IWorkflowPipelineHook.RunActionAsync"/>),
    /// whose output arguments are the response. <c>ExecuteWorkflow</c>'s stage 30 is <see cref="IWorkflowPipelineHook.ExecuteWorkflowAsync"/> (W21).
    /// </remarks>
    private async Task<OrganizationResponse> RunAsync(OrganizationRequest request, InvocationContext invocation, DataSession session,
        bool customAction, CustomApiDefinition? api, ActionDefinition? action, CancellationToken ct)
    {
        // Before anything of the message runs (stage 10 included), with the parameters as the caller sent them.
        if (!customAction) await guard.AuthorizeAsync(request, invocation.Caller, session, ct);
        if (api is not null)
        {
            CustomApiValidator.Validate(api, request);
            if (api.ExecutePrivilegeName is { } privilege)
                await guard.DemandPrivilegeAsync(invocation.Caller, privilege, api.BoundEntity ?? NoEntity, ct);
        }
        if (action is not null)
            CustomApiValidator.ValidateRequest(action.BoundEntity, action.Inputs, request,
                problem => XrmdFaultException.InvalidArgument($"Request to action '{action.Message}' is invalid: {problem}"));

        var message = request.RequestName;
        var (entityName, entityId) = api is not null ? CustomApiPrimaryEntity(api, request)
            : action is not null ? ActionPrimaryEntity(action, request)
            : PrimaryEntity(request);
        var context = await NewContextAsync(request, invocation, session, entityName, entityId, ct);
        // #76, ruling B10: Upsert is not an event on a standard table; its stage 30 runs an inner Create or Update.
        var messageTable = !customAction && entityName is not null ? metadata.GetEntity(session, entityName) : null;
        var upsertTable = StandardUpsert.Applies(message, messageTable) ? messageTable : null;
        // #76, rulings B1 and B4: a bulk message on a standard table; its stage 30 runs each target as an inner execution.
        var bulkTable = BulkStage.Applies(message, messageTable) ? messageTable : null;
        if (bulkTable is null && BulkStage.Handles(message))
            throw new InvalidOperationException($"{message} on '{entityName}' is not a standard-table bulk request: the organization service routes it.");
        // #76, ruling B5: a Create/Update on a standard table also runs the steps of its bulk message (MergedSteps).
        var standardTable = messageTable is not null && !ElasticTables.IsElastic(messageTable);
        var execution = new ExecutionState();
        var mapsOutput = ReadMessages.Contains(message);
        // The response keys a custom API does not declare, removed after stage 30 and after stage 40: logged once.
        var dropped = new SortedSet<string>(StringComparer.Ordinal);

        try
        {
            using var stage = PipelineTelemetry.Stage("prevalidation", message, entityName);
            await RunStageAsync(await StepsAsync(request, entityName, PreValidation, Synchronous, upsertTable is null, standardTable, ct),
                PreValidation, context, request, invocation, session, StepImages.None, execution, ct);
        }
        catch (PluginExecutionException ex)
        {
            throw ex.Fault;
        }

        var pre = await StepsAsync(request, entityName, PreOperation, Synchronous, upsertTable is null, standardTable, ct);
        var post = await StepsAsync(request, entityName, PostOperation, Synchronous, upsertTable is null, standardTable, ct);
        var postAsync = await StepsAsync(request, entityName, PostOperation, Asynchronous, upsertTable is null, standardTable, ct);

        var owned = session.Transaction is null
                    && (!ReadMessages.Contains(message) || pre.Count > 0 || post.Count > 0 || postAsync.Count > 0);
        if (owned) session.Transaction = await session.Connection.BeginTransactionAsync(ct);
        var state = session.Transaction is null ? null : _states.GetValue(session, static _ => new TransactionState());
        // A first-level write on an elastic table commits after stages 20, 30 and 40 (ruling E3); nested, it joins (E4).
        var perStage = ElasticStages.CommitPerStage(message,
            owned && entityName is not null ? metadata.GetEntity(session, entityName) : null, owned);

        OrganizationResponse response;
        // The message of a custom API being deleted, removed after the core Delete (CustomApiAuthoring).
        Guid? apiMessage = null;
        // The endpoint whose authvalue stage 20 took (plan 3.2): its isauthvalueset is written after the core operation.
        Guid? webhookSecret = null;
        try
        {
            Entity? preImage = null;
            if (PreImageMessages.Contains(message) && entityName is not null)
                preImage = await ReadImageAsync(entityName, entityId, [pre, post, postAsync], pre: true, session, ct);
            // A bulk execution (#76, ruling B6): one pre image per target, read before stage 20 (none is registered on CreateMultiple).
            var preImages = bulkTable is not null
                ? await ReadImagesAsync(bulkTable.LogicalName, BulkTargets.TargetIds(request, bulkTable, null), [pre, post, postAsync], pre: true, session, ct)
                : null;
            // Plan 2.14 (W5): what the real-time workflows of stages 20 and 40 are matched on, read before the operation.
            var trigger = customAction || invocation.IsCoreCascade
                ? null
                : await WorkflowTriggerAsync(request, entityName, entityId, invocation.Caller, session, ct);

            using (PipelineTelemetry.Stage("preoperation", message, entityName))
            {
                // Plan 2.21 (A16): a public write of a process is checked before the pre-operation plug-ins see it, and again after.
                if (!customAction) await ValidateWorkflowWriteAsync(request, invocation.Caller, entityName, session, ct);
                await RunStageAsync(pre, await RealtimeWorkflowsAsync(trigger, entityName, PreOperation, ct), PreOperation, context, request,
                    invocation, session, new StepImages(preImage, null, preImages, null), execution, ct);
                if (!customAction)
                {
                    apiMessage = await AuthorCustomApiAsync(request, invocation.Caller, entityName, entityId, session, ct);
                    await ValidateRegistrationAsync(request, entityName, entityId, session, ct);
                    await CheckWorkflowWriteAsync(request, invocation.Caller, entityName, entityId, session, ct);
                    webhookSecret = await AuthorServiceEndpointAsync(request, entityName, entityId, session, ct);
                    // Plan 4.1b: a mail secret leaves the Target (or the Targets of a bulk message) for good here, whatever the message that
                    // writes it: the stage-40, asynchronous and webhook contexts, and the ParentContext of an inner Create, Update or Upsert,
                    // never hold it (an Upsert's inner Create or Update gets the value through PendingMailSecrets.Carry).
                    if (entityName is not null && metadata.GetEntity(session, entityName) is { } mailTable)
                        PendingMailSecrets.StripRequest(request, mailTable);
                }
            }
            if (perStage) state = await CommitStageAsync(session, state!, ct);

            using (PipelineTelemetry.Stage("mainoperation", message, entityName))
            {
                if (api is not null) await RunMainOperationAsync(api, context, request, invocation, session, execution, ct);
                if (action is not null) await _workflows.RunActionAsync(this, action, context, invocation, session, ct);
                response = customAction
                    ? new OrganizationResponse { ResponseName = message } // a custom action has no core operation
                    : string.Equals(message, ExecuteWorkflowMessage, StringComparison.OrdinalIgnoreCase)
                    ? new ExecuteWorkflowResponse
                    {
                        ResponseName = ExecuteWorkflowMessage,
                        Results = { ["Id"] = await _workflows.ExecuteWorkflowAsync(this, request, context, invocation, session, ct) },
                    }
                    : bulkTable is not null
                    ? await BulkStage.ExecuteAsync(request, bulkTable, inner => RunInnerAsync(inner, InnerKind.Bulk, invocation, context, session, ct), ct)
                    : upsertTable is not null
                    ? await StandardUpsert.ExecuteAsync(await CreateOnlyGuardedAsync(request.Parameters.TryGetValue(Target, out var t) && t is Entity upsertTarget ? upsertTarget
                            : throw XrmdFaultException.InvalidArgument($"Required parameter '{Target}' of request '{message}' is missing or not a {nameof(Entity)}."),
                            StandardUpsert.IsCreateOnly(request), invocation, session, ct),
                        upsertTable, core, session,
                        inner => RunInnerAsync(StandardUpsert.CarryParameters(request, inner), InnerKind.Upsert, invocation, context, session, ct),
                        StandardUpsert.IsCreateOnly(request), ct)
                    // A request the core issues (the record Update of CommitFileBlocksUpload) runs as a plug-in-issued request
                    // would: one level deeper, with this message's context as its ParentContext.
                    : await core.ExecuteCoreAsync(request, invocation.Caller, session,
                        (nested, c) => ExecuteAsync(nested,
                            invocation with
                            {
                                Session = session, Parent = context, Depth = invocation.Depth + 1, IsCoreCascade = CascadeHint.IsMarked(nested),
                            }, c), ct);
                // Plan 2.14 (final review I2): the activation of an assigned definition follows its owner, in the same transaction;
                // so does it after an Update of ownerid, which plan 2.21 (A16) accepts on an Activated definition.
                if (!customAction && entityName == WorkflowSchema.Table && entityId != Guid.Empty
                    && (string.Equals(message, "Assign", StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(message, "Update", StringComparison.OrdinalIgnoreCase)
                            && request.Parameters.TryGetValue(Target, out var written) && written is Entity ownerWrite && ownerWrite.Contains("ownerid"))))
                    await _workflows.OnAssignedAsync(session, entityId, ct);
                if (apiMessage is { } deletedApiMessage) await customApiAuthoring.AfterDeleteAsync(deletedApiMessage, session, ct);
                if (webhookSecret is { } endpoint) await webhookSecrets.MarkSetAsync(endpoint, session, ct);
            }
            foreach (var (key, value) in response.Results)
                context.OutputParameters[mapsOutput ? PluginOutputKeys.GetValueOrDefault(key, key) : key] = value;
            if (api is not null) dropped.UnionWith(CustomApiValidator.Complete(api, context.OutputParameters));
            if (string.Equals(message, "Create", StringComparison.OrdinalIgnoreCase) && response.Results.TryGetValue("id", out var id)
                && id is Guid created)
                context.PrimaryEntityId = entityId = created;
            if (upsertTable is not null && response.Results.TryGetValue(Target, out var upserted) && upserted is EntityReference upsertedRef)
                context.PrimaryEntityId = entityId = upsertedRef.Id;
            if (!ReadMessages.Contains(message) && state is not null)
            {
                if (entityName is not null) state.WrittenEntities.Add(entityName);
                if (!customAction) state.WrittenEntities.UnionWith(core.WrittenEntities(request));
            }
            if (perStage) state = await CommitStageAsync(session, state!, ct);

            Entity? postImage = null;
            if (PostImageMessages.Contains(message) && entityName is not null)
                postImage = await ReadImageAsync(entityName, entityId, [post, postAsync], pre: false, session, ct);
            // A bulk execution: one post image per target, read after stage 30 (a CreateMultiple's records by its Ids).
            var postImages = bulkTable is not null
                ? await ReadImagesAsync(bulkTable.LogicalName, BulkTargets.TargetIds(request, bulkTable, response), [post, postAsync], pre: false, session, ct)
                : null;
            var images = new StepImages(preImage, postImage, preImages, postImages);

            using (PipelineTelemetry.Stage("postoperation", message, entityName))
            {
                await RunStageAsync(post, await RealtimeWorkflowsAsync(trigger, entityName, PostOperation, ct), PostOperation, context,
                    request, invocation, session, images, execution, ct);
                if (api is not null)
                {
                    dropped.UnionWith(CustomApiValidator.Complete(api, context.OutputParameters));
                    if (dropped.Count > 0) CustomApiValidator.LogDropped(_logger, api, dropped);
                }

                // Plan 2.14 (W9): the background workflows the write triggers, queued in its transaction.
                if (trigger is not null)
                    await _workflows.QueueBackgroundAsync(trigger.Message, entityName, trigger.Columns, trigger.Owner, context, invocation,
                        session, ct);

                foreach (var entry in postAsync)
                {
                    var queued = ContextFor(entry, context, request).Clone(PostOperation);
                    queued.Mode = Asynchronous;
                    queued.OwningExtension = StepReference(entry.Step);
                    AddImages(queued, entry, images, post: true);
                    await asyncQueue.EnqueueAsync(session, entry.Step, queued, ct);
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

        response.Results.Clear();
        foreach (var (key, value) in context.OutputParameters)
            response.Results[mapsOutput ? ResponseKeys.GetValueOrDefault(key, key) : key] = value;
        return response;
    }

    /// <summary>
    /// The custom API rules of stage 20 (<see cref="CustomApiAuthoring"/>, plan 3.1), for every caller but
    /// <see cref="Caller.Internal"/>: before a Create, Update or Delete of the custom API tables. Returns the message of a
    /// custom API being deleted, which <see cref="CustomApiAuthoring.AfterDeleteAsync"/> removes after the core Delete; null
    /// otherwise. An Update of a missing record is left to the core operation's 404.
    /// </summary>
    private async Task<Guid?> AuthorCustomApiAsync(OrganizationRequest request, Caller caller, string? entityName, Guid entityId,
        DataSession session, CancellationToken ct)
    {
        if (caller == Caller.Internal || entityName is null || !CustomApiTables.All.Contains(entityName)
            || !request.Parameters.TryGetValue(Target, out var target)) return null;
        switch (request.RequestName?.ToLowerInvariant())
        {
            case "create" when target is Entity created:
                await customApiAuthoring.BeforeCreateAsync(request, created, session, ct);
                return null;
            case "update" when target is Entity updated && entityId != Guid.Empty
                               && await core.RetrieveImageAsync(new EntityReference(entityName, entityId), null, session, ct) is { } existing:
                await customApiAuthoring.BeforeUpdateAsync(updated, existing, session, ct);
                return null;
            case "delete" when target is EntityReference deleted:
                return await customApiAuthoring.BeforeDeleteAsync(deleted, session, ct);
            default:
                return null;
        }
    }

    /// <summary>
    /// The internal check of stage 20: a Create/Update of a registration entity goes through the
    /// <see cref="RegistrationValidator"/> (an Update on the stored record overlaid with the target; a missing record is
    /// left to the core operation's 404); an Associate or Disassociate over <c>eventhandler</c> is refused.
    /// </summary>
    private async Task ValidateRegistrationAsync(OrganizationRequest request, string? entityName, Guid entityId, DataSession session,
        CancellationToken ct)
    {
        RegistrationValidator.DemandNotEventHandlerAssociate(request);
        if (entityName is null || !StepRegistry.RegistrationEntities.Contains(entityName)
            || !request.Parameters.TryGetValue(Target, out var t) || t is not Entity target) return;
        if (string.Equals(request.RequestName, "Create", StringComparison.OrdinalIgnoreCase))
        {
            await validator.ValidateAsync(entityName, target, null, session, ct);
        }
        else if (string.Equals(request.RequestName, "Update", StringComparison.OrdinalIgnoreCase) && entityId != Guid.Empty
                 && await core.RetrieveImageAsync(new EntityReference(entityName, entityId), null, session, ct) is { } existing)
        {
            await validator.ValidateAsync(entityName, target, existing, session, ct);
        }
    }

    /// <summary>
    /// The checks of a public Create or Update of <c>workflow</c> or <c>processtrigger</c> (plan 2.21, ruling A16) on the request's
    /// Target as it is now: <see cref="SystemTableRules.DemandValidProcessWrite"/>, then <see cref="IWorkflowPipelineHook.ValidateWriteAsync"/>
    /// (the stored row, the XAML). A Create of <c>workflow</c> without a <c>type</c> is a definition: the Target gets <c>type</c> 1 (Dataverse's
    /// default from memory, checklist 1a), so the registry and the activation, which read definitions by their type, find it. Returns the message (Create
    /// or Update) of a <c>workflow</c> write, else null. <see cref="Caller.Internal"/> is exempt from every rule.
    /// </summary>
    private async Task<string?> ValidateWorkflowWriteAsync(OrganizationRequest request, Caller caller, string? entityName, DataSession session,
        CancellationToken ct)
    {
        if (entityName is not (WorkflowSchema.Table or WorkflowSchema.TriggerTable)
            || !request.Parameters.TryGetValue(Target, out var t) || t is not Entity target) return null;
        var message = request.RequestName?.ToLowerInvariant() switch { "create" => "Create", "update" => "Update", _ => null };
        if (message is null) return null;
        SystemTableRules.DemandValidProcessWrite(message, target, caller);
        if (entityName != WorkflowSchema.Table) return null;
        if (caller != Caller.Internal && message == "Create" && !target.Contains(WorkflowSchema.TypeColumn))
            target[WorkflowSchema.TypeColumn] = new OptionSetValue((int)WorkflowSchema.Type.Definition);
        await _workflows.ValidateWriteAsync(session, caller, message, target, ct);
        return message;
    }

    /// <summary>
    /// The workflow part of the internal check of stage 20, after the pre-operation plug-ins: a public write of a process is checked
    /// again (<see cref="ValidateWorkflowWriteAsync"/>: a plug-in may have widened the Target), then an Update of <c>workflow</c>
    /// (SetState arrives as one) whose <c>statecode</c> differs from the stored one activates or deactivates the definition through
    /// the <see cref="IWorkflowPipelineHook"/> (plan 2.14, ruling W4), in the Update's transaction. A missing record is left to the
    /// core operation's 404.
    /// </summary>
    private async Task CheckWorkflowWriteAsync(OrganizationRequest request, Caller caller, string? entityName, Guid entityId,
        DataSession session, CancellationToken ct)
    {
        if (await ValidateWorkflowWriteAsync(request, caller, entityName, session, ct) != "Update" || entityId == Guid.Empty) return;
        var target = (Entity)request.Parameters[Target];
        if (!target.Contains("statecode") && !target.Contains("statuscode")) return;
        if (await core.RetrieveImageAsync(new EntityReference(entityName!, entityId), ["statecode", WorkflowSchema.TypeColumn], session, ct)
            is not { } stored) return;
        // Only a definition changes state: a state or status Update of an activation (or any other type) is 400, even when it asks
        // for the state the row already has (final review M5).
        if (stored.GetAttributeValue<OptionSetValue>(WorkflowSchema.TypeColumn)?.Value is { } type and not (int)WorkflowSchema.Type.Definition)
            throw XrmdFaultException.InvalidArgument(
                $"The process {entityId} is not a definition (type {type}): only a definition (type 1) is activated or deactivated.");
        if (target.GetAttributeValue<OptionSetValue>("statecode") is not { } state
            || stored.GetAttributeValue<OptionSetValue>("statecode")?.Value == state.Value) return;
        await _workflows.OnSetStateAsync(session, caller, entityId, state.Value, ct);
    }

    /// <summary>
    /// The internal check of stage 20 for <c>serviceendpoint</c> (plan 3.2), for every caller: a Create or Update must give an
    /// accepted webhook as the row will be (<see cref="ServiceEndpointRules.ValidateAsync"/>), then its <c>authvalue</c> is taken
    /// out of the target into the secret table (<see cref="WebhookSecrets.TakeAsync"/>); a Delete removes the secret (its steps
    /// and their images go through the Delete Cascade relationship). Returns the endpoint whose value was taken, or null. An
    /// Update of a missing record is left to the core operation's 404.
    /// </summary>
    private async Task<Guid?> AuthorServiceEndpointAsync(OrganizationRequest request, string? entityName, Guid entityId, DataSession session,
        CancellationToken ct)
    {
        if (entityName != ServiceEndpointSchema.Table || !request.Parameters.TryGetValue(Target, out var target)) return null;
        switch (request.RequestName?.ToLowerInvariant())
        {
            case "create" when target is Entity created:
                await ServiceEndpointRules.ValidateAsync(created, null, Options.WebhookAllowAnyPort, webhookSecrets, session, ct);
                return await webhookSecrets.TakeAsync(created, session, ct);
            case "update" when target is Entity updated && entityId != Guid.Empty
                               && await core.RetrieveImageAsync(new EntityReference(entityName, entityId), null, session, ct) is { } existing:
                await ServiceEndpointRules.ValidateAsync(updated, existing, Options.WebhookAllowAnyPort, webhookSecrets, session, ct);
                return await webhookSecrets.TakeAsync(updated, session, ct);
            case "delete" when target is EntityReference deleted:
                await webhookSecrets.DeleteAsync(deleted.Id, session, ct);
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// A create-only Upsert (Web API <c>If-None-Match: *</c>) may answer 412 for an existing row, so the caller first passes the
    /// guard of the Create it would otherwise run: one without the right gets the same 403 whether the row exists or not.
    /// Returns <paramref name="target"/>.
    /// </summary>
    private async Task<Entity> CreateOnlyGuardedAsync(Entity target, bool createOnly, InvocationContext invocation, DataSession session,
        CancellationToken ct)
    {
        if (createOnly) await guard.AuthorizeAsync(new CreateRequest { Target = target }, invocation.Caller, session, ct);
        return target;
    }

    /// <summary>
    /// Runs <paramref name="inner"/> as an inner execution of the message whose context is <paramref name="outerContext"/> (#76,
    /// ruling B4): marked with <paramref name="kind"/> (<see cref="InnerExecution"/>), in <paramref name="session"/> and its transaction (which it never commits
    /// or rolls back: the outer execution owns it), at the <b>same</b> depth (unlike a plug-in's call), with the outer context as
    /// its <c>ParentContext</c> (a copy that reports the outer's current stage, <see cref="MainOperation"/>: inner runs only happen
    /// there). Its failure propagates to the outer execution, which rolls the whole transaction back.
    /// </summary>
    private Task<OrganizationResponse> RunInnerAsync(OrganizationRequest inner, InnerKind kind, InvocationContext outer,
        XrmdPluginExecutionContext outerContext, DataSession session, CancellationToken ct)
    {
        InnerExecution.Mark(inner, kind);
        return ExecuteAsync(inner, outer with { Session = session, Parent = outerContext.Clone(MainOperation) }, ct);
    }

    /// <summary>
    /// The steps a stage runs (<see cref="MergedSteps.ForStageAsync"/>: the message's own steps whose filtering attributes match,
    /// merged with the bulk message's on a standard table); none when <paramref name="isEvent"/> is false (Upsert on a standard table).
    /// </summary>
    private async Task<IReadOnlyList<StageStep>> StepsAsync(OrganizationRequest request, string? entityName, int stage, int mode,
        bool isEvent, bool standardTable, CancellationToken ct) =>
        isEvent ? await MergedSteps.ForStageAsync(registry, request, entityName, stage, mode, standardTable, ct) : [];

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

    private async Task<XrmdPluginExecutionContext> NewContextAsync(OrganizationRequest request, InvocationContext invocation,
        DataSession session, string? entityName, Guid entityId, CancellationToken ct)
    {
        _organization ??= await core.OrganizationAsync(session, ct);
        var now = DateTime.UtcNow;
        return new XrmdPluginExecutionContext
        {
            MessageName = request.RequestName,
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
    }

    /// <summary>
    /// The main operation of a custom API (plan 15, C7): its plug-in type, when it has one, run once as a synthetic step of
    /// stage 30, synchronous, with no configuration, no images and no impersonation, inside the transaction and with the
    /// <c>InputParameters</c> the pre-stages saw. Its failure follows the plug-in rules (400 for an
    /// <see cref="InvalidPluginExecutionException"/>, 500 otherwise).
    /// </summary>
    /// <exception cref="PluginExecutionException">The plug-in failed (also when it could not be loaded or created).</exception>
    private Task RunMainOperationAsync(CustomApiDefinition api, XrmdPluginExecutionContext context, OrganizationRequest request,
        InvocationContext invocation, DataSession session, ExecutionState execution, CancellationToken ct)
    {
        if (api.MainOperation is not { } main) return Task.CompletedTask;
        // No step row exists for the main operation: its id is empty, so its traces and OwningExtension name no step.
        var step = new PluginStep(Guid.Empty, api.UniqueName, api.UniqueName, api.BoundEntity, MainOperation, Synchronous, 0, null, null, null, null,
            main.PluginTypeId, main.TypeName, main.AssemblyId, main.AssemblyVersion, []);
        return RunStepAsync(new StageStep(step, false), MainOperation, context, request, invocation, session, StepImages.None, execution, ct);
    }

    /// <summary>
    /// The primary entity of a custom API call (plan 15, C3): the bound table (none for a global API), and the id of
    /// <c>Target</c> for an entity-bound API (empty otherwise).
    /// </summary>
    private static (string? Name, Guid Id) CustomApiPrimaryEntity(CustomApiDefinition api, OrganizationRequest request) =>
        (api.BindingType == 0 ? null : api.BoundEntity,
            api.BindingType == 1 && request.Parameters.TryGetValue(Target, out var t) && t is EntityReference r ? r.Id : Guid.Empty);

    /// <summary>
    /// The primary entity of a XAML action call (plan 2.14, W14): the bound table (none for a global action), and the id of
    /// <c>Target</c> for a bound action (empty otherwise).
    /// </summary>
    private static (string? Name, Guid Id) ActionPrimaryEntity(ActionDefinition action, OrganizationRequest request) =>
        (action.BoundEntity,
            action.BoundEntity is not null && request.Parameters.TryGetValue(Target, out var t) && t is EntityReference r ? r.Id : Guid.Empty);

    /// <summary>The message that runs a workflow on demand (plan 2.14, W21): a core message whose stage 30 is the workflows' hook.</summary>
    public const string ExecuteWorkflowMessage = "ExecuteWorkflow";

    /// <summary>The primary entity of a message: from <c>Targets</c> (a bulk message), <c>Target</c> (entity or reference), <c>EntityName</c> (RetrieveEntityChanges),
    /// <c>EmailId</c> (SendEmail), <c>TransactionCurrencyId</c> (RetrieveExchangeRate), <c>QueueItemId</c> (RouteTo, RemoveFromQueue,
    /// PickFromQueue, ReleaseToQueue), <c>QueueId</c> (AddPrincipalToQueue), <c>UserId</c> (RetrieveUserQueues) or <c>Query</c>.</summary>
    private static (string? Name, Guid Id) PrimaryEntity(OrganizationRequest request)
    {
        var parameters = request.Parameters;
        // A bulk message (#76): the table of Targets, no id.
        if (BulkTargets.MessageOf(request.RequestName) is not null) return (BulkTargets.TableNameOf(request), Guid.Empty);
        // Plan 4.1a: RouteTo and RemoveFromQueue act on a queue item (RouteTo's Target is where it goes), AddPrincipalToQueue on a queue.
        if (string.Equals(request.RequestName, QueueSchema.RouteTo, StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.RequestName, QueueSchema.RemoveFromQueue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.RequestName, QueueSchema.PickFromQueue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.RequestName, QueueSchema.ReleaseToQueue, StringComparison.OrdinalIgnoreCase))
            return (QueueSchema.QueueItem, parameters.TryGetValue("QueueItemId", out var item) && item is Guid itemId ? itemId : Guid.Empty);
        if (string.Equals(request.RequestName, QueueSchema.AddPrincipalToQueue, StringComparison.OrdinalIgnoreCase))
            return (QueueSchema.Queue, parameters.TryGetValue("QueueId", out var queue) && queue is Guid queueId ? queueId : Guid.Empty);
        // RetrieveUserQueues: the user whose queues are read (the Web API binds it to systemuser; from memory for the plug-in context).
        if (string.Equals(request.RequestName, QueueSchema.RetrieveUserQueues, StringComparison.OrdinalIgnoreCase))
            return ("systemuser", parameters.TryGetValue("UserId", out var user) && user is Guid userId ? userId : Guid.Empty);
        if (parameters.TryGetValue(Target, out var target))
        {
            switch (target)
            {
                case Entity e:
                    var id = e.Id != Guid.Empty ? e.Id
                        : e.LogicalName is not null && e.Attributes.TryGetValue(e.LogicalName + "id", out var pk) && pk is Guid g ? g
                        : Guid.Empty;
                    return (e.LogicalName, id);
                case EntityReference r:
                    return (r.LogicalName, r.Id);
            }
        }

        // RetrieveEntityChanges (plan 2.13): the tracked table.
        if (string.Equals(request.RequestName, "RetrieveEntityChanges", StringComparison.OrdinalIgnoreCase)
            && parameters.TryGetValue("EntityName", out var tracked) && tracked is string trackedName)
            return (trackedName, Guid.Empty);

        // ExecuteCosmosSqlQuery (plan #78, ruling J9): the queried table.
        if (string.Equals(request.RequestName, "ExecuteCosmosSqlQuery", StringComparison.OrdinalIgnoreCase)
            && parameters.TryGetValue("EntityLogicalName", out var cosmosTable) && cosmosTable is string cosmosTableName)
            return (cosmosTableName.ToLowerInvariant(), Guid.Empty); // logical names are lower case: a plug-in sees the canonical one

        // SendEmail (plan 2.11, A17): the email.
        if (string.Equals(request.RequestName, ActivitySchema.SendEmailMessage, StringComparison.OrdinalIgnoreCase))
            return (ActivitySchema.Email, parameters.TryGetValue("EmailId", out var email) && email is Guid emailId ? emailId : Guid.Empty);

        // RetrieveExchangeRate (plan 2.10): the currency.
        if (string.Equals(request.RequestName, "RetrieveExchangeRate", StringComparison.OrdinalIgnoreCase)
            && parameters.TryGetValue("TransactionCurrencyId", out var currency) && currency is Guid currencyId)
            return ("transactioncurrency", currencyId);

        if (parameters.TryGetValue("Query", out var query))
        {
            switch (query)
            {
                case QueryExpression q: return (q.EntityName, Guid.Empty);
                case QueryByAttribute q: return (q.EntityName, Guid.Empty);
                case FetchExpression f: return (FetchEntityName(f.Query), Guid.Empty);
            }
        }

        return (null, Guid.Empty);
    }

    private static string? FetchEntityName(string? fetchXml)
    {
        if (string.IsNullOrWhiteSpace(fetchXml)) return null;
        try
        {
            return XDocument.Parse(fetchXml).Root?.Element("entity")?.Attribute("name")?.Value;
        }
        catch (System.Xml.XmlException)
        {
            return null; // the core operation reports the invalid query
        }
    }

    /// <summary>
    /// Reads the record once with the union of the attributes the steps' pre (type 0/2) or post (type 1/2) images ask
    /// for; null when no step wants such an image or the record does not exist.
    /// </summary>
    private async Task<Entity?> ReadImageAsync(string entityName, Guid id, IReadOnlyList<StageStep>[] stages, bool pre,
        DataSession session, CancellationToken ct)
    {
        var images = stages.SelectMany(s => s).SelectMany(s => s.Step.Images).Where(i => Wants(i, pre)).ToList();
        if (images.Count == 0 || id == Guid.Empty) return null;
        var attributes = images.Any(i => i.Attributes is null)
            ? null
            : images.SelectMany(i => i.Attributes!).Distinct(StringComparer.Ordinal).ToArray();
        return await core.RetrieveImageAsync(new EntityReference(entityName, id), attributes, session, ct);
    }

    /// <summary>
    /// The image of each record of <paramref name="ids"/>, in order, for the image collections of a bulk execution (#76, ruling
    /// B6): one read per record (<see cref="ReadImageAsync"/>; none when no step wants such an image).
    /// </summary>
    private async Task<Entity?[]> ReadImagesAsync(string entityName, Guid[] ids, IReadOnlyList<StageStep>[] stages, bool pre,
        DataSession session, CancellationToken ct)
    {
        var images = new Entity?[ids.Length];
        for (var i = 0; i < ids.Length; i++) images[i] = await ReadImageAsync(entityName, ids[i], stages, pre, session, ct);
        return images;
    }

    private static bool Wants(PluginStepImage image, bool pre) => image.ImageType == 2 || image.ImageType == (pre ? 0 : 1);

    /// <summary>
    /// The images of an execution: the single record's (<paramref name="Pre"/>, <paramref name="Post"/>), or, for a bulk
    /// execution, one per target (<paramref name="PreList"/>, <paramref name="PostList"/>, index-aligned with <c>Targets</c>).
    /// </summary>
    private sealed record StepImages(Entity? Pre, Entity? Post, Entity?[]? PreList, Entity?[]? PostList)
    {
        public static readonly StepImages None = new(null, null, null, null);
    }

    /// <summary>
    /// Gives <paramref name="context"/> the images of <paramref name="entry"/>'s step (post images only when <paramref name="post"/>).
    /// A bulk execution's step and a step merged into a single-record execution (#76, ruling B6) get them only in
    /// <c>PreEntityImagesCollection</c>/<c>PostEntityImagesCollection</c>, one collection per target (one for a merged step);
    /// any other step in <c>PreEntityImages</c>/<c>PostEntityImages</c>.
    /// </summary>
    private static void AddImages(XrmdPluginExecutionContext context, StageStep entry, StepImages images, bool post)
    {
        var step = entry.Step;
        var postImage = post ? images.Post : null;
        if (images.PreList is not null || entry.MultipleView)
        {
            context.PreEntityImagesCollection = [.. (images.PreList ?? [images.Pre]).Select(e => ImageCollection(step, e, pre: true))];
            if (post)
                context.PostEntityImagesCollection = [.. (images.PostList ?? [postImage]).Select(e => ImageCollection(step, e, pre: false))];
            return;
        }
        foreach (var image in step.Images)
        {
            if (images.Pre is not null && Wants(image, pre: true)) context.PreEntityImages[image.Alias] = Project(images.Pre, image.Attributes);
            if (postImage is not null && Wants(image, pre: false)) context.PostEntityImages[image.Alias] = Project(postImage, image.Attributes);
        }
    }

    /// <summary>The pre or post images <paramref name="step"/> asks for, taken from <paramref name="record"/> (empty when null).</summary>
    private static EntityImageCollection ImageCollection(PluginStep step, Entity? record, bool pre)
    {
        var collection = new EntityImageCollection();
        if (record is null) return collection;
        foreach (var image in step.Images)
            if (Wants(image, pre)) collection[image.Alias] = Project(record, image.Attributes);
        return collection;
    }

    /// <summary>The context <paramref name="entry"/>'s step sees: <see cref="MergedSteps.MultipleView"/> for a merged step.</summary>
    private static XrmdPluginExecutionContext ContextFor(StageStep entry, XrmdPluginExecutionContext context, OrganizationRequest request) =>
        entry.MultipleView ? MergedSteps.MultipleView(context, request) : context;

    /// <summary>A copy of <paramref name="source"/> with only <paramref name="attributes"/> (null: all of them).</summary>
    private static Entity Project(Entity source, string[]? attributes)
    {
        var copy = new Entity(source.LogicalName, source.Id);
        foreach (var (name, value) in source.Attributes)
            if (attributes is null || attributes.Contains(name, StringComparer.Ordinal)) copy[name] = value;
        foreach (var (name, value) in source.FormattedValues)
            if (copy.Attributes.ContainsKey(name)) copy.FormattedValues[name] = value;
        return copy;
    }

    private static EntityReference StepReference(PluginStep step) =>
        new("sdkmessageprocessingstep", step.Id) { Name = step.Name };

    private async Task RunStageAsync(IReadOnlyList<StageStep> steps, int stage, XrmdPluginExecutionContext context,
        OrganizationRequest request, InvocationContext invocation, DataSession session, StepImages images, ExecutionState execution,
        CancellationToken ct)
    {
        foreach (var step in steps)
            await RunStepAsync(step, stage, context, request, invocation, session, images, execution, ct);
    }

    /// <summary>
    /// Runs a stage of synchronous plug-in steps and real-time workflows as one list (plan 2.14, ruling W6): ordered by rank, then
    /// plug-ins before workflows at equal rank, then id.
    /// </summary>
    private async Task RunStageAsync(IReadOnlyList<StageStep> steps, IReadOnlyList<RealtimeWorkflow> workflows, int stage,
        XrmdPluginExecutionContext context, OrganizationRequest request, InvocationContext invocation, DataSession session, StepImages images,
        ExecutionState execution, CancellationToken ct)
    {
        if (workflows.Count == 0)
        {
            await RunStageAsync(steps, stage, context, request, invocation, session, images, execution, ct);
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
                await RunStepAsync(step, stage, context, request, invocation, session, images, execution, ct);
                continue;
            }
            var workflowContext = context.Clone(stage);
            workflowContext.Mode = Synchronous;
            workflowContext.IsInTransaction = session.Transaction is not null;
            workflowContext.OwningExtension = new EntityReference(WorkflowSchema.Table, item.Workflow!.ActivationId) { Name = item.Workflow.Name };
            await _workflows.RunRealtimeAsync(this, item.Workflow, workflowContext, invocation, session, ct);
        }
    }

    /// <summary>What a write is matched against the real-time workflows on (plan 2.14, ruling W5).</summary>
    /// <param name="Message">Create, Update, Assign, SetState or Delete.</param>
    /// <param name="Columns">The columns of the <c>Target</c> as the request sent them.</param>
    /// <param name="Owner">The record's owner before the operation; null for a table without owner.</param>
    private sealed record WorkflowTrigger(string Message, IReadOnlyCollection<string> Columns, RecordOwner? Owner);

    /// <summary>The messages real-time workflows run on; SetState arrives as an Update (<see cref="SetStateAsUpdate"/>).</summary>
    private static readonly HashSet<string> WorkflowMessages = new(StringComparer.OrdinalIgnoreCase) { "Create", "Update", "Assign", "Delete" };

    /// <summary>
    /// The <see cref="WorkflowTrigger"/> of a write, or null when no activated real-time workflow, server-side business rule or
    /// background workflow has the table as its primary entity: then nothing is read (<see cref="IWorkflowPipelineHook.HasRealtimeAsync"/>
    /// and <see cref="IWorkflowPipelineHook.HasBackgroundAsync"/> answer from memory).
    /// </summary>
    private async Task<WorkflowTrigger?> WorkflowTriggerAsync(OrganizationRequest request, string? entityName, Guid entityId, Caller caller,
        DataSession session, CancellationToken ct)
    {
        if (entityName is null || !WorkflowMessages.TryGetValue(request.RequestName, out var message)
            || !(await _workflows.HasRealtimeAsync(entityName, ct) || await _workflows.HasBackgroundAsync(entityName, ct))) return null;
        var target = request.Parameters.TryGetValue(Target, out var t) ? t as Entity : null;
        // W5: a SetState triggers as SetState (its columns are statecode and statuscode), not as an Update of any column.
        if (message == "Update" && AuditActionHint.Of(request) == AuditActions.SetState) message = "SetState";
        IReadOnlyCollection<string> columns = target is null ? [] : target.Attributes.Keys.ToList();
        return new WorkflowTrigger(message, columns, await RecordOwnerAsync(message, entityName, entityId, target, caller, session, ct));
    }

    /// <summary>
    /// The owner of the record and its business unit, as the scope of a workflow compares them (W5): on a Create the <c>ownerid</c>
    /// the request sets (else the caller), otherwise the stored owner. Null for a table without owner or a missing record (the core
    /// operation reports it).
    /// </summary>
    private async Task<RecordOwner?> RecordOwnerAsync(string message, string entityName, Guid entityId, Entity? target, Caller caller,
        DataSession session, CancellationToken ct)
    {
        var entity = metadata.GetEntity(session, entityName);
        if (entity?.OwnershipType is not { } ownership || (ownership & (OwnershipTypes.UserOwned | OwnershipTypes.TeamOwned)) == 0) return null;
        if (message == "Create")
        {
            var owner = target?.GetAttributeValue<EntityReference>("ownerid") ?? new EntityReference("systemuser", caller.UserId);
            if (target?.GetAttributeValue<EntityReference>("owningbusinessunit") is { } unit) return new RecordOwner(owner.Id, unit.Id);
            if (owner.Id == caller.UserId) return new RecordOwner(owner.Id, caller.BusinessUnitId);
            var principal = await core.RetrieveImageAsync(owner, ["businessunitid"], session, ct);
            return new RecordOwner(owner.Id, principal?.GetAttributeValue<EntityReference>("businessunitid")?.Id ?? Guid.Empty);
        }
        // No owner can be read (no id, or the record does not exist): the matcher then applies no scope. That is safe: a write of a
        // missing record fails in the core operation (404) and rolls back what a stage 20 workflow did before it; a Create has no id
        // here but takes the branch above.
        if (entityId == Guid.Empty
            || await core.RetrieveImageAsync(new EntityReference(entityName, entityId), ["ownerid", "owningbusinessunit"], session, ct) is not { } stored
            || stored.GetAttributeValue<EntityReference>("ownerid") is not { } storedOwner) return null;
        return new RecordOwner(storedOwner.Id, stored.GetAttributeValue<EntityReference>("owningbusinessunit")?.Id ?? Guid.Empty);
    }

    /// <summary>The real-time workflows <paramref name="trigger"/> runs at <paramref name="stage"/> (none without a trigger).</summary>
    private async Task<IReadOnlyList<RealtimeWorkflow>> RealtimeWorkflowsAsync(WorkflowTrigger? trigger, string? entityName, int stage,
        CancellationToken ct) =>
        trigger is null ? [] : await _workflows.GetRealtimeAsync(trigger.Message, entityName, stage, trigger.Columns, trigger.Owner, ct);

    /// <summary>Runs one synchronous step, writes its trace per the organization setting and rethrows its failure.</summary>
    /// <exception cref="PluginExecutionException">The plug-in failed (also when it could not be loaded or created).</exception>
    private async Task RunStepAsync(StageStep entry, int stage, XrmdPluginExecutionContext baseContext, OrganizationRequest request,
        InvocationContext invocation, DataSession session, StepImages images, ExecutionState execution, CancellationToken ct)
    {
        var context = ContextFor(entry, baseContext, request).Clone(stage);
        context.Mode = Synchronous;
        context.IsInTransaction = session.Transaction is not null;
        context.OwningExtension = StepReference(entry.Step);
        if (stage != PreValidation) AddImages(context, entry, images, post: stage == PostOperation);
        await ExecuteStepAsync(entry.Step, context, invocation.Caller, session, execution, ct);
    }

    /// <summary>
    /// Runs one asynchronous step for the async job worker: <paramref name="context"/> is the job's context, already
    /// rebuilt (Mode 1, Stage 40, <c>IsInTransaction</c> false, its images). <paramref name="session"/> has no transaction,
    /// so every call of the plug-in's organization service runs in a transaction of its own. The step's impersonating
    /// user, the trace setting and the error mapping apply as for a synchronous step.
    /// </summary>
    /// <exception cref="PluginExecutionException">The plug-in failed (also when it could not be loaded or created).</exception>
    public Task RunAsyncStepAsync(PluginStep step, XrmdPluginExecutionContext context, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        return ExecuteStepAsync(step, context, new Caller(context.UserId, context.BusinessUnitId), session, new ExecutionState(), ct);
    }

    /// <summary>
    /// The per-step core shared by synchronous and asynchronous steps: impersonation, the plug-in's services, the run,
    /// the trace (per the organization setting) and the failure. A step with an impersonating user runs as that user (its
    /// <c>UserId</c> and business unit), whose security then applies to the plug-in's calls; no plug-in runs when that user
    /// does not exist (404) or is disabled (403 <c>0x80040225</c>).
    /// </summary>
    /// <exception cref="PluginExecutionException">The plug-in failed (also when it could not be loaded or created).</exception>
    /// <exception cref="XrmdFaultException">The impersonating user does not exist or is disabled.</exception>
    private async Task ExecuteStepAsync(PluginStep step, XrmdPluginExecutionContext context, Caller caller, DataSession session,
        ExecutionState execution, CancellationToken ct)
    {
        // A layer operation (plan 2.6, ruling R2) calls the core operations outside the pipeline: no step may run inside one,
        // or the plug-in's own requests would inherit its bypass of the managed rules.
        System.Diagnostics.Debug.Assert(!LayerOperationScope.IsActive, "A plug-in step runs inside a solution layer operation.");
        if (step.ImpersonatingUserId is { } impersonated)
        {
            caller = await core.ResolveUserAsync(impersonated, session, ct);
            context.UserId = caller.UserId;
            context.BusinessUnitId = caller.BusinessUnitId;
        }

        if (step.Endpoint is { } endpoint)
        {
            await SendWebhookAsync(endpoint, context, ct);
            return;
        }

        // Read before the first plug-in runs (a failing plug-in may doom the transaction), once per execution.
        var setting = execution.TraceSetting ??= await core.PluginTraceLogSettingAsync(session, ct);
        var tracing = new TracingService();
        // The plug-in's organization service works only while this execution runs: revoked when the plug-in returns,
        // times out (its thread may live on) or the request is cancelled, before the transaction is rolled back.
        using var revocation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var factory = new PluginOrganizationServiceFactory(this, core, context, session, revocation.Token);
        var services = new PluginServiceProvider(context, tracing, factory, new PluginLogger(tracing, _logger));

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
    /// Posts <paramref name="context"/> to the webhook of a step on a <c>serviceendpoint</c> (plan 3.2). No trace is written (Dataverse
    /// writes none for a webhook). A context that cannot be serialized (<see cref="SerializationException"/> or
    /// <see cref="InvalidDataContractException"/>) fails as a final 500 <c>0x80040224</c> whose message names
    /// the endpoint only (never the value that could not be serialized).
    /// </summary>
    /// <exception cref="PluginExecutionException">The delivery failed (<see cref="WebhookSender.SendAsync"/>).</exception>
    private async Task SendWebhookAsync(WebhookEndpoint endpoint, XrmdPluginExecutionContext context, CancellationToken ct)
    {
        try
        {
            await webhookSender.SendAsync(endpoint, AsyncContextSerializer.ToRemote(context), context.OrganizationName, ct);
        }
        catch (Exception ex) when (ex is SerializationException or InvalidDataContractException)
        {
            _logger.LogWarning("The context for webhook {Endpoint} could not be serialized: {Error}.", endpoint.Name, ex.GetType().Name);
            throw new PluginExecutionException(new XrmdFaultException(ErrorCodes.UnexpectedPluginException,
                $"Endpoint unavailable: webhook '{endpoint.Name}' could not be sent the context: it holds a value that cannot be serialized.",
                500), unexpected: false);
        }
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

    private async Task<IPlugin> InstantiateAsync(PluginStep step, CancellationToken ct)
    {
        // The content is read only when this version is not loaded yet, and never inside the loader's lock.
        var type = loader.TryResolvePluginType(step.AssemblyId, step.AssemblyVersion, null, step.TypeName)
                   ?? loader.TryResolvePluginType(step.AssemblyId, step.AssemblyVersion,
                       await registry.GetAssemblyContentAsync(step.AssemblyId, ct), step.TypeName)!;
        return runner.CreateInstance(type, step.UnsecureConfiguration, step.SecureConfiguration);
    }
}
