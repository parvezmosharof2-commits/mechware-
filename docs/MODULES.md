# MECHWARE — Module index

> Revised 2026-11-12 after the Fable 5.2 plan review. One card per module in [`modules/`](modules/). Agents read **only**
> `CLAUDE.md` → [`NOW.md`](NOW.md) → the current card → the card's context pack. Never this whole folder.
> Source of truth order: brief v2 (`01_PROJECT_BRIEF.md`) > cards > `MASTER_PLAN.md` > other docs. Code and Swagger show what exists.
> Status: ✅ done · 🟡 partial · ⬜ not started · ⏭ after soft launch (MVP proposal, owner to confirm).

## 1. Work unit rule
One unit = one PR = one fresh session, 0.5–1.5 days (hard cap 2). At most ~400 production lines, 12 files, 1 migration,
8 mutants. Backend **or** frontend, not both. One test filter + one thing the owner can see. Bigger → split before starting.
Flow per unit: card → (new module: threat model/ADR) → failing tests → code → mutation check → gates green → HANDOFF block in card → owner demo.

## 2. Order (finish one unit, then the next)
```
C6 Search ─▶ A6 tooling (card check, test-module, layering ratchet) ─▶ C7 PLP/PDP + H1 home/legal/sitemap
─▶ H3a minimal staging ─▶ F3a notifications transport ─▶ C3 S3 storage ─▶ A8 PDF engine (Bangla) ─▶ C10 bulk import
─▶ D2 price lists (+ console screens) ─▶ D5 line validator ─▶ D4 RFQ & quotes (+ console) ─▶ D5 quick-order UI
─▶ E8 shipping rates + legal deadline ─▶ E1 cart ─▶ E3 checkout ─▶ E5 gateways ─▶ B3 credit settlement ─▶ E6 invoicing
─▶ F1 courier ─▶ F2 RMA + E7 restock ─▶ F3b notification matrix ─▶ F5 dashboards ─▶ G5 audit/users/exports
─▶ H6 consent + analytics ─▶ H2 hardening ─▶ H3b production ─▶ H4 performance ─▶ soft launch
```
Console screens are **inside** each feature module (no separate "console phase"). G1 is the shell only.

## 3. Modules
| ID | Module | Status | Card |
|---|---|---|---|
| A1 | Identity & auth | ✅ (addresses page 🟡) | [A1](modules/A1.md) |
| A2 | Audit & security | ✅ | [A2](modules/A2.md) |
| A3 | Outbox & workers | ✅ | [A3](modules/A3.md) |
| A4/A5 | Design system / API client | ✅ | [A4-A5](modules/A4-A5.md) |
| A6 | CI, gates, agent tooling | ✅ | [A6](modules/A6.md) |
| A7 | Staff passkeys | ⏭ | [A7](modules/A7.md) |
| A8 | PDF engine (EN/BN) | ✅ (A8.1, A8.2) | [A8](modules/A8.md) |
| B1 | Companies | ✅ | [B1](modules/B1.md) |
| B2 | Approvals | ✅ | [B2](modules/B2.md) |
| B3 | Credit settlement (receivables) | ⬜ **new** | [B3](modules/B3.md) |
| B4 | KYC upload | ⏭ | [B4](modules/B4.md) |
| C1/C2/C5 | Catalog, PIM, part numbers | ✅ | [C1](modules/C1.md) |
| C3 | Document storage (S3) | 🟡 | [C3](modules/C3.md) |
| C4 | PIM console screens | ⬜ | [C4](modules/C4.md) |
| C6 | Search | ✅ | [C6](modules/C6.md) |
| C7 | PLP / PDP / compare (web) | ✅ (C7.6 compare deferred) | [C7](modules/C7.md) |
| C8 | Inventory | ✅ | [C8](modules/C8.md) |
| C9 | Reviews & Q&A | ⏭ | [C9](modules/C9.md) |
| C10 | Bulk catalogue import | 🟡 (C10.1-C10.3 ✅; C10.4 images waits for C3) | [C10](modules/C10.md) |
| D1 | Pricing engine | ✅ | [D1](modules/D1.md) |
| D2 | Price lists & contract prices | 🟡 (D2.1 design ✅) | [D2](modules/D2.md) |
| D3 | Promotions admin | ⏭ (engine ✅) | [D3](modules/D3.md) |
| D4 | RFQ & quotes | ⬜ | [D4](modules/D4.md) |
| D5 | Quick order & line validator | 🟡 (D5.1 parser ✅) | [D5](modules/D5.md) |
| E1 | Cart | ✅ (web cart UI lands with E3) | [E1](modules/E1.md) |
| E2 | Orders | ✅ | [E2](modules/E2.md) |
| E3 | Checkout (web) | 🟡 (E3.1 client, E3.2 cart page ✅) | [E3](modules/E3.md) |
| E4 | Payments core | ✅ | [E4](modules/E4.md) |
| E5 | Gateway adapters | ⬜ | [E5](modules/E5.md) |
| E6 | Invoicing (Mushak-6.3/6.7/9.1) | ⬜ | [E6](modules/E6.md) |
| E7 | Restock & confirmed-order cancel | ⬜ **new** | [E7](modules/E7.md) |
| E8 | Shipping rates & legal deadline | 🟡 (E8.1, E8.2 ✅; tariffs are owner data) | [E8](modules/E8.md) |
| F1 | Courier & shipments | ⬜ | [F1](modules/F1.md) |
| F2 | RMA / returns | ⬜ | [F2](modules/F2.md) |
| F3 | Notifications (F3a transport, F3b matrix) | 🟡 (F3a.1, F3a.2a, F3a.3 ✅; F3a.2b SMS adapter waits for provider) | [F3](modules/F3.md) |
| F4 | Service & projects | ⏭ (one inquiry form only) | [F4](modules/F4.md) |
| F5 | Buyer dashboards | ⬜ | [F5](modules/F5.md) |
| G1/G5 | Console shell / audit, users, exports, settings | ✅ / ⬜ | [G](modules/G.md) |
| H1 | Home, help, legal pages, sitemap | ✅ (legal texts await lawyer) | [H1](modules/H1.md) |
| H2 | Hardening | ⬜ | [H2](modules/H2.md) |
| H3 | Deploy (H3a staging, H3b production) | 🟡 (H3a part 1 + H3a.2 ✅; staging host waits for hosting choice) | [H3](modules/H3.md) |
| H4 | Performance | ⬜ | [H4](modules/H4.md) |
| H6 | Consent + analytics | ⬜ **new** | [H6](modules/H6.md) |
| H5 | Seller marketplace, payouts, AI | ⏭ | — |

## 4. Start these now (owner) — calendar time is set by these, not by code
| Item | Blocks | Note |
|---|---|---|
| VAT consultant (Mushak-6.3, VDS, numbering, partial delivery) | E6, launch criterion 4 | Answers needed **before** E6 design |
| Lawyer (Guideline 2021 refund deadline, PDPO, legal pages) | H1, F2, E8, criterion 12 | |
| SSLCommerz / bKash merchant, SMS sender ID, email domain | E5, F3a | Approval can take weeks; often wants a live site + legal pages |
| Pen test booking | criterion 6 | Book when E-sector starts; 3–4 calendar weeks incl. retest |
| 5 real buyers + real product data and photos | criterion 1, C10/C1 exit gate | Longest lead time |
| Hosting choice, courier accounts | H3a, F1 | PDPO cross-border review |

## 5. Owner decisions still open
1. MVP cut (cards marked ⏭; saves ~45 days). Needs a dated amendment to brief §3.1 before agents skip them.
2. AI at launch? Brief §3.1 item 16 says yes; MASTER_PLAN/ARCH say after launch.
3. Load target: 2,000 users / 100 orders/min (brief §11) or 5,000 / 200 (MASTER_PLAN §1 #10)?
4. Read-deny rules for the never-open list in `.claude/settings.json` (permission change; owner must approve).
5. Quote validity (15 vs 30+ days), approval stock hold (24 vs 48–72 h), S3 vs volume, login rate limit behind NAT.
6. Staff role names: brief §9 vs code (`admin`, `finance`…) — settle in G5.
7. D2 price lists (details in [ADR-0008](adr/0008-price-lists-and-contract-prices.md) and the D2 card's `## Open (owner)`):
   (a) resolution rule — lowest valid price wins (recommended, brief §8.1) or contract always applies;
   (b) company VAT basis — today a company pays catalogue price + VAT (1,000 → 1,050) while a guest pays 1,000 VAT-inclusive;
   recommended: catalogue price is the VAT-inclusive public price for everyone (needed before D2.5);
   (c) margin floor values (global, per brand/category) and whether below-cost can ever be approved (before D2.8);
   (d) group lists at launch (recommended: defer, D2.14 ⏭).

## 6. Which model per unit type (owner-approved delegation, memory `feedback-subagent-delegation`)
| Unit type | Model |
|---|---|
| Design docs / threat models for D2, D4, E5, E6; one full audit before the pen test | Fable (limit hit → Opus high) |
| Money/security backend units (D2, D4 pricing/accept, E1 guest token, E5, E6, B3, F2 refunds, F3 OTP limits, H3 secrets/backup) | Opus |
| UI, CRUD, read APIs, search | Sonnet |
| Running tests/mutations, card checks, doc updates | main session, no sub-agent (sub-agents start cold and re-read files) |
Pick the model per unit at session start; do not switch mid-session (prompt cache is per model).
Reviews: per PR for money/security units; one batch per module for UI.

## 7. Estimate (update weekly from actuals)
Remaining: MVP ≈ 128 units → **120–160 focused days**; full brief **165–205 days** (Fable review 2026-11-12; earlier
106–139 was optimistic: UI volume, missing modules, H sector, per-unit overhead). Calendar time is driven by §4.
