# Duplicate detection

[← xrmverse documentation](../README.md#documentation)

## Introduction

Duplicate detection tells a client that a row it is about to create or update looks like a row that already exists. A
**rule** says when two rows count as the same (for example "same email address, ignoring case"); a **published** rule is
checked when a client asks for it on a Create or Update, when it calls `RetrieveDuplicates`, and in a bulk job
(`BulkDetectDuplicates`). xrmverse follows the Dataverse tables, messages, parameters and error code. Whether each detail
matches a real Dataverse is not verified. The
Microsoft documentation describes the feature in
[Detect duplicate data using code](https://learn.microsoft.com/power-apps/developer/data-platform/detect-duplicate-data-with-code),
[Enable and disable duplicate detection](https://learn.microsoft.com/power-apps/developer/data-platform/enable-disable-duplicate-detection),
[Run duplicate detection](https://learn.microsoft.com/power-apps/developer/data-platform/run-duplicate-detection) and the
[duplicate rule tables](https://learn.microsoft.com/power-apps/developer/data-platform/duplicaterule-entities). This page says
what xrmverse does and where it stops.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Tables, messages, parameters, error code | Rules, conditions, `RetrieveDuplicates`, `BulkDetectDuplicates`, the check on Create/Update | Same |
| Rules in solutions | Not carried (kept as XML and written back, with a warning) | **Narrower** |
| Default rules | None | **Narrower**: Dataverse ships rules for some tables |
| Operator 5 (Exact Match, pick list label) | Refused | **Narrower**: use operator 6 |
| Match codes | SHA-256 hashes, no length limit | **Differs**: can find duplicates Dataverse misses on long values |
| Bulk jobs | Stop at 5000 `duplicaterecord` rows; no email, no recurrence | **Narrower** |
| `Compound*` messages | Not implemented | **Narrower** |
| Import and offline switches | Stored, no effect | **Narrower** |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Switch the check on | Organization, online and table switches | [The three switches](#the-three-switches) |
| Define a rule | `xrmd duplicate-detection rule create`, web admin, Web API | [Rules](#rules) |
| Make a rule active | Publish it | [Publishing and unpublishing](#publishing-and-unpublishing) |
| Check on a Create or Update | `SuppressDuplicateDetection` false / `MSCRM.SuppressDuplicateDetection` | [Asking for the check](#asking-for-the-check) |
| Find duplicates of one row, or of many | `RetrieveDuplicates`, `BulkDetectDuplicates` | [RetrieveDuplicates](#retrieveduplicates), [BulkDetectDuplicates and duplicaterecord](#bulkdetectduplicates-and-duplicaterecord) |

## How it works

Publishing a rule gives it a table `meta.matchcode_{ruleid}` that holds one **match code** for each row of
the rule's matching table: the values of the rule's conditions are normalized (trimmed, case-folded, cut to the first or last
N characters, and so on), joined in the order of the conditions and hashed with SHA-256. Two rows are duplicates for the rule
when their codes are equal, so a lookup is an index seek and never a scan. Every core write (Create, Update, SetState, Assign,
Delete, the cascades of a Delete and the writes of a plug-in) refreshes or removes the codes of the rows it touches in the same
transaction, so a refused or rolled-back write leaves no code behind. A lookup joins the data table, so the code of a row
deleted outside the server (a raw `DELETE`) never produces a match. The tables are registered in `meta.matchcode_table`
(migration `0009_duplicate_detection.sql`); at startup any `meta.matchcode_*` table whose rule is not Publishing or Published
is dropped.

## The three switches

The check on a Create or Update runs only when **all three** are on, and when the client asks for it (see
[Asking for the check](#asking-for-the-check)).

| Switch | Where | Default |
|---|---|---|
| **Organization** | `organization.isduplicatedetectionenabled` | off |
| **Online create and update** | `organization.isduplicatedetectionenabledforonlinecreateupdate` | off |
| **Table** | `EntityMetadata.IsDuplicateDetectionEnabled` (a managed boolean) | off, on every table |

Set the two organization columns with a PATCH of the `organizations` row (or `xrmd duplicate-detection enable|disable`, or the
switches of the web admin page). Set the table flag with `UpdateEntity` (or `PUT EntityDefinitions(LogicalName='…')`), at
`CreateEntity`, with `xrmd duplicate-detection enable --table <table>`, with the **Duplicate detection** switch on the table's
page in the web admin, or by importing a solution (`<IsDuplicateCheckSupported>`, see [Solutions](solutions.md)). A rule can be
written only for tables with the flag on.

- **Turning a switch off unpublishes.** Turning `isduplicatedetectionenabled` off unpublishes every rule; turning the table flag
  off unpublishes the rules that have the table as their base or matching table. Both happen in the same transaction as the
  change. Turning a switch back on republishes nothing: publish the rules again.
- **Not every table can have the flag.** Custom tables can, and so can the seeded `account` and `contact`; intersect tables and
  every other system table cannot, and setting the flag on one is a 400. An import that carries `<IsDuplicateCheckSupported>1`
  for such a table does not apply it and logs a warning.
- **Import and offline.** `isduplicatedetectionenabledforimport` and `isduplicatedetectionenabledforofflinesync` are stored and
  returned but change nothing: xrmverse has no bulk data import and no offline client.
- **Defaults.** No rules are seeded and all four organization switches are off in a new organization (see
  [Known limits](#known-limits)).

## Rules

A rule is a row of `duplicaterule` (entity set `duplicaterules`) with its conditions in `duplicateruleconditions`. The
documented columns:

| Table | Columns |
|---|---|
| `duplicaterule` | `name`, `description`, `baseentityname`, `matchingentityname` (both required), `excludeinactiverecords`, `iscasesensitive`, `uniquename`; read-only: `baseentitytypecode`, `matchingentitytypecode`, `baseentitymatchcodetable`, `matchingentitymatchcodetable`; `statecode` and `statuscode` |
| `duplicaterulecondition` | `baseattributename`, `matchingattributename`, `operatorcode`, `operatorparam`, `ignoreblankvalues`, `regardingobjectid` (the rule; required), `uniquerulename` (read-only) |

Case sensitivity is a setting of the **rule** (`iscasesensitive`), as in the Microsoft table reference, not of a condition.

**State.** A new rule is `statecode` 0 (Inactive) and `statuscode` 0 (Unpublished). Publishing makes it 0/1 (Publishing) and
then 1/2 (Published). `statecode` and `statuscode` change only through `PublishDuplicateRule` and `UnpublishDuplicateRule`; a
Create, Update or SetState that changes them is a 400.

**Operators** (`operatorcode`):

| Code | Meaning | Column types | `operatorparam` |
|---|---|---|---|
| 0 | Exact match | String, Memo, numbers, Boolean, Picklist/State/Status (the value), Lookup/Owner/Customer (the id) | not allowed |
| 1 | Same first characters | String, Memo | required, 1 or more |
| 2 | Same last characters | String, Memo | required, 1 or more |
| 3 | Same date | DateTime | not allowed |
| 4 | Same date and time | DateTime | not allowed |
| 5 | Exact match (pick list label) | any | **refused**, see [Known limits](#known-limits) |
| 6 | Exact match (pick list value) | Picklist, State, Status | not allowed |

**What the server checks** (every refusal is a 400 with the offending value named):

- On a rule Create, both tables exist and have `IsDuplicateDetectionEnabled`. The two table names cannot change afterwards.
- A client value for a column the server fills (`baseentitytypecode`, `matchingentitytypecode`, the two match code table names,
  `uniquerulename`) is refused. The server sets the type codes from the table names, `uniquename` to
  `duplicaterule_<id without dashes>` when absent, and each condition's `uniquerulename` from its rule.
- A condition needs both columns to exist and not be virtual, file, image or multi-select, of compatible types (the same type;
  String and Memo are compatible; Lookup and Customer need a common target), with an operator that fits the type and
  `operatorparam` as in the table above.
- A rule in Publishing or Published cannot be updated, and its conditions cannot be created, updated or deleted: unpublish it
  first. (The relationship `DuplicateRule_DuplicateRuleConditions`, an Associate or Disassociate, gives the same answer; a
  Disassociate of a condition is always refused because a condition needs its rule.)
- Writing a condition needs **Write on its rule**, as well as the privilege on `duplicaterulecondition`.
- `DeleteAttribute` of a column a condition names is a 400. `DeleteEntity` of a table a rule names deletes those rules, their
  conditions and their match code tables in the same transaction. Deleting a rule in any state is allowed and drops its match
  code table.

**Matching rules, in short.** Text is trimmed and, unless the rule is case-sensitive, compared case-insensitively (the invariant
upper-case form). Operators 1 and 2 compare the first or last N characters, or the whole value when it is shorter. Operator 3
compares the UTC date, operator 4 the UTC value to the second. Numbers compare as numbers (a decimal is rounded to its column's
precision, money to four digits, rounding away from zero as SQL Server does). A **blank** value is null or an empty string after
trimming. With one condition, blanks are always ignored (a row with a blank value has no code and matches nothing; Learn treats
`ignoreblankvalues` false as true then). With several conditions, a blank on a condition with `ignoreblankvalues` true gives the
row no code at all; a blank on one with false counts as a value of its own, so two rows blank in the same place match there.

At most **5** rules in Publishing or Published per base table.

## Publishing and unpublishing

`PublishDuplicateRule` (SDK request `PublishDuplicateRuleRequest`; Web API action bound to `duplicaterules(<id>)`) checks, in
this order: the rule exists (404); it is not already Publishing (400, "wait for its publish job"); `isduplicatedetectionenabled`
is on; both tables have the flag; the rule has at least one condition; fewer than five other rules are Publishing or Published
for the base table; and, with `excludeinactiverecords`, the matching table's `statecode` has no option other than 0 and 1
(xrmverse treats only 0 as active). Each refusal is a 400. Then, in one transaction, the rule becomes Publishing (0/1), its
match code table is created, and a **system job** is queued: an `asyncoperation` with `operationtype` 7 (Duplicate Detection
Rule Publish), `regardingobjectid` the rule, owned by the caller. The response is the job id (SDK `JobId`) or the
`asyncoperation` (Web API, with `asyncoperationid` and `operationtype`; it is not read back, so a caller without Read on
`asyncoperation` still gets the id).

The job fills the codes of every row of the matching table in batches of 5000, then sets the rule to Published (1/2). Writes made
while the job runs keep the table up to date themselves, so the job never overwrites a fresher code. If the job fails, the rule
goes back to Unpublished (0/0) and its table is dropped; the same happens when you **cancel** the job before it runs (an Update
of the `asyncoperation` to `statecode` 3, `statuscode` 32), and at startup to a rule that is Publishing with no running job. A
rule already Published can be published again: it is rebuilt. A Publishing rule is **never used by a lookup**, because its codes
may be incomplete.

**Each job belongs to one publish.** The publish registers the rule's match code table with the job's id, and every write of
the job (each batch, the move to Published, the reset after a failure) happens only while the table is still registered with
that job. So if you unpublish a rule while its job runs (or a switch-off does), then edit it and publish it again, the old job
stops at its next step without writing anything: it ends **Succeeded** (3/30) with the message "The duplicate detection rule
was unpublished, published again or deleted before this publish job ended: the job stopped without changing it." Only the
new job fills the new table and publishes the rule. The same happens to a job whose rule was deleted.

`UnpublishDuplicateRule` runs synchronously: the rule becomes 0/0, its match code table is dropped and the registry row
removed. The SDK response is empty and the Web API answer is 204. (Dataverse's Web API page says the call submits an
asynchronous job; whether it returns one is on the checklist.) Publishing and unpublishing need Write on the rule.

```bash
xrmd duplicate-detection enable                       # the organization switch
xrmd duplicate-detection enable --create-update       # the create/update switch
xrmd duplicate-detection enable --table contact       # the flag of a table
xrmd duplicate-detection rule create --name "Same email" --base contact --matching contact \
    --condition emailaddress1:exact
xrmd duplicate-detection rule publish "duplicaterule_<id>" --wait
```

The same in the Web API:

```http
POST /api/data/v9.2/duplicaterules(<ruleid>)/Microsoft.Dynamics.CRM.PublishDuplicateRule

POST /api/data/v9.2/UnpublishDuplicateRule
{ "DuplicateRuleId": "<ruleid>" }
```

## Asking for the check

A Create or Update does the check only when the request asks for it. **The default is no check**, as in Dataverse for SDK and
Web API calls.

- **SDK.** Send the optional parameter `SuppressDuplicateDetection` = `false` on a `CreateRequest` or `UpdateRequest`.
  `IOrganizationService.Create` and `Update` (without a request object) can never ask for it. A value that is not a Boolean is a
  400. On any other message the parameter is ignored.
- **Web API.** Send the header `MSCRM.SuppressDuplicateDetection: false` on a POST or PATCH of a record (any letter case; a
  value other than `true` or `false` is a 400).

```csharp
var create = new CreateRequest { Target = contact };
create.Parameters["SuppressDuplicateDetection"] = false;
try { service.Execute(create); }
catch (FaultException<OrganizationServiceFault> ex) when (ex.Detail.ErrorCode == unchecked((int)0x80040333))
{
    // a duplicate: show it, or retry with SuppressDuplicateDetection = true
}
```

```http
POST /api/data/v9.2/contacts
MSCRM.SuppressDuplicateDetection: false
Content-Type: application/json

{ "lastname": "Rossi", "emailaddress1": "mario.rossi@example.com" }
```

**The answer** when a duplicate exists is error code **`0x80040333`** (`-2147220685`): HTTP **412** on the Web API, a SOAP fault
with the same `ErrorCode` over the SDK. The message text is xrmverse's own, so switch on the code. Learn shows 500 on one page
and 412 on another for this error; xrmverse takes 412 (the checklist records the real status).

**When the check runs.** All of these hold: the two organization switches are on; the table has the flag; at least one
**Published** rule has the table as its base table; the request asks for it. It runs inside the Create or Update, on the final
values after the pre-operation (stage 20) plug-ins, in the write's transaction (so a plug-in that rolls the write back, or the
duplicate error itself, leaves no match code). For a Create the check uses the values written. For an Update it uses the stored
row overlaid with the changes, and a rule counts only when the update **changes the row's match code for that rule**: updating
an unrelated column of a row that already duplicates another never fails (the check does not refuse every later edit of an
existing duplicate pair). The row itself is excluded. A plug-in that calls `Execute(CreateRequest)` on its service with the
parameter set gets the check too.

An Update that changes only `statecode` or `statuscode` (a reactivation) is not checked again: it can bring back a row that
duplicates an active one. (Whether Dataverse checks it is on the checklist.)

## RetrieveDuplicates

`RetrieveDuplicates` finds the rows that duplicate a given row. It does **not** depend on the three switches, only on
published rules (turning a switch off already unpublished them).

| Parameter | Meaning |
|---|---|
| `BusinessEntity` | An `Entity`. When it carries the id of an existing row, that row's stored values are read and the attributes you give overlay them (the Web API example sends only the id); the row itself is left out of the matches. Without an id, only the given attributes are used. |
| `MatchingEntityName` | The table to look in. |
| `PagingInfo` | `Count` 1 to 5000 and `PageNumber` 1 or more; anything else is a 400. Absent, or a `Count` or `PageNumber` of 0, means page 1 of 5000. Paging cookies are not used; the response says `MoreRecords`. |

Only **Published** rules from the table of `BusinessEntity` to `MatchingEntityName` count. With none, the answer is an empty
collection, not an error. The caller needs Read on the base table, and on the stored row when the id is given (the lookup fails
closed). The matches are then read **as the caller** through the same path as `RetrieveMultiple` (all columns), so record
security and [field-level security](field-security.md) apply, and a match the caller cannot read is left out before paging.
A rule's conditions are filters on the matched rows, so the rule of field security for query conditions applies to them: when
an applicable Published rule compares a **secured column the caller cannot read** (on the base or the matching side), the
request is refused with **403** `0x8004F504` (FieldReadDenied), as a `RetrieveMultiple` filtering on that column would be. A
field share on some rows does not lift it.
(The Create/Update check, in contrast, counts every matching row whatever the caller may read, because its error carries no
data.) Rules with `excludeinactiverecords` leave out the rows whose `statecode` is not 0.

```csharp
var request = new RetrieveDuplicatesRequest
{
    BusinessEntity = contact,                 // new Entity("contact") with emailaddress1, or just the id
    MatchingEntityName = "contact",
    PagingInfo = new PagingInfo { Count = 50, PageNumber = 1 },
};
var duplicates = ((RetrieveDuplicatesResponse)service.Execute(request)).DuplicateCollection;
```

```http
GET /api/data/v9.2/RetrieveDuplicates(BusinessEntity=@p1,MatchingEntityName=@p2,PagingInfo=@p3)
    ?@p1={"@odata.type":"Microsoft.Dynamics.CRM.contact","contactid":"<id>"}
    &@p2='contact'&@p3={"PageNumber":1,"Count":50}
```

The Web API answer is a collection of the matching table with `@Microsoft.Dynamics.CRM.morerecords` true when another page
exists. Each parameter can be given inline or as an `@alias`.

## BulkDetectDuplicates and duplicaterecord

`BulkDetectDuplicates` starts a system job that looks for duplicates among many rows and records what it finds.

| Parameter | Meaning |
|---|---|
| `Query` | A `QueryExpression` or `FetchExpression` for the rows to examine. In the Web API a `QueryExpression` carries only `EntityName` (any other property is refused: send a `FetchExpression` to filter). An aggregate query is a 400. |
| `JobName` | The job's name. |
| `SendEmailNotification`, `RecurrencePattern` | `true` and a non-empty pattern are **refused** (400): xrmverse sends no email and runs no recurring jobs. |
| `TemplateId`, `ToRecipients`, `CCRecipients`, `RecurrenceStartTime` | Accepted and ignored. |

The query's table needs the flag and at least one Published rule with it as base table, and the caller needs Read on it (all
400/403 otherwise). The response is `JobId`: an `asyncoperation` with `operationtype` 8 (Bulk Duplicate Detection), owned by the
caller, `primaryentitytype` the table. The job runs the query **as its owner**, page by page; for each row and each Published
base rule that finds at least one match (in any matching table), it writes **one `duplicaterecord` row** with `baserecordid` the
row, `duplicateruleid` the rule, `asyncoperationid` the job, owner the job's owner and `duplicaterecordid` null (as in Learn's
sample). It stops at **5000** rows and says so in the job's `message` ("Stopped after 5000 duplicate records … Narrow the query to
see the rest"); the rows after that point are not examined.

Read the result from `duplicaterecords` filtered by the job:

```http
GET /api/data/v9.2/duplicaterecords?$filter=_asyncoperationid_value eq <jobid>
```

`$expand` of the collection-valued navigation property `AsyncOperation_DuplicateBaseRecord` of `asyncoperations` is not served
(a general Web API gap, not specific to this feature): use the `$filter` above. `duplicaterecord` is user-owned, so a user reads
the rows of their own jobs, and others by depth. Deleting a job deletes its `duplicaterecord` rows (Cascade); deleting a rule
removes the link only (RemoveLink). Both cascades are from memory (checklist).

```bash
xrmd duplicate-detection bulk --table contact --name "Contacts" --wait    # --timeout minutes, default 30
xrmd duplicate-detection find --table contact --id <id>                   # RetrieveDuplicates
```

## Privileges and security

The tables get privileges like every table (`prvCreateDuplicateRule`, `prvReadDuplicateRule`, …). System Administrator has all;
System Customizer has every privilege on `duplicaterule` and `duplicaterulecondition` and Read on `duplicaterecord`, at Global
depth (an organization seeded earlier gets them at startup). `duplicaterulecondition` is organization-owned in xrmverse (so it
has privileges); Learn says ownership "none". Publishing and unpublishing need Write on the rule; there is no separate publish
privilege (the checklist asks whether Dataverse has one). See [Security](security.md).

## Plug-ins

`PublishDuplicateRule`, `UnpublishDuplicateRule`, `RetrieveDuplicates` and `BulkDetectDuplicates` are core messages (so
privileges and transactions apply, and a plug-in can call them through its service) **without** `sdkmessage` rows: a step cannot
be registered on any of them (400, unknown message), and they are not audited. The Create and Update steps of the written table
run as usual; the duplicate check happens after the stage 20 steps and before the insert or update (see
[Plug-ins](plugins.md)).

## Known limits

Anything xrmverse cannot do faithfully is refused, not emulated:

- **Operator 5 (Exact Match, pick list label)** is refused when a condition is written: labels depend on the user's language and
  the core has no per-user label resolution. Use operator 6 (the option value).
- **No rule import or export.** A solution's `<DuplicateRules>` node is kept byte for byte and written back on export, with a
  warning in the import job log, but no rule is created. `AddSolutionComponent` with the rule component type is the usual
  "Component type … is not supported" 400. `IsDuplicateCheckSupported` of a table **is** modelled (read on import, written on
  export; an organization that kept it as opaque XML from an earlier version is converted once at startup). Create the rules
  again with the Web API, `xrmd duplicate-detection rule create` or the web admin.
- **No default rules.** Dataverse ships rules for some tables; xrmverse seeds none, and all four organization switches start off.
- **Import and offline switches** are stored and returned, with no effect.
- **`CompoundUpdateDuplicateDetectionRule` and `CompoundCreate`** are not implemented (the usual unknown-message 400): create
  the rule with a Create, then each condition with its own Create. Those writes are separate transactions. `xrmd
  duplicate-detection rule create` deletes the rule again when a condition is refused; the web admin dialog keeps what was
  saved, shows the server's message and a note that the rule may be partly saved, and lists the conditions the server holds so
  you can fix them and save again; your own calls are yours to clean up.
- **Hashed match codes.** Dataverse limits the length of a match code, which can hide a duplicate in very long values; a SHA-256
  hash has no such limit, so xrmverse can find duplicates Dataverse misses on long values.
- **Concurrent creates.** No lock is taken: two simultaneous creates of the same values can both succeed (Learn says the same of
  Dataverse). Match code upkeep itself is transactional.
- **Multi-row writes can deadlock** against a publish or bulk job batch, because the match code table is read under
  `REPEATABLE READ` while it is being filled. SQL Server then answers error 1205 to one of them; the publish job retries its
  batch, and a client write gets the explicit SQL error and can retry.
- **Reads in the check.** The Create/Update check counts every candidate row without looking at the caller's access (see above).
- **`RetrieveDuplicates` collects every candidate id** of the published rules (from the match code tables) before it pages, and
  then reads the matches as the caller in chunks of 1000 until the page is full. A rule that matches very many rows costs a
  lookup of all their ids on each page request.
- **Bulk jobs** stop at 5000 `duplicaterecord` rows and run one query as one owner; no email, no recurrence.
- **`$expand` of `AsyncOperation_DuplicateBaseRecord`** is not served; filter `duplicaterecords` instead.
- **`uniquerulename` of the conditions.** Changing a rule's `uniquename` rewrites `uniquerulename` on its conditions with a
  direct SQL update, not a write through the pipeline: no plug-in step fires for it and it does not touch their `modifiedon`.
- **Statecode-only updates** are not re-checked (see above).
- **A rule that is Publishing** is invisible to lookups until the job ends. Cancelling its job before it runs puts the rule
  back to Unpublished at once; a job can only be deleted once it has completed, by which time it has published or reset the
  rule. A rule left Publishing in any other way (a process that stopped between a failure and its reset) is reset by the
  startup sweep; until then, `UnpublishDuplicateRule` is the workaround.
- **Field security in bulk jobs.** `BulkDetectDuplicates` reads its rows as the job's owner, but its lookup compares the rule's
  columns whatever field security says (as the Create/Update check). It records only that a match exists (a `duplicaterecord`
  row without the matching row's id), so it reveals less than `RetrieveDuplicates`, which refuses such rules.
- **`duplicaterecord.baserecordid` is not cleaned up** when its base row is deleted: the reference then names a missing row.
  Dataverse removes such rows with the base table's relationships; xrmverse has no per-table relationship for this dynamic
  lookup. Delete the job (its `duplicaterecord` rows go with it) or the rows themselves.
- **A bulk job fails as a whole** (3/31) when one of its base rows or its rule is deleted while it runs: writing the
  `duplicaterecord` checks its lookups (404). The rows written before that point stay. Run the job again.
- **The rule cache can briefly see an uncommitted rule.** The cache of published rules is loaded through the session that needs
  it, so a session that wrote rules inside its own open transaction can show them to other sessions until it commits or rolls
  back (which reloads the cache). With the current messages (no `ExecuteTransaction`, no plug-in steps on publish) this is hard
  to reach.
- **Plug-in steps.** None on the four messages (see above).

## See also

- [Web admin](web-admin.md#duplicate-detection)
- [CLI xrmd](cli.md#duplicate-detection)
- [Security](security.md)
