# Security

[← xrmverse documentation](../README.md#documentation)

## Introduction

Security decides who can see and change which records. xrmverse uses the Dataverse model: tree-shaped **business units** (one root), **users** (`systemuser`) in a BU,
**teams** (owner team `teamtype` 0, access team `teamtype` 1), **roles** with privileges at a **depth**, records owned
by a user or a team (`ownerid`, `owningbusinessunit`), plus per-record **sharing**. Every request (Web API,
SOAP, plugin service, asynchronous jobs) goes through the same checks before the pipeline; reads get an added
predicate in the SQL query (never row filters applied after the query).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Model | Business units, users, owner and access teams, roles with depth, ownership, sharing | Same |
| Where it is enforced | Before the pipeline for every request; reads get a predicate in the SQL query | Same rules for every client (Web API, SOAP, plug-ins, async jobs) |
| Standard roles | System Administrator, System Customizer, Basic User, Delegate | **Differs**: the content of System Customizer and Basic User is xrmverse's choice |
| Roles per business unit | Roles exist only in the root business unit | **Differs**: no per-BU copies |
| Users | Mapped from the token's `oid`, no auto-provisioning; disabled, never deleted | Same principle |
| Field security, hierarchy security, access teams | Supported | Same (see the chapters below and [Field-level security](field-security.md)) |
| Position hierarchy | Not supported (`usepositionhierarchy` true is refused) | **Narrower** |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Understand what a role reaches | Depths and privileges | [The security model](#the-security-model) |
| Give a user a role, or add a team member | `Associate` / `AddPrivilegesRole` | [Administering roles and teams](#administering-roles-and-teams) |
| Add a user | `Create` of `systemuser` plus a role | [Identity and users](#identity-and-users) |
| Act for another user | `MSCRMCallerID`, `CallerObjectId` | [Impersonation](#impersonation) |
| Share one record | `GrantAccess`, `ModifyAccess`, `RevokeAccess` | [Sharing](#sharing) |
| Give access per record to a group of users | Access team templates | [Access teams](#access-teams) |
| Let managers see their reports' records | Hierarchy security | [Hierarchy security](#hierarchy-security) |

## The security model

A role grants privileges at a depth, which decides which records the privilege reaches:

| Depth | `privilegedepthmask` | SDK `PrivilegeDepth` | Records reached |
|---|---|---|---|
| Basic | 1 | 0 | own, own teams', shared with oneself or with one's teams |
| Local | 2 | 1 | of one's own BU |
| Deep | 4 | 2 | of one's own BU and child BUs |
| Global | 8 | 3 | all |

The roles of an owner team apply to its members as if assigned to them; access teams receive only shares (see [Access teams](#access-teams) for those created from a template).
For `systemuser`, `team`, `businessunit` and `role` the depth is evaluated on `owningbusinessunit` and Basic counts as Local.

### Privileges

`prv` + verb + entity `SchemaName` (`prvCreatepb_Project`), verbs `Create`, `Read`, `Write`,
`Delete`, `Append`, `AppendTo`, `Assign`, `Share`; generated for every entity, custom ones too (`privilegeid` =
MD5 of `"xrmd:privilege:" + lowercase name`). Not entity-based: `prvActOnBehalfOfAnotherUser`, the metadata
privileges (`prvCreateEntity`, `prvCreateAttribute` and the others listed in [Solution](solutions.md)), `prvImportCustomization`,
`prvExportCustomization`, `prvPublishCustomization`, `prvWorkflowExecution` (run a workflow on demand),
`prvOverrideCreatedOnCreatedBy` (create a record with its own creation date through `overriddencreatedon`; see
[Record Created On](#record-created-on)).

### Standard roles

In the root BU, `roleid` = template id:

| Role | Template id | Content |
|---|---|---|
| System Administrator | `627090FF-40A3-4053-8790-584EDC5BE201` | every privilege at Global, also for entities created later (recognized by `roletemplateid`, without `roleprivileges` rows) |
| System Customizer | `119F245C-3CC8-4B62-B31C-D1A046CED15D` | plugin and role registration, reads of the system tables, metadata privileges, solution tables, every privilege at Global on the custom API tables and on `workflow` and `processtrigger`, `prvWorkflowExecution` Global, `prvOverrideCreatedOnCreatedBy` Global (top-up `override-createdon-role-privilege`), import/export/publish of customizations |
| Basic User | `236750CD-45AE-4939-AB12-B24B920CED93` | Read Global on `organization`, `businessunit`, `systemuser`, `team`, `role` and the custom API tables (`customapi`, `customapirequestparameter`, `customapiresponseproperty`); Read/Delete Basic on `asyncoperation`; `prvWorkflowExecution` Global; Read Global on `systemform` (as Dataverse's Basic User: top-up `system-form-role-privileges`); Read Global on `webresource` (top-up `web-resource-role-privileges`), so that form libraries and web resources load for every user; nothing on custom tables |
| Delegate (template "Proxy") | `D892CC0B-28C7-4E88-BD92-72F2C366BAED` | only `prvActOnBehalfOfAnotherUser` Global |

The content of System Customizer and Basic User is an xrmverse choice (xrmverse does not have the standard tables of the
real roles). Roles exist only in the root BU and are assigned to users and owner teams of any BU (no per-BU copies).
Startup recognizes the standard roles by `roletemplateid` and seeds their privileges only when it creates the role row:
privileges removed or changed afterwards stay as they are. A privilege that a later version adds to the standard roles
reaches an existing organization through a one-time top-up at startup: the standard roles get the grants they lack,
the top-up is recorded by name in `meta.upgrade_step`, and it never runs again, so a grant removed after it stays
removed. The custom API grants above (System Customizer and Basic User) arrive this way in an organization created
before custom APIs.

## Administering roles and teams

With the SDK and SOAP: `Associate`/`Disassociate` of `systemuserroles_association`, `teamroles_association`,
`teammembership_association`; `AddPrivilegesRole`, `ReplacePrivilegesRole`, `RemovePrivilegeRole`. Whoever assigns a role or
adds privileges must have Write on the principal or on the role, and every privilege of the role at an equal or higher depth
(`RoleUpdateValidationFailure`); System Administrator can be assigned only by a System Administrator. Adding a member to
an owner team is equivalent to assigning them every role of the team (same check).

## Identity and users

### Identity and first administrator

The token is mapped to `systemuser.azureactivedirectoryobjectid` (claim `oid`), without
auto-provisioning (unknown user: `0x80072560`). On first start `Seed:AdminObjectId` creates the user with the
System Administrator role (dev/test path; `xrmd org create` arrives in Plan 6). Every start with `Seed:AdminObjectId`
reassigns the System Administrator role to that user if it was removed: this is a development path, not to be
configured in production. Other users are created with a
`Create` of `systemuser` (`azureactivedirectoryobjectid`, `businessunitid`) and an association to a role. A user with
`isdisabled = 1` receives `0x80040225` from every endpoint; a user without roles (direct or from teams) receives `0x80042F09`
from every endpoint except `/healthz`, so not even `WhoAmI` succeeds. Users cannot be deleted (400): they are disabled.

### Entra identity

`systemuser.azureactivedirectoryobjectid` is unique among non-null values (filtered index; a
duplicate is 412 `0x80040237`). It is set at user `Create`; changing it with an `Update` is reserved to a System
Administrator (otherwise `0x80040220`): whoever has only `prvWriteSystemUser` cannot relink another
user's identity (for example an administrator's) to their own token. SYSTEM and internal writes do not have this limit.

### SYSTEM user

Its `systemuserid` is derived from the organization name (`organization.name`, as in
`SeedOptions.DeriveSystemUserId`): changing `organization.name` would change the expected id, and the existing SYSTEM row would
no longer be recognized as such (nor protected by the rules above). Do not rename the organization.

## Web sign-in and the session cookie

### Web session cookie

So that the browser's own requests (a form library's `<script src>`, an HTML web resource in an
IFrame, a script's `fetch(…, { credentials: "same-origin" })`) are authenticated as they are in Dataverse, the web shell posts
its access token to `POST /api/session` with the bearer header after sign-in, after every silent token renewal and before the
token expires. The server sets the cookie `xrmd.session` to that same token: `HttpOnly`, `SameSite=Strict`, `Path=/`, no
`Domain` (an organization is its host name), `Secure` when the request is HTTPS (after the trusted proxy's forwarded headers),
expiring at the token's `exp`. A token longer than 3900 characters is refused (400): a browser would drop the cookie.
`DELETE /api/session`, also with the bearer header (never the cookie), clears it; at sign-out the shell sends it when it has a
token without user interaction, and otherwise leaves the cookie to lapse at the token's `exp`. The cookie carries
no other state: it is validated on every request exactly as the bearer token (signature, issuer, audience, lifetime), so an
expired token in it is a 401. It is read **only** when the request has no `Authorization` header (a header always wins, even an
invalid one) and only on `/api/data/…`, `/WebResources/…`, `/{…}/WebResources/…` (the cache-busting prefix),
`/Controls/…` (custom control files) and `/ClientGlobalContext.js.aspx`; never on `/admin`, `/mcp`, `/XRMServices`, `/Image` or `/api/session` itself (the cookie cannot
renew itself). **CSRF**: a cookie-authenticated `POST`, `PATCH`, `PUT` or `DELETE` is refused with 403 (`0x80048306`) when its
`Origin` header is present and is not the request's own origin (scheme, host and port; `Origin: null` included), or when its
`Content-Type` is `application/x-www-form-urlencoded`, `multipart/form-data` or `text/plain`, the types an HTML form can send
without a CORS preflight. `SameSite=Strict` already keeps the cookie off cross-site requests; these rules also cover a
same-site but cross-origin page (another port or subdomain) and browsers that ignore `SameSite`. Requests with the bearer header
are not subject to them. The path list is part of this CSRF protection, not a sandbox for web resources (see below).

### One cookie per browser, sign-in per tab

The shell keeps its sign-in per tab (session storage), but `xrmd.session` is one
cookie per host for the whole browser profile, and the last token posted wins. A tab therefore posts its own token again when it
gains focus or becomes visible, if that token differs from the one it last posted or another tab has set (or cleared) the cookie
since. The active tab wins: **signing in as two different users in two tabs of the same browser at the same time is not
supported**. While you work in one tab, the form scripts and web resources of a tab in the background run as the user of the
tab you last used; use separate browser profiles or a private window for a second user. A token counts as posted only when the
server accepted it. A failed post is retried after 2, 5, 15, 30 and 60 seconds, and the shell shows a warning bar with the
server's message until a post succeeds. A refusal (4xx) is not retried: in particular a token longer than 3900 characters (for
example an Entra token with many group claims) gets its own message, and form scripts and web resources do not work for that
user until the token is shorter.

### Web resources are trusted code

Form libraries (a hidden `about:blank` IFrame of the shell) and HTML web resources (an
IFrame of `/WebResources/…`, not sandboxed) run on the shell's own origin, as they do in Dataverse. They can read the tab's
session storage, which holds the access token (MSAL's cache; in development mode the signing key itself), and the shell's
`Xrm`, so they can call every endpoint, `/admin` and `/mcp` included, with the bearer header and the full rights of the user
who opened the form. The cookie's path list and CSRF rules do not change this. Whoever can create or import a web resource or
change a form's libraries (System Customizer, System Administrator, and anyone with `prvImportCustomization` through a solution
import) can run code as every user who opens that form, System Administrators included. Grant those rights, and import
solutions, only from people and sources you trust as you trust code you deploy.

The same holds for **custom control (PCF) bundles**. A control's scripts, served from `/Controls/{name}/…`, are injected into
the shell window itself, not into an IFrame: they run as the signed-in user, can read the session token from session storage
and call the API with the user's rights, exactly as form scripts can. Anyone who can import a solution can therefore put code
on every form that uses one of its controls. Import solutions with custom controls only from publishers you trust.

## Impersonation

HTTP header `MSCRMCallerID` (systemuserid) or `CallerObjectId` (oid; takes precedence over `MSCRMCallerID`),
SOAP header `CallerId` (`ServiceClient.CallerId`; if present it replaces the HTTP header). It requires
`prvActOnBehalfOfAnotherUser` from a directly assigned role (not from a team) or a direct System Administrator, otherwise
`0x8004A110`; `Guid.Empty` = no impersonation; SYSTEM cannot be impersonated. The effective privileges are the intersection
of the impersonated user's and the impersonator's. `createdby`/`modifiedby` are the impersonated user; `createdonbehalfby`/`modifiedonbehalfby` are
not maintained. `IOrganizationServiceFactory.CreateOrganizationService(userId)` in plugins applies the security
of the given user without requiring `prvActOnBehalfOfAnotherUser` at runtime; the check is at registration (a step
with an `impersonatinguserid` different from the caller requires that direct privilege).

## Sharing

`GrantAccess`, `ModifyAccess`, `RevokeAccess` on `principalobjectaccess` (`accessrightsmask`: Read 1,
Write 2, Append 4, AppendTo 16, Create 32, Delete 65536, Share 262144, Assign 524288). Rights the caller does not
have on the record cannot be granted; `ModifyAccess` without an existing share creates it. A share names its record by id
**and** table (`objectid`, `objecttypecode`): records of two tables can have the same id (a `task` created with an account's
id, for example), and `GrantAccess`, `ModifyAccess`, `RevokeAccess` and the Delete of a record read and change only the
shares of that table's record, never those of the other. The unique index of `principalobjectaccess` is
(`objectid`, `objecttypecode`, `principalid`); an existing database gets it from migration `0011_share_object_type.sql`,
which replaces the earlier (`objectid`, `principalid`) index (plan 2.11, final review C2).

## System tables and metadata

### System tables

These rules also apply to SYSTEM and System Administrator: `asyncoperation` cannot be created, is updated
only to cancel a waiting job (`3/32`) and is deleted only if `statecode` is 3; `plugintracelog` only Read and Delete;
`principalobjectaccess`, `privilege` and the intersect tables (`systemuserroles`, `teamroles`, `teammembership`,
`roleprivileges`, `privilegeobjecttypecodes`) cannot be written directly; the SYSTEM row of `systemuser` cannot be modified.
The four standard roles cannot be deleted (`0x80040220`, "Standard roles cannot be deleted."); of the System
Administrator role only `name` and `description` can be changed (anything else: `0x80040220`); a new role cannot use a reserved
template id as its `roleid` (400).

### Metadata

`POST EntityDefinitions` requires `prvCreateEntity`, `POST EntityDefinitions(...)/Attributes`
`prvCreateAttribute` (System Customizer and System Administrator have them; otherwise `0x80040220`). The properties
assigned by the server (`MetadataId`, `ObjectTypeCode`, `IsIntersect`, `IsManaged`, `IsCustomEntity`, `IsLogicalEntity`,
`IsPrivate`, `IntroducedVersion`; on attributes `IsManaged`, `IsCustomAttribute`, `IsLogical`, `AttributeOf`,
`IntroducedVersion`) are ignored if they come from the client. `SchemaName` (from which the privilege names derive) is unique
among entities, case-insensitively: a duplicate is 400.

### Record Created On

A Create that gives `overriddencreatedon` a value (a data import keeping the original creation date) needs
`prvOverrideCreatedOnCreatedBy`, besides Create on the table. Without it the Create is refused with PrivilegeDenied
(`0x80040220`, HTTP 403), whose message names `prvOverrideCreatedOnCreatedBy`. System Administrator holds it by its
template; System Customizer gets it at Global, also on an existing organization (top-up
`override-createdon-role-privilege`, run once at startup); Basic User does not. The check runs on the caller's request,
before the plug-ins: a value that a plug-in of stage 10 or 20 adds is not checked. The privilege name is the one Learn gives
for data import ("Override Created on or Created by for Records during Data Import"); its id is derived like every
xrmverse privilege id. What the column does is in [Migration](migration.md#record-created-on-overriddencreatedon).

## Error codes

Web API: HTTP 403 unless indicated; SOAP: a fault with the same `ErrorCode`.

| Code | Name | When |
|---|---|---|
| `0x80040220` | PrivilegeDenied | the `prv…` privilege is missing at the required depth; write to a system table |
| `0x80048306` | AccessDenied | privilege present but record outside depth and not shared |
| `0x80040231` | PrincipalPrivilegeDenied | the new owner (Assign) or the share recipient does not have `prvRead` on the entity |
| `0x80042F09` | UserNotAssignedRoles | user without roles |
| `0x8004A110` | CannotActOnBehalfOfAnotherUser | impersonation without `prvActOnBehalfOfAnotherUser` |
| `0x80048D3C` | RoleUpdateValidationFailure | assigning a role or privileges the caller does not have |
| `0x80090903` | InvalidPrivilege (HTTP 400) | nonexistent privilege in `AddPrivilegesRole` and the like |
| `0x80040225` | UserDisabled | disabled user |
| `0x80040333` | DuplicateRecordsFound (HTTP 412, not 403) | a Create or Update that asked for the check (`SuppressDuplicateDetection` false) found a row a published duplicate detection rule matches; it is not a privilege error and is listed here for completeness (see [Duplicate detection](duplicate-detection.md)) |

### Not verified on Microsoft Learn

The UserDisabled code `0x80040225`, the 403 status code by code (it is the known
Dataverse behavior) and the object type codes of the intersects (`principalobjectaccess` 11, `roleprivileges` 12,
`systemuserroles` 15, `teammembership` 23, `privilegeobjecttypecodes` 31, `teamroles` 40).

## Duplicate detection

See [Duplicate detection](duplicate-detection.md). `duplicaterule`, `duplicaterulecondition` and
`duplicaterecord` get the usual generated privileges (`prvCreateDuplicateRule`, `prvReadDuplicateRule`, …). System
Administrator holds all of them; System Customizer holds every privilege on `duplicaterule` and `duplicaterulecondition` and
Read on `duplicaterecord`, at Global depth (an organization seeded by an earlier version gets these grants once at startup).
Creating, updating or deleting a condition also needs **Write on its rule**. `PublishDuplicateRule` and
`UnpublishDuplicateRule` need Write on the rule; whether Dataverse has a separate publish privilege is not verified. A lookup
(`RetrieveDuplicates`) reads the matches as the caller, so record security and field security apply to what it returns, and it
needs Read on the base table and on the given row; the Create/Update check counts matching rows whatever the caller can read.
The state of a rule (`statecode`, `statuscode`) changes only through the two messages; a write that sets it is a 400 for every
caller but the server itself.

## Activities and notes

The activity tables share one privilege set instead of one per table (see [Activities and notes](activities.md#security)):

- **Privileges.** `email`, `task`, `phonecall`, `appointment`, every custom activity and `activitypointer` use
  `prvCreateActivity`, `prvReadActivity`, `prvWriteActivity`, `prvDeleteActivity`, `prvAppendActivity`,
  `prvAppendToActivity`, `prvAssignActivity` and `prvShareActivity`; `annotation` uses the same eight verbs with `Note`
  (`prvReadNote`, …). The privilege rows belong to `activitypointer` and `annotation`, so deleting a custom activity keeps
  them and every role's grants. There is no `prvReadEmail` and no privilege for `activityparty`. Per-table activity
  privileges are a possible later improvement (#66).
- **Basic User** holds all sixteen at Basic depth (Assign and Share included; the depths are from memory). An organization
  created earlier gets them once at startup (top-up `activity-role-privileges`).
- **`activitypointer`.** A pointer row carries its activity's owner columns, so the usual depth, business unit and hierarchy
  rules apply with `prvReadActivity`. Shares and access teams are recorded on the activity, with its own table's type code;
  the pointer's read filter matches them by id and by the row's `activitytypecode`, so a share of an email opens that email's
  pointer row, and a share of another table's record that happens to have the same id opens nothing. A table left out of
  hierarchy security is left out through the pointer as well.
- **`activityparty`.** A party row is readable when the pointer row of its activity is; there is no privilege to check. A
  link from `activityparty` never opens the rows of activities the caller cannot read.
- **Writing parties.** Naming a record in a party list needs Read on it: no Read privilege on its table is 403
  `PrivilegeDenied`, a record out of reach is 403 `AccessDenied`, a missing record is 404, and nothing is written. Append and
  AppendTo on the party record are not needed. The sender that `SendEmail` fills in is the caller's own user and is not
  checked. Reads are not filtered by party record: a reader of an activity sees each party's name and address.
- **Field security.** The common activity columns of every activity table, every column of `activitypointer` and
  `annotation.documentbody` cannot be secured (400 `FieldNotSecurable`), because the pointer copy and the note body are read
  outside the column checks. Other columns of an activity table can be secured.
- **System table rules.** Create, Update, Delete, Assign, SetState, GrantAccess and ModifyAccess of `activitypointer`, and
  Create, Update and Delete of `activityparty`, are refused (400) for every caller, System Administrator and SYSTEM included.
- **Note bodies** are read only for the notes a query returns, so they follow the note's read access; their
  `fileattachment` rows need Read on the note.

## Classic workflows

Processes are rows of `workflow`, with the generated privileges (`prvReadWorkflow`, `prvWriteWorkflow`, …); see
[Classic workflows](workflows.md).

- **Grants.** System Customizer holds every privilege on `workflow` and `processtrigger` at Global; System Customizer and Basic
  User hold `prvWorkflowExecution` at Global (its id is derived like every xrmverse privilege id; the depths are xrmverse's
  choice). An organization created earlier gets them once at startup (top-ups `workflow-role-privileges` and
  `workflow-execution-role-privilege`).
- **Authoring.** Creating a process needs `prvCreateWorkflow`, updating it `prvWriteWorkflow` at the record's depth; listing,
  exporting and every `apply` (a dry run too) need `prvReadWorkflow`. The authoring API and the MCP tools need no role: the
  privileges decide. See [Workflow authoring as code](workflow-authoring.md#privileges).
- **Activating, deactivating, assigning, deleting** a process needs Write, Assign or Delete on its definition, as for any row.
  Activating or deactivating also needs the caller to own the definition or hold `prvActOnBehalfOfAnotherUser`, plus
  `prvActivateSynchronousWorkflow` (real-time workflow, action) or `prvActivateBusinessRule` (business rule); a background
  workflow needs neither. System Customizer holds both new privileges at Global through the named grant
  `workflow-activation-role-privileges` (the depths are xrmverse's choice, from memory); Basic User holds neither; a solution
  import is exempt.
- **Raw writes.** An Update re-validates the XAML only when it sends a definition column (the XAML, mode, trigger or stage columns), so an imported Draft with unsupported steps, or of category 1, 4 or 5, can still be renamed. A public Create or Update of `workflow` is accepted for a Draft definition of category 0, 2 or 3 whose XAML
  parses, validates and has no unsupported step; `processtrigger` is still written by solution import only.
- **`ExecuteWorkflow`** needs Read on the process, `prvWorkflowExecution` and Read on the record.
- **As whom steps run.** A real-time workflow with `runas` 1 runs as the caller, with `runas` 0 as its owner; a background
  workflow runs as its owner; an on-demand run as the user who starts it; an action as its caller; a child workflow as the
  child's owner. Every step is a request checked with that user's privileges, record access and field security, so a step the
  user may not do fails with the usual 403.

## Access teams

An **access team template** lets a user be given a fixed set of rights on one record, without creating a team by hand. A
table is switched on with `AutoCreateAccessTeams`, a template names the table and the rights, and a call to
`AddUserToRecordTeam` puts a user on the record's own access team for that template. The team is created on the first add, it
holds one share of the record with the template's rights, and its members read (and, if the template says so, write) the
record from then on. It adds no new check to the security predicate: the share is an ordinary `principalobjectaccess` row
granted to a team, which the read and write checks already honor.

### Switching a table on

`AutoCreateAccessTeams` is a table setting. It is set on `CreateEntity` and `UpdateEntity` (SDK and
`PUT EntityDefinitions(...)`), in the web admin (the **Auto-create access teams** switch of a table), with
`xrmd access-team enable <table>`, or by a solution import (`<AutoCreateAccessTeams>` in `customizations.xml`), and it is
exported. Rules, all 400 `InvalidArgument`:
- only user-owned and team-owned tables, never organization-owned, business-owned or intersect tables;
- at most 100 tables are switched on at the same time (`MaxEntitiesEnabledForAutoCreatedAccessTeams`, a fixed constant);
- the setting cannot be turned off while a template for the table exists;
- `DeleteEntity` of a table that was switched on is refused while a template or an access team refers to it.

### Templates

Templates are rows of `teamtemplate` (object type code 92, entity set `teamtemplates`, organization-owned; see
[Tables in every organization](system-tables.md)). Creating, reading, changing and deleting them needs the
`teamtemplate` privileges (`prvCreateTeamTemplate`, `prvReadTeamTemplate`, `prvWriteTeamTemplate`,
`prvDeleteTeamTemplate`, `prvAppendTeamTemplate` and `prvAppendToTeamTemplate`, Global depth only), which only the System
Administrator role holds. Rules, all 400 `InvalidArgument`:
- `objecttypecode` must be the object type code of a table that is switched on, and cannot change on Update;
- `defaultaccessrightsmask` is a non-empty combination of Read 1, Write 2, Append 4, AppendTo 16, Delete 65536, Share 262144
  and Assign 524288 (the `AccessRights` values); any other bit, or 0, is refused;
- at most 4 templates per table (`MaxAutoCreatedAccessTeamsPerEntity`, a fixed constant);
- `issystem` is read-only: a request that names it is refused, even with the stored value.

A changed mask applies only to teams created afterwards; the share of an existing team is not touched. Deleting a template
deletes its access teams (relationship `teamtemplate_Teams`, Delete Cascade), with their memberships and shares, so its
members lose the access on their next request.

### The access team

The first `AddUserToRecordTeam` for a record and a template creates one team (at most one per record and
template: a unique index guarantees it, also for parallel calls):

| Column | Value |
|---|---|
| `name` | `{recordid}+{teamtemplateid}`, lowercase GUIDs in "D" format |
| `teamtype` | 1 (access team) |
| `systemmanaged` | true |
| `businessunitid` | the record's `owningbusinessunit` |
| `administratorid` | the caller (SYSTEM when the call is internal) |
| `regardingobjectid` | the record: a Lookup whose targets are the tables that are switched on |
| `teamtemplateid` | the template |

The team's share of the record carries the template's mask at that moment. The call then adds the user as a member. The
security cache is invalidated on commit, so the change takes effect on the next request.

### The messages

`AddUserToRecordTeam` and `RemoveUserFromRecordTeam` take `Record` (an `EntityReference`), `SystemUserId` and
`TeamTemplateId`, and answer `AccessTeamId`:
- SDK and SOAP: `AddUserToRecordTeamRequest` and `RemoveUserFromRecordTeamRequest`.
- Web API: `POST systemusers({id})/Microsoft.Dynamics.CRM.AddUserToRecordTeam` with
  `{ "Record": { "@odata.type": "Microsoft.Dynamics.CRM.account", "accountid": "<id>" }, "TeamTemplate": { "teamtemplateid": "<id>" } }`,
  answering `{ "AccessTeamId": "<id>" }`; `RemoveUserFromRecordTeam` has the same shape. A missing or untyped `Record` is 400.
- CLI: `xrmd access-team add|remove` (see [CLI](cli.md)).

Checks, in this order (the first that fails answers):
1. the template exists (404 `ObjectDoesNotExist`);
2. `Record` is a row of the template's table (400);
3. the table is switched on (400);
4. the record exists (404);
5. the user exists (404) and is enabled (400; this applies to `RemoveUserFromRecordTeam` too);
6. the caller holds `prvShare` on the table at a depth that covers the record's owner, and holds certain rights on the record
   (roles, shares and hierarchy all count), otherwise 403 (own text); a System Administrator passes.
   `AddUserToRecordTeam` checks every right it hands out, so a caller cannot use a template to hand out a right they lack:
   the template's current mask and, when the record's team for the template already exists, the rights of that team's share
   of the record (a new member receives the share, which can be wider than the template if the template was narrowed after
   the team was created). `RemoveUserFromRecordTeam` checks only the template's current mask: it only takes rights away, so
   the wider share of an existing team is not asked for. This is the fail-closed choice; the checklist asks what Dataverse
   checks;
7. `AddUserToRecordTeam` only: the added user holds, at Basic depth or wider, the privilege of each of those rights on the
   table (`prvRead` for Read, `prvWrite` for Write, `prvAppend`, `prvAppendTo`, `prvDelete`, `prvShare` and `prvAssign`
   likewise), otherwise 400 naming the missing privilege (own text). The check applies whoever the caller is, a System
   Administrator included; it is not made for the internal caller (a request run by the server itself), and the built-in
   SYSTEM user as the added user is exempt.

Repeating an add is a no-op that returns the same team; removing a user who is not a member is a no-op.
`RemoveUserFromRecordTeam` answers `Guid.Empty` when the record has no team for the template yet. The team and its share stay
when the last member leaves.

### Locking

The core locks the template, then the record, then the team's key until the commit, so the adds and removes
for one template are serialized (and so are a template Delete and a record Delete against the calls): two parallel first
adds create one team and one share, and both users end up members. If the template's mask widens, or the existing team's share gains
a right, between the guard's rights check and the write, the call is refused with 400 and can be retried. A pre-operation plug-in that changes `Record`,
`SystemUserId` or `TeamTemplateId` also gets 400, because the rights were checked on the original values.

### Records and teams
- Deleting a record deletes its access teams (memberships and shares go with them), and only its own: a team names its
  record by `regardingobjectid` and `regardingobjectidtype`, so the Delete of a record of another table that has the same
  id leaves them alone. Assigning or sharing a record leaves them as they are.
- Revocation is immediate: the next Retrieve, RetrieveMultiple, `$expand` or file download of the removed user is refused.
- System-managed teams are written only by the two messages. A request that creates a `team` with `teamtemplateid`,
  `regardingobjectid` or `systemmanaged` is 400, and so is an Update that **changes** one of them (a Retrieve-then-Update
  that sends the stored values back passes, as for `teamtype`). `AddMembersTeam`/`RemoveMembersTeam` and an Associate or
  Disassociate of `teammembership` on a system-managed team are 400 ("use AddUserToRecordTeam/RemoveUserFromRecordTeam");
  `AddMembersTeam` and `RemoveMembersTeam` are not implemented in xrmverse (see Deferred), so today they answer "not
  supported", and the rule starts to act when they are built. Deleting a system-managed team stays allowed with the team
  privilege.
- A system-managed team is shared only on its own record, and only by the record team messages: `GrantAccess` or
  `ModifyAccess` of any record to it is 400 ("a system-managed access team gets access only to its own record through
  AddUserToRecordTeam"), for every caller, System Administrator included. Learn says such a team "can't be shared with
  other records"; refusing a change of its own share too keeps its rights those the adders were checked for. The share the
  first add creates is written by the server directly, so it is not affected.
- An access team owns no record. Learn: "An access team doesn't own records". A `Create` or `Update` whose `ownerid` names a
  team with `teamtype` 1, and an `Assign` to one, are 400 `InvalidArgument` ("An access team cannot own records.", own
  text), whether the team is system-managed or made by hand, for every caller, SYSTEM and System Administrator included (only
  a request run by the server itself is exempt). Without the rule, a record owned by a system-managed team would give each
  member `AddUserToRecordTeam` adds owner-level access, beyond the template rights the adder was checked for. The core
  operation also refuses an owner team of another type ("is not an owner team"), and ownership never moves through an
  Associate, so this rule refuses the request earlier, with one message, before the pre-operation plug-ins run.
- A field share (`principalobjectattributeaccess`, see [Field-level security](field-security.md)) never names a
  system-managed team: a Create whose `principalid` is such a team, or an Update of a share whose principal is one, is 400
  `InvalidArgument` (own text), for every caller, System Administrator included. A column share would reach each new member
  without the adder being checked for it, as for `GrantAccess` above. A field share to an access team made by hand stays
  allowed.
- Plug-ins: both messages are SDK messages with `sdkmessage` rows and no table filter; steps at stages 10, 20 and 40 run
  (`AccessTeamId` is in the stage 40 output). The team, the membership and the share are written by the core, so the steps of
  `Create`, `GrantAccess` and `Associate` do not fire for them.
- Auditing: creating the team writes one **Share** audit row on the record (principal = the team), as `GrantAccess` does when
  the table is audited. Adding and removing a member write no audit row (team membership is not audited).

### Known limits
- The two organization settings behind the limits (4 per table, 100 tables) are not modelled; the values are constants.
- A disabled user cannot be removed from an access team: `RemoveUserFromRecordTeam` answers 400 for a disabled user, as
  `AddUserToRecordTeam` does (ruling A7). A disabled user gets no access anyway. To clean up, re-enable the user, remove them
  and disable them again, or delete the whole team (with the team privilege; the next `AddUserToRecordTeam` creates a new one).
- Shares do not cascade to related child records. In Dataverse a template's rights apply to the record and to its related
  records according to the relationships' cascade rules (Share). xrmverse never cascades a share, whoever grants it
  (`GrantAccess` does not either: see "cascade of share/assign to child records" under Deferred), so the access team's share
  covers its own record only; the access-team case inherits that general limit.
- A team template inside a solution is not exported or imported: only the `AutoCreateAccessTeams` flag of the table travels
  (see [Solutions](solutions.md)). The checklist asks what Dataverse writes.
- The form sub-grid "Associated Record Team Members" is UI work of phase 3, and `RetrieveSharedPrincipalsAndAccess` is not
  implemented, so no message lists the access team.
- A user-made access team (`teamtype` 1 with `GrantAccess`) works as before; templates add nothing to it.
- The names `team_regardingobjectid_<table>` of the relationships that `regardingobjectid` gets per table, the object type
  code 92, the cascade of `teamtemplate_Teams`, the team name format and every error code of an access team failure are from
  our reading of the Microsoft documentation or from memory, not from a real environment.

## Hierarchy security

The **manager hierarchy** lets a manager work with the records of the people who report to them. It is off by default and
needs no position model: only the manager model exists (`systemuser.parentsystemuserid`, relationship `user_parent_user`).
A System Administrator switches it on and sets its depth with `xrmd hierarchy-security`, the Admin API or the web admin
Hierarchy security page (see [CLI](cli.md), [Admin API](admin-api.md) and [Web admin](web-admin.md)).

### Settings

Columns of `organization`. Only a System Administrator can change them: anyone else who changes one gets 403
`0x80040220`, while an Update that repeats the stored values passes.

| Column | Default | Meaning |
|---|---|---|
| `ishierarchicalsecuritymodelenabled` | false | the model is on |
| `maxdepthforhierarchicalsecuritymodel` | 3 (from memory, unverified) | levels of reports a manager reaches; 1 to 100, else 400 |
| `usepositionhierarchy` | false | true is refused with 400 ("the position hierarchy is not supported") |

### Reach of a manager

For a manager M, with the model on and depth `d`:
- The reports are the users found by walking `parentsystemuserid` down from M: level 1 is the direct reports, down to level
  `d`. Disabled reports count. A cycle in the data (written by SQL) cannot loop: each user is visited once.
- A report counts only when M's business unit is the report's business unit or its parent. The rule is judged for each
  report on its own: the walk goes on through a report that fails it, and the reports below that one are judged by
  themselves. It follows the Dataverse default (`ManagersMustBeInSameOrParentBusinessUnitAsReports` = true) and is not
  configurable.
- A report's records are those it owns, those owned by any team it belongs to (xrmverse has no default team per business unit yet) and those shared with it or with one of its teams.
- **Rights.** On the records of direct reports: Read, Write, Append and AppendTo. On the records of reports at levels 2 to
  `d`: Read only. Never Create, Delete, Assign or Share: `GrantAccess` and `ModifyAccess` are never allowed through the
  hierarchy alone. Share must come from the manager's own role (a depth that covers the owner) or from a share of the
  record; a manager who holds Share that way can grant the rights it gets through the hierarchy (Write on a direct report's
  record, for example), never Delete or Assign.
  On a record that the manager reaches only through a share, the manager gets at most the rights of that share.
- M must hold the matching privilege (`prvRead`, `prvWrite`, `prvAppend`, `prvAppendTo` on the table) at any depth. A System
  Administrator, or a role with Global depth, already reaches everything.
- Reads add the reach as a predicate to the SQL query, like the other depths. Under impersonation the effective rights are
  the AND of both users, so the reach of the impersonated manager never exceeds what the impersonator may do. A plug-in
  that runs as the manager through `CreateOrganizationService(userId)` gets the manager's reach.
- A change to `parentsystemuserid`, to a team or its members, or to the settings takes effect on the next request, without
  a restart.

### Tables covered

Tables owned by users or teams, including new custom tables. Organization-owned and business-owned
tables, intersect tables and `fileattachment` (which follows its parent record) are never affected. A System Administrator
can **exclude** tables (`xrmd hierarchy-security exclude <table>`; stored in `meta.hierarchy_security_exclusion`): an
excluded table, or a table that no longer exists, is skipped. Exclusions do not travel in solutions.

### Managers

A user cannot be their own manager and the chain cannot loop: both are 400 `InvalidArgument`, checked on every
write of `parentsystemuserid` (a loop already present above the new manager, written by SQL, is refused with the same code
and a message that names it) (on Create of a user that names itself, the security guard is expected to answer 404 because the new user does not exist
yet: this is observed behaviour, not covered by a request-level test, and the checklist compares it with Dataverse).

### Where else the hierarchy shows

The tree operators `under`, `eq-or-under`, `above`, `eq-or-above` and `not-under` work
on every table that has a hierarchical relationship (`account` through `account_parent_account`, `systemuser` through
`user_parent_user`, a custom self-referencing relationship that a solution import marks `IsHierarchical`), and `eq-useroruserhierarchy` and
`eq-useroruserhierarchyandteams` filter by the caller and the caller's reports: see [Supported queries](queries.md).

### Known limits
- Only the manager model. The position model (`position`, `systemuser.positionid`) is not built (H1).
- `ManagersMustBeInSameOrParentBusinessUnitAsReports` = false is not available (H4).
- There is no dedicated "change hierarchy security settings" privilege: only System Administrators change the settings. The
  checklist records Dataverse's privilege; a follow-up may add it to the catalog (H2).
- The Web API query functions (`Microsoft.Dynamics.CRM.Under` and the others) are not supported in `$filter`, like every
  query function; FetchXML (`?fetchXml=`) and `QueryExpression` work (H9).
- `rowaggregate="CountChildren"` is refused with 400 (H8).
- The hierarchy visualization and `hierarchyrule` are not modelled. `RetrievePrincipalAccess` is available (see
  [Model-driven apps](model-driven-apps.md)): it reports the rights the security check grants on a record.
- **Field-level security does not follow the hierarchy.** A secured column is read and written according to the caller's
  own field security profiles and field shares (and, under impersonation, the AND of both users), never the reach, the
  profiles or the shares of the reports. The field-share rules check Read on the record, and that check includes the
  hierarchy reach, so a manager who holds the column rights can create a field share on a report's record. The
  comparison with Dataverse is pending (see [Field-level security](field-security.md)).
- The default depth 3 and the cascade of `user_parent_user` are from memory; the error codes of hierarchy failures are our
  own generic ones.

### Differences and xrmverse choices
- `role.roletemplateid` is read-only: no request writes it (400), not even a System Administrator's.
- `team.teamtype` is fixed at creation: an Update with a type different from the stored one is rejected (400 "The team type cannot be changed."). A NULL `teamtype` counts as Owner (0) everywhere: record ownership, team roles and the escalation check.
- Adding a member to a team requires Write on the team and, for an owner team, the same escalation check
  as assigning each of the team's roles (`RoleUpdateValidationFailure`; a team with System Administrator can be
  widened only by a System Administrator). Whoever has Write on teams without those privileges cannot add members to teams
  more powerful than themselves.
- Changing the business unit of a user or team does not move the `owningbusinessunit` of the records they own.
- `CallerObjectId` takes precedence over `MSCRMCallerID`; via SOAP the `CallerId` header replaces the HTTP header. A SOAP
  `Header` after the `Body` makes the request malformed (fault `0x80040203`, "The SOAP request is malformed.").
- Asynchronous jobs run as the owner of the `asyncoperation` row (`ownerid` = `UserId` of the context that
  queued them); `createdby` is the `InitiatingUserId`, which the job restores. The Entra object ids of a job's context are
  read from the `systemuser` rows of these two users, not from the payload. An asynchronous step whose
  impersonation user does not exist or is disabled fails immediately (3/31, without retry).
- `IPluginExecutionContext3.AuthenticatedUserId` is the authenticated user of the request (the impersonator if there is
  impersonation); nested calls inherit it.
- Registering plugins is equivalent to being able to act as SYSTEM: a plugin's code can call
  `CreateOrganizationService(null)` and write without restrictions. Whoever has registration privileges (System Customizer
  included) must therefore be considered as trusted as an administrator; the `prvActOnBehalfOfAnotherUser` check on
  steps with `impersonatinguserid` is defense in depth, not a security boundary.
- Security checks run before stage 10, on the request parameters: a PreValidation or
  PreOperation plugin can still change `Target` (for example add columns or change `ownerid`) after the check, and
  the change is not rechecked. This too presupposes trusted plugins.
- Nested plugin calls (`CreateOrganizationService(context.UserId)`) run with all the privileges
  of the impersonated user: the impersonator is not propagated, so the privilege intersection applies only to the
  top-level request.
- The deserialization allowlist (SOAP and asynchronous payloads) admits only .NET primitives, types and enums of
  `Microsoft.Xrm.Sdk`/`Microsoft.Crm.Sdk.Proxy`, their arrays and early-bound subclasses of `Entity`,
  `OrganizationRequest`, `OrganizationResponse`: `DBNull` and every other .NET type are rejected (a plugin that puts a
  different type in `SharedVariables` fails when queued asynchronously).
- The privilege cache is invalidated entirely after every write to users, teams, BUs, roles, privileges or intersects.
- Associate/Disassociate 1:N require Append on the referencing record and AppendTo on the referenced one.
- Create with an `ownerid` different from the caller evaluates the Create depth on the owner, without requiring Assign.
- Lookup names remain visible even if the record pointed to is not readable.
- Custom actions have no privilege checks. A [custom API](custom-apis.md) with an `executeprivilegename` demands that
  privilege, at any depth, before stage 10 (403 `0x80040220`); one without it, like a custom action, checks nothing. Its
  plug-in type then runs as the caller.

### Deferred
- Plan 6: `xrmd org create` and the `xrmd user/role` commands.
- Phase 2: hierarchy security, field-level security and access team templates are done: see
  [Hierarchy security](#hierarchy-security), [Field-level security](field-security.md) and [Access teams](#access-teams).
- Out of the MVP: `RetrieveRolePrivilegesRole`, `AddMembersTeam`/`RemoveMembersTeam`, `SetBusinessSystemUser`,
  default team per BU, cascade of share/assign to child records. `RetrievePrincipalAccess`, `RetrieveUserPrivileges` and
  `RetrieveSharedPrincipalsAndAccess` came with the model-driven apps (UI-1b).

## See also

- [Field-level security](field-security.md)
- [Auditing](auditing.md)
- [Local identity provider](local-idp.md)
- [Web admin](web-admin.md)
