# Full test report (review prompt R1 + R3 + R4, whole repository)

Environment: Ubuntu container, .NET 10.0.114, Node 22, PostgreSQL 16, Redis 7, Chromium (Playwright). Date 2026-10-29.

## Automated suites

| Suite | Result |
|---|---|
| `dotnet test` (Release) — unit | **130 / 130 pass** |
| `dotnet test` (Release) — integration (PostgreSQL + Redis + RedLock, API in-process) | **42 / 42 pass**, 0 skipped |
| `dotnet format --verify-no-changes` | clean |
| Frontend `lint:tokens` | pass |
| Frontend `eslint` (newly added — `pnpm lint` was a dead script) | 0 errors, 33 unused-import warnings in `design-system/page.tsx` |
| Frontend `typecheck` | pass (was failing on `main`) |
| `@mechware/ui` tests (26 component tests, 13 axe tests, contrast matrix) | pass |
| Frontend `build` (web + console) | pass |

## Runtime smoke test (API on :5000 with the seeded database)

`/health/live` 200 · `/health/ready` 503 only because MinIO is not running in the sandbox (PostgreSQL and Redis healthy) · `swagger.json` 200 ·
storefront categories / category listing / product-by-slug 200 with `Cache-Control`, `X-Content-Type-Options`, `X-Frame-Options`, CSP and correlation-id headers ·
unknown slug 404 problem+json · wrong password 401 with a generic message.

## Browser matrix (Playwright + axe-core): 9 routes × 4 widths (360 / 768 / 1280 / 1920) × light/dark × EN/BN = 72 page loads

Routes: `/`, `/bn`, `/design-system`, `/bn/design-system`, `/auth/login`, `/auth/register`, `/auth/forgot-password`, `/bn/auth/login`, `/account/addresses`.

### Defects found and fixed in this pass (all pre-existing, none from Phase 4)

| Sev | Where | Defect | Fix |
|---|---|---|---|
| **Critical** | `apps/web`, `apps/console` | **No `postcss.config.js`: Tailwind was never compiled, so every production page rendered unstyled** (55 KB CSS vs 9 KB of raw directives). | Added `postcss.config.js` (+ autoprefixer) to both apps. |
| **High** | `AuthModal.tsx` + Tailwind preset | Auth screens used a token vocabulary (`bg-bg-canvas`, `fg-primary`, `brand-primary`, `border-subtle`, `state-danger`…) that did not exist, so inputs were white-on-white (contrast 1.04), the primary button had no fill, the modal scrim was missing. | Added the aliases to `tailwind.preset.js`, mapped onto the semantic tokens. |
| **High** | `AuthModal.tsx` | Sign-in form shipped **pre-filled with a demo account** (`buyer@mechware.com.bd`), a fake reference id, and showed JWT/cookie implementation details to users. | Defaults cleared; notice rewritten (EN + BN). |
| **High** | 3 auth pages, `api-client`, `addresses/page.tsx` | CI red since Phase 3: wrong relative imports, `api-client` pointing at an unbuilt `dist/`, a page exporting a non-page constant. | Fixed (see PR). |
| High | Auth + addresses forms | 18 inputs/selects without associated labels (axe `label`, critical). | `htmlFor`/`id` pairs. |
| Medium | Auth pages | No `<h1>` on any full-page auth screen. | Title renders `h1` on pages, `h2` inside dialogs. |
| Medium | `Progress`, design-system | Progress bars unnamed and rendered as indeterminate; icon-only select/theme buttons unnamed or label-mismatched. | `value` + default `aria-label`; labels fixed. |
| Medium | dark theme | `--text-subtle` 3.7–4.1:1; destructive button white on `#F87171` 2.8:1. | `--text-subtle` → `#8393AB` (≥ 5.4:1); destructive uses dark text in dark mode. |
| Low | `DataTable`, `Tabs` | Scroll regions not keyboard-focusable; tab list overflowed phones. | `tabIndex`/`role="region"`; `max-w-full overflow-x-auto`. |
| Low | `launchSettings.json` | API listened on 5095 while README, API contract and `api-client` all say 5000. | Port set to 5000. |

### Still open (documented, not fixed)

* `/design-system` (internal component gallery): horizontal scroll at 360 and 768 px (a payment radio-card row) and a few remaining dark-theme contrast nits — not user-facing.
* All 72 loads return 200; axe violations across the five user-facing pages: **none**. Remaining violations are limited to `/design-system`.
* The home page and design-system hot-link **images from Unsplash** (blocked in this sandbox, so 176 images failed to load here). External hot-linking is fragile; host them yourself before launch.
* Every page has the same `<title>`; add per-route metadata (SEO brief §12).
* Product/category/PDP/cart/checkout storefront screens do not exist yet (Phases 6–8), so R2 design critique and Lighthouse on those pages are not possible.
* The 33 ESLint warnings (unused imports in the gallery page).
