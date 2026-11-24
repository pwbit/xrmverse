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
using Xrmd.Model.Query;
using Xrmd.Pipeline;
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
/// A Create of a row that is a solution component (<see cref="ComponentType.OfRow"/>: plug-in registrations, roles, web
/// resources, views, forms, site maps, apps, process definitions but not activations) records it in the <c>Default</c> solution and, with the request's
/// <c>SolutionUniqueName</c>, in that solution too; its Delete removes it from every solution. The Delete of a
/// <c>solution</c> row deletes the container only (<see cref="SolutionDeleter"/>): its components stay, tracked by <c>Default</c>;
/// a managed solution's row is deleted here only by its uninstall (<see cref="Solutions.SolutionUninstaller"/>, which the
/// organization service runs for the Delete of a managed solution row; plan 2.6, ruling M9).
/// Create, Update (Assign and SetState with their own action), and Delete write their <c>audit</c> row through the
/// <see cref="AuditWriter"/> in the same session, so in the operation's transaction (plan 13, ruling A6).
/// </summary>
public sealed class CoreOperations(IMetadataStore store, QueryExecutor queryExecutor, SecurityAdministration security,
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

    /// <summary>The messages <see cref="ExecuteCoreAsync"/> implements.</summary>
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

    /// <inheritdoc />
    /// <remarks>
    /// Associate/Disassociate over a security relationship: its intersect (<c>systemuserroles</c>, <c>teamroles</c>,
    /// <c>teammembership</c>, <c>systemuserprofiles</c>, <c>teamprofiles</c>), over a saved many-to-many relationship: its intersect entity; AddPrivilegesRole,
    /// ReplacePrivilegesRole, RemovePrivilegeRole: <c>roleprivileges</c>; AddUserToRecordTeam, RemoveUserFromRecordTeam: <c>team</c>,
    /// <c>teammembership</c> and <c>principalobjectaccess</c> (plan 2.16, ruling A9).
    /// </remarks>
    public IReadOnlyCollection<string> WrittenEntities(OrganizationRequest request)
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

    /// <inheritdoc />
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
    public async Task<OrganizationResponse> ExecuteNonPipelineAsync(OrganizationRequest request, Caller caller, DataSession session,
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
    /// <exception cref="InvalidOperationException">A file message, which needs the pipeline's <see cref="NestedRequest"/>.</exception>
    public Task<OrganizationResponse> ExecuteCoreAsync(OrganizationRequest request, Caller caller, DataSession session, CancellationToken ct) =>
        ExecuteCoreAsync(request, caller, session, null, ct);

    /// <inheritdoc cref="ExecuteCoreAsync(OrganizationRequest, Caller, DataSession, CancellationToken)"/>
    /// <remarks>
    /// The file messages (<see cref="FileColumnService"/>): InitializeFileBlocksUpload, UploadBlock, CommitFileBlocksUpload (which
    /// writes the record with an Update run through <paramref name="nested"/>, so the Update steps fire and the guard checks it)
    /// and DeleteFile (the same Update, clearing the column).
    /// </remarks>
    public Task<OrganizationResponse> ExecuteCoreAsync(OrganizationRequest request, Caller caller, DataSession session,
        NestedRequest? nested, CancellationToken ct) =>
        ExecuteCoreAsync(request, caller, session, nested, null, ct);

    /// <inheritdoc cref="ExecuteCoreAsync(OrganizationRequest, Caller, DataSession, NestedRequest?, CancellationToken)"/>
    /// <param name="serverSetValues">Plan 4.1c: the columns the server writes on a Create (<see cref="CreateAsync"/>); null otherwise.</param>
    /// <exception cref="InvalidOperationException"><paramref name="serverSetValues"/> on a message other than Create.</exception>
    public async Task<OrganizationResponse> ExecuteCoreAsync(OrganizationRequest request, Caller caller, DataSession session,
        NestedRequest? nested, IReadOnlyDictionary<string, object?>? serverSetValues, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (serverSetValues is not null
            && (CoreMessages.TryGetValue(request.RequestName ?? "", out var serverMessage) ? serverMessage : request.RequestName) != "Create")
            throw new InvalidOperationException($"Server values are written on a Create only, not on '{request.RequestName}'.");
        // Message names are case-insensitive (as in the step registry): switch on the canonical name.
        switch (CoreMessages.TryGetValue(request.RequestName ?? "", out var canonical) ? canonical : request.RequestName)
        {
            case "Create":
                var created = Parameter<Entity>(request, "Target");
                var id = await CreateAsync(session, caller, created, ct, DetectDuplicates(request), serverSetValues);
                if (ComponentType.OfRow(created) is { } componentType)
                    await tracker.TrackAsync(session, componentType, id, SolutionUniqueName(request), ct, markActive: caller != Caller.Internal);
                await MarkParentChangedAsync(session, caller, created.LogicalName, id, null, ct);
                return await ElasticRecords.WithSessionTokenAsync(session, EntityOf(created.LogicalName), id,
                    new CreateResponse { ResponseName = "Create", Results = { ["id"] = id } }, ct);
            case "Retrieve":
                UnmaskedData.Refuse(request);
                var target = Parameter<EntityReference>(request, "Target");
                var columns = request.Parameters.TryGetValue("ColumnSet", out var c) ? c as ColumnSet : null;
                var entity = await RetrieveAsync(session, caller, target.LogicalName, target.Id, columns, ct,
                    ElasticRecordKey.Of(EntityOf(target.LogicalName), target, request));
                return new RetrieveResponse { ResponseName = "Retrieve", Results = { ["Entity"] = entity } };
            case "Update":
                var updated = Parameter<Entity>(request, "Target");
                var updatedTable = EntityOf(updated.LogicalName);
                // Plan 3.4: an elastic row is found in its partition first (404), under an update lock.
                await ElasticRecords.BeforeUpdateAsync(session, updatedTable, updated, request, ct);
                // SetState runs as an Update the pipeline marks with its own audit action (ruling A6).
                await UpdateAsync(session, caller, updated, AuditActionHint.Of(request) ?? AuditActions.Update, ct, DetectDuplicates(request));
                return await ElasticRecords.WithSessionTokenAsync(session, updatedTable,
                    UpdateId(updated, updatedTable, SystemAttributes.PrimaryIdName(updatedTable)), new UpdateResponse { ResponseName = "Update" }, ct);
            case "Delete":
                var deleted = Parameter<EntityReference>(request, "Target");
                var deletedId = await ElasticRecords.BeforeDeleteAsync(session, EntityOf(deleted.LogicalName), deleted, request, ct);
                await DeleteAsync(session, caller, deleted.LogicalName, deletedId, nested, ct);
                return new DeleteResponse { ResponseName = "Delete" };
            case ElasticUpsert.MessageName:
                return await new ElasticUpsert(this).ExecuteAsync(session, caller, Parameter<Entity>(request, "Target"), request, ct);
            case ElasticCosmosQuery.MessageName:
                UnmaskedData.Refuse(request);
                return await ElasticCosmosQuery.ExecuteAsync(this, session, caller, request, ct);
            case "RetrieveMultiple":
                UnmaskedData.Refuse(request);
                // A front end that built the query model itself passes it along; a plug-in that replaced Query wins.
                var model = request is RetrieveMultipleModelRequest withModel && withModel.UsesModel
                    ? withModel.Model
                    : ReadQuery(Parameter<QueryBase>(request, "Query"));
                model = ElasticQueryRules.ApplyRequest(model, request, store);
                var collection = await RetrieveMultipleAsync(session, caller, model, ct);
                return new RetrieveMultipleResponse { ResponseName = "RetrieveMultiple", Results = { ["EntityCollection"] = collection } };
            case "Assign":
                var assigned = Parameter<EntityReference>(request, "Target");
                await AssignAsync(session, caller, assigned, Parameter<EntityReference>(request, "Assignee"), ct);
                return new AssignResponse { ResponseName = "Assign" };
            case "Associate":
            case "Disassociate":
                var associate = canonical == "Associate";
                await AssociateAsync(session, caller, Parameter<EntityReference>(request, "Target"),
                    Parameter<Relationship>(request, "Relationship"), Parameter<EntityReferenceCollection>(request, "RelatedEntities"),
                    associate, ct);
                return associate
                    ? new AssociateResponse { ResponseName = "Associate" }
                    : new DisassociateResponse { ResponseName = "Disassociate" };
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

    /// <summary>
    /// Assign: the Update of <c>ownerid</c> to <paramref name="assignee"/> (same rules as an Update of the owner: a
    /// <c>systemuser</c> or an owner <c>team</c>, the owning columns follow, modifiedon/modifiedby set).
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) when the entity has no owner or the assignee is not a
    /// systemuser or owner team; ObjectDoesNotExist (404) when the record or the user does not exist.</exception>
    public Task AssignAsync(DataSession s, Caller caller, EntityReference target, EntityReference assignee, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(assignee);
        return UpdateAsync(s, caller, new Entity(target.LogicalName, target.Id) { [OwnerId] = assignee }, AuditActions.Assign, ct);
    }

    /// <summary>
    /// Associate/Disassociate over a security relationship (<see cref="SecurityAdministration.FindRelationship"/>:
    /// <c>systemuserroles_association</c>, <c>teamroles_association</c>, <c>teammembership_association</c>, the target on
    /// either side) writes its intersect through <see cref="SecurityAdministration"/>. Over a saved many-to-many relationship
    /// (<see cref="IMetadataStore.GetRelationship"/>) it inserts or deletes the intersect rows of (target, related record),
    /// see <see cref="AssociateManyToManyAsync"/>. Otherwise over a one-to-many relationship derived from a lookup
    /// (<see cref="IMetadataStore.GetOneToManyRelationships"/>): the lookup of the N side is set to the 1 side
    /// (Associate) or cleared (Disassociate; only where it points to that record, the target must exist), with an Update
    /// per record on <paramref name="s"/> (one transaction). Owner lookups (ownerid/owninguser/owningteam) are excluded. The
    /// target is either the referenced record (the related records are the referencing ones) or the referencing record
    /// (then exactly one related record, the referenced one). The reads of Disassociate run as <paramref name="caller"/>.
    /// The privilege checks (Append, AppendTo) belong to the pipeline's security guard, not to this operation.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>Relationship '{name}' is not supported</c> for an unknown, many-to-many or
    /// read-only or owner relationship; 400 when the records are not of the relationship's entities; 404 for a missing record;
    /// for a security or many-to-many relationship, 412 DuplicateRecord for an association that already exists; for a security
    /// relationship, 400 for a role assigned to a team that is not an owner team;
    /// 403 PrivilegeDenied/AccessDenied (Read) when Disassociate reads a record the caller cannot see.</exception>
    public async Task AssociateAsync(DataSession s, Caller caller, EntityReference target, Relationship relationship,
        EntityReferenceCollection related, bool associate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(relationship);
        ArgumentNullException.ThrowIfNull(related);
        if (SecurityAdministration.FindRelationship(relationship.SchemaName) is { } securityRelationship)
        {
            await AssociateSecurityAsync(s, caller, securityRelationship, target, related, associate, ct);
            return;
        }
        if (store.GetRelationship(relationship.SchemaName ?? "") is ManyToManyRelationshipMetadata manyToMany)
        {
            await AssociateManyToManyAsync(s, caller, manyToMany, target, related, associate, ct);
            return;
        }

        var r = store.GetRelationship(relationship.SchemaName ?? "") as OneToManyRelationshipMetadata;
        var lookup = r is null ? null : store.GetEntity(r.ReferencingEntity)?.Attributes?.FirstOrDefault(a => a.LogicalName == r.ReferencingAttribute);
        // Ownership goes through Assign, never through a relationship.
        if (r is null || lookup is null || !EntityValidator.IsWritable(lookup, isCreate: false)
            || r.ReferencingAttribute is OwnerId or "owninguser" or "owningteam")
            throw XrmdFaultException.InvalidArgument($"Relationship '{relationship.SchemaName}' is not supported");
        EntityOf(target.LogicalName);
        // 404 when missing; read as the caller, so a record it cannot see is 403 AccessDenied (Read).
        if (!associate) await RetrieveAsync(s, caller, target.LogicalName, target.Id, null, ct);

        if (target.LogicalName == r.ReferencedEntity)
        {
            foreach (var record in related)
            {
                if (record.LogicalName != r.ReferencingEntity)
                    throw XrmdFaultException.InvalidArgument(
                        $"Entity '{record.LogicalName}' is not the referencing entity of relationship '{r.SchemaName}'.");
                await SetLookupAsync(record, target);
            }
        }
        else if (target.LogicalName == r.ReferencingEntity)
        {
            if (related.Count != 1 || related[0].LogicalName != r.ReferencedEntity)
                throw XrmdFaultException.InvalidArgument(
                    $"Relationship '{r.SchemaName}' from its referencing entity '{r.ReferencingEntity}' takes exactly one related " +
                    $"'{r.ReferencedEntity}' record.");
            await SetLookupAsync(target, related[0]);
        }
        else
        {
            throw XrmdFaultException.InvalidArgument($"Entity '{target.LogicalName}' is not part of relationship '{r.SchemaName}'.");
        }

        // Associate points the lookup of record to parent; Disassociate clears it only when it points to parent.
        async Task SetLookupAsync(EntityReference record, EntityReference parent)
        {
            if (!associate)
            {
                var current = (await RetrieveAsync(s, caller, record.LogicalName, record.Id, new ColumnSet(r.ReferencingAttribute), ct))
                    .GetAttributeValue<EntityReference>(r.ReferencingAttribute);
                if (current is null || current.Id != parent.Id) return;
            }

            await UpdateAsync(s, caller, new Entity(record.LogicalName, record.Id)
            {
                [r.ReferencingAttribute] = associate ? new EntityReference(parent.LogicalName, parent.Id) : null,
            }, ct);
        }
    }

    /// <summary>
    /// Associate/Disassociate over a saved many-to-many relationship: one intersect row per (target, related record), the
    /// target on either side (a self-referential relationship puts it in <c>Entity1IntersectAttribute</c>). Every record must
    /// exist (read with HOLDLOCK). Associate locks the pair (UPDLOCK, HOLDLOCK) and inserts it; Disassociate deletes it (in
    /// both orders for a self-referential relationship; nothing to delete is not an error).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when a record is not of the relationship's entities; 404 for a missing record;
    /// 412 DuplicateRecord when the pair is already associated.</exception>
    private async Task AssociateManyToManyAsync(DataSession s, Caller caller, ManyToManyRelationshipMetadata r, EntityReference target,
        EntityReferenceCollection related, bool associate, CancellationToken ct)
    {
        var targetIsFirst = target.LogicalName == r.Entity1LogicalName;
        if (!targetIsFirst && target.LogicalName != r.Entity2LogicalName)
            throw XrmdFaultException.InvalidArgument($"Entity '{target.LogicalName}' is not part of relationship '{r.SchemaName}'.");
        var other = targetIsFirst ? r.Entity2LogicalName : r.Entity1LogicalName;
        if (related.FirstOrDefault(e => e.LogicalName != other) is { } wrong)
            throw XrmdFaultException.InvalidArgument(
                $"Relationship '{r.SchemaName}' associates a '{target.LogicalName}' with a '{other}', not a '{wrong.LogicalName}'.");

        var intersect = EntityOf(r.IntersectEntityName);
        var table = $"{s.DataTable(intersect.LogicalName)}";
        string first = s.Quote(r.Entity1IntersectAttribute), second = $"{s.Quote(r.Entity2IntersectAttribute)}";
        foreach (var record in related.Prepend(target))
        {
            var m = EntityOf(record.LogicalName);
            if (!await ExistsWhereAsync(s, m.LogicalName, SystemAttributes.PrimaryIdName(m), record.Id, ct))
                throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, record.Id);
        }

        var changed = new List<EntityReference>();
        foreach (var record in related)
        {
            var (one, two) = targetIsFirst ? (target.Id, record.Id) : (record.Id, target.Id);
            DbParameter[] parameters =
            [
                new DetachedParameter("@first", DbType.Guid, one),
                new DetachedParameter("@second", DbType.Guid, two),
                new DetachedParameter("@caller", DbType.Guid, caller.UserId),
            ];
            if (!associate)
            {
                var reverse = r.Entity1LogicalName == r.Entity2LogicalName ? $" OR ({first} = @second AND {second} = @first)" : "";
                // Only a pair that existed is a change: deleting nothing writes no audit row.
                if (await ExecuteNonQueryAsync(s, $"DELETE FROM {table} WHERE ({first} = @first AND {second} = @second){reverse}", parameters, ct) > 0)
                    changed.Add(record);
                continue;
            }

            int inserted;
            try
            {
                // Plan 2.7a, Task 6: the key lock of the pair, then one insert that checks for it under the range lock.
                await s.KeyLockAsync(intersect.LogicalName, $"{one:N}:{two:N}", ct);
                inserted = await ExecuteNonQueryAsync(s, $"""
                    INSERT INTO {table} ({s.Quote(SystemAttributes.PrimaryIdName(intersect))}, {first}, {second},
                        createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                    SELECT {s.Dialect.NewGuid}, @first, @second, {s.Dialect.UtcNow}, @caller, {s.Dialect.UtcNow}, @caller, 0, 1
                    WHERE NOT EXISTS (SELECT 1 FROM {table}{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE {first} = @first AND {second} = @second)
                    """, parameters, ct);
            }
            catch (DbException ex) when (s.Dialect.Classify(ex) == DbErrorKind.UniqueViolation)
            {
                inserted = 0; // a concurrent Associate inserted the pair first
            }
            if (inserted != 1) throw XrmdFaultException.DuplicateAssociation();
            changed.Add(record);
        }

        // One row on the Target (ruling A1), in the transaction of the operation.
        await AuditRelationshipAsync(s, caller, target, r.SchemaName,
            associate ? AuditActions.AssociateEntities : AuditActions.DisassociateEntities, changed, ct);
    }

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
    /// Writes the audit row of an Associate or Disassociate on <paramref name="target"/> (a <see cref="RelationshipChange"/> of the
    /// records that really changed); nothing when none did, or when the table or the organization is not audited.
    /// </summary>
    private Task AuditRelationshipAsync(DataSession s, Caller caller, EntityReference target, string relationship, int action,
        IReadOnlyList<EntityReference> changed, CancellationToken ct) =>
        changed.Count == 0
            ? Task.CompletedTask
            : audit.WriteAsync(s, caller, action, AuditOperations.Update, target,
                new RelationshipChange(relationship, changed.Select(e => new EntityReference(e.LogicalName, e.Id)).ToList()), ct);

    /// <summary>Associate/Disassociate over a security relationship: one intersect row per (target, related record).</summary>
    /// <exception cref="XrmdFaultException">400 when a record is not of the relationship's entities, plus the faults of
    /// <see cref="SecurityAdministration"/>.</exception>
    private async Task AssociateSecurityAsync(DataSession s, Caller caller, SecurityRelationship relationship, EntityReference target,
        EntityReferenceCollection related, bool associate, CancellationToken ct)
    {
        if (!relationship.TryGetPairs(target, related, out var pairs))
            throw XrmdFaultException.InvalidArgument(
                $"Relationship '{relationship.SchemaName}' associates a '{relationship.Entity1}' with a '{relationship.Entity2}'.");
        var changed = new List<EntityReference>();
        foreach (var (first, second) in pairs)
        {
            if (relationship.SchemaName == SecurityAdministration.TeamMembership)
            {
                if (associate) await security.AddMemberAsync(s, first.Id, second.Id, ct);
                else await security.RemoveMemberAsync(s, first.Id, second.Id, ct);
            }
            else if (SecurityAdministration.IsProfileRelationship(relationship.SchemaName))
            {
                // The principal (systemuser or team) is Entity1, the field security profile Entity2.
                if (associate) await security.AddProfileAsync(s, first, second.Id, ct);
                else await security.RemoveProfileAsync(s, first, second.Id, ct);
            }
            else if (associate)
            {
                await security.AddRoleAsync(s, first, second.Id, ct);
                changed.Add(Other(first, second));
            }
            else if (await security.RemoveRoleAsync(s, first, second.Id, ct))
            {
                changed.Add(Other(first, second));
            }
        }

        // Role assignments are audited on the side the request names; team membership is not (ruling A2).
        var action = relationship.SchemaName switch
        {
            SecurityAdministration.SystemUserRoles => associate ? AuditActions.AssignRoleToUser : AuditActions.RemoveRoleFromUser,
            SecurityAdministration.TeamRoles => associate ? AuditActions.AssignRoleToTeam : AuditActions.RemoveRoleFromTeam,
            _ => 0,
        };
        if (action != 0) await AuditRelationshipAsync(s, caller, target, relationship.SchemaName, action, changed, ct);

        // The record on the other side of the pair from the target.
        EntityReference Other(EntityReference first, EntityReference second) => first.Id == target.Id && first.LogicalName == target.LogicalName ? second : first;
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

    /// <inheritdoc/>
    public async Task<bool> LockExistsAsync(EntityReference target, DataSession session, CancellationToken ct)
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
    public async Task<Entity?> RetrieveImageAsync(EntityReference target, string[]? attributes, DataSession session, CancellationToken ct)
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
    private RecordUpdate RecordUpdate(NestedRequest? nested, OrganizationRequest request, DataSession session) =>
        nested is null
            ? throw new InvalidOperationException($"'{request.RequestName}' writes a record through the pipeline: run it through the PipelineExecutor.")
            : async (target, ct) => await nested(await ElasticRecords.FileUpdateAsync(session, EntityOf(target.LogicalName), target, ct), ct);

    /// <summary>A string parameter of <paramref name="request"/>; null when missing or of another type.</summary>
    private static string? OptionalString(OrganizationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) ? value as string : null;

    /// <summary>The optional <c>SolutionUniqueName</c> parameter (a Create in a solution); null when absent or empty.</summary>
    private static string? SolutionUniqueName(OrganizationRequest request) =>
        request.Parameters.TryGetValue("SolutionUniqueName", out var value) && value is string { Length: > 0 } name ? name : null;

    /// <summary>A required <see cref="Guid"/> parameter of <paramref name="request"/>; InvalidArgument (400) when missing or of another type.</summary>
    internal static Guid GuidParameter(OrganizationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && value is Guid id
            ? id
            : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.RequestName}' is missing or not a Guid.");

    /// <summary>
    /// True when a Create or Update asks for the duplicate check (plan 2.15, ruling D1): <c>SuppressDuplicateDetection</c> false.
    /// An absent parameter suppresses it (Learn: the default for SDK and Web API calls).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a value that is not a Boolean.</exception>
    public static bool DetectDuplicates(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Parameters.TryGetValue(DuplicateFinder.SuppressParameter, out var value)) return false;
        return value is bool suppress
            ? !suppress
            : throw XrmdFaultException.InvalidArgument(
                $"The parameter '{DuplicateFinder.SuppressParameter}' of request '{request.RequestName}' must be a Boolean, got '{value ?? "null"}'.");
    }

    /// <summary>A required parameter of <paramref name="request"/>; InvalidArgument (400) when missing or of another type.</summary>
    internal static T Parameter<T>(OrganizationRequest request, string name) where T : class =>
        request.Parameters.TryGetValue(name, out var value) && value is T typed
            ? typed
            : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.RequestName}' is missing or not a {typeof(T).Name}.");

    /// <summary>
    /// Inserts the record. Id: <see cref="Entity.Id"/>, else the primary key attribute, else a new one. Sets
    /// createdon/modifiedon (UTC now), createdby/modifiedby (caller), owningbusinessunit, statecode 0/statuscode (the DefaultStatus of state 0, else 1) when
    /// not given and, for user-owned entities, ownerid (default: caller) with owninguser. An activity (plan 2.11) also gets its
    /// party rows and its <c>activitypointer</c> row in this session. With <paramref name="detectDuplicates"/>, the duplicate
    /// check of plan 2.15 runs on the values about to be inserted (<see cref="Duplicates.DuplicateFinder.DemandNoDuplicateAsync"/>).
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for invalid input; ObjectDoesNotExist (404) for a
    /// lookup or party to a missing record; DuplicateRecord (412) when the id is taken or a unique index refuses a value;
    /// DuplicateRecordsFound (412) when the duplicate check finds a duplicate.</exception>
    /// <param name="serverSetValues">Plan 4.1c: columns the server writes besides the caller's values (such as <c>email.senton</c> of
    /// an incoming email), set after <paramref name="entity"/> is validated, so a column not valid for Create may be among them; the
    /// caller's entity never holds them. Null for a caller's Create.</param>
    /// <exception cref="InvalidOperationException"><paramref name="serverSetValues"/> names a column the table does not have.</exception>
    public async Task<Guid> CreateAsync(DataSession s, Caller caller, Entity entity, CancellationToken ct, bool detectDuplicates = false,
        IReadOnlyDictionary<string, object?>? serverSetValues = null)
    {
        var (m, attrs) = ValidateWrite(entity, isCreate: true);
        // Plan 3.5 UI-1g: the refusals of the post tables, on the values after the plug-ins.
        PostRules.DemandWritable(m, entity, isCreate: true);
        if (!ManagedProperties.Bypassed(caller) && ComponentChildren.TryGetValue(m.LogicalName, out var parentOfNew)
            && entity.Attributes.TryGetValue(parentOfNew.Column, out var parentValue) && parentValue is EntityReference parentRef)
            await DemandParentCustomizableAsync(s, m.LogicalName, parentRef.Id, ct);
        if (!ManagedProperties.Bypassed(caller)) await DemandTableAllowsAsync(s, entity, ct);
        var pk = SystemAttributes.PrimaryIdName(m);
        var id = entity.Id != Guid.Empty ? entity.Id
            : entity.GetAttributeValue<Guid?>(pk) is { } given && given != Guid.Empty ? given
            : Guid.NewGuid();

        // Plan 2.11: the party lists are written as activityparty rows after the activity row (the caller's entity keeps them).
        var activity = ActivitySchema.IsActivityTable(m);
        var partyLists = activity ? ActivityParties.ExtractPartyLists(m, entity = Copy(entity)) : null;
        if (partyLists is not null) await DemandPartiesReadableAsync(s, caller, partyLists, ct);
        // Plan 2.11 A16: a note body has no column; it is stored in the file store before the note's row is written.
        var noteBody = ExtractNoteBody(m, ref entity);
        // Plan 4.1b: the write-only mail secrets have no column; they are stored after the row is written.
        var mailSecrets = MailSecrets.Take(ref entity, m);
        var row = WritableValues(entity, attrs, isCreate: true);
        if (serverSetValues is not null)
            foreach (var (name, value) in serverSetValues)
                row[name] = attrs.ContainsKey(name)
                    ? value
                    : throw new InvalidOperationException($"The server value '{name}' is not a column of '{m.LogicalName}'.");
        row[pk] = id;
        if (m.LogicalName == MailSchema.EmailServerProfile) ApplyProfileRules(row, null);
        Mailboxes.DemandSupportedDeliveryMethods(row, m.LogicalName);
        TrackingTokens.DemandSupportedOrganizationColumns(row, m.LogicalName);
        // Migration spec §7.5: inside a migration scope the record keeps the system columns it carries.
        var migrated = MigrationScope.IsActive ? MigratedSystemColumns(entity, attrs) : null;
        if (migrated is not null) detectDuplicates = false;
        // Plan 2.10, final review I1: the currency rules on the final row (after the plug-ins), in the transaction.
        await CurrencyRules.EnforceRowAsync(row, id, isCreate: true, store, s, ct);
        await ApplyAccessTeamRulesAsync(s, m, attrs, row, isCreate: true, id, ct);
        await DuplicateRuleRules.ApplyServerColumnsAsync(s, store, m.LogicalName, row, isCreate: true, id, ct);
        // #167 (ruling E2): unique and fixed definition names, one value per definition, the value's default name.
        await EnvironmentVariableRules.ApplyRowAsync(s, m.LogicalName, row, isCreate: true, id, ct);
        await OrganizationSettingRules.ApplyRowAsync(s, m.LogicalName, row, isCreate: true, id, ct);
        var now = DateTime.UtcNow;
        var callerRef = new EntityReference(SystemUser, caller.UserId);
        SetIfDefined(row, attrs, "createdon", now);
        // #343.5 (Learn, "Import auditing data"): a given overriddencreatedon becomes createdon; the column keeps the time of the Create.
        // A migration then puts back the source's createdon and overriddencreatedon (MigrationSystemColumns).
        if (row.GetAttributeValue<DateTime?>(SystemAttributes.OverriddenCreatedOnName) is { } original && attrs.ContainsKey("createdon"))
        {
            row["createdon"] = original;
            row[SystemAttributes.OverriddenCreatedOnName] = now;
        }
        SetIfDefined(row, attrs, "modifiedon", now);
        SetIfDefined(row, attrs, "createdby", callerRef);
        SetIfDefined(row, attrs, "modifiedby", callerRef);
        SetIfDefined(row, attrs, "owningbusinessunit", new EntityReference(BusinessUnit, caller.BusinessUnitId));
        ApplyIsManaged(m, attrs, entity, row, caller, isCreate: true);
        if (activity) ApplyActivityStateOnCreate(m, attrs, row, id, now);
        if (row.GetAttributeValue<OptionSetValue>("statecode") is null) SetIfDefined(row, attrs, "statecode", new OptionSetValue(0));
        if (row.GetAttributeValue<OptionSetValue>("statuscode") is null) SetIfDefined(row, attrs, "statuscode", new OptionSetValue(DefaultStatus(attrs)));
        if (attrs.ContainsKey(OwnerId))
            await ApplyOwnerAsync(s, row, attrs, row.GetAttributeValue<EntityReference>(OwnerId) ?? callerRef, caller, ct);
        ApplyBusinessUnitOnCreate(m.LogicalName, row, attrs, caller);
        if (migrated is not null) foreach (var (name, value) in migrated) row[name] = value;
        if (m.LogicalName == SystemUser && row.GetAttributeValue<EntityReference>(ParentSystemUserId)?.Id == id)
            throw XrmdFaultException.InvalidArgument(OwnManager);
        if (IsContact(m)) await ApplyFullNameAsync(s, row, id, isCreate: true, ct);
        await CurrencyStamp.ApplyAsync(s, caller, m, attrs, row, id, isCreate: true, ct);
        await ApplyAutoNumbersAsync(s, m, row, attrs, ct);
        // Plan 3.5 UI-1g: a post's regarding columns and postregarding row; a post's or comment's organization and delegate.
        await PostRules.ApplyCreateAsync(s, store, caller, m, attrs, row, ct);

        await ElasticRecords.ApplyLookupPartitionsAsync(s, store, attrs, row, ct);
        await EnsureReferencesExistAsync(s, row, attrs, ct);
        // Plan 4.1a (ruling R15): one active queue item per record (a received email excepted), under the record's lock. A migration
        // imports the source's items as they are.
        if (m.LogicalName == QueueSchema.QueueItem && migrated is null) await QueueMessages.DemandNewItemAllowedAsync(s, store, row, ct);
        // An image set to a byte[] becomes its thumbnail, image id, timestamp and (CanStoreFullImage) full image (ruling F4).
        if (row.Attributes.Any(kv => attrs[kv.Key] is ImageAttributeMetadata))
            await files.ApplyImagesAsync(s, m, id, row, isCreate: true, ct);
        // Plan 2.15 (ruling D2): before the INSERT, so a refused Create writes neither the row nor its match codes (RF3).
        if (detectDuplicates) await DuplicateFinder.DemandNoDuplicateAsync(s, m, row, id, null, ct);
        if (m.LogicalName == ActivitySchema.Annotation) await ApplyNoteBodyAsync(s, m, attrs, row, id, noteBody, isCreate: true, ct);
        if (m.LogicalName == MailSchema.Attachment) await ApplyAttachmentAsync(s, m, attrs, row, id, noteBody, isCreate: true, ct);

        // Plan 2.12b, ruling R7: a new row starts with its rollups not calculated (state 0, _date now, 0 or null).
        rollups?.ApplyInitialValues(m, row, now);
        var parameters = EntityRowMapper.ToParameters(row, attrs, store.GetEntity);
        var sql = $"INSERT INTO {s.DataTable(m.LogicalName)} " +
                  $"({string.Join(", ", parameters.Select(p => s.Quote(p.ParameterName[1..])))}) " +
                  $"VALUES ({string.Join(", ", parameters.Select(p => p.ParameterName))})";
        try
        {
            await ExecuteNonQueryAsync(s, sql, parameters, ct);
        }
        catch (DbException ex) when (s.Dialect.IsPrimaryKeyViolation(ex))
        {
            throw new XrmdFaultException(ErrorCodes.DuplicateRecord,
                $"A record that has the attribute values {pk} = {id} already exists on entity '{m.LogicalName}'.", 412);
        }
        catch (DbException ex) when (s.Dialect.Classify(ex) == DbErrorKind.UniqueViolation
                                      && ex.Message.Contains(FieldSecurityTables.ShareUniqueIndex, StringComparison.Ordinal))
        {
            // A concurrent Create of the same field share passed the guard's check first (F6 rule 3).
            throw DuplicateShare(entity);
        }
        catch (DbException ex) when (IsUniqueIndexViolation(s, ex))
        {
            throw DuplicateUniqueValue(m.LogicalName, row);
        }
        await MailSecrets.StoreAsync(s, id, mailSecrets, ct);
        // Plan 4.1b, task 7: testemailconfigurationscheduled true queues the mailbox's test, in this transaction.
        if (!MigrationScope.IsActive) await MailboxTestJob.EnqueueIfScheduledAsync(s, caller, JobQueue, m.LogicalName, id, row, ct);
        // Plan 2.15: the row's match codes, in this transaction (nothing is read when no rule matches the table).
        InvalidateDuplicateRulesOnWrite(s, m);
        await duplicates.MatchCodes.RefreshAsync(s, m, id, ct);
        if (m.LogicalName == SystemUser) await InsertUserSettingsAsync(s, caller, id, ct);
        // Plan 4.1a: a user and an owner team get a private default queue, in this transaction.
        // Plan 4.1b: a user and a queue get their mailbox, in this transaction (a user's before its default queue, whose Create makes the queue's).
        if (m.LogicalName is SystemUser or QueueSchema.Queue && !MigrationScope.IsActive && store.GetEntity(MailSchema.Mailbox) is not null)
            await Mailboxes.InsertForAsync(s, caller, new EntityReference(m.LogicalName, id), ct, store);
        if (m.LogicalName is SystemUser or Team) await InsertDefaultQueueAsync(s, caller, m, id, ct);
        if (activity) await WriteActivityRowsAsync(s, m, id, partyLists!, isCreate: true, ct);
        if (CurrencyMessages.InvalidatesCatalog(m.LogicalName)) CurrencyMessages.InvalidateAfterTransaction(currencies, s);
        if (await audit.IsAuditedAsync(s, m, ct))
            await WriteAuditAsync(s, caller, AuditActions.Create, AuditOperations.Create, m, id, null,
                await AuditImageAsync(s, m, id, AuditPolicy.AuditedAttributes(m).Select(a => a.LogicalName), ct), ct);
        return id;
    }

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
    internal async Task<Guid> InsertDefaultQueueAsync(DataSession s, Caller caller, EntityMetadata principal, Guid principalId, CancellationToken ct)
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
        var queueId = await CreateAsync(s, caller, new Entity(QueueSchema.Queue)
        {
            ["name"] = QueueSchema.DefaultQueueName(name),
            ["queueviewtype"] = new OptionSetValue(QueueSchema.PrivateViewType),
            ["queuetypecode"] = new OptionSetValue(QueueSchema.TypeCode),
            [OwnerId] = new EntityReference(principal.LogicalName, principalId),
        }, ct);
        await ExecuteNonQueryAsync(s,
            $"UPDATE {s.DataTable(principal.LogicalName)} SET {s.Quote(QueueSchema.QueueIdColumn)} = @queue " +
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
    /// Plan 4.1a: the default queue goes with its team, in the team's Delete, unless it holds active queue items (400, nothing is
    /// deleted). Only the queue the team owns and points to is deleted; its inactive items go by the cascade of <c>queue_entries</c>.
    /// Users are never deleted, so a user's default queue has no such step.
    /// </summary>
    private async Task DeleteDefaultQueueAsync(DataSession s, Caller caller, Guid teamId, CancellationToken ct)
    {
        if (store.GetEntity(QueueSchema.Queue) is null || store.GetEntity(QueueSchema.QueueItem) is null) return;
        Guid? queueId;
        await using (var cmd = s.Command(
                         $"SELECT t.{s.Quote(QueueSchema.QueueIdColumn)} FROM {s.DataTable(Team)} t " +
                         $"JOIN {s.DataTable(QueueSchema.Queue)} q ON q.{s.Quote(QueueSchema.QueueIdColumn)} = t.{s.Quote(QueueSchema.QueueIdColumn)} " +
                         $"AND q.{s.Quote(OwnerId)} = t.{s.Quote("teamid")} WHERE t.{s.Quote("teamid")} = {IdParameter}"))
        {
            cmd.AddParameter(IdParameter, DbType.Guid, teamId);
            queueId = await cmd.ExecuteScalarAsync(ct) is Guid g ? g : null;
        }
        if (queueId is not { } queue) return;
        await using (var cmd = s.Command(
                         $"SELECT COUNT(*) FROM {s.DataTable(QueueSchema.QueueItem)} WHERE {s.Quote(QueueSchema.QueueIdColumn)} = {IdParameter} " +
                         $"AND {s.Quote("statecode")} = 0"))
        {
            cmd.AddParameter(IdParameter, DbType.Guid, queue);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0)
                throw XrmdFaultException.InvalidArgument(
                    $"The team {teamId} cannot be deleted: its default queue {queue} still holds active queue items. Remove or deactivate them first.");
        }
        await DeleteAsync(s, caller, QueueSchema.Queue, queue, null, ct);
    }

    /// <summary>
    /// Fills each autonumber column the Create leaves without a value (absent, null or empty) from its
    /// <c>AutoNumberFormat</c> (autonumber plan, rulings N2 and N4): the format is checked here, at the save of a record,
    /// as Dataverse does; a value the caller gives is kept.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for a format that is not valid or a value longer than the
    /// column's MaxLength.</exception>
    private static async Task ApplyAutoNumbersAsync(DataSession s, EntityMetadata m, Entity row,
        IReadOnlyDictionary<string, AttributeMetadata> attrs, CancellationToken ct)
    {
        foreach (var a in attrs.Values.OfType<StringAttributeMetadata>().Where(a => !string.IsNullOrEmpty(a.AutoNumberFormat)))
        {
            if (row.GetAttributeValue<string>(a.LogicalName) is { Length: > 0 }) continue;
            AutoNumberFormat format;
            try
            {
                format = AutoNumberFormat.Parse(a.AutoNumberFormat!);
            }
            catch (XrmdValidationException ex)
            {
                throw XrmdFaultException.InvalidArgument($"Attribute '{m.LogicalName}.{a.LogicalName}': {ex.Message}");
            }
            long sequence = 0;
            if (format.UsesSequence)
            {
                await using var cmd = s.Command(AutoNumberSequences.NextValueSql(a, s.Dialect));
                sequence = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            var value = format.Render(sequence, DateTime.UtcNow);
            if (a.MaxLength is { } max && value.Length > max)
                throw XrmdFaultException.InvalidArgument(
                    $"Attribute '{m.LogicalName}.{a.LogicalName}': the autonumber value '{value}' is longer than its MaxLength {max}.");
            row[a.LogicalName] = value;
        }
    }

    /// <summary>
    /// Plan 2.16 at stage 30, in the request's transaction: a template's values are checked again on what is written (ruling A2,
    /// <see cref="AccessTeamRules.DemandTemplateAsync"/>, which on Create takes the lock that serializes it with disabling the
    /// table and counts the templates under it). xrmverse applies no column default on insert, so a Create writes
    /// <c>teamtemplate.issystem</c> false and, when not given, <c>team.systemmanaged</c> false.
    /// </summary>
    private async Task ApplyAccessTeamRulesAsync(DataSession s, EntityMetadata m, IReadOnlyDictionary<string, AttributeMetadata> attrs,
        Entity row, bool isCreate, Guid id, CancellationToken ct)
    {
        if (m.LogicalName == AccessTeamSchema.TeamTemplate)
        {
            await AccessTeamRules.DemandTemplateAsync(s, store, row, isCreate, id, ct);
            if (isCreate) SetIfDefined(row, attrs, AccessTeamSchema.IsSystem, false);
        }
        else if (isCreate && m.LogicalName == Team && !row.Contains(AccessTeamSchema.SystemManaged))
        {
            SetIfDefined(row, attrs, AccessTeamSchema.SystemManaged, false);
        }
    }

    /// <summary>Test seam: runs in <see cref="RetrieveAsync"/> after the caller's read found nothing, before the unrestricted one.</summary>
    internal Func<Task>? AfterRestrictedRetrieveMiss { get; set; }

    /// <summary>
    /// Reads one record with the columns of <paramref name="columns"/> (primary key first; <c>AllColumns</c> → every
    /// readable attribute; null/empty → the primary key only), through the <see cref="QueryExecutor"/>: lookups carry the
    /// target's name and the entity its formatted values, as in RetrieveMultiple.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for an unknown entity or column; ObjectDoesNotExist (404)
    /// when the record does not exist; PrivilegeDenied (403) without read privilege on the entity; AccessDenied (403, Read)
    /// when the record exists but the caller's read restriction hides it.</exception>
    /// <param name="key">The row of an elastic table: its id and partition (plan 3.4, ruling E1); null finds the row by
    /// <paramref name="id"/> alone.</param>
    public async Task<Entity> RetrieveAsync(
        DataSession s, Caller caller, string entityName, Guid id, ColumnSet? columns, CancellationToken ct, ElasticRecordKey? key = null)
    {
        var m = EntityOf(entityName);
        if (key is { } k)
        {
            id = k.Id;
            // Plan 3.4, ruling E1: the partition matches exactly (the query's comparison follows the column's collation).
            if (!(await ElasticRecords.FindAsync(s, m, k, lockRow: false, ct)).Found) throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, id);
        }
        ConditionNode[] identity = key?.Conditions(m) ?? [new ConditionNode(null, SystemAttributes.PrimaryIdName(m), FilterOperator.Equal, [id])];
        var root = new EntityNode { EntityName = m.LogicalName, AllColumns = columns?.AllColumns == true };
        if (!root.AllColumns)
            foreach (var column in columns?.Columns ?? Enumerable.Empty<string>()) root.Columns.Add(new ColumnNode(column));
        root.Filter.Conditions.AddRange(identity);

        var result = await queryExecutor.ExecuteAsync(new QueryModel { Root = root, Top = 1 }, caller, s, ct);
        if (result.Entities.Count > 0) return result.Entities[0];

        // Not visible to a restricted caller: 403 when the record exists (read unrestricted), 404 otherwise. An unrestricted
        // caller sees every record, so for it the second read finds nothing either.
        if (caller != Caller.Internal)
        {
            if (AfterRestrictedRetrieveMiss is { } hook) await hook();
            var any = new EntityNode { EntityName = m.LogicalName };
            any.Filter.Conditions.AddRange(identity);
            if ((await queryExecutor.ExecuteAsync(new QueryModel { Root = any, Top = 1 }, Caller.Internal, s, ct)).Entities.Count > 0)
            {
                // The two reads are not one snapshot: a record committed by another session after the caller's read is
                // found by the second. Read it as the caller once more before answering 403 (a committed row stays).
                result = await queryExecutor.ExecuteAsync(new QueryModel { Root = root, Top = 1 }, caller, s, ct);
                if (result.Entities.Count > 0) return result.Entities[0];
                throw XrmdFaultException.AccessDenied(caller.UserId, AccessRights.ReadAccess, m.LogicalName, id);
            }
        }
        throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, id);
    }

    /// <summary>
    /// Writes only the given (updatable) attributes plus modifiedon/modifiedby. Read-only attributes are ignored, so a
    /// retrieved entity can be sent back. A new ownerid also moves owninguser and owningbusinessunit.
    /// </summary>
    /// <exception cref="XrmdFaultException">ObjectDoesNotExist (404) when the record or a referenced record does not exist;
    /// DuplicateRecord (412) when a unique index refuses the new values.</exception>
    /// <remarks>With <paramref name="detectDuplicates"/>, the duplicate check of plan 2.15 runs on the stored row overlaid with the
    /// changes, when they touch a column a Published rule matches on (<see cref="Duplicates.DuplicateFinder.DemandNoDuplicateAsync"/>,
    /// 412 DuplicateRecordsFound).</remarks>
    public Task UpdateAsync(DataSession s, Caller caller, Entity entity, CancellationToken ct, bool detectDuplicates = false) =>
        UpdateAsync(s, caller, entity, AuditActions.Update, ct, detectDuplicates);

    /// <summary>
    /// The Update of <paramref name="entity"/>, audited with <paramref name="auditAction"/> (Update, Assign or Set State). An
    /// activity (plan 2.11) keeps its party rows and its <c>activitypointer</c> row in this session; a closed one changes only its
    /// state and status, unless assigned (A11, pre-flight C12).
    /// </summary>
    private Task UpdateAsync(DataSession s, Caller caller, Entity entity, int auditAction, CancellationToken ct, bool detectDuplicates = false) =>
        UpdateAsync(s, caller, entity, auditAction, null, ct, detectDuplicates);

    /// <summary>
    /// What a message writes with its Update besides the caller's values (plan 2.11, SendEmail): read-only columns the server
    /// maintains (such as <c>email.senton</c>), set after the writable values are taken, and party lists the server fills (such as
    /// the sender), written with the caller's party lists but without the readability check of ruling Y2.
    /// </summary>
    internal sealed record ServerValues(IReadOnlyDictionary<string, object?> Attributes, IReadOnlyDictionary<string, EntityCollection> PartyLists);

    /// <summary>
    /// The Update of <paramref name="entity"/> for a message other than Update (SendEmail): the same path, with the values the
    /// server adds (<paramref name="server"/>). No Update step fires: the message's own steps run around it.
    /// </summary>
    internal Task UpdateForMessageAsync(DataSession s, Caller caller, Entity entity, int auditAction, ServerValues server,
        CancellationToken ct) =>
        UpdateAsync(s, caller, entity, auditAction, server, ct);

    private async Task UpdateAsync(DataSession s, Caller caller, Entity entity, int auditAction, ServerValues? server, CancellationToken ct,
        bool detectDuplicates = false)
    {
        ArgumentNullException.ThrowIfNull(entity);
        // The Update of a CommitFileBlocksUpload carries the new file id: the only value a file column takes (ruling F4).
        var commit = files.PendingCommit(s, entity);
        if (commit is not null) entity = Without(entity, commit.Attribute);
        entity = await WithoutUnchangedFullNameAsync(s, entity, ct);
        var (m, attrs) = ValidateWrite(entity, isCreate: false);
        PostRules.DemandWritable(m, entity, isCreate: false); // plan 3.5 UI-1g: posts, comments and postregarding are never updated
        if (IsContact(m)) DemandNoFullName(entity);
        var pk = SystemAttributes.PrimaryIdName(m);
        var id = UpdateId(entity, m, pk);
        await DemandRowUpdateAllowedAsync(s, caller, m.LogicalName, id, ct, entity);
        await DemandEntityNamesMatchStoredAsync(s, m, attrs, entity, id, ct);

        // Plan 2.11: the party lists are written as activityparty rows after the activity row (the caller's entity keeps them).
        var activity = ActivitySchema.IsActivityTable(m);
        var partyLists = activity ? ActivityParties.ExtractPartyLists(m, entity = Copy(entity)) : null;
        if (partyLists is not null) await DemandPartiesReadableAsync(s, caller, partyLists, ct);
        if (partyLists is not null && server is { PartyLists.Count: > 0 })
        {
            var merged = new Dictionary<string, EntityCollection>(partyLists, StringComparer.Ordinal);
            foreach (var (name, list) in server.PartyLists) merged[name] = list;
            partyLists = merged;
        }
        var noteBody = ExtractNoteBody(m, ref entity);
        var mailSecrets = MailSecrets.Take(ref entity, m);
        var row = WritableValues(entity, attrs, isCreate: false);
        if (server is not null)
            foreach (var (name, value) in server.Attributes)
                if (attrs.ContainsKey(name)) row[name] = value; // null clears the column
        if (m.LogicalName == MailSchema.EmailServerProfile && ProfileRules.Columns.Any(row.Contains))
            ApplyProfileRules(row, await RetrieveAsync(s, Caller.Internal, m.LogicalName, id, new ColumnSet([.. ProfileRules.Columns, "name"]), ct));
        Mailboxes.DemandSupportedDeliveryMethods(row, m.LogicalName);
        TrackingTokens.DemandSupportedOrganizationColumns(row, m.LogicalName);
        if (m.LogicalName == MailSchema.Mailbox && caller != Caller.Internal)
        {
            var storedMailbox = await RetrieveAsync(s, Caller.Internal, m.LogicalName, id, new ColumnSet([.. MailSchema.MailboxServerColumns, "regardingobjectid"]), ct);
            Mailboxes.DemandNotServerMaintained(row, storedMailbox, caller);
            Mailboxes.DemandOwnerUnchanged(row, storedMailbox);
        }
        // Plan 4.1c: changed incoming settings send incomingemailstatus back to Not Run; the sync state is then forgotten after the UPDATE.
        var incomingReset = false;
        if (m.LogicalName == MailSchema.Mailbox)
        {
            var statusSent = row.Contains("incomingemailstatus");
            await Mailboxes.ApplyIncomingChangeAsync(s, row, id, mailSecrets, ct);
            incomingReset = !statusSent && row.Contains("incomingemailstatus");
        }
        // Plan 4.1b: a changed address of a user or queue sends its approval back to Pending Approval (the mailbox follows after the UPDATE).
        var mailboxTracked = m.LogicalName is SystemUser or QueueSchema.Queue && !MigrationScope.IsActive && store.GetEntity(MailSchema.Mailbox) is not null;
        var addressChanged = mailboxTracked && await Mailboxes.ApplyAddressChangeAsync(s, m.LogicalName, id, row, ct);
        var deliveryChanged = mailboxTracked ? await Mailboxes.ChangedDeliveryColumnsAsync(s, m.LogicalName, id, row, ct) : null;
        ApplyIsManaged(m, attrs, entity, row, caller, isCreate: false);
        await ApplyAccessTeamRulesAsync(s, m, attrs, row, isCreate: false, id, ct);
        await DuplicateRuleRules.ApplyServerColumnsAsync(s, store, m.LogicalName, row, isCreate: false, id, ct);
        // #167 (ruling E2): unique and fixed definition names, one value per definition, the value's default name.
        await EnvironmentVariableRules.ApplyRowAsync(s, m.LogicalName, row, isCreate: false, id, ct);
        await OrganizationSettingRules.ApplyRowAsync(s, m.LogicalName, row, isCreate: false, id, ct);
        CompleteStateAndStatus(m, attrs, row, id);
        if (activity && !MigrationScope.IsActive)
            await ApplyActivityStateOnUpdateAsync(s, m, attrs, row, partyLists!, commit?.Attribute, id, auditAction, server?.Attributes.Keys, ct);
        // Plan 2.10, final review I1: the currency rules on the final row (after the plug-ins), in the transaction.
        await CurrencyRules.EnforceRowAsync(row, id, isCreate: false, store, s, ct);
        SetIfDefined(row, attrs, "modifiedon", DateTime.UtcNow);
        SetIfDefined(row, attrs, "modifiedby", new EntityReference(SystemUser, caller.UserId));
        // Migration spec §7.5: inside a migration scope the record keeps the system columns it carries.
        var migrated = MigrationScope.IsActive ? MigratedSystemColumns(entity, attrs) : null;
        if (migrated is not null) detectDuplicates = false;
        if (row.Contains(OwnerId))
            await ApplyOwnerAsync(s, row, attrs,
                row.GetAttributeValue<EntityReference>(OwnerId)
                ?? throw XrmdFaultException.InvalidArgument($"Attribute '{OwnerId}' of entity '{m.LogicalName}' cannot be null."),
                caller, ct);
        await ApplyBusinessUnitOnUpdateAsync(s, m.LogicalName, id, row, attrs, ct);
        if (migrated is not null) foreach (var (name, value) in migrated) row[name] = value;
        if (m.LogicalName == SystemUser && row.GetAttributeValue<EntityReference>(ParentSystemUserId) is { } manager)
            await EnsureNoManagerLoopAsync(s, id, manager.Id, ct);
        if (IsContact(m)) await ApplyFullNameAsync(s, row, id, isCreate: false, ct);
        await CurrencyStamp.ApplyAsync(s, caller, m, attrs, row, id, isCreate: false, ct);

        await ElasticRecords.ApplyLookupPartitionsAsync(s, store, attrs, row, ct);
        await EnsureReferencesExistAsync(s, row, attrs, ct);
        // Plan 4.1a (ruling R15): an item reactivated, or pointed at another record, keeps one active item per record.
        if (m.LogicalName == QueueSchema.QueueItem && !MigrationScope.IsActive) await QueueMessages.DemandItemUpdateAllowedAsync(s, id, row, ct);
        // A file column set to null deletes its file (ruling F4); the commit's column gets its file id and name.
        if (commit is not null || row.Attributes.Any(kv => attrs[kv.Key] is FileAttributeMetadata))
            await files.ApplyOnUpdateAsync(s, m, id, row, commit, ct);
        // An image set to a byte[] or null (ruling F4), as on Create; null also releases the full image.
        if (row.Attributes.Any(kv => attrs[kv.Key] is ImageAttributeMetadata))
            await files.ApplyImagesAsync(s, m, id, row, isCreate: false, ct);
        // Plan 2.15 (ruling D2, RF1): before the UPDATE, on the stored row overlaid with the changes.
        if (detectDuplicates) await DuplicateFinder.DemandNoDuplicateAsync(s, m, row, id, row.Attributes.Keys.ToList(), ct);
        if (m.LogicalName == ActivitySchema.Annotation) await ApplyNoteBodyAsync(s, m, attrs, row, id, noteBody, isCreate: false, ct);
        if (m.LogicalName == MailSchema.Attachment) await ApplyAttachmentAsync(s, m, attrs, row, id, noteBody, isCreate: false, ct);

        // The audited columns this Update writes, read before it under an update lock (only when the table is audited).
        string[] audited = [];
        Entity? before = null;
        if (await audit.IsAuditedAsync(s, m, ct))
        {
            audited = AuditPolicy.AuditedAttributes(m).Where(a => row.Contains(a.LogicalName)).Select(a => a.LogicalName).ToArray();
            if (audited.Length > 0 && await LockRowAsync(s, m, id, ct)) before = await AuditImageAsync(s, m, id, audited, ct);
        }

        var parameters = EntityRowMapper.ToParameters(row, attrs, store.GetEntity).ToList();
        var sql = $"UPDATE {s.DataTable(m.LogicalName)} " +
                  $"SET {string.Join(", ", parameters.Select(p => $"{s.Quote(p.ParameterName[1..])} = {p.ParameterName}"))} " +
                  $"WHERE {s.Quote(pk)} = {IdParameter}";
        parameters.Add(new DetachedParameter(IdParameter, DbType.Guid, id));
        int updated;
        try
        {
            updated = await ExecuteNonQueryAsync(s, sql, parameters, ct);
        }
        catch (DbException ex) when (IsUniqueIndexViolation(s, ex))
        {
            throw DuplicateUniqueValue(m.LogicalName, row);
        }
        if (updated == 0) throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, id);
        await MailSecrets.StoreAsync(s, id, mailSecrets, ct);
        // Plan 4.1b, task 7: testemailconfigurationscheduled true queues the mailbox's test, in this transaction.
        if (!MigrationScope.IsActive) await MailboxTestJob.EnqueueIfScheduledAsync(s, caller, JobQueue, m.LogicalName, id, row, ct);
        // Plan 4.1c, task 9: another server or account may be read now: the next sync starts again from processemailreceivedafter (the
        // message keys keep what was imported from coming in twice), and a pass still running writes nothing (final review M2).
        if (incomingReset) await MailboxSyncState.ResetAsync(s, id, ct);
        if (mailboxTracked) await Mailboxes.SyncAsync(s, m.LogicalName, id, row, addressChanged, ct, store, deliveryChanged);
        if (writes is not null && !MigrationScope.IsActive) await writes.OnWrittenAsync(s, m.LogicalName, id, ct); // plan 2.14 (W10)
        if (activity) await WriteActivityRowsAsync(s, m, id, partyLists!, isCreate: false, ct);
        // Plan 4.1a: a queue-enabled record that leaves the active state deactivates its active items, in this transaction.
        if (m.IsValidForQueue?.Value == true && !MigrationScope.IsActive && row.GetAttributeValue<OptionSetValue>("statecode") is { Value: not 0 })
            await QueueMessages.DeactivateItemsAsync(s, caller, m, id, row.GetAttributeValue<OptionSetValue>("statuscode")?.Value, ct);
        // Plan 2.15: the row's match codes when a column a rule matches on changed (Assign and SetState come through here too);
        // the organization switch turned off unpublishes every rule (ruling D11), in this transaction.
        InvalidateDuplicateRulesOnWrite(s, m);
        await duplicates.MatchCodes.RefreshAsync(s, m, id, row.Attributes.Keys, ct);
        if (m.LogicalName == Organization && row.Contains(DuplicateDetectionSchema.OrgEnabled) && row[DuplicateDetectionSchema.OrgEnabled] is not true)
            await duplicates.UnpublishAllAsync(s, null, ct);
        // A cancelled publish job (the only Update of a system job) puts its rule back to Unpublished at once (D10).
        if (m.LogicalName == AsyncOperation && row.Contains("statecode")) await duplicates.OnJobEndedAsync(s, id, ct);
        // Plan 2.10, ruling X2: the base currency's symbol and precision are copied to the organization in this transaction.
        if (m.LogicalName == CurrencySchema.Table && (row.Contains(CurrencySchema.Symbol) || row.Contains(CurrencySchema.Precision)))
            await CurrencyMessages.CopyBaseCurrencyAsync(s, id, ct);
        if (CurrencyMessages.InvalidatesCatalog(m.LogicalName)) CurrencyMessages.InvalidateAfterTransaction(currencies, s);
        if (before is not null)
            await WriteAuditAsync(s, caller, auditAction, AuditOperations.Update, m, id, before, await AuditImageAsync(s, m, id, audited, ct), ct);
        if (ComponentType.OfTable(m.LogicalName) is { } componentType) await MarkChangedAsync(s, caller, componentType, id, ct);
        await MarkParentChangedAsync(s, caller, m.LogicalName, id, null, ct);
    }

    /// <summary>
    /// The read-only <c>ismanaged</c> column of a row component (plan 2.6, ruling M14): written only by the layer operations,
    /// as <see cref="Caller.Internal"/> (any other caller's value is refused by <see cref="SystemTableRules"/> before this);
    /// a Create by anyone else writes false.
    /// </summary>
    private static void ApplyIsManaged(EntityMetadata m, IReadOnlyDictionary<string, AttributeMetadata> attrs, Entity entity, Entity row,
        Caller caller, bool isCreate)
    {
        if (ComponentType.OfTable(m.LogicalName) is null || !attrs.ContainsKey(SystemTableRules.IsManaged)) return;
        if (caller == Caller.Internal && entity.GetAttributeValue<bool?>(SystemTableRules.IsManaged) is { } given)
            row[SystemTableRules.IsManaged] = given;
        else if (isCreate) row[SystemTableRules.IsManaged] = false;
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
        Entity? target = null)
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
        if (target is not null && target.Attributes.TryGetValue(parent.Column, out var moved) && moved is EntityReference newParent)
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

    /// <summary>
    /// 400 for the Create of a form (<c>systemform</c>), view (<c>savedquery</c>) or system chart (<c>savedqueryvisualization</c>)
    /// of a table that a managed solution delivers with <c>CanCreateForms</c>, <c>CanCreateViews</c> or <c>CanCreateCharts</c>
    /// false (ruling M13; final review I4).
    /// </summary>
    private async Task DemandTableAllowsAsync(DataSession s, Entity entity, CancellationToken ct)
    {
        if (!TableBoundRows.TryGetValue(entity.LogicalName, out var bound)
            || !entity.Attributes.TryGetValue(bound.Column, out var value) || value is not string table
            || store.GetEntity(table) is not { MetadataId: { } tableId }
            || await ManagedProperties.StateAsync(s, ComponentType.Entity, tableId, ct) is not { } state) return;
        var allowed = bound.Flag switch
        {
            "CanCreateForms" => state.Flags.CanCreateForms,
            "CanCreateCharts" => state.Flags.CanCreateCharts,
            _ => state.Flags.CanCreateViews,
        };
        if (!allowed)
            throw ManagedProperties.TableFlagFalse($"{bound.What} '{entity.GetAttributeValue<string>("name") ?? entity.Id.ToString()}'", table,
                bound.Flag, state);
    }

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

    /// <summary>
    /// Plan 4.1b: the profile rules (<see cref="ProfileRules"/>) on <paramref name="row"/> as the row will be: the stored profile
    /// (<paramref name="stored"/>, none on a Create) with the change on top. An SMTP profile's Auto Detect is written as Basic.
    /// </summary>
    private static void ApplyProfileRules(Entity row, Entity? stored)
    {
        var merged = new Entity(row.LogicalName);
        if (stored is not null) foreach (var (name, value) in stored.Attributes) merged[name] = value;
        foreach (var (name, value) in row.Attributes) merged[name] = value;
        ProfileRules.Validate(merged);
        // The values the rules settled on (Auto Detect stored as Basic, an absent Graph incoming side taking the outgoing one).
        foreach (var column in new[] { "outgoingauthenticationprotocol", "incomingauthenticationprotocol", "incomingcredentialretrieval" })
        {
            var settled = merged.GetAttributeValue<OptionSetValue>(column);
            if (settled is not null && settled.Value != stored?.GetAttributeValue<OptionSetValue>(column)?.Value)
                row[column] = settled;
        }
    }

    /// <summary>The note body of a write (plan 2.11, A16): <see cref="Given"/> false when the write does not carry <c>documentbody</c>.</summary>
    private readonly record struct NoteBody(bool Given, string? Base64);

    /// <summary>
    /// Takes <c>documentbody</c> out of a write of <c>annotation</c> (it has no column: <see cref="ApplyNoteBodyAsync"/> stores it);
    /// <paramref name="entity"/> becomes a copy without it. Any other table: nothing given.
    /// </summary>
    private static NoteBody ExtractNoteBody(EntityMetadata m, ref Entity entity)
    {
        // Plan 4.1b: the body of an email attachment (activitymimeattachment.body) is kept in the file store the same way.
        var attribute = m.LogicalName switch
        {
            ActivitySchema.Annotation => ActivitySchema.DocumentBody,
            MailSchema.Attachment => MailSchema.AttachmentBody,
            _ => null,
        };
        if (attribute is null || !entity.Attributes.TryGetValue(attribute, out var value)) return default;
        entity = Copy(entity);
        entity.Attributes.Remove(attribute);
        return new NoteBody(true, value as string);
    }

    /// <summary>
    /// The attachment of an email, on its Create or Update (plan 4.1b, ruling P4), before its row is written: the email must be an open
    /// one (its row is locked), the file name not a blocked extension, and the body (base64, checked against
    /// <c>organization.maxuploadfilesize</c>) is stored by <see cref="AttachmentBodies"/>, replacing the previous one. <c>filesize</c>
    /// follows it; a new attachment takes the next <c>attachmentnumber</c> (when it names none) and <c>mimetype</c>, when not sent, the
    /// one of the file name's extension. A body sent null or empty clears it. An Update that sends no body keeps it, and a new
    /// <c>filename</c> or <c>mimetype</c> is copied to its <c>fileattachment</c> row. <c>email.attachmentcount</c> follows a Create.
    /// An attachment never moves to another email.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for a table other than an email, an email that is not open, a blocked
    /// extension, a body that is not base64 or a change of the email; 400 0x80044a02 over the limit.</exception>
    private async Task ApplyAttachmentAsync(DataSession s, EntityMetadata m, IReadOnlyDictionary<string, AttributeMetadata> attrs, Entity row,
        Guid id, NoteBody body, bool isCreate, CancellationToken ct)
    {
        var email = EntityOf(ActivitySchema.Email);
        string? fileName;
        if (isCreate)
        {
            var emailId = await EmailAttachments.DemandOpenEmailAsync(s, email, row.GetAttributeValue<EntityReference>(ActivitySchema.ObjectId), ct);
            fileName = row.GetAttributeValue<string>(ActivitySchema.FileName);
            await EmailAttachments.DemandNotBlockedAsync(s, fileName, ct);
            if (!row.Contains(MailSchema.AttachmentNumber))
                SetIfDefined(row, attrs, MailSchema.AttachmentNumber, await EmailAttachments.NextNumberAsync(s, m, emailId, ct));
            await EmailAttachments.RecountAsync(s, email, m, emailId, adjust: 1, ct);
        }
        else
        {
            var emailId = await EmailAttachments.StoredEmailAsync(s, m, id, ct) ?? throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, id);
            // Any objectid in the write must be the stored email: a null (also a Disassociate) detaches the attachment.
            if (row.Contains(ActivitySchema.ObjectId)
                && (row.GetAttributeValue<EntityReference>(ActivitySchema.ObjectId) is not { } moved || moved.Id != emailId || moved.LogicalName != ActivitySchema.Email))
                throw XrmdFaultException.InvalidArgument($"The attachment {id} belongs to the email {emailId}: it cannot be moved to another record or detached.");
            await EmailAttachments.DemandOpenAsync(s, email, emailId, ct);
            await LockRowAsync(s, m, id, ct);
            fileName = row.Contains(ActivitySchema.FileName)
                ? row.GetAttributeValue<string>(ActivitySchema.FileName)
                : await EmailAttachments.StoredStringAsync(s, m, id, ActivitySchema.FileName, ct);
            if (row.Contains(ActivitySchema.FileName) || body.Given) await EmailAttachments.DemandNotBlockedAsync(s, fileName, ct);
        }
        var bodies = files.Attachments;
        if (!body.Given)
        {
            if (isCreate) SetIfDefined(row, attrs, ActivitySchema.FileSize, 0);
            else if (row.Contains(ActivitySchema.FileName) || row.Contains(ActivitySchema.MimeType))
                await bodies.DescribeAsync(s, id, row.Contains(ActivitySchema.FileName) ? row.GetAttributeValue<string>(ActivitySchema.FileName) ?? "" : null,
                    row.Contains(ActivitySchema.MimeType) ? row.GetAttributeValue<string>(ActivitySchema.MimeType) ?? "" : null, ct);
            return;
        }
        if (string.IsNullOrEmpty(body.Base64))
        {
            if (!isCreate) await bodies.ClearAsync(s, id, ct);
            SetIfDefined(row, attrs, ActivitySchema.FileSize, 0);
            return;
        }
        var mimeType = row.GetAttributeValue<string>(ActivitySchema.MimeType);
        var stored = await bodies.WriteAsync(s, id, body.Base64, fileName, mimeType,
            MigrationScope.IsActive ? int.MaxValue : await MaxUploadFileSizeAsync(s, ct), ct);
        SetIfDefined(row, attrs, ActivitySchema.FileSize, checked((int)stored.Size));
        if (string.IsNullOrEmpty(mimeType)) SetIfDefined(row, attrs, ActivitySchema.MimeType, stored.MimeType);
    }

    /// <summary>
    /// The note body of a Create or Update of <c>annotation</c> (plan 2.11, A16), before the note's row is written. A body sent
    /// (base64) is checked against <c>organization.maxuploadfilesize</c> and stored by <see cref="AnnotationBodies"/> (replacing the
    /// previous one); <c>filesize</c> and <c>isdocument</c> follow it (a client's <c>isdocument</c> is ignored on every write) and <c>mimetype</c>, when not sent, is the one of the file name's
    /// extension (from memory: checklist 3g). A body sent null or empty clears it: <c>filesize</c> 0 and <c>isdocument</c> false. A
    /// Create without a body gets those two values too; an Update without one keeps the body, and a new <c>filename</c> or
    /// <c>mimetype</c> is copied to its <c>fileattachment</c> row.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for a body that is not base64; 400 0x80044a02 over the limit.</exception>
    private async Task ApplyNoteBodyAsync(DataSession s, EntityMetadata m, IReadOnlyDictionary<string, AttributeMetadata> attrs, Entity row,
        Guid id, NoteBody body, bool isCreate, CancellationToken ct)
    {
        var bodies = files.Annotations;
        // An Update locks the note first, as a body write of another transaction does: one body per note.
        if (!isCreate && (body.Given || row.Contains(ActivitySchema.FileName) || row.Contains(ActivitySchema.MimeType)))
            await LockRowAsync(s, m, id, ct);
        // isdocument is derived by the server from whether the note has a body, on every write: a client value is ignored (from
        // memory: checklist 3g).
        if (!body.Given)
        {
            row.Attributes.Remove(ActivitySchema.IsDocument);
            if (isCreate)
            {
                SetIfDefined(row, attrs, ActivitySchema.FileSize, 0);
                SetIfDefined(row, attrs, ActivitySchema.IsDocument, false);
            }
            else if (row.Contains(ActivitySchema.FileName) || row.Contains(ActivitySchema.MimeType))
                await bodies.DescribeAsync(s, id, row.Contains(ActivitySchema.FileName) ? row.GetAttributeValue<string>(ActivitySchema.FileName) ?? "" : null,
                    row.Contains(ActivitySchema.MimeType) ? row.GetAttributeValue<string>(ActivitySchema.MimeType) ?? "" : null, ct);
            return;
        }
        if (string.IsNullOrEmpty(body.Base64))
        {
            if (!isCreate) await bodies.ClearAsync(s, id, ct);
            SetIfDefined(row, attrs, ActivitySchema.FileSize, 0);
            SetIfDefined(row, attrs, ActivitySchema.IsDocument, false);
            return;
        }
        var fileName = row.Contains(ActivitySchema.FileName) || isCreate
            ? row.GetAttributeValue<string>(ActivitySchema.FileName)
            : await StoredStringAsync(s, m, id, ActivitySchema.FileName, ct);
        var mimeType = row.GetAttributeValue<string>(ActivitySchema.MimeType);
        var stored = await bodies.WriteAsync(s, id, body.Base64, fileName, mimeType,
            MigrationScope.IsActive ? int.MaxValue : await MaxUploadFileSizeAsync(s, ct), ct);
        SetIfDefined(row, attrs, ActivitySchema.FileSize, checked((int)stored.Size));
        SetIfDefined(row, attrs, ActivitySchema.IsDocument, true);
        // A new body without a mimetype takes the one of the file name's extension (from memory: checklist 3g).
        if (string.IsNullOrEmpty(mimeType)) SetIfDefined(row, attrs, ActivitySchema.MimeType, stored.MimeType);
    }

    /// <summary><c>organization.maxuploadfilesize</c>, its default (5 MB) when not set.</summary>
    internal static async Task<int> MaxUploadFileSizeAsync(DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command(
            s.Dialect.Limit($"SELECT {s.Quote(ActivitySchema.MaxUploadFileSize)} FROM {s.DataTable("organization")}", "1"));
        return await cmd.ExecuteScalarAsync(ct) is int value ? value : ActivitySchema.DefaultMaxUploadFileSize;
    }

    /// <summary>A string column of a stored row (null when empty or when the row does not exist).</summary>
    private static async Task<string?> StoredStringAsync(DataSession s, EntityMetadata m, Guid id, string column, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT {s.Quote(column)} FROM {s.DataTable(m.LogicalName)} WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// Takes an update lock on the record's row for the rest of the transaction, so that the audited "before" image read next
    /// is the one the following UPDATE or DELETE replaces; false when the row does not exist.
    /// </summary>
    private static async Task<bool> LockRowAsync(DataSession s, EntityMetadata m, Guid id, CancellationToken ct)
    {
        await s.KeyLockAsync(m.LogicalName, id, ct);
        // Plan 2.7a, Task 6: the key read with the lock clause (PostgreSQL's FOR UPDATE takes no aggregate), so that the row lock
        // is taken on both engines: a writer that locked the row first (a record team add on its template) is waited for.
        var key = s.Quote(SystemAttributes.PrimaryIdName(m));
        await using var cmd = s.Command(
            $"SELECT {key} FROM {s.DataTable(m.LogicalName)}{s.Dialect.LockHint(RowLock.UpdateRow)} " +
            $"WHERE {key} = {IdParameter}{s.Dialect.LockClause(RowLock.UpdateRow)}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) is not (null or DBNull);
    }

    /// <summary>The record with <paramref name="columns"/>, read unrestricted in <paramref name="s"/> (lookups carry their name).</summary>
    private Task<Entity> AuditImageAsync(DataSession s, EntityMetadata m, Guid id, IEnumerable<string> columns, CancellationToken ct) =>
        RetrieveAsync(s, Caller.Internal, m.LogicalName, id, new ColumnSet(columns.ToArray()), ct);

    /// <summary>The audit row of a Create (no <paramref name="before"/>), Update or Delete (no <paramref name="after"/>), if a column changed.</summary>
    private async Task WriteAuditAsync(DataSession s, Caller caller, int action, int operation, EntityMetadata m, Guid id, Entity? before,
        Entity? after, CancellationToken ct)
    {
        if (AuditWriter.AttributeChange(m, before, after) is { } change)
            await audit.WriteAsync(s, caller, action, operation, new EntityReference(m.LogicalName, id), change, ct);
    }

    /// <summary>400 0x8004F50B for a field share whose (record, column, principal) the unique index already holds.</summary>
    private XrmdFaultException DuplicateShare(Entity share)
    {
        var record = share.GetAttributeValue<EntityReference>(FieldSecurityTables.ShareObjectId);
        var attributeId = share.GetAttributeValue<Guid?>(FieldSecurityTables.ShareAttributeId);
        var column = store.GetEntity(record?.LogicalName ?? "")?.Attributes.FirstOrDefault(a => a.MetadataId == attributeId)?.LogicalName;
        return XrmdFaultException.ShareDuplicate(column ?? attributeId?.ToString() ?? "", record?.Id ?? Guid.Empty,
            share.GetAttributeValue<EntityReference>(FieldSecurityTables.SharePrincipalId)?.Id ?? Guid.Empty);
    }

    /// <summary>
    /// 412 DuplicateRecord for a write that a unique index refuses (e.g. <c>systemuser.azureactivedirectoryobjectid</c>,
    /// unique among the non-null values); on <c>transactioncurrency</c>, whose only unique index is on <c>isocurrencycode</c>, 400
    /// DuplicateIsoCurrencyCode (plan 2.10, ruling X3: a concurrent write took the code after the request rules checked it).
    /// </summary>
    private static XrmdFaultException DuplicateUniqueValue(string entity, Entity row) =>
        entity == CurrencySchema.Table
            ? CurrencyRules.DuplicateCode(row.GetAttributeValue<string>(CurrencySchema.IsoCode) ?? "")
            : new(ErrorCodes.DuplicateRecord, $"A record with the same value of a unique attribute already exists on entity '{entity}'.", 412);

    /// <exception cref="XrmdFaultException">ObjectDoesNotExist (404) when the record does not exist; for a <c>solution</c>, the
    /// refusals of <see cref="SolutionDeleter.RemoveContainerAsync"/> (system solution 403, managed solution 400); InvalidArgument
    /// (400) when a one-to-many relationship with Delete Restrict still has records pointing to the record.</exception>
    public Task DeleteAsync(DataSession s, Caller caller, string entityName, Guid id, CancellationToken ct) =>
        DeleteAsync(s, caller, entityName, id, null, ct);

    /// <summary>
    /// Deletes the record and applies the delete cascade of every one-to-many relationship whose referenced entity is the
    /// record's (in the transaction of the Delete): Restrict refuses the Delete while records point to it, Cascade deletes those
    /// records first (each through <paramref name="nested"/>, so as a Delete message of the pipeline with its own privilege
    /// check, plug-in steps and cascades; directly through this method when there is no pipeline), RemoveLink clears their lookup.
    /// A record already being deleted by the same cascade (a cycle, a self-reference) is not deleted twice.
    /// </summary>
    private async Task DeleteAsync(DataSession s, Caller caller, string entityName, Guid id, NestedRequest? nested, CancellationToken ct)
    {
        var m = EntityOf(entityName);
        await DemandRowDeleteAllowedAsync(s, caller, m.LogicalName, id, ct);
        // Plan 4.1a: before the rule that a team owning records stays (its default queue is not one that keeps it).
        if (m.LogicalName == Team) await DeleteDefaultQueueAsync(s, caller, id, ct);
        // Plan 4.1a: a queue goes only once its items are inactive (they then go by the cascade of queue_entries).
        if (m.LogicalName == QueueSchema.Queue && !MigrationScope.IsActive) await QueueMessages.DemandNoActiveItemsAsync(s, id, ct);
        // Plan 4.1b: a queue goes with its mailbox and the mailbox's secrets (internal deletes: the caller's rights are those on the queue).
        if (m.LogicalName == QueueSchema.Queue && store.GetEntity(MailSchema.Mailbox) is not null)
            foreach (var mailbox in await Mailboxes.DetachAsync(s, m.LogicalName, id, ct)) await DeleteAsync(s, Caller.Internal, MailSchema.Mailbox, mailbox, null, ct);
        await CheckDeleteRulesAsync(s, m.LogicalName, id, ct);
        // Plan 2.14 (W14): an action's message goes with its definition, before the cascades (its activations) run.
        if (writes is not null && !MigrationScope.IsActive) await writes.OnDeletingAsync(s, m.LogicalName, id, ct);
        // Plan 2.10, final review I1: the base currency and a currency in a user's settings, read under locks in the transaction.
        if (m.LogicalName == CurrencySchema.Table) await CurrencyRules.EnforceDeleteAsync(id, s, ct);
        var deleting = DeleteCascades.GetValue(s, static _ => new DeleteCascade());
        deleting.Depth++;
        try
        {
            deleting.Records.Add((m.LogicalName, id));
            // Plan 2.15, ruling D4: a condition of a published rule goes only with its rule.
            if (m.LogicalName == DuplicateDetectionSchema.Condition)
                await DuplicateRuleRules.DemandConditionDeleteAsync(s, id, rule => deleting.Records.Contains((DuplicateDetectionSchema.Rule, rule)), ct);
            await ApplyDeleteCascadesAsync(s, caller, m, id, deleting, nested, ct);
            await DeleteAccessTeamsAsync(s, caller, m, id, ct);
            // A managed solution's row goes only as the last step of its uninstall (a layer operation, ruling R2).
            if (m.LogicalName == SolutionDeleter.Table) await _solutionDeleter.RemoveContainerAsync(s, id, ManagedProperties.Bypassed(caller), ct);
            var parentComponent = caller == Caller.Internal ? null : await ParentComponentAsync(s, m.LogicalName, id, ct);
            // Plan 2.11 (A1, A10): an activity's party rows and pointer row go first, then its own row, in this transaction.
            if (ActivitySchema.IsActivityTable(m))
            {
                // The activity row is locked first, as an Update locks it before its party and pointer rows (no lock-order inversion).
                await LockRowAsync(s, m, id, ct);
                await ActivityParties.DeleteAsync(s, id, ct);
                await ActivityPointerSync.DeleteAsync(s, id, ct);
            }
            // The audited columns of the record, read before it goes under an update lock (only when the table is audited).
            Entity? before = null;
            if (await audit.IsAuditedAsync(s, m, ct) && await LockRowAsync(s, m, id, ct))
                before = await AuditImageAsync(s, m, id, AuditPolicy.AuditedAttributes(m).Select(a => a.LogicalName), ct);
            // Plan 4.1b: an attachment's email counts its attachments (unless the email is the one being deleted); locked before the row goes.
            Guid? attachedTo = null;
            if (m.LogicalName == MailSchema.Attachment && await EmailAttachments.StoredEmailAsync(s, m, id, ct) is { } owner
                && !deleting.Records.Contains((ActivitySchema.Email, owner)))
            {
                attachedTo = owner;
                await LockRowAsync(s, EntityOf(ActivitySchema.Email), owner, ct);
            }
            var sql = $"DELETE FROM {s.DataTable(m.LogicalName)} WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}";
            var parameters = new[] { new DetachedParameter(IdParameter, DbType.Guid, id) };
            if (await ExecuteNonQueryAsync(s, sql, parameters, ct) == 0)
                throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, id);
            if (attachedTo is { } counted) await EmailAttachments.RecountAsync(s, EntityOf(ActivitySchema.Email), m, counted, adjust: 0, ct);
            // Plan 2.14 (W4, W10): before the links to the record are cleared (a job still names its deleted activation).
            if (writes is not null && !MigrationScope.IsActive) await writes.OnDeletedAsync(s, m.LogicalName, id, ct);
            if (CurrencyMessages.InvalidatesCatalog(m.LogicalName)) CurrencyMessages.InvalidateAfterTransaction(currencies, s);
            // Plan 2.13: a tracked table's deletion is logged in this transaction (cascaded children come through here too).
            if (store.ChangeTrackingTables.Contains(m.LogicalName)) await ChangeTrackingLog.RecordDeleteAsync(s, m.LogicalName, id, ct);
            // Plan 2.15: the record's match codes go with it (cascaded children too); a rule's match code table goes with the
            // rule, whatever its state (ruling D4).
            InvalidateDuplicateRulesOnWrite(s, m);
            await duplicates.MatchCodes.RemoveAsync(s, m.LogicalName, id, ct);
            if (m.LogicalName == DuplicateDetectionSchema.Rule) await duplicates.OnRuleDeletedAsync(s, id, ct);
            if (m.LogicalName == AsyncOperation) await duplicates.OnJobEndedAsync(s, id, ct);
            if (before is not null) await WriteAuditAsync(s, caller, AuditActions.Delete, AuditOperations.Delete, m, id, before, null, ct);
            await DeleteDependentRowsAsync(s, m.LogicalName, id, ct);
            // Plan 2.11 (A10, pre-flight C10): a deleted party record keeps its party rows, marked ispartydeleted (users are never deleted).
            if (ActivitySchema.PartyRecordTables.Contains(m.LogicalName) || m.LogicalName == Team)
                await _parties.MarkPartyDeletedAsync(s, m.LogicalName, id, ct);
            await RemoveLinksAsync(s, m.LogicalName, id, ct);
            await files.DeleteRecordFilesAsync(s, m, id, ct); // ruling F8: the blobs go after the commit
            // Plan 3.5 UI-1d (ruling R2): a custom control's files go with its row, in this transaction.
            if (m.LogicalName == CustomControlSchema.Table) await CustomControlFiles.DeleteRowsAsync(s, id, ct);
            // Plan 4.1b: the write-only mail secrets go with their record, in this transaction.
            if (MailSecrets.HasSecrets(m.LogicalName)) await MailSecrets.DeleteAsync(s, id, ct);
            // Plan 4.1c: an email's message keys go with it, in this transaction.
            if (m.LogicalName == ActivitySchema.Email) await MailMessageKeys.DeleteAsync(s, id, ct);
            // Plan 4.1c, task 9: a mailbox's sync state goes with it (its sync job ends at its next run).
            if (m.LogicalName == MailSchema.Mailbox) await MailboxSyncState.DeleteAsync(s, id, ct);
            var images = m.LogicalName == StepTable ? await DeleteStepImagesAsync(s, id, ct) : [];
            if (ComponentType.OfTable(m.LogicalName) is not null) await tracker.RemoveDeletedAsync(s, [id, .. images], ct);
            if (parentComponent is not null) await MarkParentChangedAsync(s, caller, m.LogicalName, id, parentComponent, ct);
        }
        finally
        {
            if (--deleting.Depth == 0) DeleteCascades.Remove(s);
        }
    }

    /// <summary>
    /// Plan 2.16, ruling A8: the system-managed access teams of a deleted record go with it, before its dependent rows. They are
    /// plain team Deletes of this class (no nested Delete message: the record's Delete is the message), so their memberships
    /// and shares go as for any team (<see cref="DeleteDependentRowsAsync"/>). Only for a table <c>team.regardingobjectid</c>
    /// targets (a target is never removed while a team refers to it). The record's row is update-locked first, then the teams
    /// are read on <c>UX_team_record_template</c> (its filter repeated, so the seek and its key-range lock use that index): the
    /// order AddUserToRecordTeam locks them in, so the two queue up on the record instead of deadlocking.
    /// </summary>
    private async Task DeleteAccessTeamsAsync(DataSession s, Caller caller, EntityMetadata m, Guid id, CancellationToken ct)
    {
        if (m.LogicalName == Team
            || store.GetEntity(Team)?.Attributes?.OfType<LookupAttributeMetadata>()
                .FirstOrDefault(a => a.LogicalName == AccessTeamSchema.RegardingObjectId)?.Targets?.Contains(m.LogicalName) != true)
            return;
        var teams = new List<Guid>();
        await s.KeyLockAsync(m.LogicalName, id, ct);
        await using (var cmd = s.Command(
                         $"SELECT COUNT(*) FROM {s.DataTable(m.LogicalName)}{s.Dialect.LockHint(RowLock.UpdateRowRange)} " +
                         $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}; " +
                         $"SELECT {s.Quote(SystemAttributes.PrimaryIdName(Team))} FROM {s.DataTable(Team)}{s.Dialect.LockHint(RowLock.UpdateRange)} " +
                         $"WHERE {s.Quote(AccessTeamSchema.RegardingObjectId)} = {IdParameter} AND {s.Quote(AccessTeamSchema.TeamTemplateId)} IS NOT NULL " +
                         $"AND {s.Quote(AccessTeamSchema.SystemManaged)} = {s.Dialect.BooleanLiteral(true)} " +
                         // Final re-review N2: the teams of this table's record; a record of another table with the same id keeps its own.
                         $"AND {s.Quote(AccessTeamSchema.RegardingObjectId + TypeMapping.TypeColumnSuffix)} = @otc{s.Dialect.LockClause(RowLock.UpdateRange)}"))
        {
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            cmd.AddParameter("@otc", DbType.Int32, m.ObjectTypeCode ?? 0);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            await rd.NextResultAsync(ct);
            while (await rd.ReadAsync(ct)) teams.Add(rd.GetGuid(0));
        }
        if (teams.Count == 0) return;
        foreach (var team in teams) await DeleteAsync(s, caller, Team, team, null, ct);
        security.InvalidateOnCommit(s);
    }

    /// <summary>The records a Delete and the deletes it cascades to are removing, shared by the nested Deletes of one session.</summary>
    private sealed class DeleteCascade
    {
        public HashSet<(string Entity, Guid Id)> Records { get; } = [];
        public int Depth { get; set; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataSession, DeleteCascade> DeleteCascades = new();

    /// <summary>
    /// Delete Restrict and Delete Cascade of the one-to-many relationships whose referenced entity is <paramref name="m"/>, before
    /// the record's row goes: first every Restrict (a record that points to it and is not itself being deleted refuses the
    /// Delete), then every Cascade (the records that point to it are deleted). The record must exist (404 otherwise), so that
    /// nothing is deleted on behalf of a missing record.
    /// </summary>
    private async Task ApplyDeleteCascadesAsync(DataSession s, Caller caller, EntityMetadata m, Guid id, DeleteCascade deleting,
        NestedRequest? nested, CancellationToken ct)
    {
        var relationships = store.GetOneToManyRelationships().Where(r => r.ReferencedEntity == m.LogicalName
            && r.CascadeConfiguration?.Delete is CascadeType.Restrict or CascadeType.Cascade).ToList();
        if (relationships.Count == 0) return;
        if (!await ExistsWhereAsync(s, m.LogicalName, SystemAttributes.PrimaryIdName(m), id, ct))
            throw XrmdFaultException.ObjectDoesNotExist(m.LogicalName, id);

        foreach (var r in relationships.Where(r => r.CascadeConfiguration!.Delete == CascadeType.Restrict))
        {
            var children = await ReferencingRecordsAsync(s, r, id, ct);
            if (children.Any(child => !deleting.Records.Contains((r.ReferencingEntity, child))))
                throw XrmdFaultException.InvalidArgument(
                    $"The {m.LogicalName} record {id} cannot be deleted: {r.ReferencingEntity} records still point to it through the " +
                    $"relationship '{r.SchemaName}', whose delete behavior is Restrict. Delete them or clear their '{r.ReferencingAttribute}' first.");
        }
        foreach (var r in relationships.Where(r => r.CascadeConfiguration!.Delete == CascadeType.Cascade))
            foreach (var child in await ReferencingRecordsAsync(s, r, id, ct))
            {
                if (deleting.Records.Contains((r.ReferencingEntity, child))) continue; // a cycle or a self-reference
                // The rows of duplicaterecord (plan 2.15, ruling D15) and processtrigger (plan 2.14, ruling W3) refuse a Delete request:
                // they go with their job or their process in the core, so on purpose no Delete step runs for them (the table refuses
                // every client write, a step registered on it included). The same pattern for appmodulecomponent (plan 3.5 UI-1a final
                // review, I3): the table is import-only for requests (AppModuleSchema.ImportOnly), so its rows go with their app module
                // in the core, not through a nested Delete. Likewise postregarding (plan 3.5 UI-1g), which refuses every client write;
                // its posts then go as nested Deletes (their steps fire), through the nested Delete passed on below. The attachments of an email
                // (plan 4.1b) go as nested Deletes too (their steps fire); the guard accepts that marked cascade without Write on the email.
                if (nested is not null && r.ReferencingEntity is not (DuplicateDetectionSchema.Record or WorkflowSchema.TriggerTable
                        or AppModuleSchema.Component or PostSchema.Regarding))
                {
                    // A core cascade (plan 2.14, ruling W5): its plug-in steps fire, but it triggers no workflow.
                    var cascaded = new DeleteRequest { Target = new EntityReference(r.ReferencingEntity, child) };
                    CascadeHint.Mark(cascaded);
                    await nested(cascaded, ct);
                }
                else
                    await DeleteAsync(s, caller, r.ReferencingEntity, child, r.ReferencingEntity == PostSchema.Regarding ? nested : null, ct);
            }
    }

    /// <summary>The ids of the records of <paramref name="r"/>'s referencing entity whose lookup holds <paramref name="id"/>.</summary>
    private async Task<List<Guid>> ReferencingRecordsAsync(DataSession s, OneToManyRelationshipMetadata r, Guid id, CancellationToken ct)
    {
        var referencing = store.GetEntity(r.ReferencingEntity);
        var lookup = referencing?.Attributes?.FirstOrDefault(a => a.LogicalName == r.ReferencingAttribute);
        if (referencing is null || lookup is null) return [];
        var (where, parameters) = LookupMatch(s.Dialect, lookup, r.ReferencedEntity, id);
        await using var cmd = s.Command(
            $"SELECT {s.Quote(SystemAttributes.PrimaryIdName(referencing))} FROM {s.DataTable(referencing.LogicalName)}{s.Dialect.LockHint(RowLock.UpdateRange)} " +
            $"WHERE {where}{s.Dialect.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameters(parameters);
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// Final re-review N4: the condition that <paramref name="lookup"/> names the record <paramref name="id"/> of
    /// <paramref name="referencedEntity"/>. A polymorphic lookup (Customer, Owner, regarding, a note's <c>objectid</c>, …) also
    /// matches its type column, as <see cref="ActivityParties.MarkPartyDeletedAsync"/> does: a record of another of its targets
    /// with the same id is not the deleted one.
    /// </summary>
    private (string Sql, DbParameter[] Parameters) LookupMatch(ISqlDialect d, AttributeMetadata lookup, string referencedEntity, Guid id)
    {
        var idParameter = new DetachedParameter(IdParameter, DbType.Guid, id);
        if (!TypeMapping.IsPolymorphicLookup(lookup)) return ($"{d.Quote(lookup.LogicalName)} = {IdParameter}", [idParameter]);
        return ($"{d.Quote(lookup.LogicalName)} = {IdParameter} AND {d.Quote(lookup.LogicalName + TypeMapping.TypeColumnSuffix)} = @otc",
            [idParameter, new DetachedParameter("@otc", DbType.Int32, EntityOf(referencedEntity).ObjectTypeCode ?? 0)]);
    }

    private const string StepTable = "sdkmessageprocessingstep";

    /// <summary>
    /// Deletes the images of a deleted step (Dataverse deletes them with it) and returns their ids, to leave every solution.
    /// </summary>
    private static async Task<Guid[]> DeleteStepImagesAsync(DataSession s, Guid stepId, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"DELETE FROM {s.DataTable("sdkmessageprocessingstepimage")}{s.Dialect.ReturningInline(["sdkmessageprocessingstepimageid"], RowImage.Deleted)} " +
            $"WHERE sdkmessageprocessingstepid = {IdParameter}{s.Dialect.ReturningTrailing(["sdkmessageprocessingstepimageid"], RowImage.Deleted)}");
        cmd.AddParameter(IdParameter, DbType.Guid, stepId);
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return [.. ids];
    }

    /// <summary>
    /// The integrity rules of Delete that depend on the entity: users are never deleted; a business unit with users, teams or
    /// child business units, and a team that owns records, cannot be deleted.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) when a rule forbids the Delete.</exception>
    private async Task CheckDeleteRulesAsync(DataSession s, string entity, Guid id, CancellationToken ct)
    {
        switch (entity)
        {
            case SystemUser:
                throw XrmdFaultException.InvalidArgument("Users cannot be deleted; set isdisabled instead.");
            case BusinessUnit:
                if (await ExistsWhereAsync(s, SystemUser, BusinessUnitId, id, ct)
                    || await ExistsWhereAsync(s, Team, BusinessUnitId, id, ct)
                    || await ExistsWhereAsync(s, BusinessUnit, ParentBusinessUnitId, id, ct))
                    throw XrmdFaultException.InvalidArgument(
                        $"The business unit {id} has users, teams or child business units and cannot be deleted.");
                break;
            case Team:
                foreach (var owned in store.GetAllEntities().Where(e =>
                             e.OwnershipType is OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned
                             && e.Attributes?.Any(a => a.LogicalName == "owningteam") == true))
                    if (await ExistsWhereAsync(s, owned.LogicalName, "owningteam", id, ct))
                        throw XrmdFaultException.InvalidArgument($"The team {id} owns records and cannot be deleted.");
                break;
        }
    }

    /// <summary>
    /// The rows that hang from a deleted record, deleted in the transaction of the Delete: the shares and field shares of the
    /// record and, for a team or a role, membership, role assignments, privileges, field security profile links and the shares
    /// and field shares a team holds; for a role or an app module, its app links (<c>appmoduleroles</c>); for a field security profile, its user and team links (its permissions go by the cascade
    /// of <c>lk_fieldpermission_fieldsecurityprofileid</c>); the record's associations in the
    /// intersect of every saved many-to-many relationship it is a side of (both columns of a self-referential one).
    /// </summary>
    private async Task DeleteDependentRowsAsync(DataSession s, string entity, Guid id, CancellationToken ct)
    {
        (string Table, string Column)[] dependents = entity switch
        {
            Team => [("teammembership", "teamid"), ("teamroles", "teamid"), (PrincipalObjectAccess, "principalid"),
                (FieldSecurityTables.TeamProfiles, "teamid"), (FieldSecurityTables.Share, FieldSecurityTables.SharePrincipalId)],
            // Users are never deleted (CheckDeleteRulesAsync); listed so that the rule holds should that change.
            SystemUser => [(FieldSecurityTables.UserProfiles, "systemuserid"), (FieldSecurityTables.Share, FieldSecurityTables.SharePrincipalId)],
            // appmoduleroles has no many-to-many metadata (ruling R1'), so the association cleanup below does not see it.
            Role => [("systemuserroles", "roleid"), ("teamroles", "roleid"), ("roleprivileges", "roleid"), (AppModuleSchema.RolesIntersect, "roleid")],
            AppModule => [(AppModuleSchema.RolesIntersect, "appmoduleid")],
            FieldSecurityTables.Profile => [(FieldSecurityTables.UserProfiles, FieldSecurityTables.ProfileId),
                (FieldSecurityTables.TeamProfiles, FieldSecurityTables.ProfileId)],
            _ => [],
        };
        var associations = store.GetManyToManyRelationships().SelectMany(r =>
            (r.Entity1LogicalName == entity ? [(r.IntersectEntityName, r.Entity1IntersectAttribute)] : Array.Empty<(string, string)>())
            .Concat(r.Entity2LogicalName == entity ? [(r.IntersectEntityName, r.Entity2IntersectAttribute)] : Array.Empty<(string, string)>()));
        // Final review C2: the record's own shares and field shares are those of its id and its table; a record of another table
        // with the same id keeps its own. principalobjectaccess holds the table's object type code, a field share its logical name.
        if (store.GetEntity(PrincipalObjectAccess) is not null)
            await ExecuteNonQueryAsync(s,
                $"DELETE FROM {s.DataTable(PrincipalObjectAccess)} WHERE {s.Quote("objectid")} = {IdParameter} AND {s.Quote("objecttypecode")} = @otc",
                [
                    new DetachedParameter(IdParameter, DbType.Guid, id),
                    new DetachedParameter("@otc", DbType.Int32, EntityOf(entity).ObjectTypeCode ?? 0),
                ], ct);
        if (store.GetEntity(FieldSecurityTables.Share) is not null)
            await ExecuteNonQueryAsync(s,
                $"DELETE FROM {s.DataTable(FieldSecurityTables.Share)} " +
                $"WHERE {s.Quote(FieldSecurityTables.ShareObjectId)} = {IdParameter} AND {s.Quote(FieldSecurityTables.ShareObjectTypeCodeColumn)} = @table",
                [
                    new DetachedParameter(IdParameter, DbType.Guid, id),
                    new DetachedParameter("@table", DbType.String, entity, 128),
                ], ct);
        foreach (var (table, column) in dependents.Concat(associations))
        {
            if (store.GetEntity(table) is null) continue;
            await ExecuteNonQueryAsync(s, $"DELETE FROM {s.DataTable(table)} WHERE {s.Quote(column)} = {IdParameter}",
                [new DetachedParameter(IdParameter, DbType.Guid, id)], ct);
        }
    }

    /// <summary>Above this many records cleared by one RemoveLink, the table is woken at once rather than record by record.</summary>
    private const int RemoveLinkWakeBatch = 100;

    /// <summary>
    /// The delete cascade RemoveLink of the one-to-many relationships whose referenced entity is <paramref name="entity"/>
    /// (those of Customer columns, the seeded <c>contact_customer_contacts</c> and <c>account_primary_contact</c> among them): the records that pointed to the
    /// deleted one keep their row and lose the link (the lookup and its type column are set to null), in the transaction of
    /// the Delete. The regarding relationships to <c>activitypointer</c> are skipped (plan 2.11, A6).
    /// </summary>
    private async Task RemoveLinksAsync(DataSession s, string entity, Guid id, CancellationToken ct)
    {
        foreach (var r in store.GetOneToManyRelationships().Where(r =>
                     r.ReferencedEntity == entity && r.CascadeConfiguration?.Delete == CascadeType.RemoveLink))
        {
            // Plan 2.11 (A6, pre-flight C12): the regarding relationships to activitypointer (Account_ActivityPointers, …) carry
            // RemoveLink, but the pointer row is kept by ActivityPointerSync with its activity, which the Cascade of the activity
            // table's own relationship deletes: the cascade engine leaves it alone.
            if (r.ReferencingEntity == ActivitySchema.Pointer && r.ReferencingAttribute == ActivitySchema.RegardingObjectId) continue;
            var referencing = store.GetEntity(r.ReferencingEntity);
            var lookup = referencing?.Attributes?.FirstOrDefault(a => a.LogicalName == r.ReferencingAttribute);
            if (referencing is null || lookup is null) continue;
            // Plan 2.15: the rows whose cleared lookup a rule matches on get their codes again once it is cleared.
            var coded = await duplicates.MatchCodes.RowsReferencingAsync(s, referencing, r.ReferencingAttribute, id, ct);
            var columns = TypeMapping.ToColumns(lookup).Select(c => c.Name).ToList();
            var (where, parameters) = LookupMatch(s.Dialect, lookup, entity, id);
            var update = $"UPDATE {s.DataTable(r.ReferencingEntity)} SET {string.Join(", ", columns.Select(c => $"{s.Quote(c)} = NULL"))}";
            if (writes is null)
                await ExecuteNonQueryAsync(s, update + $" WHERE {where}", parameters, ct);
            else
            {
                // Plan 2.14 (W10): each cleared record is a write of it; past a batch, every job waiting on the table wakes at once.
                var cleared = new List<Guid>();
                string[] returned = [s.Quote(SystemAttributes.PrimaryIdName(referencing))];
                await using (var cmd = s.Command(update + $"{s.Dialect.ReturningInline(returned)} WHERE {where}{s.Dialect.ReturningTrailing(returned)}"))
                {
                    cmd.AddParameters(parameters);
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct)) cleared.Add(rd.GetGuid(0));
                }
                if (cleared.Count > RemoveLinkWakeBatch) await writes.OnWrittenAsync(s, r.ReferencingEntity, null, ct);
                else
                    foreach (var record in cleared)
                        await writes.OnWrittenAsync(s, r.ReferencingEntity, record, ct);
            }
            await duplicates.MatchCodes.RefreshManyAsync(s, referencing, coded, ct);
        }
    }

    /// <summary>
    /// Plan 2.15: a write of a duplicate rule, of one of its conditions or of the organization (its duplicate detection switch)
    /// drops the cached rules now and after the transaction's outcome.
    /// </summary>
    private void InvalidateDuplicateRulesOnWrite(DataSession s, EntityMetadata m)
    {
        if (m.LogicalName is DuplicateDetectionSchema.Rule or DuplicateDetectionSchema.Condition or Organization)
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

    /// <summary>
    /// Create rules on business units: a systemuser or team gets <c>businessunitid</c> (default: the caller's) and
    /// <c>owningbusinessunit</c> follows it; a business unit must have a parent.
    /// </summary>
    private static void ApplyBusinessUnitOnCreate(string entity, Entity row, IReadOnlyDictionary<string, AttributeMetadata> attrs, Caller caller)
    {
        switch (entity)
        {
            case SystemUser or Team:
                var bu = row.GetAttributeValue<EntityReference>(BusinessUnitId);
                if (bu is null)
                {
                    if (caller.BusinessUnitId == Guid.Empty)
                        throw XrmdFaultException.InvalidArgument($"Attribute '{BusinessUnitId}' of entity '{entity}' is required.");
                    bu = new EntityReference(BusinessUnit, caller.BusinessUnitId);
                    row[BusinessUnitId] = bu;
                }
                SetIfDefined(row, attrs, "owningbusinessunit", new EntityReference(BusinessUnit, bu.Id));
                break;
            case BusinessUnit when row.GetAttributeValue<EntityReference>(ParentBusinessUnitId) is null:
                throw XrmdFaultException.InvalidArgument("A business unit must have a parent business unit.");
        }
    }

    /// <summary>
    /// Update rules on business units: <c>owningbusinessunit</c> of a systemuser or team follows a new <c>businessunitid</c>;
    /// the root business unit keeps no parent and the others keep one that does not close a loop.
    /// </summary>
    private async Task ApplyBusinessUnitOnUpdateAsync(
        DataSession s, string entity, Guid id, Entity row, IReadOnlyDictionary<string, AttributeMetadata> attrs, CancellationToken ct)
    {
        switch (entity)
        {
            case SystemUser or Team when row.Contains(BusinessUnitId):
                var bu = row.GetAttributeValue<EntityReference>(BusinessUnitId)
                         ?? throw XrmdFaultException.InvalidArgument($"Attribute '{BusinessUnitId}' of entity '{entity}' cannot be null.");
                SetIfDefined(row, attrs, "owningbusinessunit", new EntityReference(BusinessUnit, bu.Id));
                break;
            case BusinessUnit when row.Contains(ParentBusinessUnitId):
                var parent = row.GetAttributeValue<EntityReference>(ParentBusinessUnitId);
                Guid? current;
                await using (var cmd = s.Command(
                    $"SELECT parentbusinessunitid FROM {s.DataTable(BusinessUnit)}{s.Dialect.LockHint(RowLock.Shared)} WHERE businessunitid = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}"))
                {
                    cmd.AddParameter(IdParameter, DbType.Guid, id);
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(BusinessUnit, id);
                    current = rd.IsDBNull(0) ? null : rd.GetGuid(0);
                }

                if (current is null)
                {
                    if (parent is not null)
                        throw XrmdFaultException.InvalidArgument("The root business unit cannot have a parent business unit.");
                    row.Attributes.Remove(ParentBusinessUnitId); // root stays root
                }
                else if (parent is null)
                {
                    throw XrmdFaultException.InvalidArgument("A business unit must have a parent business unit.");
                }
                else if (parent.Id != current)
                {
                    await EnsureNoLoopAsync(s, id, parent.Id, ct);
                }
                break;
        }
    }

    /// <summary>Walks up from <paramref name="newParent"/>; reaching <paramref name="id"/> means the hierarchy would loop.</summary>
    private static async Task EnsureNoLoopAsync(DataSession s, Guid id, Guid newParent, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        Guid? cursor = newParent;
        while (cursor is { } node)
        {
            if (node == id || !visited.Add(node))
                throw XrmdFaultException.InvalidArgument("Business unit hierarchy cannot contain a loop.");
            await using var cmd = s.Command(
                $"SELECT parentbusinessunitid FROM {s.DataTable(BusinessUnit)}{s.Dialect.LockHint(RowLock.Shared)} WHERE businessunitid = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
            cmd.AddParameter(IdParameter, DbType.Guid, node);
            var next = await cmd.ExecuteScalarAsync(ct);
            cursor = next is Guid g ? g : null;
        }
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

    /// <summary>Runs a query already in the <see cref="QueryModel"/> form (see <see cref="QueryExecutor.ExecuteAsync(QueryModel, Caller, DataSession, CancellationToken)"/>).</summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for an invalid or unsupported query;
    /// AggregateQueryRecordLimitExceeded (400).</exception>
    public async Task<EntityCollection> RetrieveMultipleAsync(DataSession s, Caller caller, QueryModel query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            return await queryExecutor.ExecuteAsync(query, caller, s, ct);
        }
        catch (XrmdValidationException ex)
        {
            throw XrmdFaultException.InvalidArgument(ex.Message);
        }
    }

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

    /// <summary>
    /// The checks of Create/Update that need no database (so that they precede any connection). Changes <paramref name="entity"/>
    /// in place: the ids of key attributes are filled in, the id of an Update is set, and the values of calculated columns are
    /// removed (ignored, plan 2.12a ruling K5), so a caller reusing the same <see cref="Entity"/> object sees them gone.
    /// </summary>
    internal (EntityMetadata Metadata, Dictionary<string, AttributeMetadata> Attributes) ValidateWrite(Entity entity, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var m = EntityOf(entity.LogicalName);
        // Plan 3.4: ids given by key attributes are filled in first (refused on a standard table).
        ElasticRecordKey.Normalize(m, entity, isCreate, store.GetEntity);
        var attrs = AttributesOf(m);
        // Plan 2.10, ruling X3: a currency's rate out of range is InvalidExchangeRate, not the generic range error.
        CurrencyRules.DemandRateInRange(entity);
        EntityValidator.Validate(entity, m, attrs, isCreate);
        // Plan 2.12a, ruling K5 (and 2.12c F9 for a formula column): a value sent for a calculated column is ignored; it is dropped here, so that it never reaches a
        // plug-in's Target nor the audit of the write. Plan 2.12b, ruling R11: the same for a rollup column, its companions and the
        // _base of a Currency rollup (only the calculation writes them).
        foreach (var name in entity.Attributes.Keys.Where(n => attrs.GetValueOrDefault(n) is { } a
                     && (ComputedColumnRules.IsComputed(a) || RollupColumns.IsRollupValue(a, attrs.GetValueOrDefault))).ToList())
            entity.Attributes.Remove(name);
        // The full name of a contact is the server's (ruling C3): a value is refused rather than silently replaced.
        // On update a value equal to the stored one is a no-op, which only the database can tell: UpdateAsync refuses the rest.
        if (isCreate && IsContact(m)) DemandNoFullName(entity);
        ElasticWriteRules.DemandWrite(m, entity, isCreate);
        if (!isCreate) UpdateId(entity, m, SystemAttributes.PrimaryIdName(m));
        return (m, attrs);
    }

    /// <summary>
    /// A retrieved contact sent back to Update carries its <c>fullname</c>: a value equal to the stored one is a no-op and is
    /// dropped; a different one stays in the entity, which <see cref="ValidateWrite"/> refuses (400).
    /// </summary>
    private async Task<Entity> WithoutUnchangedFullNameAsync(DataSession s, Entity entity, CancellationToken ct)
    {
        if (entity.LogicalName != ContactFullName.Contact || !entity.Contains(ContactFullName.FullName)
            || store.GetEntity(entity.LogicalName) is not { } m || !IsContact(m)) return entity;
        var id = entity.Id != Guid.Empty ? entity.Id : entity.GetAttributeValue<Guid?>("contactid") ?? Guid.Empty;
        if (id == Guid.Empty) return entity;
        await using var cmd = s.Command(
            $"SELECT fullname FROM {s.DataTable(ContactFullName.Contact)} WHERE contactid = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return entity;
        var stored = rd.IsDBNull(0) ? null : rd.GetString(0);
        return Equals(entity[ContactFullName.FullName], stored) ? Without(entity, ContactFullName.FullName) : entity;
    }

    private static void DemandNoFullName(Entity entity)
    {
        if (entity.Contains(ContactFullName.FullName))
            throw XrmdFaultException.InvalidArgument(
                $"Attribute '{ContactFullName.FullName}' of entity '{ContactFullName.Contact}' is computed by the server " +
                $"from {string.Join(", ", ContactFullName.Parts)} and cannot be set.");
    }

    private static bool IsContact(EntityMetadata m) =>
        m.LogicalName == ContactFullName.Contact && MetadataStore.IsSystemEntity(m);

    /// <summary>
    /// Sets <c>fullname</c> of a contact row from its name parts (those not in the row are read from the stored record on an
    /// update) in the order of <c>organization.fullnameconventioncode</c>. An update that touches no name part leaves it alone.
    /// </summary>
    private async Task ApplyFullNameAsync(DataSession s, Entity row, Guid id, bool isCreate, CancellationToken ct)
    {
        if (!ContactFullName.Parts.Any(row.Contains)) return;
        var parts = ContactFullName.Parts.ToDictionary(p => p, p => row.Contains(p) ? row.GetAttributeValue<string>(p) : null);
        if (!isCreate && ContactFullName.Parts.Any(p => !row.Contains(p)))
        {
            await using var cmd = s.Command(
                $"SELECT firstname, middlename, lastname FROM {s.DataTable(ContactFullName.Contact)} WHERE contactid = {IdParameter}");
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return; // no such record: the update reports it
            for (var i = 0; i < ContactFullName.Parts.Count; i++)
                if (!row.Contains(ContactFullName.Parts[i])) parts[ContactFullName.Parts[i]] = rd.IsDBNull(i) ? null : rd.GetString(i);
        }

        int convention;
        await using (var cmd = s.Command(s.Dialect.Limit($"SELECT fullnameconventioncode FROM {s.DataTable("organization")}", "1")))
            convention = await cmd.ExecuteScalarAsync(ct) is int code && code is >= 0 and <= 7 ? code : ContactFullName.DefaultConvention; // a stored value outside 0..7 falls back to the default
        row[ContactFullName.FullName] = ContactFullName.Compose(parts["firstname"], parts["middlename"], parts["lastname"], convention);
    }

    private static Guid UpdateId(Entity entity, EntityMetadata m, string pk) =>
        entity.Id != Guid.Empty ? entity.Id
        : entity.Attributes.TryGetValue(pk, out var value) && value is Guid g ? g
        : throw XrmdFaultException.InvalidArgument($"Update of '{m.LogicalName}' requires the record id.");

    /// <summary>A copy of <paramref name="entity"/> (its attribute collection; the values are shared).</summary>
    private static Entity Copy(Entity entity)
    {
        var copy = new Entity(entity.LogicalName, entity.Id);
        foreach (var (name, value) in entity.Attributes) copy[name] = value;
        return copy;
    }

    /// <summary>
    /// Plan 2.11, after the activity row is written: its party rows (<see cref="ActivityParties.WriteAsync"/>), its pointer row
    /// (<see cref="ActivityPointerSync.WriteAsync"/>), then its Owner and Regarding party rows, read from the pointer row. On
    /// Create the pointer row comes first: its plain INSERT refuses an id another activity holds (412, final review C1) before a
    /// party row is written.
    /// </summary>
    /// <remarks>
    /// Lock order (final review I2), as the Delete of an activity and of a party record: the activity row (locked by the Update,
    /// inserted by the Create) before everything else; then the party records (read with HOLDLOCK by
    /// <see cref="ActivityParties.WriteAsync"/>) before the <c>activityparty</c> rows; the pointer row last on Update.
    /// </remarks>
    private async Task WriteActivityRowsAsync(DataSession s, EntityMetadata m, Guid id, IReadOnlyDictionary<string, EntityCollection> partyLists,
        bool isCreate, CancellationToken ct)
    {
        if (isCreate) await _pointer.WriteAsync(s, m, id, isCreate: true, ct);
        await _parties.WriteAsync(s, m, id, partyLists, isCreate, ct);
        if (!isCreate) await _pointer.WriteAsync(s, m, id, isCreate: false, ct);
        await ActivityParties.SyncSystemPartiesAsync(s, id, ct);
    }

    /// <summary>
    /// Ruling Y2: every party record named in <paramref name="partyLists"/> must be readable by <paramref name="caller"/>, checked
    /// as a Retrieve of it (PrivilegeDenied without Read on its table, AccessDenied when the record is out of the caller's reach,
    /// 404 when it does not exist) before anything is written or its address is read, so a party list discloses nothing the
    /// caller cannot read. AppendTo on the party is not required (A9). From memory: checklist 3d (party without Read / without
    /// AppendTo).
    /// </summary>
    private async Task DemandPartiesReadableAsync(DataSession s, Caller caller, IReadOnlyDictionary<string, EntityCollection> partyLists,
        CancellationToken ct)
    {
        var seen = new HashSet<(string, Guid)>();
        foreach (var party in partyLists.Values.SelectMany(l => l.Entities)
                     .Select(e => e.GetAttributeValue<EntityReference>(ActivitySchema.PartyId)).OfType<EntityReference>())
            if (seen.Add((party.LogicalName, party.Id)))
                await RetrieveAsync(s, caller, party.LogicalName, party.Id, null, ct);
    }

    /// <summary>The closed states of an activity (A11): Completed and Canceled.</summary>
    private static bool IsClosedState(int? state) => state is 1 or 2;

    /// <summary>
    /// Plan 2.11 on the Create of an activity: <c>activitytypecode</c> names the table (A7); without state or status the record
    /// starts in state 0 with that state's default status from the table's metadata (pre-flight C11: a task starts Not Started,
    /// 2), and a status must belong to its state (A11); a record created closed gets <c>actualend</c> now when none is given
    /// (from memory: checklist 3e).
    /// </summary>
    private static void ApplyActivityStateOnCreate(EntityMetadata m, IReadOnlyDictionary<string, AttributeMetadata> attrs, Entity row, Guid id,
        DateTime now)
    {
        SetIfDefined(row, attrs, ActivitySchema.ActivityTypeCode, m.LogicalName);
        if (row.GetAttributeValue<OptionSetValue>("statecode") is null && row.GetAttributeValue<OptionSetValue>("statuscode") is null)
            SetIfDefined(row, attrs, "statecode", new OptionSetValue(0));
        CompleteStateAndStatus(m, attrs, row, id);
        if (IsClosedState(row.GetAttributeValue<OptionSetValue>("statecode")?.Value) && row.GetAttributeValue<DateTime?>(ActualEnd) is null)
            SetIfDefined(row, attrs, ActualEnd, now);
    }

    private const string ActualEnd = "actualend";

    /// <summary>
    /// Plan 2.11 on the Update of an activity (A11), with the stored state read under an update lock: a closed activity (state 1
    /// or 2) is read-only, only its state, status and regarding change (a SetState, a Set Regarding), except by Assign (pre-flight
    /// C12: Dataverse assigns closed activities; checklist 4); moving it to a closed state sets <c>actualend</c> to now when it is null (checklist 3e). A
    /// missing record is left to the Update's 404.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for a change of a closed activity.</exception>
    private static async Task ApplyActivityStateOnUpdateAsync(DataSession s, EntityMetadata m, IReadOnlyDictionary<string, AttributeMetadata> attrs,
        Entity row, IReadOnlyDictionary<string, EntityCollection> partyLists, string? committedFile, Guid id, int auditAction,
        IEnumerable<string>? serverColumns, CancellationToken ct)
    {
        int? state;
        DateTime? actualEnd;
        await using (var cmd = s.Command(
                         $"SELECT statecode, {s.Quote(ActualEnd)} FROM {s.DataTable(m.LogicalName)}{s.Dialect.LockHint(RowLock.UpdateRow)} " +
                         $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}{s.Dialect.LockClause(RowLock.UpdateRow)}"))
        {
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return;
            state = rd.IsDBNull(0) ? null : rd.GetInt32(0);
            actualEnd = rd.IsDBNull(1) ? null : rd.GetDateTime(1);
        }

        if (IsClosedState(state) && auditAction != AuditActions.Assign)
        {
            // A file commit's column was taken out of the entity before (ruling F4): it is a change all the same.
            // The columns a message's server writes with the state (plan 4.1b: the send job's senton on a Pending Send email) are no change.
            var server = serverColumns?.ToHashSet(StringComparer.Ordinal) ?? [];
            // The regarding (and its type name) can be set on a closed activity, as Dataverse's Set Regarding does on a closed email (from memory, #339).
            var changed = row.Attributes.Keys.Where(k => k is not ("statecode" or "statuscode" or "regardingobjectid" or "regardingobjecttypecode") && !server.Contains(k)).Concat(partyLists.Keys)
                .Concat(committedFile is null ? [] : [committedFile]).Distinct().ToList();
            if (changed.Count > 0)
                throw XrmdFaultException.InvalidArgument(
                    $"The {m.LogicalName} record {id} is closed (statecode {state}): only its state, status and regarding can change, so " +
                    $"'{string.Join("', '", changed)}' cannot be written. Reopen it first (statecode 0).");
        }
        if (row.GetAttributeValue<OptionSetValue>("statecode") is { } newState && IsClosedState(newState.Value) && actualEnd is null
            && row.GetAttributeValue<DateTime?>(ActualEnd) is null)
            SetIfDefined(row, attrs, ActualEnd, DateTime.UtcNow);
    }

    /// <summary>
    /// Plan 2.11 A7 on Update: an EntityName companion (<c>regardingobjecttypecode</c>, …) sent without its lookup must name the
    /// stored target of the lookup (read from its type column), or be null when the lookup is empty. With the lookup in the
    /// request, the <c>EntityValidator</c> has already compared them.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for another table.</exception>
    private async Task DemandEntityNamesMatchStoredAsync(DataSession s, EntityMetadata m, IReadOnlyDictionary<string, AttributeMetadata> attrs,
        Entity entity, Guid id, CancellationToken ct)
    {
        foreach (var (name, value) in entity.Attributes)
        {
            if (attrs[name] is not EntityNameAttributeMetadata { AttributeOf: { Length: > 0 } lookup } companion || entity.Contains(lookup)) continue;
            await using var cmd = s.Command(
                $"SELECT {s.Quote(lookup)}, {s.Quote(lookup + TypeMapping.TypeColumnSuffix)} FROM {s.DataTable(m.LogicalName)}{s.Dialect.LockHint(RowLock.UpdateRow)} " +
                $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = {IdParameter}{s.Dialect.LockClause(RowLock.UpdateRow)}");
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return; // the Update answers 404
            var stored = rd.IsDBNull(0) || rd.IsDBNull(1) ? null : store.GetEntityByObjectTypeCode(rd.GetInt32(1))?.LogicalName;
            if (!string.Equals(value as string, stored, StringComparison.Ordinal))
                throw EntityValidator.MismatchedCompanion(m, companion, value as string, stored);
        }
    }

    /// <summary>A copy of <paramref name="entity"/> without <paramref name="attribute"/>.</summary>
    private static Entity Without(Entity entity, string attribute)
    {
        var copy = new Entity(entity.LogicalName, entity.Id);
        foreach (var (name, value) in entity.Attributes)
            if (name != attribute) copy[name] = value;
        return copy;
    }

    private static Dictionary<string, AttributeMetadata> AttributesOf(EntityMetadata m) =>
        (m.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);

    /// <summary>
    /// The caller's attributes that this operation may write (validated beforehand), plus, on Update, the file columns set to
    /// null (not valid for update, but null deletes the file: ruling F4).
    /// </summary>
    /// <summary>The system columns a migrated record keeps (migration spec §7.5), as written by the source.</summary>
    private static readonly string[] MigrationSystemColumns =
        ["createdon", "createdby", "modifiedon", "modifiedby", "owningbusinessunit", SystemAttributes.OverriddenCreatedOnName];

    /// <summary>
    /// The values of <see cref="MigrationSystemColumns"/> that <paramref name="entity"/> carries (not null) and its table has: inside
    /// a migration scope they replace what the server would set.
    /// </summary>
    private static List<KeyValuePair<string, object>> MigratedSystemColumns(Entity entity, IReadOnlyDictionary<string, AttributeMetadata> attrs) =>
        [.. MigrationSystemColumns.Where(attrs.ContainsKey)
            .Where(name => entity.Attributes.TryGetValue(name, out var value) && value is not null)
            .Select(name => new KeyValuePair<string, object>(name, entity[name]))];

    private static Entity WritableValues(Entity entity, IReadOnlyDictionary<string, AttributeMetadata> attrs, bool isCreate)
    {
        var row = new Entity(entity.LogicalName);
        foreach (var (name, value) in entity.Attributes)
            if (EntityValidator.IsWritable(attrs[name], isCreate) || (!isCreate && value is null && attrs[name] is FileAttributeMetadata))
                row[name] = value;
        return row;
    }

    /// <summary>
    /// Keeps <c>statecode</c>/<c>statuscode</c> consistent: a <c>statuscode</c> must belong to the given <c>statecode</c>
    /// (else 400) and sets it when not given; a <c>statecode</c> alone brings its default status.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for a status that belongs to another state.</exception>
    private static void CompleteStateAndStatus(EntityMetadata m, IReadOnlyDictionary<string, AttributeMetadata> attrs, Entity row, Guid id)
    {
        var state = row.GetAttributeValue<OptionSetValue>("statecode");
        var status = row.GetAttributeValue<OptionSetValue>("statuscode");
        if (status is not null)
        {
            var owning = (attrs.GetValueOrDefault("statuscode") as StatusAttributeMetadata)?.OptionSet?.Options
                .OfType<StatusOptionMetadata>().FirstOrDefault(o => o.Value == status.Value)?.State;
            if (owning is not { } owningState) return;
            if (state is null) SetIfDefined(row, attrs, "statecode", new OptionSetValue(owningState));
            else if (state.Value != owningState)
                throw XrmdFaultException.InvalidArgument(
                    $"{status.Value} is not a valid status code for state code {state.Value} on {m.LogicalName} with Id {id}.");
        }
        else if (state is not null)
        {
            var defaultStatus = (attrs.GetValueOrDefault("statecode") as StateAttributeMetadata)?.OptionSet?.Options
                .OfType<StateOptionMetadata>().FirstOrDefault(o => o.Value == state.Value)?.DefaultStatus;
            if (defaultStatus is { } d) SetIfDefined(row, attrs, "statuscode", new OptionSetValue(d));
        }
    }

    /// <summary>
    /// The <c>statuscode</c> a Create writes when none is given (plan 2.15, ruling D3): the <c>DefaultStatus</c> of the state
    /// option 0, else 1. A new duplicate rule is so Unpublished (0); the tables whose state 0 defaults to 1 keep 1.
    /// </summary>
    private static int DefaultStatus(IReadOnlyDictionary<string, AttributeMetadata> attrs) =>
        (attrs.GetValueOrDefault("statecode") as StateAttributeMetadata)?.OptionSet?.Options
        .OfType<StateOptionMetadata>().FirstOrDefault(o => o.Value == 0)?.DefaultStatus ?? 1;

    private static void SetIfDefined(Entity row, IReadOnlyDictionary<string, AttributeMetadata> attrs, string name, object value)
    {
        if (attrs.ContainsKey(name)) row[name] = value;
    }

    /// <summary>
    /// Sets <c>ownerid</c> and the owning columns: a systemuser owner gives owninguser (owningteam cleared) and the user's
    /// business unit, an owner team gives owningteam (owninguser cleared) and the team's business unit.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for an owner of another type or a team that is not an owner
    /// team; ObjectDoesNotExist (404) for a missing owner.</exception>
    private static async Task ApplyOwnerAsync(
        DataSession s, Entity row, IReadOnlyDictionary<string, AttributeMetadata> attrs,
        EntityReference owner, Caller caller, CancellationToken ct)
    {
        if (owner.LogicalName is not (SystemUser or Team))
            throw XrmdFaultException.InvalidArgument(
                $"Owner of type '{owner.LogicalName}' is not supported: '{OwnerId}' must reference a {SystemUser} or a {Team}.");

        Guid? ownerBusinessUnit = caller.BusinessUnitId;
        if (owner.LogicalName == Team)
        {
            await using var cmd = s.Command(
                $"SELECT businessunitid, teamtype, name FROM {s.DataTable(Team)}{s.Dialect.LockHint(RowLock.Shared)} WHERE teamid = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
            cmd.AddParameter(IdParameter, DbType.Guid, owner.Id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(Team, owner.Id);
            if (!rd.IsDBNull(1) && rd.GetInt32(1) != 0)
                throw XrmdFaultException.InvalidArgument(
                    $"Team '{(rd.IsDBNull(2) ? owner.Id.ToString() : rd.GetString(2))}' is not an owner team and cannot own records.");
            ownerBusinessUnit = rd.IsDBNull(0) ? null : rd.GetGuid(0);
        }
        else if (owner.Id != caller.UserId)
        {
            await using var cmd = s.Command(
                $"SELECT businessunitid FROM {s.DataTable(SystemUser)}{s.Dialect.LockHint(RowLock.Shared)} WHERE systemuserid = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
            cmd.AddParameter(IdParameter, DbType.Guid, owner.Id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(SystemUser, owner.Id);
            ownerBusinessUnit = rd.IsDBNull(0) ? null : rd.GetGuid(0);
        }

        var isTeam = owner.LogicalName == Team;
        row[OwnerId] = new EntityReference(owner.LogicalName, owner.Id);
        SetIfDefined(row, attrs, "owninguser", isTeam ? null! : new EntityReference(SystemUser, owner.Id));
        SetIfDefined(row, attrs, "owningteam", isTeam ? new EntityReference(Team, owner.Id) : null!);
        if (ownerBusinessUnit is { } bu) SetIfDefined(row, attrs, "owningbusinessunit", new EntityReference(BusinessUnit, bu));
    }

    /// <summary>
    /// Every caller-supplied lookup must point to an existing record of a known entity; the check runs in the write
    /// transaction (HOLDLOCK) since <c>data.*</c> tables have no foreign keys.
    /// </summary>
    private async Task EnsureReferencesExistAsync(
        DataSession s, Entity row, IReadOnlyDictionary<string, AttributeMetadata> attrs, CancellationToken ct)
    {
        foreach (var (name, value) in row.Attributes)
        {
            if (value is not EntityReference reference || attrs[name] is not LookupAttributeMetadata || name == OwnerId) continue;
            if (IsSystemSet(name)) continue;

            var target = (reference.LogicalName is null ? null : store.GetEntity(reference.LogicalName))
                         ?? throw XrmdFaultException.InvalidArgument(
                             $"Attribute '{name}' references entity '{reference.LogicalName}', which does not exist.");
            await using var cmd = s.Command(
                $"SELECT 1 FROM {s.DataTable(target.LogicalName)}{s.Dialect.LockHint(RowLock.Shared)} " +
                $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(target))} = {IdParameter}{s.Dialect.LockClause(RowLock.Shared)}");
            cmd.AddParameter(IdParameter, DbType.Guid, reference.Id);
            if (await cmd.ExecuteScalarAsync(ct) is null) throw XrmdFaultException.ObjectDoesNotExist(target.LogicalName, reference.Id);
        }
    }

    /// <summary>Lookups the service fills itself from known records (caller, caller's or owner's business unit).</summary>
    private static bool IsSystemSet(string name) =>
        name is "createdby" or "modifiedby" or "owninguser" or "owningteam" or "owningbusinessunit";

    private static async Task<int> ExecuteNonQueryAsync(
        DataSession s, string sql, IEnumerable<DbParameter> parameters, CancellationToken ct)
    {
        await using var cmd = s.Command(sql);
        cmd.AddParameters(parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
