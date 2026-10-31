# MECHWARE Platform Implementation Progress

> **Target Standard:** USD 100,000+ Enterprise Industrial Commerce & Automation Platform
> **Architecture:** C# 13 / .NET 10 Clean Architecture + Next.js 15 Monorepo + PostgreSQL 16 + Redis 7 + Meilisearch

---

## 📊 Phase Overview

- [x] **Phase 0: Kickoff, Architecture & Master Blueprint** (Completed & Signed Off)
- [x] **Phase 1: Enterprise Foundation (.NET 10 Solution, Next.js Monorepo, Docker, CI)** (Completed & Verified)
- [x] **Phase 2: Cybernetic Design System & Component Library (`packages/ui`)** (Completed & Verified)
- [x] **Phase 3: Identity, Authentication, Granular RBAC & Audit Interceptors** (Completed & R1 Signed Off)
- [x] **Phase 4: Product Catalog, Concurrency Inventory & 400+ Industrial Seed Data**
- [ ] **Phase 5: Sub-50ms Typo-Tolerant Search & Discovery (Meilisearch + Bangla)**
- [ ] **Phase 6: Storefront Foundation, Mega Menu & Dynamic Homepage Sections**
- [ ] **Phase 7: Industrial Product Experience (PLP & PDP with CAD/Datasheets)**
- [ ] **Phase 8: Cart, Promotions Engine, Atomic Checkout & Multi-Payment Gateways**
- [ ] **Phase 9: Order Fulfillment, Courier Tracking, Returns & Vector PDF Invoices**
- [ ] **Phase 10: Multi-Vendor Seller Center & Double-Entry Financial Ledger**
- [ ] **Phase 11: Operations & Executive Admin Console (Stripe/Shopify Grade)**
- [ ] **Phase 12: Google Gemini AI Intelligence (Search, Review Summaries, Assistant)**
- [ ] **Phase 13: Hardening, OWASP ASVS Security, Lighthouse 90+ & Accessibility**
- [ ] **Phase 14: k6 Concurrency Load Testing, Production Containerization & Launch**

---

## Detailed Task Breakdown

### Phase 0: Kickoff, Architecture & Master Blueprint
- [x] Analyze `docs/01_PROJECT_BRIEF.md`, `docs/COMPANY_PROFILE.md`, and `docs/02_DESIGN_SYSTEM.md`.
- [x] Create Architectural Decision Records (`docs/adr/`):
  - [x] ADR 0001: Modular Monolith with Clean Architecture (.NET 10 + Next.js 15)
  - [x] ADR 0002: Money Representation, Rounding, and Currency Handling
  - [x] ADR 0003: Order Splitting per Seller (Parent Order and SubOrder Hierarchy)
  - [x] ADR 0004: Stock Reservation Strategy and High-Concurrency Inventory Control
  - [x] ADR 0005: Search Provider Abstraction and Meilisearch Integration
  - [x] ADR 0006: Dual-Token Authentication, Refresh Token Family Tracking, and RBAC
  - [x] ADR 0007: AI Provider Abstraction and Gemini API Key Security
- [x] Formulate Full PostgreSQL Database Specification & ER Diagram (`docs/DATA_MODEL.md`).
- [x] Define OpenAPI & ProblemDetails REST API Contract (`docs/API_CONTRACT.md`).
- [x] Formulate System Architecture with C4, Request Flows & Traceability Matrix (`docs/ARCHITECTURE.md`).
- [x] Document Open Questions and Default Recommendations for Product Owner.
- [x] Establish Master `PROGRESS.md` Task Tracker.

---

### Phase 1: Enterprise Foundation (.NET 10 Solution, Next.js Monorepo, Docker, CI)
- [x] **Repository Structure Setup**:
  - [x] Initialize `backend/` C# 13 / .NET 10 solution (`ShopVerse.sln`).
  - [x] Create `ShopVerse.Domain` project (Entities, Value Objects, Domain Events).
  - [x] Create `ShopVerse.Application` project (CQRS MediatR, FluentValidation).
  - [x] Create `ShopVerse.Infrastructure` project (EF Core 10, Npgsql, Redis).
  - [x] Create `ShopVerse.Payment` project (Gateway abstractions).
  - [x] Create `ShopVerse.Worker` project (BackgroundService outbox relay).
  - [x] Create `ShopVerse.Api` project (ASP.NET Core Web API).
  - [x] Create unit & integration test projects (`ShopVerse.UnitTests`, `ShopVerse.IntegrationTests`).
- [x] **Backend Foundation & Quality Plumbing**:
  - [x] Strongly typed `IOptions<T>` configuration validation at startup.
  - [x] Serilog structured JSON logging with `CorrelationId` and `TraceId`.
  - [x] Global Exception Handling Middleware returning RFC 7807 ProblemDetails.
  - [x] Health checks (`/health/live`, `/health/ready`) for Postgres, Redis, MinIO.
  - [x] OpenAPI / Swagger configuration with XML comments and JWT Bearer UI.
  - [x] Native 128-bit `Money` Domain Value Object with Banker's Rounding and 100% unit test coverage.
- [x] **Frontend Monorepo Foundation**:
  - [x] Initialize pnpm workspace in `frontend/`.
  - [x] Create Next.js 15 App Router skeletons for `apps/web` and `apps/console`.
  - [x] Configure `next-intl` (en, bn) and theme provider (dark default).
  - [x] Create `packages/ui`, `packages/config`, and `packages/api-client`.
  - [x] Set up OpenAPI client code generation (`openapi-typescript` / `orval`).
- [x] **Docker Infrastructure (`infra/docker/docker-compose.yml`)**:
  - [x] PostgreSQL 16 container with healthchecks and persistent volumes.
  - [x] Redis 7 container with persistent snapshotting.
  - [x] Meilisearch container with master key.
  - [x] MinIO container with automated bucket provisioning (`public-media`, `private-kyc`).
  - [x] Mailpit container for local email testing.
- [x] **CI & Automation**:
  - [x] GitHub Actions workflow for `dotnet test` and `dotnet format`.
  - [x] GitHub Actions workflow for frontend type-check, and build.
  - [x] Integration test runner using `WebApplicationFactory` and unit tests.

---

### Phase 2: Cybernetic Design System & Component Library (`packages/ui`)
- [x] Extract and configure MECHWARE semantic color tokens (Tech Midnight `#060B18`, Electric Cyan `#00E5FF`, Industrial Navy `#0F3E7D`).
- [x] Configure typography tokens (Inter, Plus Jakarta Sans, Hind Siliguri, tabular numerals).
- [x] Build core UI components with Radix/shadcn primitives:
  - [x] `Button`, `IconButton` with loading spinners and accessible touch targets.
  - [x] `Input`, `Textarea`, `Select`, `Combobox`, `PhoneInput` (+880 BD format).
  - [x] `Checkbox`, `Radio`, `RadioCard` (for delivery and payment selection).
  - [x] `QuantityStepper`, `Price` (with tabular nums and lakh formatting `৳1,25,000`), `Rating`, `Badge`.
  - [x] `ProductCard` (3 sizes: default, compact, horizontal) with matching zero-CLS skeleton loader.
  - [x] `Modal` (`Dialog`), `Drawer` (`Sheet`), `Toast`, `AlertBanner`, `Breadcrumb`, `Pagination`.
  - [x] `Carousel` (Embla), `CountdownTimer`, `ImageGallery` with zoom & lightbox.
  - [x] `DataTable` (TanStack Table) with sorting, pagination, and sticky headers.
- [x] Configure Framer Motion presets respecting `prefers-reduced-motion`.
- [x] Build custom SVG line illustration set (`EmptyCart`, `EmptySearch`, `OrderConfirmed`, `SystemFault`, `NoOrders`).
- [x] Build `/design-system` showcase documentation page in `apps/web`.
- [x] Run WCAG 2.2 AA automated contrast audit (22/22 token pairs passed) and unit tests.
- [x] Automated Axe Core Accessibility tests verifying 0 violations across all components.
- [x] Visual validation at 360px and 1440px in light and dark themes with headless screenshots captured.
- [x] **Principal Architecture & Cybersecurity Audit Remediation (Full Sign-off)**:
  - [x] Fixed Redis socket exhaustion in `RedisHealthCheck` by registering singleton `IConnectionMultiplexer`.
  - [x] Sanitized `/health/ready` output to prevent internal exception disclosure in non-development environments.
  - [x] Hardened `SecurityHeadersMiddleware` with strict `Content-Security-Policy` and `X-XSS-Protection: 0`.
  - [x] Sanitized `X-Correlation-Id` in `CorrelationIdMiddleware` against regex to eliminate log injection.
  - [x] Added `UseForwardedHeaders` before `UseRateLimiter` so reverse proxies rate limit per client IP.
  - [x] Hardened `GlobalExceptionHandlingMiddleware` with response `HasStarted` guard and mapped `ValidationException`.
  - [x] Optimized `Money.cs` value object with cached `CultureInfo("bn-BD")` and integer power calculation.
  - [x] Fixed `DataTable.tsx` pagination state and sticky header.
  - [x] Fixed `Price.tsx` and `formatters.ts` discount rounding (`Math.floor`) and fractional poisha BDT formatting.
  - [x] Fixed `ImageGallery.tsx` CSS injection and added accessible lightbox keyboard trap (`Escape`, `ArrowLeft`, `ArrowRight`).
  - [x] Fixed `CommandPalette.tsx` grouped item index mismatch and added `role="dialog"`.
  - [x] Fixed `CountdownTimer.tsx` Next.js 15 SSR hydration mismatch with `mounted` guard.
  - [x] Fixed `PhoneInput.tsx` paste bug with `+88` / `88` country prefix stripping.
  - [x] Fixed `FileUploader.tsx` unmount timer cleanup and aligned `ProductCardSkeleton.tsx` zero-CLS heights.
  - [x] Replaced browser blocking `alert()` with toast in design system showcase.
  - [x] Added missing `flash_sales`, `flash_sale_items`, `conversations`, `messages` to `docs/DATA_MODEL.md` and `docs/API_CONTRACT.md`.
  - [x] Added nested `status.*` color tokens to `tailwind.preset.js` and `globals.css` (`MW-P2-NEW-01`).
  - [x] Scoped `/swagger` Content-Security-Policy and enabled OpenAPI XML documentation (`MW-P1-NEW-02`).
  - [x] Hardened `ForwardedHeadersOptions` (`ForwardLimit = 1` & Dev/Test proxy guard) (`MW-P1-NEW-03`).
  - [x] Fixed `MechWareApiClient` non-JSON error stream double-read bug (`MW-P1-NEW-04`) and added 15-test regression suite.
  - [x] Replaced all raw Tailwind palette classes in `Badge.tsx`, `Rating.tsx`, and `design-system/page.tsx` and hardened `lint-tokens.mjs` (`MW-P2-NEW-06`).
  - [x] Synchronized `PhoneInput.tsx` controlled `value` prop & country switch handler, and added `getMotionVariants` reduced-motion helper (`MW-P2-NEW-07`).
  - [x] Deduplicated `ix_otp_codes_lookup` index, synced Light Mode `--focus-ring` (`#00707D`), and aligned all Phase 3 Auth/2FA/OAuth/Upazila specs in `DATA_MODEL.md` and `API_CONTRACT.md` (`MW-P0-NEW-08`).

---

### Phase 3: Identity, Authentication, Granular RBAC & Audit Interceptors
- [x] EF Core mapping and migrations for `User`, `Address`, `Role`, `Permission`, `RefreshToken`, `OtpCode`, `AuditLog`, and `BdLocation` (`ShopVerseDbContext`; the tables are created by `20261029190715_Phase4CatalogInventoryOutbox`).
- [x] Authentication flows:
  - [x] Email + Password registration and login with 210,000-iteration PBKDF2-SHA512 / Argon2id-equivalent hashing and NIST password policy enforcement.
  - [x] Phone SMS OTP registration and login with bound `reference_id` and `ISmsProvider` adapter (`LoggingSmsProvider`).
  - [x] Google OAuth / OIDC token validation (`POST /api/v1/auth/google`).
  - [x] Secure email verification & password reset flows (`POST /api/v1/auth/password/forgot`, `POST /api/v1/auth/password/reset`) with automatic session revocation on password update.
- [x] Token infrastructure:
  - [x] 15-minute access JWT signed with high-entropy key (`JwtTokenService`).
  - [x] Rotating refresh token in `httpOnly, Secure, SameSite=Lax, Path=/api/v1/auth` cookie.
  - [x] Token family tracking (`family_id`) with 10-second same-IP multi-tab/SSR grace window (`ADR-0006`) and instant token family revocation upon reuse detection.
  - [x] Session listing and revocation endpoints (`GET /api/v1/auth/sessions`, `POST /api/v1/auth/sessions/revoke`).
- [x] RFC 6238 TOTP 2FA + 10 hashed single-use recovery codes mandatory for `admin` and `finance` staff accounts (`TotpService`).
- [x] Progressive brute-force protection & account lockout (`FailedLoginAttempts`, `LockoutUntilUtc`).
- [x] Granular permission authorization handler (`[RequirePermission(Permissions.OrdersRefund)]`, `PermissionAuthorizationPolicyProvider`, `PermissionAuthorizationHandler`).
- [x] Seller resource isolation handler (`SellerResourceIsolationHandler` preventing cross-seller IDOR access, verified by unit & integration tests).
- [x] EF Core `SaveChangesInterceptor` (`AuditLogSaveChangesInterceptor`) automatically populating `AuditLog` with secret redaction.
- [x] Address book CRUD (`GET/POST/PUT/DELETE /api/v1/users/me/addresses`) with pre-seeded Bangladesh divisions, districts, and upazilas (`BangladeshLocationSeeder`, `GET /api/v1/locations/bd`).
- [x] GDPR Right-to-Access JSON export (`GET /api/v1/users/me/export`) and Right-to-Erasure anonymization (`DELETE /api/v1/users/me`).
- [x] Frontend auth modals and dedicated pages in `apps/web` (bilingual `en`/`bn` `AuthModal`, `HeaderAccountMenu`, `/[locale]/auth/login`, `/[locale]/auth/register`, `/[locale]/auth/forgot-password`, `/[locale]/account/addresses`, protected route middleware with sanitized `returnTo`) and `apps/console` (`/login` with mandatory 2FA, `RoleAwareSidebar`).
- [x] **Phase 3 R1 Sign-Off Audit & Remediation (Principal Engineer + Senior Product Designer + QA Lead)**:
  - [x] `AUD-P3-01` (Critical): Implemented RFC 6238 §5.2 TOTP replay guard via `User.LastUsedTotpTimeStep` and `TotpService.TryVerifyTotpCode` to reject replayed 6-digit TOTP codes within the 90-second (`±1` step) window.
  - [x] `AUD-P3-02` (High): Hardened `IdentityService.GoogleLogin` with structured Google OIDC JWT claim validation (`iss`, `exp`, `email_verified`, `sub`, `email`).
  - [x] `AUD-P3-03` (High): Extracted `IIdentityService` in `ShopVerse.Application.Identity` and removed direct `ShopVerse.Infrastructure.Identity` dependency from `ShopVerse.Api` controllers.
  - [x] `AUD-P3-04` (High): Configured `"AuthStrictPolicy"` rate limiter (`10 req/min` per IP) in `Program.cs` and applied `[EnableRateLimiting(AuthStrictRateLimitPolicy)]` to `/api/v1/auth/*` endpoints.
  - [x] `AUD-P3-05` (High): Fixed single-session logout via `RevokeRefreshTokenByCookie` (revokes only the cookie's `FamilyId`) and added `RevokeAllExceptCurrent` support to `/api/v1/auth/sessions/revoke`.
  - [x] `AUD-P3-06` (Medium): Aligned `OtpCode.MaxVerificationAttempts = 3` (`DATA_MODEL.md` §2.1) and added `POST /api/v1/auth/otp/send`.
  - [x] `AUD-P3-07` (Medium): Enforced 3-level parent-child `Division -> District -> Upazila` validation in `IdentityService.ValidateBdLocationHierarchy`.
  - [x] `AUD-P3-08` (Medium): Upgraded `AccountAddressesPage` and `Phase3IdentityWorkbench` with full Address Book CRUD, Light/Dark theme switching, and 16-viewport Visual QA verification (`360px`, `768px`, `1280px`, `1920px` × Light/Dark × EN/BN).

---

### Phase 4: Product Catalog, Concurrency Inventory & 400+ Industrial Seed Data
- [x] EF Core mapping for `Category` (materialized path, JSONB attribute schema), `Brand`, `Seller`, `Product`, `ProductVariant`, `ProductMedia`, `AttributeDefinition`/`AttributeValue`, `Warehouse`, `InventoryItem`, `StockReservation`, `SlugHistory`, `Review`, `Question`/`Answer`, `OutboxMessage`; migration `Phase4CatalogInventoryOutbox` (also carries the baseline schema because the Phase 3 migration file was never wired with `[Migration]`).
- [x] Product lifecycle Draft → PendingReview → Active/Rejected → Archived (`Product`, unit-tested).
- [x] High-concurrency inventory reservation service (`InventoryService`):
  - [x] PostgreSQL `SELECT ... FOR UPDATE` in deterministic `(variant_id, warehouse_id)` order, `Available = OnHand - Reserved`, `ck_inventory_available` CHECK as a last line of defence.
  - [x] Redis RedLock (RedLock.net) per variant, optional — correctness never depends on it.
  - [x] `ReservationExpiryWorker` (60 s, 15 min TTL, set-based sweep with `FOR UPDATE SKIP LOCKED`).
  - [x] `StockLowDomainEvent` raised once per downward threshold crossing and delivered through the outbox.
- [x] Deterministic pricing domain service (`PricingEngine`: sale windows `[start,end)`, compare-at, floor-% discount, VAT inclusive/exclusive, banker's rounding).
- [x] Transactional outbox: `DomainEventOutboxInterceptor` writes events in the same `SaveChanges`; `OutboxRelay` (`SKIP LOCKED`) feeds the `search:sync:queue` Redis list and storefront cache invalidation.
- [x] Storefront read models: `GET /api/v1/storefront/products/{slug}` (Redis cache, 301 on retired slugs), `/categories`, `/categories/{slug}/products`.
- [x] Deterministic seed (`dotnet run --project backend/src/ShopVerse.Worker -- seed-catalog`, ~5 s): 12 roots / 120 categories / 72 leaves, 40 brands, 25 sellers, 572 industrial products (≈840 variants), 3 240 reviews (446 with photos), 242 questions, report in `docs/PHASE4_SEED_REPORT.md`.
- [x] Concurrency test: 50 parallel reservations for 10 units → exactly 10 succeed (PostgreSQL-only and with RedLock).
- [x] Unit tests: pricing, lifecycle, variant generation, slug-history redirects, inventory invariants, seed-plan integrity.
- [x] Phase 4 audit (`docs/PHASE4_AUDIT.md`): 10 findings fixed with regression tests, 6 documented as accepted/open.
- [ ] Known gaps / follow-ups: product images are generated SVG illustrations (no licensed photography reachable from the build environment); CAD/datasheet files are not seeded; `checkout_sessions` is deferred to Phase 8 (reservations use `reference_id`); manufacturer part numbers and specs must be verified against datasheets before real listings.

---

### Phase 5: Sub-50ms Typo-Tolerant Search & Discovery (Meilisearch + Bangla)
- [ ] Implement `ISearchProvider` port and `MeilisearchSearchProvider` adapter.
- [ ] Product indexing schema with facets (category, brand, price, voltage, phase, availability).
- [ ] Typo-tolerance, exact prefix match, and popularity score ranking rules.
- [ ] Synonyms and Bangla transliteration support ("inverter = vfd = ইনভার্টার").
- [ ] Sub-50ms autocomplete endpoint (`/api/v1/search/autocomplete`).
- [ ] Transactional Outbox relay synchronization from PostgreSQL to Meilisearch.
- [ ] Frontend search bar with instant dropdown, recent queries, and keyboard navigation.

---

### Phase 6: Storefront Foundation, Mega Menu & Dynamic Homepage Sections
- [ ] Sticky cybernetic header with utility bar, category mega menu, and search input.
- [ ] Mobile responsive layout with drawer navigation and thumb-zone bottom bar.
- [ ] 12 CMS Homepage Sections:
  1. Hero carousel with priority LCP image loading.
  2. Value proposition strip (Free Delivery, Authentic, 7-Day Returns, COD).
  3. Industrial Category visual grid.
  4. Deals of the Day with live countdown and stock progress bars.
  5. Trending and recommended machinery.
  6. Promotional multi-banner campaigns.
  7. Best sellers by category tabs.
  8. Verified brand showcase strip.
  9. New arrivals in automation.
  10. Recently viewed machinery history.
  11. Newsletter and Industry 4.0 technical bulletin subscription.
  12. Comprehensive footer with trust certifications, courier logos, and legal links.

---

### Phase 7: Industrial Product Experience (PLP & PDP with CAD/Datasheets)
- [ ] **Product Listing Page (PLP)**:
  - [ ] Dynamic faceted filter sidebar (price range, brands, ratings, technical specs).
  - [ ] Active filter chips with live URL query synchronization.
  - [ ] Grid and list layout toggles.
  - [ ] Category SEO intro, FAQ accordion, and JSON-LD structured data.
- [ ] **Product Detail Page (PDP)**:
  - [ ] High-resolution image gallery with hover zoom and lightbox.
  - [ ] Technical specifications table with grouped engineering categories.
  - [ ] Direct CAD model (.STEP/.IGES) and PDF datasheet download buttons.
  - [ ] Variant matrix selector (voltage, phase, power output) with live stock updates.
  - [ ] Sticky Buy Box with Add to Cart, Buy Now, and Request for Quote (RFQ) buttons.
  - [ ] Verified customer reviews breakdown with AI pros/cons summary chips.
  - [ ] Frequently bought together and technical cross-reference comparisons.

---

### Phase 8: Cart, Promotions Engine, Atomic Checkout & Multi-Payment Gateways
- [ ] Guest cart cookie storage in Redis with seamless migration on login.
- [ ] Cart line grouping by fulfilling seller with delivery timeline estimates.
- [ ] Advanced promotion engine supporting stacking rules, spend tiers, and coupon validation.
- [ ] Atomic checkout with 15-minute stock reservation token.
- [ ] Two-tier order creation: 1 Master Order (`SV-YYMMDD-XXXXXX`) split into child `SubOrders`.
- [ ] Payment Gateway Integrations (`ShopVerse.Payment`):
  - [ ] bKash Tokenized Checkout adapter.
  - [ ] Stripe PaymentIntent adapter.
  - [ ] SSLCommerz Hosted Payment adapter.
  - [ ] Cash on Delivery (COD) adapter with mandatory SMS OTP challenge.
- [ ] Idempotent, cryptographically verified payment webhook handlers.
- [ ] Double-entry ledger batch posting upon successful payment capture.

---

### Phase 9: Order Fulfillment, Courier Tracking, Returns & Vector PDF Invoices
- [ ] Sub-order state machine with transition event audit log (`order_events`).
- [ ] Customer account order history, visual stepper timeline, and tracking details.
- [ ] Courier integration adapters (Steadfast, Pathao) with tracking webhooks.
- [ ] Vector PDF invoice generation using QuestPDF with QR code verification.
- [ ] RMA Return Request flow with customer photo uploads and inspection approvals.
- [ ] Automated review solicitation trigger (Delivered + 48 hours).
- [ ] Verified customer review submission and seller single-reply feature.

---

### Phase 10: Multi-Vendor Seller Center & Double-Entry Financial Ledger
- [ ] Seller onboarding wizard with KYC document upload to private S3 bucket.
- [ ] Seller management portal (`apps/console`):
  - [ ] Real-time sales, order status, and inventory dashboard.
  - [ ] Product catalog manager with specification inputs and variant matrix generator.
  - [ ] Sub-order fulfillment, packaging slips, and courier label printing.
- [ ] Double-Entry Financial Ledger (`ledger_accounts`, `ledger_entries`):
  - [ ] Guaranteed zero-sum balance ($\sum \text{Debits} == \sum \text{Credits}$).
  - [ ] Platform commission tracking and seller payable escrow.
  - [ ] Automated weekly Sunday payout execution job (minimum 500 BDT).
  - [ ] Downloadable CSV and PDF payout statements.

---

### Phase 11: Operations & Executive Admin Console (Stripe/Shopify Grade)
- [ ] Executive KPI dashboard with sparklines, revenue charts, and anomaly indicators.
- [ ] Global command palette (⌘K) for instant navigation to orders, products, and users.
- [ ] Server-side paginated data tables with multi-filter, column toggle, and bulk exports.
- [ ] Seller KYC review and approval workflow.
- [ ] Catalog moderation queue for new seller listings.
- [ ] Refund management with granular permission checks (`orders.refund`).
- [ ] System audit log viewer with JSON before/after state diffing.

---

### Phase 12: Google Gemini AI Intelligence (Search, Review Summaries, Assistant)
- [ ] Server-side `IAiProvider` with `GeminiAiProvider` implementation.
- [ ] Natural language search parser mapping queries to Meilisearch filters.
- [ ] Automated review summarization generating "Customers say..." and pros/cons tags.
- [ ] Grounded shopping assistant with strict tool calling against catalog APIs.
- [ ] Seller listing assistant generating titles, highlights, and SEO tags from inputs.
- [ ] AI review and Q&A content moderation background job.
- [ ] Redis caching for all AI responses with circuit breaker fallbacks.

---

### Phase 13: Hardening, OWASP ASVS Security, Lighthouse 90+ & Accessibility
- [ ] OWASP Top 10 security audit:
  - [ ] Strict Content Security Policy (CSP) with nonces.
  - [ ] Rate limiting on authentication, search, and checkout endpoints.
  - [ ] DOMPurify sanitization on rich text inputs.
  - [ ] PII encryption at rest for KYC and financial data.
- [ ] Performance optimization:
  - [ ] Core Web Vitals audit: LCP < 2.5s, INP < 200ms, CLS < 0.1.
  - [ ] Mobile Lighthouse score >= 90 across storefront routes.
  - [ ] Next.js bundle optimization ensuring initial route JS < 170 KB gzipped.
- [ ] Accessibility verification: WCAG 2.2 AA compliance with zero axe errors.
- [ ] Full bilingual testing in English and Bengali.

---

### Phase 14: k6 Concurrency Load Testing, Production Containerization & Launch
- [ ] k6 load test script simulating 5,000 concurrent users.
- [ ] High-concurrency checkout stress test: 200 orders/minute without overselling.
- [ ] Multi-stage production Dockerfiles for .NET 10 API, Worker, and Next.js apps.
- [ ] Production Docker Compose / Kubernetes deployment manifests.
- [ ] Automated database backup and disaster recovery test (RPO 1h, RTO 4h).
- [ ] Final launch verification checklist sign-off.

---

### B2B track — Company accounts (brief v2 §3.1.3), slice 1 of N — backend (2026-10-30)
- [x] Domain `Company`, `CompanySite`, `CostCentre`, `CompanyMember` (flag roles Requester/Buyer/Approver/Admin, spend limit, cost centres); BIN (13 digit) / TIN (12 digit) validation.
- [x] Credit rules (§8.7) in the aggregate: `ReserveCredit` allowed only if `used + total ≤ limit`, only for an active company with net terms; `SetCredit` cannot go below used; DB check constraints + `xmin` token back it up.
- [x] `ICompanyService` / `CompanyService`: register (registrant = first admin, company `pending`), sites (exactly one default), cost centres (unique code per company), members (last-admin protection under a row lock).
- [x] Tenancy: access is decided from `company_members` in the database on every call; a company the caller does not belong to answers **404**, a member without the role answers **403** (`ForbiddenException`). Not derived from a JWT claim.
- [x] API: `/api/v1/companies/**` (members) and `/api/v1/admin/companies/**` (staff: `companies.view` / `companies.manage` / `companies.credit`, credit = finance only).
- [x] Migration `B2bCompanyAccounts` (4 tables). Tests: 22 unit (incl. 300×200 random credit operations) and 13 integration against PostgreSQL (IDOR matrix over every operation, cross-tenant child ids, last-admin race, 30 racing credit reservations).
- [x] **Slice 2, web** (same branch): sign-in / register / OTP / password reset now call the real API (the in-browser fake tokens and fake Google session are removed; Google is disabled until `NEXT_PUBLIC_GOOGLE_CLIENT_ID` is set). `AuthProvider` restores the session from the httpOnly refresh cookie. Middleware reads a credential-free `mw_session` marker because the refresh cookie is scoped to `/api/v1/auth` and never reached `/account`.
- [x] Pages `/[locale]/account/company`, `/register`, `/[companyId]` (status, credit usage, tabs: sites, cost centres, members), EN + BN, loading / empty / error / success states. `@mechware/api-client` gained `createCompaniesApi` and a pure model with 8 unit tests (in CI).
- [x] Verified against the real API and PostgreSQL with Playwright (14 checks): sign-in redirect and returnTo, wrong password, BIN validation, duplicate BIN, site / cost centre CRUD, last-admin refusal, reload restores session, another tenant gets "not found" and no data, Bangla at 360 px without horizontal scroll, no console errors.
- [x] **Slice 3, console** (same branch): staff sign-in is real (password, then TOTP or recovery code for admin / finance; the fake e-mail-name check and fake tokens are gone; buyers are refused and signed out). Sidebar uses the signed-in user's real permissions instead of a role switcher; modules without a page render as disabled "SOON". `/companies` lets staff verify (activate), suspend / reactivate, and set credit terms and limit (finance only, same rules as the API), with filter and paging. Console is responsive (sidebar stacks on small screens). `problem*` helpers moved into `@mechware/api-client`; credit form rules (`buildCreditRequest`) unit-tested.
- [x] Verified against the real API and PostgreSQL with Playwright (23 checks): signed-out redirect, wrong password, buyer refused, seller signs in with no companies module and `/companies` answers 403, admin TOTP step (wrong code rejected, replay guard active, RFC 6238 code accepted), verify -> buyer sees `active` through the API, credit form refusals, net30 / BDT 1,00,000 saved, suspended company blocked with 409, filter empty state, reload keeps session, sign out locks the console, 360 px without horizontal scroll, no console errors.
- [x] **E2E in CI**: both Playwright suites (37 checks) moved to `frontend/packages/e2e` and run as the `e2e` job (PostgreSQL + Redis services, API from `dotnet publish`, production builds of web and console, screenshots and logs uploaded as an artifact). Passed on the GitHub runner (14/14 web, 23/23 console). Two earlier runner failures: a missing NuGet restore before `dotnet ef` (fixed), and one Bangla-page step that timed out once and passed on the next run with a stricter wait; the cause of that one is **not identified** (suspected timing around refresh-token rotation), so watch for flakiness. The failing step now prints its URL, API calls and page text.
- [ ] Not yet: light theme (layout is hard-coded dark), e-mail invites, KYC upload, credit 80% notification, global EF query filter, console i18n (English only), TOTP enrolment screen, and the other console modules (orders, KYC, ledger, users, audit) are still planned.
- [ ] Backend, not yet: approval rules (next module), credit use by checkout, global EF query filter (isolation is explicit in the service for now).
- Known gap: identity is still the in-memory store (Phase 3), so `company_members.user_id` has no FK and members are added by user id, not e-mail. Revisit when identity moves to PostgreSQL.

### B2B track, slice 4: Approvals, backend (brief v2 §8.3), same branch
- [x] `ApprovalRule` (amount threshold, optional cost centre and categories, ordered chain of 1-5 approvers) and `ApprovalRequest` with ordered `ApprovalStep`s. Rule choice is deterministic (highest threshold, then more conditions, then oldest). The requester is never an approver on their own request; rejecting needs a comment; finished requests never change; approvers who left the company are skipped.
- [x] `IApprovalService` / `ApprovalService` and `/api/v1/companies/{id}/approvals/**` (rules for admins; submit, mine, inbox, decide, cancel). Tenancy as before (404 / 403) through the new shared `CompanyAccess`, which `CompanyService` now uses too.
- [x] Migration `ApprovalRulesAndRequests` (3 tables). `xmin` token on the request plus a unique index on one open request per item. Mutation-checked: removing `xmin` makes the concurrent-decision test fail.
- [x] Tests: 19 unit (incl. 300x40 random state-machine runs) and 13 integration on PostgreSQL (full two-step chain, rejection, self-approval, IDOR over every operation, visibility inside the company, 8 racing submits create one request, 6 racing decisions count once over 8 rounds, suspended company, foreign approvers and cost centres refused).
- [x] **Approvals UI** (web, company dashboard, new "Approvals" tab, EN + BN): approver inbox (approve / reject with a required comment), my requests with progress and cancel, and a "New purchase request" form (generic subject until orders exist), plus approval rules for company admins (amount threshold, optional cost centre, ordered approver chain with up / down). `@mechware/api-client`: `createApprovalsApi` and a pure model (rule form checks, who-can-decide, decision checks), 14 unit tests in total.
- [x] E2E extended (web suite now 26 checks): admin makes the buyer an approver, saves a rule, a below-threshold amount needs no approval, two requests wait for the buyer, rejecting without a comment is refused, the buyer approves one and rejects the other in a separate browser, the requester sees both outcomes. Passed twice in a row locally against production builds. The suites now need the API started with `RateLimiting__GlobalPermitPerMinute=2000` (about 150 requests a minute from one IP otherwise queue behind the default limit of 100 and show up as 30 s stalls); CI sets it.
- Finding, not fixed: the global limit is 100 requests a minute **per IP** with a queue, and several office users behind one corporate NAT address would share it. Worth revisiting with real traffic numbers before launch.
- [ ] Not yet: `Idempotency-Key`, outbox events and notifications, hooking checkout / orders in (Phase 8; until then `subjectType`/`subjectId` is generic), spend limits per member and budgets per cost centre are stored but not yet checked.

### Bundle size (brief v2 §11: initial route JS < 170 kB gzip), same branch
- [x] **Root cause:** `@mechware/ui` is one barrel (`export *`) and its `package.json` did not declare `sideEffects`, so webpack could not drop unused modules and every page that imported one component also shipped the carousel, table, motion and other libraries. Measured with source-map-explorer, not guessed. One line (`"sideEffects": false`; the package has no CSS or global side-effect imports and no top-level `window` use) did most of the work.
- [x] The dashboard's non-default tabs (cost centres, approvals, members) now load on demand (`next/dynamic`); the default Sites tab stays in the page bundle.
- [x] First-load JS (gzip, as `next build` reports it): company list 230 -> 153 kB, register 230 -> 153 kB, dashboard 237 -> 168 kB, console `/` 216 -> 122 kB, console `/companies` 145 kB. Auth pages 112 kB. What is left is mostly Next.js and React themselves (about 213 kB raw), which cannot be trimmed.
- [x] **Budget gate in CI:** `pnpm check:bundle` (`packages/config/check-bundle-budget.mjs`) reads the build manifests and fails any route over 170 kB gzip; the numbers match `next build`'s. Mutation-checked: a 150 kB budget fails it, a missing build fails it. Exempt: `/design-system` (a showcase, 213 kB) and `_not-found`.
- Headroom is thin on the company dashboard (168.3 of 170 kB); the next feature added to it should lazy-load its heavy parts from the start. The browser E2E (49 checks) passes against the new build.

### Security: threat model for companies, credit and approvals (draft, 2026-10-30)
- [x] `docs/security/THREAT_MODEL_COMPANIES_APPROVALS.md`: STRIDE per module, findings register with severity, and for every claim how it was checked (RUN / TEST / CODE). Drafted by an AI assistant; **needs a human review**.
- **C1 (Critical), reproduced, then fixed:** the in-memory identity seeded a known admin (`admin@mechware.com.bd`, password and TOTP secret published in this repo) in **every** environment; a Production-mode API accepted it and returned the `admin` role with `companies.credit`. Now demo accounts exist only when the environment is `Development` (`IdentitySeedPolicy`); the same attack on a Production-mode API answers 401. 11 unit and 3 integration tests, mutation-checked. **Consequence:** Production / Staging start with no accounts until identity is database-backed and a first-administrator provisioning step exists.
- Other findings: approvals enforce nothing until orders exist and the amount is client-supplied (H1); company registration is unlimited and BIN can be squatted (H2); no step-up or tamper-evident audit for credit (H3); BIN / TIN in clear and in audit values (H4); staff other than admin / finance have no second factor (M1); no CSP on web and console (M5).

### Hardening track (started early, 2026-10-30) — see `docs/HARDCORE_TEST_REPORT.md`
- [x] Brief v2 (B2B procurement, direct seller first) and USD 100K review plan.
- [x] Architecture tests (layering, controller dependencies, exception hierarchy).
- [x] Property-based tests: Money, PricingEngine (20k cases), slug/URL fuzzing, XSS corpus + random markup.
- [x] JWT attacker-model tests; validator now enforces `alg`, `iss`, `aud`, `nbf`/`iat`, canonical encoding, size cap.
- [x] Endpoint authorisation coverage + forged-credential sweep + error-leakage tests.
- [x] Production boot guards: no published JWT secret, Swagger off, brute-force throttling verified.
- [x] Inventory chaos test (reserve/commit/cancel/expiry race) — found and fixed a sweeper deadlock.
- [x] CI: CodeQL, NuGet + npm vulnerability gate, Dependabot.
- [ ] Branch protection + GitHub secret scanning (owner action in GitHub settings).
- [ ] Playwright E2E journeys and k6 load tests (with checkout, Phase 8+).

## First administrator for environments without demo accounts

Production and Staging start with an empty identity store (C1), so nobody could sign in. The first administrator is
now provisioned from deployment secrets at startup:

| Setting | Meaning |
|---|---|
| `Identity__BootstrapAdmin__Email` | Enables the feature; empty means no bootstrap administrator |
| `Identity__BootstrapAdmin__Password` | Must satisfy the normal password policy |
| `Identity__BootstrapAdmin__TotpSecret` | Required, base32, at least 26 characters (16 bytes); `Phone` and `FullName` are optional |

- Partly configured settings, a weak password or a short or malformed TOTP secret stop the API at startup (fail closed).
- The account always demands the second factor; no password-only administrator can be provisioned.
- Audited as `user.bootstrap_admin_provisioned` (e-mail only, never a secret). No recovery codes are issued; to
  recover, change the deployment secret and restart.
- Verified: unit tests (settings validation, 2FA flow), a `Production` integration test (wrong password 401,
  password-only 202, wrong code 401, correct code 200 with the admin role, demo admin still 401), and a mutation
  check (removing the TOTP secret makes the integration test fail).

## Identity in PostgreSQL

`IdentityService` no longer keeps users, sessions, OTP codes, addresses or audit events in memory. They live in the
database, so a restart or a second API instance loses nothing (the tables come from `20261029190715_Phase4CatalogInventoryOutbox`;
the old hand-written `202610290001_Phase3IdentityAndAccess.cs` had no `[Migration]` attribute, never ran, and is removed).

- `IIdentityService` is async and the service is registered per request (it holds the request's `ShopVerseDbContext`).
- Demo accounts (Development only) and the bootstrap administrator are created by `IdentitySeeder` at startup,
  idempotently: an existing account is never reset. Nothing touches the database at boot when there is nothing to seed.
- Refresh-token rotation and OTP checks take a row lock (`SELECT ... FOR UPDATE`) inside a transaction, so two
  concurrent rotations of one token have exactly one winner and the OTP attempt limit cannot be raced. The reuse
  revocation, failed-login counters and lockouts are saved before the request fails, so a rollback cannot undo them.
- Registration races are settled by the unique indexes `ux_users_email` / `ux_users_phone`.
- `RefreshToken` and `OtpCode` are `[NotAudited]` (row noise); the meaningful events are still written explicitly.
- Tests: 17 service tests plus seeding and bootstrap tests run against real PostgreSQL (`IdentityServiceTests`,
  `AccountSeedingTests`), and the API-level tests use `DbBackedFactory`, a host over its own throw-away database.
  Mutation checks: removing the row lock, the pre-throw save of the breach revocation, or the failed-login save each
  makes a specific test fail. Checked by hand against a Production-mode API: bootstrap admin signs in with password
  and TOTP, the API restarts, and the refresh cookie issued before the restart still works.

`company_members.user_id` now has a foreign key to `users` (`OnDelete.Restrict`: users are soft-deleted and
anonymised, never removed), and `RegisterCompany` / `AddMember` refuse an id that is not a live account (threat model
M2, existence part). The migration `CompanyMembersUserFk` stops with a clear message if old rows name users that do not
exist, rather than deleting a company's admins silently; checked against a database that had such a row.

Still open: invitation and consent when a user is added to a company (the rest of M2); GDPR erasure still deletes
addresses outright.


## Phase 8 slice b: promotion engine (pure domain)

`ShopVerse.Domain.Pricing.PromotionEngine` prices a basket's promotions with no I/O: percent off, fixed off, volume tiers
per line, spend tiers, scoped by everything / category / seller / variant, with priority order, exclusivity, caps,
validity windows and global / per-user limits (the counters are supplied by the caller). A discount is rounded once
(banker's, ADR-0002) and allocated to lines by largest remainder, so the line discounts add up to exactly the promotion's
discount. 16 unit tests, including 500 random baskets that must balance to the poisha, input-order independence, and
mutation checks (half-up rounding, no largest-remainder step, ignored cap and ignored exclusivity each fail a test).
Not built yet: cart (Redis), order and atomic checkout, payment providers and webhooks, the frontend.

## Phase 8 slice a: cart rules (pure domain)

`ShopVerse.Domain.Cart.CartEngine`: add / set / remove with stock, minimum order quantity, pack size and line limits;
merge of a guest cart into the signed-in buyer's cart on login (quantities add up, capped, the buyer's order kept);
re-validation against the catalog that returns a notice for every drift (price changed, stock reduced, sold out,
withdrawn, below the minimum order, adjusted to the pack size) and is idempotent. 12 unit tests; five mutations
(replace instead of add, no stock check, uncapped merge, stale price, no pack snapping) each make a test fail.
The catalog has no minimum-order or pack-size columns yet, so the engine takes them as inputs that default to 1.
Not built yet: the Redis cart store with its signed guest token, the HTTP endpoints, the frontend.

## Phase 8 slice c (part 1): order state machine and order number (pure domain)

`ShopVerse.Domain.Orders.OrderStateMachine` is the single place that says which state may follow which (brief §8.6):
`pending_approval -> pending_payment -> confirmed -> processing -> packed -> shipped -> out_for_delivery -> delivered
-> completed`, plus cancelled, delivery_failed, returned, return_requested, return_approved and refunded. Every change
is an `OrderEvent` (who, when, why); a reason is mandatory for cancelling, a failed delivery and a return approval.
`OrderNumber` generates `MW-YYMMDD-XXXXXX` with six random base32 characters. 22 unit tests: every pair of states is
checked against an independently written table, every state is reachable, terminal states have no exit, and five
mutations (shipped orders cancellable, completed reopens, no reason, stock held after shipping, lower-case numbers)
each fail a test.

Note: ADR-0003 still shows the old `SV-` order-number prefix. The brief (v2) says `MW-` and is the source of truth;
the ADR should be updated when the order tables are built. Not built yet: the order tables, the atomic
`PlaceOrder` transaction with idempotency, payment providers and webhooks, the frontend.

## Phase 8 slice d (part 1): webhook verification (pure domain)

`ShopVerse.Domain.Payments.WebhookVerifier` checks a provider webhook the way brief §8.7 requires: HMAC-SHA256 over
`{timestamp}.{rawBody}` compared in constant time, and a time window (default 5 minutes, small clock skew tolerated,
requests from the future refused). The timestamp is part of the signed message, so a captured request cannot be
replayed with a fresh timestamp. Deduplication by provider event id needs storage and stays with the inbox (not built
yet). 19 unit tests; five mutations (timestamp not signed, no expiry, future allowed, signature unchecked, prefix not
stripped) each fail a test. Not built yet: the `IPaymentProvider` adapters (SSLCommerz, bKash, Stripe, COD), the
inbox, the HTTP webhook endpoints, and "confirmed only by server-side verification, never by the browser redirect"
wiring.

## Phase 8 slice c (part 2): order pricing (pure domain)

`ShopVerse.Domain.Orders.OrderPricing.Price` turns server-resolved lines into the order's money: per-line VAT
(inclusive prices are split, exclusive prices get VAT added), one sub-order per seller with its own shipping, the
marketplace commission on the seller's discounted net goods, and the seller's earning. Rounding happens once per line
in whole poisha (banker's); every higher total is a sum, so `GrandTotal = sum of line totals + shipping` holds exactly.
The input type has no field for a client-supplied price, tax or total, and a discount larger than its line, a negative
price, an impossible VAT rate, a zero quantity, or missing / duplicate seller terms are refused: this is the arithmetic
half of "no price can be changed from the client". 9 unit tests including 800 random orders in both tax modes that must
reconcile to the poisha; five mutations (VAT added twice, commission on gross, shipping dropped, half-up VAT, oversized
discount) each fail a test.

Not built yet: the order tables and migration, the atomic `PlaceOrder` transaction (re-price from the database, reserve
stock through `IInventoryService`, insert order + sub-orders + outbox event, idempotency key), the tamper test against
the HTTP endpoint, payment adapters, the inbox, the frontend.

## Phase 8 slice c (part 3): atomic PlaceOrder, order tables, idempotency

`IOrderService.PlaceAsync` (`ShopVerse.Infrastructure.Orders.OrderService`) turns a request that carries **only variant ids
and quantities** (no price, tax, discount or total field exists) into an order:
re-prices every line from the database (`PricingEngine` + `OrderPricing`), checks the products are live, reserves stock
through `IInventoryService` under the order id, and writes the order, one sub-order per seller, frozen line snapshots,
its first `order_events` row, the idempotency record and the `OrderPlacedEvent` outbox message. Tables: `orders`,
`sub_orders`, `order_items`, `order_events`, `idempotency_keys` (migration `OrdersAndSubOrders`), with CHECK constraints
that make the database itself refuse an order whose money does not add up (`ck_order_items_money`, `ck_sub_orders_commission`).

- Same `Idempotency-Key` and same request: the original order is returned (`Replayed = true`), stock is held once. Same key,
  different request: `IDEMPOTENCY_KEY_REUSED`. Eight parallel retries of one key give one order: they queue behind a
  session-level Postgres advisory lock. (Found by a test: without the lock, retries lost the stock race and were told
  "insufficient stock" instead of getting their own order back.)
- 20 buyers for 5 units: exactly 5 orders, the rest `INSUFFICIENT_STOCK`, stock never negative.
- If saving fails after stock was reserved, the hold is released; an order-number clash is retried with a fresh number.
- 14 integration tests against PostgreSQL. Mutations: no advisory lock, replay ignoring the request hash, no release on a
  number clash, no release on a generic failure, and duplicate lines not merged each fail a test. One mutation survives on
  purpose: removing the service's own "product is live" check changes nothing because `InventoryService` already refuses
  non-live products with the same error; the service check is a second layer, not a missing test.

Open: promotions are not yet fed into the order (the engine exists, no promotion store yet); shipping is 0 until zones are
built; approval routing (`pending_approval`) and the credit-terms check (`creditUsed + total <= creditLimit`, brief §8.7)
are not wired; the HTTP endpoint with the tamper test, payment adapters and the inbox, the frontend.

## Phase 8 slice c (part 4): the order endpoint

`POST /api/v1/orders` (authenticated; `Idempotency-Key` header; body = variant ids and quantities, optional `companyId`)
and `GET /api/v1/orders/{id}` (the buyer's own order; anyone else's answers 404, the same as an unknown id).
201 for the request that creates the order, 200 with the original order for a replay, 409 for the same key with a different
request, 400 for a bad body, 409 `INSUFFICIENT_STOCK` / `VARIANT_UNAVAILABLE` when it cannot be fulfilled.

- **Tamper test (acceptance criterion of Phase 8):** a request that also sends `unitPriceMinor: 1`, `total: 1`,
  `grandTotalMinor: 1`, a 999,999,999 discount and `vatRate: 0` is charged the server's price (2 x 2,500.00), and the stored
  order has no discount. There is no field in the request type for any of them.
- **Company orders:** an order in a company's name needs a Buyer of that company; a non-member gets 404, a Requester-only
  member 403 `COMPANY_ROLE_REQUIRED`, an unverified company 409 `COMPANY_NOT_VERIFIED`, a suspended one 409
  `COMPANY_SUSPENDED`. Company orders are VAT-exclusive (VAT added on top), guest/personal orders VAT-inclusive (brief §8.1).
- 8 more integration tests (22 for orders in all). Mutations: client price trusted, membership check skipped, verified
  check skipped, any user able to read any order, replay answering 201 each fail a test. Equivalent mutants left alone and
  recorded: dropping the controller's "header required" check (the service rejects a missing key with the same 400), and
  dropping the service's "product is live" check (`InventoryService` refuses non-live products with the same error).

Still open for Phase 8: promotions fed into the order, shipping zones, approval routing and the credit-terms check,
payment adapters (SSLCommerz, bKash, Stripe, COD) and the webhook inbox, auto-cancel of unpaid orders after 30 minutes,
and the whole frontend (cart, checkout, confirmation).

## Phase 8 slice c (part 5): credit terms on orders (brief §8.7)

A company order may set `payOnCredit`. The company's row is locked (`SELECT ... FOR UPDATE`, with `xmin` selected explicitly
because EF needs it as a concurrency token and a bare `SELECT *` omits system columns), `Company.ReserveCredit` checks
`creditUsed + orderTotal <= creditLimit`, the order moves to `confirmed` with an event, and everything is saved in one
transaction; the held stock is then converted into a real deduction. A refused order releases its stock and leaves the
credit untouched. Errors: `NO_CREDIT_TERMS`, `CREDIT_LIMIT_EXCEEDED`, 400 for `payOnCredit` without a company; a replay
reports the real status (`confirmed`).

Lesson recorded: my first parallel test (10 orders for one variant) passed with the lock removed, because the inventory
lock serialised the orders and the credit race never happened. The test now gives each order its own variant and releases
them together; with the lock it passes 3 of 3 runs, without it it fails 3 of 3.

Known window: if the process dies between the committed order and the stock `CommitAsync`, the stock stays reserved until
the reservation expires and is released while the order is confirmed. A reconciliation sweep (confirmed order with an
active reservation) belongs with the payment slice and is not built yet. Cancelling a credit order must also call
`ReleaseCredit`; that belongs with the cancel flow.

## Phase 8 slice c (part 6): cancelling orders and the 30-minute unpaid sweep (brief §8.6)

`POST /api/v1/orders/{id}/cancel` lets a buyer cancel their own order while it is still pending (payment or approval): the
order row is locked, the state machine validates the move, the event is recorded with the reason, and the held stock is
released. A confirmed order is refused (`ORDER_NOT_CANCELLABLE`) because the inventory has no way to return stock to the
shelf yet. `UnpaidOrderExpiryWorker` runs `ExpireUnpaidAsync` every minute and cancels orders still `pending_payment` 30
minutes after placement; parallel sweeps cancel each order once.

Three defects found by tests and by reading the code while writing this, all fixed:
- **The stock hold was shorter than the order's payment window.** Reservations default to 15 minutes, orders stay payable
  for 30, so a payment arriving between minutes 15 and 30 would have found no stock. The order now reserves for the full
  30 minutes (`TheStockHold_LastsAsLongAsTheOrderCanBePaid`).
- **Order events could tie on their timestamp** (the "placed" and "confirmed" events of a credit order share one instant), so
  their order was undefined. Each event now has a per-order sequence number, kept as a counter on the order row so no
  event list has to be loaded, and `(order_id, seq)` is unique.
- **My first version computed the sequence from the loaded event list**, which the cancel path does not load; the unique
  index rejected the duplicate on the first run. Hence the counter on the order row.

Mutations that each fail a test: cancel without releasing stock, sweep ignoring the cutoff, any user cancelling any order,
confirmed orders cancellable, no row lock on the order, no reason required, reservation back to 15 minutes.

Not built yet: restocking (so a confirmed order can be cancelled and its credit released), the reconciliation sweep for
the credit-order crash window, payment adapters and the inbox, promotions and shipping in the order, approval routing,
the frontend.
