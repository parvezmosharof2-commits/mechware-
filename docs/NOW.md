# NOW (keep ≤ 30 lines; update at the end of every unit)

**Done (2026-11-13):** C10.1 bulk-import dry run (`POST /api/v1/admin/catalog/import/dry-run`, docs/catalog/IMPORT_FORMAT.md; `kA` unit added) ·
H3a part 1 Dockerfiles ×4 + staging compose · H3a.2 deploy readiness (one DB setting, optional S3, persisted Data Protection keys,
`Mechware.Worker migrate`) · pnpm pinned to 10.34.9 (`packageManager`; keeps the lockfile's security overrides) ·
A8.1/A8.2 PDF engine EN/BN (ADR-0009) · H1.4 metadata in `<head>`, Lighthouse mobile 92-95 · D2.1 price-list design (ADR-0008) ·
F3a.1/.2a/.3 email + OTP SMS limits · company page `?tab=`. Earlier: C6 ✅, C7 ✅ (C7.6 deferred), H1 ✅, A6 ✅.
Branch `claude/c6-search-and-module-plan` (pushed; PR not opened yet — open it on GitHub).

**Next:** security unit — encrypt TOTP secrets (`users.two_factor_secret`) at rest with FieldEncryption (Opus) · C10.2 import commit ·
then MODULES.md order (D2.2+ waits for owner decisions MODULES.md §5 #7; C3 waits for S3-vs-volume).

**How work runs:** main session Opus plans and verifies; routine code → Sonnet sub-agent; money/security → Opus sub-agent
(memory `feedback-subagent-delegation`). Tools: `python scripts/test_module.py <ID>`, `python scripts/check_cards.py`,
`python scripts/api_snapshot.py [--out <dir>]` after API changes (`--out` when a running API locks bin/Release).

**Before starting:** Docker Desktop running → `docker compose -f infra/docker/docker-compose.yml up -d postgres redis meilisearch mailpit`.
Seeded catalogue lives in DB `mechware` (the default for API and Worker; one setting `ConnectionStrings__DefaultConnection`).
Deploy: `docker run --env-file … mechware-worker migrate` before starting a new api/worker (docs/modules/H3.md).

**Owner actions (start now):** VAT consultant · lawyer · SSLCommerz/bKash merchant · SMS sender ID + email domain · pen-test booking ·
hosting choice (staging) · 5 real buyers + real product data. Open decisions: [MODULES.md](MODULES.md) §5.
