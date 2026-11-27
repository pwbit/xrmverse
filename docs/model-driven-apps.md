# Model-driven apps

[← xrmverse documentation](../README.md#documentation)

## Introduction

xrmverse opens the model-driven apps of an imported solution in its own app shell: an app picker, the app's site map as
navigation, views, forms, dashboards, web resources, form scripts and PCF controls. The shell is part of the same
single-page app as the [web admin](web-admin.md) (`ui/`), uses the same sign-in, and reads everything through the public
Web API, so security, field security and the query engine apply as for any other client.

The classic renderer (stage **UI-1**: shell and views, forms, web resources and form scripts, field and dataset PCF
controls, custom commands, dashboards and charts, timeline) is complete. On top of it, an app can use a **modern look**
with its own theme, view modes and form layouts (stage **UI-2**). See [Stages](#stages). Whatever is not delivered opens a
placeholder or a notice that says so, never a blank page, and a Client API member that is not implemented throws an error
that names the member.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Apps, site map, views | opened from the imported `AppModule`, trimmed by the user's roles and privileges; a command bar with multiple row selection on every list | Same behaviour; own xrmverse look |
| Forms, commands, Assign and Share | tabs, sections, standard controls, subgrids, quick views, standard and custom commands | Same idea; see the limits per stage |
| Web resources, form scripts, Client API | served from the organization, OnLoad, OnSave, OnChange, `Xrm.*` | Narrower: unimplemented members throw an error naming them |
| PCF controls | field and dataset controls with React and Fluent UI as platform libraries | Same idea |
| Dashboards and charts | system dashboards and charts, drawn with Fluent UI React Charts | Differs: own chart drawing |
| Timeline | activities, notes and, when switched on, posts | Same idea; own look |
| Queues | **Add to queue** on lists of queue-enabled tables; **Pick**, **Release**, **Route** and **Remove from queue** on the queue item grid | Same idea; own look (see [Email and queues](email-and-queues.md)) |
| Email form | editable party lists, **Send**, the send status, attachments with a preview of PDF files, images and plain text | Same idea; own look (see [Email form](#email-form)) |
| Modern look | presentation overlay, board, calendar, cards, wizard, free grid, coloured choice values | Extra; the background colour of a choice value is Dataverse's own `Color` |
| `XrmVerse`, app side panes, app notifications, app OnLoad | a client extension, panes beside the page, app-wide notifications and a `xrmverse_AppOnLoad` setting | Extra; not importable into Dataverse (see [xrmverse extensions](#xrmverse-extensions)) |
| Business process flows, client-side business rules, editable grids, offline, canvas apps, custom pages | not available | Narrower |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Open an app | `/` or `/apps`, then a banner tile | [URLs](#urls) |
| Run a command on several records | tick the rows, then the view's command bar | [Views](#views) |
| Give users an app | share it with a security role | [Who sees which app](#who-sees-which-app) |
| Switch an app to the modern look | app settings, environment default or the user's Personalization | [Setting the look](#setting-the-look) |
| Show a view as a board, calendar or cards | the overlay's `views` key | [View modes](#view-modes-modern-look) |
| Use a wizard or a free grid on a form | the overlay's `forms` key | [Form layouts](#form-layouts-modern-look) |
| Show a subgrid as a board, calendar or cards | the overlay's `forms` key (`subgrids`) | [Subgrids](#subgrids) |
| Show choice values as coloured badges | the web admin's **Colours** dialog | [Choice colours](#choice-colours-modern-look) |
| Run form scripts | web resources and form events | [Form scripts](#form-scripts) |
| Use PCF controls | controls in the solution | [PCF controls](#pcf-controls) |
| Open dashboards | the site map | [Dashboards and charts](#dashboards-and-charts) |
| Turn posts on | the posts app setting | [Posts](#posts) |
| Keep a console beside the page | `XrmVerse.App.sidePanes` and the `xrmverse_AppOnLoad` setting | [xrmverse extensions](#xrmverse-extensions) |

## What the app shell offers

- **Apps from solutions.** Importing a solution that contains an `AppModule` fills `appmodule` (client type, form factor,
  navigation type, web resource, version, publisher and the other standard columns), `appmodulecomponent` (the app's
  tables, forms, views, charts and its site map), `appmoduleroles` (the security roles the app is shared with) and
  `appsetting` (the app's `<appsettings>`, of the definitions the organization has). A re-import replaces the components,
  roles and settings with those of the file. The export round trip of `<AppModule>` is unchanged.
- **App picker** at `/apps`: a card per active app the user may open, sorted by name. Each card shows a banner with the
  xrmverse mark, then, centred, the app's name on one line (cut with an ellipsis when too long, the full name as its
  tooltip) and up to two lines of its description; the whole card opens the app. The banner is a gradient of the brand
  colours of the theme in force on `/apps`: the environment's presentation overlay (brand colours, font, density and
  colour scheme, as the web admin applies it; `whiteLabel` does not apply), else the xrmverse theme in the user's light or
  dark colour scheme. The overlay is read for every user whose roles can read `organizationsetting`; otherwise the page
  keeps the xrmverse theme. The [workflow studio](workflow-studio.md) follows the same theme. A second list, **Tools**, holds the
  [workflow studio](workflow-studio.md) tile for users who can read processes. The app's own icon
  (`appmodule.webresourceid`) is not shown yet.
- **App shell** at `/main.aspx`: a header with the xrmverse logo and name, the app's name, the user and Sign out; the
  site map on the left; the page in the middle.
- **Site map navigation**: areas (an area switcher when there are several), groups and subareas, trimmed by the user's
  privileges.
  A button in the header collapses it (see [The navigation toggle](#the-navigation-toggle)).
- **Views**: the public and personal views of a table, a view selector, the view's columns and widths, its order, a
  header-click sort, paging with first, previous and next, and quick find.
- **Server messages and tables** the shell uses: `RetrieveUserPrivileges` (SDK, SOAP and the Web API bound function
  `systemusers({id})/Microsoft.Dynamics.CRM.RetrieveUserPrivileges()`), the `userquery` table (personal views), the
  `userentityuisettings` table (the last form used), and
  `usersettings.uilanguageid` and `usersettings.paginglimit`.

## URLs

| URL | What it opens |
|---|---|
| `/` | Redirects to `/apps`. |
| `/apps` | The app picker. |
| `/main.aspx?appid={id}` | The app's home: the first subarea of the site map that the user may see and that opens inside the app. |
| `/main.aspx?appid={id}&pagetype=entitylist&etn={logicalname}` | The table's list, in its default view. |
| `…&pagetype=entitylist&etn={logicalname}&viewid={id}&viewType=1039` | A public view (`savedquery`). |
| `…&pagetype=entitylist&etn={logicalname}&viewid={id}&viewType=4230` | A personal view (`userquery`). |
| `…&pagetype=entityrecord&etn={logicalname}&id={id}[&formid={id}]` | A record, on its main form (see [Forms](#forms)). Without `id` the form opens in create mode. |
| `…&pagetype=dashboard[&id={id}][&type=system]` | A system dashboard (see [Dashboards and charts](#dashboards-and-charts)). Without `id`, the app's first dashboard. `type` is ignored. |
| `…&pagetype=webresource&webresourceName={name}[&data={data}]` | An HTML web resource, full size inside the app (see [HTML web resources](#html-web-resources)). |
| `…&pagetype=custom&name={name}` | A custom page: placeholder "Custom pages are not planned." |

`main.aspx` and its parameters are the documented URL interface of model-driven apps, so a bookmark or a link from an
email keeps working after a migration. Parameter names are read in any case, GUIDs with or without braces (`{…}` or
`%7b…%7d`). `/main.aspx` without `appid` goes to `/apps`. An app that does not exist, or that the user may not open,
shows "This app does not exist or you do not have access to it." with a link to `/apps`. Any other `pagetype` shows
`Pages of type "{pageType}" are not available in this version.` A subarea with an external URL opens in a new tab.

## Who sees which app

- A **System Administrator** sees every app.
- Any other user sees an app when one of the app's roles (`appmoduleroles`) is among the user's roles, assigned directly
  or through a team the user belongs to.
- An app shared with **no role** is shown to System Administrators only.
- A user with no app is told "No apps are available to you. Ask an administrator to give you a security role of an app."

The shell reads `appmodules`, the caller's `systemuserroles`, `teammembership` and `teamroles`, the `roles` (to recognise
System Administrator by its role template) and the `appmoduleroles` of the caller's roles, and joins them in the browser;
then the `settingdefinitions` and `appsettings` of the apps it lists. Every user can read these: Basic User has Read on
`appmodule`, `appmodulecomponent`, `sitemap`, `savedquery`, `settingdefinition` and `appsetting`, and the
intersect tables are readable by every member. Opening an app is not a security boundary of its own: the data behind the
app is protected by the user's roles, as in Dataverse.

## Site map trimming

The site map comes from the app's `sitemap` row (`sitemapxml`); the user's privileges come from `RetrieveUserPrivileges`.

- A subarea with an `Entity` is shown when the user holds `prvRead{SchemaName}` of that table at any depth. For an
  activity table (`email`, `task`, `phonecall`, `appointment` and the other tables with `IsActivity`), the shared
  `prvReadActivity` is enough, as in Dataverse.
- Every `<Privilege Entity="…" Privilege="Read,Write"/>` child of a subarea must hold too (`prvRead…` and `prvWrite…`).
- A subarea of a table the organization does not have is hidden.
- A group, or an area, left empty is hidden.
- Subareas without an `Entity` (a dashboard, a web resource, an external URL) are shown, and open their page or
  URL. A URL opens only when it is `http:`, `https:` or relative to the site; any other scheme (`javascript:`, `data:`,
  …) is shown disabled, never as a link.

The selected subarea is marked (`aria-current="page"`); a record page marks its table's list.

## Views

Public views, and their view modes in the modern look, are edited in the web admin's view designer (see
[Views](web-admin.md#views)); an app shows a saved view the next time it loads it.

The list of a table (`pagetype=entitylist`) shows:

- **The view selector.** The public views of the table (`savedquery`, `querytype` 0, active), sorted by name, then a
  separator and the user's personal views (`userquery`, `querytype` 0, active), sorted by name. A user without
  `prvReadUserQuery` sees the public views only.
- **The view that opens.** The URL's `viewid` when it is one of the listed views; else the view marked default
  (`isdefault`); else the first public view by name; else the first personal view. Choosing another view puts its id and
  `viewType` in the URL and starts it over: page 1, its own order, no search.
- **The view's header.** One card on two rows. The first row has **Back** (when the list was opened from another page
  of the app), the table's name in small type over the view selector, then **Show as** (the view's modes, modern look),
  the commands, **Share** and **View actions** as icons. The second row has the search box.
- **View actions** (#101), the icon at the end of the header. "Save as new view" creates a personal view (`userquery`, `querytype` 0) with the open view's
  query, the current sort and no paging, and its `LayoutXml` as it is, then opens it. On a personal view the user owns,
  "Save view" saves the query and layout over it, "Rename view" changes its name, and "Delete view" deletes it after a
  confirmation and opens the default view. A quick find search is not saved.
- **The grid.** The `LayoutXml` `<cell name width>` columns in their order, widths in pixels; hidden cells are not drawn.
  Number, currency and date columns, headers included, are aligned to the end of the cell, and a currency value shows a
  space between its symbol and its digits (`€ 420,000.00`).
  As in Dataverse, every cell stays on one line: a longer text is cut with an ellipsis and shows whole in the cell's
  tooltip. In the modern look a view can let chosen columns wrap onto more lines (the overlay's `wrap`, set in the view
  designer); subgrids and dashboard grids that show the view follow it.
  A cell named `alias.attr` reads the column of that link-entity. The header label is the column's display label in the
  user's language; a related table's column adds, in brackets, the display label of the lookup it follows (an N:1 link,
  e.g. "Email (Primary Contact)") or else the related table's display name. Values are the server's formatted values; a lookup shows the related record's name, never its GUID.
  A column hidden by [field-level security](field-security.md) is empty.
- **Order.** The view's FetchXML `<order>`, the root's first and then those inside each link-entity. A header click sorts
  by that column ascending, a second click descending; the grid goes back to page 1. A related table's column
  (`alias.attr`) is sorted by an `<order>` inside its link-entity. As in Dataverse, a lookup column sorts by the related record's primary name, and a choice
  (or Yes/No) column by its label, unless the view's FetchXML sets `useraworderby="true"`.
- **Paging.** The page size is the user's `usersettings.paginglimit`: 25, 50, 75, 100 or 250 (any other value, or none,
  gives 50). An organization upgraded from a version without the column gets 50 for every user once; a value cleared
  afterwards stays cleared (and pages by 50). Paging uses the FetchXML paging cookie, with first, previous and next. The range reads "1 - 50 of 213" when
  the server returns the total, and "1 - 50" otherwise.
- **Scrolling.** The app fills the window: the header and the navigation stay, and the page scrolls inside the main area.
  On a list, the view's header card and the paging stay too, and only the grid's rows scroll, both ways, with the
  column headers kept at the top. The grid and its paging sit in a card of their own, with the column titles in bold.
  On a form, the command bar and the header card stay, and only the form's content scrolls. The board, the calendar and
  the subgrids of a form scroll with the page.
- **A row click** (or Enter on the first cell's link) opens the record URL (see [Forms](#forms)).
- **The command bar** in the view's header has the same look as the form's (icons with labels, the commands that do not
  fit move into a **More commands** menu): **New**, **Delete**, **Activate**, **Deactivate**, **Refresh** and **Assign**,
  then **Share** as an icon. New follows the create privilege and Delete the delete privilege; Delete asks before it
  deletes the selected records. Activate and Deactivate exist on tables whose `statecode` has at least two states, and
  Assign and Share on user- or team-owned tables. Refresh reads the page again (grid and cards).
- **Selecting rows.** A checkbox column comes first: a row checkbox selects the row (a click on the row still opens the
  record), and the header checkbox selects or clears the page, with a mixed state for a partial page. The cards of the
  modern look have a checkbox each and a page checkbox. The selection belongs to the page shown: it is cleared when the
  view, the quick find search, the sort, the page or the mode changes. The board and the calendar have no selection, and
  their bar shows New only.
- **Commands on the selection.** A command is disabled, with its reason as a tooltip, when nothing is selected. Activate is
  enabled when every selected record is inactive and Deactivate when every one is active; a mixed selection disables both
  ("mixed states"). Assign and Share are enabled for one record or more. A command opens the form's dialog (with the number
  of records in its title), then runs **one request per record, in order**: a failure does not stop the others. The result
  appears above the grid as "{done} of {total} done" with one line per failure ("{name}: {server message}"), and the page
  reloads. The view does not check the rights record by record: a record the user may not change fails in that list.
  **Share** on several records starts on the add tab and grants the chosen rights on each record (`GrantAccess`); there is
  no current-shares tab (see [Known limits](#known-limits)).

The rows come from `GET {entityset}?fetchXml=…` with `Prefer: odata.include-annotations="*"`.

## Quick find

The search box ("Search this view") uses the table's quick find view (`querytype` 4: the default one, else the first by
name). Enter runs it; clearing the box, or Enter on an empty box, goes back to the selected view.

- The conditions of the filters marked `isquickfindfields="1"` get the search text. For `like` and `not-like`, `%` is
  appended unless the text ends with `*`, and a leading `*` becomes `%` (a "contains" search). `begins-with` and
  `not-begin-with` get the text as typed, so a `*` there is matched literally (Dataverse treats `*` as a wildcard in
  quick find; see Known limits). Numeric operators get the text when it is a number, and are dropped otherwise.
- A `like`, `not-like`, `begins-with`, `not-begin-with`, `ends-with` or `not-end-with` condition on a lookup column
  matches the related record's primary name, as in Dataverse (for a Customer or Owner lookup, the name of whichever table
  the row points to). A row with an empty lookup matches neither `like` nor `not-like`.
- The result shows the quick find view's columns, with its own sort and paging.
- A table without a quick find view has no search box.

## Languages

Labels follow the user's `usersettings.uilanguageid`:

- Site map titles use the `<Title LCID>` of that language, else 1033, else the first title; a subarea without a title
  shows its `Id`.
- Column headers use the display label in that language, else 1033.
- The shell's own strings ship in English and Italian (`1040`); any other language shows English.

The language applies to the app picker and the app shell only. The [web admin](web-admin.md) under `/manage` stays in
English, and the shell strings go back to English when the user leaves the app shell.

## The shell's look

The shell uses Fluent UI React (MIT) with Fluent icons, the xrmverse brand theme in light and dark (see [the web admin's look and feel](web-admin.md#look-and-feel)) and the Sora brand font (system font stack as fallback). The
header shows the xrmverse logo and name (in the modern look, an overlay can replace them: see
[Looks: classic and modern](#looks-classic-and-modern)). Nothing is taken from Power Apps: no script, style sheet, image, icon or font.
The layout is the familiar one (site map on the left, grid in the middle); the identity is xrmverse's. The web admin uses
the same theme.

## Looks: classic and modern

Since UI-2a (item 3.6) an app can be shown in one of two **looks**. Both read the same app, site map, views, forms and
dashboards; neither changes them.

- **Classic** is the look of UI-1 and the default: the xrmverse brand bar, the site map on the left, the page in the
  middle. A classic app ignores any overlay that is configured. Since X.10 its header has a button that collapses and shows
  the site map (see [The navigation toggle](#the-navigation-toggle)); otherwise it looks as it did before UI-2a.
- **Modern** draws its own app frame: a header in the overlay's brand colour (as the web admin's brand bar) with a logo and a name, the site map as a navigation rail that a
  header button collapses and expands (the browser remembers whether it is open; see
  [The navigation toggle](#the-navigation-toggle)), and the theme of the app's **presentation overlay** (brand colours, font, density,
  colour scheme and white-label). Icons are Fluent's filled icons, in menus, dialogs and popups too (classic shows the
  regular ones, filled on hover). A view can also be shown as a board, a calendar or cards (see
  [View modes](#view-modes-modern-look)); a main form can use the wizard, the free grid and the timeline restyle (see
  [Form layouts](#form-layouts-modern-look)); dashboards inside the frame are the classic ones.

### Which look an app uses

The look comes from the setting definition `xrmverse_Renderer` (default `classic`), the first one set of:

1. the user's own choice (`usersettings.xrmverse_renderer`, set from the Personalization dialog);
2. the app's value (an `appsetting` row of the app);
3. the environment's value (an `organizationsetting` row, set in the web admin);
4. the definition's default, `classic`.

An empty value at any level counts as not set. A look id other than `classic` or `modern` opens the app in the classic
look with the notice "The look '{id}' is not available in this version; the classic look is used."

The overlay follows the same order without the user level: the app's value of `xrmverse_Presentation`, else the
environment's, else none. Its value is the name of a web resource. The classic look ignores it.

#### Colour scheme

The user's choice (`usersettings.xrmverse_colorscheme`: System, Light or Dark) wins unless it is
System; then, in the modern look only, the overlay's `theme.colorScheme`; then the operating system's setting. The
user's choice applies to the classic look too.

### The presentation overlay (version 1)

The overlay is a JSON document kept in a web resource of a text type (script, HTML, CSS, XML or SVG; a script web
resource named `….json` is the usual choice). The shell reads it from `/WebResources/{name}` with the user's session,
so the user needs read access to web resources, as for any form script.

```json
{
  "version": 1,
  "theme": {
    "brandRamp": {
      "10": "#031a1c", "20": "#052829", "30": "#073637", "40": "#094444",
      "50": "#0c5352", "60": "#0e615f", "70": "#106f6d", "80": "#127d7a",
      "90": "#2d8c8a", "100": "#489c99", "110": "#62aba8", "120": "#7dbab8",
      "130": "#98c9c8", "140": "#b2d8d7", "150": "#cde8e6", "160": "#e8f7f6"
    },
    "fontFamily": "Georgia",
    "density": "comfortable",
    "colorScheme": "system"
  },
  "whiteLabel": { "name": "Harbor Desk", "logo": "lk_/looks/logo.svg", "favicon": "lk_/looks/favicon.ico" }
}
```

| Key | Meaning |
|---|---|
| `version` | Must be `1`. Any other value: the whole overlay is ignored, with the notice "This presentation uses version {v}, which this version does not read." |
| `theme.brandRamp` | The 16 shades of the brand colour, keys `10` to `160` (dark to light), each `#rrggbb`. All 16 or none: an incomplete or malformed ramp is dropped with a console warning and the xrmverse ramp is used. In the light scheme the main brand colour is shade `80`, in the dark scheme shade `70`; links and brand text in the dark scheme use shades `110` and `120`. |
| `theme.fontFamily` | The name of one font family, put in front of the xrmverse font stack. The font must be installed on the user's device or loaded by the page; the overlay does not download fonts. Regular text uses weight 300 with this font too; a font without a light weight shows its nearest weight. |
| `theme.density` | `comfortable` (default) or `compact`. Compact scales the spacing by 0.75 (rounded to whole pixels); font sizes stay. |
| `theme.colorScheme` | `system` (default), `light` or `dark`; see the colour scheme order above. |
| `whiteLabel.name` | Replaces the app's name in the header, in the navigation's label and in the browser tab title. |
| `whiteLabel.logo` | A web resource (an image) shown 24 px high in the header in place of the xrmverse logo, with the name as its alternative text. |
| `whiteLabel.favicon` | A web resource used as the page icon while the app is open; the previous icon comes back when the user leaves the app. |
| `views` | Per-view modes, keyed by the id of a public or personal view (`savedqueryid` or `userqueryid`; case and braces do not matter). Each entry may have `kanban` (`{ "groupBy": "<column>" }`), `calendar` (`{ "start": "<column>", "end": "<column>", "layout": "month" }`, `end` optional; `layout` optional, `month` or `timeline`, `month` when absent; another value is ignored with a console warning), `cards` (`{}`), `default` (`grid`, `kanban`, `calendar` or `cards`; a mode that is not configured is ignored with a console warning) and `wrap` (a list of the view's cell names, as in its LayoutXml, e.g. `["title", "pc.fullname"]`: those columns wrap onto more lines in the grid, every other one stays on one line with an ellipsis; case does not matter; a value that is not a name is ignored with a console warning). A mode whose column does not fit is not offered, with a console warning. A subgrid's entry under `forms` has no `wrap`: a subgrid wraps the columns its view's entry names. See [View modes](#view-modes-modern-look). |
| `forms` | Per-form layouts, keyed by the id of a main form (`systemformid`; case and braces do not matter). Each entry may have `layout` (`tabs`, the default, or `wizard`), `grid` (per tab name, per section name, `{ "col": 1–12, "span": 1–12, "row": ≥1, "rowSpan": ≥1 }`; `span` and `rowSpan` optional, default 1) `timeline` (`{ "appearance": "list" or "cards", "density": "comfortable" or "compact" }`), `background` (`{ "sections": { "<section name>": "<colour>" }, "fields": { "<column>": "<colour>" } }`) and `subgrids` (per subgrid control id, case does not matter, an entry shaped like a `views` entry). `grid` combines with either layout. See [Form layouts](#form-layouts-modern-look) and [Subgrids](#subgrids). |
| `choices` | Text colours of choice values, keyed by the name of a global choice or by `table.column` for a column's own choice (case does not matter), then by the option's value: `{ "text": "#RRGGBB" }`. A key that is not a whole number or a colour that is not `#RRGGBB` gives a console warning and is ignored. See [Choice colours](#choice-colours-modern-look). |

Any other key, at the top level or inside `theme`, `whiteLabel`, a `views` entry, a `forms` entry and an entry of a form's `subgrids`, gives a console warning and is ignored. When the web
resource is missing, cannot be read or is not JSON, the app opens in the modern look with the default theme and the
notice "The presentation {name} could not be read: {reason}. The default look is used."; the app stays usable.

The PCF controls of a modern app get the overlay's theme too: `context.fluentDesignLanguage.tokenTheme` and
`isDarkTheme` follow the theme in force.

The web admin of a System Administrator applies the theme of the environment's overlay too (brand colours, font, density and colour scheme); `whiteLabel` does not apply there, and the admin stays xrmverse branded. A System Customizer's admin keeps the xrmverse theme, because only System Administrators read the environment values (see [Look and feel](web-admin.md#look-and-feel)).

### Setting the look

- **For an app:** add the app settings `xrmverse_Renderer` (`modern`) and `xrmverse_Presentation` (the overlay's web
  resource name) to the app, in the app's `<appsettings>` in the solution or as `appsetting` rows through the Web API.
  They travel with the app in the solution, and the overlay web resource travels as a component of the solution (see
  [Solutions](solutions.md)).
- **For the environment:** in the web admin, **Environment > Model-driven apps look** sets the default look and the
  presentation web resource for every app that does not set its own. The values are `organizationsetting` rows (see
  [System tables](system-tables.md)); only System Administrators and System Customizers can change
  them, and they are not exported in solutions.
- **For a user:** the user menu in the header (the user's name) opens **Personalization**, with **Look** (App default,
  Classic, Modern), **Colour scheme** (System, Light, Dark) and the date and time formats (see
  [Choosing the date and time format](#choosing-the-date-and-time-format)). Saving writes the user's own `usersettings` row and
  re-opens the app in the chosen look; if a form has unsaved changes, the usual discard prompt asks first. Any user can
  change their own row; App default clears the choice.

### View modes (modern look)

Since UI-2b, in the modern look, the overlay's `views` key can show a view in more ways than the grid. The modes a view
offers are listed in the **Show as** menu of the view's header, which names the current one (**Grid**, **Board**,
**Calendar**, **Cards**, in this order, only those that are configured and usable); a view with the grid only shows no
menu.

```json
{
  "version": 1,
  "views": {
    "{5B1E7C20-3D4F-4A61-9B80-2C3D4E5F6A01}": {
      "default": "kanban",
      "kanban": { "groupBy": "statuscode" },
      "calendar": { "start": "createdon" },
      "cards": {}
    }
  }
}
```

The column names are checked against the table's metadata when the view opens. A column that does not exist or has the
wrong type gives a console warning (prefix `[xrmverse presentation]`) and the mode is not offered; the view opens as
usual. When the metadata of a column cannot be read, the column counts as unusable in the same way.

#### Mode state

The view opens in the overlay's `default` mode (else the grid). Changing the view starts again from that
view's default. The mode is not in the URL (Dataverse has no such parameter) and is not remembered. Quick find and the
view's filters apply in every mode.

#### Board (`kanban`)

One column per option of `groupBy`, which must be a choice (local or global), the status reason
(`statuscode`), the status (`statecode`) or a yes/no column; a multi-select choice, a lookup or a text column is not
usable. Each card shows the view's first column (a link that opens the record) and up to three more values. A card moves
by drag and drop (desktop) or with its **Move to…** menu, which lists the other options and, while the **(none)** column is
shown, **(none)** (keyboard and touch). A move saves
at once with `PATCH` on the record, sent with `If-Match: *` so that a record deleted meanwhile answers 404 (and the card
goes back) instead of being created again:

- the body is `{ "<groupBy>": <value> }`;
- for `statuscode`, the body also carries the `statecode` of the target status when it differs from the record's state
  (moving an account to **Inactive** sends `statuscode` 2 and `statecode` 1);
- for `statecode`, only `statecode` is sent and the server picks the default status of that state;
- a drop on **(none)** sends `null`.

When the server refuses the move (no write privilege, a status transition that is not allowed), the card goes back to
its column and the server's message is shown. Records whose value is empty sit in a **(none)** column; records whose value
is not one of the options (an option removed later) sit in an **Other** column, which accepts no drop. Neither column is
shown when empty.

#### Calendar

A calendar of one month on `start`, which must be a date column. A record's days are read in the user's time zone (see
[User time zone and date formats](#user-time-zone-and-date-formats)); a date-only column is placed by its date and never
shifted, and a Time Zone Independent column is placed by its stored date and time as they are (read as UTC text, not
shifted into the user's zone). With `end` (also a date column with the same date behaviour as `start`; otherwise it is
ignored with a warning), a record runs from its start day to its end day; without `end`, or when the end is before the
start, it is one day long.

Each record is one continuous bar from its start to its end, with its title and `start – end · N d` (for example
`Nov 3 – Nov 26 · 24 d`). A bar is a button that opens the record; its accessible name is `title, start – end, N days`.
Bars take their colour from the 9 named colours, in turn (they follow the dark theme). The bar has a rounded edge and a
coloured marker on the day where the record really starts, a rounded edge where it really ends, `◀` when it started
before the shown days and `▶` when it ends after them.

The calendar has two layouts, chosen with the **Month** | **Timeline** toggle in its toolbar. It starts on the overlay's
`layout` (`month` when absent); the user's choice is not saved: it lasts until another view is opened or the list moves to the grid or the cards.

- **Month** (`month`): a grid of six weeks. A record that crosses weeks is cut into one bar piece per week row; its title
  is written once, on the first piece. The bars are sorted by start (longer first on the same day), and each takes the
  first free line of the week rows and keeps it in every week, so a bar can be followed down the weeks. A week row shows at
  most 4 lines of bars; the records beyond are counted on each of their days as `+N more`, which opens a list of them. The
  week starts on Sunday when the user's language is English (United States, 1033), otherwise on Monday.
- **Timeline** (`timeline`): the days of the month as columns (weekday initial and day number; weekends and today
  shaded) and one row per record that meets the month, sorted by start. The first column shows the record's title (a link
  that opens it) and `start – end · N d`; the bar spans the record's days and shows `N d`. The title column stays in view
  when the days scroll sideways on a narrow screen.

The request asks only for the records of the six-week grid: for a date and time column with `ge`/`le` on the UTC instants
of the grid's first and last day in the user's zone, for a date-only column with `on-or-after`/`on-or-before`; with
`end`, a record that starts before the grid and ends in it or after it is read too. Records without a start date are counted by a
separate request and shown as "Without a date, not shown: {count}". Previous, next and **Today** move between months; the
calendar has no drag.

#### Cards (`cards`)

The view's page as cards: the first visible column is the title (a link that opens the record), the
next four columns are `header: value` lines, empty values left out. **Sort by** sorts on a visible column (again to
reverse); paging is the grid's paging. On a narrow screen (640 px wide or less) the grid of the modern look is shown as
cards too, whether or not the view configures `cards`.

#### Record limit

The board and the calendar read at most 250 records in one request, with no paging. When the view has
more, the notice "Showing the first 250 records. Narrow the view or use quick find to see the rest." is shown. A
subgrid, which has no quick find, says "Showing the first 250 records. Use the grid to see the rest." instead.

#### Subgrids

A subgrid on a main form can offer the same modes. They are set per form and per subgrid, in the form's entry of the
`forms` key: `subgrids` maps the id of the subgrid's control in the FormXml (`<control id="…">`, case does not matter) to an
entry shaped exactly like a `views` entry. The same Contacts subgrid can be a board on one account form and only a grid on
another.

```json
{
  "version": 1,
  "forms": {
    "{5B1E7C20-3D4F-4A61-9B80-2C3D4E5F6A03}": {
      "subgrids": {
        "Contacts": { "default": "kanban", "kanban": { "groupBy": "statuscode" }, "calendar": { "start": "birthdate" }, "cards": {} }
      }
    }
  }
}
```

- The columns are columns of the subgrid's **target table** (here `contact`), checked as for a view: a column that does
  not fit gives a console warning and its mode is not offered.
- The **Grid**, **Board**, **Calendar** and **Cards** tabs sit at the end of the subgrid's title line, before the
  **Commands** drop-down (**New** and **Add Existing**). The subgrid opens in the entry's `default` mode; the mode is not in the URL and is not remembered: the
  **Refresh** command and reopening the form go back to the default mode.
- Every mode lists the same records: the subgrid's view, its relationship to the form's record and the user's privileges.
  The grid and the cards keep the subgrid's paging (`RecordsPerPage`); the board and the calendar read at most 250 records
  in one request and say so when there are more (see [Record limit](#record-limit)). A board move saves at once, as in a
  view; a refused move puts the card back and shows the server's message. Records added with **Add Existing** appear on
  the board or the calendar at once.
- The calendar's layout applies to subgrids too: the same `layout` key (`month` or `timeline`, `month` when absent) sets
  the layout it opens in, and the **Month** | **Timeline** toggle in its toolbar switches it; the user's choice is not
  saved. The form designer's Subgrids block offers it as **Calendar layout**, as the view designer does.
- An entry whose key is not the id of a subgrid of the form is ignored with a console warning.
- These stay grids and never read `subgrids`: the classic look, the items of the **Related** tab (associated views),
  subgrids on dashboards and subgrids inside a quick view form. A subgrid replaced by a dataset PCF control keeps the
  control; its entry is ignored with a console warning.

The web admin's form designer edits these entries in the **Modern look** card (see [Forms](web-admin.md#forms)).

#### Classic look

The classic look never reads `views`: every list is a grid, with no tabs.

### Known limits of the view modes (UI-2b)

- **Not scheduled:** creating a record from a board column or a calendar day, drag on the calendar, week and day
  calendar views, colouring the calendar's bars by a choice column, swimlanes, totals per board column, and view modes
  on associated views (the **Related** tab), dashboards and subgrids inside quick view forms (they stay grids).
- **250 records** on the board and the calendar, as above.


### Form layouts (modern look)

Since UI-2c, in the modern look, the overlay's `forms` key changes how a main form is laid out on the record page. The
FormXml is never changed: the same tabs, sections, fields, scripts and commands are there, only drawn differently. Quick
create, quick view and dashboard forms are never affected.

```json
{
  "version": 1,
  "forms": {
    "{5B1E7C20-3D4F-4A61-9B80-2C3D4E5F6B01}": {
      "layout": "wizard",
      "grid": {
        "general": {
          "identity": { "col": 1, "span": 6, "row": 1 },
          "contact": { "col": 7, "span": 6, "row": 1 },
          "activity": { "col": 1, "span": 12, "row": 2 }
        }
      },
      "timeline": { "appearance": "cards", "density": "comfortable" },
      "background": { "sections": { "contact": "yellow" }, "fields": { "telephone1": "#c7e8ff" } }
    }
  }
}
```

#### Wizard (`layout: "wizard"`)

Only when the form creates a record (no record id yet); an existing record shows the
normal tabs. The steps are the form's visible tabs, in FormXml order, and follow the scripts live: a script that hides a
tab removes its step, and when it hides the current step the user moves to the nearest visible step. The Related tab is
never a step: it becomes a **Related** link after the steps, and its page has a way back to the form, on the same step.
Above the step the form shows "Step {n} of {total}: {tab label}" and the step labels as tabs, which only go
back. **Next** runs the form's own required check on the current step (the visible, enabled fields of its visible
sections): while one is empty the step stays and the field shows its error. **Back** never checks. The last step shows
the form's **Save**, which is the same save as the command bar's (the required check of the whole form, OnSave, the
server's errors) with the same rules: hidden without the privilege to create, disabled while nothing changed or a save
runs. When the wizard's Save fails the required check on a field of an earlier step, the wizard goes to the first step
with a field in error. The command bar keeps working on every step (its Save leaves the step where it is). A script's `tab.setFocus()` moves to that tab's step.

#### Free grid (`grid`)

For each tab named in `grid`, the tab's sections are placed on a 12-column grid, ignoring the
FormXml column split of that tab: `col` is the first column, `span` the number of columns, `row` sets the order of the
rows (sections with the same `row` sit side by side) and `rowSpan` spans rows. Gaps left by unused row numbers or by
sections a script hides collapse. A `col + span - 1` past 12 shortens `span`, and a number that is not a whole number or
is out of range is brought into range; both with a console warning. The tab's visible sections that are not placed
follow after the last row, full width, in FormXml order. An unknown tab or section name gives a console warning and is
ignored. On a narrow screen (640 px wide or less) the tab is one column, the sections ordered by row, then column. Tabs
not named in `grid`, the header and the footer keep their classic layout.

#### Timeline (`timeline`)

`appearance: "cards"` draws each item of the form's timelines as a card with space around it;
`density: "compact"` uses less padding (items start collapsed, as always). The data, the filters, the actions and the
commands are the same. Without the key the timeline is unchanged.

#### Background colours (`background`)

Highlights sections and fields with a background colour. `sections` colours a section by its `name`: the whole
section card, so its fields show the same colour. `fields` colours a field by the column's logical name, in every section
of the form: the colour fills the field's control box (the input, the drop-down, the lookup or party list box), while its
label stays on the section's background. Multiple-choice (check box group), file and image fields have no such box and are
not coloured. A colour is one of the names `yellow`, `orange`, `red`, `pink`,
`purple`, `blue`, `teal`, `green` and `gray`, which follow the light or dark theme (a soft tint in light, a deep shade
in dark), or a free colour as `#rrggbb`, which is the same in both themes (pick one that reads well in both). An unknown
colour, or a name of a section or column the form does not have, is ignored (the first with a console warning). The
header and the footer are never coloured.

Every problem in a `forms` entry is a console warning with the prefix `[xrmverse presentation]`; the form always opens.

#### Classic look

The classic look never reads `forms`: every form shows its normal tabs.

### Known limits of the form layouts (UI-2c)

- **Not scheduled:** the wizard on an existing record, step-change events for scripts, grid placement of the header, the
  footer or single fields, timeline options per control, background colours of tabs, the header, the footer and
  non-field cells (subgrids, quick views, web resources), a field colour on multiple-choice, file and image fields (they
  have no control box to fill), and changing a colour from a script. The layouts are edited in the web admin's form designer
  (see [Forms](web-admin.md#forms)).


### Choice colours (modern look)

In the modern look, a value of a choice column can be shown as a coloured badge instead of plain text, so that a status
or a priority reads at a glance. Badges appear wherever the modern look lists records: the grid of a view, the subgrids of
a form, the cards, and the cards and column headers of the board.

- **The background** is the option's own colour: `Color` of the option (`OptionMetadata.Color`, `#RRGGBB`), the same
  value that Power Apps uses for coloured choices. It is set with `InsertOptionValue` or `UpdateOptionValue` (see
  [Metadata messages](solutions.md#metadata-messages)), with a solution import, or in the web admin (see
  [Choice colours](web-admin.md#choice-colours)), and it travels with the choice in solutions.
- **The text** is automatic: black or white, whichever has the higher contrast (WCAG) on the background. An app's
  presentation overlay can set another text colour per value in its `choices` key:

```json
{
  "version": 1,
  "choices": {
    "account.industrycode": { "1": { "text": "#FFFFFF" } },
    "xv_priority": { "100000000": { "text": "#1A1A1A" } }
  }
}
```

The key is the name of a global choice (`xv_priority`, for every column that uses it) or `table.column` for a column's
own choice (`account.industrycode`). A value without an entry keeps the automatic text colour.

A value without a colour is shown as plain text. A multi-select choice shows its values as badges only when every
selected value has a colour; otherwise the cell shows the values as text, as in the classic look.

#### Compared with Dataverse

The background is Dataverse's `Color`: a solution exported from xrmverse carries it, and Power Apps shows it where it
shows choice colours. The text colour is an xrmverse extension kept in the overlay web resource: Dataverse does not read
it, and the overlay travels in a solution as an ordinary web resource.

#### Classic look

The classic look never shows badges and never reads `choices`: choice values are plain text.

### Known limits of the choice colours

- **No badges in form fields and charts.** The choice drop-down of a form and the charts show the label only.
- **One set of colours.** The colours are used as they are in the light and the dark colour scheme.
- **Not in the schema files.** `xrmd schema` does not read or write option colours; set them in the web admin, with the
  option value messages or with a solution.
- **The text colour stays in xrmverse.** It lives in the overlay, which Dataverse does not read; see
  [Known issues](known-issues.md#model-driven-apps).

### Known limits of the looks (UI-2a)

- **Two looks only.** External looks (renderer bundles loaded from web resources) are not scheduled; an unknown look id
  falls back to classic with a notice.
- **Modern changes the frame, the views and the main forms, not the other pages.** A view can be a board, a calendar or
  cards (see [View modes](#view-modes-modern-look)); a main form can be a wizard, place its sections on a free grid and
  draw its timeline as cards (see [Form layouts](#form-layouts-modern-look)). Dashboards, subgrids, quick create and
  quick view forms keep their classic layout in the modern frame, with the overlay's theme; subgrids show coloured choice
  values as badges (see [Choice colours](#choice-colours-modern-look)).
- **The navigation rail starts closed on a narrow screen.** At 640 px or less the rail starts closed so it does not cover
  the page; the header button opens it. On wider screens it starts open.
- **Environment values do not travel.** `organizationsetting` rows are not exported in solutions; set them again in each
  environment.
- **No font download.** `theme.fontFamily` names a font; it does not load one.
- **The web admin** (`/manage`) keeps the xrmverse look; the app picker too, in the user's colour scheme (a user who
  chose Dark sees it dark).
- **Notifications use the page-wide theme.** Toasts are drawn in the theme of the page root (the xrmverse look in the
  user's colour scheme), not in the colours of the modern overlay.

## Forms

`pagetype=entityrecord` opens a record on a main form, and without `id` the empty create form. The page is built from the
FormXml the customer wrote: the tabs, columns, sections and cells are their design, the look is xrmverse's. Everything is
read and written through the public Web API, so privileges, field security and plug-ins apply as for any other client.

Forms are edited in the web admin's form designer; see [Forms](web-admin.md#forms).

### Which form

- The form selector lists the table's **main** forms (`type` 2) that are active (`formactivationstate` 1). When the app
  names at least one main form of the table among its form components (`componenttype` 60), only the app's forms are
  listed; otherwise every main form is. (Quick view forms listed by an app do not count: they would hide every main form.)
- A form whose FormXml has `<DisplayConditions>` with `<Role Id>` children is listed only for users holding one of those
  roles. A form without them, or with `<Everyone/>`, is listed for everyone, and a System Administrator sees every form.
  A form marked `FallbackForm="true"` is also listed for a user who holds none of the roles any form of the table names.
- The order is `DisplayConditions/@Order`, then the default form (`systemform.isdefault`), then name.
- The form that opens is the URL's `formid` when it is listed (else the first one listed, with the notice "The requested
  form is not available. The default form opened instead."); without `formid`, the user's last form of the table when it
  is still listed, else the first one listed. The form bar's selector switches form and keeps the record.
- **Last form used.** A form opened through the URL's `formid` (the form selector and a script's `formSelector` navigate
  that way) becomes the user's last form of the table, stored in `userentityuisettings` (see
  [Tables in every organization](system-tables.md)) as Dataverse does, so it follows the user to another browser. Reading
  or storing it is best effort: a failure is logged in the browser console and the form opens anyway.
- With no form listed the page says "No form of this table is available in this app."
- A record that does not exist, or that the user may not read, shows "This record does not exist or you do not have
  access to it." with a link back to the list.

### Layout

- Tabs are a tab list in order (`visible="false"` ones are not drawn; the first visible tab opens; arrow keys, Home and
  End move between tabs). The OpenProject365 Project main form has eight: Summary, WBS, Gantt, Costs, Time, Materials,
  Purchasing and Team.
- A tab's columns sit side by side at their percentage widths and their sections stack. A section has its label, its
  columns (a cell spans `colspan` of them) and its label position. `columns` is either a count of equal columns (`"2"`)
  or one digit per column giving its relative width (`"111"` is three equal columns, `"12"` two columns of one third and
  two thirds), as dashboards and many main forms write it. Invisible sections and cells are not drawn, and
  a cell with no control keeps its space. A multi-line text field's `rowspan` sets its height (two lines of text per
  row), and a web resource's sets its height in rows; other controls ignore it.
- A cell's label is its label in the user's language, else 1033, else the column's display label; `showlabel="false"`
  hides it, and the control then takes the cell's full width (the label's space is not kept).
- A section's `celllabelposition` places its field labels: `Left` (the default) beside the field, `Top` above it. The
  form designer sets it per section.
- Editable fields are drawn with a soft gray fill and no border (Fluent's `filled-darker` appearance, which follows the
  light and dark themes); read-only and disabled fields keep their outline, so they stand apart. The web admin's record
  pages keep the outlined fields.
- The form's **header card** holds the title (with the auto-save status), the table's name and the form selector under
  it, the **header** cells and, at its foot, the tabs. The header cells are shown compact and read-only at the end of the
  title's row (value over label); the chevron next to them opens a panel where they are edited, as in Power Apps' flyout
  (same values, same save). The **footer** cells are drawn read-only.
- A form with `<formLibraries>` and `<events>` runs its scripts (see [Form scripts](#form-scripts)).
- On a narrow page or side pane the form adapts, as Unified Interface does: below 48rem of tab width the tab's columns
  stack (the timeline goes under the main column), below 32rem of section width a section's columns collapse into one,
  and a cell narrower than 20rem puts its label above the control.

### Controls

The control comes from the **column's metadata**; the `classid` only picks the non-field controls (a real form often
carries an old classid on a plain column, so a column asking for another presentation, such as option buttons for a Yes/No
column, is drawn with the standard control).

- **Columns:** single line and multiple lines of text, whole number, decimal, float, currency (with the record's
  currency symbol), date only and date and time, choice, multi-select choice, two options, lookup with search (one target
  or several, owner and customer included), party list (see [Email form](#email-form)), status reason (only the reasons of
  the record's `statecode`), file and image.
  `statecode` is read-only. Dates follow the column's behaviour and the user's time zone (see
  [User time zone and date formats](#user-time-zone-and-date-formats)).
- **Numbers and currency** show digit grouping and the column's decimals in the user's language (`1.234.567,50` in
  Italian) when the field is not being edited; while editing, the value has no grouping and takes the language's decimal
  mark, and Italian dots are read as grouping. **Multiple lines of text** can be made taller by dragging the field's
  corner.
- **Dates** use the Fluent date picker: type the date as `YYYY-MM-DD` (the text is stored as typed) or pick it in the
  calendar, whose month and day names follow the user's language (Italian weeks start on Monday). A date and time column
  adds a time box beside it: type `HH:mm` (24 hours) or pick a time in 30-minute steps; without a time the value is
  `00:00` in the user's time zone (User Local) or `00:00` as stored (Time Zone Independent), and the field's hint names
  the zone. Clearing the date clears the value. A read-only date does
  not open the calendar.
- A column is **required** when its level is `SystemRequired` or `ApplicationRequired`: the label is marked and Save is
  blocked with "{label}: a value is required." next to the field and in the form notification. `Recommended` columns get
  no marker.
- A column is **read-only** when its control has `disabled="true"`, when it is not valid for create or for update, when
  field security denies the write, or when the whole form is read-only: the user lacks write access to the record
  (`RetrievePrincipalAccess`), and the form says "You do not have permission to edit this record."
- A new record starts with the current user as **Owner** and, for a money record, the user's default currency, else the
  organization's base currency (when the form carries the column). A value the user enters before a default arrives is
  kept.
- **Non-field controls:** subgrid, quick view, timeline (see [Timeline](#timeline)), web resource
  and IFrame (an IFrame in the cell: see [HTML web resources](#html-web-resources)). A column whose form names a field PCF
  control for the web runs that control (see [PCF controls](#pcf-controls)). An unrecognised control is drawn as an
  explicit placeholder.

### Subgrids and quick views

- A **subgrid** shows the rows of the relationship it names (a one-to-many by the referencing column, a many-to-many
  through its intersect table) with the view it names, the view's columns, paging (`RecordsPerPage`, default 4 as
  Dataverse's) and a row click that opens the related record. On the create form it says "Save the record to see related
  records."; a relationship or view the organization does not have says "This subgrid cannot be shown: {reason}." and
  leaves the rest of the form alone.
- A form subgrid with `ChartGridMode` `Chart` and a `VisualizationId` shows the notice "This subgrid's chart is not shown on
  a form in this version; its records are listed instead." above its grid (see the
  [known limits](#known-limits-of-dashboards-and-charts-ui-1f)).
- A subgrid (and a Related tab item) has a **Commands** drop-down at the end of the title's line, styled as the form's
  command bar, whose menu holds **New {table}** and **Add Existing {table}**, each with its icon (#118):
  - **New** needs `prvCreate` on the related table (for an activity table, the shared `prvCreateActivity` is enough; the
    list's **New** follows the same rule). It opens the related table's create form. For a one-to-many, the
    relationship's lookup is set to this record through `extraqs` (`{lookup}`, `{lookup}name`, `{lookup}type`). For a
    many-to-many, the new row is associated with this record after its first save. After that save, the form comes back to
    this record and the subgrid shows the new row. For a one-to-many, the create form also asks
    [`InitializeFrom`](#initializefrom) (`ValidForCreate`) for the values mapped from this record
    (#143) and fills the columns the form shows and no
    `extraqs` parameter set; they are defaults, so the form is not dirty until the user changes something. A lookup's name
    is read from its table. If the call fails, the form says "Mapped fields from the parent could not be filled: {reason}"
    and stays usable. If the relationship's lookup is not on the related form, the parameter is reported as ignored, as any
    form parameter.
  - **Add Existing** needs `prvAppend` on the related table and `prvAppendTo` on this table (`prvAppendActivity` and
    `prvAppendToActivity` for an activity table). It opens a search of the
    related table, where the user picks one or more rows:
    - for a one-to-many, the search lists rows of no record or of another record, and each chosen row's lookup is
      PATCHed (`{nav}@odata.bind`);
    - for a many-to-many, each row is associated with `POST …/{nav}/$ref`, and a row already associated counts as done.

    A failure stays in the dialog.
- The **Related** tab follows the form's own tabs when the FormXml has a `<Navigation>` with items. It lists the items
  of `NavBar` in `Sequence` order (items with `Show="false"` are left out) and shows one at a time. A
  `NavBarByRelationshipItem` lists the related rows as a subgrid of 10 rows: the table on the other side of the
  relationship, in its associated view (`querytype` 2), else its default public view, else its first public view. A
  relationship that does not relate the form's table says so. Any other item (`NavBarItem`, a link to a page or a web
  resource) is listed and says "{label} is not available here", instead of being left out. Scripts cannot select the
  Related tab.
- A subgrid whose form names a **dataset PCF control** for the web runs that control instead of the grid (see
  [Dataset controls](#dataset-controls)).
- In the modern look a subgrid can also be shown as a board, a calendar or cards when the presentation overlay says so
  for that form (see [Subgrids](#subgrids)).
- A subgrid can list the rows that match another lookup of the form instead of related rows, an xrmverse extension (see
  [Subgrids filtered by a form column](#subgrids-filtered-by-a-form-column)).
- A **quick view** shows the related record's quick view form (`type` 6, named in `QuickForms` for the lookup's target)
  read-only. An empty lookup, or a target with no quick form in the parameter, shows nothing.

### Opening related records

A lookup's value, a form subgrid's row and a form subgrid's **New** open the related record's main form in a **side
pane**: a panel on the right, two thirds as wide as the form, with the same form, commands and scripts as the full page
(it is the side dialog of [`navigateTo`](#an-entity-record-in-a-dialog), position 2). Ctrl+click (Cmd+click on a Mac)
opens the full page instead. The pane closes with the **X** at the end of its command bar, with Escape, or after
**Save & Close**; the subgrid then reads its rows again, and a lookup shows the record's name as it was saved (the form's
own value does not change, so the form does not become dirty). A read-only lookup still opens its record. A dashboard's
grid opens the full page.

The app setting `xrmverse_LookupOpenMode` (id `6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e30`, seeded in every organization) chooses
the behaviour: `sidepane` (the default) or `page`, which navigates to the record as Dataverse does. It is read as the
look settings are: the app's `appsetting` value, else the environment's `organizationsetting` value (through the Web
API), else the default. An unknown value gives a console warning and `sidepane`. The side pane is an xrmverse extension:
Dataverse opens a lookup's record on the full page.

### Email form

Roadmap item 4.1b adds what an email form needs to write and send an email (see
[Email and queues](email-and-queues.md#outgoing-email)). The server seeds a default email form (see
[Default forms and views](email-and-queues.md#default-forms-and-views)); an app's solution can bring its own instead.

- **Party lists.** Every party list column (`from`, `to`, `cc`, `bcc`, `requiredattendees` and the others) is an editable
  list of chips, on classic and modern forms. Typing searches the column's tables by name or email address; an address that
  matches one record becomes that record, several matches ask which one, and no match stays as an address alone (a dashed
  chip marked "unresolved"). Pasted text is split at `;` and `,`. Backspace or **Remove** drops a chip; the search button
  opens the lookup dialog. **From** holds one party, chosen from the user and the queues the user can read; with the sender
  policy `queueOnly` the user is listed but cannot be chosen. On save, each changed list is written in
  `{activity}_activity_parties` (on an update, the whole party set; see
  [Activities and notes](activities.md#party-lists)). A read-only form shows the chips only.
- **Send.** The command bar of an open email that the user can write has **Send** first. It saves the form, warns before
  sending to unresolved recipients when the organization does not allow them, and runs `SendEmail` with `IssueSend` true. A
  refusal of the server shows as a form notification.
- **Send status.** A banner shows Pending Send (and follows the send until it ends, then reloads the form), Sent (with
  `senton`, the sender and the `Message-ID`) or Failed (with the reason of the email's newest send job).
- **Attachments.** A section lists the attachments with their size, with **Preview**, **Download** and
  **Delete**, and adds files by browsing or by dropping them. A file over `maxuploadfilesize` or with a blocked extension is
  refused before it is sent. The section is hidden on a new email and read-only once the email is sent. On a tab with
  two or more columns (the seeded email form) it sits at the top of the last column, above that column's sections;
  otherwise it sits beside the whole form.
- **Preview** opens in a side surface with the name, the size, "n of m", previous and next, and **Download**. It shows PDF
  files, images and plain text only. HTML, SVG and XML files, and any other type, are never rendered: they show "No preview"
  with **Download**. A file named `.pdf` that does not start like a PDF shows "No preview" too.
- A sent email (state Completed) opens read-only.

## Commands

The form's command bar shows, in this order, **New**, **Save**, **Save & Close**, **Delete**, **Activate** or
**Deactivate**, **Refresh**, **Assign** and **Share**. A command the user may not use is **hidden**, not disabled. The table's [custom commands](#custom-commands) follow.

The labels never wrap: the commands that do not fit move, from the end, into a **More commands** (…) menu. The **Close** button, when shown, stays at the end of the bar.

Commands use Fluent icons (`ui/src/shell/icons.ts`), also the list's New and paging buttons and the subgrid buttons; each keeps its text label or aria-label.

- **New** (create privilege) opens the empty form of the same table and form. The list's **+ New** does the same for users
  with the create privilege.
- **Save** is enabled when something changed. Create POSTs and then puts the new id in the URL; update PATCHes only the
  changed columns. A server error (plug-in, duplicate detection) is shown as "{code}: {message}" in the form
  notification and the values stay. **Save & Close** saves and returns to the list the user came from.
- **Delete** asks first, then returns to the list. **Refresh** reloads the record (and the subgrids), asking first when
  there are changes.
- **Deactivate** (an active record, `statecode` 0) and **Activate** (an inactive one) exist on tables whose `statecode`
  has at least two states, for users with write access. The dialog lists the status reasons (`statuscode`) of the target
  state with its `DefaultStatus` selected; a table with several inactive states (an activity: Completed, Canceled) also
  asks which state. The change is one PATCH of `statecode` and `statuscode`, asking first when there are changes, and the
  record is read again. An inactive record opens read-only with the notice "Read-only: this record's status is
  {state}."; only New, Delete, Activate, Refresh, Assign and Share remain, and `getFormType()` returns 4 (Disabled).
- **Assign** (user- and team-owned tables, with assign access) opens a dialog to pick a user or a team; the record is then
  read again, because its owner and the user's rights changed.
- **Share** (with share access) opens a side pane with two tabs:
  - **Current shares** lists each user or team the record is shared with (`RetrieveSharedPrincipalsAndAccess`), by
    name, with one column per right: Read, Write, Delete, Append, Append To, Assign and Share. Ticked and cleared rights
    are kept until **Save changes** (`ModifyAccess`); a row left with no right is removed. The bin of a row removes that
    share at once (`RevokeAccess`).
  - **Add users or teams** searches users or teams by name; the picked ones (several at a time) get the ticked rights
    (Read by default) with **Share** (`GrantAccess`, which adds to the rights of a principal already shared with).
  A user or team whose name cannot be read is shown as *Unknown user* or *Unknown team*; its id is in the tooltip.
- **Unsaved changes.** Leaving a changed form (site map, list, subgrid row, form selector, Refresh, Assign, Activate,
  Deactivate, browser back
  inside the app) asks "Discard your changes?"; closing the tab asks through the browser's own prompt.
- **Auto-save** (#117) follows the organization's `isautosaveenabled` (on by default, as in Dataverse; a System
  Administrator turns it off on the web admin Environment page). With it on, an existing record with changes is saved 30
  seconds after the last change, and when the user leaves the form through the app, instead of the "Discard your
  changes?" prompt. The `onsave` handlers run first with save mode 70 (`AutoSave`); `preventDefault()` stops that save. A
  create form never saves by itself. A prevented or failed auto-save keeps the changes and shows its error; leaving then
  asks "Discard your changes?", and the timer starts again with the next change (no retries). Nothing is auto-saved while
  another request of the form runs. The footer says "Unsaved changes", "Saving..." or "Saved". Closing the browser tab is
  not an auto-save: the browser's own prompt asks.

### Custom commands

Since UI-1e, the form's command bar also shows the buttons a solution adds in its `RibbonDiffXml`. The UI reads them from
`GET /Ribbon/{entity}` (session cookie): the `RibbonDiffXml` each installed solution kept, oldest layer first. A later
`CommandDefinition`, rule, `LocLabel` or `CustomAction` with the same `Id` replaces the earlier one.

- **What is applied:** the `CustomAction`s whose `Location` starts with `Mscrm.Form.{entity}.`, each with one `Button`.
  They come after the standard commands, ordered by `Sequence`, then document order. The label is `LabelText`
  (`$LocLabels:` resolved to the user's language, then 1033, then the first title, else the id); `ToolTipTitle` and
  `ToolTipDescription` make the tooltip.
- **Rules:** each `DisplayRule` and `EnableRule` is an AND of its rules. A command whose display or enable rules are false
  is hidden, not disabled, as in Power Apps. Supported: `FormStateRule` (`Create`, `Existing`, `ReadOnly`, `Disabled`,
  `BulkEdit` is always false), `ValueRule` (the value as text: a choice's number, `true`/`false`, `null`, a lookup's id in
  lower case without braces), `EntityRule`, `CrmClientTypeRule` (`Web`), `OrRule` and `CustomRule`. `InvertResult` and
  `Default` apply; a `CustomRule` that does not answer in 10 seconds is false. Rules run after the form loads, after each
  save, after a change of a value, and on `formContext.ui.refreshRibbon()`; the newest evaluation wins.
- **Actions:** `JavaScriptFunction`s run one after another. Their libraries load once per form into a separate script
  host (with `Xrm`), shared by all its commands. The button is disabled while its actions run and while the form saves;
  an action that throws or rejects shows "Script error in {function}: {message}".
- **Parameters:** `CrmParameter` `PrimaryControl` (the formContext), `PrimaryEntityTypeName`, `FirstPrimaryItemId`
  (braced, upper case), `PrimaryItemIds`, `PrimaryControlId` (the form id), `UserLcid`, `OrgLcid`, `OrgName`; and
  `StringParameter`, `BoolParameter`, `IntParameter`, `DecimalParameter`.
- **Icons:** `ModernImage` `Accept`, `Cancel`, `Send`, `Close`, `New`, `Add`, `Delete`, `Edit`, `Refresh` and `Save` map to
  Fluent icons; any other name shows no icon.
- **Never silently:** a command xrmverse cannot honour (an unknown rule or parameter, a `Url` action, a library that does
  not load) is not shown, and the form warns "Command '{label}' is not available: {reason}". A caller who cannot read
  `solution` gets no custom commands and the warning "Custom commands could not be read: {message}".

#### Verified on OpenProject365

The export's purchase request form shows Submit and Cancel Request on a draft request,
and hides Approve, Reject, Create RFQ and Create Order (its `FormStateRule`, `OrRule` and `ValueRule`s on `statuscode`).
Submit runs `OP365.Purchasing.submitRequest` from `op365_/scripts/purchasing.js`, which calls the bound custom API
`op365_SubmitPurchaseRequest` with `fetch` and the session cookie, shows its form notification and refreshes the form and
the ribbon: the request is Submitted and the bar shows Approve and Reject. Approve asks through
`Xrm.Navigation.openConfirmDialog` and leaves the request Approved, with Create RFQ and Create Order. The script and the
diff run unmodified (`ui/e2e/model-driven-commands.spec.ts`, local only).

### Server messages added in UI-1b

`RetrieveAppComponents`, `RetrieveUnpublished` (returns the published row: xrmverse has no unpublished layer), and over
the Web API `RetrievePrincipalAccess`, `RetrieveSharedPrincipalsAndAccess`, `GrantAccess`, `ModifyAccess` and
`RevokeAccess`. `AccessRights` is the comma-separated string of Learn (`ReadAccess, WriteAccess, …`). A user's or team's
rights on a record come from the existing access check (privileges, depth, shares, team shares, hierarchy security).
`systemform` has the new columns `isdefault` and `formactivationstate`, filled by solution import. Elastic tables refuse
`RetrievePrincipalAccess` with 400.

### InitializeFrom

`InitializeFrom` (#143) gives the values a new record of
`TargetEntityName` starts with, taken from the record `EntityMoniker` through the maps from its table to the target table,
over the SDK (`InitializeFromRequest`) and the Web API (`GET InitializeFrom(EntityMoniker=@p1,TargetEntityName=@p2,TargetFieldType=@p3)`
with `@p1={'@odata.id':'accounts(<id>)'}`, `@p2='contact'` and `@p3=Microsoft.Dynamics.CRM.TargetFieldType'ValidForCreate'`;
a number for `TargetFieldType` is refused with `0x80060888`, as Dataverse does). The maps are:

- the lookup of each one-to-many relationship from the source table to the target table (the owner and audit lookups
  excepted), set to the source record, unless `SkipParentalRelationshipMapping` is true;
- the column maps a solution imported (`attributemap`, see [Solutions](solutions.md)), each copying a value that is set.

Only the target columns that `TargetFieldType` keeps (`All`, `ValidForCreate`, `ValidForUpdate`, `ValidForRead`) are returned,
and only those with a value. The Web API answers with the target's `@odata.context` and `@odata.type`, plain values as a
Retrieve writes them, and each lookup as `<navigation>@odata.bind: "<set>(<id>)"`, ready to be sent in a `POST`. The caller
must be able to read the source record (403, 404 otherwise), and field security applies to what is copied. A pair of tables
with neither a relationship nor an imported map is a 400.

## Web resources

A solution's web resources (`webresource` rows, filled by solution import) are served by the host, so that a form
library, an HTML page, a style sheet or an image keeps the address it has in Dataverse.

| URL | What it answers |
|---|---|
| `GET /WebResources/{name}` | The web resource whose `name` is the whole path after `/WebResources/` (`op365_/scripts/account.form.js`), matched in any case. |
| `GET /%7b{anything}%7d/WebResources/{name}` | The same, under the cache-busting prefix Dataverse puts in its links; the prefix is ignored. |
| `GET /ClientGlobalContext.js.aspx` | xrmverse's own short script for HTML web resources: it defines `GetGlobalContext()` from the app that hosts the page (its `parent`, or the `opener` of a window opened by `openWebResource`) and sets `window.Xrm` when the page has none. Outside the app, `GetGlobalContext()` throws. No Microsoft script is served or copied. |

- **Content type** from `webresourcetype`: 1 HTML `text/html`, 2 CSS `text/css`, 3 script `application/javascript`, 4 XML,
  9 XSL and 12 RESX `text/xml`, 5 PNG, 6 JPG, 7 GIF, 10 ICO `image/x-icon`, 11 SVG `image/svg+xml`; text types carry
  `; charset=utf-8`. Type 8 (Silverlight, XAP) is refused with 415: no browser runs it.
- **Caching.** `ETag` is weak and follows `modifiedon`; `If-None-Match` answers 304. `Cache-Control: private, no-cache`,
  so the browser asks again every time and a re-imported library is never stale. `X-Content-Type-Options: nosniff`.
- **Security.** The row is read as the caller, through the same pipeline and Read check as a Web API retrieve. A web
  resource that is missing, or that the caller may not read, is a 404. Basic User reads `webresource`.
- **Authentication.** A `<script src>`, an IFrame `src` and a page's own `fetch(…, { credentials: "same-origin" })` cannot
  send a bearer header. The app shell therefore keeps a same-origin **session cookie** (`xrmd.session`) that carries the
  user's access token: `HttpOnly`, `SameSite=Strict`, accepted only on `/api/data/…`, `/WebResources/…`, `/Controls/…` and
  `/ClientGlobalContext.js.aspx`, with a CSRF check on writes. That path list limits what a request from another site
  could do with the cookie; it does not limit what a web resource can do. Form libraries and HTML web resources run on the
  shell's own origin, so they can reach the tab's token and call any endpoint as the user, as in Dataverse: treat them as
  trusted code (see "Web resources are trusted code" in [Security](security.md), with the cookie's rules under "Web session
  cookie"). When the cookie cannot be set, the shell shows a warning bar with the server's reason, because form scripts
  and web resources fail without it.
- **Control files.** `GET /Controls/{name}/{path}` serves a file of an imported custom (PCF) control: `{name}` is the
  `customcontrol` row's `name` (`op365_OP365.ProgressField`, any case) and `{path}` the file's path as its manifest writes
  it, relative to the manifest folder (`ControlManifest.xml`, `bundle.js`, `strings/ProgressField.1033.resx`). Content type
  by extension (`.js` `text/javascript`, `.css` `text/css`, `.resx`/`.xml` `application/xml`, PNG, SVG, GIF, JPG images,
  else `application/octet-stream`); `ETag` is the SHA-256 of the content, `If-None-Match` answers 304,
  `Cache-Control: private, max-age=0, must-revalidate`. 403 without Read on `customcontrol` (Basic User has it); an unknown
  control or file, or a path with `..`, `.` or empty segments, a backslash or an encoded slash, is a 404. The session
  cookie authenticates it.

## Form scripts

A main form's `<formLibraries>` and `<events>` run as they do in Dataverse; OpenProject365's form scripts run unchanged.

- **Script host.** Each open form gets a hidden, same-origin IFrame of its own (`about:blank`). The form's libraries
  load into it in FormXml order as `<script src="/WebResources/{name}">`, so a library's globals (`var OP365 = …`) belong
  to that form. Leaving the form (another form, record or app) disposes the IFrame: the old form's handlers and timers
  stop, and the next form starts with fresh globals.
- **Events.**
  - `onload` handlers run once the record and its metadata are on the form and the form's libraries are loaded. They run
    again after the Refresh command, after Assign (which reads the record again), and after the first save of a create
    form (the form becomes the new record's form). `formContext.data.refresh()` reads the record again **without**
    running OnLoad, as in Dataverse.
  - The formContext exists from the moment the form opens, before its libraries finish loading: `Xrm.Page` (and
    `parent.Xrm.Page` from a web resource on the form) is the form at once, also when a library or the script host fails.
  - `onsave` handlers run before Save and Save & Close, and before a save a script starts with `formContext.data.save()`.
    `getEventArgs()` gives `getSaveMode()` (1 Save, 2 Save & Close, 70 auto-save; `data.save({ saveMode })` passes its own mode),
    `preventDefault()` and `isDefaultPrevented()`. A prevented save does not run.
  - `onchange` handlers of a column run after a user change (once the user leaves a text or number field with a new
    value, at once for a choice, a lookup or a date), and after `fireOnChange()`. A script's `setValue` does not raise
    OnChange, so a handler that sets its own column does not loop.
- **Handlers.** Handlers of one event run in order. `enabled="false"` handlers are skipped; `passExecutionContext="true"`
  passes the execution context first; `parameters` is read as a JSON list and passed after it.
- **Errors never break the form.** Each failure is a form notification at level error, and the form stays usable (Save
  still works):
  - a handler that throws: "Script error in {functionName}: {message}". The next handler still runs;
  - a function that is not there: "Script error: {functionName} was not found in {libraryName}.";
  - a library that does not load (404 or a syntax error): "Script error: the library {name} could not be loaded.";
  - parameters that cannot be read: "Script error in {functionName}: the parameters could not be read ({message}).".
- **Promises and the app settings AsyncOnLoad and AsyncOnSave** (#129).
  By default a promise returned by an OnLoad or OnSave handler is not waited for (a rejection is still a notification).
  When the app's setting is on, the form waits for each handler's promise, in order, before it runs the next handler and,
  for OnSave, before it saves:
  - each promise gets 10 seconds; after that the form goes on without it and shows a notification. An OnSave handler that
    calls `getEventArgs().disableAsyncTimeout()` is waited for without a limit;
  - a rejected promise (or a timeout) is a notification and does **not** cancel the save, unless that handler called
    `getEventArgs().preventDefaultOnError()` (as Learn describes the Save event arguments);
  - the settings are `appsetting` rows of the app, whose `value` turns the setting on when it is `true` (in any case; `1`
    or any other text leaves it off). An app without a row uses the definition's `defaultvalue` (`false` as seeded). The
    shell reads them with the list of apps (`settingdefinitions` and `appsettings`, which every user reads). They come from
    the `<appsettings>` block of the app in a solution file, or from a `PATCH` or `POST` of `appsettings` through the Web
    API by a System Administrator; there is no maker page to switch them (see
    [Known limits of web resources and form scripts](#known-limits-of-web-resources-and-form-scripts-ui-1c)).
- **Notifications.** `setFormNotification` shows in the form's notification area with its level (error, warning, info)
  until `clearFormNotification`. A control notification (`setNotification`, `addNotification`) shows under its field and
  blocks Save while it is there.
- **`Xrm`** is set on the app shell's window and in each script host. `Xrm.Page` is the current form's `formContext`
  (with `Xrm.Page.context`, the global context), also on a form without scripts.

## Client API

The Client API is reimplemented from its public documentation: names, signatures, enum values and return shapes follow
Learn. Only the documented, supported members exist. A documented member that this version does not implement **throws**
`{member} is not available in this version (stage {stage}).`; a promise-based one rejects with that error. It is never
`undefined` and never a silent no-op. An undocumented member (`Xrm.Internal`, `Mscrm.*`, `Xrm.Page.context` internals)
stays `undefined`, as in Dataverse, and scripts that work on the Power Apps page structure are not supported.

| Namespace | What works |
|---|---|
| Execution context | `getFormContext()`, `getEventSource()`, `getDepth()`, `getEventArgs()` (OnSave), `getContext()` (the global context), `getSharedVariable()` and `setSharedVariable()`. |
| `formContext` | `getAttribute(…)` and `getControl(…)` (by name, index, predicate or none; `null` for an unknown name), `data`, `ui`. |
| `formContext.data` | `entity`, `attributes`, `getIsDirty()`, `isValid()`, `save(saveOptions?)` and `refresh(save?)` (promises). |
| `formContext.data.entity` | `getId()` (`{GUID}` upper case, `""` on create), `getEntityName()`, `getEntitySetName()`, `getEntityReference()`, `getIsDirty()`, `getPrimaryAttributeValue()`, `isValid()`, `save()` and `save("saveandclose")`, `attributes`, `addOnSave`, `removeOnSave`, `addOnPostSave`, `removeOnPostSave`. |
| Attributes | `getName()`, `getAttributeType()`, `getFormat()`, `getValue()`, `setValue()`, `getIsDirty()`, `getInitialValue()`, `getRequiredLevel()`, `setRequiredLevel()`, `getSubmitMode()`, `setSubmitMode()`, `addOnChange()`, `removeOnChange()`, `fireOnChange()`, `controls`, `getParent()`; choices: `getOptions()`, `getOption()`, `getText()`, `getSelectedOption()`; numbers: `getMax()`, `getMin()`, `getPrecision()`; text: `getMaxLength()`; lookups: `getIsPartyList()`. Values have Learn's shapes: a lookup is `[{ id, name, entityType }]` or `null`, a choice a number, a multi-select choice an array, a date a `Date`. |
| Controls | `getName()`, `getControlType()`, `getAttribute()`, `getLabel()`, `setLabel()`, `getVisible()`, `setVisible()`, `getDisabled()`, `setDisabled()`, `setNotification()`, `clearNotification()`, `addNotification()`, `setFocus()`, `getParent()`; lookups: `addPreSearch()`, `removePreSearch()`, `addCustomFilter()` (inside a PreSearch handler), `getEntityTypes()`, `setEntityTypes()`; choices: `addOption()`, `removeOption()`, `clearOptions()`; subgrids: `refresh()`, `getEntityName()`; web resource and IFrame: `getSrc()`, `setSrc()`, `getInitialUrl()`, `getObject()` (the IFrame element), `getContentWindow()`. Header controls are named `header_{column}`, footer controls `footer_{column}`, a second body control of a column `{column}1`. |
| `formContext.ui` | `getFormType()` (1 create, 2 update, 3 read-only), `setFormNotification()`, `clearFormNotification()`, `close()`, `refreshRibbon()` (evaluates the [custom commands](#custom-commands)' rules again), `setFormEntityName()`, `tabs` (name, label, visibility, `getDisplayState()`, `setDisplayState()`, `setFocus()`, `sections`), sections (name, label, visibility, `controls`), `controls`, `formSelector` (`getCurrentItem()`, `items` with `getId()`, `getLabel()`, `navigate()`, `getVisible()`, `setVisible()`). |
| `Xrm.WebApi` (and `.online`) | `createRecord`, `retrieveRecord`, `retrieveMultipleRecords` (OData options or `?fetchXml=`, `maxPageSize`, `nextLink`), `updateRecord`, `deleteRecord`, `execute` (custom and standard actions and functions, bound and unbound, and CRUD requests, from `getMetadata()`), `executeMultiple` (one by one, or as one `$batch` when the list holds a change set), `isAvailableOffline()` (`false`). A failure rejects with `{ errorCode, message, code }`. |
| `Xrm.Navigation` | `openForm` (with form parameters), `navigateTo` (`entityrecord` inline or in a dialog, `entitylist` inline, `webresource` inline or in a dialog), `openAlertDialog`, `openConfirmDialog`, `openErrorDialog`, `openUrl` (http and https only), `openWebResource`. |
| `Xrm.Utility` | `getGlobalContext()` (`userSettings`, `organizationSettings`, `getClientUrl()`, `getCurrentAppUrl()`, `getCurrentAppName()`, `getCurrentAppProperties()`, `getVersion()`, `prependOrgName()`, `getQueryStringParameters()`), `showProgressIndicator()`, `closeProgressIndicator()`, `lookupObjects()`, `getEntityMetadata()`. |
| HTML web resources | `parent.Xrm` and `parent.Xrm.Page` from a page in a form, a page or a dialog; `GetGlobalContext()` after `ClientGlobalContext.js.aspx`; the `data` query parameter; `window.close()` in a dialog. |

The dialogs (alert, confirm, error, lookup) and the progress indicator are the shell's own Fluent UI components with the
xrmverse theme; their strings ship in English and Italian. The progress indicator counts: each `showProgressIndicator`
needs its `closeProgressIndicator`. Leaving the form (another form, record or page) closes it whatever the count, so a
handler that threw before closing it does not leave the overlay over the next page.

### An entity record in a dialog

`navigateTo({ pageType: "entityrecord", … }, { target: 2 })` opens the record's
form in a dialog with the same title, size and position options. The form works as on its page (commands, scripts,
the unsaved-changes prompt); leaving the record (Save & Close, Close, Esc) closes the dialog. On a create form the
promise resolves with `savedEntityReference`: the record saved last, with its primary name, or an empty array when
nothing was saved. On an existing record it resolves with nothing. While the dialog is open, `Xrm.Page` is its form;
the page's form comes back when it closes.

**What throws "not available"** (stage after UI-1 unless noted):

- `Xrm.App`, `Xrm.Copilot`, `Xrm.Device`, `Xrm.Encoding` and `Xrm.Panel`, as soon as they are read
  (#132); `Xrm.WebApi.offline`; `Xrm.Navigation.openFile`.
- `Xrm.Utility.getAllowedStatusTransitions`, `getEntityMainFormDescriptor`, `getLearningPathAttributeName`,
  `getPageContext`, `getResourceString`, `invokeProcessAction`, and `refreshParentGrid`.
- On the global context: `client`, `getAdvancedConfigSetting`, `getWebResourceUrl`, `isOnPremises`; the user settings
  `dateFormattingInfo.LongDatePattern`, `LongTimePattern`, `FullDateTimePattern`, `MonthDayPattern`, `YearMonthPattern`,
  `ShortestDayNames`, `defaultDashboardId`, `isGuidedHelpEnabled`, `isHighContrastEnabled`, `securityRolePrivileges`,
  `transactionCurrency`, `transactionCurrencyId` and `getSecurityRolePrivilegesInfo`; the organization settings
  `attributes`, `baseCurrency`, `isTrialOrganization`, `organizationExpiryDate`, `useSkypeProtocol`
  and `fullNameConventionCode`.
- `formContext.data.process`, `ui.process`, `ui.navigation`, `ui.quickForms` (business process flows and the rest are out
  of UI-1), `data.addOnLoad`, `ui.addOnLoad` and their `remove`, `ui.getViewPortHeight`, `getViewPortWidth`,
  `data.entity.getDataXml` (Learn does not document its format) and `save("saveandnew")`.
- Attributes: `getUserPrivilege`, `isValid`, `setIsValid`, `setPrecision`, and `getAttributeType` for a column type Learn
  does not list. Controls: lookup `addCustomView`, `getDefaultView`, `setDefaultView`, `addOnLookupTagClick`; tab
  `addTabStateChange`, `getContentType`, `setContentType`; IFrame `addOnReadyStateComplete`; subgrid `getGrid` and the
  grid members; `getOutputs` (see [PCF controls](#pcf-controls)); the notification members of a control that is not a field.
- Options that would change the result: `openForm` or `navigateTo` with `useQuickCreateForm`, `createFromEntity`,
  `relationship`, a process or stage, `tabName`; `navigateTo` with `dashboard`, `custom` or `generative` pages;
  `lookupObjects` with a view that filters on a related table.

## HTML web resources

- **On a form.** A web resource or IFrame control becomes an IFrame in its cell. A web resource shows
  `/WebResources/{name}` with the control's `data`; an IFrame shows its `Url`, which must be http or https (any other
  scheme shows a message instead). When the control passes the record's parameters
  (`PassParameters`), the address gets `id` (empty until the record is saved), `typename`, `orgname`, `userlcid` and
  `orglcid`. A same-origin page reaches `parent.Xrm` and `parent.Xrm.Page`; a page of another origin is sandboxed
  (`allow-scripts allow-forms allow-popups`) and cannot; the control's `Security` option (restricted cross-frame scripting) sandboxes
  any page completely. A missing web resource shows "The web resource {name} does not exist or you do not have access to it."
- **As a page.** `main.aspx?…&pagetype=webresource&webresourceName={name}&data={data}` (and `navigateTo` with
  `target: 1`) shows the page full size inside the app shell, with `data` in its query string.
- **As a dialog.** `navigateTo` with `target: 2` opens it in a dialog with the given title, size (pixels or `%`) and
  position (centre or side). The page closes the dialog with `window.close()`, as it does in Dataverse, and the promise
  resolves then; the dialog's Close button and Esc do the same. A page shown inline or on a form cannot close anything
  outside itself.
- **`Xrm.Navigation.openWebResource`** opens the page in a new window, which reaches the app through `window.opener`.

## PCF controls

A field PCF control (a code component bound to a column) that a solution imports runs on the form in place of the
standard control. Its row is in the `customcontrol` table and its files (`ControlManifest.xml`, the bundle, stylesheets,
`.resx` and images) are served by xrmverse under `/Controls/{name}/{path}`, with the same session cookie as web resources.

### What runs

When a form's `controlDescription` names a control for the web (`formFactor="0"`, else one without a form
factor), the cell loads it: the app reads the control's manifest once per session, injects its stylesheets and scripts in
their `order`, and waits up to 10 seconds for the bundle to register its constructor with
`ComponentFramework.registerControl`. Then:

- a **standard** control gets `init(context, notifyOutputChanged, state, container)` once, with a `div` in the cell;
- a **virtual** (React) control gets `init(context, notifyOutputChanged, state)`, and the React element each
  `updateView` returns is rendered in the cell by the app's own React;
- `updateView(context)` runs after `init` and whenever a bound value, the mode (disabled, label) or the tracked size
  changes, or the control calls `factory.requestRender()`;
- `notifyOutputChanged` makes the app read `getOutputs()` and write each bound property it returns as a user edit: the
  form becomes dirty and the column's OnChange runs. A disabled control (read-only form, a field without update
  privilege, a disabled control) writes nothing. A value of the wrong type for the column is not written; the field
  shows an error instead, which blocks the save until a correct value arrives;
- `destroy()` runs once when the cell goes (the form closes, another tab is selected, the field is hidden).

### Context

The members a control gets:

| Member | What it gives |
|---|---|
| `parameters` | each bound property: `raw` (the Client API value: numbers for whole, decimal, float and currency; `true`/`false`; the option value; a `Date`; a lookup as `[{id, name, entityType}]`), `formatted`, `type`, `attributes` (`DisplayName`, `LogicalName`, `RequiredLevel`, `Type`, and `Precision`, `MinValue`, `MaxValue`, `MaxLength`, `Options` when the column has them), `error`, `errorMessage`, `security`; each input property: `raw` and `formatted` from the form's literal, else the manifest's `default-value` |
| `mode` | `isControlDisabled`, `isVisible`, `label`, `allocatedWidth` and `allocatedHeight` (-1 until `trackContainerResize(true)`), `trackContainerResize`, `setControlState` (kept for the session and handed back to the next `init`), `contextInfo` (see below) |
| `client` | `getFormFactor()` 1 (web), `getClient()` `Web`, `isOffline()` false, `isNetworkAvailable()` true, `disableScroll` |
| `userSettings` | the global context's user settings, plus `numberFormattingInfo` for the shell language |
| `formatting` | `formatCurrency` (the record's currency symbol by default), `formatDecimal`, `formatInteger`, `formatDateShort`, `formatDateLong`, `formatDateLongAbbreviated`, `formatDateYearMonth`, `formatTime`, `getWeekOfYear`, in the shell language |
| `resources` | `getString` (the user's language, else 1033, else the first `.resx`; a key no file has is returned as is), `getResource` (an `img` file of the manifest as base64) |
| `webAPI`, `navigation` | the app's `Xrm.WebApi` and `Xrm.Navigation` (see [Client API](#client-api)) |
| `utils` | `getEntityMetadata` and `lookupObjects` as in `Xrm.Utility`; `hasEntityPrivilege` from the user's privileges (`RetrieveUserPrivileges`, read once per session) |
| `factory` | `requestRender()` |
| `page` | `entityId`, `entityTypeName`, `isPageReadOnly` |
| `fluentDesignLanguage` | `tokenTheme` (the shell's Fluent v9 theme), `typographyTokens`, `brand` (the xrmverse brand ramp), `isDarkTheme` false |
| `updatedProperties` | the parameters changed since the last `updateView` (all of them on the first), plus `layout` after a size change |

**`mode.contextInfo`** is `{ entityId, entityTypeName, entityRecordName }`: the record id (empty on a new record, set after
the first save), the table's logical name and the record's primary name as last saved. Learn's `Mode` reference does not
list it and the PCF FAQ says controls get no record id; Power Apps provides it in practice and published controls read the
record id from it, so xrmverse provides it as observed behaviour (issue #181). The same value is in `page.entityId`.

### Platform libraries

A virtual control names React and Fluent UI in its manifest (`<platform-library>`). xrmverse does
not ship the platform's bundles: it hands the control its own React 18.3.1 for `React` 16.x (as `Reactv16`) and its own
`@fluentui/react-components` 9.75.1 for `Fluent` 9.x (as `FluentUIReactv940` and `FluentUIReactv9`). Any other library
or version (Fluent 8, another React major) stops the control from loading.

**When a control cannot run** (no such control, a manifest or script that fails, no registration within 10 seconds, an
unsupported platform library, a bound property naming a column the table does not have, a dataset control in a field cell,
an exception in `init`, `updateView` or the rendered element) the cell shows the standard control and the form shows the warning
"Custom control '{name}' could not be loaded: {reason}". The rest of the form works as usual. A missing control, an invalid
manifest and an unsupported platform library are remembered until the page is reloaded; after a manifest read or a script
that failed, or a registration that timed out, the next form that uses the control tries to load it again.

### Client API

`formContext.getControl(name)` returns the field control as before; `getControlType()` returns
`customcontrol` while the PCF control runs (`standard`, `lookup`, ... while it loads or after it fell back);
`setDisabled` and `setLabel` drive `mode.isControlDisabled` and `mode.label`. `setVisible` does not change `mode`
(`mode.isVisible` is always true): hiding the field removes the control from the form, which calls `destroy()`, and showing
it again starts the control with a new `init`.

### Dataset controls

Since UI-1e, a **form subgrid** whose `controlDescription` names a dataset control (a manifest with a `<data-set>`; the
FormXml's `customControl` has a `<data-set name>` child of `<parameters>`) runs that control in place of the grid. The
control loads, starts and updates as a field control does (`init` once, `updateView`, `destroy` when the subgrid goes, the
same context members), with these differences:

- `parameters.{data-set name}` is the **DataSet** object, the same object for the life of the control. It lists the
  subgrid's view, limited to this record through the subgrid's relationship, `RecordsPerPage` rows at a time. Its members,
  as Learn's `DataSet` reference for model-driven apps describes them:
  - `loading`, `error`, `errorMessage`;
  - `columns`: the view's visible columns in order, each with `name`, `displayName`, `dataType` (the manifest type),
    `alias`, `order`, `visualSizeFactor` (the column width), `isHidden`, `isPrimary`, `disableSorting`;
  - `records` (by id) and `sortedRecordIds`; each record has `getRecordId()`, `getValue(column)` (the Client API value; a
    lookup is an `EntityReference` `{ id: { guid }, name, etn }`), `getFormattedValue(column)` and `getNamedReference()`;
  - `paging`: `pageSize`, `totalResultCount`, `firstPageNumber`, `lastPageNumber`, `hasNextPage`, `hasPreviousPage`,
    `loadNextPage()`, `loadPreviousPage()`, `loadExactPage(n)`, `setPageSize(n)`, `reset()`;
  - `sorting`: `{ name, sortDirection }` entries (0 ascending, 1 descending); a change followed by `refresh()` queries
    again in that order;
  - `refresh()` (back to page 1), `getSelectedRecordIds()`, `setSelectedRecordIds(ids)`, `clearSelectedRecordIds()`,
    `getTargetEntityType()`, `getViewId()`, `getTitle()` (the view's name), and `openDatasetItem(reference)`, which opens
    the record's form through `Xrm.Navigation.openForm`.
- `updatedProperties` holds the data-set name after each load (and when a load starts), and `layout` after a size change.
- `mode.contextInfo` and `page` hold the **form's** record, as for a field control, not the subgrid's rows.
  `mode.isControlDisabled` is false unless the whole form is read-only; `mode.label` is the subgrid's label.
- On a **new record** a subgrid with a relationship has nothing to list: the data-set queries nothing, `loading` is false
  and `sortedRecordIds` is empty. After the first save it loads. A subgrid without a relationship lists the whole view.
- `formContext.getControl(subgrid).refresh()` and a refresh of the form load page 1 again.
- When the control cannot run (as for a field control, or a manifest with no `data-set` of the FormXml's name, or a
  `property-set` in the data-set) the classic grid takes its place, with the same warning "Custom control '{name}' could
  not be loaded: {reason}".

Not available: `filtering`, `linking`, `addColumn` and `retrieveRecordCommand` throw "… is not available in this
version" (see [the known limits](#known-limits-of-dataset-controls-ui-1e)).

#### Verified on OpenProject365

The project form's `WbsTreeGrid` (WBS tab) and `GanttChart` (Gantt tab) run in place of
their subgrids with no fallback, unmodified. Both read the DataSet's `loading`, `error`, `columns` and `refresh()` and
query the WBS nodes through `context.webAPI` themselves. Both draw a project's nodes; `WbsTreeGrid` also reads the
project's currency symbol with `$expand=transactioncurrencyid` (#183). Checked by
`ui/e2e/model-driven-pcf.spec.ts`, local only.

## Dashboards and charts

Since UI-1f the shell opens an app's **system dashboards** and draws the **system charts** they hold.

### Import and export

A solution's root `<Dashboards>` become `systemform` rows of `type` 0 (dashboard), `objecttypecode`
`none`, with the dashboard's `<form>` in `formxml` and its base-language name and description (component type 60). Each
table's `<Visualizations>` become rows of the `savedqueryvisualization` table (entity set `savedqueryvisualizations`,
component type 59): `name`, `description`, `primaryentitytypecode`, `datadescription` and `presentationdescription` (kept as
XML text), `isdefault`, `charttype`, `type`, `webresourceid` and `introducedversion`. Basic User reads the table at Global
depth, as `savedquery`. An export writes `<Dashboards>` and each table's `<Visualizations>` back, with the ids as they came
in (braces and case).

### The page

`main.aspx?appid=…&pagetype=dashboard&id={formid}` lays the dashboard out by the form rules
([Layout](#layout)): its tabs one under the other, then columns, sections and cells. Without `id` the app's first
dashboard opens, in sitemap order; an app with none shows "This app has no dashboards.". Above the layout are the
dashboard's name and a **Dashboard** selector of the app's dashboards (the app's dashboard components, those in its
sitemap first); choosing one changes `id` in the URL. Each cell loads on its own, so a cell that fails shows its error
and the others still render:

| Cell | What it shows |
|---|---|
| Subgrid with `ChartGridMode` `Chart` and a `VisualizationId` | The chart, over the subgrid's view (below). |
| Subgrid with `ChartGridMode` `Grid`, or with no chart | The view in the grid of the lists, with no parent filter, `RecordsPerPage` rows per page; a row opens its record. |
| Subgrid whose `controlDescription` names a dataset control | The [dataset control](#dataset-controls), with no parent record (it lists the whole view). |
| Web resource | The UI-1c frame ([HTML web resources](#html-web-resources)). |
| Anything else (an IFRAME of an address, a timeline, a field) | "This dashboard component is not supported in this version". |

A cell that names a view or a chart that is not there shows "The view {id} was not found" or "The chart {id} was not
found". An interactive dashboard (`FormPresentation` 1 or `stream` cells) shows "Interactive dashboards are not supported
in this version" and is laid out by the classic rules. `EnableChartPicker` or `EnableViewPicker` set to true shows one
notice, "Chart and view pickers are not available in this version", and is otherwise ignored.

### The chart query

A chart runs the FetchXML of its `datadescription` (an aggregate query with one group-by and its
measures), to which the view's `<filter>` elements are added, and its `<link-entity>` elements stripped of their
`<attribute>`, `<all-attributes>` and `<order>` children (their filters stay). The view's columns and order are not
used. A chart and a view on different tables is a cell error: "Chart '{name}' is for table {a}, the view is for {b}". The
merged query goes to the Web API as `?fetchXml=`, so the caller's security applies: a user who cannot read the table sees
"You do not have access to the data of this chart" in that cell only.

### Drawing

Charts are drawn by Fluent UI React Charts (`@fluentui/react-charts`, MIT), as wide as their cell, in the
theme's colours and in light or dark:

- `Column`, `Bar`, `Line`, `Area`, `Pie` and `Doughnut` are drawn. A `Bar` chart with several series is drawn as grouped
  columns (Fluent's horizontal bar chart draws one series). Any other `ChartType` (such as `StackedColumn`,
  `Funnel`, `Point`) shows "Chart type {type} is not supported in this version" and the view's grid in its place. A Power BI
  chart (`charttype` 1) or a web resource chart (`webresourceid`) shows "This chart kind is not supported in this version".
- A chart whose fetch has more than one `groupby="true"` attribute, or whose `datadescription` has more than one
  `<category>`, shows "Charts with more than one group-by are not supported in this version." and the view's grid in its
  place.
- The categories are the formatted values of the group-by column (a choice's label, a lookup's name); an empty value is
  "(blank)". The values are the aggregate values; with `IsValueShownAsLabel="True"` each column shows its formatted
  value, while a horizontal bar or a slice shows the number in Fluent's short form (such as `1.2k`). Hovering or focusing a point shows its category and formatted value; a long category label is cut and shows in
  full as a tooltip.
- Colours come from `PaletteCustomColors` ("r,g,b; …") when the chart has them, otherwise from Fluent's data
  visualization palette. Charts have Fluent's legend (one entry per series, or per slice for Pie and Doughnut).
- A query that returns no rows shows "No data to show".

### Formatted values on aggregate results

Aggregate FetchXML results (SDK, SOAP and the Web API) now carry formatted
values, which the charts use: a group-by on a choice, a Yes/No or a status gives its label in the caller's language, a
group-by on a lookup its name; `count` and `countcolumn` are formatted as whole numbers; other aggregates as their column
(see the [known limits](#known-limits-of-dashboards-and-charts-ui-1f) for dates and money).

### Verified on OpenProject365

The four dashboards of the export open from the sitemap, unmodified. **Spend** draws its
three charts over the view "Active Purchase Orders" (`Spend by Supplier` and `Spend by Project`, one bar per supplier or
project; `Purchase Orders by Status`, one arc per status) and lists "Open Purchase Orders" and "Invoices to Review".
**Resource Load**, **Time Approval** and **Timesheet** each run their dataset control (`ResourceLoad`, `TimeApproval`,
`WeeklyTimesheet`) with no fallback: Time Approval lists the entries waiting for approval and Timesheet draws the week of
the user's resource. Resource Load draws its toolbar and legend, then shows its own error "The resource load could not be
loaded" (see [the known limits](#known-limits-of-dashboards-and-charts-ui-1f)). Checked by
`ui/e2e/model-driven-dashboards.spec.ts`, local only.

## Timeline

Since UI-1g, a form cell with the timeline control (`notescontrol`, classid `{06375649-C143-495E-A496-C962E5B4488E}`)
shows the record's **activities** and **notes** in one list, and its **posts** when the app turns them on (see
[Posts](#posts)). The behaviour follows Learn's
[Use timeline](https://learn.microsoft.com/power-apps/user/add-activities),
[Set up the timeline control](https://learn.microsoft.com/power-apps/maker/model-driven-apps/set-up-timeline-control) and
the [timeline FAQ](https://learn.microsoft.com/power-apps/maker/model-driven-apps/faqs-timeline-control). The control is
xrmverse's own (React and Fluent UI): it does not look like Power Apps' one.

### What it lists

- Activities whose Regarding (`regardingobjectid`) is the record, of every activity table, custom ones included. Fax and
  letter are left out, as Learn's default. A party of an activity (To, From, Required) does not bring it in.
- Notes whose `objectid` is the record.
- A table without activities (`HasActivities` false) shows notes only: no Activities and Overdue chips, no activity
  filters, no activity in the + menu. A table with neither says "This table has no activities or notes." A new record
  says "Save the record to see its timeline."
- Every query runs as the signed-in user, so the items and the counts are only those the user can read. An activity type
  the user cannot read (`prvReadActivity`) is left out, and so are the notes when the user lacks `prvReadNote`: the
  timeline then shows the activities only.

### What the user can do

- **Search** the title and the text (`subject` and `description` of activities, `subject` and `notetext` of notes). The
  search runs on the server with `contains`.
- **Chips** (an xrmverse addition): All, Activities, Notes, Overdue and Mine, each with its count. A chip is a shortcut
  for one filter of the pane: selecting the chip or the same filter in the pane is the same thing.
- **Filter pane**: record type, activity type, activity status (Active, Overdue, Closed), activity status reason (per
  activity type, each with its count), activity due date, modified date, and Owner = Me (an xrmverse addition).
  Options of one category combine with OR, categories with AND. A filter that only an activity can match (type, status,
  status reason, due date) hides the notes while it is set. Each count is the number of items that adding that option
  would show, given the other categories. **Clear all filters** empties the pane; it keeps the search text and the sort.
- **Sort** newest first (the default) or oldest first, by `modifiedon`, from the More menu. **Refresh** is there too.
- **Expand all / collapse all**, or one item at a time. An expanded item shows the body and, for a note, the attachment's
  name and size.
- **"Load more"** after 10 items adds 10 more.
- **+** (Create a timeline record): Note first, then each activity table the user may create. A new activity opens its
  main form with Regarding set to the record; after its first save the form comes back to the record.
- **Note entry** under the search box: a title, a text and one attachment. Dropping a file on the timeline opens the entry
  with that file. A file larger than `organization.maxuploadfilesize` is refused before the upload.
- **Overdue**: an overdue activity shows "Overdue N days" whatever the grouping option.
- **Quick actions**: on an activity, Open, Complete, Assign and Delete; on a note, Edit, Download attachment and Delete.
  Each shows only when the user holds the privilege. Complete closes the activity in one click (state Completed with its
  default status reason). Email has no Complete.
- **Keyboard** (an xrmverse addition): Up and Down move between items, Enter opens an activity (and shows or hides a
  note's details), Space expands or collapses, N opens the note entry. "Load more" and filter changes are announced to
  screen readers.

The timeline refreshes after a change made from it. As on Learn, it does not refresh by itself for a change made
elsewhere: use Refresh, or a script.

### Timeline in the Client API

`formContext.getControl("notescontrol")` (or the control's own name) is a timeline wall control:
`getControlType()` returns `timelinewall`, and `getName`, `getLabel`, `setLabel`, `getParent`, `getVisible`,
`setVisible`, `getDisabled`, `setDisabled`, `setFocus` and `refresh` work, as listed in Learn's
[control reference](https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/reference/controls/refresh).
`refresh()` reads the first page again with the user's current filters. `formContext.data.refresh()` reloads the
timelines too. A disabled timeline still lists, expands and opens items, but hides +, the note entry, the file drop and the
quick actions.

### Options

Two presentation options are off by default, so an app looks like Dataverse unless its maker turns them on:

| Setting definition | When `true` |
|---|---|
| `xrmverse_TimelineGroupByDate` | Items are grouped: Due (overdue activities, by due date, always first, up to 50 with "Show all overdue" after), Today, Yesterday, This week, This month, Older. |
| `xrmverse_TimelineRememberFilters` | The chips, pane filters and sort order are kept per user, per form and per timeline, in the browser's storage. |

Both definitions are seeded in every organization with `defaultvalue` `false`. Turn one on for an app with an `appsetting`
row whose `value` is `true`, the same way as `AsyncOnLoad` (see [Form scripts](#form-scripts)): in the app's
`<appsettings>` in a solution file, or with a `POST` of `appsettings` through the Web API by a System Administrator. The
shell reads the settings when it lists the apps, so a change shows after a new page load.

### Posts

A **post** is a short message a user leaves on a record; other users **reply** to it with comments, and a post or a reply
can **@mention** users. The tables (`post`, `postcomment`, `postregarding`), their messages and their security are
described in [Activities and notes](activities.md#posts). In Dataverse, posts need a Dynamics 365 app and the table
turned on in Post Configurations; xrmverse does not model those configuration tables, so the timeline has its own switch.

#### Turning posts on

Posts are off by default, as in a new Dataverse environment. The setting definition
`xrmverse_TimelinePosts` (id `6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e03`, `defaultvalue` `false`) is seeded in every
organization; an app turns posts on with an `appsetting` row whose `value` is `true`, the same way as the two options
below: in the app's `<appsettings>` in a solution file, or with a `POST` of `appsettings` through the Web API:

```http
POST /api/data/v9.2/appsettings
{
  "value": "true",
  "parentappmoduleid@odata.bind": "/appmodules(<app id>)",
  "settingdefinitionid@odata.bind": "/settingdefinitions(6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e03)"
}
```

With the option on, every timeline of the app shows posts for its record, whatever the table. With it off, the timeline
shows no post UI and sends no post query; the tables still work through the APIs. A user without `prvReadPost` sees no
post UI either.

#### Reading

- Posts are a third stream, merged with activities and notes. A post sorts by `createdon` (it cannot be changed), the other
  items by `modifiedon`. A reply does not move its post.
- A post shows its author, its date, its text with the mentions, and "1 reply" or "N replies" (nothing when it has none).
  Expanding the post lists its replies, oldest first, each with its author and date.
- **Posts** joins the Record type category of the filter pane and the chips; **Posts by** is a new category with **Auto
  post** (`source` 1) and **Users** (`source` 2), with counts. Setting Posts by hides activities and notes. Mine matches the
  posts the user created and the posts the user replied to. Modified date filters posts on `createdon`. Search matches
  `post.text`.
- An auto post, should one exist (the server refuses to create one, and posts are not migrated in this version), is listed
  as "Auto post" with the notice "The content of auto posts is not shown in this version": its text is XML whose format
  Learn does not document.

#### Writing

- **+ > Post** (after Note) opens a post entry under the search box: one text box of 2000 characters with a counter,
  **Post** and **Cancel**. Ctrl+Enter (Cmd+Enter) posts.
- **Reply**, under an expanded post, opens a reply box of 1000 characters with **Reply** and **Cancel**.
- **Delete** is offered on the user's own posts and replies, with a confirmation ("Delete this post?", "Delete this
  reply?"), since there is no undo. Deleting a post deletes its replies.
- + > Post and Reply need `prvCreatePost`; Delete needs `prvDeletePost`. `setDisabled(true)` hides + > Post, Reply and
  Delete, as it hides the note actions.
- A refusal of the server shows above the list ("The action could not be completed: …") and keeps the typed text.

#### @mentions

Typing `@` followed by some letters in a post or a reply lists the users (`systemuser`) whose full name,
a later word of it (the last name) or email address starts with them, up to 10, with the user's recent picks first (kept in the browser, up to 5).
Picking one inserts `@[8,{id},"Full Name"]` into the text, in the format Learn documents for mentions
(`@[Entity,ID,"Display String"]`, no spaces after the commas); the mention counts towards the length limit. When reading,
a mention whose Entity is `8` or `systemuser` shows as a link to the user's record; a mention of another table shows its
display string as plain text; malformed `@[` text shows as typed. The server stores the text as written and does not check
the mentions.

#### Verified on OpenProject365

On the project form, a task created from + gets the project as Regarding and the form
comes back to the project after its first save (the test adds a main form for `task` first: see
[the known limits](#known-limits-of-the-timeline-ui-1g)); a note with a file, the Notes chip, Complete and "Load more"
work. The
purchase order form (a table without activities) shows notes only. Checked by `ui/e2e/model-driven-timeline.spec.ts`,
local only. With `xrmverse_TimelinePosts` on for the app, a post with a mention picked from the list, two replies, the
delete of one reply (the count goes back from "2 replies" to "1 reply") and the Posts chip work on the project form:
`ui/e2e/model-driven-timeline-posts.spec.ts`, local only.

## xrmverse extensions

xrmverse adds a few things that Dataverse does not have. They are xrmverse's own, with names chosen for xrmverse, and
they take effect only inside a model-driven app (not on the [web admin](web-admin.md) pages), even when the web admin
configures them.

- **`XrmVerse`**, a client extension on the app shell's window and in every script host, next to `Xrm`. HTML web
  resources reach it as `parent.XrmVerse`. It is defined in every app, whether or not the app has OnLoad handlers.
  `Xrm.App` and `Xrm.Panel` stay "not available".
- **App side panes**: panels on the right of the main area that keep their content while the user navigates.
- **App notifications**: messages under the app header that a script raises for the whole app.
- **The app OnLoad event**, set with the `xrmverse_AppOnLoad` app setting.
- **A navigation toggle** in the header of both looks.
- **Subgrids filtered by a form column**, set with the `xrmverse_SubgridFilters` setting and the form designer.

### Members

| Member | Shape | Behavior |
|---|---|---|
| `XrmVerse.App.sidePanes.state` | number, read and write | `0` collapsed (only the tab strip shows), `1` expanded. Another value throws `TypeError`. |
| `XrmVerse.App.sidePanes.createPane(options)` | `Promise<SidePane>` | Adds a pane. Rejects when `paneId` is already in use; the first pane is left untouched. |
| `XrmVerse.App.sidePanes.getPane(paneId)` | `SidePane \| undefined` | The open pane with that id. |
| `XrmVerse.App.sidePanes.getAllPanes()` | `SidePane[]` | The open panes in display order. |
| `XrmVerse.App.sidePanes.getSelectedPane()` | `SidePane \| undefined` | The pane whose content is shown. |
| `XrmVerse.App.addGlobalNotification(n)` | `Promise<string>` | Shows a notification and resolves with its id. |
| `XrmVerse.App.clearGlobalNotification(id)` | `Promise<void>` | Removes it; an unknown id resolves. |

`createPane` options, all optional:

| Option | Type | Default | Meaning |
|---|---|---|---|
| `paneId` | string | generated: `pane-1`, `pane-2`, … | Identifier, unique among the open panes. |
| `title` | string | `""` | Header text and tab tooltip. |
| `imageSrc` | string | a generic pane icon | Icon in the tab strip. Only a web resource path (`WebResources/…`); anything else rejects with `TypeError`. |
| `width` | number (px) | `340` | Requested content width, read back as set. Drawn as `max(300, min(width, viewport − 320))` and applied again when the window resizes. |
| `canClose` | boolean | `true` | Shows a close button in the header. |
| `hideHeader` | boolean | `false` | Hides the header (title and close button). |
| `isSelected` | boolean | `true` | Selects the new pane and expands the strip; `false` adds it without switching. |
| `hidden` | boolean | `false` | Keeps the pane but hides its tab and content. |
| `alwaysRender` | boolean | `false` | Keeps the content mounted while another pane is selected or the strip is collapsed. |
| `keepBadgeOnSelect` | boolean | `false` | Selecting the pane does not clear its badge. |

A `SidePane` has the options above as read and write properties (`paneId` is read-only), plus:

| Member | Behavior |
|---|---|
| `badge` | `false` or `null` none; `true` a dot; a number from 1 to 99 a count (`99+` above); cleared on select unless `keepBadgeOnSelect`. |
| `navigate(pageInput)` | `Promise<void>`. Takes `{ pageType: "webresource", webresourceName, data }` and replaces the pane's content (no history). Resolves when the shell has checked the web resource (the IFrame is created then for a shown pane, and when the pane is shown for one that is not selected, collapsed or hidden); a web resource that is missing or that the user cannot read rejects, and the pane shows an inline error when it is shown. |
| `select()` | Selects the pane and expands the strip. |
| `close()` | Removes the pane, also when `canClose` is `false` (the flag only governs the user's close button). |

Panes are ordered with the ones that have `canClose: false` first, then the others, each group in creation order.

Notification fields:

| Field | Meaning |
|---|---|
| `level` | Required: `1` success, `2` error, `3` warning, `4` information. |
| `message` | Required, plain text. |
| `showCloseButton` | Default `false`. |
| `action` | `{ actionLabel, eventHandler }`: a button labelled `actionLabel` that calls `eventHandler()`. A throwing or rejected handler is logged and the notification stays. |

A missing or wrong `level` or `message` rejects with `TypeError`, and so does an `action` without a non-empty string
`actionLabel` and a function `eventHandler`. Notifications stack under the app header, newest
first; three show and the rest are folded into a "+N more" button. They last until a script clears them, the user closes
them or the app is left.

```js
const pane = await XrmVerse.App.sidePanes.createPane({
  paneId: "console",
  title: "Console",
  canClose: false,
  alwaysRender: true,
});
await pane.navigate({ pageType: "webresource", webresourceName: "xrmv_/console/index.html" });
pane.badge = 3;

const id = await XrmVerse.App.addGlobalNotification({
  level: 3,
  message: "3 calls are waiting",
  showCloseButton: true,
});
await XrmVerse.App.clearGlobalNotification(id);
```

### The app OnLoad event

An app turns the event on with an `appsetting` for the definition `xrmverse_AppOnLoad` (id
`6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e20`, default value empty, seeded in every organization). The value is a JSON array
of `{ "library": string, "function": string }`:

```json
[{ "library": "xrmv_/scripts/agent.js", "function": "Xrmv.Agent.onAppLoad" }]
```

The setting travels in the app's `<appsettings>` block of a solution file, or a System Administrator sets it through the
Web API, like the other app settings (see [Solutions](solutions.md)).

- **When.** Once each time the user opens the app: the shell loads with that app, or the user switches to it from the app
  picker. Not on navigation inside the app and not on the reload of one page.
- **How.** The shell loads each library once into an app script host that lives as long as the app is open, then calls
  the functions in array order. Each handler receives the execution context as its first argument: its `getContext()`
  is the global context and its `getEventSource()` is `null`. A returned promise is awaited for up to 10 seconds, then
  the next handler runs. The app never waits for the handlers before it shows its first page.
- **Errors.** Each failure shows one error notification and the app keeps working:
  - A missing library, a missing function, a throw, a rejected promise or a promise still pending after 10 seconds:
    `App OnLoad: {library}/{function}: {message}`. The message is `the library could not be loaded`,
    `the function was not found`, the error's message, or `timed out after 10 seconds` (the late outcome of that handler
    is ignored).
  - An invalid value (not JSON, or JSON that is not an array of objects with a `library` and a `function` string):
    `App OnLoad: {error}`, where `{error}` names the problem, for example `xrmverse_AppOnLoad must be a JSON array`. No
    handler runs, and the app opens.
  - The app script host cannot start: `App OnLoad: the script host could not be started ({message}).` No handler runs.
- **Trust.** The libraries are web resources served with the user's session, as form scripts are: trusted code (see
  [Security](security.md)).

### Side panes in the shell

- The pane area sits on the right of the main area, under the app header: a vertical tab strip (one icon per visible
  pane, the title as tooltip, the badge on the icon) and, when expanded, the selected pane with its header and content.
  The main area shrinks and is never covered. With no panes, the strip is not drawn. Its collapse button sets `state`.
- The content belongs to the shell, so it survives navigation in the main area and a save in the Personalization dialog
  that keeps the look. Without `alwaysRender`, an unselected pane's content is unmounted and mounted again, fresh, when
  the pane is selected.
- The pane area stays in view while the page scrolls: it sticks to the top of the window, takes the window's height below
  the header (all of it once the header has scrolled away), and the pane's IFrame fills that height, not the page's.
- A pane shows a web resource in an IFrame of `/WebResources/{name}?data=…`, with `parent.Xrm` and `parent.XrmVerse`
  reachable as for an HTML web resource in a page ([HTML web resources](#html-web-resources)). `Xrm.Navigation` called
  from a pane acts on the main area, which is how a console opens a record (`parent.Xrm.Navigation.openForm`) and
  keeps its own IFrame.
- Panes belong to one browser tab and one app. Leaving the app, with the app picker or by opening another app, closes the
  app script host, the panes and the notifications together.
- Both looks draw the pane area with their own theme. Under the shell's narrow breakpoint the strip stays and an
  expanded pane overlays the main area at full width minus the strip, instead of shrinking it.

### The navigation toggle

The header of both looks (classic and modern) has a button that collapses and shows the site map. The browser
remembers the choice (`localStorage`, one value for all apps), and on a narrow screen the navigation starts collapsed.
A browser that blocks local storage simply does not remember.

### Subgrids filtered by a form column

A subgrid of a main form normally lists the rows related to the form's record (its relationship) or, with no
relationship, every row of its view. xrmverse adds a third choice: the rows whose lookup column matches **another lookup
of the form**. For example, on a work item form, a grid of the open cases of the customer chosen in the form's Customer
lookup, which follows the lookup when the user changes it.

Dataverse has no equivalent, so this is an xrmverse extension. The FormXml is not changed: the subgrid stays a valid
Dataverse subgrid with an empty `RelationshipName` ("All records"), and the filter is kept outside the form, in a JSON web
resource that a setting names.

#### Tasks

| Task | How |
|---|---|
| Add a filtered subgrid | In the web admin's form designer, **Add subgrid** → Records **Records matching a column of this form** (see below). |
| Change an existing subgrid | Select it, then in **Properties** choose Records **Matching a column of this form**, or back to **All records**. |
| Ship the filter in a solution | Add the JSON web resource to the solution and give the app an `appsetting` for `xrmverse_SubgridFilters` that names it. |
| Remove the filter | Delete the subgrid, or switch it to another Records option, and save the form. |

#### The setting

The setting definition `xrmverse_SubgridFilters` (id `6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e31`, default value empty) is
seeded in every organization. Its value is a JSON array of web resource names:

```json
["xrmv_/xrmverse/subgridfilters.json"]
```

An app reads both values: the app's (`appsetting`) and the environment's (`organizationsetting`). Unlike the look
settings, where the first value found wins, the two are **merged**: the entries of every resource named by either value
apply, and when the app's resources and the environment's have an entry for the same form and control, the app's entry
wins. The value is a list of names rather than the entries themselves because a setting value holds at most 2,000
characters.

#### The resource

Each named resource is a script web resource (type 3) whose name ends in `.json` and whose content is:

```json
{
  "forms": {
    "<form id>": {
      "<subgrid control id>": { "source": "xrmv_customerid", "target": "xrmv_customerid" }
    }
  }
}
```

- `<form id>` is the `systemform` id, with or without braces, in any case. `<subgrid control id>` is the `id` of the
  subgrid's `<control>` in the FormXml, in any case.
- `source` is a lookup, Customer or Owner column of the form's table, placed on the form: the subgrid follows its current
  value on the form. A source that is not on the form counts as empty.
- `target` is a lookup column of the subgrid's table that can hold the records of `source` (for a Customer source, a column
  that accepts both account and contact).
- The grid lists the rows of its view where `target` equals the current value of `source`; the view's own filter is kept.
- Unknown keys and malformed entries are ignored with a console warning that starts with `[xrmverse subgrid filter]`; a
  resource that cannot be read or parsed is ignored with one such warning, and the others still apply.

#### On the form

The filter works in the classic and the modern look and in every app that reads the setting.

- **The grid follows the source.** When the source lookup changes on the form, by the user or by a script
  (`setValue`), the grid reloads with the new value.
- **Empty source.** The grid shows no rows and the notice "Fill {label} to see the records.", where `{label}` is the
  source's label on the form; **New** is disabled.
- **Create form.** The grid works before the first save: it follows the source as soon as it is filled. There is no "Save
  the record to see related records."
- **New** opens the subgrid table's create form with `target` set to the source's value (`extraqs` `{target}`,
  `{target}name`, `{target}type`). **Add Existing** is not offered: the rows are not related to the form's record.
- **Client API.** `getRelationship()` returns `null`, as for an "All records" subgrid; `getFetchXml()` returns the filtered
  query; `refresh()` and the subgrid's OnLoad event work as for any subgrid. The board, calendar and cards modes of the
  modern look ([Subgrids](#subgrids)) show the filtered rows.
- **A broken entry never shows all records.** If `target` is not a lookup of the subgrid's table, or cannot hold the
  source's record, the subgrid shows "This subgrid's filter cannot be applied: {reason}." instead of rows, and a
  `[xrmverse subgrid filter]` warning is logged.
- **A subgrid with a relationship keeps it.** An entry for a subgrid whose FormXml names a `RelationshipName` is ignored
  with a console warning: the subgrid lists the related records, as Dataverse would.
- **Dataset controls.** A subgrid replaced by a dataset PCF control shows "This subgrid is filtered by a form column,
  which its custom control does not support." instead of the control.
- **While the resources are read.** A subgrid without a `RelationshipName` shows "Loading related records..." until every
  resource named by `xrmverse_SubgridFilters` is read, so it never shows all records first. If a resource cannot be read,
  the others still apply, and a subgrid without a `RelationshipName` replaced by a dataset PCF control shows "This
  subgrid's filters could not be read, so its custom control is not shown." instead of the control.

#### In the form designer

The web admin's form designer (see [Forms](web-admin.md#forms)) offers the filter as a third **Records** option, marked
*xrmverse*:

- **Add subgrid**: **Records matching a column of this form**, then **Table** (the subgrid's table), **Column of this form**
  (the lookup, Customer and Owner columns placed on the form) and **Matches column of {table}** (only the lookups of that
  table that can hold the same records are listed).
- **Properties** of a subgrid without a relationship: Records **All records** or **Matching a column of this form**, with
  the same two column lists, and the hint "Lists {table} records whose {target} equals this form's {source}."
- Both show the warning "xrmverse extension. Exported to Dataverse, this subgrid lists all records of the view."

On **Save**, the designer writes the form's entries to the **first** resource named by the environment's value of
`xrmverse_SubgridFilters`, keeping every other form's entries. If the environment's value names none, the designer creates
the script web resource `{prefix}_/xrmverse/subgridfilters.json`, where `{prefix}` is the customization prefix of the
Default solution's publisher, and sets the environment's value to name it. To ship it, add that web resource to your own
solution. A resource from a managed solution, or one that cannot be parsed, is not written: the designer shows an error
and keeps the page's changes. The designer reads and writes only the environment's value, never an app's.

#### In Dataverse

A solution with a filtered subgrid imports into Dataverse, because the FormXml is a standard subgrid, but there the
subgrid lists **all records** of its view: Dataverse does not read `xrmverse_SubgridFilters` (the setting definition does
not exist there, so an `appsetting` for it does not import). Keep such forms out of solutions that you also deploy to
Dataverse, or accept the unfiltered grid there.

### Not importable into Dataverse

A solution whose app uses `xrmverse_AppOnLoad`, or whose scripts call `XrmVerse`, does not import into Dataverse: the
setting definition does not exist there and the scripts fail on `XrmVerse`. Keep such an app out of solutions that you
also deploy to Dataverse. Import and export in xrmverse keep the setting in the app's `<appsettings>` like any other.

### Known limits of the xrmverse extensions (X.10)

- **A pane shows web resources only.** `navigate` in a pane accepts only `pageType: "webresource"`; `entityrecord` and
  `entitylist` reject with "{pageType} is not available in a side pane in this version".
- **A pending `navigate` is resolved when its pane closes or the app is left.**
- **Switching the look reloads the panes.** A Personalization save that changes the look (classic to modern or back)
  draws another app frame, so every pane's IFrame, `alwaysRender` ones too, is created again and loses its state. A save
  that keeps the look (colour scheme, language) keeps them.
- **Panes are not restored after a browser reload.** The OnLoad handlers create them again when the app opens.
- **No drag to resize a pane.** The width is the option, clamped as above.
- **Icons are web resources.** `imageSrc` takes no URL: the shell makes no outbound request for it.
- **Handlers run once per app open, with a 10 second wait each.** A handler that needs more time keeps running, but its
  outcome is ignored and a notification says so.
- **No `XrmVerse` on the web admin pages.**

### Known limits of the subgrids filtered by a form column (X.11)

- **xrmverse only.** In Dataverse the same subgrid lists all records of its view.
- **One `appsetting` per app.** An app has one value of `xrmverse_SubgridFilters`: two solutions that set it for the same
  app overwrite each other's value, and only the last imported list of resources applies to that app. The environment's
  value still applies too.
- **The designer writes one resource.** It stores entries only in the first resource named by the environment's value; to
  use another resource, or an app's, edit its JSON.
- **One lookup column, one condition.** No filter by a text, choice or date column, by several columns, or together with a
  relationship; no script API to change a subgrid's filter.
- **Main forms only.** Entries for subgrids inside quick view forms, on dashboards and in the Related tab are ignored.
- **An unreadable resource hides dataset controls.** While a resource named by `xrmverse_SubgridFilters` cannot be read,
  every subgrid of the app without a `RelationshipName` that is replaced by a dataset PCF control shows a notice instead
  of the control, whether or not it has an entry: xrmverse cannot tell which subgrids the missing resource filters.

## User time zone and date formats

Each user's `usersettings` row ([System tables](system-tables.md)) holds a time zone (`timezonecode`) and date and time
formats (`dateformatstring`, `timeformatstring`, `dateseparator`, `timeseparator`, `amdesignator`, `pmdesignator`). A new
user gets code 92 (UTC) and the en-US formats `M/d/yyyy`, `h:mm tt`, `/`, `:`, `AM`, `PM`; rows made by an earlier
version get the same values once, at startup. The time zones are the rows of the read-only table `timezonedefinition`
(code, `standardname`, `bias`, `retiredorder`), which every user can read. Saving a `usersettings` row with a code that is
not in that table is refused with 400. Each user picks the date and time formats in **Personalization** (see below); the
time zone is set through the Web API or the SDK, for example `PATCH usersettingscollection({systemuserid})` with
`{"timezonecode":110}`.

### Choosing the date and time format

**Personalization** (the user menu in the header) has three lists for the signed-in user's own row:

| List | Choices | Column |
|---|---|---|
| **Date format** | `M/d/yyyy`, `MM/dd/yyyy`, `d/M/yyyy`, `dd/MM/yyyy`, `yyyy/MM/dd` | `dateformatstring` |
| **Date separator** | `/`, `-`, `.` | `dateseparator` |
| **Time format** | `h:mm tt`, `hh:mm tt`, `H:mm`, `HH:mm` | `timeformatstring` |

Each choice shows today in that format. A value set through the API that is not in a list stays selectable. The patterns
are .NET custom patterns: `/` stands for the date separator, `:` for the time separator and `tt` for the AM/PM designators.

The formats apply everywhere a date is shown:

- **Formatted values.** The server writes every `FormattedValue` of a date and time column in the caller's formats, and
  a User Local value in the caller's time zone (a Date Only or Time Zone Independent value is shown as stored). This is
  what views, subgrids, cards, the board, the calendar and charts show, and what Web API and SDK clients receive. An
  internal caller and a user without a settings row get `M/d/yyyy h:mm tt` in UTC.
- **Forms.** The date box shows the user's date format and reads text typed in it (fields in the format's order, any
  separator), and also `YYYY-MM-DD`; the time box shows the user's time format and reads it, and also `HH:mm`. The
  placeholder shows the format.
- **PCF controls.** `context.formatting.formatDateShort` and `formatTime` use the user's formats.

The app reads the row when it starts and applies the zone by the column's `DateTimeBehavior`:

| Behaviour | On the form | `getValue()` / `setValue()` |
|---|---|---|
| User Local (also when the column states no behaviour) | shown and typed in the user's zone; a date-only column is the user's local midnight | the instant: a `Date` whose UTC fields are the stored UTC value |
| Date Only, Time Zone Independent | shown as stored, no conversion | a `Date` whose browser-local fields are the stored date and time, so `getHours()` gives the stored hour whatever the browser's zone |

A local time that does not exist (the hour skipped when the clocks go forward) is moved forward by the gap; a local time
that occurs twice (when the clocks go back) is read as standard time. The server and the app use the same rule.

`getGlobalContext().userSettings.getTimeZoneOffsetMinutes()` is the user's offset from UTC now, east positive (Rome in
summer: 120). `userSettings.dateFormattingInfo` gives `AMDesignator`, `PMDesignator`, `DateSeparator`, `TimeSeparator`,
`ShortDatePattern` and `ShortTimePattern` from the user's settings, `FirstDayOfWeek` (as the date picker: 1 for Italian,
0 otherwise), and `DayNames`, `AbbreviatedDayNames`, `MonthNames`, `AbbreviatedMonthNames` in the shell's language.

The server converts with the SDK messages `LocalTimeFromUtcTime` (`TimeZoneCode`, `UtcTime` → `LocalTime`) and
`UtcTimeFromLocalTime` (`TimeZoneCode`, `LocalTime` → `UtcTime`), open to every member of the organization. Over the Web
API only the first is a function, as in Learn's list:

```http
GET /api/data/v9.2/LocalTimeFromUtcTime(TimeZoneCode=110,UtcTime=2026-03-29T01:30:00Z)
```

returns `{"LocalTime":"2026-03-29T03:30:00Z"}`. The local time carries a `Z` although it is not UTC, as Dataverse
returns it.

## Stages

| Stage | What it adds |
|---|---|
| UI-1b (done) | Forms: tabs, sections, standard controls, subgrids, quick views, standard commands, the Assign and Share dialogs; `RetrieveAppComponents`, `RetrievePrincipalAccess` and `RetrieveUnpublished`. |
| UI-1c (done) | Web resources (`/WebResources/{name}`, the session cookie), form scripts (OnLoad, OnSave, OnChange) and the Client API (`formContext`, `Xrm.WebApi`, `Xrm.Navigation`, `Xrm.Utility`); HTML web resources on forms, as pages and as dialogs. |
| UI-1d (done) | Field PCF controls (`customcontrol`, `/Controls/{name}/{path}`): standard and virtual controls bound to a column, with the app's React and Fluent UI as platform libraries. |
| UI-1e (done) | Dataset PCF controls in form subgrids and custom form commands from `RibbonDiffXml` (`/Ribbon/{entity}`). |
| UI-1f (done) | Dashboards (`systemform` type 0) and system charts (`savedqueryvisualization`) drawn with Fluent UI React Charts; dataset controls, views and web resources on dashboards. |
| UI-1g (done) | The timeline: activities, notes and, with an app option, posts of the record (see [Timeline](#timeline)). |

UI-1 is complete: every stage above is delivered. Custom rendering (UI-2, item 3.6) follows in stages:

| Stage | What it adds |
|---|---|
| UI-2a (done) | The classic and modern looks, their resolution (user, app, environment), the presentation overlay v1 (theme, density, colour scheme, white-label), the Personalization dialog and the environment's look settings (see [Looks: classic and modern](#looks-classic-and-modern)). |
| UI-2b (done) | View modes in the modern look: board, calendar and cards, and cards on narrow screens (see [View modes](#view-modes-modern-look)). |
| UI-2c (done) | Form layouts in the modern look: wizard, free grid and timeline cards (see [Form layouts](#form-layouts-modern-look)). |
| UI-2e (done) | View designer in the web admin, with the view modes of the modern look (see [Views](web-admin.md#views)). |
| UI-2f (done) | Form designer in the web admin (see [Forms](web-admin.md#forms)). |
| UI-2g (done) | More from the form designer: drag and drop; creating and configuring subgrids, quick views, web resources and custom (PCF) controls; editing events and form libraries (see [Forms](web-admin.md#forms)). |
| X.10 (in progress) | The xrmverse-only extension `XrmVerse` (app side panes and app notifications), the app OnLoad event and the collapsible navigation in the classic look (see [xrmverse extensions](#xrmverse-extensions)). |
| X.11 (in progress) | Subgrids filtered by a lookup of the form, set with the `xrmverse_SubgridFilters` setting and the form designer (see [Subgrids filtered by a form column](#subgrids-filtered-by-a-form-column)). |

Out of UI-1 (declared in the spec): business process flows, client-side business rules, editable grids, offline, canvas
apps and custom pages.

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- **View command bar (selection).** Share on several records only grants rights: to see or remove the current shares of a
  record, open it. The view does not read each record's rights before a command, so a record the user may not change is
  reported in the result, not hidden. There is no selection on the board and the calendar. Custom commands do not appear
  in the view's bar yet.
- **`RetrieveAppComponents`, `RetrievePrincipalAccess` and `RetrieveUnpublished`** are available since UI-1b, to the
  SDK, SOAP and Web API callers and to plug-ins. A plug-in's `RetrievePrincipalAccess` and
  `RetrieveSharedPrincipalsAndAccess` run in its own transaction, outside the pipeline (no plug-in step fires on them),
  and see the rows it has not committed yet.
- **No N:N `appmoduleroles_association` and no collection-valued `$expand`.** `appmoduleroles` is a plain intersect
  table, read as an entity set (`appmodulerolescollection?$filter=…`); `appmodules?$expand=appmoduleroles_association` is a 400.
  The shell joins apps, app roles and the user's roles in the browser, which costs a few extra requests when the app
  picker loads.
- **`appmodulecomponent` and `appmoduleroles` are import-only.** A Create, Update, Delete or Assign through the API is a
  400 `0x80040203` "App modules are created by solution import in this version." `appmodule` itself stays writable through
  the API, as in Dataverse: deleting an app deletes its components and its role links with it, and deleting a role
  removes its app links. `appmodulecomponent` is not change-tracked (like `savedquery` and `systemform`).
- **A role or component the organization lacks is a warning, not a failure.** The solution import writes a warning in the
  import job's log for each missing role, and for each table or site map component it cannot resolve (for example a table
  the organization does not have), and imports the rest. Dataverse may refuse such an import. A view, chart or form
  component that does not exist is linked anyway (a later import may bring it) with a warning in the log; since UI-1f a
  chart component resolves to its `savedqueryvisualization` row. Business process flow components keep the id of the file without a check.
- **Apps are matched to roles by role id.** The server does not fill `parentrootroleid`, so an app shared with a role of
  the root business unit matches users holding that role row. If roles are ever copied per business unit, a user holding
  a child unit's copy will not see the app.
- **Apps without roles** are shown to System Administrators only; in Power Apps, makers may see them too.
- **Object type codes and names** of `appmodulecomponent` (9007) and `appmoduleroles` (9009, entity set
  `appmodulerolescollection`, key `appmoduleroleid`) were checked against a real organization
  (#105). An organization seeded earlier (9008, set
  `appmoduleroles`, key `appmodulerolesid`) gets the right names at its next startup, rows kept.
- **`appmoduleroles` columns differ from Dataverse.** Dataverse types `appmoduleid` and `roleid` as lookups (read as
  `_appmoduleid_value`, `_roleid_value`), gives the table no owner and adds the solution columns (`appmoduleroleidunique`,
  `ismanaged`, `componentstate`, `introducedversion`, `solutionid`). xrmverse keeps them as unique identifiers named
  `appmoduleid` and `roleid` on an organization-owned intersect, like its other intersect tables, without the solution
  columns.
- **`usersettings.paginglimit`** accepts any integer on the server (Learn gives no list); the shell pages by 25, 50, 75,
  100 or 250 and uses 50 for any other value.
- **`*` in a `begins-with` quick find condition** is matched literally. Dataverse treats `*` as a wildcard in quick
  find; the OpenProject365 quick find views use `like` only, where `*` is handled.
- **A lookup whose targets have no primary name** cannot be matched or sorted by name: a `like`-family condition or an
  `<order>` on it is refused with 400 `0x80040203`. Dataverse tables always have one, so this concerns only odd metadata.
- **Choice labels follow the user's language.** The formatted value of a choice, a Yes/No column and an activity's status
  uses the label in the caller's `usersettings.uilanguageid`, else 1033, else the first label, and a choice is sorted by
  that same label (#109). In a `distinct` query an order
  on a choice by label is refused (400): set `useraworderby="true"` to sort by value. The group-by order of an aggregate
  query, and the order on a column that names a table (`objecttypecode`), stay by stored value. OData `$orderby` sorts a
  choice by its value, as Learn states for the Web API; the SQL (TDS) endpoint orders every column by its stored value.
  Audit history labels stay in 1033.
- **Related-table column headers** show the column's own label; Power Apps adds the related table's name in parentheses
  (#104). A related-table lookup without a formatted
  value is shown empty rather than as a GUID. A related-table `uniqueidentifier` column that is not a lookup (for
  example the related table's own id) is shown empty as well, a side effect of never showing a GUID.
- **Personal views are not shared from the grid.** "View actions" saves, renames and deletes the user's own personal
  views (#101); sharing a view, and the column editor and
  filters of Power Apps, are not offered. A personal view shared with the user is listed and opens, but only "Save as new
  view" applies to it.
- **Language.** `/manage` stays in English, and the shell strings go back to English when the user leaves the app shell.
  Only English and Italian shell strings ship.
- **Font.** The shell uses the xrmverse brand font Sora (`@fontsource-variable/sora`, OFL-1.1, bundled with the app, no CDN;
  system font stack as fallback, #102). Regular text uses Sora's
  light weight (300); semibold (600) and bold are unchanged. The same weight 300 applies to a modern overlay's
  `theme.fontFamily`; a font without a light weight shows its nearest weight. Web resources in
  iframes are not affected. The brand theme itself is under legal review in
  #11.

### Known limits of forms (UI-1b)

- **Auto-save does not run when the tab is closed** (Power Apps saves then too); the browser's unload prompt asks instead.
- **Mapped columns are filled only by New from a one-to-many subgrid**
  (#143). New from a many-to-many subgrid and
  `Xrm.Navigation.openForm` with `createFromEntity` (still refused) fill nothing. There is no quick create form: New opens
  the main form.
- **Table and column maps.** `entitymaps` and `attributemaps` list only the maps a solution imported: the lookup map of
  each one-to-many relationship is applied by `InitializeFrom` but has no row, and the other system maps of the seeded
  tables (for example the address and phone columns from `account` to `contact`) are not seeded, so they are not applied.
  Maps cannot be created, changed or deleted through the API (400). See [Solutions](solutions.md) for what import checks.
- **`InitializeFrom` runs no plug-in step** (it is not a pipeline message here). The 400 for two tables with neither a
  relationship nor a map was not observed in Dataverse: its text is xrmverse's own.
- **Related tab items that are not relationships** (`NavBarItem`: pages, web resources) are listed but not opened.
- **`Recommended` columns get no marker**, only the levels that block saving.
- **Hidden, not disabled.** A command without the privilege disappears; so does a [custom command](#custom-commands) whose rules are false.
- **Header edited inline**, not in a flyout.
- **The control follows the column's metadata**, so a classid asking for another presentation of a plain column is
  ignored.
- **`rowspan` sets only the height of multi-line text fields and web resources**; other cells ignore it.
- **Create-time field security is not detectable**: a column the user may not create is not marked read-only on the
  create form. A secured column the server leaves out of the read shows as "no access".
- **Field PCF controls run since UI-1d** (see [Known limits of PCF controls](#known-limits-of-pcf-controls-ui-1d)).
  Form scripts run since UI-1c (see
  [Known limits of web resources and form scripts](#known-limits-of-web-resources-and-form-scripts-ui-1c)).
- **The timeline runs since UI-1g** (see [Known limits of the timeline](#known-limits-of-the-timeline-ui-1g)).
- **The classids of the quick view, web resource, IFrame and timeline controls, the `DisplayConditions`, header, footer
  and `QuickForms` shapes and the `RecordsPerPage` default are written from memory** of the documented interface; a
  wrong one shows as an explicit "unknown control" placeholder, never silently. The checklist compares them.
- **A self-referential many-to-many** subgrid shows the record side (`Entity1`) of the intersect.
- **The extra read on `appmodule`**: `RetrieveAppComponents` checks the caller's read on `appmodule` and reads the
  components with system context, as Dataverse treats app components as app metadata.
- **The save error text is "{code}: {message}"**, not Dataverse's dialog.
- **Form security matches roles by role id.** A form whose `DisplayConditions` name a role is listed for users holding
  that role row; as for apps, a user holding a child business unit's copy of the role would not match if roles were ever
  copied per business unit.
- **Sign out in development sign-in mode does not ask about unsaved changes.** It signs out at once and the form's
  changes are lost. Entra and local sign-in leave the page, where the browser's unload prompt asks first.

### Known limits of web resources and form scripts (UI-1c)

- **App settings for asynchronous handlers** (#129):
  - only the definitions `AsyncOnLoad` and `AsyncOnSave`, and the timeline's `xrmverse_TimelineGroupByDate` and
    `xrmverse_TimelineRememberFilters` (see [Timeline](#timeline)), are seeded. A solution's setting of another definition
    (`AppChannel`, for example) is a warning in the import log and is not stored;
  - `settingdefinition` keeps `uniquename`, `displayname` and `defaultvalue` only: `datatype`, `overridablelevel`,
    `isoverridable` and the rest of Dataverse's columns are not modelled, so a setting cannot be overridden per user or
    per environment;
  - the object type codes of `settingdefinition` (9966) and `appsetting` (9965) are xrmverse's: Dataverse ships the
    tables in a solution and gives them a code of 10000 or more that differs per environment;
  - the import replaces an app's settings with those of its file: a re-import without `<appsettings>` turns them off. The
    export writes the app's `<appsettings>` block as it was imported, so a change made later through the Web API is not
    exported. An `<appsetting>` without `settingdefinitionid.uniquename`, or two for the same definition, is a 400
    `InvalidArgument`;
  - the OnLoad event has no event arguments (`getEventArgs()` is `null`), so an OnLoad handler cannot call
    `disableAsyncTimeout()`: its promise always gets 10 seconds;
  - the shell reads the settings when it lists the apps: a change shows after the app list is loaded again (a new page load).
- **`executeMultiple` without a change set runs the requests one by one**: the first failure stops the rest and rejects,
  and the requests already run stay done. With a change set (a nested array), the whole list goes as one Web API `$batch`
  ([Bulk operations](bulk-operations.md#web-api-batch)): each change set commits or rolls back as a whole, and the first
  failure rejects with its error. A Retrieve or a function in a change set is refused, as Learn says GET is.
- **`dateFormattingInfo` has no long patterns**: `LongDatePattern`, `LongTimePattern`, `FullDateTimePattern`,
  `MonthDayPattern`, `YearMonthPattern` and `ShortestDayNames` throw "not available". `MonthNames` and
  `AbbreviatedMonthNames` have 13 entries, the last empty, as .NET's `DateTimeFormatInfo`; not compared with Dataverse.
- **`Xrm.Panel`, `Xrm.App`, `Xrm.Device`, `Xrm.Encoding` and `Xrm.Copilot` throw "not available"** when a script reads
  them (#132).
- **`addNotification` shows its messages but not its actions** (the quick fixes).
- **`getVersion()` is `9.2.0.0`**, the Web API version xrmverse speaks. A script that checks for a later build takes the
  older branch.
- **`getControlType()`** follows Learn: `choice` for a choice and `choices` for a multi-select choice, and `standard`
  for a control xrmverse does not recognise (the form already shows an "unknown control" placeholder there). Scripts
  that compare with an older value take the other branch; the checklist compares the values with a real environment.
- **`getAttributeType()` throws "not available"** for a column type Learn's list does not include, so a script that
  walks every attribute fails on such a form, with a notification.
- **Header and footer control names** (`header_{column}`, `footer_{column}`) are written from memory: Learn names only
  `header_process_` controls.
- **`getDataXml()` throws "not available"**: Learn does not document the XML it returns.
- **A prevented or failed `data.save()`** rejects with `errorCode` 0 and xrmverse's own message: Learn documents the
  shape, not the values.
- **`formSelector.getCurrentItem()` is `null`** when the table has a single form, as Learn states.
- **Web API values from memory:**
  - `retrieveRecord`, `retrieveMultipleRecords`, a CRUD retrieve and functions ask for every annotation, so formatted
    values and lookup names come back;
  - an enumeration parameter of a function is written as `Microsoft.Dynamics.CRM.{Type}'{value}'`;
  - `createRecord`, `updateRecord` and `deleteRecord` return a bare lower-case id, `lookupObjects` and lookup values
    `{GUID}` upper case, `userSettings.userId` `{GUID}` upper case, role, organization and currency ids bare lower case;
  - the CRUD request property names of `execute` come from Learn's samples;
  - `retrieveMultipleRecords` without `maxPageSize` asks for 5,000 rows, Learn's default for a standard table.
- **Global context:** `userSettings.isRTL` is true for Arabic, Hebrew, Urdu and Persian (1025, 1037, 1056, 1065: from
  memory); `organizationSettings.languageId` is the number 1033 (Learn types it as a string, and xrmverse has no base
  language column); `uniqueName` is the organization's name; `defaultCountryCode` is empty; `prependOrgName()` returns
  the path unchanged, because xrmverse serves one organization at the root of its address;
  `getCurrentAppProperties()` gives the app web resource and welcome page as `null`.
- **`getEntityMetadata()`** gives Learn's shape for what xrmverse models (labels in the user's language, `Attributes` as a
  collection, `OptionSet`, the State and Status helpers); a member it cannot fill in Learn's type throws "not available".
  The multi-select choice `DefaultFormValue` is a number, where Learn types it as a Boolean.
- **`lookupObjects()`** applies a view's own filters; a view that filters on a related table rejects with
  "not available" rather than widen the search. A `link-entity` with no filter in the view is ignored, so an inner join
  that only restricts rows to those with a related row does not restrict them: the dialog can list more rows than the
  view does in Dataverse.
- **Scripts get the shell's objects.** `Xrm` belongs to the shell's window, so the promises and errors it returns come
  from the shell's JavaScript realm, not the library's: inside a library, `e instanceof Error` is false for an error from
  `Xrm` (`e.message` works). Check `typeof e.message` rather than `instanceof`.
- **`openForm()` resolves once the form opens**, with no value. Learn's success callback does not run for a form either;
  quick create is not offered (it throws "not available").
- **Form parameters** (`openForm` and `extraqs`) that name no column of the form, or do not fit it, are ignored with a
  warning on the form that lists them. Learn says they cause an error.
- **`openErrorDialog()` with only an `errorCode`** shows the code and xrmverse's generic text; Dataverse looks up a
  message for known codes.
- **IFrame controls:** the `Data` parameter, the `Scrolling` values and the web resource name in `Url` are read from
  memory of the FormXml; the table type code (`type`) is not passed with the record parameters (Learn recommends
  `typename`); `getObject()` is the IFrame element and `getContentWindow()` resolves its window, as Learn states; any
  same-origin `Url` is not sandboxed, as in Dataverse, so such a page reaches `parent.Xrm`. The IFrame's
  OnReadyStateComplete event is not raised (its methods throw "not available").
- **`window.close()` in a dialog.** The shell replaces the page's `close` as soon as the frame exists and after every
  load. A script that calls `close()` in the first milliseconds of parsing, before that, is ignored by the browser and
  the dialog stays open (its Close button still works).
- **Content types** of ICO (`image/x-icon`), XSL, RESX and XML (`text/xml`) are xrmverse's choice: Learn lists the types,
  not their content types.
- **Errors are xrmverse's own text.** The "Script error …" notifications and the "not available" messages are xrmverse's
  wording; only the codes of Web API errors match Dataverse.
- **Scripts that work on the Power Apps page structure** (`document.getElementById` on the form, `window.top.document`)
  are not supported, as in Dataverse. Each library runs in its own hidden window, not in the form's page.

### Known limits of PCF controls (UI-1d)

- **Not available in the context:** `mode.setFullScreen`, `device`, `factory.getPopupService`, `events` and `copilot`
  throw "… is not available in this version". So does `formContext.getControl(name).getOutputs()`: Learn does not
  document the shape of its dictionary.
- **Dataset controls** (a manifest with a `data-set`) run only on form subgrids, since UI-1e (see
  [Dataset controls](#dataset-controls)); in a field cell they fall back with a notice.
- **`getControlType()` returns `customcontrol`** for a field a PCF control renders, as read in the Power Apps
  documentation; not compared with Dataverse.
- **The `customcontrol` object type code is assumed to be 66**, equal to the solution component type 66 (Custom Control).
- **Files come from xrmverse's own route** `/Controls/{name}/{path}`, not from the platform's resource URLs; a control
  that builds its own address to its files (rather than using `getResource`) may not find them.
- **The platform libraries are the app's versions** (React 18.3.1, Fluent UI 9.75.1), not the versions the manifest
  names: a control relying on a React 16 behaviour removed in 18 may differ.
- **The control state** of `setControlState` lives in memory for the session (per table and control); it is not stored.
- **A control is destroyed when its cell goes away** (another tab is selected, the field is hidden) and starts again with
  a new `init` when it comes back.
- **A bound property without a parameter** in the FormXml is bound to the cell's column when it is the first bound
  property; an input property without a literal gets its manifest `default-value`, else `null`.
- **Static input literals** of a number type (`Whole.*`, `Decimal`, `FP`, `Currency`) and of `TwoOptions` are converted to a
  number or a boolean; any other type stays text.
- **Formatting uses the shell language** (en-US or it-IT) through the browser's `Intl`, not the user's number formats.
- **An output notified while the form is saving is dropped**: a `notifyOutputChanged` call during a save writes nothing; the
  control has to notify again after the save.
- **`notifyOutputChanged` reads `getOutputs()` at once**, inside the control's own call (Power Apps reads it later). The
  column's OnChange handlers therefore run before `notifyOutputChanged` returns to the control.
- **`parameters.*.security.readable` is always true**; `editable` and `secured` follow the column and the user.
- **`utils.hasEntityPrivilege` reads the user's own role privileges** (`RetrieveUserPrivileges`); privileges that come only
  from a team's roles are not counted.
- **Solutions imported before UI-1d have no `customcontrol` rows**: earlier versions kept the `Controls/` entries without
  reading them. Import such a solution again to get its controls.

### Known limits of dataset controls (UI-1e)

- **`filtering`, `linking`, `addColumn` and `retrieveRecordCommand`** of the DataSet throw "… is not available in this
  version": a control cannot add filters, linked tables or columns to the view, nor read record commands.
- **`property-set` is not supported**: a manifest whose `data-set` declares one falls back to the classic grid with
  "property-set is not supported in this version".
- **Dashboards** run their dataset controls since UI-1f (see [Dashboards and charts](#dashboards-and-charts)); on a
  dashboard the control has no parent record.
- **`formContext.getControl(subgrid).getGrid()`** comes in a later stage; it throws "not available".
- **Sorting applies the first `sorting` entry only**; further entries are ignored.
- **The other manifest properties** of a dataset control are given as literals: the FormXml's value, else the
  `default-value`; they are not bound to columns.
- **The view picker and quick find** of the subgrid's `data-set` parameters (`EnableViewPicker`, `EnableQuickFind`) are not
  offered: the control lists the subgrid's view.

### Known limits of custom commands (UI-1e)

- **Grid command bars:** `Mscrm.HomepageGrid.*` and `Mscrm.SubGrid.*` locations are ignored; lists and subgrids have no
  custom command bar yet.
- **`HideCustomAction` is not applied:** each one shows "Hiding '{Location}' is not supported in this version".
- **`Url` actions are not run:** their command is not shown, with a warning.
- **Other rules** (`SelectionCountRule`, `RecordPrivilegeRule`, `PageRule` and the rest) make their command unavailable,
  with a warning, as do platform rules (`Mscrm.*`) a diff only references.
- **Modern commands** (`appaction` rows of the command designer) are not applied (#184), such as OpenProject365's single one
  (`op365_project_savebaseline`, Save Baseline on the project form).
- **`Image16by16` web resource icons** are not shown; only the `ModernImage` names above have an icon.
- **`getGrid()` and `Xrm.Utility.refreshParentGrid`** still throw "not available"; they moved to a later stage.
- **Basic User can read `solution` rows**, including the kept XML, because the route reads them as the caller. That kept XML is not only ribbons: it also holds the
  archive files the import did not consume (base64 `Files` in `xrmverse_unsupportedxml`), readable the same way.
- **A library added by a later solution** while a form is open loads only when the form opens again: the form's ribbon
  script host is created once, with the libraries known then. Until then its command warns "its library was added after
  the form opened; open the form again".

### Known limits of dashboards and charts (UI-1f)

- **Dashboards are `systemform` rows (ruling F1).** A solution imported before UI-1f keeps its `<Dashboards>` in the
  solution's kept XML, not as rows: import it again to get its dashboards.
- **Charts are `savedqueryvisualization` rows (ruling F2).** The object type code 1111 is assumed to be Dataverse's; it is
  not yet checked against a real environment. A solution imported before UI-1f keeps each table's `<Visualizations>` in the
  solution's kept XML, not as rows: import it again to get its charts. Until then its export writes the kept charts back;
  once the table has chart rows, the kept charts whose id is no row are written after the rows, in one `<Visualizations>`.
- **A dashboard imported without kept XML is exported bare:** a dashboard row that has no `<Dashboard>` element kept from
  its solution file (for example one created in xrmverse) is exported from the row's data only: its `FormId`, its
  `FormXml`, and its base-language name and description (when the row has one). Labels in other languages, `IsDefault`,
  `IntroducedVersion` and the other elements of a real export are not written.
- **One group-by per chart:** a chart with more than one group-by (a second `groupby="true"` attribute or a second
  `<category>`, as Dataverse uses for a series per value) is not drawn; it shows "Charts with more than one group-by are not
  supported in this version." and the view's grid.
- **Charts on forms are not drawn:** a main-form subgrid with `ChartGridMode` `Chart` shows its grid with the notice "This
  subgrid's chart is not shown on a form in this version; its records are listed instead.". Drawing it needs the parent
  record's filter on the chart query, which is not built.
- **No interactive dashboards (ruling F3):** a dashboard with `FormPresentation` 1 or `stream` cells shows "Interactive
  dashboards are not supported in this version" and is laid out by the classic rules.
- **No chart interaction (ruling F4):** clicking a chart point (drill-down), switching a cell between chart and grid, and the
  chart and view pickers are not available; `EnableChartPicker` and `EnableViewPicker` show one notice.
- **The chart query (ruling F5)** takes the view's filters and links, never its columns or order. Joining a view's
  one-to-many `<link-entity>` into the aggregate can count a record once per linked row, which multiplies `count` and `sum`,
  and the view's `distinct` is not carried into the chart query. Whether Dataverse joins the view's links into a chart the
  same way is to be verified on a real environment.
- **No personal dashboards (ruling F6):** `userform` rows are out of scope; the selector lists system dashboards only.
- **Dashboard names are the base-language name.** The selector and the heading show `systemform.name`, which the import
  takes from the base-language label; they are not localized per user.
- **Dashboard security roles are not applied** ("Enable security roles"): every dashboard of the app is listed to every
  user of the app.
- **An app with no dashboard components** lists the dashboards its sitemap opens. Dataverse may instead offer all
  dashboards in that case; this is to be verified.
- **Tab labels are not drawn** on dashboards.
- **Export order:** charts and dashboards are exported sorted by id, as views and forms are, not in the order of the
  imported file.
- **At most 5000 categories:** a chart asks for 5000 groups; when there are more it draws the first 5000 with the notice
  "Only the first 5000 categories are shown.".
- **Combo charts** (series of different types) are drawn as the first series' type, with a notice.
- **Section columns written as digits:** a multi-digit section `columns` value (for example `"12"` or `"111"`) is read as
  one column per digit, with the digit as its relative width. This follows how exports are written; Microsoft Learn does
  not document it. Several digits with a `0` (such as `"10"`) are read as one column.
- **Aggregate formatted values:** a date grouping by month, week, quarter or year shows the integer (such as `11` for
  November); a grouping by day shows `M/d/yyyy` in UTC. A money aggregate, and a group-by on a money column, are formatted
  in the base currency.

### Known limits of the timeline (UI-1g)

- **Posts are turned on per app**, not per table: `xrmverse_TimelinePosts` stands in for Dataverse's Post
  Configurations and the form's "Enable user posts", which xrmverse does not read. With the option on, every timeline of the
  app shows posts.
- **No auto posts** and no post rules or post configuration tables (`msdyn_postconfig`, `msdyn_postruleconfig`,
  `postruleconfig`): creating a post with `source` 1 (Auto Post) or 3 (ActionHub Post) is refused. An existing auto post
  shows as "Auto post" without its content. An ActionHub post matches neither Posts by option: it shows only when Posts by
  is empty. (#213)
- **No likes or follows** (`postlike`, `postfollow`). (#214)
- **No attachments** on posts or replies (`activityfileattachment`). (#215)
- **No `#` references** to records (accounts, contacts and other tables) inside a post; only users can be @mentioned.
  (#216)
- **No personal wall** ("What's New" with the posts that mention the user) and **no mention notifications**.
  (#217)
- **Plain text posts only**, no rich text. (#218)
- **Posts cannot be edited**: Learn lists no Update message for `post` and `postcomment`, so the timeline offers Delete only.
- **Mentions are written with the type code `8`**, not the logical name `systemuser`; Learn accepts both, and which form
  Dataverse writes is not yet verified.
- **Reading a post needs read access to its record** (our call, stricter than what Learn documents), and so does deleting
  one: a user with `prvDeletePost` still cannot delete a post of a record they cannot read. Delete is offered only on the
  user's own posts and replies.
- **Errors of Reply and Delete** show above the list, not inside the reply box.
- **The @ list searches the full name and the email address.** xrmverse's `systemuser` has no `firstname` or
  `lastname` column, so a user is found by the start of the full name, of a later word of it, or of the email address.
- **Activity rollup** (Extended or Related, for accounts and contacts) is not applied: the timeline lists only the
  activities whose Regarding is the record.
- **Card forms and the sort date column** are not read: every item uses xrmverse's own card, and the sort is by
  `modifiedon` for activities and notes.
- **Pin and unpin** are not offered.
- **Email reply, reply all and forward** are not offered; an email is written and sent on its form (see
  [Email form](#email-form)).
- **Notes use a plain multi-line editor**; the rich text editor and its configuration URL are not supported.
- **Seeded forms for some activity tables only.** xrmverse seeds a default main form for `email`, `task`, `phonecall`
  and `appointment` (see [Default forms and views](email-and-queues.md#default-forms-and-views)). The other system
  activity tables (fax, letter, recurring appointment and the rest) have none, so + offers them but the new activity
  opens "No form of this table is available in this app." until a main form of the table exists (from a solution, or a
  `systemform` row created through the Web API). Custom activity tables use their own forms.
- **No quick create form**: "create an activity" opens the activity's main form (as for any New, see
  [Known limits of forms](#known-limits-of-forms-ui-1b)).
- **The timeline's settings in FormXml are not read.** Learn documents them in the form designer but not how FormXml
  stores them. A timeline with no `<parameters>` uses Learn's defaults (all record types, 10 per page, newest first,
  filter pane on, search on, items collapsed). One with `<parameters>` uses the same defaults and shows a notice it can
  dismiss: "This timeline has settings xrmverse does not read yet. It uses the default settings." The console names the
  parameters. So fax and letter are always left out, and a timeline set to show fewer record types still shows them all.
- **Activity privileges are the shared ones** (`prvCreateActivity`, `prvReadActivity`, `prvWriteActivity`,
  `prvAssignActivity`, `prvDeleteActivity`), as in Dataverse and xrmverse 2.11; the timeline also accepts a per-table
  privilege, for when per-table activity privileges come (#66).
- **Complete is one click**, with no dialog: it sets the Completed state and its default status reason.
- **The note entry takes one file.** Only the first of several dropped files is used.
- **Download attachment** reads the note's `documentbody` and saves it in the browser (the note body has no `$value`
  route, see [Activities and notes](activities.md)).
- **Remembered filters live in the browser** (when `xrmverse_TimelineRememberFilters` is on): another browser or device
  starts from the defaults, and clearing the site data forgets them. The search text is not remembered. Learn's "Remember
  my filters" command is not offered: the option decides.
- **Items of the same second are ordered by id.** The server answers `modifiedon` in whole seconds, so an activity and a
  note changed in the same second may come in either order. "Load more" still shows each item once.
- **The Due group** (when `xrmverse_TimelineGroupByDate` is on) shows up to 50 overdue activities; "Show all overdue"
  then sets the Overdue filter.

### Known limits of time zones and date formats (#131)

- **Formats only, no region.** Personalization sets the date and time formats; there is no region list ("current
  format"), no number or currency format, no long date format, and the time zone and the AM/PM designators are set
  through the Web API or the SDK. `localeid`, `dateformatcode` and `timeformatcode` are not modelled.
- **Month names are not read when typed.** A pattern with `MMM` set through the API is shown, but the form reads such a
  date only as `YYYY-MM-DD`.
- **Other dates stay in en-US and UTC:** aggregate values grouped by date, audit history and the SQL endpoint.
- **The rules come from the host's time zone database.** `timezonerule` and the daylight names are not modelled; each code
  maps to an IANA zone, so a rule change can land at a different time than in Dataverse. Five codes have a current bias
  different from the one observed in Dataverse (59, 129, 133, 195, 304); the retired codes 13, 30, 251, 256 and 266 use
  today's rules of their region; code 75 is a fixed UTC−2. The names in `userinterfacename` are the host's own names of
  those zones.
- **Skipped and repeated local times** follow xrmverse's rule (forward by the gap; standard time), not compared with
  Dataverse.
- **`UtcTimeFromLocalTime` has no Web API route** (Learn lists none). Neither message can carry a plug-in step: they run
  outside the pipeline.
- **The record editor of the web admin stays UTC**; its hint names the behaviour.
- **Form parameters** (`extraqs`, `openForm`) with a date are read as the user's local wall clock.

## See also

- [Web admin](web-admin.md)
- [Solutions](solutions.md)
- [Activities and notes](activities.md)
- [Security](security.md)
- [Field security](field-security.md)
- [Known issues](known-issues.md#model-driven-apps)
