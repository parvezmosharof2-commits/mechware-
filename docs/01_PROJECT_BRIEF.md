# PROJECT BRIEF — MECHWARE Industrial Procurement & Automation Platform (v2)

> This file is the single source of truth. Every agent session MUST read this file,
> `COMPANY_PROFILE.md`, `02_DESIGN_SYSTEM.md` and `100K_GRADE_REVIEW_AND_PLAN.md` before writing any
> code. If code and this brief disagree, the brief wins until the product owner updates it.
>
> **v2 (2026-10-30)** replaces the v1 brief, which was adapted from a generic B2C marketplace
> template (consumer personas, consumer KPIs, "B2B pricing — do not build"). The rationale is in
> `docs/100K_GRADE_REVIEW_AND_PLAN.md`. Code namespaces keep the internal code name `ShopVerse`;
> every user-facing surface says **MECHWARE**.

---

## 1. Mission and Quality Bar

Build the place where Bangladeshi factories buy automation parts, spare parts, machines and
engineering services from MECHWARE: from a part number to an approved, invoiced order in minutes,
with the depth of **McMaster-Carr / DigiKey / Mouser / Grainger**, the quote-to-order flow of
**Amazon Business**, and the admin quality of **Stripe / Linear**. The quality must be impossible to
tell apart from a USD 100,000+ agency build.

Benchmarks to match or exceed:
- **McMaster-Carr / DigiKey / Mouser**: spec-dense product tables, parametric filters, datasheets and
  CAD on every product, part-number search that just works, instant page loads.
- **Grainger / Amazon Business**: company accounts, multi-buyer roles, approvals, quotes, PO and
  credit terms, reorder lists, quick order.
- **Siemens Industry Mall**: lifecycle status (active / phase-out / discontinued) with successor parts.
- **Stripe / Linear**: console UX, data tables, keyboard-first operations.

"Done" means ALL of the following: it works end-to-end, looks premium at every breakpoint and in
both themes, is fast on a mid-range Android over 4G, accessible, secure (with evidence), covered by
tests, and documented.

---

## 2. Business Context

- **Brand**: MECHWARE — tagline: YOUR GATEWAY TO INDUSTRY 4.0
- **Subtitle**: Technology for Smarter Systems | Made in Bangladesh Machinery
- **Office**: Sha-23/E (6th Floor), Modhubon Tower, Gulshan Link Rd, Dhaka 1212, Bangladesh
- **Contact**: +88 01748-305931 | parvez.mechware@gmail.com | mechware.com.bd
- **Who buys**: factories and conglomerates in 12 industries (textile, garments, electronics, food
  & beverage, pharma, paper, foam, plastics, blade manufacturing, chemicals, power, ETP/water).
  They buy with quotations, purchase orders, internal approvals, credit terms and VAT invoices.
- **Business model (launch)**: **MECHWARE sells directly** (own stock + authorised-distributor
  stock of Siemens, Mitsubishi Electric, Delta, Omron, Yaskawa), plus Made-in-Bangladesh machinery,
  retrofit, AMC and engineering projects.
- **Business model (after launch)**: open the catalogue to vetted third-party sellers
  (marketplace). The data model keeps `SellerId` on catalogue and order lines from day one so this
  needs no migration, but seller onboarding, payouts and the payout ledger are **not** in the launch
  scope.
- **Markets**: Bangladesh primary (BDT, Bangla + English). Export of Made-in-BD machinery (USD,
  quote-only) secondary.
- **Launch categories (12)**:
  1. PLCs & Automation Controllers
  2. HMIs & Industrial Displays
  3. VFDs & Motor Inverters
  4. Servo Drives & Motion Control
  5. SCADA Software & IIoT Edge Gateways (OPC UA / MQTT)
  6. Sensors & Process Instrumentation
  7. Control Panels & Switchgear
  8. Made in Bangladesh Machinery (cutting, packing, chemical dosing, ETP skids) — quote-only
  9. Retrofit Kits & Machine Spare Parts
  10. Industrial Cooling & HVAC Automation
  11. Pneumatics, Power Supplies & Encoders
  12. Engineering Services, AMC & Training — quote / booking only

### 2.1 Personas

| Persona | Profile | Core needs | Design implications |
|---|---|---|---|
| **Sabbir — purchase officer** | Procurement desk at a garment group, laptop + office printer, buys 30–200 lines/month | Find exact part numbers fast, compare prices, get a formal quote, raise a PO, get a Mushak-6.3 invoice | Quick order (paste / CSV), requisition lists, quote PDF, invoice archive, dense tables |
| **Tanvir — maintenance engineer** | Plant floor, Android phone, line is down | "Which part replaces this one?", stock now, delivery today | Part-number search, successor/alternative parts, stock per warehouse, cut-off timer, mobile-first PDP |
| **Mr. Haque — plant manager / approver** | Approves spend, reads on phone | See what is waiting, approve or reject in one tap, stay within budget | Approval inbox, spend vs budget, push/email with deep link |
| **Farzana — MECHWARE sales engineer** | Internal, builds quotes for projects | Turn an RFQ or BOM into a priced quote, apply contract prices, track follow-ups | Quote desk in console, price override with reason, versioning |
| **Ops admin** | MECHWARE staff | Catalogue quality, orders, fulfilment, CMS | Fast tables, bulk actions, audit log |
| **Finance** | MECHWARE staff | Credit limits, invoices, payments, reconciliation, VAT returns | Ledgers, exports (Mushak-9.1 data), reports |
| **Walk-in buyer** | Small workshop owner, phone, pays bKash/COD | Buy 1–5 items without an account | Guest checkout still supported |

### 2.2 Success KPIs

| KPI | Target |
|---|---|
| Part number → order placed (known buyer, quick order) | < 3 minutes |
| RFQ submitted → first quote sent | < 4 working hours (SLA shown to buyer) |
| Quote → order conversion | ≥ 35% |
| Search: exact part-number hit rate | ≥ 98% (any formatting: spaces, dashes, case) |
| Zero-result searches | < 3% |
| Repeat-buyer share of revenue (90 days) | ≥ 60% |
| Approval turnaround (median) | < 4 working hours |
| Mobile Lighthouse Performance | ≥ 90 on home, PLP, PDP, cart |
| Orders delivered inside the legal window (Guideline 2021) | ≥ 99% |

---

## 3. Scope

### 3.1 Launch scope — must ship
1. **Storefront**: home, category, search, product detail, compare, cart, quick order, checkout,
   order confirmation, order tracking, help center, legal pages, "Request a quote", "Book an
   engineer / project inquiry".
2. **Identity**: email + password, phone OTP, Google sign-in, passkeys (staff mandatory, buyers
   optional), TOTP; guest checkout for small orders.
3. **Company accounts (B2B)**: company profile (trade licence, BIN, TIN), branches / delivery
   sites, users with roles (Requester, Buyer, Approver, Company Admin), cost centres, spend limits,
   credit terms and limits set by MECHWARE finance.
4. **Catalogue / PIM**: category tree, brands, products, variants (SKU = manufacturer part
   number), **class-based technical attributes with units** (ETIM/ECLASS-style), datasheets, CAD,
   certificates, lifecycle (active / phase-out / discontinued) with successor and alternatives,
   compatible accessories, media.
5. **Search**: part-number normalisation, typo tolerance, parametric facets from attributes,
   synonyms, Bangla / Banglish.
6. **Pricing**: list price, quantity breaks, customer-group price lists, company contract prices,
   quote prices; VAT inclusive / exclusive; validity windows.
7. **RFQ & quotes**: RFQ from cart, from a BOM/CSV upload or from free text; quote versions, PDF,
   expiry, accept → order.
8. **Quick order & lists**: paste SKUs with quantities, CSV/BOM upload with line-level validation,
   requisition lists, reorder from history.
9. **Approvals**: rule-based (amount, category, cost centre) approver chains; approve / reject with
   comment; order is placed only when the chain completes.
10. **Checkout & payments**: SSLCommerz (cards / net banking), bKash, bank transfer against
    invoice, **credit terms (net 15/30) for approved companies**, COD with OTP for small orders.
11. **Orders & fulfilment**: state machine with legal SLA timers, partial shipments, courier
    adapters, delivery to site, returns / RMA, refunds.
12. **Invoicing & tax**: **Mushak-6.3** tax invoices, credit notes, immutable PDF archive,
    Mushak-9.1 export for finance.
13. **Service & projects**: project inquiries, retrofit / AMC requests, engineer booking;
    "My machines" (installed base delivered by MECHWARE) with compatible spare parts — v1 is a list
    per company maintained by staff.
14. **Console**: dashboard, catalogue/PIM, pricing (4-eyes), quote desk, orders, companies & credit,
    invoices, CMS, reviews, roles/permissions, audit log viewer, settings.
15. **Notifications**: email, SMS, in-app (matrix in §10).
16. **AI** (server-side only, §15): natural-language and part-number search assistance, datasheet
    Q&A over our own catalogue data, quote-from-BOM line matching.

### 3.2 After launch — design for it, do NOT build now
Third-party seller onboarding, seller center, commissions and payout ledger; PunchOut (cXML / OCI)
for large corporate buyers; ERP connectors beyond CSV export; mobile apps (same API); loyalty;
sponsored listings; live chat; installed-base portal v2 (IoT data, service history); flash sales.

### 3.3 Explicitly out of scope
Consumer fashion/lifestyle catalogue, consumer promotions such as buy-X-get-Y and wishlists-as-gifts,
marketplace-wide wallet.

---

## 4. Tech Stack

> C# / .NET 10 for the core backend, order processing, pricing and high-concurrency APIs;
> TypeScript / Next.js for the storefront and console. **Patched versions are a requirement**: the
> lockfile must never resolve a version listed in CISA KEV or a GitHub critical advisory
> (e.g. React Server Components CVE-2025-55182, Next.js middleware CVE-2025-29927).

| Area | Choice | Notes |
|---|---|---|
| Storefront | Next.js 15.5 App Router (TypeScript, Tailwind CSS, Radix/shadcn, TanStack Query, next-intl) | Tailwind 3.4 today; move to Tailwind 4 with the design refresh; move to Next 16 (`proxy.ts`) once stable in our stack |
| Console | Next.js (TanStack Table, Recharts) | Separate subdomain behind Zero-Trust access |
| Backend | C# / .NET 10 ASP.NET Core Web API, Clean Architecture, enforced module boundaries | Architecture tests in CI |
| ORM / data | EF Core 10 + Npgsql (+ raw SQL for hot paths) | Migrations reviewed; xmin concurrency tokens |
| Database | PostgreSQL 16 (managed, PITR) | Row locks for stock, check constraints for invariants |
| Workers | .NET BackgroundService + transactional outbox / inbox | `FOR UPDATE SKIP LOCKED` relay |
| Cache / locks | Redis 7 (StackExchange.Redis, RedLock) | Never the source of truth |
| Search | Meilisearch behind `ISearchProvider` | Parametric facets from PIM attributes |
| Storage | S3-compatible (MinIO local, R2/S3 prod), signed URLs, AV scan before publish | Separate cookieless domain |
| Money | `decimal` + integer minor units in DB, banker's rounding | ADR-0002 |
| Payments | SSLCommerz, bKash, bank transfer, credit terms, COD via `IPaymentProvider` | Hosted pages only |
| PDFs | QuestPDF (quotes, Mushak-6.3 invoices, credit notes) | Immutable after issue |
| AI | Server-side `AiProvider` (current model generation, provider-agnostic) | Key never reaches the browser |
| Edge | Cloudflare (DNS, WAF, bot management, rate limits, image resizing) | |
| Observability | OpenTelemetry, Serilog JSON, Sentry, Prometheus/Grafana | SLOs in §11 |
| Testing | xUnit, FluentAssertions, real Postgres/Redis in CI, Playwright E2E, axe, k6 | §16 |

---

## 5. Repository Structure

```
mechware-/
├── backend/
│   ├── ShopVerse.sln
│   ├── src/  (Domain, Application, Infrastructure, Payment, Worker, Api)
│   ├── seed/ (catalogue dump)
│   └── tests/ (UnitTests, IntegrationTests)
├── frontend/
│   ├── apps/web       # storefront
│   ├── apps/console   # console
│   └── packages/      # ui, config, api-client
├── infra/             # docker, k6
├── docs/              # brief, design system, ADRs, architecture, security
├── PROGRESS.md
└── README.md
```

---

## 6. Architecture Principles

1. **Modular monolith, enforced.** Modules: identity, companies, catalog/PIM, inventory, pricing,
   search, quotes, cart & quick order, approvals, checkout, payments, orders, fulfilment, invoicing,
   service, cms, notifications, ai, audit (sellers/ledger later). A module uses another only through
   its application interface or integration events. Layering (Domain ← Application ← Infrastructure
   ← Api) is checked by architecture tests in CI.
2. **Transactional outbox + inbox.** Events are written in the same DB transaction as the state
   change. Consumers dedupe through an inbox table (the same table dedupes payment webhooks).
   Events carry `type`, `version`, `occurredAtUtc`, `correlationId`. Examples: `QuoteSent`,
   `QuoteAccepted`, `ApprovalRequested`, `ApprovalGranted`, `OrderPlaced`, `PaymentCaptured`,
   `PaymentFailed`, `OrderShipped`, `OrderDelivered`, `InvoiceIssued`, `CreditLimitExceeded`,
   `ProductUpdated`, `StockLow`, `ProductDiscontinued`.
3. **Ports and adapters** for every external dependency (payment, courier, SMS, email, storage,
   search, AI, ERP export), each with a mock adapter for dev and tests.
4. **Typed contract.** OpenAPI from the API; `packages/api-client` is generated from it.
5. **Validated config.** Options validated at boot; the app refuses to start if invalid.
6. **Idempotency.** Every mutating checkout / payment / quote-accept / approval endpoint requires
   an `Idempotency-Key`; responses stored 24 h.
7. **Money.** Integer minor units + ISO currency; one `Money` type; banker's rounding (ADR-0002).
8. **Snapshots.** Quotes, orders and invoices store immutable price/tax/address snapshots; they are
   never recomputed from current prices.
9. **Tenancy.** Every B2B row carries `CompanyId`; EF Core global query filters enforce company
   scope; an IDOR test suite proves it for every endpoint.
10. **Time.** Store UTC, display Asia/Dhaka by default.
11. **Audit.** Every staff and company-admin write is recorded (actor, action, entity, before,
    after, IP) in a hash-chained, tamper-evident log.
12. **Feature flags** for risky features. **ADRs** for every significant decision.

---

## 7. Domain Model (minimum fields — extend as needed)

Existing (built in Phases 3–4): User, Address, Role/Permission, RefreshToken, OtpCode, AuditLog,
Category, Brand, Product, ProductVariant, ProductMedia, AttributeDefinition, SlugHistory, Warehouse,
InventoryItem, StockReservation, Review, Seller, Outbox.

New for v2:
- **Company**: legalName, tradeLicenceNo, BIN, TIN, industry, status (pending/active/suspended),
  creditTerms (none/net15/net30), creditLimit (Money), creditUsed, defaultPriceListId, accountManagerId.
- **CompanySite**: companyId, name, address, contact, isDefaultDelivery.
- **CompanyUser**: companyId, userId, roles (requester/buyer/approver/admin), costCentreIds, spendLimit.
- **CostCentre**: companyId, code, name, monthlyBudget.
- **PriceList / PriceListEntry**: name, currency, validFrom/To, status (draft/pending_approval/active);
  entry: variantId, minQty, unitPrice. Assignment to customer group or company (contract).
- **Rfq**: companyId?, contact, source (cart/bom/free_text), lines (requested part no., qty, notes,
  matched variantId?), attachments, status.
- **Quote / QuoteVersion / QuoteLine**: number `MWQ-YYMMDD-XXXX`, version, lines (variant snapshot,
  qty, unitPrice, discount, VAT), validUntil, status (draft/sent/accepted/expired/rejected),
  terms, preparedBy, pdfObjectKey.
- **RequisitionList**: companyId, owner, name, lines.
- **ApprovalRule / ApprovalRequest / ApprovalStep**: conditions (amount, category, cost centre),
  approver chain, decisions with comment and timestamp.
- **Order / OrderLine / OrderEvent**: as v1 plus companyId, siteId, poNumber, costCentreId, quoteId?,
  approvalRequestId?, paymentTerms, legalDeliveryDeadlineUtc.
- **Payment / Refund / Shipment / ReturnRequest**: as v1.
- **Invoice (Mushak-6.3)**: number (sequential per fiscal year, gap-free), issueDateUtc, seller BIN,
  buyer BIN/name/address, lines (description, qty, unit price, VAT rate, VAT amount, supplementary
  duty if any), totals, status (issued/cancelled), pdfObjectKey, sha256. **CreditNote** references
  the invoice.
- **InstalledMachine**: companyId, siteId, model, serial, commissionedAt, compatibleVariantIds.
- **ServiceRequest**: companyId, type (retrofit/AMC/breakdown/project), description, attachments, status.
- **Consent**: userId, purpose, version, grantedAtUtc, withdrawnAtUtc.
- **IdempotencyRecord, InboxMessage, FeatureFlag, Setting**.

---

## 8. Business Rules (implement exactly; unit-test every rule)

### 8.1 Price resolution (server-side only)
- For a buyer, variant and quantity, the unit price is the **lowest valid** of: accepted quote line
  (if ordering from that quote) → company contract price → customer-group price list → quantity
  break on list price → list price. Sale prices apply only to list price. The chosen source is
  returned with the price so the UI can explain it ("Your contract price").
- VAT: per category (default 5% unless configured); BD shows VAT-inclusive for guests and
  **VAT-exclusive with VAT line for logged-in companies**; tax is calculated server-side only.
- The server recalculates every price, discount, shipping fee and tax at checkout. Client totals
  are ignored. Price-list changes require a second staff approver (4-eyes).

### 8.2 Quotes
- Quote prices are fixed for the quote's validity (default 15 days). Accepting an expired quote is
  rejected. Accepting creates an order with the quote's snapshot prices and re-checks stock.
- Every change creates a new version; sent versions are immutable. Overrides below floor price need
  manager approval and a reason.

### 8.3 Approvals
- A company admin defines rules; if any rule matches, the order waits in `pending_approval` and
  stock is **not** reserved until approval (configurable soft-hold for 24 h).
- Approver cannot approve their own request. Rejections require a comment. All decisions audited.

### 8.4 Quick order
- Input: pasted lines (`partNo qty`), CSV or BOM. Each line is validated for: SKU found (normalised),
  lifecycle (discontinued → show successor), MOQ, pack size, stock, price, approval route.
  Errors are per line and actionable. Max 500 lines per upload.

### 8.5 Inventory
- Stock per variant per warehouse. Reserve on checkout start with a 15-minute TTL using row locks in
  deterministic order (built). Convert to deduction on payment or on order confirmation for credit
  terms; release on failure or expiry. **Never oversell** — proved by concurrency and chaos tests.

### 8.6 Checkout, orders and legal timelines
- Order number `MW-YYMMDD-XXXXXX` (random base32, unique).
- State machine (single place, every change in `OrderEvent`):
  `pending_approval → pending_payment → confirmed → processing → packed → shipped → out_for_delivery
  → delivered → completed`; side paths `cancelled`, `delivery_failed → returned`,
  `return_requested → return_approved → refunded`.
- **Digital Commerce Operation Guidelines 2021**: delivery deadline = 5 days (same city) / 10 days
  (other city) from confirmation, stored as `legalDeliveryDeadlineUtc` and monitored; hand to courier
  within 48 h after advance payment; advance-payment and escrow rules per the guideline; refunds
  within the guideline's deadline. (Exact refund deadline to be confirmed by counsel.)
- Pending online-payment orders auto-cancel after 30 minutes.

### 8.7 Payments
- Confirmed **only** by a server-side verification: SSLCommerz IPN + Order Validation API
  (`val_id`, matching `tran_id`, amount, currency, valid status); bKash query/execute. Never by the
  browser redirect.
- Webhooks are signature-verified, time-windowed and deduped through the inbox.
- Credit terms: order allowed only if `creditUsed + orderTotal ≤ creditLimit`; checked in the same
  transaction that places the order (row lock on the company credit row).
- COD: serviceable areas only, max order value configurable (default 15,000 BDT), phone OTP.
- Nightly reconciliation with provider settlements; mismatches raise alerts.
- Card data never touches our servers (hosted payment pages).

### 8.8 Invoicing (Mushak-6.3)
- One Mushak-6.3 invoice per taxable supply, issued on confirmation (credit terms) or payment.
- Gap-free sequential numbering per fiscal year (July–June), generated in the DB transaction.
- Issued invoices are immutable; corrections only via credit note. PDF + SHA-256 stored.
- Monthly export with the data finance needs for the Mushak-9.1 return. Format reviewed by a VAT
  consultant before launch.

### 8.9 Shipping
- Zones (Inside Dhaka, Dhaka suburbs, Outside Dhaka, International quote-only); rate = zone base +
  per-kg on chargeable weight; heavy/oversize items quote-only. Cut-off timer on PDP.

### 8.10 Returns
- Per-product return policy (default 7 days after delivery; electrical parts opened/installed are
  non-returnable unless faulty). RMA with photos; refund to original method or credit note.

### 8.11 Reviews
- Only verified purchasers can review, once per order line, after delivery; moderation queue.

---

## 9. Roles and Permissions (permission-based, never role checks in code)

Staff:

| Permission group | Super Admin | Ops Admin | Catalog Manager | Sales Engineer | Support | Finance | Content Editor |
|---|---|---|---|---|---|---|---|
| Users & roles | ✔ | – | – | – | view | – | – |
| Catalogue / PIM | ✔ | ✔ | ✔ | view | view | – | – |
| Price lists | ✔ (approve) | view | propose | propose | – | approve | – |
| Quotes | ✔ | ✔ | – | ✔ | view | view | – |
| Orders | ✔ | ✔ | view | view | ✔ | view | – |
| Refunds | ✔ | ✔ | – | – | ≤ limit | ✔ | – |
| Companies & credit | ✔ | view | – | view | view | ✔ | – |
| Invoices | ✔ | view | – | view | view | ✔ | – |
| CMS | ✔ | ✔ | – | – | – | – | ✔ |
| Audit log | ✔ | view | – | – | – | view | – |

Company users (scoped to their own company only):

| Permission | Requester | Buyer | Approver | Company Admin |
|---|---|---|---|---|
| Browse, RFQ, lists | ✔ | ✔ | ✔ | ✔ |
| Place order | submit for approval | ✔ (within limit) | ✔ | ✔ |
| Approve orders | – | – | ✔ (not own) | ✔ (not own) |
| View company orders & invoices | own | ✔ | ✔ | ✔ |
| Manage users, sites, rules | – | – | – | ✔ |

Permissions are strings like `quotes.send`, `pricelists.approve`, checked by
`[RequirePermission]`; the UI hides what the user cannot do; the API enforces it regardless.

---

## 10. Notification Matrix

| Event | Buyer | Approver | Company Admin | MECHWARE staff | Channels |
|---|---|---|---|---|---|
| Account / OTP | ✔ | | | | email, SMS |
| RFQ received | ✔ | | | sales | email, in-app |
| Quote sent / expiring in 48 h | ✔ | | | | email, in-app |
| Approval requested | | ✔ | | | email, in-app, SMS (optional) |
| Approved / rejected | ✔ | | | | email, in-app |
| Order placed / payment failed | ✔ | | | ops | email, SMS, in-app |
| Shipped / out for delivery / delivered | ✔ | | | | email, SMS, in-app |
| Delivery deadline at risk (legal SLA) | | | | ops | in-app, email |
| Invoice issued | ✔ | | ✔ | | email (PDF link), in-app |
| Credit limit 80% / exceeded | | | ✔ | finance | email, in-app |
| Product discontinued (on a requisition list) | ✔ | | | | email |
| Back in stock | ✔ | | | | email |
| Low stock | | | | ops | in-app |

Bilingual, branded, mobile-friendly templates with plain-text fallback; marketing mail needs consent.

---

## 11. Non-Functional Requirements

| Area | Requirement |
|---|---|
| Performance | Mobile Lighthouse ≥ 90 (home, PLP, PDP, cart). LCP < 2.5 s, INP < 200 ms, CLS < 0.1 on 4G mid-range Android. Initial route JS < 170 KB gz. Search p95 < 150 ms; add-to-cart p95 < 300 ms; checkout submit p95 < 800 ms. |
| Scalability | 2,000 concurrent users, 100 orders/minute peak without overselling (k6). Stateless API, horizontal scaling. |
| Availability | Storefront 99.9 % SLO with error budget; health/readiness endpoints; graceful shutdown. |
| Data protection | RPO ≤ 15 min, RTO ≤ 4 h (managed Postgres PITR). Restore drill every quarter with written evidence. |
| Security | OWASP ASVS 5.0 Level 2 for everything, Level 3 controls for payments, pricing and admin (§11.1). |
| Accessibility | WCAG 2.2 AA. Axe in CI with zero serious/critical; manual screen-reader pass before launch. |
| i18n | English + Bangla (UI, emails, SMS, PDFs), locale-aware numbers/dates/currency. |
| Browser support | Last 2 versions of Chrome, Safari, Firefox, Edge; iOS Safari 15+, Android Chrome. |
| Observability | OpenTelemetry traces browser → API → DB → worker; structured logs; Sentry; SLO burn-rate alerts; business alerts (payment validation failures, OTP conversion drop, oversell attempts, outbox lag). |

### 11.1 Security requirements
- **Process**: STRIDE threat model per module before building it; ASVS 5.0 requirement → test →
  evidence matrix in `docs/security/`; CodeQL, secret scanning, dependency audit and SBOM in CI;
  patch SLA (critical/KEV ≤ 48 h, high ≤ 7 days); external grey-box pen test before launch and
  retest; incident-response runbook and tabletop exercise.
- **Identity**: Argon2id passwords; lockout + progressive delay; access JWT 15 min (HS256 with
  `alg`, `iss`, `aud`, `exp` all validated); rotating refresh token in `__Host-` httpOnly, Secure,
  SameSite cookie with reuse detection; passkeys mandatory for staff; TOTP mandatory for admin and
  finance; step-up auth for price lists, refunds, credit limits, role changes and exports.
- **Authorisation**: every endpoint is either `[Authorize]`/`[RequirePermission]` or on a reviewed
  public allow-list (enforced by an automated test). Next.js middleware/proxy is UX only, never an
  authorisation boundary. Company scope via global query filters + IDOR suite.
- **Abuse**: rate limits (login/OTP 5/min per IP+identifier, general 100/min per IP, search
  30/10 s), CAPTCHA before OTP send, per-number OTP limits, BD-number allowlist, conversion
  alerting (SMS pumping); bot management on price/search endpoints against price scraping.
- **Input/output**: validation on every endpoint; output encoding; rich text sanitised with an
  allow-list (built); URLs restricted to https or site-relative (built); no stack traces or internal
  details in error responses.
- **Web**: CSP with nonces, HSTS preload, `frame-ancestors 'none'`, Referrer-Policy,
  Permissions-Policy, SRI for third-party scripts, script inventory on payment pages.
- **Files**: type allow-list, magic-byte check, size limits, AV scan before publish, signed URLs from
  a separate cookieless domain.
- **Secrets**: managed secret store only; never in code, logs, or the client bundle.
- **Data**: PII and KYC numbers encrypted at application level; tamper-evident audit log.

---

## 12. SEO Requirements
- Clean URLs: `/c/[category-slug]`, `/p/[product-slug]-[shortId]`, `/b/[brand-slug]`,
  `/search?q=`; part-number URLs `/pn/[normalised-part-no]` redirect to the product. Locale prefix
  `/bn/...`.
- Dynamic metadata, OG images; JSON-LD: Organization, WebSite + SearchAction, BreadcrumbList,
  Product + Offer (with `mpn`, `brand`, `gtin` when known), ItemList, FAQPage.
- Split sitemaps, robots, canonical, `hreflang`, faceted URLs `noindex` except whitelisted filters.
- 301 redirects when slugs change (built: slug history).
- Datasheet PDFs are indexable through their product page, not hot-linked.

---

## 13. Analytics
- GA4 ecommerce events with standard names plus B2B custom events: `rfq_submit`, `quote_view`,
  `quote_accept`, `quick_order_upload`, `approval_decision`.
- Consent-aware (no marketing tags before consent). Internal analytics tables for console reports.

---

## 14. Compliance and Legal (Bangladesh)
- **Digital Commerce Operation Guidelines 2021**: seller identity, delivery and refund timelines
  shown before payment; full price including VAT and delivery fee before payment; delivery
  deadlines enforced in the order state machine (§8.6).
- **VAT and SD Act / NBR**: Mushak-6.3 invoices (§8.8); BIN shown on invoices and in the footer.
- **Personal Data Protection Ordinance 2025**: explicit, purpose-specific, revocable consent with
  records; privacy notice in Bangla and English; data-subject requests (export, correction,
  deletion/anonymisation) in the account area; retention schedule (tax records kept as the law
  requires); processor list and cross-border transfer review before choosing hosting.
- **Pages**: Terms of Sale (B2B and guest), Privacy, Cookie, Returns & Warranty, Shipping, Credit
  Terms, Quote Terms.
- Legal details marked "to be confirmed" in this brief must be signed off by a Bangladeshi lawyer and
  VAT consultant before launch.

---

## 15. AI Features

The AI key lives only in the server environment. All calls go through `AiProvider` with
timeouts, retries, per-user rate limits, response caching, cost logging and a non-AI fallback.
AI never invents prices, stock or specifications: it only uses tool calls to our catalogue.

| Feature | Where | Behaviour |
|---|---|---|
| Natural-language search | Storefront | "Delta 2.2 kW VFD 3-phase with Modbus" → query + attribute filters; falls back to keyword search |
| Part-number assist | Search | Fuzzy match on partial/garbled part numbers; suggests successor for discontinued parts |
| BOM line matching | RFQ / quick order | Matches free-text BOM lines to variants with confidence; humans confirm low-confidence lines |
| Datasheet Q&A | PDP | Answers from our stored datasheet text with citations; labelled "AI answer" |
| Quote drafting | Console | Drafts a quote from an RFQ for the sales engineer to edit |
| Review moderation | Background | Flags spam/abuse |

Prompts are versioned in code; outputs shown to users are labelled.

---

## 16. Engineering Standards and Definition of Done

- Conventional Commits; small focused commits; PRs reviewed; `main` protected.
- `dotnet format`, ESLint, typecheck pass with zero warnings; no `any` / `@ts-ignore` without reason.
- Standard error shape `{ error: { code, message, details, traceId } }`; no internals leaked.
- Every page/component has loading, empty, error and success states; every string through i18n.
- **Test pyramid (all in CI)**:
  - Unit tests for every rule in §8, plus **property-based tests** for Money, pricing and
    sanitisers, and **architecture tests** for layering.
  - Integration tests against real Postgres/Redis, including **concurrency and chaos tests** for
    stock, credit limits and invoice numbering.
  - **Security regression tests**: endpoint authorisation coverage, JWT tampering, IDOR matrix,
    error-leakage, rate limits.
  - Playwright E2E for: part number → order, RFQ → quote → order, approval flow, guest checkout.
  - Axe accessibility checks; visual regression on key screens.
  - k6 load tests before release.
- **Supply chain**: CodeQL, secret scanning, dependency audit (NuGet + pnpm) blocking on high/critical.

**A feature is DONE only when**:
1. It meets the acceptance criteria in its phase prompt.
2. All checks above pass in CI.
3. It was visually verified at 360, 768, 1280 and 1920 px, light and dark, English and Bangla.
4. No console errors or warnings.
5. Threat-model entries for the feature are addressed; `PROGRESS.md` and docs are updated.

---

## 17. Agent Working Rules

1. Read this brief, `COMPANY_PROFILE.md`, `02_DESIGN_SYSTEM.md` and
   `100K_GRADE_REVIEW_AND_PLAN.md` at the start of every phase.
2. Before coding, post a short plan (files, risks, threat-model notes). Wait for approval if the plan
   changes architecture.
3. Build vertical slices (DB → API → UI → tests) so each slice is demoable.
4. No placeholder content in final UI; no hard-coded secrets, prices or business config.
5. Never silently skip a requirement; if something cannot be done, say so and propose an alternative.
6. AI-assisted review does not replace the human gates: design review, QA sign-off, pen test.
7. After each phase: run all checks, open the app in the browser, screenshot key screens, and report
   what was done, what is left, and how to verify it.
8. If a requirement is ambiguous, ask a concise question with a recommended default.
