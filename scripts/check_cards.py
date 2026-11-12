#!/usr/bin/env python3
"""Static check of the module cards (docs/modules/*.md, except _*.md).

* Every backticked token that starts with backend/ frontend/ docs/ infra/ scripts/ .github/ must exist
  ({a,b} braces are expanded, * and ** are globs that must match at least one file).
* Every `~Mechware.UnitTests...` / `~Mechware.IntegrationTests...` filter must match a namespace declared under
  backend/tests/<project>/ (the same namespace or a sub-namespace).
Paths of files a FUTURE unit will create go in plain text (no backticks). Other backticked text is ignored (--verbose lists it).
Exit 1 and `card: missing path X` lines on failure.  Usage: python scripts/check_cards.py [--verbose]
"""
import glob
import itertools
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ROOTS = ("backend/", "frontend/", "docs/", "infra/", "scripts/", ".github/")
TICKS = re.compile(r"`([^`\n]+)`")
FILTER = re.compile(r"~(Mechware\.(UnitTests|IntegrationTests)(?:\.[A-Za-z0-9_.]*[A-Za-z0-9_])?)")
NS = re.compile(r"^\s*namespace\s+([A-Za-z0-9_.]+)", re.M)


def expand(token):
    m = re.search(r"\{([^{}]*)\}", token)
    if not m:
        return [token]
    return list(itertools.chain.from_iterable(expand(token[:m.start()] + o + token[m.end():]) for o in m.group(1).split(",")))


def exists(path):
    path = path.rstrip("/")
    if "*" not in path:
        return (ROOT / path).exists()
    parts = re.split(r"(\*+)", path)
    pattern = "".join(p if p.startswith("*") else glob.escape(p) for p in parts)
    return any(True for _ in glob.iglob(str(ROOT / pattern), recursive=True))


def namespaces(project):
    found = set()
    for f in (ROOT / "backend" / "tests" / project).rglob("*.cs"):
        if any(p in ("obj", "bin") for p in f.parts):
            continue
        found.update(NS.findall(f.read_text(encoding="utf-8-sig", errors="replace")))
    return found


def main():
    verbose = "--verbose" in sys.argv
    errors, skipped, ns_cache, checked = [], [], {}, 0
    for card in sorted((ROOT / "docs" / "modules").glob("*.md")):
        if card.name.startswith("_"):
            continue
        text = card.read_text(encoding="utf-8")
        for tok in TICKS.findall(text):
            tok = tok.strip()
            f = FILTER.search(tok)
            if f:
                prefix, project = f.group(1), "Mechware." + f.group(2)
                ns = ns_cache.setdefault(project, namespaces(project))
                checked += 1
                if not any(n == prefix or n.startswith(prefix + ".") for n in ns):
                    errors.append(f"{card.name}: missing namespace {prefix} (no .cs under backend/tests/{project})")
                continue
            if not tok.startswith(ROOTS) or re.search(r"\s", tok):
                skipped.append(f"{card.name}: {tok}")
                continue
            clean = re.split(r"[#:]", tok, maxsplit=1)[0] if not re.match(r"^[A-Za-z]:", tok) else tok
            for p in expand(clean):
                checked += 1
                if not exists(p):
                    errors.append(f"{card.name}: missing path {p}")
    if verbose:
        print("skipped (not repo paths):")
        for s in skipped:
            print("  ", s)
    if errors:
        print("\n".join(sorted(set(errors))))
        print(f"check_cards: {len(set(errors))} problem(s)")
        return 1
    print(f"check_cards: OK, {checked} paths/filters checked, {len(skipped)} tokens skipped")
    return 0


if __name__ == "__main__":
    sys.exit(main())
