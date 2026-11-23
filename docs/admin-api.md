# Admin API

[← xrmverse documentation](../README.md#documentation)

## Introduction

The Admin API is an HTTP API for running an xrmverse installation: health, logs, organization databases, backups, schema
files, cascade deletes and migrations. It has no Dataverse equivalent (in Dataverse these tasks belong to the Power Platform
admin center); the `xrmd` CLI and the web admin call it for you.

| Item | Value |
|---|---|
| Base path | `/admin`, on the same host and port as the Web API |
| Authentication | A valid token **and** the System Administrator role |
| Error format | The same JSON error format as the Web API |
| Related route group | `/authoring/workflows` (see [The authoring API](#the-authoring-api)) |

## Access

| Caller | Answer |
|---|---|
| No token | 401 |
| Valid token without the System Administrator role | **403 with code `0x80040220`** ("The Admin API requires the System Administrator role.") |
| System Administrator | The route's answer |

`/admin/health` is admin-only too, which is why `xrmd server status` needs a profile. For an unauthenticated liveness probe
use `GET /healthz` (as the Docker health check does; see [Observability](observability.md)).

## Routes

### Health and logs

| Route | Purpose |
|---|---|
| `GET /admin/health` | `{status, ...}`, with `sdkMode` (`standard` or `none`) and `sdkVersion` (null on an [SDK-free server](microsoft-sdk.md#sdk-free-servers)); **503** when the status is `Unhealthy`, 200 otherwise |
| `GET /admin/doctor` | Self-checks (configuration, authentication, migrations, the file store, plug-ins) |
| `GET /admin/logs?level=&since=&take=` | Recent log entries from an in-memory ring buffer (`take` 1 to 1000) |

### Organizations and backups

| Route | Purpose |
|---|---|
| `GET /admin/orgs`, `POST /admin/orgs`, `DELETE /admin/orgs/{database}` | Organization databases |
| `POST /admin/orgs/{database}/backup`, `POST /admin/orgs/{database}/restore` | Backup and restore |

### Schema, solutions and security

| Route | Purpose |
|---|---|
| `GET /admin/schema`, `POST /admin/schema/apply?dryRun=` | Export and apply a schema file. **`dryRun` defaults to true** (pass `dryRun=false` to write); rejections are a 400 with the plan |
| `DELETE /admin/solutions/{uniquename}?cascade=true` | Cascade delete of a solution (`cascade=true` is required) |
| `GET /admin/hierarchy-security`, `PUT /admin/hierarchy-security` | The manager hierarchy security model: `{enabled, depth, excludedTables[]}`. `PUT` takes any of the three: the parts given replace the current ones (`excludedTables` replaces the whole list). An unknown table or a depth outside 1 to 100 is a 400 and nothing is written. See [Security](security.md#hierarchy-security) |

### Migration from Dataverse

See [Migrating from Dataverse](migration.md) for the whole process.

| Route | Purpose |
|---|---|
| `POST /admin/migrate/packages`, `PUT /admin/migrate/packages/{id}/chunks/{n}`, `POST /admin/migrate/packages/{id}/complete`, `GET`/`DELETE /admin/migrate/packages/{id}` | Chunked upload of a migration package (chunks of at most 8 MiB, 413 beyond; `complete` takes `{chunks, sha256}`). See [Migrating from Dataverse](migration.md#import-on-the-server) |
| `POST /admin/migrate/runs`, `GET /admin/migrate/runs`, `GET /admin/migrate/runs/{id}` | Start (`{packageId` or `serverPath, passphrase, users, options}`, 202; options `strict`, `strictLookups`, `baseCurrencyMap`, `keepPackage`, `verifySample`), list (latest 20) and read migration runs |
| `GET /admin/migrate/runs/{id}/report` | The run's report: tables, issues, nulled lookups, users by action, phase timings. See [Migrating from Dataverse](migration.md#report) |
| `POST /admin/migrate/runs/{id}/cancel`, `/resume`, `/finalize` | Cancel a queued (200) or running (202) run; resume a failed or cancelled run with `{passphrase}` (202); finalize a run that will not finalize on its own, `{keepPackage}` (200). A state that does not allow it is a 409 |
| `POST /admin/migrate/runs/{id}/activate` | Switch back on the business logic a finalized run left off: `{all: true}` or `{ids: [...]}`, with `dryRun`; 409 until the run is finalized. See [Migrating from Dataverse](migration.md#xrmd-migrate-activate) |

## The authoring API

Workflow authoring as code has its own route group, `/authoring/workflows`, beside the Admin API on the same host and port
(see [Workflow authoring as code](workflow-authoring.md)).

| Item | Rule |
|---|---|
| Authentication | A valid token (401 without one), **no role required** |
| Authorization | Every operation is a request as the caller: the Dataverse privileges on `workflow` decide ([Security](security.md#classic-workflows)) |
| Errors | The Web API error envelope |

`/admin` stays for System Administrators.

| Route | Purpose |
|---|---|
| `POST /authoring/workflows/apply?dryRun=&solution=&activate=` | Body: the YAML (`text/yaml`, at most 1 MiB). **`dryRun` defaults to true**, as for `POST /admin/schema/apply`. 200 with `{ id, name, action, state, stateChanged, assumedShapes, errors }`; 400 with the same body when `errors` is not empty (nothing is written) |
| `GET /authoring/workflows?kind=&entity=` | The list: `id`, `name`, `kind`, `entity`, `mode`, `state`, `unsupportedSteps` |
| `GET /authoring/workflows/{nameOrId}?format=yaml\|xaml` | The process as YAML (`text/yaml`, the default) or its XAML (`text/xml`); a process with no YAML form is a 400 with the reasons |
| `POST /authoring/workflows/{nameOrId}/activate`, `/deactivate` | Change the state; the response is the summary after |

### Naming a process

- A name that contains `/` must be given by its id.
- A name that matches several processes is a 400 listing their ids.
- An unknown name or id is 404.

## See also

- [CLI xrmd](cli.md), which calls these routes
- [Multiple organizations](multiple-organizations.md)
- [Observability](observability.md)
