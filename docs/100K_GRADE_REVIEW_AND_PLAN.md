# MECHWARE — Is the plan "USD 100K grade"? Review and corrected plan

> Scope: review of `docs/01_PROJECT_BRIEF.md`, `02_DESIGN_SYSTEM.md`, `03_PHASE_PROMPTS.md`,
> `04_REVIEW_PROMPTS.md`, `ARCHITECTURE.md`, `DATA_MODEL.md`, `API_CONTRACT.md`,
> `COMPANY_PROFILE.md`, `MASTER_EXECUTION_GUIDE.md`, the ADRs and the code built so far (Phases 0–4).
> Backed by external research (sources at the end). Date: 2026-10-30.

---

## সংক্ষেপে (Bangla summary)

**রায়:** প্ল্যানের ইঞ্জিনিয়ারিং ভিত্তি ভালো (Clean Architecture, Money value object, row-lock
inventory, outbox, audit log, ADR)। কিন্তু **এটা এখনো ১০০K USD মানের প্ল্যান না**, কারণ:

1. **ভুল প্রোডাক্ট টার্গেট** — brief টা একটা সাধারণ B2C ফ্যাশন-মার্কেটপ্লেস টেমপ্লেট থেকে নেওয়া
   (ShopVerse, "black running shoes", Rahim/Nadia persona)। MECHWARE-এর আসল ক্রেতা কারখানা ও
   কর্পোরেট (Square, Singer, RFL…)। অথচ brief-এ **"B2B pricing — do NOT build"** লেখা। এটাই সবচেয়ে বড় ভুল।
2. **২১ দিনে AI দিয়ে পুরো সিস্টেম + AI দিয়েই অডিট** — বাস্তবসম্মত না। মানুষের pen-test, ডিজাইনার, QA নেই।
3. **নিরাপত্তা "ফিচার" আছে, নিরাপত্তা "প্রসেস" নেই** — threat model, SAST/DAST, SBOM, patch SLA,
   incident response, backup restore drill, PDPO 2025 অনুযায়ী consent/data rights নেই।
4. **বাংলাদেশের আইন** — Digital Commerce Guideline 2021 (ডেলিভারি সময়, অগ্রিম/এসক্রো),
   Mushak-6.3 VAT invoice, ব্যক্তিগত তথ্য সুরক্ষা অধ্যাদেশ ২০২৫ — প্ল্যানে নেই বা অসম্পূর্ণ।
5. **লুক** — "Cybernetic neon dark" থিম গেমিং/কনজিউমার লুক। ইন্ডাস্ট্রিয়াল ক্রেতা (ইঞ্জিনিয়ার, পারচেজ
   অফিসার) McMaster-Carr / DigiKey / Mouser-এর মতো **হালকা, ঘন, স্পেক-টেবিল** ইন্টারফেস চায়।

**সমাধান:** নিচের সংশোধিত প্ল্যান — B2B কোর (কোম্পানি অ্যাকাউন্ট, কোটেশন/RFQ, কন্ট্রাক্ট প্রাইস,
অ্যাপ্রুভাল, কুইক অর্ডার, Mushak-6.3), ASVS 5.0 L2 নিরাপত্তা প্রোগ্রাম, light-first ইন্ডাস্ট্রিয়াল
ডিজাইন, এবং ১৬–২০ সপ্তাহের মানুষ+AI রোডম্যাপ।

---

## 1. Scorecard

| Area | Score now | Why | Target |
|---|---|---|---|
| Backend engineering foundations | **8/10** | Clean Architecture, minor-unit Money + banker's rounding, `FOR UPDATE` in deterministic order, outbox with `SKIP LOCKED`, xmin concurrency, sanitizer, ADRs, real Postgres integration tests | 9 — enforce module boundaries, inbox idempotency, event versioning |
| Product fit (does it sell what MECHWARE sells?) | **4/10** | Generic B2C marketplace brief; B2B features explicitly deferred; no engineering-project / spare-parts / service flows | 9 — B2B procurement core |
| Security | **5/10** | Good controls in code (JWT + refresh rotation, TOTP, audit, rate limits, HMAC webhooks planned) but no threat model, no supply-chain or patch process, no human pen test, admin not isolated | 9 — ASVS 5.0 L2 (L3 for payments/admin) with evidence |
| Compliance (Bangladesh) | **3/10** | Guideline 2021, Mushak-6.3, PDPO 2025 not designed in | 8 |
| Look & UX | **5/10** | Token system and components exist, but neon dark default, illustration images, card-only PLP; no Figma stage, no visual regression | 9 — industrial, dense, trustworthy, light-first |
| Delivery plan | **3/10** | 21 days, AI-only builder + AI-only auditor (Gemini 1.5 Pro is retired) | 8 — 16–20 weeks, small human team accelerated by AI |
| Operations | **4/10** | K8s topology drawn but no SLOs, no DR drill, no runbooks | 8 |

**Overall: about 5/10 against a real USD 100K agency deliverable.** The code quality is ahead of the
plan; the plan is what needs fixing.

---

## 2. Planning mistakes (ranked)

### P0-1 — The brief describes the wrong business
- `01_PROJECT_BRIEF.md` is a lightly renamed B2C template: project name "ShopVerse", the AI example
  "black running shoes", consumer personas, consumer KPIs.
- `COMPANY_PROFILE.md` says MECHWARE sells automation (Siemens, Mitsubishi, Delta, Omron, Yaskawa),
  builds machines and runs retrofit/ETP projects for 30+ industrial clients. Those clients buy with
  **purchase orders, quotations, approvals, credit terms and VAT invoices**.
- §3.2 lists **"B2B pricing" under "do NOT build"**. For this company it is the core.
- Missing entirely: RFQ/quotation, company accounts with multiple buyers, approval chains,
  customer-specific prices, quantity breaks, quick order by SKU / CSV / BOM upload, reorder lists,
  datasheets and CAD as first-class assets, cross-references/alternatives, obsolescence (e.g. a
  discontinued PLC with its successor), installed-base spare parts, service/AMC requests.

### P0-2 — Unrealistic delivery model
- 21 days, one AI builder, one AI auditor. AI reviewing AI shares the same blind spots; there is no
  independent human security test, no designer, no QA, no product owner sign-off on flows.
- The guide pins the auditor to Gemini 1.5 Pro, which is no longer current. Any model choice should
  be "latest available", and none replaces a human pen test.
- A credible USD 100K build is ~16–20 weeks with a small team (see §7).

### P0-3 — Security is a list of features, not a programme
Absent: threat model, abuse cases, SAST/SCA/secret scanning in CI, SBOM, dependency patch SLA,
container scanning, branch protection, admin isolation, tamper-evident audit, backup/restore drills,
incident response, external pen test. (Details in §4.)

### P0-4 — Bangladesh legal and tax requirements not designed in
- **Digital Commerce Operation Guidelines 2021**: delivery within 5 days (same city) / 10 days
  (other city); hand to courier within 48 h of advance payment; advance payment limits unless funds
  go through a Bangladesh Bank–approved escrow; timely refunds. The order state machine, SLA timers
  and payment design must encode this. (Refund deadlines are reported differently by different
  sources — confirm with counsel.)
- **Mushak-6.3**: every VAT-registered seller must issue a Mushak-6.3 tax invoice per taxable
  supply; it may be issued electronically from ERP/VAT software. Monthly Mushak-9.1 returns follow.
  The invoice module must produce compliant numbering, BIN, VAT breakdown, and immutable copies.
- **Personal Data Protection Ordinance 2025**: explicit, revocable consent, purpose limitation,
  data-subject rights, conditions on cross-border transfer, special care for sensitive data (NID,
  bank). Hosting region and processors must be chosen with this in mind.

### P1-1 — Scope order is upside down
Seller center, double-entry payout ledger, AI chat assistant, three card/wallet providers and flash
sales are scheduled **before** the B2B buying journey exists. For a launch, MECHWARE's own catalogue
(direct seller) plus a few authorised distributors is enough; multi-vendor payouts can follow.

### P1-2 — Over-built infrastructure, under-built operations
K8s topology is drawn, but there are no SLOs, error budgets, RPO/RTO, restore drills or runbooks.
Start on managed containers + managed Postgres with point-in-time recovery; adopt K8s when traffic
or team size demands it.

### P1-3 — Stack drift and patch hygiene
- Brief says Tailwind 4; code uses Tailwind 3.4. Pick one (recommend Tailwind 4 during the design
  refresh).
- Next.js: lockfile resolves **15.5.28 / React 19.3.1**, which is patched for CVE-2025-55182
  (React Server Components RCE, in CISA KEV) and CVE-2025-29927 (middleware auth bypass). But
  `package.json` floors are `^15.1.7` / `^19.0.0` — raise them so a fresh lock can never resolve a
  vulnerable version. Plan a move to Next 16 (`middleware.ts` → `proxy.ts`).
- `apps/web/src/middleware.ts` gates `/account`, `/checkout`, `/orders`. Keep it as UX only;
  CVE-2025-29927 shows middleware must never be the authorisation boundary. Every API call is
  authorised server-side (already true for the .NET API — keep it a rule).

### P1-4 — Design direction fights the audience
Neon cyan on midnight navy as the **default** storefront theme reads as gaming/crypto. Procurement
staff work long sessions in offices, compare specs, print quotes. Market leaders in this category
(McMaster-Carr, DigiKey, Mouser, Grainger) use light, dense, table-first layouts. Baymard's B2B
components & machinery research recommends **product tables** (not cards) for spec-heavy desktop
listings.

---

## 3. Architecture plan

### 3.1 Shape: enforced modular monolith
ADR-0001 already chooses a modular monolith; make it enforceable:
- One folder + one schema per module; no cross-module table access; modules talk through
  application interfaces or integration events.
- Architecture tests (NetArchTest or ArchUnitNET) fail CI on forbidden references.
- Integration events carry `type`, `version`, `occurredAt`, `correlationId`; consumers are
  idempotent through an **inbox** table (the outbox exists; add the inbox side). Same table dedupes
  payment webhooks.

### 3.2 Modules (new ones in bold)

| Module | Responsibility |
|---|---|
| Identity | users, sessions, MFA, passkeys |
| **Accounts (B2B)** | company, branches, buyer roles (requester / approver / admin), spend limits, tax IDs (BIN/TIN), credit terms |
| Catalog / **PIM** | products, variants, **class-based technical attributes with units (ETIM/ECLASS-style)**, datasheets, CAD, certificates, **alternatives & successors**, lifecycle (active / NRND / obsolete) |
| Inventory | stock per warehouse, reservations (built) |
| **Pricing** | list price → quantity breaks → customer-group price list → contract price → quote price; VAT inclusive/exclusive; validity dates; 4-eyes approval for price-list changes |
| Search | Meilisearch, **parametric facets from PIM attributes**, part-number normalisation (`6ES7 214-1AG40-0XB0` = `6ES72141AG400XB0`), Banglish |
| **Quotes / RFQ** | RFQ from cart or free text/BOM, sales-rep pricing, versioned quotes, PDF, expiry, convert to order |
| Cart & **Quick order** | SKU paste, CSV/BOM upload with per-line validation (SKU, MOQ, pack size, stock, price, approval route), requisition lists, reorder |
| **Approvals** | rule engine (amount, category, cost centre) → approver chain; audit of every decision |
| Checkout & Payments | SSLCommerz, bKash, bank transfer/PO, credit terms, COD with OTP; hosted payment pages only |
| Orders & Fulfilment | state machine with Guideline-2021 SLA timers, partial shipments, courier adapters, RMA |
| **Invoicing & Tax** | Mushak-6.3 numbering and PDF, credit notes, immutable storage, Mushak-9.1 export |
| **Service & Projects** | engineering project inquiries, retrofit/AMC requests, **"My machines" installed base → compatible spare parts** (this is MECHWARE's moat) |
| Sellers & Ledger | defer to after launch; keep the double-entry design |
| CMS, Reviews, Notifications, Audit | as planned |
| **Integration** | ERP/accounting sync (orders, invoices, stock) via outbox; PunchOut (cXML/OCI) later for large corporate buyers |

### 3.3 Data and tenancy rules
- Every B2B row carries `CompanyId`; EF Core global query filters enforce company scope; a test
  matrix calls every endpoint as company A against company B's IDs (IDOR suite).
- Prices and money stay in minor units (done). Quotes and invoices are **immutable snapshots**
  (never recomputed from current prices).
- Sensitive columns (NID, bank account) encrypted at the application layer; keys in a managed KMS.

### 3.4 Deployment (right-sized)
- Cloudflare in front (DNS, WAF, bot management, rate limits, image resizing/CDN).
- Managed containers for API, worker, web, console in a region close to Bangladesh (e.g. Singapore
  or Mumbai), subject to the PDPO data-transfer review.
- Managed PostgreSQL 16 with PITR, Redis, object storage with signed URLs.
- Targets: **RPO ≤ 15 min, RTO ≤ 4 h**, restore drill every quarter, documented.
- Environments: dev → staging (prod-like, anonymised data) → prod; infrastructure as code.

### 3.5 Observability and SLOs
- OpenTelemetry traces/metrics/logs end to end (browser → Next → API → DB → worker).
- SLOs: storefront availability 99.9 %; search p95 < 150 ms; add-to-cart p95 < 300 ms;
  checkout submit p95 < 800 ms; outbox lag p99 < 30 s. Alerts on burn rate, not raw errors.
- Business alerts: payment validation failures, OTP conversion drop (SMS pumping), oversell attempts.

---

## 4. Security plan (OWASP ASVS 5.0)

Target **ASVS 5.0 Level 2** for the whole platform and **Level 3 controls for payments, pricing
and admin**. Keep a requirement → test → evidence matrix in `docs/security/ASVS_MATRIX.md`.

### 4.1 Process
1. **Threat model** (STRIDE + abuse cases) per module before it is built. Must-cover abuse cases:
   client price tampering, quote/approval manipulation, cross-company IDOR, credit-limit abuse,
   price-list scraping by competitors, RFQ spam, SMS pumping, buyer-admin account takeover,
   refund fraud, webhook replay.
2. **CI security gates**: CodeQL (C#, TS), Semgrep, gitleaks/secret scanning, Dependabot or
   Renovate, `dotnet list package --vulnerable`, `pnpm audit`, Trivy on images, CycloneDX SBOM per
   release, GitHub Actions pinned by commit SHA.
3. **Repo controls**: protected `main`, required reviews and status checks, no direct pushes,
   signed commits for releases.
4. **Patch SLA**: critical/KEV within 48 h, high within 7 days; subscribe to Next.js, React, .NET,
   Npgsql advisories.
5. **Human pen test** (grey-box) before launch, retest after fixes; bug bounty/VDP after launch.
6. **Incident response** runbook (roles, contacts, evidence preservation, customer/regulator
   notice), tabletop exercise before launch.

### 4.2 Identity and sessions
- BFF pattern: browser holds only `__Host-` httpOnly, Secure, SameSite cookies; access tokens never
  reach JavaScript. Refresh-token family with reuse detection (ADR-0006) stays.
- Passkeys (WebAuthn) for staff (mandatory) and buyers (optional); TOTP fallback.
- Step-up authentication for: price-list edits, refunds, payout changes, role changes, exports.
- OTP: CAPTCHA/Turnstile before send, per-IP and per-number limits, exponential backoff, BD-only
  number allowlist, conversion-rate alerting.

### 4.3 Authorisation
- Policy checks in the API for every endpoint (never only in Next middleware).
- Company and seller scope via global query filters + the IDOR test suite in CI.
- Approval and pricing decisions recorded with actor, before/after, reason.

### 4.4 Payments
- Hosted/redirect payment pages only, so card data never touches MECHWARE servers (keeps PCI scope
  minimal; confirm SAQ type with the acquirer).
- **Never mark an order paid from the browser redirect.** SSLCommerz: verify the IPN, call the
  Order Validation API server-side with `val_id`, match `tran_id`, amount and currency against the
  stored order, accept only valid statuses, process idempotently through the inbox. bKash: server
  side query/execute with the same checks.
- Webhooks: HMAC signature, timestamp window, replay protection, store raw payload.
- Escrow/advance-payment rules from the 2021 Guidelines enforced in the payment policy.

### 4.5 Web and edge
- CSP with nonces (no `unsafe-inline` scripts), HSTS preload, `frame-ancestors 'none'`,
  Referrer-Policy, Permissions-Policy; SRI for any third-party script; script inventory on payment
  pages.
- Cloudflare WAF managed rules, bot management on search/price endpoints, rate limits (existing
  ASP.NET limiter stays as a second layer).

### 4.6 Files
- Datasheets, CAD, KYC docs: size/type allow-list, magic-byte check, antivirus scan (ClamAV) in
  the worker before publish, served from a separate cookieless domain via signed URLs, PDFs
  re-rendered or sandboxed.

### 4.7 Admin
- Console on its own subdomain behind Zero-Trust access (e.g. Cloudflare Access) + passkeys.
- 4-eyes on price lists, refunds above threshold, seller payouts.
- **Tamper-evident audit log**: each entry stores a hash of the previous one; daily anchor exported
  to write-once storage.

### 4.8 Privacy (PDPO 2025)
- Consent records (purpose, version, timestamp, withdrawal), privacy notice in Bangla and English.
- Data-subject requests: export and delete/anonymise flows in the account area and console.
- Retention schedule (orders/invoices kept for tax law; marketing data deleted on withdrawal).
- Data inventory and processor list (SMS, email, payment, hosting) with transfer basis.

---

## 5. Look and UX plan ("top class" for an industrial buyer)

### 5.1 Direction
- **Light theme default for the storefront**, dark as an option; the console can default to dark.
- Keep the brand: Royal Industrial Navy `#0F3E7D` as primary, Mech Teal `#00A8B5` as accent,
  Electric Cyan only for focus rings, highlights and the hero. Re-check every pair for WCAG 2.2 AA.
- Typography: Inter or IBM Plex Sans for Latin, **Noto Sans Bengali / Hind Siliguri** for Bangla,
  **tabular numerals** for prices, quantities and specs.
- Photography: real product photos on a consistent white background (manufacturer assets with
  distributor permission) and real project photos from MECHWARE's installations. Replace generated
  illustrations before launch.

### 5.2 Key screens
- **PLP**: product table on desktop (part no., key specs as columns, stock, price-break, add-to-cart
  qty), list view on mobile; parametric filters with units and ranges; compare up to 4 with specs
  normalised to the same units.
- **PDP**: part number and manufacturer prominent; spec table grouped by class; datasheet/CAD/
  certificate downloads; stock per warehouse + lead time; quantity-break price table; "Add to quote"
  next to "Add to cart"; alternatives/successor; compatible accessories; "used in MECHWARE projects".
- **Quick order** page and requisition lists.
- **Quote** view: versions, validity countdown, PDF, accept → order.
- **Company dashboard**: orders awaiting my approval, open quotes, invoices (Mushak-6.3 PDFs),
  spend by cost centre.
- **Trust strip**: trade licence, BIN, authorised-distributor certificates, warranty terms, office
  address, real client logos (with written permission).

### 5.3 Process and quality gates
- Figma first for the 12 key screens at 360/768/1440 before code; design tokens exported (Style
  Dictionary) into `packages/config` so code and design never drift.
- Storybook + visual regression (Playwright screenshots or Chromatic) on every PR; axe in CI (exists).
- Performance budgets on a mid-range Android over 4G: LCP < 2.5 s, INP < 200 ms, CLS < 0.1,
  initial JS < 170 KB gz on PLP/PDP.
- Usability test with 5 real buyers from existing clients before launch.

---

## 6. Corrections to existing documents

| Document | Change |
|---|---|
| `01_PROJECT_BRIEF.md` | Replace ShopVerse/B2C personas and KPIs with B2B personas (purchase officer, maintenance engineer, plant manager approver, MECHWARE sales engineer); move B2B pricing, quotes, approvals, quick order into Phase 1 scope; defer seller payouts, flash sale, AI chat; add Guideline-2021, Mushak-6.3, PDPO 2025 to §14; Tailwind version aligned with code |
| `02_DESIGN_SYSTEM.md` | Light default storefront; add product-table, spec-table, price-break, quote and approval components; Bangla font stack; tabular numerals |
| `ARCHITECTURE.md` | Add the new modules (§3.2), inbox, event versioning, architecture tests, right-sized deployment, SLOs, RPO/RTO |
| `DATA_MODEL.md` | Company, CompanyUser, Role, PriceList, PriceListEntry, Quote, QuoteLine, QuoteVersion, ApprovalRule, ApprovalRequest, Invoice (Mushak-6.3), CreditTerm, InstalledMachine, Consent |
| `API_CONTRACT.md` | `/companies`, `/quotes`, `/approvals`, `/quick-order/validate`, `/invoices`, `/me/consents`, `/me/data-export` |
| `MASTER_EXECUTION_GUIDE.md` | Replace the 21-day plan with §7; AI auditor model = "latest available"; add human gates (design review, QA sign-off, pen test) |
| `04_REVIEW_PROMPTS.md` | Add B2B-specific review items (cross-company IDOR, quote immutability, approval bypass) |

---

## 7. Revised roadmap

**Team (minimum):** tech lead/.NET (full time), frontend engineer (full time), product designer
(~50 %), QA engineer (~50 %), product owner from MECHWARE sales (few hours/week), external pen
tester (1–2 weeks). AI coding assistants accelerate the engineers; they do not replace the gates.

| Wk | Phase | Exit gate |
|---|---|---|
| ✅ | 0–4 Foundation, design system v1, identity, catalogue & inventory | Done (CI green) |
| 1–2 | **Discovery & design**: interview 5 client buyers, rewrite brief, Figma key screens, threat model v1, decide direct-seller-first | Signed brief + Figma + threat model |
| 3–4 | **PIM upgrade**: attribute classes with units, datasheets/CAD with AV scan, alternatives/lifecycle, real photos import | 400+ products with verified specs |
| 5–6 | **Search & PLP/PDP** with product tables, parametric facets, part-number normalisation | p95 search < 150 ms; Lighthouse ≥ 90 |
| 7–9 | **B2B core**: companies & roles, pricing engine (price lists/breaks/contract), RFQ & quotes, approvals, quick order & CSV/BOM | IDOR suite green; pricing property tests |
| 10–12 | **Cart, checkout, payments, invoicing**: SSLCommerz/bKash/bank transfer/credit terms/COD, Mushak-6.3, Guideline-2021 rules | Payment tamper & replay tests; sample invoice reviewed by VAT consultant |
| 13–14 | **Orders & fulfilment**: state machine with SLA timers, courier adapters, RMA, notifications; customer & company dashboards | E2E order-to-invoice tests |
| 15–16 | **Admin console**: catalogue, pricing with 4-eyes, quotes desk for sales engineers, orders, audit viewer; ERP export | Staff UAT sign-off |
| 17–18 | **Hardening**: pen test + fixes, k6 load (no oversell), DR restore drill, CSP/WAF, privacy flows, runbooks | Pen-test retest clean; restore drill evidence |
| 19–20 | **Soft launch** with selected clients → public launch | SLO dashboards live; incident on-call set |
| Later | Seller marketplace + payout ledger, AI assistant, PunchOut, installed-base portal v2, mobile app | — |

---

## 8. "Definition of USD 100K grade" (launch checklist)

- [ ] A purchase officer can go from part number to approved PO-backed order in < 3 minutes.
- [ ] Every price shown comes from the server pricing engine; quotes/invoices are immutable snapshots.
- [ ] Mushak-6.3 invoice generated for every taxable order; reviewed by a VAT professional.
- [ ] Zero cross-company data access in the IDOR suite; zero client-trusted prices.
- [ ] ASVS 5.0 L2 matrix complete with evidence; external pen test retested clean (no High/Critical).
- [ ] CI: CodeQL, Semgrep, secret scan, SCA, SBOM, image scan — all blocking.
- [ ] RPO ≤ 15 min / RTO ≤ 4 h proven by a restore drill.
- [ ] SLO dashboards and alerts live; incident runbook tested.
- [ ] Lighthouse mobile ≥ 90, CLS < 0.1, WCAG 2.2 AA (axe + manual screen-reader pass), EN + BN.
- [ ] Real product photography and verified datasheet specs for all launch SKUs.
- [ ] Privacy notice, consent records, data export/delete working (PDPO 2025).
- [ ] Guideline-2021 delivery timers and refund flow in the order state machine.

---

## 9. Immediate next actions

1. Decide with MECHWARE management: **direct-seller B2B first, marketplace later** (recommended).
2. Rewrite `01_PROJECT_BRIEF.md` scope and personas (§6).
3. Raise `next`/`react` floors in `package.json` to patched versions; enable branch protection on `main`.
4. Add CodeQL + gitleaks + Dependabot workflows.
5. Start the Figma pass on PLP (product table), PDP, quick order, quote, approval screens.
6. Draft the threat model for Pricing, Quotes, Approvals and Payments.

---

## Sources

- Bangladesh Digital Commerce Guidelines — delivery timelines: [TBS News](https://www.tbsnews.net/economy/e-commerce-have-deliver-products-within-5-days-commerce-ministry-268369), [Dhaka Tribune](https://www.dhakatribune.com/business/251123/e-commerce-products-have-to-be-delivered-within)
- Personal Data Protection Ordinance 2025: [Mahbub & Company](https://mahbub-law.com/key-highlights-of-the-personal-data-protection-ordinance-2025-for-businesses/), [BSS News](https://www.bssnews.net/news/324658)
- Mushak-6.3 / Bangladesh VAT: [VATupdate — Bangladesh e-invoicing booklet](https://www.vatupdate.com/2026/08/28/bangladesh-e-invoicing-e-reporting-country-booklet/), [Lookuptax — Bangladesh VAT](https://lookuptax.com/docs/country/bangladesh)
- OWASP ASVS 5.0: [owasp.org/asvs](https://owasp.org/asvs)
- CVE-2025-55182 (React Server Components RCE): [Help Net Security](https://www.helpnetsecurity.com/2025/12/04/react-node-js-vulnerability-cve-2025-55182/)
- CVE-2025-29927 (Next.js middleware bypass): [GitHub advisory GHSA-f82v-jwr5-mffw](https://github.com/vercel/next.js/security/advisories/GHSA-f82v-jwr5-mffw)
- Next.js 16 (`proxy.ts`): [nextjs.org/blog/next-16](https://nextjs.org/blog/next-16)
- PCI DSS SAQ A eligibility: [Adyen docs](https://docs.adyen.com/pt/development-resources/pci-dss-compliance-guide/saq-a-eligibility)
- SMS pumping defences: [Telnyx Verify best practices](https://developers.telnyx.com/docs/identity/verify/security-best-practices)
- SSLCommerz order validation: [SSLCommerz v4 Order Validation API](https://sandbox-gw.sslcommerz.com/doc/v4/order-validation.html)
- B2B UX — product tables & comparison: [Baymard — product tables for desktop listings](https://baymard.com/research-articles/use-product-tables-for-desktop-product-listings), [Baymard — B2B components & machinery UX](https://baymard.com/industries/b2b-electronic-components-machinery-ux), [Baymard — comparison tool](https://baymard.com/ecommerce-design-examples/39-comparison-tool)
- B2B quick order / RFQ / PunchOut: [ChannelDock — quick order validation](https://channeldock.com/en/blogs/b2b-quick-order-form-wholesale/), [Storetasker — B2B portals for manufacturers](https://resources.storetasker.com/blog/b2b-ecommerce-portals-for-manufacturers), [DJUST — PunchOut](https://www.djust.io/en/blog/ecommerce/punchout-b2b-how-it-works-benefits-and-implementation)
- ETIM / ECLASS classification: [Credencys — ETIM & ECLASS](https://www.credencys.com/blog/etim-eclass-classification-standards/), [WisePIM — ETIM guide](https://wisepim.com/guides/product-taxonomy/etim)

> Several sources are vendor or secondary write-ups; legal items (Guideline 2021 refund deadlines,
> PDPO 2025 localisation, Mushak-6.3 penalties, PCI SAQ type) must be confirmed with a Bangladeshi
> lawyer/VAT consultant and the payment acquirer before launch.
