# Getting started (Docker)

[← xrmverse documentation](../README.md#documentation)

## Introduction

This page gets an xrmverse server running on your machine with Docker in a few minutes, so that you can try it with your own
clients. It uses **development mode**: a signing key on the server stands in for Microsoft Entra ID, so you need no tenant and
no app registration.

For a server that other people will use (host names, certificates, Entra ID, backups), follow
[Installing xrmverse](install.md) instead.

| Path | When to use it | Port |
|---|---|---|
| [With the `xrmd` CLI](#with-the-xrmd-cli) | You have a released CLI: the recommended way | 5001 (one per organization) |
| [From a clone](#from-a-clone) | You work on xrmverse's source | 5001 |
| [Without the CLI (repository compose file)](#without-the-cli-repository-compose-file) | You only want the compose file of the repository | 5000 |

### Before you start

| You need | Notes |
|---|---|
| Docker with Compose | On Apple Silicon the SQL Server image runs under emulation (see [Notes](#notes)) |
| The `xrmd` CLI | See [CLI xrmd](cli.md); not needed for the repository compose file path |
| Access to the server image | `ghcr.io/pwbit/xrmd:0.1.11` is public; no GHCR login is needed. An [offline archive](#public-release-and-image-access) and source builds are alternatives |
| Your acceptance of two sets of license terms | SQL Server's (see [SQL Server editions](sql-server-editions.md)) and the Microsoft SDK's (see [Installing the Microsoft SDK](microsoft-sdk.md)); both questions default to no |

## Public release and image access

The public v0.1.11 CLI archives, server archive and checksums are on the
[v0.1.11 release](https://github.com/pwbit/xrmverse/releases/tag/v0.1.11) of the public source repository (releases up to 0.1.10 are in `pwbit/xrmverse-releases`).
See [installing the CLI](install.md#2-install-the-cli); it is not published to NuGet.org, Homebrew or winget.
The current source tree may contain features newer than the released binaries.

The server image `ghcr.io/pwbit/xrmd:0.1.11` is public and supports anonymous pulls; no `docker login` is needed.
For an offline transfer, download `xrmd-server-0.1.11-linux-amd64.tar.gz` and its `.sha256` from the same release, verify the
checksum (`sha256sum -c <file>.sha256`, or `shasum -a 256 -c <file>.sha256` on macOS), then run
`docker load -i xrmd-server-0.1.11-linux-amd64.tar.gz`. This loads the tag the v0.1.11 CLI expects; `server up`
can use the loaded image without pulling it. SQL Server and the Microsoft SDK still require separate downloads
and your acceptance of their terms. See [offline installation and verification limits](install.md#9-offline-install).

Alternatively, build from the public source with the .NET 10 SDK and Docker, as shown below.

## With the `xrmd` CLI

A released CLI runs the published server image of its own version (`ghcr.io/pwbit/xrmd:<version>`).

### Steps

```bash
xrmd server init --dir ./xrmd-server                 # one organization, "xrmd", on port 5001; writes docker-compose.yml and .env (XRMD_IMAGE pinned to the CLI version)
                                                     # asks you to accept the SQL Server license terms (default no; --accept-sql-server-eula)
xrmd sdk install --dir ./xrmd-server                 # the Microsoft SDK, after you accept Microsoft's license terms (asks; default no)
xrmd server up --dir ./xrmd-server                   # refuses to start while ./xrmd-server/sdk/sdk.json is missing
curl http://localhost:5001/healthz                   # -> ok (once the xrmd-xrmd service is up)
export XRMD_DEV_SIGNING_KEY=$(grep '^AUTH_DEV_SIGNING_KEY=' ./xrmd-server/.env | cut -d= -f2)
xrmd auth status                                     # WhoAmI through the dev profile "xrmd" created by server init
xrmd server status --dir ./xrmd-server              # one row per organization; also needs XRMD_DEV_SIGNING_KEY in dev mode
```

### What `server init` does for you

- Generates the SQL Server password and the dev signing key.
- Creates a dev CLI profile per organization.
- Offers to install the Microsoft SDK (see [Installing the Microsoft SDK](microsoft-sdk.md)).
- Configures the bundled SQL Server with the free **Express** edition (at most 10 GB per database); for another edition, and
  for the license terms you accept, see [SQL Server editions](sql-server-editions.md).

### Useful variations

| You want | Do |
|---|---|
| Several organizations | `--org acme --org zenith` (ports 5001, 5002, …); see [Multiple organizations](multiple-organizations.md) |
| `https://<org>.<domain>` on 443 instead of one port each | Add `--domain <domain> --tls acme\|files\|internal` to `server init`; see [Installing xrmverse](install.md) |
| Stop the server | `xrmd server down --dir ./xrmd-server` |
| Stop and delete the data | `xrmd server down --dir ./xrmd-server --volumes` |

### What happens at startup

1. `xrmd` waits for SQL Server (30 retries x 2 s).
2. It creates its organization database if missing.
3. It applies the migrations and the seed (organization, root business unit, administrator user).

Instances started together on the same database are serialized with a `sp_getapplock` (`xrmd-startup:<database>`);
organizations on other databases of the same SQL Server start without waiting for each other. Versions before this change
used one server-wide name (`xrmd-startup`), so an old and a new process do not serialize each other: during an upgrade,
upgrade one host of a database at a time with its other processes stopped (one process per database is the rule anyway).
One process per database is enforced by another lock (`xrmd-instance`, see [Multiple organizations](multiple-organizations.md)).

## From a clone

```bash
git clone https://github.com/pwbit/xrmverse.git
cd xrmverse
```

Install the .NET 10 SDK and Docker with Compose v2 first. This path builds xrmverse locally, without access to its GHCR package.

A development CLI (`dotnet run --project src/Xrmd.Cli --` from the repository root) has no release version: `server init`
writes `XRMD_IMAGE=xrmd:local` and prints the command that builds it. Build the image first, then run the same commands:

```bash
docker build -f docker/Dockerfile -t xrmd:local .
dotnet run --project src/Xrmd.Cli -- server init --dir ./xrmd-server
dotnet run --project src/Xrmd.Cli -- sdk install --dir ./xrmd-server
dotnet run --project src/Xrmd.Cli -- server up --dir ./xrmd-server
curl --fail http://localhost:5001/healthz
```

## Without the CLI (repository compose file)

`docker/docker-compose.yml` builds the image from source and runs one organization (database `xrmverse`) on port **5000**;
it is what the development token script below targets.

```bash
cp docker/.env.example docker/.env      # set a strong SA_PASSWORD and AUTH_DEV_SIGNING_KEY (openssl rand -hex 32)
# Read the SQL Server terms and set ACCEPT_EULA=Y in docker/.env only if you accept them (MSSQL_PID defaults to Express).
xrmd sdk install                        # the Microsoft SDK into ~/.xrmd/sdk, after you accept Microsoft's license terms (asks; default no)
docker compose -f docker/docker-compose.yml --env-file docker/.env up --build -d
curl http://localhost:5000/healthz      # -> ok
```

### Where the SDK comes from

- The compose file mounts the SDK folder read-only into the container: `XRMD_SDK_HOST_DIR` in `docker/.env`, else
  `~/.xrmd/sdk` (`${HOME}/.xrmd/sdk`).
- Without the CLI installed, `dotnet run --project src/Xrmd.Cli -- sdk install` does the same as `xrmd sdk install`.
- Set `XRMD_SDK_HOST_DIR` when the SDK is elsewhere (`xrmd sdk install` with `XRMD_SDK_DIR` set) or when `HOME` is not set
  in your shell (some Windows shells), which would turn the mount into `/.xrmd/sdk`.
- A missing folder is created empty by Docker, and the container then exits with code 3 and the missing-SDK message.

To stop: `docker compose -f docker/docker-compose.yml --env-file docker/.env down` (`-v` also deletes the `mssql-data`
volume).

## Development token (`AUTH_MODE=Development`)

```bash
TOKEN=$(docker/dev-token.sh)            # requires only bash + openssl; reads docker/.env
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/data/v9.2/WhoAmI
```

| Mode | Tokens accepted |
|---|---|
| `Development` | An HS256 JWT (issuer `xrmverse-dev`, audience `AUTH_AUDIENCE`, claim `oid` = `SEED_ADMIN_OBJECT_ID`, valid for 1 hour) signed with `AUTH_DEV_SIGNING_KEY` |
| `Entra` | Microsoft Entra ID tokens for the tenant `AUTH_TENANT_ID`; `AUTH_AUDIENCE` must match the server's base URL |

With `Entra`, xrmd accepts only **v2.0** tokens (issuer `https://login.microsoftonline.com/{tenant}/v2.0`):

- in the app registration manifest set `"accessTokenAcceptedVersion": 2` (with the default value Entra issues v1 tokens,
  with issuer `https://sts.windows.net/...`, which are rejected);
- use the tenant **GUID** (not the domain name) as `AUTH_TENANT_ID`, because the token issuer contains the GUID.

## Notes

| Topic | Note |
|---|---|
| ServiceClient | `Microsoft.PowerPlatform.Dataverse.Client` drops the port from the URI, so it works only if the server responds on the default port (80/443). Give the organizations host names on 443 (`server init --domain`, see [Installing xrmverse](install.md)), or map `80:5000`; the acceptance tests use Kestrel on port 80 |
| Apple Silicon | The `mssql/server:2022-latest` image is `linux/amd64` and runs under emulation (Rosetta/QEMU): startup is slow and SQL Server may take longer to become healthy |
| Network | The Docker build requires access to `api.nuget.org` for the restore |

## Next steps

- [Installing xrmverse](install.md) for a real server
- [CLI xrmd](cli.md) to manage tables, data, plug-ins and solutions
- [Configuration](configuration.md) for every setting
