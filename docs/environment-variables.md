# Environment variables

[← xrmverse documentation](../README.md#documentation)

## Introduction

An **environment variable** is a named setting that a solution ships, such as a service URL or an approver's email, whose
value changes from one environment to the next. The solution carries the **definition** (name, type, default value) and,
optionally, a **value**; an administrator sets the value of each environment after the import, and plug-ins, flows and form
scripts read it at run time. The Microsoft documentation describes the feature in
[Environment variables overview](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables) and the
table references of
[environmentvariabledefinition](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/environmentvariabledefinition)
and
[environmentvariablevalue](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/environmentvariablevalue).
xrmverse keeps the same tables, solution files and plug-in interface, so a solution that uses environment variables imports
and its code reads them unchanged. This page says what xrmverse does and where it stops.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Tables and solution files | `environmentvariabledefinition`, `environmentvariablevalue`, the same folders in a solution | Same |
| Plug-in interface | `IEnvironmentVariableService.RetrieveEnvironmentVariableValue` | Same |
| Secret variables (Azure Key Vault) | Refused | **Narrower** |
| Data source variables | Stored as text, nothing resolves them | **Narrower**: no connection references |
| Type validation | None: values are stored as text | **Narrower** |
| Values in solutions | Travel with their definition; not a component of their own | **Differs**: a value is component 381 in Dataverse |
| Web admin | No page: use `xrmd envvar`, the Web API or the SDK | Not applicable |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Ship a variable | In a solution | [Solutions](#solutions) |
| Set the value of an environment | `xrmd envvar set` | [The CLI](#the-cli) |
| Read it in a plug-in | `IEnvironmentVariableService` | [Plug-ins](#plug-ins) |
| Read it from any client | Query the two tables | [The tables](#the-tables) |

## The tables

Every organization has two user-owned tables with the Dataverse names (details in [System tables](system-tables.md)):

| Table | Entity set | Type code | Columns |
|---|---|---|---|
| `environmentvariabledefinition` | `environmentvariabledefinitions` | 380 | `schemaname` (required), `displayname`, `description`, `defaultvalue`, `type`, `isrequired`, `secretstore`, `valueschema`, `introducedversion`, `hint` |
| `environmentvariablevalue` | `environmentvariablevalues` | 381 | `schemaname`, `value` and the required lookup `environmentvariabledefinitionid` |

`type` is a choice: 100000000 String (the default), 100000001 Number, 100000002 Boolean, 100000003 JSON, 100000004 Data source,
100000005 Secret. The relationship `environmentvariabledefinition_environmentvariablevalue` cascades Delete, Assign and
Reparent: deleting a definition deletes its value.

They are ordinary tables for every read path: the SDK, SOAP, the Web API, FetchXML and QueryExpression, including the usual
helper code that queries the definition with an outer link to its value. The Basic User role reads both at Global depth, so
plug-ins and form scripts read them as the calling user; System Administrator and System Customizer hold every privilege on
them.

## Writing rows

Clients create, update and delete both tables, as on Dataverse. The server refuses with a 400 that names the problem:

- a definition whose `schemaname` equals another one's without regard to case, or a change of `schemaname` after create;
- a value that names no existing definition, or a second value for a definition that already has one.

A value created without `schemaname` takes its definition's. The text of a value or default is not checked against `type`.

## Solutions

An import reads each `environmentvariabledefinitions/{schemaname}/environmentvariabledefinition.xml` (see
[Solutions](solutions.md) for the full rules):

- The definition row is found by `schemaname` without regard to case and updated, or created. It is a component of the
  solution (type 380) even when `solution.xml` does not list it, and it is layered like the other row components: a managed
  uninstall deletes it and its value; uninstalling an upper managed layer brings back the definition of the layer below and
  leaves the value as it is.
- An `environmentvariablevalues.json` beside it creates the value row, or overwrites the existing value (the file wins).
  Without that file, a value already in the organization, for example one set with `xrmd envvar set`, is kept. A value is not
  a component of its own.
- Labels in other languages, `iscustomizable` and elements xrmverse does not model are kept for the export.
- After the import, the job log has one warning for each definition left with neither a value nor a default, telling you to
  set one with `xrmd envvar set`.

An export writes each definition of the solution back as the same folder, with `environmentvariablevalues.json` when the
definition has a value row, and lists it as a root component of type 380. Export and import into another organization carry
definitions and values over unchanged.

## Plug-ins

A plug-in gets `IEnvironmentVariableService` from its service provider:

```csharp
var variables = (IEnvironmentVariableService)serviceProvider.GetService(typeof(IEnvironmentVariableService));
var url = variables.RetrieveEnvironmentVariableValue("new_ServiceUrl");
```

`RetrieveEnvironmentVariableValue` returns the value row's `value`, else the definition's `defaultvalue`, else null. It reads
as the system user, so it works whatever the caller may read. An unknown schema name fails the plug-in with
`Environment variable '<name>' was not found.`; a definition of type Secret fails it too (see below).

## The CLI

`xrmd envvar` lists the variables and sets their values over the Web API as the signed-in user (see [CLI](cli.md#environment-variables)):

```bash
xrmd envvar list                                   # schema name, type, default value, current value
xrmd envvar get new_ServiceUrl                     # one variable, with the value in effect
xrmd envvar set new_ServiceUrl https://example.com # creates the value row or updates it
xrmd envvar unset new_ServiceUrl                   # deletes the value row: the default applies again
```

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- **No web admin page.** Set values with `xrmd envvar`, the Web API or the SDK.
- **No type validation.** A value or default is stored as text whatever `type` says; `valueschema` is stored and not checked.
- **Secret variables are not supported.** On Dataverse a Secret variable points to Azure Key Vault, which xrmverse does not
  support. The plug-in service fails for a Secret definition (`Secret environment variables need Azure Key Vault, which
  xrmverse does not support.`), and `xrmd envvar set` refuses one (exit 2). The SDK reference xrmverse builds against (1.2.27)
  has no method for secret values, so there is nothing else to call.
- **One value per definition.** xrmverse refuses a second value row for a definition. This follows how the feature is used;
  it was not observed on Dataverse.
- **Data source variables are stored, nothing more.** xrmverse has no connection references, so a Data source variable is a
  row with text that nothing resolves.
- **Type codes assumed.** The object type codes 380 and 381 are taken to equal the solution component types of the two tables,
  as for custom APIs; not verified on Dataverse. Basic User's read access is also our reading, not an observation.
- **Restore keeps the value.** When a layer is removed and the definition is restored from the layer below, only the
  definition is re-applied; the value row stays as it is.
- **Values travel with their definition.** On Dataverse a current value is a solution component of its own (381) that can be
  left out of a solution, and Microsoft advises not to ship values. xrmverse does not track values as components: an export
  writes the current value whenever there is one, and an import of a file with a value replaces the target's value. Run
  `xrmd envvar unset` in the source organization (or remove `environmentvariablevalues.json` from the file) before moving a
  solution between environments that need different values. An upgrade whose new version has no value file leaves the old
  value in place, where Dataverse removes a value that came with the managed solution.
- **A value file needs its definition.** A solution whose `environmentvariabledefinitions/{name}/` folder holds only
  `environmentvariablevalues.json` is refused with a 400 naming the path.
- **`iscustomizable` of value files.** An export always writes `iscustomizable` "1" in `environmentvariablevalues.json` (an
  unmanaged export), so a value file whose `iscustomizable` was "0" comes back with "1".

## See also

- [Solutions](solutions.md)
- [Plug-ins](plugins.md)
- [CLI xrmd](cli.md#environment-variables)
- [Tables in every organization](system-tables.md)
