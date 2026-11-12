#!/usr/bin/env python3
"""Run the tests named by a module card and print only the counts.

Usage: python scripts/test_module.py <ID> [--allow-skip]     (e.g. C6)
Collects the `~Mechware.UnitTests...` / `~Mechware.IntegrationTests...` filters of docs/modules/<ID>.md, runs
`dotnet test backend/tests/<project> --filter "FullyQualifiedName~a|FullyQualifiedName~b"` per project, parses the TRX files.
Exit 1 on failure or when any test was skipped (integration tests skip without Postgres/Redis/Meilisearch) unless --allow-skip.
"""
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FILTER = re.compile(r"~(Mechware\.(UnitTests|IntegrationTests)(?:\.[A-Za-z0-9_.]*[A-Za-z0-9_])?)")
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    allow_skip = "--allow-skip" in sys.argv
    if len(args) != 1:
        print(__doc__)
        return 2
    card = ROOT / "docs" / "modules" / f"{args[0]}.md"
    if not card.exists():
        print(f"no card {card}")
        return 2
    filters = {}
    for tok in re.findall(r"`([^`\n]+)`", card.read_text(encoding="utf-8")):
        for m in FILTER.finditer(tok):
            filters.setdefault("Mechware." + m.group(2), [])
            if m.group(1) not in filters["Mechware." + m.group(2)]:
                filters["Mechware." + m.group(2)].append(m.group(1))
    if not filters:
        print(f"{args[0]}: card has no test filters")
        return 2
    bad = False
    for project, prefixes in sorted(filters.items()):
        flt = "|".join(f"FullyQualifiedName~{p}" for p in prefixes)
        with tempfile.TemporaryDirectory() as out:
            proc = subprocess.run(
                ["dotnet", "test", str(ROOT / "backend" / "tests" / project), "--filter", flt,
                 "--logger", "trx;LogFilePrefix=r", "--results-directory", out, "--verbosity", "quiet"],
                cwd=ROOT, capture_output=True, text=True)
            passed = failed = skipped = 0
            failing = []
            trx = list(Path(out).glob("*.trx"))
            for f in trx:
                for r in ET.parse(f).getroot().iterfind(".//t:UnitTestResult", NS):
                    o = r.get("outcome")
                    if o == "Passed":
                        passed += 1
                    elif o == "Failed":
                        failed += 1
                        failing.append(r.get("testName"))
                    else:
                        skipped += 1
            print(f"{project} [{', '.join(prefixes)}]: passed {passed}, failed {failed}, skipped {skipped}")
            for n in failing:
                print(f"  FAILED: {n}")
            if not trx:
                print("  NO TRX produced (build error?). Last output:")
                print("\n".join((proc.stdout + proc.stderr).strip().splitlines()[-15:]))
                bad = True
            elif passed + failed + skipped == 0:
                print("  WARNING: filter matched 0 tests")
                bad = True
            if failed or proc.returncode not in (0, 1) and not failing:
                bad = True
            if skipped and not allow_skip:
                print("  !!! integration tests skipped: start docker compose -f infra/docker/docker-compose.yml up -d postgres redis meilisearch")
                bad = True
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
