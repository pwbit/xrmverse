# Stage-20 golden dumps (S.2e3c2b)

Each `{scenario}.txt` is what the internal checks of stage 20 answered for one request in
`tests/Xrmd.Integration.Tests/Stage20GoldenTests.cs`: custom API authoring (`CustomApiAuthoring`), registration validation
(`RegistrationValidator`), process writes (`ValidateWorkflowWriteAsync`, `CheckWorkflowWriteAsync` and the Workflows hook), service
endpoints (`ServiceEndpointRules`, `WebhookSecrets`), mail secrets (`PendingMailSecrets`) and the owner a real-time workflow is matched
on (`RecordOwnerAsync`), all in `src/Xrmd.Pipeline/Sdk/PipelineExecutor.Sdk.cs` and the helpers it calls. They were captured on the code
before these checks move onto the model request (plan S.2e3c2b, Task 1; Review Focus 1, 2 and 4). Later tasks keep these files
byte-identical.

## Scenarios
- Custom APIs: a Create (the defaults and `sdkmessageid` filled in the caller's Target), a Create with a related record (400), an Update
  of an immutable column (400), a request parameter Create (`isoptional` filled), a Delete refused while a step is registered on the
  API's message (400), a Delete (message and filter rows gone), and a Create with an integer `uniquename`.
- Registration: a plug-in assembly Create from its content only (`name`, `version`, `culture`, `publickeytoken` filled), a step Create on
  stage 30 (400), a step Update moving `eventhandler` to a missing service endpoint (twice: as the System Administrator and as the internal
  caller), an Associate over `plugintype_sdkmessageprocessingstep` (400), a plug-in type Update with a string `isworkflowactivity`.
- Processes: a `workflow` Create without `type` (stage 40), a Create of type 2 (400), a Create without `type` whose XAML the Workflows hook
  refuses, an Update of an Activated definition (400), a SetState that activates a definition, a SetState of an activation (400), a
  `processtrigger` Create (400).
- Service endpoints: a Create with `authvalue`, a Create with a blank `authvalue` (400), a Create with an `OptionSetValue` `url`, an Update
  with a new `authvalue`, a Delete.
- Mail secrets: a mailbox Create with a password (stage 40), an `emailserverprofile` Update with a valid `incomingpassword` and a
  non-string `outgoingpassword` (400), a mailbox Upsert that creates and one that updates (stage 40 of the inner Create or Update), a
  CreateMultiple of two mailboxes with passwords (stage 40 of the inner Creates).
- The owner of a real-time workflow's table (`xrmv_order`, with an activated real-time workflow): an Update whose `ownerid` is a string,
  and a Create whose `ownerid` is a string, as the System Administrator and as the internal caller (pre-flight ruling P1).

A scenario arranges its rows as the administrator (with the administrator's `OrganizationService`), then runs its request once through
`PipelineExecutor.ExecuteAsync` at depth 1 with no session (the pipeline opens one), as `GuardFaultGoldenTests` does.

### Which code raises each fault
The fault a caller gets is the first one raised (guard, validators, process write, pre-operation steps, custom API, registration,
process write again, service endpoint, mail secrets). As captured at the base commit:
- `CustomApiAuthoring` (stage 20): `CustomApiCreateWithRelatedRecord`, `CustomApiUpdateImmutableColumn`, `CustomApiDeleteWithStep`,
  `CustomApiCreateIntegerUniquename` (its `InvalidCastException`).
- `RegistrationValidator` (stage 20): `StepCreateInvalidStage`, `AssociateStepEventhandler` (`DemandNotEventHandlerAssociate`),
  `PluginTypeUpdateStringIsWorkflowActivity` (its `InvalidCastException`).
- The security guard (before stage 10): `StepUpdateEventhandlerMissingEndpoint` (the lookup read, 404), `WorkflowCreateType2` and
  `ProcessTriggerCreate` (`SystemTableRules.DemandValidProcessWrite` run by the guard; the pipeline's own call of it comes later),
  `RealtimeOwnerCreateString` (the guard's `SdkValue<EntityReference>` read of `ownerid`).
- The core (stage 30): `StepUpdateEventhandlerMissingEndpointInternal` (the reference check, 404: the internal caller passes the guard
  and the registration validator), `RealtimeOwnerUpdateString` (the core's entity validation, 400: the owner read of the real-time
  workflow match reads the stored owner on an Update, so it never casts the Target's `ownerid`).
- The Workflows hook, through `ValidateProcessWriteAsync` before the pre-operation steps: `WorkflowCreateXamlRefused` (after `type` 1
  was filled in the caller's Target), `WorkflowUpdateActivated`.
- `CheckWorkflowWriteAsync` (stage 20): `SetStateOfActivation`.
- `ServiceEndpointRules` (stage 20): `EndpointCreateBlankAuthvalue`, `EndpointCreateOptionSetUrl` (its `InvalidCastException`).
- `PendingMailSecrets` (stage 20): `ProfileUpdateNonStringPassword` (both secret columns are out of the caller's Target: the valid one
  was taken before the non-string one was refused).
- `RecordOwnerAsync` (the real-time workflow match, before stage 20): `RealtimeOwnerCreateStringInternal`.

## Caller
The first line names the caller: the System Administrator (the administrator of the test organization, `{user}`), or the internal
caller (`Caller.Internal`), which skips the guard and is exempt from the custom API and process rules. The internal caller pins a check
that the guard reaches first for the administrator (ruling P1).

## File format
- `--- request ---`: the request as it was sent, with the `CoreDump` writers of S.2e3a (`tests/fixtures/core-response-golden/README.md`).
- `--- fault ---`: the fault (exception type, error code and HTTP status of an `XrmdFaultException`, message), or `none`.
- `--- request after ---`: the same request object as the caller holds it after the call, with the same writers: what the checks wrote
  into it (filled columns, an endpoint's id and `isauthvalueset`, removed secrets) on success and on refusal.
- `--- rows ---`: the rows of the table written, read with plain SQL after the call, one line per row as `{label}: column=value, ...`
  (strings quoted, Booleans `true`/`false`, ids normalised), or `{label}: none`. A count is written as `{label}: name=n`.
- `--- secrets ---` (service endpoints and mail tables): the rows of `meta.serviceendpoint_secret` (every row: a scenario starts with none)
  or of `meta.mail_secret` of the scenario's records, in the same form.
- `--- stage 40 ---` (where named): a `ContextRecorderPlugin` is registered on PostOperation of the message(s) on the table, synchronous
  (rank 1) and asynchronous (rank 2). One line per synchronous step: `step {Message|Stage|Mode|Depth}: target={column names of its
  Target}; targets={count of Targets}; parent={parent message}; parenttarget={column names of the parent's Target}`; then one line per
  asynchronous job the call queued: `job:` and, for each level of its `asyncoperation.data` payload from the step up, `{Message}|{Stage}
  target={column names}` (and `targets=` the column names of each of `Targets`). Lines are sorted; the jobs are deleted afterwards.
  Steps cannot be registered on `serviceendpoint`, so its scenarios have no stage-40 section: the caller's Target after the call is what
  a later stage would see.

## Normalisation
As in S.2e3c2a (`tests/fixtures/guard-fault-golden/README.md`), through `CoreScenarioKit.Normalize` with fixed dates kept:
- Named ids are `{a:label}`: the rows a scenario arranges (`{a:api}`, `{a:step}`, `{a:type}`, `{a:assembly}`, `{a:definition}`,
  `{a:activation}`, `{a:endpoint}`, `{a:queue}`, `{a:mailbox}`, `{a:profile}`, `{a:order}`). The administrator's ids are `{user}`,
  `{businessunit}`, `{organization}` and `{useraad}`. `{unknown}` is the fixed id of a missing record and `{empty}` the empty id. Every
  other id (a message or filter id, a created row's id) is `{idN}` in first-seen order over the file.
- A custom table's object type code in a fault text is `OTC={otc}`.
- The base64 content of `Xrmd.TestPlugins.dll` is written `{content of Xrmd.TestPlugins.dll}`, and the quoted XAML of the parity
  export's OnCreate (`tests/fixtures/workflows/real/OnCreate-....xaml`) `{xaml of the parity export's OnCreate}`: they are fixtures, not
  values a check changes.

## Determinism
The scenarios use fixed names only (`new_S20...` custom APIs, `s20 ...` processes, endpoints, mailboxes and profile, the queue
`s20q queue`, the orders `s20 order...`). A query reads only the rows of its own scenario. What a scenario creates is removed when the kit
is disposed: plug-in registrations and custom APIs (`PluginTestHelper.ClearAsync`), the `s20 ...` definitions (deactivated, then
deleted), every service endpoint and endpoint secret, the `s20 ...` mailboxes, the queue and the profile.

## Provenance
Captured from the code at base commit dbbc60f2 (plan S.2e3c2b Task 1; the branch head 692f6b2d differs from it only by the plan file).
The write-if-missing rule is the same as for the S.2e3a, S.2e3b and S.2e3c2a goldens: a missing file is written once and the test
fails, and an existing file is never rewritten. `.gitattributes` turns off line-ending conversion for this directory.
