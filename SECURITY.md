# Security policy

## Supported versions

Security fixes are made for the **latest release** only. Older releases do not receive fixes: upgrade to the latest
release (`xrmd server upgrade` for a server installation, a new `xrmd` executable or `dotnet tool update -g xrmd` for the
CLI).

## Reporting a vulnerability

Please do not open a public issue, discussion or pull request for a vulnerability.

Report it privately with **GitHub private vulnerability reporting**: open the repository's **Security** tab and choose
**Report a vulnerability**. Only the maintainers can read the report.

Please include:

- the affected component (server, `xrmd` CLI, web admin, Docker image) and its version (`xrmd --version`, or the
  release tag);
- the configuration that matters (for example `Auth:Mode`, `XRMD_BIND`, an external SQL Server, the files store);
- the steps to reproduce, or a proof of concept, and what an attacker gains;
- whether the issue is already known to others or published anywhere.

Do not include real customer data or credentials in the report.

## What happens next

- We confirm that we received the report.
- We reproduce the issue and tell you whether we accept it as a vulnerability, and how severe we think it is.
- We work on a fix in private and keep you informed of the progress. If we need more information, we ask in the report.
- We publish the fix in a new release together with a GitHub security advisory, and credit you unless you prefer not to
  be named.
- Please keep the details private until the fixed release is out. If you plan to publish, tell us when, so we can agree
  on a date.

## Secure defaults

These defaults are deliberate; changing them is a decision of the person who runs the installation.

- **Loopback binding.** The ports of the server are bound to `127.0.0.1`. Set `XRMD_BIND=0.0.0.0` in `.env` to expose
  them on the network.
- **Authentication required.** Every API endpoint requires a token. The exceptions are the liveness probe `/healthz`,
  the web admin's static files with its public boot settings `/ui/config.json` (never a secret), and the Prometheus
  endpoint `/metrics`, which is anonymous because a scraper has no token: do not expose it to untrusted networks, or turn
  it off with `Observability:Metrics:Enabled=false`. Without `Auth:Mode` the server rejects every token.
- **Dry runs by default.** Schema apply (Admin API and MCP `apply_schema`) only plans unless the caller passes
  `dryRun=false`.
- **No default passwords.** `xrmd server init` generates the SQL Server password and the development signing key; the
  repository compose file refuses to start without `SA_PASSWORD`, and the server refuses to start in Development mode
  without a signing key of at least 32 characters.

## SBOM and vulnerability scanning

- Every release attaches a CycloneDX SBOM of the CLI (`xrmd-cli-<version>.cdx.json`) and of the server image
  (`xrmd-server-<version>.cdx.json`).
- Each archive, the dotnet tool package and the server image (`/app/THIRD_PARTY_NOTICES.md`) carry the third-party
  notices.
- Pull requests and releases are scanned for vulnerable dependencies: NuGet packages, including transitive ones, and the
  web admin's production npm packages. A High or Critical advisory fails the pull request check and blocks the release.
  Moderate and Low advisories are reported as warnings. Known exploited vulnerabilities are checked by hand before each
  release.

## Telemetry

No telemetry leaves the installation. The server sends traces only to an OTLP endpoint that you configure
(`Observability:Otlp:Endpoint`), and serves metrics only to whoever scrapes `/metrics` on your network.
