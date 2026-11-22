# Custom APIs

[← xrmverse documentation](../README.md#documentation)

## Introduction

A custom API is a message that a solution defines: a name, how it is bound, its request parameters, its response
properties and, usually, the plug-in type that does the work. xrmverse imports the custom API definitions of a solution
exported from Dataverse, lets you create and change them directly (see [Authoring](#authoring)), and runs them: they can
be called through the SDK (`OrganizationRequest` by name), the SOAP endpoint and the Web API, with the plug-in type as
the main operation. Custom actions (a custom `sdkmessage` row) keep working as before; see
[Plug-ins](plugins.md). XAML actions (classic processes of category 3) are a third kind of message; see
[Custom APIs, XAML actions and custom actions](#custom-apis-xaml-actions-and-custom-actions).

Microsoft's own description of the feature is on Learn:
[Create and use custom APIs](https://learn.microsoft.com/power-apps/developer/data-platform/custom-api).

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Import from a Dataverse export | APIs, parameters and properties of every binding and type | Same; Power Fx custom APIs are refused |
| Authoring | SDK, SOAP, Web API and CLI Creates | Narrower: no deep insert, managed APIs never changed in place |
| Calling | SDK by name, SOAP, Web API | Differs: Web API names are case-sensitive; only the `tag` optional parameter |
| Main operation | the registered plug-in type | Same idea; runs as a stand-in step |
| Business events, background operations | not available | Narrower |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Bring APIs from Dataverse | solution import | [What is imported](#what-is-imported) |
| Create an API | SDK, SOAP, Web API or `xrmd customapi create` | [Authoring](#authoring) |
| Call it | `OrganizationRequest` by name, or a Web API action/function | [Binding and calling](#binding-and-calling) |
| Understand an error | validation and error codes | [Validation and error codes](#validation-and-error-codes) |
| Add steps to its message | plug-in registration | [The main operation and steps](#the-main-operation-and-steps) |
| Inspect APIs | `xrmd customapi list`, MCP, web admin | [CLI, MCP and web admin](#cli-mcp-and-web-admin) |

## What is imported

The `customapis/` folder of a solution file, in the layout a Dataverse export writes:

- `customapis/{uniquename}/customapi.xml`, one per API;
- `customapis/{uniquename}/customapirequestparameters/{name}/customapirequestparameter.xml`, one per request parameter;
- `customapis/{uniquename}/customapiresponseproperties/{name}/customapiresponseproperty.xml`, one per response property.

Every binding (global, table, table collection), actions and functions, private APIs, all 13 field types, the three
values of `allowedcustomprocessingsteptype` and APIs without a plug-in type are supported. The plug-in type is named by
`plugintypeexportkey` (or `plugintypeid`), which must be the id of a plug-in type in the same file or already in the
organization; a workflow activity is refused.

The import fails as a whole (400 `0x80040203`, naming the API and what is wrong, nothing written) for:

- a Power Fx (low-code) API, recognized by `powerfxruleid` or `fxexpressionid`;
- a function that is also enabled for workflows;
- `isoptional` on a response property;
- a binding, step type or field type out of range;
- a bound API without `boundentitylogicalname`, a global API with one, or a table that is neither in the organization nor
  in the file;
- a `logicalentityname` on a type that takes none (only Entity, EntityReference and, for request parameters,
  EntityCollection take one), or naming an unknown table;
- two parameters or two properties with the same name (ignoring case), or a parameter named `Target` on a table-bound API
  (the server adds `Target` itself);
- a unique name that does not start with the prefix of an existing publisher followed by `_`, that has characters other
  than letters, digits and `_`, or that equals (ignoring case) a message the server handles itself or an existing custom
  action;
- a global API whose unique name is exactly the entity set name of a table of the organization or of the file (the Web
  API could not reach it);
- an unknown plug-in type, or a workflow activity;
- on a re-import, a change to a property that cannot change once created: `allowedcustomprocessingsteptype`,
  `bindingtype`, `boundentitylogicalname`, `isfunction` and `workflowsdkstepenabled` of the API, `isoptional`,
  `logicalentityname` and `type` of a parameter, `logicalentityname` and `type` of a property.

An `executeprivilegename` that names no privilege of the organization is imported, with a warning in the import job log:
nobody holds such a privilege, so only System Administrators and internal calls can run the API.

`workflowsdkstepenabled` is stored, and validated with `isfunction` at authoring (above), but a perform-action step does not
check it: a classic workflow's "perform action" step sends its request by message name through the pipeline, so it reaches a
custom API whatever the flag says (see [Classic workflows](workflows.md)). `statecode` has
no effect, as on Dataverse.

## Custom APIs, XAML actions and custom actions

Three kinds of message can be called by name with an `OrganizationRequest`. A name is unique across them: a XAML action whose
message name is taken by a custom API, and a custom API whose name is taken by an action's message, are refused.

| | Custom API | XAML action (category 3 process) | Custom action |
|---|---|---|---|
| Defined by | the `customapi` tables (authoring or the `customapis/` folder) | a `<Workflow>` of category 3 in a solution; its message is created at import and activation | an `sdkmessage` row |
| Request check | declared parameters, before stage 10 (400 `0x80040203`) | the XAML's `x:Members` arguments, with the same rules and code | none |
| Stage 30 | its plug-in type | the XAML, as the caller | nothing |
| Privilege | `executeprivilegename` | none beyond those of the steps | none |
| Response | completed to the declared properties | the outputs the XAML set | what the steps set |
| Web API | an action or function in `$metadata` | not exposed (`POST /<name>` is 404) | not exposed |

See [Classic workflows](workflows.md#actions) for the message name, the state rules and deletion of a XAML action.

## The tables

| Table | Entity set | Object type code | Contents |
|---|---|---|---|
| `customapi` | `customapis` | 9990 | `uniquename`, `name`, `displayname`, `description`, `bindingtype` (choice `customapi_bindingtype`: 0 Global, 1 Entity, 2 Entity Collection), `boundentitylogicalname`, `isfunction`, `isprivate`, `allowedcustomprocessingsteptype` (choice `customapi_allowedcustomprocessingsteptype`: 0 None, 1 Async Only, 2 Sync and Async), `executeprivilegename`, `workflowsdkstepenabled`, `plugintypeid` (lookup to `plugintype`), `sdkmessageid` (lookup to `sdkmessage`), `statecode`/`statuscode` |
| `customapirequestparameter` | `customapirequestparameters` | 9991 | `uniquename`, `name`, `displayname`, `description`, `customapiid`, `type` (choice `customapifieldtype`), `isoptional`, `logicalentityname` |
| `customapiresponseproperty` | `customapiresponseproperties` | 9992 | the same columns without `isoptional` |

All three are user-owned. Relationships: `plugintype_customapi` (Delete Restrict), `sdkmessage_customapi`,
`customapi_customapirequestparameter` and `customapi_customapiresponseproperty` (both Delete Cascade). The single-valued
navigation property from `customapi` to its plug-in type is `plugintypeid`.

Field types (`customapifieldtype`) and the SDK type a value must have:

| Value | Name | SDK type | Edm type in `$metadata` |
|---|---|---|---|
| 0 | Boolean | `bool` | `Edm.Boolean` |
| 1 | DateTime | `DateTime` | `Edm.DateTimeOffset` |
| 2 | Decimal | `decimal` | `Edm.Decimal` |
| 3 | Entity | `Entity` | the entity type of `logicalentityname`, else `crmbaseentity` |
| 4 | EntityCollection | `EntityCollection` | `Collection(crmbaseentity)` |
| 5 | EntityReference | `EntityReference` | the entity type of `logicalentityname`, else `crmbaseentity` |
| 6 | Float | `double` | `Edm.Double` |
| 7 | Integer | `int` | `Edm.Int32` |
| 8 | Money | `Money` | `Edm.Decimal` |
| 9 | Picklist | `OptionSetValue` | `Edm.Int32` |
| 10 | String | `string` | `Edm.String` |
| 11 | StringArray | `string[]` | `Collection(Edm.String)` |
| 12 | Guid | `Guid` | `Edm.Guid` |

Basic User reads the three tables at Global depth; System Customizer holds every privilege on them. Every organization
has these grants: one created before custom APIs receives them once at startup (see [Security](security.md)).

### Messages and filters

Each API owns one `sdkmessage` row (`name` = the unique name, `categoryname` `CustomAPI`,
`isprivate` = the API's flag) and one `sdkmessagefilter` row (the bound table, or `none` for a global API;
`iscustomprocessingstepallowed` is false when the API allows no steps). `customapi.sdkmessageid` points to the message.

## Authoring

The three tables are written like any other table, over the SDK, SOAP and the Web API, by a user who holds the Create,
Write or Delete privilege on them (System Administrator, System Customizer). A custom API made this way is callable by
the next request, with no restart, and `$metadata` lists it. The API's `sdkmessage` and `sdkmessagefilter` rows are
created, updated and deleted by the server; writing them directly, or creating an `sdkmessage` with an API's name, stays
refused with 400 ("the message and filter of a custom API are maintained by the server; write the customapi row
instead"). The prefix of the unique name may be that of any existing publisher, as for tables and columns; the solution
you add the API to need not belong to that publisher.

Create the API, then each request parameter and response property with its own Create; a parameter or property names its
API in `customapiid`. Every check below that fails is 400 `0x80040203` (`InvalidArgument`) naming the API, and the
parameter or property where there is one. Nothing is written when one fails.

### SDK

```csharp
var apiId = service.Create(new Entity("customapi")
{
    ["uniquename"] = "new_Ping", ["name"] = "Ping", ["displayname"] = "Ping",
    ["bindingtype"] = new OptionSetValue(0),                       // 0 Global, 1 Entity, 2 Entity Collection
    ["isfunction"] = false,
    ["allowedcustomprocessingsteptype"] = new OptionSetValue(0),   // 0 None, 1 Async Only, 2 Sync and Async
    ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),   // optional
});
service.Create(new Entity("customapirequestparameter")
{
    ["uniquename"] = "Text", ["name"] = "Text", ["displayname"] = "Text",
    ["type"] = new OptionSetValue(10),                             // String
    ["isoptional"] = false,
    ["customapiid"] = new EntityReference("customapi", apiId),
});
service.Create(new Entity("customapiresponseproperty")
{
    ["uniquename"] = "TextOut", ["name"] = "TextOut", ["displayname"] = "TextOut",
    ["type"] = new OptionSetValue(10),
    ["customapiid"] = new EntityReference("customapi", apiId),
});
```

### SOAP

The same `Create` requests as the SDK sends: `CreateRequest` with the `customapi` entity, then with the
parameters and properties.

### Web API

The lookups are set with `@odata.bind`: `plugintypeid@odata.bind` on the API and `CustomAPIId@odata.bind`
(the navigation property of the `customapiid` lookup) on a parameter or property. Add the header
`MSCRM.SolutionUniqueName: MySolution` to add the API to that solution (a parameter or property travels with its API).

```http
POST /api/data/v9.2/customapis
{ "uniquename": "new_Ping", "name": "Ping", "displayname": "Ping", "bindingtype": 0, "isfunction": false,
  "allowedcustomprocessingsteptype": 0, "plugintypeid@odata.bind": "/plugintypes(00000000-0000-0000-0000-000000000000)" }

POST /api/data/v9.2/customapirequestparameters
{ "uniquename": "Text", "name": "Text", "displayname": "Text", "type": 10, "isoptional": false,
  "CustomAPIId@odata.bind": "/customapis(11111111-1111-1111-1111-111111111111)" }

POST /api/data/v9.2/customapiresponseproperties
{ "uniquename": "TextOut", "name": "TextOut", "displayname": "TextOut", "type": 10,
  "CustomAPIId@odata.bind": "/customapis(11111111-1111-1111-1111-111111111111)" }
```

### CLI

`xrmd customapi create --file ping.json` does the same in one command; see [CLI](cli.md#custom-apis).

### Rules on Create

The same definition rules as the import (see [What is imported](#what-is-imported)) apply, with the
same refusals; in addition:

- the unique name is unique across custom APIs, ignoring case: "Custom API '{name}' already exists." Two concurrent
  Creates of one name serialize, and the second gets this error;
- a field needs `uniquename`, `customapiid` (an existing API) and `type`; a field refusal reads "Custom API '{api}' cannot
  take the request parameter '{p}': {reason}." (or "response property");
- `sdkmessageid` must not be given, with any value: the server creates the message and filter itself (message id and
  filter id as the import derives them) in the same transaction, so a Create that fails halfway leaves no message;
- `plugintypeid` must name an existing plug-in type that is not a workflow activity;
- a column left out is stored as an import stores it: `isprivate`, `workflowsdkstepenabled` and `isfunction` as false,
  `bindingtype` and `allowedcustomprocessingsteptype` as 0, and `isoptional` of a request parameter as false, so a
  Retrieve or GET reads back like an imported API;
- `boundentitylogicalname` and `logicalentityname` are stored in lower case and an empty one as none; an Update that
  repeats the stored value in another case, or an empty one for none, is not a change;
- an `executeprivilegename` that names no privilege of the organization is accepted, with the warning the import logs in
  the server log (no user holds it, so only System Administrators can call the API);
- `RelatedEntities` on the Target of the Create (deep insert) is refused: "Creating related records in the same request is
  not supported for {table}: create the request parameters and response properties with their own Create." A Web API
  body that nests the parameters in the API is refused too, 400, because `customapi` has no collection-valued navigation
  properties. Create the fields with their own requests.

### Cannot be changed after Create

An Update that repeats the stored value is accepted (`boundentitylogicalname` and
`logicalentityname` in any case, or empty for none); a different value, a case
change of `uniquename` included, is refused: "{column} of custom API '{name}' cannot be changed once created." For a
parameter or property the name reads "'{api}' request parameter '{p}'" (or "response property").

| Table | Immutable columns |
|---|---|
| `customapi` | `uniquename`, `bindingtype`, `boundentitylogicalname`, `isfunction`, `allowedcustomprocessingsteptype`, `workflowsdkstepenabled`, `sdkmessageid` |
| `customapirequestparameter` | `uniquename`, `type`, `isoptional`, `logicalentityname`, `customapiid` |
| `customapiresponseproperty` | `uniquename`, `type`, `logicalentityname`, `customapiid` |

You can change `name`, `displayname`, `description`, `isprivate` (also written to the API's message), `executeprivilegename`
and `plugintypeid` of an API, `name`, `displayname` and `description` of a field, and `ownerid`, `statecode` and
`statuscode` as for any user-owned row. Because `customapiid` cannot change, Associate and Disassociate on
`customapi_customapirequestparameter` and `customapi_customapiresponseproperty` are refused, even to the same API.
`isoptional` on a response property is refused as an unknown attribute (400), and so is `iscustomizable` on any of the
three tables: it is not a column in xrmverse (a managed file sets it, see [Solutions](#solutions)).

Adding a required parameter to an API that callers already use works at once: the very next call without it gets the 400
of [Validation and error codes](#validation-and-error-codes).

### Delete

Deleting a `customapi` removes its parameters and properties, then its filter and message. It is refused while
a step (`sdkmessageprocessingstep`) is registered on its message: "Custom API '{name}' cannot be deleted: the step
'{step name}' ({id}) is registered on its message. Delete the step first." Delete the step, then the API. (Whether
Dataverse refuses too is not verified.) A parameter or property can be deleted alone.

### Managed APIs

An API that a managed solution delivered (`ismanaged` = 1) is not changed in place: any Update of it, and
any Create, Update or Delete of its parameters or properties, is refused: "Custom API '{name}' is managed: change it in
its solution and import an update." Deleting the API row is refused by the generic rule for managed components;
uninstall the solution instead. This is stricter than Dataverse, which lets a managed API that is marked customizable be
changed.

### Adding the API to a solution

With the `MSCRM.SolutionUniqueName` header the API
becomes a component of the solution, and an export writes the `customapis/` folder back from the rows; an authored API
exports and imports into an empty organization unchanged, with the plug-in type and its assembly in the solution too.
Calls by internal code (solution import, restore, cascade delete) skip these checks and keep the import's own.

### The plug-in that does the work

The plug-in type that does the work is built and registered as for any plug-in (see [Plug-ins](plugins.md));
registering it with the Plug-in Registration Tool is optional and needs that Microsoft tool, which you bring yourself.

## Binding and calling

### SDK and SOAP

An `OrganizationRequest` whose `RequestName` is the API's unique name (ignoring case), with one entry
per parameter; for a table-bound API, `Target` is an `EntityReference` to a record of the bound table. The response's
`Results` hold every declared response property, and its `ResponseName` is the request name as the caller spelled it.
Through `ServiceClient` this is an ordinary `Execute`:

```csharp
var response = client.Execute(new OrganizationRequest("op365_ExpireAgentProposals"));
var count = (int)response.Results["Count"];
```

A plug-in can call any custom API through its organization service, private ones included.

### Web API

The operation name is matched exactly (case-sensitive), like entity set names.

| Binding | Action | Function |
|---|---|---|
| Global | `POST /api/data/v9.2/{name}` | `GET /api/data/v9.2/{name}(p1=@a)?@a=…` |
| Table | `POST /api/data/v9.2/{set}({id})/Microsoft.Dynamics.CRM.{name}` | `GET` on the same path, with a parameter list |
| Table collection | `POST /api/data/v9.2/{set}/Microsoft.Dynamics.CRM.{name}` | `GET` on the same path, with a parameter list |

- Global operations also accept the `Microsoft.Dynamics.CRM.` prefix; bound operations require it.
- `{set}` must be the entity set of the bound table. Any other set, a bound operation without the prefix, a `GET` of an
  action and a `POST` of a function are all the usual 404 `0x80060888` for an unknown segment.
- For a table-bound API the record in the URL is the `Target`; a `Target` in the body or the parameter list is a 400.
- `{set}({id})/{navigation property}({key})` is the same 400 as `{set}({id})/{navigation property}`: navigation
  property paths are not served; use `$expand`.
- `tag` is passed in the query string, `?tag=value`, on actions and functions; the plug-ins get it as the request
  parameter `tag`. A `tag` in the body or the parameter list, or given twice, is a 400.
- Action bodies are JSON: numbers, strings, booleans, ISO dates, GUIDs as strings, string arrays as JSON arrays, Picklist
  as an integer, Money and Decimal as numbers (or strings with `IEEE754Compatible=true`). Records use the `crmbaseentity`
  form: `{"@odata.type":"Microsoft.Dynamics.CRM.account","name":"…"}` for an Entity, an array of those for an
  EntityCollection, and `{"@odata.type":"Microsoft.Dynamics.CRM.account","accountid":"…"}` (the type and the primary
  key only) for an EntityReference. An Entity or a record of an EntityCollection can leave out `@odata.type` when the
  parameter has a `logicalentityname`.
- Function parameters are inline literals or `@alias` values from the query string: `'text'`, numbers, `true`/`false`,
  `null`, bare GUIDs and dates, and JSON (with single quotes) for records and string arrays, for example
  `{'@odata.id':'accounts(…)'}`.
- Responses:
  - no response property: 204;
  - exactly one property, of type Entity: that record with `@odata.context` `$metadata#{set}/$entity` (204 when the
    plug-in left it unset);
  - exactly one property, of type EntityCollection: `{ "value": [...] }` with `@odata.context`
    `$metadata#Collection(Microsoft.Dynamics.CRM.crmbaseentity)`;
  - otherwise: 200 with `@odata.context` `$metadata#Microsoft.Dynamics.CRM.{name}Response` and one JSON property per
    response property.
- A function without response properties cannot be called on the Web API: 404 `0x8006088a`.

## Validation and error codes

Before anything runs (no privilege check, no step, no plug-in), the request is checked against the definition, in this
order. Every failure is 400 `0x80040203` (`InvalidArgument`) naming the API and the parameter.

1. A table-bound API needs `Target`, an `EntityReference` to the bound table. The record itself is not read: its
   existence and the caller's access to it are not checked.
2. Every parameter that is not optional must be present and not null.
3. Every value must already have the SDK type of the table above; nothing is converted (an `int` for a Picklist is
   refused, so is a string for a Guid). An Entity, EntityReference or EntityCollection parameter with a
   `logicalentityname` must name that table for every record.
4. Any other parameter name is refused, except `tag`, and `Target` on a table-bound API. Parameter names match exactly.

Then, when the API has an `executeprivilegename`, the caller must hold that privilege at any depth, or the call is 403
`0x80040220` (`PrivilegeDenied`). System Administrators and internal calls pass.

On the Web API a JSON value or a literal that does not fit the declared type is the same 400 `0x80040203`, naming the
parameter; the remaining checks are the SDK's, because the Web API builds the same request.

| Case | HTTP | Code |
|---|---|---|
| Missing required parameter, wrong type, undeclared parameter, wrong `Target` | 400 | `0x80040203` |
| Caller lacks `executeprivilegename` | 403 | `0x80040220` |
| The main operation throws `InvalidPluginExecutionException` | 400 | the plug-in's |
| The main operation throws anything else, or sets a response property to a value of the wrong type | 500 | `0x80040224` |
| Web API: unknown operation, wrong set, wrong verb, missing prefix on a bound operation | 404 | `0x80060888` |
| Web API: function without response properties | 404 | `0x8006088a` |

## The main operation and steps

The pipeline runs a custom API like any other message, with stage 30 filled in:

| Stage | What runs |
|---|---|
| 10, 20 | registered steps on the API's message, if the API allows them |
| 30 | the API's plug-in type, once, synchronously, inside the transaction: `Stage` 30, `Mode` 0, the caller as `UserId`, no configuration, no images, the same `InputParameters` the earlier stages saw. No plug-in type: nothing runs |
| 40 | registered steps (synchronous and asynchronous), if the API allows them |

`PrimaryEntityName` is the bound table, or `none` for a global API; `PrimaryEntityId` is the `Target` id for a table-bound
API and empty otherwise. A failure rolls the transaction back and follows the plug-in rules of [Plug-ins](plugins.md).
The main operation is not a registered step: its context and trace rows carry the step id `Guid.Empty`.

After stage 30, and again after the synchronous stage 40 steps:

- a declared response property nobody set is returned with the default of its SDK type (`false`, `0`, `Guid.Empty`,
  `DateTime.MinValue`, or null for reference types);
- a declared property set to a value of another type is 500 `0x80040224` naming it; a null value for a value type
  (Boolean, DateTime, Decimal, Float, Integer, Guid) counts as another type;
- an output that is not declared is dropped from the response, and the server logs one warning naming the API and the
  keys.

**Steps on the API's message** follow `allowedcustomprocessingsteptype`, for registrations through the SDK, the Web API
and solution import: 0 refuses every step (400); 1 accepts only asynchronous stage 40 steps; 2 accepts stages 10, 20 and
40 as for any message. Stage 30 is never registrable.

## `$metadata`

Every API that is neither private nor a function without response properties is declared in the
`Microsoft.Dynamics.CRM` namespace as a non-composable `Action` or `Function`:

- bound APIs have a first parameter `entity` (the bound table's entity type) or `entityset` (a collection of it);
  global ones are unbound and have an `ActionImport` or `FunctionImport`;
- one parameter per request parameter, with the Edm type of the table above, `Nullable` when optional;
- the return type is nothing, the entity type or `Collection(crmbaseentity)` for a single Entity or EntityCollection
  response property, or otherwise the complex type `{name}Response`, with one property per response property (Entity
  and EntityCollection properties are navigation properties of that type).

An API is left out, with a server warning naming it, when its bound table is not in the model, when its name or its
`{name}Response` is already a name of the model, when it is global and its name is already an entity set of the
model, or when a request parameter has the name of its binding parameter. An
unknown `logicalentityname` is typed `crmbaseentity`, also with a warning. The model is rebuilt when an import or a delete
changes the custom APIs.

## Solutions

- An API is a solution component with `componenttype` 9990. Its parameters and properties travel with it and are not
  components of their own. As in the real Dataverse export, APIs are not listed under `RootComponents` of `solution.xml`.
- Import order: plug-in assemblies and types, then the custom APIs (with their messages and filters), then the steps, so
  a step exported by xrmverse on an API's message imports back (for a global API's message, see Known limits).
- A re-import updates `name`, `displayname`, `description`, `isprivate`, `executeprivilegename` and the plug-in type, and
  adds new parameters and properties. Parameters and properties missing from the file stay, as unmanaged imports are
  additive. An identical re-import changes nothing.
- `iscustomizable` and the full label sets of `displayname` and `description` are kept as opaque XML
  (`xrmverse_componentxml`) and written back on export. `iscustomizable` is not a column: `$select=iscustomizable` is the
  usual 400 for an unknown column.
- Export writes the `customapis/` folder back from the rows; the files of an imported Dataverse export come out equal to
  the originals after XML normalization.
- Dependencies: deleting the plug-in type of an API, its assembly, or the API's bound table is refused with 400 naming the
  API.
- Deleting a solution (`Delete` of the `solution` row) removes only the container, as for every component. The
  xrmverse-only cascade delete (`xrmd solution delete --cascade`, `DELETE /admin/solutions/{name}?cascade=true`) also
  removes the solution's APIs, their parameters, properties, message and filter, and the steps on the message with their
  images; a step that belongs to another solution blocks it with 400 naming the step, and a step registered while the
  delete runs ends it with 409 and nothing deleted.

## CLI, MCP and web admin

The CLI authors; the MCP tool and the web admin only read. Create an API with the CLI, the SDK, the Web API or a
solution import.

- CLI: `xrmd customapi list` and `xrmd customapi show <uniquename>` (both with `-o json`), `xrmd customapi create --file`
  and `xrmd customapi delete <uniquename>`; see [CLI](cli.md#custom-apis).
- MCP: the `list_custom_apis` tool, read-only; see [MCP](mcp.md).
- Web admin: the **Custom APIs** tab of the Plug-ins page, read-only; see [Web admin](web-admin.md).

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

### Not in this version

- `Xrm.WebApi.execute` and the client scripting API: the model-driven UI is phase 3.
- Business events and the `catalog` and `catalogassignment` tables.
- Background operations.
- `workflowsdkstepenabled` is stored, and validated with `isfunction` at authoring, but not checked by a perform-action step: a
  classic workflow's perform action step can call any custom API.
- Power Fx (low-code) custom APIs: the import is refused.
- A step from a Dataverse export on a custom API's message: the step carries Dataverse's id for that message, which
  xrmverse cannot map, so the import fails with 400 "references the SDK message …". Steps exported by xrmverse import back.
- A step on a global API's message keeps its `none` filter on import only when the API is in the same solution file;
  otherwise it imports without a filter.
- Managed properties (`iscustomizable` is kept as opaque XML; managed solutions are item 2.6).

### Differences and choices to verify against Dataverse

- The object type codes 9990–9992 and the component type 9990 are xrmverse's own. Dataverse gives these tables a code of
  10000 or more that changes per environment; the real export used by the tests has no root component for its APIs.
- Web API operation names are case-sensitive, while the SDK path ignores case.
- A bound Web API operation without the `Microsoft.Dynamics.CRM.` prefix is 404; a `Target` in the body of a table-bound
  call is 400.
- Of the optional parameters Dataverse accepts on every message, only `tag` is accepted; others (such as
  `SuppressDuplicateDetection` or `BypassCustomPluginExecution`) are refused as undeclared.
- A `Target` on a global or collection-bound API is refused as an undeclared parameter.
- On the Web API an entity set takes precedence over a global operation of the same name. A solution whose global API is
  named like an entity set of the organization or of the file is refused at import (400 naming the table). A table
  created later with an API's name as its entity set takes `POST /{name}` over, and `$metadata` leaves the API out with
  a server warning.
- The 403 for a global API's `executeprivilegename` names the entity `none`.
- A plug-in that sets a value-type response property to null gets 500 `0x80040224`, not the default value.
- The main operation runs as a stand-in step with id `Guid.Empty`: `OwningExtension` and the `pluginstepid` of its trace
  rows name no step.
- `$metadata`: an optional parameter of a collection type (StringArray, EntityCollection) is declared
  `Nullable="false"`, because CSDL `Nullable` on a collection describes its elements; the document declares no
  `Alias="mscrm"` for the namespace.
- The navigation property to the plug-in type is `plugintypeid` (lowercase; bind with `plugintypeid@odata.bind`); Learn
  writes `PluginTypeId`.
- Authoring: no deep insert (create the fields with their own Creates); no `iscustomizable` column; a managed API is
  never changed in place, where Dataverse allows it when the API is customizable; a Create takes the prefix of any
  existing publisher, not only that of the solution; a Delete is refused while a step is registered on the API's message,
  and the unique-name check, the immutable columns and the refusal texts follow Learn's description and are not verified
  against Dataverse; a Create through the CLI is not atomic on the server (it deletes the API again when a field is
  refused; if the process dies in between, `xrmd customapi delete` removes the rest).
- `customapi` has no collection-valued navigation properties to its parameters and properties
  (`CustomAPIRequestParameters`, `CustomAPIResponseProperties`): query the two tables with a filter on `_customapiid_value`.
  The CLI and the web admin do so.
- The response type check does not enforce `logicalentityname` on Entity and EntityReference response properties.
- Cascade delete: the API's message and filter rows go with the API, without a separate Delete check on `sdkmessage` and
  `sdkmessagefilter`.
- Not verified: the relationship, choice and navigation property names, `categoryname` `CustomAPI`, whether
  `plugintypeexportkey` always equals `plugintypeid`, the error codes Dataverse uses for each validation failure (it may
  use `0x80060387` for a type mismatch), `PrimaryEntityName` `none` for a global API, the binding parameter names
  `entity` and `entityset`, whether a private API is callable from a plug-in, and whether an output a plug-in sets without
  declaring it is dropped.

## See also

- [Plug-ins](plugins.md)
- [Classic workflows](workflows.md#actions)
- [Solutions](solutions.md)
- [CLI](cli.md#custom-apis)
- [MCP](mcp.md)
