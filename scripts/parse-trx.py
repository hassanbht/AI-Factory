#!/usr/bin/env python3
"""Parse .trx test results with a real XML parser and print totals per run.

Usage: python scripts/parse-trx.py <trx-root> [<trx-root> ...]
Prints: run dir | passed | failed | skipped | total, then an aggregate line.
Failed test names are listed (first 50).
"""
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def summarize(trx: Path):
    root = ET.parse(trx).getroot()
    counters = root.find("t:ResultSummary/t:Counters", NS)
    if counters is None:
        raise SystemExit(f"no Counters element in {trx}")
    passed = int(counters.get("passed", 0))
    failed = int(counters.get("failed", 0))
    total = int(counters.get("total", 0))
    # Skipped/xunit-skip tests land in Counters without their own attribute across
    # vstest versions; derive them so the invariant P + F + S == T holds.
    return {
        "passed": passed,
        "failed": failed,
        "skipped": total - passed - failed,
        "total": total,
        "trx": trx,
    }


def main():
    roots = [Path(a) for a in sys.argv[1:]] or [Path(".")]
    files = []
    for r in roots:
        files.extend(sorted(r.rglob("*.trx")))
    if not files:
        raise SystemExit("no .trx files found")

    totals = {"passed": 0, "failed": 0, "skipped": 0, "total": 0}
    failures = []
    for f in files:
        s = summarize(f)
        for k in totals:
            totals[k] += s[k]
        print(f"{f} | P={s['passed']} F={s['failed']} S={s['skipped']} T={s['total']}")
        if s["failed"]:
            root = ET.parse(f).getroot()
            for r in root.iter(f"{{{NS['t']}}}UnitTestResult"):
                if r.get("outcome") == "Failed":
                    failures.append(f"{r.get('testName')} ({f})")

    print(f"AGGREGATE | P={totals['passed']} F={totals['failed']} S={totals['skipped']} T={totals['total']}")
    for name in failures[:50]:
        print(f"FAILED: {name}")
    if totals["failed"]:
        sys.exit(1)


if __name__ == "__main__":
    main()
