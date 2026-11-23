# Plug-ins

[← xrmverse documentation](../README.md#documentation)

## Introduction

A plug-in is .NET code that runs inside the server when a message (Create, Update, a custom API, …) is processed. xrmverse runs
existing Dataverse plug-ins (signed .NET Framework assemblies implementing `IPlugin`) unchanged: they get the same execution
context, the same stages and the same services.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Registration | Rows of `pluginassembly`, `plugintype`, `sdkmessageprocessingstep`, `sdkmessageprocessingstepimage` through the SDK, SOAP (Plugin Registration Tool) or Web API | Same; steps take effect at once, without a restart |
| Stages and transaction | 10, 20, 40 (sync and async); 20, 30 and 40 in one transaction | Same |
| Context and services | `InputParameters`, images, `Depth`, `SharedVariables`, `IOrganizationServiceFactory`, `ITracingService` | Same |
| Isolation | A collectible `AssemblyLoadContext` in the `xrmd` process | **Differs**: no security sandbox; plug-in code runs in the server process |
| Microsoft SDK | Installed by you (`xrmd sdk install`) | **Differs**: bring your own SDK |
| Instances | A new instance per execution | **Differs**: no instance cache |
| Doomed transactions | A few read-only calls do not doom it; calls after it is doomed still run, then roll back | **Differs** in detail (see [Sandbox and limits](#sandbox-and-limits)) |
| Custom actions over the Web API | Not served (SDK and SOAP only) | **Narrower** (custom APIs are served) |
| Steps on elastic `*Multiple` messages | Refused | **Narrower** |
| Incoming email deliveries | Run as the delivery user at depth 1, with the reserved shared variable `xrmverse_MailboxDeliveryId`; stage 10 inside the import's transaction | **Extra** variable; **Differs** for stage 10 (see [Incoming email deliveries](#incoming-email-deliveries)) |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Register an assembly and its steps | `xrmd plugin register`, `xrmd plugin step add`, Plugin Registration Tool, Web API | [Registration](#registration) |
| Know which messages accept steps | Message list | [Messages](#messages) |
| Read traces | `plugintracelog`, MCP `get_plugin_traces` | [Trace](#trace) |
| Follow asynchronous steps | `asyncoperation` | [Async queue](#async-queue) |
| Write steps for bulk messages | `CreateMultiple`, `UpdateMultiple` | [Steps on `CreateMultiple` and `UpdateMultiple`](#steps-on-createmultiple-and-updatemultiple) |

## Registration

Plug-ins are registered as in Dataverse, by creating the rows of `pluginassembly` (`content` = base64 of the DLL),
`plugintype`, `sdkmessageprocessingstep` and `sdkmessageprocessingstepimage` via SDK/SOAP (`ServiceClient`, Plugin
Registration Tool) or Web API. Registered steps take effect immediately, without a restart; a new `content` replaces the
loaded assembly. The CLI does the same with `xrmd plugin` (see [CLI xrmd](cli.md)).

## Stages

| Stage | Name | Mode | Notes |
|---|---|---|---|
| 10 | PreValidation | synchronous | outside the transaction (at the first level) |
| 20 | PreOperation | synchronous | inside the transaction; changes to `Target` reach the core |
| 30 | Core / main operation | synchronous | the SQL operation (no plugin); for a [custom API](custom-apis.md), its plug-in type, and for a XAML action, its XAML, inside the transaction (not registrable) |
| 40 | PostOperation | synchronous or asynchronous | synchronous inside the transaction; asynchronous queued in `asyncoperation` |

Pre/post images (Create: post only; Delete: pre only) and filtering attributes (on `Update` only) are supported.

## Messages

| Message | Notes |
|---|---|
| `Create`, `Update`, `Delete`, `Retrieve`, `RetrieveMultiple`, `Assign` | For `Retrieve` and `RetrieveMultiple` the result is in `OutputParameters["BusinessEntity"]` / `["BusinessEntityCollection"]`, as in Dataverse |
| `SetState` | Executed as an `Update` of `statecode`/`statuscode`: `Update` steps fire, not those registered on `SetState` |
| `Associate`, `Disassociate` | 1:N, N:N and security relationships (see [Security](security.md)) |
| Custom actions | A custom `sdkmessage` row, via SDK/SOAP only (see [Custom actions and custom APIs](#custom-actions-and-custom-apis)) |
| Custom APIs | See [Custom APIs](custom-apis.md) |
| `AddUserToRecordTeam`, `RemoveUserFromRecordTeam` | Steps without a table filter at stages 10, 20 and 40; `AccessTeamId` is in the stage 40 output. The team, the membership and the share are written by the core, so the `Create`, `GrantAccess` and `Associate` steps do not fire for them; a pre-operation plug-in that changes `Record`, `SystemUserId` or `TeamTemplateId` gets 400 (see [Security](security.md#access-teams)) |
| `RetrieveEntityChanges` | A step without a table filter fires for every table and sees it as `PrimaryEntityName`; the read is at stage 30 like `RetrieveMultiple`, the result is in `OutputParameters["EntityChanges"]`, and the Web API delta queries run through the same message (see [Change tracking](change-tracking.md)) |
| `RetrieveExchangeRate` | A step without a table filter, `PrimaryEntityName` `transactioncurrency`; the read is at stage 30 like `RetrieveMultiple`, the value is in `OutputParameters["ExchangeRate"]` (see [Currencies](currencies.md#retrieveexchangerate)) |
| `PublishDuplicateRule`, `UnpublishDuplicateRule`, `RetrieveDuplicates`, `BulkDetectDuplicates` | Core messages with **no** `sdkmessage` rows: a step cannot be registered on them (the usual unknown-message 400); a plug-in can call them through its service |

### Values the server sets

- **Duplicate detection.** The duplicate check of a Create or Update, when the request asks for it, runs after the stage 20
  steps on the final values, and a `CreateRequest` a plug-in executes with `SuppressDuplicateDetection` = `false` gets it
  too (see [Duplicate detection](duplicate-detection.md)).
- **Money.** On `Create` and `Update` of a table with money, the server sets `exchangerate` and the `_base` values at stage
  30: pre-operation plug-ins do not see them in `Target`, post-operation images do, and a `_base` a plug-in sets is
  overwritten.

### Activities and notes

See [Activities and notes](activities.md).
- `SendEmail` is a message with a filter on `email` (stages 10, 20 and 40). `PrimaryEntityName` is `email` and the
  primary id is `EmailId`; pre and post images hold the email (the post image shows the state, status and the sender the
  send filled in); `Subject` is in the output parameters. The send writes the email through the core's Update path, but
  the `Update` and `SetState` steps of `email` do not fire for it. The message name is `SendEmail`; a step registered on a
  message named `Send` follows the rules for an unknown message.
- Party lists are in `Target` as the client sent them (an `EntityCollection` of `activityparty` entities per column), and
  in pre and post images, where each party list of the table is present (empty when it has no rows). Changing a party
  list in `Target` at stage 20 changes what is written.
- `activitypointer` and the party rows are written by the core in the activity's transaction, after the activity row and
  before stage 40: no step fires for these writes, and their message filters (Retrieve, RetrieveMultiple, Associate,
  Disassociate) allow no write step on them.
- `annotation.documentbody` is in `Target`, and in images taken with all columns or naming it (read from the file store
  each time). The body is written at stage 30, so a body set by a pre-operation plug-in is the one stored.
- The refused activity messages (`Book`, `Rollup`, `InitializeAnnotationBlocksUpload`, …, see
  [What is refused](activities.md#what-is-refused)) are refused before any step runs, also when the organization has an
  `sdkmessage` row with that name, and no custom API can take their names.

### Incoming email deliveries

Each message a mailbox receives is imported through the pipeline (see [Incoming email](email-and-queues.md#incoming-email)):
the `email` Create, one `activitymimeattachment` Create per attachment, a note `annotation` Create when attachments were
left out, and, for a queue's mailbox, `AddToQueue`, whose nested `queueitem` Create (depth 2, `ParentContext` the
`AddToQueue`) fires the `queueitem` Create steps. The steps see:

- **`Depth` 1, no `ParentContext`**, and `UserId` = `InitiatingUserId` = the delivery user (the mailbox's owning user, or the
  administrator of the team that owns it); never SYSTEM.
- **`SharedVariables["xrmverse_MailboxDeliveryId"]`**: the mailbox id as a string (`D` format), an **xrmverse extension**. Every
  context of the delivery has it in its own `SharedVariables`, the nested `queueitem` Create included, so a guard reads its
  own context, not `ParentContext`. An asynchronous step of a delivery sees it in its context too.
- **Reserved.** The variable is the server's. No SDK, SOAP or Web API parameter maps to it (a `tag` does not reach it), and a
  synchronous plug-in or workflow step that adds, changes or removes it fails the message with 400 `InvalidArgument`. The
  requests a plug-in makes, synchronous or asynchronous, do not carry it.
- **Server values.** `senton`, `correlationmethod` and `emailsender` are written by the core at stage 30: they are not in the
  `Target` of the email Create, and the post-image has them.
- **One message, one transaction.** Every step of the delivery, stage 10 (PreValidation) included, runs inside the import's
  transaction: a step that fails rolls back the whole message (its email, attachments and queue item), and the
  synchronization tries the message again (see [Synchronization](email-and-queues.md#synchronization)). At the first level
  of an ordinary request, stage 10 runs outside the transaction.

How a contact center guard uses the variable is in
[Building a delivery guard](email-and-queues.md#building-a-delivery-guard).

### Registration validation

A registration that breaks these rules is a 400 `InvalidArgument`: `content` must be a .NET assembly (if missing, `name`,
`version`, `culture` and `publickeytoken` are taken from its metadata); `typename` must exist and implement `IPlugin` or
derive from `System.Activities.CodeActivity` (a custom workflow activity: `isworkflowactivity` is then set to true, and a
type sent with `isworkflowactivity` true that is not one is a 400; see [Classic workflows](workflows.md#custom-workflow-activities));
stage only 10/20/40, asynchronous only on 40; the filter must belong to the message; the filtered attributes must exist.
On a custom API's message, the API's `allowedcustomprocessingsteptype` also applies: 0 refuses every step, 1 accepts only
asynchronous stage 40 steps, 2 accepts any.

### One assembly per name

A `pluginassembly` `name` is unique in the organization, ignoring case, as in Dataverse. A
Create (SDK, SOAP or Web API) with the name of an existing assembly, given or taken from the metadata, and an Update that
renames an assembly to the name of another, fail with 412 `0x80040237` (DuplicateRecord): update the existing assembly
instead (`xrmd plugin register` and the Plugin Registration Tool do). A solution import that carries an assembly under another id than
the organization's assembly of the same name fails with the same error before anything is written, naming the assembly
and both ids (the two are not merged); with the same id it updates the assembly as usual. A database written before
this rule may still hold assemblies sharing a name; when they are the same assembly identity, their builds replace each
other's loaded copy on every execution. The `pluginAssemblies` check of `/admin/doctor` (`xrmd server doctor`) warns
about them, listing their ids; keep one.

### Classic workflows

See [Classic workflows](workflows.md).
- Real-time workflows and Entity-scope business rules run at stages 20 and 40 in the same list as the synchronous plug-in steps,
  ordered by `rank`, then plug-in steps before workflows at equal rank, then id. Their steps are requests at depth + 1, so plug-in
  steps fire on them (the reads of a workflow are `Retrieve` requests); a workflow that updates the primary record at stage 20
  writes into `Target` instead, as a pre-operation plug-in does.
- A background workflow is a system job of `operationtype` 10, queued at post-operation in the triggering transaction and run by
  the same worker as asynchronous plug-in steps; it is not retried.
- A XAML action (category 3) is a message: steps can be registered on it at stages 20 and 40, and its XAML runs at stage 30.
- `ExecuteWorkflow` (run a workflow on demand) is a core message; it needs Read on the process and `prvWorkflowExecution`.

### Webhooks

A step can also send the execution context to an HTTP receiver instead of calling a plug-in type: its `eventhandler` is a
`serviceendpoint` of contract 8 (see [Webhooks](webhooks.md)). Stage, mode, filter, images, rank and `asyncautodelete` follow the rules above;
a webhook step has no plug-in type and no configuration, and writes no trace rows. As in Dataverse, the Web API navigation property of
`sdkmessageprocessingstep.eventhandler` is `eventhandler_plugintype` for a plug-in type (in `@odata.bind` and `$expand`) and
`eventhandler_serviceendpoint` for a webhook. `asyncautodelete` deletes the succeeded system job of
plug-in and webhook steps alike.

### Custom actions and custom APIs

Both are messages that a solution adds, and both are called with an
`OrganizationRequest` by name.
- A *custom action* is only an `sdkmessage` row (category `CustomAction`): stage 30 does nothing, the request is not
  checked against any declared parameter, there is no privilege check, the outputs are whatever the steps set, and it is
  reachable through the SDK and SOAP only.
- A *custom API* is created over the SDK or the Web API, or imported from the `customapis/` folder of a solution: its parameters are validated before stage 10,
  its `executeprivilegename` is demanded, its plug-in type runs at stage 30 as the main operation, its response is
  completed to the declared properties, and it is also a Web API action or function declared in `$metadata`. See
  [Custom APIs](custom-apis.md).

## Sandbox and limits
- Each assembly is loaded in a collectible `AssemblyLoadContext`; `Microsoft.Xrm.Sdk` and `Microsoft.Crm.Sdk.Proxy` are
  always the host's. No security isolation: plugin code runs in the `xrmd` process.
- .NET plug-ins need the Microsoft SDK: the host's `Microsoft.Xrm.Sdk` and `Microsoft.Crm.Sdk.Proxy` are those of the SDK
  folder that you fill with `xrmd sdk install` (not shipped with xrmverse), whatever SDK version the plug-in was built
  against. See [Installing the Microsoft SDK](microsoft-sdk.md) for the lookup, the version check and the
  commands that need it.
- Timeout per execution: 2 minutes (`Pipeline:PluginTimeoutSeconds`, default 120).
- Maximum depth 8: beyond that, error `0x80044182` (infinite loop, HTTP 400).
- `InvalidPluginExecutionException` → rollback and HTTP 400 with the plugin's message; any other exception → rollback and
  HTTP 500 (`0x80040224`). Via SDK the caller receives `FaultException<OrganizationServiceFault>`.
- Custom workflow activities (`CodeActivity`, `isworkflowactivity = true`) are registered like plug-in types and run as steps of
  classic workflows, through CoreWF and the workflow SDK that `xrmd sdk install` adds; they follow the same timeout, error
  mapping, trace and service rules, except that `CreateOrganizationService(null)` acts as the workflow's run user. See
  [Classic workflows](workflows.md#custom-workflow-activities).
- A plugin that cannot be loaded or instantiated (invalid assembly, missing type or dependency, no supported
  constructor) → HTTP 500 (`0x80040224`) with the message `The plug-in type '…' could not be loaded: …` and a row
  in `plugintracelog`.
- Nested calls and transaction (as in Dataverse): if a plugin's service call fails inside the
  transaction, the transaction is doomed even if the plugin catches the `FaultException` and continues; at commit everything is
  rolled back and the request fails with HTTP 500 `0x80040224` "There is no active transaction. This error is usually
  caused by custom plug-ins that ignore errors from service calls and continue processing.". Outside the transaction
  (PreValidation at the first level, asynchronous jobs) a caught error has no consequences.
  Differences from Dataverse: an error from `WhoAmI`, `RetrieveEntity`, `RetrieveAttribute` or from
  `CreateOrganizationService` with an unknown user does not doom the transaction (they write no data); calls
  made after the transaction is doomed are still executed and rolled back at commit, instead of failing immediately.
- `CreateOrganizationService(userId)` returns a service that acts as that user, with that user's security: `Guid.Empty` (or the
  execution's own user id) acts as the user of the execution (`context.UserId`), and `null` acts as the built-in SYSTEM
  user, as in Dataverse. When the message runs internally (the context reports `UserId` = `Guid.Empty`), "the user of the execution" is SYSTEM. A plug-in never acts as the server's internal
  caller, so the rules that only internal code may pass (such as writing system-managed teams) refuse a plug-in's service.
- On startup xrmd enables `READ_COMMITTED_SNAPSHOT` on the organization database if it is off
  (`ALTER DATABASE … WITH ROLLBACK IMMEDIATE`): on an existing database the first start interrupts open sessions
  and requires the ALTER DATABASE permission for the xrmd login.
- `WhoAmI`, `RetrieveEntity` and `RetrieveAttribute` from a plugin's service do not go through the pipeline (no steps,
  as for the frontends); `WhoAmI` returns the user the service acts as, its business unit and the organization.
- A plugin instance is created on every execution (no instance cache, unlike Dataverse):
  state in fields does not survive between executions.
- Each nesting level occupies a pool thread (the calling plugin stays blocked waiting for the nested call):
  a deep chain (up to 8) with many concurrent requests consumes many threads.
- A new `pluginassembly` `content` does not recheck the `plugintype` rows already registered: if a type no longer exists, the steps
  that use it fail at execution (500, see above).
- `RetrieveMultiple` via Web API: plugins see `Query` as a placeholder `QueryExpression` that contains only
  the entity (not the OData filters); modifying it in place has no effect, only replacing `Query` with another query does.
- One copy of each assembly per process for the executions, and a write that changes only the version or other columns
  (same `content`) keeps it. When an execution first needs a new `content` of an assembly that is already loaded, xrmd
  releases the old copy. For an ordinary (not early-bound) assembly it then waits a short time for .NET to reclaim the
  memory of the copy it has just released (not of older copies still in use, which are reclaimed once nothing runs
  them), and loads the new one. It never waits for an early-bound copy: those stay loaded until restart (two bullets
  down), so two copies of one early-bound assembly can be in memory side by side, after an update or after the
  registration check of a new build (next bullet). That matters only to a component that scans the loaded assemblies
  for proxy types, such as an in-process `ServiceClient`: the SDK keeps a process-wide registry of them and fails with
  "A proxy type with the name … has been defined by another assembly".
- The `typename` check of a `plugintype` registration runs inside the registration's transaction, which may still roll
  back, so it never releases the copy that the executions run. With the same `content` it uses that copy. With another
  one, it loads an ordinary assembly into a copy of its own, unloaded as soon as the check is done; an early-bound one
  is loaded beside the copy in use, and the first execution of that `content`, once the registration has committed,
  runs it instead of loading another. If the registration rolls back, that early-bound copy stays in memory until restart.
- Early-bound assemblies (`[assembly: ProxyTypesAssembly]`, classes generated by CrmSvcUtil/`pac modelbuilder`): they work,
  but the SDK keeps every assembly of this kind alive for the whole life of the process, so its old copy is never
  released. After an update, both copies stay in memory until xrmd restarts (memory grows with every update). The new
  `content` runs at once, in synchronous and asynchronous steps alike. Only a Dataverse `ServiceClient` running inside the
  same process, whose first proxy-type scan comes after the update, would hit the error above. xrmd hosts no
  `ServiceClient`, so this only matters to in-process test harnesses: they should connect their client before updating
  an early-bound assembly.

## Trace

`ITracingService` writes to `plugintracelog` according to `organization.plugintracelogsetting` (`0` Off,
`1` Exception — default, `2` All); `messageblock` is truncated to 10,240 characters and rows survive rollback.

## Async queue

An asynchronous step creates an `asyncoperation` row (operationtype 1) executed by a background
worker, in creation order. States (`statecode`/`statuscode`):

| State | Meaning |
|---|---|
| `0/0` | ready (waiting) |
| `2/20` | running |
| `1/10` | suspended, waiting for the next attempt (`postponeuntil`) |
| `3/30` | completed |
| `3/31` | failed (`InvalidPluginExecutionException` immediately; other errors after `Pipeline:AsyncMaxRetries` attempts, default 3) |
| `3/32` | canceled (step deleted or disabled; for a workflow job, see [Classic workflows](workflows.md#background-jobs-and-waits)) |

A job runs as the owner of its `asyncoperation` row (with their current business unit), never as the users named
in the payload; a nonexistent or disabled owner makes the job fail (`3/31`). The payload accepts only the types allowed
by the allowlist (see [Security](security.md)).
On startup the jobs left in `2/20` return to `0/0`: one `xrmd` process per database is assumed.
`Pipeline:AsyncWorkerEnabled=false` disables the worker (jobs are still queued).

## Not available yet

- Custom actions via Web API (`POST /{action}`); custom APIs are on the Web API (see [Custom APIs](custom-apis.md)).
- Dependency report (`xrmd doctor`).
- Service Bus endpoints as `eventhandler`.

## Elastic tables

On an [elastic table](elastic-tables.md) the stages of a first-level write commit one after the other and a write nested in another message's transaction joins it. Steps cannot be registered on `CreateMultiple`, `UpdateMultiple`, `UpsertMultiple` or `DeleteMultiple` of an elastic table: register on the single-record message, which fires once per record.

## Steps on `CreateMultiple` and `UpdateMultiple`

On a standard table you can register steps on `CreateMultiple` and `UpdateMultiple` (with a table filter), and they merge
with the `Create` / `Update` steps in both directions: a bulk runs the single-record steps once per target, and a plain
`Create` or `Update` (also one reached through `Upsert`, `UpsertMultiple` or a Web API `PATCH`) runs the `*Multiple` steps
with a one-item `Targets`. A bulk is all-or-nothing in one transaction. `Upsert` and `UpsertMultiple` are not events on
standard tables. A step on either that names a standard table is refused, so register on `Create` or `Update`. A step on
`Upsert` with no table filter is still accepted, but it never fires for standard tables.

- `Targets` replaces `Target`; images come only from `PreEntityImagesCollection` / `PostEntityImagesCollection`, one per
  target, and an image on an asynchronous or webhook `*Multiple` step is refused.
- An `UpdateMultiple` step with filtering attributes fires when any target carries one of them.
- Only changes to the shared `Entity` persist from a `*Multiple` step in a single write; changes to the parameter
  collections are not carried over.
- Asynchronous steps queued inside a bulk carry the bulk context as `ParentContext`, so their payloads grow with the bulk.
- A bulk message called from plug-in code is refused.

The rules, rulings and unverified points are in [Bulk operations](bulk-operations.md).

## See also

- [Webhooks](webhooks.md)
- [Custom APIs](custom-apis.md)
- [Classic workflows](workflows.md)
- [Bulk operations](bulk-operations.md)
- [Installing the Microsoft SDK](microsoft-sdk.md)
