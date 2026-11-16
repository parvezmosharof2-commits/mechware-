# ADR 0010: Credit Settlement (receivables ledger, customer payments, credit release)

## Status
Proposed 2026-11-16 (unit B3.1, design only, written by an AI assistant, not yet reviewed by a person). Six points need
the owner (§13). Each has a recommended default. The units that depend on one are marked in `docs/modules/B3.md`, and
the design does not wait for the answers.

## Date
2026-11-16

## Context
Brief §8.5/§8.7: verified companies may buy on Net-15 or Net-30 terms up to a limit set by MECHWARE finance
(`Company.SetCredit`, step-up `company.credit`). **What the code does today:**
- **When credit is used:** at **confirmation**, never at placement while an approval is pending. A credit order with no
  approval rule is confirmed inside `OrderService.SaveAsync`: `companies` row `FOR UPDATE` (plus the `xmin` token),
  `Company.ReserveCredit(order.GrandTotalMinor)`, `order.Confirm(...)`, one transaction. An order that waits for an
  approval holds no credit. `ApproveOrderAsync` takes the order row `FOR UPDATE` then the company row `FOR UPDATE` and
  reserves credit. If credit ran out meanwhile, the order is cancelled ("credit no longer covers this order").
- **What "used" means:** `companies.credit_used` = the sum of the grand totals of every confirmed credit order, ever.
  The check is `amount ≤ limit − used`. `ReserveCredit` refuses a non-active company (`COMPANY_NOT_ACTIVE`) and
  `NO_CREDIT_TERMS`.
- **When it is released: never.** `Company.ReleaseCredit` exists (it refuses to go below zero) but only unit tests call it.
  A confirmed order cannot be cancelled (`ORDER_NOT_CANCELLABLE`, E7.2 pending). There is no record of customer
  payments, no due date (`orders.payment_due_at` is the gateway unpaid-expiry deadline, not a credit due date), no
  statement and no history. `credit_used` is a bare number that cannot be explained.
- Any accepted member of the company, of any role, sees limit, used and available through `GET /companies/{id}`.
- Existing patterns reused here: four-eyes `Refund.Approve`, `[RequireStepUp("…")]` (header `X-Step-Up-Code`), the
  append-only trigger of `stock_movements`/`audit_logs`, `PaymentReconciliationWorker`, Dhaka fixed `+06:00` dates
  (E8.3), IDOR 404 rules of D2.10.

Result: a Net-30 customer who has bought up to the limit stays blocked forever. Finance has no system of record for
cheques and transfers.

## Decision

### 1. Vocabulary and invariants
- **Receivable** of an order: what the company still owes for one confirmed credit order (≥ 0).
- **On-account money:** money MECHWARE holds for the company that is not applied to any order (over-payment, the paid
  part of a cancelled order, the excess of a credit note) (≥ 0).
- **`Company.CreditUsedMinor`** stays the cached number that order placement checks under the row lock. Its meaning
  becomes "the sum of open receivables". New cached `Company.CreditOnAccountMinor` and `Company.CreditLedgerSeq`.
- **Invariants (enforced by the database at commit, §2.3, and checked again nightly, §11):**
  I1 `credit_used = Σ receivable_delta` of the company's ledger · I2 `credit_on_account = Σ on_account_delta` ·
  I3 entries are numbered 1, 2, 3 … per company without gaps, and each row's `*_after` = previous `*_after` + its
  delta · I4 per order `0 ≤ receivable ≤ charge` · I5 per payment, applied + still on account = amount (0 after a bounce) ·
  I6 the reductions of an order (credit notes, cancellation, write-off) never exceed its charge.
- `CreditAvailable = limit − used` can go negative after a bounce (§8.1). UIs show it as 0, and orders are refused as today.
  On-account money does **not** raise available credit until it is applied to an order (§6).

### 2. The ledger: `credit_ledger_entries` (append-only)

#### 2.1 Columns
| Column | Rule |
|---|---|
| `id`, `company_id`, `seq` | `(company_id, seq)` unique; `seq = companies.credit_ledger_seq + 1`, taken under the company row lock. |
| `event_id` | Groups the rows of one business event (one verification can write several rows). |
| `kind` | §2.2. Stored as snake_case text with a CHECK on the list. |
| `order_id`, `payment_id`, `adjustment_id`, `credit_note_id` | What the row is about; rules per kind (CHECK). `payment_id` / `credit_note_id` on on-account rows name the **source** of that money. |
| `receivable_delta_minor`, `on_account_delta_minor` | Signed `bigint` poisha. At least one is non-zero; the signs per kind are a CHECK. |
| `receivable_after_minor`, `on_account_after_minor` | Running totals after this row; both `≥ 0` (CHECK). |
| `due_on` | `date`, required on `charge` rows only (§3). |
| `occurred_on` | Business date (Dhaka): confirmation date, bank received/cleared date, decision date. |
| `currency` | `char(3)`, CHECK `= 'BDT'`. |
| `idempotency_key` | `varchar(120)`, unique. Deterministic per row: e.g. `charge:{orderId}`, `verify:{paymentId}:{orderId}`, `verify:{paymentId}:on_account`. |
| `actor_user_id` (null = system), `correlation_id`, `reason`, `created_at_utc` | No IP address here: the IP goes in the matching `audit_logs` row, which can be pruned. The ledger cannot be pruned. |

#### 2.2 Kinds and signs (R = receivable delta, A = on-account delta, X > 0)
| Kind | R | A | Required | Written by |
|---|---|---|---|---|
| `charge` | +X | 0 | order, `due_on` | confirmation of a credit order (E2 path, §3) |
| `payment_applied` | −X | 0 | order, payment | verification (§7) |
| `payment_on_account` | 0 | +X | payment | verification: the part not applied |
| `on_account_applied` | −X | −X | order, source (payment or credit note) | staff "apply on-account" (§6) |
| `payment_reversed` | +X or 0 | 0 or −X | payment (+ order when R) | bounce (§8.1): one row per order the money reached, one for the part still on account |
| `charge_reduced` | −X or 0 | 0 or +X | order (+ source on A rows) | order cancelled (E7.2) or credit note (E6.5), §8.2 |
| `write_off` | −X | 0 | order, adjustment | approved adjustment (§8.3) |
| `on_account_correction` | 0 | ±X | adjustment, source | approved adjustment (§8.3) |
| `on_account_payout` | 0 | −X | adjustment, source | money paid back to the customer by bank (§8.3) |

No kind can raise a receivable except `charge` and `payment_reversed`. A debt is created only by an order (and later an
invoice), never by a staff adjustment.

#### 2.3 Database guards (installed by `CreditLedgerSql.Install`, like `StockMovementSql`)
1. **Append-only:** `BEFORE UPDATE OR DELETE` row trigger plus a `TRUNCATE` statement trigger raise
   `insufficient_privilege`.
2. **Chain:** a `BEFORE INSERT` trigger checks `seq = previous seq + 1` and each `*_after = previous *_after + delta`
   (previous = the company's highest `seq`).
3. **Cached totals equal the ledger:** a `CONSTRAINT TRIGGER … DEFERRABLE INITIALLY DEFERRED` on ledger inserts **and**
   on updates of `companies.credit_used | credit_on_account | credit_ledger_seq` checks at commit that the company row
   equals the last entry (or 0/0/0 with no entry). A code path that changes `credit_used` without a ledger row, or
   writes a row without updating the company, fails at `COMMIT`. This check costs O(1): it compares with the last row only.
4. CHECKs of §2.1/§2.2. `credit_payments` gets `CHECK (verified_by IS NULL OR verified_by <> recorded_by)`, and
   `credit_adjustments` gets `CHECK (approved_by IS NULL OR approved_by <> requested_by)`.

### 3. Charges and due dates
- A charge is posted **in the same transaction and at the same two places** where `ReserveCredit` runs today (`SaveAsync`
  and `ApproveOrderAsync`). `ReserveCredit` is replaced by `Company.ChargeCredit(orderId, amount, dueOn, …)`, which
  checks the same things, returns the ledger entry and updates the cached totals. Amount = `order.GrandTotalMinor`,
  one charge per order (not per sub-order). Key `charge:{orderId}`: an approval sweep that runs twice cannot charge twice.
- **`due_on` = the Dhaka date of confirmation + the company's terms days at that moment (15 or 30)**, stored on the
  charge. A later terms change does not move it (**OWNER 1** for the start date). E6 plans its invoice at credit
  confirmation, so today the invoice date and the confirmation date are the same day. If E6.9 issues invoices per
  shipment, revisit this.
- An order is **overdue** when `today(Dhaka) > due_on` and its receivable is > 0. Days past due = `today − due_on`.
- **Backfill (in the B3.3 migration):** one `charge` per existing confirmed credit order (actor system, `due_on` from
  `confirmed_at`, or `placed_at` when that is null). If a company's sum differs from `credit_used`, the migration
  aborts and lists the company ids. It never invents an "opening balance". This is pre-launch data.

### 4. Customer payments: `credit_payments`
| Field | Rule |
|---|---|
| `company_id` | Company status Active or Suspended (a suspended company still pays). Pending/Rejected → 404. |
| `amount_minor` | Whole poisha, `1 … Credit:MaxPaymentMinor` (default BDT 10 crore). JSON integer only: `1.5` or `"100"` → 400. BDT only. |
| `method` | `cheque`, `pay_order` (bank draft), `beftn`, `rtgs`, `npsb`, `bank_deposit`, `cash`, `bkash`, `nagad` (manual entry; gateway payments stay E5). |
| `reference` | Normalised: upper-case, spaces and `-` removed, ASCII letters and digits only, 4–40. Cheque/pay order: cheque number; BEFTN/RTGS/NPSB: bank trace/transaction id; deposit: slip number; bKash/Nagad: TrxID (**never the wallet phone number**); cash: none, the server issues receipt number `CR-YYMMDD-XXXXXX`. |
| `drawer_bank` | Cheque, pay order and deposit only: bank name normalised the same way, max 60. |
| `received_on` | Dhaka date: `≤ today` and `≥ today − 30 days`. If it is older, a `late_entry_reason` (10–500 characters) is required and the queue flags the payment. |
| `cheque_date` | Cheque/pay order: `received_on − 180 days ≤ cheque_date ≤ received_on + 180 days`. A post-dated cheque (PDC) may be recorded, but it is not verified before its date. |
| `allocations` | Optional `[{orderId, amountMinor}]`, at most 50 entries, each order at most once, sum ≤ amount (§6). |
| `note` | Staff only, ≤ 500. Never shown to the buyer, CSV-guarded on export. |
| `status` | `recorded` → `verified` \| `rejected`; `verified` → `bounced` (any method except `cash`). |
| `recorded_by/at`, `verified_by/at`, `cleared_on`, `decision_reason`, `idempotency_key` | `cleared_on` = the date the money is in MECHWARE's bank (`≥ cheque_date`, `≤ today`). |

**Duplicates:** partial unique indexes, which exclude `rejected` rows so a mistake can be re-entered:
`(method, reference)` for `beftn, rtgs, npsb, bkash, nagad` (globally unique ids), and
`(company_id, method, drawer_bank, reference)` for `cheque, pay_order, bank_deposit`. The same reference written
differently ("abc-123" / "ABC 123") is the same key. **Record idempotency:** header `Idempotency-Key` (8–100), unique
`(recorded_by, idempotency_key)` with a body hash. A replay returns the same payment; another body gets 409 `IDEMPOTENCY_KEY_REUSED`.
Attachments (deposit slip, cheque scan) wait for C3 storage.

### 5. Maker-checker, permissions, step-up
| Permission (new) | Finance | Admin | Allows |
|---|---|---|---|
| `credit.view` | ✓ | ✓ | queue, aging, statements, CSV (the all-company CSV also needs step-up `credit.export_all`) |
| `credit.record_payment` | ✓ | ✓ | record a payment; apply on-account money (§6) |
| `credit.verify_payment` | ✓ | ✓ | verify / reject a payment **someone else recorded**; record a bounce |
| `credit.adjust` | ✓ | ✓ | request a write-off / on-account correction / payout |
| `credit.adjust_approve` | — | ✓ | approve **someone else's** adjustment |
| `credit.hold_override` | — | ✓ | lift the overdue hold for ≤ 7 days (§9) |

Support, Moderator and Seller roles get none of these.
- **Recording does nothing to credit.** Only verification by a second person releases credit. The rule is
  verifier ≠ recorder, checked in the service (409 `CREDIT_PAYMENT_FOUR_EYES`) and by the DB CHECK. Finance holds both
  permissions, like refunds today. The rule applies per payment, not per role.
- **Step-up:** verify `credit.payment.verify` (**OWNER 3**: every verification, recommended; the other option is only
  above an amount), bounce `credit.payment.bounce` (always), adjustment approval `credit.adjustment.approve` (always),
  hold override `credit.hold.override` (always). Recording and rejecting need no step-up: neither releases credit.
- An amount threshold for four-eyes is **not** offered. Splitting one payment into small records would get around it,
  and every payment is cheap to verify.

### 6. Allocation (applying money to orders)
At **verification** time (not at recording: the receivables may have changed since), the pure
`CreditAllocationPlanner` decides, using the company's open receivables read under the company lock:
1. the recorder's explicit allocations, in the order given. Each must be an order of **this company** with a receivable
   (otherwise 409 `ALLOCATION_ORDER_NOT_OPEN`, the same answer for another company's order and for an unknown one)
   and at most its current receivable (409 `ALLOCATION_EXCEEDS_OUTSTANDING`). The verifier then rejects the payment and
   the recorder re-records it. The amount is never silently clamped;
2. the remaining money goes to the other open receivables **oldest `due_on` first** (tie: `confirmed_at`, then order id);
3. whatever is left becomes on-account money (`payment_on_account`).
There is no rounding anywhere: integer poisha, splits are exact, and Σ rows = amount (property test).
**Apply on-account** (`credit.record_payment`, no four-eyes: the money was already verified and the amount cannot change)
moves on-account money to open receivables with the same planner, consuming the oldest source first. It is audited.
On-account money is never applied automatically at order placement, so the E2 hot path stays small.

### 7. Credit release: the verification transaction
One database transaction, with no external call inside:
`SELECT credit_payments … FOR UPDATE` → status `recorded`, four-eyes, dates (`cheque_date ≤ today`,
`cleared_on`) → `SELECT companies … FOR UPDATE` → read the open receivables per order (ledger aggregate, index
`(company_id, order_id)`) → planner → `Company.ApplyPayment(...)` returns the rows and updates the cached totals →
insert rows → `payment.Verify(verifier, clearedOn)` → one `audit_logs` row (`credit.payment_verified`: actor, IP,
correlation id, before/after totals, event id) → outbox event `credit.payment_verified` (for F3 later) → `COMMIT`.
- **Global lock order** (no cycles): `orders` → `credit_payments` | `credit_adjustments` → `companies` → `promotions`
  → ledger inserts. Placement takes `price_lists` (SHARE) → `companies` → `promotions`. Approval takes `orders` →
  `companies`. Settlement **never locks or updates an `orders` row**: the credit status of an order is derived from the
  ledger (§10). E7.2 must take `companies` before it touches `promotions`.
- **Concurrency:** a verification and a new credit order serialise on the company row. The order sees the released
  credit only after the commit. Two verifications of one payment serialise on the payment row; the second gets 409
  `CREDIT_PAYMENT_NOT_RECORDED`. The deterministic ledger keys are the backstop.
- **Crash safety:** everything commits together or nothing does. A crash before the commit leaves the payment
  `recorded`, and it is verified again.

### 8. Reversals
#### 8.1 Bounce (`credit.verify_payment` + step-up + reason 5–500; any verifier, no four-eyes)
In one transaction: payment `verified → bounced`. For every order the payment's money reached, directly or through
`on_account_applied`, a `payment_reversed` row puts that amount back on the order's receivable. The money still on
account gets an A-negative row. Credit used can go above the limit, and new credit orders are refused as today. Keys
`bounce:{paymentId}:{orderId|on_account}`, so a second bounce is a no-op. The original `due_on` stays, so the order can become
overdue at once. Cash is never "bounced": a mistaken cash entry is corrected by an adjustment. A bank's bounce fee
passed on to the customer is not handled here: it needs an invoice line (E6).

#### 8.2 Order cancellation and credit notes (contract for E7.2 and E6.5)
```csharp
// Mechware.Application.Credit (B3.5). Runs INSIDE the caller's open transaction, after the caller locked the order row.
Task<CreditReductionResult?> ReduceChargeAsync(CreditReduction request, CancellationToken ct = default);
record CreditReduction(Guid OrderId, CreditReductionKind Kind /* OrderCancelled | CreditNote */,
    long? AmountMinor /* null = everything not yet reduced (cancellation) */, string SourceKey /* cancel:{orderId} | creditnote:{id} */,
    Guid? CreditNoteId, Guid? ActorUserId, string Reason);
record CreditReductionResult(long ReceivableReleasedMinor, long MovedOnAccountMinor, bool AlreadyApplied);
```
- It returns `null` (no-op) for an order that is not a credit order or was never charged (for example, cancelled while
  pending approval). It takes `companies FOR UPDATE` itself. It refuses to open its own transaction (unlike
  `RestockAsync`): cancelling and releasing the credit are atomic, so there is no crash window to sweep.
- **Rule:** reduction `R` ≤ charge − earlier reductions (409 `CREDIT_REDUCTION_EXCEEDS_CHARGE`). The receivable falls by
  `min(R, receivable)`. The rest, which is money the customer already paid, becomes on-account money, attributed to the
  payments that paid the order (newest first), one row per source. Example: charge 100, credit note 20, paid 30,
  receivable 50. Cancelling gives `R = 80`, the receivable falls by 50 and 30 goes on account.
- **Idempotency:** same `SourceKey`, same amount → `AlreadyApplied = true`. Another amount → 409 `CREDIT_REDUCTION_CONFLICT`.
- **E7.2** calls it with `OrderCancelled` in its cancel transaction (order row → company → promotions), then runs
  `RestockAsync` after the commit. Once E6 issues invoices, a cancelled invoiced order gets a credit note (E6.5) instead.
  The two never stack, because I6 caps them together. **A refund of a credit order is a credit note**, never a gateway
  refund (`Refund` stays for E4 payments). Money back to the customer's bank is an `on_account_payout` (§8.3).

#### 8.3 Adjustments: `credit_adjustments` (request → approve, the `Refund` pattern)
Types: `write_off` (lower an order's receivable, at most what is outstanding; bad debt: the VAT treatment goes to the
consultant), `on_account_correction` (±, for a mis-posted on-account amount, never below 0), `on_account_payout` (at most
the on-account money; MECHWARE's own bank transfer reference is required). Requested with `credit.adjust` and a reason of
10–500 characters. Approved with `credit.adjust_approve`, step-up, by a different user (409 `CREDIT_ADJUSTMENT_FOUR_EYES`,
plus the DB CHECK). The approver must also not be the user who recorded or verified a payment the adjustment touches.
The ledger rows are written only by the approval, under the same locks as §7.

### 9. Overdue, aging and the credit hold
- **Aging** for open receivables, by days past due: `current` (not yet due), `1–30`, `31–60`, `61–90`, `90+`, per
  company and in total. Staff only.
- **Hold (OWNER 2, recommended):** a new credit order is refused with 409 `CREDIT_HOLD_OVERDUE` while the company has a
  receivable more than `Credit:HoldAfterDaysPastDue` (default **15**) days past due. The check runs under the company
  lock at placement **and** at approval-confirmation. There an order is cancelled with the reason "credit on hold:
  overdue invoices" and nothing is charged. An Admin can lift the hold until a date ≤ 7 days ahead (step-up, reason,
  audited; the column on `companies` is `credit_hold_override_until`).
  The hold **never** blocks: recording or verifying payments, gateway-paid orders, statements, or the company itself
  (suspension stays a separate manual B1 action).
- Approvals (B2) and contract prices (D2) are unchanged: the hold and the credit check apply to the final server total
  at confirmation, as today. Reminders (3 days before due, on the due date, +7 days, at hold) come with F3, through the outbox events.

### 10. Read APIs
**Buyer** (D2.10 rules: the company id is checked against `company_members` on every call; members of that company
with role **Admin or Buyer** only (**OWNER 6**); a non-member, an unknown company, a member without the role or an
unaccepted invitation all get the **same 404**; anonymous gets 401. Unlike D2.10, a **suspended** company still sees
its credit; Pending and Rejected companies get 404. `Cache-Control: private, no-store`, `Vary: Authorization`):
- `GET /api/v1/companies/{companyId}/credit`: terms, limit, used, available (clamped at 0), on-account, overdue
  total, oldest `due_on`, `onHold`, aging buckets, and `pendingPayments` (recorded, not yet verified: date, method,
  amount, last 4 characters of the reference).
- `GET /api/v1/companies/{companyId}/credit/open-items`: per open order: number, charge, receivable, `due_on`, days
  past due. At most 100 per page.
- `GET /api/v1/companies/{companyId}/credit/statement?from&to&page`: ledger lines: date, kind, order number,
  payment method plus the last 4 characters of the reference, both deltas, both running totals. **Never** staff ids,
  names, IPs, notes, reasons of internal adjustments, rejected payments, or anything of another company.
- `OrderDto` gains `credit` (`status` = `open | partially_paid | paid | overdue | cancelled`, `dueOn`,
  `outstandingMinor`) on credit orders only, under the existing `GET /orders/{id}` owner rule. It is derived from the
  ledger, never stored on the order.
- **Finding:** today `GET /companies/{id}` shows limit, used and available to every member, including Requesters. B3.9
  narrows it to the OWNER 6 roles (the others get these fields as null).

**Staff** (`api/v1/admin/...`): record `POST companies/{id}/credit/payments`; queue `GET credit/payments?status=`;
`POST credit/payments/{id}/verify|reject|bounce`; `POST companies/{id}/credit/apply-on-account`;
`POST companies/{id}/credit/adjustments`, `POST credit/adjustments/{id}/approve|reject`;
`GET credit/aging`, `GET companies/{id}/credit/statement` (+ `.csv`), `GET credit/aging.csv` (step-up);
`PUT companies/{id}/credit/hold-override`.
**CSV:** UTF-8 with BOM. Every cell is quoted, and a cell that starts with `= + - @`, tab or CR gets a leading `'`.
Amounts are written twice: integer poisha and BDT with 2 decimals. Every export writes an audit row (filters, row count)
and is rate-limited.

### 11. Reconciliation (`CreditReconciliationWorker`, nightly 02:30 Dhaka, plus a callable service method for tests)
It checks I1–I6 and the chain for every company. It also checks that every confirmed credit order has exactly one
`charge`, that every cancelled charged order has its `charge_reduced`, that no `verified` payment lacks ledger rows, and
it lists payments still `recorded` after 7 days and PDCs whose date has come. A mismatch gives an error log (company and
check name, no personal data), an audit row `credit.reconciliation_mismatch` and the metric
`credit_reconciliation_mismatches`. The worker **never repairs anything**: corrections go through §8.3. Pattern: `PaymentReconciliationWorker`.

### 12. Audit, privacy, retention
- Every ledger-writing command writes **one** `audit_logs` row in the same transaction, with actor, IP, correlation id,
  event id and the cached totals before and after. Ledger rows carry the actor and the correlation id. The audit chain
  worker covers the audit rows.
- **Data:** this is mostly company financial data. Personal data is limited to staff user ids, the IPs (audit only) and
  whatever staff type into `note`. Never store wallet phone numbers or payer national-ID numbers.
- **Retention (RULE, lawyer and VAT consultant to confirm in `docs/LEGAL_REVIEW.md`):** the ledger, payments and
  adjustments are kept at least the statutory accounting period (assumed **6 years after the fiscal year ends**). A
  PDPO/GDPR erasure of a user pseudonymises the person's name in staff screens but keeps the ids in money records.
  A company that has any ledger row cannot be deleted.

### 13. Owner decisions (defaults apply until answered)
1. **When does the 15/30 days start?** Recommended: from order confirmation (that is also the invoice date in E6's plan).
   The other option is the delivery date: that is friendlier to customers but needs F1 courier data and delays cash.
   Affects B3.2, B3.7.
2. **What happens when a customer pays late?** Recommended: new credit orders are blocked automatically once any
   invoice is **15 days** past due; an Admin can lift the block for up to 7 days. Payments are never blocked. Option: a
   flag only, staff decide. Affects B3.7.
3. **Does every payment verification need an authenticator code?** Recommended: yes, every one. The other option is
   only above an amount such as BDT 1,00,000, which splitting can get around. Maker-checker also needs **two people**
   with finance or admin access from day one. Affects B3.4.
4. **Which payment methods do we accept on credit?** Recommended: all of §4, including post-dated cheques (credit freed
   only after clearing) and cash up to `Credit:MaxCashPaymentMinor` (default BDT 2,00,000; the accountant to confirm).
   Affects B3.4.
5. **Over-payments:** recommended: keep them on the customer's account and apply them to their next orders. Paying money
   back needs a request and an approval by an Admin. Option: always pay back at once. Affects B3.6.
6. **Who at the customer may see the balance and statement?** Recommended: the company's Admins and Buyers (not
   Requesters or Approvers). Today every member sees limit and used. Affects B3.9.

### 14. Not in B3
Interest or penalties on late payment, automatic bank-statement import (BEFTN files), gateway payment of an open credit
order (E5 later; it would post `payment_applied` from E4 confirmation), seller payouts waiting for the customer's payment
(H5 marketplace), bounce fees, multi-currency, attachments (C3), e-mail/SMS reminders (F3).

## Consequences
### Positive
- `credit_used` becomes explainable line by line, and the database refuses a drift at commit, not the next morning.
- No single staff account can free credit or write off a debt: four-eyes, step-up and an audit row each time.
- Cancellation, credit notes and bounces share one reduction rule, so they cannot double-release.
- The order placement path changes by one call (charge instead of reserve) and one hold check.
### Negative
- Three tables, three new company columns and a deferred trigger. Every credit write must go through `Company` methods,
  and a test that sets `credit_used` directly now fails at commit (`ConcurrentCreditChanges_…` is rewritten in B3.3).
- The order credit status is computed from the ledger on read. If large companies make it slow, a per-order summary
  table can be added later (H4).
- Two colluding finance staff can still verify a fake payment. The defences are mandatory staff 2FA, step-up, the audit
  trail, nightly reconciliation and the PDC/clearing dates.

## Implementation notes: where B3.2/B3.3 deviate from this design (2026-11-16, AI assistant, not reviewed)
1. **§2.3 item 2, the chain check is deferred, not `BEFORE INSERT`.** It runs in the same `DEFERRABLE INITIALLY DEFERRED`
   constraint trigger as item 3: at COMMIT each new line must have line n − 1 with `*_after` = previous `*_after` + delta
   (line 1: = delta), and the company must equal its last line. Reason: EF Core orders the inserts of one save by key, not
   by `seq`, so a `BEFORE INSERT` check would refuse a valid multi-line event (B3.4 verification). Still O(1) per line.
2. **§3, `ChargeCredit` signature:** `Company.ChargeCredit(orderId, amountMinor, confirmedAtUtc, termsStartUtc, actor,
   correlationId)`; the company computes `due_on` = Dhaka date of `termsStartUtc` + its terms days, so a caller cannot pass
   a wrong date. `termsStartUtc` comes from `Credit:DueDateStart` (owner decision 1; only `Confirmation` is accepted until
   F1 provides delivery dates). Services call it through the pure `CreditLedger.Charge`, which makes a replayed
   `charge:{orderId}` a no-op.
3. **`ReleaseCredit` is deleted, not kept.** Its rule (never below zero) is the receivable ≥ 0 rule of the single internal
   writer `Company.PostCredit` (same code `CREDIT_RELEASE_EXCEEDS_USED`); a public method that lowers `credit_used` without
   a line would fail at COMMIT anyway.
4. **Backfill details (§3):** the confirmation instant is `confirmed_at`, else the first `Confirmed` order event, else
   `placed_at`; it touches only companies with no line yet; it also stops for a company with `credit_used` > 0 and no
   confirmed credit order; it ends with `SET CONSTRAINTS ALL IMMEDIATE` so later DDL in the same transaction meets no
   pending trigger events.
5. **Additions:** the ledger has FKs to `companies` and `orders` (RESTRICT) and CHECKs that a receivable moves only with an
   order and never rises with an adjustment id; options section `Credit` holds all six owner defaults (§13).
6. **Not yet in the schema** (one migration per unit): `companies.credit_hold_override_until` (B3.7).
   `ck_companies_credit_within_limit` was dropped by B3.5 (see the B3.5 notes). (`credit_payments` and `credit_adjustments` came with B3.3b, below.)

## Implementation notes: where B3.3b deviates from this design (2026-11-16, AI assistant, not reviewed)
Migration `AddCreditPayments`; domain `CreditPayment`, `CreditAdjustment`, `CreditAllocationPlanner`. Each point below is a
deviation from, or a decision the text above left open:
1. **§4 duplicate keys are stricter: the method is not part of them.** Transfer ids: unique `reference` across `beftn, rtgs,
   npsb, bkash, nagad` (design: `(method, reference)`). Instruments: unique `(company_id, drawer_bank, reference)` across
   `cheque, pay_order, bank_deposit` (design: with `method`). So the same money recorded under a wrong method (a pay order
   entered once as a cheque, a transfer once as BEFTN and once as NPSB) is still a duplicate. Cost: a cheque and a deposit
   slip of the same number at the same bank for the same company collide. Rejected rows are still excluded.
2. **§4 normalisation is wider:** after Unicode NFKC (full-width forms), every space, punctuation mark and symbol is dropped
   (not only spaces and `-`), digits of any script count as 0-9 (Bengali ১২৩ = 123), letters are upper-cased, and any other
   character (a Cyrillic look-alike, a Bengali letter) is refused, so no look-alike makes a second key. The drawer bank is
   normalised the same way (2–60). The database CHECKs the normalised form.
3. **§4 columns added:** `rejected_by/at` and `bounced_by/at` (the design names only `verified_by/at`); a status-shape CHECK;
   `allocations` is a jsonb list `[{OrderId, AmountMinor}]` (≤ 50, CHECKed); the record-idempotency body hash is stored on
   the payment (`request_hash`, SHA-256 hex) with `ux (recorded_by, idempotency_key)` over all statuses; `xmin` tokens; foreign
   keys to `users` for `recorded_by`, `verified_by`, `requested_by`, `approved_by` (a verifier/approver must be a real user, so
   a made-up id cannot pass four-eyes). The amount CHECK is the hard ceiling 1,000,000,000,000 poisha (the highest
   `Credit:MaxPaymentMinor` the options accept); the configured limits are checked by the domain.
4. **§5 rejection is four-eyes too** (domain + CHECK `rejected_by <> recorded_by`), as the permission table says ("verify /
   reject a payment someone else recorded"). Adjustments: the requester MAY reject (withdraw) their own request, since a
   rejection moves no money (the design is silent).
5. **§4 dates:** a post-dated cheque is one whose `cheque_date` is after the Dhaka date of recording (`AcceptPostDatedCheques
   = false` refuses those); `cleared_on` must be ≥ `received_on` as well as ≥ `cheque_date` (a back-dated clearing is refused,
   CS11); `late_entry_reason` is required exactly when `received_on < today − 30` and refused otherwise.
6. **§4 wallets:** a bKash/Nagad reference that is a Bangladeshi mobile number (`01XXXXXXXXX`, `8801XXXXXXXXX`) is refused
   (CS31, domain half). **Cash:** receipt `CR{yyMMdd of received_on}{first 6 hex of the payment id}`, stored without dashes;
   not in a unique index (the design lists none for cash).
7. **New guard trigger** (`CreditLedgerSql.InstallSettlement`, like `price_lists`): rows are inserted only in their first
   status; an update may write only the columns of the next step (recorded → verified | rejected, verified → bounced;
   requested → approved | rejected); DELETE and TRUNCATE are refused. So even a **recorded** payment cannot be edited: a
   mistake is rejected and recorded again.
8. **§6 planner:** named orders get exactly their amounts and are left out of the FIFO pass ("the other open receivables");
   the last tie-break is the order id in PostgreSQL uuid order (`Guid.CompareTo` follows it); a charge with receivable 0 is
   not open. `plan.Postings(...)` builds the verification lines with keys `verify:{paymentId}:{orderId|on_account}`.
9. **§8.3 adjustments:** `amount_minor` is signed (negative only for a correction); a correction or payout names exactly ONE
   source (`payment_id` or `credit_note_id`), so a payout of money from several sources is one adjustment per source (B3.6
   may revisit); `Approve` must be given the source payment and refuses its recorder and verifier. The reason is 10–500
   characters as §8.3 says (the B3.3b brief said 5–500; the design was followed).

## Implementation notes: where B3.5 deviates from this design (2026-11-16, AI assistant, not reviewed)
Migration `DropCreditWithinLimitCheck`; domain `CreditReversals`; `ICreditLedger.ReduceChargeAsync` (`CreditLedgerService`); bounce API.
1. **§7 lock MODES (found by the CS16 test, a real 40P01):** every ledger line has foreign keys to the order and the payment it
   names, so its INSERT takes `FOR KEY SHARE` on those rows, which `FOR UPDATE` blocks. Two cycles followed: a bounce holding
   its payment `FOR UPDATE` waits for the company while a reduction holding the company writes an on-account line naming that
   payment; a cancel holding its order `FOR UPDATE` waits for the company while a verification holding the company writes a line
   naming that order. Fix: verify/reject/bounce lock the payment `FOR NO KEY UPDATE` (still one decision at a time), and the
   `ReduceChargeAsync` caller (E7.2, E6.5) must lock the order `FOR NO KEY UPDATE`. The ORDER of locks is unchanged.
   "Settlement never locks an order" stays true only in this sense: it takes `FOR KEY SHARE` through foreign keys.
2. **§1 the CHECK `credit_used <= credit_limit` is replaced** by triggers (`CreditLedgerSql.InstallLimitGuard`): a `charge`
   line may not take the running total above the company's limit, and the limit may not be LOWERED below the credit used
   (raising it while still below is allowed in the database; `Company.SetCredit` still refuses any limit below used). Only a
   `payment_reversed` line can take used above the limit. Down puts the CHECK back and fails while a company is above its limit.
3. **§8.1 bounce:** what a payment still pays on an order = its `payment_applied` and `on_account_applied` lines, less its
   `payment_reversed` lines and the `charge_reduced` on-account lines in its name; its on-account money = the sum of the
   on-account deltas naming it. So a payment whose order was cancelled meanwhile only loses its on-account money. A second bounce
   is 409 `CREDIT_PAYMENT_NOT_VERIFIED` (the status), the keys `bounce:{p}:{order|on_account}` are the backstop.
   `occurred_on` = the Dhaka date of the bounce; the lines carry the bounce reason. Audit + outbox `credit.payment_bounced`.
4. **§8.2 reduction:** keys `reduce:{SourceKey}:receivable` and `reduce:{SourceKey}:{paymentOrCreditNoteId}` (SourceKey 1–60
   printable ASCII); a cancellation may give an amount (partial cancel); a credit note must give its id and amount. The
   receivable line of a credit note carries `credit_note_id`; its on-account lines name the PAYING source only (so the source of
   on-account money is never ambiguous); the event id ties them. "Newest first" = the source whose latest paying line is newest.
   One audit row `credit.charge_reduced` (entity `orders`), no outbox event (E7.2/E6.5 emit their own). It calls
   `SaveChanges` inside the caller's transaction, so the caller's pending changes in that context are saved with it.
5. **Apply on-account (§6) is not in B3.5:** split into B3.5b (its idempotency needs its own design: Idempotency-Key + body
   hash without a table of its own).

## Implementation notes: E7.2 (2026-11-16, AI assistant, not reviewed)
1. **§7 global lock order, extended:** gateway `payments` (FOR UPDATE, id order) → `orders` (FOR NO KEY UPDATE on the cancel path) →
   `credit_payments` | `credit_adjustments` → `companies` → `promotions`. The payment webhook already locks payment → order, so the
   staff cancel (`OrderCancellationService`) takes the order's gateway payments before the order; the other way round deadlocks with a
   duplicate webhook. Credit orders have no gateway payments, so for them it is the §7 order unchanged.
2. **§8.2 as built:** `ReduceChargeAsync(OrderCancelled, AmountMinor null, SourceKey cancel:{orderId:N})` in the cancel transaction,
   then the restock after the commit (§8.2). The service never loads the company and clears its change tracker first. A captured gateway
   payment of a cancelled order becomes `needs_refund` (refund queue, four eyes); a credit order's paid part goes on account as §8.2 says.
