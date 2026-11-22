---
name: xrmd-security
description: Manage who can see and change what in an xrmverse organization with the xrmd CLI - users, security roles, the local identity provider (passwords, TOTP, OAuth clients), field-level security, manager hierarchy security, access teams and auditing. Use this whenever someone wants to add a user, assign a role, let someone sign in with a password, create an app client for an integration, hide or protect a column, give a manager access to their reports' records, share a record with a team of users, or switch on, read or clean up the audit history in xrmverse, even when they describe it in Dataverse terms ("field security profile", "access team template", "turn on auditing for account").
---

# xrmd: users, roles and security

xrmverse reproduces the Dataverse security model (business units, roles, sharing, field security, hierarchy, access teams)
and auditing. The `xrmd` commands below change it through the Web API or Admin API as the signed-in user.

## Working with xrmd (all skills)

- Every command runs as the signed-in profile's user; missing privileges are a 403 (exit 3). Most of what follows needs
  a System Administrator profile. Check with `xrmd auth status`.
- `-o json` for parseable output, `--profile <name>` to pick the organization's profile.
- `xrmd <group> <command> --help` is the truth when an option is unclear.
- Destructive commands need `--yes` without a terminal: pass it only after the user confirmed that action.
- Passwords are never arguments: `--password-stdin` reads one line from standard input. Do not echo or log them.
- Exit codes: 0 ok, 1 unexpected/declined, 2 usage, 3 authentication or privilege, 4 not found, 5 rejected by the server,
  6 unreachable.

A `<user>` is accepted as a `systemuserid`, an Entra object id (GUID) or a domain name (sign-in name).

## Users and roles

```bash
xrmd user list
xrmd user add --object-id <entra-oid> --name "Jane Doe" [--domain-name jane@contoso.example] [--business-unit "Sales"]
xrmd role list
xrmd role assign "Salesperson" --user jane@contoso.example
```

- `user add` creates a user from an Entra identity; the default business unit is the root one.
- Roles exist per business unit: `role assign` uses the role of that name in the user's business unit.
- A new user has no access until a role is assigned (as in Dataverse).

## Local identity provider (servers created with `--auth local`)

```bash
printf '%s\n' "$NEW_PASSWORD" | xrmd idp user set-password jane@contoso.example --password-stdin   # >= 12 chars, clears a lockout
xrmd idp user totp enable jane@contoso.example        # prints the otpauth:// URI ONCE: hand it to the user, do not store it
xrmd idp user totp disable jane@contoso.example
xrmd idp user remove jane@contoso.example             # removes only the local credential; the user stays
xrmd idp client add --name "ERP sync" --app-user erp-sync@contoso.example   # confidential client; secret printed ONCE
xrmd idp client add --name "SPA" --public --redirect-uri https://app.example.com/callback
xrmd idp client list
xrmd idp client remove <client-id>                    # xrmd-cli and xrmd-web cannot be removed
```

For an integration: create a user for it (`user add`), give it a role, then `idp client add --app-user <that user>`. The
integration signs in with client credentials and acts as that user. Tell the user to store the printed secret now: it is
never shown again.

## Field-level security

Order matters: secure the column, then grant through a profile.

```bash
xrmd fieldsecurity secure account xv_iban                       # only System Administrators can read/write it now
xrmd fieldsecurity profile create "Finance" --description "Bank data"
xrmd fieldsecurity permission set "Finance" account xv_iban --read --update   # an option left out is not allowed
xrmd fieldsecurity assign "Finance" --user jane@contoso.example  # or --team "Finance team"
xrmd fieldsecurity profiles
```

`unassign`, `permission remove`, `profile delete` (destructive: the profile's users and teams lose it) and `unsecure` (drops
the column's permissions and field shares) undo each step.

## Manager hierarchy security (Admin API, System Administrator)

```bash
xrmd hierarchy-security show
xrmd hierarchy-security enable
xrmd hierarchy-security depth 3           # 1 to 100 levels of reports
xrmd hierarchy-security exclude xv_payslip
xrmd hierarchy-security include xv_payslip
xrmd hierarchy-security disable
```

Managers come from `systemuser.parentsystemuserid`; set it on the users before expecting an effect.

## Access teams

```bash
xrmd access-team enable account                                   # user- or team-owned tables only
xrmd access-team template create --table account --name "Deal reviewers" --rights read,write
xrmd access-team template list --table account
xrmd access-team add    --table account --record <accountid> --user jane@contoso.example --template "Deal reviewers"
xrmd access-team remove --table account --record <accountid> --user jane@contoso.example --template "Deal reviewers"
xrmd access-team template delete <templateid>                    # destructive: also deletes the teams made from it
xrmd access-team disable account                                  # refused while the table has templates
```

`--rights` takes `read, write, append, appendto, delete, share, assign`. Looking up a template **by name** reads
`teamtemplate`, which only System Administrators can: other callers must pass the template id.

## Auditing

A change is audited only when three switches are on: organization, table and column. The organization switch is off by
default; most columns are on by default; tables vary.

```bash
xrmd audit status                                    # organization switch, retention, audited tables
xrmd audit enable                                    # organization
xrmd audit enable --table xv_project                 # table
xrmd audit disable --table xv_project --column xv_notes   # one column out
xrmd audit retention 365                             # days, or --forever
xrmd audit history xv_project <id> [--column xv_budget] [--top 50]
xrmd audit delete --table xv_project --id <id>       # one record's history (destructive)
xrmd audit delete --before 2026-01-01                # whole calendar quarters ended by that date, never the current one
```

A setting fixed by the system (for example the primary key column) is refused with exit 5: report it, do not retry.

## Pitfalls

- Exit 3 on these commands is usually a missing privilege of the profile's user, not a broken token: check the roles.
- Prefer teams over many individual assignments for field security and roles; it is what the model was built for.
