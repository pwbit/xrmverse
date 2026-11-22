# SQL endpoint

[← xrmverse documentation](../README.md#documentation)

## Introduction

The SQL endpoint lets SQL tools read an organization's data: SQL Server Management Studio (SSMS), Power BI, `Microsoft.Data.SqlClient` and anything else that speaks the SQL Server protocol (TDS). It is **read-only**: it translates a `SELECT` into the same query the Web API runs, so the caller's security applies exactly as over HTTP. The table below sums up how it compares with the Dataverse endpoint; the chapters then go from who can connect and how to switch it on, to the SQL it understands, its limits and troubleshooting. The overall picture is in [xrmverse compared with Dataverse](dataverse-comparison.md).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Protocol | TDS 7.x, read-only `SELECT` | Same idea; no `Encrypt=Strict` (TDS 8) |
| Sign-in | SQL logins of the local identity provider only | Differs: Dataverse accepts Entra sign-in only |
| Security | the caller's roles, sharing and field security, as over the Web API | Same |
| Plug-ins and auditing | no plug-ins run, reads not audited | Same |
| SQL subset | what the query engine can express; no subqueries, unions, scalar functions, `HAVING` | Narrower |
| Results | buffered and capped at `Tds:MaxRows` (50000) | Differs: never a partial result |
| Catalog | `INFORMATION_SCHEMA` only; other `sys.*` views refused | Narrower |
| Turning it off | remove the organization's port | Narrower: no per-user privilege |
| Database engine | organizations on SQL Server or PostgreSQL alike: clients send T-SQL in both cases, translated into the query engine's own query | Same: the client never sees the engine |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Check who may sign in | local identity provider users with a password | [Who can connect](#who-can-connect) |
| Open the endpoint | `--tds-port` at `xrmd server init` | [Enable it](#enable-it) |
| Connect a tool | SSMS, Power BI, `SqlClient` | [Connect](#connect) |
| Write a query | the supported `SELECT` subset | [Supported SQL](#supported-sql) |
| Find column names | logical names and the `<column>name` columns | [Columns](#columns) |
| Renew the certificate | automatic through `tds-certs` | [The certificate](#the-certificate) |
| Fix a failing connection | symptom table | [Troubleshooting](#troubleshooting) |

## Who can connect

The endpoint accepts **SQL logins only: a user name and a password, for organizations that use the local identity provider** (`Auth:Mode=Local`, `xrmd server init --auth local`).

- Entra and Development organizations have **no SQL endpoint** for now. `--tds-port` is refused for them, and a login to the endpoint of any other mode fails.
- A sign-in with an access token (Microsoft Entra: `SqlConnection.AccessToken`, SSMS "Microsoft Entra MFA", the Power BI Dataverse connector) is **not supported**: the TDS server library does not pass the token on, and the login is refused with error 18456 and the message `Login failed: the SQL endpoint does not accept access tokens (Microsoft Entra sign-in).` SSMS "Microsoft Entra MFA" and the Power BI Dataverse connector are therefore expected not to work: a token login that reaches the endpoint is refused with 18456 (not yet verified with these clients). Entra support is a backlog item (B1 of the plan: vendor the protocol layer).
- This is the opposite of Dataverse, whose endpoint accepts Entra sign-in only.
- The user name is the one of the web sign-in (`systemuser.domainname`, for example `Administrator` or `kim@example.com`), with the password set by `xrmd idp user set-password` (see [Local identity provider](local-idp.md#users-and-totp)).
- A user with **TOTP** is refused: TDS has no second factor. The refusal counts as a failed attempt.
- **Lockout is shared with web sign-in**: five failed attempts lock the account for 15 minutes, whichever door they came through.
- A **disabled** user, a user **without roles** and a user who is not a member of the organization are refused at login. The user sees "Login failed" and never which part was wrong; the reason is in the server log at Information, with the user name and never the password (only the login log carries the name; batch logs carry the user id).

Every refusal at login is error 18456 with the message `Login failed for the SQL endpoint.` (the access token case has its own message above).

## Enable it

Give the organization a port when you create it:

```bash
xrmd server init --dir /srv/xrmd --org acme --org zenith --domain example.com --tls acme --acme-email admin@example.com \
  --auth local --tds-port 5558 --accept-microsoft-license --accept-sql-server-eula
```

`--tds-port` is the host port of the **first** organization; the others take the next ports (`acme` 5558, `zenith` 5559). To add it to a new organization later:

```bash
xrmd server org add orbit --public-host orbit.example.com --tds-port 5560 --dir /srv/xrmd
```

`init` and `org add` print the connection target of each organization (`acme.example.com,5558`) and remind you to open the port in your firewall.

- The port is **per organization** because TDS negotiates TLS inside its own handshake, so a proxy cannot route it by host name. Clients connect as `<host>,<port>`.
- The organization needs a **public host** (its certificate names it) and the local identity provider. Otherwise the command exits 2.
- Ports 80 and 443 are refused behind the generated Caddy proxy (the proxy publishes them), and a port already used by another organization (HTTP or SQL) is refused.
- The port is published on every address. `XRMD_TDS_BIND` in `.env` chooses another address (default `0.0.0.0`). `ORG_<ORG>_TDS_PORT` in `.env` holds the port of each organization; empty means no endpoint.
- With `--no-proxy` (your own proxy for HTTP), `--tds-cert` and `--tds-key` are **required**: the PEM certificate and unencrypted key of the SQL endpoints, one pair covering every host, copied to `<dir>/certs/tds.crt` and `tds.key`. TDS cannot go through your HTTP proxy; clients connect to the server's published port directly.
- `xrmd server status` shows the TDS port and the certificate expiry of each organization; `xrmd server doctor` checks the certificate (see [The certificate](#the-certificate)).
- Removing the organization (`xrmd server org remove`) removes its endpoint and its certificate folder.

The reference for the commands is [CLI `xrmd`](cli.md#set-up-a-server); the installation steps are in [Installing xrmverse](install.md#sql-endpoint).

## Connect

Use the host and port, **encryption on**, and SQL Server authentication. The SSMS and Power BI settings below are expected to work; they are not yet verified.

| Client | Settings |
|---|---|
| SSMS | Server name `acme.example.com,5558`; Authentication "SQL Server Authentication"; Login the user name; Password |
| Power BI Desktop | The **SQL Server** connector (not the Dataverse connector, which signs in with Entra), server `acme.example.com,5558`, Import mode, "Database" credentials with the user name and password |
| `Microsoft.Data.SqlClient` | `Data Source=acme.example.com,5558;User ID=<user>;Password=<password>;Encrypt=True` |

- `Encrypt=True` works. **`Encrypt=Strict` (TDS 8) does not**: the server library speaks TDS 7.x. A connection without encryption is refused by the server.
- The client must trust the certificate. A certificate from a public CA (ACME) needs nothing; with the proxy's internal CA, trust `<dir>/certs/caddy-root.crt` on the client (see [Certificates](install.md#6-certificates)), or use `TrustServerCertificate=True` for a test.
- The session reports the organization's database name as its database.
- Verified with `Microsoft.Data.SqlClient` in the automated tests. SSMS and Power BI are to be verified by hand.
- SSMS sends multi-statement batches and `sys.*` queries when it connects and browses, and Power BI may wrap a native query in a subquery. The endpoint refuses both by design (error 40000), so **SSMS Object Explorer and some Power BI queries may be refused**; a query window in SSMS and a plain `SELECT` in Power BI are the expected paths.

```csharp
using Microsoft.Data.SqlClient;

await using var connection = new SqlConnection(
    "Data Source=acme.example.com,5558;User ID=kim@example.com;Password=...;Encrypt=True");
await connection.OpenAsync();
await using var command = new SqlCommand("SELECT TOP 10 name, revenue FROM account ORDER BY name", connection);
await using var reader = await command.ExecuteReaderAsync();
```

## Supported SQL

The endpoint supports the subset of SQL that the query engine can express. Anything else is refused **before anything runs**, with error 40000 and the message `The SQL endpoint does not support <construct>.`

| Supported | Notes |
|---|---|
| `SELECT` with columns, aliases or `*` | `*` and `<alias>.*` expand to the readable columns (see [Columns](#columns)) |
| `TOP n`, `DISTINCT` | `TOP` takes a number or a `@parameter`; not `PERCENT`, not `WITH TIES` |
| `FROM <table> [alias]` | A table is a logical name, with an optional `dbo.` schema; `[brackets]`, quotes and upper case resolve to the same table |
| `INNER JOIN`, `LEFT JOIN` | Only `ON a.<lookup> = b.<primary key>`, in either direction |
| `WHERE` | `=`, `<>`, `!=`, `<`, `<=`, `>`, `>=`, `LIKE`, `NOT LIKE`, `IN`, `NOT IN`, `BETWEEN`, `IS [NOT] NULL`, `AND`, `OR`, `NOT`; a column against a literal or a `@parameter` |
| `ORDER BY` | Columns or aliases |
| `GROUP BY` | Columns, with `COUNT(*)`, `COUNT(col)`, `COUNT(DISTINCT col)`, `SUM`, `AVG`, `MIN`, `MAX` |
| `sp_executesql` | A statement with parameters; this is how SqlClient sends a parameterized command |
| `SET` options | Accepted and **ignored** (`SET NOCOUNT`, isolation level, `DATEFORMAT` and the like). `SET FMTONLY OFF` is ignored too; `SET FMTONLY ON` is refused, because ignoring it would run the query; `SET ROWCOUNT` other than 0 is refused |
| System queries | See [System queries](#system-queries) |

Refused (error 40000): any statement other than `SELECT` (`INSERT`, `UPDATE`, `DELETE`, `DROP`, and every other DML or DDL), `EXEC` of any procedure other than `sp_executesql`, more than one statement in a batch (`SELECT 1; DROP TABLE x`), subqueries, common table expressions, `UNION` and the other set operators, `HAVING`, `OFFSET`/`FETCH`, `CROSS JOIN`, `RIGHT` and `FULL` joins, `APPLY`, `PIVOT`, `SELECT INTO`, table and query hints, window functions, `CASE`, `CAST`/`CONVERT`, other functions or expressions in the select list or in `WHERE`, comparisons between two columns, comparisons with `NULL` (use `IS NULL`), `sys.*` catalog views, and names with a server or a database.

### Errors

The numbers are the generic SQL Server ones; the texts are xrmverse's own.

| Number | When | Message |
|---|---|---|
| 40000 | A construct that is not supported, an invalid query, a row or time limit | `The SQL endpoint does not support <construct>.`, the query engine's message, or a limit message below |
| 207 | A column that does not exist or is not exposed | `Invalid column name '<name>'.` |
| 208 | A table that does not exist | `Invalid object name '<name>'.` |
| 209 | A column name that more than one table of the query has | `The column name '<name>' is in more than one table: qualify it with the table or its alias.` |
| 1013 | Two tables of the query with the same name or alias | `The query names two tables '<name>': give them different aliases.` |
| 102 | The SQL text cannot be parsed | `The SQL text has a syntax error at line <n>, column <n>.` |
| 137 | A `@variable` that was not passed as a parameter | `The variable "<name>" has no value: pass it as a parameter of the request.` |
| 229 | The user lacks the read privilege on a table, or filters on a secured column they cannot read | The security engine's message |
| 18456 | Login refused | `Login failed for the SQL endpoint.` |
| 50000 | A failure of the server itself | `The SQL endpoint could not answer the request.` |

A timeout is error 40000 with `The query exceeded the 5 minute limit.` The server never returns SQL text, stack traces or other internals in an error.

## Columns

Each attribute keeps its logical name (`accountid`, `name`, `primarycontactid`).

| Dataverse type | SQL type | Notes |
|---|---|---|
| Single line and multiple lines of text | `nvarchar` | |
| Whole number | `int` | |
| Big integer | `bigint` | |
| Decimal | `decimal` | |
| Floating point | `float` | |
| Currency | `money` | |
| Two options | `bit` | |
| Date and time | `datetime2` | Always UTC |
| Unique identifier | `uniqueidentifier` | |
| Choice, state and status | `int` | The value; the label is in `<column>name` |
| Lookup, customer, owner | `uniqueidentifier` | The id; the name of the target is in `<column>name` |

- A lookup, customer, owner, choice, state or status column also has a virtual column `<column>name` (for example `parentcustomeridname`, `statuscodename`): the name of the target record, or the label in the user's language. Select it by name; it is not part of `*`. You cannot filter, group or sort on it. If a real column already has that name, the real column wins.
- File and image columns (and their companion columns), multiple-choice, party list, virtual and other types are not exposed: they give `Invalid column name`.
- **`*` leaves out the secured columns the user cannot read.** Naming such a column returns `NULL`, and filtering on it is refused with error 229.

## System queries

Clients send a few queries on connect. They are answered by the endpoint itself:

- `SELECT @@VERSION` (the text ends with `12.0.2000.8`, the version number clients compare), `SELECT SERVERPROPERTY('ProductVersion' | 'Edition' | 'EngineEdition' | 'ProductLevel')`, `SELECT DB_NAME()`, `SELECT @@SPID`;
- `INFORMATION_SCHEMA.TABLES` and `INFORMATION_SCHEMA.COLUMNS`, with an optional `WHERE TABLE_NAME = '<table>'`.

`INFORMATION_SCHEMA` lists **only the tables the caller can read**, and the columns of the rules above (no secured column the caller cannot read; the `<column>name` columns included). Other `sys.*` views and metadata functions give error 40000, so a client that browses the catalog (SSMS Object Explorer) may show only part of the tree.

## Limits

- **Row cap.** A query returns at most `Tds:MaxRows` rows, **50000** by default. A query over the cap fails with `The query returns more than 50000 rows. Add TOP or a filter.`; **no partial result is ever returned**. Add `TOP` or a filter.
- **Grouped queries** are read in one page of up to 5000 groups; more fail with `The aggregate query returns more than 5000 rows. Add TOP or a filter.` (or the cap message when `Tds:MaxRows` is 5000 or less).
- **Time limit.** A batch that runs longer than 5 minutes fails (`Tds:Timeout`).
- **Page time limit.** Each internal page must complete within 30 seconds; a slower page fails the batch with the same error 40000 and time-limit message.
- Large reads are paged internally; results are buffered in the server's memory, not streamed (backlog item B2).
- `TOP 0` returns the columns and no row.

## Security and plug-ins

- The query runs **as the user who logged in**. Business-unit, team and sharing rules, and field security, apply as over the Web API: a user sees only the records they may read, a secured column they cannot read is `NULL`, and a table without the read privilege gives error 229.
- **No plug-ins run** on SQL reads (as in Dataverse).
- The endpoint is read-only: nothing it accepts can create, change or delete data.
- Reads are **not audited**, as over HTTP.
- A password change does not end open SQL sessions; disable the user, or remove their roles, to cut them at the next query.
- Each batch is logged at Information with the user id, the duration and the number of rows, **never the SQL text or the parameter values**, and never a password. Errors log the exception type only.
- TLS is **required** (`RequireEncryption`). The only exception is a host in `Development` mode without a certificate, which listens on the loopback address only. Without TLS, the password would be only obfuscated.
- An organization that does not use the local identity provider cannot be given an endpoint with the CLI, and the endpoint refuses every login there.

## The certificate

The endpoint presents the certificate of the organization's public host.

- With the generated Caddy proxy, a long-running **`tds-certs`** service copies each organization's certificate from the proxy's storage into `<dir>/tds/<org>/<host>.crt` and `.key`, readable by the server and by nobody else. Each organization's container mounts only its own folder. It copies at start, then sleeps and copies again every **12 hours** (ACME), every **1 hour** (`--tls internal`, whose certificates live 12 hours) or every **5 minutes** (`--tls files` or `--no-proxy`, where the pair is the file you replace by hand: `certs/server.crt` or `certs/tds.crt`). While a pair is missing, it tries again every minute.
- The server checks the files every **10 seconds** and restarts the listener on the same port when they change (about 10 seconds after `tds-certs` copied them). Open connections are closed and the port is briefly unavailable while the listener restarts; clients reconnect. A pair that cannot be loaded (half written) keeps the running server.
- If the files are **missing at start**, the host still starts (HTTP, web admin and identity provider are not affected) and logs a warning; the endpoint opens once the files appear. `server up` right after `init` may therefore have a closed SQL port for up to a few minutes with ACME, until the certificate is issued and copied.
- `xrmd server status` shows the expiry day of each endpoint certificate (`missing` while there is none); `xrmd server doctor` has a `tds-certificate` check: an error when the file is missing, unreadable or expired, a warning 14 days before expiry (not with the internal CA, which renews on its own).

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| "Login failed" (18456) | Wrong password; locked out (wait 15 minutes, or set a new password); a user with TOTP; a disabled user or a user without roles; an Entra or Development organization; an access token login |
| `Encrypt=Strict` fails | TDS 8 is not supported: use `Encrypt=True` |
| A connection without encryption fails | TLS is required |
| The client does not trust the certificate | With the internal CA, trust `certs/caddy-root.crt`; otherwise check the host name in `Data Source` matches the certificate |
| Connection refused or timeout | The endpoint is not open yet (no certificate: `xrmd server status`, `xrmd server logs`); the port is not open in the firewall; wrong port |
| `The SQL endpoint does not support …` | The query uses SQL outside the [subset](#supported-sql); rewrite it, or load the table with a plain `SELECT` and transform it in the tool |
| Power BI or SSMS shows an empty or partial list of tables | They browse the catalog with `sys.*` views, which are not supported; write the query by hand |
| `Invalid column name` | The name is not a logical column of the table, or its type is not exposed (file, image, multiple-choice) |

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- Local identity provider only; no Entra (see [Who can connect](#who-can-connect)).
- One port per organization.
- `SELECT` of the subset above only; no subqueries, unions, scalar functions or `HAVING`.
- Results are buffered and capped, not streamed.
- TDS 7.x only; no `Encrypt=Strict`.
- No per-user privilege to turn the endpoint on or off (Dataverse has one), and no setting to turn it off from the web admin: remove the organization's port.

## See also

- [Local identity provider](local-idp.md)
- [Install](install.md#sql-endpoint)
- [Queries](queries.md)
- [Security](security.md)
- [Field security](field-security.md)
- [xrmverse compared with Dataverse](dataverse-comparison.md)
