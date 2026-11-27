# Guard fault golden dumps (S.2e3c2a)

Each `{scenario}.txt` is what the security guard (`SecurityGuard.AuthorizeAsync`) and the Security rules it runs first
(`SystemTableRules`, `CurrencyRules`, `EnvironmentVariableRules`, `RollupJobRules`, `AccessTeamRules`, `DuplicateRuleRules`) answered
for one request in `tests/Xrmd.Integration.Tests/GuardFaultGoldenTests.cs`. They were captured on the code before the guard and the
rules move onto the model types (plan S.2e3c2a, Task 1; Review Focus 1 to 3). Later tasks keep these files byte-identical.

## Scenarios
There is one scenario per guard branch and rule family: the record messages (Create, Update, Delete, Assign, Associate over a lookup
and over a security relationship, a role wider than the caller's), sharing (GrantAccess, RevokeAccess), the role privilege messages,
AddUserToRecordTeam (one scenario per first failing step of ruling A6, step 5 twice: a missing and a disabled user), the read and job
messages (RetrieveEntityChanges, RetrieveExchangeRate, PublishDuplicateRule, RetrieveDuplicates, BulkDetectDuplicates with FetchXML,
CalculateRollupField), email and queues (AddToQueue, RouteTo, PickFromQueue, RetrieveUserQueues, SendEmail from a queue), the bulk and
elastic messages (CreateMultiple, an elastic Upsert), field security (a Create and an Update with two denied secured columns, a field
share Create that is allowed, a field share Update of a fixed column), a duplicate rule condition Update, the rules that hold for every
caller (SYSTEM creating a `privilege`, a currency with a string rate, an environment variable value without a definition, the Delete of
a rollup job, a `workflow` of type 2), and malformed requests from a denied caller (a Create whose `Target` is an `EntityReference`, a
GrantAccess with no principal, a Delete by key only).

A scenario arranges its rows as the administrator (with the administrator's `OrganizationService`, or with plain SQL where the rules
refuse the row: the access team template on `contact`, the rollup job), then runs its request once through
`PipelineExecutor.ExecuteAsync` at depth 1 with no session (the pipeline opens one).

## Caller
The first line names the caller. It is a new user in the root business unit (`{a:caller}`) with only the Basic User role, or with Basic
User and a role holding the privileges listed (name and depth), or with only such a role, or SYSTEM (`{system}`). A scenario gives its
caller the privileges that take it past the earlier checks of the branch, so that the fault is the one of the step it pins (the
Assign to an owner without read, a role wider than the caller's, the right of a share the caller lacks, the last step of
AddUserToRecordTeam, the secured columns, the duplicate rule of a condition).

## File format
- `--- request ---`: the request as it was sent, with the `CoreDump` writers of S.2e3a (`tests/fixtures/core-response-golden/README.md`).
- `--- fault ---`: the fault (exception type, error code and HTTP status of an `XrmdFaultException`, message), or `none` when the call
  succeeded (the field share Create).
- `--- request after ---`: the same request object as the caller holds it after the call, with the same writers. Only the field share
  Create changes it: the rules fill `objectid`, `objecttypecode`, `readaccess` and `updateaccess` of its `Target`. The dump shows values,
  not instances (the new `objectid` instance is the Task 5 probe's).
- `--- rows ---`: the number of rows the request would have written (or deleted), counted with plain SQL after the call: `{label}: n`.
  It is 0 for every refused request and 1 for the field share Create. A request that writes nothing writes `rows written: 0`.

## Normalisation
As in S.2e3b (`tests/fixtures/core-row-golden/README.md`), through `CoreScenarioKit.Normalize` with fixed dates kept:
- Named ids are `{a:label}`: the caller (`{a:caller}`), the rows a scenario arranges (`{a:account}`, `{a:contact}`, `{a:role}`,
  `{a:template}`, `{a:queue}`, `{a:destination}`, `{a:item}`, `{a:email}`, `{a:rule}`, `{a:condition}`, `{a:record}`, `{a:share}`,
  `{a:job}`...), the other users (`{a:principal}`, `{a:member}`, `{a:added}`, `{a:assignee}`, `{a:disabled}`), and `{system}` for
  SYSTEM. Each label names one id per file. The administrator's ids are `{user}`, `{businessunit}`, `{organization}` and `{useraad}`.
  `{unknown}` is the fixed id of a missing record, template, user or currency, and `{empty}` the empty id. Every other id (a privilege
  id, a column's `MetadataId`, the elastic row's fixed id) is `{idN}` in first-seen order over the file.
- In a fault text, a custom table's object type code (`OTC=` followed by 10000 or more) is `OTC={otc}`: it depends on how many tables
  were created before. The seeded tables keep theirs (`OTC=1`, `OTC=4414`...).
- A date before 2024 is a fixed value a scenario wrote (`RecurrenceStartTime` of BulkDetectDuplicates) and is written as it is.

## Determinism
The scenarios use fixed names only (`gfg ...` rows; the table `new_gfgsecure` with the secured columns `new_gfgs1` and `new_gfgs2`,
created once; S.2e3a's elastic table `new_crgelastic`). A count reads only the rows of its own scenario. What a scenario turns on is
turned off again when the kit is disposed: access teams on account, duplicate detection on contact (and its rule), the template on
contact and the rollup job written with SQL. The files are the same on SQL Server and PostgreSQL (a local run with
`XRMD_TEST_PROVIDER=postgresql` passed against the files written on SQL Server).

## Provenance
Captured from the code at base commit 6ca311d1 (plan S.2e3c2a Task 1). The write-if-missing rule is the same as for the S.2e3a and S.2e3b
goldens: a missing file is written once and the test fails, and an existing file is never rewritten. `.gitattributes` turns off
line-ending conversion for this directory.
