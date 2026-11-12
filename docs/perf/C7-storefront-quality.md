# C7.8 storefront quality (search + PDP), 2026-11-12

Laptop numbers (Windows, production `next start`, local API/Postgres/Meilisearch, Lighthouse 12 mobile, simulated 4G + 4x CPU). Single runs; +-5 points of noise.

| Page | Perf | A11y | Best pr. | SEO | LCP (sim) | TBT | CLS |
|---|---|---|---|---|---|---|---|
| /search?q=inverter (en) | 81 | 100 | 100 | 63* | 3.6 s | 380 ms | 0 |
| /bn/search?q=inverter | 77 | 100 | 100 | 63* | 3.6 s | 510 ms | 0 |
| /products/<hager-mbn110...> (en) | 84 | 100 | 93** | 100 | 3.4 s | 320 ms | 0 |
| /bn/products/<same> | 79 | 100 | 93** | 100 | 3.2 s | 530 ms | 0 |

\* `is-crawlable` fails on purpose: search results carry `noindex,follow` (C7.2). Not a defect.
\*\* Seed product images are blocked: the API sends `Cross-Origin-Resource-Policy: same-origin` (backend `SecurityHeadersMiddleware`), and localhost:3000 vs :5000 is cross-origin. Backend change (CORP `cross-origin` for `/seed-media` and media routes) or a same-origin media proxy; not touched here. With no image the LCP element is the H1/first card.

Real LCP (Chromium, 360 px, 4x CPU throttle, no network throttle, PerformanceObserver): 0.5-0.9 s on all four pages. Lighthouse devtools-throttling on the PDP: LCP 2.2 s, Perf 75, TBT 970 ms.

## Exit bars
- Lighthouse Perf >= 90: NOT met (75-84). A11y, best practices (except **), SEO (except *) met.
- LCP < 2.5 s: met in a real browser and with devtools throttling; Lantern simulation says 3.2-3.6 s.
- Route JS < 170 kB: met. `pnpm --dir frontend check:bundle` exit 0; first load /search 148 kB, PDP 145 kB gzip.
- axe: 0 violations (wcag2a/aa, 2.1 a/aa, best-practice) on /search (results), /search (empty), PDP, en+bn, 360+1280 px.
- E2E: `tests/storefront.mjs` 58/58 (search -> brand filter -> URL -> product -> variant switch changes part number and `?v=` -> back restores filters; Bangla digits in PDP price; no horizontal overflow; `html lang`).

## Fixes made
PDP: copy button moved inside the `dd` (axe `definition-list`). EmptyState got `headingLevel`; search empty state uses h2 (axe `heading-order`). `metadataBase` in the locale layout makes canonical/hreflang absolute (PDP SEO 85 -> 100).

## Rerun
Start Postgres/Redis/Meilisearch (docker compose), API on :5000 (Development, `RateLimiting__GlobalPermitPerMinute=5000`), then
`NEXT_PUBLIC_API_BASE_URL=http://localhost:5000 pnpm --filter @mechware/web build && ... start`, then
`E2E_STOREFRONT=1 pnpm --filter @mechware/e2e test:storefront` (env: `E2E_BRAND_SLUG`, `E2E_MULTI_VARIANT_SLUG`, `E2E_QUERY`).
Not in `pnpm test` and exits 0 ("SKIP") without `E2E_STOREFRONT=1`: the CI e2e job has no Meilisearch and no catalogue seed. axe-core comes from `@mechware/ui` devDependencies.
Lighthouse (no dependency added): `CHROME_PATH=<playwright chromium> npx lighthouse@12 <url> --form-factor=mobile --chrome-flags="--headless=new --no-sandbox"`.

## Known gaps
- Perf < 90: main-thread cost is the framework runtime (shared chunk 363, ~100 kB; 400-550 ms TBT under 4x CPU) plus a 45 kB Radix chunk (86% unused on first view, Lighthouse "unused JS"). `optimizePackageImports` made no size difference. Real fix: split `@mechware/ui` barrel / drop Radix from first load, or static + ISR pages instead of `force-dynamic` (needed today for the CSP nonce).
- Media CORP issue above; seeded images are SVGs, so no real LCP image tuning was possible.
- Single-run laptop numbers; run Lighthouse in CI on a quiet runner before treating 90 as a gate.
