# Workflow studio

[← xrmverse documentation](../README.md#documentation)

## Introduction

The **workflow studio** is a separate app at `/studio/workflows` for designing classic workflows, actions and Entity-scope
business rules by pointing and clicking. It is a second editor of the same YAML definition that
[Workflow authoring as code](workflow-authoring.md) describes: there is no second model and no second compiler. The
studio reads and writes YAML, and the server, through the 2.21 authoring API, stays the only judge of what is valid.
What the studio saves is exactly what `xrmd workflow apply` would save from the same text.

The studio has its own shell and its own access. It does not open the web admin and it does not need an administrator
role: access is decided by the Dataverse privileges on the Process table (`workflow`), checked by the server. Its tile
appears on the apps page (`/apps`, in a **Tools** list) and in the web admin's header for users who can read processes.

The studio opens in the user's language and colour scheme, like the apps.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Authoring surface | a flow canvas and a code view on the same YAML | Differs: own editor, own look; Dataverse authors classic processes in its classic designer |
| Access | the Process privileges, enforced by the server | Same idea: no System Administrator or System Customizer role is needed |
| What is saved | the same designer XAML in the same `workflow` row as an import | Same format; the unconfirmed shapes are listed in [Assumed shapes](workflow-authoring.md#assumed-shapes) |
| Processes the YAML cannot express | opened read-only with the server's message | Extra: never an approximation |
| Business process flows, cloud flows, dialogs, form-scope business rules | not available | Narrower |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Give a user access | Process privileges plus a base role | [Access](#access) |
| Open the studio | the **Workflow studio** tile on `/apps`, or `/studio/workflows` | [The list](#the-list) |
| Start a process | **New** on the list | [The list](#the-list) |
| Build a flow | the canvas | [The canvas](#the-canvas) |
| Set a trigger, a value or a condition | the properties panel | [The properties panel](#the-properties-panel) |
| Edit the YAML | the **Code** tab | [The code view](#the-code-view) |
| See what is wrong | the problems pane | [Problems](#problems) |
| Save, activate or deactivate | the editor's header | [Saving and activating](#saving-and-activating) |
| Put a process in a solution | **Add to solution** | [Saving and activating](#saving-and-activating) |

## Access

The studio calls the [authoring API](admin-api.md#the-authoring-api) and the Web API as the signed-in user, so the user's
privileges decide everything, and a hidden button is never the only protection. A user needs:

| Privilege | For |
|---|---|
| `prvReadWorkflow` | opening the studio at all: the list, the editor, export and every check of a draft |
| `prvCreateWorkflow` | creating a process |
| `prvWriteWorkflow` | saving a change, activating and deactivating |
| `prvDeleteWorkflow` | deleting a process |
| `prvActivateSynchronousWorkflow` | activating a real-time workflow or an action (a business rule needs `prvActivateBusinessRule`; a background workflow needs neither) |

Add a base role such as **Basic User**: the studio reads the user's own settings (language and colour scheme), which that
role allows, as in Dataverse. No System Administrator or System Customizer role is needed. The full table of what each
operation needs is in [Privileges](workflow-authoring.md#privileges).

A user without `prvReadWorkflow` who opens `/studio/workflows` sees a message that names the missing privilege, and has
no studio tile on the apps page. A server that cannot run the feature (a PostgreSQL organization on a release before
2.7b answers 501; from 2.7b the studio works on PostgreSQL organizations as on SQL Server ones) shows the server's
message as it is.

## The list

`/studio/workflows` lists the processes the user can read: name, kind (workflow, action or business rule), table (`(global)`
for a global action), mode and state. A **Kind** filter and a **Table** filter narrow it, and a search box matches the name only. The command bar has **New**
and **Refresh**, and **Open**, **Activate**, **Deactivate** and **Delete** when a row is selected; Delete asks first. A server refusal
is shown as it comes.

A **Managed** badge marks a process that comes from a managed solution, and "N unsupported steps" a process that has steps
the YAML cannot express. Opening such a process is still possible (see [Read-only processes](#read-only-processes)).

**New** asks for the kind, the name (at most 100 characters), the table and, for a workflow, the mode (background or
real-time) or, for an action, the message (or a global action with no table). The editor then opens with an empty flow.

## The editor

The editor shows one document in two views, **Canvas** and **Code**, chosen with the tabs in the header. Switching never
loses data: both views edit the same YAML text, and each change in one view is derived into the other.

The header has the name, the kind, table and mode, the state badge, **Managed**, **Unsaved changes**, the view tabs, **Save**,
**Activate** or **Deactivate**, and a menu with **Export YAML**, **View XAML**, **Add to solution** and **Delete**. Leaving
a changed document asks "Discard your changes?".

### The canvas

The canvas draws the process as cards in a column, from the trigger card down. A condition shows its branches side by side
(**Then**, **Else if**, **Else**) and joins them again; a wait shows its **On timeout** branch.

- **Trigger card.** The first card shows the kind, the table, the mode and the events in words (created, "When name
  change", deleted, on demand, started by another workflow). It cannot be removed.
- **Add a step.** The **+** between two cards, and at the end of every branch, opens a palette of the steps that are valid
  there. A step that is not valid in that place (a wait in a real-time workflow, a rule action outside a condition) is not
  in the palette. The steps the library does not have are listed under **Not available** with their reason and cannot be
  added.
- **Move, cut, paste and delete.** A card's menu has Move up, Move down, Cut, Paste after and Delete; Delete asks first
  when the card has children. A card can also be dragged to another slot. A move follows the same place rule as the
  palette: a slot that does not take the card, or a step inside it, refuses it (no drop there, and Paste is disabled with
  the reason), so a rule action cannot leave its condition, and a card never moves inside itself.
- **Branches.** A condition card has **Add else if** and **Add else**; a wait card has **Add on timeout** (which needs a
  timeout).
- **Zoom and pan.** Zoom out, Zoom in (25 to 200 %) and **Fit to view** in the toolbar, Ctrl or Cmd with the wheel, and a drag on the empty
  canvas to pan.
- **Keyboard.** Cards take the focus; Enter or Space selects one.
- A card with a problem shows a badge, and a click on a problem in the problems pane selects its card.

An operation the document cannot take (for example, adding an on-timeout branch to a wait without a timeout) changes
nothing and shows why.

### The properties panel

Selecting a card shows its properties to the right of the canvas (below it on a narrow window).

- **The trigger card** edits the name, the description, the mode, the scope, the run-as setting, the events (create,
  update and delete, each with its stage on a real-time workflow), the columns of an update trigger, on demand, child
  process and the failure log or auto-delete setting; for an action, the message and its arguments. Switching a workflow
  from real-time to background removes the settings that only a real-time workflow has, and a real-time workflow is refused
  while the flow has a wait.
- **A step card** shows a form generated from the step's keys. Required fields are marked, enumerations are lists, tables
  and columns are picked from the organization's metadata, and a record is the primary record, a related record through a
  lookup, or a record created by an earlier step.
- **Values.** Each value is a literal typed by its column (a number, a choice with its options, a Yes/No with its labels, a
  date), the empty value, a column of the primary or a related or created record, a record, an output of an earlier step,
  an argument of the action, the time the workflow runs, a lookup to a chosen record, or one of the operators (add,
  subtract, multiply, increment, decrement, set, concat, first of). A shape the panel does not know is shown as "Other: edit
  in the code view" and is never rewritten.
- **Conditions.** A condition is a comparison, or a group (all, any) or a negation of conditions. A comparison is a column,
  one of the twelve operators of the format and, except for null and not null, a value typed by the column. A condition
  that cannot be built (a literal on the left) is not offered.

### The code view

The code view is a YAML editor with line numbers, syntax highlighting and undo. The server's problems appear in the text at
their line and column. Beside it, a **read-only live diagram** follows the text; while the text has problems, the diagram keeps
the last version that could be read, with a note. The properties panel is not shown in the code view.

Comments and the order of keys are kept while the document is edited, including by canvas operations, except on the nodes an
operation removes. The server stores XAML only, so comments, key order and layout do not survive a save: after a save the
editor keeps the text the user has, and a process opened again shows the server's export.

If the text cannot be read as a process (an unknown key, a step in the wrong kind, a syntax error), the canvas shows "Fix
the problems in the code view to see the diagram" and the code view stays usable.

### Problems

The problems pane lists the document's problems with a line number: first those the studio finds while reading the text,
then, from the server, the errors of a **dry run** (the same `apply` the CLI runs with `--dry-run`, which writes nothing) and
its [assumed-shape warnings](workflow-authoring.md#assumed-shapes). The dry run starts about 0.8 seconds after the last change
and once when the document opens. A click on a problem goes to its line in the code view or selects its card on the canvas.
The studio never accepts what the server refuses: **Save** is disabled while the text has problems the studio finds itself;
the server's problems do not block it: a save the server refuses keeps the text and lists the errors.

### Saving and activating

- **Save** sends `apply` for real. The first save of a new process puts its `id` in the document and opens it under its own
  address. An edit made while a save is running is kept and still counts as unsaved. If that edit left the text unable to
  take the `id` (for example a text that is no longer a mapping), the studio says so and keeps **Save** disabled until the
  text is fixed; the `id` then goes in, so a later save updates the process instead of creating a second one.
- **Activate** saves and activates in one call (`apply` with `activate`), so the two happen in one transaction; with unsaved
  changes, or on a new process, the studio asks "Save your changes first?". **Deactivate** saves, then deactivates.
  With no changes, either one only changes the state.
- **Add to solution** picks an unmanaged solution; the next save passes it (`--solution` of the CLI), then the choice is
  cleared.
- **Export YAML** downloads the current text. **View XAML** shows the stored XAML, read-only.
- **Delete** asks first; the process is deleted through the Web API.

### Read-only processes

A process the YAML cannot represent (a dialog, a step outside the library, a process with a non-zero rank) has no YAML: the
server answers the export with "not representable" (see [Export](workflow-authoring.md#export-and-not-representable)). The
studio opens it on a read-only page with the server's message as it is, **View XAML**, **Activate** or **Deactivate** and
**Delete**. It never shows an approximation, and never opens it as if it were editable.

## Relation to the apps

The studio shares the apps' sign-in, theme and languages. It is not a model-driven app and
has no site map; the apps page links to it, and its header links back to the apps.

## Known limits

- **No concurrent edit detection.** Two people saving the same process: the last save wins; the studio does not merge, and
  does not tell the first person that the process changed. There is no real-time co-editing.
- **Comments and layout do not survive a save.** The server stores XAML only; see [The code view](#the-code-view).
- **A process the YAML cannot represent is read-only.** There is no canvas for it.
- **Managed processes are not protected by the server.** The studio shows a **Managed** badge but does not block a save,
  and the server does not refuse an `apply` on a managed process today (see [Known issues](known-issues.md)).
- **No business process flows, cloud flows, dialogs or form-scope business rules**, as in
  [Workflow authoring as code](workflow-authoring.md#known-limits).
- **Values, tables and columns are judged by the server.** The studio checks only the shape of the text, so those
  problems appear after the dry run, not at once.

## See also

- [Workflow authoring as code](workflow-authoring.md)
- [Classic workflows](workflows.md)
- [Model-driven apps](model-driven-apps.md#what-the-app-shell-offers)
- [Web admin](web-admin.md)
- [Security](security.md#classic-workflows)
- [Known issues and limitations](known-issues.md)
