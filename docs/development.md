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
- `eng/test-shard.sh <project> <shard> <count>` prints the test filter of one shard. Classes are spread by number of
  test cases; the last shard takes every test the others do not, so no test is left out.

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
