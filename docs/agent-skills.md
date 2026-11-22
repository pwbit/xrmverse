# Skills for coding agents

[← xrmverse documentation](../README.md#documentation)

## Introduction

xrmverse comes with **agent skills**: short instruction files (`SKILL.md`) that teach Claude, and any coding agent that
reads `SKILL.md` skills, how to use the `xrmd` command line. Each skill covers one area of the CLI. It says which command
to use for a task, in what order, and which rules must hold. With the skills installed, you can ask an agent "install
xrmverse on this machine", "add a lookup from contact to project" or "migrate my CRM from Dataverse", and the agent runs
the right `xrmd` commands.

The skills live in the `skills/` folder of the repository. The repository is also a Claude Code plugin marketplace, so
one command installs all of them.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Agent guidance for the command line | seven skills that cover the whole `xrmd` CLI | Extra: written for xrmverse and its CLI |
| What an agent can do | whatever the signed-in user can do through `xrmd` | Same security as any client: no privileged path |
| Agents that read data directly | the built-in [MCP server](mcp.md) | Separate from the skills: the skills drive the CLI, MCP gives tools |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Install the skills in Claude Code | `/plugin marketplace add pwbit/xrmverse`, then `/plugin install xrmd@xrmverse` | [Installing the skills](#installing-the-skills) |
| Install them in a Claude app | upload a zipped skill folder | [Installing the skills](#installing-the-skills) |
| Choose the right skill | the skills table | [The skills](#the-skills) |
| Know what an agent will not do on its own | the rules every skill keeps | [Rules the skills keep](#rules-the-skills-keep) |

## Installing the skills

| Where | How |
|---|---|
| Claude Code, as a plugin | `/plugin marketplace add pwbit/xrmverse`, then `/plugin install xrmd@xrmverse` |
| Claude Code, by hand | copy the skill folders you want into `~/.claude/skills/` (every project) or `.claude/skills/` (one project) |
| Claude apps | zip a skill folder (the folder itself, with `SKILL.md` at its root) and upload it in the app's skill settings |

The agent also needs the `xrmd` CLI itself (see [CLI](cli.md#install)) and, for anything beyond installing a server, a
signed-in profile (`xrmd auth login`, see [CLI](cli.md#profiles-and-authentication)). The skills load on their own when a
request matches them: you do not have to name them.

## The skills

| Skill | Command groups | Use it to |
|---|---|---|
| `xrmd-server` | `server`, `auth`, `sdk`, `logs`, `mcp` | install, start, upgrade and troubleshoot a server, add organizations, sign in with profiles, install the Microsoft SDK, connect MCP clients |
| `xrmd-security` | `user`, `role`, `idp`, `fieldsecurity`, `hierarchy-security`, `access-team`, `audit` | add users, assign roles, set passwords and TOTP, create integration clients, secure columns, set up hierarchy security and access teams, switch on and read auditing |
| `xrmd-schema` | `publisher`, `table`, `apply`, `currency`, `envvar`, `change-tracking`, `duplicate-detection` | design the data model: publishers and prefixes, tables, columns, lookups, choices, schema YAML files; currencies, environment variables, change tracking and duplicate detection |
| `xrmd-solutions` | `solution` | create solutions, add components, export, import (update, upgrade, stage and promote), publish, uninstall, delete and inspect layers |
| `xrmd-extensions` | `plugin`, `webhook`, `customapi`, `workflow` | register plug-ins, steps and images, read traces, register webhooks, create custom APIs, author workflows and business rules as YAML |
| `xrmd-data` | `data`, `dev seed`, `org` | query records, import and export CSV or JSON, seed sample data, create, back up and restore organizations |
| `xrmd-migrate` | `migrate` | move an application from Dataverse: export, inspect, map users, import, follow the run, read the report, finalize and switch business logic back on |

## Rules the skills keep

| Rule | What the agent does |
|---|---|
| License terms are the user's decision | the Microsoft SDK terms (`--accept-microsoft-license`) and the bundled SQL Server edition's terms (`--accept-sql-server-eula`) are accepted only when the user says so; the agent never adds these flags on its own |
| Destructive commands need confirmation | destructive commands, such as `server down --volumes`, ask for confirmation; the agent passes `--yes` only after the user confirmed that specific action |
| Secrets never go on the command line | passwords go through `--password-stdin` and the migration passphrase through `XRMD_MIGRATE_PASSPHRASE` or `--passphrase-stdin`, never as arguments |
| Every command runs as the signed-in user | the agent can do only what the profile's user may do; `org` and some server commands need System Administrator |

## Keeping the skills true

The skills describe the CLI of the same release. When a command or an option changes, its skill changes in the same pull
request: every `xrmd ...` line in a skill names a command and options that exist (`xrmd <group> <command> --help`), and
every YAML example parses with the schema or workflow reader.

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- The skills cover the `xrmd` CLI only. They do not teach the SDK, the Web API or the web admin.
- They are written for Claude. Another agent can read them if it supports `SKILL.md` skills.
- A skill matches the CLI of its own release: use the skills of the release you run.

## See also

- [CLI](cli.md)
- [MCP](mcp.md)
- [Install](install.md)
- [Migrating from Dataverse](migration.md)
