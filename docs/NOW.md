# NOW (keep ≤ 30 lines; update at the end of every unit)

**Done (2026-11-13):** E8.1 shipping zones + calculator · E8.2 shipping priced inside orders (server-side; no zones ⇒ Production refuses orders with SHIPPING_NOT_AVAILABLE until the owner enters tariffs; `POST /api/v1/orders/shipping-quote`; PO number on company orders) ·
E3.1/E3.2 cart api-client + `/cart` page + header badge + PDP Add to cart · E1 cart ✅ (E1.1 boundary + store contract · E1.2 Redis CAS store + signed guest token · E1.3 `/api/v1/cart` endpoints, server prices only ·
E1.4 atomic merge on login/refresh; carts need ONE Redis node, no Cluster; web cart UI comes with E3) · D5.1 quick-order parser · C10.3 console import screen
(seller is a temporary GUID field: needs an admin sellers list endpoint) · C10.2 import commit (`POST .../import/commit`, all-or-nothing, idempotent, advisory-locked; needs `sellerId`) · C10.1 bulk-import dry run (`POST /api/v1/admin/catalog/import/dry-run`, docs/catalog/IMPORT_FORMAT.md; `kA` unit added) ·
H3a part 1 Dockerfiles ×4 + staging compose · H3a.2 deploy readiness (one DB setting, optional S3, persisted Data Protection keys,
`Mechware.Worker migrate`) · pnpm pinned to 10.34.9 (`packageManager`; keeps the lockfile's security overrides) ·
A8.1/A8.2 PDF engine EN/BN (ADR-0009) · H1.4 metadata in `<head>`, Lighthouse mobile 92-95 · D2.1 price-list design (ADR-0008) ·
F3a.1/.2a/.3 email + OTP SMS limits · company page `?tab=`. Earlier: C6 ✅, C7 ✅ (C7.6 deferred), H1 ✅, A6 ✅.
Branch `claude/c6-search-and-module-plan` (pushed; PR not opened yet — open it on GitHub).

TOTP secrets + recovery-code hashes encrypted at rest (A1 HANDOFF; deploy: `migrate` converts old rows).
**Watch:** `InventoryConcurrencyTests.FiftyParallelBuyers_TenUnits_ExactlyTenSucceed_PostgresRowLocksOnly` failed once in a full
integration run on 2026-11-13 (message not captured), then passed 3× alone and in a full 552/552 run — if it fails again, capture
the message (oversell = bug; `busy > 0` = lock timeout under load).
**Watch:** intermittent React hydration error #418 on /search after a language switch (seen twice in E2E, not reproduced in 40+ loads); bn home Lighthouse varies 74-95 on this PC (re-measure on a quiet CPU after the preview API is updated).
**Next:** E3.3-E3.7 checkout pages · E8.3 legal deadline · E8.4 console zones editor · D5.2 line-validate API (price part waits for D2) · small unit: admin sellers list endpoint ·
then MODULES.md order (D2.2+ waits for owner decisions MODULES.md §5 #7; C3 waits for S3-vs-volume).

**How work runs:** main session Opus plans and verifies; routine code → Sonnet sub-agent; money/security → Opus sub-agent
(memory `feedback-subagent-delegation`). Tools: `python scripts/test_module.py <ID>`, `python scripts/check_cards.py`,
`python scripts/api_snapshot.py [--out <dir>]` after API changes (`--out` when a running API locks bin/Release).

**Before starting:** Docker Desktop running → `docker compose -f infra/docker/docker-compose.yml up -d postgres redis meilisearch mailpit`.
Seeded catalogue lives in DB `mechware` (the default for API and Worker; one setting `ConnectionStrings__DefaultConnection`).
Deploy: `docker run --env-file … mechware-worker migrate` before starting a new api/worker (docs/modules/H3.md).

**Owner actions (start now):** VAT consultant · lawyer · SSLCommerz/bKash merchant · SMS sender ID + email domain · pen-test booking ·
hosting choice (staging) · 5 real buyers + real product data. Open decisions: [MODULES.md](MODULES.md) §5.
