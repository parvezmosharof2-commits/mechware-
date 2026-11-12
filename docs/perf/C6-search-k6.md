# C6.5 search load test (k6)

- **Date:** 2026-11-12
- **Machine:** developer laptop (Windows 11, Docker Desktop). Postgres, Redis, Meilisearch in Docker; Api and Worker run natively in Release, Development environment, log level Warning. k6 0.54.0 in Docker, over `host.docker.internal`.
- **Dataset:** the deterministic demo seed (`seed-catalog --no-media`), 544 products indexed (Active, not deleted).
- **Scenario** (`infra/k6/search.js`): ramp to 20 VUs in 30 s, hold 60 s, ramp down 10 s. Mix: 50 % `/api/v1/search?q=<word>` (inverter, motor, sensor, contactor, siemens, abb, schneider, plc, cable, breaker), 30 % `/api/v1/search?q=<real part number>` (read from `q=siemens` at setup), 20 % `/api/v1/search/autocomplete?q=<2-3 letters>`.
- **Local run only:** the StorefrontRead limiter (1,200 per minute per IP) would reject a single-IP load test, so the Api was started with `RateLimiting__StorefrontPermitPerMinute`, `RateLimiting__IpCeilingPerMinute` and `RateLimiting__GlobalPermitPerMinute` raised to 100000000. Code defaults are unchanged.

## Result (second run; the first was the same within noise)
| Metric | Value | Threshold |
|---|---|---|
| Requests | 174,046 (about 1,740 per second) | |
| p50 | 7.6 ms | |
| p90 | 14.6 ms | |
| p95 | 17.8 ms | < 150 ms: pass |
| p99 | 25.9 ms | |
| max | 2.4 s (single outlier, start-up/JIT) | |
| Failed requests | 0.00 % (0 of 174,046) | < 1 %: pass |
| Checks (status 200) | 100 % | |

**Verdict: PASS** (p95 17.8 ms against a 150 ms limit, no errors).

## Caveats
- 20 VUs on a laptop with 544 products. The brief's target of 2,000 to 5,000 concurrent users and a production-size catalogue is for H4 on real hardware; this report does not prove that.
- k6, Api and Meilisearch share one machine, so the numbers include contention from the load generator.

## Rerun
1. `docker compose -f infra/docker/docker-compose.yml up -d postgres redis meilisearch`
2. `dotnet run -c Release --project backend/src/Mechware.Worker -- seed-catalog --no-media`
3. Start the Worker (`DOTNET_ENVIRONMENT=Development`) and ask for an index build with `-- rebuild-search`.
4. Start the Api (`ASPNETCORE_ENVIRONMENT=Development`, `ASPNETCORE_URLS=http://0.0.0.0:5000`, the three `RateLimiting__*` variables above). The Api's default database is `mechware_db`, the Worker's is `mechware`: set `ConnectionStrings__DefaultConnection` on the Api to the Worker's database.
5. Wait until `/api/v1/search?q=inverter` returns hits, then `docker run --rm -i -e BASE_URL=http://host.docker.internal:5000 grafana/k6:0.54.0 run - < infra/k6/search.js`
