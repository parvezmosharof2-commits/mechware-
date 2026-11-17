# Threat model v2: Pricing, Quotes, Payments, Invoicing (মানুষের রিভিউর জন্য খসড়া)

> **অবস্থা:** AI সহকারীর লেখা খসড়া, ২০২৬-১১-০৫। কোনো মানুষ এখনো রিভিউ করেননি; এটা penetration test নয়।
>
> **২০২৬-১১-০৬:** মালিক মানুষের রিভিউ মওকুফ করেছেন, তাই এটাকে কাজের ভিত্তি ধরা হচ্ছে। তবে কোনো মানুষ এটা যাচাই করেননি, এই কথাটা সত্যই থাকছে। বাইরের পেন-টেস্ট (স্প্রিন্ট ১৭–১৮) এখনো পরিকল্পনায় আছে।
> **কেন:** `docs/MASTER_PLAN.md` স্প্রিন্ট ১–২-এর Exit gate চায় "threat model v2: Pricing, Quotes, Payments, Invoicing"।
> v1 (`THREAT_MODEL_COMPANIES_APPROVALS.md`) Companies আর Approvals ঢেকেছে, আর সেটা লেখা হয়েছিল কোড বানানোর পরে।

**মডিউলভিত্তিক অবস্থা**

| মডিউল | কোডের অবস্থা | এই ডকে কী করা হয়েছে |
|---|---|---|
| Payments | আছে | ঝুঁকি যাচাই করা হয়েছে, প্রমাণসহ |
| Pricing ও Promotions | আছে | ঝুঁকি যাচাই করা হয়েছে, প্রমাণসহ |
| Quotes | এখনো বানানো হয়নি | কোড লেখার **আগেই** নিরাপত্তার নিয়ম (design rules) ঠিক করা হয়েছে; §১৩-এ অপব্যবহারের ঘটনা QT1–QT17 (D4.1) |
| Invoicing | এখনো বানানো হয়নি | কোড লেখার **আগেই** নিরাপত্তার নিয়ম (design rules) ঠিক করা হয়েছে |
| Cart (E1) | store ও guest token আছে (E1.2); endpoint ও merge এখনো নেই | §৯: ঝুঁকি যাচাই করা হয়েছে টেস্টসহ; endpoint আর merge-এর নিয়ম আগেই |

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
| P1 | **High** | গেটওয়ের সাময়িক ত্রুটিতে payment event চিরতরে হারায়; টাকা কাটা হয়েছে কিন্তু অর্ডার নিশ্চিত হয় না | RUN → TEST | ✅ **ঠিক হয়েছে** (২০২৬-১১-০৫) |
| P2 | **High** | একই অর্ডারে দুবার টাকা দিলে দ্বিতীয়টা নীরবে থেকে যায়, refund-এর জন্য চিহ্নিত হয় না | RUN → TEST | ✅ **ঠিক হয়েছে** (২০২৬-১১-০৫) |
| P3 | Medium | `NeedsRefund` পেমেন্টের জন্য কোনো কাজের তালিকা, সতর্কবার্তা বা refund পথ নেই | TEST | ✅ **ঠিক হয়েছে** (২০২৬-১১-০৫): refund তালিকা + 4-eyes; সতর্কবার্তা বাকি |
| P4 | Medium | ব্যর্থ বা অসম্পূর্ণ webhook-এর কোনো reconciliation নেই | TEST | ✅ **ঠিক হয়েছে** (২০২৬-১১-০৫): প্রতি মিনিটে গেটওয়েকে জিজ্ঞাসা; দৈনিক settlement মেলানো বাকি |
| P5 | Low | এক অর্ডারে যত খুশি payment session খোলা যায় (P2-এর পথ) | TEST | ✅ **ঠিক হয়েছে**: একসাথে সর্বোচ্চ ৩টা খোলা চেষ্টা |
| P6 | Low | webhook-ও global ১০০/মিনিট/IP সীমার মধ্যে; গেটওয়ের burst-এ 429 → গেটওয়ে retry করে | TEST | ✅ **ঠিক হয়েছে** (২০২৬-১১-০৫): webhook-এর আলাদা bucket |
| R1 | Medium | কুপন কোড আছে কিনা বোঝা যায়: অজানা কোডে এক উত্তর, আছে-কিন্তু-চলে-না কোডে আরেক | TEST | ✅ **ঠিক হয়েছে** (২০২৬-১১-০৫) |
| R2 | Info | promotion বানানো বা বদলানোর কোনো HTTP পথ নেই; শুধু seed বা database দিয়ে। ভবিষ্যতে admin API এলে নিয়ম লাগবে | CODE | স্প্রিন্ট ১৫–১৬ |
| R3 | Info (নিয়ম) | কোম্পানিভিত্তিক price list: 4-eyes, margin floor, audit | RULE | স্প্রিন্ট ৭–৯ |
| Q1–Q9 | নিয়ম | Quotes-এর নিরাপত্তার নিয়ম (§৪); অপব্যবহারের ঘটনা QT1–QT17 ও টেস্ট §১৩-এ (D4.1, ২০২৬-১১-১৭) | RULE | D4 ইউনিট (`docs/modules/D4.md`) |
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

**ফিক্স (২০২৬-১১-০৫):** `PaymentService.HandleWebhookAsync` এখন inbox row-কে "দাবি" হিসেবে দেখে:
- verify, settle বা স্টক commit-এর যেকোনো ধাপে exception হলে দাবি ফেরত দেওয়া হয় (`outcome = 'received'` row মুছে)। ফলে গেটওয়ের retry আবার প্রসেস হয়।
- প্রসেস নিজেই মারা গেলে (মুছে ফেলার সুযোগ না পেলে), row `PaymentService.StaleClaimAfter` (২ মিনিট) পরে পুরোনো ধরা হয়। পরের delivery conditional `UPDATE` দিয়ে সেটা দখল করে; একাধিক delivery একসাথে এলে ঠিক একজন জেতে।
- চলমান চেষ্টার (২ মিনিটের কম) row-কে duplicate ধরা হয়, তাই একসাথে দুজন প্রসেস করে না।
- স্টক commit idempotent, তাই `AlreadyConfirmed` হলেও ডাকা হয়। এতে settle আর commit-এর মাঝখানে crash হলেও কাজ শেষ হয়।

**প্রমাণ:**
- TEST: `ATransientProviderFailure_IsRetried_NotSwallowedAsDuplicate`, `AnAttemptThatDiedMidway_IsTakenOverOnceStale_ButNotWhileItMayStillBeRunning`।
- **Mutation check:** দাবি ফেরত বা stale দখল সরালে নিজ নিজ টেস্ট লাল হয়।
- পুরোনো `EightParallelDeliveriesOfOneEvent_ConfirmExactlyOnce` এখনো পাস করে।

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

**ফিক্স (২০২৬-১১-০৫):**
- অর্ডার অন্য payment দিয়ে নিশ্চিত হয়ে থাকলে এই payment এখন `NeedsRefund` হয় ("already been paid by another payment"), আর outcome হয় `NeedsRefund`।
- এক অর্ডারে একসাথে সর্বোচ্চ `MaxOpenAttemptsPerOrder` = ৩টা খোলা চেষ্টা (`TOO_MANY_PAYMENT_ATTEMPTS`)।
- একাধিক চেষ্টা রাখা হয়েছে ইচ্ছে করেই: ক্রেতা এক গেটওয়ে ছেড়ে আরেকটায় যেতে পারেন।

**প্রমাণ:**
- TEST: `ASecondPaidSessionForAConfirmedOrder_IsFlaggedForRefund`, `OneOrder_CannotCollectPaymentAttemptsWithoutBound`।
- **Mutation check:** পুরোনো `AlreadyConfirmed` ফিরিয়ে আনলে বা সীমা তুলে দিলে নিজ নিজ টেস্ট লাল হয়।
- ফেরতের কাজ নিজে এখনো P3।

### P3. `NeedsRefund`-এর পরে কিছুই হয় না (Medium) [CODE]
- কম টাকা দেওয়া, বাতিল অর্ডারে টাকা দেওয়া, আর P2-এর ঠিক হওয়ার পর দ্বিতীয় পেমেন্ট — সবই `NeedsRefund` হবে।
- কিন্তু কোনো endpoint, কনসোল তালিকা, সতর্কবার্তা বা refund API নেই।

**নিয়ম:**
- কনসোলে "refund লাগবে" তালিকা থাকবে।
- refund শুরু করবে `payments.refund` permission-ধারী একজন, অনুমোদন দেবে **আরেকজন** (4-eyes)।
- refund-এর পরিমাণ মূল payment-এর বেশি হতে পারবে না।
- পুরো প্রক্রিয়া idempotent হবে, আর audit-এ লেখা থাকবে।

**ASVS:** V2.3.5 (L3, বড় অঙ্কে একাধিক জনের অনুমোদন)।

**ফিক্স (২০২৬-১১-০৫):** নতুন `Refund` entity, `RefundService` আর `AdminRefundsController`।

**Endpoint ও permission:**

| Endpoint | কাজ | Permission |
|---|---|---|
| `GET /api/v1/admin/refunds` | তালিকা: `NeedsRefund` পেমেন্ট আর শেষ না হওয়া refund | `orders.refund` |
| `POST /api/v1/admin/payments/{id}/refunds` | refund চাওয়া (অঙ্ক + ৫–৫০০ অক্ষরের কারণ) | `orders.refund` |
| `POST /api/v1/admin/refunds/{id}/approve` | অনুমোদন | নতুন `payments.refund_approve` (finance, admin) |
| `POST /api/v1/admin/refunds/{id}/reject` | প্রত্যাখ্যান | নতুন `payments.refund_approve` (finance, admin) |

Support টাকা ফেরত চাইতে পারে, কিন্তু অনুমোদন দিতে পারে না।

**নিয়ম:**
- **4-eyes:** যিনি চেয়েছেন তিনি কখনো অনুমোদন দিতে পারবেন না (`REFUND_FOUR_EYES`); প্রথম চেষ্টা ব্যর্থ হওয়ার পরেও না।
- **অঙ্কের সীমা:** payment row-এ `FOR UPDATE` lock নিয়ে চেক হয়, প্রত্যাখ্যাত refund বাদে বাকি সবগুলো গুনে। একসাথে কয়েকজন চাইলেও মোট অঙ্ক payment ছাড়ায় না।
- **টাকা পাঠানো তিন ধাপে:**
  1. refund দাবি করা হয় (Processing), row lock নিয়ে।
  2. গেটওয়েকে refund পাঠানো হয়। **idempotency key = refund id**।
  3. ফল লেখা হয়।
- **গেটওয়ে ব্যর্থ হলে:** refund `Failed` হয়, আবার অনুমোদন দিয়ে retry করা যায়।
- **প্রসেস মাঝপথে মারা গেলে:** `Processing` অবস্থায় ৫ মিনিট পার হলে আবার অনুমোদন দেওয়া যায়।
- **কোনো অবস্থাতেই** একই refund দুবার টাকা পাঠায় না।
- **পুরো অঙ্ক ফেরত গেলে:** payment `Refunded` হয়। এরপর গেটওয়ের দেরিতে আসা event সেটাকে আর refund তালিকায় ফেরায় না (`AlreadyRefunded`)।
  - এই বাগটা code review-এ ধরা পড়েছিল, টেস্টসহ ঠিক করা হয়েছে।
- প্রতিটা পরিবর্তন audit interceptor-এ লেখা হয়, actor-সহ।
- `IPaymentProvider`-এ যোগ হয়েছে `RefundAsync(providerRef, amount, idempotencyKey)`। আসল adapter-কে গেটওয়ের refund reference ব্যবহার করতে হবে (SSLCommerz `refund_ref_id`)।

**প্রমাণ:**
- **TEST:**
  - `IT/Orders/RefundServiceTests.cs`: ৮টা টেস্ট — 4-eyes, অঙ্কের সীমা, সমান্তরাল চাওয়া, সমান্তরাল অনুমোদন, গেটওয়ে ব্যর্থতা, আটকে থাকা refund, ভুল ইনপুট, তালিকা ও দেরির event।
  - `IT/Api/RefundEndpointsTests.cs`: anonymous হলে 401, buyer হলে 403।
  - `UT/Payments/RefundRulesTests.cs`: কোন role-এর কী permission, endpoint-এর permission, অবস্থার নিয়ম।
- **Mutation check:** ৮টা mutant, সবগুলো ধরা পড়েছে। mutant-গুলো: 4-eyes বন্ধ, সীমা বন্ধ, payment lock বন্ধ, এলোমেলো idempotency key, `Refunded` না করা, Processing সবসময় অনুমোদনযোগ্য, প্রত্যাখ্যাত refund-কেও অঙ্কে গোনা, দেরির event-এর guard সরানো।

**বাকি:**
- নতুন refund-এর সতর্কবার্তা (ইমেইল বা কনসোল badge)।
- অনুমোদনে step-up auth: ASVS ম্যাট্রিক্সে "Step-up auth (refund, price list, role)" স্প্রিন্ট ১৫–১৬।
- কনসোলের UI।

### P4. Reconciliation নেই (Medium) [CODE]
- webhook একেবারে না এলে কিছুই ধরা পড়ে না: গেটওয়ের সমস্যা, আমাদের downtime, বা P1।
- **নিয়ম:** worker প্রতিদিন গেটওয়ের settlement তালিকা (SSLCommerz transaction query API, bKash search) টেনে আনবে আর `payments`-এর সাথে মেলাবে।
- অমিল হলে সতর্কবার্তা যাবে। ক্রেতার "redirect হয়ে ফিরে আসা" কখনো নিজে থেকে অর্ডার নিশ্চিত করবে না; এটা আগে থেকেই আছে, TEST: `RedirectingBackWithoutAWebhook_NeverConfirmsAnything`।

**ফিক্স (২০২৬-১১-০৫):** `PaymentReconciliationWorker` (API host-এ, যেখানে গেটওয়েগুলো নিবন্ধিত) প্রতি মিনিটে `PaymentService.ReconcileAsync` চালায়।
- **কোন payment দেখা হয়:** যেসব payment চেষ্টা ২ মিনিটের বেশি পুরোনো আর ২ ঘণ্টার কম, আর এখনো `Pending`, তাদের সব। অর্ডারের অবস্থা যা-ই হোক।
- **কী করা হয়:** প্রতিটার জন্য গেটওয়েকে সরাসরি জিজ্ঞেস করা হয় (`VerifyAsync`)। টাকা এসে থাকলে webhook-এর সেই একই locked, idempotent settlement চলে:
  - অর্ডার এখনো অপেক্ষায় থাকলে নিশ্চিত হয়।
  - অর্ডার আগেই অন্য payment-এ নিশ্চিত, বা বাতিল হয়ে থাকলে `NeedsRefund` হয়, অর্থাৎ refund-এর তালিকায় যায়।
- একটা গেটওয়ে বন্ধ থাকলেও বাকিগুলো চলে।
- **ফল:** webhook হারালেও টাকা দেওয়া অর্ডার ৩০ মিনিটের unpaid-expiry-তে আর বাতিল হয় না।

**প্রমাণ:**
- TEST: `APaidAttemptWhoseWebhookNeverCame_IsConfirmedByReconciliation`, `Reconciliation_LeavesYoungAndUnpaidAttemptsAlone_AndSurvivesAProviderThatIsDown`, `ReconciliationAndTheWebhookAtTheSameMoment_ConfirmOnce`, `ASecondPaidTabWhoseWebhookWasLost_IsFlaggedForRefund_EvenThoughTheOrderIsAlreadyConfirmed`।
- **Mutation check:**
  - ৬টা mutant-এর ৫টা ধরা পড়েছে: বয়সের সীমা, window, স্টক commit, exception-এ পুরো pass থেমে যাওয়া, অর্ডার-অবস্থার filter।
  - "paid flag উপেক্ষা" mutant-টা equivalent: settlement নিজেই না-পাওয়া টাকা প্রত্যাখ্যান করে।
- **Code review-এ ধরা পড়া ফাঁক:** প্রথম সংস্করণে শুধু অপেক্ষমাণ অর্ডারের payment দেখা হতো। তাতে দ্বিতীয় tab-এর হারানো webhook-এর টাকা কখনো refund তালিকায় পৌঁছাত না। ঠিক করা হয়েছে, টেস্টসহ।

**বাকি:**
- গেটওয়ের দৈনিক settlement report-এর সাথে মেলানো (আসল adapter এলে)।
- একাধিক API instance থাকলে একই payment কয়েকবার জিজ্ঞেস করা হয়। settlement নিরাপদ, শুধু বাড়তি API call হয়।

### P5, P6 (Low)
- **P5:** session খোলার সংখ্যার কোনো সীমা নেই; P2-এর সাথেই ঠিক হবে।
- **P6:** `/payments/webhooks/*`-এর জন্য আলাদা rate-limit policy লাগবে: গেটওয়ের IP allowlist (যেখানে গেটওয়ে প্রকাশ করে), নয়তো উঁচু সীমা। ৪২৯ পেলে গেটওয়ে retry করে, তাতে confirm দেরিতে হয়, তবে টাকা হারায় না (P1 ঠিক হওয়ার পরে)।

**P6 ফিক্স (২০২৬-১১-০৫):** `/api/v1/payments/webhooks/*`-এর নিজস্ব bucket আছে, প্রতি IP-তে মিনিটে ১০০০টা।
- সীমাটা কনফিগে বদলানো যায় (`RateLimiting:WebhookPermitPerMinute`)।
- browser-এর ১০০/মিনিটের বাজেট এই bucket-এর সাথে ভাগ হয় না।
- জাল webhook-এর বন্যা হলে শুধু নিজের bucket-টাই শেষ হয়, আর প্রতিটা জাল বার্তা স্বাক্ষর মেলাতে গিয়েই প্রত্যাখ্যাত হয়।

TEST: `ABurstOfWebhooks_IsNotThrottledByTheBrowserLimit_AndBrowsersAreNotStarvedByWebhooks`, `TheWebhookBucket_HasItsOwnLimit`। আলাদা bucket সরিয়ে দিলে প্রথম টেস্টটা লাল হয় (mutation-checked)।

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

**ফিক্স (২০২৬-১১-০৫):**
- অজানা, মেয়াদের বাইরে আর ব্যবহার-শেষ কোড এখন একই উত্তর পায় (`COUPON_INVALID`, একই শব্দে)।
- যে কোডগুলো এভাবে প্রত্যাখ্যাত হয়, সেগুলো **একসাথে** নাম ধরে বলা হয়। তাই একই অর্ডারে আসল আর বানানো কোড মেশালেও আসলটা আলাদা করে বোঝা যায় না।
- প্রতিটা প্রত্যাখ্যাত কোড আলাদা করে গোনা হয়। এক ঘণ্টায় ১০টার বেশি হলে সেই ব্যবহারকারীর সব কোড না পড়েই প্রত্যাখ্যাত হয় (`COUPON_ATTEMPTS_EXCEEDED`)।
  - হিসাব থাকে `coupon_failure_windows` টেবিলে, atomic upsert দিয়ে, তাই একাধিক সার্ভারেও সীমা ঠিক থাকে।
  - কোড ছাড়া অর্ডার কখনো আটকায় না।
  - অন্য ক্রেতারা প্রভাবিত হন না।

**জেনেশুনে রাখা হয়েছে:** `NO_ELIGIBLE_LINES`, `BELOW_MINIMUM_SPEND`, `NOT_STACKABLE` আর `PER_USER_LIMIT_REACHED` এখনো বলে দেয় যে কোডটা আছে। কারণ:
- কোডধারী এগুলো দেখে কাজ করতে পারেন (আরও কেনা, basket বদলানো)।
- ঘণ্টায় ১০টার সীমা এগুলোকেও সীমিত রাখে।

**প্রমাণ:**
- TEST:
  - `AnExpiredNotYetStartedOrUsedUpCode_LooksExactlyLikeOneThatNeverExisted`
  - `MixingARealCodeWithAGuess_DoesNotRevealTheRealOne_AndEveryGuessIsCounted`
  - `TooManyRefusedCodes_StopThatUserForAnHour_ButNoOneElse` (দুটো পরপর window-সহ)
  - `ParallelRefusals_AreAllCounted`
- **Mutation check:** ৬টা পরিবর্তন করে চালানো হয়েছে, প্রতিটাতেই অন্তত একটা টেস্ট লাল হয়েছে। পরিবর্তনগুলো:
  - মেয়াদ শেষ কোডের আলাদা উত্তর ফিরিয়ে আনা
  - সীমা-চেক সরানো
  - window আর কখনো নতুন না হওয়া
  - প্রত্যাখ্যান না গোনা
  - একাধিক কোডকে একটা হিসেবে গোনা
  - `Expired`-কে মৃত কোডের তালিকা থেকে বাদ দেওয়া

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
  - **২০২৬-১১-১৩:** বিস্তারিত অপব্যবহারের ঘটনা, প্রতিকার আর টেস্টের চূড়ান্ত নাম এখন §৮-এ (D2.1)।

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

> **২০২৬-১১-১৭ (D4.1):** এই নিয়মগুলোর নকশা [`docs/adr/0011-rfq-and-quotes.md`](../adr/0011-rfq-and-quotes.md)-এ, আর অপব্যবহারের ঘটনা ও টেস্ট §১৩-এ (QT1–QT17)। Q7 আরও কঠোরভাবে বাস্তবায়িত হবে: কোনো লিংক নেই, শুধু sign-in করা stream। Q4-কে স্পষ্ট করা হয়েছে: "এক version থেকে একটাই জীবিত অর্ডার"।

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
| ~~P1 + P2 + P5 ঠিক করা, টেস্ট ও mutation check সহ~~ ✅ ২০২৬-১১-০৫ | — | V2.3.3 (L2), V15.4.2 (L3) |
| ~~P3 refund তালিকা + 4-eyes refund~~ ✅ ২০২৬-১১-০৫ (UI, সতর্কবার্তা, step-up বাকি) | ১৫–১৬ | V2.3.5 (L3) |
| ~~P4 reconciliation worker~~ ✅ ২০২৬-১১-০৫ (দৈনিক settlement report বাকি) | ১০–১২ | — (সরাসরি requirement নেই; ব্যবসায়িক নিয়ন্ত্রণ) |
| ~~P6 webhook-এর আলাদা rate-limit policy~~ ✅ ২০২৬-১১-০৫ | — | V2.4.1 (L2) |
| ~~R1 কুপন গণনা ঠেকানো~~ ✅ ২০২৬-১১-০৫ | — | V2.4.1 (L2) |
| R3 + Q1–Q9 (price list, কোটেশন) | ৭–৯ | V2.2.2 (L1), V2.3.1 (L1), V2.3.5 (L3), V8.2.2 (L1) |
| I1–I10 (ইনভয়েস, মূসক) | ১০–১২ | V2.3.3 (L2), V8.2.2 (L1), V5.2.1 (L1), V5.2.2 (L1), V16.3.3 (L2) |
| আসল গেটওয়ে adapter-এর নিয়ম (§৩) | ১০–১২ | V4.1.5 (L3), V11.2.4 (L3), V13.3.1 (L2) |

---

## ৭. যাচাইয়ের রেকর্ড (২০২৬-১১-০৫)

**১. বিদ্যমান টেস্ট চালানো হয়েছে** (local PostgreSQL 16, Redis 7):
- `dotnet test tests/Mechware.IntegrationTests --filter "PaymentServiceTests|OrdersApiTests|ApiSecurityRegressionTests"` → **৫৪/৫৪ পাস**।
- `dotnet test tests/Mechware.UnitTests --filter "Payments|Pricing|Orders"` → **৯৯/৯৯ পাস**।

**২. P1 ও P2-এর পুনরুৎপাদন:** একটা অস্থায়ী integration টেস্ট-ফাইল লেখা হয়েছিল, যেটা `PaymentServiceTests`-এর মতো একই fixture, একই seed আর একই `MockPaymentProvider` ব্যবহার করে। চালানোর পর মুছে ফেলা হয়েছে, commit হয়নি। দুটো probe চালানো হয়েছিল:

| Probe | কী করা হয়েছিল | ফল |
|---|---|---|
| P1 | mock-কে মোড়ানো একটা provider, যার প্রথম `VerifyAsync` `HttpRequestException` ছোড়ে; একই webhook দুবার পাঠানো হয় | `first=threw HttpRequestException retry=Duplicate orderStatus=PendingPayment` |
| P2 | এক অর্ডারে দুটো `StartAsync`, দুটোতেই `SimulatePayment`, দুটো আলাদা event | `outcome1=Confirmed outcome2=AlreadyConfirmed payments=[Captured 100000; Pending 100000]` |

এই দুটো probe এখন স্থায়ী টেস্ট (`PaymentServiceTests`, শেষ ৪টা)। ফিক্সের পর পুরো backend suite পাস করে: unit ৩৬৫/৩৬৫, integration ২০৯/২০৯।

**৩. TEST চিহ্নের সত্যতা:** এই ডকে নাম ধরে বলা প্রতিটা টেস্ট repo-তে আছে কিনা `grep` করে মেলানো হয়েছে। ব্যতিক্রম শুধু "লাগবে এমন টেস্ট" কলামের নামগুলো; সেগুলো এখনো লেখা হয়নি।

---

## ৮. D2 — Price list ও contract price: অপব্যবহারের ঘটনা ও টেস্ট (২০২৬-১১-১৩) [RULE]

> **অবস্থা:** ডিজাইন ইউনিট D2.1-এর অংশ, AI সহকারীর লেখা। কোনো মানুষ রিভিউ করেননি, আর এখনো কোনো কোড নেই।
> নকশা: [`docs/adr/0008-price-lists-and-contract-prices.md`](../adr/0008-price-lists-and-contract-prices.md)। এই অংশ R3-এর ছয়টা নিয়মকে বিস্তারিত করে।
> নিচের টেস্টগুলো এখনো লেখা হয়নি। যে ইউনিট কোড বানাবে, এই নামগুলোই তার exit test (`docs/modules/D2.md`)।
> **২০২৬-১১-১৬:** D2.2–D2.6-এর টেস্ট লেখা হয়েছে ও পাস করে (D2.6-এর গার্ডগুলো D2.5-এর সাথেই বানানো হয়েছে)। D2.5 নতুন তিনটা ঘটনা যোগ করেছে: PL22–PL24। বাকি টেস্ট D2.7 থেকে।
> **২০২৬-১১-১৬ (D2.8):** PL1, PL2, PL3, PL10 ও PL19-এর D2.8 টেস্ট লেখা হয়েছে ও পাস করে (`backend/tests/Mechware.IntegrationTests/Pricing/PriceListApprovalEndpointsTests.cs`, আসল API, আসল ডেটাবেস, আসল role): `Approve_WithoutStepUpCode_IsRefused`, `Approve_WithoutPermission_Is403`, `Approve_WithAStaleContentHash_IsRefused`, `TwoParallelApprovals_ApproveExactlyOnce`, `Approve_BelowFloorWithoutExtraPermission_IsRefused`, `Approve_BelowFloorWithoutAcknowledgement_IsRefused`, `FloorIsRecheckedAtApproval_WithTheCostOfThatMoment`, `Withdraw_NeedsPermissionStepUpAndReason`, `EveryPriceListTransition_IsAudited_WithActorAndContentHash`; সাথে: একই code দ্বিতীয়বার চলে না, submitter বা editor approve করলে 409 (body-তে অন্য approver-এর id পাঠালেও), reject-এ step-up লাগে না (দাম চালু করে না), `pricing.view_cost` ছাড়া approver শুধু entry id দেখেন (অঙ্ক নয়, raw JSON পরীক্ষা), আর আসল order path-এ: শুরুর এক সেকেন্ড আগে public দাম, শুরু থেকে contract দাম, withdraw-এর পরের অর্ডারে আবার public দাম, আগের অর্ডার নিজের দাম রাখে।
> **Mutation (হাতে, সব ফিরিয়ে আনা হয়েছে):** (a) approve থেকে step-up সরানো → `Approve_WithoutStepUpCode_IsRefused` ও step-up তালিকার টেস্ট fail; (b) approver body থেকে নেওয়া → four-eyes টেস্ট fail; (c) স্বীকৃত id-র বদলে সার্ভারের নিজের তালিকা → `Approve_BelowFloorWithoutAcknowledgement_IsRefused` ও `FloorIsRecheckedAtApproval_…` fail; (d) পাঠানো hash-এর বদলে জমা রাখা hash → `Approve_WithAStaleContentHash_IsRefused` fail; (e) row lock ছাড়া approve → `TwoParallelApprovals_ApproveExactlyOnce` fail; (f) below-floor permission সবসময় true → `Approve_BelowFloorWithoutExtraPermission_IsRefused` fail। সবগুলো মারা গেছে।
> **২০২৬-১১-১৬ (D2.9):** PL11-এর টেস্ট লেখা হয়েছে ও পাস করে (`backend/tests/Mechware.IntegrationTests/Pricing/MarginFloorEndpointsTests.cs`, আসল API, ডেটাবেস ও role): `ChangingAFloor_NeedsItsOwnPermissionAndStepUp_AndIsAudited` (Support ও Finance-এর PUT/DELETE 403, code ছাড়া, ভুল code ও একই code দ্বিতীয়বার Admin-এরও প্রত্যাখ্যাত, audit-এ আগের ও পরের markup), সাথে: floor পড়তে `pricing.view_cost` লাগে (Support 403), আকার ও সীমা (1000.01 ও 10.001 প্রত্যাখ্যাত, round করা হয় না), global row মোছা যায় না (409 `GLOBAL_FLOOR_REQUIRED`, শুধু মান বদলানো যায়), `effective` নীতির ক্রম মানে (brand+category → brand → category → global), একই key-তে ৮টা সমান্তরাল upsert = একটা row, আগের মান হারায় না। **Floor বদলানো কিছু approve করে না:** submit-এর পরে floor কমালে approval-এর মুহূর্তে নতুন হিসাব হয় (স্বীকৃতি লাগে না, list তখনও pending); বাড়ালে approval নতুন id-গুলোর স্বীকৃতি চায় (`LoweringAFloorAfterSubmit_ChangesTheApprovalTimeEvaluation_AndApprovesNothingByItself`, `RaisingAFloorAfterSubmit_MakesTheApprovalDemandTheAcknowledgement_WithTheNewIds`)।
> **Mutation (হাতে, সব ফিরিয়ে আনা হয়েছে):** (a) PUT থেকে `pricing.manage_floors` সরানো → exit test fail; (b) PUT থেকে step-up সরানো → exit test ও step-up তালিকার টেস্ট fail; (c) global row মোছা যায় → global টেস্ট fail; (d) audit-এ আগের মান বাদ → exit, global ও সমান্তরাল টেস্ট fail; (e) table lock সরানো → সমান্তরাল টেস্ট fail (unique index-এ 500)। সবগুলো মারা গেছে। বাকি ঝুঁকি: যে Admin floor বদলান তিনিই পরে list approve করতে পারেন (4-eyes list-এর জন্য, floor-এর জন্য নয়); global row মোছার বাধা শুধু সার্ভিসে, DB-তে নয় (মুছলে fail closed: সব entry floor-এর নিচে)।
> **২০২৬-১১-১৬ (D2.10):** PL6, PL9, PL12 ও PL17-এর D2.10 টেস্ট লেখা হয়েছে ও পাস করে (`backend/tests/Mechware.IntegrationTests/Pricing/CompanyPriceEndpointsTests.cs`, আসল API, ডেটাবেস, Redis ও Meilisearch, প্রতি টেস্টে নতুন host): `Idor_PricesOfAnotherCompany_Are404` (A-র buyer B-র দাম চাইলে আর অজানা company চাইলে একই 404 body, তাতে B-র কোনো দাম বা নাম নেই; anonymous 401), `CompanyPriceEndpoint_SendsPrivateNoStore` (200, 404 ও 400 — তিনটাতেই `private, no-store` ও `Vary: Authorization`), `PublicProductResponse_IsTheSameForGuestAndCompanyMember` (product, category listing ও search: guest আর contract-ওয়ালা সদস্যের উত্তর byte-এ এক, search-এ শুধু `meta.processingMs` বাদ), `StorefrontCache_HoldsNoCompanyPrices` (company endpoint আর সদস্যের storefront পড়ার পরে Redis-এর সব `sf:*` key ও value এবং Meilisearch-এর document-এ contract দামের কোনো রূপ নেই; product key-তে public দাম আছে, তাই scan সত্যিই পাতাটা দেখেছে), `BuyerPriceResponse_NeverContainsCostOrFloor` (approval-এ cost জমা থাকা list; উত্তরে cost/floor/margin/version/entry-id নামের field নেই, field-এর নাম একটা নির্দিষ্ট তালিকার ভেতরে, raw text-এ cost ও floor-এর অঙ্ক নেই), `PriceQuery_WithMoreThanTheMaximumVariants_IsRefused` (১০০ চলে, ১০১ → 400 `TOO_MANY_VARIANTS`)। সাথে: দেখানো দাম = একই cart-এর আসল অর্ডার (`QuoteShippingAsync` ও `PlaceAsync`: প্রতি লাইনের দাম, net, VAT, মোট, উৎস, contract নম্বর, public দাম), public দামের চেয়ে বেশি contract হেরে যায় (উৎস `list`), পরিমাণে tier বদলায়, break table শুধু নিজের কোম্পানির, withdraw বা মেয়াদ-শেষ list আর দেখায় না, অজানা / unpublished / inactive variant শুধু `{variantId, status}`, ভুল ধরনের `items` 400, দুই কোম্পানির সদস্য প্রতিটার নিজের দাম পান, আর role matrix (Buyer 200; Buyer role ছাড়া company admin, approver/requester, accept না করা invitation, যাচাই না হওয়া বা suspended কোম্পানির buyer, seller — সবাই একই 404)।
> **Mutation (হাতে, সব ফিরিয়ে আনা হয়েছে):** (a) সদস্যপদ ব্যবহারকারীর যেকোনো কোম্পানিতে যাচাই, কিন্তু দাম URL-এর কোম্পানির → `Idor_PricesOfAnotherCompany_Are404` fail; (b) cache header সরানো → `CompanyPriceEndpoint_SendsPrivateNoStore` fail; (c) DTO-তে `costMinor` → `BuyerPriceResponse_NeverContainsCostOrFloor` fail; (d) ১০১টা variant গ্রহণ → `PriceQuery_WithMoreThanTheMaximumVariants_IsRefused` fail; (e) Buyer role-এর চেক সরানো → role matrix টেস্ট fail। সবগুলো মারা গেছে। বাকি ঝুঁকি: দেখানো দাম promotion ও শিপিং ছাড়া (contract লাইনে promotion লাগে না; public লাইনে অর্ডারের সময় কুপন বা automatic promotion কম দাম দিতে পারে, বেশি নয়); দাম "এখনকার" — checkout-এর আগে list বদলালে §9-এর `PRICE_CHANGED` ধরবে।

**R3-এর তিনটা টেস্টের নাম এখন:**
- `TheAuthorCannotApproveTheirOwnPriceList` → `Approve_BySameUserWhoSubmitted_IsRefused` + `Approve_ByAUserWhoEditedTheDraft_IsRefused`
- `BelowFloorLinesNeedAReason` → `Submit_WithBelowFloorEntryWithoutReason_IsRefused`
- `AnOrderKeepsThePriceListVersionItWasPlacedWith` → নাম একই থাকছে

**নতুন asset:** অনুমোদিত price list (কোম্পানির গোপন দাম); খরচ (cost) ও margin floor; order line-এ দামের snapshot।

**নতুন trust boundary:**
1. **কোম্পানির সদস্য → দামের API:** `companyId` প্রতিবার `company_members` থেকে যাচাই হয় (B1-এর নিয়ম)। ক্রেতা কখনো দাম বা price-list id পাঠান না।
2. **স্টাফ → price-list API:** permission, তার ওপরে 4-eyes আর step-up।
3. **Public cache, CDN, search index:** এখানে শুধু public দাম থাকে (list, sale, সবার জন্য quantity break)। কোম্পানির দাম কখনো নয়।

| ID | অপব্যবহার | প্রতিকার | যে টেস্ট প্রমাণ করবে | ইউনিট |
|---|---|---|---|---|
| PL1 | **নিজের list নিজে অনুমোদন।** যিনি বানিয়েছেন, বদলেছেন বা জমা দিয়েছেন, তিনিই approve করেন | সার্ভারে চেক: approver ≠ creator, ≠ submitter, ≠ এই version-এর কোনো contributor। DB-তেও `CHECK (approved_by <> created_by AND approved_by <> submitted_by)` | `Approve_BySameUserWhoSubmitted_IsRefused`, `Approve_ByAUserWhoEditedTheDraft_IsRefused`, `ApprovedByEqualsSubmittedBy_FailsAtDbLevel` | D2.3, D2.4, D2.8 |
| PL2 | চুরি হওয়া session দিয়ে, বা permission ছাড়া approve / withdraw | `pricelists.approve` + `[RequireStepUp]` (নতুন authenticator code)। Withdraw-এ কারণ লেখা বাধ্যতামূলক | `Approve_WithoutStepUpCode_IsRefused`, `Approve_WithoutPermission_Is403`, `Withdraw_NeedsPermissionStepUpAndReason` | D2.8 |
| PL3 | অনুমোদনকারী যা দেখেছেন তার বদলে অন্য কিছু approve হয় (জমার পরে edit; একসাথে দুটো approve) | জমার পর list frozen। Approve-এ দেখানো `contentHash` ফেরত পাঠাতে হয়; না মিললে 409। Row `FOR UPDATE` | `EditingAPendingList_IsRefused`, `Approve_WithAStaleContentHash_IsRefused`, `TwoParallelApprovals_ApproveExactlyOnce` | D2.3, D2.8 |
| PL4 | **চালু list বদলানো।** সাথে: request-এ status বা approver বসানো (mass assignment); list-এর কোম্পানি পাল্টে দেওয়া | Approved list immutable; বদল মানে নতুন version। DB trigger `UPDATE` / `DELETE` ফেরায়। Create / edit request-এ status, approver, author নেই। Scope ও `companyId` তৈরির পরে বদলায় না | `EditingAnApprovedList_IsRefused`, `UpdatingAnEntryOfAnApprovedList_FailsAtDbLevel`, `CreateOrEdit_CannotSetStatusOrApprover`, `ScopeAndCompany_CannotChangeAfterCreation` | D2.3, D2.4, D2.7 |
| PL5 | **পেছনের তারিখের validity** দিয়ে আগের সময়ে কম দাম চালানো; অসীম মেয়াদ | `effectiveFromUtc = max(validFrom, approvedAt + delay)`। মেয়াদ সার্ভারের ঘড়িতে, `[from, to)`। `validTo` বাধ্যতামূলক, সর্বোচ্চ `MaxValidityDays`। দেওয়া অর্ডারের দাম আর কখনো নতুন করে হিসাব হয় না | `BackdatedValidFrom_NeverAppliesBeforeApproval`, `ValidityWindow_StartIsInclusive_EndIsExclusive`, `ValidityLongerThanTheMaximum_IsRefused`, `ApprovingAListWhoseWindowHasPassed_IsRefused` | D2.2, D2.3 |
| PL6 | **এক কোম্পানি অন্য কোম্পানির দাম দেখে বা পায় (IDOR)** | `companyId` প্রতিবার `company_members` থেকে যাচাই; অন্য কোম্পানি → 404। ক্রেতার কোনো endpoint price-list id নেয় না। Resolver শুধু যাচাই করা company id পায়। Guest ও ব্যক্তিগত অর্ডারে negotiated দাম নেই | `Idor_PricesOfAnotherCompany_Are404`, `MemberOfTwoCompanies_GetsOnlyTheOrderingCompanysPrices`, `GuestOrPersonalOrder_NeverGetsACompanyPrice`, `Resolver_WithoutACompany_NeverReturnsANegotiatedSource` | D2.2, D2.5, D2.10 |
| PL7 | **ক্রেতা দাম, price-list id বা মোট পাঠিয়ে কম দামে কেনে** | `PlaceOrderLine`-এ শুধু variant ও পরিমাণ; বাকি field উপেক্ষিত। `ExpectedGrandTotalMinor` শুধু অর্ডার থামাতে পারে, দাম বদলাতে পারে না | `ClientSentPriceOrPriceListId_IsIgnored`, `ClientExpectedTotal_CanOnlyRefuseAnOrder_NeverLowerItsPrice` | D2.5, D2.6 |
| PL8 | **শূন্য বা ঋণাত্মক দাম**, শূন্য বা ঋণাত্মক `minQty`, বেশি কিনলে বেশি দাম | Domain validation, সাথে DB `CHECK (unit_price_minor > 0)` ও `CHECK (min_qty >= 1)`। একই variant-এ `minQty` বাড়লে দাম বাড়তে পারে না | `Entry_WithZeroOrNegativePrice_IsRefused`, `Entry_WithZeroOrNegativeMinQty_IsRefused`, `NonPositivePrice_FailsAtDbLevel`, `Breaks_MustNotGetDearerAsQuantityGrows`, `OrderingMore_NeverRaisesTheUnitPrice` | D2.2, D2.3, D2.4 |
| PL9 | **Overflow**, বা বিশাল list / বিশাল প্রশ্ন দিয়ে সার্ভার আটকানো | `MaxUnitPriceMinor`, `MaxEntriesPerList`; দামের প্রশ্নে সর্বোচ্চ ১০০ variant; গুণ `checked` | `Entry_AboveTheMaximumUnitPrice_IsRefused`, `Resolver_NeverReturnsZeroNegativeOrOverflows` (property), `ListWithMoreThanTheMaximumEntries_IsRefused`, `PriceQuery_WithMoreThanTheMaximumVariants_IsRefused` | D2.2, D2.3, D2.10 |
| PL10 | **স্টাফ margin floor-এর নিচে দাম নামায়** | Floor = cost-এর ওপর ন্যূনতম markup, ওপরের দিকে round। নিচের entry-তে লাগে: কারণ, `pricelists.approve_below_floor`, আর approver-এর স্পষ্ট স্বীকৃতি (entry id-র তালিকা সার্ভারের তালিকার সাথে মিলতে হবে)। Approve-এর মুহূর্তে আবার হিসাব। Cost না থাকলে floor-এর নিচে ধরা হয়। Cost-এর নিচে হলে প্রত্যাখ্যাত | `Submit_WithBelowFloorEntryWithoutReason_IsRefused`, `Approve_BelowFloorWithoutExtraPermission_IsRefused`, `Approve_BelowFloorWithoutAcknowledgement_IsRefused`, `FloorIsRecheckedAtApproval_WithTheCostOfThatMoment`, `EntryForAVariantWithoutCost_IsTreatedAsBelowFloor`, `EntryBelowCost_IsRefused_UnlessConfigAllows`, `Floor_IsRoundedUp_NeverDown` | D2.3, D2.8 |
| PL11 | আগে floor কমিয়ে, তারপর list approve | Floor বদলাতে আলাদা permission (`pricing.manage_floors`) + step-up + audit (আগের ও পরের মান)। Global floor না থাকলে সব entry floor-এর নিচে (fail closed) | `ChangingAFloor_NeedsItsOwnPermissionAndStepUp_AndIsAudited`, `NoGlobalFloor_MeansEveryEntryIsBelowFloor` | D2.3, D2.9 |
| PL12 | ক্রেতা বা অনুমতিহীন স্টাফ cost / margin দেখে | Cost ও floor শুধু `pricing.view_cost`। ক্রেতার response-এ এমন field-ই নেই | `BuyerPriceResponse_NeverContainsCostOrFloor`, `StaffWithoutViewCost_SeesNoCostFields` | D2.7, D2.10 |
| PL13 | **Approval আর অর্ডারের race:** প্রায় একই মুহূর্তের দুই অর্ডার দুই দাম পায়, পরে ব্যাখ্যা করা যায় না | Activation delay: `effectiveFromUtc`-এর আগে পুরোনো দাম, তখন থেকে নতুন দাম; ততক্ষণে approval commit হয়ে গেছে। দাম নির্ভর করে শুধু `asOf` আর commit হওয়া ডেটার ওপর | `OrderPricedBeforeEffectiveFrom_PaysTheOldPrice_AtOrAfterIt_TheNewPrice`, `ResolvingAgainAtTheOrdersInstant_GivesTheSamePriceAndSource` | D2.2, D2.5 |
| PL14 | Withdrawal আর অর্ডারের race; withdraw করে পুরোনো সস্তা version ফিরিয়ে আনা (একজনের হাতে দাম কমানো) | অর্ডার সংরক্ষণের transaction-এ ব্যবহৃত list row আবার `FOR SHARE` পড়া হয়; withdraw নেয় `FOR UPDATE`। চালু হওয়ার পরে withdraw করা version পুরোনো version-গুলোকে ঢেকেই রাখে | `WithdrawRacingAnOrder_NoOrderCommittedAfterTheWithdrawalCarriesItsPrice`, `WithdrawingAVersion_NeverBringsBackAnOlderCheaperOne` | D2.2, D2.6 |
| PL15 | Cart আর checkout-এর মাঝে list-এর মেয়াদ শেষ: ক্রেতা না জেনে বেশি দাম দেন, বা মেয়াদ শেষ দামে কেনেন | অর্ডারের দাম এক মুহূর্তে, সার্ভারে। মেয়াদের বাইরের list কখনো ব্যবহার হয় না। মোট দেখানো অঙ্কের চেয়ে বেশি হলে `409 PRICE_CHANGED`, অর্ডার হয় না | `Resolver_NeverUsesAListOutsideItsWindowOrNotApproved`, `ListExpiredBetweenCartAndCheckout_OrderIsRefusedWithPriceChanged` | D2.2, D2.6 |
| PL16 | অর্ডারের পরে দাম বদলে যায় (list বদল, withdraw, মেয়াদ শেষ), ইনভয়েস (E6) মেলে না | Order line-এ দাম, উৎস, list id ও version-এর snapshot। এরপর ওই অর্ডারের জন্য আর কোনো list পড়া হয় না। Idempotent replay আগের অর্ডারই ফেরত দেয় | `AnOrderKeepsThePriceListVersionItWasPlacedWith`, `WithdrawingAList_DoesNotChangePlacedOrders`, `IdempotentReplay_ReturnsTheOriginalPrices` | D2.5 |
| PL17 | **Public cache-এ কোম্পানির দাম ঢুকে যায় (cache poisoning):** অন্য কেউ সেই দাম দেখে | কোম্পানির দাম আলাদা authenticated endpoint-এ, `Cache-Control: private, no-store`। Public endpoint, Redis `sf:*` আর search index-এ শুধু public দাম; guest আর সদস্য একই উত্তর পান। Web পাতা public ডেটা দিয়ে render হয়; কোম্পানির দাম আলাদা করে, ব্যবহারকারী ধরে আনা হয় | `CompanyPriceEndpoint_SendsPrivateNoStore`, `PublicProductResponse_IsTheSameForGuestAndCompanyMember`, `StorefrontCache_HoldsNoCompanyPrices`, E2E `ContractPrice_IsNotInTheCachedPageHtml` | D2.10, D2.13 |
| PL18 | Contract দামের ওপর কুপন চাপিয়ে floor-এর নিচে নামা; পয়সার গরমিল | Negotiated উৎসের (Contract, GroupList, Quote) লাইন `PromotionEngine`-এ যায় না। Rounding আগের মতোই, প্রতি লাইনে একবার | `Coupon_DoesNotApplyToContractPricedLines`, `RandomOrdersWithContractPrices_AlwaysReconcileToThePoisha` | D2.6 |
| PL19 | কে কোন দাম অনুমোদন করলেন তা অস্বীকার করা | প্রতিটা ধাপ audit-এ (actor, IP, `contentHash`)। Approval-এর মুহূর্তের cost ও floor entry-তে লেখা থাকে | `EveryPriceListTransition_IsAudited_WithActorAndContentHash` | D2.8 |
| PL20 | Resolver ভুল উৎস বেছে নেয় (বেশি বা কম দাম) | Pure function, property test। নিয়ম A (ADR-0008 §5.1, মালিকের সিদ্ধান্ত ১): সবচেয়ে কম বৈধ দাম; সমান হলে বেশি নির্দিষ্ট উৎস | `Resolver_AlwaysReturnsTheLowestValidCandidate`, `Resolver_TieGoesToTheMoreSpecificSource`, `Resolver_PicksTheBreakWithTheLargestMinQtyNotAboveTheQuantity`, `Resolver_IsDeterministic_InputOrderDoesNotMatter` | D2.2 |
| PL21 | (পরে, group list) একজন স্টাফ পরিচিত কোম্পানিকে সস্তা group-এ বসান | `Company.DefaultPriceListId` বদলাতে দ্বিতীয় অনুমোদনকারী লাগবে। আজ কোনো কোড এই column লেখে না [CODE] | `AssigningAGroupList_NeedsASecondApprover` | D2.14 ⏭ |
| PL22 | **ফ্রি শিপিং-এর সীমায় কোম্পানি guest-এর চেয়ে বেশি দেয়:** public দাম net-এ নামানোর rounding (বা contract দাম) কোম্পানির পণ্যের মোটকে সীমার এক-দুই পয়সা নিচে নামায়; guest ফ্রি শিপিং পান, কোম্পানি ফি দেয় (যেমন ৫,০০০.০০ টাকা, ১৫ % VAT: কোম্পানি ৪,৯৯৯.৯৯) | কোম্পানির অর্ডারে free-shipping basis = একই লাইন guest-এর জন্য যত ধরা হতো: পরিমাণ × guest-এর VAT-সহ দাম, লাইনের নিজের ছাড় (VAT-সহ) বাদ (`OrderPricing.FreeShippingBasisMinor`) [CODE] | `ACompanyAtTheFreeShippingThreshold_ShipsFreeLikeAGuest`, `RandomOrdersWithContractPrices_AlwaysReconcileToThePoisha` (property: basis সমান, কোম্পানির মোট ≤ guest-এর মোট) | D2.5 |
| PL23 | **Snapshot আর আসল দাম আলাদা:** order line-এ এমন উৎস বা list লেখা থাকে যার দাম ধরা হয়নি; অডিট ভুল ব্যাখ্যা দেয় | লাইনের দাম বা variant snapshot-এর সাথে না মিললে `OrderPricing` অর্ডার ফেরায়। DB CHECK `ck_order_items_price_snapshot`: list-উৎস হলেই list id + version + entry, contract হলেই শুধু contract নম্বর, উৎস থাকলে basis ও public দাম [CODE] | `ASnapshotThatDoesNotMatchItsLine_IsRefused`, `AnOrderKeepsThePriceListVersionItWasPlacedWith` | D2.5 |
| PL24 | **Adapter পুরোনো সস্তা version ফিরিয়ে আনে:** list SQL-এ সময় দিয়ে ছাঁকা হলে বা Withdrawn version বাদ পড়লে, শুরু হয়ে যাওয়া পরের version (withdrawn বা মেয়াদ শেষ) আর পুরোনোটাকে ঢাকে না | Adapter প্রতিটা series-এর সব Approved ও Withdrawn version এক query-তে পড়ে, SQL-এ সময় দিয়ে ছাঁকে না; সময় ধরে সিদ্ধান্ত নেয় শুধু resolver [CODE] | `AWithdrawnOrExpiredLaterVersion_NeverBringsBackTheOlderCheaperOne` | D2.5 |

**যে ঝুঁকি থেকে যায়:**
- দুজন স্টাফের যোগসাজশ, বা একজনের হাতে দুটো স্টাফ অ্যাকাউন্ট: 4-eyes এটা ঠেকায় না। নিয়ন্ত্রণ: স্টাফের বাধ্যতামূলক 2FA, আলাদা `users.manage`, audit, floor-এর নিচের জন্য আলাদা permission।
- Sale price আর promotion আজ margin floor-এর সাথে মেলানো হয় না। নিয়ম A-তে একটা ভুল sale price contract ক্রেতারাও পাবেন।
- ১ পয়সার catalogue দাম: net দামের ন্যূনতম ১ পয়সা নিয়মে কোম্পানি guest-এর চেয়ে কয়েক পয়সা বেশি দিতে পারে (বাস্তবে এমন দাম নেই; D2.5)।
- কুপন: কোম্পানির অর্ডারে ছাড় net দামের ওপর, guest-এর অর্ডারে VAT-সহ দামের ওপর (আগের মতোই, D2-এর বাইরে)। নির্দিষ্ট অঙ্কের কুপনে (যেমন ১০০ টাকা ছাড়) কোম্পানি তাই ছাড়ের VAT-ও বাঁচায়, কিন্তু তার free-shipping basis guest-এর চেয়ে ঠিক ততটা (যেমন ৫ টাকা) কম হয়; সীমার একদম কাছে কোম্পানি তখন শিপিং ফি দিতে পারে যেখানে guest দেন না। শতাংশের কুপনে পার্থক্য rounding-এর এক-দুই পয়সা।
- Audit log এখনো tamper-evident নয় (v1 H3)।
- Cost ফাঁকা থাকলে fail closed; কিন্তু cost ভুল থাকলে floor-ও ভুল হয়, আর সেটা ধরা পড়ে না।
- **আজকের কোডে পাওয়া (D2-এর আগে থেকেই আছে) [CODE + TEST]:** একই catalogue দাম guest-এর জন্য VAT-সহ আর কোম্পানির জন্য VAT ছাড়া ধরা হয়, তারপর কোম্পানির ওপর VAT যোগ হয় (`OrderService.cs`-এ `CompanyId` দেখে `TaxMode` ঠিক হয়; `OrderServiceTests`: ১,০০০.০০ দামে ১০৫,০০০ পয়সা)। ফলে একই পণ্যে কোম্পানি guest-এর চেয়ে VAT-এর হার পরিমাণ বেশি দেয়। এটা ঠিক না হলে "সবচেয়ে কম দাম" আর "আপনার contract দাম" অর্থহীন। ADR-0008 §2 ও §12, মালিকের সিদ্ধান্ত ২।

**মালিকের সিদ্ধান্ত লাগবে** (ADR-0008 §12): (১) দাম বাছাইয়ের নিয়ম, (২) catalogue দামে VAT-এর অর্থ, (৩) margin floor-এর মান, (৪) launch-এ group list লাগবে কিনা।

---

## ৯. E1 — কার্টের store ও guest-cart token: অপব্যবহারের ঘটনা ও টেস্ট (২০২৬-১১-১৩) [TEST / RULE]

> **অবস্থা:** ইউনিট E1.2-এর অংশ, AI সহকারীর লেখা; কোনো মানুষ রিভিউ করেননি। কোড: `backend/src/Mechware.Infrastructure/Cart/RedisCartStore.cs`, `backend/src/Mechware.Infrastructure/Cart/GuestCartTokenService.cs`।
> কার্টের endpoint (E1.3) তৈরি হয়েছে (`CartController`)। Login-এ merge (E1.4) তৈরি হয়েছে: `backend/src/Mechware.Application/Cart/CartMergeService.cs`, `backend/src/Mechware.Api/Services/GuestCartHandover.cs`, `RedisCartStore.SaveMergedAsync`; টেস্ট `backend/tests/Mechware.IntegrationTests/Cart/CartMergeTests.cs`।

**Asset:** কার্ট (কোন variant, কত; দাম শুধু "ক্রেতা শেষবার যা দেখেছিলেন", checkout সবসময় সার্ভারে নতুন করে দাম ধরে, PL7)। Guest-cart cookie (কোন কার্ট কার, তার প্রমাণ)। গোপন `GuestCart:TokenKey`।

**Trust boundary:** (১) Browser → কার্টের API: token শুধু cookie-তে। (২) API → Redis: Redis-এ যা আছে তা-ও বিশ্বাস করা হয় না, প্রতিবার পড়ার সময় আবার যাচাই হয়।

| ID | অপব্যবহার | প্রতিকার | যে টেস্ট প্রমাণ করে | ইউনিট |
|---|---|---|---|---|
| CT1 | **Guest id বানিয়ে বা অনুমান করে অন্যের কার্ট পড়া বা বদলানো** | Guest id সার্ভারের বানানো random v4 GUID (১২২ random bit), অনুমান করা অসম্ভব। Token-এ HMAC-SHA256 স্বাক্ষর (key আলাদা secret থেকে HKDF দিয়ে), তাই বানানো বা বদলানো id বাতিল। Token গোপনীয়তা দেয় না, অখণ্ডতা দেয়: payload যে কেউ পড়তে পারে, তাতে গোপন কিছু নেই। `CartOwner` কাঁচা string নেয় না | `ForgedSignature_IsRefused`, `AForgedGuestId_WithTheSignatureOfAnotherToken_IsRefused`, `FlippingAnyBitOfAnyByte_InPayloadOrSignature_IsRefused`, `ChangingAnyCharacter_IsRefused_IncludingSpellingsThatDecodeToTheSameBytes`, `ATokenSignedWithAnotherKey_IsRefused`, `ASignedTokenForTheEmptyGuid_IsRefused` | E1.2 |
| CT2 | মেয়াদ শেষের পরেও token চালানো; অসীম মেয়াদের token | মেয়াদ স্বাক্ষর করা payload-এর ভেতরে (৭ দিন, `ICartStore.GuestTtl`)। এর চেয়ে বেশি মেয়াদ দাবি করা token বাতিল। Version byte যাচাই হয় | `AnExpiredToken_IsRefused_ExactlyAtItsExpiry`, `ATokenThatClaimsToLiveLongerThanAnyIssuedToken_IsRefused`, `ASignedTokenOfAnotherVersion_IsRefused` | E1.2 |
| CT3 | উল্টাপাল্টা input দিয়ে API ভাঙা, বা কেন বাতিল হলো তা জেনে নেওয়া (oracle) | আগে নির্দিষ্ট দৈর্ঘ্য (৭৮ অক্ষর) মেলে, তারপর decode। শুধু canonical base64url। কোনো field পড়ার আগে MAC যাচাই, constant-time-এ, সব key-এ (আগে থামে না)। যেকোনো ব্যর্থতায় একই `null`, exception কখনো নয় | `TruncatedOrExtendedTokens_AreRefused`, `HugeInput_IsRefusedWithoutDecoding`, `NonBase64UrlCharacters_OfTheRightLength_AreRefused`, `EmptyOrJunkInput_IsRefused`, `ANonCanonicalSpelling_OfAValidToken_IsRefused`, `RandomGarbage_NeverThrows_AndIsNeverAccepted`, `Validate_ComparesTheSignatureInConstantTime` | E1.2 |
| CT4 | Key ফাঁস, দুর্বল key, বা প্রকাশিত dev key production-এ | `GuestCart:TokenKey` নিজস্ব secret, কমপক্ষে ৩২ random byte। Development/Testing ছাড়া key না থাকলে, ছোট হলে, বা প্রকাশিত dev key হলে (previous key-এর মধ্যে থাকলেও) API চালুই হয় না। Staging compose key ছাড়া চালু হয় না, আর key শুধু `api` container পায়। Rotation: নতুন key সই করে, পুরোনোটা (`PreviousTokenKeys`) ৭ দিন শুধু যাচাই করে | `OutsideDevelopment_AWeakOrMissingKey_RefusesStartup`, `Production_RefusesToStart_WithoutARealGuestCartKey`, `Production_Starts_WithARealGuestCartKey_TheControl`, `KeyRotation_OldTokensStillValidate_NewTokensUseTheNewKey` | E1.2, H3b (secret store) |
| CT5 | **কার্ট overwrite race:** দুই tab বা দুই API instance একসাথে লেখে, একজনের বদল নীরবে হারায় | Redis-এ একটা Lua script-এ compare-and-set: ETag (১২৮ random bit) মিললে তবেই লেখা। না মিললে কিছুই লেখা হয় না (TTL-ও না), caller আবার পড়ে আবার চেষ্টা করে। `null` মানে "কার্ট থাকতেই পারবে না" (SET NX)। Random ETag বলে ABA নেই: কার্ট বদলে আগের অবস্থায় ফিরলেও পুরোনো ETag conflict দেয় | `FiftyConcurrentWriters_WithTheSameETag_ExactlyOneSaves`, `ConcurrentFirstSaves_ExactlyOneCreatesTheCart`, `Save_WithAStaleETag_Conflicts_AndWritesNothing`, `AConflict_DoesNotRefreshTheTtl`, `TheETagIsRandom_ACartThatChangesBack_StillConflictsForAnOldReader`, `Save_WithAMalformedExpectedETag_Conflicts_AndWritesNothing` | E1.2 |
| CT6 | **Cart stuffing:** বিশাল কার্ট দিয়ে মেমরি খাওয়া বা পরে checkout ভারী করা | সীমা save-এ ও load-এ দুবারই: সর্বোচ্চ ১০০ লাইন, প্রতি লাইনে ১ থেকে ১০,০০০, দাম ঋণাত্মক নয়, একই variant দুবার নয় (`OrderLimits`)। JSON সর্বোচ্চ ৩২ KB, আর Redis থেকে আনার আগেই আকার মাপা হয় | `Save_OverTheLimits_IsRefused_AndWritesNothing`, `StoreLimits_RefuseWhatTheEngineNeverProduces`, `StoreLimits_AcceptAFullCart_AtTheLimits`, `InvalidUtf8_AnOversizedPayload_AWrongKeyType_AMissingOrMalformedETag_AllReadAsAbsent` | E1.2 |
| CT7 | Redis-এ নষ্ট বা অচেনা ডেটা (bug, হাতে লেখা, পুরোনো format) বিশ্বাস করা | কঠোর JSON: অচেনা, duplicate বা না-থাকা field বাতিল; schema নম্বর; সীমা আবার যাচাই। পড়া না গেলে: log (কারণ ও আকার, বিষয়বস্তু নয়), এর মধ্যে কেউ না লিখে থাকলে মুছে ফেলা, আর "কার্ট নেই" উত্তর, যাতে ক্রেতা আটকে না যান। নতুন build-এর লেখা কার্ট (বড় schema নম্বর) রেখে দেওয়া হয় আর request 503 পায়: rolling deploy কার্ট মুছবে না | `AnUnreadableOrOverLimitCart_ReadsAsAbsent_IsDiscarded_AndANewCartCanBeSaved` (২৫ রকম payload), `AHandWrittenValidPayload_IsRead_SoTheUnreadableCasesFailForTheirOwnReason` (control), `ACartWrittenByANewerBuild_Is503_AndIsKept` | E1.2 |
| CT8 | **Redis বন্ধ বা flush** | বন্ধ বা timeout: 503 `CART_UNAVAILABLE`, খালি কার্ট কখনো নয় (খালি দেখালে ক্রেতা ভাবতেন কার্ট হারিয়েছে, নতুন করে ভরতেন)। Flush: কার্ট হারায়, অর্ডার বা টাকা নয় (দাম checkout-এ সার্ভারে); পুরোনো ETag তখন conflict দেয়, caller আবার পড়ে নতুন কার্ট পায়। Compose-এ AOF চালু | `WithoutRedis_EveryOperationIs503_NeverAnEmptyCart`, `Delete_IsIdempotent_AndFreesTheCart`, `AnExpiredCart_ReadsAsAbsent_AndCanBeCreatedAgain` | E1.2, H3b |
| CT9 | User আর guest-এর কার্ট মিশে যাওয়া; কার্ট enumeration | আলাদা key: `cart:u:{id}` আর `cart:g:{id}`। User id শুধু sign-in করা principal থেকে, guest id শুধু যাচাই করা token থেকে; অন্য কোনোভাবে `CartOwner` বানানো যায় না | `AUserAndAGuest_WithTheSameId_NeverShareACart`, `UserAndGuest_WithTheSameId_HaveDifferentStoreKeys`, `AnOwnerNotMadeByTheFactories_IsRefused` | E1.1, E1.2 |
| CT10 | **Login-এর পরে token replay;** cookie fixation দিয়ে অন্যের account-এ জিনিস ঢোকানো | Token stateless, তাই আলাদা করে বাতিল করা যায় না। (১) E1.4: session শুরু হলে (password, OTP, Google, 2FA enrolment, refresh) user কার্ট save আর guest কার্ট delete **একই Lua script-এ** (দুটোই, নয়তো কিছুই না); তারপর response-এ guest cookie মুছে দেওয়া হয় (একই attribute, মেয়াদ অতীতে)। Replay করা token তখন শুধু খালি কার্ট পায়, পুরোনো লাইন ফেরে না। (২) E1.3: token শুধু `__Host-` cookie থেকে (HttpOnly, Secure, SameSite=Lax), URL, body বা header থেকে কখনো নয়; তাই অন্য subdomain cookie বসাতে পারে না। (৩) Logout-এর response-ও guest cookie মোছে, তাই পরের guest কার্ট নতুন id পায়। (৪) Merge-এর notice (`quantityCapped`, `cartFull`) login-এর response-এ `cartMerged`-এ | `GuestToken_IsReadOnlyFromTheHostCookie` (E1.3, `CartApiTests`); `AfterMerge_TheGuestCartIsGone_AndReplayingItsTokenShowsAnEmptyCart`, `Logout_StartsANewGuestId`, `Refresh_AlsoTakesOverTheGuestCart` (E1.4, লেখা হয়েছে: `CartMergeTests`) | E1.3, E1.4 |
| CT11 | অসংখ্য guest কার্ট বানিয়ে Redis-এর মেমরি শেষ করা | [RULE] E1.3: guest id আর কার্ট শুধু প্রথম লেখায়, GET-এ নয়; নতুন guest কার্টের per-IP সীমা; TTL ৭ দিন; কার্ট ≤৩২ KB। H3b: Redis-এ `maxmemory` + alert আর `noeviction` (কার্ট নীরবে মুছবে না, ভরে গেলে 503), অথবা কার্টের জন্য আলাদা Redis | `ReadingWithoutAToken_CreatesNoCart`, `NewGuestCarts_ArePerIpLimited` (E1.3, লেখা হয়েছে: `CartApiTests`, `CartGuestLimitTests`; সীমা ঘণ্টায় প্রতি IP-তে ২০টি নতুন guest কার্ট, মালিক পর্যালোচনা করবেন) | E1.3, H3b |
| CT12 | **Merge দুবার হওয়া (পরিমাণ দ্বিগুণ), বা merge-এর সময় অন্য tab-এর যোগ করা জিনিস হারানো;** একই cookie দিয়ে একসাথে কয়েকটা login | `CartEngine.Merge` পরিমাণ যোগ করে, তাই একই guest কার্ট দুবার merge হলে দ্বিগুণ হতো। প্রতিকার: `ICartStore.SaveMergedAsync` একটা দুই-key Lua script: user কার্টের ETag মিললে **আর** guest কার্টে ঠিক সেই ETag থাকলে (যে version merge করা হলো) তবেই user কার্ট লেখা আর guest কার্ট মোছা, দুটো একসাথে; নইলে কিছুই লেখা হয় না, দুটো আবার পড়ে আবার চেষ্টা (৩ বার)। তাই "save হলো কিন্তু delete হলো না" অবস্থা হতেই পারে না; merge হওয়া guest কার্ট আর থাকে না, পরের login কিছু পায় না। অন্য tab-এর যোগ guest কার্টের ETag বদলায়, তাই commit conflict দেয় আর retry নতুন version merge করে। Commit-এর পরে আসা যোগ নতুন guest কার্টে যায় (পরের login/refresh-এ merge) | `AMergeWhoseResponseWasLost_IsNotAppliedAgain_OnTheNextLogin`, `ParallelLogins_WithTheSameGuestCookie_MergeItExactlyOnce`, `AGuestAddInAnotherTab_RightBeforeTheMergeCommits_IsMergedToo_NotLost`, `AGuestAddRacingTheLogin_EndsUpExactlyOnce_InTheUserCartOrTheGuestCart`, `ConcurrentMerges_OfOneGuestCart_ExactlyOneCommits`, `SaveMerged_WhenTheGuestCartChangedSinceItWasRead_WritesNothing`, `SaveMerged_WhenTheUserCartChanged_WritesNothing_AndKeepsTheGuestCart`, `Merge_IsNotIdempotent_SoTheStoreMustMergeAGuestCartAtMostOnce` (unit) | E1.4 |
| CT13 | **Merge-এর ত্রুটিতে login আটকে যাওয়া** (Redis বন্ধ = কেউ sign in করতে পারে না), বা ত্রুটিতে guest কার্ট হারানো; অন্যের guest id জেনে তার কার্ট নিজের account-এ টেনে নেওয়া | Merge-এর যেকোনো ত্রুটি log হয়, login সফল থাকে, guest cookie রাখা হয় আর guest কার্ট অপরিবর্তিত (পরের login বা refresh আবার চেষ্টা করে)। Guest id শুধু যাচাই করা token থেকে (`Validate`); বানানো, অন্য key-এর, মেয়াদ শেষ বা খালি GUID cookie চুপচাপ উপেক্ষা হয় আর মুছে দেওয়া হয়। User id শুধু সফল sign-in-এর ফল থেকে, request থেকে কখনো নয় | `LoginStillSucceeds_WhenTheMergeFails_AndTheGuestCartIsKept_ForTheNextLogin`, `LoginStillSucceeds_WhenRedisIsDown_AndKeepsTheGuestCookie`, `AForgedOrExpiredGuestCookie_AtLogin_IsIgnored_WithoutAnError`, `Login_WithoutAGuestCookie_ChangesNoCart_AndSetsNoCartCookie` | E1.4 |

**TTL-এর সিদ্ধান্ত:** guest ৭ দিন, user ৩০ দিন, প্রতিটা save-এ **আর** কার্ট পাওয়া প্রতিটা read-এ নতুন করে শুরু (read script-এর ভেতরেই, আলাদা round trip নেই)। কারণ: B2B ক্রেতা approval বা বাজেটের অপেক্ষায় কার্ট বারবার খোলেন কিন্তু বদলান না; শুধু save-এ বাড়ালে খোলা অবস্থাতেই ৮ বা ৩১ দিনে কার্ট হারাত। Token-ও প্রতিটা response-এ নতুন করে দেওয়া হবে (E1.3), তাই দুটো একসাথে চলে। Conflict কিছুই লেখে না, TTL-ও বাড়ায় না।

**Mutation check (২০২৬-১১-১৩):** হাতে করা; প্রতিটা বদল আলাদা করে, তারপর ফাইল হুবহু ফিরিয়ে আনা। ১৩টার মধ্যে ১১টা ধরা পড়েছে (বন্ধনীতে কয়টা টেস্ট ব্যর্থ হয়েছে):
স্বাক্ষর যাচাই বাদ (৭), CAS-এ expected ETag উপেক্ষা (৪), "থাকতে পারবে না" উপেক্ষা (৩), মেয়াদ যাচাই বাদ (১), load-এ সীমা আবার যাচাই বাদ (৮),
read-এ TTL বাড়ানো বাদ (১), Redis-এর ত্রুটি 503-এ না বদলানো (১), `FixedTimeEquals`-এর বদলে `SequenceEqual` (১, IL পরীক্ষা), version যাচাই বাদ (৩),
অপাঠ্য কার্ট না মোছা (২৬), সর্বোচ্চ মেয়াদ যাচাই বাদ (১)। দুটো বেঁচে গেছে, কারণ সেগুলো দ্বিতীয় স্তর: canonical re-encode (এখনকার .NET-এর Base64Url
নিজেই non-canonical লেখা বাতিল করে) আর নির্দিষ্ট দৈর্ঘ্যের check (লম্বা input decode-এর সময়ই আটকায়; check-টা শুধু অকারণ কাজ বাঁচায়)। দুটো একসাথে বাদ দিলে
ধরা পড়ে (`TruncatedOrExtendedTokens_AreRefused`, `HugeInput_IsRefusedWithoutDecoding`)।

**Mutation check, E1.4 (২০২৬-১১-১৩):** ৭টার ৭টাই ধরা পড়েছে (কোন টেস্ট): user CAS-এর আগে শর্তহীন guest delete (`AGuestAddInAnotherTab…`, `LoginStillSucceeds_WhenTheMergeFails…`),
commit-এ guest ETag যাচাই বাদ (৪, যেমন `ConcurrentMerges_OfOneGuestCart_ExactlyOneCommits`), commit-এ guest delete বাদ (১০, যেমন `AfterMerge_TheGuestCartIsGone…`, `AMergeWhoseResponseWasLost…`),
merge-এর পরে cookie না মোছা (৪), logout-এ cookie না মোছা (`Logout_StartsANewGuestId`), merge-এর ত্রুটিতে login ব্যর্থ (২, Redis বন্ধ ও store ত্রুটি), conflict-এ retry বাদ (১)।

**যে ঝুঁকি থেকে যায়:**
- Token চুরি (XSS, শেয়ার করা কম্পিউটার) মানে ৭ দিন পর্যন্ত ওই guest কার্ট দেখা বা বদলানো। Guest কার্টে ব্যক্তিগত তথ্য নেই আর দাম checkout-এ নতুন করে ধরা হয়, তাই ক্ষতি কম। প্রতিকার: HttpOnly cookie, CSP (v1 M5)।
- Redis-এ কার্ট এনক্রিপ্ট করা নয় (কী কেনা হচ্ছে, user id)। Redis-এ ঢুকতে পারা মানে সব ডেটা দেখা; staging-এ Redis শুধু ভেতরের network-এ। Production-এ AUTH/TLS: H3b।
- পড়া যায় না এমন কার্ট মুছে ফেলা হয়। সেটা ডেটা হারানো, কিন্তু সেই কার্ট এমনিতেই ব্যবহার করা যেত না; log-এ কারণ আর আকার থাকে।
- ~~`DeleteAsync` শর্তহীন: merge-এর সময় অন্য tab-এর যোগ হারাতে পারে~~ E1.4-এ সমাধান: merge আর শর্তহীন delete ব্যবহার করে না (CT12)। `DeleteAsync` শুধু ক্রেতার নিজের "কার্ট খালি করো"-তে।
- **Redis Cluster চলবে না (E1.4):** merge-এর script দুটো key ছোঁয় (`cart:u:…`, `cart:g:…`), এক hash slot-এ রাখা সম্ভব নয়। কার্টের Redis একটা node হতে হবে (একক বা আলাদা instance, H3b)। Cluster-এ Redis script-টা CROSSSLOT দিয়ে ফিরিয়ে দেয়: 503, login চলে, merge হয় না, guest কার্ট থেকে যায় (হারায় না, কিন্তু কখনো merge-ও হয় না)। বাকি সব কার্ট script এক-key।
- **Cookie fixation (CT10):** কেউ যদি ক্রেতার browser-এ নিজের guest cookie বসাতে পারে (`__Host-` থাকায় subdomain থেকে পারে না; XSS বা শেয়ার করা কম্পিউটার লাগে), login-এ তার কার্টের জিনিস ক্রেতার কার্টে ঢুকবে। ক্ষতি: অচেনা লাইন কার্টে (সর্বোচ্চ ১০০ লাইন, দাম checkout-এ সার্ভারের), টাকা নয়। `cartMerged`-এর notice আর কার্ট দেখে ক্রেতা বুঝবেন।
- Refresh-এও merge হয় (web প্রতিটা page load-এ refresh দিয়ে session ফেরায়); তাই একই browser-এ sign-in থাকা ক্রেতার session ফেরার আগে বানানো guest কার্ট account-এ যায়। শেয়ার করা browser-এ যিনি sign in করা, কার্ট তাঁর।

---

## ১০. E8.2 — অর্ডারের দামে shipping ও PO নম্বর: অপব্যবহারের ঘটনা ও টেস্ট (২০২৬-১১-১৩) [TEST / RULE]

> **অবস্থা:** ইউনিট E8.2-এর অংশ, AI সহকারীর লেখা; কোনো মানুষ রিভিউ করেননি। কোড: `backend/src/Mechware.Infrastructure/Shipping/OrderShippingQuoter.cs`, `backend/src/Mechware.Infrastructure/Orders/OrderService.cs` (`PriceAsync`, `DistrictIdAsync`, `QuoteShippingAsync`), `backend/src/Mechware.Domain/Orders/Order.cs` (`NormalisePurchaseOrderNumber`)। টেস্ট: `backend/tests/Mechware.IntegrationTests/Orders/OrderShippingTests.cs`, `backend/tests/Mechware.IntegrationTests/Orders/OrderShippingApiTests.cs`, `backend/tests/Mechware.UnitTests/Shipping/OrderShippingQuoterTests.cs`।

**Asset:** অর্ডারের shipping fee (প্রতি seller-এর sub-order-এ, grand total-এ), seller-এর payout-এর হিসাব, কোম্পানির PO নম্বর (invoice, e-mail, CSV-তে যাবে)। **Trust boundary:** browser → `POST /api/v1/orders` ও `POST /api/v1/orders/shipping-quote`; body-তে টাকার কোনো field নেই, অচেনা JSON member bind হয় না।

| ID | অপব্যবহার | প্রতিকার | যে টেস্ট প্রমাণ করে |
|---|---|---|---|
| SH1 | **Client নিজের shipping পাঠায়** (`shippingMinor: 0`, `freeShipping: true`, line-এ `weightKg`) | Shipping শুধু database থেকে: ঠিকানার district-এর zone, variant-এর `weight_kg`, product-এর মাপ। Body-তে টাকার field নেই; যা পাঠানো হয় তা পড়াই হয় না | `AClientThatSendsItsOwnShipping_StillPaysTheZonesFee`, `AClientThatSendsItsOwnPricesAndTotals_StillPaysTheServersPrice` |
| SH2 | Shipping grand total-এ দুবার (বা sub-order আর order-এ আলাদা) | Fee শুধু `SellerTerms`-এ যায়; `OrderPricing` প্রতিটা sub-order total-এ একবার যোগ করে, grand total = sub-order total-এর যোগফল। Preview আর placement একই pricing path | `Shipping_ComesFromTheZone_AndIsInTheGrandTotalExactlyOnce`, `TwoSellers_EachPayForTheirOwnParcel_AndTheOrderSumsThem`, `ThePreview_ChargesWhatPlacementCharges_AndHoldsNothing` |
| SH3 | **Zone না থাকলে fail-open:** production-এ shipping নীরবে ০ | `Shipping:AllowFreeWhenNoZones` শুধু Development/Testing-এ (ডিফল্ট সেখানে চালু, অন্য সব জায়গায় বন্ধ); বাইরে চালু করলে API চালুই হয় না। চালু থাকলেও শুধু যতক্ষণ একটাও active zone নেই; একটা zone হলেই প্রতিটা ঠিকানা হয় priced, নয় 422 `SHIPPING_NOT_AVAILABLE`। অচেনা বা দ্ব্যর্থক district = null = refused, অনুমান নয়। `OrderService`-এ quoter বাধ্যতামূলক dependency (লুকানো default নেই) | `Production_RefusesToStart_WithFreeShippingWithoutZonesSwitchedOn`, `TheContainersOrderService_FailsClosedInProduction_AndShipsFreeInDevelopment_WhenNoZoneExists`, `WithTheDevelopmentSwitch_ShippingIsZero_AndWithoutIt_TheOrderIsRefused`, `TheSwitch_DefaultsOnOnlyInDevelopmentOrTesting`, `OutsideDevelopment_TheSwitchCannotBeTurnedOn`, `OnceAZoneExists_TheSwitchNoLongerHelps_AnUnservedOrUnknownDestination`, `ADestinationNoZoneServes_IsRefused_AndHoldsNoStock` |
| SH4 | **Quote-only এড়িয়ে যাওয়া:** ভারী/বড় মাল সাধারণ band-এ বা বিনা খরচে | Heavy, oversize বা শেষ band-এর বেশি = পুরো অর্ডার 422 `SHIPPING_QUOTE_ONLY` (কারণ + SKU সহ), stock ধরা হয় না; free shipping-এর চেয়ে quote-only আগে। ক্রেতা RFQ (D4) ব্যবহার করবেন | `QuoteOnlyGoods_AreRefused_WithTheReasonAndSku_AndHoldNoStock`, `QuoteOnly_IsRefused_NamingTheSku_AndWinsOverFreeShipping`, `TheShippingQuoteEndpoint_PricesPerSeller_AndRefusesQuoteOnlyWith422` |
| SH5 | Weight নেই এমন listing দিয়ে বিনা খরচে পাঠানো, বা 500 | Weight ≤ 0 হলে 422 `VARIANT_WEIGHT_MISSING`, SKU-র নাম সহ, free shipping থাকলেও | `AVariantWithoutWeight_IsRefusedByItsSku_NeverPricedAsFree`, `AMissingWeight_IsRefusedWithTheSku_NeverFree` |
| SH6 | Free-shipping threshold কুপন দিয়ে ফাঁকি | Threshold-এর ভিত্তি discount-এর **পরের** পণ্যের মোট (VAT সহ, shipping ছাড়া) | `FreeShipping_StartsExactlyAtTheThreshold_OnTheGoodsTotal`, `FreeShipping_StartsAtTheThreshold_OfTheGoodsTotal` (সীমানা; কুপন-সহ threshold-এর আলাদা টেস্ট এখনো নেই, শুধু code-এ) |
| SH7 | **PO নম্বরে injection:** control character, CSV formula (`=…`), HTML, অদৃশ্য অক্ষর, বিশাল string | Trim, ১-৪০ অক্ষর, শুধু ASCII অক্ষর/সংখ্যা/space ও `- _ . # /`; বাকি সব 400, stock ধরার আগেই। শুধু কোম্পানির অর্ডারে। DB-তে `varchar(40)` | `APurchaseOrderNumber_WithAnythingElse_IsRefused`, `ABadPurchaseOrderNumber_IsRefused_BeforeAnyStockIsHeld`, `APurchaseOrderNumber_OnAPersonalOrder_IsRefused` |
| SH8 | একই `Idempotency-Key`-এ অন্য PO নম্বর দিয়ে আগের অর্ডার ফেরত পাওয়া | PO নম্বর idempotency hash-এর অংশ (থাকলে তবেই যোগ হয়, তাই পুরোনো key-ও replay হয়); অন্য বা না-থাকা PO = 409 `IDEMPOTENCY_KEY_REUSED` | `TheSameKey_WithAnotherPurchaseOrderNumber_IsRefused_AndWithTheSameOneReplays` |

**Mutation check (২০২৬-১১-১৩):** হাতে করা, প্রতিটা আলাদা করে, তারপর ফাইল হুবহু ফেরত। ৫টার ৫টাই ধরা পড়েছে: client-এর `shippingMinor` মেনে নেওয়া (`AClientThatSendsItsOwnShipping…`), grand total-এ shipping দুবার (৫টা টেস্ট), quote-only-কে ০ দামে চালানো (৩টা), quoter switch উপেক্ষা করে zone না থাকলে সবসময় free (৩টা, production container সহ), switch সব environment-এ ডিফল্ট চালু ও guard বাদ (৪টা)।

**যে ঝুঁকি থেকে যায়:**
- **VAT on shipping ঠিক হয়নি** (মালিক + কর উপদেষ্টা): fee এখন final amount, VAT ভাগ নেই। বদলাতে হলে `OrderPricing`-এ, এক জায়গায়।
- ক্রেতা নিজের ঠিকানার district নিজেই বেছে নেন (সস্তা zone-এর district লিখে অন্য জায়গায় নেওয়া)। Courier ঠিকানাতেই দেয়, তাই ক্ষতি সীমিত; district text সংরক্ষণের সময় তালিকার সাথে মেলানো হয়। Lat/long বা postal code যাচাই পরে।
- Zone প্রতিটা pricing call-এ database থেকে পড়া হয় (cache নেই); বেশি load-এ cache লাগতে পারে।

---

## ১১. E7.1 — Stock ফেরত (restock): অপব্যবহারের ঘটনা ও টেস্ট (২০২৬-১১-১৬) [TEST]

> **অবস্থা:** ইউনিট E7.1-এর অংশ, AI সহকারীর লেখা; কোনো মানুষ রিভিউ করেননি। Inventory এই ডকের মূল বিষয় নয়, কিন্তু confirmed অর্ডার বাতিল/ফেরত (E7.2, F2) টাকার সাথে জড়িত, তাই এখানে। কোড: `InventoryService.RestockAsync` (`backend/src/Mechware.Infrastructure/Inventory/InventoryService.cs`), `InventoryItem.Restock`, `StockMovement`, `StockMovementSql`। টেস্ট: `backend/tests/Mechware.IntegrationTests/Catalog/InventoryRestockTests.cs`, `backend/tests/Mechware.UnitTests/Catalog/InventoryItemTests.cs`। এখনো কোনো endpoint নেই (E7.2/G5)।

| ID | অপব্যবহার | প্রতিকার | যে টেস্ট প্রমাণ করে |
|---|---|---|---|
| RS1 | **দুবার restock** (retry, replay, একই বাতিল দুবার) | Idempotency key = (reference type, reference id, reason, variant); inventory row-lock-এর ভেতরে যাচাই; একই পরিমাণ = কিছু বদলায় না (`AlreadyApplied`), অন্য পরিমাণ = 409 `RESTOCK_CONFLICT`; DB-তে unique index `ux_stock_movements_reference` backstop | `Replay_OfTheSameReference_AddsStockOnce_AndAnotherReferenceAddsAgain` |
| RS2 | **অর্ডার যা নিয়েছিল তার বেশি ফেরত** (বা যে অর্ডার stock কাটেইনি তার নামে stock বানানো) | `order` reference-এ শুধু ওই অর্ডারের committed (fulfilled) hold যতটা নিয়েছিল, তার থেকে আগে ফেরত দেওয়াটা (যেকোনো reason) বাদ দিয়ে; বেশি = 409 `RESTOCK_EXCEEDS_DEDUCTED`; stock যে warehouse থেকে গিয়েছিল সেখানেই ফেরে | `CancelledOrder_GoesBackToTheWarehousesItCameFrom_AndNeverMoreThanWasTaken`, `AnOrderThatNeverDeductedStock_CannotRestock_ItsHoldIsReleasedInstead` |
| RS3 | **Negative / শূন্য / বিশাল পরিমাণ**, overflow | ১ থেকে ১০,০০০ (নইলে validation, কিছুই বদলায় না); domain-এ overflow guard; DB-তে `ck_inventory_available` ও `ck_stock_movements_delta` | `Quantity_OutsideOneToMax_IsRefused`, `Restock_OfNothingOrANegativeQuantity_IsRefused`, `Restock_ThatWouldOverflow_IsRefused` |
| RS4 | **কারণ বা reference ছাড়া restock** (হিসাবহীন stock) | Reason enum ১ থেকে শুরু (০ = অবৈধ), অচেনা মান refused; reference type ছোট হাতের অক্ষর/`_`, ১-৪০; reference id খালি নয়; `OrderCancelled` শুধু `order` reference-এ | `Restock_WithoutAReasonOrReference_IsRefused`, `StockMovement_RequiresAReasonAReferenceAndAQuantity` |
| RS5 | **Restock-এ hold কমে যাওয়া** (reserved স্পর্শ করা) বা race-এ negative stock | শুধু `on_hand` বাড়ে; reserve/commit/cancel-এর মতো একই `FOR UPDATE` lock, একই (variant, warehouse) ক্রমে | `Restock_RaisesOnHand_NeverTouchesReserved`, `ConcurrentRestockAndReserve_NeverNegative_AndTheBalanceAddsUp` (৫০ ক্রেতা + ২০ restock) |
| RS6 | **ইতিহাস মুছে ফেলা / বদলানো** | `stock_movements` append-only (UPDATE/DELETE/TRUNCATE trigger); প্রতিটা restock-এ `inventory.restocked` audit row (actor বা `system`) | `Restock_WritesAMovementAndAnAuditRow_AndTheLedgerIsAppendOnly` |

**Mutation check (২০২৬-১১-১৬):** হাতে করা, প্রতিটা আলাদা, তারপর ফাইল ফেরত। ৪টার ৪টাই ধরা পড়েছে: restock-এ reserved কমানো (৩টা টেস্ট), idempotency check বাদ (replay টেস্ট), restock-এ row lock বাদ (concurrency টেস্ট ৬ বারে ৬ বার fail, lost update), reindex request বাদ (search টেস্ট)।

**যে ঝুঁকি থেকে যায়:** staff-এর হাতে restock endpoint এখনো নেই (E7.2/G5 permission + step-up দেবে); `Correction`/`StockReceived` এক warehouse-এ যায় (কয়েকটা থাকলে বেছে নিতে হয়), পরিমাণ শুধু ১০,০০০ দিয়ে সীমিত; stock কমানোর (negative correction) পথ এখনো নেই।

---

## ১২. B3 — বাকিতে কেনার টাকা আদায় (credit settlement): অপব্যবহারের ঘটনা ও টেস্ট (২০২৬-১১-১৬) [RULE]

> **অবস্থা:** ডিজাইন ইউনিট B3.1-এর অংশ, AI সহকারীর লেখা। কোনো মানুষ রিভিউ করেননি, আর ২০২৬-১১-১৬ পর্যন্ত শুধু লেজার-কোর (B3.2, B3.3) কোড ও টেস্ট হয়েছে: append-only লেজার, commit-এ DB-এর মিল-পরীক্ষা, charge-এ idempotency; payment, adjustment, allocation ও বাকি সারির টেস্ট এখনো নেই (B3.3b থেকে)।
> নকশা: [`docs/adr/0010-credit-settlement.md`](../adr/0010-credit-settlement.md)। নিচের নামগুলোই প্রতিটা ইউনিটের exit test (`docs/modules/B3.md`)।
> **এখনকার অবস্থা (কোড পড়ে):** credit order confirm হলে (`OrderService.SaveAsync` বা `ApproveOrderAsync`, company row `FOR UPDATE`) `credit_used` বাড়ে; কোনো কিছু কখনো কমায় না (`ReleaseCredit`-কে শুধু unit test ডাকে)। গ্রাহকের পেমেন্টের কোনো রেকর্ড, due date, statement বা ইতিহাস নেই। কোম্পানির যেকোনো সদস্য (Requester সহ) `GET /companies/{id}`-এ limit ও used দেখেন।

**নতুন asset:** কোম্পানির credit ledger (প্রতিটা দেনা-পাওনার লাইন), গ্রাহকের পেমেন্ট (cheque, BEFTN, RTGS, নগদ, bKash/Nagad), on-account টাকা, write-off। **Trust boundary:** (১) finance staff → record/verify/bounce/adjust API (permission, four-eyes, step-up); (২) কোম্পানির সদস্য → credit summary/statement API (প্রতিবার `company_members` থেকে যাচাই); (৩) order placement → ledger (একই transaction, একই company row lock)।

| ID | অপব্যবহার | প্রতিকার | যে টেস্ট প্রমাণ করবে | ইউনিট |
|---|---|---|---|---|
| CS1 | **একই পেমেন্ট দুবার verify** (double click, retry, দুজন একসাথে): credit দুবার ছাড়া হয় | payment row `FOR UPDATE` + status `recorded` যাচাই; ledger-এর deterministic key (`verify:{paymentId}:{orderId}`) unique | `TwoParallelVerifications_ReleaseCreditOnce`, `VerifyingAVerifiedPayment_Is409_AndChangesNothing` | B3.4 |
| CS2 | **যিনি record করেছেন তিনিই verify করেন** (একজন staff নিজেই credit ছাড়েন) | সার্ভারে verifier ≠ recorder (409 `CREDIT_PAYMENT_FOUR_EYES`); DB-তে `CHECK (verified_by <> recorded_by)` | `Verify_ByTheRecorder_IsRefused`, `VerifiedByEqualsRecordedBy_FailsAtDbLevel` | B3.2, B3.3, B3.4 |
| CS3 | চুরি হওয়া session দিয়ে, বা permission ছাড়া record/verify | `credit.record_payment` / `credit.verify_payment`; verify-তে `[RequireStepUp("credit.payment.verify")]` (মালিকের সিদ্ধান্ত ৩); Support/Seller কিছুই পান না | `Verify_WithoutStepUpCode_IsRefused`, `Verify_WithoutPermission_Is403`, `RecordPayment_BySupportOrSeller_Is403` | B3.4 |
| CS4 | **একই bank reference দুবার** (একই বা অন্য কোম্পানির নামে), লেখা বদলে ("abc-123" / "ABC 123") | Reference normalise (বড় হাতের অক্ষর, শুধু অক্ষর/সংখ্যা); BEFTN/RTGS/NPSB/bKash/Nagad-এ (method, reference) সব জায়গায় unique; cheque/pay order/deposit-এ (company, method, bank, reference); বাতিল (rejected) পেমেন্ট reference আটকে রাখে না | `TheSameBankReference_CannotBeRecordedTwice_EvenForAnotherCompany`, `TheSameBankReference_DifferentlyWritten_IsADuplicate`, `TheSameChequeNumber_FromAnotherBank_IsAllowed`, `ARejectedPayment_FreesItsReference` | B3.3, B3.4 |
| CS5 | Record request replay করে দুটো পেমেন্ট বানানো | `Idempotency-Key` + body hash, unique (recorder, key); অন্য body = 409 `IDEMPOTENCY_KEY_REUSED` | `RecordPayment_ReplayWithTheSameKey_ReturnsTheSamePayment`, `RecordPayment_SameKeyOtherBody_Is409` | B3.4 |
| CS6 | **এক পেমেন্ট দুই অর্ডারে খরচ** (allocation-এর যোগফল > পেমেন্ট), বা round করে poisha বানানো | Pure planner: explicit → পুরোনো due আগে (FIFO) → বাকিটা on-account; শুধু integer, কোনো rounding নেই; প্রতি payment-এ applied + on-account = amount | `Allocations_NeverExceedThePayment`, `AllocationPlanner_Property_SumsMatchExactly_AndNoOrderGoesNegative` | B3.2, B3.4 |
| CS7 | **অন্য কোম্পানির অর্ডারে** নিজের টাকা জমা দেখানো (বা উল্টো) | Allocation শুধু পেমেন্টের কোম্পানির খোলা receivable-এ (company lock-এর ভেতরে পড়া); অন্য কোম্পানির ও অজানা অর্ডার একই 409 `ALLOCATION_ORDER_NOT_OPEN` | `Allocation_ToAnotherCompanysOrder_IsRefusedLikeAnUnknownOrder` | B3.4 |
| CS8 | অর্ডারের বাকির চেয়ে বেশি allocate করে receivable negative | Explicit allocation > বাকি = 409 `ALLOCATION_EXCEEDS_OUTSTANDING` (চুপচাপ কমানো হয় না); প্রতি অর্ডারে `0 ≤ receivable ≤ charge` | `Allocation_AboveTheOrdersOutstanding_IsRefused`, `NoOrderReceivable_EverGoesNegative` (property) | B3.2, B3.4 |
| CS9 | **বেশি টাকা দিলে** হারিয়ে যায়, বা available credit বাড়িয়ে দেয় | বাড়তি অংশ on-account (উৎস payment সহ); staff apply না করা পর্যন্ত available বাড়ে না | `AnOverPayment_GoesOnAccount_AndDoesNotRaiseAvailableCredit`, `ApplyOnAccount_UsesTheOldestSourceFirst` | B3.2, B3.4, B3.5 |
| CS10 | **অঙ্ক নিয়ে কারসাজি:** শূন্য, negative, দশমিক, string, বিশাল, অন্য currency | ১ থেকে `Credit:MaxPaymentMinor`; JSON integer ছাড়া 400; শুধু BDT; DB-তে `CHECK currency = 'BDT'`, delta ≠ 0 | `PaymentAmount_OutsideOneToMax_IsRefused`, `PaymentAmount_AsDecimalOrString_Is400`, `NonBdtCurrency_IsRefused` | B3.4 |
| CS11 | **তারিখ পিছিয়ে/এগিয়ে লেখা** (সময়মতো দেওয়া দেখানো, PDC আগেই ভাঙানো) | `received_on` ≤ আজ (ঢাকা) এবং ≥ আজ − ৩০ দিন (পুরোনো হলে কারণ লাগে, queue-তে চিহ্ন); post-dated cheque তার তারিখের আগে verify হয় না; `cleared_on` ≥ cheque-এর তারিখ ও ≤ আজ | `ReceivedOn_InTheFuture_IsRefused`, `ReceivedOn_OlderThan30Days_NeedsALateEntryReason`, `PostDatedCheque_CannotBeVerifiedBeforeItsDate`, `ClearedOn_BeforeTheChequeDate_IsRefused` | B3.2, B3.4 |
| CS12 | **Cheque bounce হলেও credit ছাড়া থেকে যায়**, বা bounce দুবার | Bounce (step-up + কারণ): টাকা যে যে অর্ডারে গিয়েছিল (on-account থেকে apply হওয়া সহ) সেখানে দেনা ফেরে, বাকি on-account কমে; key `bounce:{paymentId}:…`; আগের due date থাকে | `ABouncedCheque_PutsTheDebtBackOnTheSameOrders`, `ABounce_AfterOnAccountWasApplied_ReversesThatApplicationToo`, `Bounce_NeedsStepUpAndReason`, `BouncingTwice_IsAppliedOnce` | B3.5 |
| CS13 | **Ledger মুছে ফেলা / বদলানো** | `credit_ledger_entries` append-only (UPDATE/DELETE row trigger + TRUNCATE statement trigger) | `CreditLedger_IsAppendOnly_UpdateDeleteAndTruncateFail` | B3.3 |
| CS14 | **Cached `credit_used` ledger থেকে সরে যায়** (কোনো code path ledger ছাড়া used বদলায়, বা উল্টো) | Deferred constraint trigger: commit-এর সময় company row = শেষ ledger row-এর running total; প্রতিটা row-এ seq ও running total-এর chain যাচাই | `ChangingCreditUsed_WithoutALedgerEntry_FailsAtCommit`, `ALedgerEntry_WithoutUpdatingTheCompany_FailsAtCommit`, `LedgerChain_WithAGapOrAWrongRunningTotal_FailsAtCommit` | B3.3 |
| CS15 | **Race:** verify আর নতুন credit order একসাথে, limit পার হয় বা lost update | দুটোই company row `FOR UPDATE` নেয়; ledger key backstop। Mutation: company lock সরালে টেস্ট fail করতে হবে | `ParallelCreditOrdersAndVerifications_NeverExceedTheLimit_AndTheLedgerAddsUp` (৩০ অর্ডার + ১০ verify) | B3.4 |
| CS16 | Cancel, approval আর settlement একসাথে চললে deadlock | একটাই lock ক্রম: gateway payments → orders (`FOR NO KEY UPDATE`) → credit_payments/adjustments → companies → promotions; settlement কখনো `orders` row lock করে না | `CancelVerifyBounceAndPlace_InParallel_NeverDeadlock_AndTheLedgerAddsUp`, `ACancelHoldingTheOrder_NeverBlocksAVerificationThatWritesALineNamingIt`, `ACancel_AndAPaymentWebhookOfTheSameOrder_TakeThePaymentFirst_NeverDeadlock` | B3.5, E7.2 |
| CS17 | **Crash মাঝপথে:** ledger লেখা হলো কিন্তু payment status/cached total নয় | সব এক transaction-এ, ভেতরে কোনো বাইরের call নেই; audit row ও outbox event-ও সেই transaction-এ | `AFailureAfterTheLedgerInsert_RollsBackEverything` (interceptor দিয়ে ব্যর্থতা) | B3.4 |
| CS18 | Credit order-এর charge হয় না, বা approval sweep দুবার চলে দুবার charge | `ReserveCredit`-এর জায়গায় `ChargeCredit`, একই দুই জায়গায়, একই transaction-এ; key `charge:{orderId}`; approval-এর অপেক্ষায় থাকা অর্ডার কিছুই charge করে না | `ACreditOrder_PostsExactlyOneCharge_AtConfirmation`, `ReplayingAnApproval_DoesNotChargeTwice`, `APendingApprovalCreditOrder_ChargesNothing` | B3.3 |
| CS19 | **বাতিল হওয়া confirmed credit order-এর credit আটকে থাকে**, বা credit note + বাতিল মিলে দুবার ছাড়া | `ReduceChargeAsync` (cancel-এর transaction-এর ভেতরে): মোট কমানো ≤ charge; বাকি দেনা কমে, আগে দেওয়া টাকা on-account; একই key = একবার | `CancellingAConfirmedCreditOrder_ReleasesItsCredit_ThroughAReductionEntry`, `ReducingACharge_TwiceWithTheSameKey_AppliesOnce`, `CreditNoteAndCancellation_TogetherNeverExceedTheCharge`, `ThePaidPart_OfACancelledOrder_GoesOnAccount`; E7.2: `CancellingAConfirmedCreditOrder_RestocksReleasesItsCreditAndRecordsEverything`, `CancellingTwice_OrFourTimesAtOnce_ReturnsTheStockAndTheCreditOnce` | B3.5, E7.2 |
| CS20 | **Write-off দিয়ে বন্ধুর দেনা মাফ**, নিজের adjustment নিজে approve, adjustment দিয়ে দেনা বানানো | Request (`credit.adjust`) → অন্য Admin approve (`credit.adjust_approve` + step-up + কারণ); approver ≠ requester এবং ≠ সংশ্লিষ্ট payment-এর recorder/verifier; adjustment কখনো receivable বাড়ায় না; write-off ≤ বাকি | `WriteOff_NeedsApprovalByAnotherAdmin_WithStepUpAndReason`, `Adjustment_ApprovedByItsRequester_IsRefused`, `AReceivableIncrease_ByAdjustment_IsRefused`, `WriteOff_AboveTheOutstanding_IsRefused` | B3.6 |
| CS21 | **On-account টাকা বাইরে পাঠানো** (payout) একজনের হাতে | Payout = adjustment, four-eyes + step-up, ≤ on-account, MECHWARE-এর bank reference বাধ্যতামূলক | `Payout_AboveTheOnAccountBalance_IsRefused`, `Payout_NeedsFourEyesAndStepUp` | B3.6 |
| CS22 | **IDOR:** এক কোম্পানির buyer অন্য কোম্পানির balance/statement দেখেন | D2.10-এর নিয়ম: সদস্যপদ ও role (Admin বা Buyer, মালিকের সিদ্ধান্ত ৬) প্রতিবার DB থেকে; বাকি সবাই একই 404; `private, no-store` | `Idor_CreditOfAnotherCompany_Is404`, `CreditEndpoints_RoleMatrix_OnlyAdminAndBuyerSeeThem`, `CreditEndpoints_SendPrivateNoStore` | B3.9 |
| CS23 | Buyer-এর statement-এ staff-এর নাম/id, IP, ভেতরের note, বাতিল পেমেন্ট ফাঁস | Field-এর নির্দিষ্ট তালিকা; reference-এর শুধু শেষ ৪ অক্ষর; rejected পেমেন্ট ও adjustment-এর কারণ নেই | `BuyerStatement_HasOnlyTheAllowedFields`, `BuyerStatement_HidesRejectedPaymentsAndStaffNotes` | B3.9 |
| CS24 | Suspended কোম্পানি নিজের দেনা দেখতে বা শোধ করতে পারে না (তাই টাকা আটকে যায়) | Credit read ও payment record-এ Active বা Suspended চলে; শুধু Pending/Rejected 404; নতুন credit order আগের মতোই বন্ধ | `ASuspendedCompany_StillSeesItsStatement_AndCanBePaid` | B3.4, B3.9 |
| CS25 | **Overdue hold এড়ানো** (approval-এর পথে, একসাথে কয়েকটা অর্ডার), বা hold পেমেন্টও আটকে দেয় | Hold check company lock-এর ভেতরে, placement ও approval-confirm দুই জায়গায়; override শুধু Admin, step-up, কারণ, ≤ ৭ দিন; পেমেন্ট কখনো আটকায় না | `AnOverdueCompany_CannotPlaceANewCreditOrder`, `AnOrderApprovedWhileOnHold_IsCancelledNotCharged`, `OnHold_PaymentsCanStillBeRecordedAndVerified`, `HoldOverride_NeedsAdminStepUpAndReason_AndExpires` | B3.7 |
| CS26 | **Due date সরানো** (পরে terms বদলে, বা রাত ১২টার timezone সীমানায়) | `due_on` charge-এর সময় স্থির (ঢাকার তারিখ + তখনকার terms); পরে কোনো কিছু বদলায় না | `DueOn_IsFixedAtTheCharge_AndATermsChangeDoesNotMoveIt`, `DueOn_UsesTheDhakaDate_AroundMidnight` | B3.2, B3.3 |
| CS27 | **CSV export-এ formula injection** (`=HYPERLINK…` note বা bank নামে), বা সব কোম্পানির বই একজনে নামিয়ে নেওয়া | প্রতিটা cell quoted; `= + - @`, tab, CR দিয়ে শুরু হলে আগে `'`; প্রতিটা export audit ও rate-limit; সব কোম্পানির export-এ step-up | `CreditCsv_NeutralisesFormulaPrefixes`, `CreditExport_IsAudited_AndTheAllCompanyExportNeedsStepUp` | B3.8 |
| CS28 | **গরমিল চুপচাপ থেকে যায়** | রাতের reconciliation: I1–I6, chain, প্রতিটা confirmed credit order-এ ঠিক একটা charge, verified পেমেন্টের ledger row; গরমিলে alert + audit, নিজে কিছু ঠিক করে না | `Reconciliation_FindsAMismatch_AndAlerts_WithoutRepairing`, `Reconciliation_OnAConsistentBook_ReportsNothing` | B3.10 |
| CS29 | কে কখন কোন টাকা ছাড়লেন তার প্রমাণ নেই | প্রতিটা ledger-লেখা কাজে একই transaction-এ একটা audit row (actor, IP, correlation id, আগের ও পরের total) | `EveryLedgerWrite_HasAnAuditRow_WithActorIpAndCorrelationId` | B3.4, B3.5, B3.6 |
| CS30 | Migration-এর backfill ভুল (পুরোনো `credit_used` ব্যাখ্যাহীন) | প্রতিটা পুরোনো confirmed credit order-এর জন্য একটা charge; যোগফল না মিললে migration থেমে যায়, কোম্পানির id জানায়; "opening balance" বানানো হয় না | `Backfill_ChargesEveryConfirmedCreditOrder_AndMatchesCreditUsed`, `Backfill_WithAMismatch_StopsAndNamesTheCompany` | B3.3 |
| CS31 | ব্যক্তিগত তথ্য অকারণে জমা (wallet-এর ফোন নম্বর, NID) ও আইনি মেয়াদের আগে মুছে ফেলা [RULE] | bKash/Nagad-এ শুধু TrxID; ledger-এ IP নেই (audit-এ আছে); টাকার রেকর্ড অন্তত আইনি হিসাবের মেয়াদ পর্যন্ত (ধরে নেওয়া ৬ বছর, আইনজীবী/কর উপদেষ্টা নিশ্চিত করবেন); user erase-এ নাম লুকায়, id থাকে | `MobileWalletPayment_StoresTheTransactionIdOnly` | B3.4 |

**যে ঝুঁকি থেকে যায় (নকশায়):** দুজন finance staff একজোট হলে ভুয়া পেমেন্ট verify করতে পারেন (প্রতিরোধ: staff-এর 2FA, step-up, audit, রাতের reconciliation, cheque-এর clearing date); bank statement স্বয়ংক্রিয়ভাবে মেলানো হয় না (পরে); bounce fee ও দেরিতে দেওয়ার জরিমানা নেই; on-account টাকা নিজে থেকে নতুন অর্ডারে লাগে না (staff লাগান); অর্ডারের credit status প্রতিবার ledger থেকে হিসাব হয় (বড় কোম্পানিতে ধীর হলে H4-এ summary table)।

---

## ১৩. D4 — RFQ ও কোটেশন: অপব্যবহারের ঘটনা ও টেস্ট (২০২৬-১১-১৭) [RULE]

> **অবস্থা:** ডিজাইন ইউনিট D4.1-এর অংশ, AI সহকারীর লেখা। কোনো মানুষ রিভিউ করেননি, আর এখনো কোনো কোড নেই।
> নকশা: [`docs/adr/0011-rfq-and-quotes.md`](../adr/0011-rfq-and-quotes.md)। এই অংশ §৪-এর Q1–Q9 নিয়মগুলো আবার লেখে না। প্রতিটা সারি বলে কোন অপব্যবহার কোন নিয়ম (Q-নম্বর) আর নকশার কোন অংশ (ADR §) দিয়ে ঠেকানো হয়, আর কোন টেস্ট তা প্রমাণ করবে। টেস্টের নামগুলোই `docs/modules/D4.md`-এর প্রতিটা ইউনিটের exit test। Q-নিয়মের পুরোনো টেস্ট-নামগুলো রাখা হয়েছে; একটাই বদলেছে (QT9)।
> **§৪-এর তিনটা নিয়ম যেভাবে বাস্তবায়িত হবে:** Q7: PDF-এর কোনো লিংকই থাকবে না, শুধু sign-in করা অবস্থায় stream (ADR §10), তাই "signed link ≤ ১৫ মিনিট"-এর চেয়েও কঠোর; পরে কখনো লিংক এলে তখন ≤ ১৫ মিনিট, ব্যবহারকারী ও version-এ বাঁধা। Q8: launch-এ সব editor-এর জন্য একই ছাড়ের সীমা (৫ %), role অনুযায়ী আলাদা সীমা G5-এর পরে। Q9: launch-এ কোনো ফাইল সংযুক্তি নেই (C3.1 + V5 পর্যন্ত); CSV/BOM ব্রাউজারে পড়ে text হিসেবে পাঠানো হয় (D5.3-এর মতো)। Q4-কে আরও স্পষ্ট করা হয়েছে: "এক version থেকে একটাই **জীবিত** অর্ডার"। যে অর্ডার confirm হওয়ার আগেই বাতিল হয়েছে (approval না পাওয়া, টাকা না দেওয়া), মেয়াদের মধ্যে সেই কোটেশন আবার গ্রহণ করা যায়; confirm হওয়া অর্ডার থাকলে আর নয় (ADR §8.6)।

**নতুন asset:** কোম্পানির সাথে দর-কষাকষির দাম (কোটেশনের লাইন), ছাড়ের সিদ্ধান্ত ও তার কারণ, issued PDF, ক্রেতার RFQ-তে লেখা চাহিদা (কোন যন্ত্রাংশ, কত, কবে)।
**নতুন trust boundary:** (১) কোম্পানির সদস্য → RFQ/quote API: প্রতিবার `company_members` থেকে যাচাই হয়, ক্রেতা কোনো দাম পাঠান না; (২) quote desk staff → draft/send API: permission আর conflict-of-interest; (৩) approver → approve API: 4-eyes, step-up, hash; (৪) কোটেশন গ্রহণ → order path: একই transaction, lock-এর ক্রম `quotes → quote_versions → payments → orders → credit → companies → promotions`; (৫) PDF store → download: SHA-256 যাচাই।

| ID | অপব্যবহার | প্রতিকার (নিয়ম · ADR §) | যে টেস্ট প্রমাণ করবে | ইউনিট |
|---|---|---|---|---|
| QT1 | **IDOR:** অন্য কোম্পানির RFQ, কোটেশন, PDF দেখা বা গ্রহণ/প্রত্যাখ্যান করা; একই কোম্পানির ভেতরে Requester অন্যের কোটেশন দেখেন; permission ছাড়া staff | Q6 · §13: URL-এর company id প্রতিবার DB থেকে যাচাই হয়, আর quote-এর `company_id`-এর সাথে মিলতে হয়; অচেনা কোম্পানি, অন্য কোম্পানির কোটেশন, অনুমতিহীন role, Pending/Rejected কোম্পানি, ভুল নম্বর — সবার জন্য একই 404 body; Requester শুধু নিজের RFQ আর যেখানে তিনি contact; staff-এর জন্য `quotes.*` permission | `Idor_QuotesOfAnotherCompany_Are404` (list, detail, by-number, PDF, preview, accept, reject — সবকটায় একই body), `Idor_RfqsOfAnotherCompany_Are404`, `QuoteRoleMatrix_RequesterSeesOnlyOwn_BuyerAndAdminSeeAll_OthersNotFound`, `StaffQuoteEndpoints_WithoutPermission_Are403` | D4.5, D4.6, D4.10, D4.11 |
| QT2 | **ক্রেতা দাম বদলে দেন:** accept body-তে লাইন, পরিমাণ, দাম, কুপন বা অন্য company id পাঠানো; RFQ-তে দাম পাঠানো | Q2 · §8.1, §8.3: accept body-তে এমন কোনো field নেই, বাড়তি field উপেক্ষিত; প্রতিটা লাইন snapshot-এর net দামে, উৎস `Quote`; কোটেশনের লাইন promotion engine-এ যায় না; RFQ লাইনে দামের কোনো field নেই | `AcceptingUsesTheSnapshotPrices_NotTheClients`, `AcceptBody_CannotCarryLinesQuantitiesPricesCouponsOrACompany`, `AQuoteLine_NeverGetsAPromotionOrCoupon` | D4.9, D4.10 |
| QT3 | **পাঠানোর পরে কোটেশন বদলানো** (staff, code-এর bug, বা সরাসরি DB): লাইন, দাম, মেয়াদ, শর্ত; version মুছে ফেলা | Q1 · §6: Draft ছাড়া অন্য সব অবস্থায় domain method কাজ ফেরায়; guard trigger content column ও লাইনের যেকোনো বদল ফেরায়, §3-এর বাইরের transition ফেরায়, আর সব DELETE ফেরায়; বদলাতে হলে নতুন version; content hash-এ ক্রেতা যা দেখেন তার সবকিছু থাকে | `AnIssuedVersionCannotBeEdited`, `UpdatingALineOrTheHeaderOfASentVersion_FailsAtDbLevel`, `DeletingAVersionOrLine_FailsAtDbLevel`, `Revise_MakesANewVersion_AndLeavesTheSentOneUntouched`, `ContentHash_ChangesWithEveryBuyerVisibleField_AndNotWithStaffOnlyOnes` (property) | D4.2, D4.3, D4.4, D4.8 |
| QT4 | **একই কোটেশন থেকে দুটো অর্ডার:** দুবার ক্লিক, retry, একসাথে দুটো accept, replay; পুরোনো (superseded), প্রত্যাহার করা বা প্রত্যাখ্যাত version গ্রহণ; ক্রেতা যা দেখেননি সেই version গ্রহণ | Q4 · §8.6, §9: Idempotency-Key + request hash (একই key = একই অর্ডার, অন্য body = 409); quote row `FOR NO KEY UPDATE`-এ সিরিয়াল; DB-তে partial unique index `ux_orders_live_quote_version`; accept-এ `contentHash` ফেরত পাঠাতে হয়; শুধু "acceptable version" গ্রহণ করা যায়; confirm হওয়া অর্ডার থাকলে কোটেশন শেষ | `AcceptingTwiceMakesOneOrder`, `TwoParallelAccepts_MakeExactlyOneOrder`, `AcceptReplay_WithTheSameKeyButAnotherBody_Is409`, `ASupersededVersionCannotBeAccepted`, `AWithdrawnOrRejectedVersionCannotBeAccepted`, `AcceptWithAStaleContentHash_Is409`, `AQuoteWhoseOrderLapsedBeforeConfirmation_CanBeAcceptedAgainWhileValid`, `AQuoteWhoseOrderWasConfirmed_CannotBeAcceptedAgain_EvenAfterACancel`, `ASecondLiveOrderForOneVersion_FailsAtDbLevel` | D4.4, D4.9 |
| QT5 | **মেয়াদ শেষ কোটেশন গ্রহণ** (পুরোনো সস্তা দাম চালানো), ঘড়ির সীমানায় কারসাজি, অসীম মেয়াদ, approval দেরিতে হওয়ায় মেয়াদ আগেই কমে যাওয়া | Q3 · §7: মেয়াদ সার্ভারের ঘড়িতে (অর্ডারের একক মুহূর্ত); `valid_until` = শেষ বৈধ দিনের পরের দিনের ঢাকার ০০:০০, exclusive; client কোনো তারিখ পাঠান না; মেয়াদ ১–৯০ দিন, ৩০ দিনের বেশি হলে approval; মেয়াদ শুরু হয় পাঠানোর মুহূর্ত থেকে; approval-এর অপেক্ষায় থাকা অর্ডার নিজের দাম রাখে | `AnExpiredQuoteCannotBeAccepted`, `ValidUntil_IsTheStartOfTheDhakaDayAfterTheLastValidDay_AndIsExclusive`, `ValidityStartsWhenTheVersionIsSent_NotWhenItWasDrafted`, `ValidityAboveTheMaximum_IsRefused_AndAboveTheThreshold_NeedsApproval`, `AnOrderWaitingForApproval_KeepsTheQuotePrice_AfterTheQuoteExpires` | D4.2, D4.3, D4.8, D4.9 |
| QT6 | **এক কোম্পানির দর-কষাকষি আরেক কোম্পানিতে ফাঁস:** buyer response-এ cost, floor, অন্য কোম্পানির রেফারেন্স দাম বা ভেতরের note; public cache বা search index-এ কোটেশনের দাম; ভুল মানুষের কাছে e-mail; অন্য কোম্পানির কোটেশন কপি করা; staff নিজের কোম্পানির কোটেশন বানান বা approve করেন | §4, §13, §14: buyer DTO field ধরে ধরে বানানো (unit test তালিকা আটকে রাখে); Draft/Pending version কখনো দেখানো হয় না; `private, no-store`; কোটেশনের দাম `sf:*`, CDN বা search index-এ যায় না; notice শুধু তখনকার কার্যকর সদস্যদের কাছে যায়, outbox payload-এ শুধু id; contact-কে ওই কোম্পানির সদস্য হতে হবে; অন্য কোম্পানিতে কপি করার কোনো সুবিধা নেই; কোম্পানির সদস্য এমন staff সেই কোম্পানির কোটেশন edit/send/approve করতে পারেন না | `BuyerQuoteResponse_HasOnlyTheAllowedFields`, `QuoteEndpoints_SendPrivateNoStore`, `QuotePrices_NeverReachPublicCachesOrTheSearchIndex`, `QuoteNotices_GoOnlyToEffectiveMembersOfTheQuotesCompany`, `AQuoteContact_MustBeAMemberOfTheQuotesCompany`, `AStaffMemberWhoBelongsToTheCompany_CannotEditApproveOrSendItsQuote`, `AMemberOfTwoCompanies_SeesEachCompanysQuotesOnlyUnderThatCompany` | D4.6, D4.7, D4.10, D4.12 |
| QT7 | **একজন staff একা বড় ছাড় দেন** (পরিচিত ক্রেতাকে): নিজের কোটেশন নিজে approve; চুরি হওয়া session; floor-এর নিচে বা cost-এর নিচে দাম; approve-এর আগে লাইন বদলানো; প্রতিটা version-এ ৫ % করে নামানো (ratchet); কোন লাইনে কী flag তা না দেখে approve | Q8 · §5: সীমা ক্রেতার নিজের resolver দামের তুলনায় (আগের version-এর তুলনায় নয়); floor = ADR-0008-এর নিয়ম (cost না থাকলে নিচে ধরা হয়); cost-এর নিচে প্রত্যাখ্যাত; approver ≠ creator/submitter/contributor (domain + DB CHECK + trigger); `quotes.approve` + step-up `quote.approve`; floor-এর নিচের লাইনে আলাদা `quotes.approve_below_floor` (Admin) আর লাইনভিত্তিক কারণ; `contentHash` ও flag-ওয়ালা লাইনের id-তালিকা সার্ভারের তালিকার সাথে হুবহু মিলতে হয়; approval-এর মুহূর্তে cost, floor ও দামের নতুন হিসাব; row lock; প্রতিটা ধাপ audit-এ | `ADiscountBeyondTheStaffLimitNeedsASecondApprover`, `ABelowFloorLine_CannotBeSentWithoutApproval`, `Approve_ByTheAuthorSubmitterOrAContributor_IsRefused`, `ApprovedByEqualsCreatedBy_FailsAtDbLevel`, `Approve_WithoutStepUpOrPermission_IsRefused`, `Approve_BelowFloorWithoutTheExtraPermission_IsRefused`, `Approve_WithoutAcknowledgingExactlyTheFlaggedLines_IsRefused`, `Approve_WithAStaleContentHash_IsRefused`, `FlagsAreRecheckedAtApproval_WithTheCostFloorAndPriceOfThatMoment`, `TheDiscountIsMeasuredAgainstTheBuyersResolverPrice_NotThePreviousVersion`, `ALineBelowCost_IsRefused_UnlessConfigAllows`, `ALineWithoutCost_IsTreatedAsBelowFloor`, `TwoParallelApprovals_ApproveAndSendOnce`, `EveryQuoteTransition_IsAudited_WithActorIpAndContentHash` | D4.3, D4.4, D4.8 |
| QT8 | **গণনা (enumeration):** কোটেশন বা RFQ নম্বর আন্দাজ করে খোঁজা; উত্তরের পার্থক্য দেখে "আছে কিনা" বোঝা; তালিকার মোট সংখ্যা থেকে অন্যদের কাজের আন্দাজ | §3, §12, §13: নম্বরে ৬টা random base32 অক্ষর (brief-এর ৪টার বদলে), id UUID; by-number-এ অচেনা, ভুল আকারের আর অন্য কোম্পানির নম্বর — তিনটাতেই একই 404; by-number ও PDF-এ rate limit (`QuoteRead`); তালিকা আর তার মোট সংখ্যা শুধু caller যা দেখতে পারেন তার | `QuoteAndRfqNumbers_AreRandom_AndValidated`, `QuoteByNumber_UnknownMalformedOrForeign_IsTheSame404`, `QuoteByNumber_IsRateLimited`, `QuoteLists_CountOnlyTheCallersVisibleQuotes` | D4.2, D4.10 |
| QT9 | **PDF জাল করা বা বদলানো:** ক্রেতা বা তৃতীয় পক্ষ কম দামের নকল PDF দেখান; store-এ রাখা ফাইল বদলে দেওয়া; পরে বদলানো কোম্পানি বা পণ্যের তথ্য দিয়ে পুরোনো PDF আবার বানানো; draft-কে offer হিসেবে চালানো | Q7 · §10: PDF শুধু frozen snapshot থেকে বানানো হয়; প্রথমবার বানিয়ে store-এ রাখা হয়, SHA-256 DB-তে; প্রতিবার download-এর আগে hash আবার হিসাব হয়, না মিললে ফাইল দেওয়া হয় না (audit + alert); PDF-এ নম্বর, revision আর content-hash-এর প্রথম ১৬ অক্ষর ছাপা থাকে; staff যেকোনো কপি SHA দিয়ে যাচাই করতে পারেন; কোনো public URL নেই; draft শুধু "DRAFT, NOT AN OFFER" লেখা preview, store হয় না | `ThePdfDownload_ChecksAccessEveryTime_AndThereIsNoPublicUrl` (Q7-এর আগের নাম `ThePdfLinkExpires_AndChecksAccess`-এর বদলে), `TheQuotePdf_IsByteIdenticalForTheSameSnapshot_AndItsHashIsRecorded` (golden file), `ATamperedStoredPdf_IsNotServed_AndIsAudited`, `APdfIsBuiltOnlyFromTheSentSnapshot_LaterCompanyOrProductChangesDoNotShow`, `LookupBySha_FindsTheIssuedVersion_AndNothingElse`, `DraftsHaveNoIssuedPdf` | D4.11 |
| QT10 | **RFQ spam বা সম্পদ নষ্ট:** যাচাই না হওয়া অ্যাকাউন্ট বা কোম্পানি; ঘণ্টায় শত শত RFQ; বিশাল BOM; replay করে একই RFQ বারবার; cart-এর বদলে body-তে বানানো লাইন | Q9 · §2: `UserStatus.Active` + Active কোম্পানি; প্রতি কোম্পানিতে ঘণ্টায় ≤ ১০টা আর একসাথে খোলা ≤ ২০টা, DB-তে গোনা হয় advisory lock-এর ভেতরে; প্রতি ব্যবহারকারীর HTTP সীমা; ≤ ৫০০ লাইন, ≤ ১,০০,০০০ অক্ষর; Idempotency-Key; `cart` উৎসে সার্ভার নিজের cart পড়ে | `RfqIsRateLimited`, `AnUnverifiedAccountOrCompany_CannotRequestAQuote`, `AnRfqOverTheLineOrSizeLimit_IsRefused`, `RfqReplay_WithTheSameKey_CreatesOne`, `RfqFromCart_ReadsTheServerCart_NotTheBody` | D4.5 |
| QT11 | **গ্রহণের পথে কোম্পানির approval বা খরচের সীমা এড়ানো**; Buyer role ছাড়া সদস্য অর্ডার বানান | Q5 · §8.2, §8.7: অর্ডার দেওয়ার একই পথ: Buyer role, approval rule সার্ভারের মোট অঙ্কে, অর্ডার আর approval request একই transaction-এ, spend limit ও cost-centre budget আগের মতো | `AcceptanceWaitsForApproval`, `Accept_ByAMemberWithoutTheBuyerRole_Is403`, `Accept_ObeysTheSpendLimitAndBudget_LikeAnOrder` | D4.9, D4.10 |
| QT12 | **কোটেশন দিয়ে credit-এর নিয়ম এড়ানো:** overdue hold বা limit পার হলেও বাকিতে গ্রহণ; "advance only" কোটেশন বাকিতে নেওয়া; দুবার charge; কোটেশনে নিজের মতো credit terms লেখা | §8.3, §8.7 (ADR-0010): একই `ChargeCreditAsync` (company lock, limit, hold), charge শুধু confirmation-এ, key `charge:{orderId}`; `Advance` শর্তে `payOnCredit` = 409; কোটেশন কখনো terms দেয় না বা বদলায় না; approval-এর সময় hold পেলে অর্ডার বাতিল হয়, আর মেয়াদের মধ্যে কোটেশন আবার গ্রহণযোগ্য হয় | `AcceptingOnCredit_WhileOnHoldOrOverTheLimit_IsRefused`, `AnAdvanceOnlyQuote_CannotBeAcceptedOnCredit`, `AnAcceptedCreditQuote_PostsExactlyOneCharge_AtConfirmation`, `AnApprovalThatFindsTheHold_CancelsTheOrder_AndTheQuoteCanBeAcceptedAgain` | D4.9 |
| QT13 | **Race ও deadlock:** withdraw বা নতুন version পাঠানোর সাথে একই সময়ে accept; accept, cancel, পেমেন্ট verify আর নতুন অর্ডার একসাথে | §9: lock-এর একটাই ক্রম `quotes → quote_versions → payments → orders → credit_payments/adjustments → companies → promotions`; `payments`, `orders`, credit বা `companies`-এর lock ধরে থাকা কোনো পথ quote row lock করে না; FK-র জন্য `FOR NO KEY UPDATE` (B3.5-এর শিক্ষা); status check ঠিক করে কে জিতবে | `WithdrawRacingAnAccept_NoOrderCommitsAfterTheWithdrawal`, `SendingANewVersionRacingAnAccept_ExactlyOneWins`, `AcceptCancelVerifyAndPlace_InParallel_NeverDeadlock_AndTheLedgerAddsUp` | D4.9 |
| QT14 | **কোটেশন দিয়ে stock আটকে রাখা** (কম দামের কোটেশন খুলে রেখে অন্যদের "stock নেই" দেখানো), বা গ্রহণে stock আবার যাচাই না হওয়া | §8.5: পাঠানো কোটেশন কোনো stock ধরে রাখে না; গ্রহণ ঠিক অর্ডারের মতো reserve করে (৩০ মিনিট / ২৪ ঘণ্টা ৩০ মিনিট) আর খোলা unpaid অর্ডারের সীমায় (৩) গোনা হয় | `ASentQuoteHoldsNoStock_AndAcceptingRechecksIt`, `QuoteAcceptance_CountsTowardsTheOpenUnpaidOrderCap` | D4.9 |
| QT15 | **মোট অঙ্ক চুপচাপ বদলায়:** পাঠানোর পরে VAT-এর হার বা হিসাব করা shipping বদলে যায়; preview যা দেখায় অর্ডার তার চেয়ে বেশি নেয় | §8.1, §8.3, §8.4: `expectedGrandTotalMinor` বাধ্যতামূলক; সার্ভারের মোট তার চেয়ে বেশি হলে 409 `PRICE_CHANGED`, অর্ডার হয় না; net দাম কোটেশনেরই থাকে, শুধু VAT লাইন বদলায় (ADR-0008 §2); preview আর গ্রহণ একই হিসাবের পথে | `Accept_WithoutAnExpectedTotal_Is400`, `AVatRateChangeAfterSending_KeepsTheNetPrice_AndNeedsConfirmation`, `AcceptPreview_EqualsTheAcceptedOrder` | D4.9, D4.10 |
| QT16 | **নির্দিষ্ট shipping-এর কোটেশনে ঠিকানা বদলে দেওয়া** (কাছের site-এর ফি দিয়ে দূরে পাঠানো), বা মুছে ফেলা site-এ পাঠানো | §8.3: `Fixed` হলে গন্তব্য = কোটেশনের site-এর snapshot ঠিকানা; অন্য site বা ঠিকানা দিলে 409 `QUOTE_DELIVERY_FIXED`; site মুছে গেলে 409 `QUOTE_DELIVERY_SITE_GONE` | `AFixedShippingQuote_ShipsOnlyToItsQuotedSite`, `AQuotedSiteThatWasRemoved_RefusesTheAcceptance` | D4.9 |
| QT17 | **খোলা লেখার ঘরে ক্ষতিকর লেখা** (RFQ message, note, কারণ, শর্ত): পাতায় script, e-mail-এ HTML, PDF-এ বানানো লেখা, CSV formula | §14: সাধারণ text হিসেবে জমা হয়, control character বাদ দেওয়া হয় (D5.1-এর মতো); React, e-mail template ও PDF-এর text run কখনো markup হিসেবে পড়ে না; analytics-এ যায় না; D4-এ কোটেশনের কোনো CSV export নেই | `RfqTextFields_AreStoredAsPlainText_AndControlCharactersAreStripped`, `QuoteTextOnThePdfAndInEmails_IsNeverInterpretedAsMarkup` | D4.5, D4.11, D4.12 |

**যে ঝুঁকি থেকে যায় (নকশায়):**
- দুজন স্টাফের যোগসাজশ, বা একজনের হাতে দুটো স্টাফ অ্যাকাউন্ট: 4-eyes এটা ঠেকায় না। নিয়ন্ত্রণ: স্টাফের বাধ্যতামূলক 2FA, step-up, audit, conflict-of-interest নিয়ম, আর floor-এর নিচের জন্য শুধু Admin।
- সীমার ভেতরের ছাড় (≤ ৫ %) একজনই দিতে পারেন, আর launch-এ কোটেশনের মোট অঙ্কের কোনো সীমা নেই (মালিকের পরে যোগ করার বিকল্প, ADR §15 সিদ্ধান্ত ৩)। Floor সবসময় খাটে।
- Stock ধরে রাখা হয় না: গ্রহণের আগে পণ্য শেষ হয়ে যেতে পারে (তখন revision লাগবে)। Backorder নেই।
- Cost ভুল থাকলে floor-ও ভুল হয়, আর সেটা ধরা পড়ে না (D2-এর মতোই)।
- PDF-এ ডিজিটাল স্বাক্ষর নেই: যাচাই হয় SHA-256 আর ছাপা verification code দিয়ে, MECHWARE-কে জিজ্ঞাসা করে। এর বাইরে আইনি স্বীকৃতি লাগলে মালিকের সিদ্ধান্ত (ADR §15 d)।
- Renderer (SkiaSharp) upgrade হলে নতুন করে বানানো PDF-এর hash বদলাতে পারে: তাই store করা ফাইলই প্রমাণ; ফাইল হারালে নতুন version পাঠাতে হয়।
- Audit log এখনো tamper-evident নয় (v1 H3)।

---

## সম্পর্কিত ডক
- D2 নকশা: [`docs/adr/0008-price-lists-and-contract-prices.md`](../adr/0008-price-lists-and-contract-prices.md) (§৮ এর ভিত্তি)
- B3 নকশা: [`docs/adr/0010-credit-settlement.md`](../adr/0010-credit-settlement.md) (§১২ এর ভিত্তি; ইউনিট [`docs/modules/B3.md`](../modules/B3.md))
- D4 নকশা: [`docs/adr/0011-rfq-and-quotes.md`](../adr/0011-rfq-and-quotes.md) (§১৩ এর ভিত্তি; ইউনিট [`docs/modules/D4.md`](../modules/D4.md))
- কার্ট: [`docs/modules/E1.md`](../modules/E1.md) (§৯-এর ইউনিট ও handoff)
- v1: [`THREAT_MODEL_COMPANIES_APPROVALS.md`](THREAT_MODEL_COMPANIES_APPROVALS.md) (H1 approval, H3 audit — এখানে উল্লেখ আছে)
- ASVS ম্যাট্রিক্স: [`ASVS_MATRIX.md`](ASVS_MATRIX.md)
- আগের রিভিউ: `docs/reviews/R3_ORDERS_AND_PAYMENTS.md`
