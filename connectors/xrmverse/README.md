# xrmverse custom connector

The files a customer imports as a custom connector, so that cloud flows and apps can read and write the data of an xrmverse
server.

| File | Content |
| --- | --- |
| `apiDefinition.swagger.json` | The OpenAPI 2.0 definition of the actions and of the operations behind their pickers. |
| `apiProperties.json` | The connection: OAuth 2.0 with Microsoft Entra ID, and the brand color of the icon. |
| `icon.png` | The xrmverse symbol, 256 × 256 pixels. |

## Actions

| Action | Request |
| --- | --- |
| List rows (`ListRows`) | `GET /api/data/v9.2/{table}`, with `$select`, `$filter`, `$orderby`, `$expand`, `$top` or `fetchXml`; the pages follow `@odata.nextLink` |
| Get a row by ID (`GetRow`) | `GET /api/data/v9.2/{table}({id})` |
| Add a new row (`AddRow`) | `POST /api/data/v9.2/{table}`, answered with the saved row (`Prefer: return=representation`) |
| Update a row (`UpdateRow`) | `PATCH /api/data/v9.2/{table}({id})` with `If-Match: *`: the row must exist |
| Delete a row (`DeleteRow`) | `DELETE /api/data/v9.2/{table}({id})` |
| Run a custom API (`RunCustomApi`) | `POST /api/data/v9.2/Microsoft.Dynamics.CRM.{customApi}`, a custom API not bound to a table |

## Trigger

| Trigger | Request |
| --- | --- |
| When a row changes (`OnRowChanged`) | `POST /api/connector/v1/subscriptions` with `{ table, changes, filteringColumns, callbackUrl }`: answered 201 with a `Location` header and `{ id }`; the platform fills `callbackUrl` in |

The platform removes the subscription when the flow is turned off or deleted, with the hidden operation `DeleteSubscription`
(`DELETE /api/connector/v1/subscriptions/{id}`, 204). `changes` is a comma-separated subset of `create`, `update` and `delete`.
The server calls the flow with `{ change, table, id, row }` (no `row` for a delete); the columns of `row` come from `GetTableSchema`
with `use=read`. See [Cloud flow connector](../../docs/flow-connector.md#the-trigger).

The table and custom API pickers, and the columns and parameters shown for the one picked, come from four hidden operations
under `/api/connector/v1`: `GetTables`, `GetTableSchema`, `GetCustomApis` and `GetCustomApiSchema`. They list only what the
signed-in user may read or run.

## Placeholders

The two JSON files are templates. Before the import, replace:

| Placeholder | Value |
| --- | --- |
| `{{HOST}}` | The host name of the server, without `https://` (for example `acme.example.com`). |
| `{{CLIENT_ID}}` | The application (client) ID of the Entra app registration the connector signs in with. |
| `{{RESOURCE_URI}}` | The application ID URI of the server's Entra app registration, the audience of its tokens. |

The client secret of the connector's app registration is never written in these files: give it at import time.
