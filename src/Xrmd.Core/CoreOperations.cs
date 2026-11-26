using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Audit;
using Xrmd.Core.Duplicates;
using Xrmd.Core.Elastic;
using Xrmd.Core.Errors;
using Xrmd.Core.Mail;
using Xrmd.Core.Query;
using Xrmd.Core.Validation;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Mail;
using Xrmd.Query;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.Solution.Layers;

namespace Xrmd.Core;

/// <summary>
/// The data operations (validation, owner, references, INSERT/UPDATE/DELETE, queries) on a <see cref="DataSession"/>.
/// It never opens a connection or a transaction nor commits: every command comes from <see cref="DataSession.Command"/>,
/// so the caller of the operation decides what shares a transaction. The caller is resolved beforehand.
/// A Create of a row that is a solution component (<see cref="ComponentType.OfTable"/>: plug-in registrations, roles, web
/// resources, views, forms, site maps, apps, process definitions but not activations) records it in the <c>Default</c> solution and, with the request's
/// <c>SolutionUniqueName</c>, in that solution too; its Delete removes it from every solution. The Delete of a
/// <c>solution</c> row deletes the container only (<see cref="SolutionDeleter"/>): its components stay, tracked by <c>Default</c>;
/// a managed solution's row is deleted here only by its uninstall (<see cref="Solutions.SolutionUninstaller"/>, which the
/// organization service runs for the Delete of a managed solution row; plan 2.6, ruling M9).
/// Create, Update (Assign and SetState with their own action), and Delete write their <c>audit</c> row through the
/// <see cref="AuditWriter"/> in the same session, so in the operation's transaction (plan 13, ruling A6).
/// </summary>
public sealed partial class CoreOperations(IMetadataStore store, QueryExecutor queryExecutor, SecurityAdministration security,
    SolutionComponentTracker tracker, FileColumnService files, AuditWriter audit, AuditMessages auditMessages,
    DuplicateRulePublisher duplicates, ICurrencyCatalog currencies, AccessMessages accessMessages, ILogger<CoreOperations>? logger = null,
    IRecordWriteObserver? writes = null, Rollups.RollupCalculator? rollups = null) : IPipelineCore
{
    private const string SystemUser = "systemuser";
    private const string SystemUserIdColumn = "systemuserid";
    private const string BusinessUnit = "businessunit";
    private const string OwnerId = "ownerid";
    private const string Team = "team";
    private const string Role = "role";
    private const string AppModule = "appmodule";
    private const string Organization = "organization";
    private const string AsyncOperation = "asyncoperation";
    private const string BusinessUnitId = "businessunitid";
    private const string ParentBusinessUnitId = "parentbusinessunitid";
    private const string ParentSystemUserId = "parentsystemuserid";
    private const string PrincipalObjectAccess = "principalobjectaccess";
    private const string IdParameter = "@_id"; // logical names start with a letter: never clashes with a column parameter

    /// <summary>The core messages: the eight CRUD messages, dispatched on the model (<see cref="ExecuteCrudAsync"/>, S.2e3b), and the
    /// messages <see cref="ExecuteCoreSdkAsync"/> implements.</summary>
    private static readonly HashSet<string> CoreMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Create", "Retrieve", "Update", "Delete", "RetrieveMultiple", "Assign", "Associate", "Disassociate",
        "GrantAccess", "ModifyAccess", "RevokeAccess", "AddPrivilegesRole", "ReplacePrivilegesRole", "RemovePrivilegeRole",
        "InitializeFileBlocksUpload", "UploadBlock", "CommitFileBlocksUpload", "DeleteFile",
        "InitializeFileBlocksDownload", "DownloadBlock", AccessTeamRules.AddUserToRecordTeam, AccessTeamRules.RemoveUserFromRecordTeam,
        ChangeTracker.MessageName, DuplicateRulePublisher.PublishMessage, DuplicateRulePublisher.UnpublishMessage,
        DuplicateFinder.RetrieveMessage, BulkDetectDuplicatesJob.Message,
        CurrencyMessages.MessageName, EmailMessages.SendEmail, EmailMessages.GetTrackingTokenEmail, ElasticUpsert.MessageName, ElasticCosmosQuery.MessageName,
        // Plan 4.1a (tasks 5 and 6): the queue messages.
        QueueSchema.AddToQueue, QueueSchema.RemoveFromQueue, QueueSchema.RouteTo, QueueSchema.AddPrincipalToQueue,
        QueueSchema.PickFromQueue, QueueSchema.ReleaseToQueue, QueueSchema.RetrieveUserQueues,
        // Plan 2.12b (R10): one rollup column of one row, calculated in the request's transaction.
        CalculateRollupFieldMessage,
        // Plan 2.14 (W21): its stage 30 is the classic workflows' hook, which the pipeline calls (it needs the pipeline to run a
        // real-time workflow); ExecuteCoreAsync refuses it.
        PipelineExecutor.ExecuteWorkflowMessage,
    };

    /// <summary>The intersect written by the role privilege messages.</summary>
    private const string RolePrivileges = "roleprivileges";

    /// <summary>The rights a share may carry (<c>principalobjectaccess.accessrightsmask</c>); Create is not one of them.</summary>
    private const AccessRights ShareableRights = AccessRights.ReadAccess | AccessRights.WriteAccess | AccessRights.AppendAccess
        | AccessRights.AppendToAccess | AccessRights.DeleteAccess | AccessRights.ShareAccess | AccessRights.AssignAccess;

    private (Guid Id, string Name)? _organization;

    private readonly SolutionDeleter _solutionDeleter = new(tracker);

    private RecordTeams? _recordTeams;

    /// <summary>The <c>activitypointer</c> row of each activity (plan 2.11, ruling A1).</summary>
    private readonly ActivityPointerSync _pointer = new(store);

    /// <summary>The <c>activityparty</c> rows of each activity (plan 2.11, rulings A9, A10).</summary>
    private readonly ActivityParties _parties = new(store);

    /// <summary>The party lists of activities, read for the plug-in images (plan 2.11) and the activity reads.</summary>
    internal ActivityParties Parties => _parties;

    /// <summary>The system job queue (plan 4.1b: the send jobs of SendEmail).</summary>
    internal Xrmd.Pipeline.Async.AsyncOperationQueue JobQueue => duplicates.Queue;

    /// <summary>The record team messages' stage 30 (plan 2.16), on this instance's data paths.</summary>
    internal RecordTeams RecordTeams => _recordTeams ??= new RecordTeams(this, store, security);

    private EmailMessages? _emailMessages;

    /// <summary>SendEmail's stage 30 (plan 2.11, A17), on this instance's Update path.</summary>
    internal EmailMessages EmailMessages =>
        _emailMessages ??= new EmailMessages(this, store, (ILogger?)logger ?? NullLogger.Instance);

    private QueueMessages? _queueMessages;

    /// <summary>The queue messages' stage 30 (plan 4.1a), on this instance's data paths.</summary>
    public QueueMessages QueueMessages => _queueMessages ??= new QueueMessages(this, store);

    private ChangeTracker? _changeTracker;

    /// <summary>The change tracking reads (plan 2.13): RetrieveEntityChanges' stage 30 and the Web API delta, on this instance's query path.</summary>
    public ChangeTracker ChangeTracker => _changeTracker ??= new ChangeTracker(this, store);

    private DuplicateFinder? _duplicateFinder;

    /// <summary>The duplicate lookups (plan 2.15): the Create/Update check and RetrieveDuplicates' stage 30, on this instance's query path.</summary>
    public DuplicateFinder DuplicateFinder => _duplicateFinder ??= new DuplicateFinder(this, store, duplicates.MatchCodes);

    /// <summary>The solution messages that queue a system job (import, uninstall, upgrade, promote): not available to plug-ins.</summary>
    private static readonly string[] SolutionJobMessages =
        ["ImportSolutionAsync", "ExecuteAsync", "UninstallSolutionAsync", "StageAndUpgradeAsync", "DeleteAndPromoteAsync"];

    /// <summary>The solution messages that xrmverse answers for the organization service's callers only, not for plug-ins
    /// (the upgrade and the promote write metadata in transactions of their own, plan 2.6 ruling M15).</summary>
    private static readonly string[] CallerOnlyMessages = [.. SolutionJobMessages, "ExportSolution", "StageAndUpgrade", "DeleteAndPromote"];

    /// <summary>The CalculateRollupField message (plan 2.12b, ruling R10).</summary>
    public const string CalculateRollupFieldMessage = "CalculateRollupField";

    /// <summary>The RetrieveAppComponents message (plan 3.5 UI-1b task 1).</summary>
    public const string RetrieveAppComponents = "RetrieveAppComponents";

    /// <summary>The RetrieveUnpublished message (plan 3.5 UI-1b task 1).</summary>
    public const string RetrieveUnpublished = "RetrieveUnpublished";

    /// <summary>The messages answered outside the pipeline: these, the access reads (<see cref="AccessMessages"/>), the metadata
    /// messages (<see cref="MetadataOperations"/>) and the audit messages (<see cref="AuditMessages"/>).</summary>
    private static readonly HashSet<string> NonPipelineMessages = new(
        ["WhoAmI", "RetrieveUserPrivileges", RetrieveAppComponents, RetrieveUnpublished, InitializeFrom.MessageName, "RetrieveEntity", "RetrieveAttribute", "ImportSolution", "RetrieveFormattedImportJobResults",
            AccessMessages.RetrievePrincipalAccess, AccessMessages.RetrieveSharedPrincipalsAndAccess, .. CallerOnlyMessages,
            .. MetadataOperations.Messages, .. AuditMessages.Messages], StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool IsCoreMessage(string messageName) => CoreMessages.Contains(messageName);

    /// <inheritdoc />
    public bool IsNonPipelineMessage(string messageName) => NonPipelineMessages.Contains(messageName);

    /// <summary>The SDK handler of <see cref="WrittenEntities(Xrmd.Model.Messages.Request)"/> (S.2e3a: the entry adapter calls it).</summary>
    /// <remarks>
    /// Associate/Disassociate over a security relationship: its intersect (<c>systemuserroles</c>, <c>teamroles</c>,
    /// <c>teammembership</c>, <c>systemuserprofiles</c>, <c>teamprofiles</c>), over a saved many-to-many relationship: its intersect entity; AddPrivilegesRole,
    /// ReplacePrivilegesRole, RemovePrivilegeRole: <c>roleprivileges</c>; AddUserToRecordTeam, RemoveUserFromRecordTeam: <c>team</c>,
    /// <c>teammembership</c> and <c>principalobjectaccess</c> (plan 2.16, ruling A9).
    /// </remarks>
    private IReadOnlyCollection<string> WrittenEntitiesSdk(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (CoreMessages.TryGetValue(request.RequestName ?? "", out var canonical) ? canonical : request.RequestName)
        {
            case "Associate" or "Disassociate":
                if (!request.Parameters.TryGetValue("Relationship", out var r) || r is not Relationship relationship) return [];
                if (SecurityAdministration.FindRelationship(relationship.SchemaName) is { } security) return [security.IntersectEntity];
                return store.GetRelationship(relationship.SchemaName ?? "") is ManyToManyRelationshipMetadata manyToMany
                    ? [manyToMany.IntersectEntityName]
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
    /// The SDK handler of <see cref="ExecuteNonPipelineAsync(Xrmd.Model.Messages.Request, Caller, DataSession, CancellationToken)"/>
    /// (S.2e3a: the entry adapter calls it).
    /// </summary>
    /// <remarks>
    /// RetrieveFormattedImportJobResults is answered (<see cref="RetrieveFormattedImportJobResultsAsync"/>, a read in the plug-in's
    /// session); the audit messages are answered by <see cref="AuditMessages"/> in the plug-in's session (so a delete rolls back
    /// with it); RetrievePrincipalAccess and RetrieveSharedPrincipalsAndAccess by <see cref="AccessMessages"/> in the plug-in's
    /// session (it sees the plug-in's uncommitted rows); ImportSolutionAsync, ExecuteAsync, UninstallSolutionAsync,
    /// StageAndUpgrade(Async), DeleteAndPromote(Async) and ExportSolution are refused (400, queueing an import or an uninstall, upgrading and exporting a solution are not
    /// replicated for plug-ins).
    /// The metadata reads are answered (<see cref="MetadataOperations.Retrieve"/>); the metadata writes and ImportSolution are refused (400):
    /// this is a plug-in's call, whose transaction has already written <c>data.*</c> rows, and metadata is always written
    /// before data in a transaction (the order that keeps the metadata lock and the SQL locks from deadlocking).
    /// </remarks>
    /// <exception cref="NotSupportedException">Any other message.</exception>
    private async Task<OrganizationResponse> ExecuteNonPipelineSdkAsync(OrganizationRequest request, Caller caller, DataSession session,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        switch (NonPipelineMessages.TryGetValue(request.RequestName ?? "", out var canonical) ? canonical : request.RequestName)
        {
            case "WhoAmI":
                return WhoAmI(caller, (await OrganizationAsync(session, ct)).Id);
            case "RetrieveUserPrivileges":
                return await RetrieveUserPrivilegesAsync(request, caller, session, ct);
            case RetrieveAppComponents:
                return await RetrieveAppComponentsAsync(request, caller, session, ct);
            case RetrieveUnpublished:
                return await RetrieveUnpublishedAsync(request, caller, session, ct);
            case InitializeFrom.MessageName:
                return await InitializeFromAsync(request, caller, session, ct);
            case "RetrieveEntity":
                return RetrieveEntity(request);
            case "RetrieveAttribute":
                return RetrieveAttribute(request);
            case "RetrieveFormattedImportJobResults":
                return await RetrieveFormattedImportJobResultsAsync(request, caller, session, ct);
            case var name when AccessMessages.IsAccessMessage(name):
                // In the plug-in's session: the access read sees its uncommitted rows (plan 3.5 UI-1b final review, I3).
                return await accessMessages.ExecuteAsync(request, caller, session, ct);
            case var name when AuditMessages.IsAuditMessage(name):
                // In the plug-in's session: the reads see its uncommitted rows, the deletes roll back with it.
                return await auditMessages.ExecuteAsync(request, caller, session, ct);
            case var name when MetadataOperations.IsWriteMessage(name) || name == "ImportSolution":
                throw XrmdFaultException.InvalidArgument(
                    $"The metadata message '{request.RequestName}' cannot be executed from a plug-in.");
            case var name when CallerOnlyMessages.Contains(name, StringComparer.OrdinalIgnoreCase):
                // Not replicated: xrmverse queues solution import jobs and exports solutions only for the organization service's callers.
                throw XrmdFaultException.InvalidArgument(
                    $"The message '{request.RequestName}' cannot be executed from a plug-in in xrmverse.");
            case var name when MetadataOperations.IsMetadataMessage(name):
                return MetadataOperations.Retrieve(store, request);
            default:
                throw new NotSupportedException($"Request '{request.RequestName}' is not supported yet.");
        }
    }

    /// <summary>
    /// RetrieveFormattedImportJobResults in <paramref name="session"/>: the <c>data</c> of the <c>importjob</c> (the import log)
    /// read as a Retrieve of <paramref name="caller"/> (read privilege and access on <c>importjob</c>; 404 when it does not
    /// exist), as an Excel workbook (<see cref="ImportJobLog.ToSpreadsheet"/>).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 without <c>ImportJobId</c>; 404 for an unknown import job; the Retrieve's faults.</exception>
    public async Task<RetrieveFormattedImportJobResultsResponse> RetrieveFormattedImportJobResultsAsync(OrganizationRequest request,
        Caller caller, DataSession session, CancellationToken ct)
    {
        var job = await RetrieveAsync(session, caller, "importjob", ImportJobIdParameter(request), new ColumnSet("data"), ct);
        return FormattedImportJobResults(job);
    }

    /// <summary>The <c>ImportJobId</c> of a RetrieveFormattedImportJobResults request.</summary>
    /// <exception cref="XrmdFaultException">400 when it is missing or empty.</exception>
    public static Guid ImportJobIdParameter(OrganizationRequest request) =>
        request.Parameters.TryGetValue("ImportJobId", out var value) && value is Guid id && id != Guid.Empty
            ? id
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'ImportJobId' of request '{request.RequestName}' is missing.");

    /// <summary>The response of RetrieveFormattedImportJobResults for the retrieved <c>importjob</c> <paramref name="job"/>.</summary>
    public static RetrieveFormattedImportJobResultsResponse FormattedImportJobResults(Entity job) => new()
    {
        ResponseName = "RetrieveFormattedImportJobResults",
        Results = { ["FormattedResults"] = ImportJobLog.ToSpreadsheet(job.GetAttributeValue<string>("data")) },
    };

    /// <summary>
    /// RetrieveUserPrivileges (plan 3.5 UI-1a task 3): the user's privileges (<see cref="UserPrivileges"/>) once
    /// <paramref name="caller"/> has read the <c>systemuser</c> row as a Retrieve would (403 without read access, 404 when the
    /// user does not exist).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 without <c>UserId</c>; the Retrieve's faults.</exception>
    public async Task<RetrieveUserPrivilegesResponse> RetrieveUserPrivilegesAsync(OrganizationRequest request, Caller caller,
        DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = request.Parameters.TryGetValue("UserId", out var value) && value is Guid id && id != Guid.Empty
            ? id
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'UserId' of request '{request.RequestName}' is missing.");
        await RetrieveAsync(session, caller, SystemUser, userId, new ColumnSet(SystemUserIdColumn), ct);
        return new RetrieveUserPrivilegesResponse
        {
            ResponseName = "RetrieveUserPrivileges",
            Results = { ["RolePrivileges"] = (await UserPrivileges.ForUserAsync(userId, session, ct)).ToArray() },
        };
    }

    /// <summary>
    /// InitializeFrom (#143): the values a new record of <c>TargetEntityName</c> starts with, taken from the record
    /// <c>EntityMoniker</c> through the maps from its table to the target table: the lookup of each one-to-many relationship between
    /// them set to the source record (unless <c>SkipParentalRelationshipMapping</c>; derived, see
    /// <see cref="InitializeFrom.ParentalRelationships"/>), and the imported <c>attributemap</c> rows, each copying a value that is not
    /// null. Only the target columns that <c>TargetFieldType</c> keeps are set; a map whose column no longer exists is skipped. The
    /// source is read as a Retrieve of <paramref name="caller"/> (403 without read access, 404 when it does not exist, field security
    /// applies); the maps are read as <see cref="Caller.Internal"/>. Runs outside the pipeline.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing parameter, an unknown table or a pair of tables without a map; the
    /// Retrieve's faults.</exception>
    public async Task<InitializeFromResponse> InitializeFromAsync(OrganizationRequest request, Caller caller, DataSession session,
        CancellationToken ct)
    {
        var parameters = InitializeFrom.Parameters(request);
        var source = EntityOf(parameters.Source.LogicalName);
        var target = EntityOf(parameters.Target);
        var relationships = InitializeFrom.ParentalRelationships(store, source.LogicalName, target.LogicalName);

        var mapQuery = new QueryExpression(AttributeMapSchema.EntityMap) { ColumnSet = new ColumnSet(AttributeMapSchema.SourceEntity) };
        mapQuery.Criteria.AddCondition(AttributeMapSchema.SourceEntity, ConditionOperator.Equal, source.LogicalName);
        mapQuery.Criteria.AddCondition(AttributeMapSchema.TargetEntity, ConditionOperator.Equal, target.LogicalName);
        var mapIds = (await RetrieveMultipleAsync(session, Caller.Internal, ReadQuery(mapQuery), ct)).Entities.Select(e => (object)e.Id).ToArray();
        if (relationships.Count == 0 && mapIds.Length == 0)
            throw XrmdFaultException.InvalidArgument($"No map exists from table '{source.LogicalName}' to table '{target.LogicalName}'.");
        var maps = new List<(AttributeMetadata From, AttributeMetadata To)>();
        if (mapIds.Length > 0)
        {
            var attributeQuery = new QueryExpression(AttributeMapSchema.AttributeMap)
            {
                ColumnSet = new ColumnSet(AttributeMapSchema.SourceAttribute, AttributeMapSchema.TargetAttribute),
            };
            attributeQuery.Criteria.AddCondition(AttributeMapSchema.EntityMapLookup, ConditionOperator.In, mapIds);
            foreach (var row in (await RetrieveMultipleAsync(session, Caller.Internal, ReadQuery(attributeQuery), ct)).Entities)
            {
                var from = source.Attributes?.FirstOrDefault(a => a.LogicalName == row.GetAttributeValue<string>(AttributeMapSchema.SourceAttribute));
                var to = target.Attributes?.FirstOrDefault(a => a.LogicalName == row.GetAttributeValue<string>(AttributeMapSchema.TargetAttribute));
                if (from is not null && to is not null) maps.Add((from, to));
            }
        }

        var columns = maps.Select(m => m.From.LogicalName).ToList();
        if (source.PrimaryNameAttribute is { } primaryName) columns.Add(primaryName);
        var record = await RetrieveAsync(session, caller, source.LogicalName, parameters.Source.Id, new ColumnSet(columns.Distinct().ToArray()), ct);

        var result = new Entity(target.LogicalName);
        if (!parameters.SkipParentalRelationshipMapping)
            foreach (var relationship in relationships)
            {
                var lookup = target.Attributes?.FirstOrDefault(a => a.LogicalName == relationship.ReferencingAttribute);
                if (lookup is null || !InitializeFrom.Keeps(lookup, parameters.FieldType)) continue;
                result[lookup.LogicalName] = new EntityReference(source.LogicalName, parameters.Source.Id)
                {
                    Name = source.PrimaryNameAttribute is { } name ? record.GetAttributeValue<string>(name) : null,
                };
            }
        foreach (var (from, to) in maps)
        {
            if (result.Contains(to.LogicalName) || !InitializeFrom.Keeps(to, parameters.FieldType)) continue;
            if (record.Attributes.TryGetValue(from.LogicalName, out var value) && value is not null) result[to.LogicalName] = value;
        }
        return new InitializeFromResponse { ResponseName = InitializeFrom.MessageName, Results = { ["Entity"] = result } };
    }

    /// <summary>The tables RetrieveUnpublished supports (Learn's bound-function list, narrowed to the tables xrmverse has).</summary>
    private static readonly string[] UnpublishedTables = ["systemform", "savedquery", "sitemap", "appmodule", "webresource"];

    /// <summary>
    /// RetrieveAppComponents: the <c>appmodulecomponent</c> rows of the app <c>AppModuleId</c> (<c>AppComponents</c>), once
    /// <paramref name="caller"/> has read the <c>appmodule</c> row as a Retrieve would (403 without read access, 404 for an unknown
    /// app). The caller needs no privilege on <c>appmodulecomponent</c>: the rows are read as <see cref="Caller.Internal"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 without <c>AppModuleId</c>; the Retrieve's and the query's faults.</exception>
    public async Task<RetrieveAppComponentsResponse> RetrieveAppComponentsAsync(OrganizationRequest request, Caller caller,
        DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var appId = request.Parameters.TryGetValue("AppModuleId", out var value) && value is Guid id && id != Guid.Empty
            ? id
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'AppModuleId' of request '{request.RequestName}' is missing.");
        await RetrieveAsync(session, caller, "appmodule", appId, new ColumnSet("appmoduleid"), ct);
        var query = new QueryExpression(AppModuleSchema.Component) { ColumnSet = new ColumnSet(true) };
        query.Criteria.AddCondition("appmoduleidunique", ConditionOperator.Equal, appId);
        var components = await RetrieveMultipleAsync(session, Caller.Internal, ReadQuery(query), ct);
        return new RetrieveAppComponentsResponse { ResponseName = RetrieveAppComponents, Results = { ["AppComponents"] = components } };
    }

    /// <summary>
    /// The row and columns of a RetrieveUnpublished request (<c>Target</c>, <c>ColumnSet</c>): xrmverse has no unpublished layer,
    /// so the message is a Retrieve of the published row.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 without <c>Target</c> or <c>ColumnSet</c>, or for a table other than systemform,
    /// savedquery, sitemap, appmodule and webresource.</exception>
    internal static (EntityReference Target, ColumnSet Columns) UnpublishedTarget(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Parameters.TryGetValue("Target", out var t) && t is EntityReference reference
            ? reference
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'Target' of request '{request.RequestName}' is missing.");
        var columns = request.Parameters.TryGetValue("ColumnSet", out var c) && c is ColumnSet columnSet
            ? columnSet
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'ColumnSet' of request '{request.RequestName}' is missing.");
        RequireUnpublishedTable(target.LogicalName);
        return (target, columns);
    }

    /// <summary>The tables RetrieveUnpublished supports, for the Web API metadata.</summary>
    public static IReadOnlyList<string> UnpublishedSupportedTables => UnpublishedTables;

    /// <exception cref="XrmdFaultException">400 for a table RetrieveUnpublished does not support.</exception>
    public static void RequireUnpublishedTable(string logicalName)
    {
        if (!UnpublishedTables.Contains(logicalName, StringComparer.OrdinalIgnoreCase))
            throw XrmdFaultException.InvalidArgument(
                $"RetrieveUnpublished is not supported for the table '{logicalName}': it supports {string.Join(", ", UnpublishedTables)}.");
    }

    /// <summary>RetrieveUnpublished in <paramref name="session"/>: the Retrieve of the published row (see <see cref="UnpublishedTarget"/>).</summary>
    public async Task<RetrieveUnpublishedResponse> RetrieveUnpublishedAsync(OrganizationRequest request, Caller caller,
        DataSession session, CancellationToken ct)
    {
        var (target, columns) = UnpublishedTarget(request);
        var row = await RetrieveAsync(session, caller, target.LogicalName, target.Id, columns, ct);
        return new RetrieveUnpublishedResponse { ResponseName = RetrieveUnpublished, Results = { ["Entity"] = row } };
    }

    /// <summary>The <see cref="WhoAmIResponse"/> of <paramref name="caller"/> in organization <paramref name="organizationId"/>.</summary>
    public static WhoAmIResponse WhoAmI(Caller caller, Guid organizationId) => new()
    {
        ResponseName = "WhoAmI",
        Results =
        {
            ["UserId"] = caller.UserId,
            ["BusinessUnitId"] = caller.BusinessUnitId,
            ["OrganizationId"] = organizationId,
        },
    };

    /// <summary>
    /// RetrieveEntity by <c>LogicalName</c>: a copy of the whole definition whatever the filters, with the relationships
    /// derived from lookups and its many-to-many relationships.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 without <c>LogicalName</c>; 404 for an unknown entity.</exception>
    public RetrieveEntityResponse RetrieveEntity(OrganizationRequest request)
    {
        // The store's instances are shared: hand out a copy.
        var entity = ExportedSettings.Entity(store, EntityDefinition(StringParameter(request, "LogicalName")));
        entity.SetRelationships(store.GetOneToManyRelationships(), store.GetManyToManyRelationships());
        return new RetrieveEntityResponse { ResponseName = "RetrieveEntity", Results = { ["EntityMetadata"] = entity } };
    }

    /// <summary>RetrieveAttribute by <c>EntityLogicalName</c> and <c>LogicalName</c>: a copy of the attribute's definition.</summary>
    /// <exception cref="XrmdFaultException">400 for a missing parameter; 404 for an unknown entity or attribute.</exception>
    public RetrieveAttributeResponse RetrieveAttribute(OrganizationRequest request)
    {
        var entity = EntityDefinition(StringParameter(request, "EntityLogicalName"));
        var name = StringParameter(request, "LogicalName");
        var attribute = entity.Attributes?.FirstOrDefault(a => a.LogicalName == name)
            ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"Could not find attribute {name} of entity {entity.LogicalName}.", 404);
        return new RetrieveAttributeResponse { ResponseName = "RetrieveAttribute", Results = { ["AttributeMetadata"] = ExportedSettings.Attribute(store, entity.LogicalName, attribute) } };
    }

    private EntityMetadata EntityDefinition(string logicalName) =>
        store.GetEntity(logicalName)
        ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"Could not find an entity with name {logicalName}.", 404);

    private static string StringParameter(OrganizationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && value is string { Length: > 0 } text
            ? text
            : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.RequestName}' is missing.");

    /// <summary>
    /// Stage 30 of Create, Retrieve, Update, Delete, RetrieveMultiple, Assign, Associate, Disassociate, GrantAccess, ModifyAccess,
    /// RevokeAccess, AddPrivilegesRole (the depth of each privilege listed is set), ReplacePrivilegesRole (the role gets exactly
    /// these privileges), RemovePrivilegeRole, AddUserToRecordTeam and RemoveUserFromRecordTeam (<see cref="Core.RecordTeams"/>), PublishDuplicateRule and
    /// UnpublishDuplicateRule (<see cref="DuplicateRulePublisher"/>), RetrieveDuplicates (<see cref="Duplicates.DuplicateFinder"/>) and
    /// BulkDetectDuplicates (<see cref="BulkDetectDuplicatesJob.EnqueueAsync"/>: the job is queued),
    /// SendEmail and GetTrackingTokenEmail (<see cref="Core.EmailMessages"/>, plans 2.11 and 4.1b), AddToQueue, RemoveFromQueue, RouteTo, AddPrincipalToQueue,
    /// PickFromQueue, ReleaseToQueue and RetrieveUserQueues (<see cref="Core.QueueMessages"/>, plan 4.1a), reading the parameters from
    /// <c>request.Parameters</c> (the plug-ins' <c>InputParameters</c>) as they are after the pre-operation stages.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing parameter, plus the faults of the operation.</exception>
    /// <exception cref="NotSupportedException">Any other message.</exception>
    /// <exception cref="InvalidOperationException">A file message without <paramref name="nested"/> (the pipeline's nested request);
    /// <paramref name="serverSetValues"/> on a message other than Create.</exception>
    /// <remarks>
    /// The SDK handler of <see cref="ExecuteCoreAsync(Xrmd.Model.Messages.Request, CoreCall, CancellationToken)"/> (S.2e3a: the entry
    /// adapter calls it). The file messages (<see cref="FileColumnService"/>): InitializeFileBlocksUpload, UploadBlock,
    /// CommitFileBlocksUpload (which writes the record with an Update run through <paramref name="nested"/>, so the Update steps fire
    /// and the guard checks it) and DeleteFile (the same Update, clearing the column).
    /// </remarks>
    /// <param name="serverSetValues">Plan 4.1c: the columns the server writes on a Create (<see cref="CreateAsync"/>); null otherwise.</param>
    private async Task<OrganizationResponse> ExecuteCoreSdkAsync(OrganizationRequest request, Caller caller, DataSession session,
        SdkNestedRequest? nested, IReadOnlyDictionary<string, object?>? serverSetValues, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (serverSetValues is not null
            && (CoreMessages.TryGetValue(request.RequestName ?? "", out var serverMessage) ? serverMessage : request.RequestName) != "Create")
            throw new InvalidOperationException($"Server values are written on a Create only, not on '{request.RequestName}'.");
        // Message names are case-insensitive (as in the step registry): switch on the canonical name.
        switch (CoreMessages.TryGetValue(request.RequestName ?? "", out var canonical) ? canonical : request.RequestName)
        {
            case ElasticUpsert.MessageName:
                return await new ElasticUpsert(this).ExecuteAsync(session, caller, Parameter<Entity>(request, "Target"), request, ct);
            case ElasticCosmosQuery.MessageName:
                UnmaskedData.Refuse(request);
                return await ElasticCosmosQuery.ExecuteAsync(this, session, caller, request, ct);
            case "GrantAccess":
                await ShareAsync(session, caller, Parameter<EntityReference>(request, "Target"), Parameter<PrincipalAccess>(request, "PrincipalAccess"),
                    replace: false, ct);
                return new GrantAccessResponse { ResponseName = "GrantAccess" };
            case "ModifyAccess":
                await ShareAsync(session, caller, Parameter<EntityReference>(request, "Target"), Parameter<PrincipalAccess>(request, "PrincipalAccess"),
                    replace: true, ct);
                return new ModifyAccessResponse { ResponseName = "ModifyAccess" };
            case "RevokeAccess":
                await RevokeAccessAsync(session, caller, Parameter<EntityReference>(request, "Target"), Parameter<EntityReference>(request, "Revokee"), ct);
                return new RevokeAccessResponse { ResponseName = "RevokeAccess" };
            case AccessTeamRules.AddUserToRecordTeam:
                return new AddUserToRecordTeamResponse
                {
                    ResponseName = AccessTeamRules.AddUserToRecordTeam,
                    Results = { ["AccessTeamId"] = await RecordTeams.AddUserAsync(session, caller, request, ct) },
                };
            case AccessTeamRules.RemoveUserFromRecordTeam:
                return new RemoveUserFromRecordTeamResponse
                {
                    ResponseName = AccessTeamRules.RemoveUserFromRecordTeam,
                    Results = { ["AccessTeamId"] = await RecordTeams.RemoveUserAsync(session, caller, request, ct) },
                };
            case CurrencyMessages.MessageName:
                return await CurrencyMessages.ExecuteAsync(request, session, ct);
            case CalculateRollupFieldMessage:
                return await CalculateRollupFieldAsync(request, caller, session, ct);
            case PipelineExecutor.ExecuteWorkflowMessage:
                // Stage 30 is IWorkflowPipelineHook.ExecuteWorkflowAsync, which the pipeline runs (plan 2.14, W21).
                throw new InvalidOperationException("ExecuteWorkflow runs through the pipeline, which hands it to the classic workflows.");
            case EmailMessages.SendEmail:
                return await EmailMessages.ExecuteAsync(request, caller, session, ct);
            case EmailMessages.GetTrackingTokenEmail:
                return await EmailMessages.GetTrackingTokenAsync(request, session, ct);
            case QueueSchema.AddToQueue or QueueSchema.RemoveFromQueue or QueueSchema.RouteTo or QueueSchema.AddPrincipalToQueue
                or QueueSchema.PickFromQueue or QueueSchema.ReleaseToQueue or QueueSchema.RetrieveUserQueues:
                // Plan 4.1a: the item and membership writes run through the pipeline, one level deeper (decision 4).
                return await QueueMessages.ExecuteAsync(request, caller, session, nested, ct);
            case ChangeTracker.MessageName:
                UnmaskedData.Refuse(request);
                return ChangeTracker.ToResponse(await ChangeTracker.RetrieveAsync(session, caller, ChangeTracker.ReadRequest(request), ct));
            case DuplicateRulePublisher.PublishMessage:
                var jobId = await duplicates.PublishAsync(session, caller, GuidParameter(request, DuplicateRulePublisher.RuleIdParameter), ct);
                return new PublishDuplicateRuleResponse { ResponseName = DuplicateRulePublisher.PublishMessage, Results = { ["JobId"] = jobId } };
            case DuplicateFinder.RetrieveMessage:
                UnmaskedData.Refuse(request);
                var duplicateCollection = await DuplicateFinder.RetrieveAsync(session, caller, Parameter<Entity>(request, "BusinessEntity"),
                    StringParameter(request, "MatchingEntityName"),
                    request.Parameters.TryGetValue("PagingInfo", out var paging) ? paging as PagingInfo : null, ct);
                return new RetrieveDuplicatesResponse { ResponseName = DuplicateFinder.RetrieveMessage, Results = { ["DuplicateCollection"] = duplicateCollection } };
            case BulkDetectDuplicatesJob.Message:
                var bulkJob = await BulkDetectDuplicatesJob.EnqueueAsync(session, caller, request, store, duplicates.MatchCodes.Cache, duplicates.Queue, ct);
                return new BulkDetectDuplicatesResponse { ResponseName = BulkDetectDuplicatesJob.Message, Results = { ["JobId"] = bulkJob } };
            case DuplicateRulePublisher.UnpublishMessage:
                await duplicates.UnpublishAsync(session, caller, GuidParameter(request, DuplicateRulePublisher.RuleIdParameter), ct);
                return new UnpublishDuplicateRuleResponse { ResponseName = DuplicateRulePublisher.UnpublishMessage };
            case "AddPrivilegesRole":
            case "ReplacePrivilegesRole":
                var replace = canonical == "ReplacePrivilegesRole";
                var roleId = GuidParameter(request, "RoleId");
                var requested = SecurityAdministration.FromSdk(Parameter<RolePrivilege[]>(request, "Privileges"));
                await DemandRowUpdateAllowedAsync(session, caller, "role", roleId, ct);
                var auditRole = await RolePrivilegesBeforeAsync(session, roleId, ct);
                await security.SetRolePrivilegesAsync(session, roleId, requested, replace, ct);
                await MarkChangedAsync(session, caller, ComponentType.Role, roleId, ct);
                await AuditRolePrivilegesAsync(session, caller, roleId, auditRole,
                    replace ? AuditActions.ReplacePrivilegesInRole : AuditActions.AddPrivilegesToRole, ct);
                return replace
                    ? new ReplacePrivilegesRoleResponse { ResponseName = "ReplacePrivilegesRole" }
                    : new AddPrivilegesRoleResponse { ResponseName = "AddPrivilegesRole" };
            case "RemovePrivilegeRole":
                var removedFrom = GuidParameter(request, "RoleId");
                await DemandRowUpdateAllowedAsync(session, caller, "role", removedFrom, ct);
                var removeBefore = await RolePrivilegesBeforeAsync(session, removedFrom, ct);
                await security.RemoveRolePrivilegeAsync(session, removedFrom, GuidParameter(request, "PrivilegeId"), ct);
                await MarkChangedAsync(session, caller, ComponentType.Role, removedFrom, ct);
                await AuditRolePrivilegesAsync(session, caller, removedFrom, removeBefore, AuditActions.RemovePrivilegesFromRole, ct);
                return new RemovePrivilegeRoleResponse { ResponseName = "RemovePrivilegeRole" };
            case "InitializeFileBlocksUpload":
                var token = await files.InitializeUploadAsync(caller, Parameter<EntityReference>(request, "Target"),
                    OptionalString(request, "FileAttributeName") ?? "", OptionalString(request, "FileName") ?? "", session, ct);
                return new InitializeFileBlocksUploadResponse { ResponseName = "InitializeFileBlocksUpload", Results = { ["FileContinuationToken"] = token } };
            case "UploadBlock":
                await files.UploadBlockAsync(caller, OptionalString(request, "FileContinuationToken") ?? "", OptionalString(request, "BlockId") ?? "",
                    new MemoryStream(Parameter<byte[]>(request, "BlockData"), writable: false), session, ct);
                return new UploadBlockResponse { ResponseName = "UploadBlock" };
            case "CommitFileBlocksUpload":
                var (fileId, size) = await files.CommitAsync(caller, OptionalString(request, "FileContinuationToken") ?? "",
                    Parameter<string[]>(request, "BlockList"), OptionalString(request, "FileName"), OptionalString(request, "MimeType"), session,
                    RecordUpdate(nested, request, session), ct);
                return new CommitFileBlocksUploadResponse
                {
                    ResponseName = "CommitFileBlocksUpload", Results = { ["FileId"] = fileId, ["FileSizeInBytes"] = size },
                };
            case "DeleteFile":
                await files.DeleteAsync(caller, GuidParameter(request, "FileId"), session, RecordUpdate(nested, request, session), ct);
                return new DeleteFileResponse { ResponseName = "DeleteFile" };
            case "InitializeFileBlocksDownload":
                var download = await files.InitializeDownloadAsync(caller, Parameter<EntityReference>(request, "Target"),
                    OptionalString(request, "FileAttributeName") ?? "", session, ct);
                return new InitializeFileBlocksDownloadResponse
                {
                    ResponseName = "InitializeFileBlocksDownload",
                    Results =
                    {
                        ["FileContinuationToken"] = download.Token, ["FileSizeInBytes"] = download.Size, ["FileName"] = download.FileName,
                        ["IsChunkingSupported"] = download.IsChunkingSupported,
                    },
                };
            case "DownloadBlock":
                await using (var block = await files.DownloadBlockAsync(caller, OptionalString(request, "FileContinuationToken") ?? "",
                                 LongParameter(request, "Offset"), LongParameter(request, "BlockLength"), session, ct))
                {
                    using var data = new MemoryStream();
                    await block.CopyToAsync(data, ct);
                    return new DownloadBlockResponse { ResponseName = "DownloadBlock", Results = { ["Data"] = data.ToArray() } };
                }
            default:
                throw new NotSupportedException($"Request '{request.RequestName}' is not supported yet.");
        }
    }

    /// <summary>A required <see cref="long"/> parameter of <paramref name="request"/>; InvalidArgument (400) when missing or of another type.</summary>
    private static long LongParameter(OrganizationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && value is long or int
            ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)
            : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.RequestName}' is missing or not a number.");

    /// <summary>The role's privileges before a change, read only when the <c>role</c> table is audited (null otherwise).</summary>
    private async Task<IReadOnlyList<RolePrivilege>?> RolePrivilegesBeforeAsync(DataSession s, Guid roleId, CancellationToken ct) =>
        await audit.IsAuditedAsync(s, EntityOf(Role), ct) ? await security.RolePrivilegesAsync(s, roleId, ct) : null;

    /// <summary>
    /// The audit row of a role privilege message on the role (ruling A1): the privileges before (<paramref name="old"/>, null when
    /// the table is not audited) and after; no row when the role's privileges did not change.
    /// </summary>
    private async Task AuditRolePrivilegesAsync(DataSession s, Caller caller, Guid roleId, IReadOnlyList<RolePrivilege>? old, int action,
        CancellationToken ct)
    {
        if (old is null) return;
        var now = await security.RolePrivilegesAsync(s, roleId, ct);
        var same = old.Count == now.Count && old.Zip(now).All(p => p.First.PrivilegeId == p.Second.PrivilegeId && p.First.Depth == p.Second.Depth);
        if (same) return;
        await audit.WriteAsync(s, caller, action, AuditOperations.Update, new EntityReference(Role, roleId), new RolePrivilegesChange(old, now), ct);
    }

    /// <summary>
    /// GrantAccess (<paramref name="replace"/> false: the rights are added to the share) and ModifyAccess (true: the share gets
    /// exactly these rights): the <c>principalobjectaccess</c> row of the record and the principal, created when missing.
    /// The row is read with UPDLOCK/HOLDLOCK, so concurrent shares of the same pair queue up; an insert that still loses the
    /// race against the unique index is retried once as an update.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an entity that is not user or team owned, a principal that is not a
    /// systemuser or team, or an invalid access mask; 404 when the record or the principal does not exist.</exception>
    public async Task ShareAsync(DataSession s, Caller caller, EntityReference target, PrincipalAccess access, bool replace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(access);
        var principal = access.Principal ?? throw XrmdFaultException.InvalidArgument("Required parameter 'PrincipalAccess.Principal' is missing.");
        var mask = access.AccessMask;
        var m = ShareableEntity(target, principal);
        ElasticWriteRules.DemandShareable(m); // plan 3.4, ruling E9
        if (mask == 0 || (mask & ~ShareableRights) != 0) throw XrmdFaultException.InvalidArgument($"Invalid access mask {mask}.");
        await EnsureShareRecordsExistAsync(s, m, target.Id, principal, ct);

        for (var attempt = 0; ; attempt++)
        {
            if (await UpdateShareAsync(s, target.Id, m.ObjectTypeCode ?? 0, principal.Id, mask, replace, ct) is { } old)
            {
                var now = replace ? mask : old | mask;
                // A grant of rights the principal already holds changes nothing: no row.
                if (now != old) await AuditShareAsync(s, caller, replace ? AuditActions.ModifyShare : AuditActions.Share, target, principal, old, now, ct);
                return;
            }
            try
            {
                // Plan 2.7a, Task 6: under a savepoint where a failed statement aborts the transaction (the next round reads on).
                await s.RecoverableAsync(() => ExecuteNonQueryAsync(s, $"""
                    INSERT INTO {s.DataTable(PrincipalObjectAccess)}
                        ({s.Quote(PrincipalObjectAccess + "id")}, principalid, principaltypecode, objectid, objecttypecode, accessrightsmask,
                         inheritedaccessrightsmask, changedon)
                    VALUES ({s.Dialect.NewGuid}, @principal, @principaltype, {IdParameter}, @otc, @mask, 0, {s.Dialect.UtcNow})
                    """,
                    [
                        new DetachedParameter("@principal", DbType.Guid, principal.Id),
                        new DetachedParameter("@principaltype", DbType.Int32, principal.LogicalName == Team ? 9 : 8),
                        new DetachedParameter(IdParameter, DbType.Guid, target.Id),
                        new DetachedParameter("@otc", DbType.Int32, m.ObjectTypeCode ?? 0),
                        new DetachedParameter("@mask", DbType.Int32, (int)mask),
                    ], ct), ct);
                await AuditShareAsync(s, caller, replace ? AuditActions.ModifyShare : AuditActions.Share, target, principal, AccessRights.None, mask, ct);
                return;
            }
            catch (DbException ex) when (s.Dialect.Classify(ex) == DbErrorKind.UniqueViolation && attempt == 0)
            {
                // A concurrent share inserted the row first: the next round updates it.
            }
        }
    }

    /// <summary>RevokeAccess: deletes the share of the record with the principal (nothing to delete is not an error).</summary>
    /// <exception cref="XrmdFaultException">400 for an entity that is not user or team owned or a principal that is not a
    /// systemuser or team; 404 when the record or the principal does not exist.</exception>
    public async Task RevokeAccessAsync(DataSession s, Caller caller, EntityReference target, EntityReference revokee, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(revokee);
        var m = ShareableEntity(target, revokee);
        await EnsureShareRecordsExistAsync(s, m, target.Id, revokee, ct);
        await using var cmd = s.Command(
            $"DELETE FROM {s.DataTable(PrincipalObjectAccess)}{s.Dialect.ReturningInline(["accessrightsmask"], RowImage.Deleted)} " +
            $"WHERE objectid = {IdParameter} AND objecttypecode = @otc AND principalid = @principal{s.Dialect.ReturningTrailing(["accessrightsmask"], RowImage.Deleted)}");
        cmd.AddParameter(IdParameter, DbType.Guid, target.Id);
        // Final review C2: the record is its id and its table; another table's record with the same id keeps its shares.
        cmd.AddParameter("@otc", DbType.Int32, m.ObjectTypeCode ?? 0);
        cmd.AddParameter("@principal", DbType.Guid, revokee.Id);
        var old = AccessRights.None;
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) old |= rd.IsDBNull(0) ? AccessRights.None : (AccessRights)rd.GetInt32(0);
        // Nothing to revoke is not a change: no row.
        if (old != AccessRights.None) await AuditShareAsync(s, caller, AuditActions.Unshare, target, revokee, old, AccessRights.None, ct);
    }

    /// <summary>
    /// The share's audit row on the shared record (ruling A1): the action follows the message (GrantAccess 14, ModifyAccess 48,
    /// RevokeAccess 49); the rights before and after are in the <see cref="ShareChange"/>.
    /// </summary>
    private Task AuditShareAsync(DataSession s, Caller caller, int action, EntityReference target, EntityReference principal,
        AccessRights old, AccessRights now, CancellationToken ct) =>
        audit.WriteAsync(s, caller, action, AuditOperations.Update, target,
            new ShareChange(new EntityReference(principal.LogicalName, principal.Id), old, now), ct);

    /// <summary>The entity of a shared record: user or team owned, and the principal a systemuser or a team.</summary>
    /// <exception cref="XrmdFaultException">400 otherwise.</exception>
    private EntityMetadata ShareableEntity(EntityReference target, EntityReference principal)
    {
        var m = EntityOf(target.LogicalName);
        if (m.OwnershipType is not (OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned))
            throw XrmdFaultException.InvalidArgument($"Entity '{m.LogicalName}' is not user or team owned and cannot be shared.");
        if (principal.LogicalName is not (SystemUser or Team))
            throw XrmdFaultException.InvalidArgument(
                $"Principal of type '{principal.LogicalName}' is not supported: a share must name a {SystemUser} or a {Team}.");
        return m;
    }

    /// <summary>The shared record and the principal must exist (read with HOLDLOCK in the share's transaction).</summary>
    /// <exception cref="XrmdFaultException">404 for the first one missing.</exception>
    private async Task EnsureShareRecordsExistAsync(DataSession s, EntityMetadata m, Guid id, EntityReference principal, CancellationToken ct)
    {
        if (!await ExistsWhereAsync(s, m.LogicalName, SystemAttributes.PrimaryIdName(m), id, ct))
            throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, id);
        var principalEntity = EntityOf(principal.LogicalName);
        if (!await ExistsWhereAsync(s, principalEntity.LogicalName, SystemAttributes.PrimaryIdName(principalEntity), principal.Id, ct))
            throw XrmdFaultException.ObjectDoesNotExist(principalEntity.LogicalName, principal.Id);
    }

    /// <summary>
    /// A unique index other than the primary key rejected the row (SQL Server error 2601; ruling P9): an alternate key or a unique
    /// value of the table, not the record's id.
    /// </summary>
    private static bool IsUniqueIndexViolation(DataSession s, DbException ex) =>
        s.Dialect.Classify(ex) == DbErrorKind.UniqueViolation && !s.Dialect.IsPrimaryKeyViolation(ex);

    /// <summary>
    /// Locks the share of the pair (UPDLOCK, HOLDLOCK: also the key range when it is missing) and, when it exists, sets its
    /// mask (<paramref name="replace"/>) or adds <paramref name="mask"/> to it; the mask it had before, or null when there is no share yet.
    /// The record is its id and its table's <paramref name="objectTypeCode"/> (final review C2): a record of another table with the
    /// same id has its own share, which this never reads or changes.
    /// </summary>
    private static async Task<AccessRights?> UpdateShareAsync(DataSession s, Guid objectId, int objectTypeCode, Guid principalId, AccessRights mask,
        bool replace, CancellationToken ct)
    {
        // Plan 2.7a, Task 6: the batch became the key lock of the share, its locking read, then its update.
        var d = s.Dialect;
        var table = s.DataTable(PrincipalObjectAccess);
        await s.KeyLockAsync(PrincipalObjectAccess, $"{objectId:N}:{objectTypeCode}:{principalId:N}", ct);
        int? current;
        await using (var read = s.Command($"""
            SELECT {d.IsNull("accessrightsmask", "0")} FROM {table}{d.LockHint(RowLock.UpdateRange)}
            WHERE objectid = {IdParameter} AND objecttypecode = @otc AND principalid = @principal{d.LockClause(RowLock.UpdateRange)}
            """))
        {
            read.AddParameter(IdParameter, DbType.Guid, objectId);
            read.AddParameter("@otc", DbType.Int32, objectTypeCode);
            read.AddParameter("@principal", DbType.Guid, principalId);
            current = await read.ExecuteScalarAsync(ct) is { } value and not DBNull ? Convert.ToInt32(value) : null;
        }
        if (current is null) return null;
        await using var cmd = s.Command($"""
            UPDATE {table} SET accessrightsmask = @mask, changedon = {d.UtcNow}
            WHERE objectid = {IdParameter} AND objecttypecode = @otc AND principalid = @principal
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, objectId);
        cmd.AddParameter("@otc", DbType.Int32, objectTypeCode);
        cmd.AddParameter("@principal", DbType.Guid, principalId);
        cmd.AddParameter("@mask", DbType.Int32, replace ? (int)mask : current.Value | (int)mask);
        await cmd.ExecuteNonQueryAsync(ct);
        return (AccessRights)current.Value;
    }

    /// <summary>The SDK handler of <see cref="LockExistsAsync(Xrmd.Model.Records.RecordReference, DataSession, CancellationToken)"/>.</summary>
    private async Task<bool> LockExistsSdkAsync(EntityReference target, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(session);
        if (session.Transaction is null) throw new InvalidOperationException("LockExistsAsync must run inside a transaction.");
        var m = EntityOf(target.LogicalName);
        await session.KeyLockAsync(m.LogicalName, target.Id, ct);
        // Plan 2.7a, Task 6: the key read with the lock clause, so that an existing row is locked on both engines.
        var key = session.Quote(SystemAttributes.PrimaryIdName(m));
        await using var cmd = session.Command(
            $"SELECT {key} FROM {session.DataTable(m.LogicalName)}{session.Dialect.LockHint(RowLock.UpdateRange)} " +
            $"WHERE {key} = {IdParameter}{session.Dialect.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter(IdParameter, DbType.Guid, target.Id);
        return await cmd.ExecuteScalarAsync(ct) is not (null or DBNull);
    }

    /// <summary>
    /// The record with <paramref name="attributes"/> (null: all; names the entity does not have are ignored); null when
    /// it does not exist.
    /// </summary>
    private async Task<Entity?> RetrieveImageSdkAsync(EntityReference target, string[]? attributes, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var m = EntityOf(target.LogicalName);
        // Plan 2.11: the party lists of an activity have no column; they are read from activityparty and added to the image.
        var partyLists = ActivitySchema.PartyLists(m).Select(p => p.Attribute).ToHashSet(StringComparer.Ordinal);
        ColumnSet columns;
        string[] parties;
        if (attributes is null)
        {
            columns = new ColumnSet(true);
            parties = [.. partyLists];
        }
        else
        {
            var known = AttributesOf(m);
            columns = new ColumnSet(attributes.Where(a => known.ContainsKey(a) && !partyLists.Contains(a)).ToArray());
            parties = attributes.Where(partyLists.Contains).ToArray();
        }

        Entity image;
        try
        {
            image = await RetrieveAsync(session, Caller.Internal, m.LogicalName, target.Id, columns, ct);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.ObjectDoesNotExist)
        {
            return null;
        }
        if (attributes is null)
        {
            // All columns: the read above already filled the party lists that have rows (ActivityRead) and left out the empty
            // ones; they are not read a second time (final review M3), the empty ones are added as empty collections.
            foreach (var name in parties)
                if (!image.Contains(name)) image[name] = new EntityCollection { EntityName = ActivitySchema.Party };
        }
        else if (parties.Length > 0)
            foreach (var (name, list) in (await _parties.ReadAsync(session, m, [target.Id], parties, ct))[target.Id])
                image[name] = list;
        return image;
    }

    /// <summary>The <see cref="Caller"/> of an existing, enabled <c>systemuser</c> (its current business unit).</summary>
    /// <exception cref="XrmdFaultException">ObjectDoesNotExist (404) when the systemuser does not exist; UserDisabled (403,
    /// <c>0x80040225</c>) when it is disabled.</exception>
    public async Task<Caller> ResolveUserAsync(Guid systemUserId, DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command(
            $"SELECT businessunitid, isdisabled FROM {session.DataTable(SystemUser)} WHERE systemuserid = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, systemUserId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(SystemUser, systemUserId);
        if (!rd.IsDBNull(1) && rd.GetBoolean(1)) throw XrmdFaultException.UserDisabled(systemUserId);
        return new Caller(systemUserId, rd.IsDBNull(0) ? Guid.Empty : rd.GetGuid(0));
    }

    /// <summary>The SYSTEM user seeded for the organization (its id derives from the organization name).</summary>
    public async Task<Guid> SystemUserIdAsync(DataSession session, CancellationToken ct) =>
        SeedOptions.DeriveSystemUserId((await OrganizationAsync(session, ct)).Name);

    /// <summary>The organization id and name (read once per instance).</summary>
    public async Task<(Guid Id, string Name)> OrganizationAsync(DataSession session, CancellationToken ct)
    {
        if (_organization is { } cached) return cached;
        await using var cmd = session.Command(session.Dialect.Limit($"SELECT organizationid, name FROM {session.DataTable("organization")}", "1"));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct))
            throw new InvalidOperationException("The organization has not been seeded (data.organization is empty).");
        _organization = (rd.GetGuid(0), rd.IsDBNull(1) ? string.Empty : rd.GetString(1));
        return _organization.Value;
    }

    /// <summary><c>organization.plugintracelogsetting</c> (0 off, 1 exception, 2 all); 1 when not set.</summary>
    public async Task<int> PluginTraceLogSettingAsync(DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command(session.Dialect.Limit($"SELECT plugintracelogsetting FROM {session.DataTable("organization")}", "1"));
        return await cmd.ExecuteScalarAsync(ct) is int setting ? setting : 1;
    }

    /// <summary>
    /// The Update of a record made by a file message, through the pipeline (its steps fire, the guard checks it); on an elastic
    /// table it names the row's stored partition (plan 3.4, <see cref="ElasticRecords.FileUpdateAsync"/>).
    /// </summary>
    private RecordUpdate RecordUpdate(SdkNestedRequest? nested, OrganizationRequest request, DataSession session) =>
        nested is null
            ? throw new InvalidOperationException($"'{request.RequestName}' writes a record through the pipeline: run it through the PipelineExecutor.")
            : async (target, ct) => await nested(await ElasticRecords.FileUpdateAsync(session, EntityOf(target.LogicalName), target, ct), ct);

    /// <summary>A string parameter of <paramref name="request"/>; null when missing or of another type.</summary>
    private static string? OptionalString(OrganizationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) ? value as string : null;

    /// <summary>A required <see cref="Guid"/> parameter of <paramref name="request"/>; InvalidArgument (400) when missing or of another type.</summary>
    internal static Guid GuidParameter(OrganizationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && value is Guid id
            ? id
            : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.RequestName}' is missing or not a Guid.");

    /// <summary>A required parameter of <paramref name="request"/>; InvalidArgument (400) when missing or of another type.</summary>
    internal static T Parameter<T>(OrganizationRequest request, string name) where T : class =>
        request.Parameters.TryGetValue(name, out var value) && value is T typed
            ? typed
            : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.RequestName}' is missing or not a {typeof(T).Name}.");

    /// <summary>
    /// The <c>usersettings</c> row of a new <c>systemuser</c> (plan 2.10): keyed by the user's id, owned by the user, in the
    /// user's business unit, without a currency of its own (the base one applies), with the default time zone and formats (#131),
    /// unless the user already has one (a row left by an earlier user with the same id is kept). The seed backfills the users it did
    /// not create.
    /// </summary>
    private async Task InsertUserSettingsAsync(DataSession s, Caller caller, Guid userId, CancellationToken ct)
    {
        if (store.GetEntity(CurrencySchema.UserSettings) is not { } settings) return;
        // #131 (ruling Z2): UTC and the en-US formats, for the columns the table has.
        var defaults = TimeZoneSchema.UserSettingsDefaults.Where(d => settings.Attributes?.Any(a => a.LogicalName == d.Column) == true).ToList();
        // Plan 4.1c: the user's incoming email filtering, From contacts and accounts, when the table has the column.
        var filtering = settings.Attributes?.Any(a => a.LogicalName == MailSchema.FilteringMethodColumn) == true
            ? (Column: ", " + MailSchema.FilteringMethodColumn, Value: ", " + MailSchema.UserFilterDefault.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : (Column: "", Value: "");
        await s.KeyLockAsync(CurrencySchema.UserSettings, userId, ct);
        await using var cmd = s.Command($"""
            INSERT INTO {s.DataTable(CurrencySchema.UserSettings)} ({s.Quote(SystemAttributes.PrimaryIdName(settings))}, ownerid,
                owneridtype, owninguser, owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode,
                {AppModuleSchema.PagingLimit}, {AppModuleSchema.UserColorScheme}{filtering.Column}{string.Concat(defaults.Select(d => ", " + d.Column))})
            SELECT u.systemuserid, u.systemuserid, @_type, u.systemuserid, u.businessunitid,
                {s.Dialect.UtcNow}, @_by, {s.Dialect.UtcNow}, @_by, 0, 1, {AppModuleSchema.DefaultPagingLimit}, {AppModuleSchema.DefaultColorScheme}{filtering.Value}{string.Concat(defaults.Select((_, i) => $", @_tz{i}"))}
            FROM {s.DataTable(SystemUser)} u WHERE u.systemuserid = {IdParameter}
                AND NOT EXISTS (SELECT 1 FROM {s.DataTable(CurrencySchema.UserSettings)} s{s.Dialect.LockHint(RowLock.UpdateRange)}
                    WHERE s.{s.Quote(SystemAttributes.PrimaryIdName(settings))} = u.systemuserid)
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, userId);
        cmd.AddParameter("@_by", DbType.Guid, caller.UserId);
        cmd.AddParameter("@_type", DbType.Int32, store.GetEntity(SystemUser)?.ObjectTypeCode ?? 8);
        for (var i = 0; i < defaults.Count; i++) cmd.AddParameter($"@_tz{i}", DbValueTypes.Of(defaults[i].Value), defaults[i].Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Plan 4.1a: the default queue of a new user or owner team (an access or group team gets none): a private queue named
    /// <c>&lt;fullname&gt;</c> or <c>&lt;team name&gt;</c> (from memory, checklist 4), owned by the principal, written as an ordinary
    /// Create of <c>queue</c> in this session, then linked from the principal's <c>queueid</c>. A user is also a member of its queue
    /// (a <c>queuemembership</c> row); a team's members are members at read time (task 4), so a team has no row. Not written inside
    /// a migration (the source's own queues are imported) or when the organization has no <c>queue</c> table.
    /// </summary>
    /// <returns>The id of the new queue, or <see cref="Guid.Empty"/> when the principal gets none.</returns>
    internal async Task<Guid> InsertDefaultQueueAsync(DataSession s, Caller caller, TableDefinition principal, Guid principalId, CancellationToken ct)
    {
        if (MigrationScope.IsActive || store.GetEntity(QueueSchema.Queue) is null) return Guid.Empty;
        var isTeam = principal.LogicalName == Team;
        string? name;
        await using (var cmd = s.Command(isTeam
                         ? $"SELECT {s.Quote("name")}, {s.Quote("teamtype")} FROM {s.DataTable(Team)} WHERE {s.Quote("teamid")} = {IdParameter}"
                         : $"SELECT {s.Quote("fullname")}, 0 FROM {s.DataTable(SystemUser)} WHERE {s.Quote("systemuserid")} = {IdParameter}"))
        {
            cmd.AddParameter(IdParameter, DbType.Guid, principalId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Guid.Empty;
            name = rd.IsDBNull(0) ? null : rd.GetString(0);
            if (!rd.IsDBNull(1) && Convert.ToInt32(rd.GetValue(1)) != 0) return Guid.Empty;
        }
        var queueId = await CreateRecordAsync(s, caller, new Record(QueueSchema.Queue)
        {
            ["name"] = QueueSchema.DefaultQueueName(name),
            ["queueviewtype"] = new ChoiceValue(QueueSchema.PrivateViewType),
            ["queuetypecode"] = new ChoiceValue(QueueSchema.TypeCode),
            [OwnerId] = Reference(principal.LogicalName!, principalId),
        }, ct);
        await ExecuteNonQueryAsync(s,
            $"UPDATE {s.DataTable(principal.LogicalName!)} SET {s.Quote(QueueSchema.QueueIdColumn)} = @queue " +
            $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(principal))} = {IdParameter}",
            [new DetachedParameter("@queue", DbType.Guid, queueId), new DetachedParameter(IdParameter, DbType.Guid, principalId)], ct);
        if (!isTeam && store.GetEntity(QueueSchema.QueueMembership) is { } membership)
            await ExecuteNonQueryAsync(s, $"""
                INSERT INTO {s.DataTable(membership.LogicalName)} ({s.Quote(SystemAttributes.PrimaryIdName(membership))}, {s.Quote(QueueSchema.QueueIdColumn)},
                    {s.Quote("systemuserid")}, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                VALUES ({s.Dialect.NewGuid}, @queue, {IdParameter}, {s.Dialect.UtcNow}, @caller, {s.Dialect.UtcNow}, @caller, 0, 1)
                """,
                [
                    new DetachedParameter("@queue", DbType.Guid, queueId), new DetachedParameter(IdParameter, DbType.Guid, principalId),
                    new DetachedParameter("@caller", DbType.Guid, caller.UserId),
                ], ct);
        return queueId;
    }

    /// <summary>
    /// The child rows of a row component that are written with it (plan 2.6, ruling M4): the images of a step and the
    /// parameters and response properties of a custom API, by table, with the column naming their component and its type.
    /// </summary>
    private static readonly Dictionary<string, (string Column, int Type)> ComponentChildren = new(StringComparer.Ordinal)
    {
        ["sdkmessageprocessingstepimage"] = ("sdkmessageprocessingstepid", ComponentType.SdkMessageProcessingStep),
        [CustomApiTables.RequestParameter] = (CustomApiTables.ApiId, ComponentType.CustomApi),
        [CustomApiTables.ResponseProperty] = (CustomApiTables.ApiId, ComponentType.CustomApi),
    };

    /// <summary>
    /// An unmanaged write of a component (plan 2.6, ruling M4): its Active marker when it has a System or Managed layer;
    /// nothing for an unlayered component or for <see cref="Caller.Internal"/> (the layer operations).
    /// </summary>
    private Task MarkChangedAsync(DataSession s, Caller caller, int componentType, Guid id, CancellationToken ct) =>
        caller == Caller.Internal ? Task.CompletedTask : tracker.MarkChangedAsync(s, componentType, id, ct);

    /// <summary>
    /// The Create, Update or Delete of a child row of a component (<see cref="ComponentChildren"/>) changes that component:
    /// its Active marker when it is layered. <paramref name="parentId"/> is the component read before a Delete; otherwise it
    /// is read from the row.
    /// </summary>
    private async Task MarkParentChangedAsync(DataSession s, Caller caller, string? table, Guid id, Guid? parentId, CancellationToken ct)
    {
        if (caller == Caller.Internal || table is null || !ComponentChildren.TryGetValue(table, out var parent)) return;
        parentId ??= await ParentComponentAsync(s, table, id, ct);
        if (parentId is { } component) await tracker.MarkChangedAsync(s, parent.Type, component, ct);
    }

    /// <summary>
    /// The managed rules of a row Update (plan 2.6): the row of a managed solution changes only through the solution
    /// operations (ruling M14); a component, or the component a child row belongs to, delivered by a managed solution with
    /// <c>IsCustomizable</c> false is not changed (ruling M13). The layer operations bypass them (ruling R2).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the component or solution.</exception>
    /// <param name="target">The Update's target: a child row moved to another component (its parent lookup changed) needs
    /// that component customizable too.</param>
    private async Task DemandRowUpdateAllowedAsync(DataSession s, Caller caller, string table, Guid id, CancellationToken ct,
        Record? target = null)
    {
        if (ManagedProperties.Bypassed(caller)) return;
        if (table == SolutionDeleter.Table)
        {
            await DemandUnmanagedSolutionAsync(s, id, ct);
            return;
        }
        if (ComponentType.OfTable(table) is { } type && await ManagedProperties.StateAsync(s, type, id, ct) is { Flags.IsCustomizable: false } state)
            throw ManagedProperties.NotCustomizable($"{table} '{id}'", state);
        if (!ComponentChildren.TryGetValue(table, out var parent)) return;
        await DemandParentCustomizableAsync(s, table, await ParentComponentAsync(s, table, id, ct), ct);
        if (target is not null && target.Attributes.TryGetValue(parent.Column, out var moved) && moved is RecordReference newParent)
            await DemandParentCustomizableAsync(s, table, newParent.Id, ct);
    }

    /// <summary>
    /// The managed rules of a row Delete (plan 2.6): a managed component is not deleted directly, its solution is uninstalled
    /// (rulings M13 <c>CanBeDeleted</c> and M14); a child row of a component delivered with <c>IsCustomizable</c> false stays.
    /// The layer operations bypass them (ruling R2).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the component and its solution.</exception>
    private async Task DemandRowDeleteAllowedAsync(DataSession s, Caller caller, string table, Guid id, CancellationToken ct)
    {
        if (ManagedProperties.Bypassed(caller)) return;
        if (ComponentType.OfTable(table) is { } type && await ManagedProperties.StateAsync(s, type, id, ct) is { } state)
            throw ManagedProperties.NotDeletable($"{table} '{id}'", state);
        if (ComponentChildren.ContainsKey(table)) await DemandParentCustomizableAsync(s, table, await ParentComponentAsync(s, table, id, ct), ct);
    }

    /// <summary>400 when the component <paramref name="parentId"/> a child row of <paramref name="table"/> belongs to is
    /// delivered by a managed solution with <c>IsCustomizable</c> false (ruling M13).</summary>
    private async Task DemandParentCustomizableAsync(DataSession s, string table, Guid? parentId, CancellationToken ct)
    {
        if (parentId is not { } parent || !ComponentChildren.TryGetValue(table, out var owner)) return;
        if (await ManagedProperties.StateAsync(s, owner.Type, parent, ct) is { Flags.IsCustomizable: false } state)
            throw ManagedProperties.NotCustomizable($"{ComponentType.TableOf(owner.Type)} '{parent}'", state);
    }

    /// <summary>The table column of a form or view row, and the managed flag of the table that allows creating one (M13).</summary>
    private static readonly Dictionary<string, (string Column, string Flag, string What)> TableBoundRows = new(StringComparer.Ordinal)
    {
        ["systemform"] = ("objecttypecode", "CanCreateForms", "form"),
        ["savedquery"] = ("returnedtypecode", "CanCreateViews", "view"),
        // Plan 3.5 UI-1f: a system chart of a table, allowed by the table's CanCreateCharts.
        [ChartSchema.Table] = (ChartSchema.PrimaryEntityTypeCode, "CanCreateCharts", "chart"),
    };

    /// <summary>400 for the row of a managed solution (ruling M14): only importing, upgrading and uninstalling change it.</summary>
    private static async Task DemandUnmanagedSolutionAsync(DataSession s, Guid solutionId, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT uniquename FROM {s.DataTable(SolutionDeleter.Table)} WHERE solutionid = {IdParameter} AND ismanaged = {s.Dialect.BooleanLiteral(true)}");
        cmd.AddParameter(IdParameter, DbType.Guid, solutionId);
        if (await cmd.ExecuteScalarAsync(ct) is string name)
            throw XrmdFaultException.InvalidArgument(
                $"The solution '{name}' is managed: its row changes only by importing, upgrading or uninstalling the solution, not by an update.");
    }

    /// <summary>The component a child row of <paramref name="table"/> belongs to; null for another table or no parent.</summary>
    private async Task<Guid?> ParentComponentAsync(DataSession s, string table, Guid id, CancellationToken ct)
    {
        if (!ComponentChildren.TryGetValue(table, out var parent) || store.GetEntity(table) is not { } m) return null;
        await using var cmd = s.Command(
            $"SELECT {s.Quote(parent.Column)} FROM {s.DataTable(table)} WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary><c>organization.maxuploadfilesize</c>, its default (5 MB) when not set.</summary>
    internal static async Task<int> MaxUploadFileSizeAsync(DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command(
            s.Dialect.Limit($"SELECT {s.Quote(ActivitySchema.MaxUploadFileSize)} FROM {s.DataTable("organization")}", "1"));
        return await cmd.ExecuteScalarAsync(ct) is int value ? value : ActivitySchema.DefaultMaxUploadFileSize;
    }

    /// <summary>
    /// Plan 2.15: a write of a duplicate rule, of one of its conditions or of the organization (its duplicate detection switch)
    /// drops the cached rules now and after the transaction's outcome.
    /// </summary>
    private void InvalidateDuplicateRulesOnWrite(DataSession s, string? table)
    {
        if (table is DuplicateDetectionSchema.Rule or DuplicateDetectionSchema.Condition or Organization)
            duplicates.MatchCodes.Cache.InvalidateWith(s);
    }

    private static async Task<bool> ExistsWhereAsync(DataSession s, string table, string column, Guid value, CancellationToken ct)
    {
        await s.KeyLockAsync(table, value, ct);
        await using var cmd = s.Command(
            s.Dialect.Limit($"SELECT 1 FROM {s.DataTable(table)}{s.Dialect.LockHint(RowLock.Shared)} WHERE {s.Quote(column)} = {IdParameter}", "1")
            // Plan 2.7a, Task 6: the trailing clause takes the row lock on PostgreSQL (FOR SHARE after the LIMIT), so that a writer
            // holding the row (a record team add on its template) is waited for, as SQL Server's HOLDLOCK does.
            + s.Dialect.LockClause(RowLock.Shared));
        cmd.AddParameter(IdParameter, DbType.Guid, value);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private const string OwnManager = "A user cannot be their own manager (parentsystemuserid).";

    /// <summary>
    /// The manager hierarchy (<c>systemuser.parentsystemuserid</c>, plan 2.5 ruling H5) has no loop: walks up from
    /// <paramref name="newManager"/>; reaching <paramref name="id"/> means the hierarchy would loop, and reaching a user already
    /// visited means the chain above <paramref name="newManager"/> already loops (written outside the API); both are refused, with
    /// their own message.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static async Task EnsureNoManagerLoopAsync(DataSession s, Guid id, Guid newManager, CancellationToken ct)
    {
        if (newManager == id) throw XrmdFaultException.InvalidArgument(OwnManager);
        var visited = new HashSet<Guid>();
        Guid? cursor = newManager;
        while (cursor is { } node)
        {
            if (node == id)
                throw XrmdFaultException.InvalidArgument(
                    $"The manager hierarchy cannot contain a loop: making user {newManager} the manager of user {id} would close one.");
            // A loop that does not contain id was written outside the API (e.g. SQL): refusing is right, but the cause differs.
            if (!visited.Add(node))
                throw XrmdFaultException.InvalidArgument(
                    $"The manager hierarchy cannot contain a loop: the manager chain above user {newManager} already loops " +
                    $"(at user {node}); correct it before making user {newManager} the manager of user {id}.");
            await using var cmd = s.Command(
                $"SELECT {s.Quote(ParentSystemUserId)} FROM {s.DataTable(SystemUser)}{s.Dialect.LockHint(RowLock.Shared)} WHERE systemuserid = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
            cmd.AddParameter(IdParameter, DbType.Guid, node);
            var next = await cmd.ExecuteScalarAsync(ct);
            cursor = next is Guid g ? g : null;
        }
    }

    /// <summary>Ruling F3 for a lookup that filters on <paramref name="columns"/> (<see cref="QueryExecutor.DemandFilterableAsync"/>).</summary>
    /// <exception cref="XrmdFaultException">403 FieldReadDenied.</exception>
    public Task DemandFilterableAsync(Caller caller, IEnumerable<(string Entity, string Attribute)> columns, CancellationToken ct) =>
        queryExecutor.DemandFilterableAsync(caller, columns, ct);

    /// <summary>Reads a <see cref="QueryExpression"/>, <see cref="QueryByAttribute"/> or <see cref="FetchExpression"/> into the query model.</summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for an invalid or unsupported query.</exception>
    public static QueryModel ReadQuery(QueryBase query)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            return query switch
            {
                QueryExpression expression => QueryExpressionReader.Read(expression),
                QueryByAttribute byAttribute => QueryExpressionReader.Read(byAttribute),
                FetchExpression fetch => FetchXmlParser.Parse(fetch.Query),
                _ => throw XrmdFaultException.InvalidArgument($"Query type '{query.GetType().Name}' is not supported."),
            };
        }
        catch (XrmdValidationException ex)
        {
            throw XrmdFaultException.InvalidArgument(ex.Message);
        }
    }

    /// <summary>The metadata store the operations read.</summary>
    internal IMetadataStore Store => store;

    /// <summary>The elastic tables (plan 3.4, <see cref="Elastic.ElasticTtl"/>).</summary>
    internal IEnumerable<EntityMetadata> ElasticEntities() => store.GetAllEntities().Where(Metadata.ElasticTables.IsElastic);

    /// <summary>The entity definition; InvalidArgument (400) when unknown.</summary>
    internal EntityMetadata EntityOf(string? logicalName) =>
        (logicalName is null ? null : store.GetEntity(logicalName))
        // Exact Dataverse text: client code matches on "was not found in the MetadataCache" to detect an unknown table.
        ?? throw XrmdFaultException.InvalidArgument(
            $"The entity with a name = '{logicalName}' with namemapping = 'Logical' was not found in the MetadataCache.");

    /// <summary>
    /// CalculateRollupField's stage 30 (plan 2.12b, ruling R10): calculates <c>FieldName</c> of the row <c>Target</c> only, as the
    /// system (R12: no row or field security on the sources), in <paramref name="s"/>'s transaction, and returns the row with the
    /// rollup's value, <c>_date</c>, <c>_state</c> (and <c>_base</c> for Currency), each column the caller cannot read (field security
    /// of the rollup, which its companions follow) set to null. The guard has demanded Write on the row before (0x8004E027).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing parameter or an unknown table; 400 0x80060554 for a column that is not a
    /// rollup; 404 for a row that does not exist; 400 0x80060547 / 0x8004E025 above the online limits (R6).</exception>
    private async Task<OrganizationResponse> CalculateRollupFieldAsync(OrganizationRequest request, Caller caller, DataSession s, CancellationToken ct)
    {
        var target = Parameter<EntityReference>(request, "Target");
        var field = StringParameter(request, "FieldName");
        var table = NormalizeRollupTarget(target);
        // Never a silent no-op: a host without the calculator cannot answer the message.
        if (rollups is null)
            throw new XrmdFaultException(ErrorCodes.Unexpected, $"{CalculateRollupFieldMessage} is not available: the rollup calculator is not configured.", 500);
        var entity = await rollups.CalculateOneAsync(s, target, field, ct);
        if (caller != Caller.Internal)
        {
            var hidden = (await AuditFieldSecurity.HiddenAsync(queryExecutor.Fields, caller, [(table.LogicalName, target.Id)], s, ct))(table.LogicalName, target.Id);
            foreach (var column in entity.Attributes.Keys.Where(hidden.Contains).ToList()) entity[column] = null;
        }
        return new CalculateRollupFieldResponse { ResponseName = CalculateRollupFieldMessage, Results = { ["Entity"] = entity } };
    }

    /// <summary>
    /// The row a <c>CalculateRollupField</c> names, normalised before the security check reads it (<see cref="OrganizationService"/>)
    /// and again by the core (any other way into the pipeline), so both read the same id: key attributes are refused on a standard
    /// table (400, xrmverse has no alternate keys there); on an elastic table a primary-id key fills an empty <see cref="EntityReference.Id"/>
    /// (400 when it differs from the id given). Returns the table.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown table or a refused key.</exception>
    internal EntityMetadata NormalizeRollupTarget(EntityReference target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var m = EntityOf(target.LogicalName);
        ElasticRecordKey.Of(m, target, null);
        ElasticRecordKey.Normalize(m, target);
        return m;
    }

    private static Dictionary<string, AttributeMetadata> AttributesOf(EntityMetadata m) =>
        (m.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);

    private static async Task<int> ExecuteNonQueryAsync(
        DataSession s, string sql, IEnumerable<DbParameter> parameters, CancellationToken ct)
    {
        await using var cmd = s.Command(sql);
        cmd.AddParameters(parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
