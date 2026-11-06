# MECHWARE — মাস্টার প্ল্যান (১০০K USD মান, ১০/১০ লক্ষ্য)

> **এটাই একমাত্র সত্যের উৎস।** কী বানাতে হবে, কোন ক্রমে, আর প্রতিটা কাজ শেষে কী প্রমাণ দিতে হবে — সব এখানে।
> পুরনো `MASTER_EXECUTION_GUIDE.md` (২১ দিন, Gemini 1.5 Pro) বাতিল; সেটা আর পুরনো নোট/প্রম্পট প্যাক এখন `docs/archive/`-এ।
> ভিত্তি: `docs/01_PROJECT_BRIEF.md` (v2), `docs/100K_GRADE_REVIEW_AND_PLAN.md`, আর ২০২৬-১১-০৪ তারিখের স্বাধীন টেস্ট রিপোর্ট।
> সিদ্ধান্ত (মালিক, ২০২৬-১১-০৪): **বর্তমান কোডের উপর গড়া হবে**, কাজ করবে **AI + মালিক**, গেট থাকবে **CI-তে**।

---

## ০. বর্তমান অবস্থা (শুরুর বিন্দু)

| ক্ষেত্র | স্কোর | মন্তব্য |
|---|---|---|
| ব্যাকএন্ড ইঞ্জিনিয়ারিং ও টেস্টিং | ৮.৫/১০ | ৩৫০ unit + ১৭৯ integration + ৪৯ E2E পাস |
| নিরাপত্তা | ৬.৫/১০ | ১ High + ৩ Medium খোলা ছিল (স্প্রিন্ট ০-এ ঠিক করা হয়েছে) |
| প্রোডাক্ট সম্পূর্ণতা | ৩/১০ | লঞ্চ-স্কোপের ~৩৫% |
| ডকুমেন্টের সামঞ্জস্য | ৫/১০ | স্প্রিন্ট ০-তে ঠিক করা হয়েছে (§৭ স্কোরকার্ড দেখুন) |
| অপারেশন / ডেপ্লয় | ২/১০ | Dockerfile, IaC, মনিটরিং নেই |
| **সামগ্রিক** | **৫.৫/১০** | |

যা রাখা হবে (ভালো মানের, আবার লেখার দরকার নেই): Identity (DB-তে, TOTP, refresh rotation), Catalog, Inventory
(row lock), Outbox, Orders (idempotency, state machine, pricing), Payments (webhook inbox, server-side confirm),
Companies, Approvals, Promotions, design system, web/console-এর company অংশ।

---

## ১. "১০/১০" মানে কী (লঞ্চের মাপকাঠি)

প্রতিটা সংখ্যা মাপা যায়; অনুমান দিয়ে টিক দেওয়া যাবে না।

| # | মাপকাঠি | লক্ষ্য | কীভাবে প্রমাণ |
|---|---|---|---|
| 1 | পার্ট নম্বর থেকে অনুমোদিত অর্ডার | < ৩ মিনিট | ৫ জন আসল ক্রেতার usability test, ভিডিও |
| 2 | দাম কোথা থেকে আসে | ১০০% সার্ভারের pricing engine | tamper test সব endpoint-এ |
| 3 | অন্য কোম্পানির ডেটা দেখা | ০টি | IDOR suite (CI) |
| 4 | Mushak-6.3 ইনভয়েস | প্রতিটা করযোগ্য অর্ডারে | VAT কনসালট্যান্টের লিখিত মতামত |
| 5 | নিরাপত্তা | ASVS 5.0 L2 ম্যাট্রিক্স ১০০%, পেমেন্ট/অ্যাডমিন L3 | `docs/security/ASVS_MATRIX.md` |
| 6 | বাইরের পেন-টেস্ট | ০ High/Critical (retest-এর পর) | পেন-টেস্ট রিপোর্ট |
| 7 | পারফরম্যান্স | search p95 < ১৫০ ms, add-to-cart p95 < ৩০০ ms, checkout p95 < ৮০০ ms | k6 রিপোর্ট |
| 8 | ওয়েব ভাইটাল | Lighthouse mobile ≥ ৯০, LCP < ২.৫ s, CLS < ০.১, JS < ১৭০ kB | Lighthouse CI, `pnpm check:bundle` |
| 9 | Accessibility | WCAG 2.2 AA, axe ০ violation, EN + BN | axe CI + হাতে screen-reader পাস |
| 10 | Oversell | ০ (৫,০০০ যুগপৎ ব্যবহারকারী) | k6 + DB invariant check |
| 11 | ব্যাকআপ | RPO ≤ ১৫ মিনিট, RTO ≤ ৪ ঘণ্টা | restore drill-এর লগ |
| 12 | আইন | Guideline 2021 ডেলিভারি টাইমার, PDPO 2025 consent/export/delete | আইনজীবীর মতামত + E2E |

> **সৎ সতর্কতা:** শুধু AI + মালিক দিয়ে কোড ১০/১০ করা সম্ভব। কিন্তু লঞ্চের আগে **৩টা বাইরের যাচাই ছাড়া "১০/১০"
> দাবি করা যাবে না**: (১) এক সপ্তাহের স্বাধীন পেন-টেস্ট, (২) VAT কনসালট্যান্ট দিয়ে Mushak-6.3 যাচাই,
> (৩) আইনজীবী দিয়ে PDPO 2025 ও Digital Commerce Guideline 2021 যাচাই। বাকি সব বিনামূল্যের টুল দিয়ে স্বয়ংক্রিয়।

---

## ২. কাজের পদ্ধতি (AI + মালিক)

প্রতিটা কাজ একটা ছোট **slice** (১–৩ দিনের কাজ, একটা PR)। ধাপগুলো সবসময় এই ক্রমে:

```
১. ব্রিফ       → মালিক এক অনুচ্ছেদে লেখেন: কে, কী করতে চায়, কেন। ব্যবসার নিয়ম স্পষ্ট করেন।
২. Threat model → AI নতুন endpoint/টেবিলের জন্য STRIDE + abuse case লেখে (docs/security/ এ)।
৩. টেস্ট আগে   → ব্যবসার নিয়ম ও abuse case-এর টেস্ট লেখা হয়, সেগুলো প্রথমে লাল।
৪. কোড          → টেস্ট সবুজ না হওয়া পর্যন্ত।
৫. Mutation     → মূল গার্ডগুলো একে একে সরিয়ে দেখানো যে টেস্ট ফেল করে (PR-এ লিখে রাখা)।
৬. স্বয়ংক্রিয় গেট → CI (§৫) সব সবুজ।
৭. AI স্ব-রিভিউ → `/code-review high` ও `/security-review` চালানো, পাওয়া সমস্যা ঠিক করা।
৮. মালিকের ডেমো → ৫ মিনিট: স্ক্রিনশট/ভিডিও দেখে হ্যাঁ/না। "না" হলে কারণসহ ফেরত।
৯. Merge        → PROGRESS.md আপডেট, স্কোরকার্ড আপডেট।
```

**মালিকের দায়িত্ব:** ব্যবসার নিয়মের সিদ্ধান্ত, প্রতি slice-এর ডেমো সাইন-অফ, GitHub সেটিংস (branch protection, secret
scanning), বাইরের তিন যাচাইকারী ঠিক করা, আসল ক্রেতাদের সাথে কথা বলা, আসল পণ্যের ছবি ও datasheet জোগাড়।

**AI-এর দায়িত্ব:** কোড, টেস্ট, threat model, ডকুমেন্ট, CI, রিভিউ রিপোর্ট। AI কখনো নিজের কাজকে নিজে "১০/১০" বলবে না;
প্রমাণ (টেস্টের আউটপুট, স্ক্রিনশট, সংখ্যা) দেখাবে।

**নিয়ম:** `main`-এ সরাসরি push নিষেধ। প্রতিটা PR-এ `.github/pull_request_template.md` পূরণ বাধ্যতামূলক।

---

## ৩. ধাপে ধাপে রোডম্যাপ (১ স্প্রিন্ট = ১ সপ্তাহ, মোট ~২০ সপ্তাহ)

প্রতিটা ধাপের **Exit gate** পূরণ না হলে পরের ধাপে যাওয়া যাবে না।

### স্প্রিন্ট ০ — স্থিতিশীল করা (১ সপ্তাহ) — ✅ সম্পূর্ণ (২০২৬-১১-০৫; PR #25, #26, #27)
| কাজ | বিস্তারিত |
|---|---|
| বাগ #১ (High) | Development ছাড়া অন্য environment-এ চালুর সময় demo ইমেইল বা প্রকাশিত TOTP secret-ওয়ালা অ্যাকাউন্ট পেলে API চালু হবে না (fail closed) |
| বাগ #২ | ইমেইল/ফোন যাচাই না করা অ্যাকাউন্ট অর্ডার দিতে পারবে না; একজন ক্রেতার খোলা unpaid অর্ডার সর্বোচ্চ ৩টা |
| বাগ #৩ | একাধিক গুদাম থেকে ভাগ করে রিজার্ভ করা (storefront যা দেখায়, ততটাই কেনা যায়) |
| বাগ #৪ | `shipToJson`-এর বদলে typed ঠিকানা (address id বা যাচাই করা ফিল্ড), আকারের সীমা, ঠিকানা বাধ্যতামূলক; ভুল ইনপুটে 400, কখনো 500 নয় |
| বাগ #৫, #৭ | রেজিস্ট্রেশনে অ্যাকাউন্ট আছে কিনা ফাঁস না করা; web/console-এ security header (CSP, HSTS, X-Frame-Options) |
| বাগ #৬ | `ForwardedHeaders`-এ `KnownNetworks` কনফিগারযোগ্য করা (Cloudflare/LB) |
| পরিচ্ছন্নতা | ✅ ৩৩টা ESLint warning ঠিক, `--max-warnings 0` চালু; ✅ রুটের পুরনো Vite/AI Studio অ্যাপ মোছা, পুরনো নোট `docs/archive/`-এ |
| CI গেট | ✅ Semgrep ও Trivy ব্লকিং (finding log-এ দেখায়); ✅ ZAP চালানো হয়েছে; ⬜ ASVS ম্যাট্রিক্সে অফিসিয়াল requirement ID বসানো |
| ডকুমেন্ট | ✅ `ARCHITECTURE.md` v2 (কোড + brief v2); ADR-0003 ও DATA_MODEL-এ `MW-` নম্বর |
| মালিক | GitHub-এ branch protection, secret scanning + push protection চালু |

**Exit gate:** উপরের প্রতিটা বাগের regression test + mutation check; সব CI সবুজ; নতুন ZAP baseline-এ High নেই।

### স্প্রিন্ট ১–২ — ডিসকভারি ও ডিজাইন
- ৫ জন আসল ক্লায়েন্ট-ক্রেতার সাথে ৩০ মিনিটের সাক্ষাৎকার (মালিক); প্রশ্ন ও উত্তর `docs/discovery/`-এ।
- ১২টা মূল স্ক্রিনের ডিজাইন ৩৬০/৭৬৮/১৪৪০ px-এ, **light theme ডিফল্ট**: হোম, PLP (টেবিল), PDP, quick order, কার্ট,
  চেকআউট, কোটেশন, approval inbox, কোম্পানি ড্যাশবোর্ড, ইনভয়েস, কনসোল অর্ডার, কনসোল প্রাইস লিস্ট।
  (ডিজাইনার নেই, তাই AI HTML প্রোটোটাইপ বানাবে, মালিক ক্রেতাদের দেখিয়ে মতামত নেবেন।)
- Threat model v2: Pricing, Quotes, Payments, Invoicing।

**Exit gate:** মালিকের সাইন-অফ করা প্রোটোটাইপ + threat model।

**অবস্থা (২০২৬-১১-০৫):**
- ✅ ১২ স্ক্রিনের ক্লিকযোগ্য প্রোটোটাইপ তৈরি:
  - `docs/prototypes/sprint1/` (EN/বাংলা, light/dark, ৩৬০–১৪৪০ px)
  - প্রতিটা স্ক্রিনে সাক্ষাৎকারের প্রশ্ন আছে
- ⏳ মালিক: ৫ জন ক্রেতার সাক্ষাৎকার, তারপর সাইন-অফ।
- ✅ AI: threat model v2, খসড়া, মানুষের রিভিউ বাকি (`docs/security/THREAT_MODEL_PRICING_QUOTES_PAYMENTS_INVOICING.md`)। Payments-এ দুটো High (P1, P2) RUN দিয়ে পুনরুৎপাদিত; ফিক্স আলাদা PR-এ।

### স্প্রিন্ট ৩–৪ — PIM (পণ্যের তথ্য)
- unit-সহ attribute class (ETIM-ধাঁচ), datasheet/CAD/সার্টিফিকেট আপলোড (magic-byte check + ClamAV স্ক্যান + signed URL),
  বিকল্প/উত্তরসূরি পণ্য, lifecycle (active / NRND / obsolete), MOQ ও pack size কলাম (CartEngine ইতিমধ্যে নেয়)।
- আসল পণ্যের ছবি ও যাচাই করা স্পেক (মালিক জোগাড় করবেন)।

**Exit gate:** ৪০০+ পণ্য যাচাই করা স্পেকসহ; ফাইল আপলোডের abuse test (ভুল টাইপ, বড় ফাইল, EICAR ভাইরাস নমুনা) পাস।

**অবস্থা (২০২৬-১১-০৬):**
- ✅ **ধাপ ১:** supply status (active / NRND / obsolete), উত্তরসূরি পণ্য, MOQ ও pack size।
  - Domain-এর নিয়ম মেনে চলে, আর database-এ check constraint আছে।
  - Public PDP API-তে field-গুলো আসে।
  - টেস্ট আর mutation check হয়েছে।
- ⏳ **PDP-তে banner আর quantity ধাপ:** স্প্রিন্ট ৫–৬-এর PDP পাতার সাথে হবে (web-এ PDP পাতা এখনো নেই)।
- ⏳ **Seller বা admin-এর সম্পাদনার API:** product লেখার API এলে সেখানে `SetSupply`/`SetOrderUnits` ব্যবহার হবে।
  - উত্তরসূরি "একই seller-এর পণ্য" কিনা, সেই যাচাই domain-এ আছে, তাই ওই API-কে আলাদা করে এটা যাচাই করতে হবে না।
- ⏳ **পরের ধাপ:** unit-সহ attribute, datasheet/CAD আপলোড (AV scan), বিকল্প পণ্য।

### স্প্রিন্ট ৫–৬ — সার্চ + পণ্যের তালিকা ও পেজ
- Meilisearch adapter (outbox থেকে sync), parametric facet, পার্ট-নম্বর normalisation (`6ES7 214-1AG40-0XB0` = `6ES72141AG400XB0`), Banglish প্রতিশব্দ।
- PLP: ডেস্কটপে product table, মোবাইলে list; PDP: স্পেক টেবিল, quantity-break টেবিল, গুদামভিত্তিক স্টক, "Add to quote"।

**Exit gate:** search p95 < ১৫০ ms (k6); Lighthouse ≥ ৯০; E2E EN + BN।

### স্প্রিন্ট ৭–৯ — B2B মূল অংশ
- Pricing: list → quantity break → customer-group price list → contract price → quote price; price-list পরিবর্তনে 4-eyes।
- RFQ ও versioned quote (PDF, মেয়াদ, quote → order; quote **অপরিবর্তনীয় snapshot**)।
- Approval-কে অর্ডারের সাথে যুক্ত করা (`pending_approval`), spend limit ও cost-centre budget চেক (threat model H1)।
- Quick order: SKU paste, CSV/BOM আপলোড, প্রতি লাইনে যাচাই; requisition list, reorder।

**Exit gate:** IDOR suite সব নতুন endpoint-এ; pricing property test (২০k কেস); approval bypass test।

### স্প্রিন্ট ১০–১২ — কার্ট, চেকআউট, পেমেন্ট, ইনভয়েস
- Redis cart + signed guest token + লগইনে merge (CartEngine ইতিমধ্যে আছে)।
- পেমেন্ট: SSLCommerz (IPN + Order Validation API), bKash (server execute/query), ব্যাংক ট্রান্সফার/PO, ক্রেডিট টার্ম (আছে), COD + OTP + সীমা।
- রাতের reconciliation job, refund, restock (confirmed অর্ডার বাতিল + ক্রেডিট ফেরত)।
- Mushak-6.3 ইনভয়েস: নম্বর, BIN, VAT ভাঙন, অপরিবর্তনীয় PDF; Mushak-9.1 export।
- Guideline 2021: অগ্রিম পেমেন্টের পর ৪৮ ঘণ্টায় কুরিয়ারে হস্তান্তর, ৫/১০ দিনের ডেলিভারি টাইমার।

**Exit gate:** পেমেন্ট tamper/replay test; sandbox-এ আসল গেটওয়ে দিয়ে end-to-end; নমুনা ইনভয়েস VAT কনসালট্যান্টকে পাঠানো।

### স্প্রিন্ট ১৩–১৪ — ফুলফিলমেন্ট
- কুরিয়ার adapter (Steadfast/Pathao/Paperfly) + webhook, আংশিক শিপমেন্ট, RMA, SMS/ইমেইল নোটিফিকেশন (outbox থেকে)।
- ক্রেতা ও কোম্পানির ড্যাশবোর্ড: অর্ডার টাইমলাইন, ইনভয়েস, cost-centre অনুযায়ী খরচ।

**Exit gate:** অর্ডার থেকে ইনভয়েস পর্যন্ত E2E; প্রতিটা state transition-এর টেস্ট।

### স্প্রিন্ট ১৫–১৬ — অ্যাডমিন কনসোল
- ক্যাটালগ, প্রাইস লিস্ট (4-eyes), প্রমোশন, কোটেশন ডেস্ক (সেলস ইঞ্জিনিয়ার), অর্ডার, refund (step-up auth), অডিট ভিউয়ার, ERP export।
- স্টাফের জন্য passkey; কনসোল আলাদা সাবডোমেইনে Zero-Trust access-এর পেছনে।

**Exit gate:** মালিক ও স্টাফের UAT সাইন-অফ; প্রতিটা অ্যাডমিন endpoint-এ permission test।

### স্প্রিন্ট ১৭–১৮ — হার্ডেনিং ও ডেপ্লয়
- Production Dockerfile, IaC, managed PostgreSQL (PITR), staging environment, OpenTelemetry + SLO ড্যাশবোর্ড।
- ZAP full scan, k6 লোড (৫,০০০ ব্যবহারকারী, ২০০ অর্ডার/মিনিট, oversell ০), DR restore drill।
- CSP nonce, tamper-evident audit log, PII এনক্রিপশন, PDPO consent + data export/delete।
- Incident response runbook + tabletop exercise।
- **বাইরের পেন-টেস্ট** (১ সপ্তাহ) → ফিক্স → retest।

**Exit gate:** §১-এর ১২টা মাপকাঠির প্রতিটার প্রমাণ।

### স্প্রিন্ট ১৯–২০ — লঞ্চ
- নির্বাচিত ক্লায়েন্টদের নিয়ে সফট লঞ্চ (২ সপ্তাহ), সমস্যা ঠিক করা, তারপর পাবলিক লঞ্চ।
- On-call ও অ্যালার্ট চালু।

### লঞ্চের পর (এখন বানানো হবে না)
Seller marketplace + payout ledger, AI assistant, PunchOut (cXML/OCI), installed-base portal v2, মোবাইল অ্যাপ।

---

## ৪. প্রতিটা কাজ শেষে বাধ্যতামূলক টেস্ট (Definition of Done)

| স্তর | কখন বাধ্যতামূলক | টুল / প্যাটার্ন |
|---|---|---|
| Unit | প্রতিটা ব্যবসার নিয়ম | xUnit, `backend/tests/ShopVerse.UnitTests` |
| Property / fuzz | টাকা, দাম, ডিসকাউন্ট, পরিমাণ, পার্সিং | `Properties/PropertyRunner.cs` |
| Integration | DB ছোঁয় এমন সব কিছু; race condition | আসল PostgreSQL, `Support/DbBackedFactory.cs` |
| Concurrency | স্টক, ক্রেডিট, কুপন, approval, পেমেন্ট | সমান্তরাল অনুরোধ, invariant check |
| Mutation check | প্রতিটা নিরাপত্তা/টাকার গার্ড | গার্ড সরিয়ে টেস্ট ফেল করে দেখানো, PR-এ লেখা |
| API | প্রতিটা নতুন endpoint | স্ট্যাটাস কোড, ProblemDetails, auth matrix |
| E2E | প্রতিটা ব্যবহারকারী-যাত্রা | Playwright, `frontend/packages/e2e`, EN + BN, ৩৬০ px |
| Accessibility | প্রতিটা নতুন UI | axe (০ violation), কিবোর্ড নেভিগেশন |
| Performance | প্রতিটা নতুন পেজ/endpoint | `pnpm check:bundle`, Lighthouse, k6 (হট পাথ) |
| Visual | প্রতিটা UI পরিবর্তন | light/dark × EN/BN স্ক্রিনশট PR-এ |

**কখনো করা যাবে না:** টেস্ট skip/disable করে CI সবুজ করা; mock দিয়ে DB-race টেস্ট "পাস" করানো; টেস্ট ছাড়া বাগ-ফিক্স।

---

## ৫. প্রতিটা কাজ শেষে বাধ্যতামূলক সিকিউরিটি চেক

### ৫.১ প্রতিটা নতুন endpoint-এর চেকলিস্ট (PR টেমপ্লেটে আছে)
1. **Auth:** anonymous হলে কেন, তা `ApiSecurityRegressionTests`-এর পাবলিক তালিকায় যুক্ত ও যুক্তিসহ।
2. **Permission:** `[RequirePermission]` বা সার্ভিসে চেক; শুধু Next middleware-এ চেক যথেষ্ট নয়।
3. **IDOR:** অন্য ব্যবহারকারী/কোম্পানির id দিয়ে 404 (403 নয়, যাতে অস্তিত্ব ফাঁস না হয়)।
4. **ক্লায়েন্টকে বিশ্বাস নয়:** দাম, ট্যাক্স, মোট, স্টক, রোল কখনো request থেকে নেওয়া হবে না।
5. **Input সীমা:** প্রতিটা string-এর দৈর্ঘ্য, প্রতিটা তালিকার আকার, JSON যাচাই; ভুল ইনপুটে 400, **কখনো 500 নয়**।
6. **Abuse:** যাচাই না করা অ্যাকাউন্ট কী করতে পারে? একজন কতবার পারে? (স্টক hoarding, SMS pumping, RFQ spam)
7. **Rate limit ও idempotency:** লেখার endpoint-এ `Idempotency-Key`; সংবেদনশীল endpoint-এ কড়া limit।
8. **Concurrency:** দুইটা একই সময়ের অনুরোধে invariant ভাঙে কিনা (row lock / conditional update / unique index)।
9. **Error leak:** Production-এ stack trace, SQL, অভ্যন্তরীণ নাম নেই; অ্যাকাউন্ট আছে কিনা ফাঁস নেই।
10. **Audit:** টাকা, অনুমতি, দাম বদলালে কে/কখন/আগে/পরে লেখা হয়; secret কখনো লগে নয়।
11. **Environment:** Development-এর জিনিস (demo অ্যাকাউন্ট, mock গেটওয়ে, প্রকাশিত secret) অন্য environment-এ কাজ করে না — Production মোডে চালিয়ে যাচাই।

### ৫.২ স্বয়ংক্রিয় গেট (CI, প্রতিটা PR)
| গেট | কী ধরে | ফাইল | অবস্থা (২০২৬-১১-০৪) |
|---|---|---|---|
| CodeQL | কোডের নিরাপত্তা দুর্বলতা (C#, TS) | `security.yml` | ব্লকিং |
| gitleaks | git ইতিহাসের যেকোনো কমিটে secret | `security.yml` | ব্লকিং; লোকালি চালিয়ে পরিষ্কার (৩টা যাচাই করা মিথ্যা-সতর্কতা `.gitleaksignore`-এ কারণসহ) |
| Semgrep | OWASP Top 10 প্যাটার্ন (C#, TS) | `security.yml` | ব্লকিং; finding log-এ দেখায় (প্রথম finding ঠিক করা হয়েছে, PR #26) |
| Trivy | ঝুঁকিপূর্ণ প্যাকেজ, ভুল কনফিগ, working tree-তে secret | `security.yml` | ব্লকিং (HIGH / CRITICAL); প্রথম রানে ০ |
| NuGet + npm audit | পরিচিত CVE | `security.yml` | ব্লকিং |
| OWASP ZAP | চালু API-এর প্রতিটা OpenAPI operation-এ anonymous আক্রমণ | `dast.yml` | রাতে + হাতে চালানো যায়; High alert পেলে ফেল |
| টেস্ট + format + typecheck + bundle + E2E | মান | `ci.yml` | ব্লকিং |
| ESLint warning = ০ | অব্যবহৃত কোড, hooks-এর ভুল | `ci.yml` | ব্লকিং (`--max-warnings 0`) |
| Dependabot | সাপ্তাহিক আপডেট PR | `dependabot.yml` | চালু |

> কেন Semgrep ও Trivy এখনই ব্লকিং নয়: যে environment-এ এগুলো লেখা হয়েছে, সেখান থেকে Semgrep-এর rule registry
> আর Trivy-র ডাউনলোড পাওয়া যায়নি, তাই বর্তমান কোডে এগুলো একবারও চালানো হয়নি। না দেখে ব্লকিং করলে প্রথম দিনেই
> মিথ্যা-সতর্কতায় CI লাল হতে পারে। ZAP workflow-ও Docker না থাকায় লোকালি চালানো হয়নি; প্রথমবার হাতে চালিয়ে
> (Actions → DAST → Run workflow) রিপোর্ট দেখতে হবে।

### ৫.৩ মানুষের চেক (AI + মালিক দিয়ে)
- প্রতি slice: `/security-review` আর নিজের লেখা attack probe (Production মোডে চালানো API-এর বিরুদ্ধে)।
- প্রতি ৪ স্প্রিন্টে: পুরো সিস্টেমের স্বাধীন রিভিউ (নতুন AI সেশন, আগের প্রসঙ্গ ছাড়া) — আগের রিপোর্টের মতো।
- লঞ্চের আগে: বাইরের পেন-টেস্ট (§১ সতর্কতা)।

---

## ৬. নিয়মিত রুটিন

| কখন | কাজ |
|---|---|
| প্রতিদিন | CI লাল থাকলে আগে সেটা ঠিক, তারপর নতুন কাজ |
| প্রতি সপ্তাহ | Dependabot PR merge; স্কোরকার্ড আপডেট; PROGRESS.md আপডেট |
| প্রতি মাস | ZAP full scan রিপোর্ট পড়া; ব্যাকআপ restore drill (লঞ্চের পর প্রতি ত্রৈমাসিক) |
| প্যাচ SLA | Critical / CISA KEV: ৪৮ ঘণ্টা; High: ৭ দিন; Medium: ৩০ দিন |

---

## ৭. স্কোরকার্ড (প্রতি স্প্রিন্ট শেষে আপডেট)

| স্প্রিন্ট | ইঞ্জিনিয়ারিং | নিরাপত্তা | প্রোডাক্ট | UX | কমপ্লায়েন্স | ডকুমেন্ট | অপারেশন | সামগ্রিক |
|---|---|---|---|---|---|---|---|---|
| শুরু (২০২৬-১১-০৪) | ৮.৫ | ৬.৫ | ৩ | ৫ | ৩ | ৫ | ২ | ৫.৫ |
| ০ (সম্পূর্ণ, ২০২৬-১১-০৫) | ৮.৫ | ৭.৫ | ৩ | ৫ | ৩ | ৭ | ২.৫ | ৫.৯ |
| লক্ষ্য (লঞ্চ) | ১০ | ১০ | ১০ | ১০ | ১০ | ১০ | ১০ | ১০ |

---

## ৮. স্বাধীন টেস্ট রিপোর্টের সমস্যা (২০২৬-১১-০৪) — অবস্থা

| # | মাত্রা | সমস্যা | অবস্থা (২০২৬-১১-০৫) | Commit |
|---|---|---|---|---|
| ১ | High | Development-এ তৈরি demo admin (প্রকাশিত পাসওয়ার্ড + TOTP) DB-তে থাকলে Production API পুরো admin access দেয় | ✅ Development ছাড়া অন্য environment-এ এমন অ্যাকাউন্ট থাকলে API চালুই হয় না | `91243eb` |
| ২ | Medium-High | যাচাই না করা অ্যাকাউন্ট অর্ডার দিতে পারে; unpaid অর্ডারের সীমা নেই | ✅ যাচাই ছাড়া 403; একজনের সর্বোচ্চ ৩টা unpaid অর্ডার (race-safe) | `46313be` |
| ৩ | Medium | storefront সব গুদামের মোট স্টক দেখায়, চেকআউট শুধু একটা গুদাম থেকে রিজার্ভ করে | ✅ লাইন একাধিক গুদামে ভাগ হয়; তালিকা শুধু সক্রিয় গুদাম গোনে | `4c150c2` |
| ৪ | Medium | `shipToJson` যাচাই নেই: ভুল JSON-এ 500, ২ MB গ্রহণ, ঠিকানা ছাড়া অর্ডার | ✅ ঠিকানা বেছে নেওয়া হয় (সংরক্ষিত ঠিকানা / কোম্পানি site); ঠিকানা ছাড়া অর্ডার নয় | `f8b819a` |
| ৫ | Low | রেজিস্ট্রেশন বলে দেয় ইমেইল আগে থেকে আছে কিনা | ✅ আগে থেকে থাকা অ্যাকাউন্টেও একই উত্তর; পুনরায় টেস্টে পাওয়া নামের-যাচাই ফাঁকও বন্ধ | `9694f22`, `c298bc1` |
| ৬ | Deploy | proxy-এর পেছনে সব ব্যবহারকারী এক IP-এর rate limit শেয়ার করবে | ✅ `ForwardedHeaders:KnownNetworks` / `KnownProxies` কনফিগারযোগ্য (ডেপ্লয়ের সময় সেট করতে হবে) | `b7d58f0` |
| ৭ | Low | web ও console-এ security header নেই | ✅ CSP, X-Frame-Options, HSTS ইত্যাদি; API-তে CORP (ZAP-এর Low) | `8ff0ca9` |

প্রতিটা ফিক্সের regression test আছে, আর mutation check-এ গার্ড সরালে টেস্ট ফেল করে (commit message-এ বিস্তারিত)।
২০২৬-১১-০৫-এ চলমান API-র বিরুদ্ধে আগের আক্রমণগুলো আবার চালানো হয়েছে: সব আটকে যায়।

**ডেপ্লয়ের জন্য নতুন শর্ত:**
- Development ছাড়া অন্য environment-এ API চালু হওয়ার সময় ডাটাবেস লাগবে (demo অ্যাকাউন্ট চেক)।
- Cloudflare / load balancer-এর পেছনে `ForwardedHeaders__KnownNetworks` সেট করতে হবে।

**স্প্রিন্ট ০ সম্পূর্ণ (২০২৬-১১-০৫):** ASVS ম্যাট্রিক্সে OWASP ASVS 5.0.0-এর অফিসিয়াল ID বসানো (৮২টা, প্রকাশিত তালিকার সাথে script দিয়ে মেলানো)। তা করতে গিয়ে একটা ফাঁক পাওয়া ও ঠিক করা: web/console CSP-তে `base-uri 'self'` ছিল, V3.4.3 চায় `'none'`। পরের ধাপ স্প্রিন্ট ১–২ (ডিসকভারি ও ডিজাইন), যেখানে মালিকের অংশগ্রহণ দরকার।
