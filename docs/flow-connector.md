# Cloud flow connector

[← xrmverse documentation](../README.md#documentation)

## Introduction

xrmverse ships its own **custom connector** so that cloud flows (and apps that accept custom connectors) can read and write the
data of an organization. The connector is a small package of files: an OpenAPI 2.0 definition, the connection settings and an icon.
You import it once per organization into your Power Platform environment, create a connection that signs in with Microsoft Entra ID,
and the flow designer then offers six actions and one trigger on any table of the organization, with the table picked from a list and its columns
shown as fields. Custom tables and custom columns appear too, with no re-import.

The connector is not a copy of the connector Microsoft ships for Dataverse. Its names, actions and behavior are xrmverse's own, and
the Dataverse connector cannot be pointed at another server.

Row actions call the same Web API as every other client. A row written by a flow therefore runs plug-ins, workflows,
auditing and security exactly as any other write, and the flow acts as the signed-in user of the connection.

The trigger **When a row changes** starts a flow when a row of a table is created, updated or deleted: the server calls the flow
after the change is saved (see [The trigger](#the-trigger)).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Connector | Own custom connector named `xrmverse`, imported once per organization | **Differs**: Dataverse has a ready-made connector in the gallery; xrmverse's is not in the public gallery |
| Actions | List rows, get a row, add a row, update a row, delete a row, run a custom API | **Narrower**: no bound actions, batch, relevance search, changesets, associate/disassociate, file and image transfer |
| Trigger | When a row changes: create, update or delete of a row of one table, delivered by the server calling the flow | **Narrower**: no trigger on elastic tables, no scope or organization/business-unit filter, no filter expression, no "run as" choice (the flow acts as the user who made the flow) |
| Tables and columns | Picked from the organization at design time, including custom ones | Same idea |
| Sign-in | OAuth 2.0 with Microsoft Entra ID, as the user of the connection | Same security model |
| Local identity provider | Not supported | **Narrower**: the organization needs Entra ID sign-in |
| Reachability | The organization must be reachable over public HTTPS | **Differs**: Dataverse is a hosted service |
| On-premises data gateway | Not supported | **Narrower** |
| Writes | Go through the Web API pipeline (plug-ins, workflows, auditing, security) | Same |
| Errors | The Web API error body is passed through; what the connector cannot do fails with an explicit error | Same behavior |

Everything outside these rules fails with an explicit error; nothing is silently ignored. The full list is in [Known limits](#known-limits).

### Before you start

- An organization with **Microsoft Entra ID** sign-in (`--auth entra`), reachable from the Internet over **https** (for example
  `https://acme.example.com`). See [Install](install.md#4-entra-app-registrations).
- A Power Platform environment where you may create custom connectors, and Microsoft Entra rights to register an application.
- The `xrmd` CLI, signed in to the organization with a profile that uses its https URL (see [CLI xrmd](cli.md)).
- The flow's user must exist as a user of the organization with the privileges for the tables the flow uses. A token for an unknown
  user gets the usual 401 or 403; users are not created automatically.

## Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Register the client app the connector signs in with | A Microsoft Entra app registration with a secret | [Setting up](#setting-up) |
| Write the package for your organization | `xrmd connector export` | [Setting up](#setting-up) |
| Import the package | `pac connector create` or the maker portal | [Setting up](#setting-up) |
| Sign in from a flow | Create a connection | [Setting up](#setting-up) |
| Read, add, change or delete rows | The row actions | [Actions](#actions) |
| Start a flow when a row changes | The *When a row changes* trigger | [The trigger](#the-trigger) |
| Run a custom API | The *Run a custom API* action | [Actions](#actions) |
| See which columns can be written | The column types table | [Column types](#column-types) |

## Setting up

Do these steps once per organization. One import serves every flow of that environment.

### 1. Register a client app in Microsoft Entra ID

The connector signs in through an app registration of your own tenant, separate from the organization's API app registration.

1. Register an application (web platform). Its **application (client) ID** goes to `xrmd connector export`.
2. Create a **client secret**. You give it at import time; it is never written into the package.
3. Give the app the delegated permission of the organization's API app registration (the one described in
   [Install](install.md#4-entra-app-registrations)), and grant consent as your tenant requires.
4. After the import, add the redirect URL that the connector shows to the app registration (see step 3 below).

### 2. Write the package with `xrmd connector export`

```bash
xrmd connector export --out ./xrmverse-connector --client-id <client app id>
```

The command reads the host and the resource of the signed-in profile and writes `apiDefinition.swagger.json`,
`apiProperties.json` and `icon.png` to the folder, with the host, the client id and the resource filled in. It makes no call to the
server.

| Option | Meaning |
|---|---|
| `--out` | Folder to write the package to (required) |
| `--client-id` | Application (client) ID of the client app of step 1 (required) |
| `--resource` | Application ID URI of the organization's API app registration, the audience of its tokens. Default: the URL of the profile. Give it when the audience is an `api://` URI |
| `--profile` | Profile of the organization (global option, see [CLI xrmd](cli.md)) |
| `--force` | Overwrite files that already exist in `--out` |

The command refuses a profile of a Local identity provider organization, a profile in dev mode, and a URL that is not https, each
with a message that says why. The same files, as templates with `{{HOST}}`, `{{CLIENT_ID}}` and `{{RESOURCE_URI}}` placeholders, are
in the repository under `connectors/xrmverse/`.

### 3. Import the package

With the Power Platform CLI:

```bash
pac connector create --environment <environment id> \
  --api-definition-file ./xrmverse-connector/apiDefinition.swagger.json \
  --api-properties-file ./xrmverse-connector/apiProperties.json \
  --icon-file ./xrmverse-connector/icon.png \
  --secret <client secret>
```

Or in the maker portal: **Custom connectors**, **Import an OpenAPI file**, then choose `apiDefinition.swagger.json`. On the security
step, enter the client secret.

After the import, open the connector's security settings, copy the **redirect URL** and add it to the app registration of step 1.
There is one connector import per organization: the host and the OAuth resource are fixed in it.

### 4. Create a connection

In a flow, add an action of the connector and sign in. The connection signs in as you; the flow then reads and writes with your
privileges. Another maker creates their own connection and acts with theirs.

## Actions

| Action | Request it sends | Notes |
|---|---|---|
| List rows | `GET /api/data/v9.2/{table}` | Columns to return, filter, sort, expand, row count, or a FetchXML query; the pages follow `@odata.nextLink` |
| Get a row by ID | `GET /api/data/v9.2/{table}({id})` | Columns to return and expand |
| Add a new row | `POST /api/data/v9.2/{table}` | Answers with the saved row (`Prefer: return=representation`) |
| Update a row | `PATCH /api/data/v9.2/{table}({id})` | Sends `If-Match: *`, so a row that does not exist fails instead of being created |
| Delete a row | `DELETE /api/data/v9.2/{table}({id})` | |
| Run a custom API | `POST /api/data/v9.2/Microsoft.Dynamics.CRM.{customApi}` | Only a custom API that is not bound to a table |

The table is picked from the tables the signed-in user may read; the custom API from the unbound custom APIs the user may run. You
can also give the table by its set name, for example `accounts`. Columns of a table the user may not read are not offered
(field-level security applies, see [Field-level security](field-security.md)).

Four hidden operations under `/api/connector/v1` feed the designer: the table list, the columns of a table, the custom API list and
the parameters or response of a custom API. They only describe metadata and respect the caller's privileges.

### Lookups

A lookup is written as the Web API writes it: a property named `<navigation>@odata.bind` with the value `/<entityset>(<id>)`, for
example `parentcustomerid_account@odata.bind` = `/accounts(<guid>)`. The designer shows one field per target table. A **Customer**
column shows two (account and contact); fill in one. There is no translation layer: the flow's body is the Web API body. When
reading, a lookup is the plain value `_<column>_value` (the row's ID).

### Errors

The Web API error body (`error.code`, `error.message`) is passed through to the flow, so an error raised by a plug-in or a business
rule shows up as the failure of the action with its own message.

## The trigger

**When a row changes** starts a flow when a row of one table is created, updated or deleted. When you turn the flow on, the
connector registers a subscription with the server and gives it the address of the flow; when you turn the flow off or delete it, the
subscription is removed. The server calls the flow once per change, after the change is saved.

| Setting | Meaning |
|---|---|
| Table to watch | The table to watch, picked from the tables you may read |
| Changes | *Created*, *Updated*, *Deleted* or a combination of them (seven choices, sent as `create`, `update`, `delete` joined by commas) |
| Columns to watch (updates) (advanced) | For updates: the flow starts only when one of these columns changed; empty means any column |

### Who may subscribe

A subscription needs two things: the privilege to create flow subscriptions (`prvCreatexrmverse_FlowSubscription`, which the Basic
User role has at user depth by default) and read access to the table. Elastic tables and the subscription table itself are refused
with a 400 error. A subscription belongs to the user of the connection that registered it; it can be removed by its owner or by a
System Administrator. At most `Connector:MaxSubscriptionsPerUser` subscriptions (default 50) exist per user; one more fails with
a 429 error (code `0x80090910`).

Subscriptions cannot be created, changed, assigned or deleted through `/api/data` by anyone (403). A System Administrator can read
them at `/api/data/v9.2/xrmverse_flowsubscriptions`; the callback URL is never readable there. It is stored apart from the row and,
as for webhook secrets, not encrypted yet (see [Known limits](#known-limits)).

### Which changes start the flow

| Change | When |
|---|---|
| `create` | A row is created |
| `update` | A row is updated, its state or status is set, or it is assigned (an assign counts as an update of `ownerid`) |
| `delete` | A row is deleted |

Columns to watch apply to updates only. Bulk messages notify once per row, and a cascade delete notifies for each row it removes.

The server reads the row inside the transaction of the write, **as the owner of the subscription**: at the end of the operation
for a create or an update, and for a delete just before the row is removed (after the pre-operation plug-ins):

- field-level security applies to the columns the flow receives;
- a disabled owner, or an owner who cannot read the row at the change, gets no notification; for a delete this is the owner's
  access to the row being deleted (privilege depth and sharing), as for any other change;
- a write that is rolled back sends nothing, a delete that fails after the row was read included.

Each change has its own background job (system job type 99014, *Flow notification: {table} {change}*). A job is deleted when the
flow accepts the notification; a failed job stays for diagnosis.

### What the flow receives

```json
{
  "change": "update",
  "table": "account",
  "id": "5b0c9f0e-3a3e-4f52-9a4f-0d3b1d3c6a11",
  "row": { "accountid": "5b0c9f0e-3a3e-4f52-9a4f-0d3b1d3c6a11", "name": "Contoso" }
}
```

`change` is `create`, `update` or `delete`; `table` is the logical name; `id` is the row ID. `row` is the row as
`GET /api/data/v9.2/{set}({id})` returns it to the owner, without `@odata.context` and `@odata.etag`; it is absent for a delete.
The designer shows the columns of the picked table as dynamic content.

### The address of the flow

The server calls only addresses that pass these rules, at subscribe time, again at connect time (private and loopback addresses are
refused again) and again when the stored address is used to send:

- `https` on port 443, no IP address in place of a host name, no user information, at most 2048 characters;
- a host that matches `Connector:CallbackHosts`. The default is `*.logic.azure.com` and `*.environment.api.powerplatform.com`
  (the hosts of flow addresses in the public cloud); `*.` matches one or more labels. A list you configure replaces the default,
  it does not add to it.

At connect time the resolved address must not be loopback, private (10/8, 172.16/12, 192.168/16, fc00::/7), link-local
(169.254/16, fe80::/10), site-local (fec0::/10), shared (100.64/10), "this network" (0/8), multicast or reserved (224/4, 240/4,
ff00::/8), nor an IPv6 address that carries one of these IPv4 addresses (IPv4-mapped, NAT64 `64:ff9b::/96`, 6to4 `2002::/16`).

### Delivery and retries

Delivery is asynchronous, after the write has committed, and follows the [webhook retry rules](webhooks.md#retry-rules):

| Answer | Treatment |
|---|---|
| 502, 503, 504 | One immediate extra attempt, then retryable |
| Any other 5xx, a timeout, a connection or TLS error | Retryable |
| 3xx, 4xx | Final |
| 404, 410 | The flow no longer exists: the subscription is ended (its row is deleted) |

A subscription whose stored address is missing is ended too, and its job completes with a message that says so.

A retryable failure is tried again up to `Pipeline:AsyncMaxRetries` times, waiting `Pipeline:AsyncRetryDelay` times 2 to the power
(n - 1) before retry n (the power stops growing at 2^20). Each attempt waits at most `Pipeline:WebhookTimeout`.

### Settings

| Key | Default | Meaning |
|---|---|---|
| `Connector:CallbackHosts` | `*.logic.azure.com`, `*.environment.api.powerplatform.com` | Host patterns a flow address must match; a configured list replaces the default |
| `Connector:MaxSubscriptionsPerUser` | `50` | Subscriptions one user may hold |
| `Connector:AllowPrivateCallbacks` | `false` | For tests and development only: allows `http`, any port, loopback and private addresses |

See [Configuration](configuration.md#cloud-flow-connector).

## Column types

What the add and update schemas offer, and what they leave out:

| Column type | In the flow designer | Writable |
|---|---|---|
| Text, multi-line text | Text | Yes |
| Whole number | Integer (32 bit) | Yes |
| Big integer | Integer (64 bit) | Yes |
| Decimal, floating point, currency | Number | Yes |
| Yes/no | Boolean | Yes |
| Date and time | Date-time; date only for a date-only column | Yes |
| Choice | Integer with the option values and their labels | Yes |
| Multiple-choice | Text, with the values and labels in the description; the Web API format is a comma-separated list of values | Yes |
| Status and status reason | Integer with option labels | Update only (the server sets them on create) |
| Lookup, Customer | `<navigation>@odata.bind` properties, one per target | Yes |
| Unique identifier | Text, as a read-only value | No |
| File, image | Not offered | No |
| Party list (activity parties) | Not offered | No |

Labels come from the user's interface language. A required column of the table (system or application required) is required in the
add schema, except the ones the server fills in (owner, state, status). Columns marked as not valid for create or update, and columns
the user may not create or update because of field-level security, are left out of the matching schema.

## Known limits

| Limit | Details |
|---|---|
| Trigger on elastic tables | Not available: a subscription on an elastic table is refused |
| Callback URL storage | The address of the flow is stored apart from the subscription row but not encrypted, as webhook secrets are (issue #86) |
| Outbound proxy | Calls to the flow do not go through an outbound HTTP proxy |
| Several server instances | The subscription list is cached in each server process: with several instances on one organization, a change to a subscription is seen by the others only after a restart |
| Trigger filters | One table, a change type and update columns; no filter expression and no scope |
| Local identity provider | Organizations in Local identity provider mode are not supported; `xrmd connector export` refuses them |
| Public HTTPS | The organization must be reachable over public https from the Power Platform. You may restrict inbound traffic to the Power Platform address ranges; xrmverse does not enforce it |
| On-premises data gateway | Not supported: a gateway only carries calls from the cloud to the server |
| Public connector gallery | The connector is imported by the customer; it is not certified or published in the public gallery |
| One import per organization | The host and the OAuth resource are fixed in the import |
| FetchXML paging | The result of a FetchXML query is not paged by the connector; use a `top` count or an OData query for large sets |
| Columns not offered for writing | File, image and party list columns, and read-only columns |
| Custom APIs | Only unbound custom APIs; a bound one, a function or a private one is refused with an explicit error. Entity-valued parameters are free objects in the Web API shape |
| Not covered | Bound actions, batch, relevance search, changesets, associate and disassociate as separate actions, environment variables |
| Not verified | A live run against a real Power Platform tenant has not been done yet; the connector and the trigger are tested against the server only |

## See also

- [CLI xrmd](cli.md)
- [Installing xrmverse](install.md)
- [Custom APIs](custom-apis.md)
- [Field-level security](field-security.md)
- [Webhooks](webhooks.md): the other way to call out of xrmverse, and the retry rules the trigger follows
- [Configuration](configuration.md#cloud-flow-connector)
- [Known issues and limitations](known-issues.md)
