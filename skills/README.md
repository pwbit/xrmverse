# Skills for the xrmd CLI

Agent skills that teach Claude (Claude Code, Claude apps and any client that reads `SKILL.md` skills) how to use the
`xrmd` command line. Each skill covers one area of the CLI, says when to use which command and in what order, and keeps
the rules that matter: license terms are always the user's decision, destructive commands need the user's confirmation,
secrets never go on the command line.

| Skill | Covers |
|---|---|
| [`xrmd-server`](xrmd-server/SKILL.md) | `server`, `auth`, `sdk`, `logs`, `mcp`: install, run, upgrade, troubleshoot, profiles |
| [`xrmd-security`](xrmd-security/SKILL.md) | `user`, `role`, `idp`, `fieldsecurity`, `hierarchy-security`, `access-team`, `audit` |
| [`xrmd-schema`](xrmd-schema/SKILL.md) | `publisher`, `table`, `apply` (schema YAML), `currency`, `envvar`, `change-tracking`, `duplicate-detection` |
| [`xrmd-solutions`](xrmd-solutions/SKILL.md) | `solution`: create, add components, export, import, promote, uninstall, delete, layers |
| [`xrmd-extensions`](xrmd-extensions/SKILL.md) | `plugin`, `webhook`, `customapi`, `workflow` (YAML) |
| [`xrmd-data`](xrmd-data/SKILL.md) | `data`, `dev seed`, `org`: queries, CSV/JSON import and export, backups |
| [`xrmd-migrate`](xrmd-migrate/SKILL.md) | `migrate`: from Dataverse to xrmverse |

## Install

**Claude Code, as a plugin** (this repository is a plugin marketplace):

```
/plugin marketplace add pwbit/xrmverse
/plugin install xrmd@xrmverse
```

**Claude Code, by hand**: copy the folders you want into `~/.claude/skills/` (every project) or `.claude/skills/` (one
project).

**Claude apps**: zip a skill folder (the folder itself, with `SKILL.md` at its root) and upload it in the app's skill settings.

## Keeping them true

The skills describe the CLI of this repository. When a command or option changes, update the skill in the same pull
request. Every `xrmd ...` line in the skills must name a command and options that exist (`xrmd <group> <command>
--help`), and every YAML example must parse with the schema or workflow reader.

---

Licensed under the [Apache License, Version 2.0](../LICENSE), like the rest of xrmverse.

xrmverse is an independent project and is not affiliated with, endorsed by or sponsored by Microsoft. Microsoft,
Dataverse, Dynamics 365 and Power Apps are trademarks of the Microsoft group of companies.
