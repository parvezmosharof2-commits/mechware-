# Threat model v2: Pricing, Quotes, Payments, Invoicing (মানুষের রিভিউর জন্য খসড়া)

> **অবস্থা:** AI সহকারীর লেখা খসড়া, ২০২৬-১১-০৫। কোনো মানুষ এখনো রিভিউ করেননি; এটা penetration test নয়।
> **কেন:** `docs/MASTER_PLAN.md` স্প্রিন্ট ১–২-এর Exit gate চায় "threat model v2: Pricing, Quotes, Payments, Invoicing"।
> v1 (`THREAT_MODEL_COMPANIES_APPROVALS.md`) Companies আর Approvals ঢেকেছে, আর সেটা লেখা হয়েছিল কোড বানানোর পরে।

**মডিউলভিত্তিক অবস্থা**

| মডিউল | কোডের অবস্থা | এই ডকে কী করা হয়েছে |
|---|---|---|
| Payments | আছে | ঝুঁকি যাচাই করা হয়েছে, প্রমাণসহ |
| Pricing ও Promotions | আছে | ঝুঁকি যাচাই করা হয়েছে, প্রমাণসহ |
| Quotes | এখনো বানানো হয়নি | কোড লেখার **আগেই** নিরাপত্তার নিয়ম (design rules) ঠিক করা হয়েছে |
| Invoicing | এখনো বানানো হয়নি | কোড লেখার **আগেই** নিরাপত্তার নিয়ম (design rules) ঠিক করা হয়েছে |

**এই ডকের বাইরে:** Companies ও Approvals (v1-এ আছে), Catalog, Inventory, Identity।

## প্রতিটা দাবি কীভাবে যাচাই হয়েছে

| চিহ্ন | মানে |
|---|---|
| **RUN** | সিস্টেম চালিয়ে পুনরুৎপাদন করা হয়েছে (নিচের "যাচাইয়ের রেকর্ড"-এ পদ্ধতি) |
| **TEST** | স্বয়ংক্রিয় টেস্ট প্রমাণ করে (টেস্টের নামসহ, repo-তে আছে) |
| **CODE** | শুধু সোর্স পড়ে বলা; কিছুই এটা চালায় না। এটাই সবচেয়ে দুর্বল প্রমাণ, রিভিউয়ার আগে এগুলো দেখবেন |
| **RULE** | এখনো কোড নেই; যে মডিউল বানাবে তার জন্য বাধ্যতামূলক নিয়ম, সাথে কোন টেস্ট লিখতে হবে |

**Severity:**
- **Critical:** বাইরের কেউ আজই টাকা বা অন্য কোম্পানির ডেটা নিতে পারে।
- **High:** বাস্তবসম্মত পথে টাকা হারায় বা ভুল হিসাব হয়। আসল গেটওয়ে চালুর আগে ঠিক করতেই হবে।
- **Medium:** insider বা অস্বাভাবিক পরিস্থিতি লাগে।
- **Low / Info:** কঠোরকরণ, বা ভবিষ্যতের কাজের নিয়ম।

---

## ১. Findings register (আগে এটা পড়ুন)

| ID | Severity | সারাংশ | প্রমাণ | কবে ঠিক হবে |
|---|---|---|---|---|
| P1 | **High** | গেটওয়ের সাময়িক ত্রুটিতে payment event চিরতরে হারায়; টাকা কাটা হয়েছে কিন্তু অর্ডার নিশ্চিত হয় না | RUN | আসল গেটওয়ের আগে (স্প্রিন্ট ১০–১২), আলাদা PR-এ এখনই প্রস্তাবিত |
| P2 | **High** | একই অর্ডারে দুবার টাকা দিলে দ্বিতীয়টা নীরবে থেকে যায়, refund-এর জন্য চিহ্নিত হয় না | RUN | একই |
| P3 | Medium | `NeedsRefund` পেমেন্টের জন্য কোনো কাজের তালিকা, সতর্কবার্তা বা refund পথ নেই | CODE | স্প্রিন্ট ১০–১২ / ১৫–১৬ |
| P4 | Medium | ব্যর্থ বা অসম্পূর্ণ webhook-এর কোনো reconciliation নেই (provider-এর তালিকার সাথে দৈনিক মেলানো) | CODE | স্প্রিন্ট ১০–১২ |
| P5 | Low | এক অর্ডারে যত খুশি payment session খোলা যায় (P2-এর পথ) | CODE | P2-এর সাথে |
| P6 | Low | webhook-ও global ১০০/মিনিট/IP সীমার মধ্যে; গেটওয়ের burst-এ 429 → গেটওয়ে retry করে | CODE | স্প্রিন্ট ১৭–১৮ |
| R1 | Medium | কুপন কোড আছে কিনা বোঝা যায়: অজানা কোডে এক উত্তর, আছে-কিন্তু-চলে-না কোডে আরেক | CODE | স্প্রিন্ট ১০–১২ |
| R2 | Info | promotion বানানো বা বদলানোর কোনো HTTP পথ নেই; শুধু seed বা database দিয়ে। ভবিষ্যতে admin API এলে নিয়ম লাগবে | CODE | স্প্রিন্ট ১৫–১৬ |
| R3 | Info (নিয়ম) | কোম্পানিভিত্তিক price list: 4-eyes, margin floor, audit | RULE | স্প্রিন্ট ৭–৯ |
| Q1–Q9 | নিয়ম | Quotes-এর নিরাপত্তার নিয়ম (§৪) | RULE | স্প্রিন্ট ৭–৯ |
| I1–I10 | নিয়ম | Invoicing / মূসক-এর নিয়ম (§৫) | RULE | স্প্রিন্ট ১০–১২ |

কোনো **Critical** পাওয়া যায়নি। আজ Production-এ কোনো গেটওয়ে নিবন্ধিত নেই: mock গেটওয়ে শুধু Development-এ চলে (`Program.cs`, R3 রিভিউ)। তাই P1 আর P2 আজ কারো টাকা নিচ্ছে না। কিন্তু SSLCommerz বা bKash লাগানোর দিন থেকেই এগুলো সত্যিকারের ক্ষতি করবে।

### P1. সাময়িক ত্রুটিতে payment event হারিয়ে যায় (High) [RUN]

`PaymentService.HandleWebhookAsync`-এর ধাপ:
1. Inbox row (`payment_events`, unique `(provider, event_id)`) **আলাদাভাবে save করে**।
2. তারপর `provider.VerifyAsync` ডাকে।
3. তারপর আলাদা transaction-এ `SettleAsync` চালায়।

২ বা ৩ নম্বর ধাপে exception হলে (গেটওয়ে timeout, DB deadlock) ক্রমটা এরকম দাঁড়ায়:
- API 5xx দেয়।
- গেটওয়ে একই event আবার পাঠায়।
- inbox-এ row আগেই আছে, তাই উত্তর আসে `Duplicate`।
- event আর কখনো প্রসেস হয় না।

**পুনরুৎপাদন:** একটা অস্থায়ী টেস্ট লেখা হয়েছিল, যেখানে প্রথম `VerifyAsync` exception ছোড়ে। ফল: `first=threw HttpRequestException retry=Duplicate orderStatus=PendingPayment`। ক্রেতা টাকা দিয়েছেন, অর্ডার নিশ্চিত হয়নি, আর একটু পরে unpaid-order-এর মেয়াদ শেষে স্টকও ছেড়ে দেওয়া হবে।

**ঠিক করার দিক:**
- inbox row-এর একটা অবস্থা থাকবে (`received` → `processed`)।
- duplicate মানে হবে শুধু "আগে **সফলভাবে** প্রসেস হয়েছে"।
- অসম্পূর্ণ row আবার চেষ্টা করা হবে। এর জন্য row lock বা `FOR UPDATE SKIP LOCKED` লাগবে, যাতে দুজন একসাথে না চালায়।
- বিকল্প পথ: inbox insert আর settle একই transaction-এ রাখা, আর `VerifyAsync` তার আগে ডাকা।

**লাগবে এমন টেস্ট:** `ATransientProviderFailure_IsRetried_NotSwallowedAsDuplicate`, `ADeadlockDuringSettle_IsRetried`।

### P2. দ্বিতীয় পেমেন্ট নীরবে থেকে যায় (High) [RUN]

এক অর্ডারে দুবার `POST /orders/{id}/payments` করা যায়: দুই ট্যাব, অথবা প্রথম পেজ বন্ধ করে আবার। তাতে দুটো session তৈরি হয়। দুটোতেই টাকা দেওয়া হলে:
- প্রথম webhook আসে `Confirmed`।
- দ্বিতীয় webhook আসে `AlreadyConfirmed`।
- দ্বিতীয় `payments` row **`Pending`-ই থেকে যায়**, যদিও গেটওয়ে বলছে টাকা এসেছে।

**পুনরুৎপাদন:** অস্থায়ী টেস্টের ফল `outcome1=Confirmed outcome2=AlreadyConfirmed payments=[Captured 100000; Pending 100000]`। কোনো staff তালিকা বা refund flag এটা ধরে না: টাকা দুবার কাটা হয়েছে, রেকর্ডে একবার।

**কারণ:** `SettleAsync` order-এর অবস্থা `Confirmed` দেখলে সরাসরি `AlreadyConfirmed` ফেরত দেয়। এই payment-ই যে অর্ডার নিশ্চিত করেছে কিনা, সেটা দেখে না (`PaymentService.cs`, শেষ `return`)।

**ঠিক করার দিক:**
1. অর্ডার অন্য payment দিয়ে নিশ্চিত হয়ে থাকলে এই payment `NeedsRefund` হবে।
2. একটা অর্ডারে একসাথে একটাই খোলা session (P5)। নতুন session চাইলে পুরোনোটা বাতিল বা পুনর্ব্যবহার।

**লাগবে এমন টেস্ট:** `ASecondPaidSessionForAConfirmedOrder_IsFlaggedForRefund`, `StartingTwiceReusesTheOpenSession`।

### P3. `NeedsRefund`-এর পরে কিছুই হয় না (Medium) [CODE]
- কম টাকা দেওয়া, বাতিল অর্ডারে টাকা দেওয়া, আর P2-এর ঠিক হওয়ার পর দ্বিতীয় পেমেন্ট — সবই `NeedsRefund` হবে।
- কিন্তু কোনো endpoint, কনসোল তালিকা, সতর্কবার্তা বা refund API নেই।

**নিয়ম:**
- কনসোলে "refund লাগবে" তালিকা থাকবে।
- refund শুরু করবে `payments.refund` permission-ধারী একজন, অনুমোদন দেবে **আরেকজন** (4-eyes)।
- refund-এর পরিমাণ মূল payment-এর বেশি হতে পারবে না।
- পুরো প্রক্রিয়া idempotent হবে, আর audit-এ লেখা থাকবে।

**ASVS:** V2.3.5 (L3, বড় অঙ্কে একাধিক জনের অনুমোদন)।

### P4. Reconciliation নেই (Medium) [CODE]
- webhook একেবারে না এলে কিছুই ধরা পড়ে না: গেটওয়ের সমস্যা, আমাদের downtime, বা P1।
- **নিয়ম:** worker প্রতিদিন গেটওয়ের settlement তালিকা (SSLCommerz transaction query API, bKash search) টেনে আনবে আর `payments`-এর সাথে মেলাবে।
- অমিল হলে সতর্কবার্তা যাবে। ক্রেতার "redirect হয়ে ফিরে আসা" কখনো নিজে থেকে অর্ডার নিশ্চিত করবে না; এটা আগে থেকেই আছে, TEST: `RedirectingBackWithoutAWebhook_NeverConfirmsAnything`।

### P5, P6 (Low)
- **P5:** session খোলার সংখ্যার কোনো সীমা নেই; P2-এর সাথেই ঠিক হবে।
- **P6:** `/payments/webhooks/*`-এর জন্য আলাদা rate-limit policy লাগবে: গেটওয়ের IP allowlist (যেখানে গেটওয়ে প্রকাশ করে), নয়তো উঁচু সীমা। ৪২৯ পেলে গেটওয়ে retry করে, তাতে confirm দেরিতে হয়, তবে টাকা হারায় না (P1 ঠিক হওয়ার পরে)।

### R1. কুপন কোড গণনা করা যায় (Medium) [CODE]
`OrderService.ApplyPromotionsAsync` দুই রকম উত্তর দেয়:
- অজানা কোডে `COUPON_INVALID` ("This code is not valid")।
- আছে-কিন্তু-চলে-না কোডে `COUPON_<reason>` (যেমন `COUPON_GLOBAL_LIMIT_REACHED`, মেয়াদ, ন্যূনতম কেনা)।

তাই উত্তর দেখেই বোঝা যায় কোনো কোড আসল কিনা।

**এখনকার বাধা:**
- verified অ্যাকাউন্ট লাগে (`ACCOUNT_NOT_VERIFIED`, বাগ #২, TEST: `StockHoardingTests`)।
- স্টক রাখার আগেই কুপন যাচাই হয়, তাই স্টক আটকানো যায় না [CODE]।
- global সীমা ১০০/মিনিট/IP।

**নিয়ম:**
- শব্দের মতো সহজ কোড নয়, ≥ ৮ অক্ষরের এলোমেলো কোড।
- কুপনের ভুল চেষ্টায় প্রতি ব্যবহারকারীর আলাদা সীমা (যেমন ১০/ঘণ্টা)।
- যে কোডটা মানুষকে দেওয়াই হয়নি তার জন্য বার্তা এক রকম হবে।
- যে কোডটা ব্যবহারকারী জানেন (মেয়াদ শেষ ইত্যাদি), তার জন্য স্পষ্ট বার্তা চলতে পারে।

**ASVS:** V2.4.1 (L2, anti-automation)।

### R2, R3 (Info / নিয়ম)
- **R2:** promotion বদলানোর API এলে লাগবে:
  - `promotions.manage` permission
  - audit-এ আগের ও পরের মান
  - শতাংশ ছাড় ≤ ১০০ আর নির্দিষ্ট ছাড় ≥ ০ (domain-এ আছে: `PromotionDefinition.Create`, TEST: `PromotionEngineTests.BadInput_IsRefused`)
  - একটা নির্দিষ্ট সীমার বেশি ছাড়ে দ্বিতীয় জনের অনুমোদন
- **R3 — কোম্পানির price list** (প্রোটোটাইপের স্ক্রিন ১২-এর নিয়ম, কোড লেখার আগেই):
  1. যিনি বানান তিনি অনুমোদন দিতে পারবেন না (server-এ চেক)।
  2. margin floor-এর নিচের লাইনে কারণ লেখা বাধ্যতামূলক, অনুমোদনকারীকে আলাদাভাবে নিশ্চিত করতে হবে।
  3. অনুমোদিত list immutable; বদলাতে নতুন version লাগবে, কার্যকর তারিখসহ।
  4. দাম resolution হবে শুধু সার্ভারে, অর্ডারের মুহূর্তের version-এর snapshot দিয়ে।
  5. খরচ ও margin শুধু `pricing.view_cost` permission দেখবে; ক্রেতা কখনো দেখবে না।
  6. প্রতিটা অনুমোদনের audit থাকবে।
  - **লাগবে এমন টেস্ট:** `TheAuthorCannotApproveTheirOwnPriceList`, `BelowFloorLinesNeedAReason`, `AnOrderKeepsThePriceListVersionItWasPlacedWith`।

---

## ২. Assets ও trust boundary

| Asset | কোথায় | কে পড়ে বা বদলায় |
|---|---|---|
| অর্ডারের মোট, লাইনের দাম, VAT, ছাড় | `orders`, `sub_orders`, `order_items` | শুধু সার্ভার হিসাব করে (`OrderPricing`); ক্রেতা পড়ে নিজেরটা |
| Payment ও তার অবস্থা | `payments`, `payment_events` | সার্ভার, গেটওয়ে webhook (স্বাক্ষরিত) |
| গেটওয়ের webhook secret ও store password | deployment config | শুধু API; repo-তে শুধু mock secret, যা Development-এ চলে |
| Promotion, কুপন কোড, ব্যবহার গণনা | `promotions`, `promotion_user_usage`, `promotion_redemptions` | আজ শুধু seed বা DB দিয়ে |
| (ভবিষ্যৎ) কোম্পানির price list, খরচ | নতুন টেবিল | pricing স্টাফ (বানান), sales manager (অনুমোদন দেন) |
| (ভবিষ্যৎ) কোটেশন ও তার version | নতুন টেবিল | স্টাফ বানান; কোম্পানির সদস্যরা পড়েন ও গ্রহণ করেন |
| (ভবিষ্যৎ) মূসক-৬.৩ ইনভয়েস, ক্রমিক নম্বর, credit note | নতুন টেবিল + PDF storage | সার্ভার ইস্যু করে; ক্রেতা ও হিসাব বিভাগ পড়েন |

**Trust boundary:**
1. **ব্রাউজার → API:** পরিমাণ, variant id আর কুপন কোড ছাড়া ক্রেতার কিছুই বিশ্বাস করা হয় না।
2. **গেটওয়ে → API (webhook):** শুধু HMAC স্বাক্ষর আর সময়ের window। টাকার অঙ্ক webhook থেকে **কখনো** নেওয়া হয় না; গেটওয়েকে সার্ভার থেকে আলাদা করে জিজ্ঞেস করা হয়।
3. **API → গেটওয়ে (verify):** বাইরের সার্ভিস, সেটা বন্ধ বা ধীর হতে পারে (P1)।
4. **স্টাফ কনসোল → API:** permission, আর বড় অঙ্কে 4-eyes।

---

## ৩. Payments ও Pricing — STRIDE (যা আছে)

| হুমকি | উদাহরণ | এখনকার নিয়ন্ত্রণ | প্রমাণ |
|---|---|---|---|
| **S**poofing | জাল webhook দিয়ে অর্ডার নিশ্চিত করানো | HMAC-SHA256, constant-time তুলনা, timestamp স্বাক্ষরের ভেতরে | TEST: `WebhookVerifierTests.ATamperedBody_IsRefused`, `AWrongSecret_IsRefused`, `ForgedStaleAndGarbledWebhooks_AreRejected_AndRecordNothing` |
| Spoofing | পুরোনো বৈধ webhook replay | ৫ মিনিটের window, ভবিষ্যতের timestamp প্রত্যাখ্যাত, event id dedupe | TEST: `ReplayingACapturedRequestWithAFreshTimestamp_IsRefused`, `ARequestFromTheFuture_IsRefused_ButSmallClockSkewIsTolerated`, `TheSameEventDeliveredTwice_IsProcessedOnce` |
| Spoofing | Production-এ mock গেটওয়ে (প্রকাশিত secret) | mock শুধু Development-এ নিবন্ধিত | CODE: `Program.cs`; R3 রিভিউ |
| **T**ampering | ক্রেতা দাম, VAT বা মোট পাঠায় | দাম ও VAT সার্ভার resolve করে (category-র `VatRate`), অতিরিক্ত field উপেক্ষিত | TEST: `OrderPricingTests.AClientCannotInfluenceTheTotals_OnlyTheServerResolvedInputsCount`, `OrdersApiTests` (tamper) |
| Tampering | কম টাকা দিয়ে "paid" | গেটওয়ের কাছ থেকে verify করা অঙ্ক ও মুদ্রা মেলানো; না মিললে `NeedsRefund` | TEST: `ADifferentAmountPaid_IsNeverConfirmed_AndIsFlaggedForRefund` |
| Tampering | ছাড় দিয়ে ঋণাত্মক মোট, পয়সার হিসাবে গরমিল | নির্দিষ্ট ছাড় basket-এর মূল্যের বেশি নয়; প্রতি লাইনে একবার banker's rounding; random basket সবসময় পয়সা পর্যন্ত মেলে | TEST: `FixedOff_NeverExceedsWhatTheBasketIsWorth`, `RandomBaskets_AlwaysBalanceToThePoisha`, `RandomOrders_AlwaysReconcileToThePoisha` |
| Tampering | একাধিক promotion একসাথে চাপানো | priority ক্রম, `Exclusive` উভয় দিকে বাধা দেয়, প্রতি অর্ডারে কুপনের সীমা | TEST: `Stacking_AppliesInPriorityOrder_EachOnWhatTheEarlierOnesLeft`, `Exclusive_BlocksOthers_InBothDirections` |
| Tampering | race দিয়ে কুপনের শেষ ব্যবহার দুজনে নেওয়া | atomic conditional `UPDATE` (global) + `ON CONFLICT … WHERE` (প্রতি ব্যবহারকারী), একই transaction-এ | CODE: `OrderService.SaveAsync`; TEST: `OrderPromotionsTests` (ASVS ম্যাট্রিক্স) |
| Tampering | বাতিলের সাথে payment-এর race | payment ও order দুটোতেই `FOR UPDATE` | TEST: `PayingAfterTheOrderWasCancelled_IsNotConfirmed_AndIsFlaggedForRefund`, `EightParallelDeliveriesOfOneEvent_ConfirmExactlyOnce` |
| **R**epudiation | কে কখন পেমেন্ট নিশ্চিত করল | order-এর status history-তে কারণ লেখা ("Payment captured via …"); inbox-এ outcome | CODE; tamper-evident নয় (v1 H3) |
| **I**nformation disclosure | webhook কোন কারণে প্রত্যাখ্যাত হলো | caller শুধু 401 পায়, কারণ শুধু লগে | CODE: `PaymentService.HandleWebhookAsync` |
| Information disclosure | কুপন আছে কিনা বোঝা যায় | — | **R1** |
| Information disclosure | অন্যের অর্ডারে payment শুরু করা | মালিক না হলে 404 | TEST: `OnlyThePendingOrdersOwnerCanStartAPayment_AndOnlyForAKnownProvider` |
| **D**enial of service | বিশাল webhook body | ৬৪ KB-এর বেশি পড়ে না, তাহলেই 413 | CODE: `PaymentsController.Webhook`; ASVS ম্যাট্রিক্স |
| Denial of service | গেটওয়ে timeout হলে event হারায় | — | **P1** |
| **E**levation of privilege | অজানা provider নাম দিয়ে অন্য secret ব্যবহার | provider allowlist; অজানা হলে 400 | TEST: একই owner টেস্ট |
| Elevation / টাকা | দুবার টাকা কাটা, দ্বিতীয়টা গায়েব | — | **P2** |

**আসল গেটওয়ে adapter-এর জন্য নিয়ম (SSLCommerz, bKash, Nagad; স্প্রিন্ট ১০–১২):**
1. **IPN বা callback-এর বডি বিশ্বাস নয়।** সবসময় সার্ভার থেকে validation API ডাকা হবে: SSLCommerz `validator/api/validationserverAPI.php` (`val_id` দিয়ে), bKash `query payment`। এখনকার `VerifyAsync` চুক্তি এটাই চায়।
2. **কয়েকটা জিনিস মেলাতে হবে:**
   - অঙ্ক (পয়সায়, `amount`-এর string → minor unit রূপান্তরের নিজস্ব টেস্টসহ)
   - মুদ্রা `BDT`
   - আমাদের `tran_id` = `payments.provider_ref`
   - `status` = `VALID` / `Completed`
   - **risk flag** (SSLCommerz `risk_level`) থাকলে স্বয়ংক্রিয় confirm নয়, স্টাফ রিভিউ।
3. **Store password, app key ও secret** থাকবে শুধু deployment secret-এ।
   - Production-এ sandbox URL বা sandbox key দিয়ে চালু হতে দেওয়া হবে না (fail closed)। `DemoAccountGuard`-এর মতো একটা startup check, সাথে টেস্ট।
4. **Secret rotation:** একসাথে দুটো secret গ্রহণযোগ্য (পুরোনো ও নতুন), একটা নির্দিষ্ট সময়সীমা পর্যন্ত।
5. **SSLCommerz-এর ক্ষেত্রে HMAC নেই, IPN আসে form post হিসেবে।** তাই "স্বাক্ষর" মানে validation API-র উত্তর।
   - `WebhookVerifier`-এর মতো একটা স্পষ্ট adapter-ভিত্তিক verdict রাখা হবে।
   - verify ছাড়া কোনো অবস্থা বদলাবে না।

---

## ৪. Quotes — নিয়ম (কোড লেখার আগে) [RULE]

| ID | নিয়ম | কেন | লাগবে এমন টেস্ট |
|---|---|---|---|
| Q1 | কোটেশনের দাম স্টাফ দেন। প্রতিটা version ইস্যু হওয়ার পর **immutable** (লাইন, দাম, শর্ত); বদলাতে নতুন version | ক্রেতা ও স্টাফের মধ্যে বিরোধ, আর audit | `AnIssuedVersionCannotBeEdited` |
| Q2 | কোটেশন "গ্রহণ → অর্ডার" হবে শুধু সেই version-এর **snapshot দামে**, সার্ভারে। ক্রেতা লাইন বা দাম পাঠাতে পারবেন না | price tamper | `AcceptingUsesTheSnapshotPrices_NotTheClients` |
| Q3 | মেয়াদ (`valid_until`) সার্ভারের ঘড়িতে চেক হবে। মেয়াদ শেষ হলে গ্রহণ করা যাবে না | পুরোনো সস্তা দাম চালানো | `AnExpiredQuoteCannotBeAccepted` |
| Q4 | গ্রহণ **একবারই** হবে (idempotent, unique constraint)। একই কোটেশন থেকে দুটো অর্ডার হবে না; পুরোনো version গ্রহণ করা যাবে না | দুবার কম দামে কেনা | `AcceptingTwiceMakesOneOrder`, `ASupersededVersionCannotBeAccepted` |
| Q5 | গ্রহণে কোম্পানির approval rule খাটবে; অর্ডার আর approval একই transaction-এ (v1 H1-এর নিয়ম) | approval এড়িয়ে যাওয়া | `AcceptanceWaitsForApproval` |
| Q6 | অন্য কোম্পানির কোটেশন দেখলে বা গ্রহণ করতে গেলে **404**; কোম্পানির ভেতরে permission না থাকলে 403 | IDOR | `Idor_QuotesOfAnotherCompany_Are404` |
| Q7 | PDF লিংক signed ও সময়সীমা-যুক্ত (≤ ১৫ মিনিট), সাথে প্রবেশাধিকার চেক; কখনো অনুমানযোগ্য public URL নয় | ফাঁস | `ThePdfLinkExpires_AndChecksAccess` |
| Q8 | স্টাফের ছাড়ের সীমা আছে (role অনুযায়ী %)। তার ওপরে বা margin floor-এর নিচে দ্বিতীয় জনের অনুমোদন (R3-এর মতো) | insider ছাড় | `ADiscountBeyondTheStaffLimitNeedsASecondApprover` |
| Q9 | কোটেশনের অনুরোধ (RFQ): verified অ্যাকাউন্ট, প্রতি কোম্পানি/ঘণ্টায় সীমা, ফাইল সংযুক্তি থাকলে V5 নিয়ম (আকার, টাইপ, স্ক্যান) | spam, ক্ষতিকর ফাইল | `RfqIsRateLimited`, upload টেস্ট |

---

## ৫. Invoicing ও মূসক — নিয়ম (কোড লেখার আগে) [RULE]

| ID | নিয়ম | কেন | লাগবে এমন টেস্ট |
|---|---|---|---|
| I1 | মূসক-৬.৩ চালান নম্বর **ক্রমিক, ফাঁকহীন, অপুনর্ব্যবহারযোগ্য**, অর্থবছরভিত্তিক (`MW/6.3/2026-27/000482`)। নম্বর দেওয়া হবে DB-তে row lock-করা counter দিয়ে, ইনভয়েস insert-এর **একই transaction-এ** (Postgres `sequence` ফাঁক রাখে, তাই নয়) | NBR অডিট | `ParallelIssuesGetConsecutiveNumbers_WithNoGaps`, `ARolledBackIssueLeavesNoGap` |
| I2 | ইস্যুর পর ইনভয়েস **immutable**। সংশোধন বা ফেরত শুধু credit note (মূসক-৬.৭) দিয়ে, মূল ইনভয়েসের রেফারেন্সসহ; মুছে ফেলা যায় না | আইন ও হিসাব | `AnIssuedInvoiceCannotBeChanged`, `ACreditNoteReferencesItsInvoice` |
| I3 | বিক্রেতা ও ক্রেতার BIN, নাম, ঠিকানা ইস্যুর মুহূর্তে **snapshot**। পরে প্রোফাইল বদলালেও পুরোনো ইনভয়েস বদলাবে না | ঐতিহাসিক সত্যতা | `ChangingTheBuyersBinDoesNotRewriteOldInvoices` |
| I4 | ইনভয়েসের মোট = অর্ডারের মোট, পয়সা পর্যন্ত। সংখ্যাগুলো `OrderPricing`-এর lines থেকে নেওয়া হবে, আবার হিসাব করা হবে না | গরমিল | `InvoiceTotalsEqualTheOrdersToThePoisha` (property test) |
| I5 | ইনভয়েস ইস্যু হবে কেবল নিশ্চিত অর্ডারে (paid বা credit), একবারই (unique `order_id`) | দ্বিগুণ ইনভয়েস | `AnOrderGetsExactlyOneInvoice` |
| I6 | ইনভয়েস বা PDF দেখা: মালিক বা কোম্পানির `invoices.view` সদস্য; অন্যদের **404**। PDF signed লিংকে (Q7-এর মতো) | IDOR, BIN ফাঁস | `Idor_InvoicesOfAnotherCompany_Are404` |
| I7 | VDS: ক্রেতার আপলোড করা মূসক-৬.৬ ফাইল পাবে আকারের সীমা, magic-byte টাইপ চেক (PDF / ছবি) আর স্ক্যান; সংরক্ষিত ফাইল চালানো যায় না (V5) | ক্ষতিকর ফাইল | upload টেস্ট (V5.2.1, V5.2.2) |
| I8 | VDS-এর অঙ্ক ইনভয়েসের VAT-এর বেশি হতে পারবে না; হিসাব বিভাগ মিলিয়ে নিশ্চিত করবেন | ভুল কর্তন | `VdsCannotExceedTheInvoiceVat` |
| I9 | ইনভয়েস ও credit note রাখা হবে NBR-এর নির্ধারিত মেয়াদ পর্যন্ত (মূসক আইন অনুযায়ী ন্যূনতম কয়েক বছর; সঠিক মেয়াদ হিসাবরক্ষক নিশ্চিত করবেন)। ব্যক্তিগত ডেটা মুছে ফেলার অনুরোধেও আইনি রেকর্ড থাকবে, শুধু অপ্রয়োজনীয় তথ্য মাস্ক হবে | আইন বনাম PDPO | `ErasureKeepsInvoices_MasksTheRest` |
| I10 | ইনভয়েস ইস্যু, বাতিল আর credit note audit-এ যাবে, **tamper-evident** (hash chain বা write-once anchor; v1 H3-এর সমাধানের সাথে একসাথে) | অডিটে প্রমাণ | `TheAuditChainDetectsAnEditedRow` |

---

## ৬. পরের কাজ (backlog)

| কাজ | স্প্রিন্ট | ASVS 5.0 |
|---|---|---|
| P1 + P2 + P5 ঠিক করা, টেস্ট ও mutation check সহ | **এখনই, আলাদা PR** (আসল গেটওয়ের আগে বাধ্যতামূলক) | V2.3.3 (L2), V15.4.2 (L3) |
| P3 refund তালিকা + 4-eyes refund | ১০–১২ / ১৫–১৬ | V2.3.5 (L3) |
| P4 দৈনিক reconciliation worker | ১০–১২ | — (সরাসরি requirement নেই; ব্যবসায়িক নিয়ন্ত্রণ) |
| P6 webhook-এর আলাদা rate-limit policy | ১৭–১৮ | V2.4.1 (L2) |
| R1 কুপন গণনা ঠেকানো | ১০–১২ | V2.4.1 (L2) |
| R3 + Q1–Q9 (price list, কোটেশন) | ৭–৯ | V2.2.2 (L1), V2.3.1 (L1), V2.3.5 (L3), V8.2.2 (L1) |
| I1–I10 (ইনভয়েস, মূসক) | ১০–১২ | V2.3.3 (L2), V8.2.2 (L1), V5.2.1 (L1), V5.2.2 (L1), V16.3.3 (L2) |
| আসল গেটওয়ে adapter-এর নিয়ম (§৩) | ১০–১২ | V4.1.5 (L3), V11.2.4 (L3), V13.3.1 (L2) |

---

## ৭. যাচাইয়ের রেকর্ড (২০২৬-১১-০৫)

**১. বিদ্যমান টেস্ট চালানো হয়েছে** (local PostgreSQL 16, Redis 7):
- `dotnet test tests/ShopVerse.IntegrationTests --filter "PaymentServiceTests|OrdersApiTests|ApiSecurityRegressionTests"` → **৫৪/৫৪ পাস**।
- `dotnet test tests/ShopVerse.UnitTests --filter "Payments|Pricing|Orders"` → **৯৯/৯৯ পাস**।

**২. P1 ও P2-এর পুনরুৎপাদন:** একটা অস্থায়ী integration টেস্ট-ফাইল লেখা হয়েছিল, যেটা `PaymentServiceTests`-এর মতো একই fixture, একই seed আর একই `MockPaymentProvider` ব্যবহার করে। চালানোর পর মুছে ফেলা হয়েছে, commit হয়নি। দুটো probe চালানো হয়েছিল:

| Probe | কী করা হয়েছিল | ফল |
|---|---|---|
| P1 | mock-কে মোড়ানো একটা provider, যার প্রথম `VerifyAsync` `HttpRequestException` ছোড়ে; একই webhook দুবার পাঠানো হয় | `first=threw HttpRequestException retry=Duplicate orderStatus=PendingPayment` |
| P2 | এক অর্ডারে দুটো `StartAsync`, দুটোতেই `SimulatePayment`, দুটো আলাদা event | `outcome1=Confirmed outcome2=AlreadyConfirmed payments=[Captured 100000; Pending 100000]` |

ফিক্সের PR-এ এই দুটো probe স্থায়ী টেস্ট হয়ে আসবে। ফিক্স ছাড়া তারা লাল হবে, তাতেই mutation check হয়ে যাবে।

**৩. TEST চিহ্নের সত্যতা:** এই ডকে নাম ধরে বলা প্রতিটা টেস্ট repo-তে আছে কিনা `grep` করে মেলানো হয়েছে। ব্যতিক্রম শুধু "লাগবে এমন টেস্ট" কলামের নামগুলো; সেগুলো এখনো লেখা হয়নি।

---

## সম্পর্কিত ডক
- v1: [`THREAT_MODEL_COMPANIES_APPROVALS.md`](THREAT_MODEL_COMPANIES_APPROVALS.md) (H1 approval, H3 audit — এখানে উল্লেখ আছে)
- ASVS ম্যাট্রিক্স: [`ASVS_MATRIX.md`](ASVS_MATRIX.md)
- আগের রিভিউ: `docs/reviews/R3_ORDERS_AND_PAYMENTS.md`
