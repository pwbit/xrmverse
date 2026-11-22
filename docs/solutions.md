# Solutions

[← xrmverse documentation](../README.md#documentation)

## Introduction

`xrmd` imports **managed and unmanaged** solutions, and exports unmanaged ones, in the native `solution.zip` format (`solution.xml`, `customizations.xml`,
`[Content_Types].xml`, assemblies and web resources), with the same messages as Dataverse: via SDK/SOAP (`ServiceClient`,
`pac solution import`/`export`, which use `ServiceClient`) and as Web API actions. Acceptance criterion B
(import of the sample solution with `ServiceClient`, imported plug-ins that fire, N:N, asynchronous import, export
round-trip) is the `SolutionAcceptanceTests` test.
Managed solutions (layers, upgrades, uninstall) are described in [Managed solutions and layers](#managed-solutions-and-layers).
The page then follows the life of a solution: the messages, importing, exporting, what is preserved, deleting, and the
limits.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| File format | native `solution.zip`, imported and exported with the same messages | Same; `pac` and `ServiceClient` work |
| Unmanaged solutions | import, export, add and remove components | Same |
| Managed solutions | install, update, stage, upgrade, promote, uninstall, layers | Narrower: no managed export, no patches |
| Layer merging | the top layer wins for every component | Differs: Dataverse merges forms, site maps and app modules |
| Data loss on uninstall or upgrade | refused unless confirmed through the Admin API, CLI or web admin | Differs: Dataverse drops the data |
| Deleting an unmanaged solution | container only; the cascade is an `xrmd` command | Same for the SDK and Web API |
| What xrmverse does not model | kept as opaque XML and written back on export | Extra: never silently lost |
| File size | 95 MB compressed, 100 MB uncompressed | Narrower on uncompressed size |
| Microsoft first-party solutions | not supported; the import goes on with a warning | Narrower |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Import a solution | `xrmd solution import`, `ImportSolution`, `pac solution import` | [Import](#import) |
| Export an unmanaged solution | `ExportSolution`, `pac solution export` | [Export](#export) |
| Upgrade a managed solution | `--mode upgrade`, `StageAndUpgrade`, or stage then promote | [Install, update, stage, upgrade, promote](#install-update-stage-upgrade-promote) |
| Uninstall a managed solution | `xrmd solution uninstall`, Admin API, web admin | [Uninstall](#uninstall) |
| See the layers of a component | `xrmd solution layers` | [Where to manage them](#where-to-manage-them) |
| Change tables and columns | metadata messages | [Metadata messages](#metadata-messages) |
| Delete a solution | `DELETE solutions(id)` | [Deleting a solution](#deleting-a-solution) |

### Microsoft first-party solutions

Microsoft first-party solutions are not supported. Solutions that Microsoft publishes (for example the `msdyn_*`
Dynamics 365 and Power Platform ones) depend on components xrmverse does not have. An import whose publisher has the
customization prefix `msdyn`, `msdynce`, `msdynmkt`, `mspp` or `adx`, or a unique name that starts with `microsoft` or is
`dynamics365` (case-insensitive), is not blocked: it logs a warning, and the same text is in the import job:
`The solution '<solution>' comes from a Microsoft publisher ('<publisher>', prefix '<prefix>'). Microsoft first-party solutions are not supported by xrmverse.`

## Messages

The solution messages, their Web API form and the privilege each needs:

| Message | Web API | Privilege | Notes |
|---|---|---|---|
| `ImportSolution` | `POST ImportSolution` (204) | `prvImportCustomization` | synchronous, one transaction, `importjob` row; a managed file installs, updates or (with `HoldingSolution`) stages |
| `ImportSolutionAsync` | `POST ImportSolutionAsync` | `prvImportCustomization` | queues an `asyncoperation` (operationtype 203); response `AsyncOperationId`, `ImportJobKey` (= `importjobid`) |
| `ExecuteAsync` | — | `prvImportCustomization` | only with an `ImportSolutionRequest`, `StageAndUpgradeRequest` or `DeleteAndPromoteRequest` inside; response `AsyncJobId` |
| `StageAndUpgrade` | `POST StageAndUpgrade` | `prvImportCustomization` | stage and promote in one transaction (a plain install when the solution is not installed); the parameters of `ImportSolution` except `HoldingSolution`, `SchemaUpdatesOnly`, `StageAndUpgrade` and the template parameters (see [Request options](#request-options)) |
| `StageAndUpgradeAsync` | `POST StageAndUpgradeAsync` | `prvImportCustomization` | queues an `asyncoperation` (operationtype 211); response `AsyncOperationId`, `ImportJobKey` |
| `DeleteAndPromote` | `POST DeleteAndPromote` | `prvImportCustomization` | parameter `UniqueName`; response `SolutionId` (the id of the former holding row) |
| `DeleteAndPromoteAsync` | `POST DeleteAndPromoteAsync` | `prvImportCustomization` | queues an `asyncoperation` (operationtype 207); response `AsyncOperationId` |
| `UninstallSolutionAsync` | `POST UninstallSolutionAsync` | Delete on `solution` | parameter `SolutionUniqueName`; queues an `asyncoperation` (operationtype 208); response `AsyncOperationId` |
| `ExportSolution` | `POST ExportSolution` | `prvExportCustomization` | unmanaged solutions only (see Known limits); response `ExportSolutionFile` |
| `RetrieveFormattedImportJobResults` | `GET RetrieveFormattedImportJobResults(ImportJobId=@p1)?@p1=…` | Read on `importjob` | `FormattedResults`: Excel workbook (SpreadsheetML 2003) |
| `AddSolutionComponent`, `RemoveSolutionComponent` | `POST` with the same name | `prvWriteSolution` | response `id` (`solutioncomponentid`) |
| `PublishXml`, `PublishAllXml` | `POST` with the same name (204) | `prvPublishCustomization` | no-op: metadata take effect from the commit; they reload the cache |
| `Delete` of `solution` | `DELETE solutions(id)` | Delete on `solution` | an unmanaged row: container only; a managed row: uninstall (see [Deleting a solution](#deleting-a-solution)) |

### Metadata messages

The metadata messages are `CreateEntity`, `CreateAttribute`, `CreateOneToMany`, `CreateManyToMany`, `CreateOptionSet`, `UpdateEntity`,
`UpdateAttribute`, `UpdateOptionSet`, `InsertOptionValue`, `UpdateOptionValue`, `DeleteEntity`, `DeleteAttribute`, `DeleteRelationship`,
`DeleteOptionSet` (privileges `prvCreateEntity`, `prvCreateAttribute`, `prvCreateRelationship`, `prvCreateOptionSet`,
`prvWriteEntity`, `prvWriteAttribute`, `prvWriteOptionSet`, `prvDeleteEntity`, `prvDeleteAttribute`,
`prvDeleteRelationship`, `prvDeleteOptionSet`) and the reads `RetrieveRelationship`, `RetrieveOptionSet`,
`RetrieveAllOptionSets`, `RetrieveAllEntities` (no privilege, as for `RetrieveEntity`). Every write is in its own
transaction (DDL included) and the cache is published only after the commit. The `SolutionUniqueName` parameter (SDK) and
the `MSCRM.SolutionUniqueName` header (Web API) also add the component to that solution; every created component
ends up in `Default` anyway. N:N relationships create an intersect table that is readable in queries (link-entity too) and
written only by `Associate`/`Disassociate` (SDK) or `$ref` (Web API). The `prv…` names of the metadata privileges and
`prvPublishCustomization` are not verified on Learn (they follow the `prv<Verb><Component>` pattern). The metadata
write messages, `ImportSolution`, `ImportSolutionAsync`, `ExecuteAsync` and `ExportSolution` from a plug-in's service are
rejected (400), and so are the managed solution messages of the table above; `RetrieveFormattedImportJobResults` and
metadata reads are allowed.

### Rules for metadata writes

Metadata writes (SDK and Web API) reject rather than silently diverge:

- `UpdateEntity` and `UpdateAttribute` (also `PUT EntityDefinitions(…)` and `PUT …/Attributes(…)`) apply only a closed list
  of properties: the labels (`DisplayName`, `DisplayCollectionName`, `Description`) and, for attributes, also
  `RequiredLevel`, `MaxLength` (string, memo, growth only) and the options of picklist, multi-select, `statecode` and
  `statuscode` (new options, labels of existing ones), plus `IsAuditEnabled` of tables and columns when it can change
  (see [Auditing](auditing.md)), and `ChangeTrackingEnabled` of tables (one-way, see below). The request is compared with the current definition: any
  other property with a different value is a 400 naming it (for example
  `OwnershipType`, `MaxValue`, `FormatName`). A body obtained with retrieve (or `GET`) and sent back with only the labels
  changed passes; options equal to the current ones are ignored (so a picklist with a global option set can be renamed too).
  A property with the value a new SDK object has (`AttributeTypeName`, an option's `IsHidden`, …)
  counts as not given. The child collections of an entity (attributes, relationships, privileges, keys) are not part of
  `UpdateEntity` and are ignored, as in Dataverse. On a system component, whoever is not a System Administrator changes only
  the labels (403 for the rest).
- `InsertOptionValue` and `UpdateOptionValue` (SDK, and Web API actions of the same name) work on a global choice
  (`OptionSetName`) or on the choice of a column (`EntityLogicalName`, `AttributeLogicalName`): a choice or multi-select
  choice column, and for `UpdateOptionValue` a status reason (`statuscode`) column too; a `statecode` or Yes/No column is
  a 400. They take `Color` (`#RRGGBB`, the option's background colour; any other value is a 400 naming it), `Label`,
  `Description` and `SolutionUniqueName`; `InsertOptionValue` also takes `Value` (else the next free value), and
  `UpdateOptionValue` requires `Value` and takes `MergeLabels` (with `true`, the labels given replace those of their
  languages and the others stay; otherwise the label given is the whole label). In `UpdateOptionValue` a parameter left
  out keeps the option's property, a `Color` that is empty (SDK) or `null` (Web API) clears the colour, and a `Label`
  without any localized label is a 400 naming `Label`. Both refuse `ParentValues` (non-empty), `ExternalValue` and
  `IsHidden` `true` with a 400 naming them; `IsHidden` `false` (the default) is accepted. A caller that is not a System
  Administrator may update the labels and the colour of an option of a system choice, not add an option to it.
  `UpdateStateValue` is not supported.
- `AutoCreateAccessTeams` of a table is modelled (see [Access teams](security.md#access-teams)): `CreateEntity`,
  `UpdateEntity` and a solution import (`<AutoCreateAccessTeams>` in `customizations.xml`) set it, and the export writes the
  stored value. It is allowed only on user- and team-owned tables, at most 100 tables, and cannot be turned off while a
  template for the table exists (all 400). An import whose file has no such element leaves the table as it is. An
  organization that kept the element as opaque XML from an earlier version is converted once at startup
  (`AccessTeamSettingsUpgrade`); a kept `1` is applied only where the rules allow it. **Team templates (`teamtemplate` rows)
  are not carried by a solution**: the shape of a template inside `customizations.xml` is not known, so an export holds the
  flag but not the templates, and an import creates none. Create them again with the Web API, `xrmd access-team template
  create` or the web admin page.
- `ChangeTrackingEnabled` of a table is modelled (see [Change tracking](change-tracking.md)): `CreateEntity`, `UpdateEntity`
  and a solution import (`<ChangeTrackingEnabled>` in `customizations.xml`) set it, and the export writes it together with
  the computed `CanChangeTrackingBeEnabled`. It is one-way: an import of `1` switches a table on (400 for a table that
  cannot be tracked); an import of `0` for a table that is on never switches it off, the table is kept and the import logs a
  warning. An import whose file has no such element leaves the table as it is. An organization that kept the element as
  opaque XML from an earlier version is converted once at startup (`ChangeTrackingSettingsUpgrade`).
- `IsActivity`, `ActivityTypeMask`, `HasNotes` and `HasActivities` of a table are modelled (see
  [Activities and notes](activities.md)): `CreateEntity` with `IsActivity` true creates a custom activity under its rules,
  and `HasNotes`/`HasActivities` are one-way, set by `CreateEntity`, `UpdateEntity` and a solution import. Solutions
  carry them as follows:
  - **Import.** A table with `<IsActivity>1</IsActivity>` is created as a custom activity, its `subject` taken as the
    primary column even when the file lists it as a system column. Its common columns, party lists and EntityName columns,
    and the relationships of the activity model (regarding, notes, parties), are derived by xrmverse and never imported
    from the file, so no "System attribute … is not created" warning is written for them. When the file's version of one
    of them differs from xrmverse's (a label, a description, a required level, an audit or secured flag, a relationship
    marked custom or named differently), the import log gets one warning per difference and the import goes on; a file
    with labels in several languages can give many such lines.
    `HasRelatedNotes` and `HasRelatedActivities` set `HasNotes` and `HasActivities`: a `1` switches the table on (the A5
    rules apply, 400), a `0` on a table that is on is kept on with a warning, and a `0` for `HasRelatedNotes` on a new
    custom activity is turned into true with a warning. A team- or organization-owned activity is a 400, and so is a
    re-import that turns an existing table into an activity or back, or changes its `ActivityTypeMask`. A table whose
    entity set name is one of the refused activity message names (`Rollup`, `Book`, `RouteTo`, …, see
    [What is refused](activities.md#what-is-refused)) is a 400, as it is at creation.
  - **Export.** `IsActivity`, `ActivityTypeMask` (empty for a table that is not an activity) and `HasRelatedNotes`/
    `HasRelatedActivities` are written from the metadata, `True` when on; `False` is written only where the imported
    file had the element, or for a table created in xrmverse. The export of a custom activity carries only its own
    columns. The type names of party list and EntityName columns are `partylist` and `entityname`.
  - **Uninstall.** Deleting a custom activity removes its pointer and party rows. An uninstall that would delete a table
    whose records have notes, or activities regarding them, is refused with 400 (dry run included); notes are not deleted
    with it. Restoring a lower layer never changes `HasNotes` or `HasActivities` (they are one-way, as
    `ChangeTrackingEnabled`).
  - Activity, party and note rows are data, never solution components. An organization that kept `HasRelatedNotes`/
    `HasRelatedActivities`/`ActivityTypeMask` as opaque XML from an earlier version is converted once at startup
    (`ActivitySettingsUpgrade`); a kept `1` is applied only where the rules allow it, and the others are logged.
  - The real shape of a custom activity in a Dataverse export (its system columns, the `Type` of its party list
    columns, its relationships) has not been checked.
- `IsDuplicateDetectionEnabled` of a table is modelled (see [Duplicate detection](duplicate-detection.md)): `CreateEntity`,
  `UpdateEntity` and a solution import (`<IsDuplicateCheckSupported>` in `customizations.xml`) set it, and the export writes the
  stored value (the element used to be written as a literal `0`). Setting it on a table that cannot have it (an intersect table
  or a system table other than `account` and `contact`) is a 400 through the metadata messages; a `1` in an import for such a
  table is not applied and the import logs a warning. An import whose file has no such element leaves the table as it is. An
  organization that kept the element as opaque XML from an earlier version is converted once at startup
  (`DuplicateDetectionSettingsUpgrade`). **Duplicate detection rules are not carried by a solution**: Dataverse has them as
  solution components, but their shape in `customizations.xml` is not known to xrmverse. A `<DuplicateRules>` node is kept
  byte for byte and written back on export, the import logs a warning (and so does the import job), and no rule is created;
  `AddSolutionComponent` with the rule component type keeps the usual "Component type … is not supported" 400. Create the rules
  again with the Web API, `xrmd duplicate-detection rule create` or the web admin page.
- **Table and column maps** (`<EntityMaps>`, #143) are
  imported into `entitymap` and `attributemap` (see [Tables in every organization](system-tables.md)), after the tables and
  columns of the file. A map is keyed by its source and target tables: an existing one is reused, an attribute map replaces the
  one of the same target column, and maps the file does not name stay. A map that does not fit fails the whole import with a
  400 naming it: a table or column that does not exist, two columns of different types (a lookup's target must accept every
  table of the source lookup), a text target shorter than its source, a party list, a target column mapped twice, or a target
  that is the lookup to the source table (the relationship's own map fills it). The text format of two columns and the option
  values of two choices are not compared. The `<EntityMaps>` node is kept as the file gave it and the export writes it back;
  the maps are not solution components (no `solutioncomponent` row, no layers), so an uninstall leaves them, and xrmverse has
  no API to change or delete them. The lookup map that Dataverse creates with each one-to-many relationship (not exported) is
  not a row in xrmverse: `InitializeFrom` derives it from the relationship.
- `CreateEntity` (SDK and `POST EntityDefinitions`) rejects with 400 an
  `OwnershipType` other than user and organization (the same rule as the import) and every table setting that
  xrmverse does not model (`EntityColor`, …) with a
  value different from the one the export writes for a table created in xrmverse (`false`/`0`, …). Likewise
  `CreateAttribute`, the attributes of `CreateEntity` (the SDK primary attribute, the Web API `Attributes`) and the
  lookup of `CreateOneToMany` reject with 400 the column flags that xrmverse does not model (`IsSortableEnabled`,
  `IsSearchable`, `ImeMode`, …) with a value different from the one the export writes. `IsAuditEnabled` is modelled:
  creation accepts either value, except a fixed column's (see [Auditing](auditing.md)).
- Reads (`RetrieveEntity`, `RetrieveAllEntities`, `RetrieveAttribute`, `GET EntityDefinitions` and its
  `Attributes`) show the unmodeled table and column settings with the value the export writes: the one
  preserved from the file for an imported component (see [Preserved XML](#preserved-xml)), the default one for a component created in xrmverse
  (an element with no property in the SDK is not shown; `HasNotes` and `HasActivities` are modelled and show the stored
  value, false when unset). This way a read and the export never show different values. The settings cannot be modified with
  `UpdateEntity`/`UpdateAttribute` (400), but a definition read and sent back with the same values passes. A preserved
  value that cannot be shown on its property is a 500 naming it.
- The attributes of an intersect table (N:N) belong to the relationship: `CreateAttribute`, `UpdateAttribute` and
  `DeleteAttribute` on it are 400. A lookup (`CreateAttribute`, or an attribute of `CreateEntity`) must point to
  an existing entity (or to the entity being created) that is not an intersect, like `CreateOneToMany`: 400 otherwise.
- `DeleteEntity` deletes with the table its views (`savedquery`) and forms (`systemform`), as Dataverse does, and
  removes them from every solution. The `Delete` of a step also deletes its images (and removes them from the solutions).
- 412 `DuplicateRecord` only for a component that already exists (entity, entity set, attribute, relationship, global
  option set); any other internal error remains one.

### System solutions

`Default` (`FD140AAF-4DF4-11DD-BD17-0019B9312238`), `Active` (`FD140AAE-4DF4-11DD-BD17-0019B9312238`)
and the publisher `DefaultPublisher<org>` (`D21AAB71-79E7-11DD-8874-00188B01E34F`, prefix `new`) are seeded with the
well-known Dataverse ids. `Default` registers an entity with `rootcomponentbehavior` 0 (subcomponents included) without rows for its
system attributes and relationships: a query on `solutioncomponent` of `Default` returns fewer rows than in
Dataverse.

## Managed solutions and layers

A managed solution is a file whose `solution.xml` has `<Managed>1</Managed>`. Importing one does not copy its components
into the organization as if they had always been there: each component remembers which solution delivered which state, and
the solution can later be updated, replaced by a newer version or removed again.

### The rules in short

- **Layers.** Every component a managed solution carries gets a *Managed layer* of that solution
  (`meta.component_layer`, one row per component and solution, holding the component as the file delivered it). A seeded
  component (a system table, say) that a managed solution changes first gets a *System layer*: its base state, captured
  before the first change. The state you read through the API is always the **top** layer's.
- **Order.** Each managed solution has a rank (`meta.solution_rank`); the layer order comes from it. A newly installed
  solution is above every earlier one, a holding solution is directly above its base, and the System layer is at the
  bottom. The top layer wins for every component type. Dataverse merges the layers of forms, site maps and app modules;
  xrmverse cannot, so a managed layer written under a higher layer of such a component is hidden, and the import log
  says so with a warning naming the component.
- **The Active layer.** An unmanaged customization of a component that has a managed or System layer (a relabelled column,
  an edited view, a row changed by `Update`) puts an *Active* marker on top of it. Active sits above every managed
  layer and is what keeps your change when a managed solution is updated or upgraded. It disappears with the component,
  or when an import sets `OverwriteUnmanagedCustomizations` (below). A write that happens to set the managed values back keeps
  the marker.
- **A component created unmanaged** that a managed file later contains keeps its Active marker on top; after the managed
  solution is uninstalled the component is deleted with it, marker included.

### Install, update, stage, upgrade, promote

- **Install**: a managed file whose unique name is not installed. A name that is installed unmanaged is a 400, and so is an
  unmanaged file whose name is installed managed. A version lower than the installed one is a 400.
- **Update**: the same name, no flag, same or higher version. The solution's layers are replaced with the file's.
  Components the old version had and the new one lacks **stay** with their old layer (this is what Dataverse does for an
  update, and why an upgrade exists). A layer that is not on top is stored but does not change the component, and the log
  names it ("kept: layer above").
- **Stage** (`HoldingSolution` true): the file must have a strictly higher version. It creates the solution
  `{name}_Upgrade`, a managed row in the same publisher, whose layers sit directly above the base's. The base is
  unchanged until the promote. While a holding solution exists, another import of the same name (an update) is a 400.
- **Promote** (`DeleteAndPromote`): removes the base's layers, deletes the components that no layer holds any more (behind
  the data guard below), renames the holding row to `{name}` and keeps **its** id, which is the response's `SolutionId`.
  It is refused with `0x8004F01D` when a component the base created (its lowest layer is the base's, no System layer) is
  not in the upgrade but another managed solution has a layer on it; the message names the component and that solution.
- **StageAndUpgrade**: stage and promote in one transaction. For a name that is not installed it is a plain install.
- **Same-version StageAndUpgrade is refused** (the stage needs a higher version).
- **`OverwriteUnmanagedCustomizations`** now has an effect on managed imports: before the file is applied, the Active
  markers of its components are removed, so the file's state shows, except where another managed solution sits above.
- **Concurrent operations.** An import decides what it does (install, update, stage) before its transaction; under the
  metadata lock it reads the solution rows again (the solution and its `{name}_Upgrade`, locked to the commit) and
  classifies the file again. If another import, upgrade or uninstall changed them meanwhile, the import is a **409**
  ("changed while it was being imported"; retry) and nothing changes. A promote and an uninstall plan again under the lock
  in the same way: a plan that would delete more than the first one, or (promote) another holding solution, is a 409.
- **Asynchronous forms.** `StageAndUpgradeAsync` checks only what is cheap at request time (the file, the privilege, the
  options, the name). A refusal that needs the whole plan, such as data loss or a dependency, **fails the asynchronous job**
  (`asyncoperation` state 3, status 31 / failed, with the message) and changes nothing.

### Uninstall

Deleting a managed `solution` row (SDK, Web API), `UninstallSolutionAsync` and
`POST /admin/solutions/{uniquename}/uninstall` remove the solution in one transaction: its holding solution first (if
any), then the solution's layers. A component with no managed and no System layer left is deleted (also if an Active marker
is still on it); a component whose top layer was removed is **restored** from the layer now on top. Then the
`solutioncomponent` rows and the `solution` row go; the publisher stays.
- An uninstall is refused with `0x8004F01D` when another managed solution has a layer on a component whose **lowest**
  layer is this solution's and that has no System layer (a component this solution created and another one builds on).
  The message names the component and the other solution. A component that exists in the base system (it has a System
  layer) never blocks an uninstall.
- A **holding solution can be uninstalled directly**; that cancels the pending upgrade and the base stays as it was.

### Data is never dropped silently

This is a deliberate difference from Dataverse. Before an uninstall, a promote
(`DeleteAndPromote`, `StageAndUpgrade`) or an upgrade removes a table or a column, xrmverse lists every table to be dropped
that has at least one row and every column to be dropped that has at least one non-null value. If the list is not empty,
SDK, SOAP and Web API requests are refused with 400 naming each table and column and the Admin command that confirms; nothing
changes. Only the Admin API (`dropData: true`), `xrmd solution uninstall --drop-data` and the web admin dialog, after
showing the plan, drop the data. `dryRun: true` returns the plan without writing. The confirmation can carry what the
dry run showed (`expectedDataLoss`, the web admin always sends it): when the plan made under the lock drops a table or column
that is not in it (a row written since the dry run, say), the request is a **409** and nothing changes; review the plan
again. The counts are taken in the operation's transaction with a shared table lock held to the commit, so no row can be
added between the check and the drop. If you rely on Dataverse's behavior of deleting the data with the solution, expect
the first request to fail.

### Managed properties

The flags a managed file carries (`IsCustomizable`, `IsRenameable`, `CanBeDeleted`,
`CanModifyRequirementLevelSettings`, the table flags `CanCreateAttributes`, `CanCreateForms` and `CanCreateViews`, and the
audit setting of [Auditing](auditing.md)) are honoured on components with a Managed layer: a customization that would
break one is a 400 naming the flag and the component. The flags are read from the payload of the **top Managed layer** of
the component.
- `IsCustomizable` false on a table, or `CanCreateAttributes` false, refuses a new column (`CreateAttribute`, the Web API
  `POST …/Attributes`, a lookup of `CreateOneToMany` or `CreateCustomerRelationships`) and a new relationship
  (`CreateOneToMany` on either side, `CreateManyToMany`) on that table; an unmanaged import that adds a column or a
  relationship to it is refused whole.
- `CanCreateForms` false refuses the `Create` of a `systemform` whose `objecttypecode` is the table; `CanCreateViews` false
  refuses the `Create` of a `savedquery` whose `returnedtypecode` is the table. Only `Create` is checked (see Known
  limits).
- The other `CanModify*` flags guard properties xrmverse never lets anyone change, so they hold by construction.
  `CanCreateCharts` is read but has nothing to guard (see Known limits). The flags of an unmanaged component are
  informative only.

### Managed components and solutions

- Deleting a managed component directly (`DeleteEntity`, `DeleteAttribute`, a row `Delete`, …) is a 400 naming its
  solution: uninstall the solution.
- `AddSolutionComponent` and `RemoveSolutionComponent` on a managed solution are a 400; adding a managed component to an
  unmanaged solution works.
- Updating a managed `solution` row through the SDK or Web API is a 400; only the messages above change it.
- `ismanaged` of a `solution` row is set by no request: a `Create` or `Update` that gives it (any value) is a 400. Only an
  import of a managed file makes a solution managed. A `Create` or rename that gives a row the name `{name}_Upgrade` of an
  installed managed solution `{name}` is a 400 too: only `HoldingSolution` and `StageAndUpgrade` create a holding solution.
- `ExportSolution` of a managed solution is a 400 (see Known limits).
- Reads report the state: `IsManaged` of tables, columns, relationships and choices, and the read-only `ismanaged` column of the
  row component tables (`savedquery`, `systemform`, `webresource`, `role`, `sdkmessageprocessingstep`, …).
- Internal operations (restoring a layer, uninstalling, promoting) **bypass** these guards: they must be able to change what
  a user may not. An uninstall's deletes need the caller's Delete privilege on each component's table (a promote needs
  `prvImportCustomization`); restores are metadata updates and are not privilege-checked on their own.

### Restore

When a layer goes away, the component is rebuilt from the payload of the layer below it, through the importer
in a restore mode (no `solution`, `solutioncomponent` or `importjob` rows, no Active marking).
- A restore **replaces** labels: the label set of the restored layer replaces the current one; a language that only the
  removed layer had disappears. The colour of an option is replaced the same way: an option without a colour in the
  restored layer loses its colour.
- An option value that an upgrade removed from a choice is **kept** (rows may use it), and the result carries a warning.
- Uninstall and promote **return the warnings** of their restores, as entries of `kept` (component and reason) in the Admin API
  result, the CLI output and the web admin.
- A single restore whose layers were captured in different languages is a 400 naming both languages and the component;
  nothing changes.
- A difference the store cannot apply (a shorter `MaxLength`, another attribute type) fails the whole operation, as an
  import does. A difference in a relationship's cascade behavior is **not reverted**; the restore logs a warning naming the
  relationship.
- The system columns of a table that a managed file lists (primary key, `createdon`, …) are layered like any other column, so
  they come back too, though they are not rows of `solutioncomponent`.

### Where to manage them

- **Web API actions** and the SDK messages: the table at the top of this page.
- **Admin API:** `POST /admin/solutions/{uniquename}/uninstall` and `POST /admin/solutions/{uniquename}/promote`, body
  `{ "dropData": false, "dryRun": false, "expectedDataLoss": [{ "table": "…", "column": null }] }` (all optional;
  `expectedDataLoss` takes the dry run's `dataLoss` entries, their `rows` ignored; 409 when the plan under the lock drops
  anything else), response `{ deleted[], restored[], kept[], dataLoss[] }` (`kept` also carries the warnings of restores);
  `GET /admin/solutions/{uniquename}/layers` and `GET /admin/layers/{componenttype}/{objectid}` list layers (solution, kind,
  order, version).
- **CLI:** `xrmd solution import <file> --mode update|upgrade|stage` (default `update`) and `--overwrite`;
  `xrmd solution uninstall <name> [--drop-data] [--dry-run] [--yes]`; `xrmd solution promote <name> [--drop-data] [--yes]`;
  `xrmd solution layers <name>` and `xrmd solution layers --type <t> --id <id>`. An `import --mode upgrade` that data loss
  refuses names `--mode stage` followed by `promote --drop-data` as the way forward.
- **MCP:** the read-only tool `list_component_layers` (by solution or by component); `list_solutions` shows `holding`. There is
  no destructive solution tool.
- **Web admin**, Solutions page: managed and holding badges, an import mode selector, **Uninstall** and **Apply upgrade**
  dialogs that show the plan and require a checked box when data would be lost (the confirmation sends the loss shown; on a
  409 the dialog loads the new plan), and a Layers panel listing the layers a solution owns.

## Import

- Order of application (§7): publisher → global option sets → entities (non-lookup attributes) → relationships and lookups →
  roles and privileges → `savedquery`/`systemform`/sitemap/app module as opaque XML → web resources (stored, not served) →
  custom controls → `pluginassembly` → `plugintype` → custom APIs → environment variables → processes (`workflow`, below) → `sdkmessageprocessingstep` and images →
  `solutioncomponent`.
- All in **one** SQL transaction: an error midway also rolls back tables and columns already created; the metadata, security
  and step caches stay unchanged. The `importjob` row is written after the rollback, in its own transaction, with
  `progress` 0 and the XML error log. Dependencies (entities, attributes, option sets referenced and absent from both
  the package and the org) are checked before writing: 400 naming the missing component.
- A solution with `<Managed>1</Managed>` is imported as managed: install, update or stage, by the rules of
  [Managed solutions and layers](#managed-solutions-and-layers). The Microsoft first-party warning applies to it exactly as to an unmanaged one. A solution file that is
  a patch (a parent-solution element in `solution.xml`, or a unique name ending `_Patch_` and eight hex digits) is a 400
  before anything is written; patches are not supported yet.
- Currencies are data, not solution components: a solution never carries `transactioncurrency` rows. A money column in the file
  brings `transactioncurrencyid` and `exchangerate` with it (so the old warning that these system attributes are not created is
  gone), and the `TransactionCurrency_*` relationship in the file is not imported, as system relationships are derived. An
  export writes the pair and the `_base` columns. An import applies the labels of a `_base` column from the file (an unmanaged
  import over a managed, non-renameable money column is refused); see [Currencies](currencies.md).
- Re-import (same or higher version) = idempotent, additive upgrade, like Dataverse's unmanaged: new columns and
  options added, labels updated, nothing deleted. The colour of an option (`Color` on `<option>`, `#RRGGBB`; another
  value is a 400 naming the option) is updated when the file gives one; an option without `Color` in the file keeps its
  current colour. On system attributes labels, required
  level and state options are applied; any other difference from those generated by xrmverse is a warning in the log naming the
  property. On custom attributes and relationships a difference that the store cannot apply is 400 (component and
  property); a relationship with the same `SchemaName` but different entities is 400.
- Privileges: `prvImportCustomization` is enough; components are applied without per-component checks (Dataverse
  checks them one by one), except for the system table rules (standard roles and roles with a template → 400; a step with
  `impersonatinguserid` requires the importer's direct `prvActOnBehalfOfAnotherUser`).
- Roles: created in the root BU with their `roleid`; privileges are resolved by name, an unknown one is a warning and
  is skipped.
- Steps: messages are resolved from the Dataverse `sdkmessage` ids (name ↔ id table for `Create`, `Update`, `Delete`, `Retrieve`,
  `RetrieveMultiple` and `Associate`, the ids observed in a real export) or by name for custom actions; an unknown id is a 400 naming it. Filters are resolved from message +
  `PrimaryEntity`; the export writes the `SdkMessageFilterId`s of the xrmverse catalog.
- Custom APIs (`customapis/`, component type 9990): applied after the plug-in types and before the steps, so a step that
  xrmverse exported on an API's message imports back (a step on a global API's message keeps its `none` filter only when
  the API is in the same file); a step of a Dataverse export on an API's message is a 400 (its
  message id cannot be mapped). Validation, re-import rules and export: see [Custom APIs](custom-apis.md#solutions).
- Environment variables (`environmentvariabledefinitions/{schemaname}/`, component type **380**, #167): applied after the
  custom APIs. Each `environmentvariabledefinition.xml` becomes an `environmentvariabledefinition` row, found by `schemaname`
  without regard to case and updated (its `schemaname` stays as stored), else created with an id derived from the name. It is
  a component of the solution whether or not `solution.xml` lists it under `RootComponents` (the OpenProject365 export lists
  none), and it is layered like the other row components: a managed uninstall deletes it, and its value with it (Delete
  Cascade); uninstalling an upper managed solution restores the definition of the layer below, leaving the value as it is.
  The base language's labels fill `displayname` and `description`; every label, `iscustomizable` and any element xrmverse
  does not model are kept for the export. An `environmentvariablevalues.json` beside it creates the definition's value row, or
  updates it when there is one (the file wins); without that file an existing value, such as one set with `xrmd envvar set`,
  is left as it is. A value file is not a component of its own (381 is never tracked). After the rows are written, the import
  job has one warning per definition left with neither a value nor a default value: `Environment variable '<schemaname>' has
  no value and no default value: set one with `xrmd envvar set`.` The rules of the tables apply to the import as to any
  client (see [System tables](system-tables.md)); two definitions of one file whose names differ only by case are two
  entries differing only by case, a 400 before anything is written. The text of a value is not checked against `type`.
- Custom controls (`<CustomControls>` and `Controls/`, component type **66**, plan 3.5 UI-1d): applied after the web resources.
  For each `<CustomControl>` the manifest its `FileName` names (leading slash dropped, e.g.
  `Controls/{name}/ControlManifest.xml`) is read, and every file its `<resources>` names (`code`, `css`, `resx`, `img`, `html`,
  relative to the manifest's folder) must be in the archive: a missing manifest or file is a 400 naming the control and the
  file, before anything is written. The control becomes a `customcontrol` row, found by `name` without regard to case and
  updated, else created with an id derived from the name: `manifest` (the manifest as text), `version` (the `control`'s
  `version`), `compatibledatatypes` (the `of-type` of each bound property, comma-joined, then `dataset` for a control with a
  `data-set`), and the `<CustomControl>` element kept for the export. Every file of the manifest's folder (the manifest
  included) replaces the control's files, byte for byte, so a re-import with a new bundle serves the new one. It is a component
  of the solution (listed under `RootComponents` by name, as in the OpenProject365 export) and is layered like a web resource:
  a managed uninstall deletes the row and its files, and uninstalling an upper managed solution restores the control and the
  files of the layer below. The `Controls/` entries are no longer kept unread, and no warning names them. The table is written
  only by the import (see [System tables](system-tables.md)). A file whose path, relative to the manifest's folder, is longer
  than 400 characters is a 400 naming the control and the file; a control whose manifest folder lies inside another control's
  folder keeps its own files, and the outer control does not take them. A solution imported before this version has no
  `customcontrol` rows (its `Controls/` entries were kept unread): import it again to get its controls.
- Processes (`<Workflows>`, component type **29**, from memory): a `<Workflow>` of category 0 (workflow), 2 (business rule) or
  3 (action) becomes a `workflow` definition row with its `processtrigger` rows and the XAML of its `XamlFileName` (a missing
  XAML file is a 400 naming the file and the process); the element itself is kept, so the export writes it back with the
  changed values in place and the XAML byte for byte. The process is saved Draft, then activated when the file's `StateCode` is
  1 or when `PublishWorkflows` is true. An activation the server refuses is a warning in the import job log (group
  `workflows`), and the process stays Draft; steps xrmverse does not run are a warning naming the process and each step, the
  process keeps its state and fails when triggered, and the rest of the solution imports. A `StateCode` other than 0 or 1 is
  a warning and the process stays Draft. An action gets its message at import (see [Classic workflows](workflows.md#actions));
  a step in the same file on that message resolves to it. Processes of category 1 and 4–7 (dialogs, business process flows,
  modern, desktop and AI flows) and a `<Workflow>` without `Category` stay preserved XML, with a warning "kept for export but
  not run"; when the file has no process of category 0, 2 or 3, the whole node is kept unchanged. Only definitions are
  solution components: activation rows are not tracked, and `AddSolutionComponent` of an activation is a 400. Processes take part
  in managed layers (section `Workflows/Workflow`, keyed by the process id), and an uninstall removes them after the steps and
  cancels their waiting jobs. See [Classic workflows](workflows.md).
  A process written as YAML (`xrmd workflow apply --solution <unique name>`, see [Workflow authoring as code](workflow-authoring.md))
  is added to an unmanaged solution as component type 29 like an imported one, and the export carries it: for a process without
  kept XML the export writes the `<labels><steplabels>` of its business rule step labels and the `<Descriptions>`, as the real
  export's rule has them, so an authored rule exports like an imported one.
- Unknown elements of supported components and nodes of unsupported components (ribbon, report, template, processes of the
  categories above that do not run, …): see [Preserved XML](#preserved-xml).

### Request options

| Request | Accepted | Rejected (400 naming the option) |
|---|---|---|
| `ImportSolution`, `ImportSolutionAsync`, `ExecuteAsync(ImportSolution)` | `CustomizationFile`, `ImportJobId`; `HoldingSolution` (managed files only: stage); `OverwriteUnmanagedCustomizations` (removes the Active markers of the file's components, managed files; no effect on an unmanaged file); `PublishWorkflows` (activates every process of category 0, 2 and 3 in the file whose `StateCode` is 0 or 1); `SkipProductUpdateDependencies`, `SkipQueueRibbonJob`, `AsyncRibbonProcessing` with any value (there is nothing to skip) | `HoldingSolution` true on an unmanaged file, `ConvertToManaged` true, non-empty `ComponentParameters` or `SolutionParameters`, `IsTemplateMode` true, non-empty `TemplateSuffix`/`TemplateDisplayNamePrefix`, any `LayerDesiredOrder`, `SchemaUpdatesOnly` true (Web API only), `StageAndUpgrade` as a parameter (Web API only: use the `StageAndUpgrade` action) |
| `StageAndUpgrade`, `StageAndUpgradeAsync`, `ExecuteAsync(StageAndUpgrade)` | `CustomizationFile`, `ImportJobId`, `OverwriteUnmanagedCustomizations`, `PublishWorkflows`, `SkipProductUpdateDependencies`, `SkipQueueRibbonJob`, `AsyncRibbonProcessing`, as for `ImportSolution`; a managed file only | an unmanaged file, `ConvertToManaged` true, non-empty `ComponentParameters` or `SolutionParameters`, any `LayerDesiredOrder`; `HoldingSolution`, `SchemaUpdatesOnly`, `IsTemplateMode`, `TemplateSuffix`, `TemplateDisplayNamePrefix` are not parameters of the action (Web API 400) |
| `ExecuteAsync` | `Request` = `ImportSolutionRequest`, `StageAndUpgradeRequest` or `DeleteAndPromoteRequest` | any other request, also `Merge` |
| `DeleteAndPromote`, `DeleteAndPromoteAsync` | `UniqueName` (a solution with a holding solution `{name}_Upgrade`) | any other parameter; no holding solution 400 |
| `UninstallSolutionAsync` | `SolutionUniqueName` | any other parameter; `Default`, `Active`, an unmanaged solution |
| `CloneAsPatch`, `CloneAsSolution` | — | always 400 (patches are not supported yet) |
| `ExportSolution` | `SolutionName` (an unmanaged solution), `Managed` false, every `Export*` false | `Managed` true, a managed solution, an `Export*` true, `TargetVersion`, `ExportComponentsParams`, any other parameter; system solutions; a step on a message whose Dataverse id xrmverse does not know |
| `RetrieveFormattedImportJobResults` | `ImportJobId` (404 if unknown) | — |
| `AddSolutionComponent` | `ComponentId`, `ComponentType` (types tracked by xrmverse), `SolutionUniqueName`, `DoNotIncludeSubcomponents` (`rootcomponentbehavior` 1 on an entity), `AddRequiredComponents` (adds the attributes of an entity; no-op for option sets, web resources, sitemaps, plug-in assemblies) | non-empty `IncludedComponentSettingsValues`, `AddRequiredComponents` on the other types (xrmverse does not compute dependencies), an untracked type; nonexistent component 404 |
| `RemoveSolutionComponent` | `SolutionComponent`/`ComponentId`, `ComponentType`, `SolutionUniqueName` | removal from `Default`; component not present in the solution 404 |

Via Web API a parameter the action does not have, a value of the wrong type or a `null` for a non-nullable parameter is
a 400 naming the parameter (the error code Dataverse uses for unknown parameters is not verified). A JSON body
with duplicate keys is a 400 (`Invalid request body: …`), for every Web API endpoint. A non-nullable parameter
**missing** from the body is not rejected: a boolean counts as `false`, like the default value of the SDK request (not
verified that Dataverse accepts it instead of rejecting it).

### Asynchronous import

`ImportSolutionAsync` and `ExecuteAsync` write an `asyncoperation` row with `operationtype` 203
("Import Solution Async Operation", Learn), `messagename` `ImportSolution`, the file in `data` (base64) and the `importjobid`
in the `xrmverse_importjobid` column (`importjob` is not a `regardingobjectid` target on Learn). The background worker
runs it as the user who queued it: `3/30` on successful import, `3/31` on failed import; `data` is cleared when the
job ends. Poll `asyncoperation(id)` until `statecode` 3. The worker runs the import **inline**, in the same
loop that runs the asynchronous plug-in jobs: while an import is running, queued asynchronous plug-in jobs wait.

## Export

The components are the `solutioncomponent` rows of the solution; metadata come from the cache, rows from the
database, preserved XML is re-emitted in its place. The `customizations.xml` of a just-imported solution, normalized,
matches the original; the plug-in assembly comes out byte for byte identical. An entity with behavior 0 is written with all
its attributes, custom relationships and its views and forms; with behavior 1 and 2 with primary key, primary name and
the attributes the solution contains: **behavior 1 and 2 export identically** (xrmverse does not keep separate
table metadata). System relationships are not written (xrmverse derives them from lookups with their own names); global
option sets not contained in the solution become `MissingDependency`.
Environment variable definitions (#167) are written from their rows as `environmentvariabledefinitions/{schemaname}/environmentvariabledefinition.xml`,
with what the import kept merged back and the base-language label of `displayname` and `description` brought in line with the
row (the other languages' labels are kept); `environmentvariablevalues.json` is written beside it when the definition has a
value row, with `iscustomizable` "1" (an unmanaged export). Each definition is listed as a `RootComponent` of type 380 by
schema name, as a Dataverse export lists it. So a definition created through the API and added to a solution with
`AddSolutionComponent` exports too. The definition files are equal to the imported ones in content, not byte for byte; a value
file whose `iscustomizable` was "0" is written back with "1".
Custom controls (plan 3.5 UI-1d) are written from their rows: one `<CustomControl>` per control under `<CustomControls>`, in
name order (the order of the source file is not kept), with the element the import kept and its `Name` and `FileName`; every
file of the control at its path under the manifest's folder, byte for byte as imported; and a `RootComponent` of type 66 by name.
App settings and looks (plan 3.6 UI-2a): the `<appsettings>` of an app travel with the app, so an app's
`xrmverse_Renderer` and `xrmverse_Presentation` values come back on export and are applied by the import in the target
organization; the overlay JSON and the white-label images are web resources and travel as ordinary web resource components
(add them to the solution). The environment values of settings (`organizationsetting` rows, set in the web admin) are **not**
exported: an imported app that relies on the environment's look shows the target environment's look (or classic). Dataverse can
carry setting environment values in a solution; xrmverse does not yet (a known limit). See
[Model-driven apps](model-driven-apps.md#looks-classic-and-modern).
Choice colours: the background colour of a choice value is the option's `Color` attribute in `customizations.xml`, written
on export and read on import, so it round-trips with Dataverse. The text colour of a value is not part of the choice: it
lives in the `choices` key of a presentation overlay (an xrmverse extension that Dataverse ignores), which travels as
that overlay's web resource. See [Choice colours](model-driven-apps.md#choice-colours-modern-look).

- Calculated columns (`SourceType` 1 with a `FormulaDefinitionFileName`, see [Calculated columns](calculated-columns.md)): the XAML file is read and
  kept as the column's `FormulaDefinition`, and the column is created after the simple columns and relationships of the import. Export writes
  the file back byte for byte at `/Formulas/<table>-<column>.xaml` (confirmed on a parity export).
- Rollup columns (`SourceType` 2 with a `FormulaDefinitionFileName`, see [Rollup columns](rollup-columns.md#solution-files)): read and written
  the same way, with the `_base` of a Currency rollup as an attribute with `SourceType` 2 and `CalculationOf`; the generated `_date` and
  `_state` columns are never written (and skipped if a file has them). Calculated and rollup columns are created after the simple columns
  and relationships, in dependency order, and each imported rollup's Mass Calculate Rollup Field job is due at once. An uninstall removes
  calculated and formula columns, then rollup columns, before the columns and tables they read.
- Formula columns (`SourceType` 3, Power Fx, see [Formula columns](formula-columns.md#solution-files)): the formulas of a table share one
  file, `/Formulas/<table>-FormulaDefinitions.yaml`, with one line `<column>: =<formula>` per column, named by every formula column's
  `FormulaDefinitionFileName` (confirmed on a parity export). Import reads each column's line and stores the formula without the `=`;
  formula columns are created with the calculated and rollup columns, in dependency order. Export writes the file back, one line per
  formula column in attribute order, with CRLF line ends (the parity export's file byte for byte). A managed layer keeps only its own
  columns' lines, so an uninstall restores each column's formula from the layer below.

## Preserved XML

What a solution file provides and xrmverse does not model is not silently lost.
- Nodes of unsupported components (ribbon, report, template, processes of category 1 and 4–7, …), the solution names in all languages and
  the publisher details: opaque XML in the `solution` row (`xrmverse_unsupportedxml`), re-emitted on export.
- Archive entries that xrmverse does not read (the folders of recent exports such as `bots/`, `botcomponents/`,
  `Formulas/` (except the XAML of calculated and rollup columns and the YAML of formula columns, which are read), and the `Workflows/` and `CanvasApps/` files of
  unsupported components; the XAML files of processes of category 0, 2 and 3 are read, not kept): kept byte for byte in the same column and written back at the same path on export; the
  import logs one warning per top-level folder. They have no effect in xrmverse. The `customapis/` folder is read and
  applied (see [Custom APIs](custom-apis.md)); a file under it that is not one of the three custom API file paths is still
  kept this way, with the warning. The same holds for `environmentvariabledefinitions/` (read and applied since #167, see
  [Import](#import)) and a file of it on another path than the two it reads.
- `MissingDependencies` of `solution.xml` are not verified: xrmverse checks itself that the tables and choices the
  components use exist, and the import logs one warning with the number of declared dependencies.
- Unmodeled children of entities, attributes, relationships, option sets, web resources, steps, images, assemblies, views, forms
  and sitemaps (search flags, managed properties, `DisplayMask`, `SecurityTypes`, …): preserved as opaque XML
  in the original position (`meta.*.unmodelled_xml`, `xrmverse_componentxml` column of the system tables) and re-emitted
  on export. They have no effect in xrmverse and are not visible via SDK or Web API. The exception is `IsAuditEnabled`
  of tables and columns and the table's `CanModifyAuditSettings`, which are modelled (see [Auditing](auditing.md)), and
  the table's `AutoCreateAccessTeams` (below).
  The associated menu configuration of relationships is preserved the same way.
- The internal opaque columns are written **only by the import**: `xrmverse_componentxml` of every table,
  `solution.xrmverse_unsupportedxml` and `appmodule.descriptor` (read-only in Dataverse). A Create/Update via SDK or
  Web API that contains one is a 400 naming the column; app modules can still be created via SDK, without `descriptor`.
  A preserved node that has the name of a root node written by xrmverse (`Entities`, `AppModules`, `Languages`, …) is
  an explicit export error (500 naming it), never a replacement.
- The elements the normalizer ignores in the comparison are only those of form (`IntroducedVersion`,
  `CustomizationLevel`, order of keyed sets, name case, empty labels, …).
- An entity that the file carries only as a "shell" (not contained, or behavior 2) does not overwrite the preserved settings;
  the log flags it with a warning naming the component.

## Deleting a solution

By design (decision of 2026-11-08, option C), the Delete of a `solution` row (SDK `Delete`,
Web API `DELETE solutions(id)`) deletes only the container, like Dataverse for an unmanaged solution: the `solution` row
and its `solutioncomponent` rows disappear, **every component remains** (tables, columns, data, roles, steps,
assemblies, web resources) and is tracked by `Default`; the publisher remains. The Delete of a **managed** row is an
**uninstall** (see [Managed solutions and layers](#managed-solutions-and-layers)): its components are removed, restored or kept according to their layers,
and data is never dropped without the explicit Admin confirmation. The unmanaged cascade (delete the components of an unmanaged
solution, dropping tables and data) is available only through an explicit xrmverse command (`xrmd` CLI / Admin API): **never**
via SDK or Web API. The Admin cascade on a managed solution is a 400 that points to `uninstall`. `Default` and `Active` are
rejected with 403. The dependency refusal `0x8004f01d` exists only for the uninstall of a managed solution with a layer above
(see above); an unmanaged delete has none.

## Size limit

- A `CustomizationFile` (decoded) over 95 MB, Dataverse's maximum for a solution file, is rejected by
  `ImportSolution`, `ImportSolutionAsync` and `ExecuteAsync(ImportSolution)`, via SOAP and Web API, before writing
  anything: 400 `0x80040375` (ImportFileTooLargeToUpload), "The import file is too large to upload." (code and
  message from Learn). The limit is 95 × 1024 × 1024 bytes: Learn says "95 MB" without specifying the unit.
- The **uncompressed** content of the zip is limited to 100 MB in total (`SolutionZip.MaxUncompressedBytes`, zip
  bomb defense): beyond that it is 400 "The solution file is larger than 100 MB uncompressed.". Dataverse does not have this limit: a
  valid file of 95 MB compressed that expands beyond 100 MB is explicitly rejected by xrmverse.
- The routes that carry a solution file (SOAP endpoint, Web API `ImportSolution` and `ImportSolutionAsync`) accept
  requests up to 128 MB (134,217,728 bytes), Dataverse's maximum for a request, so a 95 MB file fits once
  base64-encoded. Beyond that: 413 (via SOAP a fault with status 413). The other routes stay at Kestrel's default
  limit (30,000,000 bytes, 413 beyond), lower than Dataverse's 128 MB.
- The SOAP endpoint does not know the message before reading the body: the 128 MB limit and the array quota
  (`byte[]` up to 128 MB) apply to **every** SOAP message, not only the import, and the body is read entirely into memory
  before deserialization. Memory per SOAP request can therefore reach a few hundred MB (body plus
  deserialized objects); the buffer is not preallocated from `Content-Length` (a client could declare 128 MB without
  sending it).
- A large import holds in memory, per request, the file, the decompressed entries (up to 100 MB), the XML documents and the
  parsed package: the peak can reach **a few hundred MB per import**, and there is no limit on the number of concurrent
  imports (no gate): several large imports together add up their memory.

## Existing databases

On startup an existing database receives the migrations (`0002`–`0008`, `meta.*` columns; `0008` adds
`meta.component_layer` and `meta.solution_rank`; the row component tables get a read-only `ismanaged` column, and rows that
existed before count as unmanaged; before
1.0 the old `0003_solution_settings` was merged into `0003_unmodelled_xml`: a development database that had
applied only the old `0003` must be recreated) and the
new columns of the system tables (`xrmverse_importjobid`, `xrmverse_componentxml`), so import, export and asynchronous
import work. It does not, however, receive changes to columns already present or the new privileges of the standard roles:
- `asyncoperation.operationtype` does not have the options 203, 207, 208 and 211: those jobs are written and read normally,
  but without a label (no formatted value such as "Import Solution Async Operation");
- the `asyncoperation.data` metadata keep `MaxLength` 1,048,576 instead of 1,073,741,823 (the SQL column is
  `nvarchar(max)` and the asynchronous import writes it anyway): `RetrieveEntity`/`EntityDefinitions` show the old value;
- System Customizer does not have the new privileges (metadata, `prvImportCustomization`, `prvExportCustomization`,
  `prvPublishCustomization`, solution tables): standard role privileges are seeded only when the role row
  is created (see [Security](security.md)), so a System Customizer receives 403 on these messages
  (System Administrator does not, because it has everything by template).

- a database created before Plan 5 has no `solutioncomponent` rows for the components created earlier (tables,
  columns, steps, …): `Default` is **incomplete** and an export or a query on `solutioncomponent` does not see them until they are
  added with `AddSolutionComponent`.

To align a database created before 1.0 there are two ways: recreate it (empty database, new full seed),
or keep it and manually grant the missing privileges to the System Customizer role with `AddPrivilegesRole` (the two
`asyncoperation` metadata differences remain).

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- **Known limits of managed solutions** (each is refused explicitly or documented here):
  - **No patches** (a later item): `CloneAsPatch`, `CloneAsSolution` and a patch file are 400.
  - **No managed export** (a later item): `ExportSolution` with `Managed` true, or of a managed solution, is 400. xrmverse
    cannot produce managed files.
  - Forms, site maps and app modules are not merged between layers: the top layer wins and the hidden one is a log warning.
  - A holding solution is recognized by the `_Upgrade` suffix of its unique name only.
  - A `StageAndUpgrade` of the same version as the installed one is refused (checked against Dataverse in the checklist).
  - Writes to `fieldpermission` rows do not put an Active marker on the component.
  - `GET` layers of a component that exists but was never layered returns 404.
  - An unmanaged customization of a seeded component made before the first managed solution layered it becomes part of
    that component's System layer; xrmverse cannot tell it apart.
  - `RemoveActiveCustomizations`, the `msdyn_componentlayer` and `msdyn_solutioncomponentsummary` virtual tables,
    `RetrieveDependenciesForUninstall`, `RetrieveDependentComponents` and dependency tracking beyond layers are not available
    (layers are readable through the Admin API, CLI, MCP and web admin).
  - `LayerDesiredOrder`, `ConvertToManaged`, templates and `SolutionParameters` stay refused.
  - Data loss is refused on SDK, SOAP and Web API paths (deliberate, see above).
  - `CanCreateCharts` is read from a managed table but not enforced: xrmverse has no chart table
    (`savedqueryvisualization`), so there is no chart to create.
  - `CanCreateForms` and `CanCreateViews` are checked on `Create` only: an `Update` that moves an existing form
    (`objecttypecode`) or view (`returnedtypecode`) onto a table whose flag is false is not refused.
  - The SDK or Web API `Delete` of a managed `solution` row is its uninstall: it runs outside the pipeline, so no plug-in
    step registered on the Delete of `solution` fires, and the audit records the deletes of the uninstall (the solution row
    and its components) as the internal caller, not as the user who asked.
  - The restores of an uninstall or a promote are not privilege-checked on their own (internal operations bypass the
    customization guards, ruling R2); only the deletes need the caller's privileges.
- Views, forms, sitemaps and app modules are opaque XML without validation: an invalid view or form is imported without
  error. Web resources are stored but not served.
- `asyncoperation.regardingobjectid` remains a `UniqueIdentifier` (not navigable).
- In `$metadata` the metadata entity sets (`EntityDefinitions`, `RelationshipDefinitions`,
  `GlobalOptionSetDefinitions`, …) are minimal open types (key and name) and `crmbaseentity` is not their base type: a
  client that generates proxies from the CSDL gets them incomplete (`ServiceClient` uses `RetrieveEntity` and is not affected).
- The referencing navigation property of a custom 1:N relationship is its `ReferencingEntityNavigationPropertyName` (default:
  the lookup's `SchemaName`, `xv_ProjectId`, as Dataverse) in `$metadata`, `$expand`, `@odata.bind` and the retrieved
  relationship; `@odata.bind` accepts exactly that name. A re-import does not change the name of an existing relationship.
- A Customer column (`contact.parentcustomerid`, or one made with `CreateCustomerRelationships`) has one navigation property
  per target in `$metadata`, `@odata.bind` and the `associatednavigationproperty` annotation:
  `parentcustomerid_account` and `parentcustomerid_contact` (the `ReferencingEntityNavigationPropertyName` of each
  relationship). A bind through the other table's name, through both names in one body, or through the bare
  `parentcustomerid@odata.bind` fails with 400 naming the valid names. `$expand` of a Customer column is not supported (as for
  any polymorphic lookup). `ownerid` and the other multi-target lookups keep one navigation property. A Customer column is
  created only with `CreateCustomerRelationships` (SDK or `POST CreateCustomerRelationships`), for exactly `account` and
  `contact`. The Web API action takes the body of the Microsoft documentation as it is: a `Lookup` typed
  `ComplexLookupAttributeMetadata` (or `LookupAttributeMetadata`) and, when it gives a `SchemaName` and no `LogicalName`,
  the `SchemaName` in lower case as the logical name (only this action derives it; the other creation paths need a
  `LogicalName`). On its relationships Delete is Cascade, RemoveLink (the default) or Restrict and Merge is Cascade (the
  default; the only valid option on Dataverse for `account` and `contact`) or NoCascade; any other Delete or Merge value is
  refused with 400, and Assign, Share, Unshare, Reparent, Archive and RollupView are stored as given (not enforced, see
  "Delete cascades"), so a solution exported from Dataverse with a Customer column imports. The action is not declared in
  `$metadata`.
- **Delete cascades.** The Delete behavior of every saved one-to-many relationship (seeded, created with `CreateOneToMany`
  or `CreateCustomerRelationships`, or imported) is enforced in the transaction of the `Delete`: **Restrict** refuses the
  `Delete` with 400 naming the relationship while records point to the record (a record pointing to itself, or one being
  deleted by the same cascade, does not count); **Cascade** deletes the records that point to it first, each as a `Delete`
  message of the pipeline (the privilege check of the caller on that record, its plug-in steps with the parent `Delete` as
  `ParentContext` at depth + 1, its files, its shares and its own cascades), so a chain deeper than the pipeline's maximum
  depth (8) fails with the infinite-loop error, and a cycle or self-reference is deleted once; **RemoveLink** clears the
  lookup (and the type column of a Customer column) of the records that point to it with a direct update: `modifiedon`
  and `modifiedby` do not change, no plug-in runs for those records. Whether Dataverse runs the cascaded deletes' plug-in
  steps at depth + 1 and checks the caller's privileges on each child is not verified against a real Dataverse (see the
  accounts and contacts checklist). With auditing on, each record deleted by a Cascade gets its own Delete audit row
  (when its table is audited); the lookups cleared by RemoveLink are not audited (see [Auditing](auditing.md), Known limits).
  A failure anywhere rolls the whole `Delete` back.
  Relationships implied by a lookup without a saved relationship (system lookups such as `ownerid`) have no cascade.
- **Known gap: the Assign, Share, Unshare, Reparent and Merge cascades are not enforced.** They are stored, imported,
  exported and reported by the metadata reads, but `Assign`, `GrantAccess`/`ModifyAccess`/`RevokeAccess` and a change of
  parent do not cascade to the child records, and `Merge` itself is refused. Code that relies on assigning or sharing an
  account to also assign or share its contacts sees different data than on Dataverse.
- `CreateEntity`/`UpdateEntity`/`UpdateAttribute` reject (400) the properties that xrmverse does not model or apply
  (see the metadata write rules above). On column creation the flags that the export
  writes from the default model are checked, and so are the other column properties the export does not write
  (`IsValidForAdvancedFind`, `IsValidForForm`, `IsValidForGrid`, `IsValidForRead` other than true; any value of
  `CanBeSecuredFor*`, `DeprecatedVersion`, `InheritsFrom`, `ColumnNumber`, `LinkedAttributeId`): they are rejected with 400.
- A metadata write holds a pool connection open while waiting for the metadata lock (one write at a
  time per process): many concurrent metadata writes can exhaust the connection pool.
- Web API `$ref`: an unknown entity set in the request URL (`{set}({id})/{nav}/$ref`) is 404, in the reference
  URL (`@odata.id` of the body or `$id`) it is 400.

### Not yet verified against a real Dataverse export

These points wait for a dedicated step with a real solution (decision of 2026-11-08); today the verification uses the
hand-written fixture `tests/fixtures/solutions/XrmverseFixture`:
- the Dataverse `sdkmessage` ids (only `Create` and `Update` mapped);
- the shape of `AppModule` (componenttype 80 included);
- the `importjob` log format;
- the form type mapping (`systemform.type` ↔ `forms/@type`);
- the shape of `MissingDependency`;
- the SDK default values that the export writes for the unmodeled properties of components created in xrmverse;
- the columns of the SpreadsheetML workbook of `RetrieveFormattedImportJobResults`;
- the meaning of the `id` returned by `RemoveSolutionComponent`;
- the error code for an unknown parameter of a Web API action.
- whether Dataverse accepts (as `false`) or rejects a non-nullable boolean parameter missing from the body of a Web API action.

## See also

- [CLI](cli.md)
- [Admin API](admin-api.md)
- [Web admin](web-admin.md)
- [Plug-ins](plugins.md)
- [Custom APIs](custom-apis.md)
- [Classic workflows](workflows.md)
- [Migration from Dataverse](migration.md)
