using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Query;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Security;

/// <summary>
/// The privilege and access checks of one message, run by the pipeline before stage 10 at every depth (custom actions
/// excepted). Retrieve and RetrieveMultiple are secured by the read predicate of their query (<see cref="ReadSecurity"/>).
/// </summary>
/// <remarks>
/// A missing privilege is <c>PrivilegeDenied</c>; a privilege whose depth (or a share) does not cover the record is
/// <c>AccessDenied</c> with the right. A missing record is 404. Malformed requests (missing parameters, unknown entities or
/// relationships) pass: the core operation answers them with its own 400. Every check holds for each principal of the
/// call: the caller and, with impersonation, the impersonator (the privileges are the intersection).
/// </remarks>
public sealed class SecurityGuard(SecurityCache cache, IMetadataStore store, FieldSecurity fields)
{
    private const string Target = "Target";
    private const string OwnerId = "ownerid";
    private const string OwningUser = "owninguser";
    private const string OwningTeam = "owningteam";
    private const string OwningBusinessUnit = "owningbusinessunit";
    private const string BusinessUnitId = "businessunitid";
    private const string SystemUser = "systemuser";
    private const string Team = "team";
    private const string Role = "role";
    private const string EntraObjectId = "azureactivedirectoryobjectid";
    private const string IdParameter = "@_id";
    private const string AttachmentColumn = "regardingfieldname"; // the column of the record a fileattachment belongs to

    /// <summary>Messages secured by their query's read predicate, not here.</summary>
    private static readonly HashSet<string> ReadMessages = new(StringComparer.OrdinalIgnoreCase) { "Retrieve", "RetrieveMultiple", "ExecuteCosmosSqlQuery" };

    /// <summary>Lookups the service fills itself (caller, owner, business unit): writing them needs no Append/AppendTo.</summary>
    private static readonly HashSet<string> SystemSetLookups = new(StringComparer.Ordinal)
    {
        "createdby", "modifiedby", OwningUser, OwningTeam, OwningBusinessUnit,
    };

    /// <summary>The rights a share may carry, in the order they are checked.</summary>
    private static readonly AccessRights[] ShareableRightsInOrder =
    [
        AccessRights.ReadAccess, AccessRights.WriteAccess, AccessRights.AppendAccess, AccessRights.AppendToAccess,
        AccessRights.DeleteAccess, AccessRights.ShareAccess, AccessRights.AssignAccess,
    ];

    /// <summary>The rights a share may carry (any other bit makes the mask invalid).</summary>
    private static readonly AccessRights ShareableRights = ShareableRightsInOrder.Aggregate((a, b) => a | b);

    /// <summary>The owner columns of a stored record and the values of the lookups asked for.</summary>
    private sealed record StoredRecord(RecordOwnership Owner, IReadOnlyDictionary<string, Guid?> Lookups);

    /// <summary>
    /// Checks that <paramref name="caller"/> may run <paramref name="request"/> (the parameters as they are before stage 10;
    /// SetState arrives already converted to Update).
    /// </summary>
    /// <remarks>
    /// Create: <c>prvCreate</c> with a depth covering the owner (<c>ownerid</c> or the caller) and its business unit; each
    /// written lookup: <c>prvAppend</c> on the entity and AppendTo on the referenced record; a value for <c>overriddencreatedon</c>:
    /// <c>prvOverrideCreatedOnCreatedBy</c> (#343.5, 403 PrivilegeDenied naming it). Update: Write on the record;
    /// each changed lookup: Append on the record and AppendTo on the new referenced record; a changed <c>ownerid</c>: the
    /// rules of Assign; <c>systemuser.azureactivedirectoryobjectid</c> (the identity a token maps to): only a System
    /// Administrator (403 PrivilegeDenied otherwise; Create may set it). Delete: Delete. Assign: Assign on the record, and
    /// the new owner must hold <c>prvRead</c> on the entity. Associate/Disassociate over a lookup: Append on the referencing records, AppendTo on the referenced one.
    /// Associate/Disassociate over a security relationship: Write on the user or team a role is assigned to (or removed from),
    /// Write on the team for a membership; over <c>systemuserprofiles_association</c> or <c>teamprofiles_association</c>:
    /// <c>prvWriteFieldSecurityProfile</c>; assigning a role, or adding a member to an owner team (for each role of the
    /// team), also needs every privilege of the role at its depth or wider (System Administrator: only a System
    /// Administrator assigns it). AddPrivilegesRole/ReplacePrivilegesRole: Write on the
    /// role and every privilege of <c>Privileges</c> at its depth or wider; RemovePrivilegeRole: Write on the role.
    /// GrantAccess/ModifyAccess: Share on the record, every right of the mask held on the record (AccessDenied on the first
    /// one missing), and the principal must hold <c>prvRead</c> on the entity. RevokeAccess: Share on the record.
    /// AddUserToRecordTeam/RemoveUserFromRecordTeam: the order of plan 2.16 ruling A6 (<see cref="AuthorizeRecordTeamAsync"/>),
    /// whose data checks and added-user check hold for SYSTEM too.
    /// RetrieveEntityChanges: <c>prvRead</c> on <c>EntityName</c> at Global depth (<see cref="DemandGlobalReadAsync"/>, plan 2.13).
    /// PublishDuplicateRule and UnpublishDuplicateRule: Write on the rule <c>DuplicateRuleId</c> (plan 2.15, ruling D17).
    /// RetrieveDuplicates: <c>prvRead</c> on the table of <c>BusinessEntity</c>, and Read on the row when its id names a stored one
    /// (plan 2.15, ruling D14; the matches are then read as the caller, so record and field security apply to them).
    /// BulkDetectDuplicates: <c>prvRead</c> on the table of <c>Query</c> (plan 2.15, ruling D15; the job reads the rows as the caller).
    /// RetrieveExchangeRate: Read on the currency <c>TransactionCurrencyId</c> (plan 2.10).
    /// CalculateRollupField: Write on the row <c>Target</c>, else 403 <c>0x8004E027</c> (plan 2.12b, ruling R10).
    /// SendEmail: Write on the email <c>EmailId</c>, and update on the secured columns it writes (plan 2.11, A17); an email whose
    /// sender (<c>from</c>) is a queue also needs the queue readable (plan 4.1b, <see cref="DemandQueueReadableAsync(Caller, Guid, DataSession, CancellationToken)"/>).
    /// GetTrackingTokenEmail: no rule of its own (plan 4.1b, task 7).
    /// A mailbox's Update that sets <c>testemailconfigurationscheduled</c> true: the owner of the mailbox or a System Administrator only
    /// (plan 4.1b, task 7, <see cref="DemandMailboxTestAllowed"/>).
    /// Queue items (plan 4.1a, ruling R23; a <c>queueitem</c> has no owner): working an item (PickFromQueue, ReleaseToQueue, RouteTo
    /// for the queue it leaves, RemoveFromQueue, the Update and Delete of a <c>queueitem</c>) needs the item privilege (Write, Delete
    /// for RemoveFromQueue and Delete), its current queue readable (<see cref="DemandQueueReadableAsync"/>: the read predicate of
    /// <see cref="ReadSecurity"/>, privacy included) and Read on its record; placing one (AddToQueue, RouteTo for the queue it goes to,
    /// the Create of a <c>queueitem</c>) needs Create Queue Item, the destination readable and Read on the record (AddToQueue: Read on
    /// <c>Target</c>). Moving an item elsewhere is its Update, checked as such. Write on the <c>queue</c> record is never needed to work
    /// its items; AddPrincipalToQueue needs Append To on the queue. RetrieveUserQueues: Global Read Queue for another user's queues,
    /// the read predicate otherwise.
    /// CreateMultiple and UpdateMultiple on a standard table: <c>prvCreate</c> or <c>prvWrite</c> on the table of <c>Targets</c>
    /// at any depth (#76, ruling B13); each target is then checked as its inner Create or Update. UpsertMultiple: no rule of its own.
    /// Create and Update of a secured column: the field rules of <see cref="AuthorizeFieldWritesAsync"/> (ruling F4).
    /// InitializeFileBlocksUpload: Write on <c>Target</c>; DeleteFile: Write on the record that owns the file; on a secured
    /// column both also need update on it (its profiles or a field share on the record; 403 FieldUpdateDenied otherwise, ruling
    /// F4). UploadBlock and CommitFileBlocksUpload: no rule of their own (the token is bound to the caller and its record, and
    /// the commit's Update of the record is checked as an Update, field rules included). InitializeFileBlocksDownload: Read on
    /// <c>Target</c> and, on a secured column, field read (its profiles or a field share on the record; 403 FieldReadDenied
    /// otherwise, ruling F4); DownloadBlock: no rule of its own
    /// (the token is bound to the caller and its record).
    /// No effect for <see cref="Caller.Internal"/>, the SYSTEM user, Retrieve and RetrieveMultiple, except the
    /// <see cref="SystemTableRules"/>, which run first for every caller.
    /// </remarks>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied, AccessDenied, PrincipalPrivilegeDenied,
    /// RoleUpdateValidationFailure or CannotActOnBehalfOfAnotherUser; 403 FieldCreateDenied (0x8004F502) or FieldUpdateDenied
    /// (0x8004F507) for a secured column (ruling F4); 400 from <see cref="SystemTableRules"/>; 404 for a missing record.</exception>
    /// <exception cref="InvalidOperationException">A message without a rule (fails closed).</exception>
    public async Task AuthorizeAsync(OrganizationRequest request, Caller caller, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(session);

        // Before any exemption: these rules hold for SYSTEM and System Administrator too.
        await SystemTableRules.EnforceAsync(request, caller, await cache.SystemUserIdAsync(ct), cache, store, session, ct);
        await SystemTableRules.EnforceFieldSharesAsync(request, caller, fields, store, session,
            (record, c) => DemandAccessAsync(caller, record, AccessRights.ReadAccess, session, c), ct);
        if (AccessTeamRules.IsRecordTeamMessage(request.RequestName))
        {
            // Its own order (ruling A6): the data checks come before the caller's rights, and the added user's read applies to
            // every caller but the internal one.
            await AuthorizeRecordTeamAsync(request, caller, session, ct);
            return;
        }
        if (await IsUnrestrictedAsync(caller, ct) || ReadMessages.Contains(request.RequestName ?? "")) return;
        var principals = await PrincipalsAsync(caller, ct);
        var parameters = request.Parameters;

        switch (request.RequestName?.ToLowerInvariant())
        {
            case "create":
                if (parameters.TryGetValue(Target, out var created) && created is Entity createTarget)
                    await AuthorizeCreateAsync(principals, caller, createTarget, session, ct);
                break;
            case "update":
                if (parameters.TryGetValue(Target, out var updated) && updated is Entity updateTarget)
                    await AuthorizeUpdateAsync(principals, updateTarget, session, ct);
                break;
            case "upsert":
                // Plan 3.4, ruling E6 (elastic tables): the rule of the Update when the row exists, else of the Create.
                // On a standard table there is nothing to check here (#76, ruling B10): no step fires on Upsert there, and its
                // stage 30 runs an inner Create or Update, which this guard checks as that message.
                if (parameters.TryGetValue(Target, out var upserted) && upserted is Entity upsertTarget && IsElasticTarget(upsertTarget))
                {
                    if (await UpsertUpdatesAsync(upsertTarget, request, session, ct)) await AuthorizeUpdateAsync(principals, upsertTarget, session, ct);
                    else await AuthorizeCreateAsync(principals, caller, upsertTarget, session, ct);
                }
                break;
            case "createmultiple" or "updatemultiple":
                // #76, ruling B13: the table's Create or Write privilege (any depth) before stage 10, so a caller without it runs no
                // plug-in of the bulk message; each target is then checked in full by its inner Create or Update.
                if (BulkTable(parameters) is { } bulkTable)
                    DemandPrivilege(principals, request.RequestName!.Equals("createmultiple", StringComparison.OrdinalIgnoreCase)
                        ? AccessRights.CreateAccess : AccessRights.WriteAccess, bulkTable);
                break;
            case "upsertmultiple":
                // #76, ruling B13: nothing here; no step fires before its inner Upserts check each target.
                break;
            case "delete":
                if (parameters.TryGetValue(Target, out var deleted) && deleted is EntityReference deleteTarget
                    && store.GetEntity(deleteTarget.LogicalName ?? "") is { } deletedEntity)
                {
                    // Plan 4.1a (ruling R23): a queue item has no owner; its Delete is working it.
                    if (deletedEntity.LogicalName == QueueSchema.QueueItem)
                        await DemandWorkItemAsync(principals, KeyedId(deletedEntity, deleteTarget), AccessRights.DeleteAccess, session, ct);
                    else if (deletedEntity.LogicalName == MailSchema.Attachment)
                    {
                        // The cascade of an email's Delete (marked by the core): the email's Delete was checked.
                        if (!CascadeHint.IsMarked(request))
                            await DemandAttachmentWriteAsync(principals, deletedEntity, KeyedId(deletedEntity, deleteTarget), null, session, ct);
                    }
                    else
                        await DemandRecordAsync(principals, deletedEntity, KeyedId(deletedEntity, deleteTarget), AccessRights.DeleteAccess, session, ct);
                }
                break;
            case "assign":
                if (parameters.TryGetValue(Target, out var assigned) && assigned is EntityReference assignTarget
                    && parameters.TryGetValue("Assignee", out var a) && a is EntityReference assignee)
                    await AuthorizeAssignAsync(principals, assignTarget, assignee, session, ct);
                break;
            case "associate" or "disassociate":
                if (parameters.TryGetValue(Target, out var t) && t is EntityReference target
                    && parameters.TryGetValue("Relationship", out var r) && r is Relationship relationship
                    && parameters.TryGetValue("RelatedEntities", out var rel) && rel is EntityReferenceCollection related)
                    await AuthorizeAssociateAsync(principals, target, relationship, related,
                        request.RequestName!.Equals("associate", StringComparison.OrdinalIgnoreCase), session, ct);
                break;
            case "grantaccess" or "modifyaccess":
                if (parameters.TryGetValue(Target, out var shared) && shared is EntityReference shareTarget
                    && parameters.TryGetValue("PrincipalAccess", out var pa) && pa is PrincipalAccess { Principal: { } principal } access)
                    await AuthorizeShareAsync(principals, shareTarget, principal, access.AccessMask, session, ct);
                break;
            case "revokeaccess":
                if (parameters.TryGetValue(Target, out var revoked) && revoked is EntityReference revokeTarget
                    && store.GetEntity(revokeTarget.LogicalName ?? "") is { OwnershipType: OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned } revokedEntity)
                    await DemandRecordAsync(principals, revokedEntity, revokeTarget.Id, AccessRights.ShareAccess, session, ct);
                break;
            case "addprivilegesrole" or "replaceprivilegesrole":
                if (parameters.TryGetValue("RoleId", out var role) && role is Guid roleId
                    && parameters.TryGetValue("Privileges", out var p) && p is RolePrivilege[] privileges)
                    await AuthorizeRolePrivilegesAsync(principals, roleId, SecurityAdministration.FromSdk(privileges), session, ct);
                break;
            case "removeprivilegerole":
                if (parameters.TryGetValue("RoleId", out var removedFrom) && removedFrom is Guid removedFromId)
                    await DemandRecordAsync(principals, Definition(Role), removedFromId, AccessRights.WriteAccess, session, ct);
                break;
            case "initializefileblocksupload":
                if (parameters.TryGetValue(Target, out var uploaded) && uploaded is EntityReference uploadTarget
                    && store.GetEntity(uploadTarget.LogicalName ?? "") is { } uploadEntity)
                {
                    await DemandRecordAsync(principals, uploadEntity, uploadTarget.Id, AccessRights.WriteAccess, session, ct);
                    if (parameters.TryGetValue("FileAttributeName", out var uploadColumn) && uploadColumn is string uploadAttribute)
                        await DemandFieldUpdateAsync(caller, uploadEntity, uploadTarget.Id, [uploadAttribute], session, ct);
                }
                break;
            case "initializefileblocksdownload":
                if (parameters.TryGetValue(Target, out var downloaded) && downloaded is EntityReference downloadTarget
                    && store.GetEntity(downloadTarget.LogicalName ?? "") is { } downloadEntity)
                {
                    await DemandRecordAsync(principals, downloadEntity, downloadTarget.Id, AccessRights.ReadAccess, session, ct);
                    if (parameters.TryGetValue("FileAttributeName", out var column) && column is string attribute)
                        await DemandFieldReadAsync(caller, downloadEntity, downloadTarget.Id, attribute, session, ct);
                }
                break;
            case "retrieveentitychanges":
                if (parameters.TryGetValue("EntityName", out var tracked) && tracked is string trackedName
                    && store.GetEntity(trackedName) is { } trackedEntity)
                    DemandGlobalRead(principals, trackedEntity);
                break;
            case "executeworkflow":
                // Plan 2.14, ruling W21: Read on the process (its definition or activation). Read on the record of EntityId is
                // demanded when the process is known (ExecuteWorkflowMessage: its primary entity names the record's table).
                // Learn: starting a workflow also needs prvWorkflowExecution ("Execute Workflow Job").
                if (parameters.TryGetValue("WorkflowId", out var workflow) && workflow is Guid workflowId)
                    await DemandRecordAsync(principals, Definition(WorkflowSchema.Table), workflowId, AccessRights.ReadAccess, session, ct);
                await DemandPrivilegeAsync(caller, PrivilegeNames.WorkflowExecution, WorkflowSchema.Table, ct);
                break;
            case "publishduplicaterule" or "unpublishduplicaterule":
                // Plan 2.15, ruling D17: Write on the rule.
                if (parameters.TryGetValue("DuplicateRuleId", out var duplicateRule) && duplicateRule is Guid duplicateRuleId)
                    await DemandRecordAsync(principals, Definition(DuplicateDetectionSchema.Rule), duplicateRuleId, AccessRights.WriteAccess, session, ct);
                break;
            case "retrieveduplicates":
                // Plan 2.15, ruling D14: Read on the base table.
                if (parameters.TryGetValue("BusinessEntity", out var business) && business is Entity { LogicalName: { } businessName } given
                    && store.GetEntity(businessName) is { } businessEntity)
                {
                    DemandPrivilege(principals, AccessRights.ReadAccess, businessEntity);
                    // Its id names a stored row whose values the lookup reads: Read on that row too (fails closed, so the matches
                    // cannot reveal the values of a row the caller cannot read). An id of no row is an unsaved record.
                    if (StoredRowId(businessEntity, given) is { } stored && await RowExistsAsync(businessEntity, stored, session, ct))
                        await DemandRecordAsync(principals, businessEntity, stored, AccessRights.ReadAccess, session, ct);
                }
                break;
            case "bulkdetectduplicates":
                // Plan 2.15, ruling D15: Read on the query's table (the job then reads its rows as the caller).
                if (parameters.TryGetValue("Query", out var bulkQuery) && QueryEntity(bulkQuery) is { } bulkName
                    && store.GetEntity(bulkName) is { } bulkEntity)
                    DemandPrivilege(principals, AccessRights.ReadAccess, bulkEntity);
                break;
            case "calculaterollupfield":
                // Plan 2.12b, ruling R10: Write on the row (Learn's maker page: Write privilege on the table and Write access on the
                // row), refused with 0x8004E027; a row that does not exist stays the 404. Nothing on the aggregated rows (R12).
                if (parameters.TryGetValue(Target, out var rollupRow) && rollupRow is EntityReference rollupTarget
                    && store.GetEntity(rollupTarget.LogicalName ?? "") is { } rollupEntity)
                {
                    // The row the core calculates: its id, else the primary id among its key attributes (the core fills it the same way).
                    var rollupId = KeyedId(rollupEntity, rollupTarget);
                    try
                    {
                        await DemandRecordAsync(principals, rollupEntity, rollupId, AccessRights.WriteAccess, session, ct);
                    }
                    catch (XrmdFaultException ex) when (ex.HttpStatus == 403)
                    {
                        var column = parameters.TryGetValue("FieldName", out var field) && field is string { Length: > 0 } name
                            ? $"the column '{rollupEntity.LogicalName}.{name}'"
                            : $"a column of '{rollupEntity.LogicalName}'";
                        throw new XrmdFaultException(ErrorCodes.RollupFieldNoWriteAccess,
                            $"Cannot calculate {column} for the row {rollupId}: the caller has no Write access to the row. {ex.Message}", 403);
                    }
                }
                break;
            case "retrieveexchangerate":
                // Plan 2.10, ruling X12: reading a currency's rate is reading the currency (a missing one is the 404 here).
                if (parameters.TryGetValue("TransactionCurrencyId", out var currency) && currency is Guid currencyId
                    && store.GetEntity(CurrencySchema.Table) is { } currencyEntity)
                    await DemandRecordAsync(principals, currencyEntity, currencyId, AccessRights.ReadAccess, session, ct);
                break;
            case "sendemail":
                // Plan 2.11, A17 (pre-flight C15): sending writes the email (state, status, tracking token, direction, sender), so
                // it needs Write on it, and update on the secured columns it writes. A missing email is the 404 here.
                if (parameters.TryGetValue("EmailId", out var email) && email is Guid emailId
                    && store.GetEntity(ActivitySchema.Email) is { } emailEntity)
                {
                    await DemandRecordAsync(principals, emailEntity, emailId, AccessRights.WriteAccess, session, ct);
                    await DemandFieldUpdateAsync(caller, emailEntity, emailId, SendEmailColumns(parameters), session, ct);
                    // Plan 4.1b (spec section 7): sending from a queue needs the queue readable, as a query reads it (its privacy).
                    if (await QueueSenderAsync(emailId, session, ct) is { } queueSender)
                        await DemandQueueReadableAsync(principals, queueSender, session, ct);
                }
                break;
            case "gettrackingtokenemail":
                // Plan 4.1b, task 7: handing out the organization's next tracking token needs no privilege of its own (from memory:
                // checklist 7); any organization member may, as any member may send email with a token.
                break;
            case "addtoqueue":
                // Plan 4.1a (ruling R23): placing. Read on the record, Create Queue Item and the destination queue readable (its
                // privacy). Moving an item that is elsewhere is the item's Update, which this guard checks as such (working it: the
                // queue it leaves readable).
                if (parameters.TryGetValue(Target, out var queued) && queued is EntityReference queuedRecord
                    && store.GetEntity(queuedRecord.LogicalName ?? "") is { } queuedEntity)
                    await DemandRecordAsync(principals, queuedEntity, KeyedId(queuedEntity, queuedRecord), AccessRights.ReadAccess, session, ct);
                if (parameters.TryGetValue("DestinationQueueId", out var destination) && destination is Guid destinationId)
                    await DemandQueuePlacementAsync(principals, destinationId, session, ct);
                break;
            case "removefromqueue":
                // Plan 4.1a (ruling R23): working the item with Delete (its Delete is then checked as a Delete).
                if (parameters.TryGetValue("QueueItemId", out var removed) && removed is Guid removedId)
                    await DemandWorkItemAsync(principals, removedId, AccessRights.DeleteAccess, session, ct);
                break;
            case "routeto":
                // Plan 4.1a (ruling R23): working the item (the queue it leaves), and placing it in the queue it goes to (the target
                // queue, or the default queue of the target user or team).
                if (parameters.TryGetValue("QueueItemId", out var routed) && routed is Guid routedId)
                    await DemandWorkItemAsync(principals, routedId, AccessRights.WriteAccess, session, ct);
                if (parameters.TryGetValue(Target, out var routeTarget) && routeTarget is EntityReference routeTo
                    && await RouteDestinationAsync(routeTo, session, ct) is { } routeQueue)
                    await DemandQueuePlacementAsync(principals, routeQueue, session, ct);
                break;
            case "addprincipaltoqueue":
                // Plan 4.1a (task 5, ruling R1): the rule of an Associate over queuemembership_association, Append To on the queue.
                if (parameters.TryGetValue("QueueId", out var memberQueue) && memberQueue is Guid memberQueueId)
                    await DemandRecordAsync(principals, Definition(QueueSchema.Queue), memberQueueId, AccessRights.AppendToAccess, session, ct);
                break;
            case "pickfromqueue" or "releasetoqueue":
                // Plan 4.1a (ruling R23): working the item with Write. The worker may be any user (decision 4: a routing service
                // picks for agents); the item's Update or Delete is then checked as such.
                if (parameters.TryGetValue("QueueItemId", out var worked) && worked is Guid workedId)
                    await DemandWorkItemAsync(principals, workedId, AccessRights.WriteAccess, session, ct);
                break;
            case "retrieveuserqueues":
                // Plan 4.1a (task 6): another user's queues need Read Queue at Global depth; the query then returns only the queues
                // the caller can read (the queue privacy of the read security, ruling R13).
                if (parameters.TryGetValue("UserId", out var queuesOf) && queuesOf is Guid queuesUser && queuesUser != caller.UserId)
                    DemandGlobalRead(principals, Definition(QueueSchema.Queue));
                break;
            case "downloadblock":
                // No record rule here: the continuation token is bound to the caller and its record, whose Read was demanded by
                // InitializeFileBlocksDownload.
                break;
            case "uploadblock" or "commitfileblocksupload":
                // No record rule here: the continuation token is bound to the caller and its record, whose Write was demanded
                // by InitializeFileBlocksUpload; CommitFileBlocksUpload writes the record with an Update, which this guard checks.
                break;
            case "deletefile":
                if (parameters.TryGetValue("FileId", out var file) && file is Guid fileId
                    && await FileOwnerAsync(fileId, session, ct) is var (ownerEntity, ownerId, fileAttribute))
                {
                    await DemandRecordAsync(principals, ownerEntity, ownerId, AccessRights.WriteAccess, session, ct);
                    // Fails closed (final review M3): a file whose column is not recorded cannot be field-checked, and the core
                    // operation refuses it with the same 404 before any write; never let it through unchecked.
                    if (fileAttribute is null) throw XrmdFaultException.ObjectDoesNotExist(FileColumns.AttachmentEntity, fileId);
                    await DemandFieldUpdateAsync(caller, ownerEntity, ownerId, [fileAttribute], session, ct);
                }
                break;
            default:
                throw new InvalidOperationException($"Message '{request.RequestName}' has no security rule."); // fail closed
        }

        await AuthorizeDuplicateConditionAsync(principals, request, session, ct);
        await AuthorizeFieldWritesAsync(request, caller, session, ct);
    }

    /// <summary>The table of a bulk message's <c>Targets</c> (its <c>EntityName</c>, else the first target's); null when unknown.</summary>
    // Repeats BulkTargets.TableNameOf: Xrmd.Pipeline references Xrmd.Security, so this project cannot reference that class.
    private EntityMetadata? BulkTable(ParameterCollection parameters) =>
        parameters.TryGetValue("Targets", out var value) && value is EntityCollection targets
        && (string.IsNullOrEmpty(targets.EntityName) ? targets.Entities.FirstOrDefault()?.LogicalName : targets.EntityName) is { } name
            ? store.GetEntity(name)
            : null;

    /// <summary>The queue that is the sender (<c>from</c> party) of the email <paramref name="emailId"/>; null for any other sender or none.</summary>
    private async Task<Guid?> QueueSenderAsync(Guid emailId, DataSession session, CancellationToken ct)
    {
        if (store.GetEntity(QueueSchema.Queue)?.ObjectTypeCode is not { } queueType) return null;
        await using var cmd = session.Command(
            $"SELECT partyid FROM {session.DataTable(ActivitySchema.Party)} WHERE activityid = @id AND participationtypemask = 1 AND partyidtype = @type");
        cmd.AddParameter("@id", System.Data.DbType.Guid, emailId);
        cmd.AddParameter("@type", System.Data.DbType.Int32, queueType);
        return await cmd.ExecuteScalarAsync(ct) is Guid queue ? queue : null;
    }

    /// <summary>
    /// Plan 4.1b: the queue <paramref name="queueId"/> readable by <paramref name="caller"/>, as a query reads it (the read predicate
    /// of <see cref="ReadSecurity"/>, privacy included): what sending from a queue needs, checked by SendEmail and again by the send
    /// job. No effect for <see cref="Caller.Internal"/> and the SYSTEM user.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied or AccessDenied; 404 for a missing queue.</exception>
    public async Task DemandQueueReadableAsync(Caller caller, Guid queueId, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(session);
        if (await IsUnrestrictedAsync(caller, ct)) return;
        await DemandQueueReadableAsync(await PrincipalsAsync(caller, ct), queueId, session, ct);
    }

    /// <summary>The columns of <c>email</c> a SendEmail writes besides the server-maintained ones (<c>senton</c>, the sender).</summary>
    private static List<string> SendEmailColumns(ParameterCollection parameters)
    {
        List<string> columns = ["statecode", "statuscode", "directioncode"];
        if (parameters.TryGetValue("TrackingToken", out var token) && token is string { Length: > 0 }) columns.Add("trackingtoken");
        return columns;
    }

    /// <summary>The table a query of <c>BulkDetectDuplicates</c> reads (null for anything else; the core refuses it).</summary>
    /// <exception cref="XrmdValidationException">A FetchXML query that does not parse (400).</exception>
    private static string? QueryEntity(object? query) => query switch
    {
        Microsoft.Xrm.Sdk.Query.QueryExpression expression => expression.EntityName,
        Microsoft.Xrm.Sdk.Query.QueryByAttribute byAttribute => byAttribute.EntityName,
        Microsoft.Xrm.Sdk.Query.FetchExpression fetch => Xrmd.Query.FetchXml.FetchXmlParser.Parse(fetch.Query).Root.EntityName,
        _ => null,
    };

    /// <summary>
    /// Plan 2.15, ruling D6: the Create, Update or Delete of a duplicate rule condition also needs Write on its rule (on Update,
    /// on the rule it names and on the rule it leaves), and so does an Associate or Disassociate over the conditions
    /// relationship (Write on the rule named and on the stored rule of each condition named). Other requests return at once.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied or AccessDenied on the rule.</exception>
    private async Task AuthorizeDuplicateConditionAsync(IReadOnlyList<SecurityProfile> principals, OrganizationRequest request,
        DataSession session, CancellationToken ct)
    {
        var message = request.RequestName?.ToLowerInvariant();
        if (message is not ("create" or "update" or "delete" or "associate" or "disassociate")
            || !request.Parameters.TryGetValue(Target, out var target)) return;
        var rules = new List<Guid>();
        if (message is "associate" or "disassociate")
        {
            // A condition moved or unlinked over the conditions relationship: Write on the rule it goes to and on the rule it
            // leaves (its stored rule), whichever side is the target.
            if (target is not EntityReference end
                || !request.Parameters.TryGetValue("Relationship", out var r) || r is not Relationship relationship
                || !string.Equals(relationship.SchemaName, DuplicateDetectionSchema.ConditionsRelationship, StringComparison.OrdinalIgnoreCase)
                || !request.Parameters.TryGetValue("RelatedEntities", out var rel) || rel is not EntityReferenceCollection related)
                return;
            foreach (var reference in related.Prepend(end))
            {
                if (string.Equals(reference.LogicalName, DuplicateDetectionSchema.Rule, StringComparison.Ordinal))
                    rules.Add(reference.Id);
                else if (string.Equals(reference.LogicalName, DuplicateDetectionSchema.Condition, StringComparison.Ordinal)
                         && await DuplicateRuleRules.StoredConditionRuleAsync(session, reference.Id, ct) is { } stored)
                    rules.Add(stored);
            }
            target = null;
        }
        switch (target)
        {
            case Entity { LogicalName: DuplicateDetectionSchema.Condition } condition:
                if (condition.Attributes.TryGetValue(DuplicateDetectionSchema.RegardingObjectId, out var given)
                    && DuplicateRuleRules.IdOf(given) is { } named)
                    rules.Add(named);
                var id = condition.Id != Guid.Empty ? condition.Id : condition.GetAttributeValue<Guid?>(DuplicateDetectionSchema.ConditionId) ?? Guid.Empty;
                if (message == "update" && id != Guid.Empty && await DuplicateRuleRules.StoredConditionRuleAsync(session, id, ct) is { } left)
                    rules.Add(left);
                break;
            case EntityReference { LogicalName: DuplicateDetectionSchema.Condition } deleted when message == "delete":
                if (await DuplicateRuleRules.StoredConditionRuleAsync(session, deleted.Id, ct) is { } owner) rules.Add(owner);
                break;
        }
        foreach (var rule in rules.Distinct())
            await DemandRecordAsync(principals, Definition(DuplicateDetectionSchema.Rule), rule, AccessRights.WriteAccess, session, ct);
    }

    /// <summary>
    /// Ruling F4, the secured columns a Create or an Update writes: Create needs <c>cancreate</c> on each secured column of
    /// <c>Target</c> with a non-null value (403 FieldCreateDenied); Update needs <c>canupdate</c>, or a field share with update on
    /// the record, on each secured column present in <c>Target</c>, whatever its value (403 FieldUpdateDenied).
    /// <see cref="FieldSecurity"/> decides who bypasses (F1). It runs after the record checks of the message, so a missing record
    /// or privilege is reported first; the callers the early return of <see cref="AuthorizeAsync"/> lets through (internal work
    /// and SYSTEM) are among those F1 bypasses, so that return skips nothing.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 FieldCreateDenied (0x8004F502) or FieldUpdateDenied (0x8004F507).</exception>
    private async Task AuthorizeFieldWritesAsync(OrganizationRequest request, Caller caller, DataSession session, CancellationToken ct)
    {
        var parameters = request.Parameters;
        switch (request.RequestName?.ToLowerInvariant())
        {
            case "create":
                if (parameters.TryGetValue(Target, out var created) && created is Entity createTarget
                    && store.GetEntity(createTarget.LogicalName ?? "") is { } createdEntity)
                    await DemandFieldCreateAsync(caller, createdEntity, createTarget, ct);
                break;
            case "update" or "upsert":
                if (parameters.TryGetValue(Target, out var updated) && updated is Entity updateTarget
                    && store.GetEntity(updateTarget.LogicalName ?? "") is { } updatedEntity)
                {
                    var upsert = request.RequestName!.Equals("upsert", StringComparison.OrdinalIgnoreCase);
                    if (upsert && !ElasticTables.IsElastic(updatedEntity)) break; // standard: checked by its inner Create or Update (#76, B10)
                    if (upsert && !await UpsertUpdatesAsync(updateTarget, request, session, ct))
                    {
                        await DemandFieldCreateAsync(caller, updatedEntity, updateTarget, ct);
                        break;
                    }
                    var id = KeyedId(updatedEntity, updateTarget);
                    // An Upsert that replaces the row also clears the columns it does not carry (ruling E6): they change too.
                    var changed = upsert
                        ? updateTarget.Attributes.Keys.Concat(ElasticTables.UpsertClearedColumns(updatedEntity, updateTarget)).ToList()
                        : updateTarget.Attributes.Keys.ToList();
                    if (id != Guid.Empty) // the core operation rejects it
                        await DemandFieldUpdateAsync(caller, updatedEntity, id, changed, session, ct);
                }
                break;
        }
    }

    /// <summary>Ruling F4: each secured column <paramref name="target"/> sets to a value needs create (403 FieldCreateDenied).</summary>
    private async Task DemandFieldCreateAsync(Caller caller, EntityMetadata entity, Entity target, CancellationToken ct)
    {
        var written = target.Attributes.Where(a => a.Value is not null).Select(a => a.Key).ToList();
        if (written.Count == 0) return;
        var set = await fields.ForAsync(caller, entity.LogicalName, ct);
        if (written.FirstOrDefault(a => !set.CanCreate(a)) is { } denied)
            throw XrmdFaultException.FieldCreateDenied(entity.LogicalName, denied, caller.UserId);
    }

    /// <summary>
    /// Ruling F4: each secured column of <paramref name="attributes"/> needs update through the caller's profiles or a field share
    /// with update on the record <paramref name="recordId"/>. Field shares are read only for the columns the profiles refuse.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 FieldUpdateDenied (0x8004F507).</exception>
    private async Task DemandFieldUpdateAsync(Caller caller, EntityMetadata entity, Guid recordId, IReadOnlyCollection<string> attributes,
        DataSession session, CancellationToken ct)
    {
        if (attributes.Count == 0) return;
        var set = await fields.ForAsync(caller, entity.LogicalName, ct);
        var refused = attributes.Where(a => !set.CanUpdate(a)).ToList();
        if (refused.Count == 0) return;
        var shares = await fields.SharesAsync(caller, entity.LogicalName, [recordId], refused, session, ct);
        var granted = shares.TryGetValue(recordId, out var grant) ? grant.Update : null;
        if (refused.FirstOrDefault(a => granted?.Contains(a) != true) is { } denied)
            throw XrmdFaultException.FieldUpdateDenied(entity.LogicalName, denied, caller.UserId);
    }

    /// <summary>
    /// Ruling F4: reading the content of a secured file or image column needs read on the column, through the caller's profiles
    /// or a field share on the record. Field shares are not consulted when the profiles allow it.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 FieldReadDenied (0x8004F504).</exception>
    private async Task DemandFieldReadAsync(Caller caller, EntityMetadata entity, Guid recordId, string attribute, DataSession session,
        CancellationToken ct)
    {
        var set = await fields.ForAsync(caller, entity.LogicalName, ct);
        if (set.CanRead(attribute)) return;
        var shares = await fields.SharesAsync(caller, entity.LogicalName, [recordId], [attribute], session, ct);
        if (shares.TryGetValue(recordId, out var grant) && grant.Read.Contains(attribute)) return;
        throw XrmdFaultException.FieldReadDenied(entity.LogicalName, attribute, caller.UserId);
    }

    /// <summary>
    /// Demands the right <paramref name="right"/> on the existing record <paramref name="record"/>: the privilege, then a
    /// depth covering its owner or a share with that right (owner and shares read on <paramref name="session"/>).
    /// No effect for <see cref="Caller.Internal"/> and the SYSTEM user.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied or AccessDenied; 404 when the record does not exist; 400 for an
    /// unknown entity.</exception>
    public async Task DemandAccessAsync(Caller caller, EntityReference record, AccessRights right, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(session);
        if (await IsUnrestrictedAsync(caller, ct)) return;
        var entity = store.GetEntity(record.LogicalName ?? "")
                     ?? throw XrmdFaultException.InvalidArgument(
                         $"The entity with a name = '{record.LogicalName}' with namemapping = 'Logical' was not found in the MetadataCache.");
        await DemandRecordAsync(await PrincipalsAsync(caller, ct), entity, record.Id, right, session, ct);
    }

    /// <summary>The rights RetrievePrincipalAccess reports: every access right of a record (the shareable ones and Create).</summary>
    private static readonly AccessRights[] PrincipalAccessRights = [AccessRights.CreateAccess, .. ShareableRightsInOrder];

    /// <summary>
    /// RetrievePrincipalAccess (plan 3.5 UI-1b task 2, ruling B9): the rights <paramref name="principal"/> (a <c>systemuser</c> or a
    /// <c>team</c>) holds on <paramref name="record"/>, by the checks of this guard and no other rule: for each right, the
    /// <see cref="AccessEvaluator"/> on the principal's profile (the privilege at the widest depth of its roles covers the record's
    /// owner, a System Administrator holds every right), else a share with the principal (a user: or one of its teams) carrying the
    /// right, else the manager hierarchy reach. A team is evaluated as a principal of its own: the profile of its roles
    /// (<see cref="SecurityCache.GetTeamProfileAsync"/>) with the team as the only owner it can be; the hierarchy applies to users
    /// only, so a team has none. The SYSTEM user holds every right (as <c>DemandOwnerCanReadAsync</c> treats it).
    /// Reads only; it checks no access of the caller (the caller's own read of the record is demanded by the message).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown entity or a principal that is not a systemuser or a team; 404 when the
    /// record or the principal does not exist.</exception>
    public async Task<AccessRights> RetrievePrincipalAccessAsync(EntityReference principal, EntityReference record, DataSession session,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(session);
        var entity = store.GetEntity(record.LogicalName ?? "")
                     ?? throw XrmdFaultException.InvalidArgument(
                         $"The entity with a name = '{record.LogicalName}' with namemapping = 'Logical' was not found in the MetadataCache.");
        if (principal.LogicalName is not (SystemUser or Team))
            throw XrmdFaultException.InvalidArgument(
                $"Principal of type '{principal.LogicalName}' is not supported: access is retrieved for a {SystemUser} or a {Team}.");
        var stored = await ReadRecordAsync(entity, record.Id, [], session, ct);
        var isTeam = principal.LogicalName == Team;
        if (!isTeam && principal.Id == await cache.SystemUserIdAsync(ct)) return PrincipalAccessRights.Aggregate((a, b) => a | b);
        await BusinessUnitOfAsync(principal, session, ct); // a missing principal is the 404

        var profile = isTeam ? await cache.GetTeamProfileAsync(principal.Id, ct) : await cache.GetProfileAsync(principal.Id, ct);
        var tree = await cache.GetBusinessUnitTreeAsync(ct);
        var rights = AccessRights.None;
        AccessRights? shared = null;
        var reportShares = new StrongBox<AccessRights?>();
        foreach (var right in PrincipalAccessRights)
        {
            var depth = profile.DepthOf(PrivilegeNames.For(right, entity));
            if (AccessEvaluator.HasAccess(profile, tree, entity, depth, stored.Owner, right, 0)) { rights |= right; continue; }
            shared ??= await SharedRightsAsync(entity, record.Id, principal.Id, userAndTeams: !isTeam, session, ct);
            if (AccessEvaluator.HasAccess(profile, tree, entity, depth, stored.Owner, right, shared.Value)
                || await HierarchyGrantsAsync(profile, entity, record.Id, stored.Owner, right, reportShares, session, ct))
                rights |= right;
        }
        return rights;
    }

    /// <summary>
    /// The shares of the record, one row per principal that holds a right through them (direct or inherited mask; empty masks left
    /// out): the principal's id, its <c>principaltypecode</c> and the mask. With <paramref name="principal"/>, only the shares with
    /// that principal (and, with <paramref name="userAndTeams"/>, with the teams it is a member of).
    /// </summary>
    public static async Task<IReadOnlyList<(Guid PrincipalId, int PrincipalType, AccessRights Mask)>> ShareRowsAsync(EntityMetadata entity,
        Guid id, Guid? principal, bool userAndTeams, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(session);
        var filter = principal is null ? "" : userAndTeams
            ? $"""
              AND (poa.principalid = @principal OR poa.principalid IN
                   (SELECT tm.teamid FROM {session.DataTable("teammembership")} AS tm WHERE tm.systemuserid = @principal))
              """
            : "AND poa.principalid = @principal";
        await using var cmd = session.Command($"""
            SELECT poa.principalid, poa.principaltypecode, {session.Dialect.IsNull("poa.accessrightsmask", "0")} | {session.Dialect.IsNull("poa.inheritedaccessrightsmask", "0")}
            FROM {session.DataTable("principalobjectaccess")} AS poa
            WHERE poa.objectid = {IdParameter} AND poa.objecttypecode = @otc {filter}
            ORDER BY poa.changedon, poa.principalid
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        cmd.AddParameter("@otc", DbType.Int32, entity.ObjectTypeCode ?? 0);
        if (principal is { } p) cmd.AddParameter("@principal", DbType.Guid, p);
        var rows = new List<(Guid, int, AccessRights)>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var mask = (AccessRights)rd.GetInt32(2);
            if (mask != AccessRights.None) rows.Add((rd.GetGuid(0), rd.IsDBNull(1) ? 0 : rd.GetInt32(1), mask));
        }
        return rows;
    }

    /// <summary>
    /// A privilege that is not of an entity record (e.g. <c>prvCreateEntity</c>): every principal of the call must hold it
    /// at some depth (a System Administrator holds all). No effect for <see cref="Caller.Internal"/> and the SYSTEM user.
    /// </summary>
    /// <param name="entity">The entity named in the fault's message.</param>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied.</exception>
    public async Task DemandPrivilegeAsync(Caller caller, string privilegeName, string entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrEmpty(privilegeName);
        if (await IsUnrestrictedAsync(caller, ct)) return;
        foreach (var p in await PrincipalsAsync(caller, ct))
            if (p.DepthOf(privilegeName) == DepthMask.None)
                throw XrmdFaultException.PrivilegeDenied(p.UserId, privilegeName, entity, 0);
    }

    /// <summary>
    /// Change tracking (plan 2.13): every principal of the call must hold <c>prvRead</c> on <paramref name="entity"/> at Global
    /// depth (organization-level read; a System Administrator holds it). No effect for <see cref="Caller.Internal"/> and the SYSTEM
    /// user. The RetrieveEntityChanges message gets it from <see cref="AuthorizeAsync"/>; a front end that reads changes without the
    /// message (the Web API delta) calls it itself.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied; 400 for an unknown entity.</exception>
    public async Task DemandGlobalReadAsync(Caller caller, string entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(entity);
        if (await IsUnrestrictedAsync(caller, ct)) return;
        var metadata = store.GetEntity(entity)
                       ?? throw XrmdFaultException.InvalidArgument(
                           $"The entity with a name = '{entity}' with namemapping = 'Logical' was not found in the MetadataCache.");
        DemandGlobalRead(await PrincipalsAsync(caller, ct), metadata);
    }

    /// <summary>Every principal must hold the read privilege of <paramref name="entity"/> at Global depth (403 PrivilegeDenied).</summary>
    private static void DemandGlobalRead(IReadOnlyList<SecurityProfile> principals, EntityMetadata entity)
    {
        var privilege = PrivilegeNames.For(AccessRights.ReadAccess, entity);
        foreach (var p in principals)
            if (p.DepthOf(privilege) != DepthMask.Global)
                throw XrmdFaultException.PrivilegeDenied(p.UserId, privilege, entity.LogicalName, entity.ObjectTypeCode ?? 0);
    }

    /// <summary>
    /// Turning <c>IsSecured</c> off on <paramref name="entity"/>.<paramref name="attribute"/> (final review I1): the caller
    /// must hold read, create and update on the column through its profiles (under impersonation, every principal). Callers
    /// field security does not restrict (ruling F1) always may. No effect on a column that is not secured.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 UnsecureWithoutPermissions (0x8004F508).</exception>
    public async Task DemandUnsecureAsync(Caller caller, string entity, string attribute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        var set = await fields.ForAsync(caller, entity, ct);
        if (!set.CanRead(attribute) || !set.CanCreate(attribute) || !set.CanUpdate(attribute))
            throw XrmdFaultException.UnsecureWithoutPermissions(entity, attribute, caller.UserId);
    }

    /// <summary>
    /// True for internal work, SYSTEM, and a caller whose principals (the user and, when impersonating, the impersonator)
    /// are all System Administrators.
    /// </summary>
    public async Task<bool> IsAdministratorAsync(Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await IsUnrestrictedAsync(caller, ct)) return true;
        return (await PrincipalsAsync(caller, ct)).All(p => p.IsSystemAdministrator);
    }

    private async Task<bool> IsUnrestrictedAsync(Caller caller, CancellationToken ct) =>
        caller == Caller.Internal || cache.IsUnrestricted(caller, await cache.SystemUserIdAsync(ct));

    /// <summary>The principals every check holds for: the caller (impersonation adds the impersonator).</summary>
    private Task<IReadOnlyList<SecurityProfile>> PrincipalsAsync(Caller caller, CancellationToken ct) => cache.PrincipalsAsync(caller, ct);

    // ---- messages ---------------------------------------------------------------------------------------------------

    private async Task AuthorizeCreateAsync(IReadOnlyList<SecurityProfile> principals, Caller caller, Entity target,
        DataSession session, CancellationToken ct)
    {
        if (store.GetEntity(target.LogicalName ?? "") is not { } entity) return;
        // #343.5 (Learn): a record created with its own creation date needs prvOverrideCreatedOnCreatedBy, on every table.
        if (target.Attributes.TryGetValue(SystemAttributes.OverriddenCreatedOnName, out var original) && original is not null)
            foreach (var p in principals)
                if (p.DepthOf(PrivilegeNames.OverrideCreatedOnCreatedBy) == DepthMask.None)
                    throw XrmdFaultException.PrivilegeDenied(p.UserId, PrivilegeNames.OverrideCreatedOnCreatedBy, entity.LogicalName, entity.ObjectTypeCode ?? 0);
        // Plan 4.1b: an email's attachment follows its email (Write on it), whatever the table's own privileges.
        if (entity.LogicalName == MailSchema.Attachment)
        {
            await DemandAttachmentWriteAsync(principals, entity, Guid.Empty, target.GetAttributeValue<EntityReference>(ActivitySchema.ObjectId), session, ct);
            return;
        }
        var privilege = DemandPrivilege(principals, AccessRights.CreateAccess, entity);
        await DemandMailAdministrationAsync(principals, entity, target, Guid.Empty, session, ct);
        var pk = SystemAttributes.PrimaryIdName(entity);
        var id = target.Id != Guid.Empty ? target.Id : target.GetAttributeValue<Guid?>(pk) ?? Guid.Empty;

        // The owner the record will have: ownerid (default: the caller), or the business unit of a business-owned record.
        RecordOwnership owner;
        switch (entity.OwnershipType)
        {
            case OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned:
                var ownerRef = target.GetAttributeValue<EntityReference>(OwnerId) ?? new EntityReference(SystemUser, caller.UserId);
                if (ownerRef.LogicalName is not (SystemUser or Team)) return; // the core operation rejects it
                var isTeam = ownerRef.LogicalName == Team;
                var ownerUnit = await BusinessUnitOfAsync(ownerRef, session, ct);
                owner = new RecordOwnership(isTeam ? null : ownerRef.Id, isTeam ? ownerRef.Id : null, ownerUnit);
                break;
            case OwnershipTypes.BusinessOwned:
                var unit = entity.Attributes?.Any(a => a.LogicalName == BusinessUnitId) == true
                    ? target.GetAttributeValue<EntityReference>(BusinessUnitId)?.Id
                    : null;
                owner = new RecordOwnership(null, null, unit ?? caller.BusinessUnitId);
                break;
            default:
                owner = new RecordOwnership(null, null, null);
                break;
        }

        var tree = await cache.GetBusinessUnitTreeAsync(ct);
        foreach (var p in principals)
            if (!AccessEvaluator.HasAccess(p, tree, entity, p.DepthOf(privilege), owner, AccessRights.CreateAccess, 0))
                throw XrmdFaultException.AccessDenied(p.UserId, AccessRights.CreateAccess, entity.LogicalName, id);

        // Plan 3.5 UI-1g (controller ruling): the post tables have no Append or AppendTo privilege; a post or comment is created on
        // a record, or a post, the caller can read (the read rule of ReadSecurity.PostFilter), instead of Append/AppendTo.
        if (entity.LogicalName is PostSchema.Post or PostSchema.Comment)
        {
            await DemandPostParentReadAsync(principals, entity, target, session, ct);
            return;
        }
        // Plan 4.1a (ruling R23): a queue item is placed in a queue the caller can read, for a record it can read (no Append check).
        if (entity.LogicalName == QueueSchema.QueueItem)
        {
            await DemandQueueItemParentsAsync(principals, target, session, ct);
            return;
        }
        var lookups = WrittenLookups(entity, target, isCreate: true)
            .Where(l => l.Value is not null).ToList();
        if (lookups.Count == 0) return;
        DemandPrivilege(principals, AccessRights.AppendAccess, entity);
        foreach (var (_, reference) in lookups)
            await DemandAppendToAsync(principals, reference!, session, ct);
    }

    /// <summary>
    /// Create, Update and Delete of an <c>activitymimeattachment</c> (plan 4.1b, ruling P4): Write on the email it belongs to (the
    /// privilege, then the record's depth, shares and hierarchy), as the table has no owner and no privilege of its own. A new
    /// attachment names its email in <paramref name="email"/>; a stored one is read by <paramref name="id"/>. An email that is
    /// missing, or no email, is left to the core operation (404, 400).
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied or AccessDenied; 404 for a missing attachment or email.</exception>
    private async Task DemandAttachmentWriteAsync(IReadOnlyList<SecurityProfile> principals, EntityMetadata attachment, Guid id,
        EntityReference? email, DataSession session, CancellationToken ct)
    {
        if (store.GetEntity(ActivitySchema.Email) is not { } emailEntity) return;
        Guid emailId;
        if (id != Guid.Empty)
        {
            var stored = await ReadRecordAsync(attachment, id, [ActivitySchema.ObjectId], session, ct);
            // Fails closed: an attachment without an email (never made by the server) is writable by nobody but unrestricted callers.
            if (stored.Lookups.GetValueOrDefault(ActivitySchema.ObjectId) is not { } owner)
                throw XrmdFaultException.AccessDenied(principals[0].UserId, AccessRights.WriteAccess, attachment.LogicalName, id);
            emailId = owner;
        }
        else
        {
            if (email is null || email.LogicalName != ActivitySchema.Email) return;
            emailId = KeyedId(emailEntity, email);
        }
        await DemandRecordAsync(principals, emailEntity, emailId, AccessRights.WriteAccess, session, ct);
    }

    /// <summary>
    /// A direct Create of a <c>queueitem</c> (plan 4.1a, spec §6, ruling R23): its queue (<c>queueid</c>) readable
    /// (<see cref="DemandQueueReadableAsync"/>; Create Queue Item is demanded by the caller), and Read on the record it points to
    /// (<c>objectid</c>). A missing or unknown reference is left to the core operation (400).
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied or AccessDenied; 404 for a missing queue or record.</exception>
    private async Task DemandQueueItemParentsAsync(IReadOnlyList<SecurityProfile> principals, Entity target, DataSession session,
        CancellationToken ct)
    {
        if (target.GetAttributeValue<EntityReference>(QueueSchema.QueueIdColumn) is { } queue && store.GetEntity(QueueSchema.Queue) is not null)
            await DemandQueueReadableAsync(principals, queue.Id, session, ct);
        if (target.GetAttributeValue<EntityReference>(QueueSchema.ObjectId) is { } record && store.GetEntity(record.LogicalName ?? "") is { } recordEntity)
            await DemandRecordAsync(principals, recordEntity, KeyedId(recordEntity, record), AccessRights.ReadAccess, session, ct);
    }

    private async Task AuthorizeUpdateAsync(IReadOnlyList<SecurityProfile> principals, Entity target, DataSession session,
        CancellationToken ct)
    {
        if (store.GetEntity(target.LogicalName ?? "") is not { } entity) return;
        var id = KeyedId(entity, target);
        if (id == Guid.Empty) return; // the core operation rejects it

        if (entity.LogicalName == QueueSchema.QueueItem)
        {
            await AuthorizeQueueItemUpdateAsync(principals, id, target, session, ct);
            return;
        }

        if (entity.LogicalName == MailSchema.Attachment)
        {
            await DemandAttachmentWriteAsync(principals, entity, id, null, session, ct);
            return;
        }

        var lookups = WrittenLookups(entity, target, isCreate: false).ToList();
        DemandPrivilege(principals, AccessRights.WriteAccess, entity);
        await DemandMailAdministrationAsync(principals, entity, target, id, session, ct);
        if (entity.LogicalName == SystemUser && target.Attributes.ContainsKey(EntraObjectId)
            && principals.FirstOrDefault(p => !p.IsSystemAdministrator) is { } notAdministrator)
            throw new XrmdFaultException(ErrorCodes.PrivilegeDenied,
                $"Principal user (Id={notAdministrator.UserId}, type=8) is not a System Administrator: only a System Administrator can change {EntraObjectId} of {SystemUser}.",
                403);
        var record = await ReadRecordAsync(entity, id, lookups.Select(l => l.Attribute).ToList(), session, ct);
        await DemandRecordAsync(principals, entity, id, AccessRights.WriteAccess, record, session, ct);
        if (entity.LogicalName == MailSchema.Mailbox && target.Attributes.ContainsKey(MailSchema.TestScheduled)
            && await ChangesTestScheduledAsync(entity, id, target.GetAttributeValue<bool?>(MailSchema.TestScheduled) == true, session, ct))
            DemandMailboxTestAllowed(principals, record.Owner);

        var changed = lookups.Where(l => record.Lookups.GetValueOrDefault(l.Attribute) != l.Value?.Id).ToList();
        if (changed.Count > 0)
        {
            await DemandRecordAsync(principals, entity, id, AccessRights.AppendAccess, record, session, ct);
            foreach (var (_, reference) in changed)
                if (reference is not null) await DemandAppendToAsync(principals, reference, session, ct);
        }

        if (entity.OwnershipType is OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned
            && target.Attributes.TryGetValue(OwnerId, out var o) && o is EntityReference newOwner
            && newOwner.Id != (record.Owner.OwningUser ?? record.Owner.OwningTeam))
        {
            await DemandRecordAsync(principals, entity, id, AccessRights.AssignAccess, record, session, ct);
            await DemandOwnerCanReadAsync(entity, newOwner, session, ct);
        }
    }

    /// <summary>
    /// Plan 4.1b (controller ruling P2): the approval of a user's, queue's or mailbox's email address and a mailbox's server profile are
    /// written by a System Administrator only. On an Update a column sent with its stored value is not a change (a client that
    /// retrieves all columns and sends them back passes). <paramref name="id"/> is empty on a Create, where every named column counts.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied.</exception>
    private async Task DemandMailAdministrationAsync(IReadOnlyList<SecurityProfile> principals, EntityMetadata entity, Entity target, Guid id,
        DataSession session, CancellationToken ct)
    {
        var pk = SystemAttributes.PrimaryIdName(entity);
        var sent = (entity.LogicalName == MailSchema.Mailbox
                ? target.Attributes.Keys.Where(c => !MailSchema.MailboxUserColumns.Contains(c) && c != pk
                                                    && entity.Attributes?.Any(a => a.LogicalName == c && a.AttributeOf is null && !MailSchema.IsSecret(a)) == true)
                : MailSchema.AdministratorColumns(entity.LogicalName).Where(target.Attributes.ContainsKey)).ToList();
        if (sent.Count == 0 || principals.FirstOrDefault(p => !p.IsSystemAdministrator) is not { } notAdministrator) return;
        static object? Normal(string column, object? v) => v switch
        {
            OptionSetValue o => o.Value,
            EntityReference r => r.Id,
            string { Length: 0 } => null,
            null when column == MailSchema.ApprovalColumn => 0,
            var x => x,
        };
        if (id != Guid.Empty)
        {
            await using var cmd = session.Command($"SELECT {string.Join(", ", sent.Select(session.Quote))} FROM {session.DataTable(entity.LogicalName)} " +
                                                  $"WHERE {session.Quote(pk)} = @id");
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                var keep = new List<string>();
                for (var i = 0; i < sent.Count; i++)
                {
                    object? stored = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    var value = Normal(sent[i], target[sent[i]]);
                    stored = Normal(sent[i], stored is Guid or string or null or DateTime or bool ? stored : stored is IConvertible ? Convert.ToInt32(stored) : stored);
                    if (!Equals(stored, value)) keep.Add(sent[i]);
                }
                sent = keep;
            }
        }
        if (sent.Count > 0)
            throw new XrmdFaultException(ErrorCodes.PrivilegeDenied,
                $"Principal user (Id={notAdministrator.UserId}, type=8) is not a System Administrator: only a System Administrator can change {string.Join(", ", sent)} of {entity.LogicalName}.",
                403);
    }

    /// <summary>
    /// Whether an Update writes <c>testemailconfigurationscheduled</c>: true schedules a test, false while a test waits withdraws it; the
    /// stored value sent back (false while nothing waits) is not a write.
    /// </summary>
    private static async Task<bool> ChangesTestScheduledAsync(EntityMetadata entity, Guid id, bool value, DataSession session, CancellationToken ct)
    {
        if (value) return true;
        await using var cmd = session.Command($"SELECT {session.Quote(MailSchema.TestScheduled)} FROM {session.DataTable(entity.LogicalName)} " +
                                              $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(entity))} = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) is { } stored and not DBNull && Convert.ToBoolean(stored);
    }

    /// <summary>
    /// Plan 4.1b, task 7 (ruling P2): the outgoing test of a mailbox (<c>testemailconfigurationscheduled</c>, set true or withdrawn) is
    /// asked for or withdrawn by the owner of the mailbox (its owning user, or a member of its owning team) or a System Administrator; Write on the mailbox alone is
    /// not enough. Every principal (the caller and the impersonated user) must qualify.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied.</exception>
    private static void DemandMailboxTestAllowed(IReadOnlyList<SecurityProfile> principals, RecordOwnership owner)
    {
        foreach (var p in principals)
        {
            if (p.IsSystemAdministrator || owner.OwningUser == p.UserId || (owner.OwningTeam is { } team && p.TeamIds.Contains(team))) continue;
            throw new XrmdFaultException(ErrorCodes.PrivilegeDenied,
                $"Principal user (Id={p.UserId}, type=8) can test only its own mailbox: only the owner of a mailbox or a System Administrator " +
                $"can change {MailSchema.TestScheduled}.", 403);
        }
    }

    /// <summary>
    /// The Update of a <c>queueitem</c> (plan 4.1a, ruling R23): working the item (<see cref="DemandWorkItemAsync"/> with Write); a new
    /// <c>queueid</c> must be readable (<see cref="DemandQueueReadableAsync"/>) and a new <c>objectid</c> readable.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied or AccessDenied; 404 for a missing item, queue or record.</exception>
    private async Task AuthorizeQueueItemUpdateAsync(IReadOnlyList<SecurityProfile> principals, Guid id, Entity target,
        DataSession session, CancellationToken ct)
    {
        var stored = await DemandWorkItemAsync(principals, id, AccessRights.WriteAccess, session, ct);
        if (target.GetAttributeValue<EntityReference>(QueueSchema.QueueIdColumn) is { } moved && moved.Id != stored.Queue)
            await DemandQueueReadableAsync(principals, moved.Id, session, ct);
        if (target.GetAttributeValue<EntityReference>(QueueSchema.ObjectId) is { } record && record.Id != stored.Record
            && store.GetEntity(record.LogicalName ?? "") is { } recordEntity)
            await DemandRecordAsync(principals, recordEntity, KeyedId(recordEntity, record), AccessRights.ReadAccess, session, ct);
    }

    /// <summary>
    /// Working the existing item <paramref name="itemId"/> (plan 4.1a, ruling R23): <paramref name="right"/> on <c>queueitem</c> (the
    /// table has no owner, so any depth covers every item), the queue it is in readable (<see cref="DemandQueueReadableAsync"/>), and
    /// Read on the record it points to. Write on the queue record is not needed.
    /// </summary>
    /// <returns>The item's stored queue and record ids.</returns>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied or AccessDenied; 404 for a missing item.</exception>
    private async Task<(Guid? Queue, Guid? Record)> DemandWorkItemAsync(IReadOnlyList<SecurityProfile> principals, Guid itemId, AccessRights right,
        DataSession session, CancellationToken ct)
    {
        var item = Definition(QueueSchema.QueueItem);
        DemandPrivilege(principals, right, item);
        Guid? queue, record;
        int? recordType;
        await using (var cmd = session.Command(
                         $"SELECT {session.Quote(QueueSchema.QueueIdColumn)}, {session.Quote(QueueSchema.ObjectId)}, {session.Quote(QueueSchema.ObjectId + TypeMapping.TypeColumnSuffix)} " +
                         $"FROM {session.DataTable(item.LogicalName)} WHERE {session.Quote(SystemAttributes.PrimaryIdName(item))} = {IdParameter}"))
        {
            cmd.AddParameter(IdParameter, DbType.Guid, itemId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(item.LogicalName, itemId);
            queue = rd.IsDBNull(0) ? null : rd.GetGuid(0);
            record = rd.IsDBNull(1) ? null : rd.GetGuid(1);
            recordType = rd.IsDBNull(2) ? null : Convert.ToInt32(rd.GetValue(2), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (queue is { } q) await DemandQueueReadableAsync(principals, q, session, ct);
        if (record is { } r)
        {
            // Fail closed (final re-review N2): a record whose table does not resolve cannot be shown readable.
            if (recordType is not { } code || store.GetEntityByObjectTypeCode(code) is not { } recordEntity)
                throw XrmdFaultException.InvalidArgument(
                    $"The record of queue item {itemId} has the type code {recordType?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}, " +
                    "which names no table: the item cannot be changed.");
            await DemandRecordAsync(principals, recordEntity, r, AccessRights.ReadAccess, session, ct);
        }
        return (queue, record);
    }

    /// <summary>
    /// The queue RouteTo moves an item to: the target queue, or the default queue (<c>queueid</c>) of the target user or team; null
    /// when the target names none (another table, or a principal without a default queue), which the core operation refuses (400).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 for a missing user or team.</exception>
    private async Task<Guid?> RouteDestinationAsync(EntityReference target, DataSession session, CancellationToken ct)
    {
        if (target.LogicalName == QueueSchema.Queue) return target.Id;
        if (target.LogicalName is not (SystemUser or Team)) return null;
        var principal = await ReadRecordAsync(Definition(target.LogicalName), target.Id, [QueueSchema.QueueIdColumn], session, ct);
        return principal.Lookups.GetValueOrDefault(QueueSchema.QueueIdColumn);
    }

    /// <summary>
    /// Placing an item in a queue by a message (plan 4.1a, ruling R23: AddToQueue, RouteTo): Create Queue Item and the queue readable
    /// (<see cref="DemandQueueReadableAsync"/>). Write on the queue record is not needed.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied or AccessDenied; 404 for a missing queue.</exception>
    private async Task DemandQueuePlacementAsync(IReadOnlyList<SecurityProfile> principals, Guid queueId, DataSession session, CancellationToken ct)
    {
        DemandPrivilege(principals, AccessRights.CreateAccess, Definition(QueueSchema.QueueItem));
        await DemandQueueReadableAsync(principals, queueId, session, ct);
    }

    /// <summary>
    /// The queue <paramref name="queueId"/> readable by every principal, exactly as a query reads it (plan 4.1a, ruling R23): the read
    /// predicate of <see cref="ReadSecurity"/> for <c>queue</c> (Read Queue at its depth, and the privacy: public, owned by the
    /// principal or one of its teams, a member, or shared for Read; a System Administrator reads every queue) is compiled and run on
    /// the row, so the guard and the queries share one definition of the rule.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied without Read Queue, AccessDenied when the queue is not readable; 404 for
    /// a missing queue.</exception>
    private async Task DemandQueueReadableAsync(IReadOnlyList<SecurityProfile> principals, Guid queueId, DataSession session, CancellationToken ct)
    {
        var queue = Definition(QueueSchema.Queue);
        var key = SystemAttributes.PrimaryIdName(queue);
        var security = new ReadSecurity(principals, await cache.GetBusinessUnitTreeAsync(ct), store, queryUserId: Guid.Empty);
        var root = new EntityNode { EntityName = queue.LogicalName, Columns = { new ColumnNode(key) } };
        root.Filter.Conditions.Add(new ConditionNode(null, key, Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, [queueId]));
        var compiled = new SqlQueryCompiler(store, session.Dialect).Compile(new QueryModel { Root = root, Top = 1 },
            new QueryContext(Guid.Empty, Guid.Empty, DateTime.UtcNow) { Security = security, Hierarchy = security });
        await using (var cmd = session.Command(compiled.Sql))
        {
            cmd.AddParameters(compiled.Parameters);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) return;
        }
        await ReadRecordAsync(queue, queueId, [], session, ct); // 404 for a missing queue
        var denied = principals.FirstOrDefault(p => !p.IsSystemAdministrator) ?? principals[0];
        throw XrmdFaultException.AccessDenied(denied.UserId, AccessRights.ReadAccess, queue.LogicalName, queueId);
    }

    private async Task AuthorizeAssignAsync(IReadOnlyList<SecurityProfile> principals, EntityReference target, EntityReference assignee,
        DataSession session, CancellationToken ct)
    {
        // An entity without owner: the core operation rejects the Assign.
        if (store.GetEntity(target.LogicalName ?? "") is not { OwnershipType: OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned } entity)
            return;
        await DemandRecordAsync(principals, entity, target.Id, AccessRights.AssignAccess, session, ct);
        await DemandOwnerCanReadAsync(entity, assignee, session, ct);
    }

    private async Task AuthorizeAssociateAsync(IReadOnlyList<SecurityProfile> principals, EntityReference target, Relationship relationship,
        EntityReferenceCollection related, bool associate, DataSession session, CancellationToken ct)
    {
        if (SecurityAdministration.FindRelationship(relationship.SchemaName) is { } security)
        {
            await AuthorizeSecurityAssociateAsync(principals, security, target, related, associate, session, ct);
            return;
        }
        if (store.GetRelationship(relationship.SchemaName ?? "") is ManyToManyRelationshipMetadata manyToMany)
        {
            await AuthorizeManyToManyAsync(principals, manyToMany, target, related, session, ct);
            return;
        }

        if (store.GetRelationship(relationship.SchemaName ?? "") is not OneToManyRelationshipMetadata r
            || store.GetEntity(r.ReferencingEntity) is not { } referencingEntity
            || store.GetEntity(r.ReferencedEntity) is not { } referencedEntity)
            return; // the core operation rejects it

        List<EntityReference> referencing;
        EntityReference referenced;
        if (target.LogicalName == r.ReferencedEntity)
        {
            referencing = related.Where(e => e.LogicalName == r.ReferencingEntity).ToList();
            referenced = target;
        }
        else if (target.LogicalName == r.ReferencingEntity && related.Count == 1 && related[0].LogicalName == r.ReferencedEntity)
        {
            referencing = [target];
            referenced = related[0];
        }
        else
        {
            return; // the core operation rejects it
        }

        // Plan 4.1a (final re-review N1, ruling R24): pointing a queue item's lookup elsewhere is its Update, so it follows the R23
        // rule of AuthorizeQueueItemUpdateAsync instead of Append/AppendTo (the core sets the lookup with a direct Update).
        if (referencingEntity.LogicalName == QueueSchema.QueueItem)
        {
            await AuthorizeQueueItemRelinkAsync(principals, r, referencing, referenced, associate, session, ct);
            return;
        }

        foreach (var record in referencing)
            await DemandRecordAsync(principals, referencingEntity, record.Id, AccessRights.AppendAccess, session, ct);
        await DemandRecordAsync(principals, referencedEntity, referenced.Id, AccessRights.AppendToAccess, session, ct);
    }

    /// <summary>
    /// A one-to-many Associate or Disassociate whose child is a <c>queueitem</c> (plan 4.1a, ruling R24): each item is worked
    /// (<see cref="DemandWorkItemAsync"/> with Write: its current queue readable, Read on its current record); on Associate the new
    /// parent must be readable as an Update would need it: a queue (<c>queueid</c>) readable, a record (<c>objectid</c>) readable,
    /// and AppendTo on the parent of any other lookup. The single-active-item rule is kept by the core Update.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied or AccessDenied; 404 for a missing item, queue or record.</exception>
    private async Task AuthorizeQueueItemRelinkAsync(IReadOnlyList<SecurityProfile> principals, OneToManyRelationshipMetadata relationship,
        IReadOnlyList<EntityReference> items, EntityReference parent, bool associate, DataSession session, CancellationToken ct)
    {
        foreach (var item in items)
            await DemandWorkItemAsync(principals, item.Id, AccessRights.WriteAccess, session, ct);
        if (!associate) return; // clearing the lookup reaches no new parent
        if (relationship.ReferencingAttribute == QueueSchema.QueueIdColumn)
            await DemandQueueReadableAsync(principals, parent.Id, session, ct);
        else if (relationship.ReferencingAttribute == QueueSchema.ObjectId)
            await DemandRecordAsync(principals, Definition(parent.LogicalName), parent.Id, AccessRights.ReadAccess, session, ct);
        else
            await DemandRecordAsync(principals, Definition(parent.LogicalName), parent.Id, AccessRights.AppendToAccess, session, ct);
    }

    /// <summary>
    /// A many-to-many relationship (Associate and Disassociate alike): Append and AppendTo on the target, AppendTo on each
    /// related record; for <c>queuemembership_association</c>, Append To on the queue(s) only. Records of the wrong entities are left to the core operation's 400.
    /// </summary>
    private async Task AuthorizeManyToManyAsync(IReadOnlyList<SecurityProfile> principals, ManyToManyRelationshipMetadata relationship,
        EntityReference target, EntityReferenceCollection related, DataSession session, CancellationToken ct)
    {
        var other = target.LogicalName == relationship.Entity1LogicalName ? relationship.Entity2LogicalName
            : target.LogicalName == relationship.Entity2LogicalName ? relationship.Entity1LogicalName
            : null;
        if (other is null || related.Any(e => e.LogicalName != other)
            || store.GetEntity(target.LogicalName) is not { } targetEntity || store.GetEntity(other) is not { } otherEntity)
            return; // the core operation rejects it

        // Plan 4.1a (task 4): a queue's membership is the queue's own: Append To on the queue, whichever side names it, and nothing on
        // the users (the same rule the AddPrincipalToQueue message of task 5 uses).
        if (relationship.SchemaName == QueueSchema.MembershipRelationshipName)
        {
            foreach (var queue in target.LogicalName == QueueSchema.Queue ? [target] : related.ToList())
                await DemandRecordAsync(principals, Definition(QueueSchema.Queue), queue.Id, AccessRights.AppendToAccess, session, ct);
            return;
        }

        await DemandRecordAsync(principals, targetEntity, target.Id, AccessRights.AppendAccess, session, ct);
        await DemandRecordAsync(principals, targetEntity, target.Id, AccessRights.AppendToAccess, session, ct);
        foreach (var record in related)
            await DemandRecordAsync(principals, otherEntity, record.Id, AccessRights.AppendToAccess, session, ct);
    }

    /// <summary>
    /// A field security profile relationship (<c>systemuserprofiles_association</c>, <c>teamprofiles_association</c>):
    /// <c>prvWriteFieldSecurityProfile</c>, and nothing on the user or team.
    /// A security relationship: Write on the principal of each role pair (and, to assign, every privilege of the role) or on
    /// the team of each membership pair (and, to add a member to an owner team, every privilege of each role of the team, as
    /// if assigning it). Records of the wrong entities are left to the core operation's 400.
    /// </summary>
    private async Task AuthorizeSecurityAssociateAsync(IReadOnlyList<SecurityProfile> principals, SecurityRelationship relationship,
        EntityReference target, EntityReferenceCollection related, bool associate, DataSession session, CancellationToken ct)
    {
        if (!relationship.TryGetPairs(target, related, out var pairs)) return;
        // F9: giving or taking a field security profile is a write of the profile, whoever the principal is.
        if (SecurityAdministration.IsProfileRelationship(relationship.SchemaName))
        {
            DemandPrivilege(principals, AccessRights.WriteAccess, Definition(FieldSecurityTables.Profile));
            return;
        }
        foreach (var (first, second) in pairs)
        {
            // Entity1 is the principal (systemuser/team) of a role pair and the team of a membership pair.
            await DemandRecordAsync(principals, Definition(first.LogicalName), first.Id, AccessRights.WriteAccess, session, ct);
            if (!associate) continue;
            if (second.LogicalName == Role) await DemandCanGrantRoleAsync(principals, second.Id, session, ct);
            // A new member of an owner team receives the team's roles: the same rule as assigning each of them.
            else if (second.LogicalName == SystemUser)
                foreach (var role in await OwnerTeamRolesAsync(first.Id, session, ct))
                    await DemandCanGrantRoleAsync(principals, role, session, ct);
        }
    }

    /// <summary>The roles of the team when it is an owner team (<c>teamtype</c> 0 or NULL); none for an access team or a missing one.</summary>
    private static async Task<List<Guid>> OwnerTeamRolesAsync(Guid teamId, DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command($"""
            SELECT tr.roleid FROM {session.DataTable("teamroles")} AS tr
            JOIN {session.DataTable("team")} AS t ON t.teamid = tr.teamid AND {session.Dialect.IsNull("t.teamtype", "0")} = 0
            WHERE tr.teamid = {IdParameter}
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, teamId);
        var roles = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) roles.Add(rd.GetGuid(0));
        return roles;
    }

    /// <summary>AddPrivilegesRole/ReplacePrivilegesRole: Write on the role, then every privilege listed at its depth or wider.</summary>
    private async Task AuthorizeRolePrivilegesAsync(IReadOnlyList<SecurityProfile> principals, Guid roleId,
        IReadOnlyList<(Guid PrivilegeId, DepthMask Depth)> privileges, DataSession session, CancellationToken ct)
    {
        await DemandRecordAsync(principals, Definition(Role), roleId, AccessRights.WriteAccess, session, ct);
        var names = await PrivilegeNamesAsync(privileges.Select(p => p.PrivilegeId), session, ct);
        // An unknown privilege is left to the core operation's 0x80090903.
        DemandCanGrant(principals, privileges.Where(p => names.ContainsKey(p.PrivilegeId)).Select(p => (names[p.PrivilegeId], p.Depth)));
    }

    /// <summary>
    /// The escalation rule of a role assignment: each principal that is not a System Administrator must hold every privilege
    /// of the role at its depth or wider; the System Administrator role only a System Administrator assigns. A missing role
    /// is left to the core operation's 404.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 RoleUpdateValidationFailure.</exception>
    private async Task DemandCanGrantRoleAsync(IReadOnlyList<SecurityProfile> principals, Guid roleId, DataSession session, CancellationToken ct)
    {
        if (principals.All(p => p.IsSystemAdministrator)) return;
        await using var cmd = session.Command($"""
            SELECT r.roletemplateid, p.name, rp.privilegedepthmask
            FROM {session.DataTable("role")} AS r
            LEFT JOIN {session.DataTable("roleprivileges")} AS rp ON rp.roleid = r.roleid
            LEFT JOIN {session.DataTable("privilege")} AS p ON p.privilegeid = rp.privilegeid
            WHERE r.roleid = {IdParameter}
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, roleId);
        var administrator = false;
        var privileges = new List<(string Name, DepthMask Depth)>();
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                administrator |= !rd.IsDBNull(0) && rd.GetGuid(0) == StandardRoles.SystemAdministrator;
                if (!rd.IsDBNull(1) && !rd.IsDBNull(2)) privileges.Add((rd.GetString(1), (DepthMask)rd.GetInt32(2)));
            }
        }

        if (administrator)
        {
            // Reported on the first privilege of the catalog the caller lacks at Global (by name).
            await using var all = session.Command($"SELECT name FROM {session.DataTable("privilege")} WHERE name IS NOT NULL ORDER BY name");
            var catalog = new List<string>();
            await using (var rd = await all.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) catalog.Add(rd.GetString(0));
            var caller = principals.First(p => !p.IsSystemAdministrator);
            var missing = catalog.FirstOrDefault(n => caller.DepthOf(n) != DepthMask.Global)
                          ?? catalog.FirstOrDefault() ?? PrivilegeNames.ActOnBehalfOfAnotherUser;
            throw XrmdFaultException.RoleUpdateValidationFailure(caller.UserId, missing, nameof(DepthMask.Global));
        }
        DemandCanGrant(principals, privileges);
    }

    /// <summary>Each principal that is not a System Administrator holds every privilege at its (widest) depth or wider.</summary>
    /// <exception cref="XrmdFaultException">403 RoleUpdateValidationFailure on the first privilege missing.</exception>
    private static void DemandCanGrant(IReadOnlyList<SecurityProfile> principals, IEnumerable<(string Name, DepthMask Depth)> privileges)
    {
        var required = privileges.ToList();
        foreach (var p in principals.Where(p => !p.IsSystemAdministrator))
            foreach (var (name, depth) in required)
            {
                var needed = DepthMasks.Highest(depth);
                if (needed != DepthMask.None && p.DepthOf(name) < needed)
                    throw XrmdFaultException.RoleUpdateValidationFailure(p.UserId, name, needed.ToString());
            }
    }

    /// <summary>
    /// GrantAccess/ModifyAccess: Share on the record, each right of <paramref name="mask"/> held on the record, the principal
    /// holds <c>prvRead</c>. An entity that cannot be shared or an invalid mask is left to the core operation's 400.
    /// </summary>
    private async Task AuthorizeShareAsync(IReadOnlyList<SecurityProfile> principals, EntityReference target, EntityReference principal,
        AccessRights mask, DataSession session, CancellationToken ct)
    {
        if (store.GetEntity(target.LogicalName ?? "") is not { OwnershipType: OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned } entity
            || mask == 0 || (mask & ~ShareableRights) != 0)
            return;
        var record = await ReadRecordAsync(entity, target.Id, [], session, ct);
        await DemandRecordAsync(principals, entity, target.Id, AccessRights.ShareAccess, record, session, ct);
        await DemandRightsAsync(principals, entity, target.Id, mask, record, session, ct);
        await DemandOwnerCanReadAsync(entity, principal, session, ct);
    }

    /// <summary>
    /// AddUserToRecordTeam and RemoveUserFromRecordTeam (plan 2.16, rulings A6 and A7), in the order of A6: steps 1–5, the
    /// template, the record and the user (<see cref="AccessTeamRules.DemandRecordTeamAsync"/>); step 6, the caller holds Share
    /// on the record and every right the call hands out on it, as for GrantAccess (<see cref="AuthorizeShareAsync"/>: roles,
    /// shares and the manager hierarchy count; not for SYSTEM or a System Administrator); step 7, AddUserToRecordTeam only, the
    /// added user holds the privilege of each of those rights (<c>prvRead</c> for Read, <c>prvWrite</c> for Write, and so on) on
    /// the table at some depth (SYSTEM as the added user is exempt). The rights handed out are the template's mask and, on
    /// AddUserToRecordTeam when the record's team for the template already exists, the rights of that team's share of the record
    /// (the member receives the share, which a narrowed template or an earlier write may have left wider than the template).
    /// No effect for <see cref="Caller.Internal"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 or 400 from steps 1–5; 403 PrivilegeDenied or AccessDenied (step 6); 400
    /// InvalidArgument (step 7).</exception>
    private async Task AuthorizeRecordTeamAsync(OrganizationRequest request, Caller caller, DataSession session, CancellationToken ct)
    {
        if (caller == Caller.Internal) return;
        var (record, userId, templateId) = AccessTeamRules.RecordTeamParameters(request);
        // Outside the transaction (stage 10 has not run yet): no locks; the core reads again under its locks and checks that
        // what it writes is what was checked here (AccessTeamRules.DemandSameAsChecked and DemandTeamShareChecked).
        var target = await AccessTeamRules.DemandRecordTeamAsync(session, store, record, userId, templateId, lockRows: false, ct);
        var add = string.Equals(request.RequestName, AccessTeamRules.AddUserToRecordTeam, StringComparison.OrdinalIgnoreCase);
        var mask = target.Mask;
        if (add) mask |= await AccessTeamRules.TeamShareMaskAsync(session, target, lockRows: false, ct) ?? AccessRights.None;

        if (!await IsUnrestrictedAsync(caller, ct))
        {
            var principals = await PrincipalsAsync(caller, ct);
            var stored = await ReadRecordAsync(target.Table, target.RecordId, [], session, ct);
            await DemandRecordAsync(principals, target.Table, target.RecordId, AccessRights.ShareAccess, stored, session, ct);
            await DemandRightsAsync(principals, target.Table, target.RecordId, mask, stored, session, ct);
        }

        if (add && userId != await cache.SystemUserIdAsync(ct))
        {
            var profile = await cache.GetProfileAsync(userId, ct);
            foreach (var right in ShareableRightsInOrder.Where(r => (mask & r) != 0))
            {
                var privilege = PrivilegeNames.For(right, target.Table);
                if (profile.DepthOf(privilege) == DepthMask.None)
                    throw XrmdFaultException.InvalidArgument(
                        $"The user {userId} cannot be added to the access team of this '{target.Table.LogicalName}' record: the team " +
                        $"grants {right}, so the user needs {privilege} at Basic depth or wider.");
            }
        }
        AccessTeamRules.MarkChecked(request, target with { Mask = mask });
    }

    // ---- checks -----------------------------------------------------------------------------------------------------

    /// <summary>Every principal must hold the privilege of <paramref name="right"/> on the entity; returns its name.</summary>
    private static string DemandPrivilege(IReadOnlyList<SecurityProfile> principals, AccessRights right, EntityMetadata entity)
    {
        var privilege = PrivilegeNames.For(right, entity);
        foreach (var p in principals)
            if (p.DepthOf(privilege) == DepthMask.None)
                throw XrmdFaultException.PrivilegeDenied(p.UserId, privilege, entity.LogicalName, entity.ObjectTypeCode ?? 0);
        return privilege;
    }

    private async Task DemandRecordAsync(IReadOnlyList<SecurityProfile> principals, EntityMetadata entity, Guid id, AccessRights right,
        DataSession session, CancellationToken ct)
    {
        DemandPrivilege(principals, right, entity);
        await DemandRecordAsync(principals, entity, id, right, await ReadRecordAsync(entity, id, [], session, ct), session, ct);
        // Plan 3.5 UI-1g: reading or deleting a post-table row also needs read on its regarding record, or post (ReadSecurity.PostFilter).
        if (PostSchema.IsPostTable(entity.LogicalName) && right is AccessRights.ReadAccess or AccessRights.DeleteAccess)
            await DemandPostParentReadAsync(principals, entity, await PostParentAsync(entity, id, session, ct), session, ct);
    }

    /// <summary>
    /// The read rule of the post tables on one row (plan 3.5 UI-1g, spec §3.5): read access to the regarding record of a
    /// <c>post</c> or <c>postregarding</c> (<c>regardingobjectid</c> of <paramref name="row"/>), or to the post of a
    /// <c>postcomment</c> (<c>postid</c>), which is read access to that post's regarding record (and <c>prvReadPost</c>). A row
    /// without the lookup is left to the core operation (400 or 404).
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied or AccessDenied; 404 when the record or post does not exist.</exception>
    private async Task DemandPostParentReadAsync(IReadOnlyList<SecurityProfile> principals, EntityMetadata entity, Entity row,
        DataSession session, CancellationToken ct)
    {
        if (entity.LogicalName == PostSchema.Comment)
        {
            if (row.GetAttributeValue<EntityReference>(PostSchema.PostId) is not { } postRef) return;
            var post = Definition(PostSchema.Post);
            await DemandRecordAsync(principals, post, KeyedId(post, postRef), AccessRights.ReadAccess, session, ct);
            return;
        }
        if (row.GetAttributeValue<EntityReference>(ActivitySchema.RegardingObjectId) is not { } regarding
            || store.GetEntity(regarding.LogicalName ?? "") is not { } regardingEntity) return;
        await DemandRecordAsync(principals, regardingEntity, KeyedId(regardingEntity, regarding), AccessRights.ReadAccess, session, ct);
    }

    /// <summary>The stored lookup of a post-table row its read rule follows: <c>postid</c> of a comment, else <c>regardingobjectid</c>.</summary>
    private async Task<Entity> PostParentAsync(EntityMetadata entity, Guid id, DataSession session, CancellationToken ct)
    {
        var row = new Entity(entity.LogicalName, id);
        var column = entity.LogicalName == PostSchema.Comment ? PostSchema.PostId : ActivitySchema.RegardingObjectId;
        var typed = column == ActivitySchema.RegardingObjectId;
        await using var cmd = session.Command(
            $"SELECT {session.Quote(column)}{(typed ? $", {session.Quote(column + TypeMapping.TypeColumnSuffix)}" : "")} FROM {session.DataTable(entity.LogicalName)} " +
            $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(entity))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct) || rd.IsDBNull(0)) return row;
        if (!typed) row[column] = new EntityReference(PostSchema.Post, rd.GetGuid(0));
        else if (!rd.IsDBNull(1) && store.GetEntityByObjectTypeCode(rd.GetInt32(1)) is { } table)
            row[column] = new EntityReference(table.LogicalName, rd.GetGuid(0));
        return row;
    }

    /// <summary>
    /// The privilege, then a depth covering the record's owner, a share of the right with the principal, or (plan 2.5, ruling
    /// H4) the principal's manager hierarchy reach.
    /// </summary>
    private async Task DemandRecordAsync(IReadOnlyList<SecurityProfile> principals, EntityMetadata entity, Guid id, AccessRights right,
        StoredRecord record, DataSession session, CancellationToken ct)
    {
        var privilege = DemandPrivilege(principals, right, entity);
        var tree = await cache.GetBusinessUnitTreeAsync(ct);
        foreach (var p in principals)
        {
            var depth = p.DepthOf(privilege);
            if (AccessEvaluator.HasAccess(p, tree, entity, depth, record.Owner, right, 0)) continue;
            var shared = await SharedRightsAsync(entity, id, p.UserId, session, ct);
            if (AccessEvaluator.HasAccess(p, tree, entity, depth, record.Owner, right, shared)) continue;
            if (!await HierarchyGrantsAsync(p, entity, id, record.Owner, right, null, session, ct))
                throw XrmdFaultException.AccessDenied(p.UserId, right, entity.LogicalName, id);
        }
    }

    /// <summary>
    /// True when the manager hierarchy (plan 2.5, ruling H4) gives <paramref name="p"/> <paramref name="right"/> on the record:
    /// by its owner first, then by the rights shared with the reach, read with one query only when the owner does not match.
    /// </summary>
    /// <param name="reportShares">The shares of the reach already read for this principal and record, or null; set when read.</param>
    private static async Task<bool> HierarchyGrantsAsync(SecurityProfile p, EntityMetadata entity, Guid id, RecordOwnership owner,
        AccessRights right, StrongBox<AccessRights?>? reportShares, DataSession session, CancellationToken ct)
    {
        if (p.Hierarchy is not { } reach || (right & AccessEvaluator.HierarchyRights) == 0 || !reach.Covers(entity)) return false;
        if (AccessEvaluator.HasHierarchyAccess(p, entity, owner, right, 0)) return true;
        AccessRights shares;
        if (reportShares?.Value is { } known)
        {
            shares = known;
        }
        else
        {
            shares = await ReportSharesAsync(entity, id, reach, session, ct);
            if (reportShares is not null) reportShares.Value = shares;
        }
        return AccessEvaluator.HasHierarchyAccess(p, entity, owner, right, shares);
    }

    /// <summary>
    /// Every principal must hold each right of <paramref name="mask"/> on the record (a depth covering its owner, or a share
    /// with a privilege of any depth, or the manager hierarchy reach), else AccessDenied on the first right missing, in the order
    /// of <see cref="ShareableRightsInOrder"/>.
    /// </summary>
    private async Task DemandRightsAsync(IReadOnlyList<SecurityProfile> principals, EntityMetadata entity, Guid id, AccessRights mask,
        StoredRecord record, DataSession session, CancellationToken ct)
    {
        var tree = await cache.GetBusinessUnitTreeAsync(ct);
        foreach (var p in principals)
        {
            AccessRights? shared = null;
            var reportShares = new StrongBox<AccessRights?>();
            foreach (var right in ShareableRightsInOrder.Where(r => (mask & r) != 0))
            {
                var depth = p.DepthOf(PrivilegeNames.For(right, entity));
                if (AccessEvaluator.HasAccess(p, tree, entity, depth, record.Owner, right, 0)) continue;
                shared ??= await SharedRightsAsync(entity, id, p.UserId, session, ct);
                if (AccessEvaluator.HasAccess(p, tree, entity, depth, record.Owner, right, shared.Value)) continue;
                if (!await HierarchyGrantsAsync(p, entity, id, record.Owner, right, reportShares, session, ct))
                    throw XrmdFaultException.AccessDenied(p.UserId, right, entity.LogicalName, id);
            }
        }
    }

    /// <summary>AppendTo on the record a written lookup points to (an unknown entity is left to the core operation).</summary>
    private async Task DemandAppendToAsync(IReadOnlyList<SecurityProfile> principals, EntityReference reference, DataSession session,
        CancellationToken ct)
    {
        if (store.GetEntity(reference.LogicalName ?? "") is { } referenced)
            await DemandRecordAsync(principals, referenced, KeyedId(referenced, reference), AccessRights.AppendToAccess, session, ct);
    }

    /// <summary>A new owner (user or team) must hold <c>prvRead</c> on the entity; the SYSTEM user always does.</summary>
    /// <exception cref="XrmdFaultException">403 PrincipalPrivilegeDenied; 404 for a missing owner.</exception>
    private async Task DemandOwnerCanReadAsync(EntityMetadata entity, EntityReference owner, DataSession session, CancellationToken ct)
    {
        var privilege = PrivilegeNames.For(AccessRights.ReadAccess, entity);
        bool holds;
        switch (owner.LogicalName)
        {
            case SystemUser:
                holds = owner.Id == await cache.SystemUserIdAsync(ct)
                        || (await cache.GetProfileAsync(owner.Id, ct)).DepthOf(privilege) != DepthMask.None;
                break;
            case Team:
                holds = await TeamHoldsAsync(owner.Id, privilege, session, ct);
                break;
            default:
                return; // the core operation rejects an owner of another type
        }
        if (!holds) throw XrmdFaultException.PrincipalPrivilegeDenied();
    }

    // ---- reads ------------------------------------------------------------------------------------------------------

    private EntityMetadata Definition(string logicalName) =>
        store.GetEntity(logicalName) ?? throw new InvalidOperationException($"Entity '{logicalName}' is not defined.");

    /// <summary>The names of the catalog privileges among <paramref name="ids"/>, by id.</summary>
    private static async Task<Dictionary<Guid, string>> PrivilegeNamesAsync(IEnumerable<Guid> ids, DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command($"""
            SELECT p.privilegeid, p.name FROM {session.DataTable("privilege")} AS p
            WHERE p.name IS NOT NULL AND p.privilegeid IN (SELECT j.id FROM {session.Dialect.JsonRecords("@ids", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$")])} AS j)
            """);
        cmd.AddParameter("@ids", DbType.String, System.Text.Json.JsonSerializer.Serialize(ids.Distinct()), -1);
        var names = new Dictionary<Guid, string>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) names[rd.GetGuid(0)] = rd.GetString(1);
        return names;
    }

    /// <summary>
    /// The lookups of <paramref name="target"/> the operation writes (the owner and the system-set ones excepted), with
    /// their value (null: cleared). A field share's lookups (<c>objectid</c>, <c>principalid</c>) need no Append/AppendTo: who may
    /// share a column on a record is ruling F6's rule 4 (<see cref="SystemTableRules.EnforceFieldSharesAsync"/>), as Basic User
    /// holds only Create, Read, Write and Delete on the table.
    /// </summary>
    private static IEnumerable<(string Attribute, EntityReference? Value)> WrittenLookups(EntityMetadata entity, Entity target, bool isCreate)
    {
        if (entity.LogicalName == FieldSecurityTables.Share) yield break;
        foreach (var (name, value) in target.Attributes)
        {
            if (name == OwnerId || SystemSetLookups.Contains(name)) continue;
            if (entity.Attributes?.FirstOrDefault(a => a.LogicalName == name) is not LookupAttributeMetadata lookup) continue;
            if ((isCreate ? lookup.IsValidForCreate : lookup.IsValidForUpdate) == false) continue; // ignored by the core
            // Plan 2.11 A9: a party list is no column of the record, and its parties need no privilege beyond their existence.
            if (ActivitySchema.IsPartyList(lookup)) continue;
            switch (value)
            {
                case EntityReference reference: yield return (name, reference); break;
                case null: yield return (name, null); break;
            }
        }
    }

    /// <summary>The id of <paramref name="entity"/>: <see cref="Entity.Id"/>, else its primary key attribute; null when neither is set.</summary>
    private static Guid? StoredRowId(EntityMetadata definition, Entity entity) =>
        entity.Id != Guid.Empty ? entity.Id
        : entity.Attributes.TryGetValue(SystemAttributes.PrimaryIdName(definition), out var pk) && pk is Guid id && id != Guid.Empty ? id
        : null;

    /// <summary>
    /// The id of a write's target: <see cref="Entity.Id"/>, else its primary key attribute, else the primary id among its
    /// <c>KeyAttributes</c> (a row of an elastic table named by key, plan 3.4); empty when none is set. On an elastic table the
    /// ids given must agree (400 otherwise, as in the core), so the row checked here is the row written.
    /// </summary>
    private static Guid KeyedId(EntityMetadata definition, Entity target)
    {
        ElasticTables.DemandOneRowId(definition, target);
        return StoredRowId(definition, target) ?? KeyedId(definition, target.KeyAttributes);
    }

    /// <summary>The id of a reference: <see cref="EntityReference.Id"/>, else the primary id among its <c>KeyAttributes</c>.</summary>
    private static Guid KeyedId(EntityMetadata definition, EntityReference reference) =>
        reference.Id != Guid.Empty ? reference.Id : KeyedId(definition, reference.KeyAttributes);

    private static Guid KeyedId(EntityMetadata definition, KeyAttributeCollection? keys) =>
        keys is not null && keys.TryGetValue(SystemAttributes.PrimaryIdName(definition), out var value)
            ? value switch { Guid g => g, string text when Guid.TryParse(text, out var parsed) => parsed, _ => Guid.Empty }
            : Guid.Empty;

    /// <summary>True when <paramref name="target"/> is a record of an elastic table (plan 3.4).</summary>
    private bool IsElasticTarget(Entity target) => store.GetEntity(target.LogicalName ?? "") is { } entity && ElasticTables.IsElastic(entity);

    /// <summary>
    /// True when an Upsert of <paramref name="target"/> updates a stored row (one has its id and, exactly, its partition: the key
    /// attribute <c>partitionid</c>, else the <c>partitionId</c> parameter of <paramref name="request"/> when not null, else the
    /// <c>partitionid</c> value, else none), false when it creates one. The core operation decides the same under its lock; an id
    /// stored in another partition is a Create there, which fails with 412 DuplicateRecord (plan 3.4, ruling E1), so here it gets
    /// the Create rule.
    /// </summary>
    private async Task<bool> UpsertUpdatesAsync(Entity target, OrganizationRequest request, DataSession session, CancellationToken ct)
    {
        if (store.GetEntity(target.LogicalName ?? "") is not { } entity || KeyedId(entity, target) is var id && id == Guid.Empty) return false;
        if (!ElasticTables.IsElastic(entity)) return false;
        var given = target.KeyAttributes is { } keys && keys.TryGetValue(ElasticTables.PartitionId, out var key) ? key
            : PartitionIdParameter(request) is { } parameter ? parameter
            : target.Attributes.TryGetValue(ElasticTables.PartitionId, out var value) ? value : null;
        // As the core: a partition id is a string (400 otherwise).
        var partition = given as string
                        ?? (given is null ? null : throw XrmdFaultException.InvalidArgument("Invalid partitionid value: it must be a string."));
        await using var cmd = session.Command(
            $"SELECT COUNT(*) FROM {session.DataTable(entity.LogicalName)} WHERE {session.Quote(SystemAttributes.PrimaryIdName(entity))} = {IdParameter} AND " +
            (partition is null
                ? $"{session.Quote("partitionid")} IS NULL"
                // Exact, as the core: '=' ignores trailing spaces even in a binary collation, the byte length does not.
                : $"{session.Dialect.BinaryCollate(session.Quote("partitionid"))} = @partition AND " +
                  $"{session.Dialect.DataLength(session.Quote("partitionid"))} = {session.Dialect.DataLength("@partition")}"));
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        if (partition is not null) cmd.AddParameter("@partition", DbType.String, partition, ElasticTables.PartitionIdMaxLength);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    /// <summary>The <c>partitionId</c> parameter of <paramref name="request"/> (any case, as the core reads it); null when absent.</summary>
    private static object? PartitionIdParameter(OrganizationRequest request) =>
        request.Parameters.FirstOrDefault(p => string.Equals(p.Key, "partitionId", StringComparison.OrdinalIgnoreCase)).Value;

    private static async Task<bool> RowExistsAsync(EntityMetadata entity, Guid id, DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command(
            $"SELECT COUNT(*) FROM {session.DataTable(entity.LogicalName)} WHERE {session.Quote(SystemAttributes.PrimaryIdName(entity))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    /// <summary>The owner columns and the <paramref name="lookups"/> of a record.</summary>
    /// <exception cref="XrmdFaultException">404 when the record does not exist.</exception>
    private static async Task<StoredRecord> ReadRecordAsync(EntityMetadata entity, Guid id, IReadOnlyList<string> lookups,
        DataSession session, CancellationToken ct)
    {
        var known = (entity.Attributes ?? []).Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);
        string[] owners = [OwningUser, OwningTeam, OwningBusinessUnit];
        var columns = owners.Concat(lookups).Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        var select = columns.Count == 0 ? "1" : string.Join(", ", columns.Select(c => session.Quote(c)));
        await using var cmd = session.Command(
            $"SELECT {select} FROM {session.DataTable(entity.LogicalName)} " +
            $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(entity))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(entity.LogicalName, id);

        var values = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
            values[columns[i]] = rd.IsDBNull(i) ? null : rd.GetGuid(i);
        return new StoredRecord(
            new RecordOwnership(values.GetValueOrDefault(OwningUser), values.GetValueOrDefault(OwningTeam),
                values.GetValueOrDefault(OwningBusinessUnit)),
            values);
    }

    /// <summary>
    /// The record that owns the file <paramref name="fileId"/> (<c>fileattachment.objectid</c>) and its column
    /// (<c>regardingfieldname</c>, null when not set); null when the file or its entity does not exist (the core operation
    /// answers with its 404).
    /// </summary>
    private async Task<(EntityMetadata Entity, Guid Id, string? Attribute)?> FileOwnerAsync(Guid fileId, DataSession session, CancellationToken ct)
    {
        if (store.GetEntity(FileColumns.AttachmentEntity) is null) return null;
        await using var cmd = session.Command(
            $"SELECT {session.Quote(FileColumns.AttachmentLookup)}, {session.Quote(FileColumns.AttachmentLookup + TypeMapping.TypeColumnSuffix)}, {session.Quote(AttachmentColumn)} " +
            $"FROM {session.DataTable(FileColumns.AttachmentEntity)} WHERE {session.Quote(SystemAttributes.PrimaryIdName(FileColumns.AttachmentEntity))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, fileId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct) || rd.IsDBNull(0) || rd.IsDBNull(1)) return null;
        return store.GetEntityByObjectTypeCode(rd.GetInt32(1)) is { } entity
            ? (entity, rd.GetGuid(0), rd.IsDBNull(2) ? null : rd.GetString(2))
            : null;
    }

    /// <summary>The rights shared on the record with the user and, with <paramref name="userAndTeams"/>, its teams (direct or inherited).</summary>
    private static async Task<AccessRights> SharedRightsAsync(EntityMetadata entity, Guid id, Guid principalId, bool userAndTeams,
        DataSession session, CancellationToken ct) =>
        (await ShareRowsAsync(entity, id, principalId, userAndTeams, session, ct)).Aggregate(AccessRights.None, (all, row) => all | row.Mask);

    private static Task<AccessRights> SharedRightsAsync(EntityMetadata entity, Guid id, Guid userId, DataSession session, CancellationToken ct) =>
        SharedRightsAsync(entity, id, userId, userAndTeams: true, session, ct);

    /// <summary>
    /// The rights shared on the record with the manager hierarchy reach (plan 2.5, ruling H4), direct or inherited, in one query:
    /// Read from a share with any of the <see cref="HierarchyReach.ReadPrincipals"/>, the other rights only from a share with
    /// one of the <see cref="HierarchyReach.WritePrincipals"/> (the reports below the direct ones give Read only).
    /// </summary>
    private static async Task<AccessRights> ReportSharesAsync(EntityMetadata entity, Guid id, HierarchyReach reach, DataSession session,
        CancellationToken ct)
    {
        await using var cmd = session.Command($"""
            SELECT poa.principalid, {session.Dialect.IsNull("poa.accessrightsmask", "0")} | {session.Dialect.IsNull("poa.inheritedaccessrightsmask", "0")}
            FROM {session.DataTable("principalobjectaccess")} AS poa
            WHERE poa.objectid = {IdParameter} AND poa.objecttypecode = @otc
              AND poa.principalid IN (SELECT j.id FROM {session.Dialect.JsonRecords("@principals", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$")])} AS j)
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        cmd.AddParameter("@otc", DbType.Int32, entity.ObjectTypeCode ?? 0);
        cmd.AddParameter("@principals", DbType.String, System.Text.Json.JsonSerializer.Serialize(reach.ReadPrincipals), -1);
        var mask = (AccessRights)0;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var rights = (AccessRights)rd.GetInt32(1);
            mask |= reach.WritePrincipals.Contains(rd.GetGuid(0)) ? rights : rights & AccessRights.ReadAccess;
        }
        return mask;
    }

    /// <summary>The business unit of a <c>systemuser</c> or <c>team</c> owner.</summary>
    /// <exception cref="XrmdFaultException">404 when the owner does not exist.</exception>
    private async Task<Guid?> BusinessUnitOfAsync(EntityReference owner, DataSession session, CancellationToken ct)
    {
        var table = owner.LogicalName == Team ? Team : SystemUser;
        var entity = store.GetEntity(table) ?? throw new InvalidOperationException($"Entity '{table}' is not defined.");
        await using var cmd = session.Command(
            $"SELECT {session.Quote(BusinessUnitId)} FROM {session.DataTable(entity.LogicalName)} " +
            $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(entity))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, owner.Id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(table, owner.Id);
        return rd.IsDBNull(0) ? null : rd.GetGuid(0);
    }

    /// <summary>True when one of the team's roles is System Administrator or grants <paramref name="privilege"/>.</summary>
    /// <exception cref="XrmdFaultException">404 when the team does not exist.</exception>
    private static async Task<bool> TeamHoldsAsync(Guid teamId, string privilege, DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command($"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM {session.DataTable("teamroles")} AS tr
                JOIN {session.DataTable("role")} AS r ON r.roleid = tr.roleid
                WHERE tr.teamid = t.teamid
                  AND (r.roletemplateid = @admin OR EXISTS (
                      SELECT 1 FROM {session.DataTable("roleprivileges")} AS rp
                      WHERE rp.roleid = tr.roleid AND rp.privilegeid = @privilege AND rp.privilegedepthmask <> 0)))
                THEN 1 ELSE 0 END
            FROM {session.DataTable("team")} AS t WHERE t.teamid = {IdParameter}
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, teamId);
        cmd.AddParameter("@admin", DbType.Guid, StandardRoles.SystemAdministrator);
        cmd.AddParameter("@privilege", DbType.Guid, PrivilegeNames.IdOf(privilege));
        return await cmd.ExecuteScalarAsync(ct) switch
        {
            int holds => holds == 1,
            _ => throw XrmdFaultException.ObjectDoesNotExist(Team, teamId),
        };
    }
}
