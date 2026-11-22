# Rollup column XAML fixtures

## `real/`: the parity export (confirmed)

`real/` holds the four rollup column definitions of the parity export
`tests/fixtures/solutions/RealExports/xrmCalcTest_1_0_0_4.zip` (plan 2.12b), extracted byte for byte from its `Formulas` folder
(`.gitattributes` keeps their CRLF line ends; no byte order mark), named as the export names them. The data of that environment
is invented.

| File | Rollup column (`account`) | Definition |
| --- | --- | --- |
| `account-xrmv_ordertotal.xaml` | `xrmv_ordertotal` (decimal) | `Sum` of `xrmv_calcorder.xrmv_amount` where `xrmv_status` `Equal` 2, over `xrmv_calcorder_AccountId_account` |
| `account-xrmv_ordercount.xaml` | `xrmv_ordercount` (whole number) | `Count` of `xrmv_calcorder.xrmv_calcorderid` over `xrmv_calcorder_AccountId_account` |
| `account-xrmv_lastorder.xaml` | `xrmv_lastorder` (date and time) | `Maximum` of `xrmv_calcorder.xrmv_startedon` over `xrmv_calcorder_AccountId_account` |
| `account-xrmv_treebudget.xaml` | `xrmv_treebudget` (currency) | `Sum` of `account.xrmv_budget` over the hierarchy `account_parent_account` (empty `Target`) |

What the export confirmed about the XAML (plan 2.12b, ruling R3): a root `Sequence` `RollupRuleStep1` with the three designer
sequences `Source`, `Target` and `Aggregate`; the variable `HierarchicalRelationshipName` of `Source` (`[Nothing]` for none); the
related rows named by an `mcwc:SetAttributeValue` whose `DisplayName` is `<related>.<lookup>.<relationship>` and whose `Entity` is
`CreatedEntities("relatedlinked_<relationship>#<lookup>#<related>#Temp")`; a filter on the related rows as `GetEntityProperty`,
`CreateCrmType` and `EvaluateCondition` bound to a Boolean variable; and the aggregate as one `GetEntityProperty` and one
`EvaluateExpression` whose operator is `Sum`, `Count` or `Maximum`.

The export `xrmCalcTest_1_0_0_5.zip` (plan 2.12b, Task 7) added four more, also byte for byte:

| File | Rollup column (`account`) | Definition |
| --- | --- | --- |
| `account-xrmv_avgamount.xaml` | `xrmv_avgamount` (decimal) | `Average` of `xrmv_calcorder.xrmv_amount` over `xrmv_calcorder_AccountId_account` |
| `account-xrmv_firstorder.xaml` | `xrmv_firstorder` (date and time) | `Minimum` of `xrmv_calcorder.xrmv_startedon` over `xrmv_calcorder_AccountId_account` (no filter) |
| `account-xrmv_wonprice.xaml` | `xrmv_wonprice` (currency) | `Sum` of `xrmv_calcorder.xrmv_price` over `xrmv_calcorder_AccountId_account` (no filter) |
| `account-xrmv_treeorders.xaml` | `xrmv_treeorders` (whole number) | `Count` of `xrmv_calcorder.xrmv_calcorderid` over `xrmv_calcorder_AccountId_account` (no hierarchy, no filter) |

They confirm the operators `Minimum` and `Average` and a related Currency `Sum`.

### `designer/`: filters from the classic rollup designer

On a licensed test environment the filters of two rollups were set in the classic rollup designer, and their `FormulaDefinition`
was read back from the metadata, byte for byte:

| File | Rollup column (`account`) | Definition |
| --- | --- | --- |
| `designer/account-xrmv_firstorder.xaml` | `xrmv_firstorder` (date and time) | `Minimum` of `xrmv_startedon` over related orders where `xrmv_status` = Open **and** `xrmv_amount` > 100 (`EvaluateLogicalCondition` `And`) |
| `designer/account-xrmv_treeorders.xaml` | `xrmv_treeorders` (whole number) | `Count` of related orders over the hierarchy, with a source filter `xrmv_budget` NotNull (condition in the `Source` sequence) |

They confirm two conditions in one section and a source filter with a related table and a hierarchy. The values of that run are
in the integration test `Parity_values_match_the_licensed_environment_run`.

`tests/Xrmd.Workflows.Tests/RollupTranslatorTests.cs` translates each one.

## The hand-written files (provisional, `assumed`)

The files at the top of this folder are **hand-written by xrmverse** (plan 2.12b, ruling R1, after 2.12a's K2) for the shapes the
export does not show. They use only the shapes above (R3); what they add is `assumed` until the recalculated export of the plan's
question Q1 confirms or corrects it. Names are invented (`xrmv_` publisher prefix, legal guardrails §8); the tables are those of the
export (`account`, `xrmv_calcorder`).

| File | What it adds (assumed) | Definition |
| --- | --- | --- |
| `source-filter.xaml` | a filter on the rollup's own rows (in `Source`), a related table together with the hierarchy, a unary condition | `Count` of `xrmv_calcorder.xrmv_calcorderid` over `xrmv_calcorder_AccountId_account` and `account_parent_account`, for accounts whose `xrmv_budget` `NotNull` |
| `two-conditions.xaml` | two conditions in one section, combined with AND; a Decimal literal | `Sum` of `xrmv_calcorder.xrmv_amount` where `xrmv_status` `Equal` 1 and `xrmv_amount` `GreaterThan` 100 |
| `minimum.xaml` | the operator `Minimum` | `Minimum` of `xrmv_calcorder.xrmv_startedon` |
| `average.xaml` | the operator `Average` | `Average` of `xrmv_calcorder.xrmv_amount` |
| `currency-sum.xaml` | a related Currency `Sum` | `Sum` of `xrmv_calcorder.xrmv_price` where `xrmv_status` `Equal` 2 |
