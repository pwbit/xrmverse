#!/usr/bin/env bash
# Prints the `dotnet test --filter` expression of one shard of a test project, so CI can run a slow project
# (Xrmd.Integration.Tests) as N parallel jobs.
#
#   eng/test-shard.sh <test project> <shard, 1-based> <shard count>
#
# The project must be built (the test list comes from `dotnet test --no-build --list-tests`). Test classes are spread
# over the shards by their number of test cases, largest first, each to the least loaded shard (ties to the lowest
# index), so the split is the same on every runner. Shards 1..N-1 select their classes; the last shard selects every
# test that is NOT in another shard, so a test the list missed (or a class added since) still runs somewhere.
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: eng/test-shard.sh <test project> <shard> <count>" >&2
  exit 2
fi

project="$1"
shard="$2"
count="$3"
if ! [[ "$shard" =~ ^[0-9]+$ && "$count" =~ ^[0-9]+$ ]] || (( shard < 1 || shard > count )); then
  echo "test-shard: shard must be within 1..count" >&2
  exit 2
fi

if (( count == 1 )); then
  echo "FullyQualifiedName!=__none__"
  exit 0
fi

list="$(dotnet test "$project" --no-build --list-tests)"

# Test names follow the "The following Tests are available:" line, indented by four spaces. xUnit names them
# Namespace.Class.Method (nested classes Outer+Inner, theories with "(args)"): the class is the name without its
# arguments and its last segment.
classes="$(printf '%s\n' "$list" \
  | awk '/The following Tests are available:/ { on = 1; next } on && /^    [^ ]/ { sub(/^    /, ""); print }' \
  | sed -E 's/\(.*$//; s/\.[^.]+$//' \
  | sort | uniq -c | sort -k1,1nr -k2,2)"

if [[ -z "$classes" ]]; then
  echo "test-shard: no tests listed for $project" >&2
  exit 1
fi

printf '%s\n' "$classes" | awk -v shard="$shard" -v count="$count" '
  {
    best = 1
    for (s = 2; s <= count; s++) if (load[s] < load[best]) best = s
    load[best] += $1
    owner[$2] = best
    order[++n] = $2
  }
  END {
    sep = ""
    for (i = 1; i <= n; i++) {
      c = order[i]
      if (shard < count && owner[c] == shard) { out = out sep "FullyQualifiedName~" c "."; sep = "|" }
      if (shard == count && owner[c] != count) { out = out sep "FullyQualifiedName!~" c "."; sep = "&" }
    }
    printf "test-shard: shard %d of %d, %d test cases\n", shard, count, load[shard] > "/dev/stderr"
    print out
  }'
