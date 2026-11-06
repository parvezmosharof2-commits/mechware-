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
  - পরের ধাপ: attribute unit, datasheet/CAD আপলোড (virus scan সহ), বিকল্প পণ্য। শুরু করতে মালিকের অনুমোদন লাগবে।

## ২. বাকি কাজ (পরের বার এখান থেকে শুরু)

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
4. **রোডম্যাপের পরের স্প্রিন্ট:** `PROGRESS.md` আর `docs/MASTER_PLAN.md` দেখুন। স্প্রিন্ট ১–২-এ ক্রেতা সাক্ষাৎকার বাকি।

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

## ৪. আবার শুরু করার আগে

- **Branch:** `claude/practical-gauss-qjujs4`, প্রতিটা merge-এর পর `main`-এর সাথে sync করা।
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
