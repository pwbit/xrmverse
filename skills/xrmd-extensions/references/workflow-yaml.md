# Workflow YAML reference (`xrmd workflow apply`)

One file holds one process: a classic workflow, a business rule or an action. The server compiles it to the designer XAML
and stores it in the `workflow` row. Reading is strict: an unknown key, a key of another kind or a duplicate key is an error
with line and column; nothing is coerced.

## Keys

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

## Triggers, records and values


`trigger.create`, `trigger.update` and `trigger.delete` are the events a workflow starts on. `columns` is the update trigger's
column list in the order given; the column `ownerid` means "record is assigned" and `statecode` means "status changes". A
`stage` (`pre` or `post`) is accepted on a real-time workflow only; the default is `post` for create and update, and `pre`
for delete, the only stage a delete takes. A workflow with no trigger and `onDemand: true` runs when a user starts it.

### Steps

Each list item is a mapping with exactly one step key; every step takes an optional `description`.
Activity tables (`task`, `email` and the like) use `activityid` as their key, so `{ record: primary }` and `{ created: ... }` work on them as on any table. A record is `primary`, `{ related: <lookup column of the primary table> }`
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

## Examples

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

## Refused on purpose

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

