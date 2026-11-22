#!/usr/bin/env bash
set -euo pipefail
# One-time setup in the order CI uses: web admin first (ui/dist is linked into the host), then .NET.
corepack enable
# corepack picks the pnpm of the packageManager field of the current directory, so run pnpm inside ui/.
cd ui
pnpm install --frozen-lockfile
pnpm build
cd ..
dotnet restore
dotnet build --no-restore
