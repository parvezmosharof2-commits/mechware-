# MECHWARE — rules for Claude

## Start of every session (read only these)
1. This file → `docs/NOW.md` → the current card in `docs/modules/<ID>.md` → that card's context pack.
2. Do **not** read `docs/MODULES.md` in full unless choosing the next module; never read the whole `docs/modules/` folder.
3. Work in **units**: one unit = one PR = one session, 0.5–1.5 days, ≤ ~400 production lines, ≤ 12 files, 1 migration, backend **or** frontend. Too big → split first.
4. At the end of a unit: update the card's HANDOFF block and `docs/NOW.md`.

## Never open (token cost, stale or generated)
`backend/src/Mechware.Infrastructure/Persistence/Migrations/**` · `backend/src/Mechware.Infrastructure/Seed/**` · `docs/archive/**` ·
`docs/discovery/synthetic-*.md` · `docs/API_CONTRACT.md` and `docs/DATA_MODEL.md` (stale v1; use the card + Swagger) ·
`docs/03_PHASE_PROMPTS.md` · `docs/100K_GRADE_REVIEW_AND_PLAN.md` · `docs/*TEST_REPORT*.md` · `PROGRESS.md` below its header ·
`docs/prototypes/sprint1/mechware-prototype.html` · `frontend/apps/web/src/app/[locale]/design-system/page.tsx` · `frontend/pnpm-lock.yaml`.
For a large file, grep for the method instead of reading it (e.g. `IdentityService.cs`, `OrderService.cs`).

## Models and sub-agents
Per unit type (owner-approved, `docs/MODULES.md` §6 and memory `feedback-subagent-delegation`): design/threat models → Fable
(limit hit → Opus high, tell the owner); money/security code → Opus; UI/CRUD → Sonnet; test runs/doc updates → Haiku.
Pick the model at session start; don't switch mid-session. Say in one line which agent/model is used; verify sub-agent results
by running the tests yourself.

## Testing on this PC
Docker Desktop running, then `docker compose -f infra/docker/docker-compose.yml up -d postgres redis meilisearch`.
Integration tests **skip** (look green) without Postgres/Redis/Meilisearch: always check `Skipped: 0`.
Before push: `dotnet format backend/Mechware.sln --verify-no-changes`, `pnpm --dir frontend typecheck`, `pnpm --dir frontend lint`.
