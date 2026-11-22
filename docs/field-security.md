# Field-level security

[← xrmverse documentation](../README.md#documentation)

## Introduction

Field-level security protects single columns: a secured column is readable and writable only by System Administrators and by the
users and teams that a field security profile, or a field share on one record, lets in. The Microsoft documentation
describes the feature in
[Column-level security to control access](https://learn.microsoft.com/power-platform/admin/field-level-security) and
[Column-level security by using code](https://learn.microsoft.com/power-apps/developer/data-platform/column-level-security).
This page says what xrmverse does and where it stops. Whether each detail matches a real Dataverse is not verified
(see the checklist).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Securing a column | `IsSecured`, profiles with permissions, field sharing | Same |
| Who bypasses it | System Administrators | Same |
| Masking | Not supported: `canreadunmasked` other than 0 and `UnMaskedData` true are refused | **Narrower** |
| `RetrieveUserPrivileges`, `RetrievePrincipalAccess`, `RetrieveAttributePrivileges` | Not implemented | **Narrower** |
| Hierarchy security | Does not widen column access | See [Known limits](#known-limits) |
| Verification | Type codes, securable columns and some codes not verified | **Unverified** where stated |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Secure a column | `xrmd fieldsecurity secure`, web admin **Secured** switch, `UpdateAttribute` | [Securing a column](#securing-a-column) |
| Let users read or update it | Profiles and permissions | [Profiles and permissions](#profiles-and-permissions) |
| Open one column of one record to one user | Field sharing | [Field sharing](#field-sharing) |
| Move profiles between organizations | Solutions | [Solutions](#solutions) |

## Securing a column

`AttributeMetadata.IsSecured` is set with `CreateAttribute` or `UpdateAttribute`, the Web API
`PUT EntityDefinitions(…)/Attributes(…)`, `xrmd fieldsecurity secure|unsecure`, the **Secured** switch of the web admin,
or a solution; it needs `prvWriteAttribute`, like any other change of a column. Turning `IsSecured` off also needs read,
create and update permission on that column (through profiles; System Administrators bypass), so a System Customizer cannot
lift the protection of a column it cannot read: otherwise 403 `0x8004F508`, with nothing removed. A solution import that
turns a secured column off is checked the same way. Every column definition reports
`CanBeSecuredForCreate`, `CanBeSecuredForRead` and `CanBeSecuredForUpdate`, computed by the server (a value you send is
ignored):

- **Securable** (all three true): String, Memo, Integer, BigInt, Decimal, Double, Money, Boolean, Picklist,
  MultiSelectPicklist, DateTime, File and Image columns, custom or system.
- **Not securable** (all three false): the primary id and primary name; Lookup, Customer and Owner columns; `statecode`,
  `statuscode`, `createdon`, `modifiedon`, `createdby`, `modifiedby`, `ownerid` and `versionnumber`; the companion
  columns of another column (`*name`, `*yominame`, `*_base`, `*_url`, `*_timestamp`); and every column of the five field
  security tables, of `audit` and of the security tables (users, teams, roles, business units and their links).
  `IsSecured` true on such a column is a 400 with code `0x8004F501`.

Turning `IsSecured` off removes the column's permissions from every profile and its field shares, and so does deleting the
column; deleting a table removes every permission naming it and the field shares of its records. A column created later
with the same name therefore starts with no grants. `describe_table` (MCP) marks secured columns with `"secured": true`.

### Companion columns follow their column

A secured Money column's `_base` value, a secured File column's file name
(`<column>_name`) and a secured Image column's id, timestamp and url (`<column>id`, `<column>_timestamp`, `<column>_url`)
are protected with the rights of the column itself: they read as null (or are left out of the JSON), a filter, order or
aggregate on them is the same 403 `0x8004F504`, naming one in a Create or Update is the 403 of the column, and a field
share on the column covers them on that record. The `fileattachment` row of a secured File column, or of a secured
Image column's full-size image, holds the same details (file name, type, size): a caller whose profiles do not grant
read on the column gets no such row, whether it queries `fileattachment` directly, retrieves the row by id or links to it
from the record.

## Who reads and writes a secured column

- **Everyone holding the System Administrator role** (directly or through an owner team), the SYSTEM user and the
  server's own work: always, with or without a profile.
- **Everyone else**, System Customizer included: only what their profiles allow. A user's profiles are those given to
  the user plus those of every team the user belongs to (owner and access teams); per column the permissions add up, so
  one profile allowing read is enough. Under impersonation both the impersonator and the impersonated user must be
  allowed. A plug-in's service is checked as the user it runs as.
- Changes to profiles, permissions, profile assignments, team membership and `IsSecured` apply to the next request, with
  no restart.

## Profiles and permissions

A profile is a `fieldsecurityprofile` row; each of its `fieldpermission` rows names a table
(`entityname`) and a secured column (`attributelogicalname`) and sets `canread`, `cancreate` and `canupdate` to 4
(Allowed) or 0 (Not Allowed, the default). A permission on a column that is not secured is a 400 (`0x8004F50E`), and
one on a table or column that does not exist a 404; the same holds when an Update moves a permission to another column.
Profiles are given to users and teams with `Associate` on `systemuserprofiles_association` and
`teamprofiles_association` (direct writes on the intersects `systemuserprofiles` and `teamprofiles` are refused), which
needs `prvWriteFieldSecurityProfile`. Deleting a profile deletes its permissions and its assignments. Only the System
Administrator role holds the profile and permission privileges; System Customizer and Basic User hold none, so they cannot
change profiles directly or through a solution import (see [Solution](solutions.md)).

The **System Administrator** profile (id `572329c1-a042-4e22-be47-367c6374ea45`, from memory and not verified) is seeded
in every organization, has no permissions and cannot be changed or deleted, and no permission can be added to it (403,
`0x8004F50C`). Holding it changes nothing: the System Administrator role already bypasses field security.

## Reads

A secured column the caller cannot read on a record is handled the same way wherever it was asked for (a
column set, `AllColumns`, `$select`, `$expand`, a FetchXML attribute or link-entity):

- the SDK and SOAP return the column's key with a null value, so a protected value looks like an empty one;
- the Web API leaves the property out of the JSON;
- the value is not read from SQL, unless a field share may grant it on some of the rows (then it is read and blanked per
  row);
- a **filter, order, aggregate, `groupby` or link join** on it is refused before any SQL runs, with 403 `0x8004F504`, in
  FetchXML (nested filters too), `QueryExpression`, `QueryByAttribute`, `$filter`, `$orderby` and the options of an `$expand`. It is an error, never an empty result, also when a field share would grant the column on some rows. `count(*)`
  is allowed;
- `RetrieveDuplicates` is refused with the same 403 when a published duplicate detection rule it uses compares such a column
  (the rule's conditions are filters on the matches; see [Duplicate detection](duplicate-detection.md#retrieveduplicates));
- downloading a secured File or Image column needs read (403 `0x8004F504`); the companion columns follow the column (see
  above);
- the audit messages leave out the old and new values of such columns, and `RetrieveAttributeChangeHistory` on one is a
  403 `0x8004F504`. The `audit` table keeps the full values for administrators.

## Writes

Checked before the pipeline, on the SDK, SOAP and Web API paths:

- **Create**: a secured column sent with a value needs create permission (403 `0x8004F502`); sent as null it is
  accepted, since it writes nothing.
- **Update**: a secured column present in the request needs update permission, or a
  field share with update access on that record (403 `0x8004F507`), even when the value is unchanged or null.
- Starting an upload to, or deleting, a secured File or Image column needs update permission (403
  `0x8004F507`).
- A 403 `0x8004F507` raised by a request a plug-in makes through its organization service reaches the original caller as
  the 400 that wraps every plug-in failure, with the plug-in's message.

## Field sharing

A `principalobjectattributeaccess` row gives one user or team read and/or update on one secured
column (`attributeid`, the column's `MetadataId`) of one record (`objectid`), with `readaccess` and `updateaccess`.
Shares are created, changed and deleted with plain Create, Update and Delete (entity set
`principalobjectattributeaccessset`). The server checks, in this order:

1. the column is secured (400 `0x8004F50E`);
2. at least one access is granted (400 `0x8004F509` on create, `0x8004F50A` on update; delete the share instead);
3. the same column of the same record is not already shared with that principal (400 `0x8004F50B`; a unique index
   enforces it under concurrency);
4. the caller can read the record (the usual record-level 403);
5. the caller can update the column on that record and, when the share grants read, also read it there (403
   `0x8004F503`): nobody gives more than they have. Deleting a share needs the same rights as granting it.

Only `readaccess` and `updateaccess` change on Update. A share given to a user or team that does not exist is a 404. A
share on a team applies to every member. The shares of a record
are deleted with the record, those of a user or team with the principal. The Basic User role holds create, read, write
and delete on `principalobjectattributeaccess` (Global) in organizations created by this version.

## Error codes

Values from the Microsoft documentation, messages in our own words:

| Code | HTTP | When |
|---|---|---|
| `0x8004F501` (-2147158783) | 400 | `IsSecured` on a column that cannot be secured |
| `0x8004F502` (-2147158782) | 403 | Create sets a secured column without create permission |
| `0x8004F503` (-2147158781) | 403 | a field share grants more than the sharer holds |
| `0x8004F504` (-2147158780) | 403 | reading, filtering, ordering, aggregating or joining on a secured column without read permission |
| `0x8004F507` (-2147158777) | 403 | Update (or a file upload) of a secured column without update permission |
| `0x8004F508` (-2147158776) | 403 | `IsSecured` turned off without read, create and update permission on the column (code from memory, not verified) |
| `0x8004F509` (-2147158775) | 400 | a new field share grants neither read nor update |
| `0x8004F50A` (-2147158774) | 400 | an Update leaves a field share with neither read nor update |
| `0x8004F50B` (-2147158773) | 400 | the column of that record is already shared with that principal |
| `0x8004F50C` (-2147158772) | 403 | the System Administrator profile is changed or deleted |
| `0x8004F50E` (-2147158770) | 400 | a permission or field share on a column that is not secured |
| `0x80040217` (-2147220969) | 404 | a permission on a table or column that does not exist |

## Solutions

`IsSecured` travels with the column. Profiles are solution components of type 70: they are exported and
imported in `customizations.xml` under `FieldSecurityProfiles`. Each `FieldSecurityProfile` has the attributes
`name`, `description` and `fieldsecurityprofileid` and holds its `FieldPermission` elements in a `FieldPermissions`
element. A permission's values are child elements, in this order: `EntityName`, `AttributeName`, `CanRead`,
`CanUpdate`, `CanCreate`, `CanReadUnmasked`; `CanRead`, `CanUpdate` and `CanCreate` are 0 or 4, and `CanReadUnmasked` is
imported as 0. This shape is confirmed against a real unmanaged Dataverse export
(`tests/fixtures/solutions/RealExports/OpenProject365_1_0_1_1.zip`). Only the group names of a profile in the import log
(`fieldSecurityProfiles`, `fieldSecurityProfile`) are not yet compared with Dataverse (checklist). On import:

- a solution that carries profiles needs the create and write privileges on `fieldsecurityprofile` and `fieldpermission`
  (in practice the System Administrator role): otherwise the import fails with 403 before anything is written;
- a `FieldPermission` placed directly under its profile, without the `FieldPermissions` element, is not imported and the
  import log says so;

- the profile's permissions are replaced by those in the file;
- a permission on a column that is not secured in the organization is skipped with a warning, as is a second permission
  on the same column (the first one wins);
- `canreadunmasked` other than 0 is imported as 0 with a warning (no masking, see below);
- the System Administrator profile is never imported (warning), exported or deleted by a cascade solution delete, and
  `AddSolutionComponent` refuses it (400);
- users and teams holding a profile never travel.

## Existing organizations

Before this version, `IsSecured` was stored when a solution set it but nothing enforced it,
and profiles were not imported. From this version those columns are enforced: users other than System Administrators no
longer read them, and an Update that names one is refused, until a profile lets them in. Re-import the solution (its
profiles now travel with it) or create profiles with `xrmd fieldsecurity` or the web admin. To see which columns are
secured: the web admin Field security matrix lists them, each column of the **Tables** page shows its **Secured** switch,
`GET /api/data/v9.2/EntityDefinitions(LogicalName='<table>')/Attributes` returns `IsSecured` per column, and the MCP
`describe_table` tool marks them with `"secured": true`.

## Tools

`xrmd fieldsecurity` (`profiles`, `profile create|delete`, `permission set|remove`, `assign|unassign --user
|--team`, `secure|unsecure`; see [CLI xrmd](cli.md)) and the web admin Field security page and Secured switches (see
[Web admin](web-admin.md)). No new MCP tool and no Admin API endpoint.

## Known limits

Each of these is either refused with an error or stated here; none is a silent difference.

- **Hierarchy security does not widen column access.** A manager who reads a report's record through
  [hierarchy security](security.md#hierarchy-security) sees a secured column only through the manager's own profiles and
  field shares, never the report's. The record Read check of the field-share rules includes the hierarchy reach, so a
  manager with the column rights can share that column of a report's record.
- **Masking is not supported.** `canreadunmasked` other than 0 is a 400 on a direct write (on solution import it becomes
  0 with a warning), and no masking rules are seeded. Values are never masked, so asking for unmasked data
  (`UnMaskedData` true on Retrieve or RetrieveMultiple through the SDK, SOAP or a plug-in's organization service, or as a
  Web API query option, including on a single column's URL) is a 400; false, or leaving it out, is accepted.
- **The web admin record form** lets a user edit a secured column that the user can read but not update; saving then
  fails with 403 `0x8004F507`. The editor is not disabled ahead of time.
- **Existing organizations**: the Basic User role of an organization created before this version does not get the
  `principalobjectattributeaccess` privileges, so its users get a 403 when they share a column. Add the four privileges
  to the role (`AddPrivilegesRole`) to allow field sharing.
- **Distinct queries** return null for a secured column the caller cannot read through a profile, even on a record
  where a field share grants it.
- **Field shares do not reach `fileattachment` rows.** The row of a secured File or Image column is hidden from a caller
  without read on the column through a profile, even on a record where a field share grants that column; the file
  itself can still be downloaded there through the column. This errs on the side of hiding.
- **Plug-in images** are built for the plug-in's registered user and are not filtered: a pre- or post-image can carry a
  secured value. A column a pre-operation plug-in adds to the Target is not checked again.
- **`Upsert`** applies the checks above: on a standard table through the Create or Update it turns into; on an elastic
  table the create check when it inserts, and the update check (including the columns it clears) when it replaces the row.
  See [Bulk operations](bulk-operations.md) and [Elastic tables](elastic-tables.md).
- **Not implemented**: the `applicationuserprofile` relationship; `RetrieveUserPrivileges`, `RetrievePrincipalAccess`
  and `RetrieveAttributePrivileges` (planned with the web UI work). The [SQL endpoint](sql-endpoint.md#columns) applies field
  security: a secured column the user cannot read is `NULL`, and filtering on it is refused.
- **Calculated and rollup columns** that read secured columns: see the Security sections of
  [Calculated columns](calculated-columns.md#security) and [Rollup columns](rollup-columns.md#security). **Formula columns**
  cannot be secured, as in Dataverse; see [Formula columns](formula-columns.md#security).
- **Not verified against a real Dataverse**: the object type codes of the five tables (1200, 1201, 1202, 1203 and 44),
  the System Administrator profile id, which columns are securable, the Basic User privileges on field shares, the code
  `0x8004F508`, whether a System Customizer may import a solution that carries profiles, and the group names of profiles
  in the import log.

## See also

- [Security](security.md)
- [Web admin](web-admin.md)
- [CLI xrmd](cli.md)
