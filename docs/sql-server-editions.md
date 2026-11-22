# SQL Server editions

[← xrmverse documentation](../README.md#documentation)

## Introduction

xrmverse stores each organization in a SQL Server database. With the bundled `sqlserver` service, xrmd runs the official
`mcr.microsoft.com/mssql/server:2022-latest` image, and you choose which **edition** it runs. That choice decides which
Microsoft license applies to you, so xrmd never leaves it to the image's own default, and never accepts the license terms
for you.

| You decide | With | Default |
|---|---|---|
| The edition | `MSSQL_PID` (`xrmd server init --sql-edition`) | `Express` |
| Acceptance of the license terms | `ACCEPT_EULA=Y` (`--accept-sql-server-eula` or the prompt) | Not accepted |

In Dataverse the database is managed by Microsoft and has no license of its own for you to choose. With an external SQL
Server (`server init --sql-connection`) neither setting applies: see [External SQL Server](#external-sql-server).

## Choosing an edition

The edition is set by the `MSSQL_PID` variable:

| `MSSQL_PID` | What it means for you |
|---|---|
| `Express` (default) | Free, also in production. It is capped: at most **10 GB per database** (each organization is one database, so the cap applies per organization), about 1.4 GB of buffer-pool memory, and the lesser of one socket or four cores. Enough to try xrmverse and for small installations; plan a move to a licensed edition before an organization approaches the size cap. |
| `Developer` | All features, no caps, but licensed only for development and testing, **not for production**. |
| `Standard`, `EnterpriseCore`, `Enterprise` | Production editions that need a SQL Server license of your own. Microsoft documents `Enterprise` as the older per-user (Server + CAL) model and `EnterpriseCore` as the per-core one. |
| a product key `XXXXX-XXXXX-XXXXX-XXXXX-XXXXX` | The edition your key unlocks. |

xrmd accepts exactly these values (edition names in any case); anything else is refused (`server init` exit 2, any later
`server` command exit 5 naming `MSSQL_PID`).

## Setting the edition

- `xrmd server init --sql-edition <value>` writes `MSSQL_PID` to `.env`; without the option a new installation gets
  `Express`. `server init --force` over an existing `.env` keeps the edition written there.
- Later, edit `MSSQL_PID` in `.env` and run `xrmd server up` (or `server upgrade`): the compose file is re-rendered
  from `.env`. The generated compose file requires the key (`${MSSQL_PID:?…}`), so Compose refuses to start when it is
  missing.
- Repository compose file (`docker/docker-compose.yml`): set `MSSQL_PID` in `docker/.env`; when it is unset the
  service runs `Express`.
- Changing the edition of an instance that already holds data is an edition change of SQL Server, not an xrmd
  operation: check Microsoft's guidance first, and remember that Express refuses to grow a database past its cap.

## Installations from before this setting

Until `MSSQL_PID` existed, the `sqlserver` service ran the image's default,
which is the Developer edition. So that the edition of an existing data volume never changes behind your back, `server
up`, `server upgrade` and `server org add|remove` on a multi-organization `.env` without `MSSQL_PID` write
`MSSQL_PID=Developer` into it and keep running Developer; `server init --force` over such a `.env` does the same.
Whenever the edition is Developer these commands print, on stderr:

```
This installation runs the SQL Server Developer edition, which is not licensed for production. Set MSSQL_PID in .env (see README, "SQL Server editions").
```

Choose the edition you are licensed for and write it to `.env` to stop the warning. Legacy single-organization
installations (no `XRMD_ORGS`) are not changed. With the repository compose file, a `mssql-data` volume created
before this setting also ran Developer; set `MSSQL_PID=Developer` in `docker/.env` to keep it, or choose another edition.

## External SQL Server

With `server init --sql-connection` there is no `sqlserver` service: the edition and its
license are those of your own SQL Server, and `--sql-edition` is refused (exit 2), as is `MSSQL_PID` in that `.env`.

## License terms (`ACCEPT_EULA`)

The SQL Server image starts only with `ACCEPT_EULA=Y`, which states that you accept Microsoft's license terms for the
edition in `MSSQL_PID` and hold a valid license for it. xrmd never sets it for you:

- `xrmd server init` with the bundled SQL Server shows where the terms of the chosen edition are and asks at a terminal
  (default no). `--accept-sql-server-eula` accepts them without the question. Without a terminal and without the flag,
  init writes nothing (exit 2). Once accepted, `ACCEPT_EULA=Y` is in `.env` and the generated compose file reads it from
  there (`${ACCEPT_EULA:?…}`): Compose refuses to start without it. `server init --force` keeps an accepted `ACCEPT_EULA`.
- An installation written before this setting has no `ACCEPT_EULA` in `.env`. `server up`, `server upgrade` and
  `server org add|remove` ask the same question at a terminal, or take `--accept-sql-server-eula` (`up` and `upgrade`);
  otherwise they stop (exit 5) before anything changes and name the command to run.
- `ACCEPT_EULA` other than `Y` is refused (exit 5). With an external SQL Server (`--sql-connection`) there is nothing to
  accept: the flag is refused (exit 2) and `.env` has no such key.
- Repository compose file (`docker/docker-compose.yml`): set `ACCEPT_EULA=Y` in `docker/.env` yourself, after reading the
  terms; Compose refuses to start without it.

### Where the terms are

| Edition | Terms |
|---|---|
| Express | [Microsoft's free production license](https://go.microsoft.com/fwlink/?linkid=857693) |
| Developer | The [SQL Server downloads page](https://www.microsoft.com/sql-server/sql-server-downloads) |
| Standard, Enterprise, EnterpriseCore, product keys | Your license from [Microsoft Licensing](https://www.microsoft.com/licensing) |

What `ACCEPT_EULA=Y` and `MSSQL_PID` express is explained in
[running production editions in containers](https://learn.microsoft.com/sql/linux/containers/deploy#run-production-container-images).

## Sources

Microsoft Learn: [editions and limits of SQL Server 2022](https://learn.microsoft.com/sql/sql-server/editions-and-components-of-sql-server-2022#scale-limits),
[the same for Linux](https://learn.microsoft.com/sql/linux/sql-server-linux-editions-and-components-2022#scale-limits),
[`MSSQL_PID` values](https://learn.microsoft.com/sql/linux/configure/environment-variables#environment-variables),
[running production editions in containers](https://learn.microsoft.com/sql/linux/containers/deploy#run-production-container-images).

## See also

- [Getting started (Docker)](getting-started.md), [Installing xrmverse](install.md)
- [Multiple organizations](multiple-organizations.md)
