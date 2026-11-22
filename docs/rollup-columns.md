# Rollup columns

[← xrmverse documentation](../README.md#documentation)

## Introduction

A rollup column holds an aggregate (a sum, a count, a minimum, a maximum or an average) of the rows related to a row, or of the
rows below it in its hierarchy. Unlike a [calculated column](calculated-columns.md), which is computed each time it is read, a
rollup column is **stored**: background jobs recalculate it and write the result into the row, together with the time of the
calculation and its state. Between two calculations the value is the one last written, so it can be behind the related rows.
Because it is stored, it filters, sorts and aggregates like any column, in FetchXML, QueryExpression, OData and the
[SQL endpoint](sql-endpoint.md).

The Microsoft documentation describes the feature in
[Define rollup columns that aggregate values](https://learn.microsoft.com/power-apps/maker/data-platform/define-rollup-fields)
and, for developers, in [Specialized columns using code](https://learn.microsoft.com/power-apps/developer/data-platform/specialized-columns).
This page says what xrmverse supports, how a definition gets in, how and when values are calculated, and what is refused.

### What has been compared with Dataverse

The XAML that Dataverse keeps for a rollup definition is not documented. The shapes
xrmverse reads come from a parity export of a licensed environment (`xrmCalcTest` 1.0.0.4, four rollup columns on `account`;
`tests/fixtures/rollup/real`), from `xrmCalcTest` 1.0.0.5 (four more), and from a run on a licensed environment where eight
rollups were recalculated and filters were added in the classic rollup designer (`tests/fixtures/rollup/real/designer`). The
values of that run are encoded in the integration test `Parity_values_match_the_licensed_environment_run`. Where this page says
*confirmed*, it comes from one of those. Every point marked *assumed* is our choice or our reading of the Learn pages; treat it
as "not yet compared with Dataverse". These points are tracked in issue #20.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Storage | Stored; recalculated by background jobs | Same |
| How you define one | `CreateAttribute`/`UpdateAttribute` (SDK, Web API), `xrmd schema` (`rollup:`), solution import | **Extra**: Dataverse defines them in the maker portal |
| Jobs | A Mass Calculate Rollup Field job per column and an incremental job per table; `CalculateRollupField` | Same jobs and message |
| Job control | Postpone and change the recurrence (1 hour to 365 days) | **Narrower**: no pause, resume, cancel or delete |
| Empty sets, `AVG`, Currency | Empty set gives state 1, `AVG` counts empty values as 0, Currency converted per row | **Confirmed** on a licensed environment |
| Hierarchy rollups | Include the row itself; jobs walk 100 levels | See [Semantics](#semantics) |
| `rollupfield` table | Not modelled; `regardingobjectid` of the jobs stays empty | **Narrower** |
| Audit | Rollup writes are never audited | See [Known limits](#known-limits) |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Check what can be rolled up | Types, aggregates, sources, filters | [Supported](#supported) |
| Define a column | SDK, Web API, `xrmd schema`, solution | [Defining a rollup column](#defining-a-rollup-column) |
| Know when values change | Mass and incremental jobs | [Jobs](#jobs) |
| Recalculate one row now | `CalculateRollupField` | [`CalculateRollupField`](#calculaterollupfield) |
| Read the state of a value | Companion columns | [Companions and states](#companions-and-states) |

## Supported

### Types and aggregates

A rollup column can be a Whole number (`Integer`), Decimal, Currency (`Money`) or Date and time column. The aggregate it uses
must suit both the rollup column and the column it aggregates:

| Rollup column | `COUNT` | `SUM`, `AVG` | `MIN`, `MAX` |
|---|---|---|---|
| Whole number | any column | of a Whole number | of a Whole number |
| Decimal | — | of a Decimal or a Whole number | of a Decimal or a Whole number |
| Currency | — | of a Currency column | of a Currency column |
| Date and time | — | — | of a Date and time column with the same `DateTimeBehavior` and format |

In the XAML the aggregates are the operators `Sum`, `Count`, `Maximum`, `Minimum` and `Average` (all confirmed by the parity
exports). A Currency rollup aggregates the money column itself, never its `_base`.

Any other type of column (text, Choice, Yes/No, Double, …) cannot be a rollup column, and an aggregate outside the matrix is
refused when the column is defined (see [Refused](#refused)).

### What a rollup reads

- **Related rows.** The rows of another table that point to the row through a lookup: a one-to-many relationship from the
  rollup's table to the related table (for example `account` → `xrmv_calcorder` through `xrmv_accountid`). A Customer lookup
  works through the relationship of the target table. The relationship cannot be self-referential, and cannot lead to
  `activitypointer` or `activityparty`. Many-to-many relationships cannot be used.
- **The hierarchy.** On a table with a hierarchical relationship (such as `account_parent_account`), a rollup can aggregate over
  the row **and every row below it**. With related rows as well, it aggregates the related rows of all those rows; without, it
  aggregates a column of the rows of the hierarchy themselves (confirmed shape: `account.xrmv_treebudget`, the sum of
  `xrmv_budget` over the hierarchy). The row itself is included (confirmed: a parent with budget 10000 and two children with
  2500 and 500 gives 13000).
- **What can be aggregated and filtered.** Simple columns, and *basic* calculated and [formula](formula-columns.md) columns:
  those whose `SourceTypeMask` is 1 (they read simple columns of their own row only); a basic formula column also must not use
  `UTCNow` or `UTCToday` (Learn), nor `Now` or `IsUTCToday` (an xrmverse extension of that rule: they read the current time too). A rollup column, its companions, its `_base`, a calculated or formula
  column that reads a related row, a chain or a rollup, and a formula column that reads the current time cannot be used.

### Filters

A rollup can filter the related rows, and the rows of its own table (on a hierarchy, the rows that count; without a hierarchy,
the row itself: a row that does not match gets the value of an empty set). Each filter compares a column of the filtered row
with a constant:

- `Equal`, `NotEqual`, `GreaterThan`, `GreaterEqual`, `LessThan`, `LessEqual`, `Null`, `NotNull`, and on text `Contains`,
  `DoesNotContain`, `BeginsWith`, `EndsWith`.
- A Yes/No, Choice, Status or Status Reason column is compared only with `Equal` and `NotEqual`, and only with an option that
  exists. A lookup can only be tested with `Null` and `NotNull`. An empty constant means "has no value" (`Equal`) or "has a
  value" (`NotEqual`).
- Several conditions on the same rows are combined with **and** (confirmed: the classic designer writes them as an
  `EvaluateLogicalCondition` with `And`). An **or** between conditions is refused until an export shows how Dataverse writes it.
- On a hierarchy, a filter on the rows of the table applies to the row itself too (confirmed: a parent whose own column does
  not match the filter does not count its own related rows).
- There is no implicit filter on `statecode`: inactive related rows count unless a filter leaves them out (assumed).

## Defining a rollup column

The paths are those of [calculated columns](calculated-columns.md#defining-a-calculated-column), with `SourceType` 2; all of them
end in the same validation.

| Path | How |
|---|---|
| Solution import | An `<attribute>` with `SourceType` 2 and a `FormulaDefinitionFileName` naming the XAML file (see [Solution files](#solution-files)) |
| SDK | `CreateAttributeRequest` or `UpdateAttributeRequest` with `SourceType = 2` and the XAML in `FormulaDefinition` |
| Web API | `POST` or `PUT` of an attribute under `EntityDefinitions(…)/Attributes` with `SourceType` 2 and `FormulaDefinition` |
| `xrmd schema apply` | The key `rollup:` on a column, holding the XAML |

**An xrmverse extension.** Learn says rollup formulas are set in the maker portal only. xrmverse also accepts the definition over
the metadata API, so tools that write metadata can create one. What Dataverse answers to such a request is not yet compared.

A schema file:

```yaml
tables:
  - name: account
    columns:
      - name: xrmv_ordertotal
        type: decimal
        rollup: |
          <Activity ...>the XAML of the definition</Activity>
```

`rollup:` is allowed on the types `int`, `decimal`, `money` and `datetime`, cannot be empty and cannot be combined with
`calculated:`. The schema export writes it back (as a literal block, or quoted when the text holds a carriage return or a byte
order mark), so a schema file round-trips. In a schema file, calculated and rollup columns are applied in document order: put a
rollup after the basic calculated columns it reads.

**The XAML.** xrmverse reads the shape the maker editor writes (confirmed on the export): a root sequence holding the three
sections `Source` (the hierarchical relationship, or none, and the filters of the row's own table), `Target` (the related
relationship and the filters of the related rows) and `Aggregate` (the aggregated column and the aggregate). Each section uses
only its own variables. Anything else is refused, naming the element; xrmverse never evaluates a shape it does not know. The
entries are listed in the step catalog of [Classic workflows](workflows.md) (`Shape:Rollup*`, `EvaluateExpression:Sum` …).

**Change rules.**

- `SourceType` cannot change: a simple or calculated column cannot become a rollup, and a rollup cannot become anything else.
- A `FormulaDefinition` can change; it is validated again and the column's mass calculation is rescheduled (see
  [Jobs](#jobs)). An update that changes nothing reschedules it too, as Learn describes.
- A rollup column is never valid for create or update and never required; the server sets these. `MinValue` and `MaxValue`
  are kept (an export writes them) and bound the value.
- The `SourceTypeMask` of a rollup is 0. A calculated column that reads a rollup or one of its companions gets bit 16, and
  shows the value last calculated; so does a formula column that reads a rollup (it cannot read the companions).
- Deleting a column, lookup, relationship or table that a rollup reads is refused, naming the rollup. Delete the rollup first.
- Changing the `DateTimeBehavior` of a date a rollup reads is refused.
- A change to a basic calculated or formula column that a rollup aggregates or filters on, which makes it no longer basic, is
  refused, naming the rollup.
- Elastic tables have no rollup columns, and a rollup cannot aggregate the rows of an elastic table.

## Companions and states

Each rollup column `<col>` gets two columns of its own, generated by the server (an export does not carry them, confirmed):

| Column | Type | Holds |
|---|---|---|
| `<col>_date` (`<SchemaName>_Date`) | Date and time (User local, assumed) | When the value was last written |
| `<col>_state` (`<SchemaName>_State`) | Whole number | The state of the last calculation |

A Currency rollup also has its usual `<col>_base`. The companions have `AttributeOf` set to the rollup, are read-only, and go
with the rollup when it is deleted. They cannot be updated or deleted on their own. A table column already named like a
companion makes the rollup's creation fail.

The states are those Learn lists: 0 `NotCalculated`, 1 `Calculated`, 2 `OverflowError`, 3 `OtherError`, 4 `RetryLimitExceeded`,
5 `HierarchicalRecursionLimitReached`, 6 `LoopDetected`, 7 `CurrencyMissing`. xrmverse produces 0, 1, 2, 5, 6 and 7; a failed
run fails its job instead of writing 3 or 4.

**Initial values.** When a row is created, a rollup over related rows is calculated at once: the new row has no related rows,
so it gets the value of an empty set with state 1 and `_date` = now (confirmed). A rollup over the hierarchy only gets state 0
and a null value until a job or `CalculateRollupField` runs (confirmed). When a rollup column is added to a table, the existing
rows get state 0, `_date` = now, and the value 0 for a `COUNT` and for a `SUM` that is neither over a hierarchy nor Currency;
null otherwise, until the mass job runs (as the 1.0.0.4 export showed).

## Semantics

The rules below are confirmed by the licensed run where marked; the others are xrmverse's reading of Learn (assumed).

- **Truncation before aggregating** (assumed, Decimal only). Each aggregated Decimal value is cut (not rounded) to the rollup
  column's precision before it is aggregated, as Learn's example shows: 10.009 and 0.004 summed into a column with two decimals
  give 10.00. Whole numbers are not cut, and Currency values are not cut either (see below).
- **Empty set** (confirmed). `COUNT`, `SUM` and `AVG` give 0, `MIN` and `MAX` give null, and the state is 1 (Calculated), on
  `CalculateRollupField`, on the mass job and on Create. A `SUM` over a hierarchy whose rows have no value gives 0.
- **`COUNT`** counts the rows whose aggregated column has a value (nulls are not counted).
- **`AVG`** counts a related row whose column is empty as 0 (confirmed: 1500, 300 and an empty value average to 600). It is
  computed with ten decimals and rounded half away from zero to the column's precision; for a Whole number, to a whole number
  (rounding assumed).
- **Currency** (confirmed). Each related row is converted to the base currency from its full value (value ÷ the row's
  `exchangerate`, not its rounded `_base`), and the converted values are summed without rounding: 200 and 80 at rate 12 plus
  40 in the base currency give 63.333332 in Dataverse. The rollup's `_base` is that sum, and its value is `_base` × the row's
  `exchangerate`. xrmverse stores money with four decimals, so it keeps 63.3333 where Dataverse shows 63.333332. On a row with
  no related rows Dataverse leaves `_base` empty, while xrmverse writes 0. A Decimal rollup of a column on rows in different
  currencies does not convert (confirmed). A row without a currency gets a null value and state 7. When the row's currency changes, the stored value keeps
  the old rate until the next calculation (the usual currency stamping leaves rollup columns alone).
- **Overflow.** A result outside the column's `MinValue`/`MaxValue`, outside its SQL type, or a `_base` outside
  ±922337203685477 gives a null value and state 2.
- **Hierarchy limits.** Jobs walk at most 100 levels below a row: a row with more gets a null value and state 5. A loop in the
  hierarchy (a row found again below itself, possible after a migration import) gives the rows on that loop state 6. Every
  other row of the same run is calculated normally.
- **Error states** (2, 5, 6, 7) store a null value and a null `_base`; they never keep the previous value.
- **Dates.** `_date` is written in UTC. A job run writes only the rows whose value or state changes (and, in the incremental
  job, the rows touched since its last run), so `_date` on an unchanged row keeps the time of its last write.

## Jobs

Two kinds of system jobs (`asyncoperation` rows owned by SYSTEM) keep rollup values current. Their `operationtype` values and
names follow Learn; between runs they wait in state 1 (Suspended), status 10 (Waiting), and they restart after a host restart.

| Job | `operationtype` | One per | Runs | Recalculates |
|---|---|---|---|---|
| Mass Calculate Rollup Field | 58 | rollup column | `Rollups:MassCalculationDelay` (default 12 hours) after the column is created or updated; **at once** when it comes from a solution import or a restored solution layer; then again 10 years later | every row of the table |
| Calculate Rollup Field | 57 | table with rollups | every hour by default (`recurrencepattern` `FREQ=HOURLY;INTERVAL=1`), first run one hour after creation | for each rollup whose mass job has run: the rows whose value or state would change, and the rows touched since the last run (the parent of a related row created, changed, moved or deleted, and, over a hierarchy, its ancestors) |

- The incremental job is created with the table's first rollup and deleted with its last. A column's mass job is deleted with
  the column. At start, the host checks the jobs against the metadata: missing jobs are created, the jobs of columns or tables
  that are gone and duplicate jobs are removed, and a failed rollup job is replaced by a new one. A mass job whose recorded
  definition is not the column's current one (a change the host had not yet passed to the jobs when it stopped) is due again after
  `Rollups:MassCalculationDelay`, as after an update.
- **Transient failures.** When a run meets a database failure that trying again can cure (a deadlock, a command or lock timeout,
  a database or connection that is not available for now), it is logged and the job waits `Rollups:TransientRetryDelay`
  (default 5 minutes, **an xrmverse setting**: Learn documents no such delay), then runs again; a mass job that has not completed
  keeps no `completedOn`, so the incremental job still leaves its column alone. Any other failure (a definition the calculation
  refuses, another SQL error) ends the job as Failed, and the host replaces it at its next start. A column that fails in an
  incremental run is logged and tried again at the next run, the other columns still run.
- A change of definition while the column's mass job runs restarts the delay when the run ends, so old and new values are never
  mixed. Until a column's mass job has run, the incremental job leaves that column alone.
- The job rows' `data` holds the table, the column and the bookkeeping of the last runs as JSON; `name` is
  `Mass Calculate Rollup Field: <table>.<column>` or `Calculate Rollup Field: <table>` (our own wording). `regardingobjectid`
  stays empty: the `rollupfield` table is not modelled.
- **Writes.** A job writes only the rollup's value, `_base`, `_date` and `_state`, with one SQL statement, as the system: no
  plug-in step runs, no audit entry is written, `modifiedon` and `modifiedby` do not change, so no workflow starts, as Learn
  describes. `versionnumber` moves, so [change tracking](change-tracking.md) reports the row (assumed).

**Administering the jobs.** A caller with Write on `asyncoperation` can:

- **Postpone** a job: an Update of `postponeuntil` alone, to a date.
- **Modify the recurrence** of a Calculate Rollup Field job: an Update of `recurrencepattern` alone, to
  `FREQ=HOURLY|DAILY|WEEKLY;INTERVAL=n` (`INTERVAL` defaults to 1; names in any case, spaces and one trailing `;` are accepted,
  as in Learn's own examples). The new pattern applies from the job's next run, counted from its `recurrencestarttime`.
  Below one hour is refused with `0x8004D2A2`. A period longer than 365 days is refused: **an xrmverse limit**, which keeps
  every rollup recalculated at least once a year.

A job cannot be cancelled, deleted, paused or resumed (400): postpone it instead. Learn describes pause and resume; xrmverse does
not replicate them.

## `CalculateRollupField`

The message calculates one rollup column of one row at once, synchronously, as the form's Recalculate button does.

- **SDK.** `CalculateRollupFieldRequest` with `Target` (an `EntityReference` to the row) and `FieldName` (the rollup column). The
  response's `Entity` holds the value, `<col>_date`, `<col>_state` and, for Currency, `<col>_base`.
- **Web API.** The function
  [CalculateRollupField](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/calculaterollupfield):
  `GET [Organization URI]/api/data/v9.2/CalculateRollupField(Target=@t,FieldName=@f)?@t={'@odata.id':'accounts(<id>)'}&@f='xrmv_ordertotal'`
  (the parameters may also be written inline). It answers 200 with the row as an entity of its table: the primary key and the
  same columns.
- **Security.** The caller needs Write access on the row (and on its table); without it the answer is 403 `0x8004E027`. Access
  to the related rows is not needed (see [Security](#security)). Learn's maker page asks for Write; its SDK reference says
  Read; which one Dataverse enforces is on the checklist.
- **Online limits.** The calculation is refused when it would read more than 50,000 related rows (400 `0x8004E025`) or when the
  row has more than 10 levels of hierarchy below it (400 `0x80060547`). The jobs have neither limit. An online calculation has a
  time limit of 120 seconds; the jobs have none.
- It writes the row like a job (no audit, `modifiedon` unchanged) but inside the request's transaction, and always writes
  `_date`. It does not recalculate other rollups that read this one; they catch up at their next job run, as Learn says.
- It is a pipeline message: plug-in steps can be registered on it, and a failing post-operation step rolls the write back.
- `Target` must name the row by its id; a row given by alternate key on a standard table is a 400. A column that is not a rollup
  is a 400 `0x80060554`. A missing row is a 404.
- The answer applies the caller's [field security](field-security.md): a secured rollup the caller cannot read comes back with
  its value and companions null.

## Writing and processes

- A value sent for a rollup column, its `_date`, `_state` or `_base` in a `Create`, `Update`, `Upsert` or `*Multiple` is
  ignored: it is not in the plug-in target, not stored and not audited (whether Dataverse ignores or refuses it is not yet
  compared).
- [Auditing](auditing.md): rollup columns and their companions are never audited, whatever the column's audit setting: nobody
  writes them, and neither the jobs nor `CalculateRollupField` write audit entries.
- [Classic workflows](workflows.md): activating a process whose wait condition (or wait timeout) reads a rollup column or a
  companion, or whose update trigger lists one, is refused with 400 `0x80060557`; the process stays a draft. A rollup update
  never starts a workflow.

## Security

The aggregate reads the related rows, and the rows of the hierarchy, **with system rights**, as Learn describes: a user's row
security and the related columns' [field security](field-security.md) do not change the result. So a rollup can show a user an
aggregate of rows or columns they cannot read.

The rollup column keeps its own field security, like any column. **If the rows or columns it aggregates are restricted, secure
the rollup column too** (set `IsSecured` on it and grant read to the right profiles); its `_date`, `_state` and `_base` follow it.

## Solution files

A solution carries a rollup column as `<SourceType>2</SourceType>` and a `<FormulaDefinitionFileName>` in `customizations.xml`,
with the XAML as a separate file, `/Formulas/<table>-<column>.xaml` (confirmed).

- **Import** reads the file verbatim into `FormulaDefinition`. Calculated and rollup columns are created after the simple columns
  and relationships of the import, in dependency order: a rollup after the basic calculated or formula columns it reads, a
  calculated or formula column after the rollups it reads. Each imported rollup's mass calculation job is due at once, as Learn describes; so is the
  job of a rollup whose definition comes back when an upper managed layer is uninstalled.
- **The `_base` of a Currency rollup.** Dataverse exports it as an attribute of its own with `SourceType` 2, no file and a
  `CalculationOf` naming the Currency column (confirmed). Import reads it as the column's base-currency companion; export writes
  it the same way.
- **Companions.** `_date` and `_state` are not in an export (confirmed); if a file carries them, they are skipped as generated
  (assumed).
- **Export** writes `SourceType` 2, the file name and the XAML file byte for byte (with its byte order mark, if it had one), the
  `_base` as above, and no companions. The parity export round-trips byte for byte.
- **Uninstall and delete.** Deleting a solution removes the calculated columns that read rollups first, then the rollups, then
  what they read.
- A column of a type that cannot be a rollup is an import error with the code `0x8006053e`.

## Limits

| Limit | Value |
|---|---|
| Rollup columns per table | 50 (`0x80060543`) |
| Rollup columns per organization | 200 (`0x80060542`) |
| Related rows of an online calculation | 50,000 (`0x8004E025`) |
| Hierarchy depth of an online calculation | 10 levels below the row (`0x80060547`) |
| Hierarchy depth of a job | 100 levels below the row (deeper: state 5) |
| Incremental job recurrence | 1 hour to 365 days |

The limits of 50 and 200 follow Learn's maker page; Learn's developer page still says 10 and 100 (on the checklist).

## Refused

| What | Answer |
|---|---|
| A rollup column on a server without the translator | 400 `0x80040203` `Rollup columns are not available on this server.` |
| A type that cannot be a rollup | 400 `0x8006053e` |
| An aggregate the rollup's type cannot hold (for example `COUNT` into a Decimal, `SUM` into a date) | 400 `0x80060545` |
| An aggregate that does not suit the aggregated column (for example `SUM` of a date) | 400 `0x8006053a` |
| An aggregated column whose type does not suit the rollup's (for example a Currency rollup of a Decimal) | 400 `0x8006053d` |
| A date rollup of a date with another behavior or format | 400 `0x80060544` |
| Aggregating a rollup, a companion, a `_base`, or a calculated or formula column that is not basic | 400 `0x8006053c` |
| Filtering on such a column: own rows, related rows | 400 `0x80060548`, `0x80060549` |
| A filter condition that is not valid (operator, column, constant): own rows, related rows | 400 `0x80060538`, `0x80060539` |
| Neither related rows nor a hierarchy | 400 `0x80060533` |
| A related relationship that is not a one-to-many from the table through the named lookup | 400 `0x80060534` |
| A hierarchy on a table without that hierarchical relationship | 400 `0x80060535` |
| A self-referential related relationship | 400 `0x80060551` |
| Related rows of `activitypointer` or `activityparty` | 400 `0x80060552` |
| The 51st rollup of a table, the 201st of the organization | 400 `0x80060543`, `0x80060542` |
| A companion name already taken | 400 `0x80060556` |
| Updating or deleting a companion | 400 `0x80060555` |
| Deleting a column, lookup, relationship or table a rollup reads | 400 `0x80060541`, naming the rollup |
| Changing the behavior of a date a rollup reads | 400 `0x80097214` |
| A XAML shape outside the catalog (an **or**, an unknown element, a variable of another section) | 400 `0x80040203`, naming the element |
| A rollup on, or over, an elastic table | 400 |
| `CalculateRollupField` without Write access | 403 `0x8004E027` |
| `CalculateRollupField` on a column that is not a rollup | 400 `0x80060554` |
| `CalculateRollupField` over 50,000 rows or 10 levels | 400 `0x8004E025`, `0x80060547` |
| A rollup in a wait condition or an update trigger | 400 `0x80060557` |
| A [duplicate detection](duplicate-detection.md) rule on a rollup or one of its companions | 400 `0x80040203` `Duplicate detection rules cannot use the rollup column '…'.` (a companion: the refusal of columns of another column) |
| A recurrence below one hour | 400 `0x8004D2A2` |
| A recurrence of another shape or longer than 365 days | 400 `0x80040203` |
| Cancelling, deleting, pausing or resuming a rollup job | 400 |

The codes are Dataverse's, from Learn's [web service error codes](https://learn.microsoft.com/power-apps/developer/data-platform/reference/web-service-error-codes);
the texts are xrmverse's own and name the column.

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- **The incremental job aggregates the whole table.** To catch related rows that moved to another parent or were deleted, each
  run computes every row of the table for each rollup and writes only those that change. On a large table every hourly run is a
  full aggregate.
- **A mass run is one `UPDATE` over the whole table** in one transaction. On a large table SQL Server may lock the table for the
  statement's duration and block writers.
- **One job at a time.** The background worker runs one job at a time, so a long mass run delays other queued jobs
  (asynchronous plug-ins, workflows).
- **A Postpone sent while the job runs** is overwritten by the run's own outcome.
- **The `rollupfield` table is not modelled.** Jobs have no `regardingobjectid`, and a query on `rollupfield` fails as an unknown
  table.
- **Pause and resume** of the jobs are not replicated.
- **The `CalculateRollupField` answer** carries the state in `<col>_state` of its entity. Learn's developer page on
  [rollup columns](https://learn.microsoft.com/power-apps/developer/data-platform/specialized-columns#rollup-columns) mentions a
  `CalculateRollupFieldResponse.FieldState`, while the SDK reference of
  [CalculateRollupFieldResponse](https://learn.microsoft.com/dotnet/api/microsoft.crm.sdk.messages.calculaterollupfieldresponse)
  lists only `Entity`; xrmverse answers `Entity` alone. Which one Dataverse sends is to be settled on the trial (checklist item 3).
- **Existing organizations** get the two new `asyncoperation` columns but keep their `operationtype` option set, so 57 and 58
  have no label there.
- **Migration.** [`xrmd migrate`](migration.md) carries a rollup column only with the exported solution that holds it; one
  outside the exported solutions is listed in `schema/unsupported.json` ("import it with its solution").
- **Currency precision.** Money is stored with four decimals, so a Currency rollup keeps four (63.3333 where Dataverse shows
  63.333332), and an empty set writes a `_base` of 0 where Dataverse leaves it empty.
- **Not yet compared with Dataverse:** every *assumed* point above (Decimal truncation, `AVG` rounding, `COUNT` with nulls,
  overflow, an **or** between filters, inactive rows), the error codes and the answers of the definition API, the Write versus Read check of
  `CalculateRollupField`, the job rows' names and texts, and the effect of a job on change tracking (issue #20).

## See also

- [Calculated columns](calculated-columns.md)
- [Currencies](currencies.md)
- [Configuration](configuration.md) (`Rollups:*`)
- [Solutions](solutions.md)
