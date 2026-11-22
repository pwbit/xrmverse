# Elastic tables

[← xrmverse documentation](../README.md#documentation)

## Introduction

An elastic table is a Dataverse table meant for very large volumes of rows that arrive quickly and are rarely joined to
anything: telemetry, event logs, IoT readings. In Dataverse it lives in a document store and is partitioned; the
Microsoft documentation describes the feature in
[Elastic tables](https://learn.microsoft.com/power-apps/developer/data-platform/elastic-tables),
[Create and edit elastic tables](https://learn.microsoft.com/power-apps/developer/data-platform/elastic-tables-create) and
the developer pages on bulk operations and partitioning. xrmverse emulates the same programming surface on SQL Server,
so a client written for an elastic table talks to xrmverse unchanged. This page says what is the same, what is
different and what is refused. None of it has been compared with a real Dataverse environment yet.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Storage | An ordinary SQL table | **Differs**: Dataverse uses a partitioned document store; xrmverse gains no scalability from the type |
| Programming surface | `TableType`, `partitionid`, `ttlinseconds`, session token, bulk messages, `ExecuteCosmosSqlQuery` | Same surface, so clients work unchanged |
| Ids across partitions | Unique across partitions | **Narrower**: Dataverse allows the same id in two partitions |
| Bulk messages | One pipeline per record, partial success; steps on `*Multiple` refused | **Narrower**: register steps on `Create` / `Update` |
| JSON queries | Basic subset of `ExecuteCosmosSqlQuery` | **Narrower**: other constructs are refused with a 400 |
| Solutions | No solution export or import yet (#77) | **Narrower** |
| Verification | Not yet compared with a real Dataverse environment | **Unverified** |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Decide whether to use one | Check the workload | [When to use one](#when-to-use-one) |
| Create one | `CreateEntity` with `TableType` `Elastic`, or `tableType: Elastic` in a schema file | [How xrmverse stores them](#how-xrmverse-stores-them) |
| Read and write by partition | `partitionId` parameter or alternate key | [Partitions](#partitions), [Writes](#writes) |
| Expire rows | `ttlinseconds` | [Time to live](#time-to-live) |
| Query inside JSON columns | `ExecuteCosmosSqlQuery` | [Querying JSON columns](#querying-json-columns) |

## When to use one

Use an elastic table for append-heavy data that is read by id, by partition or by a simple filter. Keep standard tables
for anything that needs joins, rollups, calculated columns, sharing or the full security model. Because xrmverse stores
an elastic table in SQL, it gains no scalability from the type; the type exists so that code and metadata written for
Dataverse keep working, and so that you can find out early which features such a table would refuse.

## How xrmverse stores them

- `EntityMetadata.TableType` is `Standard` (the default, also reported for every existing table) or `Elastic`. The value
  is case-insensitive on input; any other value (`Virtual`, …) is a 400 naming it. The type is fixed at creation:
  `UpdateEntity` with another type is a 400.
- An elastic table is an ordinary SQL table. Columns are stored as in a standard table; there is no document layout.
  A column that holds JSON is a `String` with `FormatName` `Json`, validated as well-formed JSON on write. The SDK that
  xrmverse is built against (1.2.27) has no JSON format on `Memo` columns, so only the String form is available.
- Two system columns are added: `partitionid` (String, up to 100 characters, no required level, settable at creation and
  never afterwards) and `ttlinseconds` (Integer, 1 to 2147483647). There is no `statecode` or `statuscode`. The
  ownership is `UserOwned` or `OrganizationOwned` only.
- The alternate key `keyfornosqlentitywithpkpartitionid` (schema name `KeyForNoSqlEntityWithPKPartitionId`, columns
  primary id and `partitionid`) is reported, read-only, in `EntityMetadata.Keys` of `RetrieveEntity` and in
  `EntityDefinitions(…)/Keys`. You cannot define keys of your own.
- Schema files (`xrmd schema apply` and `export`) carry `tableType: Elastic` and round-trip it.

## Partitions

A row belongs to the partition named by its `partitionid`; a row without one belongs to no partition. The value has at
most 100 characters and may not contain any of `/ < > * % & : \ ? +` (a 400 `Invalid partitionid value: …`).

### Passing the partition

| Where | How |
|---|---|
| SDK `Retrieve`, `Update`, `Delete`, `Upsert` | a `partitionId` optional parameter, or a `KeyAttributeCollection` on the `EntityReference` / `Entity` holding the primary id and `partitionid`. For `Update` and `Upsert` the key attribute wins, then a non-null `partitionId` parameter, then the target's `partitionid` attribute; an `Upsert` that creates the row stores it in that partition, and a `partitionid` attribute naming another partition is a 400 |
| SDK `Create` | the `partitionid` attribute on the entity. `Update` also accepts `partitionid` as an attribute, but only to name the partition of the row: a value that differs from the stored one is refused, because the column never changes |
| SDK `RetrieveMultiple` (QueryExpression, FetchXML) | the `partitionId` optional parameter |
| Web API single record | the alternate key `{entityset}(accountid=<guid>,partitionid='<value>')` (either order), or `?partitionId=<value>` on a GET or DELETE |
| Web API collection | `?partitionId=<value>` |
| Web API writes | `?partitionId=` on POST and PATCH is ignored; the body's `partitionid` (create) or the key (update; when the key names no partition, the body's `partitionid`) decides |

Rules that follow from the above, all stated in the code and covered by tests:

- The partition match is exact and case-sensitive, and trailing spaces count: `'a'` and `'a '` are different partitions.
- A row stored with a partition is found only when the same partition is given. A row stored without one is found only
  when no partition (or null) is given; a key with the primary id alone therefore means "no partition".
- Naming a partition and finding no row there is a 404 (`0x80040217`).
- **The id stays unique.** Dataverse allows two rows with the same id in different partitions. xrmverse keeps the primary
  key on the id, so a `Create` whose id already exists in another partition is refused with `0x80040237` (412,
  DuplicateRecord), and so is an `Upsert` that would do the same. The refusal is explicit rather than silent. Microsoft
  itself warns that the duplicate-id pattern does not suit Power Apps.
- A `partitionId` parameter on a query against a standard table is a 400, and so are `KeyAttributes` on a standard table
  (there only the primary id is a key). Dataverse may ignore both; xrmverse prefers to tell you.
- A `partitionId` cannot be combined with change tracking (a 400).

## Session token

Every `Create`, `Update`, `Upsert`, `CreateMultiple`, `UpdateMultiple` and `UpsertMultiple` on an elastic table returns a
session token: `Results["x-ms-session-token"]` on the SDK and the response header `x-ms-session-token` on the Web API.
Its value is opaque to clients (xrmverse builds it from the version number of the last row written). `Delete` returns
none, as in Dataverse. The optional parameter `SessionToken` and the request header `MSCRM.SessionToken` are accepted
and ignored: reads in xrmverse always see the latest committed data.

## Writes

- **Create** needs no partition; with one, the rules above apply. Related records cannot be created together with the
  row (no deep insert; a 400).
- **Update** changes the named columns of an existing row. **PATCH on the Web API is update-only**: a PATCH against a row
  that does not exist is a 404 and never creates it, as for standard tables. Clients that rely on PATCH as an upsert get
  the 404.
- **Upsert** (single, elastic tables only) creates the row when the id and partition are not found, and otherwise
  replaces it: every writable column that is not in the payload is set to null. It runs as its own `Upsert` message, so
  no `Create` or `Update` step fires, and it returns `RecordCreated`. On a standard table `Upsert` also exists, but it is
  not an event there and runs an inner `Create` or `Update` (see [Bulk operations](bulk-operations.md#upsert)).
- **Delete** needs the right partition (a 404 otherwise).
- **Sharing** (`GrantAccess`, `ModifyAccess`) of an elastic row is a 400.

## Time to live

Set `ttlinseconds` on a row and it expires at `modifiedon` plus that many seconds. Expiry is carried out by a sweep in the
server process:

- `Elastic:TtlSweepSeconds` sets the interval (default 60; 0 switches the sweep off). It is documented with the other keys
  in [Configuration](configuration.md).
- Each sweep deletes up to 500 expired rows per elastic table, as the SYSTEM user, through the core delete. Audit and
  change tracking therefore record a Delete, but no plug-in step fires. A row that cannot be deleted is logged and
  skipped, and does not hold back the rows behind it. A sweep that filled a batch runs again at once.
- There is one sweep process per organization database (see [Multiple organizations](multiple-organizations.md)).
- Rows that have expired but are not swept yet can still be read; Dataverse gives no read-time guarantee either.

## Bulk messages and partial success

`CreateMultiple`, `UpdateMultiple`, `UpsertMultiple` and `DeleteMultiple` work on elastic tables, on the SDK and as Web API
actions (`Microsoft.Dynamics.CRM.CreateMultiple`, …).

- **One pipeline per target.** Each target runs as the single-record message (`Create`, `Update`, `Upsert`, `Delete`),
  in order, through the full pipeline with its own security checks. Dataverse runs one pipeline for the whole batch;
  xrmverse does not merge the pipelines for elastic tables (the merge exists for standard tables, see
  [Bulk operations](bulk-operations.md)).
- **Plug-in steps** registered on a `*Multiple` message are refused at registration (400, "register on the
  single-record message"). Register the step on `Create`, `Update`, `Upsert` or `Delete` and it fires once per record.
- **Partial success.** A failing target does not stop the others. When any target failed, the request fails with the
  code of the first failure, and `ErrorDetails["Plugin.BulkApiErrorDetails"]` holds a JSON array of
  `{ "RequestIndex": n, "Id": "<guid or empty>", "StatusCode": n }` for every failed target (on the Web API it is the
  annotation `@Microsoft.PowerApps.CDS.ErrorDetails.Plugin.BulkApiErrorDetails` in the error body, written only when the
  request sends a `Prefer: odata.include-annotations` header that names `*`, `Microsoft.PowerApps.CDS.ErrorDetails.*`
  or the annotation itself).
- `UpdateMultiple` ignores a later target whose id was already seen; `UpsertMultiple` with a repeated id fails the whole
  request before any write.
- **Not from a plug-in.** A bulk message sent from inside a plug-in is refused with 400
  `<Message> cannot be called from plug-in code.`
- **Standard tables.** `CreateMultiple`, `UpdateMultiple` and `UpsertMultiple` work there with all-or-nothing semantics and
  steps on the `*Multiple` messages; `DeleteMultiple` is a 400 with the text `DeleteMultiple has not yet been implemented.`
  (the observable Dataverse outcome). See [Bulk operations](bulk-operations.md).
- **Web API PATCH.** On an elastic table PATCH stays update only; `If-None-Match: *` is a 400 there. On a standard table
  PATCH is an upsert.

## Transactions

Dataverse does not give an elastic write a transaction across the pipeline, and xrmverse follows with one difference.

- **First-level writes commit per stage.** A write that a client sends commits after stage 20 (PreOperation), after stage
  30 (the operation, with its audit and change-tracking rows in the same transaction) and after stage 40
  (PostOperation). A failure in PostOperation is returned to the caller but the row stays; a write made in
  PreOperation stays even if stage 30 fails. PreValidation still blocks everything.
- **Nested writes join the outer transaction.** When a plug-in writes to an elastic table from inside another message's
  transaction, the write joins that transaction instead of committing on its own, and rolls back with it. This is
  stronger than Dataverse: a second commit on the same connection is not possible without risking deadlocks.

## Queries

Elastic tables are read with QueryExpression, FetchXML and OData like any table, with these limits.

- The default page size is 500 (standard tables: 5000). `top` and `count` are unchanged.
- A link entity, on either side, is refused with `0x80048d0b` ("Link entities are not supported for elastic table …"). An
  OData `$expand` on a collection is a link entity and is refused the same way; so is `$expand` on a single elastic
  record (see known gaps).
- Also refused with a 400: sorting by a lookup column; a distinct query on a user-owned table, with more than one sort
  column, or sorted by a column it does not select; sorting combined with group by in an aggregate query.
- Querying inside JSON columns: see [Querying JSON columns](#querying-json-columns).

See also [Supported queries](queries.md).

## Querying JSON columns

`ExecuteCosmosSqlQuery` answers a small subset of the Cosmos-style SQL dialect over an elastic table, including the
paths inside its JSON columns. It is available as an SDK message and as a Web API function. The query is translated to
ordinary SQL Server conditions (`ISJSON`, `JSON_QUERY`, `OPENJSON`), so it runs under the same read privilege, row
security and field security as any other query. Anything outside the subset is refused with a 400.

Example, on an invented table `new_sensorreading` with a JSON column `new_payload`:

```sql
SELECT c.props.new_name AS name, c.props.new_payload.reading.humidity AS humidity
FROM c
WHERE c.props.new_payload.site.zone = @zone AND c.props.new_payload.reading.humidity >= 40
ORDER BY c.props.new_name
```

### Request

| Parameter | SDK type | Web API type | Required |
|---|---|---|---|
| `QueryText` | string, at most 32768 characters | `Edm.String` | yes |
| `EntityLogicalName` | string | `Edm.String` | yes |
| `QueryParameters` | `ParameterCollection` (keys such as `@zone`) | `{"Keys":["@zone"],"Values":[{"Type":"System.String","Value":"north"}]}` | no |
| `PageSize` | long, 1 to 5000, default 500 | `Edm.Int64` | no |
| `PagingCookie` | string | `Edm.String` | no |
| `PartitionId` | string | `Edm.String` | no |

In the Web API the parameters are inline or `@alias` values taken from the query string. An alias, or a value, of `null`
means that the parameter is absent. An alias that is repeated is a 400.

### Response

| | SDK | Web API |
|---|---|---|
| Container | `Results["Result"]` is an `Entity` (`LogicalName` `expando`, empty id) | an `expando` object |
| `Result` | a string that holds a JSON array | a JSON array of `expando` objects |
| `HasMore` | bool | bool |
| `PagingCookie` | string, present only when `HasMore` is true | the same |

### Supported grammar

- `SELECT <item>[, <item>...] FROM c [WHERE <expression>] [ORDER BY <column> [ASC|DESC]]`. Keywords are not case sensitive.
  The source is `FROM <alias>`, usually `c`; the same alias must start every path. `FROM c AS x` and any other form of
  the source are refused.
- An item is a path with an optional `[AS] alias`. A path is `c.props.<column>` (the logical name, matched without
  regard to case), optionally followed, for a JSON column only, by `.key`, `["quoted key"]` or `[n]` with a
  non-negative integer literal. Parameters are values only: never an index or a key.
- An expression combines comparisons (`=`, `!=`, `<>`, `<`, `>`, `<=`, `>=`) between one path and one literal or
  `@parameter` (on either side), `IS_DEFINED(path)`, `NOT`, `AND`, `OR` and parentheses.
- Literals are strings in single or double quotes (escapes `\' \" \\ \/ \b \f \n \r \t \uXXXX`), numbers (sign,
  decimals, exponent), `true`, `false` and `null`.
- `ORDER BY` takes one top-level column only. It is refused on a JSON path or a whole JSON column, and a column the
  caller may not read is a 403.

### Comparison rules

On a path inside a JSON column, comparisons are type strict, as in Cosmos:

- `=` matches a value of the same JSON type and equal content. `!=` matches every value that exists and is not equal,
  including values of other types.
- `<`, `>`, `<=`, `>=` match only strings against a string literal or numbers against a number literal. A boolean or
  `null` literal with a range operator is refused.
- A path that does not exist (missing key, step through a scalar, null or invalid JSON in the column) never matches a
  comparison. `NOT` is applied by negating the operator, so `NOT (path > 5)` still excludes rows without the path,
  while `NOT IS_DEFINED(path)` returns them.
- An index step matches only an array, and a property step matches only an object.
- Strings are compared with a binary collation, so case matters. Equality is exact. Range comparisons ignore trailing
  spaces, because SQL Server pads strings when it compares them.
- JSON numbers are compared as float. A value outside the float range never matches, and integers above 2^53 lose
  precision.
- If a JSON object repeats a key, a condition matches when any of the duplicates matches.

On a top-level column, a null column counts as undefined: it is left out of the result and `IS_DEFINED` is false.
Literals are checked against the column type. On a text column (string or memo) a number or boolean value is a 400,
`ExecuteCosmosSqlQuery: the value for '<column>' must be a string.` On an integer, big integer, decimal, double or money
column a string or boolean value is a 400, `ExecuteCosmosSqlQuery: the value for '<column>' must be a number.` The same
applies to `@parameter` values. Other column types (date, option set, lookup, and so on) convert the value as
RetrieveMultiple does, and text that cannot be converted is a 400. `!= null` and
`IS_DEFINED` mean "not null"; `= null` on a column is refused, use `NOT IS_DEFINED`. Strings compare with a binary
collation.

### Results

`Result` has one object per row, with the properties in SELECT order. A property is named after its alias, otherwise
after the last step of the path (identifier or quoted key), otherwise `$1`, `$2`... counted over the unnamed items. Two
items with the same name are a 400. Values:

- strings, bools and numbers as in JSON; a date and time as UTC `yyyy-MM-ddTHH:mm:ss.FFFFFFFZ`; a guid or lookup id as
  a `D` string; an option set as its integer; a multi-select as an array of integers;
- a whole JSON column as embedded JSON, and a JSON path as the JSON value it reaches;
- undefined values are omitted: a null column, a missing path, a path through a scalar, a column that field security
  hides from the caller, and also NaN, Infinity and binary values;
- non-ASCII text is written as is, not as `\u` escapes.

### Parameters

`QueryParameters` keys are matched with or without the leading `@`, with case. Unused keys are ignored. Accepted value
types are `System.String`, `System.Int32`, `System.Int64`, `System.Double`, `System.Decimal` and `System.Boolean`;
anything else is a 400 naming the key. A `@name` used in the query without a value is a 400
(`ExecuteCosmosSqlQuery: the parameter '@name' has no value.`).

### Paging

The order is the `ORDER BY` column and then the primary id, or the primary id alone. `ORDER BY` on a string column uses
the column collation, so it is case-insensitive (filters on strings are case-sensitive); null values sort first when
ascending. `PageSize` goes up to 5000. The
`PagingCookie` is returned only when `HasMore` is true. It is opaque and URL-safe; send it back unchanged, with the same
query and `PageSize`. With an `ORDER BY`, pages are positional, so concurrent writes, or another `PageSize`, can shift
the pages and skip or repeat rows. It is not tied to the query text, so reusing it with another query is an error that is not detected. A cookie
that was altered is a 400 (`ExecuteCosmosSqlQuery: the PagingCookie is not valid.`).

### Partitions

A `PartitionId` restricts the query to that partition (exact match, validated like on RetrieveMultiple). Without it
the query spans every partition.

### Security

- No read privilege on the table is a 403, as for RetrieveMultiple. Rows outside the caller's read scope never appear.
- A condition on a secured column, or on a path inside one, that the caller cannot read is a 403 (`0x8004F504`). In the
  SELECT list such a column is simply undefined, unless a field share grants it on that row.
- The message runs through the plug-in pipeline like a read message (`PrimaryEntityName` is `EntityLogicalName`) and can
  be called from a plug-in.
- Paths and string values never become SQL text: they travel as SQL parameters.

### Errors

A refused construct is a 400 `0x80040203` with the text `ExecuteCosmosSqlQuery: <construct> is not supported by
xrmverse.` A syntax error is `ExecuteCosmosSqlQuery: syntax error at position <n>: <detail>.` with a 0-based offset.
A standard table gives `ExecuteCosmosSqlQuery applies to elastic tables only; '<table>' is not an elastic table.`
`<construct>` is one of:

- `SELECT *`, `SELECT VALUE`, `DISTINCT`, `TOP`, `OFFSET LIMIT`, `GROUP BY`, `JOIN`, `a subquery`, `EXISTS`, `IN`,
  `BETWEEN`, `LIKE`, `undefined`;
- `function <NAME>` (every function except `IS_DEFINED`, aggregates and `udf.*` included);
- `an arithmetic operator`, `string concatenation (||)`, `the ?? operator`, `the ?: operator`,
  `an object or array literal`;
- `a comparison between two properties`, `a comparison without a property`;
- `a property outside c.props`, `a property path into the non-JSON column '<column>'`,
  `a comparison of the whole JSON column '<column>'`, `a range comparison with <bool|null>`,
  `a comparison of a column with null (use NOT IS_DEFINED)`,
  `comparing a column with null (use IS_DEFINED or NOT IS_DEFINED)` (the form `NOT (c.props.<column> != null)`);
- `ORDER BY on a JSON path`, `ORDER BY with more than one item`.

Other limits, all 400: a duplicate result name; a result name that starts with `@`
(`ExecuteCosmosSqlQuery: the result name '<name>' is reserved.`); `ORDER BY` on a lookup column, which gives the generic
elastic text `Queries on elastic table '<table>' do not support sorting by the lookup column '<column>'.`; an unknown column; `QueryText` over 32768 characters; a number literal
outside the decimal range; a string with an unpaired surrogate; a key that has control characters other than
`\b \f \n \r \t`; an AND/OR chain of more than 1000 operators; nesting of parentheses and `NOT` deeper than 32 levels;
a `PageSize` outside 1 to 5000.

### Not yet compared with Dataverse

Some details come from our reading of the documentation and wait for a licensed environment (issue
#20): whether `c.props` uses logical or schema names and
holds the system columns; how null and unset columns appear; the formats of date, lookup and option set values; the
`LogicalName` of the result entity; whether `Result` is an array or a string on the Web API; whether a query without
`PartitionId` spans partitions; the error code of an unsupported construct; and the paging cookie, which is ours.

## Refused on elastic tables

Each of these is a 400 `InvalidArgument` naming the feature and the elastic table, raised at the request that would turn
it on:

- many-to-many relationships; a one-to-many cascade other than `NoCascade` for any action;
- column types Money, MultiSelectPicklist, State, Status and Image, and calculated, rollup or formula columns; Integer
  columns with a format other than None; Customer lookups;
- alternate keys of your own, duplicate detection rules and `IsDuplicateDetectionEnabled`, `AutoCreateAccessTeams`,
  `IsActivity`;
- sharing of a record and deep insert, as above.

## Solutions

The customizations XML of an elastic table has not been seen in a real environment (it needs a Dataverse environment,
see issue #20). Until then:

- **Export** of a solution that contains an elastic table, or only a column of one, fails with a 400 naming the table.
- **Import** cannot recognise the type and is a known gap.
- Use `xrmd schema apply` and `export` (with `tableType: Elastic`) to move the definition between organizations.

Both are tracked in issue #77.

## Known gaps

These are the known limits of elastic tables in xrmverse; none is silent. What is refused by design is in
[Refused on elastic tables](#refused-on-elastic-tables).

- File columns: a Web API route carrying a partition key (`…(id=<guid>,partitionid='p')/column`) is refused with a 400
  that names the elastic table, rather than silently ignoring the partition. Use the route by id alone (`…(<id>)/column`):
  since the id is unique, it reaches the row in whatever partition it is stored in, for upload, read, download and delete.
  The SDK file messages (`InitializeFileBlocksUpload`, `CommitFileBlocksUpload`, `DeleteFile`, the downloads) name the row
  by id in the same way, and the update they make keeps the row's stored partition.
- `$expand` on a single elastic record is refused (it is a link entity).
- The bulk actions are not listed in `$metadata`.
- Memo columns with Json format are not available (SDK 1.2.27); use a String column.
- Solution export and import of elastic tables (#77).

## See also

- [Bulk operations](bulk-operations.md)
- [Supported queries](queries.md#elastic-tables)
- [Configuration](configuration.md) (`Elastic:TtlSweepSeconds`)
