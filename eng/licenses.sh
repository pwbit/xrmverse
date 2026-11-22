#!/usr/bin/env bash
# Third-party notices, license check and SBOM (tools/Xrmd.Licenses).
#
#   eng/licenses.sh notices --out THIRD_PARTY_NOTICES.md
#   eng/licenses.sh check   --notices THIRD_PARTY_NOTICES.md
#   eng/licenses.sh sbom    --scope cli|server --version <v> --out <file.cdx.json>
#   eng/licenses.sh vulnerabilities
#
# vulnerabilities (ruling P8) restores the solution, asks NuGet and the npm registry for advisories
# (dotnet list package --vulnerable --include-transitive, pnpm audit --prod) and fails on High or Critical ones.
#
# Builds src/Xrmd.Host and src/Xrmd.Cli in Release (their deps.json files tell what ships), lists every NuGet package
# of the solution and the web admin's npm packages, then runs the tool on those files and the NuGet cache.
# Exit codes are the tool's: 0 ok, 1 violations found, 2 usage or input error.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

if [[ $# -eq 0 ]]; then
  echo "usage: eng/licenses.sh notices|check|sbom|vulnerabilities [args]" >&2
  exit 2
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

nuget_packages="${NUGET_PACKAGES:-$HOME/.nuget/packages}"

if [[ "$1" == "vulnerabilities" ]]; then
  dotnet restore xrmverse.sln --nologo -v q >&2
  # Both commands may exit non-zero (dotnet list on a reported problem, pnpm audit on any advisory). The tool decides:
  # a reported problem, or output that is not a report, is exit 2, so a scan that did not run never passes.
  dotnet list xrmverse.sln package --vulnerable --include-transitive --format json > "$work/dotnet-vulnerable.json" || true
  pnpm -C ui audit --prod --json > "$work/pnpm-audit.json" || true
  dotnet build tools/Xrmd.Licenses -c Release --nologo -v q >&2
  dotnet tools/Xrmd.Licenses/bin/Release/net10.0/Xrmd.Licenses.dll "$@" \
    --dotnet "$work/dotnet-vulnerable.json" --pnpm-audit "$work/pnpm-audit.json"
  exit 0
fi

# dotnet list needs every project of the solution restored, not only the two that ship.
dotnet restore xrmverse.sln --nologo -v q >&2
dotnet build src/Xrmd.Host -c Release --nologo -v q >&2
dotnet build src/Xrmd.Cli -c Release --nologo -v q >&2
# A failed input is exit 2 (input error) with its output shown; exit 1 is reserved for "violations found".
if ! dotnet list xrmverse.sln package --include-transitive --format json > "$work/dotnet-packages.json"; then
  echo "dotnet list package failed:" >&2
  cat "$work/dotnet-packages.json" >&2
  exit 2
fi

pnpm -C ui install --frozen-lockfile >&2
if ! pnpm -C ui licenses list --json --prod > "$work/pnpm-licenses-prod.json"; then
  echo "pnpm licenses list --prod failed:" >&2
  cat "$work/pnpm-licenses-prod.json" >&2
  exit 2
fi
if ! pnpm -C ui licenses list --json > "$work/pnpm-licenses-all.json"; then
  echo "pnpm licenses list failed:" >&2
  cat "$work/pnpm-licenses-all.json" >&2
  exit 2
fi

dotnet build tools/Xrmd.Licenses -c Release --nologo -v q >&2
dotnet tools/Xrmd.Licenses/bin/Release/net10.0/Xrmd.Licenses.dll "$@" \
  --deps src/Xrmd.Cli/bin/Release/net10.0/xrmd.deps.json src/Xrmd.Host/bin/Release/net10.0/Xrmd.Host.deps.json \
  --nuget-packages "$nuget_packages" \
  --pnpm-licenses "$work/pnpm-licenses-prod.json" \
  --pnpm-licenses-all "$work/pnpm-licenses-all.json" \
  --all-packages "$work/dotnet-packages.json" \
  --image-packages eng/licenses/image-packages.json
