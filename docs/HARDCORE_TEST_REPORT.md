# Hard-core Test Layer — Report (2026-10-30)

Goal: go beyond example-based tests and attack the system the way production traffic and attackers
will — random inputs, forged credentials, every route, and real concurrency against PostgreSQL.

**Result:** 290 tests (222 unit + 68 integration), all green. Before: 172. The new tests found
**6 real defects**, all fixed in this change.

## 1. What was added

| Suite | File | What it proves |
|---|---|---|
| Architecture | `UnitTests/Architecture/LayeringTests.cs` | Domain has no I/O or framework references; Application does not reference Infrastructure/Api; controllers inject only Application ports; controllers sealed; every domain exception maps to a 4xx |
| Money & pricing properties | `UnitTests/Properties/MoneyAndPricingPropertyTests.cs` | Minor-unit round-trip, banker's rounding, algebraic laws, `Distribute` never loses a poisha, mixed currency rejected; for 20,000 random price/sale/VAT/time inputs: `net + tax = gross`, sale never raises price, half-open sale window, discount = floor %, determinism; VAT extraction drift ≤ ½ poisha |
| Input hardening fuzz | `UnitTests/Properties/InputHardeningFuzzTests.cs` | 47 OWASP XSS payloads + 3,000 random nested markup documents re-parsed as a browser would: only allow-listed tags/attributes, only `https`/`mailto`/`tel`/relative links. 20,000 random Unicode strings for slugs and URLs |
| JWT attacker model | `UnitTests/Identity/JwtTamperingTests.cs` | Payload privilege escalation, foreign key, `alg:none`/`HS512`/`RS256` confusion, `crit`, expiry edge, missing/wrong `iss`/`aud`, `nbf`/future `iat`, signed-but-malformed payloads, 20,000 mutated/garbage tokens, oversized token |
| API security regression | `IntegrationTests/Security/ApiSecurityRegressionTests.cs` | Every route is protected or on a reviewed public allow-list (stale entries fail too); credential routes use the strict limiter; every protected route answers 401 to 7 forged/missing credential variants; hostile bodies/queries/paths never 500 or leak internals; Production refuses the published dev JWT secret or a missing one; Swagger hidden in Production; 6th login attempt per minute → 429 |
| Inventory chaos | `IntegrationTests/Catalog/InventoryChaosTests.cs` | 160 buyers (48 in flight) reserve random 1–3-line baskets in random order while others commit/cancel and an expiry sweeper races them; afterwards `reserved = Σ active holds`, `onHand = initial − Σ fulfilled`, `0 ≤ reserved ≤ onHand`, no exception escaped, short holds all expired |

All randomised tests use fixed seeds; a failure message prints `seed` and `iteration` so it can be
replayed exactly.

Mutation check: removing `[Authorize]` from a controller, disabling the secret guard, and enabling
Swagger in Production each made the security suite fail. Restoring the old sweeper made the chaos
test fail 3 out of 3 runs (deadlock); the fixed version passed 6 out of 6.

## 2. Defects found and fixed

| # | Severity | Defect | Fix |
|---|---|---|---|
| 1 | **Critical** | `appsettings.json` (loaded in every environment, repo is public) contained the JWT signing secret and MinIO keys. A production deploy without an override would accept tokens anyone can forge, including admin tokens. | Secrets moved to `appsettings.Development.json`; base values empty, so Production fails validation without a real secret; Production also refuses the published value explicitly. **Treat the old value as compromised; never use it anywhere.** |
| 2 | **High** | Inventory expiry sweeper locked reservation rows before inventory rows — the opposite order of commit/cancel → PostgreSQL deadlock (40P01) under load; one side's request failed. | Sweeper now locks inventory first with `SKIP LOCKED` (never blocks checkout), then expires only holds on the balances it locked. |
| 3 | **Medium** | JWT validator accepted tokens without `iss`/`aud`, ignored the header `alg`/`crit`, had no `nbf`/future-`iat` check, accepted non-canonical base64, had no size limit, and could throw on non-string claims. Not exploitable without the key, but ASVS V9 requires these checks. | Validator rewritten: exact `alg=HS256`, `typ` if present, no `crit`, mandatory `iss`/`aud`/`exp`, `nbf`/`iat` with 60 s skew, canonical base64url, 8 KB cap, never throws. |
| 4 | **Medium** | Rich-text sanitiser kept protocol-relative (`//evil.example`) and backslash (`\\evil.example`) links in seller descriptions — off-site phishing links from a trusted page. | `FilterUrl` hook drops protocol-relative and backslash URLs. |
| 5 | **Medium** | Swagger UI and the full API map were served in Production. | Enabled only in Development (or with `Swagger:Enabled=true`). |
| 6 | **Low** | `SafeUrl.IsSafe` crashed on `file://` + U+200E: **`Uri.TryCreate` in .NET 10 throws `IndexOutOfRangeException`** for this input (framework bug). `Slug.From` threw `ArgumentException` on unpaired surrogates. Both would surface as HTTP 500. | `SafeUrl` accepts printable ASCII only (also blocks bidi spoofing characters) and guards the parser; `Slug` strips lone surrogates before normalisation. |

Also fixed in this change: `next-intl` 3.26 → 4.14 (open redirect, prototype pollution advisories),
PostCSS forced to ≥ 8.5.32 inside Next (two high advisories), `next`/`react` floors raised to patched
releases. `pnpm audit` and NuGet vulnerability scan: zero known vulnerabilities.

## 3. CI additions

- `.github/workflows/security.yml`: CodeQL (`csharp`, `javascript-typescript`, security-extended
  queries) on push/PR/weekly; dependency gate failing on any vulnerable NuGet package or
  high/critical npm advisory.
- `.github/dependabot.yml`: weekly NuGet, npm and GitHub Actions updates.
- CI uses pnpm 10 (the version the lockfile was produced and verified with).

## 4. Still open (needs the owner or later phases)

- Enable **branch protection** on `main` and **GitHub secret scanning + push protection**
  (Settings → Code security). A gitleaks job was not added because it could not be validated
  locally in this session.
- Playwright E2E journeys and k6 load tests belong with checkout (Phase 8+).
- The 33 existing ESLint warnings (unused variables) in `apps/web` are untouched.
