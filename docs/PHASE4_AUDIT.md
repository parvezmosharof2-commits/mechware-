# Phase 4 audit (R1 sign-off + R3 security + R4 performance)

Scope: `backend/` catalogue, inventory, pricing, outbox, storefront read API and seed. Method: code read-through against
`docs/01_PROJECT_BRIEF.md` §6–8, then regression tests for every fixed finding (`SecurityHardeningTests`, `CatalogHardeningTests`).
Frontend design review (R2) and Lighthouse (R4) are **not applicable yet**: Phase 4 ships no UI.

## Audit matrix

| # | Severity | Location | Flaw | Fix | Status / test |
|---|---|---|---|---|---|
| 1 | **High** | `InventoryService.ReserveAsync`, `ReservationLine` | Caller supplied `LockedUnitPriceMinor`: client price trust (brief §8.1). | Price removed from the contract; service reads the variant, applies `PricingEngine` (sale window + category VAT) and locks that. | Fixed · `ReservedUnitPrice_ComesFromTheCatalogue_NotFromTheCaller` |
| 2 | **High** | `InventoryService` | Draft, rejected, archived, deleted products and inactive variants could be reserved. | Reservation joins variant→product→category and returns `Unavailable` for anything not Active. | Fixed · `DraftOrArchivedProducts_CannotBeReserved`, `UnknownVariant_IsUnavailable` |
| 3 | **High** | `Product.DescriptionHtml` | Seller-authored rich text stored verbatim: stored XSS once rendered. | Allow-list sanitizer (`RichTextSanitizer`, HtmlSanitizer) on construct/update; no script, event handlers, iframe, img, `javascript:`. | Fixed · `RichText_IsSanitized_OnConstructionAndUpdate` |
| 4 | **High** | media / datasheet / logo / banner / review-photo URLs | Any string accepted (`javascript:`, `data:`, `//host`, plain http). | `SafeUrl`: https or site-relative only; blur placeholders restricted to tiny `data:image/*;base64`. | Fixed · `SafeUrl_*`, `Entities_RefuseUnsafeUrls` |
| 5 | Medium | `InventoryService` idempotency | Retrying a reference with *different* lines silently returned the old hold. | Compare lines; mismatch → `ConflictException` (HTTP 409). | Fixed · `ReusingAReferenceWithDifferentLines_IsRejectedAsConflict` |
| 6 | Medium | `InventoryService` | Unbounded TTL, quantity (int overflow on sum), line count: stock-hoarding / DoS. | TTL ≤ 30 min, qty 1–10 000 (summed in `long`), ≤ 50 lines. | Fixed · `AbusiveRequests_AreBounded` |
| 7 | Medium | `SlugHistory` | Renaming A→B→A→C violated the unique history index; no guard against taking another entity's live slug; category rename left descendant paths stale. | `CatalogSlugService` (transactional): drops shadowing history, rejects live conflicts (409), rebases descendants. | Fixed · `SlugCanReturnToAnOldValue…`, `SlugOwnedByAnotherProduct…`, `CategorySlugChange…` |
| 8 | Medium | Product / Category / Variant | No optimistic concurrency: two admins silently overwrite each other. | `xmin` concurrency token; middleware maps `DbUpdateConcurrencyException` → 409. | Fixed · `ConcurrentProductEdits_SecondWriterGets409…` |
| 9 | Medium | `PricingEngine` | If the regular price was lowered below a scheduled sale price, the "sale" *raised* the price. | Sale only applies when `sale < price`; `ChangePrice` rejects conflicting values; sale timestamps must be UTC. | Fixed · `Sale_NeverRaisesThePrice…`, `ChangePrice_Rejects…` |
| 10 | Medium | EF migrations | Phase 3 migration file was never wired (no `[Migration]`), so nothing created the schema. | `Phase4CatalogInventoryOutbox` carries the baseline; `Phase4AuditHardening` adds the concurrency tokens. | Fixed · `MigrationsApplyToAnEmptyDatabase_AndMatchTheModel` |
| 11 | Medium | `StorefrontCatalogReader` | Random slugs create 30 s negative-cache keys and a DB lookup each (cache-penetration). | Slug regex gate + per-IP rate limit (1 200/min) already bound it; no per-key lock. | **Accepted**: add singleflight/Bloom filter if abuse is observed |
| 12 | Low | `InventoryItem` is `[NotAudited]` | Admin stock corrections will not appear in `audit_logs`. | Needed to keep the hot row out of the audit trail; admin adjustment endpoint (Phase 11) must write its own audit entry. | **Open** → Phase 11 |
| 13 | Low | Storefront price mode | Reader always renders VAT-inclusive; brief wants tax-exclusive for international buyers. | `TaxMode` exists in the engine; the API needs a buyer-region input. | **Open** → Phase 8 |
| 14 | Low | Outbox | Multiple publishers are not atomic; a failure re-delivers to earlier publishers (at-least-once). Poison messages stop after 10 tries with no alert. | Message ids are stable so consumers de-duplicate; add a dead-letter metric in Phase 14. | **Accepted / Open** |
| 15 | Low | Cache stampede on a cold hot product | Many concurrent misses hit PostgreSQL. | Compiled query + indexed slug lookup (0.03 ms); add a Redis lock only if profiling shows need. | **Accepted** |
| 16 | Info | Seed data | Part numbers/specs written from product knowledge; images are generated illustrations; reviews are synthetic. | Flag in `PROGRESS.md`; engineer must verify against datasheets before real listings. | **Open** (business) |

R3 checklist for this phase: IDOR/broken access control – no authenticated endpoints added (public read only); price/quantity tampering – #1, #6; coupon/webhook/payment – not in scope; injection – all SQL parameterised (`FromSql` interpolation, `ANY(@ids)`), slug regex gates Redis keys; XSS – #3, #4 (frontends must still escape specs/highlights/reviews as plain text); SSRF – no server-side fetch of user URLs; rate limits – storefront policy; verbose errors – existing middleware hides details outside Development.

## R4 – performance evidence

| Measurement | Result |
|---|---|
| Product-by-slug, Redis hit, sequential ×2 000 (in-process TestServer, full middleware) | p50 1.4 ms · **p95 2.0 ms** · p99 2.3 ms |
| Same, 16 concurrent ×3 200 | p50 6.6 ms · **p95 13 ms** · p99 18 ms |
| `EXPLAIN ANALYZE` slug lookup | Index scan `ux_products_slug`, 0.03 ms |
| `EXPLAIN ANALYZE` variant stock sum | Index scans, 0.08 ms |
| `EXPLAIN ANALYZE` category listing (572 products) | 0.9 ms (tables are small; sequential scans are expected at this size) |
| Seed | ≈ 5 s |

Numbers are in-process on a shared dev VM, not over a network or on production hardware; repeat with k6 in Phase 14.
