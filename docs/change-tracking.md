# Change tracking

[← xrmverse documentation](../README.md#documentation)

## Introduction

Change tracking lets a client that keeps a copy of a table (a sync job, a data warehouse load, an offline cache) ask for
"what changed since my last call" instead of reading everything again. xrmverse offers it through the
`RetrieveEntityChanges` message of the Dataverse SDK and through the Web API delta queries. Whether each detail matches a
real Dataverse is not verified. The Microsoft
documentation describes the feature in
[Use change tracking to synchronize data with external systems](https://learn.microsoft.com/power-apps/developer/data-platform/use-change-tracking-synchronize-data-external-systems),
[RetrieveEntityChangesRequest](https://learn.microsoft.com/dotnet/api/microsoft.xrm.sdk.messages.retrieveentitychangesrequest)
and the [organization table reference](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/organization)
(`ExpireChangeTrackingInDays`). This page says what xrmverse does and where it stops.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| APIs | `RetrieveEntityChanges` (SDK, SOAP) and Web API delta queries | Same |
| Switching on | `ChangeTrackingEnabled`, one-way | Same |
| Tables that can be tracked | All except intersect tables and some system tables, `audit` included | **Narrower**: Dataverse tracks `audit` |
| Retention of deletions | `expirechangetrackingindays`, default 7 days | Same |
| Expired tokens | Refused with a 400, never a partial delta; every token issued before a database restore is expired | Same principle; error codes and texts are xrmverse's own |
| Paging cookie, skip token | xrmverse's own format | **Differs**: treat them as opaque |
| Privilege | Global read on the table | Same (organization-level read) |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Switch a table on | `xrmd change-tracking enable <table>`, `UpdateEntity`, web admin, solution | [Switching a table on](#switching-a-table-on) |
| Sync with the SDK | `RetrieveEntityChangesRequest` | [RetrieveEntityChanges (SDK, SOAP)](#retrieveentitychanges-sdk-soap) |
| Sync with the Web API | `Prefer: odata.track-changes`, then the delta link | [Web API delta queries](#web-api-delta-queries) |
| Set how long deletions are kept | `xrmd change-tracking retention <days>` | [Retention and expired tokens](#retention-and-expired-tokens) |

## How it works

Every data table has `versionnumber`, a SQL Server `rowversion`: a database-wide counter that grows with
every insert and update. A token is a version. A read for "changes after token T" returns the rows with a version above T
and below `MIN_ACTIVE_ROWVERSION()`, the lowest version a transaction still open could take. Every version below that bound
is committed, so a transaction that took its version early and commits late is never skipped: its row shows up in the next
call. The token a read returns is that bound minus one. Deleted rows leave no row behind, so a tracked table also writes
one row per deletion to the log `meta.deleted_record`, in the same transaction as the delete, with its own version on the
same counter. Updates and deletions are therefore ordered on one clock.

## Switching a table on

`ChangeTrackingEnabled` of a table is off by default, on every table: no seeded table is tracked out of the box. Turn it on
with `UpdateEntity` (or `PUT EntityDefinitions(LogicalName='…')`), at `CreateEntity`, with `xrmd change-tracking enable
<table>` (see [CLI](cli.md#change-tracking)), with the **Track changes** switch on the table's page in the web admin (see
[Web admin](web-admin.md#change-tracking)) or by importing a solution (see [Solutions](solutions.md)). Whoever may change table
metadata may do it.

| Tool | How |
|---|---|
| SDK, Web API | `UpdateEntity`, `PUT EntityDefinitions(LogicalName='…')`, or at `CreateEntity` |
| CLI | `xrmd change-tracking enable <table>` (see [CLI](cli.md#change-tracking)) |
| Web admin | The **Track changes** switch on the table's page (see [Web admin](web-admin.md#change-tracking)) |
| Solutions | `<ChangeTrackingEnabled>` (see [Solutions](#solutions)) |

- **It is one-way.** Sending `ChangeTrackingEnabled` false for a tracked table is a 400, as Dataverse does not let you turn
  it off again. Sending true again (or a retrieved definition) is a no-op.
- **Not every table can be tracked.** `EntityMetadata.CanChangeTrackingBeEnabled` (a managed boolean, computed on every
  read, never stored) says whether it can. It is false for intersect tables and for the system tables whose rows xrmverse
  deletes directly, outside the path that writes the deletion log: `audit`, `principalobjectaccess`,
  `principalobjectattributeaccess`, `fileattachment`, `solutioncomponent`, `fieldpermission`,
  `sdkmessageprocessingstepimage`, `savedquery`, `systemform`, `privilege`, `sdkmessagefilter`,
  `sdkmessageprocessingstep`, `activitypointer` and `activityparty` (see [Activities and notes](activities.md)), and
  `asyncoperation` (a succeeded job of a step with `asyncautodelete` is deleted by the async worker), `appmodulecomponent`
  (the solution import replaces an app module's components). Setting the flag on one
  of them is a 400. Every other table, seeded or custom, can be tracked.
  A test fails if a later change adds a direct delete on a table that is not in that list.
- **What happens when a table is switched on.** The server records the version at which tracking started
  (`meta.change_tracking_table`) and creates the index `IX_{table}_versionnumber` when it is missing, so that a delta read
  seeks instead of scanning. An organization whose tables were switched on by an earlier import gets both at startup.
- **Deleting the table** (`DeleteEntity`) removes its tracking rows and its deletion log in the same transaction.

## RetrieveEntityChanges (SDK, SOAP)

`RetrieveEntityChangesRequest` works over the SDK and SOAP, from a plug-in's service and through `ExecuteMultiple` and
transactions as any other message.

| Parameter | What it does |
|---|---|
| `EntityName` | The tracked table. An unknown table, or one with change tracking off, is a 400. |
| `Columns` | A `ColumnSet` (`AllColumns` allowed) for the new and updated rows. It goes through the same path as `RetrieveMultiple`, so an unknown column is the same 400 and a secured column is masked exactly as in a read (see [Field-level security](field-security.md)). Every row carries its id and its `RowVersion`. |
| `DataVersion` | The `DataToken` of the previous call. None: the first sync (see below). |
| `PageInfo` | `Count` (default and maximum 5000; another value is a 400), `PageNumber` and `PagingCookie`. |
| `GetGlobalMetadataVersion` | True fills `GlobalMetadataVersion` with the current metadata version stamp (as a string); otherwise it is null. |

The response holds `EntityChanges` (`BusinessEntityChanges`): `Changes` (a collection of `NewOrUpdatedItem` and
`RemovedOrDeletedItem`), `DataToken`, `MoreRecords` and `PagingCookie`.

### The sync loop

Call without `DataVersion`: every row comes back as a `NewOrUpdatedItem` and no deletion (the client has
nothing to remove). Keep the `DataToken` of the last page. On the next sync send it as `DataVersion`: the response holds the
rows created or updated since, then one `RemovedOrDeletedItem` per row deleted since. A row created and deleted between two
syncs comes back as the deletion only, even though the client never had it. Store the new token after each complete sync.

```csharp
var request = new RetrieveEntityChangesRequest
{
    EntityName = "account",
    Columns = new ColumnSet("name"),
    DataVersion = savedToken,          // null the first time
    PageInfo = new PagingInfo { Count = 1000, PageNumber = 1 },
};
var changes = ((RetrieveEntityChangesResponse)service.Execute(request)).EntityChanges;
// repeat with PageNumber + 1 and changes.PagingCookie while changes.MoreRecords
savedToken = changes.DataToken;
```

### The token

`DataToken` is `{version}!{yyyy-MM-dd HH:mm:ss}`: a decimal version, `!`, and the UTC time the token was
issued (to the second). The version says where the next read starts; the time is read for expiry only (see
[Retention and expired tokens](#retention-and-expired-tokens)). Treat the whole string as opaque. A `DataVersion` whose
version does not parse is a 400.

### Paging

The first page fixes the upper version bound, and every page of that read answers the same `DataToken`, so a
write between two pages moves no row from one page to another (it shows up in the next sync). Pages walk the new and
updated rows by version, then the deletions by log version, so one page may hold both when the updates end in the middle of
it. `MoreRecords` is true while either kind has rows left. Send the `PagingCookie` of the page before with the next page
number; a page number above 1 without a valid cookie is a 400, and so is a cookie from another read. **The format of the
cookie is xrmverse's own** (`{bound}!{u|d}!{version}!{issued}`) and may change: do not parse it.

### Privilege

The caller needs `prvRead` on the table at **Global** depth (the Microsoft documentation asks for
organization-level read access); anything less is a 403. SYSTEM passes. At Global depth no row is filtered out by owner or
sharing.

### Plug-ins

`RetrieveEntityChanges` has an `sdkmessage` row without a table filter, so steps can be registered on it (a
step fires for every table; the plug-in sees the table as `PrimaryEntityName`, and the response is in
`OutputParameters["EntityChanges"]`). The read runs at stage 30, like `RetrieveMultiple`. It is not audited.

## Web API delta queries

Over the Web API a tracked table's entity set answers the OData change tracking protocol. The delta runs as a
`RetrieveEntityChanges` request through the same organization pipeline, so the Global read guard, the plug-in steps and the
errors are those of the SDK message.

1. `GET /api/data/v9.2/accounts?$select=name` with the header `Prefer: odata.track-changes` returns every row, the header
   `Preference-Applied: odata.track-changes` and an `@odata.deltaLink`: the request URL with `$deltatoken={DataToken}`
   (URL-encoded) added. `$metadata` marks the entity sets of tracked tables with the
   `Org.OData.Capabilities.V1.ChangeTracking` annotation (`Supported` true).
2. `GET` the delta link (no header needed) returns the rows created or updated since, and one entry for each deletion:

   ```json
   {
     "@odata.context": "https://org.example.com/api/data/v9.2/$metadata#accounts/$deletedEntity",
     "id": "3b1f0a4e-8c5d-4f43-9a4e-1f6d2c9b7a10",
     "reason": "deleted"
   }
   ```

   `@odata.context` of the response ends in `/$delta`, and the response carries a new `@odata.deltaLink`.
3. **Pages.** More than 5000 changes, or `Prefer: odata.maxpagesize=n` (1 to 5000), give an `@odata.nextLink` that keeps the
   `$deltatoken` (when there is one) and adds a `$skiptoken`. Follow the link as written: the skip token is
   `<cookie pagenumber="N" pagingcookie="…" istracking="True" />`, the shape of a FetchXML paging cookie, and `istracking`
   makes the next page a delta page even without the header. On a first sync that spans several pages there is a
   `$skiptoken` and no `$deltatoken` until the last page. The `@odata.deltaLink` comes on the last page only.
4. **Counting.** `$count=true` on a delta adds `@odata.count`, the number of changes across all pages. `GET
   accounts/$count?$deltatoken=…` answers the same number as `text/plain`; it needs a `$deltatoken` and no `$skiptoken`.
5. **Options.** `$select` and `$count` combine with tracking. `$filter`, `$orderby`, `$expand`, `$top`, `$apply` and
   `fetchXml`, with the header or with a `$deltatoken`, are a 400, and so is any other `$` option xrmverse does not know in
   this context. A table without change tracking refuses the header, `$deltatoken` and a tracking `$skiptoken` with a 400. An
   empty `$deltatoken` and a repeated one are a 400.

## Retention and expired tokens

Deleted rows cannot be kept forever. `organization.expirechangetrackingindays` (Integer, 0 to 365, default **7**, as in
Dataverse; a value outside the range is a 400) says how many days the deletion log is kept. Set it with an Update of the
organization row, `xrmd change-tracking retention <days>` or the Admin API.

### Purge

A hosted service purges the log every hour (the first run one minute after the server starts). It deletes the rows older than
the setting in batches of 5000 and, in the same transaction, raises `purged_version` in `meta.change_tracking_state` to the
highest version it removed. After a purge, a client that has not synced for longer than the retention can no longer be told
what was deleted.

### When a token is expired

A token is **expired**, and the call is a 400 `InvalidArgument` that tells the client to sync again
without a token, when any of these holds:

- its version is below `purged_version` (deletions after it may be gone);
- its version plus one is below the version at which the table was switched on (deletions from before tracking started were
  never logged);
- its version is above the current bound minus one, a version this database has not reached yet (the token comes from
  another database, or from this one before it was restored from an older backup);
- **age:** its time is older than `expirechangetrackingindays` days, as in Dataverse, whether or not a purge has run since. A
  token without a readable time is expired too, as its age is unknown;
- **restore:** its time is before the last restore of the database from a backup. A restore (`xrmd org restore`, the Admin
  API) records its time in `meta.change_tracking_state.restored_at` of the restored database, so **every token issued before
  a restore is expired** and each client does a full sync (no `DataVersion`) once. Versions in the restored database go back
  to the backup's and would be reused by new writes, so an older token could otherwise skip them. A token issued right after
  the restore is valid at once: a new token is never dated before `restored_at`.

An expired token is always refused, never answered with a silent partial delta. The token of a sync made right after a table
was switched on is valid. With `expirechangetrackingindays` 0, every logged deletion is purged at the next run and every
token expires as soon as its second has passed, so delta syncs are effectively off: we read 0 literally, not as "never". The
token time, the age rule and `restored_at` all use the database's UTC clock (`SYSUTCDATETIME()`), so the clock of the xrmd host
does not matter.

## Solutions

`<ChangeTrackingEnabled>` of a table in `customizations.xml` is read and written, and the export also writes the computed
`CanChangeTrackingBeEnabled`. Importing `1` switches a table on; importing `0` never switches it off: the table stays on and
the import logs a warning. See [Solutions](solutions.md).

## Existing databases and currencies

The startup upgrade for [currencies](currencies.md#existing-databases) sets a currency and rate 1 on the rows of tables that
had money columns before it. That is a plain SQL update, so `versionnumber` moves: a client tracking such a table sees each of
those rows once as updated, with no real change.

## Known limits

None of these is silent: each is refused with an error or stated here.

- **Ineligible tables** (above): `ChangeTrackingEnabled` is a 400 on them. In particular **`audit` is not tracked**, although
  Dataverse tracks it.
- **The paging cookie and the skip token are ours.** Their shape is not Dataverse's; treat both as opaque. Neither is signed
  (nor is the `DataToken`): the issue time page 1 puts in the cookie is the one every later page's `DataToken` carries, so a
  client that edits the cookie changes the time its own token reports, exactly as editing the `DataToken` itself would.
- **`RetrieveEntityChanges` is not audited**, as a read is not.
- **Changes of ownership and sharing.** `Assign` updates the row, so it is a `NewOrUpdatedItem`. Whether `Share` shows up as
  a change (it changes no column of the row) is not verified against a real Dataverse.
- **Rows the caller may not read.** The read needs Global read, so a caller sees every row of the table; there is no
  per-row filter.
- **No deletion for a table that was off.** Rows deleted before a table was switched on are not logged and never come back
  as deletions.
- **A delete at the instant tracking is switched on.** A delete that runs in the instant between enabling tracking on a table
  and the publish of that metadata may not be logged, so a first sync started at that instant can keep a row that was deleted
  then (it never comes back as a deletion). Sync again from scratch if a table was switched on while rows were being deleted.
- **Error codes and texts are ours.** Dataverse's codes for an expired token, an untracked table and the like are not
  documented; xrmverse answers a generic 400 `InvalidArgument` (403 for the privilege) with its own text, and the checklist
  records the real ones.

## See also

- [Supported queries](queries.md), [Bulk operations](bulk-operations.md)
- [Solutions](solutions.md), [CLI xrmd](cli.md#change-tracking), [Web admin](web-admin.md#change-tracking)
