# Classic workflow XAML fixtures

## `real/`: the parity export (byte for byte)

The five XAML files of `tests/fixtures/solutions/RealExports/xrmdWfTest_1_0_0_2.zip`, extracted unchanged (CRLF line ends kept,
`real/.gitattributes`). Exported 2026-11-12 from a licensed Dataverse environment by Power Builders: its own publisher `xrmv`,
invented data (legal guardrails §8). Table `xrmv_order` (`xrmv_amount` decimal, `xrmv_status` choice, `xrmv_accountid` lookup to
account).

| File | Category | What it covers |
| --- | --- | --- |
| `Child-0FE1B782-….xaml` | 0, real-time, subprocess | stop workflow (`TerminateWorkflow`, canceled, message in `Reason`) |
| `OnCreate-3E1F0307-….xaml` | 0, real-time | create a task (literal, primary key, option set), start the child workflow (`InputParameters` an empty dictionary) |
| `OnUpdate-BC9A625B-….xaml` | 0, background | wait (`ConditionSequence` Wait=True, one `ConditionBranch`), then send an email to the account's primary contact; lookup literal; `Persist` |
| `Newbusinessrule-216FA344-….xaml` | 2 | condition (`Null`) with an otherwise (`[True]` branch); `mcwc:SetAttributeValue`; `mcwc:SetMessage` with one `mcwo:StepLabel` |
| `Recalculate-9DF696A1-….xaml` | 3, bound to `xrmv_order` | `x:Members` arguments with `mxsw:Argument*Attribute`s, `CustomOperationArguments`, `Add`, update, output assignment (`DirectCast`), condition `LessThan`, stop |

Every catalog entry a parse of them relies on is an `export` entry of `src/Xrmd.Workflows/Xaml/ActivityCatalog.cs`, and every
`export` entry is relied on by them (`RealExportXamlTests.The_catalog_entries_the_export_uses_are_the_export_entries`, and
`WorkflowParityTests.Parity_catalog_has_no_assumed_entry_used_by_export` on the zip itself). `RealExportXamlTests` asserts their step
trees and `RealShapeVariantsTests` that variants of them the export did not show stay unsupported.

## The other files: provisional (hand-written)

These files are **hand-written by xrmverse** (W25). Plan 2.14, Task 9 checked each against the parity export: none of the shapes
listed below appears in it (where a file overlapped the export, Task 3b already moved its tests to the real file), so they stay
provisional and on the checklist. They stay only for the shapes the parity export does not contain, each listed below; the parser tests that use them say which provisional shape they cover. Shapes marked `assumed` in the catalog are our
expectation of what the classic designer writes, and are still to be compared with a real Dataverse (expressions and operators, elements).

| File | Category | Provisional shapes it still covers |
| --- | --- | --- |
| `realtime-create-update.xaml` | 0 | the `StepComposite` step wrapper, `UpdatedEntities`, Integer literals |
| `background-wait.xaml` | 0 | else-if (nested condition in `Else`), an `Else` block, And/NotNull/Equal, set state, assign, wait with a timeout branch, plain `Postpone` |
| `child-and-stop.xaml` | 0 | `New Guid(...)` workflow ids, child workflow on a related record after `RetrieveEntity`, stop message in the exception (the export's stops, canceled, carry an empty message in `Reason`) |
| `business-rule-entity.xaml` | 2 | `ActivityReference` business rule activities (set default value, show error, client-only), `SetEntityProperty` on the primary record, an error shown when a value is negative (the export's rule shows its error whenever the amount is not empty) |
| `action-echo.xaml` | 3 | the "perform action" step (`ExecuteAction`), output assigned straight from an argument, a create step reading an input argument |
| `action-call.xaml` | 0 | a "perform action" step on the primary record (`Target`) with a Decimal input, and a later step reading its output `[ActionStep1_result("Total")]` (`Vb:ActionOutput`) |
| `custom-activity.xaml` | 0 | a custom workflow activity with inputs and an output; other namespace prefixes |
| `unsupported-expression.xaml` | 0 | an unknown VB expression, an unknown `ActivityReference` type and an unknown element |

Names, ids and texts of the provisional files are invented. No customer or third-party content belongs here.

## `yaml/`: the YAML twins (plan 2.21)

`yaml/*.yaml` are the workflow-as-code form of the five `real/` files (`oncreate`, `onupdate`, `child`, `business-rule`,
`recalculate`), in the canonical text `WorkflowYaml.Write` produces. They are the fixtures of the YAML format tests and, later, the
expected output of the decompiler for the real exports. See `yaml/README.md`.
