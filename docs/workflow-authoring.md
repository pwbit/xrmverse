# Workflow authoring as code

[← xrmverse documentation](../README.md#documentation)

## Introduction

Workflows, business rules and actions are written as **YAML files**, kept in Git and applied with `xrmd workflow apply`
(or the Web API, or the MCP tools). The server compiles the file to the same **designer XAML** that a solution brings and
stores it in the same `workflow` row, so there is one format and one engine: what you author runs exactly like an imported
process ([Classic workflows](workflows.md)), and it is built to be compatible with the Dataverse classic designer: the
compiled XAML uses the step names, variables and descriptions of that designer, and a solution exported from xrmverse is
meant to open there (the unconfirmed parts are listed in [Assumed shapes](#assumed-shapes)). `xrmd workflow export` turns
a process back into YAML, whether you authored it or imported it.

Microsoft describes the features themselves on Learn: the
[workflow table](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/workflow) and
[business rules](https://learn.microsoft.com/power-apps/maker/data-platform/data-platform-create-business-rule).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Authoring surface | YAML files kept in Git, compiled on the server; the [workflow studio](workflow-studio.md) edits the same YAML on a canvas | Extra: Dataverse authors classic processes in its designer |
| Stored result | the same designer XAML in the same `workflow` row | Same format as an imported process |
| Export | YAML or the stored XAML; refused when the YAML cannot represent it | Extra: never an approximation |
| Designer compatibility | built to open in the classic designer | Unverified for the [assumed shapes](#assumed-shapes) |
| Form-scope rules, dialogs, business process flows, recommendations | refused | Narrower |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Write a process | one YAML file per process | [The format](#the-format) |
| Check it without writing | `xrmd workflow apply --dry-run` (the HTTP and MCP default) | [Commands, API and tools](#commands-api-and-tools) |
| Apply and activate | `xrmd workflow apply <file> --activate` | [Commands, API and tools](#commands-api-and-tools) |
| Turn a process back into YAML | `xrmd workflow export <nameOrId>` | [Export](#export-and-not-representable) |
| Put it in a solution | `--solution <name>` | [Solutions and actions](#solutions-and-actions) |
| Understand a refusal | the error list with line and column | [What is refused and why](#what-is-refused-and-why) |

## The format

One file holds one process. The top-level keys are the same for every kind; the others depend on `kind`. An unknown key, a
key of another kind or a duplicate key is an error that names its line and column. Reading is strict: nothing is coerced
(`true`, `1` and `2026-01-01` stay text until the compiler types them by the column they go into).

`workflow` keys: `mode` (required), `scope`, `runAs`, `logFailures`, `autoDelete`, `trigger`, `onDemand`, `childProcess`.
`businessRule` keys: `scope: entity` (the only value). `action` keys: `message` (required) and `arguments`. The limits are a
body of at most 1 MiB and a nesting of at most 64 levels.

The reference below is generated from the code (`WorkflowYaml.Keys`); a test keeps it equal to it.

<!-- workflow-yaml:start -->
| Key | Meaning |
|---|---|
| `kind` | workflow, businessRule or action (required) |
| `id` | the workflow id (a GUID, optional) |
| `name` | the workflow name (required, at most 100 characters) |
| `description` | the description |
| `entity` | the primary table (required; an action takes a table or none for a global action) |
| `steps` | the list of steps (required) |
| `mode` | workflow only (required): realtime or background |
| `scope` | workflow: user (default), businessUnit, parentChildBusinessUnits or organization; business rule: entity (the only one) |
| `runAs` | real-time workflow only: caller (default) or owner |
| `logFailures` | real-time workflow only: log a failure (default true) |
| `autoDelete` | background workflow only: delete finished runs (default false) |
| `onDemand` | workflow only: a user can start it by hand (default false) |
| `childProcess` | workflow only: other workflows can start it as a child (default false) |
| `trigger.create` | workflow only: true, or { stage: pre \| post } (stage on a real-time workflow only) |
| `trigger.update` | workflow only: { columns: [&lt;column&gt;, ...], stage: pre \| post }; ownerid is record assigned, statecode is status changes |
| `trigger.delete` | workflow only: true, or { stage: pre } (stage on a real-time workflow only) |
| `message` | action only (required): the message name |
| `arguments` | action only: a list of { name, direction: input \| output, type, entity?, required?, description? } |
| `steps[].create` | { id?, entity, set }: create a record |
| `steps[].update` | { record, set }: update a record |
| `steps[].assign` | { record, owner }: assign a record |
| `steps[].setState` | { record, state, status }: set the state and status of a record |
| `steps[].sendEmail` | { id?, set }: send an email described by its columns |
| `steps[].if` | { when, then, elseIf?: [{ when, then }], else? }: a condition |
| `steps[].wait` | background workflow only: { until?, timeout?, then, onTimeout? } (at least one of until and timeout) |
| `steps[].stop` | { status: succeeded \| canceled, message? }: stop the workflow |
| `steps[].startChild` | { workflow, record? }: start a child workflow (a name or GUID) on a record (default primary) |
| `steps[].performAction` | { id?, message, target?, inputs? }: run an action |
| `steps[].customActivity` | { id?, type, assembly, inputs?, outputs? }: run a custom workflow activity |
| `steps[].setOutput` | action only: { argument, value }: set an output argument |
| `steps[].setValue` | business rule only: { field, value } |
| `steps[].setDefault` | business rule only: { field, value }: set when the column is empty |
| `steps[].clearValue` | business rule only: { field } |
| `steps[].showError` | business rule only: { field?, message } |
| `steps[].setVisibility` | business rule only: { field, visible: true \| false } |
| `steps[].setLock` | business rule only: { field, locked: true \| false } |
| `steps[].setRequirement` | business rule only: { field, level: none \| recommended \| required } |
| `steps[].*.description` | every step takes an optional description |
| `record` | primary, { related: &lt;lookup column&gt; } or { created: &lt;step id&gt; } |
| `value` | a scalar (a literal; ~ is the empty value) or one of { field, related?, created? }, { record }, { arg }, { output, of }, { now: true }, { lookup, id, name? }, { add \| subtract \| multiply \| increment \| decrement \| set \| concat \| firstOf: [&lt;value&gt;, ...] } |
| `condition` | a value plus op and value ({ field: a, op: gt, value: 1 }), or { all: [...] }, { any: [...] }, { not: ... }; op is eq, ne, null, notNull, contains, notContains, beginsWith, endsWith, gt, ge, lt or le |
<!-- workflow-yaml:end -->

Defaults are the values the designer's export writes for each kind: a real-time workflow runs as the calling user (`runAs:
caller`) and logs failures; a background workflow keeps its finished runs unless `autoDelete` is true; a workflow's `scope`
is `user`; a business rule and an action carry the Organization scope in `scope`, as the export shows (a rule's own scope is
Entity, in `processtriggerscope`). The `scope` default of a workflow is from memory of the designer, not yet verified.

### Triggers

`trigger.create`, `trigger.update` and `trigger.delete` are the events a workflow starts on. `columns` is the update trigger's
column list in the order given; the column `ownerid` means "record is assigned" and `statecode` means "status changes". A
`stage` (`pre` or `post`) is accepted on a real-time workflow only; the default is `post` for create and update, and `pre`
for delete, the only stage a delete takes. A workflow with no trigger and `onDemand: true` runs when a user starts it.

### Steps

Each list item is a mapping with exactly one step key; every step takes an optional `description` (see
[Names and descriptions](#names-and-descriptions)). Activity tables (`task`, `email` and the like) use `activityid` as their key, so `{ record: primary }` and `{ created: ... }` work on them as on any table. A record is `primary`, `{ related: <lookup column of the primary table> }`
or `{ created: <id of an earlier create, sendEmail or performAction step> }` (a performAction step's result is read with
`{ output, of }` instead, see below). `create` and `sendEmail` take an optional `id` that later steps use to refer to the
record they create. `wait` is for background workflows only and needs `until`, `timeout` or both; the steps after the wait
are its `then` list. `startChild` names a workflow (or its GUID) that has `childProcess: true`.

Business rules take `if` and, inside a condition, the rule actions `setValue`, `setDefault`, `clearValue`, `showError`,
`setVisibility`, `setLock` and `setRequirement`. A rule action outside an `if` is refused. Only `setValue`, `setDefault`,
`clearValue` and `showError` have an effect on the server; the others are written to the XAML for the designer and do nothing
on the server (they matter in a form, which xrmverse does not have yet).

### Values and conditions

A YAML scalar is a literal, typed by its destination: the column of `set` or `field`, the argument of `setOutput` or `inputs`,
or the left operand of a condition. A plain `null` or `~` is the empty value (it clears a column); a quoted `'null'` is the
text. Every dynamic value is a mapping with one kind key:

| Value | Meaning |
|---|---|
| `{ field: <column> }` | a column of the primary record; add `related: <lookup>` for the record that lookup points to, or `created: <id>` for a created record |
| `{ record: <record> }` | a reference to that record (for a lookup column) |
| `{ arg: <input argument> }` | an input argument of the action |
| `{ output: <name>, of: <step id> }` | an output of a performAction step, or a named output of a customActivity step |
| `{ now: true }` | the time the workflow runs |
| `{ lookup: <table>, id: <guid>, name?: <text> }` | a literal reference; `name` defaults to the record's primary name, read when the file is compiled (a missing or unreadable record is an error) |
| `{ add \| subtract \| multiply \| increment \| decrement \| set: [<value>, ...] }` | arithmetic over the operands |
| `{ concat: [<value>, ...] }` | text concatenation |
| `{ firstOf: [<value>, ...] }` | the designer's "default value": the first operand that is not empty (two operands or more) |

A comparison is a value plus `op` and, except for `null` and `notNull`, `value`. The operators are `eq`, `ne`, `null`,
`notNull`, `contains`, `notContains`, `beginsWith`, `endsWith`, `gt`, `ge`, `lt` and `le`. Conditions combine with `all`,
`any` and `not`. There is no `between`: write `all: [ge, le]`, which is what the designer's Between reads back as, so an
export always returns that form. A `concat` cannot be the left operand of a comparison.

The literal formats are invariant: Boolean `true` or `false`, numbers with a point, a DateTime as ISO 8601 UTC. A choice value
is its number and must be one of the column's options.

The compiler reads the table metadata to pick the type of every literal, and refuses a value the destination cannot take (a
record reference into a text column, text into a lookup, a column that is not valid for create or update).

### Examples

The examples below are complete files; each is the YAML form of a process of the real export that xrmverse's
parity tests use. A test compiles every `yaml` block of this page.

A workflow, background: wait until the amount is over 100, then email the primary contact of the order's account.

```yaml
kind: workflow
id: bc9a625b-e4de-f111-84e8-002248872b66
name: OnUpdate
entity: xrmv_order
mode: background
scope: organization
autoDelete: true
trigger:
  update: { columns: [xrmv_status] }
steps:
  - wait:
      until: { field: xrmv_amount, op: gt, value: 100 }
      then:
        - sendEmail:
            set:
              to: { field: primarycontactid, related: xrmv_accountid }
              regardingobjectid: { record: primary }
              correlatedsubjectchanged: false
              deliveryprioritycode: 1
              directioncode: true
              followemailuserpreference: false
              isduplicatesenderunresolved: false
              notifications: 0
              prioritycode: 1
              transactioncurrencyid: { lookup: transactioncurrency, id: 692363f0-a3da-f111-84e8-3833c5c48caf, name: Euro }
```

A real-time workflow: on create, make a task and start a child workflow.

```yaml
kind: workflow
id: 3e1f0307-d8de-f111-84e8-002248872b66
name: OnCreate
entity: xrmv_order
mode: realtime
scope: organization
trigger:
  create: true
steps:
  - create:
      entity: task
      set:
        subject: TEST
        regardingobjectid: { record: primary }
        prioritycode: 1
  - startChild: { workflow: Child }
```

A business rule (Entity scope): when the amount is empty, set a status; otherwise show an error on the status.

```yaml
kind: businessRule
id: 216fa344-e5de-f111-84e8-002248872b66
name: New business rule
description: Click to add description
entity: xrmv_order
steps:
  - if:
      description: New Condition
      when: { field: xrmv_amount, op: null }
      then:
        - setValue: { field: xrmv_status, value: 128830001, description: New Action }
      else:
        - showError: { field: xrmv_status, message: fdsfds, description: New Action }
```

An action bound to a table, with an input and an output argument:

```yaml
kind: action
id: 9df696a1-e6de-f111-84e8-002248872b66
name: Recalculate
entity: xrmv_order
message: xrmv_Recalculate
arguments:
  - { name: Increment, direction: input, type: decimal, required: true, description: New Argument }
  - { name: Total, direction: output, type: decimal, required: true, description: New Argument }
steps:
  - update:
      record: primary
      set:
        xrmv_amount: { add: [{ field: xrmv_amount }, { arg: Increment }] }
  - setOutput: { argument: Total, value: { field: xrmv_amount }, description: 'Total\' }
  - if:
      when: { arg: Increment, op: lt, value: 0 }
      then:
        - stop: { status: canceled }
```

(In the business rule, `description` on an `if` is written as the `Description` of each of its branches, and the exporter
reads it back from there.)

## Names and descriptions

The classic designer shows each step by its name and description, so the compiler writes them as the designer does. A step's
name is a counter over the process's designer steps in document order, with the prefix of its kind (`CreateStep1`,
`UpdateStep2`, `ConditionStep3`, `ConditionBranchStep4`, `WaitStep5`, `StopWorkflowStep6`, ...), followed by `: ` and the
step's `description` when it has one. The variables the XAML needs are named after their step. A business rule's actions are
named `<Kind>Step_ConditionBranchStep<n>_<k>` after the first branch of their condition, with `k` counting the rule's actions
in document order (the real rule's habit, kept). On export the generated part of a name is removed again and what is left is
the `description`.

Step label ids (the ids of the labels a business rule shows) are derived from the process id and the step's name, so
compiling the same file twice gives the same bytes.

## Identity, updates and state

- **Identity.** `apply` targets the process whose id is the file's `id`. Without `id`, the id is derived from the kind, the
  table and the name (a version 5 UUID of `kind/entity/name` in lower case, in a fixed namespace), so the same file gives the
  same id in every organization and a solution that holds it does not duplicate it on import. `export` always writes `id`.
- **Name clash.** Before creating, `apply` refuses when another process of the same kind and table has the name but another
  id: "a process named '...' already exists with id ...; add `id: ...` to the file to update it". A clash with a process the
  caller cannot read gives no id: "a process named '...' already exists; add its id to the file to update it".
- **Unreadable processes.** Before anything else, `apply` (a dry run too) retrieves the process the file's id (or the derived
  id) names as the caller. When the caller cannot read it, the only error is "the process with id ... cannot be read or
  changed by you", and the result carries the file's id and name, `unchanged` and `draft`, never the stored name, state or
  kind.
- **Update in place.** An existing process is updated in place (activations are its versions, as in
  [Classic workflows](workflows.md#getting-processes-in-activating-and-running-them)). Its kind, table and (for an action) its
  message cannot change: that is a 400. An update of an imported process also refreshes its exported names, descriptions and
  step labels.
- **State.** A new process is **Draft**. An existing process keeps its state. An Activated one whose compiled XAML or columns
  differ is deactivated, updated and activated again in the same transaction (jobs waiting on the old activation keep it). A
  file that changes nothing writes nothing at all: the result says `unchanged`, and no update, no state change and no audit
  row are made, so a CI job can apply the same files again without disturbing anything. `--activate` activates after the
  write (and does nothing when the process is already Activated). The YAML has no state key: `activate` and `deactivate` are
  their own commands.
- **One transaction.** `apply` writes the process, adds it to the solution and changes its state in one transaction, as the
  caller: a failure in any of them rolls everything back, and plug-ins and auditing see an authored process like any other
  write. A dry run compiles, resolves the identity and reports what would happen (`created`, `updated` or `unchanged`, and the
  state change) and writes nothing.

## Solutions and actions

`--solution <unique name>` (or the `solution` query option) puts the process in an **unmanaged** solution: a new process is
created in it, and an existing one that is not yet in it is added (component type 29). The new process's introduced version is
the solution's version, or `1.0.0.0` without one. Because the process is a solution component, `xrmd solution export` carries
it: for a process whose XML xrmverse has not kept, the export writes the step labels and descriptions as an imported rule
has them, so an authored process exports like an imported one ([Solutions](solutions.md)).

An action's `message` is the full message name callers use, for example `xrmv_Recalculate`. The compiler splits it into the
publisher prefix and the unique name (`Recalculate`, as the export stores it). The prefix must be the prefix of the publisher of
the `--solution`, or without one of the solution that already holds the process, or else of the Default solution's publisher; a
mismatch is a 400 naming both prefixes. A bound action gets its `Target` input generated and never written in the file; an
action with `entity: none` is global. An argument is `{ name, direction: input | output, type, entity?, required?, description? }`
with `type` one of `boolean`, `dateTime`, `decimal`, `double`, `entityReference` (with `entity`), `integer`, `money`,
`picklist` and `string`. An authored action gets its message when it is applied, even as Draft, as an import does; calling it
while it is Draft is a 400 "is not activated".

The literal inputs of a `performAction` and `customActivity` step are typed by what they call: the request parameters of the
message and the argument types of the activity. A `performAction` step refers to a record it makes only through
`{ output, of }`; `{ created: <performAction id> }` is refused.

## Commands, API and tools

Every surface runs as the caller, so the Dataverse privileges decide ([Privileges](#privileges)).

| What | CLI | HTTP | MCP tool |
|---|---|---|---|
| Apply a file | `xrmd workflow apply <file.yaml> [--solution <name>] [--activate] [--dry-run]` | `POST /authoring/workflows/apply?dryRun=&solution=&activate=` (body: the YAML, `text/yaml`) | `apply_workflow` |
| Export | `xrmd workflow export <nameOrId> [--format yaml\|xaml] [--file <path>]` | `GET /authoring/workflows/{nameOrId}?format=yaml\|xaml` | `export_workflow` |
| List | `xrmd workflow list [--kind <kind>] [--entity <table>]` | `GET /authoring/workflows?kind=&entity=` | `list_workflows` |
| Activate, deactivate | `xrmd workflow activate <nameOrId>`, `deactivate <nameOrId>` | `POST /authoring/workflows/{nameOrId}/activate`, `.../deactivate` | `set_workflow_state` (`activated: true \| false`) |

### CLI

`apply` reads the file and checks its YAML before it sends anything: a local error exits **2** and prints
`path:line:column: message`. A document the server refuses prints every error (each with its line and column) and exits **5**;
nothing is written. The assumed shapes the file uses are printed as `warning: <shape>` lines on stderr. Exit codes are the
usual ones: 3 for authentication and missing privileges, 4 for a name or id that does not exist, 5 for a refused request. The
export option is `--format` (the global `--output` is `table` or `json`). YAML goes to stdout unless `--file` is given.

### HTTP

The routes are under `/authoring/workflows`, beside the Admin API, but they need only a valid token (401 without one):
no role. They use the Web API error envelope. `dryRun` defaults to **true**, as in `POST /admin/schema/apply`; pass
`dryRun=false` to write. The response is the result below: 200, or 400 with the same body when it has errors. A body larger
than 1 MiB is a 400. See [Admin API](admin-api.md#the-authoring-api).

### MCP

The four tools are described in [MCP](mcp.md#workflow-tools). `apply_workflow` defaults to a dry run too; a refused
document is a tool error (`isError`) whose text holds the result.

### The apply result

The result of `apply` is `{ id, name, action: created | updated | unchanged, state: draft | activated, stateChanged,
assumedShapes, errors }`; every error has a `message` and, when it comes from the file, `at: { line, column }`. `action`
describes the definition only: adding an existing process to a solution without other changes reports `unchanged`. A name that
matches several processes is a 400 that lists their ids; in the CLI and the HTTP routes a name that contains `/` must be given
by its id (MCP accepts it).

## Privileges

The authoring surfaces decide by Dataverse privileges ([Security](security.md#classic-workflows)):

| Operation | Needs |
|---|---|
| Every apply (a dry run too), list, export | `prvReadWorkflow`. The process the file's id names, the processes its name clashes with, child workflows and lookup records are retrieved as the caller: an unreadable lookup record or child workflow is reported like a missing one, and an unreadable process of the file's id or name gives an error that names nothing of it. The stored columns of a readable process, and the actions a `performAction` step may call, are read without a further check |
| Create a process | `prvCreateWorkflow` |
| Update a process | `prvWriteWorkflow` at the record's depth |
| `--solution` | the checks of `AddSolutionComponent` |
| Activate or deactivate | Write on the definition, and either being its owner or holding `prvActOnBehalfOfAnotherUser`; and `prvActivateSynchronousWorkflow` for a real-time workflow or an action, `prvActivateBusinessRule` for a business rule. A background workflow needs neither |

System Administrator holds all of them. System Customizer holds the two activation privileges at Global through a named grant
(the depths are xrmverse's choice, from memory); Basic User holds neither. A solution import is exempt, as it is from every
2.14 rule.

## Export and "not representable"

`export` returns YAML in the canonical form: keys in the order of the reference above, defaults left out, `set` in XAML order,
descriptions recovered from the names, a child workflow by name when the name is unique among the definitions the caller can
read (else by id).
`--format xaml` returns the stored XAML unchanged. Before it answers, the exporter compiles the YAML again with the same
organization and requires the result to be equivalent to the stored process; if not, or if the process has a shape with no YAML
form, the export is a 400 "is not representable in YAML" listing every reason. It never answers with an approximation. A process
is not representable when it is a dialog, a business process flow or a modern flow, when it has an unsupported step, or when it
uses a value or condition the format has no word for (a `Cast` outside `setOutput`, a concatenation as a condition operand), or
when its `rank` (the run order of real-time workflows) is not 0: the YAML has no key for it.
A lookup literal whose record was renamed between edits is refused on export.

The round trip is pinned by tests: for a valid document, compiling it, reading the XAML back and writing YAML gives the
document again.

## What is refused and why

Compilation reports every error of a file together, with its line and column, and writes nothing. xrmverse fails explicitly
instead of approximating:

- YAML syntax errors, unknown or duplicate keys, a key of another kind;
- a step of the wrong kind for the category: a `wait` in a real-time workflow, a business rule or an action;
  `create`, `sendEmail`, `startChild`, `performAction`, `customActivity`, `stop`, `assign`, `setState` or `update` in a
  business rule; rule actions outside a business rule; `setOutput` outside an action;
- a table, column, lookup, choice value, argument, step id or child workflow that does not exist (or a child name that
  matches more than one definition); a column that is not valid for create or update;
- a literal that does not parse as its destination's type, or a choice value outside the options; a value kind the
  destination cannot take;
- `elseIf` or `else` without a `when` branch; a `stage` on a background workflow;
- a `businessRule` scope other than `entity`: form-scope rules run only in forms, which xrmverse does not have yet;
- anything the step library lacks, with the alternative named: recommendations, dialogs, templates, "send email from
  template". Use a webhook or a custom workflow activity instead;
- XAML that the 2.14 parser reads with any unsupported step (for example a custom activity when the workflow SDK is not
  installed), or that the workflow validator refuses.

A **raw XAML write through the Web API or the SDK** (a Create or Update of `workflow`) follows the same rules. A Create is
accepted as Draft (`type` 1, or absent), for a workflow, a business rule or an action; an Update of anything but the state or
the owner needs a Draft definition, and may not change the category, table, type or an action's unique name; the XAML must
parse, pass the validator and hold no unsupported step (400 listing them). The server-written columns
(`xrmverse_unsupportedsteps`, `activeworkflowid`, `parentworkflowid`, `sdkmessageid`, `xrmverse_componentxml`, `ismanaged`) are
refused, and so is every write of `processtrigger`. A public Update re-validates the XAML only when it touches definition
columns (the XAML, mode, triggers and stages), so an imported process with unsupported steps can still be renamed while Draft.

## Assumed shapes

xrmverse generates only XAML shapes that a real export has shown, or that its parser already reads. A construct no export
has confirmed is written in the designer's style, parses back to the step it stands for, and is **reported** instead of
refused: `apply` returns it in `assumedShapes` and the CLI prints `warning: <shape>`. A shape is the name of a catalog entry
of [Classic workflows](workflows.md#the-catalog) whose source is `assumed` or `learn` (`mxswa:AssignEntity`,
`EvaluateCondition:Equal`, `Shape:WaitTimeoutBranch`, ...) or one of the names the writer gives its own choices:

| Shape | What it is |
|---|---|
| `createdKey:id`, `sendEmail:assignToId` | an `id` on a `create` or `sendEmail` step |
| `sendEmail:noPersist` | a real-time email (the export shows a background one, with a `Persist`) |
| `stop:succeeded`, `stop:message` | a stop that succeeds, or that has a message |
| `literal:Integer`, `literal:Double`, `literal:Money`, `literal:DateTime` | a literal of that type |
| `argument:<type>`, `action:global` | an action argument other than decimal or entityReference; a global action |
| `customActivity:<type>` | a custom workflow activity step |
| `clearValue`, `showError:noField` | a rule's clear value; an error that is not on a field |
| `ruleCondition:noDescription`, `ruleCondition:elseIfDescription`, `ruleCondition:nested` | a rule condition without a description, with an else-if, or inside another |

Each is still to be compared with a real export; when one
confirms it, the shape is made an `export` entry of the catalog and stops being reported. A warning does not block anything.

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- **No `processtrigger` writes.** A business rule of Entity scope is written with `processtriggerscope` on the workflow row;
  the table's rows are written by solution import only.
- **No form-scope rules**, no recommendations, no dialogs or business process flows, and no `between` (use `all: [ge, le]`).
- **The YAML has no state.** Use `activate` and `deactivate`.
- `ApplyResult.action` describes the definition only: adding an existing process to a solution reports `unchanged`.
- In the CLI and the HTTP routes a name containing `/` must be given by its id (MCP accepts it).
- A lookup literal whose record was renamed between edits is refused on export.
- A rule action outside an `if` is refused: no real export shows one.
- A Draft action is accepted as a callee at design time (the runtime needs it activated). A Draft action created by a plain
  SDK Create has no message until it is activated.
- A core message without an SDK request class exposes no inputs to a `performAction` step, and a perform-action step's
  outputs are untyped.
- Custom activity step ids are exported as `activity<k>`.
- Unreadable process ids: `export`, `activate` and `deactivate` by an id the caller cannot read answer 403; a missing id is
  404.
- The shapes of [Assumed shapes](#assumed-shapes) are not yet confirmed in the real designer.

## See also

- [Workflow studio](workflow-studio.md): the same YAML on a flow canvas, with a code view
- [Classic workflows](workflows.md)
- [CLI](cli.md)
- [Admin API](admin-api.md#the-authoring-api)
- [MCP](mcp.md#workflow-tools)
- [Security](security.md#classic-workflows)
- [Solutions](solutions.md)
