#!/usr/bin/env bash
# Plan 12, B10: no shipped artifact contains a Microsoft SDK assembly.
#
#   eng/check-no-sdk.sh
#   eng/check-no-sdk.sh --artifacts <dir>
#   eng/check-no-sdk.sh --tree <dir>
#
# Without arguments: publishes the host (framework-dependent), the CLI (single-file, linux-x64) and packs the dotnet
# tool, then lists every file, including those inside the tool .nupkg, and looks inside the single-file bundle for the
# assembly names.
#
# --artifacts <dir>: checks the files a release ships, as the release job downloads them (no build). Lists every
# .tar.gz, .zip and .nupkg in <dir>, extracts the archives and looks inside each single-file xrmd / xrmd.exe (every
# RID) for the assembly names: a single-file bundle lists its embedded files by name in its manifest. Fails closed:
# no archive, no package, an unreadable archive or an archive without the executable is an error.
#
# --tree <dir>: checks an extracted filesystem, e.g. the server image after `docker create` + `docker export | tar -x`
# (no build). Looks for the assembly names as file names at any depth only: it does not look inside single-file bundles
# or nested archives (fine while the host is published framework-dependent). Fails closed: a missing or empty folder is an
# error.
#
# Exits 1 naming any SDK assembly found (or anything that could not be checked), 0 when there is none.
set -euo pipefail

caller="$PWD"   # a relative --artifacts folder is the caller's, not the repository root's
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

# Microsoft.Xrm.Sdk.Workflow.dll: the workflow SDK (Microsoft.CrmSdk.Workflow), installed by the customer with
# `xrmd sdk install` like the three SDK assemblies (plan 2.14, ruling W18, issue #45).
# Microsoft.SqlServer.Management.SqlParser.dll: a proprietary dependency of Meziantou.Framework.TdsServer, restored with
# ExcludeAssets="all" so that it never ships (plan 3.3, ruling T11, issue #46).
# Microsoft.Data.SqlClient.SNI.dll: the native Windows SNI library of SqlClient, under Microsoft's proprietary terms;
# excluded in Directory.Build.props, SqlClient uses its managed networking on Windows too (issue #15).
sdk_assemblies=(Microsoft.Xrm.Sdk.dll Microsoft.Crm.Sdk.Proxy.dll Microsoft.PowerPlatform.Dataverse.Client.dll
  Microsoft.Xrm.Sdk.Workflow.dll Microsoft.SqlServer.Management.SqlParser.dll Microsoft.Data.SqlClient.SNI.dll)

# What betrays an assembly inside a single-file bundle: its name in the bundle manifest. Not for the native SNI library,
# whose file name SqlClient itself carries as a P/Invoke target: there it is the package id, which the embedded deps.json
# lists only when the native asset ships.
bundle_marker() {
  case "$1" in
    Microsoft.Data.SqlClient.SNI.dll) echo "Microsoft.Data.SqlClient.SNI.runtime" ;;
    *) echo "$1" ;;
  esac
}

fail() {
  echo "check-no-sdk: $*" >&2
  exit 1
}

# The release assets in $1: archives (.tar.gz, .zip) with the single-file CLI, and the tool package (.nupkg).
check_artifacts() {
  local dir="$1"
  [[ -z "$dir" || "$dir" == /* ]] || dir="$caller/$dir"
  [[ -n "$dir" && -d "$dir" ]] || fail "--artifacts needs an existing folder (got '${dir}')"
  shopt -s nullglob
  local archives=("$dir"/*.tar.gz "$dir"/*.zip) packages=("$dir"/*.nupkg)
  shopt -u nullglob
  [[ ${#archives[@]} -gt 0 ]] || fail "no .tar.gz or .zip archive in $dir"
  [[ ${#packages[@]} -gt 0 ]] || fail "no .nupkg tool package in $dir"
  # Fail closed on anything the release would attach but this scan cannot inspect: only archives, tool packages and the
  # text SBOMs/notices (*.json, *.md) may ship.
  local other
  other="$(find "$dir" -mindepth 1 -maxdepth 1 ! \( -type f \( -name '*.tar.gz' -o -name '*.zip' -o -name '*.nupkg' -o -name '*.json' -o -name '*.md' \) \) -print -quit)"
  [[ -z "$other" ]] || fail "unscanned release asset $other (only .tar.gz, .zip, .nupkg, .json and .md are allowed)"

  # Global, not local: the EXIT trap runs after the function has returned.
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  : > "$tmp/files.txt"
  : > "$tmp/bundles.txt"

  local i=0 artifact base out bundle
  for artifact in "${archives[@]}" "${packages[@]}"; do
    base="$(basename "$artifact")"
    [[ -s "$artifact" ]] || fail "empty artifact $artifact"
    i=$((i + 1))
    out="$tmp/x$i"
    mkdir -p "$out"
    case "$base" in
      *.tar.gz)
        tar -tzf "$artifact" > "$tmp/list" 2>/dev/null || fail "cannot list $artifact"
        tar -xzf "$artifact" -C "$out" 2>/dev/null || fail "cannot extract $artifact"
        ;;
      *.zip)
        unzip -Z1 "$artifact" > "$tmp/list" 2>/dev/null || fail "cannot list $artifact"
        unzip -q -o "$artifact" -d "$out" > /dev/null 2>&1 || fail "cannot extract $artifact"
        ;;
      *.nupkg)
        unzip -Z1 "$artifact" > "$tmp/list" 2>/dev/null || fail "cannot list $artifact"
        ;;
    esac
    sed "s|^|${base}!|" "$tmp/list" >> "$tmp/files.txt"
    if [[ "$base" != *.nupkg ]]; then
      find "$out" -type f \( -name xrmd -o -name xrmd.exe \) > "$tmp/found"
      [[ -s "$tmp/found" ]] || fail "no xrmd or xrmd.exe executable in $artifact"
      while IFS= read -r bundle; do
        [[ -s "$bundle" ]] || fail "empty executable ${bundle#"$out"/} in $artifact"
        printf '%s\t%s\n' "$base!${bundle#"$out"/}" "$bundle" >> "$tmp/bundles.txt"
      done < "$tmp/found"
    fi
    echo "checked $base"
  done

  local found=0 name label path
  for name in "${sdk_assemblies[@]}"; do
    if grep -iE "(^|[/!])${name//./\\.}$" "$tmp/files.txt"; then
      echo "SDK assembly in a release asset: $name" >&2
      found=1
    fi
    while IFS=$'\t' read -r label path; do
      if grep -aqiF "$(bundle_marker "$name")" "$path"; then
        echo "SDK assembly embedded in the single-file executable $label: $name" >&2
        found=1
      fi
    done < "$tmp/bundles.txt"
  done

  [[ $found -eq 0 ]] || exit 1
  echo "No Microsoft SDK assembly in the release assets of $dir (${#archives[@]} archive(s), ${#packages[@]} package(s), $(wc -l < "$tmp/files.txt") entries, $(wc -l < "$tmp/bundles.txt") single-file executable(s) checked)."
}

# An extracted filesystem (the exported server image) in $1, looked at by file name.
check_tree() {
  local dir="$1"
  [[ -z "$dir" || "$dir" == /* ]] || dir="$caller/$dir"
  [[ -n "$dir" && -d "$dir" ]] || fail "--tree needs an existing folder (got '${dir}')"
  [[ -n "$(find "$dir" -type f -print -quit)" ]] || fail "no files in $dir"
  local name hits found=0 count
  for name in "${sdk_assemblies[@]}"; do
    hits="$(find "$dir" -type f -iname "$name" -print)"
    if [[ -n "$hits" ]]; then
      echo "SDK assembly in the image filesystem: $name" >&2
      echo "$hits" >&2
      found=1
    fi
  done
  [[ $found -eq 0 ]] || exit 1
  count="$(find "$dir" -type f | wc -l)"
  echo "No Microsoft SDK assembly in the image filesystem $dir ($count files checked)."
}

if [[ $# -gt 0 ]]; then
  case "$1" in
    --tree)
      check_tree "${2:-}"
      exit 0
      ;;
    --artifacts)
      check_artifacts "${2:-}"
      exit 0
      ;;
    *)
      fail "unknown argument '$1' (usage: eng/check-no-sdk.sh [--artifacts <dir> | --tree <dir>])"
      ;;
  esac
fi

# XRMD_CHECK_ARTIFACTS_DIR points at a prebuilt layout (host/, cli/xrmd, tool/*.nupkg) and skips publishing; the
# self-test in tests/Xrmd.Repository.Tests uses it.
if [[ -n "${XRMD_CHECK_ARTIFACTS_DIR:-}" ]]; then
  work="$XRMD_CHECK_ARTIFACTS_DIR"
else
  work="$(mktemp -d)"
  trap 'rm -rf "$work"' EXIT
  dotnet publish src/Xrmd.Host -c Release --nologo -v q -o "$work/host" >&2
  dotnet publish src/Xrmd.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true --nologo -v q -o "$work/cli" >&2
  dotnet pack src/Xrmd.Cli -c Release --nologo -v q -o "$work/tool" >&2
fi

# Fail closed: an artifact that is missing or empty was not checked.
if [[ -z "$(find "$work/host" -type f -print -quit 2>/dev/null)" ]]; then
  echo "check-no-sdk: no files in the host publish folder ($work/host)" >&2
  exit 1
fi
if [[ ! -s "$work/cli/xrmd" ]]; then
  echo "check-no-sdk: the single-file CLI is missing or empty ($work/cli/xrmd)" >&2
  exit 1
fi
shopt -s nullglob
packages=("$work"/tool/*.nupkg)
if [[ ${#packages[@]} -eq 0 ]]; then
  echo "check-no-sdk: no tool package in $work/tool" >&2
  exit 1
fi
for pkg in "${packages[@]}"; do
  if [[ ! -s "$pkg" ]]; then
    echo "check-no-sdk: empty tool package $pkg" >&2
    exit 1
  fi
done

: > "$work/files.txt"
(cd "$work/host" && find . -type f) | sed 's|^\./|host/|' >> "$work/files.txt"
(cd "$work/cli" && find . -type f) | sed 's|^\./|cli/|' >> "$work/files.txt"
for pkg in "${packages[@]}"; do
  unzip -Z1 "$pkg" | sed "s|^|tool/$(basename "$pkg")!|" >> "$work/files.txt"
done

found=0
for name in "${sdk_assemblies[@]}"; do
  if grep -iE "(^|[/!])${name//./\\.}$" "$work/files.txt"; then
    echo "SDK assembly in a shipped artifact: $name" >&2
    found=1
  fi
  # The single-file bundle lists its embedded assemblies by name in its manifest.
  if grep -aqF "$(bundle_marker "$name")" "$work/cli/xrmd"; then
    echo "SDK assembly embedded in the single-file CLI: $name" >&2
    found=1
  fi
done

if [[ $found -ne 0 ]]; then
  exit 1
fi
echo "No Microsoft SDK assembly in the host, the single-file CLI or the tool package ($(wc -l < "$work/files.txt") files checked)."
