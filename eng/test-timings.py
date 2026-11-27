#!/usr/bin/env python3
"""Seconds per test class from `dotnet test --logger trx` result files, as eng/test-shard.sh reads them.

    eng/test-timings.py <file.trx>...              print "<class>\t<seconds>", slowest first
    eng/test-timings.py --merge <file.tsv>...      merge printed timing lists (one per shard) into one, slowest first
    eng/test-timings.py --merge --serial <collection> --sources <dir> <file.tsv>...
                                                   the same, the classes outside that xUnit collection weighed less

CI prints the timings of each integration shard at the end of its log, between the lines "test-timings: begin" and
"test-timings: end". To refresh eng/test-timings/Xrmd.Integration.Tests.tsv, save the lines between those markers of
each shard of a pull request run to a file and run `eng/test-timings.py --merge shard*.tsv > eng/test-timings/...`.

A class's time is the wall time from the end of the test that ended last before its first test started (any class) to
the end of its last test: its class fixture and the start of its collection's fixture count (a test's own duration
does not), which the sum of its tests' durations would miss. Only the relative weights matter to the shard split.

xUnit runs the classes of one collection one after the other and the collections side by side. In
Xrmd.Integration.Tests most classes share the "xrmd" collection (one database), so a shard lasts about as long as
its "xrmd" classes; the other collections run beside them and only compete for the CPU. With --serial, a class
outside that collection (read from the [Collection("...")] attributes under --sources) weighs PARALLEL_SHARE of
its time. To refresh the committed file:

    eng/test-timings.py --merge --serial xrmd --sources tests/Xrmd.Integration.Tests shard*.tsv \
      > eng/test-timings/Xrmd.Integration.Tests.tsv
"""
import bisect
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from datetime import datetime

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
PARALLEL_SHARE = 0.2
CLASS = re.compile(r'(\[Collection\("([^"]+)"\)\][^{;]*?)?\b(?:public|internal)\s+(?:(?:sealed|abstract|partial|static)\s+)*class\s+(\w+)')


def seconds(duration: str) -> float:
    # hh:mm:ss.fffffff
    h, m, s = duration.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def instant(text: str) -> float:
    # 2026-11-27T06:41:28.1234567+00:00: at most 6 fraction digits for fromisoformat.
    head, sep, rest = text.partition(".")
    if sep:
        digits = ""
        while rest and rest[0].isdigit():
            digits, rest = digits + rest[0], rest[1:]
        text = f"{head}.{digits[:6]}{rest}"
    return datetime.fromisoformat(text).timestamp()


def from_trx(paths):
    totals = defaultdict(float)
    for path in paths:
        root = ET.parse(path).getroot()
        classes = {}
        for test in root.iter(NS + "UnitTest"):
            method = test.find(NS + "TestMethod")
            if method is not None:
                classes[test.get("id")] = method.get("className")
        runs = []  # (start, end, class)
        for result in root.iter(NS + "UnitTestResult"):
            cls = classes.get(result.get("testId"))
            if not cls:
                continue
            if result.get("startTime") and result.get("endTime"):
                runs.append((instant(result.get("startTime")), instant(result.get("endTime")), cls))
            elif result.get("duration"):
                totals[cls] += seconds(result.get("duration"))
        if not runs:
            continue
        first, last = {}, {}
        for start, end, cls in runs:
            first[cls] = min(first.get(cls, start), start)
            last[cls] = max(last.get(cls, end), end)
        ends = sorted(end for _, end, _ in runs)
        origin = min(start for start, _, _ in runs)
        for cls in first:
            i = bisect.bisect_right(ends, first[cls])
            before = ends[i - 1] if i > 0 else origin
            totals[cls] += last[cls] - min(before, first[cls])
    return totals


def from_tsv(paths):
    totals = defaultdict(float)
    for path in paths:
        with open(path, encoding="utf-8") as f:
            for line in f:
                parts = line.rstrip("\n").split("\t")
                if len(parts) == 2 and parts[0] and not parts[0].startswith("#"):
                    totals[parts[0]] += float(parts[1])
    return totals


def collections(sources):
    """Namespace.Class -> xUnit collection name, or None for a class without a [Collection] attribute."""
    found = {}
    for folder, _, files in os.walk(sources):
        for name in files:
            if not name.endswith(".cs"):
                continue
            with open(os.path.join(folder, name), encoding="utf-8", errors="replace") as f:
                text = f.read()
            ns = re.search(r"namespace\s+([\w.]+)", text)
            for m in CLASS.finditer(text):
                found[f"{ns.group(1) if ns else ''}.{m.group(3)}".lstrip(".")] = m.group(2)
    return found


def main(argv):
    serial = sources = None
    if argv[:1] == ["--merge"]:
        rest = argv[1:]
        while rest[:1] in (["--serial"], ["--sources"]) and len(rest) > 1:
            if rest[0] == "--serial":
                serial = rest[1]
            else:
                sources = rest[1]
            rest = rest[2:]
        if not rest or (serial is None) != (sources is None):
            print(__doc__.strip(), file=sys.stderr)
            return 2
        totals = from_tsv(rest)
    elif argv:
        totals = from_trx(argv)
    else:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    if serial is not None:
        owner = collections(sources)
        for cls in totals:
            if owner.get(cls.split("+")[0]) != serial:
                totals[cls] *= PARALLEL_SHARE
    for cls, secs in sorted(totals.items(), key=lambda kv: (-kv[1], kv[0])):
        print(f"{cls}\t{secs:.1f}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
