# CLI xrmd

[← xrmverse documentation](../README.md#documentation)

## Introduction

`xrmd` is the command line for installing, administering and scripting an xrmverse server. Every command goes through
the server's Web API, Admin API or MCP endpoint **as the signed-in user**: the CLI has no privileged path.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Role | installs, administers and scripts an xrmverse server | Differs: plays the part of `pac` and the admin center for xrmverse; `pac` itself still works for solutions |
| Identity | every command runs as the signed-in user, through the Web API, Admin API or MCP | Same security as any client: no privileged path |
| Installation commands | `xrmd server` sets up Docker Compose, certificates and organizations | Extra: Dataverse is a hosted service |
| Destructive commands | confirmation, dry runs, `--cascade` only through the Admin API | Extra safety |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Install the CLI | `dotnet tool install --global xrmd` or a release executable | [Install](#install) |
| Set up a server | `xrmd server init`, `up` | [Set up a server](#set-up-a-server) |
| Upgrade the server | `xrmd server upgrade --to latest` or `--to <version>` | [Set up a server](#set-up-a-server) |
| Sign in | `xrmd auth login` | [Profiles and authentication](#profiles-and-authentication) |
| Back up an organization | `xrmd org backup` | [Organizations and backups](#organizations-and-backups) |
| Create tables, register plug-ins, move data | `table`, `plugin`, `data`, `apply` | [Tables, plug-ins, data and schema files](#tables-plug-ins-data-and-schema-files) |
| Script in CI | `--output json` and the exit codes | [Exit codes](#exit-codes) |
| Let an AI agent use the CLI | the Claude Code plugin | [Skills for coding agents](agent-skills.md) |

## Install

```bash
dotnet tool install --global xrmd              # from a nupkg: --add-source <dir>; requires the .NET 10 runtime
```

Self-contained single-file executables (`win-x64`, `osx-arm64`, `osx-x64`, `linux-x64`) are attached to every GitHub
release of a `v*` tag, together with the `xrmd` nupkg; `packaging/homebrew/xrmd.rb.template` is the Homebrew formula
template used for the macOS archives. Nothing is published to nuget.org.

## Command groups

| Group | Purpose |
|---|---|
| `auth` | `login`, `status`, `logout`: profiles and tokens |
| `idp` | `user set-password/totp enable|disable/remove`, `client add/list/remove`: the local identity provider (`Auth:Mode=Local`) |
| `server` | `init`, `up`, `down`, `status`, `doctor`, `upgrade`, `versions`, `logs`, `org add`, `org remove`: the Docker Compose installation and its organizations |
| `org` | `create`, `list`, `delete`, `backup`, `restore`: organization databases (Admin API) |
| `user`, `role` | `user add/list`; `role list/assign` |
| `publisher` | `create`, `list`: the publishers whose prefixes custom names start with (see [Publishers and the prefix rule](#publishers-and-the-prefix-rule)) |
| `logs` | the server's recent log entries (`--since`, `--level`, `--category`, `--take`, `-f`) |
| `table` | `create` (`--activity`, `--notes`, `--activities`), `list`, `describe`, `set <table> --notes\|--activities`, `delete`, `column`, `relationship`, `choice` |
| `solution` | `create`, `import`, `export`, `list`, `add-component`, `publish`, `delete` |
| `plugin` | `register`, `list`, `step add [--type\|--webhook]`, `step remove`, `image`, `trace` |
| `webhook` | `create --name --url --auth key\|header\|query --value`, `list`, `show <name>`, `update <name> [--url] [--auth --value]`, `delete <name> --yes`: webhooks (service endpoints of contract 8) over the Web API; see [Webhooks](webhooks.md) |
| `data` | `query` (FetchXML or OData), `export`, `import` |
| `migrate` | `export`: read a Dataverse environment into an encrypted package (resumable); `inspect`: verify a package offline; `users`: write the users mapping template; `import`: upload a package, start the run and follow it; `status`, `report` (md, json, csv), `cancel`, `finalize`, `activate`: a run; `run`: export then import in one command; see [Migrating from Dataverse](migration.md) |
| `customapi` | `list`, `show <uniquename>`, `create --file [--solution]`, `delete <uniquename>`: custom API definitions over the Web API |
| `audit` | `status`, `enable`, `disable` (organization, `--table`, `--table --column`), `retention <days>\|--forever`, `history <table> <id> [--column] [--top]`, `delete --table --id\|--before <date>`: auditing over the Web API |
| `fieldsecurity` | `profiles`, `profile create\|delete`, `permission set\|remove <profile> <table> <column> [--read] [--create] [--update]`, `assign\|unassign <profile> --user\|--team`, `secure\|unsecure <table> <column>`: field-level security over the Web API |
| `hierarchy-security` | `show`, `enable`, `disable`, `depth <n>`, `exclude <table>`, `include <table>`: the manager hierarchy security model over the Admin API (System Administrators; `depth` is 1 to 100) |
| `access-team` | `template list [--table]`, `template create --table --name --rights read,write,... [--description]`, `template delete <id> [--yes]`, `enable\|disable <table>`, `add\|remove --table --record --user --template`: access team templates and the access teams of records over the Web API (see [Access teams](#access-teams)) |
| `change-tracking` | `status [--table]`, `enable <table> [--yes]`, `retention <days>`: change tracking over the Web API (there is no `disable`: tracking is one-way) |
| `duplicate-detection` | `status`, `enable\|disable [--create-update] [--table <table>]`, `rule list\|create\|publish\|unpublish\|delete`, `find --table --id [--matching]`, `bulk --table [--name] [--wait]`: duplicate detection over the Web API |
| `currency` | `list`, `create <code> --name --symbol --rate [--precision] [--custom]`, `rate <code> <rate>`, `activate\|deactivate <code>`, `pricing-precision <0-4>`: currencies over the Web API |
| `envvar` | `list`, `get <schemaname>`, `set <schemaname> <value>`, `unset <schemaname>`: environment variable values over the Web API; see [Environment variables](environment-variables.md) |
| `workflow` | `apply <file.yaml> [--solution] [--activate] [--dry-run]`, `export <nameOrId> [--format yaml\|xaml] [--file]`, `list [--kind] [--entity]`, `activate\|deactivate <nameOrId>`: workflows, business rules and actions as YAML over the authoring API; see [Workflow authoring as code](workflow-authoring.md) |
| `queue` | `list [--public]`, `create <name> [--private]`, `member add <queue> <user\|team>`, `items <queue>`: queues over the Web API; see [Email and queues](email-and-queues.md) |
| `dev` | `seed <table>`: sample rows |
| `apply <schema.yaml>` | applies a schema file (tables, columns, choices, relationships) |
| `mcp` | stdio MCP bridge to the server's `/mcp` endpoint (see [MCP](mcp.md)) |
| `sdk` | `install`, `status`: the Microsoft SDK assemblies, which you install yourself (see [Installing the Microsoft SDK](microsoft-sdk.md)) |

Run `xrmd <group> --help` for the options of each command. Every command accepts `--output json|table` (default
`table`; with `json` only JSON goes to stdout and diagnostics to stderr), `--url` (overrides the profile's URL) and
`--profile`. Response files are disabled: `@file` is never expanded by the parser; `data query --fetch @file` reads
the FetchXML from that file.

## Set up a server

```bash
xrmd server init --dir ./xrmd --org acme     # writes docker-compose.yml and .env with generated secrets; dev profile "acme"
                                             # (asks you to accept the SQL Server license terms; --accept-sql-server-eula)
xrmd sdk install --dir ./xrmd                # the Microsoft SDK into ./xrmd/sdk (license prompt), if init did not install it
xrmd server up --dir ./xrmd                  # exit 1 with this command when ./xrmd/sdk/sdk.json is missing
export XRMD_DEV_SIGNING_KEY=$(grep '^AUTH_DEV_SIGNING_KEY=' ./xrmd/.env | cut -d= -f2)
xrmd server status --dir ./xrmd                 # http://localhost:5001/
```

`server init` (`--org` repeatable, `--auth dev|entra|local`, `--port` (default 5001), `--tenant-id`, `--admin-object-id`
(one per `--org`, in the same order), `--sql-connection`, `--sql-edition` (default `Express`, see
[SQL Server editions](sql-server-editions.md)), `--accept-sql-server-eula` (you accept the bundled SQL Server's license
terms, written as `ACCEPT_EULA=Y`; without it init asks at a terminal, see
[License terms](sql-server-editions.md#license-terms-accept_eula)), `--accept-microsoft-license` (installs the SDK into `<dir>/sdk` now); `--force` overwrites, keeping the `SA_PASSWORD` and dev signing
key of an existing `.env`) generates the SQL Server password and,
in dev mode, the signing key, and creates one dev profile per organization. `server org add|remove` change the set of
organizations; see [Multiple organizations](multiple-organizations.md) for the `.env` keys, ports, profiles, the
external SQL Server and the single-instance guard. Notes about the generated files:

- The compose file has a `backups` named volume (a Docker volume, not a host folder) mounted at
  `/var/opt/mssql/backup` in the SQL Server container and in every `xrmd-<org>` container; `org backup` and
  `org restore` use that directory. A one-shot `backups-init` service `chown`s the volume to the `mssql` user
  (uid 10001) before SQL Server starts: Docker creates the volume root-owned and SQL Server runs as non-root, so
  without it `BACKUP` fails with "Access is denied". To get a backup file out, use
  `docker compose cp sqlserver:/var/opt/mssql/backup/<file> .`. With `--sql-connection` there is neither `sqlserver`
  nor `backups-init` (see [Multiple organizations](multiple-organizations.md), External SQL Server).
- With `--auth local`, each organization's one-time administrator password is written to `<dir>/secrets/<org>/admin-password`; the `xrmd-<org>` container mounts only its own `secrets/<org>` folder (a one-shot `secrets-init` service gives those folders to the server's group), and `server org remove` deletes the folder.
- The xrmd image is `XRMD_IMAGE` in `.env` (required by the compose file, never `latest`). `server init` writes
  `ghcr.io/pwbit/xrmd:<version of the CLI>`; a development CLI (no release version) writes `xrmd:local` and
  prints the build command (`docker build -f docker/Dockerfile -t xrmd:local .` from the repository root). `--image` chooses
  another one, and `server upgrade --to <version>` moves the tag before pulling. `server up`/`upgrade` add the key, with a
  notice, to a `.env` written before it existed.
- `server upgrade --to latest` (or `--latest`) asks GitHub for the newest release of `pwbit/xrmverse` (and of
  `pwbit/xrmverse-releases`, which holds the releases up to 0.1.10), pre-releases included, and continues as `--to <that version>`: `.env` always gets an exact version, never `latest`. It
  stops before changing anything (exit 5) when that release is newer than the CLI: install that CLI first, or pass
  `--allow-cli-mismatch`. Offline or when GitHub does not answer it exits 6 (1 for an error answer) and asks for
  `--to <version>`. `server versions` lists the releases, newest first, and marks the version in `.env` (`installed`)
  and the CLI's (`cli`). Both send two anonymous requests to `api.github.com`, only when you run them.
- Each organization's audience is `http://localhost:<port>/` unless `ORG_<ORG>_AUDIENCE` is set. With Entra the
  audience **must** equal the API's Application ID URI (the audience of the tokens): set `ORG_<ORG>_AUDIENCE` when
  that is not the organization's URL, and `AUTH_TENANT_ID` to the tenant GUID (see [Development token](getting-started.md#development-token-auth_modedevelopment)).
- The ports are bound to `127.0.0.1`; set `XRMD_BIND=0.0.0.0` in `.env` to expose them.
- `--domain`, `--public-host`, `--tls`, `--acme-email`, `--tls-cert`, `--tls-key`, `--no-proxy`, `--trusted-proxy`,
  `--expose-ports` and `--edge-subnet` give the organizations public host names on 443 behind a generated Caddy proxy
  or your own: see [Installing xrmverse](install.md) and [Multiple organizations](multiple-organizations.md#host-names).
- `--tds-port`, `--tds-cert` and `--tds-key` give the organizations a read-only SQL endpoint (see [SQL endpoint](sql-endpoint.md)):
  - `init --tds-port <port>` (with `--auth local`, and a public host for each organization) gives the first organization
    that host port and the next ones the next ports (`ORG_<ORG>_TDS_PORT`, published on `XRMD_TDS_BIND`, default every
    address); `org add <name> --tds-port <port>` gives one organization its own. Other authentication modes, an
    organization without a host, ports 80 and 443 behind the generated proxy, and a port another organization uses are
    refused (exit 2).
  - With `--no-proxy`, `--tds-cert` and `--tds-key` are required (a PEM certificate covering every host and its
    unencrypted key, copied to `<dir>/certs/tds.crt` and `tds.key`); without `--no-proxy` or `--tds-port` they are refused. Behind your own proxy, `server up` and `server upgrade` exit 5 when `certs/tds.crt` or `certs/tds.key` is missing.
  - A long-running `tds-certs` service in the compose file copies each organization's certificate into `<dir>/tds/<org>/`
    at start, then sleeps and copies again every 12 hours (ACME), every hour (`--tls internal`) or every 5 minutes (a pair in `certs/`).
  - `server status` adds the TDS port and the certificate expiry of each organization (`tdsPort`,
    `tdsCertificateExpires` in JSON), and `server doctor` the `tds-certificate` check.

`server status` and `server doctor` call `/admin/health` and `/admin/doctor`, which are admin-only: they need a
profile like any other command. In an installation directory (`--dir`, default `.`) they query every organization
through its own profile; otherwise the current profile's server. `server doctor` exits 1 when a check is an error; its
`fileStore` check confirms the file store is reachable (see [Files and images](files-and-images.md#checking-the-file-store)).

## Profiles and authentication

Profiles live in `config.json` in `~/.xrmd` (override with `XRMD_CONFIG_DIR`); the directory is created with mode 0700
and its files with 0600 on Unix. A profile holds the URL, mode and non-secret identifiers only. `--url` replaces the
profile's URL for one command (a profile is still needed for the token). A dev profile of an installation with
`TLS_MODE=internal` also holds `caFile`, the root of Caddy's internal CA exported by `server up`: the CLI trusts it for
that profile's host only (with `--url` on another host it is not used). Profiles created by `auth login` get none.

| `auth login --mode` | Use | Credentials |
|---|---|---|
| `dev` | `AUTH_MODE=Development` servers | HS256 token signed with the key in the environment variable named by `--signing-key-env` (default `XRMD_DEV_SIGNING_KEY`); `--object-id` is the `oid` claim |
| `device-code` | Entra, interactive, headless-friendly | `--client-id`, optional `--tenant-id` (default `organizations`) |
| `browser` | Entra, interactive with a browser | as above; register the redirect URI `http://localhost` as a mobile/desktop platform URI |
| `client-secret` | Entra, unattended | `--client-id`, `--tenant-id`, secret in `XRMD_CLIENT_SECRET` (tokens stay in memory) |

`--idp entra|local` (default `entra`) chooses the identity provider of `device-code`, `browser` and `client-secret`. With `--idp local` the
authority is the server's own, `<url>adfs/`, there is no `--tenant-id`, and `--client-id` defaults to `xrmd-cli` for `device-code` and `browser`
(`client-secret` still needs the id of a client made by `xrmd idp client add`). `--ca-file <pem>` (only with `--idp local`) trusts that root
certificate for the server's host only, for a host behind Caddy's internal CA (`<dir>/certs/caddy-root.crt`); a later login of the same
profile keeps it. See [Local identity provider](local-idp.md).

### Where tokens are kept

The MSAL token cache (`msal-cache.bin`, used by `device-code` and `browser`) is stored **in clear text, unencrypted,
in the config directory** (mode 0600 on Unix; no keychain or DPAPI integration). Anyone who can read that file can
use the cached tokens: protect the directory, and use `xrmd auth logout` on shared machines. Secrets (signing key,
client secret) are never written to `config.json`; they are read from the environment at use time.

## Local identity provider (`idp`)

For servers with `Auth:Mode=Local` (`server init --auth local`); see [Local identity provider](local-idp.md). Every command needs a profile of an administrator. A `<user>` is a GUID (`systemuserid` or object id) or a domain name.

| Command | What it does |
|---|---|
| `idp user set-password <user> --password-stdin` | Sets the local password (at least 12 characters, one line from standard input), creates the credential, clears a lockout |
| `idp user totp enable <user>` | Gives the user a TOTP key and prints its `otpauth://` URI once |
| `idp user totp disable <user>` | Turns TOTP off |
| `idp user remove <user>` | Removes the local credential (the user stays) |
| `idp client add --name <name> --app-user <user>` | Creates a confidential client for client credentials, bound to that user; prints the secret once |
| `idp client add --name <name> --public --redirect-uri <uri>...` | Creates a public client (authorization code with PKCE) |
| `idp client list` | Lists the clients; secrets are never shown |
| `idp client remove <client-id>` | Deletes a client (`xrmd-cli` and `xrmd-web` cannot be deleted) |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | unexpected error, server 5xx, or a declined confirmation ("Aborted.") |
| 2 | usage error: invalid command line, or a local input problem (missing file such as the compose file or `.env` of `server` commands, a table that has no entity set) |
| 3 | authentication: 401, 403, no profile, a profile that does not exist, no token, a rejected SQL login (`org restore --direct`) |
| 4 | not found on the server (404, or a name the server does not know) |
| 5 | rejected by the server (400, 409, 412) |
| 6 | server unreachable, or a `--wait` timeout on a system job that keeps running (duplicate detection publish and bulk detect) |
| 7 | `org restore` finished, but the restored database references files the file store does not hold (FilesIncomplete; listed on stderr) |
| 8 | a migration package that cannot be read or trusted: not a package, an unknown format version, or entries that fail verification (listed on stderr) |

Columns that are settable on create but whose values are not plain data (types the CLI cannot read, image, file, the
party lists of activities) are never exported or seeded: `data export` and `dev seed` print "Skipped column ..." on stderr
for each, and `data import` of a file naming one exits 2.

Destructive commands (`solution delete` with and without `--cascade`, `table delete`, `table column remove`,
`plugin step remove`, `webhook delete`, `server down --volumes`, `org delete`, `org restore --overwrite`) ask for confirmation;
`--yes` skips the question. Without a terminal on stdin and without `--yes` they exit 2 with "Refusing to run a
destructive command without --yes when stdin is not a terminal."

## Organizations and backups

- `org create <name> --admin-object-id <guid>` creates a database (the organization name is the database name), runs
  the migrations and seeds the first System Administrator.
- On PostgreSQL the backup is a `.dump` of `pg_dump` in the xrmd container's backup directory, and `--direct` runs
  `pg_restore` from your machine on a local `.dump`: see [PostgreSQL organizations](postgresql.md#backup-and-restore).
  The rest of this section applies to both engines unless it names SQL Server.
- `org backup <database>` writes a `COPY_ONLY` backup and returns **a path on the SQL Server host** (in the
  `backups` volume of the generated compose file). `org restore <database> --file <path>` takes such a **server-side
  path**, not a local file; add `--overwrite` to restore over an existing database.
- The backup also writes `<name>.files.tar` beside the `.bak`: every blob of the organization's file and image columns
  (read through the configured file store, file system or S3; staged uploads excluded). The xrmd container mounts the
  same `backups` volume at the same path; the volume is owned by `mssql:1654` with mode `2770` and xrmd runs as
  `1654:1654` (the aspnet image's `APP_UID`), because SQL Server (uid 10001) and xrmd both write it. `org restore` needs that archive: without it the restore stops
  before touching SQL Server, unless you pass `--no-files` (Admin API: `"files": false`), which restores the database
  only and leaves the target's blobs as they are. With the archive, the target's blobs are replaced by the archive's
  (also when restoring under another database name). If the blobs fail after the database was restored, the error says
  so; repeat the restore with `--overwrite`. `--direct` restores the database only and requires `--no-files`.
- The database and the archive are not one atomic snapshot: a file deleted or replaced between the SQL backup and the
  archive is referenced by the `.bak` but absent from the archive. After every restore (also with `--no-files`) each
  `fileattachment` row of the restored database is checked against the store. If blobs are missing, the Admin API answers
  200 with `missingCount`, `missingFiles` (id, entity, attribute, record; the first 1000) and a `warning`, the server logs
  a warning, and `org restore` prints them on stderr and **exits with code 7** (the database is restored anyway).
- `Admin:FilesBackupDirectory` is the archive directory as xrmd sees it; it defaults to `Admin:BackupDirectory`. Set it
  when xrmd reaches the backup folder under another path than SQL Server does (for example SQL Server on another host with
  a shared folder mounted differently); `org backup` reports the archive at this path.
- `org restore --direct --connection-string <...> --overwrite` runs the restore from your machine against SQL Server
  instead of through the Admin API, for the organization the server serves: stop the server first. It restores to the
  same database name only. Without `--overwrite` it fails (exit 5) if the database exists; with `--overwrite` it
  replaces it. A rejected SQL login exits 3, an unreachable SQL Server 6.
- A restore over an existing database (Admin API or `--direct`) sets it `SINGLE_USER` first and back to `MULTI_USER`
  afterwards. If a failed restore also leaves that revert failing, the error (server) or a stderr warning (CLI, exit
  code unchanged) says the database may still be in `SINGLE_USER` mode and gives the `ALTER DATABASE ... SET
  MULTI_USER` statement to run.
- `org create` of a name that already exists, including one created concurrently, is a 409 (exit 5); a create that
  fails later drops only the database it created itself.

## Publishers and the prefix rule

As in Dataverse, the name of every custom component you create (table, column, relationship, choice) is
`{prefix}_{name}`, where `{prefix}` is the customization prefix of an existing publisher. The rule applies on every path
that creates a component: the SDK messages, the Web API, `xrmd table`, `xrmd apply` and the solution import. The
publisher of the solution being imported counts as existing. The rule checks the prefix only, not that it is the prefix
of the publisher of the solution you add the component to (Dataverse accepts any existing publisher's prefix). The
prefix is compared exactly, so `PB_x` is refused when only `pb` exists, and so are `pb_` (no name after the prefix) and
`pbx_y`. A refusal is a 400 that names the prefixes that exist. The names of the system tables and columns, and the
names the server generates (the `_base` column of a money column, file and image companions, the relationship implied by
a lookup, the intersect table of a many-to-many relationship), are exempt. Every organization has the default publisher
(prefix `new`); to use another prefix, create its publisher first.

```bash
xrmd publisher create --unique-name acme --friendly-name "Acme" --prefix ac --option-value-prefix 12000
xrmd publisher list
```

The prefix is 2 to 8 lowercase letters or digits, starting with a letter and not with `mscrm`; the option value prefix is
10000 to 99999 (invalid values are exit 2, nothing is sent). A unique name or prefix that a publisher already has is
refused by the CLI with exit 1 (the server itself does not check this when you create a `publisher` row through the Web
API or the SDK; a server-side check is in the backlog). The `publisher:` of a schema file given to `xrmd apply` must be the prefix of an existing publisher:
otherwise the apply (a `--diff` too) fails before any change, with a hint to run `xrmd publisher create`.

## Tables, plug-ins, data and schema files

- `table`, `plugin` and `solution` commands call the Web API; `--solution <uniquename>` puts a created component in a
  solution. `plugin register` inspects the assembly locally, so no SDK needs to be installed.
- `solution import --async` queues the import as a system job and polls it every 2 s. Up to 5 consecutive failed
  reads of the job (server unreachable, 5xx, 404) are retried; 401/403 stop at once. `--timeout <minutes>` (default
  60) bounds the wait: past it the command exits 6 with "Import still running ... check asyncoperation <id>"; the
  import itself keeps running on the server and is not reported as failed. A failed import prints the import job's
  log and exits 5.
- `data import <table> --file f --format csv|json` creates one row per request. `--batch` (default 100) is the
  **number of rows in flight, with no ordering guarantee**: for a file where parents must exist before children, use
  `--batch 1`. In CSV, a lookup column is written as `table:guid`. A failing row never affects the others; a row the
  server chose as a deadlock victim (rolled back, 0x80044150) is sent again, up to three tries in all (`dev seed` writes
  the same way); failed rows are listed on stderr with their row numbers and the exit code is 5. `data export` produces files that `data import`
  accepts.
- `apply <schema.yaml>` always sends `dryRun` explicitly to the server. `--diff` is a dry run: it prints the plan
  (`+` change, `=` extra, `!` rejection) and changes nothing. Anything the server rejects makes the command exit 5.
  The dry run executes the plan in a transaction that is always rolled back, so a value the server refuses (e.g. a
  `maxLength` out of range) is a `!` rejection in `--diff` exactly as in the real apply.
  The schema format is additive: tables, columns, choices and relationships; unsupported options are rejected with a
  400 naming them, never ignored.
- Autonumber columns: `autoNumberFormat` on a `string` column or on `primaryName` (see [Autonumber columns](autonumber-columns.md));
  `apply` creates it and changes a different one, and the schema export (`GET /admin/schema`) writes it.
- File and image columns: schema type `file` (option `maxSizeInKB`) and `image` (`maxSizeInKB`, `canStoreFullImage`,
  `primary: true`, at most one per table; `primary` is only ever true), and
  `table column add --type file|image [--max-size-in-kb <n>] [--can-store-full-image] [--primary]`. As in Dataverse, a
  create ignores `IsPrimaryImage = true` when the table already has an image column (the first image column is the
  primary one) and only an update moves the flag: `apply`, `--primary` and a solution import create the column, then
  update it. An import whose primary image is not created (e.g. the system `entityimage`) says so in its log. Their companion
  columns (`<file>_name`, `<image>id`, `<image>_timestamp`, `<image>_url`) and the `{table}_FileAttachments`
  relationship are the server's: schema files and solutions never carry them, and an import that finds them in a
  file creates them only with their main column.
- Activities and notes (see [Activities and notes](activities.md)): `table create <table> --activity` creates a custom
  activity (primary column `subject`, label "Subject", user owned, notes on; `--primary-name` with it exits 2, and
  `--activities` or `--ownership organization` with it are refused by the server, exit 5). `--notes` and `--activities`
  on `table create` switch `HasNotes` and `HasActivities` on. `table set <table> --notes|--activities` switches them on for
  an existing table: it asks for confirmation (`--yes` skips it and is needed without a terminal), reports a setting that
  is already on and sends nothing for it, and cannot switch one off (there is no `--no-notes`; `--notes false` exits 2);
  a refusal of the server (for example `--activities` on an activity table) exits 5. `table describe` shows `isActivity`,
  `hasNotes` and `hasActivities`. In schema files the keys are `isActivity`, `hasNotes` and `hasActivities` on a table;
  an activity must have `primaryName: { name: subject }`, and `hasActivities: true`, `hasNotes: false` or ownership
  `organization` on it are format errors. `apply` switches a flag on with an update, refuses to switch one off or to
  change `isActivity`, and the schema export (`GET /admin/schema`) writes a flag only when it is true. `data export` and `dev seed` skip party list
  columns with a line that names the party navigation (`email_activity_parties`); write parties with the SDK or the Web
  API.
- Customer columns: schema type `customer` (no `target`: it always refers to `account` and `contact`) and
  `table column add <table> <name> --type customer`. Both call `CreateCustomerRelationships`, the only way to create a
  Customer column, with two relationships named `{table}_{column}_account` and `{table}_{column}_contact` (navigation
  properties `<column>_account` and `<column>_contact`, the default cascades Delete RemoveLink and Merge Cascade). `apply` then `export` returns the
  same `customer` column; other multi-target lookups are still refused by `export`. `dev seed` binds a Customer column to
  an existing account or contact, and `data import` writes it as `account:guid` or `contact:guid`. Example:

  ```yaml
  tables:
    - name: xv_deal
      label: Deal
      primaryName: { name: xv_name }
      columns:
        - { name: xv_customerid, type: customer, label: Customer }
  ```

- Webhooks: `xrmd webhook create --name <n> --url <u> --auth key|header|query --value <v>` (for `header` and `query`, `--value K=V` is
  repeatable; the CLI builds the settings XML of the `authvalue`) prints the id, or the record with `-o json`. `webhook list` shows
  name, url without its query string, auth type and step count; `webhook show <name>` adds the steps and `isauthvalueset`; neither
  ever prints an auth value. `webhook update <name> [--url] [--auth --value]` changes the url and/or the authentication, and
  `webhook delete <name> --yes` deletes the endpoint, its steps and their images; queued jobs are cancelled (3/32). `plugin step add --webhook <name>`
  binds a step to a webhook instead of a plug-in type (exactly one of `--type` and `--webhook`; `--config` is refused with
  `--webhook`), and `plugin list` shows webhook steps under a "Webhooks" heading. Exit 4 for an unknown webhook, 5 for a server refusal,
  2 for a usage error. See [Webhooks](webhooks.md).

## Custom APIs

The CLI reads, creates and deletes custom APIs (a solution import adds them too; see [Custom APIs](custom-apis.md)):

```bash
xrmd customapi list [-o json]            # unique name, binding (global, entity:<table>, collection:<table>), kind, private, plug-in type, parameter and property counts
xrmd customapi show xt_Echo [-o json]    # the definition with each request parameter (type, optional, table) and response property
xrmd customapi create --file ping.json [--solution MySolution] [-o json]
xrmd customapi delete new_Ping [--yes]
```

`create` reads a JSON file: the `customapis` Web API body, plus the arrays `requestparameters` and `responseproperties`
with the Web API bodies of the fields (without `customapiid`), and an optional `"plugintype": "<typename>"`, which the CLI
resolves to `plugintypeid@odata.bind`:

```json
{
  "uniquename": "new_Ping", "name": "Ping", "displayname": "Ping",
  "bindingtype": 0, "isfunction": false, "allowedcustomprocessingsteptype": 0,
  "plugintype": "Acme.Plugins.PingPlugin",
  "requestparameters":  [ { "uniquename": "Text", "name": "Text", "displayname": "Text", "type": 10, "isoptional": false } ],
  "responseproperties": [ { "uniquename": "TextOut", "name": "TextOut", "displayname": "TextOut", "type": 10 } ]
}
```

It posts the API, then each parameter and property, with `--solution` as the `MSCRM.SolutionUniqueName` header on every
request, and prints the new API id (or `customapiid`, `uniqueName` and the field counts with `-o json`). The server has no
deep insert, so these are separate requests: if one is refused, the CLI deletes the API again (the delete removes the fields
already created), prints the server's message and exits with the code of the failure (5 for a 400). If the CLI is killed
in between, `xrmd customapi delete <uniquename>` removes the rest. `delete` finds the API by unique name and asks for confirmation (`--yes`/`-y` skips it; without a terminal it is required, exit 2); the server
refuses it while a step is registered on the API's message, which exits 5 with the server's message. An unknown API name, or a plug-in type that is not registered,
exits 4 (not found). The rules the server applies are in [Authoring](custom-apis.md#authoring).

## Workflows as code (`workflow`)

`xrmd workflow` applies and exports classic workflows, business rules and actions as YAML (see
[Workflow authoring as code](workflow-authoring.md) for the format). The commands call the authoring API and run as the profile's
user, so the Dataverse privileges decide.

- `apply <file.yaml> [--solution <unique name>] [--activate] [--dry-run]` reads the file and checks its YAML locally first: an
  error there exits 2 and prints `path:line:column: message`, before any request. The server compiles the file and creates or
  updates the process by its identity; a new one is Draft, an existing one keeps its state, and a file that changes nothing
  prints "unchanged" and writes nothing. `--dry-run` compiles and reports without writing. The assumed shapes the file uses are
  printed on stderr as `warning: <shape>`. A refused document prints every error (with line and column) and exits 5.
- `export <nameOrId> [--format yaml|xaml] [--file <path>]` writes the process as YAML (the default) or as its stored XAML, to
  stdout or to the file. The option is `--format`, not `--output`, which stays `table` or `json` for every command. A process
  that has no YAML form is refused with every reason (exit 5).
- `list [--kind workflow|businessRule|action] [--entity <table>]` prints id, name, kind, table, mode, state and the number of
  unsupported steps.
- `activate <nameOrId>` and `deactivate <nameOrId>` change the state; they need the activation privileges of
  [Security](security.md#classic-workflows).

A name that contains `/` must be given by its id (the command refuses it locally with exit 2). Exit codes are those of the
other groups: 3 authentication or missing privilege, 4 unknown name or id, 5 rejected.

## Access teams

`xrmd access-team` manages access team templates and puts users on a record's access team (see
[Access teams](security.md#access-teams)). It runs over the Web API as the signed-in user, so the server's privilege checks
apply (templates need the `teamtemplate` privileges, which System Administrators hold).

```bash
xrmd access-team enable account                        # AutoCreateAccessTeams on (user- or team-owned tables only)
xrmd access-team template create --table account --name "Deal reviewers" --rights read,write
xrmd access-team template list --table account
xrmd access-team add --table account --record <accountid> --user jane@contoso.example --template "Deal reviewers"
xrmd access-team remove --table account --record <accountid> --user jane@contoso.example --template "Deal reviewers"
xrmd access-team template delete <templateid> --yes    # asks first without --yes; deletes the template and its access teams
xrmd access-team disable account                       # refused while a template for the table exists
```

- `--rights` is a comma-separated list of `read`, `write`, `append`, `appendto`, `delete`, `share` and `assign`; an unknown
  right exits 2 before any request.
- `--user` is a system user id, an Entra object id or a domain name; `--template` is a template id or its name (unique for the
  table; two templates with the same name exit 5). A name is looked up by reading the `teamtemplate` table, which needs
  `prvReadTeamTemplate`: only System Administrators hold it in the standard roles, so anyone else gets 403 (exit 3) with a
  name and must pass the template's id, which `add` and `remove` use without reading the template.
- `add` and `remove` print the access team's id (`accessTeamId` with `-o json`); `remove` prints the empty id when the record
  has no team for the template yet.
- `template delete` also deletes the access teams made from the template, so it asks for confirmation; `--yes` skips the
  question (and is required when stdin is not a terminal).
- Server refusals are shown as they are: 403 exits 3, an unknown table, template or user exits 4, an invalid mask, a limit or
  any other 400 exits 5 (see [Exit codes](#exit-codes)).

## Queues

`xrmd queue` lists and creates queues, adds users or teams to a queue, and lists a queue's items (see
[Email and queues](email-and-queues.md)). It runs over the Web API as the signed-in user, so the server's privilege checks apply.

```bash
xrmd queue create "Support"                   # a public queue; add --private for a private one
xrmd queue list --public
xrmd queue member add "Support" jane@example.com
xrmd queue items "Support"
```

- A queue is named by its name or its id. A name that matches several queues exits 5; use the id.
- An unknown queue, user or team exits 4 (not found). Any other refusal is the server's, shown as it is.

## Change tracking

`xrmd change-tracking` switches change tracking on for a table and sets how long deleted rows are kept (see
[Change tracking](change-tracking.md)). It runs over the Web API as the signed-in user, so the server's privilege checks apply
(changing table metadata, and the organization row for `retention`).

```bash
xrmd change-tracking status                 # tracked tables and the retention
xrmd change-tracking status --table account # whether the table is tracked and whether it can be
xrmd change-tracking enable account         # asks first; --yes skips the question
xrmd change-tracking retention 14           # days the deletion log is kept, 0 to 365
```

- `enable` cannot be undone and there is no `disable`, so it asks for confirmation; `--yes` skips the question and is
  required when stdin is not a terminal. On a table that is already tracked it says so and changes nothing.
- `retention` sets `organization.expirechangetrackingindays`; a value outside 0 to 365 exits 5.
- `-o json` is available on all three. Server refusals are shown as they are: 403 exits 3, an unknown table exits 4, a table
  that cannot be tracked (an intersect table, `audit`, …) or a retention out of range exits 5 (see [Exit codes](#exit-codes)).

## Duplicate detection

`xrmd duplicate-detection` switches duplicate detection on, manages rules and looks for duplicates (see
[Duplicate detection](duplicate-detection.md)). It runs over the Web API as the signed-in user, so the server's privilege
checks apply.

```bash
xrmd duplicate-detection status                                  # the four switches, the tables with the flag, the rules
xrmd duplicate-detection enable                                  # organization switch (disable turns it off)
xrmd duplicate-detection enable --create-update                  # the create/update switch
xrmd duplicate-detection enable --table contact                  # the flag of a table
xrmd duplicate-detection rule create --name "Same email" --base contact --matching contact \
    --condition emailaddress1:exact --exclude-inactive
xrmd duplicate-detection rule create --name "Name and city" --base account --matching account \
    --condition name:first:5:ignore-blank --condition address1_city:exact --case-sensitive
xrmd duplicate-detection rule publish <rule> --wait --timeout 10  # <rule> is an id or a unique name
xrmd duplicate-detection rule unpublish <rule>
xrmd duplicate-detection rule delete <rule> --yes
xrmd duplicate-detection find --table contact --id <id> [--matching <table>]
xrmd duplicate-detection bulk --table contact --name "Contacts" --wait --timeout 30
```

- A `--condition` is `<attr>[:<matchattr>]:<exact|first:N|last:N|date|datetime|value>[:ignore-blank]`; repeat it for more
  conditions. The matching column defaults to the base column. Operator 5 (pick list label) has no keyword: the server refuses
  it.
- `rule create` writes the rule and then each condition, and deletes the rule again if a condition is refused. The rule is not
  published: run `rule publish`. `rule delete` asks first; `--yes` skips the question and is required when stdin is not a
  terminal.
- `rule publish` and `bulk` start system jobs. Without `--wait` they print the job id and return. With `--wait` they poll the
  job: a job that ends without success exits 5 with its message, and one still running after `--timeout` minutes (default 10 for
  publish, 30 for bulk) exits 6 while it keeps running on the server. A `--timeout` of 0 or less exits 2.
- `find` calls `RetrieveDuplicates` and lists the id and primary name of every match (all pages). `bulk` stops at 5000
  `duplicaterecord` rows; read them with `GET duplicaterecords?$filter=_asyncoperationid_value eq <job>`.
- `-o json` is available on all of them. Server refusals are shown as they are: 403 exits 3, an unknown table or rule exits 4, a
  400 or 412 exits 5 (see [Exit codes](#exit-codes)).

## Currencies

`xrmd currency` lists and manages currencies, exchange rates and the pricing precision (see [Currencies](currencies.md)). It
runs over the Web API as the signed-in user, so the server's privilege checks and rules apply: only System Administrators
change currencies.

```bash
xrmd currency list                                  # code, name, symbol, precision, rate, state; the base currency is marked
xrmd currency create USD --name "US dollar" --symbol '$' --rate 1.10 --precision 2
xrmd currency create ZZX --name "Test unit" --symbol 'Z' --rate 2 --custom   # three capital letters, not an ISO code
xrmd currency rate USD 1.12                         # records keep their rate until a save changes their money or currency
xrmd currency deactivate USD                        # asks first; --yes skips the question
xrmd currency activate USD
xrmd currency pricing-precision 3                   # organization.pricingdecimalprecision
```

- `--rate` is the number of units of the currency for one unit of the base currency; `--precision` defaults to 2.
- `deactivate` asks for confirmation; `--yes` skips the question and is required when stdin is not a terminal. The base
  currency cannot be deactivated or given another rate.
- `-o json` is available on every command. Server refusals are shown as they are: 403 exits 3, a code that does not exist
  exits 4, any rule refusal (unknown ISO code, duplicate, rate or precision out of range, base currency) exits 5, a rate or
  precision that is not a number exits 2 (see [Exit codes](#exit-codes)).

## Environment variables

`xrmd envvar` lists the environment variables of the organization and sets their values (see
[Environment variables](environment-variables.md)). It runs over the Web API as the signed-in user, so the server's privilege
checks and rules apply.

```bash
xrmd envvar list                                   # schema name, type, default value, current value; "(none)" when empty
xrmd envvar get new_ServiceUrl                     # definition, default, current value and the value in effect
xrmd envvar set new_ServiceUrl https://example.com # creates the value row, or updates it
xrmd envvar unset new_ServiceUrl                   # deletes the value row, so the default applies again
```

- The schema name is matched by the server's `eq` filter. A name that does not exist exits 4.
- `set` stores the text as given: it is not checked against the variable's type. A Secret variable is refused with exit 2,
  since its value would live in Azure Key Vault, which xrmverse does not support.
- `unset` on a variable that has no value changes nothing and exits 0.
- `-o json` is available on every command. A refusal of the server exits 5, a missing privilege 3 (see [Exit codes](#exit-codes)).

## Deleting a solution

- **`xrmd solution delete <name>` behaves as in Dataverse: it removes only the solution container. Its components
  (tables, columns, data, ...) stay in the system, tracked by the Default solution.**
- **`xrmd solution delete <name> --cascade` is xrmverse-only: it also drops the components (tables and their data)
  that no other solution holds. It goes through the Admin API, asks for confirmation, and cannot be undone.** It is
  never available through the SDK or the Web API: a Web API/SDK delete of a solution is always the container-only one.
  System components and components held by other solutions are kept and reported.

## Skills for AI agents

The [`skills/`](../skills/README.md) folder holds agent skills (`SKILL.md`) that teach Claude how to use these commands,
one skill per area (server, security, schema, solutions, extensions, data, migration). The repository is also a Claude Code
plugin marketplace: `/plugin marketplace add pwbit/xrmverse`, then `/plugin install xrmd@xrmverse`. See [Skills for coding agents](agent-skills.md).

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- The MSAL token cache is kept in clear text in the config directory (see [Where tokens are kept](#where-tokens-are-kept)).
- A unique name or prefix that a publisher already has is refused by the CLI only; the server does not check it (see
  [Publishers and the prefix rule](#publishers-and-the-prefix-rule)).
- Nothing is published to nuget.org: the tool comes from the release assets (see [Install](#install)).

## See also

- [Install](install.md)
- [Admin API](admin-api.md)
- [MCP](mcp.md)
- [Web admin](web-admin.md)
- [Configuration](configuration.md)
