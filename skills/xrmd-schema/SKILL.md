---
name: xrmd-schema
description: Design and change the data model of an xrmverse organization with the xrmd CLI - publishers and prefixes, tables, columns, lookups, choices, many-to-many relationships, schema-as-code YAML files (`xrmd apply`), plus the table-level features currencies, environment variables, change tracking and duplicate detection. Use this whenever someone wants to create or change tables or columns in xrmverse, write or apply a schema.yaml, add a lookup or option set, set up a publisher prefix, turn on notes/activities or change tracking, configure duplicate detection rules, add a currency or exchange rate, or set an environment variable value, even if they phrase it as "model this entity", "add a field" or "create the data model for ...".
---

# xrmd: the data model

xrmverse follows the Dataverse metadata model. You can change it **imperatively** (`xrmd table ...`) or **declaratively**
with a schema YAML file (`xrmd apply`). Prefer the YAML file for anything beyond one or two columns: it is reviewable,
repeatable, and has a real dry run.

## Working with xrmd (all skills)

- Every command runs as the signed-in profile's user (metadata changes need customization privileges; a System Administrator profile has them).
- `-o json` for parseable output; `--profile <name>` selects the organization.
- `xrmd <group> <command> --help` is the truth when an option is unclear.
- Destructive commands need `--yes` without a terminal: pass it only after the user confirmed that action.
- Exit codes: 0 ok, 1 unexpected/declined, 2 usage, 3 authentication or privilege, 4 not found, 5 rejected by the server
  (the message says why), 6 unreachable.

## Step 1: the publisher prefix

Every custom name is `{prefix}_{name}`, and the prefix must belong to an existing publisher (the default publisher has
`new`). Create the publisher first:

```bash
xrmd publisher list
xrmd publisher create --unique-name acme --friendly-name "Acme" --prefix ac --option-value-prefix 12000
```

Prefix: 2 to 8 lowercase letters or digits, starting with a letter, not `mscrm`. Option value prefix: 10000 to 99999 (it
becomes the start of custom choice values, e.g. 12000 → 120000000). The prefix is compared exactly: `AC_x`, `ac_` and `acx_y`
are refused.

## Step 2a: schema as code (recommended)

```bash
xrmd apply schema.yaml --diff     # dry run: + change, = already there, ! rejection; writes nothing
xrmd apply schema.yaml            # writes; any rejection exits 5
```

The dry run executes the plan in a transaction that is rolled back, so a `!` in `--diff` is exactly what the real apply
would refuse. Always run `--diff` first and show the user the plan before writing.

The format is **additive** (it creates and updates, never deletes) and **strict**: an unknown key, a name without the
prefix, a duplicate, an unsupported type or a parameter the type does not take is an error with line and column. Nothing
is silently ignored. Read [references/schema-yaml.md](references/schema-yaml.md) before writing a file; a minimal one:

```yaml
publisher: ac                    # the prefix of an existing publisher
solution: AcmeCore               # optional: components go into this unmanaged solution
choices:
  - name: ac_priority
    label: Priority
    options: [{ value: 120000000, label: Low }, { value: 120000001, label: High }]
tables:
  - name: ac_project
    label: Project
    pluralLabel: Projects
    primaryName: { name: ac_name, label: Name, maxLength: 200 }
    columns:
      - { name: ac_budget, type: money, label: Budget }
      - { name: ac_priority, type: choice, choice: ac_priority, label: Priority }
      - { name: ac_accountid, type: lookup, target: account, label: Account }
```

## Step 2b: imperative commands

```bash
xrmd table create ac_project --label Project --plural-label Projects [--ownership organization] [--notes] [--activities] [--solution AcmeCore]
xrmd table create ac_visit --label Visit --plural-label Visits --activity        # a custom activity table
xrmd table column add ac_project ac_budget --type money --label Budget
xrmd table column add ac_project ac_accountid --type lookup --target account     # creates the 1:N relationship too
xrmd table column add ac_project ac_customerid --type customer                   # account or contact, two relationships
xrmd table choice add ac_status2 --label "Stage" --options "Draft=120000000,Live=120000001"
xrmd table column add ac_project ac_stage --type choice --choice ac_status2
xrmd table relationship add ac_project --to ac_tag --many-to-many
xrmd table list --custom
xrmd table describe ac_project                                                   # verify what you made
```

Column types: `string, memo, int, decimal, money, bool, datetime, choice, choices, lookup, customer, file, image`.

## One-way and destructive changes (confirm with the user first)

| Change | Why it needs care |
|---|---|
| `table set <t> --notes` / `--activities` | cannot be switched off afterwards |
| `change-tracking enable <t>` | cannot be undone; there is no disable |
| `table column remove <t> <c>` | deletes the column **and its data** |
| `table delete <t>` | deletes the table and its rows; refused while another component refers to it |
| `--activity` (custom activity) | set at creation only |

A column cannot switch between simple and calculated, a table cannot change its `tableType` (Standard/Elastic), and the
schema YAML never deletes: removing a column from the file does nothing.

## Table-level features

**Currencies** (System Administrator):

```bash
xrmd currency list
xrmd currency create USD --name "US dollar" --symbol '$' --rate 1.10 [--precision 2]   # 1 base = 1.10 USD
xrmd currency rate USD 1.12          # records keep their old rate until a save changes their money or currency
xrmd currency deactivate USD         # the base currency cannot be deactivated
xrmd currency pricing-precision 3
```

**Environment variables** (values per organization; definitions come with solutions):

```bash
xrmd envvar list
xrmd envvar get ac_ServiceUrl        # definition, default, current value and the value in effect
xrmd envvar set ac_ServiceUrl https://api.example.com   # not checked against the type; Secret variables are refused
xrmd envvar unset ac_ServiceUrl      # back to the default value
```

**Change tracking** (delta queries for integrations):

```bash
xrmd change-tracking status [--table ac_project]
xrmd change-tracking enable ac_project      # one-way
xrmd change-tracking retention 14           # days deleted rows stay in the log, 0 to 365
```

**Duplicate detection**: switches, rules, publishing, finding.

```bash
xrmd duplicate-detection enable                      # organization switch
xrmd duplicate-detection enable --create-update      # check on create/update
xrmd duplicate-detection enable --table contact
xrmd duplicate-detection rule create --name "Same email" --base contact --matching contact --condition emailaddress1:exact --exclude-inactive
xrmd duplicate-detection rule publish <rule-id> --wait       # rules are created unpublished; id or unique name
xrmd duplicate-detection find --table contact --id <id>
xrmd duplicate-detection bulk --table contact --wait          # stops at 5000 duplicate rows
```

A `--condition` is `<attr>[:<matchattr>]:<exact|first:N|last:N|date|datetime|value>[:ignore-blank]`, repeatable.
`--wait` that times out exits 6 while the job keeps running on the server: report it, do not restart it.

## Pitfalls

- Exit 5 with a list of prefixes: the name's prefix has no publisher. Create the publisher or fix the name.
- A lookup target must be a table of the same schema file or one that already exists; `apply` creates tables before lookups.
- `data import` and the UI need the metadata first: model, then load data (see the xrmd-data skill).
- What xrmverse cannot reproduce faithfully is refused with an explicit error, never approximated. Report the message to
  the user instead of looking for a workaround.
