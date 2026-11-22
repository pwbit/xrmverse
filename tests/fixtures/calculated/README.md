# Calculated column XAML fixtures

## `real/`: the parity export (confirmed)

`real/` holds the 27 calculated column definitions of the 2.12a parity export
(`tests/fixtures/solutions/RealExports/xrmCalcTest_1_0_0_4.zip`), extracted byte for byte from its `Formulas` folder
(`.gitattributes` keeps their CRLF line ends), and the values Dataverse showed for invented records after the export:

- `xrmv_calcorder-<column>.xaml`: one file per calculated column of `xrmv_calcorder`, named as the export names them.
  `tests/Xrmd.Workflows.Tests/RealCalculatedXamlTests.cs` translates every one.
- `accounts.csv`, `xrmv_calcorders.csv`: three accounts and four orders as Dataverse returned them on 2026-11-16, read at about
  15:45 UTC (the columns that read the current time hold at that moment only). Every date is UTC. The columns that carry ids of
  the source environment (record ids, the owning business unit, the currency, version numbers) are left out; a lookup is written
  as the name of the account it points to (`account`, `parentaccount`). The rollup (`account`) and Power Fx formula (`xrmv_fx…` except
  `xrmv_fxtotal`, which is calculated) columns are kept as recorded, for 2.12b and 2.12c. The data is invented.
  `CalculatedParityTests.Parity_values_match_recorded_dataverse_values` (Xrmd.Integration.Tests) creates the records again and
  compares every calculated column.

What the export confirmed about the XAML: the function operators are named in Pascal case (`AddDays`, `DiffInHours`, `Concat`,
`TrimLeft`, ...), the current time is the operator `Now` with an empty `Parameters` array, every definition is wrapped in a
`ConditionSequence` with a single `[True]` branch, a `TargetType` that is not the value's own type is the column's type, the Yes/No
literals are `1` and `0`, and the files have no byte order mark and CRLF line ends.

## The hand-written files (provisional)

The files at the top of this folder are **hand-written by xrmverse** (plan 2.12a, ruling K2), written before the parity export.
Names are invented (`xrmv_` publisher prefix, legal guardrails §8). Their operator names were renamed to the ones the export
uses (`Concat`, `AddDays`, `AddWeeks`, `AddMonths`, `DiffInDays`, `TrimLeft`, `TrimRight`, `Now`); they stay because their
table (`xrmv_quantity`, `xrmv_orderdate`, `xrmv_rush`) and shapes (a condition with several branches, nested functions, `Or`
around `And`) serve the query, write, schema and refusal tests. The tests they shared with the export (a Currency product,
`CONCAT` with a parent column, `DIFFINDAYS` with the current time, a leading byte order mark) now read the files of `real/`.

They use the shapes of the 2.14 parity export (`tests/fixtures/workflows/real`): a designer `Sequence` per set-value step with the
business rule's bookkeeping (`CreatedEntities("primaryEntity#Temp")`, `GetEntityProperty`, `SetEntityProperty`,
`mcwc:SetAttributeValue`), `ConditionSequence`/`ConditionBranch` with a last `[True]` branch as the otherwise, `EvaluateCondition`,
and `EvaluateExpression` with `CreateCrmType`. Their arguments follow the function's signature (`AddDays(number, date)`,
`DiffInDays(start, end)`, `TrimLeft(text, count)`), as the export does.

The table `xrmv_calcorder` they read: `xrmv_name` (text), `xrmv_amount` (currency), `xrmv_quantity` (whole number),
`xrmv_orderdate` (date and time), `xrmv_rush` (yes/no), `xrmv_status` (choice), `xrmv_accountid` (lookup to `account`), and the
calculated columns below.

| File | Calculated column | Definition |
| --- | --- | --- |
| `arithmetic-money.xaml` | `xrmv_total` (currency) | `xrmv_amount * xrmv_quantity` |
| `concat-related.xaml` | `xrmv_label` (text) | `CONCAT(xrmv_name, " / ", xrmv_accountid.name)`: a column of the parent record |
| `conditional-dates.xaml` | `xrmv_followup` (date and time) | amount > 1000: `ADDDAYS(2, xrmv_orderdate)`; amount > 100: `ADDWEEKS(1, …)`; otherwise `ADDMONTHS(1, …)` |
| `diff-now.xaml` | `xrmv_age` (whole number) | `DIFFINDAYS(xrmv_orderdate, now)` (`Now`) |
| `boolean-and-or.xaml` | `xrmv_isbig` (yes/no) | `(amount > 1000 AND quantity >= 10) OR rush = yes`: yes, otherwise no |
| `picklist-branches.xaml` | `xrmv_tier` (choice) | status = 100000001: 100000002, otherwise 100000000 |
| `trim.xaml` | `xrmv_code` (text) | `TRIMLEFT(TRIMRIGHT(xrmv_name, 2), 3)` |
| `chained.xaml` | `xrmv_double` (decimal) | `xrmv_total / 2`: another calculated column |

`tests/Xrmd.Workflows.Tests/CalculatedTranslatorTests.cs` translates each one, and derives variants of them (renamed columns,
another operator) for the refusals.
