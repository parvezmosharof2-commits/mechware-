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
- [x] EF Core mapping and migrations for `User`, `Address`, `Role`, `Permission`, `RefreshToken`, `OtpCode`, `AuditLog`, and `BdLocation` (`ShopVerseDbContext`, `202610290001_Phase3IdentityAndAccess`).
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
- Known: company pages ship about 230 kB first-load JS (brief budget 170 kB) because `@mechware/ui` is imported as one barrel; needs per-component imports or `optimizePackageImports`.
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
