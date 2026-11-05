# MECHWARE — Principal Architecture, Cybersecurity & Design Audit Report (Phase 0 + Phase 1 + Phase 2 Deep Re-Verification)

> **Auditor Persona:** Principal Software Architect, Staff Cybersecurity Auditor, and Lead Product Designer  
> **Project:** MECHWARE ($100,000 Enterprise Industrial Automation & B2B/B2C Commerce Platform)  
> **Repository Audited:** `https://github.com/parvezmosharof2-commits/mechware-.git` (HEAD commit `b7c0d72`)  
> **Audit Scope:** Phase 0 (`docs/`), Phase 1 (`backend/`, `infra/`, `.github/`, `frontend/packages/api-client`), and Phase 2 (`frontend/packages/ui`, `frontend/packages/config`, `frontend/apps/web`, `frontend/apps/console`)  
> **Reference Standards:** `docs/04_REVIEW_PROMPTS.md` (`R1` Phase Sign-Off Review, `R2` Premium Design Critique, `R3` Security Review, `R4` Performance Review), `docs/01_PROJECT_BRIEF.md`, `docs/02_DESIGN_SYSTEM.md`, `docs/03_PHASE_PROMPTS.md`  
> **Overall Verdict:** **CONDITIONAL HOLD ON PHASE 3** — Commit `b7c0d72` and `bee490b` successfully resolved **26 of 26** prior findings, **HOWEVER**, our deep re-audit uncovered **1 Critical Regression**, **4 High-Severity Flaws**, **2 Medium-Severity Flaws**, and **1 Low-Severity Schema/Token Drift** (`MW-P2-NEW-01` through `MW-P0-NEW-08`) that must be patched before starting Phase 3.

---

## 1. Verification of Previous Audit Remediations (`bee490b` & `b7c0d72`)

We verified the git commit history (`bee490b` + `b7c0d72`) and ran the automated verification suites:
- **`node frontend/packages/config/lint-tokens.mjs`**: Passed (`0` violations with existing regex).
- **`node frontend/packages/ui/scripts/verify-contrast.mjs`**: Passed (`22/22` WCAG 2.2 AA dark/light token pairs verified).
- **`node --test frontend/packages/ui/tests/components.test.mjs`**: Passed (`11/11` tests).
- **Previously Reported Issues Confirmed Fixed (`26/26`):**
  - `CRIT-01` (`RedisHealthCheck` singleton `IConnectionMultiplexer` DI): **Verified Fixed** in `Program.cs:117–124` & `RedisHealthCheck.cs:11–14`.
  - `CRIT-02` (`/health/ready` exception leak sanitization in production): **Verified Fixed** in `Program.cs:179–187`.
  - `CRIT-03` (`DataTable.tsx` pagination state & sticky header): **Verified Fixed** in `DataTable.tsx:37–62` & `104`.
  - `CRIT-04` (Phase 0 `DATA_MODEL.md` missing tables, `ADR-0004` `FOR UPDATE SKIP LOCKED`, `API_CONTRACT.md` `clientPrice` removal): **Verified Fixed** in commit `bee490b`.
  - `HIGH-02` (`CorrelationIdMiddleware` regex `^[a-zA-Z0-9\-_]{1,64}$`): **Verified Fixed** in `CorrelationIdMiddleware.cs:13–14`.
  - `HIGH-04` (`GlobalExceptionHandlingMiddleware` `HasStarted` guard & `FluentValidation` details): **Verified Fixed** in `GlobalExceptionHandlingMiddleware.cs:37–51`.
  - `HIGH-05` (`Money.cs` `CultureInfo.ReadOnly` cache & decimal math): **Verified Fixed** in `Money.cs:11–12` & `40`.
  - `HIGH-07` (`Price.tsx` & `formatters.ts` `Math.floor` discount rounding & BDT poisha decimals): **Verified Fixed** in `formatters.ts:13–28, 54`.
  - `HIGH-08` (`ImageGallery.tsx` URL encoding & lightbox keyboard/dialog ARIA): **Verified Fixed** in `ImageGallery.tsx:33–53, 134`.
  - `HIGH-09` (`CommandPalette.tsx` `flatGroupedItems` keyboard sync & dialog ARIA): **Verified Fixed** in `CommandPalette.tsx:35–38, 76`.
  - `HIGH-10` (`CountdownTimer.tsx` SSR `mounted` guard & `onExpireRef`): **Verified Fixed** in `CountdownTimer.tsx:23–35`.
  - `HIGH-11` (`axe-all-components.test.tsx` added with `vitest-axe` for all 35 components): **Verified Fixed** in `frontend/packages/ui/tests/axe-all-components.test.tsx`.

---

## 2. R1 — Phase 0, Phase 1 & Phase 2 Requirement Sign-Off Tables (`docs/04_REVIEW_PROMPTS.md`)

### Phase 0 Sign-Off Table (Architecture, Contracts & ADRs)

| # | Phase 0 Requirement (`03_PHASE_PROMPTS.md`) | Status | Evidence & Audit Notes |
|---|---|---|---|
| 1 | `docs/ARCHITECTURE.md` (C4 L1–L3, bounded contexts, outbox, cache, observability, deployment) | **Done** | C4 L1–L3 Mermaid diagrams, 10 bounded contexts, transactional outbox, Redis cache-aside + RedLock, and bKash server-to-server `ExecutePayment` flow documented (`ADR-0001` documents `ShopVerse.*` assembly mapping). |
| 2 | `docs/DATA_MODEL.md` (Full ER diagram + every table, column, type, FK, index, soft-delete, audit) | **Partial (Minor DDL Bug)** | All 41 tables + `trg_enforce_balanced_ledger_batch` deferrable trigger are documented, **but** `otp_codes` (Lines 184–188) has a duplicate `ix_otp_codes_lookup` index block with conflicting column lists (`MW-P0-NEW-08`). |
| 3 | `docs/API_CONTRACT.md` (Every v1 endpoint, RFC 7807 errors, rate limits, auth scopes) | **Done** | Covers Auth, Catalog, Cart, Atomic Checkout (`/reserve` + `/orders`), bKash/SSLCommerz/COD OTP, Seller Console, RFQ (`/rfq`), Messaging, Notifications, and Admin Disputes. |
| 4 | `docs/adr/0001` through `0007` | **Done** | All 7 ADRs complete and aligned with `01_PROJECT_BRIEF.md`. |

### Phase 1 Sign-Off Table (Enterprise Foundation — Repo, Docker, CI, Skeletons)

| # | Phase 1 Requirement (`03_PHASE_PROMPTS.md`) | Status | Evidence & Audit Notes |
|---|---|---|---|
| 1 | `infra/docker/docker-compose.yml` (Postgres 16, Redis 7, Meilisearch, MinIO, Mailpit, Seq, OTel, Prometheus, Grafana) | **Done** | All 9 services configured with healthchecks, resource limits, and named volumes. |
| 2 | `.NET 10 Solution` + OpenAPI / Swagger documentation with XML comments and JWT bearer auth at `/swagger` | **Partial (Broken in Browser)** | `SecurityHeadersMiddleware.cs:21` applies `Content-Security-Policy: default-src 'none'` to `/swagger`, blocking all Swagger UI scripts/styles! Also `ShopVerse.Api.csproj` is missing `<GenerateDocumentationFile>true</GenerateDocumentationFile>` (`MW-P1-NEW-02`). |
| 3 | Middlewares (`Serilog`, `OpenTelemetry`, `ProblemDetails`, `SecurityHeaders`, `RateLimiter`, `/health/live`, `/health/ready`) | **Partial (Security Flaw)** | `Program.cs:56–61` clears `KnownIPNetworks` and `KnownProxies` without `ForwardLimit = 1` or environment restriction, allowing trivial rate-limit bypass via `X-Forwarded-For` header spoofing (`MW-P1-NEW-03`). |
| 4 | Domain Primitives (`Money`, `Currency`, `Entity<TId>`, `AggregateRoot<TId>`, `DomainException`) | **Done** | Banker's rounding (`MidpointRounding.ToEven`), 4-decimal intermediate math, and read-only `bn-BD` `CultureInfo` verified by 18 unit tests in `MoneyTests.cs`. |
| 5 | Typed API client (`packages/api-client`) | **Partial (Runtime Crash Bug)** | `MechWareApiClient.fetch()` (`packages/api-client/src/index.ts:57–68`) calls `await response.json()` in `try` and `await response.text()` in `catch` on the same `Response`, throwing `TypeError: Body has already been read` whenever a non-JSON error occurs (`MW-P1-NEW-04`). |
| 6 | Integration tests (`ShopVerse.IntegrationTests`) & CI (`ci.yml`) | **Partial** | `ApiStartupIntegrationTests.cs` only tests `/health/live` and `/unknown-route`, never asserting `/health/ready`, `SecurityHeadersMiddleware`, or `CorrelationIdMiddleware` sanitization (`MW-P1-NEW-05`). |

### Phase 2 Sign-Off Table (Cybernetic Design System & Component Library)

| # | Phase 2 Requirement (`03_PHASE_PROMPTS.md` & `02_DESIGN_SYSTEM.md`) | Status | Evidence & Audit Notes |
|---|---|---|---|
| 1 | `packages/ui/tokens/` + `tailwind.preset.js` + `lint:tokens` CI rule | **Partial (Critical Regression)** | `tailwind.preset.js` defines flat `online`, `pending`, `fault` colors, **omitting** nested `status: { online, pending, fault }`. Yet 11 components and app pages use `bg-status-online`, `text-status-online`, `border-status-online`, `text-status-pending`, `text-status-fault` — generating **zero CSS** (`MW-P2-NEW-01`)! |
| 2 | Full 35-Component Catalog in `packages/ui/src/components/` | **Partial** | 1) `Badge.tsx` (Lines 38–51) uses raw Tailwind palette classes (`text-amber-400`, `text-sky-300`, `text-emerald-400`) that fail Light Mode contrast (`MW-P2-NEW-06`).<br>2) `PhoneInput.tsx` ignores controlled `value` updates and doesn't emit `onChange` when switching countries (`MW-P2-NEW-07`).<br>3) `motionPresets.ts` lacks a `prefers-reduced-motion` helper (`MW-P2-NEW-07`). |
| 3 | Showcase route `/[locale]/design-system` in `apps/web` | **Done** | Renders all 35 components, tokens, Dark/Light mode toggle, and EN/BN locale preview without `window.alert`. |
| 4 | Unit tests (`components.test.mjs`) + `axe` tests (`axe-all-components.test.tsx`) | **Done** | Both Node test runner (`11/11`) and Vitest + `vitest-axe` (`35/35` components) are present. |

---

## 3. R2 — Premium $100K Design Critique Scorecard (`docs/04_REVIEW_PROMPTS.md`)

| Dimension | Score (1–10) | Verdict & Evidence |
|---|---|---|
| **1. Visual Hierarchy** | **8.5 / 10** | Strong 60-30-10 Cybernetic Industrial dark hierarchy (`#060B18` canvas, `#0D1635` surface, `#00E5FF` electric cyan accent). **Deduction:** Missing `status.*` in `tailwind.preset.js` makes `ProductCard` "Added to Cart" state (`bg-status-online text-bg`) and `Stepper` completed state render dark `#060B18` text on a transparent background! |
| **2. Typography** | **9.5 / 10** | `Plus Jakarta Sans`, `Inter`, `JetBrains Mono`, and `Noto Sans Bengali` are properly loaded with `tabular-nums` (`font-mono`) on all prices, SKUs, and timers. |
| **3. Color & Contrast** | **8.0 / 10** | Semantic tokens pass 22/22 WCAG AA checks (`verify-contrast.mjs`), **but** `Badge.tsx` uses raw `text-amber-400` (`1.67:1` on white) and `text-sky-300` (`1.54:1` on white), which become unreadable when toggling to Light Mode. |
| **4. Spacing & Rhythm** | **9.0 / 10** | Strict 4px base grid and consistent `14px`/`10px` industrial border radii across cards, modals, and inputs. |
| **5. Motion & Micro-interactions** | **8.0 / 10** | Spring and cubic-bezier presets exist in `motionPresets.ts`, **but** neither `motionPresets.ts` nor `Carousel.tsx` respects `prefers-reduced-motion: reduce` (`02_DESIGN_SYSTEM.md` §2.5). |
| **6. States Coverage** | **9.0 / 10** | Custom industrial SVG illustrations (`EmptyCart`, `EmptySearch`, `NoOrders`, `OrderConfirmed`, `SystemFault`) and zero-CLS `ProductCardSkeleton` are well-crafted. |
| **7. Responsive Behavior** | **9.0 / 10** | Clean mobile-first breakpoints and `44x44px` minimum touch targets on `QuantityStepper`, `IconButton`, and `Pagination`. |
| **8. Accessibility (WCAG 2.2 AA)** | **8.5 / 10** | Radix primitives + `axe-all-components.test.tsx` cover ARIA roles, **but** Light Mode badge contrast (`Badge.tsx`) and `prefers-reduced-motion` need remediation. |
| **9. Brand Cohesion** | **9.5 / 10** | Authentic industrial automation aesthetic tailored to Siemens/Delta/Mitsubishi PLCs, VFDs, and Made-in-Bangladesh ETP skids. |
| **10. Conversion Clarity** | **8.0 / 10** | Broken `bg-status-online` class in `ProductCard.tsx` and `Stepper.tsx` directly harms add-to-cart and checkout step clarity until `tailwind.preset.js` is patched. |

---

## 4. R3 & R4 — Security (OWASP ASVS L2) & Performance Review Summary

### R3 Security Audit Findings (Phase 0–2 Scope)
1. **[HIGH] Rate-Limit Bypass via `X-Forwarded-For` Spoofing (`Program.cs:56–61`):** Clearing `KnownIPNetworks` and `KnownProxies` unconditionally without `ForwardLimit = 1` or environment checks allows any attacker to spoof `X-Forwarded-For: <random-ip>` on every request, completely bypassing the `100 req/min` IP rate limiter.
2. **[HIGH] CSP Header Breaks `/swagger` (`SecurityHeadersMiddleware.cs:21`):** Applying `default-src 'none'` globally without a route policy for `/swagger` breaks the developer OpenAPI portal in Development/Staging.

### R4 Performance & Core Web Vitals Findings (Phase 0–2 Scope)
1. **Zero-CLS Skeleton Parity (`ProductCardSkeleton.tsx`):** Verified fixed (`aspect-square`, `min-h-[2.5rem]` title, spec tags, rating row, price row, `h-8` button).
2. **Backend Memory Allocation (`Money.cs` & `RedisHealthCheck.cs`):** Verified fixed (`CultureInfo.ReadOnly` static cache and singleton `IConnectionMultiplexer`).

---

## 5. Actionable Audit Matrix — 8 New/Residual Flaws (`MW-P2-NEW-01` to `MW-P0-NEW-08`)

### 🔴 [MW-P2-NEW-01] (CRITICAL) Missing `status.*` Color Namespace in `tailwind.preset.js` Breaks 11 Components & App Pages
- **File / Location:** `frontend/packages/config/tailwind.preset.js` (Lines 40–45)
- **Affected Consumers:** `ProductCard.tsx` (Lines 115, 281), `Stepper.tsx` (Lines 73, 110), `Toast.tsx` (Lines 30–39), `AlertBanner.tsx` (Lines 26–33), `PhoneInput.tsx` (Lines 78, 117), `FileUploader.tsx` (Line 142), `Badge.tsx` (Line 62), `Avatar.tsx` (Line 15), `Progress.tsx` (Lines 15–16), `apps/web/src/app/[locale]/page.tsx` (Line 10), `apps/console/src/app/page.tsx` (Lines 15, 24, 34, 38).
- **Flaw Explanation:**
  All 11 files above use `bg-status-online`, `text-status-online`, `border-status-online`, `ring-status-online`, `bg-status-pending`, `text-status-pending`, `border-status-pending`, `text-status-fault`, and `border-status-fault`. However, `tailwind.preset.js` only defines flat keys (`online`, `warning`, `pending`, `fault`) and omits the nested `status: { ... }` object! Consequently, Tailwind generates **zero CSS** for those classes. When a user clicks "Add to Cart" on `ProductCard`, `bg-status-online text-bg` has a transparent background with `#060B18` dark text on a `#0D1635` dark card — rendering the button text invisible!
- **Exact Code Fix (`frontend/packages/config/tailwind.preset.js`):**
```javascript
        online: 'var(--status-online)',
        success: 'var(--status-online)',
        warning: 'var(--status-pending)',
        pending: 'var(--status-pending)',
        fault: 'var(--status-fault)',
        deal: 'var(--deal)',
        status: {
          online: 'var(--status-online)',
          success: 'var(--status-online)',
          pending: 'var(--status-pending)',
          warning: 'var(--status-pending)',
          fault: 'var(--status-fault)',
        },
```

---

### 🟠 [MW-P1-NEW-02] (HIGH) `SecurityHeadersMiddleware` `default-src 'none'` CSP Breaks Swagger UI (`/swagger`) & XML Comments Are Disabled
- **File / Location:** `backend/src/ShopVerse.Api/Middleware/SecurityHeadersMiddleware.cs` (Line 21), `backend/src/ShopVerse.Api/ShopVerse.Api.csproj`, `backend/src/ShopVerse.Api/Program.cs` (Line 74)
- **Flaw Explanation:**
  1. `SecurityHeadersMiddleware` runs before `app.UseSwaggerUI()` (`Program.cs:151` vs `164`) and sets `Content-Security-Policy: default-src 'none'` on every response. Opening `/swagger/index.html` in any browser fails completely because Swagger UI scripts, styles, and images are blocked by CSP.
  2. Phase 1 Deliverable #2 requires *"OpenAPI / Swagger documentation with XML comments and JWT bearer auth at /swagger"*, but `<GenerateDocumentationFile>true</GenerateDocumentationFile>` and `c.IncludeXmlComments(...)` are missing.
- **Exact Code Fix:**
```csharp
// 1. In backend/src/ShopVerse.Api/Middleware/SecurityHeadersMiddleware.cs:
var isSwagger = context.Request.Path.StartsWithSegments("/swagger") ||
                context.Request.Path.StartsWithSegments("/openapi");
headers.TryAdd("Content-Security-Policy", isSwagger
    ? "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'"
    : "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");

// 2. In backend/src/ShopVerse.Api/ShopVerse.Api.csproj (<PropertyGroup>):
<GenerateDocumentationFile>true</GenerateDocumentationFile>
<NoWarn>$(NoWarn);1591</NoWarn>

// 3. In backend/src/ShopVerse.Api/Program.cs (inside AddSwaggerGen):
var xmlFilename = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFilename);
if (System.IO.File.Exists(xmlPath))
{
    c.IncludeXmlComments(xmlPath);
}
```

---

### 🟠 [MW-P1-NEW-03] (HIGH) Unrestricted `ForwardedHeadersOptions` Allows Trivial Rate-Limit Bypass via `X-Forwarded-For` Spoofing
- **File / Location:** `backend/src/ShopVerse.Api/Program.cs` (Lines 56–62)
- **Flaw Explanation:**
  Clearing `KnownIPNetworks` and `KnownProxies` unconditionally without setting `options.ForwardLimit = 1` or checking the environment allows any external client to send `X-Forwarded-For: <random-ip>` on every request, bypassing the `100 req/min` `GlobalLimiter` IP partition.
- **Exact Code Fix (`backend/src/ShopVerse.Api/Program.cs`):**
```csharp
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    if (builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing")
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});
```

---

### 🟠 [MW-P1-NEW-04] (HIGH) `MechWareApiClient` Double-Reads Consumed Response Body Stream on Non-JSON Errors
- **File / Location:** `frontend/packages/api-client/src/index.ts` (Lines 55–70)
- **Flaw Explanation:**
  When `!response.ok`, `await response.json()` in the `try` block reads and locks the response `ReadableStream`. If the body is non-JSON (e.g., 502 Bad Gateway HTML from Nginx/Cloudflare or plain-text 429), `response.json()` throws `SyntaxError`, and the `catch` block immediately calls `await response.text()` on the already-consumed stream — throwing `TypeError: Body is unusable: Body has already been read`!
- **Exact Code Fix (`frontend/packages/api-client/src/index.ts`):**
```typescript
    if (!response.ok) {
      const rawBody = await response.text();
      let problem: ProblemDetails;
      try {
        problem = JSON.parse(rawBody) as ProblemDetails;
      } catch {
        problem = {
          title: response.statusText || 'HTTP Error',
          status: response.status,
          detail: rawBody || `Request failed with status ${response.status}`,
        };
      }
      throw new ApiError(problem);
    }
```

---

### 🟠 [MW-P1-NEW-05] (HIGH) Integration Tests Omit Security Header, Correlation-ID Sanitization, and `/health/ready` Verification
- **File / Location:** `backend/tests/ShopVerse.IntegrationTests/Api/ApiStartupIntegrationTests.cs`
- **Flaw Explanation:**
  `ApiStartupIntegrationTests.cs` only checks `/health/live` and `/unknown-route`. It never tests that `SecurityHeadersMiddleware` emits `Content-Security-Policy` / `X-Content-Type-Options: nosniff`, nor that `CorrelationIdMiddleware` rejects CRLF/malformed `X-Correlation-Id` inputs.
- **Exact Code Fix (`backend/tests/ShopVerse.IntegrationTests/Api/ApiStartupIntegrationTests.cs`):**
```csharp
    [Fact]
    public async Task SecurityHeaders_And_CorrelationId_Sanitization_AreEnforced()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "malicious_header_with_spaces !@#$");

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("X-Content-Type-Options", out var nosniff).Should().BeTrue();
        nosniff!.First().Should().Be("nosniff");
        response.Headers.Contains("Content-Security-Policy").Should().BeTrue();

        response.Headers.TryGetValues("X-Correlation-Id", out var corrId).Should().BeTrue();
        corrId!.First().Should().MatchRegex("^[a-f0-9]{32}$");
    }
```

---

### 🟡 [MW-P2-NEW-06] (MEDIUM) `Badge.tsx` Uses Raw Tailwind Colors That Fail Light-Mode Contrast & Bypass `lint-tokens.mjs`
- **File / Location:** `frontend/packages/ui/src/components/Badge.tsx` (Lines 38–51) & `frontend/packages/config/lint-tokens.mjs` (Line 19)
- **Flaw Explanation:**
  `Badge.tsx` uses `bg-amber-500/15 text-amber-400`, `text-sky-300`, `text-emerald-400`, and `via-indigo-500/20 to-purple-500/20`. These slipped past `lint-tokens.mjs` because its regex omitted `amber|sky|emerald|indigo|violet|rose|teal|cyan|orange|lime|fuchsia`. On Light Mode `#FFFFFF` surfaces, `text-amber-400` (`1.67:1`) and `text-sky-300` (`1.54:1`) fail WCAG 2.2 AA contrast.
- **Exact Code Fix:**
  1. Replace raw classes in `Badge.tsx` with semantic tokens (`warning`, `accent`, `online`, `deal`).
  2. Expand `RAW_TAILWIND_COLOR_REGEX` in `lint-tokens.mjs`.

---

### 🟡 [MW-P2-NEW-07] (MEDIUM) `PhoneInput.tsx` Controlled Value Desync & `motionPresets.ts` Reduced-Motion Support
- **File / Location:** `frontend/packages/ui/src/components/PhoneInput.tsx` (Lines 40, 87–90) & `frontend/packages/ui/src/motion/motionPresets.ts`
- **Flaw Explanation:**
  1. `PhoneInput.tsx` never updates `rawNumber` when the controlled `value` prop changes after mount, and changing the country dropdown does not call `onChange` with the newly selected country dial prefix.
  2. `motionPresets.ts` lacks a helper (`getReducedMotionVariants`) to disable transform/translate animations when `prefers-reduced-motion: reduce` is active (`02_DESIGN_SYSTEM.md` §2.5).

---

### 🔵 [MW-P0-NEW-08] (LOW) Duplicate `ix_otp_codes_lookup` Index in `docs/DATA_MODEL.md` & `--focus-ring` Hex Drift
- **File / Location:** `docs/DATA_MODEL.md` (Lines 184–188), `docs/02_DESIGN_SYSTEM.md` (Line 45), `frontend/packages/ui/tokens/colors.ts` (Line 76)
- **Flaw Explanation:**
  1. `docs/DATA_MODEL.md` lines 184–188 have two consecutive `**Indexes:**` sections on `otp_codes` defining `ix_otp_codes_lookup` twice with different column lists.
  2. `docs/02_DESIGN_SYSTEM.md` line 45 and `tokens/colors.ts` line 76 specify Light Mode `--focus-ring` as `#00A8B5`, whereas `globals.css` line 54 and `verify-contrast.mjs` use `#00707D` (WCAG AA `5.55:1`).

---

## 6. Ready-to-Paste Prompt for Antigravity (Final Pre-Phase-3 Patch)

Copy and paste the block below into **Antigravity** to resolve all 8 residual/regression items (`MW-P2-NEW-01` through `MW-P0-NEW-08`):

```markdown
We completed the deep Principal Architect & Staff Cybersecurity re-audit across Phase 0, Phase 1, and Phase 2 (commits `bee490b` and `b7c0d72`). All 26 previous items are verified fixed, but the deep test uncovered 8 residual/regression issues (`MW-P2-NEW-01` to `MW-P0-NEW-08`) that must be resolved before starting Phase 3:

1. **[MW-P2-NEW-01] (CRITICAL) Add `status.*` color namespace to `frontend/packages/config/tailwind.preset.js`:**
   - 11 components and pages (`ProductCard.tsx`, `Stepper.tsx`, `Toast.tsx`, `AlertBanner.tsx`, `PhoneInput.tsx`, `FileUploader.tsx`, `Badge.tsx`, `Avatar.tsx`, `Progress.tsx`, `apps/web`, `apps/console`) use `bg-status-online`, `text-status-online`, `border-status-online`, `ring-status-online`, `bg-status-pending`, `text-status-pending`, `text-status-fault`, and `border-status-fault`.
   - In `frontend/packages/config/tailwind.preset.js` under `theme.extend.colors`, add:
     ```js
     status: {
       online: 'var(--status-online)',
       success: 'var(--status-online)',
       pending: 'var(--status-pending)',
       warning: 'var(--status-pending)',
       fault: 'var(--status-fault)',
     },
     ```

2. **[MW-P1-NEW-02] (HIGH) Allow Swagger UI in `SecurityHeadersMiddleware.cs` & Enable OpenAPI XML Comments:**
   - In `backend/src/ShopVerse.Api/Middleware/SecurityHeadersMiddleware.cs`, check if `context.Request.Path.StartsWithSegments("/swagger") || context.Request.Path.StartsWithSegments("/openapi")`. If true, set `Content-Security-Policy` to `"default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'"`, otherwise keep `"default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'"`.
   - In `backend/src/ShopVerse.Api/ShopVerse.Api.csproj`, add `<GenerateDocumentationFile>true</GenerateDocumentationFile>` and `<NoWarn>$(NoWarn);1591</NoWarn>`.
   - In `backend/src/ShopVerse.Api/Program.cs` inside `AddSwaggerGen`, include the XML comments file via `c.IncludeXmlComments(xmlPath)`.

3. **[MW-P1-NEW-03] (HIGH) Harden `ForwardedHeadersOptions` in `backend/src/ShopVerse.Api/Program.cs`:**
   - Set `options.ForwardLimit = 1;` and only call `options.KnownIPNetworks.Clear(); options.KnownProxies.Clear();` when `builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing"` so production clients cannot spoof `X-Forwarded-For` to bypass rate limiting.

4. **[MW-P1-NEW-04] (HIGH) Fix Double Stream Read in `frontend/packages/api-client/src/index.ts`:**
   - In `MechWareApiClient.fetch()`, when `!response.ok`, read `const rawBody = await response.text();` once, then `JSON.parse(rawBody)` inside `try/catch` instead of calling `await response.json()` followed by `await response.text()` on an already-consumed stream.

5. **[MW-P1-NEW-05] (HIGH) Expand `backend/tests/ShopVerse.IntegrationTests/Api/ApiStartupIntegrationTests.cs`:**
   - Add an integration test verifying `SecurityHeadersMiddleware` (`X-Content-Type-Options: nosniff`, `Content-Security-Policy`) and `CorrelationIdMiddleware` (confirming an invalid `X-Correlation-Id` with spaces/special chars is sanitized and replaced with a 32-char hex GUID).

6. **[MW-P2-NEW-06] (MEDIUM) Replace Raw Tailwind Colors in `Badge.tsx` & Harden `lint-tokens.mjs`:**
   - In `frontend/packages/ui/src/components/Badge.tsx`, replace `amber-500`, `amber-400`, `sky-300`, `sky-500`, `emerald-500`, `emerald-400`, `indigo-500`, and `purple-500` with semantic tokens (`warning`, `accent`, `online`, `deal`).
   - In `frontend/packages/config/lint-tokens.mjs`, update `RAW_TAILWIND_COLOR_REGEX` to also block `amber|sky|emerald|indigo|violet|rose|teal|cyan|orange|lime|fuchsia` and gradient prefixes `from|via|to`.

7. **[MW-P2-NEW-07] (MEDIUM) Sync `PhoneInput.tsx` Controlled `value` Prop & Add Reduced-Motion Helper in `motionPresets.ts`:**
   - In `PhoneInput.tsx`, add a `React.useEffect` to sync `rawNumber` when `value` prop updates, and trigger `onChange` when the user switches countries in `<select>`.
   - In `frontend/packages/ui/src/motion/motionPresets.ts`, export a `getMotionVariants(variants, prefersReducedMotion)` helper that disables `x`/`y`/`scale` transforms when `prefers-reduced-motion: reduce` is enabled.

8. **[MW-P0-NEW-08] (LOW) Deduplicate `ix_otp_codes_lookup` in `docs/DATA_MODEL.md` & Sync `--focus-ring`:**
   - Remove the duplicate `**Indexes:**` block at `docs/DATA_MODEL.md` lines 186–188.
   - Update Light Mode `focusRing` in `frontend/packages/ui/tokens/colors.ts` (Line 76) and `docs/02_DESIGN_SYSTEM.md` (Line 45) from `#00A8B5` to `#00707D` to match `globals.css` and `verify-contrast.mjs`.
```
