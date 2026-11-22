# Formula columns

[← xrmverse documentation](../README.md#documentation)

## Introduction

A formula column is a column whose value is computed from a Power Fx formula each time it is read (`SourceType` 3). As with a
[calculated column](calculated-columns.md), nothing is stored: xrmverse checks the formula when the column is defined, turns it
into SQL and runs it inside the query that reads the column, so the value is always current and can be filtered and sorted. The
formulas are parsed and type-checked with Microsoft's open-source Power Fx library (`Microsoft.PowerFx.Core`, MIT) and then
translated by xrmverse into SQL; no formula is ever evaluated in memory, so the value is the same on every read path.

The Microsoft documentation describes the feature in
[Work with formula columns](https://learn.microsoft.com/power-apps/maker/data-platform/formula-columns), lists the functions in
[Formula reference – Dataverse formula columns](https://learn.microsoft.com/power-platform/power-fx/formula-reference-formula-columns)
and, for developers, describes `SourceType` 3 in
[Specialized columns using code](https://learn.microsoft.com/power-apps/developer/data-platform/specialized-columns). This page
says what xrmverse supports, how a formula gets in, the rules it follows for blank values, types and dates, the limits, and what
is refused.

### What has been compared with Dataverse

A parity export from a licensed environment (`xrmCalcTest` 1.0.0.4, `tests/fixtures/solutions/RealExports`) holds three formula
columns on `xrmv_calcorder`, and the values Dataverse showed for them on four invented records were recorded:
`Upper(xrmv_code) & "-" & Text(xrmv_qty, "0")` (Text), `IsBlank(xrmv_duedate)` (Yes/No) and
`DateAdd(xrmv_startedon,3,TimeUnit.Days)` (User local date), as the export writes them.

A second run on the same licensed test environment, on 2026-11-19 (Q1: eleven formula columns created through the Web API on
eight invented rows, `tests/fixtures/formula`), settled blank operands, division by zero and error propagation, `Blank() = 0`,
`Round`, `Mod`, `Trim`, `Text` padding, a related column, `Weekday` on a User local date, the language of the formula, the
leading `=` and `SourceTypeMask`; then a formula column reading a failed one, and `Max` with blank arguments.

Where this page says *confirmed*, it is one of those runs. Everything else follows our reading of the Learn pages and is
**not yet compared with Dataverse**; those points are tracked in issue #20.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| How values are produced | Computed in SQL inside the query that reads the column; nothing is stored | Same values for the formulas of the two runs (**confirmed**); the rest is not yet compared |
| How you define one | `CreateAttribute`/`UpdateAttribute` (SDK, Web API), `xrmd schema` (`formula:`), solution import | Same over the Web API (**confirmed**); **Extra**: `xrmd schema` |
| Language of the formula | Invariant form only (`,` between arguments, `.` as decimal mark); a `;` is refused with a hint | **Narrower**: Dataverse's Web API reads the caller's separators (**confirmed**) |
| Leading `=` | Refused over the SDK and the Web API | Same (**confirmed**) |
| Related columns | One level, through a lookup to one table, named by its navigation property | Same (**confirmed**) |
| Column types | Text, Whole number, Decimal, Float, Yes/No, Date and time | **Narrower**: Choice is refused; Currency does not exist in Dataverse either |
| Blank values | A blank number is 0; `Max` and `Min` leave blank arguments out; `Blank() = 0` is false | **Confirmed** |
| Errors | Propagate and show as an empty value; `Error`, `IfError` and `IsError` are refused | **Confirmed** for arithmetic; **Narrower**: no error functions |
| Whole number results | Truncated toward zero | Not yet compared |
| Text comparisons | Database collation (case-insensitive by default) | Not yet compared; Power Fx's `=` is case-sensitive |
| Date parts | On Date only dates only | `Weekday` of a User local date is refused as in Dataverse (**confirmed**); the rest not yet compared |
| Sorting | Always works | **Differs**: Dataverse disables it for some formulas |
| Writes, audit | Writes are ignored; no audit entries | Writes not yet compared |
| Field security | A formula column cannot be secured; the sources' field security does not hide its value | Same for securing (Learn); the value shown is not yet compared |
| Duplicate detection | A rule on a formula column is refused | **Differs**: Learn says rules are not triggered on them |
| Limits | 1,000 characters, chain of 10, 50 computed columns per query, bounds on the SQL; `Max`/`Min` need SQL Server 2022 | xrmverse limits |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Check what a formula can use | Types, sources, operators, functions | [Supported](#supported) |
| Define a column | SDK, Web API, `xrmd schema`, solution | [Defining a formula column](#defining-a-formula-column) |
| Understand an empty value | Blanks, errors, chained columns | [Blank values and errors](#blank-values-and-errors) |
| Understand the result | Result types, `Text`, rounding, text functions | [Types and results](#types-and-results) |
| Work with dates | Behaviors, `DateAdd`, `DateDiff`, date parts | [Dates](#dates) |
| Stay within the limits | Length, chain, query, SQL size | [Limits](#limits) |
| Move it with a solution | `/Formulas/<table>-FormulaDefinitions.yaml` | [Solution files](#solution-files) |
| Read an error | The 400 texts | [Refused](#refused) |

## Supported

- **Column types.** Text (`String`, format Text only), Whole number (`Integer`, format None only), Decimal (precision 0 to 10,
  2 when none is given), Float (`Double`), Yes/No (`Boolean`) and Date and time (`DateTime`, any behavior). Currency and Choice
  formula columns are refused (see [Refused](#refused)).
- **What a formula reads.**
  - Columns of its own row by logical name (`xrmv_amount`), also written `ThisRecord.xrmv_amount`. A display name in single
    quotes (`'Order Code'`) is accepted when it names exactly one column of the table; the formula is stored as written.
  - A column of a related row through a lookup to one table, one level away, named by the lookup's **navigation property**
    (`ReferencingEntityNavigationPropertyName`, case as in the metadata): `xrmv_AccountId.name` (confirmed, 2026-11-19). The
    lookup's logical name (`xrmv_accountid.name`) or display name is refused with a 400 that names the navigation property to
    use; Dataverse refuses it too, with a generic error. For a custom lookup the navigation property is its SchemaName unless
    the relationship was given another one; a system lookup's is its logical name (`createdby.fullname`). A lookup that can
    point to several tables (Customer, Owner, regarding) and a second level (`xrmv_AccountId.primarycontactid.fullname`) are
    refused.
  - Text, multiline text, whole number, big integer, decimal, float, yes/no and date columns. A Currency column of its own row
    only through `Decimal(xrmv_price)`; a Currency column of a related row and any `_base` column are refused. Choice columns,
    a lookup read as a value, file and image columns are refused.
  - Other formula columns and calculated columns (a chain, see [Limits](#limits)), and rollup columns (the value last
    calculated by the rollup jobs). A rollup's `_date` and `_state` companions are refused.
  - Constants: numbers (decimal, within ±100,000,000,000), text in double quotes, `true`, `false`, `Blank()`, and the
    `TimeUnit` values `Milliseconds`, `Seconds`, `Minutes`, `Hours`, `Days`, `Months`, `Quarters`, `Years`.
- **Operators.** `+`, `-`, `*`, `/`, `^`, `%` (percent: `x%` is `x / 100`), `&` (text), `=`, `<>`, `<`, `<=`, `>`, `>=`,
  `&&`, `||`, `!`, `in` and `exactin`.
- **Functions** (Power Fx names; a name is case-sensitive, as in Power Fx):

  | Group | Functions |
  |---|---|
  | Logic | `If`, `Switch`, `And`, `Or`, `Not`, `IsBlank`, `Blank` |
  | Numbers | `Abs`, `Average`, `Exp`, `Int`, `Ln`, `Max`, `Min`, `Mod`, `Power`, `Round`, `RoundDown`, `RoundUp`, `Sqrt`, `Sum`, `Trunc` |
  | Conversion | `Decimal`, `Float`, `Value`, `Text` (`Text(number, format)` only), `Char` |
  | Text | `Concatenate`, `EndsWith`, `Left`, `Len`, `Lower`, `Mid`, `Replace`, `Right`, `StartsWith`, `Substitute`, `Trim`, `TrimEnds`, `Upper` |
  | Dates | `DateAdd`, `DateDiff`, `Day`, `Month`, `Year`, `Hour`, `Minute`, `Second`, `Weekday`, `WeekNum`, `ISOWeekNum` |
  | Current time | `Now`, `UTCNow`, `UTCToday`, `IsUTCToday` |

  A function with the wrong number or kind of arguments, and any other function, is refused when the column is defined.
- **Where the value shows.** In `Retrieve` and `RetrieveMultiple` (FetchXML, QueryExpression, OData), `$select`, conditions,
  orders, plug-in post-images and the [SQL endpoint](sql-endpoint.md), like any column. FetchXML, OData and the SQL endpoint filter and
  sort a formula column the same way.
- **`SourceTypeMask`.** Returned on every metadata read, computed from the formula as for calculated columns: 1 reads a column of
  its own row, 2 reads a column of a related row, 8 reads another formula or calculated column, 16 reads a rollup column; the
  bits are combined. A formula that reads no column (`Mod(-7, 3)`) has 1. A value a client sends for it is ignored. Confirmed
  on 2026-11-19: 1 for own columns and for constants, 2 for a related column. A formula that reads only the current time
  (`Now()`, `UTCNow()`) also has 1; that is extrapolated from the constants and not yet compared.
- **A calculated column can read a formula column**, and a formula column a calculated one. A rollup can aggregate (or filter
  on) a formula column only when it is basic: `SourceTypeMask` 1, and no `UTCNow` or `UTCToday` (Learn); xrmverse also refuses
  `Now` and `IsUTCToday` there, which Learn does not name, since they read the current time too.

## Defining a formula column

All paths end in the same validation, so a formula that one path accepts every path accepts.

| Path | How |
|---|---|
| Solution import | An `<attribute>` with `SourceType` 3 and a `FormulaDefinitionFileName` naming the table's formula file (see [Solution files](#solution-files)). The column is created after every simple column and relationship of the solution, in dependency order with the calculated and rollup columns |
| SDK | `CreateAttributeRequest` or `UpdateAttributeRequest` with `SourceType = 3` and the formula in `FormulaDefinition` |
| Web API | `POST` or `PUT` of an attribute under `EntityDefinitions(…)/Attributes` with `SourceType` 3 and `FormulaDefinition` |
| `xrmd schema apply` | The key `formula:` on a column (see below) |

`FormulaDefinition` holds the formula **without** a leading `=`: a definition that starts with `=` is refused over the SDK and
the Web API (400 `… has a formula that starts with '=': …`), as Dataverse's Web API refuses it (confirmed, 2026-11-19).
`RetrieveAttribute` and the Web API return the formula as stored, without `=` (confirmed). Only a solution's formula file
writes the `=` (see [Solution files](#solution-files)); the import and `xrmd schema` remove it before the column is defined.

**Invariant separators only.** Dataverse's Web API reads a formula in the language of the calling user: for an Italian user the
arguments are separated by `;` (`Round(xrmv_amount / 7; 2)`), and a `,` is refused; it then stores and exports the formula in
the invariant form, with `,` (confirmed, 2026-11-19). xrmverse reads the invariant form only, whatever the caller's language:
that is what Dataverse stores, returns and exports, so solution files, schema files and formulas read back from Dataverse go in
unchanged. A formula that holds `;` outside a string literal is a 400 with a hint (`… uses ';', which formula columns do not read:
use ',' between arguments and '.' as decimal mark (the invariant form Dataverse stores).`); one with a `,` decimal mark is a
400 too (`… has a formula that is not valid: …`). Neither is ever read another way. See [known issues](known-issues.md).

A formula column can be defined over the metadata API (SDK and Web API) as well as in the maker portal; Dataverse accepts it
too (confirmed: the Q1 columns were created through the Web API with `SourceType` 3 and `FormulaDefinition`).

A schema file:

```yaml
tables:
  - name: xrmv_calcorder
    columns:
      - { name: xrmv_fxlabel, type: string, maxLength: 100, formula: 'Upper(xrmv_code) & "-" & Text(xrmv_qty, "0")' }
      - name: xrmv_fxnodue
        type: bool
        formula: IsBlank(xrmv_duedate)
```

`formula:` is allowed on the types `string`, `int`, `decimal`, `bool` and `datetime`, cannot be empty, cannot be combined with
`calculated:`, `rollup:` or `autoNumberFormat`, and a column cannot switch between simple, calculated, rollup and formula. The
schema export writes it back (a one-line formula as a plain or quoted scalar, a multi-line one as a literal block, one holding a
carriage return or a byte order mark double-quoted); `=x` and `x` are the same formula: `xrmd schema` accepts a leading `=` in
the file and sends the formula without it. A Float formula column has no schema type and is left out of a schema export, like
any Float column. For `money` or `choice`, the schema file is refused by the CLI with its own text
(`formula applies only to the types string, int, decimal, bool and datetime, not to 'money'`), before the server's refusals
below are reached.

**Change rules.**

- `SourceType` cannot change (400). The formula can change; it is checked again, and so is every formula or calculated column
  that reads the column. A new formula whose result is not of the column's type is refused, since a column's type never changes
  (Learn). The check of a column that reads it includes the [size of its SQL](#limits): a change that would make a formula column
  that reads the column too complex is a 400 `The change of '…' would make the formula column '…' invalid: The formula column
  '…' is too complex to compute: …`. Only a change of a formula, a source type, choice options, lookup targets or a date
  behavior re-checks the columns that read it; were a reader ever to pass the bounds another way, a query reading it would give
  the same "too complex" 400 when it is read, not a SQL Server error.
- A formula column is never required and never valid for create or update; the server sets these. `MinValue` and `MaxValue`
  narrower than the type's widest range are refused (Learn: no minimum or maximum); the widest range is accepted.
- Deleting a column, lookup or table that a formula column reads is a 400 `0x80040203` naming the dependents as formula
  columns (and the calculated ones as calculated columns).
- A table's primary name column cannot be a formula column, and elastic tables refuse formula columns.

## Blank values and errors

Formula columns do not treat blanks as calculated columns do (Learn), and Power Fx tells a **blank** (no value) from an
**error** (an operation that failed). The rules xrmverse follows:

- **Numbers.** A blank number is 0 in arithmetic, in a numeric function and in `<`, `<=`, `>`, `>=`: `xrmv_amount * xrmv_qty`
  with no amount gives 0, where a calculated column gives null; `xrmv_amount / xrmv_qty` with no amount and a quantity of 1 gives
  0, and `Round(xrmv_amount / 7, 2)` with no amount gives 0 (confirmed, 2026-11-19).
- **`Max` and `Min` leave blank arguments out**: `Max(Blank(), -5)` is -5, and `Max(xrmv_amount, -5)` is -5 when the amount is
  blank, not 0 (confirmed, 2026-11-19). When every argument is blank the result is blank (Power Fx), which is 0 in a further
  operation; Dataverse not compared for that case. An error in an argument is still an error.
- **Text.** A blank text is `""` in `&`, `Concatenate`, a string function, `in` and `exactin`: `xrmv_code & "x"` with no code
  gives `"x"`, `Upper(xrmv_code) & "-" & Text(xrmv_qty, "0")` with neither gives `"-0"`, `Text(blank, "000")` gives `"000"`
  (confirmed).
- **`=` and `<>`** see the real blank: `Blank() = Blank()` is true, `xrmv_qty <> 5` is true when the quantity is blank, and
  `Blank() = 0` is false (confirmed: `If(Blank() = 0, 1, 0)` gives 0), so `xrmv_qty = 0` is false when it is blank.
- **`IsBlank`.** True for a blank value, and for a text also for `""` (confirmed for a blank date: `IsBlank(xrmv_duedate)`).
  A text holding a space is not blank.
- **Dates.** A blank date stays blank: `DateAdd` of a blank date is blank, and `<`, `<=`, `>`, `>=` with a blank date are false;
  `d = Blank()` is true and `d <> Blank()` false for a blank date.
- **`If` and `Switch`** without a matching branch and without a default give a blank, which is then 0 or `""` like any blank.
- **Errors.** An operation that fails gives an **error**, which the column shows as empty (null): a division by zero
  (`xrmv_amount / 0`, also `0 / 0`), `Mod(x, 0)`, `Sqrt` or `Ln` of a negative number, `Exp` or `Power` beyond a float, `Char`
  outside its range, `Left`, `Mid` and the others with a count or start out of range, a `DateAdd` beyond year 9999 or with a count
  outside a whole number, a decimal intermediate outside ±100,000,000,000, `Value`, `Decimal` or `Float` of text that is not a
  number. **An error is not a blank: it stays an error through every further operation**, so `xrmv_amount / 0 + 1` is empty on
  every row and `xrmv_amount / xrmv_qty` is empty when the quantity is 0 (confirmed, 2026-11-19), and so are `Mod(x, 0) + 1`,
  `Text(x / 0, "0") & "x"`, `Max(x / 0, 1)` and `Len(Left(t, -1))`. A comparison or `IsBlank` of an error is an error, and so is an
  `If` or `Switch` whose condition (or value) is one: `If(x / 0 > 1, 1, 0)` and `IsBlank(x / 0)` are empty, not 0 or false (Power
  Fx: "error in, error out", only `IsError`, `IsBlankOrError` and `IfError` take an error in; Dataverse not compared for these
  shapes). `And` and `Or` stop as Power Fx does: `And(false, x / 0 > 1)` is false and `Or(true, x / 0 > 1)` true, since the error
  is never reached. A value that can be a blank or an error (`If(c, a / b)`, `DateAdd` of a column) is 0 or `""` only when it is
  the blank.
- **A formula or calculated column read by another formula** is read like any column: its empty value is a blank (0 or `""`),
  also when it is empty because its own formula failed: `xrmv_fxdiv + 1`, where `xrmv_fxdiv` is `xrmv_amount / xrmv_qty`, gives 1
  when the quantity is 0 (confirmed, 2026-11-19). The error does not pass on to the reading column.

## Types and results

- **The result type must fit the column** (checked when the column is defined): Text for a Text column, a Decimal number for a
  Whole number or Decimal column, a Float for a Float column (only `Float()`, `Sqrt`, `Ln`, `Exp`, `Power` and `^` give one), a
  yes/no value for a Yes/No column, a date for a Date and time column. Number literals are decimals; `If` and `Switch` branches
  must give values of one type.
- **Results** are cast to the column as for calculated columns: a Text result is cut to the column's `MaxLength`; a Decimal is
  rounded to the column's precision; a **Whole number result is truncated toward zero** (`7 / 2` gives 3), and null when it does
  not fit in 32 bits. Whether Dataverse rounds instead is not yet compared.
- **No implicit conversions.** A number is turned into text only with `Text(number, format)`, and text into a number only with
  `Value`, `Decimal` or `Float`: `12 & "a"`, `Upper(xrmv_qty)` and a date in a string function are refused with a text that says
  so (Learn).
- **`Text(number, format)`.** (`Text(3, "000")` is `"003"`, confirmed.) The format is a constant made of `0` and `#` placeholders and literal characters (`"000"`,
  `"ORD-#"`, `"0-0"`), with at least one `0` or `#`; it cannot hold `.`, `,`, `%`, `;`, `e`, `E`, `[`, `]`, quotes or `\` (Learn: no locale-dependent tokens).
  The number is rounded to a whole number (half away from zero), a `0` pads with zeros, a `#` gives no digit for a zero value,
  and a negative value starts with `-` (`Text(-7, "000")` is `-007`). `Text(0, "0")` is `"0"` (confirmed: `ORD-002-0`).
- **`Value` and `Decimal` of text** read a plain decimal number (`" 12.5"` is 12.5); text that is not one, including `"1e3"`,
  `"1,000"` and `"50%"`, is an error (empty); a blank or `""` text gives blank. `Float` of text also reads exponents (`"1e3"` is 1000). Power Fx reads more forms; not yet
  compared.
- **Rounding.** `Round` rounds half away from zero (2.5 → 3, -2.5 → -3; `Round(1500 / 7, 2)` is 214.29, confirmed), `RoundUp`
  away from zero, `RoundDown` and `Trunc` toward zero, `Int` down (-2.5 → -3). `Mod` takes the sign of the divisor (`Mod(-7, 3)`
  is 2, confirmed).
- **Text functions.** `Len` counts trailing spaces. `Trim` removes leading and trailing spaces and collapses inner runs to one
  (`Len(Trim("  a  b "))` is 3, confirmed); `TrimEnds` removes only the outer ones. `Substitute` matches case-sensitively; its fourth argument (which match) must be a
  whole number constant from 1 to 100. `Mid`, `Left`, `Right` and `Replace` with a negative count, and `Mid` and `Replace` with
  a start below 1 (0 included), are errors (empty).
- **Text comparisons follow the database collation.** `=`, `<>`, `in`, `StartsWith` and `EndsWith` compare as the SQL Server
  database does: case-insensitively with the default collation, and `=` ignores trailing spaces. `exactin` and `Substitute` are
  case-sensitive. Power Fx's own `=` is case-sensitive; what Dataverse does is not yet compared
  (#20).

## Dates

- **Behaviors.** A formula's date has the behavior of the column it comes from. A User local date goes only with another User
  local date (in `DateDiff`, comparisons and `If`/`Switch` branches); Date only and Time zone independent dates go together (Learn).
  `Now()` is a User local date, `UTCNow()` a Time zone independent one and `UTCToday()` a Date only one. The result must fit the
  column: a User local result needs a User local column, a Date only result a Date only or Time zone independent column, a Time zone
  independent result a Time zone independent column.
- **Computed in UTC.** `DateAdd` and `DateDiff` are UTC arithmetic, also on a User local date: `DateAdd(xrmv_startedon, 3,
  TimeUnit.Days)` on 15 November 23:00 UTC gives 18 November 23:00 UTC (confirmed). `Now()` is the current UTC time, which is the
  same instant as Dataverse's user-local `Now()`.
- **`DateAdd`** takes a whole count: a fractional count is truncated (`DateAdd(d, 1.5)` adds one day), where Power Fx adds a
  fraction of the unit; not yet compared. A count outside the range of a whole number (±2,147,483,647, such as
  `DateAdd(d, 1e11, TimeUnit.Milliseconds)`) is an error (empty), where Power Fx gives a date.
  A unit below a day (`Hours`, `Minutes`, …) on a Date only date is refused.
- **`DateDiff`** counts the unit boundaries between the two dates (as calculated columns' `DIFFIN…` do); the default unit is
  `Days`. Not yet compared.
- **Date parts** (`Day`, `Month`, `Year`, `Hour`, `Minute`, `Second`, `Weekday`, `WeekNum`, `ISOWeekNum`) work on Date only
  dates. On a User local date they are refused (a part depends on the user's time zone); Dataverse refuses `Weekday` of a User
  local date as well, because it would need a time zone conversion (confirmed, 2026-11-19; the wording of the two refusals
  differs). On a Time zone independent date they
  are refused too: the Power Fx type check does not accept that kind of date there, and the refusal ("has a formula that is not
  valid: …") carries the engine's own reason, even though the refusal text for a User local date suggests a Time zone
  independent date. Whether Dataverse accepts date parts on these dates is not yet compared.
- **`Weekday`** gives 1 for Sunday to 7 for Saturday; `WeekNum` counts weeks starting on Sunday, week 1 holding 1 January;
  `ISOWeekNum` is the ISO week. The `StartOfWeek` argument is refused.
- **`IsUTCToday(date)`** is true when the date falls on today's UTC date, on any behavior.
- `+` and `-` on dates are refused: use `DateAdd` and `DateDiff`.

## Limits

| Limit | Value |
|---|---|
| Formula length | 1,000 characters |
| Chain depth: a formula column that reads a formula or calculated column that reads … | 10 columns, counting the column itself and the calculated columns in the chain (a chain of calculated columns alone keeps its limit of 5). Learn's example counts a formula reading another formula as depth 1, so Dataverse may allow one level more; not yet compared |
| Cycles | Refused when the formula is saved, naming the path |
| Related rows | One level, through a lookup to one table, named by its navigation property |
| Decimal values | ±100,000,000,000 for constants (refused) and intermediate results (an error: empty) |
| Computed columns one query reads | 50 calculated and formula columns together; the next one is a 400 whose text says "calculated columns" |
| `Substitute` instance | A constant from 1 to 100 |
| SQL of one formula column | 300,000 characters, 250 intermediate values (`OUTER APPLY`s), `CASE` nested at most 10 deep in one expression, counting the formula and calculated columns it chains (an xrmverse limit of the translation to SQL: SQL Server's compile time grows faster than the SQL, and these bounds keep one column within about a second on SQL Server 2022; ordinary formulas stay far below them) |
| SQL of the calculated and formula columns one query reads | Twice the bounds of one column, for all of them together: 600,000 characters and 500 intermediate values. Two columns near the bounds can be read together (SQL Server 2022 compiles them in about 5 seconds, within the 30-second command timeout); a query reading three or more columns near the bounds is a 400 (read fewer formula columns in one query). Ordinary formula columns are far below the bounds, so a query can read many of them |

**`Max` and `Min` need SQL Server 2022** or later (they use `GREATEST` and `LEAST`). The bundled `sqlserver` service runs SQL
Server 2022; on an older server a query that reads such a formula column fails.

## Writing

- A value sent for a formula column in `Create`, `Update`, `Upsert` or a Web API `PATCH` is ignored, as for any column that
  cannot be written; it is not in the plug-in target and not stored (whether Dataverse ignores or refuses it is not yet compared).
  A post-image holds the computed value.
- The column is read-only in forms and in the metadata (`IsValidForCreate` and `IsValidForUpdate` are false).
- A [duplicate detection](duplicate-detection.md) rule cannot use a formula column (400), and an autonumber format cannot be set
  on one. This is a difference: Learn says duplicate detection rules are not triggered on formula columns, not that such a rule
  is refused.
- [Auditing](auditing.md): a formula column has no audit entries; a change to its sources is audited on the sources.

## Security

A formula column **cannot be secured** with [field security](field-security.md), as in Dataverse (Learn lists formula columns
among the columns that cannot be secured): `CanBeSecuredForCreate`, `CanBeSecuredForRead` and `CanBeSecuredForUpdate` are false,
and `IsSecured` on one is refused (400 `0x8004F501`), in `CreateAttribute`, `UpdateAttribute` and solution import (where the
file's `IsSecured` is not applied, with a warning).

The formula reads its sources, and the related row it follows, **with system rights**: row security and the sources' field
security do not hide anything from the calculation. So a formula column can show a user a value computed from a column they
cannot read. Whether Dataverse shows that value is not yet compared (#20); to keep
a secured value hidden, do not read it in a formula column.

## Sorting

Learn says that sorting is not available for some formula columns in Dataverse. xrmverse sorts every formula column, in `order` of
FetchXML and QueryExpression, in `$orderby` and on the SQL endpoint, as it does for calculated columns; this is a documented
difference in [xrmverse compared with Dataverse](dataverse-comparison.md).

## Solution files

A solution carries a formula column as `<SourceType>3</SourceType>` and a `<FormulaDefinitionFileName>` in `customizations.xml`.
The formulas of a table share one file, `/Formulas/<table>-FormulaDefinitions.yaml`, which every formula column of the table names
(confirmed). It holds one line per column, `<logical name>: =<formula>`, with CRLF line ends and no byte order mark (confirmed).

- **Import** reads the column's line from the file named by its `FormulaDefinitionFileName` and stores the formula without the
  `=` (the form the API takes and `RetrieveAttribute` returns). A value may also be a quoted or block YAML scalar. A formula column without a file name, a missing file, a file that is not
  a YAML mapping of column names to text, a column named twice (column names ignore case) and a column missing from the file are
  import errors that name the column or the file. Formula columns are created in the same final pass as calculated and rollup
  columns, after the simple columns and relationships of the import, each after the columns it reads, whatever the order in the
  files.
- **Export** writes one file per table with the table's formula columns in attribute order, each `<column>: =<formula>`, and every
  column's `FormulaDefinitionFileName` pointing to it; the parity export's file comes back byte for byte (confirmed). A formula
  that a plain YAML value cannot hold (several lines, `": "`, `" #"`, surrounding spaces, control characters) is written
  double-quoted with escapes; how Dataverse writes such a formula is not yet compared.
- **Layers.** Each managed layer keeps only the line of its own column, so uninstalling an upper solution restores each column's
  own formula from the layer below.
- **Uninstall and delete.** Deleting a solution removes its formula and calculated columns first (a column that reads another
  before the one it reads), then the relationships, columns and tables they read.

## Refused

| What | Answer |
|---|---|
| A formula column on a server without the translator | 400 `Formula columns are not available on this server.` |
| A Currency formula column | 400 `A formula column cannot be of type Money: …` |
| A Choice formula column | 400 `Choice formula columns are not supported yet: …` |
| Another type (Memo, lookup, …) | 400 `A formula column cannot be of type <type>: …` |
| A prompt column (`SourceType` 4) | 400 `Prompt columns are not supported: …` |
| `SourceType` changed on update | 400 `The SourceType of a column cannot change: …` |
| No formula, or an empty one | 400 `The formula column '…' has no FormulaDefinition.` |
| A formula that starts with `=` (SDK, Web API) | 400 `The formula column '…' has a formula that starts with '=': give the formula without it (only a solution's formula file writes it with '=').` |
| A related column named by the lookup's logical or display name | 400 `The formula column '…' reads '<lookup>.<column>' through the lookup's logical name or display name: a formula reads a related record through the lookup's navigation property name; use '<navigation>.<column>'.` |
| A formula over 1,000 characters | 400 `The formula column '…' has a formula of n characters: a formula column takes 1,000 at most.` |
| A formula that does not parse or type-check | 400 `The formula column '…' has a formula that is not valid: <reason>.` |
| A function outside the list, including `Error`, `IfError` and `IsError` | 400 `The formula column '…' uses the function '<name>', which formula columns do not support.` |
| The `StartOfWeek` argument of `Weekday` or `WeekNum` | 400 `The formula column '…' uses the StartOfWeek argument of …, which formula columns do not support.` |
| A result of another type than the column | 400 `The formula of '…' gives <type>, but the column is <type>.` |
| A date result whose behavior does not fit the column | 400 `The formula of '…' gives a <behavior> date, but the column is <behavior>.` |
| A String column with a format other than Text, an Integer column with a format | 400 `A formula column of type String takes the format Text only: …`, `… of type Integer takes the format None only: …` |
| A Decimal precision outside 0 to 10 | 400 `The precision of the formula column '…' is n: a formula column takes 0 to 10.` |
| `MinValue` or `MaxValue` narrower than the type's widest range | 400 `MinValue and MaxValue cannot be set on a formula column: …` |
| A formula primary name column | 400 `The primary name column of a table cannot be a formula column: …` |
| A cycle, a chain too deep | 400 `… is part of a cycle of formula or calculated columns: a -> b -> a.`, `… chains more than 10 formula or calculated columns: …` |
| A formula whose SQL would be too large (checked when the column is defined, and when a column it reads changes; a query reading it gives the same 400) | 400 `The formula column '…' is too complex to compute: its SQL would exceed 300,000 characters; simplify the formula.` (or `… would need more than 250 intermediate values; …`, `… would nest conditions more than 10 deep; …`). A value a formula reads several times, a nested `If` or `Switch`, the instance of a `Substitute` and a chain of formula columns are each computed once, so ordinary formulas stay far below the bounds; each `+` or `-` of decimals is an intermediate value (its range), so a chain of long sums reaches 250 first. |
| A query whose calculated and formula columns together pass twice the bounds of one column | 400 `The query is too complex to compute: the calculated and formula columns it reads would together need more than 500 intermediate values; read fewer formula columns in one query.` (or `… more than 600,000 characters of SQL; …`) |
| `IsSecured` on a formula column | 400 `0x8004F501` `The column '…' cannot be secured with field security.` |
| Anything else the rules above refuse | 400 `The formula column '…' <reason>.` naming the rule: a number or date passed to a string function, a number with `&`, `in` or `exactin`, text in arithmetic, `Text` without a constant format or with an unsupported one, a Currency column not read through `Decimal`, a related Currency column, a `_base` column, a rollup companion, a column of a type formula columns cannot read, a lookup read as a value, a polymorphic lookup or a second level, mixed date behaviors, `;` argument separators or a `,` decimal mark, a date part of a User local date, `DateAdd` with a time unit on a Date only date, `+` or `-` on dates, `If` branches of different types, string interpolation, records, tables, `;` chains, `As`, `Parent`, `Self`, a non-constant `Substitute` instance |
| Deleting a column a formula column reads | 400 `0x80040203` `The component cannot be deleted because it is referenced by formula column '…'.` (`… by calculated column '…' and formula column '…'.` when both kinds read it) |
| A rollup aggregating or filtering on a formula column that is not basic | 400 `0x8006053c` and the filter codes of [Rollup columns](rollup-columns.md#refused) |
| A duplicate detection rule on one | 400 `Duplicate detection rules cannot use the formula column '…'.` |
| An autonumber format on one | 400 `…: an AutoNumberFormat cannot be set on a formula column.` |
| One on an elastic table | 400 `… cannot be a calculated, rollup or formula column: elastic tables do not support it.` |
| A query reading more than 50 calculated and formula columns | 400 `A query cannot read more than 50 calculated columns: '…' is one more.` |

The texts are xrmverse's own and name the column; refusals without a Dataverse error code use `0x80040203`.

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- **Choice formula columns** are not supported: a formula that returns a choice value needs binding rules that no export shows.
- **Error functions** (`Error`, `IfError`, `IsError`) are refused. An error propagates as Power Fx does and shows as an empty
  value, but a formula cannot test for it or replace it.
- **Formulas in the caller's language.** Dataverse's Web API reads a formula with the calling user's separators (`;` for an
  Italian user); xrmverse takes the invariant form only (see [Defining a formula column](#defining-a-formula-column)).
- **A renamed navigation property.** When the entity metadata does not carry the relationship, a custom lookup's navigation
  property is taken to be its SchemaName (the default Dataverse gives it); a relationship created with another
  `ReferencingEntityNavigationPropertyName` is read by that name only where the relationship is at hand.
- **Migration and solution import.** [`xrmd migrate`](migration.md) carries a formula column only with the exported solution
  that holds it; one outside the exported solutions is listed in `schema/unsupported.json` ("import it with its solution"). A
  solution that holds a formula xrmverse refuses (a Choice formula column, `IfError`, a date part of a Time zone independent date,
  …) fails to import, and so does a migration run that imports it.
- **`Trim`** also removes any U+0001 control character from the text (it uses the character as an internal marker).
- **Not yet compared with Dataverse** (issue #20): errors in
  conditions, `IsBlank`, `And`/`Or`, the guarded text functions and an error in `Max`/`Min` (Q1 compared arithmetic, a chained
  formula column and blank `Max` arguments), `Max`/`Min` of blanks only, `SourceTypeMask` of a formula reading only the current
  time, the Whole number truncation, `Text` formats other than padding, the date parts other
  than `Weekday` on a User local date, `Value` parsing, `DateAdd` with a fraction, text comparisons and case, a write to a
  formula column, the delete refusal and its text, the chain depth, the sorting cases, the error codes, and the export of a
  formula that a plain YAML value cannot hold.

## See also

- [Calculated columns](calculated-columns.md)
- [Rollup columns](rollup-columns.md)
- [Supported queries](queries.md#calculated-and-formula-columns)
- [Solutions](solutions.md)
- [Field-level security](field-security.md)
- [Known issues and limitations](known-issues.md)
