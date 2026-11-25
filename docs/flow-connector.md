# Cloud flow connector

[← xrmverse documentation](../README.md#documentation)

## Introduction

xrmverse ships its own **custom connector** so that cloud flows (and apps that accept custom connectors) can read and write the
data of an organization. The connector is a small package of files: an OpenAPI 2.0 definition, the connection settings and an icon.
You import it once per organization into your Power Platform environment, create a connection that signs in with Microsoft Entra ID,
and the flow designer then offers six actions on any table of the organization, with the table picked from a list and its columns
shown as fields. Custom tables and custom columns appear too, with no re-import.

The connector is not a copy of the connector Microsoft ships for Dataverse. Its names, actions and behavior are xrmverse's own, and
the Dataverse connector cannot be pointed at another server.

Row actions call the same Web API as every other client. A row written by a flow therefore runs plug-ins, workflows,
auditing and security exactly as any other write, and the flow acts as the signed-in user of the connection.

**Only the actions exist today.** The trigger ("when a row is added, modified or deleted") is not available yet; it is planned as X.12b
(see [Known limits](#known-limits)).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Connector | Own custom connector named `xrmverse`, imported once per organization | **Differs**: Dataverse has a ready-made connector in the gallery; xrmverse's is not in the public gallery |
| Actions | List rows, get a row, add a row, update a row, delete a row, run a custom API | **Narrower**: no bound actions, batch, relevance search, changesets, associate/disassociate, file and image transfer |
| Trigger | Not yet available (X.12b) | **Narrower** until X.12b |
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
| List rows | `GET /api/data/v9.2/{table}` | Select columns, filter, sort, expand, row count, or a FetchXML query; the pages follow `@odata.nextLink` |
| Get a row by ID | `GET /api/data/v9.2/{table}({id})` | Select columns and expand |
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
| Trigger | Not available yet: only actions. Planned as X.12b, which delivers "when a row is added, modified or deleted" by calling the flow from the server |
| Local identity provider | Organizations in Local identity provider mode are not supported; `xrmd connector export` refuses them |
| Public HTTPS | The organization must be reachable over public https from the Power Platform. You may restrict inbound traffic to the Power Platform address ranges; xrmverse does not enforce it |
| On-premises data gateway | Not supported: a gateway only carries calls from the cloud to the server |
| Public connector gallery | The connector is imported by the customer; it is not certified or published in the public gallery |
| One import per organization | The host and the OAuth resource are fixed in the import |
| FetchXML paging | The result of a FetchXML query is not paged by the connector; use a `top` count or an OData query for large sets |
| Columns not offered for writing | File, image and party list columns, and read-only columns |
| Custom APIs | Only unbound custom APIs; a bound one, a function or a private one is refused with an explicit error. Entity-valued parameters are free objects in the Web API shape |
| Not covered | Bound actions, batch, relevance search, changesets, associate and disassociate as separate actions, environment variables |
| Not verified | A live run against a real Power Platform tenant has not been done yet; the connector is tested against the server only |

## See also

- [CLI xrmd](cli.md)
- [Installing xrmverse](install.md)
- [Custom APIs](custom-apis.md)
- [Field-level security](field-security.md)
- [Webhooks](webhooks.md): the other way to call out of xrmverse
- [Known issues and limitations](known-issues.md)
