#!/usr/bin/env python3
"""Regenerate the committed OpenAPI snapshot and the TypeScript types made from it (A6.4).

    python scripts/api_snapshot.py [--no-build]

Needs only the .NET SDK and `pnpm install` in frontend/. No database, Redis or search server: the Swashbuckle CLI builds the
host without running it (Program.cs skips the identity-store boot check for the `dotnet-swagger` process).
Writes frontend/packages/api-client/openapi.json and frontend/packages/api-client/src/schema.d.ts.
"""
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
API_DIR = ROOT / "backend" / "src" / "Mechware.Api"
DLL = API_DIR / "bin" / "Release" / "net10.0" / "Mechware.Api.dll"
CLIENT = ROOT / "frontend" / "packages" / "api-client"
SNAPSHOT = CLIENT / "openapi.json"


def run(cmd, **kw):
    shell = os.name == "nt" and cmd[0] == "pnpm"  # pnpm is a .cmd shim on Windows
    subprocess.run(cmd, check=True, shell=shell, **kw)


def main():
    if "--no-build" not in sys.argv:
        run(["dotnet", "build", str(API_DIR), "-c", "Release", "--nologo", "-v", "q"])
    run(["dotnet", "tool", "restore"], cwd=ROOT)
    with tempfile.TemporaryDirectory() as tmp:
        raw = pathlib.Path(tmp) / "swagger.json"
        env = {**os.environ, "ASPNETCORE_ENVIRONMENT": "Development"}
        # cwd = the Api project so appsettings.Development.json is found (dummy local keys only).
        run(["dotnet", "swagger", "tofile", "--output", str(raw), str(DLL), "v1"], cwd=API_DIR, env=env)
        doc = json.loads(raw.read_text(encoding="utf-8"))
    SNAPSHOT.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    run(["pnpm", "--dir", str(CLIENT), "run", "generate:types"])
    print(f"OK: {SNAPSHOT.relative_to(ROOT)} and src/schema.d.ts regenerated")


if __name__ == "__main__":
    main()
