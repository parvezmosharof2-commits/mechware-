# PROJECT BRIEF — MECHWARE Industrial Commerce & Automation Platform

> This file is the single source of truth. Every agent session MUST read this file,
> `COMPANY_PROFILE.md`, and `02_DESIGN_SYSTEM.md` before writing any code. If code and this brief disagree, the brief
> wins until the product owner updates it.

---

## 1. Mission and Quality Bar

Build a production-grade, industrial machinery and automation marketplace whose quality cannot be told
apart from a USD 100,000+ agency build (combining the depth of Amazon Business / McMaster-Carr with the visual finesse of Apple / Siemens Industry).

Benchmarks to match or exceed:
- **Amazon Business / McMaster-Carr / Grainger**: catalog depth, technical spec filtering, instant RFQ/invoicing, CAD/datasheet attachments, corporate purchasing.
- **Apple Store / Siemens Digital Enterprise**: sleek dark industrial aesthetic, cybernetic circuit motifs, typography, product highlight precision.
- **Shopify Plus / Stripe**: streamlined checkout, frictionless quote-to-order, modern B2B/B2C payment workflows.
- **Stripe / Linear / Shopify Admin**: admin and seller dashboard UX.

"Done" means ALL of the following: it works end-to-end, looks premium at every breakpoint and
in both themes, is fast, accessible, secure, covered by tests, and documented.

---

## 2. Business Context

- **Brand**: MECHWARE — tagline: [YOUR GATEWAY TO INDUSTRY 4.0]
- **Subtitle**: Technology for Smarter Systems | Made in Bangladesh Machinery
- **Origin & Office**: Established 2024 | Sha-23/E (6th Floor), Modhubon Tower, Gulshan Link Rd, Dhaka 1212, Bangladesh
- **Contact**: +88 01748-305931 | parvez.mechware@gmail.com | mechware.com.bd
- **Business model**: Hybrid Marketplace & Direct Manufacturer/Engineering Provider.
  - Direct Sales of MechWare Made-in-Bangladesh Machinery & Retrofit services.
  - Multi-vendor marketplace for industrial component sellers, authorized distributors (Siemens, Mitsubishi, Delta, Omron).
  - Commission per category (default 8%, configurable 3–20%).
  - Seller plans: Free (commission only) and Pro (monthly fee, lower commission, more listings).
  - RFQ (Request for Quote) & Turnkey Engineering project bookings.
- **Markets**: Bangladesh primary (BDT, Bangla + English). International secondary (USD, Export of Made-in-BD machinery).
- **Launch categories (12)**:
  1. PLCs & Automation Controllers (Siemens, Mitsubishi, Delta, Omron)
  2. HMIs & Industrial Touch Displays
  3. VFDs & Motor Inverters (Energy-saving variable frequency drives)
  4. Servo Drives & Motion Control Systems
  5. SCADA Software & IIoT Edge Gateways (OPC UA / MQTT)
  6. Smart Sensors & Process Instrumentation (Temp, Pressure, Flow, Optical)
  7. Industrial Control Panels & Switchgear
  8. Made in Bangladesh Machinery (Cutting, Packing, Chemical Dosing, ETP skids)
  9. Retrofit Kits & Machine Spare Parts
  10. Industrial Cooling & HVAC Automation
  11. Pneumatics, Power Supplies & Encoders
  12. Engineering Services, AMC & Technical Training

### 2.1 Personas

| Persona | Profile | Core needs | Design implications |
|---|---|---|---|
| Rahim — value buyer | 24, Android phone, slow 4G, prefers Cash on Delivery | Low price, trust, COD, clear delivery date | Mobile-first, light pages, COD always visible, deal badges |
| Nadia — premium buyer | 34, iPhone + laptop, pays by card | Speed, authenticity, easy returns | Clean UI, "Authentic" badges, 1-click reorder, saved cards |
| Karim — seller | Runs a small shop, not technical | Easy listing, see orders, get paid on time | Guided onboarding, bulk upload, clear payout ledger |
| Ops admin | Company staff | Control catalog, orders, sellers, content | Fast data tables, bulk actions, audit log |
| Support agent | Company staff | Find any order, refund, contact customer | Global search, order timeline, notes |
| Finance | Company staff | Commissions, payouts, reconciliation | Ledgers, exports, reports |

### 2.2 Success KPIs (the product is designed to hit these)

| KPI | Target |
|---|---|
| Storefront conversion rate | ≥ 2.5% |
| Cart abandonment | < 65% |
| Guest checkout completion time | < 90 seconds |
| Search → product click rate | ≥ 40% |
| Zero-result searches | < 5% |
| Mobile Lighthouse Performance | ≥ 90 |
| Seller time-to-first-listing | < 15 minutes |

---

## 3. Scope

### 3.1 MVP — must ship
1. Storefront: home, category, search, product detail, cart, checkout, order confirmation,
   order tracking, customer account, seller store page, help center, static/legal pages.
2. Auth: email + password, phone OTP, Google sign-in, guest checkout.
3. Catalog: categories (tree), brands, products, variants, attributes, media.
4. Search: typo-tolerant, faceted, autocomplete, synonyms, Bangla support.
5. Cart, promotions engine, checkout, payments (card, mobile wallet, COD).
6. Orders: split by seller, state machine, shipping, tracking, returns, refunds.
7. Seller center: onboarding/KYC, listings, inventory, fulfilment, payouts, analytics.
8. Admin: dashboard, catalog, orders, customers, sellers, promotions, CMS, reviews,
   finance, reports, roles/permissions, audit log, settings.
9. Notifications: email, SMS, in-app.
10. AI features (section 15).

### 3.2 Phase 2 — design the architecture for these, do NOT build them now
Mobile apps (same API), sponsored product ads, loyalty points, subscriptions,
live chat, B2B pricing, multi-warehouse routing, flash-sale queueing, marketplace-wide wallet.

---

## 4. Tech Stack (Enterprise Hybrid: C# .NET 10 Backend + Next.js 15 Frontend)

> [!NOTE]
> **C# (.NET 10, ASP.NET Core 10, C# 13, Visual Studio 2026)** is chosen for the **Core Backend, Order Processing, Financial Ledger & High-Concurrency APIs**. Verified and running on .NET 10.0.401 on this machine. TypeScript (Next.js 15) is kept for the **Storefront and Admin/Seller Web UI** for top-tier SEO, Core Web Vitals, and responsive UI design.

| Area | Choice | Why |
|---|---|---|
| Frontend Storefront | Next.js 15 App Router (TypeScript, Tailwind CSS 4, shadcn/ui, Framer Motion, TanStack Query, Zustand, next-intl) | Industry leader in SSR/ISR, SEO, LCP performance, image optimization |
| Admin + Seller Console | Next.js 15 (TypeScript, TanStack Table, Recharts) | Rapid UI development, rich data-table ecosystems |
| Backend Core Engine | **C# 13 / .NET 10 ASP.NET Core Web API** (Visual Studio 2026 Clean Architecture) | Latest cutting-edge performance, rock-solid type safety, asynchronous high-concurrency |
| ORM & Database Access | **Entity Framework Core 10 (EF Core) + Npgsql + Dapper** (for ultra-fast read queries) | Strongly typed migrations, LINQ safety, raw SQL speed where needed |
| Database | PostgreSQL 16 | ACID transactions, JSONB for flexible attributes, row-level locking |
| Concurrency & Architecture | CQRS with MediatR, FluentValidation, Pipeline Behaviors (Logging, Validation, Transaction, Idempotency) | Clear boundaries, testable business logic |
| Background Workers & Queue | .NET BackgroundService / MassTransit / Hangfire with Redis & PostgreSQL Outbox | Guaranteed outbox processing, resilient retry policies (Polly) |
| Cache / Sessions / Cart | Redis 7 (via StackExchange.Redis) | Distributed locks (RedLock), fast sub-millisecond cart & sessions |
| Search | Meilisearch / OpenSearch (via Meilisearch .NET Client) | Typo-tolerant, instant faceted search |
| Storage | S3-compatible (MinIO locally, AWS S3 / Cloudflare R2 in prod) via AWS .NET SDK | Secure presigned URLs, private KYC bucket |
| Money Precision | C# native `decimal` (128-bit high precision) + Minor-unit integer storage in DB | Zero floating-point rounding bugs in financial ledgers |
| Payments | Stripe .NET SDK, SSLCommerz, bKash, Cash on Delivery via `IPaymentProvider` | Factory pattern, secure HMAC webhook validation |
| AI Integration | Google Gemini API via official SDK / HttpClient with typed DTOs | Streaming responses, tool/function calling |
| Observability | OpenTelemetry, Serilog (Structured JSON), Sentry, Prometheus & Grafana | Distributed tracing across requests |
| Testing | xUnit, FluentAssertions, Testcontainers (.NET), Moq, Playwright (E2E) | Production test coverage |

---

## 5. Repository Structure

```
shopverse/
├── backend/                             # C# .NET 8/9 Solution (Clean / Modular Architecture)
│   ├── ShopVerse.sln
│   ├── src/
│   │   ├── ShopVerse.Domain/           # Entities, Enums, Value Objects (Money), Domain Events
│   │   ├── ShopVerse.Application/      # CQRS Commands/Queries, MediatR Handlers, Validators
│   │   ├── ShopVerse.Infrastructure/   # EF Core DbContext, Repositories, Redis, S3, Email/SMS
│   │   ├── ShopVerse.Payment/          # Stripe, bKash, SSLCommerz implementations
│   │   ├── ShopVerse.Worker/           # Background jobs, Outbox relay, reconciliation
│   │   └── ShopVerse.Api/              # ASP.NET Core Web API, Controllers, Middleware, Swagger
│   └── tests/
│       ├── ShopVerse.UnitTests/        # Business logic, pricing, promotion, ledger tests
│       └── ShopVerse.IntegrationTests/ # Testcontainers with real Postgres & Redis
├── frontend/                            # Next.js Frontend Monorepo
│   ├── apps/
│   │   ├── web/                        # Customer storefront (Next.js 15)
│   │   └── console/                    # Admin + Seller center (Next.js 15)
│   └── packages/
│       ├── ui/                         # Design system tokens, shadcn/ui components
│       ├── config/                     # Tailwind preset, tsconfig, eslint
│       └── api-client/                 # Auto-generated TypeScript client from .NET Swagger
├── infra/
│   ├── docker/                         # docker-compose.yml (Postgres, Redis, Meilisearch, MinIO, Mailpit)
│   └── k6/                             # Load testing scripts
├── docs/                               # Brief, Design System, ADRs, Architecture docs
├── PROGRESS.md
└── README.md
```

---

## 6. Architecture Principles

1. **Modular monolith with bounded contexts**: identity, catalog, inventory, pricing,
   promotion, cart, checkout, order, payment, shipping, returns, seller, finance, review,
   notification, search, cms, analytics, ai, audit. A module may only use another module
   through its exported service interface or through domain events. Never import another
   module's repository or Prisma models directly.
2. **Domain events + transactional outbox**: write the event to an `outbox` table in the same
   DB transaction as the state change; a relay pushes it to BullMQ. Events include:
   `OrderPlaced`, `PaymentCaptured`, `PaymentFailed`, `OrderShipped`, `OrderDelivered`,
   `OrderCancelled`, `ReturnApproved`, `RefundIssued`, `ProductUpdated`, `StockLow`,
   `SellerApproved`, `PayoutCreated`, `ReviewSubmitted`.
3. **Ports and adapters** for every external dependency: Payment, Shipping, SMS, Email,
   Storage, Search, AI. Each has a **mock adapter** used in dev and tests.
4. **Typed contract**: the API publishes OpenAPI; `packages/api-client` is generated from it.
   Frontends never hand-write API types.
5. **Validated config**: all env vars parsed with Zod at boot; the app refuses to start if invalid.
6. **Idempotency**: every mutating checkout/payment/refund endpoint requires an
   `Idempotency-Key` header; responses are stored for 24h.
7. **Money**: integer minor units + ISO currency code, one `Money` utility, never floats.
   Rounding: banker's rounding at line level, documented in an ADR.
8. **Time**: store UTC, display in the user's timezone (default Asia/Dhaka).
9. **Audit**: every admin/seller write is recorded (actor, action, entity, before, after, IP).
10. **Feature flags**: DB-backed flags for risky features (AI assistant, new checkout, etc.).
11. **Read models**: search index in Meilisearch; hot product/category data cached in Redis
    with tag-based invalidation.
12. **ADRs**: every significant decision gets a short ADR in `docs/adr/`.

---

## 7. Domain Model (minimum fields — extend as needed)

- **User**: id, email, phone, passwordHash, name, avatar, locale, roles[], emailVerifiedAt,
  phoneVerifiedAt, twoFactorEnabled, status (active/suspended), lastLoginAt, timestamps, deletedAt.
- **Address**: userId, label (Home/Office), recipientName, phone, line1, line2, area, city,
  district, division, postalCode, country, geo (lat/lng), isDefault.
- **Seller**: userId, storeName, slug, logo, banner, description, status
  (pending/approved/rejected/suspended), kyc (NID, tradeLicense, TIN, documents[]),
  payoutMethod (bank/bKash), commissionOverride, plan, rating, responseRate, joinedAt.
- **Category**: parentId, name (i18n), slug, image, icon, path (materialized), depth,
  sortOrder, commissionRate, attributeSchema (which attributes/filters apply), seo fields.
- **Brand**: name, slug, logo, isVerified.
- **Product**: sellerId, categoryId, brandId, title (i18n), slug, shortDescription,
  description (rich text), highlights[], specs (JSONB key-value groups), status
  (draft/pending_review/active/rejected/archived), condition, warranty, returnPolicyDays,
  weight, dimensions, tags[], seo fields, ratingAvg, ratingCount, soldCount, timestamps.
- **ProductVariant**: productId, sku (unique), barcode, optionValues (e.g. color=Black,
  size=XL), price, compareAtPrice, costPrice, currency, weight, isDefault, status.
- **ProductMedia**: productId, variantId?, url, alt, type (image/video), position, width, height, blurDataURL.
- **InventoryItem**: variantId, warehouseId, onHand, reserved, lowStockThreshold.
  `available = onHand - reserved`.
- **StockReservation**: variantId, quantity, cartId/orderId, expiresAt.
- **Cart / CartItem**: cartId (user or guest token), variantId, quantity, priceSnapshot, savedForLater.
- **Promotion / Coupon**: see 8.2.
- **Order** (parent): orderNumber, userId?, guestEmail, guestPhone, currency, subtotal,
  discountTotal, shippingTotal, taxTotal, grandTotal, paymentStatus, shippingAddress
  (snapshot), billingAddress (snapshot), placedAt, source (web/app), notes.
- **SubOrder** (per seller): orderId, sellerId, status, items, subtotal, shippingFee,
  commissionAmount, sellerEarning, shipmentId.
- **OrderItem**: subOrderId, variantId, productTitleSnapshot, variantLabelSnapshot,
  imageSnapshot, sku, unitPrice, quantity, discount, tax, lineTotal.
- **OrderEvent**: orderId/subOrderId, type, fromStatus, toStatus, actor, note, createdAt (timeline).
- **Payment**: orderId, provider, method, amount, currency, status
  (initiated/authorized/captured/failed/refunded/partially_refunded), providerRef,
  rawPayload (encrypted), idempotencyKey.
- **Refund**: paymentId, amount, reason, status, providerRef.
- **Shipment**: subOrderId, courier, trackingNumber, labelUrl, status, events[], estimatedDeliveryAt.
- **ReturnRequest (RMA)**: orderItemId, reason, photos[], status, resolution (refund/replace), pickupShipmentId.
- **Review**: productId, userId, orderItemId (verified purchase), rating, title, body,
  photos[], status (pending/published/rejected), helpfulCount, sellerReply.
- **Question / Answer**: product Q&A.
- **Wishlist**, **RecentlyViewed**, **SavedSearch**.
- **LedgerAccount / LedgerEntry**: double-entry ledger for seller earnings, commissions, fees, payouts.
- **Payout**: sellerId, period, amount, status, reference.
- **Banner / HomepageSection / Page**: CMS content with schedule (startsAt/endsAt) and targeting.
- **Notification**: userId, type, channel, payload, readAt.
- **AuditLog**, **Outbox**, **IdempotencyRecord**, **FeatureFlag**, **Setting**.

---

## 8. Business Rules (implement exactly; unit-test every rule)

### 8.1 Pricing
- Displayed price = variant `price`. If `compareAtPrice > price`, show strikethrough and
  discount % (rounded down to whole %).
- Scheduled sale prices (startsAt/endsAt) override price inside the window.
- VAT: configurable per category ([default 5%]); prices are shown tax-inclusive in BD and
  tax-exclusive for international. Tax is calculated server-side only.
- The server recalculates every price, discount, shipping fee and tax at checkout.
  Client-sent totals are ignored.

### 8.2 Promotions engine
- Types: percentage off, fixed amount off, free shipping, buy X get Y, tiered spend
  ("spend 5000 save 500"), category/brand/seller-specific, first-order only.
- Conditions: min cart value, eligible products/categories/sellers, user segment
  (new/returning/VIP), payment method (e.g. 10% off with bKash), date window.
- Limits: total redemptions, per-user redemptions, max discount cap.
- Stacking: at most 1 coupon code + automatic promotions; each promotion has
  `priority` and `stackable`. The engine returns the best valid combination and an
  explanation for each rejected promotion ("Add 450 more to unlock").
- Seller-funded vs platform-funded discounts are recorded separately in the ledger.

### 8.3 Inventory
- Stock per variant per warehouse. When checkout starts, reserve stock with a
  15-minute TTL inside a DB transaction (`SELECT ... FOR UPDATE` or atomic conditional update).
- On payment success, convert the reservation into a deduction. On failure or expiry,
  release it (scheduled job every minute).
- Never oversell. Concurrent-checkout test: 50 parallel buyers for 10 units → exactly 10 orders.
- `StockLow` event when available ≤ threshold → notify seller.

### 8.4 Cart
- Guest cart is identified by a signed httpOnly cookie, stored in Redis for 30 days.
- On login, merge the guest cart into the user cart (sum quantities, cap by stock).
- On cart view and at checkout, revalidate price/stock and show clear notices:
  "Price dropped from X to Y", "Only 2 left — quantity updated".
- Max quantity per line: min(stock, 10) unless configured.

### 8.5 Checkout and orders
- One checkout = one parent `Order` + one `SubOrder` per seller.
- Order number format: `SV-YYMMDD-XXXXXX` (random base32, unique).
- Sub-order state machine (enforce transitions in one place, record every change in `OrderEvent`):
  `pending_payment → confirmed → processing → packed → shipped → out_for_delivery → delivered → completed`;
  side paths: `cancelled`, `delivery_failed → returned_to_seller`, `return_requested → return_approved → refunded`.
- Pending online-payment orders auto-cancel after 30 minutes.
- Customer may cancel before `packed`. After that, only a return.
- `completed` = delivered + return window passed → seller earning becomes payable.

### 8.6 Payments
- Providers: Stripe (cards, international), SSLCommerz (cards/net banking BD), bKash, COD.
- An order is confirmed ONLY by a verified server-side webhook or validation API call,
  never by the browser redirect.
- Webhooks are idempotent (dedupe by provider event id) and signature-verified.
- Nightly reconciliation job compares provider settlements with our payments and flags mismatches.
- Full and partial refunds, recorded in `Refund` and the ledger.
- COD rules: available only in serviceable areas, max order value [15,000 BDT],
  phone OTP verification required for COD orders, COD fee configurable.
- Never store card data (PCI-DSS SAQ-A: card entry happens on the provider's hosted fields).

### 8.7 Shipping
- Zones: [Inside Dhaka, Dhaka suburbs, Outside Dhaka, International].
- Rate = zone base + per-kg rate on chargeable weight (max of actual and volumetric).
- Free shipping threshold per zone (configurable) shown as a progress bar in the cart.
- Delivery estimate on PDP/cart/checkout based on seller location, zone and cut-off time
  ("Order within 3h 20m, get it by Thursday, 31 Oct").
- Courier adapters create the shipment, fetch the label, and receive tracking webhooks.

### 8.8 Returns and refunds
- Return window: per product `returnPolicyDays` (default 7 days after delivery).
- Reasons list + optional photos. Auto-approve for certain reasons/amounts, otherwise manual review.
- Refund to original method; COD refunds to bKash/bank or store credit.

### 8.9 Sellers and finance
- Onboarding: account → store info → KYC documents → payout method → agreement → admin review.
- Products from new sellers require admin approval for the first [10] listings.
- Commission = category rate (or seller override) × item subtotal after seller-funded discounts.
- Double-entry ledger: every order, commission, fee, refund and payout creates balanced entries.
- Earnings become payable when the sub-order is `completed`. Weekly payout cycle,
  minimum payout [500 BDT], payout statements downloadable as PDF/CSV.

### 8.10 Reviews and Q&A
- Only verified purchasers can review, once per order item, after delivery.
- Profanity filter + AI moderation flag; flagged reviews go to the moderation queue.
- Helpful votes; sort by most helpful/most recent/rating; seller can reply once.
- Rating aggregates updated via event and cached.

---

## 9. Roles and Permissions (RBAC, permission-based not role-checks in code)

| Permission group | Super Admin | Ops Admin | Catalog Manager | Support | Finance | Content Editor | Seller | Seller Staff |
|---|---|---|---|---|---|---|---|---|
| Users & roles | ✔ | – | – | view | – | – | – | – |
| Catalog (all) | ✔ | ✔ | ✔ | view | – | – | own | own |
| Orders | ✔ | ✔ | view | ✔ | view | – | own | own |
| Refunds | ✔ | ✔ | – | ≤ limit | ✔ | – | request | – |
| Sellers & KYC | ✔ | ✔ | – | view | view | – | – | – |
| Promotions | ✔ | ✔ | – | – | – | – | own | – |
| CMS | ✔ | ✔ | – | – | – | ✔ | own store page | – |
| Finance & payouts | ✔ | view | – | – | ✔ | – | own | – |
| Settings | ✔ | – | – | – | – | – | own store | – |
| Audit log | ✔ | view | – | – | – | – | – | – |

Permissions are strings like `orders.refund`, checked by a `@RequirePermission()` decorator;
the UI hides what the user cannot do.

---

## 10. Notification Matrix

| Event | Customer | Seller | Admin | Channels |
|---|---|---|---|---|
| Account created / OTP | ✔ | | | email, SMS |
| Order placed | ✔ | ✔ | | email, SMS, in-app |
| Payment failed | ✔ | | | email, in-app |
| Order shipped (with tracking) | ✔ | | | email, SMS, in-app |
| Out for delivery | ✔ | | | SMS |
| Delivered + review request (D+2) | ✔ | | | email, in-app |
| Cancellation / refund issued | ✔ | ✔ | | email, in-app |
| Abandoned cart (1h, 24h) | ✔ | | | email |
| Price drop on wishlist item | ✔ | | | email, in-app |
| Back in stock | ✔ | | | email |
| Low stock | | ✔ | | in-app, email |
| New seller application / product pending review | | | ✔ | in-app |
| Payout sent | | ✔ | | email |

All templates are bilingual, branded, mobile-friendly (React Email), with plain-text fallback
and an unsubscribe link for marketing mail.

---

## 11. Non-Functional Requirements

| Area | Requirement |
|---|---|
| Performance | Mobile Lighthouse ≥ 90 (home, category, product, cart). LCP < 2.5s, INP < 200ms, CLS < 0.1 on 4G mid-range Android. Initial route JS < 170 KB gzipped. API p95 < 200 ms for cached reads, < 500 ms for writes. |
| Scalability | 5,000 concurrent users and 200 orders/minute in a flash sale without overselling (proved by k6). Stateless API, horizontal scaling. |
| Availability | 99.9% target. Health/readiness endpoints. Graceful shutdown. |
| Security | OWASP ASVS Level 2 and OWASP Top 10. See section 11.1. |
| Accessibility | WCAG 2.2 AA. Axe checks in CI with zero serious/critical issues. |
| i18n | English + Bangla (all UI strings, emails, SMS), locale-aware numbers/dates/currency, Bangla digits optional. |
| Browser support | Last 2 versions of Chrome, Safari, Firefox, Edge; iOS Safari 15+, Android Chrome. |
| Backups | Automated daily DB backups + point-in-time recovery. RPO 1 hour, RTO 4 hours. Restore tested. |
| Observability | Every request has a trace id; logs are structured JSON; errors go to Sentry with release tags; business dashboards (orders/min, payment failure rate). |

### 11.1 Security requirements
- Passwords: Argon2id. Account lockout + progressive delay after failed logins.
- Tokens: access JWT 15 min; rotating refresh token in httpOnly, Secure, SameSite=Lax cookie; reuse detection revokes the session family.
- 2FA (TOTP) mandatory for all admin and finance roles.
- Rate limits: login/OTP 5/min per IP+identifier, general 100/min per IP, search 30/10s.
- Input validation on every endpoint; output encoding; rich text sanitized (DOMPurify allowlist).
- CSRF protection for cookie-authenticated mutations.
- Security headers: CSP (nonce-based), HSTS, X-Content-Type-Options, Referrer-Policy, Permissions-Policy.
- File uploads: type sniffing, size limits, image re-encoding, private bucket + signed URLs for KYC docs.
- Secrets only in env/secret manager; never in code, logs, or the client bundle.
- PII encrypted at rest (KYC numbers, payment payloads). Data export + account deletion flows.
- Dependency scanning (Dependabot / `pnpm audit`) and secret scanning in CI.

---

## 12. SEO Requirements
- Clean URLs: `/c/[category-slug]`, `/p/[product-slug]-[shortId]`, `/b/[brand-slug]`,
  `/s/[seller-slug]`, `/search?q=`. Locale prefix `/bn/...` for Bangla.
- Dynamic metadata for every page, OG/Twitter images (generated for products).
- JSON-LD: Organization, WebSite + SearchAction, BreadcrumbList, Product + Offer +
  AggregateRating + Review, ItemList on category pages, FAQPage on help pages.
- `sitemap.xml` (split index for products/categories/brands), `robots.txt`, canonical URLs,
  `hreflang`, faceted URLs `noindex` except whitelisted filters.
- Category pages have unique SEO intro copy and FAQ blocks.
- 301 redirects when slugs change (slug history table).

---

## 13. Analytics and Tracking
- GA4 ecommerce events with the exact standard names: `view_item_list`, `select_item`,
  `view_item`, `add_to_wishlist`, `add_to_cart`, `remove_from_cart`, `view_cart`,
  `begin_checkout`, `add_shipping_info`, `add_payment_info`, `purchase`, `refund`, `search`, `view_promotion`, `select_promotion`.
- Meta Pixel + Conversions API (server-side) for `purchase`.
- A typed `track()` wrapper; consent-aware (no marketing tags before consent).
- Internal analytics tables for admin dashboards (do not depend on GA for business reports).

---

## 14. Compliance and Legal
- Pages: Terms of Use, Privacy Policy, Cookie Policy, Return & Refund Policy, Shipping
  Policy, Seller Agreement, Prohibited Items.
- Cookie consent banner with categories (necessary / analytics / marketing), stored choice.
- Follow [Bangladesh Digital Commerce Operations Guidelines] — show seller identity,
  delivery timelines and refund timelines clearly; product pages show full price
  including VAT and delivery fee before payment.
- GDPR-style rights for international users: data export and account deletion.

---

## 15. AI Features (Google Gemini)

The Gemini API key is stored ONLY in `GEMINI_API_KEY` in the server environment. It must
never reach the browser, the git repo, logs, or error messages. All AI calls go through the
API's `AiProvider` with: timeouts, retries, per-user rate limits, response caching, cost
logging, and a graceful non-AI fallback.

| Feature | Where | Behaviour |
|---|---|---|
| Semantic + natural-language search | Storefront search | "black running shoes under 3000 for women" → parsed into query + filters; falls back to keyword search |
| AI review summary | PDP | "Customers say…" summary + pros/cons chips generated from reviews, refreshed when reviews change, cached |
| Shopping assistant | Floating chat on storefront | Answers product questions using catalog data only (tool calls to search/product APIs), compares products, adds to cart on confirmation. Never invents prices or stock. |
| Listing assistant | Seller center | Generates title, bullet highlights, description and SEO meta from a few inputs or a product photo; seller edits before publishing |
| Review / Q&A moderation | Background job | Flags spam, abuse, off-topic content |
| Admin insights | Admin dashboard | Weekly plain-language summary of sales trends and anomalies |

All AI output shown to users is labelled ("AI-generated summary"), and prompts are versioned
in code (`apps/api/src/modules/ai/prompts/`).

---

## 16. Engineering Standards and Definition of Done

- Conventional Commits; small, focused commits per feature.
- ESLint (strict) + Prettier + type-check pass with zero warnings.
- No `any`, no `// @ts-ignore` without a linked reason.
- Every module: `controller → service → repository`, DTOs validated, errors mapped to a
  standard error shape `{ error: { code, message, details, traceId } }`.
- Every user-facing string goes through i18n.
- Every page/component has loading, empty, error and success states.
- Tests: unit tests for all business rules in section 8; API integration tests with a real
  Postgres (Testcontainers); Playwright E2E for critical journeys; axe accessibility checks.

**A feature is DONE only when**:
1. It meets the acceptance criteria in its phase prompt.
2. Lint, type-check, unit, integration and E2E tests pass.
3. It was visually verified in the browser at 360px, 768px, 1280px and 1920px, in light and dark mode, in English and Bangla.
4. It has no console errors or warnings.
5. `PROGRESS.md` and relevant docs are updated.

---

## 17. Agent Working Rules

1. Read `docs/01_PROJECT_BRIEF.md` and `docs/02_DESIGN_SYSTEM.md` at the start of every phase.
2. Before coding, post a short plan (files to create/change, risks). Wait for approval if the plan changes architecture.
3. Build in vertical slices (DB → API → UI → tests) so each slice is demoable.
4. Never use placeholder content ("Lorem ipsum", "Product 1", grey boxes) in the final UI.
5. Never hard-code secrets, prices, or business config — use settings/env.
6. Never silently skip a requirement. If something cannot be done, say so and propose an alternative.
7. After each phase: run all checks, open the app in the browser, take screenshots of key screens, and report what was done, what is left, and how I can verify it.
8. If a requirement is ambiguous, ask a concise question with a recommended default.
