# Tables in every organization

[← xrmverse documentation](../README.md#documentation)

## Introduction

Every xrmverse organization is created with the system tables that Dataverse clients, plug-ins and solutions expect, with the
Dataverse logical names, plus `account` and `contact`. This page is the reference of those tables, grouped by the feature that
adds them; each feature page explains how they are used.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Names | Dataverse logical names, entity sets and relationship names | Same |
| Object type codes | Many are from memory; some are xrmverse's own (custom APIs, `organizationsetting`) | **Unverified** or **Differs** where stated: use logical names, not codes |
| Columns | Only the columns listed for each table are modelled | **Narrower**: other standard columns give the usual "attribute not found" 400 |
| Business tables | `account` and `contact` only | **Narrower**: other standard tables (`lead`, `opportunity`, …) are not seeded |
| Upgrades | An organization created by an earlier version gets new tables, columns and grants at startup | xrmverse behaviour |

### All tables at a glance

| Table | Entity set | Type code | Owner | Chapter |
|---|---|---|---|---|
| `account` | `accounts` | 1 | User | [Account and contact](#account-and-contact) |
| `contact` | `contacts` | 2 | User | [Account and contact](#account-and-contact) |
| `audit` | `audits` | 4567 | None | [Auditing](#auditing) |
| `fieldsecurityprofile`, `fieldpermission` | `fieldsecurityprofiles`, `fieldpermissions` | 1200, 1201 | Organization | [Field-level security](#field-level-security) |
| `systemuserprofiles`, `teamprofiles` | `systemuserprofilescollection`, `teamprofilescollection` | 1202, 1203 | Organization (intersects) | [Field-level security](#field-level-security) |
| `principalobjectattributeaccess` | `principalobjectattributeaccessset` | 44 | Organization | [Field-level security](#field-level-security) |
| `customapi`, `customapirequestparameter`, `customapiresponseproperty` | `customapis`, `customapirequestparameters`, `customapiresponseproperties` | 9990, 9991, 9992 | User | [Custom APIs](#custom-apis) |
| `entitymap`, `attributemap` | `entitymaps`, `attributemaps` | 4600, 4601 | Organization | [Table and column maps](#table-and-column-maps) |
| `environmentvariabledefinition`, `environmentvariablevalue` | `environmentvariabledefinitions`, `environmentvariablevalues` | 380, 381 | User | [Environment variables](#environment-variables) |
| `customcontrol` | `customcontrols` | 66 | Organization | [Code components](#code-components) |
| `organizationsetting` | `organizationsettings` | 9967 | Organization | [App settings and user settings](#app-settings-and-user-settings) |
| `serviceendpoint` | `serviceendpoints` | 4618 | Organization | [Webhooks](#webhooks) |
| `userentityuisettings` | `userentityuisettingsset` | 2500 | User | [Last form per user](#last-form-per-user) |
| `teamtemplate` | `teamtemplates` | 92 | Organization | [Access teams](#access-teams) |
| `duplicaterule`, `duplicaterulecondition`, `duplicaterecord` | `duplicaterules`, `duplicateruleconditions`, `duplicaterecords` | 4414, 4416, 4415 | User, Organization, User | [Duplicate detection](#duplicate-detection) |
| `transactioncurrency` | `transactioncurrencies` | 9105 | Organization | [Currencies, user settings and time zones](#currencies-user-settings-and-time-zones) |
| `usersettings` | `usersettingscollection` | 150 | User | [Currencies, user settings and time zones](#currencies-user-settings-and-time-zones) |
| `timezonedefinition` | `timezonedefinitions` | 4810 | None | [Currencies, user settings and time zones](#currencies-user-settings-and-time-zones) |
| `activitypointer`, `email`, `task`, `phonecall`, `appointment` | `activitypointers`, `emails`, `tasks`, `phonecalls`, `appointments` | 4200, 4202, 4212, 4210, 4201 | User | [Activities and notes](#activities-and-notes) |
| `activityparty` | `activityparties` | 135 | None | [Activities and notes](#activities-and-notes) |
| `annotation` | `annotations` | 5 | User | [Activities and notes](#activities-and-notes) |
| `workflow`, `processtrigger` | `workflows`, `processtriggers` | 4703, 4712 | User, Organization | [Classic workflows](#classic-workflows) |

The tables of [Glossary and synonyms](glossary-and-synonyms.md) are described on their own page.

## Account and contact

Every organization is seeded with `account` (object type code 1) and `contact` (2), user-owned, with activities and notes
enabled and the Dataverse logical names, so that solutions and code written for Dataverse find them.

| Table | Columns |
|---|---|
| `account` | `name` (primary), `accountnumber`, `telephone1`, `emailaddress1`, `websiteurl`, `address1_line1`, `address1_city`, `address1_postalcode`, `address1_country`, `parentaccountid` (lookup to `account`), `primarycontactid` (lookup to `contact`) |
| `contact` | `firstname`, `middlename`, `lastname`, `fullname`, `emailaddress1`, `telephone1`, `mobilephone`, `jobtitle`, the same four address columns, `parentcustomerid` (a Customer column: a lookup to `account` or `contact`, with one relationship per target) |

The tables are not customizable beyond what any system table allows: a column you add needs a publisher prefix (see
[Publishers and the prefix rule](cli.md#publishers-and-the-prefix-rule)), and a solution that adds one exports `account` as a
system table that carries only the added columns.

- **`fullname`** is computed on `Create` and `Update` from the first, middle and last name, following the organization's
  `fullnameconventioncode` (0 to 7, default 0: "Last, First"; 1: "First Last"). A `fullname` sent on `Create` is a 400; on
  `Update` the value that is already stored is ignored and a different one is a 400. Conventions 2 and 3 (the middle
  initial is written "M.") and 6 and 7 follow our reading of the labels in the Microsoft documentation and are **not
  verified against a real Dataverse**. Changing `fullnameconventioncode` does not recompute the contacts that exist.
- **Phones.** `telephone1` and `mobilephone` have the format Text, as in the Microsoft documentation; the `FormatName`
  value `Phone` is not modelled.
- **Existing organizations.** The tables are created at startup. If an organization already has a custom table named
  `account` or `contact`, the server does not start: it logs `Organization '<org>' already has a custom table named
  '<table>' ... Rename or delete the custom table, then restart the server.`
- **Relationships and their cascades**, with the names and the `CascadeConfiguration` of the Microsoft documentation:
  `contact_customer_accounts` (Delete **Cascade**: deleting an account deletes its contacts; Assign, Merge, Reparent, Share
  and Unshare Cascade), `contact_customer_contacts` (Delete RemoveLink: the contacts under a deleted contact lose their
  `parentcustomerid`; Assign, Merge, Reparent, Share and Unshare Cascade), `account_parent_account` (lookup
  `parentaccountid`, Delete RemoveLink; Assign, Merge, Reparent, Share and Unshare Cascade) and `account_primary_contact`
  (lookup `primarycontactid`, Delete RemoveLink: deleting a contact clears the `primarycontactid` of the accounts; Merge
  Cascade). Archive and RollupView are NoCascade. Only the Delete cascades are enforced (see "Delete cascades" in the
  [Solution](solutions.md) section); the others are stored and reported, not applied. An organization seeded by an earlier version gets
  these cascades at startup (its saved relationships keep their ids); the account lookups, which had no saved relationship
  and the generated names `account_parentaccountid_account` and `account_primarycontactid_contact`, now carry the
  Dataverse names (nothing stored referred to the generated ones; the collection-valued navigation properties of `account`
  and `contact` change name accordingly).
- **Customer columns.** A Customer column is created only with `CreateCustomerRelationships` (see the limits of the
  Solution section for the navigation properties and `$expand`). `CreateCustomerRelationships` is not declared in
  `$metadata`, and `$expand` of a Customer column is a 400.


## Auditing

Every organization also has the read-only **`audit`** table (object type code 4567, entity set `audits`, no owner), where
the audit rows are written when auditing is switched on; see [Auditing](auditing.md).

## Field-level security

Field-level security adds five organization-owned tables (see [Field-level security](field-security.md)):
`fieldsecurityprofile` (entity set `fieldsecurityprofiles`, object type code 1200), `fieldpermission`
(`fieldpermissions`, 1201), the intersects `systemuserprofiles` (`systemuserprofilescollection`, 1202) and `teamprofiles`
(`teamprofilescollection`, 1203), and `principalobjectattributeaccess` (`principalobjectattributeaccessset`, 44). The type
codes are from memory and not verified. Every organization holds the read-only System Administrator profile.

## Custom APIs

Custom API definitions use three user-owned tables, written by authoring or by solution import and delete (see
[Custom APIs](custom-apis.md)): `customapi` (entity set `customapis`, object type code 9990), `customapirequestparameter`
(`customapirequestparameters`, 9991) and `customapiresponseproperty` (`customapiresponseproperties`, 9992). The type codes
are xrmverse's own (Dataverse assigns these tables a code of 10000 or more per environment). In every organization the
Basic User role reads them at Global depth and System Customizer holds every privilege on them; an organization created
before them gets these grants once at startup.

## Table and column maps

Table and column maps (see [Model-driven apps](model-driven-apps.md#subgrids-and-quick-views)) use two organization-owned
tables: **`entitymap`** (entity set `entitymaps`, object type code 4600: `sourceentityname`, `targetentityname`) and
**`attributemap`** (`attributemaps`, 4601: the lookup `entitymapid`, `sourceattributename`, `targetattributename`,
`issystem`), linked by `entity_map_attribute_maps`. They hold the maps a solution imports; a request cannot write them (400).
The Basic User role reads them at Global depth; an organization created before them gets this grant once at startup.

## Environment variables

Environment variables use two user-owned tables: **`environmentvariabledefinition`** (entity set `environmentvariabledefinitions`,
object type code 380: `schemaname` (required, String 100), `displayname`, `description`, `defaultvalue`, `type` (String, Number,
Boolean, JSON, Data source, Secret), `isrequired`, `secretstore`, `valueschema`, `introducedversion`, `hint`) and
**`environmentvariablevalue`** (`environmentvariablevalues`, 381: `schemaname`, `value` and the required lookup
`environmentvariabledefinitionid`), linked by `environmentvariabledefinition_environmentvariablevalue` (deleting, assigning or
reparenting a definition does the same to its value). Clients create, update and delete both, with these rules (400 otherwise): a
definition's `schemaname` is unique without regard to case and cannot change; a value names an existing definition, a definition has
at most one value, and a value created without `schemaname` takes its definition's. The text is not checked against `type`. The
Basic User role reads both at Global depth and System Customizer holds every privilege on them; an organization created before
them gets these grants once at startup. The type codes are the solution component types of the two tables, assumed equal to the
object type codes. A definition is a solution component (type 380, see [Solutions](solutions.md)): it has the read-only
`ismanaged` and the import-only `xrmverse_componentxml` of the other row components; a value is not a component. How solutions, plug-ins and the CLI use them: [Environment variables](environment-variables.md).

## Code components

Code components (PCF controls) use the organization-owned table **`customcontrol`** (entity set `customcontrols`, object type code 66, assumed equal to the solution component type "Custom Control"): `name` (required, String 200, the control's `namespace.constructor`), `compatibledatatypes` (String 1000), `manifest`, `version` (String 40), `clientjson`, `authoringmanifest`, `introducedversion`, with the read-only `ismanaged` and the import-only `xrmverse_componentxml` of the other row components. Its rows come from solution import only: a Create, Update or Delete by a request is refused (400 "Custom controls are created by solution import in this version."; Dataverse lets a client write the table, a known limit). The Basic User role reads it at Global depth; an organization created before it gets this grant once at startup. The files each control's manifest names (bundle, styles, resource strings, images) are kept in the internal table `meta.customcontrol_file` (migration 0014: control id, path, content, SHA-256), which no read path of the Web API reaches; they go with their row.

## App settings and user settings

Environment values of app settings use the organization-owned table **`organizationsetting`** (entity set
`organizationsettings`, object type code 9967, primary name `displayname`): `displayname`, `uniquename` (String 100), `value`
(String 2000) and the lookup `settingdefinitionid` to `settingdefinition` (relationship `settingdefinition_organizationsetting`,
Delete Restrict: a definition with an environment value cannot be deleted). A definition has at most one environment value: a
second Create for the same definition is a 400 `0x80040237` "The setting {uniquename} already has an environment value." The
Basic User role reads the table at Global depth; Create, Write and Delete belong to System Administrator and System Customizer
only. The object type code is xrmverse's own. Today the looks of model-driven apps read it (`xrmverse_Renderer` and
`xrmverse_Presentation`, see [Model-driven apps](model-driven-apps.md#looks-classic-and-modern)); the web admin writes it from
**Environment > Model-driven apps look**. Its rows are not exported in solutions. Two columns are added to
**`usersettings`**: `xrmverse_renderer` (String 100; empty means "follow the app") and `xrmverse_colorscheme` (choice
`xrmverse_colorscheme`: 0 System, 1 Light, 2 Dark; default 0), which every user can write on their own row. An organization
seeded by an earlier version gets the table, its privileges and the columns at startup (existing `usersettings` rows read
empty and 0).

## Webhooks

Webhooks (see [Webhooks](webhooks.md)) add the organization-owned table **`serviceendpoint`** (entity set `serviceendpoints`, object type
code 4618): `serviceendpointid`, `name` (String 256, required), `description`, `url` (String 2000), `contract` (choice, only 8 Webhook is
accepted), `authtype` (choice, only 4, 5 and 6), the write-only `authvalue`, the read-only `isauthvalueset`, `messageformat`,
`messagecharset`, `connectionmode`, `userclaim`, `namespaceaddress`, `path`, `solutionnamespace` and `saskeyname`. The `authvalue` is not a
column of the table: it is stored in the metadata table `meta.serviceendpoint_secret` (created by the migration
`0012_service_endpoint_secrets.sql`), so that no read path can return it. `sdkmessageprocessingstep.eventhandler` is a polymorphic
lookup to `plugintype` or `serviceendpoint`, with the navigation properties `eventhandler_plugintype` and `eventhandler_serviceendpoint`.
System Customizer holds every privilege on `serviceendpoint` and AppendTo; Basic User holds none. An organization seeded by an earlier
version gets the table, its privileges and the new lookup target at startup.

## Application users

`systemuser.applicationid` (Uniqueidentifier, schema `ApplicationId`) is the app id of an application user, as in
Dataverse: null for interactive users, written by whoever creates the application user (Web API, SDK); xrmverse never
fills it from a token, and the [local identity provider](local-idp.md) binds a confidential client to its user without it.
An organization seeded by an earlier version gets the column at startup.

## Last form per user

`userentityuisettings` (user-owned, entity set `userentityuisettingsset`, object type code 2500, key
`userentityuisettingsid`) keeps, per user and table (`objecttypecode`, the table's logical name), the last main form the
user chose (`lastviewedformxml`: `<MRUForm><Form Type="Main" Id="{form id}" /></MRUForm>`), so the model-driven form
opens on it again in any browser. Its other Dataverse columns (tab order, recently viewed records, lookup history) are not
modelled. Basic User creates, reads and writes its own rows (Basic depth). An organization seeded by an earlier version
gets the table and the grants at startup.

## Hierarchy security

Hierarchy security (see [Security](security.md#hierarchy-security)) adds these columns: on `organization`,
`ishierarchicalsecuritymodelenabled` (Boolean, default false), `maxdepthforhierarchicalsecuritymodel` (Integer, default 3,
1 to 100) and `usepositionhierarchy` (Boolean, default false, true refused); on `systemuser`, the lookup
`parentsystemuserid` (manager, schema `ParentSystemUserId`) with the hierarchical relationship `user_parent_user` (Delete
RemoveLink, the other cascades NoCascade, from memory). `account_parent_account` is hierarchical too. The tables left out
of hierarchy security are stored in `meta.hierarchy_security_exclusion` (a metadata table, not a Dataverse table). An
organization seeded by an earlier version gets the columns, their defaults and the `IsHierarchical` flags at startup.

## Access teams

Access teams (see [Security](security.md#access-teams)) add the organization-owned table **`teamtemplate`** (entity set
`teamtemplates`, object type code 92, from memory): `teamtemplateid`, `teamtemplatename` (String 100, required),
`objecttypecode` (Integer, required: the object type code of the table), `defaultaccessrightsmask` (Integer, required),
`description` (Memo 2000) and the read-only `issystem`. Its privileges are `prvCreateTeamTemplate`, `prvReadTeamTemplate`,
`prvWriteTeamTemplate`, `prvDeleteTeamTemplate`, `prvAppendTeamTemplate` and `prvAppendToTeamTemplate` (Global depth), held by
the System Administrator role only. `team` gets three columns: `teamtemplateid` (Lookup to `teamtemplate`, relationship
`teamtemplate_Teams`, Delete Cascade, from memory), `regardingobjectid` (a Lookup to the record of a table switched on for
access teams, with one relationship `team_regardingobjectid_<table>` per table, derived names) and `systemmanaged` (Boolean,
default false). An organization seeded by an earlier version gets the table and the columns at startup.

## Change tracking

Change tracking (see [Change tracking](change-tracking.md)) adds one column to `organization`,
`expirechangetrackingindays` (Integer, required, 0 to 365, default 7, the number of days the log of deleted rows is kept; a
value outside the range is a 400), and three metadata tables in the `meta` schema (not Dataverse tables, created by the
migration `0007_change_tracking.sql`): `meta.deleted_record` (the log of deletions of tracked tables: a `rowversion` version,
the table, the id and the deletion time), `meta.change_tracking_table` (one row per tracked table, with the version at which
tracking started) and `meta.change_tracking_state` (one row: the version up to which the log was purged). An organization
seeded by an earlier version gets the column with its default at startup.

## Duplicate detection

Duplicate detection (see [Duplicate detection](duplicate-detection.md)) adds three tables, with object type codes from memory
and not verified: **`duplicaterule`** (entity set `duplicaterules`, 4414, user-owned: `name`, `baseentityname`,
`matchingentityname`, `description`, `excludeinactiverecords`, `iscasesensitive`, `uniquename`, the server-written
`baseentitytypecode`, `matchingentitytypecode`, `baseentitymatchcodetable` and `matchingentitymatchcodetable`, and
`statecode` 0 Inactive / 1 Active with `statuscode` 0 Unpublished / 1 Publishing / 2 Published; a new rule is 0/0),
**`duplicaterulecondition`** (`duplicateruleconditions`, 4416, organization-owned in xrmverse so that it has privileges, where
Learn says none: `baseattributename`, `matchingattributename`, `operatorcode` 0 to 6, `operatorparam`, `ignoreblankvalues`,
`regardingobjectid` to the rule, `uniquerulename`) and **`duplicaterecord`** (`duplicaterecords`, 4415, primary key
`duplicateid`, user-owned so that reads follow depth, where Learn says none: `asyncoperationid`, `baserecordid`,
`duplicaterecordid`, `duplicateruleid`; written only by the bulk job). `organization` gets four Boolean columns, all default
false: `isduplicatedetectionenabled`, `isduplicatedetectionenabledforonlinecreateupdate`,
`isduplicatedetectionenabledforimport` and `isduplicatedetectionenabledforofflinesync`. `asyncoperation.operationtype` gains 7
(Duplicate Detection Rule Publish) and 8 (Bulk Duplicate Detection). Tables can carry the flag
`EntityMetadata.IsDuplicateDetectionEnabled`. The migration `0009_duplicate_detection.sql` creates the metadata table
`meta.matchcode_table` (one row per published rule); the per-rule tables `meta.matchcode_{ruleid}` (`objectid`, `matchcode`
`binary(32)`) are created when a rule is published and dropped when it is unpublished or deleted. An organization seeded by an
earlier version gets the tables, the columns, the defaults and the System Customizer privileges at startup.

## Currencies, user settings and time zones

Currencies (see [Currencies](currencies.md)) add two tables and some columns. **`transactioncurrency`** (organization-owned,
entity set `transactioncurrencies`, object type code 9105 from memory): `currencyname` (String 100), `isocurrencycode` (String
5, unique), `currencysymbol` (String 10), `currencyprecision` (Integer, 0 to 4), `exchangerate` (Decimal, 12 decimals,
0.000000000001 to 100000000000; the server accepts 0.0000000001 and up), `currencytype` (choice `transactioncurrency_currencytype`:
0 System, 1 Custom), `statecode` and `statuscode` (Active, Inactive). **`usersettings`** (user-owned, entity set
`usersettingscollection`, object type code 150 from memory, key `systemuserid`) holds `transactioncurrencyid`, the
user's default currency (relationship `transactioncurrency_usersettings`), and the columns added later: `paginglimit`,
`uilanguageid`, the time zone `timezonecode` (Integer, default 92) and the formats `dateformatstring`, `timeformatstring`
(String 255), `dateseparator`, `timeseparator` (String 5), `amdesignator`, `pmdesignator` (String 25), defaulting to
`M/d/yyyy`, `h:mm tt`, `/`, `:`, `AM`, `PM` (see [Model-driven apps](model-driven-apps.md#user-time-zone-and-date-formats)).
**`timezonedefinition`** (no owner, entity set `timezonedefinitions`, object type code 4810, primary name
`userinterfacename`, String 100) holds one row per time zone code: `timezonecode`, `standardname` (String 100, the
Windows id), `bias` (minutes west of UTC) and `retiredorder`. Its rows are inserted at startup when missing, with stable
ids; every user reads them (Basic User, Global) and no request writes them (403). On `organization`: `basecurrencyid` (lookup to
`transactioncurrency`, relationship `basecurrency_organization`, read-only after the seed), `pricingdecimalprecision` (Integer,
0 to 4, default 2), `currencydisplayoption` (choice, 0 symbol, 1 code, default 0), `currencyformatcode` (choice, 0 to 3, default
0) and the read-only `basecurrencysymbol` (String 5) and `basecurrencyprecision`. `account` and `contact` have the pair
`transactioncurrencyid` (relationships `transactioncurrency_account` and `transactioncurrency_contact`, Delete Restrict) and
`exchangerate`, and so does every table that gets a money column (relationship `TransactionCurrency_<table>`); each money
column has a `<name>_base`. In every organization the Basic User role reads currencies at Global depth and can read, write
and append to its own `usersettings`; creating, changing and deleting currencies stays with System Administrator. An
organization seeded by an earlier version gets the tables, columns, base currency and grants at startup.

## Activities and notes

Activities and notes (see [Activities and notes](activities.md)) add seven user-owned tables, except `activityparty`, which
has no owner: **`activitypointer`** (entity set `activitypointers`, object type code 4200), a read-only copy of the common
columns of every activity that the server writes; **`activityparty`** (`activityparties`, 135), one row per participant of an
activity (`activityid`, `partyid` to `account`, `contact` or `systemuser`, `participationtypemask`, `addressused`,
`ispartydeleted`), read-only; the activity tables **`email`** (`emails`, 4202), **`task`** (`tasks`, 4212), **`phonecall`**
(`phonecalls`, 4210) and **`appointment`** (`appointments`, 4201); and **`annotation`** (`annotations`, 5), the notes, whose
`documentbody` lives in the file store. The type codes are from memory. On `systemuser`: `internalemailaddress` (String 100,
the address a user gets as a party). On `organization`: `maxuploadfilesize` (Integer, required, 0 to 131072000, default
5242880, the largest note body in bytes; the range is from memory). `account` and `contact` are targets of
`regardingobjectid` and `annotation.objectid` with the relationships `Account_ActivityPointers`, `Account_Emails`, …,
`Account_Annotation` and their `Contact_` counterparts, and the four activity tables have notes. Every role's activity
privileges are the shared `prv…Activity` set and its note privileges `prv…Note`; Basic User holds both at Basic depth. An
organization seeded by an earlier version gets the tables, columns, defaults, relationships and grants at startup; a custom
table named like one of the seven stops the server with the same message as for `account` (below).

## Classic workflows

Classic workflows (see [Classic workflows](workflows.md)) add two tables, with object type codes from memory and not verified.
**`workflow`** (entity set `workflows`, object type code 4703, user-owned, primary name `name`): `uniquename`, `description`,
`category` (0 Workflow, 1 Dialog, 2 Business Rule, 3 Action, 4 Business Process Flow, 5 Modern Flow, 6 Desktop Flow, 7 AI Flow),
`mode` (0 Background, 1 Real-time), `type` (1 Definition, 2 Activation, 3 Template), `scope` (1 User, 2 Business Unit, 3 Parent:
Child Business Units, 4 Organization), `primaryentity` (String 100, the table's logical name, `none` for a global action),
`xaml`, `ondemand`, `subprocess`, `triggeroncreate`, `triggerondelete`, `triggeronupdateattributelist` (comma-separated column
names), `asyncautodelete`, `syncworkflowlogonfailure`, `istransacted`, `createstage`, `updatestage`, `deletestage` (20
Pre-operation, 40 Post-operation), `runas` (0 Owner, 1 Calling User), `processtriggerscope` (1 Form, 2 Entity, 3 All Forms; 3
is unconfirmed), `rank`, `parentworkflowid` and `activeworkflowid` (lookups to `workflow`, relationships
`workflow_parent_workflow`, Delete Cascade, and `workflow_active_workflow`, Delete RemoveLink), `sdkmessageid` (an action's
message), `inputparameters`, `introducedversion`, `iscustomizable`, `ismanaged`, the server-written `xrmverse_unsupportedsteps`
(the steps xrmverse does not run, one per line) and `xrmverse_componentxml` (the element of the imported file, kept for export),
and `statecode` 0 Draft / 1 Activated with `statuscode` 1 Draft / 2 Activated (other statuses are a 400). **`processtrigger`**
(`processtriggers`, 4712, organization-owned): `processid` (relationship `process_processtrigger`, Delete Cascade),
`primaryentitytypecode` (String 100), `event`, `controlname`, `controltype` (1 Attribute, 2 Form Tab), `formid`, `scope` (1 Form,
2 Entity), and the stored-only `pipelinestage` and `methodid`. `processtrigger` is written only by solution import and by
activation (a public Create, Update or Delete is a 400). `workflow` is also written by authoring: a public Create or Update of
a Draft definition is accepted when its XAML parses, validates and has no unsupported step, and `xrmverse_unsupportedsteps` is
still server-written only. An Update re-validates the XAML only when it sends a definition column (the XAML, mode, trigger or stage columns), so an imported Draft with unsupported steps, or of category 1, 4 or 5, can still be renamed. (See [Workflow authoring as code](workflow-authoring.md#what-is-refused-and-why) and
[Classic workflows](workflows.md#getting-processes-in-activating-and-running-them)).
`asyncoperation` uses `operationtype` 10 (Workflow) for background workflow jobs and has the lookup `workflowactivationid`
(relationship `Workflow_AsyncOperations`, Delete RemoveLink). The migration `0013_workflows.sql` creates the metadata table
`meta.workflow_wait` (one row per job waiting on a record: `asyncoperationid`, `entity`, `objectid`, `wake_at`, `seen_version`).
`email` gains five columns that a workflow's send email step sets: `correlatedsubjectchanged`, `followemailuserpreference`,
`isduplicatesenderunresolved` (Boolean, default false), `deliveryprioritycode` (0 Low, 1 Normal, 2 High, default 1) and
`notifications` (0 None, 1 and 2, default 0), as local choices; emails that existed before read NULL in them. System Customizer
holds every privilege on `workflow` and `processtrigger` at Global. An organization seeded by an earlier version gets the tables,
the columns and the grants at startup.

## See also

- [Security](security.md), [Solutions](solutions.md)
- [Supported queries](queries.md)
- [Known issues and limitations](known-issues.md)
