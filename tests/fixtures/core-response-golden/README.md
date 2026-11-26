# Core response golden dumps (S.2e3a)

Each `{message}.txt` is what the core answered for one message in
`tests/Xrmd.Integration.Tests/CoreResponseGoldenTests.cs`. There is one scenario per message of `CoreOperations.CoreMessages` and
`CoreOperations.NonPipelineMessages`. Each scenario arranges its request twice, on fresh rows, and runs it two ways:

- **(a)** through `PipelineExecutor.ExecuteAsync`, as the administrator at depth 1 with no session (the pipeline opens one);
- **(b)** through `IPipelineCore.ExecuteCoreAsync` (core messages) or `IPipelineCore.ExecuteNonPipelineAsync` (the others) directly, as
  the administrator, in a transaction of its own that is committed when the call succeeds. A core message gets a `NestedRequest` that
  runs what the core issues (the record Update of `CommitFileBlocksUpload`, a queue item write) through the pipeline in that transaction.

Each section starts with the request as it was sent (`--- request ---`: its name, id and parameters), so an id that comes from the
request shares its token with the response. Then `--- response ---` holds the response CLR type (full name), `ResponseName` and the
results in ordinal key order, values as `Type:value`, or the fault that replaced the response (exception type, error code and HTTP
status for an `XrmdFaultException`, message).

`mutation.txt` is written by `tests/Xrmd.Integration.Tests/CoreRequestMutationProbeTests.cs`. For every scenario it runs the request
through (b) and compares a deep dump of the request before and after. The dump has object identities as tokens, so a replaced instance
shows. There is one line per message: `unchanged`, or the changed paths (`+` added, `-` removed, `~` changed value or another
instance), with `(fault)` when the call failed. On today's code no core message changes its request. That file is the contract that
Task 3 checks on the extension path.

## Writers
The known SDK types (requests, entities, references, collections, option sets, money, relationships) are written by explicit writers
that name their public data members. An entity's `Attributes`, `FormattedValues` and `KeyAttributes` are written in collection order
(the Web API writes them in that order, so a reordering fails); that order was the same on every rerun and on both providers. The exception is `RetrieveDuplicates`, whose core reads the matches with all columns: their order is the cached table
metadata's, which is definition order until a metadata reload (by an earlier test of the run) makes it name order, so its entities'
`Attributes` and `FormattedValues` are written by name. Other
bags (`Results` included) are written by key in ordinal order, lists by index. Reflection over the public
properties (ordinal order) is the fallback for any other type: metadata, `RolePrivilege`, `PrincipalAccess`, `AuditDetailCollection`,
`BusinessEntityChanges`. A `byte[]` longer than 64 bytes is written as its length and SHA-256.

## Normalisation
- Ids: the administrator's `{user}`, `{businessunit}`, `{organization}` and `{useraad}` by role, and `{prvReadpb_Pipe}`. `{unknown}`
  is the fixed id of `ExecuteWorkflow`'s missing workflow, and `{empty}` is the empty id.
- Named ids, per section: the rows a scenario arranges and reads back (`{a:rm-a}`, `{b:duplicate}`, `{a:audit-update}`,
  `{b:principal}`...) and the rows a call creates, read back from the database after the call by the scenario's anchor
  (`{a:created}` for Create, `{b:queueitem}`, `{a:accessteam}`, `{b:job}`, `{a:file}`). A returned id that is not the stored row's
  therefore fails.
- Every other id is numbered `{id1}`, `{id2}`... in first-seen order over the whole file.
- `DateTime` values are `{date:Utc}`, `{date:Local}` or `{date:Unspecified}` (the value is hidden, the kind is kept); a
  `DateTimeOffset` is `{date:+hh:mm}` with its offset. Formatted dates in `FormattedValues` are `{date}`.
- Row versions (`Entity.RowVersion`, `versionnumber`) are `{rowversion}`.
- A custom table's `ObjectTypeCode` is `{otc}`, because it depends on how many tables were created before.
- The results whose text changes with every call are written as `{label}`: `DataToken`, `FileContinuationToken` and
  `x-ms-session-token`.

The scenarios use fixed names only (`crg ...` rows; the tables `new_crgfile`, `new_crgtracked` and `new_crgelastic`; the option set
`new_crgoptions`, created once). They delete their own earlier rows where a read would see them. Everything they turn on (access teams
on account, duplicate detection, tracking tokens, the organization's auditing) is turned off again. The audit scenarios run on
`new_crgaudited` (auditing on, no other test uses it): a row is created and updated once, so the histories hold an
`AttributeAuditDetail` per change and `RetrieveAuditDetails` reads the update's audit row. `RetrieveFormattedImportJobResults` reads an
`importjob` with a one-result log, written through the core as the solution import writes it. `CalculateRollupField` runs on a database of its own, as
RollupMessageTests does, because it adds a rollup column to account. The files are the same on SQL Server and PostgreSQL.

## What today's code answers
- **(a) for a non-pipeline message** is `NotSupportedException`. The pipeline does not route those messages: `OrganizationService` and
  the plug-in service send them to `ExecuteNonPipelineAsync`. The pipeline would run one as a custom action only if a custom
  `sdkmessage` row of that name existed, and none exists here.
- **(b) for the metadata writes, `ImportSolution` and the caller-only solution messages** is the plug-in refusal (400 `0x80040203`).
- **(b) for `AddUserToRecordTeam` and `RemoveUserFromRecordTeam`** is 500 `0x80040216`. The core demands the security guard's check
  mark, which only the pipeline's guard sets.
- **(b) for `ExecuteWorkflow`** is `InvalidOperationException`, because its stage 30 is the workflows' hook. (a) asks for an unknown
  workflow and gets 404.

## What these files do not pin
- Object identity of a response (the same instance a stage-40 step edited) and early-bound values: Tasks 3 and 4 test them, and the
  S.2e2c plug-in context goldens cover what plug-ins see.
- The probe sees the request object graph only, not the marks kept in side tables keyed on the request (`CascadeHint`,
  `AuditActionHint`, the mail secrets, the guard's check mark).

## Skipped messages
| Message | Reason |
|---|---|
| `RetrieveAllEntities` | Its answer is the whole organization's metadata. Other tests in the shared database change it, so no file could stay stable. Its code path is `MetadataOperations.Retrieve`, which `RetrieveEntity`, `RetrieveRelationship` and `RetrieveOptionSet` also take. |
| `RetrieveAllOptionSets` | The same reason: every global option set of the organization. |

## Provenance
Captured from the code before `IPipelineCore` moved onto the model (base commit 8d421c0f, plan S.2e3a Task 1). The write-if-missing
rule is the same as for the S.2e2c goldens: a missing file is written once and the test fails, and an existing file is never
rewritten. A later task never regenerates them. `.gitattributes` turns off line-ending conversion for this directory.
