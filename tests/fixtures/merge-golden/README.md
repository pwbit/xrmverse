# Merge golden dumps (S.2e3c3a)

Each `{scenario}.txt` is what a merged message did for one request in `tests/Xrmd.Integration.Tests/MergeGoldenTests.cs`: the
standard-table `Upsert` (`StandardUpsert`: an inner Create or Update) and `CreateMultiple`, `UpdateMultiple` and `UpsertMultiple`
(`BulkStage` and `BulkTargets`: one inner Create, Update or Upsert per target), whose stage 30 is `MainOperationAsync` in
`src/Xrmd.Pipeline/Sdk/PipelineExecutor.Sdk.cs`. They were captured on base commit `3c88e341` (S.2e3c2b), before that stage moves onto
the model request (plan S.2e3c3a, Task 1; Review Focus 1–5; pre-flight rulings F2, F8 and F9). Later tasks keep these files
byte-identical; a missing file is written once and the test fails, an existing file is never rewritten.

## Scenarios
The table is `xrmv_order` (`TestEntities.EnsureOrderAsync`; rows named `mrg ...`, deleted before each scenario and on dispose) unless
a scenario says otherwise. A target is `xrmv_name`, `xrmv_status` (an `OptionSetValue`) and `xrmv_amount`.
- Upsert: `UpsertCreate`; `UpsertUpdate` (the caller's Target holds the primary id column `xrmv_orderid`, the inner Update's does not);
  `UpsertCalculatedAndRollup` (on `account`, on a database of its own: a calculated column and the rollup `xrmv_ordertotal` with its
  `_state` companion are dropped from the inner Create's Target, seen at stage 20, and kept in the caller's); `UpsertKeyAttributes` (the
  inner core refuses them); `UpsertWithoutTarget` (400); `UpsertSuppressDuplicateDetection` (the inner request carries it);
  `UpsertInnerStepThrows`; `UpsertInnerStepChangesOptionInPlace` (Review Focus 1: an inner PreOperation step sets `Value` of the
  caller's `OptionSetValue` in place); `UpsertCreateOnlyExisting` (412) and `UpsertCreateOnlyWithoutCreatePrivilege` (403 for an
  existing row), both through `OrganizationService.UpsertAsync(target, createOnly: true)` (Review Focus 4);
  `UpsertAsyncStepOnInnerUpdate` (Review Focus 5).
- CreateMultiple: `CreateMultipleTwoRows` (a recorder on the inner Create and one on CreateMultiple, both at 40);
  `CreateMultipleSharedOptionChangedInPlace` (Review Focus 1: three rows hold one `OptionSetValue`, the inner PreOperation step of the
  first row changes it in place, the recorder of each inner Create reads `ParentContext`); `CreateMultipleAsyncSteps` (an asynchronous
  step on the inner Create and one on CreateMultiple); `CreateMultipleEarlyBoundAsyncStep` (on `pb_pipe`: early-bound
  `Xrmd.TestPlugins.EarlyBound.Pipe` targets, the SDK fallback of the model, with an asynchronous inner step).
- UpdateMultiple: `UpdateMultipleRepeatedId` (the repeated id is skipped); `UpdateMultipleImages` (the image collections: pre images at
  20, pre and post images at 40); `UpdateMultipleImagesTargetIdChanged` (ruling F2: a PreOperation step on UpdateMultiple sets the second
  target's id to a third row; the pre image collection holds the second row, the post image collection the third);
  `UpdateMultipleStepEmptiesTargets`, `UpdateMultipleStepAddsOtherTable`, `UpdateMultipleStepRepeatsId` (Review Focus 2) and
  `UpdateMultipleStepRemovesSecond` (ruling F2), each a PreOperation step on UpdateMultiple that changes `Targets`.
- UpsertMultiple: `UpsertMultipleUpdateAndCreate` (the `UpsertResponse` array); `UpsertMultipleSecondTargetFails` (Review Focus 3: the
  second target's inner Create fails in its PreOperation step, nothing is stored).
- DeleteMultiple: `DeleteMultipleStandardTable`, through `OrganizationService.ExecuteAsync` (400, Dataverse's text).

The steps are plug-ins of `Xrmd.TestPlugins` registered as the administrator: `MergeRecorderPlugin` (the recorder),
`InPlaceOptionPlugin`, `ThrowOnValuePlugin`, `TargetsEditPlugin` (added for these goldens, ruling F8), `ThrowPlugin` and, for the
asynchronous steps, `SetAttributePlugin` (`description=async`, which changes nothing stored).

## Caller
A scenario arranges its rows and steps as the administrator, then runs its request once. The first line names the way and the caller:
`PipelineExecutor.ExecuteAsync` as the System Administrator (`{user}`) at depth 1 with no session (the pipeline opens one), as
`CoreScenarios.ViaPipelineAsync` does; or `OrganizationService.UpsertAsync(target, createOnly: true)` /
`OrganizationService.ExecuteAsync` as the System Administrator or as a user with only the privileges it names (`{a:caller}`). For the
`OrganizationService` ways the request written is an `UpsertRequest` holding the Target passed (the service builds its own request
around it) or the request passed.

## File format
- `--- request ---`: the request as it was sent, with the `CoreDump` writers of S.2e3a (`tests/fixtures/core-response-golden/README.md`).
- `--- fault ---`: the fault (exception type, error code and HTTP status of an `XrmdFaultException`, message), or `none`.
- `--- request after ---`: the same dump of the request as the caller holds it after the call, then one line
  `same instance {slot}: True/False` per slot of the request that held an instance the scenario shares (a Target, a row of `Targets`,
  an `OptionSetValue`): whether the slot still holds that instance.
- `--- response ---`: the response (type, `ResponseName`, results) with the `CoreDump` writers, or `none` after a fault.
- `--- rows ---`: the rows of the table written, one line each as `{label}: column=value, ...`, or `{label}: none`.
- `--- stage 40 ---`: what each recorder step saw, in the order the steps ran (most at 40, some at 20, as the scenario registers them),
  or `none` (a fault rolls the recorder's rows back with the rest): `step Message|Stage|Mode|Depth`, then its `Target` or `Targets`
  (each entity as `Type(table) id=... {column=value, ...}` in its own column order, key attributes after), the other input parameters, the
  single images, the counts and images of the image collections, and `parent: Message|Stage` with the parent's Target(s), or
  `parent: none`.
- `--- async ---`: the `asyncoperation.data` of each job the call queued, one line each (`job: ...`), or `none`.

## Normalisation
The whole file goes through `GuardKit.Normalize` (`CoreScenarioKit.Normalize`, formatted dates before 2024 kept): the ids the scenario
names are written by role (`{a:order}`, `{a:a}`, `{a:b}`, `{a:c}`, `{a:account}`, `{a:caller}`; `{user}`, `{businessunit}`,
`{organization}`), every other id `{idN}` in first-seen order over the whole file, the empty id `{empty}`, a custom table's object type
code in a fault `OTC={otc}`. In a job's payload, dates are `{date}` and row versions `{rowversion}` first; the jobs are ordered by their
text with every unnamed id masked (`CoreScenarioKit.Mask`), then deleted.

## Base conversion counts
`MergeGoldenTests.Base_conversion_counts` prints the `SdkMirror.SyncCount` of three probes on the base commit (not asserted; each run
twice on a fresh mirror, same count both times). The probes are the `internal static` methods of `MergeGoldenTests` (ruling F1), each run
through the executor as the administrator at depth 1 with its own mirror, with one inner step (`SetAttributePlugin`, `telephone1=x`) on
PreOperation Create of `account`:

| Probe | Method | `SyncCount` |
|---|---|---|
| Upsert that creates (a new `account`, its id given) | `UpsertCreatesProbeAsync` | 18 |
| CreateMultiple of 10 `account` rows | `CreateMultipleOfTenProbeAsync` | 156 |
| UpsertMultiple of 2 `account` rows (one existing, one new with its id given) | `UpsertMultipleOfTwoProbeAsync` | 39 |
