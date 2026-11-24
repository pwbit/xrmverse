using System.Data;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Security;

/// <summary>
/// What no request may do, whoever the caller (SYSTEM and System Administrator included): write the tables the system
/// maintains, modify the SYSTEM user's row, set <c>role.roletemplateid</c>, or register a step impersonating another user
/// without <c>prvActOnBehalfOfAnotherUser</c>. Only requests are concerned: the internal writers (async queue, trace
/// writer, bootstrap) write these tables with their own SQL.
/// </summary>
public static class SystemTableRules
{
    private const string Target = "Target";
    private const string AsyncOperation = "asyncoperation";
    private const string PluginTraceLog = "plugintracelog";
    private const string SystemUser = "systemuser";
    private const string Role = "role";
    private const string Team = "team";
    private const string TeamType = "teamtype";
    private const string OwnerId = "ownerid";
    private const int AccessTeamType = 1;
    private const string RoleTemplateId = "roletemplateid";

    /// <summary>The column of the XML a solution import keeps with a component row (views, forms, site maps, web resources, ...).</summary>
    public const string ComponentXml = "xrmverse_componentxml";

    /// <summary>The column of the XML a solution import keeps with the <c>solution</c> row (unsupported components, names, publisher).</summary>
    public const string UnsupportedXml = "xrmverse_unsupportedxml";

    /// <summary>
    /// The read-only flag of a row component (plan 2.6, ruling M14): true on a row a managed solution delivered. Written only by
    /// the layer operations, as <see cref="Caller.Internal"/>. The <c>solution</c> table's own <c>ismanaged</c> is not concerned.
    /// </summary>
    public const string IsManaged = "ismanaged";

    /// <summary>The column of an app module's solution XML (Dataverse: read-only, IsValidForCreate and IsValidForUpdate false).</summary>
    public const string AppModuleDescriptor = "descriptor";

    /// <summary>
    /// The import-only columns, by table (null: every table): opaque content a solution import keeps and the export writes
    /// again. No request sets them (400 naming the column); only the solution import writes them, with its own SQL.
    /// </summary>
    private static readonly (string? Table, string Column)[] ImportOnly =
    [
        (null, ComponentXml), (Solution, UnsupportedXml), ("appmodule", AppModuleDescriptor),
    ];
    private const string Step = "sdkmessageprocessingstep";
    private const string ImpersonatingUserId = "impersonatinguserid";
    private const string StateCode = "statecode";
    private const string StatusCode = "statuscode";
    private const int Completed = 3;
    private const int Canceled = 32;
    private const string IdParameter = "@_id";

    private const string OnlyCancellation = "Only the cancellation of a system job (statecode 3, statuscode 32) is supported.";
    private const string OnlyCompletedDeleted = "Only completed system jobs can be deleted.";
    private const string StandardRolesNotDeleted = "Standard roles cannot be deleted.";
    private const string SystemAdministratorRoleFixed = "Only the name and description of the System Administrator role can be changed.";
    private const string ReservedRoleId = "The roleid is reserved for a standard role.";
    private const string TeamTypeFixed = "The team type cannot be changed.";
    private const string Organization = "organization";
    private const string IsUserAccessAuditEnabled = "isuseraccessauditenabled";
    private const string AuditRetentionPeriod = "auditretentionperiodv2";
    private const string AuditSettings = "auditsettings";
    private const string StoreLabelNames = "StoreLabelNameforPicklistAudits";
    private const int MaxRetentionDays = 365000;
    private const string ChangeTrackingExpiry = ChangeTrackingSchema.ExpireDaysColumn;
    private static readonly string ChangeTrackingExpiryOutOfRange =
        $"{ChangeTrackingExpiry} must be a whole number of days from 0 to {ChangeTrackingSchema.MaxExpireDays}.";
    private const string HierarchyEnabled = "ishierarchicalsecuritymodelenabled";
    private const string HierarchyDepth = "maxdepthforhierarchicalsecuritymodel";
    private const string PositionHierarchy = "usepositionhierarchy";
    private const int MaxHierarchyDepth = 100;

    /// <summary>The hierarchy security settings of <c>organization</c> (plan 2.5, ruling H2): changed by a System Administrator only.</summary>
    private static readonly string[] HierarchySettings = [HierarchyEnabled, HierarchyDepth, PositionHierarchy];

    private const string HierarchySettingsAdministratorOnly =
        "Only a System Administrator can change the hierarchy security settings (ishierarchicalsecuritymodelenabled, " +
        "maxdepthforhierarchicalsecuritymodel, usepositionhierarchy).";
    private const string HierarchyDepthOutOfRange = "maxdepthforhierarchicalsecuritymodel must be a whole number from 1 to 100.";
    private const string PositionHierarchyUnsupported =
        "usepositionhierarchy cannot be set to true: the position hierarchy is not supported by xrmverse, only the manager hierarchy.";

    private const string UserAccessAuditingUnsupported =
        "isuseraccessauditenabled cannot be set to true: user access auditing is not supported by xrmverse.";
    private const string RetentionOutOfRange =
        "auditretentionperiodv2 must be -1 (keep forever) or a number of days from 1 to 365000.";
    private const string AuditSettingsUnsupported =
        "auditsettings accepts only null or {\"StoreLabelNameforPicklistAudits\":false}: xrmverse stores option values, not their labels.";

    /// <summary>The ids of the standard roles (their template ids), which no other role may take.</summary>
    private static readonly HashSet<Guid> StandardRoleIds = StandardRoles.All.Select(r => r.Id).ToHashSet();

    /// <summary>
    /// Tables no request writes: shares, the privilege catalog, the security intersects, the solution components (written by
    /// the solution messages and component tracking) and the import jobs (written by the solution import).
    /// </summary>
    private static readonly HashSet<string> NoDirectWrites = new(StringComparer.Ordinal)
    {
        "principalobjectaccess", "privilege", "systemuserroles", "teamroles", "teammembership", "roleprivileges", "privilegeobjecttypecodes",
        "solutioncomponent", "importjob", FieldSecurityTables.UserProfiles, FieldSecurityTables.TeamProfiles,
        // #131 (ruling Z3): the time zones are the seed's catalog.
        TimeZoneSchema.Table,
    };

    private const string Solution = "solution";
    private const string UniqueName = "uniquename";

    /// <summary>The suffix of a holding solution's unique name (plan 2.6, ruling M11).</summary>
    private const string HoldingSuffix = "_Upgrade";
    private const string SystemSolutionFixed = "The system solutions Default and Active cannot be deleted and their unique name cannot be changed.";

    /// <summary>The write messages these rules know, by lower-cased name, with their canonical name.</summary>
    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        ["create"] = "Create", ["update"] = "Update", ["delete"] = "Delete", ["assign"] = "Assign",
        ["associate"] = "Associate", ["disassociate"] = "Disassociate",
        ["addmembersteam"] = "AddMembersTeam", ["removemembersteam"] = "RemoveMembersTeam",
        ["grantaccess"] = "GrantAccess", ["modifyaccess"] = "ModifyAccess",
    };

    /// <summary>
    /// The rule of the ruling "asyncoperation / plugintracelog / principalobjectaccess / privilege / intersect / SYSTEM row" and
    /// of the step with <c>impersonatinguserid</c>. Holds for every caller, SYSTEM and System Administrator included.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>principalobjectaccess</c>, <c>privilege</c> and the intersects (the security ones and every entity with
    /// <c>IsIntersect</c>, those of many-to-many relationships): no Create, Update, Delete or Assign (the intersects are written
    /// through Associate and the role privilege messages).</item>
    /// <item><c>plugintracelog</c>: no Create, Update or Assign (Read and Delete only).</item>
    /// <item><c>asyncoperation</c>: no Create or Assign; Update only to cancel (<c>statecode</c> 3, <c>statuscode</c> 32, no other
    /// column but the system ones, which the core ignores) a job in state 0 or 1, read with UPDLOCK; Delete only in state 3.
    /// A missing job is left to the core operation's 404. A rollup calculation job (operationtype 57 or 58, plan 2.12b, ruling R9) is
    /// never cancelled, paused, resumed or deleted: an Update sets <c>postponeuntil</c> or (57) <c>recurrencepattern</c> alone
    /// (<see cref="RollupJobRules"/>).</item>
    /// <item>The SYSTEM user: no Update (SetState included: it arrives as Update), Delete or Assign of its row, and no
    /// security Associate/Disassociate naming it.</item>
    /// <item><c>role.roletemplateid</c>: set by no request (only the bootstrap's SQL).</item>
    /// <item><c>ismanaged</c> of a row component (any table but <c>solution</c>; plan 2.6, ruling M14): set by no request but
    /// those of <see cref="Caller.Internal"/>, the layer operations (400 naming the column).</item>
    /// <item>The import-only columns (<c>xrmverse_componentxml</c> of any table, <c>solution.xrmverse_unsupportedxml</c>,
    /// <c>appmodule.descriptor</c>): set by no request (only the solution import's SQL; <see cref="IsImportOnly"/>).</item>
    /// <item><c>team.teamtype</c>: fixed at Create; an Update setting another value than the stored one (NULL counts as 0,
    /// Owner) is refused with 400, so the escalation check of an owner team's members cannot be skipped by a type flip.</item>
    /// <item><c>solutioncomponent</c> and <c>importjob</c>: no Create, Update, Delete or Assign (written by the solution
/// messages, the component tracking and the import). The system solutions <c>Default</c> and <c>Active</c>: no Delete and no
/// Update of <c>uniquename</c> (403 PrivilegeDenied).</item>
/// <item>Standard roles (<c>roleid</c> a template id, or <c>roletemplateid</c> set): no Delete (403 PrivilegeDenied);
    /// the System Administrator role: Update of <c>name</c> and <c>description</c> only (403 PrivilegeDenied); Create of a
    /// role whose <c>roleid</c> is a template id: 400.</item>
    /// <item>Field security: the System Administrator profile is never updated or deleted, and no <c>fieldpermission</c> row is
    /// created, updated, deleted or moved onto it (403 <c>0x8004F50C</c>). On <c>fieldpermission</c>, <c>cancreate</c>,
    /// <c>canread</c> and <c>canupdate</c> are 0 or 4 and <c>canreadunmasked</c> is 0, as masking is not supported (400); a
    /// Create, or an Update changing <c>entityname</c> or <c>attributelogicalname</c>, names an existing secured column (404
    /// when it does not exist, 400 <c>0x8004F50E</c> when it is not secured).
    /// <c>systemuserprofiles</c> and <c>teamprofiles</c> are written only through their relationships.</item>
    /// <item><c>sdkmessageprocessingstep</c> Create, or Update, whose resulting <c>impersonatinguserid</c> (the value of the
    /// target, else on Update the stored one) is another user than the caller (or than its impersonator): the caller (and
    /// the impersonator) must hold <c>prvActOnBehalfOfAnotherUser</c> from a role assigned directly (<see cref="SecurityProfile.DirectPrivileges"/>);
    /// SYSTEM and <see cref="Caller.Internal"/> are exempt from this check alone.</item>
    /// <item><c>organization</c>: the hierarchy security settings (<c>ishierarchicalsecuritymodelenabled</c>,
    /// <c>maxdepthforhierarchicalsecuritymodel</c>, <c>usepositionhierarchy</c>) are written by a System Administrator only (403
    /// PrivilegeDenied; SYSTEM and <see cref="Caller.Internal"/> pass); the depth is 1 to 100 and the position hierarchy is not
    /// supported (400).</item>
    /// <item>Custom APIs (plan 15, C10, narrowed by plan 3.1 R1): no Create, Update, Delete or Assign of an API's own
    /// <c>sdkmessage</c> row (one an API points to, or whose <c>categoryname</c> is <c>CustomAPI</c>, or a new one named like an
    /// API) and its <c>sdkmessagefilter</c> rows (400): only the server writes them (import, authoring, solution delete), as
    /// <see cref="Caller.Internal"/>, which alone is exempt. The custom API tables are authored with the usual privileges;
    /// custom action messages and their filters stay writable.</item>
    /// <item>Access team templates (plan 2.16, ruling A2; <see cref="AccessTeamRules.DemandTemplateAsync"/>): <c>issystem</c> is
    /// set by no request (400, before the core operation would ignore it as read-only); <c>defaultaccessrightsmask</c> is a
    /// non-empty subset of Read, Write, Append, AppendTo, Delete, Share and Assign (400); on Create, <c>objecttypecode</c> names a
    /// table with <c>AutoCreateAccessTeams</c> that has fewer than <see cref="AccessTeamRules.MaxTemplatesPerTable"/> templates
    /// (400; the core operation checks it again under the lock that serializes it with disabling the table); on Update,
    /// <c>objecttypecode</c> does not change (400). Holds for every caller.</item>
    /// <item>System-managed access teams (ruling A4; <see cref="Caller.Internal"/> is exempt; the record team messages write these
    /// teams in the core, without these messages): a <c>team</c> Create naming <c>teamtemplateid</c>, <c>regardingobjectid</c> or <c>systemmanaged</c> (any value),
    /// and an Update (or the lookup Associate/Disassociate that is one) changing one of them from its stored value, are refused
    /// (400); Associate/Disassociate of <c>teammembership_association</c> naming a team with <c>systemmanaged</c> = 1, and
    /// AddMembersTeam/RemoveMembersTeam of such a team (<c>TeamId</c>), are refused (400 naming
    /// AddUserToRecordTeam/RemoveUserFromRecordTeam). GrantAccess and ModifyAccess whose <c>PrincipalAccess</c> names such a team
    /// are refused for any record, its own included (400: the team gets its one share from the record team messages, which the
    /// core writes directly, so a widened share cannot hand members rights the adder was never checked for). Deleting such a team
    /// follows the normal rules.</item>
    /// <item>Access teams own no record (final re-review of plan 2.16, notes 1 and 2; <see cref="Caller.Internal"/> is exempt): a
    /// Create or Update (or the lookup Associate that is one) whose <c>ownerid</c> names a team with <c>teamtype</c> 1, and an
    /// Assign whose <c>Assignee</c> is one, are refused (400), whether the team is system-managed or not, before the core
    /// operation's own owner-team check. A Create of <c>principalobjectattributeaccess</c> whose <c>principalid</c> is a team with
    /// <c>systemmanaged</c> = 1, and an Update of a share whose principal (given, else stored) is one, are refused (400): a column
    /// share would reach each member AddUserToRecordTeam adds without the adder being checked for it.</item>
    /// <item>Duplicate detection (plan 2.15, rulings D3 to D6; <see cref="Caller.Internal"/> is exempt, as the publishing messages
    /// write a rule's state): <see cref="DuplicateRuleRules.DemandRuleWriteAsync"/> on a rule's Create and Update,
    /// <see cref="DuplicateRuleRules.DemandConditionWriteAsync"/> on a condition's Create and Update (the Delete of a condition is
    /// checked by the core, which knows whether its rule is being deleted with it); no Create, Update, Delete or Assign of
    /// <c>duplicaterecord</c>, whose rows only the bulk detection job writes (D15, 400).</item>
    /// <item>Webhooks (plan 3.2): a Create of <c>serviceendpoint</c>, of a step on one or of an image of such a step, with
    /// <c>SolutionUniqueName</c>, and an Update pointing a step that is a component of a solution (other than Default and Active)
    /// at an endpoint, are refused (400 <see cref="WebhookNotInSolution"/>; AddSolutionComponent:
    /// <see cref="DemandNotWebhookComponentAsync"/>).</item>
    /// <item>Custom APIs (plan 15, C5): no Delete of a <c>plugintype</c> an API runs as its main operation, nor of the
    /// <c>pluginassembly</c> that holds it (400 naming the API), for every caller.</item>
    /// <item>Activities (plan 2.11): no Create, Update (SetState included), Delete, Assign, GrantAccess or ModifyAccess of
    /// <c>activitypointer</c> or <c>activityparty</c>, nor the lookup Associate/Disassociate that updates one of their rows (400):
    /// the Core keeps them with each activity.</item>
    /// <item>Currencies (plan 2.10): the rules of <see cref="CurrencyRules"/> on <c>transactioncurrency</c>, <c>organization</c>
    /// and <c>usersettings</c> (base currency fixed, unique and valid codes, rate and precision ranges, active user currency).</item>
    /// <item>Processes (plan 2.14, ruling W4, and plan 2.21, ruling A16, which lifts 2.14's W3; <see cref="Caller.Internal"/> is
    /// exempt): a Create or Update of <c>workflow</c> goes through <see cref="DemandValidProcessWrite"/> (no server-written column,
    /// type 1, category 0, 2 or 3, created Draft; the XAML and the Draft-only rules are the pipeline's, at stage 20); no Create,
    /// Update or Delete of <c>processtrigger</c> (400); no Delete of a <c>workflow</c> row whose definition is Activated (400).</item>
    /// <item>Environment variables (#167, ruling E2): a Create or Update of <c>environmentvariablevalue</c> names an existing
    /// definition (400); the other rules of E2 run on the final row in the core (<see cref="EnvironmentVariableRules.ApplyRowAsync"/>).</item>
    /// <item>Associate/Disassociate over a one-to-many (lookup) relationship is, for each referencing record, the Update of
    /// its lookup (to the referenced record, or null): the Update rules above hold for it, with the message's name. A lookup
    /// that points to SYSTEM does not modify the SYSTEM row.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied for a system-maintained table, the SYSTEM user or a standard
    /// role; 400 InvalidArgument for a refused <c>asyncoperation</c> transition, a write of <c>roletemplateid</c> or a reserved
    /// <c>roleid</c>; 403
    /// CannotActOnBehalfOfAnotherUser.</exception>
    public static async Task EnforceAsync(OrganizationRequest request, Caller caller, Guid systemUserId, SecurityCache cache,
        IMetadataStore store, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(session);
        if (!Messages.TryGetValue(request.RequestName?.ToLowerInvariant() ?? "", out var message)) return;
        var parameters = request.Parameters;
        parameters.TryGetValue(Target, out var target);
        var rules = new Rules(caller, systemUserId, cache, store, session);
        // Plan 2.10: the currency rules (transactioncurrency, organization, usersettings), for every caller.
        await CurrencyRules.EnforceAsync(request, caller, systemUserId, cache, store, session, ct);
        // Plan 2.11 (A1, A10): the pointer and party rows are written by the Core with their activity, never by a request.
        if (message is not ("Associate" or "Disassociate" or "AddMembersTeam" or "RemoveMembersTeam"))
            DemandNotActivityStore(message, (target as Entity)?.LogicalName ?? (target as EntityReference)?.LogicalName);
        // Plan 3.5 UI-1a: the components and roles of an app are written by the solution import, never by a request.
        if (caller != Caller.Internal && message is not ("Associate" or "Disassociate" or "AddMembersTeam" or "RemoveMembersTeam"))
            DemandNotAppModuleStore((target as Entity)?.LogicalName ?? (target as EntityReference)?.LogicalName);

        switch (message)
        {
            case "Create" when target is Entity created:
                DemandValidProcessWrite(message, created, caller);
                await DemandNotCustomApiAsync(message, created.LogicalName, created.Id, created, caller, session, ct);
                await EnvironmentVariableRules.DemandDefinitionGivenAsync(session, created, isCreate: true, ct);
                await DemandCustomApiLookupsAsync(message, created, isCreate: true, caller, session, ct);
                DemandNotFileAttachment(message, created.LogicalName);
                DemandNotDuplicateRecord(message, created.LogicalName, caller);
                DemandPostWrite(message, created, caller);
                DemandNotMaintained(store, message, created.LogicalName, alsoTraceLog: true, alsoAsyncOperation: true);
                DemandNoRoleTemplate(created);
                DemandKnownTimeZone(created);
                DemandNoImportOnlyColumn(created);
                DemandNoIsManaged(created, caller);
                await DemandWebhookNotInSolutionAsync(request, created, session, ct);
                await rules.DemandNotHoldingNameAsync(created, ct);
                DemandValidAuditSettings(created);
                await rules.DemandHierarchySettingsAsync(created, update: false, ct);
                if (created.LogicalName == FieldSecurityTables.Permission)
                {
                    DemandValidPermission(created);
                    if (IsAdministratorProfile(ValueOf(created, FieldSecurityTables.ProfileId))) throw XrmdFaultException.AdminProfileReadOnly();
                    DemandSecuredColumn(store, ValueOf(created, FieldSecurityTables.EntityName) as string,
                        ValueOf(created, FieldSecurityTables.AttributeLogicalName) as string);
                }
                if (created.LogicalName == Role && StandardRoleIds.Contains(RecordId(created)))
                    throw XrmdFaultException.InvalidArgument(ReservedRoleId);
                if (created.LogicalName == Step) await rules.DemandCanImpersonateAsync(ValueOf(created, ImpersonatingUserId), ct);
                if (created.LogicalName == AccessTeamSchema.TeamTemplate)
                {
                    DemandNoIsSystem(created);
                    await AccessTeamRules.DemandTemplateAsync(session, store, created, isCreate: true, Guid.Empty, ct);
                }
                if (caller != Caller.Internal && created.LogicalName == DuplicateDetectionSchema.Rule)
                    await DuplicateRuleRules.DemandRuleWriteAsync(session, store, created, isCreate: true, Guid.Empty, ct);
                if (caller != Caller.Internal && created.LogicalName == DuplicateDetectionSchema.Condition)
                    await DuplicateRuleRules.DemandConditionWriteAsync(session, store, created, isCreate: true, Guid.Empty, ct);
                if (created.LogicalName == Team && caller != Caller.Internal
                    && AccessTeamRules.SystemManagedColumns.FirstOrDefault(created.Attributes.ContainsKey) is { } managedColumn)
                    throw XrmdFaultException.InvalidArgument(SystemManagedColumnFixed(managedColumn));
                await rules.DemandOwnerNotAccessTeamAsync(ValueOf(created, OwnerId), ct);
                if (created.LogicalName == FieldSecurityTables.Share)
                    await rules.DemandShareNotToSystemManagedTeamAsync(ValueOf(created, FieldSecurityTables.SharePrincipalId) as EntityReference, ct);
                break;
            case "Update" when target is Entity updated:
                DemandKnownTimeZone(updated);
                await EnvironmentVariableRules.DemandDefinitionGivenAsync(session, updated, isCreate: false, ct);
                await rules.UpdateAsync(message, updated, ct);
                break;
            case "Delete" when target is EntityReference deleted:
                await DemandNotCustomApiAsync(message, deleted.LogicalName, deleted.Id, null, caller, session, ct);
                if (deleted.LogicalName == WorkflowSchema.Table && caller != Caller.Internal) await DemandProcessNotActivatedAsync(deleted.Id, session, ct);
                if (deleted.LogicalName == WorkflowSchema.TriggerTable && caller != Caller.Internal) throw ProcessTriggerRefused(message);
                DemandNotFileAttachment(message, deleted.LogicalName);
                DemandNotDuplicateRecord(message, deleted.LogicalName, caller);
                DemandPostWrite(message, new Entity(deleted.LogicalName, deleted.Id), caller);
                DemandNotMaintained(store, message, deleted.LogicalName, alsoTraceLog: false, alsoAsyncOperation: false);
                if (deleted.LogicalName == SystemUser) DemandNotSystemUser(deleted.Id, systemUserId);
                if (deleted.LogicalName == FieldSecurityTables.Profile && deleted.Id == FieldSecurityTables.SystemAdministratorProfileId)
                    throw XrmdFaultException.AdminProfileReadOnly();
                if (deleted.LogicalName == FieldSecurityTables.Permission
                    && IsAdministratorProfile(await StoredPermissionProfileAsync(deleted.Id, session, ct)))
                    throw XrmdFaultException.AdminProfileReadOnly();
                if (deleted.LogicalName == Solution && IsSystemSolution(deleted.Id))
                    throw new XrmdFaultException(ErrorCodes.PrivilegeDenied, SystemSolutionFixed, 403);
                if (deleted.LogicalName == Role && await RoleTemplateAsync(deleted.Id, session, ct) is not null)
                    throw new XrmdFaultException(ErrorCodes.PrivilegeDenied, StandardRolesNotDeleted, 403);
                if (deleted.LogicalName == AsyncOperation) await RollupJobRules.DemandNotDeletedAsync(deleted.Id, session, ct); // plan 2.12b (R9)
                if (deleted.LogicalName == AsyncOperation && await JobStateAsync(deleted.Id, session, ct) is { } state && state != Completed)
                    throw XrmdFaultException.InvalidArgument(OnlyCompletedDeleted);
                await DemandNoCustomApiUsesAsync(deleted, store, session, ct);
                break;
            case "Assign" when target is EntityReference assigned:
                await DemandNotCustomApiAsync(message, assigned.LogicalName, assigned.Id, null, caller, session, ct);
                DemandNotDuplicateRecord(message, assigned.LogicalName, caller);
                DemandNotMaintained(store, message, assigned.LogicalName, alsoTraceLog: true, alsoAsyncOperation: true);
                if (assigned.LogicalName == SystemUser) DemandNotSystemUser(assigned.Id, systemUserId);
                await rules.DemandOwnerNotAccessTeamAsync(parameters.TryGetValue("Assignee", out var assignee) ? assignee : null, ct);
                break;
            case "Associate" or "Disassociate" when target is EntityReference associated
                && parameters.TryGetValue("Relationship", out var r) && r is Relationship relationship
                && parameters.TryGetValue("RelatedEntities", out var rel) && rel is EntityReferenceCollection related:
                if (SecurityAdministration.FindRelationship(relationship.SchemaName) is not null)
                {
                    foreach (var record in related.Prepend(associated))
                        if (record.LogicalName == SystemUser) DemandNotSystemUser(record.Id, systemUserId);
                    if (relationship.SchemaName == SecurityAdministration.TeamMembership && caller != Caller.Internal)
                        foreach (var record in related.Prepend(associated).Where(r => r.LogicalName == Team))
                            if (await AccessTeamRules.IsSystemManagedTeamAsync(session, record.Id, ct))
                                throw AccessTeamRules.UseRecordTeamMessages(record.Id);
                }
                else if (store.GetRelationship(relationship.SchemaName ?? "") is OneToManyRelationshipMetadata lookup)
                {
                    foreach (var update in LookupUpdates(lookup, associated, related, message == "Associate"))
                        await rules.UpdateAsync(message, update, ct);
                }
                break;
            case "AddMembersTeam" or "RemoveMembersTeam" when caller != Caller.Internal
                && parameters.TryGetValue("TeamId", out var teamId) && teamId is Guid membersTeam:
                if (await AccessTeamRules.IsSystemManagedTeamAsync(session, membersTeam, ct))
                    throw AccessTeamRules.UseRecordTeamMessages(membersTeam);
                break;
            case "GrantAccess" or "ModifyAccess" when caller != Caller.Internal
                && parameters.TryGetValue("PrincipalAccess", out var pa) && pa is PrincipalAccess { Principal: { LogicalName: Team } sharedWith }:
                if (await AccessTeamRules.IsSystemManagedTeamAsync(session, sharedWith.Id, ct))
                    throw XrmdFaultException.InvalidArgument(SystemManagedTeamNotShared(sharedWith.Id, message));
                break;
        }
    }

    /// <summary>The refusal of a webhook or a step on one in a solution (plan 3.2): an export never meets one.</summary>
    public const string WebhookNotInSolution = ServiceEndpointSchema.NotInSolution;

    /// <summary>The <c>componenttype</c> values of steps, step images and <c>serviceendpoint</c>.</summary>
    private const int StepComponentType = 92, StepImageComponentType = 93, ServiceEndpointComponentType = 95;

    private const string StepImage = "sdkmessageprocessingstepimage";

    /// <summary>
    /// Plan 3.2: a Create with <c>SolutionUniqueName</c> (the <c>MSCRM.SolutionUniqueName</c> header) of a <c>serviceendpoint</c>,
    /// of a step whose <c>eventhandler</c> is one, or of an image of such a step is refused (400), for every caller.
    /// </summary>
    private static async Task DemandWebhookNotInSolutionAsync(OrganizationRequest request, Entity created, DataSession session,
        CancellationToken ct)
    {
        if (!request.Parameters.TryGetValue("SolutionUniqueName", out var solution) || solution is not string { Length: > 0 }) return;
        if (created.LogicalName == ServiceEndpointSchema.Table || created.LogicalName == Step && IsEndpoint(ValueOf(created, "eventhandler"))
            || created.LogicalName == StepImage && ValueOf(created, "sdkmessageprocessingstepid") is EntityReference step
                                             && await IsWebhookStepAsync(step.Id, session, ct))
            throw XrmdFaultException.InvalidArgument(WebhookNotInSolution);
    }

    /// <summary>
    /// Plan 3.2: an Update (or lookup Associate) that points a step's <c>eventhandler</c> at a <c>serviceendpoint</c> is refused
    /// (400) when the step is a component of a solution other than Default and Active.
    /// </summary>
    private static async Task DemandWebhookStepNotInSolutionAsync(Entity target, Guid id, DataSession session, CancellationToken ct)
    {
        if (target.LogicalName != Step || id == Guid.Empty || !target.Attributes.TryGetValue("eventhandler", out var handler)
            || !IsEndpoint(handler)) return;
        await using var cmd = session.Command(session.Dialect.Limit($"""
            SELECT 1 FROM {session.DataTable("solutioncomponent")}
            WHERE objectid = @id AND componenttype = {StepComponentType} AND solutionid NOT IN (@default, @active)
            """, "1"));
        cmd.AddParameter("@id", DbType.Guid, id);
        cmd.AddParameter("@default", DbType.Guid, SystemSeed.DefaultSolutionId);
        cmd.AddParameter("@active", DbType.Guid, SystemSeed.ActiveSolutionId);
        if (await cmd.ExecuteScalarAsync(ct) is not null) throw XrmdFaultException.InvalidArgument(WebhookNotInSolution);
    }

    /// <summary>
    /// Plan 3.2, for AddSolutionComponent: a <c>serviceendpoint</c> (component type 95), a step (92) whose <c>eventhandler</c> is
    /// one, or an image (93) of such a step, is refused (400) before anything else is checked.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <see cref="WebhookNotInSolution"/>.</exception>
    public static async Task DemandNotWebhookComponentAsync(int componentType, Guid componentId, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var refused = componentType switch
        {
            ServiceEndpointComponentType => true,
            StepComponentType => await IsWebhookStepAsync(componentId, session, ct),
            StepImageComponentType => await ImageStepAsync(componentId, session, ct) is { } step && await IsWebhookStepAsync(step, session, ct),
            _ => false,
        };
        if (refused) throw XrmdFaultException.InvalidArgument(WebhookNotInSolution);
    }

    private static bool IsEndpoint(object? handler) => handler is EntityReference { LogicalName: ServiceEndpointSchema.Table };

    /// <summary>True when the stored step's <c>eventhandlertype</c> is <c>serviceendpoint</c>.</summary>
    private static async Task<bool> IsWebhookStepAsync(Guid stepId, DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command(
            $"SELECT eventhandlertype FROM {session.DataTable(Step)} WHERE sdkmessageprocessingstepid = @id");
        cmd.AddParameter("@id", DbType.Guid, stepId);
        return await cmd.ExecuteScalarAsync(ct) is int type && type == ServiceEndpointSchema.ObjectTypeCode;
    }

    private static async Task<Guid?> ImageStepAsync(Guid imageId, DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command(
            $"SELECT sdkmessageprocessingstepid FROM {session.DataTable(StepImage)} WHERE sdkmessageprocessingstepimageid = @id");
        cmd.AddParameter("@id", DbType.Guid, imageId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>The refusal of a GrantAccess or ModifyAccess to a system-managed team (final review of plan 2.16, I2).</summary>
    private static string SystemManagedTeamNotShared(Guid teamId, string message) =>
        $"{message} to the team {teamId} is refused: a system-managed access team gets access only to its own record through AddUserToRecordTeam.";

    /// <summary>The refusal of an access team as the owner of a record (final re-review of plan 2.16, note 1).</summary>
    private static string AccessTeamNotOwner(Guid teamId) =>
        $"An access team cannot own records. The team {teamId} is an access team (teamtype 1): assign the record to a user or an owner team.";

    /// <summary>The refusal of a field share to a system-managed team (final re-review of plan 2.16, note 2).</summary>
    private static string SystemManagedTeamNoFieldShare(Guid teamId) =>
        $"A field share to the team {teamId} is refused: a system-managed access team gets access only to its own record through AddUserToRecordTeam.";

    /// <summary>The refusal of a write of a system-managed team column (ruling A4).</summary>
    private static string SystemManagedColumnFixed(string column) =>
        $"The attribute {column} of {Team} is written only by AddUserToRecordTeam/RemoveUserFromRecordTeam, for the access teams the system manages.";

    /// <summary><c>teamtemplate.issystem</c> is read-only: a value (null included) is refused rather than ignored.</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static void DemandNoIsSystem(Entity target)
    {
        if (target.Attributes.ContainsKey(AccessTeamSchema.IsSystem))
            throw XrmdFaultException.InvalidArgument(
                $"The attribute {AccessTeamSchema.IsSystem} of {AccessTeamSchema.TeamTemplate} is maintained by the system and cannot be set.");
    }

    /// <summary>
    /// Ruling F6, the writes of field shares (<c>principalobjectattributeaccess</c>), for every caller. Other requests return at
    /// once, without reading anything.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Create: <c>objectid</c> and <c>principalid</c> are needed. <c>objecttypecode</c> is filled from <c>objectid</c> when
    /// missing (and must name its table when given); the row mapper writes <c>principalidtype</c> from <c>principalid</c>; a
    /// missing <c>readaccess</c> or <c>updateaccess</c> is stored false. Then, in this order: the column (<c>attributeid</c>, a
    /// column of that table) must be secured (400 0x8004F50E); <c>readaccess</c> or <c>updateaccess</c> must be true (400
    /// 0x8004F509); the record, column and principal must not be shared already (400 0x8004F50B; the unique index
    /// <see cref="FieldSecurityTables.ShareUniqueIndex"/> settles a race); the record must exist (404); the
    /// caller must hold Read on the record (<paramref name="demandRecordRead"/>, the record-access 403); then the grant rule.</item>
    /// <item>Update: only <c>readaccess</c> and <c>updateaccess</c> change (400 for the other columns); the stored column must
    /// still be secured (0x8004F50E); the result must keep one of them true (400 0x8004F50A); Read on the record; the grant rule
    /// on the resulting access. A missing share is left to the core operation's 404.</item>
    /// <item>Delete: Read on the record, then the grant rule on the stored access, unless the column is no longer secured.</item>
    /// <item>The grant rule (F6 rule 4, 403 0x8004F503; F1 bypasses): the caller can update the column on the record (profile
    /// <c>canupdate</c> or an update share) and, when the share grants read, can also read it there (profile <c>canread</c> or a
    /// read share), the AND over the user and a non-SYSTEM impersonator. Nobody grants more than they hold.</item>
    /// <item>Associate/Disassociate over a lookup of the share table: 400 (it would move the share).</item>
    /// </list>
    /// </remarks>
    /// <exception cref="XrmdFaultException">400, 403 or 404 as listed.</exception>
    /// <param name="demandRecordRead">Demands Read on a record for the caller (the record-access check of <see cref="SecurityGuard"/>).</param>
    public static async Task EnforceFieldSharesAsync(OrganizationRequest request, Caller caller, FieldSecurity fields, IMetadataStore store,
        DataSession session, Func<EntityReference, CancellationToken, Task> demandRecordRead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(demandRecordRead);
        if (!Messages.TryGetValue(request.RequestName?.ToLowerInvariant() ?? "", out var message)) return;
        var parameters = request.Parameters;
        parameters.TryGetValue(Target, out var target);
        switch (message)
        {
            case "Create" when target is Entity { LogicalName: FieldSecurityTables.Share } created:
                await new ShareRules(caller, fields, store, session, demandRecordRead).CreateAsync(created, ct);
                break;
            case "Update" when target is Entity { LogicalName: FieldSecurityTables.Share } updated:
                await new ShareRules(caller, fields, store, session, demandRecordRead).UpdateAsync(updated, ct);
                break;
            case "Delete" when target is EntityReference { LogicalName: FieldSecurityTables.Share } deleted:
                await new ShareRules(caller, fields, store, session, demandRecordRead).DeleteAsync(deleted.Id, ct);
                break;
            case "Associate" or "Disassociate" when target is EntityReference associated
                && parameters.TryGetValue("Relationship", out var r) && r is Relationship relationship
                && parameters.TryGetValue("RelatedEntities", out var rel) && rel is EntityReferenceCollection related
                && store.GetRelationship(relationship.SchemaName ?? "") is OneToManyRelationshipMetadata { ReferencingEntity: FieldSecurityTables.Share } lookup:
                if (LookupUpdates(lookup, associated, related, message == "Associate").Any())
                    throw XrmdFaultException.InvalidArgument(ShareColumnsFixed);
                break;
        }
    }

    private const string ShareColumnsFixed =
        "Only readaccess and updateaccess of a field share can be changed; delete the share and create another one instead.";

    /// <summary>The F6 rules of one field share write (<see cref="EnforceFieldSharesAsync"/>).</summary>
    private sealed class ShareRules(Caller caller, FieldSecurity fields, IMetadataStore store, DataSession session,
        Func<EntityReference, CancellationToken, Task> demandRecordRead)
    {
        private const string ShareTable = FieldSecurityTables.Share;

        /// <summary>A stored share: its record (table, id), column id and access.</summary>
        private sealed record StoredShare(string? Table, Guid Record, Guid Attribute, bool Read, bool Update);

        public async Task CreateAsync(Entity target, CancellationToken ct)
        {
            // The record: objectid, with objecttypecode filled from it (or naming its table when objectid has none).
            if (ValueOf(target, FieldSecurityTables.ShareObjectId) is not EntityReference { Id: var recordId } objectRef || recordId == Guid.Empty)
                throw XrmdFaultException.InvalidArgument($"A field share needs {FieldSecurityTables.ShareObjectId}, the record it applies to.");
            var givenTable = ValueOf(target, FieldSecurityTables.ShareObjectTypeCodeColumn) as string;
            var table = string.IsNullOrEmpty(objectRef.LogicalName) ? givenTable : objectRef.LogicalName;
            if (string.IsNullOrEmpty(table) || store.GetEntity(table) is not { } entity)
                throw XrmdFaultException.InvalidArgument($"The table of the record of a field share ('{table}') was not found.");
            if (!string.IsNullOrEmpty(givenTable) && !string.Equals(givenTable, entity.LogicalName, StringComparison.OrdinalIgnoreCase))
                throw XrmdFaultException.InvalidArgument(
                    $"{FieldSecurityTables.ShareObjectTypeCodeColumn} '{givenTable}' does not name the table of {FieldSecurityTables.ShareObjectId} ('{entity.LogicalName}').");
            target[FieldSecurityTables.ShareObjectId] = new EntityReference(entity.LogicalName, recordId);
            target[FieldSecurityTables.ShareObjectTypeCodeColumn] = entity.LogicalName;

            // The principal: principalidtype is written from its table by the row mapper.
            if (ValueOf(target, FieldSecurityTables.SharePrincipalId) is not EntityReference { Id: var principalId } || principalId == Guid.Empty)
                throw XrmdFaultException.InvalidArgument($"A field share needs {FieldSecurityTables.SharePrincipalId}, the user or team it is given to.");

            // 1. The column must be secured.
            var attribute = SecuredColumn(entity, ValueOf(target, FieldSecurityTables.ShareAttributeId));
            // 2. It must grant something; a missing (or null) access is stored false.
            var read = IsTrue(ValueOf(target, FieldSecurityTables.ShareReadAccess));
            var update = IsTrue(ValueOf(target, FieldSecurityTables.ShareUpdateAccess));
            if (!read && !update) throw XrmdFaultException.ShareWithoutAccess();
            target[FieldSecurityTables.ShareReadAccess] = read;
            target[FieldSecurityTables.ShareUpdateAccess] = update;
            // 3. Once per record, column and principal.
            if (await ShareExistsAsync(recordId, attribute.MetadataId!.Value, principalId, ct))
                throw XrmdFaultException.ShareDuplicate(attribute.LogicalName, recordId, principalId);
            // The record must exist (the core operation answers a missing principal with its 404); the caller must read the record;
            // then 4. the grant rule.
            if (!await RecordExistsAsync(entity, recordId, ct)) throw XrmdFaultException.ObjectDoesNotExist(entity.LogicalName, recordId);
            await demandRecordRead(new EntityReference(entity.LogicalName, recordId), ct);
            await DemandCanGrantAsync(entity.LogicalName, recordId, attribute.LogicalName, read, ct);
        }

        public async Task UpdateAsync(Entity target, CancellationToken ct)
        {
            var id = RecordId(target);
            var primaryId = SystemAttributes.PrimaryIdName(ShareTable);
            if (target.Attributes.Keys.FirstOrDefault(name => name is not (FieldSecurityTables.ShareReadAccess or FieldSecurityTables.ShareUpdateAccess)
                    && name != primaryId && !SystemAttributes.IsReservedName(ShareTable, name)) is not null)
                throw XrmdFaultException.InvalidArgument(ShareColumnsFixed);
            if (id == Guid.Empty || await StoredAsync(id, ct) is not { } stored) return; // the core operation's 400 or 404
            if (stored.Table is null || store.GetEntity(stored.Table) is not { } entity) return;

            // 1. The column must still be secured.
            var attribute = SecuredColumn(entity, stored.Attribute);
            // 2. The share must keep granting something.
            var read = target.Attributes.TryGetValue(FieldSecurityTables.ShareReadAccess, out var r) ? IsTrue(r) : stored.Read;
            var update = target.Attributes.TryGetValue(FieldSecurityTables.ShareUpdateAccess, out var u) ? IsTrue(u) : stored.Update;
            if (!read && !update) throw XrmdFaultException.ShareUpdateWithoutAccess();
            // Read on the record, then 4. the grant rule on the resulting access.
            await demandRecordRead(new EntityReference(entity.LogicalName, stored.Record), ct);
            await DemandCanGrantAsync(entity.LogicalName, stored.Record, attribute.LogicalName, read, ct);
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            if (id == Guid.Empty || await StoredAsync(id, ct) is not { } stored) return; // the core operation's 404
            if (stored.Table is null || store.GetEntity(stored.Table) is not { } entity) return;
            await demandRecordRead(new EntityReference(entity.LogicalName, stored.Record), ct);
            if (entity.Attributes.FirstOrDefault(a => a.MetadataId == stored.Attribute) is not { IsSecured: true, LogicalName: { } column })
                return; // a share left on a column that is no longer secured grants nothing: who reads the record removes it
            // Revoking needs the rights granting does (R6d).
            await DemandCanGrantAsync(entity.LogicalName, stored.Record, column, stored.Read, ct);
        }

        /// <summary>The column of <paramref name="entity"/> whose MetadataId is <paramref name="attributeId"/>, which must be secured.</summary>
        /// <exception cref="XrmdFaultException">400 0x8004F50E for an unknown or unsecured column.</exception>
        private static AttributeMetadata SecuredColumn(EntityMetadata entity, object? attributeId)
        {
            var id = attributeId switch { Guid g => g, string s when Guid.TryParse(s, out var g) => g, _ => Guid.Empty };
            var attribute = id == Guid.Empty ? null : entity.Attributes.FirstOrDefault(a => a.MetadataId == id);
            if (attribute is not { IsSecured: true, LogicalName: not null, MetadataId: not null })
                throw XrmdFaultException.PermissionOnUnsecuredColumn(entity.LogicalName, attribute?.LogicalName ?? id.ToString());
            return attribute;
        }

        /// <summary>
        /// F6 rule 4: update of the column on the record and, with <paramref name="read"/>, read of it there, each through the
        /// caller's profiles or a share on the record (<see cref="FieldSecurity.ForAsync"/> and <see cref="FieldSecurity.SharesAsync"/>
        /// AND the principals; F1 bypasses).
        /// </summary>
        /// <exception cref="XrmdFaultException">403 0x8004F503.</exception>
        private async Task DemandCanGrantAsync(string entity, Guid record, string attribute, bool read, CancellationToken ct)
        {
            var set = await fields.ForAsync(caller, entity, ct);
            var canUpdate = set.CanUpdate(attribute);
            var canRead = !read || set.CanRead(attribute);
            if (!canUpdate || !canRead)
            {
                var shares = await fields.SharesAsync(caller, entity, [record], [attribute], session, ct);
                var grant = shares.TryGetValue(record, out var g) ? g : null;
                canUpdate |= grant?.Update.Contains(attribute) == true;
                canRead |= grant?.Read.Contains(attribute) == true;
            }
            if (!canUpdate) throw XrmdFaultException.ShareWithoutUpdate(attribute, record, caller.UserId);
            if (!canRead) throw XrmdFaultException.ShareWithoutRead(attribute, record, caller.UserId);
        }

        private async Task<bool> ShareExistsAsync(Guid record, Guid attribute, Guid principal, CancellationToken ct)
        {
            await session.KeyLockAsync(ShareTable, record, ct);
            await using var cmd = session.Command(
                $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {session.DataTable(ShareTable)}{session.Dialect.LockHint(RowLock.UpdateRange)} " +
                $"WHERE {session.Quote(FieldSecurityTables.ShareObjectId)} = @_record AND {session.Quote(FieldSecurityTables.ShareAttributeId)} = @_attribute " +
                $"AND {session.Quote(FieldSecurityTables.SharePrincipalId)} = @_principal) THEN 1 ELSE 0 END");
            cmd.AddParameter("@_record", DbType.Guid, record);
            cmd.AddParameter("@_attribute", DbType.Guid, attribute);
            cmd.AddParameter("@_principal", DbType.Guid, principal);
            return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
        }

        private async Task<bool> RecordExistsAsync(EntityMetadata entity, Guid id, CancellationToken ct)
        {
            await using var cmd = session.Command(
                $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {session.DataTable(entity.LogicalName)} " +
                $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(entity))} = {IdParameter}) THEN 1 ELSE 0 END");
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
        }

        /// <summary>The stored share (read with UPDLOCK); null when it does not exist.</summary>
        private async Task<StoredShare?> StoredAsync(Guid id, CancellationToken ct)
        {
            await using var cmd = session.Command(
                $"SELECT {session.Quote(FieldSecurityTables.ShareObjectTypeCodeColumn)}, {session.Quote(FieldSecurityTables.ShareObjectId)}, {session.Quote(FieldSecurityTables.ShareAttributeId)}, " +
                $"{session.Quote(FieldSecurityTables.ShareReadAccess)}, {session.Quote(FieldSecurityTables.ShareUpdateAccess)} " +
                $"FROM {session.DataTable(ShareTable)}{session.Dialect.LockHint(RowLock.Update)} WHERE {session.Quote(SystemAttributes.PrimaryIdName(ShareTable))} = {IdParameter}" +
                session.Dialect.LockClause(RowLock.Update));
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            return new StoredShare(rd.IsDBNull(0) ? null : rd.GetString(0), rd.IsDBNull(1) ? Guid.Empty : rd.GetGuid(1),
                rd.IsDBNull(2) ? Guid.Empty : rd.GetGuid(2), !rd.IsDBNull(3) && rd.GetBoolean(3), !rd.IsDBNull(4) && rd.GetBoolean(4));
        }

        private static bool IsTrue(object? value) => value is true;
    }

    /// <summary>
    /// The Updates a one-to-many Associate/Disassociate makes, as the core operation resolves the sides: the target is the
    /// referenced record (each related record of the referencing entity is updated) or, failing that, the referencing record
    /// (with exactly one related record). Records the core operation rejects (400) yield nothing.
    /// </summary>
    internal static IEnumerable<Entity> LookupUpdates(OneToManyRelationshipMetadata lookup, EntityReference target,
        EntityReferenceCollection related, bool associate)
    {
        var attribute = lookup.ReferencingAttribute;
        if (attribute is null) yield break;
        if (target.LogicalName == lookup.ReferencedEntity)
        {
            foreach (var record in related.Where(e => e.LogicalName == lookup.ReferencingEntity))
                yield return new Entity(record.LogicalName, record.Id)
                {
                    [attribute] = associate ? new EntityReference(target.LogicalName, target.Id) : null,
                };
        }
        else if (target.LogicalName == lookup.ReferencingEntity && related.Count == 1 && related[0].LogicalName == lookup.ReferencedEntity)
        {
            yield return new Entity(target.LogicalName, target.Id)
            {
                [attribute] = associate ? new EntityReference(related[0].LogicalName, related[0].Id) : null,
            };
        }
    }

    /// <summary>The rules that read the caller's profile or the database.</summary>
    private sealed class Rules(Caller caller, Guid systemUserId, SecurityCache cache, IMetadataStore store, DataSession session)
    {
        /// <summary>
        /// A <c>solution</c> row is not given, by a Create or a rename, the unique name <c>{name}_Upgrade</c> of the holding
        /// solution of the installed managed solution <c>{name}</c> (final review I2): only an import with
        /// <c>HoldingSolution</c> or <c>StageAndUpgrade</c> writes it, inside <see cref="LayerOperationScope"/>. (A managed row
        /// is not renamed by any request: its Update is refused by the core, ruling M14.)
        /// </summary>
        /// <exception cref="XrmdFaultException">400 InvalidArgument naming both solutions.</exception>
        public async Task DemandNotHoldingNameAsync(Entity target, CancellationToken ct)
        {
            if (target.LogicalName != Solution || caller == Caller.Internal || LayerOperationScope.IsActive) return;
            if (!target.Attributes.TryGetValue(UniqueName, out var value) || value is not string name
                || !name.EndsWith(HoldingSuffix, StringComparison.OrdinalIgnoreCase) || name.Length == HoldingSuffix.Length) return;
            var baseName = name[..^HoldingSuffix.Length];
            await using var cmd = session.Command(
                $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {session.DataTable(Solution)} WHERE {session.Quote(UniqueName)} = @_base AND {session.Quote(IsManaged)} = {session.Dialect.BooleanLiteral(true)}) THEN 1 ELSE 0 END");
            cmd.AddParameter("@_base", DbType.String, baseName, 256);
            if ((int)(await cmd.ExecuteScalarAsync(ct))! == 1)
                throw XrmdFaultException.InvalidArgument(
                    $"The unique name '{name}' is the holding solution name of the managed solution '{baseName}': only an import with HoldingSolution or StageAndUpgrade creates it.");
        }

        /// <summary>The rules of an Update of <paramref name="target"/>, reported under <paramref name="message"/>.</summary>
        public async Task UpdateAsync(string message, Entity target, CancellationToken ct)
        {
            DemandNotActivityStore(message, target.LogicalName);
            DemandValidProcessWrite(message, target, caller);
            await DemandNotCustomApiAsync(message, target.LogicalName, RecordId(target), target, caller, session, ct);
            await DemandCustomApiLookupsAsync(message, target, isCreate: false, caller, session, ct);
            DemandNotFileAttachment(message, target.LogicalName);
            DemandNotDuplicateRecord(message, target.LogicalName, caller);
            DemandPostWrite(message, target, caller);
            DemandNotMaintained(store, message, target.LogicalName, alsoTraceLog: true, alsoAsyncOperation: false);
            var id = RecordId(target);
            if (target.LogicalName == SystemUser) DemandNotSystemUser(id, systemUserId);
            // Plan 2.12b (R9): a rollup calculation job is postponed or rescheduled, never cancelled; any other job only cancelled.
            if (target.LogicalName == AsyncOperation && !await RollupJobRules.DemandUpdateAsync(target, id, session, ct))
                await DemandCancellationAsync(target, id, session, ct);
            DemandNoRoleTemplate(target);
            DemandNoImportOnlyColumn(target);
            DemandNoIsManaged(target, caller);
            await DemandWebhookStepNotInSolutionAsync(target, id, session, ct);
            await DemandNotHoldingNameAsync(target, ct);
            DemandValidAuditSettings(target);
            await DemandHierarchySettingsAsync(target, update: true, ct);
            if (target.LogicalName == FieldSecurityTables.Profile && id == FieldSecurityTables.SystemAdministratorProfileId)
                throw XrmdFaultException.AdminProfileReadOnly();
            if (target.LogicalName == FieldSecurityTables.Permission)
            {
                DemandValidPermission(target);
                if ((target.Attributes.TryGetValue(FieldSecurityTables.ProfileId, out var profile) && IsAdministratorProfile(profile))
                    || (id != Guid.Empty && IsAdministratorProfile(await StoredPermissionProfileAsync(id, session, ct))))
                    throw XrmdFaultException.AdminProfileReadOnly();
                // Moving a permission to another column: the resulting column must exist and be secured.
                var entityGiven = target.Attributes.TryGetValue(FieldSecurityTables.EntityName, out var entityValue);
                var attributeGiven = target.Attributes.TryGetValue(FieldSecurityTables.AttributeLogicalName, out var attributeValue);
                if (entityGiven || attributeGiven)
                {
                    var storedColumn = id == Guid.Empty ? (null, null) : await StoredPermissionColumnAsync(id, session, ct);
                    DemandSecuredColumn(store, entityGiven ? entityValue as string : storedColumn.Entity,
                        attributeGiven ? attributeValue as string : storedColumn.Attribute);
                }
            }
            if (target.LogicalName == Solution && target.Attributes.ContainsKey("uniquename") && IsSystemSolution(id))
                throw new XrmdFaultException(ErrorCodes.PrivilegeDenied, SystemSolutionFixed, 403);
            if (target.LogicalName == Team && target.Attributes.TryGetValue(TeamType, out var teamType)
                && id != Guid.Empty && await StoredTeamTypeAsync(id, ct) is { } stored && TeamTypeValue(teamType) != stored)
                throw XrmdFaultException.InvalidArgument(TeamTypeFixed);
            if (target.LogicalName == Team && caller != Caller.Internal && id != Guid.Empty) await DemandSystemManagedUnchangedAsync(target, id, ct);
            if (target.Attributes.TryGetValue(OwnerId, out var owner)) await DemandOwnerNotAccessTeamAsync(owner, ct);
            if (target.LogicalName == FieldSecurityTables.Share && id != Guid.Empty)
                await DemandShareNotToSystemManagedTeamAsync(
                    target.Attributes.TryGetValue(FieldSecurityTables.SharePrincipalId, out var principal) && principal is EntityReference given
                        ? given : await StoredSharePrincipalAsync(id, ct), ct);
            if (target.LogicalName == AccessTeamSchema.TeamTemplate)
            {
                DemandNoIsSystem(target);
                await AccessTeamRules.DemandTemplateAsync(session, store, target, isCreate: false, id, ct);
            }
            if (caller != Caller.Internal && target.LogicalName == DuplicateDetectionSchema.Rule)
                await DuplicateRuleRules.DemandRuleWriteAsync(session, store, target, isCreate: false, id, ct);
            if (caller != Caller.Internal && target.LogicalName == DuplicateDetectionSchema.Condition)
                await DuplicateRuleRules.DemandConditionWriteAsync(session, store, target, isCreate: false, id, ct);
            if (target.LogicalName == Role && target.Attributes.Keys.Any(n => n is not ("name" or "description" or "roleid"))
                && await RoleTemplateAsync(id, session, ct) == StandardRoles.SystemAdministrator)
                throw new XrmdFaultException(ErrorCodes.PrivilegeDenied, SystemAdministratorRoleFixed, 403);
            if (target.LogicalName == Step)
            {
                if (cache.IsUnrestricted(caller, systemUserId)) return;
                var impersonating = target.Attributes.TryGetValue(ImpersonatingUserId, out var value) ? value
                    : id == Guid.Empty ? null
                    : await StoredImpersonatingUserAsync(id, ct);
                await DemandCanImpersonateAsync(impersonating, ct);
            }
        }

        /// <summary>
        /// An access team owns no record (final re-review of plan 2.16, note 1): <paramref name="owner"/>, the <c>ownerid</c> of a
        /// Create or Update or the <c>Assignee</c> of an Assign, does not name a team whose <c>teamtype</c> is 1 (Access),
        /// system-managed or not. <see cref="Caller.Internal"/> is exempt. A missing team is left to the core operation's 404;
        /// <c>teamtype</c> never changes (<see cref="TeamTypeFixed"/>), so the row is read without a lock.
        /// </summary>
        /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
        public async Task DemandOwnerNotAccessTeamAsync(object? owner, CancellationToken ct)
        {
            if (caller == Caller.Internal || owner is not EntityReference { LogicalName: Team, Id: var teamId } || teamId == Guid.Empty) return;
            await using var cmd = session.Command(
                $"SELECT {session.Dialect.IsNull(session.Quote(TeamType), "0")} FROM {session.DataTable(Team)} " +
                $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(Team))} = {IdParameter}");
            cmd.AddParameter(IdParameter, DbType.Guid, teamId);
            if (await cmd.ExecuteScalarAsync(ct) is int type && type == AccessTeamType)
                throw XrmdFaultException.InvalidArgument(AccessTeamNotOwner(teamId));
        }

        /// <summary>
        /// A field share (<c>principalobjectattributeaccess</c>) never names a system-managed access team (final re-review of
        /// plan 2.16, note 2): the record team messages check the adder for the template's record rights only, so a column the
        /// team could read or update would reach each new member unchecked. <see cref="Caller.Internal"/> is exempt.
        /// </summary>
        /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
        public async Task DemandShareNotToSystemManagedTeamAsync(EntityReference? principal, CancellationToken ct)
        {
            if (caller == Caller.Internal || principal is not { LogicalName: Team, Id: var teamId }) return;
            if (await AccessTeamRules.IsSystemManagedTeamAsync(session, teamId, ct))
                throw XrmdFaultException.InvalidArgument(SystemManagedTeamNoFieldShare(teamId));
        }

        /// <summary>
        /// The stored principal id of a field share, as a team reference (a user's id matches no team row, so the check above
        /// passes for it); null when the share is missing.
        /// </summary>
        private async Task<EntityReference?> StoredSharePrincipalAsync(Guid shareId, CancellationToken ct)
        {
            if (caller == Caller.Internal) return null;
            await using var cmd = session.Command(
                $"SELECT {session.Quote(FieldSecurityTables.SharePrincipalId)} FROM {session.DataTable(FieldSecurityTables.Share)} " +
                $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(FieldSecurityTables.Share))} = {IdParameter}");
            cmd.AddParameter(IdParameter, DbType.Guid, shareId);
            return await cmd.ExecuteScalarAsync(ct) is Guid id ? new EntityReference(Team, id) : null;
        }

        /// <summary>
        /// The hierarchy security settings of the <c>organization</c> row (plan 2.5): a write that changes any of them comes from
        /// a System Administrator (under impersonation, both principals; SYSTEM and <see cref="Caller.Internal"/> pass). On an
        /// Update, a setting sent with its stored value is not a change (final review, minor 4: a Retrieve-all-columns-then-Update
        /// by another user passes); on a Create every setting named counts. Then <c>maxdepthforhierarchicalsecuritymodel</c> is
        /// 1 to 100 and <c>usepositionhierarchy</c> is not true (the position model is not supported).
        /// </summary>
        /// <exception cref="XrmdFaultException">403 PrivilegeDenied; 400 InvalidArgument.</exception>
        public async Task DemandHierarchySettingsAsync(Entity target, bool update, CancellationToken ct)
        {
            if (target.LogicalName != Organization || !HierarchySettings.Any(target.Attributes.ContainsKey)) return;
            if (!cache.IsUnrestricted(caller, systemUserId)
                && !(await cache.PrincipalsAsync(caller, ct)).All(p => p.IsSystemAdministrator)
                && (!update || await ChangesHierarchySettingsAsync(target, ct)))
                throw new XrmdFaultException(ErrorCodes.PrivilegeDenied, HierarchySettingsAdministratorOnly, 403);
            if (target.Attributes.TryGetValue(HierarchyDepth, out var depth)
                && !(depth is int or long && Convert.ToInt64(depth) is >= 1 and <= MaxHierarchyDepth))
                throw XrmdFaultException.InvalidArgument(HierarchyDepthOutOfRange);
            if (target.Attributes.TryGetValue(PositionHierarchy, out var position) && position is true)
                throw XrmdFaultException.InvalidArgument(PositionHierarchyUnsupported);
        }

        /// <summary>
        /// A step impersonating (<paramref name="impersonating"/>: an EntityReference or a Guid) another user than the caller,
        /// or than the caller's impersonator: that principal must hold <c>prvActOnBehalfOfAnotherUser</c> from a role
        /// assigned directly. SYSTEM and <see cref="Caller.Internal"/> are exempt.
        /// </summary>
        /// <exception cref="XrmdFaultException">403 CannotActOnBehalfOfAnotherUser.</exception>
        public async Task DemandCanImpersonateAsync(object? impersonating, CancellationToken ct)
        {
            var impersonated = impersonating switch
            {
                EntityReference reference => reference.Id,
                Guid id => id,
                _ => Guid.Empty,
            };
            if (impersonated == Guid.Empty || cache.IsUnrestricted(caller, systemUserId)) return;
            foreach (var principal in (Guid?[])[caller.UserId, caller.ImpersonatorId])
            {
                if (principal is not { } id || id == impersonated || id == systemUserId) continue;
                if (!(await cache.GetProfileAsync(id, ct)).DirectPrivileges.Contains(PrivilegeNames.ActOnBehalfOfAnotherUser))
                    throw XrmdFaultException.CannotActOnBehalfOfAnotherUser();
            }
        }

        /// <summary>
        /// True when the Update sets a hierarchy setting to another value than the stored one. A row that cannot be read, or a
        /// value of an unexpected type, counts as a change (fails closed).
        /// </summary>
        private async Task<bool> ChangesHierarchySettingsAsync(Entity target, CancellationToken ct)
        {
            var id = RecordId(target);
            if (id == Guid.Empty) return true;
            await using var cmd = session.Command(
                $"SELECT {session.Quote(HierarchyEnabled)}, {session.Quote(HierarchyDepth)}, {session.Quote(PositionHierarchy)} FROM {session.DataTable(Organization)}" +
                $"{session.Dialect.LockHint(RowLock.UpdateRange)} WHERE {session.Quote(SystemAttributes.PrimaryIdName(Organization))} = {IdParameter}" +
                session.Dialect.LockClause(RowLock.UpdateRange));
            cmd.AddParameter(IdParameter, DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return true;
            for (var i = 0; i < HierarchySettings.Length; i++)
                if (target.Attributes.TryGetValue(HierarchySettings[i], out var given)
                    && !SameSetting(given, rd.IsDBNull(i) ? null : rd.GetValue(i)))
                    return true;
            return false;
        }

        /// <summary>A setting value equals the stored one: null and null, two booleans, or two whole numbers.</summary>
        private static bool SameSetting(object? given, object? stored) => (given, stored) switch
        {
            (null, null) => true,
            (bool a, bool b) => a == b,
            (int or long, int or long or short or byte) => Convert.ToInt64(given) == Convert.ToInt64(stored),
            _ => false,
        };

        /// <summary>
        /// Ruling A4: an Update of a team does not change <c>teamtemplateid</c>, <c>regardingobjectid</c> or <c>systemmanaged</c>
        /// (a stored value sent back passes, NULL <c>systemmanaged</c> as false). A missing team is left to the core's 404.
        /// </summary>
        /// <exception cref="XrmdFaultException">400 InvalidArgument naming the column.</exception>
        private async Task DemandSystemManagedUnchangedAsync(Entity target, Guid id, CancellationToken ct)
        {
            if (!AccessTeamRules.SystemManagedColumns.Any(target.Attributes.ContainsKey)
                || await AccessTeamRules.StoredSystemManagedColumnsAsync(session, id, ct) is not { } stored) return;
            foreach (var column in AccessTeamRules.SystemManagedColumns)
            {
                if (!target.Attributes.TryGetValue(column, out var value)) continue;
                var same = column == AccessTeamSchema.SystemManaged
                    ? (value ?? false) is bool flag && flag == stored.SystemManaged
                    : LookupId(value) == (column == AccessTeamSchema.TeamTemplateId ? stored.Template : stored.Regarding);
                if (!same) throw XrmdFaultException.InvalidArgument(SystemManagedColumnFixed(column));
            }
        }

        /// <summary>The id of a lookup value (null for null); <see cref="Guid.Empty"/> for a value of another type (a change).</summary>
        private static Guid? LookupId(object? value) => value switch
        {
            null => null,
            EntityReference reference => reference.Id,
            Guid id => id,
            _ => Guid.Empty,
        };

        /// <summary>The stored <c>teamtype</c> of the team (NULL as 0), read with UPDLOCK; null when the team does not exist.</summary>
        private async Task<int?> StoredTeamTypeAsync(Guid teamId, CancellationToken ct)
        {
            await using var cmd = session.Command(
                $"SELECT {session.Dialect.IsNull(session.Quote(TeamType), "0")} FROM {session.DataTable(Team)}{session.Dialect.LockHint(RowLock.UpdateRange)} " +
                $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(Team))} = {IdParameter}{session.Dialect.LockClause(RowLock.UpdateRange)}");
            cmd.AddParameter(IdParameter, DbType.Guid, teamId);
            return await cmd.ExecuteScalarAsync(ct) as int?;
        }

        /// <summary>The <c>teamtype</c> an Update sets (null as 0, Owner); an unreadable value counts as a change.</summary>
        private static int TeamTypeValue(object? value) => value switch
        {
            null => 0,
            OptionSetValue option => option.Value,
            int number => number,
            _ => int.MinValue,
        };

        /// <summary>The stored <c>impersonatinguserid</c> of the step (null when not set or when the step does not exist).</summary>
        private async Task<Guid?> StoredImpersonatingUserAsync(Guid stepId, CancellationToken ct)
        {
            await using var cmd = session.Command(
                $"SELECT {session.Quote(ImpersonatingUserId)} FROM {session.DataTable(Step)} " +
                $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(Step))} = {IdParameter}");
            cmd.AddParameter(IdParameter, DbType.Guid, stepId);
            return await cmd.ExecuteScalarAsync(ct) as Guid?;
        }
    }

    /// <summary>
    /// The id of an Update's target: <see cref="Entity.Id"/>, else its primary key attribute when it is a Guid; otherwise
    /// <see cref="Guid.Empty"/> (the core operation answers a missing or malformed id with its 400).
    /// </summary>
    internal static Guid RecordId(Entity target) =>
        target.Id != Guid.Empty ? target.Id
        : target.Attributes.TryGetValue(SystemAttributes.PrimaryIdName(target.LogicalName ?? ""), out var value) && value is Guid id ? id
        : Guid.Empty;

    /// <summary>The system solutions <c>Default</c> and <c>Active</c> (fixed ids, seeded).</summary>
    private static bool IsSystemSolution(Guid id) => id == SystemSeed.DefaultSolutionId || id == SystemSeed.ActiveSolutionId;

    /// <summary>The system-maintained tables: <see cref="NoDirectWrites"/> and every intersect entity (<c>IsIntersect</c>).</summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied when the table is maintained by the system for this message;
    /// 400 InvalidArgument for the <c>audit</c> table, which no request writes.</exception>
    private static void DemandNotMaintained(IMetadataStore store, string message, string? entity, bool alsoTraceLog, bool alsoAsyncOperation)
    {
        if (entity is null) return;
        if (entity == AuditTable.Name)
            throw XrmdFaultException.InvalidArgument(
                $"{message} is not supported on '{AuditTable.Name}': audit rows are written by the audit writer only, and removed by the audit delete messages and the retention period.");
        if (NoDirectWrites.Contains(entity) || store.GetEntity(entity)?.IsIntersect == true
            || (alsoTraceLog && entity == PluginTraceLog) || (alsoAsyncOperation && entity == AsyncOperation))
            throw XrmdFaultException.SystemMaintainedTable(message, entity);
    }

    /// <summary>
    /// The columns of a <c>workflow</c> row only the server writes (plan 2.21, ruling A16): the activation's links, an action's
    /// message, the unsupported steps (W4), and the solution layer columns <c>xrmverse_componentxml</c> and <c>ismanaged</c>. Those two
    /// have their own refusals before stage 10 too; listing them here makes the pipeline's check after the pre-operation plug-ins
    /// refuse them when a plug-in adds them (fix round 1, minor 1).
    /// </summary>
    private static readonly string[] ServerWrittenProcessColumns =
    [
        WorkflowSchema.UnsupportedStepsColumn, WorkflowSchema.ActiveWorkflowId, WorkflowSchema.ParentWorkflowId, WorkflowSchema.SdkMessageId,
        ComponentXml, IsManaged,
    ];

    /// <summary>
    /// Plan 2.21, ruling A16 (lifting 2.14's W3): the checks of a public write of a process that need no stored row and no parser.
    /// A Create or Update (or the lookup Associate/Disassociate that is one) of <c>processtrigger</c> is refused. A Create or Update
    /// of <c>workflow</c> is refused when it sends a server-written column (<c>xrmverse_unsupportedsteps</c>, <c>activeworkflowid</c>,
    /// <c>parentworkflowid</c>, <c>sdkmessageid</c>, <c>xrmverse_componentxml</c>, <c>ismanaged</c>), a <c>type</c> other than 1 (Definition) or a <c>category</c> other than 0, 2
    /// or 3; a Create must give its <c>category</c> and be Draft (<c>statecode</c> 0 and <c>statuscode</c> 1, or none). Every problem
    /// is listed. <see cref="Caller.Internal"/> (the solution import and <c>WorkflowStore</c>) is exempt. The rest of A16 (an Update
    /// edits a Draft definition only, the immutable columns, the XAML parsed and validated, no unsupported step) is the pipeline's,
    /// at stage 20 through <c>IWorkflowPipelineHook.ValidateWriteAsync</c>; the pipeline repeats this check there, after the
    /// pre-operation plug-ins, which could have widened the Target. Delete and Assign follow their own rules (the Delete of a
    /// <c>processtrigger</c> is refused too: its rows go with their definition, in the core).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    public static void DemandValidProcessWrite(string message, Entity target, Caller caller)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (caller == Caller.Internal) return;
        if (target.LogicalName == WorkflowSchema.TriggerTable) throw ProcessTriggerRefused(message);
        if (target.LogicalName != WorkflowSchema.Table) return;

        var errors = new List<string>();
        foreach (var column in ServerWrittenProcessColumns.Where(target.Attributes.ContainsKey))
            errors.Add($"The column {column} is written by the server and cannot be set.");
        if (target.Attributes.TryGetValue(WorkflowSchema.TypeColumn, out var type) && OptionValue(type) != (int)WorkflowSchema.Type.Definition)
            errors.Add($"Only a definition (type 1) is written, not type {Describe(type)}: activations are made by SetState.");
        var create = message == "Create";
        if (target.Attributes.TryGetValue(WorkflowSchema.CategoryColumn, out var category))
        {
            if (OptionValue(category) is not ((int)WorkflowSchema.Category.Workflow or (int)WorkflowSchema.Category.BusinessRule or (int)WorkflowSchema.Category.Action))
                errors.Add($"Processes of category {Describe(category)} are not written: use 0 (workflow), 2 (business rule) or 3 (action).");
        }
        else if (create)
        {
            errors.Add("A process needs its category: 0 (workflow), 2 (business rule) or 3 (action).");
        }
        if (create && ((target.Attributes.TryGetValue(StateCode, out var state) && OptionValue(state) != WorkflowSchema.DraftState)
                       || (target.Attributes.TryGetValue(StatusCode, out var status) && OptionValue(status) != WorkflowSchema.DraftStatus)))
            errors.Add("A process is created Draft (statecode 0, statuscode 1): activate it afterwards with SetState.");
        if (errors.Count > 0)
            throw XrmdFaultException.InvalidArgument($"{message} of '{WorkflowSchema.Table}' is refused: {string.Join(" ", errors)}");
    }

    private static string Describe(object? value) => (value is null ? null : OptionValue(value))?.ToString() ?? "(none)";

    private static XrmdFaultException ProcessTriggerRefused(string message) =>
        XrmdFaultException.InvalidArgument(
            $"{message} of '{WorkflowSchema.TriggerTable}' is not supported: its rows are written by solution import only, with their " +
            "process (a business rule of Entity scope gives it with processtriggerscope on the workflow row).");

    /// <summary>
    /// Plan 2.14, ruling W4: an activated process is not deleted. The Delete of a <c>workflow</c> row is refused while the definition
    /// it belongs to (the row itself, or the parent of an activation) is Activated; deleting a Draft definition deletes its
    /// activations with it (<c>workflow_parent_workflow</c>, Cascade). A missing row is left to the core operation's 404.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static async Task DemandProcessNotActivatedAsync(Guid id, DataSession session, CancellationToken ct)
    {
        var table = $"{session.DataTable(WorkflowSchema.Table)}";
        await using var cmd = session.Command(
            $"SELECT COALESCE(p.{session.Quote(StateCode)}, w.{session.Quote(StateCode)}) FROM {table} w{session.Dialect.LockHint(RowLock.Update)} " +
            $"LEFT JOIN {table} p ON w.{session.Quote(WorkflowSchema.TypeColumn)} = {(int)WorkflowSchema.Type.Activation} " +
            $"AND p.{session.Quote(WorkflowSchema.WorkflowId)} = w.{session.Quote(WorkflowSchema.ParentWorkflowId)} " +
            $"WHERE w.{session.Quote(WorkflowSchema.WorkflowId)} = {IdParameter}{session.Dialect.LockClause(RowLock.Update, "w")}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        if (await cmd.ExecuteScalarAsync(ct) is int state && state == WorkflowSchema.ActivatedState)
            throw XrmdFaultException.InvalidArgument($"The process {id} is activated: deactivate it before deleting it.");
    }

    /// <summary>
    /// The rows of <c>activitypointer</c> and <c>activityparty</c> are kept by the Core in the transaction of each write of an
    /// activity (plan 2.11, rulings A1 and A10): Create, Update (SetState included), Delete, Assign, GrantAccess and ModifyAccess of
    /// these tables, and the lookup Associate/Disassociate that is an Update of one of their rows, are refused for every caller
    /// (a share recorded on the pointer would be honoured by the pointer's read predicate, pre-flight T3 row).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static void DemandNotActivityStore(string message, string? entity)
    {
        if (entity is ActivitySchema.Pointer or ActivitySchema.Party)
            throw XrmdFaultException.InvalidArgument(
                $"{message} is not supported on '{entity}': its rows are kept by the server with each activity " +
                "(write, assign, change the state of or delete the activity itself).");
    }

    /// <summary>
    /// The rows of <c>appmodulecomponent</c> and <c>appmoduleroles</c> are written by the solution import only (plan 3.5 UI-1a,
    /// ruling R2): a Create, Update, Delete or Assign of either by any caller but <see cref="Caller.Internal"/> is refused. Dataverse
    /// has no such message; this is the one wording the plan gives (<see cref="AppModuleSchema.ImportOnly"/>). The same holds for
    /// <c>entitymap</c> and <c>attributemap</c> (#143, <see cref="AttributeMapSchema.ImportOnly"/>), which Dataverse lets a client write,
    /// and for <c>customcontrol</c> (plan 3.5 UI-1d, ruling R1, <see cref="CustomControlSchema.ImportOnly"/>).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static void DemandNotAppModuleStore(string? entity)
    {
        if (entity is AppModuleSchema.Component or AppModuleSchema.RolesIntersect)
            throw XrmdFaultException.InvalidArgument(AppModuleSchema.ImportOnly);
        // #143: the table and column maps come from solution import too.
        if (AttributeMapSchema.IsMapTable(entity))
            throw XrmdFaultException.InvalidArgument(AttributeMapSchema.ImportOnly);
        // Plan 3.5 UI-1d (ruling R1): the custom controls come from solution import too (a known limit: Dataverse lets a client write them).
        if (entity == CustomControlSchema.Table)
            throw XrmdFaultException.InvalidArgument(CustomControlSchema.ImportOnly);
    }

    /// <summary>
    /// The rows of <c>fileattachment</c> are written by the file and image messages and column values, never by a Create, Update or
    /// Delete of the table (the internal data path of the file service is not a request).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static void DemandNotFileAttachment(string message, string? entity)
    {
        if (entity == FileColumns.AttachmentEntity)
            throw XrmdFaultException.InvalidArgument(
                $"{message} is not supported on '{FileColumns.AttachmentEntity}': its rows are written by the file and image column messages (upload, delete, and image values).");
    }

    /// <summary>
    /// The writes the post tables refuse (plan 3.5 UI-1g, Global Constraints), before any privilege check, so a caller without a
    /// Write privilege (there is none on these tables) gets the 400 too: an Update of a <c>post</c> or <c>postcomment</c>
    /// (Associate and Disassociate over their lookups included, as Updates); a Create of a post with an auto <c>source</c> (1 or 3);
    /// a Create, Update or Delete of <c>postregarding</c> by any caller but <see cref="Caller.Internal"/> (the Core keeps those rows,
    /// and deletes them with their record). The Core checks Create and Update again on the row it writes (<c>PostRules</c>), after
    /// the plug-ins.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static void DemandPostWrite(string message, Entity target, Caller caller)
    {
        switch (target.LogicalName)
        {
            case PostSchema.Regarding when caller != Caller.Internal && message is "Create" or "Update" or "Delete" or "Associate" or "Disassociate":
                throw XrmdFaultException.InvalidArgument(PostSchema.ServerMaintained);
            case PostSchema.Post or PostSchema.Comment when message is "Update" or "Associate" or "Disassociate":
                throw XrmdFaultException.InvalidArgument(PostSchema.NotUpdatable);
            case PostSchema.Post when message == "Create" && target.GetAttributeValue<OptionSetValue>(PostSchema.Source) is { } source
                                      && PostSchema.IsAutoSource(source.Value):
                throw XrmdFaultException.InvalidArgument(PostSchema.AutoPostsRefused);
        }
    }

    /// <summary>
    /// The rows of <c>duplicaterecord</c> are written by the bulk duplicate detection job only (plan 2.15, ruling D15) and go with
    /// the Delete of their job (<c>AsyncOperation_DuplicateBaseRecord</c>, Cascade): a Create, Update, Delete or Assign of the table by any
    /// caller but <see cref="Caller.Internal"/> is refused.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static void DemandNotDuplicateRecord(string message, string? entity, Caller caller)
    {
        if (entity == DuplicateDetectionSchema.Record && caller != Caller.Internal)
            throw XrmdFaultException.InvalidArgument(
                $"{message} is not supported on '{DuplicateDetectionSchema.Record}': its rows are written by the BulkDetectDuplicates system job and deleted with it.");
    }

    /// <summary>
    /// The audit settings of the <c>organization</c> row that xrmverse cannot honour fail explicitly: user access auditing
    /// (<c>isuseraccessauditenabled</c> true), a retention period outside -1 and 1..365000 days, and <c>auditsettings</c> other
    /// than null or <c>{"StoreLabelNameforPicklistAudits":false}</c> (only option values are stored). Also the change tracking
    /// retention <c>expirechangetrackingindays</c>: 0 to 365 days.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static void DemandValidAuditSettings(Entity target)
    {
        if (target.LogicalName != Organization) return;
        if (target.Attributes.TryGetValue(IsUserAccessAuditEnabled, out var access) && access is true)
            throw XrmdFaultException.InvalidArgument(UserAccessAuditingUnsupported);
        if (target.Attributes.TryGetValue(AuditRetentionPeriod, out var retention)
            && !(retention is int or long && Convert.ToInt64(retention) is -1 or (>= 1 and <= MaxRetentionDays)))
            throw XrmdFaultException.InvalidArgument(RetentionOutOfRange);
        if (target.Attributes.TryGetValue(AuditSettings, out var settings) && settings is not null && !IsDefaultAuditSettings(settings))
            throw XrmdFaultException.InvalidArgument(AuditSettingsUnsupported);
        // The change tracking retention (plan 2.13, ruling C1): 0 to 365 days, never empty (SystemRequired).
        if (target.Attributes.TryGetValue(ChangeTrackingExpiry, out var expiry)
            && !(expiry is int or long && Convert.ToInt64(expiry) is >= 0 and <= ChangeTrackingSchema.MaxExpireDays))
            throw XrmdFaultException.InvalidArgument(ChangeTrackingExpiryOutOfRange);
    }

    /// <summary>True for a JSON object whose only member is <c>StoreLabelNameforPicklistAudits</c> set to false.</summary>
    private static bool IsDefaultAuditSettings(object value)
    {
        if (value is not string text) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            var members = root.EnumerateObject().ToList();
            return members.Count == 1 && members[0].Name == StoreLabelNames && members[0].Value.ValueKind == System.Text.Json.JsonValueKind.False;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The values of a field permission: <c>cancreate</c>, <c>canread</c> and <c>canupdate</c> are 0 (Not Allowed) or 4
    /// (Allowed); <c>canreadunmasked</c> is 0, since masking is not supported. A null value is left as it is (read as 0).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the column.</exception>
    private static void DemandValidPermission(Entity target)
    {
        foreach (var column in PermissionColumns)
            if (target.Attributes.TryGetValue(column, out var value) && value is not null
                && OptionValue(value) is not (FieldSecurityTables.NotAllowed or FieldSecurityTables.Allowed))
                throw XrmdFaultException.InvalidArgument(
                    $"The {column} column of {FieldSecurityTables.Permission} accepts only {FieldSecurityTables.NotAllowed} (not allowed) or {FieldSecurityTables.Allowed} (allowed).");
        if (target.Attributes.TryGetValue(FieldSecurityTables.CanReadUnmasked, out var unmasked) && unmasked is not null
            && OptionValue(unmasked) != FieldSecurityTables.NotAllowed)
            throw XrmdFaultException.InvalidArgument(
                $"The {FieldSecurityTables.CanReadUnmasked} column of {FieldSecurityTables.Permission} must be 0: masking is not supported by xrmverse.");
    }

    /// <summary>
    /// A <c>fieldpermission</c> names an existing secured column (final review I2): a table or column that does not exist is a
    /// 404, a column that is not secured a 400 <c>0x8004F50E</c>. Without this, a permission staged on a column that is not
    /// secured yet, or left from a deleted column of the same name, would start granting the moment the column is secured.
    /// </summary>
    private static void DemandSecuredColumn(IMetadataStore store, string? entity, string? attribute)
    {
        if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(attribute))
            throw XrmdFaultException.InvalidArgument(
                $"A {FieldSecurityTables.Permission} row needs both {FieldSecurityTables.EntityName} and {FieldSecurityTables.AttributeLogicalName}.");
        var column = store.GetEntity(entity)?.Attributes
            .FirstOrDefault(a => string.Equals(a.LogicalName, attribute, StringComparison.OrdinalIgnoreCase));
        if (column is null)
            throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist,
                $"The column {attribute} of {entity} named by the field permission does not exist.", 404);
        if (column.IsSecured != true) throw XrmdFaultException.PermissionOnUnsecuredColumn(entity, attribute);
    }

    /// <summary>The stored table and column of a <c>fieldpermission</c> (nulls when it does not exist).</summary>
    private static async Task<(string? Entity, string? Attribute)> StoredPermissionColumnAsync(Guid permissionId, DataSession session,
        CancellationToken ct)
    {
        await using var cmd = session.Command(
            $"SELECT {session.Quote(FieldSecurityTables.EntityName)}, {session.Quote(FieldSecurityTables.AttributeLogicalName)} " +
            $"FROM {session.DataTable(FieldSecurityTables.Permission)} " +
            $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(FieldSecurityTables.Permission))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, permissionId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return (null, null);
        return (rd.IsDBNull(0) ? null : rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1));
    }

    /// <summary>The permission columns limited to Not Allowed and Allowed.</summary>
    private static readonly string[] PermissionColumns =
        [FieldSecurityTables.CanCreate, FieldSecurityTables.CanRead, FieldSecurityTables.CanUpdate];

    /// <summary>The value of a choice column (an <see cref="OptionSetValue"/> or a number); null for anything else.</summary>
    private static int? OptionValue(object value) => value switch
    {
        OptionSetValue option => option.Value,
        int number => number,
        _ => null,
    };

    /// <summary>True for a reference (or id) of the System Administrator field security profile.</summary>
    private static bool IsAdministratorProfile(object? value) => value switch
    {
        EntityReference reference => reference.Id == FieldSecurityTables.SystemAdministratorProfileId,
        Guid id => id == FieldSecurityTables.SystemAdministratorProfileId,
        _ => false,
    };

    /// <summary>The stored profile of a field permission (read with UPDLOCK); null when the row or its profile is missing.</summary>
    private static async Task<Guid?> StoredPermissionProfileAsync(Guid permissionId, DataSession session, CancellationToken ct)
    {
        if (permissionId == Guid.Empty) return null;
        await using var cmd = session.Command(
            $"SELECT {session.Quote(FieldSecurityTables.ProfileId)} FROM {session.DataTable(FieldSecurityTables.Permission)}{session.Dialect.LockHint(RowLock.Update)} " +
            $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(FieldSecurityTables.Permission))} = {IdParameter}{session.Dialect.LockClause(RowLock.Update)}");
        cmd.AddParameter(IdParameter, DbType.Guid, permissionId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <exception cref="XrmdFaultException">403 PrivilegeDenied for the SYSTEM user's row.</exception>
    private static void DemandNotSystemUser(Guid id, Guid systemUserId)
    {
        if (systemUserId != Guid.Empty && id == systemUserId) throw XrmdFaultException.SystemUserCannotBeModified();
    }

    /// <summary>
    /// #131 (ruling Z4): a <c>usersettings</c> row's <c>timezonecode</c>, when given and not null, is a code of
    /// <see cref="TimeZoneCatalog"/>, so that every client can convert the user's dates.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the code.</exception>
    private static void DemandKnownTimeZone(Entity target)
    {
        if (target.LogicalName != CurrencySchema.UserSettings || !target.Attributes.TryGetValue(TimeZoneSchema.Code, out var value)) return;
        if (value is int code && !TimeZoneCatalog.Contains(code))
            throw XrmdFaultException.InvalidArgument(
                $"usersettings.{TimeZoneSchema.Code} {code} is not a time zone code: read the codes from timezonedefinitions.");
    }

    /// <summary>A role carrying <c>roletemplateid</c> (any value, null included) is refused: the template makes a System Administrator.</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static void DemandNoRoleTemplate(Entity target)
    {
        if (target.LogicalName == Role && target.Attributes.ContainsKey(RoleTemplateId))
            throw XrmdFaultException.InvalidArgument($"The attribute {RoleTemplateId} of {Role} is maintained by the system and cannot be set.");
    }

    /// <summary>
    /// The <c>ismanaged</c> flag (<see cref="IsManaged"/>) is set by no request (any value, null included). Of a row component,
    /// only <see cref="Caller.Internal"/> (the layer operations) writes it. Of a <c>solution</c> row, only the solution import
    /// writes it, inside <see cref="LayerOperationScope"/> (final review I2): a request that could make a row managed could
    /// forge a managed solution, or the holding solution of one that a DeleteAndPromote then applies.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the column.</exception>
    private static void DemandNoIsManaged(Entity target, Caller caller)
    {
        if (caller == Caller.Internal || !target.Attributes.ContainsKey(IsManaged)) return;
        if (target.LogicalName != Solution)
            throw XrmdFaultException.InvalidArgument(
                $"The attribute {IsManaged} of {target.LogicalName} is maintained by the solution layers and cannot be set.");
        if (!LayerOperationScope.IsActive)
            throw XrmdFaultException.InvalidArgument(
                $"The attribute {IsManaged} of {Solution} is maintained by the solution import and cannot be set: a managed solution is installed by importing its managed file.");
    }

    /// <summary>True for an import-only column of <paramref name="table"/> (see <see cref="ImportOnly"/>).</summary>
    public static bool IsImportOnly(string? table, string column) =>
        ImportOnly.Any(c => (c.Table is null || c.Table == table) && c.Column == column);

    /// <summary>
    /// A row carrying an import-only column (any value, null included) is refused: it holds what a solution file gave the
    /// component, which the export writes again; only the solution import writes it (with its own SQL).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the column.</exception>
    private static void DemandNoImportOnlyColumn(Entity target)
    {
        if (target.Attributes.Keys.FirstOrDefault(name => IsImportOnly(target.LogicalName, name)) is { } column)
            throw XrmdFaultException.InvalidArgument(
                $"The attribute {column} of {target.LogicalName} is maintained by the solution import and cannot be set.");
    }

    /// <summary>
    /// An Update of <c>asyncoperation</c> is a cancellation: <c>statecode</c> 3 and <c>statuscode</c> 32, no other column
    /// the core would write, and the job in state 0 or 1 (read with UPDLOCK). A missing job is left to the core's 404.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument otherwise.</exception>
    private static async Task DemandCancellationAsync(Entity target, Guid id, DataSession session, CancellationToken ct)
    {
        foreach (var name in target.Attributes.Keys)
            if (name is not (StateCode or StatusCode) && (name == OwnerId || !SystemAttributes.IsReservedName(AsyncOperation, name)))
                throw XrmdFaultException.InvalidArgument(OnlyCancellation);
        if (ValueOf(target, StateCode) is not OptionSetValue { Value: Completed }
            || ValueOf(target, StatusCode) is not OptionSetValue { Value: Canceled })
            throw XrmdFaultException.InvalidArgument(OnlyCancellation);
        if (id != Guid.Empty && await JobStateAsync(id, session, ct) is { } state && state is not (0 or 1))
            throw XrmdFaultException.InvalidArgument(OnlyCancellation);
    }

    private const string SdkMessage = "sdkmessage";
    private const string SdkMessageFilter = "sdkmessagefilter";
    private const string CategoryName = "categoryname";
    private const string MessageNameParameter = "@_name";

    /// <summary>The refusal of a direct write to an API's message or filter (plan 15 C10, plan 3.1 R1).</summary>
    private static XrmdFaultException CustomApiWrite(string message, string table) =>
        XrmdFaultException.InvalidArgument(
            $"{message} of '{table}' is not supported: the message and filter of a custom API are maintained by the server; write the customapi row instead.");

    /// <summary>
    /// Plan 15, C10, as plan 3.1 (ruling R1) leaves it: the <c>sdkmessage</c> and <c>sdkmessagefilter</c> rows of an API are
    /// written by the server only (the solution import, the authoring of a <c>customapi</c> and the cascade delete, as
    /// <see cref="Caller.Internal"/>, the only caller this rule spares). The custom API tables themselves are written by any
    /// caller with the privileges (their rules are the pipeline's <c>CustomApiAuthoring</c>). An API's message is a row a
    /// <c>customapi</c> points to or whose <c>categoryname</c> is <c>CustomAPI</c>; a created or updated message is also one
    /// when it says so or takes the name of an API. A filter is an API's when its stored or new message is. Custom actions (any
    /// other message) are not concerned.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    private static async Task DemandNotCustomApiAsync(string message, string? table, Guid id, Entity? target, Caller caller,
        DataSession session, CancellationToken ct)
    {
        if (table is null || caller == Caller.Internal) return;
        switch (table)
        {
            case SdkMessage:
                if (target is not null && ValueOf(target, CategoryName) is string category
                    && string.Equals(category, CustomApiTables.MessageCategory, StringComparison.OrdinalIgnoreCase))
                    throw CustomApiWrite(message, table);
                // The new name on Create and Update: steps match messages by name, so a message renamed to an API's name
                // would take over the API's message.
                var name = message is "Create" or "Update" && target is not null ? ValueOf(target, "name") as string : null;
                if ((id != Guid.Empty || name is not null) && await IsApiMessageAsync(id, name, session, ct))
                    throw CustomApiWrite(message, table);
                break;
            case SdkMessageFilter:
                if (target is not null && ValueOf(target, CustomApiTables.SdkMessageId) is { } newMessage
                    && IdOf(newMessage) is { } newId && await IsApiMessageAsync(newId, null, session, ct))
                    throw CustomApiWrite(message, table);
                if (message != "Create" && id != Guid.Empty && await StoredFilterMessageAsync(id, session, ct) is { } storedId
                    && await IsApiMessageAsync(storedId, null, session, ct))
                    throw CustomApiWrite(message, table);
                break;
        }
    }

    /// <summary>
    /// Plan 3.1, the checks of an authored custom API row that must run here, before the generic lookup checks of the security
    /// guard (whose missing record would be a 404). The pattern: a lookup that must exist, or a lookup that must not be given
    /// or changed, is checked here; every other rule of an authored API, parameter or property is the pipeline's
    /// <c>CustomApiAuthoring</c> (stage 20), which repeats these checks as a second line. <see cref="Caller.Internal"/> is spared.
    /// <list type="bullet">
    /// <item><c>customapi</c>: <c>sdkmessageid</c> on a Create (any value: the server creates the API's message and sets it),
    /// or on an Update with a value other than the stored one (immutable, whether or not the message exists); a
    /// <c>plugintypeid</c> naming no plug-in type, on a Create or an Update.</item>
    /// <item>A request parameter or response property: on a Create, a <c>customapiid</c> naming no API; on an Update, a
    /// <c>customapiid</c> other than the stored one (immutable, whether or not that API exists). An Associate or Disassociate over the API's relationship is that Update:
    /// it is always refused (ruling R-B), even to the same API.</item>
    /// </list>
    /// </summary>
    /// <param name="message">The request's message: Create, Update, Associate or Disassociate.</param>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the API (and the parameter or property).</exception>
    private static async Task DemandCustomApiLookupsAsync(string message, Entity target, bool isCreate, Caller caller,
        DataSession session, CancellationToken ct)
    {
        if (caller == Caller.Internal) return;
        if (target.LogicalName is CustomApiTables.RequestParameter or CustomApiTables.ResponseProperty)
        {
            await DemandCustomApiFieldLookupsAsync(message, target, isCreate, session, ct);
            return;
        }
        if (target.LogicalName != CustomApiTables.Api) return;
        var sdkMessageGiven = target.Attributes.ContainsKey(CustomApiTables.SdkMessageId);
        var type = ValueOf(target, CustomApiTables.PluginTypeId) is { } t ? IdOf(t) : null;
        if (!sdkMessageGiven && type is null) return;

        await using var cmd = session.Command($"""
            SELECT (SELECT uniquename FROM {session.DataTable(CustomApiTables.Api)} WHERE customapiid = {IdParameter}),
                CASE WHEN EXISTS (SELECT 1 FROM {session.DataTable("plugintype")} WHERE plugintypeid = @_type) THEN 1 ELSE 0 END,
                (SELECT {session.Quote(CustomApiTables.SdkMessageId)} FROM {session.DataTable(CustomApiTables.Api)} WHERE customapiid = {IdParameter})
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, RecordId(target));
        cmd.AddParameter("@_type", DbType.Guid, (object?)type ?? DBNull.Value);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        var stored = !rd.IsDBNull(0);
        var name = isCreate ? ValueOf(target, CustomApiTables.UniqueName) as string : stored ? rd.GetString(0) : null;
        var refused = $"Custom API '{name ?? RecordId(target).ToString("D")}' cannot be {(isCreate ? "created" : "updated")}";
        if (sdkMessageGiven && isCreate)
            throw XrmdFaultException.InvalidArgument(
                $"{refused}: sdkmessageid is set by the server, which creates the API's message; leave it out.");
        // An Update of a missing API is left to the core's 404.
        if (sdkMessageGiven && stored && IdOrNull(ValueOf(target, CustomApiTables.SdkMessageId)) != (rd.IsDBNull(2) ? null : rd.GetGuid(2)))
            throw XrmdFaultException.InvalidArgument(Immutable(CustomApiTables.SdkMessageId, $"'{name}'"));
        if (type is not null && rd.GetInt32(1) == 0)
            throw XrmdFaultException.InvalidArgument($"{refused}: its plug-in type '{type}' (plugintypeid) does not exist.");
    }

    /// <summary>The <see cref="DemandCustomApiLookupsAsync"/> checks of a request parameter or response property.</summary>
    private static async Task DemandCustomApiFieldLookupsAsync(string message, Entity target, bool isCreate, DataSession session,
        CancellationToken ct)
    {
        var table = target.LogicalName;
        var what = table == CustomApiTables.RequestParameter ? "request parameter" : "response property";
        if (!target.Attributes.TryGetValue(CustomApiTables.ApiId, out var givenApi)) return;
        var newApi = IdOrNull(givenApi);
        var fieldId = isCreate ? Guid.Empty : RecordId(target);

        // The API given (Create) or the stored one (Update), with the stored field.
        await using var cmd = session.Command($"""
            SELECT f.uniquename, f.{CustomApiTables.ApiId}, a.uniquename,
                (SELECT uniquename FROM {session.DataTable(CustomApiTables.Api)} WHERE customapiid = @_api)
            FROM (SELECT 1 AS one) AS x
            LEFT JOIN {session.DataTable(table)} AS f ON f.{session.Quote(SystemAttributes.PrimaryIdName(table))} = {IdParameter}
            LEFT JOIN {session.DataTable(CustomApiTables.Api)} AS a ON a.customapiid = f.{CustomApiTables.ApiId}
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, fieldId);
        cmd.AddParameter("@_api", DbType.Guid, (object?)newApi ?? DBNull.Value);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        var givenApiName = rd.IsDBNull(3) ? null : rd.GetString(3);
        if (isCreate)
        {
            var field = ValueOf(target, CustomApiTables.UniqueName) as string;
            if (newApi is { } apiId && givenApiName is null)
                throw XrmdFaultException.InvalidArgument(
                    $"Custom API {what} '{field}' cannot be created: its custom API '{apiId}' (customapiid) does not exist.");
            return;
        }
        // An Update of a missing row is left to the core's 404.
        if (rd.IsDBNull(0)) return;
        var storedApi = rd.IsDBNull(1) ? (Guid?)null : rd.GetGuid(1);
        if (message != "Update" || newApi != storedApi)
            throw XrmdFaultException.InvalidArgument(
                Immutable(CustomApiTables.ApiId, $"'{(rd.IsDBNull(2) ? storedApi?.ToString("D") : rd.GetString(2))}' {what} '{rd.GetString(0)}'"));
    }

    /// <summary>The refusal of a change to an immutable column of a custom API (<paramref name="subject"/>: <c>'api'</c>, or
    /// <c>'api' request parameter 'p'</c>), worded as the pipeline's <c>CustomApiAuthoring</c> words it.</summary>
    private static string Immutable(string column, string subject) => $"{column} of custom API {subject} cannot be changed once created.";

    private static Guid? IdOrNull(object? value) => value is null ? null : IdOf(value);

    /// <summary>
    /// Plan 15, C5: a <c>plugintype</c> a custom API runs as its main operation, and the <c>pluginassembly</c> holding such a
    /// type, cannot be deleted while the API exists: the API would silently lose its main operation.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the API.</exception>
    private static async Task DemandNoCustomApiUsesAsync(EntityReference deleted, IMetadataStore store, DataSession session, CancellationToken ct)
    {
        var table = deleted.LogicalName;
        if (table is not ("plugintype" or "pluginassembly") || store.GetEntity(CustomApiTables.Api) is null) return;
        await using var cmd = session.Command(session.Dialect.Limit(table == "plugintype"
            ? $"SELECT c.uniquename FROM {session.DataTable(CustomApiTables.Api)} AS c WHERE c.{session.Quote(CustomApiTables.PluginTypeId)} = {IdParameter} ORDER BY c.uniquename"
            : $"""
               SELECT c.uniquename FROM {session.DataTable(CustomApiTables.Api)} AS c
               JOIN {session.DataTable("plugintype")} AS t ON t.plugintypeid = c.{session.Quote(CustomApiTables.PluginTypeId)}
               WHERE t.pluginassemblyid = {IdParameter} ORDER BY c.uniquename
               """, "1"));
        cmd.AddParameter(IdParameter, DbType.Guid, deleted.Id);
        if (await cmd.ExecuteScalarAsync(ct) is string api)
            throw XrmdFaultException.InvalidArgument(table == "plugintype"
                ? $"The plug-in type {deleted.Id} cannot be deleted: custom API '{api}' runs it as its main operation."
                : $"The plug-in assembly {deleted.Id} cannot be deleted: custom API '{api}' runs one of its plug-in types as its main operation.");
    }

    private static Guid? IdOf(object value) => value switch
    {
        EntityReference reference => reference.Id,
        Guid id => id,
        _ => null,
    };

    /// <summary>
    /// True when the message <paramref name="id"/> is an API's (a <c>customapi</c> points to it, or its <c>categoryname</c> is
    /// <c>CustomAPI</c>), or when an API has the unique name <paramref name="name"/>.
    /// </summary>
    private static async Task<bool> IsApiMessageAsync(Guid id, string? name, DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command($"""
            SELECT CASE WHEN EXISTS (SELECT 1 FROM {session.DataTable(CustomApiTables.Api)}
                    WHERE {session.Quote(CustomApiTables.SdkMessageId)} = {IdParameter} OR {session.Quote(CustomApiTables.UniqueName)} = {MessageNameParameter})
                OR EXISTS (SELECT 1 FROM {session.DataTable(SdkMessage)}
                    WHERE {session.Quote(SystemAttributes.PrimaryIdName(SdkMessage))} = {IdParameter} AND {session.Quote(CategoryName)} = @_category)
            THEN 1 ELSE 0 END
            """);
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        cmd.AddParameter(MessageNameParameter, DbType.String, (object?)name ?? DBNull.Value, 256);
        cmd.AddParameter("@_category", DbType.String, CustomApiTables.MessageCategory, 256);
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }

    /// <summary>The stored <c>sdkmessageid</c> of the filter; null when it has none or does not exist.</summary>
    private static async Task<Guid?> StoredFilterMessageAsync(Guid filterId, DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command(
            $"SELECT {session.Quote(CustomApiTables.SdkMessageId)} FROM {session.DataTable(SdkMessageFilter)} " +
            $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(SdkMessageFilter))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, filterId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>The value of the attribute in <paramref name="target"/>; null when absent.</summary>
    private static object? ValueOf(Entity target, string attribute) =>
        target.Attributes.TryGetValue(attribute, out var value) ? value : null;

    /// <summary>
    /// The template of a standard role: its id when it is a template id, else its stored <c>roletemplateid</c> (only the
    /// bootstrap sets one); null for any other role, or a missing one (left to the core's 404).
    /// </summary>
    private static async Task<Guid?> RoleTemplateAsync(Guid roleId, DataSession session, CancellationToken ct)
    {
        if (StandardRoleIds.Contains(roleId)) return roleId;
        if (roleId == Guid.Empty) return null;
        await using var cmd = session.Command(
            $"SELECT {session.Quote(RoleTemplateId)} FROM {session.DataTable(Role)} WHERE {session.Quote(SystemAttributes.PrimaryIdName(Role))} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, roleId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>The <c>statecode</c> of the job (read with UPDLOCK); null when it does not exist.</summary>
    private static async Task<int?> JobStateAsync(Guid id, DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command(
            $"SELECT statecode FROM {session.DataTable(AsyncOperation)}{session.Dialect.LockHint(RowLock.Update)} " +
            $"WHERE {session.Quote(SystemAttributes.PrimaryIdName(AsyncOperation))} = {IdParameter}{session.Dialect.LockClause(RowLock.Update)}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) switch
        {
            int state => state,
            null => null,
            _ => -1, // NULL statecode: not a state that can be cancelled or deleted
        };
    }
}
