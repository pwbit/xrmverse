---
name: xrmd-migrate
description: Move an application from a Microsoft Dataverse environment into an xrmverse organization with `xrmd migrate` - export to an encrypted package, inspect it, map users, import it on the server, follow the run, read its report, resume, finalize and switch business logic back on. Use this whenever someone wants to migrate, copy or move data, solutions or users from Dataverse / Dynamics 365 / Power Apps into xrmverse, plan such a migration, fix a failed or partial migration run, or understand what a migration report says, even if they only say "bring my CRM over" or "the import stopped halfway".
---

# xrmd: migrating from Dataverse

A one-off move of an application: schema (solutions plus customizations outside them), users, records **with their ids
and system fields** (`createdon`, `createdby`, owner, ...), N:N rows, shares, files and notes. It goes through an
encrypted package, so the machine that imports needs no route to Dataverse.

```
Dataverse ─► migrate export ─► package.xrmpkg (AES-256-GCM) ─► migrate users ─► migrate import ─► report ─► activate
```

## Working with xrmd (all skills)

- The import runs as the signed-in xrmverse profile's user and needs **System Administrator** on the target organization.
- `-o json` for parseable output; `--profile <name>` selects the target organization.
- `xrmd migrate <command> --help` is the truth when an option is unclear.
- Exit codes: 0 ok, 2 usage (missing option, bad users file, non-empty export folder), 3 sign-in refused or wrong
  passphrase, 4 not found, 5 rejected or a run that ended with errors, 6 Dataverse/Entra/xrmverse unreachable,
  8 a package that cannot be read or trusted.

## Before starting: what the user must provide

1. **Their own Entra app registration** with access to the Dataverse environment: `--client-id` and `--tenant` are
   required, there is no default client. For `--auth browser`, the redirect URI `http://localhost` registered as a
   mobile/desktop platform; for `--auth secret`, the secret in `XRMD_MIGRATE_CLIENT_SECRET` and an application user in
   the environment; for `--auth certificate`, `--certificate-path` (password in `XRMD_MIGRATE_CERTIFICATE_PASSWORD`) or
   `--certificate-thumbprint`.
2. **The solutions to carry**: `--solution a,b` and/or `--all-unmanaged`. Managed solutions cannot be exported from
   Dataverse: pass their original zip with `--solution-file` (same version as installed).
3. **A passphrase** of at least 12 characters in `XRMD_MIGRATE_PASSPHRASE` (or `--passphrase-stdin`). The package holds
   personal data: the passphrase is never a command-line argument, never printed, and is kept apart from the package.
   Ask the user to set the variable themselves rather than typing it into the conversation.
4. A target xrmverse organization and a System Administrator profile for it (xrmd-server skill).

Microsoft first-party apps (`msdyn_`, `msdynce_`, `msdynmkt_`, `adx_`, `mspp_`) are never carried: they are not
supported on xrmverse. Do not try to work around this.

## Step by step

```bash
export XRMD_MIGRATE_PASSPHRASE='...'      # set by the user

# 1. Export (read-only on Dataverse; resumable: rerun the same command after an interruption)
xrmd migrate export --source https://contoso.crm4.dynamics.com \
  --solution contoso_core,contoso_sales [--solution-file ./isv_managed.zip] \
  --auth browser --client-id <app id> --tenant contoso.onmicrosoft.com \
  --out ./contoso-export --pack ./contoso.xrmpkg [--exclude <tables>] [--require-admin]

# 2. Check the package offline
xrmd migrate inspect ./contoso.xrmpkg

# 3. Users mapping: write the template, review it with the user, edit
xrmd migrate users ./contoso.xrmpkg > users.yaml

# 4. Import into the target organization (uploads in 8 MiB chunks, starts the run, follows it)
xrmd migrate import ./contoso.xrmpkg --users users.yaml [--strict] [--strict-lookups] [--base-currency-map]

# 5. Read the report
xrmd migrate report <runId>                 # md (default), --format json, or --format csv (the issues)

# 6. Switch business logic back on, after checking the data
xrmd migrate activate <runId> --all --dry-run
xrmd migrate activate <runId> --all         # or --id <id> --id <id> for a selection
```

One-shot alternative for a machine that reaches both sides: `xrmd migrate run --source ... --solution ... --auth ...
--client-id ... --tenant ... --users users.yaml` (export options + import options; the export is kept on failure).

## The users mapping

`migrate users` proposes one entry per exported user. Review it with the user before importing:

| Action | Effect |
|---|---|
| `entra` | keeps the user and its Entra object id (default for licensed users with an object id) |
| `entra-remap` | same user, new Entra object id (`objectid:`), e.g. another tenant |
| `local` | user of xrmverse's own identity provider with a user name; set the password later with `xrmd idp user set-password` |
| `existing` | maps onto a user the organization already has (`target:`) |
| `stub` | disabled user without sign-in, kept so `createdby`, `modifiedby` and owners resolve (default for disabled and application users) |

## Import options

| Option | Use when |
|---|---|
| `--strict` | anything that cannot be carried must stop the run before any solution is imported |
| `--strict-lookups` | a row whose lookup points outside the package must fail instead of having the lookup emptied |
| `--base-currency-map` | the source and target base currencies have different ISO codes |
| `--keep-package` | keep the uploaded package on the server after the run |
| `--no-wait` | start and return; follow with `xrmd migrate status <runId> --wait` |

## When a run does not complete

| Run state | What to do |
|---|---|
| `completed` | read the report, then `activate` |
| `completed_with_errors` | read the report issues (`--format csv`); fix the cause, then `xrmd migrate import --resume <runId>`, or accept the result with `xrmd migrate finalize <runId>` |
| `failed` / `cancelled` | `xrmd migrate import --resume <runId>` continues from the first phase not completed |
| still running | `xrmd migrate status <runId> --wait`; Ctrl-C only stops following, not the run; `xrmd migrate cancel <runId>` cancels |

Only one run is open per organization; the organization stays locked until the run completes or is finalized.
`activate` is refused until the run is finalized. Finalizing accepts the result as it is: confirm with the user first.

## Reading the report

- `tables`: exported / imported / failed per table.
- `issues`: phase, severity, code, table, record, column, message (codes are the Web API `0x8004xxxx` codes).
- `lookupsNulled`: lookups emptied because their target was not in the package (use `--strict-lookups` to fail instead).
- Verification codes: `verify-missing`, `verify-mismatch`, `verify-file-missing`, `verify-file-mismatch` are errors.
- "Business logic left off": plug-in steps, webhook steps and processes that `activate` will switch on.

## What is not carried

- Forms, views, charts, roles and processes **outside** the exported solutions (warning `default-solution-component`:
  add them to a solution in Dataverse and export again).
- Rollup, formula and prompt columns, virtual tables and multi-table lookups (`schema/unsupported.json`).
- Runtime tables (system jobs, logs, audit) and tables xrmverse does not support yet (counted in the warnings).

During the run no business logic fires (no plug-ins, workflows, rules, duplicate detection, auditing or webhooks); that
is why `activate` exists. Activate only after the user has checked the data.
