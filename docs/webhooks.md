# Webhooks

[← xrmverse documentation](../README.md#documentation)

## Introduction

A webhook lets xrmverse call your own HTTP service when something happens in an organization. You register an **endpoint**
(a `serviceendpoint` row with contract 8, *Webhook*) and one or more **processing steps** on it. When the message of a step runs
(for example Create of `account`), xrmverse posts the execution context, as JSON, to your receiver.

Webhooks are the simplest way to extend xrmverse without writing a .NET Framework plug-in: the receiver can be written in any
language, needs no Microsoft SDK and no plug-in assembly. For in-process extensions see [Plug-ins](plugins.md).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Endpoint contracts | Webhook (contract 8) only | **Narrower**: Azure Service Bus queue and topic and Event Hub contracts are refused (#84) |
| Authentication types | Webhook Key, Http Header, Http Query String | Same three types |
| Request body | `RemoteExecutionContext` as JSON | Same shape (serialized from the SDK types) |
| Steps (stage, mode, filters, images) | Same rules as plug-in steps | Same |
| Solutions | Endpoints and their steps are registered per organization | **Narrower**: they cannot be part of a solution (#85) |
| Stored secret | Stored **unencrypted** in the organization database, never returned by any API | **Differs**: protect the database and its backups (#86) |
| `x-ms-dynamics-organization` header | The organization name | **Differs**: Dataverse sends its domain name |
| Port of the receiver | 80 or 443, any port only when `Pipeline:WebhookAllowAnyPort` is on | Extra setting for development and on-premises receivers |

Everything outside these rules fails with an explicit error; nothing is silently ignored. The full list is in [Known limits](#known-limits).

### Before you start

- The Microsoft SDK must be installed on the server, as for plug-ins: the request body is produced from the SDK types
  (see [Installing the Microsoft SDK](microsoft-sdk.md)).
- The receiver must be reachable from the xrmverse server over `http` or `https` on port 80 or 443 (see
  [Configuration](#configuration) to allow other ports).
- You need the privileges to create `serviceendpoint` and `sdkmessageprocessingstep` rows.

## Where you manage webhooks

| Tool | What you can do |
|---|---|
| CLI `xrmd webhook` and `xrmd plugin step` | Create, list, show, update and delete endpoints; add steps (see [CLI xrmd](cli.md)) |
| Web API | Create and update `serviceendpoints` and `sdkmessageprocessingsteps` rows |
| SDK (SOAP) and the Plug-in Registration Tool | "Register New WebHook" and step registration, as with Dataverse |
| Web admin | **Not available**: the Plug-ins page lists plug-in steps only |

## Registering a webhook

Registering is always two operations: create the endpoint, then add a step that points to it.

### With the CLI

```bash
xrmd webhook create --name new_hook --url https://hooks.example.test/in --auth header --value X-Api-Key=s3cr3t
xrmd plugin step add --webhook new_hook --message Create --entity account --stage 40 --mode async
```

Options of `xrmd webhook create`:

| Option | Meaning |
|---|---|
| `--name` | Name of the endpoint |
| `--url` | Address of the receiver |
| `--auth key` | One `--value`, sent as the query parameter `code` |
| `--auth header` | One or more `--value K=V`, sent as HTTP headers |
| `--auth query` | One or more `--value K=V`, sent as query parameters |

The CLI builds the settings XML for you. On `xrmd plugin step add`, use `--webhook <name>` instead of `--type`: exactly one of
the two is required, and `--config` is refused together with `--webhook`.

### With the Web API

```http
POST /api/data/v9.2/serviceendpoints
{ "name": "new_hook", "contract": 8, "authtype": 5, "url": "https://hooks.example.test/in",
  "authvalue": "<settings><setting name=\"X-Api-Key\" value=\"s3cr3t\" /></settings>" }

POST /api/data/v9.2/sdkmessageprocessingsteps
{ "name": "new_hook: Create of account", "stage": 40, "mode": 1, "rank": 1,
  "eventhandler_serviceendpoint@odata.bind": "/serviceendpoints(<id>)",
  "sdkmessageid@odata.bind": "/sdkmessages(<id>)", "sdkmessagefilterid@odata.bind": "/sdkmessagefilters(<id>)" }
```

### With the SDK (SOAP)

1. Create an `Entity("serviceendpoint")` with `contract` = `OptionSetValue(8)`, `authtype`, `url` and `authvalue`.
2. Create a step whose `eventhandler` is `EntityReference("serviceendpoint", id)` and whose `plugintypeid` is empty.

The Plug-in Registration Tool's "Register New WebHook" performs the same two operations.

## Managing webhooks

| Task | CLI | Notes |
|---|---|---|
| List endpoints | `xrmd webhook list` | The url is shown **without its query string**, because it may carry a key |
| Show one endpoint | `xrmd webhook show new_hook` | The auth value is never shown; `isauthvalueset` tells whether one is stored |
| Change the url or the auth | `xrmd webhook update new_hook --url https://hooks.example.test/v2` | |
| List the steps | `xrmd plugin list` | Webhook steps appear under a "Webhooks" heading; in `-o json` they follow the plug-in rows with a `webhook` property |
| Move a step to another handler | Update of the step's `eventhandler` | Associate and Disassociate over `plugintype_sdkmessageprocessingstep` or `serviceendpoint_sdkmessageprocessingstep` are refused (400) |
| Delete an endpoint | `xrmd webhook delete new_hook --yes` | Also deletes its steps and their images; queued jobs are cancelled (state 3, status 32) |

CLI exit codes: `5` refused by the server, `4` not found, `2` usage error.

## Endpoint rules

### Columns of `serviceendpoint`

| Column | Rule |
|---|---|
| `contract` | 8 (required) |
| `authtype` | 4 Webhook Key, 5 Http Header, 6 Http Query String |
| `url` | Absolute `http` or `https`, no user info, no fragment; port 80 or 443 unless `Pipeline:WebhookAllowAnyPort` is true |
| `authvalue` | Required on Create and **write-only** (never returned). Type 4: any non-blank string. Types 5 and 6: `<settings><setting name="K" value="V" />…</settings>` with at least one setting and non-blank names |
| `isauthvalueset` | Read-only Boolean |
| `messageformat` | Null or 2 (Json) |
| `messagecharset` | Null, 0 (Default) or 1 (UTF8) |
| `connectionmode` | Null or 1 (Normal) |
| `userclaim` | Null or 1 (None) |
| `namespaceaddress`, `path`, `solutionnamespace`, `saskeyname` | Must be null (they apply to Azure Service Bus) |

Any other contract, authentication type or Service Bus value is refused at Create and Update with 400 `0x80040203`.
The table is `serviceendpoint`, object type code 4618 (see [Tables in every organization](system-tables.md)).

### Headers you can send

- The name must be an HTTP token.
- These names are refused: `Content-Type`, `Content-Length`, `Host`, `Transfer-Encoding`, and any name starting with `x-ms-`.
- Values must be printable ASCII.

### Step rules

- A webhook step has `eventhandler` set to the endpoint, and no `plugintypeid` and no `configuration`.
- Stage, mode, filter, filtering attributes, images, rank, impersonating user and `asyncautodelete` follow the same rules as plug-in
  steps (see [Plug-ins](plugins.md)), and so does a custom API's `allowedcustomprocessingsteptype`.
- No step runs on a message of `serviceendpoint` itself, not even a step without a table filter: those messages are not events.

## The request your receiver gets

xrmverse sends `POST {url}` with the `RemoteExecutionContext` as the body (`Content-Type: application/json; charset=utf-8`). The
body is produced by `DataContractJsonSerializer` over the SDK types, so its shape is the one the SDK defines.

### Headers

| Header | Value |
|---|---|
| `x-ms-dynamics-organization` | The organization name (Dataverse sends its domain name) |
| `x-ms-dynamics-entity-name` | `PrimaryEntityName` |
| `x-ms-dynamics-request-name` | `MessageName` |
| `x-ms-correlation-request-id` | `CorrelationId` |
| `x-ms-dynamics-msg-size-exceeded` | `true`, only when the body was trimmed (see below) |

### Authentication

| `authtype` | What is added to the request |
|---|---|
| 4 Webhook Key | Query parameter `code` |
| 5 Http Header | The configured headers |
| 6 Http Query String | The configured query parameters |

The query parameters already present in the url are kept.

### Large bodies and proxies

- A body over 256 KB (262,144 bytes) is sent again without `ParentContext`, `InputParameters`, `PreEntityImages` and
  `PostEntityImages`, with the header `x-ms-dynamics-msg-size-exceeded: true`.
- The sender honours the standard .NET proxy settings `HTTP_PROXY`, `HTTPS_PROXY` and `NO_PROXY`.

## Outcomes, retries and where failures show

### Success and failure

A **2xx** answer is success. Anything else is a delivery failure, reported as 500 `0x80040224` with the message
`Endpoint unavailable: webhook '{name}' {reason}.`, where the reason is one of:

- `returned {status} {reason phrase}`
- `did not answer within {n} s`
- `could not be reached ({error})`

The message never contains the query string, a header or the auth value.

### Retry rules

| Answer | Treatment |
|---|---|
| 502, 503, 504 | One immediate extra attempt, then retryable |
| Any other 5xx, a timeout, a connection or TLS error | Retryable |
| 3xx (redirects are not followed), 4xx | Final |

Each attempt waits at most `Pipeline:WebhookTimeout` (default one minute).

### What happens to the operation

| Step mode | Failure | Result |
|---|---|---|
| Synchronous | Any | The request fails with the fault and the transaction rolls back. The POST cannot be recalled, so the receiver may already have acted |
| Asynchronous | Retryable | The system job is retried with the async queue's backoff (`Pipeline:AsyncMaxRetries`, default 3), then ends failed (3/31) |
| Asynchronous | Final | The system job ends failed (3/31) at once |

### Finding failed deliveries

For an asynchronous step, the failure shows in its system job: `asyncoperation.message` holds the fault message and
`owningextensionid` is the step.

```
GET /api/data/v9.2/asyncoperations?$filter=_owningextensionid_value eq <step id>&$select=statecode,statuscode,message
```

- `asyncautodelete` on a step deletes the system job when it succeeds (plug-in and webhook steps alike).
- No `plugintracelog` rows are written for webhook steps.
- The asynchronous job runs as the owner of its row, as for plug-ins (see [Plug-ins](plugins.md)).

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Pipeline:WebhookTimeout` | `00:01:00` | Time to wait for each attempt: a positive `TimeSpan` of at most `24.20:31:23.647` (`int.MaxValue` milliseconds) |
| `Pipeline:WebhookAllowAnyPort` | `false` | Allow urls on ports other than 80 and 443 (tests, development, on-premises receivers) |
| `Pipeline:AsyncMaxRetries` | `3` | Retries of a failed asynchronous job (shared with plug-ins) |

They are ASP.NET configuration keys: as environment variables, write them as `Section__Key` (for example
`Pipeline__WebhookTimeout`). See [Configuration](configuration.md).

## Known limits

| Limit | Details |
|---|---|
| Other contracts | Contracts other than 8, and `IServiceEndpointNotificationService`, are not supported. Service Bus queue and topic and Event Hub contracts: #84 |
| Not in solutions | Webhooks and their steps cannot be added to a solution (component 95), a solution export never contains them, and an import that holds a `ServiceEndpoints` node or a step with `EventHandlerTypeCode` 4618 is refused. Register them in each organization. #85 |
| Organization header | `x-ms-dynamics-organization` carries the organization name, not Dataverse's domain name |
| Secret stored unencrypted | The `authvalue` is stored unencrypted in `meta.serviceendpoint_secret` of the organization database, outside the table so that no read path returns it. Protect access to the database and its backups. #86 |
| Async primary-name image | The automatic `AsynchronousStepPrimaryName` image of an asynchronous step is not added. #87 |
| Not verified against Dataverse | The synchronous failure code `0x80040224` (the documentation names the error dialog, not the code), and the retry of async jobs beyond the one extra attempt |
| `TriggerServiceEndpointCheck` | Not served |
| Navigation properties | `serviceendpoint_sdkmessageprocessingstep` is not in `$metadata`; `$expand` and the navigation path return 400. The single-valued `eventhandler_serviceendpoint` of a step works. #88 |
| Web admin | The Plug-ins page lists plug-in steps only; use `xrmd webhook` and `xrmd plugin list` |

## See also

- [Plug-ins](plugins.md): steps, stages, modes and images
- [Installing the Microsoft SDK](microsoft-sdk.md)
- [CLI xrmd](cli.md)
- [Known issues and limitations](known-issues.md)
