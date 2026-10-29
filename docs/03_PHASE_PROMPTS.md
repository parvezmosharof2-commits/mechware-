# PHASE PROMPTS — Paste one phase at a time

> Before Phase 0, copy `01_PROJECT_BRIEF.md` and `02_DESIGN_SYSTEM.md` into the project's `docs/` folder.
> Paste each phase prompt below into the agent only after the previous phase has passed review
> (`04_REVIEW_PROMPTS.md`). Every prompt begins by pointing the agent to the docs, so the agent
> always works against the full specification.

---

## Phase 0 — Kickoff, Architecture and Plan (no product code)

```text
PHASE 0 — KICKOFF

Read docs/01_PROJECT_BRIEF.md and docs/02_DESIGN_SYSTEM.md completely. They are the single
source of truth for this project. Do not write product code in this phase.

Produce these documents in docs/:

1. ARCHITECTURE.md
   - System context diagram and container diagram (mermaid).
   - Bounded contexts/modules with responsibilities, owned tables, public service
     interfaces, and the domain events each publishes/consumes.
   - Request flow diagrams (mermaid sequence) for: guest add-to-cart, checkout with bKash,
     payment webhook, COD order with OTP, order shipped via courier webhook, return + refund,
     weekly seller payout.
   - Caching strategy (what is cached, keys, TTL, invalidation tags).
   - Rendering strategy per storefront route (RSC/SSR/ISR/CSR, revalidate times).
   - Deployment topology for production.

2. DATA_MODEL.md
   - Full ER diagram (mermaid) and every table with columns, types, constraints, indexes
     and the reason for each index. Include outbox, idempotency, audit and ledger tables.

3. API_CONTRACT.md
   - Every endpoint grouped by module: method, path, auth/permission, request and response
     shape, error codes, pagination style, idempotency requirement, rate limit.

4. ADRs in docs/adr/: modular monolith, money handling and rounding, order splitting per
   seller, stock reservation strategy, search provider abstraction, auth token strategy,
   AI provider and key handling.

5. PROGRESS.md
   - The full task breakdown for phases 1–14 as checkboxes, each task small enough
     to finish and verify in one sitting.

6. A list of open questions for me, each with your recommended default.

Acceptance criteria:
- Every requirement in sections 3, 7, 8, 9, 10 and 15 of the brief is mapped to at least
  one module, table, endpoint or task. Add a traceability table (requirement → where handled).
- No contradictions with the brief. If you disagree with something in the brief, explain why in an ADR instead of silently changing it.

Stop after producing the documents and wait for my approval.
```

---

## Phase 1 — Enterprise Foundation: C# .NET API + Next.js Monorepo, Docker, CI

```text
PHASE 1 — FOUNDATION

Read docs/01_PROJECT_BRIEF.md (sections 4, 5, 6, 16), docs/ARCHITECTURE.md and PROGRESS.md.

Build:
1. Repository structure exactly as in brief section 5:
   - backend/: C# 13 / .NET 10 Solution (ShopVerse.sln):
     - ShopVerse.Domain (Entities, Value Objects, Domain Events, Enums)
     - ShopVerse.Application (CQRS Commands/Queries, MediatR, FluentValidation)
     - ShopVerse.Infrastructure (EF Core DbContext, PostgreSQL, Redis, MinIO, Mailpit)
     - ShopVerse.Payment (Payment gateway interfaces & providers)
     - ShopVerse.Worker (BackgroundService / Hangfire outbox processor)
     - ShopVerse.Api (ASP.NET Core Web API, Controllers, Middleware, Swagger/OpenAPI)
   - frontend/: Next.js 15 Monorepo (apps/web, apps/console, packages/ui, packages/config, packages/api-client)
2. C# Backend Foundation:
   - Strongly typed appsettings.json with IOptions<T> validation at startup.
   - Serilog structured JSON logging with CorrelationId and RequestId.
   - Global Exception Handling Middleware returning RFC 7807 ProblemDetails { error: { code, message, details, traceId } }.
   - Health checks (/health/live, /health/ready) for DB, Redis, and MinIO.
   - OpenAPI / Swagger documentation with XML comments and JWT bearer auth at /swagger.
   - Rate limiting middleware, CORS, security headers (HSTS, CSP).
   - C# Domain Value Object `Money` using native 128-bit `decimal` with minor-unit conversions, banker's rounding, multi-currency support, and 100% unit test coverage.
3. frontend/ Monorepo Foundation:
   - Next.js 15 skeletons for apps/web and apps/console with next-intl (en, bn), theme provider (light/dark), design tokens, and Sentry wiring.
   - Typed API client auto-generation from .NET OpenAPI JSON (orval / openapi-typescript).
4. infra/docker/docker-compose.yml:
   - postgres 16, redis 7, meilisearch, minio (with bucket bootstrap), mailpit, imgproxy. All with healthchecks.
5. GitHub Actions CI:
   - .NET build, test, and formatting checks (`dotnet test`, `dotnet format`).
   - Frontend lint, type-check, and build (`pnpm lint`, `pnpm build`).
   - Integration tests with Testcontainers.NET.
6. .env.example, appsettings.Development.json, and one-command README setup.

Acceptance criteria:
- `dotnet test` and frontend CI checks pass with zero warnings.
- Starting the API without required DB/Redis config fails immediately with a clear error.
- Money value object has 100% test coverage including rounding edge cases.

Do NOT build product features yet. Update PROGRESS.md and report.
```

---

## Phase 2 — Design System and UI Library

```text
PHASE 2 — DESIGN SYSTEM

Read docs/02_DESIGN_SYSTEM.md completely. This phase decides whether the product looks like
a USD 100,000 product, so be meticulous.

Build in packages/ui:
1. Tokens (section 2) as TypeScript source → CSS variables (light + dark) → Tailwind preset.
   No raw colors or arbitrary values may be used elsewhere (add an ESLint rule or
   Tailwind config restriction to enforce it).
2. Every component listed in section 3 with all variants, sizes and states, built on
   Radix/shadcn primitives and restyled to the tokens. Include: Button, IconButton, Input,
   Textarea, Select, Combobox, PhoneInput, Checkbox, Radio, RadioCard, Switch,
   QuantityStepper, Price, Rating, Badge, Card, Tabs, Accordion, Tooltip, Popover,
   DropdownMenu, Dialog, Sheet/Drawer, Toast, AlertBanner, Breadcrumb, Pagination,
   Skeleton, Avatar, Progress, Stepper, Carousel (Embla), CountdownTimer, ImageGallery
   with zoom + lightbox, DataTable (TanStack), EmptyState, ErrorState, FileUploader,
   CommandPalette, ProductCard (all 3 sizes + skeleton, spec section 3.1).
3. Motion presets (section 2.5) as Framer Motion variants; reduced-motion respected.
4. An illustration set for empty/error states (consistent SVG line style).
5. A /design-system route in apps/web that documents every token and every component in
   every state, light and dark, with usage notes — it should look like a professional
   design system site (think Vercel Geist or Radix Themes docs).
6. Unit tests (Testing Library) for interactive components and axe tests for every component.

Acceptance criteria:
- Every component is keyboard accessible and passes axe with zero violations.
- All text/background pairs pass WCAG AA (write an automated contrast test over the tokens).
- Visual check at 360px and 1440px in both themes; screenshots attached in the report.
- ProductCard renders identically sized skeleton → loaded state (no layout shift).

Do NOT use any placeholder lorem text on the showcase page; write realistic sample content.
```

---

## Phase 3 — Identity: Auth, Users, RBAC, Audit

```text
PHASE 3 — IDENTITY AND ACCESS

Read brief sections 7 (User, Address), 9, 11.1 and docs/API_CONTRACT.md (auth, users).

Backend (C# ShopVerse.Infrastructure/Identity, ShopVerse.Api):
1. EF Core entities + Fluent API configurations + Migrations for User, Address, Role, Permission, UserRole, RefreshToken (token family tracking), OtpCode, AuditLog.
2. Authentication pipeline:
   - Email + Password login with Argon2id / ASP.NET Core PasswordHasher.
   - Phone OTP verification (via ISmsProvider adapter).
   - Google OAuth / OpenID Connect token validation.
   - Secure email verification & password reset flows.
3. Token Architecture:
   - 15-minute access JWT signed with asymmetric or high-entropy key.
   - Rotating refresh token stored in an httpOnly, Secure, SameSite=Lax cookie.
   - Refresh token reuse detection (revokes entire token family upon duplicate presentation).
   - Session revocation endpoints (/api/auth/sessions/revoke).
4. TOTP 2FA (QR generation, backup recovery codes) mandatory for admin & finance roles.
5. Brute-force protection: progressive login delays & IP/account lockout.
6. RBAC in ASP.NET Core:
   - Custom `[RequirePermission(Permissions.OrdersRefund)]` authorization policy attribute.
   - Custom AuthorizationHandler checking user permission claims.
   - Seller resource isolation handler (ensures sellers only access their own resources).
7. Audit Logging:
   - EF Core `SaveChangesAsync` interceptor automatically capturing ChangedEntities, OldValues, NewValues, ActorId, IP, and Timestamp into the AuditLog table.
8. Address book CRUD with pre-seeded Bangladesh division/district/upazila data.
9. GDPR export (JSON) & anonymization service.

Frontend:
- apps/web: sign in, sign up, OTP, forgot/reset password, Google button — as modal and as
  full pages; header account menu; protected route handling with return-to URL.
- apps/console: sign in with mandatory 2FA for staff, role-aware sidebar.

Tests:
- Unit: password policy, token rotation and reuse detection, permission checks.
- Integration: full auth flows against real Postgres + Redis.
- E2E: sign up → verify → sign in → sign out; refresh after access-token expiry.

Acceptance criteria:
- No token is ever readable by JavaScript except the short-lived access token in memory.
- A seller cannot read or modify another seller's resources (tested).
- Auth screens match the design system and work in Bangla.
```

---

## Phase 4 — Catalog, Media, Inventory, Seed Data

```text
PHASE 4 — CATALOG

Read brief sections 7 (Category → InventoryItem), 8.1, 8.3 and design-system section 2.6.

Backend (C# ShopVerse.Domain, Application, Infrastructure):
1. EF Core Models & Mapping: Category (hierarchical tree with materialized path, JSONB attribute schemas), Brand, Product, ProductVariant, ProductMedia, Attribute definitions, Warehouse, InventoryItem, StockReservation, SlugHistory.
2. Product lifecycle state management: Draft -> PendingReview -> Active/Rejected -> Archived.
3. High-concurrency Inventory Service:
   - `Available = OnHand - Reserved`.
   - Concurrency control: PostgreSQL row-level locks (`SELECT ... FOR UPDATE` via raw SQL or EF Core) combined with Redis distributed locks (RedLock.net) for flash-sale peak loads.
   - Background worker for releasing expired stock reservations (15 min TTL).
   - Domain Event `StockLowDomainEvent` dispatched via Outbox pattern when stock drops below threshold.
4. Deterministic Pricing Engine: Base price, compare-at discounts, scheduled sale windows, VAT calculations (pure C# domain service with 128-bit `decimal` precision).
5. Storefront Read Models: High-speed cached read endpoints (Redis via StackExchange.Redis + Dapper / compiled EF Core queries).
6. Outbox Pattern: Save domain events (`ProductUpdatedEvent`) into `OutboxMessages` within the same EF Core transaction; background relay pushes to Meilisearch sync queue.

Seed data (critical for the premium look):
- 12 categories with 3 levels of sub-categories and category images.
- 40 brands with logos.
- 25 sellers with logos, banners and ratings.
- 400+ products with realistic titles, highlights, long descriptions, grouped specs,
  2–6 high-quality images each (Unsplash or similar license-free sources, correct
  product type), variants where natural (fashion sizes/colors, phone storage/colors),
  realistic BDT prices and discounts, stock levels (some low, some out of stock), ratings
  and 3,000+ realistic reviews (varied length and tone, some with photos), Q&A.
- Seed is deterministic (fixed random seed) and runs in under 2 minutes.

Tests:
- Concurrency test: 50 parallel reservations for 10 units → exactly 10 succeed.
- Unit tests for pricing (sale windows, VAT, discount %), variant generation, slug history redirects.

Acceptance criteria:
- No product in the seed looks fake; spot-check 20 random products in the report.
- Product-by-slug endpoint p95 < 50 ms when cached.
```

---

## Phase 5 — Search and Discovery

```text
PHASE 5 — SEARCH

Read brief section 6 (principles 3 and 11), section 15 and design-system sections 4.1 (autocomplete) and 4.3.

Build:
1. SearchProvider interface + Meilisearch adapter. Index documents per product with:
   title (en/bn), brand, category path, attributes, price, discount, rating, soldCount,
   stock flag, seller, createdAt, popularity score.
2. Ranking: typo tolerance, then exact/prefix match, then popularity (soldCount, rating,
   CTR) and in-stock boost; out-of-stock items sink.
3. Facets: category, brand, price ranges, rating, dynamic attributes per category
   attributeSchema, availability, seller, discount buckets — with counts.
4. Synonyms and Bangla support: e.g. "mobile = phone = মোবাইল", "tshirt = t-shirt = টি-শার্ট";
   admin-editable synonym list; transliteration of common Banglish queries.
5. Autocomplete endpoint (< 50 ms p95): suggested queries, products (4) with thumbnail and
   price, categories, brands; recent and trending searches.
6. Zero-result handling: "Did you mean", relaxed query fallback, and logging of
   zero-result queries for the admin report.
7. Search analytics: log query, results count, clicked position (for CTR ranking).
8. Full reindex command + incremental sync from ProductUpdated events; index settings in code.
9. Recommendations v1 (rule-based, cached): frequently bought together (co-purchase),
   customers also viewed (co-view), similar items (same category + attributes),
   trending (last 7 days), personalized home feed from browsing history.

Frontend: search bar with autocomplete (keyboard navigation, recent searches stored
locally, highlighting), search results page using the PLP layout (built fully in Phase 7).

Acceptance criteria:
- "iphon 15", "samsng", "running shoe women", "মোবাইল" return relevant results.
- Zero-result rate on a test list of 100 realistic queries < 5%; include the test list and results in the report.
```

---

## Phase 6 — Storefront Shell and Homepage

```text
PHASE 6 — STOREFRONT SHELL AND HOMEPAGE

Read docs/02_DESIGN_SYSTEM.md sections 4.1, 4.2, 5, 6 and brief sections 11, 12, 13.

Build in apps/web:
1. Root layout: header (sticky, condenses on scroll), mega menu, mobile drawer, mobile
   bottom navigation, footer, cookie consent banner, toast region, skip link.
2. Delivery location selector (stored in cookie; drives delivery estimates).
3. Language switch (en/bn) and currency switch (BDT/USD) with correct formatting.
4. Homepage with all 12 sections in the exact order of section 4.2, driven by CMS
   endpoints (Banner, HomepageSection with schedule and targeting). Build the CMS API now;
   the admin UI comes in Phase 11.
5. Deals of the Day with server-synced countdown and claimed progress bars.
6. Personalization: logged-in users see "Recommended for you"; guests see "Trending now";
   "Continue shopping" only when history exists.
7. Rendering: homepage ISR (revalidate 120s) + on-demand revalidation when CMS changes;
   personalized sections streamed with Suspense and skeletons so they never block LCP.
8. Analytics wrapper `track()` with GA4 events view_promotion, select_promotion,
   view_item_list, select_item; consent-aware.
9. SEO: Organization + WebSite SearchAction JSON-LD, metadata, OG image.

Acceptance criteria:
- Mobile Lighthouse on homepage: Performance ≥ 90, Accessibility ≥ 95, SEO 100.
- Hero image is the LCP element and loads with priority; CLS < 0.05.
- The homepage must not look like a template: compare side-by-side with Amazon and the
  Apple Store, list 5 things that look less premium than those references, then fix them.
- Screenshots at 360, 768, 1280, 1920 px, light and dark, EN and BN.
```

---

## Phase 7 — Category Page and Product Detail Page

```text
PHASE 7 — PLP AND PDP

Read docs/02_DESIGN_SYSTEM.md sections 3.1, 4.3, 4.4, 5 and brief sections 8.1, 8.7, 12.

Build:
1. Category and search results pages exactly per section 4.3: filters with counts (desktop
   sidebar, mobile bottom sheet), URL-synced state, active chips, sort, grid/list toggle,
   pagination with crawlable links, zero-result state, SEO intro + FAQ, ItemList and
   BreadcrumbList JSON-LD, noindex rules for faceted URLs.
2. Product detail page exactly per section 4.4, including: gallery with zoom/lightbox/video,
   variant selection that updates URL (?variant=sku), images, price and stock without full
   reload; out-of-stock "Notify me"; size guide; offers and coupon chips; delivery
   estimate by location with cut-off countdown; buy box; seller card; specs; frequently
   bought together bundle; reviews with histogram filters, photo gallery, helpful votes,
   seller replies; Q&A with search and ask-a-question; similar/compare table; recently viewed;
   mobile sticky buy bar.
3. Write-a-review flow (verified purchasers only) with star input, photos and guidelines.
4. Wishlist (optimistic, works for guests via local storage then syncs on login).
5. Recently viewed tracking (local + server for logged-in users).
6. Product JSON-LD (Product, Offer with availability and priceValidUntil, AggregateRating,
   Review), dynamic OG image with product photo, price and rating.
7. ISR for PDP (revalidate 300s) + on-demand revalidation on ProductUpdated; stock and
   price fetched fresh client-side so cached HTML never shows stale stock.
8. GA4 events: view_item, select_item, add_to_wishlist, search, view_item_list.

Acceptance criteria:
- PDP mobile Lighthouse Performance ≥ 90; switching variants has no layout shift.
- Google Rich Results Test passes for a sample PDP (validate JSON-LD structure in a test).
- Filters with 5 facets selected still respond in < 300 ms.
```

---

## Phase 8 — Cart, Promotions, Checkout, Payments

```text
PHASE 8 — CART, PROMOTIONS, CHECKOUT AND PAYMENTS (highest-risk phase — prioritise correctness)

Read brief section 6 (principles 6 and 7), sections 8.2–8.7, 11.1 and design-system sections 4.5, 4.6.

Backend (C# ShopVerse.Application & ShopVerse.Payment):
1. Cart Service: Redis-backed cart with signed guest token, atomic merge on user login, stock and price drift revalidation returning structured notifications.
2. Promotions Engine: Pure, deterministic C# domain engine using MediatR. Computes coupon discounts, tiered volume pricing, category/seller promotions, and line-item allocations with zero rounding loss using banker's rounding on C# `decimal`.
3. Checkout Orchestrator:
   - MediatR Command `PlaceOrderCommand` executing inside an explicit EF Core Database Transaction.
   - Idempotency Pipeline Behavior: Checks `Idempotency-Key` header against Redis/DB before execution.
   - Atomic action: Verify prices -> Reserve inventory -> Create Order & Seller SubOrders with immutable price/name snapshots -> Compute platform commission -> Insert OutboxMessage `OrderPlacedEvent` -> Commit transaction.
4. C# `IPaymentProvider` implementations:
   - Stripe (via Stripe.net)
   - SSLCommerz (hosted & seamless payment)
   - bKash (tokenized checkout API with grant token and execute payment)
   - Cash on Delivery (with mandatory phone OTP verification and order cap)
   - MockPaymentProvider for unit and integration testing.
5. Webhook Security & Verification:
   - Asynchronous webhook controllers verifying cryptographic HMAC-SHA256 signatures.
   - Provider Event Id deduplication (idempotent webhook processing).
   - Order transitions governed strictly by the C# Order State Machine.
6. Financial Reconciliation & Jobs:
   - Nightly Hangfire / BackgroundService job reconciling gateway settlement logs against internal database records.
   - Background generation of PDF invoices (via QuestPDF in C# - lightning fast vector PDF rendering) and transactional emails/SMS dispatched via Outbox pattern.

Frontend:
- Mini-cart drawer, cart page, coupon UX, free-shipping progress bar, save for later.
- Distraction-free checkout per section 4.6 with correct autocomplete attributes, inline
  validation, error summary, saved addresses, cascading BD address selects, payment radio
  cards with logos, processing state, double-submit prevention, redirect/return handling
  for each provider, failure page with retry and alternative methods.
- Order confirmation page with guest "create account" prompt.
- GA4: add_to_cart, remove_from_cart, view_cart, begin_checkout, add_shipping_info,
  add_payment_info, purchase (sent once, server-confirmed).

Tests (mandatory):
- Unit: every promotion type, stacking, caps, per-user limits, allocation rounding; shipping rates; tax.
- Integration: place order with each provider using the mock; duplicate webhook; webhook
  before redirect; redirect without webhook (must NOT confirm); payment timeout; partial refund.
- Concurrency: 100 parallel checkouts for 10 units → exactly 10 orders, no negative stock.
- E2E (Playwright): guest checkout with COD; logged-in checkout with card (Stripe test mode)
  including coupon; multi-seller cart producing 2 sub-orders.

Acceptance criteria:
- It is impossible to change any price/total from the client (prove with a test that tampers the request).
- Guest checkout can be completed in under 90 seconds by a first-time user.
```

---

## Phase 9 — Orders, Shipping, Returns, Notifications, Customer Account

```text
PHASE 9 — POST-PURCHASE

Read brief sections 8.5, 8.7, 8.8, 10 and design-system section 4.7.

Backend:
1. Order state machine module (single source of transitions, guards, side effects, OrderEvent timeline).
2. ShippingProvider adapters (mock + [Pathao/RedX/Steadfast] sandbox): create shipment,
   label PDF, pickup request, tracking webhook → status updates → notifications.
3. Cancellation rules, return requests (RMA) with reasons/photos, auto-approval rules,
   pickup, inspection, refund through the original provider or store credit.
4. Notification service: templates (React Email, bilingual), channel routing per section 10,
   user preferences, quiet hours for SMS, in-app notification center API, retries with backoff,
   delivery logs. Scheduled jobs: abandoned cart (1h, 24h), review request (delivered + 2 days),
   price drop and back-in-stock alerts.

Frontend (apps/web account area, section 4.7):
- Overview, orders list with filters, order detail with visual timeline and live tracking,
  invoice download, buy again, cancel, return wizard, write review from order.
- Addresses, payment methods (tokenised cards from provider), wishlist, reviews,
  notifications center, profile & security (password, 2FA, sessions, data export, delete account), preferences.
- Public "Track your order" page (order number + phone).

Tests:
- State machine: every allowed and disallowed transition.
- E2E: order → shipped (simulated webhook) → delivered → return → refund, verifying emails in Mailpit.

Acceptance criteria:
- Every status change creates an OrderEvent and sends exactly the notifications in section 10 (no duplicates on retries).
- All email templates render correctly in light/dark email clients (screenshots in report).
```

---

## Phase 10 — Seller Center and Finance Ledger

```text
PHASE 10 — SELLER CENTER AND FINANCE

Read brief sections 8.9, 9 and design-system section 4.9.

Backend:
1. Seller onboarding wizard API + KYC documents in a private bucket (signed URLs), admin review flow.
2. Seller-scoped APIs: products (with approval rules), bulk CSV/XLSX import with row-level
   validation report, inventory, orders/fulfilment (accept, pack, print label, hand over),
   returns, store page settings, seller staff accounts with limited permissions, seller promotions.
3. Double-entry ledger (C# Domain Service with decimal precision):
   - Chart of Accounts: SellerPayable, PlatformCommissionRevenue, PaymentGatewayClearing, CustomerRefunds, ShippingFees.
   - Immutable LedgerEntry rows: Debit and Credit columns must sum to zero within every transaction scope (enforced by EF Core SaveChanges interceptor).
4. Payout cycle job (weekly background task):
   - Computes payable balance = Completed SubOrders − Refunds − Platform Commission − Adjustments.
   - Payout statements generated as vector PDFs using QuestPDF / CSV exports.
5. Seller analytics: sales, orders, conversion, top products, traffic sources, ratings.

Frontend (apps/console, seller area):
- Onboarding wizard with progress, document upload, status screen.
- Dashboard, products table + product editor (section 4.9) with AI listing assistant
  button (wired in Phase 12), bulk upload with error report, inventory, orders with
  fulfilment actions and label printing, returns, payouts and statements, analytics,
  store page customisation with live preview, staff management, settings.

Acceptance criteria:
- A brand-new seller can sign up and publish a first product in under 15 minutes (time it in an E2E test with realistic steps).
- Ledger invariants hold for 1,000 randomly generated order/refund scenarios (property-based test).
```

---

## Phase 11 — Admin Console

```text
PHASE 11 — ADMIN CONSOLE

Read brief sections 9, 10, 13 and design-system section 4.9. The admin must feel like
Stripe/Shopify admin: fast, dense but clean, keyboard-friendly.

Build (apps/console, admin area):
1. Dashboard: KPI cards (GMV, net revenue, orders, AOV, conversion, new customers) with
   period comparison and sparklines, revenue chart, orders by status, top products/sellers/
   categories, low stock, payment failure rate, zero-result searches, date-range picker.
2. Catalog: products (approve/reject with reason, bulk actions), categories tree editor
   (drag-and-drop, attribute schema editor, commission rate), brands, attributes, synonyms.
3. Orders: advanced filters, order detail with timeline, per-seller sub-orders, refunds
   (with permission limits), cancel, resend notifications, internal notes, fraud flags.
4. Customers: profiles, orders, addresses, notes, suspend, impersonate (read-only, audit logged).
5. Sellers: applications + KYC review, sellers list, commission overrides, suspend, payouts approval.
6. Promotions: create/edit all promotion types with a live "preview on sample cart" panel; usage stats.
7. CMS: homepage sections and banners with scheduling, targeting, drag-and-drop ordering and
   live preview; static pages (rich text); navigation/mega menu editor; help center articles.
8. Reviews & Q&A moderation queue (with AI flags).
9. Finance: ledger explorer, payouts, reconciliation mismatches, exports.
10. Reports: sales, products, categories, sellers, customers, search terms, coupons — CSV export.
11. Settings: store info, tax, shipping zones and rates, payment methods, COD rules,
    notification templates, feature flags, roles & permissions editor, staff users.
12. Audit log viewer with filters and diff view. Global ⌘K command palette.

Acceptance criteria:
- Every page has loading, empty and error states; every table supports server-side
  pagination, sorting, filtering, saved views and CSV export.
- Permissions are enforced on both API and UI (tested for each role in brief section 9).
```

---

## Phase 12 — AI Features (Google Gemini)

```text
PHASE 12 — AI FEATURES

Read brief section 15 carefully. The Gemini API key exists ONLY as GEMINI_API_KEY in the
API server environment. It must never be sent to the browser, committed, or logged.
Add a test/CI check that fails if the key pattern appears in any client bundle.

Build (apps/api/modules/ai):
1. AiProvider interface + Gemini adapter + mock adapter. Timeouts (8s), retries with
   backoff, per-user and global rate limits, Redis response cache, token/cost logging,
   feature flags per AI feature, graceful fallback when AI is unavailable.
2. Versioned prompt templates in code with structured JSON outputs validated by Zod.
3. Natural-language search: parse the query into { keywords, category, filters, sort };
   validate against real facets; fall back to keyword search on any failure.
4. Review summary: per product, from published reviews; summary + pros/cons with mention
   counts; regenerated when 10+ new reviews arrive; labelled "AI-generated from customer reviews".
5. Shopping assistant (storefront chat widget): uses tool/function calling to search
   products, get product details, compare products and add to cart (only after the user
   confirms). It must answer only from tool results, never invent prices, stock or
   policies, and hand off to support for order issues. Streaming responses, conversation
   memory per session, suggested prompts, product cards inside the chat.
6. Seller listing assistant: from a few fields or a product photo, generate title,
   highlights, description and SEO meta in EN and BN; the seller reviews before saving.
7. Moderation: classify reviews/Q&A for spam/abuse/off-topic; flagged items go to the queue.
8. Admin weekly insights summary from aggregated metrics (no PII sent to the model).

Security and privacy:
- Strip PII before sending data to the model; protect against prompt injection from
  review/product text (treat as data, delimit clearly, never execute instructions found in it).

Acceptance criteria:
- With AI disabled or the key missing, every feature degrades gracefully and the site still works.
- An evaluation set of 50 natural-language queries with expected filters scores ≥ 90% correct; include results.
```

---

## Phase 13 — Performance, SEO, Accessibility and Security Hardening

```text
PHASE 13 — HARDENING

Read brief sections 11, 12, 14 and design-system section 7.

Do all of the following and report before/after numbers:
1. Performance: Lighthouse (mobile) on home, category, PDP, cart, checkout. Bundle analysis;
   remove/replace heavy dependencies; dynamic import below-the-fold widgets (chat, reviews
   gallery); image sizes and priorities; font subsetting; prefetch strategy; Redis cache hit
   rate; DB query review with EXPLAIN ANALYZE for the 20 most frequent queries; add missing indexes; fix N+1s.
2. SEO: sitemap index, robots, canonicals, hreflang, slug redirects, structured data on all
   page types, metadata audit, Core Web Vitals.
3. Accessibility: axe on every route in CI (zero serious/critical), manual keyboard-only
   run of the complete purchase journey, screen-reader labels review.
4. Security: OWASP ZAP baseline scan, CSP with nonces, header audit, rate-limit verification,
   dependency audit, secrets scan, authz tests for every endpoint (automated matrix),
   file-upload attacks, IDOR checks, webhook spoofing tests.
5. Resilience: graceful degradation when Meilisearch, Redis, email/SMS or AI are down.
6. Legal pages, cookie consent behaviour, data export/delete verified.

Acceptance criteria: all targets in brief section 11 are met; any exception is documented with a justification and a plan.
```

---

## Phase 14 — Testing, Load Test, Production Readiness and Launch

```text
PHASE 14 — LAUNCH READINESS

1. Complete the Playwright suite for the critical journeys: browse → search → filter → PDP →
   add to cart → guest checkout (COD) / card checkout / bKash (mock); account flows; return
   flow; seller onboarding → listing → fulfil; admin refund. Run on Chromium, WebKit and a mobile viewport.
2. Visual regression snapshots for key pages (light/dark, EN/BN).
3. k6 load tests (infra/k6): browsing mix at 5,000 virtual users; flash sale of 1 product
   with 100 units and 2,000 buyers → exactly 100 orders, p95 latencies within targets; report.
4. Production Dockerfiles (multi-stage, non-root, small images), docker-compose.prod for a
   single VM option, and a documented AWS option (ECS/RDS/ElastiCache/S3/CloudFront).
5. CI/CD: staging auto-deploy on main, production deploy on tag with manual approval,
   database migration step, rollback procedure, preview deployments for PRs.
6. Observability: Sentry releases + source maps, Grafana dashboards (API latency, error rate,
   orders/min, payment failure rate, queue depth), alerts to email/Slack, uptime checks.
7. Backups: automated DB backups + PITR, restore drill documented.
8. Documentation: README, ARCHITECTURE, API docs link, RUNBOOK (incidents, payment provider
   outage, courier outage, rollback), ADMIN_GUIDE and SELLER_GUIDE with screenshots,
   environment variable reference, handover checklist.
9. Final launch checklist with every item ticked or explained.

Acceptance criteria: a new developer can deploy to a fresh staging environment by following the docs alone.
```
