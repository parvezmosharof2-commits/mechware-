# কাজের নোট: নিরাপত্তা অডিট ও ফিক্স (২০২৬-১১-০৫ থেকে ২০২৬-১১-০৬)

পরে আবার শুরু করার জন্য: কী শেষ হয়েছে, কী বাকি, আর কোন সিদ্ধান্তগুলো মালিকের হাতে।

**কাজের নিয়ম:**
- ছোট ছোট ধাপে কাজ।
- প্রতি ধাপে আগে টেস্ট লিখে সমস্যা প্রমাণ করা, তারপর ফিক্স।
- এরপর mutation check (প্রতিটা নিরাপত্তা-শর্ত সরালে কোনো টেস্ট ভাঙে কিনা), তারপর code review।
- Review-এ যা পাওয়া যায় তা ঠিক করা, CI-র ১২টা চেক সবুজ হলে merge।

---

## ১. শেষ হয়েছে (main-এ merge হয়েছে)

| PR | কাজ | Threat model |
|---|---|---|
| #39 | Cost centre, খরচের সীমা আর মাসিক বাজেট বাস্তবায়ন | v1 M3 |
| #40 | **Google লগইনে token যাচাই হতো না,** তাই যে কেউ যে কোনো অ্যাকাউন্টে ঢুকতে পারত (Critical)। OTP বা Google দিয়ে 2FA এড়ানো বন্ধ। | v1 C2, H5 |
| #41 | সব staff role-এ 2FA বাধ্যতামূলক। প্রথম authenticator সেট করতে অন্য admin-এর দেওয়া setup code লাগে। চুরি যাওয়া token দিয়ে চালু 2FA বদলানো বন্ধ। | v1 M1, H6 |
| #42 | প্রতিটা request-এ অ্যাকাউন্ট, session আর role database থেকে যাচাই হয় (sign-out বা suspend সঙ্গে সঙ্গে কার্যকর) | v1 M4 |
| #43 | Web আর console-এ nonce-ভিত্তিক CSP, script-এ `unsafe-inline` নেই | v1 M5 |
| #44 | লগইন করা প্রত্যেকের আলাদা rate limit; IP-প্রতি সর্বোচ্চ সীমা; 429 দেরি না করে সঙ্গে সঙ্গে আসে | v1 L3 |
| #45 | Company-তে যোগ করা মানে আমন্ত্রণ, সদস্য গ্রহণ না করা পর্যন্ত কোনো ক্ষমতা পায় না | v1 M2 |
| #46 | Credit বদলানো, refund অনুমোদন আর setup code দেওয়ার সময় নতুন TOTP কোড (step-up) | v1 H3 |
| #47 | Audit log শুধু যোগ করা যায় (মোছা বা বদলানো যায় না) আর hash chain-এ বাঁধা | v1 H3 |
| #48 | BIN, TIN আর trade licence নম্বর AES-256-GCM দিয়ে encrypt; BIN খুঁজতে blind index | v1 H4 |

এর আগে merge হয়েছিল:
- #28–#38: স্প্রিন্ট ০, ১২ স্ক্রিনের প্রোটোটাইপ, glass theme।
- Threat model v2-এর P1–P6 আর R1।
- v1-এর H1 আর H2।

**ফলাফল:** threat model v1-এর সব finding সমাধান হয়েছে (C1–C2, H1–H6, M1–M5, L3)। শেষ রানে backend-এর ৩৯৭টা unit আর ৩৬৪টা integration টেস্ট সবুজ; E2E-তে web ৩৪/৩৪ আর console ৩০/৩০।

## ১খ. দ্বিতীয় দফায় শেষ হয়েছে (২০২৬-১১-০৬, পরে)

| PR | কাজ |
|---|---|
| #49 | প্রত্যাখ্যান করা আমন্ত্রণ ৩০ দিন মনে রাখা হয় (M2) |
| #50 | Encryption key rotation (`Encryption__PreviousFieldKeys`)। এর সাথে একটা বাগ ঠিক হয়েছে: কেউ licence নম্বর `enc:...` লিখলে company পড়া যেত না (H4) |
| #51 | Console-এ "Staff authenticator setup" পাতা (M1), আর CSP violation report `/api/csp-report` (M5) |

- **H4 mutation check শেষ:** ৭টা mutant, সবগুলো টেস্টে ধরা পড়েছে।
- এই check চালাতে গিয়ে একটা দুর্বলতা পাওয়া গেছে: কোনো সারি নতুন key দিয়ে লেখা না গেলে backfill worker চিরকাল ঘুরত।
  - **ঠিক হয়েছে।** Review-এ আরেকটা সমস্যা ধরা পড়েছিল: প্রথম guard-টা একটা আটকে থাকা সারির জন্য বাকি সারিগুলোর encryption-ও থামিয়ে দিত। সেটাও ঠিক করা হয়েছে (প্রতি সারি একবার, শেষে কতগুলো আটকে আছে তার error)। টেস্ট ২টা, mutation ৪/৪।
  - টেস্ট পাস করেছে, আর guard সরিয়ে দিলে টেস্টটা ধরে (mutation check)।
  - **#55-এ merge হয়েছে।**

- **#56 merge হয়েছে (স্প্রিন্ট ৩–৪, PIM ধাপ ১):** supply status, উত্তরসূরি পণ্য আর MOQ/pack size।
  - টেস্ট, mutation check আর code review হয়েছে।
  - ধাপ ২ (মালিকের বেছে নেওয়া): unit-সহ সংখ্যাভিত্তিক attribute।
    - `UnitCatalog` আর `numeric_value`/`numeric_max` যোগ হয়েছে।
    - টেস্ট: unit ৪৬টা, seed integration ১টা। Mutation: ১৮টা mutant, সবগুলো ধরা পড়েছে।
    - Review-এ ৩টা সমস্যা পাওয়া গিয়েছিল: mm² দৈর্ঘ্য হিসেবে পড়া হচ্ছিল, লম্বা লেখায় 500 error হচ্ছিল, আর মানের দৈর্ঘ্যের কোনো সীমা ছিল না। তিনটাই ঠিক করা হয়েছে।
  - পরের ধাপ: datasheet/CAD আপলোড (virus scan সহ), বিকল্প পণ্য।

## ১গ. তৃতীয় দফা (২০২৬-১১-০৬, রাত): তিনটা session একীভূত

- **কী হয়েছিল:** তিনটা session একই সময়ে একই repo-তে কাজ করছিল, ফলে H4 mutation আর PIM ধাপ ১ দুবার তৈরি হয়েছিল।
  - এখন থেকে **একটাই session** চলবে।
  - পুরনো কাজ আছে `backup/galileo-before-rebase` branch-এ, আর অন্য session-এর `claude/hopeful-mendel-k1dmso`-তে।
- **`main`-এর উপর নতুন করে যোগ হয়েছে** (branch `claude/inspiring-galileo-lqeixj`):
  - **H4-এর বাকি ফাঁক বন্ধ:** কাটা ciphertext, key derivation-এর known-answer test, fingerprint না থাকলে যেকোনো save-এ ফিরে আসা, audit log-এ fingerprint না যাওয়া, আর backfill-এর তিনটা কেস (শুধু fingerprint নেই, শুধু TIN পুরনো, শুধু licence পুরনো) আর একাধিক batch। প্রতিটা টেস্ট তার mutant-এর অধীনে ব্যর্থ হয়।
  - **অর্ডারে MOQ/pack যাচাই:** `POST /orders` সরাসরি ডাকলে ভুল পরিমাণ যেত। এখন 400 আসে, পরিমাণ রাউন্ড করা হয় না। মজুদ থাকলে obsolete পার্ট এখনো অর্ডার করা যায়।
  - **উত্তরসূরির `isDropIn` আর `note`, আর বিকল্প পার্ট** (সর্বোচ্চ ১০টা, শুধু বিক্রিতে থাকা)। Mutation: প্রথমে ৯টা mutant চালানো হয়েছিল, সব ধরা পড়ে।
  - Code review-এ ৭টা সমস্যা পাওয়া গিয়েছিল, সবগুলো ঠিক করা হয়েছে:
    - বিকল্পের position-এর দ্বন্দ্ব;
    - শুধু status বদলালে উত্তরসূরির নোট মুছে যাওয়া;
    - বিকল্প বা উত্তরসূরি obsolete হলে যে পাতায় সেটা আছে তার ক্যাশ না মোছা। এখন outbox relay সেই পাতাগুলোও মোছে;
    - obsolete পার্ট বিকল্প হিসেবে যোগ করা যেত;
    - "বিক্রিতে আছে" শর্ত দুই জায়গায় ছিল, এখন `Product.IsLinkable`-এ একটাই;
    - নথির ভুল সংখ্যা;
    - টেস্টের ফাঁক।
  - এই সংশোধনের জন্য আরও ১৪টা mutant চালানো হয়েছে। ১১টা ধরা পড়েছে, ১টা compile-এ ভেঙেছে। ২টা বেঁচে গিয়েছিল, টেস্ট দুই ধাপে ভেঙে ধরানো হয়েছে।
  - ১টা বেঁচে আছে, এটা সমতুল্য (equivalent): `SoftDelete` নিজেই status `Archived` করে, তাই `IsLinkable`-এর `DeletedAtUtc` শর্ত বাদ দিলেও ফল বদলায় না। শর্তটা রাখা হয়েছে সতর্কতা হিসেবে।
  - **স্প্রিন্ট ১–২ বন্ধ:** ৫টা কাল্পনিক (AI) সাক্ষাৎকার আছে `docs/discovery/`-এ। মালিক রিভিউ মওকুফ করেছেন।
- **শিক্ষা:** `StorefrontCatalogReader`-এর product query একটা static compiled query। তাই টেস্টে reader-কে fixture-এর context দিয়ে বানানো যাবে না, HTTP দিয়ে টেস্ট করতে হবে। নইলে পরে API-তে 500 আসে।

## ১ঘ. চতুর্থ দফা (২০২৬-১১-০৬/০৭): PIM ধাপ ৩–৫, সবকিছু `main`-এ

`main`-এ merge হয়েছে (শেষ commit `ecb28c4`): [#58](https://github.com/parvezmosharof2-commits/mechware-/pull/58), [#59](https://github.com/parvezmosharof2-commits/mechware-/pull/59), [#60](https://github.com/parvezmosharof2-commits/mechware-/pull/60)। শেষ রান: unit ৫৯৮, integration ৪৪২ টেস্ট সবুজ, CI-র ১২টা চেক সবুজ।

| PR | কাজ |
|---|---|
| #58 | অর্ডারে MOQ ও pack size যাচাই (`POST /orders` সরাসরি ডাকলেও); উত্তরসূরির `isDropIn` ও `note`; বিকল্প পার্ট (সর্বোচ্চ ১০); উত্তরসূরি বা বিকল্প obsolete হলে যে পাতাগুলো সেটা দেখায় তাদের ক্যাশ মোছা; H4-এর mutation check-এর ফাঁকের টেস্ট; ৫টা কাল্পনিক সাক্ষাৎকার (`docs/discovery/`, অনুমান, প্রমাণ নয়) |
| #59 | staff-এর জন্য পণ্যের তথ্য সম্পাদনার API: `/api/v1/admin/products/{id}/{pim,supply,alternatives,variants/{id}/order-units}`, নতুন অনুমতি `catalog.manage` (শুধু Admin)। উত্তরসূরি-সহ পণ্যের status বদলাতে উত্তরসূরি আবার পাঠাতে হয় (চুপচাপ হারানো ঠেকাতে), সরাতে `clearSuccessor: true`। একসাথে লিখলে 409, 500 নয় |
| #60 | datasheet, CAD, certificate আপলোড: `/api/v1/admin/products/{id}/documents` + public download `/api/v1/storefront/products/{slug}/documents/{id}`। ধরন প্রথম byte দেখে, আকার পড়ার সময় গোনা, ClamAV স্ক্যান (না পেলে 503, কিছু সংরক্ষণ হয় না), ২০টার সীমা ও একই ফাইল দুবার না (পণ্যের row-lock-এর নিচে)। Production-এ `Documents__LocalPath` আর `Antivirus__Host` ছাড়া API চালুই হয় না |

- **Sprint ১–২ বন্ধ:** মালিকের সাক্ষাৎকার নেওয়ার লোক নেই, ৫টা AI-র কাল্পনিক সাক্ষাৎকার দিয়ে চলছে। সাইন-অফ আর threat model v2-এর মানুষের রিভিউ মওকুফ।
- **Mutation check এখন repo-তে:** `scripts/mutation/run.py` (ব্যবহার ফাইলের মাথায় লেখা)। **আলাদা git worktree-তে চালান**, নইলে stop-hook আর commit ভাঙা কোড ধরে ফেলে। প্রতিটা mutant-এ ~২ মিনিট।
- **শিক্ষা, যা বারবার কাজে লেগেছে:**
  - `StorefrontCatalogReader`-এর product query static compiled query: টেস্টে reader-কে fixture-এর context দিয়ে সরাসরি বানাবেন না, HTTP দিয়ে টেস্ট করুন।
  - `Enum.TryParse` কমা-দেওয়া মান জুড়ে দেয় (`"datasheet,cad"` = `certificate`): ইনপুটে সরাসরি switch ব্যবহার করুন।
  - নতুন বেনামী endpoint `ApiSecurityRegressionTests`-এর পর্যালোচিত তালিকায় কারণসহ লিখতে হয়। এটা ইচ্ছাকৃত দরজা।
  - CI-র মেশিনে `/tmp`-এর কিছু ডিরেক্টরি পড়া যায় না: টেস্টে `/tmp` ঘাঁটবেন না, নির্দিষ্ট পথ দেখুন।
  - Branch-এ force-push করা যায় না (অনুমতি নেই): squash-merge-এর পর `git commit-tree HEAD^{tree} -p HEAD -p origin/<branch>` দিয়ে tree-একই একটা merge commit বানিয়ে সাধারণ push করা হয়েছে। নতুন কাজ সবসময় `origin/main` থেকে।

## ২. বাকি কাজ (পরের বার এখান থেকে শুরু)

**সবচেয়ে আগে (চতুর্থ দফার পরে):**
- **স্প্রিন্ট ৫–৬ — সার্চ + পণ্যের তালিকা/পাতা:** Meilisearch adapter (outbox থেকে sync; `SearchSyncQueuePublisher` ইতিমধ্যে queue-তে ঘটনা ফেলে), পার্ট-নম্বর normalisation (`6ES7 214-1AG40-0XB0` = `6ES72141AG400XB0`; সাক্ষাৎকারে সবাই space, dash আর 0/O গোলমালের কথা বলেছেন), Banglish প্রতিশব্দ, PLP (টেবিল), PDP (স্পেক টেবিল, quantity-break, গুদামভিত্তিক স্টক)। Exit gate: search p95 < ১৫০ ms (k6), Lighthouse ≥ ৯০।
- **PIM-এর বাকি:** আসল পণ্যের ছবি আর যাচাই করা স্পেক (মালিক জোগাড় করবেন), staff-এর জন্য frontend পাতা (এখন শুধু API), ফাইল সংরক্ষণ MinIO/S3-তে (এখন স্থানীয় ডিরেক্টরি; `IObjectStorage` interface-এ adapter বসবে), পুরনো ফাইল নতুন signature দিয়ে আবার স্ক্যান।

**আগের তালিকা (বাকিগুলো এখনো প্রযোজ্য):**


1. ~~**H4-এর mutation check শেষ করা।**~~ **শেষ হয়েছে।** (নিচের তিনটা লাইন এখন আর প্রযোজ্য নয়)
   - দুবার শুরু করে থামাতে হয়েছে, কারণ প্রতিটা mutant-এ build সহ দেড় মিনিটের মতো লাগে।
   - একটা আলাদা git worktree-তে background-এ পুরোটা চালাতে হবে।
   - কোনো mutant বেঁচে গেলে সেটার জন্য টেস্ট যোগ করতে হবে।
2. **ছোট বাকি কাজ (নথিভুক্ত, ঝুঁকি কম):**
   - ~~**M2:** প্রত্যাখ্যান করা আমন্ত্রণ মনে রাখা হয় না।~~ **ঠিক হয়েছে:** ৩০ দিন একই company আবার আমন্ত্রণ পাঠাতে পারে না।
   - **H3:** step-up কোড action ব্যর্থ হলেও খরচ হয়ে যায়। **ইচ্ছাকৃতভাবে এভাবেই রাখা হয়েছে (accepted):** কোড ফেরত দিলে কেউ সেটা আবার ব্যবহার (replay) করতে পারত।
   - ~~**H4:** key rotation নেই।~~ **হয়েছে:** `Encryption__PreviousFieldKeys` দিয়ে rotation। এর সাথে একটা বাগও ঠিক হয়েছে: কেউ licence নম্বর `enc:...` লিখলে company পড়া যেত না।
   - **M1:** ~~setup code দেওয়ার console পাতা নেই~~ **হয়েছে** ("Staff authenticator setup")। Passkey (WebAuthn) এখনো হয়নি।
   - **H6:** চালু authenticator বদলানোর আলাদা নিরাপদ ব্যবস্থা নেই।
   - **M5:** CSP violation report **হয়েছে** (`/api/csp-report`)। Style-এ এখনো `unsafe-inline` আছে। CSS দিয়ে code চালানো যায় না, তাই এটা মেনে নেওয়া ঝুঁকি।
3. **Threat model v2-এর বাকি অংশ:** Q1–Q9 (quotes) আর I1–I10 (invoicing)। এগুলো ওই মডিউলগুলো তৈরি হওয়ার সাথে সাথে করতে হবে (রোডম্যাপের স্প্রিন্ট ৭–৯ আর ১০–১২)।
4. **রোডম্যাপ:** `PROGRESS.md` আর `docs/MASTER_PLAN.md`। স্প্রিন্ট ১–২ বন্ধ, স্প্রিন্ট ৩–৪ প্রায় শেষ (উপরে বাকি অংশ)।

## ৩. মালিকের সিদ্ধান্ত বা কাজ দরকার

- **[deploy-এর আগে জরুরি] `Encryption__FieldKey`:**
  - একটা র‍্যান্ডম ৩২-byte key, base64-এ, production secret store-এ রাখতে হবে।
  - এটা ছাড়া, বা প্রকাশিত development key দিয়ে, API চালু হবে না।
  - Key হারালে encrypt করা BIN, TIN আর licence নম্বর আর পড়া যাবে না, তাই একটা নিরাপদ backup রাখুন।
- **Google লগইন চালু করতে:**
  - `Authentication__Google__ClientId` সেট করতে হবে।
  - সার্ভার থেকে `www.googleapis.com`-এ যাওয়ার অনুমতি লাগবে।
- **লগইনের সীমা:**
  - Production-এ প্রতি IP থেকে মিনিটে ৫ বার, এটা brief §11.1-এর নিয়ম।
  - সমস্যা: NAT-এর পেছনে একটা অফিসের অনেকে একসাথে লগইন করলে ৬ষ্ঠ জন থেকে আটকে যাবে।
  - সীমা বাড়ালে credential stuffing একটু সহজ হয়। বাড়াব কিনা জানাবেন।
- **Staff যোগ করার প্রক্রিয়া:**
  - Staff role এখন শুধু database-এ সরাসরি দেওয়া যায়।
  - নতুন staff-কে authenticator সেট করাতে admin কল করবেন `POST /api/v1/admin/users/{id}/two-factor-setup-code`, step-up কোড দিয়ে।
  - তারপর কোডটা ব্যক্তিগতভাবে হাতে দিতে হবে।

- **নতুন (ফাইল আপলোড):**
  - **`Documents__LocalPath`:** production-এ একটা persistent volume-এর ডিরেক্টরি লাগবে। MinIO/S3 বা volume, কোনটা চান জানাবেন।
  - **ClamAV:** production-এ `Antivirus__Host` (আর `Antivirus__Port`, ডিফল্ট ৩৩১০) ছাড়া API চালু হবে না। `infra/docker/docker-compose.yml`-এ ClamAV আছে (শুধু `127.0.0.1`-এ খোলা)। clamd-এর `StreamMaxLength` সবচেয়ে বড় আপলোডের (৫০ MB) উপরে রাখতে হবে।
  - **সাক্ষাৎকারের আসল যাচাই:** `docs/discovery/SYNTHESIS.md`-এর "যাচাই করতে হবে" অংশ। যেমন VDS-এর হার (VAT পরামর্শক), WhatsApp-এ approval, নবাবপুরের বদলে integrator কেন আসবেন।

## ৪. আবার শুরু করার আগে

- **Branch:** `claude/inspiring-galileo-lqeixj`, প্রতিটা merge-এর পর `main`-এর সাথে sync করা। **একসাথে একটাই session চালান।**
- **নতুন container-এ:** `apt-get install -y dotnet-sdk-10.0`, `dotnet tool install -g dotnet-ef`, তারপর `pg_ctlcluster 16 main start` আর `redis-server --daemonize yes` (container বন্ধ হলে এ দুটো আবার চালাতে হয়; না চালালে integration টেস্ট ব্যর্থ নয়, **skip** হয়: ফলে সবুজ দেখায়, সাবধান!)। Postgres-এ `CREATE ROLE shopverse LOGIN SUPERUSER PASSWORD 'shopverse_secret'` একবার। Migration বানাতে `dotnet ef migrations add <নাম> -p ShopVerse.Infrastructure -s ShopVerse.Infrastructure --no-build` (`export PATH=$PATH:/root/.dotnet/tools`)।
- **লোকাল টেস্টের জন্য:**
  - PostgreSQL 16-এ একটা user লাগবে (`shopverse` / `shopverse_secret`), আর Redis চালু থাকতে হবে।
  - চালানোর কমান্ড: `MECHWARE_TEST_PG=... dotnet test backend/ShopVerse.slnx`।
- **Push-এর আগে চালাতে হবে:**
  - `dotnet format backend/ShopVerse.sln --verify-no-changes`, কারণ CI এটা যাচাই করে।
  - `pnpm --dir frontend typecheck` আর `pnpm --dir frontend lint`।
- **লোকালে E2E চালাতে:**
  - নতুন database, তারপর `dotnet ef database update`।
  - API, web আর console চালু করে `frontend/packages/e2e`-এ `pnpm test`।
- **বিস্তারিত কোথায়:**
  - `docs/security/THREAT_MODEL_COMPANIES_APPROVALS.md`: প্রতিটা finding-এর FIXED অংশে কী করা হয়েছে আর তার প্রমাণ।
  - `docs/API_CONTRACT.md`: নতুন endpoint আর error code।
  - `docs/security/ASVS_MATRIX.md`
