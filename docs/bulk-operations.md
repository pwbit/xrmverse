# Bulk operations

[← xrmverse documentation](../README.md#documentation)

## Introduction

Bulk operations let one request create, update or upsert many records of a table, faster than one request per record.
xrmverse offers the bulk messages `CreateMultiple`, `UpdateMultiple`, `UpsertMultiple` and `DeleteMultiple`, plus
`Upsert`, on the SDK (SOAP and `IOrganizationService`) and as Web API actions. This page covers standard tables in detail;
[Elastic tables](elastic-tables.md#bulk-messages-and-partial-success) covers partial success there.

Microsoft describes the behaviour in these Learn pages, which this page follows and paraphrases:
[Use bulk operation messages](https://learn.microsoft.com/power-apps/developer/data-platform/bulk-operations),
[Write plug-ins for CreateMultiple and UpdateMultiple](https://learn.microsoft.com/power-apps/developer/data-platform/write-plugin-multiple-operation),
[Use Upsert to create or update a record](https://learn.microsoft.com/power-apps/developer/data-platform/use-upsert-insert-update-record),
[Perform conditional operations](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/perform-conditional-operations-using-web-api#limit-upsert-operations)
and the message lists of the
[account](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/account#messages) and
[elastic](https://learn.microsoft.com/dynamics365/developer/reference/entities/msdyn_ocliveworkitemcontextitemelastic) table references.

Several points below come from our reading of those pages, not from a running Dataverse environment. They are marked
**unverified** and collected in [Unverified points](#unverified-points).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| `CreateMultiple`, `UpdateMultiple` on standard tables | All-or-nothing, events (steps can be registered) | Same model; offered on more tables than Dataverse (**unverified**) |
| `UpsertMultiple`, `Upsert` | Not events: register on `Create` / `Update` | Same |
| `DeleteMultiple` on standard tables | Refused (400) | Same as Dataverse's documented state |
| Elastic tables | Partial success; steps on `*Multiple` refused | **Narrower**: register on `Create` / `Update` |
| Size of a bulk | No maximum number of targets | **Differs**: limited only by the SQL transaction |
| Inner execution depth, step order, rank ties | Our reading of the documentation | **Unverified** (see [Unverified points](#unverified-points)) |
| Web API `$batch` | Up to 1000 requests, atomic change sets | Same; a change set refuses what commits in its own transaction |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Choose a message for a table | Check the table type | [Messages by table type](#messages-by-table-type) |
| Write plug-ins that see bulk and single writes | Register on `CreateMultiple`/`UpdateMultiple` or `Create`/`Update` | [The pipeline merge](#the-pipeline-merge) |
| Create or update by key | `Upsert`, Web API PATCH | [Upsert](#upsert), [Web API PATCH](#web-api-patch) |
| Send many requests at once over HTTP | `$batch` | [Web API batch](#web-api-batch) |

## Messages by table type

| Message | Standard table | Elastic table |
|---|---|---|
| `CreateMultiple`, `UpdateMultiple` | Yes, all-or-nothing. Events: steps can be registered | Yes, partial success. Steps are refused: register on `Create` / `Update` |
| `UpsertMultiple` | Yes, all-or-nothing. Not an event | Yes, partial success. Steps refused |
| `DeleteMultiple` | Refused: 400, `DeleteMultiple has not yet been implemented.` | Yes, partial success. Steps refused |
| `Upsert` | Yes. Not an event: register on `Create` / `Update` | Yes, replaces the row. Steps allowed (see [Elastic tables](elastic-tables.md#writes)) |

- **Which standard tables.** Every standard table that has `Create` and `Update` message filters offers the three
  messages: all tables except `activitypointer` and `activityparty`. Dataverse offers them on custom tables and on many,
  but not all, system tables, so xrmverse offers more than Dataverse does (**unverified**: the exact Dataverse coverage).
- **Detecting support.** Query `sdkmessagefilter` joined to `sdkmessage`: a row for `CreateMultiple` or `UpdateMultiple`
  on `account` exists, and none for `UpsertMultiple`, as the Learn page describes. Elastic tables have rows for all four
  bulk messages and for `Upsert`. Filters for `Upsert` on standard tables are removed at startup unless a step uses
  one, in which case the filter stays and a warning names the step (such a step never fires).
- **One table per request.** All targets must belong to one table.
- **No size limit.** xrmverse sets no maximum number of targets. A bulk that is too large for the SQL transaction fails
  as any large transaction does.
- **Not from plug-in code.** A bulk message sent from a plug-in (any table type) is refused with 400
  `<Message> cannot be called from plug-in code.` `Upsert` from a plug-in is allowed.

## Standard tables: all-or-nothing

A bulk request on a standard table runs in one transaction that xrmverse owns. The first target that fails, in its
plug-ins, its security check or its write, ends the request:

- every row, audit entry, change-tracking entry and queued asynchronous job of the request is rolled back;
- the client receives that target's fault unchanged (code, message, HTTP status), without the `Plugin.BulkApiErrorDetails`
  detail, which exists only for elastic tables;
- `CreateMultipleResponse.Ids` lists the new ids in target order; `UpdateMultipleResponse` is empty;
  `UpsertMultipleResponse.Results` has one `UpsertResponse` per target; no session token is returned.

Repeated ids:

- `UpdateMultiple` acts on the first target of a repeated id and drops later ones before the pipeline, so plug-ins never
  see them. Targets are checked again after the PreOperation stage of the `*Multiple` steps: the table must still be
  the one in the request, and repeated ids are handled again, so a plug-in that edits `Targets` cannot slip a bad
  target past the checks.
- `UpsertMultiple` fails the whole request, before anything runs, when an id repeats.
- In `CreateMultiple` and `UpdateMultiple`, invalid targets (wrong table, no entity, and similar) are rejected before any
  plug-in runs.

Security: before stage 10 of `CreateMultiple` / `UpdateMultiple`, the caller must hold the table's Create (or Write)
privilege at any depth level, so a caller without it runs no `*Multiple` plug-in. Every target is then checked fully when
its own inner execution runs. `UpsertMultiple` and `Upsert` have no outer check, since no step can fire before the inner
checks.

## The pipeline merge

Dataverse runs the plug-ins of the single-record and the bulk message together, in both directions. xrmverse does the
same.

### Bulk to single

Each target of a bulk message runs the single-record message (`Create`, `Update`, or `Upsert` for `UpsertMultiple`)
through the full pipeline as an *inner* execution, in the bulk's transaction:

- the same `Depth` as the bulk message;
- `ParentContext` is the bulk message's context;
- its own `SharedVariables`;
- `IsInTransaction` is true, also for its PreValidation steps.

The order is: bulk PreValidation, bulk PreOperation, then for each target in order the single-record PreValidation,
PreOperation, core write and PostOperation, then the bulk PostOperation. Asynchronous steps are queued, not run.
Keeping `Depth` unchanged means a common guard such as `if (context.Depth > 1) return;` still lets a single-record
plug-in run once per record. Dataverse does not document depth or order for this case (**unverified**, B4).

Asynchronous steps that fire inside a bulk carry the bulk context as `ParentContext`. That context holds all targets, so
each queued payload grows with the size of the bulk and the total stored grows with its square. For large bulks, prefer
synchronous steps or keep the bulk smaller; the cost is real for the `asyncoperation` table.

### Single to bulk

A plain `Create` or `Update` on a standard table also runs the steps registered on `CreateMultiple` /
`UpdateMultiple` for that table, in the same stages. Within a stage the two lists are merged by `rank`, then by step id
(**unverified**: how Dataverse breaks rank ties between the two lists, B5). Writes that arrive through `Upsert`, a Web
API `PATCH` or `UpsertMultiple` merge in the same way, because on a standard table `Upsert` and `UpsertMultiple` are not
events and their inner `Create` / `Update` is what steps see. Only the targets of a `CreateMultiple` / `UpdateMultiple`
skip the merge (they would fire the bulk steps twice).

A `*Multiple` step sees a **multiple view** of the context:

- `MessageName` is `CreateMultiple` or `UpdateMultiple`;
- `InputParameters` are the request's, with `Target` replaced by `Targets`, an `EntityCollection` of one item, the same
  `Entity` instance as the single-record target;
- `OutputParameters["Ids"]` holds the id after stage 30 of a create;
- `SharedVariables` are shared with the single-record steps of the same execution.

The view is rebuilt for each step. Only changes made to the shared `Entity` persist (a PreOperation step that sets `name`
on `Targets[0]` changes the saved row). Changes to `InputParameters`, `OutputParameters`, or replacing the item of
`Targets` are not carried over.

## Images

A `*Multiple` step reads images only from `PreEntityImagesCollection` and `PostEntityImagesCollection`
(`IPluginExecutionContext4`), one `EntityImageCollection` per target, index-aligned with `Targets`. `PreEntityImages`
and `PostEntityImages` stay empty. The single-record rules apply: no pre image on `CreateMultiple`, and a post image
only at PostOperation. When the step registered no image, the entry for each target is an empty collection; Dataverse
does not document this, so it is on the checklist for #20.

Asynchronous and webhook steps get a remote execution context, which has no image collections. An image on such a
`*Multiple` step is refused at registration:
`Images on asynchronous or webhook steps of '<Message>' are not supported by xrmverse: the remote execution context has no
image collections.`

Images are read once per target, so a bulk of N targets with images costs N reads (N+1 pattern). This is a known cost;
batching is not done yet.

## Step registration

- A step on a `*Multiple` message must name a table: `Plug-in steps on '<Message>' must name a table (sdkmessagefilterid).`
  An unknown `sdkmessagefilterid` gives the existing 404.
- A step on a `*Multiple` message of an elastic table is refused with a message telling you to register on the
  single-record message.
- A step on `Upsert` or `UpsertMultiple` of a standard table is refused (a client can only reach this by forging the
  filter id): `'<Message>' is not an event on the standard table '<table>': register on Create or Update.`
- **Filtering attributes** are accepted on `Update` and `UpdateMultiple` steps only. Any other message gets
  `Filtering attributes are supported only for the Update and UpdateMultiple messages.` An `UpdateMultiple` step with
  filtering attributes fires when **any** target carries one of them, once, with all targets. In the single-to-bulk
  view the single target decides.

## Upsert

`Upsert` takes the record by `Entity.Id` or by the primary key attribute. `KeyAttributes` are refused on standard tables
(xrmverse has no alternate keys there). The message returns `RecordCreated` and `Target`.

On a **standard table**, `Upsert` is not an event: no step fires on `Upsert` itself, and it needs no outer security
check. Its core step takes `UPDLOCK, HOLDLOCK` on the id inside the Upsert's transaction, then runs an inner `Update`
(the target without its primary key attribute) when the row exists, or an inner `Create` (with the given id, or a new
one) when it does not. Consequences:

- each branch validates with its own rules, Update rules when the row exists and Create rules when it does not, because
  the inner message does the validating;
- the optional parameters of the request (such as `SuppressDuplicateDetection`) are carried to the inner message;
- the inner `Create` / `Update` fires `Create`, `Update`, `CreateMultiple` and `UpdateMultiple` steps, whether it was
  reached by a direct `Upsert`, a Web API `PATCH` or an `UpsertMultiple`;
- the inner execution uses the same `Depth` as the Upsert, with the Upsert's context as `ParentContext`;
- the id lock means two concurrent upserts of one new id end as one create and one update, never as a DuplicateRecord.

On an **elastic table**, `Upsert` replaces the row and is its own event (see [Elastic tables](elastic-tables.md#writes)).

## Web API PATCH

On a standard table `PATCH {set}(<id>)` follows the Learn conditional-operations page:

| Request | Result |
|---|---|
| no `If-Match` / `If-None-Match` | upsert: updates the row, or creates it with that id |
| `If-Match: *` | update only; 404 when the row is missing |
| `If-None-Match: *` | create only; 412 `0x80040237` when the row exists. The Create privilege is checked first, so a caller without it gets 403 whether or not the row exists |
| both headers | 400 |
| a primary key in the body that differs from the URL | 400 `The primary key in the body does not match the URL.` (our choice, **unverified**) |

With `Prefer: return=representation` the status is 201 when the row was created and 200 when it was updated. ETag values
in `If-Match` / `If-None-Match` are refused with 400.

On an **elastic table** `PATCH` stays update only (a missing row is 404): an elastic Upsert replaces the row, so a PATCH
must not turn into one silently. `If-None-Match: *` on an elastic table is a 400.

The bulk actions (`Microsoft.Dynamics.CRM.CreateMultiple`, and so on) reach standard tables through the same service
and so behave as described here.

## Web API batch

`POST /api/data/v9.2/$batch` takes a `multipart/mixed` body of requests and change sets, as Learn's
[Execute batch operations using the Web API](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/execute-batch-operations-using-web-api)
describes it. The requests run in order, and the responses come back in the same order.

- **Each request is a request of its own.** It runs as the batch's caller, with its own headers only: the batch's headers,
  `Prefer` included, do not apply to it. Its URL may be absolute, start with `/api/data/v9.2/`, or be relative to the
  service root. A batch holds at most 1000 requests, and a batch inside a batch is refused.
- **A change set is one transaction.** It is the Web API form of `ExecuteTransaction`: every request in it runs in the
  same SQL transaction, and the transaction commits only when all of them succeed. A failed change set answers with one
  response, the failed request's. A request can name a record created earlier in the same change set by its `Content-ID`:
  `$1` at the start of its URL, or as a string value in its body (`"primarycontactid@odata.bind": "$1"`). An unknown
  reference is a 400 "Content-ID Reference: '$1' does not exist in the batch context."
- **What a change set can hold.** Record writes (POST, PATCH, DELETE on an entity set), `$ref`, and actions. A GET is
  refused. Anything that commits in a transaction of its own is refused with a 400 instead of running outside the
  change set: metadata, file columns, solution messages (import, upgrade, uninstall, the delete of a solution row), audit
  and autonumber messages, and writes on elastic tables (Learn: elastic tables do not support multi-record
  transactions).
- **Errors.** The first failed request stops the batch, and the batch's status is that request's status. With
  `Prefer: odata.continue-on-error` the batch runs the rest and answers 200.
- **Line breaks.** The body needs CRLF line breaks between parts, as Learn requires.

## Edge cases worth knowing

- `Upsert` takes the id lock before the inner privilege check. A caller without the privilege therefore gets a 403 whose text
  depends on the row: it names the Write privilege when the id exists and the Create privilege when it does not.
- Bulk pre-images are read for the targets as they were validated, before the PreOperation stage. A PreOperation step that
  adds, removes or reorders items in `Targets` breaks the alignment between `Targets` and the image collections.
- A `DeleteMultiple` step on a standard table gets the same "is not an event on the standard table" refusal as a step on
  `Upsert` or `UpsertMultiple`.

## Known costs and limits

- Image reads are per target (see above).
- Asynchronous payloads grow with the bulk (see above).
- The multiple view loses changes outside the shared `Entity` (see above).
- No batching of the inner executions: a bulk is N pipeline executions in one transaction, so very large bulks hold the
  transaction open for the whole run.

## Unverified points

These follow our reading of the Learn pages and are to be compared with a real Dataverse environment
(#20):

- **B4**: the `Depth` and step order of the inner executions, and `ParentContext` of the single-record steps in a bulk.
- **B5**: how rank ties are ordered across the two merged lists, and what a `*Multiple` step sees in a single write.
- **B2**: which tables expose the three messages.
- The shape of the image collections of a `*Multiple` step that registered no image (an empty collection per target here).
- The error of a step registered on `Upsert:account`.
- The PATCH primary-key mismatch error.

## See also

- [Plug-ins](plugins.md)
- [Elastic tables](elastic-tables.md#bulk-messages-and-partial-success)
- [Supported queries](queries.md)
