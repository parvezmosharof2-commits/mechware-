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
| Quotes | এখনো বানানো হয়নি | কোড লেখার **আগেই** নিরাপত্তার নিয়ম (design rules) ঠিক করা হয়েছে |
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

**যে ঝুঁকি থেকে যায়:**
- দুজন স্টাফের যোগসাজশ, বা একজনের হাতে দুটো স্টাফ অ্যাকাউন্ট: 4-eyes এটা ঠেকায় না। নিয়ন্ত্রণ: স্টাফের বাধ্যতামূলক 2FA, আলাদা `users.manage`, audit, floor-এর নিচের জন্য আলাদা permission।
- Sale price আর promotion আজ margin floor-এর সাথে মেলানো হয় না। নিয়ম A-তে একটা ভুল sale price contract ক্রেতারাও পাবেন।
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

## সম্পর্কিত ডক
- D2 নকশা: [`docs/adr/0008-price-lists-and-contract-prices.md`](../adr/0008-price-lists-and-contract-prices.md) (§৮ এর ভিত্তি)
- কার্ট: [`docs/modules/E1.md`](../modules/E1.md) (§৯-এর ইউনিট ও handoff)
- v1: [`THREAT_MODEL_COMPANIES_APPROVALS.md`](THREAT_MODEL_COMPANIES_APPROVALS.md) (H1 approval, H3 audit — এখানে উল্লেখ আছে)
- ASVS ম্যাট্রিক্স: [`ASVS_MATRIX.md`](ASVS_MATRIX.md)
- আগের রিভিউ: `docs/reviews/R3_ORDERS_AND_PAYMENTS.md`
