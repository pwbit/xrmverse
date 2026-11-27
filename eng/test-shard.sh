#!/usr/bin/env bash
# Prints the `dotnet test --filter` expression of one shard of a test project, so CI can run a slow project
# (Xrmd.Integration.Tests) as N parallel jobs.
#
#   eng/test-shard.sh <test project> <shard, 1-based> <shard count>
#
# The project must be built (the test list comes from `dotnet test --no-build --list-tests`). Test classes are spread
# over the shards by their weight, heaviest first, each to the least loaded shard (ties to the lowest index), so the
# split is the same on every runner. A class's weight is its measured time in eng/test-timings/<project name>.tsv
# (written by eng/test-timings.py); a class missing there weighs its number of test cases times the average seconds
# per test case of the listed classes; with no timings file, every class weighs its number of test cases.
# Shards 1..N-1 select their classes; the last shard selects every test that is NOT in another shard, so a test the
# list missed (or a class added since) still runs somewhere.
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

timings="$(dirname "$0")/test-timings/$(basename "${project%/}" .csproj).tsv"
[[ -f "$timings" ]] || timings=""

printf '%s\n' "$classes" | awk -v shard="$shard" -v count="$count" -v timings="$timings" '
  BEGIN {
    # "<class>\t<seconds>" lines; "#" starts a comment line.
    if (timings != "") while ((getline line < timings) > 0) {
      split(line, f, "\t")
      if (f[1] != "" && f[1] !~ /^#/ && f[2] + 0 > 0) secs[f[1]] = f[2] + 0
    }
  }
  { cases[$2] = $1; listed[++m] = $2 }
  END {
    # Seconds per test case of the timed classes, for the classes not timed yet.
    for (i = 1; i <= m; i++) if (listed[i] in secs) { tsecs += secs[listed[i]]; tcases += cases[listed[i]] }
    per = tcases > 0 ? tsecs / tcases : 1
    for (i = 1; i <= m; i++) {
      c = listed[i]
      w[c] = (c in secs) ? secs[c] : cases[c] * per
    }
    # Heaviest first, ties by name (a stable order on every runner).
    for (i = 1; i <= m; i++) order[i] = listed[i]
    for (i = 2; i <= m; i++) {
      c = order[i]; j = i - 1
      while (j >= 1 && (w[order[j]] < w[c] || (w[order[j]] == w[c] && order[j] > c))) { order[j + 1] = order[j]; j-- }
      order[j + 1] = c
    }
    n = m
    for (i = 1; i <= n; i++) {
      c = order[i]
      best = 1
      for (s = 2; s <= count; s++) if (load[s] < load[best]) best = s
      load[best] += w[c]
      ntests[best] += cases[c]
      owner[c] = best
    }
    sep = ""
    for (i = 1; i <= n; i++) {
      c = order[i]
      if (shard < count && owner[c] == shard) { out = out sep "FullyQualifiedName~" c "."; sep = "|" }
      if (shard == count && owner[c] != count) { out = out sep "FullyQualifiedName!~" c "."; sep = "&" }
    }
    printf "test-shard: shard %d of %d, %d test cases, weight %.0f of %.0f\n", shard, count, ntests[shard], load[shard], total(load, count) > "/dev/stderr"
    print out
  }
  function total(a, k,   s, t) { for (s = 1; s <= k; s++) t += a[s]; return t }'
