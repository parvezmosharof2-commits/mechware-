#!/usr/bin/env python3
"""
Mutation check, by hand (the team's practice since sprint 0): for every security or money condition, remove or break it and
confirm at least one test fails. A mutant that survives is a missing test, or a condition that does nothing.

Usage (run it in a SEPARATE git worktree, never in the folder you work in: it rewrites source files while it runs):

    git worktree add ../mw-mutation HEAD
    MUTANTS=my_mutants FILTER="FullyQualifiedName~MyTests|FullyQualifiedName~OtherTests" \
        python3 scripts/mutation/run.py ../mw-mutation results.json            # every mutant
    ... run.py ../mw-mutation results.json some-mutant-name another-one         # only these (re-run)

  * MUTANTS: a python module on the path next to this file (default "mutants") that defines M, a list of
    (name, file relative to the worktree, exact old text, new text). The old text must occur exactly once in the file,
    otherwise the mutant is reported as BAD-PATTERN and skipped.
  * FILTER: the `dotnet test --filter` expression; keep it to the tests that cover the code being mutated, because each
    mutant costs one build and one test run (about 1.5-2 minutes).
  * results.json is written after every mutant, so an interrupted run resumes where it stopped (finished names are skipped).
  * Needs Postgres and Redis running and MECHWARE_TEST_PG / MECHWARE_TEST_REDIS exported (see docs/SESSION_NOTES_*.md).

Outcomes: KILLED (a test failed, or the build broke), KILLED(timeout) (the tests hung: count it, but ask why they hang),
SURVIVED (read it: add a test, or, if the condition really changes nothing, say so and remove or comment it).
Equivalent mutants are real; write down why instead of forcing a test.
"""
import importlib
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
M = importlib.import_module(os.environ.get("MUTANTS", "mutants")).M
W = os.path.abspath(sys.argv[1])
OUT = os.path.abspath(sys.argv[2])
only = set(sys.argv[3:])
filt = os.environ.get("FILTER") or "FullyQualifiedName~Security"
TEST_TIMEOUT_SECONDS = 300


def sh(command, timeout=None):
    try:
        return subprocess.run(command, shell=True, cwd=W, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        subprocess.run(f"pkill -f '{W}/backend/tests'", shell=True)
        return None


res = json.load(open(OUT)) if os.path.exists(OUT) else {}
for name, path, old, new in M:
    if only and name not in only:
        continue
    if name in res and not only:
        continue
    target = os.path.join(W, path)
    source = open(target).read()
    if source.count(old) != 1:
        res[name] = f"BAD-PATTERN({source.count(old)})"
        print(name, res[name], flush=True)
        continue
    open(target, "w").write(source.replace(old, new))
    try:
        build = sh("dotnet build backend/ShopVerse.slnx -v q -nologo 2>&1 | tail -3")
        if " 0 Error(s)" not in build.stdout:
            res[name] = "KILLED(compile)"
        else:
            test = sh(f'dotnet test backend/ShopVerse.slnx --no-build --filter "{filt}" -nologo 2>&1', timeout=TEST_TIMEOUT_SECONDS)
            if test is None:
                res[name] = "KILLED(timeout: tests hang)"
            else:
                failed = [l.strip().split(" [")[0] for l in test.stdout.splitlines() if l.strip().startswith("Failed ShopVerse")]
                res[name] = ("KILLED: " + "; ".join(failed[:3])) if test.returncode != 0 else "SURVIVED"
    finally:
        open(target, "w").write(source)   # always put the original back
    print(name, res[name], flush=True)
    json.dump(res, open(OUT, "w"), indent=1, ensure_ascii=False)
