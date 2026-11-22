# Supported queries

[← xrmverse documentation](../README.md#documentation)

## Introduction

xrmverse reads data with the same query languages as Dataverse: `QueryExpression` and FetchXML through the SDK, FetchXML and
OData through the Web API. All of them are translated into one internal query, so a feature behaves the same whichever
language you use. This page lists what each language supports, the limits, and where results differ from Dataverse.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Query languages | `QueryExpression`, `QueryByAttribute`, FetchXML, OData, `ExecuteCosmosSqlQuery` | Same |
| Condition operators | The list in [Operators](#operators); any other is a 400 | **Narrower**: unsupported operators are refused, never ignored |
| OData query functions (`Microsoft.Dynamics.CRM.*`) | Not supported in `$filter` (400) | **Narrower** (#50); use FetchXML |
| Page size, aggregate limit | 5000 rows per page, 50,000 records per aggregate | Same |
| Nesting and parameters | 32 filter levels, 10 link-entity levels, 2000 parameters | **Extra limits**: not present in Dataverse |
| Dates | Evaluated in UTC | **Differs**: see [Known deviations](#known-deviations) |
| Calculated and formula columns | Computed in the query; can always be sorted | **Differs**: Dataverse disables sorting on some |

## Query languages

| Frontend | Features |
|---|---|
| SDK / SOAP (`ServiceClient`) | `QueryExpression`, `QueryByAttribute` (criteria, nested filters, link-entity, order, `TopCount`, `PageInfo`) |
| FetchXML | `FetchExpression` via SDK and Web API `?fetchXml=` (filters, link-entity, order, aggregates, `top`, `page`/`count`/`paging-cookie`) |
| `ExecuteCosmosSqlQuery` (elastic tables) | SDK message and Web API function: a subset of Cosmos-style SQL with `c.props` paths into JSON columns, paged by `PagingCookie`: see [Querying JSON columns](elastic-tables.md#querying-json-columns) |
| OData (Web API) | `$select`, `$filter`, `$orderby`, `$top`, `$count`, `$expand` (single-valued, nested), `$apply`, `odata.maxpagesize` + `@odata.nextLink` |

## Operators

Any operator not listed here returns 400.

| Group | Operators | Where |
|---|---|---|
| Comparison | `eq`, `ne`, `gt`, `ge`, `lt`, `le`, `in`, `not-in`, `between`, `not-between`, `null`, `not-null` | All languages |
| Text | `like`, `not-like`, `begins-with`, `not-begin-with`, `ends-with`, `not-end-with` | All languages |
| Dates | `on`, `on-or-after`, `on-or-before`, `last-x-days`, `next-x-days`, `last-x-months`, `this-month`, `this-year` | All languages |
| Current user and business unit | `eq-userid`, `ne-userid`, `eq-businessid`, `eq-userteams`, `eq-useroruserteams` | FetchXML and `QueryExpression`; not the OData query functions `Microsoft.Dynamics.CRM.*` |
| Hierarchy | `under`, `eq-or-under`, `above`, `eq-or-above`, `not-under` | On `businessunit` and on every table with a hierarchical relationship (see [Hierarchical operators](#hierarchical-operators)); 400 on other tables |
| User hierarchy | `eq-useroruserhierarchy`, `eq-useroruserhierarchyandteams` | See [Hierarchy security](security.md#hierarchy-security) |

## Aggregates

`count`, `countcolumn` (also `distinct`), `sum`, `avg`, `min`, `max`, `groupby`, `dategrouping` (`day`, `week`, `month`,
`quarter`, `year`).

## Limits

| Limit | Value | Beyond it |
|---|---|---|
| Rows per page (`count`, `top`, `$top`, `odata.maxpagesize`) | 5000 | 400 |
| Records per aggregate query | 50,000 | `0x8004E023` (`AggregateQueryRecordLimitExceeded`, HTTP 400) |
| Filter nesting | 32 levels | 400 `InvalidArgument` |
| Link-entity nesting | 10 levels | 400 `InvalidArgument` |
| SQL parameters per query | 2000 (an `in` condition with more than 100 values uses a single parameter) | 400 `InvalidArgument` |
| `$expand` items | 15, all levels counted | 400 |
| Calculated columns per query | 50 distinct | 400 |

## Paging

| Query shape | Paging method |
|---|---|
| Ordered only on the primary key, with N:1 links only | Keyset on the primary key (with paging cookie) |
| Anything else | `OFFSET/FETCH` by page number |

## Activities, party lists and note bodies

These rules apply to the tables of [Activities and notes](activities.md).

- **`activitypointer`** is an ordinary table for queries: every activity has one row there, so one query finds the
  activities of every type regarding a record (`regardingobjectid` eq the record's id; on the Web API
  `$filter=_regardingobjectid_value eq <id>`), and `activitytypecode` filters by type with a logical name
  (`activitytypecode eq 'email'`, FetchXML `value="email"`) or a type code. Paging, sorting and aggregates work as on any
  table. The formatted value of its `statuscode` comes from the option set of each row's activity table. See
  [Activities and notes](activities.md).
- **Party lists** (`to`, `from`, `requiredattendees`, …) can be selected on an activity (SDK, every page of
  `RetrieveMultiple`; on the Web API through `$expand={activity}_activity_parties`, with `$select` only inside it), but
  not used in a condition, an order, a link's `from`/`to`, an aggregate or `groupby`, an alias, a link-entity's columns or
  a `$select` (400 naming the column). To filter by participant, link to `activityparty` on `activityid` and filter its
  `partyid` and `participationtypemask`. A distinct query that asks for a party list must also select the primary key
  (400 otherwise). An empty party list is left out of the record.
- **`annotation.documentbody`** is read for the notes a page returns (also through a link-entity, named or with all
  attributes) but cannot be filtered, sorted, aliased, joined on or aggregated (400). A page whose bodies add up to more
  than `Files:MaxNoteBodyBytesPerRead` (64 MB by default) is a 400.
- **`$expand`** of the per-target navigations of the activity lookups (`regardingobjectid_account_email`,
  `regardingobjectid_account` on `activitypointers`, `activityid_email`, `partyid_contact`) and of
  `fileattachment`'s `objectid_{table}` is supported, one level deep.

## Hierarchical operators

| Operator | SDK `ConditionOperator` | Returns |
|---|---|---|
| `under` | `Under` | The descendants, without the record itself |
| `eq-or-under` | `UnderOrEqual` | The descendants and the record |
| `above` | `Above` | The ancestors, without the record itself |
| `eq-or-above` | `AboveOrEqual` | The ancestors and the record |
| `not-under` | `NotUnder` | Everything except the record and its descendants |

The operators (SDK `ConditionOperator.Under`, `UnderOrEqual`, `Above`,
`AboveOrEqual`, `NotUnder`) walk a table's hierarchical relationship (`IsHierarchical`: `account_parent_account`,
`user_parent_user`, a custom self-referencing relationship imported with a solution as hierarchical). One relationship per table can be
hierarchical.
- The condition's attribute is the table's primary key, or a lookup to a table that has a hierarchical relationship (for
  example `parentaccountid`, or `owningbusinessunit`); the value is the id of the referenced record. A table without a
  hierarchical relationship is a 400.
- On `ownerid` (targets `systemuser` and `team`) the operators walk the user hierarchy (`user_parent_user`), the one that
  applies to owners: a team id matches nothing (`not-under`: every row). Any other lookup with several targets (a Customer
  lookup such as `contact.parentcustomerid`, or another polymorphic lookup) is refused with 400 ("not supported on the
  multi-target lookup") rather than walking one of its targets; filter on the target's primary key through a link-entity
  instead. The checklist compares this with Dataverse (hierarchy security, item 1 (u)).
- `under` returns the descendants without the record itself, `eq-or-under` with it; `above` and `eq-or-above` walk up.
  `not-under` excludes the record and everything below it. An id that does not exist matches nothing (`not-under`: every
  row).
- The walk is a recursive query limited to 100 levels (never an error) and stops on a cycle in the data. It ignores read
  security, as `CountChildren` does in Dataverse; the read filter still applies to the rows returned.
- `eq-useroruserhierarchy` matches a lookup or uniqueidentifier column that equals the caller or one of the caller's
  reports, down to the configured depth; `eq-useroruserhierarchyandteams` adds the teams of the caller and of those
  reports. The business unit rule of hierarchy security does not apply. With the hierarchy model switched off they fail
  with 400.
- `rowaggregate="CountChildren"` in FetchXML is refused with 400 ("not supported") instead of being ignored.
- The OData query functions (`Microsoft.Dynamics.CRM.Under` and the others) are not supported in `$filter`, like every
  query function: they fail with 400 ("Dataverse query functions are not supported in $filter"); use FetchXML
  (`?fetchXml=`). Support for the query functions is a backlog item (#50).

## Counting rows

`$count=true` and FetchXML `returntotalrecordcount="true"` give the same result: `@odata.count` with the number of
matching records, up to 5,000 for standard tables and 500 for elastic tables (Learn: "Count rows by using OData"). The SDK
gets the same value in `EntityCollection.TotalRecordCount`, and `TotalRecordCountLimitExceeded` is `true` when more records
match. When `Prefer: odata.include-annotations` names `Microsoft.Dynamics.CRM.totalrecordcount`,
`Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded`, `Microsoft.Dynamics.CRM.*` or `*`, the response also carries
`@Microsoft.Dynamics.CRM.totalrecordcount` (`-1` when no count was asked for) and
`@Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded`. An aggregate query cannot ask for a count.

## Known deviations

Each point below is a stated difference from Dataverse or an xrmverse-only limit; none is silent.

- Dates are evaluated in UTC (also `dategrouping`).
- Query functions (`Microsoft.Dynamics.CRM.*`) are not supported in `$filter`, the hierarchical ones included.
- Money values are formatted with the symbol and precision of the record's currency (`{money}_base` with the base currency's),
  in en-US digit grouping, ignoring per-user number formats; distinct queries format money in a record currency without symbol (`N2`) while `{money}_base` still gets the base symbol, so one
  distinct result can mix formats; negative amounts are always `-<symbol><amount>`; and aggregate results have no formatted
  value. See [Currencies](currencies.md#formattedvalue-of-money).
- `$skip` is not supported (as in Dataverse).
- `top` takes precedence over paging.
- Ordering by lookup sorts by id, not by name.
- Global option set labels are present only if embedded in the attribute.
- `$expand` of a single-valued navigation property accepts `$select` and a nested `$expand` of single-valued navigation
  properties, at any depth, as Dataverse does
  (#190). The nested record is written inside its parent and
  `@odata.context` lists it as `parent(a,b,child(c))`. A query can carry at most 15 `$expand` items, all levels counted (the
  link-entity nesting limit below applies too). `$filter`, `$orderby`, `$top` and `/$ref` inside a single-valued `$expand`
  are a 400: Microsoft Learn documents them for collection-valued navigation properties only.
- Multi-valued `$expand` and polymorphic `$expand` are not supported, except `{activity}_activity_parties` and the
  per-target navigations of the activity and note lookups (see above); Customer and Owner lookups cannot be expanded.
- A party list cannot be a column of a link-entity, and an empty party list is left out of the record (whether Dataverse
  returns an empty collection is in the activities checklist).
- `dategrouping="week"` uses `DATEPART(week)`: the week number depends on the SQL Server `DATEFIRST` setting.
- Nesting limits (filters 32, link-entity 10) and total parameter limit (2000), not present in Dataverse.
- A `distinct` query includes the primary key only if requested (explicit attribute or all columns); otherwise
  the returned records have no id and there is no paging-cookie.
- The conditions of a link-entity can refer only to itself or to link-entities declared earlier (otherwise 400).
- `ConditionExpression.CompareColumns` and `ColumnSet.AttributeExpressions` are not supported (400).

## Calculated and formula columns

A [calculated column](calculated-columns.md) is computed inside the query that reads it, so it can be selected, filtered, sorted, aggregated and read through a link entity like a stored column. A [formula column](formula-columns.md) (Power Fx) is computed the same way and is selected, filtered and sorted in FetchXML, QueryExpression, OData and on the [SQL endpoint](sql-endpoint.md). A query reads at most 50 distinct calculated and formula columns together (a 400 beyond that); a chain of calculated columns is at most 5 deep, a chain that holds a formula column at most 10. Sorting by one is applied even where Dataverse disables it. A formula column compares text with the database collation (case-insensitive by default), as a stored column does.

## Elastic tables

Queries on an [elastic table](elastic-tables.md) accept a `partitionId` parameter, default to a page size of 500 and refuse link entities (`0x80048d0b`), `$expand` and a few sort and distinct combinations; `partitionId` on a standard table is a 400.

On an elastic table, `ExecuteCosmosSqlQuery` adds filters and projections over the paths inside JSON columns. It accepts `SELECT ... FROM c WHERE ... ORDER BY` with comparisons, `IS_DEFINED`, `NOT`, `AND` and `OR`, and refuses every other construct with a 400 that names it; see [Querying JSON columns](elastic-tables.md#querying-json-columns).

## See also

- [Bulk operations](bulk-operations.md), [Elastic tables](elastic-tables.md), [Calculated columns](calculated-columns.md)
- [Security](security.md): how read security filters every query
- [SQL endpoint](sql-endpoint.md): the same engine over T-SQL
