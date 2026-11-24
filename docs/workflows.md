# Classic workflows

[← xrmverse documentation](../README.md#documentation)

## Introduction

xrmverse runs the classic processes that a solution brings: **workflows** (category 0, real-time or background),
**business rules** (category 2, Entity scope only) and **actions** (category 3). It reads their XAML with its own
interpreter: no XAML runtime and no Windows Workflow Foundation runs it. A process that uses a step or an expression
xrmverse cannot run faithfully is imported, but it fails explicitly when it would run, never silently. Custom workflow
activities (`CodeActivity`) run through CoreWF once you install the Microsoft workflow SDK yourself.

Microsoft's own description of these
features is on Learn: [workflow table](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/workflow),
[real-time workflows](https://learn.microsoft.com/power-apps/maker/data-platform/overview-realtime-workflows) (and, for their
stages, rank and `runas` in code, the on-premises developer page
[Create real-time workflows](https://learn.microsoft.com/dynamics365/customerengagement/on-premises/developer/create-real-time-workflows?view=op-9-1)),
[asynchronous service](https://learn.microsoft.com/power-apps/developer/data-platform/asynchronous-service),
[business rules](https://learn.microsoft.com/power-apps/maker/data-platform/data-platform-create-business-rule),
[`ExecuteWorkflow`](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/executeworkflow) and the
[workflow activities namespace](https://learn.microsoft.com/dotnet/api/microsoft.xrm.sdk.workflow.activities?view=dataverse-sdk-latest).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Workflows (real-time, background, on demand, child) | run from the solution XAML with an own interpreter | Same behaviour for the supported steps; no Windows Workflow Foundation |
| Business rules | Entity scope runs on the server | Narrower: Form scope is stored but not run on the server |
| Actions (XAML) | run as the main operation of their message, over the SDK, SOAP and the Web API | Same |
| Dialogs, business process flows, cloud/desktop/AI flows | imported and kept for export | Narrower: never run |
| Unsupported steps and expressions | process imports, then fails explicitly when it would run | Differs: Dataverse would run them |
| Custom workflow activities | run through CoreWF on .NET, in the `xrmd` process | Differs: no sandbox isolation; .NET Framework-only APIs fail |
| Pause and Resume of jobs | not supported (400) | Narrower: cancel instead |
| Workflow logs and process sessions | no such tables; progress is on `asyncoperation` | Narrower |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Bring processes in | solution import, or authoring as code | [Getting processes in](#getting-processes-in-activating-and-running-them) |
| Activate or deactivate | `SetStateRequest`, `PATCH workflows(<id>)`, `xrmd workflow activate` | [Getting processes in](#getting-processes-in-activating-and-running-them) |
| Run on demand | `ExecuteWorkflow` | [`ExecuteWorkflow`](#executeworkflow) |
| See what a step does | the step table and the catalog | [Steps and expressions](#steps-and-expressions) |
| Follow or cancel a background job | `asyncoperation` rows | [Background jobs and waits](#background-jobs-and-waits) |
| Run your own `CodeActivity` | install the workflow SDK, register the assembly | [Custom workflow activities](#custom-workflow-activities) |

## What runs

| Category | Runs | How |
|---|---|---|
| 0 Workflow, mode 1 (real-time) | yes | inside the triggering request, at its stage, in its transaction |
| 0 Workflow, mode 0 (background) | yes | as a system job (`asyncoperation`, `operationtype` 10) queued by the triggering request |
| 0 Workflow, on demand | yes | `ExecuteWorkflow` (below) |
| 0 Workflow, child | yes | queued as a background job by a "start child workflow" step |
| 2 Business rule, Entity scope | yes | real-time, stage 20 of Create and Update of its table |
| 2 Business rule, Form scope | no | imported and stored; it runs only in a form (the UI is phase 3) |
| 3 Action | yes | as the main operation (stage 30) of its own message |
| 1 Dialog, 4 Business process flow, 5 Modern flow, 6 Desktop flow, 7 AI flow | no | kept for export, never run; the import job log says so |

A process is one **definition** row (`type` 1) in the `workflow` table. Activating it creates an **activation** row
(`type` 2, a copy with `parentworkflowid` set to the definition), and the definition's `activeworkflowid` points to it;
triggers, jobs and `asyncoperation.workflowactivationid` use the activation. The tables are described in
[Tables in every organization](system-tables.md).

### Triggers

| Message | Triggers a workflow when |
|---|---|
| Create | `triggeroncreate` is true |
| Update | a column of `triggeronupdateattributelist` is in the request, even with an unchanged value |
| Assign | the list holds `ownerid` (an Assign is an update of the owner) |
| SetState | the list holds `statecode` or `statuscode` |
| Delete | `triggerondelete` is true |

Business rules run on every Create and Update of their table, whatever columns change, and never on Assign, SetState or
Delete. Writes that the core makes as a cascade (the child rows of a cascading Delete) trigger nothing; the record Update
that commits a file column upload is a write of that column and does trigger.

#### Scope

`scope` limits automatic triggers by the owner of the record (before the operation) compared with the owner of
the workflow: 1 the same user, 2 the same business unit, 3 the workflow owner's business unit or one below it, 4 any. A
record without an owner matches every scope. Business rules, on-demand runs and child workflows ignore scope.

### Stages and order with plug-ins

A real-time workflow runs at the stage of its message: `createstage`, `updatestage` (20 pre-operation or 40
post-operation) or `deletestage` (20 only; 40 is refused at activation). An empty stage means 40 for Create and Update and
20 for Delete. In a stage, synchronous plug-in steps and real-time workflows form one list ordered by `rank`, then plug-ins
before workflows at equal rank, then id. Nothing runs at stage 10 or on read messages.

#### The primary record at stage 20

An "update the primary record" step of a pre-operation workflow writes into the
message's `Target`, as a pre-operation plug-in would, instead of sending an Update; reads of the primary record see the
stored row with the `Target` laid over it. At stage 40 and in background runs it is a nested Update.

### Identity

| Run | Runs as |
|---|---|
| Real-time, `runas` 1 (Calling User) | the caller of the triggering request |
| Real-time, `runas` 0 (Owner) | the workflow's owner; a team owner fails the request with 400 |
| Background | the workflow's owner, who owns the job (`createdby` is the caller); a team owner fails the triggering request with 400 |
| On demand (`ExecuteWorkflow`) | the user who starts it, who also owns the job |
| Action | the caller of the action's message, whatever `runas` says |
| Child workflow | the child workflow's owner |

The owner of a process is the user who imported it (for a queued import, the owner of the import job); a later import keeps
the owner the process has. So the scope of an imported process and the user its background jobs run as are the importer's
until the process is assigned.

Every step sends its request through the pipeline at depth + 1 as that user: privileges, record access, field security,
plug-ins, auditing, duplicate detection and change tracking apply to it as to any request. Reads of columns are `Retrieve`
requests, so `Retrieve` plug-in steps fire on them. At depth 8 the next level fails with `0x80044182` (infinite loop), so a
workflow that triggers itself stops.

### Transactions

Real-time workflows and actions run in the triggering transaction: a failing step rolls back the request and every nested
write. A background job runs each step in a transaction of its own, so the steps before a failure stay committed. A job is
queued at post-operation inside the triggering transaction, so the job of a rolled-back write never exists.

## Getting processes in, activating and running them

Processes arrive in two ways. **Solution import** (`xrmd solution import`, `ImportSolution`, `pac` with Entra; see
[Solutions](solutions.md)) brings the processes of a file. **Authoring as code** writes them from YAML: `xrmd workflow
apply`, the authoring API and the MCP tools compile a file to the same designer XAML and store it in the same row (see
[Workflow authoring as code](workflow-authoring.md)). A raw Create or Update of `workflow` through the SDK, SOAP or the Web
API is accepted under the same rules (a Draft definition of category 0, 2 or 3, its XAML parsed and validated, no
server-written column); An Update re-validates the XAML only when it sends a definition column (the XAML, mode, trigger or stage columns), so an imported Draft with unsupported steps, or of category 1, 4 or 5, can still be renamed. A Create or Update of `processtrigger` is 400 ("its rows are written by solution import only").
What a client can do:

| Operation | SDK / SOAP | Web API | Rule |
|---|---|---|---|
| Activate | `SetStateRequest` (`State` 1, `Status` 2) or an Update of `statecode`/`statuscode` | `PATCH workflows(<id>)` with `"statecode": 1, "statuscode": 2` | needs Write on the definition, the activation privileges below; the XAML is parsed and validated (below) |
| Deactivate | `SetStateRequest` (`State` 0, `Status` 1) | `PATCH` with `"statecode": 0, "statuscode": 1` | deletes the activation row, unless a job that has not ended still uses it: then it is deleted when that job ends |
| Assign | `AssignRequest` | not available: a `PATCH` of `ownerid` is an Update of a column other than the state, so it is 400 with the refusal above; use `AssignRequest` through the SDK or SOAP | assigning a definition also reassigns its current activation, in the same transaction; background jobs queued from then on, `runas` 0 runs and the scope use the new owner |
| Delete | `DeleteRequest` | `DELETE workflows(<id>)` | 400 while the process is Activated; a Draft definition takes its activations with it and cancels (3/32) the jobs still waiting on them |
| Run on demand | `ExecuteWorkflowRequest` (`WorkflowId`, `EntityId`) | `POST workflows(<id>)/Microsoft.Dynamics.CRM.ExecuteWorkflow` with `{ "EntityId": "<id>" }` | see below |

Any other column in the activating Update, or a SetState of an activation row, is 400. `xrmd workflow` (apply, export, list,
activate, deactivate) and the matching MCP tools work on processes by name or id; there is no web admin page, and the
generic record commands and the Web API work on `workflows`.

### Activation privileges

Activating or deactivating a definition, from any surface, also needs: being its owner or holding
`prvActOnBehalfOfAnotherUser` (a definition owned by a team is not the caller's own); `prvActivateSynchronousWorkflow` for a
real-time workflow (`mode` 1) and for an action; `prvActivateBusinessRule` for a business rule. A background workflow needs
neither. System Administrator holds them by its template, System Customizer at Global, Basic User holds none; a solution
import is exempt. Details: [Security](security.md#classic-workflows).

### Activation checks

The XAML is parsed and checked; any of these is a 400 naming the reason: XAML that is not well formed
or not an `Activity`, a category that does not run, a mode other than 0 or 1, a wait in a real-time workflow, an action or a
business rule, an update trigger whose column list is empty, a stage that does not fit the trigger, a business rule without a
table, an action without a unique name. A process whose only problems are steps xrmverse does not run **is** activated: the
steps are listed in `xrmverse_unsupportedsteps` (one "path (display name): reason" per line) and every trigger of it fails
(see [What fails explicitly](#what-fails-explicitly)).

### `ExecuteWorkflow`

`WorkflowId` may be the definition or its current activation. Checks, in order: both ids given (400);
the process exists (404); it is a definition or an activation (400); category 0 (400); no `InputArguments` (400; an empty
collection counts as none); Activated, and a named activation is the current one (400; an activation that a deactivation
removed is 404); `ondemand` true (400); the record exists (404); the caller can read it (403). The caller needs Read on the
process and the privilege `prvWorkflowExecution` (System Customizer and Basic User hold it at Global; System Administrator
by its template). A background workflow is queued and the response `Id` is the job id; a real-time one runs at once, in the
request's transaction, and `Id` is `Guid.Empty`. Either runs as the user who started it.

## Steps and expressions

The XAML is read element by element. Every element, referenced activity type and expression shape the parser knows is an
entry of `ActivityCatalog` (`src/Xrmd.Workflows/Xaml/ActivityCatalog.cs`); anything else is an unsupported step. The steps
of the designer that run:

| Step | What it does on xrmverse |
|---|---|
| Create record | `Create`; empty values are left out. A created activity gets `isworkflowcreated` true |
| Update record (primary or related through an N:1 lookup) | `Update`; an empty value clears the column; at stage 20 on the primary record, see [above](#stages-and-order-with-plug-ins) |
| Assign record | `Assign` to a user or a team |
| Change status | `SetState` |
| Send email | creates the email, then `SendEmail` with `IssueSend` true: the email is Pending Send (state 1, status 6) and the send job sends it through the sender's mailbox, or sets it to Failed when that mailbox cannot send (see [Email and queues](email-and-queues.md#sending)) |
| Check condition, conditional branch, default action | the first true branch runs, otherwise the default branch |
| Wait condition, timeout | background only; see [Waits](#background-jobs-and-waits) |
| Start child workflow | queues a background job of the child (below) |
| Perform action | sends the action's message through the pipeline (an action, a custom API or a custom action); its outputs can be read by later steps |
| Stop workflow | Succeeded ends the run normally; Canceled ends it as canceled (below) |
| Custom workflow activity | runs the `CodeActivity` (below) |
| Business rule: set field value, set default value, clear field value, show error message | server-side effects (below) |
| Business rule: show or hide, enable or disable, set requirement level, recommendation | no-ops on the server |

### Values

Literals of every column type, columns of the primary record and of N:1 related records, the execution time,
records created by earlier steps, the action's own input arguments, outputs of earlier steps, arithmetic (`Add`,
`Subtract`, `Multiply`, `Increment`, `Decrement`, `Set`) on numbers and dates, text concatenation and a "first non-empty
value" choice. A value keeps its type: only two conversions exist (a Guid primary key to a reference of its table, and a
reference to a party list with one party); any other mismatch, for example a Money column read where the XAML expects a
Decimal, fails the run with a 400 naming the column and both types. These rules are ours until the checklist confirms them:

- an empty operand makes arithmetic empty; a date plus or minus a number moves it by that many days; the result keeps the
  first operand's type, and a result out of range fails;
- text comparisons ignore case, and an empty text counts as empty;
- with an empty operand, Equal is false, Not Equal true, the ordering and text operators false, except Does Not Contain,
  which is true;
- concatenation takes only text and the names of references (numbers, dates and choices would need the user's formats);
  only empty parts give an empty result;
- reading an action input that was not given, or an output before its step ran, gives an empty value.

No general Visual Basic expression is evaluated: any other expression text, `x:Code` or unknown function is an unsupported
step.

### The catalog

`Source` says where the entry comes from: `export` was seen in the real parity export
(`tests/fixtures/solutions/RealExports/xrmdWfTest_1_0_0_2.zip`), `learn` is a public type name of the Learn reference that the
export does not use, and `assumed` is the shape we expect the designer to write; every `assumed` entry is a line of checklist
item 1g or 1h. `ClientOnly` entries are business rule actions that do nothing on the server; the `Function` and `CalculatedArithmetic` operators are read only in a calculated column definition (plan 2.12a), and a process using one has an unsupported step; `Unsupported` entries make
the process's step unsupported, with the reason shown. The `mxswa:` prefix stands for the namespace
`Microsoft.Xrm.Sdk.Workflow.Activities`, `mcwc:` for `Microsoft.Crm.Workflow.ClientActivities`; names without a prefix
or namespace are System.Activities elements; full type names are `ActivityReference` targets. The table is generated from
the code, and `ActivityCatalogDocTests` keeps the two equal.

<!-- activity-catalog:start -->
| Entry | Kind | Status | Source | Reason |
|---|---|---|---|---|
| `mxswa:Workflow` | Root | Supported | export |  |
| `mxswa:CreateEntity` | Create | Supported | export |  |
| `mxswa:UpdateEntity` | Update | Supported | export |  |
| `mxswa:AssignEntity` | Assign | Supported | learn |  |
| `mxswa:SetState` | SetState | Supported | learn |  |
| `mxswa:SendEmail` | SendEmail | Supported | export |  |
| `mxswa:StartChildWorkflow` | ChildWorkflow | Supported | export |  |
| `mxswa:Postpone` | Wait | Supported | learn |  |
| `mxswa:GetEntityProperty` | GetValue | Supported | export |  |
| `mxswa:SetEntityProperty` | SetValue | Supported | export |  |
| `mxswa:RetrieveEntity` | Retrieve | Supported | learn |  |
| `mxswa:ActivityReference` | Reference | Supported | export |  |
| `mxswa:ReferenceLiteral` | TypeLiteral | Supported | export |  |
| `mxswa:SendEmailFromTemplate` | SendEmailFromTemplate | Unsupported | learn | sending an email from a template is not supported: xrmverse has no email templates |
| `mxswa:WebActivityReference` | WebActivityReference | Unsupported | learn | web activity references are not supported |
| `mxswa:GetPrimaryEntity` | GetPrimaryEntity | Unsupported | learn | GetPrimaryEntity is not part of the shapes xrmverse runs |
| `Sequence` | Sequence | Supported | export |  |
| `Assign` | EntityBookkeeping | Supported | export |  |
| `Persist` | Persist | Supported | export |  |
| `Variable` | Variable | Supported | export |  |
| `TerminateWorkflow` | Stop | Supported | export |  |
| `Microsoft.Crm.Workflow.Activities.Composite` | Composite | Supported | export |  |
| `Microsoft.Crm.Workflow.Activities.StepComposite` | StepComposite | Supported | assumed |  |
| `Microsoft.Crm.Workflow.Activities.ConditionSequence` | Condition | Supported | export |  |
| `Microsoft.Crm.Workflow.Activities.ConditionBranch` | ConditionBranch | Supported | export |  |
| `Microsoft.Crm.Workflow.Activities.EvaluateCondition` | EvaluateCondition | Supported | export |  |
| `Microsoft.Crm.Workflow.Activities.EvaluateLogicalCondition` | EvaluateLogicalCondition | Supported | export | export evidence from calculated column definitions only (2.12a) |
| `Microsoft.Crm.Workflow.Activities.EvaluateExpression` | EvaluateExpression | Supported | export |  |
| `Microsoft.Crm.Workflow.Activities.ConvertCrmXrmTypes` | ConvertTypes | Supported | assumed |  |
| `Microsoft.Crm.Workflow.Activities.ExecuteAction` | ActionCall | Supported | assumed |  |
| `mcwc:SetAttributeValue` | SetAttributeValue | Supported | export |  |
| `mcwc:SetMessage` | ClientSetMessage | Supported | export |  |
| `Microsoft.Crm.Workflow.Activities.SetDefaultValue` | SetDefaultValue | Supported | assumed |  |
| `Microsoft.Crm.Workflow.Activities.SetMessage` | ShowError | Supported | assumed |  |
| `Microsoft.Crm.Workflow.Activities.SetVisibility` | SetVisibility | ClientOnly | assumed |  |
| `Microsoft.Crm.Workflow.Activities.SetDisplayMode` | SetDisplayMode | ClientOnly | assumed |  |
| `Microsoft.Crm.Workflow.Activities.SetFieldRequiredLevel` | SetFieldRequiredLevel | ClientOnly | assumed |  |
| `Shape:EntityBookkeeping` | `Assign New Entity("x") to a #Temp key, its .Id, copies between entity dictionaries` | Supported | export |  |
| `Shape:RecordKeys` | `InputEntities("primaryEntity"), InputEntities("related_<lookup>#<entity>"), CreatedEntities("<step>")` | Supported | export |  |
| `Shape:StopException` | `TerminateWorkflow Exception = New InvalidPluginExecutionException(OperationStatus.Canceled\|Succeeded[, message]), Reason = the message` | Supported | export |  |
| `Shape:DesignerSequence` | a Sequence with DisplayName and Sequence.Variables for each designer step | Supported | export |  |
| `Shape:WaitBranch` | ConditionSequence Wait=True with one ConditionBranch: wait for its condition, then its Then | Supported | export |  |
| `Shape:WaitTimeoutBranch` | ConditionSequence Wait=True with a second branch whose Then starts with Postpone | Supported | assumed |  |
| `Shape:ElseBranch` | a last ConditionBranch whose Condition is [True]: the otherwise block | Supported | export |  |
| `Shape:ChildInputs` | StartChildWorkflow InputParameters = a variable whose Default is an empty Dictionary(Of String, Object) | Supported | export |  |
| `Shape:OutputAssign` | `Assign To = [<output argument>]: sets an action's output argument` | Supported | export |  |
| `Shape:ArgumentAttributes` | x:Property.Attributes Argument{Required,Target,Description,Direction,Entity}Attribute | Supported | export |  |
| `Shape:LookupLiteral` | `CreateCrmType { EntityReference, "entity", "name", <Guid literal variable>, "Lookup" }` | Supported | export |  |
| `Shape:TargetType` | Get/SetEntityProperty and EvaluateExpression TargetType: the value's own type, a Guid primary key as EntityReference, an EntityReference as EntityCollection (one activity party) | Supported | export |  |
| `Shape:StepLabel` | SetMessage.StepLabels with one mcwo:StepLabel (Description, LabelId, LanguageCode) | Supported | export |  |
| `Shape:CalculatedTargetType` | in a calculated column definition, a TargetType other than the value's own is the column's type, never another: it converts nothing | Supported | export |  |
| `Shape:RollupRule` | a rollup column definition: Sequence RollupRuleStep1 holding the designer sequences Source, Target and Aggregate | Supported | export |  |
| `Shape:RollupHierarchy` | the Source variable HierarchicalRelationshipName: the hierarchical relationship a rollup column aggregates over, [Nothing] for none | Supported | export |  |
| `Shape:RollupRelatedLink` | `mcwc:SetAttributeValue DisplayName="<related>.<lookup>.<relationship>" Entity=CreatedEntities("relatedlinked_<relationship>#<lookup>#<related>#Temp"): the related rows of a rollup column` | Supported | export |  |
| `EvaluateExpression:CreateCrmType` | Literal | Supported | export |  |
| `EvaluateExpression:RetrieveCurrentTime` | ExecutionTime | Supported | assumed |  |
| `EvaluateExpression:SelectFirstNonNull` | FirstNonNull | Supported | export |  |
| `EvaluateExpression:CustomOperationArguments` | ArgumentRead | Supported | export |  |
| `EvaluateExpression:Add` | Arithmetic | Supported | export |  |
| `EvaluateExpression:Subtract` | Arithmetic | Supported | export | export evidence from calculated column definitions only (2.12a) |
| `EvaluateExpression:Multiply` | Arithmetic | Supported | export | export evidence from calculated column definitions only (2.12a) |
| `EvaluateExpression:Increment` | Arithmetic | Supported | assumed |  |
| `EvaluateExpression:Decrement` | Arithmetic | Supported | assumed |  |
| `EvaluateExpression:Set` | Arithmetic | Supported | assumed |  |
| `EvaluateExpression:Divide` | CalculatedArithmetic | Supported | export | calculated column definitions only |
| `EvaluateExpression:Now` | CalculatedTime | Supported | export | calculated column definitions only |
| `EvaluateExpression:AddHours` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:AddDays` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:AddWeeks` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:AddMonths` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:AddYears` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:SubtractHours` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:SubtractDays` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:SubtractWeeks` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:SubtractMonths` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:SubtractYears` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:DiffInDays` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:DiffInHours` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:DiffInMinutes` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:DiffInMonths` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:DiffInWeeks` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:DiffInYears` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:Concat` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:TrimLeft` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:TrimRight` | Function | Supported | export | calculated column definitions only |
| `EvaluateExpression:Sum` | RollupAggregate | Supported | export | rollup column definitions only |
| `EvaluateExpression:Count` | RollupAggregate | Supported | export | rollup column definitions only |
| `EvaluateExpression:Maximum` | RollupAggregate | Supported | export | rollup column definitions only |
| `EvaluateExpression:Minimum` | RollupAggregate | Supported | export | rollup column definitions only |
| `EvaluateExpression:Average` | RollupAggregate | Supported | export | rollup column definitions only |
| `Vb:String.Concat` | Concat | Supported | assumed |  |
| `Vb:DirectCast` | Cast to System.String or System.Decimal | Supported | export |  |
| `Vb:DirectCast:OtherPropertyTypes` | Cast to the CLR type of another workflow property type | Supported | assumed |  |
| `Vb:ActionOutput` | `<perform action result>("<output>"): an output of a perform action step` | Supported | assumed |  |
| `EvaluateCondition:Null` | ConditionOperator | Supported | export |  |
| `EvaluateCondition:GreaterThan` | ConditionOperator | Supported | export |  |
| `EvaluateCondition:LessThan` | ConditionOperator | Supported | export |  |
| `EvaluateCondition:Equal` | ConditionOperator | Supported | export | export evidence from calculated column definitions only (2.12a) |
| `EvaluateCondition:GreaterEqual` | ConditionOperator | Supported | export | export evidence from calculated column definitions only (2.12a) |
| `EvaluateCondition:NotEqual` | ConditionOperator | Supported | assumed |  |
| `EvaluateCondition:NotNull` | ConditionOperator | Supported | assumed |  |
| `EvaluateCondition:Contains` | ConditionOperator | Supported | assumed |  |
| `EvaluateCondition:DoesNotContain` | ConditionOperator | Supported | assumed |  |
| `EvaluateCondition:BeginsWith` | ConditionOperator | Supported | assumed |  |
| `EvaluateCondition:EndsWith` | ConditionOperator | Supported | assumed |  |
| `EvaluateCondition:LessEqual` | ConditionOperator | Supported | assumed |  |
| `EvaluateCondition:Between` | ConditionOperator | Supported | assumed |  |
| `EvaluateCondition:NotBetween` | ConditionOperator | Supported | assumed |  |
| `EvaluateLogicalCondition:And` | LogicalOperator | Supported | export | export evidence from calculated column definitions only (2.12a) |
| `EvaluateLogicalCondition:Or` | LogicalOperator | Supported | export | export evidence from calculated column definitions only (2.12a) |
| `EvaluateLogicalCondition:Not` | LogicalOperator | Supported | assumed |  |
<!-- activity-catalog:end -->

## Background jobs and waits

A background workflow is a system job with `operationtype` 10: `name` is the workflow's name, `messagename` the
triggering message, `regardingobjectid` the record, `workflowactivationid` the activation, `depth` the triggering depth + 1,
and `data` the run's state (where it stopped and its variables). The worker runs it as its owner.

| State | Meaning |
|---|---|
| 0/0 | ready |
| 2/20 | running |
| 1/10 | waiting: for `postponeuntil`, or for a write of the record its wait condition reads |
| 3/30 | succeeded; with `asyncautodelete` the job row is deleted instead |
| 3/31 | failed, with the fault's code and message; workflow jobs are not retried |
| 3/32 | canceled: a stop step with Canceled (with its message), the workflow was deleted, or a client canceled it |

### Waits

A wait whose condition holds goes on at once. Otherwise the job suspends (1/10) and registers a row in
`meta.workflow_wait`; any core write of the record it waits on (Update, Assign, SetState, `SendEmail`, a lookup cleared by a
deleted parent, …) wakes it, it evaluates its condition again and suspends again while the condition is false. A "wait until
a date" step sets `postponeuntil`. When a timeout passes, its block runs and the rest of that wait's sequence is skipped. If
the record of a wait is deleted, the job fails (3/31, "The record of the wait was deleted."). A waiting job survives a restart:
its state is in `data`. Writes cost nothing extra while no job waits.

### Cancelling

Update the job to `statecode` 3, `statuscode` 32 (`PATCH asyncoperations(<id>)`): allowed while it is ready
or waiting; its wait row goes with it. Pause and Resume are not supported (the usual 400).

### Deactivating and deleting

Deactivating a workflow keeps its waiting jobs running to their end, on the kept activation.
Deleting a Draft definition, or uninstalling the managed solution that holds it, cancels its waiting jobs (3/32, "The workflow
was deleted.").

### Child workflows

The child must be Activated, have `subprocess` true and run on the same table as the record passed;
otherwise the step fails (400). It is queued at depth + 1 as a background job of its own, owned by the child workflow's owner,
also when the parent is real-time (then inside the parent's transaction). A job above depth 8 fails with `0x80044182`.

## Business rules on the server

A business rule runs on the server when its `processtriggerscope` is 2 (Entity), as the export's `<ProcessTriggerScope>`
element sets it, or when one of its `processtrigger` rows has `scope` 2. It runs at stage 20 of every Create and Update of its
table, in rank order with the plug-ins:

- **set field value** and **clear field value** write into the message's `Target`;
- **set default value** applies on Create only, and only when the column is empty;
- **show error message** fails the request with 400 `0x80040265` (the plug-in abort code) and the rule's text;
- show or hide, enable or disable, requirement level and recommendations do nothing on the server.

A Form-scope rule (scope 1) is stored and exported and never runs on the server.

## Actions

Activating an action (category 3), or importing one in any state, creates its message: an `sdkmessage` named with the
publisher prefix, `_` and the action's `uniquename` (an export's `Recalculate` of publisher `xrmv` becomes
`xrmv_Recalculate`; a name that already has the prefix keeps it). The message is created with the export's `SdkMessageId`
when that id is free, with a filter on the bound table (`none` for a global action). A name that clashes with a core message,
a custom API or another message is refused (400, and the import fails naming the process and the message). Deactivating
keeps the message, so plug-in steps registered on it stay; calling a Draft action's message is 400 "not activated". Deleting
the definition deletes the message, and is refused while a step is registered on it. The table and the category of an action
cannot change on a re-import.

Calling the message checks the request against the arguments of the XAML (`x:Members`): a missing required input, a value of
the wrong type, an undeclared parameter or a `Target` of another table is 400 `0x80040203`, as for custom APIs. Plug-in steps
on the message fire at stages 20 and 40 around the XAML, which runs at stage 30 in the caller's transaction, as the caller.
Output arguments the XAML set are in the response; outputs it did not set are left out. A stop with Canceled fails the
request with 400 `0x80040265`. How actions compare with custom APIs: see [Custom APIs](custom-apis.md).

### Calling an action

Through the SDK and SOAP, send an `OrganizationRequest` named after the message, with `Target` for a bound action. On the Web
API, an activated action is an OData action named after its message:

| Action | Request |
|---|---|
| Global (`primaryentity` `none`) | `POST /api/data/v9.2/<message>` (also with the `Microsoft.Dynamics.CRM.` prefix) |
| Bound to a table | `POST /api/data/v9.2/<entityset>(<id>)/Microsoft.Dynamics.CRM.<message>` |

The body is a JSON object with the input arguments; a bound action takes its `Target` from the URL (a `Target` in the body is
400). The request then runs through the organization service exactly as over SOAP, with the same checks and errors: a missing
required input is 400 `0x80040203`, the caller's privileges apply to the steps, and plug-in steps on the message fire. The
response is written as a custom API's: 200 with `@odata.context` `$metadata#Microsoft.Dynamics.CRM.<message>Response` and one
property per output argument (`null` for one the XAML did not set); the record or the collection itself when the only output is an Entity or an
EntityCollection; 204 when the action has no output arguments.

A Draft or deactivated action is not a Web API operation: the request is 404 `0x80060888`, as for any unknown segment, where
the same call over SOAP is 400 "not activated" (the 404 is from memory of Dataverse, not confirmed on Learn). An action is
always called with `POST`, never as a function, and a bound action only on a record of its table.

`$metadata` declares every activated action, after the custom APIs, as `<Action>` in `Microsoft.Dynamics.CRM`: bound with
`IsBound="true"` and a first parameter `entity` of its table's type, or global with an `<ActionImport>`; one parameter per input
argument (nullable when not required) and the complex type `<message>Response` for the outputs, with the type mapping of custom
APIs. An action that cannot be declared (bound to a table the model does not have, or a name taken by a type or operation of
the model) is left out of `$metadata` with a warning in the server log, as a custom API is.
`$metadata` follows activation and deactivation at once: the cache of the activated processes is refreshed after the commit
that changes them.

## Custom workflow activities

A plug-in type whose class derives from `System.Activities.CodeActivity` (checked by full name, without loading the
assembly) is a workflow activity: registration sets `isworkflowactivity` true, and an import marks the types that have a
`WorkflowActivityGroupName`. A type sent with `isworkflowactivity` true that is not a `CodeActivity` is 400.

Running one needs `Microsoft.Xrm.Sdk.Workflow.dll`, which xrmverse does not ship. `xrmd sdk install` downloads it with the
other SDK packages after the one license acceptance (`--from-workflow <file.nupkg>` offline); see
[Installing the Microsoft SDK](microsoft-sdk.md). The host starts without it. Without it, a step that names a custom activity is
an unsupported step with the reason "custom workflow activities need the Microsoft workflow SDK: run xrmd sdk install",
shown in the import log and `xrmverse_unsupportedsteps`; activation and import check again, so installing the SDK and
re-activating (or restarting) is enough.

The activity loads in the plug-in's collectible context, with CoreWF's `System.Activities` and the installed
`Microsoft.Xrm.Sdk.Workflow` shared from the host, and runs through CoreWF's `WorkflowInvoker` with a context
(`IWorkflowContext`), an organization service factory and a tracing service:

- **Arguments.** Inputs are `InArgument<T>` with `[Input]`, outputs `OutArgument<T>` with `[Output]`. A value must already be
  of the argument's type and an `EntityReference` must point to the table of `[ReferenceTarget]`; otherwise, and for an unset
  `[RequiredArgument]` input, an unset input with a `[Default]` or an empty value for a value type, the step fails with 400
  naming the argument.
- **Context.** `Depth` is the triggering message's; `StageName` is `PreOperation` (20), `MainOperation` (actions and real-time on-demand runs) or
  `PostOperation` (40 and background); `Mode` is 0 for real-time and actions, 1 for background, `WorkflowMode` the
  opposite; `WorkflowCategory` the process's; `ParentContext` is null; a background run has empty input parameters and
  there are no entity images. A member xrmverse does not fill throws `NotSupportedException` naming it.
- **Service.** `CreateOrganizationService(null)`, `Guid.Empty` or the run user's id act as the run user; another id acts as
  that user. Requests go through the pipeline as the workflow's own steps do; a failed call inside a transaction dooms it, as
  for plug-ins.
- **Errors and limits.** As a plug-in: `InvalidPluginExecutionException` is 400 with its message, any other exception 500,
  the plug-in timeout applies, and traces go to `plugintracelog` (`operationtype` 2, no step id) under
  `plugintracelogsetting`. A type without a parameterless constructor is 400. `StopWorkflowException` with `Succeeded` ends
  the run normally, with `Canceled` as a canceled stop, with `Failed` as the 400 abort (a background job fails 3/31);
  `Retry` and `Suspended` are 400 "not supported". An instance is created per run.

## What fails explicitly

| Where | What | How it shows |
|---|---|---|
| Import | a step or expression xrmverse does not run | a warning in the import job log naming the process and each step; the process keeps its state from the file and `xrmverse_unsupportedsteps` lists the steps; the rest of the solution imports |
| Import | a process that fails the activation checks | a warning, and the process stays Draft |
| Import | a category that does not run (1, 4–7) | a warning "kept for export but not run" |
| Import | a `StateCode` other than 0 or 1 | a warning, and the process stays Draft |
| Run time, real-time and `ExecuteWorkflow` | a process with unsupported steps | 400 `0x80040203` "Workflow '…' cannot run on xrmverse: …", nothing is written |
| Run time, background | the same | the job fails 3/31 with that text |
| Run time | a step error (privilege, missing record, value of the wrong type, …) | the step's fault: the request fails (real-time) or the job fails 3/31 (background) |
| Run time | a stop with Canceled in real time or in an action | 400 `0x80040265` with the stop's message, or "The workflow '…' canceled the operation." when it has none |
| Run time | an activation whose XAML no longer parses | treated as a process with an unsupported step |

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

Each item is refused with an error or stated here; none is a silent difference.

- **`processtrigger` is written by solution import only**; a Create, Update or Delete of it is 400. A public Create or Update
  of `workflow` is accepted (see [Workflow authoring as code](workflow-authoring.md#what-is-refused-and-why)), but not with
  the server-written `xrmverse_unsupportedsteps`. The limits of authoring are on
  [that page](workflow-authoring.md#known-limits).
- **No `workflowlog`, `processsession` or process stage tables.** Reading them is the usual unknown-table error. A
  background job's progress and error are on its `asyncoperation` row (`message`, `friendlymessage`, `errorcode`).
- **No Pause or Resume** of workflow jobs; cancel them instead.
- **No Assign over the Web API.** `PATCH workflows(<id>)` with `ownerid@odata.bind` is 400; assign a process with
  `AssignRequest` (SDK or SOAP).
- **Form-scope business rules** do not run on the server (no forms until phase 3).
- **"Send email from template"** is an unsupported step: xrmverse has no email templates.
- **Custom workflow activities** run on .NET: an API that exists only on .NET Framework fails with the plug-in "could not be
  loaded" error. There is no isolation: the activity runs in the `xrmd` process, as plug-ins do.
- **`primaryentity`** and `processtrigger.primaryentitytypecode` are text columns (String 100) holding the table's logical
  name, not EntityName columns.
- **On-demand runs** do not accept `InputArguments`.
- **A lookup literal to a record of another organization** (for example the exporting organization's currency in an email
  step) fails the step with a 404 naming the table and id; create a record with that id, or change the process, and run it
  again.
- **At-least-once.** A job interrupted by a crash, or by a stop in the middle of a run, runs again from its last saved state, so
  the steps after its last wait can run twice.
- **Background workflows on Delete** run after the record is gone: a step that reads it fails the job (404). Dataverse gives
  them the record's pre-image.
- **Team owners.** A background workflow, or a real-time one with `runas` 0, owned by a team fails its trigger with 400.
- **Not checked against Dataverse.** Every `assumed` catalog entry, the expression rules above, the default stages, the
  order at equal rank, the job messages, the error codes of the refusals and the context values of custom activities are our
  reading until a real Dataverse confirms them.

## See also

- [Workflow authoring as code](workflow-authoring.md)
- [Plug-ins](plugins.md)
- [Solutions](solutions.md)
- [Security](security.md#classic-workflows)
- [Custom APIs](custom-apis.md)
- [Tables in every organization](system-tables.md)
