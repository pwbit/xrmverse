# Calculated columns

[← xrmverse documentation](../README.md#documentation)

## Introduction

A calculated column is a column whose value is computed from other columns each time it is read. Nothing is stored for it:
xrmverse turns its definition into SQL and runs it inside the query that reads the column, so the value is always current. The
Microsoft documentation describes the feature in
[Define calculated columns](https://learn.microsoft.com/power-apps/maker/data-platform/define-calculated-fields) and, for
developers, in
[Calculated and rollup columns](https://learn.microsoft.com/power-apps/developer/data-platform/calculated-rollup-attributes).
This page says what xrmverse supports, how a definition gets in, the limits, the rules that follow from reading instead of
storing, and what is refused.

### What has been compared with Dataverse

The XAML that Dataverse keeps for a calculated column is not documented. The
shapes xrmverse reads were confirmed by a parity export from a licensed environment (`xrmCalcTest` 1.0.0.4, 27 calculated
columns; `tests/fixtures/calculated/real`), and the values Dataverse showed for four invented records were compared column by
column: where this page says *confirmed*, it is one of those. Where it says *assumed*, the behavior is our choice or our
reading of the Learn pages and has not been checked against a real environment. Those points are
tracked in issue #20; until they are checked, treat them as "not yet compared with Dataverse".

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| How values are produced | Computed in SQL inside the query that reads the column; nothing is stored | Same result for the supported shapes (**confirmed** on a parity export) |
| How you define one | `CreateAttribute`/`UpdateAttribute` (SDK, Web API), `xrmd schema` (`calculated:`), solution import | **Extra**: Dataverse documents the maker portal |
| Sources | Own row, a parent through one lookup, constants, current time, other calculated and formula columns (chain of 5, 10 with a formula column) | Same |
| Per query | At most 50 calculated columns, formula columns included | xrmverse limit |
| Sorting | Always works | **Differs**: Dataverse disables it for some columns |
| Writes, audit | Writes are ignored; no audit entries | Same |
| Overflow, division by zero | Null | **Confirmed** for the points marked so below |
| Formula columns | Supported (Power Fx), on their own page: [Formula columns](formula-columns.md) | See that page |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Check what can be calculated | Types, sources, operators, functions | [Supported](#supported) |
| Define a column | SDK, Web API, `xrmd schema`, solution | [Defining a calculated column](#defining-a-calculated-column) |
| Understand the result | Nulls, rounding, dates, text | [Semantics](#semantics) |
| Move it with a solution | XAML in `/Formulas/` | [Solution files](#solution-files) |

## Supported

- **Column types** that can be calculated: Text (`String`), Whole number (`Integer`), Decimal, Currency (`Money`), Yes/No
  (`Boolean`), Date and time, and Choice (`Picklist`). Memo, Double, multi-select, lookup, file and image columns cannot be.
- **Sources.** A calculated column reads columns of its own row, columns of a parent row reached through one lookup (for
  example `accountid.name`), constants, the current time, and other calculated columns and [formula columns](formula-columns.md).
  Of a lookup it reads the parent's columns, not the parent's own parents: a calculated column reads two tables at most.
- **Operators.** `+`, `-`, `*` and `/` on numbers.
- **Conditions.** `if` / `else` branches (one value is set per branch), combined with `and`, `or` and `not`. A condition
  compares with `Equal`, `NotEqual`, `GreaterThan`, `GreaterEqual`, `LessThan`, `LessEqual`, `Null`, `NotNull`, and on text
  `Contains`, `DoesNotContain`, `BeginsWith`, `EndsWith`. A Choice column is compared, or set, only with an option value that
  exists in its option set.
- **Functions.**

  | Group | Functions |
  |---|---|
  | Add to a date | `ADDHOURS`, `ADDDAYS`, `ADDWEEKS`, `ADDMONTHS`, `ADDYEARS` (number, date) |
  | Subtract from a date | `SUBTRACTHOURS`, `SUBTRACTDAYS`, `SUBTRACTWEEKS`, `SUBTRACTMONTHS`, `SUBTRACTYEARS` (number, date) |
  | Difference of two dates | `DIFFINMINUTES`, `DIFFINHOURS`, `DIFFINDAYS`, `DIFFINWEEKS`, `DIFFINMONTHS`, `DIFFINYEARS` (start, end), a whole number |
  | Text | `CONCAT` (two or more texts), `TRIMLEFT(text, count)`, `TRIMRIGHT(text, count)` |

  A function with the wrong number or kind of arguments is refused when the column is defined.
- **Where the value shows.** In `RetrieveMultiple` (FetchXML, QueryExpression, OData), `Retrieve`, `$select`, conditions,
  orders, aggregates and link entities, like any column. A Currency calculated column also has its `_base` companion,
  computed from the record's exchange rate (a record with no currency gives null).
- **`SourceTypeMask`.** Returned on every metadata read, computed from the definition: 1 reads a column of its own row,
  2 reads a column of the parent row, 8 reads another calculated or formula column, 16 reads a [rollup column](rollup-columns.md) or one
  of its companions; the bits are combined, and a simple column has 0. A value a client sends for it is ignored.
- **Reading a rollup column.** A calculated column can read a rollup column of its own row or of its parent, and its `_date`
  or `_state`. It gets the value last calculated by the rollup jobs, which can be behind the related rows. A rollup can in turn
  aggregate a calculated column only when it is basic (`SourceTypeMask` 1).

## Defining a calculated column

There are four ways in; all of them end in the same validation, so a definition that one path accepts every path accepts.

| Path | How |
|---|---|
| Solution import | A `<attribute>` with `SourceType` 1 and a `FormulaDefinitionFileName` that names the XAML file (see [Solution files](#solution-files)). The column is created after every simple column and relationship of the solution, so it can read columns created by the same import |
| SDK | `CreateAttributeRequest` or `UpdateAttributeRequest` with `SourceType = 1` and the XAML in `FormulaDefinition` on the column's metadata |
| Web API | `POST` or `PUT` of an attribute under `EntityDefinitions(…)/Attributes` with `SourceType` 1 and `FormulaDefinition` |
| `xrmd schema apply` | The key `calculated:` on a column, holding the XAML (see below) |

**An xrmverse extension.** Learn's documentation describes defining a calculated column in the maker portal and does not
describe defining one through the metadata API. xrmverse accepts the definition over the API as well, so the tools that write metadata can create one. What
Dataverse answers to a `CreateAttribute` that carries a formula is not yet compared.

A schema file:

```yaml
tables:
  - name: xrmv_calcorder
    columns:
      - name: xrmv_total
        type: money
        calculated: |
          <Activity ...>the XAML of the definition</Activity>
```

`calculated:` is allowed on the types `string`, `int`, `decimal`, `money`, `bool`, `datetime` and `choice`, cannot be empty,
and cannot be combined with `autoNumberFormat`. The schema export writes it back, so a schema file round-trips.

**Change rules.**

- `SourceType` cannot change: a simple column cannot become calculated, and a calculated one cannot become simple (400).
  A `FormulaDefinition` can change; it is validated again, and so is every calculated column that reads the changed column
  (a change that would make one of them invalid is refused, naming it).
- A calculated column cannot be required, takes no `MinValue` or `MaxValue` narrower than its type's widest range, and is
  never valid for create or update; the server sets these. The widest range is accepted because a Dataverse export writes it
  on every calculated Whole number (-2147483648 to 2147483647), Decimal (±100000000000) and Currency (±922337203685477)
  column (confirmed).
- Deleting a column that a calculated column reads (as a source, or as the lookup it follows) is a 400 `0x80040203` that names
  the dependents. The same holds for deleting a table whose columns another table's calculated column reads. Delete the
  calculated column first.
- Elastic tables refuse calculated columns.
- A table's primary name column cannot be calculated: a lookup to the table reads its name from that column.

## Limits

| Limit | Value |
|---|---|
| Chain depth: a calculated column that reads a calculated column that reads … | 5, counting the column itself; 10 once a [formula column](formula-columns.md) is in the chain |
| Calculated columns one query reads | 50 distinct columns, formula columns included; the next one is a 400 |
| Tables a definition reads | Its own and its parent's, through one lookup |
| Cycles | Refused when the definition is saved, naming the path |

## Semantics

Dataverse documents the functions but not every edge. These are the rules xrmverse follows; the ones marked *assumed* are
ours and are on the checklist.

- **Null.** Arithmetic with a null operand is null (confirmed). `CONCAT` treats a null part as empty (confirmed:
  `CONCAT(code, "-", account name)` without an account gives `ORD-003-`). A condition that compares a null value is false, so
  the otherwise branch applies (confirmed: `amount > 1000 and qty >= 2` with no amount gives No). A comparison with an empty
  constant means "has no value" (`Equal`) or "has a value" (`NotEqual`). A branch that sets an empty constant gives null; an
  empty constant anywhere else (an operand, a function argument) is refused when the column is defined.
- **Division.** Division by zero is null, not an error (confirmed); zero times a number is zero.
- **Result type** (assumed). The expression is cast to the column. Text is cut to the column's `MaxLength`. A Whole number that
  does not fit in 32 bits is null, not an error. A Decimal or Currency is rounded to the column's precision; a value that does
  not fit is null. A Date only column keeps the date part. A Choice column takes the literal option value of the branch.
- **Text.** `TRIMLEFT(text, n)` drops the first `n` characters and `TRIMRIGHT(text, n)` the last `n` (confirmed); dropping
  all of them gives an empty text (confirmed for a count equal to the length; more than the length is assumed).
- **Dates.** Computed in UTC: `ADD…` and `SUBTRACT…` are plain UTC arithmetic, also on a User local column across a daylight
  saving change (confirmed: one month before 15 November 23:00 UTC is 15 October 23:00 UTC). `DIFFIN…` counts the boundaries
  between the two dates, and is negative when the end is before the start. Boundary counting is confirmed for weeks (Monday
  to the next Monday less 7 hours 15 minutes is 1 week, where whole weeks would give 0; which day starts a week was not shown);
  hours and minutes are consistent with it and rule out truncation (7 hours 15 minutes back is -8 hours and -435 minutes,
  160 hours 45 minutes is 160 hours). The same day, month or year gives zero, as Learn states, so `DIFFINMONTHS`
  between 31 January and 1 February is 1 (assumed). A `DIFFIN…` whose two columns have
  different date behaviors (for example User local and Date only) is refused when the column is defined, as Learn
  describes. `ADDMONTHS` and `SUBTRACTMONTHS` follow SQL Server's month arithmetic at the end of a month. Dates are not shifted into a
  user's time zone for the calculation (what Dataverse does for User local columns is on the checklist).
- **Currency.** (assumed) A calculated Currency column is rounded to the column's precision; its `_base` is the value divided by the
  record's `exchangerate`. A parent's Currency column enters the formula as its `_base` times the record's `exchangerate`, as
  Learn describes.

## Writing

- A value sent for a calculated column in a `Create` or `Update` is ignored, as for any column that cannot be written. It is
  not in the plug-in target, not in the audit and not stored (whether Dataverse ignores or refuses it is not yet compared).
- The column is read-only in forms and in the metadata (`IsValidForCreate` and `IsValidForUpdate` are false).
- A [duplicate detection](duplicate-detection.md) rule cannot use a calculated column: it has no stored value to build
  match codes from (400).
- [Auditing](auditing.md): a calculated column has no audit entries. A change to its sources is audited on the sources.
- [Change tracking](change-tracking.md) has no effect of its own for a calculated column.

## Security

The expression reads its sources, and the parent row it follows, **with system rights**. Row security and the sources'
[field security](field-security.md) do not hide anything from the calculation, as in Dataverse. The calculated column keeps
its own field security: a user who cannot read it gets it left out, like any secured column.

So a calculated column can show a user a value computed from a column they cannot read. **If a source is secured, secure the
calculated column too** (set `IsSecured` on it and grant read to the same profiles).

## Sorting

Learn says that sorting by some calculated columns (those that read a parent, a chain, or the current time) is disabled in
Dataverse. xrmverse sorts them correctly, in `order` of FetchXML and QueryExpression and in `$orderby`. A correct order cannot
break a client that already copes with an ignored one; this is a documented difference in
[xrmverse compared with Dataverse](dataverse-comparison.md).

## Solution files

A solution carries a calculated column as `<SourceType>1</SourceType>` and a `<FormulaDefinitionFileName>` in
`customizations.xml`, and the XAML as a separate file in the archive.

- **Import** reads the file named by `FormulaDefinitionFileName`, takes it as UTF-8 text and keeps it verbatim in
  `FormulaDefinition`. A missing file, a file that is not UTF-8, a `FormulaDefinitionFileName` on a column with `SourceType`
  0 and a calculated column with no file are import errors that name the column.
- **Export** writes `<SourceType>`, `<FormulaDefinitionFileName>` (`/Formulas/<table>-<column>.xaml`, confirmed) and the XAML
  file byte for byte, with its byte order mark if it had one. Dataverse writes the files without a byte order mark and with
  CRLF line ends (confirmed); xrmverse keeps whatever the imported file had.
- **The `_base` of a calculated Currency column.** Dataverse exports it as an attribute of its own with `SourceType` 1, no
  `FormulaDefinitionFileName` and a `CalculationOf` that names the Currency column (confirmed). Import reads it as that
  column's base-currency companion; a `_base` like it beside a Currency column that is not calculated is refused as a
  calculated column with no file. Export writes it as it was read; a `_base` with nothing kept (its column created in xrmverse) is written
  with `SourceType` 1, no file and a `CalculationOf` naming the Currency column, after `AccuracySource`, as Dataverse does.
- **XAML shapes** (confirmed). The function operators are named in Pascal case (`AddDays`, `DiffInHours`, `Concat`,
  `TrimLeft`, ...; the upper-case names are refused), the current time is the operator `Now` (the workflow operator
  `RetrieveCurrentTime` is refused in a definition), a definition is wrapped in a condition with a single always-true
  branch, and a `TargetType` that is not the value's own type is the column's type (xrmverse refuses any other).
- **Uninstall and delete.** Deleting a solution (and uninstalling a managed one) removes its calculated columns first, before
  the relationships, columns and tables they read, including the calculated columns of other tables that read a deleted
  table. Deleting a referenced column on its own, outside such a set, is the 400 above.
- A rollup column (`SourceType` 2) is imported and exported too, with its XAML in the same folder: see
  [Rollup columns](rollup-columns.md#solution-files). A formula column (`SourceType` 3) is imported and exported too; the Power
  Fx formulas of a table share one file, `Formulas/<table>-FormulaDefinitions.yaml` (confirmed): see
  [Formula columns](formula-columns.md#solution-files). Calculated, rollup and formula columns are created in dependency order.

## Refused

| What | Answer |
|---|---|
| A calculated column on a server without the translator | 400 `Calculated columns are not available on this server.` |
| `SourceType` changed on update | 400 `The SourceType of a column cannot change: …` |
| A formula on a column that is neither calculated, rollup nor formula | 400 `A FormulaDefinition needs SourceType 1 (calculated), 2 (rollup) or 3 (formula): …` |
| Prompt column (`SourceType` 4), or another value | 400 `Prompt columns are not supported: …`, `SourceType n is not valid: …` |
| A type that cannot be calculated | 400 `A calculated column cannot be of type …: …` |
| `MinValue` or `MaxValue` on it, narrower than the type's widest range | 400 `MinValue and MaxValue cannot be set on a calculated column: …` |
| A calculated primary name column | 400 `The primary name column of a table cannot be a calculated column: …` |
| An empty constant used as a value | 400 `The calculated column '…' uses … with an empty value …`: an empty constant (`Nothing`) can only be compared with `Equal` or `NotEqual`, or be the value of a branch beside branches that set a value; a definition that sets an empty value in every branch is refused too |
| `ADDHOURS` or `SUBTRACTHOURS` on a Date only value | 400 `The calculated column '…' uses ADDHOURS on a date only value …` |
| A cycle | 400 `The calculated column '…' is part of a cycle of calculated columns: a -> b -> a.` (`… of formula or calculated columns` when a formula column is in it) |
| Chain too deep | 400 `The calculated column '…' chains more than 5 calculated columns: …` (`more than 10 formula or calculated columns` when a formula column is in it) |
| More than 50 in a query | 400 `A query cannot read more than 50 calculated columns: '…' is one more.` |
| A definition that cannot be read or translated | 400 `The calculated column '…' has a definition that is not valid: …`, or a message of the form `The calculated column '…' <reason>.` naming what is refused: an unsupported function, operator or condition, a wrong argument count or kind, a column that does not exist or has no value of its own, a record other than its own or its parent, a lookup that points to several tables, a Choice value that is not an option |
| Deleting a column a calculated column reads | 400 `0x80040203` `The component cannot be deleted because it is referenced by calculated column '…'.` |
| A change that makes a dependent invalid | 400 `The change of '…' would make the calculated column '…' invalid: …` |
| A duplicate detection rule on one | 400 `Duplicate detection rules cannot use the calculated column '…'.` |
| An autonumber format on one | 400 `…: an AutoNumberFormat cannot be set on a calculated column.` |
| One on an elastic table | 400 `… cannot be a calculated, rollup or formula column: elastic tables do not support it.` |

The texts are xrmverse's own.

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- System (non-custom) calculated columns are not modelled: only custom columns carry a calculated definition.
- Formula columns (Power Fx, `SourceType` 3) are computed in SQL like calculated columns but follow Power Fx rules (a blank
  number is 0, for example) and have their own page, [Formula columns](formula-columns.md). Rollup columns (2.12b) are a
  different mechanism (stored, recalculated by jobs) and have their own page, [Rollup columns](rollup-columns.md).
- No in-memory evaluation: every supported shape has a SQL translation, and anything outside the catalog is refused at
  definition time, so a value can never differ between two paths.
- The overflow, rounding and month-end rules, the time zone of a user, and the error codes are not yet compared with Dataverse
  (issue #20). The XAML shapes, the file naming, and the
  null, division, text and date arithmetic rules marked confirmed above were compared on the parity export.

## See also

- [Formula columns](formula-columns.md)
- [Rollup columns](rollup-columns.md)
- [Supported queries](queries.md#calculated-and-formula-columns)
- [Solutions](solutions.md)
