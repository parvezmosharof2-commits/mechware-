# NOW (keep ≤ 30 lines; update at the end of every unit)

**Done (2026-11-13):** F3a.1 email + F3a.3 order/approval emails + F3a.2a OTP SMS limits ✅ · D2.1 price-list design ✅ ([ADR-0008](adr/0008-price-lists-and-contract-prices.md), threat model §৮ PL1–PL21; D2.2+ waits for owner decisions MODULES.md §5 #7) · company page opens `?tab=approvals` · H1 ✅ (H1.1 home/header/footer, H1.2 help + 7 legal draft pages EN/BN, H1.3 pages sitemap; legal texts await the lawyer, see docs/LEGAL_REVIEW.md; storefront E2E 154/154, Lighthouse mobile 90-94). **Earlier (2026-11-12):** C6 search module ✅ (C6.1–C6.6; C6.2b moved to E5) · A6 tooling ✅ · C7.1 api-client · C7.2 search page + filters · C7.3 results table/list · C7.4 product page core ✅ · C7.5, C7.7 ✅ · C7.8 + C7.8b ✅ quality gate (axe 0, E2E 58/58; Lighthouse mobile Perf 91-93 on search + PDP en/bn, see docs/perf/C7-storefront-quality.md); C7 module done except deferred C7.6. Cards: [C6](modules/C6.md), [A6](modules/A6.md), [C7](modules/C7.md).
Branch `claude/c6-search-and-module-plan` (pushed; PR not opened yet — open it on GitHub).

**Next:** a perf unit for Lighthouse >= 90 on /search and the media CORP header (backend); owner: lawyer texts for the legal pages, then flip `LEGAL_APPROVED` (docs/LEGAL_REVIEW.md).

**How work runs:** main session Opus plans and verifies; routine code → Sonnet sub-agent; money/security → Opus sub-agent (memory `feedback-subagent-delegation`). Tools: `python scripts/test_module.py <ID>`, `python scripts/check_cards.py`, `python scripts/api_snapshot.py` after API changes.

**Before starting:** Docker Desktop running → `docker compose -f infra/docker/docker-compose.yml up -d postgres redis meilisearch`. Seeded catalogue lives in DB `mechware` (Worker default); the Api's default is `mechware_db` → set `ConnectionStrings__DefaultConnection` for manual runs.

**Owner actions (start now):** VAT consultant · lawyer · SSLCommerz/bKash merchant · SMS sender ID + email domain · pen-test booking · 5 real buyers + real product data. Open decisions: [MODULES.md](MODULES.md) §5.
