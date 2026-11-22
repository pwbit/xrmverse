# Auditing

[← xrmverse documentation](../README.md#documentation)

## Introduction

Auditing keeps a history of the changes made to your data: who created, updated, assigned, shared or deleted a record, when,
and which values changed. xrmverse writes this history to the `audit` table and returns it through the same audit messages
and SDK types as Dataverse, so code that reads audit history keeps working.

The Microsoft documentation describes the feature in
[Auditing overview](https://learn.microsoft.com/power-apps/developer/data-platform/auditing/overview),
[Configure auditing](https://learn.microsoft.com/power-apps/developer/data-platform/auditing/configure),
[Retrieve the history of audited data changes](https://learn.microsoft.com/power-apps/developer/data-platform/auditing/retrieve-audit-data)
and [Delete audit data](https://learn.microsoft.com/power-apps/developer/data-platform/auditing/delete-audit-data).
This page says what xrmverse does and where it stops. **Anything not listed under [What is audited](#what-is-audited) writes
no audit row.** Whether each detail matches a real Dataverse is not verified unless stated (see the checklist).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Switches (organization, table, column) | Same three levels and the same metadata properties | Same; the defaults of some seeded tables are our reading of the documentation, not verified |
| Data changes, sharing, role assignments, role privileges | Audited in the transaction of the operation | Same messages and detail types |
| User access auditing | Refused (`isuseraccessauditenabled` true is a 400) | **Narrower** |
| Audit configuration and metadata events (100–111), team membership | Not audited | **Narrower** |
| Deleting old audit data | `DeleteAuditData` deletes whole calendar quarters | **Differs**: stands in for Dataverse's quarterly partitions; `RetrieveAuditPartitionList` is not served |
| Retention | `auditretentionperiodv2`, default 30 days, plus a background cleanup | Same setting |
| `changedata` column | xrmverse's own versioned JSON | **Differs**: do not parse it; the messages are the contract |
| Audit privileges | Held by System Administrator only | **Differs** (our choice): System Customizer and the other standard roles do not hold them |

### Before you start

- Auditing is **off** by default: an organization has no audit cost until an administrator switches it on.
- To read history you need `prvReadAuditSummary` and `prvReadRecordAuditHistory` (see [Privileges](#privileges)).

## Where you manage auditing

| Tool | What you can do |
|---|---|
| CLI `xrmd audit` | `status`, `enable`, `disable`, `retention`, `history`, `delete` (see [CLI xrmd](cli.md)) |
| Web admin | Audit section (`/manage/audit`): switch the organization on or off, set the retention, browse the audit log. Tables: per-table and per-column switches. Record forms: the record's audit history. It does not delete audit data (see [Web admin](web-admin.md)) |
| SDK, SOAP and Web API | The metadata properties and the audit messages below |
| MCP | The tool `get_audit_history` (see [MCP](mcp.md)) |
| Solutions | Carry the table and column switches (see [Solutions and auditing](#solutions-and-auditing)) |

There is no Admin API endpoint: auditing is served by the Core.

## Switching auditing on

A row is written only when **all three** switches are on.

| Level | Where it is set | Default |
|---|---|---|
| Organization | `organization.isauditenabled` (Update of the organization row, `xrmd audit enable`, web admin) | `false` |
| Table | `EntityMetadata.IsAuditEnabled` (`UpdateEntity`, `PUT EntityDefinitions(…)`, `xrmd audit enable --table`, a solution) | See [Table defaults](#table-defaults) |
| Column | `AttributeMetadata.IsAuditEnabled` (`UpdateAttribute`, `PUT …/Attributes(…)`, `xrmd audit enable --table --column`, a solution) | See [Column defaults](#column-defaults) |

### Table defaults

| Tables | Default | Can be changed |
|---|---|---|
| `account`, `contact`, `systemuser`, `team`, `role`, `businessunit` | `true` | Yes |
| Every other seeded table (`audit` itself, `organization`, plug-in, solution, async and security catalog tables, intersects, `fileattachment`) | `false` | No |
| A table created in xrmverse | `false` | Yes |

The defaults of `contact`, `systemuser`, `team`, `role` and `businessunit` follow our reading of the documentation and are
**not verified against a real Dataverse**.

### Column defaults

| Columns | Default | Can be changed |
|---|---|---|
| Most columns, including `ownerid`, `statecode` and `statuscode` | `true` | Yes |
| The primary key, `createdon`, `createdby`, `modifiedon`, `modifiedby`, `owninguser`, `owningteam`, `owningbusinessunit`, `versionnumber`, the money `_base` columns, file and image columns and their companion columns | `false` | No |

### How the switches behave

- The table and column settings are `BooleanManagedProperty` values (managed property `canmodifyauditsettings`).
- `CreateEntity` and `CreateAttribute` accept either value; `UpdateEntity` and `UpdateAttribute` apply a changed value.
- Changing a fixed setting (`CanBeChanged` false), or changing `CanBeChanged` itself, is a 400 naming `IsAuditEnabled`.
- A change applies to the operations that start after it commits.
- Existing organizations get the settings at startup: values kept from an earlier solution import are moved into the
  metadata once; the others take the defaults above.

### Other organization settings

| Setting | Accepted values |
|---|---|
| `isuseraccessauditenabled` | `false` only; `true` is a 400 (user access auditing is not supported) |
| `useraccessauditinginterval` | Stored, no effect |
| `auditsettings` | Null or `{"StoreLabelNameforPicklistAudits":false}` (xrmverse stores option values, not labels); anything else is a 400 |

## What is audited

Audit rows are written in stage 30, inside the transaction of the operation: if the operation rolls back, so does its
audit row. Action and operation codes are those of the `audit_action` and `audit_operation` choices.

| Event | Action | Operation | What the row holds |
|---|---|---|---|
| Create | 1 | 1 | The audited columns that hold a value after the create, as new values |
| Update | 2 | 2 | Only the audited columns whose value changed, with old and new value. No change, no row |
| Assign | 13 | 2 | As Update |
| SetState | 41 | 2 | As Update. SetState still reaches plug-ins as an Update, with the same parameters as before |
| Delete | 3 | 3 | The audited columns that held a value, as old values, read before the row is removed. Each record deleted by a Cascade relationship gets its own Delete row |
| Associate / Disassociate (many-to-many) | 33 / 34 | 2 | One row on the `Target` record, with the relationship and the other records |
| Role assigned to a user / removed | 55 / 56 | 2 | As Associate / Disassociate over `systemuserroles` |
| Role assigned to a team / removed | 53 / 54 | 2 | As Associate / Disassociate over `teamroles` |
| `GrantAccess` / `ModifyAccess` / `RevokeAccess` | 14 / 48 / 49 | 2 | One row on the shared record, with the principal and the rights before and after |
| `AddPrivilegesRole` / `RemovePrivilegeRole` / `ReplacePrivilegesRole` | 57 / 58 / 59 | 2 | One row on the role, with the privileges before and after, when `role` is audited |

- A one-to-many Associate is an Update of the lookup and is audited as an Update.
- A relationship, share or role event that changes nothing (for example revoking a share that does not exist) writes no row.

### Columns of every audit row

| Column | Value |
|---|---|
| `userid` | The caller; the SYSTEM user for the server's own work |
| `callinguserid` | The impersonating user, or null |
| `transactionid` | One id per SQL transaction |
| `createdon` | UTC |
| `timetoliveinseconds` | See [Retention](#retention-and-deleting-audit-data) |
| `attributemask` | Always null (xrmverse has no column numbers) |

## Reading audit history

### The `audit` table

| Property | Value |
|---|---|
| Object type code | 4567 (from memory, not verified) |
| Entity set | `audits` |
| Ownership | None |
| Writes | Read-only for everyone: Create, Update, Delete and Assign through any front end are a 400, System Administrator included |
| Read privilege | `prvReadAuditSummary` (Global), otherwise 403 |
| Filter on `objecttypecode` | An integer type code is translated to the table's logical name; an unknown code is a 400 |
| Indexes | `IX_audit_objectid` (`objectid`, `objecttypecode`, `createdon` descending) and `IX_audit_createdon`, created at startup when missing |

### The `changedata` column

`changedata` holds xrmverse's own versioned JSON (`{"v":1,…}`). **Do not parse it**: as the Microsoft documentation also
advises, the messages below are the contract, and the format may change.

- It is not part of the Web API `audit` entity type: it is absent from `$metadata` and from every response, including the
  columns of an `all-attributes` FetchXML link-entity. `$select`, `$filter` or `$orderby` on it, or naming it anywhere in a
  `fetchXml` query, is a 400.
- The SDK returns it as stored.
- Text values longer than 5000 characters are cut to 5000 and end with `…`.
- Lookups keep the name of their target as it was when the row was written.

### Audit messages

The messages run outside the plug-in pipeline (no `sdkmessage` rows, no steps can be registered on them) and work on the SDK,
SOAP and Web API paths, and from a plug-in's service.

| Message | Privileges | Web API |
|---|---|---|
| `RetrieveAuditDetails(AuditId)` → `AuditDetail` | `prvReadAuditSummary` | `GET audits(<id>)/Microsoft.Dynamics.CRM.RetrieveAuditDetails` |
| `RetrieveRecordChangeHistory(Target, PagingInfo?)` → `AuditDetailCollection` | `prvReadRecordAuditHistory` and `prvReadAuditSummary` | `GET RetrieveRecordChangeHistory(Target=@target,PagingInfo=@paginginfo)?@target={'@odata.id':'accounts(<id>)'}&@paginginfo={"PageNumber":1,"Count":10}` |
| `RetrieveAttributeChangeHistory(Target, AttributeLogicalName, PagingInfo?)` → `AuditDetailCollection` | The same | `GET RetrieveAttributeChangeHistory(Target=@target,AttributeLogicalName='telephone1')?@target=…` |
| `DeleteRecordChangeHistory(Target)` → `DeletedEntriesCount` | `prvDeleteRecordChangeHistory` | `POST DeleteRecordChangeHistory` with `{"Target":{"@odata.type":"Microsoft.Dynamics.CRM.account","accountid":"<id>"}}` |
| `DeleteAuditData(EndDate)` → `PartitionsDeleted` | `prvDeleteAuditPartitions` | `POST DeleteAuditData` with `{"EndDate":"2026-06-30T00:00:00Z"}` |

### What the messages return

- **Detail types**: `AttributeAuditDetail` (Create, Update, Assign, SetState, Delete), `RelationshipAuditDetail`,
  `ShareAuditDetail` and `RolePrivilegeAuditDetail`.
- **Values**: `OldValue` and `NewValue` are entities of the audited table with formatted values; a cleared value is left out.
- **`AuditRecord`**: on the SDK path it is the audit row (with formatted `createdon`, `action`, `operation` and the user's
  name). The Web API does not return it (whether Dataverse's does is not verified: checklist step 1 (m)); read `audits` for
  the date and user.
- **Order**: newest first.
- **Formatted values on the Web API**: send `Prefer: odata.include-annotations`. The types are declared in `$metadata`.

### Paging

| `PagingInfo` property | Behaviour |
|---|---|
| `PageNumber` | Page to return; paging follows it |
| `Count` | Default 5000, at most 5000 |
| `ReturnTotalRecordCount` | When true, `TotalRecordCount` is filled; otherwise it is -1 |
| `PagingCookie` | Returned as `<cookie page="N" />` and accepted back; any other cookie is a 400 |

### History of deleted records, columns and tables

- History survives the record: the rows of a deleted record, column or table stay readable.
- A column the table no longer has moves to `DeletedAttributes`.
- On the Web API the history of a deleted table cannot be named (see [Known limits](#known-limits)); the SDK and SOAP paths reach it.

## Privileges

Four privileges, not tied to a table, Global only:

| Privilege | Needed for |
|---|---|
| `prvReadAuditSummary` | Reading `audits` and every history message |
| `prvReadRecordAuditHistory` | `RetrieveRecordChangeHistory`, `RetrieveAttributeChangeHistory` |
| `prvDeleteRecordChangeHistory` | `DeleteRecordChangeHistory` |
| `prvDeleteAuditPartitions` | `DeleteAuditData` |

System Administrator holds them; **System Customizer and the other standard roles do not** (our choice; not verified against a
real Dataverse). Grant them to any role with `AddPrivilegesRole`. Access to the audited record itself is not checked: the
record may already be deleted.

## Retention and deleting audit data

### Retention

| Item | Behaviour |
|---|---|
| Setting | `organization.auditretentionperiodv2`: days from 1 to 365000, or -1 to keep rows forever. Default 30; any other value, null included, is a 400 |
| How to set it | `xrmd audit retention <days>` or `xrmd audit retention --forever`, or the web admin |
| Stamping | Each row is stamped when written (`timetoliveinseconds`, -1 for forever), so a new period applies to new rows only (our reading of the documentation, not verified: checklist step 1 (n)) |
| Cleanup | A background service deletes expired rows every hour, in batches; the first run is one minute after startup |

### Deleting on demand

- `DeleteRecordChangeHistory` deletes every audit row of one record and returns how many.
- `DeleteAuditData` stands in for Dataverse's quarterly partitions. It deletes the rows of every calendar quarter (UTC) that
  ended on or before `EndDate`, never the current quarter, and returns the number of quarters that had rows. It runs in one
  transaction, which holds the locks of the deleted rows until it commits.

## Solutions and auditing

- `IsAuditEnabled` of tables and columns, and the table's `CanModifyAuditSettings`, are read on import and written on export.
- A file that sets a fixed setting to another value is not applied, and the import reports a warning.
- The organization audit settings are not solution components and do not travel.
- `xrmd apply` schema files do not carry `IsAuditEnabled`: use `xrmd audit enable/disable` or a solution.

See [Solutions](solutions.md).

## Known limits

What xrmverse does not audit, or does differently. None of these is silent parity: each is either refused with an error or
stated here.

### Events that write no audit row

- User access events (64, 65, 112, 113): `isuseraccessauditenabled` true is refused.
- Audit configuration and metadata events (100–111: a table or column deleted, auditing switched on or off at any level,
  audit data deleted). The documentation gives their action codes but not the shape of their detail.
- Adding or removing team members (`teammembership`).
- The lookups cleared by a RemoveLink cascade (a direct update; see "Delete cascades" in [Solutions](solutions.md)).
- The rows a Delete removes directly with the deleted record: its many-to-many associations, its shares
  (`principalobjectaccess`, including those a deleted team or user held), for a deleted role or team its role assignments
  (`systemuserroles`, `teamroles`), and for a deleted role its privileges (`roleprivileges`). The Delete row of the record
  is written; no Disassociate (34), RevokeAccess (49) or role removal (54, 56) row is written for them (what Dataverse writes
  is checklist step 1 (o)).
- Role privilege changes made by a solution import (they are not messages).
- An Update, Assign or SetState that changes no audited column.
- The bootstrap administrator created by `server init` (it exists before auditing can be switched on).

### Messages not implemented

`RetrieveAuditPartitionList`, `BulkDelete` of `audit`, `Upsert` and `Merge` are refused as unsupported messages (so they
write no audit row either).

### Behaviour that differs

| Item | Details |
|---|---|
| `DeletedAttributes` keys | The SDK type is a dictionary keyed by integer. xrmverse has no column numbers, so the key is an ordinal with no meaning; only the values (the logical names) are reliable. For a deleted table, the entities are empty and every stored column is listed in `DeletedAttributes`: our own reading, since the documentation does not say what Dataverse returns (checklist step 1 (i)) |
| Empty rather than an error | `RetrieveAttributeChangeHistory` with a column the table never had returns an empty collection, so the history of a deleted column stays readable. Dataverse may refuse it |
| Very long retention | `timetoliveinseconds` is an Integer: a retention above 24855 days is stamped as `int.MaxValue` seconds (about 68 years), so such rows are deleted after about 68 years, not after the configured period |
| Concurrent metadata edits | The table and column switches of the CLI and the web admin read the full definition and send it back without `If-Match`: a metadata change made by someone else in between can be overwritten |
| `CanBeChanged` true on a fixed setting | Sent explicitly with an unchanged value, it is ignored, not refused: the server cannot tell it from the default of the SDK object |
| Web API `ShareAuditDetail` rights | Written as .NET flag names separated by `, ` (`"ReadAccess, WriteAccess"`); Dataverse's exact separator is not verified |
| Deleted tables on the Web API | `RetrieveRecordChangeHistory` and `DeleteRecordChangeHistory` name the record by its `@odata.type` or entity set, which a deleted table no longer has, so the Web API refuses them with a 400. The SDK and SOAP paths name the table by its logical name and reach that history |
| Web admin record history | Calls `RetrieveAuditDetails` once per row; one failing call fails that page of 20 (as the CLI's `history` does) |

### Not verified against a real Dataverse

The type code 4567, the defaults of the seeded tables, which roles hold the audit privileges, the action of an Update that
changes only `ownerid` (xrmverse: Update 2, not Assign 13), the `userid` of a row written by a plug-in that runs as another
user (xrmverse: the user the service runs as), and the paging cookie format.

## See also

- [Security](security.md): roles and privileges
- [Field-level security](field-security.md)
- [CLI xrmd](cli.md), [Web admin](web-admin.md), [MCP](mcp.md)
- [Known issues and limitations](known-issues.md)
