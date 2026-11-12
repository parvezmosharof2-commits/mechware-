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

## C7.8b perf pass (2026-11-12): all four pages >= 90
Median of 3 clean runs (nothing else running; Lighthouse 12 mobile, simulated 4G + 4x CPU, production `next start`, local API). Before = C7.8 table above (single runs); a re-measure of /search en before any change gave 87-88 (TBT 200 ms, LCP 3.4 s).

| Page | Perf before | Perf after (median) | LCP (sim) | TBT |
|---|---|---|---|---|
| /search?q=inverter (en) | 81 | 92 (92, 92, 93) | 3.05 s | 119 ms |
| /bn/search?q=inverter | 77 | 92 (92, 92, 94) | 3.06 s | 145 ms |
| PDP (en) | 84 | 91 (83, 91, 93) | 3.1 s | 164 ms |
| /bn PDP | 79 | 93 (93, 93, 94) | 3.0 s | 71 ms |

What moved the needle (in order):
1. **Deep imports from `@mechware/ui`** in `apps/web` (`@mechware/ui/src/components/X`). `layout.tsx` imported `ToastProvider` from the barrel, and the barrel was not tree-shaken, so every page loaded Select, DropdownMenu, Tooltip, Accordion, Dialog, Popover (about 290 kB raw JS removed from the root layout; this was the "45 kB Radix chunk"). `"sideEffects": false` was already set; the barrel stays for the console and design-system page.
2. **Server-rendered rows on /search**: `ResultsTable`/`ResultsList` are Server Components passed to `SearchView` as `results`; the client island gets only meta + brand names (no hit rows to hydrate).
3. **Server-rendered PDP sections**: `ProductSections` (description, specs, documents) and `LifecycleBanner` are Server Components passed as `sections`/`banner`; `ProductView` keeps only the variant-dependent availability section and gets the product without description/specs/documents.
Tried and dropped: `preload: false` on the local fonts (no change).

Variance is +-4 points between runs; two runs overlapping on the same CPU dropped scores to 65-82, so run Lighthouse one at a time. The remaining gap to 100 is simulated LCP (about 3 s vs 0.5 s observed): Lantern charges the whole shared framework chunk against LCP.

## H1.4 storefront gate (2026-11-13): all pages x locales
Median of 3 (Lighthouse 12 mobile, simulated 4G + 4x CPU, `next start`, local API, one run at a time on an idle CPU).

| Page | Perf | A11y | Best pr. | SEO | LCP (sim) | TBT |
|---|---|---|---|---|---|---|
| / (en) | 95 | 100 | 100 | 100 | 2.8 s | 80 ms |
| /bn | 92 (92, 93, 91) re-run | 100 | 100 | 100 | 2.8 s | 219 ms |
| /search?q=inverter (en) | 93 | 100 | 100 | 63* | 3.1 s | 84 ms |
| /bn/search?q=inverter | 92 | 100 | 100 | 63* | 3.1 s | 157 ms |
| PDP (en) | 93 | 100 | 93** | 100 | 3.2 s | 49 ms |
| /bn PDP | 93 | 100 | 93** | 100 | 3.2 s | 49 ms |
| /help (en) | 94 | 100 | 100 | 100 | 2.8 s | 144 ms |
| /bn/help | 95 | 100 | 100 | 100 | 2.8 s | 98 ms |
| /legal/terms (en) | 94 | 100 | 100 | 66* | 2.8 s | 117 ms |
| /bn/legal/terms | 95 | 100 | 100 | 66* | 2.7 s | 70 ms |

\* `is-crawlable` fails on purpose (search `noindex,follow`; draft legal pages `noindex` until `LEGAL_APPROVED`). \*\* seed images blocked by CORP (see above).
Note: the first sweep had the owner's heavy `Bench.exe` running on the machine and scored 72-81; the table is the sweep on an idle CPU. /bn home came in at 86 (86, 86, 95) in that sweep; the main session re-ran it on a quieter CPU (`next start`, with the account-menu session-hint change): /bn 92 (92, 93, 91), en 94 (93, 93, 95, 94, 94).

**Cause of "meta description missing / noindex not seen" (Problem 1).** Next 15.5 streams metadata (`<meta name=description|robots>`, canonical, hreflang) into `<body>` for every user agent that does not match `htmlLimitedBots` (a list of old bots); Googlebot also got it in the body. curl showed the tags, but they were after `</head>`, so Lighthouse did not count them. Fix: `htmlLimitedBots: /.*/` in `next.config.ts` makes metadata blocking (in `<head>`) for all agents. Cost: negligible, since pages are already `force-dynamic`; SEO score on /help and /legal went 91 -> 100 (legal 66 is the intended noindex). E2E (`storefront.mjs`, `headChecks`) now fetches /, /search, PDP, /help, /legal/terms (en+bn) with a browser UA and asserts description, canonical and robots are inside `<head>` and none after it.

**Perf fix (Problem 2).** The language switch is a server component (the request path+query comes from `x-mw-url`, set by middleware), and the account menu is a lazy client island (`dynamic`, ssr false) mounted at idle, with the sign-in button as placeholder (a null `loading` caused a 0.16 CLS header shift; fixed). TBT on /help went from 160-210 ms to 60-140 ms, on home from ~545 to 80 on a quiet CPU.

## Exit bars
- Lighthouse Perf >= 90: met after C7.8b (91-93; before 77-84). A11y, best practices (except **), SEO (except *) met.
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
- (Resolved by C7.8b, see above) Perf < 90: main-thread cost is the framework runtime (shared chunk 363, ~100 kB; 400-550 ms TBT under 4x CPU) plus a 45 kB Radix chunk (86% unused on first view, Lighthouse "unused JS"). `optimizePackageImports` made no size difference. Real fix: split `@mechware/ui` barrel / drop Radix from first load, or static + ISR pages instead of `force-dynamic` (needed today for the CSP nonce).
- Media CORP issue above; seeded images are SVGs, so no real LCP image tuning was possible.
- Single-run laptop numbers; run Lighthouse in CI on a quiet runner before treating 90 as a gate.
