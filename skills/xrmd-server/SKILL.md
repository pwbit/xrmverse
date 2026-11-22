---
name: xrmd-server
description: Install, start, upgrade and troubleshoot an xrmverse server with the xrmd CLI (Docker Compose), add or remove organizations, sign in with profiles, install the Microsoft SDK and connect MCP clients. Use this whenever someone wants to set up xrmverse, run `xrmd server ...`, `xrmd auth ...`, `xrmd sdk ...` or `xrmd mcp`, fix a server that does not start or answer, read server logs, add an organization, expose a server on a public host with TLS, or sign in to xrmverse, even if they only say "install xrmd", "the server is down" or "connect Claude to my xrmverse".
---

# xrmd: server, profiles, SDK and MCP

xrmverse is a Dataverse-compatible server; `xrmd` is its command line. This skill covers the installation (a Docker Compose
folder), its organizations, sign-in profiles, the Microsoft SDK the server needs, and the stdio MCP bridge.

## Working with xrmd (all skills)

- Every command calls the server's Web API, Admin API or MCP endpoint **as the signed-in user**. There is no privileged
  path: missing privileges are a 403 (exit 3). Admin API commands (`server status`, `server doctor`, `org`, `logs`,
  `hierarchy-security`, `migrate import`) need the System Administrator role.
- Global options on every command: `-o json` (only JSON on stdout, diagnostics on stderr; use it whenever you parse the
  output), `--profile <name>`, `--url <url>` (overrides the profile's URL for one command).
- When an option is unclear, run `xrmd <group> <command> --help`: the installed CLI is the truth, this skill may be older.
- Destructive commands ask for confirmation and, without a terminal, refuse to run unless `--yes` is given. Pass `--yes`
  only after the user has confirmed that specific action in the conversation.
- Secrets are never command-line arguments: passwords and passphrases go through `--password-stdin` / `--passphrase-stdin`
  or environment variables. Never echo them back.

| Exit | Meaning |
|---|---|
| 0 | success |
| 1 | unexpected error, server 5xx, or a declined confirmation |
| 2 | usage error or local input problem (bad option, missing file) |
| 3 | authentication: 401/403, no profile, no token |
| 4 | not found (404 or an unknown name) |
| 5 | rejected by the server (400/409/412) |
| 6 | server unreachable, or a `--wait` timeout while the job keeps running |
| 7 | `org restore` done but files are missing from the file store |
| 8 | a migration package that cannot be read or trusted |

## License terms: always the user's decision

Two Microsoft license terms are involved, and accepting them is a legal act of the person who runs the server:

- the bundled **SQL Server** edition's terms: `--accept-sql-server-eula` on `server init`, `server up`, `server upgrade`;
- the **Microsoft SDK** terms (`Microsoft.PowerPlatform.Dataverse.Client` and `Microsoft.CrmSdk.Workflow`):
  `--accept-microsoft-license` on `server init` and `sdk install`.

Never add these flags on your own initiative. Tell the user what they accept (the help text names the terms and the link),
and pass the flag only after they say they accept. Without the flag and without a terminal, the command installs or writes
nothing, which is the safe outcome. xrmverse does not ship the SDK assemblies ("bring your own SDK"), which is why this step
exists.

## Set up a new installation

```bash
xrmd server init --dir ./xrmd --org acme          # docker-compose.yml + .env with generated secrets, dev profile "acme"
xrmd sdk install --dir ./xrmd                     # only if init did not install the SDK (license prompt)
xrmd server up --dir ./xrmd                       # docker compose up -d; exit 1 if ./xrmd/sdk/sdk.json is missing
export XRMD_DEV_SIGNING_KEY=$(grep '^AUTH_DEV_SIGNING_KEY=' ./xrmd/.env | cut -d= -f2)
xrmd server status --dir ./xrmd                   # http://localhost:5001/
```

Choose the `server init` options from what the user needs:

| Need | Options |
|---|---|
| Several organizations | `--org a --org b` (one database, `xrmd-<org>` service and profile each; ports from `--port`, default 5001) |
| Local trial, no identity provider | `--auth dev` (default): tokens signed with `AUTH_DEV_SIGNING_KEY` from `.env` |
| Microsoft Entra ID | `--auth entra --tenant-id <guid> --admin-object-id <guid>` (one admin per `--org`, same order) |
| The server's own sign-in (passwords, TOTP) | `--auth local` (needs https URLs, so a public host); one-time admin password in `<dir>/secrets/<org>/admin-password` |
| External SQL Server | `--sql-connection "<conn string without Database>"` (no bundled SQL Server, no EULA flag) |
| Another SQL Server edition | `--sql-edition Developer\|Standard\|Enterprise\|...` |
| Public host names on 443 | `--domain example.com` (or `--public-host acme=crm.example.com`) with `--tls acme --acme-email ...`, `--tls files --tls-cert ... --tls-key ...`, or `--tls internal`; `--no-proxy --trusted-proxy <cidr>` behind your own proxy |
| Read-only SQL (TDS) endpoint | `--tds-port 5558` (needs `--auth local` and a public host) |
| Pinned image | `--image ghcr.io/pwbit/xrmd:<version>`; the default is the CLI's own version, never `latest` |

`--force` rewrites existing files but keeps the SQL password and dev signing key of the old `.env`.

Things that surprise people:

- Ports bind to `127.0.0.1`. To expose them, set `XRMD_BIND=0.0.0.0` in `.env` (or better, use a public host behind TLS).
- With Entra, each organization's audience must equal the API's Application ID URI: set `ORG_<ORG>_AUDIENCE` in `.env`
  when that is not the organization URL, and `AUTH_TENANT_ID` to the tenant GUID.
- A dev profile needs the signing key in its environment variable (`XRMD_DEV_SIGNING_KEY` by default) at every command,
  also for MCP clients that start `xrmd mcp`.

## Run and maintain

| Task | Command |
|---|---|
| Health of every organization of the folder | `xrmd server status --dir <dir>` (exit 1 unless all Healthy) |
| Self-checks (config, auth, migrations, file store, DNS and certificates of public hosts) | `xrmd server doctor --dir <dir>` (exit 1 on an error) |
| Container logs | `xrmd server logs --dir <dir> [-f] [--tail 200]` |
| Application log entries (in-memory ring buffer) | `xrmd logs [--since <iso>] [--level Warning] [--category <text>] [--take 200] [-f]` |
| Upgrade to the newest image of the pinned tag | `xrmd server upgrade --dir <dir>` |
| Move to another version | `xrmd server upgrade --dir <dir> --to 1.5.0` (rewrites `XRMD_IMAGE`, pulls, waits for health) |
| Move to the newest release | `xrmd server upgrade --dir <dir> --to latest` (asks GitHub, writes the exact version; exit 5 if the CLI is older: install that CLI first) |
| List the releases | `xrmd server versions --dir <dir>` (marks the installed version and the CLI's) |
| Stop | `xrmd server down --dir <dir>` |
| Stop **and delete all data** | `xrmd server down --dir <dir> --volumes` (destroys the databases: confirm with the user first) |
| Add an organization | `xrmd server org add <name> --dir <dir> [--admin-object-id <guid>] [--public-host <host>] [--tds-port <port>]` |
| Remove an organization | `xrmd server org remove <name> --dir <dir>` (database kept; `--drop-database` drops it, destructive) |

Organization names match `^[a-z][a-z0-9]{0,31}$`. `server status`, `doctor` and `upgrade` run in a multi-organization
folder through the profile named as each organization, so those profiles must exist.

When diagnosing, go from the outside in: `server status` (is it up and does the token work?) → `server doctor` (which check
fails?) → `logs --level Warning` (what did the application log?) → `server logs` (did the container start?). Exit 3 means a
profile or token problem, not a server problem; exit 6 means nothing answers at that URL.

## Profiles and sign-in

Profiles live in `~/.xrmd/config.json` (override with `XRMD_CONFIG_DIR`). They hold URL, mode and non-secret ids only.

| Server auth | Sign-in |
|---|---|
| dev | `xrmd auth login --url http://localhost:5001/ --mode dev [--profile acme]` (key from `XRMD_DEV_SIGNING_KEY`) |
| Entra, interactive | `xrmd auth login --url <url> --mode device-code --client-id <app id> [--tenant-id <tenant>]` or `--mode browser` (redirect URI `http://localhost` as a mobile/desktop platform) |
| Entra, unattended | `--mode client-secret --client-id <id> --tenant-id <tenant>` with the secret in `XRMD_CLIENT_SECRET` |
| Local identity provider | `--mode device-code --idp local` (client `xrmd-cli` by default); behind `--tls internal` add `--ca-file <dir>/certs/caddy-root.crt` |

`xrmd auth status` shows the profile and the server's WhoAmI; `xrmd auth logout` removes the profile and its cached tokens.
The MSAL cache is stored unencrypted in the config folder (mode 0600): suggest `auth logout` on shared machines.

## Microsoft SDK

`xrmd sdk status [--dir <dir>]` shows what is installed and the license acceptance record. `xrmd sdk install --dir <dir>`
installs into the installation (mounted into the containers); `--user` installs into `~/.xrmd/sdk`; `--from <nupkg>` and
`--from-workflow <nupkg>` install from local packages on machines without nuget.org access. See the license rule above.

## MCP for AI clients

`xrmd mcp` is a stdio bridge to the server's `/mcp` endpoint using a profile's token. A client configuration:

```json
{ "mcpServers": { "xrmverse": { "command": "xrmd", "args": ["mcp", "--profile", "acme"] } } }
```

The profile must exist first (`xrmd auth login`), and with a dev profile the client's environment must carry the signing key.
