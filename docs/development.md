# Development

[← xrmverse documentation](../README.md#documentation)

## Dev container

The repository has a dev container in `.devcontainer/`. It has the .NET 10 SDK, Node 22, pnpm (through corepack) and
Docker. You need nothing else on your machine.

### Open it

- **GitHub Codespaces (first choice).** On GitHub, choose Code, then Codespaces, then create a codespace on the
  branch. Use a machine with 4 cores or more.
- **VS Code.** Install the Dev Containers extension and Docker, open the folder, and run "Dev Containers: Reopen in
  Container".

On the first start, `.devcontainer/post-create.sh` runs once. It follows the CI order and stops at the first error:

1. `corepack enable`
2. `pnpm install --frozen-lockfile` in `ui/` (corepack uses the pnpm version pinned there)
3. `pnpm build` in `ui/`
4. `dotnet restore`
5. `dotnet build --no-restore`

### Tests

`dotnet test` works inside the container. The container lets the `vscode` user open port 80, so the ServiceClient
acceptance tests can run too. They run `server init --accept-sql-server-eula`, so run them only if you accept the SQL
Server terms; otherwise use `--filter "Category!=Acceptance"`, as CI and the `devcontainer` workflow do.

The integration tests start SQL Server containers with Testcontainers, which sets `ACCEPT_EULA` for those test
containers. This is under review in issue #60.

### Run the server

The dev container does not set `ACCEPT_EULA`. Accepting the SQL Server terms is your decision.

```bash
cd docker
cp .env.example .env
```

Read the SQL Server terms ([license terms](sql-server-editions.md#license-terms-accept_eula)). If you accept them, set
`ACCEPT_EULA=Y` in `docker/.env` yourself. Then start the server:

```bash
docker compose up -d
```

Without `ACCEPT_EULA`, `docker compose up` fails and names the terms. Ports 5000 and 5173 are forwarded.

### Why it is built this way

- **amd64 on every host.** SQL Server has no arm64 image, so the container runs as `linux/amd64`. On Apple Silicon
  this is slower, because it runs under emulation.
- **Docker inside the container.** Some tests reach the test process from other containers through
  `host.docker.internal`. With the host Docker socket, "host" would be your machine, not the dev container.
- **No C# Dev Kit.** C# Dev Kit has a proprietary Microsoft license. The container installs the C# extension
  (`ms-dotnettools.csharp`), ESLint, Docker and EditorConfig. You can install C# Dev Kit yourself under its own
  license.

### CI check

The `devcontainer` workflow builds the dev container and runs `dotnet test` inside it. It runs on pull requests that
touch `.devcontainer/**` and by hand (Actions, then devcontainer, then Run workflow).

## What the `ci` workflow runs

The `ci` workflow is kept small, because Actions minutes are limited:

| Event | Runs |
|---|---|
| Pull request | web admin, build and unit tests, integration tests in 4 parallel shards, vulnerability gate; license gate only when dependencies change |
| Push to `main` | web admin and build and unit tests only (the pull request already ran the integration tests) |
| Every night (02:30 UTC) | everything, when `main` changed in the last 25 hours; the license and vulnerability gates every night |
| By hand (Actions, ci, Run workflow) | everything |

The job `postgres` ("postgres integration tests (n/4)") runs the integration tests on PostgreSQL
(`XRMD_TEST_PROVIDER=postgres`, filter `Category!=Acceptance`, the same tests as on SQL Server) in the same 4 shards, **only** on the
nightly schedule and by hand, never on pull requests or pushes. Run the PostgreSQL tests of the area you change on your own
machine before you open a pull request (see [Tests on PostgreSQL](#tests-on-postgresql)).

- Changes to `docs/`, `site/` or `*.md` files only start no run; the nightly run covers them.
- A new push to a pull request cancels the run of the previous push.
- The web admin is built once per content of `ui/`: its `ui/dist` is cached and reused by the integration shards.
- `eng/test-shard.sh <project> <shard> <count>` prints the test filter of one shard. Classes are spread by their
  measured time in `eng/test-timings/<project>.tsv` (a class not listed there counts its test cases times the average
  time per test case); the last shard takes every test the others do not, so no test is left out.
- Each integration shard prints its seconds per test class at the end of its log (`test-timings: begin` ... `end`).
  When the shards drift apart, save those lines of the four shards and run
  `eng/test-timings.py --merge --serial xrmd --sources tests/Xrmd.Integration.Tests shard*.tsv > eng/test-timings/Xrmd.Integration.Tests.tsv`.
  A class's time includes its fixtures; classes outside the `xrmd` collection run beside it and weigh less.

## Tests on your own machine

```bash
dotnet test      # integration tests use Testcontainers (the Docker daemon is required)
dotnet test --filter "Category!=Acceptance"   # excludes the ServiceClient acceptance tests
```

The ServiceClient acceptance tests (`[Trait("Category", "Acceptance")]`) start Kestrel on `127.0.0.1:80`: they need
permission to open port 80 (root, or `CAP_NET_BIND_SERVICE` / `net.ipv4.ip_unprivileged_port_start=80` on
Linux) and the port must be free. Where that is not possible, exclude them with the filter above.

## SDK-free probe

`tests/Xrmd.Integration.Tests/SdkFree/SdkFreeProbeTests.cs` starts a host in `Sdk:Mode` `none` and runs a fixed tour. Its results
are compared with `tests/fixtures/sdk-free-baseline.json` (the steps that still need the SDK) and
`tests/fixtures/sdk-free-assemblies.json`. A step that fails and is not in the baseline is a regression; a step in the baseline
that now passes must be removed from it, so the lists only shrink. See [SDK-free servers](microsoft-sdk.md#sdk-free-servers).

### SDK usage counter

`tests/Xrmd.Repository.Tests/SdkUsageTests.cs` counts, per project, the `.cs` files under `src/` that use the
`Microsoft.Xrm.Sdk*`, `Microsoft.Crm.Sdk*` or `Microsoft.PowerPlatform.Dataverse*` namespaces: a using directive (also `global`,
alias and `static`) or a fully qualified name in code (`Microsoft.Xrm.Sdk.Entity`, also after `global::`). Comments and string
literals do not count (strings hold wire-format names such as the package id or workflow XAML namespaces). It compares the
counts with `tests/fixtures/sdk-usage-baseline.json`. The count of a project may only go down. When it goes down, lower its
number in the baseline in the same pull request; when it goes up, the test fails. The test reads `git ls-files`, so a new file
must be `git add`ed before the test sees it.

### Xrmd.Model

`src/Xrmd.Model` has no references. Its metadata types (namespace `Xrmd.Model.Metadata`: tables, columns, choice sets,
relationships) have names of their own; their property and enum names are the Web API names, because they are the JSON
contract. `[WireType]` holds the Web API type name of a type, as `@odata.type` carries it. For the same input JSON, including
partial JSON with missing keys, empty objects or `null` values, `MetadataJson` writes the same JSON as `MetadataSerializer` of
`Xrmd.Metadata` (the SDK serializer): the defaults of the model types are those of the SDK types. `Privileges` and
`Keys[].AsyncJob.KeyAttributes` are the exception: the SDK serializer cannot read them at all.
`tests/Xrmd.Metadata.Tests/Golden/ModelParityTests.cs` checks this on the raw seed tables, on every variant of the complete
golden files with one key dropped, one object emptied or one value set to `null`, and on new instances of each type.

Golden files in `tests/fixtures/metadata-golden/` pin that JSON: `tables` (the seed tables) and `full` (per type a `full`, an
`.empty` and an `.omitted` file). They are regenerated only from the SDK serializer, never from `MetadataJson`. To regenerate
them, run `XRMD_UPDATE_GOLDEN=1 dotnet test tests/Xrmd.Metadata.Tests --filter FullyQualifiedName~MetadataGoldenTests`. That
run deletes the whole `tests/fixtures/metadata-golden/` folder, writes every file again, and then fails on purpose, so that the
files are reviewed before they are committed. A change of a golden file is a change of the JSON contract: review it.

`SdkMetadataBridge` (`src/Xrmd.Metadata`) converts between the SDK types and the model by a JSON round trip. It is the SDK edge
that the server uses while the SDK is still in the process; a table with `Privileges` or `Keys[].AsyncJob` cannot be converted
to the SDK side (stored metadata never has them).

### SDK view of the metadata store

The metadata store (`Xrmd.Metadata`, `IMetadataStore`) holds its tables, columns, choice sets and relationships as model types.
Modules that have not moved to the model yet (Query, Security, Core, Pipeline, Web API, Solutions, Workflows and the rest) still
read the SDK types (`EntityMetadata`, `AttributeMetadata`, `OptionSetMetadata`, `RelationshipMetadataBase`). They get them through
the SDK view: extension methods with the same names in `src/Xrmd.Metadata/Sdk/SdkMetadataView.cs`, plus SDK twins of the helpers
in `src/Xrmd.Metadata/Sdk/`. The view converts a model object to its SDK form by `SdkMetadataBridge` (a JSON round trip).

The view is cached per model instance in `SdkViewCache` (a `ConditionalWeakTable`), so the same model gives the same SDK object
for as long as the store holds it; a write replaces the model and so the view. A reverse map from view to model lets a twin find
the model of a view without converting again; for an object that is not a view, the twin converts a fresh copy.

Query (`Xrmd.Query`) is on the model since S.2c: its compilers and translators read `Xrmd.Model.Metadata` types and no longer
touch the SDK metadata types. Its SDK edge is `src/Xrmd.Query/Sdk/`: the `QueryExpression` reader (SDK query to the model query),
the SDK condition values, the callback interfaces that other modules implement (`IQuerySecurity`, `IHierarchyResolver`; model-typed since S.2d), and SDK twins of the public helpers that other modules still call.

Security (`Xrmd.Security`) is on the model since S.2d: privilege and access evaluation, field security and the system-table rules
read `Xrmd.Model.Metadata` types, and its own types (privileges, access rights, field permissions and so on) live in
`Xrmd.Model.Security`. The query callbacks `IQuerySecurity` and `IHierarchyResolver` are now typed on the model, so the compilers pass
model tables and columns, not SDK views. The SDK twins of the public security helpers sit in `src/Xrmd.Security/Sdk/`
(`SdkSecurityTwins.cs`, `SdkSecurityTypes.cs`) and convert with `ToModel()` before they delegate. Records and messages
(`Entity`, `EntityReference`, `OrganizationRequest`) stay SDK types in the rules and the guard until S.2e.

The model types and converters for records and messages exist since S.2e1; the modules start using them in S.2e2 to S.2e4. `Xrmd.Model.Records` holds the record types (`Record`, `RecordReference`,
`RecordCollection` and their values) and `Xrmd.Model.Messages` holds the request and response types. Primitive values keep their
CLR types; only SDK classes have model counterparts. The converters `Xrmd.Abstractions.Sdk.SdkRecords` and
`Xrmd.Abstractions.Sdk.SdkMessages` convert both ways and the round trip is lossless for records and values, nesting and nulls included (a typed request is not, see below). A value of an SDK
type the converters do not know passes through unchanged, and a typed SDK request (for example `CreateRequest`) becomes the base
`OrganizationRequest`, so that conversion keeps the name and parameters but not the request's CLR type. The golden files under `tests/fixtures/remote-context-golden` capture the remote execution context as
today's code writes it; they are the contract that the own writer of S.2e2 must meet byte for byte. The work is split in four:
S.2e1 is the model and the converters, S.2e2 the pipeline, the SDK plug-in adapter and the own serializer, S.2e3 the Core
operations, and S.2e4 Mail, Elastic, Duplicates, Solutions and Rollups.

Queries in messages follow the same pattern (S.2e2a). `Xrmd.Model.Query` now also holds the query definitions, our counterparts of `QueryExpression`, `QueryByAttribute`,
`FetchExpression`, `FilterExpression`, `ConditionExpression`, `LinkEntity`, `OrderExpression` and `PagingInfo` (with their own
names, `RecordQuery`, `AttributeQuery`, `FetchQuery` and so on), and the counterpart of `ColumnSet`, `ColumnSelection`, lives in
`Xrmd.Model.Records`, and `Xrmd.Abstractions.Sdk.SdkQueries` converts them both ways. The converters carry every public settable member
except `ExtensionData` and the five lazy-file members of `ColumnSet` (`HasLazyFileAttribute`, `LazyFileAttributeEntityName`,
`LazyFileAttributeKey`, `LazyFileAttributeValue`, `LazyFileAttributeSizeLimit`), which are not part of the data contract; condition
values convert like record values, and an undefined enum value survives the round trip. `QueryExpressionReader` now reads the model
definitions and produces the same `QueryModel` as before, with the same refusals and fault messages for the known value types (money, choice and record references); the SDK overloads in `src/Xrmd.Query/Sdk/` convert and
delegate, with a nesting guard. The remaining S.2e2 work is split in two: S.2e2b writes our own serializers whose output equals the
golden files, and S.2e2c moves the pipeline onto the model and adds the SDK plug-in adapter.

The remote context has its own serializers too (S.2e2b). `Xrmd.Model.Serialization` holds `ContextXml` (the XML of the remote execution context stored for async operations), `QueryXml` (the XML of a query kept in a message) and `ContextJson` (the webhook payload, UTF-8 JSON without a byte-order mark, plus `Trim` for the size rule). They use only the BCL (`System.Xml`, `System.Runtime.Serialization.Json`) and write the same bytes as the SDK serializers did: the golden files and differential tests against the SDK serializer pin this for every context and query the model can carry. The XML reader uses the same settings as before (`DtdProcessing.Prohibit`, no resolver) and never instantiates a type named in the payload; a type outside the model set is reported as unknown.

`AsyncContextSerializer` and `WebhookPayload` keep their signatures and choose per call. Writing: if the model can carry every value of the context (checked on the SDK object, before conversion), the own writer runs, otherwise the private SDK serializer does. Reading: if the own reader recognises the whole payload, the result is converted to the SDK object; otherwise the private SDK reader runs, with the allowlist unchanged, so a type outside it is still refused with `Type '...' is not allowed.`. The fallback is decided only by these two checks, never by catching an exception. A broken payload (not well-formed XML, a DTD, a wrong root, a bad value) throws `SerializationException`, as before; where the XML parser or a value conversion failed, the `XmlException` is the inner exception, and the message text may differ. Nesting is bounded the same way on both sides: the own reader stops at 256 XML levels (its stack guard) and reports the payload as unknown, and the model refuses to carry values nested deeper than that, so over the limit the old SDK serializers are used, with no behaviour change against before. S.2i removes the private SDK fallbacks together with the twins and the bridge; some shapes the writer carries (for example a null value in `FormattedValues`) are still read through the fallback today, and S.2i has to teach the own reader those shapes first.

One behaviour changes on purpose: query payloads that contain an `OptionSetValueCollection`, an `EntityReferenceCollection` or a `Relationship` value could not be read by the old reader (it failed with `SerializationException`); they are readable now.

The pipeline runs on the model since S.2e2c. `StepContext` (`Xrmd.Model.Messages`) is the execution context of a step: the 30
members of the remote context, with a `StepContext` as parent and `OwningExtension` never null, plus the members that only an
in-process plug-in sees (the per-step image collections, the caller's identity details, the environment). `PipelineExecutor.cs`
has no Microsoft SDK namespace (it still names the SDK view type `XrmdPluginExecutionContext`, which it lends to the workflow
hooks, so S.2i touches it too): the stage loop (stages 10, 20 and 40), the transactions, the steps, the real-time workflows and the async
enqueue work on `StepContext`, `Request` and `Record`.

`SdkMirror` (`src/Xrmd.Abstractions/Sdk/`) connects the two sides. Where `SdkRecords` and `SdkMessages` copy, the mirror gives
every SDK object one model twin and writes state into the existing twin at each crossing: `Out` writes a model object into its SDK
twin, `In` the reverse. Object identity and CLR subtypes (typed requests and responses, early-bound entities) therefore survive
any number of crossings. One mirror serves a whole execution chain, nested calls included. Its rules:

- **In before Out.** An `Out` is authoritative: it overwrites any SDK change that was not read back first, an in-place
  `OptionSetValue.Value` included. Every boundary therefore reads the SDK side in before it writes the model out.
- **Identity follows the slot.** Sharing that came from the SDK survives: one SDK value held in several slots (the same
  `OptionSetValue` in two attributes, or in every row of a CreateMultiple) is one model value and stays one SDK object, as long as
  each slot still holds the paired SDK instance. A value the model creates, or places in a slot that did not hold its twin (for
  example in an image), gets a fresh SDK instance, so the model never aliases objects that are separate on the SDK side.
  Responses follow the SDK pipeline, where the response and the steps' `OutputParameters` hold the same objects: the executor
  first places, in each result slot, the instance the steps' collection holds for that key, so the response keeps it. One case
  stays a fresh instance: an immutable value that the core put in a response (an `OptionSetValue`, `Money`, `AliasedValue` or
  `Relationship` result) is a new object in the view of the first post-operation step, and that object is the one the caller
  gets. No step has seen the core's instance, so a plug-in could tell only if the core put the same immutable instance both at the
  top level of a response and inside another result; no core message is known to do that. Keeping the core's instance there would
  mean pairing the steps' collection before any view exists.
- **Weak tables.** The pairs are held in `ConditionalWeakTable`s, so the objects of a finished nested call can be collected while
  the mirror lives.

The SDK plug-in adapter lives in `src/Xrmd.Pipeline/Sdk/`, together with the other files of the Pipeline that still use the SDK
(the plug-in runner, its organization service and service provider, the tracing service, the assembly loader). Its
`PluginContextAdapter` lends a step an SDK view of its `StepContext` (`XrmdPluginExecutionContext`, up to
`IPluginExecutionContext7`) and reads the view back into the model when the step returns; around each service call of a running
plug-in it reads the view in before the call and writes it out again after. A parent level that came from an async payload is
lent as a real `RemoteExecutionContext`, as before.

`src/Xrmd.Pipeline/Sdk/PipelineExecutor.Sdk.cs` is the boundary file, a partial of `PipelineExecutor`. It holds the public entry
on an `OrganizationRequest` and the work that still needs SDK values: the guard, the custom API and action validation, the primary
entity, the stage-20 checks, stage 30 (core operations, bulk, upsert, actions, `ExecuteWorkflow`, custom APIs), the images the core
reads, the workflow trigger, the webhook send and the response. The request crosses into the model after the guard and the
validation, so the guard's own writes to the request are part of what crosses. At the boundary of stages 20 and 30 the model is
written into the SDK request once, the core works on that request, and the request is read back once.

What stays on the SDK, and the step that moves it:

| Still on the SDK | Moves in |
|---|---|
| The held window of the bridged messages | With their bodies (S.2e3c3b, S.2e4, S.2f) |
| The held window of custom actions and APIs, XAML actions and `ExecuteWorkflow` | S.2h |
| The read of the outer SDK request before a lend of an Upsert or bulk level, and its read-back after stage 30 | S.2i |
| `PrivilegeCatalog` and `SecurityCache`, which implement the SDK-typed metadata hooks `IEntityCreatedHandler` and `IEntityDeletedHandler` | With the metadata writes |
| `OrganizationService`, `RetrieveMultipleModelRequest` and `MetadataOperations` (RetrieveEntity, RetrieveAttribute and the metadata reads) | S.2e3c3b |
| The core entry adapter, and the callers of the `PipelineCoreSdk` extension methods in Workflows, Solution and Admin; the methods are removed | S.2e3c3b |
| Mail, Elastic, Duplicates, Solutions and Rollups, and their bridges | S.2e4 |
| Files and Audit, and the bridges that reach them | S.2f |
| Workflows (`Xrmd.Workflows`), whose hooks the pipeline calls with a lent SDK view | S.2h |
| The SDK plug-in adapter, which moves to `Xrmd.Sdk.Adapters`, and the SDK overloads, which are removed | S.2i |

The cost of a message is linear in its size:

- The request crosses once at entry (`In`), plus one `Out` and one `In` at the boundary of stages 20 and 30.
- Each step or hook writes its own parameter bags and images when it is lent, and reads the bags back on return. Around each
  service call of a running plug-in, the levels it has lent are read back and written again.
- **Lazy parent lend.** A parent level is lent only when the plug-in first reads `ParentContext`, and then stays lent until the
  view is lent again. A step that never reads its parent converts nothing of it.
- **Linear bulk.** The inner executions of a CreateMultiple or UpdateMultiple do not convert the outer request, so a step on the
  inner message costs the same per target whatever the number of targets.
- The response is returned unconverted unless something observes it: a post-operation step, an async step, a workflow trigger or
  a custom API. When it is observed, it crosses in once, its output parameters cross about twice per post-operation step, and
  it crosses out once at the end.
- **Residual.** In a bulk request, each inner step that reads `ParentContext` converts the whole outer request once, because the
  parent level holds all the targets. A step on the inner message of a large bulk request should not read its parent unless it
  needs to. An inner async step or webhook step of a bulk request also reads the whole registered outer request once, before its payload
  is written. That payload already held the whole parent chain before S.2e2c, so the order of growth does not change.

Async payloads and webhook bodies are written from the model context. The model path (`ContextXml`, `ContextJson`) is taken only
when the model can carry every value and every value has a plain SDK twin: a twin of exactly the SDK type the mirror creates, with no
`ExtensionData`. A context that holds an early-bound entity or a value read with extension data serializes the lent SDK view, as
before, so its bytes do not change. `AsyncOperationQueue.EnqueueAsync` therefore keeps its `SdkMirror` parameter, which the
twin check needs. It is internal: its caller must first read every level of the chain that the core holds, and the executor is
its only caller. The async worker rebuilds the job context with `StepContext.FromRemote` and overrides the same members as before;
it no longer uses the SDK itself: since S.2e3a it sends the core a model `Delete` request (see below). The SDK usage count of
Pipeline is 30.

The interface between the pipeline and the core is on the model since S.2e3a. S.2e3 is split in three: S.2e3a moves
`IPipelineCore` (`src/Xrmd.Pipeline/IPipelineCore.cs`), S.2e3b the CRUD handlers and S.2e3c the other core messages. In S.2e3a no
handler body changes, and every caller sees the same objects, CLR types and in-place changes as before. The response goldens in
`tests/fixtures/core-response-golden/` and the request-mutation probe pin this, and so do the plug-in context goldens of S.2e2c.

- **The interface.** `IPipelineCore` names no SDK type. `ExecuteCoreAsync(Request, CoreCall, CancellationToken)` replaces the three
  SDK overloads. `CoreCall` is a record of the caller, the session, the nested delegate and the columns the server writes on a
  Create. `NestedRequest` takes and returns model messages. `WrittenEntities` takes a `Request`, `LockExistsAsync` and
  `RetrieveImageAsync` take a `RecordReference`, and `RetrieveImageAsync` returns a `Record`. `ExecuteCoreAsync` and
  `ExecuteNonPipelineAsync` return a model `Response`.
- **The core entry adapter.** `src/Xrmd.Core/Sdk/CoreOperations.Sdk.cs` implements the model members over the SDK handlers of
  `CoreOperations`. It converts with the request's chain mirror, so the handler gets the SDK twin of the request: the same instance
  the caller or the pipeline holds, with its CLR type, its early-bound values and the marks keyed on it. A request with no chain
  mirror gets a new mirror for that call, and both directions of the call use it. Such a caller has no mirror to read a shell
  with, so it gets the response already read in (see the read rule below). S.2e3c3b removes the adapter.
- **The SDK extension methods.** The callers outside the pipeline (Workflows, Solution, Admin, Core.Solutions, the custom API
  authoring, the plug-in organization service and the tests) still hold SDK objects. They call extension methods with the old names
  and SDK signatures, in `src/Xrmd.Pipeline/Sdk/PipelineCoreSdk.cs` (namespace `Xrmd.Pipeline`, so no `using` changes).
  `SdkNestedRequest` is the SDK form of the nested delegate. Each method crosses its arguments into the model with a mirror of the
  call, registers that mirror for the model request, calls the model member and crosses the answer back with the same mirror. The
  request a caller passes is therefore the twin the core works on, and the response it gets is the core's own typed instance (a
  `CreateResponse` stays a `CreateResponse`). `RetrieveImageAsync` names the call's mirror for the image
  (`CoreHandOff.ReadImagesWith`), so the caller gets the core's own `Entity`. `LockExistsAsync` converts only the reference, and
  `WrittenEntities` only the message name and the `Relationship` parameter, because the answer depends on nothing else. S.2e3c3b
  moves these callers to their own boundary and removes the methods.

`SdkMirror` gained a registry and response shells for this:

- **The registry.** `SdkMirror.Register(Request, SdkMirror)` records the chain mirror of a model request, and `SdkMirror.For(Request)`
  returns it, or null. The table is static and weak: an entry lives as long as its request. Registering the same mirror again does
  nothing; registering another mirror for the same request throws `InvalidOperationException`. The pipeline registers its chain
  mirror right after the request crosses into the model.
- **Unread shells.** `mirror.Shell(OrganizationResponse)` pairs a new model `Response`, which carries only the name, with a typed SDK
  response, without reading its results and without counting anything in `SyncCount`. `IsUnread` is true until the twin is read in.
  While a shell is unread, its `Results` are empty and `Out` of it returns the twin unchanged. `In` of the twin fills the shell, and
  from then on it is an ordinary twin.
- **The read rule.** A model read of an unread shell cannot be detected; the shell simply reads as empty. Every model read or write
  of a core response therefore calls `mirror.In(twin)` first. Only a caller that registered the request's mirror gets a shell: when
  the core had to create the mirror, it returns the response already read in. A model write to an unread shell (a result added, the results replaced
  by a non-empty bag, or the name changed) cannot reach the twin, so the next `Out` of the shell throws `InvalidOperationException`
  rather than drop the write.

`CoreHandOff` (`src/Xrmd.Pipeline/Sdk/PipelineExecutor.Sdk.cs`) is `internal`. Only the core sees it, through
`InternalsVisibleTo Include="Xrmd.Core"` in `Xrmd.Pipeline.csproj`. It holds two weak tables:

- **The held request.** `HoldAtCoreAsync` records the run's model request with the SDK request the pipeline holds, and `ReleaseCore`
  removes the entry (it runs in a `finally`, so no entry outlives a fault). While the entry exists, `TryGetHeld` gives the adapter that
  SDK request: the handler runs on it directly, nothing is converted, and the pipeline reads it back once, as in S.2e2c.
- **The image mirror.** `ReadImagesWith(RecordReference, SdkMirror)` names the mirror into which the adapter converts one image read.
  The key is the reference instance created for that read, so two reads of the same record never collide. The pipeline passes its
  chain mirror, so the record's twin there is the core's own `Entity`.

Two `AsyncLocal` test seams, `HandlerRequest` and `ImageRead`, follow the `AsyncOperationWorker.BeforeAutoDelete` precedent. In
production each costs one null read per call.

The pipeline calls the model members. Stage 30 calls `ExecuteCoreAsync` inside the held window (at this step; S.2e3b removed the
window for the CRUD messages, S.2e3c2b for the messages the core answers on the model, and S.2e3c3a for standard Upsert and bulk)
and takes the SDK twin of the returned shell, which is the core's typed instance. `ResponseToContext` reads it in only when the
response is observed. The nested delegate gets the core's nested request as a model request, runs its SDK twin (the request the core
built, with `CascadeHint` and the other marks), and answers with the model twin of the nested response, so the core gets the filled
typed response back.
`StandardUpsert` locks with a `RecordReference`. The async worker sends a model `Delete` request (`Target` is the `asyncoperation`
reference) with `CoreCall(Caller.Internal, session)`, which has no nested delegate, so no step sees it, as before.

The cost of a message through the core, measured against the code before S.2e3a (`PipelineModelBoundaryTests` pins the counts):

- **Unobserved responses are free.** A response nobody observes stays an unread shell and is never converted. A RetrieveMultiple of
  1,000 rows with no observer converts 21 objects, the same as for 1 row. With one post-operation step it converts 50 objects for
  1 row and 4,046 for 1,000, as before.
- **Images are converted once.** Each pre- or post-image is converted once, into the chain mirror. An Update with a pre-image and
  a post-image converts 23 objects, as before.
- **Core-issued nested requests cost one extra small conversion each.** The adapter reads each nested request in (a small Update or
  Delete with its reference) before the pipeline's own read after the guard. A delete cascade of an email to 100 attachments converts
  806 objects instead of 606, or 2 to 3 more per child. The time stays within noise.
- **The stage-20 SDK image checks cost one conversion each.** The checks on writes of the custom API, plug-in registration, workflow
  and service endpoint tables, and the owner lookup of real-time workflows, read their image through the model member into a mirror
  of their own. Each read converts that one row in once, and the checks get the core's `Entity`. S.2e3c2b moves these
  checks to `Record`: they read the stored image through `RetrieveImageAsync` with no mirror named and convert nothing.
- **Direct SDK callers pay three conversions of the request.** An extension method converts the request in, and the adapter writes
  it out and reads it back, so a caller outside a pipeline pays three times the size of its request. That is what a message pays
  through the pipeline too (one read at entry, one write and one read at the core): an Associate of 5 objects converts 15 there, as
  before. The response crosses back unconverted as the shell's twin. The same holds for the CRUD twins: an extension caller still
  mirrors the typed response out (`mirror.Out`), which this step does not remove, so a CRUD read through an extension method pays
  that conversion of the response on top of the three of the request.
- **Direct SDK image reads convert the image once.** The `RetrieveImageAsync` extension method (used by the workflow store and
  authoring, `ExecuteWorkflow` and the solution importer) converts the target and then the image once, into the call's mirror, and
  returns the core's own `Entity`. Before S.2e3a these reads converted nothing; the cost is one conversion of one row.

What moves next is S.2e3b, below, and then S.2e3c.

### Core CRUD on the model (S.2e3b)

S.2e3b moves the eight CRUD messages of the core (Create, Retrieve, Update, Delete, RetrieveMultiple, Assign, Associate,
Disassociate) and the read path they use onto `Record`. External contracts do not change: the response goldens in
`tests/fixtures/core-response-golden/`, the row goldens in `tests/fixtures/core-row-golden/` (stored rows, activity parties,
audit rows, match codes and fault texts), the parameter goldens in `tests/fixtures/row-parameters-golden/` and the plug-in and
remote context goldens are byte-identical to the code before S.2e3b. The handlers read `TableDefinition` and `ColumnDefinition`
from the metadata store, and match model values (`ChoiceValue`, `MoneyValue`, `RecordReference`, `RecordCollection`, ...). Fault
texts that name a value type keep the SDK name they show today.

- **The moved files.** `EntityRowMapper`, `ActivityParties`, `ActivityPointerSync`, `CurrencyStamp`, `PostRules`,
  `Validation/EntityValidator` and `Query/QueryExecutor` with `ActivityRead`, `NoteBodyRead` and `UserDateFormatRead` name no SDK
  type any more. The handlers are partial files of `CoreOperations`: `CoreOperations.Read.cs`, `.Write.cs` (the shared write
  rules), `.Create.cs`, `.Update.cs`, `.Delete.cs`, `.Associate.cs` and `.Messages.cs` (the dispatch).
- **The model members and their SDK twins.** The members take and return model types. Each public member that other modules call
  keeps an SDK twin with the old name and signature in `src/Xrmd.Core/Sdk/CoreOperations.Records.Sdk.cs`; the twin leaves the
  caller's `Entity` as it did before.

  | Model member | SDK twin |
  |---|---|
  | `RetrieveRecordAsync` | `RetrieveAsync` |
  | `RetrieveRecordsAsync` | `RetrieveMultipleAsync` |
  | `CreateRecordAsync` | `CreateAsync` |
  | `UpdateRecordAsync` | `UpdateAsync` |
  | `DeleteRecordAsync` | `DeleteAsync` |
  | `AssignRecordAsync` | `AssignAsync` |
  | `AssociateRecordsAsync` | `AssociateAsync` (`associate: false` disassociates) |
  | `QueryExecutor.ExecuteRecordsAsync` | `QueryExecutorSdk.ExecuteAsync` |

- **The bridges.** `src/Xrmd.Core/Sdk/CoreSdkBridges.cs` (internal) is the only place where the moved code reaches the SDK-typed
  helpers of other steps: Security row rules, Mail, Elastic, Duplicates, Rollups, Files and Audit. The guard rule: a bridge first
  runs the helper's own guard (the table name, an elastic table, an image column, a rollup column, an audited table,
  `detectDuplicates`), copied from the helper and commented with its name. If the guard says the helper does nothing, nothing is
  converted. Otherwise the bridge converts the row with a mirror of the call, reads it in before writing it out, calls the helper
  and reads the row back into the same `Record`. A Create, Update, Retrieve or Delete of a plain custom table therefore crosses no
  bridge; `CoreSdkBridges.Crossings` and `CountCrossings()` are the test seams that prove it. Each bridge goes away with the step
  that moves its helper (S.2e3c2a for Security, S.2e4 for Mail, Elastic, Duplicates and Rollups, S.2f for Files and Audit).
- **The mark copies.** `CascadeHint` and `AuditActionHint` have model keys (`Mark(Request)`, `IsMarked(Request)`, `Of(Request)`)
  in SDK-free files; their SDK overloads are in `src/Xrmd.Abstractions/Sdk/RequestHints.Sdk.cs`. The pipeline copies the marks at its
  crossings: an SDK `AuditActionHint` goes onto the model request after the request crosses in, and a model `CascadeHint` goes onto
  the SDK twin of a core-issued nested request, because the stage-20 guard still reads it there. A query model that a front end
  gives is attached to the model request with `QueryModelHint.Attach` and read with `QueryModelHint.Of`.
- **Typed responses by name.** The handlers return model responses. For these eight messages the adapter pairs the model response
  with `CoreResponses.Typed(name)` (`src/Xrmd.Core/Sdk/CoreResponses.cs`), a new `CreateResponse`, `RetrieveResponse`, ... with the
  response name set, and fills it once. `CoreResponses.IsCrud` tells the adapter which names it applies to. The caller still gets the
  same CLR type as before.
- **No held window for CRUD.** The model request is current when the pipeline reaches the core. The model bodies do change the Target in
  place (validation strips calculated values, a write by key fills key ids and `partitionid`), and `KeepCallerTarget` copies exactly
  those changes onto the caller's `Entity`, so the SDK request is not written out for stage 30. The pipeline writes the request out and reads it back only for the tables in
  `HoldChecksMayWrite` (the custom API, registration, workflow, service endpoint and mail-secret tables), whose stage-20 checks may
  change it (until S.2e3c2b, which removed it: see Stage 20 on the model). The core no longer holds a plug-in's early-bound `Entity`: it works on the `Record`, and the stored row and the async
  payload are unchanged.

The read path shapes `Record`s, and that moves the cost of a read. A direct SDK caller (the TDS endpoint, MCP, Host and Admin
helpers, the migration verification) would pay one conversion per row, so the row shaper writes through a row sink
(`QueryRow`, `RowSink` and `QueryPage` in `src/Xrmd.Core/Query/QueryRow.cs`) with two implementations: one makes `Record` rows, the
other (in `Sdk/QueryExecutor.Sdk.cs`) makes `Entity` rows directly, building no `Record`. The per-column
logic is the same code for both. The responses of Retrieve and RetrieveMultiple copy their typed rows plainly, as the SDK handler
did before; the mirror, which keeps instance identity, is used only when a caller reads the same instance back (an observed
pipeline: a stage-40 step, a workflow or a plug-in that uses `OutputParameters`). The counts in `PipelineModelBoundaryTests` are:

- An unobserved read converts no row. A RetrieveMultiple of 1,000 rows converts 7 objects, the request, the same as for 1 row.
- An observed read (one post-operation step) converts 33 objects for 1 row and 3,030 for 1,000, at most the 50 and 4,046 of S.2e3a.

Timing is measured by `tests/Xrmd.Integration.Tests/CoreCrudTimingTests.cs` (`Category=Timing`, excluded from the CI test filters; run
it by name). The medians of 10 warm runs, main against this branch in one container, back to back, three rounds:

| Scenario | Round 1 | Round 2 | Round 3 |
|---|---|---|---|
| 2,000 Creates through the pipeline | -0.6% | +3.9% | -6.8% |
| 5,000-row RetrieveMultiple through the Web API | +2.7% | +8.0% | +4.1% |
| 5,000-row `QueryExecutor` SDK twin read | +32.0% | +2.4% | -4.5% |
| Migration import of 2,000 rows | +5.3% | +3.0% | -5.6% |

The budget is +10%. The SDK twin read is over it in one round of three, and the Web API read is within it in all three, so the
step meets the budget. Each caller that reads through the SDK twin stops paying it when its own step moves to the model (S.2f for
the Web API, S.2h for MCP and Host).

The SDK usage count of Core is 55 and that of Abstractions is 8; Pipeline stays 30. S.2e3c1 moves the other core messages, S.2e3c2a the guard and the Security row rules (S.2e3c2b the rest of stage 20, done), and
S.2e3c3b `OrganizationService` and `RetrieveMultipleModelRequest`, removes the core entry adapter and the SDK extension methods, and
moves the callers outside the pipeline to their own boundary.

### The other core messages on the model (S.2e3c1)

S.2e3c is split in three: S.2e3c1 moves the other core messages, S.2e3c2 moves Security (S.2e3c2a the stage-20 guard and the row rules, S.2e3c2b the rest of stage 20 and the
marks), and S.2e3c3b moves `OrganizationService`, `RetrieveMultipleModelRequest`, `MetadataOperations` and the SDK callers outside the
pipeline. S.2e3c1 moves the core-owned messages onto `Request`, `Record` and `TableDefinition`. External contracts do not change:
the response goldens (86 files and `mutation.txt`), the row, plug-in context, remote context, metadata and row parameter goldens are
byte-identical to the code before S.2e3c1, and so are the fault texts and the CLR types a caller or a plug-in sees.

| Group | Messages | Model body |
|---|---|---|
| Access and sharing | GrantAccess, ModifyAccess, RevokeAccess, RetrievePrincipalAccess, RetrieveSharedPrincipalsAndAccess | `CoreOperations.Access.cs`, `AccessMessages` |
| Record teams | AddUserToRecordTeam, RemoveUserFromRecordTeam | `RecordTeams` |
| Role privileges | AddPrivilegesRole, ReplacePrivilegesRole, RemovePrivilegeRole, RetrieveUserPrivileges | `CoreOperations.Roles.cs`, `UserPrivileges` |
| Currency, change tracking | RetrieveExchangeRate, RetrieveEntityChanges | `CurrencyMessages`, `ChangeTracker` |
| Queues | AddToQueue, RemoveFromQueue, RouteTo, AddPrincipalToQueue, PickFromQueue, ReleaseToQueue, RetrieveUserQueues | `QueueMessages` |
| Non-pipeline reads | WhoAmI, RetrieveAppComponents, RetrieveUnpublished, InitializeFrom, RetrieveFormattedImportJobResults, and the refusals of the caller-only and metadata-write messages and of ImportSolution from a plug-in | `CoreOperations.NonPipeline.cs`, `InitializeFrom` |
| Lookups | `WrittenEntities`, `RetrieveImageAsync`, `LockExistsAsync` | `CoreOperations.Messages.cs`, `CoreOperations.NonPipeline.cs` |

- **One model dispatch.** Every core message enters `ExecuteMessageAsync` (pipeline messages) or `ExecuteNonPipelineMessageAsync`
  (non-pipeline messages) in `src/Xrmd.Core/CoreOperations.Messages.cs`. The CRUD switch of S.2e3b is one branch of it. Each moved
  branch first runs today's server-values check and then reads its parameters in the order of the SDK branch it replaces, with the
  same texts (they still name the SDK types, for example "not a EntityReference" or "missing or not a RolePrivilege[]").
  ExecuteWorkflow keeps its `InvalidOperationException`, and an unknown message keeps its `NotSupportedException` text.
- **The bridged messages.** `CoreSdkMessages` (`src/Xrmd.Core/Sdk/CoreSdkMessages.cs`) holds the SDK switches for the messages whose
  bodies belong to later steps, and the `Enter`/`Leave` logic of the held window. `CoreSdkMessages.Bridged` is exactly these 26 names
  (a test pins the list):
  - Mail: SendEmail, GetTrackingTokenEmail (S.2e4);
  - Duplicates: PublishDuplicateRule, UnpublishDuplicateRule, RetrieveDuplicates, BulkDetectDuplicates (S.2e4);
  - Elastic: the elastic upsert and ExecuteCosmosSqlQuery (S.2e4);
  - Rollups: CalculateRollupField (S.2e4);
  - Files: the six file messages (S.2f);
  - Audit: the audit messages (S.2f);
  - Metadata: RetrieveEntity, RetrieveAttribute and the metadata reads of `MetadataOperations.Retrieve` (S.2e3c3b).

  A bridged message still runs on the held SDK request, with the same response instance and in-place changes as before
  (`mutation.txt` pins this). `CoreHandOff.HandlerRequest` fires only for bridged messages.
- **Model values.** Values that crossed the mirror as opaque SDK objects have model twins now. `SdkRecords` and `SdkMirror` convert
  them:

  | SDK value | Model value |
  |---|---|
  | `PrincipalAccess`, `PrincipalAccess[]` | `Xrmd.Model.Security.PrincipalAccessValue(RecordReference Principal, AccessMask AccessMask)`, `PrincipalAccessValue[]` |
  | `RolePrivilege`, `RolePrivilege[]` | `PrivilegeGrant`, `PrivilegeGrant[]` |
  | `AccessRights` | `AccessMask` |

  Arrays convert for the exact array type only, as for `Entity[]`. The mirror pairs these values by instance, like any other value,
  so a plug-in that reads or replaces a `PrincipalAccess` or a `RolePrivilege` sees the same CLR types, values and in-place semantics
  as before. A `RolePrivilege` that goes back into the caller's own instance keeps its `RecordFilterId` and `RecordFilterUniqueName`,
  which the model does not carry. Unlike `SdkRecords`, the mirror does not range-check the privilege depth and keeps a null element,
  so a bad depth or a null privilege still reaches the core and gets today's 400.
- **Async payloads (note for S.2i).** `SdkRemoteContext.ModelCanCarry` and the model writers (`ContextXml`, `ContextJson`) do not
  know these three value types. A context that holds one is serialized from the SDK view, as before, so the `asyncoperation.data` of
  an async step on GrantAccess or AddPrivilegesRole and the body of a webhook on those messages do not change. Before S.2i removes the
  SDK fallbacks, the own writer and reader must learn `PrincipalAccessValue`, `PrivilegeGrant` and `AccessMask`, with the bytes the SDK
  serializer writes today.
- **Typed responses and requests.** `CoreResponses.Typed(name)` covers every moved message (`GrantAccessResponse`,
  `RetrieveUserQueuesResponse`, `WhoAmIResponse`, `InitializeFromResponse`, ...), with the class each SDK branch returned before. The
  queue messages write `queueitem` and `queuemembership` rows through the nested delegate as model `Create`, `Update`, `Delete` and
  `Associate` requests. `CoreResponses.TypedRequest(name)` pairs them with `CreateRequest`, `UpdateRequest`, `DeleteRequest` and
  `AssociateRequest`, so the pipeline, the guard and the plug-ins on those writes get today's request classes.
- **SDK twins.** A public member that another module calls keeps an SDK twin with the old name and signature in `src/Xrmd.Core/Sdk/`:
  `ShareAsync` (the migration `SharingPhase`), `AccessMessages.ExecuteAsync` (the Web API and
  `OrganizationService`), and `RetrieveUserPrivilegesAsync`, `InitializeFromAsync`, `RetrieveAppComponentsAsync`, `WhoAmI` and the
  import job and unpublished helpers that `OrganizationService` calls. Each twin reads the request in through a mirror of the call and
  answers with the typed response. A member that nothing outside the core called lost its SDK form: `CurrencyMessages.ExecuteAsync`,
  `ChangeTracker.ReadRequest` and `QueueMessages.ExecuteAsync` exist on the model only.
- **Security stays on the SDK.** The moved code calls `SecurityGuard` and the Security row rules through their model overloads where
  S.2d made one, and through bridges in `CoreSdkBridges` otherwise (the access checks, the share rows, the record team checks, the role
  grants, and the share and privilege audit rows). As in S.2e3b, a bridge runs the helper's guard first. S.2e3c2a removes these bridges.
  `ChangeTracker` still answers with the SDK `BusinessEntityChanges`, built by a bridge, because it has no model twin yet.
- **The held window.** The model request is current when the core runs a moved message, so the core no longer uses the held SDK
  request for them. The pipeline still holds a window for the moved messages that go through it, which costs one write and one read
  of the request, as before; the core ignores it. S.2e3c2b removed it for the core's model messages (see below).
- **Images.** `RetrieveImageAsync` reads the image on the model (`ReadImageAsync`). When the pipeline named a mirror for the read
  (`CoreHandOff.ReadImagesWith`), the record is written out into that mirror once, so its SDK twin there is the core's own `Entity`
  and an image still costs one conversion.

The SDK usage count of Core is 51 (55 before S.2e3c1): `RecordTeams.cs`, `AccessMessages.cs`, `CurrencyMessages.cs`,
`ChangeTracker.cs`, `QueueMessages.cs` and `InitializeFrom.cs` name no SDK type any more, and the new `CoreOperations.Access.cs`,
`CoreOperations.Roles.cs` and `CoreOperations.NonPipeline.cs` are SDK-free. Abstractions stays 8 and Pipeline 30.

### The guard and the Security rules on the model (S.2e3c2a)

S.2e3c2 is split in two. S.2e3c2a moves the stage-20 guard (`SecurityGuard`) and the Security row rules (`SystemTableRules`,
`CurrencyRules`, `DuplicateRuleRules`, `EnvironmentVariableRules`, `OrganizationSettingRules`, `RollupJobRules`, `AccessTeamRules`
and `SecurityAdministration`) onto `Request`, `Record`, `RecordReference` and `TableDefinition`, runs the guard on the pipeline's
model request and removes the Security bridges of `CoreSdkBridges`. S.2e3c2b moved the rest of stage 20 (see below).
External contracts do not change: the response, row, plug-in context, remote context, metadata and row parameter goldens are
byte-identical to the code before S.2e3c2a, and so are the new guard fault goldens in `tests/fixtures/guard-fault-golden/` (45
scenarios, written on the code before the move by `GuardFaultGoldenTests`: the fault, the request after the call and the rows written,
on SQL Server and PostgreSQL).

- **Model values.** The guard and the rules match model values, as the core does since S.2e3b: `Record`, `RecordReference`,
  `RecordReferenceCollection`, `RelationshipName`, `RecordCollection`, `ChoiceValue`, `MoneyValue`, `PrincipalAccessValue`,
  `PrivilegeGrant`, `AccessMask`, `ParameterBag` and the three model queries (a fetch query is parsed from `FetchQuery.Text`). They
  read `TableDefinition` and `ColumnDefinition` from `IMetadataStore.GetTable`. The model members:

  | Member | Model signature |
  |---|---|
  | `SecurityGuard.AuthorizeAsync` | `(Request, Caller, DataSession, ct) : Task<bool>`, `true` when the guard changed the request |
  | `SecurityGuard.DemandAccessAsync` | `(Caller, RecordReference, AccessMask, DataSession, ct)` |
  | `SecurityGuard.RetrievePrincipalAccessAsync` | `(RecordReference, RecordReference, DataSession, ct) : Task<AccessMask>` |
  | `SecurityGuard.ShareRowsAsync` | `(TableDefinition, …) : Task<IReadOnlyList<(Guid, int, AccessMask)>>` |
  | `SystemTableRules.EnforceAsync` | `(Request, Caller, Guid, SecurityCache, IMetadataStore, DataSession, ct)` |
  | `SystemTableRules.EnforceFieldSharesAsync` | `(Request, …) : Task<bool>`, `true` only when a field share Create passes |
  | `SystemTableRules.DemandValidProcessWrite` | `(string, Record, Caller)` |
  | `CurrencyRules`, `DuplicateRuleRules`, `EnvironmentVariableRules`, `OrganizationSettingRules`, `RollupJobRules` | the same members on `Request` and `Record`; `DuplicateRuleRules.IdOf` matches `RecordReference` or `Guid` |
  | `AccessTeamRules` | `DemandTemplateAsync(…, Record, …)`, `RecordTeamParameters(Request)`, `DemandRecordTeamAsync(…, RecordReference, …)`, `TeamShareMaskAsync(…) : Task<AccessMask?>`, `MarkChecked`, `DemandSameAsChecked` and `DemandTeamShareChecked` on `Request` |
  | `SecurityAdministration` | `TryGetPairs(RecordReference, RecordReferenceCollection, …)`, `AddRoleAsync`, `RemoveRoleAsync`, `AddProfileAsync`, `RemoveProfileAsync` on `RecordReference`, `RoleGrantsAsync(DataSession, Guid, ct) : Task<IReadOnlyList<PrivilegeGrant>>` |

- **SDK twins.** A public member that a caller outside Security still calls with SDK types keeps a twin with the old name and
  signature, in `src/Xrmd.Security/Sdk/SecurityGuard.Sdk.cs` and `src/Xrmd.Security/Sdk/SecurityRules.Sdk.cs` (the classes are
  `partial`). A twin reads its arguments in with a mirror of the call (`new SdkMirror()`), runs the model member and writes back into
  the caller's objects only what the model member changed. Tests count as callers. A member that nothing outside Security calls with
  SDK types lost its SDK form (`RetrievePrincipalAccessAsync`, `ShareRowsAsync`, `EnforceFieldSharesAsync`, `RecordTeamParameters`,
  `DemandRecordTeamAsync`, `TryGetPairs`, `RemoveProfileAsync`, the duplicate rule, environment variable, organization setting and
  rollup job members).

  | SDK twin | Why it stays |
  |---|---|
  | `SecurityGuard.AuthorizeAsync(OrganizationRequest, …)` | `SolutionCascadeDeleter` and `SolutionUninstaller` only (the pipeline's create-only Upsert guards the model request since S.2e3c2b); it also carries the request's `CascadeHint` onto the model request |
  | `SecurityGuard.AuthorizeAsync(Request, SdkMirror, …)` | The pipeline's crossing form (below) |
  | `SecurityGuard.DemandAccessAsync(Caller, EntityReference, AccessRights, …)` | The workflows (`WorkflowActivation`, `ExecuteWorkflow`), `IncomingEmailImporter`, `SolutionUninstaller`, `SolutionCascadeDeleter` |
  | `SystemTableRules.EnforceAsync(OrganizationRequest, …)` | `SolutionImporter`, `SolutionCascadeDeleter`, tests (the `DemandValidProcessWrite` twin was removed in S.2e3c2b: its only caller, the pipeline, runs the model member) |
  | `CurrencyRules.DemandRateInRange(Entity)` | Tests |
  | `AccessTeamRules.MarkChecked(OrganizationRequest, …)`, `CopyMark`, the `RecordTeamTarget(EntityMetadata, …, AccessRights, …)` constructor | The handshake test of `RecordTeamTests`, which marks an SDK request |
  | `SecurityAdministration.AddRoleAsync`, `RemoveRoleAsync`, `AddProfileAsync` (`EntityReference`), `RolePrivilegesAsync` (`RolePrivilege`), `FromSdk` | The migration `SecurityPhase`, tests. `RolePrivilegesAsync` keeps its SDK form because C# cannot overload on the return type; the model member is `RoleGrantsAsync` |

  A twin converts only when the model member could act: `SystemTableRules.EnforceAsync` passes an unknown message
  without converting anything.
- **Texts keep the SDK names.** `src/Xrmd.Security/SdkTypeNames.cs` (internal, SDK-free: the SDK names are string literals) keeps
  today's texts on model values:
  - `SdkTypeNames.Of(Type)` gives the SDK simple name of a model type (`RecordReference` → "EntityReference", `ChoiceValue` →
    "OptionSetValue", `MoneyValue` → "Money", arrays as `X[]`), and any other type its own `Name`: "not a EntityReference" and "got a
    Money" read as before.
  - `SdkTypeNames.Text(object)` prints what the SDK value's `ToString()` printed (the full SDK type name of a converted class, the
    schema name of a relationship).
  - `SdkValue<T>(this Record, string)` reads a column as `Record.GetValue<T>` does, and turns a wrong-typed value's
    `InvalidCastException` into the runtime's text with the SDK type names (`CastText`), as `Entity.GetAttributeValue<T>` threw it.
    That text reaches a plug-in through the sandbox gate (`0x80040216` with the exception's message). Every model read of the guard and
    the rules that replaced a `GetAttributeValue` uses it; `SdkTypeNamesTests` compares it with the SDK read, and
    `SecurityModelTests.Cast_errors_keep_todays_sdk_text` pins it through the pipeline.
- **The crossing before the guard.** `PipelineExecutor.RunAsync` crosses the request into the model (`mirror.In`, the
  `AuditActionHint` copy, `SdkMirror.Register`, the bag pairing) before the guard instead of after it, and runs the guard on the model
  request through `AuthorizeAsync(Request, SdkMirror, …)`. The crossing moves; none is added. The custom API and action validators
  that follow the guard still read the SDK request, so a custom action now crosses before its validators run; the order of the checks
  does not change.
- **The `Out` after a field share Create.** The guard changes the request only for a field share Create
  (`SystemTableRules.ShareRules`: `objectid`, `objecttypecode`, `readaccess`, `updateaccess`). Then the crossing form writes the model
  out once (`mirror.Out`), so the caller's `Entity` holds those values, with a new `objectid` instance, as before, and the validators
  and steps read them. A field share Create refused after the fill is written out too, so the caller's `Target` keeps the filled values
  as before. Nothing is written out for any other request.
- **The record team mark.** `AccessTeamRules.RecordTeamTarget` is `(TableDefinition Table, Guid RecordId, Guid UserId, Guid TemplateId,
  AccessMask Mask, Guid? OwningBusinessUnit)` and replaces the core's `RecordTeams.Target`. `MarkChecked`, `DemandSameAsChecked` and
  `DemandTeamShareChecked` key a weak table on the model request. The guard marks the pipeline's model request, which is the one the
  core gets, so `RecordTeams` finds the mark with no bridge. The SDK `MarkChecked(OrganizationRequest, …)` keeps its own table, and
  `PipelineCoreSdk` copies a mark from it onto the model request at its crossing (`AccessTeamRules.CopyMark`), as `RunAsync` copies
  `AuditActionHint`.
- **The cascade mark.** `PipelineExecutor.CoreNestedAsync` no longer copies `CascadeHint` onto the SDK twin of a core-issued nested
  request: the guard and `IsCoreCascade` read the mark on the model request. The copy in `CoreOperations.CrudNested` and the SDK
  `CascadeHint` overloads stay, for the SDK callers of `ExecuteCoreAsync` (S.2e3c3b).
- **The bridges removed.** From `CoreSdkBridges`: `EnforceCurrencyRulesAsync`, `DemandRateInRange`, `DemandAccessTeamTemplateAsync`,
  `ApplyDuplicateRuleColumnsAsync`, `ApplyEnvironmentVariableRowAsync`, `ApplyOrganizationSettingRowAsync`, `SecurityPairs`,
  `AddRoleAsync`, `RemoveRoleAsync`, `AddProfileAsync`, `RemoveProfileAsync`, `DemandAccessAsync`, `RetrievePrincipalAccessAsync`,
  `ShareRowsAsync`, `DemandRecordTeamAsync`, `DemandSameAsChecked`, `TeamShareMaskAsync`, `DemandTeamShareChecked`, `CheckedRequest`,
  `RoleGrantsAsync` and the private `Sdk(RecordTeams.Target)`. The core calls the model members directly. Their bridge tests in
  `CoreSdkBridgeTests` became tests of the model members in `SecurityModelTests`, which also prove that the row rules on a plain table
  cross no bridge. The share and role privilege audit bridges (S.2f), the change tracking answer and the Mail, Elastic, Duplicates,
  Rollups and Files bridges stay.

The pipeline path converts nothing more: the counts of `PipelineModelBoundaryTests` do not change. The SDK twins convert one small
request (a Delete of one reference, a Create of one target) for the solution uninstall callers, and the
workflow Target for `DemandValidProcessWrite` (until S.2e3c2b, which removed that twin). Three more twin conversions are new on callers that ran the SDK rules
without one before: `SystemTableRules.EnforceAsync(OrganizationRequest, …)` reads in each request of a known message, once per imported
row in `SolutionImporter` and, together with the `AuthorizeAsync` twin, twice per delete in `SolutionCascadeDeleter`, and the
`DemandAccessAsync` twin reads in one reference for Workflows and incoming mail.

The SDK usage count of Security is 6 (13 before S.2e3c2a): `PrivilegeCatalog` and `SecurityCache` (the SDK-typed metadata hooks),
`Sdk/SdkSecurityTwins.cs`, `Sdk/SdkSecurityTypes.cs`, `Sdk/SecurityRules.Sdk.cs` and `Sdk/SecurityGuard.Sdk.cs`. Core stays 51,
Abstractions 8 and Pipeline 30.

### Stage 20 on the model (S.2e3c2b)

S.2e3c2b moves the rest of stage 20 onto the model request: the custom API authoring, the registration validation, the process
writes, the service endpoint checks and the mail secrets run on `Request`, `Record`, `RecordReference` and `ChoiceValue`, as the
guard has since S.2e3c2a, and the held window of the core's model messages is gone. External contracts do not change: the Web API,
SOAP, webhook and `asyncoperation.data` bytes, the stored rows, the audit rows, the fault codes and texts and the in-place changes to
the caller's request are the same as before. The response, row, plug-in context, remote context, metadata, row parameter and guard
fault goldens are byte-identical, and 33 new goldens in `tests/fixtures/stage20-golden/` (`Stage20GoldenTests`, written on the code
before the step) pin each check's request after, rows, secrets and fault. They cover the custom API, registration, process, service
endpoint and mail scenarios and the real-time workflow owner.

- **Model members.** Every check reads and sets model values. A read that replaced a `GetAttributeValue<T>` uses
  `SdkValue<T>(this Record, string)` (see S.2e3c2a), so a wrong-typed value keeps the `InvalidCastException` text with the SDK type
  names that reaches a plug-in through the sandbox gate. A member that answers `bool` answers true when it changed its Target.

  | Check | Model member |
  |---|---|
  | Custom API authoring | `CustomApiAuthoring.BeforeCreateAsync(Request, Record, DataSession, …) : Task<bool>`, `BeforeUpdateAsync(Record, Record, …) : Task<bool>`, `BeforeDeleteAsync(RecordReference, …) : Task<Guid?>`; its own writes are model requests through `ExecuteCoreAsync` with `CoreCall(Caller.Internal, …)` |
  | Registration validation | `RegistrationValidator.ValidateAsync(string, Record, Record?, DataSession, …) : Task<bool>`, `DemandNotEventHandlerAssociate(Request)` |
  | Process writes | `SystemTableRules.DemandValidProcessWrite(string, Record, Caller)`, the workflow `type` default as `ChoiceValue(1)`, the Workflows hook (below) |
  | Service endpoints | `ServiceEndpointRules.ValidateAsync(Record, Record?, bool, WebhookSecrets, DataSession, …)`, `EndpointProblem(Record, bool)`; `WebhookSecrets.TakeAsync(Record, DataSession, …) : Task<Guid?>` (a non-null answer means the Target changed) |
  | Mail secrets | `PendingMailSecrets.Extract(Record, TableDefinition)`, `Strip(Record, TableDefinition) : bool`, `StripRequest(Request, TableDefinition) : bool`, `Claim(Record)`, `Carry(Record, Record)`, `Carries(Record)` |

  The stored images the checks read, and the owner lookup of real-time workflows, come from `RetrieveImageAsync` with no mirror named
  and convert nothing. `SdkImageAsync` is removed.
- **SDK twins.** The classes are `partial` and their SDK twins are in `src/Xrmd.Pipeline/Sdk/StageChecks.Sdk.cs`. A twin keeps the
  old name and signature, reads its arguments in with a mirror of its own, calls the model member and writes back into the caller's
  objects what the member changed. Twins stay only for callers outside the pipeline:

  | Twin | Callers |
  |---|---|
  | `RegistrationValidator.ValidateAsync(string, Entity, Entity?, …)` | `SolutionImporter` (S.2g) |
  | `ServiceEndpointRules.EndpointProblem(Entity, bool)` | `ServiceEndpointRulesTests` |
  | `WebhookSecrets.TakeAsync(Entity, …)` | `UpsertInterleavingTests` (PostgreSQL) |
  | `StandardUpsert.MarkCreateOnly(OrganizationRequest)`, `IsCreateOnly(OrganizationRequest)` | `OrganizationService` (S.2e3c3b) |

  The twins only the pipeline called are removed, including the SDK `DemandValidProcessWrite` and the SDK forms of the custom API
  `Before*` members. `CustomApiAuthoring.AfterDeleteAsync` sends model requests and has no twin.
- **Write-back after the checks.** `HoldChecksMayWrite`, which wrote the request out before the checks of its tables and read it back
  after them, is removed. The checks run on `run.Request`; when one answers true, the pipeline writes the model request out once
  (`mirror.Out`), so the caller's Target holds what it held before: the custom API defaults and `sdkmessageid`, the registration
  fills, a workflow's `type` 1, an endpoint's id and `isauthvalueset` without `authvalue`, and the Targets without their mail
  secrets. When a check refuses, the model is written out if the table is one a check writes (the private `StageCheckTables`: the
  custom API and registration tables, `workflow`, `processtrigger`, `serviceendpoint` and the tables with mail secret columns), so a
  refusal after a partial change leaves the caller's Target as before. Nothing is written out for another table, and a check that
  changed nothing crosses nothing, except on a Create or Update of `workflow`, whose Workflows hook is always
  called through its lent view. The write-out keeps the caller's column order: `SdkRecords.Refill` of a `DataCollection` removes the
  keys the model lacks and sets the others in the model's order, so kept columns stay where they were and a new column takes a freed
  slot, as a direct write on the `Entity` did (`Checks_keep_the_caller_column_order` pins it).
- **The Workflows hook behind its lent view.** `IWorkflowPipelineHook` keeps its SDK signatures (S.2h). The pipeline calls
  `ValidateWriteAsync` through a lend of the context (`LentAsync`) with the view's `Target`: the caller's own `Entity`, written from
  the model by the lend and read back on return, as for every other hook. `OnSetStateAsync` and `OnAssignedAsync` take ids and are
  called directly. The lend is new on `workflow` writes only.
- **Mail secrets keyed on the `Record`.** `PendingMailSecrets` keys its pending values on the model Target. The core claims them with
  `PendingMailSecrets.Claim(Record)` in its model Create and Update (`CoreOperations.Messages.cs`); only the pipeline strips, and it
  strips the model request (`StripRequest`) after the other checks of stage 20. `CoreSdkBridges.TakeMailSecrets` calls the model `Extract` without
  crossing. The SDK twins `CoreOperations.CreateAsync` and `UpdateAsync` no longer claim. The secrets are carried at the crossing: a
  bulk inner execution's Target is the same `Record` the outer Targets held, and an Upsert's inner Create or Update gets the outer
  Target's values when its request crosses in (`PendingMailSecrets.Carry`), not in `StandardUpsert.Copy`. A write of a mail table
  that carries no secret no longer crosses at all.
- **Marks on the model request.** `InnerExecution.Mark(Request, InnerKind)` and `KindOf(Request)` and
  `StandardUpsert.MarkCreateOnly(Request)` and `IsCreateOnly(Request)` key the model request; `InnerExecution.cs` names no SDK type.
  `RunInnerAsync` passes an `InnerRun(Kind, SecretsFrom)` to a private `ExecuteAsync`, and `RunAsync` marks the model request right
  after the crossing. The SDK form of the create-only mark keeps its own table, and `RunAsync` copies it onto the model request at the
  crossing (`StandardUpsert.CopyCreateOnly`), as it copies `AuditActionHint`. The create-only guard of an Upsert runs
  `AuthorizeAsync(Request, …)` on the model Target after the stage-20 checks and writes that Target out when the guard changed it.
- **`AnswersOnModel`.** `IPipelineCore.AnswersOnModel(string messageName)` is true for a core message that is not in
  `CoreSdkMessages.Bridged` (`CoreOperations`, case-insensitive; the test wrappers delegate). `Run.CoreOnModel` is computed once in
  `RunAsync`: it is false for a custom action or API, an Upsert, a bulk message and `ExecuteWorkflow`, whose stage 30 is not the core's
  model dispatch. The checks run first, then the held window opens (one `Out`) only when `CoreOnModel` is false: the window carries
  what the checks wrote, and a message the core answers on the model has no window at all, so the SDK request is no longer written and
  read for it. This was the rule at S.2e3c2b. Since S.2e3c3a, standard Upsert and the bulk messages open no window either
  (`Run.MergeOnModel`), so the window remains only for custom actions and APIs, XAML actions, `ExecuteWorkflow` and the bridged
  messages (see the S.2e3c3a section below).
- **Conversions removed.** A message the core answers on the model loses the window's `Out` and `In` of its request. A write of a
  check table loses the `Out` before the checks and the `In` after them; one `Out` remains when a check wrote. Each stage-20 image and
  the real-time owner lookup convert nothing. The counts of `PipelineModelBoundaryTests` do not change.
- **Other changes.** `SecurityGuard.MayChange` is internal and shared with the create-only guard, which runs on the model Target after the
  checks, as the SDK guard read the SDK Target after them before. `Xrmd.Security` gives `InternalsVisibleTo` to `Xrmd.Pipeline` for `SdkValue<T>`.

The SDK usage counts are Core 51, Abstractions 8, Pipeline 25 (30 before S.2e3c2b: `InnerExecution.cs`, `CustomApiAuthoring.cs`,
`RegistrationValidator.cs`, `ServiceEndpointRules.cs`, `WebhookSecrets.cs` and `PendingMailSecrets.cs` name no SDK type any more, and
the twins are the new `Sdk/StageChecks.Sdk.cs`) and Security 6.

The view is transitional. The other modules move to the model in S.2d to S.2h, and S.2i removes the view, the twins and the bridge.

### Standard Upsert and bulk on the model (S.2e3c3a)

S.2e3c3 is split in two. S.2e3c3a (this section) moves stage 30 of standard Upsert and of the bulk messages onto the model, which
S.2e3c2b left on the held window. S.2e3c3b moves the SDK edge of the core: `OrganizationService`, `RetrieveMultipleModelRequest`,
`MetadataOperations` and the metadata reads in `CoreSdkMessages.Bridged`, the core entry adapter, and the callers of the
`PipelineCoreSdk` extension methods in Workflows, Solution and Admin. The two do not depend on each other. External contracts do not
change: the responses (CLR types included), faults (codes, texts and order), stored rows, plug-in contexts, asynchronous payloads and the
in-place changes to the caller's request (column order and shared instances included) are the same as before. The goldens of earlier
steps are byte-identical, and 25 new goldens in `tests/fixtures/merge-golden/` (`MergeGoldenTests`, written on the code before the step)
pin the Upsert, CreateMultiple, UpdateMultiple, UpsertMultiple and DeleteMultiple scenarios: the caller's request after the call, the
stage-40 contexts and images, the stored rows, the async operations and the faults.

- **What exists.** The standard-table bulk messages the pipeline runs are `CreateMultiple`, `UpdateMultiple` and `UpsertMultiple`
  (`BulkStage.Handles`). `DeleteMultiple` never reaches the pipeline as a bulk message: on a standard table `OrganizationService`
  refuses it with Dataverse's text, and on an elastic table it runs each target as a single Delete. The elastic Upsert and the elastic
  bulk path move with Elastic in S.2e4.
- **Model members.** `BulkTargets` and `StandardUpsert` are `partial`; `BulkStage` has no SDK half. The model half is SDK-free: no
  `Microsoft.*` using and no SDK type name, also not in a cref. Every fault text is the text of the SDK member, through private
  helpers that both halves share.

  | Class | Model members |
  |---|---|
  | `BulkTargets` | the constants, `MessageOf`, `IsRepeated`; `RecordTargetList` and `Read(Request)` (the `Record` targets of the four bulk messages, the `RecordReference` targets of DeleteMultiple); `DemandOneTable(RecordTargetList, string?)`; `IdOf(TableDefinition, object?)`; `TargetIds(Request, TableDefinition, Guid[]?)` |
  | `StandardUpsert` | `Applies(string?, TableDefinition?)`; `ExecuteAsync(Record, TableDefinition, IPipelineCore, DataSession, Func<Request, Task<Response>>, bool, ct) : Task<(bool Created, Guid Id)>`; `CarryParameters(Request, Request)`; `MarkCreateOnly(Request)`, `IsCreateOnly(Request)`; the private `Copy`, which copies `LogicalName`, the new `Id`, `RowVersion`, the attributes in source order (without the computed values: `ComputedColumnRules.IsComputed` and `RollupColumns.IsRollupValue` over `TableDefinition.Attributes`) and `KeyAttributes`, and nothing else |
  | `BulkStage` | `Handles(string?)`, `FromPluginMessage(string)`; `Applies(string?, TableDefinition?)`; `ExecuteAsync(Request, TableDefinition, Func<Request, Task<Response>>, ct) : Task<Response?[]>`, the model response of each target in order, null for a repeated UpdateMultiple target that did not run |

- **SDK halves.** One file, `src/Xrmd.Pipeline/Sdk/MergeStages.Sdk.cs`, holds what callers outside the pipeline still call with SDK types:

  | SDK member | Callers |
  |---|---|
  | `BulkTargets.TargetList`, `Read(OrganizationRequest)`, `TableOf`, `DemandOneTable(TargetList, …)`, `TableNameOf`, `IdOf(EntityMetadata, …)`, `DemandDistinctIds`, `Prepare`, `Response` | `OrganizationService`, `ElasticBulkMessages` (S.2e3c3b, S.2e4), and the pipeline's primary entity and bulk response |
  | `StandardUpsert.MarkCreateOnly(OrganizationRequest)`, `IsCreateOnly(OrganizationRequest)`, `CopyCreateOnly(OrganizationRequest, Request)` and the table they key | `OrganizationService` (S.2e3c3b), `RunAsync` |

  The SDK members that only stage 30 called are removed: `StandardUpsert.ExecuteAsync`, `Applies` and `Copy` on SDK types,
  `BulkStage.ExecuteAsync` and `Applies` on SDK types, and `BulkTargets.TargetIds(OrganizationRequest, …)`. One rule remains for the
  parameters of an inner request, the model `StandardUpsert.CarryParameters(Request, Request)`, shared by the Upsert and bulk.
- **Typed responses.** The pipeline's public entry still returns an `OrganizationResponse` until S.2i, so the response is the typed SDK
  object of today. The Upsert branch builds the `UpsertResponse` (`ResponseName` "Upsert", `RecordCreated`, `Target` as a new
  `EntityReference`) from the `(Created, Id)` of `StandardUpsert.ExecuteAsync`. The bulk branch (`BulkAsync`) builds the response with
  the SDK `BulkTargets.Response` from the SDK twins of the model singles, so an `UpsertMultipleResponse` holds the inner runs' own
  `UpsertResponse` instances. A single without its own SDK twin is a server bug and throws `InvalidOperationException`; it cannot
  happen today, because `RunInnerAsync` always answers with the paired model response or a shell, which pairs. Both are built in
  `PipelineExecutor.Sdk.cs`.
- **Inner requests from twins.** `RunInnerAsync(Request, InnerKind, Run, ct)` pairs the model inner request with today's typed SDK
  request (`CreateRequest`, `UpdateRequest` or `UpsertRequest`) and fills its `Parameters` by assignment, never by writing a `Record`
  out: each model value takes its existing SDK twin from the chain mirror, and a value without one is written out. The only `Record`
  without a twin is the Upsert's copy. `PairUpsertCopy` gives it a new `Entity` whose attributes and key attributes are, key by key in
  the copy's order, the instances the outer Target's SDK twin holds. That twin holds every key of the copy, because the Target crossed
  in from the caller's request and every change before stage 30 is written out, so a missing twin or key throws
  `InvalidOperationException` (a server bug) instead of a silent written-out value. So an inner step sees the same instances in the
  same order as before, the inner Create's or Update's Target is the caller's own `Entity` for a bulk row, and the inner crossing
  pays the same one `In` as before. The answer is the model twin of the inner response, and the Upsert reads it in only when it is an
  unread shell (the read rule of S.2e3a). The test seam `PipelineExecutor.InnerRequest` (`AsyncLocal`, internal) lets a test see each
  inner SDK request.
- **No window at the core.** `Run.MergeOnModel` is true when `UpsertTable` or `BulkTable` is set. It joins `CoreOnModel` in
  `StageChecksAsync`, which writes the request out once when a check changed it. `HoldAtCoreAsync` of a merge run neither writes the
  model out nor calls `CoreHandOff.Hold`: the SDK request is already current, because the guard, the checks and the create-only guard
  write out what they change. The held window now opens only for custom actions and APIs, XAML actions and `ExecuteWorkflow` (S.2h) and
  for the bridged messages (S.2e3c3b, S.2e4, S.2f). Two reads of the outer SDK request stay until S.2i: the read before a lend of the
  level (`HeldLevels`, `SyncBoundaries`), and the read-back in `ReleaseCore` after stage 30. The merge level is registered in `AtCore`
  for them (`HeldAtCore`, `OwnsAtCore`). They keep today's semantics for SDK instances the caller shares: an inner step that changes in
  place an `OptionSetValue` held by several rows (or by the caller's Upsert Target and the copy) changes the caller's objects as before,
  the next inner crossing reads it, and the outer stage 40 sees what the inner runs did. A model-authoritative outer level would write
  stale values over the caller's shared instances, so it is not an option without a recorded divergence.
- **Images and ids on the model.** The pre and post image collections of a bulk run take their ids from the model request
  (`BulkTargets.TargetIds(run.Request, run.BulkTable, created)`, with `created` the `Ids` of the SDK response of a CreateMultiple), and
  `Run.UpsertTable` and `Run.BulkTable` are `TableDefinition`s read with `IMetadataStore.GetTable`. `Run.StandardTable` and
  `CommitsPerStage` keep their `EntityMetadata`. A missing Upsert Target keeps its text: "Required parameter 'Target' of request
  'Upsert' is missing or not a Entity." (`SdkTypeNames.Of(typeof(Record))`).
- **Conversions removed.** A merge run loses the window's opening `Out` of its whole request, and an inner request costs what it cost
  before (its Target is assigned, not written, and read in once at the inner crossing). The probes in `MergeGoldenTests` count
  `SyncCount` of one run each on a fresh mirror:

  | Probe | Before | Task 3 | Task 4 |
  |---|---|---|---|
  | Upsert that creates | 18 | 16 (−2, the `Out` of the Upsert request) | 16 |
  | CreateMultiple of 10 | 156 | 156 | 144 (−12, the `Out` of the CreateMultiple request) |
  | UpsertMultiple of 2, one existing | 39 | 35 (−4, the window `Out` of the two inner Upserts) | 31 (−4, the `Out` of the UpsertMultiple request) |

  A bulk run of T targets of size S now pays about 2·T·S (each inner `In` and the closing `In`) instead of 3·T·S. `MergeModelTests`
  asserts these counts and that no merge run fires `CoreHandOff.Held`. The counts of `PipelineModelBoundaryTests` only go down.

The SDK usage counts are Core 51, Abstractions 8, Pipeline 23 (25 before S.2e3c3a: `StandardUpsert.cs` and `BulkStage.cs` name no SDK
type any more, and `BulkTargets.cs` became SDK-free as its SDK half moved into `Sdk/MergeStages.Sdk.cs`, which counts) and Security 6.

## Tests on PostgreSQL

The integration tests run on SQL Server by default. `XRMD_TEST_PROVIDER` chooses the engine of the run:

| Value | Engine |
|---|---|
| `sqlserver` (or not set; also `mssql`) | SQL Server in a Testcontainers container |
| `postgres` (also `postgresql`, `pg`) | PostgreSQL 16 (`postgres:16-alpine`) in a Testcontainers container; the server runs with `Storage:Provider=PostgreSql` |

```bash
XRMD_TEST_PROVIDER=postgres dotnet test tests/Xrmd.Integration.Tests \
  --filter "Category!=Acceptance&FullyQualifiedName~ChangeTracking"
```

The backup tests on PostgreSQL run `pg_dump` and `pg_restore` of the machine: `XRMD_TEST_PG_TOOLS` names their directory;
without it the tests take the highest `/usr/lib/postgresql/<major>/bin` (16 or later) that has `pg_dump`, else the one on
`PATH`. Use version 18, as the server image does (the CI job installs `postgresql-client-18`).

Test traits and attributes that concern the engine:

| Marker | Meaning |
|---|---|
| `[Trait("Category", "Postgres")]` | The class starts a PostgreSQL container of its own and runs on both providers' runs: the meta schema parity, the bootstrap, the parity tests, the sweep of the features PostgreSQL got in 2.7b (`PostgresFeatureSweepTests`), the live dialect tests of `Xrmd.Query.Tests` |
| `[SqlServerFact("<reason>")]` | A test of a SQL Server engine artifact (a T-SQL migration, a database option, a `sys.*` view). It is skipped on PostgreSQL with the reason. Never use it to skip a feature on PostgreSQL: every feature runs on both engines |

Rules for code that touches SQL:

- SQL text that differs between the engines comes from `ISqlDialect` (`src/Xrmd.Abstractions/Storage/ISqlDialect.cs`), with
  `SqlServerDialect` and `PostgresDialect` (`src/Xrmd.Storage.PostgreSql`). Parameters are added with `AddParameter` and a
  `DbType`. `SqlClientConfinementTests` fails when `Microsoft.Data.SqlClient` is used outside the SQL Server classes, or
  `Npgsql` outside the PostgreSQL project.
- What PostgreSQL cannot do as SQL Server does is refused with `ProviderGuard.Unavailable(<feature>)` (HTTP 501), never
  answered differently. Since 2.7b the one such refusal is the PostgreSQL dialect's `like` with character ranges.
- `TsqlConfinementTests` fails when T-SQL constructs (`TOP`, lock hints, `OPENJSON`, `SYSUTCDATETIME`, `ISNULL`, …) appear in
  string literals outside the SQL Server classes: take them from `ISqlDialect`.
- A meta schema migration from 0019 on has two files with the same number: `src/Xrmd.Metadata/Migrations/NNNN_x.sql` and
  `src/Xrmd.Metadata/Migrations/PostgreSql/NNNN_x.sql`. `MigrationParityTests` fails when one is missing. PostgreSQL starts
  from `Migrations/PostgreSql/0018_baseline.sql`.
- `docker/docker-compose.postgres.yml` runs a server built from the clone on PostgreSQL (see
  [PostgreSQL organizations](postgresql.md#running-from-a-clone)).
- The server image builds `pg_dump`, `pg_restore` and `libpq` from PostgreSQL's source tarball, pinned by version and
  sha256 (`PG_VERSION` and `PG_SHA256` in `docker/Dockerfile`, stage `pgtools`). For a PostgreSQL security release, change
  `PG_VERSION`, `PG_SHA256` (the `.sha256` file published next to the tarball on `ftp.postgresql.org`) and the `version` of
  the `postgresql` entry in `eng/licenses/image-packages.json` together, in one pull request (`LicensesTests` fails when the
  two versions differ), then regenerate the notices with `eng/licenses.sh notices --out THIRD_PARTY_NOTICES.md`.
