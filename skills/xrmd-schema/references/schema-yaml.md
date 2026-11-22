# Schema YAML reference (`xrmd apply`)

One file describes choices and tables of one publisher prefix. Every value is read as text and typed by the key; nothing
unknown is ignored.

## Document

| Key | Required | Meaning |
|---|---|---|
| `publisher` | yes | prefix of an existing publisher; every custom name starts with `<prefix>_` |
| `solution` | no | unique name of the unmanaged solution that receives the created components |
| `choices` | no | global choices (option sets) |
| `tables` | no | tables |

## Choice

| Key | Meaning |
|---|---|
| `name` | logical name with the prefix |
| `label`, `description` | text |
| `options` | list of `{ value: <int>, label: <text> }`; values usually start from the publisher's option value prefix × 10000 |

## Table

| Key | Meaning |
|---|---|
| `name` | logical name with the prefix |
| `schemaName` | default: the logical name |
| `label`, `pluralLabel`, `description` | text |
| `ownership` | `user` (default) or `organization`; cannot change later |
| `tableType` | `Standard` (default) or `Elastic`; cannot change later |
| `isActivity` | `true` for a custom activity: `primaryName` must be `{ name: subject }`, user owned, notes on, no `hasActivities` |
| `hasNotes`, `hasActivities` | `true` switches them on; one-way (false on an existing true is a rejection) |
| `primaryName` | `{ name, schemaName?, label?, description?, maxLength?, autoNumberFormat? }` (a string column; default max length 100) |
| `columns` | list of columns |
| `manyToMany` | list of `{ name: <relationship schema name>, with: <other table> }`; the intersect table takes the relationship's name |

## Column

Common keys: `name` (with prefix), `schemaName`, `type`, `label`, `description`.

| `type` | Extra keys |
|---|---|
| `string` | `maxLength`, `autoNumberFormat` (e.g. `PRJ-{SEQNUM:5}`), `calculated`, `formula` |
| `memo` | `maxLength` |
| `int` | `calculated`, `formula` |
| `decimal` | `precision`, `calculated`, `formula` |
| `money` | `calculated` (the server adds the `_base` column) |
| `bool` | `calculated`, `formula` (options Yes = 1, No = 0) |
| `datetime` | `calculated`, `formula` |
| `choice` | `choice: <global choice>` **or** `options: [...]` (local), `calculated` |
| `choices` | `choice` or `options` (multi-select) |
| `lookup` | `target: <table>` (in this file or already in the organization) |
| `customer` | none: always refers to `account` and `contact` |
| `file` | `maxSizeInKB` |
| `image` | `maxSizeInKB`, `canStoreFullImage`, `primary: true` (at most one per table; only `true` is written) |

`calculated` is the XAML of a calculated column's formula definition, written as a literal block (`calculated: |`). A
column cannot switch between simple and calculated.

`formula` is the Power Fx text of a formula column (`SourceType` 3), for example
`{ name: ac_label, type: string, formula: 'Upper(ac_code) & "-" & Text(ac_qty, "0")' }`; a multi-line formula is a literal block
(`formula: |`). It is allowed on `string`, `int`, `decimal`, `bool` and `datetime` only, cannot be empty, cannot be combined
with `calculated`, `rollup` or `autoNumberFormat`, and a column cannot switch between simple, calculated, rollup and formula.
A leading `=` is optional in the file: the CLI sends the formula without it (the server, like Dataverse's Web API, refuses one
that starts with `=`), and `=x` and `x` compare as the same formula. Write the formula with `,` between arguments (the invariant
form Dataverse stores and exports), whatever your language; a related column is read through the lookup's navigation property
(`ac_AccountId.name`). The server checks the formula when the file is applied (functions, types, blank rules: see the formula
columns page of the docs). A
`formula:` on `money` or `choice` is refused by the CLI itself with `Column '<name>': formula applies only to the types string,
int, decimal, bool and datetime, not to 'money'.`, a text that differs from the server's refusal of the same column
(`A formula column cannot be of type Money: …`, `Choice formula columns are not supported yet: …`). A Float formula column has
no schema type, so a schema export leaves it out.

## What an apply can and cannot change on existing components

| Allowed (update) | Rejected (`!` in `--diff`, exit 5) |
|---|---|
| labels and descriptions | type change |
| a larger `maxLength` | a smaller `maxLength` |
| a new `autoNumberFormat` | another lookup target, precision, ownership or primary name |
| new options, option labels | a choice column bound to a different choice |
| `hasNotes` / `hasActivities` switched on | switching them off, changing `isActivity` |
| new tables, columns, lookups, N:N, choices | a reference to a table or choice that exists nowhere |

Components with the prefix that the file does not mention are reported as extras (`=`) and left alone: `apply` never
deletes. To remove something use `xrmd table column remove` / `xrmd table delete` after the user confirms.

## Full example

```yaml
publisher: ac
solution: AcmeCore
choices:
  - name: ac_priority
    label: Priority
    options:
      - { value: 120000000, label: Low }
      - { value: 120000001, label: High }
tables:
  - name: ac_tag
    label: Tag
    pluralLabel: Tags
    ownership: organization
    primaryName: { name: ac_name, label: Name }
  - name: ac_project
    label: Project
    pluralLabel: Projects
    hasNotes: true
    hasActivities: true
    primaryName: { name: ac_name, label: Name, maxLength: 200, autoNumberFormat: "PRJ-{SEQNUM:5}" }
    columns:
      - { name: ac_budget, type: money, label: Budget }
      - { name: ac_start, type: datetime, label: Start }
      - { name: ac_priority, type: choice, choice: ac_priority, label: Priority }
      - { name: ac_accountid, type: lookup, target: account, label: Account }
      - { name: ac_customerid, type: customer, label: Customer }
      - { name: ac_logo, type: image, maxSizeInKB: 1024, primary: true }
    manyToMany:
      - { name: ac_project_ac_tag, with: ac_tag }
  - name: ac_visit
    label: Visit
    pluralLabel: Visits
    isActivity: true
    primaryName: { name: subject }
```
