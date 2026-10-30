# @mechware/e2e

Browser checks that drive the real stack: the API on PostgreSQL, the storefront (`apps/web`) and the staff
console (`apps/console`). They run in CI as the `e2e` job (`.github/workflows/ci.yml`) and fail the PR if a step
breaks. Screenshots, and the API / web / console logs, are uploaded as the `e2e-artifacts` artifact.

- `tests/web.mjs`: sign-in redirect, company registration, sites, cost centres, members, tenant isolation, Bangla at 360 px.
- `tests/console.mjs`: staff sign-in with TOTP, buyer refused, verify / suspend / credit, suspended company blocked.

They rely on the Development seed accounts of the in-memory identity store and on a **fresh database**
(`web.mjs` expects its seeded owner to have no company yet). Registered companies use unique BINs.

## Run locally
```bash
# 1. PostgreSQL with the shopverse user, then the schema
SHOPVERSE_DESIGN_DB="Host=localhost;Database=shopverse;Username=shopverse;Password=shopverse_secret" \
  dotnet ef database update --project backend/src/ShopVerse.Infrastructure --startup-project backend/src/ShopVerse.Infrastructure
# 2. API (Development) on :5000, web on :3000, console on :3001
RateLimiting__GlobalPermitPerMinute=2000 ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project backend/src/ShopVerse.Api --urls http://localhost:5000
cd frontend && pnpm --filter @mechware/web build && pnpm --filter @mechware/web start
cd frontend && pnpm --filter @mechware/console build && pnpm --filter @mechware/console start
# 3. browsers once, then the tests
cd frontend && pnpm --filter @mechware/e2e exec playwright install chromium
cd frontend/packages/e2e && pnpm test
```
Override `E2E_WEB_URL`, `E2E_CONSOLE_URL`, `E2E_API_URL`, `E2E_SHOTS_DIR`; set `PW_CHROMIUM_PATH` to use a
browser installed outside Playwright's own cache.
The API needs `RateLimiting__GlobalPermitPerMinute` raised for a run: the suites send about 150 requests a minute from one IP, and the default limit (100 a minute) queues the rest, which shows up as 30 s stalls. Truncate `companies` (cascade) between local runs.
