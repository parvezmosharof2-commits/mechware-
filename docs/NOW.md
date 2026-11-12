# NOW (keep ≤ 30 lines; update at the end of every unit)

**Done (2026-11-12):** C6 search module ✅ (C6.1–C6.6; C6.2b moved to E5) · A6 tooling ✅ · C7.1 api-client · C7.2 search page + filters · C7.3 results table/list · C7.4 product page core ✅. Cards: [C6](modules/C6.md), [A6](modules/A6.md), [C7](modules/C7.md).
Branch `claude/c6-search-and-module-plan` (pushed; PR not opened yet — open it on GitHub).

**Next:** C7.5 PDP specs/documents/stock/lifecycle (needs: manufacturer part number + quantity breaks in `ProductDetailDto` — small backend unit first), then C7.7 SEO/JSON-LD, C7.8 Lighthouse/a11y pass, H1 home/legal/sitemap.

**How work runs:** main session Opus plans and verifies; routine code → Sonnet sub-agent; money/security → Opus sub-agent (memory `feedback-subagent-delegation`). Tools: `python scripts/test_module.py <ID>`, `python scripts/check_cards.py`, `python scripts/api_snapshot.py` after API changes.

**Before starting:** Docker Desktop running → `docker compose -f infra/docker/docker-compose.yml up -d postgres redis meilisearch`. Seeded catalogue lives in DB `mechware` (Worker default); the Api's default is `mechware_db` → set `ConnectionStrings__DefaultConnection` for manual runs.

**Owner actions (start now):** VAT consultant · lawyer · SSLCommerz/bKash merchant · SMS sender ID + email domain · pen-test booking · 5 real buyers + real product data. Open decisions: [MODULES.md](MODULES.md) §5.
