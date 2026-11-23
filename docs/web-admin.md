# Web admin

[← xrmverse documentation](../README.md#documentation)

## Introduction

The host serves a web admin (React single-page app, built from `ui/`) at the root of the same host and port,
e.g. `http://localhost:5001/` for the first organization of `xrmd server init` (`http://localhost:5000/` with the repository
compose file). The Docker image builds it into `wwwroot` (`docker/Dockerfile`); outside Docker the host
serves the directory in `Ui:RootPath` (default `wwwroot` under the content root) and, if it holds no build, a placeholder
page. The shell uses only the Web API and the Admin API: every write goes through the Core, so the pipeline, plug-ins and
security apply as for any other client. The anonymous `GET /ui/config.json` publishes the public values the shell boots
from (`authMode`, `audience`, `authority`, `clientId`, `scope`, `apiVersion`); never the signing key.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Admin experience | one React single-page app for the whole organization | Differs: replaces the Power Platform admin center and maker portal for what xrmverse supports |
| Writes | only through the Web API and the Admin API, so the pipeline, plug-ins and security apply | Same rules as any client |
| Theme | Fluent UI v9 with the xrmverse brand, light and dark; follows the environment's presentation theme | Differs: own look |
| Cascade solution delete | "Delete solution and its components", System Administrator only | Extra |
| Plug-in registration | with the CLI; the page browses and enables or disables steps | Narrower: no registration from the browser |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Open it and sign in | `/manage` (the host root opens `/apps`), Development or Entra sign-in | [Opening the web admin](#opening-the-web-admin) |
| Find a section | `/manage/*` | [Sections](#sections) |
| Colour the values of a choice | the **Colours** button of a choice column | [Choice colours](#choice-colours) |
| Edit a view | the view designer | [Views](#views) |
| Edit a form | the form designer | [Forms](#forms) |
| Delete a solution | the Solutions page | [Deleting a solution in the UI](#deleting-a-solution-in-the-ui) |
| Set up outgoing email | the Email page | [Email](#email) |
| Work on the UI itself | `pnpm -C ui dev` | [Development loop](#development-loop) |

## Opening the web admin

The web admin answers under `/manage/*` on the organization's host, for example `http://localhost:5001/manage` for the
first organization of `xrmd server init`. The base URL itself (`http://localhost:5001/`) opens the apps page `/apps` for
any signed-in user; administrators reach the web admin from there or directly at `/manage`. The header of the web admin shows a **Workflow studio** link to users who can read processes (see [Workflow studio](workflow-studio.md)). Sign in (see
[Signing in](#signing-in)); every section lives under `/manage/*`,
because `/admin/*` is the Admin API. Server paths (`/api`, `/admin`, `/mcp`, `/XRMServices`, `/metrics`, `/healthz`,
`/ui/config.json`, `/WebResources`, `/Image`) are never answered with the shell.

### Who can use it

Users holding the **System Administrator** or **System Customizer** role (directly or through an owner team; recognised
by the role template, not by name). Anyone else sees an "Access denied" page naming the two roles. `/apps` and
`/main.aspx` open the model-driven apps of imported solutions for any signed-in user: see
[Model-driven apps](model-driven-apps.md).

### Sections

| Section | What it covers | Who |
|---|---|---|
| Environment | health, doctor, version, the forms' auto-save switch (`organization.isautosaveenabled`), and the "Model-driven apps look" section: the environment's default look and presentation web resource, kept as `organizationsetting` rows (see [Model-driven apps](model-driven-apps.md)) | System Administrators; a Customizer who opens `/manage/environment` sees "Requires System Administrator" |
| Users, Teams, Security roles, Business units | identity and security | Administrators and Customizers |
| Tables | columns, relationships, views (with the view designer), forms (with the form designer), records | Administrators and Customizers |
| Solutions | import, export, delete; the cascade solution delete is for System Administrators only | Administrators and Customizers |
| Plug-ins | assemblies, steps, trace log | Administrators and Customizers |
| Async operations, Audit, Duplicate detection | system jobs, audit history, duplicate detection rules | Administrators and Customizers |
| Field security, Hierarchy security | secured columns and profiles; the hierarchy model | System Administrators only |
| Email | email server profiles, mailboxes (approve, test) and the sender policy | System Administrators only |
| About | the trademark notice, where the third-party notices are, and a link to `SECURITY.md` | every signed-in administrator or customizer |

## Signing in

- **Development (`AUTH_MODE=Development`).** The sign-in page asks for the signing key (`AUTH_DEV_SIGNING_KEY`), an
  object id (default: the seed administrator `00000000-0000-0000-0000-000000000001`) and a display name. The browser
  mints the same HS256 token as `docker/dev-token.sh` with WebCrypto; no server endpoint mints tokens. The key is kept
  in `sessionStorage` only (gone when the tab closes), never in `localStorage`. WebCrypto needs a **secure context**: open
  the shell over HTTPS or on `localhost`; elsewhere the page says WebCrypto is unavailable.
- **Microsoft Entra ID (`AUTH_MODE=Entra`).** The shell signs in with MSAL.js (redirect flow) and needs its own
  **SPA app registration**, separate from the API's:
  1. In the tenant, register an application with a **Single-page application** platform and the redirect URI of the
     shell: the origin of the page plus `/` (e.g. `http://localhost:5000/`, or your HTTPS URL).
  2. Under *API permissions* add the scope exposed by the API registration (the one whose Application ID URI is
     `AUTH_AUDIENCE`).
  3. Configure the server with the SPA's client id and that scope: `Ui:Entra:ClientId` and `Ui:Entra:Scope`
     (`UI_ENTRA_CLIENT_ID` and `UI_ENTRA_SCOPE` in `.env`, which the compose files map to `Ui__Entra__ClientId` and
     `Ui__Entra__Scope`; empty by default). The authority is derived from `Auth:TenantId`
     (`https://login.microsoftonline.com/{tenant}/v2.0`). If any of the three is missing, the shell shows a
     configuration error naming the keys instead of a sign-in button.

Errors from the server are shown with the Dataverse error code and message. Lists page 50 records at a time
(`Prefer: odata.maxpagesize=50`, `@odata.nextLink`).

## Look and feel

The web admin, the sign-in page and the model-driven apps share one theme, built with Fluent UI v9's own theme functions
from the xrmverse brand ramp (bronze, `ui/src/shell/theme.ts`): `createLightTheme` for the light theme and
`createDarkTheme` for the dark one, with the brand foregrounds lifted to ramp steps 110 and 120 so links and selected items
stay readable on dark backgrounds. The theme follows the operating system's light or dark setting
(`prefers-color-scheme`), and PCF controls get the same choice through `context.fluentDesignLanguage.isDarkTheme`. The
header bar uses the brand's pressed background with our own logo and name. The brand font is Sora
(`@fontsource-variable/sora`, OFL-1.1, bundled; system font stack as fallback, #102); regular text uses its light
weight (300).

The web admin also follows the environment's presentation overlay (the environment value "Model-driven apps look" on the
Environment page; see [The presentation overlay](model-driven-apps.md#the-presentation-overlay-version-1)). Its brand
colours, font, density and colour scheme apply to the admin as they do to the apps, whichever look the environment uses.
An app's own overlay does not apply to the admin, and `whiteLabel` (name, logo, icon) does not either: the header keeps
the xrmverse name and logo. Because the admin has no per-user colour scheme, the overlay's `colorScheme` wins over the
operating system's setting, and `system` follows the operating system. The admin renders at once with the xrmverse theme
and switches when the overlay has been read. The sign-in page keeps the xrmverse theme. When the overlay or the setting
cannot be read, the admin keeps the xrmverse theme and shows the overlay's notice once as a dismissible warning under the
header. Only a System Administrator's web admin follows the environment's look: a System Customizer's admin keeps the
xrmverse theme, because only System Administrators read the environment values. Saving "Model-driven
apps look" changes the admin's theme without a reload.

Every colour in the UI is a Fluent token (`tokens.colorBrandBackground`, `tokens.colorNeutralStroke2`, …). The only
literal colours are the brand ramp in `theme.ts` and the parsing of a chart's `PaletteCustomColors`; the
`noHardcodedColors` test fails on a six- or eight-digit hex colour or an `rgb()` value anywhere else, so a new component
picks up both themes without extra work. Navigation (admin sections, an app's site map) is Fluent's `NavDrawer`;
charts are Fluent UI React Charts (`@fluentui/react-charts`, MIT).

Each admin page has a title on its own line and its commands in a command bar under it; pages below a list (a table, a record) show a breadcrumb of the parent pages above the title. The section
navigation highlights the current section; on narrow windows (600 px and less) it collapses behind a Menu button in
the header and closes when a section is chosen.

Commands use Fluent icons (`ui/src/shell/icons.ts`); every command keeps its text label.

Lists work as the views of a model-driven app: a row has no buttons of its own. Select a row (its radio in the first column,
or a click on the row) and the command bar shows the commands for it, such as **Roles**, **Change business unit** or
**Disable** for a user; a second click clears the selection. One row is selected at a time. A link in a row (a name that
opens a record or a designer) and an editable cell (a switch, a checkbox, a privilege depth) keep working in the row. The
business unit tree and the plug-in steps select an item the same way, with a click on its name.

## Audit

The **Audit** section (`/manage/audit`, see [Auditing](auditing.md)):
- switches auditing of the organization on or off and sets the retention in days or forever (System Administrator only;
  others see the settings read-only);
- lists the audit log, newest first, filtered by table, operation and user, and opens an entry in a drawer with the old
  and new value of each column. Reading it needs `prvReadAuditSummary`; without it the page shows the server's 403.

In **Tables**, a table has an "Audit this table" switch and each column an "Audit" switch; a setting the server has
fixed is disabled. A record form of an audited table shows its **Audit history**, read from `audits` and
`RetrieveAuditDetails` (it needs `prvReadAuditSummary`). The web admin does not delete audit data: use `xrmd audit delete` or the messages.

## Field security

The **Field security** section (`/manage/security/fieldprofiles`, see [Field-level security](field-security.md)) is
shown only to System Administrators, the only role that holds the profile privileges. It lists the profiles, creates and
deletes them, and edits a profile's permissions in a matrix of secured columns (display label, logical name below)
against read, create and update; each toggle writes the `fieldpermission` row. It gives a profile to users and teams and
takes it back. The System Administrator profile is shown read-only.

In **Tables**, each column has a **Secured** switch, disabled when the column cannot be secured. A record form marks
secured columns with a lock and shows "No access" for a secured column the server left out of the record.

## Hierarchy security

The **Hierarchy security** section (`/manage/security/hierarchy`, see [Hierarchy security](security.md#hierarchy-security))
is shown only to System Administrators. It switches the manager hierarchy model on or off, sets how many levels of reports a
manager reaches (1 to 100) and lists the excluded tables, which you add by logical name and include again one by one. Saving
writes the settings through the Admin API; the server judges every value and its refusal is shown as it is.

## Access team templates

The **Access team templates** section (`/manage/security/accesstemplates`, see [Access teams](security.md#access-teams)) lists
the templates with their table, name and rights, one checkbox per right (Read, Write, Append, AppendTo, Delete, Share, Assign).
It creates a template for a table, changes its rights (each click is one update, and only teams created afterwards get the
new rights) and deletes it. Delete asks for confirmation first, because the access teams made from the template are deleted
too. A **Auto-create access teams** switch for the chosen table is on the page (a template can be created only for a table
that is switched on) and on the table's own page in **Tables**. The page is not limited to System Administrators in the
menu, but the server answers with its 403 (shown as it is) to a user without the `teamtemplate` privileges, which in the
standard roles only System Administrators hold. The web admin does not add users to a record's team: use
`AddUserToRecordTeam` (SDK, Web API) or `xrmd access-team add`.

## Change tracking

On a table's page in **Tables**, a **Track changes** switch turns change tracking on for the table (see
[Change tracking](change-tracking.md)). Change tracking cannot be turned off again, so the switch asks for confirmation
first and, once on, stays on and disabled with a hint. It is disabled with a hint too when the table cannot be tracked (an
intersect table, `audit` and the other tables the page lists as ineligible); the server's refusal of any other case is shown
as it is. The retention (`expirechangetrackingindays`) is not on a page: use `xrmd change-tracking retention` or the
organization row.

## Duplicate detection

The **Duplicate detection** page (`/manage/duplicates`, open to System Administrators and System Customizers) manages duplicate
detection (see [Duplicate detection](duplicate-detection.md)). At the top, four switches set the organization's columns:
duplicate detection, detect when a row is created or updated, detect on import and detect on offline sync; the last two carry a
hint that they are stored and nothing applies them. Below, the table lists the rules with their base table, matching table and
status (Unpublished, Publishing or Published). **New rule** and **Edit** open a dialog with the name, description, base and
matching table (only tables with duplicate detection on are offered; the tables cannot change on an existing rule), the two
checkboxes (compare text with case, ignore inactive rows of the matching table) and the conditions: a base column, a matching
column, an operator (exact match, same first or last characters with a count, same date, same date and time, exact match of
option value) and ignore blank values. **Publish** starts the system job and the page shows its progress (waiting, running,
ended) until the rule is Published or the job's message explains the failure; **Unpublish** and **Delete** ask for
confirmation first. A published rule cannot be edited: unpublish it first.

On a table's page in **Tables**, a **Duplicate detection** switch sets `IsDuplicateDetectionEnabled` (turning it off unpublishes
the rules of the table, as the hint says). The server judges every value and its message is shown as it is. A rule is saved in
several writes, so when one is refused the dialog shows the server's text and a note that the rule may be partly saved; it then
lists the conditions the server holds, and you fix the problem and save again. Running `RetrieveDuplicates` and
`BulkDetectDuplicates` is not on a page: use `xrmd duplicate-detection find` and `bulk`, or the Web API.

## Activities and notes

In **Tables** (see [Activities and notes](activities.md)):

- **New table** has an **Activity table** switch. When it is on, the primary name column is `Subject` (shown and disabled)
  and the ownership is user (disabled); the table is created as a custom activity. This cannot change later.
- A table's page has a **Notes** switch and an **Activities** switch (`HasNotes`, `HasActivities`). Each asks for
  confirmation before it saves, because neither can be turned off again; once on, the switch stays on and disabled with a
  note. An activity table shows "This is an activity table" and no Activities switch. When the server does not report a
  setting, no switch is shown; a refusal of the server is shown as it is.
- A record form of an activity shows its party lists (`from`, `to`, `requiredattendees`, …) **read-only**, read through
  `$expand={activity}_activity_parties`: each participant as "Name (table)", with the table's logical name; a participant
  with no name as the table and id; an address alone as the address; "None" for an empty list. Party lists are never sent on
  create or update, and are not offered as list columns. Change participants with the SDK or the Web API.
- A note's form hides `documentbody`. Timelines, activity forms and a note editor are not part of the web admin.

## Queues

`queue`, `queueitem` and `queuemembership` are tables like the others: Tables lists them, and you can add your own columns,
forms and views to `queue` and `queueitem`. There is no queue-specific page, apart from the one-way **enable queues** switch on a table's page; working queue items is done in a model-driven
app or with `xrmd queue` (see [Email and queues](email-and-queues.md)).

## Email

The **Email** page (`/manage/email`, System Administrators only) sets up outgoing and incoming email (see
[Email and queues](email-and-queues.md#outgoing-email) and [Incoming email](email-and-queues.md#incoming-email)). It has
three parts:

- **Email server profiles**: name, type (SMTP or Exchange Online through Microsoft Graph), server or tenant, where the
  credentials are, whether the secret is set, and state. **New profile** and **Edit profile** open a dialog with the fields
  of the type. The secret is an empty password box with the hint "Set" or "Not set": it is sent only when you type one,
  and it is never shown. The dialog sets the outgoing side; the incoming columns of an IMAP profile are written with the
  Web API (see [Setting up receiving](email-and-queues.md#setting-up-receiving)).
- **Mailboxes** of the users and queues: name, email address, approval, profile, the last outgoing test (or "Scheduled"),
  the last incoming test (or "Not receiving" when the mailbox does not receive), when the test ran, the last successful
  synchronization (**Last sync**), and the last error. Select a mailbox for **Approve** (approves its user's or queue's
  address), **Link to profile** or **Test** (schedules a test of both sides; reload the page to see the result).
  **Link to profile** opens a dialog to choose an active profile; saving sets the profile, Server-Side Synchronization and
  outgoing email on. Its **Receive email** switch shows whether the mailbox receives: turned on, the save also sets
  Server-Side Synchronization for incoming email and `enabledforincomingemail`; turned off, it sets the incoming delivery
  method to None and `enabledforincomingemail` to false. A switch left untouched changes no incoming column.
- **Sender policy**: the switch **Send only from queues** sets the xrmverse setting `xrmverse_EmailSendFrom` to `queueOnly`
  (see [Sender policy](email-and-queues.md#sender-policy-xrmverse_emailsendfrom)).

The page does not change a user's or queue's email address: set it on the record (see
[Mailboxes](email-and-queues.md#mailboxes)). A refusal of the server is shown as it is. The CLI does the same with
`xrmd mailbox` (see [CLI](cli.md#mailboxes-and-email-server-profiles)).

## Currencies

The **Currencies** page (`/manage/currencies`, System Administrators only) lists the currencies with code, name, symbol, precision, exchange rate and
state; the base currency has a **Base** badge. **New currency** opens a dialog for a System currency (ISO 4217 code) or a
Custom one (three capital letters), with name, symbol, precision and rate. The list is read-only: select a
currency and **Edit exchange rate** opens a dialog for its rate (disabled for the base currency, whose rate is fixed at 1,
with a hint). **Deactivate** asks for confirmation; **Activate** does not; neither is offered for the base currency.
**Pricing precision** opens a dialog that sets `organization.pricingdecimalprecision` (0 to 4). The page says that
changing a rate does not change existing records until a save changes their money or currency. A column with no value (a
NULL rate, for example) is shown empty. A refusal from the server is shown in a message bar as it is, inside the dialog for a
new currency (the dialog stays open). In a
record form the currency is a normal lookup; `exchangerate` and the `_base` columns are not offered, because the server sets
them. See [Currencies](currencies.md).

## Choice colours

In **Tables**, on a table's **Columns** tab, a choice, choices (multi-select) or status reason (`statuscode`) column has a
**Colours** button. It opens a dialog that lists the column's values, each with a preview of its badge as the modern look
of the model-driven apps shows it (see [Choice colours](model-driven-apps.md#choice-colours-modern-look)):

- **Background.** A colour picker, and **No colour** to remove the colour (the value is then plain text). The colour is the
  option's own `Color`: **Save** writes each changed value with `UpdateOptionValue`, then publishes the table (or the global
  choice) once with `PublishXml`. A column that uses a global choice says so: its colours apply to every column that uses
  that choice.
- **Text.** **Automatic text colour** (on by default) uses black or white, whichever reads better on the background. Turned
  off, it shows a text colour picker; the colour is written only when it differs from the automatic one. The text colours
  are kept in a presentation overlay: the dialog offers the overlays in use, the environment's (preselected) and each app's
  own, and with none in use it links to the Environment page, where one is chosen. Save writes only this choice's entry
  under the overlay's `choices` key and keeps every other part of the overlay, then publishes the web resource. An overlay
  that is not valid JSON, or whose `choices` is not an object, is shown as unreadable and never overwritten; the text
  colours are then disabled.

Only what changed is written. If a write fails, the server's message is shown and the dialog stays open; backgrounds
already written are published, and the table's columns are read again when the dialog closes. The `statecode` column has
no Colours button: `UpdateOptionValue` does not change the options of a status (see
[Rules for metadata writes](solutions.md#rules-for-metadata-writes)).

## Views

A table's page (`/manage/tables/{table}`) has a **Views** tab after Relationships. It lists the table's views
(`savedquery` rows whose `returnedtypecode` is the table), with their name, type (Public, Advanced find, Associated, Quick
find, Lookup, or "Other ({n})" for any other `querytype`), a check mark on the default view and a **Managed** badge for a
view that comes from a managed solution. Personal views (`userquery`) are not listed. Each row's name opens the view designer;
for the selected row the command bar offers **Set as default** (Public views that are not the default) and **Delete** (any view but the default, after
a confirmation). **New view** opens the designer on a new Public view (`querytype` 0).

The **view designer** (`/manage/tables/{table}/views/{id}`, `new` for a new view) edits the view's FetchXML and LayoutXML:

- **Properties.** Name (required, up to 200 characters) and description.
- **Columns.** A column of the table, or of a table reached through one of its lookups (one level: the related table's
  columns are added through a `<link-entity link-type="outer">` with the alias `a_{lookup}`, or through a link-entity
  the view already has for that lookup). A related column is shown as "{column} ({lookup})", for example "Full Name
  (Primary Contact)". Columns can be removed, moved left or right and given a width (25 to 1000 pixels; 100 for a new
  column, 150 for the primary name). The primary key stays in the query and is never a column; removing a column keeps its
  `<attribute>` when the filter or the sort still uses it, and a link-entity left empty is removed.
- **Sort.** Up to two orders on the table's own columns, ascending or descending. A view with a sort the designer does not
  edit (more than two orders, an order inside a link-entity, an aggregate order) shows it read-only and keeps it as it is.
- **Filter.** Groups of conditions (all of these / any of these, nested to any depth) on the table's own columns, with the
  operators that fit the column: text (equals, contains, begins with, ends with and their negations, has data, no data),
  numbers (comparisons), dates (on, on or after, on or before, today, yesterday, tomorrow, this week, month or year, last
  or next X days), choices and Yes/No (equals, is one of and their negations), lookups (equals, does not equal, and for a
  user or business unit lookup "current user" and "current business unit"). A part of the filter the designer does not
  edit (a condition on a related table, an operator not in these lists, a filter inside a link-entity) is shown read-only
  and written back exactly as it was; the parts around it stay editable.
- **Find columns.** For a quick find view (`querytype` 4): the columns the search box looks in (text, number and lookup
  columns of the table). They are kept as the view's `isquickfindfields` filter and are not shown in the filter editor.
- **Modern look.** This view's board (grouped by a choice, status or Yes/No column), calendar (a start date
  column, an optional end date column with the same date behaviour, and the **Calendar layout** it starts on: Month or
  Timeline), cards and default mode, as the modern look of the
  model-driven apps reads them (see [View modes](model-driven-apps.md#view-modes-modern-look)), and **Wrap text**: one
  switch per column of the view, off by default. A column switched on wraps its text onto more lines in the grid (and in
  the subgrids that show the view); the others stay on one line, cut with an ellipsis, as in Dataverse. A column removed
  from the view is dropped from the list when the view is saved. They are kept in a
  presentation overlay: the card offers the overlays in use, the environment's (preselected) and each app's own. With none
  in use it links to the Environment page, where one is chosen. The overlay is written only when the card changed: this
  view's entry under `views` is set (or removed when every mode is off and no column wraps) and every other part of the overlay is kept, then
  the web resource is published. An overlay that is not valid JSON, or whose `views` is not an object, is shown read-only
  and never overwritten. If the overlay cannot be saved, the view is still saved and the error is shown in the card.
- **Preview.** The first 10 rows of the view as it stands, refreshed half a second after the last change. A preview
  error is shown in the preview and does not block saving.

**Save** creates or updates the view and publishes the table (`PublishXml`); the model-driven apps show the change the
next time they load the view. **Save as** (Public views) saves a copy named "{name} (copy)" and opens it; the view modes
go with the copy. **Back** asks for confirmation when there are unsaved changes. The FetchXML and LayoutXML keep every
element and attribute the designer does not edit. Any `/manage` user can open the designer; the server decides who may
save. A view from a managed solution shows "This view comes from a managed solution: your changes go to the unmanaged
layer."; a change the server refuses (a component that is not customizable) shows the server's message. Every server
error is shown as it is. What the designer does not do is listed in [Known issues](known-issues.md#model-driven-apps).

## Forms

A table's page has a **Forms** tab after Views. It lists the table's forms (`systemform` rows whose `objecttypecode` is the
table) with their name, type (2 Main, 6 Quick view, 7 Quick create, 11 Card, 0 Dashboard, or "Other ({n})"), a check mark
on the default form, whether the form is active (an empty `formactivationstate` counts as active) and a **Managed** badge
for a form that comes from a managed solution. Each row's name opens the form designer; for the selected row the command bar offers **Set as default**
(active Main forms that are not the default; the other default Main forms are cleared), **Activate** or **Deactivate**
(not on the default form) and **Delete** (any form but the default, after a confirmation). **New form** offers a Main,
Quick view or Quick create form; each opens the designer on a new form of that type with one tab "General", one section
"General" and the table's primary name column.

The **form designer** (`/manage/tables/{table}/forms/{id}`, `new` for a new form) edits the form's FormXML. Main, Quick view
and Quick create forms are edited; a form of another type opens read-only with the notice "This form type is not edited in
the designer", and its FormXML is never touched. The **Header** and **Modern look** cards are for Main forms only: they
are not shown for Quick view and Quick create forms, which have no header at run time and no modern look layout. The
**Events** card and a field's OnChange handlers are not shown for Quick view forms, where Dataverse offers no form
libraries or events.

- **Properties.** Name (required, up to 100 characters) and description.
- **Tabs.** Add, move left or right and delete a tab (delete asks first when the tab holds anything, a field or another
  control, and is disabled for the only tab). A tab has a label, show label, visible and a layout: one column, two
  columns (50/50 or 67/33) or three. Fewer columns move the sections of the dropped columns, in order, to the end of the
  last column; nothing is lost.
- **Sections.** Add a section to a tab column, move it up, down or to the previous or next column, and delete it (asks
  first when it holds anything, a field or another control, disabled for the only section of a tab). A section has a
  label, show label, visible, 1 to 4 columns and a label position (beside the field, the default, or above it; written
  as `celllabelposition`, as Dataverse does); a changed column count re-flows its cells row by row.
- **Fields.** Add a column of the table from a picker with search (hidden, virtual and system-only columns are not offered,
  except the primary name; a column already in the body is not offered again). Move a field up or down, to the previous or
  next section, or remove it. A field has a label, show label, visible and read-only; whether the column is required is
  shown, not edited. Removing a required field, or a field with `onchange` handlers, asks first and names what is lost;
  confirming also removes that field's `onchange` handlers, those added since the form was opened included, when it was
  the column's last control on the form (body, header or footer). Adding the column back does not restore them. Timelines and IFrames (a URL) show as read-only chips that can
  be moved and removed but not created or configured; a spacer or other empty cell shows as an "Empty cell" placeholder
  that stays where it is.
- **Drag and drop.** A field or control, a section, a tab and a header field can also be dragged with the mouse. A cell
  drops before another cell or at the end of a section, a section before another section or at the end of a tab column,
  both in the tab shown; a tab drops before another tab, a header field before another header field. A line shows where the
  item will land. After the drop the moved item is selected and keeps the focus; a drop on the item's own place changes
  nothing. Moving to another tab, and between the body and the header, stays with the buttons, which are the keyboard way
  to do every move.
- **Subgrids.** **Add subgrid** (in the canvas toolbar, for the selected section or the section of the selected cell)
  asks for the records (only related records, through a one-to-many relationship where the table is the referenced side or
  a many-to-many where it is either side, or all records of any table), the table, the default view (the table's active
  public views, with its default view preselected), the records per page (1 to 250, default 4) and the label. The subgrid
  spans the section's columns and gets a unique control name (the table's name, then `_2`, `_3`…). Its properties are the
  label, show label, visible, the view, the records per page and **Show view selector**; to change the relationship or the
  table, remove it and add another.
- **Quick views.** **Add quick view** asks for a lookup column of the table, one of its tables and one of that table's
  active quick view forms. Its properties are the label, show label, visible and the form for each table of the lookup;
  another table of a polymorphic lookup can be added.
- **Web resources.** **Add web resource** lists the HTML and image web resources (PNG, JPG, GIF, ICO, SVG). Its
  properties are the label, show label, visible, the web resource, the custom parameter (`data`), pass the record's
  parameters, restrict cross-frame scripting, scrolling (auto, yes or no) and border.
- **Custom controls.** A field cell offers, in **Custom control**, the installed custom (PCF) controls with a bound
  property of the column's type; a subgrid offers the dataset controls. A control whose manifest cannot be read, or that
  the apps could not run, is not offered. Each property of the manifest takes a column of a matching type (the first one
  defaults to the cell's column) or, for an input property, a value. A property is labelled with its display name from
  the control's strings (resx), with the property's name as the hint when the two differ. Save is blocked, with a message
  naming them, until the required properties are set. While the list of installed custom controls is still loading, Save
  and Save as wait for it, with a message, because the required properties are checked against it; if the list cannot be
  read, the message shows the error with **Retry**. The designer edits the web entry; the phone and tablet entries follow
  it when they were the same, otherwise they are kept and the panel says so. **Use the standard control** removes the
  custom control. A control that is not installed, or whose manifest cannot be read, is shown read-only with the reason and
  can only be removed. A custom control that does not fit its cell (it does not take the cell's column type) is shown
  read-only with the reason "it doesn't fit this cell"; **Use the standard control** replaces it.
- **Events.** The **Events** card lists the form's libraries in their order, with Add (the JavaScript web resources),
  move up or down and Remove. Remove is refused, naming the handlers, while a handler uses the library. Under them are the
  form's OnLoad and OnSave handlers; the OnChange handlers of a field are in the field's properties. A handler has a
  library of the form, a function name (required), enabled, pass the execution context and its parameters (the
  comma-separated list as typed); handlers can be added, edited, moved and removed. An event left without handlers is
  removed. Other events (tab events, which Dataverse keeps under their tab, control events other than OnChange, events of
  the application) are counted with their names and kept as they are; a library one of their handlers uses cannot be
  removed either. OnChange handlers of a column that is no longer on the form are listed under **OnChange of columns not
  on the form**, by column, each with a **Remove** button that removes that column's handlers.
- **Rows.** The rows of a section stay as they were stored: an edit re-flows only the rows it touches (a new field goes in
  the first free slot of the last row or in a new row; removing a field shifts the later fields of its row left).
- **Header.** Up to four fields in the header, added, moved left or right and removed, kept in one row. Other header cells
  are kept.
- **Names stay.** The `name` of a tab or section is never changed or reused, because scripts and the modern look's grid
  placements refer to it; only labels are edited. A label is written for the language of the web admin (English or Italian)
  and the labels of other languages stay as they are.
- **Modern look.** This form's layout (tabs or wizard), the timeline's appearance and density (when the form has a timeline)
  the grid placement of the sections of a tab, and the background colour of each section and of its fields, as the modern
  look of the model-driven apps reads them (see [Form layouts](model-driven-apps.md#form-layouts-modern-look)). A colour
  is picked from the named swatches (they follow the light and dark theme), from the browser's colour picker (**Custom
  colour**), or removed with **No colour**; a field without its own colour shows its section's. They are kept in a
  presentation overlay, chosen and written like the view modes of a view. A placement that is not valid is shown as
  invalid and not saved; the stored one stays. Placements and colours of a section or field deleted in the designer are
  removed on save.
- **Subgrids** (in the Modern look card). One block per subgrid of the form, with its label and table, to choose which
  view modes the subgrid offers in the modern look: **Board** (the column to group by), **Calendar**, **Calendar end** and **Calendar layout**,
  **Cards** and the **Default mode**, the same fields as the view modes of a view, listing the columns of the subgrid's
  table. **Use the view's modes** (shown when the subgrid's view has view modes) copies them into the block. A subgrid
  replaced by a custom control keeps its own layout and has no fields. The setting of a subgrid deleted in the designer is
  removed on save. See [Subgrids](model-driven-apps.md#subgrids).

**Save** creates or updates the form and publishes the table (`PublishXml`); the model-driven apps show the change the next
time they load the form. **Save as** saves a copy named "{name} (copy)" with the same type and FormXML, not the default and
active, and opens it; the modern look entry goes with the copy. **Back** asks for confirmation when there are unsaved
changes. Everything the designer does not model (other events, navigation, display conditions, footer, other attributes
and elements, a control's parameters it does not edit) is kept exactly as it was: a form that is loaded and saved without
an edit is unchanged. Save as keeps the custom controls, libraries and handlers with their ids. Any
`/manage` user can open the designer; the server decides who may save. A form from a managed solution shows "This form comes
from a managed solution. Changes are saved in the unmanaged layer if the solution allows it."; every server error is shown
as it is. What the designer does not do is listed in [Known issues](known-issues.md#model-driven-apps).

## Deleting a solution in the UI

The Solutions page offers the same two options as the CLI (see [Deleting a solution](cli.md#deleting-a-solution)):

- **Delete solution removes only the solution container, as in Dataverse. Its components stay in the system, tracked by
  the Default solution.**
- **Delete solution and its components is xrmverse-only (System Administrator only): it also drops the components
  (tables and their data) that no other solution holds. It goes through the Admin API, requires typing the solution's
  unique name, and cannot be undone.**

## Development loop

```bash
pnpm -C ui install
pnpm -C ui dev        # http://localhost:5173, proxies /api, /admin and /ui/config.json to http://localhost:5000
pnpm -C ui test       # unit tests (Vitest)
pnpm -C ui typecheck
pnpm -C ui build      # writes ui/dist
pnpm -C ui e2e        # Playwright scenarios against a real host and SQL Server
```

Run the .NET host on port 5000 first (`dotnet run --project src/Xrmd.Host` with the configuration of
`docker/.env`, or the Docker stack). The host is built without the Microsoft SDK and exits with code 3 until it finds it:
install it once with `xrmd sdk install` (into `~/.xrmd/sdk`), or, as a developer, point `XRMD_SDK_DIR` at the restored
package, `~/.nuget/packages/microsoft.powerplatform.dataverse.client/1.2.27/lib/net10.0` (`NUGET_PACKAGES` if you set it).
`pnpm -C ui e2e` sets `XRMD_SDK_DIR` to that folder itself and stops with a message when it is missing (run
`dotnet restore xrmverse.sln`). `pnpm -C ui e2e` starts its own SQL Server with `docker run`, builds the UI, the host
and the fixture solution, and runs the host in development mode on a free port; it needs Docker, the .NET SDK, `zip`
and a Chromium (`XRMD_E2E_CHROMIUM`, else `/opt/pw-browsers/chromium`, else Playwright's own). See [`ui/README.md`](../ui/README.md).

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

Anything the server cannot do faithfully is reported, never emulated:

- **Business unit disable.** The UI sends `isdisabled` like Dataverse, but the server's `businessunit` table has no
  `isdisabled` column yet, so the server answers with an explicit error, which the UI shows.
- **Cascade delete preview.** The confirmation dialog lists the solution's components; which of them are kept (system
  components, components held by another solution) is only known after the delete, because the server has no dry run.
  The result then lists deleted and kept components with the reason.
- **Plug-in assemblies are registered with the CLI** (`xrmd plugin register <dll> [--solution <name>]`): the CLI
  inspects the DLL on your machine. The Plug-ins page browses assemblies, types and steps, and enables or disables steps; it lists plug-in steps only, so a webhook and its steps are managed with `xrmd webhook`, the SDK or the Web API (see [Webhooks](webhooks.md)). Its **Custom APIs** tab lists the custom APIs read-only (binding, kind, private, plug-in type, parameters and properties); it does not create or change them: use `xrmd customapi create`, the SDK or the Web API, or import a solution (see [Custom APIs](custom-apis.md#authoring)).
- **New schema names.** When you create a table or column the UI suggests `<default publisher prefix>_<PascalCase
  display name>` (for example `xv_ProjectTask`); you can edit it. The logical name is the lowercased schema name. If the
  default publisher has no prefix, no name is suggested.
- **Server gaps the UI works around.** `EntityDefinitions` does not expand relationships, so the UI reads
  `RelationshipDefinitions` to find the navigation property of a lookup. A polymorphic lookup accepts one navigation name
  per attribute on the server; the UI binds with that name and never guesses one.

## See also

- [Model-driven apps](model-driven-apps.md)
- [Admin API](admin-api.md)
- [CLI](cli.md)
- [Security](security.md)
- [Solutions](solutions.md)
