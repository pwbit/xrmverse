---
name: xrmd-data
description: Read and move records in an xrmverse organization with the xrmd CLI - FetchXML and OData queries, CSV/JSON export and import, sample data, and organization databases (create, list, back up, restore, delete). Use this whenever someone wants to query xrmverse data, count or list records, load a CSV into a table, export a table, seed demo data, copy data between organizations, back up or restore an organization, or create a new empty organization database, even if they say "how many accounts do we have", "load this spreadsheet" or "make a backup before I try this".
---

# xrmd: data, backups and organizations

## Working with xrmd (all skills)

- Every command runs as the signed-in profile's user, so data commands see exactly what that user's security allows.
  `org` commands use the Admin API and need System Administrator.
- `-o json` for parseable output (always use it when you post-process results); `--profile <name>` selects the
  organization.
- `xrmd <group> <command> --help` is the truth when an option is unclear.
- Destructive commands need `--yes` without a terminal: pass it only after the user confirmed that action.
- Exit codes: 0 ok, 1 unexpected/declined, 2 usage, 3 authentication or privilege, 4 not found, 5 rejected (or failed
  rows on import), 6 unreachable, 7 restore done but files missing.

## Query

```bash
xrmd data query --odata "accounts?\$select=name,revenue&\$filter=statecode eq 0&\$orderby=name" --top 20
xrmd data query --odata "accounts?\$apply=aggregate(\$count as total)" -o json
xrmd data query --fetch @active-projects.xml --all -o json          # FetchXML from a file, every page
xrmd data query --fetch '<fetch top="5"><entity name="contact"><attribute name="fullname"/></entity></fetch>'
```

- `--odata` is relative to the Web API service root and uses the **entity set name** (plural, e.g. `accounts`), not the
  logical name. Quote it and escape `$` in shells.
- Without `--all` you get one page; use `--all` for complete results and `--top` to cap them.
- Aggregates (FetchXML `aggregate="true"` or OData `$apply`) stop at 50,000 records (error `0x8004E023`): filter first.
- Use the logical names from `xrmd table describe <table>`; lookups are read as `_<column>_value` in OData.
- What the query engine cannot reproduce faithfully is refused with an error, not answered approximately: report it.

## Export and import

```bash
xrmd data export ac_project --format csv --out projects.csv [--filter "statecode eq 0"]
xrmd data import ac_project --file projects.csv --format csv [--batch 100]
xrmd data export account --format json --out accounts.json --profile dev
xrmd data import account --file accounts.json --format json --profile test        # copy between organizations
```

- `data export` writes files that `data import` accepts; columns that are not plain data (image, file, party lists,
  unsupported types) are skipped with a "Skipped column ..." line on stderr.
- CSV lookups are written `table:guid` (customer columns `account:guid` or `contact:guid`).
- `--batch` is the number of rows **in flight in parallel, unordered**. When rows reference each other (parents before
  children, a self-referencing lookup), import parents first or use `--batch 1`.
- Each row is its own request: failed rows are listed on stderr with their row numbers and the command exits 5, while
  the other rows are created. Fix and re-import only the failed rows, otherwise the successful ones are created twice.
- Import creates rows; it does not update or upsert existing ones.
- The table and its columns must exist first (see the xrmd-schema skill).

## Sample data

```bash
xrmd dev seed ac_project --count 50 --seed 42      # plausible rows; same seed, same data
```

Lookups are bound to existing rows, so seed parent tables first.

## Organizations and backups (Admin API, System Administrator)

```bash
xrmd org list
xrmd org create contoso --admin-object-id <entra-oid> [--admin-name "Jane Doe"]
xrmd org backup contoso                         # prints a path ON THE SQL SERVER HOST, plus <name>.files.tar beside it
xrmd org restore contoso --file <server-side path> [--overwrite]
xrmd org restore contoso_copy --file <server-side path>             # restore under another name
xrmd org delete contoso_old                      # destructive; not the database the server serves
```

- `--file` is a path on the SQL Server host (the backups volume), not a local file. To fetch one with the generated
  compose: `docker compose cp sqlserver:/var/opt/mssql/backup/<file> .`
- The backup has two parts: the `.bak` and `<name>.files.tar` (file and image columns). `restore` needs both unless
  `--no-files` (database only, blobs untouched).
- Exit 7 after a restore: the database is restored, but some files it references are missing from the store (listed on
  stderr). Report the list; it is not a failed restore.
- `--direct --connection-string "..." --no-files --overwrite` restores from this machine straight into SQL Server for the
  organization the server serves; stop the server first.
- `--overwrite` replaces the target database: confirm with the user. Suggest `org backup` before any risky change
  (solution import with `--overwrite`, `solution delete --cascade`, schema clean-up).
- `org create` makes an empty organization in the same SQL Server. For a new organization with its own service, port and
  profile in a Docker installation, use `xrmd server org add` instead (xrmd-server skill).
