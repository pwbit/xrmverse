using Xrmd.Abstractions;
using Xrmd.Audit;
using Xrmd.Core.Duplicates;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Mail;
using Xrmd.Query;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Security;
using Xrmd.Solution;

namespace Xrmd.Core;

/// <summary>
/// S.2e3c1 (Decision 2): the model dispatch every core message enters (<see cref="ExecuteMessageAsync"/>,
/// <see cref="ExecuteNonPipelineMessageAsync"/>). A message whose body has not moved to the model yet is bridged to its SDK handler
/// (<see cref="CoreSdkMessages"/>); a name that is no core message goes there too, where the SDK switch refuses it with today's
/// <see cref="NotSupportedException"/>.
/// <para>
/// S.2e3b (Task 8): the eight CRUD messages (Create, Retrieve, Update, Delete, RetrieveMultiple, Assign, Associate, Disassociate)
/// dispatched on the model request: the parameters are read as model values and the model bodies run, with no conversion of the
/// request. The answer is a model <see cref="Response"/> with today's results; the entry adapter (<c>Sdk/CoreOperations.Sdk.cs</c>)
/// pairs it with today's typed SDK response. A helper of another step that is still on the SDK is reached through
/// <see cref="CoreSdkBridges"/>, which converts only when the helper applies.
/// </para>
/// </summary>
public sealed partial class CoreOperations
{
    /// <summary>
    /// Test seam: called with each model request <see cref="ExecuteCrudAsync"/> dispatches (the CRUD messages no longer reach the
    /// adapter's <c>CoreHandOff.HandlerRequest</c> seam, which sees the SDK request of a handler of <c>CoreOperations.cs</c>).
    /// </summary>
    internal static readonly AsyncLocal<Action<Request>?> CrudRequest = new();

    /// <summary>
    /// Test seam: called with each model request that enters the model dispatch (<see cref="ExecuteMessageAsync"/>,
    /// <see cref="ExecuteNonPipelineMessageAsync"/>), bridged or not. <see cref="CrudRequest"/> still fires for the CRUD branch
    /// (ruling P1).
    /// </summary>
    internal static readonly AsyncLocal<Action<Request>?> MessageRequest = new();

    /// <summary>
    /// True for a core message whose body runs on the model (the eight CRUD messages, S.2e3b; the share and record team messages,
    /// S.2e3c1 Task 3; the role privilege messages, RetrieveExchangeRate and RetrieveEntityChanges, Task 4; the queue messages, Task 5; ExecuteWorkflow, refused, Task 6): a core message of <see cref="CoreMessages"/> that is not bridged (<see cref="CoreSdkMessages.Bridged"/>). The
    /// entry adapter pairs its model response with today's typed SDK response. Each such name has a model case in
    /// <see cref="ExecuteMessageAsync"/> and a typed response in <see cref="CoreResponses.Typed"/>.
    /// </summary>
    private static bool IsOnModel(string canonicalName) =>
        CoreMessages.Contains(canonicalName) && !CoreSdkMessages.Bridged.Contains(canonicalName);

    /// <summary>
    /// True for a message answered outside the pipeline whose body runs on the model (RetrievePrincipalAccess and
    /// RetrieveSharedPrincipalsAndAccess, S.2e3c1 Task 3; RetrieveUserPrivileges, Task 4; WhoAmI, RetrieveAppComponents,
    /// RetrieveUnpublished, InitializeFrom, RetrieveFormattedImportJobResults and the refusals from a plug-in, Task 6): one of <see cref="NonPipelineMessages"/> that is not bridged. As
    /// <see cref="IsOnModel"/>, each has a model case in <see cref="ExecuteNonPipelineMessageAsync"/> and a typed response.
    /// </summary>
    private static bool IsNonPipelineOnModel(string canonicalName) =>
        NonPipelineMessages.Contains(canonicalName) && !CoreSdkMessages.Bridged.Contains(canonicalName);

    /// <summary>
    /// A core message, named <paramref name="canonicalName"/> (the canonical case of <see cref="CoreMessages"/>, or the request's own
    /// name when it is no core message), on the model request: a message on the model runs its model body (the CRUD messages,
    /// <see cref="ExecuteCrudAsync"/>; the access messages, <see cref="ExecuteAccessAsync"/>; the role privilege, currency and change
    /// tracking messages, <see cref="ExecuteRoleCurrencyOrChangeAsync"/>; the queue messages, <see cref="ExecuteQueueAsync"/>; ExecuteWorkflow, refused); any other name goes to its SDK handler through <see cref="CoreSdkMessages.ExecuteAsync"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">A name that is no core message (today's text, from the SDK switch).</exception>
    private async Task<Response> ExecuteMessageAsync(Request request, string canonicalName, CoreCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);
        MessageRequest.Value?.Invoke(request);
        if (!IsOnModel(canonicalName)) return await CoreSdkMessages.ExecuteAsync(this, request, call, ct);
        if (IsAccessMessage(canonicalName)) return await ExecuteAccessAsync(request, canonicalName, call, ct);
        if (IsRoleCurrencyOrChangeMessage(canonicalName)) return await ExecuteRoleCurrencyOrChangeAsync(request, canonicalName, call, ct);
        if (QueueSchema.Messages.Contains(canonicalName)) return await ExecuteQueueAsync(request, call, ct);
        if (canonicalName == PipelineExecutor.ExecuteWorkflowMessage) throw ExecuteWorkflowRefusal(request, call);
        return await ExecuteCrudAsync(request, canonicalName, call, ct);
    }

    /// <summary>
    /// ExecuteWorkflow at the core (S.2e3c1 Task 6): its stage 30 is <c>IWorkflowPipelineHook.ExecuteWorkflowAsync</c>, which the
    /// pipeline runs (plan 2.14, W21), so the core refuses it with today's texts (the check of server values first, as the SDK handler did).
    /// </summary>
    private static InvalidOperationException ExecuteWorkflowRefusal(Request request, CoreCall call) =>
        call.ServerSetValues is not null
            ? new InvalidOperationException($"Server values are written on a Create only, not on '{request.Name}'.")
            : new InvalidOperationException("ExecuteWorkflow runs through the pipeline, which hands it to the classic workflows.");

    /// <summary>
    /// The queue messages (S.2e3c1 Task 5) on the model request (<see cref="Core.QueueMessages"/>, plan 4.1a): the item and membership
    /// writes run through the pipeline, one level deeper (decision 4), as model requests the entry adapter pairs with today's request
    /// classes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Server values (written on a Create only).</exception>
    private Task<Response> ExecuteQueueAsync(Request request, CoreCall call, CancellationToken ct)
    {
        if (call.ServerSetValues is not null)
            throw new InvalidOperationException($"Server values are written on a Create only, not on '{request.Name}'.");
        return QueueMessages.ExecuteAsync(request, call.Caller, call.Session, call.Nested, ct);
    }

    /// <summary>
    /// A message answered outside the pipeline, named <paramref name="canonicalName"/> (the canonical case of
    /// <see cref="NonPipelineMessages"/>, or the request's own name), on the model request: the access reads, RetrieveUserPrivileges and the reads and refusals of <see cref="ExecuteNonPipelineReadAsync"/> run their model body
    /// (<see cref="AccessMessages"/>, in the caller's session, so a plug-in's call sees its uncommitted rows: plan 3.5 UI-1b final
    /// review, I3); any other name goes to its SDK handler through <see cref="CoreSdkMessages.ExecuteNonPipelineAsync"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">A name that is no such message (today's text, from the SDK switch).</exception>
    private async Task<Response> ExecuteNonPipelineMessageAsync(Request request, string canonicalName, Caller caller, DataSession session,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        MessageRequest.Value?.Invoke(request);
        if (!IsNonPipelineOnModel(canonicalName)) return await CoreSdkMessages.ExecuteNonPipelineAsync(this, request, caller, session, ct);
        ArgumentNullException.ThrowIfNull(caller);
        return canonicalName switch
        {
            AccessMessages.RetrievePrincipalAccess or AccessMessages.RetrieveSharedPrincipalsAndAccess =>
                await accessMessages.ExecuteAsync(request, caller, session, ct),
            "RetrieveUserPrivileges" => await RetrieveUserPrivilegesAsync(request, caller, session, ct),
            _ when IsNonPipelineRead(canonicalName) => await ExecuteNonPipelineReadAsync(request, canonicalName, caller, session, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(canonicalName), canonicalName, "Not a message answered outside the pipeline on the model."),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// S.2e3c1 (Task 4): read on the model request, its name and its <c>Relationship</c> (a <see cref="RelationshipName"/>); no SDK request
    /// is built. Associate/Disassociate over a security relationship: its intersect (<c>systemuserroles</c>, <c>teamroles</c>,
    /// <c>teammembership</c>, <c>systemuserprofiles</c>, <c>teamprofiles</c>), over a saved many-to-many relationship: its intersect
    /// entity; AddPrivilegesRole, ReplacePrivilegesRole, RemovePrivilegeRole: <c>roleprivileges</c>; AddUserToRecordTeam,
    /// RemoveUserFromRecordTeam: <c>team</c>, <c>teammembership</c> and <c>principalobjectaccess</c> (plan 2.16, ruling A9);
    /// PublishDuplicateRule, UnpublishDuplicateRule: <c>duplicaterule</c>.
    /// </remarks>
    public IReadOnlyCollection<string> WrittenEntities(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (CoreMessages.TryGetValue(request.Name ?? "", out var canonical) ? canonical : request.Name)
        {
            case "Associate" or "Disassociate":
                if (!request.Parameters.TryGetValue("Relationship", out var r) || r is not RelationshipName relationship) return [];
                if (SecurityAdministration.FindRelationship(relationship.SchemaName) is { } security) return [security.IntersectEntity];
                return store.GetRelationshipDefinition(relationship.SchemaName ?? "") is Model.Metadata.ManyToManyRelationship manyToMany
                    ? [manyToMany.IntersectEntityName!]
                    : [];
            case "AddPrivilegesRole" or "ReplacePrivilegesRole" or "RemovePrivilegeRole":
                return [RolePrivileges];
            case AccessTeamRules.AddUserToRecordTeam or AccessTeamRules.RemoveUserFromRecordTeam:
                return [Team, "teammembership", PrincipalObjectAccess];
            case DuplicateRulePublisher.PublishMessage or DuplicateRulePublisher.UnpublishMessage:
                return [DuplicateDetectionSchema.Rule];
            default:
                return [];
        }
    }

    /// <summary>
    /// One of the eight CRUD messages, named <paramref name="canonicalName"/> (the canonical case of <see cref="CoreMessages"/>), on the
    /// model request. The order of the reads and checks is the one of the SDK handlers it replaces, so the same fault comes first.
    /// </summary>
    /// <exception cref="InvalidOperationException">Server values on a message other than Create.</exception>
    /// <exception cref="XrmdFaultException">400 for a missing parameter or one of another type (named with today's SDK type); the
    /// faults of the model bodies.</exception>
    private async Task<Response> ExecuteCrudAsync(Request request, string canonicalName, CoreCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);
        if (call.ServerSetValues is not null && canonicalName != "Create")
            throw new InvalidOperationException($"Server values are written on a Create only, not on '{request.Name}'.");
        CrudRequest.Value?.Invoke(request);
        var (caller, session) = (call.Caller, call.Session);
        switch (canonicalName)
        {
            case "Create":
                var created = Parameter<Record>(request, "Target", "Entity");
                var detectOnCreate = DetectDuplicates(request);
                var id = await CreateRecordAsync(session, caller, created, PendingMailSecrets.Claim(created), ct,
                    detectOnCreate, call.ServerSetValues);
                if (ComponentTypeOf(created) is { } componentType)
                    await tracker.TrackAsync(session, componentType, id, SolutionUniqueName(request), ct, markActive: caller != Caller.Internal);
                await MarkParentChangedAsync(session, caller, created.LogicalName, id, null, ct);
                return await WithSessionTokenAsync(session, TableOf(created.LogicalName), id,
                    new Response { Name = "Create", Results = { ["id"] = id } }, ct);
            case "Retrieve":
                UnmaskedData.Refuse(request);
                var target = Parameter<RecordReference>(request, "Target", "EntityReference");
                var columns = request.Parameters.TryGetValue("ColumnSet", out var c) ? c as ColumnSelection : null;
                var record = await RetrieveRecordAsync(session, caller, target.LogicalName, target.Id, columns, ct,
                    CoreSdkBridges.ElasticKeyOf(TableOf(target.LogicalName), target, request));
                return new Response { Name = "Retrieve", Results = { ["Entity"] = record } };
            case "Update":
                var updated = Parameter<Record>(request, "Target", "Entity");
                var updatedTable = TableOf(updated.LogicalName);
                // Plan 3.4: an elastic row is found in its partition first (404), under an update lock.
                await CoreSdkBridges.BeforeElasticUpdateAsync(session, updatedTable, updated, request, ct);
                // SetState runs as an Update the pipeline marks with its own audit action (ruling A6).
                var detectOnUpdate = DetectDuplicates(request);
                await UpdateRecordAsync(session, caller, updated, AuditActionHint.Of(request) ?? AuditActions.Update, null,
                    PendingMailSecrets.Claim(updated), null, ct, detectOnUpdate);
                return await WithSessionTokenAsync(session, updatedTable, UpdateId(updated, updatedTable, SystemAttributes.PrimaryIdName(updatedTable)),
                    new Response { Name = "Update" }, ct);
            case "Delete":
                var deleted = Parameter<RecordReference>(request, "Target", "EntityReference");
                var deletedId = await CoreSdkBridges.BeforeElasticDeleteAsync(session, TableOf(deleted.LogicalName), deleted, request, ct);
                await DeleteRecordAsync(session, caller, deleted.LogicalName, deletedId, call.Nested, ct);
                return new Response { Name = "Delete" };
            case "RetrieveMultiple":
                UnmaskedData.Refuse(request);
                // A front end that built the query model itself passes it along (QueryModelHint); a plug-in that replaced Query wins.
                var model = QueryModelHint.Of(request) ?? ReadQuery(request);
                model = CoreSdkBridges.ApplyElasticRequest(model, request, store);
                var collection = await RetrieveRecordsAsync(session, caller, model, ct);
                return new Response { Name = "RetrieveMultiple", Results = { ["EntityCollection"] = collection } };
            case "Assign":
                var assigned = Parameter<RecordReference>(request, "Target", "EntityReference");
                await AssignRecordAsync(session, caller, assigned, Parameter<RecordReference>(request, "Assignee", "EntityReference"), ct);
                return new Response { Name = "Assign" };
            case "Associate":
            case "Disassociate":
                var associate = canonicalName == "Associate";
                await AssociateRecordsAsync(session, caller, Parameter<RecordReference>(request, "Target", "EntityReference"),
                    Parameter<RelationshipName>(request, "Relationship", "Relationship"),
                    Parameter<RecordReferenceCollection>(request, "RelatedEntities", "EntityReferenceCollection"), associate, ct);
                return new Response { Name = canonicalName };
            default:
                throw new ArgumentOutOfRangeException(nameof(canonicalName), canonicalName, "Not a CRUD message.");
        }
    }

    /// <summary>
    /// A required parameter of the model <paramref name="request"/>; InvalidArgument (400) when missing or of another type, the type
    /// named as the SDK one a caller passes (<paramref name="sdkTypeName"/>), as the SDK handlers said it.
    /// </summary>
    internal static T Parameter<T>(Request request, string name, string sdkTypeName) where T : class =>
        request.Parameters.TryGetValue(name, out var value) && value is T typed
            ? typed
            : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.Name}' is missing or not a {sdkTypeName}.");

    /// <summary>The model <c>DetectDuplicates</c>: true when <c>SuppressDuplicateDetection</c> is false (plan 2.15, ruling D1).</summary>
    /// <exception cref="XrmdFaultException">400 for a value that is not a Boolean.</exception>
    internal static bool DetectDuplicates(Request request)
    {
        if (!request.Parameters.TryGetValue(DuplicateFinder.SuppressParameter, out var value)) return false;
        return value is bool suppress
            ? !suppress
            : throw XrmdFaultException.InvalidArgument(
                $"The parameter '{DuplicateFinder.SuppressParameter}' of request '{request.Name}' must be a Boolean, got '{value ?? "null"}'.");
    }

    /// <summary>The optional <c>SolutionUniqueName</c> parameter (a Create in a solution); null when absent or empty.</summary>
    private static string? SolutionUniqueName(Request request) =>
        request.Parameters.TryGetValue("SolutionUniqueName", out var value) && value is string { Length: > 0 } name ? name : null;

    /// <summary>
    /// The component type of a created record (the model counterpart of the removed SDK <c>ComponentType.OfRow</c>): a <c>workflow</c> row is a component only as a
    /// definition.
    /// </summary>
    private static int? ComponentTypeOf(Record record) =>
        ComponentType.OfTable(record.LogicalName) is { } type
        && (type != ComponentType.Workflow || ComponentType.IsWorkflowDefinition(
            record.Attributes.TryGetValue(WorkflowSchema.TypeColumn, out var value) ? value is ChoiceValue choice ? choice.Value : value : null))
            ? type
            : null;

    /// <summary>
    /// The query of a RetrieveMultiple request, read from its <c>Query</c> parameter: a model query definition, or (through
    /// <see cref="CoreSdkBridges.ReadQuery"/>) any other value, which the SDK reader answers or refuses as today.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an invalid or unsupported query, or a missing one.</exception>
    private static QueryModel ReadQuery(Request request)
    {
        var query = request.Parameters.TryGetValue("Query", out var value) ? value : null;
        try
        {
            return query switch
            {
                RecordQuery expression => QueryExpressionReader.Read(expression),
                AttributeQuery byAttribute => QueryExpressionReader.Read(byAttribute),
                FetchQuery fetch => FetchXmlParser.Parse(fetch.Text!),
                _ => CoreSdkBridges.ReadQuery(query, request.Name),
            };
        }
        catch (XrmdValidationException ex)
        {
            throw XrmdFaultException.InvalidArgument(ex.Message);
        }
    }

    /// <summary>
    /// <paramref name="response"/> with the session token of the write of <paramref name="id"/> on an elastic table
    /// (<see cref="CoreSdkBridges.ElasticSessionTokenAsync"/>); unchanged for a standard table.
    /// </summary>
    private static async Task<Response> WithSessionTokenAsync(DataSession s, Model.Metadata.TableDefinition table,
        Guid id, Response response, CancellationToken ct)
    {
        if (await CoreSdkBridges.ElasticSessionTokenAsync(s, table, id, ct) is { } token) response.Results[Elastic.ElasticRecords.SessionToken] = token;
        return response;
    }
}
