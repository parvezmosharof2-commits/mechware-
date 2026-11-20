# ADR 0012: Invoicing (Mushak-6.3 / 6.7 / 9.1) and gap-free numbering

## Status
Accepted 2026-11-21 as the E6 design (unit E6.1, design only, written by an AI assistant (Opus, standing in for Fable), not
reviewed by a person). **Consultant check before relying on it** (owner 2026-11-20): the first launch has no VAT
consultant, so every legal fact below is "as best known" and the open points are listed in §19 for the owner to forward.
Owner decisions are listed in §18; the defaults there apply until the owner answers. Nothing here is code.

## Date
2026-11-21

## Context
Brief and card E6: one invoice per taxable supply, gap-free number per fiscal year (July–June) taken inside the DB
transaction, immutable, corrections only by credit note, PDF + SHA-256, PO number, VDS/AIT, monthly 9.1 export. Threat model
v2 §৫ already fixes the rules I1–I10; §১৪ of the same document now lists the E6 abuse cases IT1–IT16, whose test names are
the exit tests of the units in `docs/modules/E6.md`.

**Binding owner decisions (2026-11-20, `docs/COMPANY_FACTS.md`, `docs/NOW.md`):**
1. No VAT consultant at first launch. Invoices follow the published NBR forms (Mushak-6.3 tax invoice, 6.7 credit note,
   9.1 monthly return) and are flagged "consultant check before relying on it".
2. No payment gateway: bank transfer, PO, company credit, COD.
3. Manual shipping: staff type courier + tracking number. One shipment per order (`ux_shipments_order`); partial
   shipments are F1.4 and later.
4. The company BIN, TIN, trade licence and full address are still missing. They come from configuration and **must be
   present before the first real invoice is issued**.

**What exists (read from code and cards, 2026-11-21):**
- `Order` with one `SubOrder` per seller (ADR-0003). Lines (`OrderItem`) freeze sku, title, quantity, unit price, VAT rate,
  gross, discount, net, tax, total; `OrderPricing` rounds VAT once per line (banker's, ADR-0002), every total is a plain
  sum. Company orders are `TaxMode.Exclusive` (unit price is net, ADR-0008 §2, §12 decision 2: net = floor(gross / (1 + VAT)));
  personal orders are `TaxMode.Inclusive` (VAT split out of the line total). `PurchaseOrderNumber` (normalised, no markup),
  `QuoteId`/`QuoteVersionId`, `ConfirmedAtUtc`, `PaysOnCredit`, `ShipToJson`.
- `Company.Bin` and `Company.Tin` are set when the company is created and have no setter (no later change path).
- F1.1: `OrderFulfilmentService.ShipAsync` = one transaction, `orders` row `FOR NO KEY UPDATE`, Packed → Shipped, one
  `shipments` row (courier, tracking), audit + outbox `OrderShippedEvent`.
- E7.2: staff cancel of Confirmed / Processing / Packed (never after Shipped) → `ICreditLedger.ReduceChargeAsync(OrderCancelled)`.
- B3 / ADR-0010 §8.2: `ReduceChargeAsync(CreditReduction(Kind CreditNote, AmountMinor, SourceKey "creditnote:{id}", CreditNoteId, …))`
  is already the E6.5 contract; rule I6 of the ledger caps cancellation + credit notes together at the charge.
- A8 / ADR-0009: deterministic SkiaSharp + HarfBuzz PDF EN/BN. D4.11 / ADR-0011 §6, §10: canonical-JSON content hash,
  store-once PDF with SHA-256 recorded null → value, hash recomputed on every download, `admin/quote-documents/{sha256}`.
- Audit log is hash-chained (`GET admin/audit/chain`, G5.4a). Step-up (`RequireStepUp`), four-eyes (`Refund`,
  `CreditPayment`), CSV formula neutralisation and per-person export limits (B3.8) exist as patterns.
- Dhaka time is a fixed +06:00 everywhere (E8.3, B3, D4); Bangladesh has no daylight saving.

**NBR facts as best known (assistant's knowledge, not verified; each is a §19 question):**
- VAT and SD Act 2012 + VAT and SD Rules 2016. A registered supplier issues a tax invoice (Mushak-6.3) for each taxable
  supply. The 6.3 for goods is also the document that travels with the goods (it has fields for the vehicle and the
  date and time of supply).
- Time of supply / tax point (Act s. 32 as best known): VAT becomes payable at the **earliest** of: the invoice is issued,
  the goods are delivered or made available, or payment is received (in full or in part).
- 6.3 content: supplier name, BIN, address of the registered and of the issuing premises; buyer name, BIN (if registered),
  address; delivery destination; vehicle type and number; invoice number; issue date and time; per line: description,
  unit, quantity, unit price, value, SD rate and amount, VAT rate and amount, total incl. taxes; responsible person.
- 6.7 credit note and 6.8 debit note adjust an issued 6.3 (decreasing / increasing adjustment), citing it. There may be a
  time limit for a credit note after the supply (possibly 6 months).
- 6.6 is the VDS certificate a withholding entity gives the supplier; the supplier claims it in the 9.1.
- 9.1 is the monthly return, filed online (IVAS) by the 15th of the next month; 6.10 lists single purchases/sales above
  BDT 2 lakh; 6.2 is the sales register.
- Records are kept for a fixed number of years (believed 5 for VAT; income tax may need 6).

## Decision

### 1. Overview
- One **tax invoice** (`Kind = TaxInvoice`, Mushak-6.3) per **(shipment, issuing seller)**, issued **inside the ship
  transaction** (§2). Launch: one shipment per order and one issuing seller (MECHWARE's own store, §3), so one invoice per
  order.
- One **credit note** (`Kind = CreditNote`, Mushak-6.7) per correction, always against one tax invoice, never more than
  what is left of it (§9).
- Numbers come from a **counter row locked in the issuing transaction** (§4), never from a PostgreSQL sequence.
- Invoices and their lines are **immutable** in the database (§8). The issued content is a canonical snapshot with a
  SHA-256 content hash written in the same transaction (§6); PDFs are renderings of that snapshot (§12).
- Debit notes (6.8), e-invoicing / EFD integration, third-party seller invoices and partial-shipment invoices are not in
  the launch scope (§20).

### 2. When a 6.3 is issued: at shipment (Packed → Shipped)
**Decision: the 6.3 is issued in the same transaction that moves the order to Shipped.** Options considered:

| | A: at confirmation (old card text, ADR-0010 note) | **B: at shipment (chosen)** |
|---|---|---|
| Matches the 6.3 as the document travelling with the goods | No: the goods have not left; vehicle/courier unknown | Yes: courier + tracking are typed in the same step |
| COD (no payment before delivery) | Wrong moment or no confirmation at all | Works: the supply happens at hand-over |
| Cancel before dispatch (E7.2: Confirmed/Processing/Packed) | Every such cancel needs a 6.7 | No invoice exists yet; E7.2 stays as built |
| Partial shipments later (E6.9) | Needs a split rule | One 6.3 per shipment, the same code |
| Advance payment received before dispatch | Tax point = invoice = payment: aligned | **Gap:** if s. 32 makes the payment the tax point, VAT is due for the month of payment while the 6.3 is dated at dispatch (§19 Q1) |
| Credit days (ADR-0010 OWNER 1 = from confirmation) | Invoice date = confirmation date | Due date counts from confirmation, a few days before the invoice date (§18 O3) |

Why B: launch payment methods are bank transfer, PO, credit and COD, and goods are handed to a courier by staff. The
dispatch is the only moment that exists for every method and that knows the transport facts the form asks for. The
advance-payment gap is the one legal risk, and it is a reporting matter (the 9.1 of the month of payment), not a
document matter: §13 lists confirmed-but-not-shipped prepaid orders per month so the accountant can decide.

**Fallback if the consultant says "at payment / at confirmation":** the issuer (§3) is one service method called inside a
caller's open transaction (`IInvoiceIssuer.IssueAsync(trigger, …)`). Calling it from the confirmation paths (credit
confirmation, approval confirmation, staff payment verification) is a follow-up unit (E6.3e, listed in the card, not built
unless needed). The numbering, snapshot, immutability and PDF are unchanged by that choice.

**Rules:**
- Ship refuses in Live mode when the issuer facts are incomplete (409 `INVOICE_SETUP_INCOMPLETE`, nothing moves) or when
  the order has a sub-order of a seller that MECHWARE does not invoice for (409 `INVOICE_SELLER_NOT_SUPPORTED`; marketplace
  sellers are H5). No invoice ⇒ no shipment, so a shipped order without a 6.3 cannot happen in Live mode.
- Only an order that reached Confirmed can be shipped (F1.1 state machine), so only confirmed supplies get an invoice.
- The supply date/time printed is the issue instant (§5). Delivery (Shipped → Delivered) changes nothing on the invoice.

### 3. Issuer facts and modes (configuration)
- Section `Invoicing` (`InvoicingOptions`, validated at startup): `Mode` = `Off | Specimen | Live`; `IssuerSellerId`
  (the seller row of MECHWARE's own store); `LegalNameEn/Bn`, `Bin` (13 digits as best known, §19 Q8), `Tin` (optional on
  the form), `TradeLicence` (optional), `RegisteredAddressEn/Bn`, `IssuingPremisesAddressEn/Bn` (defaults to the
  registered one), `ResponsiblePersonName`, `ResponsiblePersonDesignation` (pad: Md. Parvez Mosharaf, Managing Director),
  `NumberPrefix` (`MW`), `CreditNoteMaxAgeDays` (180, §9), `ShippingVatRate` (§7).
- **Live:** every required fact must be present and valid, or the host refuses to start (fail closed). Owner decision 4 is
  met by construction: no real number can be taken without the facts.
- **Specimen:** allowed outside Production only. Series `SPEC-…` (own counters), PDF watermarked "SPECIMEN · NOT A TAX
  INVOICE"; missing facts print as "‹missing›". For staging, demos and the consultant's sample (E6.0 exit).
- **Off:** no invoice is issued; ship works as today. Allowed in Production only as an explicit choice (owner, §18 O1),
  with a warning log on start and on each ship. Switching Off → Live never issues invoices for orders shipped while Off.
- `Specimen` in Production stops the host. Production has **no default** for `Mode`: it must be written in the deploy
  config (H3), so nobody gets Off or Live by accident.

### 4. Numbering: per series and fiscal year, gap-free, inside the transaction
- **Format:** `{Prefix}-INV-{FY}-{serial:D6}` and `{Prefix}-CN-{FY}-{serial:D6}`, e.g. `MW-INV-2026-27-000482`,
  `MW-CN-2026-27-000017`; specimen `SPEC-INV-2026-27-000001`. FY label = `yyyy-yy` of the July–June year. Serial starts at
  1 each fiscal year; above 999,999 it simply gets longer. (This replaces the example `MW/6.3/2026-27/000482` in rule I1:
  no slashes, so a number is safe in file names and URLs.) The owner's paper pad series `MW-<client>-INV-<ddmmyy>` is a
  separate, manual series and is never continued by the website (§19 Q6).
- **Series** = (issuing seller, kind, mode): `tax_invoice`, `credit_note`, and the two specimen ones. Credit notes have
  their own series (§19 Q6).
- **Counter table** `invoice_number_counters(seller_id, series, fiscal_year, next_serial, last_issued_at)`, primary key
  `(seller_id, series)`: **one row per series**, carrying the current fiscal year.
- **Taking a number** (inside the issuing transaction, as the **last** lock of the transaction, §15):
  1. `INSERT … ON CONFLICT DO NOTHING` the series row once (first invoice ever; fiscal year 0, next 1).
  2. `SELECT … FROM invoice_number_counters WHERE seller_id = $1 AND series = $2 FOR UPDATE`.
  3. `SELECT clock_timestamp()` **after** the lock = the issue instant `t` (§5); FY(t) from the Dhaka date.
  4. If FY(t) > row FY: the year rolls over: serial = 1. If FY(t) = row FY: serial = `next_serial`. If FY(t) < row FY or
     `t < last_issued_at`: refuse (500 `INVOICE_CLOCK_BEHIND`, error log + alert): the database clock went backwards.
  5. `UPDATE` the row (`fiscal_year`, `next_serial = serial + 1`, `last_issued_at = t`) and `INSERT` the invoice with that
     serial, FY and `issued_at = t`. Commit.
- **Why no gap:** the increment is an ordinary row update in the same transaction as the invoice insert. If anything
  fails before COMMIT (validation, a constraint, a crash, a lost connection, the ship refusing), PostgreSQL rolls back both,
  and the next issuer gets the same serial. Two issuers serialise on the row lock: the second waits, then reads the
  committed row (READ COMMITTED re-reads the locked row) and gets the next serial. A crash **after** COMMIT leaves a complete
  invoice (the PDF is rendered later, §12), never a hole.
- **Why not a sequence:** `nextval()` is deliberately non-transactional: a value handed out is never given back on
  ROLLBACK; values cached per session (`CACHE > 1`) are lost when the session ends; after a crash the sequence may jump
  ahead (it WAL-logs values in advance). Each of these makes a hole, and NBR auditors read a hole as a hidden invoice.
  Sequences also cannot restart per fiscal year inside a transaction.
- **Backstops (database, E6.2/E6.3b):** unique `(seller_id, series, fiscal_year, serial)` and unique `number`; a guard
  trigger on the counter allows only "next + 1 in the same year" or "rollover to a later year with next = 2"; no DELETE or
  TRUNCATE; a **deferred constraint trigger** checks at COMMIT that every counter change has exactly the invoice with serial
  `next − 1` and no invoice with a higher serial (pattern: B3.3 deferred chain check). The nightly reconciliation (§17)
  checks "count = max serial, min = 1" per series and year.
- **Throughput:** issues of one series serialise for the few milliseconds between the counter lock and COMMIT. Nothing slow
  (PDF, storage, e-mail, HTTP) runs in that window. For B2B volumes (tens a day) this is no limit.

### 5. Issue instant, fiscal year and clocks
- The issue instant is the **database's** `clock_timestamp()` read after the counter lock, never an application clock and
  never `now()` (transaction start). So serial order = time order within a series, and app servers with skewed clocks cannot
  put invoice N+1 before invoice N or into the wrong year.
- Fiscal year: the Dhaka date `d = (t + 06:00).Date`; FY start year = `d.Month >= 7 ? d.Year : d.Year − 1`. Edge: 30 June
  23:59:59 Dhaka (= 17:59:59 UTC) is the old year; 18:00:00 UTC on 30 June is 1 July Dhaka, serial 1 of the new year.
- The printed date and time are Dhaka local (`dd-MM-yyyy HH:mm`); the stored value is UTC.
- The 9.1 month of an invoice is the Dhaka calendar month of `t` (§13).

### 6. Snapshot and content hash (I3, I4)
Everything printed is copied into the invoice rows at issue time and never read live again:
- **Seller:** the §3 facts of that moment (name EN/BN, BIN, TIN, trade licence, both addresses, responsible person).
- **Buyer:** company orders: `Company.Name`, `Company.Bin`, billing address of the company at that moment; personal orders:
  the user's display name and the order's ship-to address, BIN empty ("unregistered"). PO number, quote number ("Against
  quotation MWQ-…"), order number.
- **Delivery:** the order's `ShipToJson` address and the shipment's courier name + tracking number (as "vehicle / transport",
  §19 Q4).
- **Lines:** from the order's lines of that seller's sub-order (I4: copied, never recomputed), plus one delivery line for the
  sub-order's shipping (§7).
- **Content hash** = lowercase hex SHA-256 of a canonical UTF-8 JSON (fixed property order, no whitespace, money as integer
  poisha, VAT rates as 4-decimal strings, instants ISO-8601 UTC) of all of the above plus number, kind, series, FY, serial,
  issue instant, original invoice (credit notes) and the consultant-check flag. Written in the issuing transaction; printed
  (first 16 hex) on the PDF; recomputed nightly (§17). Same canonicaliser pattern as ADR-0011 §6.
- Buyer check in Live mode: a company order needs an **Active** company whose BIN passes the format check. There is **no
  silent fallback** to "unregistered": the ship is refused with 409 `INVOICE_BUYER_BIN_INVALID` so staff fix the company
  first (B1 verification). A wrong BIN discovered later is corrected by §9.4.

### 7. VAT and rounding (consistent with ADR-0002 and ADR-0008)
- Line money is the order line's money exactly: `gross`, `discount`, `net` (value excl. VAT), `vat`, `total`. The invoice
  total of a seller = Σ lines + delivery line = the sub-order's `TotalMinor`; for the launch (one seller) = the order's
  `GrandTotalMinor`, to the poisha (I4).
- **Unit price excl. VAT:** company orders store it exactly (net basis). Personal orders store a VAT-inclusive unit price;
  the 6.3 prints `net / quantity` to 2 decimals, banker's rounding, **for display only**, with a footnote "unit prices
  rounded; line values are exact". The line value columns are authoritative.
- **Discount:** printed as its own column; the value excl. VAT is after the discount (§19 Q5).
- **VAT-inclusive public prices** (ADR-0008 §12 decision 2) do not change anything here: the invoice shows the net and the VAT
  that the order already split; the buyer pays what the order says.
- **Delivery line:** amount = the sub-order's `ShippingMinor`, VAT split with `Invoicing:ShippingVatRate` (default **0**,
  marked "consultant check"; the owner action "VAT on shipping" is still open): `net = round_even(amount / (1 + rate))`,
  `vat = amount − net`, so the total never changes. When E8 later stores a shipping VAT rate on the sub-order, the invoice
  copies that instead.
- **SD (supplementary duty):** printed as 0 / "—" (industrial goods at launch carry none as best known, §19 Q9).
- **Totals:** Σ net, Σ SD, Σ VAT, Σ total; amount in words (lakh/crore, EN and BN, reuse D4.11).
- No new rounding anywhere: the only derived number on the whole document is the displayed unit price above.

### 8. Immutability (I2)
- Tables `invoices` and `invoice_lines` (one table for both kinds, §16). Guard trigger (pattern `QuoteSql` /
  `StockMovementSql`): **INSERT only**; UPDATE refused except `pdf_{en,bn}_{key,sha256}` null → value once; DELETE and
  TRUNCATE always refused. Lines: no UPDATE, DELETE or TRUNCATE at all.
- Foreign keys `RESTRICT` (orders, shipments, sub-orders, companies, original invoice); deleting an order with an invoice is
  impossible.
- No domain method changes an issued invoice. There is no "void" or "cancel" state: a wrong 6.3 is answered by a 6.7 (§9).
- A superuser who disables triggers is the residual risk; the hash-chained audit, the content hash and the nightly
  reconciliation detect it after the fact (§17).

### 9. Credit notes (Mushak-6.7) (I2, rule "cannot exceed the original")
#### 9.1 What a credit note is
- Always against exactly one tax invoice of the same seller (`original_invoice_id`), with a reason
  `GoodsReturned | PriceReduction | Error | Other` and a text (5–500). It has its own number (§4 `credit_note` series),
  snapshot (copied from the original, not from live data), content hash and PDF.
- **Lines** reference original lines (`original_line_id`): credited quantity (0 … what is left) and credited value excl.
  VAT (1 … what is left). VAT of a credit line = `round_even(net × rate)` of the original line, **except** when the line
  becomes fully credited: then it takes exactly the remaining net and VAT, so a fully credited line always sums to zero
  (no poisha left behind). The delivery line can be credited the same way.
#### 9.2 Never more than the original
- Per original line: Σ credited quantity ≤ invoiced quantity; Σ credited net ≤ line net; Σ credited VAT ≤ line VAT.
  Per invoice: Σ credit-note totals ≤ invoice total. Enforced in the domain (pure `CreditNotePlanner`, property-tested), in
  the service under a lock (the original invoice row `FOR NO KEY UPDATE`, so two parallel credit notes of one invoice
  serialise), and by a **deferred constraint trigger** at COMMIT summing all credit lines per original line.
- Only a tax invoice can be credited (not a credit note), only in Live/Specimen of the same mode, and only within
  `CreditNoteMaxAgeDays` of the original's issue (default 180, §19 Q3); older → 409 `CREDIT_NOTE_TOO_LATE` (the money side
  can still be settled by a B3 adjustment, without a VAT adjustment).
#### 9.3 Who: request → approve (four eyes)
- A credit note gives money back, so it follows the `Refund` / `CreditAdjustment` pattern: **request** by a person with
  `invoices.credit_request` (Support, Finance, Admin); **approve** by a different person with `invoices.credit_approve`
  (Finance, Admin), step-up `invoice.credit_note`, approver ≠ requester (domain + DB CHECK). Only the approval takes a
  number, inside its transaction. A request never has a number and can be rejected or withdrawn.
- `Idempotency-Key` + request hash on the request (same key = same request, other body = 409).
#### 9.4 Correcting a wrong invoice (wrong BIN, wrong buyer)
- Full credit note with reason `Error`, then a **replacement** 6.3 for the same shipment (`replaces_invoice_id`), issued
  by the approver of that credit note in the same transaction from the same lines, re-reading only the buyer facts
  (after the company record was fixed). DB: at most one tax invoice per (shipment, seller) that is not fully credited
  (`replacement_no` 0, 1, 2 …; a replacement needs the previous one fully credited). §19 Q7.

### 10. Relation to orders, quotes, credit, refunds, returns
| Event | Before shipment (no invoice) | After shipment (invoice exists) |
|---|---|---|
| Staff cancel (E7.2) | As built: `ReduceChargeAsync(OrderCancelled)`, refund flag, restock | Not possible: E7.2 refuses Shipped and later |
| Return of goods (F2) | n/a | F2's accepted return asks E6 for a credit note `GoodsReturned` of the returned lines (contract below); restock stays F2's |
| Price reduction / goodwill | Change nothing on the order (orders are immutable) | Credit note `PriceReduction` |
| Wrong invoice data | n/a | §9.4 |
| Credit order money effect | E7.2 | Credit note approval calls `ReduceChargeAsync(CreditReduction(OrderId, CreditNote, total, "creditnote:{id:N}", id, approver, reason))` in the same transaction; cancellation + credit notes are capped together by ledger I6 |
| Prepaid order money effect | E7.2 refund flag | The credit note records `refund_due_minor`; a captured gateway payment gets a refund request (four eyes, as E7.2); bank-transfer / COD money back is a manual payout recorded by Finance (no gateway; E4/E3.5 own the method, §18 O5) |

- **Contract for F2** (F2 owns its card; E6.5b builds this): `ICreditNoteService.RequestForReturnAsync(orderId, returnId,
  lines[{orderItemId, quantity}], actor, reason)` → a credit-note **request** (not yet issued), idempotent on `return:{returnId}`;
  value per line = the original line's net × quantity / invoiced quantity, the last unit taking the remainder. Approval then
  follows §9.3. A return of an order shipped while invoicing was Off has no invoice: F2 refunds without a credit note.
- **Quotes:** the 6.3 prints "Against quotation {number} rev. {n}" when the order came from a quote; prices are the order
  line's (which are the quote's).
- **Credit (B3):** the credit charge still happens at confirmation and the due date counts from confirmation (ADR-0010
  OWNER 1, §18 O3). Statements and aging gain the invoice number per order (read-side join, E6.8a). VDS/AIT §11.
- **Payment receipts** (money receipts for bank transfers / COD) are not tax invoices and are not in E6.

### 11. VDS and AIT for B2B buyers (E6.6, design as far as possible without the consultant)
- **VDS (VAT deducted at source):** some buyers (government, banks, NGOs, large companies… as best known) must keep part of
  the VAT, pay it to the treasury, and give the supplier a Mushak-6.6 certificate. For MECHWARE this means: the buyer pays
  less than the invoice, and the difference is settled by the certificate, which the 9.1 claims.
- **AIT (advance income tax deducted at source):** the buyer keeps an income-tax amount from the payment and gives a tax
  deduction certificate. It is income tax, not VAT: it is **not** in the 9.1.
- **Design:** the invoice is unchanged (it always shows the full VAT). A new record `tax_withholdings` (kind `Vds | Ait`,
  invoice id, certificate number, certificate date, amount, recorded by, verified by, status) is **recorded by Finance and
  verified by another Finance/Admin** (the `CreditPayment` maker-checker pattern, step-up on verify). Verification posts a
  ledger line that settles the order's receivable like a payment (B3 kind `withholding_applied`, new; one migration in B3's
  ledger kinds). Caps: Σ VDS of an invoice ≤ its VAT less VAT already credited (I8); Σ AIT ≤ its net value less credited
  net (the true AIT rate band is §19 Q11); a certificate number is unique per (company, kind).
- Company flag `withholding_entity` (Admin, step-up, audited) only drives a hint on the invoice PDF ("If you deduct VAT or
  tax at source, please send us the certificate") and the payment-screen text. It never changes amounts.
- **Launch limits:** only for credit orders (a receivable exists); no file upload until C3 + V5 (Finance types the
  certificate facts from the paper/e-mail; I7 applies when uploads come); prepaid withholding buyers are §18 O6.

### 12. PDF, SHA-256 and archive (A8, D4.11 patterns)
- Built **only from the snapshot rows** (§6), never live data. Layout = Mushak-6.3 / 6.7 fields (§ Context) on the
  owner's pad style (D4.11 composer parts reused: band, key-values, branded table, totals, amount in words), heading
  "Tax invoice (Mushak-6.3)" / "Credit note (Mushak-6.7)", locales `bn` and `en` (§19 Q10: is an English 6.3 acceptable?).
  Footer: number · verification ‹first 16 hex of content hash› · "page x of y" · the line **"Prepared to the NBR
  Mushak-6.3 format; not yet checked by a VAT consultant"** while `Invoicing:ConsultantChecked = false` (owner flips it after
  E6.0; the flag is part of the content hash).
- **Rendered eagerly** after COMMIT by an outbox handler for `InvoiceIssuedEvent` (both locales), with the D4.11 lazy
  fallback on first download. Stored with `IObjectStorage` under `invoices/{invoiceId:N}/{locale}.pdf`; key + SHA-256
  recorded null → value once (first writer wins; two renders of one snapshot are byte-identical, ADR-0009). Render and
  upload run outside any row lock.
- **Download** is an authenticated stream only (`private, no-store`, `attachment`), access checked on every request (§14).
  The stored file's SHA-256 is recomputed before sending: mismatch → 503 `INVOICE_PDF_UNAVAILABLE` + error log + audit
  `invoice.pdf_integrity_failed`; missing file → rendered again and served only if the hash equals the recorded one; else
  503 + alert (renderer upgraded: the content hash and the rows still prove the invoice; staff print from the rows, §19 Q12).
- **No public or long-lived URL;** e-mails link to the order page and never attach the PDF (as D4.11).
- Staff check: `GET admin/invoice-documents/{sha256}` → number, kind, locale or 404.
- **Archive:** the rows are the legal record; the stored PDFs are kept as long as the rows (§17). Storage = C3's choice
  (S3 or volume, owner); the backup of that storage is H3's.

### 13. Monthly 9.1 export (E6.7)
- `GET admin/invoices/exports/vat-month?month=2026-12&format=csv` (and the same as JSON for the console preview).
  Permission `invoices.export` (Finance, Admin), **step-up** `invoice.export`, rate limit per person (B3.8), audit
  `invoice.exported` (month, row count, totals, actor, IP), `private, no-store`, CSV formula neutralisation, never e-mailed.
- Period = Dhaka calendar month of the issue instant, Live series only (specimen never appears).
- Content (one file, sections the accountant maps to 9.1 notes, §19 Q2): (a) sales register: every 6.3 of the month (number,
  date/time, buyer name, buyer BIN, net, SD, VAT, total, VAT rate band); (b) every 6.7 of the month (number, original
  number and month, net, VAT); (c) verified VDS certificates of the month (§11); (d) totals per VAT rate (standard,
  reduced, zero, exempt); (e) the 6.10 candidate list (single invoices or credit notes above `Invoicing:Mushak610ThresholdMinor`,
  default BDT 2,00,000); (f) **check list:** orders confirmed with money received in the month but not shipped by month end
  (the §2 advance-payment question).
- Proof: totals of (a) = Σ invoices of the month, (b) = Σ credit notes, from an independent SQL in the test.
- The export never writes anything and never changes an invoice.

### 14. Access and API sketch (`api/v1`, JSON `data` envelope, all reads `private, no-store`, `Vary: Authorization`)
**Buyer** (access = exactly the order's own visibility rule: the placer, and company members who may see the company's orders;
everything else, a foreign id or an unknown id = the same 404):
| Method and path | Purpose |
|---|---|
| `GET orders/{orderId}/invoices` | the order's invoices and credit notes (number, kind, date, totals, original) |
| `GET orders/{orderId}/invoices/{invoiceId}/pdf?locale=bn\|en` | stored PDF stream, rate limit `InvoiceRead` |
| `GET companies/{companyId}/invoices?from&to&page` | company list (company Admin and Buyer roles, like B3 OWNER 6) |
No by-number lookup for buyers: numbers are sequential by law, so they are guessable (§ residual risks).

**Staff:** `invoices.view` (Support, Finance, Admin): `GET admin/invoices` (filters: number, order, company, kind, month),
`GET admin/invoices/{id}`, `GET admin/invoices/{id}/pdf`, `GET admin/invoice-documents/{sha256}`.
`invoices.credit_request`: `POST admin/invoices/{id}/credit-notes` (Idempotency-Key) · `invoices.credit_approve` + step-up:
`POST admin/credit-note-requests/{id}/approve | reject` · `GET admin/credit-note-requests` (queue).
`invoices.export` + step-up: §13. `credit.record_payment` / `credit.verify_payment` reuse for §11
(`POST admin/invoices/{id}/withholdings`, `…/withholdings/{wid}/verify | reject`).
Issuing has **no endpoint**: it happens only inside ship.

### 15. Concurrency and lock order
Global order (ADR-0010 §7, E7.2, ADR-0011 §9) extended with two tiers: `quotes → quote_versions → rfqs → gateway payments →
orders → invoices → credit_payments | credit_adjustments | tax_withholdings → companies → promotions → invoice_number_counters`.
- **Ship + issue:** `orders` NO KEY UPDATE → inserts (shipment, invoice, lines; their FKs take KEY SHARE) → counter FOR UPDATE
  → update counter → COMMIT. Nothing after the counter.
- **Approve a credit note:** gateway payments of the order (FOR UPDATE, id order; only when a refund request is made) → `orders`
  NO KEY UPDATE → original `invoices` row NO KEY UPDATE → `ReduceChargeAsync` (companies) → counter → inserts → COMMIT.
- **Verify a withholding:** `orders` NO KEY UPDATE → `invoices` NO KEY UPDATE (cap check) → `tax_withholdings` NO KEY UPDATE →
  companies (ledger). No counter.
- **Rule:** the counter is always the last lock; no path that holds a counter locks anything else. Reads (lists, PDF,
  export, reconciliation) take no row lock.
- `FOR NO KEY UPDATE` on `orders` and `invoices` because invoice lines, credit notes and ledger lines reference them by foreign
  key (B3.5 lesson). The counter has no incoming FK, so `FOR UPDATE` is safe there.
- Ship stays idempotent through the order state: a second ship of a Shipped order is 409 `ORDER_INVALID_TRANSITION` before
  anything is issued; unique `(shipment_id, seller_id, replacement_no)` is the backstop (23505 → 409, never 500).

### 16. Data model (migrations: `AddInvoiceNumbering` E6.2, `AddInvoices` E6.3b, `AddCreditNoteRequests` E6.5a, `AddTaxWithholdings` E6.6a)
Enums as text with CHECK lists, `xmin` tokens, FKs `RESTRICT`, no deletes.
- **`invoice_number_counters`** (§4).
- **`invoices`:** `id`, `kind` (`TaxInvoice | CreditNote`), `mode` (`Live | Specimen`), `number` unique, `seller_id`, `series`,
  `fiscal_year` (start year), `serial`, `issued_at`, `order_id`, `sub_order_id`, `shipment_id` (tax invoice), `replacement_no`,
  `replaces_invoice_id` null, `original_invoice_id` null (credit note), `reason`, `reason_text`, `company_id` null,
  `seller_snapshot` jsonb, `buyer_snapshot` jsonb, `delivery_snapshot` jsonb, `purchase_order_number`, `quote_number`,
  `order_number`, `currency`, `net_minor`, `sd_minor`, `vat_minor`, `total_minor`, `refund_due_minor` (credit note),
  `consultant_checked` bool, `content_hash` char(64), `issued_by` (the staff member who shipped, or the approver of a
  credit note), `requested_by` (credit note), `pdf_en_key/sha256`, `pdf_bn_key/sha256`.
  CHECKs: kind shape (tax invoice ⇒ shipment, no original; credit note ⇒ original, reason, issued_by ≠ requested_by),
  money ≥ 0, `total = net + sd + vat`, hash format, number matches series/FY/serial.
  Uniques: `number`; `(seller_id, series, fiscal_year, serial)`; `(shipment_id, seller_id, replacement_no) WHERE kind = 'TaxInvoice'`.
- **`invoice_lines`:** `id`, `invoice_id`, `line_no`, `kind` (`Goods | Delivery`), `order_item_id` null, `original_line_id` null,
  `sku`, `part_number`, `description_en/bn`, `unit` (`pcs` until the catalogue has units, §19 Q5), `quantity`,
  `unit_price_minor` (exact, company) or null, `display_unit_price` numeric(18,2) (personal), `gross`, `discount`, `net`,
  `sd_rate`, `sd`, `vat_rate`, `vat`, `total`. CHECK `total = net + sd + vat`, all ≥ 0.
- **`credit_note_requests`** (E6.5a): request → approved/rejected/withdrawn, lines as jsonb, `idempotency_key`,
  `request_hash`, `source_key` (`return:{id}` unique when present), `issued_invoice_id` null.
- **`tax_withholdings`** (E6.6a) §11.

### 17. Audit, reconciliation, retention, privacy (I9, I10)
- Audit actions (hash-chained log, G5.4a): `invoice.issued`, `invoice.issue_refused` (setup/BIN/seller), `invoice.pdf_rendered`,
  `invoice.pdf_integrity_failed`, `invoice.credit_requested | credit_approved | credit_rejected | credit_withdrawn`,
  `invoice.replaced`, `invoice.exported`, `invoice.withholding_recorded | verified | rejected`. Outbox:
  `InvoiceIssuedEvent`, `CreditNoteIssuedEvent` (ids only; e-mail "your invoice is ready" links to the order page, F3).
- **Nightly `InvoiceReconciliationWorker`** (B3.10 pattern: alert + audit, never repairs, no row locks): no gap or duplicate
  per series and year; each stored content hash equals the recomputed one; each Live-mode shipped order has exactly one live
  tax invoice per issuing seller; invoice totals = sub-order totals; Σ credit notes ≤ original per line and per invoice;
  Σ withholdings within caps.
- **Retention:** invoices, credit notes, their PDFs, withholdings and the related audit rows are never deleted by the system.
  Earliest permitted deletion = `Invoicing:RetentionYears` (default 6, the longer of the believed VAT and income-tax
  periods, §19 Q13) after the end of the fiscal year; no deletion job is built.
- **Erasure (PDPO):** an erased user's invoices stay unchanged (legal record, rule I9: "ErasureKeepsInvoices_MasksTheRest");
  only non-legal data elsewhere is masked. The invoice snapshot already holds only what the form needs.

### 18. Owner decisions (defaults apply until answered)
- **O1** Production mode at launch if BIN/address are still missing: (a) **Live and wait** (shipping blocked until the facts
  are in, recommended: no website shipment without a 6.3) or (b) `Off` and the paper pad invoice meanwhile.
- **O2** Who requests and who approves credit notes: default Support/Finance/Admin request, Finance/Admin approve with an
  authenticator code, never the same person. Needs two finance/admin people (as B3).
- **O3** Credit due date: from confirmation (ADR-0010 OWNER 1, as built) or from the invoice (= shipment) date (the pad says
  "due within 7 days of the invoice").
- **O4** Print language: default both Bangla and English PDFs are offered; which is the "official" printed copy that travels
  with the goods (default Bangla, subject to Q10).
- **O5** Money back on a credit note of a prepaid bank-transfer or COD order: default a manual payout recorded by Finance
  (four eyes), until E4/E3.5 define it.
- **O6** Withholding buyers who prepay: default they must pay in full or use credit terms; VDS/AIT certificates are accepted on
  credit orders only.
- **O7** `CreditNoteMaxAgeDays` default 180 (Q3) and the 6.10 threshold BDT 2,00,000 (Q2).

### 19. Consultant questions (E6.0, for the owner to forward; plain English)
1. **When must the Mushak-6.3 be issued?** We plan to issue it when the goods are handed to the courier. If a buyer pays in
   advance by bank transfer days earlier, is the VAT due in the month of payment, and must we issue the 6.3 at payment
   instead?
2. **9.1 return mapping:** which note of Mushak-9.1 does each of our figures go to (standard-rated sales, credit notes, VDS
   certificates received)? Do we need to file Mushak-6.10 (sales over BDT 2 lakh) and keep a Mushak-6.2 sales register, and is
   our export enough for both?
3. **Credit note time limit:** is there a deadline (we think 6 months) for issuing a Mushak-6.7 after the sale? What if a
   return comes later?
4. **Courier delivery:** the 6.3 asks for vehicle type and number. When goods go by a courier company, is the courier name
   and tracking number acceptable there?
5. **Line format:** may we show a discount column and the unit "pcs" for all items? For retail (VAT-inclusive) prices, is a
   unit price rounded to 2 decimals acceptable when the line values are exact?
6. **Numbering:** is one series per fiscal year (July–June) starting at 1 correct, with credit notes in their own series?
   Can the website series (`MW-INV-2026-27-000001`) run alongside the paper invoice pad, or must there be one series for the
   whole business?
7. **Correcting a wrong invoice** (for example a wrong buyer BIN): is "full credit note, then a new 6.3" the right method?
8. **BIN check:** is a BIN always 13 digits? Should we check buyer BINs online before invoicing?
9. **SD:** do any of our goods (industrial automation parts: PLCs, drives, sensors, cables) carry supplementary duty, or
   reduced/zero VAT rates? Is delivery (shipping) charged to the buyer subject to VAT, at what rate?
10. **Language and copies:** is an English 6.3 valid, or must it be the Bangla form? How many copies, and is a signature or
    seal required on a computer-printed invoice?
11. **VDS and AIT:** for which buyers and goods does VDS apply to us, and at what rate? Which AIT rate applies to supply
    payments, and do we need anything else from the buyer than the certificate?
12. **Electronic invoices:** are we required to use an NBR-approved EFD/SDC device or the online system for invoices at our
    size, or is our own computer system acceptable?
13. **Record keeping:** how many years must invoices, credit notes and the sales register be kept, and is a PDF archive
    acceptable?
14. **Marketplace:** if other sellers later sell through our website, who issues their 6.3, us or them?
15. **Sign-off:** please check one sample 6.3 and one sample 6.7 (specimen PDFs) and confirm in writing that we may use them.

### 20. Not in E6 (launch)
Debit notes (Mushak-6.8, an upward correction: a new 6.3 for the extra amount until the consultant says otherwise);
third-party seller invoices (H5); partial-shipment invoices (E6.9, after F1.4); issue at confirmation (E6.3e, only if Q1 says
so); EFD/IVAS integration (Q12); certificate file uploads (after C3 + V5); USD invoices; purchase-side registers (6.1).

## Consequences
### Positive
- No shipped order without a tax invoice in Live mode, and no real number without the company facts (owner decision 4).
- Gap-free by construction (one row, one transaction), with three backstops (unique, deferred check, nightly check).
- Cancellations before dispatch need no credit notes; partial shipments later reuse the same issue path.
- Every amount is the order's own (I4); the only display rounding is the personal-order unit price.
- PDF, hash, store and download reuse the D4.11 code paths; the audit chain already exists.

### Negative
- Shipping of one issuing seller is serialised through the counter for a few milliseconds per invoice.
- If NBR treats an advance payment as the tax point, the document date and the return month can differ (§2; export check
  list (f) helps the accountant).
- Credit days count from confirmation while the invoice is dated at dispatch, until O3 is answered.
- Live mode blocks shipping until the owner supplies BIN and address (by design).
- Sequential invoice numbers reveal sales volume to anyone holding two invoices (unavoidable, legal requirement).
