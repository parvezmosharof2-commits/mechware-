# NOW (keep ≤ 30 lines; update at the end of every unit)

**State 2026-11-16:** work resumed after the owner's go-ahead; everything below is committed and pushed. Auto-resume cron runs every 20 min inside the session (dies with the session). Start Docker Desktop first. If the 5-hour usage limit message appears, wait for the reset (Opus-only units like D2.8 wait for it; Sonnet units can run).
**Done since the stop:** D2.5+D2.6 (resolver in order pricing, company VAT fix, price snapshot, PRICE_CHANGED guard) · D2.7 (staff API for price lists, part 1) · D2.11 (console price-list editor) · D5.2 (`POST /api/v1/quick-order/validate`, 4 SQL queries for 500 lines).

**Done:** C6 search ✅ · C7 storefront ✅ · H1 home/legal/help ✅ · A6 tooling ✅ · A8 PDF engine EN/BN ✅ · E1 cart ✅ (Redis CAS store, signed guest token, endpoints, atomic merge on login; needs ONE Redis node) ·
E3.1–E3.4 cart page + checkout delivery/review (placeholder confirmation) · E3b backend follow-ups (quote totals, `GET /orders`, cart cleared on order, `GET /admin/sellers`) ·
E8.1/E8.2 shipping zones + calculator + priced inside orders (no zones ⇒ Production refuses orders until the owner enters tariffs; `POST /orders/shipping-quote`; PO number) ·
C10.1–C10.3b bulk catalogue import (dry run, commit, console screen + seller picker; images C10.4 wait for C3) · D5.1 quick-order parser ·
D2.1–D2.4 price lists: ADR-0008 + decisions (lowest valid price, VAT-inclusive catalogue price for everyone, 10 % global margin floor, no group lists), pure resolver, PriceList aggregate + four-eyes, tables + DB triggers + permissions ·
H3a Dockerfiles ×4 + staging compose + H3a.2 deploy readiness (`Mechware.Worker migrate`) · TOTP secrets encrypted at rest · F3a.1/.2a/.3 email + OTP limits · D2/E1/E8 threat-model sections.
Branch `claude/c6-search-and-module-plan` (pushed; PR not opened yet — open it on GitHub). Tests at last full run: 1137 unit + 688 integration, Skipped 0.

**Next (in order):** **D2.8** approve/reject/withdraw with step-up (Opus; HANDOFF in docs/modules/D2.md has the exact domain calls and the `LockedAsync` helper) · D2.9 margin-floor staff API (Opus) · D2.10 buyer company-price API · D2.12 console approval queue + floors · D2.13 storefront price display ·
D5.3 web quick-order page · E3.5 payment choice (needs gateway) · E3.6/E3.7 confirmation + guest checkout · E8.3 legal deadline / E8.4 console zones editor (needs a zones admin API) · small: admin SKU→variant lookup endpoint (the price-list editor needs it), `GET /admin/companies?q=` · E4/E5 gateways · E6 invoicing · F1/F2 · H2 hardening.
**Watch:** concurrency tests (`InventoryConcurrencyTests…`) failed twice only while other servers loaded the machine (pass alone); intermittent React hydration error #418 on /search after a language switch (not reproduced); /cart Lighthouse mobile ~81 (LCP 3.3 s, client-fetched content; fix in H4); bn home Lighthouse varies 74-95 on this PC.

**How work runs:** main session plans + verifies; routine code → Sonnet sub-agent; money/security → Opus (high/xhigh); design → Fable. Verify every agent report myself (unit + integration with Skipped 0, `dotnet format` EXIT CODE, `check_cards.py`, typecheck/lint) before commit; never push to main.
Tools: `python scripts/test_module.py <ID>`, `python scripts/check_cards.py`, `python scripts/api_snapshot.py --out <dir>` after API changes. Build/test with `-p:BaseOutputPath=<scratch>/bo/` (forward slashes, trailing slash).
**Before starting:** Docker Desktop → `docker compose -f infra/docker/docker-compose.yml up -d postgres redis meilisearch mailpit`. Seeded catalogue is in DB `mechware`. Deploy: `mechware-worker migrate` before a new api/worker (docs/modules/H3.md). Preview used 3001 (web, git worktree in the session scratchpad) and 5200 (API from a scratch build); both die with the laptop — rebuild the API from HEAD and run `Mechware.Worker migrate` on `mechware` first.

**Owner actions (start now):** shipping tariffs per zone + heavy/oversize limits + free-shipping threshold + VAT-on-shipping · VAT consultant · lawyer (docs/LEGAL_REVIEW.md) · SSLCommerz/bKash merchant · SMS sender ID + email domain · hosting choice (staging) · pen-test booking · 5 real buyers + real product data · S3 vs volume. Open decisions: [MODULES.md](MODULES.md) §5.
