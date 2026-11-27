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
    /// messages <see cref="ExecuteCoreSdkAsync"/> implements, reached through the bridge (<see cref="CoreSdkMessages"/>, S.2e3c1).</summary>
    internal static readonly HashSet<string> CoreMessages = new(StringComparer.OrdinalIgnoreCase)
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
    internal static readonly string[] CallerOnlyMessages = [.. SolutionJobMessages, "ExportSolution", "StageAndUpgrade", "DeleteAndPromote"];

    /// <summary>The CalculateRollupField message (plan 2.12b, ruling R10).</summary>
    public const string CalculateRollupFieldMessage = "CalculateRollupField";

    /// <summary>The RetrieveAppComponents message (plan 3.5 UI-1b task 1).</summary>
    public const string RetrieveAppComponents = "RetrieveAppComponents";

    /// <summary>The RetrieveUnpublished message (plan 3.5 UI-1b task 1).</summary>
    public const string RetrieveUnpublished = "RetrieveUnpublished";

    /// <summary>The messages answered outside the pipeline: these, the access reads (<see cref="AccessMessages"/>), the metadata
    /// messages (<see cref="MetadataOperations"/>) and the audit messages (<see cref="AuditMessages"/>).</summary>
    internal static readonly HashSet<string> NonPipelineMessages = new(
        ["WhoAmI", "RetrieveUserPrivileges", RetrieveAppComponents, RetrieveUnpublished, InitializeFrom.MessageName, "RetrieveEntity", "RetrieveAttribute", "ImportSolution", "RetrieveFormattedImportJobResults",
            AccessMessages.RetrievePrincipalAccess, AccessMessages.RetrieveSharedPrincipalsAndAccess, .. CallerOnlyMessages,
            .. MetadataOperations.Messages, .. AuditMessages.Messages], StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool IsCoreMessage(string messageName) => CoreMessages.Contains(messageName);

    /// <inheritdoc />
    public bool IsNonPipelineMessage(string messageName) => NonPipelineMessages.Contains(messageName);

    /// <summary>
    /// The SDK handler of <see cref="ExecuteNonPipelineAsync(Xrmd.Model.Messages.Request, Caller, DataSession, CancellationToken)"/>
    /// (S.2e3a; S.2e3c1: the bridge <see cref="CoreSdkMessages.ExecuteNonPipelineAsync"/> calls it).
    /// </summary>
    /// <remarks>
    /// The bridged messages answered outside the pipeline (S.2e3c1 Decision 1): the audit messages, answered by <see cref="AuditMessages"/>
    /// in the plug-in's session (so a delete rolls back with it), and the metadata reads (RetrieveEntity, RetrieveAttribute,
    /// <see cref="MetadataOperations.Retrieve"/>). The others run on the model (<see cref="ExecuteNonPipelineMessageAsync"/>): the access
    /// reads (Task 3), RetrieveUserPrivileges (Task 4), the reads and the refusals of Task 6.
    /// </remarks>
    /// <exception cref="NotSupportedException">Any other message.</exception>
    internal async Task<OrganizationResponse> ExecuteNonPipelineSdkAsync(OrganizationRequest request, Caller caller, DataSession session,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        switch (NonPipelineMessages.TryGetValue(request.RequestName ?? "", out var canonical) ? canonical : request.RequestName)
        {
            case "RetrieveEntity":
                return RetrieveEntity(request);
            case "RetrieveAttribute":
                return RetrieveAttribute(request);
            case var name when AuditMessages.IsAuditMessage(name):
                // In the plug-in's session: the reads see its uncommitted rows, the deletes roll back with it.
                return await auditMessages.ExecuteAsync(request, caller, session, ct);
            case var name when MetadataOperations.IsMetadataMessage(name):
                return MetadataOperations.Retrieve(store, request);
            default:
                throw new NotSupportedException($"Request '{request.RequestName}' is not supported yet.");
        }
    }

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
    /// Stage 30 of the bridged core messages (S.2e3c1: the CRUD, share, record team, role privilege, exchange rate, change tracking and
    /// queue messages run on the model, see <see cref="ExecuteMessageAsync"/>): PublishDuplicateRule and
    /// UnpublishDuplicateRule (<see cref="DuplicateRulePublisher"/>), RetrieveDuplicates (<see cref="Duplicates.DuplicateFinder"/>) and
    /// BulkDetectDuplicates (<see cref="BulkDetectDuplicatesJob.EnqueueAsync"/>: the job is queued),
    /// SendEmail and GetTrackingTokenEmail (<see cref="Core.EmailMessages"/>, plans 2.11 and 4.1b), reading the parameters from
    /// <c>request.Parameters</c> (the plug-ins' <c>InputParameters</c>) as they are after the pre-operation stages.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing parameter, plus the faults of the operation.</exception>
    /// <exception cref="NotSupportedException">Any other message.</exception>
    /// <exception cref="InvalidOperationException">A file message without <paramref name="nested"/> (the pipeline's nested request);
    /// <paramref name="serverSetValues"/> on a message other than Create.</exception>
    /// <remarks>
    /// The SDK handler of <see cref="ExecuteCoreAsync(Xrmd.Model.Messages.Request, CoreCall, CancellationToken)"/> (S.2e3a; S.2e3c1:
    /// the bridge <see cref="CoreSdkMessages.ExecuteAsync"/> calls it). The file messages (<see cref="FileColumnService"/>): InitializeFileBlocksUpload, UploadBlock,
    /// CommitFileBlocksUpload (which writes the record with an Update run through <paramref name="nested"/>, so the Update steps fire
    /// and the guard checks it) and DeleteFile (the same Update, clearing the column).
    /// </remarks>
    /// <param name="serverSetValues">Plan 4.1c: the columns the server writes on a Create (<see cref="CreateAsync"/>); null otherwise.</param>
    internal async Task<OrganizationResponse> ExecuteCoreSdkAsync(OrganizationRequest request, Caller caller, DataSession session,
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
            case CalculateRollupFieldMessage:
                return await CalculateRollupFieldAsync(request, caller, session, ct);
            case EmailMessages.SendEmail:
                return await EmailMessages.ExecuteAsync(request, caller, session, ct);
            case EmailMessages.GetTrackingTokenEmail:
                return await EmailMessages.GetTrackingTokenAsync(request, session, ct);
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
    /// A unique index other than the primary key rejected the row (SQL Server error 2601; ruling P9): an alternate key or a unique
    /// value of the table, not the record's id.
    /// </summary>
    private static bool IsUniqueIndexViolation(DataSession s, DbException ex) =>
        s.Dialect.Classify(ex) == DbErrorKind.UniqueViolation && !s.Dialect.IsPrimaryKeyViolation(ex);

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
