# NOW (keep ≤ 30 lines; update at the end of every unit)

**Last units:** C6.1 search adapter ✅ · C6.2 indexer worker + rebuild ✅ (2026-11-12). Card: [modules/C6.md](modules/C6.md).
Branch `claude/c6-search-and-module-plan` (pushed; PR not opened yet — open it on GitHub).

**Next unit:** C6.3 search API — `GET /api/v1/search`, `/api/v1/search/autocomplete` (anonymous, `StorefrontRead`, public-list entry; part-number input → `Fuzzy(Normalise(q))` key lookup first). Model: **Sonnet**, fresh session. Then the module code review (Opus, high) because a public endpoint is added.
Small side unit any time: C6.2b one worker registration.

**After that:** C6.4 facets/synonyms · C6.5 k6 · A6.1–A6.5 tooling.

**Before starting:** Docker Desktop running → `docker compose -f infra/docker/docker-compose.yml up -d postgres redis meilisearch`.

**Owner actions (start now):** VAT consultant · lawyer · SSLCommerz/bKash merchant · SMS sender ID + email domain · pen-test booking · 5 real buyers + real product data. Open decisions: [MODULES.md](MODULES.md) §5.
