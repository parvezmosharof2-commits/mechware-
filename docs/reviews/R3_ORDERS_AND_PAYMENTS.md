# R3 security review: orders, cancellation and payments (Phase 8, in progress)

Method: `docs/04_REVIEW_PROMPTS.md` R3, as the attacker, against what exists today (cart rules, pricing, orders, credit,
cancellation, payments with the mock gateway). Each row has proof and a regression test. This is an interim review of the
slices built so far; the real gateway adapters and the frontend are not built, so this is **not** a Phase 8 sign-off.

| # | Attack | Result | Proof (test) |
|---|---|---|---|
| 1 | Price / total / discount / VAT tampering in the order request | Cannot influence the order | `AClientThatSendsItsOwnPricesAndTotals_StillPaysTheServersPrice`: unitPrice 1, total 1, 999,999,999 discount, vatRate 0 all ignored |
| 2 | Choose the amount to pay | Not possible: the body names only the provider | `PayingThroughTheMockGateway_...` sends `amountMinor: 1`, session is for the order's own total |
| 3 | IDOR: read another buyer's order | 404, same as unknown id | `SomeoneElsesOrder_LooksLikeItDoesNotExist` |
| 4 | IDOR: cancel or pay for another buyer's order | 404 | `CancelRefusals_...`, `OnlyThePendingOrdersOwnerCanStartAPayment_...` |
| 5 | Order in a company you do not belong to / lack the role for / that is unverified or suspended | 404 / 403 / 409 / 409 | `CompanyOrders_AreRefusedUnlessTheCallerIsABuyerOfAnActiveCompany` |
| 6 | Over-spend credit with parallel orders | Limit holds (row lock) | `TwelveParallelCreditOrders_NeverPassTheLimit_...` |
| 7 | Oversell with parallel checkouts | Exactly the stock sells | `TwentyBuyersForFiveUnits_ExactlyFiveOrders_...` |
| 8 | Replay a request / race one idempotency key | One order, one stock hold | `EightParallelRetriesOfOneKey_...`, `TheSameKeyAndRequest_ReplaysTheFirstOrder_...` |
| 9 | Reuse a key for a different request | 409 | `TheSameKeyWithADifferentRequest_IsRefused` |
| 10 | Forge a payment webhook (wrong secret, altered body) | 401, nothing recorded | `ForgedStaleAndGarbledWebhooks_...`, HTTP test |
| 11 | Replay a captured webhook with a new timestamp | Signature covers the timestamp: 401 | `ReplayingACapturedRequestWithAFreshTimestamp_IsRefused` |
| 12 | Replay a genuine webhook | Processed once | `TheSameEventDeliveredTwice_...`, `EightParallelDeliveriesOfOneEvent_...` |
| 13 | Confirm an order without paying (browser redirect, "paid" webhook while the provider says unpaid) | Never confirmed | `RedirectingBackWithoutAWebhook_...`, `AWebhookForAPaymentTheProviderDoesNotSayIsPaid_...` |
| 14 | Pay less and keep the goods | Not confirmed, flagged `NeedsRefund` | `ADifferentAmountPaid_IsNeverConfirmed_...` |
| 15 | Pay after cancelling to take the stock | Not confirmed, flagged `NeedsRefund`, stock untouched | `PayingAfterTheOrderWasCancelled_...` |
| 16 | Webhook DoS: very large body | **Found and fixed**: the body was read whole into memory by an anonymous endpoint. Now capped at 64 KB, 413 beyond it | `AnOversizedWebhookBody_...` (fails without the cap) |
| 17 | Learn which webhook check failed (signature oracle) | **Found and fixed**: the error text named the failed check. Now generic, reason only in the logs | same test (fails without the fix) |
| 18 | Malformed / oversized order bodies | Always a 4xx, never a 500 | `MalformedAndOversizedOrderBodies_GetCleanClientErrors_...` |
| 19 | Unauthenticated order access | 401 | `AnonymousCallers_AreRefused` |
| 20 | A payment gateway with a published secret in production | **Found and fixed**: the mock was registered by default everywhere. Now only in Development | `Program.cs` registration, reviewed in code |

## Open (not fixed, recorded)

- Real SSLCommerz / bKash / Stripe adapters are not built, so their specific quirks (IPN formats, amount rounding, bKash
  token grants) are untested.
- A process that dies between a committed credit order and its stock deduction leaves a window until the reservation
  expires; the reconciliation sweep that closes it does not exist yet.
- No rate limit specific to `POST /orders` beyond the global per-IP limit.
- COD (phone OTP, serviceable areas, order cap) is not built.
