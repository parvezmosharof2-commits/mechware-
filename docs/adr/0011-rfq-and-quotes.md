# ADR 0011: RFQs and versioned quotes

## Status
Accepted 2026-11-17 (unit D4.1, design only, written by an AI assistant, not reviewed by a person). The defaults in §15 were
**decided 2026-11-17 under the owner's delegation** ("tomar jeta valo money hoi koro": the assistant decides sensible
defaults and records them). They are options or data, not code, unless §15 says otherwise. Real legal text, letterhead
facts and the holiday calendar stay the owner's (§15 b). Nothing in the build waits for them.

## Date
2026-11-17

## Context
Brief §8.2: quote prices are fixed for the validity (default 15 days). Accepting an expired quote is refused. Acceptance
creates an order at the snapshot prices and re-checks stock. Every change creates a new version. Sent versions are immutable.
A price below the floor needs a manager's approval and a reason. Brief §7: `Rfq` (source cart / BOM / free text), `Quote /
QuoteVersion / QuoteLine` (number `MWQ-YYMMDD-XXXX`). Threat model v2 §৪ already fixes the rules Q1–Q9. §১৩ of the same
document now lists the D4 abuse cases QT1–QT17. Their test names are the exit tests of the units in `docs/modules/D4.md`.

**What exists (read from the code, 2026-11-17):**
- `PriceSource.Quote = 6` already exists. `OrderService.ApplyPromotionsAsync` already keeps `Quote` lines away from promotions
  (ADR-0008 §6). `ck_order_items_price_snapshot` already accepts source `Quote` without a price-list reference. It needs
  `price_basis` and a public price > 0.
- ADR-0008 §5: an order made by accepting a quote is **not resolved**. Each line takes the snapshot price with source `Quote`,
  even if something else is lower. §2: quote lines are **VAT-exclusive**, and a VAT-rate change moves only the VAT line.
  §4: `MarginFloorPolicy.EvaluateEntry(entryId, netUnitPrice, facts, floors)` was written to be reused. It is fail-closed,
  the floor is rounded up, and it covers below cost.
- `OrderService.PlaceAsync`: per-buyer and cost-centre advisory locks, an Idempotency-Key with a request hash, a verified
  account, the open-unpaid cap (3), the Buyer role of an Active company, the approval rule on the server total, the early
  credit-hold check, the spend limit and budget, stock reservation (30 min unpaid, 24 h 30 min while waiting for approval),
  then `SaveAsync` (price lists `FOR SHARE` → companies `FOR UPDATE` + `ChargeCreditAsync` (limit + hold) → promotions) and
  `ExpectedGrandTotalMinor` (409 `PRICE_CHANGED` when the server total is higher).
- ADR-0010 §7 and its E7.2 note set the global lock order: gateway `payments` → `orders` (`FOR NO KEY UPDATE`) →
  `credit_payments | credit_adjustments` → `companies` → `promotions`. Ledger foreign keys take `FOR KEY SHARE`, so a path
  that names a row through a foreign key must lock it `FOR NO KEY UPDATE`, never `FOR UPDATE` (B3.5).
- A8: `IPdfRenderer.Render(PdfDocumentModel) → PdfResult(Bytes, Sha256Hex, PageCount)` gives identical bytes for the same
  input (ADR-0009: store the file, not only the hash). C3: `IObjectStorage` (Put / OpenRead / Delete, server-chosen keys).
  Only `LocalObjectStorage` exists. The S3 adapter is C3.1.
- F3a: `NotificationOutboxPublisher` reads recipients at send time, and payloads carry ids only. The template `quote-sent`
  (EN/BN: customerName, quoteNumber, total, validUntil, quoteUrl) exists and is not wired yet.
- D5.1 `QuickOrderLines.Parse` (≤ 500 lines, Bangla digits, per-line codes). D5.2 `IQuickOrderValidator` (matching by part
  key or SKU, ambiguous and obsolete lines, public prices). E1 `ICartService.GetAsync(owner)`. E8 refuses heavy or oversize
  items with 422 `SHIPPING_QUOTE_ONLY`. `PlaceOrderRequest.MaxLines = 50`, `MaxQuantityPerLine = 10,000`.
- Module boundary (`ModuleBoundaryTests`): Infrastructure modules talk only through Application ports or `Persistence`.

## Decision

### 1. Overview
```
RFQ  (buyer)   Submitted ─► Quoting ─► Answered ─► Closed (staff, reason shown)        Withdrawn (buyer, any time)
Quote (staff)  one company, optional RFQ, versions 1, 2, 3 …; at most ONE open (Draft | PendingApproval) version
               and at most ONE live (Sent | Accepted) version at a time
Version        Draft ──send, no flag──────────────────────► Sent ──accept──► Accepted ──(order lapsed before confirmation)──┐
                 │ send with flags (§5)                    │  ├─reject (buyer)───► Rejected                                 │
                 ▼                                         │  ├─withdraw (staff)─► Withdrawn                                │
               PendingApproval ──approve = send (§5)──────►│  └─newer version sent► Superseded       acceptable again ◄─────┘
                 └─reject approval / recall ─► Draft       └─ "Expired" = Sent and now ≥ valid_until (computed, never stored)
```
The approval **sends** the version. There is no "approved, not yet sent" state, so an approved quote reaches the buyer at
once (KPI: first quote within 4 working hours). Expiry is computed, as price-list status is in ADR-0008 §1. Nothing flips
at midnight, so no worker can be late and no acceptance can race with one.

### 2. RFQ (request for quote)
- **Who:** a signed-in user with `UserStatus.Active` (verified) who is an effective member, in any role, of an **Active**
  company (brief §9: Requesters may ask for quotes). Pending company → 409 `COMPANY_NOT_VERIFIED`. Suspended → 409
  `COMPANY_SUSPENDED`. A non-member or an unknown company → 404. Personal accounts and guests: §15 decision 5.
- **Sources:** `cart` (the server reads the caller's own server-side cart through `ICartService`; the body has no lines;
  the cart is not changed), `text` (pasted lines), `csv` (the file is read in the browser and posted as text, as D5.3 does;
  the label is all that differs), `product` (one variant + quantity, from a product page or a quote-only shipping notice).
  Other body fields: `message` (≤ 2,000), `neededBy` (a Dhaka date from today to today + 365, optional), `shipToSiteId`
  (optional, a site of this company, so staff can quote delivery). Header `Idempotency-Key` (8–100), unique
  `(requested_by, idempotency_key)` with a body hash. A replay returns the same RFQ. Another body → 409 `IDEMPOTENCY_KEY_REUSED`.
- **Lines:** `text`/`csv` go through `QuickOrderLines.Parse` and then the D5.2 matching. A parser error (bad quantity, a line
  too long …) refuses the RFQ with 422 `RFQ_LINES_INVALID` and the parser's per-line list. Nothing broken is stored. An
  unmatched part is **accepted**: staff are there for it. Each line stores what the buyer asked: `line_no`, `raw_text` (≤ 200),
  `part_key`, `quantity` (1–10,000 after merging, the order cap; more → 422 `QTY_EXCEEDS_LIMIT`), `match_status`
  (`matched | ambiguous | not_found | obsolete`), `variant_id` (when matched or obsolete), `successor_variant_id` (obsolete only),
  `note` (≤ 500). At most 500 lines and 100,000 characters (D5.1). Lines never change after insert.
- **Caps (Q9):** at most `Quotes:MaxRfqsPerCompanyPerHour` (10) and `Quotes:MaxOpenRfqsPerCompany` (20, Submitted or
  Quoting), counted in the database under the transaction advisory lock `rfq:{companyId}`, so parallel requests cannot all
  pass. Over the cap → 429 `RFQ_RATE_LIMITED` / 409 `TOO_MANY_OPEN_RFQS`. Plus the HTTP policy `QuoteRequests` (10 a minute
  per signed-in user).
- **Attachments:** none in D4. They wait for C3.1 and the V5 upload rules (magic bytes, size, ClamAV). Q9's upload test
  moves with them.
- **Status:** `Submitted` → `Quoting` (staff created a quote from it) → `Answered` (the first quote was sent;
  `first_quote_sent_at` measures the SLA) → `Closed` (staff, reason 5–500, shown to the buyer, for example "no bid"). Or
  `Withdrawn` (buyer, any time; quotes already sent stay acceptable). Number `MWR-YYMMDD-XXXXXX`.

### 3. Quote and versions
- A quote belongs to **one company** and optionally to **one RFQ**. Neither ever changes. An RFQ can lead to several quotes
  (a BOM of more than 50 lines is split, §4). Staff may also open a quote with no RFQ (after a phone call or a visit) for an
  Active company.
- `contact_user_id`: the buyer who gets the notices. It defaults to the RFQ requester. Staff may choose another effective
  member of that company with the Buyer or Admin role (it must be a member of the quote's company, QT6).
- **Number** `MWQ-YYMMDD-XXXXXX`: the `OrderNumber` generator (6 random base32 characters, UTC date) with the prefix `MWQ`.
  This deviates from the brief's `XXXX`: 4 characters give about 1 million numbers a day, which can be guessed at scale.
  Unique index, retried on a clash. Shown as "MWQ-261117-K7Q2MX rev. 2". PDF file name `MWQ-261117-K7Q2MX-r2-en.pdf`.
- **Transitions** (only these; domain methods plus the guard trigger of §11):

| From → To | Action | Who | Step-up |
|---|---|---|---|
| (new) → Draft | create / revise | `quotes.edit` | – |
| Draft → Sent | send, no flag (§5) | `quotes.edit` | – |
| Draft → PendingApproval | send, with flags | `quotes.edit` | – |
| PendingApproval → Sent | approve | `quotes.approve` (+ `quotes.approve_below_floor` for a floor flag), four-eyes (§5) | `quote.approve` |
| PendingApproval → Draft | reject approval (comment 5–500) / author recalls | `quotes.approve` / `quotes.edit` | – |
| Sent → Accepted | accept (§8) | buyer with the Buyer role | – |
| Sent → Rejected | reject ("what should change", ≤ 1,000) | quote contact, RFQ requester, company Buyer or Admin | – |
| Sent → Withdrawn | withdraw (reason 5–500) | `quotes.edit` | – |
| Sent, or Accepted whose order lapsed (§8.6) → Superseded | a newer version is sent, in the same transaction | system | – |

- **Revise** copies the latest version into Draft `n + 1` (lines, header, buyer note). It is allowed while there is no open
  version and no version with a live or confirmed order. The old Sent version stays acceptable until the new one is sent.
- **Close** (staff, reason 5–500) ends the quote. The live Sent version is withdrawn in the same transaction. A closed quote
  gets no new versions.
- Withdrawing needs no step-up. It cannot lower a price or move money. The reason is audited.

### 4. Lines, price snapshot and totals
- A line is a **catalogue variant** (Active product and variant). An item that is not in the catalogue needs a PIM product
  first. The buyer is answered in the cover note.
- At most **50 lines** per version (`PlaceOrderRequest.MaxLines`, so a version can always become one order), one line per
  variant (unique), quantity 1–10,000 that passes `ProductVariant.QuantityProblem` (MOQ, pack) at send and again at acceptance.
- `unit_price_minor`: whole poisha, **VAT-exclusive** (ADR-0008 §2), 1 … `PriceListLimits.MaxUnitPriceMinor`. It is an
  absolute price: no percentage fields and no discount on the whole quote.
- **Snapshot**, written by the server and never by the client. Fixed at send (or when the version enters PendingApproval) and
  part of the content hash (§6): `sku`, `part_number`, `title_en`, `title_bn`, `seller_id`, `vat_rate` (the rate the PDF
  shows), `public_unit_price_minor` (the public price on the net basis at that instant, `PriceResolver.ToOrderBasis`; the
  order line later records it as its public price), line net, VAT and total (`OrderPricing` rule: VAT rounded once per line,
  ADR-0002), version totals (goods net, VAT, goods total; shipping and grand total only when delivery is fixed, §8.3).
- **Staff-only check figures**, recomputed at send and again at approval, **not** in the hash:
  `reference_unit_price_minor` + `reference_source` (what `IPriceResolver` gives **this company** for this quantity at that
  instant, Exclusive mode: contract, list, sale or quantity break), `cost_minor_at_check` and `floor_minor_at_check` (shown
  only with `pricing.view_cost`), `below_floor`, `over_authority`, and `below_floor_reason` (required when below the floor).
- `line_note` (≤ 300) is shown to the buyer. `internal_note` on the version (≤ 2,000) never is.
- A quote price may be **above** the buyer's current price. It stays fixed for the validity, and the accept preview says when
  the buyer's normal price is now lower (§8.4).

### 5. Approval: discount authority, margin floor, four-eyes, step-up (Q8)
The pure `QuoteApprovalPolicy.Evaluate(version, costFacts, floors, resolvedReferences, options)` runs at send and again
inside the approval transaction, with the cost, floors and resolver prices of that moment:

| Flag | Rule | Needs |
|---|---|---|
| F1 below floor | `MarginFloorPolicy.EvaluateEntry(lineId, unitNet, facts, floors).BelowFloor`: same rule as price lists, fail-closed (no cost, or no global floor row ⇒ below floor) | `quotes.approve_below_floor` + `below_floor_reason` on the line |
| F2 below cost | `unitNet < cost` while `Pricing:AllowBelowCost = false` | refused (409 `QUOTE_BELOW_COST`) |
| F3 over authority | `unit × 10,000 < reference × (10,000 − Quotes:DiscountAuthorityBasisPoints)` (default 500 = 5 %); equal is within authority | `quotes.approve` |
| F4 long validity | `validity_days > Quotes:ValidityWithoutApprovalDays` (default 30) | `quotes.approve` |

- The reference is **the buyer's own resolver price now**, never a previous version or another quote. So revisions cannot
  ratchet the price down 5 % at a time, and price lists (four-eyes, ADR-0008) are the only way to move the reference.
- **Conflict of interest (refusal, not a flag):** a staff user who is an effective member of the quote's company may not edit,
  send or approve its quotes (403 `QUOTE_CONFLICT_OF_INTEREST`).
- Send with no flag → Sent. With flags → PendingApproval: content hash stored, version frozen, outbox
  `quote.approval_requested`. The send answers `{status, flags}`.
- **Approve** needs all of: (1) `quotes.approve`, and `quotes.approve_below_floor` when any F1; (2) a fresh authenticator code,
  `[RequireStepUp("quote.approve")]`; (3) the approver is not the creator, not the submitter and not a contributor of this
  version (domain, plus DB CHECK `approved_by <> created_by AND approved_by <> submitted_by`, plus contributors in the guard
  trigger, as price lists do); (4) the `contentHash` the approver was shown, else 409 `QUOTE_CHANGED`; (5) `belowFloorLineIds`
  and `overAuthorityLineIds` exactly equal to the server's lists recomputed now, else 409 `QUOTE_FLAGS_NOT_ACKNOWLEDGED` (a
  flag that appeared since submit therefore needs a new look); (6) the quote row and version row locked (§9), version still
  PendingApproval (two approvals → one wins, the other gets 409). The approval sends the version in the same transaction (§7).
- **Reject approval:** `quotes.approve`, comment 5–500, no step-up (nothing becomes binding) → Draft.
- No total-amount threshold at launch (§15 decision 3). Q8's "% per role" is one value for every editor until G5 settles roles.

### 6. Content hash and immutability (Q1)
- `content_hash` = lowercase hex SHA-256 of a canonical UTF-8 JSON (fixed property order, no whitespace, money as integers,
  VAT rates as 4-decimal strings, dates ISO-8601) of everything the buyer sees and accepts: quote id and number, version no,
  company id and company snapshot (name, BIN, billing address), contact user id, lines (line no, variant id, sku, part number,
  titles, quantity, unit price, VAT rate, line net/VAT/total, public price, line note), shipping (mode, per-seller fees,
  ship-to snapshot), payment terms, validity days, terms text, buyer note, currency. **Not** in it: statuses, timestamps,
  staff ids, internal note, check figures and flags.
- It is stored when the version leaves Draft. The approver sends it back (§5) and so does the buyer (§8). The PDF footer
  prints its first 16 hex characters (§10).
- **Immutable once not Draft**: domain methods refuse, and the guard trigger `QuoteSql.Install` (pattern
  `PriceListSql`) refuses any change of content columns and of lines, any transition outside §3, any column outside the step
  being taken, and every DELETE. The only later writes are the next step's columns, the check figures during the approval
  (PendingApproval → Sent), the PDF hash and key (null → value, once), and the move of `accepted_order_id` allowed by §8.6.
- RFQ lines never change. Quote company, RFQ and number never change.

### 7. Validity and expiry (Q3)
- `validity_days`: 1 … `Quotes:MaxValidityDays` (90), default `Quotes:DefaultValidityDays` (15, brief §8.2). It is chosen
  in the draft and is part of the hash.
- **Validity starts when the version is sent** (direct send or approval), at instant t. The last valid Dhaka date is
  D = DhakaDate(t) + validity_days. `valid_until_utc` = the start of D + 1 in Asia/Dhaka (fixed +06:00, as in E8.3 and B3),
  **exclusive**. The PDF prints "valid until D".
- Acceptance is allowed only if the order's single server instant `now < valid_until_utc`. The client sends no dates. No grace.
- An order that waits for the company's approval keeps the quote price after `valid_until` (ADR-0008 §8).
- An expired, rejected or withdrawn version is revised into a new version with a new validity, evaluated again (§5).
- The expiry worker (D4.12) only sends notices (§14). It never changes a status.

### 8. Acceptance → order (Q2, Q4, Q5)
#### 8.1 Request
`POST /api/v1/companies/{companyId}/quotes/{quoteId}/accept`, header `Idempotency-Key` (8–100), body
`{versionNo, contentHash, expectedGrandTotalMinor, payOnCredit, shipToSiteId?, shippingAddressId?, costCentreId?,
purchaseOrderNumber?}`. There are no lines, quantities, prices, coupons or company id. Extra properties are ignored, and a
test proves it (QT2). `expectedGrandTotalMinor` is **required** here (400 without it). It is the total of the accept preview.

#### 8.2 Who
An effective member of `companyId` with the **Buyer** role of an Active, unsuspended company: the same rule as placing an
order (`RequireCompanyMayBuyAsync`). The quote's `company_id` must equal `companyId`, else 404 (also for every other
mismatch). A member without the Buyer role → 403 `COMPANY_ROLE_REQUIRED`, as for ordering. The order is always in the quote's
company. A member of two companies cannot move a quote to the other one.

#### 8.3 The path: `IOrderService.PlaceFromQuoteAsync`
It shares `PlaceLockedAsync` with `PlaceAsync`: per-buyer and cost-centre advisory locks, the idempotency record (request hash
= quote id, version, content hash, delivery, payment, cost centre, PO number), a verified account, the open-unpaid cap for
non-credit orders, cost-centre rules, ship-to, the approval rule on the server total, the early credit-hold check, the spend
limit and budget, stock reservation with the same lifetimes, and the order-number retry. Differences:
1. **Pricing:** no resolver and no promotions. Each line → `OrderLineDraft(unit = snapshot net, vatRate = the category's
   current rate, PriceResolution(Quote, NetOfVat, no list ids, public = snapshot public))` + `quote_line_id`. Tax mode
   Exclusive. `OrderPricing` does the math as today.
2. **Shipping:** `Fixed` → per-seller fees from the snapshot, ship-to = the snapshot address of the quoted site (a
   `shipToSiteId` / `shippingAddressId` other than that site → 409 `QUOTE_DELIVERY_FIXED`; a site removed since → 409
   `QUOTE_DELIVERY_SITE_GONE`). `Calculated` → the normal E8 quoter for the chosen destination (heavy/oversize → 422
   `SHIPPING_QUOTE_ONLY`: ask for a revision with fixed delivery).
3. **Payment terms:** `Advance` → `payOnCredit` must be false (409 `QUOTE_REQUIRES_ADVANCE_PAYMENT`). `AsAccount` → as an
   order. A quote never grants or changes credit terms (ADR-0010: finance sets them).
4. **Early checks** (no lock, before stock is held): acceptable version (§8.6), not expired at `now`, equal content hash
   (409 `QUOTE_CHANGED`), active variants (409 `VARIANT_UNAVAILABLE`), quantities still valid (400, nothing rounded).
5. **In `SaveAsync`'s transaction, first:** the quote row and then the version row `FOR NO KEY UPDATE`. The §8.6 check runs
   again with the same `now`: a withdrawal, a newer version or another acceptance that committed meanwhile wins. Then (quote
   lines name no price list) the company lock and `ChargeCreditAsync` for a credit order with no approval rule (limit + hold,
   ADR-0010 §3, §9), or the approval request. Then `version.Accept(orderId, userId, now)`, the order row with
   `quote_id` + `quote_version_id`, one audit row `quote.accepted` and the outbox event `quote.accepted`, all in the same
   transaction. The buyer also gets the usual order e-mail from `OrderPlacedEvent`.
6. **`PRICE_CHANGED`:** a server grand total above `expectedGrandTotalMinor` → 409 `PRICE_CHANGED`, nothing placed. This
   happens when the VAT rate changed since sending or when calculated shipping changed. A lower or equal total goes through at
   the server's total.

The order side reaches quotes only through an Application port, `IQuoteAcceptanceGate` (load the snapshot; lock and check
inside the caller's transaction; mark accepted), implemented in `Infrastructure/Quotes`, so the module-boundary ratchet
gets no new pair. Company membership for quotes uses a helper in `Persistence` (as `BuyerCreditAccess` does), never
`Infrastructure.Companies`.

#### 8.4 The quote price wins while valid
The lines are not resolved (ADR-0008 §5), even when a contract or a sale is lower now. The **accept preview**
(`POST …/accept-preview`, the same pricing path with no stock and no writes, like `QuoteShippingAsync`) returns the lines, VAT
now, shipping, grand total and notices: `vatRateChanged`, `lowerPriceNow` per line (the buyer's own resolver price is lower;
the buyer may order normally instead), `stockShort` per line.

#### 8.5 Stock
A sent quote **holds no stock** (§15 decision 4). Acceptance reserves exactly like an order and counts towards the open-unpaid
cap. `INSUFFICIENT_STOCK` → 409, and the buyer asks for a revision. Backorders are not modelled (§16).

#### 8.6 One live order per version, and re-acceptance
The **acceptable version** of a quote is the version that is `Sent`, or `Accepted` whose order is `Cancelled` with
`confirmed_at IS NULL` (an approval was rejected or lapsed, the order expired unpaid, or the buyer cancelled it while pending).
That one may be accepted again while it is valid, and `accepted_order_id` moves to the new order. Once an order from the quote
has been **confirmed**, the quote is done, even if staff cancel the order later (E7.2). Database backstops: the partial unique
index `ux_orders_live_quote_version ON orders (quote_version_id) WHERE quote_version_id IS NOT NULL AND status <> 'Cancelled'`,
and the guard trigger lets `accepted_order_id` move only away from a cancelled, never-confirmed order. A replay with the same
key returns the original order (`Replayed = true`), even after it was cancelled, as `PlaceAsync` does. The order side never
writes quote rows: re-acceptance is derived from the order's status.

#### 8.7 Approvals and credit (Q5, ADR-0010)
These code paths are unchanged. A company rule that covers the server total makes the order `PendingApproval` in the same
transaction. Credit is charged only at confirmation, with key `charge:{orderId}`. The overdue hold refuses credit at
placement and at approval-confirmation. An approved order that finds the hold is cancelled, and the quote becomes acceptable
again (§8.6) while valid, for example with advance payment. Terms days are the company's at confirmation. The hold never
blocks sending, reading or rejecting quotes. The staff quote view shows only `creditTerms` and `creditOnHold` (no amounts)
next to the company, so sales knows before quoting "on account".

#### 8.8 Reject
`POST …/quotes/{quoteId}/reject` `{versionNo, reason?}` → Rejected, then staff are told. The quote stays open for a revision.

### 9. Concurrency and the global lock order
**Extended order:** `quotes` → `quote_versions` → `rfqs` → gateway `payments` → `orders` → `credit_payments | credit_adjustments` →
`companies` → `promotions`.
- **Accept:** advisory locks (buyer, cost centre; session level, before any row) → stock reservation in its own transaction
  (as today) → `SaveAsync`: `quotes` NO KEY UPDATE → `quote_versions` NO KEY UPDATE → `companies` FOR UPDATE (credit only) →
  inserts (no promotions for quote lines).
- **Send, approve, reject approval, recall, withdraw, revise, close, buyer reject:** `quotes` NO KEY UPDATE → `quote_versions`
  NO KEY UPDATE; a send or an approval that sends the version then locks the quote's `rfqs` row NO KEY UPDATE (to mark it
  Answered, D4.8); nothing else. Inside the quote tier: the quote row first, then its versions by version number. The policy
  facts (catalogue, resolver prices, cost, floors) are plain reads inside that transaction.
- **RFQ commands** (assign, close, buyer withdraw) lock only the `rfqs` row; **create a quote from an RFQ** locks the `rfqs` row
  and inserts the new quote (no existing quote row is locked), so no path locks an RFQ before a quote.
- **Rule:** no path that holds a payments, orders, credit or companies row lock may lock a quote row. Cancel, approval,
  payment and credit paths never touch quotes (§8.6), so the new tier adds no cycle.
- `FOR NO KEY UPDATE`, not `FOR UPDATE`: `orders.quote_version_id` and `order_items.quote_line_id` are foreign keys whose
  inserts take `FOR KEY SHARE` (the B3.5 lesson).
- Two parallel accepts serialise on the quote row. The second sees `Accepted` → 409 `QUOTE_ALREADY_ACCEPTED`, or its own
  replay. The partial unique index is the backstop (23505 → the same 409). A newer version, a withdrawal and an acceptance
  serialise on the same row. The status check decides, so exactly one wins.
- Staff commands map a unique violation (23505) and the deferred one-live exclusion constraint (23P01, raised at commit) to 409
  `QUOTE_VERSION_CHANGED` (`ux_quotes_number` → 409 `QUOTE_NUMBER_CLASH`), never a 500 (D4.6, D4.8).
- Drafts: optimistic `xmin` token (two editors; an edit against a send → 409 `QUOTE_VERSION_CHANGED`).
- RFQ caps: transaction advisory lock `rfq:{companyId}` around count + insert.

### 10. PDF (A8, Q7)
- Built **only from the frozen snapshot** (never live company or product data): letterhead text (settings, owner data §15 b),
  "Quotation", number + revision, issue date and "valid until" (Dhaka dates), buyer company snapshot (name, BIN, address),
  contact name, table (no, part number, description, qty, unit price excl. VAT, VAT %, line total; money column right-aligned),
  goods net, VAT, shipping, grand total (or "delivery calculated at order"), payment terms, validity and stock lines ("prices
  fixed until D; stock is not reserved and is confirmed at order"), terms text, prepared by (name), footer "MWQ-… rev. n ·
  verification ‹first 16 hex of content_hash› · page x of y". Locales `en` and `bn` (`Hind Siliguri`).
- **Rendered lazily** at the first request and stored with `IObjectStorage` under the server-chosen key
  `quotes/{quoteId:N}/v{n}-{locale}.pdf`. `pdf_{en,bn}_sha256` and the key are recorded on the version, null → value once
  (first writer wins; two renders of one snapshot are byte-identical, ADR-0009). Render and upload happen outside any row lock.
- **Download** is an authenticated stream only: `Cache-Control: private, no-store`, `Content-Disposition: attachment`, access
  checked on every request (buyer §13, staff `quotes.view`). The stored file's SHA-256 is recomputed before it is sent. A
  mismatch is not served (503 `QUOTE_PDF_UNAVAILABLE`), and it leaves an error log and audit `quote.pdf_integrity_failed`. A
  missing file is rendered again: same hash → stored and served; different hash (renderer upgraded) → 503 + alert, and staff
  re-issue it as a new version.
- **No public, guessable or long-lived URL.** E-mails link to the quote page (sign-in needed) and never attach the PDF. This
  meets Q7 in a stricter form: there is no link to sign. If an attachment or a direct link is added later, it must be ≤ 15
  minutes, bound to the user and version, and access is checked again.
- Staff check of a document: `GET admin/quote-documents/{sha256}` → number, version, locale, or 404 ("is this PDF ours and
  unmodified?"). A draft can be previewed on screen with a "DRAFT, NOT AN OFFER" heading. It is never stored or hashed.

### 11. Data model (migrations: `AddRfqsAndQuotes` in D4.4, `AddOrderQuoteLink` in D4.9)
Enums are stored as text (`HasConversion<string>()`, PascalCase, CHECK on the list), as `orders` and `price_lists` do.
All foreign keys are `RESTRICT`, there are no deletes, and every table has an `xmin` token.

**`rfqs`:** `id`, `number` varchar(17) unique, `company_id` → companies, `requested_by` → users, `source`
(`Cart | Text | Csv | Product`), `status` (`Submitted | Quoting | Answered | Closed | Withdrawn`), `message` ≤ 2,000,
`needed_by` date null, `ship_to_site_id` → company_sites null, `line_count`, `idempotency_key`, `request_hash` char(64),
`assigned_to` → users null, `first_quote_sent_at`, `closed_by/at`, `close_reason`, `withdrawn_at`, `created_at`, `updated_at`.
Unique `(requested_by, idempotency_key)`. Index `(company_id, created_at DESC)`, `(requested_by, created_at DESC)`, partial
`(created_at) WHERE status IN ('Submitted','Quoting')` (inbox). Guard: company, requester, source, number never change.

**`rfq_lines`:** `id`, `rfq_id`, `line_no` 1–500 (unique per RFQ), `raw_text` ≤ 200, `part_key` ≤ 64 null, `quantity`
1–10,000, `match_status`, `variant_id` null, `successor_variant_id` null, `note` ≤ 500 null. CHECK: `variant_id` present iff
matched or obsolete. Append-only trigger (UPDATE, DELETE and TRUNCATE refused).

**`quotes`:** `id`, `number` varchar(17) unique, `company_id`, `rfq_id` null, `contact_user_id` → users, `assigned_to` null,
`created_by`, `created_at`, `latest_version_no`, `closed_by/at`, `close_reason`. Index `(company_id, created_at DESC)`,
`(rfq_id)`, partial `(assigned_to) WHERE closed_at IS NULL`.

**`quote_versions`:** `id`, `quote_id`, `version_no` ≥ 1 (unique per quote), `status`
(`Draft | PendingApproval | Sent | Accepted | Rejected | Withdrawn | Superseded`), `validity_days` 1–365 (hard cap; options
decide), `payment_terms` (`AsAccount | Advance`), `shipping_mode` (`Calculated | Fixed`), `ship_to_site_id` null,
`ship_to` jsonb null (address snapshot), `shipping_fees` jsonb null (`[{SellerId, ShippingMinor ≥ 0}]`), `terms_text` ≤ 4,000,
`buyer_note` ≤ 2,000, `internal_note` ≤ 2,000, `company_snapshot` jsonb null, `preparer_name` ≤ 200 null, `goods_net_minor`,
`tax_minor`, `goods_total_minor`, `shipping_total_minor` null, `grand_total_minor` null, `content_hash` char(64) null, `flags`
jsonb (staff only), `created_by`, `submitted_by/at`, `approved_by/at`, `approval_comment`, `approval_rejected_by/at`,
`sent_by/at`, `valid_until_utc`, `accepted_order_id` → orders null, `accepted_by/at`, `rejected_by/at`, `reject_reason`
≤ 1,000, `withdrawn_by/at`, `withdraw_reason`, `superseded_at`, `pdf_en_key/sha256`, `pdf_bn_key/sha256`.
CHECKs: `approved_by <> created_by AND approved_by <> submitted_by`. Status shape: every status after Draft ⇒ `content_hash`
(`^[0-9a-f]{64}$`) and `submitted_by`. Every status from Sent on ⇒ `sent_at`, `valid_until_utc`, `company_snapshot`,
`preparer_name`. Accepted ⇒ `accepted_order_id`. `(shipping_mode = 'Fixed') = (shipping_fees, ship_to_site_id, ship_to,
grand_total_minor all NOT NULL)`. Money ≥ 0, `goods_total = goods_net + tax`.
Indexes: unique partial `(quote_id) WHERE status IN ('Draft','PendingApproval')` (one open version), unique partial
`(quote_id) WHERE status IN ('Sent','Accepted')` (one live version), partial `(valid_until_utc) WHERE status = 'Sent'`
(expiry notices), partial `(submitted_at) WHERE status = 'PendingApproval'` (approval queue).

**`quote_lines`:** `id`, `version_id`, `line_no` 1–50 (unique per version), `variant_id` → product_variants (unique per
version), `rfq_line_id` null, `seller_id`, `sku` ≤ 64, `part_number` ≤ 64 null, `title_en` / `title_bn` ≤ 255, `quantity`
1–10,000, `unit_price_minor` 1 … 100,000,000,000, `vat_rate` numeric(5,4) 0–1, `line_net/tax/total` ≥ 0 with
`total = net + tax`, `public_unit_price_minor` > 0, `line_note` ≤ 300 null, staff-only `reference_unit_price_minor` > 0,
`reference_source`, `cost_minor_at_check` null, `floor_minor_at_check` null, `below_floor`, `over_authority`,
`below_floor_reason` ≤ 500 null. Guard: insert, update and delete only while the version is Draft; during PendingApproval →
Sent only the staff-only check columns; afterwards nothing.

**`quote_version_contributors`:** `(version_id, user_id)` primary key. Inserted only while Draft, never removed. The trigger
refuses an approval by a contributor.

**`orders`** (D4.9): `quote_id` → quotes null, `quote_version_id` → quote_versions null, CHECK both or neither,
`ux_orders_live_quote_version` (§8.6). **`order_items`**: `quote_line_id` → quote_lines null, and
`ck_order_items_price_snapshot` gains `(quote_line_id IS NULL) = (price_source IS DISTINCT FROM 'Quote')`. `OrderDto` gains
`quoteNumber`. EF never updates these columns after insert [CODE; no trigger].

### 12. API sketch (`api/v1`, JSON `data` envelope; buyer reads `private, no-store`, `Vary: Authorization`)
**Buyer** (membership checked from `company_members` on every call, §13):
| Method and path | Purpose |
|---|---|
| `POST companies/{companyId}/rfqs` (Idempotency-Key) | create from `cart` / `text` / `csv` / `product` (201, replay 200) |
| `GET companies/{companyId}/rfqs?status&page` · `GET …/rfqs/{rfqId}` | own (Requester) or all (Buyer, Admin) |
| `POST …/rfqs/{rfqId}/withdraw` | buyer withdraws |
| `GET companies/{companyId}/quotes?status&page` · `GET …/quotes/{quoteId}` | sent versions only; drafts and pending never |
| `GET quotes/by-number/{number}` | `{companyId, quoteId}` for e-mail links; every failure is the same 404; rate-limited |
| `GET …/quotes/{quoteId}/versions/{n}/pdf?locale=en\|bn` | authenticated stream (§10) |
| `POST …/quotes/{quoteId}/accept-preview` | §8.4, no writes |
| `POST …/quotes/{quoteId}/accept` (Idempotency-Key) | §8; 201 `PlacedOrder` + `quoteNumber`, replay 200 |
| `POST …/quotes/{quoteId}/reject` | §8.8 |

**Staff** (`admin/…`):
| Method and path | Permission |
|---|---|
| `GET admin/rfqs?status&assigned=me&companyId&q&page` · `GET admin/rfqs/{id}` | `quotes.view` |
| `POST admin/rfqs/{id}/assign` · `POST admin/rfqs/{id}/close` | `quotes.edit` |
| `POST admin/quotes` `{rfqId? \| companyId, contactUserId?}` (pre-fills matched RFQ lines) | `quotes.edit` |
| `GET admin/quotes?status&companyId&assigned&q&page` · `GET admin/quotes/{id}` (all versions) | `quotes.view` (cost with `pricing.view_cost`) |
| `PUT admin/quotes/{id}/versions/{n}` (header + all lines, `rowVersion`) | `quotes.edit` |
| `POST admin/quotes/{id}/versions/{n}/price-preview` (lines → reference, public, flags; nothing saved) | `quotes.edit` |
| `POST …/versions/{n}/send` → `{status: sent \| pending_approval, flags, quote}` | `quotes.edit` |
| `POST …/versions/{n}/approve` `{contentHash, belowFloorLineIds, overAuthorityLineIds, comment?}` | `quotes.approve` (+ below floor), step-up `quote.approve` |
| `POST …/versions/{n}/reject-approval` `{comment}` · `POST …/versions/{n}/recall` | `quotes.approve` · `quotes.edit` |
| `POST admin/quotes/{id}/revise` · `POST …/versions/{n}/withdraw` `{reason}` · `POST admin/quotes/{id}/close` `{reason}` | `quotes.edit` |
| `GET admin/quotes/approvals?page` | `quotes.approve` |
| `GET admin/quotes/{id}/versions/{n}/pdf?locale` · `GET admin/quote-documents/{sha256}` | `quotes.view` |

Error codes (new): `RFQ_LINES_INVALID`, `RFQ_RATE_LIMITED`, `TOO_MANY_OPEN_RFQS`, `QUOTE_CHANGED`, `QUOTE_VERSION_CHANGED`,
`QUOTE_FLAGS_NOT_ACKNOWLEDGED`, `QUOTE_BELOW_COST`, `QUOTE_CONFLICT_OF_INTEREST`, `QUOTE_NOT_ACCEPTABLE` (expired, withdrawn,
rejected, superseded, closed; one code, the message says which), `QUOTE_ALREADY_ACCEPTED`, `QUOTE_DELIVERY_FIXED`,
`QUOTE_DELIVERY_SITE_GONE`, `QUOTE_REQUIRES_ADVANCE_PAYMENT`, `QUOTE_PDF_UNAVAILABLE`. Reused: `PRICE_CHANGED`,
`INSUFFICIENT_STOCK`, `VARIANT_UNAVAILABLE`, `SHIPPING_QUOTE_ONLY`, `CREDIT_HOLD_OVERDUE`, `IDEMPOTENCY_KEY_REUSED`,
`COMPANY_ROLE_REQUIRED`.

Codes added while building (D4.2–D4.8), with their HTTP status: 409 `QUOTE_INVALID_TRANSITION` (a step outside §3, e.g. a
second send, approval or withdrawal, or a recall of a version that is not pending), 409 `QUOTE_NOT_EDITABLE` (a draft edit or
preview of a version that left Draft), 409 `QUOTE_CLOSED`, 409 `QUOTE_OPEN_VERSION_EXISTS`, 409 `RFQ_INVALID_TRANSITION`,
409 `QUOTE_FOUR_EYES` (the approver created, changed or submitted the version), 409 `QUOTE_CONTACT_LOCKED` (D4.7), 409
`QUOTE_NUMBER_CLASH` (D4.6), 409 `QUOTE_VERSION_CHANGED` also for 23505 / 23P01 (§9). A below-floor line without its reason is
400 `VALIDATION_ERROR` (field `belowFloorReason`) at the send. Reused from price lists: 409 `BELOW_FLOOR_REASON_REQUIRED` at an
approval (a line fell below the floor since the send and has no reason: reject it), 403 `BELOW_FLOOR_PERMISSION_REQUIRED`; and 409
`COMPANY_NOT_ACTIVE` (create, send, approve), 403 `STEP_UP_REQUIRED` / `STEP_UP_FAILED` (approve). The other statuses: 403
`QUOTE_CONFLICT_OF_INTEREST`, 409 `QUOTE_CHANGED`, `QUOTE_FLAGS_NOT_ACKNOWLEDGED` (its message names the line ids flagged now,
never an amount), `QUOTE_BELOW_COST`, `QUOTE_DELIVERY_SITE_GONE` (a send whose fixed-delivery site was removed; a site that
still exists is copied again at the send, so the buyer is offered its address of that moment).

### 13. Permissions and access
| Permission (new) | Support | Finance | Admin | Allows |
|---|---|---|---|---|
| `quotes.view` | ✓ | ✓ | ✓ | inbox, RFQs, quotes, PDFs, document check |
| `quotes.edit` | ✓ | – | ✓ | create, edit, send within authority, revise, withdraw, close, assign |
| `quotes.approve` | – | ✓ | ✓ | approve (and so send) or reject a pending version, never one's own |
| `quotes.approve_below_floor` | – | – | ✓ | needed in addition for a below-floor line |

Support is the sales-facing role today (`Permissions.cs` comment). The brief's "Sales Engineer" arrives with G5. Moderator,
Seller and Buyer get none. Cost and floor figures need `pricing.view_cost` (existing; Support does not have it, so it sees
the flags and not the numbers).

**Buyer side:** the company id in the URL is checked against `company_members` on every call (B1, D2.10, B3.9). An effective
member (active, invitation accepted) of an Active or Suspended company (reads) or of an Active company (create, accept).
Visibility: `Quotes:VisibleTo` (default **Admin | Buyer**) see all of the company's RFQs and quotes. A member without those
roles sees the RFQs they requested and the quotes they are the contact for. Accept = Buyer role. Reject = contact, requester,
Buyer or Admin. Every other case (unknown or foreign company, another company's quote, a role that may not see it, a
Pending or Rejected company, a malformed number, platform staff on a buyer route) is the **same 404** ("Quote not found." /
"Request not found."). Anonymous → 401.
Buyer DTOs are built field by field, and a unit test pins the lists. They never contain cost, floor, margin, flags,
reference price or source, discount percentages, the internal note, approval data, staff ids or e-mail addresses, Draft or
PendingApproval versions, or anything of another company. The preparer's full name is shown as the business contact. Public
caches, `sf:*` keys and the search index never hold quote prices (as PL17). Rate limits: `QuoteRequests` (create RFQ), and
`QuoteRead` for by-number and PDF (30 a minute per user).

### 14. Audit, notifications, privacy, retention
- **Audit** (one `audit_logs` row per command, in the same transaction: actor, IP, correlation id, quote/version, old/new status,
  content hash): `rfq.submitted`, `rfq.withdrawn`, `rfq.assigned`, `rfq.closed`, `quote.created`, `quote.draft_changed`,
  `quote.submitted_for_approval`, `quote.approved`, `quote.approval_rejected`, `quote.recalled`, `quote.sent`, `quote.revised`,
  `quote.withdrawn`, `quote.closed`, `quote.accepted` (order id), `quote.rejected`, `quote.pdf_rendered` (sha),
  `quote.pdf_integrity_failed`.
- **Outbox** (ids only; `NotificationOutboxPublisher` reads recipients and values at send time):
  `quote.rfq_submitted` → staff notice (sales inbox `Quotes:SalesInboxEmail`, owner data) · `quote.approval_requested` →
  staff holding `quotes.approve`, minus the author · `quote.sent` → the contact (template `quote-sent`, exists) ·
  `quote.accepted` / `quote.rejected` → assigned staff + sales inbox · `quote.withdrawn` → the contact, in-app only at launch ·
  `quote.expiring` (48 h before `valid_until`, worker) → the contact (new template EN/BN) · `quote.expired` → assigned staff.
  Staff notices use one English template with parameters. Worker event ids are a UUIDv8 of SHA-256(version id, kind), so each
  is sent once per version, with any number of passes or replicas (B3.10 pattern).
  Recipients must be effective members of the quote's company at send time. A contact who left gets nothing, and staff are
  told instead.
- **Privacy:** personal data = user ids, staff names on PDFs, free text (RFQ message, notes, reasons). Free text is stored as
  plain text with control characters stripped (as D5.1). It is never interpreted as markup (React, e-mail templates, PDF
  text runs) and never sent to analytics. Erasure pseudonymises names in screens and keeps ids and issued PDFs (commercial
  record).
- **Retention (RULE, lawyer and VAT consultant to confirm in `docs/LEGAL_REVIEW.md`):** RFQs, quotes, versions, lines and
  issued PDFs are kept at least 6 years after the fiscal year ends, like ADR-0010 §12. They are never deleted, because orders
  point at them.

### 15. Owner decisions
**Decided 2026-11-17 under the owner's delegation (reversible as noted; options are validated at start-up):**
1. **Validity:** default **15 days** (brief §8.2), staff choose **1–90** per quote, and more than **30 days** needs a second
   person (price risk on imported stock when the taka moves). Options `Quotes:DefaultValidityDays`, `MaxValidityDays`,
   `ValidityWithoutApprovalDays`.
2. **Discount authority:** a line up to **5 %** below the buyer's own resolver price needs no second person. More needs
   `quotes.approve` (Finance or Admin) with an authenticator code. Below the margin floor (10 % markup on cost, ADR-0008
   decision 3) needs an **Admin** (`quotes.approve_below_floor`), a written reason per line and the code. Below cost is
   refused. One value for every editor (`Quotes:DiscountAuthorityBasisPoints` = 500). Per-role limits come after G5.
3. **No quote-total threshold** for four-eyes at launch. Floor and authority are per line, so splitting a quote changes
   nothing. One option can add a threshold later.
4. **Stock:** a sent quote holds **no stock**. Acceptance re-checks and reserves like an order. The PDF says so.
5. **RFQs and quotes are for verified companies only.** Personal accounts and guests use the inquiry form (F4) or register
   a company. Reversible (quotes would need a VAT-inclusive basis).
6. **Numbers** `MWQ-YYMMDD-XXXXXX` and `MWR-YYMMDD-XXXXXX` (6 random characters, like orders; the brief's 4 can be guessed).
7. **PDF:** no digital signature or stamp image at launch. It prints the preparer's name, number, revision and a
   verification code (content-hash prefix), and MECHWARE checks any copy by its SHA-256 (§10). No integrator-branded
   (white-label) PDFs at launch.
8. **Heavy/oversize items** stay quote-only for shipping as the E8 data decides. A quote with a fixed delivery fee to a named
   site is the way to order them.
9. **One quote → one order, all lines** (partial acceptance = ask for a revision). A quote whose order lapsed before
   confirmation may be accepted again while it is valid (§8.6).
10. **The quote price is fixed even when it is above the buyer's current price.** The preview tells the buyer.
11. **Payment terms on a quote:** "as your account allows" or "advance payment only". A quote never grants credit terms.
12. **SLA clock** for "first quote < 4 working hours": Sunday–Thursday 09:00–18:00 Dhaka, shown in the inbox only.
13. **Approval sends the quote** (no separate step after approval).
14. **E-mails link to the quote page and never attach the PDF.** Withdrawals are shown in the app only.

**Stay with the owner (real data or legal text; the build does not wait for them):**
a. The quotation terms-and-conditions text (lawyer, `docs/LEGAL_REVIEW.md`). Until it is given, the PDF prints only the
   factual validity, payment and stock lines, with no T&C section.
b. Letterhead facts: legal name, trade licence, BIN, address, phone, the sales e-mail (also the RFQ notice inbox).
c. The public-holiday list for the SLA clock (empty until given).
d. Whether a real digital signature (a CCA-licensed certificate) is wanted later, with its provider and cost.
e. Whether four-eyes should name specific people (for example, only the owner approves below the floor). Role names come in G5.

### 16. Not in D4
Attachments (C3.1 + V5), backorders and ordering with a lead time, partial acceptance, discounts on the whole quote or
percentage prices, USD quotes, quote templates, copying a quote to another company (it would carry one company's prices
to another), personal or guest quotes, AI quote drafting (H5), e-signature, CRM follow-ups, quote CSV export, and products
with no public price (the order CHECK needs a public price > 0; that needs a catalogue flag first).

## Consequences
### Positive
- A quote price can always be explained: who wrote it, the buyer's own price and the floor at that moment, who approved it
  with a code, what the buyer saw (hash) and which order used it.
- Acceptance reuses the whole order path (idempotency, stock, approvals, credit, hold, `PRICE_CHANGED`). Only pricing and
  shipping differ, behind one switch.
- Expiry is a pure function of time and committed data. The worker only sends notices.
- The PDF can be checked by anyone holding it (SHA-256) and is never reachable without signing in.
### Negative
- Six new tables, a guard trigger family, two order columns and one order-item column, plus a partial unique index on
  `orders`.
- `OrderService` gains a second pricing mode. Its tests must cover both, and `PlaceLockedAsync` grows by one parameter.
- A VAT-rate change between sending and accepting changes the total, so the buyer confirms again (the net stays as quoted).
- Two colluding staff members, or one person with two staff accounts, can still approve a bad discount. The controls are
  mandatory staff 2FA, step-up, the audit trail, the conflict-of-interest rule and the below-floor permission (Admin only).
- Without stock holds, a quoted item can sell out before acceptance. The buyer then needs a revision.
