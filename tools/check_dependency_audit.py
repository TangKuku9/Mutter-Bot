"""Fail closed when a NuGet JSON audit reports vulnerabilities or feed problems."""
import json
import sys
from pathlib import Path


def failures(value):
    if isinstance(value, dict):
        for key, child in value.items():
            if key in {"vulnerabilities", "problems", "errors"} and child:
                yield key, child
            else:
                yield from failures(child)
    elif isinstance(value, list):
        for child in value:
            yield from failures(child)


report = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
if report.get("version") != 1 or not report.get("projects"):
    raise SystemExit("Unrecognized or empty NuGet audit report")
issues = list(failures(report))
if issues:
    for kind, details in issues:
        print(kind + ": " + json.dumps(details, ensure_ascii=False))
    raise SystemExit(1)
print("NuGet audit: no known dependency vulnerabilities or feed problems")
