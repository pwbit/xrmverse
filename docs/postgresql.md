# PostgreSQL organizations

[← xrmverse documentation](../README.md#documentation)

## Introduction

An xrmverse server can keep its organizations on **PostgreSQL 16 or later** instead of SQL Server. Clients do not see the
difference: the Web API, the SDK (SOAP), FetchXML, QueryExpression, OData, the web admin and the CLI work the same way, and
the same organization answers the same way on either engine. The engine is chosen once per server, when you create the
installation; every organization of that server uses it.

PostgreSQL organizations are complete (roadmap item 2.7: the core in 2.7a, the rest in 2.7b) and have every feature of a
SQL Server organization: metadata (tables, columns, relationships, choices, keys, autonumber), create, retrieve, update and
delete, associate and disassociate, upsert, `ExecuteMultiple` and `$batch`, queries (FetchXML, QueryExpression and OData, with aggregates,
paging and links), security (roles, business units, teams, sharing, access teams, hierarchy and field security), auditing,
change tracking, asynchronous operations and plug-in steps, the plug-in trace, custom APIs, currencies, activities,
calculated, rollup and formula columns, environment variables, the glossary and synonym tables, time zones and the web admin, and
solutions (import, export and uninstall, with managed layers and `xrmd apply`) with the model-driven apps they bring, duplicate
detection (rules, their publishing, the Create and Update check, `RetrieveDuplicates` and `BulkDetectDuplicates`), elastic
tables (partitions, time to live, JSON column filters and `ExecuteCosmosSqlQuery`), file and image columns with note
attachments (uploads and downloads in blocks, the Web API file routes, `DeleteFile`, `documentbody`, and the blob cleanup), classic
workflows (real-time and background workflows, business rules, custom process actions, `ExecuteWorkflow` and workflow as code),
webhooks (service endpoints and their delivery), the local identity provider (`--auth local`), the SQL endpoint (TDS), backup
and restore, and the migration from Dataverse (package upload, import runs, verification, `xrmd migrate run` and the
`migrate_*` MCP tools). The one thing a PostgreSQL organization refuses is a `like` pattern with a character range: see
[What PostgreSQL organizations refuse](#what-postgresql-organizations-refuse).
Text equality follows SQL Server's `_CI_AS` rules (case, trailing spaces, width and kana type ignored, `ß` equal to `ss`,
accents kept); `like` patterns and the order of punctuation are where text still differs: see
[Text: case, accents and sorting](#text-case-accents-and-sorting).

### Compared with Dataverse

Dataverse keeps its data in a database Microsoft runs; an xrmverse organization keeps it in a database you run. The table
compares a PostgreSQL organization with Dataverse, and with an xrmverse organization on SQL Server where that is the
useful comparison.

| Area | xrmverse on PostgreSQL | Compared with Dataverse |
|---|---|---|
| Database | PostgreSQL 16 or later, bundled (`postgres:16-alpine`) or your own server, with direct access | **Differs**: Dataverse's database is managed by Microsoft, with no direct access |
| Engine per server | One engine for every organization of a server | **Extra**: SQL Server or PostgreSQL, chosen at `xrmd server init` |
| License of the database | No SQL Server license terms to accept | **Extra** |
| Text in filters, keys and sorting | Case ignored, accents kept, as in SQL Server organizations (`_CI_AS`) | Same |
| Sorting of punctuation and symbols | Follows the Unicode root collation | **Differs**: the order of punctuation and symbols may differ from a SQL Server organization; letters, digits, case and accents sort the same |
| Trailing spaces, width, kana and `ß` in text equality | Ignored, as on SQL Server: `'Acme '` = `'Acme'`, `'ＡＣＭＥ'` = `'ACME'`, `'あいう'` = `'アイウ'`, `'straße'` = `'STRASSE'`, in filters, joins, `distinct`, grouping and keys | Same |
| Width, kana and `ß` in `like` | Count: `like 'straße%'` does not find `STRASSE` | **Differs** from a SQL Server organization (see [Known limits](#known-limits)) |
| `like` with a character range (`[a-c]`) | Refused with 501 | **Narrower**: sets such as `[abc]` work |
| Migration from Dataverse | The same import, on the same package: records keep their GUIDs | Same as a SQL Server organization |
| Change tracking | The same tokens and the same guarantee: no committed change is ever skipped | Same |
| Table and column names | At most 63 bytes (UTF-8) | **Narrower**: a longer name is refused |
| Backups | `xrmd org backup` and `org restore` with `pg_dump` and `pg_restore` in the server image | **Differs**: Dataverse backs up for you; the file is a `pg_dump` archive, not a SQL Server `.bak` (see [Backup and restore](#backup-and-restore)) |

Outside these rules, what a PostgreSQL organization cannot do as SQL Server does fails with an explicit error. The
exceptions are `like` with full-width, kana or `ß` and the order of punctuation: they give a different result without an
error. The full list is in [Known limits](#known-limits).

### Before you start

- **Docker with Compose v2** and the `xrmd` CLI, as for any installation (see [Installing xrmverse](install.md)).
- **The Microsoft SDK**, installed with `xrmd sdk install`: the server needs it on both engines (see
  [Installing the Microsoft SDK](microsoft-sdk.md)).
- **PostgreSQL 16 or later.** By default `xrmd server init` adds a `postgres` service to the installation. To use a server you
  already run, see [Using your own PostgreSQL server](#using-your-own-postgresql-server).
- **Decide before you create the installation.** An installation keeps its engine: organizations are not moved from one
  engine to the other, and `server init --force` keeps the engine of the existing `.env`.
- **Check what you need.** [What PostgreSQL organizations refuse](#what-postgresql-organizations-refuse) and the
  [known limits](#known-limits) list what differs from a SQL Server organization.

## Where you manage it

| Tool | What you can do |
|---|---|
| CLI `xrmd server init --database postgresql` | Create an installation whose organizations run on PostgreSQL (see [Creating a PostgreSQL installation](#creating-a-postgresql-installation)) |
| CLI `xrmd server org add`, `server org remove` | Add and remove organizations; they follow the installation's engine (see [Multiple organizations](multiple-organizations.md)) |
| `.env` of the installation | `STORAGE_PROVIDER` and `POSTGRES_PASSWORD` (see [Settings](#settings)) |
| Configuration of a server you run yourself | `Storage:Provider` and `ConnectionStrings:Org` (see [Configuration](configuration.md#database-and-startup)) |
| Repository compose file | `docker/docker-compose.postgres.yml`, for development (see [Running from a clone](#running-from-a-clone)) |

There is nothing to switch inside an organization: the Web API, the SDK, the web admin and the CLI work as with SQL Server.

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Create an installation on PostgreSQL | `xrmd server init --database postgresql` | [Creating a PostgreSQL installation](#creating-a-postgresql-installation) |
| Use your own PostgreSQL server | `--database postgresql --sql-connection "Host=…;Username=…;Password=…"` | [Using your own PostgreSQL server](#using-your-own-postgresql-server) |
| Add an organization | `xrmd server org add <name>` | [Adding organizations](#adding-organizations) |
| Run a server built from a clone | `docker compose -f docker/docker-compose.postgres.yml` | [Running from a clone](#running-from-a-clone) |
| Back up and restore an organization | `xrmd org backup`, `xrmd org restore` | [Backup and restore](#backup-and-restore) |
| Know what is refused | The 501 and its text | [What PostgreSQL organizations refuse](#what-postgresql-organizations-refuse) |

## Creating a PostgreSQL installation

```bash
xrmd server init --dir ./xrmd-pg --org acme --database postgresql --accept-microsoft-license
xrmd server up --dir ./xrmd-pg
export XRMD_DEV_SIGNING_KEY=$(grep '^AUTH_DEV_SIGNING_KEY=' ./xrmd-pg/.env | cut -d= -f2)
xrmd server status --dir ./xrmd-pg      # acme on http://localhost:5001/
```

`--database` takes `sqlserver` (the default) or `postgresql`. With `postgresql`:

- The compose file has a `postgres` service (image `postgres:16-alpine`, restart `unless-stopped`, data in the `pg-data`
  volume, health check `pg_isready -U postgres -d postgres`) instead of the `sqlserver` and `backups-init` services, and no
  `backups` volume.
- Each organization's service has `Storage__Provider: PostgreSql`, waits until `postgres` is healthy, and connects with
  `Host=postgres;Database=<org>;Username=postgres;Password=${POSTGRES_PASSWORD}`. The organization's database is created
  at its first start.
- `.env` has `STORAGE_PROVIDER=PostgreSql` and a generated `POSTGRES_PASSWORD` (32 letters and digits); it has no
  `SA_PASSWORD`, `MSSQL_PID` or `ACCEPT_EULA`. No SQL Server license terms are asked for.
- `--accept-sql-server-eula` and `--sql-edition` are refused (exit 2): they apply only to SQL Server.
- `--auth local` and `--tds-port` work as with SQL Server: the organizations get the local identity provider (see
  [Local identity provider](local-idp.md)) and, with `--tds-port`, the SQL endpoint, which speaks T-SQL to its clients as on
  SQL Server whatever the engine behind it.

Host names, TLS, the generated proxy and the other options of `server init` work as with SQL Server (see
[Installing xrmverse](install.md)).

### Running `server init --force` again

`--force` rewrites `docker-compose.yml` and `.env` and keeps the generated secrets, `POSTGRES_PASSWORD` included. It also
keeps the engine: without `--database` it uses the engine of the existing `.env`, and a `--database` that names the other
engine is refused before anything is written (exit 5), because the data volume of the current engine holds every
organization. To change engine, create a new installation in another directory (`--dir`).

## Using your own PostgreSQL server

```bash
xrmd server init --dir ./xrmd-pg --org acme --database postgresql \
  --sql-connection "Host=db.example.test;Username=xrmd;Password=…" --accept-microsoft-license
```

- The connection string must set `Host` and must not set `Database` (exit 2 otherwise): each organization's connection
  string is this one plus `;Database=<org>`.
- No `postgres` service and no `POSTGRES_PASSWORD` are generated.
- The server must be PostgreSQL 16 or later. An older one stops the organization at startup with
  `xrmverse needs PostgreSQL 16 or later (found <version>).`
- At startup each organization connects to the maintenance database `postgres`, creates its own database when it does not
  exist (`TEMPLATE template0`, `ENCODING 'UTF8'`, `LOCALE_PROVIDER icu`, `ICU_LOCALE 'und'`), then creates the `citext`
  extension and the schemas `meta`, `data` and `auth` in it. The login therefore needs to connect to `postgres`, to create
  databases, and to own the organization's database. The minimum set of privileges has not been verified on a hardened
  server (not verified).
- A database name longer than 63 bytes (UTF-8) is refused: PostgreSQL would cut it.
- Every connection sets `standard_conforming_strings=on` as a startup option. A connection pooler that rejects startup
  options (for example PgBouncer in transaction mode without `ignore_startup_parameters = options`) makes the connection
  fail with an explicit error (not verified on a managed service).

## Adding organizations

`xrmd server org add <name>` and `xrmd server org remove <name>` work as described in
[Multiple organizations](multiple-organizations.md); a new organization uses the installation's engine, and `--tds-port`
gives it the SQL endpoint as on SQL Server.

`xrmd org create`, `org list`, `org delete`, `org backup` and `org restore` (Admin API) also work on PostgreSQL (see
[Backup and restore](#backup-and-restore)).

## Running from a clone

The repository has a compose file for development, the counterpart of `docker/docker-compose.yml` without SQL Server:

```bash
cp docker/.env.example docker/.env     # set POSTGRES_PASSWORD, AUTH_*, SEED_*
docker compose -f docker/docker-compose.postgres.yml --env-file docker/.env up --build
```

It runs `postgres:16-alpine` and an `xrmd` service built from `docker/Dockerfile`, with `Storage__Provider: PostgreSql` and
the database `xrmverse`, with the volumes `files` (file and image columns) and `backups`. `SA_PASSWORD`, `MSSQL_PID` and
`ACCEPT_EULA` are not read. `AUTH_MODE` can be `Development`, `Entra` or `Local`; the SQL endpoint lines are commented out,
as in `docker/docker-compose.yml`. See [Getting started](getting-started.md) for the rest of `docker/.env`.

## Configuring a server by hand

A server you run yourself, outside the generated compose file, needs two settings:

| Configuration key | Environment variable | Value |
|---|---|---|
| `Storage:Provider` | `Storage__Provider` | `PostgreSql` (case ignored). `SqlServer`, or no value, is SQL Server |
| `ConnectionStrings:Org` | `ConnectionStrings__Org` | An Npgsql connection string: `Host=…;Database=<org>;Username=…;Password=…` |

Any other value of `Storage:Provider` stops the server at startup with `Storage:Provider must be SqlServer or PostgreSql.`
`Auth:Mode=Local` and `Tds:Port` work on PostgreSQL as on SQL Server.

## What PostgreSQL organizations refuse

Every feature runs on PostgreSQL. One query construct is refused where it is compiled, before anything is read, with:

| Front end | What the caller gets |
|---|---|
| Web API and Admin API | HTTP **501**, `{"error":{"code":"0x80040216","message":"'<feature>' is not available on PostgreSQL organizations yet."}}` |
| SDK (SOAP), plug-ins | An `OrganizationServiceFault` with `ErrorCode` `0x80040216` (-2147220970) and the same message (HTTP 500, as every SOAP fault) |
| MCP | A tool error with the same message |
| CLI commands that call the server | The same message, exit code 1 (server 5xx) |

| Feature | Where it is refused |
|---|---|
| `Like patterns with character ranges` | A `like` or `not-like` condition (FetchXML, QueryExpression, OData `contains` and the other text functions that become `like`) whose pattern has a range such as `[a-c]` or `[^0-9]`. See [Text: case, accents and sorting](#text-case-accents-and-sorting) |

On SQL Server this refusal does not exist.

## Text: case, accents and sorting

Each organization database is created with the ICU root collation (`und`), and every text column has the type `citext`.
Text compares as with SQL Server's `_CI_AS` collation of the organization databases (`SQL_Latin1_General_CP1_CI_AS`):
case is ignored and accents are kept.

**Equality** goes further, as on SQL Server: trailing spaces, width (full-width and half-width letters) and kana type
(hiragana and katakana) are ignored too, and `ß` and `ẞ` equal `ss`. This applies to `eq`, `ne`, `in` and `not-in`
conditions, joins on text columns, `distinct` and grouping, and unique and alternate keys. Stored values keep their spaces and
characters: only the comparison ignores them.

| Example | Result |
|---|---|
| `name eq 'ACME'` | finds `Acme` |
| `name eq 'Acme '` | finds `Acme` |
| `name eq 'ＡＣＭＥ'` | finds `ACME` |
| `name eq 'アイウ'` | finds `あいう` |
| `name eq 'STRASSE'` | finds `straße` |
| `name eq 'e'` | does not find `é` |
| `begins-with 'ac'` | finds `ACME Corp` |
| An alternate key on `name` | refuses `acme  ` when `Acme` exists (duplicate key, `0x80040237`) |
| `distinct` or a group on `name` over `Acme` and `ACME  ` | one row, which shows one of the stored values as it is |
| `$orderby=name` | `alpha`, `Beta`, `gamma` |

Differences from a SQL Server organization:

- **`like` with width, kana or `ß`**: `like`, `begins-with`, `ends-with` and the OData text functions compare with `citext`,
  which ignores case and keeps accents but does not ignore width or kana type and does not treat `ß` as `ss`. So
  `like 'straße%'` does not find `STRASSE`, and `begins-with 'ＡＣ'` does not find `ACME`. PostgreSQL 16 cannot run `like` on
  the collation that equality uses; this may change when xrmverse needs PostgreSQL 18 or later.
- **Punctuation and symbols** may sort in another order than on SQL Server. Letters, digits, case and accents sort the
  same. This stays: no PostgreSQL collation reproduces SQL Server's order of punctuation.
- **`like` with a character range** (`[a-c]`, `[^0-9]`) is refused with 501 and
  `'Like patterns with character ranges' is not available on PostgreSQL organizations yet.` On SQL Server a range follows
  the collation's order (`[a-c]` matches `á` and `B`); a PostgreSQL range follows code points, and no PostgreSQL operator
  orders it as SQL Server does. Sets of characters (`[abc]`, `[^abc]`), `%`, `_` and single bracketed characters (`[%]`,
  `[_]`, `[[]`) work, with case ignored and accents kept.
- **`distinct` on text columns** costs more on PostgreSQL: to show one stored value per group of equal values, the query
  reads every matching row through a window function before it removes the duplicates. On a large result, filter first.

### Organizations made before 2.7b

An organization created with 2.7a compared trailing spaces, width, kana and `ß`. At its first start on 2.7b its unique text
indexes are rebuilt with the rule above. When existing rows already hold two values that are now equal, that index keeps
the old rule, the organization keeps serving, and `xrmd server doctor` (and `GET /admin/health`) warns:
`Alternate key '<key>' on '<table>' has values that differ only by trailing spaces, width, kana or ß; fix the rows, then run 'xrmd server upgrade'.`
The rebuild is tried again at each start until no rows collide.

## Change tracking

Change tracking works on PostgreSQL with the same messages, delta links and tokens as on SQL Server (see
[Change tracking](change-tracking.md)). Row versions come from one sequence per organization instead of SQL Server's
`rowversion`. A token never lies above a change that is still being written by an open transaction, as with SQL Server's
lowest active row version: a change committed after a token was handed out comes in the next delta, never skipped. A
transaction that stays open holds the token back for every reader until it ends, as on SQL Server.

## Formula and calculated columns

Calculated, formula and rollup columns are computed in SQL on PostgreSQL too, with the same results as on SQL Server for
the recorded cases (rounding half away from zero, Power Fx blank as 0, dates in UTC, rollups over empty sets). Two
differences remain:

| Case | PostgreSQL | SQL Server |
|---|---|---|
| Power Fx `Char(n)` with `n` = 0, or a surrogate code (55296 to 57343) | Empty (null) | A character |
| A formula whose SQL is close to the 300,000-character limit | The PostgreSQL SQL is a few percent longer, so such a formula may be refused on PostgreSQL and accepted on SQL Server | |

## Reference

### Settings

| `.env` key | Configuration key | Meaning |
|---|---|---|
| `STORAGE_PROVIDER` | `Storage:Provider` (rendered as `Storage__Provider` for each organization) | `PostgreSql` for a PostgreSQL installation; absent or empty for SQL Server. Written by `server init --database postgresql`, never changed by `--force` |
| `POSTGRES_PASSWORD` | (the `postgres` service and the organizations' connection strings) | The password of the `postgres` user of the bundled server, applied when the `pg-data` volume is first set up. Required with the bundled server; empty with `--sql-connection` |
| `SQL_CONNECTION` | `ConnectionStrings:Org` (plus `;Database=<org>`) | Your own PostgreSQL server: `Host=…;Username=…;Password=…`, without `Database` |

A non-empty `MSSQL_PID` or `ACCEPT_EULA` in a PostgreSQL `.env` makes every `server` command exit 5 naming the key.
`Startup:SqlRetries` and `Startup:SqlRetryDelaySeconds` apply to PostgreSQL as well: the time the server waits for the
database at startup.

### How data is stored

| Column type | SQL Server | PostgreSQL |
|---|---|---|
| Unique identifier | `uniqueidentifier` | `uuid` |
| Text, multiline text | `nvarchar(n)`, `nvarchar(max)` | `citext` (the length is checked by xrmverse before the write, as on SQL Server) |
| Date and time | `datetime2` | `timestamptz` (microseconds; SQL Server keeps 100-nanosecond units) |
| Decimal, currency | `decimal(p,s)` | `numeric(p,s)` |
| Float | `float` | `double precision` |
| Two options | `bit` | `boolean` |
| Whole number, big integer | `int`, `bigint` | `integer`, `bigint` |
| Binary | `varbinary(max)` | `bytea` |
| Row version | `rowversion` | `bigint` from the organization's sequence `data.versionnumber_seq` |

The database layout (schemas `meta`, `data`, `auth`) is not a contract: read and write data through the APIs.

## Backup and restore

`xrmd org backup` and `org restore` (Admin API `POST /admin/orgs/{database}/backup` and `/restore`) work on PostgreSQL as on
SQL Server (see [Organizations and backups](cli.md#organizations-and-backups)), with these differences:

| Topic | PostgreSQL |
|---|---|
| Backup file | `{db}-{yyyyMMddHHmmss}.dump` (`{db}-{yyyyMMddHHmmss}-{n}.dump` for a second backup of the same database in the same second), written by `pg_dump --format=custom --no-owner --no-privileges` in `Admin:BackupDirectory` (default `/var/lib/xrmd/backups`, the `backups` volume). The path is in the xrmd container, not on the database server. The `.files.tar` is written beside it |
| Client tools | `pg_dump` and `pg_restore` of PostgreSQL 18.7, built from PostgreSQL's source into the xrmd image at `/usr/lib/postgresql/18/bin` (`Storage:PgToolsPath`), with `libpq`; no other PostgreSQL program (no `psql`). PostgreSQL's copyright notice is at `/usr/share/doc/postgresql/COPYRIGHT` in the image. A server you run outside the image needs `pg_dump` and `pg_restore` 16 or later at `Storage:PgToolsPath`, of the same major as the server or newer |
| Writes during a backup | Not blocked: `pg_dump` reads one consistent snapshot |
| Restore | Into a new database (`xrmd_restore_<id>`), which takes the organization's name only when `pg_restore` and the schema upgrade have succeeded. Over an existing database (`--overwrite`) the old one is replaced in one step (renamed `xrmd_replaced_<id>`) and then dropped. A failed restore leaves the existing database as it was |
| Staging databases | `xrmd_restore_*` and `xrmd_replaced_*` databases are hidden: never listed as organizations, and their names cannot be used for one. One left behind by a restore that stopped half way (the process died) is swept, that is dropped, by the next restore through the server (`org restore` or the Admin API), once no session is on it. `org restore --direct` does not sweep them; drop such a database by hand with `DROP DATABASE` |
| Restore under another name | Supported. Ownership goes to the connection's user; the collation, the indexes and the sequences come with the dump |
| Server versions | PostgreSQL 16 and later. `pg_restore` 18 sets settings that a 16 or 17 server does not know; xrmd accepts those refusals only, any other error fails the restore |
| `--direct` | `xrmd org restore --direct --connection-string "Host=…;Username=…;Password=…" --file <local .dump>` runs `pg_restore` from your `PATH` against the server (stop the server first). A dump of the server image is made by `pg_dump` 18 and needs `pg_restore` 18 or later: before asking for confirmation the command reads the dump with `pg_restore --list`, and without a `pg_restore` that can read it it exits 1 with `'xrmd org restore --direct' on PostgreSQL needs pg_restore on PATH, of the PostgreSQL version that made the backup or later: 18 or later for the backups of the xrmd server image (at least 16).` A file that is no dump exits 5. The password is passed in the environment of `pg_restore`, never on its command line |
| Single-user mode | Not used: sessions on the replaced database are ended when it is swapped |

## Known limits

| Limit | Details |
|---|---|
| One engine per server | All organizations of a server use the same engine; an organization cannot be moved from one engine to the other |
| Backups | `xrmd org backup` writes a `pg_dump` custom-format `.dump` (no owners, no privileges) and a `.files.tar` to the server's backup directory. See [Backup and restore](#backup-and-restore). A dump made with other tools or options is not supported |
| `pg_restore` for `--direct` | A backup of the server image is made by `pg_dump` 18: `org restore --direct` needs `pg_restore` 18 or later on your `PATH`, and checks it with `pg_restore --list` before it asks for confirmation |
| `like` with ranges | Refused; sets work. See [Text: case, accents and sorting](#text-case-accents-and-sorting) |
| `like` with width, kana and `ß` | `like`, `begins-with` and `ends-with` do not ignore width or kana type and do not treat `ß` as `ss`, unlike SQL Server; equality does |
| Sorting of punctuation | May differ from SQL Server |
| `distinct` on text | Reads every matching row through a window function, so it costs more than on SQL Server on large results |
| Names | Table and column names longer than 63 bytes (UTF-8) are refused with `The <table or column> name '<name>' is longer than the 63 bytes PostgreSQL allows for a table or column name.`; organization names longer than 63 bytes with `The database name '<name>' is longer than the 63 bytes PostgreSQL allows.` |
| Formula columns | `Char` differs at the edges, and a formula near the SQL length limit may be refused. See [Formula and calculated columns](#formula-and-calculated-columns) |
| Date precision | Times keep microseconds, not 100-nanosecond units |
| Table locks | Some writes take a table lock that SQL Server does not need: removing solution components (uninstall, the cascade delete of a solution) locks `solutioncomponent` against other writers, and publishing or unpublishing duplicate detection rules locks `duplicaterule`. Readers are not blocked; concurrent writers wait. Two transactions that both write rules and publish them can deadlock; one of them fails and can be retried |
| Order of duplicates | `RetrieveDuplicates` and the duplicate check order candidates by GUID, and the two engines sort GUIDs differently: with more matches than the page, the two engines can return different subsets |
| Elastic tables: JSON edge cases | An object with a key twice keeps the last value (SQL Server sees both); a value with `\u0000` or a lone surrogate escape (`\ud800`) is treated as not JSON; range conditions on JSON strings compare UTF-8 bytes (`COLLATE "C"`), where SQL Server compares UTF-16 code units (`BIN2`): the order differs only between characters above U+FFFF and those from U+E000 to U+FFFF |
| Tests | Every integration test runs on both engines: on SQL Server on every pull request, on PostgreSQL every night and on demand. Run the PostgreSQL tests of the area you change before you open a pull request (see [Development](development.md#tests-on-postgresql)) |
| Apple silicon | The `xrmd` image is `linux/amd64` only and runs under emulation there, as with SQL Server |

## See also

- [Installing xrmverse](install.md), [Multiple organizations](multiple-organizations.md)
- [Configuration](configuration.md)
- [Supported queries](queries.md), [Change tracking](change-tracking.md), [Formula columns](formula-columns.md)
- [xrmverse compared with Dataverse](dataverse-comparison.md)
- [Known issues and limitations](known-issues.md)
