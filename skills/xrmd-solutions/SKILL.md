---
name: xrmd-solutions
description: Package and move customizations between xrmverse organizations with xrmd solution commands - create unmanaged solutions, add components, export, import (update, upgrade, stage and promote), publish, uninstall managed solutions, delete solutions and inspect component layers. Use this whenever someone wants to import a Dataverse/Power Apps solution zip into xrmverse, export or deploy a solution from dev to test or prod, upgrade a managed solution, find out which solution owns a component or why a customization is not showing, or remove a solution, keep a solution in git as source files, check that a repository folder matches an organization, or pull an organization's changes into a folder (unpack, pack, diff, sync), even if they just say "deploy my solution" or "bring this zip over".
---

# xrmd: solutions

xrmverse imports and exports the Dataverse solution zip format and keeps the managed/unmanaged layering. Solutions are
how customizations move between organizations.

## Working with xrmd (all skills)

- Every command runs as the signed-in profile's user (solution work needs System Administrator or equivalent privileges).
- `-o json` for parseable output; `--profile <name>` selects the organization. Moving between organizations = the same
  command with another `--profile`.
- `xrmd <group> <command> --help` is the truth when an option is unclear.
- Destructive commands need `--yes` without a terminal: pass it only after the user confirmed that action.
- Exit codes: 0 ok, 1 unexpected/declined, 2 usage, 3 authentication or privilege, 4 not found, 5 rejected (a failed
  import prints the import job's log), 6 unreachable or an `--async` import still running after `--timeout`.

## Build a solution

```bash
xrmd publisher create --unique-name acme --prefix ac --option-value-prefix 12000   # if the publisher does not exist
xrmd solution create AcmeCore --publisher acme --display-name "Acme Core" [--version 1.0.0.0]
xrmd table create ac_project --label Project --plural-label Projects --solution AcmeCore   # new components straight in
xrmd solution add-component AcmeCore --type entity --id <MetadataId>                       # existing components
xrmd solution publish                                                                      # PublishAllXml
xrmd solution export AcmeCore --out ./AcmeCore_1_0_0_0.zip
```

Most create commands (`table`, `apply` via `solution:`, `plugin`, `customapi`, `workflow apply`) take `--solution` so the
component lands in the solution at creation; prefer that over `add-component` afterwards. Get a component id from
`xrmd table describe <t> -o json` (MetadataId) or the record id. `--type` takes a number or a name such as `entity`,
`attribute`, `optionset`, `role`, `savedquery`, `systemform`, `webresource`, `workflow`, `pluginassembly`,
`sdkmessageprocessingstep`, `customapi`, `environmentvariabledefinition`, `appmodule`.

Only **unmanaged** export exists: xrmverse does not produce managed solutions. To deliver managed solutions, build them in
Dataverse and import them here.

## Import

```bash
xrmd solution import ./AcmeCore_1_0_0_1.zip                       # --mode update (default): over the installed version
xrmd solution import ./AcmeCore_managed.zip --mode upgrade        # apply at once; refused when it would drop data
xrmd solution import ./AcmeCore_managed.zip --mode stage          # holding solution AcmeCore_Upgrade, apply later:
xrmd solution promote AcmeCore                                    # DeleteAndPromote; --drop-data if it drops data
xrmd solution import ./big.zip --async --timeout 120              # queue as a system job, poll every 2 s
xrmd solution import ./AcmeCore.zip --overwrite                   # overwrite unmanaged customizations
```

Choose the mode:

| Situation | Mode |
|---|---|
| Unmanaged solution, or managed patch-like update with nothing removed | `update` |
| New managed version that removes components, applied now | `upgrade` |
| New managed version you want to check before applying | `stage`, then `promote` |
| Large solution or long-running import | add `--async` |

`--drop-data` (promote, uninstall) confirms the loss of tables with rows or columns with values. Never add it without
the user's explicit agreement: tell them what would be dropped first.

An `--async` import that exits 6 is **still running** on the server ("check asyncoperation <id>"): do not import again;
follow the job instead.

Solutions whose publisher is Microsoft (`msdyn`, `msdynce`, `adx`, ...) are first-party apps and are not supported on
xrmverse; the import logs a warning. Do not try to make them work.

## Inspect

```bash
xrmd solution list
xrmd solution layers AcmeCore                                  # layers this solution owns
xrmd solution layers --type entity --id <MetadataId>           # the stack of one component, top layer first
```

Use `layers` when "my change does not show": an unmanaged layer or a higher managed layer usually sits on top.

## Source control: unpack, pack, diff, sync

A solution kept in git as unpacked XML files (`Other/Solution.xml`, `Entities/<table>/Entity.xml`, `WebResources/`, ...), in
the layout Dataverse's own tools write. Unmanaged only.

```bash
xrmd solution unpack ./AcmeCore.zip --folder ./src/AcmeCore        # zip -> folder (offline)
xrmd solution pack ./src/AcmeCore --out ./AcmeCore.zip             # folder -> zip (offline), then solution import
xrmd solution diff AcmeCore --folder ./src/AcmeCore                # folder against the organization; exit 9 when they differ
xrmd solution diff AcmeCore --folder ./src/AcmeCore --zip ./a.zip  # against a zip instead, no server
xrmd solution sync AcmeCore --folder ./src/AcmeCore --dry-run      # preview, then without --dry-run to write
```

| Situation | Command |
|---|---|
| Put an existing zip into git | `unpack` |
| Deploy what is in git | `pack`, then `import` |
| Is the organization still what the repository says? (CI gate before an import) | `diff`: exit 0 same, 9 differs |
| Someone changed the organization; record it in git | `sync`, review `git diff`, commit |

`diff` prints one line per component: changed, missing (only in the folder) or new (only in the organization); `-o json`
gives an array. `sync` writes changed files and removes those of components the solution no longer holds; it keeps files
outside the component folders. Run `sync --dry-run` first when the folder has uncommitted work.

Exit codes here: 9 differences (`diff` only), 5 the solution has a component the layout cannot carry (canvas apps,
business rules, business process flows, `.resx` and others; nothing is written), 2 a managed zip or
a bad folder, 4 solution not found, 3 not allowed, 6 unreachable. A folder first written by Dataverse's tools shows
differences until one `sync`; run it once after the first import and commit the result.

## Remove

| Command | Effect |
|---|---|
| `xrmd solution uninstall <managed> --dry-run` | prints what would be deleted, restored and kept; changes nothing (always run first) |
| `xrmd solution uninstall <managed>` | deletes what only it delivered, restores the layer below for the rest; `--drop-data` if data would go |
| `xrmd solution delete <name>` | as in Dataverse: removes only the solution container; components stay (Default solution) |
| `xrmd solution delete <name> --cascade` | **xrmverse-only, irreversible**: also drops components no other solution holds, tables and their data included |

All of these are destructive and ask for confirmation. For `--cascade`, list the components first
(`xrmd solution layers <name>`) and get an explicit yes for dropping them and their data.

## Pitfalls

- Exit 5 on import: read the printed import log; it names the component and the reason. Things xrmverse cannot reproduce
  faithfully are refused explicitly rather than imported half-way.
- A component name whose prefix has no publisher is refused; the publisher of the solution being imported counts as
  existing.
- If changed forms or views do not show after an import, run `xrmd solution publish`, then check `layers`.
