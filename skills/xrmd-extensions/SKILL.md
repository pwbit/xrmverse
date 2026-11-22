---
name: xrmd-extensions
description: Add business logic to an xrmverse organization with the xrmd CLI - register plug-in assemblies, steps and images and read plug-in traces, register webhooks, create custom APIs, and author classic workflows, business rules and actions as YAML (`xrmd workflow apply/export`). Use this whenever someone wants to deploy or debug a Dataverse plug-in on xrmverse, call an external URL when a record changes, define a custom API or action, write or change a workflow or business rule as code, export an existing process to YAML, or find out why a plug-in failed, even if they say "register my dll", "hook a step on Update of account" or "automate this on create".
---

# xrmd: plug-ins, webhooks, custom APIs and workflows

xrmverse runs the Dataverse event pipeline: plug-in steps at stages 10/20/40, webhooks, custom APIs, classic workflows,
business rules and actions. Existing plug-in assemblies built against the Dataverse SDK run unchanged.

## Working with xrmd (all skills)

- Every command runs as the signed-in profile's user; registering logic needs System Administrator or equivalent.
- `-o json` for parseable output; `--profile <name>` selects the organization.
- `xrmd <group> <command> --help` is the truth when an option is unclear.
- Destructive commands need `--yes` without a terminal: pass it only after the user confirmed that action.
- Exit codes: 0 ok, 1 unexpected/declined, 2 usage (local validation, nothing sent), 3 authentication or privilege,
  4 not found (unknown type, webhook, API, process), 5 rejected by the server, 6 unreachable.
- Add `--solution <UniqueName>` when the logic belongs to a solution, so it travels with exports.

## Plug-ins

```bash
xrmd plugin register ./Acme.Plugins.dll [--solution AcmeCore]     # create, or update content/version and add new types
xrmd plugin list                                                  # assemblies, types, steps (and webhook steps)
xrmd plugin step add --type Acme.Plugins.ProjectPlugin --message Update --entity ac_project \
    --stage 20 [--mode sync] [--filtering ac_budget,ac_status] [--rank 1] [--config '{"x":1}'] [--solution AcmeCore]
xrmd plugin image add --step <stepid> --type pre --alias PreImage [--attributes ac_budget,ac_status]
xrmd plugin trace [--type Acme.Plugins.ProjectPlugin] [--take 20]
xrmd plugin step remove <stepid>                                  # destructive: removes its images too
```

Rules the server enforces (exit 5 otherwise):

| Option | Rule |
|---|---|
| `--stage` | 10 pre-validation, 20 pre-operation, 40 post-operation; nothing else |
| `--mode async` | only at stage 40 |
| `--filtering` | only for `Update`; the columns must exist |
| `--entity` | must belong to the message's filters; omit it only for messages that allow no table |
| images | `pre` is not available on Create, `post` not on Delete (as in Dataverse) |

`plugin register` reads the assembly locally (no SDK needed on the client). The server needs the Microsoft SDK installed
to run plug-ins (`xrmd sdk status`).

To debug: reproduce the operation, then `xrmd plugin trace --type <type>` (the plug-in's `ITracingService` output and the
exception), and `xrmd logs --level Warning` for pipeline-level errors.

## Webhooks

```bash
xrmd webhook create --name "Orders to ERP" --url https://erp.example.com/hooks/orders --auth key --value <key>
xrmd webhook create --name "Audit sink" --url https://sink.example.com/in --auth header --value X-Api-Key=<key> --value X-Env=prod
xrmd plugin step add --webhook "Orders to ERP" --message Create --entity salesorder --stage 40 --mode async
xrmd webhook list
xrmd webhook show "Orders to ERP"                                 # steps; the auth value is never shown
xrmd webhook update "Orders to ERP" --url https://erp2.example.com/hooks/orders
xrmd webhook delete "Orders to ERP"                               # destructive: endpoint, steps, images; queued jobs cancelled
```

- `--auth key` sends the key as the `code` query parameter; `header`/`query` take repeatable `K=V` values.
- The URL must be `http`/`https` on port 80 or 443, unless the server sets `Pipeline:WebhookAllowAnyPort`.
- `--config` is refused on a webhook step. Prefer `--mode async` at stage 40 so a slow receiver does not block saves; a
  synchronous webhook that fails makes the save fail.
- Never print the key or header values back to the user after creating the webhook.

## Custom APIs

```bash
xrmd customapi list
xrmd customapi show ac_Recalculate
xrmd customapi create --file recalc.json [--solution AcmeCore]
xrmd customapi delete ac_Recalculate                               # refused while a step is registered on it
```

The JSON file is the `customapis` Web API body plus `requestparameters` and `responseproperties` arrays (bodies without
`customapiid`) and an optional `"plugintype"` (full type name, resolved for you):

```json
{
  "uniquename": "ac_Recalculate", "name": "Recalculate", "displayname": "Recalculate",
  "bindingtype": 0, "isfunction": false, "allowedcustomprocessingsteptype": 0,
  "plugintype": "Acme.Plugins.RecalculatePlugin",
  "requestparameters":  [ { "uniquename": "ProjectId", "name": "ProjectId", "displayname": "Project", "type": 12, "isoptional": false } ],
  "responseproperties": [ { "uniquename": "Total", "name": "Total", "displayname": "Total", "type": 2 } ]
}
```

`bindingtype`: 0 global, 1 entity, 2 entity collection (then `boundentitylogicalname`). Parameter `type` values are the
Dataverse ones: 0 Boolean, 1 DateTime, 2 Decimal, 3 Entity, 4 EntityCollection, 5 EntityReference, 6 Float, 7 Integer,
8 Money, 9 Picklist, 10 String, 11 StringArray, 12 Guid. Register the plug-in assembly first so the type exists. If one
field is refused, the CLI deletes the API again and exits with the failure: fix the file and rerun.

## Workflows, business rules and actions as YAML

```bash
xrmd workflow list [--kind workflow|businessRule|action] [--entity ac_project]
xrmd workflow export "OnCreate" [--format yaml|xaml] [--file OnCreate.yaml]   # start from an existing process
xrmd workflow apply OnCreate.yaml --dry-run                                   # compile and compare, write nothing
xrmd workflow apply OnCreate.yaml [--solution AcmeCore] [--activate]
xrmd workflow activate "OnCreate"
xrmd workflow deactivate "OnCreate"
```

- Always `--dry-run` first. Local YAML errors exit 2 as `path:line:column: message`; compile errors exit 5 listing every
  error with its line.
- A new process is created as Draft; an existing one keeps its state. The file has no state: use `activate`/`deactivate`.
- `warning: <shape>` lines are XAML shapes not yet confirmed against a real Dataverse export. They do not block; mention
  them to the user if the process must also open in the Dataverse designer.
- A name containing `/` must be given by its id.
- Write files with [references/workflow-yaml.md](references/workflow-yaml.md): keys, steps, values, conditions and full
  examples. When unsure of the shape, export a similar process and edit it.

Refused on purpose (do not look for workarounds): form-scope business rules, dialogs, business process flows,
recommendations, email templates, `between` (use `all: [ge, le]`), and a `wait` outside a background workflow. Suggest a
webhook or a custom workflow activity for what the step library lacks.

## Choosing the mechanism

| Need | Use |
|---|---|
| Validate or change data in the save transaction | plug-in, stage 20 sync |
| React after save, can fail independently | plug-in or webhook, stage 40 async |
| Call an external system with no code in xrmverse | webhook |
| Expose an operation callable via Web API/SDK | custom API (plug-in at stage 30) |
| No-code logic maintained by consultants | workflow / business rule YAML |
