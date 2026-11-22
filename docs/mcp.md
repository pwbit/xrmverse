# MCP

[← xrmverse documentation](../README.md#documentation)

## Introduction

The server exposes an MCP endpoint at `/mcp` so that AI agents and MCP clients can read and change an organization's data
and metadata. It uses **stateless streamable HTTP** (`POST` only, no sessions, no SSE) and requires the same bearer token
as the Web API. Every tool call runs through the Core as the authenticated user, with that user's privileges.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Endpoint and tools | `/mcp` on every organization, stateless streamable HTTP | Differs: the tool set is xrmverse's own, not a copy of a Dataverse one |
| Identity | the Web API bearer token; tools run as that user | Same security as every other surface |
| Business knowledge | descriptions, synonyms and glossary terms in `describe_table` | Same tables as Dataverse's glossary and synonyms |
| Destructive tools | dry run by default for `apply_schema`, `apply_workflow`, `migrate_activate`; no destructive solution tool | Extra safety |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Connect a client over HTTP | `POST /mcp` with a bearer token | [Connecting a client](#connecting-a-client) |
| Connect a stdio-only client | the `xrmd mcp` bridge | [Connecting a client](#connecting-a-client) |
| Read and write records | `query`, `create_record`, `update_record`, `delete_record` | [The tools](#the-tools) |
| Plan a schema change | `apply_schema` (dry run by default) | [The tools](#the-tools) |
| Give agents business meaning | glossary and synonyms | [Knowledge for agents](#knowledge-for-agents) |
| Author classic workflows | `apply_workflow` and the other workflow tools | [Workflow tools](#workflow-tools) |
| Import from Dataverse | the `migrate_*` tools | [Migration tools](#migration-tools) |

## Connecting a client

A client that speaks streamable HTTP calls `POST /mcp` with the same bearer token it would send to the Web API.

Clients that only speak stdio use the bridge `xrmd mcp`, which forwards tools and resources to `/mcp` with the
profile's token (stdout is the MCP channel; nothing else is written to it). A desktop MCP client configuration:

```json
{
  "mcpServers": {
    "xrmverse": {
      "command": "xrmd",
      "args": ["mcp", "--profile", "default"]
    }
  }
}
```

The profile must already exist (`xrmd auth login`); with `dev` mode the client's environment must provide the signing
key variable.

## The tools

| Tool | What it does |
|---|---|
| `list_tables` | lists the tables |
| `describe_table` | describes a table; secured columns carry `"secured": true` (see also [Knowledge for agents](#knowledge-for-agents)) |
| `query` | FetchXML or OData, paged |
| `create_record`, `update_record`, `delete_record` | write records |
| `list_solutions`, `export_solution` | list and export solutions |
| `get_plugin_traces`, `get_async_jobs` | read plug-in traces and system jobs |
| `get_audit_history` | the audit history of one record, newest first; read-only, needs the audit privileges |
| `list_custom_apis` | read-only, optional `name` (below) |
| `list_glossary_terms` | read-only (see [Knowledge for agents](#knowledge-for-agents)) |
| `apply_schema` | plans or applies a schema change (below) |

Resources: `xrmverse://tables/{name}` and `xrmverse://schema{?publisher}` (the latter is admin-only).

### `apply_schema`

`apply_schema` needs the System Administrator role and **`dryRun` defaults to true**: it only plans unless the caller
passes `dryRun: false`; with `dryRun: false`, rejections are a tool error (`isError`) carrying the plan.

### `list_custom_apis`

`list_custom_apis` returns `{ customApis: [...] }`, each entry with `uniqueName`, `name`, `displayName`, `description`, `binding` (`global`, `entity:<table>` or `collection:<table>`), `kind` (`action` or `function`), `private`, `pluginType`, `parameters` (`uniqueName`, `type`, `optional`, `table`) and `properties` (`uniqueName`, `type`, `table`). It reads as the caller and pages through every result. The caller needs read access to custom APIs and to plug-in types: without the plug-in type privilege the tool fails with the server's refusal naming it. The tool is read-only: create custom APIs with the CLI, the SDK or the Web API (see [Custom APIs](custom-apis.md#authoring)).

## Knowledge for agents

The tables of [Glossary and synonyms](glossary-and-synonyms.md) give agents the business meaning of a table. Everything is read as the caller, so depth, sharing and field security apply.

- `describe_table` and the resource `xrmverse://tables/{name}` keep their fields and add: `description` (the table's description), per column `description` and `synonyms` (a list of strings), and `glossary` (a list of `{ term, description? }` for the table). Each is left out when empty.
- `list_glossary_terms` (optional `table`, the logical name of a table, and `source`, the name of a knowledge source; read-only, idempotent) returns `{ terms: [{ term, description?, sources }] }`, ordered by term without regard to case. Terms are grouped by term and description, so a term with two descriptions appears twice. The list is not paged.

Which rows count:

- Only active rows (`statecode` 0).
- A term or synonym counts only when its knowledge source is active and the caller can read it.
- A caller who cannot read the knowledge tables gets empty results, not an error.
- The synonyms string is split on `,` and `;` and trimmed; empty parts are dropped.
- In `describe_table`, synonyms rows that name a column the table does not have are skipped. `list_glossary_terms` with `table` does not check that the table exists: an unknown name just matches no source.

## Workflow tools

Four tools author and manage classic workflows, business rules and actions written as YAML (see
[Workflow authoring as code](workflow-authoring.md)). They run as the caller with **no role requirement**: the Dataverse
privileges on `workflow` decide, so a process author who is not an administrator can use them.

- `apply_workflow` (`yaml`, optional `solution`, `activate`, `dryRun`) applies a document: it creates the process, or updates the
  one with the same identity. **`dryRun` defaults to true**: it only reports what would change; pass `dryRun: false` to write.
  The result is `{ id, name, action (created | updated | unchanged), state (draft | activated), stateChanged, assumedShapes,
  errors }`. A refused document is a tool error (`isError`) whose text holds the result with every error and its line and column;
  nothing is written then. Every apply, a dry run too, needs `prvReadWorkflow`.
- `export_workflow` (`nameOrId`, `format` yaml or xaml) returns `{ format, text }`. A process with no YAML form is refused with
  the reasons.
- `list_workflows` (optional `kind`, `entity`) returns `{ workflows: [...] }` with `id`, `name`, `kind`, `entity`, `mode`, `state`
  and `unsupportedSteps`.
- `set_workflow_state` (`nameOrId`, `activated`) activates or deactivates a process and returns its summary; it needs the
  activation privileges ([Security](security.md#classic-workflows)).

A name that matches several processes is a 400 that lists their ids.

## Migration tools

Seven tools drive an import from [Dataverse](migration.md#mcp-tools). They need the **System Administrator** role and answer
the JSON of their Admin API route; the package passphrase is an input only and never appears in an answer.

- `migrate_inspect_package` (`packageId`, `passphrase`; read-only) shows what an uploaded package holds.
- `migrate_start_import` (`passphrase`, `packageId` or `serverPath`, optional `users` and `options` objects; destructive)
  starts a run and returns `{ runId, asyncOperationId }`.
- `migrate_get_status` and `migrate_get_report` (`runId`; read-only) read the run.
- `migrate_cancel` (`runId`) and `migrate_finalize` (`runId`, optional `keepPackage`) are destructive.
- `migrate_activate` (`runId`, `all` or `ids`) switches back on the business logic a finalized run left off.
  **`dryRun` defaults to true**: it only lists what would change; pass `dryRun: false` to switch on.

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- The endpoint is stateless: `POST` only, no sessions and no SSE.
- `list_custom_apis` is read-only: create custom APIs with the CLI, the SDK or the Web API.
- `list_glossary_terms` is not paged.
- There is no destructive solution tool.

## See also

- [CLI](cli.md)
- [Admin API](admin-api.md)
- [Glossary and synonyms](glossary-and-synonyms.md)
- [Workflow authoring as code](workflow-authoring.md)
- [Migration from Dataverse](migration.md#mcp-tools)
- [Security](security.md)
