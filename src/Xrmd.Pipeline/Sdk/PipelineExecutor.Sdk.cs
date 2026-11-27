using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Audit;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Model.Serialization;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Bulk;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Mail;
using Xrmd.Pipeline.Merge;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Pipeline.Webhooks;
using Xrmd.Pipeline.Workflows;
using Xrmd.Security;

namespace Xrmd.Pipeline;

/// <summary>
/// The SDK side of the <see cref="PipelineExecutor"/> (S.2e2c, Decision 4): the public entry on an <see cref="OrganizationRequest"/>,
/// which crosses the request into the model through the chain's <see cref="SdkMirror"/> and back; the checks that work on SDK values
/// (the guard, the custom API and action validation, the primary entity); the boundary of stages 20 and 30, where the core, the internal
/// checks and the hooks work on the SDK request (<see cref="HoldAtCoreAsync"/> writes the model into it once, <see cref="ReleaseCore"/>
/// reads it back once); the images the core reads; the response.
/// </summary>
public sealed partial class PipelineExecutor
{
    /// <summary>
    /// The <c>InputParameters</c> of the executions whose SDK request the core holds (between <see cref="HoldAtCoreAsync"/> and
    /// <see cref="ReleaseCore"/>), with that request's parameters: a level of a nested chain lent meanwhile is first read from it
    /// (<see cref="HeldLevels"/>, <see cref="SyncBoundaries"/>), so the view never writes stale model values over what the core did.
    /// </summary>
    private static readonly ConditionalWeakTable<ParameterBag, ParameterCollection> AtCore = new();

    /// <summary>One execution of a message: its SDK request and model twin, who runs it, what it runs on, and its model context.</summary>
    private sealed class Run
    {
        public required OrganizationRequest Sdk { get; init; }
        public required Request Request { get; init; }
        public required InvocationContext Invocation { get; init; }
        public required DataSession Session { get; init; }
        public required SdkMirror Mirror { get; init; }
        public required StepContext Context { get; init; }
        public required string Message { get; init; }
        public required string? EntityName { get; init; }
        public required Guid EntityId { get; set; }
        public required bool CustomAction { get; init; }
        public CustomApiDefinition? Api { get; init; }
        public ActionDefinition? Action { get; init; }

        /// <summary>The table of an Upsert on a standard table (#76, ruling B10): no event, its stage 30 runs an inner Create or Update.</summary>
        public EntityMetadata? UpsertTable { get; init; }

        /// <summary>The table of a bulk message on a standard table (#76, rulings B1 and B4).</summary>
        public EntityMetadata? BulkTable { get; init; }

        public bool StandardTable { get; init; }
        public bool BulkInner { get; init; }
        public bool MapsOutput { get; init; }
        public bool IsEvent => UpsertTable is null;

        /// <summary>True when something reads the response after stage 30 (Decision 3).</summary>
        public bool Observed { get; set; }

        public OrganizationResponse? Response { get; set; }

        /// <summary>The model twin of <see cref="Response"/>; null on the fast path (the response is never converted).</summary>
        public Response? ModelResponse { get; set; }

        /// <summary>
        /// S.2e3b (Decision 8): true for a CRUD message the core answers on the model request (<see cref="CoreHandOff.IsModelMessage"/>):
        /// no window is held at the core for it.
        /// </summary>
        public bool CoreOnModel => !CustomAction && UpsertTable is null && BulkTable is null && CoreHandOff.IsModelMessage(Message);

        /// <summary>The model response the core answered with (<see cref="CoreOperationAsync"/>); null for any other stage 30.</summary>
        public Response? CoreResponse { get; set; }

        public bool HeldAtCore { get; set; }

        /// <summary>True when this execution registered its bag in <see cref="AtCore"/> (not one sharing its caller's held bag).</summary>
        public bool OwnsAtCore { get; set; }

        /// <summary>True when this execution opened the <see cref="CoreHandOff"/> window of its model request.</summary>
        public bool OwnsHeld { get; set; }
        public Guid? ApiMessage { get; set; }
        public Guid? WebhookSecret { get; set; }
    }

    /// <summary>
    /// Runs <paramref name="request"/> through the pipeline and returns the response, whose results are the final
    /// <c>OutputParameters</c> (a post-operation plug-in may change them; Retrieve/RetrieveMultiple results appear to plug-ins
    /// as <c>BusinessEntity</c>/<c>BusinessEntityCollection</c> and are mapped back to <c>Entity</c>/<c>EntityCollection</c>). The first level (no
    /// <see cref="InvocationContext.Session"/>) opens a connection; a nested call reuses the session and its transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SetState</c> becomes an <see cref="UpdateRequest"/> of <c>statecode</c>/<c>statuscode</c> run as the message
    /// <c>Update</c> (its steps fire), answered with a <see cref="SetStateResponse"/>. A message the core does not implement
    /// runs as a custom action when it is a custom <c>sdkmessage</c> row: its steps fire around a no-op stage 30 and the
    /// response is an <see cref="OrganizationResponse"/> named after the message whose results are the
    /// <c>OutputParameters</c>. A custom API is looked up first: its request must match its definition (400 <c>0x80040203</c>),
    /// the caller must hold its <c>executeprivilegename</c> (403 <c>0x80040220</c>), its plug-in type runs at stage 30, and its
    /// response holds exactly its declared properties (unset ones at their type's default; an undeclared key is dropped and
    /// logged; a value of the wrong type is 500 <c>0x80040224</c>).
    /// </para>
    /// <para>
    /// The request crosses into the model through the chain's <see cref="InvocationContext.Mirror"/> (created here at the first SDK
    /// entry): its <c>Parameters</c> are the <c>InputParameters</c> every step's view lends, and the request object itself is what the
    /// core runs. A call made by a running plug-in or workflow first reads its view back into the model, and writes the model into it
    /// again when it ends. The response is the core's own object; when nothing reads it after stage 30 it is returned unconverted.
    /// </para>
    /// </remarks>
    /// <exception cref="XrmdFaultException">The plug-in's fault (400 abort, 500 unexpected), the core operation's fault, the
    /// <see cref="SecurityGuard"/>'s 403/404, 400 <c>0x80044182</c> when <see cref="InvocationContext.Depth"/> exceeds
    /// <see cref="MaxDepth"/>, or 400 <c>0x80040203</c> for a bulk message issued from a plug-in (#76, ruling B12).</exception>
    /// <exception cref="NotSupportedException">The message is neither a core message nor a custom <c>sdkmessage</c>, or it is an
    /// activity or note message xrmverse refuses (plan 2.11, A18), whatever <c>sdkmessage</c> rows the organization holds.</exception>
    /// <exception cref="InvalidOperationException"><see cref="InvocationContext.Parent"/> is set without
    /// <see cref="InvocationContext.Mirror"/> (a server bug), server values on a message other than Create, or a migration scope is
    /// active.</exception>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, InvocationContext invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invocation);
        // Plan 4.1c: server values are written by a Create only (a server bug otherwise, never a caller's fault).
        if (invocation.ServerSetValues is not null && !string.Equals(request.RequestName, "Create", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Server values are written on a Create only, not on '{request.RequestName}'.");
        // Migration spec §7.5: the writes of a migration call the core directly; no step, workflow or webhook may run inside one.
        if (MigrationScope.IsActive) throw new InvalidOperationException("The pipeline cannot run inside a migration scope.");
        // A nested call shares its chain's mirror: with a new one the caller's view would be neither read back nor refreshed.
        if (invocation.Parent is not null && invocation.Mirror is null)
            throw new InvalidOperationException("A call with a parent context needs the mirror of its execution chain (InvocationContext.Mirror).");
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

        var mirror = invocation.Mirror;
        if (mirror is null)
        {
            mirror = new SdkMirror();
            invocation = invocation with { Mirror = mirror };
        }
        // The calling plug-in's (or workflow's) view holds what it did since it was lent: the model takes it before anything runs.
        var parent = invocation.Parent is { } p && PluginContextAdapter.IsRunning(p, mirror) ? p : null;
        if (parent is not null) PluginContextAdapter.Sync(parent, mirror);
        try
        {
            if (invocation.Session is { } session) return await RunAsync(request, invocation, session, customAction, api, action, ct);
            await using var conn = await factory.OpenAsync(ct);
            return await RunAsync(request, invocation, new DataSession(conn, null), customAction, api, action, ct);
        }
        finally
        {
            if (parent is not null)
            {
                PluginContextAdapter.Refresh(parent, mirror, HeldLevels(mirror));
            }
        }
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
    private async Task<OrganizationResponse> RunAsync(OrganizationRequest request, InvocationContext invocation,
        DataSession session, bool customAction, CustomApiDefinition? api, ActionDefinition? action, CancellationToken ct)
    {
        // Plan 4.1c: a plug-in or workflow that changed a reserved shared variable makes no request with it changed (its own step fails
        // after it returns; a request it makes before that, outside a transaction at stage 10, would otherwise be written).
        if (invocation.Parent is { } parent)
            ReservedSharedVariables.DemandKept(parent, $"The plug-in or workflow that issued this '{request.RequestName}' request");
        // S.2e3c2a (Decision 5): the request crosses into the model before the guard, which runs on the model request the core gets
        // (a record team mark it leaves there is found by the core; a cascade mark the core left on a request it issues is read there).
        // From here to the boundary, only the guard (written out below), the views of the steps and hooks (each read back) and the
        // process-write check (read back after it) change the SDK request.
        var mirror = invocation.Mirror!;
        var model = mirror.In(request)!;
        // S.2e3b (Decision 6): the audit action is a mark on the SDK request; the core on the model reads it on the model request.
        if (AuditActionHint.Of(request) is { } auditAction) AuditActionHint.Mark(model, auditAction);
        // S.2e3a: the model request carries its chain mirror, so the core converts with it (twins and typed responses keep their identity).
        SdkMirror.Register(model, mirror);
        // The request's own collection is the SDK side of its parameters: every view's InputParameters is that collection. A request
        // built on a collection that already has a twin (a plug-in executing a request on its own InputParameters) shares that bag, as
        // the SDK pipeline shares the collection: pairing it again would take the collection from the caller's level.
        if (mirror.TryGetModel(request.Parameters, out var twin) && twin is ParameterBag shared)
        {
            if (!ReferenceEquals(model.Parameters, shared))
            {
                model.Parameters = shared;
                mirror.In(request.Parameters);
            }
        }
        else if (!mirror.TryGetSdk(model.Parameters, out _)) mirror.Pair(model.Parameters, request.Parameters);

        // Before anything of the message runs (stage 10 included), with the parameters as the caller sent them. What the guard writes
        // (the Target of a field share Create gets its objectid, objecttypecode and access flags) is written out into the SDK request
        // the steps and the caller see, on a refusal after the fill too (ruling 4a).
        if (!customAction) await guard.AuthorizeAsync(model, mirror, invocation.Caller, session, ct);
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
        var context = await NewContextAsync(model, invocation, session, entityName, entityId, ct);
        // #76, ruling B10: Upsert is not an event on a standard table; its stage 30 runs an inner Create or Update.
        var messageTable = !customAction && entityName is not null ? metadata.GetEntity(session, entityName) : null;
        var upsertTable = StandardUpsert.Applies(message, messageTable) ? messageTable : null;
        // #76, rulings B1 and B4: a bulk message on a standard table; its stage 30 runs each target as an inner execution.
        var bulkTable = BulkStage.Applies(message, messageTable) ? messageTable : null;
        if (bulkTable is null && BulkStage.Handles(message))
            throw new InvalidOperationException($"{message} on '{entityName}' is not a standard-table bulk request: the organization service routes it.");

        var run = new Run
        {
            Sdk = request,
            Request = model,
            Invocation = invocation,
            Session = session,
            Mirror = invocation.Mirror!,
            Context = context,
            Message = message,
            EntityName = entityName,
            EntityId = entityId,
            CustomAction = customAction,
            Api = api,
            Action = action,
            UpsertTable = upsertTable,
            BulkTable = bulkTable,
            // #76, ruling B5: a Create/Update on a standard table also runs the steps of its bulk message (MergedSteps).
            StandardTable = messageTable is not null && !ElasticTables.IsElastic(messageTable),
            BulkInner = InnerExecution.KindOf(request) == InnerKind.Bulk,
            MapsOutput = ReadMessages.Contains(message),
        };
        await RunStagesAsync(run, ct);
        return FinishResponse(run);
    }

    /// <summary>True when a first-level write on an elastic table commits after each of stages 20, 30 and 40 (ruling E3).</summary>
    private bool CommitsPerStage(Run run, bool owned) =>
        ElasticStages.CommitPerStage(run.Message, owned && run.EntityName is not null ? metadata.GetEntity(run.Session, run.EntityName) : null, owned);

    // ---- the stage-20/30 boundary -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The check of a public write of a process before the pre-operation plug-ins see it (plan 2.21, A16), on the SDK request (which
    /// the views of stage 10 left current); what it set in the Target is read back into the model.
    /// </summary>
    private async Task ValidateProcessWriteAsync(Run run, CancellationToken ct)
    {
        if (await ValidateWorkflowWriteAsync(run.Sdk, run.Invocation.Caller, run.EntityName, run.Session, ct) is not null)
            run.Mirror.In(run.Sdk);
    }

    /// <summary>
    /// The start of the boundary (Decision 4), after the pre-operation steps: the model request is written into the SDK request once,
    /// which the core holds from here, then the internal checks of stage 20 run on it (custom API authoring, registration, process
    /// writes, service endpoints, the mail secrets).
    /// </summary>
    /// <remarks>
    /// S.2e3b (Decision 8): a CRUD message (<see cref="Run.CoreOnModel"/>) runs on the model request, so no window is held for it. The SDK
    /// request is current here (every view's <c>InputParameters</c> is its collection, read back after each step), so the checks run on
    /// it as it is. A check that writes the request (the tables of <see cref="HoldChecksMayWrite"/>) gets it written out first and read
    /// back after, as today; for any other table the request does not cross here.
    /// </remarks>
    private async Task HoldAtCoreAsync(Run run, CancellationToken ct)
    {
        var onModel = run.CoreOnModel;
        var crosses = !onModel || HoldChecksMayWrite(run.EntityName);
        if (crosses) run.Mirror.Out(run.Request);
        if (!onModel)
        {
            // A nested request on the caller's own collection shares the caller's bag: only the execution that registered it removes it.
            run.HeldAtCore = true;
            run.OwnsAtCore = !AtCore.TryGetValue(run.Request.Parameters, out _);
            if (run.OwnsAtCore) AtCore.Add(run.Request.Parameters, run.Sdk.Parameters);
            run.OwnsHeld = CoreHandOff.Hold(run.Request, run.Sdk);
        }
        if (run.CustomAction) return;
        var request = run.Sdk;
        var caller = run.Invocation.Caller;
        run.ApiMessage = await AuthorCustomApiAsync(request, caller, run.EntityName, run.EntityId, run.Session, ct);
        await ValidateRegistrationAsync(request, run.EntityName, run.EntityId, run.Session, ct);
        await CheckWorkflowWriteAsync(request, caller, run.EntityName, run.EntityId, run.Session, ct);
        run.WebhookSecret = await AuthorServiceEndpointAsync(request, run.EntityName, run.EntityId, run.Session, ct);
        // Plan 4.1b: a mail secret leaves the Target (or the Targets of a bulk message) for good here, whatever the message that
        // writes it: the stage-40, asynchronous and webhook contexts, and the ParentContext of an inner Create, Update or Upsert,
        // never hold it (an Upsert's inner Create or Update gets the value through PendingMailSecrets.Carry).
        if (run.EntityName is not null && metadata.GetEntity(run.Session, run.EntityName) is { } mailTable)
            PendingMailSecrets.StripRequest(request, mailTable);
        if (onModel && crosses) run.Mirror.In(run.Sdk);
    }

    /// <summary>
    /// True for a table whose internal checks of stage 20 may write the request (<see cref="HoldAtCoreAsync"/>): the custom API tables
    /// (<see cref="AuthorCustomApiAsync"/>), the registration entities (<see cref="ValidateRegistrationAsync"/>), <c>workflow</c> and
    /// <c>processtrigger</c> (<see cref="CheckWorkflowWriteAsync"/>), <c>serviceendpoint</c> (<see cref="AuthorServiceEndpointAsync"/>)
    /// and a table with mail secret columns (<see cref="PendingMailSecrets.StripRequest"/>). The other checks only read the request.
    /// </summary>
    private static bool HoldChecksMayWrite(string? entityName) =>
        entityName is not null
        && (CustomApiTables.All.Contains(entityName) || StepRegistry.RegistrationEntities.Contains(entityName)
            || entityName is WorkflowSchema.Table or WorkflowSchema.TriggerTable or ServiceEndpointSchema.Table
            || MailSchema.SecretColumns.Keys.Any(k => k.StartsWith(entityName + ".", StringComparison.Ordinal)));

    /// <summary>
    /// The end of the boundary, after stage 30 and its follow-ups (also when they failed): the model reads the SDK request back once.
    /// </summary>
    private void ReleaseCore(Run run)
    {
        if (!run.HeldAtCore) return;
        run.HeldAtCore = false;
        if (run.OwnsAtCore) AtCore.Remove(run.Request.Parameters);
        CoreHandOff.Release(run.Request, run.OwnsHeld);
        run.Mirror.In(run.Sdk);
    }

    /// <summary>
    /// Before a context is serialized whole (the asynchronous queue and a webhook write every level, from the model or from the view the
    /// mirror lends): each level of its chain whose request the core holds is first read from the SDK request, so the model, and the write
    /// of the view (an <c>Out</c>), carry what the core did.
    /// </summary>
    private static void SyncBoundaries(StepContext context, SdkMirror mirror)
    {
        for (var level = context; level is not null; level = level.ParentContext)
            if (AtCore.TryGetValue(level.InputParameters, out var held)) mirror.In(held);
    }

    /// <summary>
    /// The <c>beforeLevel</c> of a lend (<see cref="PluginContextAdapter.Lend"/>, <see cref="PluginContextAdapter.Refresh"/>): a level
    /// whose SDK request the core holds is read from it right before the level is written into its view. The adapter lends a parent
    /// level only when the plug-in reads it, so a step that never reads its parent converts nothing of the held request (a bulk
    /// message's inner steps stay linear in the targets).
    /// </summary>
    private static Action<StepContext> HeldLevels(SdkMirror mirror) => level =>
    {
        if (AtCore.TryGetValue(level.InputParameters, out var held)) mirror.In(held);
    };

    /// <summary>
    /// Stage 30: the custom API's plug-in type, the XAML action, <c>ExecuteWorkflow</c>, the bulk or Upsert inner executions or the
    /// core operation, on the SDK request; then the follow-ups of the same transaction.
    /// </summary>
    private async Task MainOperationAsync(Run run, ExecutionState execution, CancellationToken ct)
    {
        var request = run.Sdk;
        var invocation = run.Invocation;
        var session = run.Session;
        var context = run.Context;
        var message = run.Message;
        if (run.Api is { } api) await RunMainOperationAsync(api, run, execution, ct);
        if (run.Action is { } action)
            await LentAsync(context.Clone(context.Stage), run.Mirror,
                view => _workflows.RunActionAsync(this, action, view, WithoutServerValues(invocation), session, ct));
        run.Response = run.CustomAction
            ? new OrganizationResponse { ResponseName = message } // a custom action has no core operation
            : string.Equals(message, ExecuteWorkflowMessage, StringComparison.OrdinalIgnoreCase)
            ? new ExecuteWorkflowResponse
            {
                ResponseName = ExecuteWorkflowMessage,
                Results =
                {
                    ["Id"] = await LentAsync(context.Clone(context.Stage), run.Mirror,
                        view => _workflows.ExecuteWorkflowAsync(this, request, view, WithoutServerValues(invocation), session, ct)),
                },
            }
            : run.BulkTable is { } bulkTable
            ? await BulkStage.ExecuteAsync(request, bulkTable, inner => RunInnerAsync(inner, InnerKind.Bulk, run, ct), ct)
            : run.UpsertTable is { } upsertTable
            ? await StandardUpsert.ExecuteAsync(await CreateOnlyGuardedAsync(request.Parameters.TryGetValue(Target, out var t) && t is Entity upsertTarget ? upsertTarget
                    : throw XrmdFaultException.InvalidArgument($"Required parameter '{Target}' of request '{message}' is missing or not a {nameof(Entity)}."),
                    StandardUpsert.IsCreateOnly(request), invocation, session, ct),
                upsertTable, core, session,
                inner => RunInnerAsync(StandardUpsert.CarryParameters(request, inner), InnerKind.Upsert, run, ct),
                StandardUpsert.IsCreateOnly(request), ct)
            : await CoreOperationAsync(run, ct);
        // Plan 2.14 (final review I2): the activation of an assigned definition follows its owner, in the same transaction;
        // so does it after an Update of ownerid, which plan 2.21 (A16) accepts on an Activated definition.
        if (!run.CustomAction && run.EntityName == WorkflowSchema.Table && run.EntityId != Guid.Empty
            && (string.Equals(message, "Assign", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(message, "Update", StringComparison.OrdinalIgnoreCase)
                    && request.Parameters.TryGetValue(Target, out var written) && written is Entity ownerWrite && ownerWrite.Contains("ownerid"))))
            await _workflows.OnAssignedAsync(session, run.EntityId, ct);
        if (run.ApiMessage is { } deletedApiMessage) await customApiAuthoring.AfterDeleteAsync(deletedApiMessage, session, ct);
        if (run.WebhookSecret is { } endpoint) await webhookSecrets.MarkSetAsync(endpoint, session, ct);
    }

    /// <summary>
    /// The core operation on the model (S.2e3a): the core gets the model request, which the pipeline holds (<see cref="CoreHandOff"/>),
    /// so its handler runs on <see cref="Run.Sdk"/> without a conversion. The answer is an unread shell paired in the chain's mirror; the
    /// response is its typed SDK twin, the core's own object, which <see cref="ResponseToContext"/> reads in only when it is observed.
    /// </summary>
    private async Task<OrganizationResponse> CoreOperationAsync(Run run, CancellationToken ct)
    {
        var invocation = run.Invocation;
        var shell = await core.ExecuteCoreAsync(run.Request,
            new CoreCall(invocation.Caller, run.Session, (nested, c) => CoreNestedAsync(nested, run, c), invocation.ServerSetValues), ct);
        run.CoreResponse = shell;
        return run.Mirror.TryGetSdk(shell, out var twin) && twin is OrganizationResponse response
            ? response
            : run.Mirror.Out(shell)!; // a core that answers with a model response of its own
    }

    /// <summary>
    /// A request the core issues (the record Update of CommitFileBlocksUpload, a delete cascade) runs as a plug-in-issued request would:
    /// one level deeper, with (a copy of) this message's context as its ParentContext. Plan 4.1c: it keeps the server shared variables
    /// (the queueitem Create of a delivery's AddToQueue carries the delivery's), not the server values. The core's adapter has just read
    /// the request in, so its SDK twin is the core's own request (its type). The guard reads the core's marks, such as
    /// <see cref="CascadeHint"/>, on <paramref name="nested"/> itself, the model twin of that request (S.2e3c2a, Decision 5). The answer
    /// goes back as the model twin of the nested execution's response (an unread shell when nothing converted it), so the core gets
    /// that SDK response unchanged and filled.
    /// </summary>
    private async Task<Response> CoreNestedAsync(Request nested, Run run, CancellationToken ct)
    {
        var mirror = run.Mirror;
        var request = mirror.TryGetSdk(nested, out var twin) && twin is OrganizationRequest held ? held : mirror.Out(nested)!;
        var response = await ExecuteAsync(request,
            run.Invocation with
            {
                // A copy of this context (same stage and values, shared bags) per call, as for the inner executions: the view
                // of a copy lent once is never lent again with values of a later stage.
                Session = run.Session, Parent = run.Context.Clone(run.Context.Stage), Depth = run.Invocation.Depth + 1,
                IsCoreCascade = CascadeHint.IsMarked(nested),
                ServerSetValues = null,
            }, ct);
        // The nested execution answered with its core's typed response (paired with an unread shell in this chain's mirror when nothing
        // observed it) or with the response it wrote out of its model (FinishResponse), so the pair is current either way.
        return mirror.TryGetModel(response, out var model) && model is Response paired ? paired : mirror.Shell(response);
    }

    /// <summary>
    /// Runs <paramref name="inner"/> as an inner execution of <paramref name="run"/> (#76, ruling B4): marked with
    /// <paramref name="kind"/> (<see cref="InnerExecution"/>), in its session and transaction (which it never commits or rolls back:
    /// the outer execution owns it), at the <b>same</b> depth (unlike a plug-in's call), with the outer context as its
    /// <c>ParentContext</c> (a copy that reports the outer's current stage, <see cref="MainOperation"/>: inner runs only happen
    /// there). Its failure propagates to the outer execution, which rolls the whole transaction back.
    /// </summary>
    private Task<OrganizationResponse> RunInnerAsync(OrganizationRequest inner, InnerKind kind, Run run, CancellationToken ct)
    {
        InnerExecution.Mark(inner, kind);
        return ExecuteAsync(inner, run.Invocation with { Session = run.Session, Parent = run.Context.Clone(MainOperation), ServerSetValues = null }, ct);
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

    // ---- the response ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// After stage 30: the response's results become the <c>OutputParameters</c> (Retrieve/RetrieveMultiple keys mapped), unless
    /// nobody reads them (Decision 3: no step at 40, no workflow, no custom API, and nothing in <c>OutputParameters</c> yet), then the
    /// response stays unconverted. A custom API's outputs are brought to its declaration; the id of a Create or Upsert becomes the
    /// primary entity id.
    /// </summary>
    private void ResponseToContext(Run run, SortedSet<string> dropped)
    {
        var response = run.Response!;
        var context = run.Context;
        if (run.Observed || context.OutputParameters.Count > 0)
        {
            // S.2e3b (Decision 7): a CRUD response is the core's model response, already written into its typed twin (an ordinary twin,
            // current): it is not read back. Any other core response is an unread shell, read in here.
            run.ModelResponse = run.CoreResponse is { } answered && !run.Mirror.IsUnread(answered)
                                && run.Mirror.TryGetSdk(answered, out var answeredTwin) && ReferenceEquals(answeredTwin, response)
                ? answered
                : run.Mirror.In(response)!;
            foreach (var (key, value) in run.ModelResponse.Results)
                context.OutputParameters[run.MapsOutput ? PluginOutputKeys.GetValueOrDefault(key, key) : key] = value;
            if (run.Api is not null) CompleteCustomApi(run, dropped);
        }
        if (string.Equals(run.Message, "Create", StringComparison.OrdinalIgnoreCase) && response.Results.TryGetValue("id", out var id)
            && id is Guid created)
            context.PrimaryEntityId = run.EntityId = created;
        if (run.UpsertTable is not null && response.Results.TryGetValue(Target, out var upserted) && upserted is EntityReference upsertedRef)
            context.PrimaryEntityId = run.EntityId = upsertedRef.Id;
    }

    /// <summary>The custom API's outputs brought to its declared response properties (<see cref="CustomApiValidator.Complete"/>).</summary>
    private static void CompleteCustomApi(Run run, SortedSet<string> dropped)
    {
        var output = run.Mirror.Out(run.Context.OutputParameters)!;
        dropped.UnionWith(CustomApiValidator.Complete(run.Api!, output));
        run.Mirror.In(output);
    }

    /// <summary>The tables the message wrote, for the caches invalidated at the commit.</summary>
    private void AddWrittenEntities(Run run, HashSet<string> written)
    {
        if (run.EntityName is not null) written.Add(run.EntityName);
        if (!run.CustomAction) written.UnionWith(core.WrittenEntities(run.Request));
    }

    /// <summary>
    /// The response: the core's own object, its results refilled from the final <c>OutputParameters</c>; untouched when nothing read
    /// it after stage 30.
    /// </summary>
    /// <remarks>
    /// As with the SDK pipeline, each result is the instance the steps' <c>OutputParameters</c> hold: before the write, a result slot
    /// gets the SDK value of the same key in the steps' collection when it is the twin of the model value, so the write keeps it (an
    /// immutable value written into a slot that did not hold its twin would be a new instance). One lookup per key.
    /// </remarks>
    private OrganizationResponse FinishResponse(Run run)
    {
        if (run.ModelResponse is not { } model) return run.Response!;
        var mirror = run.Mirror;
        var response = run.Response!;
        var stepOutput = mirror.TryGetSdk(run.Context.OutputParameters, out var lent) ? lent as ParameterCollection : null;
        model.Results.Clear();
        foreach (var (key, value) in run.Context.OutputParameters)
        {
            var resultKey = run.MapsOutput ? ResponseKeys.GetValueOrDefault(key, key) : key;
            model.Results[resultKey] = value;
            if (value is not null && stepOutput is not null && stepOutput.TryGetValue(key, out var sdkValue) && sdkValue is not null
                && mirror.TryGetModel(sdkValue, out var twin) && ReferenceEquals(twin, value))
                response.Results[resultKey] = sdkValue;
        }
        return mirror.Out(model)!;
    }

    // ---- the internal checks of stage 20 ----------------------------------------------------------------------------------------

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
                               && await SdkImageAsync(entityName, entityId, null, session, ct) is { } existing:
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
                 && await SdkImageAsync(entityName, entityId, null, session, ct) is { } existing)
        {
            await validator.ValidateAsync(entityName, target, existing, session, ct);
        }
    }

    /// <summary>
    /// The checks of a public Create or Update of <c>workflow</c> or <c>processtrigger</c> (plan 2.21, ruling A16) on the request's
    /// Target as it is now: <see cref="SystemTableRules.DemandValidProcessWrite(string, Entity, Caller)"/>, then <see cref="IWorkflowPipelineHook.ValidateWriteAsync"/>
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
        if (await SdkImageAsync(entityName!, entityId, ["statecode", WorkflowSchema.TypeColumn], session, ct)
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
                               && await SdkImageAsync(entityName, entityId, null, session, ct) is { } existing:
                await ServiceEndpointRules.ValidateAsync(updated, existing, Options.WebhookAllowAnyPort, webhookSecrets, session, ct);
                return await webhookSecrets.TakeAsync(updated, session, ct);
            case "delete" when target is EntityReference deleted:
                await webhookSecrets.DeleteAsync(deleted.Id, session, ct);
                return null;
            default:
                return null;
        }
    }

    // ---- the primary entity ------------------------------------------------------------------------------------------------------

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

    // ---- images --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The pre images of an execution, read before stage 20: the record's, and for a bulk execution (#76, ruling B6) one per target
    /// (none is registered on CreateMultiple), whose ids are read on the SDK request.
    /// </summary>
    private async Task<StepImages> PreImagesAsync(Run run, IReadOnlyList<StageStep>[] stages, CancellationToken ct)
    {
        Record? pre = null;
        if (PreImageMessages.Contains(run.Message) && run.EntityName is not null)
            pre = await ReadImageAsync(run.EntityName, run.EntityId, stages, pre: true, run, ct);
        Record?[]? preList = null;
        if (run.BulkTable is { } bulkTable)
        {
            // The SDK request is current: the views of stage 10 returned into it (their bags are its own collections).
            preList = await ReadImagesAsync(bulkTable.LogicalName, BulkTargets.TargetIds(run.Sdk, bulkTable, null), stages, pre: true, run, ct);
        }
        return new StepImages(pre, null, preList, null);
    }

    /// <summary>
    /// <paramref name="images"/> with the post images, read after stage 30: the record's, and for a bulk execution one per target (a
    /// CreateMultiple's records by its Ids).
    /// </summary>
    private async Task<StepImages> PostImagesAsync(Run run, StepImages images, IReadOnlyList<StageStep>[] stages, CancellationToken ct)
    {
        Record? post = null;
        if (PostImageMessages.Contains(run.Message) && run.EntityName is not null)
            post = await ReadImageAsync(run.EntityName, run.EntityId, stages, pre: false, run, ct);
        var postList = run.BulkTable is { } bulkTable
            ? await ReadImagesAsync(bulkTable.LogicalName, BulkTargets.TargetIds(run.Sdk, bulkTable, run.Response), stages, pre: false, run, ct)
            : null;
        return images with { Post = post, PostList = postList };
    }

    /// <summary>
    /// Reads the record once with the union of the attributes the steps' pre (type 0/2) or post (type 1/2) images ask
    /// for; null when no step wants such an image or the record does not exist. The core converts the image once, into the chain's
    /// mirror (<see cref="ImageAsync"/>), so the record's SDK twin is the core's own <c>Entity</c>, which <see cref="AddImages"/>
    /// projects from.
    /// </summary>
    private async Task<Record?> ReadImageAsync(string entityName, Guid id, IReadOnlyList<StageStep>[] stages, bool pre, Run run,
        CancellationToken ct)
    {
        var images = stages.SelectMany(s => s).SelectMany(s => s.Step.Images).Where(i => Wants(i, pre)).ToList();
        if (images.Count == 0 || id == Guid.Empty) return null;
        var attributes = images.Any(i => i.Attributes is null)
            ? null
            : images.SelectMany(i => i.Attributes!).Distinct(StringComparer.Ordinal).ToArray();
        return await ImageAsync(entityName, id, attributes, run.Mirror, run.Session, ct);
    }

    /// <summary>
    /// The record <paramref name="entityName"/> <paramref name="id"/> as an image (S.2e3a): read by the core, which converts it once
    /// into <paramref name="mirror"/> (<see cref="CoreHandOff.ReadImagesWith"/>), so its SDK twin there is the core's own <c>Entity</c>.
    /// </summary>
    private async Task<Record?> ImageAsync(string entityName, Guid id, string[]? attributes, SdkMirror mirror, DataSession session,
        CancellationToken ct)
    {
        var target = new RecordReference { LogicalName = entityName, Id = id };
        CoreHandOff.ReadImagesWith(target, mirror);
        return await core.RetrieveImageAsync(target, attributes, session, ct);
    }

    /// <summary>
    /// The image the internal checks of stage 20 read on the SDK side: the core's own <c>Entity</c>, the twin of the record it converted
    /// once (<see cref="ImageAsync"/>, in a mirror of this read).
    /// </summary>
    private async Task<Entity?> SdkImageAsync(string entityName, Guid id, string[]? attributes, DataSession session, CancellationToken ct)
    {
        var mirror = new SdkMirror();
        return await ImageAsync(entityName, id, attributes, mirror, session, ct) is { } record && mirror.TryGetSdk(record, out var sdk)
            ? (Entity)sdk!
            : null;
    }

    /// <summary>
    /// The image of each record of <paramref name="ids"/>, in order, for the image collections of a bulk execution (#76, ruling
    /// B6): one read per record (<see cref="ReadImageAsync"/>; none when no step wants such an image).
    /// </summary>
    private async Task<Record?[]> ReadImagesAsync(string entityName, Guid[] ids, IReadOnlyList<StageStep>[] stages, bool pre, Run run,
        CancellationToken ct)
    {
        var images = new Record?[ids.Length];
        for (var i = 0; i < ids.Length; i++) images[i] = await ReadImageAsync(entityName, ids[i], stages, pre, run, ct);
        return images;
    }

    /// <summary>
    /// Gives <paramref name="context"/> the images of <paramref name="entry"/>'s step (post images only when <paramref name="post"/>).
    /// A bulk execution's step and a step merged into a single-record execution (#76, ruling B6) get them only in
    /// <c>PreEntityImagesCollection</c>/<c>PostEntityImagesCollection</c>, one collection per target (one for a merged step);
    /// any other step in <c>PreEntityImages</c>/<c>PostEntityImages</c>. Each image is projected from the record the core read (its
    /// SDK twin, so the values stay the instances every step's image shares), then crosses into the model.
    /// </summary>
    private static void AddImages(StepContext context, StageStep entry, StepImages images, bool post, SdkMirror mirror)
    {
        var step = entry.Step;
        var preImage = Sdk(images.Pre, mirror);
        var postImage = post ? Sdk(images.Post, mirror) : null;
        if (images.PreList is not null || entry.MultipleView)
        {
            context.PreEntityImagesCollection =
                [.. (images.PreList?.Select(r => Sdk(r, mirror)) ?? [preImage]).Select(e => ImageCollection(step, e, pre: true, mirror))];
            if (post)
                context.PostEntityImagesCollection =
                    [.. (images.PostList?.Select(r => Sdk(r, mirror)) ?? [postImage]).Select(e => ImageCollection(step, e, pre: false, mirror))];
            return;
        }
        foreach (var image in step.Images)
        {
            if (preImage is not null && Wants(image, pre: true)) context.PreEntityImages[image.Alias] = Image(preImage, image.Attributes, mirror);
            if (postImage is not null && Wants(image, pre: false)) context.PostEntityImages[image.Alias] = Image(postImage, image.Attributes, mirror);
        }
    }

    private static Entity? Sdk(Record? record, SdkMirror mirror) =>
        record is not null && mirror.TryGetSdk(record, out var sdk) ? (Entity)sdk! : null;

    /// <summary>The pre or post images <paramref name="step"/> asks for, taken from <paramref name="record"/> (empty when null).</summary>
    private static IDictionary<string, Record> ImageCollection(PluginStep step, Entity? record, bool pre, SdkMirror mirror)
    {
        var collection = new Dictionary<string, Record>(StringComparer.Ordinal);
        if (record is null) return collection;
        foreach (var image in step.Images)
            if (Wants(image, pre)) collection[image.Alias] = Image(record, image.Attributes, mirror);
        return collection;
    }

    private static Record Image(Entity source, string[]? attributes, SdkMirror mirror) => (Record)mirror.In(Project(source, attributes))!;

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

    // ---- real-time workflows -----------------------------------------------------------------------------------------------------

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
    /// and <see cref="IWorkflowPipelineHook.HasBackgroundAsync"/> answer from memory). Read on the SDK request, which the views of
    /// stage 10 left current.
    /// </summary>
    private async Task<WorkflowTrigger?> WorkflowTriggerAsync(Run run, CancellationToken ct)
    {
        var request = run.Sdk;
        var entityName = run.EntityName;
        if (entityName is null || !WorkflowMessages.TryGetValue(request.RequestName, out var message)
            || !(await _workflows.HasRealtimeAsync(entityName, ct) || await _workflows.HasBackgroundAsync(entityName, ct))) return null;
        var target = request.Parameters.TryGetValue(Target, out var t) ? t as Entity : null;
        // W5: a SetState triggers as SetState (its columns are statecode and statuscode), not as an Update of any column.
        if (message == "Update" && AuditActionHint.Of(request) == AuditActions.SetState) message = "SetState";
        IReadOnlyCollection<string> columns = target is null ? [] : target.Attributes.Keys.ToList();
        return new WorkflowTrigger(message, columns,
            await RecordOwnerAsync(message, entityName, run.EntityId, target, run.Invocation.Caller, run.Session, ct));
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
            var principal = await SdkImageAsync(owner.LogicalName, owner.Id, ["businessunitid"], session, ct);
            return new RecordOwner(owner.Id, principal?.GetAttributeValue<EntityReference>("businessunitid")?.Id ?? Guid.Empty);
        }
        // No owner can be read (no id, or the record does not exist): the matcher then applies no scope. That is safe: a write of a
        // missing record fails in the core operation (404) and rolls back what a stage 20 workflow did before it; a Create has no id
        // here but takes the branch above.
        if (entityId == Guid.Empty
            || await SdkImageAsync(entityName, entityId, ["ownerid", "owningbusinessunit"], session, ct) is not { } stored
            || stored.GetAttributeValue<EntityReference>("ownerid") is not { } storedOwner) return null;
        return new RecordOwner(storedOwner.Id, stored.GetAttributeValue<EntityReference>("owningbusinessunit")?.Id ?? Guid.Empty);
    }

    // ---- the step's SDK parts ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Posts the context of a step on a <c>serviceendpoint</c> (plan 3.2) to its webhook: the model's remote context written by the own
    /// serializer when the model set carries it and no value has an SDK twin that is not plain (<see cref="SdkMirror.IsPlainTwin"/>),
    /// else, as before, the remote context of the view the mirror lends (an early-bound entity keeps its contract, a value the model
    /// does not know is written as itself). Either way the levels the core holds are read first. No trace is written (Dataverse writes
    /// none for a webhook). A context that cannot be serialized
    /// (<see cref="SerializationException"/> or <see cref="InvalidDataContractException"/>) fails as a final 500 <c>0x80040224</c>
    /// whose message names the endpoint only (never the value that could not be serialized).
    /// </summary>
    /// <exception cref="PluginExecutionException">The delivery failed (<see cref="WebhookSender.SendAsync"/>).</exception>
    private async Task SendWebhookAsync(WebhookEndpoint endpoint, StepContext context, SdkMirror mirror, CancellationToken ct)
    {
        try
        {
            SyncBoundaries(context, mirror);
            var model = context.ToRemote();
            if (ModelSet.CanCarry(model, mirror.IsPlainTwin))
            {
                await webhookSender.SendAsync(endpoint, model, context.OrganizationName!, ct);
                return;
            }

            RemoteExecutionContext remote;
            var view = PluginContextAdapter.Lend(context, mirror, HeldLevels(mirror));
            try
            {
                remote = AsyncContextSerializer.ToRemote(view);
            }
            finally
            {
                PluginContextAdapter.Return(view, mirror);
            }
            await webhookSender.SendAsync(endpoint, remote, context.OrganizationName!, ct);
        }
        catch (Exception ex) when (ex is SerializationException or InvalidDataContractException)
        {
            _logger.LogWarning("The context for webhook {Endpoint} could not be serialized: {Error}.", endpoint.Name, ex.GetType().Name);
            throw new PluginExecutionException(new XrmdFaultException(ErrorCodes.UnexpectedPluginException,
                $"Endpoint unavailable: webhook '{endpoint.Name}' could not be sent the context: it holds a value that cannot be serialized.",
                500), unexpected: false);
        }
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

/// <summary>
/// S.2e3a: what the pipeline hands the core's entry adapter. While the pipeline holds a model request at the core (between
/// <c>HoldAtCoreAsync</c> and <c>ReleaseCore</c>, S.2e2c), its SDK request, so the adapter runs the handler on it without converting the
/// request again; and the mirror an image is read into (<see cref="ReadImagesWith"/>), so the image is converted once, into the chain's
/// mirror, paired with the core's own <c>Entity</c>. Internal: only the core (<c>InternalsVisibleTo</c>) acts on a held request.
/// </summary>
internal static class CoreHandOff
{
    /// <summary>The model request of each execution the pipeline holds at the core, with its SDK request.</summary>
    private static readonly ConditionalWeakTable<Request, OrganizationRequest> Held = new();

    /// <summary>The mirror the core converts the image of a target into (<see cref="ReadImagesWith"/>).</summary>
    private static readonly ConditionalWeakTable<RecordReference, SdkMirror> ImageMirrors = new();

    /// <summary>
    /// Test seam: called by the core's entry adapter with the SDK request its handler runs on (early-bound identity tests).
    /// </summary>
    internal static readonly AsyncLocal<Action<OrganizationRequest>?> HandlerRequest = new();

    /// <summary>
    /// The eight CRUD messages (Create, Retrieve, Update, Delete, RetrieveMultiple, Assign, Associate, Disassociate; any case, as the
    /// step registry matches names), which the core answers on the model request (S.2e3b, Decision 8): the pipeline holds no window for
    /// them and the adapter's <see cref="HandlerRequest"/> seam never fires for them. The core's own list is <c>CoreResponses.IsCrud</c>.
    /// </summary>
    internal static bool IsModelMessage(string? message) => message is not null && ModelMessages.Contains(message);

    private static readonly HashSet<string> ModelMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Create", "Retrieve", "Update", "Delete", "RetrieveMultiple", "Assign", "Associate", "Disassociate",
    };

    /// <summary>Test seam: called by the core's entry adapter with each image record it wrote out into the mirror named for it
    /// (<see cref="ReadImagesWith"/>) and that SDK twin.</summary>
    internal static readonly AsyncLocal<Action<Entity, Record>?> ImageRead = new();

    /// <summary>
    /// True, with the held SDK request, while the pipeline holds <paramref name="request"/> at the core; the core then works on
    /// <paramref name="sdk"/> directly and writes nothing back (the pipeline reads it back once). False outside a held window.
    /// </summary>
    internal static bool TryGetHeld(Request request, out OrganizationRequest? sdk)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (Held) return Held.TryGetValue(request, out sdk);
    }

    /// <summary>
    /// The start of a held window (<c>HoldAtCoreAsync</c>). True when this call opened it; false, leaving the entry as it is, when a run
    /// already holds <paramref name="request"/> (a run that re-enters on the same model request inside the outer window). Like the
    /// <c>AtCore</c> entry beside it, the window belongs to the run that opened it.
    /// </summary>
    internal static bool Hold(Request request, OrganizationRequest sdk)
    {
        lock (Held) return Held.TryAdd(request, sdk);
    }

    /// <summary>
    /// The end of a held window (<c>ReleaseCore</c>): only the run that opened it (<paramref name="owns"/>, from <see cref="Hold"/>)
    /// removes the entry, so an inner run never ends the outer run's window.
    /// </summary>
    internal static void Release(Request request, bool owns)
    {
        if (!owns) return;
        lock (Held) Held.Remove(request);
    }

    /// <summary>Makes the core read the image of <paramref name="target"/> into <paramref name="mirror"/> (see the class remarks).</summary>
    internal static void ReadImagesWith(RecordReference target, SdkMirror mirror)
    {
        lock (ImageMirrors) ImageMirrors.AddOrUpdate(target, mirror);
    }

    /// <summary>The mirror to read the image of <paramref name="target"/> into, or null (the core then uses a mirror of its own).</summary>
    internal static SdkMirror? ImageMirror(RecordReference target)
    {
        lock (ImageMirrors) return ImageMirrors.TryGetValue(target, out var mirror) ? mirror : null;
    }
}
